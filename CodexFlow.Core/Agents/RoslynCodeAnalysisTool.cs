using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;

namespace CodexFlow.Core.Agents;

public class RoslynCodeAnalysisTool : ICodexTool
{
    private readonly ICodeAnalysisService _analysisService;

    public string Name => "analyze_code";
    public string Description => "使用 Roslyn 引擎对指定的 C# 代码片段进行静态分析，返回编译错误和警告诊断信息。Few-shot: analyze_code({\"code\":\"class A { void M(){ int x = \\\"1\\\"; } }\"})。";
    public ToolCategory Category => ToolCategory.Analysis;
    public IReadOnlyList<int> AllowedStages => new[] { 1, 3, 4 }; // 允许在分析、执行、验证阶段调用

    public RoslynCodeAnalysisTool(ICodeAnalysisService analysisService)
    {
        _analysisService = analysisService;
    }

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!arguments.TryGetValue("code", out var codeObj) || codeObj is not string code)
        {
            return CodexToolResult.Error("缺少必需的参数 'code'。");
        }

        var diagnostics = await _analysisService.AnalyzeCodeAsync(code).ConfigureAwait(false);
        if (diagnostics.Count == 0) return CodexToolResult.Succeeded("✅ 代码分析通过，未发现错误或警告。");

        var sb = new StringBuilder();
        sb.AppendLine("❌ 发现以下诊断信息：");
        foreach (var diag in diagnostics)
        {
            sb.AppendLine(FormattableString.Invariant($"- [{diag.Severity}] {diag.Id}: {diag.Message} (行: {diag.Line}, 列: {diag.Column})"));
        }

        return CodexToolResult.Succeeded(sb.ToString(), diagnostics);
    }
}
