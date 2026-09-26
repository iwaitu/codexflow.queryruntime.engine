using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CodexFlow.QueryRuntime.Models;

namespace CodexFlow.QueryRuntime.Cli.Diagnostics;

/// <summary>One scripted offline model response.</summary>
internal sealed record QreOfflineResponse
{
    /// <summary><c>text</c>, <c>tool_call</c> or <c>http_error</c>.</summary>
    public required string Kind { get; init; }

    public string? Text { get; init; }

    public string? ToolName { get; init; }

    public string? CallId { get; init; }

    /// <summary>Tool arguments as a JSON object text.</summary>
    public string? ArgumentsJson { get; init; }

    public int StatusCode { get; init; } = 200;

    public static QreOfflineResponse TextResponse(string text) => new() { Kind = "text", Text = text };

    public static QreOfflineResponse ToolCall(string name, string callId, string argumentsJson)
        => new() { Kind = "tool_call", ToolName = name, CallId = callId, ArgumentsJson = argumentsJson };
}

/// <summary>
/// In-memory terminal transport for offline request rebuilds and tests. It has no
/// downstream handler and never opens a socket: the real SDK serializes and sends
/// its request into this handler, which returns the next scripted, protocol-legal
/// streaming response for the API mode and rejects any unscripted request.
/// </summary>
internal sealed class QreOfflineModelTransport(QreModelApiMode apiMode, IReadOnlyList<QreOfflineResponse> script)
    : HttpMessageHandler
{
    private readonly List<byte[]> _requestBodies = [];
    private int _requests;

    /// <summary>Raw request bodies kept in memory only, for test assertions.</summary>
    public IReadOnlyList<byte[]> RequestBodies
    {
        get
        {
            lock (_requestBodies)
            {
                return _requestBodies.ToArray();
            }
        }
    }

    public int RequestCount => Volatile.Read(ref _requests);

    public bool Exhausted { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var index = Interlocked.Increment(ref _requests) - 1;
        var body = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        lock (_requestBodies)
        {
            _requestBodies.Add(body);
        }
        if (index >= script.Count)
        {
            Exhausted = true;
            return Json(HttpStatusCode.Conflict, "{\"error\":\"offline fixture has no scripted response for this request\"}");
        }
        var response = script[index];
        if (response.Kind == "http_error" || response.StatusCode >= 400)
        {
            return Json((HttpStatusCode)response.StatusCode, "{\"error\":\"scripted offline failure\"}");
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildStream(apiMode, response), Encoding.UTF8, "text/event-stream")
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Builds a complete, protocol-legal streaming response including its finish marker.</summary>
    internal static string BuildStream(QreModelApiMode mode, QreOfflineResponse response)
    {
        var builder = new StringBuilder();
        var isTool = response.Kind == "tool_call";
        var name = response.ToolName ?? string.Empty;
        var callId = response.CallId ?? "call-1";
        var arguments = response.ArgumentsJson ?? "{}";
        var text = response.Text ?? string.Empty;
        switch (mode)
        {
            case QreModelApiMode.ChatCompletions:
                if (isTool)
                {
                    Data(builder, Chunk(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["tool_calls"] = new JsonArray(new JsonObject
                        {
                            ["index"] = 0,
                            ["id"] = callId,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments }
                        })
                    }, null));
                    Data(builder, Chunk(new JsonObject(), "tool_calls"));
                }
                else
                {
                    Data(builder, Chunk(new JsonObject { ["role"] = "assistant", ["content"] = text }, null));
                    Data(builder, Chunk(new JsonObject(), "stop"));
                }
                builder.Append("data: [DONE]\n\n");
                break;
            case QreModelApiMode.Responses:
                Event(builder, "response.created", new JsonObject
                {
                    ["type"] = "response.created",
                    ["response"] = new JsonObject { ["id"] = "resp-offline", ["model"] = "offline", ["status"] = "in_progress" }
                });
                if (isTool)
                {
                    Event(builder, "response.output_item.added", new JsonObject
                    {
                        ["type"] = "response.output_item.added",
                        ["output_index"] = 0,
                        ["item_id"] = "item-1",
                        ["item"] = FunctionItem(callId, name, string.Empty)
                    });
                    Event(builder, "response.function_call_arguments.delta", new JsonObject
                    {
                        ["type"] = "response.function_call_arguments.delta",
                        ["output_index"] = 0,
                        ["item_id"] = "item-1",
                        ["delta"] = arguments
                    });
                    Event(builder, "response.output_item.done", new JsonObject
                    {
                        ["type"] = "response.output_item.done",
                        ["output_index"] = 0,
                        ["item_id"] = "item-1",
                        ["item"] = FunctionItem(callId, name, arguments)
                    });
                }
                else
                {
                    Event(builder, "response.output_text.delta", new JsonObject
                    {
                        ["type"] = "response.output_text.delta",
                        ["output_index"] = 0,
                        ["item_id"] = "item-1",
                        ["delta"] = text
                    });
                }
                Event(builder, "response.completed", new JsonObject
                {
                    ["type"] = "response.completed",
                    ["response"] = new JsonObject { ["id"] = "resp-offline", ["model"] = "offline", ["status"] = "completed" }
                });
                break;
            case QreModelApiMode.AnthropicMessages:
                Event(builder, "message_start", new JsonObject
                {
                    ["type"] = "message_start",
                    ["message"] = new JsonObject
                    {
                        ["id"] = "msg-offline",
                        ["model"] = "offline",
                        ["role"] = "assistant",
                        ["content"] = new JsonArray()
                    }
                });
                Event(builder, "content_block_start", new JsonObject
                {
                    ["type"] = "content_block_start",
                    ["index"] = 0,
                    ["content_block"] = isTool
                        ? new JsonObject { ["type"] = "tool_use", ["id"] = callId, ["name"] = name, ["input"] = new JsonObject() }
                        : new JsonObject { ["type"] = "text", ["text"] = string.Empty }
                });
                Event(builder, "content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = 0,
                    ["delta"] = isTool
                        ? new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = arguments }
                        : new JsonObject { ["type"] = "text_delta", ["text"] = text }
                });
                Event(builder, "content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 });
                Event(builder, "message_delta", new JsonObject
                {
                    ["type"] = "message_delta",
                    ["delta"] = new JsonObject { ["stop_reason"] = isTool ? "tool_use" : "end_turn" },
                    ["usage"] = new JsonObject { ["output_tokens"] = 1 }
                });
                Event(builder, "message_stop", new JsonObject { ["type"] = "message_stop" });
                break;
        }
        return builder.ToString();
    }

    private static JsonObject FunctionItem(string callId, string name, string arguments)
        => new()
        {
            ["type"] = "function_call",
            ["id"] = "item-1",
            ["call_id"] = callId,
            ["name"] = name,
            ["arguments"] = arguments
        };

    private static JsonObject Chunk(JsonObject delta, string? finishReason)
        => new()
        {
            ["id"] = "chatcmpl-offline",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1,
            ["model"] = "offline",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = delta,
                ["finish_reason"] = finishReason
            })
        };

    private static void Data(StringBuilder builder, JsonObject payload)
        => builder.Append("data: ").Append(payload.ToJsonString()).Append("\n\n");

    private static void Event(StringBuilder builder, string name, JsonObject payload)
        => builder.Append("event: ").Append(name).Append('\n')
            .Append("data: ").Append(payload.ToJsonString()).Append("\n\n");
}
