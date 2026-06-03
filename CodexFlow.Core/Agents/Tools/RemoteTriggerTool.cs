using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Protocols;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

public sealed class RemoteTriggerTool(
    IRemoteTriggerService triggerService,
    Func<SpawnWorkerRequest, CancellationToken, Task<SpawnWorkerResult>>? spawnWorkerFunc = null) : ICodexTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => "remote_trigger";

    public string Description => "记录外部 webhook/event，并可选派发 worker。参数: source, event_type, payload?, session_id?, user_id?, workspace_path?, dispatch_worker?, worker_type?, prompt?, task_id?, max_rounds?, list?。";

    public ToolCategory Category => ToolCategory.System;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 16_384);

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        var action = arguments.GetValueOrDefault("action")?.ToString();
        if (string.Equals(action, "list", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arguments.GetValueOrDefault("list")?.ToString(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return await ListAsync(arguments, ct).ConfigureAwait(false);
        }

        var source = arguments.GetValueOrDefault("source")?.ToString();
        var eventType = arguments.GetValueOrDefault("event_type")?.ToString()
            ?? arguments.GetValueOrDefault("event")?.ToString();
        if (string.IsNullOrWhiteSpace(source))
        {
            return CodexToolResult.Error("Missing source.");
        }
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return CodexToolResult.Error("Missing event_type.");
        }

        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        var userId = arguments.GetValueOrDefault("user_id")?.ToString();
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var prompt = arguments.GetValueOrDefault("prompt")?.ToString();
        var dispatchWorker = ReadBool(arguments.GetValueOrDefault("dispatch_worker")) ?? false;
        var workerJobId = default(string);

        if (dispatchWorker)
        {
            if (spawnWorkerFunc == null)
            {
                return CodexToolResult.Error("Worker dispatch is unavailable in this runtime.");
            }
            if (string.IsNullOrWhiteSpace(sessionId) ||
                string.IsNullOrWhiteSpace(userId) ||
                string.IsNullOrWhiteSpace(workspacePath) ||
                string.IsNullOrWhiteSpace(prompt))
            {
                return CodexToolResult.Error("dispatch_worker requires session_id, user_id, workspace_path, and prompt.");
            }
        }

        var workerTypeText = arguments.GetValueOrDefault("worker_type")?.ToString() ?? "forge";
        var workerType = ParseWorkerType(workerTypeText);
        if (workerType == null)
        {
            return CodexToolResult.Error($"Unsupported worker_type: {workerTypeText}");
        }

        if (dispatchWorker)
        {
            var spawnResult = await spawnWorkerFunc!(
                new SpawnWorkerRequest
                {
                    SessionId = sessionId!,
                    UserId = userId!,
                    WorkspacePath = workspacePath!,
                    WorkerType = workerType.Value,
                    Prompt = prompt!,
                    Description = $"remote_trigger:{source}:{eventType}",
                    TaskId = arguments.GetValueOrDefault("task_id")?.ToString(),
                    MaxRounds = ReadInt(arguments.GetValueOrDefault("max_rounds"))
                },
                ct).ConfigureAwait(false);

            if (!spawnResult.Success)
            {
                return CodexToolResult.Error(spawnResult.Message ?? "remote_trigger failed to dispatch worker.");
            }

            workerJobId = spawnResult.JobId;
        }

        var triggerEvent = new RemoteTriggerEventDefinition
        {
            Source = source.Trim(),
            EventType = eventType.Trim(),
            SessionId = sessionId,
            UserId = userId,
            PayloadJson = SerializePayload(arguments.GetValueOrDefault("payload")),
            DispatchWorker = dispatchWorker,
            WorkerType = workerType.Value.ToString().ToLowerInvariant(),
            Prompt = prompt,
            WorkspacePath = workspacePath,
            WorkerJobId = workerJobId
        };

        var stored = await triggerService.RecordAsync(triggerEvent, ct).ConfigureAwait(false);
        var result = new
        {
            recorded = true,
            trigger = stored,
            worker_job_id = workerJobId
        };
        var output = JsonSerializer.Serialize(result, JsonOptions);

        return CodexToolResult.Succeeded(
            output,
            result,
            summary: workerJobId == null
                ? $"remote trigger recorded: {stored.Id}"
                : $"remote trigger recorded: {stored.Id}, worker={workerJobId}");
    }

    private async Task<CodexToolResult> ListAsync(Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        var source = arguments.GetValueOrDefault("source")?.ToString();
        var eventType = arguments.GetValueOrDefault("event_type")?.ToString()
            ?? arguments.GetValueOrDefault("event")?.ToString();
        var events = await triggerService.ListAsync(sessionId, source, eventType, ct).ConfigureAwait(false);
        var result = new { count = events.Count, events };
        return CodexToolResult.Succeeded(
            JsonSerializer.Serialize(result, JsonOptions),
            result,
            summary: $"remote triggers: {events.Count}");
    }

    private static WorkerType? ParseWorkerType(string value)
    {
        return Enum.TryParse<WorkerType>(value, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    private static string SerializePayload(object? raw)
    {
        if (raw == null)
        {
            return "{}";
        }

        if (raw is string text)
        {
            return string.IsNullOrWhiteSpace(text) ? "{}" : text;
        }

        return JsonSerializer.Serialize(raw, JsonOptions);
    }

    private static bool? ReadBool(object? raw)
        => raw switch
        {
            bool value => value,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null
        };

    private static int? ReadInt(object? raw)
        => raw switch
        {
            int value => value,
            long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null
        };
}
