using CodexFlow.QueryRuntime.Engine.V2;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class RuntimeCoreParityGateTests
{
    [Fact]
    public void Compare_AcceptsIdenticalExecutionWithNormalizedText()
    {
        var baseline = Projection("answer with spacing");
        var candidate = Projection(" answer   with\r\nspacing ");

        var report = RuntimeCoreParityGate.Compare(baseline, candidate);

        Assert.True(report.Passed);
        Assert.True(report.ExecutionSemanticsMatch);
        Assert.True(report.FinalTextMatches);
        Assert.Empty(report.Differences);
    }

    [Fact]
    public void Compare_TextToleranceCannotHideExecutionSemanticDifference()
    {
        var baseline = Projection("baseline");
        var candidate = Projection("different") with
        {
            ToolOrder = ["read", "verify"],
            SideEffectCount = 1
        };

        var report = RuntimeCoreParityGate.Compare(
            baseline,
            candidate,
            new RuntimeCoreParityOptions(RuntimeFinalTextComparison.Ignore));

        Assert.False(report.Passed);
        Assert.False(report.ExecutionSemanticsMatch);
        Assert.True(report.FinalTextMatches);
        Assert.Collection(
            report.Differences,
            difference => Assert.Equal("tool_order", difference.Dimension),
            difference => Assert.Equal("side_effect_count", difference.Dimension));
        Assert.All(report.Differences, static difference => Assert.True(difference.IsExecutionSemantic));
    }

    [Fact]
    public void Compare_ReportsFinalTextSeparately()
    {
        var report = RuntimeCoreParityGate.Compare(
            Projection("baseline"),
            Projection("candidate"),
            new RuntimeCoreParityOptions(RuntimeFinalTextComparison.Exact));

        Assert.True(report.ExecutionSemanticsMatch);
        Assert.False(report.FinalTextMatches);
        Assert.False(report.Passed);
        var difference = Assert.Single(report.Differences);
        Assert.Equal("final_text", difference.Dimension);
        Assert.False(difference.IsExecutionSemantic);
    }

    private static RuntimeCoreParityProjection Projection(string text)
        => new(
            ["verify:allow", "read:allow"],
            ["verify", "read"],
            "completed",
            SideEffectCount: 0,
            text);
}
