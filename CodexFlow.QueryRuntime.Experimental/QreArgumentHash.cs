using System.Globalization;
using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexFlow.QueryRuntime.Experimental;

public static class QreArgumentHash
{
    public static string Compute(IReadOnlyDictionary<string, object?> arguments)
    {
        var normalized = NormalizeObject(arguments);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string NormalizeObject(IReadOnlyDictionary<string, object?> arguments)
    {
        var pairs = arguments
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => $"\"{Escape(pair.Key)}\":{NormalizeValue(pair.Value)}");
        return "{" + string.Join(",", pairs) + "}";
    }

    private static string NormalizeValue(object? value)
        => value switch
        {
            null => "null",
            bool boolean => boolean ? "true" : "false",
            int or long or double or float or decimal => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
            string text => $"\"{Escape(text)}\"",
            JsonElement element => NormalizeJsonElement(element),
            IReadOnlyDictionary<string, object?> dictionary => NormalizeObject(dictionary),
            IDictionary dictionary => NormalizeDictionary(dictionary),
            IEnumerable enumerable => NormalizeEnumerable(enumerable),
            _ => $"\"{Escape(value.ToString() ?? string.Empty)}\""
        };

    private static string NormalizeDictionary(IDictionary dictionary)
    {
        var pairs = dictionary.Keys
            .Cast<object>()
            .OrderBy(static key => Convert.ToString(key, CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .Select(key =>
            {
                var name = Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty;
                return $"\"{Escape(name)}\":{NormalizeValue(dictionary[key])}";
            });
        return "{" + string.Join(",", pairs) + "}";
    }

    private static string NormalizeEnumerable(IEnumerable enumerable)
        => "[" + string.Join(",", enumerable.Cast<object?>().Select(NormalizeValue)) + "]";

    private static string NormalizeJsonElement(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(
                ",",
                element.EnumerateObject()
                    .OrderBy(static property => property.Name, StringComparer.Ordinal)
                    .Select(static property => $"\"{Escape(property.Name)}\":{NormalizeJsonElement(property.Value)}")) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(NormalizeJsonElement)) + "]",
            JsonValueKind.String => $"\"{Escape(element.GetString() ?? string.Empty)}\"",
            JsonValueKind.Number => NormalizeJsonNumber(element.GetRawText()),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => "null"
        };

    private static string NormalizeJsonNumber(string raw)
        => decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("G29", CultureInfo.InvariantCulture)
            : raw;

    private static string Escape(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
