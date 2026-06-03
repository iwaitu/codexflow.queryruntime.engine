namespace CodexFlow.Core.Abstractions;

public sealed record CronScheduleDefinition
{
    public string Id { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string? TaskId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Cron { get; init; } = string.Empty;
    public string WorkerType { get; init; } = "forge";
    public string Prompt { get; init; } = string.Empty;
    public string? WorkspacePath { get; init; }
    public string? TimeZone { get; init; }
    public int? MaxRounds { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

public interface ICronSchedulerService
{
    Task<CronScheduleDefinition> CreateAsync(CronScheduleDefinition schedule, CancellationToken ct = default);
    Task<bool> DeleteAsync(string scheduleId, CancellationToken ct = default);
    Task<IReadOnlyList<CronScheduleDefinition>> ListAsync(string? sessionId = null, CancellationToken ct = default);
}

public sealed record RemoteTriggerEventDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string? SessionId { get; init; }
    public string? UserId { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public bool DispatchWorker { get; init; }
    public string? WorkerType { get; init; }
    public string? Prompt { get; init; }
    public string? WorkspacePath { get; init; }
    public string? WorkerJobId { get; init; }
    public DateTime ReceivedAtUtc { get; init; } = DateTime.UtcNow;
}

public interface IRemoteTriggerService
{
    Task<RemoteTriggerEventDefinition> RecordAsync(RemoteTriggerEventDefinition triggerEvent, CancellationToken ct = default);
    Task<IReadOnlyList<RemoteTriggerEventDefinition>> ListAsync(string? sessionId = null, string? source = null, string? eventType = null, CancellationToken ct = default);
}

public sealed record PushNotificationRequest
{
    public string? SessionId { get; init; }
    public string? TaskId { get; init; }
    public required string UserId { get; init; }
    public string? JobId { get; init; }
    public string Title { get; init; } = "CodexFlow notification";
    public string Message { get; init; } = string.Empty;
    public string MarkdownReport { get; init; } = string.Empty;
    public string Priority { get; init; } = "P2";
    public IReadOnlyList<string> Channels { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed record PushNotificationResult
{
    public required bool Success { get; init; }
    public IReadOnlyList<string> DeliveredChannels { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
    public string? NotificationId { get; init; }
}

public interface IPushNotificationService
{
    Task<PushNotificationResult> PushAsync(PushNotificationRequest request, CancellationToken ct = default);
}

public sealed record WorkflowRunRecord
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string SkillName { get; init; } = "workflow";
    public string ScriptPath { get; init; } = string.Empty;
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    public string? SessionId { get; init; }
    public string? UserId { get; init; }
    public string? WorkspacePath { get; init; }
    public string Status { get; init; } = "running";
    public string? Output { get; init; }
    public string? Error { get; init; }
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; init; }
}

public interface IWorkflowAuditStore
{
    Task<WorkflowRunRecord> StartAsync(WorkflowRunRecord run, CancellationToken ct = default);
    Task<WorkflowRunRecord?> CompleteAsync(string workflowId, string status, string? output = null, string? errorMessage = null, CancellationToken ct = default);
    Task<WorkflowRunRecord?> GetAsync(string workflowId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowRunRecord>> ListAsync(string? sessionId = null, string? name = null, CancellationToken ct = default);
}
