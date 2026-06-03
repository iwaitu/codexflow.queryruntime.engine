namespace CodexFlow.QueryRuntime.Sandbox.Docker;

public static class DockerSandboxSeccompProfile
{
    private const string BundledProfileRelativePath = "seccomp/qre-seccomp-profile.json";

    public static string? ResolveBundledProfilePath()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, BundledProfileRelativePath);
        yield return Path.Combine(
            AppContext.BaseDirectory,
            "CodexFlow.QueryRuntime.Sandbox.Docker",
            BundledProfileRelativePath);

        foreach (var root in EnumerateParents(AppContext.BaseDirectory))
        {
            yield return Path.Combine(
                root,
                "CodexFlow.QueryRuntime.Sandbox.Docker",
                BundledProfileRelativePath);
        }

        foreach (var root in EnumerateParents(Directory.GetCurrentDirectory()))
        {
            yield return Path.Combine(
                root,
                "CodexFlow.QueryRuntime.Sandbox.Docker",
                BundledProfileRelativePath);
        }
    }

    private static IEnumerable<string> EnumerateParents(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        while (directory != null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }
}
