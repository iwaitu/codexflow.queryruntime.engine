using System.Runtime.CompilerServices;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

/// <summary>
/// Q6 durable reservation precondition: a second StepPrepared checkpoint for the
/// same sampling Step must be accepted by the emitter ordering, the JSON store and
/// recovery, so a turn-level repair reservation can be persisted before the repair
/// request without a new checkpoint kind.
/// </summary>
public sealed class RuntimeToolCallRepairCheckpointProbeTests
{
    [Fact]
    public async Task RepeatedStepPreparedForSameStep_IsStoredValidatedAndResumable()
    {
        var sink = new InMemoryRuntimeCheckpointSink();
        var request = CreateRequest() with
        {
            CheckpointSink = sink,
            RecoveryCompatibilityId = "probe-h1:v1"
        };
        var result = await new RuntimeAgentLoop(new FixedTextModel("source")).RunAsync(
            request,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);

        var prepared = Assert.Single(sink.Checkpoints, static value => value.Kind == RuntimeCheckpointKind.StepPrepared);
        var history = RuntimeHistory.RestoreCanonical(
            prepared.CanonicalHistory,
            prepared.Session.HistoryVersion,
            prepared.NextHistoryMessageSequence,
            blobs: prepared.HistoryBlobs);
        var repeated = RuntimeCheckpointDocument.Capture(
            prepared.Sequence + 1,
            prepared.Attempt,
            RuntimeCheckpointKind.StepPrepared,
            prepared.Request.ToLoopRequest(),
            prepared.Session,
            history.Snapshot(),
            prepared.FinalText,
            prepared.CreatedAt);

        var directory = Path.Combine(Path.GetTempPath(), "qre-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RuntimeJsonCheckpointStore(directory);
            await store.SaveAsync(prepared, TestContext.Current.CancellationToken);
            await store.SaveAsync(repeated, TestContext.Current.CancellationToken);
            var reread = RuntimeJsonCheckpointStore.Read(store.CheckpointPath);
            Assert.Equal(RuntimeCheckpointKind.StepPrepared, reread.Kind);
            Assert.Equal(prepared.Sequence + 1, reread.Sequence);

            var model = new FixedTextModel("resumed");
            var resumed = await new RuntimeAgentLoop(model).ResumeAsync(
                reread.Request.ToLoopRequest() with
                {
                    Attempt = RuntimeRunAttempt.Resume(reread, "attempt-probe-resumed"),
                    CheckpointSink = new InMemoryRuntimeCheckpointSink()
                },
                reread,
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeTurnStatus.Completed, resumed.Status);
            Assert.Equal("resumed", resumed.FinalText);
            Assert.Equal(1, model.Calls);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static RuntimeAgentLoopRequest CreateRequest()
        => new(
            new RuntimeSessionId("probe-session"),
            new RuntimeTurnId("probe-turn"),
            "probe objective",
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("start")])],
            [],
            new RuntimeModelParameters(),
            new RuntimePolicySnapshot("policy-v1", "test"),
            new RuntimeEnvironmentSnapshot("local", "workspace", "sha256:test"),
            new RuntimeBudgetSnapshot(3, 3),
            CreatedAt: DateTimeOffset.UnixEpoch);

    private sealed class FixedTextModel(string text) : IRuntimeModelClient
    {
        public int Calls { get; private set; }

        public IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
            RuntimeModelRequest request,
            CancellationToken ct = default)
        {
            Calls++;
            return Stream(text, ct);
        }

        private static async IAsyncEnumerable<RuntimeModelStreamEvent> Stream(
            string text,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new RuntimeTextDeltaEvent(text);
            yield return new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn);
        }
    }
}
