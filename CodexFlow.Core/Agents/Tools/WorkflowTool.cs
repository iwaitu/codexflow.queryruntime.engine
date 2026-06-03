using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

public sealed class WorkflowTool(
    ISkillScriptRunner? scriptRunner,
    IWorkflowAuditStore auditStore) : ICodexTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => "workflow";

    public string Description => "运行并审计脚本工作流。参数: action(run|get|list), name?, skill_name?, script_path?, args?, workflow_id?, session_id?, user_id?, workspace_path?。";

    public ToolCategory Category => ToolCategory.System;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 20_000);

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        var action = arguments.GetValueOrDefault("action")?.ToString();
        return (string.IsNullOrWhiteSpace(action) ? "run" : action.Trim().ToLowerInvariant()) switch
        {
            "run" => await RunAsync(arguments, ct).ConfigureAwait(false),
            "get" => await GetAsync(arguments, ct).ConfigureAwait(false),
            "list" => await ListAsync(arguments, ct).ConfigureAwait(false),
            _ => CodexToolResult.Error("Unsupported workflow action. Use run, get, or list.")
        };
    }

    private async Task<CodexToolResult> RunAsync(Dictionary<string, object?> arguments, CancellationToken ct)
    {
        if (scriptRunner == null)
        {
            return CodexToolResult.Error("workflow script runner is unavailable in this runtime.");
        }

        var scriptPath = arguments.GetValueOrDefault("script_path")?.ToString();
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            return CodexToolResult.Error("Missing script_path.");
        }

        scriptPath = NormalizeScriptPath(scriptPath);
        if (!IsSafeRelativePath(scriptPath))
        {
            return CodexToolResult.Error("script_path must be a safe relative path inside the workflow skill.");
        }

        var skillName = arguments.GetValueOrDefault("skill_name")?.ToString();
        if (string.IsNullOrWhiteSpace(skillName))
        {
            skillName = "workflow";
        }

        var args = ReadStringArray(arguments.GetValueOrDefault("args"));
        var name = arguments.GetValueOrDefault("name")?.ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileNameWithoutExtension(scriptPath);
        }

        var started = await auditStore.StartAsync(new WorkflowRunRecord
        {
            Name = name,
            SkillName = skillName,
            ScriptPath = scriptPath,
            Args = args,
            SessionId = arguments.GetValueOrDefault("session_id")?.ToString(),
            UserId = arguments.GetValueOrDefault("user_id")?.ToString(),
            WorkspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString()
        }, ct).ConfigureAwait(false);

        try
        {
            var output = await scriptRunner.RunAsync(skillName, scriptPath, args, ct).ConfigureAwait(false);
            var failed = LooksLikeRunnerFailure(output);
            var completed = await auditStore.CompleteAsync(
                started.Id,
                failed ? "failed" : "succeeded",
                failed ? null : output,
                failed ? output : null,
                ct).ConfigureAwait(false);

            var payload = new
            {
                workflow_id = started.Id,
                completed = !failed,
                run = completed ?? started,
                output
            };

            return failed
                ? CodexToolResult.Error(
                    JsonSerializer.Serialize(payload, JsonOptions),
                    payload,
                    summary: $"workflow failed: {started.Id}")
                : CodexToolResult.Succeeded(
                    JsonSerializer.Serialize(payload, JsonOptions),
                    payload,
                    summary: $"workflow completed: {started.Id}");
        }
        catch (OperationCanceledException)
        {
            await auditStore.CompleteAsync(started.Id, "cancelled", errorMessage: "cancelled", ct: CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var completed = await auditStore.CompleteAsync(started.Id, "failed", errorMessage: ex.Message, ct: ct)
                .ConfigureAwait(false);
            var payload = new
            {
                workflow_id = started.Id,
                completed = false,
                run = completed ?? started,
                error = ex.Message
            };
            return CodexToolResult.Error(
                JsonSerializer.Serialize(payload, JsonOptions),
                payload,
                summary: $"workflow failed: {started.Id}");
        }
    }

    private async Task<CodexToolResult> GetAsync(Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var workflowId = arguments.GetValueOrDefault("workflow_id")?.ToString()
            ?? arguments.GetValueOrDefault("id")?.ToString();
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            return CodexToolResult.Error("Missing workflow_id.");
        }

        var run = await auditStore.GetAsync(workflowId, ct).ConfigureAwait(false);
        if (run == null)
        {
            return CodexToolResult.Error($"workflow not found: {workflowId}");
        }

        return CodexToolResult.Succeeded(
            JsonSerializer.Serialize(run, JsonOptions),
            run,
            summary: $"workflow read: {run.Id}");
    }

    private async Task<CodexToolResult> ListAsync(Dictionary<string, object?> arguments, CancellationToken ct)
    {
        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        var name = arguments.GetValueOrDefault("name")?.ToString();
        var runs = await auditStore.ListAsync(sessionId, name, ct).ConfigureAwait(false);
        var payload = new { count = runs.Count, runs };

        return CodexToolResult.Succeeded(
            JsonSerializer.Serialize(payload, JsonOptions),
            payload,
            summary: $"workflows: {runs.Count}");
    }

    private static string NormalizeScriptPath(string scriptPath)
        => scriptPath.Replace('\\', '/').Trim();

    private static bool IsSafeRelativePath(string scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath) ||
            Path.IsPathFullyQualified(scriptPath) ||
            Path.IsPathRooted(scriptPath) ||
            scriptPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }

        return !scriptPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(segment => segment == "." || segment == "..");
    }

    private static bool LooksLikeRunnerFailure(string output)
        => output.StartsWith("ExitCode:", StringComparison.OrdinalIgnoreCase) ||
           output.StartsWith("Failed to start", StringComparison.OrdinalIgnoreCase);

    private static string[] ReadStringArray(object? raw)
    {
        if (raw == null)
        {
            return [];
        }

        if (raw is string text)
        {
            return string.IsNullOrWhiteSpace(text)
                ? []
                : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (raw is IEnumerable<string> strings)
        {
            return strings
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .ToArray();
        }

        if (raw is Newtonsoft.Json.Linq.JArray array)
        {
            return array
                .Values<string?>()
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .ToArray();
        }

        if (raw is JsonElement { ValueKind: JsonValueKind.Array } jsonArray)
        {
            return jsonArray
                .EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .ToArray();
        }

        return [];
    }
}
