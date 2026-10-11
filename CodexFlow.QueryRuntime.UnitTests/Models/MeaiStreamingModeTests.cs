using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using CodexFlow.QueryRuntime.Protocol;
using CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Models;

public sealed class MeaiStreamingModeTests
{
    [Fact]
    public async Task Streaming_DefaultNeverCallsNonStreaming()
    {
        var chat = new ScriptedChatClient(streamText: "streamed");
        using var client = Client(chat, MeaiStreamingMode.Streaming);

        var events = await CollectAsync(client);

        Assert.Equal("streamed", Text(events));
        Assert.Equal((1, 0), (chat.StreamingCalls, chat.NonStreamingCalls));
    }

    [Fact]
    public async Task NonStreaming_MapsTextToolCallsUsageAndFinishReason()
    {
        var chat = new ScriptedChatClient(
            responseText: "checking",
            responseToolCall: new FunctionCallContent("call-1", "open_document", new Dictionary<string, object?> { ["id"] = "doc-1" }),
            responseFinish: ChatFinishReason.ToolCalls);
        using var client = Client(chat, MeaiStreamingMode.NonStreaming);

        var events = await CollectAsync(client);

        Assert.Equal((0, 1), (chat.StreamingCalls, chat.NonStreamingCalls));
        Assert.Equal("checking", Text(events));
        var call = Assert.Single(events.OfType<RuntimeToolCallEvent>()).Call;
        Assert.Equal("open_document", call.Name);
        Assert.Equal("doc-1", call.Arguments.GetProperty("id").GetString());
        var usage = Assert.Single(events.OfType<RuntimeUsageEvent>()).Usage;
        Assert.Equal((11L, 7L), (usage.InputTokens!.Value, usage.OutputTokens!.Value));
        Assert.Equal(RuntimeModelStopReason.ToolCall, Assert.IsType<RuntimeModelCompletedEvent>(events[^1]).StopReason);
    }

    [Fact]
    public async Task Fallback_ResendsWithoutStreamingWhenStreamIsEmpty()
    {
        var chat = new ScriptedChatClient(streamText: null, responseText: "recovered");
        using var client = Client(chat, MeaiStreamingMode.StreamingWithNonStreamingFallbackOnEmpty);

        var events = await CollectAsync(client);

        Assert.Equal((1, 1), (chat.StreamingCalls, chat.NonStreamingCalls));
        Assert.Equal("empty_stream_fallback", Assert.IsType<RuntimeWarningEvent>(events[0]).Warning.Code);
        Assert.Equal("recovered", Text(events));
        // The empty stream's usage and completion were discarded.
        Assert.Equal(11L, Assert.Single(events.OfType<RuntimeUsageEvent>()).Usage.InputTokens);
        Assert.Single(events.OfType<RuntimeModelCompletedEvent>());
        // The resend is prepared afresh from the same Runtime request (a new ChatOptions per call).
        Assert.NotSame(chat.StreamingOptions, chat.NonStreamingOptions);
        Assert.Equal(chat.StreamingOptions!.ToolMode, chat.NonStreamingOptions!.ToolMode);
    }

    [Fact]
    public async Task Fallback_DoesNotTriggerOnceContentWasStreamed()
    {
        var chat = new ScriptedChatClient(streamText: "streamed", responseText: "unused");
        using var client = Client(chat, MeaiStreamingMode.StreamingWithNonStreamingFallbackOnEmpty);

        var events = await CollectAsync(client);

        Assert.Equal((1, 0), (chat.StreamingCalls, chat.NonStreamingCalls));
        Assert.Equal("streamed", Text(events));
        Assert.DoesNotContain(events, static e => e is RuntimeWarningEvent);
        // Buffered usage still arrives once content starts.
        Assert.Single(events.OfType<RuntimeUsageEvent>());
    }

    [Fact]
    public async Task Fallback_DiagnosticsRecordStreamingAndNonStreamingCalls()
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = QreOutboundDiagnostics.Create(
            new QreOutboundDiagnosticsOptions { Mode = QreOutboundDiagnosticMode.Structure },
            sink);
        var chat = new ScriptedChatClient(streamText: null, responseText: "recovered");
        using var client = new MeaiRuntimeModelClient(
            chat,
            static _ => new ChatOptions(),
            new MeaiRuntimeModelClientOptions { StreamingMode = MeaiStreamingMode.StreamingWithNonStreamingFallbackOnEmpty },
            diagnostics);

        var events = await CollectAsync(client);

        Assert.Equal("recovered", Text(events));
        var adapters = sink.OfType(QreDiagnosticEventTypes.AdapterPrepared);
        Assert.Equal(["true", "false"], adapters.Select(static r => r.Request!.Stream.Value));
        Assert.Equal(["GetStreamingResponseAsync", "GetResponseAsync"], adapters.Select(static r => r.Request!.Stream.SourcePath));
        Assert.Equal(2, adapters.Select(static r => r.ModelCallId).Distinct().Count());
    }

    [Fact]
    public async Task Fallback_ThroughAgentLoopCompletesWithWarning()
    {
        var chat = new ScriptedChatClient(streamText: null, responseText: "final answer");
        using var client = Client(chat, MeaiStreamingMode.StreamingWithNonStreamingFallbackOnEmpty);

        var result = await new RuntimeAgentLoop(client).RunAsync(
            CodexFlow.QueryRuntime.UnitTests.Runtime.RuntimeLoopTestSupport.CreateRequest(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("final answer", result.FinalText);
        Assert.Contains(result.Turn.Steps[0].Output!.Warnings, static w => w.Code == "empty_stream_fallback");
    }

    private static MeaiRuntimeModelClient Client(IChatClient chat, MeaiStreamingMode mode)
        => new(chat, static _ => new ChatOptions(), new MeaiRuntimeModelClientOptions { StreamingMode = mode });

    private static async Task<List<RuntimeModelStreamEvent>> CollectAsync(MeaiRuntimeModelClient client)
    {
        var request = new RuntimeModelRequest(
            new RuntimeSessionId("s"),
            new RuntimeTurnId("t"),
            new RuntimeStepId("step"),
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("hi")])],
            [],
            new RuntimeModelParameters(),
            0);
        var events = new List<RuntimeModelStreamEvent>();
        await foreach (var runtimeEvent in client.StreamAsync(request, new RuntimeModelAttemptContext(1), TestContext.Current.CancellationToken))
        {
            events.Add(runtimeEvent);
        }
        return events;
    }

    private static string Text(IEnumerable<RuntimeModelStreamEvent> events)
        => string.Concat(events.OfType<RuntimeTextDeltaEvent>().Select(static e => e.Text));

    private sealed class ScriptedChatClient(
        string? streamText = null,
        string? responseText = null,
        FunctionCallContent? responseToolCall = null,
        ChatFinishReason? responseFinish = null) : IChatClient
    {
        public int StreamingCalls { get; private set; }

        public int NonStreamingCalls { get; private set; }

        public ChatOptions? StreamingOptions { get; private set; }

        public ChatOptions? NonStreamingOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            NonStreamingCalls++;
            NonStreamingOptions = options;
            var contents = new List<AIContent>();
            if (responseText != null)
            {
                contents.Add(new TextContent(responseText));
            }
            if (responseToolCall != null)
            {
                contents.Add(responseToolCall);
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
            {
                FinishReason = responseFinish ?? ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 7, TotalTokenCount = 18 }
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingCalls++;
            StreamingOptions = options;
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 5, OutputTokenCount = 0, TotalTokenCount = 5 })]);
            if (streamText != null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(streamText)]);
            }
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, FinishReason = ChatFinishReason.Stop };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
