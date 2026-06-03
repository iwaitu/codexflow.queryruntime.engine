namespace CodexFlow.QueryRuntime.IntegrationTests.Infrastructure;

internal static class RepositoryPathHelper
{
    public static string? FindRepositoryFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (File.Exists(Path.Combine(current.FullName, "CodexFlow.slnx")))
            {
                var rooted = Path.Combine(current.FullName, relativePath);
                if (File.Exists(rooted))
                {
                    return rooted;
                }
            }

            current = current.Parent;
        }

        return null;
    }
}
