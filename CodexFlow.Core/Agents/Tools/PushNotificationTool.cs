using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using System.Text.Json;

namespace CodexFlow.Core.Agents.Tools;

public sealed class PushNotificationTool(IPushNotificationService notificationService) : ICodexTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => "push_notification";

    public string Description => "向用户或会话推送通知。参数: user_id, title?, message, session_id?, task_id?, job_id?, priority?, channels?, markdown_report?, metadata?。";

    public ToolCategory Category => ToolCategory.System;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 12_288);

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ToolArgumentNormalizer.NormalizeInPlace(arguments);

        var userId = arguments.GetValueOrDefault("user_id")?.ToString();
        var message = arguments.GetValueOrDefault("message")?.ToString()
            ?? arguments.GetValueOrDefault("short_summary")?.ToString();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return CodexToolResult.Error("Missing user_id.");
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            return CodexToolResult.Error("Missing message.");
        }

        var channels = ReadStringArray(arguments.GetValueOrDefault("channels"));
        if (channels.Length == 0)
        {
            channels = ["signalr_sync"];
        }

        var metadata = ReadStringDictionary(arguments.GetValueOrDefault("metadata"));
        var request = new PushNotificationRequest
        {
            UserId = userId,
            SessionId = arguments.GetValueOrDefault("session_id")?.ToString(),
            TaskId = arguments.GetValueOrDefault("task_id")?.ToString(),
            JobId = arguments.GetValueOrDefault("job_id")?.ToString(),
            Title = arguments.GetValueOrDefault("title")?.ToString() ?? "CodexFlow notification",
            Message = message,
            MarkdownReport = arguments.GetValueOrDefault("markdown_report")?.ToString() ?? string.Empty,
            Priority = arguments.GetValueOrDefault("priority")?.ToString() ?? "P2",
            Channels = channels,
            Metadata = metadata
        };

        var result = await notificationService.PushAsync(request, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return CodexToolResult.Error(result.Error ?? "push_notification failed.");
        }

        var payload = new
        {
            pushed = true,
            notification_id = result.NotificationId,
            delivered_channels = result.DeliveredChannels,
            request
        };

        return CodexToolResult.Succeeded(
            JsonSerializer.Serialize(payload, JsonOptions),
            payload,
            summary: $"notification pushed to {userId}: {string.Join(",", result.DeliveredChannels)}");
    }

    private static string[] ReadStringArray(object? raw)
    {
        if (raw == null)
        {
            return [];
        }

        if (raw is string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
        }

        if (raw is IEnumerable<string> strings)
        {
            return strings
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .ToArray();
        }

        if (raw is Newtonsoft.Json.Linq.JArray array)
        {
            return array
                .Values<string?>()
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .ToArray();
        }

        return [];
    }

    private static IReadOnlyDictionary<string, string> ReadStringDictionary(object? raw)
    {
        if (raw == null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        if (raw is IReadOnlyDictionary<string, string> typed)
        {
            return typed;
        }

        if (raw is IDictionary<string, string> dictionary)
        {
            return new Dictionary<string, string>(dictionary, StringComparer.OrdinalIgnoreCase);
        }

        if (raw is IDictionary<string, object?> objectDictionary)
        {
            return objectDictionary
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }

        if (raw is Newtonsoft.Json.Linq.JObject json)
        {
            return json.Properties()
                .Where(property => !string.IsNullOrWhiteSpace(property.Name))
                .ToDictionary(property => property.Name, property => property.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
