using CodexFlow.QueryRuntime.Abstractions;

namespace CodexFlow.QueryRuntime.Experimental;

public sealed class ExperimentalCapabilityPolicy : IQueryRuntimeCapabilityPolicy
{
    private static readonly IReadOnlySet<string> ReadOnlyCapabilities = CapabilitySet(
        QueryRuntimeCapabilities.ReadFileSystem,
        QueryRuntimeCapabilities.ExecuteProcess);

    private static readonly IReadOnlySet<string> VerifyCapabilities = CapabilitySet(
        QueryRuntimeCapabilities.ReadFileSystem,
        QueryRuntimeCapabilities.WriteArtifacts,
        QueryRuntimeCapabilities.ExecuteProcess,
        QueryRuntimeCapabilities.GitRead,
        QueryRuntimeCapabilities.RunTests,
        QueryRuntimeCapabilities.Build);

    private static readonly IReadOnlySet<string> RepairCapabilities = CapabilitySet(
        QueryRuntimeCapabilities.ReadFileSystem,
        QueryRuntimeCapabilities.WriteFileSystem);

    public QueryRuntimeCapabilityDecision Evaluate(QueryRuntimeCapabilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.Network.Mode, SandboxNetworkPolicy.Deny.Mode, StringComparison.OrdinalIgnoreCase))
        {
            return QueryRuntimeCapabilityDecision.Deny("Network access is not allowed by the experimental capability policy.");
        }

        var profile = ExperimentalToolRegistry.NormalizeProfileName(request.Profile.Name);
        return profile switch
        {
            "none" => request.Capabilities.Count == 0
                ? QueryRuntimeCapabilityDecision.Allow("none profile has no capabilities")
                : QueryRuntimeCapabilityDecision.Deny("none profile cannot execute tools."),
            "readonly" => EvaluateReadOnly(request),
            "verify" => EvaluateVerify(request),
            "repair" => EvaluateRepair(request),
            _ => QueryRuntimeCapabilityDecision.Deny($"Unknown profile: {request.Profile.Name}")
        };
    }

    private static QueryRuntimeCapabilityDecision EvaluateRepair(QueryRuntimeCapabilityRequest request)
    {
        var capabilityDecision = EvaluateCapabilitySet(request, RepairCapabilities, allowCommands: false);
        if (capabilityDecision.Kind != QueryRuntimeCapabilityDecisionKind.Allow)
        {
            return capabilityDecision;
        }

        if (!string.Equals(request.Mounts.Mode, SandboxMountPolicy.WorkspaceReadWrite.Mode, StringComparison.OrdinalIgnoreCase))
        {
            return QueryRuntimeCapabilityDecision.Deny("repair file tools require a read-write workspace mount.");
        }

        return request.ToolName switch
        {
            "qre_write_file" or "qre_apply_patch" when request.Command.Count == 0
                => QueryRuntimeCapabilityDecision.Allow("repair workspace file tool allowed"),
            _ => QueryRuntimeCapabilityDecision.Deny($"Tool is not allowed in repair profile: {request.ToolName}")
        };
    }

    private static QueryRuntimeCapabilityDecision EvaluateVerify(QueryRuntimeCapabilityRequest request)
    {
        var capabilityDecision = EvaluateCapabilitySet(request, VerifyCapabilities, allowCommands: true);
        if (capabilityDecision.Kind != QueryRuntimeCapabilityDecisionKind.Allow)
        {
            return capabilityDecision;
        }

        return request.ToolName switch
        {
            "qre_git_status" => CommandStartsWith(request.Command, "git", "status", "--short")
                ? AllowWhenCommandAllowed(request, false, true, "verify git status allowed")
                : QueryRuntimeCapabilityDecision.Deny("qre_git_status may only run git status --short."),
            "qre_git_diff" => CommandStartsWith(request.Command, "git", "diff")
                ? AllowWhenCommandAllowed(request, false, true, "verify git diff allowed")
                : QueryRuntimeCapabilityDecision.Deny("qre_git_diff may only run git diff."),
            "qre_dotnet_test" => IsDotnetTestNoRestore(request.Command)
                ? AllowWhenCommandAllowed(request, true, true, "verify dotnet test --no-restore allowed")
                : QueryRuntimeCapabilityDecision.Deny("qre_dotnet_test may only run dotnet test --no-restore."),
            "qre_dotnet_build" => IsDotnetBuildNoRestore(request.Command)
                ? AllowWhenCommandAllowed(request, true, true, "verify dotnet build --no-restore allowed")
                : QueryRuntimeCapabilityDecision.Deny("qre_dotnet_build may only run dotnet build --no-restore."),
            "qre_rg_search" => IsRipgrepReadOnlySearch(request.Command)
                ? AllowWhenCommandAllowed(request, false, true, "verify rg search allowed")
                : QueryRuntimeCapabilityDecision.Deny("qre_rg_search may only run rg."),
            "qre_sandbox_exec" => EvaluateCommandCapabilities(
                request,
                allowWorkspaceWrites: true,
                requireApprovalForRestrictedCommands: true),
            _ => QueryRuntimeCapabilityDecision.Deny($"Tool is not allowed in verify profile: {request.ToolName}")
        };
    }

    private static QueryRuntimeCapabilityDecision EvaluateReadOnly(QueryRuntimeCapabilityRequest request)
    {
        var capabilityDecision = EvaluateCapabilitySet(request, ReadOnlyCapabilities, allowCommands: true);
        if (capabilityDecision.Kind != QueryRuntimeCapabilityDecisionKind.Allow)
        {
            return capabilityDecision;
        }

        return request.ToolName switch
        {
            "qre_list_files" or "qre_read_file" or "qre_search_files" when request.Command.Count == 0
                => QueryRuntimeCapabilityDecision.Allow("readonly file tool allowed"),
            "qre_rg_search" => IsRipgrepReadOnlySearch(request.Command)
                ? AllowWhenCommandAllowed(request, false, false, "readonly rg search allowed")
                : QueryRuntimeCapabilityDecision.Deny("qre_rg_search may only run rg."),
            "qre_sandbox_exec" => EvaluateCommandCapabilities(
                request,
                allowWorkspaceWrites: false,
                requireApprovalForRestrictedCommands: false),
            _ => QueryRuntimeCapabilityDecision.Deny($"Tool is not allowed in readonly profile: {request.ToolName}")
        };
    }

    private static QueryRuntimeCapabilityDecision AllowWhenCommandAllowed(
        QueryRuntimeCapabilityRequest request,
        bool allowWorkspaceWrites,
        bool requireApprovalForRestrictedCommands,
        string allowReason)
    {
        var decision = EvaluateCommandCapabilities(
            request,
            allowWorkspaceWrites,
            requireApprovalForRestrictedCommands);
        return decision.Kind == QueryRuntimeCapabilityDecisionKind.Allow
            ? QueryRuntimeCapabilityDecision.Allow(allowReason)
            : decision;
    }

    private static QueryRuntimeCapabilityDecision EvaluateCommandCapabilities(
        QueryRuntimeCapabilityRequest request,
        bool allowWorkspaceWrites,
        bool requireApprovalForRestrictedCommands)
    {
        var commandCapabilities = request.CommandCapabilities.Count > 0
            ? request.CommandCapabilities
            : ExperimentalCommandCapabilityClassifier.Classify(request.Command, request.Mounts);

        if (!allowWorkspaceWrites &&
            commandCapabilities.Contains(QueryRuntimeCommandCapabilities.WriteWorkspace))
        {
            return QueryRuntimeCapabilityDecision.Deny("Command may write workspace files.");
        }

        if (ExperimentalCommandCapabilityClassifier.HasAny(
                commandCapabilities,
                QueryRuntimeCommandCapabilities.NetworkAccess,
                QueryRuntimeCommandCapabilities.PackageInstall,
                QueryRuntimeCommandCapabilities.PackageRestore,
                QueryRuntimeCommandCapabilities.PackagePublish,
                QueryRuntimeCommandCapabilities.GitPush,
                QueryRuntimeCommandCapabilities.GitWrite,
                QueryRuntimeCommandCapabilities.Destructive,
                QueryRuntimeCommandCapabilities.Deploy,
                QueryRuntimeCommandCapabilities.ArbitraryExecution))
        {
            if (!requireApprovalForRestrictedCommands)
            {
                return QueryRuntimeCapabilityDecision.Deny("Command is not allowed by this profile.");
            }

            if (request.ExplicitApproval)
            {
                var reason = string.IsNullOrWhiteSpace(request.ApprovalReason)
                    ? "Explicit approval granted by capability policy."
                    : $"Explicit approval granted by capability policy: {request.ApprovalReason.Trim()}";
                return QueryRuntimeCapabilityDecision.Allow(reason);
            }

            return QueryRuntimeCapabilityDecision.RequireApproval("Command requires explicit approval by capability policy.");
        }

        if (commandCapabilities.Contains(QueryRuntimeCommandCapabilities.UnknownProcess))
        {
            return QueryRuntimeCapabilityDecision.Deny("Command is not covered by the experimental command capability schema.");
        }

        return QueryRuntimeCapabilityDecision.Allow();
    }

    private static QueryRuntimeCapabilityDecision EvaluateCapabilitySet(
        QueryRuntimeCapabilityRequest request,
        IReadOnlySet<string> allowedCapabilities,
        bool allowCommands)
    {
        var deniedCapability = request.Capabilities.FirstOrDefault(capability => !allowedCapabilities.Contains(capability));
        if (deniedCapability != null)
        {
            return QueryRuntimeCapabilityDecision.Deny($"Capability is not allowed for profile {request.Profile.Name}: {deniedCapability}");
        }

        if (!allowCommands && request.Command.Count > 0)
        {
            return QueryRuntimeCapabilityDecision.Deny($"Profile {request.Profile.Name} does not allow process execution.");
        }

        return QueryRuntimeCapabilityDecision.Allow();
    }

    private static bool CommandStartsWith(IReadOnlyList<string> command, params string[] expected)
    {
        if (command.Count < expected.Length)
        {
            return false;
        }

        for (var i = 0; i < expected.Length; i++)
        {
            if (!string.Equals(command[i], expected[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDotnetTestNoRestore(IReadOnlyList<string> command)
        => command.Count >= 3 &&
           string.Equals(command[0], "dotnet", StringComparison.Ordinal) &&
           string.Equals(command[1], "test", StringComparison.Ordinal) &&
           command.Contains("--no-restore", StringComparer.Ordinal);

    private static bool IsDotnetBuildNoRestore(IReadOnlyList<string> command)
        => command.Count >= 3 &&
           string.Equals(command[0], "dotnet", StringComparison.Ordinal) &&
           string.Equals(command[1], "build", StringComparison.Ordinal) &&
           command.Contains("--no-restore", StringComparer.Ordinal);

    private static bool IsRipgrepReadOnlySearch(IReadOnlyList<string> command)
        => command.Count >= 1 &&
           string.Equals(command[0], "rg", StringComparison.Ordinal);

    private static IReadOnlySet<string> CapabilitySet(params string[] capabilities)
        => new HashSet<string>(capabilities, StringComparer.Ordinal);
}
