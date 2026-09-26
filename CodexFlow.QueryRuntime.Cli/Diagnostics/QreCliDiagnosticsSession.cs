using CodexFlow.QueryRuntime.Models.Diagnostics;

namespace CodexFlow.QueryRuntime.Cli.Diagnostics;

/// <summary>
/// One CLI run's outbound diagnostics: the recorder, its private sidecar store and
/// the coverage actually available for the built model client. Failures here are
/// best effort and never change the run's business exit code.
/// </summary>
internal sealed class QreCliDiagnosticsSession : IAsyncDisposable
{
    private int _completed;

    private QreCliDiagnosticsSession(QreOutboundDiagnostics diagnostics, QreDiagnosticsStore store)
    {
        Diagnostics = diagnostics;
        Store = store;
    }

    public QreOutboundDiagnostics Diagnostics { get; }

    public QreDiagnosticsStore Store { get; }

    public QreDiagnosticsCoverage Coverage { get; private set; } = new()
    {
        TransportCapture = "not_built",
        AttemptCoverage = "handler_visible"
    };

    /// <summary>Starts diagnostics, or returns null with a reason when storage is unavailable.</summary>
    public static QreCliDiagnosticsSession? TryStart(
        string workspace,
        QreOutboundDiagnosticMode mode,
        out string? failureReason)
    {
        failureReason = null;
        var options = new QreOutboundDiagnosticsOptions { Mode = mode };
        var store = default(QreDiagnosticsStore);
        try
        {
            // The store is the sink; the recorder needs it at creation, so create a
            // provisional segment id first and reuse it for the manifest.
            var sink = new DeferredSink();
            var diagnostics = QreOutboundDiagnostics.Create(options, sink);
            store = QreDiagnosticsStore.Create(
                workspace,
                options,
                diagnostics.SegmentId,
                new QreDiagnosticsCoverage { TransportCapture = "not_built", AttemptCoverage = "handler_visible" });
            sink.Target = store;
            return new QreCliDiagnosticsSession(diagnostics, store);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            failureReason = "diagnostic_store_unavailable";
            return null;
        }
    }

    public void SetCoverage(QreDiagnosticsCoverage coverage) => Coverage = coverage;

    public static QreDiagnosticsCoverage StaticModelCoverage(QreOutboundDiagnosticMode mode)
        => new()
        {
            TransportCapture = "transport_capture_unavailable",
            AttemptCoverage = "not_applicable",
            ProviderCategory = "static",
            CapabilityStatus = QreCapabilityStatus.Unsupported,
            RequestStructure = false,
            ResponseBodies = false
        };

    public static QreDiagnosticsCoverage ProviderCoverage(QreOutboundDiagnosticsTarget target, QreOutboundDiagnosticMode mode)
    {
        var cell = QreTransportCapabilityMatrix.Get(target.ProviderCategory, target.ApiMode);
        return new QreDiagnosticsCoverage
        {
            TransportCapture = cell.InjectedTransportUsed ? "handler" : "transport_capture_unavailable",
            AttemptCoverage = "handler_visible",
            ProviderCategory = target.ProviderCategory,
            ApiMode = target.ApiMode,
            SdkVersion = target.SdkVersion,
            CapabilityStatus = cell.Status,
            RequestStructure = mode == QreOutboundDiagnosticMode.Structure && cell.InjectedTransportUsed,
            ResponseBodies = false
        };
    }

    public void LinkLocalRun(string? auditRunDirectory, string? runAttemptId)
        => Store.WriteLocalIndex(new QreDiagnosticsLocalIndex
        {
            DiagnosticRunId = Store.DiagnosticRunId,
            AuditRunDirectory = auditRunDirectory,
            RunAttemptId = runAttemptId
        });

    public async Task<QreRunDiagnosticsSummary> CompleteAsync(string? entryOutcome)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            throw new InvalidOperationException("Diagnostics were already completed.");
        }
        var manifest = await Store.CompleteAsync(Diagnostics, Coverage, entryOutcome).ConfigureAwait(false);
        var counters = manifest.Counters;
        return new QreRunDiagnosticsSummary
        {
            Mode = manifest.Mode,
            Status = entryOutcome != null && counters?.ModelCallsStarted == 0
                ? "model_call_not_started"
                : manifest.CompletionStatus,
            RunDirectory = Store.RunDirectory,
            TransportCapture = Coverage.TransportCapture,
            ModelCalls = counters?.ModelCallsStarted ?? 0,
            HttpAttempts = counters?.HttpAttemptsStarted ?? 0,
            RecordsDropped = counters?.RecordsDropped ?? 0,
            EvidenceIncomplete = counters?.EvidenceIncomplete ?? true,
            ReasonCode = entryOutcome
        };
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Volatile.Read(ref _completed) == 0)
            {
                await CompleteAsync(null).ConfigureAwait(false);
            }
        }
        finally
        {
            await Store.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class DeferredSink : IQreOutboundDiagnosticSink
    {
        public IQreOutboundDiagnosticSink? Target { get; set; }

        public bool TryWrite(QreOutboundDiagnosticRecord record, ReadOnlyMemory<byte> utf8Json)
            => Target?.TryWrite(record, utf8Json) ?? false;
    }
}
