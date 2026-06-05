using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Experimental;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Contracts;

public sealed class HostAdapterContractTestKitTests
{
    [Fact]
    public Task HostAdapterContract_PreToolHookBlocksWriteTool()
        => HostAdapterContractTestKit.AssertPreToolHookBlocksWriteToolAsync(
            CreateEngine,
            TestContext.Current.CancellationToken);

    [Fact]
    public Task HostAdapterContract_StopGateRequiresContinuation()
        => HostAdapterContractTestKit.AssertStopGateRequiresContinuationAsync(
            CreateEngine,
            TestContext.Current.CancellationToken);

    [Fact]
    public Task HostAdapterContract_RequiredToolTriggersContinuation()
        => HostAdapterContractTestKit.AssertRequiredToolContractTriggersContinuationAsync(
            CreateEngine,
            TestContext.Current.CancellationToken);

    [Fact]
    public Task HostAdapterContract_ResultMetadataMapsHostSemantics()
        => HostAdapterContractTestKit.AssertResultMetadataMapsHostSemanticsAsync(
            CreateEngine,
            TestContext.Current.CancellationToken);

    [Fact]
    public Task HostAdapterContract_TracePathContainment()
        => HostAdapterContractTestKit.AssertTracePathContainmentAsync(
            CreateEngine,
            TestContext.Current.CancellationToken);

    private static IQueryRuntimeHostEngine CreateEngine(IExperimentalModelClient modelClient)
        => new ExperimentalQueryRuntimeHarness(modelClient);
}
