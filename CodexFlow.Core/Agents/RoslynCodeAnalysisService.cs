using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using System;
using System.Globalization;

namespace CodexFlow.Core.Agents
{
    public class RoslynCodeAnalysisService : ICodeAnalysisService
    {
        private readonly ILogger<RoslynCodeAnalysisService> _logger;

        public RoslynCodeAnalysisService(ILogger<RoslynCodeAnalysisService> logger)
        {
            _logger = logger;
        }

        public Task<List<CodeDiagnostic>> AnalyzeCodeAsync(string code, string language = "C#")
        {
            ArgumentNullException.ThrowIfNull(code);

            var result = new List<CodeDiagnostic>();

            try
            {
                // 解析语法树
                var syntaxTree = CSharpSyntaxTree.ParseText(code);

                // 创建一个简单的内存编译对象
                var compilation = CSharpCompilation.Create("AgentAnalysis")
                    .AddReferences(
                        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                        MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location))
                    .AddSyntaxTrees(syntaxTree);

                // 获取诊断信息
                var diagnostics = compilation.GetDiagnostics();

                foreach (var diag in diagnostics)
                {
                    // 仅关注错误和严重警告
                    if (diag.Severity == DiagnosticSeverity.Error || diag.Severity == DiagnosticSeverity.Warning)
                    {
                        var lineSpan = diag.Location.GetLineSpan();
                        result.Add(new CodeDiagnostic(
                            diag.Id,
                            diag.GetMessage(CultureInfo.InvariantCulture),
                            diag.Severity.ToString(),
                            lineSpan.StartLinePosition.Line + 1, // 1-indexed
                            lineSpan.StartLinePosition.Character + 1
                        ));
                    }
                }
            }
            catch (ArgumentException ex)
            {
                StructuredLog.Error(_logger, ex, "Roslyn 分析过程出现异常");
            }
            catch (InvalidOperationException ex)
            {
                StructuredLog.Error(_logger, ex, "Roslyn 分析过程出现异常");
            }

            return Task.FromResult(result);
        }

        public Task<List<CodeDiagnostic>> AnalyzeProjectAsync(string projectPath)
        {
            // 这里以后可以扩展为使用 MSBuildWorkspace 加载整个 .sln
            StructuredLog.Warning(_logger, "全项目分析功能待扩展，目前仅支持代码片段。");
            return Task.FromResult(new List<CodeDiagnostic>());
        }

        public Task<DependencyGraph> BuildGraphAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            // 暂不支持通过此服务构建 Graph，请使用 SemanticDependencyScanner
            throw new NotImplementedException("Use SemanticDependencyScanner for graph building.");
        }

        public Task<GuardrailResult> CheckGuardrailAsync(DependencyGraph graph, string targetFilePath, string taskRiskLevel)
            => Task.FromResult(new GuardrailResult(false, null));

        public List<string> GetImpactedFiles(DependencyGraph graph, string changedFilePath)
            => new List<string>();
    }
}

