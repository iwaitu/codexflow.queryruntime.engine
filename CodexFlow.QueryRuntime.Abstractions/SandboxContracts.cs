namespace CodexFlow.QueryRuntime.Abstractions;

public interface ISandboxRunner
{
    Task<SandboxResult> RunAsync(
        SandboxJobSpec spec,
        CancellationToken ct = default);
}

public sealed record SandboxJobSpec
{
    /// <summary>
    /// Executable and arguments. Implementations must not invoke a shell unless
    /// the shell itself is explicitly present in this list.
    /// </summary>
    public required IReadOnlyList<string> Command { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Optional workspace root for isolation-capable runners. When provided,
    /// runners should mount this root and execute in <see cref="WorkingDirectory"/>
    /// relative to it. LocalProcess ignores this value.
    /// </summary>
    public string? WorkspaceRoot { get; init; }

    /// <summary>
    /// Explicit child-process environment. LocalProcess clears inherited host
    /// variables first and injects only these entries.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public SandboxLimits Limits { get; init; } = SandboxLimits.Default;

    /// <summary>
    /// Requested network policy. This is an enforceable contract only for
    /// isolation-capable runners. LocalProcess rejects Allow and treats Deny as
    /// advisory because it cannot block process network syscalls.
    /// </summary>
    public SandboxNetworkPolicy Network { get; init; } = SandboxNetworkPolicy.Deny;

    /// <summary>
    /// Requested workspace mount policy. This is advisory for LocalProcess and
    /// enforceable only by future container, VM, or OS-level runners.
    /// </summary>
    public SandboxMountPolicy Mounts { get; init; } = SandboxMountPolicy.WorkspaceReadOnly;
}

public sealed record SandboxLimits
{
    public static SandboxLimits Default { get; } = new();

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxOutputBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Maximum memory visible to isolation-capable runners. LocalProcess cannot
    /// enforce this limit.
    /// </summary>
    public long MemoryBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>
    /// Fractional CPU quota for isolation-capable runners. LocalProcess cannot
    /// enforce this limit.
    /// </summary>
    public double CpuCount { get; init; } = 1.0;
}

public sealed record SandboxNetworkPolicy(string Mode)
{
    public static SandboxNetworkPolicy Deny { get; } = new("deny");

    public static SandboxNetworkPolicy Allow { get; } = new("allow");
}

public sealed record SandboxMountPolicy(string Mode)
{
    public static SandboxMountPolicy WorkspaceReadOnly { get; } = new("workspace-readonly");

    public static SandboxMountPolicy WorkspaceReadWrite { get; } = new("workspace-readwrite");
}

public sealed record SandboxResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    long DurationMs);
