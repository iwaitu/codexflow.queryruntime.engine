using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Runtime.RuntimeLoopTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

/// <summary>
/// A checkpoint must not grow with the square of the Step count: a Turn of about
/// 36 Steps with a full context window used to exceed the 16 MiB file quota.
/// </summary>
public sealed class RuntimeCheckpointSizeTests
{
    private const int Steps = 12;

    private static RuntimeAgentLoopRequest Request(InMemoryRuntimeCheckpointSink checkpoints)
        => CreateRequest(
            tools: [ToolDescriptor("open_document")],
            executor: new RecordingToolExecutor(),
            budget: new RuntimeBudgetSnapshot(Steps + 2, Steps + 2)) with
        {
            CheckpointSink = checkpoints,
            RecoveryCompatibilityId = "checkpoint-size-test"
        };

    [Fact]
    public async Task Checkpoint_KeepsTheRequestOfTheLastStepOnly()
    {
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var script = Enumerable.Range(0, Steps)
            .Select(index => (Func<RuntimeModelRequest, IAsyncEnumerable<RuntimeModelStreamEvent>>)(_ => Tool(ToolCall($"c{index}", "open_document"))))
            .Append(_ => Text("done"))
            .ToArray();

        var result = await RunLoopAsync(new RuntimeAgentLoop(new ScriptedModelClient(script)), Request(checkpoints));

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        var checkpoint = checkpoints.Checkpoints.Last(static value => value.Kind == RuntimeCheckpointKind.ToolBatchCommitted);
        var steps = checkpoint.Session.ActiveTurn!.Steps;
        Assert.Equal(Steps, steps.Count);
        Assert.All(steps.Take(steps.Count - 1), static step =>
        {
            Assert.Empty(step.Context.ModelRequest.Messages);
            Assert.Null(step.Context.PreparedContext);
            // What recovery and the loop still read from an earlier Step.
            Assert.NotEmpty(step.Context.ModelRequest.Tools);
            Assert.NotNull(step.Output);
            Assert.NotEmpty(step.ToolInvocations!);
        });
        Assert.NotEmpty(steps[^1].Context.ModelRequest.Messages);
        // The running Turn itself still has every request.
        Assert.All(result.Turn.Steps, static step => Assert.NotEmpty(step.Context.ModelRequest.Messages));
    }

    [Fact]
    public async Task Resume_FromACheckpointWithoutEarlierRequestsFinishesTheTurn()
    {
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var crashing = new ScriptedModelClient(
            _ => Tool(ToolCall("c0", "open_document")),
            _ => Tool(ToolCall("c1", "open_document")),
            _ => Tool(ToolCall("c2", "open_document")),
            _ => Throw(new InvalidOperationException("process crashed")));
        await RunLoopAsync(new RuntimeAgentLoop(crashing), Request(checkpoints));
        var checkpoint = checkpoints.Checkpoints.Last(static value => value.Kind == RuntimeCheckpointKind.ToolBatchCommitted);
        Assert.Empty(checkpoint.Session.ActiveTurn!.Steps[0].Context.ModelRequest.Messages);

        var resumedModel = new ScriptedModelClient(_ => Text("resumed"));
        var request = checkpoint.Request.ToLoopRequest() with
        {
            Attempt = RuntimeRunAttempt.Resume(checkpoint),
            CheckpointSink = new InMemoryRuntimeCheckpointSink(),
            ToolExecutor = new RecordingToolExecutor()
        };
        var result = await new RuntimeAgentLoop(resumedModel).ResumeAsync(
            request,
            checkpoint,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("resumed", result.FinalText);
        // The resumed request is rebuilt from the canonical history: all three calls and results.
        var items = Assert.Single(resumedModel.Requests).Messages.SelectMany(static message => message.Items).ToArray();
        Assert.Equal(3, items.OfType<RuntimeToolCallItem>().Count());
        Assert.Equal(3, items.OfType<RuntimeToolResultItem>().Count());
    }
}
