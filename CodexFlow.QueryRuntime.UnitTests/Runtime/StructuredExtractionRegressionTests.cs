using System.Net;
using System.Runtime.CompilerServices;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using CodexFlow.QueryRuntime.Protocol;
using CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics;
using Microsoft.Extensions.AI;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Runtime.RuntimeLoopTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class StructuredExtractionRegressionTests
{
    [Theory]
    [InlineData(RuntimeContextTruncationPolicy.FailClosedOnInitialMessages)]
    [InlineData(RuntimeContextTruncationPolicy.FailClosedOnAny)]
    public async Task Resume_MustStillRejectPreviouslyTruncatedInitialEvidence(RuntimeContextTruncationPolicy policy)
    {
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var options = new RuntimeContextOptions
        {
            MaxContextTokens = 4_000, MaxItemTokens = 1_000,
            MaxToolResultTokens = 1_000, LargeToolResultTokens = 500, SummaryTokens = 500,
            TruncationPolicy = policy
        };
        var request = CreateRequest() with
        {
            InitialMessages = [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(new string('x', 6_000))])],
            ContextManager = new RuntimeContextManager(options),
            CheckpointSink = checkpoints,
            RecoveryCompatibilityId = "pr2-review"
        };
        var originalModel = new ScriptedModelClient(_ => Text("should not be called"));
        var original = await RunLoopAsync(new RuntimeAgentLoop(originalModel), request);
        Assert.Equal("context_truncation_rejected", original.Error!.Code);
        Assert.Empty(originalModel.Requests);
        var checkpoint = checkpoints.Checkpoints.First(c => c.Kind == RuntimeCheckpointKind.TurnStarted);
        Assert.Contains(checkpoint.HistoryNormalizationEvents!, e => e.Code == "text_fragment_hard_cap");
        var root = Directory.CreateTempSubdirectory("qre-truncation-checkpoint-");
        try
        {
            var store = new RuntimeJsonCheckpointStore(root.FullName);
            await store.SaveAsync(checkpoint, TestContext.Current.CancellationToken);
            checkpoint = RuntimeJsonCheckpointStore.Read(store.CheckpointPath);
        }
        finally
        {
            root.Delete(recursive: true);
        }
        var resumedModel = new ScriptedModelClient(_ => Text("accepted shortened evidence"));
        var resumed = await new RuntimeAgentLoop(resumedModel).ResumeAsync(
            request with { Attempt = RuntimeRunAttempt.Resume(checkpoint) }, checkpoint,
            ct: TestContext.Current.CancellationToken);
        Assert.True(resumed.Error?.Code == "context_truncation_rejected",
            $"Resumed status={resumed.Status}, modelCalls={resumedModel.Requests.Count}, error={resumed.Error?.Code ?? "none"}");
        Assert.Empty(resumedModel.Requests);
    }

    [Fact]
    public async Task Resume_RejectsTruncatedToolResultsWithoutRepeatingTheTool()
    {
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var executor = new LargeResultExecutor();
        var request = CreateRequest(budget: new RuntimeBudgetSnapshot(3, 3)) with
        {
            Tools = [ToolDescriptor("read_document")],
            ToolExecutor = executor,
            CheckpointSink = checkpoints,
            RecoveryCompatibilityId = "structured-extraction-regression",
            ContextManager = new RuntimeContextManager(new RuntimeContextOptions
            {
                MaxContextTokens = 4_000, MaxItemTokens = 1_000,
                MaxToolResultTokens = 1_000, LargeToolResultTokens = 500, SummaryTokens = 500,
                TruncationPolicy = RuntimeContextTruncationPolicy.FailClosedOnAny
            })
        };
        var original = await RunLoopAsync(new RuntimeAgentLoop(new ScriptedModelClient(
            _ => Tool(ToolCall("c1", "read_document")), _ => Text("unreachable"))), request);
        Assert.Equal("context_truncation_rejected", original.Error!.Code);
        var checkpoint = Assert.Single(checkpoints.Checkpoints, c => c.Kind == RuntimeCheckpointKind.ToolBatchCommitted);
        var resumedModel = new ScriptedModelClient(_ => Text("unreachable"));
        var resumed = await new RuntimeAgentLoop(resumedModel).ResumeAsync(
            request with { Attempt = RuntimeRunAttempt.Resume(checkpoint) }, checkpoint,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal("context_truncation_rejected", resumed.Error!.Code);
        Assert.Empty(resumedModel.Requests);
        Assert.Equal(1, executor.Calls);
    }

    [Theory]
    [InlineData(RuntimeContextTruncationPolicy.Allow)]
    [InlineData(RuntimeContextTruncationPolicy.FailClosedOnAny)]
    public async Task LegacyCheckpoint_RequiresNormalizationEvidenceOnlyForStrictPolicies(RuntimeContextTruncationPolicy policy)
    {
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var request = CreateRequest() with
        {
            CheckpointSink = checkpoints,
            RecoveryCompatibilityId = "structured-extraction-regression",
            ContextManager = new RuntimeContextManager(new RuntimeContextOptions { TruncationPolicy = policy })
        };
        await RunLoopAsync(new RuntimeAgentLoop(new ScriptedModelClient(_ => Text("source"))), request);
        var checkpoint = checkpoints.Checkpoints.First(c => c.Kind == RuntimeCheckpointKind.TurnStarted)
            with { HistoryNormalizationEvents = null };
        var resumedModel = new ScriptedModelClient(_ => Text("resumed"));
        var loop = new RuntimeAgentLoop(resumedModel);
        var resumeRequest = request with { Attempt = RuntimeRunAttempt.Resume(checkpoint) };
        if (policy == RuntimeContextTruncationPolicy.Allow)
        {
            var resumed = await loop.ResumeAsync(resumeRequest, checkpoint, ct: TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeTurnStatus.Completed, resumed.Status);
        }
        else
        {
            var error = await Assert.ThrowsAsync<RuntimeResumeException>(() =>
                loop.ResumeAsync(resumeRequest, checkpoint, ct: TestContext.Current.CancellationToken));
            Assert.Equal("checkpoint_context_history_missing", error.Error.Code);
            Assert.Empty(resumedModel.Requests);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyFallback_MustAllowRetryOfContentFree503(bool observed)
    {
        var chat = new EmptyThenTransientChatClient();
        var diagnostics = observed ? QreOutboundDiagnostics.Create(
            new QreOutboundDiagnosticsOptions { Mode = QreOutboundDiagnosticMode.Structure },
            new InMemoryDiagnosticSink()) : null;
        using var client = new MeaiRuntimeModelClient(chat, _ => new ChatOptions(),
            new MeaiRuntimeModelClientOptions
            {
                StreamingMode = MeaiStreamingMode.StreamingWithNonStreamingFallbackOnEmpty,
                ClassifyProviderErrors = true
            }, diagnostics);
        var request = CreateRequest(budget: new RuntimeBudgetSnapshot(1, 0, maxModelRetries: 2)) with
        {
            ModelRetryPolicy = new RuntimeRetryPolicy { BaseDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, JitterRatio = 0 }
        };
        var result = await RunLoopAsync(new RuntimeAgentLoop(client), request);
        Assert.True(result.Status == RuntimeTurnStatus.Completed,
            $"Status={result.Status}, error={result.Error?.Code}, retryable={result.Error?.Retryable}, " +
            $"attempts={result.Turn.Steps[0].ModelAttempts}, nonStreamingCalls={chat.NonStreamingCalls}");
        Assert.Equal(2, result.Turn.Steps[0].ModelAttempts);
        Assert.Equal(2, chat.NonStreamingCalls);
        Assert.Single(result.Turn.Steps[0].Output!.Warnings, w => w.Code == "empty_stream_fallback");
    }

    [Fact]
    public void EqualSchemas_MustHaveEqualHashesAndWorkAsSetKeys()
    {
        var left = RuntimeResponseFormat.ForJsonSchema(Json("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"},\"b\":{\"type\":\"number\"}}}"));
        var right = RuntimeResponseFormat.ForJsonSchema(Json("{\"properties\":{\"b\":{\"type\":\"number\"},\"a\":{\"type\":\"string\"}},\"type\":\"object\"}"));
        Assert.Equal(left, right);
        var set = new HashSet<RuntimeResponseFormat> { left };
        Assert.True(set.Contains(right), $"Equals=true but Contains=false; hashes={left.GetHashCode()}/{right.GetHashCode()}");
    }

    [Fact]
    public void NumericallyEquivalentSchemas_MustHaveEqualHashes()
    {
        var left = RuntimeResponseFormat.ForJsonSchema(Json("{\"type\":\"object\",\"minimum\":1}"));
        var right = RuntimeResponseFormat.ForJsonSchema(Json("{\"minimum\":1.0e0,\"type\":\"object\"}"));
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left.ComputeSchemaSha256(), right.ComputeSchemaSha256());
    }

    private sealed class LargeResultExecutor : IRuntimeToolExecutor
    {
        public int Calls { get; private set; }
        public ValueTask<RuntimeToolResult> ExecuteAsync(RuntimeToolDescriptor descriptor, RuntimeToolCall call,
            RuntimeToolExecutionContext context, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(new RuntimeToolResult(call.InvocationId, new string('y', 12_000), true));
        }
    }

    private sealed class EmptyThenTransientChatClient : IChatClient
    {
        public int NonStreamingCalls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            NonStreamingCalls++;
            if (NonStreamingCalls == 1)
                throw new HttpRequestException("temporary outage", null, HttpStatusCode.ServiceUnavailable);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "recovered")) { FinishReason = ChatFinishReason.Stop });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, FinishReason = ChatFinishReason.Stop };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
