using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Models.Diagnostics;

namespace CodexFlow.QueryRuntime.Cli.Diagnostics;

/// <summary>
/// Independent sidecar storage for projected outbound diagnostics under
/// <c>.qre/v2/diagnostics/&lt;run-id&gt;/</c>. Writes go through a bounded queue
/// (record and byte limits) to a background writer; a full queue, run quota or
/// disk failure drops records and is reported as incomplete evidence without
/// affecting the model call. Existing audit/checkpoint files never depend on it.
/// </summary>
internal sealed class QreDiagnosticsStore : IQreOutboundDiagnosticSink, IAsyncDisposable
{
    public const string ManifestFileName = "manifest.json";
    public const string EventsFileName = "events.jsonl";
    public const string LocalIndexFileName = "local-index.json";

    private static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<byte[]> _queue;
    private readonly QreOutboundDiagnosticsOptions _options;
    private readonly Task _writer;
    private readonly DateTimeOffset _createdUtc;
    private long _pendingBytes;
    private long _writtenBytes;
    private long _written;
    private long _queueDropped;
    private long _quotaDropped;
    private long _writeFailures;
    private volatile bool _writeFailed;
    private int _completed;

    private QreDiagnosticsStore(string runDirectory, string runId, QreOutboundDiagnosticsOptions options)
    {
        RunDirectory = runDirectory;
        DiagnosticRunId = runId;
        _options = options;
        _createdUtc = DateTimeOffset.UtcNow;
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.MaxPendingRecords)
        {
            SingleReader = true,
            SingleWriter = false,
            // TryWrite remains non-blocking, but must return false when full so
            // the byte reservation and drop counters can be updated exactly once.
            FullMode = BoundedChannelFullMode.Wait
        });
        _writer = Task.Run(WriteLoopAsync);
    }

    public string RunDirectory { get; }

    public string DiagnosticRunId { get; }

    public string EventsPath => Path.Combine(RunDirectory, EventsFileName);

    public string ManifestPath => Path.Combine(RunDirectory, ManifestFileName);

    public static string DiagnosticsRoot(string workspace)
        => QueryRuntimePathSafety.ResolveUnderRoot(Path.GetFullPath(workspace), Path.Combine(".qre", "v2", "diagnostics"));

    /// <summary>
    /// Creates a new private run directory after applying retention and quotas to
    /// earlier diagnostic runs. Cleanup is confined to the diagnostics root and
    /// never follows links.
    /// </summary>
    public static QreDiagnosticsStore Create(
        string workspace,
        QreOutboundDiagnosticsOptions options,
        string segmentId,
        QreDiagnosticsCoverage coverage)
    {
        options.Validate();
        var root = DiagnosticsRoot(workspace);
        QueryRuntimePathSafety.RejectWorkspaceLinks(Path.GetFullPath(workspace), root, "store diagnostics");
        QreDiagnosticsFileSecurity.CreatePrivateDirectory(root);
        Prune(root, options);
        var runId = $"diag-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}";
        var runDirectory = QueryRuntimePathSafety.ResolveUnderRoot(root, runId);
        QreDiagnosticsFileSecurity.CreatePrivateDirectory(runDirectory);
        return CreateAt(runDirectory, options, segmentId, coverage);
    }

    /// <summary>Opens a store in an existing private run directory.</summary>
    internal static QreDiagnosticsStore CreateAt(
        string runDirectory,
        QreOutboundDiagnosticsOptions options,
        string segmentId,
        QreDiagnosticsCoverage coverage)
    {
        var store = new QreDiagnosticsStore(runDirectory, Path.GetFileName(runDirectory), options);
        store.WriteManifest(store.BuildManifest(segmentId, coverage, "in_progress", null, null));
        return store;
    }

    public bool TryWrite(QreOutboundDiagnosticRecord record, ReadOnlyMemory<byte> utf8Json)
    {
        if (_writeFailed || Volatile.Read(ref _completed) != 0)
        {
            Interlocked.Increment(ref _queueDropped);
            return false;
        }
        var length = utf8Json.Length + 1;
        if (Interlocked.Add(ref _pendingBytes, length) > _options.MaxPendingBytes)
        {
            Interlocked.Add(ref _pendingBytes, -length);
            Interlocked.Increment(ref _queueDropped);
            return false;
        }
        var line = new byte[length];
        utf8Json.Span.CopyTo(line);
        line[^1] = (byte)'\n';
        if (!_queue.Writer.TryWrite(line))
        {
            Interlocked.Add(ref _pendingBytes, -length);
            Interlocked.Increment(ref _queueDropped);
            return false;
        }
        return true;
    }

    /// <summary>Writes the restricted local index linking this run to local audit evidence.</summary>
    public void WriteLocalIndex(QreDiagnosticsLocalIndex index)
    {
        try
        {
            var path = Path.Combine(RunDirectory, LocalIndexFileName);
            WriteAtomically(path, JsonSerializer.SerializeToUtf8Bytes(index, QreDiagnosticsJsonContext.Default.QreDiagnosticsLocalIndex));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _writeFailures);
        }
    }

    /// <summary>
    /// Stops accepting records, flushes within a bounded time, and atomically writes
    /// the final manifest. Returns the manifest that was written (or attempted).
    /// </summary>
    public async Task<QreDiagnosticsManifest> CompleteAsync(
        QreOutboundDiagnostics diagnostics,
        QreDiagnosticsCoverage coverage,
        string? entryOutcome = null,
        TimeSpan? flushTimeout = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _queue.Writer.TryComplete();
        }
        var flushTimedOut = false;
        try
        {
            await _writer.WaitAsync(flushTimeout ?? DefaultFlushTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            flushTimedOut = true;
        }

        var counters = diagnostics.GetCounters();
        var snapshot = new QreDiagnosticsCountersSnapshot
        {
            RecordsEmitted = counters.RecordsEmitted,
            RecordsWritten = Interlocked.Read(ref _written),
            RecordsDropped = counters.RecordsDropped + Interlocked.Read(ref _quotaDropped) + Interlocked.Read(ref _writeFailures),
            QueueDropped = Interlocked.Read(ref _queueDropped),
            QuotaDropped = Interlocked.Read(ref _quotaDropped),
            WriteFailures = Interlocked.Read(ref _writeFailures),
            RecordsOversized = counters.RecordsOversized,
            ProjectionFailures = counters.ProjectionFailures,
            UncorrelatedHttpAttempts = counters.UncorrelatedHttpAttempts,
            ModelCallsStarted = counters.ModelCallsStarted,
            ModelCallsEnded = counters.ModelCallsEnded,
            HttpAttemptsStarted = counters.HttpAttemptsStarted,
            HttpAttemptsEnded = counters.HttpAttemptsEnded,
            FlushTimedOut = flushTimedOut
        };
        var incomplete = counters.EvidenceIncomplete || flushTimedOut || snapshot.QuotaDropped > 0 ||
                         snapshot.WriteFailures > 0 || snapshot.RecordsWritten != counters.RecordsEmitted;
        snapshot = snapshot with { EvidenceIncomplete = incomplete };
        var manifest = BuildManifest(
            diagnostics.SegmentId,
            coverage,
            incomplete ? "incomplete" : "complete",
            snapshot,
            entryOutcome);
        WriteManifest(manifest);
        return manifest;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _queue.Writer.TryComplete();
        }
        try
        {
            await _writer.WaitAsync(DefaultFlushTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    private async Task WriteLoopAsync()
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(EventsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
            QreDiagnosticsFileSecurity.RestrictPrivateFile(EventsPath);
            await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                Interlocked.Add(ref _pendingBytes, -line.Length);
                if (_writeFailed)
                {
                    Interlocked.Increment(ref _writeFailures);
                    continue;
                }
                if (Interlocked.Read(ref _writtenBytes) + line.Length > _options.MaxRunBytes)
                {
                    Interlocked.Increment(ref _quotaDropped);
                    continue;
                }
                try
                {
                    await stream.WriteAsync(line).ConfigureAwait(false);
                    // Flush per record: complete JSONL lines survive a process crash.
                    await stream.FlushAsync().ConfigureAwait(false);
                    Interlocked.Add(ref _writtenBytes, line.Length);
                    Interlocked.Increment(ref _written);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _writeFailed = true;
                    Interlocked.Increment(ref _writeFailures);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writeFailed = true;
            Interlocked.Increment(ref _writeFailures);
            // Drain so producers observe drops instead of blocking.
            while (_queue.Reader.TryRead(out var line))
            {
                Interlocked.Add(ref _pendingBytes, -line.Length);
                Interlocked.Increment(ref _writeFailures);
            }
        }
        finally
        {
            if (stream != null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private QreDiagnosticsManifest BuildManifest(
        string segmentId,
        QreDiagnosticsCoverage coverage,
        string status,
        QreDiagnosticsCountersSnapshot? counters,
        string? entryOutcome)
        => new()
        {
            ManifestSchema = QreDiagnosticsManifest.CurrentSchema,
            EventSchema = QreOutboundDiagnosticSchema.SchemaVersion,
            ProjectionPolicyVersion = _options.ProjectionPolicyVersion,
            NormalizerVersion = QreOutboundDiagnosticSchema.NormalizerVersion,
            DiagnosticRunId = DiagnosticRunId,
            SegmentId = segmentId,
            Mode = _options.Mode == QreOutboundDiagnosticMode.Structure ? "structure" : "metadata",
            CompletionStatus = status,
            CreatedUtc = _createdUtc,
            CompletedUtc = status == "in_progress" ? null : DateTimeOffset.UtcNow,
            Coverage = coverage,
            Quotas = new QreDiagnosticsQuotas
            {
                MaxRequestCaptureBytes = _options.MaxRequestCaptureBytes,
                MaxRecordBytes = _options.MaxRecordBytes,
                MaxRunBytes = _options.MaxRunBytes,
                MaxPendingRecords = _options.MaxPendingRecords,
                MaxPendingBytes = _options.MaxPendingBytes,
                MaxJsonDepth = _options.MaxJsonDepth,
                RetentionDays = (int)Math.Ceiling(_options.Retention.TotalDays)
            },
            Counters = counters,
            EntryOutcome = entryOutcome
        };

    private void WriteManifest(QreDiagnosticsManifest manifest)
    {
        try
        {
            WriteAtomically(ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, QreDiagnosticsJsonContext.Default.QreDiagnosticsManifest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _writeFailures);
        }
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temporary, bytes);
        QreDiagnosticsFileSecurity.RestrictPrivateFile(temporary);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Applies retention, run-count and total-size quotas, oldest first.</summary>
    internal static void Prune(string root, QreOutboundDiagnosticsOptions options)
    {
        var cutoff = DateTimeOffset.UtcNow - options.Retention;
        var runs = new List<(string Path, DateTime Created, long Bytes)>();
        foreach (var candidate in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(candidate);
            if (!name.StartsWith("diag-", StringComparison.Ordinal))
            {
                continue;
            }
            var resolved = QueryRuntimePathSafety.ResolveUnderRoot(root, name);
            var info = new DirectoryInfo(resolved);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || ContainsLinks(info))
            {
                continue;
            }
            runs.Add((resolved, info.CreationTimeUtc, info.EnumerateFiles().Sum(static file => file.Length)));
        }

        var keep = new List<(string Path, DateTime Created, long Bytes)>();
        foreach (var run in runs.OrderBy(static run => run.Created))
        {
            if (run.Created < cutoff.UtcDateTime)
            {
                TryDelete(run.Path);
            }
            else
            {
                keep.Add(run);
            }
        }
        // Reserve room for the run about to be created.
        var total = keep.Sum(static run => run.Bytes);
        while (keep.Count > 0 &&
               (keep.Count + 1 > options.MaxStoredRuns || total + options.MaxRunBytes > options.MaxTotalStorageBytes))
        {
            total -= keep[0].Bytes;
            TryDelete(keep[0].Path);
            keep.RemoveAt(0);
        }
    }

    private static bool ContainsLinks(DirectoryInfo directory)
        => directory.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly)
            .Any(static entry => entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry is DirectoryInfo);

    private static void TryDelete(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                File.Delete(file);
            }
            Directory.Delete(path, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Private directory/file permissions for diagnostic storage.</summary>
internal static class QreDiagnosticsFileSecurity
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeValue = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            CreateWindowsDirectory(path);
            return;
        }
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, DirectoryMode);
    }

    public static void RestrictPrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictWindowsFile(path);
            return;
        }
        File.SetUnixFileMode(path, FileModeValue);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsDirectory(string path)
    {
        var user = CurrentUser();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        var info = new DirectoryInfo(path);
        if (info.Exists)
        {
            info.SetAccessControl(security);
        }
        else
        {
            info.Create(security);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindowsFile(string path)
    {
        var user = CurrentUser();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new UnauthorizedAccessException("The current Windows identity has no SID.");
    }
}
