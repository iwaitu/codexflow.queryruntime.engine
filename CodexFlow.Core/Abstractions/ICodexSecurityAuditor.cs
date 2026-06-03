using CodexFlow.Core.Models;

namespace CodexFlow.Core.Abstractions;

/// <summary>
/// [Level 8] 独立的安全审计代理接口。
/// 负责调度 Polylgot Security Matrix 工具并在独立上下文内评估风险。
/// </summary>
public interface ICodexSecurityAuditor
{
    /// <summary>
    /// 对指定会话或路径执行安全审计。
    /// </summary>
    /// <param name="session">当前会话上下文</param>
    /// <param name="targetPath">审计目标路径（通常为 workspace 或 specific file）</param>
    /// <param name="changedFiles">增量审计：仅需检查的变更文件列表（相对路径）</param>
    /// <returns>审计结果报告</returns>
#pragma warning disable CA1068 // Preserve legacy public parameter order for compatibility.
    Task<SecurityAuditResult> AuditAsync(CodexSession session, string targetPath, CancellationToken ct = default, IEnumerable<string>? changedFiles = null);
#pragma warning restore CA1068
}

public record SecurityAuditResult(
    bool IsPassed,
    string Summary,
    IReadOnlyList<string> Risks,
    string ReportPath,
    IReadOnlyList<string>? LegacyRisks = null,
    IReadOnlyList<string>? DeferredRisks = null);
