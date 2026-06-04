namespace CodexFlow.QueryRuntime.Abstractions;

public interface IToolRegistry
{
    IReadOnlyList<QueryRuntimeToolDescriptor> ListTools(QueryRuntimeToolProfile profile);
}

public sealed record QueryRuntimeToolDescriptor(
    string Name,
    string? Description,
    IReadOnlySet<string> Capabilities,
    QueryRuntimeToolProfile Profile);

public interface IQueryRuntimeCapabilityPolicy
{
    QueryRuntimeCapabilityDecision Evaluate(QueryRuntimeCapabilityRequest request);
}

public interface IQueryRuntimePolicyDecisionSink
{
    Task OnPolicyDecisionAsync(
        QueryRuntimePolicyDecisionRecord decision,
        CancellationToken ct = default);
}

public sealed record QueryRuntimePolicyDecisionRecord(
    string Profile,
    string ToolName,
    IReadOnlySet<string> Capabilities,
    IReadOnlyList<string> Command,
    string Network,
    string Mount,
    string Decision,
    bool Allowed,
    string Reason,
    DateTimeOffset Timestamp);

public sealed record QueryRuntimeCapabilityRequest
{
    public required QueryRuntimeToolProfile Profile { get; init; }

    public required string ToolName { get; init; }

    public required IReadOnlySet<string> Capabilities { get; init; }

    public IReadOnlyList<string> Command { get; init; } = [];

    public IReadOnlySet<string> CommandCapabilities { get; init; } = EmptyStringSet.Value;

    public bool ExplicitApproval { get; init; }

    public string? ApprovalReason { get; init; }

    public string? WorkspacePath { get; init; }

    public SandboxNetworkPolicy Network { get; init; } = SandboxNetworkPolicy.Deny;

    public SandboxMountPolicy Mounts { get; init; } = SandboxMountPolicy.WorkspaceReadOnly;
}

public sealed record QueryRuntimeCapabilityDecision(
    QueryRuntimeCapabilityDecisionKind Kind,
    string Reason)
{
    public static QueryRuntimeCapabilityDecision Allow(string reason = "allowed")
        => new(QueryRuntimeCapabilityDecisionKind.Allow, reason);

    public static QueryRuntimeCapabilityDecision Deny(string reason)
        => new(QueryRuntimeCapabilityDecisionKind.Deny, reason);

    public static QueryRuntimeCapabilityDecision RequireApproval(string reason)
        => new(QueryRuntimeCapabilityDecisionKind.RequireApproval, reason);
}

public enum QueryRuntimeCapabilityDecisionKind
{
    Allow = 0,
    Deny = 1,
    RequireApproval = 2
}

public static class QueryRuntimeCapabilities
{
    public const string ReadFileSystem = "read_fs";
    public const string WriteFileSystem = "write_fs";
    public const string WriteArtifacts = "write_artifacts";
    public const string ExecuteProcess = "execute_process";
    public const string GitRead = "git_read";
    public const string RunTests = "run_tests";
    public const string Build = "build";
}

public static class QueryRuntimeCommandCapabilities
{
    public const string ReadWorkspace = "command.read_workspace";
    public const string WriteWorkspace = "command.write_workspace";
    public const string NetworkAccess = "command.network_access";
    public const string PackageInstall = "command.package_install";
    public const string PackageRestore = "command.package_restore";
    public const string PackagePublish = "command.package_publish";
    public const string GitPush = "command.git_push";
    public const string GitWrite = "command.git_write";
    public const string Destructive = "command.destructive";
    public const string Deploy = "command.deploy";
    public const string ArbitraryExecution = "command.arbitrary_execution";
    public const string UnknownProcess = "command.unknown_process";
}

internal static class EmptyStringSet
{
    public static readonly IReadOnlySet<string> Value = new HashSet<string>(StringComparer.Ordinal);
}
