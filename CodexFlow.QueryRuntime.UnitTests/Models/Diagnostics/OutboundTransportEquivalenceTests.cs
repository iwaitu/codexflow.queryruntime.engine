using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using CodexFlow.QueryRuntime.Cli.Diagnostics;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics.DiagnosticsTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics;

/// <summary>
/// Hard gate for Structure mode: the observing request wrapper must leave the
/// bytes, length semantics and per-protocol transfer behavior unchanged. A real
/// loopback Kestrel server records what actually arrived on the wire.
/// </summary>
public sealed class OutboundTransportEquivalenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Http11_RealSdkRequestIsByteAndFramingEquivalentWithStructureCapture()
    {
        await using var server = await LoopbackServer.StartAsync(HttpProtocols.Http1);
        var observedSink = new InMemoryDiagnosticSink();

        await SendThroughSdkAsync(server, diagnostics: null);
        await SendThroughSdkAsync(server, Create(observedSink));

        var requests = server.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(requests[0].Body, requests[1].Body);
        Assert.All(requests, request =>
        {
            Assert.Equal("HTTP/1.1", request.Protocol);
            Assert.Null(request.ContentLength);
            Assert.Equal("chunked", request.TransferEncoding);
        });
        var structure = Assert.Single(observedSink.OfType(QreDiagnosticEventTypes.RequestStructureObserved));
        Assert.Equal(QreDiagnosticCaptureStatus.Complete, structure.CaptureStatus);
        Assert.Equal(requests[1].Body.Length, structure.HttpRequest!.ObservedRequestBytes);
        var ended = Assert.Single(observedSink.OfType(QreDiagnosticEventTypes.ModelCallEnded));
        Assert.Equal("completed", ended.ModelOutcome!.Outcome);
    }

    [Fact]
    public async Task Http2_DeferredJsonContentKeepsUnknownLengthAndBytes()
    {
        await using var server = await LoopbackServer.StartAsync(HttpProtocols.Http2);
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink);
        var payload = new Dictionary<string, object> { ["model"] = "m", ["messages"] = new[] { new Dictionary<string, string> { ["role"] = "user", ["content"] = "hi" } } };

        foreach (var observed in new[] { false, true })
        {
            HttpMessageHandler handler = new SocketsHttpHandler();
            if (observed)
            {
                handler = diagnostics.CreateHandler(handler);
            }
            using var http = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.BaseUri, "/v1/chat/completions"))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = JsonContent.Create(payload)
            };
            using var response = await http.SendAsync(request, Ct);
            await response.Content.ReadAsStringAsync(Ct);
        }

        var requests = server.Requests.ToArray();
        Assert.Equal(requests[0].Body, requests[1].Body);
        Assert.All(requests, request =>
        {
            Assert.Equal("HTTP/2", request.Protocol);
            Assert.Null(request.ContentLength);
            Assert.Null(request.TransferEncoding);
        });
        Assert.Equal("2.0", sink.OfType(QreDiagnosticEventTypes.HttpAttemptStarted).Single().HttpRequest!.HttpVersion);
        Assert.Equal(QreDiagnosticCaptureStatus.Complete, sink.OfType(QreDiagnosticEventTypes.RequestStructureObserved).Single().CaptureStatus);
    }

    [Fact]
    public async Task Http11_KnownLengthContentKeepsContentLengthAndNoChunking()
    {
        await using var server = await LoopbackServer.StartAsync(HttpProtocols.Http1);
        var diagnostics = Create(new InMemoryDiagnosticSink());
        const string json = "{\"model\":\"m\",\"messages\":[]}";

        foreach (var observed in new[] { false, true })
        {
            HttpMessageHandler handler = new SocketsHttpHandler();
            if (observed)
            {
                handler = diagnostics.CreateHandler(handler);
            }
            using var http = new HttpClient(handler);
            using var response = await http.PostAsync(
                new Uri(server.BaseUri, "/v1/chat/completions"),
                new StringContent(json, Encoding.UTF8, "application/json"),
                Ct);
        }

        var requests = server.Requests.ToArray();
        Assert.All(requests, request =>
        {
            Assert.Equal(Encoding.UTF8.GetByteCount(json), request.ContentLength);
            Assert.Null(request.TransferEncoding);
            Assert.Equal(json, Encoding.UTF8.GetString(request.Body));
        });
    }

    [Fact]
    public async Task RetryHandlerAbove_ResendsOriginalContentAndEachAttemptIsObserved()
    {
        await using var server = await LoopbackServer.StartAsync(HttpProtocols.Http1, failFirst: true);
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink);
        using var http = new HttpClient(new ResendOnceHandler { InnerHandler = diagnostics.CreateHandler(new SocketsHttpHandler()) });
        var content = JsonContent.Create(new Dictionary<string, string> { ["model"] = "m" });

        using var response = await http.PostAsync(new Uri(server.BaseUri, "/v1/chat/completions"), content, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requests = server.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(requests[0].Body, requests[1].Body);
        Assert.Equal(2, sink.OfType(QreDiagnosticEventTypes.HttpAttemptStarted).Count);
        Assert.All(sink.OfType(QreDiagnosticEventTypes.RequestStructureObserved), static s =>
            Assert.Equal(QreDiagnosticCaptureStatus.Complete, s.CaptureStatus));
    }

    [Fact]
    public async Task CancellationDuringRequestWrite_PropagatesAndYieldsOnlyPartialEvidence()
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var http = new HttpClient(diagnostics.CreateHandler(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))));
        var content = new BlockingJsonContent();

        var send = http.PostAsync("http://offline.test/v1/chat/completions", content, cts.Token);
        await content.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        var structure = Assert.Single(sink.OfType(QreDiagnosticEventTypes.RequestStructureObserved));
        Assert.Equal(QreDiagnosticCaptureStatus.Partial, structure.CaptureStatus);
        Assert.Null(structure.Request);
        Assert.Equal(2, structure.HttpRequest!.ObservedRequestBytes);
    }

    private static async Task SendThroughSdkAsync(LoopbackServer server, QreOutboundDiagnostics? diagnostics)
    {
        HttpMessageHandler transport = new SocketsHttpHandler();
        if (diagnostics != null)
        {
            transport = diagnostics.CreateHandler(transport);
        }
        var selector = QreModelProviderSelector.CreateDefault();
        using var client = new MeaiRuntimeModelClient(
            selector.CreateClient(new QreModelClientDescriptor
            {
                ApiUrl = new Uri(server.BaseUri, "/v1"),
                ApiKey = "equivalence-key",
                Model = ChatModel,
                HttpClient = new HttpClient(transport)
            }),
            CliMapping,
            diagnostics,
            QreOutboundDiagnosticsTarget.ForProvider(selector.Select(ChatModel), QreModelApiMode.ChatCompletions));
        var request = Request(
            [Tool("qre_read_file")],
            new RuntimeModelParameters(Model: ChatModel, Temperature: 0.3, MaxOutputTokens: 77, RequireJsonObject: true, RequiredToolName: "qre_read_file"));
        var events = await DrainAsync(client.StreamAsync(request, new RuntimeModelAttemptContext(1), Ct), Ct);
        Assert.Equal("wire ok", Assert.IsType<RuntimeTextDeltaEvent>(events[0]).Text);
    }

    private sealed record CapturedRequest(string Protocol, long? ContentLength, string? TransferEncoding, byte[] Body);

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private LoopbackServer(WebApplication app, Uri baseUri)
        {
            _app = app;
            BaseUri = baseUri;
        }

        public Uri BaseUri { get; }

        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();

        public static async Task<LoopbackServer> StartAsync(HttpProtocols protocols, bool failFirst = false)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = protocols));
            var app = builder.Build();
            LoopbackServer? server = null;
            var count = 0;
            app.Run(async context =>
            {
                using var buffer = new MemoryStream();
                await context.Request.Body.CopyToAsync(buffer);
                server!.Requests.Enqueue(new CapturedRequest(
                    context.Request.Protocol,
                    context.Request.ContentLength,
                    context.Request.Headers.TransferEncoding.FirstOrDefault(),
                    buffer.ToArray()));
                if (failFirst && Interlocked.Increment(ref count) == 1)
                {
                    context.Response.StatusCode = 503;
                    return;
                }
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync(QreOfflineModelTransport.BuildStream(
                    QreModelApiMode.ChatCompletions,
                    QreOfflineResponse.TextResponse("wire ok")));
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            server = new LoopbackServer(app, new Uri(address));
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class ResendOnceHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var first = await base.SendAsync(request, cancellationToken);
            if (first.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                return first;
            }
            first.Dispose();
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class BlockingJsonContent : HttpContent
    {
        public BlockingJsonContent() => Headers.ContentType = new("application/json");

        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await stream.WriteAsync("{\""u8.ToArray(), cancellationToken);
            WriteStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
