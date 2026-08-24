using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.QueryRuntime.Engine.V2;

public sealed record RuntimeAuditStoreOptions
{
    public RuntimeAuditDataMode DataMode { get; init; } = RuntimeAuditDataMode.PublicRedacted;

    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);

    public int MaxStoredRuns { get; init; } = 100;

    public long MaxTotalStorageBytes { get; init; } = 512L * 1024 * 1024;

    public int MaxEventCount { get; init; } = 100_000;

    public int MaxLineBytes { get; init; } = 4 * 1024 * 1024;

    public long MaxRunBytes { get; init; } = 64L * 1024 * 1024;

    public long MaxBlobBytes { get; init; } = 16L * 1024 * 1024;

    public long MaxTotalBlobBytes { get; init; } = 64L * 1024 * 1024;

    public int InlinePayloadBytes { get; init; } = 16 * 1024;

    public int MaxJsonDepth { get; init; } = 64;

    public RuntimeAuditReplayCapability ReplayCapability =>
        DataMode == RuntimeAuditDataMode.PublicRedacted
            ? RuntimeAuditReplayCapability.SummaryOnly
            : RuntimeAuditReplayCapability.Recorded;

    internal void Validate()
    {
        if (Retention <= TimeSpan.Zero || Retention > TimeSpan.FromDays(30) ||
            MaxStoredRuns <= 0 || MaxTotalStorageBytes <= 0 || MaxEventCount <= 0 ||
            MaxLineBytes <= 0 || MaxRunBytes <= 0 || MaxBlobBytes <= 0 ||
            MaxTotalBlobBytes <= 0 || InlinePayloadBytes <= 0 || MaxJsonDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RuntimeAuditStoreOptions), "C6 audit limits must be positive and retention must not exceed 30 days.");
        }
        if (InlinePayloadBytes > MaxLineBytes || MaxLineBytes > MaxRunBytes ||
            MaxBlobBytes > MaxTotalBlobBytes || MaxTotalBlobBytes > MaxRunBytes)
        {
            throw new ArgumentException("C6 audit limits are inconsistent.", nameof(RuntimeAuditStoreOptions));
        }
    }
}

/// <summary>
/// Bounded JSONL durable audit adapter. Public mode stores only an explicit
/// allow-list summary. Sanitized/private payloads may be content-addressed into
/// bounded blobs and are verified before recorded replay.
/// </summary>
public sealed class RuntimeJsonlAuditStore : IRuntimeAuditSink, IAsyncDisposable
{
    private const string ManifestType = "qre.v2.audit.manifest";
    private const string Redacted = "[redacted]";
    private const int ManifestWriteReserveBytes = 64 * 1024;
    private static readonly SemaphoreSlim StorageLock = new(1, 1);
    private readonly RuntimeAuditStoreOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _runsRoot;
    private RuntimeAuditManifest _manifest;
    private long _eventBytes;
    private long _blobBytes;
    private int _eventCount;
    private bool _disposed;

    private RuntimeJsonlAuditStore(
        string runId,
        string runDirectory,
        string runsRoot,
        RuntimeAuditStoreOptions options,
        TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
        RunId = runId;
        RunDirectory = runDirectory;
        _runsRoot = runsRoot;
        AuditFilePath = QueryRuntimePathSafety.ResolveUnderRoot(runDirectory, "audit.v1.jsonl");
        ManifestPath = QueryRuntimePathSafety.ResolveUnderRoot(runDirectory, "manifest.json");
        PrepareDirectory(runDirectory, options.DataMode == RuntimeAuditDataMode.PrivateDiagnostic);
        var stream = new FileStream(
            AuditFilePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _writer = new StreamWriter(stream, new UTF8Encoding(false, true));
        if (options.DataMode == RuntimeAuditDataMode.PrivateDiagnostic)
        {
            RuntimeAuditFileSecurity.RestrictPrivateFile(AuditFilePath);
        }
        var now = timeProvider.GetUtcNow();
        _manifest = new RuntimeAuditManifest(
            RuntimeAuditSchema.CurrentVersion,
            ManifestType,
            runId,
            options.DataMode,
            options.ReplayCapability,
            "active",
            0,
            0,
            0,
            null,
            now,
            now);
        WriteManifestAtomic();
    }

    public string RunId { get; }

    public string RunDirectory { get; }

    public string AuditFilePath { get; }

    public string ManifestPath { get; }

    public int EventCount => _eventCount;

    public long EventBytes => _eventBytes;

    public long BlobBytes => _blobBytes;

    public static RuntimeJsonlAuditStore Create(
        string workspacePath,
        string requestedRunId,
        RuntimeAuditStoreOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedRunId);
        options ??= new RuntimeAuditStoreOptions();
        options.Validate();
        timeProvider ??= TimeProvider.System;

        var workspaceRoot = Path.GetFullPath(workspacePath);
        var qreRoot = QueryRuntimePathSafety.ResolveUnderRoot(workspaceRoot, ".qre");
        var v2Root = QueryRuntimePathSafety.ResolveUnderRoot(qreRoot, "v2");
        var storageRoot = options.DataMode == RuntimeAuditDataMode.PrivateDiagnostic
            ? QueryRuntimePathSafety.ResolveUnderRoot(v2Root, "private")
            : v2Root;
        PrepareDirectory(storageRoot, options.DataMode == RuntimeAuditDataMode.PrivateDiagnostic);
        var runsRoot = QueryRuntimePathSafety.ResolveUnderRoot(storageRoot, "runs");
        PrepareDirectory(runsRoot, options.DataMode == RuntimeAuditDataMode.PrivateDiagnostic);
        StorageLock.Wait();
        try
        {
            PruneTerminalRuns(runsRoot, options, timeProvider.GetUtcNow());
            EnsureTotalStorageQuota(runsRoot, options, ManifestWriteReserveBytes);

            var persistedRunId = options.DataMode switch
            {
                RuntimeAuditDataMode.PublicRedacted => $"public-{Guid.NewGuid():N}",
                RuntimeAuditDataMode.PrivateDiagnostic => $"private-{Guid.NewGuid():N}",
                _ => NormalizeRunId(requestedRunId)
            };
            var runDirectory = QueryRuntimePathSafety.ResolveUnderRoot(runsRoot, persistedRunId);
            if (Directory.Exists(runDirectory))
            {
                throw new IOException($"C6 audit run already exists: {persistedRunId}");
            }
            return new RuntimeJsonlAuditStore(persistedRunId, runDirectory, runsRoot, options, timeProvider);
        }
        finally
        {
            StorageLock.Release();
        }
    }

    public async ValueTask OnEventAsync(RuntimeAuditEnvelope auditEvent, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(auditEvent);
        await StorageLock.WaitAsync(ct).ConfigureAwait(false);
        var writeLockAcquired = false;
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            writeLockAcquired = true;
            if (auditEvent.SchemaVersion != RuntimeAuditSchema.CurrentVersion)
            {
                throw new InvalidDataException($"Unsupported C6 audit schema: {auditEvent.SchemaVersion}.");
            }
            if (_eventCount >= _options.MaxEventCount)
            {
                throw new InvalidDataException($"C6 audit exceeds the {_options.MaxEventCount} event quota.");
            }

            var record = CreatePersistedRecord(auditEvent);
            var payloadBytes = record.Payload == null
                ? []
                : JsonSerializer.SerializeToUtf8Bytes(record.Payload, RuntimeAuditJsonContext.Default.RuntimeAuditPayload);
            if (_options.DataMode != RuntimeAuditDataMode.PublicRedacted &&
                payloadBytes.Length > _options.InlinePayloadBytes)
            {
                var blob = WritePayloadBlob(payloadBytes);
                record = record with { Payload = null, PayloadBlob = blob };
            }

            var line = JsonSerializer.SerializeToUtf8Bytes(record, RuntimeAuditJsonContext.Default.RuntimePersistedAuditRecord);
            if (line.Length > _options.MaxLineBytes)
            {
                throw new InvalidDataException($"C6 audit line exceeds the {_options.MaxLineBytes} byte quota.");
            }
            if (checked(_eventBytes + line.Length + 1 + _blobBytes) > _options.MaxRunBytes)
            {
                throw new InvalidDataException($"C6 audit run exceeds the {_options.MaxRunBytes} byte quota.");
            }
            EnsureTotalStorageQuota(_runsRoot, _options, checked(line.Length + 1L + ManifestWriteReserveBytes));

            await _writer.BaseStream.WriteAsync(line, ct).ConfigureAwait(false);
            await _writer.BaseStream.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
            _eventCount++;
            _eventBytes += line.Length + 1;
            _manifest = _manifest with
            {
                Status = auditEvent.Kind == RuntimeAuditEventKind.TurnTerminal
                    ? TerminalStatus(auditEvent.Payload)
                    : _manifest.Status,
                EventCount = _eventCount,
                EventBytes = _eventBytes,
                BlobBytes = _blobBytes,
                TerminationReason = auditEvent.Payload is RuntimeTurnTerminalAuditPayload terminal
                    ? terminal.TerminationReason.ToString()
                    : _manifest.TerminationReason,
                UpdatedAt = _timeProvider.GetUtcNow()
            };
            WriteManifestAtomic();
        }
        catch
        {
            if (_manifest.Status == "active")
            {
                _manifest = _manifest with
                {
                    Status = "failed",
                    TerminationReason = "audit_write_failed",
                    UpdatedAt = _timeProvider.GetUtcNow()
                };
                try
                {
                    WriteManifestAtomic();
                }
                catch (Exception)
                {
                    // Preserve the original audit failure. An unreadable active
                    // manifest is never accepted by the replay reader.
                }
            }
            throw;
        }
        finally
        {
            if (writeLockAcquired)
            {
                _writeLock.Release();
            }
            StorageLock.Release();
        }
    }

    public static string FindLatestAuditFile(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var root = Path.GetFullPath(workspacePath);
        var v2Root = QueryRuntimePathSafety.ResolveUnderRoot(root, Path.Combine(".qre", "v2"));
        var roots = new[]
        {
            QueryRuntimePathSafety.ResolveUnderRoot(v2Root, "runs"),
            QueryRuntimePathSafety.ResolveUnderRoot(v2Root, Path.Combine("private", "runs"))
        };
        var latest = roots.Where(Directory.Exists)
            .SelectMany(EnumerateSafeRunDirectories)
            .Select(directory => (Directory: directory, Manifest: TryReadManifest(directory)))
            .Where(static item => item.Manifest != null)
            .OrderByDescending(static item => item.Manifest!.UpdatedAt)
            .ThenByDescending(static item => item.Directory, StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest.Manifest == null)
        {
            throw new DirectoryNotFoundException($"No C6 v2 audit runs found under {v2Root}.");
        }
        var path = QueryRuntimePathSafety.ResolveUnderRoot(latest.Directory, "audit.v1.jsonl");
        return File.Exists(path) ? path : throw new FileNotFoundException("Latest C6 audit has no JSONL file.", path);
    }

    public static RuntimeAuditRecording Read(
        string auditFilePath,
        RuntimeAuditStoreOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditFilePath);
        options ??= new RuntimeAuditStoreOptions();
        options.Validate();
        var runDirectory = Path.GetDirectoryName(Path.GetFullPath(auditFilePath)) ??
            throw new ArgumentException("Audit path has no run directory.", nameof(auditFilePath));
        var resolved = QueryRuntimePathSafety.ResolveUnderRoot(runDirectory, "audit.v1.jsonl");
        if (!string.Equals(resolved, Path.GetFullPath(auditFilePath), PathComparison()))
        {
            throw new InvalidDataException("C6 audit must be the direct audit.v1.jsonl child of its run directory.");
        }
        var manifest = TryReadManifest(runDirectory, options.MaxJsonDepth) ?? throw new InvalidDataException("C6 audit manifest is missing or invalid.");
        if (manifest.SchemaVersion != RuntimeAuditSchema.CurrentVersion)
        {
            throw new RuntimeAuditReplayException(new RuntimeError(
                RuntimeErrorCategory.SchemaIncompatible,
                "audit_schema_incompatible",
                $"Audit schema {manifest.SchemaVersion} is not supported."));
        }
        if (!string.Equals(manifest.Type, ManifestType, StringComparison.Ordinal) ||
            !string.Equals(manifest.RunId, Path.GetFileName(runDirectory), StringComparison.Ordinal) ||
            manifest.EventCount <= 0 || manifest.EventCount > options.MaxEventCount ||
            manifest.EventBytes <= 0 || manifest.EventBytes > options.MaxRunBytes ||
            manifest.BlobBytes < 0 || manifest.BlobBytes > options.MaxTotalBlobBytes ||
            manifest.Status is not ("completed" or "failed" or "cancelled") ||
            manifest.ReplayCapability != (manifest.DataMode == RuntimeAuditDataMode.PublicRedacted
                ? RuntimeAuditReplayCapability.SummaryOnly
                : RuntimeAuditReplayCapability.Recorded))
        {
            throw new InvalidDataException("C6 audit manifest metadata is invalid or the run is not terminal.");
        }
        var info = new FileInfo(resolved);
        if (!info.Exists || info.Length > options.MaxRunBytes || info.Length != manifest.EventBytes)
        {
            throw new InvalidDataException("C6 audit file is missing, exceeds the read quota, or conflicts with its manifest length.");
        }

        var records = ReadBoundedLines(resolved, options, ct);
        var events = new List<RuntimeAuditEnvelope>(records.Count);
        long totalBlobBytes = 0;
        var verifiedBlobs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record.SchemaVersion != manifest.SchemaVersion || record.DataMode != manifest.DataMode ||
                record.ReplayCapability != manifest.ReplayCapability)
            {
                throw new InvalidDataException("C6 audit record metadata conflicts with its manifest.");
            }
            var payload = record.Payload;
            if (payload == null)
            {
                var blob = record.PayloadBlob ?? throw new InvalidDataException("C6 audit record has neither payload nor blob.");
                var bytes = ReadPayloadBlob(runDirectory, blob, options, verifiedBlobs, ref totalBlobBytes);
                payload = JsonSerializer.Deserialize(bytes, RuntimeAuditJsonContext.Default.RuntimeAuditPayload) ??
                    throw new InvalidDataException("C6 audit blob payload is null.");
            }
            else if (record.PayloadBlob != null)
            {
                throw new InvalidDataException("C6 audit record cannot contain inline and blob payloads together.");
            }
            events.Add(new RuntimeAuditEnvelope(
                record.SchemaVersion,
                record.Sequence,
                new RuntimeAuditEventId(record.EventId),
                record.Timestamp,
                record.Kind,
                new RuntimeSessionId(record.SessionId),
                new RuntimeTurnId(record.TurnId),
                record.StepId == null ? null : new RuntimeStepId(record.StepId),
                record.InvocationId == null ? null : new RuntimeInvocationId(record.InvocationId),
                record.CausationId == null ? null : new RuntimeAuditEventId(record.CausationId),
                record.CorrelationId,
                record.Sensitivity,
                payload));
        }
        if (events.Count != manifest.EventCount)
        {
            throw new InvalidDataException("C6 audit event count does not match its manifest.");
        }
        if (totalBlobBytes != manifest.BlobBytes)
        {
            throw new InvalidDataException("C6 audit blob bytes do not match the manifest.");
        }
        return new RuntimeAuditRecording(manifest.DataMode, manifest.ReplayCapability, Array.AsReadOnly(events.ToArray()), resolved);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await StorageLock.WaitAsync().ConfigureAwait(false);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_manifest.Status == "active")
            {
                _manifest = _manifest with
                {
                    Status = "failed",
                    TerminationReason = "audit_store_disposed_before_terminal",
                    UpdatedAt = _timeProvider.GetUtcNow()
                };
                WriteManifestAtomic();
            }
        }
        finally
        {
            _writeLock.Release();
            StorageLock.Release();
        }
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private RuntimePersistedAuditRecord CreatePersistedRecord(RuntimeAuditEnvelope auditEvent)
    {
        var isPublic = _options.DataMode == RuntimeAuditDataMode.PublicRedacted;
        return new RuntimePersistedAuditRecord(
            auditEvent.SchemaVersion,
            auditEvent.Sequence,
            isPublic ? $"audit:{auditEvent.Sequence}" : auditEvent.EventId.Value,
            auditEvent.Timestamp,
            auditEvent.Kind,
            isPublic ? Redacted : auditEvent.SessionId.Value,
            isPublic ? Redacted : auditEvent.TurnId.Value,
            isPublic ? null : auditEvent.StepId?.Value,
            isPublic ? null : auditEvent.InvocationId?.Value,
            auditEvent.Sequence == 1 ? null : isPublic ? $"audit:{auditEvent.Sequence - 1}" : auditEvent.CausationId?.Value,
            isPublic ? Redacted : auditEvent.CorrelationId,
            isPublic ? RuntimeAuditSensitivity.Public : auditEvent.Sensitivity,
            _options.DataMode,
            _options.ReplayCapability,
            isPublic ? ProjectPublic(auditEvent.Payload) : auditEvent.Payload,
            null);
    }

    private RuntimeAuditBlobReference WritePayloadBlob(byte[] bytes)
    {
        if (bytes.LongLength > _options.MaxBlobBytes || checked(_blobBytes + bytes.LongLength) > _options.MaxTotalBlobBytes)
        {
            throw new InvalidDataException("C6 audit payload exceeds the blob quota.");
        }
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var relative = Path.Combine("blobs", "sha256", digest[..2], digest + ".json");
        var path = QueryRuntimePathSafety.ResolveUnderRoot(RunDirectory, relative);
        PrepareDirectory(Path.GetDirectoryName(path)!, _options.DataMode == RuntimeAuditDataMode.PrivateDiagnostic);
        if (!File.Exists(path))
        {
            if (checked(_eventBytes + _blobBytes + bytes.LongLength) > _options.MaxRunBytes)
            {
                throw new InvalidDataException($"C6 audit run exceeds the {_options.MaxRunBytes} byte quota.");
            }
            EnsureTotalStorageQuota(_runsRoot, _options, checked(bytes.LongLength + ManifestWriteReserveBytes));
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            if (_options.DataMode == RuntimeAuditDataMode.PrivateDiagnostic)
            {
                RuntimeAuditFileSecurity.RestrictPrivateFile(path);
            }
            _blobBytes += bytes.LongLength;
        }
        return new RuntimeAuditBlobReference("sha256", digest, bytes.LongLength, relative.Replace('\\', '/'));
    }

    private void WriteManifestAtomic()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_manifest, RuntimeAuditJsonContext.Default.RuntimeAuditManifest);
        if (bytes.Length > 64 * 1024)
        {
            throw new InvalidDataException("C6 audit manifest exceeds 64 KiB.");
        }
        var temporary = QueryRuntimePathSafety.ResolveUnderRoot(RunDirectory, $"manifest.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temporary, bytes);
        if (_options.DataMode == RuntimeAuditDataMode.PrivateDiagnostic)
        {
            RuntimeAuditFileSecurity.RestrictPrivateFile(temporary);
        }
        File.Move(temporary, ManifestPath, overwrite: true);
        if (_options.DataMode == RuntimeAuditDataMode.PrivateDiagnostic)
        {
            RuntimeAuditFileSecurity.RestrictPrivateFile(ManifestPath);
        }
    }

    private static RuntimeAuditPayload ProjectPublic(RuntimeAuditPayload payload)
        => payload switch
        {
            RuntimeTurnStartedAuditPayload value => new RuntimePublicAuditPayload(
                MessageCount: value.InitialMessages.Count,
                ItemCount: value.InitialMessages.Sum(static message => message.Items.Count),
                HistoryVersion: value.InitialHistoryVersion),
            RuntimeContextPreparedAuditPayload value => new RuntimePublicAuditPayload(
                IncludedItemCount: value.IncludedItemIds.Count,
                OmittedItemCount: value.OmittedItemIds.Count,
                ReplacedItemCount: value.ReplacedItemIds.Count,
                EstimatedTokens: value.EstimatedTokens,
                ReservedToolTokens: value.ReservedToolTokens,
                Compacted: value.Compacted,
                HistoryVersion: value.HistoryVersion),
            RuntimeModelRequestAuditPayload value => new RuntimePublicAuditPayload(
                MessageCount: value.Request.Messages.Count,
                ItemCount: value.Request.Messages.Sum(static message => message.Items.Count),
                ToolCount: value.Request.Tools.Count,
                HistoryVersion: value.Request.HistoryVersion),
            RuntimeModelResponseAuditPayload value => new RuntimePublicAuditPayload(
                ItemCount: value.Output.Items.Count,
                ToolCallCount: value.Output.ToolCalls.Count,
                TextLength: value.Output.Text.Length,
                ReasoningLength: value.Output.Reasoning.Length,
                StopReason: value.Output.StopReason,
                InputTokens: value.Output.Usage.InputTokens,
                OutputTokens: value.Output.Usage.OutputTokens,
                TotalTokens: value.Output.Usage.TotalTokens),
            RuntimeToolObservationAuditPayload value => new RuntimePublicAuditPayload(
                ToolCallCount: 1,
                TextLength: value.Result.Text?.Length ?? 0,
                ToolSuccess: value.Result.Success,
                ErrorCode: value.Result.Error?.Code),
            RuntimeTurnTerminalAuditPayload value => new RuntimePublicAuditPayload(
                MessageCount: value.CanonicalHistory.Count,
                ToolCallCount: value.TotalToolCalls,
                TextLength: value.FinalText.Length,
                TurnStatus: value.Status,
                TerminationReason: value.TerminationReason,
                ErrorCode: value.Error?.Code,
                InputTokens: value.Usage.InputTokens,
                OutputTokens: value.Usage.OutputTokens,
                TotalTokens: value.Usage.TotalTokens,
                TotalSteps: value.TotalSteps,
                ContinuationCount: value.ContinuationCount,
                HistoryVersion: value.HistoryVersion),
            RuntimePublicAuditPayload value => value,
            _ => throw new InvalidOperationException("Unsupported C6 audit payload projection.")
        };

    private static List<RuntimePersistedAuditRecord> ReadBoundedLines(
        string path,
        RuntimeAuditStoreOptions options,
        CancellationToken ct)
    {
        var records = new List<RuntimePersistedAuditRecord>();
        var line = new List<byte>(Math.Min(options.MaxLineBytes, 16 * 1024));
        var buffer = new byte[16 * 1024];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.SequentialScan);
        long total = 0;
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            total = checked(total + read);
            if (total > options.MaxRunBytes)
            {
                throw new InvalidDataException("C6 audit exceeds the aggregate read quota.");
            }
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] == (byte)'\n')
                {
                    ParseLine();
                }
                else
                {
                    if (line.Count >= options.MaxLineBytes)
                    {
                        throw new InvalidDataException("C6 audit line exceeds the read quota.");
                    }
                    line.Add(buffer[index]);
                }
            }
        }
        if (line.Count > 0)
        {
            ParseLine();
        }
        return records;

        void ParseLine()
        {
            if (line.Count > 0 && line[^1] == (byte)'\r')
            {
                line.RemoveAt(line.Count - 1);
            }
            if (line.Count == 0)
            {
                return;
            }
            if (records.Count >= options.MaxEventCount)
            {
                throw new InvalidDataException("C6 audit exceeds the event read quota.");
            }
            try
            {
                var utf8 = CollectionsMarshal.AsSpan(line);
                ValidateJsonDepth(utf8, options.MaxJsonDepth);
                var record = JsonSerializer.Deserialize(utf8, RuntimeAuditJsonContext.Default.RuntimePersistedAuditRecord) ??
                    throw new InvalidDataException("C6 audit line deserialized to null.");
                records.Add(record);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("C6 audit contains invalid JSON.", ex);
            }
            finally
            {
                line.Clear();
            }
        }
    }

    private static byte[] ReadPayloadBlob(
        string runDirectory,
        RuntimeAuditBlobReference blob,
        RuntimeAuditStoreOptions options,
        HashSet<string> verifiedBlobs,
        ref long totalBlobBytes)
    {
        if (!string.Equals(blob.Algorithm, "sha256", StringComparison.Ordinal) ||
            blob.Digest.Length != 64 || !blob.Digest.All(Uri.IsHexDigit) ||
            blob.SizeBytes < 0 || blob.SizeBytes > options.MaxBlobBytes ||
            Path.IsPathRooted(blob.Path))
        {
            throw new InvalidDataException("C6 audit blob metadata is invalid.");
        }
        var expectedPath = $"blobs/sha256/{blob.Digest[..2]}/{blob.Digest}.json";
        if (!string.Equals(blob.Path.Replace('\\', '/'), expectedPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("C6 audit blob path does not match its content address.");
        }
        var resolved = QueryRuntimePathSafety.ResolveUnderRoot(runDirectory, blob.Path.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(resolved);
        if (!info.Exists || info.Length != blob.SizeBytes)
        {
            throw new InvalidDataException("C6 audit blob is missing or its length changed.");
        }
        if (verifiedBlobs.Add(blob.Digest))
        {
            totalBlobBytes = checked(totalBlobBytes + info.Length);
        }
        if (totalBlobBytes > options.MaxTotalBlobBytes || info.Length > int.MaxValue)
        {
            throw new InvalidDataException("C6 audit blobs exceed the aggregate read quota.");
        }
        var bytes = File.ReadAllBytes(resolved);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(digest, blob.Digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("C6 audit blob SHA-256 digest mismatch.");
        }
        ValidateJsonDepth(bytes, options.MaxJsonDepth);
        return bytes;
    }

    private static RuntimeAuditManifest? TryReadManifest(string runDirectory, int maxJsonDepth = 64)
    {
        try
        {
            var path = QueryRuntimePathSafety.ResolveUnderRoot(runDirectory, "manifest.json");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 64 * 1024)
            {
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            ValidateJsonDepth(bytes, maxJsonDepth);
            return JsonSerializer.Deserialize(bytes, RuntimeAuditJsonContext.Default.RuntimeAuditManifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static void PruneTerminalRuns(
        string runsRoot,
        RuntimeAuditStoreOptions options,
        DateTimeOffset now)
    {
        var candidates = EnumerateSafeRunDirectories(runsRoot)
            .Select(directory => new AuditRunInfo(directory, TryReadManifest(directory), MeasureRun(directory)))
            .Where(static item => item.Manifest is { Status: "completed" or "failed" or "cancelled" })
            .OrderBy(static item => item.Manifest!.UpdatedAt)
            .ToList();
        var total = candidates.Sum(static item => item.Bytes);
        var cutoff = now - options.Retention;
        foreach (var candidate in candidates.ToArray())
        {
            if (candidate.Manifest!.UpdatedAt >= cutoff &&
                candidates.Count < options.MaxStoredRuns &&
                total <= options.MaxTotalStorageBytes)
            {
                continue;
            }
            RejectLinksRecursively(candidate.Directory);
            Directory.Delete(candidate.Directory, recursive: true);
            candidates.Remove(candidate);
            total -= candidate.Bytes;
        }
    }

    private static IEnumerable<string> EnumerateSafeRunDirectories(string runsRoot)
    {
        foreach (var candidate in Directory.EnumerateDirectories(runsRoot))
        {
            var resolved = QueryRuntimePathSafety.ResolveUnderRoot(runsRoot, Path.GetFileName(candidate));
            if (new DirectoryInfo(resolved).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("C6 audit storage contains a linked run directory.");
            }
            yield return resolved;
        }
    }

    private static long MeasureRun(string root)
    {
        RejectLinksRecursively(root);
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            total = checked(total + new FileInfo(file).Length);
        }
        return total;
    }

    private static void EnsureTotalStorageQuota(
        string runsRoot,
        RuntimeAuditStoreOptions options,
        long additionalBytes)
    {
        long total = 0;
        foreach (var directory in EnumerateSafeRunDirectories(runsRoot))
        {
            total = checked(total + MeasureRun(directory));
        }
        if (checked(total + additionalBytes) > options.MaxTotalStorageBytes)
        {
            throw new InvalidDataException($"C6 audit storage exceeds the {options.MaxTotalStorageBytes} byte quota.");
        }
    }

    private static void RejectLinksRecursively(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException($"C6 audit storage refuses linked entries: {entry.FullName}");
                }
                if (entry is DirectoryInfo directory)
                {
                    pending.Push(directory);
                }
            }
        }
    }

    private static void PrepareDirectory(string path, bool isPrivate)
    {
        if (isPrivate)
        {
            RuntimeAuditFileSecurity.CreatePrivateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path);
        }
    }

    private static string NormalizeRunId(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > 128 || trimmed.Any(static value =>
                !(char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.')))
        {
            throw new ArgumentException("C6 sanitized audit run ID must contain only ASCII letters, digits, '.', '_' or '-'.", nameof(value));
        }
        return trimmed;
    }

    private static string TerminalStatus(RuntimeAuditPayload payload)
        => payload is RuntimeTurnTerminalAuditPayload terminal
            ? terminal.Status switch
            {
                RuntimeTurnStatus.Completed => "completed",
                RuntimeTurnStatus.Cancelled => "cancelled",
                _ => "failed"
            }
            : "failed";

    private static void ValidateJsonDepth(ReadOnlySpan<byte> utf8, int maxDepth)
    {
        try
        {
            using var _ = JsonDocument.Parse(
                utf8.ToArray(),
                new JsonDocumentOptions { MaxDepth = maxDepth });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"C6 audit JSON exceeds the configured depth of {maxDepth} or is invalid.", ex);
        }
    }

    private static StringComparison PathComparison()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record AuditRunInfo(string Directory, RuntimeAuditManifest? Manifest, long Bytes);
}

internal static class RuntimeAuditFileSecurity
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
