namespace CodexFlow.Core.Models;

/// <summary>
/// Runtime settings for auto-triggering conversation compression based on the
/// estimated conversation-context size relative to the active model context window.
/// </summary>
public sealed class ContextCompressionTriggerOptions
{
    public const string SectionName = "ContextCompression";

    /// <summary>
    /// Resolved from <c>VllmAgent:MaxTokensLength</c> at startup.
    /// </summary>
    public int ModelMaxContextTokens { get; set; } = 200000;

    /// <summary>
    /// Auto-compress when the estimated conversation-context tokens reach this ratio
    /// of <see cref="ModelMaxContextTokens"/>.
    /// </summary>
    public double AutoCompressionTriggerRatio { get; set; } = 0.85d;

    /// <summary>
    /// Heuristic token estimator used by the session manager. Defaults to 4 chars/token.
    /// </summary>
    public double EstimatedCharsPerToken { get; set; } = 4.0d;

    /// <summary>
    /// Small safety guard to avoid compressing on an extremely short exchange.
    /// </summary>
    public int MinRecentTurnsBeforeCompression { get; set; } = 2;

    /// <summary>
    /// Light-trim the oldest buffered turns into <see cref="CodexSession.HistorySummary"/>
    /// when the in-session recent-turn window exceeds this size.
    /// </summary>
    public int RecentTurnsSoftLimit { get; set; } = 12;

    /// <summary>
    /// Deterministically compact oversized individual turns before storing them in
    /// <see cref="CodexSession.RecentTurns"/>, while the raw turn is still appended
    /// to the audit/history store.
    /// </summary>
    public int SingleTurnSoftLimitChars { get; set; } = 6000;

    /// <summary>
    /// Number of leading characters to preserve when compacting an oversized turn.
    /// </summary>
    public int SingleTurnPreserveHeadChars { get; set; } = 2000;

    /// <summary>
    /// Number of trailing characters to preserve when compacting an oversized turn.
    /// </summary>
    public int SingleTurnPreserveTailChars { get; set; } = 1000;

    public int ResolveTriggerThresholdTokens()
    {
        var maxTokens = ModelMaxContextTokens > 0 ? ModelMaxContextTokens : 200000;
        var ratio = AutoCompressionTriggerRatio;
        if (double.IsNaN(ratio) || double.IsInfinity(ratio))
        {
            ratio = 0.85d;
        }

        ratio = Math.Clamp(ratio, 0.05d, 1.00d);
        return (int)Math.Ceiling(maxTokens * ratio);
    }

    public double ResolveEstimatedCharsPerToken()
    {
        if (double.IsNaN(EstimatedCharsPerToken) || double.IsInfinity(EstimatedCharsPerToken) || EstimatedCharsPerToken <= 0d)
        {
            return 4.0d;
        }

        return EstimatedCharsPerToken;
    }

    public int ResolveRecentTurnsSoftLimit()
        => Math.Max(2, RecentTurnsSoftLimit);

    public int ResolveSingleTurnSoftLimitChars()
        => Math.Max(512, SingleTurnSoftLimitChars);

    public int ResolveSingleTurnPreserveHeadChars()
        => Math.Max(128, SingleTurnPreserveHeadChars);

    public int ResolveSingleTurnPreserveTailChars()
        => Math.Max(128, SingleTurnPreserveTailChars);
}
