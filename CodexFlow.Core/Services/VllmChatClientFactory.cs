using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.VllmChatClient.GptOss;
using Microsoft.Extensions.AI.VllmChatClient.Gemma;
using Microsoft.Extensions.Configuration;

namespace CodexFlow.Core.Services;

public static class VllmChatClientFactory
{
    public static IChatClient Create(IConfigurationSection section, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(section);

        var apiUrl = section["ApiUrl"];
        var apiKey = section["ApiKey"];
        var model = section["Model"];

        if (string.IsNullOrWhiteSpace(apiUrl) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("Vllm chat client configuration requires ApiUrl, ApiKey, and Model.");
        }

        return Create(apiUrl, apiKey, model, section["ApiMode"], httpClient);
    }

    public static IChatClient Create(
        Uri apiUrl,
        string apiKey,
        string model,
        string? apiMode = null,
        HttpClient? httpClient = null)
        => Create(apiUrl, apiKey, model, ResolveApiMode(apiMode), httpClient);

    public static IChatClient Create(
        Uri apiUrl,
        string apiKey,
        string model,
        VllmApiMode apiMode,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var normalizedModel = model.Trim().ToLowerInvariant();
        var apiUrlText = apiUrl.ToString();

        if (IsGptOssModel(normalizedModel))
        {
            return new VllmGptOssChatClient(apiUrlText, apiKey, model, httpClient, apiMode);
        }

        if (IsOpenAiGptModel(normalizedModel))
        {
            return new VllmOpenAiGptClient(apiUrlText, apiKey, model, httpClient, apiMode);
        }

        if (IsGeminiModel(normalizedModel))
        {
            if (apiMode != VllmApiMode.ChatCompletions)
            {
                throw new NotSupportedException("Gemini client does not support non-ChatCompletions VllmApiMode.");
            }

            return new VllmGemini3ChatClient(apiUrlText, apiKey, model, httpClient);
        }

        if (IsClaudeModel(normalizedModel))
        {
            return new VllmClaudeChatClient(apiUrlText, apiKey, model, httpClient, apiMode);
        }

        if (IsKimiModel(normalizedModel))
        {
            return new VllmKimiK2ChatClient(apiUrlText, apiKey, model, httpClient, apiMode);
        }

        if (IsMiniMaxModel(normalizedModel))
        {
            return new VllmMiniMaxChatClient(apiUrlText, apiKey, model, httpClient, apiMode);
        }

        if (IsGlmModel(normalizedModel))
        {
            return new VllmGlmChatClient(apiUrlText, apiKey, model, httpClient, apiMode);
        }

        if (IsQwenModel(normalizedModel))
        {
            return new VllmQwen3NextChatClient(apiUrlText, apiKey, model, httpClient, apiMode);
        }

        if( IsDeepseekModel(normalizedModel))
        {
            return new VllmDeepseekV3ChatClient(apiUrlText, apiKey, model, httpClient, apiMode);
        }

        return new VllmQwen3NextChatClient(apiUrlText, apiKey, model, httpClient, apiMode);
    }

    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "Kept for existing configuration and call-site compatibility. Prefer the Uri overload for new code.")]
    public static IChatClient Create(
        string apiUrl,
        string apiKey,
        string model,
        string? apiMode = null,
        HttpClient? httpClient = null)
        => Create(ToAbsoluteUri(apiUrl), apiKey, model, ResolveApiMode(apiMode), httpClient);

    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "Kept for existing configuration and call-site compatibility. Prefer the Uri overload for new code.")]
    public static IChatClient Create(
        string apiUrl,
        string apiKey,
        string model,
        VllmApiMode apiMode,
        HttpClient? httpClient = null)
        => Create(ToAbsoluteUri(apiUrl), apiKey, model, apiMode, httpClient);

    public static VllmApiMode ResolveApiMode(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return VllmApiMode.ChatCompletions;
        }

        var normalized = configuredValue.Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized switch
        {
            "chat" or "chatcompletion" or "chatcompletions" or "completions"
                => VllmApiMode.ChatCompletions,
            "response" or "responses"
                => VllmApiMode.Responses,
            "anthropic" or "anthropicmessage" or "anthropicmessages" or "message" or "messages"
                => VllmApiMode.AnthropicMessages,
            _ => throw new InvalidOperationException($"Unsupported VllmAgent:ApiMode value '{configuredValue}'.")
        };
    }

    private static bool IsQwenModel(string model)
        => model.StartsWith("qwen", StringComparison.Ordinal) ||
           model.Contains("/qwen", StringComparison.Ordinal);

    private static bool IsGptOssModel(string model)
        => model.StartsWith("openai/gpt-oss", StringComparison.Ordinal) ||
           model.StartsWith("gpt-oss", StringComparison.Ordinal);

    private static bool IsOpenAiGptModel(string model)
        => model.StartsWith("openai/gpt-", StringComparison.Ordinal);

    private static bool IsGeminiModel(string model)
        => model.Contains("gemini", StringComparison.Ordinal);

    private static bool IsClaudeModel(string model)
        => model.Contains("claude", StringComparison.Ordinal);

    private static bool IsKimiModel(string model)
        => model.StartsWith("kimi-", StringComparison.Ordinal) ||
           model.Contains("/kimi-", StringComparison.Ordinal);

    private static bool IsMiniMaxModel(string model)
        => model.Contains("minimax", StringComparison.Ordinal);

    private static bool IsGlmModel(string model)
        => model.StartsWith("glm-", StringComparison.Ordinal) ||
           model.Contains("/glm-", StringComparison.Ordinal);

    private static bool IsDeepseekModel(string model)
        => model.StartsWith("deepseek", StringComparison.Ordinal) ||
           model.StartsWith("/deepseek", StringComparison.Ordinal);

    private static Uri ToAbsoluteUri(string apiUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiUrl);

        return new Uri(apiUrl, UriKind.Absolute);
    }
}
