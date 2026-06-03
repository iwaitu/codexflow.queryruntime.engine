using System.Collections;
using System.Reflection;
using System.Text;
using CodexFlow.Core.Models;

namespace CodexFlow.Core.Runtime;

public static class ToolResultTextFormatter
{
    private static readonly char[] InlineWhitespaceSeparators = ['\r', '\n', '\t'];

    public static string FormatCodexToolResult(CodexToolResult result, string? toolName = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var effectiveToolName = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName.Trim();
        var primary = FirstNonEmpty(result.Display, result.Output, result.Summary);
        var hint = NormalizeMultiline(result.SystemHint);
        var details = FormatMetadataDetails(result.Metadata);

        return result.Status switch
        {
            ToolResultStatus.Success => string.IsNullOrWhiteSpace(primary)
                ? $"（{effectiveToolName} 已执行完成，但没有返回输出）"
                : primary,
            ToolResultStatus.PartialSuccess => FormatPartialSuccess(effectiveToolName, primary, hint, details),
            ToolResultStatus.ValidationRequired => BuildStatusBlock(
                $"工具 `{effectiveToolName}` 输入校验失败。",
                primary,
                hint,
                details),
            ToolResultStatus.BlockedByGuardrail => BuildStatusBlock(
                $"工具 `{effectiveToolName}` 被 guardrail 拦截。",
                primary,
                hint,
                details),
            ToolResultStatus.Failed => BuildStatusBlock(
                $"工具 `{effectiveToolName}` 执行失败。",
                primary,
                hint,
                details),
            _ => string.IsNullOrWhiteSpace(primary)
                ? $"工具 `{effectiveToolName}` 返回了空结果。"
                : primary
        };
    }

    public static string FormatToolNotFound(string toolName, IEnumerable<string>? availableToolNames = null)
    {
        var sb = new StringBuilder();
        sb.Append("当前轮次不可用工具 `");
        sb.Append(string.IsNullOrWhiteSpace(toolName) ? "unknown" : toolName.Trim());
        sb.Append("`。");

        var suggestions = (availableToolNames ?? Array.Empty<string>())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(static name => $"`{name}`")
            .ToArray();
        if (suggestions.Length > 0)
        {
            sb.Append(" 当前已注入工具包括：");
            sb.Append(string.Join("、", suggestions));
            sb.Append('。');
        }

        return sb.ToString();
    }

    public static string FormatException(Exception ex, string? toolName = null)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var effectiveToolName = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName.Trim();
        var details = BuildExceptionDetails(ex);
        return BuildStatusBlock(
            $"工具 `{effectiveToolName}` 执行异常。",
            details,
            hint: null,
            details: null);
    }

    public static Dictionary<string, object?> BuildWrappedPayload(CodexToolResult result, string? toolName = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var display = string.IsNullOrWhiteSpace(result.Display)
            ? FormatCodexToolResult(result, toolName)
            : result.Display.Trim();
        var summary = string.IsNullOrWhiteSpace(result.Summary)
            ? SummarizeText(display, 220)
            : SummarizeText(result.Summary, 220);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["__icodex_tool_result"] = true,
            ["status"] = result.Status.ToString(),
            ["output"] = result.Output ?? string.Empty,
            ["summary"] = summary,
            ["display"] = display,
            ["truncated"] = result.IsOutputTruncated,
            ["system_hint"] = result.SystemHint,
            ["required_tool_name"] = result.SystemHintDetail?.RequiredToolName,
            ["tool_call_required"] = result.SystemHintDetail?.ToolCallRequired
        };
    }

    public static string? SummarizeText(string? text, int maxChars = 140)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = NormalizeInline(text);
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        return normalized[..Math.Max(0, maxChars - 3)] + "...";
    }

    private static string FormatPartialSuccess(
        string toolName,
        string? primary,
        string? hint,
        string? details)
    {
        if (string.IsNullOrWhiteSpace(primary))
        {
            return BuildStatusBlock(
                $"工具 `{toolName}` 部分成功，但没有返回可见输出。",
                primary: null,
                hint,
                details);
        }

        if (primary.Contains("部分成功", StringComparison.OrdinalIgnoreCase) ||
            primary.Contains("partial", StringComparison.OrdinalIgnoreCase))
        {
            return primary;
        }

        return BuildStatusBlock(
            $"工具 `{toolName}` 部分成功。",
            primary,
            hint,
            details);
    }

    private static string BuildStatusBlock(
        string header,
        string? primary,
        string? hint,
        string? details)
    {
        var sb = new StringBuilder();
        sb.Append(header);

        var normalizedPrimary = NormalizeMultiline(primary);
        if (!string.IsNullOrWhiteSpace(normalizedPrimary) &&
            !string.Equals(normalizedPrimary, header, StringComparison.Ordinal))
        {
            sb.AppendLine();
            sb.Append(normalizedPrimary);
        }

        if (!string.IsNullOrWhiteSpace(hint) &&
            (string.IsNullOrWhiteSpace(normalizedPrimary) ||
             !normalizedPrimary.Contains(hint, StringComparison.OrdinalIgnoreCase)))
        {
            sb.AppendLine();
            sb.Append("Hint: ");
            sb.Append(hint);
        }

        if (!string.IsNullOrWhiteSpace(details))
        {
            sb.AppendLine();
            sb.Append("Details:");
            sb.AppendLine();
            sb.Append(details);
        }

        return sb.ToString().TrimEnd();
    }

    private static string? BuildExceptionDetails(Exception ex)
    {
        var lines = new List<string>();

        if (TryGetNumericProperty(ex, "ExitCode", out var exitCode))
        {
            lines.Add($"exit code: {exitCode}");
        }

        AppendIfPresent(lines, ex.Message);
        AppendIfPresent(lines, TryGetStringProperty(ex, "StdErr"));
        AppendIfPresent(lines, TryGetStringProperty(ex, "Stderr"));
        AppendIfPresent(lines, TryGetStringProperty(ex, "StandardError"));
        AppendIfPresent(lines, TryGetStringProperty(ex, "StdOut"));
        AppendIfPresent(lines, TryGetStringProperty(ex, "Stdout"));
        AppendIfPresent(lines, TryGetStringProperty(ex, "StandardOutput"));

        if (lines.Count == 0)
        {
            lines.Add(ex.GetType().Name);
        }

        return string.Join(Environment.NewLine, lines.Distinct(StringComparer.Ordinal));
    }

    private static string? FormatMetadataDetails(object? metadata)
    {
        if (metadata == null)
        {
            return null;
        }

        if (metadata is string text)
        {
            return NormalizeMultiline(text);
        }

        if (metadata is IEnumerable<string> strings)
        {
            var items = strings
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Take(4)
                .Select(static value => $"- {NormalizeInline(value)}")
                .ToArray();
            return items.Length == 0 ? null : string.Join(Environment.NewLine, items);
        }

        if (metadata is IDictionary dictionary)
        {
            var lines = new List<string>();
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString();
                if (string.IsNullOrWhiteSpace(key) ||
                    string.Equals(key, "payload", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = FormatMetadataValue(entry.Value);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                lines.Add($"- {key}: {value}");
                if (lines.Count >= 4)
                {
                    break;
                }
            }

            return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
        }

        return NormalizeInline(metadata.ToString());
    }

    private static string? FormatMetadataValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is string text)
        {
            return NormalizeInline(text);
        }

        if (value is IEnumerable<string> strings)
        {
            var items = strings
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Take(3)
                .Select(static item => NormalizeInline(item))
                .ToArray();
            return items.Length == 0 ? null : string.Join("; ", items);
        }

        if (value is IEnumerable enumerable and not string)
        {
            var items = new List<string>();
            foreach (var item in enumerable)
            {
                var rendered = NormalizeInline(item?.ToString());
                if (string.IsNullOrWhiteSpace(rendered))
                {
                    continue;
                }

                items.Add(rendered);
                if (items.Count >= 3)
                {
                    break;
                }
            }

            return items.Count == 0 ? null : string.Join("; ", items);
        }

        return NormalizeInline(value.ToString());
    }

    private static string? TryGetStringProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property == null || property.PropertyType != typeof(string))
        {
            return null;
        }

        return property.GetValue(instance) as string;
    }

    private static bool TryGetNumericProperty(object instance, string propertyName, out int value)
    {
        value = 0;
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property == null)
        {
            return false;
        }

        var raw = property.GetValue(instance);
        switch (raw)
        {
            case int intValue:
                value = intValue;
                return true;
            case long longValue when longValue is >= int.MinValue and <= int.MaxValue:
                value = (int)longValue;
                return true;
            case short shortValue:
                value = shortValue;
                return true;
            default:
                return false;
        }
    }

    private static void AppendIfPresent(List<string> lines, string? value)
    {
        var normalized = NormalizeMultiline(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            lines.Add(normalized);
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string NormalizeInline(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
                " ",
                value
                    .Split(InlineWhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();
    }

    private static string NormalizeMultiline(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lines = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Take(8)
            .ToArray();

        return string.Join(Environment.NewLine, lines);
    }
}
