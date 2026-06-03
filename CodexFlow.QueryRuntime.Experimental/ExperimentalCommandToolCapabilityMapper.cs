using CodexFlow.QueryRuntime.Abstractions;

namespace CodexFlow.QueryRuntime.Experimental;

public static class ExperimentalCommandToolCapabilityMapper
{
    public static IReadOnlySet<string> InferToolCapabilities(IReadOnlySet<string> commandCapabilities)
    {
        var capabilities = new HashSet<string>(StringComparer.Ordinal)
        {
            QueryRuntimeCapabilities.ExecuteProcess
        };

        if (commandCapabilities.Contains(QueryRuntimeCommandCapabilities.ReadWorkspace))
        {
            capabilities.Add(QueryRuntimeCapabilities.ReadFileSystem);
        }

        if (commandCapabilities.Contains(QueryRuntimeCommandCapabilities.WriteWorkspace) ||
            commandCapabilities.Contains(QueryRuntimeCommandCapabilities.PackageInstall) ||
            commandCapabilities.Contains(QueryRuntimeCommandCapabilities.PackageRestore) ||
            commandCapabilities.Contains(QueryRuntimeCommandCapabilities.PackagePublish) ||
            commandCapabilities.Contains(QueryRuntimeCommandCapabilities.Destructive) ||
            commandCapabilities.Contains(QueryRuntimeCommandCapabilities.Deploy) ||
            commandCapabilities.Contains(QueryRuntimeCommandCapabilities.ArbitraryExecution) ||
            commandCapabilities.Contains(QueryRuntimeCommandCapabilities.GitWrite))
        {
            capabilities.Add(QueryRuntimeCapabilities.WriteArtifacts);
        }

        if (commandCapabilities.Contains(QueryRuntimeCommandCapabilities.GitPush) ||
            commandCapabilities.Contains(QueryRuntimeCommandCapabilities.GitWrite))
        {
            capabilities.Add(QueryRuntimeCapabilities.GitRead);
        }

        if (commandCapabilities.Contains(QueryRuntimeCommandCapabilities.PackageRestore) ||
            commandCapabilities.Contains(QueryRuntimeCommandCapabilities.PackagePublish))
        {
            capabilities.Add(QueryRuntimeCapabilities.Build);
        }

        return capabilities;
    }
}
