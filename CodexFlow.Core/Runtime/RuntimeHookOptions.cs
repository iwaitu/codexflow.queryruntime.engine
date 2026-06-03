namespace CodexFlow.Core.Runtime;

/// <summary>
/// Configuration for runtime hooks.
/// </summary>
public sealed class RuntimeHookOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "RuntimeHooks";

    /// <summary>Stop hook settings.</summary>
    public StopRuntimeHookOptions Stop { get; set; } = new();
}

/// <summary>
/// Configuration for Stop hooks.
/// </summary>
public sealed class StopRuntimeHookOptions
{
    /// <summary>Whether configured Stop hooks are enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Per-command timeout in milliseconds.</summary>
    public int TimeoutMs { get; set; } = 5_000;

    /// <summary>Whether project-local hooks under .codexflow/hooks should be auto-discovered.</summary>
    public bool EnableProjectHooks { get; set; } = true;

    /// <summary>Configured commands executed before the runtime accepts a final no-tool response.</summary>
    public List<StopHookCommandOptions> Commands { get; set; } = [];
}

/// <summary>
/// A configured Stop hook command.
/// </summary>
public sealed class StopHookCommandOptions
{
    /// <summary>Human-readable hook name used in logs.</summary>
    public string? Name { get; set; }

    /// <summary>Whether this hook is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Executable file name or absolute path.</summary>
    public string? FileName { get; set; }

    /// <summary>Optional argument string passed to the executable.</summary>
    public string? Arguments { get; set; }

    /// <summary>Optional working directory for the executable.</summary>
    public string? WorkingDirectory { get; set; }
}
