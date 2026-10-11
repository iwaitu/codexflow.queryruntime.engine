using System.Text.Json;
using CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.QueryRuntime.Engine.V2;

/// <summary>
/// Raised when a Runtime request is structurally invalid before a Turn starts.
/// It derives from <see cref="ArgumentException"/> so existing request
/// validation handlers keep working, and carries a typed error code.
/// </summary>
public sealed class RuntimeRequestValidationException(RuntimeError error, string? paramName = null)
    : ArgumentException(error.Message, paramName)
{
    public RuntimeError Error { get; } = error;
}

/// <summary>
/// Engine-owned response format decisions. The Step request records the format
/// actually in effect, so audit and replay observe the real request.
/// </summary>
internal static class RuntimeResponseFormats
{
    public const string ConflictCode = "response_format_conflict";

    public const string InvalidCode = "response_format_invalid";

    public static void Validate(RuntimeModelParameters parameters, string paramName)
    {
        if (parameters.ResponseFormat is not { } format)
        {
            return;
        }
        if (parameters.RequireJsonObject)
        {
            throw Invalid(
                ConflictCode,
                "RequireJsonObject and ResponseFormat cannot both be set; use ResponseFormat with Kind=JsonObject.",
                paramName);
        }
        if (!Enum.IsDefined(format.Kind) || !Enum.IsDefined(format.Scope))
        {
            throw Invalid(InvalidCode, "The response format kind or scope is not supported.", paramName);
        }
        if (format.Kind != RuntimeResponseFormatKind.JsonSchema)
        {
            if (format.Schema != null)
            {
                throw Invalid(InvalidCode, "A response schema is only valid with Kind=JsonSchema.", paramName);
            }
            return;
        }
        if (format.Schema is not { } schema)
        {
            throw Invalid(InvalidCode, "Kind=JsonSchema requires a schema.", paramName);
        }
        if (format.SchemaName != null && string.IsNullOrWhiteSpace(format.SchemaName))
        {
            throw Invalid(InvalidCode, "The response schema name cannot be blank.", paramName);
        }
        try
        {
            ValidateSchema(schema);
        }
        catch (ArgumentException ex)
        {
            throw Invalid(InvalidCode, $"Invalid response schema: {ex.Message}", paramName);
        }
    }

    /// <summary>
    /// Returns the format in effect for one Step: a ToolFreeStepsOnly format is
    /// withheld from Steps that expose at least one tool.
    /// </summary>
    public static RuntimeResponseFormat? ResolveForStep(RuntimeResponseFormat? format, int exposedToolCount)
        => format is { Scope: RuntimeResponseFormatScope.ToolFreeStepsOnly } && exposedToolCount > 0
            ? null
            : format;

    public static RuntimeResponseFormat Snapshot(RuntimeResponseFormat format)
        => format.Schema is { } schema ? format with { Schema = schema.Clone() } : format;

    public static string KindName(RuntimeResponseFormatKind kind)
        => kind switch
        {
            RuntimeResponseFormatKind.Text => "text",
            RuntimeResponseFormatKind.JsonObject => "json_object",
            RuntimeResponseFormatKind.JsonSchema => "json_schema",
            _ => "unknown"
        };

    /// <summary>
    /// Basic structural validation at the same depth as tool input schemas,
    /// extended with the constructs structured output schemas commonly use
    /// (type unions, $defs, anyOf/oneOf/allOf, schema-valued additionalProperties).
    /// </summary>
    private static void ValidateSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            !string.Equals(type.GetString(), "object", StringComparison.Ordinal))
        {
            throw new ArgumentException("The root must be a JSON Schema object with type=object.");
        }
        ValidateNode(schema, "$", depth: 0);
    }

    private static void ValidateNode(JsonElement schema, string path, int depth)
    {
        if (depth > 64)
        {
            throw new ArgumentException($"Schema at '{path}' is nested too deeply.");
        }
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return;
        }
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"Schema node '{path}' must be an object.");
        }
        if (schema.TryGetProperty("type", out var type) &&
            type.ValueKind != JsonValueKind.String &&
            (type.ValueKind != JsonValueKind.Array ||
             type.EnumerateArray().Any(static value => value.ValueKind != JsonValueKind.String)))
        {
            throw new ArgumentException($"Schema type at '{path}' must be a string or string array.");
        }
        foreach (var keyword in (ReadOnlySpan<string>)["properties", "$defs", "definitions"])
        {
            if (!schema.TryGetProperty(keyword, out var map))
            {
                continue;
            }
            if (map.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException($"Schema {keyword} at '{path}' must be an object.");
            }
            foreach (var property in map.EnumerateObject())
            {
                ValidateNode(property.Value, $"{path}.{property.Name}", depth + 1);
            }
        }
        foreach (var keyword in (ReadOnlySpan<string>)["items", "additionalProperties", "not"])
        {
            if (schema.TryGetProperty(keyword, out var child))
            {
                ValidateNode(child, $"{path}.{keyword}", depth + 1);
            }
        }
        foreach (var keyword in (ReadOnlySpan<string>)["anyOf", "oneOf", "allOf", "prefixItems"])
        {
            if (!schema.TryGetProperty(keyword, out var list))
            {
                continue;
            }
            if (list.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException($"Schema {keyword} at '{path}' must be an array.");
            }
            var index = 0;
            foreach (var child in list.EnumerateArray())
            {
                ValidateNode(child, $"{path}.{keyword}[{index++}]", depth + 1);
            }
        }
        if (schema.TryGetProperty("required", out var required) &&
            (required.ValueKind != JsonValueKind.Array ||
             required.EnumerateArray().Any(static value => value.ValueKind != JsonValueKind.String)))
        {
            throw new ArgumentException($"Schema required at '{path}' must be a string array.");
        }
    }

    private static RuntimeRequestValidationException Invalid(string code, string message, string paramName)
        => new(new RuntimeError(RuntimeErrorCategory.RuntimeInvariantViolation, code, message), paramName);
}
