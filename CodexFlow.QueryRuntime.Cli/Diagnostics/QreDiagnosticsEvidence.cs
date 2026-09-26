using CodexFlow.QueryRuntime.Models.Diagnostics;

namespace CodexFlow.QueryRuntime.Cli.Diagnostics;

/// <summary>Checks the records themselves, independently of manifest claims.</summary>
internal static class QreDiagnosticsEvidence
{
    public static bool LifecyclesComplete(IReadOnlyList<QreOutboundDiagnosticRecord> records)
    {
        if (records.Any(r =>
                (r.EventType is QreDiagnosticEventTypes.ModelCallStarted or QreDiagnosticEventTypes.ModelCallEnded && r.ModelCallId == null) ||
                (IsHttp(r) && r.HttpAttemptId == null)))
        {
            return false;
        }
        return records.Where(r => r.ModelCallId != null).GroupBy(r => (r.SegmentId, r.ModelCallId))
                   .All(g => Closed(g, QreDiagnosticEventTypes.ModelCallStarted, QreDiagnosticEventTypes.ModelCallEnded)) &&
               HttpAttempts(records).All(g => Closed(g, QreDiagnosticEventTypes.HttpAttemptStarted, QreDiagnosticEventTypes.HttpAttemptEnded));
    }

    public static IReadOnlyList<QreOutboundDiagnosticRecord[]> HttpAttempts(IReadOnlyList<QreOutboundDiagnosticRecord> records)
        => records.Where(r => r.HttpAttemptId != null)
            .GroupBy(r => (r.SegmentId, r.ModelCallId, r.HttpAttemptId))
            .OrderBy(g => g.Min(r => r.Sequence))
            .Select(g => g.OrderBy(r => r.Sequence).ToArray()).ToArray();

    private static bool IsHttp(QreOutboundDiagnosticRecord record)
        => record.EventType is QreDiagnosticEventTypes.HttpAttemptStarted or QreDiagnosticEventTypes.HttpAttemptEnded
            or QreDiagnosticEventTypes.HttpHeadersReceived or QreDiagnosticEventTypes.RequestStructureObserved;

    private static bool Closed(IEnumerable<QreOutboundDiagnosticRecord> records, string start, string end)
    {
        var starts = records.Where(r => r.EventType == start).ToArray();
        var ends = records.Where(r => r.EventType == end).ToArray();
        return starts.Length == 1 && ends.Length == 1 && starts[0].Sequence < ends[0].Sequence;
    }
}
