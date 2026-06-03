using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// tool_search 元工具：允许 LLM 通过关键词搜索并激活 deferred 工具
///
/// 使用示例：
/// - tool_search("git") → 返回所有 git 相关工具并激活它们
/// - tool_search("slack notification") → 返回通知相关工具
/// </summary>
public class ToolSearchMetaTool : ICodexTool
{
    private readonly IToolRegistry _registry;
    private readonly ILogger<ToolSearchMetaTool> _logger;

    public string Name => "tool_search";
    public string Description => "搜索并激活延迟加载的工具。传入关键词（如 \"git\"、\"patch\"、\"memory\"）来查找相关工具。匹配的工具会被自动激活，并在后续轮次中可调用。";
    public ToolCategory Category => ToolCategory.System;
    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 12_288);
    public IReadOnlyList<int> AllowedStages => Array.Empty<int>(); // 所有阶段都可用

    public ToolSearchMetaTool(IToolRegistry registry, ILogger<ToolSearchMetaTool> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var query = arguments.TryGetValue("query", out var queryValue)
            ? queryValue?.ToString() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(CodexToolResult.Error("查询参数不能为空。请提供搜索关键词，例如：tool_search({\"query\": \"git\"})"));
        }

        var matches = _registry.SearchTools(query).ToList();
        var activeToolNames = _registry.GetActiveToolNames();

        if (matches.Count == 0)
        {
            return Task.FromResult(new CodexToolResult
            {
                Status = ToolResultStatus.Success,
                Output = $"未找到匹配 \"{query}\" 的工具。尝试更宽泛的关键词。",
                Metadata = new { found = 0, tools = Array.Empty<object>(), message = $"No tools matched '{query}'" }
            });
        }

        // 激活所有匹配的工具，并返回足够的选择上下文，避免模型只凭名称猜测工具风险。
        var activated = new List<object>();
        foreach (var tool in matches)
        {
            var wasAlreadyAvailable = activeToolNames.Contains(tool.Name);
            var wasActivated = _registry.ActivateTool(tool.Name);
            var searchMetadata = BuildSearchMetadata(tool);

            activated.Add(new
            {
                name = tool.Name,
                description = tool.Description,
                category = tool.Category.ToString(),
                activated = wasActivated,
                available_now = wasAlreadyAvailable || wasActivated,
                activation_reason = BuildActivationReason(tool.Name, query, wasAlreadyAvailable, wasActivated),
                tags = searchMetadata.Tags,
                surface = searchMetadata.Surface,
                risk = searchMetadata.Risk,
                examples = searchMetadata.Examples
            });

            if (wasActivated)
            {
                StructuredLog.Information(_logger, "Tool search matched and activated: {ToolName}", tool.Name);
            }
        }

        var result = new
        {
            found = activated.Count,
            query,
            tools = activated
        };

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        return Task.FromResult(new CodexToolResult
        {
            Status = ToolResultStatus.Success,
            Output = json,
            Metadata = result
        });
    }

    private static ToolSearchResultMetadata BuildSearchMetadata(ICodexTool tool)
    {
        var metadata = tool.Metadata ?? ToolExecutionMetadata.ForCategory(tool.Category);
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            tool.Category.ToString().ToLowerInvariant()
        };

        foreach (var token in tool.Name.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            tags.Add(token.ToLowerInvariant());
        }

        if (metadata.IsReadOnly)
        {
            tags.Add("read-only");
        }
        if (metadata.IsDestructive)
        {
            tags.Add("destructive");
        }
        if (metadata.InterruptBehavior == ToolInterruptBehavior.RequiresConfirmation)
        {
            tags.Add("confirmation");
        }

        var curated = GetCuratedMetadata(tool.Name);
        foreach (var tag in curated.Tags)
        {
            tags.Add(tag);
        }

        var surface = curated.Surface.Count > 0 ? curated.Surface : InferSurface(tool);
        var examples = curated.Examples.Count > 0 ? curated.Examples : BuildDefaultExamples(tool);

        var risk = string.IsNullOrWhiteSpace(curated.Risk) ? InferRisk(metadata) : curated.Risk;

        return new ToolSearchResultMetadata(
            tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToArray(),
            surface.ToArray(),
            risk,
            examples.ToArray());
    }

    private static ToolSearchResultMetadata GetCuratedMetadata(string toolName)
    {
        return toolName switch
        {
            "notebook_edit" => new(
                ["notebook", "ipynb", "jupyter", "cell", "diff-preview"],
                ["main", "forge-worker"],
                "medium",
                [
                    "Replace a notebook cell with dry_run=true before writing.",
                    "Insert a markdown or code cell at a specific index."
                ]),
            "list_mcp_resources" => new(
                ["mcp", "resource", "context", "list"],
                ["main", "worker"],
                "low",
                [
                    "Find available MCP resources before reading one.",
                    "Filter resources by server or path pattern."
                ]),
            "read_mcp_resource" => new(
                ["mcp", "resource", "context", "read"],
                ["main", "worker"],
                "low",
                [
                    "Read a selected mcp:// resource URI.",
                    "Limit large resource output with max_chars."
                ]),
            "skill" => new(
                ["skill", "script", "local-automation", "workflow"],
                ["main", "worker"],
                "medium",
                [
                    "List installed skills.",
                    "Read a skill file or run a skill script inside the skill root."
                ]),
            "enter_worktree" => new(
                ["worktree", "workspace", "repo", "switch", "session"],
                ["main", "coordinator"],
                "medium",
                [
                    "Switch the current session workspace to a repo subdirectory.",
                    "Enter an external Git worktree with allow_external=true."
                ]),
            "exit_worktree" => new(
                ["worktree", "workspace", "restore", "session"],
                ["main", "coordinator"],
                "low",
                [
                    "Restore the session workspace saved by enter_worktree."
                ]),
            "fetch_webpage" => new(
                ["web", "http", "url", "fetch", "research"],
                ["main", "explore-worker", "plan-worker", "verify-worker"],
                "low",
                [
                    "Fetch the content of a known URL.",
                    "Use with web_search when research needs source details."
                ]),
            "web_search" => new(
                ["web", "search", "research", "current-info"],
                ["main", "explore-worker", "plan-worker", "verify-worker"],
                "low",
                [
                    "Search the web for current technical context.",
                    "Find candidate pages before fetch_webpage."
                ]),
            "apply_patch" => new(
                ["patch", "edit", "diff", "code-change"],
                ["forge-worker"],
                "high",
                [
                    "Apply a focused source patch.",
                    "Use after reading the target file and deciding exact changes."
                ]),
            "cron_create" => new(
                ["cron", "automation", "schedule", "worker"],
                ["main", "coordinator"],
                "medium",
                [
                    "Create a scheduled worker from a cron expression and prompt.",
                    "Use for recurring coordinator-owned automation."
                ]),
            "cron_delete" => new(
                ["cron", "automation", "schedule", "delete"],
                ["main", "coordinator"],
                "high",
                [
                    "Delete a scheduled worker by cron_id."
                ]),
            "cron_list" => new(
                ["cron", "automation", "schedule", "list"],
                ["main", "coordinator"],
                "low",
                [
                    "List scheduled workers for a session."
                ]),
            "monitor" => new(
                ["monitor", "status", "worker", "job", "session"],
                ["main", "coordinator"],
                "low",
                [
                    "Read a worker/job status snapshot by job_id.",
                    "Summarize a session and its active workers by session_id."
                ]),
            "remote_trigger" => new(
                ["remote", "trigger", "webhook", "event", "automation"],
                ["main", "coordinator"],
                "medium",
                [
                    "Record an external event payload.",
                    "Optionally dispatch a worker from a trusted event."
                ]),
            "push_notification" => new(
                ["push", "notification", "signalr", "user", "message"],
                ["main", "coordinator"],
                "medium",
                [
                    "Push a notification to a user through the configured notification channels.",
                    "Use after long-running automation completes or needs user attention."
                ]),
            "workflow" => new(
                ["workflow", "script", "automation", "audit"],
                ["main", "coordinator"],
                "medium",
                [
                    "Run an auditable script workflow from a configured skill.",
                    "List or read workflow audit records by session_id or workflow_id."
                ]),
            _ => ToolSearchResultMetadata.Empty
        };
    }

    private static IReadOnlyList<string> InferSurface(ICodexTool tool)
    {
        return tool.Category switch
        {
            ToolCategory.Forge => ["main", "forge-worker"],
            ToolCategory.Planning => ["main", "coordinator", "plan-worker"],
            ToolCategory.Read or ToolCategory.Analysis => ["main", "worker"],
            ToolCategory.Sentry => ["main", "verify-worker"],
            ToolCategory.System => ["main", "coordinator"],
            _ => ["main"]
        };
    }

    private static string InferRisk(ToolExecutionMetadata metadata)
    {
        if (metadata.IsDestructive)
        {
            return "high";
        }

        if (!metadata.IsReadOnly || metadata.InterruptBehavior == ToolInterruptBehavior.RequiresConfirmation)
        {
            return "medium";
        }

        return "low";
    }

    private static IReadOnlyList<string> BuildDefaultExamples(ICodexTool tool)
    {
        return [$"Use {tool.Name} when the task matches: {tool.Description}"];
    }

    private static string BuildActivationReason(string toolName, string query, bool wasAlreadyAvailable, bool wasActivated)
    {
        var availability = wasAlreadyAvailable
            ? "already_active"
            : wasActivated
                ? "activated_deferred"
                : "matched_active";

        return $"{availability}: '{toolName}' matched query '{query}'.";
    }

    private sealed record ToolSearchResultMetadata(
        IReadOnlyList<string> Tags,
        IReadOnlyList<string> Surface,
        string Risk,
        IReadOnlyList<string> Examples)
    {
        public static ToolSearchResultMetadata Empty { get; } = new([], [], string.Empty, []);
    }
}
