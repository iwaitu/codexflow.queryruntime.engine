using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Services;

/// <summary>
/// 規則：禁止跨層非法引用（例如 Domain 不得引用 Infrastructure）。
/// </summary>
public class ForbiddenDependencyRule : IPolicyRule
{
    public string Name => "ForbiddenDependency";
    public string Description => "確保核心領域模型不依賴於外部基礎設施。";

    public async Task<RuleResult> EvaluateAsync(CodexSession session, string shadowPath, CancellationToken ct = default)
    {
        // 簡單演示：掃描 Domain 目錄下的 .cs 文件是否包含 Infrastructure 關鍵字
        var domainDir = Path.Combine(shadowPath, "CodexFlow.Domain");
        if (!Directory.Exists(domainDir)) return new RuleResult(true, Name, "未檢測到 Domain 層，跳過。");

        var files = Directory.GetFiles(domainDir, "*.cs", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            if (content.Contains("using CodexFlow.Infrastructure", StringComparison.Ordinal))
            {
                return new RuleResult(false, Name, $"架構違規：Domain 層文件 {Path.GetFileName(file)} 引用了 Infrastructure。", file);
            }
        }

        return new RuleResult(true, Name, "依賴關係符合規約。");
    }
}
