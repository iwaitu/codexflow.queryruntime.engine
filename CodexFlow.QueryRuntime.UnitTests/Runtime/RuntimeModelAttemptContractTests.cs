using System.Runtime.CompilerServices;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

/// <summary>
/// P0 contract tests for the optional <see cref="IRuntimeModelAttemptClient"/>
/// capability. They enter through the <see cref="AgentRuntime"/> facade so the
/// PresentingModelClient pass-through is exercised, not only the lower loop.
/// </summary>
public sealed class RuntimeModelAttemptContractTests
{
    [Fact]
    public async Task Facade_RunAsync_PassesAuthoritativeAttemptContextToInnerClient()
    {
        var model = new AttemptAwareModelClient(static (_, _) => Text("done"));
        var runtime = new AgentRuntime(model);

        var result = await runtime.RunAsync(
            new RuntimeRunRequest(CreateRequest() with { Attempt = RuntimeRunAttempt.Create("attempt-run") }),
            eventSink: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        var call = Assert.Single(model.Calls);
        Assert.NotNull(call.Context);
        Assert.Equal(1, call.Context!.RuntimeModelAttemptOrdinal);
        Assert.Equal("attempt-run", call.Context.RunAttemptId);
        Assert.Equal(result.Turn.Steps[0].ModelAttempts, call.Context.RuntimeModelAttemptOrdinal);
        Assert.Equal(0, model.LegacyCalls);
    }

    [Fact]
    public async Task Facade_RunAsync_RetryableAttemptsInSameStepReceiveReducerOrdinals()
    {
        var model = new AttemptAwareModelClient(
            static (_, _) => Throwing(new RuntimeModelClientException(new RuntimeError(
                RuntimeErrorCategory.ProviderTransport,
                "transient",
                "transient",
                Retryable: true))),
            static (_, _) => Text("second try"));
        var runtime = new AgentRuntime(model);

        var result = await runtime.RunAsync(
            new RuntimeRunRequest(CreateRequest(maxModelRetries: 1)),
            eventSink: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal([1, 2], model.Calls.Select(static call => call.Context!.RuntimeModelAttemptOrdinal));
        Assert.All(model.Calls, call => Assert.Equal(model.Calls[0].Request.StepId, call.Request.StepId));
        Assert.Equal(2, Assert.Single(result.Turn.Steps).ModelAttempts);
    }

    [Fact]
    public async Task Facade_ResumeAsync_PassesContextAndOrdinalRestartsFromRecoveredState()
    {
        var sourceCheckpoints = new InMemoryRuntimeCheckpointSink();
        var sourceModel = new AttemptAwareModelClient(static (_, _) => Text("source"));
        await new AgentRuntime(sourceModel).RunAsync(
            new RuntimeRunRequest(CreateRequest() with
            {
                Attempt = RuntimeRunAttempt.Create("attempt-source"),
                CheckpointSink = sourceCheckpoints,
                RecoveryCompatibilityId = "attempt-contract:v1"
            }),
            eventSink: null,
            TestContext.Current.CancellationToken);
        var prepared = Assert.Single(sourceCheckpoints.Checkpoints, static checkpoint =>
            checkpoint.Kind == RuntimeCheckpointKind.StepPrepared);
        var resumedModel = new AttemptAwareModelClient(static (_, _) => Text("resumed"));
        var resumeRequest = prepared.Request.ToLoopRequest() with
        {
            Attempt = RuntimeRunAttempt.Resume(prepared, "attempt-resumed"),
            CheckpointSink = new InMemoryRuntimeCheckpointSink()
        };

        var result = await new AgentRuntime(resumedModel).ResumeAsync(
            new RuntimeResumeRequest(resumeRequest, prepared),
            eventSink: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        var source = Assert.Single(sourceModel.Calls);
        var resumed = Assert.Single(resumedModel.Calls);
        Assert.Equal("attempt-resumed", resumed.Context!.RunAttemptId);
        // The StepPrepared checkpoint persisted ModelAttempts=0, so the recovered
        // Step samples with ordinal 1 again. The ordinal is explanatory only; the
        // run attempt distinguishes the two calls.
        Assert.Equal(source.Context!.RuntimeModelAttemptOrdinal, resumed.Context.RuntimeModelAttemptOrdinal);
        Assert.NotEqual(source.Context.RunAttemptId, resumed.Context.RunAttemptId);
        Assert.Equal(0, resumedModel.LegacyCalls);
    }

    [Fact]
    public async Task Facade_LegacyInnerClientStillWorksAndNeverReceivesContext()
    {
        var model = new LegacyModelClient();
        var result = await new AgentRuntime(model).RunAsync(
            new RuntimeRunRequest(CreateRequest()),
            eventSink: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task Facade_PresentationEventsAreIdenticalForLegacyAndAttemptAwareInner()
    {
        var legacySink = new RecordingSink();
        var awareSink = new RecordingSink();
        await new AgentRuntime(new LegacyModelClient()).RunAsync(
            new RuntimeRunRequest(CreateRequest()),
            legacySink,
            TestContext.Current.CancellationToken);
        await new AgentRuntime(new AttemptAwareModelClient(static (_, _) => Text("done"))).RunAsync(
            new RuntimeRunRequest(CreateRequest()),
            awareSink,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            legacySink.Events.Select(static e => (e.Type, e.Text)),
            awareSink.Events.Select(static e => (e.Type, e.Text)));
        Assert.Single(awareSink.Events, static e => e.Type == RuntimePresentationEventType.StepStarted);
        Assert.Single(awareSink.Events, static e => e.Type == RuntimePresentationEventType.TextDelta);
    }

    [Fact]
    public async Task Facade_CancellationDuringAttemptAwareStreamStillReturnsCancelled()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new AttemptAwareModelClient((_, ct) => Blocking(started, ct));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var run = new AgentRuntime(model).RunAsync(new RuntimeRunRequest(CreateRequest()), null, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();
        var result = await run;

        Assert.Equal(RuntimeTurnStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Loop_DirectLegacyClientUsesOriginalOverload()
    {
        var model = new LegacyModelClient();
        var result = await new RuntimeAgentLoop(model).RunAsync(
            CreateRequest(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public void Context_RejectsNonPositiveOrdinal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeModelAttemptContext(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeModelAttemptContext(-1));
        Assert.Equal(1, new RuntimeModelAttemptContext(1).RuntimeModelAttemptOrdinal);
        Assert.True(typeof(IRuntimeModelClient).IsAssignableFrom(typeof(IRuntimeModelAttemptClient)));
    }

    private static RuntimeAgentLoopRequest CreateRequest(int maxModelRetries = 0)
        => new(
            new RuntimeSessionId("attempt-session"),
            new RuntimeTurnId("attempt-turn"),
            "attempt objective",
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("start")])],
            [],
            new RuntimeModelParameters(Model: "test-model"),
            new RuntimePolicySnapshot("policy-v1", "readonly"),
            new RuntimeEnvironmentSnapshot("local", "workspace", "sha256:test"),
            new RuntimeBudgetSnapshot(3, 4, maxModelRetries: maxModelRetries, maxContinuations: 1),
            CreatedAt: DateTimeOffset.UnixEpoch);

    private static async IAsyncEnumerable<RuntimeModelStreamEvent> Text(string text)
    {
        await Task.Yield();
        yield return new RuntimeTextDeltaEvent(text);
        yield return new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn);
    }

    private static async IAsyncEnumerable<RuntimeModelStreamEvent> Throwing(Exception exception)
    {
        await Task.Yield();
        throw exception;
#pragma warning disable CS0162 // Required to make this an iterator.
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<RuntimeModelStreamEvent> Blocking(
        TaskCompletionSource started,
        [EnumeratorCancellation] CancellationToken ct)
    {
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    private sealed record AttemptCall(RuntimeModelRequest Request, RuntimeModelAttemptContext? Context);

    /// <summary>Independent attempt-aware double; legacy doubles stay unchanged.</summary>
    private sealed class AttemptAwareModelClient(
        params Func<RuntimeModelRequest, CancellationToken, IAsyncEnumerable<RuntimeModelStreamEvent>>[] scripts)
        : IRuntimeModelAttemptClient
    {
        private int _index;

        public List<AttemptCall> Calls { get; } = [];

        public int LegacyCalls { get; private set; }

        public IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
            RuntimeModelRequest request,
            CancellationToken ct = default)
        {
            LegacyCalls++;
            Calls.Add(new AttemptCall(request, null));
            return Next(request, ct);
        }

        public IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
            RuntimeModelRequest request,
            RuntimeModelAttemptContext context,
            CancellationToken ct = default)
        {
            Calls.Add(new AttemptCall(request, context));
            return Next(request, ct);
        }

        private IAsyncEnumerable<RuntimeModelStreamEvent> Next(RuntimeModelRequest request, CancellationToken ct)
            => scripts[Math.Min(Interlocked.Increment(ref _index) - 1, scripts.Length - 1)](request, ct);
    }

    private sealed class LegacyModelClient : IRuntimeModelClient
    {
        public int Calls { get; private set; }

        public IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
            RuntimeModelRequest request,
            CancellationToken ct = default)
        {
            Calls++;
            return Text("done");
        }
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
}
