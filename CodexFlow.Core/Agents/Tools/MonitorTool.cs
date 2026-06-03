using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using System.Text;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

public sealed class MonitorTool(
    CodexSessionManager sessionManager,
    Func<string, CancellationToken, Task<IReadOnlyList<WorkerJobSummary>>>? listWorkersFunc = null,
    Func<WorkerOutputRequest, CancellationToken, Task<WorkerOutputResult>>? workerOutputFunc = null) : ICodexTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => "monitor";

    public string Description => "监控 worker/job/session 当前状态。参数: session_id?, job_id? 或 worker_id?, include_events?, max_events?, after_seq?。只读，不启动新任务。";

    public ToolCategory Category => ToolCategory.System;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: true,
        IsReadOnly: true,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 20_000);

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        var jobId = arguments.GetValueOrDefault("job_id")?.ToString()
            ?? arguments.GetValueOrDefault("worker_id")?.ToString();
        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        var includeEvents = ReadBoolean(arguments.GetValueOrDefault("include_events")) ?? false;
        var maxEvents = Math.Clamp(ReadInt32(arguments.GetValueOrDefault("max_events")) ?? 20, 1, 100);
        var afterSeq = ReadInt64(arguments.GetValueOrDefault("after_seq"));

        if (string.IsNullOrWhiteSpace(jobId) && string.IsNullOrWhiteSpace(sessionId))
        {
            return CodexToolResult.Error("Missing session_id or job_id.");
        }

        WorkerOutputResult? job = null;
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            if (workerOutputFunc == null)
            {
                return CodexToolResult.Error("Job monitoring is unavailable in this runtime.");
            }

            job = await workerOutputFunc(
                new WorkerOutputRequest
                {
                    JobId = jobId,
                    AfterSeq = afterSeq,
                    MaxEvents = includeEvents ? maxEvents : 1,
                    IncludeCurrentView = true
                },
                ct).ConfigureAwait(false);

            if (!job.Success)
            {
                return CodexToolResult.Error(job.Message ?? $"Unable to monitor job: {jobId}");
            }

            if (string.IsNullOrWhiteSpace(sessionId))
            {
                sessionId = job.View?.SessionId;
            }
        }

        CodexSession? session = null;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            session = await sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
        }

        IReadOnlyList<WorkerJobSummary> workers = Array.Empty<WorkerJobSummary>();
        if (!string.IsNullOrWhiteSpace(sessionId) && listWorkersFunc != null)
        {
            workers = await listWorkersFunc(sessionId, ct).ConfigureAwait(false);
        }

        var metadata = new
        {
            kind = "monitor_snapshot",
            session = session == null ? null : BuildSessionSnapshot(session),
            job,
            workers,
            generated_at_utc = DateTime.UtcNow
        };
        var output = FormatOutput(session, job, workers, includeEvents);

        return CodexToolResult.Succeeded(
            output,
            metadata,
            summary: BuildSummary(session, job, workers));
    }

    private static object BuildSessionSnapshot(CodexSession session)
    {
        return new
        {
            session_id = session.Id,
            user_id = session.UserId,
            workspace_path = session.WorkspacePath,
            current_stage = session.CurrentStage,
            active_task_id = session.ActiveTaskId,
            plan_count = session.Plan.Count,
            plan_status = session.Plan
                .GroupBy(task => task.Status.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            metadata = session.Metadata
        };
    }

    private static string FormatOutput(
        CodexSession? session,
        WorkerOutputResult? job,
        IReadOnlyList<WorkerJobSummary> workers,
        bool includeEvents)
    {
        var builder = new StringBuilder();
        builder.AppendLine("MONITOR_SNAPSHOT");

        if (session != null)
        {
            builder.AppendLine($"session_id: {session.Id}");
            builder.AppendLine($"stage: {session.CurrentStage}");
            if (!string.IsNullOrWhiteSpace(session.ActiveTaskId))
            {
                builder.AppendLine($"active_task_id: {session.ActiveTaskId}");
            }
            builder.AppendLine($"plan_count: {session.Plan.Count}");
        }

        if (job?.View != null)
        {
            builder.AppendLine($"job_id: {job.JobId}");
            builder.AppendLine($"job_status: {job.View.Status ?? "unknown"}");
            if (!string.IsNullOrWhiteSpace(job.View.WorkerType))
            {
                builder.AppendLine($"worker_type: {job.View.WorkerType}");
            }
            if (!string.IsNullOrWhiteSpace(job.View.StateKind))
            {
                builder.AppendLine($"state: {job.View.StateKind}");
            }
            builder.AppendLine($"latest_seq: {job.View.LatestSeq}");
            if (job.View.WaitingUser)
            {
                builder.AppendLine($"waiting_user: {job.View.WaitingReason ?? "true"}");
            }
            if (job.View.RecoveryNeeded)
            {
                builder.AppendLine($"recovery_needed: {job.View.RecoveryReason ?? "true"}");
            }
        }

        builder.AppendLine($"workers_count: {workers.Count}");
        foreach (var worker in workers.Take(20))
        {
            builder.Append("- ")
                .Append(worker.JobId)
                .Append(" status=").Append(worker.Status)
                .Append(" worker=").Append(worker.WorkerType);
            if (!string.IsNullOrWhiteSpace(worker.TaskId))
            {
                builder.Append(" task=").Append(worker.TaskId);
            }
            if (!string.IsNullOrWhiteSpace(worker.StateKind))
            {
                builder.Append(" state=").Append(worker.StateKind);
            }
            builder.AppendLine();
        }

        if (includeEvents && job?.Events.Count > 0)
        {
            builder.AppendLine("events:");
            foreach (var evt in job.Events)
            {
                builder.Append("- #").Append(evt.Seq)
                    .Append(' ').Append(evt.EventType)
                    .Append(": ").AppendLine(Trim(evt.PayloadJson, 500));
            }
        }

        builder.AppendLine("json:");
        builder.Append(JsonSerializer.Serialize(new { session = session?.Id, job = job?.JobId, workers = workers.Count }, JsonOptions));
        return builder.ToString().TrimEnd();
    }

    private static string BuildSummary(CodexSession? session, WorkerOutputResult? job, IReadOnlyList<WorkerJobSummary> workers)
    {
        var sessionPart = session == null ? "no session" : $"session {session.Id}";
        var jobPart = job?.View == null ? "no job" : $"job {job.JobId} status={job.View.Status ?? "unknown"}";
        return $"{sessionPart}; {jobPart}; workers={workers.Count}";
    }

    private static string Trim(string text, int maxChars)
        => text.Length <= maxChars ? text : text[..maxChars] + "...";

    private static bool? ReadBoolean(object? raw)
        => raw switch
        {
            bool value => value,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null
        };

    private static int? ReadInt32(object? raw)
        => raw switch
        {
            int value => value,
            long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null
        };

    private static long? ReadInt64(object? raw)
        => raw switch
        {
            long value => value,
            int value => value,
            string text when long.TryParse(text, out var parsed) => parsed,
            _ => null
        };
}
