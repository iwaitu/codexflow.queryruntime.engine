using CodexFlow.Core.Telemetry;

namespace CodexFlow.Core.Runtime;

/// <summary>
/// Phase 0B: Query Runtime 内部事件基类 — 与外部 SSE 协议解耦
/// </summary>
public abstract record QueryRuntimeEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    DateTimeOffset Timestamp = default);

/// <summary>轮次开始事件</summary>
public sealed record RoundStartedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    int MaxRounds,
    long ContextChars) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>思维链开始事件</summary>
public sealed record ThinkingStartedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>思维链增量事件</summary>
public sealed record ThinkingDeltaEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    string Delta) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>思维链结束事件</summary>
public sealed record ThinkingEndedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    string FullThinking) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>Assistant 文本增量事件</summary>
public sealed record AssistantDeltaEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    string Delta) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>Completed model sampling result before legacy tool-call recovery and tool-plan extraction.</summary>
public sealed record ModelResponseSampledEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    int AssistantTextLength,
    int ThinkingTextLength,
    int StructuredToolCallCount,
    int PrestartedToolExecutionCount) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>工具调用请求事件</summary>
public sealed record ToolCallRequestedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    string ToolName,
    string CallId,
    IReadOnlyDictionary<string, object?> Arguments) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>工具执行开始事件</summary>
public sealed record ToolExecutionStartedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    string ToolName,
    string CallId) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>工具执行完成事件</summary>
public sealed record ToolExecutionCompletedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    string ToolName,
    string CallId,
    string Result,
    bool Success,
    int? ResultLength = null) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>Streaming-first tool execution planning decision.</summary>
public sealed record StreamingToolDecisionEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    string ToolName,
    string CallId,
    bool Started,
    string Reason,
    string? Detail = null) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>恢复触发事件</summary>
public sealed record RecoveryTriggeredEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    string RecoveryType,
    int Attempt,
    string Reason) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>系统通知事件（用于注入 warning/urgency prompt）</summary>
public sealed record SystemNoticeEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    string NoticeType,
    string Content) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>Prompt/context assembly diagnostic event emitted before each model request.</summary>
public sealed record PromptAssemblySnapshotEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    PromptAssemblySnapshot Snapshot) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>Query loop stage transition diagnostic event.</summary>
public sealed record LoopPhaseChangedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    QueryRuntimeLoopPhase Phase,
    string? Detail = null) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>Tool plan extracted from a completed model response.</summary>
public sealed record ToolPlanExtractedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    int ToolCallCount,
    bool FromLegacyTextFallback,
    int AssistantTextLength,
    int ThinkingTextLength) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>Tool plan validation result before execution.</summary>
public sealed record ToolPlanValidatedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    int AcceptedCount,
    int RejectedCount,
    IReadOnlyList<RejectedToolCall> RejectedCalls,
    bool RequiresRecovery,
    string? RecoveryReason = null) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>Tool argument normalization result before execution.</summary>
public sealed record ToolArgumentsNormalizedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    int AcceptedCount,
    int NormalizedCount,
    int PrestartedToolCount) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>Tool observation result after execution and ledger update.</summary>
public sealed record ToolObservationCompletedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    int ToolResultCount,
    bool HasWriteEvidence,
    bool HasRepeatedReadEvidence,
    IReadOnlyList<string> RepeatedReadTargets,
    bool RequiredToolContractSatisfied,
    int FileEvidenceCount,
    int ToolEvidenceCount,
    int PendingModificationCount,
    int FailureCount) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>Context window governance / compaction completion diagnostic event.</summary>
public sealed record ContextCompactionCompletedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    bool Success,
    string? ErrorType,
    string? ErrorMessage,
    int? PromptTokens,
    int? CompletionTokens,
    int FinalMessageCount,
    int FileEvidenceCount,
    int PendingModificationCount) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>轮次完成事件</summary>
public sealed record RoundCompletedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    int Round,
    int ToolCallCount,
    bool HasText,
    int TextLength,
    int ThinkingLength,
    string? ContinueReason,
    int? PromptTokens = null,
    int? CompletionTokens = null) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>终止事件</summary>
public sealed record TerminatedEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    QueryTerminationReason Reason,
    int TotalRounds,
    int TotalToolCalls,
    long TotalDurationMs,
    string? DetailCode = null) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>错误事件</summary>
public sealed record ErrorEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    string ErrorType,
    string Message,
    Exception? Exception = null) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);

/// <summary>Conversation ID 设置事件（用于 CodexController 兼容）</summary>
public sealed record ConversationIdSetEvent(
    long Seq,
    Guid QueryId,
    string SessionId,
    QueryLoopEntryPoint EntryPoint,
    string ConversationId) : QueryRuntimeEvent(Seq, QueryId, SessionId, EntryPoint);
