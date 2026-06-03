using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace CodexFlow.Core.Agents;

public static class ToolArgumentNormalizer
{
    private static readonly HashSet<string> ContainerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "args",
        "arguments",
        "params",
        "parameters",
        "input_params"
    };

    private static readonly string[] LooseScalarStringArgumentMarkers =
    [
        "path",
        "dir",
        "root",
        "file"
    ];

    public static void NormalizeInPlace(Dictionary<string, object?> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            return;
        }

        FlattenContainerArguments(args);

        // BUG-001 fix: Normalize keys and values consistent with CodexController.NormalizeToolArguments
        foreach (var key in args.Keys.ToList())
        {
            if (key is null)
            {
                continue;
            }

            var trimmedKey = key.Trim();
            if (string.IsNullOrWhiteSpace(trimmedKey))
            {
                args.Remove(key);
                continue;
            }

            var normalizedValue = NormalizeToolArgumentValue(trimmedKey, args[key]);
            if (!string.Equals(trimmedKey, key, StringComparison.Ordinal))
            {
                args.Remove(key);
            }

            args[trimmedKey] = normalizedValue;
        }

        AliasIfMissing(args, "path", "file_path", "filePath", "filepath");
    }

    /// <summary>
    /// Normalize a single tool argument value: trim strings, convert empty to null,
    /// and parse line-number-like string values to int (e.g., "start_line":"10" → 10).
    /// Extracted from CodexController.NormalizeToolArgumentValue for shared use.
    /// </summary>
    public static object? NormalizeToolArgumentValue(string key, object? rawValue)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (rawValue is JValue jv)
        {
            rawValue = jv.Value;
        }

        if (rawValue is string s)
        {
            var trimmed = s.Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            if (LooksLikeLineNumberArgument(key) && int.TryParse(trimmed, out var parsedInt))
            {
                return parsedInt;
            }

            return trimmed;
        }

        var normalized = NormalizeValue(rawValue, preserveSingleItemLists: LooksLikeStructuredArrayArgument(key));
        if (LooksLikeLooseStringScalarArgument(key))
        {
            return CoerceLooseStringScalarValue(normalized);
        }

        return normalized;
    }

    private static bool LooksLikeLineNumberArgument(string key)
    {
        // Exact matches for known line-number parameters
        if (key.Equals("start_line", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("end_line", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("startLine", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("endLine", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("window_start_line", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("window_line_count", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("max_results", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Broad heuristic: any key containing "line", "start", or "end" is likely a numeric arg.
        // This covers edge cases from LLM output like "line_number", "start_offset", etc.
        return key.Contains("line", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("start", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("end", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("count", StringComparison.OrdinalIgnoreCase);
    }

    public static string? CoerceLooseStringScalarValue(object? rawValue)
    {
        var normalized = NormalizeValue(rawValue);
        return CoerceLooseStringScalarValueCore(normalized);
    }

    private static bool LooksLikeLooseStringScalarArgument(string key)
    {
        return LooseScalarStringArgumentMarkers.Any(marker =>
            key.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeStructuredArrayArgument(string key)
        => key.Equals("operations", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("newLines", StringComparison.OrdinalIgnoreCase) ||
           key.Equals("new_lines", StringComparison.OrdinalIgnoreCase);

    public static Dictionary<string, object?> NormalizeCopy(IDictionary<string, object?>? args)
    {
        var normalized = args != null
            ? new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        NormalizeInPlace(normalized);
        return normalized;
    }

    private static void AliasIfMissing(
        Dictionary<string, object?> args,
        string canonicalKey,
        params string[] aliases)
    {
        if (args.ContainsKey(canonicalKey))
        {
            return;
        }

        foreach (var alias in aliases)
        {
            if (args.TryGetValue(alias, out var value) && value != null)
            {
                args[canonicalKey] = value;
                return;
            }
        }
    }

    private static void FlattenContainerArguments(Dictionary<string, object?> args)
    {
        if (args.Count == 1)
        {
            var firstKey = args.Keys.First();
            if (ContainerKeys.Contains(firstKey) &&
                TryConvertToDictionary(args[firstKey], out var flattened))
            {
                args.Clear();
                foreach (var kv in flattened)
                {
                    args[kv.Key] = kv.Value;
                }
                return;
            }
        }

        foreach (var containerKey in args.Keys.Where(ContainerKeys.Contains).ToList())
        {
            if (!TryConvertToDictionary(args[containerKey], out var flattened))
            {
                continue;
            }

            foreach (var kv in flattened)
            {
                args[kv.Key] = kv.Value;
            }
        }
    }

    private static bool TryConvertToDictionary(object? rawValue, out Dictionary<string, object?> dictionary)
    {
        switch (rawValue)
        {
            case JObject jobj:
                dictionary = JObjectToDictionary(jobj);
                return true;
            case IDictionary<string, object?> typedDict:
                dictionary = new Dictionary<string, object?>(typedDict, StringComparer.OrdinalIgnoreCase);
                return true;
            case JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Object:
                dictionary = jsonElement.EnumerateObject()
                    .ToDictionary(
                        property => property.Name,
                        property => NormalizeJsonElement(property.Value),
                        StringComparer.OrdinalIgnoreCase);
                return true;
            default:
                dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                return false;
        }
    }

    private static object? NormalizeValue(object? rawValue, bool preserveSingleItemLists = false)
    {
        return rawValue switch
        {
            null => null,
            JValue jValue => jValue.Value,
            JObject jObject => NormalizeDictionary(
                JObjectToDictionary(jObject),
                preserveSingleItemLists),
            JArray jArray => NormalizeList(jArray.Select(token => NormalizeJToken(token, preserveSingleItemLists)).ToList(), preserveSingleItemLists),
            JsonElement jsonElement => NormalizeJsonElement(jsonElement, preserveSingleItemLists),
            IList<object> list => NormalizeList(list.Select(value => NormalizeValue(value, preserveSingleItemLists)).ToList(), preserveSingleItemLists),
            _ => rawValue
        };
    }

    private static Dictionary<string, object?> NormalizeDictionary(
        Dictionary<string, object?> dictionary,
        bool preserveSingleItemLists)
    {
        var normalized = new Dictionary<string, object?>(dictionary, StringComparer.OrdinalIgnoreCase);
        foreach (var key in normalized.Keys.ToList())
        {
            normalized[key] = NormalizeValue(
                normalized[key],
                preserveSingleItemLists || LooksLikeStructuredArrayArgument(key));
        }

        return normalized;
    }

    private static string? CoerceLooseStringScalarValueCore(object? rawValue)
    {
        switch (rawValue)
        {
            case null:
                return null;
            case IDictionary<string, object?>:
                return null;
            case string s:
            {
                var trimmed = s.Trim();
                return trimmed.Length == 0 ? null : trimmed;
            }
            case IEnumerable<object?> sequence when rawValue is not string:
            {
                foreach (var item in sequence)
                {
                    var candidate = CoerceLooseStringScalarValueCore(item);
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        return candidate;
                    }
                }

                return null;
            }
            default:
                return null;
        }
    }

    private static object? NormalizeJToken(JToken token, bool preserveSingleItemLists = false)
    {
        return token switch
        {
            JValue value => value.Value,
            JObject obj => NormalizeDictionary(
                JObjectToDictionary(obj),
                preserveSingleItemLists),
            JArray arr => NormalizeList(arr.Select(child => NormalizeJToken(child, preserveSingleItemLists)).ToList(), preserveSingleItemLists),
            _ => token.ToString()
        };
    }

    private static Dictionary<string, object?> JObjectToDictionary(JObject obj)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in obj.Properties())
        {
            dictionary[property.Name] = NormalizeJToken(property.Value);
        }

        return dictionary;
    }

    private static object? NormalizeJsonElement(JsonElement jsonElement, bool preserveSingleItemLists = false)
    {
        return jsonElement.ValueKind switch
        {
            JsonValueKind.Object => jsonElement.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => NormalizeJsonElement(
                        property.Value,
                        preserveSingleItemLists || LooksLikeStructuredArrayArgument(property.Name)),
                    StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => NormalizeList(
                jsonElement.EnumerateArray().Select(child => NormalizeJsonElement(child, preserveSingleItemLists)).ToList(),
                preserveSingleItemLists),
            JsonValueKind.String => jsonElement.GetString(),
            JsonValueKind.Number => NormalizeJsonNumber(jsonElement),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => jsonElement.ToString()
        };
    }

    private static object NormalizeJsonNumber(JsonElement jsonElement)
    {
        if (jsonElement.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (jsonElement.TryGetInt64(out var longValue))
        {
            return longValue;
        }

        if (jsonElement.TryGetDecimal(out var decimalValue))
        {
            return decimalValue;
        }

        return jsonElement.GetDouble();
    }

    private static object? NormalizeList(List<object?> values, bool preserveSingleItemLists = false)
    {
        return !preserveSingleItemLists && values.Count == 1 ? values[0] : values;
    }
}
