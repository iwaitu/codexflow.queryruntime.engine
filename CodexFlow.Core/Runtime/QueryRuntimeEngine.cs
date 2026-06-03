using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Agents;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Models;
using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Phase 1: Query Runtime Engine 默认实现
/// </summary>
/// <remarks>
/// 当前状态：
/// - 核心循环、事件发射、telemetry 已实现
/// - Recovery policy 已注入但执行路径待 Phase 2 接入
/// - Context governance 待 Phase 5 实现
/// </remarks>
public sealed class QueryRuntimeEngine : IQueryRuntimeEngine
{
    private const string PromptSnapshotQueryIdOptionKey = "__codexflow_prompt_snapshot_query_id";
    private const string PromptSnapshotSessionIdOptionKey = "__codexflow_prompt_snapshot_session_id";
    private const string PromptSnapshotRoundOptionKey = "__codexflow_prompt_snapshot_round";
    private const int ToolResultTranscriptMaxChars = 1800;
    private const int ReadToolResultTranscriptMaxChars = 16_000;
    private const int HashlineToolResultTranscriptMaxChars = 32_000;
    private const int ToolSummaryMaxChars = 220;
    private const int ReadToolSummaryMaxChars = 4_000;
    private const int HashlineToolSummaryMaxChars = 8_000;
    private const int ToolArgumentValueMaxChars = 80;
    private const int RecoverySummaryExcerptMaxChars = 1200;
    private const int WriteRecoveryContextMaxChars = 3200;
    private const int DefaultWrapUpToolContinuationLimit = 2;
    private const int DefaultStopHookContinuationLimit = 2;
    private const int MaxDynamicRequiredToolAttempts = 3;
    private const int ContextCompactionCircuitBreakerThreshold = 2;
    private static readonly char[] InlineWhitespaceSeparators = ['\r', '\n', '\t'];

    private readonly ILLMExecutor _llmExecutor;
    private readonly IContextWindowManager? _contextWindowManager;
    private readonly IToolExecutionCoordinator? _toolCoordinator;
    // Phase 2: Recovery policy will be integrated
    private readonly IQueryRecoveryPolicy? _recoveryPolicy;
    private readonly IQueryLoopTelemetry? _telemetry;
    private readonly ILogger<QueryRuntimeEngine> _logger;
    private readonly IRuntimeHookDispatcher? _runtimeHookDispatcher;
    private readonly IStreamingToolExecutionPlanner _streamingToolExecutionPlanner;
    private readonly StreamingToolExecutionOptions _streamingToolExecutionOptions;
    private readonly IQueryContextAssembler _contextAssembler;
    private readonly IToolPlanValidator _toolPlanValidator;
    private readonly IToolArgumentNormalizer _toolArgumentNormalizer;
    private readonly IToolPlanExecutor _toolPlanExecutor;
    private readonly IToolObservationProcessor _toolObservationProcessor;
    private readonly IRecoveryDecisionApplier _recoveryDecisionApplier;
    private readonly object _contextCompactionCircuitSync = new();
    private readonly Dictionary<string, int> _contextCompactionFailureCounts = new(StringComparer.Ordinal);

    public QueryRuntimeEngine(
        ILLMExecutor llmExecutor,
        IContextWindowManager? contextWindowManager,
        IToolExecutionCoordinator? toolCoordinator,
        IQueryRecoveryPolicy? recoveryPolicy,
        IQueryLoopTelemetry? telemetry,
        ILogger<QueryRuntimeEngine> logger,
        IRuntimeHookDispatcher? runtimeHookDispatcher = null,
        IStreamingToolExecutionPlanner? streamingToolExecutionPlanner = null,
        IOptions<StreamingToolExecutionOptions>? streamingToolExecutionOptions = null,
        IQueryContextAssembler? contextAssembler = null,
        IToolPlanValidator? toolPlanValidator = null,
        IToolArgumentNormalizer? toolArgumentNormalizer = null,
        IToolPlanExecutor? toolPlanExecutor = null,
        IToolObservationProcessor? toolObservationProcessor = null,
        IRecoveryDecisionApplier? recoveryDecisionApplier = null)
    {
        _llmExecutor = llmExecutor ?? throw new ArgumentNullException(nameof(llmExecutor));
        _contextWindowManager = contextWindowManager;
        _toolCoordinator = toolCoordinator;
        _recoveryPolicy = recoveryPolicy;
        _telemetry = telemetry;
        _logger = logger;
        _runtimeHookDispatcher = runtimeHookDispatcher;
        _streamingToolExecutionOptions = streamingToolExecutionOptions?.Value ?? new StreamingToolExecutionOptions();
        _streamingToolExecutionPlanner = streamingToolExecutionPlanner
            ?? new DefaultStreamingToolExecutionPlanner(Options.Create(_streamingToolExecutionOptions));
        _contextAssembler = contextAssembler ?? DefaultQueryContextAssembler.Instance;
        _toolPlanValidator = toolPlanValidator ?? new DefaultToolPlanValidator(
            _toolCoordinator,
            NullLogger<DefaultToolPlanValidator>.Instance);
        _toolArgumentNormalizer = toolArgumentNormalizer ?? DefaultToolArgumentNormalizer.Instance;
        _toolPlanExecutor = toolPlanExecutor ?? new DefaultToolPlanExecutor(
            _toolCoordinator,
            ExecuteToolCallAsync);
        _toolObservationProcessor = toolObservationProcessor ?? DefaultToolObservationProcessor.Instance;
        _recoveryDecisionApplier = recoveryDecisionApplier ?? new DefaultRecoveryDecisionApplier(
            _recoveryPolicy,
            _telemetry);
    }

    /// <inheritdoc/>
    public async Task<QueryRuntimeResult> ExecuteAsync(
        QueryRuntimeRequest request,
        IQueryRuntimeEventSink eventSink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);

        var state = InitializeState(request);
        var queryId = Guid.NewGuid();

        // Telemetry: RecordStart
        _telemetry?.RecordStart(new QueryLoopStarted(
            queryId,
            request.SessionId,
            request.EntryPoint,
            DateTimeOffset.UtcNow,
            request.MaxRounds,
            state.Messages.Sum(m => m.Text?.Length ?? 0)));

        _logger.LogDebug(
            "QueryRuntimeEngine[{EntryPoint}]: Starting query {QueryId} with {MaxRounds} max rounds",
            request.EntryPoint,
            queryId,
            request.MaxRounds);

        try
        {
            await PersistConversationCapturePreflightAsync(request, ct).ConfigureAwait(false);

            // Emit RoundStarted for round 0
            await EmitEventAsync(eventSink, new RoundStartedEvent(
                Seq: 0,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                Round: 0,
                MaxRounds: state.MaxRounds,
                ContextChars: state.Messages.Sum(m => m.Text?.Length ?? 0)));

            // Main loop
            while (state.Round < state.MaxRounds && !ct.IsCancellationRequested)
            {
                RoundResult roundResult;
                try
                {
                    // Execute one round
                    roundResult = await ExecuteRoundAsync(request, state, eventSink, queryId, ct);
                    state.TransportFailureCount = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    var transportRecovery = await TryHandleTransientTransportFailureAsync(
                        ex,
                        request,
                        state,
                        eventSink,
                        queryId,
                        ct).ConfigureAwait(false);

                    if (transportRecovery == TransportRecoveryOutcome.Continue)
                    {
                        continue;
                    }

                    if (transportRecovery == TransportRecoveryOutcome.Terminate)
                    {
                        break;
                    }

                    throw;
                }

                // Check termination conditions
                if (roundResult.ShouldTerminate && roundResult.TerminationReason.HasValue)
                {
                    state.TerminationReason = roundResult.TerminationReason.Value;
                    break;
                }

                state.Round++;

                // Emit RoundStarted for next round
                if (state.Round < state.MaxRounds)
                {
                    await EmitEventAsync(eventSink, new RoundStartedEvent(
                        Seq: state.Round * 1000L,
                        QueryId: queryId,
                        SessionId: request.SessionId,
                        EntryPoint: request.EntryPoint,
                        Round: state.Round,
                        MaxRounds: state.MaxRounds,
                        ContextChars: state.Messages.Sum(m => m.Text?.Length ?? 0)));
                }
            }

            // Check if max rounds reached
            if (state.Round >= state.MaxRounds && state.TerminationReason == QueryTerminationReason.Normal)
            {
                state.TerminationReason = QueryTerminationReason.MaxRoundsReached;
            }

            FinalizeTerminalState(state);
            var result = BuildResult(state, queryId, request);
            await ApplyContextWindowGovernanceAsync(
                request,
                result,
                state,
                eventSink,
                queryId,
                ct).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            state.TerminationReason = QueryTerminationReason.Exception;
            _logger.LogInformation("QueryRuntimeEngine[{QueryId}]: Cancelled", queryId);
            FinalizeTerminalState(state);
            return BuildResult(state, queryId, request);
        }
        catch (Exception ex)
        {
            state.TerminationReason = QueryTerminationReason.Exception;
            _logger.LogError(ex, "QueryRuntimeEngine[{QueryId}]: Exception during execution", queryId);

            await EmitEventAsync(eventSink, new ErrorEvent(
                Seq: 999999,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                ErrorType: ex.GetType().Name,
                Message: ex.Message,
                Exception: ex));

            FinalizeTerminalState(state);
            return BuildResult(state, queryId, request);
        }
        finally
        {
            state.Stopwatch.Stop();
            FinalizeTerminalState(state);

            // Telemetry: RecordTermination
            _telemetry?.RecordTermination(new QueryLoopTerminated(
                queryId,
                request.SessionId,
                request.EntryPoint,
                state.TerminationReason,
                state.TerminalDetailCode,
                state.Round + 1,
                state.TotalToolCalls,
                state.ZeroToolCallRounds,
                state.MalformedProtocolCount,
                state.EmptyResponseCount,
                state.RecoveryCount,
                state.Stopwatch.ElapsedMilliseconds,
                state.TotalPromptTokens > 0 ? state.TotalPromptTokens : null,
                state.TotalCompletionTokens > 0 ? state.TotalCompletionTokens : null));

            // Emit Terminated
            await EmitEventAsync(eventSink, new TerminatedEvent(
                Seq: (state.Round + 1) * 1000L + 999,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                Reason: state.TerminationReason,
                TotalRounds: state.Round + 1,
                TotalToolCalls: state.TotalToolCalls,
                TotalDurationMs: state.Stopwatch.ElapsedMilliseconds,
                DetailCode: state.TerminalDetailCode));

            _logger.LogDebug(
                "QueryRuntimeEngine[{EntryPoint}]: Query {QueryId} terminated with {Reason} after {Rounds} rounds",
                request.EntryPoint,
                queryId,
                state.TerminationReason,
                state.Round + 1);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static QueryRuntimeState InitializeState(QueryRuntimeRequest request)
    {
        var state = new QueryRuntimeState
        {
            MaxRounds = request.MaxRounds,
            EnableToolDeduplication = request.AdapterHints?.EnableToolDeduplication ?? false
        };
        state.Messages.AddRange(request.InitialMessages);
        QueryRuntimeCheckpointBuilder.TryRestoreEvidenceLedgerFromSession(request, state);
        return state;
    }

    private async ValueTask<QueryRuntimeDynamicContextResult?> ResolveDynamicContextAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        bool allowToolCalls,
        string? requiredToolNameForRound,
        CancellationToken ct)
    {
        if (request.DynamicContextProvider == null)
        {
            return null;
        }

        try
        {
            return await request.DynamicContextProvider(
                new QueryRuntimeDynamicContextRequest(
                    request,
                    state,
                    state.Round,
                    request.EnableTools && allowToolCalls,
                    requiredToolNameForRound),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "QueryRuntimeEngine[{EntryPoint}] dynamic context provider failed for session {SessionId} on round {Round}; continuing without dynamic context.",
                request.EntryPoint,
                request.SessionId,
                state.Round);
            AppendRuntimeRecoveryHint(
                state,
                "dynamic_context_provider",
                state.RecoveryCount,
                requiredToolName: null,
                toolCallRequired: false,
                message: "动态上下文刷新失败，本轮将使用已装配上下文继续。");
            return null;
        }
    }

    private void ApplyDynamicRequiredTool(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        string requiredToolName,
        bool forceAllowToolCalls,
        ref bool allowToolCalls,
        ref string? requiredToolNameForRound,
        out bool suppressed)
    {
        suppressed = false;
        var required = requiredToolName.Trim();
        if (string.IsNullOrWhiteSpace(required))
        {
            ResetDynamicRequiredToolState(state);
            return;
        }

        if (!string.Equals(state.DynamicRequiredToolName, required, StringComparison.OrdinalIgnoreCase))
        {
            state.DynamicRequiredToolName = required;
            state.DynamicRequiredToolAttempts = 0;
        }

        if (ShouldSuppressDynamicRequiredTool(request, state))
        {
            suppressed = true;
            _logger.LogWarning(
                "QueryRuntimeEngine[{EntryPoint}] dynamic required tool '{Tool}' released for session {SessionId} on round {Round}. Attempts={Attempts}/{MaxAttempts}, RequestMaxRounds={RequestMaxRounds}, StateMaxRounds={StateMaxRounds}",
                request.EntryPoint,
                required,
                request.SessionId,
                state.Round,
                state.DynamicRequiredToolAttempts,
                MaxDynamicRequiredToolAttempts,
                request.MaxRounds,
                state.MaxRounds);

            if (string.Equals(requiredToolNameForRound, required, StringComparison.OrdinalIgnoreCase))
            {
                requiredToolNameForRound = null;
                state.NextRoundOptionOverrides = RemoveRuntimeOptionOverrides(
                    state.NextRoundOptionOverrides,
                    "ToolMode",
                    "ThinkingEnabled");
            }

            return;
        }

        state.DynamicRequiredToolAttempts++;
        requiredToolNameForRound = required;
        if (request.EnableTools && forceAllowToolCalls)
        {
            allowToolCalls = true;
        }
    }

    private static QueryRuntimeDynamicContextResult BuildSuppressedDynamicRequiredContext(string requiredToolName)
    {
        var required = string.IsNullOrWhiteSpace(requiredToolName)
            ? "dynamic required tool"
            : requiredToolName.Trim();
        return new QueryRuntimeDynamicContextResult
        {
            Messages =
            [
                new ChatMessage(
                    ChatRole.User,
                    $"[SYSTEM] 动态上下文此前要求调用 `{required}`，但该要求已达到连续强制上限或进入原始 MaxRounds 收尾轮。本轮不要继续强制该工具；如无其它必要工具调用，请基于已有证据收尾。")
            ]
        };
    }

    private static bool ShouldSuppressDynamicRequiredTool(
        QueryRuntimeRequest request,
        QueryRuntimeState state)
        => state.DynamicRequiredToolAttempts >= MaxDynamicRequiredToolAttempts ||
           state.Round >= Math.Max(0, request.MaxRounds - 1);

    private static void ResetDynamicRequiredToolState(QueryRuntimeState state)
    {
        state.DynamicRequiredToolName = null;
        state.DynamicRequiredToolAttempts = 0;
    }

    private async Task<RoundLlmRequestAssembly> AssembleLlmRequestForRoundAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        bool allowToolCalls,
        string? requiredToolNameForRound,
        string? pendingToolBatchSummaryPrompt,
        QueryRuntimeDynamicContextResult? dynamicContext)
    {
        var currentTools = ResolveAvailableTools(request) ?? [];
        var options = EnsureRuntimeChatOptions(request.Options, state.NextRoundOptionOverrides);
        state.NextRoundOptionOverrides = null;
        if (request.EnableTools && allowToolCalls)
        {
            // Surface keeps the full tool catalog even when a required tool is set —
            // RequireSpecific is enough to steer the provider, and shrinking the surface
            // removes the model's only escape hatch when user intent diverges from the
            // forced tool (e.g. Plan/TaskList desync after API restart).
            options.Tools = currentTools
                .Cast<AITool>()
                .ToList();
            if (!string.IsNullOrWhiteSpace(requiredToolNameForRound))
            {
                options.ToolMode = ChatToolMode.RequireSpecific(requiredToolNameForRound.Trim());
            }
        }
        else
        {
            options.Tools = null;
        }

        var contextAssembly = _contextAssembler.Assemble(new QueryContextAssemblyRequest
        {
            RuntimeRequest = request,
            State = state,
            QueryId = queryId,
            Options = options,
            CurrentTools = currentTools,
            PendingToolBatchSummaryPrompt = pendingToolBatchSummaryPrompt,
            RequiredToolNameForRound = requiredToolNameForRound,
            AllowToolCalls = request.EnableTools && allowToolCalls,
            DynamicContextMessages = dynamicContext?.Messages ?? []
        });
        var promptSnapshot = contextAssembly.Snapshot;
        state.LastPromptAssemblySnapshot = promptSnapshot;
        await EmitEventAsync(eventSink, new PromptAssemblySnapshotEvent(
            Seq: seqBase + 5,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            Snapshot: promptSnapshot)).ConfigureAwait(false);
        AttachPromptSnapshotCorrelation(options, promptSnapshot);

        _logger.LogDebug(
            "QueryRuntimeEngine[{EntryPoint}] prompt assembly snapshot. QueryId={QueryId} Round={Round} Messages={MessageCount} Tools={ToolCount} ToolChoice={ToolChoice} RequiredTool={RequiredTool} Frames={FrameCount} EstimatedPromptTokens={EstimatedPromptTokens}",
            request.EntryPoint,
            queryId,
            state.Round,
            promptSnapshot.MessageCount,
            promptSnapshot.ToolNames.Count,
            promptSnapshot.ToolChoice ?? "(none)",
            promptSnapshot.RequiredToolName ?? "(none)",
            promptSnapshot.Frames.Count,
            promptSnapshot.EstimatedPromptTokens);

        return new RoundLlmRequestAssembly(
            currentTools,
            new LLMExecutionRequest(
                Messages: contextAssembly.Messages,
                Options: options,
                Scenario: request.Scenario,
                Session: request.Session,
                Caller: request.EntryPoint.ToString()));
    }

    private static void AttachPromptSnapshotCorrelation(
        ChatOptions options,
        PromptAssemblySnapshot snapshot)
    {
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.AdditionalProperties[PromptSnapshotQueryIdOptionKey] = snapshot.QueryId.ToString("N");
        options.AdditionalProperties[PromptSnapshotSessionIdOptionKey] = snapshot.SessionId;
        options.AdditionalProperties[PromptSnapshotRoundOptionKey] = snapshot.Round;
    }

    private async Task<ModelSamplingResult> SampleModelResponseAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        LLMExecutionRequest llmRequest,
        bool allowToolCalls,
        string? requiredToolNameForRound,
        Dictionary<FunctionCallContent, PrestartedToolExecution> streamingToolExecutions,
        HashSet<string> streamingToolSignatures,
        CancellationTokenSource streamingToolExecutionCts,
        CancellationToken ct)
    {
        var roundText = new StringBuilder();
        var roundThinking = new StringBuilder();
        var roundToolCalls = new List<FunctionCallContent>();
        var isThinking = false;

        await foreach (var update in _llmExecutor.StreamAsync(llmRequest, ct).ConfigureAwait(false))
        {
            if (update is UsageChatResponseUpdate usageUpdate)
            {
                AccumulateUsage(state, usageUpdate.Usage);
                continue;
            }

            var thinkingText = GetThinkingText(update);
            var contentText = GetContentText(update);

            if (!string.IsNullOrEmpty(thinkingText))
            {
                if (!isThinking)
                {
                    isThinking = true;
                    await EmitEventAsync(eventSink, new ThinkingStartedEvent(
                        Seq: seqBase + 10,
                        QueryId: queryId,
                        SessionId: request.SessionId,
                        EntryPoint: request.EntryPoint,
                        Round: state.Round));
                }

                roundThinking.Append(thinkingText);
                await EmitEventAsync(eventSink, new ThinkingDeltaEvent(
                    Seq: seqBase + 11 + roundThinking.Length,
                    QueryId: queryId,
                    SessionId: request.SessionId,
                    EntryPoint: request.EntryPoint,
                    Round: state.Round,
                    Delta: thinkingText));
            }
            else if (isThinking && !string.IsNullOrEmpty(contentText))
            {
                isThinking = false;
                await EmitEventAsync(eventSink, new ThinkingEndedEvent(
                    Seq: seqBase + 99,
                    QueryId: queryId,
                    SessionId: request.SessionId,
                    EntryPoint: request.EntryPoint,
                    Round: state.Round,
                    FullThinking: roundThinking.ToString()));
            }

            if (!string.IsNullOrEmpty(contentText) && !isThinking)
            {
                roundText.Append(contentText);
                await EmitEventAsync(eventSink, new AssistantDeltaEvent(
                    Seq: seqBase + 100 + roundText.Length,
                    QueryId: queryId,
                    SessionId: request.SessionId,
                    EntryPoint: request.EntryPoint,
                    Round: state.Round,
                    Delta: contentText));
            }

            foreach (var content in update.Contents ?? [])
            {
                if (content is FunctionCallContent fc)
                {
                    roundToolCalls.Add(fc);
                    var args = fc.Arguments != null
                        ? new Dictionary<string, object?>(fc.Arguments)
                        : new Dictionary<string, object?>();
                    await EmitEventAsync(eventSink, new ToolCallRequestedEvent(
                        Seq: seqBase + 200 + roundToolCalls.Count,
                        QueryId: queryId,
                        SessionId: request.SessionId,
                        EntryPoint: request.EntryPoint,
                        Round: state.Round,
                        ToolName: fc.Name ?? "unknown",
                        CallId: fc.CallId ?? string.Empty,
                        Arguments: args));

                    if (IsNonRequiredToolCall(fc, requiredToolNameForRound))
                    {
                        state.Flags |= RuntimeState.RequiredToolContractRecoveryUsed;
                        _logger.LogWarning(
                            "QueryRuntimeEngine[{EntryPoint}] session {SessionId} rejected streaming start for non-required tool {ToolName} during required-tool round. RequiredTool={RequiredToolName} Round={Round}",
                            request.EntryPoint,
                            request.SessionId,
                            fc.Name ?? "unknown",
                            requiredToolNameForRound,
                            state.Round);
                        continue;
                    }

                    var streamingDecision = _streamingToolExecutionPlanner.Decide(
                        new StreamingToolExecutionPlanRequest(
                            RuntimeRequest: request,
                            State: state,
                            ToolCall: fc,
                            AllowToolCallsThisRound: allowToolCalls,
                            ToolCoordinator: _toolCoordinator,
                            ActiveStreamingSignatures: streamingToolSignatures,
                            ActiveStreamingCount: streamingToolExecutions.Count));
                    await EmitStreamingToolDecisionAsync(
                            eventSink,
                            queryId,
                            request,
                            state,
                            seqBase,
                            fc,
                            roundToolCalls.Count,
                            streamingDecision)
                        .ConfigureAwait(false);

                    if (streamingDecision.ShouldStart)
                    {
                        streamingToolSignatures.Add(streamingDecision.Signature);
                        await EmitEventAsync(eventSink, new ToolExecutionStartedEvent(
                            Seq: seqBase + 300 + streamingToolExecutions.Count,
                            QueryId: queryId,
                            SessionId: request.SessionId,
                            EntryPoint: request.EntryPoint,
                            Round: state.Round,
                            ToolName: fc.Name ?? "unknown",
                            CallId: fc.CallId ?? string.Empty)).ConfigureAwait(false);

                        streamingToolExecutions[fc] = new PrestartedToolExecution(
                            ExecuteToolCallAsync(fc, request, state, streamingToolExecutionCts.Token));
                        state.Flags |= RuntimeState.StreamingToolExecutionUsed;
                    }
                }
                else if (content is UsageContent uc)
                {
                    AccumulateUsage(state, uc.Details);
                }
            }
        }

        if (isThinking)
        {
            await EmitEventAsync(eventSink, new ThinkingEndedEvent(
                Seq: seqBase + 99,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                Round: state.Round,
                FullThinking: roundThinking.ToString()));
        }

        if (_runtimeHookDispatcher != null)
        {
            var hookContext = new AfterModelResponseContext
            {
                Request = request,
                Round = state.Round,
                ResponseText = roundText.ToString(),
                ThinkingText = roundThinking.ToString(),
                ToolCalls = roundToolCalls.ToArray()
            };
            var updatedContext = await _runtimeHookDispatcher
                .DispatchAfterModelResponseAsync(hookContext, ct)
                .ConfigureAwait(false);
            if (!string.Equals(updatedContext.ResponseText, hookContext.ResponseText, StringComparison.Ordinal))
            {
                roundText.Clear();
                roundText.Append(updatedContext.ResponseText);
            }

            if (!string.Equals(updatedContext.ThinkingText, hookContext.ThinkingText, StringComparison.Ordinal))
            {
                roundThinking.Clear();
                roundThinking.Append(updatedContext.ThinkingText);
            }
        }

        await EmitEventAsync(eventSink, new ModelResponseSampledEvent(
            Seq: seqBase + 170,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            AssistantTextLength: roundText.Length,
            ThinkingTextLength: roundThinking.Length,
            StructuredToolCallCount: roundToolCalls.Count,
            PrestartedToolExecutionCount: streamingToolExecutions.Count)).ConfigureAwait(false);

        return new ModelSamplingResult(
            roundText.ToString(),
            roundThinking.ToString(),
            roundToolCalls,
            roundToolCalls.Count);
    }

    private async Task<RoundResult> ExecuteRoundAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        CancellationToken ct)
    {
        var seqBase = state.Round * 1000L;
        var allowToolCalls = ShouldAllowToolCallsThisRound(request, state);
        var requiredToolNameForRound = state.RequiredToolNameForNextRound;
        state.ForceAllowToolCallsNextRound = false;
        state.ForceDisableToolCallsNextRound = false;
        state.RequiredToolNameForNextRound = null;
        var reserveWrapUpRound = ShouldReserveToolWrapUpRound(request);
        var pendingToolBatchSummaryPrompt = state.PendingToolBatchSummaryPrompt;
        state.PendingToolBatchSummaryPrompt = null;
        var dynamicContext = await ResolveDynamicContextAsync(
            request,
            state,
            allowToolCalls,
            requiredToolNameForRound,
            ct).ConfigureAwait(false);
        if (dynamicContext != null)
        {
            var dynamicRequiredSuppressed = false;
            if (!string.IsNullOrWhiteSpace(dynamicContext.RequiredToolName))
            {
                ApplyDynamicRequiredTool(
                    request,
                    state,
                    dynamicContext.RequiredToolName,
                    dynamicContext.ForceAllowToolCalls,
                    ref allowToolCalls,
                    ref requiredToolNameForRound,
                    out dynamicRequiredSuppressed);
                if (dynamicRequiredSuppressed)
                {
                    dynamicContext = BuildSuppressedDynamicRequiredContext(dynamicContext.RequiredToolName);
                }
            }
            else
            {
                ResetDynamicRequiredToolState(state);
            }

            if (!dynamicRequiredSuppressed)
            {
                foreach (var hint in dynamicContext.RecoveryHints)
                {
                    AppendRuntimeRecoveryHint(state, hint);
                }
            }
        }
        else
        {
            ResetDynamicRequiredToolState(state);
        }
        using var streamingToolExecutionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var streamingToolExecutions = new Dictionary<FunctionCallContent, PrestartedToolExecution>(
            ReferenceEqualityComparer.Instance);
        var streamingToolSignatures = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            await SetLoopPhaseAsync(
                state,
                eventSink,
                queryId,
                request,
                seqBase + 1,
                QueryRuntimeLoopPhase.PromptAssembly,
                "assemble round messages, tools, tool choice and prompt snapshot").ConfigureAwait(false);

            var llmAssembly = await AssembleLlmRequestForRoundAsync(
                request,
                state,
                eventSink,
                queryId,
                seqBase,
                allowToolCalls,
                requiredToolNameForRound,
                pendingToolBatchSummaryPrompt,
                dynamicContext).ConfigureAwait(false);
            var currentTools = llmAssembly.CurrentTools;
            var llmRequest = llmAssembly.Request;

            var roundStartMs = state.Stopwatch.ElapsedMilliseconds;

            await SetLoopPhaseAsync(
                state,
                eventSink,
                queryId,
                request,
                seqBase + 6,
                QueryRuntimeLoopPhase.ModelSampling,
                "stream model response").ConfigureAwait(false);

            var modelResponse = await SampleModelResponseAsync(
                request,
                state,
                eventSink,
                queryId,
                seqBase,
                llmRequest,
                allowToolCalls,
                requiredToolNameForRound,
                streamingToolExecutions,
                streamingToolSignatures,
                streamingToolExecutionCts,
                ct).ConfigureAwait(false);
            var roundText = new StringBuilder(modelResponse.Text);
            var roundThinking = new StringBuilder(modelResponse.Thinking);
            var roundToolCalls = modelResponse.ToolCalls.ToList();
            var structuredToolCallCount = modelResponse.StructuredToolCallCount;
            if (roundToolCalls.Count == 0)
            {
                await RecoverLegacyToolCallsAsync(
                    request,
                    state,
                    eventSink,
                    queryId,
                    seqBase,
                    roundText,
                    roundThinking,
                    roundToolCalls,
                    currentTools).ConfigureAwait(false);
            }

            SanitizeVisibleAssistantTextBuffer(roundText);
            var toolPlan = new ToolPlan
            {
                Calls = roundToolCalls.ToArray(),
                AssistantText = roundText.ToString(),
                ThinkingText = roundThinking.ToString(),
                FromLegacyTextFallback = structuredToolCallCount == 0 && roundToolCalls.Count > 0
            };
            state.LastToolPlan = toolPlan;

            await SetLoopPhaseAsync(
                state,
                eventSink,
                queryId,
                request,
                seqBase + 180,
                QueryRuntimeLoopPhase.ToolPlanExtraction,
                $"extracted {toolPlan.Calls.Count} tool call(s); legacyFallback={toolPlan.FromLegacyTextFallback}").ConfigureAwait(false);
            await EmitEventAsync(eventSink, new ToolPlanExtractedEvent(
                Seq: seqBase + 181,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                Round: state.Round,
                ToolCallCount: toolPlan.Calls.Count,
                FromLegacyTextFallback: toolPlan.FromLegacyTextFallback,
                AssistantTextLength: toolPlan.AssistantText.Length,
                ThinkingTextLength: toolPlan.ThinkingText?.Length ?? 0)).ConfigureAwait(false);

        await SetLoopPhaseAsync(
            state,
            eventSink,
            queryId,
            request,
            seqBase + 240,
            QueryRuntimeLoopPhase.ToolPlanValidation,
            $"validate {roundToolCalls.Count} tool call(s)").ConfigureAwait(false);

        if (roundToolCalls.Count == 0)
        {
            var emptyValidationResult = new ToolPlanValidationResult
            {
                AcceptedCalls = Array.Empty<FunctionCallContent>(),
                RejectedCalls = Array.Empty<RejectedToolCall>(),
                RequiresRecovery = false
            };
            state.LastToolPlanValidation = emptyValidationResult;
            await EmitEventAsync(eventSink, new ToolPlanValidatedEvent(
                Seq: seqBase + 241,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                Round: state.Round,
                AcceptedCount: 0,
                RejectedCount: 0,
                RejectedCalls: emptyValidationResult.RejectedCalls,
                RequiresRecovery: false,
                RecoveryReason: null)).ConfigureAwait(false);
        }

        if (roundToolCalls.Count > 0 &&
            await TryHandleMalformedProtocolRecoveryAsync(
                request,
                state,
                eventSink,
                queryId,
                seqBase,
                roundToolCalls,
                ct).ConfigureAwait(false))
        {
            await EmitEventAsync(eventSink, new RoundCompletedEvent(
                Seq: seqBase + 500,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                Round: state.Round,
                ToolCallCount: roundToolCalls.Count,
                HasText: roundText.Length > 0,
                TextLength: roundText.Length,
                ThinkingLength: roundThinking.Length,
                ContinueReason: state.LastContinueReason)).ConfigureAwait(false);

            _telemetry?.RecordRound(new QueryLoopRoundCompleted(
                queryId,
                request.SessionId,
                request.EntryPoint,
                state.Round + 1,
                roundToolCalls.Count,
                roundText.Length > 0,
                roundText.Length,
                roundThinking.Length,
                state.Messages.Sum(m => m.Text?.Length ?? 0),
                state.Stopwatch.ElapsedMilliseconds - roundStartMs));

            return new RoundResult(
                Text: roundText.ToString(),
                Thinking: roundThinking.ToString(),
                ToolCalls: roundToolCalls,
                ShouldTerminate: state.TerminationReason == QueryTerminationReason.RecoveryExhausted,
                TerminationReason: state.TerminationReason == QueryTerminationReason.RecoveryExhausted
                    ? QueryTerminationReason.RecoveryExhausted
                    : null);
        }

        if (roundToolCalls.Count > 0)
        {
            UpdateConsecutiveToolCallState(state, roundToolCalls);

            if (await TryHandleStallRecoveryAsync(
                    request,
                    state,
                    eventSink,
                    queryId,
                    seqBase,
                    roundText.ToString(),
                    roundToolCalls,
                    ct).ConfigureAwait(false))
            {
                await EmitEventAsync(eventSink, new RoundCompletedEvent(
                    Seq: seqBase + 500,
                    QueryId: queryId,
                    SessionId: request.SessionId,
                    EntryPoint: request.EntryPoint,
                    Round: state.Round,
                    ToolCallCount: roundToolCalls.Count,
                    HasText: roundText.Length > 0,
                    TextLength: roundText.Length,
                    ThinkingLength: roundThinking.Length,
                    ContinueReason: state.LastContinueReason)).ConfigureAwait(false);

                _telemetry?.RecordRound(new QueryLoopRoundCompleted(
                    queryId,
                    request.SessionId,
                    request.EntryPoint,
                    state.Round + 1,
                    roundToolCalls.Count,
                    roundText.Length > 0,
                    roundText.Length,
                    roundThinking.Length,
                    state.Messages.Sum(m => m.Text?.Length ?? 0),
                    state.Stopwatch.ElapsedMilliseconds - roundStartMs));

                return new RoundResult(
                    Text: roundText.ToString(),
                    Thinking: roundThinking.ToString(),
                    ToolCalls: roundToolCalls,
                    ShouldTerminate: state.TerminationReason == QueryTerminationReason.RecoveryExhausted,
                    TerminationReason: state.TerminationReason == QueryTerminationReason.RecoveryExhausted
                        ? QueryTerminationReason.RecoveryExhausted
                        : null);
            }
        }
        else
        {
            state.ConsecutiveSameToolCount = 0;
            state.LastToolSignature = null;
        }

        var suppressedWrapUpToolCalls = await HandleWrapUpToolCallsAsync(
            request,
            state,
            eventSink,
            queryId,
            seqBase,
            allowToolCalls,
            roundToolCalls,
            roundText).ConfigureAwait(false);

        // Execute tools if any
        if (roundToolCalls.Count > 0)
        {
            var validationOutput = await _toolPlanValidator.ValidateAsync(
                new ToolPlanValidationRequest
                {
                    ToolPlan = state.LastToolPlan ?? toolPlan,
                    RuntimeRequest = request,
                    RequiredToolNameForRound = requiredToolNameForRound,
                    PrestartedStreamingCalls = streamingToolExecutions.Keys.ToArray()
                },
                ct).ConfigureAwait(false);
            var validationResult = validationOutput.ValidationResult;
            state.LastToolPlanValidation = validationResult;
            await EmitEventAsync(eventSink, new ToolPlanValidatedEvent(
                Seq: seqBase + 261,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                Round: state.Round,
                AcceptedCount: validationResult.AcceptedCalls.Count,
                RejectedCount: validationResult.RejectedCalls.Count,
                RejectedCalls: validationResult.RejectedCalls,
                RequiresRecovery: validationResult.RequiresRecovery,
                RecoveryReason: validationResult.RecoveryReason)).ConfigureAwait(false);

            await SetLoopPhaseAsync(
                state,
                eventSink,
                queryId,
                request,
                seqBase + 270,
                QueryRuntimeLoopPhase.ToolArgumentNormalization,
                "normalize accepted tool call arguments before execution").ConfigureAwait(false);

            var executableToolCalls = validationOutput.ExecutableToolCalls.ToList();
            var requiredToolViolations = validationOutput.RequiredToolViolations;

            if (!string.IsNullOrWhiteSpace(requiredToolNameForRound) && requiredToolViolations.Count > 0)
            {
                await HandleRequiredToolViolationsAsync(
                    request,
                    state,
                    eventSink,
                    queryId,
                    seqBase,
                    requiredToolNameForRound,
                    requiredToolViolations).ConfigureAwait(false);
            }

            AppendValidatedToolPlanTranscript(
                state,
                roundText,
                roundToolCalls,
                validationOutput);

            if (executableToolCalls.Count > 0)
            {
                await ExecuteAndObserveAcceptedToolCallsAsync(
                    request,
                    state,
                    eventSink,
                    queryId,
                    seqBase,
                    executableToolCalls,
                    validationResult,
                    streamingToolExecutions,
                    currentTools,
                    requiredToolNameForRound,
                    reserveWrapUpRound,
                    roundText,
                    ct).ConfigureAwait(false);
            }
            else
            {
                state.ConsecutiveToolOnlyRounds = 0;
                ClearRepeatedReadEvidence(state);
                state.Messages.Add(new ChatMessage(
                    ChatRole.User,
                    "[SYSTEM] 前一轮工具调用被拦截或跳过。请根据系统反馈调整方案，不要重复相同的调用。"));
            }
        }
        else
        {
            var recoveryRoundResult = await HandleNoToolCallsAsync(
                request,
                state,
                eventSink,
                queryId,
                seqBase,
                roundToolCalls,
                currentTools,
                roundText,
                roundThinking,
                ct).ConfigureAwait(false);
            if (recoveryRoundResult != null)
            {
                return recoveryRoundResult;
            }
        }

        // Update state
        PersistVisibleRoundState(state, roundText.ToString(), roundThinking.ToString());

        await CompleteRoundObservationAsync(
            request,
            state,
            eventSink,
            queryId,
            seqBase,
            roundStartMs,
            roundText,
            roundThinking,
            roundToolCalls).ConfigureAwait(false);

        var postRoundRecoveryResult = await TryHandlePostRoundRecoveryAsync(
            request,
            state,
            eventSink,
            queryId,
            seqBase,
            roundText.ToString(),
            roundThinking.ToString(),
            roundToolCalls,
            currentTools,
            ct).ConfigureAwait(false);
        if (postRoundRecoveryResult != null)
        {
            return postRoundRecoveryResult;
        }

        return await DecideRoundStopAsync(
            request,
            state,
            eventSink,
            queryId,
            seqBase,
            roundText,
            roundThinking,
            roundToolCalls,
            suppressedWrapUpToolCalls).ConfigureAwait(false);
        }
        finally
        {
            await CancelStreamingToolExecutionsAsync(
                    streamingToolExecutions.Values,
                    streamingToolExecutionCts)
                .ConfigureAwait(false);
        }
    }

    private async Task<RoundResult?> HandleNoToolCallsAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        List<FunctionCallContent> roundToolCalls,
        IReadOnlyList<AIFunction> currentTools,
        StringBuilder roundText,
        StringBuilder roundThinking,
        CancellationToken ct)
    {
        state.ZeroToolCallRounds++;
        var hasVisibleAssistantText = roundText.Length > 0;

        if (hasVisibleAssistantText)
        {
            state.Messages.Add(new ChatMessage(ChatRole.Assistant, roundText.ToString()));
        }

        await SetLoopPhaseAsync(
            state,
            eventSink,
            queryId,
            request,
            seqBase + 700,
            QueryRuntimeLoopPhase.RecoveryDecision,
            "evaluate zero-tool response recovery").ConfigureAwait(false);

        var recoveryRoundResult = await TryHandleZeroToolResponseRecoveryAsync(
            request,
            state,
            eventSink,
            queryId,
            seqBase,
            roundText.ToString(),
            roundThinking.ToString(),
            roundToolCalls,
            currentTools,
            ct).ConfigureAwait(false);
        if (recoveryRoundResult != null)
        {
            return recoveryRoundResult;
        }

        if (hasVisibleAssistantText)
        {
            state.ConsecutiveToolOnlyRounds = 0;
            ClearRepeatedReadEvidence(state);
        }

        return null;
    }

    private async Task<bool> HandleWrapUpToolCallsAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        bool allowToolCalls,
        List<FunctionCallContent> roundToolCalls,
        StringBuilder roundText)
    {
        if (roundToolCalls.Count == 0 || allowToolCalls)
        {
            return false;
        }

        if (TryExtendWrapUpToolContinuation(request, state, out var attempt, out var maxAttempts))
        {
            var toolNames = FormatToolNames(roundToolCalls);
            state.Flags |= RuntimeState.WrapUpToolContinuationUsed;
            _logger.LogWarning(
                "QueryRuntimeEngine[{EntryPoint}] session {SessionId} emitted {ToolCallCount} tool call(s) during wrap-up round {Round}; extending runtime to execute them and keep a summary round. Attempt={Attempt}/{MaxAttempts}",
                request.EntryPoint,
                request.SessionId,
                roundToolCalls.Count,
                state.Round,
                attempt,
                maxAttempts);

            await EmitEventAsync(eventSink, new RecoveryTriggeredEvent(
                Seq: seqBase + 935 + attempt,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                Round: state.Round,
                RecoveryType: "wrapup_tool_continuation",
                Attempt: attempt,
                Reason: "assistant emitted tool calls during the reserved wrap-up round")).ConfigureAwait(false);

            await EmitEventAsync(eventSink, new SystemNoticeEvent(
                Seq: seqBase + 945 + attempt,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                NoticeType: "wrapup_tool_continuation",
                Content: $"wrap-up 轮检测到工具调用，runtime 已额外保留总结轮并继续执行：{toolNames}（{attempt}/{maxAttempts}）")).ConfigureAwait(false);

            return false;
        }

        state.Flags |= RuntimeState.WrapUpToolCallsSuppressed;
        if (string.IsNullOrWhiteSpace(state.TerminalDetailCode))
        {
            state.TerminalDetailCode = QueryTerminalDetailCodes.WrapUpToolCallsSuppressed;
        }

        var maxRoundsMessage = BuildSuppressedWrapUpToolCallsMessage(
            roundToolCalls,
            state.WrapUpToolContinuationCount);
        _logger.LogWarning(
            "QueryRuntimeEngine[{EntryPoint}] session {SessionId} emitted {ToolCallCount} tool call(s) during wrap-up round {Round}; continuation limit reached, suppressing execution. ContinuationsUsed={ContinuationsUsed}",
            request.EntryPoint,
            request.SessionId,
            roundToolCalls.Count,
            state.Round,
            state.WrapUpToolContinuationCount);

        await EmitEventAsync(eventSink, new SystemNoticeEvent(
            Seq: seqBase + 949,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            NoticeType: "wrapup_tool_calls_suppressed",
            Content: maxRoundsMessage)).ConfigureAwait(false);

        roundToolCalls.Clear();
        if (roundText.Length == 0)
        {
            roundText.Append(maxRoundsMessage);
        }
        else
        {
            roundText.AppendLine();
            roundText.AppendLine();
            roundText.Append("[runtime] ").Append(maxRoundsMessage);
        }

        return true;
    }

    private async Task ExecuteAndObserveAcceptedToolCallsAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        List<FunctionCallContent> executableToolCalls,
        ToolPlanValidationResult validationResult,
        Dictionary<FunctionCallContent, PrestartedToolExecution> streamingToolExecutions,
        IReadOnlyList<AIFunction> currentTools,
        string? requiredToolNameForRound,
        bool reserveWrapUpRound,
        StringBuilder roundText,
        CancellationToken ct)
    {
        var normalizationResult = _toolArgumentNormalizer.Normalize(new ToolArgumentNormalizationRequest
        {
            Calls = executableToolCalls,
            RuntimeRequest = request,
            State = state,
            PrestartedStreamingCalls = streamingToolExecutions.Keys.ToArray()
        });
        executableToolCalls = normalizationResult.Calls.ToList();
        _logger.LogDebug(
            "QueryRuntimeEngine[{EntryPoint}] normalized {NormalizedCallCount}/{AcceptedCallCount} accepted tool call argument set(s)",
            request.EntryPoint,
            normalizationResult.NormalizedCallCount,
            executableToolCalls.Count);
        await EmitEventAsync(eventSink, new ToolArgumentsNormalizedEvent(
            Seq: seqBase + 271,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            AcceptedCount: validationResult.AcceptedCalls.Count,
            NormalizedCount: normalizationResult.NormalizedCallCount,
            PrestartedToolCount: streamingToolExecutions.Count)).ConfigureAwait(false);

        UpdateToolOnlyRoundState(state, hasVisibleAssistantText: roundText.Length > 0);

        var deferredStartedCount = 0;
        for (var i = 0; i < executableToolCalls.Count; i++)
        {
            var call = executableToolCalls[i];
            if (streamingToolExecutions.ContainsKey(call))
            {
                continue;
            }

            var callId = call.CallId ?? string.Empty;
            var toolName = call.Name ?? "unknown";

            await EmitEventAsync(eventSink, new ToolExecutionStartedEvent(
                Seq: seqBase + 300 + streamingToolExecutions.Count + deferredStartedCount,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                Round: state.Round,
                ToolName: toolName,
                CallId: callId));
            deferredStartedCount++;
        }

        await SetLoopPhaseAsync(
            state,
            eventSink,
            queryId,
            request,
            seqBase + 299,
            QueryRuntimeLoopPhase.ToolExecution,
            $"execute {executableToolCalls.Count} accepted tool call(s)").ConfigureAwait(false);

        var execution = await _toolPlanExecutor.ExecuteAsync(new ToolPlanExecutionRequest
        {
            Calls = executableToolCalls,
            PrestartedExecutions = streamingToolExecutions,
            AvailableTools = FilterToolsForRequiredRecoveryTool(currentTools, requiredToolNameForRound),
            RuntimeRequest = request,
            State = state
        }, ct).ConfigureAwait(false);
        var executionResults = execution.Results.ToList();

        for (var i = 0; i < executionResults.Count; i++)
        {
            var call = executableToolCalls[i];
            var result = executionResults[i];

            await EmitEventAsync(eventSink, new ToolExecutionCompletedEvent(
                Seq: seqBase + 400 + i,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                Round: state.Round,
                ToolName: result.ToolName,
                CallId: result.CallId,
                Result: result.Result,
                Success: result.Success,
                ResultLength: result.ResultLength));

            var toolResultTranscript = BuildToolResultTranscript(result);
            ChatMessage? postToolInjectedMessage = null;
            if (request.InterventionHook != null)
            {
                var critiqueResult = await request.InterventionHook.OnToolExecutionCompletedAsync(
                    result.ToolName, result.Result, result.Success, request.Session, ct);

                if (critiqueResult.ShouldSkipToolResult)
                {
                    _logger.LogWarning(
                        "Tool {ToolName} result rejected by critique hook. Reason: {Reason}",
                        result.ToolName,
                        critiqueResult.Reason);

                    if (critiqueResult.InjectedMessage != null)
                    {
                        postToolInjectedMessage = critiqueResult.InjectedMessage;
                    }

                    toolResultTranscript = BuildSyntheticToolResultTranscript(
                        result.ToolName,
                        critiqueResult.Reason,
                        "tool result rejected by runtime critique after execution");
                }
            }

            state.Messages.Add(new ChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent(call.CallId ?? string.Empty, toolResultTranscript)]));

            if (postToolInjectedMessage != null)
            {
                state.Messages.Add(postToolInjectedMessage);
            }

            state.TotalToolCalls++;
            state.ExecutedToolNames.Add(result.ToolName);
            if (result.Success)
            {
                state.SuccessfulToolNames.Add(result.ToolName);
            }

            if (PlanningToolNames.IsPlanCreationTool(result.ToolName))
            {
                state.ExecutedPlanningToolCount++;
            }

            if (ToolClassification.IsWriteTool(result.ToolName))
            {
                state.TotalWriteToolCalls++;
            }
        }

        await SetLoopPhaseAsync(
            state,
            eventSink,
            queryId,
            request,
            seqBase + 430,
            QueryRuntimeLoopPhase.Observation,
            $"observe {executionResults.Count} tool result(s) into evidence ledger").ConfigureAwait(false);

        var observation = _toolObservationProcessor.Observe(new ToolObservationRequest
        {
            ToolCalls = executableToolCalls,
            ToolResults = executionResults,
            RuntimeRequest = request,
            State = state
        });
        await EmitEventAsync(eventSink, new ToolObservationCompletedEvent(
            Seq: seqBase + 431,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            ToolResultCount: observation.ToolResults.Count,
            HasWriteEvidence: observation.HasWriteEvidence,
            HasRepeatedReadEvidence: observation.HasRepeatedReadEvidence,
            RepeatedReadTargets: observation.RepeatedReadTargets,
            RequiredToolContractSatisfied: observation.RequiredToolContractSatisfied,
            FileEvidenceCount: observation.UpdatedLedger.Files.Count,
            ToolEvidenceCount: observation.UpdatedLedger.ToolResults.Count,
            PendingModificationCount: observation.UpdatedLedger.PendingModifications.Count,
            FailureCount: observation.UpdatedLedger.Failures.Count)).ConfigureAwait(false);

        var forceReadOnlyAnalysisSynthesisNextRound =
            ShouldForceReadOnlyAnalysisSynthesisRound(request, state);
        var toolBatchSummaryPrompt = BuildToolBatchSummaryPrompt(
            executableToolCalls,
            observation.ToolResults.ToList(),
            request,
            state,
            requireFinalAnswerOnNextRound:
                forceReadOnlyAnalysisSynthesisNextRound ||
                (reserveWrapUpRound && state.Round == state.MaxRounds - 2));
        state.PendingToolBatchSummaryPrompt = toolBatchSummaryPrompt;
        state.LastToolBatchSummaryPrompt = toolBatchSummaryPrompt;
        state.EvidenceLedger.LastToolBatchSummary = toolBatchSummaryPrompt;
        state.LastContinueReason = ContinueReasons.NextToolRound;

        await SetLoopPhaseAsync(
            state,
            eventSink,
            queryId,
            request,
            seqBase + 470,
            QueryRuntimeLoopPhase.ContinuationDecision,
            "continue after tool execution").ConfigureAwait(false);
    }

    private static void AppendValidatedToolPlanTranscript(
        QueryRuntimeState state,
        StringBuilder roundText,
        IReadOnlyList<FunctionCallContent> roundToolCalls,
        ToolPlanValidationOutput validationOutput)
    {
        var assistantContents = new List<AIContent>();
        if (roundText.Length > 0)
        {
            assistantContents.Add(new TextContent(roundText.ToString()));
        }

        assistantContents.AddRange(roundToolCalls.Cast<AIContent>());
        if (assistantContents.Count > 0)
        {
            state.Messages.Add(new ChatMessage(ChatRole.Assistant, assistantContents));
        }

        foreach (var blockedToolResult in validationOutput.BlockedToolResults)
        {
            state.Messages.Add(new ChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent(blockedToolResult.Call.CallId ?? string.Empty, blockedToolResult.Transcript)]));
        }

        foreach (var injectedMessage in validationOutput.InjectedPreExecutionMessages)
        {
            state.Messages.Add(injectedMessage);
        }
    }

    private async Task HandleRequiredToolViolationsAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        string requiredToolNameForRound,
        IReadOnlyCollection<string> requiredToolViolations)
    {
        state.Flags |= RuntimeState.RequiredToolContractRecoveryUsed;
        state.RecoveryCount++;
        state.LastContinueReason = ContinueReasons.RequiredToolContractRecovery;
        state.ForceAllowToolCallsNextRound = true;
        state.RequiredToolNameForNextRound = requiredToolNameForRound;
        state.NextRoundOptionOverrides = MergeRuntimeOptionOverrides(
            state.NextRoundOptionOverrides,
            new Dictionary<string, object?>
            {
                ["ToolMode"] = ChatToolMode.RequireSpecific(requiredToolNameForRound),
                ["ThinkingEnabled"] = false
            });
        EnsureStopHookContinuationRounds(request, state, allowToolCallsOnNextRound: true);

        var rejectedTools = string.Join(", ", requiredToolViolations);
        var feedback =
            $"[SYSTEM] 上一轮处于 required-tool recovery，只允许调用 `{requiredToolNameForRound}`。Runtime 已拒绝执行这些非目标工具：{rejectedTools}。下一轮不要读取、搜索或解释，必须只调用 `{requiredToolNameForRound}`。";
        AppendPendingRoundPrompt(state, feedback);
        AppendRuntimeRecoveryHint(
            state,
            source: "required_tool_contract_violation",
            attempt: state.RecoveryCount,
            requiredToolName: requiredToolNameForRound,
            toolCallRequired: true,
            message: $"Rejected non-required tools: {rejectedTools}. Call only {requiredToolNameForRound}.",
            candidateFiles: ResolveRecoveryCandidateFiles(state));

        _logger.LogWarning(
            "QueryRuntimeEngine[{EntryPoint}] session {SessionId} rejected {RejectedCount} non-required tool call(s) during required-tool round. RequiredTool={RequiredToolName} RejectedTools={RejectedTools} Round={Round}",
            request.EntryPoint,
            request.SessionId,
            requiredToolViolations.Count,
            requiredToolNameForRound,
            rejectedTools,
            state.Round);

        await EmitEventAsync(eventSink, new RecoveryTriggeredEvent(
            Seq: seqBase + 938,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            RecoveryType: "required_tool_contract_recovery",
            Attempt: state.RecoveryCount,
            Reason: $"assistant emitted non-required tool(s) during required `{requiredToolNameForRound}` round: {rejectedTools}")).ConfigureAwait(false);

        await EmitEventAsync(eventSink, new SystemNoticeEvent(
            Seq: seqBase + 948,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            NoticeType: "required_tool_contract_recovery",
            Content: $"required-tool 轮拒绝执行非目标工具：{rejectedTools}；下一轮继续强制 `{requiredToolNameForRound}`。")).ConfigureAwait(false);
    }

    private async Task CompleteRoundObservationAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        long roundStartMs,
        StringBuilder roundText,
        StringBuilder roundThinking,
        List<FunctionCallContent> roundToolCalls)
    {
        var roundDurationMs = state.Stopwatch.ElapsedMilliseconds - roundStartMs;
        await EmitEventAsync(eventSink, new RoundCompletedEvent(
            Seq: seqBase + 500,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            ToolCallCount: roundToolCalls.Count,
            HasText: roundText.Length > 0,
            TextLength: roundText.Length,
            ThinkingLength: roundThinking.Length,
            ContinueReason: roundToolCalls.Count > 0 ? ContinueReasons.NextToolRound : null));

        _telemetry?.RecordRound(new QueryLoopRoundCompleted(
            queryId,
            request.SessionId,
            request.EntryPoint,
            state.Round + 1,
            roundToolCalls.Count,
            roundText.Length > 0,
            roundText.Length,
            roundThinking.Length,
            state.Messages.Sum(m => m.Text?.Length ?? 0),
            roundDurationMs));
    }

    private static QueryTerminationReason? DetermineRoundTerminationReason(
        QueryRuntimeState state,
        int toolCallCount,
        int textLength,
        bool suppressedWrapUpToolCalls)
    {
        if (toolCallCount == 0)
        {
            return suppressedWrapUpToolCalls && textLength == 0
                ? QueryTerminationReason.MaxRoundsReached
                : QueryTerminationReason.NoToolCalls;
        }

        return (state.Round + 1) >= state.MaxRounds
            ? QueryTerminationReason.MaxRoundsReached
            : null;
    }

    private async Task<RoundResult> DecideRoundStopAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        StringBuilder roundText,
        StringBuilder roundThinking,
        List<FunctionCallContent> roundToolCalls,
        bool suppressedWrapUpToolCalls)
    {
        var terminationReason = DetermineRoundTerminationReason(
            state,
            roundToolCalls.Count,
            roundText.Length,
            suppressedWrapUpToolCalls);

        if (terminationReason == QueryTerminationReason.MaxRoundsReached && roundText.Length == 0)
        {
            ApplyMaxRoundsReachedFeedback(state, roundToolCalls);
        }

        await SetLoopPhaseAsync(
            state,
            eventSink,
            queryId,
            request,
            seqBase + 760,
            QueryRuntimeLoopPhase.StopDecision,
            terminationReason.HasValue
                ? $"stop: {terminationReason.Value}"
                : "continue: no terminal condition").ConfigureAwait(false);

        return new RoundResult(
            Text: roundText.ToString(),
            Thinking: roundThinking.ToString(),
            ToolCalls: roundToolCalls,
            ShouldTerminate: terminationReason.HasValue,
            TerminationReason: terminationReason);
    }

    private async Task<RoundResult?> TryHandleZeroToolResponseRecoveryAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        string roundText,
        string roundThinking,
        List<FunctionCallContent> roundToolCalls,
        IReadOnlyList<AIFunction> currentTools,
        CancellationToken ct)
    {
        if (ShouldPrioritizeWriteIntentRecovery(request, state, currentTools) &&
            await TryRecoverUnexecutedWriteIntentAsync(
                request,
                state,
                eventSink,
                queryId,
                seqBase,
                roundText,
                roundThinking,
                currentTools,
                ct).ConfigureAwait(false))
        {
            PersistVisibleRoundState(state, roundText, roundThinking);
            return BuildContinuationRoundResult(roundText, roundThinking, roundToolCalls);
        }

        if (await TryRecoverUnexecutedCommandIntentAsync(
                request,
                state,
                eventSink,
                queryId,
                seqBase,
                roundText,
                roundThinking,
                currentTools,
                ct).ConfigureAwait(false))
        {
            PersistVisibleRoundState(state, roundText, roundThinking);
            return BuildContinuationRoundResult(roundText, roundThinking, roundToolCalls);
        }

        if (await TryRecoverUnexecutedReadIntentAsync(
                request,
                state,
                eventSink,
                queryId,
                seqBase,
                roundText,
                currentTools,
                ct).ConfigureAwait(false))
        {
            PersistVisibleRoundState(state, roundText, roundThinking);
            return BuildContinuationRoundResult(roundText, roundThinking, roundToolCalls);
        }

        if (await TryRecoverUnexecutedPlanningIntentAsync(
                request,
                state,
                eventSink,
                queryId,
                seqBase,
                roundText,
                roundThinking,
                currentTools,
                ct).ConfigureAwait(false))
        {
            PersistVisibleRoundState(state, roundText, roundThinking);
            return BuildContinuationRoundResult(roundText, roundThinking, roundToolCalls);
        }

        if (await TryRecoverUnexecutedWriteIntentAsync(
                request,
                state,
                eventSink,
                queryId,
                seqBase,
                roundText,
                roundThinking,
                currentTools,
                ct).ConfigureAwait(false))
        {
            PersistVisibleRoundState(state, roundText, roundThinking);
            return BuildContinuationRoundResult(roundText, roundThinking, roundToolCalls);
        }

        if (await TryHandleZeroToolCallRecoveryAsync(
                request,
                state,
                eventSink,
                queryId,
                seqBase,
                roundText,
                roundToolCalls,
                ct).ConfigureAwait(false))
        {
            PersistVisibleRoundState(state, roundText, roundThinking);
            return BuildRecoveryRoundResult(state, roundText, roundThinking, roundToolCalls);
        }

        var stopHookResult = await TryHandleStopHookContinuationAsync(
                request,
                state,
                eventSink,
                queryId,
                seqBase,
                roundText,
                roundThinking,
                ct).ConfigureAwait(false);
        if (stopHookResult.Continue)
        {
            var visibleText = stopHookResult.FinalTextOverride ?? roundText.ToString();
            PersistVisibleRoundState(state, visibleText, roundThinking);
            return BuildRecoveryRoundResult(state, visibleText, roundThinking, roundToolCalls);
        }

        return null;
    }

    private async Task<RoundResult?> TryHandlePostRoundRecoveryAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        string roundText,
        string roundThinking,
        List<FunctionCallContent> roundToolCalls,
        IReadOnlyList<AIFunction> currentTools,
        CancellationToken ct)
    {
        if (await TryHandleEmptyResponseRecoveryAsync(
                request,
                state,
                eventSink,
                queryId,
                seqBase,
                roundText,
                roundToolCalls,
                currentTools,
                ct).ConfigureAwait(false))
        {
            return BuildRecoveryRoundResult(state, roundText, roundThinking, roundToolCalls);
        }

        if (await TryRecoverInsufficientVisibleAnswerAsync(
                request,
                state,
                eventSink,
                queryId,
                seqBase,
                roundText,
                roundThinking,
                roundToolCalls,
                ct).ConfigureAwait(false))
        {
            return BuildRecoveryRoundResult(state, roundText, roundThinking, roundToolCalls);
        }

        return null;
    }

    private static RoundResult BuildContinuationRoundResult(
        string roundText,
        string roundThinking,
        List<FunctionCallContent> roundToolCalls)
        => new(
            Text: roundText,
            Thinking: roundThinking,
            ToolCalls: roundToolCalls,
            ShouldTerminate: false,
            TerminationReason: null);

    private static RoundResult BuildRecoveryRoundResult(
        QueryRuntimeState state,
        string roundText,
        string roundThinking,
        List<FunctionCallContent> roundToolCalls)
        => new(
            Text: roundText,
            Thinking: roundThinking,
            ToolCalls: roundToolCalls,
            ShouldTerminate: state.TerminationReason == QueryTerminationReason.RecoveryExhausted,
            TerminationReason: state.TerminationReason == QueryTerminationReason.RecoveryExhausted
                ? QueryTerminationReason.RecoveryExhausted
                : null);

    private static string? GetThinkingText(ChatResponseUpdate update)
    {
        // Check for ReasoningChatResponseUpdate with Thinking content
        if (update is ReasoningChatResponseUpdate ru && ru.Thinking)
        {
            var contentsText = ExtractTextFromContents(update);
            if (!string.IsNullOrEmpty(contentsText))
            {
                return contentsText;
            }

            var reasoningText = ExtractReasoningText(update);
            if (!string.IsNullOrEmpty(reasoningText))
            {
                return reasoningText;
            }

            return update.Text;
        }

        return null;
    }

    private static string? GetContentText(ChatResponseUpdate update)
    {
        if (update is ReasoningChatResponseUpdate ru && ru.Thinking)
        {
            return null; // This is thinking, not content
        }

        var contentsText = ExtractTextFromContents(update);
        return !string.IsNullOrEmpty(contentsText)
            ? contentsText
            : update.Text;
    }

    private static string? ExtractTextFromContents(ChatResponseUpdate update)
    {
        if (update.Contents == null)
        {
            return null;
        }

        StringBuilder? builder = null;
        foreach (var content in update.Contents)
        {
            if (content is not TextContent textContent || string.IsNullOrEmpty(textContent.Text))
            {
                continue;
            }

            builder ??= new StringBuilder();
            builder.Append(textContent.Text);
        }

        return builder?.ToString();
    }

    private static string? ExtractReasoningText(ChatResponseUpdate update)
    {
        if (update is not ReasoningChatResponseUpdate reasoningUpdate || !reasoningUpdate.Thinking)
        {
            return null;
        }

        var reasoningType = reasoningUpdate.GetType();
        if (reasoningType.GetProperty("Reason")?.GetValue(reasoningUpdate) is string reason &&
            !string.IsNullOrEmpty(reason))
        {
            return reason;
        }

        if (reasoningType.GetProperty("Text")?.GetValue(reasoningUpdate) is string text &&
            !string.IsNullOrEmpty(text))
        {
            return text;
        }

        return null;
    }

    private async Task<ToolExecutionResult> ExecuteToolCallAsync(
        FunctionCallContent call,
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        CancellationToken ct)
    {
        var toolName = call.Name ?? "unknown";
        var callId = call.CallId ?? string.Empty;

        // Use tool coordinator if available
        if (_toolCoordinator != null)
        {
            return await _toolCoordinator.ExecuteAsync(call, ResolveAvailableTools(request), request, state, ct)
                .ConfigureAwait(false);
        }

        // Fallback: direct execution
        try
        {
            var tool = ResolveAvailableTools(request)?.FirstOrDefault(t => t.Name == toolName);
            if (tool == null)
            {
                return new ToolExecutionResult(
                    ToolName: toolName,
                    CallId: callId,
                    Result: $"Tool '{toolName}' not found",
                    Success: false);
            }

            var args = _toolArgumentNormalizer.NormalizeArguments(call.Arguments, request.Session);

            var result = await tool.InvokeAsync(
                new AIFunctionArguments(args),
                ct);

            var resultString = result?.ToString() ?? "Success";
            return new ToolExecutionResult(
                ToolName: toolName,
                CallId: callId,
                Result: resultString,
                Success: true,
                ResultLength: resultString.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool execution failed: {ToolName}", toolName);
            return new ToolExecutionResult(
                ToolName: toolName,
                CallId: callId,
                Result: $"Error: {ex.Message}",
                Success: false);
        }
    }

    private async ValueTask EmitStreamingToolDecisionAsync(
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        long seqBase,
        FunctionCallContent call,
        int callIndex,
        StreamingToolExecutionDecision decision)
    {
        if (decision.ShouldStart)
        {
            _logger.LogDebug(
                "Streaming-first tool execution started. SessionId={SessionId} Round={Round} Tool={ToolName} CallId={CallId}",
                request.SessionId,
                state.Round,
                decision.ToolName,
                call.CallId ?? string.Empty);
        }
        else if (_streamingToolExecutionOptions.LogSkippedDecisions)
        {
            _logger.LogDebug(
                "Streaming-first tool execution skipped. SessionId={SessionId} Round={Round} Tool={ToolName} CallId={CallId} Reason={Reason} Detail={Detail}",
                request.SessionId,
                state.Round,
                decision.ToolName,
                call.CallId ?? string.Empty,
                decision.Reason,
                decision.Detail ?? string.Empty);
        }

        if (!_streamingToolExecutionOptions.EmitDecisionEvents)
        {
            return;
        }

        await EmitEventAsync(eventSink, new StreamingToolDecisionEvent(
            Seq: seqBase + 250 + callIndex,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            ToolName: decision.ToolName,
            CallId: call.CallId ?? string.Empty,
            Started: decision.ShouldStart,
            Reason: decision.Reason,
            Detail: decision.Detail)).ConfigureAwait(false);
    }

    private static async Task CancelStreamingToolExecutionsAsync(
        IEnumerable<PrestartedToolExecution> executions,
        CancellationTokenSource cancellationTokenSource)
    {
        var pending = executions
            .Where(execution => !execution.Consumed && !execution.Task.IsCompleted)
            .Select(execution => execution.Task)
            .ToArray();

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        foreach (var task in pending)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Results that were never consumed belong to a round that exited through recovery.
            }
        }
    }

    private async Task<TransportRecoveryOutcome> TryHandleTransientTransportFailureAsync(
        Exception ex,
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested ||
            request.AdapterHints?.EnableTransportFailureRecovery != true ||
            !IsTransientTransportFailure(ex, ct))
        {
            return TransportRecoveryOutcome.NotHandled;
        }

        var maxAttempts = Math.Max(1, request.AdapterHints?.MaxRecoveryAttempts ?? 3);
        var attempt = state.TransportFailureCount + 1;
        state.TransportFailureCount = attempt;
        var decision = _recoveryPolicy?.DetectRecoveryNeeded(
            state,
            request,
            new RecoveryContext(
                LastException: ex,
                ContextChars: state.Messages.Sum(m => m.Text?.Length ?? 0)));
        var needsRecovery = decision?.NeedsRecovery ?? attempt <= maxAttempts;

        if (!needsRecovery || attempt > maxAttempts)
        {
            state.RecoveryCount++;
            state.Flags |= RuntimeState.TransportFailureRecoveryUsed;
            state.TerminationReason = QueryTerminationReason.RecoveryExhausted;
            state.LastAssistantText.Clear();
            state.LastAssistantText.Append("模型服务连接不稳定（transport failure），已超过 runtime 自动重试上限。");
            state.LastNonEmptyAssistantText.Clear();
            state.LastNonEmptyAssistantText.Append(state.LastAssistantText);

            _logger.LogWarning(
                ex,
                "QueryRuntimeEngine[{EntryPoint}] transport recovery exhausted for session {SessionId} on round {Round}. attempts={Attempts}/{MaxAttempts}",
                request.EntryPoint,
                request.SessionId,
                state.Round,
                decision?.CurrentAttempt ?? attempt,
                maxAttempts);

            _telemetry?.RecordRecovery(new QueryLoopRecovery(
                queryId,
                request.SessionId,
                request.EntryPoint,
                state.Round,
                "transport_failure",
                decision?.CurrentAttempt ?? attempt,
                Continued: false,
                Terminal: true));

            await EmitEventAsync(eventSink, new RecoveryTriggeredEvent(
                Seq: state.Round * 1000L + 910 + attempt,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                Round: state.Round,
                RecoveryType: "transport_failure",
                Attempt: decision?.CurrentAttempt ?? attempt,
                Reason: decision?.Reason ?? "transport recovery exhausted")).ConfigureAwait(false);

            await EmitEventAsync(eventSink, new ErrorEvent(
                Seq: state.Round * 1000L + 980,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                ErrorType: ex.GetType().Name,
                Message: "模型服务连接不稳定（transport failure），已超过 runtime 自动重试上限。",
                Exception: ex)).ConfigureAwait(false);

            return TransportRecoveryOutcome.Terminate;
        }

        state.TransportFailureCount = attempt;
        state.RecoveryCount++;
        state.Flags |= RuntimeState.TransportFailureRecoveryUsed;

        var delayMs = Math.Min(500 * attempt, 1500);
        _logger.LogWarning(
            ex,
            "QueryRuntimeEngine[{EntryPoint}] transient transport failure for session {SessionId} on round {Round}. retry {Attempt}/{MaxAttempts} after {DelayMs}ms",
            request.EntryPoint,
            request.SessionId,
            state.Round,
            attempt,
            maxAttempts,
            delayMs);

        _telemetry?.RecordRecovery(new QueryLoopRecovery(
            queryId,
            request.SessionId,
            request.EntryPoint,
            state.Round,
            "transport_failure",
            decision?.CurrentAttempt ?? attempt,
            Continued: true,
            Terminal: false));

        await EmitEventAsync(eventSink, new RecoveryTriggeredEvent(
            Seq: state.Round * 1000L + 900 + attempt,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            RecoveryType: "transport_failure",
            Attempt: decision?.CurrentAttempt ?? attempt,
            Reason: decision?.Reason ?? "transient transport failure detected")).ConfigureAwait(false);

        if (delayMs > 0)
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }

        return TransportRecoveryOutcome.Continue;
    }

    private static QueryRuntimeResult BuildResult(
        QueryRuntimeState state,
        Guid queryId,
        QueryRuntimeRequest request)
    {
        var finalThinking = state.LastThinkingText.Length > 0
            ? state.LastThinkingText.ToString()
            : null;
        var finalText = state.LastAssistantText.Length > 0
            ? state.LastAssistantText.ToString()
            : state.LastNonEmptyAssistantText.ToString();

        return new QueryRuntimeResult
        {
            TerminationReason = state.TerminationReason,
            TotalRounds = state.Round + 1,
            TotalToolCalls = state.TotalToolCalls,
            WriteToolCalls = state.TotalWriteToolCalls,
            ZeroToolCallRounds = state.ZeroToolCallRounds,
            EmptyResponseCount = state.EmptyResponseCount,
            RecoveryCount = state.RecoveryCount,
            MalformedProtocolCount = state.MalformedProtocolCount,
            FinalText = finalText,
            FinalThinking = finalThinking,
            TotalPromptTokens = state.TotalPromptTokens > 0 ? state.TotalPromptTokens : null,
            TotalCompletionTokens = state.TotalCompletionTokens > 0 ? state.TotalCompletionTokens : null,
            TotalDurationMs = state.Stopwatch.ElapsedMilliseconds,
            Flags = state.Flags,
            TerminalDetailCode = state.TerminalDetailCode,
            FinalMessages = state.Messages,
            LastPromptAssemblySnapshot = state.LastPromptAssemblySnapshot,
            RuntimeCheckpoint = QueryRuntimeCheckpointBuilder.Build(state, queryId, request),
            QueryId = queryId
        };
    }

    private static void PersistVisibleRoundState(
        QueryRuntimeState state,
        string assistantText,
        string thinkingText)
    {
        state.LastAssistantText.Clear();
        state.LastAssistantText.Append(assistantText);
        if (!string.IsNullOrEmpty(assistantText))
        {
            state.LastNonEmptyAssistantText.Clear();
            state.LastNonEmptyAssistantText.Append(assistantText);
        }

        state.LastThinkingText.Clear();
        state.LastThinkingText.Append(thinkingText);
    }

    private static void AccumulateUsage(QueryRuntimeState state, UsageDetails? usage)
    {
        if (usage == null)
        {
            return;
        }

        state.TotalPromptTokens += (int)(usage.InputTokenCount ?? 0);
        state.TotalCompletionTokens += (int)(usage.OutputTokenCount ?? 0);
    }

    private static void FinalizeTerminalState(QueryRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.TerminalDetailCode = QueryTerminalDetailCodes.Resolve(state);
    }

    private async Task ApplyContextWindowGovernanceAsync(
        QueryRuntimeRequest request,
        QueryRuntimeResult result,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        CancellationToken ct)
    {
        if (_contextWindowManager == null || request.ConversationCapture == null)
        {
            return;
        }

        var seqBase = state.Round * 1000L;
        await SetLoopPhaseAsync(
            state,
            eventSink,
            queryId,
            request,
            seqBase + 900,
            QueryRuntimeLoopPhase.ContextCompaction,
            "apply context window governance for completed turn").ConfigureAwait(false);

        if (IsContextCompactionCircuitOpen(request.SessionId, out var failureCount))
        {
            await EmitContextCompactionCompletedEventAsync(
                request,
                result,
                state,
                eventSink,
                queryId,
                success: false,
                errorType: "ContextCompactionCircuitOpen",
                errorMessage: $"Context compaction circuit is open after {failureCount} consecutive failures.").ConfigureAwait(false);
            return;
        }

        try
        {
            await _contextWindowManager.OnTurnCompletedAsync(request, result, ct).ConfigureAwait(false);
            ResetContextCompactionCircuit(request.SessionId);
            await EmitContextCompactionCompletedEventAsync(
                request,
                result,
                state,
                eventSink,
                queryId,
                success: true,
                errorType: null,
                errorMessage: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordContextCompactionFailure(request.SessionId);
            _logger.LogWarning(
                ex,
                "QueryRuntimeEngine[{EntryPoint}] failed to update context window for session {SessionId}",
                request.EntryPoint,
                request.SessionId);
            await EmitContextCompactionCompletedEventAsync(
                request,
                result,
                state,
                eventSink,
                queryId,
                success: false,
                errorType: ex.GetType().Name,
                errorMessage: ex.Message).ConfigureAwait(false);
        }
    }

    private async Task EmitContextCompactionCompletedEventAsync(
        QueryRuntimeRequest request,
        QueryRuntimeResult result,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        bool success,
        string? errorType,
        string? errorMessage)
    {
        await EmitEventAsync(eventSink, new ContextCompactionCompletedEvent(
            Seq: state.Round * 1000L + 901,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            Success: success,
            ErrorType: errorType,
            ErrorMessage: errorMessage,
            PromptTokens: result.TotalPromptTokens,
            CompletionTokens: result.TotalCompletionTokens,
            FinalMessageCount: result.FinalMessages?.Count ?? 0,
            FileEvidenceCount: state.EvidenceLedger.Files.Count,
            PendingModificationCount: state.EvidenceLedger.PendingModifications.Count)).ConfigureAwait(false);
    }

    private bool IsContextCompactionCircuitOpen(string sessionId, out int failureCount)
    {
        lock (_contextCompactionCircuitSync)
        {
            _contextCompactionFailureCounts.TryGetValue(sessionId, out failureCount);
            return failureCount >= ContextCompactionCircuitBreakerThreshold;
        }
    }

    private void RecordContextCompactionFailure(string sessionId)
    {
        lock (_contextCompactionCircuitSync)
        {
            _contextCompactionFailureCounts[sessionId] = _contextCompactionFailureCounts.TryGetValue(sessionId, out var count)
                ? count + 1
                : 1;
        }
    }

    private void ResetContextCompactionCircuit(string sessionId)
    {
        lock (_contextCompactionCircuitSync)
        {
            _contextCompactionFailureCounts.Remove(sessionId);
        }
    }

    private async Task PersistConversationCapturePreflightAsync(
        QueryRuntimeRequest request,
        CancellationToken ct)
    {
        if (_contextWindowManager == null || request.ConversationCapture == null)
        {
            return;
        }

        await _contextWindowManager.OnTurnStartedAsync(request, ct).ConfigureAwait(false);
    }

    private static async ValueTask EmitEventAsync(IQueryRuntimeEventSink sink, QueryRuntimeEvent evt)
    {
        var eventType = GetEventType(evt);
        if (sink.IsEnabled(eventType))
        {
            await sink.OnEventAsync(evt);
        }
    }

    private static async ValueTask SetLoopPhaseAsync(
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        QueryRuntimeRequest request,
        long seq,
        QueryRuntimeLoopPhase phase,
        string? detail = null)
    {
        state.CurrentPhase = phase;
        await EmitEventAsync(eventSink, new LoopPhaseChangedEvent(
            Seq: seq,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            Phase: phase,
            Detail: detail)).ConfigureAwait(false);
    }

    private static QueryRuntimeEventType GetEventType(QueryRuntimeEvent evt) => evt switch
    {
        RoundStartedEvent => QueryRuntimeEventType.RoundStarted,
        ThinkingStartedEvent => QueryRuntimeEventType.ThinkingStarted,
        ThinkingDeltaEvent => QueryRuntimeEventType.ThinkingDelta,
        ThinkingEndedEvent => QueryRuntimeEventType.ThinkingEnded,
        AssistantDeltaEvent => QueryRuntimeEventType.AssistantDelta,
        ModelResponseSampledEvent => QueryRuntimeEventType.ModelResponseSampled,
        ToolCallRequestedEvent => QueryRuntimeEventType.ToolCallRequested,
        ToolExecutionStartedEvent => QueryRuntimeEventType.ToolExecutionStarted,
        ToolExecutionCompletedEvent => QueryRuntimeEventType.ToolExecutionCompleted,
        StreamingToolDecisionEvent => QueryRuntimeEventType.StreamingToolDecision,
        RecoveryTriggeredEvent => QueryRuntimeEventType.RecoveryTriggered,
        SystemNoticeEvent => QueryRuntimeEventType.SystemNotice,
        PromptAssemblySnapshotEvent => QueryRuntimeEventType.PromptAssemblySnapshot,
        LoopPhaseChangedEvent => QueryRuntimeEventType.LoopPhaseChanged,
        ToolPlanExtractedEvent => QueryRuntimeEventType.ToolPlanExtracted,
        ToolPlanValidatedEvent => QueryRuntimeEventType.ToolPlanValidated,
        ToolArgumentsNormalizedEvent => QueryRuntimeEventType.ToolArgumentsNormalized,
        ToolObservationCompletedEvent => QueryRuntimeEventType.ToolObservationCompleted,
        ContextCompactionCompletedEvent => QueryRuntimeEventType.ContextCompactionCompleted,
        RoundCompletedEvent => QueryRuntimeEventType.RoundCompleted,
        TerminatedEvent => QueryRuntimeEventType.Terminated,
        ErrorEvent => QueryRuntimeEventType.Error,
        ConversationIdSetEvent => QueryRuntimeEventType.ConversationIdSet,
        _ => QueryRuntimeEventType.RoundStarted
    };

    private static bool IsTransientTransportFailure(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return false;
        }

        if (ex is TaskCanceledException or OperationCanceledException)
        {
            return true;
        }

        if (ex is HttpRequestException)
        {
            return true;
        }

        var root = ex.GetBaseException();
        if (root is HttpRequestException)
        {
            return true;
        }

        var mergedMessage = $"{ex.Message} {root.Message}";
        return mergedMessage.Contains("Response ended prematurely", StringComparison.OrdinalIgnoreCase)
               || mergedMessage.Contains("ResponseEnded", StringComparison.OrdinalIgnoreCase)
               || mergedMessage.Contains("An error occurred while sending the request", StringComparison.OrdinalIgnoreCase)
               || mergedMessage.Contains("connection reset", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<StopHookContinuationResult> TryHandleStopHookContinuationAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        string assistantText,
        string thinkingText,
        CancellationToken ct)
    {
        if (_runtimeHookDispatcher == null)
        {
            return StopHookContinuationResult.None;
        }

        var configuredContinuationLimit = Math.Max(
            1,
            request.AdapterHints?.MaxRecoveryAttempts ?? DefaultStopHookContinuationLimit);
        if (state.StopHookContinuationCount >= configuredContinuationLimit &&
            state.TotalToolCalls > 0 &&
            request.RequiredToolContract == null)
        {
            return StopHookContinuationResult.None;
        }

        if (state.StopHookContinuationCount > 0 &&
            state.TotalToolCalls > 0 &&
            request.RequiredToolContract != null &&
            request.RequiredToolContract.IsSatisfiedBy(state.ExecutedToolNames, state.SuccessfulToolNames))
        {
            return StopHookContinuationResult.None;
        }

        var stopContext = new BeforeStopContext
        {
            Request = request,
            Round = state.Round,
            LastAssistantMessage = assistantText,
            ThinkingText = thinkingText,
            StopHookActive = state.StopHookContinuationCount > 0,
            ContinuationCount = state.StopHookContinuationCount,
            ExecutedToolNames = new HashSet<string>(state.ExecutedToolNames, StringComparer.OrdinalIgnoreCase),
            SuccessfulToolNames = new HashSet<string>(state.SuccessfulToolNames, StringComparer.OrdinalIgnoreCase),
            TotalToolCalls = state.TotalToolCalls
        };

        var decision = await _runtimeHookDispatcher.DispatchBeforeStopAsync(stopContext, ct).ConfigureAwait(false)
            ?? BeforeStopHookResult.None;
        if (!decision.Continue)
        {
            return StopHookContinuationResult.None;
        }

        var maxAttempts = Math.Max(
            1,
            decision.MaxContinuationAttempts ?? request.AdapterHints?.MaxRecoveryAttempts ?? DefaultStopHookContinuationLimit);
        var attempt = state.StopHookContinuationCount + 1;
        if (state.StopHookContinuationCount >= maxAttempts)
        {
            var detailCode = string.IsNullOrWhiteSpace(decision.ExhaustionDetailCode)
                ? QueryTerminalDetailCodes.RecoveryExhausted
                : decision.ExhaustionDetailCode.Trim();
            var exhaustionMessage = string.IsNullOrWhiteSpace(decision.ExhaustionMessage)
                ? $"Stop hook continuation limit reached after {maxAttempts} attempt(s)."
                : decision.ExhaustionMessage.Trim();
            var finalSummary = BuildStopHookFailureSummary(
                decision,
                detailCode,
                exhaustionMessage,
                maxAttempts,
                state);

            state.TerminationReason = QueryTerminationReason.RecoveryExhausted;
            state.TerminalDetailCode = detailCode;
            state.Flags |= string.Equals(detailCode, QueryTerminalDetailCodes.RequiredToolContractViolation, StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(decision.RequiredToolNameForNextRound)
                    ? RuntimeState.RequiredToolContractRecoveryUsed
                    : RuntimeState.ZeroToolCallRecoveryUsed;

            if (!string.IsNullOrWhiteSpace(decision.ExhaustionDetailCode))
            {
                await EmitEventAsync(eventSink, new ErrorEvent(
                    Seq: seqBase + 969,
                    QueryId: queryId,
                    SessionId: request.SessionId,
                    EntryPoint: request.EntryPoint,
                    ErrorType: detailCode,
                    Message: exhaustionMessage)).ConfigureAwait(false);
            }

            await EmitEventAsync(eventSink, new SystemNoticeEvent(
                Seq: seqBase + 970,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                NoticeType: "stop_hook_continuation_limit_reached",
                Content: finalSummary)).ConfigureAwait(false);

            return new StopHookContinuationResult(Continue: true, FinalTextOverride: finalSummary);
        }

        state.StopHookContinuationCount++;
        state.RecoveryCount++;
        state.Flags |= RuntimeState.ZeroToolCallRecoveryUsed;
        state.LastContinueReason = ContinueReasons.StopHookContinuation;
        state.ForceAllowToolCallsNextRound = decision.AllowToolCallsOnNextRound;
        state.ForceDisableToolCallsNextRound = !decision.AllowToolCallsOnNextRound;
        if (!string.IsNullOrWhiteSpace(decision.ExhaustionDetailCode))
        {
            state.Flags |= RuntimeState.RequiredToolContractRecoveryUsed;
        }

        if (decision.AllowToolCallsOnNextRound &&
            !string.IsNullOrWhiteSpace(decision.RequiredToolNameForNextRound))
        {
            state.RequiredToolNameForNextRound = decision.RequiredToolNameForNextRound;
            state.NextRoundOptionOverrides = MergeRuntimeOptionOverrides(
                state.NextRoundOptionOverrides,
                new Dictionary<string, object?>
                {
                    ["ToolMode"] = ChatToolMode.RequireSpecific(decision.RequiredToolNameForNextRound),
                    ["ThinkingEnabled"] = false
                });
        }

        EnsureStopHookContinuationRounds(request, state, decision.AllowToolCallsOnNextRound);

        var feedback = string.IsNullOrWhiteSpace(decision.Message)
            ? "[SYSTEM] Stop hook requested continuation. Continue working, address the hook feedback, then produce a complete final response."
            : $"[SYSTEM] Stop hook requested continuation: {decision.Message.Trim()}";
        state.Messages.Add(new ChatMessage(ChatRole.User, feedback));

        await EmitEventAsync(eventSink, new RecoveryTriggeredEvent(
            Seq: seqBase + 971 + attempt,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            RecoveryType: "stop_hook_continuation",
            Attempt: attempt,
            Reason: decision.Reason ?? "stop hook requested continuation before final response")).ConfigureAwait(false);

        await EmitEventAsync(eventSink, new SystemNoticeEvent(
            Seq: seqBase + 981 + attempt,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            NoticeType: "stop_hook_continuation",
            Content: $"停止前检查要求继续处理，runtime 已进入恢复轮（{attempt}/{maxAttempts}）。")).ConfigureAwait(false);

        return StopHookContinuationResult.ContinueRound;
    }

    private async Task<bool> TryHandleEmptyResponseRecoveryAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        string assistantText,
        IReadOnlyList<FunctionCallContent> toolCalls,
        IReadOnlyList<AIFunction>? currentTools,
        CancellationToken ct)
    {
        if (_recoveryPolicy == null || request.AdapterHints?.EnableEmptyResponseRecovery != true)
        {
            return false;
        }

        var decision = _recoveryPolicy.DetectRecoveryNeeded(
            state,
            request,
            new RecoveryContext(
                LastResponseText: assistantText,
                LastToolCalls: toolCalls,
                ContextChars: state.Messages.Sum(m => m.Text?.Length ?? 0)));

        if (decision.Type != RecoveryType.EmptyResponse)
        {
            return false;
        }

        var preferAnalysisSynthesis = ShouldPreferSynthesisForReadOnlyAnalysis(request, state, assistantText);
        var allowToolCallsOnNextRound = request.EnableTools &&
            currentTools is { Count: > 0 } &&
            !ShouldForceSynthesisOnlyRetry(state) &&
            !preferAnalysisSynthesis;
        var promptOverride = BuildEmptyResponseRecoveryPrompt(
            request,
            currentTools,
            state,
            allowToolCallsOnNextRound,
            preferAnalysisSynthesis);
        if (ShouldApplyStabilizingRetryOptions(
                allowToolCallsOnNextRound,
                preferAnalysisSynthesis,
                state))
        {
            state.NextRoundOptionOverrides = MergeRuntimeOptionOverrides(
                state.NextRoundOptionOverrides,
                BuildEmptyResponseRetryOptionOverrides(allowToolCallsOnNextRound));
        }

        return await ApplyRecoveryDecisionAsync(
            request,
            state,
            eventSink,
            queryId,
            seqBase,
            decision,
            "empty_response",
            RuntimeState.EmptyResponseRecoveryUsed,
            ContinueReasons.EmptyResponseRecovery,
            "LLM 连续返回空响应，已超过 runtime 自动恢复上限。",
            promptOverride,
            allowToolCallsOnNextRound,
            ct).ConfigureAwait(false);
    }

    private async Task<bool> TryRecoverInsufficientVisibleAnswerAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        string assistantText,
        string thinkingText,
        List<FunctionCallContent> toolCalls,
        CancellationToken ct)
    {
        if (toolCalls.Count > 0 ||
            !ShouldRecoverInsufficientVisibleAnswer(request, state, assistantText, thinkingText))
        {
            return false;
        }

        var maxAttempts = Math.Max(1, request.AdapterHints?.MaxRecoveryAttempts ?? 3);
        var attempt = state.InsufficientVisibleAnswerRecoveryCount + 1;
        if (attempt > maxAttempts)
        {
            return false;
        }

        state.InsufficientVisibleAnswerRecoveryCount = attempt;
        state.RecoveryCount++;
        state.Flags |= RuntimeState.ZeroToolCallRecoveryUsed;
        state.ForceAllowToolCallsNextRound = false;
        state.ForceDisableToolCallsNextRound = true;
        EnsureSynthesisOnlyRecoveryRound(state);
        state.NextRoundOptionOverrides = MergeRuntimeOptionOverrides(
            state.NextRoundOptionOverrides,
            BuildEmptyResponseRetryOptionOverrides(allowToolCallsOnNextRound: false));

        var correction = BuildInsufficientVisibleAnswerRecoveryPrompt(request, state, assistantText);
        state.Messages.Add(new ChatMessage(ChatRole.User, correction));

        _logger.LogInformation(
            "QueryRuntimeEngine[{EntryPoint}] recovered insufficient visible answer for session {SessionId} on round {Round}. attempt={Attempt}/{MaxAttempts}",
            request.EntryPoint,
            request.SessionId,
            state.Round,
            attempt,
            maxAttempts);

        _telemetry?.RecordRecovery(new QueryLoopRecovery(
            queryId,
            request.SessionId,
            request.EntryPoint,
            state.Round,
            "insufficient_visible_answer",
            attempt,
            Continued: true,
            Terminal: false));

        await EmitEventAsync(eventSink, new RecoveryTriggeredEvent(
            Seq: seqBase + 908 + attempt,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            RecoveryType: "insufficient_visible_answer",
            Attempt: attempt,
            Reason: "assistant returned a visible lead-in instead of a complete final answer")).ConfigureAwait(false);

        await EmitEventAsync(eventSink, new SystemNoticeEvent(
            Seq: seqBase + 933 + attempt,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            NoticeType: "tool_use_correction",
            Content: correction)).ConfigureAwait(false);

        if (request.AdapterHints?.EnableTransportFailureRecovery == true)
        {
            await Task.Yield();
        }

        return true;
    }

    private async Task<bool> TryHandleMalformedProtocolRecoveryAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        IReadOnlyList<FunctionCallContent> toolCalls,
        CancellationToken ct)
    {
        if (_recoveryPolicy == null ||
            request.AdapterHints?.EnableMalformedProtocolRecovery != true ||
            !toolCalls.Any(IsMalformedToolCall))
        {
            return false;
        }

        state.MalformedProtocolCount++;
        var decision = _recoveryPolicy.DetectRecoveryNeeded(
            state,
            request,
            new RecoveryContext(
                LastToolCalls: toolCalls,
                ContextChars: state.Messages.Sum(m => m.Text?.Length ?? 0)));

        return await ApplyRecoveryDecisionAsync(
            request,
            state,
            eventSink,
            queryId,
            seqBase,
            decision,
            "malformed_protocol",
            RuntimeState.MalformedProtocolRecoveryUsed,
            ContinueReasons.MalformedProtocolRecovery,
            "模型连续返回无效工具调用协议，已超过 runtime 自动恢复上限。",
            promptOverride: "上一轮工具调用协议无效。下一轮必须重新发出格式正确的工具调用；不要只解释格式问题，也不要把工具调用写成普通文本。",
            allowToolCallsOnNextRound: true,
            ct).ConfigureAwait(false);
    }

    private async Task<bool> TryHandleZeroToolCallRecoveryAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        string assistantText,
        List<FunctionCallContent> toolCalls,
        CancellationToken ct)
    {
        if (_recoveryPolicy == null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(assistantText) || toolCalls.Count > 0)
        {
            return false;
        }

        var decision = _recoveryPolicy.DetectRecoveryNeeded(
            state,
            request,
            new RecoveryContext(
                LastResponseText: assistantText,
                LastToolCalls: toolCalls,
                ContextChars: state.Messages.Sum(m => m.Text?.Length ?? 0)));

        if (decision.Type != RecoveryType.ZeroToolCall || !decision.NeedsRecovery)
        {
            return false;
        }

        return await ApplyRecoveryDecisionAsync(
            request,
            state,
            eventSink,
            queryId,
            seqBase,
            decision,
            "zero_tool_call",
            RuntimeState.ZeroToolCallRecoveryUsed,
            ContinueReasons.ZeroToolCallRecovery,
            "模型连续未发起任何工具调用，已超过 runtime 自动恢复上限。",
            promptOverride: null,
            allowToolCallsOnNextRound: false,
            ct).ConfigureAwait(false);
    }

    private async Task<bool> TryHandleStallRecoveryAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        string assistantText,
        IReadOnlyList<FunctionCallContent> toolCalls,
        CancellationToken ct)
    {
        if (_recoveryPolicy == null || request.AdapterHints?.EnableStallDetection != true)
        {
            return false;
        }

        var decision = _recoveryPolicy.DetectRecoveryNeeded(
            state,
            request,
            new RecoveryContext(
                LastResponseText: assistantText,
                LastToolCalls: toolCalls,
                ContextChars: state.Messages.Sum(m => m.Text?.Length ?? 0),
                ConsecutiveSameToolCount: state.ConsecutiveSameToolCount));

        if (decision.Type != RecoveryType.StallDetected || !decision.NeedsRecovery)
        {
            return false;
        }

        return await ApplyRecoveryDecisionAsync(
            request,
            state,
            eventSink,
            queryId,
            seqBase,
            decision,
            "stall_detected",
            RuntimeState.StallDetected,
            ContinueReasons.UrgencyPromptInjected,
            "模型连续重复相同工具调用，已超过 runtime 自动恢复上限。",
            promptOverride: null,
            allowToolCallsOnNextRound: false,
            ct).ConfigureAwait(false);
    }

    private async Task<bool> ApplyRecoveryDecisionAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        RecoveryDecision decision,
        string recoveryType,
        RuntimeState recoveryFlag,
        string continueReason,
        string exhaustedMessage,
        string? promptOverride,
        bool allowToolCallsOnNextRound,
        CancellationToken ct)
    {
        var result = await _recoveryDecisionApplier.ApplyAsync(
            new QueryRecoveryApplicationRequest
            {
                RuntimeRequest = request,
                State = state,
                EventSink = eventSink,
                QueryId = queryId,
                SeqBase = seqBase,
                Decision = decision,
                RecoveryType = recoveryType,
                RecoveryFlag = recoveryFlag,
                ContinueReason = continueReason,
                ExhaustedMessage = exhaustedMessage,
                PromptOverride = promptOverride,
                AllowToolCallsOnNextRound = allowToolCallsOnNextRound
            },
            ct).ConfigureAwait(false);

        return result.Handled;
    }

    private static string BuildEmptyResponseRecoveryPrompt(
        QueryRuntimeRequest request,
        IReadOnlyList<AIFunction>? currentTools,
        QueryRuntimeState state,
        bool allowToolCallsOnNextRound,
        bool preferAnalysisSynthesis)
    {
        var latestOriginalUserPrompt = request.InitialMessages
            .LastOrDefault(message => message.Role == ChatRole.User)?
            .Text;
        var toolNames = currentTools?
            .Select(tool => tool.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var remainingRoundsAfterCurrent = Math.Max(0, state.MaxRounds - state.Round - 1);
        var nextRoundIsFinalUsefulChance = remainingRoundsAfterCurrent <= 1;
        var repeatedReadGuidance = BuildRepeatedReadGuidance(request, state);
        var preferSynthesisOverMoreExploration =
            state.ConsecutiveToolOnlyRounds >= 2 ||
            state.ConsecutiveRepeatedReadRounds >= 1 ||
            HasRepeatedReadEvidence(state) ||
            nextRoundIsFinalUsefulChance;

        var sb = new StringBuilder();
        sb.Append("你刚刚返回了空响应，这是无效的。下一条消息必须是非空内容。");
        if (!string.IsNullOrWhiteSpace(latestOriginalUserPrompt))
        {
            sb.Append("请重新处理最初的用户请求，不要忽略它。原始用户请求如下：\n");
            sb.Append(latestOriginalUserPrompt);
            sb.Append('\n');
        }

        if (request.EnableTools && toolNames.Length > 0)
        {
            if (!allowToolCallsOnNextRound)
            {
                sb.Append("你前面已经拿到了足够的工具证据。下一轮不会再开放工具。");
                sb.Append("请直接基于已有会话、现有证据和先前工具摘要给出最终结果。");
                sb.Append("如果仍有不确定点，先给出当前最可能的结论，再明确列出剩余不确定点。");
                sb.Append("下一条消息必须直接从最终答案正文开始，不要再写“让我继续”“我将先”“接下来我会”这类导语。");
                sb.Append("正文至少要给出一组明确结论、观察点或检查结果，不能只返回思考、空白或占位语。");
                if (preferAnalysisSynthesis)
                {
                    sb.Append("这是一次只读分析/审查请求，当前已有证据足够支持先回答用户，不要继续扩张读取。");
                    sb.Append("优先输出结构化观察/发现，并在每点后补充证据位置或剩余不确定点。");
                }
                var recoverySummaryExcerpt = BuildRecoverySummaryExcerpt(state.LastToolBatchSummaryPrompt);
                if (!string.IsNullOrWhiteSpace(recoverySummaryExcerpt))
                {
                    sb.Append("\n\n上一轮工具批次摘要（供你直接综合结论，不要继续扩张读取）：\n");
                    sb.Append(recoverySummaryExcerpt);
                }

                if (!string.IsNullOrWhiteSpace(repeatedReadGuidance))
                {
                    sb.Append("\n\n");
                    sb.Append(repeatedReadGuidance);
                }

                sb.Append("不要继续读取文件、搜索、列计划，也不要输出“让我继续读取/搜索/检查”。");
            }
            else
            {
                sb.Append("当前可用工具只有：");
                sb.Append(string.Join(", ", toolNames.Select(name => $"`{name}`")));
                sb.Append('。');

                if (preferSynthesisOverMoreExploration)
                {
                    if (state.ConsecutiveToolOnlyRounds >= 2)
                    {
                        sb.Append($"你已经连续 {state.ConsecutiveToolOnlyRounds} 轮只调用工具而没有给出可见结论。");
                    }

                    if (state.ConsecutiveRepeatedReadRounds >= 1)
                    {
                        sb.Append($"你已经连续 {state.ConsecutiveRepeatedReadRounds} 轮重复读取未变化的证据。");
                    }

                    sb.Append("优先基于已有会话、先前工具摘要和现有证据直接综合结论。");
                    sb.Append("默认不要重复读取相同文件、目录或搜索结果，也不要为了补齐细枝末节继续扩张范围。");
                    if (nextRoundIsFinalUsefulChance)
                    {
                        sb.Append("当前已接近最后一个可用轮次。除非确实缺少单个关键证据，否则不要再次调用工具；若必须调用，也只能做一组最小必要调用，然后立即收尾。");
                    }
                    else
                    {
                        sb.Append("只有在还缺少单个关键证据时，才允许再调用一组必要工具，并在拿到结果后立刻收尾。");
                    }
                }
                else
                {
                    sb.Append("如果你需要证据、文件系统信息或命令结果，必须直接发起工具调用。");
                }

                if (!string.IsNullOrWhiteSpace(repeatedReadGuidance))
                {
                    sb.Append(repeatedReadGuidance);
                }

                sb.Append("不要只说“我将调用工具”或“让我继续读取...”，不要只思考，也不要输出空白。");
            }
        }
        else
        {
            sb.Append("如果无需工具，请直接给出可见文本回答；如果信息不足，请明确提出问题。");
        }

        return sb.ToString();
    }

    private static bool ShouldRecoverInsufficientVisibleAnswer(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        string assistantText,
        string thinkingText)
    {
        if (!IsReadOnlyAnalysisRequest(request) ||
            string.IsNullOrWhiteSpace(assistantText) ||
            !ShouldForceReadOnlyAnalysisSynthesisRound(request, state) && !ShouldPreferSynthesisForReadOnlyAnalysis(request, state, assistantText))
        {
            return false;
        }

        var trimmed = assistantText.Trim();
        if (trimmed.Length >= 160)
        {
            return false;
        }

        if (trimmed.Contains('\n'))
        {
            return false;
        }

        if (trimmed.Length >= 80 && thinkingText.Length < 1200)
        {
            return false;
        }

        return LooksLikeLeadInInsteadOfFinalAnswer(trimmed);
    }

    private static bool LooksLikeLeadInInsteadOfFinalAnswer(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.EndsWith('。') || trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('！'))
        {
            return false;
        }

        var hasLeadIn =
            trimmed.StartsWith("我先", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("我将", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("让我", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("现在我需要", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("我已有足够", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("let me", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("i need", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("i have enough", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("继续", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("再补充", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("补充两个关键文件", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("形成结论", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("形成洞察", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("深入", StringComparison.OrdinalIgnoreCase);

        if (!hasLeadIn)
        {
            return false;
        }

        return trimmed.EndsWith('：') ||
               trimmed.EndsWith(':') ||
               trimmed.EndsWith('，') ||
               trimmed.EndsWith(',') ||
               trimmed.EndsWith('的') ||
               trimmed.EndsWith('了') ||
               trimmed.EndsWith("关键", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith("文件", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith("结论", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith("洞察", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildInsufficientVisibleAnswerRecoveryPrompt(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        string assistantText)
    {
        var sb = new StringBuilder();
        sb.Append("你刚刚输出的是未完成的导语或半句，这不能作为最终答案。");
        sb.Append("下一条消息必须直接给出完整结论，不要再写“我先”“让我继续”“再补充几个文件”。");

        var latestOriginalUserPrompt = request.InitialMessages
            .LastOrDefault(message => message.Role == ChatRole.User)?
            .Text;
        if (!string.IsNullOrWhiteSpace(latestOriginalUserPrompt))
        {
            sb.Append("\n原始用户请求：\n");
            sb.Append(latestOriginalUserPrompt.Trim());
        }

        var recoverySummaryExcerpt = BuildRecoverySummaryExcerpt(state.LastToolBatchSummaryPrompt);
        if (!string.IsNullOrWhiteSpace(recoverySummaryExcerpt))
        {
            sb.Append("\n\n可直接用于综合结论的最近工具摘要：\n");
            sb.Append(recoverySummaryExcerpt);
        }

        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            sb.Append("\n\n你上一条未完成的开头是：");
            sb.Append(QuoteIfNeeded(assistantText.Trim()));
            sb.Append("。请不要续写这个半句，而是重写成完整最终答案。");
        }

        sb.Append("\n请直接输出 3-7 条完整观察/结论；每条尽量附上证据位置或不确定点。");
        return sb.ToString().Trim();
    }

    private static bool ShouldApplyStabilizingRetryOptions(
        bool allowToolCallsOnNextRound,
        bool preferAnalysisSynthesis,
        QueryRuntimeState state)
        => !allowToolCallsOnNextRound ||
           preferAnalysisSynthesis ||
           state.EmptyResponseCount >= 1 ||
           state.ConsecutiveToolOnlyRounds >= 2;

    private static Dictionary<string, object?> BuildEmptyResponseRetryOptionOverrides(
        bool allowToolCallsOnNextRound)
    {
        return allowToolCallsOnNextRound
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["temperature"] = 0.35f,
                ["top_p"] = 0.80f
            }
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["temperature"] = 0.20f,
                ["top_p"] = 0.70f,
                ["max_output_tokens"] = 4096
            };
    }

    private static IReadOnlyDictionary<string, object?> MergeRuntimeOptionOverrides(
        IReadOnlyDictionary<string, object?>? existing,
        Dictionary<string, object?> additions)
    {
        if (additions.Count == 0)
        {
            return existing ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (existing is { Count: > 0 })
        {
            foreach (var pair in existing)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        foreach (var pair in additions)
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    private static IReadOnlyDictionary<string, object?>? RemoveRuntimeOptionOverrides(
        IReadOnlyDictionary<string, object?>? existing,
        params string[] keys)
    {
        if (existing is not { Count: > 0 } || keys.Length == 0)
        {
            return existing;
        }

        var removeSet = new HashSet<string>(keys.Select(NormalizeOptionKey), StringComparer.OrdinalIgnoreCase);
        var remaining = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in existing)
        {
            if (!removeSet.Contains(NormalizeOptionKey(pair.Key)))
            {
                remaining[pair.Key] = pair.Value;
            }
        }

        return remaining.Count == 0 ? null : remaining;
    }

    private static bool IsMalformedToolCall(FunctionCallContent call)
    {
        return string.IsNullOrWhiteSpace(call.Name);
    }

    private static void UpdateConsecutiveToolCallState(
        QueryRuntimeState state,
        IReadOnlyList<FunctionCallContent> toolCalls)
    {
        var signature = BuildRoundToolSignature(toolCalls);
        if (string.IsNullOrWhiteSpace(signature))
        {
            state.ConsecutiveSameToolCount = 0;
            state.LastToolSignature = null;
            return;
        }

        if (string.Equals(state.LastToolSignature, signature, StringComparison.Ordinal))
        {
            state.ConsecutiveSameToolCount++;
        }
        else
        {
            state.LastToolSignature = signature;
            state.ConsecutiveSameToolCount = 1;
        }
    }

    private static string BuildRoundToolSignature(IReadOnlyList<FunctionCallContent> toolCalls)
    {
        return string.Join("||", toolCalls.Select(BuildToolCallSignature));
    }

    private static string BuildToolCallSignature(FunctionCallContent call)
    {
        var arguments = call.Arguments != null
            ? BuildStableJsonLikeString(call.Arguments)
            : "{}";

        return $"{call.Name?.Trim() ?? string.Empty}:{arguments}";
    }

    private static IReadOnlyList<AIFunction> FilterToolsForRequiredRecoveryTool(
        IReadOnlyList<AIFunction> currentTools,
        string? requiredToolName)
    {
        if (string.IsNullOrWhiteSpace(requiredToolName))
        {
            return currentTools;
        }

        var filtered = currentTools
            .Where(tool => string.Equals(tool.Name, requiredToolName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return filtered.Length > 0 ? filtered : currentTools;
    }

    private static bool IsNonRequiredToolCall(FunctionCallContent call, string? requiredToolName)
    {
        return !string.IsNullOrWhiteSpace(requiredToolName) &&
               !string.Equals(call.Name, requiredToolName, StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendPendingRoundPrompt(QueryRuntimeState state, string prompt)
    {
        state.PendingToolBatchSummaryPrompt = string.IsNullOrWhiteSpace(state.PendingToolBatchSummaryPrompt)
            ? prompt
            : $"{state.PendingToolBatchSummaryPrompt}\n\n{prompt}";
    }

    private static void AppendRuntimeRecoveryHint(
        QueryRuntimeState state,
        string source,
        int attempt,
        string? requiredToolName,
        bool toolCallRequired,
        string? message,
        IReadOnlyList<string>? candidateFiles = null)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        state.RecoveryHints.Add(new RuntimeRecoveryHint
        {
            Source = source,
            Attempt = Math.Max(0, attempt),
            RequiredToolName = string.IsNullOrWhiteSpace(requiredToolName) ? null : requiredToolName,
            ToolCallRequired = toolCallRequired,
            Message = string.IsNullOrWhiteSpace(message) ? null : CompactForRecoveryContext(message, 700),
            CandidateFiles = candidateFiles?
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray() ?? []
        });

        if (state.RecoveryHints.Count > 8)
        {
            state.RecoveryHints.RemoveRange(0, state.RecoveryHints.Count - 8);
        }
    }

    private static void AppendRuntimeRecoveryHint(
        QueryRuntimeState state,
        RuntimeRecoveryHint hint)
    {
        ArgumentNullException.ThrowIfNull(hint);
        AppendRuntimeRecoveryHint(
            state,
            hint.Source,
            hint.Attempt,
            hint.RequiredToolName,
            hint.ToolCallRequired,
            hint.Message,
            hint.CandidateFiles);
    }

    private static string[] ResolveRecoveryCandidateFiles(QueryRuntimeState state)
    {
        var files = new List<string>();
        files.AddRange(state.EvidenceLedger.PendingModifications
            .SelectMany(static evidence => evidence.CandidateFiles));
        files.AddRange(GetKnownFileEvidence(state).Select(static evidence => evidence.FilePath));

        return files
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private static List<FileEvidence> GetKnownFileEvidence(QueryRuntimeState state)
    {
        if (state.EvidenceLedger.Files.Count > 0)
        {
            return state.EvidenceLedger.Files;
        }

        return [];
    }

    private static void UpdateToolOnlyRoundState(QueryRuntimeState state, bool hasVisibleAssistantText)
    {
        if (hasVisibleAssistantText)
        {
            state.ConsecutiveToolOnlyRounds = 0;
            return;
        }

        state.ConsecutiveToolOnlyRounds++;
    }

    private static string? BuildRepeatedReadGuidance(QueryRuntimeRequest request, QueryRuntimeState state)
    {
        var repeatedReadTargets = GetRepeatedReadTargets(state);
        if (repeatedReadTargets.Length == 0)
        {
            return null;
        }

        var targets = string.Join(", ", repeatedReadTargets
            .Take(4)
            .Select(static target => $"`{target}`"));
        var sb = new StringBuilder();
        sb.Append("本轮再次读取了这些未变化的文件/证据：");
        sb.Append(targets);
        sb.Append("。这些目标的指纹或读取范围与前面一致，视为已知证据，不要再次读取。");
        if (state.ConsecutiveRepeatedReadRounds >= 2)
        {
            sb.Append($"你已经连续 {state.ConsecutiveRepeatedReadRounds} 轮重复读取未变化证据。");
            if (request.EntryPoint == QueryLoopEntryPoint.ForgeWorker && !IsReadOnlyAnalysisRequest(request))
            {
                sb.Append("下一轮默认停止读取并基于已有快照调用写工具落地修改；如果还有缺口，只允许补充一个新的关键证据点。");
            }
            else
            {
                sb.Append("下一轮默认直接给出诊断、计划或结论；如果还有缺口，只允许补充新的证据点。");
            }
        }
        else
        {
            sb.Append("请先基于已有证据给出当前结论；如果仍有缺口，只能补充新的证据点。");
        }

        return sb.ToString();
    }

    private static string[] GetRepeatedReadTargets(QueryRuntimeState state)
        => state.EvidenceLedger.RepeatedEvidenceKeys
            .Where(static target => !string.IsNullOrWhiteSpace(target))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool HasRepeatedReadEvidence(QueryRuntimeState state)
        => state.EvidenceLedger.RepeatedEvidenceKeys.Count > 0;

    private static void ClearRepeatedReadEvidence(QueryRuntimeState state)
    {
        state.ConsecutiveRepeatedReadRounds = 0;
        state.EvidenceLedger.RepeatedEvidenceKeys.Clear();
    }

    private static bool TryGetMetadataString(object? metadata, string[] keys, out string? value)
    {
        value = null;
        switch (metadata)
        {
            case null:
                return false;
            case JObject jobject:
                foreach (var key in keys)
                {
                    if (jobject.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token))
                    {
                        value = token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None);
                        return !string.IsNullOrWhiteSpace(value);
                    }
                }

                return false;
            case JsonElement element:
                return TryGetJsonElementString(element, keys, out value);
            case IReadOnlyDictionary<string, object?> readOnly:
                return TryGetDictionaryString(readOnly, keys, out value);
            case IDictionary<string, object?> dictionary:
                return TryGetDictionaryString(dictionary, keys, out value);
            case IReadOnlyDictionary<string, string?> stringReadOnly:
                return TryGetDictionaryString(stringReadOnly, keys, out value);
            case IDictionary<string, string?> stringDictionary:
                return TryGetDictionaryString(stringDictionary, keys, out value);
            default:
                return false;
        }
    }

    private static bool ShouldForceSynthesisOnlyRetry(QueryRuntimeState state)
    {
        var remainingRoundsAfterCurrent = Math.Max(0, state.MaxRounds - state.Round - 1);
        var nextRoundIsFinalUsefulChance = remainingRoundsAfterCurrent <= 1;
        if (!nextRoundIsFinalUsefulChance)
        {
            return false;
        }

        return state.TotalToolCalls > 0 ||
            !string.IsNullOrWhiteSpace(state.LastToolBatchSummaryPrompt) ||
            state.ConsecutiveToolOnlyRounds > 0 ||
            state.ConsecutiveRepeatedReadRounds > 0;
    }

    private static bool ShouldForceReadOnlyAnalysisSynthesisRound(
        QueryRuntimeRequest request,
        QueryRuntimeState state)
    {
        if (!IsReadOnlyAnalysisRequest(request) ||
            state.TotalWriteToolCalls > 0 ||
            !HasSubstantialReadOnlyAnalysisEvidence(state))
        {
            return false;
        }

        var remainingRoundsAfterCurrent = Math.Max(0, state.MaxRounds - state.Round - 1);
        if (remainingRoundsAfterCurrent <= 1)
        {
            return true;
        }

        if (state.TotalToolCalls >= 8)
        {
            return true;
        }

        if (remainingRoundsAfterCurrent <= 2 && state.TotalToolCalls >= 6)
        {
            return true;
        }

        return remainingRoundsAfterCurrent <= 3 &&
               (state.TotalToolCalls >= 8 ||
                state.ConsecutiveRepeatedReadRounds > 0 ||
                HasRepeatedReadEvidence(state));
    }

    private static bool ShouldPreferSynthesisForReadOnlyAnalysis(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        string? assistantText)
    {
        if (!IsReadOnlyAnalysisRequest(request) || !HasSubstantialReadOnlyAnalysisEvidence(state))
        {
            return false;
        }

        if (state.ConsecutiveRepeatedReadRounds > 0 ||
            HasRepeatedReadEvidence(state) ||
            state.ConsecutiveToolOnlyRounds > 0 ||
            state.EmptyResponseCount > 0 ||
            state.RecoveryCount > 0)
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(assistantText) ||
               ContainsReadOrExploreIntentWithoutToolCall(assistantText);
    }

    private static string BuildToolResultTranscript(ToolExecutionResult result)
    {
        var rawResult = result.Result ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawResult))
        {
            return rawResult;
        }

        var normalized = NormalizeToolResultText(rawResult);
        var maxChars = GetToolResultTranscriptMaxChars(result);
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        var preview = normalized[..maxChars].TrimEnd();
        var summary = SummarizeToolExecutionResult(result);
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.Append(summary);
            sb.AppendLine();
            sb.AppendLine();
        }

        sb.Append("[tool output truncated for runtime context; keep only preview]");
        sb.AppendLine();
        sb.Append(preview);
        sb.Append("...");
        return sb.ToString();
    }

    private static string BuildSyntheticToolResultTranscript(
        string toolName,
        string? reason,
        string fallbackReason)
    {
        var message = string.IsNullOrWhiteSpace(reason) ? fallbackReason : reason.Trim();
        return $"[runtime synthetic tool_result] {toolName}: {message}";
    }

    private static string ResolveToolCallName(FunctionCallContent call)
    {
        if (ToolCallSyntaxRecovery.TryNormalizeInlineInvocation(call.Name, call.Arguments, out var recoveredToolName, out _))
        {
            return recoveredToolName;
        }

        return call.Name ?? "unknown";
    }

    private static string BuildSuppressedWrapUpToolCallsMessage(
        IReadOnlyList<FunctionCallContent> toolCalls,
        int continuationsUsed)
        => $"wrap-up 收尾阶段已扩展 {continuationsUsed} 次仍继续触发工具调用（{FormatToolNames(toolCalls)}），runtime 已抑制本轮执行并结束。请基于已收集的证据给出最终结论；如需继续探索，请通过新的会话或调高 MaxRounds 后重试。";

    private static void ApplyMaxRoundsReachedFeedback(
        QueryRuntimeState state,
        List<FunctionCallContent> pendingToolCalls)
    {
        var message = pendingToolCalls.Count > 0
            ? $"已达到 runtime 最大轮次；最后一轮仍包含工具调用（{FormatToolNames(pendingToolCalls)}），工具结果已记录，但没有剩余轮次生成最终总结。请继续会话或提高 MaxRounds。"
            : "已达到 runtime 最大轮次，未能生成完整最终答复。请继续会话或提高 MaxRounds。";

        state.LastAssistantText.Clear();
        state.LastAssistantText.Append(message);
        state.LastNonEmptyAssistantText.Clear();
        state.LastNonEmptyAssistantText.Append(message);
    }

    private static string BuildStopHookFailureSummary(
        BeforeStopHookResult decision,
        string detailCode,
        string exhaustionMessage,
        int maxAttempts,
        QueryRuntimeState state)
    {
        var reason = FirstNonEmpty(
            decision.ExhaustionMessage,
            decision.Reason,
            exhaustionMessage,
            detailCode);
        var executedTools = state.ExecutedToolNames.Count == 0
            ? "无"
            : string.Join(", ", state.ExecutedToolNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        var successfulTools = state.SuccessfulToolNames.Count == 0
            ? "无"
            : string.Join(", ", state.SuccessfulToolNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

        var builder = new StringBuilder();
        builder.AppendLine("任务未完成：停止前检查多次要求继续处理，但自动恢复已达到上限。");
        builder.AppendLine();
        builder.AppendLine($"失败原因：{reason}");
        builder.AppendLine($"恢复尝试：{maxAttempts} 次");
        builder.AppendLine($"工具调用：共 {state.TotalToolCalls} 次；已执行工具：{executedTools}；成功工具：{successfulTools}");
        builder.Append("建议：请查看详细日志中的 stop hook / recovery / tool-call 事件，确认缺少的工具调用或验证步骤后重试。");
        return builder.ToString();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "停止前检查未通过。";
    }

    private static string FormatToolNames(IReadOnlyList<FunctionCallContent> toolCalls)
    {
        if (toolCalls.Count == 0)
        {
            return "none";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var call in toolCalls)
        {
            var name = string.IsNullOrWhiteSpace(call.Name) ? "unknown" : call.Name.Trim();
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        return string.Join(", ", names);
    }

    private static bool TryExtendWrapUpToolContinuation(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        out int attempt,
        out int maxAttempts)
    {
        maxAttempts = Math.Max(1, request.AdapterHints?.MaxRecoveryAttempts ?? DefaultWrapUpToolContinuationLimit);
        attempt = state.WrapUpToolContinuationCount + 1;
        if (state.WrapUpToolContinuationCount >= maxAttempts)
        {
            return false;
        }

        state.WrapUpToolContinuationCount++;
        if ((state.Round + 1) >= state.MaxRounds - 1)
        {
            state.MaxRounds++;
        }

        return true;
    }

    private static void EnsureStopHookContinuationRounds(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        bool allowToolCallsOnNextRound)
    {
        var requiredMaxRounds = allowToolCallsOnNextRound && request.EnableTools
            ? state.Round + 3
            : state.Round + 2;
        if (state.MaxRounds < requiredMaxRounds)
        {
            state.MaxRounds = requiredMaxRounds;
        }
    }

    private static string? BuildToolBatchSummaryPrompt(
        List<FunctionCallContent> toolCalls,
        List<ToolExecutionResult> executionResults,
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        bool requireFinalAnswerOnNextRound)
    {
        if (toolCalls.Count == 0 || executionResults.Count == 0)
        {
            return null;
        }

        var itemCount = Math.Min(toolCalls.Count, executionResults.Count);
        var sb = new StringBuilder();
        sb.AppendLine("[SYSTEM] 上一轮工具批次摘要：");
        for (var i = 0; i < itemCount; i++)
        {
            sb.Append(i + 1);
            sb.Append(". ");
            sb.Append(BuildToolInvocationLabel(toolCalls[i]));
            sb.Append(" -> ");
            sb.AppendLine(SummarizeToolExecutionResultForBatchPrompt(executionResults[i]));
        }

        sb.AppendLine();
        if (requireFinalAnswerOnNextRound)
        {
            sb.Append("下一轮必须基于已有工具结果与上下文直接给出最终结果，不要再次调用工具。");
            return sb.ToString().TrimEnd();
        }

        var repeatedReadGuidance = BuildRepeatedReadGuidance(request, state);
        if (!string.IsNullOrWhiteSpace(repeatedReadGuidance))
        {
            sb.AppendLine(repeatedReadGuidance);
        }

        if (state.ConsecutiveToolOnlyRounds >= 2 || state.ConsecutiveRepeatedReadRounds >= 2)
        {
            if (state.ConsecutiveToolOnlyRounds >= 2)
            {
                sb.Append($"你已经连续 {state.ConsecutiveToolOnlyRounds} 轮只调用工具而没有给出可见结论。");
            }

            if (state.ConsecutiveRepeatedReadRounds >= 2)
            {
                sb.Append($"你已经连续 {state.ConsecutiveRepeatedReadRounds} 轮重复读取未变化的文件/证据。");
            }

            sb.Append("默认直接总结并回答用户；只有确实缺少关键证据时，才允许再调用一组必要工具，并明确说明缺口。");
            sb.Append("不要为了补齐截断片段或重复验证已知事实而继续扩张读取范围。");
        }
        else
        {
            sb.Append("请先利用以上摘要形成结论。");
            sb.Append("如果这些结果已经足够回答用户，就直接给出最终结果。");
            sb.Append("只有在仍缺少关键证据时，才继续调用必要工具。");
        }

        if (ShouldPromoteConcreteFileFollowUp(toolCalls, sb.ToString()))
        {
            sb.Append("如果上面的摘要、索引或搜索结果已经给出了真实文件路径，下一轮优先直接读取这些具体文件。");
            sb.Append("不要把问题扩写成未经确认的同义词、架构术语或猜测命名，也不要仅凭文件名/命中数就下结构结论。");
            sb.Append("对某个子系统形成判断前，至少读取一个对应源码文件。");
        }

        sb.Append("不要重复读取刚刚已经读取过的相同文件、目录或搜索结果。");
        return sb.ToString().TrimEnd();
    }

    private static string? BuildRecoverySummaryExcerpt(string? toolBatchSummaryPrompt)
    {
        if (string.IsNullOrWhiteSpace(toolBatchSummaryPrompt))
        {
            return null;
        }

        return TruncateForPrompt(
            NormalizeToolResultText(toolBatchSummaryPrompt),
            RecoverySummaryExcerptMaxChars);
    }

    private static string BuildToolInvocationLabel(FunctionCallContent call)
    {
        var toolName = call.Name ?? "unknown";
        var argumentPreview = SummarizeToolArguments(call.Arguments);
        return string.IsNullOrWhiteSpace(argumentPreview)
            ? toolName
            : $"{toolName}({argumentPreview})";
    }

    private static string? SummarizeToolArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments == null || arguments.Count == 0)
        {
            return null;
        }

        var preferredKeys = new[]
        {
            "path",
            "file",
            "filepath",
            "target",
            "query",
            "pattern",
            "symbol",
            "command"
        };
        var previews = new List<string>(capacity: 2);
        foreach (var key in preferredKeys)
        {
            if (arguments.TryGetValue(key, out var value))
            {
                var formatted = FormatToolArgumentValue(value);
                if (!string.IsNullOrWhiteSpace(formatted))
                {
                    previews.Add($"{key}={formatted}");
                }
            }

            if (previews.Count >= 2)
            {
                break;
            }
        }

        if (previews.Count == 0)
        {
            var first = arguments.FirstOrDefault(pair => pair.Value != null);
            if (!string.IsNullOrWhiteSpace(first.Key))
            {
                var formatted = FormatToolArgumentValue(first.Value);
                if (!string.IsNullOrWhiteSpace(formatted))
                {
                    previews.Add($"{first.Key}={formatted}");
                }
            }
        }

        return previews.Count == 0 ? null : string.Join(", ", previews);
    }

    private static string? FormatToolArgumentValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is string text)
        {
            return QuoteIfNeeded(TruncateForPrompt(NormalizeToolResultText(text), ToolArgumentValueMaxChars));
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var items = enumerable
                .Cast<object?>()
                .Take(6)
                .Select(static item => item?.ToString())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item!)
                .ToArray();
            if (items.Length == 0)
            {
                return null;
            }

            return QuoteIfNeeded(TruncateForPrompt(string.Join(" ", items), ToolArgumentValueMaxChars));
        }

        return QuoteIfNeeded(TruncateForPrompt(value.ToString(), ToolArgumentValueMaxChars));
    }

    private static string SummarizeToolExecutionResult(ToolExecutionResult result)
        => SummarizeToolExecutionResult(result, ToolSummaryMaxChars);

    private static string SummarizeToolExecutionResultForBatchPrompt(ToolExecutionResult result)
        => SummarizeToolExecutionResult(result, GetToolSummaryMaxChars(result));

    private static string SummarizeToolExecutionResult(ToolExecutionResult result, int maxChars)
    {
        var preferred = string.IsNullOrWhiteSpace(result.Summary)
            ? NormalizeToolResultText(result.Result)
            : NormalizeToolResultText(result.Summary);
        if (string.IsNullOrWhiteSpace(preferred))
        {
            preferred = result.Success ? "Success" : "Error";
        }

        if (preferred.Length > maxChars)
        {
            preferred = preferred[..(maxChars - 3)] + "...";
        }

        if (result.IsOutputTruncated && !preferred.Contains("truncated", StringComparison.OrdinalIgnoreCase))
        {
            preferred += " [tool already truncated output]";
        }

        return preferred;
    }

    private static int GetToolResultTranscriptMaxChars(ToolExecutionResult result)
    {
        if (IsHashlineReadResult(result))
        {
            return HashlineToolResultTranscriptMaxChars;
        }

        if (IsReadFileTool(result.ToolName))
        {
            return ReadToolResultTranscriptMaxChars;
        }

        return ToolResultTranscriptMaxChars;
    }

    private static int GetToolSummaryMaxChars(ToolExecutionResult result)
    {
        if (IsHashlineReadResult(result))
        {
            return HashlineToolSummaryMaxChars;
        }

        if (IsReadFileTool(result.ToolName))
        {
            return ReadToolSummaryMaxChars;
        }

        return ToolSummaryMaxChars;
    }

    private static bool IsHashlineReadResult(ToolExecutionResult result)
        => IsReadFileTool(result.ToolName) &&
           (ContainsHashlineSnapshotHeader(result.Result) ||
            TryGetMetadataString(
                result.Metadata,
                ["FileFingerprint", "fileFingerprint", "Fingerprint", "fingerprint"],
                out _));

    private static bool IsReadFileTool(string? toolName)
        => string.Equals(toolName, "ivilson_read", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(toolName, "hs_read", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsHashlineSnapshotHeader(string? text)
        => !string.IsNullOrWhiteSpace(text) &&
           text.Contains("--- File (Hashline):", StringComparison.OrdinalIgnoreCase) &&
           text.Contains("SnapshotId:", StringComparison.OrdinalIgnoreCase) &&
           text.Contains("Fingerprint:", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeToolResultText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            text
                .Split(InlineWhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();
    }

    private static bool ShouldPromoteConcreteFileFollowUp(
        IReadOnlyList<FunctionCallContent> toolCalls,
        string currentSummaryPrompt)
    {
        if (ContainsConcreteSourceReference(currentSummaryPrompt))
        {
            return true;
        }

        foreach (var call in toolCalls)
        {
            if (call.Name is null)
            {
                continue;
            }

            if (call.Name.Equals("analyze_project", StringComparison.OrdinalIgnoreCase) ||
                call.Name.Equals("search_file_index", StringComparison.OrdinalIgnoreCase) ||
                call.Name.Equals("search_in_files", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string TruncateForPrompt(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxChars)
        {
            return value ?? string.Empty;
        }

        return value[..Math.Max(0, maxChars - 3)] + "...";
    }

    private static string QuoteIfNeeded(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return $"\"{value}\"";
    }

    private static JToken NormalizeJsonToken(JToken token)
    {
        return token.Type switch
        {
            JTokenType.Object => new JObject(
                token.Children<JProperty>()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => new JProperty(property.Name, NormalizeJsonToken(property.Value)))),
            JTokenType.Array => new JArray(token.Children().Select(NormalizeJsonToken)),
            _ => token.DeepClone()
        };
    }

    private static bool TryGetDictionaryString<TValue>(
        IEnumerable<KeyValuePair<string, TValue>> metadata,
        IReadOnlyCollection<string> keys,
        out string? value)
    {
        foreach (var pair in metadata)
        {
            if (!keys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            value = pair.Value switch
            {
                null => null,
                string text => text,
                JToken token => token.Type == JTokenType.String ? token.Value<string>() : token.ToString(Formatting.None),
                JsonElement element => JsonElementToString(element),
                _ => pair.Value.ToString()
            };
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }

    private static bool TryGetJsonElementString(
        JsonElement metadata,
        IReadOnlyCollection<string> keys,
        out string? value)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
        {
            value = null;
            return false;
        }

        foreach (var property in metadata.EnumerateObject())
        {
            if (!keys.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            value = JsonElementToString(property.Value);
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }

    private static string? JsonElementToString(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();

    private static string BuildStableJsonLikeString(object? value)
    {
        var builder = new StringBuilder();
        AppendStableJsonLikeValue(builder, value);
        return builder.ToString();
    }

    private static void AppendStableJsonLikeValue(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                return;
            case string text:
                AppendQuoted(builder, text);
                return;
            case bool boolean:
                builder.Append(boolean ? "true" : "false");
                return;
            case char character:
                AppendQuoted(builder, character.ToString());
                return;
            case JsonElement element:
                AppendStableJsonElement(builder, element);
                return;
            case JToken token:
                AppendStableJToken(builder, token);
                return;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                AppendStableJsonLikeObject(builder, pairs);
                return;
            case System.Collections.IDictionary dictionary:
                AppendStableDictionary(builder, dictionary);
                return;
            case System.Collections.IEnumerable enumerable:
                AppendStableJsonLikeArray(builder, enumerable.Cast<object?>());
                return;
            case IFormattable formattable:
                builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            default:
                AppendQuoted(builder, value.ToString() ?? string.Empty);
                return;
        }
    }

    private static void AppendStableJsonLikeObject(
        StringBuilder builder,
        IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        builder.Append('{');
        var first = true;
        foreach (var pair in pairs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            AppendQuoted(builder, pair.Key);
            builder.Append(':');
            AppendStableJsonLikeValue(builder, pair.Value);
            first = false;
        }

        builder.Append('}');
    }

    private static void AppendStableDictionary(
        StringBuilder builder,
        System.Collections.IDictionary dictionary)
    {
        var pairs = dictionary.Keys
            .Cast<object?>()
            .Where(key => key != null)
            .Select(key => new KeyValuePair<string, object?>(key!.ToString() ?? string.Empty, dictionary[key]))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal);
        AppendStableJsonLikeObject(builder, pairs);
    }

    private static void AppendStableJsonLikeArray(
        StringBuilder builder,
        IEnumerable<object?> values)
    {
        builder.Append('[');
        var first = true;
        foreach (var value in values)
        {
            if (!first)
            {
                builder.Append(',');
            }

            AppendStableJsonLikeValue(builder, value);
            first = false;
        }

        builder.Append(']');
    }

    private static void AppendStableJsonElement(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                    {
                        builder.Append(',');
                    }

                    AppendQuoted(builder, property.Name);
                    builder.Append(':');
                    AppendStableJsonElement(builder, property.Value);
                    firstProperty = false;
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                AppendStableJsonLikeArray(builder, element.EnumerateArray().Select(item => (object?)item));
                break;
            case JsonValueKind.String:
                AppendQuoted(builder, element.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                builder.Append(element.GetRawText());
                break;
            default:
                builder.Append("null");
                break;
        }
    }

    private static void AppendStableJToken(StringBuilder builder, JToken token)
    {
        switch (token.Type)
        {
            case JTokenType.Object:
                AppendStableJsonLikeObject(
                    builder,
                    token.Children<JProperty>().Select(property =>
                        new KeyValuePair<string, object?>(property.Name, property.Value)));
                break;
            case JTokenType.Array:
                AppendStableJsonLikeArray(builder, token.Children().Cast<object?>());
                break;
            case JTokenType.String:
                AppendQuoted(builder, token.Value<string>() ?? string.Empty);
                break;
            case JTokenType.Null:
            case JTokenType.Undefined:
                builder.Append("null");
                break;
            default:
                builder.Append(token.ToString(Formatting.None));
                break;
        }
    }

    private static void AppendQuoted(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
    }

    private static IReadOnlyList<AIFunction>? ResolveAvailableTools(QueryRuntimeRequest request)
    {
        if (request.AvailableToolsProvider != null)
        {
            return request.AvailableToolsProvider();
        }

        return request.AvailableTools;
    }

    private static IReadOnlyList<ICodexTool>? ResolveAvailableCodexTools(QueryRuntimeRequest request)
    {
        if (request.AvailableCodexToolsProvider != null)
        {
            return request.AvailableCodexToolsProvider();
        }

        return request.AvailableCodexTools;
    }

    private static VllmChatOptions EnsureRuntimeChatOptions(
        ChatOptions? options,
        IReadOnlyDictionary<string, object?>? optionOverrides = null)
    {
        var runtimeOptions = new VllmChatOptions();
        if (options != null)
        {
            CopyChatOptionsExplicit(options, runtimeOptions);
        }

        if (optionOverrides is { Count: > 0 })
        {
            ApplyRuntimeOptionOverrides(runtimeOptions, optionOverrides);
        }

        runtimeOptions.EnableLegacyToolCallTextFallback = true;
        return runtimeOptions;
    }

    private static void CopyChatOptionsExplicit(ChatOptions source, VllmChatOptions destination)
    {
        destination.ConversationId = source.ConversationId;
        destination.Instructions = source.Instructions;
        destination.Temperature = source.Temperature;
        destination.MaxOutputTokens = source.MaxOutputTokens;
        destination.TopP = source.TopP;
        destination.TopK = source.TopK;
        destination.FrequencyPenalty = source.FrequencyPenalty;
        destination.PresencePenalty = source.PresencePenalty;
        destination.Seed = source.Seed;
        destination.Reasoning = source.Reasoning;
        destination.ResponseFormat = source.ResponseFormat;
        destination.ModelId = source.ModelId;
        destination.StopSequences = source.StopSequences?.ToArray();
        destination.AllowMultipleToolCalls = source.AllowMultipleToolCalls;
        destination.ToolMode = source.ToolMode;
        destination.Tools = source.Tools?.ToList();
        destination.RawRepresentationFactory = source.RawRepresentationFactory;
        destination.AdditionalProperties = source.AdditionalProperties is null
            ? null
            : new AdditionalPropertiesDictionary(source.AdditionalProperties);

        if (source is VllmChatOptions vllmSource)
        {
            destination.ThinkingEnabled = vllmSource.ThinkingEnabled;
            destination.EnableSkills = vllmSource.EnableSkills;
            destination.SkillDirectoryPath = vllmSource.SkillDirectoryPath;
            destination.EnableLegacyToolCallTextFallback = vllmSource.EnableLegacyToolCallTextFallback;
        }
    }

    private static void ApplyRuntimeOptionOverrides(
        VllmChatOptions options,
        IReadOnlyDictionary<string, object?> overrides)
    {
        foreach (var pair in overrides)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            if (TryApplyRuntimeOptionOverride(options, pair.Key, pair.Value))
            {
                continue;
            }

            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.AdditionalProperties[pair.Key] = pair.Value;
        }
    }

    private static bool TryApplyRuntimeOptionOverride(
        VllmChatOptions options,
        string key,
        object? value)
    {
        switch (NormalizeOptionKey(key))
        {
            case "conversationid":
                options.ConversationId = value?.ToString();
                return true;
            case "instructions":
                options.Instructions = value?.ToString();
                return true;
            case "temperature":
                options.Temperature = ConvertToSingle(value);
                return true;
            case "maxoutputtokens":
                options.MaxOutputTokens = ConvertToInt32(value);
                return true;
            case "topp":
                options.TopP = ConvertToSingle(value);
                return true;
            case "topk":
                options.TopK = ConvertToInt32(value);
                return true;
            case "frequencypenalty":
                options.FrequencyPenalty = ConvertToSingle(value);
                return true;
            case "presencepenalty":
                options.PresencePenalty = ConvertToSingle(value);
                return true;
            case "seed":
                options.Seed = ConvertToInt32(value);
                return true;
            case "responseformat" when value is ChatResponseFormat responseFormat:
                options.ResponseFormat = responseFormat;
                return true;
            case "modelid":
                options.ModelId = value?.ToString();
                return true;
            case "stopsequences":
                options.StopSequences = ConvertToStringList(value);
                return true;
            case "allowmultipletoolcalls":
                options.AllowMultipleToolCalls = ConvertToBoolean(value);
                return true;
            case "toolmode" when value is ChatToolMode toolMode:
                options.ToolMode = toolMode;
                return true;
            case "thinkingenabled":
                options.ThinkingEnabled = ConvertToBoolean(value) ?? options.ThinkingEnabled;
                return true;
            case "enableskills":
                options.EnableSkills = ConvertToBoolean(value) ?? options.EnableSkills;
                return true;
            case "skilldirectorypath":
                options.SkillDirectoryPath = value?.ToString();
                return true;
            case "enablelegacytoolcalltextfallback":
                options.EnableLegacyToolCallTextFallback = ConvertToBoolean(value) ?? options.EnableLegacyToolCallTextFallback;
                return true;
            default:
                return false;
        }
    }

    private static float? ConvertToSingle(object? value)
        => value switch
        {
            null => null,
            float single => single,
            double number => (float)number,
            decimal number => (float)number,
            int number => number,
            long number => number,
            string text when float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            IConvertible convertible => convertible.ToSingle(CultureInfo.InvariantCulture),
            _ => null
        };

    private static int? ConvertToInt32(object? value)
        => value switch
        {
            null => null,
            int integer => integer,
            long integer => checked((int)integer),
            float number => checked((int)number),
            double number => checked((int)number),
            decimal number => checked((int)number),
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            IConvertible convertible => convertible.ToInt32(CultureInfo.InvariantCulture),
            _ => null
        };

    private static bool? ConvertToBoolean(object? value)
        => value switch
        {
            null => null,
            bool boolean => boolean,
            string text when bool.TryParse(text, out var parsed) => parsed,
            IConvertible convertible => convertible.ToBoolean(CultureInfo.InvariantCulture),
            _ => null
        };

    private static string[]? ConvertToStringList(object? value)
        => value switch
        {
            null => null,
            string text => [text],
            IEnumerable<string> strings => strings.ToArray(),
            System.Collections.IEnumerable values => values.Cast<object?>()
                .Select(item => item?.ToString())
                .Where(text => !string.IsNullOrEmpty(text))
                .Select(text => text!)
                .ToArray(),
            _ => [value.ToString() ?? string.Empty]
        };

    private static string NormalizeOptionKey(string key)
        => new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private async Task<bool> TryRecoverUnexecutedCommandIntentAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        string assistantText,
        string thinkingText,
        IReadOnlyList<AIFunction>? currentTools,
        CancellationToken ct)
    {
        if (!ContainsBuildOrTestIntentWithoutToolCall(request, assistantText, thinkingText) ||
            !HasRemainingRecoveryRound(state))
        {
            return false;
        }

        var allowCommandToolsOnRecoveryRound =
            ShouldAllowToolCallsOnNextRound(request, state) &&
            HasAnyCommandExecutionTool(currentTools);

        if (ShouldSkipNarratedIntentRecovery(state, assistantText, allowCommandToolsOnRecoveryRound))
        {
            return false;
        }

        var maxAttempts = Math.Max(1, request.AdapterHints?.MaxRecoveryAttempts ?? 3);
        var attempt = state.UnexecutedCommandIntentRecoveryCount + 1;
        if (attempt > maxAttempts)
        {
            return false;
        }

        state.UnexecutedCommandIntentRecoveryCount = attempt;
        state.RecoveryCount++;
        state.Flags |= RuntimeState.ZeroToolCallRecoveryUsed;
        state.ForceAllowToolCallsNextRound = allowCommandToolsOnRecoveryRound;
        state.ForceDisableToolCallsNextRound = !allowCommandToolsOnRecoveryRound;

        var requiredCommandTool = SelectRequiredCommandRecoveryTool(currentTools);
        if (allowCommandToolsOnRecoveryRound && !string.IsNullOrWhiteSpace(requiredCommandTool))
        {
            state.RequiredToolNameForNextRound = requiredCommandTool;
            state.NextRoundOptionOverrides = MergeRuntimeOptionOverrides(
                state.NextRoundOptionOverrides,
                new Dictionary<string, object?>
                {
                    ["ToolMode"] = ChatToolMode.RequireSpecific(requiredCommandTool),
                    ["ThinkingEnabled"] = false
                });
        }

        var correction = allowCommandToolsOnRecoveryRound
            ? BuildUnexecutedCommandIntentCorrection(request)
            : BuildSynthesisOnlyIntentCorrection(
                state,
                "你刚刚口头表示将执行构建/编译/测试/验证命令，但没有实际调用工具。");
        var detectedLanguage = ResolveProjectLanguage(request);

        state.Messages.Add(new ChatMessage(ChatRole.User, correction));
        if (allowCommandToolsOnRecoveryRound)
        {
            AppendPendingRoundPrompt(
                state,
                BuildCommandToolOnlyRecoveryPrompt(requiredCommandTool, currentTools, attempt));
        }

        AppendRuntimeRecoveryHint(
            state,
            source: "unexecuted_command_intent",
            attempt: attempt,
            requiredToolName: allowCommandToolsOnRecoveryRound ? requiredCommandTool : null,
            toolCallRequired: allowCommandToolsOnRecoveryRound,
            message: allowCommandToolsOnRecoveryRound
                ? $"Assistant described {detectedLanguage} build/test intent without a tool call. Execute the verification command with the required command tool."
                : $"Assistant deferred {detectedLanguage} verification but only a synthesis round remains.",
            candidateFiles: ResolveRecoveryCandidateFiles(state));

        if (allowCommandToolsOnRecoveryRound)
        {
            _logger.LogInformation(
                "QueryRuntimeEngine[{EntryPoint}] recovered unexecuted build/test intent for session {SessionId} on round {Round}. attempt={Attempt}/{MaxAttempts}",
                request.EntryPoint,
                request.SessionId,
                state.Round,
                attempt,
                maxAttempts);
        }
        else
        {
            _logger.LogInformation(
                "QueryRuntimeEngine[{EntryPoint}] converted unexecuted build/test intent into synthesis-only wrap-up for session {SessionId} on round {Round}. attempt={Attempt}/{MaxAttempts}",
                request.EntryPoint,
                request.SessionId,
                state.Round,
                attempt,
                maxAttempts);
        }

        _telemetry?.RecordRecovery(new QueryLoopRecovery(
            queryId,
            request.SessionId,
            request.EntryPoint,
            state.Round,
            "unexecuted_command_intent",
            attempt,
            Continued: true,
            Terminal: false));

        await EmitEventAsync(eventSink, new RecoveryTriggeredEvent(
            Seq: seqBase + 905 + attempt,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            RecoveryType: "unexecuted_command_intent",
            Attempt: attempt,
            Reason: allowCommandToolsOnRecoveryRound
                ? $"assistant described {detectedLanguage} build/test intent without tool calls"
                : $"assistant deferred {detectedLanguage} build/test verification, but only a synthesis-only wrap-up round remains")).ConfigureAwait(false);

        await EmitEventAsync(eventSink, new SystemNoticeEvent(
            Seq: seqBase + 930 + attempt,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            NoticeType: "tool_use_correction",
            Content: correction)).ConfigureAwait(false);

        return true;
    }

    private async Task<bool> TryRecoverUnexecutedReadIntentAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        string assistantText,
        IReadOnlyList<AIFunction>? currentTools,
        CancellationToken ct)
    {
        if (!ContainsReadOrExploreIntentWithoutToolCall(assistantText) ||
            LooksLikeSubstantialVisibleAnswer(assistantText))
        {
            return false;
        }

        var hasRemainingRecoveryRound = HasRemainingRecoveryRound(state);
        var canExtendSynthesisOnlyRound =
            !hasRemainingRecoveryRound &&
            HasPriorToolEvidenceForSynthesis(state);
        if (!hasRemainingRecoveryRound && !canExtendSynthesisOnlyRound)
        {
            return false;
        }

        var shouldForceReadOnlyAnalysisSynthesis =
            IsReadOnlyAnalysisRequest(request) &&
            !ShouldAllowOneMoreReadAfterProjectBootstrap(state) &&
            state.ConsecutiveToolOnlyRounds > 0 &&
            (state.TotalToolCalls >= 5 ||
             HasSubstantialReadOnlyAnalysisEvidence(state));
        var shouldPreferSynthesis =
            !ShouldAllowOneMoreReadAfterProjectBootstrap(state) &&
            ShouldPreferSynthesisForReadOnlyAnalysis(request, state, assistantText);
        var allowReadToolsOnRecoveryRound =
            hasRemainingRecoveryRound &&
            !shouldForceReadOnlyAnalysisSynthesis &&
            !shouldPreferSynthesis &&
            ShouldAllowToolCallsOnNextRound(request, state) &&
            HasExplorationTool(currentTools);

        if (ShouldSkipNarratedIntentRecovery(state, assistantText, allowReadToolsOnRecoveryRound))
        {
            return false;
        }

        var maxAttempts = Math.Max(1, request.AdapterHints?.MaxRecoveryAttempts ?? 3);
        var attempt = state.UnexecutedReadIntentRecoveryCount + 1;
        if (attempt > maxAttempts)
        {
            return false;
        }

        state.UnexecutedReadIntentRecoveryCount = attempt;
        state.RecoveryCount++;
        state.Flags |= RuntimeState.ZeroToolCallRecoveryUsed;
        state.ForceAllowToolCallsNextRound = allowReadToolsOnRecoveryRound;
        state.ForceDisableToolCallsNextRound = !allowReadToolsOnRecoveryRound;
        if (!allowReadToolsOnRecoveryRound)
        {
            EnsureSynthesisOnlyRecoveryRound(state);
        }

        var correction = allowReadToolsOnRecoveryRound
            ? BuildUnexecutedReadIntentCorrection(currentTools)
            : BuildSynthesisOnlyIntentCorrection(
                state,
                "你刚刚口头表示将继续读取/搜索/查看项目证据，但没有实际调用工具。");

        state.Messages.Add(new ChatMessage(ChatRole.User, correction));
        AppendRuntimeRecoveryHint(
            state,
            source: "unexecuted_read_intent",
            attempt: attempt,
            requiredToolName: null,
            toolCallRequired: allowReadToolsOnRecoveryRound,
            message: allowReadToolsOnRecoveryRound
                ? "Assistant described continued reading/searching without a tool call. Use one read/search tool now."
                : "Assistant deferred more reading/searching, but enough evidence exists for a synthesis-only wrap-up.",
            candidateFiles: ResolveRecoveryCandidateFiles(state));

        if (allowReadToolsOnRecoveryRound)
        {
            _logger.LogInformation(
                "QueryRuntimeEngine[{EntryPoint}] recovered unexecuted read/search intent for session {SessionId} on round {Round}. attempt={Attempt}/{MaxAttempts}",
                request.EntryPoint,
                request.SessionId,
                state.Round,
                attempt,
                maxAttempts);
        }
        else
        {
            _logger.LogInformation(
                "QueryRuntimeEngine[{EntryPoint}] converted unexecuted read/search intent into synthesis-only wrap-up for session {SessionId} on round {Round}. attempt={Attempt}/{MaxAttempts}",
                request.EntryPoint,
                request.SessionId,
                state.Round,
                attempt,
                maxAttempts);
        }

        _telemetry?.RecordRecovery(new QueryLoopRecovery(
            queryId,
            request.SessionId,
            request.EntryPoint,
            state.Round,
            "unexecuted_read_intent",
            attempt,
            Continued: true,
            Terminal: false));

        await EmitEventAsync(eventSink, new RecoveryTriggeredEvent(
            Seq: seqBase + 908 + attempt,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            RecoveryType: "unexecuted_read_intent",
            Attempt: attempt,
            Reason: allowReadToolsOnRecoveryRound
                ? "assistant described continued reading/search intent without tool calls"
                : "assistant deferred more reading/searching, but only a synthesis-only wrap-up round remains")).ConfigureAwait(false);

        await EmitEventAsync(eventSink, new SystemNoticeEvent(
            Seq: seqBase + 933 + attempt,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            NoticeType: "tool_use_correction",
            Content: correction)).ConfigureAwait(false);

        if (request.AdapterHints?.EnableTransportFailureRecovery == true)
        {
            await Task.Yield();
        }

        return true;
    }

    private async Task<bool> TryRecoverUnexecutedPlanningIntentAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        string assistantText,
        string thinkingText,
        IReadOnlyList<AIFunction>? currentTools,
        CancellationToken ct)
    {
        if (state.ExecutedPlanningToolCount > 0)
        {
            return false;
        }

        var currentStage = ResolvePlanningStage(request);
        var currentPlanCount = ResolveCurrentPlanCount(request);
        var planLossDetected = HasPlanLoss(request, currentPlanCount);
        var planningRequestedByUser = WasPlanningRequestedByUser(request);
        var synthesisOnlyRound =
            ShouldForceReadOnlyAnalysisSynthesisRound(request, state) ||
            ShouldReserveToolWrapUpRound(request) && (state.Round + 1) >= state.MaxRounds - 1;

        if (!request.EnableTools ||
            !HasAnyPlanningTool(currentTools) ||
            !ContainsPlanningIntentWithoutToolCall(assistantText))
        {
            return false;
        }

        if (!planningRequestedByUser && IsReadOnlyAnalysisRequest(request))
        {
            return false;
        }

        if (synthesisOnlyRound)
        {
            return false;
        }

        if (ShouldSkipNarratedIntentRecovery(state, assistantText, toolsAllowedOnRecoveryRound: true))
        {
            return false;
        }

        if (currentStage.HasValue && currentStage.Value > 2)
        {
            return false;
        }

        if (currentPlanCount > 0)
        {
            return false;
        }

        var maxAttempts = Math.Max(1, request.AdapterHints?.MaxRecoveryAttempts ?? 3);
        var attempt = state.UnexecutedPlanningIntentRecoveryCount + 1;
        if (attempt > maxAttempts)
        {
            return false;
        }

        state.UnexecutedPlanningIntentRecoveryCount = attempt;
        state.RecoveryCount++;
        state.Flags |= RuntimeState.ZeroToolCallRecoveryUsed;
        state.ForceAllowToolCallsNextRound = true;
        state.ForceDisableToolCallsNextRound = false;

        if (planLossDetected)
        {
            var planGeneratedAtUtc = request.Session?.PlanGeneratedAtUtc?.ToString("u") ?? "unknown";
            var planLossCorrection =
                "检测到当前会话的计划状态丢失：该会话此前已经成功生成计划，但当前计划列表为空。" +
                $"计划生成时间：{planGeneratedAtUtc}。" +
                $"现在禁止再次调用 `{PlanningToolNames.Primary}`（或兼容别名 `{PlanningToolNames.LegacyAlias}`）覆盖原计划，也不要输出新的文本计划。" +
                "下一条消息必须直接向用户说明：当前会话发生了 plan-loss，需要先恢复原计划或新建会话后再重新生成计划。";

            state.Messages.Add(new ChatMessage(ChatRole.User, planLossCorrection));
            AppendRuntimeRecoveryHint(
                state,
                source: "plan_loss_guard",
                attempt: attempt,
                requiredToolName: null,
                toolCallRequired: false,
                message: "Plan loss detected. Do not regenerate the plan; report plan-loss to the user.",
                candidateFiles: ResolveRecoveryCandidateFiles(state));

            _logger.LogWarning(
                "QueryRuntimeEngine[{EntryPoint}] blocked automatic planning-intent recovery because plan loss was detected for session {SessionId}. attempt={Attempt}/{MaxAttempts}",
                request.EntryPoint,
                request.SessionId,
                attempt,
                maxAttempts);

            _telemetry?.RecordRecovery(new QueryLoopRecovery(
                queryId,
                request.SessionId,
                request.EntryPoint,
                state.Round,
                "plan_loss_guard",
                attempt,
                Continued: true,
                Terminal: false));

            await EmitEventAsync(eventSink, new RecoveryTriggeredEvent(
                Seq: seqBase + 906 + attempt,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                Round: state.Round,
                RecoveryType: "plan_loss_guard",
                Attempt: attempt,
                Reason: "planning intent blocked because plan state was previously generated but is now missing")).ConfigureAwait(false);

            await EmitEventAsync(eventSink, new SystemNoticeEvent(
                Seq: seqBase + 931 + attempt,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                NoticeType: "plan_loss_guard",
                Content: planLossCorrection)).ConfigureAwait(false);

            state.LastContinueReason = "plan_loss_guard";
            return true;
        }

        const string correction =
            "你刚刚只用文字表示将生成开发计划，但没有实际调用工具。" +
            "现在必须立刻调用 `create_session_plan`，为当前会话生成任务清单并写入 task list。" +
            "不要重复 `analyze_project`，不要输出纯文本计划，不要说“我将调用工具”。" +
            "如果缺少参数，请直接用当前工作区路径和当前任务描述调用 `create_session_plan`。";

        state.Messages.Add(new ChatMessage(ChatRole.User, correction));
        AppendRuntimeRecoveryHint(
            state,
            source: "unexecuted_planning_intent",
            attempt: attempt,
            requiredToolName: PlanningToolNames.Primary,
            toolCallRequired: true,
            message: "Assistant described planning without calling create_session_plan. Call create_session_plan now.",
            candidateFiles: ResolveRecoveryCandidateFiles(state));

        _logger.LogInformation(
            "QueryRuntimeEngine[{EntryPoint}] recovered unexecuted planning intent for session {SessionId} on round {Round}. attempt={Attempt}/{MaxAttempts}",
            request.EntryPoint,
            request.SessionId,
            state.Round,
            attempt,
            maxAttempts);

        _telemetry?.RecordRecovery(new QueryLoopRecovery(
            queryId,
            request.SessionId,
            request.EntryPoint,
            state.Round,
            "unexecuted_planning_intent",
            attempt,
            Continued: true,
            Terminal: false));

        await EmitEventAsync(eventSink, new RecoveryTriggeredEvent(
            Seq: seqBase + 906 + attempt,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            RecoveryType: "unexecuted_planning_intent",
            Attempt: attempt,
            Reason: "assistant described planning intent without tool calls")).ConfigureAwait(false);

        await EmitEventAsync(eventSink, new SystemNoticeEvent(
            Seq: seqBase + 931 + attempt,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            NoticeType: "tool_use_correction",
            Content: correction)).ConfigureAwait(false);

        if (request.AdapterHints?.EnableTransportFailureRecovery == true)
        {
            await Task.Yield();
        }

        return true;
    }

    private async Task<bool> TryRecoverUnexecutedWriteIntentAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        string assistantText,
        string thinkingText,
        IReadOnlyList<AIFunction>? currentTools,
        CancellationToken ct)
    {
        var hasWriteIntent =
            ContainsWriteIntentWithoutToolCall(assistantText) ||
            ContainsWriteIntentWithoutToolCall(thinkingText) ||
            HasForgeReadEvidenceWithoutWrite(request, state, assistantText);

        if (state.TotalWriteToolCalls > 0 ||
            !request.EnableTools ||
            !HasWritableTool(currentTools) ||
            !hasWriteIntent ||
            !HasRemainingRecoveryRound(state))
        {
            return false;
        }

        if (IsReadOnlyAnalysisRequest(request) ||
            ShouldForceReadOnlyAnalysisSynthesisRound(request, state))
        {
            return false;
        }

        if (ShouldSkipNarratedIntentRecovery(state, assistantText, toolsAllowedOnRecoveryRound: true))
        {
            return false;
        }

        var maxAttempts = Math.Max(1, request.AdapterHints?.MaxRecoveryAttempts ?? 3);
        var attempt = state.UnexecutedWriteIntentRecoveryCount + 1;
        if (attempt > maxAttempts)
        {
            return false;
        }

        state.UnexecutedWriteIntentRecoveryCount = attempt;
        state.RecoveryCount++;
        state.Flags |= RuntimeState.ZeroToolCallRecoveryUsed;
        state.Flags |= RuntimeState.UnexecutedWriteIntentRecoveryUsed;
        state.ForceAllowToolCallsNextRound = true;
        state.ForceDisableToolCallsNextRound = false;
        EnsurePostWriteRecoveryWrapUpRound(request, state);

        var writeTools = currentTools!
            .Select(tool => tool.Name)
            .Where(name => ToolClassification.IsWriteTool(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requiredWriteTool = SelectRequiredWriteRecoveryTool(writeTools);
        if (!string.IsNullOrWhiteSpace(requiredWriteTool))
        {
            state.RequiredToolNameForNextRound = requiredWriteTool;
            state.NextRoundOptionOverrides = MergeRuntimeOptionOverrides(
                state.NextRoundOptionOverrides,
                new Dictionary<string, object?>
                {
                    ["ToolMode"] = ChatToolMode.RequireSpecific(requiredWriteTool),
                    ["ThinkingEnabled"] = false
                });
        }

        var toolList = string.Join(" / ", writeTools.Select(name => $"`{name}`"));
        var requiredToolGuidance = BuildRequiredWriteToolGuidance(requiredWriteTool);
        var writeToolPreference = string.Equals(requiredWriteTool, "hs_write", StringComparison.OrdinalIgnoreCase)
            ? "已有 Hashline 快照的既有文件优先使用 `hs_write`；仅在创建新文件时才使用 `write_file`。"
            : "既有文件优先使用就地补丁或编辑工具；仅在创建新文件时才使用 `write_file`。";
        RecordPendingModificationEvidence(state, assistantText, requiredWriteTool);
        var recoveryContext = BuildWriteRecoveryContext(state, assistantText, requiredWriteTool);
        if (attempt >= 2)
        {
            state.NextRoundOptionOverrides = MergeRuntimeOptionOverrides(
                state.NextRoundOptionOverrides,
                new Dictionary<string, object?>
                {
                    ["temperature"] = 0.05,
                    ["top_p"] = 0.4
                });
            AppendPendingRoundPrompt(
                state,
                BuildWriteToolOnlyRecoveryPrompt(requiredWriteTool, toolList, attempt));
        }

        var correction =
            "你刚刚只用文字描述将修改代码或文件，但没有实际调用写工具。" +
            $"现在必须立刻调用写工具落地修改：{toolList}。" +
            (string.IsNullOrWhiteSpace(requiredWriteTool) ? "" : $"下一轮 runtime 已强制要求调用 `{requiredWriteTool}`。") +
            requiredToolGuidance +
            writeToolPreference +
            recoveryContext +
            "不要再输出“现在执行修改”“需要修改这些文件”之类的说明文字；纯文本描述修改计划会直接视为失败。";

        state.Messages.Add(new ChatMessage(ChatRole.User, correction));
        AppendRuntimeRecoveryHint(
            state,
            source: "unexecuted_write_intent",
            attempt: attempt,
            requiredToolName: requiredWriteTool,
            toolCallRequired: true,
            message: string.IsNullOrWhiteSpace(recoveryContext)
                ? "Assistant described code/file modification without a write tool call. Use the required write tool now."
                : recoveryContext,
            candidateFiles: ResolveRecoveryCandidateFiles(state));

        _logger.LogInformation(
            "QueryRuntimeEngine[{EntryPoint}] recovered unexecuted write intent for session {SessionId} on round {Round}. attempt={Attempt}/{MaxAttempts}",
            request.EntryPoint,
            request.SessionId,
            state.Round,
            attempt,
            maxAttempts);

        _telemetry?.RecordRecovery(new QueryLoopRecovery(
            queryId,
            request.SessionId,
            request.EntryPoint,
            state.Round,
            "unexecuted_write_intent",
            attempt,
            Continued: true,
            Terminal: false));

        await EmitEventAsync(eventSink, new RecoveryTriggeredEvent(
            Seq: seqBase + 907 + attempt,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            Round: state.Round,
            RecoveryType: "unexecuted_write_intent",
            Attempt: attempt,
            Reason: "assistant described code/file modification intent without write tool calls")).ConfigureAwait(false);

        await EmitEventAsync(eventSink, new SystemNoticeEvent(
            Seq: seqBase + 932 + attempt,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            NoticeType: "tool_use_correction",
            Content: correction)).ConfigureAwait(false);

        if (request.AdapterHints?.EnableTransportFailureRecovery == true)
        {
            await Task.Yield();
        }

        return true;
    }

    private static string? SelectRequiredWriteRecoveryTool(string[] writeTools)
    {
        if (writeTools.Length == 0)
        {
            return null;
        }

        string[] preference =
        [
            "hs_write",
            "ivilson_smart_patch",
            "apply_patch",
            "edit_file",
            "write_file"
        ];

        foreach (var preferred in preference)
        {
            var match = writeTools.FirstOrDefault(name => string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return writeTools[0];
    }

    private static string BuildWriteToolOnlyRecoveryPrompt(string? requiredWriteTool, string toolList, int attempt)
    {
        var required = string.IsNullOrWhiteSpace(requiredWriteTool)
            ? "一个写工具"
            : $"`{requiredWriteTool}`";
        var toolSpecificGuidance = BuildRequiredWriteToolGuidance(requiredWriteTool);

        return $"[SYSTEM] 工具专用恢复轮 attempt={attempt}: 上一轮仍然只描述修改而没有调用写工具。现在只允许发出 {required} 工具调用；不要输出正文、不要解释、不要总结。{toolSpecificGuidance}若无法构造完整参数，使用已有上下文中的文件路径和修改内容发出最小可行写入。可用写工具：{toolList}。";
    }

    private static string BuildWriteRecoveryContext(
        QueryRuntimeState state,
        string assistantText,
        string? requiredWriteTool)
    {
        var builder = new StringBuilder();
        builder.Append("可直接用于构造写工具参数的上下文如下：");

        var taskPrompt = ExtractInitialUserTaskPrompt(state);
        if (!string.IsNullOrWhiteSpace(taskPrompt))
        {
            builder.Append("任务约束：");
            builder.Append(CompactForRecoveryContext(taskPrompt, 700));
            builder.Append('。');
        }

        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            builder.Append("上一轮已形成的修改计划：");
            builder.Append(CompactForRecoveryContext(assistantText, 900));
            builder.Append('。');
        }

        if (state.EvidenceLedger.PendingModifications.Count > 0)
        {
            builder.Append("待落地修改证据：");
            foreach (var pending in state.EvidenceLedger.PendingModifications.TakeLast(3))
            {
                if (!string.IsNullOrWhiteSpace(pending.RequiredToolName))
                {
                    builder.Append(" requiredTool=");
                    builder.Append(pending.RequiredToolName);
                }

                if (pending.CandidateFiles.Count > 0)
                {
                    builder.Append(" candidateFiles=");
                    builder.Append(string.Join(", ", pending.CandidateFiles.Take(6)));
                }

                if (!string.IsNullOrWhiteSpace(pending.AssistantPlanSummary))
                {
                    builder.Append(" plan=");
                    builder.Append(CompactForRecoveryContext(pending.AssistantPlanSummary, 500));
                }

                builder.Append(';');
            }
        }

        var fileEvidence = GetKnownFileEvidence(state);
        if (fileEvidence.Count > 0)
        {
            builder.Append("已读取的最新文件快照：");
            foreach (var evidence in fileEvidence
                         .OrderBy(static item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                         .Take(8))
            {
                builder.Append(' ');
                builder.Append(evidence.FilePath);
                if (!string.IsNullOrWhiteSpace(evidence.SnapshotId))
                {
                    builder.Append(" snapshotId=");
                    builder.Append(evidence.SnapshotId);
                }

                if (!string.IsNullOrWhiteSpace(evidence.FileFingerprint))
                {
                    builder.Append(" fileFingerprint=");
                    builder.Append(evidence.FileFingerprint);
                }

                if (evidence.WindowStartLine.HasValue || evidence.WindowEndLine.HasValue)
                {
                    builder.Append(" window=");
                    builder.Append(evidence.WindowStartLine?.ToString() ?? "?");
                    builder.Append('-');
                    builder.Append(evidence.WindowEndLine?.ToString() ?? "?");
                }

                if (evidence.TotalLineCount.HasValue)
                {
                    builder.Append(" totalLines=");
                    builder.Append(evidence.TotalLineCount.Value);
                }

                builder.Append(';');
            }
        }

        var repeatedReadTargets = GetRepeatedReadTargets(state);
        if (repeatedReadTargets.Length > 0)
        {
            builder.Append("这些文件刚刚被重复读取且指纹未变化，不要再读：");
            builder.Append(string.Join(", ", repeatedReadTargets.Take(6)));
            builder.Append('。');
        }

        if (!string.IsNullOrWhiteSpace(requiredWriteTool) &&
            string.Equals(requiredWriteTool, "hs_write", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append("优先用 hs_write 简化参数：filePath、oldString、newString；runtime 会从上述 snapshotId/fileFingerprint 补齐并生成 operations。只有当你已经有明确 line/anchor 时才直接提交 operations。");
        }

        var context = CompactForRecoveryContext(builder.ToString(), WriteRecoveryContextMaxChars);
        return string.IsNullOrWhiteSpace(context) ? string.Empty : context;
    }

    private static void RecordPendingModificationEvidence(
        QueryRuntimeState state,
        string assistantText,
        string? requiredWriteTool)
    {
        var planSummary = CompactForRecoveryContext(assistantText, 900);
        if (string.IsNullOrWhiteSpace(planSummary))
        {
            return;
        }

        var candidateFiles = GetKnownFileEvidence(state)
            .Select(static evidence => evidence.FilePath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();

        state.EvidenceLedger.PendingModifications.Add(new PendingModificationEvidence
        {
            Source = "unexecuted_write_intent",
            RequiredToolName = requiredWriteTool,
            AssistantPlanSummary = planSummary,
            CandidateFiles = candidateFiles
        });

        if (state.EvidenceLedger.PendingModifications.Count > 8)
        {
            state.EvidenceLedger.PendingModifications.RemoveRange(
                0,
                state.EvidenceLedger.PendingModifications.Count - 8);
        }
    }

    private static string? ExtractInitialUserTaskPrompt(QueryRuntimeState state)
        => state.Messages
            .FirstOrDefault(static message => message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.Text))
            ?.Text;

    private static string CompactForRecoveryContext(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var compacted = Regex.Replace(text.Trim(), @"\s+", " ");
        return compacted.Length <= maxChars
            ? compacted
            : compacted[..maxChars] + "...";
    }

    private static string BuildRequiredWriteToolGuidance(string? requiredWriteTool)
    {
        if (string.IsNullOrWhiteSpace(requiredWriteTool))
        {
            return string.Empty;
        }

        if (string.Equals(requiredWriteTool, "hs_write", StringComparison.OrdinalIgnoreCase))
        {
            return "`hs_write` 直接提交它自己的扁平参数，不要改用 `apply_patch`，也不要手动包 `edit_mode` / `request`。" +
                   "最小可行形式优先使用 Claude Edit 风格：`hs_write({\"filePath\":\"<path>\",\"oldString\":\"<旧片段>\",\"newString\":\"<新片段>\"})`；runtime 会基于已读快照补齐 fingerprint 并生成 Hashline operations。";
        }

        if (string.Equals(requiredWriteTool, "apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            return "`apply_patch` 只接受它自己的参数格式，不要混入 `hs_write` 的扁平参数。";
        }

        return string.Empty;
    }

    private static void EnsurePostWriteRecoveryWrapUpRound(QueryRuntimeRequest request, QueryRuntimeState state)
    {
        if (!ShouldReserveToolWrapUpRound(request))
        {
            return;
        }

        // A write-intent recovery consumes the previously reserved wrap-up slot.
        // Keep one extra turn so the model can see the tool result and report what changed.
        if ((state.Round + 1) >= state.MaxRounds - 1)
        {
            state.MaxRounds++;
        }
    }

    private static bool ContainsBuildOrTestIntentWithoutToolCall(QueryRuntimeRequest request, string assistantText, string thinkingText)
    {
        var normalized = NormalizeIntentText(assistantText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            var normalizedThinking = NormalizeIntentText(thinkingText);
            return HasExplicitCommandToolExecutionIntent(normalizedThinking);
        }

        if (LooksLikeAnalyticalCompilation(normalized))
        {
            return false;
        }

        if (LooksLikeCommandToolCapabilityDescription(normalized))
        {
            return false;
        }

        if (HasExplicitVerificationCommandIntent(normalized))
        {
            return HasBuildOrTestIntentVerb(normalized) ||
                   normalized.Contains("验证编译", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("验证测试", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("确认编译", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("确认测试", StringComparison.OrdinalIgnoreCase);
        }

        var detectedLanguage = ResolveProjectLanguage(request);
        return HasNaturalLanguageBuildTestIntent(normalized, detectedLanguage);
    }

    private static bool HasExplicitCommandToolExecutionIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            LooksLikeAnalyticalCompilation(text))
        {
            return false;
        }

        var mentionsCommandTool =
            text.Contains("exec_cmd", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("run_command", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("run_tests", StringComparison.OrdinalIgnoreCase);
        if (!mentionsCommandTool)
        {
            return false;
        }

        return Regex.IsMatch(
            text,
            @"\b(call|calling|execute|executing|run|running|use|using|invoke|invoking)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(
                text,
                @"(调用|执行|运行|使用)\s*(?:工具|命令)?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeCommandToolCapabilityDescription(string text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            HasExplicitVerificationCommandIntent(text) ||
            Regex.IsMatch(
                text,
                @"(我将|我要|让我|接下来|现在|立即|马上|must|need to|let me|will|going to)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }

        var mentionsCommandTool =
            text.Contains("exec_cmd", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("run_command", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("run_tests", StringComparison.OrdinalIgnoreCase);
        if (!mentionsCommandTool)
        {
            return false;
        }

        var describesCapability =
            text.Contains("用于", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("用途", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("可用于", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("工具", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(
                text,
                @"\b(tool|available|used\s+to|can\s+run|can\s+execute)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!describesCapability)
        {
            return false;
        }

        return Regex.IsMatch(
            text,
            @"(^|\s)(?:\d+\.|[-*])\s*(?:`|\*\*)?(exec_cmd|run_command|run_tests)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(
                text,
                @"\b(exec_cmd|run_command|run_tests)\b\s*(?:-|—|:|：)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string NormalizeIntentText(string text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static bool ShouldSkipNarratedIntentRecovery(QueryRuntimeState state, string assistantText, bool toolsAllowedOnRecoveryRound)
    {
        var trimmed = assistantText.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var nearWrapUp = (state.Round + 2) >= state.MaxRounds;
        return trimmed.Length >= 1200 ||
               (!toolsAllowedOnRecoveryRound && trimmed.Length >= 600) ||
               (nearWrapUp && trimmed.Length >= 280);
    }

    private static bool ShouldPrioritizeWriteIntentRecovery(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IReadOnlyList<AIFunction>? currentTools)
        => request.EnableTools &&
           HasWritableTool(currentTools) &&
           !IsReadOnlyAnalysisRequest(request) &&
           (request.EntryPoint == QueryLoopEntryPoint.ForgeWorker ||
            HasPriorReadEvidenceForWrite(state));

    private static bool HasForgeReadEvidenceWithoutWrite(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        string assistantText)
    {
        if (request.EntryPoint != QueryLoopEntryPoint.ForgeWorker ||
            state.TotalToolCalls <= 0 ||
            state.TotalWriteToolCalls > 0 ||
            !HasPriorReadEvidenceForWrite(state) ||
            LooksLikeExplicitExecutionBlocker(assistantText) ||
            LooksLikeSubstantialVisibleAnswer(assistantText))
        {
            return false;
        }

        return true;
    }

    private static bool HasPriorReadEvidenceForWrite(QueryRuntimeState state)
    {
        if (GetKnownFileEvidence(state).Count > 0 ||
            state.EvidenceLedger.SeenReadEvidenceKeys.Count > 0)
        {
            return true;
        }

        var summary = state.LastToolBatchSummaryPrompt;
        if (string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        return SummaryContainsToolName(summary, "ivilson_read") ||
               SummaryContainsToolName(summary, "hs_read") ||
               ContainsHashlineSnapshotHeader(summary) ||
               summary.Contains("SnapshotId:", StringComparison.OrdinalIgnoreCase) ||
               summary.Contains("FileFingerprint", StringComparison.OrdinalIgnoreCase) ||
               summary.Contains("Fingerprint:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeExplicitExecutionBlocker(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("无法继续", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("无法修改", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("不能修改", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("超出范围", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("范围外", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("需要用户", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("需要确认", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("out of scope", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("requires user", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSubstantialVisibleAnswer(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 180)
        {
            return false;
        }

        var nonEmptyLineCount = trimmed
            .Split('\n')
            .Count(line => !string.IsNullOrWhiteSpace(line));
        if (nonEmptyLineCount < 3)
        {
            return false;
        }

        return Regex.IsMatch(
            trimmed,
            @"(^|\n)\s*(?:\d+\.|[-*])\s+",
            RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeAnalyticalCompilation(string text)
        => Regex.IsMatch(
            text,
            @"\bcompile\s+(my|the|this)\s+(analysis|summary|findings|diagnosis)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
           || text.Contains("整理分析", StringComparison.OrdinalIgnoreCase)
           || text.Contains("汇总分析", StringComparison.OrdinalIgnoreCase)
           || text.Contains("综合分析", StringComparison.OrdinalIgnoreCase);

    private static bool HasExplicitVerificationCommandIntent(string text)
        => text.Contains("dotnet build", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("dotnet test", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("mvn test", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("mvn compile", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("mvn package", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("gradle test", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("gradle build", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("gradlew test", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("gradlew build", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("npm test", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("npm run build", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("pnpm test", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("pnpm build", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("pnpm run build", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("yarn test", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("yarn build", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("pytest", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("python -m unittest", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("pip install", StringComparison.OrdinalIgnoreCase);

    private static bool HasBuildOrTestIntentVerb(string text)
        => Regex.IsMatch(
            text,
            @"(我将|我要|让我|现在需要|接下来|立即|will|going to|need to|must|let me)\s*(直接)?\s*(执行|运行|run|running|check)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool HasNaturalLanguageBuildTestIntent(string text, string detectedLanguage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hasIntentVerb = Regex.IsMatch(
            text,
            @"(我将|我要|让我|现在需要|接下来|立即|准备|开始|will|going to|need to|must|let me)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hasExecutionLeadIn = Regex.IsMatch(
            text,
            @"(现在|立即|马上|随后|接着|继续|开始)?\s*(执行|运行)\s*(构建|编译|测试|验证|build|compile|test|validate)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hasBuildSignal = text.Contains("构建", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("编译", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("build", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("compile", StringComparison.OrdinalIgnoreCase);
        var hasTestSignal = text.Contains("测试", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("test", StringComparison.OrdinalIgnoreCase);
        var hasValidationSignal = text.Contains("验证", StringComparison.OrdinalIgnoreCase) ||
                                  text.Contains("校验", StringComparison.OrdinalIgnoreCase) ||
                                  text.Contains("检查当前状态", StringComparison.OrdinalIgnoreCase) ||
                                  text.Contains("validate", StringComparison.OrdinalIgnoreCase);
        var hasLanguageToolSignal = HasLanguageSpecificVerificationSignal(text, detectedLanguage);

        return (hasIntentVerb || hasExecutionLeadIn) &&
               ((hasBuildSignal && hasTestSignal) ||
                (hasValidationSignal && (hasBuildSignal || hasTestSignal || hasLanguageToolSignal)) ||
                hasLanguageToolSignal);
    }

    private static bool HasLanguageSpecificVerificationSignal(string text, string detectedLanguage)
        => detectedLanguage switch
        {
            "csharp" =>
                text.Contains("dotnet", StringComparison.OrdinalIgnoreCase),
            "java" =>
                text.Contains("mvn", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("maven", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("gradle", StringComparison.OrdinalIgnoreCase),
            "typescript" =>
                text.Contains("npm", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("pnpm", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("yarn", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("jest", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("vitest", StringComparison.OrdinalIgnoreCase),
            "python" =>
                text.Contains("pytest", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("unittest", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("pip install", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("uv sync", StringComparison.OrdinalIgnoreCase),
            _ =>
                false
        };

    private static string ResolveProjectLanguage(QueryRuntimeRequest request)
    {
        var raw = request.Session?.ActiveFacts?
            .FirstOrDefault(f => string.Equals(f.Key, ProjectMemoryFactKeys.ProjectLanguage, StringComparison.Ordinal))?
            .Value;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return "csharp";
        }

        var normalized = raw.Trim().ToLowerInvariant();
        if (normalized is "csharp" or "c#" or ".net" or "dotnet")
        {
            return "csharp";
        }

        if (normalized is "java")
        {
            return "java";
        }

        if (normalized is "typescript" or "javascript" or "ts" or "js" or "node" or "nodejs")
        {
            return "typescript";
        }

        if (normalized is "python" or "py")
        {
            return "python";
        }

        return normalized;
    }

    private static string BuildUnexecutedCommandIntentCorrection(QueryRuntimeRequest request)
    {
        var detectedLanguage = ResolveProjectLanguage(request);
        const string prefix =
            "你刚刚只用文字表示将执行构建/编译/测试/验证命令，但没有实际调用工具。" +
            "现在必须立刻使用 `run_command`（或 `exec_cmd` / `run_tests`，若当前可用）执行所需命令。" +
            "纯文本说明“我将执行/现在执行”会直接视为失败。";

        return detectedLanguage switch
        {
            "java" =>
                prefix +
                " 当前项目主语言是 Java，请根据仓库实际构建工具选择 Maven 或 Gradle。" +
                " 例如：`exec_cmd({\"command\":[\"mvn\",\"test\"]})`、`exec_cmd({\"command\":[\"mvn\",\"compile\"]})`、" +
                "`exec_cmd({\"command\":[\"gradle\",\"test\"]})` 或 `exec_cmd({\"command\":[\"gradle\",\"build\"]})`。" +
                " 如果任务要求编译或测试成功证据，必须真实调用这些命令并保留输出。",
            "typescript" =>
                prefix +
                " 当前项目主语言是 Node / TypeScript / JavaScript，请根据仓库实际包管理器执行验证。" +
                " 例如：`exec_cmd({\"command\":[\"npm\",\"run\",\"build\"]})`、`exec_cmd({\"command\":[\"npm\",\"test\"]})`。" +
                " 若仓库使用 `pnpm` 或 `yarn`，可改用对应命令，但仍必须真实调用工具并保留输出。",
            "python" =>
                prefix +
                " 当前项目主语言是 Python，请执行真实的依赖安装/测试命令。" +
                " 例如：`exec_cmd({\"command\":[\"pytest\"]})`、`exec_cmd({\"command\":[\"python\",\"-m\",\"unittest\"]})`。" +
                " 如验证依赖环境，可使用 `exec_cmd({\"command\":[\"pip\",\"install\",\"-e\",\".\"]})` 或仓库实际采用的安装命令。",
            _ =>
                prefix +
                " 当前项目主语言是 .NET / C#。" +
                " 如果需要编译成功证据，必须调用 `exec_cmd({\"command\":[\"dotnet\",\"build\",...]})`。" +
                " 如果需要测试成功证据，必须调用 `exec_cmd({\"command\":[\"dotnet\",\"test\",...]})`。"
        };
    }

    private static string BuildCommandToolOnlyRecoveryPrompt(
        string? requiredCommandTool,
        IReadOnlyList<AIFunction>? currentTools,
        int attempt)
    {
        var toolList = currentTools?
            .Select(tool => tool.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => $"`{name}`")
            .ToArray() ?? [];
        var availableTools = toolList.Length > 0
            ? string.Join(", ", toolList)
            : "`exec_cmd` / `run_command` / `run_tests`";
        var required = string.IsNullOrWhiteSpace(requiredCommandTool)
            ? "一个命令执行工具"
            : $"`{requiredCommandTool}`";

        return $"[SYSTEM] 命令工具专用恢复轮 attempt={attempt}: 上一轮只在 thinking 或正文中表示将调用命令工具，但没有真正发出工具调用。现在只允许发出 {required} 工具调用；不要输出正文、不要解释、不要总结。若任务要求多条命令，请一次性发出所需的最小命令工具调用。可用命令工具：{availableTools}。";
    }

    private static string BuildUnexecutedReadIntentCorrection(IReadOnlyList<AIFunction>? currentTools)
    {
        var preferredTools = currentTools?
            .Select(tool => tool.Name)
            .Where(IsExplorationToolName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(name => $"`{name}`")
            .ToArray() ?? [];

        var toolList = preferredTools.Length > 0
            ? string.Join(" / ", preferredTools)
            : "`ivilson_read` / `search_in_files` / `ivilson_ls`";

        return
            "你刚刚只用文字表示将继续读取/搜索/查看项目证据，但没有实际调用工具。" +
            $" 现在必须立刻调用真正的只读或分析工具，例如 {toolList}。" +
            " 不要再输出“让我继续读取”“我将查看文件”“接下来搜索一下”这类口头说明。" +
            " 如果现有证据已经足够，就直接给出最终结论；否则只调用最必要的读取或搜索工具。";
    }

    private static string BuildSynthesisOnlyIntentCorrection(QueryRuntimeState state, string lead)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(state.LastToolBatchSummaryPrompt))
        {
            builder.AppendLine(state.LastToolBatchSummaryPrompt!.Trim());
            builder.AppendLine();
        }

        builder.AppendLine(lead);
        builder.AppendLine("当前已经进入最终收尾阶段，下一轮默认不再继续扩张工具调用。");
        builder.AppendLine("请直接基于已有代码、工具结果和现有证据给出最终结论。");
        builder.AppendLine("明确区分：已证实的问题、对应证据位置、以及仍需后续命令或额外读取才能确认的部分。");
        builder.Append("不要再说“我将执行”“让我继续读取/搜索/查看”，也不要输出空白。");
        return builder.ToString().Trim();
    }

    private static bool IsReadOnlyAnalysisRequest(QueryRuntimeRequest request)
    {
        var latestOriginalUserPrompt = request.InitialMessages
            .LastOrDefault(message => message.Role == ChatRole.User)?
            .Text;
        if (string.IsNullOrWhiteSpace(latestOriginalUserPrompt))
        {
            return false;
        }

        var normalized = NormalizeIntentText(latestOriginalUserPrompt);
        var hasAnalysisSignal =
            normalized.Contains("分析", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("架构观察", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("架构", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("审查", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("review", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("audit", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("explain", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("compare", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("observation", StringComparison.OrdinalIgnoreCase);
        if (!hasAnalysisSignal)
        {
            return false;
        }

        var hasNoModifySignal =
            normalized.Contains("不要修改代码", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("不要改代码", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("不修改代码", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("只读", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("do not modify", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("without modifying", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("read-only", StringComparison.OrdinalIgnoreCase);
        if (hasNoModifySignal)
        {
            return true;
        }

        return !HasWriteIntent(normalized) && !HasDirectWriteInstruction(normalized);
    }

    private static bool HasSubstantialReadOnlyAnalysisEvidence(QueryRuntimeState state)
    {
        if (HasRepeatedReadEvidence(state))
        {
            return true;
        }

        var latestSummary = state.LastToolBatchSummaryPrompt;
        if (string.IsNullOrWhiteSpace(latestSummary))
        {
            return false;
        }

        if (SummaryContainsToolName(latestSummary, "analyze_project"))
        {
            return true;
        }

        return SummaryContainsExplorationTool(latestSummary) &&
               ContainsConcreteSourceReference(latestSummary);
    }

    private static bool ShouldAllowOneMoreReadAfterProjectBootstrap(QueryRuntimeState state)
    {
        var latestSummary = state.LastToolBatchSummaryPrompt;
        if (string.IsNullOrWhiteSpace(latestSummary) ||
            state.UnexecutedReadIntentRecoveryCount > 0 ||
            HasRepeatedReadEvidence(state) ||
            state.TotalToolCalls > 3)
        {
            return false;
        }

        if (!SummaryContainsToolName(latestSummary, "analyze_project"))
        {
            return false;
        }

        return !SummaryContainsToolName(latestSummary, "ivilson_read") &&
               !SummaryContainsToolName(latestSummary, "hs_read") &&
               !SummaryContainsToolName(latestSummary, "search_in_files") &&
               !SummaryContainsToolName(latestSummary, "search_file_index");
    }

    private static bool SummaryContainsToolName(string summary, string toolName)
    {
        if (string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        if (summary.Contains(toolName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Regex.IsMatch(
            summary,
            $@"^\d+\.\s+{Regex.Escape(toolName)}(?:\(|\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    }

    private static bool SummaryContainsExplorationTool(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        foreach (Match match in Regex.Matches(
                     summary,
                     @"^\d+\.\s+([A-Za-z0-9_]+)(?:\(|\s|$)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline))
        {
            if (match.Groups.Count > 1 && IsExplorationToolName(match.Groups[1].Value))
            {
                return true;
            }
        }

        return summary.Contains("read", StringComparison.OrdinalIgnoreCase) ||
               summary.Contains("search", StringComparison.OrdinalIgnoreCase) ||
               summary.Contains("show", StringComparison.OrdinalIgnoreCase) ||
               summary.Contains("analy", StringComparison.OrdinalIgnoreCase) ||
               summary.Contains("_ls", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsConcreteSourceReference(string text)
        => Regex.IsMatch(
            text,
            @"[A-Za-z0-9_\-./\\]+\.(cs|csproj|json|ts|tsx|js|jsx|py|java|go|rs|sql|xml|yml|yaml|props|targets)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool ContainsPlanningIntentWithoutToolCall(string assistantText)
        => HasPlanningIntent(NormalizeIntentText(assistantText));

    private static bool ContainsWriteIntentWithoutToolCall(string assistantText)
    {
        var normalized = NormalizeIntentText(assistantText);
        return HasWriteIntent(normalized) || HasDirectWriteInstruction(normalized);
    }

    private static bool ContainsReadOrExploreIntentWithoutToolCall(string assistantText)
    {
        var normalized = NormalizeIntentText(assistantText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (HasPlanningIntent(normalized))
        {
            return false;
        }

        var alreadyCompleted = normalized.Contains("已读取", StringComparison.OrdinalIgnoreCase) ||
                               normalized.Contains("已经读取", StringComparison.OrdinalIgnoreCase) ||
                               normalized.Contains("已查看", StringComparison.OrdinalIgnoreCase) ||
                               normalized.Contains("已经查看", StringComparison.OrdinalIgnoreCase) ||
                               normalized.Contains("已检查", StringComparison.OrdinalIgnoreCase) ||
                               normalized.Contains("已分析", StringComparison.OrdinalIgnoreCase) ||
                               normalized.Contains("已经分析", StringComparison.OrdinalIgnoreCase) ||
                               normalized.Contains("already read", StringComparison.OrdinalIgnoreCase) ||
                               normalized.Contains("already checked", StringComparison.OrdinalIgnoreCase) ||
                               normalized.Contains("already reviewed", StringComparison.OrdinalIgnoreCase) ||
                               normalized.Contains("already analyzed", StringComparison.OrdinalIgnoreCase);
        if (alreadyCompleted)
        {
            return false;
        }

        var hasIntentVerb = Regex.IsMatch(
            normalized,
            @"(我将|我要|让我|现在需要|接下来|立即|准备|开始|马上|现在|need to|must|will|going to|let me|now)\s*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hasReadAction = normalized.Contains("读取", StringComparison.OrdinalIgnoreCase) ||
                            normalized.Contains("查看", StringComparison.OrdinalIgnoreCase) ||
                            normalized.Contains("搜索", StringComparison.OrdinalIgnoreCase) ||
                            normalized.Contains("检索", StringComparison.OrdinalIgnoreCase) ||
                            normalized.Contains("检查", StringComparison.OrdinalIgnoreCase) ||
                            normalized.Contains("浏览", StringComparison.OrdinalIgnoreCase) ||
                            normalized.Contains("翻阅", StringComparison.OrdinalIgnoreCase) ||
                            normalized.Contains("分析", StringComparison.OrdinalIgnoreCase) ||
                            normalized.Contains("梳理", StringComparison.OrdinalIgnoreCase) ||
                            Regex.IsMatch(
                                normalized,
                                @"\b(read|search|inspect|examine|review|analyze|analyse|open|look\s+at|check)\b",
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hasEvidenceTarget = normalized.Contains("文件", StringComparison.OrdinalIgnoreCase) ||
                                normalized.Contains("代码", StringComparison.OrdinalIgnoreCase) ||
                                normalized.Contains("目录", StringComparison.OrdinalIgnoreCase) ||
                                normalized.Contains("项目", StringComparison.OrdinalIgnoreCase) ||
                                normalized.Contains("仓库", StringComparison.OrdinalIgnoreCase) ||
                                normalized.Contains("README", StringComparison.OrdinalIgnoreCase) ||
                                normalized.Contains("bug", StringComparison.OrdinalIgnoreCase) ||
                                Regex.IsMatch(
                                    normalized,
                                    @"[A-Za-z0-9_\-./\\]+\.(cs|csproj|json|ts|tsx|js|jsx|py|java|go|rs|sql|md)\b",
                                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var hasIncompleteEvidenceLeadIn =
            normalized.Contains("关键", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("剩余", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("上下文", StringComparison.OrdinalIgnoreCase);

        return hasIntentVerb &&
               hasReadAction &&
               (hasEvidenceTarget || hasIncompleteEvidenceLeadIn);
    }

    private static bool HasPlanningIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hasToolName = text.Contains(PlanningToolNames.Primary, StringComparison.OrdinalIgnoreCase)
                          || text.Contains(PlanningToolNames.LegacyAlias, StringComparison.OrdinalIgnoreCase);
        var hasPlanningSignal = text.Contains("开发计划", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("任务清单", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("代码改进计划", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("技术债", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("plan", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("planning", StringComparison.OrdinalIgnoreCase);
        var hasPlanningAction = text.Contains("调用", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("生成", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("创建", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("写入", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("建立", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("call", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("create", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("generate", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("write", StringComparison.OrdinalIgnoreCase);
        var hasIntentVerb = Regex.IsMatch(
            text,
            @"(我将|我要|让我|现在需要|接下来|立即|准备|开始|will|going to|need to|must|let me|call)\s*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var explicitlyNotCalling = text.Contains("不要调用", StringComparison.OrdinalIgnoreCase) ||
                                   text.Contains("不要重复", StringComparison.OrdinalIgnoreCase);

        return !explicitlyNotCalling &&
               (hasToolName
                   ? hasIntentVerb || hasPlanningAction
                   : hasIntentVerb && hasPlanningAction && hasPlanningSignal);
    }

    private static bool WasPlanningRequestedByUser(QueryRuntimeRequest request)
    {
        var latestOriginalUserPrompt = request.InitialMessages
            .LastOrDefault(message => message.Role == ChatRole.User)?
            .Text;
        if (string.IsNullOrWhiteSpace(latestOriginalUserPrompt))
        {
            return false;
        }

        var normalized = NormalizeIntentText(latestOriginalUserPrompt);
        var explicitlyNotPlanning =
            normalized.Contains("不要生成计划", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("不要创建计划", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("不要输出计划", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("不要任务清单", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("do not create a plan", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("no plan", StringComparison.OrdinalIgnoreCase);
        if (explicitlyNotPlanning)
        {
            return false;
        }

        var hasPlanningTarget =
            normalized.Contains(PlanningToolNames.Primary, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(PlanningToolNames.LegacyAlias, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("开发计划", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("任务清单", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("task list", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("implementation plan", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("dev plan", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("plan", StringComparison.OrdinalIgnoreCase);
        if (!hasPlanningTarget)
        {
            return false;
        }

        return Regex.IsMatch(
            normalized,
            @"(请|帮我|需要|生成|创建|制定|给出|整理|列出|拆解|call|create|generate|make|prepare|produce|plan)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool HasAnyPlanningTool(IReadOnlyList<AIFunction>? currentTools)
    {
        if (currentTools == null)
        {
            return false;
        }

        return currentTools.Any(tool => PlanningToolNames.IsPlanCreationTool(tool.Name));
    }

    private static bool HasWriteIntent(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (HasExplicitNoWriteInstruction(text))
        {
            return false;
        }

        var alreadyCompleted = text.Contains("已修改", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("已经修改", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("修改完成", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("完成修改", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("已更新", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("已经更新", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("补丁已应用", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("已写入", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("already updated", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("already modified", StringComparison.OrdinalIgnoreCase) ||
                               text.Contains("patch applied", StringComparison.OrdinalIgnoreCase) ||
                               Regex.IsMatch(
                                   text,
                                   @"\b(completed|finished|done)\b",
                                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (alreadyCompleted)
        {
            return false;
        }

        var hasIntentVerb = Regex.IsMatch(
            text,
            @"(我将|我要|让我|现在需要|接下来|立即|准备|开始|马上|现在|必须|务必|请|需要|要求|直接|本轮目标|need to|must|will|going to|let me|now)\s*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hasWriteAction = text.Contains("修改", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("修复", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("修复/缓解", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("缓解", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("编辑", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("更新", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("升级", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("改动", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("调整", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("打补丁", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("应用补丁", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("写入", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("落地修改", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("apply_patch", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("write_file", StringComparison.OrdinalIgnoreCase) ||
                             text.Contains("smart_patch", StringComparison.OrdinalIgnoreCase) ||
                             Regex.IsMatch(
                                 text,
                                 @"\b(modify|edit|update|change|fix|patch|write)\b",
                                 RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hasCodeTarget = text.Contains("文件", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("代码", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("方法", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("函数", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("实现", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("补丁", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("项目文件", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("源码", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("依赖", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("包版本", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("NuGet", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains(".csproj", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("patch", StringComparison.OrdinalIgnoreCase) ||
                            Regex.IsMatch(
                                text,
                                @"\b(method|function|implementation)\b",
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                            Regex.IsMatch(
                                text,
                                @"[A-Za-z0-9_\-./\\]+\.(cs|csproj|json|ts|tsx|js|jsx|py|java|go|rs|sql|md)\b",
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return hasIntentVerb && hasWriteAction && hasCodeTarget;
    }

    private static bool HasDirectWriteInstruction(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (HasExplicitNoWriteInstruction(text))
        {
            return false;
        }

        var hasDirectWritePhrase =
            text.Contains("必须直接修改", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("直接修改", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("请修改", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("请修复", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("尝试修复", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("现在修改", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("现在修复", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("立即修改", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("立即修复", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("开始修改", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("开始修复", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("执行修改", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("执行修复", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("应用修复", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("更新依赖", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("升级依赖", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("落地修改", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("产生实际修复改动", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("修复改动", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("修复或缓解", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("修复/缓解", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("apply_patch", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("write_file", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(
                text,
                @"\b(must|please|directly|actually)\s+(modify|edit|update|fix|patch|write)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!hasDirectWritePhrase)
        {
            return false;
        }

        return text.Contains("代码", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("文件", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("源码", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("项目", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("依赖", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("NuGet", StringComparison.OrdinalIgnoreCase) ||
               text.Contains(".csproj", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("package", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("dependency", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(
                   text,
                   @"[A-Za-z0-9_\-./\\]+\.(cs|csproj|json|ts|tsx|js|jsx|py|java|go|rs|sql|md)\b",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool HasExplicitNoWriteInstruction(string text)
        => text.Contains("无需修改", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("不需要修改", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("不要修改", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("不要改代码", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("不修改代码", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("不会修改", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("不能修改", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("不允许修改", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("不应修改", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("不应直接改", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("只读", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("no code changes", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("no changes needed", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("do not modify", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("must not modify", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("should not modify", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("without modifying", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("read-only", StringComparison.OrdinalIgnoreCase);

    private static int? ResolvePlanningStage(QueryRuntimeRequest request)
    {
        return request.Session?.CurrentStage ?? request.PromptMetadata?.InitialStage;
    }

    private static int ResolveCurrentPlanCount(QueryRuntimeRequest request)
    {
        if (request.Session != null)
        {
            return request.Session.Plan.Count;
        }

        return request.PromptMetadata?.PlanSize ?? 0;
    }

    private static bool HasPlanLoss(QueryRuntimeRequest request, int currentPlanCount)
    {
        return request.Session?.PlanGeneratedAtUtc.HasValue == true && currentPlanCount == 0;
    }

    private static bool HasWritableTool(IReadOnlyList<AIFunction>? tools)
    {
        if (tools == null || tools.Count == 0)
        {
            return false;
        }

        return tools.Any(tool => ToolClassification.IsWriteTool(tool.Name));
    }

    private static bool HasExplorationTool(IReadOnlyList<AIFunction>? tools)
    {
        if (tools == null || tools.Count == 0)
        {
            return false;
        }

        return tools.Any(tool => IsExplorationToolName(tool.Name));
    }

    private static bool HasAnyCommandExecutionTool(IReadOnlyList<AIFunction>? tools)
        => HasTool(tools, "run_command") || HasTool(tools, "exec_cmd") || HasTool(tools, "run_tests");

    private static string? SelectRequiredCommandRecoveryTool(IReadOnlyList<AIFunction>? tools)
    {
        var toolNames = tools?
            .Select(tool => tool.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        return toolNames.FirstOrDefault(name => string.Equals(name, "exec_cmd", StringComparison.OrdinalIgnoreCase))
            ?? toolNames.FirstOrDefault(name => string.Equals(name, "run_command", StringComparison.OrdinalIgnoreCase))
            ?? toolNames.FirstOrDefault(name => string.Equals(name, "run_tests", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasTool(IReadOnlyList<AIFunction>? tools, string toolName)
    {
        if (tools == null || string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        return tools.Any(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExplorationToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        return toolName.Contains("read", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("search", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("grep", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("find", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("show", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("analy", StringComparison.OrdinalIgnoreCase) ||
               toolName.EndsWith("_ls", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(toolName, "ls", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(toolName, "dir", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRemainingRecoveryRound(QueryRuntimeState state)
        => (state.Round + 1) < state.MaxRounds;

    private static bool ShouldAllowToolCallsOnNextRound(QueryRuntimeRequest request, QueryRuntimeState state)
        => request.EnableTools &&
           (!ShouldReserveToolWrapUpRound(request) || (state.Round + 1) < state.MaxRounds - 1);

    private static bool HasPriorToolEvidenceForSynthesis(QueryRuntimeState state)
        => state.TotalToolCalls > 0 ||
           !string.IsNullOrWhiteSpace(state.LastToolBatchSummaryPrompt);

    private static void EnsureSynthesisOnlyRecoveryRound(QueryRuntimeState state)
    {
        if ((state.Round + 1) >= state.MaxRounds)
        {
            state.MaxRounds = state.Round + 2;
        }
    }

    private async Task RecoverLegacyToolCallsAsync(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        IQueryRuntimeEventSink eventSink,
        Guid queryId,
        long seqBase,
        StringBuilder roundText,
        StringBuilder roundThinking,
        List<FunctionCallContent> roundToolCalls,
        IReadOnlyList<AIFunction>? availableTools)
    {
        if (!request.EnableTools || availableTools == null || availableTools.Count == 0)
        {
            return;
        }

        var recoveredCalls = new List<FunctionCallContent>();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        var recoveredFromOriginalPrompt = false;

        RecoverLegacyToolCallsFromText(roundText, availableTools, state.Round, recoveredCalls, fingerprints);
        RecoverLegacyToolCallsFromText(roundThinking, availableTools, state.Round, recoveredCalls, fingerprints);
        if (recoveredCalls.Count == 0)
        {
            recoveredFromOriginalPrompt = RecoverExplicitCommandToolCallExamplesFromOriginalRequest(
                request,
                state,
                roundText.ToString(),
                roundThinking.ToString(),
                availableTools,
                recoveredCalls,
                fingerprints);
        }

        if (recoveredCalls.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "QueryRuntimeEngine[{EntryPoint}] recovered {RecoveredCount} legacy text tool call(s) for session {SessionId} on round {Round}",
            request.EntryPoint,
            recoveredCalls.Count,
            request.SessionId,
            state.Round);

        await EmitEventAsync(eventSink, new SystemNoticeEvent(
            Seq: seqBase + 190,
            QueryId: queryId,
            SessionId: request.SessionId,
            EntryPoint: request.EntryPoint,
            NoticeType: "legacy_tool_call_recovered",
            Content: recoveredFromOriginalPrompt
                ? $"Recovered {recoveredCalls.Count} explicit command tool call example(s) from the original user request after a thinking-only execution plan."
                : $"Recovered {recoveredCalls.Count} legacy text-based tool call(s) from model output.")).ConfigureAwait(false);

        foreach (var call in recoveredCalls)
        {
            roundToolCalls.Add(call);
            await EmitEventAsync(eventSink, new ToolCallRequestedEvent(
                Seq: seqBase + 200 + roundToolCalls.Count,
                QueryId: queryId,
                SessionId: request.SessionId,
                EntryPoint: request.EntryPoint,
                Round: state.Round,
                ToolName: call.Name ?? "unknown",
                CallId: call.CallId ?? string.Empty,
                Arguments: call.Arguments != null
                    ? new Dictionary<string, object?>(call.Arguments)
                    : new Dictionary<string, object?>())).ConfigureAwait(false);
        }
    }

    private static bool RecoverExplicitCommandToolCallExamplesFromOriginalRequest(
        QueryRuntimeRequest request,
        QueryRuntimeState state,
        string assistantText,
        string thinkingText,
        IReadOnlyList<AIFunction> availableTools,
        List<FunctionCallContent> recoveredCalls,
        HashSet<string> fingerprints)
    {
        if (!HasAnyCommandExecutionTool(availableTools))
        {
            return false;
        }

        var modelIntent = NormalizeIntentText($"{thinkingText} {assistantText}");
        if (!LooksLikePromptExampleCommandExecutionPlan(modelIntent))
        {
            return false;
        }

        var originalUserPrompt = request.InitialMessages
            .LastOrDefault(message => message.Role == ChatRole.User)?
            .Text;
        if (string.IsNullOrWhiteSpace(originalUserPrompt) ||
            PromptExplicitlyForbidsToolCalls(originalUserPrompt) ||
            !PromptRequiresCommandToolCalls(originalUserPrompt))
        {
            return false;
        }

        var parsedCalls = ParseDirectTextToolCalls(
                originalUserPrompt,
                availableTools,
                recoveredCalls.Count,
                state.Round,
                out _)
            .Where(call => IsCommandExecutionToolName(call.Name))
            .ToArray();
        if (parsedCalls.Length == 0 || parsedCalls.Length > 4)
        {
            return false;
        }

        var added = false;
        foreach (var call in parsedCalls)
        {
            var fingerprint = CreateLegacyToolCallFingerprint(call);
            if (fingerprints.Add(fingerprint))
            {
                recoveredCalls.Add(call);
                added = true;
            }
        }

        return added;
    }

    private static bool LooksLikePromptExampleCommandExecutionPlan(string text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            LooksLikeAnalyticalCompilation(text) ||
            LooksLikeCommandToolCapabilityDescription(text))
        {
            return false;
        }

        if (HasExplicitCommandToolExecutionIntent(text))
        {
            return true;
        }

        var hasExecutionVerb = Regex.IsMatch(
            text,
            @"\b(execute|executing|run|running|call|calling|invoke|invoking|make\s+(?:both|these|the)?\s*calls?)\b|调用|执行|运行",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hasCommandTarget =
            text.Contains("命令", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("command", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(
                text,
                @"`[^`]*(?:pwd|ls|dir|dotnet|npm|pnpm|yarn|pytest|mvn|gradle|python)[^`]*`",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hasActionLeadIn = Regex.IsMatch(
            text,
            @"\b(let me|need to|must|will|going to|now)\b|让我|需要|必须|现在",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return hasExecutionVerb && hasCommandTarget && hasActionLeadIn;
    }

    private static bool PromptRequiresCommandToolCalls(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var mentionsCommandTool =
            text.Contains("exec_cmd", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("run_command", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("run_tests", StringComparison.OrdinalIgnoreCase);
        if (!mentionsCommandTool)
        {
            return false;
        }

        return Regex.IsMatch(
            text,
            @"(必须|需要|务必|请)[^。；\n]{0,120}(调用|使用|发起|执行)[^。；\n]{0,120}(工具|exec_cmd|run_command|run_tests)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(
                text,
                @"\b(must|need\s+to|required\s+to|please)\b[^.\n]{0,120}\b(call|use|invoke|execute)\b[^.\n]{0,120}\b(tool|exec_cmd|run_command|run_tests)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool PromptExplicitlyForbidsToolCalls(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Regex.IsMatch(
            text,
            @"(不要|禁止|不得|不能|无需|不需要)[^。；\n]{0,40}(调用|使用|发起)[^。；\n]{0,40}(工具|exec_cmd|run_command|run_tests)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(
                text,
                @"\b(do\s+not|don't|must\s+not|never|no\s+need\s+to)\b[^.\n]{0,80}\b(call|use|invoke)\b[^.\n]{0,80}\b(tool|exec_cmd|run_command|run_tests)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsCommandExecutionToolName(string? toolName)
        => string.Equals(toolName, "exec_cmd", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(toolName, "run_command", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(toolName, "run_tests", StringComparison.OrdinalIgnoreCase);

    private static void RecoverLegacyToolCallsFromText(
        StringBuilder buffer,
        IReadOnlyList<AIFunction> availableTools,
        int round,
        List<FunctionCallContent> recoveredCalls,
        HashSet<string> fingerprints)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var rawText = buffer.ToString();
        var parsedCalls = new List<FunctionCallContent>();
        var workingText = rawText;

        if (HasLegacyToolCallMarkers(rawText))
        {
            parsedCalls.AddRange(ParseLegacyToolCalls(rawText, out workingText, recoveredCalls.Count, round));
        }

        var directCalls = ParseDirectTextToolCalls(
            workingText,
            availableTools,
            parsedCalls.Count + recoveredCalls.Count,
            round,
            out var cleanedText);
        var narratedCommandCalls = ParseNarratedCommandToolCalls(
            cleanedText,
            availableTools,
            parsedCalls.Count + directCalls.Count + recoveredCalls.Count,
            round,
            out var finalCleanedText);

        if (parsedCalls.Count == 0 && directCalls.Count == 0 && narratedCommandCalls.Count == 0)
        {
            var strippedText = StripLegacyToolCallResidues(rawText);
            if (!string.Equals(strippedText, rawText, StringComparison.Ordinal))
            {
                buffer.Clear();
                buffer.Append(strippedText);
            }

            return;
        }

        buffer.Clear();
        buffer.Append(finalCleanedText);

        foreach (var call in parsedCalls.Concat(directCalls).Concat(narratedCommandCalls))
        {
            if (!HasTool(availableTools, call.Name ?? string.Empty))
            {
                continue;
            }

            var fingerprint = CreateLegacyToolCallFingerprint(call);
            if (fingerprints.Add(fingerprint))
            {
                recoveredCalls.Add(call);
            }
        }
    }

    private static string CreateLegacyToolCallFingerprint(FunctionCallContent call)
    {
        var toolName = call.Name ?? string.Empty;
        var argsJson = call.Arguments == null
            ? string.Empty
            : BuildStableJsonLikeString(call.Arguments);
        return $"{toolName}\n{argsJson}";
    }

    private static bool HasLegacyToolCallMarkers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("<tool_call", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<toolcall", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<|tool_call>", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<|tool_call_begin|>", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<|tool_call_argument_begin|>", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<|tool_call_end|>", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<|tool_calls_section_begin|>", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<|tool_calls_section_end|>", StringComparison.OrdinalIgnoreCase)
               || text.Contains("</tool_call>", StringComparison.OrdinalIgnoreCase)
               || text.Contains("</toolcall>", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<invoke", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<minimax:tool_call", StringComparison.OrdinalIgnoreCase)
               || text.Contains("</minimax:tool_call>", StringComparison.OrdinalIgnoreCase);
    }

    private static List<FunctionCallContent> ParseLegacyToolCalls(
        string input,
        out string remainder,
        int callIndexBase,
        int round)
    {
        var calls = new List<FunctionCallContent>();
        remainder = input ?? string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return calls;
        }

        var blockPatterns = new[]
        {
            @"<(?:tool_call|toolcall|minimax:tool_call)>([\s\S]*?)</(?:tool_call|toolcall|minimax:tool_call)>",
            @"<\|tool_call>call:(?<name>[A-Za-z_][A-Za-z0-9_]*)\{(?<args>[\s\S]*?)\}<tool_call\|>"
        };
        const string transportProtocolPattern =
            @"<\|tool_call_begin\|>(?<name>[\s\S]*?)<\|tool_call_argument_begin\|>(?<args>[\s\S]*?)<\|tool_call_end\|>";

        var recoveredCount = 0;
        remainder = Regex.Replace(
            input,
            blockPatterns[0],
            match =>
            {
                if (!match.Success)
                {
                    return match.Value;
                }

                var inner = match.Groups[1].Value;
                var parsed = TryParseLegacyToolCallJson(inner, round, callIndexBase + recoveredCount)
                             ?? TryParseDirectTextToolCallFromLegacyBlock(inner, round, callIndexBase + recoveredCount);
                if (parsed != null)
                {
                    calls.Add(parsed);
                    recoveredCount++;
                    return string.Empty;
                }

                return inner;
            },
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (var match in Regex.Matches(
            remainder,
            blockPatterns[1],
            RegexOptions.Singleline | RegexOptions.IgnoreCase).Cast<Match>())
        {
            if (!match.Success)
            {
                continue;
            }

            var parsed = TryParseGemmaStyleToolCall(
                match.Groups["name"].Value,
                match.Groups["args"].Value,
                round,
                callIndexBase + recoveredCount);
            if (parsed != null)
            {
                calls.Add(parsed);
                recoveredCount++;
            }
        }

        remainder = Regex.Replace(
            remainder,
            transportProtocolPattern,
            match =>
            {
                if (!match.Success)
                {
                    return match.Value;
                }

                var parsed = TryParseTransportProtocolToolCall(
                    match.Groups["name"].Value,
                    match.Groups["args"].Value,
                    round,
                    callIndexBase + recoveredCount);
                if (parsed != null)
                {
                    calls.Add(parsed);
                    recoveredCount++;
                }

                return string.Empty;
            },
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        remainder = Regex.Replace(
            remainder,
            blockPatterns[1],
            string.Empty,
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        remainder = StripLegacyToolCallResidues(remainder);

        return calls;
    }

    private static List<FunctionCallContent> ParseDirectTextToolCalls(
        string input,
        IReadOnlyList<AIFunction> availableTools,
        int callIndexBase,
        int round,
        out string remainder)
    {
        var calls = new List<FunctionCallContent>();
        remainder = input ?? string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return calls;
        }

        var toolNames = availableTools
            .Select(tool => tool.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(name => name!.Length)
            .Cast<string>()
            .ToArray();

        if (toolNames.Length == 0)
        {
            return calls;
        }

        var toolPattern = string.Join("|", toolNames.Select(Regex.Escape));
        var regex = new Regex(
            $@"`?(?<name>{toolPattern})`?\s*\((?<args>\{{[\s\S]*?\}})\)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var recoveredCount = 0;
        foreach (var match in regex.Matches(input).Cast<Match>())
        {
            if (!match.Success)
            {
                continue;
            }

            var parsed = TryParseDirectTextToolCall(
                match.Groups["name"].Value,
                match.Groups["args"].Value,
                round,
                callIndexBase + recoveredCount);
            if (parsed != null)
            {
                calls.Add(parsed);
                recoveredCount++;
            }
        }

        remainder = regex.Replace(input, string.Empty);
        return calls;
    }

    private static List<FunctionCallContent> ParseNarratedCommandToolCalls(
        string input,
        IReadOnlyList<AIFunction> availableTools,
        int callIndexBase,
        int round,
        out string remainder)
    {
        var calls = new List<FunctionCallContent>();
        remainder = input ?? string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return calls;
        }

        var commandToolNames = availableTools
            .Select(tool => tool.Name)
            .Where(name => string.Equals(name, "exec_cmd", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(name, "run_command", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(name => name!.Length)
            .Cast<string>()
            .ToArray();
        if (commandToolNames.Length == 0)
        {
            return calls;
        }

        var toolPattern = string.Join("|", commandToolNames.Select(Regex.Escape));
        var regex = new Regex(
            $@"`?(?<name>{toolPattern})`?\s*(?:with|using|参数|命令|调用|执行|运行|:|：|为|是)?[^\[\]\r\n]{{0,120}}(?<command>\[\s*(?:""[^""]*""|'[^']*')\s*(?:,\s*(?:""[^""]*""|'[^']*')\s*)*\])",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var recoveredCount = 0;
        foreach (var match in regex.Matches(input).Cast<Match>())
        {
            if (!match.Success ||
                LooksLikeNegatedNarratedToolCall(input, match.Index, match.Length))
            {
                continue;
            }

            var command = ParseNarratedCommandArray(match.Groups["command"].Value);
            if (command.Length == 0)
            {
                continue;
            }

            calls.Add(new FunctionCallContent(
                CreateSyntheticLegacyToolCallId(round, callIndexBase + recoveredCount),
                match.Groups["name"].Value.Trim('`', ' '),
                new Dictionary<string, object?>
                {
                    ["command"] = command
                }));
            recoveredCount++;
        }

        remainder = calls.Count == 0
            ? input
            : regex.Replace(input, string.Empty);
        return calls;
    }

    private static string[] ParseNarratedCommandArray(string rawArray)
    {
        if (string.IsNullOrWhiteSpace(rawArray))
        {
            return [];
        }

        var values = new List<string>();
        foreach (Match match in Regex.Matches(
            rawArray,
            @"""(?<value>(?:\\.|[^""\\])*)""|'(?<value>(?:\\.|[^'\\])*)'",
            RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            var value = match.Groups["value"].Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(Regex.Unescape(value));
            }
        }

        return values.ToArray();
    }

    private static bool LooksLikeNegatedNarratedToolCall(string input, int index, int length)
    {
        var start = Math.Max(0, index - 80);
        var count = Math.Min(input.Length - start, length + 120);
        var context = input.Substring(start, count);
        return context.Contains("不要调用", StringComparison.OrdinalIgnoreCase) ||
               context.Contains("禁止调用", StringComparison.OrdinalIgnoreCase) ||
               context.Contains("do not call", StringComparison.OrdinalIgnoreCase) ||
               context.Contains("don't call", StringComparison.OrdinalIgnoreCase) ||
               context.Contains("must not call", StringComparison.OrdinalIgnoreCase);
    }

    private static FunctionCallContent? TryParseDirectTextToolCall(
        string? toolName,
        string? rawArguments,
        int round,
        int index)
    {
        if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(rawArguments))
        {
            return null;
        }

        try
        {
            var token = JToken.Parse(rawArguments);
            if (token is not JObject obj)
            {
                return null;
            }

            return new FunctionCallContent(
                CreateSyntheticLegacyToolCallId(round, index),
                toolName.Trim(),
                ParseLegacyArgumentsToken(obj));
        }
        catch (JsonReaderException)
        {
            return null;
        }
        catch (JsonSerializationException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static FunctionCallContent? TryParseDirectTextToolCallFromLegacyBlock(
        string? payload,
        int round,
        int index)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        var match = Regex.Match(
            payload,
            @"^\s*`?(?<name>[A-Za-z_][A-Za-z0-9_]*)`?\s*\((?<args>\{[\s\S]*\})\)\s*$",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            return null;
        }

        return TryParseDirectTextToolCall(
            match.Groups["name"].Value,
            match.Groups["args"].Value,
            round,
            index);
    }

    private static FunctionCallContent? TryParseLegacyToolCallJson(string? json, int round, int index)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var token = JToken.Parse(json);
            if (token is not JObject obj)
            {
                return null;
            }

            var callId = obj["id"]?.ToString() ?? obj["call_id"]?.ToString();
            string? name = obj["name"]?.ToString();
            JToken? argumentsToken = obj["arguments"];

            if (string.IsNullOrWhiteSpace(name) && obj["function"] is JObject functionObj)
            {
                name = functionObj["name"]?.ToString();
                argumentsToken = functionObj["arguments"];
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var args = ParseLegacyArgumentsToken(argumentsToken);
            return new FunctionCallContent(
                callId ?? CreateSyntheticLegacyToolCallId(round, index),
                name.Trim(),
                args);
        }
        catch (JsonReaderException)
        {
            return null;
        }
        catch (JsonSerializationException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static FunctionCallContent? TryParseGemmaStyleToolCall(
        string? name,
        string? rawArgs,
        int round,
        int index)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            var arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(
                rawArgs ?? string.Empty,
                @"(\w+):(?:<\|""\|>(.*?)<\|""\|>|([^,}]*))",
                RegexOptions.Singleline))
            {
                var key = match.Groups[1].Value;
                var rawValue = (match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value).Trim();
                arguments[key] = CastLegacyGemmaArgument(rawValue);
            }

            return new FunctionCallContent(
                CreateSyntheticLegacyToolCallId(round, index),
                name.Trim(),
                arguments);
        }
        catch
        {
            return null;
        }
    }

    private static FunctionCallContent? TryParseTransportProtocolToolCall(
        string? rawName,
        string? rawArguments,
        int round,
        int index)
    {
        var normalizedName = NormalizeLegacyToolCallName(rawName);
        if (string.IsNullOrWhiteSpace(normalizedName) || string.IsNullOrWhiteSpace(rawArguments))
        {
            return null;
        }

        try
        {
            var token = JToken.Parse(rawArguments);
            return new FunctionCallContent(
                CreateSyntheticLegacyToolCallId(round, index),
                normalizedName,
                ParseLegacyArgumentsToken(token));
        }
        catch (JsonReaderException)
        {
            return null;
        }
        catch (JsonSerializationException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string NormalizeLegacyToolCallName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return string.Empty;
        }

        var normalized = rawName.Trim();
        if (normalized.StartsWith("functions.", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["functions.".Length..];
        }

        var callIndexSeparator = normalized.LastIndexOf(':');
        if (callIndexSeparator >= 0 &&
            callIndexSeparator < normalized.Length - 1 &&
            int.TryParse(normalized[(callIndexSeparator + 1)..].Trim(), out _))
        {
            normalized = normalized[..callIndexSeparator];
        }

        return normalized.Trim();
    }

    private static Dictionary<string, object?> ParseLegacyArgumentsToken(JToken? argumentsToken)
    {
        if (argumentsToken == null || argumentsToken.Type == JTokenType.Null)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        if (argumentsToken is JObject argsObj)
        {
            return argsObj.Properties().ToDictionary(
                property => property.Name,
                property => ConvertLegacyJTokenToClr(property.Value),
                StringComparer.OrdinalIgnoreCase);
        }

        if (argumentsToken.Type == JTokenType.String)
        {
            var raw = argumentsToken.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    var parsed = JToken.Parse(raw);
                    if (parsed is JObject parsedObj)
                    {
                        return parsedObj.Properties().ToDictionary(
                            property => property.Name,
                            property => ConvertLegacyJTokenToClr(property.Value),
                            StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch (JsonReaderException)
                {
                }
                catch (JsonSerializationException)
                {
                }
                catch (InvalidOperationException)
                {
                }

                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["input"] = raw
                };
            }
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static object? ConvertLegacyJTokenToClr(JToken token)
        => token.Type switch
        {
            JTokenType.Object => ((JObject)token).Properties().ToDictionary(
                property => property.Name,
                property => ConvertLegacyJTokenToClr(property.Value),
                StringComparer.OrdinalIgnoreCase),
            JTokenType.Array => ((JArray)token).Select(ConvertLegacyJTokenToClr).ToArray(),
            JTokenType.Integer => token.Value<long>(),
            JTokenType.Float => token.Value<double>(),
            JTokenType.Boolean => token.Value<bool>(),
            JTokenType.Null => null,
            JTokenType.Undefined => null,
            _ => ((JValue)token).Value
        };

    private static string StripLegacyToolCallResidues(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = Regex.Replace(text, @"</?(?:tool_call|toolcall|minimax:tool_call)>", string.Empty, RegexOptions.IgnoreCase);
        text = text.Replace("<|tool_call>", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("<|tool_call_begin|>", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("<|tool_call_argument_begin|>", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("<|tool_call_end|>", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("<|tool_calls_section_begin|>", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("<|tool_calls_section_end|>", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("<tool_call|>", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("<|tool_response>", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("<tool_response|>", string.Empty, StringComparison.OrdinalIgnoreCase);

        text = Regex.Replace(text, @"[ \t]*}[ \t]*(?=\r?\n|$)", string.Empty);
        text = Regex.Replace(text, @"(?<=\A|\r?\n)[ \t]*{\s*", string.Empty);

        return text;
    }

    private static void SanitizeVisibleAssistantTextBuffer(StringBuilder roundText)
    {
        if (roundText.Length == 0)
        {
            return;
        }

        var original = roundText.ToString();
        var sanitized = SanitizeVisibleAssistantText(original);
        if (string.Equals(sanitized, original, StringComparison.Ordinal))
        {
            return;
        }

        roundText.Clear();
        roundText.Append(sanitized);
    }

    private static string SanitizeVisibleAssistantText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var stripped = StripLegacyToolCallResidues(text);
        if (!string.Equals(stripped, text, StringComparison.Ordinal))
        {
            return ContainsMeaningfulAssistantText(stripped)
                ? stripped
                : string.Empty;
        }

        return ContainsMeaningfulAssistantText(text)
            ? text
            : string.Empty;
    }

    private static bool ContainsMeaningfulAssistantText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch >= 0x2E80)
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateSyntheticLegacyToolCallId(int round, int index)
        => $"legacy-round-{round + 1}-call-{index + 1}";

    private static object? CastLegacyGemmaArgument(string value)
    {
        if (int.TryParse(value, out var intValue))
        {
            return intValue;
        }

        if (double.TryParse(value, out var doubleValue))
        {
            return doubleValue;
        }

        if (bool.TryParse(value, out var boolValue))
        {
            return boolValue;
        }

        return value;
    }

    private static bool ShouldReserveToolWrapUpRound(QueryRuntimeRequest request)
        => request.EnableTools && request.MaxRounds > 1;

    private static bool ShouldAllowToolCallsThisRound(QueryRuntimeRequest request, QueryRuntimeState state)
        => !state.ForceDisableToolCallsNextRound &&
           !ShouldForceReadOnlyAnalysisSynthesisRound(request, state) &&
           (state.ForceAllowToolCallsNextRound
            || !ShouldReserveToolWrapUpRound(request)
            || state.Round < state.MaxRounds - 1);

    // ── Inner types ───────────────────────────────────────────────────────────

    private enum TransportRecoveryOutcome
    {
        NotHandled,
        Continue,
        Terminate
    }

    private sealed record RoundResult(
        string Text,
        string Thinking,
        List<FunctionCallContent> ToolCalls,
        bool ShouldTerminate,
        QueryTerminationReason? TerminationReason);

    private sealed record StopHookContinuationResult(bool Continue, string? FinalTextOverride = null)
    {
        public static StopHookContinuationResult None { get; } = new(Continue: false);

        public static StopHookContinuationResult ContinueRound { get; } = new(Continue: true);
    }

    private sealed record RoundLlmRequestAssembly(
        IReadOnlyList<AIFunction> CurrentTools,
        LLMExecutionRequest Request);

    private sealed record ModelSamplingResult(
        string Text,
        string Thinking,
        IReadOnlyList<FunctionCallContent> ToolCalls,
        int StructuredToolCallCount);

    private static void InjectTrustedRuntimeArguments(Dictionary<string, object?> args, CodexSession? session)
    {
        ToolArgumentNormalizer.NormalizeInPlace(args);

        if (session == null)
        {
            return;
        }

        args["session_id"] = session.Id;
        args["workspace_path"] = session.WorkspacePath;
        args["project_root"] = ToolPathResolver.ResolveProjectRoot(
            session.WorkspacePath,
            null,
            session.ProjectUrl,
            session.Metadata);
    }
}
