using CodexFlow.QueryRuntime.IntegrationTests.Infrastructure;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;

namespace CodexFlow.QueryRuntime.IntegrationTests;

public sealed class RouteTwoProtocolRealLlmTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ProtocolAdapter_StreamsSimpleTypedResponseFromRealProvider()
    {
        if (!RealQueryRuntimeTestHost.TryCreate(out var host, out var reason))
        {
            output.WriteLine(reason);
            Assert.Skip(reason);
        }

        var liveHost = host!;
        using (liveHost)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var model = liveHost.CreateRuntimeModelClient();
            var request = new RuntimeModelRequest(
                new RuntimeSessionId("route-two-live-session"),
                new RuntimeTurnId("route-two-live-turn"),
                new RuntimeStepId("route-two-live-step"),
                [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("请只回答 V2_E2E_OK")])],
                [],
                new RuntimeModelParameters(MaxOutputTokens: 64),
                HistoryVersion: 0);
            var validator = new RuntimeModelStreamValidator();
            var text = new List<string>();

            await foreach (var runtimeEvent in model.StreamAsync(request, timeout.Token))
            {
                validator.Apply(runtimeEvent);
                if (runtimeEvent is RuntimeTextDeltaEvent delta)
                {
                    text.Add(delta.Text);
                }
            }
            validator.Complete();

            var finalText = string.Concat(text);
            output.WriteLine(
                "model={0}; events={1}; stop={2}; text={3}",
                liveHost.Settings.Model,
                validator.EventCount,
                validator.StopReason?.ToString() ?? "none",
                finalText);
            Assert.Contains("V2_E2E_OK", finalText, StringComparison.Ordinal);
            Assert.NotEqual(RuntimeModelStopReason.Error, validator.StopReason);
        }
    }

    [Fact]
    public async Task C2AgentLoop_CompletesSimpleTurnThroughRealProvider()
    {
        if (!RealQueryRuntimeTestHost.TryCreate(out var host, out var reason))
        {
            output.WriteLine(reason);
            Assert.Skip(reason);
        }

        var liveHost = host!;
        using (liveHost)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var loop = new RuntimeAgentLoop(liveHost.CreateRuntimeModelClient());
            var result = await loop.RunAsync(
                new RuntimeAgentLoopRequest(
                    new RuntimeSessionId("route-two-c2-live-session"),
                    new RuntimeTurnId("route-two-c2-live-turn"),
                    "validate the C2 phase loop",
                    [new RuntimeMessage(
                        RuntimeMessageRole.User,
                        [new RuntimeTextItem("请只回答 C2_LOOP_E2E_OK")])],
                    [],
                    new RuntimeModelParameters(MaxOutputTokens: 64),
                    new RuntimePolicySnapshot("live-v1", "readonly"),
                    new RuntimeEnvironmentSnapshot("local", "live", "sha256:live"),
                    new RuntimeBudgetSnapshot(1, 0),
                    CreatedAt: DateTimeOffset.UnixEpoch),
                ct: timeout.Token);

            output.WriteLine(
                "model={0}; status={1}; stop={2}; reasoningLength={3}; text={4}",
                liveHost.Settings.Model,
                result.Status,
                result.Turn.Progress.LastModelStopReason?.ToString() ?? "none",
                result.Turn.Steps[0].Output?.Reasoning.Length ?? 0,
                result.FinalText);
            Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
            Assert.Contains("C2_LOOP_E2E_OK", result.FinalText, StringComparison.Ordinal);
            if (liveHost.Settings.Model.Contains("qwen3.8", StringComparison.OrdinalIgnoreCase))
            {
                Assert.False(string.IsNullOrWhiteSpace(result.Turn.Steps[0].Output?.Reasoning));
                Assert.Equal("C2_LOOP_E2E_OK", result.FinalText.Trim());
            }
        }
    }
}
