using System.Runtime.CompilerServices;
using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class AgentRuntimeFacadeTests
{
    [Fact]
    public async Task RunAsync_StreamsOrderedPresentationEventsAndTerminalMetadata()
    {
        var sink = new RecordingSink();
        var runtime = new AgentRuntime(new ScriptedModelClient(_ => Events(
            new RuntimeReasoningDeltaEvent("think"),
            new RuntimeTextDeltaEvent("answer"),
            new RuntimeUsageEvent(new RuntimeUsage(3, 2, 5)),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn))));

        var result = await runtime.RunAsync(
            new RuntimeRunRequest(CreateRequest()),
            sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("answer", result.FinalText);
        Assert.Equal(5, result.Usage.TotalTokens);
        Assert.Equal(
            [
                RuntimePresentationEventType.TurnStarted,
                RuntimePresentationEventType.ContextPrepared,
                RuntimePresentationEventType.StepStarted,
                RuntimePresentationEventType.ReasoningDelta,
                RuntimePresentationEventType.TextDelta,
                RuntimePresentationEventType.UsageUpdated,
                RuntimePresentationEventType.TurnCompleted
            ],
            sink.Events.Select(static runtimeEvent => runtimeEvent.Type));
        Assert.Equal(Enumerable.Range(1, sink.Events.Count).Select(static value => (long)value),
            sink.Events.Select(static runtimeEvent => runtimeEvent.Sequence));
        Assert.Equal(RuntimeTerminationReason.Completed, sink.Events[^1].TerminationReason);
    }

    [Fact]
    public async Task RunAsync_InterventionDenialPreventsToolExecutionAndCommitsObservation()
    {
        var executor = new RecordingToolExecutor();
        var runtime = new AgentRuntime(new ScriptedModelClient(
            _ => Events(
                new RuntimeToolCallEvent(ToolCall("call-1", "read_state")),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)),
            _ => Events(
                new RuntimeTextDeltaEvent("blocked handled"),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn))));
        var request = CreateRequest(
            [ToolDescriptor("read_state")],
            executor,
            new DenyAuthorization());

        var result = await runtime.RunAsync(
            new RuntimeRunRequest(request),
            eventSink: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("blocked handled", result.FinalText);
        Assert.Empty(executor.Calls);
        var observation = Assert.IsType<RuntimeToolResultItem>(result.History
            .Single(static message => message.Role == RuntimeMessageRole.Tool)
            .Items.Single());
        Assert.Equal(RuntimeErrorCategory.PolicyDenied, observation.Result.Error?.Category);
    }

    [Fact]
    public async Task RunAsync_RequiredToolContinuesUntilSuccessfulObservation()
    {
        var executor = new RecordingToolExecutor();
        var model = new ScriptedModelClient(
            _ => Events(
                new RuntimeTextDeltaEvent("draft"),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)),
            _ => Events(
                new RuntimeToolCallEvent(ToolCall("call-verify", "verify_state")),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)),
            _ => Events(
                new RuntimeTextDeltaEvent("verified final"),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)));
        var runtime = new AgentRuntime(model);
        var request = CreateRequest(
            [ToolDescriptor("verify_state")],
            executor,
            authorization: null,
            parameters: new RuntimeModelParameters(RequiredToolName: "verify_state"));

        var result = await runtime.RunAsync(
            new RuntimeRunRequest(request),
            eventSink: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("verified final", result.FinalText);
        Assert.Single(executor.Calls);
        Assert.True(result.Turn.Progress.RequiredToolSatisfied);
        Assert.Equal(1, result.Turn.Progress.ContinuationCount);
        Assert.Equal("verify_state", model.Requests[1].Parameters.RequiredToolName);
    }

    [Fact]
    public async Task RunAsync_CancellationReturnsCancelledResultAndTerminalEvent()
    {
        var model = new BlockingModelClient();
        var sink = new RecordingSink();
        var runtime = new AgentRuntime(model);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var run = runtime.RunAsync(new RuntimeRunRequest(CreateRequest()), sink, cancellation.Token);
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();
        var result = await run;

        Assert.Equal(RuntimeTurnStatus.Cancelled, result.Status);
        Assert.Equal(RuntimeTerminationReason.Cancelled, result.TerminationReason);
        Assert.Equal(RuntimePresentationEventType.TurnCancelled, sink.Events[^1].Type);
    }

    private static RuntimeAgentLoopRequest CreateRequest(
        IReadOnlyList<RuntimeToolDescriptor>? tools = null,
        IRuntimeToolExecutor? executor = null,
        IRuntimeToolAuthorization? authorization = null,
        RuntimeModelParameters? parameters = null)
        => new(
            new RuntimeSessionId("facade-session"),
            new RuntimeTurnId("facade-turn"),
            "test facade",
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("start")])],
            tools ?? [],
            parameters ?? new RuntimeModelParameters(),
            new RuntimePolicySnapshot("policy-v1", "readonly"),
            new RuntimeEnvironmentSnapshot("local", "workspace", "sha256:test"),
            new RuntimeBudgetSnapshot(4, 2, maxContinuations: 2),
            CreatedAt: DateTimeOffset.UnixEpoch)
        {
            ToolExecutor = executor,
            ToolAuthorization = authorization
        };

    private static RuntimeToolDescriptor ToolDescriptor(string name)
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        return new RuntimeToolDescriptor(
            name,
            "1",
            $"Execute {name}.",
            schema.RootElement.Clone(),
            RuntimeToolSideEffect.ReadOnly,
            RuntimeToolIdempotency.Idempotent);
    }

    private static RuntimeToolCall ToolCall(string invocationId, string name)
    {
        using var arguments = JsonDocument.Parse("{}");
        return new RuntimeToolCall(
            new RuntimeInvocationId(invocationId),
            name,
            arguments.RootElement.Clone());
    }

    private static async IAsyncEnumerable<RuntimeModelStreamEvent> Events(
        params RuntimeModelStreamEvent[] events)
    {
        foreach (var runtimeEvent in events)
        {
            await Task.Yield();
            yield return runtimeEvent;
        }
    }

    private sealed class ScriptedModelClient(
        params Func<RuntimeModelRequest, IAsyncEnumerable<RuntimeModelStreamEvent>>[] scripts)
        : IRuntimeModelClient
    {
        private int _index;

        public List<RuntimeModelRequest> Requests { get; } = [];

        public IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
            RuntimeModelRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            var stream = scripts[Interlocked.Increment(ref _index) - 1](request);
            return WithCancellation(stream, ct);
        }

        private static async IAsyncEnumerable<RuntimeModelStreamEvent> WithCancellation(
            IAsyncEnumerable<RuntimeModelStreamEvent> source,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var runtimeEvent in source.WithCancellation(ct))
            {
                yield return runtimeEvent;
            }
        }
    }

    private sealed class RecordingToolExecutor : IRuntimeToolExecutor
    {
        public List<RuntimeToolCall> Calls { get; } = [];

        public ValueTask<RuntimeToolResult> ExecuteAsync(
            RuntimeToolDescriptor descriptor,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(call);
            return ValueTask.FromResult(new RuntimeToolResult(call.InvocationId, "ok", true));
        }
    }

    private sealed class DenyAuthorization : IRuntimeToolAuthorization
    {
        public ValueTask<RuntimeToolAuthorizationDecision> AuthorizeAsync(
            RuntimeToolDescriptor descriptor,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
            => ValueTask.FromResult(RuntimeToolAuthorizationDecision.Deny("host blocked tool"));
    }

    private sealed class RecordingSink : IRuntimeEventSink
    {
        public List<RuntimePresentationEvent> Events { get; } = [];

        public ValueTask OnEventAsync(RuntimePresentationEvent runtimeEvent, CancellationToken ct)
        {
            Events.Add(runtimeEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingModelClient : IRuntimeModelClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
            RuntimeModelRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }
    }
}
