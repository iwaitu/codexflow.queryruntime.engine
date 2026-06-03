using Microsoft.CodeAnalysis;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Abstractions
{
    public interface ICodeAnalysisService
    {
        /// <summary>
        /// 对一段代码片段进行静态分析，返回错误诊断信息
        /// </summary>
#pragma warning disable CA1002 // Preserve legacy list-based API for compatibility.
        Task<List<CodeDiagnostic>> AnalyzeCodeAsync(string code, string language = "C#");
#pragma warning restore CA1002

        /// <summary>
        /// 分析指定项目路径下的所有代码（进阶版可以加载整个 .sln）
        /// </summary>
#pragma warning disable CA1002 // Preserve legacy list-based API for compatibility.
        Task<List<CodeDiagnostic>> AnalyzeProjectAsync(string projectPath);
#pragma warning restore CA1002

        /// <summary>
        /// 构建项目的语义依赖图 (Stage 1.5 核心能力)
        /// </summary>
        Task<DependencyGraph> BuildGraphAsync(string rootPath, CancellationToken cancellationToken = default);

        /// <summary>
        /// [Level 4] 语义熔断检查：评估操作是否涉及高风险核心文件
        /// </summary>
        Task<GuardrailResult> CheckGuardrailAsync(DependencyGraph graph, string targetFilePath, string taskRiskLevel);

        /// <summary>
        /// [Level 4] 因果分析：获取指定文件的下游受影响文件列表
        /// </summary>
#pragma warning disable CA1002 // Preserve legacy list-based API for compatibility.
        List<string> GetImpactedFiles(DependencyGraph graph, string changedFilePath);
#pragma warning restore CA1002
    }

    public record CodeDiagnostic(string Id, string Message, string Severity, int Line, int Column);
    public record GuardrailResult(bool IsBlocked, string? Reason);
}
