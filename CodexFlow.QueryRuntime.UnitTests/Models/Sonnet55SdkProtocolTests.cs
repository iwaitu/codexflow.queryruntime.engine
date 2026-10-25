using System.Net;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Runtime.RuntimeLoopTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Models;

/// <summary>Real SDK/MEAI/protocol/Engine/history round trips over an in-memory HTTP handler.</summary>
public sealed class Sonnet55SdkProtocolTests
{
    private const string Blocks = """
        [{"type":"thinking","thinking":"first思考","signature":"sig-one+/=","future":{"a":7}},
         {"type":"redacted_thinking","data":"opaque-redacted+/=","future":"keep"},
         {"type":"thinking","thinking":"","signature":"sig-empty"},
         {"type":"text","text":"progress"},
         {"type":"tool_use","id":"call_echo","name":"echo","input":{"value":"a<ds_safety>b</ds_safety>","nested":{"n":1.25,"ok":true},"list":[1,"x"]}}]
        """;

    [Theory]
    [InlineData(MeaiStreamingMode.Streaming)]
    [InlineData(MeaiStreamingMode.NonStreaming)]
    [InlineData(MeaiStreamingMode.StreamingWithNonStreamingFallbackOnEmpty)]
    public async Task SignedBlocks_SurviveActualEngineHistoryAndSdkReplay(MeaiStreamingMode mode)
    {
        using var handler = new RoundTripHandler();
        using var http = new HttpClient(handler);
        using var sdk = Sdk(http);
        using var client = new MeaiRuntimeModelClient(sdk,
            static _ => new VllmChatOptions { ThinkingEnabled = false, MaxOutputTokens = 4096 },
            new MeaiRuntimeModelClientOptions { StreamingMode = mode, ClassifyProviderErrors = true });
        var request = CreateRequest(tools: [EchoDescriptor()], budget: new RuntimeBudgetSnapshot(5, 5)) with
        { ContextManager = new RuntimeContextManager(RuntimeContextOptions.ForModelWindow(242048)) };
        var result = await RunLoopAsync(new RuntimeAgentLoop(client), request);
        Assert.True(result.Status == RuntimeTurnStatus.Completed,
            $"{result.Error?.Code}: {result.Error?.Message}; HTTP={handler.Requests.Count}");
        Assert.Equal("done", result.FinalText);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(20, result.Usage.InputTokens);
        Assert.Equal(14, result.Usage.OutputTokens);
        Assert.Equal(34, result.Usage.TotalTokens);
        using var next = JsonDocument.Parse(handler.Requests[1]);
        var assistant = Assert.Single(next.RootElement.GetProperty("messages").EnumerateArray(),
            m => m.GetProperty("role").GetString() == "assistant");
        var replay = assistant.GetProperty("content").EnumerateArray().ToArray();
        using var expected = JsonDocument.Parse(Blocks);
        Assert.Equal(5, replay.Length);
        foreach (var (actual, wanted) in replay.Zip(expected.RootElement.EnumerateArray()))
            Assert.True(JsonElement.DeepEquals(actual, wanted), "A complete signed block, its order or tool input changed.");
        Assert.Equal("between_tools", next.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.DoesNotContain(result.ContextEvents, e => e.Code == "reasoning_fragment_hard_cap");
    }

    [Theory]
    [InlineData(MeaiStreamingMode.Streaming)]
    [InlineData(MeaiStreamingMode.NonStreaming)]
    [InlineData(MeaiStreamingMode.StreamingWithNonStreamingFallbackOnEmpty)]
    public async Task RequiredTool_IsClassifiedNonRetryableWithoutHttpOrFallback(MeaiStreamingMode mode)
    {
        using var handler = new RoundTripHandler();
        using var http = new HttpClient(handler);
        using var sdk = Sdk(http);
        using var client = new MeaiRuntimeModelClient(sdk, static _ => new VllmChatOptions { ThinkingEnabled = false },
            new MeaiRuntimeModelClientOptions { ClassifyProviderErrors = true, StreamingMode = mode });
        var request = new RuntimeModelRequest(new("s"), new("t"), new("step"),
            [new(RuntimeMessageRole.User, [new RuntimeTextItem("call echo")])], [EchoDescriptor()],
            new RuntimeModelParameters { RequiredToolName = "echo" }, 0);
        var error = await Assert.ThrowsAsync<RuntimeModelClientException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(request, new RuntimeModelAttemptContext(1), TestContext.Current.CancellationToken)) { }
        });
        Assert.Equal("provider_request_rejected", error.Error.Code);
        Assert.False(error.Error.Retryable);
        Assert.Empty(handler.Requests);
    }

    private static IChatClient Sdk(HttpClient http) => QreModelProviderSelector.CreateDefault().CreateClient(new()
    {
        ApiUrl = new Uri("http://offline.test"), ApiKey = "offline-only", Model = "claude-sonnet-5-5",
        ApiMode = QreModelApiMode.AnthropicMessages, HttpClient = http
    });

    private static RuntimeToolDescriptor EchoDescriptor() => ToolDescriptor("echo") with
    { InputSchema = Json("""{"type":"object","properties":{},"additionalProperties":true}""") };

    private sealed class RoundTripHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add(body);
            using var doc = JsonDocument.Parse(body);
            var streaming = doc.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean();
            var first = Requests.Count == 1;
            var content = first ? Blocks : "[{\"type\":\"text\",\"text\":\"done\"}]";
            var stop = first ? "tool_use" : "end_turn";
            if (!streaming)
                return Reply($$$"""{"id":"r","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":{{{content}}},"stop_reason":"{{{stop}}}","usage":{"input_tokens":10,"output_tokens":7}}""", false);
            var events = new StringBuilder();
            void Event(string name, object payload) => events.Append("event: ").Append(name).Append("\ndata: ")
                .Append(JsonSerializer.Serialize(payload)).Append("\n\n");
            Event("message_start", new { type = "message_start", message = new { id = "r", type = "message", role = "assistant", model = "claude-sonnet-5-5", content = Array.Empty<object>(), usage = new { input_tokens = 10, output_tokens = 0 } } });
            using var blocks = JsonDocument.Parse(content);
            var index = 0;
            foreach (var block in blocks.RootElement.EnumerateArray())
            {
                // Complete start blocks deliberately exercise empty-thinking signatures
                // and future properties independently from delta reconstruction.
                Event("content_block_start", new { type = "content_block_start", index, content_block = block });
                Event("content_block_stop", new { type = "content_block_stop", index });
                index++;
            }
            Event("message_delta", new { type = "message_delta", delta = new { stop_reason = stop }, usage = new { output_tokens = 7 } });
            Event("message_stop", new { type = "message_stop" });
            return Reply(events.ToString(), true);
        }
        private static HttpResponseMessage Reply(string body, bool streaming) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
    }
}
