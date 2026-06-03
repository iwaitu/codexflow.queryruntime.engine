using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Workers;
using System.Text;

namespace CodexFlow.Core.Constants;

public static class ToolCatalogPromptComposer
{
    /// <summary>
    /// 生产默认：追加极短 discovery hint，不注入全量目录。
    /// 模型通过 tool_search 按需发现和激活工具。
    /// </summary>
    public static string AppendDiscoveryHint(string basePrompt)
    {
        const string hint = """
## 工具发现
你当前拥有一组核心工具。如需更多能力（如 git 操作、智能修复、项目记忆等），请调用 `tool_search({"query": "关键词"})` 搜索并激活。
激活后的工具将在下一轮自动可用，无需再次搜索。
""";
        return string.IsNullOrWhiteSpace(basePrompt) ? hint : $"{basePrompt}\n\n{hint}";
    }

    /// <summary>
    /// 参考 Claude Code 的 system prompt 组装方式，按当前真实工具面
    /// 追加工具使用准则与 worker 边界说明，而不是只注入静态大段提示词。
    /// </summary>
    public static string AppendRuntimeToolGuidance(
        string basePrompt,
        IEnumerable<ICodexTool>? availableTools,
        WorkerRuntimeContext? workerContext = null)
    {
        var materializedTools = availableTools?.ToArray() ?? [];
        var toolNames = materializedTools.Select(static tool => tool.Name);
        var concurrentReadTools = materializedTools
            .Where(static tool =>
            {
                var metadata = ResolveMetadata(tool);
                return metadata.IsConcurrencySafe
                    && metadata.IsReadOnly
                    && !metadata.IsDestructive;
            })
            .Select(static tool => tool.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return AppendRuntimeToolGuidance(
            basePrompt,
            toolNames,
            workerContext,
            concurrentReadTools);
    }

    public static string AppendRuntimeToolGuidance(
        string basePrompt,
        IEnumerable<string>? availableToolNames,
        WorkerRuntimeContext? workerContext = null)
        => AppendRuntimeToolGuidance(basePrompt, availableToolNames, workerContext, []);

    /// <summary>
    /// 兼容保留：追加全量目录（仅用于 debug/admin/benchmark，生产不调用）。
    /// </summary>
    public static string AppendCatalog(string basePrompt, IToolRegistry? toolRegistry)
    {
        var catalogSection = BuildCatalogSection(toolRegistry);
        if (string.IsNullOrWhiteSpace(catalogSection))
        {
            return basePrompt;
        }

        return string.IsNullOrWhiteSpace(basePrompt)
            ? catalogSection
            : $"{basePrompt}\n\n{catalogSection}";
    }

    public static string BuildCatalogSection(IToolRegistry? toolRegistry)
    {
        if (toolRegistry == null)
        {
            return string.Empty;
        }

        var catalog = toolRegistry.GetCatalog();
        if (string.IsNullOrWhiteSpace(catalog))
        {
            return string.Empty;
        }

        return $$"""
## 工具目录（精简）
以下表格列出当前会话已注册的工具能力摘要。当前能否直接结构化调用，以本轮实际注入的工具列表为准。
如果某个工具只出现在目录中、但当前无法直接调用，请优先使用 `tool_search`（若当前可用）激活相关工具，并遵守当前阶段限制。

{{catalog}}
""";
    }

    private static string AppendRuntimeToolGuidance(
        string basePrompt,
        IEnumerable<string>? availableToolNames,
        WorkerRuntimeContext? workerContext,
        IReadOnlyCollection<string> concurrentReadTools)
    {
        var toolNames = new HashSet<string>(
            availableToolNames ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var sections = new List<string>();
        var discoveryHint = BuildConditionalDiscoveryHint(toolNames);
        if (!string.IsNullOrWhiteSpace(discoveryHint))
        {
            sections.Add(discoveryHint);
        }

        var usageSection = BuildToolUsageSection(toolNames, concurrentReadTools);
        if (!string.IsNullOrWhiteSpace(usageSection))
        {
            sections.Add(usageSection);
        }

        var workerSection = BuildWorkerSurfaceSection(workerContext);
        if (!string.IsNullOrWhiteSpace(workerSection))
        {
            sections.Add(workerSection);
        }

        if (sections.Count == 0)
        {
            return basePrompt;
        }

        var suffix = string.Join("\n\n", sections);
        return string.IsNullOrWhiteSpace(basePrompt)
            ? suffix
            : $"{basePrompt}\n\n{suffix}";
    }

    private static string? BuildConditionalDiscoveryHint(HashSet<string> toolNames)
    {
        if (!toolNames.Contains("tool_search"))
        {
            return null;
        }

        return """
## 工具发现
当当前工具面缺少所需能力时，优先调用 `tool_search({"query": "关键词"})` 搜索并激活相关 deferred 工具。
激活结果会在下一轮自动生效；不要在同一轮里反复猜测一个尚未注入的工具名。
""";
    }

    private static string? BuildToolUsageSection(
        HashSet<string> toolNames,
        IReadOnlyCollection<string> concurrentReadTools)
    {
        var items = new List<string>();

        var readTools = JoinToolNames(toolNames, "ivilson_read", "hs_read");
        if (!string.IsNullOrWhiteSpace(readTools))
        {
            items.Add($"读取文件优先使用 {readTools}，不要改用 shell 命令拼接 `cat` / `type` / `sed` 来绕过专用读工具。");
        }

        var searchTools = JoinToolNames(toolNames, "search_file_index", "search_in_files");
        if (!string.IsNullOrWhiteSpace(searchTools))
        {
            items.Add($"定位文件与搜索代码优先使用 {searchTools}，不要在项目里盲目遍历目录。");
            items.Add($"当用户点名具体模块、类名、文件名或符号时，先沿用用户原词调用 {searchTools}；不要一开始就把问题扩写成未经确认的同义词、架构术语或自造命名。");
        }

        var discoveryTools = JoinToolNames(toolNames, "search_file_index", "ivilson_ls", "list_workspace", "search_in_files");
        if (!string.IsNullOrWhiteSpace(discoveryTools))
        {
            items.Add($"进入陌生仓库时，先从 `.` 或已确认存在的真实目录开始使用 {discoveryTools}；不要预设一定存在 `src`、`app`、`lib`、`server` 这类目录名。");
        }

        if ((!string.IsNullOrWhiteSpace(readTools) || !string.IsNullOrWhiteSpace(searchTools)) &&
            toolNames.Contains("analyze_project"))
        {
            items.Add("`analyze_project` 通常只需要调用一次来建立全局指纹。拿到项目摘要、索引或真实路径后，下一步优先直接读取这些具体文件补证据，而不是继续做宽泛命名猜测。");
        }

        if (!string.IsNullOrWhiteSpace(readTools) && !string.IsNullOrWhiteSpace(searchTools))
        {
            items.Add($"一旦 {searchTools} 或项目分析已经给出真实文件路径，下一步优先直接使用 {readTools} 读取这些文件。仅凭命中计数、文件名或目录结构不足以下架构结论；对某个系统下结论前，至少读取对应源码文件。");
        }

        var writeTools = JoinToolNames(toolNames, "write_file", "edit_file", "apply_patch", "ivilson_smart_patch", "hs_write");
        if (!string.IsNullOrWhiteSpace(writeTools))
        {
            items.Add($"修改代码优先使用 {writeTools} 这类专用写工具；只有当专用工具无法表达目标时，才考虑退回终端命令。");
        }

        var shellTools = JoinToolNames(toolNames, "exec_cmd", "run_command", "execute_code", "exec_code");
        if (!string.IsNullOrWhiteSpace(shellTools) &&
            (!string.IsNullOrWhiteSpace(readTools) || !string.IsNullOrWhiteSpace(searchTools) || !string.IsNullOrWhiteSpace(writeTools)))
        {
            items.Add($"将 {shellTools} 保留给构建、测试、Git 或其他必须依赖终端语义的操作；不要用 shell 代替现成的读、搜、写工具。");
        }

        if (concurrentReadTools.Count > 1)
        {
            items.Add($"如果多个只读/分析动作彼此独立，尽量在同一轮一起调用（例如 {string.Join("、", concurrentReadTools.Take(4))}）；有前后依赖时再串行执行。");
        }
        else if (!string.IsNullOrWhiteSpace(readTools) || !string.IsNullOrWhiteSpace(searchTools))
        {
            items.Add("如果一轮里需要多个独立的读取或搜索动作，优先直接成组发起，减少无效往返。");
        }

        if (toolNames.Count > 0)
        {
            items.Add("如果某个工具调用被拒绝、受限或当前轮不可用，不要机械重复完全相同的调用；先调整方案、补充上下文，或等待下一轮的新工具面。");
        }

        if (items.Count == 0)
        {
            return null;
        }

        return BuildSection("工具使用准则", items);
    }

    private static string? BuildWorkerSurfaceSection(WorkerRuntimeContext? workerContext)
    {
        if (workerContext == null)
        {
            return null;
        }

        var items = new List<string>
        {
            $"当前 worker：`{workerContext.DisplayName}`（`{workerContext.WorkerType}`）。",
            $"允许工具分类：{string.Join("、", workerContext.AllowedToolCategories.Select(FormatToolCategory))}。"
        };

        var isReadOnlySurface = !workerContext.AllowedToolCategories.Contains(ToolCategory.Forge)
            && !workerContext.AllowedToolNames.Any(IsWriteLikeToolName);
        if (isReadOnlySurface)
        {
            items.Add("当前工具面是只读/分析优先 surface。不要尝试通过 `exec_cmd`、`run_command` 或其他旁路方式绕过写入限制。");
        }

        if (workerContext.OutputContract == WorkerOutputContract.VerificationReportEnvelope)
        {
            items.Add("当前 worker 的最终输出必须满足验证型输出契约，不要退回普通聊天式总结。");
        }

        return BuildSection("当前工具面约束", items);
    }

    private static string BuildSection(string title, IEnumerable<string> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {title}");
        foreach (var item in items.Where(static item => !string.IsNullOrWhiteSpace(item)))
        {
            sb.Append("- ");
            sb.AppendLine(item);
        }

        return sb.ToString().TrimEnd();
    }

    private static string JoinToolNames(HashSet<string> toolNames, params string[] preferredNames)
        => string.Join(" / ",
            preferredNames
                .Where(toolNames.Contains)
                .Select(static name => $"`{name}`"));

    private static ToolExecutionMetadata ResolveMetadata(ICodexTool tool)
        => tool.Metadata ?? ToolExecutionMetadata.ForCategory(tool.Category);

    private static bool IsWriteLikeToolName(string toolName)
        => toolName.Equals("write_file", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("edit_file", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("apply_patch", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("ivilson_smart_patch", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("hs_write", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("delete_file", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("create_directory", StringComparison.OrdinalIgnoreCase);

    private static string FormatToolCategory(ToolCategory category)
        => category switch
        {
            ToolCategory.Read => "Read",
            ToolCategory.Forge => "Forge",
            ToolCategory.Analysis => "Analysis",
            ToolCategory.Planning => "Planning",
            ToolCategory.Sentry => "Sentry",
            ToolCategory.System => "System",
            _ => category.ToString()
        };
}
