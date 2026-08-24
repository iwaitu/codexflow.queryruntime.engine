using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.QueryRuntime.Engine.V2;

public static class RuntimeCheckpointSchema
{
    public const int CurrentVersion = 1;

    public const string RuntimeContractVersion = "qre-v2-h1-4";
}

public readonly record struct RuntimeRunAttemptId
{
    [JsonConstructor]
    public RuntimeRunAttemptId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}

public sealed record RuntimeRunAttempt(
    RuntimeRunAttemptId AttemptId,
    RuntimeRunAttemptId RootAttemptId,
    RuntimeRunAttemptId? ParentAttemptId,
    int Ordinal)
{
    public static RuntimeRunAttempt Create(string? attemptId = null)
    {
        var id = new RuntimeRunAttemptId(
            string.IsNullOrWhiteSpace(attemptId)
                ? $"attempt-{Guid.NewGuid():N}"
                : attemptId.Trim());
        return new RuntimeRunAttempt(id, id, null, 0);
    }

    public static RuntimeRunAttempt Resume(
        RuntimeCheckpointDocument checkpoint,
        string? attemptId = null)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var id = new RuntimeRunAttemptId(
            string.IsNullOrWhiteSpace(attemptId)
                ? $"attempt-{Guid.NewGuid():N}"
                : attemptId.Trim());
        if (id == checkpoint.Attempt.AttemptId)
        {
            throw new ArgumentException("A recovery must create a new RunAttempt ID.", nameof(attemptId));
        }
        return new RuntimeRunAttempt(
            id,
            checkpoint.Attempt.RootAttemptId,
            checkpoint.Attempt.AttemptId,
            checked(checkpoint.Attempt.Ordinal + 1));
    }
}

public enum RuntimeCheckpointKind
{
    TurnStarted = 0,
    StepPrepared = 1,
    ModelCommitted = 2,
    ToolBatchCommitted = 3,
    StepCommitted = 4,
    ContinuationCommitted = 5,
    Terminal = 6
}

public enum RuntimeCheckpointDisposition
{
    Resumable = 0,
    NeedsReconciliation = 1,
    Terminal = 2
}

public enum RuntimeCheckpointFailureMode
{
    FailClosed = 0
}

public sealed record RuntimeCheckpointRequestSnapshot(
    RuntimeSessionId SessionId,
    RuntimeTurnId TurnId,
    string Objective,
    IReadOnlyList<RuntimeMessage> InitialMessages,
    IReadOnlyList<RuntimeToolDescriptor> Tools,
    RuntimeModelParameters ModelParameters,
    RuntimePolicySnapshot Policy,
    RuntimeEnvironmentSnapshot Environment,
    RuntimeBudgetSnapshot Budget,
    long HistoryVersion,
    DateTimeOffset? CreatedAt,
    string? RecoveryCompatibilityId)
{
    public static RuntimeCheckpointRequestSnapshot Capture(RuntimeAgentLoopRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new RuntimeCheckpointRequestSnapshot(
            request.SessionId,
            request.TurnId,
            request.Objective,
            SnapshotMessages(request.InitialMessages),
            Array.AsReadOnly(request.Tools.Select(static tool =>
                tool with { InputSchema = tool.InputSchema.Clone() }).ToArray()),
            request.ModelParameters,
            request.Policy,
            request.Environment,
            request.Budget,
            request.HistoryVersion,
            request.CreatedAt,
            request.RecoveryCompatibilityId);
    }

    public RuntimeAgentLoopRequest ToLoopRequest()
        => new(
            SessionId,
            TurnId,
            Objective,
            SnapshotMessages(InitialMessages),
            Array.AsReadOnly(Tools.Select(static tool =>
                tool with { InputSchema = tool.InputSchema.Clone() }).ToArray()),
            ModelParameters,
            Policy,
            Environment,
            Budget,
            HistoryVersion,
            CreatedAt)
        {
            RecoveryCompatibilityId = RecoveryCompatibilityId
        };

    internal static IReadOnlyList<RuntimeMessage> SnapshotMessages(
        IEnumerable<RuntimeMessage> messages)
        => Array.AsReadOnly(messages.Select(static message => message with
        {
            Items = Array.AsReadOnly(message.Items.Select(SnapshotItem).ToArray())
        }).ToArray());

    private static RuntimeItem SnapshotItem(RuntimeItem item)
        => item switch
        {
            RuntimeTextItem text => text,
            RuntimeReasoningItem reasoning => reasoning,
            RuntimeToolCallItem toolCall => toolCall with
            {
                Call = toolCall.Call with { Arguments = toolCall.Call.Arguments.Clone() }
            },
            RuntimeToolResultItem toolResult => toolResult with
            {
                Result = toolResult.Result with
                {
                    Artifacts = toolResult.Result.Artifacts == null
                        ? null
                        : Array.AsReadOnly(toolResult.Result.Artifacts.ToArray())
                }
            },
            RuntimeArtifactItem artifact => artifact,
            _ => throw new ArgumentException(
                $"Unsupported Runtime item type '{item.GetType().FullName}'.",
                nameof(item))
        };
}

public sealed record RuntimeCheckpointDocument(
    int SchemaVersion,
    string RuntimeContractVersion,
    long Sequence,
    RuntimeRunAttempt Attempt,
    RuntimeCheckpointKind Kind,
    RuntimeCheckpointDisposition Disposition,
    DateTimeOffset CreatedAt,
    RuntimeCheckpointRequestSnapshot Request,
    string RequestFingerprint,
    RuntimeSessionState Session,
    IReadOnlyList<RuntimeHistoryMessage> CanonicalHistory,
    long NextHistoryMessageSequence,
    IReadOnlyList<RuntimeHistoryBlob> HistoryBlobs,
    string FinalText,
    string? ReconciliationReason = null)
{
    public static RuntimeCheckpointDocument Capture(
        long sequence,
        RuntimeRunAttempt attempt,
        RuntimeCheckpointKind kind,
        RuntimeAgentLoopRequest request,
        RuntimeSessionState session,
        RuntimeHistorySnapshot canonicalHistory,
        string finalText,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(canonicalHistory);
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        var disposition = kind == RuntimeCheckpointKind.Terminal
            ? RuntimeCheckpointDisposition.Terminal
            : kind == RuntimeCheckpointKind.ModelCommitted &&
              session.ActiveTurn?.Steps.LastOrDefault()?.Output?.ToolCalls.Count > 0
                ? RuntimeCheckpointDisposition.NeedsReconciliation
                : RuntimeCheckpointDisposition.Resumable;
        var reason = disposition == RuntimeCheckpointDisposition.NeedsReconciliation
            ? "The last durable model response contains tool calls whose execution outcome is not durably known."
            : null;
        var requestSnapshot = RuntimeCheckpointRequestSnapshot.Capture(request);
        return new RuntimeCheckpointDocument(
            RuntimeCheckpointSchema.CurrentVersion,
            RuntimeCheckpointSchema.RuntimeContractVersion,
            sequence,
            attempt,
            kind,
            disposition,
            createdAt,
            requestSnapshot,
            RuntimeCheckpointFingerprint.Compute(requestSnapshot),
            SnapshotSession(session),
            Array.AsReadOnly(canonicalHistory.Messages.Select(static entry => entry with
            {
                Message = RuntimeHistory.Snapshot(entry.Message),
                ItemIds = Array.AsReadOnly(entry.ItemIds.ToArray())
            }).ToArray()),
            canonicalHistory.NextMessageSequence,
            Array.AsReadOnly(canonicalHistory.Blobs
                .Values
                .Select(static blob => blob with { Data = blob.Data.ToArray() })
                .ToArray()),
            finalText ?? string.Empty,
            reason);
    }

    private static RuntimeSessionState SnapshotSession(RuntimeSessionState session)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            session,
            RuntimeCheckpointJsonContext.Default.RuntimeSessionState);
        return JsonSerializer.Deserialize(
                   bytes,
                   RuntimeCheckpointJsonContext.Default.RuntimeSessionState) ??
               throw new InvalidOperationException("Could not snapshot Runtime session state.");
    }
}

public interface IRuntimeCheckpointSink
{
    ValueTask SaveAsync(RuntimeCheckpointDocument checkpoint, CancellationToken ct);
}

public sealed class InMemoryRuntimeCheckpointSink : IRuntimeCheckpointSink
{
    private readonly object _sync = new();
    private readonly List<RuntimeCheckpointDocument> _checkpoints = [];

    public IReadOnlyList<RuntimeCheckpointDocument> Checkpoints
    {
        get
        {
            lock (_sync)
            {
                return Array.AsReadOnly(_checkpoints.ToArray());
            }
        }
    }

    public RuntimeCheckpointDocument? Latest
    {
        get
        {
            lock (_sync)
            {
                return _checkpoints.LastOrDefault();
            }
        }
    }

    public ValueTask SaveAsync(RuntimeCheckpointDocument checkpoint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (_sync)
        {
            _checkpoints.Add(checkpoint);
        }
        return ValueTask.CompletedTask;
    }
}

public sealed record RuntimeJsonCheckpointStoreOptions
{
    public long MaxFileBytes { get; init; } = 16L * 1024 * 1024;

    public int MaxJsonDepth { get; init; } = 64;

    public bool Private { get; init; }

    internal void Validate()
    {
        if (MaxFileBytes <= 0 || MaxFileBytes > 64L * 1024 * 1024 ||
            MaxJsonDepth <= 0 || MaxJsonDepth > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RuntimeJsonCheckpointStoreOptions),
                "Checkpoint limits are invalid.");
        }
    }
}

public sealed record RuntimeCheckpointFileEnvelope(
    int SchemaVersion,
    string Type,
    long PayloadLength,
    string PayloadSha256,
    RuntimeCheckpointDocument Payload);

public sealed class RuntimeJsonCheckpointStore : IRuntimeCheckpointSink
{
    private const string EnvelopeType = "qre.v2.checkpoint";
    private const string FileName = "checkpoint.v1.json";
    private readonly RuntimeJsonCheckpointStoreOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public RuntimeJsonCheckpointStore(
        string runDirectory,
        RuntimeJsonCheckpointStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        _options = options ?? new RuntimeJsonCheckpointStoreOptions();
        _options.Validate();
        RunDirectory = Path.GetFullPath(runDirectory);
        if (_options.Private)
        {
            RuntimeAuditFileSecurity.CreatePrivateDirectory(RunDirectory);
        }
        else
        {
            Directory.CreateDirectory(RunDirectory);
        }
        CheckpointPath = QueryRuntimePathSafety.ResolveUnderRoot(RunDirectory, FileName);
    }

    public string RunDirectory { get; }

    public string CheckpointPath { get; }

    public async ValueTask SaveAsync(RuntimeCheckpointDocument checkpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ValidateDocument(checkpoint);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            checkpoint,
            RuntimeCheckpointJsonContext.Default.RuntimeCheckpointDocument);
        if (payload.LongLength > _options.MaxFileBytes)
        {
            throw new InvalidDataException(
                $"Checkpoint payload exceeds the {_options.MaxFileBytes} byte quota.");
        }
        var envelope = new RuntimeCheckpointFileEnvelope(
            RuntimeCheckpointSchema.CurrentVersion,
            EnvelopeType,
            payload.LongLength,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            checkpoint);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            RuntimeCheckpointJsonContext.Default.RuntimeCheckpointFileEnvelope);
        if (bytes.LongLength > _options.MaxFileBytes)
        {
            throw new InvalidDataException(
                $"Checkpoint file exceeds the {_options.MaxFileBytes} byte quota.");
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        var temporary = QueryRuntimePathSafety.ResolveUnderRoot(
            RunDirectory,
            $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (File.Exists(CheckpointPath))
            {
                var previous = Read(CheckpointPath, _options);
                if (previous.Attempt.AttemptId != checkpoint.Attempt.AttemptId ||
                    checkpoint.Sequence <= previous.Sequence)
                {
                    throw new InvalidDataException(
                        "Checkpoint replacement must preserve the attempt and advance its sequence.");
                }
            }
            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 16 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (_options.Private && !OperatingSystem.IsWindows())
            {
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using (var stream = new FileStream(temporary, streamOptions))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (_options.Private)
            {
                RuntimeAuditFileSecurity.RestrictPrivateFile(temporary);
            }
            File.Move(temporary, CheckpointPath, overwrite: true);
            if (_options.Private)
            {
                RuntimeAuditFileSecurity.RestrictPrivateFile(CheckpointPath);
            }
        }
        finally
        {
            _writeLock.Release();
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort cleanup. A future read never selects temporary files.
            }
        }
    }

    public static RuntimeCheckpointDocument Read(
        string checkpointPath,
        RuntimeJsonCheckpointStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointPath);
        options ??= new RuntimeJsonCheckpointStoreOptions();
        options.Validate();
        var fullPath = Path.GetFullPath(checkpointPath);
        var runDirectory = Path.GetDirectoryName(fullPath) ??
            throw new ArgumentException("Checkpoint path has no run directory.", nameof(checkpointPath));
        var resolved = QueryRuntimePathSafety.ResolveUnderRoot(runDirectory, FileName);
        if (!string.Equals(fullPath, resolved, PathComparison()))
        {
            throw new InvalidDataException("Checkpoint must use the canonical checkpoint filename.");
        }
        QueryRuntimePathSafety.RejectWorkspaceLinks(runDirectory, resolved, "read for recovery");
        byte[] bytes;
        using (var stream = new FileStream(
                   resolved,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   16 * 1024,
                   FileOptions.SequentialScan))
        {
            QueryRuntimePathSafety.RejectWorkspaceLinks(runDirectory, resolved, "read for recovery");
            var length = stream.Length;
            if (length <= 0 || length > options.MaxFileBytes || length > int.MaxValue)
            {
                throw new InvalidDataException("Checkpoint file is empty or exceeds its quota.");
            }
            bytes = GC.AllocateUninitializedArray<byte>((int)length);
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    throw new InvalidDataException("Checkpoint file was truncated while it was being read.");
                }
                offset += read;
            }
            if (stream.ReadByte() != -1 || stream.Length != length)
            {
                throw new InvalidDataException("Checkpoint file changed while it was being read or exceeds its quota.");
            }
        }
        RuntimeCheckpointFileEnvelope envelope;
        try
        {
            using (JsonDocument.Parse(bytes, new JsonDocumentOptions
                   {
                       MaxDepth = options.MaxJsonDepth,
                       CommentHandling = JsonCommentHandling.Disallow,
                       AllowTrailingCommas = false
                   }))
            {
            }
            envelope = JsonSerializer.Deserialize(
                           bytes,
                           RuntimeCheckpointJsonContext.Default.RuntimeCheckpointFileEnvelope) ??
                       throw new JsonException("Checkpoint envelope is empty.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.TraceCorrupt,
                "checkpoint_json_invalid",
                "Checkpoint JSON is invalid or exceeds the configured depth."), ex);
        }
        if (envelope.SchemaVersion != RuntimeCheckpointSchema.CurrentVersion ||
            !string.Equals(envelope.Type, EnvelopeType, StringComparison.Ordinal))
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.SchemaIncompatible,
                "checkpoint_envelope_incompatible",
                "Checkpoint envelope schema or type is not supported."));
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            envelope.Payload,
            RuntimeCheckpointJsonContext.Default.RuntimeCheckpointDocument);
        var actualDigest = SHA256.HashData(payload);
        byte[] expectedDigest;
        try
        {
            expectedDigest = Convert.FromHexString(envelope.PayloadSha256);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException)
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.TraceCorrupt,
                "checkpoint_integrity_invalid",
                "Checkpoint payload digest is not valid hexadecimal."), ex);
        }
        if (envelope.PayloadLength != payload.LongLength ||
            expectedDigest.Length != actualDigest.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest))
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.TraceCorrupt,
                "checkpoint_integrity_invalid",
                "Checkpoint payload length or digest does not match."));
        }
        ValidateDocument(envelope.Payload);
        return envelope.Payload;
    }

    public static string FindLatestCheckpoint(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var workspace = Path.GetFullPath(workspacePath);
        var v2Root = QueryRuntimePathSafety.ResolveUnderRoot(workspace, Path.Combine(".qre", "v2"));
        var roots = new[]
        {
            QueryRuntimePathSafety.ResolveUnderRoot(v2Root, "runs"),
            QueryRuntimePathSafety.ResolveUnderRoot(v2Root, Path.Combine("private", "runs"))
        };
        var candidates = roots
            .Where(Directory.Exists)
            .SelectMany(static root => Directory.EnumerateDirectories(root))
            .Select(directory => QueryRuntimePathSafety.ResolveUnderRoot(directory, FileName))
            .Where(File.Exists)
            .Select(static path => new FileInfo(path))
            .OrderByDescending(static info => info.LastWriteTimeUtc)
            .ToArray();
        var loaded = new List<(FileInfo File, RuntimeCheckpointDocument Checkpoint)>(candidates.Length);
        foreach (var candidate in candidates)
        {
            var checkpoint = Read(candidate.FullName);
            loaded.Add((candidate, checkpoint));
        }
        var claimedParents = loaded
            .Where(static value => value.Checkpoint.Attempt.ParentAttemptId.HasValue)
            .Select(static value => (
                value.Checkpoint.Request.SessionId,
                value.Checkpoint.Request.TurnId,
                value.Checkpoint.Attempt.ParentAttemptId!.Value))
            .ToHashSet();
        var latest = loaded.FirstOrDefault(value =>
            value.Checkpoint.Disposition != RuntimeCheckpointDisposition.Terminal &&
            !claimedParents.Contains((
                value.Checkpoint.Request.SessionId,
                value.Checkpoint.Request.TurnId,
                value.Checkpoint.Attempt.AttemptId)));
        if (latest.File != null)
        {
            return latest.File.FullName;
        }
        throw new FileNotFoundException("No unfinished v2 checkpoint was found.");
    }

    internal static void ValidateDocument(RuntimeCheckpointDocument checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Attempt == null || checkpoint.Request == null || checkpoint.Session == null ||
            checkpoint.CanonicalHistory == null || checkpoint.Request.InitialMessages == null ||
            checkpoint.Request.Tools == null || checkpoint.Request.ModelParameters == null ||
            checkpoint.Request.Policy == null || checkpoint.Request.Environment == null ||
            checkpoint.Request.Budget == null || checkpoint.HistoryBlobs == null)
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.TraceCorrupt,
                "checkpoint_shape_invalid",
                "Checkpoint contains a missing required object or collection."));
        }
        if (checkpoint.SchemaVersion != RuntimeCheckpointSchema.CurrentVersion ||
            !string.Equals(
                checkpoint.RuntimeContractVersion,
                RuntimeCheckpointSchema.RuntimeContractVersion,
                StringComparison.Ordinal))
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.SchemaIncompatible,
                "checkpoint_runtime_incompatible",
                "Checkpoint Runtime contract version is not supported."));
        }
        if (checkpoint.Sequence <= 0 || checkpoint.Attempt.Ordinal < 0 ||
            string.IsNullOrWhiteSpace(checkpoint.Attempt.AttemptId.Value) ||
            string.IsNullOrWhiteSpace(checkpoint.Attempt.RootAttemptId.Value) ||
            checkpoint.Request.SessionId != checkpoint.Session.SessionId ||
            string.IsNullOrWhiteSpace(checkpoint.RequestFingerprint) ||
            !string.Equals(
                checkpoint.RequestFingerprint,
                RuntimeCheckpointFingerprint.Compute(checkpoint.Request),
                StringComparison.Ordinal))
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.TraceCorrupt,
                "checkpoint_shape_invalid",
                "Checkpoint identity, lineage, or request fingerprint is invalid."));
        }
        var turn = checkpoint.Session.ActiveTurn ?? checkpoint.Session.TerminalTurns.LastOrDefault();
        if (turn?.Context == null || turn.Steps == null || turn.Progress == null ||
            turn.Progress.Usage == null || turn.Progress.Usage.Additional == null ||
            turn.Context.SessionId != checkpoint.Request.SessionId ||
            turn.Context.TurnId != checkpoint.Request.TurnId)
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.TraceCorrupt,
                "checkpoint_turn_identity_invalid",
                "Checkpoint Turn identity is inconsistent."));
        }
        ValidateAttemptLineage(checkpoint);
        ValidateCanonicalHistory(
            checkpoint.CanonicalHistory,
            checkpoint.Session.HistoryVersion,
            checkpoint.NextHistoryMessageSequence);
        ValidateHistoryBlobs(checkpoint.HistoryBlobs);
        ValidateHistoryBlobReferences(checkpoint.CanonicalHistory, checkpoint.HistoryBlobs);
        ValidateStableBoundary(checkpoint, turn);
    }

    private static void ValidateCanonicalHistory(
        IReadOnlyList<RuntimeHistoryMessage> history,
        long historyVersion,
        long nextMessageSequence)
    {
        try
        {
            _ = RuntimeHistory.RestoreCanonical(history, historyVersion, nextMessageSequence);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException)
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.TraceCorrupt,
                "checkpoint_history_identity_invalid",
                $"Checkpoint canonical history identity is invalid: {ex.Message}"));
        }
    }

    private static void ValidateHistoryBlobReferences(
        IReadOnlyList<RuntimeHistoryMessage> history,
        IReadOnlyList<RuntimeHistoryBlob> blobs)
    {
        var byDigest = blobs.ToDictionary(static blob => blob.Digest, StringComparer.Ordinal);
        foreach (var artifact in history
                     .SelectMany(static entry => entry.Message.Items)
                     .SelectMany(static item => item switch
                     {
                         RuntimeToolResultItem result => result.Result.Artifacts ?? [],
                         RuntimeArtifactItem artifact => [artifact.Artifact],
                         _ => []
                     })
                     .Where(static artifact => artifact.Path.StartsWith(
                         "runtime-history://sha256/",
                         StringComparison.Ordinal)))
        {
            if (artifact.Digest == null ||
                !byDigest.TryGetValue(artifact.Digest, out var blob) ||
                !string.Equals(artifact.Path, $"runtime-history://sha256/{blob.Digest}", StringComparison.Ordinal) ||
                !string.Equals(artifact.MediaType, blob.MediaType, StringComparison.Ordinal) ||
                artifact.Length != blob.Data.Length)
            {
                throw new RuntimeResumeException(new RuntimeError(
                    RuntimeErrorCategory.TraceCorrupt,
                    "checkpoint_history_blob_reference_invalid",
                    "Checkpoint canonical history contains an unresolved or inconsistent blob reference."));
            }
        }
    }

    private static void ValidateHistoryBlobs(IReadOnlyList<RuntimeHistoryBlob> blobs)
    {
        var digests = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var blob in blobs)
        {
            if (blob == null || string.IsNullOrWhiteSpace(blob.Digest) ||
                string.IsNullOrWhiteSpace(blob.MediaType) || !digests.Add(blob.Digest))
            {
                throw new RuntimeResumeException(new RuntimeError(
                    RuntimeErrorCategory.TraceCorrupt,
                    "checkpoint_history_blob_invalid",
                    "Checkpoint history blob metadata is missing or duplicated."));
            }
            var data = blob.Data.ToArray();
            totalBytes = checked(totalBytes + data.LongLength);
            var digest = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            if (!string.Equals(digest, blob.Digest, StringComparison.Ordinal) ||
                data.Length > RuntimeContextOptions.Default.MaxBlobBytes ||
                totalBytes > RuntimeContextOptions.Default.MaxTotalBlobBytes)
            {
                throw new RuntimeResumeException(new RuntimeError(
                    RuntimeErrorCategory.TraceCorrupt,
                    "checkpoint_history_blob_invalid",
                    "Checkpoint history blob digest or quota is invalid."));
            }
        }
    }

    private static void ValidateAttemptLineage(RuntimeCheckpointDocument checkpoint)
    {
        var attempt = checkpoint.Attempt;
        var valid = attempt.Ordinal == 0
            ? attempt.ParentAttemptId == null && attempt.RootAttemptId == attempt.AttemptId
            : attempt.Ordinal < int.MaxValue &&
              attempt.ParentAttemptId != null &&
              attempt.RootAttemptId != default &&
              attempt.AttemptId != attempt.RootAttemptId &&
              attempt.AttemptId != attempt.ParentAttemptId.Value &&
              (attempt.Ordinal != 1 || attempt.ParentAttemptId.Value == attempt.RootAttemptId);
        if (!valid)
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.TraceCorrupt,
                "checkpoint_lineage_shape_invalid",
                "Checkpoint attempt lineage is malformed."));
        }
    }

    private static void ValidateStableBoundary(
        RuntimeCheckpointDocument checkpoint,
        RuntimeTurnState turn)
    {
        var active = checkpoint.Session.ActiveTurn != null;
        var last = turn.Steps.LastOrDefault();
        var expectedDisposition = checkpoint.Kind == RuntimeCheckpointKind.Terminal
            ? RuntimeCheckpointDisposition.Terminal
            : checkpoint.Kind == RuntimeCheckpointKind.ModelCommitted &&
              last?.Output?.ToolCalls.Count > 0
                ? RuntimeCheckpointDisposition.NeedsReconciliation
                : RuntimeCheckpointDisposition.Resumable;
        var shapeValid = checkpoint.Kind switch
        {
            RuntimeCheckpointKind.TurnStarted => active && last == null,
            RuntimeCheckpointKind.StepPrepared => active && last is
            {
                Phase: RuntimeStepPhase.Sampling,
                Output: null
            },
            RuntimeCheckpointKind.ModelCommitted => active && last?.Output != null &&
                last.Phase is RuntimeStepPhase.ResolvingTools or RuntimeStepPhase.CommittingObservation,
            RuntimeCheckpointKind.ToolBatchCommitted => active && last is
            {
                Phase: RuntimeStepPhase.Completed,
                Output.ToolCalls.Count: > 0
            } && HasCompleteToolBatch(last),
            RuntimeCheckpointKind.StepCommitted => active && last is
            {
                Phase: RuntimeStepPhase.Completed,
                Output.ToolCalls.Count: 0
            },
            RuntimeCheckpointKind.ContinuationCommitted => active && last is
            {
                Phase: RuntimeStepPhase.Completed
            } && turn.Progress.ContinuationCount > 0,
            RuntimeCheckpointKind.Terminal => !active && turn.Status != RuntimeTurnStatus.Running,
            _ => false
        };
        if (!shapeValid || checkpoint.Disposition != expectedDisposition ||
            checkpoint.Session.HistoryVersion < checkpoint.Request.HistoryVersion ||
            turn.Progress.ContinuationCount < 0 || turn.Progress.ToolCallCount < 0 ||
            turn.Progress.Usage.InputTokens < 0 || turn.Progress.Usage.OutputTokens < 0 ||
            turn.Progress.Usage.TotalTokens < 0 ||
            turn.Progress.Usage.Additional.Any(static value => value.Value < 0))
        {
            throw new RuntimeResumeException(new RuntimeError(
                RuntimeErrorCategory.TraceCorrupt,
                "checkpoint_stable_boundary_invalid",
                "Checkpoint state does not match its declared stable boundary."));
        }
        for (var index = 0; index < turn.Steps.Count; index++)
        {
            var step = turn.Steps[index];
            if (step?.Context == null || step.Context.ModelRequest == null ||
                step.Context.Policy == null || step.Context.Environment == null ||
                step.Context.Budget == null || step.ModelAttempts < 0 ||
                step.Context.Index != index ||
                step.Context.ModelRequest.SessionId != checkpoint.Request.SessionId ||
                step.Context.ModelRequest.TurnId != checkpoint.Request.TurnId ||
                step.Context.ModelRequest.StepId != step.Context.StepId ||
                step.Context.Policy != checkpoint.Request.Policy ||
                step.Context.Environment != checkpoint.Request.Environment ||
                step.Context.Budget != checkpoint.Request.Budget)
            {
                throw new RuntimeResumeException(new RuntimeError(
                    RuntimeErrorCategory.TraceCorrupt,
                    "checkpoint_step_snapshot_invalid",
                    "Checkpoint Step identity or frozen policy snapshot is inconsistent."));
            }
        }
    }

    private static bool HasCompleteToolBatch(RuntimeStepState step)
    {
        var calls = step.Output?.ToolCalls;
        var invocations = step.ToolInvocations;
        if (calls == null || invocations == null || calls.Count != invocations.Count)
        {
            return false;
        }
        for (var index = 0; index < calls.Count; index++)
        {
            var invocation = invocations[index];
            if (invocation?.Call == null || invocation.Call.InvocationId != calls[index].InvocationId ||
                invocation.Result?.InvocationId != calls[index].InvocationId ||
                invocation.Status is not (RuntimeToolInvocationStatus.Succeeded or
                    RuntimeToolInvocationStatus.Failed or
                    RuntimeToolInvocationStatus.Denied or
                    RuntimeToolInvocationStatus.Cancelled) ||
                invocation.Result.Error is
                {
                    Retryable: false,
                    Category: RuntimeErrorCategory.ApprovalDeclined or RuntimeErrorCategory.ApprovalTimeout
                } ||
                invocation.Result.Error?.Code is "tool_call_budget_exhausted" or "tool_preparation_failed")
            {
                return false;
            }
        }
        return true;
    }

    private static StringComparison PathComparison()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

public sealed class RuntimeResumeException(RuntimeError error, Exception? innerException = null)
    : Exception(error.Message, innerException)
{
    public RuntimeError Error { get; } = error;
}

internal static class RuntimeCheckpointFingerprint
{
    public static string Compute(RuntimeCheckpointRequestSnapshot request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            RuntimeCheckpointJsonContext.Default.RuntimeCheckpointRequestSnapshot);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(RuntimeCheckpointFileEnvelope))]
[JsonSerializable(typeof(RuntimeCheckpointDocument))]
[JsonSerializable(typeof(RuntimeCheckpointRequestSnapshot))]
[JsonSerializable(typeof(RuntimeSessionState))]
public partial class RuntimeCheckpointJsonContext : JsonSerializerContext;
