using System.Diagnostics.CodeAnalysis;
using CodexFlow.QueryRuntime.Experimental;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.VllmChatClient.Gemma;
using Microsoft.Extensions.AI.VllmChatClient.GptOss;

namespace CodexFlow.QueryRuntime.IntegrationTests.Infrastructure;

internal sealed class RealQueryRuntimeTestHost : IDisposable
{
    private readonly IChatClient _chatClient;

    private RealQueryRuntimeTestHost(RealQreModelSettings settings, IChatClient chatClient)
    {
        Settings = settings;
        _chatClient = chatClient;
    }

    public RealQreModelSettings Settings { get; }

    public Task<ChatResponse> GetResponseAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken ct)
        => _chatClient.GetResponseAsync(messages, options, ct);

    public IExperimentalModelClient CreateExperimentalModelClient()
        => new ChatClientExperimentalModelClient(_chatClient);

    public static bool TryCreate(out RealQueryRuntimeTestHost? host, out string reason)
    {
        host = null;

        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "Set RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true to enable live QueryRuntime integration tests.";
            return false;
        }

        var settings = RealQreModelSettings.FromEnvironment();
        if (string.IsNullOrWhiteSpace(settings.ApiUrl) ||
            string.IsNullOrWhiteSpace(settings.ApiKey) ||
            string.IsNullOrWhiteSpace(settings.Model))
        {
            reason = "Set QRE_API_URL, QRE_API_KEY, and QRE_MODEL to enable live QueryRuntime integration tests.";
            return false;
        }

        host = new RealQueryRuntimeTestHost(settings, CreateChatClient(settings));
        reason = string.Empty;
        return true;
    }

    public ChatOptions CreateChatOptions(IReadOnlyList<AIFunction>? tools = null, int maxOutputTokens = 256)
    {
        return new ChatOptions
        {
            Temperature = 0,
            TopP = Settings.TopP,
            MaxOutputTokens = maxOutputTokens,
            Tools = tools?.Cast<AITool>().ToList()
        };
    }

    public void Dispose() => _chatClient.Dispose();

    private static IChatClient CreateChatClient(RealQreModelSettings settings)
        => CreateVllmChatClient(settings.ApiUrl, settings.ApiKey, settings.Model, settings.ApiMode);

    private static IChatClient CreateVllmChatClient(
        string apiUrl,
        string apiKey,
        string model,
        string? apiMode = null)
        => CreateVllmChatClient(ToAbsoluteUri(apiUrl), apiKey, model, ResolveApiMode(apiMode));

    private static IChatClient CreateVllmChatClient(
        Uri apiUrl,
        string apiKey,
        string model,
        VllmApiMode apiMode)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var normalizedModel = model.Trim().ToLowerInvariant();
        var apiUrlText = apiUrl.ToString();

        if (IsGptOssModel(normalizedModel))
        {
            return new VllmGptOssChatClient(apiUrlText, apiKey, model, httpClient: null, apiMode);
        }

        if (IsOpenAiGptModel(normalizedModel))
        {
            return new VllmOpenAiGptClient(apiUrlText, apiKey, model, httpClient: null, apiMode);
        }

        if (IsGeminiModel(normalizedModel))
        {
            if (apiMode != VllmApiMode.ChatCompletions)
            {
                throw new NotSupportedException("Gemini client does not support non-ChatCompletions VllmApiMode.");
            }

            return new VllmGemini3ChatClient(apiUrlText, apiKey, model);
        }

        if (IsClaudeModel(normalizedModel))
        {
            return new VllmClaudeChatClient(apiUrlText, apiKey, model, httpClient: null, apiMode);
        }

        if (IsKimiModel(normalizedModel))
        {
            return new VllmKimiK2ChatClient(apiUrlText, apiKey, model, httpClient: null, apiMode);
        }

        if (IsMiniMaxModel(normalizedModel))
        {
            return new VllmMiniMaxChatClient(apiUrlText, apiKey, model, httpClient: null, apiMode);
        }

        if (IsGlmModel(normalizedModel))
        {
            return new VllmGlmChatClient(apiUrlText, apiKey, model, httpClient: null, apiMode);
        }

        if (IsQwenModel(normalizedModel))
        {
            return new VllmQwen3NextChatClient(apiUrlText, apiKey, model, httpClient: null, apiMode);
        }

        if (IsDeepseekModel(normalizedModel))
        {
            return new VllmDeepseekV3ChatClient(apiUrlText, apiKey, model, httpClient: null, apiMode);
        }

        return new VllmQwen3NextChatClient(apiUrlText, apiKey, model, httpClient: null, apiMode);
    }

    private static VllmApiMode ResolveApiMode(string? configuredValue)
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
            _ => throw new InvalidOperationException($"Unsupported QRE api mode value '{configuredValue}'.")
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

    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "Test host accepts endpoint text from environment variables.")]
    private static Uri ToAbsoluteUri(string apiUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiUrl);

        return new Uri(apiUrl, UriKind.Absolute);
    }

    internal sealed class RealQreModelSettings
    {
        public string ApiUrl { get; init; } = string.Empty;

        public string ApiKey { get; init; } = string.Empty;

        public string Model { get; init; } = string.Empty;

        public string? ApiMode { get; init; }

        public float? TopP { get; init; }

        public static RealQreModelSettings FromEnvironment()
            => new()
            {
                ApiUrl = Environment.GetEnvironmentVariable("QRE_API_URL") ?? string.Empty,
                ApiKey = Environment.GetEnvironmentVariable("QRE_API_KEY") ?? string.Empty,
                Model = Environment.GetEnvironmentVariable("QRE_MODEL") ?? string.Empty,
                ApiMode = Environment.GetEnvironmentVariable("QRE_API_MODE"),
                TopP = TryParseFloat(Environment.GetEnvironmentVariable("QRE_TOP_P"))
            };

        private static float? TryParseFloat(string? value)
            => float.TryParse(value, out var parsed) ? parsed : null;
    }
}
