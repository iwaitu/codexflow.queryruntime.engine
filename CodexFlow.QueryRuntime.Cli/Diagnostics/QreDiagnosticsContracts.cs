using System.Text.Json.Serialization;
using CodexFlow.QueryRuntime.Models.Diagnostics;

namespace CodexFlow.QueryRuntime.Cli.Diagnostics;

/// <summary>Versioned manifest of one diagnostic run directory or exported bundle.</summary>
internal sealed record QreDiagnosticsManifest
{
    public const string CurrentSchema = "qre.outbound-diagnostics.manifest/1";

    public required string ManifestSchema { get; init; }

    public required string EventSchema { get; init; }

    public required string ProjectionPolicyVersion { get; init; }

    public required string NormalizerVersion { get; init; }

    public required string DiagnosticRunId { get; init; }

    public required string SegmentId { get; init; }

    /// <summary><c>metadata</c> or <c>structure</c>.</summary>
    public required string Mode { get; init; }

    /// <summary><c>in_progress</c>, <c>complete</c> or <c>incomplete</c>.</summary>
    public required string CompletionStatus { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? CompletedUtc { get; init; }

    /// <summary>Always <c>client_side_handler</c>: records are client observations, not network proof.</summary>
    public string ObservationScope { get; init; } = "client_side_handler";

    public required QreDiagnosticsCoverage Coverage { get; init; }

    public required QreDiagnosticsQuotas Quotas { get; init; }

    public QreDiagnosticsCountersSnapshot? Counters { get; init; }

    /// <summary>Why no model call ran, e.g. <c>runtime_initial_validation_rejected</c>.</summary>
    public string? EntryOutcome { get; init; }

    /// <summary><c>local</c> for a run directory, <c>export</c> for a re-projected bundle.</summary>
    public string Origin { get; init; } = "local";

    public string? ExportPolicyVersion { get; init; }
}

internal sealed record QreDiagnosticsCoverage
{
    /// <summary><c>handler</c>, <c>transport_capture_unavailable</c> or <c>adapter_only</c>.</summary>
    public required string TransportCapture { get; init; }

    public required string AttemptCoverage { get; init; }

    public string? ProviderCategory { get; init; }

    public string? ApiMode { get; init; }

    public string? SdkVersion { get; init; }

    /// <summary>Capability matrix status for the Provider × API-mode cell.</summary>
    public string? CapabilityStatus { get; init; }

    public bool RequestStructure { get; init; }

    public bool ResponseBodies { get; init; }
}

internal sealed record QreDiagnosticsQuotas
{
    public required int MaxRequestCaptureBytes { get; init; }

    public required int MaxRecordBytes { get; init; }

    public required long MaxRunBytes { get; init; }

    public required int MaxPendingRecords { get; init; }

    public required long MaxPendingBytes { get; init; }

    public required int MaxJsonDepth { get; init; }

    public required int RetentionDays { get; init; }
}

internal sealed record QreDiagnosticsCountersSnapshot
{
    public long RecordsEmitted { get; init; }

    public long RecordsWritten { get; init; }

    public long RecordsDropped { get; init; }

    public long QueueDropped { get; init; }

    public long QuotaDropped { get; init; }

    public long WriteFailures { get; init; }

    public long RecordsOversized { get; init; }

    public long ProjectionFailures { get; init; }

    public long UncorrelatedHttpAttempts { get; init; }

    public long ModelCallsStarted { get; init; }

    public long ModelCallsEnded { get; init; }

    public long HttpAttemptsStarted { get; init; }

    public long HttpAttemptsEnded { get; init; }

    public bool FlushTimedOut { get; init; }

    public bool EvidenceIncomplete { get; init; }
}

/// <summary>Restricted local link from a diagnostic run to its local audit run. Never exported.</summary>
internal sealed record QreDiagnosticsLocalIndex
{
    public required string DiagnosticRunId { get; init; }

    public string? AuditRunDirectory { get; init; }

    public string? RunAttemptId { get; init; }
}

/// <summary>Diagnostics summary appended to <c>qre run --json</c> output.</summary>
internal sealed record QreRunDiagnosticsSummary
{
    public required string Mode { get; init; }

    public required string Status { get; init; }

    public string? RunDirectory { get; init; }

    public required string TransportCapture { get; init; }

    public string ObservationScope { get; init; } = "client_side_handler";

    public long ModelCalls { get; init; }

    public long HttpAttempts { get; init; }

    public long RecordsDropped { get; init; }

    public bool EvidenceIncomplete { get; init; }

    public string? ReasonCode { get; init; }
}

/// <summary>One cross-layer or cross-run difference.</summary>
internal sealed record QreDiagnosticFinding
{
    public string? ModelCallId { get; init; }

    public required string SourceStage { get; init; }

    public required string TargetStage { get; init; }

    public required string FieldPath { get; init; }

    public string? Before { get; init; }

    public string? After { get; init; }

    /// <summary><c>expected_transform</c>, <c>unexpected_change</c>, <c>not_comparable</c> or <c>insufficient_evidence</c>.</summary>
    public required string Classification { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? Rule { get; init; }

    public IReadOnlyList<long> EvidenceSequences { get; init; } = [];
}

internal sealed record QreModelCallAnalysis
{
    public required string ModelCallId { get; init; }

    public string? StepAlias { get; init; }

    public int? RuntimeModelAttemptOrdinal { get; init; }

    public required string RuntimeModelAttemptOrdinalStatus { get; init; }

    public string? Outcome { get; init; }

    public string? Classification { get; init; }

    public string? FailurePhase { get; init; }

    public int HttpAttempts { get; init; }

    public IReadOnlyList<string> HttpStatuses { get; init; } = [];

    public IReadOnlyList<string> StreamTerminations { get; init; } = [];

    /// <summary>Stages observed with complete evidence, in pipeline order.</summary>
    public IReadOnlyList<string> ObservedStages { get; init; } = [];

    /// <summary>First boundary with an unexpected change, e.g. <c>runtime_prepared-&gt;adapter_prepared</c>.</summary>
    public string? FirstUnexpectedBoundary { get; init; }

    /// <summary><c>consistent</c>, <c>unexpected_change</c> or <c>insufficient_evidence</c>.</summary>
    public required string Verdict { get; init; }

    public IReadOnlyList<QreDiagnosticFinding> Findings { get; init; } = [];

    /// <summary>Safe observations that are not differences, e.g. <c>http_preserves_case_only_mismatch</c>.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

internal sealed record QreDiagnosticsIntegrity
{
    public required bool ManifestPresent { get; init; }

    public required string CompletionStatus { get; init; }

    public required bool TruncatedTail { get; init; }

    public required int InvalidLines { get; init; }

    public required int SequenceGaps { get; init; }

    public required bool VersionsSupported { get; init; }

    public bool EvidenceIncomplete { get; init; }
}

internal sealed record QreDiagnoseInspectOutput
{
    public string Type { get; init; } = "qre.diagnose.inspect";

    public required string Run { get; init; }

    public required string Origin { get; init; }

    public required string Mode { get; init; }

    public required string ObservationScope { get; init; }

    public required QreDiagnosticsCoverage Coverage { get; init; }

    public required QreDiagnosticsIntegrity Integrity { get; init; }

    public string? EntryOutcome { get; init; }

    public required int RecordCount { get; init; }

    public required IReadOnlyList<QreModelCallAnalysis> ModelCalls { get; init; }

    /// <summary><c>consistent</c>, <c>unexpected_change</c>, <c>insufficient_evidence</c> or <c>model_call_not_started</c>.</summary>
    public required string Verdict { get; init; }

    public required IReadOnlyList<string> Limitations { get; init; }
}

internal sealed record QreDiagnoseCompareOutput
{
    public string Type { get; init; } = "qre.diagnose.compare";

    public required string Left { get; init; }

    public required string Right { get; init; }

    /// <summary><c>identical</c>, <c>different</c>, <c>not_comparable</c> or <c>alignment_ambiguous</c>.</summary>
    public required string Result { get; init; }

    public required string ReasonCode { get; init; }

    public required string Alignment { get; init; }

    public IReadOnlyList<QreAlignedCall> AlignedCalls { get; init; } = [];

    /// <summary>Earliest observable difference; not a proven root cause.</summary>
    public QreDiagnosticFinding? EarliestDifference { get; init; }

    public IReadOnlyList<QreDiagnosticFinding> Differences { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];
}

internal sealed record QreAlignedCall(string LeftModelCallId, string RightModelCallId, string Basis);

internal sealed record QreDiagnoseExportOutput
{
    public string Type { get; init; } = "qre.diagnose.export";

    public required string Run { get; init; }

    public required string Output { get; init; }

    public required int Records { get; init; }

    public required int DroppedFields { get; init; }

    public required string ExportPolicyVersion { get; init; }
}

internal sealed record QreDiagnoseRebuildOutput
{
    public string Type { get; init; } = "qre.diagnose.rebuild";

    public required string Fixture { get; init; }

    /// <summary><c>passed</c>, <c>failed</c> or <c>blocked</c>.</summary>
    public required string Result { get; init; }

    public required string ReasonCode { get; init; }

    public string Network { get; init; } = "offline_in_memory_transport";

    public string? ApiMode { get; init; }

    public string? SdkVersion { get; init; }

    public bool SdkVersionChanged { get; init; }

    public int HttpRequests { get; init; }

    public IReadOnlyList<QreFixtureAssertionResult> Assertions { get; init; } = [];

    public IReadOnlyList<string> Missing { get; init; } = [];

    public QreModelCallAnalysis? Analysis { get; init; }
}

internal sealed record QreFixtureAssertionResult(string Kind, string Target, string Expected, string Actual, bool Passed);

internal sealed record QreDiagnoseSkeletonOutput
{
    public string Type { get; init; } = "qre.diagnose.skeleton";

    public required string Run { get; init; }

    public required string Output { get; init; }

    public required string ModelCallId { get; init; }

    public required IReadOnlyList<string> Missing { get; init; }
}

/// <summary>
/// Offline SDK request-rebuild fixture. Content is human-authored and
/// non-sensitive; a skeleton converted from a diagnostic package lists what must
/// be filled in and reviewed before it can be declared runnable.
/// </summary>
internal sealed record QreRebuildFixture
{
    public const string CurrentSchema = "qre.sdk-rebuild-fixture/1";

    public required string Schema { get; init; }

    /// <summary><c>runnable</c> only after human completion and review; otherwise <c>skeleton</c>.</summary>
    public required string Status { get; init; }

    public string? Issue { get; init; }

    public required string ApiMode { get; init; }

    /// <summary>Model id used only to select the provider adapter offline.</summary>
    public required string Model { get; init; }

    public string? SdkVersion { get; init; }

    public string? AdapterVersion { get; init; }

    /// <summary><c>qre-cli</c> (the CLI options mapping) or <c>passthrough-empty</c> (a host that maps nothing).</summary>
    public string OptionsMapping { get; init; } = "qre-cli";

    public required QreFixtureRequest Request { get; init; }

    public IReadOnlyList<QreFixtureAssertion> Assertions { get; init; } = [];

    public IReadOnlyList<string> Missing { get; init; } = [];
}

internal sealed record QreFixtureRequest
{
    public required IReadOnlyList<QreFixtureMessage> Messages { get; init; }

    public IReadOnlyList<QreFixtureTool> Tools { get; init; } = [];

    public double? Temperature { get; init; }

    public int? MaxOutputTokens { get; init; }

    public bool RequireJsonObject { get; init; }

    public string? RequiredToolName { get; init; }
}

internal sealed record QreFixtureMessage
{
    public required string Role { get; init; }

    public required IReadOnlyList<QreFixtureItem> Items { get; init; }
}

internal sealed record QreFixtureItem
{
    /// <summary><c>text</c>, <c>tool_call</c> or <c>tool_result</c>.</summary>
    public required string Kind { get; init; }

    public string? Text { get; init; }

    public string? CallId { get; init; }

    public string? ToolName { get; init; }

    public string? ArgumentsJson { get; init; }
}

internal sealed record QreFixtureTool
{
    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public string InputSchemaJson { get; init; } = "{\"type\":\"object\"}";
}

/// <summary>
/// Behavioral assertion against the rebuilt request, e.g. <c>required_tool_still_required</c>,
/// <c>json_output_mapped</c>, <c>field_equals</c>, <c>no_unexpected_change</c>, <c>tool_result_links_preserved</c>.
/// </summary>
internal sealed record QreFixtureAssertion
{
    public required string Kind { get; init; }

    /// <summary>Stage for <c>field_equals</c>: <c>runtime_prepared</c>, <c>adapter_prepared</c> or <c>http_prepared</c>.</summary>
    public string? Stage { get; init; }

    public string? Field { get; init; }

    public string? Expected { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(QreDiagnosticsManifest))]
[JsonSerializable(typeof(QreDiagnosticsLocalIndex))]
[JsonSerializable(typeof(QreDiagnoseInspectOutput))]
[JsonSerializable(typeof(QreDiagnoseCompareOutput))]
[JsonSerializable(typeof(QreDiagnoseExportOutput))]
[JsonSerializable(typeof(QreDiagnoseRebuildOutput))]
[JsonSerializable(typeof(QreDiagnoseSkeletonOutput))]
[JsonSerializable(typeof(QreRebuildFixture))]
[JsonSerializable(typeof(QreRunDiagnosticsSummary))]
[JsonSerializable(typeof(QreDiagnoseErrorOutput))]
internal sealed partial class QreDiagnosticsJsonContext : JsonSerializerContext;

internal sealed record QreDiagnoseErrorOutput
{
    public string Type { get; init; } = "qre.diagnose.error";

    public required string ReasonCode { get; init; }

    public required string Message { get; init; }
}
