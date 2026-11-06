using System.Text.Json;

namespace CodexFlow.QueryRuntime.Models;

/// <summary>
/// Structural check for the OpenAI strict function-schema subset: the root is an
/// object, and every object schema sets <c>additionalProperties: false</c> and lists
/// all of its properties in <c>required</c>. Providers reject or ignore schemas that
/// do not satisfy this, so the adapter only requests strict for compatible tools.
/// </summary>
internal static class RuntimeToolStrictSchema
{
    private const int MaxDepth = 64;

    /// <summary>True when <paramref name="schema"/> can be sent with <c>strict: true</c>.</summary>
    public static bool IsCompatible(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Object &&
           IsObjectType(schema) &&
           Check(schema, depth: 0);

    private static bool Check(JsonElement node, int depth)
    {
        if (depth > MaxDepth)
        {
            return false;
        }
        if (node.ValueKind == JsonValueKind.True || node.ValueKind == JsonValueKind.False)
        {
            // A boolean sub-schema carries no structure to enforce.
            return true;
        }
        if (node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (IsObjectType(node) || node.TryGetProperty("properties", out _))
        {
            if (!node.TryGetProperty("additionalProperties", out var additional) ||
                additional.ValueKind != JsonValueKind.False)
            {
                return false;
            }
            if (!AllPropertiesRequired(node))
            {
                return false;
            }
        }

        if (node.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            foreach (var property in properties.EnumerateObject())
            {
                if (!Check(property.Value, depth + 1))
                {
                    return false;
                }
            }
        }
        if (node.TryGetProperty("items", out var items) && !Check(items, depth + 1))
        {
            return false;
        }
        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf", "prefixItems" })
        {
            if (node.TryGetProperty(keyword, out var branches))
            {
                if (branches.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }
                foreach (var branch in branches.EnumerateArray())
                {
                    if (!Check(branch, depth + 1))
                    {
                        return false;
                    }
                }
            }
        }
        foreach (var keyword in new[] { "$defs", "definitions" })
        {
            if (node.TryGetProperty(keyword, out var defs))
            {
                if (defs.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }
                foreach (var definition in defs.EnumerateObject())
                {
                    if (!Check(definition.Value, depth + 1))
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    private static bool AllPropertiesRequired(JsonElement node)
    {
        if (!node.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            // No declared properties: nothing can be left optional.
            return true;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (node.TryGetProperty("required", out var requiredList))
        {
            if (requiredList.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            foreach (var name in requiredList.EnumerateArray())
            {
                if (name.ValueKind == JsonValueKind.String)
                {
                    required.Add(name.GetString()!);
                }
            }
        }
        foreach (var property in properties.EnumerateObject())
        {
            if (!required.Contains(property.Name))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsObjectType(JsonElement node)
    {
        if (!node.TryGetProperty("type", out var type))
        {
            return false;
        }
        return type.ValueKind switch
        {
            JsonValueKind.String => type.GetString() == "object",
            JsonValueKind.Array => type.EnumerateArray().Any(static t => t.ValueKind == JsonValueKind.String && t.GetString() == "object"),
            _ => false
        };
    }
}
