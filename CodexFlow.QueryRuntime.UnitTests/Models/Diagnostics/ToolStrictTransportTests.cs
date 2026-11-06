using System.Net;
using System.Text.Json;
using CodexFlow.QueryRuntime.Cli.Diagnostics;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics;

/// <summary>
/// Evidence for the tool-strict cells of <see cref="QreTransportCapabilityMatrix"/>: every provider and
/// API mode is exercised against the locked VllmChatClient with an in-memory transport, so a registered
/// <c>tool_strict_field</c> transform or <c>tool_strict_dropped</c> limitation cannot drift from the SDK.
/// </summary>
public sealed class ToolStrictTransportTests
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

    public static TheoryData<string, string> AcceptedCells()
    {
        var data = new TheoryData<string, string>();
        foreach (var cell in QreTransportCapabilityMatrix.All.Where(static c => c.SelectorAccepts))
        {
            data.Add(cell.ProviderId, cell.ApiMode);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AcceptedCells))]
    public async Task Cell_ToolStrictFlag_MatchesRegisteredCapability(string providerId, string apiMode)
    {
        var cell = QreTransportCapabilityMatrix.Get(providerId, apiMode);
        var mode = Enum.GetValues<QreModelApiMode>().Single(m => QreApiModeNames.ToWireName(m) == apiMode);

        string? body = null;
        var transport = new DelegateHandler(async (request, ct) =>
        {
            body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            return DiagnosticsTestSupport.Sse(mode, QreOfflineResponse.TextResponse("hello"));
        });
        var client = QreModelProviderSelector.CreateDefault().CreateClient(new QreModelClientDescriptor
        {
            ApiUrl = new Uri("http://offline.test/v1"),
            ApiKey = "probe-key",
            Model = ModelByProvider[providerId],
            ApiMode = mode,
            HttpClient = new HttpClient(transport)
        });

        var strictTool = AIFunctionFactory.Create(
            (string city) => city,
            new AIFunctionFactoryOptions
            {
                Name = "strict_tool",
                Description = "Strict tool.",
                AdditionalProperties = new Dictionary<string, object?> { ["strictJsonSchema"] = true }
            });
        var plainTool = AIFunctionFactory.Create(
            (string city) => city,
            new AIFunctionFactoryOptions { Name = "plain_tool", Description = "Plain tool." });

        await foreach (var _ in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "hi")],
                           new ChatOptions { Tools = [strictTool, plainTool], MaxOutputTokens = 16 },
                           TestContext.Current.CancellationToken))
        {
        }

        Assert.NotNull(body);
        using var doc = JsonDocument.Parse(body!);
        var tools = doc.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        var strictEntry = tools.Single(t => ToolName(t, mode) == "strict_tool");
        var plainEntry = tools.Single(t => ToolName(t, mode) == "plain_tool");

        var holder = mode == QreModelApiMode.ChatCompletions ? strictEntry.GetProperty("function") : strictEntry;
        var plainHolder = mode == QreModelApiMode.ChatCompletions ? plainEntry.GetProperty("function") : plainEntry;

        // An unmarked tool never carries the flag, whatever the provider does with a marked one.
        Assert.False(plainHolder.TryGetProperty("strict", out _));

        var forwarded = holder.TryGetProperty("strict", out var strict) && strict.ValueKind == JsonValueKind.True;
        Assert.Equal(cell.RegisteredTransforms.Contains(QreTransportCapabilityMatrix.ToolStrictField), forwarded);
        Assert.Equal(cell.KnownLimitations.Contains(QreTransportCapabilityMatrix.ToolStrictDropped), !forwarded);
        client.Dispose();
    }

    private static string? ToolName(JsonElement tool, QreModelApiMode mode)
        => mode == QreModelApiMode.ChatCompletions
            ? tool.GetProperty("function").GetProperty("name").GetString()
            : tool.GetProperty("name").GetString();
}
