using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Cli.Diagnostics;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using CodexFlow.QueryRuntime.Protocol;
using CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics.DiagnosticsTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Cli;

public sealed class QreDiagnosticsEvidenceRegressionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Dictionary<string, string> Empty = new();

    [Theory]
    [InlineData("model")]
    [InlineData("tools")]
    public async Task CrossPackageIdentity_RequiresExplicitAliasMapping(string field)
    {
        var left = await Capture();
        var right = left with
        {
            Manifest = left.Manifest! with { SegmentId = "other" },
            Records = left.Records.Select(r => r.Request == null ? r : r with
            {
                Request = field == "model"
                    ? r.Request with { Model = r.Request.Model with { Alias = r.Request.Model.Alias == null ? null : "model-other",
                        EffectiveAlias = r.Request.Model.EffectiveAlias == null ? null : "model-other" } }
                    : r.Request with { Tools = r.Request.Tools with { Aliases = ["tool-other"] } }
            }).ToArray()
        };
        Assert.Equal("not_comparable", Compare(left, right).Result);
        var mapped = new Dictionary<string, string> { ["model-1"] = field == "model" ? "model-other" : "model-1",
            ["tool-1"] = field == "tools" ? "tool-other" : "tool-1" };
        Assert.Equal("identical", QreDiagnosticsComparer.Compare(left, right, Empty, mapped).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedMissingOrPartialStructure_NeverPasses(bool partial)
    {
        var doc = await Capture();
        doc = doc with { Records = doc.Records.Select(r => r.EventType != QreDiagnosticEventTypes.RequestStructureObserved ? r :
            r with { Request = partial ? r.Request : null, CaptureStatus = partial ? "partial" : "omitted" }).ToArray() };
        Assert.Equal("not_comparable", Compare(doc, doc).Result);
        Assert.Equal("insufficient_evidence", Assert.Single(QreDiagnosticsAnalyzer.Analyze(doc)).Verdict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameSegment_RejectsUnmatchedCallsOnEitherSide(bool reverse)
    {
        var doc = await Capture();
        var extra = doc with { Records = doc.Records.Concat(doc.Records.Select(r => r with
        {
            Sequence = r.Sequence + doc.Records.Count, ModelCallId = "mc-extra", StepAlias = "step-extra",
            HttpAttemptId = r.HttpAttemptId == null ? null : "ha-extra"
        })).ToArray() };
        var result = reverse ? Compare(extra, doc) : Compare(doc, extra);
        Assert.Equal("alignment_ambiguous", result.Result);
        Assert.Equal("call_count_differs", result.ReasonCode);
    }

    [Fact]
    public async Task ExplicitStepMapping_MustCoverBothRuns()
    {
        var doc = await Capture();
        var extra = doc with
        {
            Manifest = doc.Manifest! with { SegmentId = "other" },
            Records = doc.Records.Concat(doc.Records.Select(r => r with
            { Sequence = r.Sequence + doc.Records.Count, ModelCallId = "extra", StepAlias = "extra" })).ToArray()
        };
        var result = QreDiagnosticsComparer.Compare(doc, extra, new Dictionary<string, string> { ["step-1"] = "step-1" }, Empty);
        Assert.Equal("alignment_ambiguous", result.Result);
        Assert.Equal("step_map_incomplete_or_ambiguous", result.ReasonCode);
    }

    [Theory]
    [InlineData("tail")]
    [InlineData("duplicate")]
    [InlineData("missing_end")]
    public async Task Reader_VerifiesCountsSequencesAndLifecycles(string damage)
    {
        var doc = await Capture();
        var records = damage switch
        {
            "tail" => doc.Records.Take(4).ToArray(),
            "duplicate" => doc.Records.Select((r, i) => i == 1 ? r with { Sequence = 1 } : r).ToArray(),
            _ => doc.Records.Where(r => r.EventType != QreDiagnosticEventTypes.ModelCallEnded).ToArray()
        };
        // Even updating the manifest count cannot hide an unclosed call.
        if (damage == "missing_end")
            doc = doc with { Manifest = doc.Manifest! with { Counters = doc.Manifest.Counters! with { RecordsWritten = records.Length } } };
        var root = Path.Combine(Path.GetTempPath(), "qre-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(doc.Manifest,
                QreDiagnosticsJsonContext.Default.QreDiagnosticsManifest), Ct);
            await File.WriteAllLinesAsync(Path.Combine(root, "events.jsonl"), records.Select(r => JsonSerializer.Serialize(r,
                QreOutboundDiagnosticsJsonContext.Default.QreOutboundDiagnosticRecord)), Ct);
            var loaded = QreDiagnosticsReader.Load(root);
            Assert.True(loaded.Integrity.EvidenceIncomplete);
            Assert.NotEqual("consistent", QreDiagnosticsAnalyzer.RunVerdict(loaded, QreDiagnosticsAnalyzer.Analyze(loaded)));
            Assert.Equal("not_comparable", Compare(loaded, loaded).Result);
            if (damage == "tail") Assert.True(loaded.Integrity.RecordCountMismatch);
            if (damage == "duplicate") Assert.True(loaded.Integrity.InvalidSequences);
            if (damage == "missing_end") Assert.True(loaded.Integrity.IncompleteLifecycles);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryHttpAttempt_IsComparedAndMustHaveStructure(bool omitted)
    {
        var doc = AddRetry(await Capture());
        Assert.Equal("consistent", Assert.Single(QreDiagnosticsAnalyzer.Analyze(doc)).Verdict);
        var changed = doc with { Records = doc.Records.Select(r =>
            r.AttemptOrdinal != 2 || r.Request == null ? r : r with
            {
                Request = omitted ? null : r.Request with { MaxOutputTokens = r.Request.MaxOutputTokens with { Value = 999 } },
                CaptureStatus = omitted ? "omitted" : "complete"
            }).ToArray() };
        var analysis = Assert.Single(QreDiagnosticsAnalyzer.Analyze(changed));
        Assert.Equal(omitted ? "insufficient_evidence" : "unexpected_change", analysis.Verdict);
        Assert.Equal(omitted ? "not_comparable" : "different", Compare(doc, changed).Result);
        if (!omitted)
        {
            var secondSequence = changed.Records.Single(r => r.AttemptOrdinal == 2 && r.Request != null).Sequence;
            Assert.Contains(analysis.Findings, f => f.FieldPath == "maxOutputTokens" && f.EvidenceSequences.Contains(secondSequence));
        }
    }

    [Fact]
    public async Task AddedHttpAttempt_IsNotIgnored()
    {
        var doc = await Capture();
        var result = Compare(doc, AddRetry(doc));
        Assert.Equal("different", result.Result);
        Assert.Contains(result.Differences, f => f.FieldPath == "httpAttempts.count");
    }

    [Theory]
    [InlineData("chat_completions", "max_tokens")]
    [InlineData("responses", "max_output_tokens")]
    [InlineData("anthropic_messages", "max_tokens")]
    public void NumericProjection_PreservesNullAndRejectsInvalidTypes(string api, string tokens)
    {
        var diagnostics = Create(new InMemoryDiagnosticSink());
        foreach (var (raw, state, value) in new (string?, string, double?)[]
                 { (null, "absent", null), ("null", "present", null), ("7", "present", 7),
                   ("\"secret-invalid-value\"", "invalid", null), ("1e9999", "invalid", null) })
        {
            var json = raw == null ? "{}" : "{\"temperature\":" + raw + ",\"" + tokens + "\":" + raw + "}";
            var (request, status, _) = QreSemanticProjector.FromHttpJson(Encoding.UTF8.GetBytes(json), api, 32, diagnostics);
            Assert.Equal("complete", status);
            Assert.Equal(state, request!.Temperature.State);
            Assert.Equal(value, request.Temperature.Value);
            Assert.Equal(state, request.MaxOutputTokens.State);
            Assert.Equal(value, request.MaxOutputTokens.Value);
        }
    }

    [Theory]
    [InlineData("chat_completions", "{\"response_format\":null}", "present")]
    [InlineData("responses", "{\"text\":{\"format\":null}}", "present")]
    [InlineData("responses", "{\"text\":{\"format\":42}}", "invalid")]
    public void FormatProjection_PreservesNullAndTypeValidity(string api, string json, string state)
    {
        var (request, _, _) = QreSemanticProjector.FromHttpJson(Encoding.UTF8.GetBytes(json), api, 32, Create(new InMemoryDiagnosticSink()));
        Assert.Equal(state, request!.ResponseFormat.State);
        Assert.Null(request.ResponseFormat.Value);
    }

    [Theory]
    [InlineData("present", "unexpected_change")]
    [InlineData("absent", "unexpected_change")]
    [InlineData("redacted", "not_comparable")]
    [InlineData("unobserved", "not_comparable")]
    [InlineData("invalid", "not_comparable")]
    public async Task NumericComparison_HandlesNullAndUnknownStates(string state, string classification)
    {
        var doc = await Capture();
        doc = doc with { Records = doc.Records.Select(r => r.EventType != QreDiagnosticEventTypes.RequestStructureObserved ? r :
            r with { Request = r.Request! with { MaxOutputTokens = r.Request!.MaxOutputTokens with { State = state, Value = null } } }).ToArray() };
        var call = Assert.Single(QreDiagnosticsAnalyzer.Analyze(doc));
        Assert.Contains(call.Findings, f => f.FieldPath == "maxOutputTokens" && f.Classification == classification);
    }

    [Fact]
    public async Task EmptyRuns_AreNotVerified()
    {
        var doc = await Capture();
        doc = doc with { Records = [] };
        Assert.Equal("not_comparable", Compare(doc, doc).Result);
    }

    private static QreDiagnoseCompareOutput Compare(QreDiagnosticsDocument left, QreDiagnosticsDocument right)
        => QreDiagnosticsComparer.Compare(left, right, Empty, Empty);

    private static QreDiagnosticsDocument AddRetry(QreDiagnosticsDocument doc)
    {
        var records = doc.Records.Where(r => r.EventType != QreDiagnosticEventTypes.ModelCallEnded)
            .Concat(doc.Records.Where(r => r.HttpAttemptId != null).Select(r => r with { HttpAttemptId = "ha-second", AttemptOrdinal = 2 }))
            .Concat(doc.Records.Where(r => r.EventType == QreDiagnosticEventTypes.ModelCallEnded))
            .Select((r, i) => r with { Sequence = i + 1 }).ToArray();
        return doc with { Records = records, Manifest = doc.Manifest! with { Counters = doc.Manifest.Counters! with { RecordsWritten = records.Length } } };
    }

    private static async Task<QreDiagnosticsDocument> Capture()
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink);
        using var client = CreateModelClient(diagnostics, new DelegateHandler((_, _) =>
            Task.FromResult(Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("ok")))));
        await DrainAsync(client.StreamAsync(Request([Tool("alpha")], new RuntimeModelParameters(Model: ChatModel, MaxOutputTokens: 256)),
            new RuntimeModelAttemptContext(1), Ct), Ct);
        var records = sink.Records;
        var manifest = new QreDiagnosticsManifest
        {
            ManifestSchema = QreDiagnosticsManifest.CurrentSchema, EventSchema = QreOutboundDiagnosticSchema.SchemaVersion,
            NormalizerVersion = QreOutboundDiagnosticSchema.NormalizerVersion, ProjectionPolicyVersion = QreOutboundDiagnosticSchema.ProjectionPolicyVersion,
            DiagnosticRunId = "run", SegmentId = diagnostics.SegmentId, Mode = "structure", CompletionStatus = "complete", CreatedUtc = DateTimeOffset.UtcNow,
            Coverage = new() { TransportCapture = "handler", AttemptCoverage = "handler_visible" },
            Quotas = new() { MaxRequestCaptureBytes = 65536, MaxRecordBytes = 32768, MaxRunBytes = 8388608, MaxPendingRecords = 256,
                MaxPendingBytes = 2097152, MaxJsonDepth = 32, RetentionDays = 7 },
            Counters = new() { RecordsWritten = records.Count }
        };
        return new("run", manifest, records, new()
        {
            ManifestPresent = true, CompletionStatus = "complete", TruncatedTail = false, InvalidLines = 0,
            SequenceGaps = 0, VersionsSupported = true
        });
    }
}
