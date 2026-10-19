using System.Collections.Concurrent;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Runtime.RuntimeLoopTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

/// <summary>
/// Turn tool-call budget accounting across multi-call batches. A call that enters
/// Executing is already counted in Progress.ToolCallCount, so it must not be counted
/// again while later calls of the same batch are admitted.
/// </summary>
public sealed class RuntimeToolCallBudgetTests
{
    private const string Instruction = "Stop calling tools and return the final JSON now.";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Batches_3_2_3_1_1_2_UseExactlyTwelveCallBudget(bool pipeline)
    {
        // Mirrors the S3 incident: ten executed calls, then one batch of two legal calls.
        var tools = new ToolRecorder();
        var audit = new InMemoryRuntimeAuditSink();
        var model = new ScriptedModelClient([.. Batches(3, 2, 3, 1, 1, 2), _ => Text("{\"ok\":true}")]);

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(12, tools, pipeline) with { AuditSink = audit });

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(12, result.Turn.Progress.ToolCallCount);
        AssertEachExecutedOnce(tools, 12);
        Assert.All(result.Turn.Steps.SelectMany(static step => step.ToolInvocations ?? []),
            static invocation => Assert.Equal(RuntimeToolInvocationStatus.Succeeded, invocation.Status));
        var replay = RuntimeRecordedReplay.Replay(Recording(audit.Events));
        Assert.Equal(RuntimeTurnStatus.Completed, replay.Status);
        Assert.Equal(12, replay.TotalToolCalls);
        Assert.False(replay.ProviderCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SingleBatch_ExactlyAtBudgetExecutesEveryCall(bool pipeline)
    {
        var tools = new ToolRecorder();
        var model = new ScriptedModelClient([.. Batches(2), _ => Text("{\"ok\":true}")]);

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(2, tools, pipeline));

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(2, result.Turn.Progress.ToolCallCount);
        AssertEachExecutedOnce(tools, 2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverBudgetBatch_DeniesOnlyTheOverflowAndExecutesEachCallAtMostOnce(bool pipeline)
    {
        var tools = new ToolRecorder();
        var audit = new InMemoryRuntimeAuditSink();
        var model = new ScriptedModelClient([.. Batches(3, 2, 3, 1, 1, 3)]);

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(12, tools, pipeline) with { AuditSink = audit });

        Assert.Equal(RuntimeTurnStatus.Failed, result.Status);
        Assert.Equal("tool_call_budget_exhausted", result.Error!.Code);
        Assert.Equal(12, result.Turn.Progress.ToolCallCount);
        AssertEachExecutedOnce(tools, 12);
        var last = result.Turn.Steps[^1].ToolInvocations!;
        Assert.Equal(
            [RuntimeToolInvocationStatus.Succeeded, RuntimeToolInvocationStatus.Succeeded, RuntimeToolInvocationStatus.Denied],
            last.Select(static invocation => invocation.Status));
        Assert.Equal("tool_call_budget_exhausted", last[2].Result!.Error!.Code);
        Assert.DoesNotContain("b6-c3", tools.Ids);
        var replay = RuntimeRecordedReplay.Replay(Recording(audit.Events));
        Assert.Equal(RuntimeTurnStatus.Failed, replay.Status);
        Assert.Equal("tool_call_budget_exhausted", replay.Error!.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BatchLargerThanBudget_ExecutesExactlyTheRemainingBudget(bool pipeline)
    {
        var tools = new ToolRecorder();
        var model = new ScriptedModelClient([.. Batches(5)]);

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(3, tools, pipeline));

        Assert.Equal(RuntimeTurnStatus.Failed, result.Status);
        Assert.Equal("tool_call_budget_exhausted", result.Error!.Code);
        AssertEachExecutedOnce(tools, 3);
        Assert.Equal(
            [
                RuntimeToolInvocationStatus.Succeeded, RuntimeToolInvocationStatus.Succeeded, RuntimeToolInvocationStatus.Succeeded,
                RuntimeToolInvocationStatus.Denied, RuntimeToolInvocationStatus.Denied
            ],
            Assert.Single(result.Turn.Steps).ToolInvocations!.Select(static invocation => invocation.Status));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ToolFreeFinalization_AfterTwelveBatchedCallsMatchesRecordedReplay(bool pipeline)
    {
        var tools = new ToolRecorder();
        var audit = new InMemoryRuntimeAuditSink();
        var model = new ScriptedModelClient([.. Batches(3, 2, 3, 1, 1, 2), _ => Text("{\"ok\":true}")]);
        var request = Request(12, tools, pipeline) with
        {
            AuditSink = audit,
            ToolFinalization = new RuntimeToolFinalizationPolicy { AfterToolCalls = 12, Instruction = Instruction }
        };

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), request);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("{\"ok\":true}", result.FinalText);
        Assert.Equal(RuntimeToolFinalizationReason.ToolCallBudget, result.Turn.Progress.FinalizationReason);
        AssertEachExecutedOnce(tools, 12);
        Assert.Empty(model.Requests[^1].Tools);
        var payload = Assert.IsType<RuntimeToolFinalizationAuditPayload>(
            Assert.Single(audit.Events, static e => e.Kind == RuntimeAuditEventKind.ToolFinalizationTriggered).Payload);
        Assert.Equal(12, payload.ToolCallCount);
        var replay = RuntimeRecordedReplay.Replay(Recording(audit.Events));
        Assert.Equal(RuntimeTurnStatus.Completed, replay.Status);
        Assert.Equal(result.FinalText, replay.FinalText);
        Assert.Equal(12, replay.TotalToolCalls);
    }

    // pipeline=true is the route IA uses (RuntimeToolExecutionPipeline); false is the plain executor route.
    private static RuntimeAgentLoopRequest Request(int maxToolCalls, ToolRecorder tools, bool pipeline)
    {
        var budget = new RuntimeBudgetSnapshot(10, maxToolCalls);
        if (!pipeline)
        {
            return CreateRequest(tools: [ToolDescriptor("open_document")], executor: tools, budget: budget);
        }
        var toolPipeline = new RuntimeToolExecutionPipeline(new RuntimeToolRegistry([tools]), new RuntimeAllowToolPolicy());
        return CreateRequest(tools: toolPipeline.Descriptors, budget: budget) with { ToolExecutor = null, ToolPipeline = toolPipeline };
    }

    private static Func<RuntimeModelRequest, IAsyncEnumerable<RuntimeModelStreamEvent>>[] Batches(params int[] sizes)
        => sizes.Select((size, batch) => (Func<RuntimeModelRequest, IAsyncEnumerable<RuntimeModelStreamEvent>>)(_ => Tool(
                Enumerable.Range(1, size)
                    .Select(call => ToolCall($"b{batch + 1}-c{call}", "open_document"))
                    .ToArray())))
            .ToArray();

    private static void AssertEachExecutedOnce(ToolRecorder tools, int expected)
    {
        Assert.Equal(expected, tools.Ids.Count);
        Assert.Equal(expected, tools.Ids.Distinct().Count());
    }

    private static RuntimeAuditRecording Recording(IReadOnlyList<RuntimeAuditEnvelope> events)
        => new(RuntimeAuditDataMode.PrivateDiagnostic, RuntimeAuditReplayCapability.Recorded, events);

    /// <summary>Records each executed invocation id; serves as both a pipeline tool and a plain executor.</summary>
    private sealed class ToolRecorder : IRuntimeTool, IRuntimeToolExecutor
    {
        private readonly ConcurrentQueue<string> _ids = new();

        public IReadOnlyList<string> Ids => [.. _ids];

        public RuntimeToolDefinition Definition { get; } = new(
            ToolDescriptor("open_document") with { Version = "1.0.0" },
            new HashSet<string>(StringComparer.Ordinal),
            RuntimeToolConcurrency.Serial,
            RuntimeSandboxRequirements.None,
            RuntimeToolLimits.Default);

        public ValueTask<RuntimeToolResult> InvokeAsync(
            RuntimeToolInvocation invocation,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
            => Record(invocation.OriginalCall);

        public ValueTask<RuntimeToolResult> ExecuteAsync(
            RuntimeToolDescriptor descriptor,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
            => Record(call);

        private ValueTask<RuntimeToolResult> Record(RuntimeToolCall call)
        {
            _ids.Enqueue(call.InvocationId.Value);
            return ValueTask.FromResult(new RuntimeToolResult(call.InvocationId, "ok", true,
                Details: new RuntimeToolResultDetails(RuntimeToolOutcome.Succeeded)));
        }
    }
}
