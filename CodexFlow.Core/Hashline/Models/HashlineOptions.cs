using System.Collections.Generic;

namespace CodexFlow.Core.Hashline.Models;

/// <summary>
/// Hashline 配置选项。
/// </summary>
public sealed class HashlineOptions
{
    /// <summary>
    /// 允许访问的根目录列表（白名单）。
    /// 路径安全模型：
    /// - 工具调用时会传入当前 workspace/project root 作为动态 allowed root
    /// - 配置中的 AllowedRoots 作为额外的静态白名单
    /// - 只有在有效 allowed roots 下的文件才能被访问
    /// - 如果没有配置 AllowedRoots，则仅使用运行时传入的 workspace root
    /// </summary>
    public List<string> AllowedRoots { get; set; } = new();

    /// <summary>
    /// Hashline 精准编辑总开关。
    /// 启用后，既有文件进入 Hashline 编辑链路时，读写会自动联动。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 旧配置别名：保留仅用于兼容历史 appsettings / 测试。
    /// 现在统一折叠到 Enabled。
    /// </summary>
    public bool UseHashlineByDefaultForExistingFileEdits
    {
        get => Enabled;
        set
        {
            if (value)
            {
                Enabled = true;
            }
        }
    }

    /// <summary>
    /// 旧配置别名：保留仅用于兼容历史 appsettings / 测试。
    /// 现在统一折叠到 Enabled。
    /// </summary>
    public bool EnabledForReadTool
    {
        get => Enabled;
        set
        {
            if (value)
            {
                Enabled = true;
            }
        }
    }

    /// <summary>
    /// 旧配置别名：保留仅用于兼容历史 appsettings / 测试。
    /// 现在统一折叠到 Enabled。
    /// </summary>
    public bool EnabledForApplyPatch
    {
        get => Enabled;
        set
        {
            if (value)
            {
                Enabled = true;
            }
        }
    }

    /// <summary>
    /// 旧配置别名：保留仅用于兼容历史 appsettings / 测试。
    /// 现在统一折叠到 Enabled。
    /// </summary>
    public bool EnabledForSmartPatch
    {
        get => Enabled;
        set
        {
            if (value)
            {
                Enabled = true;
            }
        }
    }

    /// <summary>
    /// 对高风险文件强制启用 Hashline 验证（无视显式参数）。
    /// 高风险文件包括：Program.cs、*.csproj、appsettings.json、Auth 相关文件等。
    /// </summary>
    public bool ForceForHighRiskFiles { get; set; }

    public bool IsHashlinePipelineEnabled() => Enabled;

    public bool ShouldRequireHashlineForHighRiskFiles() => Enabled && ForceForHighRiskFiles;

    /// <summary>
    /// 是否在审计日志中启用详细字段（包括 anchorId、lineNumber、operationDetails 等）。
    /// </summary>
    public bool EnableHashlineAuditDetails { get; set; } = true;

    /// <summary>
    /// 是否在哈希计算中保留尾随空格。
    /// </summary>
    public bool PreserveTrailingWhitespaceInHash { get; set; } = true;

    /// <summary>
    /// 最大文件大小（字节）。
    /// </summary>
    public int MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024; // 默认 10MB

    /// <summary>
    /// 最大行数。
    /// </summary>
    public int MaxLineCount { get; set; } = 100000;

    /// <summary>
    /// 是否允许整文件重写操作。
    /// </summary>
    public bool AllowRewriteWholeFile { get; set; } = true;

    /// <summary>
    /// 是否启用审计日志。
    /// </summary>
    public bool EnableAuditLogging { get; set; } = true;
}
