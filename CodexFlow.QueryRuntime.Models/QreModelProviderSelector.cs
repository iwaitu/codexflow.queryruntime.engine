using Microsoft.Extensions.AI;
using CodexFlow.QueryRuntime.Models.Providers;

namespace CodexFlow.QueryRuntime.Models;

/// <summary>
/// Resolves a model identifier to an explicit <see cref="IQreModelProvider"/> and
/// constructs its <see cref="IChatClient"/>. Selection is fail-closed: unknown
/// models and unsupported api-modes raise <see cref="QreModelSelectionException"/>
/// rather than silently falling back to an assumed provider shape.
/// </summary>
public sealed class QreModelProviderSelector
{
    private readonly IReadOnlyList<IQreModelProvider> _providers;

    public QreModelProviderSelector(IEnumerable<IQreModelProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = [.. providers];

        if (_providers.Count == 0)
        {
            throw new ArgumentException("At least one model provider is required.", nameof(providers));
        }
    }

    /// <summary>The registered providers, in selection order.</summary>
    public IReadOnlyList<IQreModelProvider> Providers => _providers;

    /// <summary>
    /// The built-in provider adapters, in the order they are evaluated. Order is
    /// significant: more specific prefixes (e.g. <c>gpt-oss</c>) precede broader
    /// ones (e.g. <c>openai/gpt-</c>).
    /// </summary>
    public static IReadOnlyList<IQreModelProvider> DefaultProviders { get; } =
    [
        new GptOssModelProvider(),
        new OpenAiGptModelProvider(),
        new GeminiModelProvider(),
        new ClaudeModelProvider(),
        new KimiModelProvider(),
        new MiniMaxModelProvider(),
        new GlmModelProvider(),
        new QwenModelProvider(),
        new DeepseekModelProvider()
    ];

    /// <summary>Creates a selector backed by the built-in provider adapters.</summary>
    public static QreModelProviderSelector CreateDefault() => new(DefaultProviders);

    /// <summary>
    /// Returns the provider that owns <paramref name="model"/>, or throws
    /// <see cref="QreUnknownModelException"/> when none match.
    /// </summary>
    public IQreModelProvider Select(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var normalized = model.Trim().ToLowerInvariant();
        foreach (var provider in _providers)
        {
            if (provider.CanHandle(normalized))
            {
                return provider;
            }
        }

        throw new QreUnknownModelException(model, [.. _providers.Select(p => p.Id)]);
    }

    /// <summary>
    /// Resolves the provider for <paramref name="descriptor"/>, validates the
    /// requested api-mode, and constructs the chat client.
    /// </summary>
    public IChatClient CreateClient(QreModelClientDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var provider = Select(descriptor.Model);
        if (!provider.SupportedApiModes.Contains(descriptor.ApiMode))
        {
            throw new QreUnsupportedApiModeException(
                descriptor.Model,
                provider.Id,
                descriptor.ApiMode,
                provider.SupportedApiModes);
        }

        return provider.CreateClient(descriptor);
    }

    /// <summary>
    /// Convenience overload that builds a descriptor from raw CLI/environment text
    /// and constructs the chat client.
    /// </summary>
    public IChatClient CreateClient(
        string apiUrl,
        string apiKey,
        string model,
        string? apiMode = null,
        HttpClient? httpClient = null)
        => CreateClient(QreModelClientDescriptor.Create(apiUrl, apiKey, model, apiMode, httpClient));
}
