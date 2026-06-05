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
                profile,
                Discovery(
                    "List workspace files and directories.",
                    ["file", "list", "directory", "tree", "ls"],
                    [],
                    ["path", "max_entries"],
                    "file"),
                QueryRuntimeToolLoading.Deferred),
            new(
                "qre_read_file",
                "Read a UTF-8 text file under the workspace root.",
                CapabilitySet(QueryRuntimeCapabilities.ReadFileSystem),
                profile,
                Discovery(
                    "Read a UTF-8 workspace text file.",
                    ["file", "read", "cat", "open", "text"],
                    ["path"],
                    ["start_line", "max_lines"],
                    "file"),
                QueryRuntimeToolLoading.Deferred),
            new(
                "qre_search_files",
                "Search text files under the workspace root.",
                CapabilitySet(QueryRuntimeCapabilities.ReadFileSystem),
                profile,
                Discovery(
                    "Search text inside workspace files.",
                    ["file", "search", "grep", "text", "find"],
                    ["pattern"],
                    ["path", "max_matches"],
                    "file"),
                QueryRuntimeToolLoading.Deferred),
            new(
                "qre_rg_search",
                "Run rg as a read-only workspace search command.",
                CapabilitySet(QueryRuntimeCapabilities.ReadFileSystem, QueryRuntimeCapabilities.ExecuteProcess),
                profile,
                Discovery(
                    "Run ripgrep for fast read-only workspace search.",
                    ["rg", "ripgrep", "grep", "search", "pattern"],
                    ["pattern"],
                    ["path", "max_matches"],
                    "file"),
                QueryRuntimeToolLoading.Deferred)
        ];

    private static IReadOnlyList<QueryRuntimeToolDescriptor> Verify(QueryRuntimeToolProfile profile)
        =>
        [
            new(
                "qre_git_status",
                "Run git status --short in the workspace.",
                CapabilitySet(QueryRuntimeCapabilities.GitRead, QueryRuntimeCapabilities.ExecuteProcess),
                profile,
                Discovery(
                    "Inspect workspace git status.",
                    ["git", "status", "changes", "dirty", "short"],
                    [],
                    ["max_output_chars"],
                    "git"),
                QueryRuntimeToolLoading.Deferred),
            new(
                "qre_git_diff",
                "Run git diff in the workspace.",
                CapabilitySet(QueryRuntimeCapabilities.GitRead, QueryRuntimeCapabilities.ExecuteProcess),
                profile,
                Discovery(
                    "Inspect git diff for workspace changes.",
                    ["git", "diff", "patch", "changes"],
                    [],
                    ["path", "max_output_chars"],
                    "git"),
                QueryRuntimeToolLoading.Deferred),
            new(
                "qre_dotnet_test",
                "Run dotnet test --no-restore for trusted local verification.",
                CapabilitySet(
                    QueryRuntimeCapabilities.ReadFileSystem,
                    QueryRuntimeCapabilities.WriteArtifacts,
                    QueryRuntimeCapabilities.ExecuteProcess,
                    QueryRuntimeCapabilities.RunTests,
                    QueryRuntimeCapabilities.Build),
                profile,
                Discovery(
                    "Run dotnet tests for trusted local verification.",
                    ["dotnet", "test", "tests", "verify", "failure"],
                    [],
                    ["target", "filter", "timeout_seconds", "max_output_chars"],
                    "test"),
                QueryRuntimeToolLoading.Deferred),
            new(
                "qre_dotnet_build",
                "Run dotnet build --no-restore for trusted local verification.",
                CapabilitySet(
                    QueryRuntimeCapabilities.ReadFileSystem,
                    QueryRuntimeCapabilities.WriteArtifacts,
                    QueryRuntimeCapabilities.ExecuteProcess,
                    QueryRuntimeCapabilities.Build),
                profile,
                Discovery(
                    "Run dotnet build for trusted local verification.",
                    ["dotnet", "build", "compile", "verify"],
                    [],
                    ["target", "timeout_seconds", "max_output_chars"],
                    "test"),
                QueryRuntimeToolLoading.Deferred)
        ];

    private static IReadOnlyList<QueryRuntimeToolDescriptor> Repair(QueryRuntimeToolProfile profile)
        =>
        [
            new(
                "qre_write_file",
                "Write UTF-8 text to a workspace file.",
                CapabilitySet(QueryRuntimeCapabilities.ReadFileSystem, QueryRuntimeCapabilities.WriteFileSystem),
                profile,
                Discovery(
                    "Write UTF-8 content to a workspace file.",
                    ["file", "write", "create", "edit", "content"],
                    ["path", "content"],
                    ["overwrite"],
                    "file"),
                QueryRuntimeToolLoading.Deferred),
            new(
                "qre_apply_patch",
                "Apply a targeted text replacement patch to a workspace file.",
                CapabilitySet(QueryRuntimeCapabilities.ReadFileSystem, QueryRuntimeCapabilities.WriteFileSystem),
                profile,
                Discovery(
                    "Apply a focused text replacement patch to a workspace file.",
                    ["patch", "apply", "edit", "modify", "replace", "diff"],
                    ["path", "old_text", "new_text"],
                    ["replace_all"],
                    "file"),
                QueryRuntimeToolLoading.Deferred)
        ];

    private static IReadOnlySet<string> CapabilitySet(params string[] capabilities)
        => new HashSet<string>(capabilities, StringComparer.Ordinal);

    private static QueryRuntimeToolDiscoveryMetadata Discovery(
        string searchHint,
        IReadOnlyList<string> keywords,
        IReadOnlyList<string> requiredArgs,
        IReadOnlyList<string> optionalArgs,
        string capability)
        => new(searchHint, keywords, requiredArgs, optionalArgs, [], capability);

    public static string NormalizeProfileName(string? profile)
        => string.IsNullOrWhiteSpace(profile)
            ? "none"
            : profile.Trim().ToLowerInvariant() switch
            {
                "read-only" or "read" => "readonly",
                var value => value
            };
}
