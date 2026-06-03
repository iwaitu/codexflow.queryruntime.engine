namespace CodexFlow.Core.Runtime;

/// <summary>
/// Phase 0B: Query Runtime 事件消费端接口 — 用于 SSE adapter、telemetry、logging 等
/// </summary>
public interface IQueryRuntimeEventSink
{
    /// <summary>
    /// 消费单个 runtime 事件
    /// </summary>
    /// <param name="runtimeEvent">runtime 事件</param>
    ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent);

    /// <summary>
    /// 是否启用特定事件类型（用于过滤）
    /// </summary>
    /// <param name="eventType">事件类型</param>
    /// <returns>是否启用</returns>
    bool IsEnabled(QueryRuntimeEventType eventType);
}

/// <summary>
/// 事件类型枚举（用于过滤）
/// </summary>
public enum QueryRuntimeEventType
{
    RoundStarted,
    ThinkingStarted,
    ThinkingDelta,
    ThinkingEnded,
    AssistantDelta,
    ModelResponseSampled,
    ToolCallRequested,
    ToolExecutionStarted,
    ToolExecutionCompleted,
    StreamingToolDecision,
    RecoveryTriggered,
    SystemNotice,
    PromptAssemblySnapshot,
    LoopPhaseChanged,
    ToolPlanExtracted,
    ToolPlanValidated,
    ToolArgumentsNormalized,
    ToolObservationCompleted,
    ContextCompactionCompleted,
    RoundCompleted,
    Terminated,
    Error,
    ConversationIdSet
}
