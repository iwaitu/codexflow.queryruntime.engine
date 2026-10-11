using System.Net;
using System.Net.Sockets;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using CodexFlow.QueryRuntime.Protocol;
using CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Models;

public sealed class QreModelErrorClassifierTests
{
    [Theory]
    [InlineData(429, RuntimeErrorCategory.ProviderRateLimit, "provider_rate_limited", true)]
    [InlineData(408, RuntimeErrorCategory.ProviderTransport, "provider_timeout", true)]
    [InlineData(500, RuntimeErrorCategory.ProviderTransport, "provider_server_error", true)]
    [InlineData(502, RuntimeErrorCategory.ProviderTransport, "provider_server_error", true)]
    [InlineData(503, RuntimeErrorCategory.ProviderTransport, "provider_server_error", true)]
    [InlineData(504, RuntimeErrorCategory.ProviderTransport, "provider_server_error", true)]
    [InlineData(401, RuntimeErrorCategory.ProviderAuthentication, "provider_authentication_failed", false)]
    [InlineData(403, RuntimeErrorCategory.ProviderAuthentication, "provider_authentication_failed", false)]
    [InlineData(400, RuntimeErrorCategory.ProviderProtocol, "provider_request_rejected", false)]
    [InlineData(404, RuntimeErrorCategory.ProviderProtocol, "provider_request_rejected", false)]
    [InlineData(422, RuntimeErrorCategory.ProviderProtocol, "provider_request_rejected", false)]
    public void StatusCodes_MapByTable(int status, RuntimeErrorCategory category, string code, bool retryable)
    {
        foreach (var exception in new Exception[]
                 {
                     new HttpRequestException("failed", null, (HttpStatusCode)status),
                     new InvalidOperationException("Vllm error: {}")
                 })
        {
            var observed = exception is HttpRequestException ? (int?)null : status;
            var error = QreModelErrorClassifier.Classify(exception, CancellationToken.None, observed)!.Error;
            Assert.Equal(category, error.Category);
            Assert.Equal(code, error.Code);
            Assert.Equal(retryable, error.Retryable);
        }
    }

    [Fact]
    public void StatusCode_IsParsedFromEnsureSuccessMessage()
    {
        var error = QreModelErrorClassifier.Classify(
            new HttpRequestException("Response status code does not indicate success: 503 (Service Unavailable)."),
            CancellationToken.None)!.Error;

        Assert.Equal("provider_server_error", error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public void Exceptions_WithoutStatusMapByType()
    {
        static RuntimeError Classify(Exception exception) => QreModelErrorClassifier.Classify(exception, CancellationToken.None)!.Error;

        Assert.Equal(("provider_transport_failed", true), Pair(Classify(new HttpRequestException("refused", new SocketException(61)))));
        Assert.Equal(("provider_transport_failed", true), Pair(Classify(new IOException("reset"))));
        Assert.Equal(("provider_transport_failed", true), Pair(Classify(new SocketException(54))));
        Assert.Equal(("provider_timeout", true), Pair(Classify(new TaskCanceledException("HttpClient.Timeout", new TimeoutException()))));
        Assert.Equal(("provider_authentication_failed", false), Pair(Classify(new InvalidOperationException("Vllm error: Unauthorized"))));
        Assert.Equal(("provider_authentication_failed", false), Pair(Classify(new InvalidOperationException("Authentication failed for key"))));
        Assert.Equal(("malformed_tool_call", false), Pair(Classify(new InvalidOperationException("tool_calls with empty function.name in non-stream response."))));
        Assert.Equal(("model_stream_failed", false), Pair(Classify(new InvalidOperationException("something else"))));
        Assert.Equal(RuntimeErrorCategory.ProviderAuthentication, Classify(new InvalidOperationException("Unauthorized")).Category);
    }

    [Fact]
    public void CallerCancellation_IsNotClassified()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(QreModelErrorClassifier.Classify(new OperationCanceledException(cts.Token), cts.Token));
        Assert.Null(QreModelErrorClassifier.Classify(new TaskCanceledException(), cts.Token));
    }

    [Fact]
    public void RetryAfter_ComesFromObservationOrExceptionData()
    {
        var observed = QreModelErrorClassifier.Classify(
            new InvalidOperationException("Vllm error"),
            CancellationToken.None,
            429,
            TimeSpan.FromSeconds(7))!.Error;
        Assert.Equal(TimeSpan.FromSeconds(7), observed.RetryAfter);

        var exception = new HttpRequestException("limited", null, HttpStatusCode.TooManyRequests);
        exception.Data["Retry-After"] = "4";
        Assert.Equal(TimeSpan.FromSeconds(4), QreModelErrorClassifier.Classify(exception, CancellationToken.None)!.Error.RetryAfter);
    }

    [Theory]
    [InlineData(401, RuntimeErrorCategory.ProviderAuthentication, false)]
    [InlineData(429, RuntimeErrorCategory.ProviderRateLimit, true)]
    [InlineData(503, RuntimeErrorCategory.ProviderTransport, true)]
    [InlineData(400, RuntimeErrorCategory.ProviderProtocol, false)]
    public async Task VllmClient_WithCaptureHandlerIsClassifiedFromHttpStatus(int status, RuntimeErrorCategory category, bool retryable)
    {
        foreach (var diagnosticsEnabled in new[] { false, true })
        {
            using var http = new HttpClient(new QreModelHttpErrorCaptureHandler(new StatusHandler(status)));
            using var client = CreateClient(http, new MeaiRuntimeModelClientOptions { ClassifyProviderErrors = true }, diagnosticsEnabled);

            var exception = await Assert.ThrowsAsync<RuntimeModelClientException>(() => DrainAsync(client));

            Assert.Equal(category, exception.Error.Category);
            Assert.Equal(retryable, exception.Error.Retryable);
            Assert.Equal(TimeSpan.FromSeconds(7), exception.Error.RetryAfter);
            Assert.IsType<InvalidOperationException>(exception.InnerException);
        }
    }

    [Fact]
    public async Task VllmClient_ClassificationIsOptIn()
    {
        using var http = new HttpClient(new QreModelHttpErrorCaptureHandler(new StatusHandler(503)));
        using var client = CreateClient(http, MeaiRuntimeModelClientOptions.Default, diagnosticsEnabled: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(client));

        Assert.StartsWith("Vllm error", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VllmClient_NetworkFailureIsRetryableTransport()
    {
        using var http = new HttpClient(new QreModelHttpErrorCaptureHandler(new ThrowingHandler()));
        using var client = CreateClient(http, new MeaiRuntimeModelClientOptions { ClassifyProviderErrors = true }, diagnosticsEnabled: false);

        var exception = await Assert.ThrowsAsync<RuntimeModelClientException>(() => DrainAsync(client));

        Assert.Equal("provider_transport_failed", exception.Error.Code);
        Assert.True(exception.Error.Retryable);
    }

    private static (string, bool) Pair(RuntimeError error) => (error.Code, error.Retryable);

    private static MeaiRuntimeModelClient CreateClient(
        HttpClient http,
        MeaiRuntimeModelClientOptions options,
        bool diagnosticsEnabled)
    {
        var chat = QreModelProviderSelector.CreateDefault().CreateClient(new QreModelClientDescriptor
        {
            ApiUrl = new Uri("http://offline.test/v1"),
            ApiKey = "test-key",
            Model = DiagnosticsTestSupport.ChatModel,
            HttpClient = http
        });
        var diagnostics = diagnosticsEnabled
            ? QreOutboundDiagnostics.Create(
                new QreOutboundDiagnosticsOptions { Mode = QreOutboundDiagnosticMode.Structure },
                new InMemoryDiagnosticSink())
            : null;
        return new MeaiRuntimeModelClient(chat, static _ => new ChatOptions(), options, diagnostics);
    }

    private static async Task DrainAsync(MeaiRuntimeModelClient client)
    {
        var request = new RuntimeModelRequest(
            new RuntimeSessionId("s"),
            new RuntimeTurnId("t"),
            new RuntimeStepId("step"),
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("hi")])],
            [],
            new RuntimeModelParameters(),
            0);
        await foreach (var _ in client.StreamAsync(request, new RuntimeModelAttemptContext(1), TestContext.Current.CancellationToken))
        {
        }
    }

    private sealed class StatusHandler(int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("{\"error\":{\"message\":\"provider failure\"}}", System.Text.Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("Retry-After", "7");
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("Connection refused", new SocketException(61));
    }
}
