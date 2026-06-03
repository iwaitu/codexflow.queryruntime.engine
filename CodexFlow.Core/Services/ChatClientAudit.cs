using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections;

namespace CodexFlow.Core.Services;

public static class ChatClientAudit
{
    private const int MaxPreviewLength = 600;

    public static void AppendStreamingUpdate(
        ChatResponseUpdate update,
        StringBuilder responseText,
        StringBuilder thinkingText)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(responseText);
        ArgumentNullException.ThrowIfNull(thinkingText);

        var isThinking = update is ReasoningChatResponseUpdate ru && ru.Thinking;
        if (isThinking)
        {
            var reasonText = ExtractReasoningText(update);
            if (!string.IsNullOrEmpty(reasonText))
            {
                thinkingText.Append(reasonText);
            }
        }

        var hasTextFromContents = false;

        foreach (var part in update.Contents ?? Array.Empty<AIContent>())
        {
            if (part is not TextContent tc) continue;
            var text = tc.Text ?? string.Empty;
            if (string.IsNullOrEmpty(text)) continue;

            hasTextFromContents = true;
            if (isThinking) thinkingText.Append(text);
            else responseText.Append(text);
        }

        if (!hasTextFromContents && !string.IsNullOrEmpty(update.Text))
        {
            if (isThinking && string.IsNullOrEmpty(ExtractReasoningText(update))) thinkingText.Append(update.Text);
            else responseText.Append(update.Text);
        }
    }

    public static (string Response, string Thinking) ExtractResponse(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var responseText = new StringBuilder();
        var thinkingText = new StringBuilder();

        foreach (var message in response.Messages ?? Array.Empty<ChatMessage>())
        {
            foreach (var part in message.Contents ?? Array.Empty<AIContent>())
            {
                switch (part)
                {
                    case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        responseText.Append(tc.Text);
                        break;
                    default:
                        var reasoningText = ExtractReasoningText(part);
                        if (!string.IsNullOrEmpty(reasoningText))
                        {
                            thinkingText.Append(reasoningText);
                        }
                        break;
                }
            }
        }

        if (responseText.Length == 0 && !string.IsNullOrEmpty(response.Text))
        {
            responseText.Append(response.Text);
        }

        return (responseText.ToString(), thinkingText.ToString());
    }

    public static void LogInteraction(
        ILogger logger,
        string source,
        string mode,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        string response,
        string thinking,
        Exception? error = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(thinking);

#pragma warning disable CA1031
        try
        {
            var record = new
            {
                timestamp = DateTime.UtcNow.ToString("O"),
                eventType = error == null ? "ichatclient_interaction" : "ichatclient_interaction_error",
                sessionId = "__ichatclient__",
                stage = 0,
                round = 0,
                payload = new
                {
                    source,
                    mode,
                    request = SnapshotMessages(messages),
                    options = SnapshotOptions(options),
                    response = Truncate(response),
                    thinking = Truncate(thinking),
                    responseLength = response.Length,
                    thinkingLength = thinking.Length,
                    error = error?.Message
                }
            };

            logger.LogInformation("FULLTEXT|{AuditJson}", JsonConvert.SerializeObject(record));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write IChatClient audit log. source={Source}, mode={Mode}", source, mode);
        }
#pragma warning restore CA1031
    }

    private static object SnapshotOptions(ChatOptions? options) => new
    {
        temperature = options?.Temperature,
        topP = options?.TopP,
        toolCount = options?.Tools?.Count ?? 0,
        toolNames = options?.Tools?.Select(static tool => tool.Name).ToArray() ?? [],
        toolMode = ReadOptionProperty(options, "ToolMode"),
        thinkingEnabled = ReadOptionProperty(options, "ThinkingEnabled"),
        maxOutputTokens = options?.MaxOutputTokens
    };

    private static object? ReadOptionProperty(ChatOptions? options, string propertyName)
    {
        if (options == null)
        {
            return null;
        }

        var property = options.GetType().GetProperty(propertyName);
        var value = property?.GetValue(options);
        return value switch
        {
            null => null,
            string text => text,
            bool boolean => boolean,
            _ => value.ToString()
        };
    }

    private static List<object> SnapshotMessages(IEnumerable<ChatMessage> messages)
    {
        var snapshot = new List<object>();
        var index = 0;
        foreach (var message in messages)
        {
            var text = message.Text;
            if (string.IsNullOrEmpty(text) && message.Contents != null)
            {
                text = string.Concat(message.Contents
                    .Select(ExtractAuditableContentText)
                    .Where(t => !string.IsNullOrEmpty(t)));
            }

            snapshot.Add(new
            {
                index = index++,
                role = message.Role.ToString(),
                text = Truncate(text ?? string.Empty)
            });
        }

        return snapshot;
    }

    private static string Truncate(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= MaxPreviewLength) return text;
        return text[..MaxPreviewLength] + "...[truncated]";
    }

    private static string ExtractAuditableContentText(AIContent content)
    {
        switch (content)
        {
            case TextContent tc:
                return tc.Text ?? string.Empty;
            case FunctionResultContent frc:
                return ExtractFunctionResultText(frc);
            default:
                return string.Empty;
        }
    }

    private static string ExtractFunctionResultText(FunctionResultContent content)
    {
        var callId = content.CallId ?? string.Empty;
        var result = content.Result;
        var resultText = result switch
        {
            null => string.Empty,
            string text => text,
            IEnumerable enumerable => string.Join(" ", enumerable.Cast<object?>().Select(static item => item?.ToString()).Where(static item => !string.IsNullOrWhiteSpace(item))),
            _ => result.ToString() ?? string.Empty
        };

        return string.IsNullOrWhiteSpace(callId)
            ? resultText
            : $"[tool_result:{callId}] {resultText}";
    }

    private static string ExtractReasoningText(ChatResponseUpdate update)
    {
        if (update is not ReasoningChatResponseUpdate reasoningUpdate || !reasoningUpdate.Thinking)
        {
            return string.Empty;
        }

        var reasonProp = reasoningUpdate.GetType().GetProperty("Reason");
        if (reasonProp?.GetValue(reasoningUpdate) is string reason && !string.IsNullOrEmpty(reason))
        {
            return reason;
        }

        var textProp = reasoningUpdate.GetType().GetProperty("Text");
        if (textProp?.GetValue(reasoningUpdate) is string text && !string.IsNullOrEmpty(text))
        {
            return text;
        }

        return string.Empty;
    }

    private static string ExtractReasoningText(AIContent content)
    {
        var typeName = content.GetType().Name;
        if (!typeName.Contains("Reason", StringComparison.OrdinalIgnoreCase) &&
            !typeName.Contains("Think", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var reasonProp = content.GetType().GetProperty("Reason");
        if (reasonProp?.GetValue(content) is string reason && !string.IsNullOrEmpty(reason))
        {
            return reason;
        }

        var textProp = content.GetType().GetProperty("Text");
        if (textProp?.GetValue(content) is string text && !string.IsNullOrEmpty(text))
        {
            return text;
        }

        return string.Empty;
    }
}
