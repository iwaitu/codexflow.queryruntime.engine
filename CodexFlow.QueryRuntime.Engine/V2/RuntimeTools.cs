using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.QueryRuntime.Engine.V2;

public enum RuntimeToolConcurrency
{
    Serial = 0,
    ParallelSafe = 1,
    ExclusiveWorkspace = 2
}

public enum RuntimeSandboxKind
{
    None = 0,
    LocalProcess = 1,
    Docker = 2
}

public enum RuntimeNetworkMode
{
    Deny = 0,
    Allow = 1
}

public enum RuntimeWorkspaceMountMode
{
    ReadOnly = 0,
    ReadWrite = 1
}

public sealed record RuntimeSandboxRequirements(
    RuntimeSandboxKind Kind,
    RuntimeNetworkMode Network,
    RuntimeWorkspaceMountMode WorkspaceMount)
{
    public static RuntimeSandboxRequirements None { get; } = new(
        RuntimeSandboxKind.None,
        RuntimeNetworkMode.Deny,
        RuntimeWorkspaceMountMode.ReadOnly);
}

public sealed record RuntimeToolLimits(
    TimeSpan Timeout,
    int MaxOutputBytes,
    long? MemoryBytes = null,
    double? CpuCount = null)
{
    public static RuntimeToolLimits Default { get; } = new(TimeSpan.FromSeconds(60), 1024 * 1024);
}

public sealed record RuntimeToolDefinition(
    RuntimeToolDescriptor Descriptor,
    IReadOnlySet<string> Capabilities,
    RuntimeToolConcurrency Concurrency,
    RuntimeSandboxRequirements Sandbox,
    RuntimeToolLimits Limits);

public interface IRuntimeTool
{
    RuntimeToolDefinition Definition { get; }

    ValueTask<RuntimeToolResult> InvokeAsync(
        RuntimeToolInvocation invocation,
        RuntimeToolExecutionContext context,
        CancellationToken ct);
}

public sealed record RuntimeToolInvocation(
    RuntimeToolCall OriginalCall,
    JsonElement NormalizedArguments,
    ResolvedExecutionPlan Plan);

public sealed record RuntimeSandboxCommand(
    IReadOnlyList<string> Command,
    string WorkingDirectory,
    string? WorkspaceRoot,
    IReadOnlyDictionary<string, string> Environment,
    RuntimeToolLimits Limits,
    RuntimeNetworkMode Network,
    RuntimeWorkspaceMountMode WorkspaceMount);

public sealed record RuntimeSandboxResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    long DurationMs,
    bool Truncated = false,
    string? WorkspaceChangeEvidence = null);

public interface IRuntimeSandbox
{
    RuntimeSandboxKind Kind { get; }

    ValueTask<RuntimeSandboxResult> ExecuteAsync(RuntimeSandboxCommand command, CancellationToken ct);
}

public interface IRuntimeSandboxRouter
{
    IRuntimeSandbox? Resolve(RuntimeSandboxRequirements requirements);
}

public sealed class RuntimeSandboxRouter : IRuntimeSandboxRouter
{
    private readonly IReadOnlyDictionary<RuntimeSandboxKind, IRuntimeSandbox> _sandboxes;

    public RuntimeSandboxRouter(IEnumerable<IRuntimeSandbox> sandboxes)
    {
        ArgumentNullException.ThrowIfNull(sandboxes);
        var resolved = new Dictionary<RuntimeSandboxKind, IRuntimeSandbox>();
        foreach (var sandbox in sandboxes)
        {
            ArgumentNullException.ThrowIfNull(sandbox);
            if (sandbox.Kind == RuntimeSandboxKind.None || !resolved.TryAdd(sandbox.Kind, sandbox))
            {
                throw new ArgumentException($"Duplicate or invalid sandbox kind '{sandbox.Kind}'.", nameof(sandboxes));
            }
        }
        _sandboxes = resolved;
    }

    public IRuntimeSandbox? Resolve(RuntimeSandboxRequirements requirements)
        => requirements.Kind == RuntimeSandboxKind.None
            ? null
            : _sandboxes.GetValueOrDefault(requirements.Kind);
}

public interface IRuntimeToolRegistry
{
    IReadOnlyList<RuntimeToolDescriptor> Descriptors { get; }

    bool TryGet(string canonicalName, out IRuntimeTool? tool);
}

public sealed class RuntimeToolRegistry : IRuntimeToolRegistry
{
    private static readonly Regex CanonicalNamePattern = new(
        "^[a-z][a-z0-9_.-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex VersionPattern = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly IReadOnlyDictionary<string, IRuntimeTool> _tools;

    public RuntimeToolRegistry(IEnumerable<IRuntimeTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var resolved = new Dictionary<string, IRuntimeTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            ValidateDefinition(tool.Definition);
            var definition = tool.Definition with
            {
                Descriptor = SnapshotDescriptor(tool.Definition.Descriptor),
                Capabilities = tool.Definition.Capabilities.ToFrozenSet(StringComparer.Ordinal)
            };
            var name = definition.Descriptor.CanonicalName;
            if (!resolved.TryAdd(name, new FrozenRuntimeTool(tool, definition)))
            {
                throw new ArgumentException($"Tool canonical name collision: '{name}'.", nameof(tools));
            }
        }
        _tools = resolved;
        Descriptors = Array.AsReadOnly(resolved.Values
            .Select(static tool => SnapshotDescriptor(tool.Definition.Descriptor))
            .OrderBy(static descriptor => descriptor.CanonicalName, StringComparer.Ordinal)
            .ToArray());
    }

    public IReadOnlyList<RuntimeToolDescriptor> Descriptors { get; }

    public bool TryGet(string canonicalName, out IRuntimeTool? tool)
    {
        tool = null;
        return !string.IsNullOrWhiteSpace(canonicalName) &&
               _tools.TryGetValue(canonicalName.Trim(), out tool);
    }

    private static void ValidateDefinition(RuntimeToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Descriptor);
        ArgumentNullException.ThrowIfNull(definition.Capabilities);
        ArgumentNullException.ThrowIfNull(definition.Sandbox);
        ArgumentNullException.ThrowIfNull(definition.Limits);
        var descriptor = definition.Descriptor;
        if (!CanonicalNamePattern.IsMatch(descriptor.CanonicalName))
        {
            throw new ArgumentException($"Invalid canonical tool name '{descriptor.CanonicalName}'.");
        }
        if (!VersionPattern.IsMatch(descriptor.Version))
        {
            throw new ArgumentException($"Invalid semantic tool version '{descriptor.Version}'.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Description);
        RuntimeToolArgumentNormalizer.ValidateSchema(descriptor.InputSchema);
        if (definition.Limits.Timeout <= TimeSpan.Zero || definition.Limits.MaxOutputBytes <= 0)
        {
            throw new ArgumentException("Tool limits must be positive.");
        }
        if (descriptor.SideEffect == RuntimeToolSideEffect.WorkspaceWrite &&
            definition.Sandbox.WorkspaceMount != RuntimeWorkspaceMountMode.ReadWrite)
        {
            throw new ArgumentException("Workspace-writing tools require a read-write workspace plan.");
        }
    }

    private static RuntimeToolDescriptor SnapshotDescriptor(RuntimeToolDescriptor descriptor)
        => descriptor with { InputSchema = descriptor.InputSchema.Clone() };

    private sealed class FrozenRuntimeTool(
        IRuntimeTool inner,
        RuntimeToolDefinition definition) : IRuntimeTool
    {
        public RuntimeToolDefinition Definition { get; } = definition;

        public ValueTask<RuntimeToolResult> InvokeAsync(
            RuntimeToolInvocation invocation,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
            => inner.InvokeAsync(invocation, context, ct);
    }
}

public sealed class RuntimeToolRouter(IRuntimeToolRegistry registry)
{
    private readonly IRuntimeToolRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public bool TryRoute(string canonicalName, out IRuntimeTool? tool)
        => _registry.TryGet(canonicalName, out tool);
}

public static class RuntimeToolArgumentNormalizer
{
    public static RuntimeNormalizedArguments NormalizeAndValidate(
        JsonElement schema,
        JsonElement arguments)
    {
        ValidateSchema(schema);
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new RuntimeToolPreparationException(new RuntimeError(
                RuntimeErrorCategory.MalformedToolArguments,
                "tool_arguments_not_object",
                "Tool arguments must be a JSON object."));
        }

        ValidateValue(schema, arguments, "$");
        var normalizedBytes = Canonicalize(arguments);
        using var document = JsonDocument.Parse(normalizedBytes);
        var normalized = document.RootElement.Clone();
        var digest = Convert.ToHexString(SHA256.HashData(normalizedBytes)).ToLowerInvariant();
        return new RuntimeNormalizedArguments(normalized, digest);
    }

    public static void ValidateSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            !string.Equals(type.GetString(), "object", StringComparison.Ordinal))
        {
            throw new ArgumentException("Tool input schema must be a JSON Schema object with type=object.");
        }
        ValidateSchemaNode(schema, "$");
    }

    private static void ValidateSchemaNode(JsonElement schema, string path)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"Schema node '{path}' must be an object.");
        }
        if (schema.TryGetProperty("type", out var type) && type.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Schema type at '{path}' must be a string.");
        }
        if (schema.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException($"Schema properties at '{path}' must be an object.");
            }
            foreach (var property in properties.EnumerateObject())
            {
                ValidateSchemaNode(property.Value, path + "." + property.Name);
            }
        }
        if (schema.TryGetProperty("items", out var items))
        {
            ValidateSchemaNode(items, path + "[]");
        }
        if (schema.TryGetProperty("required", out var required) &&
            (required.ValueKind != JsonValueKind.Array ||
             required.EnumerateArray().Any(static value => value.ValueKind != JsonValueKind.String)))
        {
            throw new ArgumentException($"Schema required at '{path}' must be a string array.");
        }
        if (schema.TryGetProperty("additionalProperties", out var additional) &&
            additional.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException($"Only boolean additionalProperties is supported at '{path}'.");
        }
    }

    private static void ValidateValue(JsonElement schema, JsonElement value, string path)
    {
        if (schema.TryGetProperty("type", out var typeElement))
        {
            var type = typeElement.GetString();
            var valid = type switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => false
            };
            if (!valid)
            {
                throw Malformed("tool_argument_type_mismatch", $"Argument '{path}' does not match schema type '{type}'.");
            }
        }
        if (schema.TryGetProperty("enum", out var enumValues) &&
            enumValues.ValueKind == JsonValueKind.Array &&
            !enumValues.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
        {
            throw Malformed("tool_argument_enum_mismatch", $"Argument '{path}' is not an allowed enum value.");
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var propertySchemas = schema.TryGetProperty("properties", out var properties)
                ? properties
                : default;
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var requiredName in required.EnumerateArray().Select(static item => item.GetString()!))
                {
                    if (!value.TryGetProperty(requiredName, out _))
                    {
                        throw Malformed("tool_argument_required_missing", $"Required argument '{path}.{requiredName}' is missing.");
                    }
                }
            }
            var allowAdditional = !schema.TryGetProperty("additionalProperties", out var additional) || additional.GetBoolean();
            foreach (var property in value.EnumerateObject())
            {
                if (propertySchemas.ValueKind == JsonValueKind.Object && propertySchemas.TryGetProperty(property.Name, out var propertySchema))
                {
                    ValidateValue(propertySchema, property.Value, path + "." + property.Name);
                }
                else if (!allowAdditional)
                {
                    throw Malformed("tool_argument_unknown_property", $"Argument '{path}.{property.Name}' is not allowed.");
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateValue(itemSchema, item, $"{path}[{index++}]");
            }
        }
    }

    private static byte[] Canonicalize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, value);
        }
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
            {
                WriteCanonical(writer, item);
            }
            writer.WriteEndArray();
            return;
        }
        value.WriteTo(writer);
    }

    private static RuntimeToolPreparationException Malformed(string code, string message)
        => new(new RuntimeError(RuntimeErrorCategory.MalformedToolArguments, code, message));
}

public sealed record RuntimeNormalizedArguments(JsonElement Value, string Sha256Digest);

public enum RuntimeToolPolicyDecisionKind
{
    Allow = 0,
    Deny = 1,
    RequireApproval = 2
}

public sealed record RuntimeToolPolicyDecision(
    RuntimeToolPolicyDecisionKind Kind,
    string Reason,
    IReadOnlySet<string>? EffectiveCapabilities = null,
    RuntimeSandboxRequirements? Sandbox = null,
    TimeSpan? ApprovalLifetime = null)
{
    public static RuntimeToolPolicyDecision Allow(string reason = "allowed")
        => new(RuntimeToolPolicyDecisionKind.Allow, reason);

    public static RuntimeToolPolicyDecision Deny(string reason)
        => new(RuntimeToolPolicyDecisionKind.Deny, reason);

    public static RuntimeToolPolicyDecision RequireApproval(string reason, TimeSpan? lifetime = null)
        => new(RuntimeToolPolicyDecisionKind.RequireApproval, reason, ApprovalLifetime: lifetime);
}

public sealed record RuntimeToolPolicyContext(
    RuntimeToolDefinition Tool,
    RuntimeToolCall Call,
    RuntimeNormalizedArguments Arguments,
    RuntimeToolExecutionContext Execution);

public interface IRuntimeToolPolicyEvaluator
{
    ValueTask<RuntimeToolPolicyDecision> EvaluateAsync(RuntimeToolPolicyContext context, CancellationToken ct);
}

public sealed class RuntimeToolPolicyChain(IEnumerable<IRuntimeToolPolicyEvaluator> evaluators)
    : IRuntimeToolPolicyEvaluator
{
    private readonly IReadOnlyList<IRuntimeToolPolicyEvaluator> _evaluators =
        (evaluators ?? throw new ArgumentNullException(nameof(evaluators))).ToArray();

    public async ValueTask<RuntimeToolPolicyDecision> EvaluateAsync(
        RuntimeToolPolicyContext context,
        CancellationToken ct)
    {
        var effectiveCapabilities = new HashSet<string>(context.Tool.Capabilities, StringComparer.Ordinal);
        var sandbox = context.Tool.Sandbox;
        var approval = false;
        var approvalReason = string.Empty;
        TimeSpan? approvalLifetime = null;
        foreach (var evaluator in _evaluators)
        {
            var decision = await evaluator.EvaluateAsync(context, ct).ConfigureAwait(false) ??
                           RuntimeToolPolicyDecision.Deny("A policy evaluator returned no decision.");
            if (decision.Kind == RuntimeToolPolicyDecisionKind.Deny)
            {
                return decision;
            }
            if (decision.EffectiveCapabilities != null)
            {
                effectiveCapabilities.IntersectWith(decision.EffectiveCapabilities);
            }
            if (decision.Sandbox != null)
            {
                sandbox = decision.Sandbox;
            }
            if (decision.Kind == RuntimeToolPolicyDecisionKind.RequireApproval)
            {
                approval = true;
                approvalReason = decision.Reason;
                approvalLifetime = decision.ApprovalLifetime;
            }
        }
        return new RuntimeToolPolicyDecision(
            approval ? RuntimeToolPolicyDecisionKind.RequireApproval : RuntimeToolPolicyDecisionKind.Allow,
            approval ? approvalReason : "All policy evaluators allowed the invocation.",
            effectiveCapabilities,
            sandbox,
            approvalLifetime);
    }
}

public sealed class RuntimeAllowToolPolicy : IRuntimeToolPolicyEvaluator
{
    public ValueTask<RuntimeToolPolicyDecision> EvaluateAsync(RuntimeToolPolicyContext context, CancellationToken ct)
        => ValueTask.FromResult(RuntimeToolPolicyDecision.Allow());
}

public sealed record RuntimeApprovalBinding(string Scope, string Nonce, DateTimeOffset ExpiresAt);

public sealed record ResolvedExecutionPlan(
    string AttemptId,
    string ToolCanonicalName,
    string ToolVersion,
    JsonElement NormalizedArguments,
    string NormalizedArgumentsDigest,
    string? WorkspaceIdentity,
    string PolicyVersion,
    string Profile,
    string ExecutionMode,
    IReadOnlySet<string> EffectiveCapabilities,
    RuntimeSandboxRequirements Sandbox,
    RuntimeToolLimits Limits,
    RuntimeToolConcurrency Concurrency,
    RuntimeApprovalBinding? Approval);

public enum RuntimeToolPreparationKind
{
    Ready = 0,
    Denied = 1
}

public sealed record RuntimePreparedToolInvocation(
    RuntimeToolPreparationKind Kind,
    RuntimeToolCall Call,
    IRuntimeTool? Tool,
    ResolvedExecutionPlan? Plan,
    RuntimeToolResult? Observation)
{
    public bool RequiresApproval => Plan?.Approval != null;
}

public interface IRuntimeToolExecutionPipeline
{
    IReadOnlyList<RuntimeToolDescriptor> Descriptors { get; }

    ValueTask<RuntimePreparedToolInvocation> PrepareAsync(
        RuntimeToolCall call,
        RuntimeToolExecutionContext context,
        CancellationToken ct);

    ValueTask<RuntimeToolResult> ExecuteAsync(
        RuntimePreparedToolInvocation prepared,
        RuntimeToolExecutionContext context,
        CancellationToken ct);
}

public sealed class RuntimeToolExecutionPipeline : IRuntimeToolExecutionPipeline
{
    private readonly RuntimeToolRouter _router;
    private readonly IRuntimeToolPolicyEvaluator _policy;
    private readonly IRuntimeSandboxRouter _sandboxes;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _nonceFactory;

    public RuntimeToolExecutionPipeline(
        IRuntimeToolRegistry registry,
        IRuntimeToolPolicyEvaluator policy,
        IRuntimeSandboxRouter? sandboxes = null,
        TimeProvider? timeProvider = null,
        Func<string>? nonceFactory = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _router = new RuntimeToolRouter(registry);
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _sandboxes = sandboxes ?? new RuntimeSandboxRouter([]);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _nonceFactory = nonceFactory ?? (static () =>
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
        Descriptors = registry.Descriptors;
    }

    public IReadOnlyList<RuntimeToolDescriptor> Descriptors { get; }

    public async ValueTask<RuntimePreparedToolInvocation> PrepareAsync(
        RuntimeToolCall call,
        RuntimeToolExecutionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!_router.TryRoute(call.Name, out var tool) || tool == null)
        {
            return Denied(call, RuntimeErrorCategory.UnknownTool, "unknown_tool", $"Unknown tool '{call.Name}'.");
        }

        RuntimeNormalizedArguments normalized;
        try
        {
            normalized = RuntimeToolArgumentNormalizer.NormalizeAndValidate(
                tool.Definition.Descriptor.InputSchema,
                call.Arguments);
        }
        catch (RuntimeToolPreparationException ex)
        {
            return Denied(call, ex.Error.Category, ex.Error.Code, ex.Error.Message);
        }

        RuntimeToolPolicyDecision decision;
        try
        {
            decision = await _policy.EvaluateAsync(
                new RuntimeToolPolicyContext(tool.Definition, call, normalized, context),
                ct).ConfigureAwait(false) ?? RuntimeToolPolicyDecision.Deny("The policy returned no decision.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Denied(call, RuntimeErrorCategory.PolicyDenied, "policy_evaluation_failed", ex.Message);
        }
        if (decision.Kind == RuntimeToolPolicyDecisionKind.Deny)
        {
            return Denied(call, RuntimeErrorCategory.PolicyDenied, "tool_policy_denied", decision.Reason);
        }

        var sandbox = decision.Sandbox ?? tool.Definition.Sandbox;
        if (sandbox.Kind != RuntimeSandboxKind.None && _sandboxes.Resolve(sandbox) == null)
        {
            return Denied(
                call,
                RuntimeErrorCategory.SandboxDenied,
                "sandbox_unavailable",
                $"Sandbox '{sandbox.Kind}' is unavailable for tool '{tool.Definition.Descriptor.CanonicalName}'.");
        }

        var now = _timeProvider.GetUtcNow();
        var attemptId = _nonceFactory();
        RuntimeApprovalBinding? approval = null;
        if (decision.Kind == RuntimeToolPolicyDecisionKind.RequireApproval)
        {
            var lifetime = decision.ApprovalLifetime.GetValueOrDefault(TimeSpan.FromMinutes(5));
            if (lifetime <= TimeSpan.Zero)
            {
                return Denied(call, RuntimeErrorCategory.PolicyDenied, "invalid_approval_lifetime", "Approval lifetime must be positive.");
            }
            approval = new RuntimeApprovalBinding(
                $"{context.Policy.Profile}:{tool.Definition.Descriptor.CanonicalName}:{normalized.Sha256Digest}",
                _nonceFactory(),
                now.Add(lifetime));
        }

        var plan = new ResolvedExecutionPlan(
            attemptId,
            tool.Definition.Descriptor.CanonicalName,
            tool.Definition.Descriptor.Version,
            normalized.Value,
            normalized.Sha256Digest,
            context.Environment.WorkspaceIdentity,
            context.Policy.Version,
            context.Policy.Profile,
            context.Environment.ExecutionMode,
            (decision.EffectiveCapabilities ?? tool.Definition.Capabilities)
                .ToFrozenSet(StringComparer.Ordinal),
            sandbox,
            tool.Definition.Limits,
            tool.Definition.Concurrency,
            approval);
        return new RuntimePreparedToolInvocation(RuntimeToolPreparationKind.Ready, call, tool, plan, null);
    }

    public async ValueTask<RuntimeToolResult> ExecuteAsync(
        RuntimePreparedToolInvocation prepared,
        RuntimeToolExecutionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.Kind != RuntimeToolPreparationKind.Ready || prepared.Tool == null || prepared.Plan == null)
        {
            return prepared.Observation ?? Failure(
                prepared.Call,
                RuntimeErrorCategory.RuntimeInvariantViolation,
                "tool_not_prepared",
                "The invocation was not prepared for execution.");
        }
        var plan = prepared.Plan;
        if (plan.Approval is { } approval && _timeProvider.GetUtcNow() > approval.ExpiresAt)
        {
            return Failure(prepared.Call, RuntimeErrorCategory.ApprovalTimeout, "approval_expired", "Tool approval expired before execution.");
        }
        var sandbox = _sandboxes.Resolve(plan.Sandbox);
        var executionContext = context with { Plan = plan, Sandbox = sandbox };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(plan.Limits.Timeout);
        try
        {
            var result = await prepared.Tool.InvokeAsync(
                new RuntimeToolInvocation(prepared.Call, plan.NormalizedArguments, plan),
                executionContext,
                timeout.Token).ConfigureAwait(false);
            return result == null
                ? Failure(prepared.Call, RuntimeErrorCategory.ToolFailed, "null_tool_result", "The tool returned no observation.")
                : BoundResult(result, plan.Limits.MaxOutputBytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Failure(
                prepared.Call,
                RuntimeErrorCategory.SandboxTimeout,
                "tool_timeout",
                $"Tool execution exceeded {plan.Limits.Timeout}.",
                RuntimeToolOutcome.TimedOut);
        }
        catch (TimeoutException ex)
        {
            return Failure(prepared.Call, RuntimeErrorCategory.SandboxTimeout, "tool_timeout", ex.Message, RuntimeToolOutcome.TimedOut);
        }
        catch (Exception ex)
        {
            return Failure(prepared.Call, RuntimeErrorCategory.ToolFailed, "tool_execution_failed", ex.Message);
        }
    }

    private static RuntimePreparedToolInvocation Denied(
        RuntimeToolCall call,
        RuntimeErrorCategory category,
        string code,
        string message)
        => new(RuntimeToolPreparationKind.Denied, call, null, null, Failure(call, category, code, message, RuntimeToolOutcome.Denied));

    private static RuntimeToolResult Failure(
        RuntimeToolCall call,
        RuntimeErrorCategory category,
        string code,
        string message,
        RuntimeToolOutcome outcome = RuntimeToolOutcome.Failed)
        => new(
            call.InvocationId,
            null,
            false,
            new RuntimeError(category, code, message),
            Details: new RuntimeToolResultDetails(outcome));

    private static RuntimeToolResult BoundResult(RuntimeToolResult result, int maxOutputBytes)
    {
        var text = BoundUtf8(result.Text, maxOutputBytes, out var textTruncated);
        var details = result.Details ?? new RuntimeToolResultDetails(
            result.Success ? RuntimeToolOutcome.Succeeded : RuntimeToolOutcome.Failed);
        var stdout = BoundUtf8(details.StandardOutput, maxOutputBytes, out var stdoutTruncated);
        var stderr = BoundUtf8(details.StandardError, maxOutputBytes, out var stderrTruncated);
        return result with
        {
            Text = text,
            Details = details with
            {
                StandardOutput = stdout,
                StandardError = stderr,
                Truncated = details.Truncated || textTruncated || stdoutTruncated || stderrTruncated
            }
        };
    }

    private static string? BoundUtf8(string? value, int maxBytes, out bool truncated)
    {
        truncated = false;
        if (value == null || Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }
        truncated = true;
        var chars = 0;
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > maxBytes)
            {
                break;
            }
            bytes += runeBytes;
            chars += rune.Utf16SequenceLength;
        }
        return value[..chars];
    }
}

public sealed class RuntimeToolScheduler
{
    private static readonly RuntimeKeyedSemaphore ConcurrencyLocks = new();

    public async Task<IReadOnlyList<RuntimeToolResult>> ExecuteAsync(
        IReadOnlyList<RuntimeScheduledToolInvocation> invocations,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invocations);
        var results = new RuntimeToolResult[invocations.Count];
        for (var index = 0; index < invocations.Count;)
        {
            if (invocations[index].Concurrency != RuntimeToolConcurrency.ParallelSafe)
            {
                results[index] = await ExecuteConstrainedAsync(invocations[index], ct).ConfigureAwait(false);
                index++;
                continue;
            }
            var start = index;
            while (index < invocations.Count && invocations[index].Concurrency == RuntimeToolConcurrency.ParallelSafe)
            {
                index++;
            }
            var tasks = invocations
                .Skip(start)
                .Take(index - start)
                .Select(invocation => invocation.Execute(ct).AsTask())
                .ToArray();
            var batch = await Task.WhenAll(tasks).ConfigureAwait(false);
            Array.Copy(batch, 0, results, start, batch.Length);
        }
        return Array.AsReadOnly(results);
    }

    private static async ValueTask<RuntimeToolResult> ExecuteConstrainedAsync(
        RuntimeScheduledToolInvocation invocation,
        CancellationToken ct)
    {
        var key = invocation.Concurrency switch
        {
            RuntimeToolConcurrency.Serial => "tool:" +
                (string.IsNullOrWhiteSpace(invocation.ConcurrencyKey)
                    ? "default"
                    : invocation.ConcurrencyKey),
            RuntimeToolConcurrency.ExclusiveWorkspace => "workspace:" +
                (string.IsNullOrWhiteSpace(invocation.WorkspaceIdentity)
                    ? "default"
                    : invocation.WorkspaceIdentity),
            _ => null
        };
        if (key == null)
        {
            return await invocation.Execute(ct).ConfigureAwait(false);
        }
        await using var lease = await ConcurrencyLocks.AcquireAsync(key, ct).ConfigureAwait(false);
        return await invocation.Execute(ct).ConfigureAwait(false);
    }

    private sealed class RuntimeKeyedSemaphore
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

        public async ValueTask<IAsyncDisposable> AcquireAsync(string key, CancellationToken ct)
        {
            Entry entry;
            lock (_sync)
            {
                if (!_entries.TryGetValue(key, out entry!))
                {
                    entry = new Entry();
                    _entries.Add(key, entry);
                }
                entry.ReferenceCount++;
            }
            try
            {
                await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
                return new Lease(this, key, entry);
            }
            catch
            {
                ReleaseReference(key, entry, releaseSemaphore: false);
                throw;
            }
        }

        private void ReleaseReference(string key, Entry entry, bool releaseSemaphore)
        {
            if (releaseSemaphore)
            {
                entry.Semaphore.Release();
            }
            lock (_sync)
            {
                entry.ReferenceCount--;
                if (entry.ReferenceCount == 0)
                {
                    _entries.Remove(key);
                    entry.Semaphore.Dispose();
                }
            }
        }

        private sealed class Entry
        {
            public SemaphoreSlim Semaphore { get; } = new(1, 1);

            public int ReferenceCount { get; set; }
        }

        private sealed class Lease(
            RuntimeKeyedSemaphore owner,
            string key,
            Entry entry) : IAsyncDisposable
        {
            private int _disposed;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.ReleaseReference(key, entry, releaseSemaphore: true);
                }
                return ValueTask.CompletedTask;
            }
        }
    }
}

public sealed record RuntimeScheduledToolInvocation(
    RuntimeToolConcurrency Concurrency,
    Func<CancellationToken, ValueTask<RuntimeToolResult>> Execute,
    string? ConcurrencyKey = null,
    string? WorkspaceIdentity = null);

public sealed class RuntimeToolPreparationException(RuntimeError error) : Exception(error.Message)
{
    public RuntimeError Error { get; } = error;
}
