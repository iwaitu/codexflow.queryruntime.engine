using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Services;

/// <summary>
/// 規則：異步方法命名規範。
/// 強制所有返回 Task 或 ValueTask 的非入口方法必須以 'Async' 結尾。
/// </summary>
public class AsyncMethodNamingRule : IPolicyRule
{
    public string Name => "AsyncMethodNaming";
    public string Description => "確保所有異步方法遵循 'Async' 結尾的命名規範。";

    public async Task<RuleResult> EvaluateAsync(CodexSession session, string shadowPath, CancellationToken ct = default)
    {
        var files = Directory.GetFiles(shadowPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal));

        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var tree = CSharpSyntaxTree.ParseText(content, cancellationToken: ct);
            var root = await tree.GetRootAsync(ct).ConfigureAwait(false);

            var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

            foreach (var method in methods)
            {
                var returnType = method.ReturnType.ToString();
                var isAsyncType = returnType.Contains("Task", StringComparison.Ordinal) || returnType.Contains("ValueTask", StringComparison.Ordinal);
                var hasAsyncSuffix = method.Identifier.Text.EndsWith("Async", StringComparison.Ordinal);

                // 排除 Main 方法
                if (method.Identifier.Text == "Main") continue;

                if (isAsyncType && !hasAsyncSuffix)
                {
                    return new RuleResult(false, Name,
                        $"語義違規：方法 '{method.Identifier.Text}' 返回異步類型但未以 'Async' 結尾。",
                        $"{Path.GetFileName(file)} Line: {method.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
                }
            }
        }

        return new RuleResult(true, Name, "所有異步方法命名符合規範。");
    }
}
