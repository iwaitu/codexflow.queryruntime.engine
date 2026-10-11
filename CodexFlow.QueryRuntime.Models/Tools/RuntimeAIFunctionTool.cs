using System.Collections.Frozen;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Models.Tools;

/// <summary>
/// Runtime metadata attached to an <see cref="AIFunction"/> when it is registered
/// as an Engine v2 tool. Defaults describe a read-only, idempotent, parallel-safe
/// tool that needs no sandbox.
/// </summary>
public sealed record RuntimeAIFunctionToolOptions
{
    public string Version { get; init; } = "1.0.0";

    public RuntimeToolSideEffect SideEffect { get; init; } = RuntimeToolSideEffect.ReadOnly;

    public RuntimeToolIdempotency Idempotency { get; init; } = RuntimeToolIdempotency.Idempotent;

    public RuntimeToolConcurrency Concurrency { get; init; } = RuntimeToolConcurrency.ParallelSafe;

    public RuntimeToolLimits Limits { get; init; } = RuntimeToolLimits.Default;

    public IReadOnlySet<string> Capabilities { get; init; } = FrozenSet<string>.Empty;

    public RuntimeSandboxRequirements Sandbox { get; init; } = RuntimeSandboxRequirements.None;

    /// <summary>Model-visible tool name. Defaults to <see cref="AIFunction"/>.Name.</summary>
    public string? CanonicalName { get; init; }

    /// <summary>
    /// Model-visible description. Defaults to <see cref="AIFunction"/>.Description;
    /// the runtime registry rejects tools whose description is empty.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>Error code reported when the function throws.</summary>
    public string FailureCode { get; init; } = "ai_function_failed";

    /// <summary>
    /// Optional predicate over the rendered result text that marks the observation
    /// as truncated. Null means the adapter never reports truncation.
    /// </summary>
    public Func<string, bool>? TruncationDetector { get; init; }
}

/// <summary>
/// Adapts a host <see cref="AIFunction"/> to <see cref="IRuntimeTool"/>. Exceptions
/// other than caller cancellation become model-visible tool failures so the turn can
/// continue; policy, approval and audit stay owned by the runtime pipeline.
/// </summary>
public sealed class RuntimeAIFunctionTool : IRuntimeTool
{
    private static readonly JsonWriterOptions RelaxedWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AIFunction _function;
    private readonly RuntimeAIFunctionToolOptions _options;

    public RuntimeAIFunctionTool(AIFunction function, RuntimeAIFunctionToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(function);
        _function = function;
        _options = options ?? new RuntimeAIFunctionToolOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.Version, nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.FailureCode, nameof(options));
        ArgumentNullException.ThrowIfNull(_options.Limits, nameof(options));
        ArgumentNullException.ThrowIfNull(_options.Sandbox, nameof(options));
        ArgumentNullException.ThrowIfNull(_options.Capabilities, nameof(options));
        var name = _options.CanonicalName ?? function.Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(function));
        Definition = new RuntimeToolDefinition(
            new RuntimeToolDescriptor(
                name,
                _options.Version,
                _options.Description ?? function.Description,
                function.JsonSchema.Clone(),
                _options.SideEffect,
                _options.Idempotency),
            new HashSet<string>(_options.Capabilities, StringComparer.Ordinal),
            _options.Concurrency,
            _options.Sandbox,
            _options.Limits);
    }

    public RuntimeToolDefinition Definition { get; }

    public static IReadOnlyList<IRuntimeTool> FromFunctions(
        IEnumerable<AIFunction> functions,
        RuntimeAIFunctionToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(functions);
        var tools = functions.Select(function => (IRuntimeTool)new RuntimeAIFunctionTool(function, options)).ToArray();
        if (options?.CanonicalName != null && tools.Length > 1)
        {
            throw new ArgumentException(
                "CanonicalName cannot be shared by multiple functions.",
                nameof(options));
        }
        return Array.AsReadOnly(tools);
    }

    public async ValueTask<RuntimeToolResult> InvokeAsync(
        RuntimeToolInvocation invocation,
        RuntimeToolExecutionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        try
        {
            var value = await _function.InvokeAsync(
                new AIFunctionArguments(ToObjectDictionary(invocation.NormalizedArguments)),
                ct).ConfigureAwait(false);
            var text = FormatResult(value, _function.JsonSerializerOptions);
            var truncated = _options.TruncationDetector?.Invoke(text) ?? false;
            return new RuntimeToolResult(
                invocation.OriginalCall.InvocationId,
                text,
                true,
                Details: new RuntimeToolResultDetails(
                    RuntimeToolOutcome.Succeeded,
                    StandardOutput: text,
                    Truncated: truncated));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RuntimeToolResult(
                invocation.OriginalCall.InvocationId,
                null,
                false,
                new RuntimeError(
                    RuntimeErrorCategory.ToolFailed,
                    _options.FailureCode,
                    ex.Message),
                Details: new RuntimeToolResultDetails(
                    RuntimeToolOutcome.Failed,
                    StandardError: ex.Message));
        }
    }

    internal static string FormatResult(object? value, JsonSerializerOptions serializerOptions)
        => value switch
        {
            null => string.Empty,
            string text => text,
            JsonElement element => FormatElement(element),
            JsonValue node when node.GetValueKind() == JsonValueKind.String => node.GetValue<string>(),
            JsonNode node => WriteRelaxed(writer => node.WriteTo(writer)),
            _ => FormatElement(JsonSerializer.SerializeToElement(
                value,
                serializerOptions.GetTypeInfo(value.GetType())))
        };

    private static string FormatElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return string.Empty;
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;
        }
        var raw = element.GetRawText();
        // Raw text is preserved verbatim unless it carries \uXXXX escapes, which are
        // rewritten so non-ASCII text (for example Chinese) reaches the model unescaped.
        return raw.Contains("\\u", StringComparison.Ordinal)
            ? WriteRelaxed(element.WriteTo)
            : raw;
    }

    private static string WriteRelaxed(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, RelaxedWriterOptions))
        {
            write(writer);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    internal static Dictionary<string, object?> ToObjectDictionary(JsonElement arguments)
        => arguments.ValueKind != JsonValueKind.Object
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : arguments.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => ToObject(property.Value),
                StringComparer.Ordinal);

    private static object? ToObject(JsonElement value)
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
            _ => JsonNode.Parse(value.GetRawText())
        };
}
