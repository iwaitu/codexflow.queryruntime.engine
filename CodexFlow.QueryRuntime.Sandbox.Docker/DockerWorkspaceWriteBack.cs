using System.Security.Cryptography;
using System.Text;
using CodexFlow.QueryRuntime.Abstractions;

namespace CodexFlow.QueryRuntime.Sandbox.Docker;

internal sealed record DockerWorkspaceFileSnapshot(long Length, string Sha256);

internal sealed record DockerWorkspaceChange(
    string RelativePath,
    long SizeBytes,
    string Sha256,
    bool IsNew);

internal sealed record DockerWorkspaceChangeManifest(
    IReadOnlyList<DockerWorkspaceChange> Changes,
    long TotalBytes,
    string BoundedDiff,
    bool DiffTruncated);

internal static class DockerWorkspaceWriteBack
{
    public static IReadOnlyDictionary<string, DockerWorkspaceFileSnapshot> CaptureBaseline(string root)
        => DockerSandboxRunner.EnumerateWorkspaceFiles(root)
            .ToDictionary(
                file => NormalizeRelativePath(Path.GetRelativePath(root, file)),
                CreateSnapshot,
                GetPathComparer());

    public static void ValidateStagedBaseline(
        string stagedRoot,
        IReadOnlyDictionary<string, DockerWorkspaceFileSnapshot> baseline)
    {
        var staged = CaptureBaseline(stagedRoot);
        if (staged.Count != baseline.Count ||
            baseline.Any(pair => !staged.TryGetValue(pair.Key, out var copied) || copied != pair.Value))
        {
            throw new InvalidOperationException(
                "Workspace changed while the Docker staging snapshot was being created; retry the operation.");
        }
    }

    public static DockerWorkspaceChangeManifest Apply(
        string stagedRoot,
        string destinationRoot,
        IReadOnlyDictionary<string, DockerWorkspaceFileSnapshot> baseline,
        DockerSandboxOptions options,
        Action<int>? beforeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        stagedRoot = Path.GetFullPath(stagedRoot);
        destinationRoot = Path.GetFullPath(destinationRoot);
        RejectUnsafeEntries(stagedRoot);

        var staged = DockerSandboxRunner.EnumerateWorkspaceFiles(stagedRoot)
            .ToDictionary(
                file => NormalizeRelativePath(Path.GetRelativePath(stagedRoot, file)),
                file => new StagedFile(file, CreateSnapshot(file)),
                GetPathComparer());

        var deleted = baseline.Keys.Where(path => !staged.ContainsKey(path)).Order(StringComparer.Ordinal).ToArray();
        if (deleted.Length > 0)
        {
            throw new InvalidOperationException(
                $"Docker workspace write-back rejected {deleted.Length} deletion(s); deletion write-back is disabled.");
        }

        var changes = staged
            .Where(pair => !baseline.TryGetValue(pair.Key, out var original) || original != pair.Value.Snapshot)
            .Select(pair => new DockerWorkspaceChange(
                pair.Key,
                pair.Value.Snapshot.Length,
                pair.Value.Snapshot.Sha256,
                IsNew: !baseline.ContainsKey(pair.Key)))
            .OrderBy(static change => change.RelativePath, StringComparer.Ordinal)
            .ToArray();

        ValidateManifest(changes, options);
        var manifest = BuildManifest(changes, options);
        using var writeBackLock = AcquireWriteBackLock(destinationRoot);
        ValidateDestinations(stagedRoot, destinationRoot, staged, baseline, changes);
        ApplyTransaction(stagedRoot, destinationRoot, staged, baseline, changes, beforeCommit);
        return manifest;
    }

    private static void ValidateOptions(DockerSandboxOptions options)
    {
        if (options.MaxWriteBackFileCount <= 0 || options.MaxWriteBackFileBytes <= 0 ||
            options.MaxWriteBackTotalBytes <= 0 || options.MaxWriteBackDiffBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Docker write-back limits must be positive.");
        }
    }

    private static DockerWorkspaceChangeManifest BuildManifest(
        IReadOnlyList<DockerWorkspaceChange> changes,
        DockerSandboxOptions options)
    {
        var diff = new StringBuilder();
        var diffBytes = 0;
        foreach (var change in changes)
        {
            var line = $"{(change.IsNew ? 'A' : 'M')} {change.RelativePath} {change.SizeBytes} {change.Sha256}\n";
            var lineBytes = Encoding.UTF8.GetByteCount(line);
            if (lineBytes > options.MaxWriteBackDiffBytes - diffBytes)
            {
                return new DockerWorkspaceChangeManifest(
                    changes,
                    changes.Sum(static item => item.SizeBytes),
                    diff.ToString(),
                    DiffTruncated: true);
            }

            diff.Append(line);
            diffBytes += lineBytes;
        }

        return new DockerWorkspaceChangeManifest(
            changes,
            changes.Sum(static item => item.SizeBytes),
            diff.ToString(),
            DiffTruncated: false);
    }

    private static void ValidateManifest(
        IReadOnlyList<DockerWorkspaceChange> changes,
        DockerSandboxOptions options)
    {
        if (changes.Count > options.MaxWriteBackFileCount)
        {
            throw new InvalidOperationException(
                $"Docker workspace write-back exceeds the {options.MaxWriteBackFileCount} file limit.");
        }

        long totalBytes = 0;
        foreach (var change in changes)
        {
            if (change.SizeBytes > options.MaxWriteBackFileBytes)
            {
                throw new InvalidOperationException(
                    $"Docker workspace write-back file exceeds the {options.MaxWriteBackFileBytes} byte limit: {change.RelativePath}");
            }

            totalBytes = checked(totalBytes + change.SizeBytes);
            if (totalBytes > options.MaxWriteBackTotalBytes)
            {
                throw new InvalidOperationException(
                    $"Docker workspace write-back exceeds the {options.MaxWriteBackTotalBytes} byte aggregate limit.");
            }
        }
    }

    private static void ValidateDestinations(
        string stagedRoot,
        string destinationRoot,
        IReadOnlyDictionary<string, StagedFile> staged,
        IReadOnlyDictionary<string, DockerWorkspaceFileSnapshot> baseline,
        IReadOnlyList<DockerWorkspaceChange> changes)
    {
        foreach (var change in changes)
        {
            var source = QueryRuntimePathSafety.ResolveUnderRoot(stagedRoot, change.RelativePath);
            var destination = QueryRuntimePathSafety.ResolveUnderRoot(destinationRoot, change.RelativePath);
            QueryRuntimePathSafety.RejectProtectedWorkspacePath(destinationRoot, destination, "written by Docker");
            if (!string.Equals(source, staged[change.RelativePath].Path, GetPathComparison()))
            {
                throw new InvalidOperationException("Docker workspace staged path resolution changed unexpectedly.");
            }

            if (baseline.TryGetValue(change.RelativePath, out var original))
            {
                if (!File.Exists(destination) || CreateSnapshot(destination) != original)
                {
                    throw new InvalidOperationException(
                        $"Docker workspace write-back conflict: host file changed during execution: {change.RelativePath}");
                }
            }
            else if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new InvalidOperationException(
                    $"Docker workspace write-back conflict: host path appeared during execution: {change.RelativePath}");
            }
        }
    }

    private static void ApplyTransaction(
        string stagedRoot,
        string destinationRoot,
        IReadOnlyDictionary<string, StagedFile> staged,
        IReadOnlyDictionary<string, DockerWorkspaceFileSnapshot> baseline,
        IReadOnlyList<DockerWorkspaceChange> changes,
        Action<int>? beforeCommit)
    {
        var prepared = new List<PreparedChange>();
        try
        {
            foreach (var change in changes)
            {
                var destination = QueryRuntimePathSafety.ResolveUnderRoot(destinationRoot, change.RelativePath);
                var parent = Path.GetDirectoryName(destination)!;
                Directory.CreateDirectory(parent);
                destination = QueryRuntimePathSafety.ResolveUnderRoot(destinationRoot, change.RelativePath);
                QueryRuntimePathSafety.RejectProtectedWorkspacePath(destinationRoot, destination, "written by Docker");
                var temp = Path.Combine(parent, $".{Path.GetFileName(destination)}.qre-{Guid.NewGuid():N}.tmp");
                File.Copy(staged[change.RelativePath].Path, temp, overwrite: false);
                var copied = CreateSnapshot(temp);
                if (copied.Length != change.SizeBytes ||
                    !string.Equals(copied.Sha256, change.Sha256, StringComparison.Ordinal))
                {
                    throw new IOException($"Docker workspace staged file changed while preparing write-back: {change.RelativePath}");
                }

                prepared.Add(new PreparedChange(destination, temp, BackupPath: null, change.IsNew));
            }

            for (var i = 0; i < prepared.Count; i++)
            {
                beforeCommit?.Invoke(i);
                var item = prepared[i];
                ValidatePreparedChange(
                    stagedRoot,
                    destinationRoot,
                    staged,
                    baseline,
                    changes[i],
                    item);
                if (!item.IsNew)
                {
                    var backup = item.Destination + $".qre-{Guid.NewGuid():N}.bak";
                    File.Replace(item.TempPath, item.Destination, backup, ignoreMetadataErrors: true);
                    item = item with { BackupPath = backup, Applied = true };
                    prepared[i] = item;
                    if (CreateSnapshot(backup) != baseline[changes[i].RelativePath])
                    {
                        throw new InvalidOperationException(
                            $"Docker workspace write-back conflict at atomic replace: {changes[i].RelativePath}");
                    }
                }
                else
                {
                    File.Move(item.TempPath, item.Destination);
                    item = item with { Applied = true };
                    prepared[i] = item;
                }
            }

            foreach (var item in prepared)
            {
                DeleteFileQuietly(item.BackupPath);
            }
        }
        catch
        {
            foreach (var item in prepared.AsEnumerable().Reverse())
            {
                if (item.Applied)
                {
                    DeleteFileQuietly(item.Destination);
                }

                if (item.BackupPath != null && File.Exists(item.BackupPath))
                {
                    File.Move(item.BackupPath, item.Destination, overwrite: true);
                }

                DeleteFileQuietly(item.TempPath);
            }

            throw;
        }
    }

    private static void ValidatePreparedChange(
        string stagedRoot,
        string destinationRoot,
        IReadOnlyDictionary<string, StagedFile> staged,
        IReadOnlyDictionary<string, DockerWorkspaceFileSnapshot> baseline,
        DockerWorkspaceChange change,
        PreparedChange prepared)
    {
        var source = QueryRuntimePathSafety.ResolveUnderRoot(stagedRoot, change.RelativePath);
        if (!string.Equals(source, staged[change.RelativePath].Path, GetPathComparison()) ||
            CreateSnapshot(source) != staged[change.RelativePath].Snapshot)
        {
            throw new IOException($"Docker workspace staged file changed before commit: {change.RelativePath}");
        }

        var destination = QueryRuntimePathSafety.ResolveUnderRoot(destinationRoot, change.RelativePath);
        QueryRuntimePathSafety.RejectProtectedWorkspacePath(destinationRoot, destination, "written by Docker");
        if (!string.Equals(destination, prepared.Destination, GetPathComparison()))
        {
            throw new InvalidOperationException("Docker workspace destination resolution changed before commit.");
        }

        if (baseline.TryGetValue(change.RelativePath, out var original))
        {
            if (!File.Exists(destination) || CreateSnapshot(destination) != original)
            {
                throw new InvalidOperationException(
                    $"Docker workspace write-back conflict at commit: {change.RelativePath}");
            }
        }
        else if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new InvalidOperationException(
                $"Docker workspace write-back conflict at commit: {change.RelativePath}");
        }

        var preparedSnapshot = CreateSnapshot(prepared.TempPath);
        if (preparedSnapshot.Length != change.SizeBytes ||
            !string.Equals(preparedSnapshot.Sha256, change.Sha256, StringComparison.Ordinal))
        {
            throw new IOException($"Docker workspace prepared file changed before commit: {change.RelativePath}");
        }
    }

    private static FileStream AcquireWriteBackLock(string destinationRoot)
    {
        var qreDirectory = QueryRuntimePathSafety.ResolveUnderRoot(destinationRoot, ".qre");
        Directory.CreateDirectory(qreDirectory);
        qreDirectory = QueryRuntimePathSafety.ResolveUnderRoot(destinationRoot, ".qre");
        var lockPath = QueryRuntimePathSafety.ResolveUnderRoot(qreDirectory, "docker-writeback.lock");
        return new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);
    }

    private static void RejectUnsafeEntries(string stagedRoot)
    {
        var pending = new Stack<string>();
        pending.Push(stagedRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var relative = Path.GetRelativePath(stagedRoot, entry);
                if (DockerSandboxRunner.ShouldSkipWorkspacePath(relative))
                {
                    continue;
                }

                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Device))
                {
                    throw new InvalidOperationException(
                        $"Docker workspace write-back rejects links and device files: {NormalizeRelativePath(relative)}");
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static DockerWorkspaceFileSnapshot CreateSnapshot(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var lengthBefore = stream.Length;
        var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var lengthAfter = stream.Length;
        if (lengthBefore != lengthAfter || stream.Position != lengthAfter)
        {
            throw new IOException($"File changed while it was being inspected: {path}");
        }

        return new DockerWorkspaceFileSnapshot(lengthAfter, digest);
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison GetPathComparison()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static void DeleteFileQuietly(string? path)
    {
        try
        {
            if (path != null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record StagedFile(string Path, DockerWorkspaceFileSnapshot Snapshot);

    private sealed record PreparedChange(
        string Destination,
        string TempPath,
        string? BackupPath,
        bool IsNew,
        bool Applied = false);
}
