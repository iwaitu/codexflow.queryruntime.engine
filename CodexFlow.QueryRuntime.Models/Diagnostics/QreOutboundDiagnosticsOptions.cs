namespace CodexFlow.QueryRuntime.Models.Diagnostics;

/// <summary>How much outbound SDK evidence is collected. The default is <see cref="Off"/>.</summary>
public enum QreOutboundDiagnosticMode
{
    /// <summary>No scopes, no records, no content wrappers.</summary>
    Off = 0,

    /// <summary>Correlation, route, status and termination metadata. Request bodies are never read.</summary>
    Metadata = 1,

    /// <summary>
    /// Metadata plus a bounded side-channel observation of the serialized request,
    /// reduced to an allow-listed structure projection. Raw bytes are never persisted.
    /// </summary>
    Structure = 2
}

/// <summary>Diagnostics failure policy. The first release only supports best effort.</summary>
public enum QreOutboundDiagnosticFailurePolicy
{
    /// <summary>Diagnostic failures never change model calls; they are counted and reported.</summary>
    BestEffort = 0
}

/// <summary>
/// Outbound SDK diagnostics configuration. Capacities bound diagnostic capture only;
/// they never limit or alter business requests.
/// </summary>
public sealed record QreOutboundDiagnosticsOptions
{
    public const int MaxRetentionDays = 30;

    public QreOutboundDiagnosticMode Mode { get; init; } = QreOutboundDiagnosticMode.Off;

    public int MaxRequestCaptureBytes { get; init; } = 64 * 1024;

    public int MaxRecordBytes { get; init; } = 32 * 1024;

    public long MaxRunBytes { get; init; } = 8L * 1024 * 1024;

    public int MaxPendingRecords { get; init; } = 256;

    public long MaxPendingBytes { get; init; } = 2L * 1024 * 1024;

    public int MaxJsonDepth { get; init; } = 32;

    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);

    public int MaxStoredRuns { get; init; } = 100;

    public long MaxTotalStorageBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>Locked per release; records carry it so readers never mix policies silently.</summary>
    public string ProjectionPolicyVersion { get; init; } = QreOutboundDiagnosticSchema.ProjectionPolicyVersion;

    public QreOutboundDiagnosticFailurePolicy FailurePolicy { get; init; } = QreOutboundDiagnosticFailurePolicy.BestEffort;

    public bool IsEnabled => Mode != QreOutboundDiagnosticMode.Off;

    /// <summary>Validates configured bounds and throws for unsafe values.</summary>
    public QreOutboundDiagnosticsOptions Validate()
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }
        if (FailurePolicy != QreOutboundDiagnosticFailurePolicy.BestEffort)
        {
            throw new ArgumentOutOfRangeException(nameof(FailurePolicy), "Only BestEffort is supported.");
        }
        RequireRange(MaxRequestCaptureBytes, 1024, 16 * 1024 * 1024, nameof(MaxRequestCaptureBytes));
        RequireRange(MaxRecordBytes, 1024, 1024 * 1024, nameof(MaxRecordBytes));
        RequireRange(MaxRunBytes, 64 * 1024, 1024L * 1024 * 1024, nameof(MaxRunBytes));
        RequireRange(MaxPendingRecords, 1, 65_536, nameof(MaxPendingRecords));
        RequireRange(MaxPendingBytes, MaxRecordBytes, 256L * 1024 * 1024, nameof(MaxPendingBytes));
        RequireRange(MaxJsonDepth, 4, 256, nameof(MaxJsonDepth));
        RequireRange(MaxStoredRuns, 1, 10_000, nameof(MaxStoredRuns));
        RequireRange(MaxTotalStorageBytes, MaxRunBytes, 16L * 1024 * 1024 * 1024, nameof(MaxTotalStorageBytes));
        if (Retention <= TimeSpan.Zero || Retention > TimeSpan.FromDays(MaxRetentionDays))
        {
            throw new ArgumentOutOfRangeException(nameof(Retention), $"Retention must be within 1 tick and {MaxRetentionDays} days.");
        }
        if (string.IsNullOrWhiteSpace(ProjectionPolicyVersion))
        {
            throw new ArgumentException("ProjectionPolicyVersion is required.", nameof(ProjectionPolicyVersion));
        }
        return this;
    }

    private static void RequireRange(long value, long min, long max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, $"{name} must be between {min} and {max}.");
        }
    }
}

/// <summary>Version identifiers carried by every record.</summary>
public static class QreOutboundDiagnosticSchema
{
    public const string SchemaVersion = "qre.outbound-diagnostics/1";

    public const string ProjectionPolicyVersion = "qre.outbound-projection/1";

    public const string NormalizerVersion = "qre.outbound-normalizer/1";

    public const string AdapterVersion = "qre.meai-adapter/2";
}
