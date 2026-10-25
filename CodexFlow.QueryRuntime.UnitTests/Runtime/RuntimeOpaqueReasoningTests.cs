using System.Net;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Runtime.RuntimeLoopTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class RuntimeOpaqueReasoningTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(8000)]
    [InlineData(242048)]
    public void AtLimit_PreservesOpaqueDataAcrossAppendSnapshotRestoreAndProjection(int maxTokens)
    {
        var opaque = new string('x', maxTokens * 4);
        var options = Options(maxTokens);
        var history = RuntimeHistory.Create([], 0, options);
        history.AppendBatch([Message(opaque)]);
        var snapshot = history.Snapshot();
        var restored = RuntimeHistory.RestoreCanonical(snapshot.Messages, snapshot.Version,
            snapshot.NextMessageSequence, options);
        Assert.Equal(opaque, Assert.IsType<RuntimeReasoningItem>(restored.ToMessages()[0].Items[0]).ProtectedData);
        Assert.Equal(maxTokens, RuntimeTokenEstimator.Estimate(restored.ToMessages()[0].Items[0]));
        var prepared = new RuntimeContextManager(options).Prepare(restored.Snapshot(), "test", null);
        Assert.Contains(prepared.Messages.SelectMany(m => m.Items),
            item => item is RuntimeReasoningItem reasoning && reasoning.ProtectedData == opaque);
        Assert.DoesNotContain(restored.DrainEvents(), e => e.Code == "reasoning_fragment_hard_cap");
    }

    [Fact]
    public void UnicodeSignedEnvelope_IsPreservedVerbatim()
    {
        const string opaque = "{\"version\":1,\"block\":{\"type\":\"thinking\",\"thinking\":\"思考𝄞\",\"signature\":\"abc/+=\",\"future\":{\"v\":7}}}";
        var history = RuntimeHistory.Create([Message(opaque)], 0, Options(128));
        Assert.Equal(opaque, Assert.IsType<RuntimeReasoningItem>(history.ToMessages()[0].Items[0]).ProtectedData);
    }

    [Fact]
    public void OversizedBatch_RejectsAtomicallyBeforeVersionIndexesEventsAndIdsChange()
    {
        var history = RuntimeHistory.Create([new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("initial")])], 0, Options(8));
        var before = history.Snapshot();
        var error = Assert.Throws<RuntimeOpaqueReasoningLimitException>(() => history.AppendBatch([
            new RuntimeMessage(RuntimeMessageRole.System, [new RuntimeTextItem("new system")]),
            Message(new string('x', 33))]));
        Assert.Equal("opaque_reasoning_item_too_large", error.Code);
        Assert.Equal(9, error.EstimatedTokens);
        Assert.Equal(8, error.MaxItemTokens);
        var after = history.Snapshot();
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.NextMessageSequence, after.NextMessageSequence);
        Assert.Single(after.Messages);
        Assert.Equal(before.Messages[0].Id, after.Messages[0].Id);
        Assert.Empty(history.DrainEvents());
        history.AppendBatch([new RuntimeMessage(RuntimeMessageRole.System, [new RuntimeTextItem("new system")])]);
        Assert.Equal("h1:m1", history.Snapshot().Messages[1].Id.Value);
        Assert.Equal("new system", Assert.IsType<RuntimeTextItem>(history.ToMessages()[1].Items[0]).Text);
    }

    [Fact]
    public void RestoreUnderSmallerLimit_RejectsRatherThanCorruptingCanonicalData()
    {
        var original = RuntimeHistory.Create([Message(new string('x', 33))], 0, Options(16)).Snapshot();
        Assert.Throws<RuntimeOpaqueReasoningLimitException>(() => RuntimeHistory.RestoreCanonical(
            original.Messages, original.Version, original.NextMessageSequence, Options(8)));
    }

    [Fact]
    public async Task EngineOversizedInitialOpaque_RejectsBeforeAnySdkHttp()
    {
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler);
        using var chat = QreModelProviderSelector.CreateDefault().CreateClient(new QreModelClientDescriptor
        {
            ApiUrl = new Uri("http://offline.test/v1"), ApiKey = "offline-only", Model = "claude-sonnet-5-5",
            ApiMode = QreModelApiMode.AnthropicMessages, HttpClient = http
        });
        using var model = new MeaiRuntimeModelClient(chat, static _ => new ChatOptions());
        var request = CreateRequest() with { InitialMessages = [Message(new string('x', 33))], ContextManager = new RuntimeContextManager(Options(8)) };
        var error = await Assert.ThrowsAsync<RuntimeOpaqueReasoningLimitException>(() => RunLoopAsync(new RuntimeAgentLoop(model), request));
        Assert.Equal("opaque_reasoning_item_too_large", error.Code);
        Assert.Equal(0, handler.Count);
    }

    private static RuntimeMessage Message(string opaque) => new(RuntimeMessageRole.Assistant, [new RuntimeReasoningItem("", opaque)]);
    private static RuntimeContextOptions Options(int maxTokens) => new()
    {
        MaxContextTokens = maxTokens + 1024, MaxItemTokens = maxTokens,
        MaxToolResultTokens = 1, LargeToolResultTokens = 1, SummaryTokens = 1
    };

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
