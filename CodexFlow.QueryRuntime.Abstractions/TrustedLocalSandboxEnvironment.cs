namespace CodexFlow.QueryRuntime.Abstractions;

public static class TrustedLocalSandboxEnvironment
{
    private static readonly string[] Allowlist =
    [
        "PATH",
        "HOME",
        "USERPROFILE",
        "APPDATA",
        "LOCALAPPDATA",
        "TMPDIR",
        "TMP",
        "TEMP",
        "DOTNET_ROOT",
        "DOTNET_HOST_PATH",
        "DOTNET_CLI_HOME",
        "DOTNET_MULTILEVEL_LOOKUP",
        "DOTNET_NOLOGO",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE",
        "NUGET_PACKAGES",
        "LANG",
        "LC_ALL",
        "SYSTEMROOT",
        "WINDIR",
        "COMSPEC",
        "PATHEXT"
    ];

    public static IReadOnlyDictionary<string, string> Create()
    {
        var environment = new Dictionary<string, string>(GetComparer());
        foreach (var name in Allowlist)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    private static StringComparer GetComparer()
        => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
