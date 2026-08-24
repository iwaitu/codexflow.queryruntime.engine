using System.Runtime.CompilerServices;
using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class RuntimeAuditTests
{
    [Fact]
    public async Task AgentLoop_EmitsVersionedCausalAuditAndRecordedReplayWithoutExecution()
    {
        var sink = new InMemoryRuntimeAuditSink();
        var model = new ScriptedModelClient(_ => Events(
            new RuntimeTextDeltaEvent("done"),
            new RuntimeUsageEvent(new RuntimeUsage(4, 2, 6)),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)));

        var result = await RunAsync(model, CreateRequest() with { AuditSink = sink });
        var events = sink.Events;

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(
            [
                RuntimeAuditEventKind.TurnStarted,
                RuntimeAuditEventKind.ContextPrepared,
                RuntimeAuditEventKind.ModelRequestPrepared,
                RuntimeAuditEventKind.ModelResponseCommitted,
                RuntimeAuditEventKind.TurnTerminal
            ],
            events.Select(static value => value.Kind));
        Assert.Equal(Enumerable.Range(1, events.Count).Select(static value => (long)value),
            events.Select(static value => value.Sequence));
        Assert.Null(events[0].CausationId);
        for (var index = 1; index < events.Count; index++)
        {
            Assert.Equal(events[index - 1].EventId, events[index].CausationId);
        }

        var replay = RuntimeRecordedReplay.Replay(new RuntimeAuditRecording(
            RuntimeAuditDataMode.SanitizedFixture,
            RuntimeAuditReplayCapability.Recorded,
            events));

        Assert.Equal("done", replay.FinalText);
        Assert.Equal(6, replay.Usage.TotalTokens);
        Assert.Equal(1, replay.TotalSteps);
        Assert.Equal(0, replay.TotalToolCalls);
        Assert.False(replay.ProviderCalls);
        Assert.False(replay.ToolExecutions);
        Assert.Equal(events.Count, replay.EventCount);
        Assert.Equal(64, replay.ReplayDigest.Length);

        var tampered = events.ToArray();
        var terminal = Assert.IsType<RuntimeTurnTerminalAuditPayload>(tampered[^1].Payload);
        tampered[^1] = tampered[^1] with { Payload = terminal with { FinalText = "tampered" } };
        Assert.Equal("audit_terminal_text_mismatch", Assert.Throws<RuntimeAuditReplayException>(() =>
            RuntimeRecordedReplay.Replay(Recording(tampered))).Error.Code);
    }

    [Fact]
    public async Task AgentLoop_AuditsEveryCommittedToolObservationInModelOrder()
    {
        var sink = new InMemoryRuntimeAuditSink();
        var first = Call("call-1", "inspect");
        var second = Call("call-2", "inspect");
        var model = new ScriptedModelClient(
            _ => Events(
                new RuntimeToolCallEvent(first),
                new RuntimeToolCallEvent(second),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)),
            _ => Events(
                new RuntimeTextDeltaEvent("verified"),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)));
        var request = CreateRequest(
            [Descriptor("inspect")],
            new RecordingExecutor()) with { AuditSink = sink };

        var result = await RunAsync(model, request);
        var observations = sink.Events
            .Where(static value => value.Kind == RuntimeAuditEventKind.ToolObservationCommitted)
            .Select(static value => Assert.IsType<RuntimeToolObservationAuditPayload>(value.Payload))
            .ToArray();

        Assert.Equal(["call-1", "call-2"], observations.Select(static value => value.Call.InvocationId.Value));
        var replay = RuntimeRecordedReplay.Replay(new RuntimeAuditRecording(
            RuntimeAuditDataMode.SanitizedFixture,
            RuntimeAuditReplayCapability.Recorded,
            sink.Events));
        Assert.Equal(RuntimeTurnStatus.Completed, replay.Status);
        Assert.Equal(2, replay.TotalSteps);
        Assert.Equal(2, replay.TotalToolCalls);
        Assert.Equal("verified", replay.FinalText);
        Assert.Equal(result.History.Count, replay.CanonicalHistory.Count);

        var reordered = sink.Events.ToArray();
        var observationIndexes = reordered
            .Select((value, index) => (value, index))
            .Where(static value => value.value.Kind == RuntimeAuditEventKind.ToolObservationCommitted)
            .Select(static value => value.index)
            .ToArray();
        var left = reordered[observationIndexes[0]];
        var right = reordered[observationIndexes[1]];
        reordered[observationIndexes[0]] = left with
        {
            Payload = right.Payload,
            InvocationId = right.InvocationId
        };
        reordered[observationIndexes[1]] = right with
        {
            Payload = left.Payload,
            InvocationId = left.InvocationId
        };
        Assert.Equal("audit_tool_observation_invalid", Assert.Throws<RuntimeAuditReplayException>(() =>
            RuntimeRecordedReplay.Replay(Recording(reordered))).Error.Code);
    }

    [Fact]
    public async Task PublicStore_PersistsOnlyAllowListedSummaryAndCannotReplay()
    {
        var canary = $"C6_SECRET_{Guid.NewGuid():N}";
        using var workspace = TemporaryWorkspace.Create();
        await using var store = RuntimeJsonlAuditStore.Create(
            workspace.Path,
            "host-run-secret",
            new RuntimeAuditStoreOptions { DataMode = RuntimeAuditDataMode.PublicRedacted });
        var request = CreateRequest(objective: canary) with
        {
            InitialMessages = [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(canary)])],
            AuditSink = store
        };

        var result = await RunAsync(new ScriptedModelClient(_ => Events(
            new RuntimeTextDeltaEvent(canary),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn))), request);
        await store.DisposeAsync();

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        var persisted = string.Join('\n', Directory.EnumerateFiles(store.RunDirectory, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.DoesNotContain(canary, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("host-run-secret", persisted, StringComparison.Ordinal);
        Assert.StartsWith("public-", store.RunId, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(store.RunDirectory, "blobs")));

        var recording = RuntimeJsonlAuditStore.Read(
            store.AuditFilePath,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeAuditReplayCapability.SummaryOnly, recording.ReplayCapability);
        var error = Assert.Throws<RuntimeAuditReplayException>(() => RuntimeRecordedReplay.Replay(recording));
        Assert.Equal("audit_summary_only", error.Error.Code);
        Assert.All(recording.Events, static value => Assert.IsType<RuntimePublicAuditPayload>(value.Payload));
    }

    [Fact]
    public async Task SanitizedStore_MaterializesAndVerifiesBoundedPayloadBlob()
    {
        using var workspace = TemporaryWorkspace.Create();
        await using var store = RuntimeJsonlAuditStore.Create(
            workspace.Path,
            "fixture-run",
            new RuntimeAuditStoreOptions
            {
                DataMode = RuntimeAuditDataMode.SanitizedFixture,
                InlinePayloadBytes = 256,
                MaxLineBytes = 16 * 1024,
                MaxRunBytes = 2 * 1024 * 1024,
                MaxBlobBytes = 512 * 1024,
                MaxTotalBlobBytes = 1024 * 1024
            });
        var text = "fixture " + new string('x', 8_000);
        var result = await RunAsync(new ScriptedModelClient(_ => Events(
            new RuntimeTextDeltaEvent(text),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn))),
            CreateRequest() with { AuditSink = store });
        await store.DisposeAsync();

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.True(store.BlobBytes > 0);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(store.RunDirectory, "blobs"), "*.json", SearchOption.AllDirectories));
        var recording = RuntimeJsonlAuditStore.Read(store.AuditFilePath, new RuntimeAuditStoreOptions
        {
            DataMode = RuntimeAuditDataMode.SanitizedFixture,
            InlinePayloadBytes = 256,
            MaxLineBytes = 16 * 1024,
            MaxRunBytes = 2 * 1024 * 1024,
            MaxBlobBytes = 512 * 1024,
            MaxTotalBlobBytes = 1024 * 1024
        }, TestContext.Current.CancellationToken);
        var replay = RuntimeRecordedReplay.Replay(recording);
        Assert.Equal(text, replay.FinalText);
        Assert.False(replay.ProviderCalls);
        Assert.False(replay.ToolExecutions);
    }

    [Fact]
    public async Task SanitizedStore_RejectsTamperedBlobDigest()
    {
        using var workspace = TemporaryWorkspace.Create();
        var options = new RuntimeAuditStoreOptions
        {
            DataMode = RuntimeAuditDataMode.SanitizedFixture,
            InlinePayloadBytes = 128,
            MaxLineBytes = 16 * 1024,
            MaxRunBytes = 1024 * 1024,
            MaxBlobBytes = 256 * 1024,
            MaxTotalBlobBytes = 512 * 1024
        };
        await using var store = RuntimeJsonlAuditStore.Create(workspace.Path, "tamper-run", options);
        _ = await RunAsync(new ScriptedModelClient(_ => Events(
            new RuntimeTextDeltaEvent(new string('t', 4_000)),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn))),
            CreateRequest() with { AuditSink = store });
        await store.DisposeAsync();
        var blob = Directory.EnumerateFiles(Path.Combine(store.RunDirectory, "blobs"), "*.json", SearchOption.AllDirectories).First();
        var bytes = File.ReadAllBytes(blob);
        bytes[0] ^= 0x01;
        File.WriteAllBytes(blob, bytes);

        var error = Assert.Throws<InvalidDataException>(() => RuntimeJsonlAuditStore.Read(
            store.AuditFilePath,
            options,
            TestContext.Current.CancellationToken));
        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Replay_RejectsSchemaSequenceAndCausationCorruption()
    {
        var events = CreateMinimalRecording();
        var schema = events.ToArray();
        schema[0] = schema[0] with { SchemaVersion = RuntimeAuditSchema.CurrentVersion + 1 };
        var sequence = events.ToArray();
        sequence[1] = sequence[1] with { Sequence = 7 };
        var causation = events.ToArray();
        causation[1] = causation[1] with { CausationId = new RuntimeAuditEventId("wrong") };

        Assert.Equal("audit_schema_incompatible", Assert.Throws<RuntimeAuditReplayException>(() =>
            RuntimeRecordedReplay.Replay(Recording(schema))).Error.Code);
        Assert.Equal("audit_sequence_invalid", Assert.Throws<RuntimeAuditReplayException>(() =>
            RuntimeRecordedReplay.Replay(Recording(sequence))).Error.Code);
        Assert.Equal("audit_causation_invalid", Assert.Throws<RuntimeAuditReplayException>(() =>
            RuntimeRecordedReplay.Replay(Recording(causation))).Error.Code);
    }

    [Fact]
    public async Task AuditQuotaFailure_FailsClosedBeforeModelSampling()
    {
        using var workspace = TemporaryWorkspace.Create();
        await using var store = RuntimeJsonlAuditStore.Create(workspace.Path, "quota", new RuntimeAuditStoreOptions
        {
            DataMode = RuntimeAuditDataMode.SanitizedFixture,
            MaxEventCount = 1
        });
        var model = new ScriptedModelClient(_ => Events(
            new RuntimeTextDeltaEvent("must not run"),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)));

        var result = await RunAsync(model, CreateRequest() with { AuditSink = store });

        Assert.Equal(RuntimeTurnStatus.Failed, result.Status);
        Assert.Equal(RuntimeTerminationReason.FailClosed, result.TerminationReason);
        Assert.Equal("audit_write_failed", result.Error!.Code);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task InMemoryAuditSink_HasAnExplicitHardEventLimit()
    {
        var model = new ScriptedModelClient(_ => Events(
            new RuntimeTextDeltaEvent("must not run"),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)));

        var result = await RunAsync(
            model,
            CreateRequest() with { AuditSink = new InMemoryRuntimeAuditSink(maxEvents: 1) });

        Assert.Equal(RuntimeTurnStatus.Failed, result.Status);
        Assert.Equal(RuntimeTerminationReason.FailClosed, result.TerminationReason);
        Assert.Equal("audit_write_failed", result.Error!.Code);
        Assert.Empty(model.Requests);
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryRuntimeAuditSink(0));
    }

    [Fact]
    public async Task BestEffortAuditFailure_CompletesWithExplicitWarning()
    {
        var result = await RunAsync(
            new ScriptedModelClient(_ => Events(
                new RuntimeTextDeltaEvent("done"),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn))),
            CreateRequest() with
            {
                AuditSink = new ThrowingAuditSink(),
                AuditFailureMode = RuntimeAuditFailureMode.BestEffort
            });

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.NotEmpty(result.AuditWarnings);
        Assert.All(result.AuditWarnings, static value => Assert.Equal("audit_write_failed", value.Code));
    }

    [Fact]
    public async Task Store_PrunesOldestTerminalRunsBeforeOpeningANewRun()
    {
        using var workspace = TemporaryWorkspace.Create();
        var options = new RuntimeAuditStoreOptions
        {
            DataMode = RuntimeAuditDataMode.SanitizedFixture,
            MaxStoredRuns = 1
        };
        await using var first = RuntimeJsonlAuditStore.Create(workspace.Path, "first", options);
        _ = await RunAsync(
            new ScriptedModelClient(_ => Events(
                new RuntimeTextDeltaEvent("first"),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn))),
            CreateRequest() with { AuditSink = first });
        await first.DisposeAsync();
        Assert.True(Directory.Exists(first.RunDirectory));

        await using var second = RuntimeJsonlAuditStore.Create(workspace.Path, "second", options);

        Assert.False(Directory.Exists(first.RunDirectory));
        Assert.True(Directory.Exists(second.RunDirectory));
    }

    [Fact]
    public async Task StoreRead_RejectsManifestLengthMismatchAndConfiguredJsonDepth()
    {
        using var workspace = TemporaryWorkspace.Create();
        await using var store = RuntimeJsonlAuditStore.Create(
            workspace.Path,
            "bounded-reader",
            new RuntimeAuditStoreOptions { DataMode = RuntimeAuditDataMode.SanitizedFixture });
        _ = await RunAsync(
            new ScriptedModelClient(_ => Events(
                new RuntimeTextDeltaEvent("done"),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn))),
            CreateRequest() with { AuditSink = store });
        await store.DisposeAsync();

        Assert.Throws<InvalidDataException>(() => RuntimeJsonlAuditStore.Read(
            store.AuditFilePath,
            new RuntimeAuditStoreOptions
            {
                DataMode = RuntimeAuditDataMode.SanitizedFixture,
                MaxJsonDepth = 2
            },
            TestContext.Current.CancellationToken));

        File.AppendAllText(store.AuditFilePath, " ");
        var mismatch = Assert.Throws<InvalidDataException>(() => RuntimeJsonlAuditStore.Read(
            store.AuditFilePath,
            ct: TestContext.Current.CancellationToken));
        Assert.Contains("manifest length", mismatch.Message, StringComparison.Ordinal);
    }

    private static RuntimeAgentLoopRequest CreateRequest(
        IReadOnlyList<RuntimeToolDescriptor>? tools = null,
        IRuntimeToolExecutor? executor = null,
        string objective = "audit objective")
        => new(
            new RuntimeSessionId("audit-session"),
            new RuntimeTurnId("audit-turn"),
            objective,
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("start")])],
            tools ?? [],
            new RuntimeModelParameters(),
            new RuntimePolicySnapshot("c6", "readonly"),
            new RuntimeEnvironmentSnapshot("local", "workspace", "c6"),
            new RuntimeBudgetSnapshot(3, 4),
            CreatedAt: DateTimeOffset.UnixEpoch)
        {
            ToolExecutor = executor
        };

    private static Task<RuntimeAgentLoopResult> RunAsync(
        IRuntimeModelClient model,
        RuntimeAgentLoopRequest request)
        => new RuntimeAgentLoop(model).RunAsync(request, null, TestContext.Current.CancellationToken);

    private static RuntimeToolDescriptor Descriptor(string name)
        => new(
            name,
            "1.0.0",
            "Inspect state.",
            JsonSerializer.SerializeToElement(new { type = "object" }),
            RuntimeToolSideEffect.ReadOnly,
            RuntimeToolIdempotency.Idempotent);

    private static RuntimeToolCall Call(string id, string name)
        => new(new RuntimeInvocationId(id), name, JsonSerializer.SerializeToElement(new { }));

    private static async IAsyncEnumerable<RuntimeModelStreamEvent> Events(params RuntimeModelStreamEvent[] events)
    {
        foreach (var value in events)
        {
            await Task.Yield();
            yield return value;
        }
    }

    private static RuntimeAuditEnvelope[] CreateMinimalRecording()
    {
        var session = new RuntimeSessionId("session");
        var turn = new RuntimeTurnId("turn");
        var firstId = new RuntimeAuditEventId("turn:audit:1");
        return
        [
            new RuntimeAuditEnvelope(
                RuntimeAuditSchema.CurrentVersion,
                1,
                firstId,
                DateTimeOffset.UnixEpoch,
                RuntimeAuditEventKind.TurnStarted,
                session,
                turn,
                null,
                null,
                null,
                turn.Value,
                RuntimeAuditSensitivity.Sensitive,
                new RuntimeTurnStartedAuditPayload(
                    "objective",
                    0,
                    [],
                    new RuntimePolicySnapshot("p", "r"),
                    new RuntimeEnvironmentSnapshot("local", null, "e"),
                    new RuntimeBudgetSnapshot(1, 0))),
            new RuntimeAuditEnvelope(
                RuntimeAuditSchema.CurrentVersion,
                2,
                new RuntimeAuditEventId("turn:audit:2"),
                DateTimeOffset.UnixEpoch.AddTicks(1),
                RuntimeAuditEventKind.TurnTerminal,
                session,
                turn,
                null,
                null,
                firstId,
                turn.Value,
                RuntimeAuditSensitivity.Sensitive,
                new RuntimeTurnTerminalAuditPayload(
                    RuntimeTurnStatus.Completed,
                    RuntimeTerminationReason.Completed,
                    null,
                    "done",
                    RuntimeUsageTotals.Empty,
                    0,
                    0,
                    0,
                    0,
                    []))
        ];
    }

    private static RuntimeAuditRecording Recording(RuntimeAuditEnvelope[] events)
        => new(RuntimeAuditDataMode.SanitizedFixture, RuntimeAuditReplayCapability.Recorded, events);

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
            return WithCancellation(scripts[Interlocked.Increment(ref _index) - 1](request), ct);
        }

        private static async IAsyncEnumerable<RuntimeModelStreamEvent> WithCancellation(
            IAsyncEnumerable<RuntimeModelStreamEvent> source,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var value in source.WithCancellation(ct))
            {
                yield return value;
            }
        }
    }

    private sealed class RecordingExecutor : IRuntimeToolExecutor
    {
        public ValueTask<RuntimeToolResult> ExecuteAsync(
            RuntimeToolDescriptor descriptor,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
            => ValueTask.FromResult(new RuntimeToolResult(call.InvocationId, $"ok:{call.InvocationId.Value}", true));
    }

    private sealed class ThrowingAuditSink : IRuntimeAuditSink
    {
        public ValueTask OnEventAsync(RuntimeAuditEnvelope auditEvent, CancellationToken ct)
            => ValueTask.FromException(new IOException("audit unavailable"));
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(string path) => Path = path;
        public string Path { get; }

        public static TemporaryWorkspace Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qre-c6-audit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryWorkspace(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
