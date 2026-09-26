using System.Text.Json;
using CodexFlow.QueryRuntime.Cli.Diagnostics;

/// <summary>
/// <c>qre diagnose</c>: offline, read-only inspection, comparison, export and
/// request rebuild for outbound SDK diagnostics. Nothing here contacts a network
/// or resends a request. Exit codes for compare/rebuild: 0 verified, 1 unexpected
/// difference, 2 invalid input, insufficient evidence or unsupported capability.
/// </summary>
internal static class QreDiagnosticsCommands
{
    private const int Ok = 0;
    private const int Different = 1;
    private const int Invalid = 2;

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintHelp();
            return Ok;
        }
        var parsed = Parse(args[1..]);
        if (parsed.Error != null)
        {
            return Error(parsed, "invalid_arguments", parsed.Error);
        }
        try
        {
            return args[0] switch
            {
                "latest" => Inspect(parsed with { Positional = ["latest"] }),
                "inspect" => Inspect(parsed),
                "compare" => Compare(parsed),
                "export" => Export(parsed),
                "skeleton" => Skeleton(parsed),
                "rebuild" => await RebuildAsync(parsed, ct).ConfigureAwait(false),
                _ => Error(parsed, "unknown_subcommand", $"Unknown qre diagnose command: {args[0]}")
            };
        }
        catch (QreDiagnosticsInputException ex)
        {
            return Error(parsed, ex.ReasonCode, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Error(parsed, "input_unreadable", "Diagnostic input could not be read safely.");
        }
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  qre run --sdk-diagnostics metadata|structure ...");
        Console.WriteLine("  qre diagnose latest [--workspace .] [--json]");
        Console.WriteLine("  qre diagnose inspect <run|bundle.zip> [--workspace .] [--json]");
        Console.WriteLine("  qre diagnose compare <left> <right> [--step-map L=R] [--alias-map L=R] [--workspace .] [--json]");
        Console.WriteLine("  qre diagnose export <run|latest> --output <bundle.zip> [--force] [--workspace .] [--json]");
        Console.WriteLine("  qre diagnose skeleton <run|latest> --output <fixture.json> [--call <id>] [--force] [--json]");
        Console.WriteLine("  qre diagnose rebuild <fixture.json> [--json]");
        Console.WriteLine();
        Console.WriteLine("Observation points: runtime_prepared (QRE request), adapter_prepared (final MEAI options),");
        Console.WriteLine("and http_prepared (client-side, handler-visible SDK request). Records are client observations,");
        Console.WriteLine("not proof of network bytes or server receipt; retries, redirects and auth below the handler");
        Console.WriteLine("are not covered. Structure mode keeps an allow-listed projection only: no prompts, arguments,");
        Console.WriteLine("response bodies, headers, URLs or plaintext tool/model names. Unobserved, redacted, omitted");
        Console.WriteLine("or unsupported evidence is reported as such and never counted as a pass; a missing record");
        Console.WriteLine("does not mean no request was sent. diagnose never contacts a network or resends requests.");
        Console.WriteLine("compare/rebuild exit codes: 0 verified, 1 unexpected difference, 2 invalid or insufficient.");
    }

    private static int Inspect(Arguments parsed)
    {
        if (parsed.Positional.Count != 1)
        {
            return Error(parsed, "invalid_arguments", "qre diagnose inspect requires one run or bundle.");
        }
        var path = QreDiagnosticsReader.Resolve(parsed.Positional[0], parsed.Workspace);
        var document = QreDiagnosticsReader.Load(path);
        var calls = QreDiagnosticsAnalyzer.Analyze(document);
        var coverage = document.Manifest?.Coverage ?? new QreDiagnosticsCoverage { TransportCapture = "unknown", AttemptCoverage = "unknown" };
        var output = new QreDiagnoseInspectOutput
        {
            Run = QreDiagnosticsReader.Describe(path),
            Origin = document.Manifest?.Origin ?? "unknown",
            Mode = document.Manifest?.Mode ?? "unknown",
            ObservationScope = document.Manifest?.ObservationScope ?? "client_side_handler",
            Coverage = coverage,
            Integrity = document.Integrity,
            EntryOutcome = document.Manifest?.EntryOutcome,
            RecordCount = document.Records.Count,
            ModelCalls = calls,
            Verdict = QreDiagnosticsAnalyzer.RunVerdict(document, calls),
            Limitations = Limitations(coverage, document)
        };
        if (parsed.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(output, QreDiagnosticsJsonContext.Default.QreDiagnoseInspectOutput));
            return Ok;
        }
        Console.WriteLine($"diagnostic_run: {output.Run} ({output.Origin}, {output.Mode})");
        Console.WriteLine($"observation: {output.ObservationScope}; transport={coverage.TransportCapture}; attempts={coverage.AttemptCoverage}; capability={coverage.CapabilityStatus ?? "unknown"}");
        Console.WriteLine($"integrity: {document.Integrity.CompletionStatus}; truncated_tail={document.Integrity.TruncatedTail}; sequence_gaps={document.Integrity.SequenceGaps}; invalid_lines={document.Integrity.InvalidLines}");
        if (output.EntryOutcome != null)
        {
            Console.WriteLine($"entry: {output.EntryOutcome} (no model call started)");
        }
        Console.WriteLine($"verdict: {output.Verdict}");
        foreach (var call in calls)
        {
            Console.WriteLine($"- {call.ModelCallId} step={call.StepAlias} attempt={call.RuntimeModelAttemptOrdinal?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"} outcome={call.Outcome}/{call.Classification} http={call.HttpAttempts} [{string.Join(',', call.HttpStatuses)}] verdict={call.Verdict}");
            Console.WriteLine($"  stages: {string.Join(" -> ", call.ObservedStages)}");
            if (call.FirstUnexpectedBoundary != null)
            {
                Console.WriteLine($"  first unexpected change: {call.FirstUnexpectedBoundary}");
            }
            foreach (var finding in call.Findings.Where(static f => f.Classification != QreFindingClass.ExpectedTransform))
            {
                Console.WriteLine($"  {finding.Classification}: {finding.SourceStage}->{finding.TargetStage} {finding.FieldPath} {finding.Before} => {finding.After} [{finding.Rule}{(finding.Tags.Count > 0 ? "; " + string.Join(',', finding.Tags) : string.Empty)}]");
            }
        }
        foreach (var limitation in output.Limitations)
        {
            Console.WriteLine($"limitation: {limitation}");
        }
        return Ok;
    }

    private static int Compare(Arguments parsed)
    {
        if (parsed.Positional.Count != 2)
        {
            return Error(parsed, "invalid_arguments", "qre diagnose compare requires <left> <right>.");
        }
        var left = QreDiagnosticsReader.Load(QreDiagnosticsReader.Resolve(parsed.Positional[0], parsed.Workspace));
        var right = QreDiagnosticsReader.Load(QreDiagnosticsReader.Resolve(parsed.Positional[1], parsed.Workspace));
        var output = QreDiagnosticsComparer.Compare(left, right, parsed.StepMap, parsed.AliasMap);
        if (parsed.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(output, QreDiagnosticsJsonContext.Default.QreDiagnoseCompareOutput));
        }
        else
        {
            Console.WriteLine($"compare: {output.Left} vs {output.Right}: {output.Result} ({output.ReasonCode}); alignment={output.Alignment}");
            if (output.EarliestDifference is { } earliest)
            {
                Console.WriteLine($"earliest observable difference (not a proven root cause): {earliest.SourceStage} {earliest.FieldPath}: {earliest.Before} => {earliest.After}");
            }
            foreach (var note in output.Notes)
            {
                Console.WriteLine($"note: {note}");
            }
        }
        return output.Result switch
        {
            "identical" => Ok,
            "different" => Different,
            _ => Invalid
        };
    }

    private static int Export(Arguments parsed)
    {
        if (parsed.Positional.Count != 1 || parsed.Output == null)
        {
            return Error(parsed, "invalid_arguments", "qre diagnose export requires <run> --output <bundle.zip>.");
        }
        if (!parsed.Output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Error(parsed, "invalid_arguments", "Export output must be a .zip file.");
        }
        var path = QreDiagnosticsReader.Resolve(parsed.Positional[0], parsed.Workspace);
        var document = QreDiagnosticsReader.Load(path);
        var output = Path.GetFullPath(parsed.Output);
        var (records, dropped) = QreDiagnosticsExporter.Export(document, output, parsed.Force);
        var result = new QreDiagnoseExportOutput
        {
            Run = QreDiagnosticsReader.Describe(path),
            Output = output,
            Records = records,
            DroppedFields = dropped,
            ExportPolicyVersion = QreDiagnosticsExporter.ExportPolicyVersion
        };
        if (parsed.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, QreDiagnosticsJsonContext.Default.QreDiagnoseExportOutput));
        }
        else
        {
            Console.WriteLine($"exported: {result.Records} re-projected records to {result.Output} (dropped fields: {result.DroppedFields}; local index, checkpoints and traces excluded)");
        }
        return Ok;
    }

    private static int Skeleton(Arguments parsed)
    {
        if (parsed.Positional.Count != 1 || parsed.Output == null)
        {
            return Error(parsed, "invalid_arguments", "qre diagnose skeleton requires <run> --output <fixture.json>.");
        }
        var output = Path.GetFullPath(parsed.Output);
        if (File.Exists(output) && !parsed.Force)
        {
            return Error(parsed, "output_exists", "Skeleton output already exists; pass --force to replace it.");
        }
        var path = QreDiagnosticsReader.Resolve(parsed.Positional[0], parsed.Workspace);
        var document = QreDiagnosticsReader.Load(path);
        var fixture = QreDiagnosticsRebuilder.CreateSkeleton(document, parsed.Call);
        File.WriteAllBytes(output, JsonSerializer.SerializeToUtf8Bytes(fixture, QreDiagnosticsJsonContext.Default.QreRebuildFixture));
        var result = new QreDiagnoseSkeletonOutput
        {
            Run = QreDiagnosticsReader.Describe(path),
            Output = output,
            ModelCallId = parsed.Call ?? "first",
            Missing = fixture.Missing
        };
        if (parsed.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, QreDiagnosticsJsonContext.Default.QreDiagnoseSkeletonOutput));
        }
        else
        {
            Console.WriteLine($"skeleton: {output} (status=skeleton; not runnable until completed and reviewed)");
            foreach (var missing in fixture.Missing)
            {
                Console.WriteLine($"missing: {missing}");
            }
        }
        return Ok;
    }

    private static async Task<int> RebuildAsync(Arguments parsed, CancellationToken ct)
    {
        if (parsed.Positional.Count != 1)
        {
            return Error(parsed, "invalid_arguments", "qre diagnose rebuild requires <fixture.json>.");
        }
        var path = Path.GetFullPath(parsed.Positional[0]);
        var fixture = QreDiagnosticsRebuilder.LoadFixture(path);
        var output = await QreDiagnosticsRebuilder.RebuildAsync(fixture, Path.GetFileName(path), optionsFactory: null, ct).ConfigureAwait(false);
        if (parsed.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(output, QreDiagnosticsJsonContext.Default.QreDiagnoseRebuildOutput));
        }
        else
        {
            Console.WriteLine($"rebuild: {output.Fixture}: {output.Result} ({output.ReasonCode}); network={output.Network}; http_requests={output.HttpRequests}; sdk={output.SdkVersion}{(output.SdkVersionChanged ? " (changed since fixture)" : string.Empty)}");
            foreach (var assertion in output.Assertions)
            {
                Console.WriteLine($"  {(assertion.Passed ? "pass" : "FAIL")} {assertion.Kind} {assertion.Target}: expected {assertion.Expected}, actual {assertion.Actual}");
            }
            foreach (var missing in output.Missing)
            {
                Console.WriteLine($"  missing: {missing}");
            }
        }
        return output.Result switch
        {
            "passed" => Ok,
            "failed" => Different,
            _ => Invalid
        };
    }

    private static IReadOnlyList<string> Limitations(QreDiagnosticsCoverage coverage, QreDiagnosticsDocument document)
    {
        var limitations = new List<string>
        {
            "client_side_observation_only: records describe what passed the SDK diagnostic handler, not network bytes or server receipt",
            "handler_visible_attempts_only: redirects, authentication retries and retransmits below the handler are not observed",
            "response_bodies_not_captured",
            "missing_records_do_not_prove_no_request_was_sent"
        };
        if (coverage.TransportCapture != "handler")
        {
            limitations.Add($"transport_capture: {coverage.TransportCapture}");
        }
        if (!coverage.RequestStructure)
        {
            limitations.Add("request_structure_not_captured: http_prepared comparisons are insufficient_evidence");
        }
        if (coverage.CapabilityStatus is not null and not "verified")
        {
            limitations.Add($"capability_cell_{coverage.CapabilityStatus}: SDK rules for this Provider x API mode are not verified");
        }
        if (document.Integrity.EvidenceIncomplete)
        {
            limitations.Add("evidence_incomplete");
        }
        return limitations;
    }

    private static int Error(Arguments parsed, string reasonCode, string message)
    {
        if (parsed.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new QreDiagnoseErrorOutput { ReasonCode = reasonCode, Message = message },
                QreDiagnosticsJsonContext.Default.QreDiagnoseErrorOutput));
        }
        else
        {
            Console.Error.WriteLine($"qre diagnose: {message} ({reasonCode})");
        }
        return Invalid;
    }

    private static Arguments Parse(string[] args)
    {
        var parsed = new Arguments { Workspace = Directory.GetCurrentDirectory() };
        var positional = new List<string>();
        var stepMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliasMap = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace":
                case "-w":
                    if (++i >= args.Length)
                    {
                        return parsed with { Error = "--workspace requires a path." };
                    }
                    parsed = parsed with { Workspace = args[i] };
                    break;
                case "--json":
                    parsed = parsed with { Json = true };
                    break;
                case "--force":
                    parsed = parsed with { Force = true };
                    break;
                case "--output":
                case "-o":
                    if (++i >= args.Length)
                    {
                        return parsed with { Error = "--output requires a path." };
                    }
                    parsed = parsed with { Output = args[i] };
                    break;
                case "--call":
                    if (++i >= args.Length)
                    {
                        return parsed with { Error = "--call requires a model call id." };
                    }
                    parsed = parsed with { Call = args[i] };
                    break;
                case "--step-map":
                case "--alias-map":
                    var option = args[i];
                    if (++i >= args.Length || args[i].Split('=') is not [{ Length: > 0 } key, { Length: > 0 } value])
                    {
                        return parsed with { Error = $"{option} requires LEFT=RIGHT." };
                    }
                    (option == "--step-map" ? stepMap : aliasMap)[key] = value;
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        return parsed with { Error = $"Unknown qre diagnose option: {args[i]}" };
                    }
                    positional.Add(args[i]);
                    break;
            }
        }
        return parsed with { Positional = positional, StepMap = stepMap, AliasMap = aliasMap };
    }

    private sealed record Arguments
    {
        public required string Workspace { get; init; }

        public bool Json { get; init; }

        public bool Force { get; init; }

        public string? Output { get; init; }

        public string? Call { get; init; }

        public string? Error { get; init; }

        public IReadOnlyList<string> Positional { get; init; } = [];

        public IReadOnlyDictionary<string, string> StepMap { get; init; } = new Dictionary<string, string>();

        public IReadOnlyDictionary<string, string> AliasMap { get; init; } = new Dictionary<string, string>();
    }
}
