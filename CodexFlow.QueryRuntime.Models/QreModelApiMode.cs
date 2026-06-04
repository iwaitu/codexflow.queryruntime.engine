namespace CodexFlow.QueryRuntime.Models;

/// <summary>
/// Provider-neutral request shape that a model adapter speaks on the wire.
/// </summary>
public enum QreModelApiMode
{
    /// <summary>OpenAI-compatible <c>/chat/completions</c> shape.</summary>
    ChatCompletions = 0,

    /// <summary>OpenAI Responses API shape.</summary>
    Responses = 1,

    /// <summary>Anthropic Messages API shape.</summary>
    AnthropicMessages = 2
}

/// <summary>
/// Parses the textual <c>--api-mode</c> / <c>QRE_API_MODE</c> value into a
/// <see cref="QreModelApiMode"/>. Unknown values fail explicitly rather than
/// silently falling back to a default shape.
/// </summary>
public static class QreModelApiModeParser
{
    /// <summary>The default mode used when no value is supplied.</summary>
    public const QreModelApiMode Default = QreModelApiMode.ChatCompletions;

    /// <summary>
    /// Parses <paramref name="value"/>. A null/empty value resolves to
    /// <see cref="Default"/>. Unknown values throw
    /// <see cref="QreUnsupportedApiModeValueException"/>.
    /// </summary>
    public static QreModelApiMode Parse(string? value)
    {
        if (!TryParse(value, out var mode, out var error))
        {
            throw new QreUnsupportedApiModeValueException(value ?? string.Empty, error);
        }

        return mode;
    }

    /// <summary>
    /// Attempts to parse <paramref name="value"/>. Returns <c>false</c> with a
    /// human-readable <paramref name="error"/> when the value is not recognized.
    /// </summary>
    public static bool TryParse(string? value, out QreModelApiMode mode, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            mode = Default;
            return true;
        }

        var normalized = value.Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        switch (normalized)
        {
            case "chat":
            case "chatcompletion":
            case "chatcompletions":
            case "completions":
                mode = QreModelApiMode.ChatCompletions;
                return true;
            case "response":
            case "responses":
                mode = QreModelApiMode.Responses;
                return true;
            case "anthropic":
            case "anthropicmessage":
            case "anthropicmessages":
            case "message":
            case "messages":
                mode = QreModelApiMode.AnthropicMessages;
                return true;
            default:
                mode = Default;
                error = "Expected one of: chat-completions, responses, anthropic-messages.";
                return false;
        }
    }
}
