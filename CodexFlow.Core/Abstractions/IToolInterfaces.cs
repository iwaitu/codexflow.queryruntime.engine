using CodexFlow.Core.Models;
using CodexFlow.Core.Runtime;

namespace CodexFlow.Core.Abstractions;

public enum ToolCategory
{
    Read,       // 只读：ls, cat, grep
    Forge,      // 修改：write, patch, delete
    Analysis,   // 分析：analyze_csharp, build_graph
    Planning,   // 规划：create_session_plan
    Sentry,     // 验证：run_test, validate
    System      // 系统：revert, checkpoint
}

/// <summary>
/// 工具加载策略
/// </summary>
public enum ToolLoading
{
    AlwaysOn,   // 总是加载，每次请求都注入
    Deferred    // 延迟加载，需通过 tool_search 激活
}

/// <summary>
/// 工具暴露入口：不同入口只注入各自最小工具集
/// </summary>
public enum ToolSurface
{
    Gateway,     // Gateway 自动流程：git_clone, analyze_project, create_session_plan, ivilson_ls, ivilson_read, search_in_files, start_next_task, tool_search
    Controller,  // Controller 手动聊天：ivilson_ls, ivilson_read, search_in_files, search_file_index, tool_search + 按需激活
    Kernel,      // Kernel 执行闭环：文件读写 + 执行 + 测试 + tool_search
    Coordinator, // Coordinator 调度面：worker 调度、worker 输出、计划确认、用户提问、汇总输出
    Validator    // Validator：极少只读工具
}

/// <summary>
/// 工具中断行为语义：用于 runtime 调度与审计提示。
/// </summary>
public enum ToolInterruptBehavior
{
    None,
    CancelSafe,
    RequiresConfirmation
}

/// <summary>
/// 工具执行元数据：声明并发/只读/破坏性等关键语义。
/// </summary>
public sealed record ToolExecutionMetadata(
    bool IsConcurrencySafe,
    bool IsReadOnly,
    bool IsDestructive,
    ToolInterruptBehavior InterruptBehavior,
    int? ResultSizeSoftLimitChars = null)
{
    private static readonly ToolExecutionMetadata ReadMetadata = new(
        IsConcurrencySafe: true,
        IsReadOnly: true,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 8_192);

    private static readonly ToolExecutionMetadata AnalysisMetadata = new(
        IsConcurrencySafe: true,
        IsReadOnly: true,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 12_288);

    private static readonly ToolExecutionMetadata PlanningMetadata = new(
        IsConcurrencySafe: false,
        IsReadOnly: true,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 16_384);

    private static readonly ToolExecutionMetadata SystemMetadata = new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 12_288);

    private static readonly ToolExecutionMetadata ForgeMetadata = new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: true,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 10_240);

    private static readonly ToolExecutionMetadata SentryMetadata = new(
        IsConcurrencySafe: true,
        IsReadOnly: true,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 16_384);

    private static readonly ToolExecutionMetadata DefaultMetadata = new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.None);

    public static ToolExecutionMetadata ForCategory(ToolCategory category)
    {
        return category switch
        {
            ToolCategory.Read => ReadMetadata,
            ToolCategory.Analysis => AnalysisMetadata,
            ToolCategory.Planning => PlanningMetadata,
            ToolCategory.System => SystemMetadata,
            ToolCategory.Forge => ForgeMetadata,
            ToolCategory.Sentry => SentryMetadata,
            _ => DefaultMetadata
        };
    }
}

/// <summary>
/// 工具输入校验结果。
/// </summary>
public sealed record CodexToolValidationResult(
    bool IsValid,
    string? Message = null,
    object? Metadata = null,
    string? SystemHint = null)
{
    public static CodexToolValidationResult Valid() => new(true);

    public static CodexToolValidationResult Invalid(
        string message,
        object? metadata = null,
        string? systemHint = null)
        => new(false, message, metadata, systemHint);
}

/// <summary>
/// 工具权限检查结果。
/// </summary>
public sealed record CodexToolPermissionResult(
    bool IsAllowed,
    string? Message = null,
    object? Metadata = null,
    string? SystemHint = null)
{
    public static CodexToolPermissionResult Allowed() => new(true);

    public static CodexToolPermissionResult Denied(
        string message,
        object? metadata = null,
        string? systemHint = null)
        => new(false, message, metadata, systemHint);
}

public interface IToolRegistry
{
    void RegisterTool(ICodexTool tool);
    void RegisterTool(ICodexTool tool, ToolLoading loading);
    IEnumerable<ICodexTool> GetAvailableTools(CodexSession session);

    /// <summary>
    /// 获取所有 always-on 工具（包含已激活的 deferred 工具）
    /// </summary>
    IEnumerable<ICodexTool> GetActiveTools();

    /// <summary>
    /// 仅获取 always-on 工具（不包括 deferred 工具）
    /// </summary>
    IEnumerable<ICodexTool> GetAlwaysOnTools();

    /// <summary>
    /// 搜索相关工具（按名称或描述匹配）。
    /// Always-on 工具会作为“已可用”结果返回；deferred 工具可由 tool_search 激活。
    /// </summary>
    IEnumerable<ICodexTool> SearchTools(string query);

    /// <summary>
    /// 激活指定的 deferred 工具
    /// </summary>
    bool ActivateTool(string toolName);

    /// <summary>
    /// 生成精简的工具目录（用于 system prompt）
    /// </summary>
    string GetCatalog();

    /// <summary>
    /// 返回当前所有 active 工具的名称集合（AlwaysOn + 已激活的 Deferred）。
    /// 用于多轮对话中每轮重建 ChatOptions.Tools。
    /// </summary>
    IReadOnlySet<string> GetActiveToolNames();
}

public interface ICodexTool
{
    string Name { get; }
    string Description { get; }
    ToolCategory Category { get; }
    IReadOnlyList<int> AllowedStages { get; }
    ToolExecutionMetadata Metadata => ToolExecutionMetadata.ForCategory(Category);
    ValueTask<CodexToolValidationResult> ValidateInputAsync(
        Dictionary<string, object?> arguments,
        CancellationToken ct = default)
        => ValueTask.FromResult(CodexToolValidationResult.Valid());
    ValueTask<CodexToolPermissionResult> CheckPermissionsAsync(
        Dictionary<string, object?> arguments,
        QueryRuntimeRequest request,
        QueryRuntimeState? state = null,
        CancellationToken ct = default)
        => ValueTask.FromResult(CodexToolPermissionResult.Allowed());
    Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default);
}
