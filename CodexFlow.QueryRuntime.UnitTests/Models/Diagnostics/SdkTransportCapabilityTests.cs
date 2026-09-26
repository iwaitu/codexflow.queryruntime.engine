using System.Net;
using System.Text;
using CodexFlow.QueryRuntime.Cli.Diagnostics;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics;

/// <summary>
/// P0 evidence for <see cref="QreTransportCapabilityMatrix"/>: every cell is
/// exercised against the locked VllmChatClient with an in-memory terminal
/// transport. If an SDK upgrade changes a fact, these tests fail instead of the
/// matrix silently overstating coverage.
/// </summary>
public sealed class SdkTransportCapabilityTests
{
    private static readonly Dictionary<string, string> ModelByProvider = new(StringComparer.Ordinal)
    {
        ["openai-gpt-oss"] = "gpt-oss-20b",
        ["openai-gpt"] = "openai/gpt-4o",
        ["gemini"] = "gemini-3-pro",
        ["claude"] = "claude-sonnet-4",
        ["kimi"] = "kimi-k2",
        ["minimax"] = "minimax-m2",
        ["glm"] = "glm-4.6",
        ["qwen"] = "qwen3-next-80b",
        ["deepseek"] = "deepseek-v3"
    };

    public static TheoryData<string, string> Cells()
    {
        var data = new TheoryData<string, string>();
        foreach (var cell in QreTransportCapabilityMatrix.All)
        {
            data.Add(cell.ProviderId, cell.ApiMode);
        }
        return data;
    }

    [Fact]
    public void Matrix_CoversEveryDefaultProviderAndApiMode()
    {
        Assert.True(QreTransportCapabilityMatrix.IsVerifiedSdk, $"Running SDK {QreTransportCapabilityMatrix.SdkVersion}");
        Assert.Equal(QreModelProviderSelector.DefaultProviders.Count * 3, QreTransportCapabilityMatrix.All.Count);
        foreach (var provider in QreModelProviderSelector.DefaultProviders)
        {
            foreach (var mode in Enum.GetValues<QreModelApiMode>())
            {
                var cell = QreTransportCapabilityMatrix.Get(provider.Id, QreApiModeNames.ToWireName(mode));
                Assert.Equal(provider.SupportedApiModes.Contains(mode), cell.SelectorAccepts);
                Assert.Equal(
                    cell.SelectorAccepts ? QreCapabilityStatus.Verified : QreCapabilityStatus.Unsupported,
                    cell.Status);
            }
        }
        Assert.Equal(QreCapabilityStatus.Unsupported, QreTransportCapabilityMatrix.SharedHostHttpClientScenario);
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task Cell_MatchesLockedSdkBehavior(string providerId, string apiMode)
    {
        var cell = QreTransportCapabilityMatrix.Get(providerId, apiMode);
        var mode = ToMode(apiMode);
        var captured = new List<(string Path, HttpContent? Content, long? Length, string Body)>();
        var transport = new DelegateHandler(async (request, ct) =>
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            captured.Add((request.RequestUri!.AbsolutePath, request.Content, null, body));
            return DiagnosticsTestSupport.Sse(mode, QreOfflineResponse.TextResponse("hello"));
        });
        var http = new HttpClient(transport);
        var descriptor = new QreModelClientDescriptor
        {
            ApiUrl = new Uri("http://offline.test/v1"),
            ApiKey = "probe-key",
            Model = ModelByProvider[providerId],
            ApiMode = mode,
            HttpClient = http
        };

        if (!cell.SelectorAccepts)
        {
            Assert.Throws<QreUnsupportedApiModeException>(() => QreModelProviderSelector.CreateDefault().CreateClient(descriptor));
            return;
        }

        var client = QreModelProviderSelector.CreateDefault().CreateClient(descriptor);
        var finishReasons = new List<ChatFinishReason>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "hi")],
                           new ChatOptions { MaxOutputTokens = 12, Temperature = 0.25f, ResponseFormat = ChatResponseFormat.Json },
                           TestContext.Current.CancellationToken))
        {
            if (update.FinishReason is { } reason)
            {
                finishReasons.Add(reason);
            }
        }

        var send = Assert.Single(captured);
        Assert.Equal(cell.VisibleSendsPerStreamingCall, captured.Count);
        Assert.Equal(cell.RouteTemplate!.Replace("{endpoint}", "/v1", StringComparison.Ordinal), send.Path);
        Assert.Equal("JsonContent", send.Content!.GetType().Name);
        Assert.Null(Assert.Single(transport.ContentLengthsBeforeSend));
        Assert.Equal("tool_calls_only", cell.StreamingFinishReason);
        Assert.Empty(finishReasons);
        Assert.True(cell.MutatesInjectedDefaultHeaders);
        Assert.True(http.DefaultRequestHeaders.Authorization != null || http.DefaultRequestHeaders.Contains("x-api-key"));

        // Registered transforms and known limitations are checked against the
        // projection of the body the SDK actually produced.
        var diagnostics = QreOutboundDiagnostics.Create(
            new QreOutboundDiagnosticsOptions { Mode = QreOutboundDiagnosticMode.Structure },
            new InMemoryDiagnosticSink());
        var (semantic, status, _) = QreSemanticProjector.FromHttpJson(
            Encoding.UTF8.GetBytes(send.Body), apiMode, 32, diagnostics);
        Assert.Equal(QreDiagnosticCaptureStatus.Complete, status);
        Assert.NotNull(semantic);
        Assert.Equal(
            cell.KnownLimitations.Contains(QreTransportCapabilityMatrix.MaxTokensDropped) ? QreDiagnosticFieldStates.Absent : QreDiagnosticFieldStates.Present,
            semantic!.MaxOutputTokens.State);
        if (cell.RegisteredTransforms.Contains(QreTransportCapabilityMatrix.TemperatureNestedOptions))
        {
            Assert.Equal("options.temperature", semantic.Temperature.SourcePath);
        }
        var jsonDropped = cell.KnownLimitations.Contains(QreTransportCapabilityMatrix.ResponseFormatDropped) ||
                          cell.KnownLimitations.Contains(QreTransportCapabilityMatrix.JsonFormatUnsupportedByProtocol);
        if (jsonDropped)
        {
            Assert.NotEqual("json_object", semantic.ResponseFormat.Value);
        }
        else
        {
            Assert.Equal("json_object", semantic.ResponseFormat.Value);
        }
        Assert.Equal(
            cell.RegisteredTransforms.Contains(QreTransportCapabilityMatrix.SdkInjectedSystemPrompt),
            semantic.Messages.Items.Count > 1 && semantic.Messages.Items[0].Role == "system");

        // Disposing the chat client disposes the injected HttpClient.
        client.Dispose();
        Assert.True(cell.DisposesInjectedHttpClient);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            http.GetAsync("http://offline.test/after-dispose", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(QreModelApiMode.ChatCompletions)]
    [InlineData(QreModelApiMode.Responses)]
    [InlineData(QreModelApiMode.AnthropicMessages)]
    public async Task StreamingPath_DoesNotRetryHttpFailureAndPutsErrorBodyIntoExceptionMessage(QreModelApiMode mode)
    {
        const string canary = "CANARY-ERROR-BODY-7f3a";
        var sends = 0;
        var transport = new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref sends);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"{{\"error\":\"{canary}\"}}", Encoding.UTF8, "application/json")
            });
        });
        var client = QreModelProviderSelector.CreateDefault().CreateClient(new QreModelClientDescriptor
        {
            ApiUrl = new Uri("http://offline.test/v1"),
            ApiKey = "probe-key",
            Model = DiagnosticsTestSupport.ChatModel,
            ApiMode = mode,
            HttpClient = new HttpClient(transport)
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                               [new ChatMessage(ChatRole.User, "hi")],
                               cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Equal(1, sends);
        // Evidence for the out-of-scope risk: the SDK copies the provider error
        // body into Exception.Message, so diagnostics must never persist Message.
        Assert.Contains(canary, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(QreModelApiMode.ChatCompletions)]
    [InlineData(QreModelApiMode.Responses)]
    [InlineData(QreModelApiMode.AnthropicMessages)]
    public async Task StreamingPath_SurfacesToolCallFinishReasonOnly(QreModelApiMode mode)
    {
        var transport = new DelegateHandler((_, _) => Task.FromResult(DiagnosticsTestSupport.Sse(
            mode,
            QreOfflineResponse.ToolCall("qre_read_file", "call-fixed-1", "{\"path\":\"fixture.txt\"}"))));
        var client = QreModelProviderSelector.CreateDefault().CreateClient(new QreModelClientDescriptor
        {
            ApiUrl = new Uri("http://offline.test/v1"),
            ApiKey = "probe-key",
            Model = DiagnosticsTestSupport.ChatModel,
            ApiMode = mode,
            HttpClient = new HttpClient(transport)
        });

        var finish = new List<ChatFinishReason>();
        var calls = new List<FunctionCallContent>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "hi")],
                           cancellationToken: TestContext.Current.CancellationToken))
        {
            if (update.FinishReason is { } reason)
            {
                finish.Add(reason);
            }
            calls.AddRange(update.Contents.OfType<FunctionCallContent>());
        }

        var call = Assert.Single(calls);
        Assert.Equal("qre_read_file", call.Name);
        Assert.Equal("call-fixed-1", call.CallId);
        Assert.Equal(ChatFinishReason.ToolCalls, Assert.Single(finish));
    }

    [Fact]
    public async Task ChatCompletions_PreservesRequiredToolNameCaseExactly()
    {
        string? body = null;
        var transport = new DelegateHandler(async (request, ct) =>
        {
            body = await request.Content!.ReadAsStringAsync(ct);
            return DiagnosticsTestSupport.Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("ok"));
        });
        var client = QreModelProviderSelector.CreateDefault().CreateClient(new QreModelClientDescriptor
        {
            ApiUrl = new Uri("http://offline.test/v1"),
            ApiKey = "probe-key",
            Model = DiagnosticsTestSupport.ChatModel,
            HttpClient = new HttpClient(transport)
        });

        await foreach (var _ in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "hi")],
                           new ChatOptions
                           {
                               Tools = [AIFunctionFactory.Create((string path) => path, "qre_read_file")],
                               ToolMode = ChatToolMode.RequireSpecific("QRE_READ_FILE")
                           },
                           TestContext.Current.CancellationToken))
        {
        }

        Assert.Contains("\"name\":\"qre_read_file\"", body, StringComparison.Ordinal);
        Assert.Contains("\"function\":{\"name\":\"QRE_READ_FILE\"}", body, StringComparison.Ordinal);
    }

    private static QreModelApiMode ToMode(string apiMode) => apiMode switch
    {
        QreApiModeNames.Responses => QreModelApiMode.Responses,
        QreApiModeNames.AnthropicMessages => QreModelApiMode.AnthropicMessages,
        _ => QreModelApiMode.ChatCompletions
    };
}
