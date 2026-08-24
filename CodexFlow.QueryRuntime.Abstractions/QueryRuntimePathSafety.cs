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
        RejectLinkedRoot(root);
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
    /// Rejects paths under protected QRE workspace areas or paths with exact,
    /// high-confidence credential file names. Fuzzy secret-looking matches are
    /// intentionally not enforcement rules because names such as TokenService.cs
    /// and SecretMaskerTests.cs are normal source files.
    /// </summary>
    public static void RejectProtectedWorkspacePath(string rootPath, string resolvedPath, string operation)
    {
        RejectWorkspaceLinks(rootPath, resolvedPath, operation);
        var segments = GetRelativeSegments(rootPath, resolvedPath);
        if (segments.Any(static segment =>
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".qre", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Protected workspace artifacts cannot be {operation}.");
        }

        if (segments.Any(IsProtectedCredentialSegment))
        {
            throw new InvalidOperationException($"Protected credential paths cannot be {operation}.");
        }
    }

    /// <summary>
    /// Rejects every symlink, junction, or reparse point in a workspace tool path.
    /// Repository tools use this stricter rule because an in-workspace alias can
    /// otherwise hide a protected credential path from lexical checks.
    /// </summary>
    public static void RejectWorkspaceLinks(string rootPath, string resolvedPath, string operation)
    {
        var root = NormalizeRoot(rootPath);
        var resolved = Path.GetFullPath(resolvedPath);
        if (!IsUnderRoot(root, resolved))
        {
            throw new InvalidOperationException("Path traversal outside workspace is not allowed.");
        }

        var current = root;
        foreach (var segment in GetRelativeSegments(root, resolved))
        {
            current = Path.Combine(current, segment);
            if (GetLinkTargetOrThrow(current) != null)
            {
                throw new InvalidOperationException($"Linked workspace paths cannot be {operation}.");
            }
        }
    }

    /// <summary>
    /// Identifies exact high-confidence credential file names that are safe to
    /// use as a mandatory deny rule.
    /// </summary>
    public static bool IsProtectedCredentialSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        var name = segment.Trim().ToLowerInvariant();
        var protectedEnvironmentFile = name.Equals(".env", StringComparison.Ordinal) ||
                                       name.StartsWith(".env.", StringComparison.Ordinal) &&
                                       name is not ".env.example" and not ".env.sample" and not ".env.template";
        return protectedEnvironmentFile ||
               name is ".netrc" or "credentials" or "id_rsa" or "id_dsa" or "id_ecdsa" or "id_ed25519" ||
               name.EndsWith(".key", StringComparison.Ordinal);
    }

    /// <summary>
    /// Heuristically identifies names that may carry credentials. This helper is
    /// for warnings, manifests, or additional approval only; callers must not use
    /// fuzzy matches as an unconditional read/write denial.
    /// </summary>
    public static bool IsSecretLookingSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        var name = segment.Trim().ToLowerInvariant();
        return IsProtectedCredentialSegment(name) ||
               SecretSegmentMarkers.Any(name.Contains) ||
               name.EndsWith(".pem", StringComparison.Ordinal);
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
            var linkTarget = GetLinkTargetOrThrow(current);
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

    private static void RejectLinkedRoot(string rootPath)
    {
        var linkTarget = GetLinkTargetOrThrow(rootPath);
        if (linkTarget != null)
        {
            throw new InvalidOperationException("Workspace or trace root cannot be a symlink or junction.");
        }
    }

    private static string? GetLinkTargetOrThrow(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory)
                ? new DirectoryInfo(path)
                : new FileInfo(path);

            if (!attributes.HasFlag(FileAttributes.ReparsePoint) && string.IsNullOrWhiteSpace(info.LinkTarget))
            {
                return null;
            }

            var finalTarget = info.ResolveLinkTarget(returnFinalTarget: true);
            if (finalTarget != null)
            {
                return finalTarget.FullName;
            }

            if (string.IsNullOrWhiteSpace(info.LinkTarget))
            {
                throw new InvalidOperationException($"Unable to resolve reparse-point target: {path}");
            }

            var linkParent = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
            return Path.IsPathFullyQualified(info.LinkTarget)
                ? Path.GetFullPath(info.LinkTarget)
                : Path.GetFullPath(Path.Combine(linkParent, info.LinkTarget));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Unable to verify path link safety: {path}", ex);
        }
    }

    private static StringComparison GetComparison()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
