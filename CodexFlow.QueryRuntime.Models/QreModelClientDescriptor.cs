using System.Diagnostics.CodeAnalysis;
using System.Net.Http;

namespace CodexFlow.QueryRuntime.Models;

/// <summary>
/// Fully resolved inputs required to construct a provider model client.
/// </summary>
public sealed record QreModelClientDescriptor
{
    /// <summary>Absolute provider endpoint.</summary>
    public required Uri ApiUrl { get; init; }

    /// <summary>Provider API key.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Provider model identifier (e.g. <c>qwen3-next</c>).</summary>
    public required string Model { get; init; }

    /// <summary>Wire shape the adapter should speak.</summary>
    public QreModelApiMode ApiMode { get; init; } = QreModelApiModeParser.Default;

    /// <summary>Optional shared <see cref="HttpClient"/> for the underlying client.</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>
    /// Builds a descriptor from raw CLI/environment text. The api-mode string is
    /// parsed explicitly and unknown values fail via
    /// <see cref="QreUnsupportedApiModeValueException"/>.
    /// </summary>
    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "CLI accepts endpoint text from flags and environment variables.")]
    public static QreModelClientDescriptor Create(
        string apiUrl,
        string apiKey,
        string model,
        string? apiMode = null,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        return new QreModelClientDescriptor
        {
            ApiUrl = new Uri(apiUrl, UriKind.Absolute),
            ApiKey = apiKey,
            Model = model,
            ApiMode = QreModelApiModeParser.Parse(apiMode),
            HttpClient = httpClient
        };
    }
}
