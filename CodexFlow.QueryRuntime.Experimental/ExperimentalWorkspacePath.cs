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
            if (linkTarget == null)
            {
                continue;
            }

            if (!IsUnderRoot(workspaceRoot, linkTarget))
            {
                throw new InvalidOperationException("Symlink traversal outside workspace is not allowed.");
            }
        }
    }

    private static string? TryGetLinkTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);

            if (string.IsNullOrWhiteSpace(info.LinkTarget))
            {
                return null;
            }

            var finalTarget = info.ResolveLinkTarget(returnFinalTarget: true);
            if (finalTarget != null)
            {
                return finalTarget.FullName;
            }

            var linkParent = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
            return Path.IsPathFullyQualified(info.LinkTarget)
                ? Path.GetFullPath(info.LinkTarget)
                : Path.GetFullPath(Path.Combine(linkParent, info.LinkTarget));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static StringComparison GetComparison()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
