using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Utils;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Services.Infra;

public class RoslynSemanticDiffProvider : ILanguageSemanticDiffProvider
{
    private readonly ILogger<RoslynSemanticDiffProvider> _logger;
    private readonly ICodeAnalysisService _analysisService;

    public RoslynSemanticDiffProvider(ILogger<RoslynSemanticDiffProvider> logger, ICodeAnalysisService analysisService)
    {
        _logger = logger;
        _analysisService = analysisService;
    }

    public bool CanHandle(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<SemanticDiffResult> AnalyzeAsync(string mainPath, string shadowPath, CancellationToken ct)
    {
        var result = new SemanticDiffResult();

        if (!File.Exists(shadowPath)) return result; // Should handle deletion

        var shadowContent = await File.ReadAllTextAsync(shadowPath, ct).ConfigureAwait(false);
        var mainContent = File.Exists(mainPath) ? await File.ReadAllTextAsync(mainPath, ct) .ConfigureAwait(false) : "";

        if (shadowContent == mainContent) return result;

        AnalyzeFileDiff(mainContent, shadowContent, Path.GetFileName(shadowPath), result);

        result.HasChanges = result.ChangedSymbols.Count > 0;

        // 2. 基於依賴圖分析影響範圍
        if (result.HasChanges)
        {
            // 注意：BuildGraphAsync 通常需要目录路径。这里我们需要传入 shadowPath 的根目录或者所在目录。
            // 假设 shadowPath 是文件路径。
            // 原有的 RoslynSemanticDiffService 是接收文件夹的，现在 Provider 接收文件。
            // 但是 BuildGraphAsync 需要整个项目目录来构建图。
            // 这是一个问题。AnalyzeAsync 的签名是 mainPath, shadowPath (FILE paths).
            // 我们需要找到项目根目录。
            // 对于 Roslyn，通常是 .sln 或 .csproj 所在目录。
            // 我们可以尝试向上查找。

            var rootDir = FindProjectRoot(shadowPath);
            if (!string.IsNullOrEmpty(rootDir))
            {
                var graph = await _analysisService.BuildGraphAsync(rootDir, ct).ConfigureAwait(false);
                if (graph?.Nodes != null)
                {
                    var impacted = _analysisService.GetImpactedFiles(graph, Path.GetFileName(shadowPath));
                    if (impacted != null)
                    {
                        result.ImpactedFiles.AddRange(impacted);
                    }
                }
            }

            result.ReplaceImpactedFiles(result.ImpactedFiles.Distinct());
            if (result.ImpactedFiles.Count > 0)
            {
                result.Recommendations = $"檢測到語義變更波及了 {result.ImpactedFiles.Count} 個文件。建議執行回歸測試並檢查受影響的契約。";
            }
        }

        return result;
    }

    private static string? FindProjectRoot(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (dir != null)
        {
            if (Directory.GetFiles(dir, "*.csproj").Length > 0 || Directory.GetFiles(dir, "*.sln").Length > 0)
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return Path.GetDirectoryName(filePath); // Fallback
    }

    private static void AnalyzeFileDiff(string oldSource, string newSource, string filename, SemanticDiffResult result)
    {
        var oldTree = CSharpSyntaxTree.ParseText(oldSource);
        var newTree = CSharpSyntaxTree.ParseText(newSource);

        var oldRoot = oldTree.GetCompilationUnitRoot();
        var newRoot = newTree.GetCompilationUnitRoot();

        var oldMembers = oldRoot.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Select(m => GetMemberSignature(m)).ToHashSet();

        var newMembers = newRoot.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Select(m => GetMemberSignature(m));

        foreach (var member in newMembers)
        {
            if (!oldMembers.Contains(member))
            {
                result.ChangedSymbols.Add($"Modified Member in {filename}: {member}");
            }
        }
    }

    private static string GetMemberSignature(MemberDeclarationSyntax member)
    {
        if (member is MethodDeclarationSyntax method)
            return $"Method {method.Identifier.Text}({method.ParameterList.Parameters.Count} params)";
        if (member is PropertyDeclarationSyntax prop)
            return $"Property {prop.Identifier.Text}";
        if (member is ClassDeclarationSyntax cls)
            return $"Class {cls.Identifier.Text}";

        return member.GetType().Name;
    }
}
