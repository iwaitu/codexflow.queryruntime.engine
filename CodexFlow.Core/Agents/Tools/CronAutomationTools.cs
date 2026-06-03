using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

public sealed class CronCreateTool(
    ICronSchedulerService scheduler,
    ILogger<CronCreateTool> logger) : ICodexTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => "cron_create";

    public string Description => "创建一个定时 worker 计划。参数: session_id, cron, prompt, worker_type?, name?, task_id?, workspace_path?, timezone?, max_rounds?, enabled?。";

    public ToolCategory Category => ToolCategory.System;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 12_288);

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        var cron = arguments.GetValueOrDefault("cron")?.ToString();
        var prompt = arguments.GetValueOrDefault("prompt")?.ToString();

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return CodexToolResult.Error("Missing session_id.");
        }
        if (string.IsNullOrWhiteSpace(cron))
        {
            return CodexToolResult.Error("Missing cron.");
        }
        if (!CronExpressionValidator.IsValid(cron))
        {
            return CodexToolResult.Error("Invalid cron expression. Use a 5-field cron expression or @hourly/@daily/@weekly/@monthly.");
        }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return CodexToolResult.Error("Missing prompt.");
        }

        var workerType = arguments.GetValueOrDefault("worker_type")?.ToString();
        if (string.IsNullOrWhiteSpace(workerType))
        {
            workerType = "forge";
        }

        var maxRounds = TryGetInt(arguments.GetValueOrDefault("max_rounds"));
        var enabled = TryGetBool(arguments.GetValueOrDefault("enabled")) ?? true;
        var schedule = new CronScheduleDefinition
        {
            SessionId = sessionId,
            TaskId = arguments.GetValueOrDefault("task_id")?.ToString(),
            Name = arguments.GetValueOrDefault("name")?.ToString() ?? $"cron:{workerType}",
            Cron = cron.Trim(),
            WorkerType = workerType.Trim(),
            Prompt = prompt,
            WorkspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString(),
            TimeZone = arguments.GetValueOrDefault("timezone")?.ToString(),
            MaxRounds = maxRounds,
            Enabled = enabled
        };

        try
        {
            var stored = await scheduler.CreateAsync(schedule, ct).ConfigureAwait(false);
            var output = JsonSerializer.Serialize(new { created = true, schedule = stored }, JsonOptions);
            return CodexToolResult.Succeeded(output, new { created = true, schedule = stored }, summary: $"created cron: {stored.Id}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StructuredLog.Error(logger, ex, "cron_create failed for session {SessionId}", sessionId);
            return CodexToolResult.Error(ex.Message);
        }
    }

    private static int? TryGetInt(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is int intValue)
        {
            return intValue;
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static bool? TryGetBool(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is bool boolValue)
        {
            return boolValue;
        }

        return bool.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }
}

public sealed class CronDeleteTool(ICronSchedulerService scheduler) : ICodexTool
{
    public string Name => "cron_delete";

    public string Description => "删除一个定时 worker 计划。参数: cron_id 或 id。";

    public ToolCategory Category => ToolCategory.System;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: true,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 4_096);

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var cronId = arguments.GetValueOrDefault("cron_id")?.ToString()
            ?? arguments.GetValueOrDefault("id")?.ToString();
        if (string.IsNullOrWhiteSpace(cronId))
        {
            return CodexToolResult.Error("Missing cron_id.");
        }

        var deleted = await scheduler.DeleteAsync(cronId, ct).ConfigureAwait(false);
        if (!deleted)
        {
            return CodexToolResult.Error($"Cron schedule not found: {cronId}");
        }

        return CodexToolResult.Succeeded(
            $"CRON_DELETED\ncron_id: {cronId}",
            new { deleted = true, cronId },
            summary: $"deleted cron: {cronId}");
    }
}

public sealed class CronListTool(ICronSchedulerService scheduler) : ICodexTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => "cron_list";

    public string Description => "列出定时 worker 计划。参数: session_id?。";

    public ToolCategory Category => ToolCategory.Read;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: true,
        IsReadOnly: true,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 16_384);

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        var schedules = await scheduler.ListAsync(sessionId, ct).ConfigureAwait(false);
        var result = new { count = schedules.Count, schedules };
        var output = JsonSerializer.Serialize(result, JsonOptions);
        return CodexToolResult.Succeeded(output, result, summary: $"cron schedules: {schedules.Count}");
    }
}

internal static class CronExpressionValidator
{
    private static readonly HashSet<string> NamedExpressions = new(StringComparer.OrdinalIgnoreCase)
    {
        "@hourly",
        "@daily",
        "@weekly",
        "@monthly"
    };

    public static bool IsValid(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        var trimmed = expression.Trim();
        if (NamedExpressions.Contains(trimmed))
        {
            return true;
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 5 && parts.All(IsValidToken);
    }

    private static bool IsValidToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return token.All(ch => char.IsDigit(ch) || ch is '*' or '/' or '-' or ',');
    }
}
