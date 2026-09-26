using System.Globalization;
using CodexFlow.QueryRuntime.Models.Diagnostics;

namespace CodexFlow.QueryRuntime.Cli.Diagnostics;

/// <summary>Finding classifications (plan §8.1).</summary>
internal static class QreFindingClass
{
    public const string ExpectedTransform = "expected_transform";
    public const string UnexpectedChange = "unexpected_change";
    public const string NotComparable = "not_comparable";
    public const string InsufficientEvidence = "insufficient_evidence";
}

/// <summary>
/// Versioned cross-layer normalizer and comparer. It compares the Runtime,
/// adapter and HTTP observation points of each model call using explicit rules:
/// a field is reported lost only when both sides are completely observed and a
/// rule supports the combination; redacted, unobserved and unsupported values
/// never count as absent, and unsupported combinations never yield "consistent".
/// </summary>
internal static class QreDiagnosticsAnalyzer
{
    public const string RuntimeStage = QreDiagnosticStages.RuntimePrepared;
    public const string AdapterStage = QreDiagnosticStages.AdapterPrepared;
    public const string HttpStage = QreDiagnosticStages.HttpPrepared;

    public static IReadOnlyList<QreModelCallAnalysis> Analyze(QreDiagnosticsDocument document)
    {
        var calls = new List<QreModelCallAnalysis>();
        foreach (var group in document.Records
                     .Where(static record => record.ModelCallId != null)
                     .GroupBy(static record => record.ModelCallId!, StringComparer.Ordinal)
                     .OrderBy(static group => group.Min(static record => record.Sequence)))
        {
            calls.Add(AnalyzeCall(group.Key, group.OrderBy(static record => record.Sequence).ToArray(), document.Integrity.VersionsSupported));
        }
        return calls;
    }

    public static string RunVerdict(QreDiagnosticsDocument document, IReadOnlyList<QreModelCallAnalysis> calls)
    {
        if (calls.Count == 0)
        {
            return document.Manifest?.EntryOutcome != null ? "model_call_not_started" : QreFindingClass.InsufficientEvidence;
        }
        if (calls.Any(static call => call.Verdict == QreFindingClass.UnexpectedChange))
        {
            return QreFindingClass.UnexpectedChange;
        }
        if (calls.Any(static call => call.Verdict != "consistent") || document.Integrity.EvidenceIncomplete)
        {
            return QreFindingClass.InsufficientEvidence;
        }
        return "consistent";
    }

    public static QreModelCallAnalysis AnalyzeCall(
        string modelCallId,
        IReadOnlyList<QreOutboundDiagnosticRecord> records,
        bool versionsSupported)
    {
        var started = records.FirstOrDefault(static r => r.EventType == QreDiagnosticEventTypes.ModelCallStarted);
        var adapter = records.FirstOrDefault(static r => r.EventType == QreDiagnosticEventTypes.AdapterPrepared);
        var structures = records.Where(static r => r.EventType == QreDiagnosticEventTypes.RequestStructureObserved).ToArray();
        var ended = records.FirstOrDefault(static r => r.EventType == QreDiagnosticEventTypes.ModelCallEnded);
        var headers = records.Where(static r => r.EventType == QreDiagnosticEventTypes.HttpHeadersReceived).ToArray();
        var httpEnds = records.Where(static r => r.EventType == QreDiagnosticEventTypes.HttpAttemptEnded).ToArray();
        var first = started ?? records[0];

        var findings = new List<QreDiagnosticFinding>();
        var notes = new List<string>();
        var observed = new List<string>();
        if (IsComplete(started))
        {
            observed.Add(RuntimeStage);
        }
        if (IsComplete(adapter))
        {
            observed.Add(AdapterStage);
        }
        var httpStructure = structures.FirstOrDefault(IsComplete);
        if (httpStructure != null)
        {
            observed.Add(HttpStage);
        }

        if (!versionsSupported)
        {
            findings.Add(Insufficient(modelCallId, RuntimeStage, HttpStage, "versions", "normalizer_or_policy_version_unsupported", started));
        }
        else
        {
            if (IsComplete(started) && IsComplete(adapter))
            {
                CompareRuntimeToAdapter(modelCallId, started!, adapter!, findings, notes);
            }
            else
            {
                findings.Add(Insufficient(modelCallId, RuntimeStage, AdapterStage, "*", "stage_not_completely_observed", started ?? adapter));
            }

            if (IsComplete(adapter) && httpStructure != null)
            {
                CompareAdapterToHttp(modelCallId, adapter!, httpStructure, findings, notes);
            }
            else
            {
                var reason = structures.Length == 0
                    ? "http_structure_not_observed"
                    : $"http_structure_{structures[0].CaptureStatus}_{structures[0].ReasonCode ?? "unknown"}";
                findings.Add(Insufficient(modelCallId, AdapterStage, HttpStage, "*", reason, adapter ?? structures.FirstOrDefault()));
            }
        }

        var firstUnexpected = findings.FirstOrDefault(static f => f.Classification == QreFindingClass.UnexpectedChange);
        var verdict = firstUnexpected != null
            ? QreFindingClass.UnexpectedChange
            : findings.Any(static f => f.Classification is QreFindingClass.InsufficientEvidence or QreFindingClass.NotComparable)
                ? QreFindingClass.InsufficientEvidence
                : "consistent";
        return new QreModelCallAnalysis
        {
            ModelCallId = modelCallId,
            StepAlias = first.StepAlias,
            RuntimeModelAttemptOrdinal = first.RuntimeModelAttemptOrdinal,
            RuntimeModelAttemptOrdinalStatus = first.RuntimeModelAttemptOrdinalStatus,
            Outcome = ended?.ModelOutcome?.Outcome ?? "not_ended",
            Classification = ended?.ModelOutcome?.Classification,
            FailurePhase = ended?.ModelOutcome?.FailurePhase,
            HttpAttempts = records.Count(static r => r.EventType == QreDiagnosticEventTypes.HttpAttemptStarted),
            HttpStatuses = headers.Select(static h => h.HttpResponse!.StatusCode.ToString(CultureInfo.InvariantCulture)).ToArray(),
            StreamTerminations = httpEnds.Select(static h => h.HttpOutcome!.StreamTermination).ToArray(),
            ObservedStages = observed,
            FirstUnexpectedBoundary = firstUnexpected == null ? null : $"{firstUnexpected.SourceStage}->{firstUnexpected.TargetStage}",
            Verdict = verdict,
            Findings = findings,
            Notes = notes
        };
    }

    private static void CompareRuntimeToAdapter(
        string callId,
        QreOutboundDiagnosticRecord runtimeRecord,
        QreOutboundDiagnosticRecord adapterRecord,
        List<QreDiagnosticFinding> findings,
        List<string> notes)
    {
        var runtime = runtimeRecord.Request!;
        var adapter = adapterRecord.Request!;
        long[] evidence = [runtimeRecord.Sequence, adapterRecord.Sequence];
        QreDiagnosticFinding Finding(string field, string? before, string? after, string classification, string rule, params string[] tags)
            => new()
            {
                ModelCallId = callId,
                SourceStage = RuntimeStage,
                TargetStage = AdapterStage,
                FieldPath = field,
                Before = before,
                After = after,
                Classification = classification,
                Rule = rule,
                Tags = tags,
                EvidenceSequences = evidence
            };

        if (!Sequence(runtime.Messages).SequenceEqual(Sequence(adapter.Messages)))
        {
            findings.Add(Finding("messages", Describe(runtime.Messages), Describe(adapter.Messages), QreFindingClass.UnexpectedChange, "message_structure_preserved"));
        }
        if (!runtime.Tools.Aliases.SequenceEqual(adapter.Tools.Aliases, StringComparer.Ordinal))
        {
            findings.Add(Finding("tools", Join(runtime.Tools.Aliases), Join(adapter.Tools.Aliases), QreFindingClass.UnexpectedChange, "tool_declarations_preserved"));
        }

        // Tool choice (§8.1 rule table).
        var required = runtime.ToolChoice.State == QreDiagnosticFieldStates.Present ? runtime.ToolChoice : null;
        var mode = adapter.ToolChoice;
        if (required == null)
        {
            if (runtime.Tools.Count == 0 && mode.Mode == "none")
            {
                findings.Add(Finding("toolChoice", "unspecified", "none", QreFindingClass.ExpectedTransform, "no_tools_disables_tool_mode"));
            }
            else if (mode.State == QreDiagnosticFieldStates.Present)
            {
                findings.Add(Finding("toolChoice", "unspecified", ToolChoice(mode), QreFindingClass.ExpectedTransform, "unspecified_tool_mode_from_host"));
            }
        }
        else if (runtime.Tools.Count == 0 && mode.Mode == "none")
        {
            findings.Add(Finding("toolChoice", ToolChoice(required), "none", QreFindingClass.UnexpectedChange, "required_tool_dropped_without_tools", "input_constraint_conflict"));
        }
        else if (mode.Mode != "require_specific" ||
                 !string.Equals(mode.RequiredToolAlias, required.RequiredToolAlias, StringComparison.Ordinal))
        {
            findings.Add(Finding("toolChoice", ToolChoice(required), ToolChoice(mode), QreFindingClass.UnexpectedChange, "required_tool_preserved"));
        }
        else if (required.RequiredToolRelation == "case_only_mismatch" || mode.RequiredToolRelation == "case_only_mismatch")
        {
            // The Runtime gate matches OrdinalIgnoreCase, while the adapter hands the
            // original selection and the canonical declaration names to the SDK as-is.
            findings.Add(Finding("toolChoice.requiredToolName", ToolChoice(required), ToolChoice(mode), QreFindingClass.UnexpectedChange, "required_tool_matching_semantics", "case_only_mismatch"));
        }
        else if (mode.RequiredToolRelation == "not_declared")
        {
            findings.Add(Finding("toolChoice.requiredToolName", ToolChoice(required), ToolChoice(mode), QreFindingClass.UnexpectedChange, "required_tool_declared", "required_tool_not_declared"));
        }

        CompareNumber(runtime.Temperature, adapter.Temperature, "temperature", floatTolerance: true, Finding, findings);
        CompareNumber(runtime.MaxOutputTokens, adapter.MaxOutputTokens, "maxOutputTokens", floatTolerance: false, Finding, findings);

        // RequireJsonObject=false is equivalent to "unspecified"; only true -> no format is a loss.
        if (runtime.ResponseFormat.Value == "json_object")
        {
            if (adapter.ResponseFormat.Value is not ("json_object" or "json_schema"))
            {
                findings.Add(Finding("responseFormat", "json_object", adapter.ResponseFormat.Value ?? "none", QreFindingClass.UnexpectedChange, "explicit_constraint_mapped"));
            }
        }
        else if (adapter.ResponseFormat.State == QreDiagnosticFieldStates.Present)
        {
            findings.Add(Finding("responseFormat", "none", adapter.ResponseFormat.Value, QreFindingClass.ExpectedTransform, "unspecified_value_from_host"));
        }

        // Model identity: a null ModelId alone is not a loss.
        var runtimeModel = runtime.Model.State == QreDiagnosticFieldStates.Present ? runtime.Model.Alias : null;
        if (runtimeModel != null)
        {
            if (adapter.Model.State == QreDiagnosticFieldStates.Present)
            {
                if (!string.Equals(adapter.Model.Alias, runtimeModel, StringComparison.Ordinal))
                {
                    findings.Add(Finding("model", runtimeModel, adapter.Model.Alias, QreFindingClass.UnexpectedChange, "model_identity_preserved"));
                }
            }
            else if (adapter.Model.Source == "descriptor_default" && adapter.Model.EffectiveAlias != null)
            {
                findings.Add(string.Equals(adapter.Model.EffectiveAlias, runtimeModel, StringComparison.Ordinal)
                    ? Finding("model", runtimeModel, $"descriptor_default:{adapter.Model.EffectiveAlias}", QreFindingClass.ExpectedTransform, "descriptor_default_model_equivalent")
                    : Finding("model", runtimeModel, $"descriptor_default:{adapter.Model.EffectiveAlias}", QreFindingClass.UnexpectedChange, "descriptor_default_model_differs"));
            }
            else
            {
                findings.Add(Finding("model", runtimeModel, "unknown_default", QreFindingClass.NotComparable, "model_default_source_unknown"));
            }
        }
        if (adapter.UncheckedFieldCount > 0)
        {
            notes.Add("adapter_has_unchecked_option_fields");
        }
    }

    private static void CompareAdapterToHttp(
        string callId,
        QreOutboundDiagnosticRecord adapterRecord,
        QreOutboundDiagnosticRecord httpRecord,
        List<QreDiagnosticFinding> findings,
        List<string> notes)
    {
        var adapter = adapterRecord.Request!;
        var http = httpRecord.Request!;
        long[] evidence = [adapterRecord.Sequence, httpRecord.Sequence];
        var cell = QreTransportCapabilityMatrix.Get(adapterRecord.ProviderCategory ?? "custom", adapterRecord.ApiMode ?? "unknown");
        var verified = cell.Status == QreCapabilityStatus.Verified &&
                       string.Equals(adapterRecord.SdkVersion, QreTransportCapabilityMatrix.VerifiedSdkVersion, StringComparison.Ordinal);
        QreDiagnosticFinding Finding(string field, string? before, string? after, string classification, string rule, params string[] tags)
            => new()
            {
                ModelCallId = callId,
                SourceStage = AdapterStage,
                TargetStage = HttpStage,
                FieldPath = field,
                Before = before,
                After = after,
                Classification = classification,
                Rule = rule,
                Tags = tags,
                EvidenceSequences = evidence
            };
        bool Registered(string transform) => verified && cell.RegisteredTransforms.Contains(transform);
        string[] Limitation(string limitation) => verified && cell.KnownLimitations.Contains(limitation) ? ["known_sdk_limitation"] : [];

        if (!verified)
        {
            findings.Add(Finding("*", adapterRecord.ProviderCategory, adapterRecord.ApiMode, QreFindingClass.NotComparable, "capability_cell_not_verified"));
        }

        var adapterMessages = Sequence(adapter.Messages).ToList();
        var httpMessages = Sequence(http.Messages).ToList();
        if (Registered(QreTransportCapabilityMatrix.SdkInjectedSystemPrompt) &&
            httpMessages.Count == adapterMessages.Count + 1 && httpMessages[0] == "system:text" &&
            (adapterMessages.Count == 0 || adapterMessages[0] != "system:text"))
        {
            httpMessages.RemoveAt(0);
            findings.Add(Finding("messages", null, "system:text", QreFindingClass.ExpectedTransform, QreTransportCapabilityMatrix.SdkInjectedSystemPrompt));
        }
        if (!adapterMessages.SequenceEqual(httpMessages))
        {
            findings.Add(Finding("messages", string.Join(',', adapterMessages), string.Join(',', httpMessages), verified ? QreFindingClass.UnexpectedChange : QreFindingClass.NotComparable, "message_structure_preserved"));
        }
        if (adapter.Messages.LinkedToolResults != http.Messages.LinkedToolResults || http.Messages.UnlinkedToolResults > adapter.Messages.UnlinkedToolResults)
        {
            findings.Add(Finding("messages.toolResultLinks", $"{adapter.Messages.LinkedToolResults}", $"{http.Messages.LinkedToolResults}", QreFindingClass.UnexpectedChange, "tool_result_call_ids_preserved"));
        }
        if (http.Notes.Contains("system_hoisted_to_top_level") && Registered(QreTransportCapabilityMatrix.SystemHoistedToTopLevel))
        {
            notes.Add("system_hoisted_to_top_level");
        }

        if (adapter.ToolChoice.Mode == "none" && http.Tools.Count == 0 && adapter.Tools.Count > 0)
        {
            findings.Add(Finding("tools", Join(adapter.Tools.Aliases), "omitted", QreFindingClass.ExpectedTransform, "tools_omitted_with_tool_mode_none"));
        }
        else if (!adapter.Tools.Aliases.SequenceEqual(http.Tools.Aliases, StringComparer.Ordinal))
        {
            findings.Add(Finding("tools", Join(adapter.Tools.Aliases), Join(http.Tools.Aliases), QreFindingClass.UnexpectedChange, "tool_declarations_preserved"));
        }

        var before = ToolChoice(adapter.ToolChoice);
        var after = ToolChoice(http.ToolChoice);
        if (adapter.ToolChoice.State == QreDiagnosticFieldStates.Present && http.ToolChoice.State == QreDiagnosticFieldStates.Absent)
        {
            findings.Add(Finding("toolChoice", before, "absent", QreFindingClass.UnexpectedChange, "tool_choice_transmitted", Limitation(QreTransportCapabilityMatrix.ToolChoiceDropped)));
        }
        else if (adapter.ToolChoice.State == QreDiagnosticFieldStates.Absent && http.ToolChoice.State == QreDiagnosticFieldStates.Present)
        {
            findings.Add(Finding("toolChoice", "unspecified", after, QreFindingClass.ExpectedTransform, "protocol_default_tool_choice"));
        }
        else if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            findings.Add(Finding("toolChoice", before, after, QreFindingClass.UnexpectedChange, "tool_choice_transmitted"));
        }
        else if (http.ToolChoice.RequiredToolRelation == "case_only_mismatch")
        {
            // Captured fact: the SDK kept the case-only difference on the wire.
            notes.Add("http_preserves_case_only_mismatch");
        }

        if (adapter.Temperature.State == QreDiagnosticFieldStates.Present && http.Temperature.State == QreDiagnosticFieldStates.Present &&
            http.Temperature.SourcePath == "options.temperature")
        {
            findings.Add(Finding("temperature.path", "temperature", "options.temperature",
                Registered(QreTransportCapabilityMatrix.TemperatureNestedOptions) ? QreFindingClass.ExpectedTransform : QreFindingClass.NotComparable,
                QreTransportCapabilityMatrix.TemperatureNestedOptions));
        }
        CompareNumber(adapter.Temperature, http.Temperature, "temperature", floatTolerance: true, Finding, findings, lossTags: []);

        if (adapter.MaxOutputTokens.State == QreDiagnosticFieldStates.Absent && http.MaxOutputTokens.State == QreDiagnosticFieldStates.Present)
        {
            findings.Add(Registered(QreTransportCapabilityMatrix.MaxTokensDefaultWhenUnspecified)
                ? Finding("maxOutputTokens", "unspecified", Number(http.MaxOutputTokens), QreFindingClass.ExpectedTransform, QreTransportCapabilityMatrix.MaxTokensDefaultWhenUnspecified)
                : Finding("maxOutputTokens", "unspecified", Number(http.MaxOutputTokens), QreFindingClass.UnexpectedChange, "unregistered_default_injected"));
        }
        else
        {
            CompareNumber(adapter.MaxOutputTokens, http.MaxOutputTokens, "maxOutputTokens", floatTolerance: false, Finding, findings, Limitation(QreTransportCapabilityMatrix.MaxTokensDropped));
        }

        if (adapter.ResponseFormat.Value is "json_object" or "json_schema")
        {
            if (http.ResponseFormat.Value is "json_object" or "json_schema")
            {
                if (http.ResponseFormat.SourcePath != "response_format" && http.ResponseFormat.SourcePath != "text.format")
                {
                    notes.Add("json_format_non_standard_path");
                }
            }
            else if (http.ResponseFormat.Value == "alternative_json_only")
            {
                findings.Add(Finding("responseFormat", adapter.ResponseFormat.Value, $"alternative:{http.ResponseFormat.SourcePath}", QreFindingClass.NotComparable, "alternative_json_field_equivalence_unverified", Limitation(QreTransportCapabilityMatrix.ResponseFormatDropped)));
            }
            else if (http.ResponseFormat.SourcePath == "unsupported_by_protocol")
            {
                findings.Add(Finding("responseFormat", adapter.ResponseFormat.Value, "none", QreFindingClass.UnexpectedChange, "explicit_constraint_transmitted", "unsupported_constraint"));
            }
            else
            {
                findings.Add(Finding("responseFormat", adapter.ResponseFormat.Value, http.ResponseFormat.Value ?? "none", QreFindingClass.UnexpectedChange, "explicit_constraint_transmitted", Limitation(QreTransportCapabilityMatrix.ResponseFormatDropped)));
            }
        }

        if (adapter.Stream.Value == "true" && http.Stream.Value != "true")
        {
            findings.Add(Finding("stream", "true", http.Stream.Value ?? "absent", QreFindingClass.UnexpectedChange, "streaming_transmitted"));
        }

        var intended = adapter.Model.State == QreDiagnosticFieldStates.Present ? adapter.Model.Alias : adapter.Model.EffectiveAlias;
        if (http.Model.State != QreDiagnosticFieldStates.Present)
        {
            findings.Add(Finding("model", intended, "absent", intended == null ? QreFindingClass.NotComparable : QreFindingClass.UnexpectedChange, "model_transmitted"));
        }
        else if (intended == null)
        {
            findings.Add(Finding("model", "unknown", http.Model.Alias, QreFindingClass.NotComparable, "model_default_source_unknown"));
        }
        else if (!string.Equals(intended, http.Model.Alias, StringComparison.Ordinal))
        {
            findings.Add(Finding("model", intended, http.Model.Alias, QreFindingClass.UnexpectedChange, "model_identity_preserved"));
        }

        if (http.UncheckedFieldCount > 0)
        {
            notes.Add($"http_unchecked_top_level_fields:{http.UncheckedFieldCount}");
        }
    }

    private static void CompareNumber(
        QreSemanticNumber source,
        QreSemanticNumber target,
        string field,
        bool floatTolerance,
        Func<string, string?, string?, string, string, string[], QreDiagnosticFinding> finding,
        List<QreDiagnosticFinding> findings,
        string[]? lossTags = null)
    {
        if (source.State != QreDiagnosticFieldStates.Present)
        {
            if (target.State == QreDiagnosticFieldStates.Present)
            {
                findings.Add(finding(field, "unspecified", Number(target), QreFindingClass.ExpectedTransform, "unspecified_value_supplied_downstream", []));
            }
            return;
        }
        if (target.State != QreDiagnosticFieldStates.Present)
        {
            findings.Add(finding(field, Number(source), target.State, QreFindingClass.UnexpectedChange, "explicit_constraint_mapped", lossTags ?? []));
            return;
        }
        var left = source.Value!.Value;
        var right = target.Value!.Value;
        if (left.Equals(right))
        {
            return;
        }
        if (floatTolerance && (float)left == (float)right)
        {
            findings.Add(finding(field, Number(source), Number(target), QreFindingClass.ExpectedTransform, "double_to_float_precision", []));
            return;
        }
        findings.Add(finding(field, Number(source), Number(target), QreFindingClass.UnexpectedChange, "explicit_constraint_value_preserved", []));
    }

    private static bool IsComplete(QreOutboundDiagnosticRecord? record)
        => record?.Request != null && record.CaptureStatus == QreDiagnosticCaptureStatus.Complete;

    private static QreDiagnosticFinding Insufficient(string callId, string source, string target, string field, string reason, QreOutboundDiagnosticRecord? record)
        => new()
        {
            ModelCallId = callId,
            SourceStage = source,
            TargetStage = target,
            FieldPath = field,
            Classification = QreFindingClass.InsufficientEvidence,
            Rule = reason,
            EvidenceSequences = record == null ? [] : [record.Sequence]
        };

    /// <summary>
    /// Protocol-neutral message form: flattened <c>role:kind</c> pairs, reasoning
    /// excluded (not every protocol can carry it) and consecutive text parts collapsed.
    /// </summary>
    internal static IEnumerable<string> Sequence(QreSemanticMessages messages)
    {
        string? previous = null;
        foreach (var message in messages.Items)
        {
            foreach (var kind in message.ItemKinds)
            {
                if (kind == "reasoning")
                {
                    continue;
                }
                var entry = $"{message.Role}:{kind}";
                if (kind == "text" && entry == previous)
                {
                    continue;
                }
                previous = entry;
                yield return entry;
            }
        }
    }

    internal static string ToolChoice(QreSemanticToolChoice choice)
        => choice.Mode == "require_specific"
            ? $"require_specific({choice.RequiredToolAlias},{choice.RequiredToolRelation})"
            : choice.Mode;

    private static string Describe(QreSemanticMessages messages) => string.Join(',', Sequence(messages));

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "none" : string.Join(',', values);

    internal static string Number(QreSemanticNumber number)
        => number.Value?.ToString("R", CultureInfo.InvariantCulture) ?? number.State;
}
