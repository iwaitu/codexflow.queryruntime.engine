using System.Runtime.CompilerServices;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

/// <summary>
/// The context budget is counted with <see cref="RuntimeTokenEstimator"/> (UTF-8 bytes / 4). For text a tokenizer splits finely - Chinese,
/// and above all JSON with Chinese written as \uXXXX - that is two to three times too few, and a run whose estimate says 116,631 tokens
/// was refused by the provider at 262,145 (audited in a live run: nine 42 KB tool results, never compacted). The provider reports the real
/// count of every Step; the loop learns the ratio from it and budgets with it, so a model whose text the estimator undercounts is
/// compacted in time, and one it counts right is unaffected.
/// </summary>
public sealed class RuntimeContextCalibrationTests
{
    private static RuntimeContextOptions Window(int tokens) => RuntimeContextOptions.ForModelWindow(tokens);

    private static RuntimeHistorySnapshot ChatOf(int messages, int bytesEach)
    {
        var list = new List<RuntimeMessage>();
        for (var i = 0; i < messages; i++)
        {
            list.Add(new RuntimeMessage(
                i % 2 == 0 ? RuntimeMessageRole.User : RuntimeMessageRole.Assistant,
                [new RuntimeTextItem(new string((char)('a' + i % 26), bytesEach))]));
        }

        return RuntimeHistory.Create(list, 0).Snapshot();
    }

    // ── the manager ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Prepare_WithAnEstimateScale_CompactsAHistoryThatTheUnscaledEstimateWouldKeepWhole()
    {
        var manager = new RuntimeContextManager(Window(4_000));
        var history = ChatOf(messages: 12, bytesEach: 800);   // about 2,450 estimated tokens: fits 4,000

        var plain = manager.Prepare(history, "objective", null, 0, 1.0);
        var scaled = manager.Prepare(history, "objective", null, 0, 3.0);   // the provider counted three times as many

        Assert.False(plain.Compacted);
        Assert.True(scaled.Compacted);
        Assert.True(scaled.Messages.Count < plain.Messages.Count);
    }

    [Fact]
    public void Prepare_NeverLoosensTheBudget_WhateverScaleIsPassed()
    {
        var manager = new RuntimeContextManager(Window(2_000));
        var history = ChatOf(messages: 12, bytesEach: 800);   // does not fit 2,000

        Assert.True(manager.Prepare(history, "objective", null, 0, 1.0).Compacted);
        Assert.True(manager.Prepare(history, "objective", null, 0, 0.25).Compacted);
        Assert.True(manager.Prepare(history, "objective", null, 0, double.NaN).Compacted == manager.Prepare(history, "objective", null, 0, 1.0).Compacted);
        Assert.True(manager.Prepare(history, "objective", null, 0, double.PositiveInfinity).Compacted);
    }

    [Fact]
    public void PrepareWithoutAScale_IsExactlyWhatItWas()
    {
        var manager = new RuntimeContextManager(Window(4_000));
        var history = ChatOf(messages: 12, bytesEach: 800);

        var four = manager.Prepare(history, "objective", null, 0);
        var five = manager.Prepare(history, "objective", null, 0, 1.0);

        Assert.Equal(four.EstimatedTokens, five.EstimatedTokens);
        Assert.Equal(four.Compacted, five.Compacted);
        Assert.Equal(four.Messages.Count, five.Messages.Count);
    }

    // ── the loop ──────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class ReportingModel(double providerTokensPerEstimatedToken) : IRuntimeModelClient
    {
        private int _calls;

        public IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(RuntimeModelRequest request, CancellationToken ct = default)
        {
            var step = Interlocked.Increment(ref _calls);
            return step == 1 ? FirstStep(request) : SecondStep();
        }

        private async IAsyncEnumerable<RuntimeModelStreamEvent> FirstStep(RuntimeModelRequest request)
        {
            // What the engine estimated for this request, times what the provider is said to count per estimated token.
            var estimated = request.Messages.Sum(RuntimeTokenEstimator.Estimate) + request.Tools.Sum(RuntimeTokenEstimator.Estimate);
            var reported = Math.Max(1, (long)(estimated * providerTokensPerEstimatedToken));
            yield return new RuntimeToolCallEvent(new RuntimeToolCall(new RuntimeInvocationId("call-1"), "read_file", System.Text.Json.JsonDocument.Parse("{\"path\":\"a.txt\"}").RootElement.Clone()));
            yield return new RuntimeUsageEvent(new RuntimeUsage(reported, 5, reported + 5));
            yield return new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall);
            await Task.CompletedTask;
        }

        private static async IAsyncEnumerable<RuntimeModelStreamEvent> SecondStep()
        {
            yield return new RuntimeTextDeltaEvent("done");
            yield return new RuntimeUsageEvent(new RuntimeUsage(10, 1, 11));
            yield return new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn);
            await Task.CompletedTask;
        }
    }

    private sealed class Reader : IRuntimeToolExecutor
    {
        public ValueTask<RuntimeToolResult> ExecuteAsync(RuntimeToolDescriptor descriptor, RuntimeToolCall call, RuntimeToolExecutionContext context, CancellationToken ct)
            => ValueTask.FromResult(new RuntimeToolResult(call.InvocationId, new string('r', 2_000), true));
    }

    private static RuntimeToolDescriptor ReadFileDescriptor()
    {
        using var schema = System.Text.Json.JsonDocument.Parse("{\"type\":\"object\"}");
        return new RuntimeToolDescriptor("read_file", "1", "Execute read_file.", schema.RootElement.Clone(),
            RuntimeToolSideEffect.ReadOnly, RuntimeToolIdempotency.Idempotent);
    }

    private static async Task<RuntimeAgentLoopResult> RunAsync(double providerTokensPerEstimatedToken)
    {
        var messages = new List<RuntimeMessage>();
        for (var i = 0; i < 10; i++)
        {
            messages.Add(new RuntimeMessage(
                i % 2 == 0 ? RuntimeMessageRole.User : RuntimeMessageRole.Assistant,
                [new RuntimeTextItem(new string((char)('a' + i), 800))]));
        }

        var request = new RuntimeAgentLoopRequest(
            new RuntimeSessionId("session-1"),
            new RuntimeTurnId("turn-1"),
            "test objective",
            messages,
            [ReadFileDescriptor()],
            new RuntimeModelParameters(),
            new RuntimePolicySnapshot("policy-v1", "test"),
            new RuntimeEnvironmentSnapshot("local", "workspace", "sha256:test"),
            new RuntimeBudgetSnapshot(5, 5),
            CreatedAt: DateTimeOffset.UnixEpoch)
        {
            ToolExecutor = new Reader(),
            ContextManager = new RuntimeContextManager(Window(4_000))
        };

        return await new RuntimeAgentLoop(new ReportingModel(providerTokensPerEstimatedToken)).RunAsync(request, null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AProviderThatCountsThreeTimesTheEstimate_MakesTheNextStepCompactInTime()
    {
        var result = await RunAsync(providerTokensPerEstimatedToken: 3.0);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(2, result.Turn.Steps.Count);
        Assert.False(result.Turn.Steps[0].Context.PreparedContext!.Compacted);   // nothing was known yet
        Assert.True(result.Turn.Steps[1].Context.PreparedContext!.Compacted);    // the first Step taught the ratio
    }

    [Fact]
    public async Task AProviderThatAgreesWithTheEstimate_ChangesNothing()
    {
        var result = await RunAsync(providerTokensPerEstimatedToken: 1.0);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.False(result.Turn.Steps[1].Context.PreparedContext!.Compacted);
    }

    [Fact]
    public async Task AProviderThatCountsFewerTokensThanTheEstimate_NeverLoosensAnything()
    {
        var result = await RunAsync(providerTokensPerEstimatedToken: 0.2);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.False(result.Turn.Steps[1].Context.PreparedContext!.Compacted);
    }
}
