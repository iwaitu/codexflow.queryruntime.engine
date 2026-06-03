namespace CodexFlow.Core.Planning.Committee;

/// <summary>
/// 复杂度判定结果。
/// </summary>
public class ComplexityClassification
{
    /// <summary>是否为复杂需求（建议召开委员会）。</summary>
    public bool IsComplex { get; set; }

    /// <summary>判定理由。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>触发的复杂度指标。</summary>
    public List<string> TriggeredIndicators { get; set; } = [];
}

/// <summary>
/// 复杂度判定服务接口。
/// 决定是否建议用户召开委员会评审。
/// </summary>
public interface IComplexityClassifier
{
    /// <summary>
    /// 判定给定目标的复杂度。
    /// </summary>
    /// <param name="goal">用户目标。</param>
    /// <param name="projectSummary">项目摘要（Stage 1 产物）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<ComplexityClassification> ClassifyAsync(
        string goal,
        string projectSummary,
        CancellationToken ct = default);
}
