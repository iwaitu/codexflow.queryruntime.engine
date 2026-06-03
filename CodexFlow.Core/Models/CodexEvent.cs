namespace CodexFlow.Core.Models;

public enum CodexEventType
{
    General,
    StageStarted,
    StageCompleted,
    TaskStarted,
    TaskProgress,
    TaskCompleted,
    TaskFailed,
    ArchitectureAudit,
    SecurityAudit,
    GuardrailBlocked,
    CritiqueFeedback,
    ValidationResult,
    TaskListUpdated,
    CodePreview,
    PlanningSummary,

    // Runtime integration events (Phase 4A)
    Content,
    ThinkingStart,
    ThinkingContent,
    ThinkingEnd,
    ToolCall,
    ToolResult,
    RecoveryTriggered,
    Error
}

public class CodexEvent
{
    public string SessionId { get; set; } = string.Empty;
    public string? TaskId { get; set; }
    public CodexEventType Type { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Payload { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public static CodexEvent Info(string sessionId, string message, string? taskId = null)
        => new() { SessionId = sessionId, Type = CodexEventType.General, Message = message, TaskId = taskId };

    public static CodexEvent Task(string sessionId, string taskId, CodexEventType type, string message, object? payload = null)
        => new() { SessionId = sessionId, TaskId = taskId, Type = type, Message = message, Payload = payload };
}
