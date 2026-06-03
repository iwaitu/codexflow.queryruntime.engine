using CodexFlow.Core.Models;

namespace CodexFlow.Core.Abstractions;

public interface IArchitectureService
{
    /// <summary>
    /// 分析依赖图并计算架构指标
    /// </summary>
#pragma warning disable CA1002 // Preserve legacy list-based API for compatibility.
    List<ArchitectureMetrics> AnalyzeGraph(DependencyGraph graph);
#pragma warning restore CA1002

    /// <summary>
    /// 针对特定文件生成重构建议
    /// </summary>
    string GenerateRefactoringAdvice(ArchitectureMetrics metrics);

    /// <summary>
    /// 检查是否存在跨语言循环依赖
    /// </summary>
#pragma warning disable CA1002 // Preserve legacy list-based API for compatibility.
    List<string> DetectCircularDependencies(DependencyGraph graph);
#pragma warning restore CA1002
}
