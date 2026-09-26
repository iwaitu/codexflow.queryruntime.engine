using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;

// Offline outbound SDK diagnostics example. Nothing here opens a network
// connection: the SDK sends into an in-process terminal handler.
//
//   dotnet run --project examples/SdkOutboundDiagnostics -- demo --workspace <dir> [--fixed]
//   dotnet run --project examples/SdkOutboundDiagnostics -- hosts
//
// The demo host below has a deliberately defective options factory. It exists
// only in this example; the CLI mapping is correct.
var command = args.FirstOrDefault() ?? "demo";
var workspace = Path.GetFullPath(ArgumentAfter(args, "--workspace") ?? ".");
var fixedHost = args.Contains("--fixed");
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));

return command switch
{
    "demo" => await DefectiveHostDemo.RunAsync(workspace, fixedHost, cancellation.Token),
    "hosts" => await HostIntegrations.RunAsync(cancellation.Token),
    _ => Usage()
};

static int Usage()
{
    Console.Error.WriteLine("usage: SdkOutboundDiagnostics demo --workspace <dir> [--fixed] | hosts");
    return 2;
}

static string? ArgumentAfter(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

/// <summary>
/// Demo: the Runtime explicitly sets MaxOutputTokens and RequireJsonObject, the
/// host's options factory forgets to map them, and diagnostics capture the
/// Runtime, adapter and HTTP observation points. <c>qre diagnose latest</c> then
/// reports the first loss at runtime_prepared -> adapter_prepared.
/// </summary>
internal static class DefectiveHostDemo
{
    public const string Model = "qwen3-next-80b";

    // The intentional defect: only temperature is forwarded.
    public static ChatOptions DefectiveMapping(RuntimeModelRequest request)
        => new() { Temperature = request.Parameters.Temperature is { } t ? (float)t : null };

    // The fix, applied only in this demo host. A new instance per call avoids
    // sharing mutable ChatOptions between concurrent calls.
    public static ChatOptions FixedMapping(RuntimeModelRequest request)
        => new()
        {
            Temperature = request.Parameters.Temperature is { } t ? (float)t : null,
            MaxOutputTokens = request.Parameters.MaxOutputTokens,
            ResponseFormat = request.Parameters.RequireJsonObject ? ChatResponseFormat.Json : null
        };

    public static async Task<int> RunAsync(string workspace, bool fixedHost, CancellationToken ct)
    {
        var options = new QreOutboundDiagnosticsOptions { Mode = QreOutboundDiagnosticMode.Structure };
        await using var sink = MinimalDiagnosticSink.Create(workspace, options);
        var diagnostics = QreOutboundDiagnostics.Create(options, sink);

        // QRE-owned HttpClient: the diagnostic handler is added while the pipeline
        // is built. The locked SDK disposes this client with the chat client.
        var http = new HttpClient(diagnostics.CreateHandler(new OfflineSseHandler("{\"fixture\":\"ok\"}")));
        var selector = QreModelProviderSelector.CreateDefault();
        var descriptor = new QreModelClientDescriptor
        {
            ApiUrl = new Uri("http://offline.invalid/v1"),
            ApiKey = "example-offline-key",
            Model = Model,
            HttpClient = http
        };
        var target = QreOutboundDiagnosticsTarget.ForProvider(selector.Select(Model), descriptor.ApiMode);
        using var modelClient = new MeaiRuntimeModelClient(
            selector.CreateClient(descriptor),
            fixedHost ? FixedMapping : DefectiveMapping,
            diagnostics,
            target);

        var request = new RuntimeAgentLoopRequest(
            new RuntimeSessionId("sdk-diagnostics-demo"),
            new RuntimeTurnId("sdk-diagnostics-demo-turn"),
            "Return a JSON object describing the fixture.",
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("Return a JSON object describing the fixture.")])],
            [],
            new RuntimeModelParameters(Model: Model, Temperature: 0.2, MaxOutputTokens: 256, RequireJsonObject: true),
            new RuntimePolicySnapshot("example-v1", "none"),
            new RuntimeEnvironmentSnapshot("local", workspace, "sdk-diagnostics-demo"),
            new RuntimeBudgetSnapshot(2, 1, maxContinuations: 0));
        var result = await new AgentRuntime(modelClient).RunAsync(new RuntimeRunRequest(request), null, ct);
        await sink.CompleteAsync(diagnostics, target);

        Console.WriteLine($"host: {(fixedHost ? "fixed" : "defective")} options factory");
        Console.WriteLine($"status: {result.Status}");
        Console.WriteLine($"diagnostics: {sink.RunDirectory}");
        Console.WriteLine($"next: qre diagnose latest --workspace \"{workspace}\"");
        return result.Status == RuntimeTurnStatus.Completed ? 0 : 1;
    }
}

/// <summary>
/// The three supported host integration shapes and what each can observe.
/// </summary>
internal static class HostIntegrations
{
    public static async Task<int> RunAsync(CancellationToken ct)
    {
        var request = new RuntimeModelRequest(
            new RuntimeSessionId("hosts"),
            new RuntimeTurnId("hosts-turn"),
            new RuntimeStepId("hosts-step"),
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("hello")])],
            [],
            new RuntimeModelParameters(Model: DefectiveHostDemo.Model),
            0);
        var selector = QreModelProviderSelector.CreateDefault();
        var target = QreOutboundDiagnosticsTarget.ForProvider(selector.Select(DefectiveHostDemo.Model), QreModelApiMode.ChatCompletions);

        // 1. QRE-owned HttpClient: dedicated to the model client and disposed with it.
        {
            var sink = new CountingSink();
            var diagnostics = QreOutboundDiagnostics.Create(new() { Mode = QreOutboundDiagnosticMode.Structure }, sink);
            var client = new HttpClient(diagnostics.CreateHandler(new OfflineSseHandler("owned")));
            using var model = new MeaiRuntimeModelClient(Build(selector, client), DefectiveHostDemo.FixedMapping, diagnostics, target);
            await Drain(model.StreamAsync(request, new RuntimeModelAttemptContext(1), ct));
            Console.WriteLine($"owned-httpclient: records={sink.Count} observation=runtime,adapter,http");
        }

        // 2. Host-shared connection pool: the host keeps its handler; each model
        // client gets its own HttpClient with disposeHandler: false. Passing a
        // shared HttpClient itself is unsupported with the locked SDK: disposing
        // the chat client disposes it and the SDK writes auth headers onto it.
        using (var sharedTransport = new OfflineSseHandler("shared"))
        {
            var sink = new CountingSink();
            var diagnostics = QreOutboundDiagnostics.Create(new() { Mode = QreOutboundDiagnosticMode.Metadata }, sink);
            var perModelClient = new HttpClient(diagnostics.CreateHandler(sharedTransport), disposeHandler: false);
            using (var model = new MeaiRuntimeModelClient(Build(selector, perModelClient), DefectiveHostDemo.FixedMapping, diagnostics, target))
            {
                await Drain(model.StreamAsync(request, new RuntimeModelAttemptContext(1), ct));
            }
            using var stillUsable = new HttpClient(sharedTransport, disposeHandler: false);
            var status = (await stillUsable.GetAsync("http://offline.invalid/health", ct)).StatusCode;
            Console.WriteLine($"shared-handler: records={sink.Count} shared_transport_after_dispose={status}");
        }

        // 3. Custom IChatClient: no injected transport, so only the Runtime and
        // adapter observation points exist (transport_capture_unavailable).
        {
            var sink = new CountingSink();
            var diagnostics = QreOutboundDiagnostics.Create(new() { Mode = QreOutboundDiagnosticMode.Structure }, sink);
            using var model = new MeaiRuntimeModelClient(new CustomChatClient(), DefectiveHostDemo.FixedMapping, diagnostics);
            await Drain(model.StreamAsync(request, new RuntimeModelAttemptContext(1), ct));
            Console.WriteLine($"custom-ichatclient: records={sink.Count} observation=runtime,adapter transport=transport_capture_unavailable");
        }
        return 0;
    }

    private static IChatClient Build(QreModelProviderSelector selector, HttpClient client)
        => selector.CreateClient(new QreModelClientDescriptor
        {
            ApiUrl = new Uri("http://offline.invalid/v1"),
            ApiKey = "example-offline-key",
            Model = DefectiveHostDemo.Model,
            HttpClient = client
        });

    private static async Task Drain(IAsyncEnumerable<RuntimeModelStreamEvent> events)
    {
        await foreach (var _ in events)
        {
        }
    }

    private sealed class CountingSink : IQreOutboundDiagnosticSink
    {
        public int Count;

        public bool TryWrite(QreOutboundDiagnosticRecord record, ReadOnlyMemory<byte> utf8Json)
        {
            Interlocked.Increment(ref Count);
            return true;
        }
    }

    private sealed class CustomChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "custom transport");
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Minimal safe sink writing the CLI diagnostic package contract
/// (<c>manifest.json</c> + <c>events.jsonl</c>) so <c>qre diagnose</c> can read it.
/// Records are already projected; the sink stores their bytes and never adds
/// anything else. Production embedders should also apply private permissions,
/// bounded queues and retention like the CLI store does.
/// </summary>
internal sealed class MinimalDiagnosticSink : IQreOutboundDiagnosticSink, IAsyncDisposable
{
    private readonly FileStream _events;
    private readonly QreOutboundDiagnosticsOptions _options;
    private readonly DateTimeOffset _created = DateTimeOffset.UtcNow;
    private long _written;

    private MinimalDiagnosticSink(string runDirectory, QreOutboundDiagnosticsOptions options)
    {
        RunDirectory = runDirectory;
        _options = options;
        _events = new FileStream(Path.Combine(runDirectory, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }

    public string RunDirectory { get; }

    public static MinimalDiagnosticSink Create(string workspace, QreOutboundDiagnosticsOptions options)
    {
        var root = Path.Combine(workspace, ".qre", "v2", "diagnostics");
        var run = Path.Combine(root, $"diag-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-example");
        Directory.CreateDirectory(run);
        return new MinimalDiagnosticSink(run, options);
    }

    public bool TryWrite(QreOutboundDiagnosticRecord record, ReadOnlyMemory<byte> utf8Json)
    {
        lock (_events)
        {
            _events.Write(utf8Json.Span);
            _events.WriteByte((byte)'\n');
            _events.Flush();
            _written++;
        }
        return true;
    }

    public async Task CompleteAsync(QreOutboundDiagnostics diagnostics, QreOutboundDiagnosticsTarget target)
    {
        var counters = diagnostics.GetCounters();
        var cell = QreTransportCapabilityMatrix.Get(target.ProviderCategory, target.ApiMode);
        var manifest = new JsonObject
        {
            ["manifestSchema"] = "qre.outbound-diagnostics.manifest/1",
            ["eventSchema"] = QreOutboundDiagnosticSchema.SchemaVersion,
            ["projectionPolicyVersion"] = _options.ProjectionPolicyVersion,
            ["normalizerVersion"] = QreOutboundDiagnosticSchema.NormalizerVersion,
            ["diagnosticRunId"] = Path.GetFileName(RunDirectory),
            ["segmentId"] = diagnostics.SegmentId,
            ["mode"] = _options.Mode == QreOutboundDiagnosticMode.Structure ? "structure" : "metadata",
            ["completionStatus"] = counters.EvidenceIncomplete ? "incomplete" : "complete",
            ["createdUtc"] = _created,
            ["completedUtc"] = DateTimeOffset.UtcNow,
            ["observationScope"] = "client_side_handler",
            ["coverage"] = new JsonObject
            {
                ["transportCapture"] = "handler",
                ["attemptCoverage"] = "handler_visible",
                ["providerCategory"] = target.ProviderCategory,
                ["apiMode"] = target.ApiMode,
                ["sdkVersion"] = target.SdkVersion,
                ["capabilityStatus"] = cell.Status,
                ["requestStructure"] = _options.Mode == QreOutboundDiagnosticMode.Structure,
                ["responseBodies"] = false
            },
            ["quotas"] = new JsonObject
            {
                ["maxRequestCaptureBytes"] = _options.MaxRequestCaptureBytes,
                ["maxRecordBytes"] = _options.MaxRecordBytes,
                ["maxRunBytes"] = _options.MaxRunBytes,
                ["maxPendingRecords"] = _options.MaxPendingRecords,
                ["maxPendingBytes"] = _options.MaxPendingBytes,
                ["maxJsonDepth"] = _options.MaxJsonDepth,
                ["retentionDays"] = (int)_options.Retention.TotalDays
            },
            ["counters"] = new JsonObject
            {
                ["recordsEmitted"] = counters.RecordsEmitted,
                ["recordsWritten"] = _written,
                ["recordsDropped"] = counters.RecordsDropped,
                ["modelCallsStarted"] = counters.ModelCallsStarted,
                ["modelCallsEnded"] = counters.ModelCallsEnded,
                ["httpAttemptsStarted"] = counters.HttpAttemptsStarted,
                ["httpAttemptsEnded"] = counters.HttpAttemptsEnded,
                ["evidenceIncomplete"] = counters.EvidenceIncomplete
            }
        };
        var temporary = Path.Combine(RunDirectory, "manifest.json.tmp");
        await File.WriteAllTextAsync(temporary, manifest.ToJsonString());
        File.Move(temporary, Path.Combine(RunDirectory, "manifest.json"), overwrite: true);
    }

    public async ValueTask DisposeAsync() => await _events.DisposeAsync();
}

/// <summary>In-process terminal transport returning one streaming chat-completions text response.</summary>
internal sealed class OfflineSseHandler(string text) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content != null)
        {
            // Serialize the request as a real transport would.
            await request.Content.LoadIntoBufferAsync(ct);
        }
        if (request.Method == HttpMethod.Get)
        {
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        var delta = new JsonObject { ["role"] = "assistant", ["content"] = text };
        var sse = new StringBuilder()
            .Append("data: ").Append(Chunk(delta, null)).Append("\n\n")
            .Append("data: ").Append(Chunk(new JsonObject(), "stop")).Append("\n\n")
            .Append("data: [DONE]\n\n")
            .ToString();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };
    }

    private static string Chunk(JsonObject delta, string? finish)
        => new JsonObject
        {
            ["id"] = "chatcmpl-example",
            ["object"] = "chat.completion.chunk",
            ["created"] = 1,
            ["model"] = "offline",
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish })
        }.ToJsonString();
}
