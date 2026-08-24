using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using CodexFlow.QueryRuntime.Abstractions;

namespace CodexFlow.QueryRuntime.Experimental;

internal static class TraceStorageSecurity
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void PreparePrivateRoot(string privateRoot, TimeSpan retention)
    {
        if (retention <= TimeSpan.Zero || retention > TimeSpan.FromDays(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(retention),
                "Private diagnostic trace retention must be greater than zero and no more than 30 days.");
        }

        CreatePrivateDirectory(privateRoot);
        var runsRoot = QueryRuntimePathSafety.ResolveUnderRoot(privateRoot, "runs");
        CreatePrivateDirectory(runsRoot);
        PruneExpiredRuns(runsRoot, DateTimeOffset.UtcNow - retention);
    }

    public static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            CreateOrRestrictWindowsDirectory(path);
            return;
        }

        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, PrivateDirectoryMode);
        VerifyUnixMode(path, PrivateDirectoryMode, "directory");
    }

    public static void RestrictPrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictWindowsFile(path);
            return;
        }

        File.SetUnixFileMode(path, PrivateFileMode);
        VerifyUnixMode(path, PrivateFileMode, "file");
    }

    private static void PruneExpiredRuns(string runsRoot, DateTimeOffset cutoff)
    {
        foreach (var candidate in Directory.EnumerateDirectories(runsRoot))
        {
            var resolved = QueryRuntimePathSafety.ResolveUnderRoot(runsRoot, Path.GetFileName(candidate));
            var info = new DirectoryInfo(resolved);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("Private trace retention encountered a linked run directory.");
            }

            if (info.LastWriteTimeUtc >= cutoff.UtcDateTime)
            {
                continue;
            }

            var manifest = JsonlTraceStore.TryReadManifest(info.FullName);
            if (!IsTerminalPrivateManifest(manifest))
            {
                continue;
            }

            RejectReparsePointsRecursively(info);
            Directory.Delete(info.FullName, recursive: true);
        }
    }

    private static bool IsTerminalPrivateManifest(System.Text.Json.JsonElement? manifest)
    {
        if (!manifest.HasValue ||
            !manifest.Value.TryGetProperty("Type", out var type) ||
            !string.Equals(type.GetString(), "qre.run.manifest", StringComparison.Ordinal) ||
            !manifest.Value.TryGetProperty("DataMode", out var dataMode) ||
            !string.Equals(dataMode.GetString(), QueryRuntimeTraceDataMode.PrivateDiagnostic.ToString(), StringComparison.Ordinal) ||
            !manifest.Value.TryGetProperty("Status", out var status))
        {
            return false;
        }

        return status.GetString() is "completed" or "failed";
    }

    private static void RejectReparsePointsRecursively(DirectoryInfo root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidOperationException(
                        $"Private trace retention refuses to delete a tree containing links: {entry.FullName}");
                }

                if (entry is DirectoryInfo directory)
                {
                    pending.Push(directory);
                }
            }
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void VerifyUnixMode(string path, UnixFileMode expected, string kind)
    {
        var actual = File.GetUnixFileMode(path);
        if (actual != expected)
        {
            throw new UnauthorizedAccessException(
                $"Private trace {kind} permissions are not owner-only: {path}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateOrRestrictWindowsDirectory(string path)
    {
        var user = GetCurrentWindowsUser();
        var security = CreatePrivateDirectorySecurity(user);
        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            info.Create(security);
        }
        else
        {
            info.SetAccessControl(security);
        }

        VerifyWindowsAcl(info.GetAccessControl(AccessControlSections.Access), user, path);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindowsFile(string path)
    {
        var user = GetCurrentWindowsUser();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        var info = new FileInfo(path);
        info.SetAccessControl(security);
        VerifyWindowsAcl(info.GetAccessControl(AccessControlSections.Access), user, path);
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity CreatePrivateDirectorySecurity(SecurityIdentifier user)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier GetCurrentWindowsUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ??
               throw new UnauthorizedAccessException("The current Windows identity has no security identifier.");
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsAcl(FileSystemSecurity security, SecurityIdentifier user, string path)
    {
        if (!security.AreAccessRulesProtected)
        {
            throw new UnauthorizedAccessException($"Private trace ACL still inherits permissions: {path}");
        }

        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier));
        var fileSystemRules = rules.Cast<FileSystemAccessRule>().ToArray();
        if (fileSystemRules.Length == 0)
        {
            throw new UnauthorizedAccessException($"Private trace ACL has no current-user access rule: {path}");
        }

        foreach (var rule in fileSystemRules)
        {
            if (rule.IsInherited ||
                rule.AccessControlType != AccessControlType.Allow ||
                !user.Equals(rule.IdentityReference))
            {
                throw new UnauthorizedAccessException($"Private trace ACL grants an unexpected identity: {path}");
            }
        }
    }
}
