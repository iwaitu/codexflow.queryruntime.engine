using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

internal static class TaskCrudToolSupport
{
    public static async Task<CodexSession?> GetSessionAsync(
        CodexSessionManager sessionManager,
        Dictionary<string, object?> arguments,
        CancellationToken ct)
    {
        _ = ct;
        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return await sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
    }

    public static string Serialize(object value)
        => JsonConvert.SerializeObject(value, Formatting.Indented);

    public static bool TryParseStatus(object? value, out CodexTaskStatus status)
    {
        status = CodexTaskStatus.Pending;
        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Enum.TryParse(text.Trim(), ignoreCase: true, out status);
    }

    public static IReadOnlyList<string> ParseStringList(object? value)
    {
        if (value == null)
        {
            return [];
        }

        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                return jsonElement.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToArray();
            }

            if (jsonElement.ValueKind == JsonValueKind.String)
            {
                return ParseStringList(jsonElement.GetString());
            }
        }

        if (value is JArray jArray)
        {
            return jArray.Select(item => item.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
        }

        if (value is IEnumerable<object> objects)
        {
            return objects.Select(item => item.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
        }

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (text.TrimStart()[0] == '[')
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string[]>(text) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                return [];
            }
        }

        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static IReadOnlyList<TaskChecklistItem> ParseChecklist(object? value)
    {
        if (value == null)
        {
            return [];
        }

        try
        {
            if (value is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == JsonValueKind.Array)
                {
                    return JsonConvert.DeserializeObject<List<TaskChecklistItem>>(jsonElement.GetRawText()) ?? [];
                }

                if (jsonElement.ValueKind == JsonValueKind.String)
                {
                    return ParseChecklist(jsonElement.GetString());
                }
            }

            if (value is JArray jArray)
            {
                return jArray.ToObject<List<TaskChecklistItem>>() ?? [];
            }

            var text = value.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            if (text.TrimStart()[0] == '[')
            {
                return JsonConvert.DeserializeObject<List<TaskChecklistItem>>(text) ?? [];
            }

            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select((item, index) => new TaskChecklistItem
                {
                    Id = $"CHK_{index + 1:000}",
                    Text = item,
                    Status = TaskChecklistItemStatus.Pending
                })
                .ToArray();
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return [];
        }
    }

    public static async Task<string?> SaveTaskListSnapshotAsync(
        ITaskListService? taskListService,
        CodexSession session,
        CancellationToken ct)
    {
        if (taskListService == null)
        {
            return null;
        }

        return await taskListService.SaveSnapshotAsync(session, ct).ConfigureAwait(false);
    }
}

public sealed class TaskCreateTool(
    CodexSessionManager sessionManager,
    ITaskListService? taskListService,
    ILogger<TaskCreateTool> logger) : ICodexTool
{
    public string Name => "task_create";
    public string Description => "向当前 session 的任务列表增量新增一个任务。参数: session_id, title, description?, task_type?, status?, stage_id?, dependencies?, inputs?, outputs?, checklist_items?。`create_session_plan` 用于一次性生成计划；task_create 用于计划审批后的增量维护。";
    public ToolCategory Category => ToolCategory.Planning;
    public ToolExecutionMetadata Metadata => new(false, false, false, ToolInterruptBehavior.CancelSafe, 12_000);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var session = await TaskCrudToolSupport.GetSessionAsync(sessionManager, arguments, ct).ConfigureAwait(false);
        if (session == null)
        {
            return CodexToolResult.Error("Missing session_id.");
        }

        var title = arguments.GetValueOrDefault("title")?.ToString();
        if (string.IsNullOrWhiteSpace(title))
        {
            return CodexToolResult.Error("Missing title.");
        }

        var task = new CodexTask
        {
            Id = arguments.GetValueOrDefault("task_id")?.ToString() ?? BuildTaskId(session),
            Title = title.Trim(),
            Description = arguments.GetValueOrDefault("description")?.ToString() ?? string.Empty,
            TaskType = arguments.GetValueOrDefault("task_type")?.ToString() ?? CodexTaskClassifier.CodeTaskType,
            StageId = TryParseInt(arguments.GetValueOrDefault("stage_id"), out var stageId) ? stageId : session.CurrentStage
        };

        if (TaskCrudToolSupport.TryParseStatus(arguments.GetValueOrDefault("status"), out var status))
        {
            task.Status = status;
        }

        task.ReplaceDependencies(TaskCrudToolSupport.ParseStringList(arguments.GetValueOrDefault("dependencies")));
        task.ReplaceInputs(TaskCrudToolSupport.ParseStringList(arguments.GetValueOrDefault("inputs")));
        task.ReplaceOutputs(TaskCrudToolSupport.ParseStringList(arguments.GetValueOrDefault("outputs")));
        task.ReplaceChecklistItems(TaskCrudToolSupport.ParseChecklist(arguments.GetValueOrDefault("checklist_items")));
        CodexTaskProgressNormalizer.NormalizeTaskChecklist(task);

        if (session.Plan.Any(existing => string.Equals(existing.Id, task.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return CodexToolResult.Error($"Task already exists: {task.Id}");
        }

        session.Plan.Add(task);
        session.PlanVersion = Guid.NewGuid().ToString("N")[..8];
        session.PlanGeneratedAtUtc ??= DateTime.UtcNow;

        try
        {
            await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
            var snapshot = await TaskCrudToolSupport.SaveTaskListSnapshotAsync(taskListService, session, ct).ConfigureAwait(false);
            return CodexToolResult.Succeeded(
                $"TASK_CREATED\n{TaskCrudToolSupport.Serialize(task)}",
                new { Task = task, SnapshotJson = snapshot },
                summary: $"task created: {task.Id}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(logger, ex, "task_create failed for session {SessionId}", session.Id);
            return CodexToolResult.Error($"task_create failed: {ex.Message}");
        }
    }

    private static string BuildTaskId(CodexSession session)
        => $"TASK-{session.Plan.Count + 1:000}";

    private static bool TryParseInt(object? value, out int result)
        => int.TryParse(value?.ToString(), out result);
}

public sealed class TaskGetTool(CodexSessionManager sessionManager) : ICodexTool
{
    public string Name => "task_get";
    public string Description => "读取当前 session 中单个任务。参数: session_id, task_id。";
    public ToolCategory Category => ToolCategory.Read;
    public ToolExecutionMetadata Metadata => ToolExecutionMetadata.ForCategory(ToolCategory.Read);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var session = await TaskCrudToolSupport.GetSessionAsync(sessionManager, arguments, ct).ConfigureAwait(false);
        if (session == null)
        {
            return CodexToolResult.Error("Missing session_id.");
        }

        var taskId = arguments.GetValueOrDefault("task_id")?.ToString();
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return CodexToolResult.Error("Missing task_id.");
        }

        var task = session.Plan.FirstOrDefault(item => string.Equals(item.Id, taskId, StringComparison.OrdinalIgnoreCase));
        return task == null
            ? CodexToolResult.Error($"Task not found: {taskId}")
            : CodexToolResult.Succeeded(TaskCrudToolSupport.Serialize(task), task, summary: $"task read: {task.Id}");
    }
}

public sealed class TaskListTool(CodexSessionManager sessionManager, ITaskListService? taskListService) : ICodexTool
{
    public string Name => "task_list";
    public string Description => "列出当前 session 的任务列表，并在可用时附带 task list snapshot。参数: session_id。";
    public ToolCategory Category => ToolCategory.Read;
    public ToolExecutionMetadata Metadata => ToolExecutionMetadata.ForCategory(ToolCategory.Read);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var session = await TaskCrudToolSupport.GetSessionAsync(sessionManager, arguments, ct).ConfigureAwait(false);
        if (session == null)
        {
            return CodexToolResult.Error("Missing session_id.");
        }

        string? snapshot = null;
        if (taskListService != null && !string.IsNullOrWhiteSpace(session.Id))
        {
            snapshot = await taskListService.GetSnapshotAsync(session.Id, ct).ConfigureAwait(false);
        }

        var payload = new
        {
            SessionId = session.Id,
            PlanVersion = session.PlanVersion,
            PlanGeneratedAtUtc = session.PlanGeneratedAtUtc,
            Tasks = session.Plan,
            SnapshotJson = snapshot
        };

        return CodexToolResult.Succeeded(
            TaskCrudToolSupport.Serialize(payload),
            payload,
            summary: $"tasks listed: {session.Plan.Count}");
    }
}

public sealed class TaskUpdateTool(
    CodexSessionManager sessionManager,
    ITaskListService? taskListService,
    ILogger<TaskUpdateTool> logger) : ICodexTool
{
    public string Name => "task_update";
    public string Description => "增量更新当前 session 的单个任务。参数: session_id, task_id, title?, description?, task_type?, status?, stage_id?, dependencies?, inputs?, outputs?, checklist_items?, result_notes?, error_message?。状态更新会同步 task list snapshot。";
    public ToolCategory Category => ToolCategory.Planning;
    public ToolExecutionMetadata Metadata => new(false, false, false, ToolInterruptBehavior.CancelSafe, 12_000);
    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var session = await TaskCrudToolSupport.GetSessionAsync(sessionManager, arguments, ct).ConfigureAwait(false);
        if (session == null)
        {
            return CodexToolResult.Error("Missing session_id.");
        }

        var taskId = arguments.GetValueOrDefault("task_id")?.ToString();
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return CodexToolResult.Error("Missing task_id.");
        }

        var task = session.Plan.FirstOrDefault(item => string.Equals(item.Id, taskId, StringComparison.OrdinalIgnoreCase));
        if (task == null)
        {
            return CodexToolResult.Error($"Task not found: {taskId}");
        }

        ApplyUpdates(task, arguments);
        session.PlanVersion = Guid.NewGuid().ToString("N")[..8];

        try
        {
            await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
            var snapshot = await SaveSnapshotOrStatusAsync(session, task, arguments, ct).ConfigureAwait(false);
            return CodexToolResult.Succeeded(
                $"TASK_UPDATED\n{TaskCrudToolSupport.Serialize(task)}",
                new { Task = task, SnapshotJson = snapshot },
                summary: $"task updated: {task.Id}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(logger, ex, "task_update failed for session {SessionId} task {TaskId}", session.Id, task.Id);
            return CodexToolResult.Error($"task_update failed: {ex.Message}");
        }
    }

    private static void ApplyUpdates(CodexTask task, Dictionary<string, object?> arguments)
    {
        if (arguments.TryGetValue("title", out var title) && !string.IsNullOrWhiteSpace(title?.ToString()))
        {
            task.Title = title.ToString()!.Trim();
        }

        if (arguments.TryGetValue("description", out var description))
        {
            task.Description = description?.ToString() ?? string.Empty;
        }

        if (arguments.TryGetValue("task_type", out var taskType) && !string.IsNullOrWhiteSpace(taskType?.ToString()))
        {
            task.TaskType = taskType.ToString()!.Trim();
        }

        if (arguments.TryGetValue("status", out var statusRaw) &&
            TaskCrudToolSupport.TryParseStatus(statusRaw, out var status))
        {
            task.Status = status;
            if (status == CodexTaskStatus.Executing)
            {
                task.StartedAt ??= DateTime.UtcNow;
            }
            else if (status is CodexTaskStatus.Success or CodexTaskStatus.Failed or CodexTaskStatus.Skipped or CodexTaskStatus.CompletedWithWarnings)
            {
                task.FinishedAt ??= DateTime.UtcNow;
            }
        }

        if (arguments.TryGetValue("stage_id", out var stageIdRaw) &&
            int.TryParse(stageIdRaw?.ToString(), out var stageId))
        {
            task.StageId = stageId;
        }

        if (arguments.TryGetValue("dependencies", out var dependencies))
        {
            task.ReplaceDependencies(TaskCrudToolSupport.ParseStringList(dependencies));
        }

        if (arguments.TryGetValue("inputs", out var inputs))
        {
            task.ReplaceInputs(TaskCrudToolSupport.ParseStringList(inputs));
        }

        if (arguments.TryGetValue("outputs", out var outputs))
        {
            task.ReplaceOutputs(TaskCrudToolSupport.ParseStringList(outputs));
        }

        if (arguments.TryGetValue("checklist_items", out var checklistItems))
        {
            task.ReplaceChecklistItems(TaskCrudToolSupport.ParseChecklist(checklistItems));
        }

        if (arguments.TryGetValue("result_notes", out var resultNotes))
        {
            task.ResultNotes = resultNotes?.ToString();
        }
    }

    private async Task<string?> SaveSnapshotOrStatusAsync(
        CodexSession session,
        CodexTask task,
        Dictionary<string, object?> arguments,
        CancellationToken ct)
    {
        if (taskListService == null)
        {
            return null;
        }

        if (arguments.ContainsKey("status"))
        {
            var errorMessage = arguments.GetValueOrDefault("error_message")?.ToString();
            return await taskListService.UpdateTaskStatusAsync(session, task.Id, task.Status, errorMessage, ct).ConfigureAwait(false);
        }

        return await taskListService.SaveSnapshotAsync(session, ct).ConfigureAwait(false);
    }
}
