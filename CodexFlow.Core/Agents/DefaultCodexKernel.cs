using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.VllmChatClient.Kimi;
using Microsoft.Extensions.Logging;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Constants;
using CodexFlow.Core.Agents.Tools;
using CodexFlow.Core.Agents.Adapters;
using CodexFlow.Core.Models;
using CodexFlow.Core.Services;
using CodexFlow.Core.Runtime;
using System.Net.Http;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;
using CodexFlow.Core.Telemetry;
using CodexFlow.Core.Protocols;
using CodexFlow.Core.Workers;

namespace CodexFlow.Core.Agents;

public partial class DefaultCodexKernel : ICodexAgentKernel
{
    private static long _toolCallTotalSinceProcessStart;
    private static long _toolCallFailedSinceProcessStart;

    private readonly IChatClient _chatClient;
    private readonly IToolRegistry _toolRegistry;
    private readonly ICodexCritiqueService _critiqueService;
    private readonly IAgentRoleRegistry _roleRegistry;
    private readonly ICodeAnalysisService _analysisService;
    private readonly ILogger<DefaultCodexKernel> _logger;
    private readonly CodexFlow.Core.Services.ProjectScanner _projectScanner;
    private readonly CodexSessionManager _sessionManager;
    private readonly ILLMExecutor? _llmExecutor;
    private readonly IQueryLoopTelemetry? _queryLoopTelemetry;
    private readonly IQueryRuntimeEngine? _queryRuntimeEngine;
    private readonly ICodexGuardrail? _guardrail;
    private readonly IWorkerDefinitionRegistry? _workerDefinitionRegistry;

#pragma warning disable CA1003 // Preserve legacy public event shape for compatibility.
    public event Action<CodexEvent>? OnEvent;
#pragma warning restore CA1003

    public static (long Total, long Failed, double FailureRate) GetProcessToolCallStats()
    {
        var total = Interlocked.Read(ref _toolCallTotalSinceProcessStart);
        var failed = Interlocked.Read(ref _toolCallFailedSinceProcessStart);
        var failureRate = total > 0 ? (double)failed / total : 0d;
        return (total, failed, failureRate);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "[{RoleName}] Starting reasoning cycle...")]
        public static partial void StartingReasoningCycle(ILogger logger, string roleName);

        [LoggerMessage(EventId = 1001, Level = LogLevel.Error, Message = "GetResponseAsync transient transport failures exceeded max retries ({MaxRetries}).")]
        public static partial void TransportRetriesExceeded(ILogger logger, Exception exception, int maxRetries);

        [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "GetResponseAsync transient transport failure ({Attempt}/{MaxRetries}). Retrying after {DelayMs}ms.")]
        public static partial void TransientTransportFailure(ILogger logger, Exception exception, int attempt, int maxRetries, int delayMs);

        [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Kernel: Detected truncated JSON in tool arguments. Attempting auto-repair via feedback loop.")]
        public static partial void TruncatedJsonDetected(ILogger logger);

        [LoggerMessage(EventId = 1004, Level = LogLevel.Warning, Message = "GetResponseAsync malformed tool-call protocol failure ({Attempt}/{MaxRetries}, silentRetry={SilentRetry}). ContextDiagnostics={ContextDiagnostics}")]
        public static partial void MalformedToolCallProtocolFailure(ILogger logger, Exception exception, int attempt, int maxRetries, bool silentRetry, string contextDiagnostics);

        [LoggerMessage(EventId = 1005, Level = LogLevel.Error, Message = "Malformed tool-call protocol failures reached max retries ({MaxRetries}). ContextDiagnostics={ContextDiagnostics}")]
        public static partial void MalformedToolCallProtocolRetriesExceeded(ILogger logger, Exception exception, int maxRetries, string contextDiagnostics);

        [LoggerMessage(EventId = 1006, Level = LogLevel.Information, Message = "Malformed tool-call protocol failure first occurrence in current streak. Retrying silently without appending corrective message to context.")]
        public static partial void MalformedToolCallSilentRetry(ILogger logger);

        [LoggerMessage(EventId = 1007, Level = LogLevel.Warning, Message = "GetResponseAsync failed. Attempting auto-retry with error feedback.")]
        public static partial void ResponseFailedRetryWithFeedback(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 1008, Level = LogLevel.Warning, Message = "[{RoleName}] Parsed {Count} legacy <tool_call> text call(s) and will execute them via compatibility path. Tools: {Tools}")]
        public static partial void ParsedLegacyToolCalls(ILogger logger, string roleName, int count, string tools);

        [LoggerMessage(EventId = 1009, Level = LogLevel.Warning, Message = "[{RoleName}] Detected legacy text-based tool call markup (round {RoundCount}) without FunctionCallContent. Hints: {ToolHints}. Snippet: {Snippet}")]
        public static partial void LegacyToolCallMarkupDetected(ILogger logger, string roleName, int roundCount, string toolHints, string snippet);

        [LoggerMessage(EventId = 1010, Level = LogLevel.Error, Message = "[{RoleName}] Legacy text tool-call markup repeated {Count} times without structured function calls. Aborting reasoning cycle.")]
        public static partial void LegacyToolCallMarkupRepeated(ILogger logger, string roleName, int count);

        [LoggerMessage(EventId = 1011, Level = LogLevel.Warning, Message = "Guardrail Triggered for {Path}: {Reason}")]
        public static partial void GuardrailTriggered(ILogger logger, string path, string? reason);

        [LoggerMessage(EventId = 1012, Level = LogLevel.Information, Message = "[{RoleName}] Proposed actions (Round {Round}/{MaxRound}):\n{Actions}")]
        public static partial void ProposedActions(ILogger logger, string roleName, int round, int maxRound, string actions);

        [LoggerMessage(EventId = 1013, Level = LogLevel.Information, Message = "Initiating peer review for proposed actions...")]
        public static partial void InitiatingPeerReview(ILogger logger);

        [LoggerMessage(EventId = 1014, Level = LogLevel.Error, Message = "CRITIQUE LOOP EXCEEDED MAX RETRIES (3). ABORTING REASONING CYCLE.\nLast proposed actions:\n{Actions}\nLast feedback:\n{Feedback}")]
        public static partial void CritiqueLoopExceededMaxRetries(ILogger logger, string actions, string? feedback);

        [LoggerMessage(EventId = 1015, Level = LogLevel.Warning, Message = "Critique failed ({RetryCount}/3):\n[Proposed]\n{Actions}\n[Feedback]\n{Feedback}")]
        public static partial void CritiqueFailed(ILogger logger, int retryCount, string actions, string? feedback);

        [LoggerMessage(EventId = 1016, Level = LogLevel.Debug, Message = "Invoking OnEvent for critique failure...")]
        public static partial void InvokingOnEventForCritiqueFailure(ILogger logger);

        [LoggerMessage(EventId = 1017, Level = LogLevel.Debug, Message = "Adding critique feedback to messages...")]
        public static partial void AddingCritiqueFeedbackToMessages(ILogger logger);

        [LoggerMessage(EventId = 1018, Level = LogLevel.Debug, Message = "Continuing to next iteration of reasoning loop...")]
        public static partial void ContinuingReasoningLoop(ILogger logger);

        [LoggerMessage(EventId = 1019, Level = LogLevel.Warning, Message = "Agent emitted {EmptyCount} tool call(s) with empty names (out of {Total} total). CallIds: {CallIds}")]
        public static partial void EmptyToolNamesDetected(ILogger logger, int emptyCount, int total, string callIds);

        [LoggerMessage(EventId = 1020, Level = LogLevel.Warning, Message = "Tool call quality stats since process start (including empty-name calls): total={TotalCalls}, failed={FailedCalls}, failureRate={FailureRate:P2}")]
        public static partial void ToolCallQualityStatsWithEmptyNames(ILogger logger, long totalCalls, long failedCalls, double failureRate);

        [LoggerMessage(EventId = 1021, Level = LogLevel.Error, Message = "Empty-tool-name abort diagnostics: Role={RoleName}, Round={Round}, MessageCount={MessageCount}, LastMessageRole={LastMessageRole}, ContentDiagnostics={ContentDiagnostics}")]
        public static partial void EmptyToolNameAbortDiagnosticsSummary(ILogger logger, string roleName, int round, int messageCount, string lastMessageRole, string contentDiagnostics);

        [LoggerMessage(EventId = 1022, Level = LogLevel.Error, Message = "Empty-tool-name abort diagnostics: ResponseTextRawLength={Length}, ResponseTextRaw={ResponseTextRaw}")]
        public static partial void EmptyToolNameAbortDiagnosticsResponseText(ILogger logger, int length, string responseTextRaw);

        [LoggerMessage(EventId = 1023, Level = LogLevel.Error, Message = "Empty-tool-name abort diagnostics: ToolCalls={ToolCalls}")]
        public static partial void EmptyToolNameAbortDiagnosticsToolCalls(ILogger logger, string toolCalls);

        [LoggerMessage(EventId = 1024, Level = LogLevel.Error, Message = "Consecutive empty tool name rounds reached {Count}. Aborting reasoning cycle.")]
        public static partial void ConsecutiveEmptyToolNameRoundsReached(ILogger logger, int count);

        [LoggerMessage(EventId = 1025, Level = LogLevel.Warning, Message = "All {Count} tool calls had empty names. Injecting recovery prompt instead of processing individually.")]
        public static partial void AllToolCallsHadEmptyNames(ILogger logger, int count);

        [LoggerMessage(EventId = 1026, Level = LogLevel.Warning, Message = "Skipping repeated tool execution via cached result: tool={ToolName}, repeatCount={RepeatCount}, threshold={Threshold}, signature={Signature}")]
        public static partial void SkippingRepeatedToolExecutionViaCache(ILogger logger, string toolName, int repeatCount, int threshold, string signature);

        [LoggerMessage(EventId = 1027, Level = LogLevel.Information, Message = "Agent calling tool: {ToolName} with args: {Args}")]
        public static partial void AgentCallingTool(ILogger logger, string toolName, string args);

        [LoggerMessage(EventId = 1028, Level = LogLevel.Warning, Message = "Failed to emit CodePreview event")]
        public static partial void FailedToEmitCodePreviewEvent(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 1029, Level = LogLevel.Error, Message = "Tool execution failed: {ToolName}")]
        public static partial void ToolExecutionFailed(ILogger logger, Exception exception, string toolName);

        [LoggerMessage(EventId = 1030, Level = LogLevel.Information, Message = "TOOLCALL|sessionId={SessionId}|taskId={TaskId}|tool={ToolName}|source={Source}|elapsedMs={ElapsedMs}|failed={Failed}|args={Args}|output={Output}")]
        public static partial void ToolCallTrace(ILogger logger, string sessionId, string taskId, string toolName, string source, long elapsedMs, bool failed, string args, string output);

        [LoggerMessage(EventId = 1031, Level = LogLevel.Information, Message = "Tool execution completed: tool={ToolName}, source={Source}, elapsedMs={ElapsedMs}, failed={Failed}, outputPreview={OutputPreview}")]
        public static partial void ToolExecutionCompleted(ILogger logger, string toolName, string source, long elapsedMs, bool failed, string outputPreview);

        [LoggerMessage(EventId = 1032, Level = LogLevel.Information, Message = "Tool call quality stats since process start: total={TotalCalls}, failed={FailedCalls}, failureRate={FailureRate:P2}, lastTool={ToolName}, lastResult={LastResult}")]
        public static partial void ToolCallQualityStats(ILogger logger, long totalCalls, long failedCalls, double failureRate, string toolName, string lastResult);

        [LoggerMessage(EventId = 1033, Level = LogLevel.Error, Message = "Critical error in RunLoopAsync: {Message}")]
        public static partial void CriticalRunLoopError(ILogger logger, Exception exception, string message);

        [LoggerMessage(EventId = 1034, Level = LogLevel.Warning, Message = "Legacy <tool_call> emitted empty tool name. Tool call quality stats since process start: total={TotalCalls}, failed={FailedCalls}, failureRate={FailureRate:P2}")]
        public static partial void LegacyEmptyToolName(ILogger logger, long totalCalls, long failedCalls, double failureRate);

        [LoggerMessage(EventId = 1035, Level = LogLevel.Information, Message = "Agent calling tool via legacy <tool_call> compatibility path: {ToolName} with args: {Args}")]
        public static partial void AgentCallingToolLegacy(ILogger logger, string toolName, string args);

        [LoggerMessage(EventId = 1036, Level = LogLevel.Warning, Message = "Legacy <tool_call> tool not found: {ToolName}. Tool call quality stats since process start: total={TotalCalls}, failed={FailedCalls}, failureRate={FailureRate:P2}")]
        public static partial void LegacyToolNotFound(ILogger logger, string toolName, long totalCalls, long failedCalls, double failureRate);

        [LoggerMessage(EventId = 1037, Level = LogLevel.Information, Message = "Legacy tool execution completed: tool={ToolName}, source=legacy, elapsedMs={ElapsedMs}, failed={Failed}, outputPreview={OutputPreview}")]
        public static partial void LegacyToolExecutionCompleted(ILogger logger, string toolName, long elapsedMs, bool failed, string outputPreview);

        [LoggerMessage(EventId = 1038, Level = LogLevel.Information, Message = "Legacy <tool_call> execution finished. Tool call quality stats since process start: total={TotalCalls}, failed={FailedCalls}, failureRate={FailureRate:P2}, lastTool={ToolName}, lastResult={LastResult}")]
        public static partial void LegacyToolCallQualityStats(ILogger logger, long totalCalls, long failedCalls, double failureRate, string toolName, string lastResult);

        [LoggerMessage(EventId = 1039, Level = LogLevel.Error, Message = "Legacy <tool_call> compatibility execution failed: {ToolName}")]
        public static partial void LegacyToolExecutionFailed(ILogger logger, Exception exception, string toolName);

        [LoggerMessage(EventId = 1040, Level = LogLevel.Error, Message = "Legacy <tool_call> failure stats since process start: total={TotalCalls}, failed={FailedCalls}, failureRate={FailureRate:P2}")]
        public static partial void LegacyToolFailureStats(ILogger logger, long totalCalls, long failedCalls, double failureRate);

        [LoggerMessage(EventId = 1041, Level = LogLevel.Information, Message = "Dynamic Index Sync: Added {Path}")]
        public static partial void DynamicIndexSyncAdded(ILogger logger, string path);

        [LoggerMessage(EventId = 1042, Level = LogLevel.Warning, Message = "Failed to update dynamic index: {Message}")]
        public static partial void FailedToUpdateDynamicIndex(ILogger logger, string message);
    }

    public DefaultCodexKernel(
        IChatClient chatClient,
        IToolRegistry toolRegistry,
        ICodexCritiqueService critiqueService,
        IAgentRoleRegistry roleRegistry,
        ICodeAnalysisService analysisService,
        ILogger<DefaultCodexKernel> logger,
        CodexFlow.Core.Services.ProjectScanner projectScanner,
        CodexSessionManager sessionManager,
        ILLMExecutor? llmExecutor = null,
        IQueryLoopTelemetry? queryLoopTelemetry = null,
        IQueryRuntimeEngine? queryRuntimeEngine = null,
        ICodexGuardrail? guardrail = null,
        IWorkerDefinitionRegistry? workerDefinitionRegistry = null)
    {
        _chatClient = chatClient;
        _toolRegistry = toolRegistry;
        _critiqueService = critiqueService;
        _roleRegistry = roleRegistry;
        _analysisService = analysisService;
        _logger = logger;
        _projectScanner = projectScanner;
        _sessionManager = sessionManager;
        _workerDefinitionRegistry = workerDefinitionRegistry;
        _llmExecutor = llmExecutor;
        _queryLoopTelemetry = queryLoopTelemetry;
        _queryRuntimeEngine = queryRuntimeEngine;
        _guardrail = guardrail;
    }

#pragma warning disable CA1068 // Preserve legacy public parameter order for compatibility.
    public async Task<CodexResponse> RunLoopAsync(CodexSession session, string userPrompt, CodexAgentRole role = CodexAgentRole.Forge, CancellationToken ct = default, bool enableTools = true, TaskFileScopeDescriptor? taskFileScope = null)
#pragma warning restore CA1068
    {
        ArgumentNullException.ThrowIfNull(session);

        // Phase 4A: Runtime 路径 (渐进式迁移，通过环境变量控制)
        var useRuntime = _queryRuntimeEngine != null &&
            !string.Equals(Environment.GetEnvironmentVariable("KERNEL_DISABLE_RUNTIME"), "true", StringComparison.OrdinalIgnoreCase);

        if (useRuntime)
        {
            _logger.LogInformation("Using IQueryRuntimeEngine for DefaultCodexKernel");
            try
            {
                var result = await RunLoopWithRuntimeAsync(session, userPrompt, role, enableTools, taskFileScope, ct);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Runtime execution failed in Kernel, falling back to manual implementation");
                // Fall through to manual implementation
            }
        }

        // 原有实现
        try
        {
            var workerType = TryMapRoleToWorkerType(role);
            var workerDefinition = ResolveWorkerDefinition(workerType);
            ActivateWorkerDeferredTools(workerDefinition);
            var promptSession = workerType.HasValue
                ? WorkerExecutionSessionFactory.CreateForWorker(session, workerType.Value)
                : session;
            var rolePrompt = workerDefinition?.BuildSystemPrompt(promptSession)
                ?? _roleRegistry.GetSystemPrompt(role, session)
                ?? "你是一个 AI 助手。";
            if (enableTools)
            {
                IEnumerable<ICodexTool> initialBaseTools = _toolRegistry.GetAvailableTools(session) ?? Enumerable.Empty<ICodexTool>();
                if (workerDefinition != null && workerType.HasValue)
                {
                    initialBaseTools = _workerDefinitionRegistry!.FilterAvailableTools(workerType.Value, initialBaseTools);
                }
                else if (role == CodexAgentRole.Coordinator)
                {
                    initialBaseTools = CoordinatorToolSurfacePolicy.Filter(initialBaseTools);
                }
                else if (role == CodexAgentRole.Security)
                {
                    initialBaseTools = initialBaseTools.Where(t => t.Category == ToolCategory.Read || t.Category == ToolCategory.Analysis);
                }
                else if (role == CodexAgentRole.Forge)
                {
                    initialBaseTools = initialBaseTools.Where(t =>
                        !string.Equals(t.Name, "execute_code_task", StringComparison.OrdinalIgnoreCase) &&
                        !PlanningToolNames.IsPlanCreationTool(t.Name));
                }

                var workerContext = workerType.HasValue && workerDefinition != null
                    ? _workerDefinitionRegistry!.BuildRuntimeContext(workerType.Value)
                    : null;
                rolePrompt = ToolCatalogPromptComposer.AppendRuntimeToolGuidance(
                    rolePrompt,
                    initialBaseTools.ToList(),
                    workerContext);
            }
            var roleName = workerDefinition?.DisplayName
                ?? _roleRegistry.GetRoleName(role)
                ?? "Ivilson-Agent";

            var projectSummary = session.ProjectSummary ?? "（空项目摘要）";

            var messages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, $"{rolePrompt}\n\n# 当前项目上下文\n{projectSummary}"),
                new ChatMessage(ChatRole.User, userPrompt ?? "开始执行。")
            };

            Log.StartingReasoningCycle(_logger, roleName);

            var sb = new StringBuilder();
            var thinkingSb = new StringBuilder();
            int critiqueRetryCount = 0;
            int transportFailureCount = 0;
            int malformedToolProtocolFailureCount = 0;
            int consecutiveEmptyToolNameRounds = 0; // [Fix] Track rounds where ALL tool calls have empty names
            int consecutiveLegacyTextToolCallRounds = 0; // [Compat] Track legacy <toolcall>/<invoke> text-only outputs
            string? lastDedupToolCallSignature = null;
            string? lastDedupToolCallResult = null;
            bool lastDedupToolCallFailed = false;
            int consecutiveSameDedupToolCallCount = 0;
            const int maxTransientTransportRetries = 3;
            const int maxMalformedToolProtocolRetries = 3;
            const int duplicateToolExecutionCacheThreshold = 3;

            // Tool call statistics for Orchestrator-level zero-tool-call detection
            int totalToolCalls = 0;
            int writeToolCalls = 0;

            // Manual loop to handle tool calls
            int guard = 0;
            const int MaxInternalRounds = 60; // [Fix] Increase internal tool loop limit

            // [Phase 0 Telemetry] Per-query tracking state
            var _ql_queryId = Guid.NewGuid();
            var _ql_stopwatch = Stopwatch.StartNew();
            int _ql_zeroToolCallRounds = 0;
            int _ql_emptyResponseCount = 0;
            int _ql_recoveryCount = 0;
            var _ql_termination = QueryTerminationReason.Normal;
            int _ql_totalPromptTokens = 0;
            int _ql_totalCompletionTokens = 0;
            var _ql_initialContextChars = messages.Sum(m => m.Text?.Length ?? 0);
            _queryLoopTelemetry?.RecordStart(new QueryLoopStarted(
                _ql_queryId, session.Id ?? "unknown", QueryLoopEntryPoint.DefaultCodexKernel,
                DateTimeOffset.UtcNow, MaxInternalRounds, _ql_initialContextChars));

            while (guard++ < MaxInternalRounds)
            {
                var _ql_roundStartMs = _ql_stopwatch.ElapsedMilliseconds;
                var _ql_roundTextStart = sb.Length;
                int? _ql_roundPromptTokens = null;
                int? _ql_roundCompletionTokens = null;

                var baseTools = _toolRegistry.GetAvailableTools(session) ?? Enumerable.Empty<ICodexTool>();

                if (workerDefinition != null && workerType.HasValue)
                {
                    baseTools = _workerDefinitionRegistry!.FilterAvailableTools(workerType.Value, baseTools);
                }
                else if (role == CodexAgentRole.Coordinator)
                {
                    baseTools = CoordinatorToolSurfacePolicy.Filter(baseTools);
                }
                else if (role == CodexAgentRole.Security)
                {
                    baseTools = baseTools.Where(t => t.Category == ToolCategory.Read || t.Category == ToolCategory.Analysis);
                }
                else if (role == CodexAgentRole.Forge)
                {
                    // [Fix] Forge role (task implementation) must NOT have access to meta-execution tools
                    // to prevent infinite recursion and prompt confusion.
                    baseTools = baseTools.Where(t =>
                        !string.Equals(t.Name, "execute_code_task", StringComparison.OrdinalIgnoreCase) &&
                        !PlanningToolNames.IsPlanCreationTool(t.Name));
                }

                var availableTools = enableTools
                    ? baseTools.Select(CodexToolFunctionAdapterFactory.CreateAIFunction).ToList()
                    : new List<AIFunction>();

                var options = new ChatOptions
                {
                    Temperature = 0.7f
                    // MaxOutputTokens intentionally unset — let the model use its full context.
                    // A hard cap (e.g. 4000) can truncate thinking tokens before the model
                    // emits function calls, causing zero-tool-call failures.
                };

                if (availableTools != null && availableTools.Count > 0)
                {
                    options.Tools = availableTools.Cast<AITool>().ToList();
                }

                // Streaming LLM call with thinking chain collection
                ChatResponse response;
                try
                {
                    var (responseObj, roundThinking) = await StreamResponseAsync(messages, options, session, ct).ConfigureAwait(false);
                    response = responseObj;
                    if (!string.IsNullOrEmpty(roundThinking))
                    {
                        _logger.LogInformation("LLM thinking (round {Round}): {Thinking}", guard, TruncateForLog(roundThinking, 2000));
                        thinkingSb.Append(roundThinking);
                    }
                    transportFailureCount = 0;
                    malformedToolProtocolFailureCount = 0;
                    _ql_roundPromptTokens = (int?)response?.Usage?.InputTokenCount;
                    _ql_roundCompletionTokens = (int?)response?.Usage?.OutputTokenCount;
                }
                catch (HttpRequestException ex)
                {
                    if (IsTransientTransportFailure(ex, ct))
                    {
                        transportFailureCount++;
                        if (transportFailureCount > maxTransientTransportRetries)
                        {
                            Log.TransportRetriesExceeded(_logger, ex, maxTransientTransportRetries);
                            _queryLoopTelemetry?.RecordTermination(new QueryLoopTerminated(_ql_queryId, session.Id ?? "unknown", QueryLoopEntryPoint.DefaultCodexKernel, QueryTerminationReason.RecoveryExhausted, QueryTerminalDetailCodes.RecoveryExhaustedTransportFailure, guard - 1, totalToolCalls, _ql_zeroToolCallRounds, malformedToolProtocolFailureCount, _ql_emptyResponseCount, _ql_recoveryCount, _ql_stopwatch.ElapsedMilliseconds));
                            return new CodexResponse("模型服务连接不稳定（网络响应中断），已超过自动重试上限。请检查模型网关或稍后重试。", false);
                        }

                        var delayMs = Math.Min(500 * transportFailureCount, 1500);
                        Log.TransientTransportFailure(_logger, ex, transportFailureCount, maxTransientTransportRetries, delayMs);

                        if (delayMs > 0 && !ct.IsCancellationRequested)
                        {
                            await Task.Delay(delayMs, ct).ConfigureAwait(false);
                        }
                        _ql_recoveryCount++;
                        _queryLoopTelemetry?.RecordRecovery(new QueryLoopRecovery(_ql_queryId, session.Id ?? "unknown", QueryLoopEntryPoint.DefaultCodexKernel, guard, "transport_failure", transportFailureCount, true, false));
                        continue;
                    }

                    transportFailureCount = 0;

                    Log.ResponseFailedRetryWithFeedback(_logger, ex);
                    messages.Add(new ChatMessage(ChatRole.User, $"你的上一个工具调用导致了系统解析错误：{ex.Message}。请重试，并确保 arguments 是合法 JSON 对象，参数直接放在顶层（如 {{\"path\":\"...\"}}），不要额外包裹 args/arguments/input_params。"));
                    _ql_recoveryCount++;
                    continue;
                }
                catch (TaskCanceledException ex)
                {
                    if (IsTransientTransportFailure(ex, ct))
                    {
                        transportFailureCount++;
                        if (transportFailureCount > maxTransientTransportRetries)
                        {
                            Log.TransportRetriesExceeded(_logger, ex, maxTransientTransportRetries);
                            _queryLoopTelemetry?.RecordTermination(new QueryLoopTerminated(_ql_queryId, session.Id ?? "unknown", QueryLoopEntryPoint.DefaultCodexKernel, QueryTerminationReason.RecoveryExhausted, QueryTerminalDetailCodes.RecoveryExhaustedTransportFailure, guard - 1, totalToolCalls, _ql_zeroToolCallRounds, malformedToolProtocolFailureCount, _ql_emptyResponseCount, _ql_recoveryCount, _ql_stopwatch.ElapsedMilliseconds));
                            return new CodexResponse("模型服务连接不稳定（网络响应中断），已超过自动重试上限。请检查模型网关或稍后重试。", false);
                        }

                        var delayMs = Math.Min(500 * transportFailureCount, 1500);
                        Log.TransientTransportFailure(_logger, ex, transportFailureCount, maxTransientTransportRetries, delayMs);

                        if (delayMs > 0 && !ct.IsCancellationRequested)
                        {
                            await Task.Delay(delayMs, ct).ConfigureAwait(false);
                        }
                        _ql_recoveryCount++;
                        _queryLoopTelemetry?.RecordRecovery(new QueryLoopRecovery(_ql_queryId, session.Id ?? "unknown", QueryLoopEntryPoint.DefaultCodexKernel, guard, "transport_failure", transportFailureCount, true, false));
                        continue;
                    }

                    // [v26 Fix] 尝试从异常消息中抢救被截断的 JSON（针对 Newtonsoft 抛出的异常）
                    if (ex.Message.Contains("Unexpected end", StringComparison.Ordinal) && ex.Message.Contains("Path", StringComparison.Ordinal))
                    {
                        Log.TruncatedJsonDetected(_logger);
                        messages.Add(new ChatMessage(ChatRole.User, "你的上一个工具调用参数 JSON 被截断了（缺少闭合括号）。请重新发送完整的 JSON，确保以 `}` 结尾。"));
                        continue;
                    }

                    if (IsMalformedToolCallProtocolFailure(ex))
                    {
                        malformedToolProtocolFailureCount++;
                        transportFailureCount = 0;
                        var silentRetry = malformedToolProtocolFailureCount == 1;

                        var contextDiagnostics = BuildKernelContextDiagnosticsJson(messages, roleName, guard, availableTools?.Count ?? 0);

                        Log.MalformedToolCallProtocolFailure(
                            _logger,
                            ex,
                            malformedToolProtocolFailureCount,
                            maxMalformedToolProtocolRetries,
                            silentRetry,
                            contextDiagnostics);

                        if (malformedToolProtocolFailureCount >= maxMalformedToolProtocolRetries)
                        {
                            Log.MalformedToolCallProtocolRetriesExceeded(_logger, ex, maxMalformedToolProtocolRetries, contextDiagnostics);
                            _queryLoopTelemetry?.RecordTermination(new QueryLoopTerminated(_ql_queryId, session.Id ?? "unknown", QueryLoopEntryPoint.DefaultCodexKernel, QueryTerminationReason.RecoveryExhausted, QueryTerminalDetailCodes.RecoveryExhaustedMalformedProtocol, guard - 1, totalToolCalls, _ql_zeroToolCallRounds, malformedToolProtocolFailureCount, _ql_emptyResponseCount, _ql_recoveryCount, _ql_stopwatch.ElapsedMilliseconds));
                            return new CodexResponse("模型连续返回无效工具调用协议（空工具名或非法参数 JSON），已超过内核自动恢复上限。请重试或缩短上下文后再试。", false);
                        }

                        var delayMs = Math.Min(250 * malformedToolProtocolFailureCount, 750);
                        if (delayMs > 0 && !ct.IsCancellationRequested)
                        {
                            await Task.Delay(delayMs, ct).ConfigureAwait(false);
                        }

                        if (silentRetry)
                        {
                            Log.MalformedToolCallSilentRetry(_logger);
                            _ql_recoveryCount++;
                            _queryLoopTelemetry?.RecordRecovery(new QueryLoopRecovery(_ql_queryId, session.Id ?? "unknown", QueryLoopEntryPoint.DefaultCodexKernel, guard, "malformed_protocol", malformedToolProtocolFailureCount, true, false));
                            continue;
                        }

                        messages.Add(new ChatMessage(
                            ChatRole.User,
                            "⚠️ [SYSTEM] 你上一轮返回了无效工具调用协议（例如 function name 为空，或 arguments 不是合法 JSON 对象）。" +
                            "请重新发送标准结构化 function/tool call：function name 必须非空；arguments 必须是 JSON 对象。" +
                            "无参工具请使用 `{}`，不要使用空字符串。"));
                        _ql_recoveryCount++;
                        _queryLoopTelemetry?.RecordRecovery(new QueryLoopRecovery(_ql_queryId, session.Id ?? "unknown", QueryLoopEntryPoint.DefaultCodexKernel, guard, "malformed_protocol", malformedToolProtocolFailureCount, true, false));
                        continue;
                    }

                    transportFailureCount = 0;

                    Log.ResponseFailedRetryWithFeedback(_logger, ex);
                    messages.Add(new ChatMessage(ChatRole.User, $"你的上一个工具调用导致了系统解析错误：{ex.Message}。请重试，并确保 arguments 是合法 JSON 对象，参数直接放在顶层（如 {{\"path\":\"...\"}}），不要额外包裹 args/arguments/input_params。"));
                    continue;
                }
                catch (InvalidOperationException ex)
                {
                    // [v26 Fix] 尝试从异常消息中抢救被截断的 JSON（针对 Newtonsoft 抛出的异常）
                    if (ex.Message.Contains("Unexpected end", StringComparison.Ordinal) && ex.Message.Contains("Path", StringComparison.Ordinal))
                    {
                        Log.TruncatedJsonDetected(_logger);
                        messages.Add(new ChatMessage(ChatRole.User, "你的上一个工具调用参数 JSON 被截断了（缺少闭合括号）。请重新发送完整的 JSON，确保以 `}` 结尾。"));
                        continue;
                    }

                    if (IsMalformedToolCallProtocolFailure(ex))
                    {
                        malformedToolProtocolFailureCount++;
                        transportFailureCount = 0;
                        var silentRetry = malformedToolProtocolFailureCount == 1;

                        var contextDiagnostics = BuildKernelContextDiagnosticsJson(messages, roleName, guard, availableTools?.Count ?? 0);

                        Log.MalformedToolCallProtocolFailure(
                            _logger,
                            ex,
                            malformedToolProtocolFailureCount,
                            maxMalformedToolProtocolRetries,
                            silentRetry,
                            contextDiagnostics);

                        if (malformedToolProtocolFailureCount >= maxMalformedToolProtocolRetries)
                        {
                            Log.MalformedToolCallProtocolRetriesExceeded(_logger, ex, maxMalformedToolProtocolRetries, contextDiagnostics);
                            _queryLoopTelemetry?.RecordTermination(new QueryLoopTerminated(_ql_queryId, session.Id ?? "unknown", QueryLoopEntryPoint.DefaultCodexKernel, QueryTerminationReason.RecoveryExhausted, QueryTerminalDetailCodes.RecoveryExhaustedMalformedProtocol, guard - 1, totalToolCalls, _ql_zeroToolCallRounds, malformedToolProtocolFailureCount, _ql_emptyResponseCount, _ql_recoveryCount, _ql_stopwatch.ElapsedMilliseconds));
                            return new CodexResponse("模型连续返回无效工具调用协议（空工具名或非法参数 JSON），已超过内核自动恢复上限。请重试或缩短上下文后再试。", false);
                        }

                        var delayMs = Math.Min(250 * malformedToolProtocolFailureCount, 750);
                        if (delayMs > 0 && !ct.IsCancellationRequested)
                        {
                            await Task.Delay(delayMs, ct).ConfigureAwait(false);
                        }

                        if (silentRetry)
                        {
                            Log.MalformedToolCallSilentRetry(_logger);
                            _ql_recoveryCount++;
                            _queryLoopTelemetry?.RecordRecovery(new QueryLoopRecovery(_ql_queryId, session.Id ?? "unknown", QueryLoopEntryPoint.DefaultCodexKernel, guard, "malformed_protocol", malformedToolProtocolFailureCount, true, false));
                            continue;
                        }

                        messages.Add(new ChatMessage(
                            ChatRole.User,
                            "⚠️ [SYSTEM] 你上一轮返回了无效工具调用协议（例如 function name 为空，或 arguments 不是合法 JSON 对象）。" +
                            "请重新发送标准结构化 function/tool call：function name 必须非空；arguments 必须是 JSON 对象。" +
                            "无参工具请使用 `{}`，不要使用空字符串。"));
                        _ql_recoveryCount++;
                        _queryLoopTelemetry?.RecordRecovery(new QueryLoopRecovery(_ql_queryId, session.Id ?? "unknown", QueryLoopEntryPoint.DefaultCodexKernel, guard, "malformed_protocol", malformedToolProtocolFailureCount, true, false));
                        continue;
                    }

                    transportFailureCount = 0;

                    Log.ResponseFailedRetryWithFeedback(_logger, ex);
                    messages.Add(new ChatMessage(ChatRole.User, $"你的上一个工具调用导致了系统解析错误：{ex.Message}。请重试，并确保 arguments 是合法 JSON 对象，参数直接放在顶层（如 {{\"path\":\"...\"}}），不要额外包裹 args/arguments/input_params。"));
                    continue;
                }

                if (response == null || response.Messages.Count == 0)
                {
                    _ql_emptyResponseCount++;
                    _ql_termination = QueryTerminationReason.EmptyResponseFallback;
                    break;
                }

                var lastMessage = response.Messages.Last();
                var responseText = new StringBuilder();
                List<FunctionCallContent> toolCalls = new();

                foreach (var content in lastMessage.Contents)
                {
                    if (content == null) continue;

                    if (content is TextContent tc)
                    {
                        responseText.Append(tc.Text);
                        sb.Append(tc.Text);
                    }
                    else if (content is FunctionCallContent fc)
                    {
                        toolCalls.Add(fc);
                    }
                }

                var responseTextRaw = responseText.ToString();

                if (toolCalls.Count == 0 && enableTools)
                {
                    var parsedLegacyToolCalls = ParseToolCalls(responseTextRaw, out var remainderText).ToList();
                    if (parsedLegacyToolCalls.Count > 0)
                    {
                        consecutiveLegacyTextToolCallRounds = 0;
                        Log.ParsedLegacyToolCalls(
                            _logger,
                            roleName,
                            parsedLegacyToolCalls.Count,
                            string.Join(", ", parsedLegacyToolCalls.Select(x => x.Name)));

                        if (!string.IsNullOrWhiteSpace(remainderText))
                        {
                            messages.Add(new ChatMessage(ChatRole.Assistant, remainderText.Trim()));
                        }

                        var legacyResults = new List<string>();
                        foreach (var legacyCall in parsedLegacyToolCalls)
                        {
                            var compatOutput = await ExecuteLegacyTextToolCallAsync(session, legacyCall, ct).ConfigureAwait(false);
                            legacyResults.Add($"工具 `{legacyCall.Name}` 执行结果:\n{compatOutput}");
                        }

                        messages.Add(new ChatMessage(
                            ChatRole.User,
                            "⚠️ [SYSTEM] 已兼容执行你通过 `<tool_call>...</tool_call>` 文本标签返回的工具调用。" +
                            "后续请改用标准结构化 function/tool call（不要再使用文本标签格式）。\n\n" +
                            string.Join("\n\n", legacyResults)));

                        continue;
                    }
                }

                if (toolCalls.Count == 0 && enableTools && ContainsLegacyTextToolCallMarkup(responseTextRaw))
                {
                    consecutiveLegacyTextToolCallRounds++;
                    var legacyToolHints = ExtractLegacyToolCallNames(responseTextRaw);
                    var snippet = TruncateForLog(responseTextRaw, 500);

                    Log.LegacyToolCallMarkupDetected(
                        _logger,
                        roleName,
                        consecutiveLegacyTextToolCallRounds,
                        legacyToolHints.Count > 0 ? string.Join(", ", legacyToolHints) : "(none)",
                        snippet);

                    if (consecutiveLegacyTextToolCallRounds >= 2)
                    {
                        Log.LegacyToolCallMarkupRepeated(_logger, roleName, consecutiveLegacyTextToolCallRounds);
                        throw new InvalidOperationException(
                            $"模型连续 {consecutiveLegacyTextToolCallRounds} 次返回旧版文本工具调用格式（如 <toolcall>/<invoke>），未返回标准 FunctionCall。推理循环已中止。");
                    }

                    var availableToolNames = (_toolRegistry.GetAvailableTools(session) ?? Enumerable.Empty<ICodexTool>())
                        .Select(t => t.Name)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Take(15)
                        .ToList();

                    var toolListText = availableToolNames.Count > 0
                        ? $"\n可用工具名示例：{string.Join(", ", availableToolNames)}"
                        : string.Empty;

                    messages.Add(new ChatMessage(ChatRole.User,
                        "⚠️ [SYSTEM] 你上一轮返回的是旧版文本工具调用格式（例如 `<toolcall>` / `<invoke>`），" +
                        "当前系统只会执行标准 function/tool call（结构化调用），不会执行文本标签格式。" +
                        "请重新发送标准工具调用，不要把工具调用写成 XML/标签文本。" +
                        toolListText));
                    continue;
                }

                if (toolCalls.Count == 0)
                {
                    consecutiveLegacyTextToolCallRounds = 0;
                    _ql_zeroToolCallRounds++;
                    _ql_termination = QueryTerminationReason.NoToolCalls;
                    break; // No more tool calls, we are done
                }

                consecutiveLegacyTextToolCallRounds = 0;

                if (role == CodexAgentRole.Forge)
                {
                    bool guardrailTriggered = false;
                    var dangerousTools = new[] { "write_file", "ivilson_smart_patch", "delete_file", "ApplyPatchTool" };
                    foreach (var fc in toolCalls.Where(f => f != null && dangerousTools.Contains(f.Name)))
                    {
                        var args = fc.Arguments;
                        if (args == null) continue;

                        var targetPath = GetArgumentString(args, "path", "file_path", "target_file");

                        if (!string.IsNullOrEmpty(targetPath))
                        {
                            var graph = await _analysisService.BuildGraphAsync(session.WorkspacePath, ct).ConfigureAwait(false);
                            var currentTask = session.Plan?.FirstOrDefault(t => t != null && t.Id == session.ActiveTaskId);
                            var taskRisk = currentTask?.RiskLevel ?? "Medium";

                            var guardResult = await _analysisService.CheckGuardrailAsync(graph, targetPath, taskRisk).ConfigureAwait(false);
                            if (guardResult != null && guardResult.IsBlocked)
                            {
                                Log.GuardrailTriggered(_logger, targetPath, guardResult.Reason);
                                messages.Add(new ChatMessage(ChatRole.User, $"【熔斷預警】：你正在嘗試修改被系統鎖定的核心敏感文件：{targetPath}。原因：{guardResult.Reason}。除非你有充分的理由並能確保系統穩定性，否則請停止操作並說明原因。"));
                                guardrailTriggered = true;
                                break;
                            }
                        }
                    }
                    if (guardrailTriggered) continue;
                }

                // --- [CRITIQUE LOOP] --- (BYPASS BY BOSS ORDER)
                if (false && (role == CodexAgentRole.Forge || role == CodexAgentRole.Architect))
                {
                    // [PRE-FLATTEN] Before sending to critique, normalize the arguments to reduce false Rejects
                    var normalizedActionsList = new List<string>();
                    foreach (var fc in toolCalls.Where(f => f != null))
                    {
                        var args = ToolArgumentNormalizer.NormalizeCopy(fc.Arguments);
                        normalizedActionsList.Add($"工具: {fc.Name}, 参数: {JsonConvert.SerializeObject(args)}");
                    }

                    var proposedActions = string.Join("\n", normalizedActionsList);

                    // [FIX] 打印 proposed actions 详情，方便调试
                    Log.ProposedActions(_logger, roleName, guard, 10, proposedActions);

                    Log.InitiatingPeerReview(_logger);
                    var reviewResult = await _critiqueService.ReviewAsync(session, proposedActions, ct).ConfigureAwait(false);

                    if (reviewResult != null && !reviewResult.IsPassed)
                    {
                        if (critiqueRetryCount >= 3)
                        {
                            Log.CritiqueLoopExceededMaxRetries(_logger, proposedActions, reviewResult.Feedback);
                            return new CodexResponse($"任务执行失败：推理过程多次违反安全或逻辑约束，已被批判器物理熔断。\n最后一次反馈：{reviewResult.Feedback}", false);
                        }

                        Log.CritiqueFailed(_logger, critiqueRetryCount + 1, proposedActions, reviewResult.Feedback);
                        critiqueRetryCount++;

                        Log.InvokingOnEventForCritiqueFailure(_logger);
                        OnEvent?.Invoke(new CodexEvent
                        {
                            SessionId = session.Id ?? "unknown",
                            TaskId = session.ActiveTaskId,
                            Type = CodexEventType.CritiqueFeedback,
                            Message = $"Reasoning Critique rejected the proposed actions. ({critiqueRetryCount}/3)",
                            Payload = new { ProposedActions = proposedActions, Feedback = reviewResult.Feedback ?? "No feedback provided" }
                        });

                        Log.AddingCritiqueFeedbackToMessages(_logger);
                        // [FIX] 注入审计反馈，附带格式提示。改用 User 角色以兼容更多 API（如 DashScope/MiniMax）
                        var projectMode = session.ProjectUrl == null ? "新建项目 (Greenfield)" : "已有项目 (Brownfield)";
                        messages.Add(new ChatMessage(ChatRole.User, $@"【代码审查专家】驳回了你的提议。请根据以下反馈调整你的操作。

# 专家反馈
{reviewResult.Feedback ?? "无"}

# 修正指南
1. **参数格式**：严禁嵌套！请直接使用顶级 JSON 键值对，如 `{{ ""path"": ""."" }}`。
2. **场景匹配**：如果你处于 {projectMode} 模式，请遵循该模式的生存法则。
3. **不要重复**：同一工具不要在同一轮次中连续调用。"));

                        Log.ContinuingReasoningLoop(_logger);
                        continue; // skip execution, retry reasoning
                    }
                }

                // reset retry count on pass
                critiqueRetryCount = 0;

                // [Fix] Pre-filter: separate valid and empty-name tool calls
                var validToolCalls = toolCalls.Where(fc => fc != null && !string.IsNullOrWhiteSpace(fc.Name?.Trim())).ToList();
                var emptyNameCalls = toolCalls.Where(fc => fc != null && string.IsNullOrWhiteSpace(fc.Name?.Trim())).ToList();

                if (emptyNameCalls.Count > 0)
                {
                    var stats = RecordToolCallStats(emptyNameCalls.Count, emptyNameCalls.Count);
                    Log.EmptyToolNamesDetected(
                        _logger,
                        emptyNameCalls.Count,
                        toolCalls.Count,
                        string.Join(", ", emptyNameCalls.Select(fc => fc.CallId)));
                    Log.ToolCallQualityStatsWithEmptyNames(_logger, stats.Total, stats.Failed, stats.FailureRate);
                }

                // If ALL tool calls have empty names, this is a corrupted response — don't pollute message history
                if (validToolCalls.Count == 0 && emptyNameCalls.Count > 0)
                {
                    consecutiveEmptyToolNameRounds++;
                    if (consecutiveEmptyToolNameRounds >= 2)
                    {
                        var contentDiagnostics = new List<object>();
                        for (var i = 0; i < lastMessage.Contents.Count; i++)
                        {
                            var content = lastMessage.Contents[i];
                            if (content == null)
                            {
                                contentDiagnostics.Add(new
                                {
                                    Index = i,
                                    Type = "null"
                                });
                                continue;
                            }

                            if (content is TextContent tc)
                            {
                                contentDiagnostics.Add(new
                                {
                                    Index = i,
                                    Type = nameof(TextContent),
                                    TextLength = tc.Text?.Length ?? 0,
                                    TextPreview = TruncateForLog(tc.Text, 300)
                                });
                                continue;
                            }

                            if (content is FunctionCallContent fcDiag)
                            {
                                contentDiagnostics.Add(new
                                {
                                    Index = i,
                                    Type = nameof(FunctionCallContent),
                                    fcDiag.CallId,
                                    Name = fcDiag.Name,
                                    ArgumentKeys = fcDiag.Arguments?.Keys?.ToList() ?? new List<string>()
                                });
                                continue;
                            }

                            contentDiagnostics.Add(new
                            {
                                Index = i,
                                Type = content.GetType().FullName ?? content.GetType().Name
                            });
                        }

                        Log.EmptyToolNameAbortDiagnosticsSummary(
                            _logger,
                            roleName,
                            guard,
                            response.Messages.Count,
                            lastMessage.Role.ToString(),
                            JsonConvert.SerializeObject(contentDiagnostics));

                        Log.EmptyToolNameAbortDiagnosticsResponseText(
                            _logger,
                            responseTextRaw?.Length ?? 0,
                            string.IsNullOrWhiteSpace(responseTextRaw) ? "(empty)" : responseTextRaw);

                        Log.EmptyToolNameAbortDiagnosticsToolCalls(
                            _logger,
                            JsonConvert.SerializeObject(
                                toolCalls.Where(fc => fc != null).Select(fc => new
                                {
                                    fc.CallId,
                                    Name = fc.Name,
                                    ArgumentKeys = fc.Arguments?.Keys?.ToList() ?? new List<string>(),
                                    RawArguments = fc.Arguments
                                })));

                        Log.ConsecutiveEmptyToolNameRoundsReached(_logger, consecutiveEmptyToolNameRounds);
                        throw new InvalidOperationException($"模型连续 {consecutiveEmptyToolNameRounds} 次返回无效工具调用（空工具名），推理循环已中止。可能是模型服务不稳定或提示词过长导致响应截断。");
                    }

                    Log.AllToolCallsHadEmptyNames(_logger, emptyNameCalls.Count);

                    // Build available tool name list for the recovery prompt
                    var availableToolNames = (_toolRegistry.GetAvailableTools(session) ?? Enumerable.Empty<ICodexTool>())
                        .Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Take(10).ToList();
                    var toolHint = availableToolNames.Count > 0
                        ? $"可用工具: {string.Join(", ", availableToolNames)}"
                        : "";

                    messages.Add(new ChatMessage(ChatRole.User,
                        $"⚠️ [SYSTEM] 你上一轮返回了 {emptyNameCalls.Count} 个工具调用，但**所有工具名称均为空**。" +
                        "这通常是响应被截断或格式错误导致的。请重新思考并发起正确的工具调用，确保 function name 非空。\n" +
                        toolHint));
                    continue; // retry reasoning without adding corrupted messages
                }

                // Reset counter when we have valid tool calls
                if (validToolCalls.Count > 0)
                {
                    consecutiveEmptyToolNameRounds = 0;
                }

                // Process tool calls — add assistant message with ALL calls (including empty ones, for protocol compliance)
                var assistantMsg = new ChatMessage(ChatRole.Assistant, toolCalls.Where(fc => fc != null).Cast<AIContent>().ToList());
                messages.Add(assistantMsg);

                // Return error results for empty-name calls (protocol requires matching CallId responses)
                foreach (var emptyFc in emptyNameCalls)
                {
                    messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(emptyFc.CallId, "Error: Empty tool name. Skipped.")]));
                }

                // Execute valid tool calls
                foreach (var fc in validToolCalls)
                {
                    var rawToolName = fc.Name!.Trim();
                    var toolName = rawToolName;
                    var rawArguments = ToolCallSyntaxRecovery.CloneArguments(fc.Arguments);
                    if (ToolCallSyntaxRecovery.TryNormalizeInlineInvocation(rawToolName, fc.Arguments, out var recoveredToolName, out var recoveredArguments) &&
                        (!string.Equals(rawToolName, recoveredToolName, StringComparison.Ordinal) ||
                         recoveredArguments.Count != rawArguments.Count))
                    {
                        toolName = recoveredToolName;
                        rawArguments = recoveredArguments;
                        _logger.LogWarning("Recovered malformed inline tool call syntax. Raw={RawToolName} Normalized={ToolName}", rawToolName, toolName);
                    }

                    string toolResultOutput = string.Empty;
                    bool toolCallFailed = false;
                    bool reusedDedupCache = false;
                    long toolElapsedMs = 0;
                    string toolExecutionSource = "execute";
                    string? dedupSignature = null;
                    var canDedupRepeatedExecution = ShouldDeduplicateRepeatedToolExecution(toolName);

                    if (canDedupRepeatedExecution)
                    {
                        dedupSignature = BuildRepeatedToolCallSignature(toolName, rawArguments);
                        if (string.Equals(lastDedupToolCallSignature, dedupSignature, StringComparison.Ordinal))
                        {
                            consecutiveSameDedupToolCallCount++;
                        }
                        else
                        {
                            lastDedupToolCallSignature = dedupSignature;
                            lastDedupToolCallResult = null;
                            lastDedupToolCallFailed = false;
                            consecutiveSameDedupToolCallCount = 1;
                        }

                        if (consecutiveSameDedupToolCallCount >= duplicateToolExecutionCacheThreshold && lastDedupToolCallResult != null)
                        {
                            reusedDedupCache = true;
                            toolExecutionSource = "cache";
                            toolResultOutput = lastDedupToolCallResult;
                            toolCallFailed = lastDedupToolCallFailed;
                            Log.SkippingRepeatedToolExecutionViaCache(
                                _logger,
                                toolName,
                                consecutiveSameDedupToolCallCount,
                                duplicateToolExecutionCacheThreshold,
                                TruncateForLog(dedupSignature, 240));
                        }
                    }
                    else
                    {
                        lastDedupToolCallSignature = null;
                        lastDedupToolCallResult = null;
                        lastDedupToolCallFailed = false;
                        consecutiveSameDedupToolCallCount = 0;
                    }

                    if (!reusedDedupCache) try
                        {
                            var toolStopwatch = Stopwatch.StartNew();
                            LogAgentCallingToolIfEnabled(toolName, rawArguments);
                            var tools = _toolRegistry.GetAvailableTools(session);
                            var tool = (tools ?? Enumerable.Empty<ICodexTool>())
                                .FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));

                            if (tool != null)
                            {
                                var args = new Dictionary<string, object?>(rawArguments, StringComparer.OrdinalIgnoreCase);
                                NormalizeToolArgumentsInPlace(args);

                                // [FIX] 数组参数平铺与拆箱
                                var keys = args.Keys.ToList();
                                foreach (var key in keys)
                                {
                                    var val = args[key];
                                    if (val is JArray arr && arr.Count == 1) { args[key] = arr[0].ToString(); }
                                    else if (val is IList<object> list && list.Count == 1) { args[key] = list[0]; }
                                }

                                args["session_id"] = session.Id;
                                args["workspace_path"] = session.WorkspacePath;
                                // project_root is an internal trusted field and must not be overridden by model args.
                                args["project_root"] = ToolPathResolver.ResolveProjectRoot(session.WorkspacePath, null, session.ProjectUrl, session.Metadata);
                                CodexToolResult result;
                                if (IsBlockedByExecuteCodeTaskRetryLock(session, toolName))
                                {
                                    result = CodexToolResult.Error(BuildExecuteCodeTaskRetryLockMessage(session, toolName));
                                }
                                else
                                {
                                    result = await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
                                }
                                toolResultOutput = result.Output;
                                toolCallFailed = result.Status != ToolResultStatus.Success;

                                // [Fix] Smart Log Generation for better readability
                                string logMsg = $"Executed tool {toolName}";

                                if (IsWriteTool(toolName))
                                {
                                    var path = GetArgumentValue(args, "target_file", "path");
                                    logMsg = $"📝正在修改: {Path.GetFileName(path?.ToString())}";
                                }
                                else if (toolName.Contains("read_file", StringComparison.OrdinalIgnoreCase))
                                {
                                    var path = GetArgumentValue(args, "path");
                                    logMsg = $"📖 正在读取: {Path.GetFileName(path?.ToString())}";
                                }
                                else if (toolName.Contains("run_command", StringComparison.OrdinalIgnoreCase) || toolName.Contains("exec_cmd", StringComparison.OrdinalIgnoreCase))
                                {
                                    var cmd = GetArgumentString(args, "command") ?? string.Empty;
                                    if (cmd.Length > 20) cmd = string.Concat(cmd.AsSpan(0, 20), "...");
                                    logMsg = $"💻 执行指令: {cmd}";
                                }
                                else if (toolName.Contains("execute_code_task", StringComparison.OrdinalIgnoreCase))
                                {
                                    var tid = GetArgumentValue(args, "task_id");
                                    logMsg = $"🚀 启动子任务: {tid}";
                                }

                                OnEvent?.Invoke(new CodexEvent
                                {
                                    SessionId = session.Id ?? string.Empty,
                                    TaskId = session.ActiveTaskId,
                                    Type = CodexEventType.TaskProgress,
                                    Message = logMsg,
                                    Payload = new
                                    {
                                        Tool = fc.Name,
                                        Status = result.Status.ToString(),
                                        Metadata = result.Metadata
                                    }
                                });

                                // [Dynamic Index Sync] Real-time index update
                                if (result.Status == ToolResultStatus.Success && IsWriteTool(toolName))
                                {
                                    var pathObj = GetArgumentValue(args, "target_file", "path", "file_path");
                                    if (pathObj != null)
                                    {
                                        var filePath = pathObj.ToString();
                                        if (!string.IsNullOrEmpty(filePath))
                                        {
                                            // Resolve relative path
                                            if (!Path.IsPathRooted(filePath) && !string.IsNullOrEmpty(session.WorkspacePath))
                                            {
                                                filePath = Path.Combine(session.WorkspacePath, filePath);
                                            }
                                            await UpdateFileIndexAsync(session, filePath).ConfigureAwait(false);
                                        }
                                    }
                                }


                                // [Fix] Real-Time Code Preview
                                // Only update preview when a file is WRITTEN (not read)
                                // This prevents rapid Monaco switching and gives the editor time to render
                                var isFileModification = IsWriteTool(toolName);

                                if (result.Status == ToolResultStatus.Success && isFileModification)
                                {
                                    try
                                    {
                                        var pathObj = GetArgumentValue(args, "target_file", "path", "file_path");

                                        if (pathObj != null)
                                        {
                                            var filePath = pathObj.ToString() ?? string.Empty;
                                            if (string.IsNullOrEmpty(filePath)) continue;
                                            // If it's a relative path, combine with workspace
                                            if (!Path.IsPathRooted(filePath) && !string.IsNullOrEmpty(session.WorkspacePath))
                                            {
                                                filePath = Path.Combine(session.WorkspacePath!, filePath);
                                            }

                                            if (File.Exists(filePath))
                                            {
                                                var content = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
                                                var ext = Path.GetExtension(filePath).ToUpperInvariant();
                                                var lang = ext switch
                                                {
                                                    ".CS" => "csharp",
                                                    ".PY" => "python",
                                                    ".JS" => "javascript",
                                                    ".TS" => "typescript",
                                                    ".TSX" => "typescript",
                                                    ".JSX" => "javascript",
                                                    ".JAVA" => "java",
                                                    ".HTML" => "html",
                                                    ".CSS" => "css",
                                                    ".JSON" => "json",
                                                    ".MD" => "markdown",
                                                    ".SQL" => "sql",
                                                    ".XML" => "xml",
                                                    ".SH" => "shell",
                                                    ".YAML" => "yaml",
                                                    ".YML" => "yaml",
                                                    _ => "plaintext"
                                                };

                                                OnEvent?.Invoke(new CodexEvent
                                                {
                                                    SessionId = session.Id ?? string.Empty,
                                                    TaskId = session.ActiveTaskId,
                                                    Type = CodexEventType.CodePreview, // New event type
                                                    Message = $"File updated: {Path.GetFileName(filePath)}",
                                                    Payload = new
                                                    {
                                                        filePath = filePath,
                                                        code = content,
                                                        language = lang
                                                    }
                                                });
                                            }
                                        }
                                    }
                                    catch (IOException ex)
                                    {
                                        Log.FailedToEmitCodePreviewEvent(_logger, ex);
                                    }
                                    catch (InvalidOperationException ex)
                                    {
                                        Log.FailedToEmitCodePreviewEvent(_logger, ex);
                                    }
                                    catch (UnauthorizedAccessException ex)
                                    {
                                        Log.FailedToEmitCodePreviewEvent(_logger, ex);
                                    }
                                }
                            }
                            else
                            {
                                toolCallFailed = true;
                                toolResultOutput = $"Tool {toolName} not found.";
                            }
                            toolStopwatch.Stop();
                            toolElapsedMs = toolStopwatch.ElapsedMilliseconds;
                        }
                        catch (IOException ex)
                        {
                            toolElapsedMs = toolElapsedMs <= 0 ? 0 : toolElapsedMs;
                            toolCallFailed = true;
                            Log.ToolExecutionFailed(_logger, ex, toolName);
                            toolResultOutput = $"Error executing tool {toolName}: {ex.Message}";
                        }
                        catch (InvalidOperationException ex)
                        {
                            toolElapsedMs = toolElapsedMs <= 0 ? 0 : toolElapsedMs;
                            toolCallFailed = true;
                            Log.ToolExecutionFailed(_logger, ex, toolName);
                            toolResultOutput = $"Error executing tool {toolName}: {ex.Message}";
                        }
                        catch (HttpRequestException ex)
                        {
                            toolElapsedMs = toolElapsedMs <= 0 ? 0 : toolElapsedMs;
                            toolCallFailed = true;
                            Log.ToolExecutionFailed(_logger, ex, toolName);
                            toolResultOutput = $"Error executing tool {toolName}: {ex.Message}";
                        }
                        catch (TimeoutException ex)
                        {
                            toolElapsedMs = toolElapsedMs <= 0 ? 0 : toolElapsedMs;
                            toolCallFailed = true;
                            Log.ToolExecutionFailed(_logger, ex, toolName);
                            toolResultOutput = $"Error executing tool {toolName}: {ex.Message}";
                        }

                    if (canDedupRepeatedExecution && !string.IsNullOrEmpty(dedupSignature))
                    {
                        lastDedupToolCallSignature = dedupSignature;
                        lastDedupToolCallResult = toolResultOutput;
                        lastDedupToolCallFailed = toolCallFailed;
                    }

                    // Accumulate tool call statistics for Orchestrator-level detection
                    totalToolCalls++;
                    if (IsWriteTool(toolName))
                        writeToolCalls++;

                    var toolStats = RecordToolCallStats(1, toolCallFailed ? 1 : 0);
                    LogToolCallTraceIfEnabled(session, toolName, toolExecutionSource, toolElapsedMs, toolCallFailed, rawArguments, toolResultOutput);
                    LogToolExecutionCompletedIfEnabled(toolName, toolExecutionSource, toolElapsedMs, toolCallFailed, toolResultOutput);
                    Log.ToolCallQualityStats(
                        _logger,
                        toolStats.Total,
                        toolStats.Failed,
                        toolStats.FailureRate,
                        toolName,
                        toolCallFailed ? "failed" : "success");

                    messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(fc.CallId, toolResultOutput)]));
                }

                // [Phase 0] Emit per-round telemetry after all tool calls executed
                _ql_totalPromptTokens += _ql_roundPromptTokens ?? 0;
                _ql_totalCompletionTokens += _ql_roundCompletionTokens ?? 0;
                var _ql_roundText = sb.Length - _ql_roundTextStart;
                _queryLoopTelemetry?.RecordRound(new QueryLoopRoundCompleted(
                    _ql_queryId, session.Id ?? "unknown", QueryLoopEntryPoint.DefaultCodexKernel,
                    guard, validToolCalls.Count,
                    _ql_roundText > 0, _ql_roundText, 0,
                    messages.Sum(m => m.Text?.Length ?? 0),
                    _ql_stopwatch.ElapsedMilliseconds - _ql_roundStartMs,
                    null,
                    _ql_roundPromptTokens, _ql_roundCompletionTokens));
            }

            // Log warning for Forge with zero tool calls (but keep IsComplete=true to avoid hard-gate termination)
            if (role == CodexAgentRole.Forge && enableTools && totalToolCalls == 0)
            {
                _logger.LogWarning(
                    "Forge returned zero tool calls. session={SessionId} task={TaskId} guard={Guard}",
                    session.Id, session.ActiveTaskId, guard);
            }

            if (_ql_termination == QueryTerminationReason.Normal && guard > MaxInternalRounds)
                _ql_termination = QueryTerminationReason.MaxRoundsReached;

            _queryLoopTelemetry?.RecordTermination(new QueryLoopTerminated(
                _ql_queryId, session.Id ?? "unknown", QueryLoopEntryPoint.DefaultCodexKernel,
                _ql_termination, QueryTerminalDetailCodes.Resolve(_ql_termination), guard - 1, totalToolCalls,
                _ql_zeroToolCallRounds, malformedToolProtocolFailureCount,
                _ql_emptyResponseCount, _ql_recoveryCount,
                _ql_stopwatch.ElapsedMilliseconds,
                _ql_totalPromptTokens > 0 ? _ql_totalPromptTokens : null,
                _ql_totalCompletionTokens > 0 ? _ql_totalCompletionTokens : null));

            var thinkingContent = thinkingSb.Length > 0 ? thinkingSb.ToString() : null;
            return new CodexResponse(sb.ToString(), true, totalToolCalls, writeToolCalls, thinkingContent);
        }
        catch (HttpRequestException ex)
        {
            Log.CriticalRunLoopError(_logger, ex, ex.Message);
            return new CodexResponse($"系統內核異常: {ex.Message}\n堆棧軌跡: {ex.StackTrace}", false);
        }
        catch (IOException ex)
        {
            Log.CriticalRunLoopError(_logger, ex, ex.Message);
            return new CodexResponse($"系統內核異常: {ex.Message}\n堆棧軌跡: {ex.StackTrace}", false);
        }
        catch (InvalidOperationException ex)
        {
            Log.CriticalRunLoopError(_logger, ex, ex.Message);
            return new CodexResponse($"系統內核異常: {ex.Message}\n堆棧軌跡: {ex.StackTrace}", false);
        }
        catch (JsonException ex)
        {
            Log.CriticalRunLoopError(_logger, ex, ex.Message);
            return new CodexResponse($"系統內核異常: {ex.Message}\n堆棧軌跡: {ex.StackTrace}", false);
        }
    }

    public async Task<CodexResponse> RunLoopStreamingAsync(CodexSession session, string userPrompt, CodexAgentRole role = CodexAgentRole.Forge, CancellationToken ct = default, bool enableTools = true, TaskFileScopeDescriptor? taskFileScope = null)
    {
        // RunLoopAsync now uses streaming internally with thinking chain logging.
        return await RunLoopAsync(session, userPrompt, role, ct, enableTools, taskFileScope);
    }




    private async Task<(ChatResponse Response, string ThinkingContent)> StreamResponseAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, CodexSession session, CancellationToken ct)
    {
        var textSb = new StringBuilder();
        var thinkingSb = new StringBuilder();
        var allContents = new List<AIContent>();
        UsageDetails? lastUsage = null;

        if (_llmExecutor != null)
        {
            await foreach (var update in _llmExecutor.StreamAsync(
                new LLMExecutionRequest(messages, options, MemoryInjectionScenario.Execution, session, nameof(DefaultCodexKernel)), ct).ConfigureAwait(false))
            {
                if (update is UsageChatResponseUpdate uu)
                {
                    lastUsage = uu.Usage;
                    continue;
                }
                CollectStreamingContent(update, textSb, thinkingSb, allContents);
            }
        }
        else
        {
            var stream = _chatClient.GetStreamingResponseAsync(messages, options, ct);
            if (stream == null)
            {
                var response = await _chatClient.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
                return (response, string.Empty);
            }

            await foreach (var update in stream.ConfigureAwait(false))
            {
                if (update is UsageChatResponseUpdate uu)
                {
                    lastUsage = uu.Usage;
                    continue;
                }
                CollectStreamingContent(update, textSb, thinkingSb, allContents);
            }
        }

        var responseMsg = new ChatMessage(ChatRole.Assistant, allContents);
        var chatResponse = new ChatResponse([responseMsg]);

        // Prefer UsageChatResponseUpdate usage; fall back to UsageContent from stream.
        if (lastUsage == null)
        {
            var uc = allContents.OfType<UsageContent>().LastOrDefault();
            if (uc != null && uc.Details != null)
            {
                lastUsage = new UsageDetails
                {
                    InputTokenCount = uc.Details.InputTokenCount,
                    OutputTokenCount = uc.Details.OutputTokenCount,
                    TotalTokenCount = uc.Details.TotalTokenCount,
                };
            }
        }
        if (lastUsage != null)
        {
            chatResponse.Usage = lastUsage;
        }
        return (chatResponse, thinkingSb.ToString());
    }

    private static void CollectStreamingContent(ChatResponseUpdate update, StringBuilder textSb, StringBuilder thinkingSb, List<AIContent> allContents)
    {
        // Primary: ReasoningChatResponseUpdate.Thinking flag from the update itself
        var isThinking = update is ReasoningChatResponseUpdate ru && ru.Thinking;

        foreach (var part in update.Contents ?? Array.Empty<AIContent>())
        {
            if (part is TextContent tc && !string.IsNullOrEmpty(tc.Text))
            {
                if (isThinking)
                {
                    // [Fix] Thinking text must NOT enter assistant message contents.
                    // Otherwise it leaks into CodexResponse.Text, session history,
                    // and legacy tool-call parser.
                    thinkingSb.Append(tc.Text);
                }
                else
                {
                    textSb.Append(tc.Text);
                    allContents.Add(part);
                }
            }
            else if (part is UsageContent uc)
            {
                // Some providers send usage as a Content item rather than a standalone
                // UsageChatResponseUpdate. Capture it so token stats aren't silently dropped.
                if (uc.Details != null)
                {
                    allContents.Add(part);
                }
            }
            else
            {
                // FunctionCallContent etc — always pass through
                allContents.Add(part);
            }
        }

        // Fallback: some providers send text via update.Text instead of Contents.
        // This includes thinking text from ReasoningChatResponseUpdate on providers
        // that set update.Text directly while also marking Thinking=true on the update.
        if (string.IsNullOrEmpty(update.Text) == false && (update.Contents == null || update.Contents.All(c => c is not TextContent)))
        {
            if (isThinking)
            {
                thinkingSb.Append(update.Text);
            }
            else
            {
                textSb.Append(update.Text);
                allContents.Add(new TextContent(update.Text));
            }
        }
    }




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

    private static bool IsMalformedToolCallProtocolFailure(Exception ex)
    {
        if (ex is not InvalidOperationException)
        {
            return false;
        }

        var merged = $"{ex.Message} {ex.InnerException?.Message}".Trim();
        return merged.Contains("empty function.name", StringComparison.OrdinalIgnoreCase)
               || merged.Contains("function name is empty", StringComparison.OrdinalIgnoreCase)
               || merged.Contains("function arguments is not valid JSON", StringComparison.OrdinalIgnoreCase)
               || merged.Contains("function arguments JSON root must be object", StringComparison.OrdinalIgnoreCase)
               || merged.Contains("tool_calls with empty function.name", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldDeduplicateRepeatedToolExecution(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        return toolName.Equals("search_file_index", StringComparison.OrdinalIgnoreCase)
               || toolName.Equals("search_in_files", StringComparison.OrdinalIgnoreCase)
               || toolName.Equals("ivilson_read", StringComparison.OrdinalIgnoreCase)
               || toolName.Equals("ivilson_ls", StringComparison.OrdinalIgnoreCase)
               || toolName.Equals("list_workspace", StringComparison.OrdinalIgnoreCase)
               || toolName.Equals("fetch_webpage", StringComparison.OrdinalIgnoreCase)
               || toolName.Equals("analyze_code", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRepeatedToolCallSignature(string toolName, IDictionary<string, object?>? rawArgs)
    {
        var args = rawArgs != null
            ? new Dictionary<string, object?>(rawArgs, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        NormalizeToolArgumentsInPlace(args);

        // Session/workspace injected by kernel should not participate in dedupe signature.
        args.Remove("session_id");
        args.Remove("workspace_path");
        args.Remove("project_root");

        var sorted = new SortedDictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase);
        return $"{toolName.Trim().ToUpperInvariant()}::{JsonConvert.SerializeObject(sorted)}";
    }

    private static (long Total, long Failed, double FailureRate) RecordToolCallStats(long totalDelta, long failedDelta)
    {
        var total = Interlocked.Add(ref _toolCallTotalSinceProcessStart, totalDelta);
        long failed = failedDelta > 0
            ? Interlocked.Add(ref _toolCallFailedSinceProcessStart, failedDelta)
            : Interlocked.Read(ref _toolCallFailedSinceProcessStart);
        var failureRate = total > 0 ? (double)failed / total : 0d;
        return (total, failed, failureRate);
    }

    private static string BuildKernelContextDiagnosticsJson(
        List<ChatMessage> messages,
        string roleName,
        int round,
        int availableToolCount)
    {
        int totalTextChars = 0;
        int totalFunctionCalls = 0;
        int totalFunctionResults = 0;

        var roleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var messageSummaries = new List<object>();

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            var msgRole = msg.Role.ToString();
            roleCounts[msgRole] = roleCounts.TryGetValue(msgRole, out var c) ? c + 1 : 1;

            int textChars = 0;
            int functionCalls = 0;
            int functionResults = 0;
            int contentCount = 0;

            foreach (var content in msg.Contents ?? [])
            {
                if (content == null) continue;
                contentCount++;

                if (content is TextContent tc)
                {
                    var len = tc.Text?.Length ?? 0;
                    textChars += len;
                    totalTextChars += len;
                    continue;
                }

                if (content is FunctionCallContent fc)
                {
                    functionCalls++;
                    totalFunctionCalls++;
                    var argsLen = JsonConvert.SerializeObject(fc.Arguments)?.Length ?? 0;
                    textChars += (fc.Name?.Length ?? 0) + argsLen;
                    continue;
                }

                if (content is FunctionResultContent fr)
                {
                    functionResults++;
                    totalFunctionResults++;
                    textChars += fr.Result?.ToString()?.Length ?? 0;
                    continue;
                }

                textChars += content.ToString()?.Length ?? 0;
            }

            messageSummaries.Add(new
            {
                Index = i,
                Role = msgRole,
                ContentCount = contentCount,
                TextChars = textChars,
                FunctionCalls = functionCalls,
                FunctionResults = functionResults
            });
        }

        var diagnostics = new
        {
            Role = roleName,
            Round = round,
            MessageCount = messages.Count,
            AvailableToolCount = availableToolCount,
            ApproxContextChars = totalTextChars,
            TotalFunctionCalls = totalFunctionCalls,
            TotalFunctionResults = totalFunctionResults,
            RoleCounts = roleCounts,
            LastMessages = messageSummaries.TakeLast(6).ToList()
        };

        return JsonConvert.SerializeObject(diagnostics);
    }

    private async Task<string> ExecuteLegacyTextToolCallAsync(
        CodexSession session,
        VllmFunctionToolCall call,
        CancellationToken ct)
    {
        var toolName = call.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(toolName))
        {
            var stats = RecordToolCallStats(1, 1);
            Log.LegacyEmptyToolName(_logger, stats.Total, stats.Failed, stats.FailureRate);
            return "Error: Empty tool name (legacy <tool_call> format).";
        }

        LogAgentCallingToolLegacyIfEnabled(toolName, call.Arguments);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var tools = _toolRegistry.GetAvailableTools(session);
            var tool = (tools ?? Enumerable.Empty<ICodexTool>())
                .FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));

            if (tool == null)
            {
                stopwatch.Stop();
                var stats = RecordToolCallStats(1, 1);
                Log.LegacyToolNotFound(_logger, toolName, stats.Total, stats.Failed, stats.FailureRate);
                LogToolCallTraceIfEnabled(session, toolName, "legacy", stopwatch.ElapsedMilliseconds, true, call.Arguments, $"Tool {toolName} not found.");
                LogLegacyToolExecutionCompletedIfEnabled(toolName, stopwatch.ElapsedMilliseconds, true, $"Tool {toolName} not found.");
                return $"Tool {toolName} not found.";
            }

            var args = call.Arguments != null
                ? new Dictionary<string, object?>(call.Arguments)
                : new Dictionary<string, object?>();

            NormalizeToolArgumentsInPlace(args);

            args["session_id"] = session.Id;
            args["workspace_path"] = session.WorkspacePath;
            // project_root is an internal trusted field and must not be overridden by model args.
            args["project_root"] = ToolPathResolver.ResolveProjectRoot(session.WorkspacePath, null, session.ProjectUrl, session.Metadata);

            var result = await tool.ExecuteAsync(args, ct).ConfigureAwait(false);
            stopwatch.Stop();
            var resultFailed = result.Status != ToolResultStatus.Success;
            var successStats = RecordToolCallStats(1, resultFailed ? 1 : 0);
            LogToolCallTraceIfEnabled(session, toolName, "legacy", stopwatch.ElapsedMilliseconds, resultFailed, call.Arguments, result.Output);
            LogLegacyToolExecutionCompletedIfEnabled(toolName, stopwatch.ElapsedMilliseconds, resultFailed, result.Output);
            Log.LegacyToolCallQualityStats(
                _logger,
                successStats.Total,
                successStats.Failed,
                successStats.FailureRate,
                toolName,
                resultFailed ? "failed" : "success");
            return result.Output;
        }
        catch (IOException ex)
        {
            stopwatch.Stop();
            var stats = RecordToolCallStats(1, 1);
            Log.LegacyToolExecutionFailed(_logger, ex, toolName);
            LogToolCallTraceIfEnabled(session, toolName, "legacy", stopwatch.ElapsedMilliseconds, true, call.Arguments, $"Error executing tool {toolName}: {ex.Message}");
            LogLegacyToolExecutionCompletedIfEnabled(toolName, stopwatch.ElapsedMilliseconds, true, $"Error executing tool {toolName}: {ex.Message}");
            Log.LegacyToolFailureStats(_logger, stats.Total, stats.Failed, stats.FailureRate);
            return $"Error executing tool {toolName}: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            stopwatch.Stop();
            var stats = RecordToolCallStats(1, 1);
            Log.LegacyToolExecutionFailed(_logger, ex, toolName);
            LogToolCallTraceIfEnabled(session, toolName, "legacy", stopwatch.ElapsedMilliseconds, true, call.Arguments, $"Error executing tool {toolName}: {ex.Message}");
            LogLegacyToolExecutionCompletedIfEnabled(toolName, stopwatch.ElapsedMilliseconds, true, $"Error executing tool {toolName}: {ex.Message}");
            Log.LegacyToolFailureStats(_logger, stats.Total, stats.Failed, stats.FailureRate);
            return $"Error executing tool {toolName}: {ex.Message}";
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            var stats = RecordToolCallStats(1, 1);
            Log.LegacyToolExecutionFailed(_logger, ex, toolName);
            LogToolCallTraceIfEnabled(session, toolName, "legacy", stopwatch.ElapsedMilliseconds, true, call.Arguments, $"Error executing tool {toolName}: {ex.Message}");
            LogLegacyToolExecutionCompletedIfEnabled(toolName, stopwatch.ElapsedMilliseconds, true, $"Error executing tool {toolName}: {ex.Message}");
            Log.LegacyToolFailureStats(_logger, stats.Total, stats.Failed, stats.FailureRate);
            return $"Error executing tool {toolName}: {ex.Message}";
        }
    }

    private static void NormalizeToolArgumentsInPlace(Dictionary<string, object?> args)
    {
        ToolArgumentNormalizer.NormalizeInPlace(args);
    }

    private static object? GetArgumentValue(IDictionary<string, object?> args, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (args.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? GetArgumentString(IDictionary<string, object?> args, params string[] keys) =>
        GetArgumentValue(args, keys)?.ToString();

    private static bool IsWriteTool(string toolName) => ToolClassification.IsWriteTool(toolName);

    private static bool IsBlockedByExecuteCodeTaskRetryLock(CodexSession session, string toolName)
    {
        if (!session.Metadata.TryGetValue(ExecutionGuardMetadataKeys.ExecuteCodeTaskRetryRequired, out var retryRequiredRaw) ||
            !bool.TryParse(retryRequiredRaw, out var retryRequired) ||
            !retryRequired)
        {
            return false;
        }

        if (toolName.Contains("execute_code_task", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Bug-02: exec_code is read-only diagnostic tool; allow it during failure guard mode
        // so the model can inspect build output / project files before retrying.
        return IsWriteTool(toolName);
    }

    private static string BuildExecuteCodeTaskRetryLockMessage(CodexSession session, string toolName)
    {
        session.Metadata.TryGetValue(ExecutionGuardMetadataKeys.ExecuteCodeTaskLastFailure, out var lastFailure);
        var suffix = string.IsNullOrWhiteSpace(lastFailure)
            ? string.Empty
            : "\n\n最近一次失败摘要：\n" + lastFailure.Trim();
        return "⚠️ 当前会话上一轮 execute_code_task 执行失败。" +
            "此时允许：使用 exec_code / ivilson_read / ivilson_ls / run_command 诊断失败原因；" +
            "或在调整任务描述后重新调用 execute_code_task 重试。" +
            "禁止：使用 write_file / ivilson_smart_patch / ivilson_write 等写入类工具绕过编排流水线。"
            + $"\n当前被拦截工具: {toolName}。"
            + suffix;
    }

    private void LogAgentCallingToolIfEnabled(string toolName, object? arguments)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var serializedArguments = JsonConvert.SerializeObject(arguments);
        Log.AgentCallingTool(_logger, toolName, serializedArguments);
    }

    private void LogAgentCallingToolLegacyIfEnabled(string toolName, object? arguments)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var serializedArguments = JsonConvert.SerializeObject(arguments);
        Log.AgentCallingToolLegacy(_logger, toolName, serializedArguments);
    }

    private void LogToolCallTraceIfEnabled(CodexSession session, string toolName, string source, long elapsedMs, bool failed, object? arguments, string output)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var serializedArguments = JsonConvert.SerializeObject(arguments);
        var sessionId = session.Id ?? string.Empty;
        var taskId = session.ActiveTaskId ?? string.Empty;
        Log.ToolCallTrace(_logger, sessionId, taskId, toolName, source, elapsedMs, failed, serializedArguments, output);
    }

    private void LogToolExecutionCompletedIfEnabled(string toolName, string source, long elapsedMs, bool failed, string output)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var outputPreview = TruncateForLog(output, 500);
        Log.ToolExecutionCompleted(_logger, toolName, source, elapsedMs, failed, outputPreview);
    }

    private void LogLegacyToolExecutionCompletedIfEnabled(string toolName, long elapsedMs, bool failed, string output)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var outputPreview = TruncateForLog(output, 500);
        Log.LegacyToolExecutionCompleted(_logger, toolName, elapsedMs, failed, outputPreview);
    }

    private static bool ContainsLegacyTextToolCallMarkup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("<toolcall", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<tool_call", StringComparison.OrdinalIgnoreCase)
               || text.Contains("</tool_call>", StringComparison.OrdinalIgnoreCase)
               || text.Contains("</toolcall>", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<invoke", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<minimax:tool_call", StringComparison.OrdinalIgnoreCase)
               || text.Contains("</minimax:tool_call>", StringComparison.OrdinalIgnoreCase);
    }

    private static List<VllmFunctionToolCall> ParseToolCalls(string input, out string remainder)
    {
        var list = new List<VllmFunctionToolCall>();
        remainder = input ?? string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return list;
        }

        var matches = Regex.Matches(
            input,
            @"<tool_call>([\s\S]*?)</tool_call>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match m in matches)
        {
            if (!m.Success)
            {
                continue;
            }

            var json = m.Groups[1].Value;
            var call = TryParseToolCallJson(json);
            if (call != null)
            {
                list.Add(call);
            }
        }

        remainder = Regex.Replace(
            input,
            @"<tool_call>[\s\S]*?</tool_call>",
            string.Empty,
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        return list;
    }

    private static VllmFunctionToolCall? TryParseToolCallJson(string? json)
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

            // Common formats:
            // 1) { "name": "...", "arguments": { ... } }
            // 2) { "id": "...", "type": "function", "function": { "name": "...", "arguments": "..." } }
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

            var args = ParseArgumentsToken(argumentsToken);
            return new VllmFunctionToolCall(name.Trim(), args, callId);
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

    private static Dictionary<string, object?> ParseArgumentsToken(JToken? argumentsToken)
    {
        if (argumentsToken == null || argumentsToken.Type == JTokenType.Null)
        {
            return new Dictionary<string, object?>();
        }

        if (argumentsToken is JObject argsObj)
        {
            return argsObj.ToObject<Dictionary<string, object?>>() ?? new Dictionary<string, object?>();
        }

        if (argumentsToken.Type == JTokenType.String)
        {
            var s = argumentsToken.ToString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                try
                {
                    var parsed = JToken.Parse(s);
                    if (parsed is JObject parsedObj)
                    {
                        return parsedObj.ToObject<Dictionary<string, object?>>() ?? new Dictionary<string, object?>();
                    }
                }
                catch (JsonReaderException)
                {
                    // Keep raw string as a fallback payload.
                }
                catch (JsonSerializationException)
                {
                    // Keep raw string as a fallback payload.
                }
                catch (InvalidOperationException)
                {
                    // Keep raw string as a fallback payload.
                }

                return new Dictionary<string, object?> { ["input"] = s };
            }
        }

        return new Dictionary<string, object?>();
    }

    private static List<string> ExtractLegacyToolCallNames(string? text)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return names;
        }

        try
        {
            foreach (Match m in Regex.Matches(text, "<invoke\\s+name\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
            {
                var v = m.Groups[1].Value?.Trim();
                if (!string.IsNullOrWhiteSpace(v))
                {
                    names.Add(v);
                }
            }

            foreach (Match m in Regex.Matches(text, "\"name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
            {
                var v = m.Groups[1].Value?.Trim();
                if (!string.IsNullOrWhiteSpace(v))
                {
                    names.Add(v);
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Best-effort diagnostics only.
        }

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static string TruncateForLog(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars] + "...";
    }

    private static string PrepareToolResultForModelContext(string toolName, string? output, bool failed)
    {
        if (string.IsNullOrEmpty(output))
        {
            return string.Empty;
        }

        // Keep error details relatively complete, but still cap extreme outputs.
        if (failed)
        {
            return SummarizeTextForModel(output, 4000, 120, 80, $"tool={toolName}|failed=true");
        }

        var normalizedTool = (toolName ?? string.Empty).Trim();

        if (normalizedTool.Contains("read_file", StringComparison.OrdinalIgnoreCase) || normalizedTool.Equals("ivilson_read", StringComparison.OrdinalIgnoreCase))
        {
            return SummarizeTextForModel(output, 3500, 80, 40, $"tool={toolName}|kind=file-read");
        }

        if (normalizedTool.Contains("list_workspace", StringComparison.OrdinalIgnoreCase)
            || normalizedTool.Equals("ivilson_ls", StringComparison.OrdinalIgnoreCase)
            || normalizedTool.StartsWith("search_", StringComparison.OrdinalIgnoreCase))
        {
            return SummarizeTextForModel(output, 2500, 60, 30, $"tool={toolName}|kind=search-list");
        }

        if (normalizedTool.Contains("run_command", StringComparison.OrdinalIgnoreCase) || normalizedTool.Contains("exec_cmd", StringComparison.OrdinalIgnoreCase) || normalizedTool.Contains("run_tests", StringComparison.OrdinalIgnoreCase))
        {
            return SummarizeTextForModel(output, 3200, 80, 60, $"tool={toolName}|kind=command");
        }

        if (normalizedTool.Contains("fetch_webpage", StringComparison.OrdinalIgnoreCase) || normalizedTool.Contains("analyze_code", StringComparison.OrdinalIgnoreCase))
        {
            return SummarizeTextForModel(output, 2800, 60, 40, $"tool={toolName}|kind=analysis");
        }

        return SummarizeTextForModel(output, 3000, 70, 40, $"tool={toolName}|kind=generic");
    }

    private static string SummarizeTextForModel(
        string text,
        int maxChars,
        int maxHeadLines,
        int maxTailLines,
        string label)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var lineCount = lines.Length;

        // Line-preserving summary for command/search/file outputs.
        var head = lines.Take(Math.Min(maxHeadLines, lineCount)).ToList();
        var tail = lines.Skip(Math.Max(0, lineCount - maxTailLines)).ToList();

        var headBlock = string.Join("\n", head);
        var tailBlock = string.Join("\n", tail);

        var omittedLineCount = Math.Max(0, lineCount - head.Count - tail.Count);
        var omittedCharCount = Math.Max(0, text.Length - headBlock.Length - tailBlock.Length);
        var summary = new StringBuilder();
        summary.AppendLine(FormattableString.Invariant($"[TOOL_RESULT_SUMMARY] {label}; totalChars={text.Length}; totalLines={lineCount}"));
        summary.AppendLine("[HEAD]");
        summary.AppendLine(headBlock);
        if (omittedLineCount > 0 || omittedCharCount > 0)
        {
            summary.AppendLine(FormattableString.Invariant($"[... omitted lines={omittedLineCount}, omittedChars~={omittedCharCount} ...]"));
        }

        if (tail.Count > 0 && (tailBlock.Length > 0) && (omittedLineCount > 0 || lineCount > head.Count))
        {
            summary.AppendLine("[TAIL]");
            summary.AppendLine(tailBlock);
        }

        var result = summary.ToString().TrimEnd();
        if (result.Length <= maxChars + 400)
        {
            return result;
        }

        // Fallback when line-based summary is still too large (e.g., very long lines)
        var headChars = Math.Min(maxChars / 2, text.Length);
        var tailChars = Math.Min(maxChars / 3, Math.Max(0, text.Length - headChars));
        var headText = text[..headChars];
        var tailText = tailChars > 0 ? text[^tailChars..] : string.Empty;
        return $"[TOOL_RESULT_SUMMARY] {label}; totalChars={text.Length}\n[HEAD_CHARS]\n{headText}\n[... omitted ...]\n[TAIL_CHARS]\n{tailText}";
    }

    private sealed record VllmFunctionToolCall(string Name, Dictionary<string, object?> Arguments, string? CallId);

    private async Task UpdateFileIndexAsync(CodexSession session, string filePath)
    {
        try
        {
            var indexFact = session.ActiveFacts.FirstOrDefault(f => f.Key == "ProjectFileIndex");
            List<FileIndexEntry> index;

            if (indexFact != null && !string.IsNullOrEmpty(indexFact.Value))
            {
                index = JsonConvert.DeserializeObject<List<FileIndexEntry>>(indexFact.Value) ?? new();
            }
            else
            {
                index = new();
            }

            var relativePath = Path.GetRelativePath(session.WorkspacePath, filePath).Replace("\\", "/", StringComparison.Ordinal);

            // Check if exists
            var existing = index.FirstOrDefault(e => e.Path.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                var entry = new FileIndexEntry
                {
                    Path = relativePath,
                    Type = Path.GetExtension(relativePath).ToUpperInvariant() switch
                    {
                        ".CS" => "C# Source",
                        ".TS" => "TypeScript",
                        ".JS" => "JavaScript",
                        ".CSS" => "CSS",
                        ".HTML" => "HTML",
                        ".JSON" => "JSON",
                        _ => "File"
                    },
                    Size = 0 // Approximate
                };
                index.Add(entry);

                // Save back
                var json = JsonConvert.SerializeObject(index);
                var dynamicIndexMeta = new MemoryEntryMetadata(
                    Scope: MemoryFactScope.Session,
                    Source: "kernel_dynamic_index_sync",
                    Confidence: MemoryFactConfidence.High).ToJson();
                await _sessionManager.LearnFactAsync(session.Id, ProjectMemoryFactKeys.ProjectFileIndex, json, MemoryFactCategories.Project, dynamicIndexMeta).ConfigureAwait(false);
                Log.DynamicIndexSyncAdded(_logger, relativePath);
            }
        }
        catch (JsonReaderException ex)
        {
            Log.FailedToUpdateDynamicIndex(_logger, ex.Message);
        }
        catch (JsonSerializationException ex)
        {
            Log.FailedToUpdateDynamicIndex(_logger, ex.Message);
        }
        catch (IOException ex)
        {
            Log.FailedToUpdateDynamicIndex(_logger, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            Log.FailedToUpdateDynamicIndex(_logger, ex.Message);
        }
    }

    #region Runtime Integration (Phase 4A)

    /// <summary>
    /// Runtime 版本的 RunLoop — 使用 IQueryRuntimeEngine
    /// Phase 4A: 渐进式迁移，保留 Kernel 特有逻辑
    /// </summary>
    /// <remarks>
    /// Kernel 保留的职责：
    /// - role prompt 组装
    /// - tool access policy (按 role 过滤工具)
    /// - critique loop (在工具执行后进行审查)
    /// - guardrail
    ///
    /// Runtime 接管的职责：
    /// - round loop
    /// - tool execution
    /// - tool result append
    /// - termination 判断
    /// </remarks>
    private async Task<CodexResponse> RunLoopWithRuntimeAsync(
        CodexSession session,
        string userPrompt,
        CodexAgentRole role,
        bool enableTools,
        TaskFileScopeDescriptor? taskFileScope,
        CancellationToken ct)
    {
        if (_queryRuntimeEngine == null)
        {
            _logger.LogWarning("IQueryRuntimeEngine not available, falling back to manual implementation");
            return new CodexResponse("Runtime engine not available", false);
        }

        // 1. Role prompt 组装（Kernel 特有职责）
        var workerType = TryMapRoleToWorkerType(role);
        var workerDefinition = ResolveWorkerDefinition(workerType);
        ActivateWorkerDeferredTools(workerDefinition);
        var runtimeSession = workerType.HasValue
            ? WorkerExecutionSessionFactory.CreateForWorker(session, workerType.Value)
            : session;
        var rolePrompt = workerDefinition?.BuildSystemPrompt(runtimeSession)
            ?? _roleRegistry.GetSystemPrompt(role, session)
            ?? "你是一个 AI 助手。";
        var roleName = workerDefinition?.DisplayName
            ?? _roleRegistry.GetRoleName(role)
            ?? "Ivilson-Agent";
        var projectSummary = session.ProjectSummary ?? "（空项目摘要）";

        // 2. Tool access policy（Kernel 特有职责）
        IReadOnlyList<ICodexTool> BuildAvailableCodexTools()
        {
            if (!enableTools)
            {
                return [];
            }

            var currentBaseTools = _toolRegistry.GetAvailableTools(runtimeSession) ?? Enumerable.Empty<ICodexTool>();
            if (workerDefinition != null && workerType.HasValue)
            {
                currentBaseTools = _workerDefinitionRegistry!.FilterAvailableTools(workerType.Value, currentBaseTools);
                if (workerType.Value == WorkerType.Verify)
                {
                    currentBaseTools = currentBaseTools.Select(ApplyVerifyWorkerToolPolicy);
                }
            }
            else if (role == CodexAgentRole.Coordinator)
            {
                currentBaseTools = CoordinatorToolSurfacePolicy.Filter(currentBaseTools);
            }
            else if (role == CodexAgentRole.Security)
            {
                currentBaseTools = currentBaseTools.Where(t => t.Category == ToolCategory.Read || t.Category == ToolCategory.Analysis);
            }
            else if (role == CodexAgentRole.Forge)
            {
                currentBaseTools = currentBaseTools.Where(t =>
                    !string.Equals(t.Name, "execute_code_task", StringComparison.OrdinalIgnoreCase) &&
                    !PlanningToolNames.IsPlanCreationTool(t.Name));
            }

            return currentBaseTools.ToList();
        }

        IReadOnlyList<AIFunction> BuildAvailableTools()
            => BuildAvailableCodexTools()
                .Select(CodexToolFunctionAdapterFactory.CreateAIFunction)
                .Cast<AIFunction>()
                .ToList();

        var availableCodexTools = BuildAvailableCodexTools();
        var availableTools = availableCodexTools
            .Select(CodexToolFunctionAdapterFactory.CreateAIFunction)
            .Cast<AIFunction>()
            .ToList();
        var workerContext = workerType.HasValue && workerDefinition != null
            ? _workerDefinitionRegistry!.BuildRuntimeContext(workerType.Value)
            : null;

        if (enableTools)
        {
            rolePrompt = ToolCatalogPromptComposer.AppendRuntimeToolGuidance(
                rolePrompt,
                availableCodexTools,
                workerContext);
        }

        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, $"{rolePrompt}\n\n# 当前项目上下文\n{projectSummary}"),
            new ChatMessage(ChatRole.User, userPrompt ?? "开始执行。")
        };

        // 3. 构建 AdapterHints - Kernel 使用最完整配置
        var hints = new AdapterHints(
            EnableAutoDispatch: false,
            EnableStallDetection: false,
            EnableContextLimitCheck: false,
            EnableEmptyResponseRecovery: true,
            EnableMalformedProtocolRecovery: true,
            EnableTransportFailureRecovery: true,
            EnableToolDeduplication: true,
            MaxRecoveryAttempts: 3,
            MaxConsecutiveSameTool: 3);

        // 4. 构建 QueryRuntimeRequest
        var runtimeRequest = new QueryRuntimeRequest
        {
            SessionId = runtimeSession.Id ?? "unknown",
            EntryPoint = QueryLoopEntryPoint.DefaultCodexKernel,
            InitialMessages = messages,
            Options = new ChatOptions
            {
                Temperature = 0.7f,
                Tools = availableTools.Cast<AITool>().ToList()
            },
            Scenario = MemoryInjectionScenario.Execution,
            Session = runtimeSession,
            MaxRounds = 60,
            EnableTools = enableTools,
            AvailableTools = availableTools,
            AvailableToolsProvider = BuildAvailableTools,
            AvailableCodexTools = availableCodexTools,
            AvailableCodexToolsProvider = BuildAvailableCodexTools,
            WorkerContext = workerContext,
            RequiredToolContract = workerContext?.RequiredToolContract,
            AdapterHints = hints,
            PromptMetadata = new PromptMetadata(
                RolePrompt: rolePrompt,
                WorkspacePath: session.WorkspacePath,
                PlanSize: session.Plan?.Count ?? 0,
                InitialStage: session.CurrentStage)
        };

        // 5. 创建 Kernel 专用 adapter（处理 critique loop + guardrail）
        // Phase 4A.1: Adapter 同时实现 IQueryRuntimeInterventionHook
        var kernelAdapter = new KernelRuntimeEventAdapter(
            session,
            role,
            _critiqueService,
            _guardrail,
            _logger,
            OnEvent);

        // 5.1. 设置 InterventionHook — 组合 scope guard 和 guardrail/critique
        IQueryRuntimeInterventionHook interventionHook = kernelAdapter;
        if (taskFileScope is { HasConstraints: true })
        {
            var scopeHook = new TaskScopeInterventionHook(taskFileScope, _logger);
            interventionHook = new CompositeInterventionHook([scopeHook, kernelAdapter], _logger);
        }

        runtimeRequest = runtimeRequest with { InterventionHook = interventionHook };

        // 6. 执行 runtime
        Log.StartingReasoningCycle(_logger, roleName);
        var result = await _queryRuntimeEngine.ExecuteAsync(runtimeRequest, kernelAdapter, ct);

        // 7. 返回 CodexResponse — 把 runtime 的 tool-call 计数和思维链一并带回，
        //    否则 Orchestrator 的 ZeroToolCalls 检测会误判 runtime 路径下每一次执行，
        //    触发无意义的自愈重试。
        var isComplete =
            result.TerminationReason == QueryTerminationReason.Normal ||
            result.TerminationReason == QueryTerminationReason.NoToolCalls;

        return new CodexResponse(
            result.FinalText ?? string.Empty,
            isComplete,
            TotalToolCalls: result.TotalToolCalls,
            WriteToolCalls: result.WriteToolCalls,
            ThinkingContent: result.FinalThinking);
    }

    private WorkerDefinition? ResolveWorkerDefinition(WorkerType? workerType)
    {
        if (!workerType.HasValue || _workerDefinitionRegistry == null)
        {
            return null;
        }

        return _workerDefinitionRegistry.TryGet(workerType.Value, out var definition)
            ? definition
            : null;
    }

    private void ActivateWorkerDeferredTools(WorkerDefinition? workerDefinition)
    {
        if (workerDefinition == null)
        {
            return;
        }

        foreach (var toolName in workerDefinition.AutoActivateToolNames)
        {
            _toolRegistry.ActivateTool(toolName);
        }
    }

    private static ICodexTool ApplyVerifyWorkerToolPolicy(ICodexTool tool)
        => tool is RunCommandTool runCommandTool
            ? runCommandTool.WithPolicy(CommandExecutionPolicy.VerifyWorker)
            : tool;

    private static WorkerType? TryMapRoleToWorkerType(CodexAgentRole role) => role switch
    {
        CodexAgentRole.Forge => WorkerType.Forge,
        CodexAgentRole.Sentry => WorkerType.Verify,
        _ => null
    };

    #endregion
}
