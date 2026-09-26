using System.Diagnostics;
using System.Text.Json;

namespace CodexFlow.QueryRuntime.Models.Diagnostics;

/// <summary>
/// Receives already-projected outbound diagnostic records. Implementations must
/// not block on I/O and must not throw; return <c>false</c> when a record is
/// dropped (queue full, quota exhausted). Embedders may use their own storage.
/// </summary>
public interface IQreOutboundDiagnosticSink
{
    bool TryWrite(QreOutboundDiagnosticRecord record, ReadOnlyMemory<byte> utf8Json);
}

/// <summary>Best-effort evidence counters for the completion summary.</summary>
public sealed record QreOutboundDiagnosticsCounters(
    long RecordsEmitted,
    long RecordsDropped,
    long RecordsOversized,
    long ProjectionFailures,
    long UncorrelatedHttpAttempts,
    long ModelCallsStarted,
    long ModelCallsEnded,
    long HttpAttemptsStarted,
    long HttpAttemptsEnded,
    long AliasOverflows)
{
    /// <summary>True when any evidence was lost or a started span has no end record.</summary>
    public bool EvidenceIncomplete =>
        RecordsDropped > 0 || RecordsOversized > 0 || ProjectionFailures > 0 || AliasOverflows > 0 ||
        ModelCallsStarted != ModelCallsEnded || HttpAttemptsStarted != HttpAttemptsEnded;
}

/// <summary>
/// One outbound diagnostic segment (a run or a recovery). It owns sequencing,
/// package-local aliases and the best-effort emission path. Alias maps and any
/// plaintext they key on stay in memory and are never persisted or exported.
/// </summary>
public sealed class QreOutboundDiagnostics
{
    public const string Unavailable = "unavailable";

    private const int MaxAliasesPerKind = 4096;

    private readonly IQreOutboundDiagnosticSink _sink;
    private readonly TimeProvider _timeProvider;
    private readonly long _startTimestamp;
    private readonly object _emitLock = new();
    private readonly object _aliasLock = new();
    private readonly Dictionary<string, Dictionary<string, string>> _aliases = new(StringComparer.Ordinal);
    private long _sequence;
    private long _modelCalls;
    private long _httpAttempts;
    private long _emitted;
    private long _dropped;
    private long _oversized;
    private long _projectionFailures;
    private long _uncorrelated;
    private long _modelCallsStarted;
    private long _modelCallsEnded;
    private long _httpAttemptsStarted;
    private long _httpAttemptsEnded;
    private long _aliasOverflows;

    private QreOutboundDiagnostics(
        QreOutboundDiagnosticsOptions options,
        IQreOutboundDiagnosticSink sink,
        string segmentId,
        TimeProvider timeProvider)
    {
        Options = options;
        _sink = sink;
        SegmentId = segmentId;
        _timeProvider = timeProvider;
        _startTimestamp = timeProvider.GetTimestamp();
    }

    public QreOutboundDiagnosticsOptions Options { get; }

    /// <summary>Random per segment; recovery creates a new segment.</summary>
    public string SegmentId { get; }

    public bool IsEnabled => Options.IsEnabled;

    /// <summary>
    /// Creates a diagnostic segment. Options are validated; an <c>Off</c> segment
    /// never emits records.
    /// </summary>
    public static QreOutboundDiagnostics Create(
        QreOutboundDiagnosticsOptions options,
        IQreOutboundDiagnosticSink sink,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);
        return new QreOutboundDiagnostics(
            options.Validate(),
            sink,
            $"seg-{Guid.NewGuid():N}"[..16],
            timeProvider ?? TimeProvider.System);
    }

    /// <summary>
    /// Wraps the host's transport handler. Add it while building the HttpClient
    /// pipeline; a handler cannot be inserted into an already built HttpClient.
    /// Disposing the returned handler disposes <paramref name="innerHandler"/>
    /// unless the HttpClient is created with <c>disposeHandler: false</c>.
    /// </summary>
    public DelegatingHandler CreateHandler(HttpMessageHandler innerHandler)
    {
        ArgumentNullException.ThrowIfNull(innerHandler);
        return new QreOutboundDiagnosticHandler(this) { InnerHandler = innerHandler };
    }

    public QreOutboundDiagnosticsCounters GetCounters()
        => new(
            Interlocked.Read(ref _emitted),
            Interlocked.Read(ref _dropped),
            Interlocked.Read(ref _oversized),
            Interlocked.Read(ref _projectionFailures),
            Interlocked.Read(ref _uncorrelated),
            Interlocked.Read(ref _modelCallsStarted),
            Interlocked.Read(ref _modelCallsEnded),
            Interlocked.Read(ref _httpAttemptsStarted),
            Interlocked.Read(ref _httpAttemptsEnded),
            Interlocked.Read(ref _aliasOverflows));

    internal string NextModelCallId() => $"mc-{Interlocked.Increment(ref _modelCalls):D4}";

    internal string NextHttpAttemptId() => $"ha-{Interlocked.Increment(ref _httpAttempts):D4}";

    internal double ElapsedMs() => _timeProvider.GetElapsedTime(_startTimestamp).TotalMilliseconds;

    internal long GetTimestamp() => _timeProvider.GetTimestamp();

    internal double ElapsedSince(long timestamp) => _timeProvider.GetElapsedTime(timestamp).TotalMilliseconds;

    internal void CountProjectionFailure() => Interlocked.Increment(ref _projectionFailures);

    internal void CountUncorrelated() => Interlocked.Increment(ref _uncorrelated);

    /// <summary>
    /// Returns a stable, package-local alias for <paramref name="value"/> within
    /// <paramref name="kind"/>. Keys use ordinal comparison, so values that differ
    /// only by case get different aliases.
    /// </summary>
    internal string Alias(string kind, string value)
    {
        lock (_aliasLock)
        {
            if (!_aliases.TryGetValue(kind, out var map))
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                _aliases.Add(kind, map);
            }
            if (map.TryGetValue(value, out var alias))
            {
                return alias;
            }
            if (map.Count >= MaxAliasesPerKind)
            {
                Interlocked.Increment(ref _aliasOverflows);
                return $"{kind}-overflow";
            }
            alias = $"{kind}-{map.Count + 1}";
            map.Add(value, alias);
            return alias;
        }
    }

    internal QreOutboundDiagnosticRecord NewRecord(
        string eventType,
        string observationPoint,
        QreModelCallContext? call,
        string captureStatus = QreDiagnosticCaptureStatus.Complete)
        => new()
        {
            SchemaVersion = QreOutboundDiagnosticSchema.SchemaVersion,
            ProjectionPolicyVersion = Options.ProjectionPolicyVersion,
            NormalizerVersion = QreOutboundDiagnosticSchema.NormalizerVersion,
            Sequence = 0,
            EventType = eventType,
            TimestampUtc = _timeProvider.GetUtcNow(),
            ElapsedMs = ElapsedMs(),
            SegmentId = SegmentId,
            RunAttemptAlias = call?.RunAttemptAlias ?? Unavailable,
            StepAlias = call?.StepAlias,
            RuntimeModelAttemptOrdinal = call?.RuntimeModelAttemptOrdinal,
            RuntimeModelAttemptOrdinalStatus = call?.RuntimeModelAttemptOrdinal == null ? Unavailable : "present",
            ModelCallId = call?.ModelCallId,
            ApiMode = call?.Target.ApiMode,
            ProviderCategory = call?.Target.ProviderCategory,
            SdkVersion = call?.Target.SdkVersion,
            AdapterVersion = QreOutboundDiagnosticSchema.AdapterVersion,
            ObservationPoint = observationPoint,
            AttemptCoverage = "not_applicable",
            CaptureStatus = captureStatus,
            Correlation = call == null ? "uncorrelated" : "correlated"
        };

    /// <summary>
    /// Assigns the sequence, enforces the per-record size bound and hands the
    /// record to the sink. Never throws: diagnostics are best effort.
    /// </summary>
    internal void Emit(QreOutboundDiagnosticRecord record)
    {
        if (!IsEnabled)
        {
            return;
        }
        try
        {
            switch (record.EventType)
            {
                case QreDiagnosticEventTypes.ModelCallStarted:
                    Interlocked.Increment(ref _modelCallsStarted);
                    break;
                case QreDiagnosticEventTypes.ModelCallEnded:
                    Interlocked.Increment(ref _modelCallsEnded);
                    break;
                case QreDiagnosticEventTypes.HttpAttemptStarted:
                    Interlocked.Increment(ref _httpAttemptsStarted);
                    break;
                case QreDiagnosticEventTypes.HttpAttemptEnded:
                    Interlocked.Increment(ref _httpAttemptsEnded);
                    break;
            }
            lock (_emitLock)
            {
                var sequenced = record with { Sequence = _sequence + 1 };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(
                    sequenced,
                    QreOutboundDiagnosticsJsonContext.Default.QreOutboundDiagnosticRecord);
                if (bytes.Length > Options.MaxRecordBytes)
                {
                    Interlocked.Increment(ref _oversized);
                    sequenced = sequenced with
                    {
                        Request = null,
                        HttpRequest = null,
                        HttpResponse = null,
                        CaptureStatus = QreDiagnosticCaptureStatus.Omitted,
                        ReasonCode = "record_size_limit_exceeded"
                    };
                    bytes = JsonSerializer.SerializeToUtf8Bytes(
                        sequenced,
                        QreOutboundDiagnosticsJsonContext.Default.QreOutboundDiagnosticRecord);
                }
                _sequence++;
                if (_sink.TryWrite(sequenced, bytes))
                {
                    Interlocked.Increment(ref _emitted);
                }
                else
                {
                    Interlocked.Increment(ref _dropped);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Interlocked.Increment(ref _dropped);
            Debug.WriteLine($"QRE outbound diagnostics dropped a record: {ex.GetType().Name}");
        }
    }
}

/// <summary>Static facts about the adapter target, projected to safe categories.</summary>
public sealed record QreOutboundDiagnosticsTarget(
    string ApiMode,
    string ProviderCategory,
    string SdkVersion)
{
    public static QreOutboundDiagnosticsTarget Unknown { get; } = new("unknown", "custom", "unknown");

    /// <summary>
    /// Target for a model built through <see cref="QreModelProviderSelector"/>.
    /// The provider id is an adapter class name, not a tenant or deployment name.
    /// </summary>
    public static QreOutboundDiagnosticsTarget ForProvider(IQreModelProvider provider, QreModelApiMode apiMode)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new QreOutboundDiagnosticsTarget(
            QreApiModeNames.ToWireName(apiMode),
            provider.Id,
            QreTransportCapabilityMatrix.SdkVersion);
    }
}

public static class QreApiModeNames
{
    public const string ChatCompletions = "chat_completions";
    public const string Responses = "responses";
    public const string AnthropicMessages = "anthropic_messages";

    public static string ToWireName(QreModelApiMode mode) => mode switch
    {
        QreModelApiMode.ChatCompletions => ChatCompletions,
        QreModelApiMode.Responses => Responses,
        QreModelApiMode.AnthropicMessages => AnthropicMessages,
        _ => "unknown"
    };
}

/// <summary>
/// Immutable correlation snapshot for one adapter model call. The upstream
/// cancellation token is a handle kept in memory only; it is never persisted and
/// diagnostics never cancel it.
/// </summary>
internal sealed class QreModelCallContext(
    QreOutboundDiagnostics diagnostics,
    string modelCallId,
    string? stepAlias,
    int? runtimeModelAttemptOrdinal,
    string runAttemptAlias,
    QreOutboundDiagnosticsTarget target,
    CancellationToken upstreamToken)
{
    private int _httpAttempts;

    public QreOutboundDiagnostics Diagnostics { get; } = diagnostics;

    public string ModelCallId { get; } = modelCallId;

    public string? StepAlias { get; } = stepAlias;

    public int? RuntimeModelAttemptOrdinal { get; } = runtimeModelAttemptOrdinal;

    public string RunAttemptAlias { get; } = runAttemptAlias;

    public QreOutboundDiagnosticsTarget Target { get; } = target;

    /// <summary>The token handed to the adapter, upstream of HttpClient's linked token.</summary>
    public CancellationToken UpstreamToken { get; } = upstreamToken;

    private int _lastStatusCode;
    private string? _lastHandlerClassification;

    public int HttpAttemptCount => Volatile.Read(ref _httpAttempts);

    /// <summary>Status of the latest handler-visible response; 0 when none arrived.</summary>
    public int LastStatusCode => Volatile.Read(ref _lastStatusCode);

    /// <summary>Latest handler/stream classification, used as independent evidence by the adapter.</summary>
    public string? LastHandlerClassification => Volatile.Read(ref _lastHandlerClassification);

    public int NextHttpAttemptOrdinal() => Interlocked.Increment(ref _httpAttempts);

    public void RecordStatus(int statusCode) => Volatile.Write(ref _lastStatusCode, statusCode);

    public void RecordHandlerClassification(string classification)
        => Volatile.Write(ref _lastHandlerClassification, classification);
}

/// <summary>
/// AsyncLocal bridge used only from the adapter's SDK enumeration step to the
/// handler's SendAsync entry. The handler snapshots it once; response wrappers
/// and end callbacks receive the snapshot explicitly and never read it again.
/// </summary>
internal static class QreModelCallScope
{
    private static readonly AsyncLocal<QreModelCallContext?> CurrentContext = new();

    public static QreModelCallContext? Current => CurrentContext.Value;

    public static Scope Enter(QreModelCallContext context)
    {
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new Scope(previous);
    }

    public readonly struct Scope(QreModelCallContext? previous) : IDisposable
    {
        public void Dispose() => CurrentContext.Value = previous;
    }
}
