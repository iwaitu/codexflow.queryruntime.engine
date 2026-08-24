namespace CodexFlow.QueryRuntime.Engine.V2;

/// <summary>
/// Normalized execution projection used to compare a stable runtime with a preview runtime.
/// Hosts are responsible for mapping backend-specific terminal and policy values into the
/// same vocabulary before invoking the gate.
/// </summary>
public sealed record RuntimeCoreParityProjection(
    IReadOnlyList<string> PolicyDecisions,
    IReadOnlyList<string> ToolOrder,
    string TerminalReason,
    int SideEffectCount,
    string FinalText);

public enum RuntimeFinalTextComparison
{
    Exact = 0,
    NormalizedWhitespace = 1,
    Ignore = 2
}

public sealed record RuntimeCoreParityOptions(
    RuntimeFinalTextComparison FinalTextComparison = RuntimeFinalTextComparison.NormalizedWhitespace);

public sealed record RuntimeCoreParityDifference(
    string Dimension,
    string Baseline,
    string Candidate,
    bool IsExecutionSemantic);

public sealed record RuntimeCoreParityReport(
    IReadOnlyList<RuntimeCoreParityDifference> Differences,
    bool ExecutionSemanticsMatch,
    bool FinalTextMatches)
{
    public bool Passed => ExecutionSemanticsMatch && FinalTextMatches;
}

/// <summary>
/// Applies zero tolerance to policy, tool order, terminal reason and side-effect count.
/// Final text is evaluated separately so a permissive text tolerance can never hide an
/// execution-semantic regression.
/// </summary>
public static class RuntimeCoreParityGate
{
    public static RuntimeCoreParityReport Compare(
        RuntimeCoreParityProjection baseline,
        RuntimeCoreParityProjection candidate,
        RuntimeCoreParityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        options ??= new RuntimeCoreParityOptions();

        var differences = new List<RuntimeCoreParityDifference>();
        AddSequenceDifference(
            differences,
            "policy",
            baseline.PolicyDecisions,
            candidate.PolicyDecisions);
        AddSequenceDifference(
            differences,
            "tool_order",
            baseline.ToolOrder,
            candidate.ToolOrder);
        AddValueDifference(
            differences,
            "terminal_reason",
            baseline.TerminalReason,
            candidate.TerminalReason,
            isExecutionSemantic: true);
        AddValueDifference(
            differences,
            "side_effect_count",
            baseline.SideEffectCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            candidate.SideEffectCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            isExecutionSemantic: true);

        var finalTextMatches = CompareFinalText(
            baseline.FinalText,
            candidate.FinalText,
            options.FinalTextComparison);
        if (!finalTextMatches)
        {
            differences.Add(new RuntimeCoreParityDifference(
                "final_text",
                baseline.FinalText,
                candidate.FinalText,
                IsExecutionSemantic: false));
        }

        return new RuntimeCoreParityReport(
            differences,
            ExecutionSemanticsMatch: differences.All(static difference => !difference.IsExecutionSemantic),
            FinalTextMatches: finalTextMatches);
    }

    private static void AddSequenceDifference(
        ICollection<RuntimeCoreParityDifference> differences,
        string dimension,
        IReadOnlyList<string> baseline,
        IReadOnlyList<string> candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!baseline.SequenceEqual(candidate, StringComparer.Ordinal))
        {
            differences.Add(new RuntimeCoreParityDifference(
                dimension,
                string.Join(" -> ", baseline),
                string.Join(" -> ", candidate),
                IsExecutionSemantic: true));
        }
    }

    private static void AddValueDifference(
        ICollection<RuntimeCoreParityDifference> differences,
        string dimension,
        string baseline,
        string candidate,
        bool isExecutionSemantic)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(baseline, candidate, StringComparison.Ordinal))
        {
            differences.Add(new RuntimeCoreParityDifference(
                dimension,
                baseline,
                candidate,
                isExecutionSemantic));
        }
    }

    private static bool CompareFinalText(
        string baseline,
        string candidate,
        RuntimeFinalTextComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        return comparison switch
        {
            RuntimeFinalTextComparison.Exact => string.Equals(baseline, candidate, StringComparison.Ordinal),
            RuntimeFinalTextComparison.NormalizedWhitespace => string.Equals(
                NormalizeWhitespace(baseline),
                NormalizeWhitespace(candidate),
                StringComparison.Ordinal),
            RuntimeFinalTextComparison.Ignore => true,
            _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, "Unknown final-text comparison mode.")
        };
    }

    private static string NormalizeWhitespace(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
