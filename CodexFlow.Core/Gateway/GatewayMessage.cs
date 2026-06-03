namespace CodexFlow.Core.Gateway;

/// <summary>
/// Gateway 消息来源类型
/// </summary>
public enum GatewayMessageSource
{
    /// <summary>用户通过 codex/chat 发送的消息</summary>
    UserChat,

    /// <summary>后台任务成功完成</summary>
    TaskCompleted,

    /// <summary>后台任务执行失败</summary>
    TaskFailed,

    /// <summary>系统事件（通知、警告等）</summary>
    SystemEvent
}

/// <summary>
/// Gateway 统一消息模型 — 用户消息和系统事件在队列中是同一种类型
/// </summary>
public sealed class GatewayMessage
{
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N");
    public required GatewayMessageSource Source { get; init; }
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public string? ConversationId { get; set; }
    public string? TaskId { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// 消息内容：
    /// - UserChat: 用户输入的文本
    /// - TaskCompleted/TaskFailed: LLM-facing worker notification XML envelope
    /// - SystemEvent: 系统事件描述
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// 附加元数据（任务结果详情、重试计数等）
    /// </summary>
    public GatewayMessageMeta Meta { get; init; } = new();
}

/// <summary>
/// 消息元数据
/// </summary>
public sealed class GatewayMessageMeta
{
    /// <summary>原始 ChatRequest 字段（仅 UserChat 时有值）</summary>
    public string? GitRepoUrl { get; init; }
    public string? Branch { get; init; }
    public bool ApproveGitPush { get; init; }
    public string? ReplyId { get; init; }
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// P3-01: 会话模式（仅 UserChat 时有值）
    /// <list type="bullet">
    ///   <item><description>continue-current: 继续当前 conversation</description></item>
    ///   <item><description>reuse-master: 复用主会话（默认）</description></item>
    ///   <item><description>new-thread: 强制创建新 conversation</description></item>
    /// </list>
    /// </summary>
    public string? ConversationMode { get; init; }

    /// <summary>任务执行结果（仅 TaskCompleted/TaskFailed 时有值）</summary>
    public string? JobId { get; init; }
    public bool? TaskSuccess { get; init; }
    public string? TaskResultMessage { get; init; }
    public int RetryCount { get; init; }
    public int MaxRetries { get; init; } = 3;

    /// <summary>计划中的下一个待执行任务（仅 TaskCompleted 时由 Gateway 预填）</summary>
    public string? NextTaskId { get; init; }
    public string? NextTaskTitle { get; init; }
}
