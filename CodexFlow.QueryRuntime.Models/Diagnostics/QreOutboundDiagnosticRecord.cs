using System.Text.Json.Serialization;

namespace CodexFlow.QueryRuntime.Models.Diagnostics;

/// <summary>
/// One projected outbound diagnostic event. Every field is typed and allow-listed;
/// the envelope never carries arbitrary SDK objects, raw bodies, headers, URLs,
/// exception messages or plaintext tool/model names.
/// </summary>
public sealed record QreOutboundDiagnosticRecord
{
    public required string SchemaVersion { get; init; }

    public required string ProjectionPolicyVersion { get; init; }

    public required string NormalizerVersion { get; init; }

    /// <summary>Monotonic within one diagnostic segment. Gaps mean dropped records.</summary>
    public required long Sequence { get; init; }

    /// <summary>One of <see cref="QreDiagnosticEventTypes"/>.</summary>
    public required string EventType { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Monotonic milliseconds since the diagnostic segment started.</summary>
    public required double ElapsedMs { get; init; }

    public required string SegmentId { get; init; }

    /// <summary>Package-local alias of the run/recovery attempt, or <c>unavailable</c>.</summary>
    public required string RunAttemptAlias { get; init; }

    public string? StepAlias { get; init; }

    /// <summary>Authoritative Runtime model attempt ordinal; null when unavailable.</summary>
    public int? RuntimeModelAttemptOrdinal { get; init; }

    /// <summary><c>present</c> or <c>unavailable</c>; unavailable is never defaulted to 1.</summary>
    public required string RuntimeModelAttemptOrdinalStatus { get; init; }

    public string? ModelCallId { get; init; }

    public string? HttpAttemptId { get; init; }

    /// <summary>Per model call ordinal of handler-visible HTTP sends.</summary>
    public int? AttemptOrdinal { get; init; }

    /// <summary>One of <see cref="QreDiagnosticStages"/>.</summary>
    public string? Stage { get; init; }

    public string? ApiMode { get; init; }

    public string? ProviderCategory { get; init; }

    public string? SdkVersion { get; init; }

    public string? AdapterVersion { get; init; }

    /// <summary>One of <see cref="QreDiagnosticObservationPoints"/>.</summary>
    public required string ObservationPoint { get; init; }

    /// <summary><c>handler_visible</c> for HTTP events; lower-level retries and redirects are not observed.</summary>
    public required string AttemptCoverage { get; init; }

    /// <summary>One of <see cref="QreDiagnosticCaptureStatus"/>.</summary>
    public required string CaptureStatus { get; init; }

    public string? ReasonCode { get; init; }

    /// <summary><c>correlated</c> or <c>uncorrelated</c>.</summary>
    public required string Correlation { get; init; }

    public QreSemanticRequest? Request { get; init; }

    public QreHttpRequestMetadata? HttpRequest { get; init; }

    public QreHttpResponseMetadata? HttpResponse { get; init; }

    public QreHttpAttemptOutcome? HttpOutcome { get; init; }

    public QreModelCallOutcome? ModelOutcome { get; init; }
}

/// <summary>Allow-listed request line metadata observed before forwarding.</summary>
public sealed record QreHttpRequestMetadata
{
    public required string Method { get; init; }

    /// <summary>Known API route template such as <c>{endpoint}/chat/completions</c>, or <c>unknown</c>.</summary>
    public required string RouteTemplate { get; init; }

    /// <summary>Local alias of scheme+host+base path; never the URL itself.</summary>
    public required string EndpointAlias { get; init; }

    public required string HttpVersion { get; init; }

    /// <summary>e.g. <c>json</c>, <c>multipart</c>, <c>binary</c>, <c>absent</c>, <c>other</c>.</summary>
    public required string ContentKind { get; init; }

    /// <summary>Whether a Content-Length could be computed without reading the content.</summary>
    public required bool ContentLengthKnown { get; init; }

    public bool? ContentEncoded { get; init; }

    /// <summary>Bytes the observer saw written to the transport (Structure mode only).</summary>
    public long? ObservedRequestBytes { get; init; }
}

/// <summary>Allow-listed response header metadata.</summary>
public sealed record QreHttpResponseMetadata
{
    public required int StatusCode { get; init; }

    /// <summary><c>success</c>, <c>client_error</c>, <c>server_error</c>, <c>redirect</c>, <c>informational</c>.</summary>
    public required string StatusClass { get; init; }

    /// <summary><c>event_stream</c>, <c>json</c>, <c>text</c>, <c>other</c> or <c>absent</c>.</summary>
    public required string ContentTypeCategory { get; init; }

    /// <summary>Local alias of the provider request id; auxiliary evidence only.</summary>
    public string? ProviderRequestIdAlias { get; init; }

    public int? RetryAfterSeconds { get; init; }

    public required double HeadersElapsedMs { get; init; }

    public required string HttpVersion { get; init; }
}

/// <summary>End of one handler-visible HTTP attempt.</summary>
public sealed record QreHttpAttemptOutcome
{
    /// <summary>One of <see cref="QreDiagnosticFailurePhases"/> or <c>none</c>.</summary>
    public required string FailurePhase { get; init; }

    /// <summary>One of <see cref="QreDiagnosticClassifications"/>.</summary>
    public required string Classification { get; init; }

    /// <summary>One of <see cref="QreDiagnosticClassificationSources"/>.</summary>
    public required string ClassificationSource { get; init; }

    /// <summary>One of <see cref="QreDiagnosticStreamTerminations"/>.</summary>
    public required string StreamTermination { get; init; }

    /// <summary>Response bytes the caller actually read through the wrapper.</summary>
    public required long ResponseBytesRead { get; init; }

    /// <summary>Safe transport error enum name when the runtime exposes one.</summary>
    public string? TransportErrorKind { get; init; }

    public required double ElapsedMs { get; init; }
}

/// <summary>End of one adapter-level model call.</summary>
public sealed record QreModelCallOutcome
{
    /// <summary><c>completed</c>, <c>failed</c>, <c>cancelled</c> or <c>abandoned</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Runtime stop reason name when the model completed.</summary>
    public string? StopReason { get; init; }

    /// <summary>Whether the provider stream surfaced a finish reason.</summary>
    public required bool FinishReasonObserved { get; init; }

    public required string FailurePhase { get; init; }

    public required string Classification { get; init; }

    public required string ClassificationSource { get; init; }

    /// <summary>Stable Runtime error code for typed adapter errors; never an exception message.</summary>
    public string? ErrorCode { get; init; }

    public string? ErrorCategory { get; init; }

    public required int ProtocolEventCount { get; init; }

    public required int HttpAttemptCount { get; init; }

    public required double ElapsedMs { get; init; }
}

/// <summary>
/// Common, provider-neutral request semantics used at every observation point.
/// Only structure, counts, aliases and reviewed control parameters are kept.
/// </summary>
public sealed record QreSemanticRequest
{
    /// <summary>Source shape: <c>runtime</c>, <c>meai</c>, <c>chat_completions</c>, <c>responses</c>, <c>anthropic_messages</c>.</summary>
    public required string Protocol { get; init; }

    public required QreSemanticMessages Messages { get; init; }

    public required QreSemanticTools Tools { get; init; }

    public required QreSemanticToolChoice ToolChoice { get; init; }

    public required QreSemanticNumber Temperature { get; init; }

    public required QreSemanticNumber MaxOutputTokens { get; init; }

    public required QreSemanticValue ResponseFormat { get; init; }

    public required QreSemanticValue Stream { get; init; }

    public required QreSemanticModel Model { get; init; }

    /// <summary>Count of top-level fields no registered rule inspected. Names are never kept.</summary>
    public required int UncheckedFieldCount { get; init; }

    /// <summary>Registered protocol observations such as <c>system_hoisted_to_top_level</c>.</summary>
    public IReadOnlyList<string> Notes { get; init => field = value ?? []; } = [];
}

public sealed record QreSemanticMessages
{
    public required string State { get; init; }

    public required int Count { get; init; }

    public required IReadOnlyList<QreSemanticMessage> Items { get; init; }

    /// <summary>Tool results whose call id matches an earlier tool call in the same request.</summary>
    public required int LinkedToolResults { get; init; }

    public required int UnlinkedToolResults { get; init; }
}

public sealed record QreSemanticMessage
{
    /// <summary><c>system</c>, <c>user</c>, <c>assistant</c>, <c>tool</c> or <c>other</c>.</summary>
    public required string Role { get; init; }

    /// <summary>Item kinds in order: <c>text</c>, <c>reasoning</c>, <c>tool_call</c>, <c>tool_result</c>, <c>image</c>, <c>other</c>.</summary>
    public required IReadOnlyList<string> ItemKinds { get; init; }
}

public sealed record QreSemanticTools
{
    public required string State { get; init; }

    public required int Count { get; init; }

    /// <summary>Case-sensitive (ordinal) package-local aliases in declaration order.</summary>
    public required IReadOnlyList<string> Aliases { get; init; }
}

public sealed record QreSemanticToolChoice
{
    public required string State { get; init; }

    /// <summary><c>none</c>, <c>auto</c>, <c>required</c>, <c>require_specific</c> or <c>unspecified</c>.</summary>
    public required string Mode { get; init; }

    public string? RequiredToolAlias { get; init; }

    /// <summary>
    /// Relation of the required name to declared tools computed in memory:
    /// <c>declared_exact</c>, <c>case_only_mismatch</c>, <c>not_declared</c> or <c>not_applicable</c>.
    /// </summary>
    public required string RequiredToolRelation { get; init; }
}

public sealed record QreSemanticNumber
{
    public required string State { get; init; }

    public double? Value { get; init; }

    /// <summary>Protocol field path the value came from, e.g. <c>options.temperature</c>.</summary>
    public string? SourcePath { get; init; }
}

public sealed record QreSemanticValue
{
    public required string State { get; init; }

    public string? Value { get; init; }

    public string? SourcePath { get; init; }
}

public sealed record QreSemanticModel
{
    public required string State { get; init; }

    /// <summary>Package-local model alias; the plaintext and mapping never leave memory.</summary>
    public string? Alias { get; init; }

    /// <summary><c>explicit</c>, <c>descriptor_default</c> or <c>unknown</c>.</summary>
    public required string Source { get; init; }

    /// <summary>Alias of the effective model when the SDK falls back to a known descriptor default.</summary>
    public string? EffectiveAlias { get; init; }
}

public static class QreDiagnosticEventTypes
{
    public const string ModelCallStarted = "model_call_started";
    public const string AdapterPrepared = "adapter_prepared";
    public const string HttpAttemptStarted = "http_attempt_started";
    public const string RequestStructureObserved = "request_structure_observed";
    public const string HttpHeadersReceived = "http_headers_received";
    public const string HttpAttemptEnded = "http_attempt_ended";
    public const string ModelCallEnded = "model_call_ended";
}

public static class QreDiagnosticStages
{
    public const string RuntimePrepared = "runtime_prepared";
    public const string AdapterPrepared = "adapter_prepared";
    public const string HttpPrepared = "http_prepared";
    public const string HttpResponse = "http_response";
    public const string ModelCall = "model_call";
}

public static class QreDiagnosticObservationPoints
{
    public const string Runtime = "runtime";
    public const string Adapter = "adapter";
    public const string HttpHandler = "http_handler";
    public const string RequestContent = "request_content";
    public const string StreamWrapper = "stream_wrapper";
}

public static class QreDiagnosticCaptureStatus
{
    public const string Complete = "complete";
    public const string Partial = "partial";
    public const string Omitted = "omitted";
    public const string Unsupported = "unsupported";
    public const string Failed = "failed";
    public const string NotEnabled = "not_enabled";
}

public static class QreDiagnosticFieldStates
{
    public const string Present = "present";
    public const string Absent = "absent";
    public const string Redacted = "redacted";
    public const string Unobserved = "unobserved";
}

public static class QreDiagnosticFailurePhases
{
    public const string None = "none";
    public const string BeforeHeaders = "before_headers";
    public const string ResponseRead = "response_read";
    public const string AdapterProtocol = "adapter_protocol";
    public const string RuntimeValidation = "runtime_validation";
    public const string Unknown = "unknown";
}

public static class QreDiagnosticClassificationSources
{
    public const string Handler = "handler";
    public const string StreamWrapper = "stream_wrapper";
    public const string Adapter = "adapter";
    public const string Runtime = "runtime";
}

public static class QreDiagnosticClassifications
{
    public const string None = "none";
    public const string HttpStatusFailure = "http_status_failure";
    public const string TransportError = "transport_error";
    public const string HttpClientTimeout = "http_client_timeout";
    public const string CallerCancelled = "caller_cancelled";
    public const string CancellationTimeoutRace = "cancellation_timeout_race";
    public const string ProtocolError = "protocol_error";
    public const string MissingFinishReason = "missing_finish_reason";
    public const string ReadError = "read_error";
    public const string SdkParseFailure = "sdk_parse_failure";
    public const string Unknown = "unknown";
}

public static class QreDiagnosticStreamTerminations
{
    public const string Eof = "eof";
    public const string DisposedBeforeEof = "disposed_before_eof";
    public const string Cancelled = "cancelled";
    public const string ReadError = "read_error";
    public const string NotRead = "not_read";
    public const string NoResponse = "no_response";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(QreOutboundDiagnosticRecord))]
public sealed partial class QreOutboundDiagnosticsJsonContext : JsonSerializerContext;
