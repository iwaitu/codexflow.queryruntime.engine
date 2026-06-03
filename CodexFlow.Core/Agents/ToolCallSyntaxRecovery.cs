using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Text;

namespace CodexFlow.Core.Agents;

internal static class ToolCallSyntaxRecovery
{
    public static bool TryNormalizeInlineInvocation(
        string? rawToolName,
        IDictionary<string, object?>? existingArguments,
        out string normalizedToolName,
        out Dictionary<string, object?> recoveredArguments)
    {
        normalizedToolName = rawToolName?.Trim() ?? string.Empty;
        recoveredArguments = CloneArguments(existingArguments);

        if (string.IsNullOrWhiteSpace(rawToolName))
        {
            return false;
        }

        var candidate = rawToolName.Trim().TrimEnd(';').Trim();
        var openParenIndex = candidate.IndexOf('(');
        if (openParenIndex <= 0 || !candidate.EndsWith(')'))
        {
            return false;
        }

        var toolName = candidate[..openParenIndex].Trim();
        if (!LooksLikeToolIdentifier(toolName))
        {
            return false;
        }

        var argumentPayload = candidate[(openParenIndex + 1)..^1].Trim();
        if (!TryParseInlineArguments(argumentPayload, out var parsedArguments))
        {
            return false;
        }

        normalizedToolName = toolName;
        MergeRecoveredArguments(recoveredArguments, parsedArguments);
        return true;
    }

    public static Dictionary<string, object?> CloneArguments(IDictionary<string, object?>? arguments)
    {
        var cloned = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (arguments == null)
        {
            return cloned;
        }

        foreach (var entry in arguments)
        {
            cloned[entry.Key] = entry.Value;
        }

        return cloned;
    }

    private static bool TryParseInlineArguments(string payload, out Dictionary<string, object?> arguments)
    {
        arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return true;
        }

        if (TryParseJsonObject(payload, out var parsedObject))
        {
            arguments = parsedObject;
            return true;
        }

        foreach (var segment in SplitTopLevel(payload, ','))
        {
            var trimmedSegment = segment.Trim();
            if (trimmedSegment.Length == 0)
            {
                continue;
            }

            var separatorIndex = FindTopLevelSeparator(trimmedSegment, '=');
            if (separatorIndex <= 0)
            {
                return false;
            }

            var rawKey = trimmedSegment[..separatorIndex].Trim();
            var valueText = trimmedSegment[(separatorIndex + 1)..].Trim();
            var key = TrimQuotes(rawKey);
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            if (!TryParseValue(valueText, out var value))
            {
                return false;
            }

            arguments[key] = value;
        }

        return true;
    }

    private static bool TryParseJsonObject(string payload, out Dictionary<string, object?> arguments)
    {
        arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!payload.StartsWith('{') || !payload.EndsWith('}'))
        {
            return false;
        }

        try
        {
            var token = JToken.Parse(payload);
            if (token is not JObject obj)
            {
                return false;
            }

            foreach (var property in obj.Properties())
            {
                arguments[property.Name] = ConvertToken(property.Value);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseValue(string valueText, out object? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(valueText))
        {
            return false;
        }

        var trimmed = valueText.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) ||
            (trimmed.StartsWith('[') && trimmed.EndsWith(']')) ||
            (trimmed.StartsWith('"') && trimmed.EndsWith('"')))
        {
            try
            {
                value = ConvertToken(JToken.Parse(trimmed));
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            value = trimmed[1..^1]
                .Replace("\\'", "'", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
            return true;
        }

        if (bool.TryParse(trimmed, out var boolValue))
        {
            value = boolValue;
            return true;
        }

        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
        {
            value = null;
            return true;
        }

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            value = longValue;
            return true;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            value = doubleValue;
            return true;
        }

        value = trimmed;
        return true;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        var braceDepth = 0;
        var bracketDepth = 0;
        var parenDepth = 0;
        char quote = '\0';
        var escape = false;

        foreach (var ch in text)
        {
            if (quote != '\0')
            {
                current.Append(ch);

                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escape = true;
                }
                else if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (ch)
            {
                case '\'':
                case '"':
                    quote = ch;
                    current.Append(ch);
                    break;
                case '{':
                    braceDepth++;
                    current.Append(ch);
                    break;
                case '}':
                    braceDepth = Math.Max(0, braceDepth - 1);
                    current.Append(ch);
                    break;
                case '[':
                    bracketDepth++;
                    current.Append(ch);
                    break;
                case ']':
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    current.Append(ch);
                    break;
                case '(':
                    parenDepth++;
                    current.Append(ch);
                    break;
                case ')':
                    parenDepth = Math.Max(0, parenDepth - 1);
                    current.Append(ch);
                    break;
                default:
                    if (ch == separator && braceDepth == 0 && bracketDepth == 0 && parenDepth == 0)
                    {
                        segments.Add(current.ToString());
                        current.Clear();
                    }
                    else
                    {
                        current.Append(ch);
                    }

                    break;
            }
        }

        segments.Add(current.ToString());
        return segments;
    }

    private static int FindTopLevelSeparator(string text, char separator)
    {
        var braceDepth = 0;
        var bracketDepth = 0;
        var parenDepth = 0;
        char quote = '\0';
        var escape = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escape = true;
                }
                else if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (ch)
            {
                case '\'':
                case '"':
                    quote = ch;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    braceDepth = Math.Max(0, braceDepth - 1);
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    parenDepth = Math.Max(0, parenDepth - 1);
                    break;
                default:
                    if (ch == separator && braceDepth == 0 && bracketDepth == 0 && parenDepth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    private static void MergeRecoveredArguments(
        Dictionary<string, object?> target,
        IDictionary<string, object?> recovered)
    {
        foreach (var entry in recovered)
        {
            if (!target.TryGetValue(entry.Key, out var existingValue) || IsMissing(existingValue))
            {
                target[entry.Key] = entry.Value;
            }
        }
    }

    private static object? ConvertToken(JToken token)
    {
        return token switch
        {
            JObject obj => obj.Properties()
                .ToDictionary(property => property.Name, property => ConvertToken(property.Value), StringComparer.OrdinalIgnoreCase),
            JArray array => array.Select(ConvertToken).ToList(),
            JValue value => value.Value,
            _ => token.ToString()
        };
    }

    private static bool LooksLikeToolIdentifier(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (!(char.IsLetter(candidate[0]) || candidate[0] == '_'))
        {
            return false;
        }

        for (var i = 1; i < candidate.Length; i++)
        {
            var ch = candidate[i];
            if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static string TrimQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static bool IsMissing(object? value)
    {
        return value == null || (value is string text && string.IsNullOrWhiteSpace(text));
    }
}
