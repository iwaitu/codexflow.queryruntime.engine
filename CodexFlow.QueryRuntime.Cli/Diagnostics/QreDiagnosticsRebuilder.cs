using System.Globalization;
using System.Text.Json;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using CodexFlow.QueryRuntime.Protocol;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Cli.Diagnostics;

/// <summary>
/// Offline SDK request rebuild. The real adapter and locked SDK serialize the
/// fixture request into <see cref="QreOfflineModelTransport"/>, which has no
/// downstream network handler; nothing is resent to a real service. Fixture
/// assertions verify behavior (required tool, JSON mapping, links, fields)
/// rather than whole-string snapshots.
/// </summary>
internal static class QreDiagnosticsRebuilder
{
    public const string FillMarker = "<<fill";
    public const long MaxFixtureBytes = 1024 * 1024;
    private const string OfflineEndpoint = "http://offline.invalid/v1";

    public static QreRebuildFixture LoadFixture(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new QreDiagnosticsInputException("fixture_not_found", "Fixture file was not found.");
        }
        if (info.Length > MaxFixtureBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new QreDiagnosticsInputException("fixture_rejected", "Fixture is too large or is a link.");
        }
        QreRebuildFixture? fixture;
        try
        {
            fixture = JsonSerializer.Deserialize(File.ReadAllBytes(path), QreDiagnosticsJsonContext.Default.QreRebuildFixture);
        }
        catch (JsonException)
        {
            throw new QreDiagnosticsInputException("fixture_invalid", "Fixture is not valid JSON for the fixture schema.");
        }
        if (fixture == null || !string.Equals(fixture.Schema, QreRebuildFixture.CurrentSchema, StringComparison.Ordinal))
        {
            throw new QreDiagnosticsInputException("fixture_schema_unsupported", "Fixture schema is missing or unsupported.");
        }
        return fixture;
    }

    public static async Task<QreDiagnoseRebuildOutput> RebuildAsync(
        QreRebuildFixture fixture,
        string fixtureName,
        Func<RuntimeModelRequest, ChatOptions>? optionsFactory,
        CancellationToken ct)
    {
        var missing = FindMissing(fixture);
        if (!string.Equals(fixture.Status, "runnable", StringComparison.Ordinal) || missing.Count > 0)
        {
            return Blocked(fixtureName, fixture, "fixture_incomplete", missing);
        }
        if (fixture.Assertions.Count == 0)
        {
            return Blocked(fixtureName, fixture, "fixture_has_no_assertions", []);
        }
        if (!QreModelApiModeParser.TryParse(fixture.ApiMode, out var apiMode, out _))
        {
            return Blocked(fixtureName, fixture, "api_mode_unsupported", []);
        }
        var mapping = optionsFactory ?? fixture.OptionsMapping switch
        {
            "qre-cli" => QreCli.CreateCliChatOptions,
            "passthrough-empty" => static _ => new ChatOptions(),
            _ => null
        };
        if (mapping == null)
        {
            return Blocked(fixtureName, fixture, "options_mapping_unknown", []);
        }

        RuntimeModelRequest request;
        try
        {
            request = BuildRequest(fixture);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return Blocked(fixtureName, fixture, "fixture_request_invalid", []);
        }

        var selector = QreModelProviderSelector.CreateDefault();
        IQreModelProvider provider;
        try
        {
            provider = selector.Select(fixture.Model);
        }
        catch (QreModelSelectionException)
        {
            return Blocked(fixtureName, fixture, "fixture_model_unsupported", []);
        }
        if (!provider.SupportedApiModes.Contains(apiMode))
        {
            return Blocked(fixtureName, fixture, "capability_unsupported", []);
        }

        var sink = new QreRebuildSink();
        var diagnostics = QreOutboundDiagnostics.Create(
            new QreOutboundDiagnosticsOptions { Mode = QreOutboundDiagnosticMode.Structure },
            sink);
        var transport = new QreOfflineModelTransport(apiMode, [QreOfflineResponse.TextResponse("offline rebuild")]);
        using (var client = new MeaiRuntimeModelClient(
                   selector.CreateClient(new QreModelClientDescriptor
                   {
                       ApiUrl = new Uri(OfflineEndpoint),
                       ApiKey = "offline-fixture",
                       Model = fixture.Model,
                       ApiMode = apiMode,
                       HttpClient = new HttpClient(diagnostics.CreateHandler(transport))
                   }),
                   mapping,
                   diagnostics,
                   QreOutboundDiagnosticsTarget.ForProvider(provider, apiMode)))
        {
            try
            {
                await foreach (var _ in client.StreamAsync(request, new RuntimeModelAttemptContext(1, "offline-rebuild"), ct).ConfigureAwait(false))
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Blocked(fixtureName, fixture, "rebuild_execution_failed", []) with { HttpRequests = transport.RequestCount };
            }
        }

        var records = sink.Records;
        var callId = records.FirstOrDefault(static r => r.ModelCallId != null)?.ModelCallId;
        if (callId == null)
        {
            return Blocked(fixtureName, fixture, "no_model_call_observed", []);
        }
        var analysis = QreDiagnosticsAnalyzer.AnalyzeCall(callId, records.Where(r => r.ModelCallId == callId).ToArray(), versionsSupported: true);
        var results = fixture.Assertions.Select(assertion => Evaluate(assertion, records, analysis)).ToArray();
        var insufficient = results.Any(static r => r.Actual == "unobserved");
        var passed = results.All(static r => r.Passed);
        var sdkChanged = fixture.SdkVersion != null &&
                         !string.Equals(fixture.SdkVersion, QreTransportCapabilityMatrix.SdkVersion, StringComparison.Ordinal);
        return new QreDiagnoseRebuildOutput
        {
            Fixture = fixtureName,
            Result = insufficient ? "blocked" : passed ? "passed" : "failed",
            ReasonCode = insufficient ? "insufficient_evidence" : passed ? "assertions_passed" : "assertions_failed",
            ApiMode = QreApiModeNames.ToWireName(apiMode),
            SdkVersion = QreTransportCapabilityMatrix.SdkVersion,
            SdkVersionChanged = sdkChanged,
            HttpRequests = transport.RequestCount,
            Assertions = results,
            Analysis = analysis
        };
    }

    public static QreRebuildFixture CreateSkeleton(QreDiagnosticsDocument document, string? modelCallId)
    {
        var calls = QreDiagnosticsAnalyzer.Analyze(document);
        var call = modelCallId == null
            ? calls.FirstOrDefault(static c => c.ObservedStages.Contains(QreDiagnosticsAnalyzer.RuntimeStage))
            : calls.FirstOrDefault(c => c.ModelCallId == modelCallId);
        if (call == null)
        {
            throw new QreDiagnosticsInputException("model_call_not_found", "No model call with a complete Runtime observation was found.");
        }
        var records = document.Records.Where(r => r.ModelCallId == call.ModelCallId).ToArray();
        var runtimeRecord = records.First(static r => r.EventType == QreDiagnosticEventTypes.ModelCallStarted);
        var runtime = runtimeRecord.Request!;
        var missing = new List<string>();
        var messages = new List<QreFixtureMessage>();
        var index = 0;
        foreach (var message in runtime.Messages.Items)
        {
            index++;
            var items = new List<QreFixtureItem>();
            foreach (var kind in message.ItemKinds)
            {
                switch (kind)
                {
                    case "text":
                        items.Add(new QreFixtureItem { Kind = "text", Text = $"{FillMarker}: synthetic {message.Role} text #{index}>>" });
                        break;
                    case "tool_call":
                        items.Add(new QreFixtureItem
                        {
                            Kind = "tool_call",
                            CallId = $"{FillMarker}: call id>>",
                            ToolName = $"{FillMarker}: tool name>>",
                            ArgumentsJson = $"{FillMarker}: synthetic arguments json>>"
                        });
                        break;
                    case "tool_result":
                        items.Add(new QreFixtureItem
                        {
                            Kind = "tool_result",
                            CallId = $"{FillMarker}: call id matching an earlier tool_call>>",
                            Text = $"{FillMarker}: synthetic tool result>>"
                        });
                        break;
                    default:
                        missing.Add($"messages[{index}]: {kind} item was not converted; add an equivalent synthetic item if it matters.");
                        break;
                }
            }
            messages.Add(new QreFixtureMessage { Role = message.Role, Items = items });
        }
        var tools = runtime.Tools.Aliases.Select(alias => new QreFixtureTool
        {
            Name = $"{FillMarker}: declared name for {alias}>>",
            Description = string.Empty,
            InputSchemaJson = $"{FillMarker}: minimal input schema for {alias}>>"
        }).ToArray();
        var required = runtime.ToolChoice.RequiredToolAlias == null
            ? null
            : $"{FillMarker}: required name for {runtime.ToolChoice.RequiredToolAlias} ({runtime.ToolChoice.RequiredToolRelation})>>";

        var assertions = new List<QreFixtureAssertion>();
        foreach (var finding in call.Findings.Where(static f => f.Classification == QreFindingClass.UnexpectedChange))
        {
            switch (finding.FieldPath)
            {
                case "maxOutputTokens" when runtime.MaxOutputTokens.Value is { } tokens:
                    assertions.Add(new QreFixtureAssertion
                    {
                        Kind = "field_equals",
                        Stage = QreDiagnosticsAnalyzer.HttpStage,
                        Field = "maxOutputTokens",
                        Expected = tokens.ToString("R", CultureInfo.InvariantCulture)
                    });
                    break;
                case "responseFormat":
                    assertions.Add(new QreFixtureAssertion { Kind = "json_output_mapped" });
                    break;
                case "toolChoice" or "toolChoice.requiredToolName":
                    assertions.Add(new QreFixtureAssertion { Kind = "required_tool_still_required" });
                    break;
                case "messages.toolResultLinks":
                    assertions.Add(new QreFixtureAssertion { Kind = "tool_result_links_preserved" });
                    break;
            }
        }
        assertions.Add(new QreFixtureAssertion { Kind = "no_unexpected_change" });

        var fixture = new QreRebuildFixture
        {
            Schema = QreRebuildFixture.CurrentSchema,
            Status = "skeleton",
            Issue = $"{FillMarker}: describe the problem this regression case covers>>",
            ApiMode = runtimeRecord.ApiMode ?? $"{FillMarker}: api mode>>",
            Model = $"{FillMarker}: offline model id selecting provider '{runtimeRecord.ProviderCategory ?? "unknown"}'>>",
            SdkVersion = runtimeRecord.SdkVersion,
            AdapterVersion = runtimeRecord.AdapterVersion,
            OptionsMapping = "qre-cli",
            Request = new QreFixtureRequest
            {
                Messages = messages,
                Tools = tools,
                Temperature = runtime.Temperature.Value,
                MaxOutputTokens = runtime.MaxOutputTokens.Value is { } max ? (int)max : null,
                RequireJsonObject = runtime.ResponseFormat.Value == "json_object",
                RequiredToolName = required
            },
            Assertions = assertions.DistinctBy(static a => (a.Kind, a.Field)).ToArray()
        };
        missing.AddRange(FindMissing(fixture));
        missing.Add("Review that every filled value is synthetic and non-sensitive, then set status to \"runnable\".");
        return fixture with { Missing = missing };
    }

    internal static IReadOnlyList<string> FindMissing(QreRebuildFixture fixture)
    {
        var missing = new List<string>();
        void Check(string? value, string path)
        {
            if (value != null && value.Contains(FillMarker, StringComparison.Ordinal))
            {
                missing.Add(path);
            }
        }
        Check(fixture.Issue, "issue");
        Check(fixture.ApiMode, "apiMode");
        Check(fixture.Model, "model");
        Check(fixture.Request.RequiredToolName, "request.requiredToolName");
        for (var m = 0; m < fixture.Request.Messages.Count; m++)
        {
            for (var i = 0; i < fixture.Request.Messages[m].Items.Count; i++)
            {
                var item = fixture.Request.Messages[m].Items[i];
                var path = $"request.messages[{m}].items[{i}]";
                Check(item.Text, path + ".text");
                Check(item.CallId, path + ".callId");
                Check(item.ToolName, path + ".toolName");
                Check(item.ArgumentsJson, path + ".argumentsJson");
            }
        }
        for (var t = 0; t < fixture.Request.Tools.Count; t++)
        {
            Check(fixture.Request.Tools[t].Name, $"request.tools[{t}].name");
            Check(fixture.Request.Tools[t].InputSchemaJson, $"request.tools[{t}].inputSchemaJson");
        }
        return missing;
    }

    private static RuntimeModelRequest BuildRequest(QreRebuildFixture fixture)
    {
        var messages = fixture.Request.Messages.Select(static message => new RuntimeMessage(
            message.Role switch
            {
                "system" => RuntimeMessageRole.System,
                "user" => RuntimeMessageRole.User,
                "assistant" => RuntimeMessageRole.Assistant,
                "tool" => RuntimeMessageRole.Tool,
                _ => throw new ArgumentException("Unsupported fixture role.")
            },
            message.Items.Select<QreFixtureItem, RuntimeItem>(static item => item.Kind switch
            {
                "text" => new RuntimeTextItem(item.Text ?? string.Empty),
                "tool_call" => new RuntimeToolCallItem(new RuntimeToolCall(
                    new RuntimeInvocationId(item.CallId ?? throw new ArgumentException("tool_call requires callId.")),
                    item.ToolName ?? throw new ArgumentException("tool_call requires toolName."),
                    ParseObject(item.ArgumentsJson ?? "{}"))),
                "tool_result" => new RuntimeToolResultItem(new RuntimeToolResult(
                    new RuntimeInvocationId(item.CallId ?? throw new ArgumentException("tool_result requires callId.")),
                    item.Text,
                    true)),
                _ => throw new ArgumentException("Unsupported fixture item kind.")
            }).ToArray())).ToArray();
        var tools = fixture.Request.Tools.Select(static tool => new RuntimeToolDescriptor(
            tool.Name,
            "1",
            tool.Description,
            ParseObject(tool.InputSchemaJson),
            RuntimeToolSideEffect.ReadOnly,
            RuntimeToolIdempotency.Idempotent)).ToArray();
        return new RuntimeModelRequest(
            new RuntimeSessionId("offline-rebuild"),
            new RuntimeTurnId("offline-rebuild-turn"),
            new RuntimeStepId("offline-rebuild-step"),
            messages,
            tools,
            new RuntimeModelParameters(
                fixture.Model,
                fixture.Request.Temperature,
                fixture.Request.MaxOutputTokens,
                fixture.Request.RequireJsonObject,
                fixture.Request.RequiredToolName),
            0);
    }

    private static JsonElement ParseObject(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Fixture JSON values must be objects.");
        }
        return document.RootElement.Clone();
    }

    private static QreFixtureAssertionResult Evaluate(
        QreFixtureAssertion assertion,
        IReadOnlyList<QreOutboundDiagnosticRecord> records,
        QreModelCallAnalysis analysis)
    {
        var runtime = records.FirstOrDefault(static r => r.EventType == QreDiagnosticEventTypes.ModelCallStarted)?.Request;
        var http = records.FirstOrDefault(static r => r.EventType == QreDiagnosticEventTypes.RequestStructureObserved && r.Request != null)?.Request;
        switch (assertion.Kind)
        {
            case "required_tool_still_required":
            {
                var expected = runtime?.ToolChoice.RequiredToolAlias ?? "none";
                var actual = http == null ? "unobserved" : http.ToolChoice.Mode == "require_specific" ? http.ToolChoice.RequiredToolAlias ?? "none" : http.ToolChoice.Mode;
                return new(assertion.Kind, "http.toolChoice", $"require_specific({expected})", actual == expected ? $"require_specific({actual})" : actual, actual == expected);
            }
            case "json_output_mapped":
            {
                var actual = http?.ResponseFormat.Value ?? "unobserved";
                return new(assertion.Kind, "http.responseFormat", "json_object|json_schema", actual, actual is "json_object" or "json_schema");
            }
            case "tool_result_links_preserved":
            {
                var actual = http == null ? "unobserved" : $"{http.Messages.LinkedToolResults}/{http.Messages.UnlinkedToolResults}";
                var expected = $"{runtime?.Messages.LinkedToolResults ?? 0}/0";
                return new(assertion.Kind, "http.messages.toolResultLinks", expected, actual, actual == expected);
            }
            case "no_unexpected_change":
            {
                var unexpected = analysis.Findings.Where(static f => f.Classification == QreFindingClass.UnexpectedChange).ToArray();
                var actual = analysis.Verdict == QreFindingClass.InsufficientEvidence && unexpected.Length == 0
                    ? "unobserved"
                    : unexpected.Length == 0 ? "none" : string.Join(',', unexpected.Select(static f => $"{f.SourceStage}->{f.TargetStage}:{f.FieldPath}"));
                return new(assertion.Kind, "analysis", "none", actual, unexpected.Length == 0 && actual != "unobserved");
            }
            case "field_equals":
            {
                var stage = assertion.Stage ?? QreDiagnosticsAnalyzer.HttpStage;
                var request = stage switch
                {
                    QreDiagnosticsAnalyzer.RuntimeStage => runtime,
                    QreDiagnosticsAnalyzer.AdapterStage => records.FirstOrDefault(static r => r.EventType == QreDiagnosticEventTypes.AdapterPrepared)?.Request,
                    _ => http
                };
                var actual = request == null ? "unobserved" : Field(request, assertion.Field ?? string.Empty);
                return new(assertion.Kind, $"{stage}.{assertion.Field}", assertion.Expected ?? "null", actual, string.Equals(actual, assertion.Expected, StringComparison.Ordinal));
            }
            default:
                return new(assertion.Kind, "unknown", "known assertion kind", "unsupported", false);
        }
    }

    private static string Field(QreSemanticRequest request, string field) => field switch
    {
        "maxOutputTokens" => QreDiagnosticsAnalyzer.Number(request.MaxOutputTokens),
        "temperature" => request.Temperature.Value is { } t ? ((float)t).ToString("R", CultureInfo.InvariantCulture) : request.Temperature.State,
        "responseFormat" => request.ResponseFormat.Value ?? request.ResponseFormat.State,
        "toolChoice.mode" => request.ToolChoice.Mode,
        "toolChoice.requiredToolRelation" => request.ToolChoice.RequiredToolRelation,
        "tools.count" => request.Tools.Count.ToString(CultureInfo.InvariantCulture),
        "messages.count" => request.Messages.Count.ToString(CultureInfo.InvariantCulture),
        "stream" => request.Stream.Value ?? request.Stream.State,
        "model.state" => request.Model.State,
        _ => "unsupported_field"
    };

    private static QreDiagnoseRebuildOutput Blocked(string fixtureName, QreRebuildFixture fixture, string reason, IReadOnlyList<string> missing)
        => new()
        {
            Fixture = fixtureName,
            Result = "blocked",
            ReasonCode = reason,
            ApiMode = fixture.ApiMode,
            SdkVersion = QreTransportCapabilityMatrix.SdkVersion,
            SdkVersionChanged = fixture.SdkVersion != null &&
                                !string.Equals(fixture.SdkVersion, QreTransportCapabilityMatrix.SdkVersion, StringComparison.Ordinal),
            Missing = missing
        };

    private sealed class QreRebuildSink : IQreOutboundDiagnosticSink
    {
        private readonly List<QreOutboundDiagnosticRecord> _records = [];

        public IReadOnlyList<QreOutboundDiagnosticRecord> Records
        {
            get
            {
                lock (_records)
                {
                    return _records.OrderBy(static r => r.Sequence).ToArray();
                }
            }
        }

        public bool TryWrite(QreOutboundDiagnosticRecord record, ReadOnlyMemory<byte> utf8Json)
        {
            lock (_records)
            {
                _records.Add(record);
            }
            return true;
        }
    }
}
