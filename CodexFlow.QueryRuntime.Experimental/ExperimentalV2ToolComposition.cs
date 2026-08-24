using System.Text.Json;
using System.Text.Json.Nodes;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Experimental;

/// <summary>
/// C5 compatibility composition for the existing hardened tool packs. Runtime
/// policy, immutable plan binding, lifecycle and per-Step tool exposure are owned
/// by Engine v2; MEAI wrappers remain at this boundary while tool bodies migrate.
/// </summary>
public static class ExperimentalV2ToolComposition
{
    public static RuntimeToolExecutionPipeline Create(
        QueryRuntimeToolProfile profile,
        string workspacePath,
        ISandboxRunner? sandboxRunner = null,
        RuntimeSandboxKind sandboxKind = RuntimeSandboxKind.LocalProcess,
        bool includeExternal = false,
        TimeProvider? timeProvider = null,
        string? runDirectory = null)
        => CreateRuntime(
            profile,
            workspacePath,
            sandboxRunner,
            sandboxKind,
            includeExternal,
            toolSearch: null,
            timeProvider,
            runDirectory).Pipeline;

    public static ExperimentalV2RuntimeComposition CreateRuntime(
        QueryRuntimeToolProfile profile,
        string workspacePath,
        ISandboxRunner? sandboxRunner = null,
        RuntimeSandboxKind sandboxKind = RuntimeSandboxKind.LocalProcess,
        bool includeExternal = false,
        QueryRuntimeToolSearchOptions? toolSearch = null,
        TimeProvider? timeProvider = null,
        string? runDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var workspaceRoot = QueryRuntimePathSafety.NormalizeRoot(workspacePath);
        var normalizedProfile = ExperimentalToolRegistry.NormalizeProfileName(profile.Name);
        sandboxRunner ??= new Sandbox.LocalProcess.LocalProcessSandboxRunner();

        IReadOnlyList<AIFunction> functions = normalizedProfile switch
        {
            "none" => [],
            "readonly" => ExperimentalReadOnlyToolPack.Create(workspaceRoot),
            "verify" =>
            [
                .. ExperimentalReadOnlyToolPack.Create(workspaceRoot),
                .. ExperimentalVerifyToolPack.Create(workspaceRoot, sandboxRunner)
            ],
            "repair" =>
            [
                .. ExperimentalReadOnlyToolPack.Create(workspaceRoot),
                .. ExperimentalRepairToolPack.Create(workspaceRoot, runDirectory)
            ],
            _ => throw new ArgumentException($"Unsupported v2 tool profile '{profile.Name}'.", nameof(profile))
        };
        if (includeExternal)
        {
            functions = [.. ExternalStdioToolPack.Create(workspaceRoot), .. functions];
        }

        var legacyDescriptors = new List<QueryRuntimeToolDescriptor>(
            new ExperimentalToolRegistry().ListTools(profile));
        var externalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (includeExternal)
        {
            var externalDescriptors = ExternalStdioToolPack.ListDescriptors(profile, workspaceRoot);
            legacyDescriptors.AddRange(externalDescriptors);
            externalNames.UnionWith(externalDescriptors.Select(static descriptor => descriptor.Name));
        }
        var descriptorMap = legacyDescriptors
            .GroupBy(static descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.Single(),
                StringComparer.OrdinalIgnoreCase);
        ExperimentalToolSearchSession? toolSearchSession = null;
        if (toolSearch?.Enabled == true)
        {
            toolSearchSession = new ExperimentalToolSearchSession(
                profile,
                functions,
                legacyDescriptors,
                toolSearch);
            functions = toolSearchSession.GetAllTools();
            descriptorMap["tool_search"] = new QueryRuntimeToolDescriptor(
                "tool_search",
                "Search and activate deferred QRE tools.",
                new HashSet<string>(StringComparer.Ordinal),
                profile,
                QueryRuntimeToolDiscoveryMetadata.FromDescription(
                    "Search and activate deferred QRE tools.",
                    "discovery"),
                QueryRuntimeToolLoading.AlwaysOn);
        }
        var tools = functions.Select(function =>
        {
            if (!descriptorMap.TryGetValue(function.Name, out var descriptor))
            {
                throw new InvalidOperationException($"Tool '{function.Name}' has no registered descriptor.");
            }
            return (IRuntimeTool)new AIFunctionRuntimeTool(
                function,
                CreateDefinition(function, descriptor, sandboxKind, externalNames.Contains(function.Name)));
        }).ToArray();
        var registry = new RuntimeToolRegistry(tools);
        var sandboxes = tools.Any(static tool => tool.Definition.Sandbox.Kind != RuntimeSandboxKind.None)
            ? new RuntimeSandboxRouter([new LegacyRuntimeSandbox(sandboxKind, sandboxRunner)])
            : new RuntimeSandboxRouter([]);
        var pipeline = new RuntimeToolExecutionPipeline(
            registry,
            new ExperimentalV2PolicyEvaluator(profile),
            sandboxes,
            timeProvider);
        return new ExperimentalV2RuntimeComposition(
            pipeline,
            toolSearchSession == null
                ? null
                : new ExperimentalV2ToolCatalogSelector(toolSearchSession, pipeline.Descriptors),
            toolSearchSession?.GetCapabilityCatalog());
    }

    private static RuntimeToolDefinition CreateDefinition(
        AIFunction function,
        QueryRuntimeToolDescriptor descriptor,
        RuntimeSandboxKind sandboxKind,
        bool isExternal)
    {
        var sideEffect = descriptor.Capabilities.Contains(QueryRuntimeCapabilities.WriteFileSystem)
            ? RuntimeToolSideEffect.WorkspaceWrite
            : isExternal || descriptor.Capabilities.Any(static capability =>
                capability.Contains("network", StringComparison.OrdinalIgnoreCase))
                ? RuntimeToolSideEffect.External
                : RuntimeToolSideEffect.ReadOnly;
        var mount = sideEffect == RuntimeToolSideEffect.WorkspaceWrite ||
                    descriptor.Name is "qre_dotnet_test" or "qre_dotnet_build"
            ? RuntimeWorkspaceMountMode.ReadWrite
            : RuntimeWorkspaceMountMode.ReadOnly;
        var isVerifyProcess = descriptor.Name is
            "qre_git_status" or "qre_git_diff" or "qre_dotnet_test" or "qre_dotnet_build";
        var sandbox = isVerifyProcess
            ? new RuntimeSandboxRequirements(sandboxKind, RuntimeNetworkMode.Deny, mount)
            : new RuntimeSandboxRequirements(RuntimeSandboxKind.None, RuntimeNetworkMode.Deny, mount);
        var concurrency = descriptor.Name == "tool_search"
            ? RuntimeToolConcurrency.Serial
            : sideEffect == RuntimeToolSideEffect.WorkspaceWrite || mount == RuntimeWorkspaceMountMode.ReadWrite
            ? RuntimeToolConcurrency.ExclusiveWorkspace
            : descriptor.Capabilities.Contains(QueryRuntimeCapabilities.ExecuteProcess)
                ? RuntimeToolConcurrency.Serial
                : RuntimeToolConcurrency.ParallelSafe;
        return new RuntimeToolDefinition(
            new RuntimeToolDescriptor(
                descriptor.Name,
                "1.0.0",
                function.Description,
                function.JsonSchema.Clone(),
                sideEffect,
                sideEffect is RuntimeToolSideEffect.ReadOnly or RuntimeToolSideEffect.WorkspaceWrite
                    ? RuntimeToolIdempotency.Idempotent
                    : RuntimeToolIdempotency.Unknown),
            new HashSet<string>(descriptor.Capabilities, StringComparer.Ordinal),
            concurrency,
            sandbox,
            new RuntimeToolLimits(TimeSpan.FromMinutes(30), 800_000));
    }

    private sealed class ExperimentalV2ToolCatalogSelector(
        ExperimentalToolSearchSession session,
        IReadOnlyList<RuntimeToolDescriptor> catalog) : IRuntimeToolCatalogSelector
    {
        private readonly IReadOnlyDictionary<string, RuntimeToolDescriptor> _catalog = catalog.ToDictionary(
            static descriptor => descriptor.CanonicalName,
            StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<RuntimeToolDescriptor> SelectTools(
            PreparedRuntimeContext context,
            IReadOnlyList<RuntimeToolDescriptor> frozenCatalog,
            int stepIndex)
            => Array.AsReadOnly(session.GetActiveTools()
                .Select(function => _catalog[function.Name])
                .ToArray());

        public void Observe(RuntimeToolCall call, RuntimeToolResult result)
        {
            // tool_search mutates its session during invocation. Selection on the
            // next Step reads the newly activated names from that same session.
        }
    }

    private sealed class AIFunctionRuntimeTool(
        AIFunction function,
        RuntimeToolDefinition definition) : IRuntimeTool
    {
        public RuntimeToolDefinition Definition { get; } = definition;

        public async ValueTask<RuntimeToolResult> InvokeAsync(
            RuntimeToolInvocation invocation,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
        {
            try
            {
                var value = await function.InvokeAsync(
                    new AIFunctionArguments(ToObjectDictionary(invocation.NormalizedArguments)),
                    ct).ConfigureAwait(false);
                var text = value?.ToString() ?? string.Empty;
                var truncated = text.Contains("output truncated", StringComparison.OrdinalIgnoreCase);
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
                        "compatibility_tool_failed",
                        ex.Message),
                    Details: new RuntimeToolResultDetails(
                        RuntimeToolOutcome.Failed,
                        StandardError: ex.Message));
            }
        }
    }

    private sealed class ExperimentalV2PolicyEvaluator(QueryRuntimeToolProfile profile)
        : IRuntimeToolPolicyEvaluator
    {
        private readonly ExperimentalCapabilityPolicy _inner = new();

        public ValueTask<RuntimeToolPolicyDecision> EvaluateAsync(
            RuntimeToolPolicyContext context,
            CancellationToken ct)
        {
            if (context.Tool.Descriptor.CanonicalName == "tool_search")
            {
                return ValueTask.FromResult(new RuntimeToolPolicyDecision(
                    RuntimeToolPolicyDecisionKind.Allow,
                    "C5 deferred catalog discovery is model-visible but has no execution capability.",
                    context.Tool.Capabilities,
                    context.Tool.Sandbox));
            }
            if (context.Tool.Descriptor.SideEffect == RuntimeToolSideEffect.External)
            {
                return ValueTask.FromResult(new RuntimeToolPolicyDecision(
                    RuntimeToolPolicyDecisionKind.RequireApproval,
                    "External stdio tools require explicit host approval.",
                    context.Tool.Capabilities,
                    context.Tool.Sandbox,
                    TimeSpan.FromMinutes(5)));
            }
            var command = ResolveCommand(context.Tool.Descriptor.CanonicalName, context.Arguments.Value);
            var mount = context.Tool.Sandbox.WorkspaceMount == RuntimeWorkspaceMountMode.ReadWrite ||
                        context.Tool.Descriptor.SideEffect == RuntimeToolSideEffect.WorkspaceWrite
                ? SandboxMountPolicy.WorkspaceReadWrite
                : SandboxMountPolicy.WorkspaceReadOnly;
            var decision = _inner.Evaluate(new QueryRuntimeCapabilityRequest
            {
                Profile = profile,
                ToolName = context.Tool.Descriptor.CanonicalName,
                Capabilities = context.Tool.Capabilities,
                Command = command,
                CommandCapabilities = command.Count == 0
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : ExperimentalCommandCapabilityClassifier.Classify(command, mount),
                WorkspacePath = context.Execution.Environment.WorkspaceIdentity,
                Network = SandboxNetworkPolicy.Deny,
                Mounts = mount
            });
            if (decision.Kind == QueryRuntimeCapabilityDecisionKind.Deny)
            {
                return ValueTask.FromResult(RuntimeToolPolicyDecision.Deny(decision.Reason));
            }
            if (decision.Kind == QueryRuntimeCapabilityDecisionKind.RequireApproval ||
                context.Tool.Descriptor.SideEffect is RuntimeToolSideEffect.WorkspaceWrite or RuntimeToolSideEffect.External)
            {
                return ValueTask.FromResult(RuntimeToolPolicyDecision.RequireApproval(decision.Reason));
            }
            return ValueTask.FromResult(new RuntimeToolPolicyDecision(
                RuntimeToolPolicyDecisionKind.Allow,
                decision.Reason,
                context.Tool.Capabilities,
                context.Tool.Sandbox));
        }

        private static IReadOnlyList<string> ResolveCommand(string toolName, JsonElement arguments)
        {
            static string String(JsonElement arguments, string name)
                => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;
            return toolName switch
            {
                "qre_git_status" => ["git", "status", "--short"],
                "qre_git_diff" => string.IsNullOrWhiteSpace(String(arguments, "path")) || String(arguments, "path") == "."
                    ? ["git", "diff"]
                    : ["git", "diff", "--", String(arguments, "path")],
                "qre_dotnet_test" => DotnetCommand("test", arguments, includeFilter: true),
                "qre_dotnet_build" => DotnetCommand("build", arguments, includeFilter: false),
                "qre_rg_search" => ["rg", String(arguments, "pattern"), String(arguments, "path")],
                _ => []
            };
        }

        private static IReadOnlyList<string> DotnetCommand(
            string verb,
            JsonElement arguments,
            bool includeFilter)
        {
            var command = new List<string> { "dotnet", verb };
            if (arguments.TryGetProperty("target", out var target) &&
                target.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(target.GetString()))
            {
                command.Add(target.GetString()!);
            }
            command.Add("--no-restore");
            if (includeFilter && arguments.TryGetProperty("filter", out var filter) &&
                filter.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(filter.GetString()))
            {
                command.Add("--filter");
                command.Add(filter.GetString()!);
            }
            return command;
        }
    }

    private sealed class LegacyRuntimeSandbox(
        RuntimeSandboxKind kind,
        ISandboxRunner runner) : IRuntimeSandbox
    {
        public RuntimeSandboxKind Kind { get; } = kind;

        public async ValueTask<RuntimeSandboxResult> ExecuteAsync(
            RuntimeSandboxCommand command,
            CancellationToken ct)
        {
            var result = await runner.RunAsync(new SandboxJobSpec
            {
                Command = command.Command,
                WorkingDirectory = command.WorkingDirectory,
                WorkspaceRoot = command.WorkspaceRoot,
                Environment = command.Environment,
                Limits = new SandboxLimits
                {
                    Timeout = command.Limits.Timeout,
                    MaxOutputBytes = command.Limits.MaxOutputBytes,
                    MemoryBytes = command.Limits.MemoryBytes.GetValueOrDefault(512L * 1024 * 1024),
                    CpuCount = command.Limits.CpuCount.GetValueOrDefault(1.0)
                },
                Network = command.Network == RuntimeNetworkMode.Allow
                    ? SandboxNetworkPolicy.Allow
                    : SandboxNetworkPolicy.Deny,
                Mounts = command.WorkspaceMount == RuntimeWorkspaceMountMode.ReadWrite
                    ? SandboxMountPolicy.WorkspaceReadWrite
                    : SandboxMountPolicy.WorkspaceReadOnly
            }, ct).ConfigureAwait(false);
            return new RuntimeSandboxResult(
                result.ExitCode,
                result.StandardOutput,
                result.StandardError,
                result.TimedOut,
                result.DurationMs);
        }
    }

    private static Dictionary<string, object?> ToObjectDictionary(JsonElement arguments)
        => arguments.EnumerateObject().ToDictionary(
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

public sealed record ExperimentalV2RuntimeComposition(
    RuntimeToolExecutionPipeline Pipeline,
    IRuntimeToolCatalogSelector? ToolCatalogSelector,
    string? CapabilityCatalog);
