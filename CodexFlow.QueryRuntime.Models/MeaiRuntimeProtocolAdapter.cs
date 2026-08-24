using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Models;

/// <summary>
/// Converts Microsoft.Extensions.AI messages and stream updates at the adapter
/// boundary. Protocol remains provider-free and never references MEAI.
/// </summary>
public static class MeaiRuntimeProtocolAdapter
{
    public static IReadOnlyList<RuntimeMessage> ToProtocolMessages(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Select(ToProtocolMessage).ToArray();
    }

    public static IReadOnlyList<ChatMessage> ToMeaiMessages(IReadOnlyList<RuntimeMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Select(ToMeaiMessage).ToArray();
    }

    public static IReadOnlyList<RuntimeModelStreamEvent> ToProtocolEvents(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var events = new List<RuntimeModelStreamEvent>();
        var reasoningUpdate = update as ReasoningChatResponseUpdate;
        var emittedReasoning = false;
        var emittedUsage = false;
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent text when reasoningUpdate?.Thinking == true:
                    events.Add(new RuntimeReasoningDeltaEvent(text.Text));
                    emittedReasoning = true;
                    break;
                case TextContent text:
                    events.Add(new RuntimeTextDeltaEvent(text.Text));
                    break;
                case TextReasoningContent reasoning:
                    events.Add(new RuntimeReasoningDeltaEvent(reasoning.Text, reasoning.ProtectedData));
                    emittedReasoning = true;
                    break;
                case FunctionCallContent call:
                    events.Add(new RuntimeToolCallEvent(ToProtocolToolCall(call)));
                    break;
                case UsageContent usage:
                    events.Add(new RuntimeUsageEvent(ToProtocolUsage(usage.Details)));
                    emittedUsage = true;
                    break;
                case ErrorContent error:
                    events.Add(new RuntimeWarningEvent(new RuntimeWarning("meai_error_content", error.Message)));
                    break;
                default:
                    events.Add(new RuntimeWarningEvent(new RuntimeWarning(
                        "unsupported_meai_content",
                        $"Unsupported MEAI content: {content.GetType().Name}")));
                    break;
            }
        }

        if (reasoningUpdate?.Thinking == true &&
            !emittedReasoning &&
            !string.IsNullOrEmpty(reasoningUpdate.Reasoning))
        {
            events.Add(new RuntimeReasoningDeltaEvent(reasoningUpdate.Reasoning));
        }
        if (update is UsageChatResponseUpdate { Usage: not null } usageUpdate && !emittedUsage)
        {
            events.Add(new RuntimeUsageEvent(ToProtocolUsage(usageUpdate.Usage)));
        }

        if (update.FinishReason.HasValue)
        {
            events.Add(new RuntimeModelCompletedEvent(ToProtocolStopReason(update.FinishReason.Value)));
        }

        return events;
    }

    private static RuntimeMessage ToProtocolMessage(ChatMessage message)
        => new(
            ToProtocolRole(message.Role),
            message.Contents.Select(ToProtocolItem).ToArray());

    private static RuntimeItem ToProtocolItem(AIContent content)
        => content switch
        {
            TextContent text => new RuntimeTextItem(text.Text),
            TextReasoningContent reasoning => new RuntimeReasoningItem(reasoning.Text, reasoning.ProtectedData),
            FunctionCallContent call => new RuntimeToolCallItem(ToProtocolToolCall(call)),
            FunctionResultContent result => new RuntimeToolResultItem(new RuntimeToolResult(
                new RuntimeInvocationId(result.CallId),
                result.Result?.ToString(),
                result.Exception == null,
                result.Exception == null
                    ? null
                    : new RuntimeError(
                        RuntimeErrorCategory.ToolFailed,
                        "meai_tool_result_error",
                        result.Exception.Message))),
            _ => throw new RuntimeProtocolAdapterException($"Unsupported MEAI message content: {content.GetType().Name}")
        };

    private static ChatMessage ToMeaiMessage(RuntimeMessage message)
        => new(ToMeaiRole(message.Role), message.Items.Select(ToMeaiContent).ToList());

    private static AIContent ToMeaiContent(RuntimeItem item)
        => item switch
        {
            RuntimeTextItem text => new TextContent(text.Text),
            RuntimeReasoningItem reasoning => new TextReasoningContent(reasoning.Text)
            {
                ProtectedData = reasoning.ProtectedData
            },
            RuntimeToolCallItem call => new FunctionCallContent(
                call.Call.InvocationId.Value,
                call.Call.Name,
                ToMeaiArguments(call.Call.Arguments))
            {
                InformationalOnly = call.Call.InformationalOnly
            },
            RuntimeToolResultItem result => new FunctionResultContent(
                result.Result.InvocationId.Value,
                result.Result.Text),
            RuntimeArtifactItem => throw new RuntimeProtocolAdapterException(
                "Artifact conversion requires a provider-specific adapter."),
            _ => throw new RuntimeProtocolAdapterException($"Unsupported Runtime item: {item.GetType().Name}")
        };

    private static RuntimeToolCall ToProtocolToolCall(FunctionCallContent call)
        => new(
            new RuntimeInvocationId(call.CallId),
            call.Name,
            ProtocolJsonValueNormalizer.ToObjectElement(call.Arguments),
            call.InformationalOnly);

    private static RuntimeUsage ToProtocolUsage(UsageDetails usage)
        => new(
            usage.InputTokenCount,
            usage.OutputTokenCount,
            usage.TotalTokenCount,
            usage.AdditionalCounts == null
                ? null
                : new Dictionary<string, long>(usage.AdditionalCounts, StringComparer.Ordinal));

    private static RuntimeMessageRole ToProtocolRole(ChatRole role)
    {
        if (role == ChatRole.System)
        {
            return RuntimeMessageRole.System;
        }
        if (role == ChatRole.User)
        {
            return RuntimeMessageRole.User;
        }
        if (role == ChatRole.Assistant)
        {
            return RuntimeMessageRole.Assistant;
        }
        if (role == ChatRole.Tool)
        {
            return RuntimeMessageRole.Tool;
        }

        throw new RuntimeProtocolAdapterException($"Unsupported MEAI role: {role}");
    }

    private static ChatRole ToMeaiRole(RuntimeMessageRole role)
        => role switch
        {
            RuntimeMessageRole.System => ChatRole.System,
            RuntimeMessageRole.User => ChatRole.User,
            RuntimeMessageRole.Assistant => ChatRole.Assistant,
            RuntimeMessageRole.Tool => ChatRole.Tool,
            _ => throw new RuntimeProtocolAdapterException($"Unsupported Runtime role: {role}")
        };

    private static RuntimeModelStopReason ToProtocolStopReason(ChatFinishReason reason)
    {
        if (reason == ChatFinishReason.Stop)
        {
            return RuntimeModelStopReason.EndTurn;
        }
        if (reason == ChatFinishReason.ToolCalls)
        {
            return RuntimeModelStopReason.ToolCall;
        }
        if (reason == ChatFinishReason.Length)
        {
            return RuntimeModelStopReason.MaxOutputTokens;
        }
        if (reason == ChatFinishReason.ContentFilter)
        {
            return RuntimeModelStopReason.ContentFilter;
        }

        return RuntimeModelStopReason.Unknown;
    }

    private static Dictionary<string, object?> ToMeaiArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new RuntimeProtocolAdapterException("Tool arguments must be a JSON object.");
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            result.Add(property.Name, ProtocolJsonValueNormalizer.ToObject(property.Value));
        }
        return result;
    }
}

public sealed class RuntimeProtocolAdapterException(string message) : InvalidOperationException(message);

internal static class ProtocolJsonValueNormalizer
{
    public static JsonElement ToObjectElement(IDictionary<string, object?>? arguments)
    {
        var root = new JsonObject();
        if (arguments != null)
        {
            foreach (var (name, value) in arguments)
            {
                root.Add(name, ToNode(value));
            }
        }

        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }

    public static object? ToObject(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.Array => value.EnumerateArray().Select(ToObject).ToArray(),
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => ToObject(property.Value),
                StringComparer.Ordinal),
            _ => throw new RuntimeProtocolAdapterException($"Unsupported JSON value kind: {value.ValueKind}")
        };

    private static JsonNode? ToNode(object? value)
        => value switch
        {
            null => null,
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            JsonNode node => node.DeepClone(),
            string text => JsonValue.Create(text),
            bool boolean => JsonValue.Create(boolean),
            byte number => JsonValue.Create(number),
            sbyte number => JsonValue.Create(number),
            short number => JsonValue.Create(number),
            ushort number => JsonValue.Create(number),
            int number => JsonValue.Create(number),
            uint number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            ulong number => JsonValue.Create(number),
            float number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            decimal number => JsonValue.Create(number),
            Guid guid => JsonValue.Create(guid),
            DateTime dateTime => JsonValue.Create(dateTime),
            DateTimeOffset dateTimeOffset => JsonValue.Create(dateTimeOffset),
            IDictionary<string, object?> dictionary => ToObjectNode(dictionary),
            IEnumerable sequence when value is not string => ToArrayNode(sequence),
            _ => throw new RuntimeProtocolAdapterException(
                $"Unsupported tool argument value type: {value.GetType().FullName}")
        };

    private static JsonObject ToObjectNode(IDictionary<string, object?> values)
    {
        var result = new JsonObject();
        foreach (var (name, value) in values)
        {
            result.Add(name, ToNode(value));
        }
        return result;
    }

    private static JsonArray ToArrayNode(IEnumerable values)
    {
        var result = new JsonArray();
        foreach (var value in values)
        {
            result.Add(ToNode(value));
        }
        return result;
    }
}
