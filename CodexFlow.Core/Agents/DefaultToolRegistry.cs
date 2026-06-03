using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Planning.Artifacts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Agents;

/// <summary>
/// 工具定义数据结构（内部使用）
/// </summary>
internal sealed class ToolDefinition
{
    public ICodexTool Tool { get; set; } = null!;
    public ToolLoading Loading { get; set; }
}

public class DefaultToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, ToolDefinition> _allTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _activatedDeferredTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<DefaultToolRegistry> _logger;
    private readonly PlanningOptions _planningOptions;
    private readonly int _catalogLimit;

    public DefaultToolRegistry(
        ILogger<DefaultToolRegistry> logger,
        int catalogLimit = 50,
        IOptions<PlanningOptions>? planningOptions = null)
    {
        _logger = logger;
        _planningOptions = planningOptions?.Value ?? new PlanningOptions();
        _catalogLimit = catalogLimit;
    }

    public void RegisterTool(ICodexTool tool)
    {
        RegisterTool(tool, ToolLoading.AlwaysOn);
    }

    public void RegisterTool(ICodexTool tool, ToolLoading loading)
    {
        if (tool == null) return;

        var def = new ToolDefinition { Tool = tool, Loading = loading };
        if (_allTools.TryAdd(tool.Name, def))
        {
            StructuredLog.Debug(_logger, "Tool registered: {ToolName} ({Loading})", tool.Name, loading);

            // 如果是 deferred 工具，预先注册到 activated 字典但标记为未激活
            if (loading == ToolLoading.Deferred)
            {
                _activatedDeferredTools.TryAdd(tool.Name, false);
            }
        }
    }

    public IEnumerable<ICodexTool> GetAvailableTools(CodexSession session)
    {
        if (session == null) return Enumerable.Empty<ICodexTool>();

        // 返回所有 active 工具（always-on + 已激活的 deferred），并根据 stage 过滤
        return GetActiveTools()
            .Where(t => t.AllowedStages == null || t.AllowedStages.Count == 0 || t.AllowedStages.Contains(session.CurrentStage))
            .Where(t => IsAllowedBySessionMode(session, t));
    }

    public IEnumerable<ICodexTool> GetAlwaysOnTools()
    {
        return _allTools.Values
            .Where(d => d.Loading == ToolLoading.AlwaysOn)
            .Select(d => d.Tool);
    }

    public IEnumerable<ICodexTool> GetActiveTools()
    {
        return _allTools.Values.Where(d => IsToolActive(d)).Select(d => d.Tool);
    }

    private bool IsToolActive(ToolDefinition def)
    {
        if (def.Loading == ToolLoading.AlwaysOn)
            return true;

        // Deferred 工具需要被激活才可用
        return _activatedDeferredTools.TryGetValue(def.Tool.Name, out var activated) && activated;
    }

    private bool IsAllowedBySessionMode(CodexSession session, ICodexTool tool)
    {
        if (!_planningOptions.PlanPermissionModeEnabled)
        {
            return true;
        }

        if (!session.Metadata.TryGetValue("PlanModeActive", out var activeRaw) ||
            !bool.TryParse(activeRaw, out var active) ||
            !active)
        {
            return true;
        }

        if (tool.Name is "write_plan_file" or "read_plan_file" or "request_plan_approval" or "approve_plan" or "reject_plan" or "project_plan_to_tasks" or "plan_diff")
        {
            return true;
        }

        if (tool.Category is ToolCategory.Read or ToolCategory.Analysis or ToolCategory.Planning or ToolCategory.System)
        {
            return !tool.Metadata.IsDestructive;
        }

        return tool.Metadata.IsReadOnly && !tool.Metadata.IsDestructive;
    }

    public IEnumerable<ICodexTool> SearchTools(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Enumerable.Empty<ICodexTool>();

        var matches = SearchToolsByPattern(query).ToList();
        if (matches.Count > 0)
        {
            return OrderToolMatches(matches).Select(d => d.Tool);
        }

        return SearchToolsByTokens(query)
            .Select(match => match.Definition.Tool);
    }

    private List<ToolDefinition> SearchToolsByPattern(string query)
    {
        try
        {
            // 尝试用正则表达式匹配
            var pattern = new Regex(query, RegexOptions.IgnoreCase);
            return _allTools.Values
                .Where(d => pattern.IsMatch(d.Tool.Name) || pattern.IsMatch(d.Tool.Description))
                .ToList();
        }
        catch (ArgumentException)
        {
            // 正则无效时退化为简单字符串匹配
            return _allTools.Values
                .Where(d => d.Tool.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           d.Tool.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private List<(ToolDefinition Definition, int Score)> SearchToolsByTokens(string query)
    {
        var tokens = Regex.Matches(query, @"[\p{L}\p{N}_-]{2,}")
            .Select(match => match.Value)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (tokens.Length == 0)
        {
            return [];
        }

        return _allTools.Values
            .Select(def =>
            {
                var searchable = $"{def.Tool.Name} {def.Tool.Description}";
                var score = tokens.Count(token =>
                    searchable.Contains(token, StringComparison.OrdinalIgnoreCase));
                return (Definition: def, Score: score);
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Definition.Loading == ToolLoading.AlwaysOn ? 0 : IsToolActive(match.Definition) ? 1 : 2)
            .ThenBy(match => match.Definition.Tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IOrderedEnumerable<ToolDefinition> OrderToolMatches(IEnumerable<ToolDefinition> matches)
        => matches
            .OrderBy(d => d.Loading == ToolLoading.AlwaysOn ? 0 : IsToolActive(d) ? 1 : 2)
            .ThenBy(d => d.Tool.Name, StringComparer.OrdinalIgnoreCase);

    public bool ActivateTool(string toolName)
    {
        if (_allTools.TryGetValue(toolName, out var def) && def.Loading == ToolLoading.Deferred)
        {
            _activatedDeferredTools[toolName] = true;
            StructuredLog.Information(_logger, "Tool activated: {ToolName}", toolName);
            return true;
        }
        return false;
    }

    public IReadOnlySet<string> GetActiveToolNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _allTools)
        {
            if (IsToolActive(kvp.Value))
            {
                names.Add(kvp.Key);
            }
        }
        return names;
    }

    public string GetCatalog()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Tool | Description | ReadOnly | ConcurrencySafe | Destructive | Interrupt | ResultSoftLimit |");
        sb.AppendLine("|------|-------------|----------|-----------------|-------------|-----------|-----------------|");

        foreach (var def in _allTools.Values
            .OrderBy(d => d.Loading == ToolLoading.AlwaysOn ? 0 : 1)
            .ThenBy(d => d.Tool.Name, StringComparer.OrdinalIgnoreCase)
            .Take(_catalogLimit))
        {
            var desc = EscapeMarkdown(def.Tool.Description);
            var metadata = def.Tool.Metadata ?? ToolExecutionMetadata.ForCategory(def.Tool.Category);
            var resultLimit = metadata.ResultSizeSoftLimitChars.HasValue
                ? metadata.ResultSizeSoftLimitChars.Value.ToString()
                : "-";
            sb.Append($"| {def.Tool.Name} | {desc} | {ToYesNo(metadata.IsReadOnly)} | {ToYesNo(metadata.IsConcurrencySafe)} | {ToYesNo(metadata.IsDestructive)} | {metadata.InterruptBehavior} | {resultLimit} |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string EscapeMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // 移除换行符并替换特殊字符
        var cleaned = text.Replace("\n", " ").Replace("\r", " ").Replace("|", "\\|").Trim();
        return cleaned.Length > 100 ? cleaned.Substring(0, 100) : cleaned;
    }

    private static string ToYesNo(bool value) => value ? "yes" : "no";
}

/// <summary>
/// 一个便捷的工具包装器，允许直接将 Lambda 表达式转换为 ICodexTool
/// </summary>
public class DelegateCodexTool : ICodexTool
{
    private readonly Func<Dictionary<string, object?>, CancellationToken, Task<CodexToolResult>> _executionLogic;

    public string Name { get; }
    public string Description { get; }
    public ToolCategory Category { get; }
    public IReadOnlyList<int> AllowedStages { get; }
    public ToolExecutionMetadata Metadata { get; }

    public DelegateCodexTool(
        string name,
        string description,
        ToolCategory category,
        IReadOnlyList<int> allowedStages,
        Func<Dictionary<string, object?>, CancellationToken, Task<CodexToolResult>> logic)
        : this(name, description, category, allowedStages, metadata: null, logic)
    {
    }

    public DelegateCodexTool(
        string name,
        string description,
        ToolCategory category,
        IReadOnlyList<int> allowedStages,
        ToolExecutionMetadata? metadata,
        Func<Dictionary<string, object?>, CancellationToken, Task<CodexToolResult>> logic)
    {
        Name = name;
        Description = description;
        Category = category;
        AllowedStages = allowedStages;
        Metadata = metadata ?? ToolExecutionMetadata.ForCategory(category);
        _executionLogic = logic;
    }

    public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        return _executionLogic(arguments, ct);
    }
}
