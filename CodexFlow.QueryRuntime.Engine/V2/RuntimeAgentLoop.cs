using System.Text.Json;
using CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.QueryRuntime.Engine.V2;

/// <summary>
/// Provider-neutral, single-process Agent Loop used by the C6 runtime slice.
/// Tool routing/policy implementations and hosting projections remain external ports.
/// </summary>
public sealed class RuntimeAgentLoop
{
    private readonly IRuntimeModelClient _modelClient;
    private readonly TimeProvider _timeProvider;

    public RuntimeAgentLoop(IRuntimeModelClient modelClient, TimeProvider? timeProvider = null)
    {
        _modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<RuntimeAgentLoopResult> RunAsync(
        RuntimeAgentLoopRequest request,
        RuntimeTurnHandle? handle = null,
        CancellationToken ct = default)
        => RunCoreAsync(request, checkpoint: null, handle, ct);

    public Task<RuntimeAgentLoopResult> ResumeAsync(
        RuntimeAgentLoopRequest request,
        RuntimeCheckpointDocument checkpoint,
        RuntimeTurnHandle? handle = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return RunCoreAsync(request, checkpoint, handle, ct);
    }

    private async Task<RuntimeAgentLoopResult> RunCoreAsync(
        RuntimeAgentLoopRequest request,
        RuntimeCheckpointDocument? checkpoint,
        RuntimeTurnHandle? handle,
        CancellationToken ct)
    {
        ValidateRequest(request);
        request = SnapshotRequest(request);
        var attempt = request.Attempt ?? (checkpoint == null
            ? RuntimeRunAttempt.Create()
            : RuntimeRunAttempt.Resume(checkpoint));
        request = request with { Attempt = attempt };
        if (checkpoint != null)
        {
            ValidateResumeRequest(request, checkpoint, attempt);
        }
        using var linkedCancellation = handle == null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, handle.CancellationToken);
        var token = linkedCancellation.Token;
        var contextManager = request.ContextManager ?? new RuntimeContextManager();
        var audit = new RuntimeAuditEmitter(
            request.AuditSink,
            request.AuditFailureMode,
            request.SessionId,
            request.TurnId,
            _timeProvider);
        var checkpointEmitter = new RuntimeCheckpointEmitter(
            request.CheckpointSink,
            request.CheckpointFailureMode,
            request,
            attempt,
            _timeProvider);
        var history = checkpoint == null
            ? RuntimeHistory.Create(
                request.InitialMessages,
                request.HistoryVersion,
                contextManager.Options)
            : RuntimeHistory.RestoreCanonical(
                checkpoint.CanonicalHistory,
                checkpoint.Session.HistoryVersion,
                checkpoint.NextHistoryMessageSequence,
                contextManager.Options,
                checkpoint.HistoryBlobs);
        var preparedContexts = new List<PreparedRuntimeContext>();
        var contextEvents = new List<RuntimeContextEvent>();
        await PublishHistoryEventsAsync(history, request.ContextEventSink, contextEvents, token).ConfigureAwait(false);
        var session = checkpoint?.Session ?? RuntimeSessionState.Create(request.SessionId, request.HistoryVersion);
        RuntimeCheckpointKind? resumeKind = checkpoint?.Kind;
        if (checkpoint == null)
        {
            session = RuntimeStateReducer.StartTurn(
                session,
                new RuntimeTurnContext(
                    request.SessionId,
                    request.TurnId,
                    request.Objective,
                    request.CreatedAt ?? _timeProvider.GetUtcNow(),
                    request.ModelParameters.RequiredToolName));
        }
        else
        {
            (session, resumeKind) = PrepareResumeState(session, history, checkpoint);
            await PublishHistoryEventsAsync(
                history,
                request.ContextEventSink,
                contextEvents,
                token).ConfigureAwait(false);
        }
        var finalText = checkpoint?.FinalText ?? string.Empty;

        try
        {
            await audit.EmitAsync(
                RuntimeAuditEventKind.TurnStarted,
                RuntimeAuditSensitivity.Sensitive,
                new RuntimeTurnStartedAuditPayload(
                    request.Objective,
                    session.HistoryVersion,
                    history.ToMessages(),
                    request.Policy,
                    request.Environment,
                    request.Budget),
                token).ConfigureAwait(false);
            if (checkpoint == null)
            {
                await checkpointEmitter.SaveAsync(
                    RuntimeCheckpointKind.TurnStarted,
                    session,
                    history,
                    finalText,
                    token).ConfigureAwait(false);
            }

            if (resumeKind == RuntimeCheckpointKind.StepCommitted)
            {
                var postStep = await ApplyPostStepDecisionAsync(
                    session,
                    history,
                    request,
                    handle,
                    contextEvents,
                    token).ConfigureAwait(false);
                session = postStep.Session;
                if (postStep.Complete)
                {
                    return await CompleteResultAsync(
                        session,
                        history,
                        finalText,
                        preparedContexts,
                        contextEvents,
                        audit,
                        checkpointEmitter).ConfigureAwait(false);
                }
                await checkpointEmitter.SaveAsync(
                    RuntimeCheckpointKind.ContinuationCommitted,
                    session,
                    history,
                    finalText,
                    token).ConfigureAwait(false);
            }

            for (var stepIndex = session.ActiveTurn!.Steps.Count;
                 stepIndex < request.Budget.MaxSteps;
                 stepIndex++)
            {
                token.ThrowIfCancellationRequested();
                if (handle != null)
                {
                    var steering = handle.DrainSteering();
                    if (steering.Count > 0)
                    {
                        session = CommitHistoryBatch(session, history, steering);
                        await PublishHistoryEventsAsync(history, request.ContextEventSink, contextEvents, token).ConfigureAwait(false);
                    }
                }

                var turn = session.ActiveTurn!;
                var stepId = new RuntimeStepId($"{request.TurnId.Value}:step:{stepIndex}");
                var requiredToolName = turn.Progress.RequiredToolSatisfied
                    ? null
                    : turn.Progress.RequiredToolName;
                var historySnapshot = history.Snapshot();
                PreparedRuntimeContext preparedContext;
                IReadOnlyList<RuntimeToolDescriptor> stepTools;
                try
                {
                    if (request.ToolCatalogSelector == null)
                    {
                        stepTools = request.Tools;
                    }
                    else
                    {
                        var selectionContext = contextManager.Prepare(
                            historySnapshot,
                            request.Objective,
                            requiredToolName);
                        stepTools = ValidateSelectedTools(
                            request.ToolCatalogSelector.SelectTools(
                                selectionContext,
                                request.Tools,
                                stepIndex),
                            request.Tools);
                    }
                    if (!string.IsNullOrWhiteSpace(requiredToolName) && !stepTools.Any(tool => string.Equals(
                            tool.CanonicalName,
                            requiredToolName,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new RuntimeAgentLoopFailure(
                            RuntimeTerminationReason.FailClosed,
                            new RuntimeError(
                                RuntimeErrorCategory.RuntimeInvariantViolation,
                                "required_tool_omitted_from_context",
                                $"The C5 tool catalog omitted required tool '{requiredToolName}'."));
                    }
                    var reservedToolTokens = checked(stepTools.Sum(RuntimeTokenEstimator.Estimate));
                    preparedContext = contextManager.Prepare(
                        historySnapshot,
                        request.Objective,
                        requiredToolName,
                        reservedToolTokens);
                }
                catch (RuntimeAgentLoopFailure)
                {
                    throw;
                }
                catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "reservedToolTokens")
                {
                    throw new RuntimeAgentLoopFailure(
                        RuntimeTerminationReason.FailClosed,
                        new RuntimeError(
                            RuntimeErrorCategory.ResourceExhausted,
                            "tool_catalog_context_budget_exhausted",
                            ex.Message));
                }
                catch (Exception ex)
                {
                    throw new RuntimeAgentLoopFailure(
                        RuntimeTerminationReason.FailClosed,
                        new RuntimeError(
                            RuntimeErrorCategory.RuntimeInvariantViolation,
                            "context_preparation_failed",
                            ex.Message));
                }
                preparedContexts.Add(preparedContext);
                foreach (var runtimeEvent in preparedContext.Events)
                {
                    contextEvents.Add(runtimeEvent);
                    if (request.ContextEventSink != null)
                    {
                        await request.ContextEventSink.OnEventAsync(runtimeEvent, token).ConfigureAwait(false);
                    }
                }
                await audit.EmitAsync(
                    RuntimeAuditEventKind.ContextPrepared,
                    RuntimeAuditSensitivity.Internal,
                    new RuntimeContextPreparedAuditPayload(
                        preparedContext.HistoryVersion,
                        preparedContext.EstimatedTokens,
                        preparedContext.ReservedToolTokens,
                        preparedContext.Compacted,
                        preparedContext.IncludedItemIds,
                        preparedContext.OmittedItemIds,
                        preparedContext.ReplacedItemIds,
                        preparedContext.Events),
                    token,
                    stepId).ConfigureAwait(false);
                var modelRequest = new RuntimeModelRequest(
                    request.SessionId,
                    request.TurnId,
                    stepId,
                    preparedContext.Messages,
                    stepTools.ToArray(),
                    request.ModelParameters with { RequiredToolName = requiredToolName },
                    session.HistoryVersion);
                var stepContext = RuntimeStepContext.Create(
                    stepId,
                    stepIndex,
                    modelRequest,
                    request.Policy,
                    request.Environment,
                    request.Budget,
                    session.HistoryVersion,
                    _timeProvider.GetUtcNow(),
                    preparedContext);
                session = RuntimeStateReducer.PrepareStep(session, stepContext);
                session = RuntimeStateReducer.TransitionStep(
                    session,
                    stepId,
                    RuntimeStepPhase.Sampling);

                await audit.EmitAsync(
                    RuntimeAuditEventKind.ModelRequestPrepared,
                    RuntimeAuditSensitivity.Sensitive,
                    new RuntimeModelRequestAuditPayload(modelRequest),
                    token,
                    stepId).ConfigureAwait(false);

                await checkpointEmitter.SaveAsync(
                    RuntimeCheckpointKind.StepPrepared,
                    session,
                    history,
                    finalText,
                    token).ConfigureAwait(false);

                (session, var output) = await SampleAsync(session, stepContext, token).ConfigureAwait(false);
                session = RuntimeStateReducer.CommitModelOutput(session, stepId, output);
                finalText = output.Text;
                await audit.EmitAsync(
                    RuntimeAuditEventKind.ModelResponseCommitted,
                    RuntimeAuditSensitivity.Sensitive,
                    new RuntimeModelResponseAuditPayload(stepId, output),
                    token,
                    stepId).ConfigureAwait(false);

                if (ExceedsTokenBudget(session.ActiveTurn!.Progress.Usage, request.Budget))
                {
                    var error = new RuntimeError(
                        RuntimeErrorCategory.ResourceExhausted,
                        "token_budget_exhausted",
                        "The Turn exceeded its model token budget.");
                    session = ObserveOutstandingTools(session, error, cancelled: false);
                    throw new RuntimeAgentLoopFailure(RuntimeTerminationReason.Error, error);
                }

                if (output.StopReason == RuntimeModelStopReason.Cancelled)
                {
                    throw new RuntimeAgentLoopCancelled(session);
                }
                if (output.ToolCalls.Count == 0 && string.IsNullOrEmpty(output.Text))
                {
                    throw new RuntimeAgentLoopFailure(
                        RuntimeTerminationReason.FailClosed,
                        new RuntimeError(
                            RuntimeErrorCategory.ProviderProtocol,
                            "empty_model_response",
                            "The model completed without text or tool calls."));
                }
                if (output.ToolCalls.Count > 0)
                {
                    EnsureToolStopReason(output.StopReason);
                }

                await checkpointEmitter.SaveAsync(
                    RuntimeCheckpointKind.ModelCommitted,
                    session,
                    history,
                    finalText,
                    token).ConfigureAwait(false);

                if (output.ToolCalls.Count > 0)
                {
                    var execution = await ExecuteToolsAsync(
                        session,
                        request,
                        stepContext,
                        handle,
                        token).ConfigureAwait(false);
                    session = execution.Session;
                    session = RuntimeStateReducer.TransitionStep(
                        session,
                        stepId,
                        RuntimeStepPhase.CommittingObservation);
                    session = RuntimeStateReducer.TransitionStep(
                        session,
                        stepId,
                        RuntimeStepPhase.Completed);
                    session = CommitHistoryBatch(
                        session,
                        history,
                        [
                            CreateAssistantMessage(output),
                            new RuntimeMessage(
                                RuntimeMessageRole.Tool,
                                execution.Results.Select(static result => (RuntimeItem)new RuntimeToolResultItem(result)).ToArray())
                        ]);
                    await PublishHistoryEventsAsync(history, request.ContextEventSink, contextEvents, token).ConfigureAwait(false);
                    for (var resultIndex = 0; resultIndex < execution.Results.Count; resultIndex++)
                    {
                        await audit.EmitAsync(
                            RuntimeAuditEventKind.ToolObservationCommitted,
                            RuntimeAuditSensitivity.Sensitive,
                            new RuntimeToolObservationAuditPayload(
                                stepId,
                                output.ToolCalls[resultIndex],
                                execution.Results[resultIndex]),
                            token,
                            stepId,
                            output.ToolCalls[resultIndex].InvocationId).ConfigureAwait(false);
                    }
                    var policyFailure = execution.Results
                        .Select(static result => result.Error)
                        .FirstOrDefault(IsTerminalPolicyFailure);
                    if (policyFailure != null)
                    {
                        throw new RuntimeAgentLoopFailure(RuntimeTerminationReason.FailClosed, policyFailure);
                    }
                    if (execution.FatalError != null)
                    {
                        throw new RuntimeAgentLoopFailure(RuntimeTerminationReason.Error, execution.FatalError);
                    }
                    if (stepIndex + 1 >= request.Budget.MaxSteps)
                    {
                        throw new RuntimeAgentLoopFailure(
                            RuntimeTerminationReason.MaxSteps,
                            new RuntimeError(
                                RuntimeErrorCategory.ResourceExhausted,
                                "step_budget_exhausted",
                                "The Turn ended after tool observations because no Step budget remained."));
                    }
                    await checkpointEmitter.SaveAsync(
                        RuntimeCheckpointKind.ToolBatchCommitted,
                        session,
                        history,
                        finalText,
                        token).ConfigureAwait(false);
                    if (request.ToolCatalogSelector != null)
                    {
                        for (var resultIndex = 0; resultIndex < execution.Results.Count; resultIndex++)
                        {
                            request.ToolCatalogSelector.Observe(
                                output.ToolCalls[resultIndex],
                                execution.Results[resultIndex]);
                        }
                    }
                    continue;
                }

                session = RuntimeStateReducer.TransitionStep(
                    session,
                    stepId,
                    RuntimeStepPhase.Completed);
                session = CommitHistoryBatch(session, history, [CreateAssistantMessage(output)]);
                await PublishHistoryEventsAsync(history, request.ContextEventSink, contextEvents, token).ConfigureAwait(false);
                await checkpointEmitter.SaveAsync(
                    RuntimeCheckpointKind.StepCommitted,
                    session,
                    history,
                    finalText,
                    token).ConfigureAwait(false);
                var postStep = await ApplyPostStepDecisionAsync(
                    session,
                    history,
                    request,
                    handle,
                    contextEvents,
                    token).ConfigureAwait(false);
                session = postStep.Session;
                if (postStep.Complete)
                {
                    return await CompleteResultAsync(
                        session,
                        history,
                        finalText,
                        preparedContexts,
                        contextEvents,
                        audit,
                        checkpointEmitter).ConfigureAwait(false);
                }
                await checkpointEmitter.SaveAsync(
                    RuntimeCheckpointKind.ContinuationCommitted,
                    session,
                    history,
                    finalText,
                    token).ConfigureAwait(false);
            }

            throw new RuntimeAgentLoopFailure(
                RuntimeTerminationReason.MaxSteps,
                new RuntimeError(
                    RuntimeErrorCategory.ResourceExhausted,
                    "step_budget_exhausted",
                    "The Turn exhausted its Step budget."));
        }
        catch (RuntimeAgentLoopCancelled cancelled)
        {
            session = cancelled.Session ?? session;
            session = CancelTurn(session);
            return await CompleteResultAsync(session, history, finalText, preparedContexts, contextEvents, audit, checkpointEmitter)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            session = CancelTurn(session);
            return await CompleteResultAsync(session, history, finalText, preparedContexts, contextEvents, audit, checkpointEmitter)
                .ConfigureAwait(false);
        }
        catch (RuntimeCheckpointWriteFailure failure)
        {
            session = FailTurn(
                session,
                RuntimeTerminationReason.FailClosed,
                failure.Error);
            return await CompleteResultAsync(session, history, finalText, preparedContexts, contextEvents, audit, checkpointEmitter)
                .ConfigureAwait(false);
        }
        catch (RuntimeAuditWriteFailure failure)
        {
            session = FailTurn(
                session,
                RuntimeTerminationReason.FailClosed,
                failure.Error);
            return await CompleteResultAsync(session, history, finalText, preparedContexts, contextEvents, audit, checkpointEmitter)
                .ConfigureAwait(false);
        }
        catch (RuntimeAgentLoopFailure failure)
        {
            session = failure.Session ?? session;
            session = FailTurn(session, failure.TerminationReason, failure.Error);
            return await CompleteResultAsync(session, history, finalText, preparedContexts, contextEvents, audit, checkpointEmitter)
                .ConfigureAwait(false);
        }
    }

    private async Task<(RuntimeSessionState Session, RuntimeModelOutput Output)> SampleAsync(
        RuntimeSessionState session,
        RuntimeStepContext step,
        CancellationToken ct)
    {
        while (true)
        {
            session = RuntimeStateReducer.RecordModelAttempt(session, step.StepId);
            var validator = new RuntimeModelStreamValidator();
            var items = new List<RuntimeItem>();
            var warnings = new List<RuntimeWarning>();
            var usage = RuntimeUsageTotals.Empty;
            try
            {
                await foreach (var runtimeEvent in _modelClient
                                   .StreamAsync(step.ModelRequest, ct)
                                   .ConfigureAwait(false))
                {
                    validator.Apply(runtimeEvent);
                    switch (runtimeEvent)
                    {
                        case RuntimeTextDeltaEvent text:
                            items.Add(new RuntimeTextItem(text.Text));
                            break;
                        case RuntimeReasoningDeltaEvent reasoning:
                            items.Add(new RuntimeReasoningItem(reasoning.Text, reasoning.ProtectedData));
                            break;
                        case RuntimeToolCallEvent toolCall:
                            items.Add(new RuntimeToolCallItem(toolCall.Call with
                            {
                                Arguments = toolCall.Call.Arguments.Clone()
                            }));
                            break;
                        case RuntimeUsageEvent usageEvent:
                            usage = AddUsage(usage, usageEvent.Usage);
                            break;
                        case RuntimeWarningEvent warning:
                            warnings.Add(warning.Warning);
                            break;
                    }
                }
                validator.Complete();
                return (session, new RuntimeModelOutput(
                    items,
                    usage,
                    warnings,
                    validator.StopReason ?? RuntimeModelStopReason.Unknown));
            }
            catch (RuntimeModelClientException ex) when (
                ex.Error.Retryable &&
                validator.EventCount == 0 &&
                session.ActiveTurn!.Steps[^1].ModelAttempts <= step.Budget.MaxModelRetries)
            {
                continue;
            }
            catch (RuntimeModelStreamValidationException ex)
            {
                throw new RuntimeAgentLoopFailure(RuntimeTerminationReason.FailClosed, ex.Error, session);
            }
            catch (RuntimeModelClientException ex)
            {
                throw new RuntimeAgentLoopFailure(RuntimeTerminationReason.Error, ex.Error, session);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new RuntimeAgentLoopCancelled(session);
            }
            catch (Exception ex)
            {
                throw new RuntimeAgentLoopFailure(
                    RuntimeTerminationReason.Error,
                    new RuntimeError(
                        RuntimeErrorCategory.ProviderTransport,
                        "model_stream_failed",
                        ex.Message,
                        Retryable: false),
                    session);
            }
        }
    }

    private static async Task<ToolBatchExecution> ExecuteToolsAsync(
        RuntimeSessionState session,
        RuntimeAgentLoopRequest request,
        RuntimeStepContext step,
        RuntimeTurnHandle? handle,
        CancellationToken ct)
    {
        if (request.ToolPipeline != null)
        {
            return await ExecuteToolsWithPipelineAsync(
                session,
                request,
                step,
                handle,
                ct).ConfigureAwait(false);
        }

        var results = new List<RuntimeToolResult>();
        RuntimeError? fatalError = null;
        var descriptors = step.ModelRequest.Tools.ToDictionary(
            static descriptor => descriptor.CanonicalName,
            StringComparer.OrdinalIgnoreCase);
        foreach (var invocation in session.ActiveTurn!.Steps[^1].ToolInvocations ?? [])
        {
            ct.ThrowIfCancellationRequested();
            var call = invocation.Call;
            if (!descriptors.TryGetValue(call.Name, out var descriptor))
            {
                var result = FailureResult(
                    call,
                    RuntimeErrorCategory.UnknownTool,
                    "unknown_tool",
                    $"Tool '{call.Name}' was not exposed in the Step snapshot.");
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    call.InvocationId,
                    RuntimeToolInvocationStatus.Denied,
                    result);
                results.Add(result);
                continue;
            }
            if (session.ActiveTurn!.Progress.ToolCallCount >= request.Budget.MaxToolCalls)
            {
                var result = FailureResult(
                    call,
                    RuntimeErrorCategory.ResourceExhausted,
                    "tool_call_budget_exhausted",
                    "The Turn exhausted its tool-call budget.");
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    call.InvocationId,
                    RuntimeToolInvocationStatus.Denied,
                    result);
                results.Add(result);
                fatalError ??= result.Error;
                continue;
            }

            var executionContext = new RuntimeToolExecutionContext(
                request.SessionId,
                request.TurnId,
                step.StepId,
                step.Policy,
                step.Environment,
                step.Budget);
            RuntimeToolAuthorizationDecision authorization;
            try
            {
                authorization = request.ToolAuthorization == null
                    ? RuntimeToolAuthorizationDecision.Allow()
                    : await request.ToolAuthorization
                        .AuthorizeAsync(descriptor, call, executionContext, ct)
                        .ConfigureAwait(false);
                authorization ??= RuntimeToolAuthorizationDecision.Deny(
                    "Tool authorization returned no decision.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                var cancelled = FailureResult(
                    call,
                    RuntimeErrorCategory.Cancelled,
                    "tool_authorization_cancelled",
                    "Tool authorization was cancelled.");
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    call.InvocationId,
                    RuntimeToolInvocationStatus.Cancelled,
                    cancelled);
                throw new RuntimeAgentLoopCancelled(session);
            }
            catch (Exception ex)
            {
                authorization = RuntimeToolAuthorizationDecision.Deny(ex.Message);
                fatalError ??= new RuntimeError(
                    RuntimeErrorCategory.PolicyDenied,
                    "tool_authorization_failed",
                    ex.Message);
            }

            if (authorization.Kind == RuntimeToolAuthorizationKind.Deny)
            {
                var denied = FailureResult(
                    call,
                    RuntimeErrorCategory.PolicyDenied,
                    "tool_denied",
                    authorization.Reason ?? "Tool execution was denied.");
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    call.InvocationId,
                    RuntimeToolInvocationStatus.Denied,
                    denied);
                results.Add(denied);
                continue;
            }
            if (authorization.Kind is not (RuntimeToolAuthorizationKind.Allow or RuntimeToolAuthorizationKind.RequireApproval))
            {
                var invalid = FailureResult(
                    call,
                    RuntimeErrorCategory.PolicyDenied,
                    "invalid_tool_authorization_decision",
                    "Tool authorization returned an unknown decision.");
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    call.InvocationId,
                    RuntimeToolInvocationStatus.Denied,
                    invalid);
                results.Add(invalid);
                fatalError ??= invalid.Error;
                continue;
            }
            if (authorization.Kind == RuntimeToolAuthorizationKind.RequireApproval)
            {
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    call.InvocationId,
                    RuntimeToolInvocationStatus.AwaitingApproval);
                if (handle == null)
                {
                    var unavailable = FailureResult(
                        call,
                        RuntimeErrorCategory.ApprovalDeclined,
                        "approval_handle_unavailable",
                        "Tool approval was required but no provisional Turn handle was supplied.");
                    session = RuntimeStateReducer.TransitionTool(
                        session,
                        step.StepId,
                        call.InvocationId,
                        RuntimeToolInvocationStatus.Denied,
                        unavailable);
                    results.Add(unavailable);
                    fatalError ??= unavailable.Error;
                    continue;
                }

                bool approved;
                try
                {
                    approved = await handle.WaitForApprovalAsync(call.InvocationId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    var cancelled = FailureResult(
                        call,
                        RuntimeErrorCategory.Cancelled,
                        "approval_cancelled",
                        "Tool approval was cancelled.");
                    session = RuntimeStateReducer.TransitionTool(
                        session,
                        step.StepId,
                        call.InvocationId,
                        RuntimeToolInvocationStatus.Cancelled,
                        cancelled);
                    throw new RuntimeAgentLoopCancelled(session);
                }
                if (!approved)
                {
                    var declined = FailureResult(
                        call,
                        RuntimeErrorCategory.ApprovalDeclined,
                        "approval_declined",
                        "Tool approval was declined.");
                    session = RuntimeStateReducer.TransitionTool(
                        session,
                        step.StepId,
                        call.InvocationId,
                        RuntimeToolInvocationStatus.Denied,
                        declined);
                    results.Add(declined);
                    continue;
                }
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    call.InvocationId,
                    RuntimeToolInvocationStatus.Approved);
            }

            session = RuntimeStateReducer.TransitionTool(
                session,
                step.StepId,
                call.InvocationId,
                RuntimeToolInvocationStatus.Executing);
            RuntimeToolResult toolResult;
            try
            {
                toolResult = await request.ToolExecutor!
                    .ExecuteAsync(descriptor, call, executionContext, ct)
                    .ConfigureAwait(false);
                if (toolResult == null)
                {
                    toolResult = FailureResult(
                        call,
                        RuntimeErrorCategory.ToolFailed,
                        "null_tool_result",
                        "The tool returned no observation.");
                }
                else if (toolResult.InvocationId != call.InvocationId)
                {
                    toolResult = FailureResult(
                        call,
                        RuntimeErrorCategory.ToolFailed,
                        "tool_result_identity_mismatch",
                        "The tool returned an observation for another invocation.");
                }
                else if (!toolResult.Success && toolResult.Error == null)
                {
                    toolResult = FailureResult(
                        call,
                        RuntimeErrorCategory.ToolFailed,
                        "tool_failed_without_error",
                        "The tool returned an unsuccessful observation without a typed error.");
                }
                else if (toolResult.Success && toolResult.Error != null)
                {
                    toolResult = FailureResult(
                        call,
                        RuntimeErrorCategory.ToolFailed,
                        "tool_success_with_error",
                        "The tool returned a successful observation with a typed error.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                var cancelled = FailureResult(
                    call,
                    RuntimeErrorCategory.Cancelled,
                    "tool_execution_cancelled",
                    "Tool execution was cancelled.");
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    call.InvocationId,
                    RuntimeToolInvocationStatus.Cancelled,
                    cancelled);
                throw new RuntimeAgentLoopCancelled(session);
            }
            catch (Exception ex)
            {
                toolResult = FailureResult(
                    call,
                    RuntimeErrorCategory.ToolFailed,
                    "tool_execution_failed",
                    ex.Message);
            }

            session = RuntimeStateReducer.TransitionTool(
                session,
                step.StepId,
                call.InvocationId,
                toolResult.Success
                    ? RuntimeToolInvocationStatus.Succeeded
                    : RuntimeToolInvocationStatus.Failed,
                toolResult);
            results.Add(toolResult);
        }

        return new ToolBatchExecution(session, results, fatalError);
    }

    private static async Task<ToolBatchExecution> ExecuteToolsWithPipelineAsync(
        RuntimeSessionState session,
        RuntimeAgentLoopRequest request,
        RuntimeStepContext step,
        RuntimeTurnHandle? handle,
        CancellationToken ct)
    {
        var invocations = session.ActiveTurn!.Steps[^1].ToolInvocations ?? [];
        var results = new RuntimeToolResult?[invocations.Count];
        var prepared = new List<(int Index, RuntimePreparedToolInvocation Invocation)>();
        RuntimeError? fatalError = null;
        var exposedTools = step.ModelRequest.Tools.ToDictionary(
            static descriptor => descriptor.CanonicalName,
            StringComparer.OrdinalIgnoreCase);
        var executionContext = new RuntimeToolExecutionContext(
            request.SessionId,
            request.TurnId,
            step.StepId,
            step.Policy,
            step.Environment,
            step.Budget);

        for (var index = 0; index < invocations.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var call = invocations[index].Call;
            if (!exposedTools.ContainsKey(call.Name))
            {
                var unknown = FailureResult(
                    call,
                    RuntimeErrorCategory.UnknownTool,
                    "tool_not_exposed_in_context",
                    $"Tool '{call.Name}' was not exposed in the prepared Step context.");
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    call.InvocationId,
                    RuntimeToolInvocationStatus.Denied,
                    unknown);
                results[index] = unknown;
                continue;
            }
            if (session.ActiveTurn!.Progress.ToolCallCount + prepared.Count >= request.Budget.MaxToolCalls)
            {
                var exhausted = FailureResult(
                    call,
                    RuntimeErrorCategory.ResourceExhausted,
                    "tool_call_budget_exhausted",
                    "The Turn exhausted its tool-call budget.");
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    call.InvocationId,
                    RuntimeToolInvocationStatus.Denied,
                    exhausted);
                results[index] = exhausted;
                fatalError ??= exhausted.Error;
                continue;
            }

            RuntimePreparedToolInvocation plan;
            try
            {
                plan = await request.ToolPipeline!
                    .PrepareAsync(call, executionContext, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                var cancelled = FailureResult(
                    call,
                    RuntimeErrorCategory.Cancelled,
                    "tool_preparation_cancelled",
                    "Tool preparation was cancelled.");
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    call.InvocationId,
                    RuntimeToolInvocationStatus.Cancelled,
                    cancelled);
                throw new RuntimeAgentLoopCancelled(session);
            }
            catch (Exception ex)
            {
                plan = new RuntimePreparedToolInvocation(
                    RuntimeToolPreparationKind.Denied,
                    call,
                    null,
                    null,
                    FailureResult(
                        call,
                        RuntimeErrorCategory.RuntimeInvariantViolation,
                        "tool_preparation_failed",
                        ex.Message));
                fatalError ??= plan.Observation!.Error;
            }

            if (plan.Kind == RuntimeToolPreparationKind.Denied || plan.Plan == null || plan.Tool == null)
            {
                var denied = plan.Observation ?? FailureResult(
                    call,
                    RuntimeErrorCategory.RuntimeInvariantViolation,
                    "invalid_tool_preparation",
                    "The tool pipeline returned an invalid preparation result.");
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    call.InvocationId,
                    RuntimeToolInvocationStatus.Denied,
                    denied);
                results[index] = denied;
                continue;
            }

            if (plan.RequiresApproval)
            {
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    call.InvocationId,
                    RuntimeToolInvocationStatus.AwaitingApproval);
                if (request.ToolApproval == null)
                {
                    var unavailable = FailureResult(
                        call,
                        RuntimeErrorCategory.ApprovalDeclined,
                        "bound_approval_unavailable",
                        "Tool approval was required but no plan-bound approval provider was supplied.");
                    session = RuntimeStateReducer.TransitionTool(
                        session,
                        step.StepId,
                        call.InvocationId,
                        RuntimeToolInvocationStatus.Denied,
                        unavailable);
                    results[index] = unavailable;
                    continue;
                }

                RuntimeToolApprovalDecision approval;
                try
                {
                    approval = await request.ToolApproval
                        .DecideAsync(plan.Plan, call, executionContext with { Plan = plan.Plan }, ct)
                        .ConfigureAwait(false) ?? RuntimeToolApprovalDecision.Decline(
                            "The plan-bound approval provider returned no decision.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    var cancelled = FailureResult(
                        call,
                        RuntimeErrorCategory.Cancelled,
                        "approval_cancelled",
                        "Tool approval was cancelled.");
                    session = RuntimeStateReducer.TransitionTool(
                        session,
                        step.StepId,
                        call.InvocationId,
                        RuntimeToolInvocationStatus.Cancelled,
                        cancelled);
                    throw new RuntimeAgentLoopCancelled(session);
                }
                if (!approval.Approved)
                {
                    var declined = FailureResult(
                        call,
                        RuntimeErrorCategory.ApprovalDeclined,
                        "approval_declined",
                        approval.Reason ?? "Tool approval was declined.");
                    session = RuntimeStateReducer.TransitionTool(
                        session,
                        step.StepId,
                        call.InvocationId,
                        RuntimeToolInvocationStatus.Denied,
                        declined);
                    results[index] = declined;
                    continue;
                }
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    call.InvocationId,
                    RuntimeToolInvocationStatus.Approved);
            }

            session = RuntimeStateReducer.TransitionTool(
                session,
                step.StepId,
                call.InvocationId,
                RuntimeToolInvocationStatus.Executing);
            prepared.Add((index, plan));
        }

        try
        {
            var scheduler = new RuntimeToolScheduler();
            var executed = await scheduler.ExecuteAsync(
                prepared.Select(item => new RuntimeScheduledToolInvocation(
                    item.Invocation.Plan!.Concurrency,
                    token => request.ToolPipeline!.ExecuteAsync(item.Invocation, executionContext, token),
                    item.Invocation.Plan.ToolCanonicalName,
                    item.Invocation.Plan.WorkspaceIdentity))
                    .ToArray(),
                ct).ConfigureAwait(false);
            for (var preparedIndex = 0; preparedIndex < prepared.Count; preparedIndex++)
            {
                var (resultIndex, plan) = prepared[preparedIndex];
                var result = ValidateToolResult(plan.Call, executed[preparedIndex]);
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    plan.Call.InvocationId,
                    result.Success
                        ? RuntimeToolInvocationStatus.Succeeded
                        : RuntimeToolInvocationStatus.Failed,
                    result);
                results[resultIndex] = result;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            foreach (var (_, plan) in prepared)
            {
                var current = session.ActiveTurn!.Steps[^1].ToolInvocations!
                    .Single(item => item.Call.InvocationId == plan.Call.InvocationId);
                if (current.Status != RuntimeToolInvocationStatus.Executing)
                {
                    continue;
                }
                var cancelled = FailureResult(
                    plan.Call,
                    RuntimeErrorCategory.Cancelled,
                    "tool_execution_cancelled",
                    "Tool execution was cancelled.");
                session = RuntimeStateReducer.TransitionTool(
                    session,
                    step.StepId,
                    plan.Call.InvocationId,
                    RuntimeToolInvocationStatus.Cancelled,
                    cancelled);
            }
            throw new RuntimeAgentLoopCancelled(session);
        }

        return new ToolBatchExecution(
            session,
            Array.AsReadOnly(results.Select((result, index) => result ?? FailureResult(
                    invocations[index].Call,
                    RuntimeErrorCategory.RuntimeInvariantViolation,
                    "missing_tool_observation",
                    "The tool pipeline did not produce an observation."))
                .ToArray()),
            fatalError);
    }

    private static RuntimeToolResult ValidateToolResult(RuntimeToolCall call, RuntimeToolResult? result)
    {
        if (result == null)
        {
            return FailureResult(call, RuntimeErrorCategory.ToolFailed, "null_tool_result", "The tool returned no observation.");
        }
        if (result.InvocationId != call.InvocationId)
        {
            return FailureResult(call, RuntimeErrorCategory.ToolFailed, "tool_result_identity_mismatch", "The tool returned an observation for another invocation.");
        }
        if (!result.Success && result.Error == null)
        {
            return FailureResult(call, RuntimeErrorCategory.ToolFailed, "tool_failed_without_error", "The tool returned an unsuccessful observation without a typed error.");
        }
        if (result.Success && result.Error != null)
        {
            return FailureResult(call, RuntimeErrorCategory.ToolFailed, "tool_success_with_error", "The tool returned a successful observation with a typed error.");
        }
        return result;
    }

    private static async ValueTask<RuntimeTerminationDecision> DecideTerminationAsync(
        RuntimeSessionState session,
        IReadOnlyList<RuntimeMessage> history,
        RuntimeAgentLoopRequest request,
        RuntimeTurnHandle? handle,
        bool canContinue,
        CancellationToken ct)
    {
        var turn = session.ActiveTurn!;
        var step = turn.Steps[^1];
        var stopReason = step.Output!.StopReason;
        if (stopReason == RuntimeModelStopReason.Error)
        {
            return RuntimeTerminationDecision.FailClosed(new RuntimeError(
                RuntimeErrorCategory.ProviderProtocol,
                "provider_reported_error",
                "The provider completed the stream with an error stop reason."));
        }
        if (stopReason == RuntimeModelStopReason.ContentFilter)
        {
            return RuntimeTerminationDecision.FailClosed(new RuntimeError(
                RuntimeErrorCategory.ProviderProtocol,
                "content_filtered",
                "The provider stopped because content was filtered."));
        }
        if (stopReason == RuntimeModelStopReason.ToolCall)
        {
            return RuntimeTerminationDecision.FailClosed(new RuntimeError(
                RuntimeErrorCategory.ProviderProtocol,
                "tool_stop_without_tool_call",
                "The provider reported a tool-call stop without a structured tool call."));
        }
        if (!turn.Progress.RequiredToolSatisfied && !string.IsNullOrWhiteSpace(turn.Progress.RequiredToolName))
        {
            return RuntimeTerminationDecision.RequireTool(
                turn.Progress.RequiredToolName,
                $"You must call the required tool '{turn.Progress.RequiredToolName}' before completing.");
        }
        if (handle?.HasPendingSteering == true)
        {
            return RuntimeTerminationDecision.Continue("Apply the pending host steering message before completing.");
        }
        if (stopReason == RuntimeModelStopReason.MaxOutputTokens)
        {
            return RuntimeTerminationDecision.Continue("Continue the previous response without repeating completed content.");
        }
        if (request.TerminationPolicy == null)
        {
            return RuntimeTerminationDecision.Accept();
        }
        try
        {
            return await request.TerminationPolicy.DecideAsync(
                new RuntimeTerminationContext(session, step, history, canContinue),
                ct).ConfigureAwait(false) ?? RuntimeTerminationDecision.FailClosed(new RuntimeError(
                    RuntimeErrorCategory.RuntimeInvariantViolation,
                    "null_termination_decision",
                    "The termination policy returned no decision."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RuntimeTerminationDecision.FailClosed(new RuntimeError(
                RuntimeErrorCategory.RuntimeInvariantViolation,
                "termination_policy_failed",
                ex.Message));
        }
    }

    private static async ValueTask<PostStepDecision> ApplyPostStepDecisionAsync(
        RuntimeSessionState session,
        RuntimeHistory history,
        RuntimeAgentLoopRequest request,
        RuntimeTurnHandle? handle,
        ICollection<RuntimeContextEvent> contextEvents,
        CancellationToken ct)
    {
        var step = session.ActiveTurn?.Steps.LastOrDefault() ??
            throw new RuntimeAgentLoopFailure(
                RuntimeTerminationReason.FailClosed,
                new RuntimeError(
                    RuntimeErrorCategory.RuntimeInvariantViolation,
                    "resume_step_missing",
                    "A committed Step is required before applying the termination decision."));
        if (step.Phase != RuntimeStepPhase.Completed || step.Output == null)
        {
            throw new RuntimeAgentLoopFailure(
                RuntimeTerminationReason.FailClosed,
                new RuntimeError(
                    RuntimeErrorCategory.RuntimeInvariantViolation,
                    "resume_step_not_committed",
                    "The post-Step decision requires a completed Step with committed output."));
        }
        var canContinue = step.Context.Index + 1 < request.Budget.MaxSteps &&
            session.ActiveTurn!.Progress.ContinuationCount < request.Budget.MaxContinuations;
        var decision = await DecideTerminationAsync(
            session,
            history.ToMessages(),
            request,
            handle,
            canContinue,
            ct).ConfigureAwait(false);
        switch (decision.Kind)
        {
            case RuntimeTerminationDecisionKind.Accept:
                return new PostStepDecision(
                    RuntimeStateReducer.FinishTurn(
                        session,
                        RuntimeTurnStatus.Completed,
                        RuntimeTerminationReason.Completed),
                    Complete: true);
            case RuntimeTerminationDecisionKind.FailClosed:
                throw new RuntimeAgentLoopFailure(
                    RuntimeTerminationReason.FailClosed,
                    decision.Error ?? new RuntimeError(
                        RuntimeErrorCategory.RuntimeInvariantViolation,
                        "termination_policy_failed_closed",
                        "The termination policy failed closed."));
            case RuntimeTerminationDecisionKind.Continue:
            case RuntimeTerminationDecisionKind.RequireTool:
                if (!canContinue)
                {
                    var reason = decision.Kind == RuntimeTerminationDecisionKind.RequireTool
                        ? RuntimeTerminationReason.RequiredToolMissing
                        : RuntimeTerminationReason.MaxSteps;
                    throw new RuntimeAgentLoopFailure(
                        reason,
                        new RuntimeError(
                            RuntimeErrorCategory.ResourceExhausted,
                            "continuation_budget_exhausted",
                            "The runtime could not honor the requested semantic continuation."));
                }
                if (decision.Kind == RuntimeTerminationDecisionKind.RequireTool)
                {
                    EnsureRequiredToolAvailable(decision.RequiredToolName, request.Tools);
                }
                if (!string.IsNullOrWhiteSpace(decision.Feedback))
                {
                    session = CommitHistoryBatch(
                        session,
                        history,
                        [new RuntimeMessage(
                            RuntimeMessageRole.User,
                            [new RuntimeTextItem(decision.Feedback)])]);
                    await PublishHistoryEventsAsync(
                        history,
                        request.ContextEventSink,
                        contextEvents,
                        ct).ConfigureAwait(false);
                }
                session = RuntimeStateReducer.RecordContinuation(
                    session,
                    decision.Kind == RuntimeTerminationDecisionKind.RequireTool
                        ? decision.RequiredToolName
                        : null);
                return new PostStepDecision(session, Complete: false);
            default:
                throw new RuntimeAgentLoopFailure(
                    RuntimeTerminationReason.FailClosed,
                    new RuntimeError(
                        RuntimeErrorCategory.RuntimeInvariantViolation,
                        "unknown_termination_decision",
                        "The termination policy returned an unknown decision."));
        }
    }

    private static RuntimeSessionState ObserveOutstandingTools(
        RuntimeSessionState session,
        RuntimeError error,
        bool cancelled)
    {
        var turn = session.ActiveTurn;
        var step = turn?.Steps.LastOrDefault();
        if (step?.ToolInvocations == null)
        {
            return session;
        }
        foreach (var invocation in step.ToolInvocations.Where(static invocation =>
                     invocation.Status is not (RuntimeToolInvocationStatus.Denied or
                         RuntimeToolInvocationStatus.Succeeded or
                         RuntimeToolInvocationStatus.Failed or
                         RuntimeToolInvocationStatus.Cancelled)))
        {
            var result = new RuntimeToolResult(
                invocation.Call.InvocationId,
                null,
                false,
                error);
            var target = cancelled
                ? RuntimeToolInvocationStatus.Cancelled
                : invocation.Status == RuntimeToolInvocationStatus.Executing
                    ? RuntimeToolInvocationStatus.Failed
                    : RuntimeToolInvocationStatus.Denied;
            session = RuntimeStateReducer.TransitionTool(
                session,
                step.Context.StepId,
                invocation.Call.InvocationId,
                target,
                result);
        }
        return session;
    }

    private static RuntimeSessionState CancelTurn(RuntimeSessionState session)
    {
        var error = new RuntimeError(
            RuntimeErrorCategory.Cancelled,
            "turn_cancelled",
            "The Turn was cancelled.");
        session = ObserveOutstandingTools(session, error, cancelled: true);
        var step = session.ActiveTurn?.Steps.LastOrDefault();
        if (step != null && step.Phase is not (RuntimeStepPhase.Completed or RuntimeStepPhase.Failed or RuntimeStepPhase.Cancelled))
        {
            session = RuntimeStateReducer.TransitionStep(
                session,
                step.Context.StepId,
                RuntimeStepPhase.Cancelled);
        }
        return RuntimeStateReducer.FinishTurn(
            session,
            RuntimeTurnStatus.Cancelled,
            RuntimeTerminationReason.Cancelled);
    }

    private static RuntimeSessionState FailTurn(
        RuntimeSessionState session,
        RuntimeTerminationReason reason,
        RuntimeError error)
    {
        session = ObserveOutstandingTools(session, error, cancelled: false);
        var step = session.ActiveTurn?.Steps.LastOrDefault();
        if (step != null && step.Phase is not (RuntimeStepPhase.Completed or RuntimeStepPhase.Failed or RuntimeStepPhase.Cancelled))
        {
            session = RuntimeStateReducer.TransitionStep(
                session,
                step.Context.StepId,
                RuntimeStepPhase.Failed,
                error);
        }
        return RuntimeStateReducer.FinishTurn(session, RuntimeTurnStatus.Failed, reason, error);
    }

    private static RuntimeAgentLoopResult CreateResult(
        RuntimeSessionState session,
        RuntimeHistory history,
        string finalText,
        IReadOnlyList<PreparedRuntimeContext> preparedContexts,
        IReadOnlyList<RuntimeContextEvent> contextEvents,
        RuntimeAuditEmitter audit,
        RuntimeCheckpointEmitter checkpoints)
    {
        var historySnapshot = history.Snapshot();
        return new RuntimeAgentLoopResult(
            session,
            session.TerminalTurns[^1],
            historySnapshot.Messages.Select(static entry => entry.Message).ToArray(),
            finalText)
        {
            PreparedContexts = Array.AsReadOnly(preparedContexts.ToArray()),
            ContextEvents = Array.AsReadOnly(contextEvents.ToArray()),
            AuditEvents = audit.Events,
            AuditWarnings = audit.Warnings,
            CheckpointWarnings = checkpoints.Warnings,
            HistoryBlobs = historySnapshot.Blobs,
            Attempt = checkpoints.Attempt
        };
    }

    private static async Task<RuntimeAgentLoopResult> CompleteResultAsync(
        RuntimeSessionState session,
        RuntimeHistory history,
        string finalText,
        IReadOnlyList<PreparedRuntimeContext> preparedContexts,
        IReadOnlyList<RuntimeContextEvent> contextEvents,
        RuntimeAuditEmitter audit,
        RuntimeCheckpointEmitter checkpoints)
    {
        var result = CreateResult(session, history, finalText, preparedContexts, contextEvents, audit, checkpoints);
        try
        {
            await checkpoints.SaveAsync(
                RuntimeCheckpointKind.Terminal,
                session,
                history,
                finalText,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (RuntimeCheckpointWriteFailure failure)
        {
            session = ReplaceTerminalFailure(session, failure.Error);
            result = CreateResult(session, history, finalText, preparedContexts, contextEvents, audit, checkpoints);
        }
        try
        {
            await audit.EmitAsync(
                RuntimeAuditEventKind.TurnTerminal,
                RuntimeAuditSensitivity.Sensitive,
                new RuntimeTurnTerminalAuditPayload(
                    result.Status,
                    result.TerminationReason,
                    result.Error,
                    result.FinalText,
                    result.Usage,
                    result.Turn.Steps.Count,
                    result.Turn.Progress.ToolCallCount,
                    result.Turn.Progress.ContinuationCount,
                    result.Session.HistoryVersion,
                    result.History),
                CancellationToken.None).ConfigureAwait(false);
            return CreateResult(session, history, finalText, preparedContexts, contextEvents, audit, checkpoints);
        }
        catch (RuntimeAuditWriteFailure failure)
        {
            session = ReplaceTerminalFailure(session, failure.Error);
            await checkpoints.TrySaveTerminalBestEffortAsync(
                session,
                history,
                finalText).ConfigureAwait(false);
            return CreateResult(session, history, finalText, preparedContexts, contextEvents, audit, checkpoints);
        }
    }

    private static RuntimeSessionState ReplaceTerminalFailure(
        RuntimeSessionState session,
        RuntimeError error)
    {
        var terminal = session.TerminalTurns[^1] with
        {
            Status = RuntimeTurnStatus.Failed,
            TerminationReason = RuntimeTerminationReason.FailClosed,
            Error = error
        };
        var turns = session.TerminalTurns.ToArray();
        turns[^1] = terminal;
        return session with { TerminalTurns = Array.AsReadOnly(turns) };
    }

    private static RuntimeSessionState CommitHistoryBatch(
        RuntimeSessionState session,
        RuntimeHistory history,
        IReadOnlyList<RuntimeMessage> messages)
    {
        var historyVersion = history.AppendBatch(messages);
        session = RuntimeStateReducer.AdvanceHistory(session);
        if (session.HistoryVersion != historyVersion)
        {
            throw new InvalidOperationException("RuntimeHistory and reducer historyVersion diverged.");
        }
        return session;
    }

    private static async ValueTask PublishHistoryEventsAsync(
        RuntimeHistory history,
        IRuntimeContextEventSink? sink,
        ICollection<RuntimeContextEvent> collected,
        CancellationToken ct)
    {
        foreach (var runtimeEvent in history.DrainEvents())
        {
            collected.Add(runtimeEvent);
            if (sink != null)
            {
                await sink.OnEventAsync(runtimeEvent, ct).ConfigureAwait(false);
            }
        }
    }

    private static IReadOnlyList<RuntimeToolDescriptor> ValidateSelectedTools(
        IReadOnlyList<RuntimeToolDescriptor> selected,
        IReadOnlyList<RuntimeToolDescriptor> frozenCatalog)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var catalog = frozenCatalog.ToDictionary(
            static descriptor => descriptor.CanonicalName,
            StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshot = new List<RuntimeToolDescriptor>(selected.Count);
        foreach (var descriptor in selected)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (!names.Add(descriptor.CanonicalName) ||
                !catalog.TryGetValue(descriptor.CanonicalName, out var registered) ||
                !string.Equals(descriptor.Version, registered.Version, StringComparison.Ordinal) ||
                !JsonElement.DeepEquals(descriptor.InputSchema, registered.InputSchema))
            {
                throw new InvalidOperationException(
                    $"Tool catalog selector returned an unknown, duplicate, or mutated descriptor '{descriptor.CanonicalName}'.");
            }
            snapshot.Add(descriptor with { InputSchema = descriptor.InputSchema.Clone() });
        }
        return Array.AsReadOnly(snapshot.ToArray());
    }

    private static RuntimeMessage CreateAssistantMessage(RuntimeModelOutput output)
        => new(RuntimeMessageRole.Assistant, output.Items.Select(SnapshotItem).ToArray());

    private static RuntimeMessage SnapshotMessage(RuntimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message with { Items = message.Items.Select(SnapshotItem).ToArray() };
    }

    private static RuntimeItem SnapshotItem(RuntimeItem item)
        => item switch
        {
            RuntimeTextItem text => text,
            RuntimeReasoningItem reasoning => reasoning,
            RuntimeToolCallItem toolCall => toolCall with
            {
                Call = toolCall.Call with { Arguments = toolCall.Call.Arguments.Clone() }
            },
            RuntimeToolResultItem toolResult => toolResult with
            {
                Result = toolResult.Result with { Artifacts = toolResult.Result.Artifacts?.ToArray() }
            },
            RuntimeArtifactItem artifact => artifact,
            _ => throw new ArgumentException(
                $"Unsupported Runtime item type '{item.GetType().FullName}'.",
                nameof(item))
        };

    private static RuntimeUsageTotals AddUsage(RuntimeUsageTotals current, RuntimeUsage added)
    {
        var additional = new Dictionary<string, long>(current.Additional, StringComparer.Ordinal);
        if (added.Additional != null)
        {
            foreach (var pair in added.Additional)
            {
                additional[pair.Key] = checked(additional.GetValueOrDefault(pair.Key) + pair.Value);
            }
        }
        return new RuntimeUsageTotals(
            checked(current.InputTokens + added.InputTokens.GetValueOrDefault()),
            checked(current.OutputTokens + added.OutputTokens.GetValueOrDefault()),
            checked(current.TotalTokens + added.TotalTokens.GetValueOrDefault()),
            additional);
    }

    private static bool IsTerminalPolicyFailure(RuntimeError? error)
        => error is
        {
            Retryable: false,
            Category: RuntimeErrorCategory.ApprovalDeclined or
                      RuntimeErrorCategory.ApprovalTimeout
        };

    private static RuntimeToolResult FailureResult(
        RuntimeToolCall call,
        RuntimeErrorCategory category,
        string code,
        string message)
        => new(
            call.InvocationId,
            null,
            false,
            new RuntimeError(category, code, message),
            Details: new RuntimeToolResultDetails(category switch
            {
                RuntimeErrorCategory.Cancelled => RuntimeToolOutcome.Cancelled,
                RuntimeErrorCategory.SandboxTimeout => RuntimeToolOutcome.TimedOut,
                RuntimeErrorCategory.ToolFailed or RuntimeErrorCategory.UncertainSideEffect => RuntimeToolOutcome.Failed,
                _ => RuntimeToolOutcome.Denied
            }));

    private static bool ExceedsTokenBudget(RuntimeUsageTotals usage, RuntimeBudgetSnapshot budget)
        => budget.MaxInputTokens.HasValue && usage.InputTokens > budget.MaxInputTokens.Value ||
           budget.MaxOutputTokens.HasValue && usage.OutputTokens > budget.MaxOutputTokens.Value;

    private static void EnsureToolStopReason(RuntimeModelStopReason stopReason)
    {
        if (stopReason is RuntimeModelStopReason.Cancelled or
            RuntimeModelStopReason.Error or
            RuntimeModelStopReason.ContentFilter or
            RuntimeModelStopReason.MaxOutputTokens)
        {
            throw new RuntimeAgentLoopFailure(
                RuntimeTerminationReason.FailClosed,
                new RuntimeError(
                    RuntimeErrorCategory.ProviderProtocol,
                    "unsafe_tool_stop_reason",
                    $"Tool calls cannot execute after provider stop reason '{stopReason}'."));
        }
    }

    private static void EnsureRequiredToolAvailable(
        string? requiredToolName,
        IReadOnlyList<RuntimeToolDescriptor> tools)
    {
        if (string.IsNullOrWhiteSpace(requiredToolName) ||
            !tools.Any(tool => string.Equals(
                tool.CanonicalName,
                requiredToolName,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new RuntimeAgentLoopFailure(
                RuntimeTerminationReason.RequiredToolMissing,
                new RuntimeError(
                    RuntimeErrorCategory.UnknownTool,
                    "required_tool_unavailable",
                    "The termination policy required a tool that is not exposed in the Step snapshot."));
        }
    }

    private static void ValidateRequest(RuntimeAgentLoopRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TurnId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Objective);
        ArgumentNullException.ThrowIfNull(request.InitialMessages);
        ArgumentNullException.ThrowIfNull(request.Tools);
        ArgumentNullException.ThrowIfNull(request.ModelParameters);
        ArgumentNullException.ThrowIfNull(request.Policy);
        ArgumentNullException.ThrowIfNull(request.Environment);
        ArgumentNullException.ThrowIfNull(request.Budget);
        if (request.HistoryVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.HistoryVersion));
        }
        if (request.CheckpointSink != null &&
            (string.IsNullOrWhiteSpace(request.RecoveryCompatibilityId) ||
             !string.Equals(
                 request.RecoveryCompatibilityId,
                 request.RecoveryCompatibilityId.Trim(),
                 StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Durable checkpointing requires a non-empty, normalized host RecoveryCompatibilityId.",
                nameof(request));
        }
        if (request.CheckpointSink != null &&
            request.CheckpointFailureMode != RuntimeCheckpointFailureMode.FailClosed)
        {
            throw new ArgumentException(
                "Durable H1 recovery requires fail-closed checkpoint writes.",
                nameof(request));
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in request.Tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            ArgumentException.ThrowIfNullOrWhiteSpace(tool.CanonicalName);
            ArgumentException.ThrowIfNullOrWhiteSpace(tool.Version);
            if (!names.Add(tool.CanonicalName))
            {
                throw new ArgumentException(
                    $"Duplicate tool canonical name '{tool.CanonicalName}'.",
                    nameof(request));
            }
        }
        if (request.Tools.Count > 0 && request.ToolExecutor == null && request.ToolPipeline == null)
        {
            throw new ArgumentException(
                "A tool executor is required when tools are exposed to the model.",
                nameof(request));
        }
        if (request.ToolPipeline != null)
        {
            var pipelineTools = request.ToolPipeline.Descriptors.ToDictionary(
                static descriptor => descriptor.CanonicalName,
                StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in request.Tools)
            {
                if (!pipelineTools.TryGetValue(descriptor.CanonicalName, out var pipelineDescriptor) ||
                    !string.Equals(descriptor.Version, pipelineDescriptor.Version, StringComparison.Ordinal) ||
                    !JsonElement.DeepEquals(descriptor.InputSchema, pipelineDescriptor.InputSchema))
                {
                    throw new ArgumentException(
                        $"Step tool '{descriptor.CanonicalName}' does not match the execution pipeline catalog.",
                        nameof(request));
                }
            }
            if (pipelineTools.Count != request.Tools.Count)
            {
                throw new ArgumentException(
                    "The model-visible tool catalog must exactly match the execution pipeline catalog.",
                    nameof(request));
            }
        }
        if (!string.IsNullOrWhiteSpace(request.ModelParameters.RequiredToolName))
        {
            var requiredToolName = request.ModelParameters.RequiredToolName.Trim();
            if (!request.Tools.Any(tool => string.Equals(
                    tool.CanonicalName,
                    requiredToolName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    $"Required tool '{requiredToolName}' is not exposed by the request.",
                    nameof(request));
            }
        }
    }

    private static void ValidateResumeRequest(
        RuntimeAgentLoopRequest request,
        RuntimeCheckpointDocument checkpoint,
        RuntimeRunAttempt attempt)
    {
        RuntimeJsonCheckpointStore.ValidateDocument(checkpoint);
        if (request.CheckpointSink == null)
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.RuntimeInvariantViolation,
                "resume_checkpoint_sink_required",
                "A resumed attempt requires a checkpoint sink."));
        }
        if (request.ToolCatalogSelector != null)
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.RuntimeInvariantViolation,
                "resume_dynamic_tool_catalog_unsupported",
                "H1 cannot resume a mutable dynamic tool catalog because its activation state is not durable."));
        }
        if (checkpoint.Disposition == RuntimeCheckpointDisposition.Terminal ||
            checkpoint.Kind == RuntimeCheckpointKind.Terminal)
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.RuntimeInvariantViolation,
                "checkpoint_already_terminal",
                "A terminal checkpoint cannot be resumed."));
        }
        if (checkpoint.Disposition == RuntimeCheckpointDisposition.NeedsReconciliation)
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.UncertainSideEffect,
                "checkpoint_needs_reconciliation",
                checkpoint.ReconciliationReason ??
                "The checkpoint contains tool calls with an uncertain execution outcome."));
        }
        var fingerprint = RuntimeCheckpointFingerprint.Compute(
            RuntimeCheckpointRequestSnapshot.Capture(request));
        if (!string.Equals(fingerprint, checkpoint.RequestFingerprint, StringComparison.Ordinal))
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.RuntimeInvariantViolation,
                "checkpoint_request_mismatch",
                "The recovery request does not match the frozen checkpoint request."));
        }
        if (attempt.AttemptId == checkpoint.Attempt.AttemptId ||
            attempt.ParentAttemptId != checkpoint.Attempt.AttemptId ||
            attempt.RootAttemptId != checkpoint.Attempt.RootAttemptId ||
            attempt.Ordinal != checkpoint.Attempt.Ordinal + 1)
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.RuntimeInvariantViolation,
                "checkpoint_attempt_lineage_invalid",
                "The recovery attempt does not extend the checkpoint attempt lineage."));
        }
    }

    private static (RuntimeSessionState Session, RuntimeCheckpointKind Kind) PrepareResumeState(
        RuntimeSessionState session,
        RuntimeHistory history,
        RuntimeCheckpointDocument checkpoint)
    {
        var turn = session.ActiveTurn ??
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.TraceCorrupt,
                "checkpoint_active_turn_missing",
                "A resumable checkpoint must contain one active Turn."));
        switch (checkpoint.Kind)
        {
            case RuntimeCheckpointKind.TurnStarted:
            case RuntimeCheckpointKind.ToolBatchCommitted:
            case RuntimeCheckpointKind.ContinuationCommitted:
                return (session, checkpoint.Kind);
            case RuntimeCheckpointKind.StepPrepared:
            {
                var step = turn.Steps.LastOrDefault();
                if (step == null || step.Phase is not (RuntimeStepPhase.Preparing or RuntimeStepPhase.Sampling))
                {
                    throw ResumeCorrupt(
                        "checkpoint_prepared_step_invalid",
                        "A StepPrepared checkpoint must contain one incomplete preparing or sampling Step.");
                }
                var remaining = Array.AsReadOnly(turn.Steps.Take(turn.Steps.Count - 1).ToArray());
                return (session with { ActiveTurn = turn with { Steps = remaining } }, checkpoint.Kind);
            }
            case RuntimeCheckpointKind.ModelCommitted:
            {
                var step = turn.Steps.LastOrDefault();
                if (step?.Output == null || step.Phase != RuntimeStepPhase.CommittingObservation ||
                    step.Output.ToolCalls.Count != 0)
                {
                    throw ResumeCorrupt(
                        "checkpoint_model_boundary_invalid",
                        "A resumable ModelCommitted checkpoint must contain a text-only committed model output.");
                }
                if (ExceedsTokenBudget(turn.Progress.Usage, checkpoint.Request.Budget) ||
                    step.Output.StopReason == RuntimeModelStopReason.Cancelled ||
                    string.IsNullOrEmpty(step.Output.Text))
                {
                    throw ResumeCorrupt(
                        "checkpoint_model_output_invalid",
                        "The committed model output is cancelled, empty, or exceeds the frozen token budget.");
                }
                session = RuntimeStateReducer.TransitionStep(
                    session,
                    step.Context.StepId,
                    RuntimeStepPhase.Completed);
                session = CommitHistoryBatch(session, history, [CreateAssistantMessage(step.Output)]);
                return (session, RuntimeCheckpointKind.StepCommitted);
            }
            case RuntimeCheckpointKind.StepCommitted:
                if (turn.Steps.LastOrDefault()?.Phase != RuntimeStepPhase.Completed)
                {
                    throw ResumeCorrupt(
                        "checkpoint_committed_step_invalid",
                        "A StepCommitted checkpoint must contain a completed Step.");
                }
                return (session, checkpoint.Kind);
            case RuntimeCheckpointKind.Terminal:
                throw new RuntimeResumeException(new RuntimeError(
                    RuntimeErrorCategory.RuntimeInvariantViolation,
                    "checkpoint_already_terminal",
                    "A terminal checkpoint cannot be resumed."));
            default:
                throw ResumeCorrupt(
                    "checkpoint_kind_invalid",
                    "The checkpoint kind is not supported.");
        }
    }

    private static RuntimeResumeException ResumeCorrupt(string code, string message)
        => new(new RuntimeError(RuntimeErrorCategory.TraceCorrupt, code, message));

    private static RuntimeAgentLoopRequest SnapshotRequest(RuntimeAgentLoopRequest request)
        => request with
        {
            InitialMessages = Array.AsReadOnly(
                request.InitialMessages.Select(SnapshotMessage).ToArray()),
            Tools = Array.AsReadOnly(request.Tools
                .Select(static tool => tool with { InputSchema = tool.InputSchema.Clone() })
                .ToArray())
        };

    private sealed record ToolBatchExecution(
        RuntimeSessionState Session,
        IReadOnlyList<RuntimeToolResult> Results,
        RuntimeError? FatalError);

    private sealed record PostStepDecision(RuntimeSessionState Session, bool Complete);

    private sealed class RuntimeCheckpointEmitter(
        IRuntimeCheckpointSink? sink,
        RuntimeCheckpointFailureMode failureMode,
        RuntimeAgentLoopRequest request,
        RuntimeRunAttempt attempt,
        TimeProvider timeProvider)
    {
        private readonly List<RuntimeWarning> _warnings = [];
        private long _sequence;

        public RuntimeRunAttempt Attempt => attempt;

        public IReadOnlyList<RuntimeWarning> Warnings => Array.AsReadOnly(_warnings.ToArray());

        public async ValueTask SaveAsync(
            RuntimeCheckpointKind kind,
            RuntimeSessionState session,
            RuntimeHistory history,
            string finalText,
            CancellationToken ct)
        {
            if (sink == null)
            {
                return;
            }
            var historySnapshot = history.Snapshot();
            var checkpoint = RuntimeCheckpointDocument.Capture(
                Interlocked.Increment(ref _sequence),
                attempt,
                kind,
                request,
                session,
                historySnapshot,
                finalText,
                timeProvider.GetUtcNow());
            try
            {
                await sink.SaveAsync(checkpoint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var error = new RuntimeError(
                    RuntimeErrorCategory.TraceCorrupt,
                    "checkpoint_write_failed",
                    $"The H1 checkpoint could not be durably committed: {ex.Message}");
                if (failureMode == RuntimeCheckpointFailureMode.FailClosed)
                {
                    throw new RuntimeCheckpointWriteFailure(error, ex);
                }
                _warnings.Add(new RuntimeWarning(error.Code, error.Message));
            }
        }

        public async ValueTask TrySaveTerminalBestEffortAsync(
            RuntimeSessionState session,
            RuntimeHistory history,
            string finalText)
        {
            if (sink == null)
            {
                return;
            }
            try
            {
                var historySnapshot = history.Snapshot();
                var checkpoint = RuntimeCheckpointDocument.Capture(
                    Interlocked.Increment(ref _sequence),
                    attempt,
                    RuntimeCheckpointKind.Terminal,
                    request,
                    session,
                    historySnapshot,
                    finalText,
                    timeProvider.GetUtcNow());
                await sink.SaveAsync(checkpoint, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The primary terminal/audit error remains authoritative.
            }
        }
    }

    private sealed class RuntimeCheckpointWriteFailure(RuntimeError error, Exception innerException)
        : Exception(error.Message, innerException)
    {
        public RuntimeError Error { get; } = error;
    }

    private sealed class RuntimeAuditEmitter(
        IRuntimeAuditSink? sink,
        RuntimeAuditFailureMode failureMode,
        RuntimeSessionId sessionId,
        RuntimeTurnId turnId,
        TimeProvider timeProvider)
    {
        private readonly List<RuntimeAuditEnvelope> _events = [];
        private readonly List<RuntimeWarning> _warnings = [];
        private RuntimeAuditEventId? _previous;
        private long _sequence;
        private bool _eventLimitReported;

        public IReadOnlyList<RuntimeAuditEnvelope> Events => Array.AsReadOnly(_events.ToArray());

        public IReadOnlyList<RuntimeWarning> Warnings => Array.AsReadOnly(_warnings.ToArray());

        public async ValueTask EmitAsync(
            RuntimeAuditEventKind kind,
            RuntimeAuditSensitivity sensitivity,
            RuntimeAuditPayload payload,
            CancellationToken ct,
            RuntimeStepId? stepId = null,
            RuntimeInvocationId? invocationId = null)
        {
            if (sink == null)
            {
                return;
            }
            if (_events.Count >= RuntimeAuditSchema.MaxEventsPerTurn)
            {
                var error = new RuntimeError(
                    RuntimeErrorCategory.ResourceExhausted,
                    "audit_event_budget_exhausted",
                    $"The C6 audit exceeded the {RuntimeAuditSchema.MaxEventsPerTurn} event memory quota.");
                if (failureMode == RuntimeAuditFailureMode.FailClosed)
                {
                    throw new RuntimeAuditWriteFailure(error, new InvalidDataException(error.Message));
                }
                if (!_eventLimitReported)
                {
                    _warnings.Add(new RuntimeWarning(error.Code, error.Message));
                    _eventLimitReported = true;
                }
                return;
            }
            var sequence = checked(++_sequence);
            var eventId = new RuntimeAuditEventId($"{turnId.Value}:audit:{sequence}");
            var envelope = new RuntimeAuditEnvelope(
                RuntimeAuditSchema.CurrentVersion,
                sequence,
                eventId,
                timeProvider.GetUtcNow(),
                kind,
                sessionId,
                turnId,
                stepId,
                invocationId,
                _previous,
                turnId.Value,
                sensitivity,
                payload);
            _events.Add(envelope);
            _previous = eventId;
            try
            {
                await sink.OnEventAsync(envelope, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var error = new RuntimeError(
                    RuntimeErrorCategory.TraceCorrupt,
                    "audit_write_failed",
                    $"The C6 durable audit sink failed: {ex.Message}");
                if (failureMode == RuntimeAuditFailureMode.FailClosed)
                {
                    throw new RuntimeAuditWriteFailure(error, ex);
                }
                _warnings.Add(new RuntimeWarning(error.Code, error.Message));
            }
        }
    }

    private sealed class RuntimeAuditWriteFailure(RuntimeError error, Exception innerException)
        : Exception(error.Message, innerException)
    {
        public RuntimeError Error { get; } = error;
    }

    private sealed class RuntimeAgentLoopFailure(
        RuntimeTerminationReason terminationReason,
        RuntimeError error,
        RuntimeSessionState? session = null) : Exception(error.Message)
    {
        public RuntimeTerminationReason TerminationReason { get; } = terminationReason;

        public RuntimeError Error { get; } = error;

        public RuntimeSessionState? Session { get; } = session;
    }

    private sealed class RuntimeAgentLoopCancelled(RuntimeSessionState? session = null)
        : OperationCanceledException
    {
        public RuntimeSessionState? Session { get; } = session;
    }
}
