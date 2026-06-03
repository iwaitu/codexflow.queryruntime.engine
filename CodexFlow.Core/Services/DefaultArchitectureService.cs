using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace CodexFlow.Core.Services;

public class DefaultArchitectureService : IArchitectureService
{
    private readonly ILogger<DefaultArchitectureService> _logger;

    public DefaultArchitectureService(ILogger<DefaultArchitectureService> logger)
    {
        _logger = logger;
    }

    public List<ArchitectureMetrics> AnalyzeGraph(DependencyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var results = new List<ArchitectureMetrics>();

        foreach (var node in graph.Nodes.Values)
        {
            var metrics = new ArchitectureMetrics
            {
                FilePath = node.FilePath,
                Language = node.Language,
                AfferentCoupling = node.ReferencedBy.Count,
                EfferentCoupling = node.References.Count
            };

            // 1. Hub 检查 (高入度 + 高出度)
            if (metrics.AfferentCoupling >= 5 && metrics.EfferentCoupling >= 5)
            {
                metrics.DetectedSmells.Add("Architecture Hub (God Object)");
                metrics.RefactorPriority += 40;
            }

            // 2. Unstable Foundation (基础库依赖过多顶层逻辑)
            if (IsCoreFolder(node.FilePath) && metrics.Instability > 0.7)
            {
                metrics.DetectedSmells.Add("Unstable Foundation");
                metrics.RefactorPriority += 30;
            }

            // 3. High Coupling (绝对耦合度过高)
            if (metrics.EfferentCoupling > 15)
            {
                metrics.DetectedSmells.Add("Tight Coupling");
                metrics.RefactorPriority += 20;
            }

            results.Add(metrics);
        }

        return results.OrderByDescending(r => r.RefactorPriority).ToList();
    }

    public string GenerateRefactoringAdvice(ArchitectureMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        if (metrics.DetectedSmells.Contains("Architecture Hub (God Object)"))
        {
            return $"【建议】文件 '{metrics.FilePath}' 目前是系统的核心枢纽（入度:{metrics.AfferentCoupling}, 出度:{metrics.EfferentCoupling}）。" +
                   "这会导致任何修改都产生巨大的风险。建议将其职责拆分为更小的服务，或通过事件驱动（Event-Driven）解耦。";
        }

        if (metrics.DetectedSmells.Contains("Unstable Foundation"))
        {
            return $"【建议】检测到基础层文件 '{metrics.FilePath}' 依赖了太多的外部组件（Instability:{metrics.Instability:F2}）。" +
                   "基础层应当是稳定的。建议检查是否将业务逻辑混入了底层接口中。";
        }

        return $"文件 '{metrics.FilePath}' 架构健康度良好。";
    }

    public List<string> DetectCircularDependencies(DependencyGraph graph)
    {
        // 简化的循环依赖检测 (DFS)
        var circulars = new List<string>();
        // 此处后续可实现 Tarjan 算法或深度优先搜索
        return circulars;
    }

    private static bool IsCoreFolder(string path)
    {
        var normalized = path.ToUpper(CultureInfo.InvariantCulture);
        return normalized.Contains("CORE", StringComparison.Ordinal) ||
               normalized.Contains("DOMAIN", StringComparison.Ordinal) ||
               normalized.Contains("BASE", StringComparison.Ordinal);
    }
}
