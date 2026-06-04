using System.Security.Cryptography;
using System.Text;
using CodexFlow.QueryRuntime.Abstractions;

namespace CodexFlow.QueryRuntime.Experimental;

/// <summary>
/// Seed values and canonical projection used by strict, byte-identical replay.
/// </summary>
public sealed record DeterministicReplaySeed(
    int SchemaVersion,
    DateTimeOffset BaseTimestamp,
    Guid QueryId);

public static class DeterministicReplay
{
    private const char UnitSeparator = '';
    private const char RecordSeparator = '';

    /// <summary>
    /// The set of engine-emitted record types that form the deterministic decision
    /// trajectory. Harness envelope records (run.started/run.completed) and run-scoped
    /// identifiers (RunId/SessionId) are intentionally excluded from the canonical
    /// projection because they are not part of the replayable semantic trace.
    /// </summary>
    private static readonly HashSet<string> EngineEventTypes = new(StringComparer.Ordinal)
    {
        "model.request",
        "model.response",
        "tool.call.requested",
        "tool.execution.started",
        "tool.execution.completed",
        "round.started",
        "round.completed",
        "runtime.terminated",
        "runtime.error"
    };

    /// <summary>Reads the recorded schema version from the source trace (0 when unversioned).</summary>
    public static int ReadSchemaVersion(string traceFilePath)
    {
        var records = JsonlTraceStore.ReadRecords(traceFilePath);
        var started = records.FirstOrDefault(static record => record.Type == "run.started");
        var version = started?.TryGetLong(QueryRuntimeTraceSchema.VersionField);
        return version.HasValue ? (int)version.Value : QueryRuntimeTraceSchema.LegacyUnversioned;
    }

    /// <summary>
    /// Reads the deterministic seed (schema version, base timestamp, and source query id)
    /// from a recorded trace so a strict replay can reproduce identical clock and ids.
    /// </summary>
    public static DeterministicReplaySeed ReadSeed(string traceFilePath)
    {
        var records = JsonlTraceStore.ReadRecords(traceFilePath);
        var started = records.FirstOrDefault(static record => record.Type == "run.started");
        var version = started?.TryGetLong(QueryRuntimeTraceSchema.VersionField);

        var baseTimestamp = ReadTimestamp(started) ?? DateTimeOffset.UnixEpoch;

        var firstEngineEvent = records.FirstOrDefault(static record => EngineEventTypes.Contains(record.Type));
        var queryId = ParseQueryId(firstEngineEvent?.TryGetString("QueryId"));

        return new DeterministicReplaySeed(
            version.HasValue ? (int)version.Value : QueryRuntimeTraceSchema.LegacyUnversioned,
            baseTimestamp,
            queryId);
    }

    /// <summary>
    /// Computes a stable SHA-256 digest over the deterministic engine-event projection of a
    /// trace. Two strict replays of the same source trace and runtime version yield the same
    /// digest; the digest is independent of run-scoped RunId/SessionId.
    /// </summary>
    public static string ComputeCanonicalDigest(string traceFilePath)
    {
        var records = JsonlTraceStore.ReadRecords(traceFilePath);
        var builder = new StringBuilder();
        foreach (var record in records.Where(static record => EngineEventTypes.Contains(record.Type)))
        {
            builder.Append(record.Type).Append(UnitSeparator);
            builder.Append(record.TryGetLong("Seq")?.ToString() ?? string.Empty).Append(UnitSeparator);
            builder.Append(record.TryGetString("RuntimeEventType") ?? string.Empty).Append(UnitSeparator);
            builder.Append(record.TryGetString("QueryId") ?? string.Empty).Append(UnitSeparator);
            builder.Append(record.TryGetString("Timestamp") ?? string.Empty).Append(UnitSeparator);
            builder.Append(record.TryGetData(out var data) ? data.GetRawText() : string.Empty);
            builder.Append(RecordSeparator);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static DateTimeOffset? ReadTimestamp(JsonlTraceNodeRecord? record)
    {
        var raw = record?.TryGetString("Timestamp");
        return raw != null && DateTimeOffset.TryParse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static Guid ParseQueryId(string? raw)
        => !string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw, out var parsed)
            ? parsed
            : Guid.Empty;
}
