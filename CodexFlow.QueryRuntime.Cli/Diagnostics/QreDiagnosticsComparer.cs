using System.Globalization;
using CodexFlow.QueryRuntime.Models.Diagnostics;

namespace CodexFlow.QueryRuntime.Cli.Diagnostics;

/// <summary>
/// Cross-run comparison. Calls are aligned by id within one segment, by an
/// explicit step map, or by call order plus safe structural features; ambiguous
/// alignment is reported instead of guessing. Random ids, times and provider
/// request ids are ignored; array order and missing/null/default distinctions are
/// kept. Aliases from different packages are only compared through an explicit
/// mapping. The earliest observable difference is not presented as a root cause.
/// </summary>
internal static class QreDiagnosticsComparer
{
    private static readonly string[] StageOrder =
    [
        QreDiagnosticsAnalyzer.RuntimeStage,
        QreDiagnosticsAnalyzer.AdapterStage,
        QreDiagnosticsAnalyzer.HttpStage
    ];

    public static QreDiagnoseCompareOutput Compare(
        QreDiagnosticsDocument left,
        QreDiagnosticsDocument right,
        IReadOnlyDictionary<string, string> stepMap,
        IReadOnlyDictionary<string, string> aliasMap)
    {
        var leftName = QreDiagnosticsReader.Describe(left.Source);
        var rightName = QreDiagnosticsReader.Describe(right.Source);
        if (!left.Integrity.VersionsSupported || !right.Integrity.VersionsSupported)
        {
            return NotComparable(leftName, rightName, "normalizer_or_policy_version_unsupported");
        }
        var leftVersions = Versions(left);
        var rightVersions = Versions(right);
        if (leftVersions != rightVersions)
        {
            return NotComparable(leftName, rightName, "incompatible_versions", [$"left:{leftVersions}", $"right:{rightVersions}"]);
        }

        var leftCalls = Calls(left);
        var rightCalls = Calls(right);
        var sameSegment = left.Manifest != null && right.Manifest != null &&
                          string.Equals(left.Manifest.SegmentId, right.Manifest.SegmentId, StringComparison.Ordinal);
        var (aligned, alignment, reason) = Align(leftCalls, rightCalls, sameSegment, stepMap);
        if (aligned == null)
        {
            return new QreDiagnoseCompareOutput
            {
                Left = leftName,
                Right = rightName,
                Result = "alignment_ambiguous",
                ReasonCode = reason,
                Alignment = alignment
            };
        }

        var differences = new List<QreDiagnosticFinding>();
        var notes = new List<string>();
        var insufficient = false;
        foreach (var (leftCall, rightCall, _) in aligned)
        {
            foreach (var stage in StageOrder)
            {
                var l = Stage(leftCall.Records, stage);
                var r = Stage(rightCall.Records, stage);
                if (l == null || r == null)
                {
                    if (l != null || r != null)
                    {
                        insufficient = true;
                        differences.Add(Difference(leftCall.Id, stage, "*", l == null ? "unobserved" : "observed", r == null ? "unobserved" : "observed", QreFindingClass.InsufficientEvidence));
                    }
                    continue;
                }
                CompareSemantic(leftCall.Id, stage, l, r, sameSegment, aliasMap, differences, notes);
            }
            var lo = leftCall.Records.FirstOrDefault(static r => r.EventType == QreDiagnosticEventTypes.ModelCallEnded)?.ModelOutcome;
            var ro = rightCall.Records.FirstOrDefault(static r => r.EventType == QreDiagnosticEventTypes.ModelCallEnded)?.ModelOutcome;
            if (lo?.Outcome != ro?.Outcome || lo?.Classification != ro?.Classification)
            {
                differences.Add(Difference(leftCall.Id, QreDiagnosticStages.ModelCall, "outcome",
                    $"{lo?.Outcome}/{lo?.Classification}", $"{ro?.Outcome}/{ro?.Classification}", QreFindingClass.UnexpectedChange));
            }
        }
        if (left.Integrity.EvidenceIncomplete || right.Integrity.EvidenceIncomplete)
        {
            insufficient = true;
            notes.Add("evidence_incomplete_in_input");
        }

        var earliest = differences.FirstOrDefault(static d => d.Classification == QreFindingClass.UnexpectedChange);
        return new QreDiagnoseCompareOutput
        {
            Left = leftName,
            Right = rightName,
            Result = earliest != null ? "different" : insufficient ? "not_comparable" : "identical",
            ReasonCode = earliest != null ? "unexpected_difference" : insufficient ? "insufficient_evidence" : "no_difference",
            Alignment = alignment,
            AlignedCalls = aligned.Select(static a => new QreAlignedCall(a.Left.Id, a.Right.Id, a.Basis)).ToArray(),
            EarliestDifference = earliest,
            Differences = differences,
            Notes = notes.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private static void CompareSemantic(
        string callId,
        string stage,
        QreOutboundDiagnosticRecord leftRecord,
        QreOutboundDiagnosticRecord rightRecord,
        bool sameSegment,
        IReadOnlyDictionary<string, string> aliasMap,
        List<QreDiagnosticFinding> differences,
        List<string> notes)
    {
        var l = leftRecord.Request!;
        var r = rightRecord.Request!;
        void Check(string field, string? left, string? right)
        {
            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                differences.Add(Difference(callId, stage, field, left, right, QreFindingClass.UnexpectedChange, leftRecord.Sequence, rightRecord.Sequence));
            }
        }
        string? MapAlias(string? alias) => alias != null && aliasMap.TryGetValue(alias, out var mapped) ? mapped : alias;
        bool AliasesComparable(IEnumerable<string?> aliases) => sameSegment || aliases.All(a => a == null || aliasMap.ContainsKey(a));

        Check("messages", string.Join(',', QreDiagnosticsAnalyzer.Sequence(l.Messages)), string.Join(',', QreDiagnosticsAnalyzer.Sequence(r.Messages)));
        Check("messages.toolResultLinks", $"{l.Messages.LinkedToolResults}/{l.Messages.UnlinkedToolResults}", $"{r.Messages.LinkedToolResults}/{r.Messages.UnlinkedToolResults}");
        Check("tools.count", l.Tools.Count.ToString(CultureInfo.InvariantCulture), r.Tools.Count.ToString(CultureInfo.InvariantCulture));
        if (AliasesComparable(l.Tools.Aliases.Append(l.ToolChoice.RequiredToolAlias)))
        {
            Check("tools.aliases", string.Join(',', l.Tools.Aliases.Select(MapAlias)), string.Join(',', r.Tools.Aliases));
            Check("toolChoice.requiredTool", MapAlias(l.ToolChoice.RequiredToolAlias), r.ToolChoice.RequiredToolAlias);
        }
        else
        {
            notes.Add("tool_aliases_not_compared_without_alias_map");
        }
        Check("toolChoice.mode", l.ToolChoice.Mode, r.ToolChoice.Mode);
        Check("toolChoice.relation", l.ToolChoice.RequiredToolRelation, r.ToolChoice.RequiredToolRelation);
        Check("temperature", $"{l.Temperature.State}:{QreDiagnosticsAnalyzer.Number(l.Temperature)}", $"{r.Temperature.State}:{QreDiagnosticsAnalyzer.Number(r.Temperature)}");
        Check("maxOutputTokens", $"{l.MaxOutputTokens.State}:{QreDiagnosticsAnalyzer.Number(l.MaxOutputTokens)}", $"{r.MaxOutputTokens.State}:{QreDiagnosticsAnalyzer.Number(r.MaxOutputTokens)}");
        Check("responseFormat", $"{l.ResponseFormat.State}:{l.ResponseFormat.Value}", $"{r.ResponseFormat.State}:{r.ResponseFormat.Value}");
        Check("stream", $"{l.Stream.State}:{l.Stream.Value}", $"{r.Stream.State}:{r.Stream.Value}");
        Check("model.state", $"{l.Model.State}:{l.Model.Source}", $"{r.Model.State}:{r.Model.Source}");
        if (AliasesComparable([l.Model.Alias, l.Model.EffectiveAlias]))
        {
            Check("model.alias", MapAlias(l.Model.Alias ?? l.Model.EffectiveAlias), r.Model.Alias ?? r.Model.EffectiveAlias);
        }
        else
        {
            notes.Add("model_aliases_not_compared_without_alias_map");
        }
    }

    private static (List<(Call Left, Call Right, string Basis)>? Pairs, string Alignment, string Reason) Align(
        IReadOnlyList<Call> left,
        IReadOnlyList<Call> right,
        bool sameSegment,
        IReadOnlyDictionary<string, string> stepMap)
    {
        var pairs = new List<(Call, Call, string)>();
        if (sameSegment)
        {
            foreach (var call in left)
            {
                var match = right.FirstOrDefault(r => r.Id == call.Id);
                if (match == null)
                {
                    return (null, "correlation_ids", "call_missing_in_right");
                }
                pairs.Add((call, match, "correlation_id"));
            }
            return (pairs, "correlation_ids", "aligned");
        }
        if (stepMap.Count > 0)
        {
            foreach (var (leftStep, rightStep) in stepMap)
            {
                var l = left.Where(c => c.Step == leftStep).ToArray();
                var r = right.Where(c => c.Step == rightStep).ToArray();
                if (l.Length == 0 || l.Length != r.Length)
                {
                    return (null, "explicit_step_map", "step_map_unmatched");
                }
                for (var i = 0; i < l.Length; i++)
                {
                    pairs.Add((l[i], r[i], "explicit_step_map"));
                }
            }
            return (pairs, "explicit_step_map", "aligned");
        }
        if (left.Count != right.Count)
        {
            return (null, "order_and_features", "call_count_differs");
        }
        for (var i = 0; i < left.Count; i++)
        {
            if (left[i].Feature != right[i].Feature)
            {
                return (null, "order_and_features", "structural_features_differ");
            }
            pairs.Add((left[i], right[i], "order_and_features"));
        }
        return (pairs, "order_and_features", "aligned");
    }

    private static List<Call> Calls(QreDiagnosticsDocument document)
    {
        var stepOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        var calls = new List<Call>();
        foreach (var group in document.Records
                     .Where(static r => r.ModelCallId != null)
                     .GroupBy(static r => r.ModelCallId!, StringComparer.Ordinal)
                     .OrderBy(static g => g.Min(static r => r.Sequence)))
        {
            var records = group.OrderBy(static r => r.Sequence).ToArray();
            var step = records[0].StepAlias ?? "none";
            if (!stepOrder.ContainsKey(step))
            {
                stepOrder[step] = stepOrder.Count;
            }
            var runtime = Stage(records, QreDiagnosticsAnalyzer.RuntimeStage)?.Request;
            var feature = string.Join('|',
                stepOrder[step].ToString(CultureInfo.InvariantCulture),
                records[0].RuntimeModelAttemptOrdinal?.ToString(CultureInfo.InvariantCulture) ?? "na",
                runtime?.Messages.Items.Count(static m => m.Role == "user").ToString(CultureInfo.InvariantCulture) ?? "na",
                runtime?.Tools.Count.ToString(CultureInfo.InvariantCulture) ?? "na");
            calls.Add(new Call(group.Key, step, feature, records));
        }
        return calls;
    }

    private static QreOutboundDiagnosticRecord? Stage(IReadOnlyList<QreOutboundDiagnosticRecord> records, string stage)
        => stage switch
        {
            QreDiagnosticsAnalyzer.RuntimeStage => records.FirstOrDefault(static r => r.EventType == QreDiagnosticEventTypes.ModelCallStarted && r.Request != null),
            QreDiagnosticsAnalyzer.AdapterStage => records.FirstOrDefault(static r => r.EventType == QreDiagnosticEventTypes.AdapterPrepared && r.Request != null),
            _ => records.FirstOrDefault(static r => r.EventType == QreDiagnosticEventTypes.RequestStructureObserved && r.Request != null)
        };

    private static string Versions(QreDiagnosticsDocument document)
    {
        var first = document.Records.FirstOrDefault(static r => r.ModelCallId != null);
        return string.Join('|',
            first?.ProjectionPolicyVersion ?? document.Manifest?.ProjectionPolicyVersion,
            first?.NormalizerVersion ?? document.Manifest?.NormalizerVersion,
            first?.SdkVersion ?? document.Manifest?.Coverage.SdkVersion,
            first?.AdapterVersion,
            first?.ApiMode ?? document.Manifest?.Coverage.ApiMode);
    }

    private static QreDiagnoseCompareOutput NotComparable(string left, string right, string reason, IReadOnlyList<string>? notes = null)
        => new()
        {
            Left = left,
            Right = right,
            Result = "not_comparable",
            ReasonCode = reason,
            Alignment = "none",
            Notes = notes ?? []
        };

    private static QreDiagnosticFinding Difference(
        string callId,
        string stage,
        string field,
        string? left,
        string? right,
        string classification,
        params long[] evidence)
        => new()
        {
            ModelCallId = callId,
            SourceStage = stage,
            TargetStage = stage,
            FieldPath = field,
            Before = left,
            After = right,
            Classification = classification,
            Rule = "cross_run_field_equality",
            EvidenceSequences = evidence
        };

    private sealed record Call(string Id, string Step, string Feature, IReadOnlyList<QreOutboundDiagnosticRecord> Records);
}
