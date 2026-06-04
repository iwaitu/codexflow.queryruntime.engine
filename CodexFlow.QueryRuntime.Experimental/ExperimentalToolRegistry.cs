using CodexFlow.QueryRuntime.Abstractions;

namespace CodexFlow.QueryRuntime.Experimental;

public sealed class ExperimentalToolRegistry : IToolRegistry
{
    public IReadOnlyList<QueryRuntimeToolDescriptor> ListTools(QueryRuntimeToolProfile profile)
    {
        var normalized = NormalizeProfileName(profile.Name);
        return normalized switch
        {
            "none" => [],
            "readonly" => ReadOnly(profile),
            "verify" => [.. ReadOnly(profile), .. Verify(profile)],
            "repair" => [.. ReadOnly(profile), .. Repair(profile)],
            _ => []
        };
    }

    private static IReadOnlyList<QueryRuntimeToolDescriptor> ReadOnly(QueryRuntimeToolProfile profile)
        =>
        [
            new(
                "qre_list_files",
                "List files and directories under the workspace root.",
                CapabilitySet(QueryRuntimeCapabilities.ReadFileSystem),
                profile),
            new(
                "qre_read_file",
                "Read a UTF-8 text file under the workspace root.",
                CapabilitySet(QueryRuntimeCapabilities.ReadFileSystem),
                profile),
            new(
                "qre_search_files",
                "Search text files under the workspace root.",
                CapabilitySet(QueryRuntimeCapabilities.ReadFileSystem),
                profile),
            new(
                "qre_rg_search",
                "Run rg as a read-only workspace search command.",
                CapabilitySet(QueryRuntimeCapabilities.ReadFileSystem, QueryRuntimeCapabilities.ExecuteProcess),
                profile)
        ];

    private static IReadOnlyList<QueryRuntimeToolDescriptor> Verify(QueryRuntimeToolProfile profile)
        =>
        [
            new(
                "qre_git_status",
                "Run git status --short in the workspace.",
                CapabilitySet(QueryRuntimeCapabilities.GitRead, QueryRuntimeCapabilities.ExecuteProcess),
                profile),
            new(
                "qre_git_diff",
                "Run git diff in the workspace.",
                CapabilitySet(QueryRuntimeCapabilities.GitRead, QueryRuntimeCapabilities.ExecuteProcess),
                profile),
            new(
                "qre_dotnet_test",
                "Run dotnet test --no-restore for trusted local verification.",
                CapabilitySet(
                    QueryRuntimeCapabilities.ReadFileSystem,
                    QueryRuntimeCapabilities.WriteArtifacts,
                    QueryRuntimeCapabilities.ExecuteProcess,
                    QueryRuntimeCapabilities.RunTests,
                    QueryRuntimeCapabilities.Build),
                profile),
            new(
                "qre_dotnet_build",
                "Run dotnet build --no-restore for trusted local verification.",
                CapabilitySet(
                    QueryRuntimeCapabilities.ReadFileSystem,
                    QueryRuntimeCapabilities.WriteArtifacts,
                    QueryRuntimeCapabilities.ExecuteProcess,
                    QueryRuntimeCapabilities.Build),
                profile)
        ];

    private static IReadOnlyList<QueryRuntimeToolDescriptor> Repair(QueryRuntimeToolProfile profile)
        =>
        [
            new(
                "qre_write_file",
                "Write UTF-8 text to a workspace file.",
                CapabilitySet(QueryRuntimeCapabilities.ReadFileSystem, QueryRuntimeCapabilities.WriteFileSystem),
                profile),
            new(
                "qre_apply_patch",
                "Apply a targeted text replacement patch to a workspace file.",
                CapabilitySet(QueryRuntimeCapabilities.ReadFileSystem, QueryRuntimeCapabilities.WriteFileSystem),
                profile)
        ];

    private static IReadOnlySet<string> CapabilitySet(params string[] capabilities)
        => new HashSet<string>(capabilities, StringComparer.Ordinal);

    public static string NormalizeProfileName(string? profile)
        => string.IsNullOrWhiteSpace(profile)
            ? "none"
            : profile.Trim().ToLowerInvariant() switch
            {
                "read-only" or "read" => "readonly",
                var value => value
            };
}
