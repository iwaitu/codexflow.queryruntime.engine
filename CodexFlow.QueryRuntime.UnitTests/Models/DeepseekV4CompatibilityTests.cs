using System.Net;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Providers;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Models;

/// <summary>
/// DeepSeek V4 family routing and behaviour, exercised through the QRE factory against the real VllmChatClient 2.0.32 with an in-memory transport
/// (no socket, no provider). V3 controls prove that the default family is unchanged. The audit-tag removal is a declared SDK transformation of
/// response text and reasoning history only: tool names, ids and arguments are never filtered. The SDK's Anthropic <c>message_start</c> input usage
/// is not mapped by V3; V4 must emit exactly the usage values V3 emits and these tests do not claim to fix that older metering behaviour.
/// </summary>
public sealed class DeepseekV4CompatibilityTests
{
    private const string V4Model = "deepseek-v4.1-flash-1017";
    private const string V3Model = "deepseek-v3";
    private const string Tag = "<ds_safety>hidden</ds_safety>Safe\n\n";
    private const string Literal = "<ds_safety>keep-in-argument</ds_safety>";
    private const string SchemaJson = """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""";

    private static string J(object value) => JsonSerializer.Serialize(value);

    private sealed class Fake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private static HttpResponseMessage Reply(string body, string type = "application/json")
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static IChatClient Create(string model, QreModelApiMode mode, HttpClient http)
        => QreModelProviderSelector.CreateDefault().CreateClient(new QreModelClientDescriptor
        {
            ApiUrl = new Uri(mode == QreModelApiMode.AnthropicMessages ? "https://offline.test/apps/anthropic" : "https://offline.test/v1"),
            ApiKey = "test-key",
            Model = model,
            ApiMode = mode,
            HttpClient = http
        });

    private static (HttpClient Http, List<string> Bodies) Transport(Func<string, HttpResponseMessage> respond)
    {
        var bodies = new List<string>();
        var http = new HttpClient(new Fake(async (request, ct) =>
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            bodies.Add(body);
            return respond(body);
        }));
        return (http, bodies);
    }

    private static JsonElement Schema() => JsonDocument.Parse(SchemaJson).RootElement.Clone();

    // ---- routing -----------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("deepseek-v4")]
    [InlineData("deepseek-v4-flash")]
    [InlineData("deepseek-v4-pro")]
    [InlineData("deepseek-v4.1-flash")]
    [InlineData("deepseek-v4.1-flash-1017")]
    [InlineData("deepseek-v4.2")]
    [InlineData("deepseek-v4/flash")]
    [InlineData("deepseek-v4/x")]
    [InlineData("/deepseek-v4-flash")]
    [InlineData("/deepseek-v4/x")]
    [InlineData("DeepSeek-V4.1-Flash-1017")]
    [InlineData("  deepseek-v4.1-flash-1017  ")]
    public void IsV4Family_AcceptsOnlyAnExplicitV4Boundary(string model)
    {
        Assert.True(DeepseekModelProvider.IsV4Family(model));
    }

    [Theory]
    [InlineData("deepseek-v3")]
    [InlineData("deepseek-v3.2")]
    [InlineData("deepseek-r1")]
    [InlineData("deepseek")]
    [InlineData("deepseek-chat")]
    [InlineData("deepseek-reasoner")]
    [InlineData("deepseek-v40")]
    [InlineData("deepseek-v4x")]
    [InlineData("deepseek-v41")]
    [InlineData("deepseek-v4_flash")]
    [InlineData("deepseek-vv4")]
    [InlineData("//deepseek-v4")]
    [InlineData("vendor/deepseek-v4")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsV4Family_RejectsEveryOtherName(string model)
    {
        Assert.False(DeepseekModelProvider.IsV4Family(model));
    }

    [Fact]
    public void IsV4Family_RejectsNull()
    {
        Assert.False(DeepseekModelProvider.IsV4Family(null));
    }

    public static TheoryData<string, bool> RoutedModels() => new()
    {
        { "deepseek-v4", true }, { "deepseek-v4-flash", true }, { "deepseek-v4-pro", true }, { "deepseek-v4.1-flash", true },
        { "deepseek-v4.1-flash-1017", true }, { "deepseek-v4/flash", true }, { "deepseek-v4/x", true }, { "/deepseek-v4-flash", true },
        { "/deepseek-v4/x", true }, { "DEEPSEEK-V4.1-FLASH-1017", true },
        { "deepseek-v3", false }, { "deepseek-v3.2", false }, { "deepseek-r1", false }, { "deepseek", false }, { "deepseek-chat", false },
        { "deepseek-reasoner", false }, { "deepseek-v40", false }, { "deepseek-v4x", false }, { "/deepseek-v3", false }
    };

    [Theory]
    [MemberData(nameof(RoutedModels))]
    public void Selector_CreatesTheExactClientTypeInEveryApiMode(string model, bool v4)
    {
        Assert.Equal("deepseek", QreModelProviderSelector.CreateDefault().Select(model).Id);
        foreach (var mode in new[] { QreModelApiMode.ChatCompletions, QreModelApiMode.Responses, QreModelApiMode.AnthropicMessages })
        {
            using var http = new HttpClient(new Fake((_, _) => throw new InvalidOperationException("no request expected")));
            using var client = Create(model, mode, http);
            if (v4)
            {
                Assert.IsType<VllmDeepseekV4ChatClient>(client);
            }
            else
            {
                Assert.IsType<VllmDeepseekV3ChatClient>(client);
            }
        }
    }

    [Fact]
    public void OtherProvidersAreNotRoutedToDeepseekV4()
    {
        foreach (var (model, providerId) in new[] { ("qwen3-next-80b", "qwen"), ("claude-sonnet-4", "claude"), ("glm-4.6", "glm"), ("kimi-k2", "kimi") })
        {
            Assert.Equal(providerId, QreModelProviderSelector.CreateDefault().Select(model).Id);
            using var http = new HttpClient(new Fake((_, _) => throw new InvalidOperationException("no request expected")));
            using var client = Create(model, QreModelApiMode.ChatCompletions, http);
            Assert.IsNotType<VllmDeepseekV4ChatClient>(client);
        }
    }

    // ---- non-streaming: text and reasoning are filtered, tool calls/usage/finish are not -------------------------------------------------------

    private static string NonStreamBody(bool native, string model, string text, string reasoning)
        => native
            ? J(new
            {
                id = "msg_test", type = "message", role = "assistant", model,
                content = new object[]
                {
                    new { type = "thinking", thinking = reasoning, signature = "sig" }, new { type = "text", text },
                    new { type = "tool_use", id = "toolu_1", name = "echo", input = new { literal = Literal } }
                },
                stop_reason = "tool_use", usage = new { input_tokens = 11, output_tokens = 7 }
            })
            : J(new
            {
                id = "chat_test", @object = "chat.completion", created = 1, model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new
                        {
                            role = "assistant", content = text, reasoning_content = reasoning,
                            tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = "echo", arguments = J(new { literal = Literal }) } } }
                        },
                        finish_reason = "tool_calls"
                    }
                },
                usage = new { prompt_tokens = 11, completion_tokens = 7, total_tokens = 18 }
            });

    [Theory]
    [InlineData(QreModelApiMode.ChatCompletions)]
    [InlineData(QreModelApiMode.AnthropicMessages)]
    public async Task NonStreaming_V4FiltersTextAndReasoningButNeverToolCallsUsageOrFinish(QreModelApiMode mode)
    {
        var native = mode == QreModelApiMode.AnthropicMessages;
        var (http, bodies) = Transport(_ => Reply(NonStreamBody(native, V4Model, "before " + Tag + "after", "think " + Tag + "more")));
        using var client = Create(V4Model, mode, http);

        var response = await client.GetResponseAsync("Return JSON", new VllmChatOptions { ThinkingEnabled = false, MaxOutputTokens = 256, ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema()) },
            TestContext.Current.CancellationToken);

        Assert.Equal("before after", response.Text);
        var call = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
        Assert.Equal("echo", call.Name);
        Assert.Equal(native ? "toolu_1" : "call_1", call.CallId);
        Assert.Equal(Literal, call.Arguments!["literal"]?.ToString());
        Assert.Equal(11, response.Usage?.InputTokenCount);
        Assert.Equal(7, response.Usage?.OutputTokenCount);
        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        Assert.Equal(V4Model, response.ModelId);
        Assert.Equal(native ? "msg_test" : "chat_test", response.ResponseId);

        // Inherited metadata: the V3 default family on the very same fixture yields the same finish reason, usage, ids and tool call.
        var (v3Http, _) = Transport(_ => Reply(NonStreamBody(native, V4Model, "before " + Tag + "after", "think " + Tag + "more")));
        using var v3Client = Create(V3Model, mode, v3Http);
        var v3Response = await v3Client.GetResponseAsync("Return JSON", new VllmChatOptions { ThinkingEnabled = false, MaxOutputTokens = 256, ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema()) },
            TestContext.Current.CancellationToken);
        Assert.Equal(v3Response.FinishReason, response.FinishReason);
        Assert.Equal(J(v3Response.Usage!), J(response.Usage!));
        Assert.Equal(v3Response.ResponseId, response.ResponseId);
        Assert.Equal(v3Response.ModelId, response.ModelId);
        var v3Call = v3Response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
        Assert.Equal((v3Call.CallId, v3Call.Name, J(v3Call.Arguments!)), (call.CallId, call.Name, J(call.Arguments!)));
        Assert.Contains("<ds_safety>", v3Response.Text, StringComparison.Ordinal);       // original fixture kept: only V4 removes the declared tag

        // The real V4 outbound shape (kept separate from the V3 matrix cells).
        using var sent = JsonDocument.Parse(Assert.Single(bodies));
        var root = sent.RootElement;
        Assert.Equal(V4Model, root.GetProperty("model").GetString());
        Assert.Equal(256, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("json_schema", response.AdditionalProperties?["vllm.structured_output.requested"]);
        Assert.Equal("json_object", response.AdditionalProperties?["vllm.structured_output.actual"]);
        Assert.Equal(false, response.AdditionalProperties?["vllm.structured_output.provider_schema_enforced"]);
        Assert.Contains("VllmDeepSeekJsonObjectSchema", root.GetRawText(), StringComparison.Ordinal);
        if (native)
        {
            Assert.False(root.TryGetProperty("response_format", out _));
            var format = root.GetProperty("output_config").GetProperty("format");
            Assert.Equal("json_object", format.GetProperty("type").GetString());
            Assert.False(format.TryGetProperty("schema", out _));
            Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
            Assert.False(root.TryGetProperty("format", out _));
            Assert.False(root.TryGetProperty("extra_body", out _));
            Assert.False(root.TryGetProperty("enable_thinking", out _));
        }
        else
        {
            Assert.Equal(JsonValueKind.False, root.GetProperty("enable_thinking").ValueKind);
            Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
            Assert.False(root.TryGetProperty("format", out _));
            Assert.False(root.TryGetProperty("extra_body", out _));
            Assert.False(root.TryGetProperty("output_config", out _));
            Assert.False(root.TryGetProperty("thinking", out _));
        }
    }

    [Theory]
    [InlineData(QreModelApiMode.ChatCompletions)]
    [InlineData(QreModelApiMode.AnthropicMessages)]
    public async Task NonStreaming_V3KeepsTheAuditTagsSoTheDefaultFamilyIsUnchanged(QreModelApiMode mode)
    {
        var native = mode == QreModelApiMode.AnthropicMessages;
        var (http, _) = Transport(_ => Reply(NonStreamBody(native, V3Model, "before " + Tag + "after", "think " + Tag + "more")));
        using var client = Create(V3Model, mode, http);

        var response = await client.GetResponseAsync("hi", new VllmChatOptions { ThinkingEnabled = false }, TestContext.Current.CancellationToken);

        Assert.Contains("<ds_safety>", response.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(QreModelApiMode.ChatCompletions)]
    [InlineData(QreModelApiMode.AnthropicMessages)]
    public async Task NonStreaming_ReasoningOfAReasoningResponseIsCleanedForV4AndKeptForV3(QreModelApiMode mode)
    {
        var native = mode == QreModelApiMode.AnthropicMessages;
        async Task<string?> Reason(string model)
        {
            var (http, _) = Transport(_ => Reply(NonStreamBody(native, model, "answer", "think " + Tag + "more")));
            using var client = Create(model, mode, http);
            var response = await client.GetResponseAsync("hi", new VllmChatOptions { ThinkingEnabled = true, MaxOutputTokens = 256 }, TestContext.Current.CancellationToken);
            return Assert.IsType<ReasoningChatResponse>(response, exactMatch: false).Reason;
        }

        Assert.Equal("think more", await Reason(V4Model));
        Assert.Contains("<ds_safety>", await Reason(V3Model) ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- replayed history ------------------------------------------------------------------------------------------------------------------------

    private static string ReasoningStream(bool native)
    {
        var sse = new StringBuilder();
        if (native)
        {
            sse.Append(Event("message_start", new { type = "message_start", message = new { id = "m", type = "message", role = "assistant", model = V4Model, content = Array.Empty<object>(), usage = new { input_tokens = 1, output_tokens = 0 } } }));
            sse.Append(Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "" } }));
            foreach (var chunk in new[] { "think <ds_", "safety>hidden</ds_safety>Sa", "fe\n\nmore" })
            {
                sse.Append(Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = chunk } }));
            }
            sse.Append(Event("content_block_stop", new { type = "content_block_stop", index = 0 }));
            sse.Append(Event("content_block_start", new { type = "content_block_start", index = 1, content_block = new { type = "tool_use", id = "toolu_1", name = "echo", input = new { } } }));
            sse.Append(Event("content_block_delta", new { type = "content_block_delta", index = 1, delta = new { type = "input_json_delta", partial_json = J(new { literal = Literal }) } }));
            sse.Append(Event("content_block_stop", new { type = "content_block_stop", index = 1 }));
            sse.Append(Event("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = 7 } }));
            sse.Append(Event("message_stop", new { type = "message_stop" }));
            return sse.ToString();
        }
        foreach (var chunk in new[] { "think <ds_", "safety>hidden</ds_safety>Sa", "fe\n\nmore" })
        {
            sse.Append(ChatChunk(new { reasoning_content = chunk }));
        }
        sse.Append(ChatChunk(new { tool_calls = new[] { new { index = 0, id = "call_1", type = "function", function = new { name = "echo", arguments = J(new { literal = Literal }) } } } }));
        sse.Append(ChatChunk(new { }, "tool_calls"));
        sse.Append("data: [DONE]\n\n");
        return sse.ToString();
    }

    /// <summary>Runs one tool round trip (response, then history with the tool result, then a second request) and returns the second request body.</summary>
    private static async Task<string> ReplayBody(string model, QreModelApiMode mode, bool stream)
    {
        var native = mode == QreModelApiMode.AnthropicMessages;
        var (http, bodies) = Transport(_ => stream ? Reply(ReasoningStream(native).Replace(V4Model, model, StringComparison.Ordinal), "text/event-stream")
            : Reply(NonStreamBody(native, model, "answer", "think " + Tag + "more")));
        using var client = Create(model, mode, http);
        var options = new VllmChatOptions { ThinkingEnabled = true, MaxOutputTokens = 256 };
        ChatResponse first;
        if (stream)
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in client.GetStreamingResponseAsync("go", options, TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }
            first = updates.ToChatResponse();
        }
        else
        {
            first = await client.GetResponseAsync("go", options, TestContext.Current.CancellationToken);
        }
        var call = first.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
        Assert.Equal(Literal, call.Arguments!["literal"]?.ToString());
        var history = new List<ChatMessage> { new(ChatRole.User, "go") };
        history.AddRange(first.Messages);
        history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "{\"ok\":true}")]));
        if (stream)
        {
            await foreach (var _ in client.GetStreamingResponseAsync(history, options, TestContext.Current.CancellationToken))
            {
            }
        }
        else
        {
            await client.GetResponseAsync(history, options, TestContext.Current.CancellationToken);
        }
        return bodies[^1];
    }

    private static string ReplayedToolArgument(string replay, bool native)
    {
        using var document = JsonDocument.Parse(replay);
        var messages = document.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        return native
            ? messages.SelectMany(m => m.GetProperty("content").EnumerateArray()).Single(c => c.GetProperty("type").GetString() == "tool_use").GetProperty("input").GetProperty("literal").GetString()!
            : JsonDocument.Parse(messages.Single(m => m.TryGetProperty("tool_calls", out _)).GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString()!)
                .RootElement.GetProperty("literal").GetString()!;
    }

    private static int CountTags(string text)
    {
        var count = 0;
        for (var index = text.IndexOf("ds_safety", StringComparison.Ordinal); index >= 0; index = text.IndexOf("ds_safety", index + 1, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    [Fact]
    public async Task ChatStreamingReasoningHistoryIsCleanedForV4WhileV3AndToolArgumentsAreUntouched()
    {
        var v4 = await ReplayBody(V4Model, QreModelApiMode.ChatCompletions, stream: true);
        var v3 = await ReplayBody(V3Model, QreModelApiMode.ChatCompletions, stream: true);

        Assert.Equal(Literal, ReplayedToolArgument(v4, native: false));
        Assert.Equal(CountTags(Literal), CountTags(v4));                  // only the tool-argument literal contains the tag text
        Assert.True(CountTags(v3) > CountTags(Literal));                  // the original (V3) replay still carries the reasoning tags: original vs sent differ
        using var document = JsonDocument.Parse(v4);
        Assert.Equal("think more", document.RootElement.GetProperty("messages").EnumerateArray().Single(m => m.TryGetProperty("tool_calls", out _)).GetProperty("reasoning_content").GetString());
    }

    /// <summary>
    /// These three paths leaked stored provider reasoning in SDK 2.0.29. Published SDK 2.0.30 must clean each replay;
    /// only the deliberately tagged tool argument remains. V3 is a negative control, not a substitute for V4 acceptance.
    /// </summary>
    [Theory]
    [InlineData(QreModelApiMode.ChatCompletions, false)]
    [InlineData(QreModelApiMode.AnthropicMessages, false)]
    [InlineData(QreModelApiMode.AnthropicMessages, true)]
    public async Task ReasoningReplayIsCleanedOnEveryPreviouslyLeakingPathWhileV3AndToolArgumentsAreUntouched(QreModelApiMode mode, bool stream)
    {
        var native = mode == QreModelApiMode.AnthropicMessages;

        var v4 = await ReplayBody(V4Model, mode, stream);
        var v3 = await ReplayBody(V3Model, mode, stream);

        Assert.Equal(Literal, ReplayedToolArgument(v4, native));
        Assert.Equal(Literal, ReplayedToolArgument(v3, native));
        Assert.Equal(CountTags(Literal), CountTags(v4));
        Assert.True(CountTags(v3) > CountTags(Literal));
        using var document = JsonDocument.Parse(v4);
        var assistants = document.RootElement.GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == "assistant").ToArray();
        var reasoning = native
            ? assistants.SelectMany(m => m.GetProperty("content").EnumerateArray())
                .Single(b => b.GetProperty("type").GetString() == "thinking").GetProperty("thinking").GetString()
            : assistants.Single(m => m.TryGetProperty("reasoning_content", out _)).GetProperty("reasoning_content").GetString();
        Assert.Equal("think more", reasoning);
    }

    // ---- streaming -------------------------------------------------------------------------------------------------------------------------------

    private static string Event(string kind, object value) => "event: " + kind + "\ndata: " + J(value) + "\n\n";

    private static string NativeStream(bool tagOnly)
    {
        var sse = Event("message_start", new { type = "message_start", message = new { id = "msg_stream", type = "message", role = "assistant", model = V4Model, content = Array.Empty<object>(), usage = new { input_tokens = 11, output_tokens = 0 } } })
                  + Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } });
        foreach (var chunk in tagOnly ? new[] { "<", "ds_safety>", "hidden</ds_", "safety>Sa", "fe\n", "\n" } : new[] { "before ", "<", "ds_safety>", "hidden</ds_", "safety>Sa", "fe\n", "\nafter" })
        {
            sse += Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = chunk } });
        }
        sse += Event("content_block_stop", new { type = "content_block_stop", index = 0 });
        if (tagOnly)
        {
            return sse + Event("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 7 } }) + Event("message_stop", new { type = "message_stop" });
        }
        return sse
               + Event("content_block_start", new { type = "content_block_start", index = 1, content_block = new { type = "tool_use", id = "toolu_stream", name = "echo", input = new { } } })
               + Event("content_block_delta", new { type = "content_block_delta", index = 1, delta = new { type = "input_json_delta", partial_json = J(new { literal = Literal }) } })
               + Event("content_block_stop", new { type = "content_block_stop", index = 1 })
               + Event("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = 7 } })
               + Event("message_stop", new { type = "message_stop" });
    }

    private static string ChatChunk(object delta, string? finish = null)
        => "data: " + J(new { id = "c1", @object = "chat.completion.chunk", created = 1, model = V4Model, choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\n";

    private static string ChatStream(bool tagOnly)
    {
        var sse = new StringBuilder();
        foreach (var chunk in tagOnly ? new[] { "<ds_", "safety>hidden</ds_safety>Sa", "fe\n", "\n" } : new[] { "before ", "<ds_", "safety>hidden</ds_safety>Sa", "fe\n", "\nafter" })
        {
            sse.Append(ChatChunk(new { content = chunk }));
        }
        if (!tagOnly)
        {
            var arguments = J(new { literal = Literal });
            sse.Append(ChatChunk(new { tool_calls = new[] { new { index = 0, id = "call_stream", type = "function", function = new { name = "echo", arguments = arguments[..12] } } } }));
            sse.Append(ChatChunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = arguments[12..] } } } }));
        }
        sse.Append(ChatChunk(new { }, tagOnly ? "stop" : "tool_calls"));
        sse.Append("data: " + J(new { id = "c1", @object = "chat.completion.chunk", created = 1, model = V4Model, choices = Array.Empty<object>(), usage = new { prompt_tokens = 11, completion_tokens = 7, total_tokens = 18 } }) + "\n\n");
        sse.Append("data: [DONE]\n\n");
        return sse.ToString();
    }

    private static async Task<List<ChatResponseUpdate>> Stream(string model, QreModelApiMode mode, string sse)
    {
        var (http, _) = Transport(_ => Reply(sse, "text/event-stream"));
        using var client = Create(model, mode, http);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync("hello", new VllmChatOptions { ThinkingEnabled = false }, TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }
        return updates;
    }

    private static string Text(List<ChatResponseUpdate> updates) => string.Concat(updates.Select(u => u.Text));
    private static List<string> Usage(List<ChatResponseUpdate> updates) => [.. updates.OfType<UsageChatResponseUpdate>().Select(u => J(u.Usage!))];

    [Theory]
    [InlineData(QreModelApiMode.ChatCompletions)]
    [InlineData(QreModelApiMode.AnthropicMessages)]
    public async Task Streaming_V4RemovesTagsSplitAcrossChunksWithoutTouchingToolCallsFinishOrUsage(QreModelApiMode mode)
    {
        var native = mode == QreModelApiMode.AnthropicMessages;
        var sse = native ? NativeStream(false) : ChatStream(false);

        var updates = await Stream(V4Model, mode, sse);

        Assert.Equal("before after", Text(updates));
        var call = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().Last();
        Assert.Equal(native ? "toolu_stream" : "call_stream", call.CallId);
        Assert.Equal("echo", call.Name);
        Assert.Equal(Literal, call.Arguments!["literal"]?.ToString());
        Assert.Contains(updates, u => u.FinishReason == ChatFinishReason.ToolCalls);
        Assert.Contains(updates.OfType<UsageChatResponseUpdate>(), u => u.Usage?.OutputTokenCount == 7);

        // Usage parity with the V3 default family: V4 must emit exactly the values V3 emits for the same stream (including the inherited Anthropic
        // message_start input-usage behaviour); nothing here claims that older metering was changed.
        var v3 = await Stream(V3Model, mode, sse);
        Assert.Equal(Usage(v3), Usage(updates));
        Assert.Contains("<ds_safety>", Text(v3), StringComparison.Ordinal);                // V3 control: default family keeps the tags
    }

    [Theory]
    [InlineData(QreModelApiMode.ChatCompletions)]
    [InlineData(QreModelApiMode.AnthropicMessages)]
    public async Task Streaming_AResponseThatIsOnlyAuditTagsStillEndsWithItsFinishReasonAndUsage(QreModelApiMode mode)
    {
        var native = mode == QreModelApiMode.AnthropicMessages;

        var updates = await Stream(V4Model, mode, native ? NativeStream(true) : ChatStream(true));

        Assert.Equal(string.Empty, Text(updates));
        Assert.Contains(updates, u => u.FinishReason == ChatFinishReason.Stop);
        Assert.Contains(updates.OfType<UsageChatResponseUpdate>(), u => u.Usage?.OutputTokenCount == 7);
        Assert.Empty(updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>());
    }

    [Theory]
    [InlineData(QreModelApiMode.ChatCompletions)]
    [InlineData(QreModelApiMode.AnthropicMessages)]
    public async Task NonStreaming_AResponseThatIsOnlyAuditTagsIsEmptyTextWithItsFinishReason(QreModelApiMode mode)
    {
        var native = mode == QreModelApiMode.AnthropicMessages;
        var body = native
            ? J(new { id = "m", type = "message", role = "assistant", model = V4Model, content = new object[] { new { type = "text", text = Tag } }, stop_reason = "end_turn", usage = new { input_tokens = 3, output_tokens = 2 } })
            : J(new { id = "c", @object = "chat.completion", created = 1, model = V4Model, choices = new[] { new { index = 0, message = new { role = "assistant", content = Tag }, finish_reason = "stop" } }, usage = new { prompt_tokens = 3, completion_tokens = 2, total_tokens = 5 } });
        var (http, _) = Transport(_ => Reply(body));
        using var client = Create(V4Model, mode, http);

        var response = await client.GetResponseAsync("hi", new VllmChatOptions { ThinkingEnabled = false }, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(2, response.Usage?.OutputTokenCount);
    }

    [Theory]
    [InlineData(QreModelApiMode.ChatCompletions)]
    [InlineData(QreModelApiMode.AnthropicMessages)]
    public async Task Cancellation_IsPropagatedThroughTheQreFactoryClient(QreModelApiMode mode)
    {
        using var http = new HttpClient(new Fake(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Reply(string.Empty);
        }));
        using var client = Create(V4Model, mode, http);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync("hello", new VllmChatOptions { ThinkingEnabled = false }, cts.Token))
            {
            }
        });
    }
}
