using System.Diagnostics.CodeAnalysis;
using CodexFlow.QueryRuntime.Models;
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
}
