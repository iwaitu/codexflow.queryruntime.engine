using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class RuntimeContextTests
{
    [Fact]
    public void History_IsCanonicalVersionedAndDefensivelySnapshotted()
    {
        var sourceItems = new List<RuntimeItem> { new RuntimeTextItem("initial") };
        var history = RuntimeHistory.Create(
            [new RuntimeMessage(RuntimeMessageRole.User, sourceItems)],
            initialVersion: 7);
        sourceItems.Add(new RuntimeTextItem("mutated"));

        var committed = history.AppendBatch(
            [new RuntimeMessage(RuntimeMessageRole.Assistant, [new RuntimeTextItem("next")])]);
        var snapshot = history.Snapshot();

        Assert.Equal(8, committed);
        Assert.Equal(8, snapshot.Version);
        Assert.Equal(2, snapshot.Messages.Count);
        Assert.Single(snapshot.Messages[0].Message.Items);
        Assert.Equal("h7:m0:i0", snapshot.Messages[0].ItemIds[0].Value);
        Assert.Equal("h8:m1:i0", snapshot.Messages[1].ItemIds[0].Value);
    }

    [Fact]
    public void History_NormalizesDuplicateSystemOrphanAndUnsupportedItems()
    {
        var history = RuntimeHistory.Create(
        [
            new RuntimeMessage(RuntimeMessageRole.System, [new RuntimeTextItem("same")]),
            new RuntimeMessage(RuntimeMessageRole.System, [new RuntimeTextItem("same")]),
            new RuntimeMessage(RuntimeMessageRole.Tool,
                [new RuntimeToolResultItem(new RuntimeToolResult(new RuntimeInvocationId("orphan"), "bad", true))]),
            new RuntimeMessage(RuntimeMessageRole.User, [new UnsupportedTestItem("private")])
        ], 0);

        var messages = history.ToMessages();
        var text = messages.SelectMany(static message => message.Items).OfType<RuntimeTextItem>()
            .Select(static item => item.Text).ToArray();
        var events = history.DrainEvents();

        Assert.Equal(3, messages.Count);
        Assert.Single(text, static value => value == "same");
        Assert.Contains(text, static value => value.Contains("orphan tool result omitted", StringComparison.Ordinal));
        Assert.Contains(text, static value => value.Contains("unsupported runtime item omitted", StringComparison.Ordinal));
        Assert.Contains(events, static item => item.Code == "duplicate_system_fragment_omitted");
        Assert.Contains(events, static item => item.Code == "orphan_tool_result_normalized");
        Assert.Contains(events, static item => item.Code == "unsupported_item_normalized");
    }

    [Fact]
    public void History_ReplacesLargeToolOutputWithBoundedTextAndDigestReference()
    {
        var options = new RuntimeContextOptions
        {
            MaxContextTokens = 200,
            MaxItemTokens = 100,
            MaxToolResultTokens = 80,
            LargeToolResultTokens = 20,
            SummaryTokens = 40,
            RecentTrajectoryMessages = 4
        };
        var call = Call("call-1", "read_file");
        var large = new string('x', 800);
        var history = RuntimeHistory.Create(
        [
            new RuntimeMessage(RuntimeMessageRole.Assistant, [new RuntimeToolCallItem(call)]),
            new RuntimeMessage(RuntimeMessageRole.Tool,
                [new RuntimeToolResultItem(new RuntimeToolResult(call.InvocationId, large, true))])
        ], 0, options);

        var result = Assert.IsType<RuntimeToolResultItem>(history.ToMessages()[1].Items[0]).Result;
        var artifact = Assert.Single(result.Artifacts!);
        var blob = history.Snapshot().Blobs[artifact.Digest!];

        Assert.True(result.Details?.Truncated ?? true);
        Assert.Contains("large tool output replaced", result.Text, StringComparison.Ordinal);
        Assert.StartsWith("runtime-history://sha256/", artifact.Path, StringComparison.Ordinal);
        Assert.Equal(64, artifact.Digest!.Length);
        Assert.Equal(large, System.Text.Encoding.UTF8.GetString(blob.Data.Span));
        Assert.Contains(history.DrainEvents(), static item => item.Code == "large_tool_result_replaced");
    }

    [Fact]
    public void ContextPrepare_BelowBudgetPreservesNormalizedHistoryWithoutCompaction()
    {
        var history = RuntimeHistory.Create(
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("hello")])],
            3);

        var prepared = new RuntimeContextManager().Prepare(history.Snapshot(), "hello", null);

        Assert.False(prepared.Compacted);
        Assert.Equal(3, prepared.HistoryVersion);
        Assert.Single(prepared.Messages);
        Assert.Empty(prepared.OmittedItemIds);
        Assert.Equal(RuntimeTokenEstimator.Version, prepared.EstimatorVersion);
    }

    [Fact]
    public void ContextPrepare_ReservesToolSchemaTokensInsideHardBudget()
    {
        var options = SmallOptions();
        var history = RuntimeHistory.Create(
        [
            new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(new string('u', 300))]),
            new RuntimeMessage(RuntimeMessageRole.Assistant, [new RuntimeTextItem(new string('a', 300))]),
            new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("latest")])
        ], 0, options);

        var prepared = new RuntimeContextManager(options).Prepare(
            history.Snapshot(),
            "stay bounded",
            null,
            reservedToolTokens: 90);

        Assert.True(prepared.Compacted);
        Assert.Equal(90, prepared.ReservedToolTokens);
        Assert.True(prepared.EstimatedTokens <= options.MaxContextTokens);
        Assert.Contains(prepared.Messages,
            static message => message.Items.OfType<RuntimeTextItem>().Any(item => item.Text == "latest"));
    }

    [Fact]
    public void ContextPrepare_CompactsProjectionButPreservesCanonicalHistoryAndToolPairing()
    {
        var options = SmallOptions();
        var call = Call("call-1", "read_file");
        var initial = new List<RuntimeMessage>
        {
            new(RuntimeMessageRole.System, [new RuntimeTextItem("constraint")]),
            new(RuntimeMessageRole.User, [new RuntimeTextItem("old question " + new string('a', 200))]),
            new(RuntimeMessageRole.Assistant, [new RuntimeToolCallItem(call)]),
            new(RuntimeMessageRole.Tool,
                [new RuntimeToolResultItem(new RuntimeToolResult(call.InvocationId, "finding " + new string('b', 200), true))])
        };
        for (var i = 0; i < 12; i++)
        {
            initial.Add(new RuntimeMessage(
                i % 2 == 0 ? RuntimeMessageRole.User : RuntimeMessageRole.Assistant,
                [new RuntimeTextItem($"trajectory-{i} " + new string('z', 180))]));
        }
        initial.Add(new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("latest user request")]));
        var history = RuntimeHistory.Create(initial, 4, options);
        var before = history.ToMessages();

        var prepared = new RuntimeContextManager(options).Prepare(
            history.Snapshot(),
            "complete the task",
            "read_file");
        var after = history.ToMessages();

        Assert.True(prepared.Compacted);
        Assert.Equal(
            before.Select(static message => JsonSerializer.Serialize(message)),
            after.Select(static message => JsonSerializer.Serialize(message)));
        Assert.True(prepared.EstimatedTokens <= options.MaxContextTokens);
        Assert.Contains(prepared.Messages,
            static message => message.Items.OfType<RuntimeTextItem>().Any(item => item.Text.Contains("latest user request", StringComparison.Ordinal)));
        var visibleCalls = prepared.Messages.SelectMany(static message => message.Items).OfType<RuntimeToolCallItem>()
            .Select(static item => item.Call.InvocationId.Value).ToHashSet();
        var visibleResults = prepared.Messages.SelectMany(static message => message.Items).OfType<RuntimeToolResultItem>()
            .Select(static item => item.Result.InvocationId.Value).ToHashSet();
        Assert.True(visibleCalls.SetEquals(visibleResults));
        Assert.Contains(prepared.Events, static item => item.Kind == RuntimeContextEventKind.ContextCompacted);
        Assert.NotEmpty(prepared.ReplacedItemIds);
        Assert.StartsWith("[C5 deterministic local summary", Assert.IsType<RuntimeTextItem>(prepared.Messages[0].Items[0]).Text);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(25)]
    public void ContextPrepare_ThreeTenTwentyFiveStepTrajectoryRemainsBounded(int steps)
    {
        var options = SmallOptions();
        var history = RuntimeHistory.Create(
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("start")])],
            0,
            options);
        for (var step = 0; step < steps; step++)
        {
            var call = Call($"call-{step}", "read_file");
            history.AppendBatch(
            [
                new RuntimeMessage(RuntimeMessageRole.Assistant, [new RuntimeToolCallItem(call)]),
                new RuntimeMessage(RuntimeMessageRole.Tool,
                    [new RuntimeToolResultItem(new RuntimeToolResult(
                        call.InvocationId,
                        $"result-{step} " + new string('r', 300),
                        true))])
            ]);
            var prepared = new RuntimeContextManager(options).Prepare(history.Snapshot(), "bounded trajectory", null);
            Assert.True(prepared.EstimatedTokens <= options.MaxContextTokens);
            Assert.All(
                prepared.Messages.SelectMany(static message => message.Items),
                item => Assert.True(RuntimeTokenEstimator.Estimate(item) <= options.MaxItemTokens + 32));
        }

        Assert.Equal(steps, history.Version);
        Assert.Equal(1 + steps * 2, history.ToMessages().Count);
    }

    [Fact]
    public async Task AgentLoop_UsesPreparedProjectionAndExposesContextEvents()
    {
        var options = SmallOptions();
        var messages = Enumerable.Range(0, 20)
            .Select(index => new RuntimeMessage(
                index % 2 == 0 ? RuntimeMessageRole.User : RuntimeMessageRole.Assistant,
                [new RuntimeTextItem($"message-{index} " + new string('x', 200))]))
            .ToArray();
        var model = new CapturingModel();
        var sink = new RecordingContextSink();
        var request = new RuntimeAgentLoopRequest(
            new RuntimeSessionId("session"),
            new RuntimeTurnId("turn"),
            "finish",
            messages,
            [],
            new RuntimeModelParameters(),
            new RuntimePolicySnapshot("c5", "none"),
            new RuntimeEnvironmentSnapshot("local", "workspace", "c5"),
            new RuntimeBudgetSnapshot(1, 0))
        {
            ContextManager = new RuntimeContextManager(options),
            ContextEventSink = sink
        };

        var result = await new AgentRuntime(model).RunAsync(
            new RuntimeRunRequest(request),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.True(model.Request!.Messages.Count < messages.Length);
        Assert.Single(result.LoopResult.PreparedContexts);
        Assert.True(result.LoopResult.PreparedContexts[0].Compacted);
        Assert.Contains(sink.Events, static item => item.Kind == RuntimeContextEventKind.ContextCompacted);
        Assert.Equal(20 + 1, result.History.Count);
    }

    private static RuntimeContextOptions SmallOptions()
        => new()
        {
            MaxContextTokens = 220,
            MaxItemTokens = 100,
            MaxToolResultTokens = 80,
            LargeToolResultTokens = 60,
            SummaryTokens = 50,
            RecentTrajectoryMessages = 6
        };

    private static RuntimeToolCall Call(string id, string name)
        => new(new RuntimeInvocationId(id), name, JsonSerializer.SerializeToElement(new { path = "a.txt" }));

    private sealed record UnsupportedTestItem(string Value) : RuntimeItem;

    private sealed class CapturingModel : IRuntimeModelClient
    {
        public RuntimeModelRequest? Request { get; private set; }

        public async IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
            RuntimeModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Request = request;
            yield return new RuntimeTextDeltaEvent("done");
            yield return new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn);
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingContextSink : IRuntimeContextEventSink
    {
        public List<RuntimeContextEvent> Events { get; } = [];

        public ValueTask OnEventAsync(RuntimeContextEvent runtimeEvent, CancellationToken ct)
        {
            Events.Add(runtimeEvent);
            return ValueTask.CompletedTask;
        }
    }
}
