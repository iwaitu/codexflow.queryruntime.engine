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

/// <summary>DeepSeek models.</summary>
public sealed class DeepseekModelProvider : VllmModelProvider
{
    public override string Id => "deepseek";

    public override bool CanHandle(string normalizedModel)
        => normalizedModel.StartsWith("deepseek", StringComparison.Ordinal) ||
           normalizedModel.StartsWith("/deepseek", StringComparison.Ordinal);

    protected override IChatClient CreateClientCore(QreModelClientDescriptor descriptor, string apiUrlText)
        => new VllmDeepseekV3ChatClient(apiUrlText, descriptor.ApiKey, descriptor.Model, descriptor.HttpClient, descriptor.ApiMode.ToVllmApiMode());
}
