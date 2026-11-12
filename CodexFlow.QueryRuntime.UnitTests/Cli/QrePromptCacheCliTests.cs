using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Cli;

public sealed class QrePromptCacheCliTests
{
    [Theory]
    [InlineData(QrePromptCacheMode.Auto, QreModelApiMode.AnthropicMessages, "claude", true)]
    [InlineData(QrePromptCacheMode.Auto, QreModelApiMode.AnthropicMessages, "deepseek", false)]
    [InlineData(QrePromptCacheMode.On, QreModelApiMode.AnthropicMessages, "deepseek", true)]
    [InlineData(QrePromptCacheMode.Off, QreModelApiMode.AnthropicMessages, "claude", false)]
    // The other API shapes cache on their own and reject the Anthropic field.
    [InlineData(QrePromptCacheMode.On, QreModelApiMode.ChatCompletions, "claude", false)]
    [InlineData(QrePromptCacheMode.Auto, QreModelApiMode.Responses, "claude", false)]
    internal void AnthropicPromptCache_IsRequestedOnlyForAnthropicMessages(
        QrePromptCacheMode mode,
        QreModelApiMode apiMode,
        string providerId,
        bool expected)
        => Assert.Equal(expected, QreCli.UsesAnthropicPromptCache(mode, apiMode, providerId));

    [Fact]
    public void AnthropicCacheOptions_CarryTheSdkCacheControlAndKeepTheBaseOptions()
    {
        var request = new RuntimeModelRequest(
            new RuntimeSessionId("session-1"),
            new RuntimeTurnId("turn-1"),
            new RuntimeStepId("step-1"),
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("hello")])],
            [],
            new RuntimeModelParameters(MaxOutputTokens: 256),
            HistoryVersion: 0);

        var plain = QreCli.CreateCliChatOptions(request);
        var cached = QreCli.CreateCliChatOptionsWithAnthropicCache(request);

        Assert.Null(plain.AdditionalProperties);
        Assert.Equal(256, cached.MaxOutputTokens);
        Assert.IsType<VllmAnthropicCacheControl>(cached.AdditionalProperties![VllmAnthropicCacheControl.OptionKey]);
    }

    [Fact]
    public void CachedInputTokens_AreReadFromTheUnifiedKeyOrAProviderSpelling()
    {
        Assert.Null(new RuntimeUsage(InputTokens: 10).CachedInputTokens);
        Assert.Equal(7, new RuntimeUsage(Additional: new Dictionary<string, long>
        {
            [RuntimeUsageKeys.CachedInputTokens] = 7,
            ["prompt_tokens_details.cached_tokens"] = 99
        }).CachedInputTokens);
        Assert.Equal(5, new RuntimeUsage(Additional: new Dictionary<string, long>
        {
            ["cache_read_input_tokens"] = 5
        }).CachedInputTokens);
        Assert.Equal(0, RuntimeUsageTotals.Empty.CachedInputTokens);
        Assert.Equal(12, (RuntimeUsageTotals.Empty with
        {
            Additional = new Dictionary<string, long> { [RuntimeUsageKeys.CachedInputTokens] = 12 }
        }).CachedInputTokens);
    }
}
