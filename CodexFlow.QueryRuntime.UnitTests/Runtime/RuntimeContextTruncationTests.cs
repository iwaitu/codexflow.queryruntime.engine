using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Runtime.RuntimeLoopTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class RuntimeContextTruncationTests
{
    [Fact]
    public void ForModelWindow_ScalesDefaultRatios()
    {
        Assert.Equal(RuntimeContextOptions.Default, RuntimeContextOptions.ForModelWindow(32_000));

        var large = RuntimeContextOptions.ForModelWindow(128_000);
        Assert.Equal(128_000, large.MaxContextTokens);
        Assert.Equal(32_000, large.MaxItemTokens);
        Assert.Equal(16_000, large.MaxToolResultTokens);
        Assert.Equal(8_000, large.LargeToolResultTokens);
        Assert.Equal(8_000, large.SummaryTokens);
        Assert.Equal(RuntimeContextOptions.Default.RecentTrajectoryMessages, large.RecentTrajectoryMessages);
        Assert.Equal(RuntimeContextTruncationPolicy.Allow, large.TruncationPolicy);
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeContextOptions.ForModelWindow(512));
    }

    [Fact]
    public async Task Allow_KeepsLegacyTruncationAndEmitsItemIds()
    {
        var model = new ScriptedModelClient(_ => Text("done"));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(
            new string('x', 6_000),
            SmallOptions(RuntimeContextTruncationPolicy.Allow)));

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        var capped = Assert.Single(result.ContextEvents, static e => e.Code == "text_fragment_hard_cap");
        Assert.Equal("h0:m0:source:0", Assert.Single(capped.ItemIds).Value);
    }

    [Theory]
    [InlineData(RuntimeContextTruncationPolicy.FailClosedOnInitialMessages)]
    [InlineData(RuntimeContextTruncationPolicy.FailClosedOnAny)]
    public async Task InitialMessageTruncation_FailsClosedBeforeTheModelIsCalled(RuntimeContextTruncationPolicy policy)
    {
        var model = new ScriptedModelClient(_ => Text("unreachable"));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request(new string('证', 3_000), SmallOptions(policy)));

        Assert.Equal(RuntimeTerminationReason.FailClosed, result.TerminationReason);
        Assert.Equal(RuntimeErrorCategory.ContextOverflow, result.Error!.Category);
        Assert.Equal("context_truncation_rejected", result.Error.Code);
        Assert.Contains("h0:m0:source:0", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task ToolResultTruncation_FailsOnlyUnderFailClosedOnAny()
    {
        foreach (var (policy, expectFailure) in new[]
                 {
                     (RuntimeContextTruncationPolicy.FailClosedOnInitialMessages, false),
                     (RuntimeContextTruncationPolicy.FailClosedOnAny, true)
                 })
        {
            var model = new ScriptedModelClient(_ => Tool(ToolCall("c1", "open_document")), _ => Text("done"));
            var executor = new LargeResultExecutor(new string('y', 12_000));

            var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request("short", SmallOptions(policy)) with
            {
                Tools = [ToolDescriptor("open_document")],
                ToolExecutor = executor
            });

            if (expectFailure)
            {
                Assert.Equal("context_truncation_rejected", result.Error!.Code);
                Assert.Contains("large_tool_result_replaced", result.Error.Message, StringComparison.Ordinal);
                Assert.Single(model.Requests);
            }
            else
            {
                Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
                Assert.Equal(2, model.Requests.Count);
            }
            var replaced = Assert.Single(result.ContextEvents, static e => e.Code == "large_tool_result_replaced");
            Assert.Equal(RuntimeContextEventKind.ItemReplaced, replaced.Kind);
            Assert.NotEmpty(replaced.ItemIds);
        }
    }

    [Fact]
    public async Task CompactionOfInitialMessages_IsRejected()
    {
        // Many initial messages whose total exceeds the window force compaction.
        var initial = Enumerable.Range(0, 30)
            .Select(index => new RuntimeMessage(
                index % 2 == 0 ? RuntimeMessageRole.User : RuntimeMessageRole.Assistant,
                [new RuntimeTextItem($"{index}:" + new string('z', 600))]))
            .ToArray();
        var options = SmallOptions(RuntimeContextTruncationPolicy.FailClosedOnInitialMessages);
        var model = new ScriptedModelClient(_ => Text("unreachable"));

        var result = await RunLoopAsync(new RuntimeAgentLoop(model), Request("x", options) with { InitialMessages = initial });

        Assert.Equal("context_truncation_rejected", result.Error!.Code);
        Assert.Contains("deterministic_local_compaction", result.Error.Message, StringComparison.Ordinal);
        var compacted = Assert.Single(result.ContextEvents, static e => e.Kind == RuntimeContextEventKind.ContextCompacted);
        Assert.NotEmpty(compacted.ItemIds);

        var allowed = await RunLoopAsync(
            new RuntimeAgentLoop(new ScriptedModelClient(_ => Text("ok"))),
            Request("x", SmallOptions(RuntimeContextTruncationPolicy.Allow)) with { InitialMessages = initial });
        Assert.Equal(RuntimeTurnStatus.Completed, allowed.Status);
    }

    private static RuntimeContextOptions SmallOptions(RuntimeContextTruncationPolicy policy)
        => new()
        {
            MaxContextTokens = 4_000,
            MaxItemTokens = 1_000,
            MaxToolResultTokens = 1_000,
            LargeToolResultTokens = 500,
            SummaryTokens = 500,
            TruncationPolicy = policy
        };

    private static RuntimeAgentLoopRequest Request(string firstMessage, RuntimeContextOptions options)
        => CreateRequest(budget: new RuntimeBudgetSnapshot(3, 3)) with
        {
            InitialMessages = [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(firstMessage)])],
            ContextManager = new RuntimeContextManager(options)
        };

    private sealed class LargeResultExecutor(string text) : IRuntimeToolExecutor
    {
        public ValueTask<RuntimeToolResult> ExecuteAsync(
            RuntimeToolDescriptor descriptor,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
            => ValueTask.FromResult(new RuntimeToolResult(call.InvocationId, text, true));
    }
}
