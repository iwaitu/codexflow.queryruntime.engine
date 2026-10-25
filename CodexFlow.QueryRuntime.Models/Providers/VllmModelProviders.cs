using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.VllmChatClient.Gemma;
using Microsoft.Extensions.AI.VllmChatClient.GptOss;

namespace CodexFlow.QueryRuntime.Models.Providers;

/// <summary>OpenAI <c>gpt-oss</c> open-weight models.</summary>
public sealed class GptOssModelProvider : VllmModelProvider
{
    public override string Id => "openai-gpt-oss";

    public override bool CanHandle(string normalizedModel)
        => normalizedModel.StartsWith("openai/gpt-oss", StringComparison.Ordinal) ||
           normalizedModel.StartsWith("gpt-oss", StringComparison.Ordinal);

    protected override IChatClient CreateClientCore(QreModelClientDescriptor descriptor, string apiUrlText)
        => new VllmGptOssChatClient(apiUrlText, descriptor.ApiKey, descriptor.Model, descriptor.HttpClient, descriptor.ApiMode.ToVllmApiMode());
}

/// <summary>OpenAI GPT chat models served behind an OpenAI-compatible endpoint.</summary>
public sealed class OpenAiGptModelProvider : VllmModelProvider
{
    public override string Id => "openai-gpt";

    public override bool CanHandle(string normalizedModel)
        => normalizedModel.StartsWith("openai/gpt-", StringComparison.Ordinal);

    protected override IChatClient CreateClientCore(QreModelClientDescriptor descriptor, string apiUrlText)
        => new VllmOpenAiGptClient(apiUrlText, descriptor.ApiKey, descriptor.Model, descriptor.HttpClient, descriptor.ApiMode.ToVllmApiMode());
}

/// <summary>Google Gemini models. Only the OpenAI-compatible ChatCompletions shape is supported.</summary>
public sealed class GeminiModelProvider : VllmModelProvider
{
    private static readonly IReadOnlyCollection<QreModelApiMode> ChatCompletionsOnly =
        [QreModelApiMode.ChatCompletions];

    public override string Id => "gemini";

    public override IReadOnlyCollection<QreModelApiMode> SupportedApiModes => ChatCompletionsOnly;

    public override bool CanHandle(string normalizedModel)
        => normalizedModel.Contains("gemini", StringComparison.Ordinal);

    protected override IChatClient CreateClientCore(QreModelClientDescriptor descriptor, string apiUrlText)
        => new VllmGemini3ChatClient(apiUrlText, descriptor.ApiKey, descriptor.Model, descriptor.HttpClient);
}

/// <summary>Anthropic Claude models.</summary>
public sealed class ClaudeModelProvider : VllmModelProvider
{
    public override string Id => "claude";

    public override bool CanHandle(string normalizedModel)
        => normalizedModel.Contains("claude", StringComparison.Ordinal);

    protected override IChatClient CreateClientCore(QreModelClientDescriptor descriptor, string apiUrlText)
        => new VllmClaudeChatClient(apiUrlText, descriptor.ApiKey, descriptor.Model, descriptor.HttpClient, descriptor.ApiMode.ToVllmApiMode());
}

/// <summary>Moonshot Kimi models.</summary>
public sealed class KimiModelProvider : VllmModelProvider
{
    public override string Id => "kimi";

    public override bool CanHandle(string normalizedModel)
        => normalizedModel.StartsWith("kimi-", StringComparison.Ordinal) ||
           normalizedModel.Contains("/kimi-", StringComparison.Ordinal);

    protected override IChatClient CreateClientCore(QreModelClientDescriptor descriptor, string apiUrlText)
        => new VllmKimiK2ChatClient(apiUrlText, descriptor.ApiKey, descriptor.Model, descriptor.HttpClient, descriptor.ApiMode.ToVllmApiMode());
}

/// <summary>MiniMax models.</summary>
public sealed class MiniMaxModelProvider : VllmModelProvider
{
    public override string Id => "minimax";

    public override bool CanHandle(string normalizedModel)
        => normalizedModel.Contains("minimax", StringComparison.Ordinal);

    protected override IChatClient CreateClientCore(QreModelClientDescriptor descriptor, string apiUrlText)
        => new VllmMiniMaxChatClient(apiUrlText, descriptor.ApiKey, descriptor.Model, descriptor.HttpClient, descriptor.ApiMode.ToVllmApiMode());
}

/// <summary>Zhipu GLM models.</summary>
public sealed class GlmModelProvider : VllmModelProvider
{
    public override string Id => "glm";

    public override bool CanHandle(string normalizedModel)
        => normalizedModel.StartsWith("glm-", StringComparison.Ordinal) ||
           normalizedModel.Contains("/glm-", StringComparison.Ordinal);

    protected override IChatClient CreateClientCore(QreModelClientDescriptor descriptor, string apiUrlText)
        => new VllmGlmChatClient(apiUrlText, descriptor.ApiKey, descriptor.Model, descriptor.HttpClient, descriptor.ApiMode.ToVllmApiMode());
}

/// <summary>Alibaba Qwen models.</summary>
public sealed class QwenModelProvider : VllmModelProvider
{
    public override string Id => "qwen";

    public override bool CanHandle(string normalizedModel)
        => normalizedModel.StartsWith("qwen", StringComparison.Ordinal) ||
           normalizedModel.Contains("/qwen", StringComparison.Ordinal);

    protected override IChatClient CreateClientCore(QreModelClientDescriptor descriptor, string apiUrlText)
        => new VllmQwen3NextChatClient(apiUrlText, descriptor.ApiKey, descriptor.Model, descriptor.HttpClient, descriptor.ApiMode.ToVllmApiMode());
}

/// <summary>
/// DeepSeek models. Only an explicit V4-family model name (<c>deepseek-v4</c> followed by the end of the name, <c>-</c>, <c>.</c> or <c>/</c>) is served by
/// <see cref="VllmDeepseekV4ChatClient"/>; every other DeepSeek name (V3, R1, <c>deepseek-v40</c>, <c>deepseek-v4x</c>, aliases, unknown versions)
/// keeps the V3 client, so the family is never switched silently.
/// </summary>
public sealed class DeepseekModelProvider : VllmModelProvider
{
    private const string V4Prefix = "deepseek-v4";

    public override string Id => "deepseek";

    public override bool CanHandle(string normalizedModel)
        => normalizedModel.StartsWith("deepseek", StringComparison.Ordinal) ||
           normalizedModel.StartsWith("/deepseek", StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="model"/> names the DeepSeek V4 family. Normalised like the selector (trim, invariant lower case) and, like
    /// <see cref="CanHandle"/>, tolerant of one leading slash.
    /// </summary>
    public static bool IsV4Family(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var normalized = model.Trim().ToLowerInvariant();
        if (normalized.StartsWith('/'))
        {
            normalized = normalized[1..];
        }

        return normalized.StartsWith(V4Prefix, StringComparison.Ordinal) &&
               (normalized.Length == V4Prefix.Length || normalized[V4Prefix.Length] is '-' or '.' or '/');
    }

    protected override IChatClient CreateClientCore(QreModelClientDescriptor descriptor, string apiUrlText)
        => IsV4Family(descriptor.Model)
            ? new VllmDeepseekV4ChatClient(apiUrlText, descriptor.ApiKey, descriptor.Model, descriptor.HttpClient, descriptor.ApiMode.ToVllmApiMode())
            : new VllmDeepseekV3ChatClient(apiUrlText, descriptor.ApiKey, descriptor.Model, descriptor.HttpClient, descriptor.ApiMode.ToVllmApiMode());
}
