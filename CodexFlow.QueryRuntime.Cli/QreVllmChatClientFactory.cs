using System.Diagnostics.CodeAnalysis;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using Microsoft.Extensions.AI;

/// <summary>
/// Temporary CLI bridge that delegates model-client construction to the explicit
/// provider adapters in <see cref="QreModelProviderSelector"/>. It carries no
/// model-selection logic of its own; selection, api-mode validation and
/// fail-closed behavior all live in the provider-neutral Models surface.
/// </summary>
internal static class QreVllmChatClientFactory
{
    private static readonly QreModelProviderSelector Selector = QreModelProviderSelector.CreateDefault();

    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "CLI accepts endpoint text from flags and environment variables.")]
    public static IChatClient Create(
        string apiUrl,
        string apiKey,
        string model,
        string? apiMode = null,
        HttpClient? httpClient = null)
        => Selector.CreateClient(apiUrl, apiKey, model, apiMode, httpClient);

    /// <summary>
    /// Builds the chat client over a CLI-owned HttpClient whose pipeline contains
    /// the outbound diagnostic handler (when enabled) above the transport. The
    /// locked SDK disposes the injected HttpClient when the chat client is
    /// disposed, which disposes the handler chain; that is safe only because the
    /// CLI owns this client. The client mirrors the SDK's default shared client:
    /// default handler settings and no HttpClient timeout.
    /// </summary>
    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "CLI accepts endpoint text from flags and environment variables.")]
    public static (IChatClient Client, QreOutboundDiagnosticsTarget Target) CreateOwned(
        string apiUrl,
        string apiKey,
        string model,
        string? apiMode,
        QreOutboundDiagnostics? diagnostics,
        Func<QreModelApiMode, HttpMessageHandler>? transportFactory)
    {
        var descriptor = QreModelClientDescriptor.Create(apiUrl, apiKey, model, apiMode);
        var provider = Selector.Select(descriptor.Model);
        if (!provider.SupportedApiModes.Contains(descriptor.ApiMode))
        {
            throw new QreUnsupportedApiModeException(descriptor.Model, provider.Id, descriptor.ApiMode, provider.SupportedApiModes);
        }
        HttpMessageHandler handler = transportFactory?.Invoke(descriptor.ApiMode) ?? new HttpClientHandler();
        if (diagnostics is { IsEnabled: true })
        {
            handler = diagnostics.CreateHandler(handler);
        }
        var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            return (
                Selector.CreateClient(descriptor with { HttpClient = httpClient }),
                QreOutboundDiagnosticsTarget.ForProvider(provider, descriptor.ApiMode));
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }
    }
}
