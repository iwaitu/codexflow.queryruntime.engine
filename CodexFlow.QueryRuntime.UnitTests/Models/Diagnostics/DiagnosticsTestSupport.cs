using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Cli.Diagnostics;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics;

internal sealed class InMemoryDiagnosticSink : IQreOutboundDiagnosticSink
{
    private readonly ConcurrentQueue<(QreOutboundDiagnosticRecord Record, byte[] Bytes)> _records = new();

    public Func<QreOutboundDiagnosticRecord, bool>? Accept { get; init; }

    public bool Throw { get; init; }

    public IReadOnlyList<QreOutboundDiagnosticRecord> Records
        => _records.Select(static entry => entry.Record).OrderBy(static record => record.Sequence).ToArray();

    public string AllJson => string.Join('\n', _records.Select(static entry => Encoding.UTF8.GetString(entry.Bytes)));

    public bool TryWrite(QreOutboundDiagnosticRecord record, ReadOnlyMemory<byte> utf8Json)
    {
        if (Throw)
        {
            throw new IOException("sink failure");
        }
        if (Accept != null && !Accept(record))
        {
            return false;
        }
        _records.Enqueue((record, utf8Json.ToArray()));
        return true;
    }

    public IReadOnlyList<QreOutboundDiagnosticRecord> OfType(string eventType)
        => Records.Where(record => record.EventType == eventType).ToArray();
}

/// <summary>
/// Terminal handler with a per-request response factory; never opens a socket.
/// Like a real transport it serializes the request content before responding.
/// </summary>
internal sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    : HttpMessageHandler
{
    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Content-Length as computed before the transport serialized the content.</summary>
    public ConcurrentQueue<long?> ContentLengthsBeforeSend { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        ContentLengthsBeforeSend.Enqueue(request.Content?.Headers.ContentLength);
        if (request.Content != null)
        {
            await request.Content.LoadIntoBufferAsync(ct);
        }
        return await send(request, ct);
    }
}

internal static class DiagnosticsTestSupport
{
    public const string ChatModel = "qwen3-next-80b";

    public static QreOutboundDiagnostics Create(
        InMemoryDiagnosticSink sink,
        QreOutboundDiagnosticMode mode = QreOutboundDiagnosticMode.Structure,
        Func<QreOutboundDiagnosticsOptions, QreOutboundDiagnosticsOptions>? configure = null)
    {
        var options = new QreOutboundDiagnosticsOptions { Mode = mode };
        return QreOutboundDiagnostics.Create(configure?.Invoke(options) ?? options, sink);
    }

    /// <summary>Builds the real provider SDK client over <paramref name="transport"/> with the diagnostic handler.</summary>
    public static MeaiRuntimeModelClient CreateModelClient(
        QreOutboundDiagnostics? diagnostics,
        HttpMessageHandler transport,
        QreModelApiMode apiMode = QreModelApiMode.ChatCompletions,
        string model = ChatModel,
        Func<RuntimeModelRequest, ChatOptions>? optionsFactory = null,
        TimeSpan? timeout = null)
    {
        HttpMessageHandler pipeline = diagnostics == null ? transport : diagnostics.CreateHandler(transport);
        var http = new HttpClient(pipeline) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        var selector = QreModelProviderSelector.CreateDefault();
        var descriptor = new QreModelClientDescriptor
        {
            ApiUrl = new Uri("http://offline.test/v1"),
            ApiKey = "test-api-key",
            Model = model,
            ApiMode = apiMode,
            HttpClient = http
        };
        return new MeaiRuntimeModelClient(
            selector.CreateClient(descriptor),
            optionsFactory ?? CliMapping,
            diagnostics,
            QreOutboundDiagnosticsTarget.ForProvider(selector.Select(model), apiMode));
    }

    public static ChatOptions CliMapping(RuntimeModelRequest request)
        => new()
        {
            Temperature = request.Parameters.Temperature is { } temperature ? (float)temperature : null,
            MaxOutputTokens = request.Parameters.MaxOutputTokens,
            ResponseFormat = request.Parameters.RequireJsonObject ? ChatResponseFormat.Json : null
        };

    public static RuntimeModelRequest Request(
        IReadOnlyList<RuntimeToolDescriptor>? tools = null,
        RuntimeModelParameters? parameters = null,
        string stepId = "step-a",
        string text = "hello")
        => new(
            new RuntimeSessionId("diag-session"),
            new RuntimeTurnId("diag-turn"),
            new RuntimeStepId(stepId),
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(text)])],
            tools ?? [],
            parameters ?? new RuntimeModelParameters(Model: ChatModel),
            0);

    public static RuntimeToolDescriptor Tool(string name, string description = "Reads a file.", string schema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}")
    {
        using var document = JsonDocument.Parse(schema);
        return new RuntimeToolDescriptor(
            name,
            "1",
            description,
            document.RootElement.Clone(),
            RuntimeToolSideEffect.ReadOnly,
            RuntimeToolIdempotency.Idempotent);
    }

    public static HttpResponseMessage Sse(QreModelApiMode mode, QreOfflineResponse response)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(QreOfflineModelTransport.BuildStream(mode, response), Encoding.UTF8, "text/event-stream")
        };

    public static async Task<List<RuntimeModelStreamEvent>> DrainAsync(
        IAsyncEnumerable<RuntimeModelStreamEvent> stream,
        CancellationToken ct = default)
    {
        var events = new List<RuntimeModelStreamEvent>();
        await foreach (var runtimeEvent in stream.WithCancellation(ct))
        {
            events.Add(runtimeEvent);
        }
        return events;
    }
}
