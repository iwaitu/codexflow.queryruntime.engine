using CodexFlow.QueryRuntime.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Models;

public sealed class QreModelProviderSelectorTests
{
    private const string ApiUrl = "https://example.test/v1";
    private const string ApiKey = "test-key";

    [Theory]
    [InlineData("gpt-oss-20b", "openai-gpt-oss")]
    [InlineData("openai/gpt-oss-120b", "openai-gpt-oss")]
    [InlineData("openai/gpt-4o", "openai-gpt")]
    [InlineData("gemini-2.5-pro", "gemini")]
    [InlineData("claude-sonnet-4-6", "claude")]
    [InlineData("kimi-k2", "kimi")]
    [InlineData("provider/kimi-k2", "kimi")]
    [InlineData("minimax-m1", "minimax")]
    [InlineData("glm-4.6", "glm")]
    [InlineData("provider/glm-4.6", "glm")]
    [InlineData("qwen3-next-80b", "qwen")]
    [InlineData("provider/qwen3", "qwen")]
    [InlineData("deepseek-v3", "deepseek")]
    public void Select_ResolvesExpectedProvider(string model, string expectedProviderId)
    {
        var selector = QreModelProviderSelector.CreateDefault();

        var provider = selector.Select(model);

        Assert.Equal(expectedProviderId, provider.Id);
    }

    [Theory]
    [InlineData("GPT-OSS-20B")]
    [InlineData("Qwen3-Next")]
    public void Select_IsCaseInsensitive(string model)
    {
        var selector = QreModelProviderSelector.CreateDefault();

        // Should not throw; the model resolves regardless of casing.
        _ = selector.Select(model);
    }

    [Fact]
    public void Select_PrefersGptOssOverOpenAiGpt()
    {
        var selector = QreModelProviderSelector.CreateDefault();

        // "openai/gpt-oss-120b" matches both the gpt-oss and the openai/gpt- prefixes;
        // the more specific gpt-oss adapter must win because of registration order.
        Assert.Equal("openai-gpt-oss", selector.Select("openai/gpt-oss-120b").Id);
    }

    [Fact]
    public void Select_UnknownModel_ThrowsWithClearMessageListingProviders()
    {
        var selector = QreModelProviderSelector.CreateDefault();

        var ex = Assert.Throws<QreUnknownModelException>(() => selector.Select("totally-unknown-model"));

        Assert.Equal("totally-unknown-model", ex.Model);
        Assert.Contains("No model adapter handles model 'totally-unknown-model'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("qwen", ex.Message, StringComparison.Ordinal);
        Assert.NotEmpty(ex.KnownProviders);
    }

    [Fact]
    public void CreateClient_BuildsClientForSupportedModelAndMode()
    {
        var selector = QreModelProviderSelector.CreateDefault();

        using var client = selector.CreateClient(ApiUrl, ApiKey, "qwen3-next", "chat-completions");

        Assert.IsAssignableFrom<IChatClient>(client);
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("anthropic-messages")]
    public void CreateClient_GeminiRejectsNonChatCompletionsMode(string apiMode)
    {
        var selector = QreModelProviderSelector.CreateDefault();

        var ex = Assert.Throws<QreUnsupportedApiModeException>(
            () => selector.CreateClient(ApiUrl, ApiKey, "gemini-2.5-pro", apiMode));

        Assert.Equal("gemini", ex.ProviderId);
        Assert.Equal("gemini-2.5-pro", ex.Model);
        Assert.Contains(QreModelApiMode.ChatCompletions, ex.SupportedModes);
        Assert.Contains("does not support api-mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateClient_UnknownModel_Throws()
    {
        var selector = QreModelProviderSelector.CreateDefault();

        Assert.Throws<QreUnknownModelException>(
            () => selector.CreateClient(ApiUrl, ApiKey, "no-such-model", "chat-completions"));
    }

    [Fact]
    public void DefaultProviders_HaveUniqueIdsAndAtLeastOneSupportedMode()
    {
        var ids = QreModelProviderSelector.DefaultProviders.Select(p => p.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(QreModelProviderSelector.DefaultProviders, p => Assert.NotEmpty(p.SupportedApiModes));
    }

    [Fact]
    public void Constructor_RejectsEmptyProviderSet()
    {
        Assert.Throws<ArgumentException>(() => new QreModelProviderSelector([]));
    }
}
