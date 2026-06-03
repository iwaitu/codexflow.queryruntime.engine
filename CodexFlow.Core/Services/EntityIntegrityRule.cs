using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Services;

/// <summary>
/// 規則：領域模型完整性檢查。
/// 確保 Domain 層的類不包含 DTO 模式且具有基本屬性。
/// </summary>
public class EntityIntegrityRule : IPolicyRule
{
    public string Name => "EntityIntegrity";
    public string Description => "確保領域模型純淨，不包含 DTO 或 ViewModel 命名。";

    public async Task<RuleResult> EvaluateAsync(CodexSession session, string shadowPath, CancellationToken ct = default)
    {
        var domainDir = Directory.GetDirectories(shadowPath, "*.Domain", SearchOption.AllDirectories).FirstOrDefault();
        if (domainDir == null) return new RuleResult(true, Name, "未檢測到 Domain 項目。");

        var files = Directory.GetFiles(domainDir, "*.cs", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var tree = CSharpSyntaxTree.ParseText(content, cancellationToken: ct);
            var root = await tree.GetRootAsync(ct).ConfigureAwait(false);

            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

            foreach (var cls in classes)
            {
                var className = cls.Identifier.Text;

                // 攔截 DTO/VM 命名入侵 Domain 層
                if (className.EndsWith("Dto", StringComparison.Ordinal) ||
                    className.EndsWith("ViewModel", StringComparison.Ordinal) ||
                    className.EndsWith("Vm", StringComparison.Ordinal))
                {
                    return new RuleResult(false, Name,
                        $"架構污染：類 '{className}' 帶有 DTO/VM 後綴，不應出現在 Domain 層。",
                        Path.GetFileName(file));
                }
            }
        }

        return new RuleResult(true, Name, "領域模型純淨度檢查通過。");
    }
}
