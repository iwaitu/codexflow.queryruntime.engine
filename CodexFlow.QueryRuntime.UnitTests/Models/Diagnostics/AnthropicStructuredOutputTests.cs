using System.Net;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Cli.Diagnostics;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics;

public sealed class AnthropicStructuredOutputTests
{
    [Theory]
    [InlineData(RuntimeResponseFormatScope.AllSteps)]
    [InlineData(RuntimeResponseFormatScope.ToolFreeStepsOnly)]
    public async Task RuntimeSchemalessJson_IsNonRetryableRequestRejection_WithNoHttp(RuntimeResponseFormatScope scope)
    {
        var sends = 0;
        using var handler = new DelegateHandler((_, _) =>
        {
            sends++;
            return Task.FromResult(DiagnosticsTestSupport.Sse(QreModelApiMode.AnthropicMessages, QreOfflineResponse.TextResponse("{}")));
        });
        using var http = new HttpClient(handler);
        using var chat = QreModelProviderSelector.CreateDefault().CreateClient(new QreModelClientDescriptor
        {
            ApiUrl = new Uri("http://offline.test/v1"), ApiKey = "fake-key", Model = "claude-haiku-5-5",
            ApiMode = QreModelApiMode.AnthropicMessages, HttpClient = http
        });
        using var runtime = new MeaiRuntimeModelClient(chat, _ => new ChatOptions(),
            new MeaiRuntimeModelClientOptions { ClassifyProviderErrors = true });
        var request = DiagnosticsTestSupport.Request(parameters: new RuntimeModelParameters(Model: "claude-haiku-5-5")
        {
            ResponseFormat = RuntimeResponseFormat.JsonObject(scope)
        });
        var error = await Assert.ThrowsAsync<RuntimeModelClientException>(async () =>
        {
            await foreach (var _ in runtime.StreamAsync(request, new RuntimeModelAttemptContext(1), TestContext.Current.CancellationToken)) { }
        });
        Assert.Equal(RuntimeErrorCategory.ProviderProtocol, error.Error.Category);
        Assert.Equal(QreModelErrorClassifier.RequestRejectedCode, error.Error.Code);
        Assert.False(error.Error.Retryable);
        Assert.Equal(0, sends);
    }

    private const string Schema = """
        {"type":"object","properties":{"name":{"type":"string"},"email":{"type":"string"},"plan_interest":{"type":"string"},"demo_requested":{"type":"boolean"}},"required":["name","email","plan_interest","demo_requested"],"additionalProperties":false}
        """;

    [Theory]
    [InlineData(false, "schema")]
    [InlineData(true, "schema")]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "text")]
    [InlineData(true, "text")]
    [InlineData(false, "json")]
    [InlineData(true, "json")]
    public async Task NativeFactory_TransmitsSchemaAndDiagnosesIt_OrRejectsSchemalessBeforeSend(bool stream, string format)
    {
        using var schema = JsonDocument.Parse(Schema);
        var bodies = new List<byte[]>();
        using var handler = new DelegateHandler(async (request, ct) =>
        {
            bodies.Add(await request.Content!.ReadAsByteArrayAsync(ct));
            return stream
                ? DiagnosticsTestSupport.Sse(QreModelApiMode.AnthropicMessages, QreOfflineResponse.TextResponse("{}"))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"msg","type":"message","role":"assistant","content":[{"type":"text","text":"{}"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""", Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler);
        using var client = QreModelProviderSelector.CreateDefault().CreateClient(new QreModelClientDescriptor
        {
            ApiUrl = new Uri("http://offline.test/v1"), ApiKey = "fake-key", Model = "claude-haiku-5-5",
            ApiMode = QreModelApiMode.AnthropicMessages, HttpClient = http
        });
        var options = new ChatOptions
        {
            MaxOutputTokens = 1024,
            ResponseFormat = format switch { "schema" => ChatResponseFormat.ForJsonSchema(schema.RootElement, "contact"), "text" => ChatResponseFormat.Text, "json" => ChatResponseFormat.Json, _ => null }
        };
        async Task Send()
        {
            if (stream)
                await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Extract contact details.")], options, TestContext.Current.CancellationToken)) { }
            else
                await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Extract contact details.")], options, TestContext.Current.CancellationToken);
        }
        if (format == "json")
        {
            var error = await Assert.ThrowsAsync<NotSupportedException>(Send);
            Assert.Contains("Anthropic", error.Message);
            Assert.Contains("JSON schema", error.Message);
            Assert.Empty(bodies);
            return;
        }
        await Send();
        var bytes = Assert.Single(bodies);
        using var body = JsonDocument.Parse(bytes);
        Assert.Equal(format == "schema", body.RootElement.TryGetProperty("output_config", out var config));
        if (format == "schema")
            Assert.True(JsonElement.DeepEquals(schema.RootElement, config.GetProperty("format").GetProperty("schema")));
        var diagnostics = QreOutboundDiagnostics.Create(new QreOutboundDiagnosticsOptions { Mode = QreOutboundDiagnosticMode.Structure }, new InMemoryDiagnosticSink());
        var (semantic, status, _) = QreSemanticProjector.FromHttpJson(bytes, QreApiModeNames.AnthropicMessages, 32, diagnostics);
        Assert.Equal(QreDiagnosticCaptureStatus.Complete, status);
        Assert.Equal(format == "schema" ? "json_schema" : "none", semantic!.ResponseFormat.Value);
        Assert.Equal("output_config.format", semantic.ResponseFormat.SourcePath);
        Assert.Equal(0, semantic.UncheckedFieldCount);
        Assert.Equal("2.0.31", QreTransportCapabilityMatrix.SdkVersion);
        Assert.True(QreTransportCapabilityMatrix.IsVerifiedSdk);
    }
}
