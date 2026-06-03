using CodexFlow.Core.Abstractions;
using System.Collections.Concurrent;

namespace CodexFlow.Core.Services;

public sealed class InMemoryCronSchedulerService : ICronSchedulerService
{
    private readonly ConcurrentDictionary<string, CronScheduleDefinition> _schedules = new(StringComparer.OrdinalIgnoreCase);

    public Task<CronScheduleDefinition> CreateAsync(CronScheduleDefinition schedule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ct.ThrowIfCancellationRequested();
        var now = DateTime.UtcNow;
        var id = string.IsNullOrWhiteSpace(schedule.Id)
            ? $"cron-{Guid.NewGuid():N}"
            : schedule.Id;

        var stored = schedule with
        {
            Id = id,
            CreatedAtUtc = schedule.CreatedAtUtc == default ? now : schedule.CreatedAtUtc,
            UpdatedAtUtc = now
        };
        _schedules[id] = stored;
        return Task.FromResult(stored);
    }

    public Task<bool> DeleteAsync(string scheduleId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_schedules.TryRemove(scheduleId, out _));
    }

    public Task<IReadOnlyList<CronScheduleDefinition>> ListAsync(string? sessionId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var schedules = _schedules.Values
            .Where(schedule => string.IsNullOrWhiteSpace(sessionId) ||
                               string.Equals(schedule.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(schedule => schedule.CreatedAtUtc)
            .ThenBy(schedule => schedule.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<CronScheduleDefinition>>(schedules);
    }
}

public sealed class InMemoryRemoteTriggerService : IRemoteTriggerService
{
    private readonly ConcurrentDictionary<string, RemoteTriggerEventDefinition> _events = new(StringComparer.OrdinalIgnoreCase);

    public Task<RemoteTriggerEventDefinition> RecordAsync(RemoteTriggerEventDefinition triggerEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(triggerEvent);
        ct.ThrowIfCancellationRequested();

        var id = string.IsNullOrWhiteSpace(triggerEvent.Id)
            ? $"remote-{Guid.NewGuid():N}"
            : triggerEvent.Id;
        var stored = triggerEvent with
        {
            Id = id,
            ReceivedAtUtc = triggerEvent.ReceivedAtUtc == default ? DateTime.UtcNow : triggerEvent.ReceivedAtUtc
        };

        _events[id] = stored;
        return Task.FromResult(stored);
    }

    public Task<IReadOnlyList<RemoteTriggerEventDefinition>> ListAsync(
        string? sessionId = null,
        string? source = null,
        string? eventType = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var events = _events.Values
            .Where(item => string.IsNullOrWhiteSpace(sessionId) ||
                           string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(source) ||
                           string.Equals(item.Source, source, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(eventType) ||
                           string.Equals(item.EventType, eventType, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.ReceivedAtUtc)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<RemoteTriggerEventDefinition>>(events);
    }
}

public sealed class InMemoryPushNotificationService : IPushNotificationService
{
    private readonly ConcurrentQueue<PushNotificationRequest> _notifications = new();

    public IReadOnlyList<PushNotificationRequest> Notifications => _notifications.ToArray();

    public Task<PushNotificationResult> PushAsync(PushNotificationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        _notifications.Enqueue(request);
        var channels = request.Channels.Count > 0
            ? request.Channels
            : ["in_memory"];

        return Task.FromResult(new PushNotificationResult
        {
            Success = true,
            DeliveredChannels = channels.ToArray(),
            NotificationId = $"notification-{Guid.NewGuid():N}"
        });
    }
}

public sealed class InMemoryWorkflowAuditStore : IWorkflowAuditStore
{
    private readonly ConcurrentDictionary<string, WorkflowRunRecord> _runs = new(StringComparer.OrdinalIgnoreCase);

    public Task<WorkflowRunRecord> StartAsync(WorkflowRunRecord run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ct.ThrowIfCancellationRequested();

        var id = string.IsNullOrWhiteSpace(run.Id)
            ? $"workflow-{Guid.NewGuid():N}"
            : run.Id;
        var stored = run with
        {
            Id = id,
            Status = string.IsNullOrWhiteSpace(run.Status) ? "running" : run.Status,
            StartedAtUtc = run.StartedAtUtc == default ? DateTime.UtcNow : run.StartedAtUtc
        };

        _runs[id] = stored;
        return Task.FromResult(stored);
    }

    public Task<WorkflowRunRecord?> CompleteAsync(
        string workflowId,
        string status,
        string? output = null,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workflowId) || !_runs.TryGetValue(workflowId, out var existing))
        {
            return Task.FromResult<WorkflowRunRecord?>(null);
        }

        var updated = existing with
        {
            Status = string.IsNullOrWhiteSpace(status) ? existing.Status : status,
            Output = output,
            Error = errorMessage,
            CompletedAtUtc = DateTime.UtcNow
        };

        _runs[workflowId] = updated;
        return Task.FromResult<WorkflowRunRecord?>(updated);
    }

    public Task<WorkflowRunRecord?> GetAsync(string workflowId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(
            !string.IsNullOrWhiteSpace(workflowId) && _runs.TryGetValue(workflowId, out var run)
                ? run
                : null);
    }

    public Task<IReadOnlyList<WorkflowRunRecord>> ListAsync(
        string? sessionId = null,
        string? name = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var runs = _runs.Values
            .Where(run => string.IsNullOrWhiteSpace(sessionId) ||
                          string.Equals(run.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .Where(run => string.IsNullOrWhiteSpace(name) ||
                          string.Equals(run.Name, name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(run => run.StartedAtUtc)
            .ThenBy(run => run.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<WorkflowRunRecord>>(runs);
    }
}
