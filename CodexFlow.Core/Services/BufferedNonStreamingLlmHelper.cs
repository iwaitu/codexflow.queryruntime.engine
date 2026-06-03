using CodexFlow.Core.Governance;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Services;

/// <summary>
/// Governed helper for one-shot buffered LLM calls that intentionally stay off the runtime path.
/// It centralizes response extraction and JSON fence cleanup without changing caller semantics.
/// </summary>
[ApprovedNonStreamingLlm(
    "Governed buffered one-shot helper for short JSON/text calls that do not benefit from runtime streaming.",
    Ticket = "non-streaming-llm-migration",
    ReviewBy = "2026-06-30",
    Scope = ApprovedNonStreamingLlmScope.Facade)]
internal sealed class BufferedNonStreamingLlmHelper(
    IChatClient chatClient,
    ILogger logger)
{
    [ApprovedNonStreamingLlm(
        "Facade method for buffered one-shot text generation.",
        Ticket = "non-streaming-llm-migration",
        ReviewBy = "2026-06-30",
        Scope = ApprovedNonStreamingLlmScope.Facade)]
    public async Task<string> GetTextAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        string operationName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        var materializedMessages = messages.ToList();
        var response = await chatClient.GetResponseAsync(materializedMessages, options, ct).ConfigureAwait(false);
        var text = ExtractResponseText(response);

        logger.LogDebug("{OperationName} raw output: {Raw}", operationName, text);
        return text;
    }

    [ApprovedNonStreamingLlm(
        "Facade method for buffered one-shot JSON generation.",
        Ticket = "non-streaming-llm-migration",
        ReviewBy = "2026-06-30",
        Scope = ApprovedNonStreamingLlmScope.Facade)]
    public async Task<string> GetJsonTextAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        string operationName,
        CancellationToken ct = default)
    {
        var text = await GetTextAsync(messages, options, operationName, ct).ConfigureAwait(false);
        return CleanJsonEnvelope(text);
    }

    internal static string CleanJsonEnvelope(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();

        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["```json".Length..];
        }
        else if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[3..];
        }

        if (trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^3];
        }

        return trimmed.Trim();
    }

    private static string ExtractResponseText(ChatResponse? response)
    {
        if (response == null)
        {
            return string.Empty;
        }

        var lastMessageText = response.Messages?.LastOrDefault()?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(lastMessageText))
        {
            return lastMessageText;
        }

        return response.Text?.Trim() ?? string.Empty;
    }
}
