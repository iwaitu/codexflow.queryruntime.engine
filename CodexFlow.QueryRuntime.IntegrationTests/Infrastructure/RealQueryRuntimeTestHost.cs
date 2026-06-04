using CodexFlow.QueryRuntime.Experimental;
using CodexFlow.QueryRuntime.Models;
using Microsoft.Extensions.AI;

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

    // Live integration tests share the exact same provider-neutral adapter
    // surface as the CLI, so selection behavior cannot drift between them.
    private static IChatClient CreateChatClient(RealQreModelSettings settings)
        => QreModelProviderSelector.CreateDefault()
            .CreateClient(settings.ApiUrl, settings.ApiKey, settings.Model, settings.ApiMode);

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
