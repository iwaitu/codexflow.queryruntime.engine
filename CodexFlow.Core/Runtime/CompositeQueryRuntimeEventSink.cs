namespace CodexFlow.Core.Runtime;

public sealed class CompositeQueryRuntimeEventSink(params IQueryRuntimeEventSink[] sinks) : IQueryRuntimeEventSink
{
    private readonly IReadOnlyList<IQueryRuntimeEventSink> _sinks = sinks ?? [];

    public bool IsEnabled(QueryRuntimeEventType eventType)
        => _sinks.Any(s => s.IsEnabled(eventType));

    public async ValueTask OnEventAsync(QueryRuntimeEvent runtimeEvent)
    {
        var eventType = GetEventType(runtimeEvent);
        foreach (var sink in _sinks)
        {
            if (!sink.IsEnabled(eventType))
            {
                continue;
            }

            await sink.OnEventAsync(runtimeEvent);
        }
    }

    private static QueryRuntimeEventType GetEventType(QueryRuntimeEvent evt) => evt switch
    {
        RoundStartedEvent => QueryRuntimeEventType.RoundStarted,
        ThinkingStartedEvent => QueryRuntimeEventType.ThinkingStarted,
        ThinkingDeltaEvent => QueryRuntimeEventType.ThinkingDelta,
        ThinkingEndedEvent => QueryRuntimeEventType.ThinkingEnded,
        AssistantDeltaEvent => QueryRuntimeEventType.AssistantDelta,
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
        ToolObservationCompletedEvent => QueryRuntimeEventType.ToolObservationCompleted,
        ContextCompactionCompletedEvent => QueryRuntimeEventType.ContextCompactionCompleted,
        RoundCompletedEvent => QueryRuntimeEventType.RoundCompleted,
        TerminatedEvent => QueryRuntimeEventType.Terminated,
        ErrorEvent => QueryRuntimeEventType.Error,
        ConversationIdSetEvent => QueryRuntimeEventType.ConversationIdSet,
        _ => QueryRuntimeEventType.RoundStarted
    };
}
