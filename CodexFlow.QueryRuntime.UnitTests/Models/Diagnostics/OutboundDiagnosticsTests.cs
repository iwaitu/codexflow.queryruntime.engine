using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using CodexFlow.QueryRuntime.Cli.Diagnostics;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics.DiagnosticsTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics;

public sealed class OutboundDiagnosticsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_ProducesNoRecordsNoScopesAndNoContentWrapping()
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink, QreOutboundDiagnosticMode.Off);
        HttpContent? seen = null;
        var transport = new DelegateHandler((request, _) =>
        {
            seen = request.Content;
            Assert.Null(QreModelCallScope.Current);
            return Task.FromResult(Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("ok")));
        });
        var client = CreateModelClient(diagnostics, transport);

        var events = await DrainAsync(client.StreamAsync(Request(), new RuntimeModelAttemptContext(1), Ct), Ct);

        Assert.Empty(sink.Records);
        Assert.Equal("JsonContent", seen!.GetType().Name);
        Assert.IsType<RuntimeModelCompletedEvent>(events[^1]);
    }

    [Fact]
    public async Task Metadata_RecordsCorrelatedChainWithoutReadingOrWrappingBody()
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink, QreOutboundDiagnosticMode.Metadata);
        HttpContent? seen = null;
        var transport = new DelegateHandler((request, _) =>
        {
            seen = request.Content;
            return Task.FromResult(Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("ok")));
        });
        var client = CreateModelClient(diagnostics, transport);

        await DrainAsync(client.StreamAsync(Request(), new RuntimeModelAttemptContext(1, "attempt-x"), Ct), Ct);

        Assert.Equal("JsonContent", seen!.GetType().Name);
        Assert.Equal(
            [
                QreDiagnosticEventTypes.ModelCallStarted,
                QreDiagnosticEventTypes.AdapterPrepared,
                QreDiagnosticEventTypes.HttpAttemptStarted,
                QreDiagnosticEventTypes.HttpHeadersReceived,
                QreDiagnosticEventTypes.HttpAttemptEnded,
                QreDiagnosticEventTypes.ModelCallEnded
            ],
            sink.Records.Select(static record => record.EventType));
        Assert.Empty(sink.OfType(QreDiagnosticEventTypes.RequestStructureObserved));
        var callId = sink.Records[0].ModelCallId;
        Assert.All(sink.Records, record =>
        {
            Assert.Equal(callId, record.ModelCallId);
            Assert.Equal(1, record.RuntimeModelAttemptOrdinal);
            Assert.Equal("run-attempt-1", record.RunAttemptAlias);
            Assert.Equal("step-1", record.StepAlias);
            Assert.Equal("correlated", record.Correlation);
        });
        Assert.Equal(Enumerable.Range(1, sink.Records.Count).Select(static i => (long)i), sink.Records.Select(static r => r.Sequence));
        var started = sink.OfType(QreDiagnosticEventTypes.HttpAttemptStarted).Single();
        Assert.Equal("handler_visible", started.AttemptCoverage);
        Assert.Equal("{endpoint}/chat/completions", started.HttpRequest!.RouteTemplate);
        Assert.False(started.HttpRequest.ContentLengthKnown);
        var ended = sink.OfType(QreDiagnosticEventTypes.ModelCallEnded).Single().ModelOutcome!;
        Assert.Equal("completed", ended.Outcome);
        Assert.False(ended.FinishReasonObserved);
        Assert.Equal(QreDiagnosticClassifications.MissingFinishReason, ended.Classification);
        Assert.Equal(1, ended.HttpAttemptCount);
        Assert.False(diagnostics.GetCounters().EvidenceIncomplete);
    }

    [Fact]
    public async Task LegacyEntryPoint_MarksRuntimeAttemptOrdinalUnavailable()
    {
        var sink = new InMemoryDiagnosticSink();
        var client = CreateModelClient(Create(sink), new DelegateHandler((_, _) =>
            Task.FromResult(Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("ok")))));

        await DrainAsync(client.StreamAsync(Request(), Ct), Ct);

        Assert.All(sink.Records.Where(static r => r.ModelCallId != null), record =>
        {
            Assert.Null(record.RuntimeModelAttemptOrdinal);
            Assert.Equal("unavailable", record.RuntimeModelAttemptOrdinalStatus);
            Assert.Equal("unavailable", record.RunAttemptAlias);
        });
        Assert.Equal("runtime_model_attempt_unavailable", sink.OfType(QreDiagnosticEventTypes.ModelCallStarted).Single().ReasonCode);
    }

    [Fact]
    public async Task Facade_PassesAuthoritativeOrdinalIntoDiagnosticRecords()
    {
        var sink = new InMemoryDiagnosticSink();
        var client = CreateModelClient(Create(sink), new DelegateHandler((_, _) =>
            Task.FromResult(Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("ok")))));
        var runtime = new AgentRuntime(client);

        var result = await runtime.RunAsync(new RuntimeRunRequest(LoopRequest()), null, Ct);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        var started = sink.OfType(QreDiagnosticEventTypes.ModelCallStarted).Single();
        Assert.Equal(result.Turn.Steps[0].ModelAttempts, started.RuntimeModelAttemptOrdinal);
        Assert.Equal("present", started.RuntimeModelAttemptOrdinalStatus);
        Assert.NotEqual("unavailable", started.RunAttemptAlias);
    }

    [Fact]
    public async Task RuntimeRetry_CreatesNewModelCallIdsWithReducerOrdinals()
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink);
        var chat = new FlakyChatClient();
        var client = new MeaiRuntimeModelClient(chat, CliMapping, diagnostics);
        var runtime = new AgentRuntime(client);

        var result = await runtime.RunAsync(
            new RuntimeRunRequest(LoopRequest(maxModelRetries: 1)),
            null,
            Ct);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        var starts = sink.OfType(QreDiagnosticEventTypes.ModelCallStarted);
        Assert.Equal(2, starts.Count);
        Assert.NotEqual(starts[0].ModelCallId, starts[1].ModelCallId);
        Assert.Equal([1, 2], starts.Select(static s => s.RuntimeModelAttemptOrdinal!.Value));
        Assert.Equal(starts[0].StepAlias, starts[1].StepAlias);
        var ends = sink.OfType(QreDiagnosticEventTypes.ModelCallEnded);
        Assert.Equal("protocol_error", ends[0].ModelOutcome!.Classification);
        Assert.Equal("transient_provider", ends[0].ModelOutcome!.ErrorCode);
        Assert.Equal("completed", ends[1].ModelOutcome!.Outcome);
    }

    [Fact]
    public async Task SharedHttpClient_ConcurrentCallsNeverCrossCorrelate()
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink);
        var random = new Random(7);
        var transport = new DelegateHandler(async (_, ct) =>
        {
            await Task.Delay(random.Next(1, 15), ct);
            return Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("ok"));
        });
        var client = CreateModelClient(diagnostics, transport);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            DrainAsync(client.StreamAsync(Request(stepId: $"step-{i}"), new RuntimeModelAttemptContext(1), Ct), Ct), Ct)));

        var byCall = sink.Records.GroupBy(static r => r.ModelCallId).ToArray();
        Assert.Equal(12, byCall.Length);
        foreach (var group in byCall)
        {
            Assert.Single(group.Select(static r => r.StepAlias).Distinct());
            Assert.Single(group, static r => r.EventType == QreDiagnosticEventTypes.HttpAttemptStarted);
            Assert.Single(group, static r => r.EventType == QreDiagnosticEventTypes.HttpAttemptEnded);
            var httpIds = group.Where(static r => r.HttpAttemptId != null).Select(static r => r.HttpAttemptId).Distinct();
            Assert.Single(httpIds);
            Assert.All(group, static r => Assert.Equal("correlated", r.Correlation));
        }
        Assert.Equal(0, diagnostics.GetCounters().UncorrelatedHttpAttempts);
    }

    [Fact]
    public async Task SendWithoutModelCallScope_IsMarkedUncorrelated()
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink);
        using var http = new HttpClient(diagnostics.CreateHandler(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }))));

        using var response = await http.PostAsJsonAsync("http://offline.test/v1/chat/completions", new Dictionary<string, int> { ["n"] = 1 }, Ct);
        await response.Content.ReadAsStringAsync(Ct);

        Assert.All(sink.Records, static r => Assert.Equal("uncorrelated", r.Correlation));
        Assert.All(sink.Records, static r => Assert.Null(r.ModelCallId));
        Assert.Equal(1, diagnostics.GetCounters().UncorrelatedHttpAttempts);
    }

    [Fact]
    public async Task AsyncLocalBridge_DoesNotLeakAcrossYieldsToTheConsumer()
    {
        var sink = new InMemoryDiagnosticSink();
        QreModelCallContext? seenInHandler = null;
        var client = CreateModelClient(Create(sink), new DelegateHandler((_, _) =>
        {
            seenInHandler = QreModelCallScope.Current;
            return Task.FromResult(Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("ok")));
        }));

        await foreach (var _ in client.StreamAsync(Request(), new RuntimeModelAttemptContext(1), Ct))
        {
            Assert.Null(QreModelCallScope.Current);
        }

        Assert.NotNull(seenInHandler);
        Assert.Null(QreModelCallScope.Current);
    }

    [Fact]
    public async Task ResponseStream_EarlyDisposeIsRecordedOnceWithReadBytes()
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink);
        using var http = new HttpClient(diagnostics.CreateHandler(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 4096), Encoding.UTF8, "text/event-stream")
            }))));

        using (var response = await http.GetAsync("http://offline.test/v1/chat/completions", HttpCompletionOption.ResponseHeadersRead, Ct))
        await using (var stream = await response.Content.ReadAsStreamAsync(Ct))
        {
            var buffer = new byte[100];
            await stream.ReadExactlyAsync(buffer, Ct);
        }

        var ended = Assert.Single(sink.OfType(QreDiagnosticEventTypes.HttpAttemptEnded)).HttpOutcome!;
        Assert.Equal(QreDiagnosticStreamTerminations.DisposedBeforeEof, ended.StreamTermination);
        Assert.Equal(100, ended.ResponseBytesRead);
    }

    [Fact]
    public async Task ResponseStream_ReadErrorAndEofAreDistinguished()
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink);
        using var http = new HttpClient(diagnostics.CreateHandler(new DelegateHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("broken", StringComparison.Ordinal)
                    ? new StreamContent(new FailingStream())
                    : new StringContent("abc")
            }))));

        Assert.Equal("abc", await http.GetStringAsync("http://offline.test/ok", Ct));
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            using var response = await http.GetAsync("http://offline.test/broken", HttpCompletionOption.ResponseHeadersRead, Ct);
            await using var stream = await response.Content.ReadAsStreamAsync(Ct);
            _ = await stream.ReadAtLeastAsync(new byte[8], 1, throwOnEndOfStream: false, Ct);
        });

        var ends = sink.OfType(QreDiagnosticEventTypes.HttpAttemptEnded).Select(static r => r.HttpOutcome!).ToArray();
        Assert.Equal(QreDiagnosticStreamTerminations.Eof, ends[0].StreamTermination);
        Assert.Equal(3, ends[0].ResponseBytesRead);
        Assert.Equal(QreDiagnosticStreamTerminations.ReadError, ends[1].StreamTermination);
        Assert.Equal(QreDiagnosticFailurePhases.ResponseRead, ends[1].FailurePhase);
        Assert.Equal(QreDiagnosticClassifications.ReadError, ends[1].Classification);
    }

    [Fact]
    public async Task BeforeHeadersTransportError_IsClassifiedByType()
    {
        var sink = new InMemoryDiagnosticSink();
        var client = CreateModelClient(Create(sink), new DelegateHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ConnectionError, "connect failed secret-host"))));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            DrainAsync(client.StreamAsync(Request(), new RuntimeModelAttemptContext(1), Ct), Ct));

        var http = sink.OfType(QreDiagnosticEventTypes.HttpAttemptEnded).Single().HttpOutcome!;
        Assert.Equal(QreDiagnosticFailurePhases.BeforeHeaders, http.FailurePhase);
        Assert.Equal(QreDiagnosticClassifications.TransportError, http.Classification);
        Assert.Equal("ConnectionError", http.TransportErrorKind);
        var model = sink.OfType(QreDiagnosticEventTypes.ModelCallEnded).Single().ModelOutcome!;
        Assert.Equal(QreDiagnosticClassifications.TransportError, model.Classification);
        Assert.Equal(QreDiagnosticFailurePhases.BeforeHeaders, model.FailurePhase);
        Assert.DoesNotContain("secret-host", sink.AllJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpStatusFailure_IsClassifiedFromHandlerEvidenceNotMessage()
    {
        var sink = new InMemoryDiagnosticSink();
        var client = CreateModelClient(Create(sink), new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":\"CANARY-RATE-BODY\"}", Encoding.UTF8, "application/json"),
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(9)) }
            })));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DrainAsync(client.StreamAsync(Request(), new RuntimeModelAttemptContext(1), Ct), Ct));

        var headers = sink.OfType(QreDiagnosticEventTypes.HttpHeadersReceived).Single().HttpResponse!;
        Assert.Equal(429, headers.StatusCode);
        Assert.Equal(9, headers.RetryAfterSeconds);
        Assert.Equal("json", headers.ContentTypeCategory);
        Assert.Equal(QreDiagnosticClassifications.HttpStatusFailure, sink.OfType(QreDiagnosticEventTypes.HttpAttemptEnded).Single().HttpOutcome!.Classification);
        Assert.Equal(QreDiagnosticClassifications.HttpStatusFailure, sink.OfType(QreDiagnosticEventTypes.ModelCallEnded).Single().ModelOutcome!.Classification);
        Assert.DoesNotContain("CANARY-RATE-BODY", sink.AllJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpClientTimeout_IsNotMisreportedAsCallerCancellation()
    {
        var sink = new InMemoryDiagnosticSink();
        var client = CreateModelClient(
            Create(sink),
            new DelegateHandler(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable");
            }),
            timeout: TimeSpan.FromMilliseconds(150));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DrainAsync(client.StreamAsync(Request(), new RuntimeModelAttemptContext(1), Ct), Ct));

        Assert.IsType<TimeoutException>(exception.InnerException);
        var model = sink.OfType(QreDiagnosticEventTypes.ModelCallEnded).Single().ModelOutcome!;
        Assert.Equal(QreDiagnosticClassifications.HttpClientTimeout, model.Classification);
        Assert.Equal(QreDiagnosticClassificationSources.Adapter, model.ClassificationSource);
        Assert.Equal(QreDiagnosticFailurePhases.BeforeHeaders, model.FailurePhase);
        // The handler only sees HttpClient's linked token; without type evidence it stays unknown.
        var handler = sink.OfType(QreDiagnosticEventTypes.HttpAttemptEnded).Single().HttpOutcome!;
        Assert.Equal(QreDiagnosticClassifications.Unknown, handler.Classification);
        Assert.NotEqual(QreDiagnosticClassifications.CallerCancelled, handler.Classification);
    }

    [Fact]
    public async Task CallerCancellation_IsClassifiedFromUpstreamToken()
    {
        var sink = new InMemoryDiagnosticSink();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CreateModelClient(Create(sink), new DelegateHandler(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = DrainAsync(client.StreamAsync(Request(), new RuntimeModelAttemptContext(1), cts.Token), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(QreDiagnosticClassifications.CallerCancelled, sink.OfType(QreDiagnosticEventTypes.HttpAttemptEnded).Single().HttpOutcome!.Classification);
        var model = sink.OfType(QreDiagnosticEventTypes.ModelCallEnded).Single().ModelOutcome!;
        Assert.Equal("cancelled", model.Outcome);
        Assert.Equal(QreDiagnosticClassifications.CallerCancelled, model.Classification);
    }

    [Fact]
    public void Classification_CancellationAndTimeoutRaceIsReportedAsRace()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var context = new QreModelCallContext(
            Create(new InMemoryDiagnosticSink()), "mc", null, 1, "run", QreOutboundDiagnosticsTarget.Unknown, cts.Token);
        var race = new TaskCanceledException("t", new TimeoutException());

        Assert.Equal(QreDiagnosticClassifications.CancellationTimeoutRace, QreHttpAttempt.ClassifyException(race, context).Classification);
        Assert.Equal(QreDiagnosticClassifications.Unknown, QreHttpAttempt.ClassifyException(new TaskCanceledException(), null).Classification);
    }

    [Fact]
    public async Task Structure_ProjectsRequestWithoutSensitiveMaterial()
    {
        const string bodyCanary = "CANARY-PROMPT-BODY-91c2";
        const string schemaCanary = "CANARY-SCHEMA-DESC-44aa";
        const string descriptionCanary = "CANARY-TOOL-DESC-10bd";
        const string keyCanary = "CANARY-API-KEY-8d1e";
        const string tenantCanary = "canary-tenant-5e6f";
        const string modelCanary = "qwen3-canary-model-2b7c";
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink);
        var transport = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                QreOfflineModelTransport.BuildStream(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("CANARY-RESPONSE-BODY")),
                Encoding.UTF8,
                "text/event-stream"),
            Headers = { { "x-request-id", "CANARY-REQUEST-ID" }, { "set-cookie", "CANARY-COOKIE=1" } }
        }));
        var selector = QreModelProviderSelector.CreateDefault();
        var chat = selector.CreateClient(new QreModelClientDescriptor
        {
            ApiUrl = new Uri($"http://user:CANARY-USERINFO@{tenantCanary}.example.test/deploy-CANARY-PATH/v1?key=CANARY-QUERY"),
            ApiKey = keyCanary,
            Model = modelCanary,
            HttpClient = new HttpClient(diagnostics.CreateHandler(transport))
        });
        var client = new MeaiRuntimeModelClient(chat, CliMapping, diagnostics, QreOutboundDiagnosticsTarget.ForProvider(selector.Select(modelCanary), QreModelApiMode.ChatCompletions));
        var tool = Tool("qre_read_file", descriptionCanary, $"{{\"type\":\"object\",\"description\":\"{schemaCanary}\",\"properties\":{{\"path\":{{\"type\":\"string\",\"default\":\"{schemaCanary}\"}}}}}}");

        await DrainAsync(client.StreamAsync(
            Request([tool], new RuntimeModelParameters(Model: modelCanary, Temperature: 0.2, MaxOutputTokens: 64, RequireJsonObject: true), text: bodyCanary),
            new RuntimeModelAttemptContext(1),
            Ct), Ct);

        var json = sink.AllJson;
        foreach (var canary in new[]
                 {
                     bodyCanary, schemaCanary, descriptionCanary, keyCanary, tenantCanary, modelCanary, "CANARY-USERINFO",
                     "CANARY-PATH", "CANARY-QUERY", "CANARY-RESPONSE-BODY", "CANARY-REQUEST-ID", "CANARY-COOKIE",
                     "qre_read_file", "Bearer", "offline.test"
                 })
        {
            Assert.DoesNotContain(canary, json, StringComparison.OrdinalIgnoreCase);
        }

        var structure = sink.OfType(QreDiagnosticEventTypes.RequestStructureObserved).Single();
        Assert.Equal(QreDiagnosticCaptureStatus.Complete, structure.CaptureStatus);
        var http = structure.Request!;
        var runtime = sink.OfType(QreDiagnosticEventTypes.ModelCallStarted).Single().Request!;
        Assert.Equal(runtime.Tools.Aliases, http.Tools.Aliases);
        Assert.Equal(runtime.Model.Alias, http.Model.Alias);
        Assert.Equal(64, http.MaxOutputTokens.Value);
        Assert.Equal("json_object", http.ResponseFormat.Value);
        Assert.Equal("options.temperature", http.Temperature.SourcePath);
        Assert.Equal(0.2, http.Temperature.Value!.Value, 5);
        Assert.NotNull(sink.OfType(QreDiagnosticEventTypes.HttpHeadersReceived).Single().HttpResponse!.ProviderRequestIdAlias);
    }

    [Fact]
    public async Task ToolAliases_AreCaseSensitiveAndCaseOnlyMismatchIsVisible()
    {
        var sink = new InMemoryDiagnosticSink();
        var client = CreateModelClient(Create(sink), new DelegateHandler((_, _) =>
            Task.FromResult(Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("ok")))));

        await DrainAsync(client.StreamAsync(
            Request([Tool("qre_read_file")], new RuntimeModelParameters(Model: ChatModel, RequiredToolName: "QRE_READ_FILE")),
            new RuntimeModelAttemptContext(1),
            Ct), Ct);

        var runtime = sink.OfType(QreDiagnosticEventTypes.ModelCallStarted).Single().Request!;
        var adapter = sink.OfType(QreDiagnosticEventTypes.AdapterPrepared).Single().Request!;
        var http = sink.OfType(QreDiagnosticEventTypes.RequestStructureObserved).Single().Request!;
        Assert.NotEqual(runtime.Tools.Aliases.Single(), runtime.ToolChoice.RequiredToolAlias);
        Assert.Equal("case_only_mismatch", runtime.ToolChoice.RequiredToolRelation);
        Assert.Equal("case_only_mismatch", adapter.ToolChoice.RequiredToolRelation);
        Assert.Equal("case_only_mismatch", http.ToolChoice.RequiredToolRelation);
        Assert.Equal(runtime.ToolChoice.RequiredToolAlias, http.ToolChoice.RequiredToolAlias);
    }

    [Fact]
    public async Task CaptureLimit_OmitsStructureButSendsRequestIntact()
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink, configure: static o => o with { MaxRequestCaptureBytes = 1024 });
        byte[]? sent = null;
        var client = CreateModelClient(diagnostics, new DelegateHandler(async (request, ct) =>
        {
            sent = await request.Content!.ReadAsByteArrayAsync(ct);
            return Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("ok"));
        }));

        await DrainAsync(client.StreamAsync(Request(text: new string('a', 5000)), new RuntimeModelAttemptContext(1), Ct), Ct);

        var structure = sink.OfType(QreDiagnosticEventTypes.RequestStructureObserved).Single();
        Assert.Equal(QreDiagnosticCaptureStatus.Omitted, structure.CaptureStatus);
        Assert.Equal("capture_limit_exceeded", structure.ReasonCode);
        Assert.Null(structure.Request);
        Assert.True(sent!.Length > 5000);
        Assert.Equal(sent.Length, structure.HttpRequest!.ObservedRequestBytes);
    }

    [Fact]
    public async Task DeepOrInvalidJson_FailsProjectionOnly()
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink, configure: static o => o with { MaxJsonDepth = 4 });
        var deep = "{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":1}}}}}}";
        using var http = new HttpClient(diagnostics.CreateHandler(new DelegateHandler(async (request, ct) =>
        {
            Assert.Equal(deep, await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK);
        })));

        using var response = await http.PostAsync(
            "http://offline.test/v1/chat/completions",
            new StringContent(deep, Encoding.UTF8, "application/json"),
            Ct);

        var structure = sink.OfType(QreDiagnosticEventTypes.RequestStructureObserved).Single();
        Assert.Equal(QreDiagnosticCaptureStatus.Failed, structure.CaptureStatus);
        Assert.Equal(1, diagnostics.GetCounters().ProjectionFailures);
        Assert.True(diagnostics.GetCounters().EvidenceIncomplete);
    }

    [Theory]
    [InlineData("multipart")]
    [InlineData("gzip")]
    [InlineData("binary")]
    public async Task UnsupportedContent_IsNotWrappedAndReportsUnsupported(string kind)
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink);
        HttpContent content = kind switch
        {
            "multipart" => new MultipartFormDataContent { { new StringContent("x"), "f" } },
            "gzip" => new StringContent("{}", Encoding.UTF8, "application/json") { Headers = { ContentEncoding = { "gzip" } } },
            _ => new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("application/octet-stream") } }
        };
        HttpContent? seen = null;
        using var http = new HttpClient(diagnostics.CreateHandler(new DelegateHandler((request, _) =>
        {
            seen = request.Content;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        })));

        using var response = await http.PostAsync("http://offline.test/v1/chat/completions", content, Ct);

        Assert.Same(content, seen);
        Assert.Equal(QreDiagnosticCaptureStatus.Unsupported, sink.OfType(QreDiagnosticEventTypes.RequestStructureObserved).Single().CaptureStatus);
    }

    [Fact]
    public async Task SinkFailuresAndDrops_NeverAffectTheModelCall()
    {
        var throwing = new InMemoryDiagnosticSink { Throw = true };
        var dropping = new InMemoryDiagnosticSink { Accept = static _ => false };
        foreach (var sink in new[] { throwing, dropping })
        {
            var diagnostics = Create(sink);
            var client = CreateModelClient(diagnostics, new DelegateHandler((_, _) =>
                Task.FromResult(Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("still works")))));

            var events = await DrainAsync(client.StreamAsync(Request(), new RuntimeModelAttemptContext(1), Ct), Ct);

            Assert.Equal("still works", Assert.IsType<RuntimeTextDeltaEvent>(events[0]).Text);
            Assert.True(diagnostics.GetCounters().RecordsDropped > 0);
            Assert.True(diagnostics.GetCounters().EvidenceIncomplete);
        }
    }

    [Fact]
    public async Task OversizedRecord_IsReducedToAnOmittedEnvelope()
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink, configure: static o => o with { MaxRecordBytes = 1024 });
        var tools = Enumerable.Range(0, 80).Select(static i => Tool($"tool_{i:D3}")).ToArray();
        var client = CreateModelClient(diagnostics, new DelegateHandler((_, _) =>
            Task.FromResult(Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("ok")))));

        await DrainAsync(client.StreamAsync(Request(tools), new RuntimeModelAttemptContext(1), Ct), Ct);

        var started = sink.OfType(QreDiagnosticEventTypes.ModelCallStarted).Single();
        Assert.Equal(QreDiagnosticCaptureStatus.Omitted, started.CaptureStatus);
        Assert.Equal("record_size_limit_exceeded", started.ReasonCode);
        Assert.True(diagnostics.GetCounters().RecordsOversized > 0);
    }

    [Fact]
    public void Options_ValidateBoundsAndRetentionCap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QreOutboundDiagnosticsOptions { Retention = TimeSpan.FromDays(31) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new QreOutboundDiagnosticsOptions { MaxJsonDepth = 1 }.Validate());
        Assert.Throws<ArgumentException>(() => new QreOutboundDiagnosticsOptions { ProjectionPolicyVersion = " " }.Validate());
        var defaults = new QreOutboundDiagnosticsOptions().Validate();
        Assert.Equal(QreOutboundDiagnosticMode.Off, defaults.Mode);
        Assert.Equal(64 * 1024, defaults.MaxRequestCaptureBytes);
        Assert.Equal(QreOutboundDiagnosticFailurePolicy.BestEffort, defaults.FailurePolicy);
    }

    private static RuntimeAgentLoopRequest LoopRequest(int maxModelRetries = 0)
        => new(
            new RuntimeSessionId("diag-session"),
            new RuntimeTurnId("diag-turn"),
            "diagnostics objective",
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("start")])],
            [],
            new RuntimeModelParameters(Model: ChatModel),
            new RuntimePolicySnapshot("policy-v1", "readonly"),
            new RuntimeEnvironmentSnapshot("local", "workspace", "sha256:test"),
            new RuntimeBudgetSnapshot(3, 4, maxModelRetries: maxModelRetries, maxContinuations: 1),
            CreatedAt: DateTimeOffset.UnixEpoch);

    private sealed class FlakyChatClient : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new RuntimeModelClientException(new RuntimeError(
                    RuntimeErrorCategory.ProviderTransport,
                    "transient_provider",
                    "transient",
                    Retryable: true));
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "recovered");
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("simulated read failure");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("simulated read failure"));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
