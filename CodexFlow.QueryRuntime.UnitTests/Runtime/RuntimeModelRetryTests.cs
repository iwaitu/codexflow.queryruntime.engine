using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Runtime.RuntimeLoopTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class RuntimeModelRetryTests
{
    private static readonly RuntimeError Transient =
        new(RuntimeErrorCategory.ProviderTransport, "provider_server_error", "503", Retryable: true);

    private static readonly RuntimeRetryPolicy NoJitter = new()
    {
        BaseDelay = TimeSpan.FromSeconds(1),
        MaxDelay = TimeSpan.FromSeconds(5),
        JitterRatio = 0
    };

    [Fact]
    public void ComputeDelay_GrowsExponentiallyAndCapsAtMaxDelay()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), NoJitter.ComputeDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), NoJitter.ComputeDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), NoJitter.ComputeDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(5), NoJitter.ComputeDelay(4));
        Assert.Equal(TimeSpan.FromSeconds(5), NoJitter.ComputeDelay(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => NoJitter.ComputeDelay(0));
    }

    [Fact]
    public void ComputeDelay_AppliesInjectedJitterAndRetryAfter()
    {
        var high = NoJitter with { JitterRatio = 0.2, RandomSource = static () => 1.0 };
        var low = NoJitter with { JitterRatio = 0.2, RandomSource = static () => 0.0 };
        Assert.Equal(TimeSpan.FromMilliseconds(2400), high.ComputeDelay(2));
        Assert.Equal(TimeSpan.FromMilliseconds(1600), low.ComputeDelay(2));
        Assert.Equal(TimeSpan.FromMilliseconds(4800), high.ComputeDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(5), high.ComputeDelay(4));

        Assert.Equal(TimeSpan.FromSeconds(3), NoJitter.ComputeDelay(1, TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(5), NoJitter.ComputeDelay(1, TimeSpan.FromMinutes(1)));
        Assert.Equal(TimeSpan.FromSeconds(2), NoJitter.ComputeDelay(2, TimeSpan.FromMilliseconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(1), (NoJitter with { RespectRetryAfter = false }).ComputeDelay(1, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task RetryPolicy_WaitsWithTimeProviderAndRecordsWarnings()
    {
        var clock = new AutoAdvanceTimeProvider();
        var model = new ScriptedModelClient(
            _ => Throw(new RuntimeModelClientException(Transient)),
            _ => Throw(new RuntimeModelClientException(Transient with { RetryAfter = TimeSpan.FromSeconds(3) })),
            _ => Text("recovered"));

        var result = await RunLoopAsync(
            new RuntimeAgentLoop(model, clock),
            CreateRequest(budget: new RuntimeBudgetSnapshot(1, 0, maxModelRetries: 2)) with { ModelRetryPolicy = NoJitter });

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("recovered", result.FinalText);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)], clock.Delays);
        Assert.Equal(3, result.Turn.Steps[0].ModelAttempts);
        var warnings = result.Turn.Steps[0].Output!.Warnings.Where(static w => w.Code == "model_retry").ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.Contains("attempt 1 failed with provider_server_error", warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("1000 ms", warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("3000 ms", warnings[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoPolicy_RetriesImmediatelyWithoutWarnings()
    {
        var clock = new AutoAdvanceTimeProvider();
        var model = new ScriptedModelClient(
            _ => Throw(new RuntimeModelClientException(Transient)),
            _ => Text("recovered"));

        var result = await RunLoopAsync(
            new RuntimeAgentLoop(model, clock),
            CreateRequest(budget: new RuntimeBudgetSnapshot(1, 0, maxModelRetries: 1)));

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Empty(clock.Delays);
        Assert.Empty(result.Turn.Steps[0].Output!.Warnings);
    }

    [Fact]
    public async Task RetryPolicy_DoesNotRetryAfterEventsOrForTerminalErrors()
    {
        var partial = new ScriptedModelClient(_ => PartialThenThrow(Transient));
        var partialResult = await RunLoopAsync(
            new RuntimeAgentLoop(partial, new AutoAdvanceTimeProvider()),
            CreateRequest(budget: new RuntimeBudgetSnapshot(1, 0, maxModelRetries: 3)) with { ModelRetryPolicy = NoJitter });
        Assert.Equal(RuntimeTurnStatus.Failed, partialResult.Status);
        Assert.Single(partial.Requests);

        var authentication = new RuntimeError(RuntimeErrorCategory.ProviderAuthentication, "provider_authentication_failed", "401");
        var auth = new ScriptedModelClient(_ => Throw(new RuntimeModelClientException(authentication)));
        var clock = new AutoAdvanceTimeProvider();
        var authResult = await RunLoopAsync(
            new RuntimeAgentLoop(auth, clock),
            CreateRequest(budget: new RuntimeBudgetSnapshot(1, 0, maxModelRetries: 3)) with { ModelRetryPolicy = NoJitter });
        Assert.Equal(RuntimeTerminationReason.Error, authResult.TerminationReason);
        Assert.Equal(RuntimeErrorCategory.ProviderAuthentication, authResult.Error!.Category);
        Assert.Single(auth.Requests);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task RetryPolicy_ExhaustedBudgetSurfacesLastError()
    {
        var clock = new AutoAdvanceTimeProvider();
        var model = new ScriptedModelClient(
            _ => Throw(new RuntimeModelClientException(Transient)),
            _ => Throw(new RuntimeModelClientException(Transient)));

        var result = await RunLoopAsync(
            new RuntimeAgentLoop(model, clock),
            CreateRequest(budget: new RuntimeBudgetSnapshot(1, 0, maxModelRetries: 1)) with { ModelRetryPolicy = NoJitter });

        Assert.Equal(RuntimeTerminationReason.Error, result.TerminationReason);
        Assert.Equal("provider_server_error", result.Error!.Code);
        Assert.Equal([TimeSpan.FromSeconds(1)], clock.Delays);
    }

    [Fact]
    public async Task RetryWait_IsCancellable()
    {
        var model = new ScriptedModelClient(_ => Throw(new RuntimeModelClientException(Transient)));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new AutoAdvanceTimeProvider { OnTimer = cts.Cancel, FireTimers = false };

        var result = await new RuntimeAgentLoop(model, clock).RunAsync(
            CreateRequest(budget: new RuntimeBudgetSnapshot(1, 0, maxModelRetries: 2)) with { ModelRetryPolicy = NoJitter },
            ct: cts.Token);

        Assert.Equal(RuntimeTurnStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Policy_ValidationRejectsInvalidValues()
    {
        var loop = new RuntimeAgentLoop(new ScriptedModelClient());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => RunLoopAsync(loop, CreateRequest() with
        {
            ModelRetryPolicy = new RuntimeRetryPolicy { BaseDelay = TimeSpan.FromSeconds(10), MaxDelay = TimeSpan.FromSeconds(1) }
        }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => RunLoopAsync(loop, CreateRequest() with
        {
            ModelRetryPolicy = new RuntimeRetryPolicy { JitterRatio = 2 }
        }));
    }

    private static async IAsyncEnumerable<RuntimeModelStreamEvent> PartialThenThrow(RuntimeError error)
    {
        yield return new RuntimeTextDeltaEvent("partial");
        await Task.Yield();
        throw new RuntimeModelClientException(error);
    }

    /// <summary>Records requested timer delays and completes them at once by advancing the clock.</summary>
    private sealed class AutoAdvanceTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public List<TimeSpan> Delays { get; } = [];

        public Action? OnTimer { get; init; }

        public bool FireTimers { get; init; } = true;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                lock (_sync)
                {
                    Delays.Add(dueTime);
                    _now += dueTime;
                }
                OnTimer?.Invoke();
                if (FireTimers)
                {
                    ThreadPool.QueueUserWorkItem(_ => callback(state));
                }
            }
            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
