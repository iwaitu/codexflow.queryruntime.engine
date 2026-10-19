using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Runtime.RuntimeLoopTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

/// <summary>E1: FailClosedOnHardCaps rejects any hard cap but allows non-initial compaction.</summary>
public sealed class RuntimeContextHardCapPolicyTests
{
    [Fact]
    public async Task LaterToolResultHardCap_FailsBeforeTheNextModelRequest()
    {
        var model = new ScriptedModelClient(_ => Tool(ToolCall("c1", "open_document")), _ => Text("unreachable"));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request("short", Options()) with
        {
            Tools = [ToolDescriptor("open_document")],
            ToolExecutor = new TextExecutor(new string('y', 12_000))
        });

        Assert.Equal(RuntimeTerminationReason.FailClosed, result.TerminationReason);
        Assert.Equal(RuntimeErrorCategory.ContextOverflow, result.Error!.Category);
        Assert.Equal("context_truncation_rejected", result.Error.Code);
        Assert.Contains("large_tool_result_replaced", result.Error.Message, StringComparison.Ordinal);
        // The tool output was normalized at append time but never reached a model request.
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task InitialHardCap_FailsBeforeAnyModelRequest()
    {
        var model = new ScriptedModelClient(_ => Text("unreachable"));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(new string('证', 3_000), Options()));

        Assert.Equal("context_truncation_rejected", result.Error!.Code);
        Assert.Contains("text_fragment_hard_cap", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task NonInitialCompaction_IsAllowedButInitialCompactionFails()
    {
        var steps = Enumerable.Range(0, 6)
            .Select(index => (Func<RuntimeModelRequest, IAsyncEnumerable<RuntimeModelStreamEvent>>)(_ =>
                Tool(ToolCall($"c{index}", "open_document"))))
            .Append(_ => Text("done"))
            .ToArray();
        var model = new ScriptedModelClient(steps);
        var options = Options() with { RecentTrajectoryMessages = 4 };

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request("short", options) with
        {
            Tools = [ToolDescriptor("open_document")],
            ToolExecutor = new TextExecutor(new string('k', 3_200)),
            Budget = new RuntimeBudgetSnapshot(8, 8)
        });

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        var compacted = result.ContextEvents.Where(static e => e.Kind == RuntimeContextEventKind.ContextCompacted).ToArray();
        Assert.NotEmpty(compacted);
        Assert.DoesNotContain(compacted.SelectMany(static e => e.ItemIds), static id => id.Value.StartsWith("h0:m", StringComparison.Ordinal));
        Assert.DoesNotContain(result.ContextEvents, static e => e.Code is "tool_result_hard_cap" or "large_tool_result_replaced");

        var initial = Enumerable.Range(0, 30)
            .Select(index => new RuntimeMessage(
                index % 2 == 0 ? RuntimeMessageRole.User : RuntimeMessageRole.Assistant,
                [new RuntimeTextItem($"{index}:" + new string('z', 600))]))
            .ToArray();
        var rejected = await RunLoopAsync(
            new RuntimeAgentLoop(new ScriptedModelClient(_ => Text("unreachable"))),
            Request("x", Options()) with { InitialMessages = initial });
        Assert.Equal("context_truncation_rejected", rejected.Error!.Code);
        Assert.Contains("deterministic_local_compaction", rejected.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HardCapPolicy_PersistsNormalizationEventsAndResumeRequiresThem()
    {
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var request = Request("short", Options()) with
        {
            CheckpointSink = checkpoints,
            RecoveryCompatibilityId = "hardcap-test:v1"
        };
        var result = await RunLoopAsync(new RuntimeAgentLoop(new ScriptedModelClient(_ => Text("ok"))), request);
        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        var prepared = Assert.Single(checkpoints.Checkpoints, static c => c.Kind == RuntimeCheckpointKind.StepPrepared);
        Assert.NotNull(prepared.HistoryNormalizationEvents);
        Assert.Equal(RuntimeCheckpointSchema.CurrentVersion, prepared.SchemaVersion);

        var stripped = prepared with { HistoryNormalizationEvents = null };
        var resume = request with
        {
            Attempt = RuntimeRunAttempt.Resume(stripped, "attempt-hardcap-resume"),
            CheckpointSink = new InMemoryRuntimeCheckpointSink()
        };
        var failure = await Assert.ThrowsAsync<RuntimeResumeException>(() =>
            new RuntimeAgentLoop(new ScriptedModelClient(_ => Text("unreachable"))).ResumeAsync(
                resume, stripped, ct: TestContext.Current.CancellationToken));
        Assert.Equal("checkpoint_context_history_missing", failure.Error.Code);
    }

    [Fact]
    public async Task AllowPolicy_StillOmitsNormalizationEventsFromCheckpoints()
    {
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var request = Request("short", Options() with { TruncationPolicy = RuntimeContextTruncationPolicy.Allow }) with
        {
            CheckpointSink = checkpoints,
            RecoveryCompatibilityId = "allow-test:v1"
        };
        await RunLoopAsync(new RuntimeAgentLoop(new ScriptedModelClient(_ => Text("ok"))), request);
        Assert.All(checkpoints.Checkpoints, static c => Assert.Null(c.HistoryNormalizationEvents));
    }

    [Fact]
    public void UndefinedTruncationPolicy_IsRejected()
    {
        var options = Options() with { TruncationPolicy = (RuntimeContextTruncationPolicy)99 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeContextManager(options));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeHistory.Create([], 0, options));
    }

    private static RuntimeContextOptions Options()
        => new()
        {
            MaxContextTokens = 4_000,
            MaxItemTokens = 1_000,
            MaxToolResultTokens = 1_000,
            LargeToolResultTokens = 1_000,
            SummaryTokens = 500,
            TruncationPolicy = RuntimeContextTruncationPolicy.FailClosedOnHardCaps
        };

    private static RuntimeAgentLoopRequest Request(string firstMessage, RuntimeContextOptions options)
        => CreateRequest(budget: new RuntimeBudgetSnapshot(3, 3)) with
        {
            InitialMessages = [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(firstMessage)])],
            ContextManager = new RuntimeContextManager(options)
        };

    private sealed class TextExecutor(string text) : IRuntimeToolExecutor
    {
        public ValueTask<RuntimeToolResult> ExecuteAsync(
            RuntimeToolDescriptor descriptor,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
            => ValueTask.FromResult(new RuntimeToolResult(call.InvocationId, text, true));
    }
}
