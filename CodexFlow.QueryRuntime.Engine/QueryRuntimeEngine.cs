using Microsoft.Extensions.AI;
using HostContracts = CodexFlow.QueryRuntime.Abstractions;

namespace CodexFlow.QueryRuntime.Engine;

public sealed class QueryRuntimeEngine : IQueryRuntimeEngine
{
    private readonly IQueryRuntimeModelClient _modelClient;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _queryIdFactory;

    public QueryRuntimeEngine(IQueryRuntimeModelClient modelClient)
        : this(modelClient, timeProvider: null, queryIdFactory: null)
    {
    }

    /// <summary>
    /// Creates an engine with injectable clock and query-id sources. Both default to
    /// non-deterministic system sources; deterministic replay seeds them from a source
    /// trace so repeated replays produce a byte-identical canonical event projection.
    /// </summary>
    public QueryRuntimeEngine(
        IQueryRuntimeModelClient modelClient,
        TimeProvider? timeProvider,
        Func<Guid>? queryIdFactory)
    {
        _modelClient = modelClient ?? throw new ArgumentNullException(nameof(modelClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _queryIdFactory = queryIdFactory ?? Guid.NewGuid;
    }

    public async Task<QueryRuntimeResult> ExecuteAsync(
        QueryRuntimeRequest request,
        IQueryRuntimeEventSink eventSink,
        string runId,
        string traceFilePath,
        string? workspacePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(traceFilePath);

        var queryId = _queryIdFactory();
        var startTimestamp = _timeProvider.GetTimestamp();
        var messages = request.InitialMessages.ToList();
        var maxRounds = Math.Max(1, request.MaxRounds);
        var maxContinuations = Math.Max(0, request.MaxStopGateContinuations);
        var totalToolCalls = 0;
        var finalText = string.Empty;
        var terminationReason = QueryTerminationReason.MaxRounds;
        string? terminalDetailCode = null;
        var seq = 0L;
        var requiredToolSatisfied = false;
        var completedRounds = 0;
        var zeroToolCallRounds = 0;
        var continuationCount = 0;
        var writeToolCalls = 0;
        string? activeRequiredToolName = request.RequiredToolName;
        string? lastFunctionCall = null;
        var executedToolNames = new List<string>();
        var successfulToolNames = new List<string>();
        var toolResultSummaries = new List<string>();

        try
        {
            for (var round = 0; round < maxRounds; round++)
            {
                var currentTools = ResolveTools(request, round, requiredToolSatisfied);
                var currentToolNames = currentTools.Select(static tool => tool.Name).ToArray();
                await EmitAsync(
                    eventSink,
                    QueryRuntimeEventType.RoundStarted,
                    new RoundStartedEvent(++seq, queryId, request.SessionId, Now(), round, maxRounds)).ConfigureAwait(false);

                var options = PrepareOptions(request, currentTools, activeRequiredToolName, requiredToolSatisfied);
                await EmitAsync(
                    eventSink,
                    QueryRuntimeEventType.PromptAssemblySnapshot,
                    new PromptAssemblySnapshotEvent(
                        ++seq,
                        queryId,
                        request.SessionId,
                        Now(),
                        round,
                        messages.Count,
                        request.EnableTools,
                        currentToolNames,
                        activeRequiredToolName,
                        requiredToolSatisfied)).ConfigureAwait(false);

                var textParts = new List<string>();
                var functionCalls = new List<FunctionCallContent>();
                await foreach (var update in _modelClient.StreamAsync(
                                   new QueryRuntimeModelRequest(messages.ToArray(), options, runId, workspacePath),
                                   ct).ConfigureAwait(false))
                {
                    foreach (var content in update.Contents)
                    {
                        switch (content)
                        {
                            case TextContent textContent:
                                textParts.Add(textContent.Text);
                                if (!string.IsNullOrEmpty(textContent.Text) && request.TextDeltaSink != null)
                                {
                                    await request.TextDeltaSink(textContent.Text, ct).ConfigureAwait(false);
                                }
                                break;
                            case FunctionCallContent functionCall:
                                functionCalls.Add(functionCall);
                                break;
                        }
                    }
                }

                var assistantText = string.Concat(textParts);
                await EmitAsync(
                    eventSink,
                    QueryRuntimeEventType.ModelResponseSampled,
                    new ModelResponseSampledEvent(
                        ++seq,
                        queryId,
                        request.SessionId,
                        Now(),
                        round,
                        assistantText.Length,
                        functionCalls.Count,
                        assistantText,
                        functionCalls
                            .Select(CreateFunctionCallSnapshot)
                            .ToArray())).ConfigureAwait(false);

                if (functionCalls.Count == 0 || !request.EnableTools)
                {
                    zeroToolCallRounds++;
                    completedRounds = round + 1;
                    var stopDecision = HostContracts.QueryRuntimeStopDecision.Accept();
                    if (request.StopGate != null)
                    {
                        var canContinue = round + 1 < maxRounds && continuationCount < maxContinuations;
                        try
                        {
                            stopDecision = await request.StopGate.BeforeStopAsync(
                                new HostContracts.QueryRuntimeBeforeStopContext(
                                    runId,
                                    request.SessionId,
                                    workspacePath,
                                    round,
                                    maxRounds,
                                    assistantText,
                                    executedToolNames.ToArray(),
                                    successfulToolNames.ToArray(),
                                    toolResultSummaries.ToArray(),
                                    totalToolCalls,
                                    zeroToolCallRounds,
                                    continuationCount,
                                    maxContinuations,
                                    activeRequiredToolName,
                                    requiredToolSatisfied,
                                    canContinue,
                                    messages.ToArray()),
                                ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            stopDecision = HostContracts.QueryRuntimeStopDecision.FailClosed(
                                ex.Message,
                                "stop_gate_failed");
                        }

                        await EmitStopGateDecisionAsync(
                            eventSink,
                            ++seq,
                            queryId,
                            request,
                            round,
                            stopDecision,
                            continuationCount).ConfigureAwait(false);
                    }

                    if (stopDecision.Kind == HostContracts.QueryRuntimeStopDecisionKind.Accept)
                    {
                        AddAssistantMessageIfNotEmpty(messages, assistantText);
                        finalText = assistantText;
                        terminationReason = QueryTerminationReason.NoToolCalls;
                        await EmitRoundCompletedAsync(eventSink, ++seq, queryId, request, round, 0, assistantText, null).ConfigureAwait(false);
                        break;
                    }

                    if (stopDecision.Kind == HostContracts.QueryRuntimeStopDecisionKind.FailClosed)
                    {
                        AddAssistantMessageIfNotEmpty(messages, assistantText);
                        finalText = assistantText;
                        terminationReason = QueryTerminationReason.FailClosed;
                        terminalDetailCode = stopDecision.DetailCode ?? "stop_gate_failed_closed";
                        await EmitRoundCompletedAsync(eventSink, ++seq, queryId, request, round, 0, assistantText, "stop_gate_failed_closed").ConfigureAwait(false);
                        break;
                    }

                    var hasContinuationBudget = round + 1 < maxRounds && continuationCount < maxContinuations;
                    if (!hasContinuationBudget)
                    {
                        AddAssistantMessageIfNotEmpty(messages, assistantText);
                        finalText = assistantText;
                        terminationReason = QueryTerminationReason.FailClosed;
                        terminalDetailCode = stopDecision.DetailCode ??
                            (round + 1 >= maxRounds ? "verification_timed_out" : "verification_incomplete");
                        await EmitRoundCompletedAsync(eventSink, ++seq, queryId, request, round, 0, assistantText, terminalDetailCode).ConfigureAwait(false);
                        break;
                    }

                    var feedback = string.IsNullOrWhiteSpace(stopDecision.Feedback)
                        ? "The host verification gate requires another round before this answer can be accepted."
                        : stopDecision.Feedback!;
                    var stopGateAssistantContents = string.IsNullOrEmpty(assistantText)
                        ? new List<AIContent>()
                        : new List<AIContent> { new TextContent(assistantText) };
                    if (stopGateAssistantContents.Count > 0)
                    {
                        messages.Add(new ChatMessage(ChatRole.Assistant, stopGateAssistantContents));
                    }

                    messages.Add(new ChatMessage(ChatRole.User, feedback));
                    continuationCount++;
                    if (stopDecision.Kind == HostContracts.QueryRuntimeStopDecisionKind.RequireTool)
                    {
                        activeRequiredToolName = stopDecision.RequiredToolName;
                        requiredToolSatisfied = false;
                    }

                    await EmitRoundCompletedAsync(eventSink, ++seq, queryId, request, round, 0, assistantText, stopDecision.Kind.ToString()).ConfigureAwait(false);
                    continue;
                }

                var assistantContents = new List<AIContent>();
                if (!string.IsNullOrEmpty(assistantText))
                {
                    assistantContents.Add(new TextContent(assistantText));
                }

                assistantContents.AddRange(functionCalls);
                messages.Add(new ChatMessage(ChatRole.Assistant, assistantContents));
                var toolMessages = new List<AIContent>();
                var terminateRun = false;
                foreach (var functionCall in functionCalls)
                {
                    var arguments = functionCall.Arguments == null
                        ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, object?>(functionCall.Arguments, StringComparer.OrdinalIgnoreCase);
                    lastFunctionCall = functionCall.Name;
                    var tool = currentTools.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, functionCall.Name, StringComparison.OrdinalIgnoreCase));
                    if (tool == null)
                    {
                        var missingToolResult = $"Tool '{functionCall.Name}' is not currently available. Use tool_search first if the tool is deferred, or choose one of the currently declared tools.";
                        toolMessages.Add(new FunctionResultContent(functionCall.CallId, missingToolResult));
                        await EmitAsync(
                            eventSink,
                            QueryRuntimeEventType.ToolExecutionCompleted,
                            new ToolExecutionCompletedEvent(
                                ++seq,
                                queryId,
                                request.SessionId,
                                Now(),
                                round,
                                functionCall.Name,
                                functionCall.CallId,
                                false,
                                missingToolResult.Length,
                                missingToolResult)).ConfigureAwait(false);
                        continue;
                    }

                    await EmitAsync(
                        eventSink,
                        QueryRuntimeEventType.ToolCallRequested,
                        new ToolCallRequestedEvent(
                            ++seq,
                            queryId,
                            request.SessionId,
                            Now(),
                            round,
                            tool.Name,
                            functionCall.CallId,
                            arguments)).ConfigureAwait(false);

                    if (request.ToolIntervention != null)
                    {
                        HostContracts.QueryRuntimeToolInterventionDecision decision;
                        try
                        {
                            decision = await request.ToolIntervention.BeforeToolCallAsync(
                                new HostContracts.QueryRuntimeToolCallContext(
                                    runId,
                                    request.SessionId,
                                    workspacePath,
                                    round,
                                    tool.Name,
                                    functionCall.CallId,
                                    arguments,
                                    currentToolNames,
                                    activeRequiredToolName,
                                    messages.ToArray()),
                                ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            decision = HostContracts.QueryRuntimeToolInterventionDecision.FailClosed(
                                ex.Message,
                                "tool_intervention_pre_failed");
                        }

                        await EmitPolicyInterventionDecisionAsync(
                            eventSink,
                            ++seq,
                            queryId,
                            request,
                            round,
                            tool.Name,
                            functionCall.CallId,
                            decision).ConfigureAwait(false);

                        if (decision.Kind == HostContracts.QueryRuntimeToolInterventionDecisionKind.BlockWithFeedback)
                        {
                            var feedback = string.IsNullOrWhiteSpace(decision.Feedback)
                                ? $"Tool '{tool.Name}' was blocked by host policy."
                                : decision.Feedback!;
                            toolMessages.Add(new FunctionResultContent(functionCall.CallId, feedback));
                            await EmitAsync(
                                eventSink,
                                QueryRuntimeEventType.ToolExecutionCompleted,
                                new ToolExecutionCompletedEvent(
                                    ++seq,
                                    queryId,
                                    request.SessionId,
                                    Now(),
                                    round,
                                    tool.Name,
                                    functionCall.CallId,
                                    false,
                                    feedback.Length,
                                    feedback)).ConfigureAwait(false);
                            continue;
                        }

                        if (decision.Kind == HostContracts.QueryRuntimeToolInterventionDecisionKind.FailClosed)
                        {
                            finalText = assistantText;
                            terminationReason = QueryTerminationReason.FailClosed;
                            terminalDetailCode = decision.DetailCode ?? "tool_intervention_failed_closed";
                            terminateRun = true;
                            break;
                        }
                    }

                    await EmitAsync(
                        eventSink,
                        QueryRuntimeEventType.ToolExecutionStarted,
                        new ToolExecutionStartedEvent(++seq, queryId, request.SessionId, Now(), round, tool.Name, functionCall.CallId)).ConfigureAwait(false);

                    string resultText;
                    Exception? toolException = null;
                    totalToolCalls++;
                    executedToolNames.Add(tool.Name);
                    if (IsWriteTool(tool.Name, request.WriteToolNames))
                    {
                        writeToolCalls++;
                    }

                    try
                    {
                        var result = await tool.InvokeAsync(new AIFunctionArguments(arguments), ct).ConfigureAwait(false);
                        resultText = result?.ToString() ?? string.Empty;
                        successfulToolNames.Add(tool.Name);

                        if (string.Equals(tool.Name, activeRequiredToolName, StringComparison.OrdinalIgnoreCase))
                        {
                            requiredToolSatisfied = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        toolException = ex;
                        resultText = ex.Message;
                    }

                    toolMessages.Add(new FunctionResultContent(functionCall.CallId, resultText));
                    var resultSummary = Summarize(resultText);
                    toolResultSummaries.Add($"{tool.Name}: {resultSummary}");
                    await EmitAsync(
                        eventSink,
                        QueryRuntimeEventType.ToolExecutionCompleted,
                            new ToolExecutionCompletedEvent(
                                ++seq,
                                queryId,
                                request.SessionId,
                                Now(),
                                round,
                                tool.Name,
                                functionCall.CallId,
                                toolException == null,
                                resultText.Length,
                                resultText)).ConfigureAwait(false);

                    if (request.ToolIntervention != null)
                    {
                        try
                        {
                            await request.ToolIntervention.AfterToolExecutionAsync(
                                new HostContracts.QueryRuntimeToolExecutionResultContext(
                                    runId,
                                    request.SessionId,
                                    workspacePath,
                                    round,
                                    tool.Name,
                                    functionCall.CallId,
                                    toolException == null,
                                    resultText.Length,
                                    resultSummary,
                                    toolException?.GetType().Name,
                                    toolException?.Message),
                                ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            finalText = assistantText;
                            terminationReason = QueryTerminationReason.FailClosed;
                            terminalDetailCode = "tool_intervention_after_failed";
                            terminateRun = true;
                            await EmitAsync(
                                eventSink,
                                QueryRuntimeEventType.Error,
                                new ErrorEvent(
                                    ++seq,
                                    queryId,
                                    request.SessionId,
                                    Now(),
                                    ex.GetType().Name,
                                    ex.Message,
                                    ex)).ConfigureAwait(false);
                        }
                    }

                    if (terminateRun)
                    {
                        break;
                    }
                }

                if (toolMessages.Count > 0)
                {
                    messages.Add(new ChatMessage(ChatRole.Tool, toolMessages));
                }
                completedRounds = round + 1;
                await EmitRoundCompletedAsync(
                    eventSink,
                    ++seq,
                    queryId,
                    request,
                    round,
                    functionCalls.Count,
                    assistantText,
                    terminateRun ? terminalDetailCode : "tool_calls").ConfigureAwait(false);
                if (terminateRun)
                {
                    break;
                }
            }

            var elapsedMs = (long)_timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds;
            await EmitAsync(
                eventSink,
                QueryRuntimeEventType.Terminated,
                new TerminatedEvent(
                    ++seq,
                    queryId,
                    request.SessionId,
                    Now(),
                    terminationReason,
                    completedRounds,
                    totalToolCalls,
                    elapsedMs,
                    terminalDetailCode,
                    zeroToolCallRounds,
                    continuationCount,
                    writeToolCalls,
                    lastFunctionCall,
                    activeRequiredToolName,
                    requiredToolSatisfied)).ConfigureAwait(false);

            var runDirectory = Path.GetDirectoryName(traceFilePath);
            return new QueryRuntimeResult(
                runId,
                request.SessionId,
                traceFilePath,
                finalText,
                terminationReason,
                completedRounds,
                totalToolCalls,
                elapsedMs)
            {
                TerminalDetailCode = terminalDetailCode,
                ZeroToolCallRounds = zeroToolCallRounds,
                ContinuationCount = continuationCount,
                LastFunctionCall = lastFunctionCall,
                WriteToolCalls = writeToolCalls,
                RunDirectory = runDirectory,
                RequiredToolName = activeRequiredToolName,
                RequiredToolSatisfied = requiredToolSatisfied,
                ExecutedToolNames = executedToolNames.ToArray(),
                SuccessfulToolNames = successfulToolNames.ToArray(),
                FinalMessages = messages.ToArray()
            };
        }
        catch (Exception ex)
        {
            await EmitAsync(
                eventSink,
                QueryRuntimeEventType.Error,
                new ErrorEvent(++seq, queryId, request.SessionId, Now(), ex.GetType().Name, ex.Message, ex)).ConfigureAwait(false);
            throw;
        }
    }

    private static IReadOnlyList<AIFunction> ResolveTools(
        QueryRuntimeRequest request,
        int round,
        bool requiredToolSatisfied)
        => request.ToolProvider?.Invoke(new QueryRuntimeToolResolutionContext(round, requiredToolSatisfied)) ??
           request.AvailableTools;

    private static ChatOptions PrepareOptions(
        QueryRuntimeRequest request,
        IReadOnlyList<AIFunction> tools,
        string? requiredToolName,
        bool requiredToolSatisfied)
    {
        var options = CreateRuntimeOptions(request.Options, request.OptionsCloneFactory);
        if (request.EnableTools && tools.Count > 0)
        {
            options.Tools = tools.Cast<AITool>().ToList();
            var normalizedRequiredToolName = requiredToolName?.Trim();
            var requiredToolAvailable = !string.IsNullOrWhiteSpace(normalizedRequiredToolName) &&
                tools.Any(tool => string.Equals(tool.Name, normalizedRequiredToolName, StringComparison.OrdinalIgnoreCase));
            if (!requiredToolSatisfied && requiredToolAvailable)
            {
                options.ToolMode = ChatToolMode.RequireSpecific(normalizedRequiredToolName!);
            }
            else
            {
                options.ToolMode = null;
            }
        }
        else
        {
            options.Tools = [];
            options.ToolMode = ChatToolMode.None;
        }

        return options;
    }

    private static ChatOptions CreateRuntimeOptions(
        ChatOptions? options,
        Func<ChatOptions, ChatOptions>? optionsCloneFactory)
    {
        if (options == null)
        {
            return new ChatOptions();
        }

        return optionsCloneFactory?.Invoke(options) ?? options.Clone();
    }

    private static QueryRuntimeFunctionCallSnapshot CreateFunctionCallSnapshot(FunctionCallContent functionCall)
        => new(
            functionCall.CallId,
            functionCall.Name,
            functionCall.Arguments == null
                ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object?>(functionCall.Arguments, StringComparer.OrdinalIgnoreCase));

    private static string Summarize(string value)
    {
        const int maxLength = 512;
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private static void AddAssistantMessageIfNotEmpty(List<ChatMessage> messages, string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, [new TextContent(text)]));
        }
    }

    private static bool IsWriteTool(string toolName, IReadOnlySet<string> writeToolNames)
    {
        if (writeToolNames.Contains(toolName))
        {
            return true;
        }

        var tokens = toolName.Split(['_', '-', '.', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Any(static token => token.Equals("write", StringComparison.OrdinalIgnoreCase) ||
                                          token.Equals("edit", StringComparison.OrdinalIgnoreCase) ||
                                          token.Equals("patch", StringComparison.OrdinalIgnoreCase) ||
                                          token.Equals("apply", StringComparison.OrdinalIgnoreCase) ||
                                          token.Equals("replace", StringComparison.OrdinalIgnoreCase) ||
                                          token.Equals("delete", StringComparison.OrdinalIgnoreCase) ||
                                          token.Equals("remove", StringComparison.OrdinalIgnoreCase) ||
                                          token.Equals("move", StringComparison.OrdinalIgnoreCase) ||
                                          token.Equals("rename", StringComparison.OrdinalIgnoreCase));
    }

    private async ValueTask EmitPolicyInterventionDecisionAsync(
        IQueryRuntimeEventSink eventSink,
        long seq,
        Guid queryId,
        QueryRuntimeRequest request,
        int round,
        string toolName,
        string callId,
        HostContracts.QueryRuntimeToolInterventionDecision decision)
        => await EmitAsync(
            eventSink,
            QueryRuntimeEventType.PolicyInterventionDecision,
            new PolicyInterventionDecisionEvent(
                seq,
                queryId,
                request.SessionId,
                Now(),
                round,
                toolName,
                callId,
                decision.Kind.ToString(),
                decision.Reason,
                decision.DetailCode,
                decision.Feedback)).ConfigureAwait(false);

    private async ValueTask EmitStopGateDecisionAsync(
        IQueryRuntimeEventSink eventSink,
        long seq,
        Guid queryId,
        QueryRuntimeRequest request,
        int round,
        HostContracts.QueryRuntimeStopDecision decision,
        int continuationCount)
        => await EmitAsync(
            eventSink,
            QueryRuntimeEventType.StopGateDecision,
            new StopGateDecisionEvent(
                seq,
                queryId,
                request.SessionId,
                Now(),
                round,
                decision.Kind.ToString(),
                decision.RequiredToolName,
                decision.Reason,
                decision.DetailCode,
                decision.Feedback,
                continuationCount)).ConfigureAwait(false);

    private async ValueTask EmitRoundCompletedAsync(
        IQueryRuntimeEventSink eventSink,
        long seq,
        Guid queryId,
        QueryRuntimeRequest request,
        int round,
        int toolCallCount,
        string text,
        string? continueReason)
        => await EmitAsync(
            eventSink,
            QueryRuntimeEventType.RoundCompleted,
            new RoundCompletedEvent(
                seq,
                queryId,
                request.SessionId,
                Now(),
                round,
                toolCallCount,
                !string.IsNullOrWhiteSpace(text),
                text.Length,
                continueReason)).ConfigureAwait(false);

    private static async ValueTask EmitAsync(
        IQueryRuntimeEventSink sink,
        QueryRuntimeEventType eventType,
        QueryRuntimeEvent runtimeEvent)
    {
        if (sink.IsEnabled(eventType))
        {
            await sink.OnEventAsync(runtimeEvent).ConfigureAwait(false);
        }
    }

    private DateTimeOffset Now() => _timeProvider.GetUtcNow();
}
