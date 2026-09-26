using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Models.Diagnostics;

namespace CodexFlow.QueryRuntime.Cli.Diagnostics;

/// <summary>
/// Builds a shareable diagnostic bundle. Every record is re-projected through the
/// allow-list (unknown vocabulary values are dropped, not copied), every alias is
/// regenerated package-locally, and only <c>manifest.json</c> and
/// <c>events.jsonl</c> are written: no local index, checkpoint, private trace,
/// environment variables or audit files.
/// </summary>
internal static class QreDiagnosticsExporter
{
    public const string ExportPolicyVersion = "qre.outbound-export/1";

    private static readonly HashSet<string> EventTypes = Set(QreDiagnosticEventTypes.ModelCallStarted, QreDiagnosticEventTypes.AdapterPrepared, QreDiagnosticEventTypes.HttpAttemptStarted, QreDiagnosticEventTypes.RequestStructureObserved, QreDiagnosticEventTypes.HttpHeadersReceived, QreDiagnosticEventTypes.HttpAttemptEnded, QreDiagnosticEventTypes.ModelCallEnded);
    private static readonly HashSet<string> Stages = Set(QreDiagnosticStages.RuntimePrepared, QreDiagnosticStages.AdapterPrepared, QreDiagnosticStages.HttpPrepared, QreDiagnosticStages.HttpResponse, QreDiagnosticStages.ModelCall);
    private static readonly HashSet<string> Points = Set(QreDiagnosticObservationPoints.Runtime, QreDiagnosticObservationPoints.Adapter, QreDiagnosticObservationPoints.HttpHandler, QreDiagnosticObservationPoints.RequestContent, QreDiagnosticObservationPoints.StreamWrapper);
    private static readonly HashSet<string> Captures = Set(QreDiagnosticCaptureStatus.Complete, QreDiagnosticCaptureStatus.Partial, QreDiagnosticCaptureStatus.Omitted, QreDiagnosticCaptureStatus.Unsupported, QreDiagnosticCaptureStatus.Failed, QreDiagnosticCaptureStatus.NotEnabled);
    private static readonly HashSet<string> States = Set(QreDiagnosticFieldStates.Present, QreDiagnosticFieldStates.Absent, QreDiagnosticFieldStates.Redacted, QreDiagnosticFieldStates.Unobserved);
    private static readonly HashSet<string> Phases = Set(QreDiagnosticFailurePhases.None, QreDiagnosticFailurePhases.BeforeHeaders, QreDiagnosticFailurePhases.ResponseRead, QreDiagnosticFailurePhases.AdapterProtocol, QreDiagnosticFailurePhases.RuntimeValidation, QreDiagnosticFailurePhases.Unknown);
    private static readonly HashSet<string> Sources = Set(QreDiagnosticClassificationSources.Handler, QreDiagnosticClassificationSources.StreamWrapper, QreDiagnosticClassificationSources.Adapter, QreDiagnosticClassificationSources.Runtime);
    private static readonly HashSet<string> Classes = Set(QreDiagnosticClassifications.None, QreDiagnosticClassifications.HttpStatusFailure, QreDiagnosticClassifications.TransportError, QreDiagnosticClassifications.HttpClientTimeout, QreDiagnosticClassifications.CallerCancelled, QreDiagnosticClassifications.CancellationTimeoutRace, QreDiagnosticClassifications.ProtocolError, QreDiagnosticClassifications.MissingFinishReason, QreDiagnosticClassifications.ReadError, QreDiagnosticClassifications.SdkParseFailure, QreDiagnosticClassifications.Unknown);
    private static readonly HashSet<string> Terminations = Set(QreDiagnosticStreamTerminations.Eof, QreDiagnosticStreamTerminations.DisposedBeforeEof, QreDiagnosticStreamTerminations.Cancelled, QreDiagnosticStreamTerminations.ReadError, QreDiagnosticStreamTerminations.NotRead, QreDiagnosticStreamTerminations.NoResponse);
    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal) { "system", "user", "assistant", "tool", "other" };
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "text", "reasoning", "tool_call", "tool_result", "image", "artifact", "other" };
    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal) { "none", "auto", "required", "require_specific", "unspecified", "other" };
    private static readonly HashSet<string> Relations = new(StringComparer.Ordinal) { "declared_exact", "case_only_mismatch", "not_declared", "not_applicable" };
    private static readonly HashSet<string> Formats = new(StringComparer.Ordinal) { "none", "json_object", "json_schema", "text", "other", "alternative_json_only" };
    private static readonly HashSet<string> Protocols = new(StringComparer.Ordinal) { "runtime", "meai", QreApiModeNames.ChatCompletions, QreApiModeNames.Responses, QreApiModeNames.AnthropicMessages };
    private static readonly HashSet<string> ApiModes = new(StringComparer.Ordinal) { QreApiModeNames.ChatCompletions, QreApiModeNames.Responses, QreApiModeNames.AnthropicMessages, "unknown" };
    private static readonly HashSet<string> Providers = new(QreTransportCapabilityMatrix.All.Select(static c => c.ProviderId).Append("custom"), StringComparer.Ordinal);
    private static readonly HashSet<string> Routes = new(StringComparer.Ordinal) { "{endpoint}/chat/completions", "{endpoint}/responses", "{endpoint}/messages", "unknown", "observed" };
    private static readonly HashSet<string> Notes = new(StringComparer.Ordinal) { "system_hoisted_to_top_level", "tool_result_in_user_role", "non_function_tools_present" };

    public static (int Records, int DroppedFields) Export(QreDiagnosticsDocument document, string output, bool force)
    {
        if (File.Exists(output) && !force)
        {
            throw new QreDiagnosticsInputException("output_exists", "Export output already exists; pass --force to replace it.");
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new QreDiagnosticsInputException("output_directory_missing", "Export output directory does not exist.");
        }

        var context = new ExportContext();
        var events = new StringBuilder();
        var count = 0;
        foreach (var record in document.Records)
        {
            var projected = Project(record, context);
            events.Append(JsonSerializer.Serialize(projected, QreOutboundDiagnosticsJsonContext.Default.QreOutboundDiagnosticRecord)).Append('\n');
            count++;
        }
        var source = document.Manifest;
        var manifest = new QreDiagnosticsManifest
        {
            ManifestSchema = QreDiagnosticsManifest.CurrentSchema,
            EventSchema = QreOutboundDiagnosticSchema.SchemaVersion,
            ProjectionPolicyVersion = source?.ProjectionPolicyVersion ?? QreOutboundDiagnosticSchema.ProjectionPolicyVersion,
            NormalizerVersion = source?.NormalizerVersion ?? QreOutboundDiagnosticSchema.NormalizerVersion,
            DiagnosticRunId = "export",
            SegmentId = context.Alias("segment", source?.SegmentId ?? "unknown"),
            Mode = source?.Mode is "metadata" or "structure" ? source.Mode : "unknown",
            CompletionStatus = source?.CompletionStatus is "complete" or "incomplete" or "in_progress" ? source.CompletionStatus : "incomplete",
            CreatedUtc = source?.CreatedUtc ?? DateTimeOffset.UnixEpoch,
            CompletedUtc = source?.CompletedUtc,
            Coverage = source?.Coverage == null
                ? new QreDiagnosticsCoverage { TransportCapture = "unknown", AttemptCoverage = "handler_visible" }
                : source.Coverage with
                {
                    ProviderCategory = Keep(source.Coverage.ProviderCategory, Providers, context),
                    ApiMode = Keep(source.Coverage.ApiMode, ApiModes, context)
                },
            Quotas = source?.Quotas ?? new QreDiagnosticsQuotas
            {
                MaxRequestCaptureBytes = 0,
                MaxRecordBytes = 0,
                MaxRunBytes = 0,
                MaxPendingRecords = 0,
                MaxPendingBytes = 0,
                MaxJsonDepth = 0,
                RetentionDays = 0
            },
            Counters = source?.Counters,
            EntryOutcome = source?.EntryOutcome is "runtime_initial_validation_rejected" ? source.EntryOutcome : null,
            Origin = "export",
            ExportPolicyVersion = ExportPolicyVersion
        };

        var temporary = output + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteEntry(archive, QreDiagnosticsStore.ManifestFileName, JsonSerializer.SerializeToUtf8Bytes(manifest, QreDiagnosticsJsonContext.Default.QreDiagnosticsManifest));
                WriteEntry(archive, QreDiagnosticsStore.EventsFileName, Encoding.UTF8.GetBytes(events.ToString()));
            }
            File.Move(temporary, output, overwrite: force);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        return (count, context.Dropped);
    }

    private static QreOutboundDiagnosticRecord Project(QreOutboundDiagnosticRecord record, ExportContext context)
        => new()
        {
            SchemaVersion = QreOutboundDiagnosticSchema.SchemaVersion,
            ProjectionPolicyVersion = record.ProjectionPolicyVersion == QreOutboundDiagnosticSchema.ProjectionPolicyVersion ? record.ProjectionPolicyVersion : Drop(context, "unknown"),
            NormalizerVersion = record.NormalizerVersion == QreOutboundDiagnosticSchema.NormalizerVersion ? record.NormalizerVersion : Drop(context, "unknown"),
            Sequence = record.Sequence,
            EventType = Keep(record.EventType, EventTypes, context) ?? "unknown",
            TimestampUtc = record.TimestampUtc,
            ElapsedMs = record.ElapsedMs,
            SegmentId = context.Alias("segment", record.SegmentId),
            RunAttemptAlias = record.RunAttemptAlias == QreOutboundDiagnostics.Unavailable ? record.RunAttemptAlias : context.Alias("run-attempt", record.RunAttemptAlias),
            StepAlias = record.StepAlias == null ? null : context.Alias("step", record.StepAlias),
            RuntimeModelAttemptOrdinal = record.RuntimeModelAttemptOrdinal,
            RuntimeModelAttemptOrdinalStatus = record.RuntimeModelAttemptOrdinalStatus is "present" or QreOutboundDiagnostics.Unavailable ? record.RuntimeModelAttemptOrdinalStatus : Drop(context, QreOutboundDiagnostics.Unavailable),
            ModelCallId = record.ModelCallId == null ? null : context.Alias("mc", record.ModelCallId),
            HttpAttemptId = record.HttpAttemptId == null ? null : context.Alias("ha", record.HttpAttemptId),
            AttemptOrdinal = record.AttemptOrdinal,
            Stage = Keep(record.Stage, Stages, context),
            ApiMode = Keep(record.ApiMode, ApiModes, context),
            ProviderCategory = Keep(record.ProviderCategory, Providers, context),
            SdkVersion = record.SdkVersion is { Length: <= 32 } version && version.All(static c => char.IsAsciiDigit(c) || c == '.') ? version : Drop(context, (string?)null),
            AdapterVersion = record.AdapterVersion == QreOutboundDiagnosticSchema.AdapterVersion ? record.AdapterVersion : Drop(context, (string?)null),
            ObservationPoint = Keep(record.ObservationPoint, Points, context) ?? "unknown",
            AttemptCoverage = record.AttemptCoverage is "handler_visible" or "not_applicable" ? record.AttemptCoverage : Drop(context, "unknown"),
            CaptureStatus = Keep(record.CaptureStatus, Captures, context) ?? QreDiagnosticCaptureStatus.Failed,
            ReasonCode = SafeCode(record.ReasonCode, context),
            Correlation = record.Correlation is "correlated" or "uncorrelated" ? record.Correlation : Drop(context, "uncorrelated"),
            Request = record.Request == null ? null : Project(record.Request, context),
            HttpRequest = record.HttpRequest == null ? null : record.HttpRequest with
            {
                Method = record.HttpRequest.Method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS" ? record.HttpRequest.Method : "OTHER",
                RouteTemplate = Keep(record.HttpRequest.RouteTemplate, Routes, context) ?? "unknown",
                EndpointAlias = context.Alias("endpoint", record.HttpRequest.EndpointAlias),
                HttpVersion = record.HttpRequest.HttpVersion is "1.0" or "1.1" or "2.0" or "3.0" or "observed" ? record.HttpRequest.HttpVersion : Drop(context, "unknown"),
                ContentKind = record.HttpRequest.ContentKind is "json" or "multipart" or "binary" or "text" or "absent" or "unknown" ? record.HttpRequest.ContentKind : Drop(context, "unknown")
            },
            HttpResponse = record.HttpResponse == null ? null : record.HttpResponse with
            {
                StatusClass = record.HttpResponse.StatusClass is "success" or "client_error" or "server_error" or "redirect" or "informational" ? record.HttpResponse.StatusClass : Drop(context, "unknown"),
                ContentTypeCategory = record.HttpResponse.ContentTypeCategory is "event_stream" or "json" or "text" or "other" or "absent" ? record.HttpResponse.ContentTypeCategory : Drop(context, "other"),
                ProviderRequestIdAlias = record.HttpResponse.ProviderRequestIdAlias == null ? null : context.Alias("provider-request", record.HttpResponse.ProviderRequestIdAlias),
                HttpVersion = record.HttpResponse.HttpVersion is "1.0" or "1.1" or "2.0" or "3.0" ? record.HttpResponse.HttpVersion : Drop(context, "unknown")
            },
            HttpOutcome = record.HttpOutcome == null ? null : record.HttpOutcome with
            {
                FailurePhase = Keep(record.HttpOutcome.FailurePhase, Phases, context) ?? QreDiagnosticFailurePhases.Unknown,
                Classification = Keep(record.HttpOutcome.Classification, Classes, context) ?? QreDiagnosticClassifications.Unknown,
                ClassificationSource = Keep(record.HttpOutcome.ClassificationSource, Sources, context) ?? "unknown",
                StreamTermination = Keep(record.HttpOutcome.StreamTermination, Terminations, context) ?? "unknown",
                TransportErrorKind = record.HttpOutcome.TransportErrorKind is { } kind && Enum.TryParse<HttpRequestError>(kind, out _) ? kind : Drop(context, (string?)null)
            },
            ModelOutcome = record.ModelOutcome == null ? null : record.ModelOutcome with
            {
                Outcome = record.ModelOutcome.Outcome is "completed" or "failed" or "cancelled" or "abandoned" ? record.ModelOutcome.Outcome : Drop(context, "unknown"),
                StopReason = record.ModelOutcome.StopReason is { } stop && Enum.TryParse<CodexFlow.QueryRuntime.Protocol.RuntimeModelStopReason>(stop, out _) ? stop : Drop(context, (string?)null),
                FailurePhase = Keep(record.ModelOutcome.FailurePhase, Phases, context) ?? QreDiagnosticFailurePhases.Unknown,
                Classification = Keep(record.ModelOutcome.Classification, Classes, context) ?? QreDiagnosticClassifications.Unknown,
                ClassificationSource = Keep(record.ModelOutcome.ClassificationSource, Sources, context) ?? "unknown",
                ErrorCode = SafeCode(record.ModelOutcome.ErrorCode, context),
                ErrorCategory = record.ModelOutcome.ErrorCategory is { } category && Enum.TryParse<CodexFlow.QueryRuntime.Protocol.RuntimeErrorCategory>(category, out _) ? category : Drop(context, (string?)null)
            }
        };

    private static QreSemanticRequest Project(QreSemanticRequest request, ExportContext context)
        => new()
        {
            Protocol = Keep(request.Protocol, Protocols, context) ?? "unknown",
            Messages = new QreSemanticMessages
            {
                State = Keep(request.Messages.State, States, context) ?? QreDiagnosticFieldStates.Unobserved,
                Count = request.Messages.Count,
                Items = request.Messages.Items.Select(message => new QreSemanticMessage
                {
                    Role = Keep(message.Role, Roles, context) ?? "other",
                    ItemKinds = message.ItemKinds.Select(kind => Keep(kind, Kinds, context) ?? "other").ToArray()
                }).ToArray(),
                LinkedToolResults = request.Messages.LinkedToolResults,
                UnlinkedToolResults = request.Messages.UnlinkedToolResults
            },
            Tools = new QreSemanticTools
            {
                State = Keep(request.Tools.State, States, context) ?? QreDiagnosticFieldStates.Unobserved,
                Count = request.Tools.Count,
                Aliases = request.Tools.Aliases.Select(alias => context.Alias("tool", alias)).ToArray()
            },
            ToolChoice = new QreSemanticToolChoice
            {
                State = Keep(request.ToolChoice.State, States, context) ?? QreDiagnosticFieldStates.Unobserved,
                Mode = Keep(request.ToolChoice.Mode, Modes, context) ?? "other",
                RequiredToolAlias = request.ToolChoice.RequiredToolAlias == null ? null : context.Alias("tool", request.ToolChoice.RequiredToolAlias),
                RequiredToolRelation = Keep(request.ToolChoice.RequiredToolRelation, Relations, context) ?? "not_applicable"
            },
            Temperature = Number(request.Temperature, context),
            MaxOutputTokens = Number(request.MaxOutputTokens, context),
            ResponseFormat = new QreSemanticValue
            {
                State = Keep(request.ResponseFormat.State, States, context) ?? QreDiagnosticFieldStates.Unobserved,
                Value = Keep(request.ResponseFormat.Value, Formats, context),
                SourcePath = SafePath(request.ResponseFormat.SourcePath, context)
            },
            Stream = new QreSemanticValue
            {
                State = Keep(request.Stream.State, States, context) ?? QreDiagnosticFieldStates.Unobserved,
                Value = request.Stream.Value is "true" or "false" ? request.Stream.Value : Drop(context, (string?)null),
                SourcePath = SafePath(request.Stream.SourcePath, context)
            },
            Model = new QreSemanticModel
            {
                State = Keep(request.Model.State, States, context) ?? QreDiagnosticFieldStates.Unobserved,
                Alias = request.Model.Alias == null ? null : context.Alias("model", request.Model.Alias),
                Source = request.Model.Source is "explicit" or "descriptor_default" or "unknown" ? request.Model.Source : Drop(context, "unknown"),
                EffectiveAlias = request.Model.EffectiveAlias == null ? null : context.Alias("model", request.Model.EffectiveAlias)
            },
            UncheckedFieldCount = request.UncheckedFieldCount,
            Notes = request.Notes.Where(note => Notes.Contains(note) || Drop(context, false)).ToArray()
        };

    private static QreSemanticNumber Number(QreSemanticNumber number, ExportContext context)
        => new()
        {
            State = Keep(number.State, States, context) ?? QreDiagnosticFieldStates.Unobserved,
            Value = number.Value is { } value && double.IsFinite(value) ? value : null,
            SourcePath = SafePath(number.SourcePath, context)
        };

    private static string? SafePath(string? path, ExportContext context)
        => path == null ? null
            : path.Length <= 64 && path.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_')
                ? path
                : Drop(context, (string?)null);

    private static string? SafeCode(string? code, ExportContext context)
        => code == null ? null
            : code.Length <= 96 && code.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')
                ? code
                : Drop(context, (string?)null);

    private static string? Keep(string? value, HashSet<string> vocabulary, ExportContext context)
        => value == null ? null : vocabulary.Contains(value) ? value : Drop(context, (string?)null);

    private static T Drop<T>(ExportContext context, T replacement)
    {
        context.Dropped++;
        return replacement;
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    private sealed class ExportContext
    {
        private readonly Dictionary<string, Dictionary<string, string>> _aliases = new(StringComparer.Ordinal);

        public int Dropped { get; set; }

        public string Alias(string kind, string value)
        {
            if (!_aliases.TryGetValue(kind, out var map))
            {
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                _aliases.Add(kind, map);
            }
            if (!map.TryGetValue(value, out var alias))
            {
                alias = $"{kind}-{map.Count + 1}";
                map.Add(value, alias);
            }
            return alias;
        }
    }
}
