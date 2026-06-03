namespace CodexFlow.QueryRuntime.Experimental;

internal static class ExperimentalWorkspacePath
{
    public static string NormalizeRoot(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        return Path.GetFullPath(workspacePath);
    }

    public static string ResolveUnderRoot(string workspaceRoot, string? path)
    {
        workspaceRoot = NormalizeRoot(workspaceRoot);
        path = string.IsNullOrWhiteSpace(path) ? "." : path;

        var resolved = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(workspaceRoot, path));

        if (!IsUnderRoot(workspaceRoot, resolved))
        {
            throw new InvalidOperationException("Path traversal outside workspace is not allowed.");
        }

        RejectSymlinkEscape(workspaceRoot, resolved);
        return resolved;
    }

    private static bool IsUnderRoot(string workspaceRoot, string candidate)
    {
        var comparison = GetComparison();
        var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(root, fullCandidate, comparison))
        {
            return true;
        }

        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(rootWithSeparator, comparison);
    }

    private static void RejectSymlinkEscape(string workspaceRoot, string resolved)
    {
        var relative = Path.GetRelativePath(workspaceRoot, resolved);
        if (relative == ".")
        {
            return;
        }

        var current = workspaceRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            current = Path.Combine(current, segment);
            var linkTarget = TryGetLinkTarget(current);
            if (string.IsNullOrWhiteSpace(linkTarget))
            {
                continue;
            }

            var linkParent = Path.GetDirectoryName(current) ?? workspaceRoot;
            var target = Path.IsPathFullyQualified(linkTarget)
                ? Path.GetFullPath(linkTarget)
                : Path.GetFullPath(Path.Combine(linkParent, linkTarget));
            if (!IsUnderRoot(workspaceRoot, target))
            {
                throw new InvalidOperationException("Symlink traversal outside workspace is not allowed.");
            }
        }
    }

    private static string? TryGetLinkTarget(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return new DirectoryInfo(path).LinkTarget;
            }

            if (File.Exists(path))
            {
                return new FileInfo(path).LinkTarget;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static StringComparison GetComparison()
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
