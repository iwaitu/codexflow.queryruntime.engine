namespace CodexFlow.QueryRuntime.Abstractions;

/// <summary>
/// Shared path safety helpers for hosts that need QRE-compatible workspace and
/// trace artifact containment checks.
/// </summary>
public static class QueryRuntimePathSafety
{
    private static readonly string[] SecretSegmentMarkers =
    [
        "secret",
        "token",
        "credential"
    ];

    /// <summary>
    /// Converts a host-provided workspace or trace root to an absolute path.
    /// </summary>
    public static string NormalizeRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        return Path.GetFullPath(rootPath);
    }

    /// <summary>
    /// Resolves a relative or absolute candidate path and verifies it remains
    /// under the supplied root, including segment-by-segment link target checks.
    /// </summary>
    public static string ResolveUnderRoot(string rootPath, string? path)
    {
        var root = NormalizeRoot(rootPath);
        path = string.IsNullOrWhiteSpace(path) ? "." : path;

        var resolved = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));

        if (!IsUnderRoot(root, resolved))
        {
            throw new InvalidOperationException("Path traversal outside workspace is not allowed.");
        }

        RejectLinkEscape(root, resolved);
        return resolved;
    }

    /// <summary>
    /// Returns true when the candidate is equal to, or physically below, the root.
    /// </summary>
    public static bool IsUnderRoot(string rootPath, string candidatePath)
    {
        var comparison = GetComparison();
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(root, candidate, comparison))
        {
            return true;
        }

        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, comparison);
    }

    /// <summary>
    /// Rejects paths under protected QRE workspace areas or paths that look like
    /// credentials. The supplied path must already be resolved under the root.
    /// </summary>
    public static void RejectProtectedWorkspacePath(string rootPath, string resolvedPath, string operation)
    {
        var segments = GetRelativeSegments(rootPath, resolvedPath);
        if (segments.Any(static segment =>
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".qre", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Protected workspace artifacts cannot be {operation}.");
        }

        if (segments.Any(IsSecretLookingSegment))
        {
            throw new InvalidOperationException($"Secret-looking paths cannot be {operation}.");
        }
    }

    /// <summary>
    /// Identifies common credential file names and secret-bearing path segments.
    /// </summary>
    public static bool IsSecretLookingSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        var name = segment.Trim().ToLowerInvariant();
        return name is ".env" or ".env.local" or ".netrc" or "id_rsa" or "id_dsa" or "id_ecdsa" or "id_ed25519" ||
               SecretSegmentMarkers.Any(name.Contains) ||
               name.EndsWith(".pem", StringComparison.Ordinal) ||
               name.EndsWith(".key", StringComparison.Ordinal);
    }

    private static string[] GetRelativeSegments(string rootPath, string resolvedPath)
    {
        var relative = Path.GetRelativePath(rootPath, resolvedPath);
        return relative == "."
            ? []
            : relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void RejectLinkEscape(string rootPath, string resolvedPath)
    {
        var relative = Path.GetRelativePath(rootPath, resolvedPath);
        if (relative == ".")
        {
            return;
        }

        var current = rootPath;
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

            if (!IsUnderRoot(rootPath, linkTarget))
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
