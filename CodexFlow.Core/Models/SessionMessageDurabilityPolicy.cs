namespace CodexFlow.Core.Models;

/// <summary>
/// Maps runtime/orchestrator events to session-message durability semantics.
/// </summary>
public static class SessionMessageDurabilityPolicy
{
    public static SessionTurnWrite FromEvent(CodexEventType type, string message)
    {
        return type switch
        {
            CodexEventType.StageStarted or
            CodexEventType.StageCompleted or
            CodexEventType.TaskStarted or
            CodexEventType.TaskCompleted or
            CodexEventType.TaskFailed or
            CodexEventType.GuardrailBlocked or
            CodexEventType.ValidationResult or
            CodexEventType.TaskListUpdated or
            CodexEventType.PlanningSummary or
            CodexEventType.RecoveryTriggered or
            CodexEventType.Error
                => SessionTurnWrite.SystemBoundary(message),

            _ => SessionTurnWrite.Progress(message)
        };
    }
}
