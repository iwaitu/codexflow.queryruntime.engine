namespace CodexFlow.Core.Gateway;

/// <summary>
/// Gateway 向前端 SSE 推送的事件类型
/// </summary>
public static class GatewaySseEventType
{
    // --- AI 流式内容（复用现有 SSE 协议）---
    public const string Content = "content";
    public const string ThinkingStart = "thinking_start";
    public const string ThinkingContent = "thinking_content";
    public const string ThinkingEnd = "thinking_end";
    public const string ToolCall = "tool_call";

    // --- Gateway 特有事件 ---
    public const string SetConversationId = "setconversationid";
    public const string UpdateConversationTitle = "updateconversationtitle";
    public const string TaskStatus = "task_status";
    public const string SystemMessage = "system_message";
    public const string NextTaskStarted = "next_task_started";
    public const string RetryNotice = "retry_notice";
    public const string PlanChangeRequest = "plan_change_request";
    public const string PlanningSummary = "planning_summary";
    public const string WorkerStarted = "worker_started";
    public const string WorkerWaitingUser = "worker_waiting_user";
    public const string WorkerCompleted = "worker_completed";
    public const string WorkerFailed = "worker_failed";
    public const string FileTreeUpdated = "file_tree_updated";
    public const string Error = "error";
    public const string Heartbeat = "heartbeat";
    public const string Done = "done";
}

/// <summary>
/// SSE 推送的单个事件
/// </summary>
public sealed record GatewaySseEvent
{
    /// <summary>SSE event id (用于 Last-Event-ID 断线重连)</summary>
    public long Seq { get; init; }

    /// <summary>事件类型，见 <see cref="GatewaySseEventType"/></summary>
    public required string Type { get; init; }

    /// <summary>事件负载（JSON 序列化后写入 SSE data 字段）</summary>
    public required object Payload { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
