using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Telemetry;
using Microsoft.Extensions.AI;
using QreEngine = CodexFlow.QueryRuntime.Engine;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Core adapter that consumes the standalone QRE engine through the Core runtime interface.
/// This is intentionally a narrow bridge; platform-specific session, memory, hook, and
/// recovery behavior remains on <see cref="QueryRuntimeEngine"/> until it can be moved
/// behind explicit QRE adapters.
/// </summary>
public sealed class QreBackedQueryRuntimeEngine(ILLMExecutor llmExecutor) : IQueryRuntimeEngine
{
    public async Task<QueryRuntimeResult> ExecuteAsync(
        QueryRuntimeRequest request,
        IQueryRuntimeEventSink eventSink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);

        var runId = Guid.NewGuid().ToString("N");
        var workspacePath = request.PromptMetadata?.WorkspacePath;
        var traceFilePath = BuildTraceFilePath(workspacePath, runId);
        var tools = ResolveAvailableTools(request);
        var engine = new QreEngine.QueryRuntimeEngine(new CoreModelClient(llmExecutor, request));
        var qreRequest = new QreEngine.QueryRuntimeRequest
        {
            SessionId = request.SessionId,
            InitialMessages = request.InitialMessages,
            Options = request.Options,
            MaxRounds = request.MaxRounds,
            EnableTools = request.EnableTools,
            AvailableTools = tools,
            RequiredToolName = request.RequiredToolContract?.ResolveRequiredToolName(tools)
        };

        var result = await engine.ExecuteAsync(
            qreRequest,
            new EventSinkAdapter(eventSink, request.EntryPoint),
            runId,
            traceFilePath,
            workspacePath,
            ct).ConfigureAwait(false);

        return new QueryRuntimeResult
        {
            TerminationReason = MapTerminationReason(result.TerminationReason),
            TotalRounds = result.TotalRounds,
            TotalToolCalls = result.TotalToolCalls,
            ZeroToolCallRounds = result.TotalToolCalls == 0 ? result.TotalRounds : 0,
            EmptyResponseCount = 0,
            RecoveryCount = 0,
            MalformedProtocolCount = 0,
            FinalText = result.FinalText,
            TotalDurationMs = result.TotalDurationMs,
            TerminalDetailCode = result.TerminationReason == QreEngine.QueryTerminationReason.MaxRounds
                ? QueryTerminalDetailCodes.MaxRoundsReached
                : null
        };
    }

    private static IReadOnlyList<AIFunction> ResolveAvailableTools(QueryRuntimeRequest request)
        => request.AvailableToolsProvider?.Invoke() ?? request.AvailableTools ?? [];

    private static string BuildTraceFilePath(string? workspacePath, string runId)
    {
        var root = string.IsNullOrWhiteSpace(workspacePath)
            ? Directory.GetCurrentDirectory()
            : workspacePath;
        return Path.Combine(Path.GetFullPath(root), ".qre", "runs", runId, "events.jsonl");
    }

    private static QueryTerminationReason MapTerminationReason(QreEngine.QueryTerminationReason reason)
        => reason switch
        {
            QreEngine.QueryTerminationReason.NoToolCalls => QueryTerminationReason.NoToolCalls,
            QreEngine.QueryTerminationReason.MaxRounds => QueryTerminationReason.MaxRoundsReached,
            QreEngine.QueryTerminationReason.Error => QueryTerminationReason.Exception,
            _ => QueryTerminationReason.Exception
        };

    private sealed class CoreModelClient(
        ILLMExecutor llmExecutor,
        QueryRuntimeRequest runtimeRequest) : QreEngine.IQueryRuntimeModelClient
    {
        public IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            QreEngine.QueryRuntimeModelRequest request,
            CancellationToken ct = default)
        {
            return llmExecutor.StreamAsync(
                new LLMExecutionRequest(
                    request.Messages,
                    request.Options,
                    runtimeRequest.Scenario,
                    runtimeRequest.Session,
                    Caller: nameof(QreBackedQueryRuntimeEngine)),
                ct);
        }
    }

    private sealed class EventSinkAdapter(
        IQueryRuntimeEventSink inner,
        QueryLoopEntryPoint entryPoint) : QreEngine.IQueryRuntimeEventSink
    {
        public bool IsEnabled(QreEngine.QueryRuntimeEventType eventType)
            => inner.IsEnabled(MapEventType(eventType));

        public ValueTask OnEventAsync(QreEngine.QueryRuntimeEvent runtimeEvent)
            => inner.OnEventAsync(MapEvent(runtimeEvent));

        private static QueryRuntimeEventType MapEventType(QreEngine.QueryRuntimeEventType eventType)
            => eventType switch
            {
                QreEngine.QueryRuntimeEventType.PromptAssemblySnapshot => QueryRuntimeEventType.PromptAssemblySnapshot,
                QreEngine.QueryRuntimeEventType.ModelResponseSampled => QueryRuntimeEventType.ModelResponseSampled,
                QreEngine.QueryRuntimeEventType.ToolCallRequested => QueryRuntimeEventType.ToolCallRequested,
                QreEngine.QueryRuntimeEventType.ToolExecutionStarted => QueryRuntimeEventType.ToolExecutionStarted,
                QreEngine.QueryRuntimeEventType.ToolExecutionCompleted => QueryRuntimeEventType.ToolExecutionCompleted,
                QreEngine.QueryRuntimeEventType.RoundStarted => QueryRuntimeEventType.RoundStarted,
                QreEngine.QueryRuntimeEventType.RoundCompleted => QueryRuntimeEventType.RoundCompleted,
                QreEngine.QueryRuntimeEventType.Terminated => QueryRuntimeEventType.Terminated,
                QreEngine.QueryRuntimeEventType.Error => QueryRuntimeEventType.Error,
                _ => QueryRuntimeEventType.SystemNotice
            };

        private QueryRuntimeEvent MapEvent(QreEngine.QueryRuntimeEvent runtimeEvent)
            => runtimeEvent switch
            {
                QreEngine.RoundStartedEvent evt => new RoundStartedEvent(
                    evt.Seq,
                    evt.QueryId,
                    evt.SessionId,
                    entryPoint,
                    evt.Round,
                    evt.MaxRounds,
                    ContextChars: 0),
                QreEngine.PromptAssemblySnapshotEvent evt => new PromptAssemblySnapshotEvent(
                    evt.Seq,
                    evt.QueryId,
                    evt.SessionId,
                    entryPoint,
                    evt.Round,
                    new PromptAssemblySnapshot
                    {
                        QueryId = evt.QueryId,
                        SessionId = evt.SessionId,
                        Round = evt.Round,
                        EntryPoint = entryPoint.ToString(),
                        Frames = [],
                        ToolNames = evt.ToolNames,
                        RequiredToolName = evt.RequiredToolName,
                        ToolsEnabled = evt.ToolCallsAllowed,
                        ToolCallsAllowed = evt.ToolCallsAllowed,
                        MessageCount = evt.MessageCount,
                        EstimatedContextChars = 0,
                        EstimatedPromptTokens = 0
                    }),
                QreEngine.ModelResponseSampledEvent evt => new ModelResponseSampledEvent(
                    evt.Seq,
                    evt.QueryId,
                    evt.SessionId,
                    entryPoint,
                    evt.Round,
                    evt.AssistantTextLength,
                    ThinkingTextLength: 0,
                    evt.StructuredToolCallCount,
                    PrestartedToolExecutionCount: 0),
                QreEngine.ToolCallRequestedEvent evt => new ToolCallRequestedEvent(
                    evt.Seq,
                    evt.QueryId,
                    evt.SessionId,
                    entryPoint,
                    evt.Round,
                    evt.ToolName,
                    evt.CallId,
                    evt.Arguments),
                QreEngine.ToolExecutionStartedEvent evt => new ToolExecutionStartedEvent(
                    evt.Seq,
                    evt.QueryId,
                    evt.SessionId,
                    entryPoint,
                    evt.Round,
                    evt.ToolName,
                    evt.CallId),
                QreEngine.ToolExecutionCompletedEvent evt => new ToolExecutionCompletedEvent(
                    evt.Seq,
                    evt.QueryId,
                    evt.SessionId,
                    entryPoint,
                    evt.Round,
                    evt.ToolName,
                    evt.CallId,
                    evt.Result,
                    evt.Success,
                    evt.ResultLength),
                QreEngine.RoundCompletedEvent evt => new RoundCompletedEvent(
                    evt.Seq,
                    evt.QueryId,
                    evt.SessionId,
                    entryPoint,
                    evt.Round,
                    evt.ToolCallCount,
                    evt.HasText,
                    evt.TextLength,
                    ThinkingLength: 0,
                    evt.ContinueReason),
                QreEngine.TerminatedEvent evt => new TerminatedEvent(
                    evt.Seq,
                    evt.QueryId,
                    evt.SessionId,
                    entryPoint,
                    MapTerminationReason(evt.Reason),
                    evt.TotalRounds,
                    evt.TotalToolCalls,
                    evt.TotalDurationMs,
                    evt.DetailCode),
                QreEngine.ErrorEvent evt => new ErrorEvent(
                    evt.Seq,
                    evt.QueryId,
                    evt.SessionId,
                    entryPoint,
                    evt.ErrorType,
                    evt.Message,
                    evt.Exception),
                _ => new SystemNoticeEvent(
                    runtimeEvent.Seq,
                    runtimeEvent.QueryId,
                    runtimeEvent.SessionId,
                    entryPoint,
                    "qre_unmapped_event",
                    runtimeEvent.GetType().Name)
            };
    }
}
