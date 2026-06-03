using System.Collections.ObjectModel;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Abstractions;

/// <summary>
/// 語義差異服務，用於分析代碼改動對整個項目的影響。
/// </summary>
public interface ISemanticDiffService
{
    /// <summary>
    /// 分析影子路徑相對於主路徑的語義變更及受影響的文件。
    /// </summary>
    Task<SemanticDiffResult> AnalyzeDiffAsync(string mainPath, string shadowPath, CancellationToken ct = default);
}

public class SemanticDiffResult
{
    public bool HasChanges { get; set; }

    /// <summary>
    /// 新增或修改的符號（類、方法等）
    /// </summary>
    public Collection<string> ChangedSymbols { get; } = new();

    /// <summary>
    /// 受此次改動影響的其他文件路徑
    /// </summary>
    public Collection<string> ImpactedFiles { get; } = new();

    /// <summary>
    /// 建議的修復或跟進動作
    /// </summary>
    public string Recommendations { get; set; } = string.Empty;

    public void ReplaceChangedSymbols(IEnumerable<string>? changedSymbols) => ReplaceCollection(ChangedSymbols, changedSymbols);
    public void ReplaceImpactedFiles(IEnumerable<string>? impactedFiles) => ReplaceCollection(ImpactedFiles, impactedFiles);

    private static void ReplaceCollection<T>(Collection<T> target, IEnumerable<T>? source)
    {
        target.Clear();
        if (source == null)
        {
            return;
        }

        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
