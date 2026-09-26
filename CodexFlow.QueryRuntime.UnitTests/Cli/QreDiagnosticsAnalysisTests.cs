using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodexFlow.QueryRuntime.Cli.Diagnostics;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using CodexFlow.QueryRuntime.Protocol;
using CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics;
using Microsoft.Extensions.AI;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics.DiagnosticsTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Cli;

/// <summary>Cross-layer rule table (plan §8.1) and the end-to-end demonstrations.</summary>
public sealed class QreDiagnosticsAnalysisTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qre-diag-analysis-" + Guid.NewGuid().ToString("N"));

    public QreDiagnosticsAnalysisTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Demo host with a deliberately defective options factory: it forwards
    /// temperature but forgets MaxOutputTokens and RequireJsonObject. The defect
    /// exists only in this demo host, never in the CLI mapping.
    /// </summary>
    private static ChatOptions DefectiveDemoMapping(RuntimeModelRequest request)
        => new() { Temperature = request.Parameters.Temperature is { } t ? (float)t : null };

    [Fact]
    public async Task DefectiveOptionsFactoryDemo_LocatesLossExportsSafelyAndRebuildsOffline()
    {
        var parameters = new RuntimeModelParameters(Model: ChatModel, Temperature: 0.2, MaxOutputTokens: 256, RequireJsonObject: true);
        var defectiveRun = await RunEmbeddedAsync(DefectiveDemoMapping, parameters, "defective");
        var fixedRun = await RunEmbeddedAsync(CliMapping, parameters, "fixed");

        // 1. Locate the first lost parameter at Runtime -> adapter.
        var defective = QreDiagnosticsReader.Load(defectiveRun);
        var call = Assert.Single(QreDiagnosticsAnalyzer.Analyze(defective));
        Assert.Equal(QreFindingClass.UnexpectedChange, call.Verdict);
        Assert.Equal("runtime_prepared->adapter_prepared", call.FirstUnexpectedBoundary);
        var lost = call.Findings.Where(static f => f.Classification == QreFindingClass.UnexpectedChange).ToArray();
        Assert.Contains(lost, static f => f.FieldPath == "maxOutputTokens" && f.Before == "256" && f.After == QreDiagnosticFieldStates.Absent);
        Assert.Contains(lost, static f => f.FieldPath == "responseFormat" && f.Before == "json_object");
        Assert.All(lost, static f => Assert.Equal(QreDiagnosticsAnalyzer.RuntimeStage, f.SourceStage));
        Assert.DoesNotContain(call.Findings, static f => f.FieldPath == "temperature" && f.Classification == QreFindingClass.UnexpectedChange);

        var fixedCall = Assert.Single(QreDiagnosticsAnalyzer.Analyze(QreDiagnosticsReader.Load(fixedRun)));
        Assert.DoesNotContain(fixedCall.Findings, static f => f.Classification == QreFindingClass.UnexpectedChange &&
                                                              f.SourceStage == QreDiagnosticsAnalyzer.RuntimeStage);

        // 2. Cross-run: earliest observable difference is the adapter stage.
        var compare = QreDiagnosticsComparer.Compare(defective, QreDiagnosticsReader.Load(fixedRun), new Dictionary<string, string>(), new Dictionary<string, string>());
        Assert.Equal("different", compare.Result);
        Assert.Equal(QreDiagnosticsAnalyzer.AdapterStage, compare.EarliestDifference!.SourceStage);
        Assert.Equal("maxOutputTokens", compare.EarliestDifference.FieldPath);

        // 3. Export a safe package without secrets or bodies.
        var bundle = Path.Combine(_root, "defective.zip");
        QreDiagnosticsExporter.Export(defective, bundle, force: false);
        using (var archive = ZipFile.OpenRead(bundle))
        {
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                Assert.DoesNotContain("DEMO-PROMPT-CANARY", text, StringComparison.Ordinal);
                Assert.DoesNotContain("demo-api-key", text, StringComparison.Ordinal);
                Assert.DoesNotContain(ChatModel, text, StringComparison.Ordinal);
            }
        }

        // 4. Skeleton from the package, completed by a human with synthetic content.
        var skeleton = QreDiagnosticsRebuilder.CreateSkeleton(QreDiagnosticsReader.Load(bundle), null);
        Assert.Equal("skeleton", skeleton.Status);
        Assert.Contains(skeleton.Assertions, static a => a.Kind == "field_equals" && a.Field == "maxOutputTokens" && a.Expected == "256");
        Assert.Contains(skeleton.Assertions, static a => a.Kind == "json_output_mapped");
        var blocked = await QreDiagnosticsRebuilder.RebuildAsync(skeleton, "skeleton", DefectiveDemoMapping, Ct);
        Assert.Equal("blocked", blocked.Result);
        var fixture = QreDiagnosticsCliTests.CompleteFixture(skeleton, requiredTool: "unused");

        // 5. Offline verification: the defective host fails, the fixed host passes.
        var failing = await QreDiagnosticsRebuilder.RebuildAsync(fixture, "demo", DefectiveDemoMapping, Ct);
        Assert.Equal("failed", failing.Result);
        Assert.Contains(failing.Assertions, static a => !a.Passed && a.Target.EndsWith("maxOutputTokens", StringComparison.Ordinal));
        var passing = await QreDiagnosticsRebuilder.RebuildAsync(fixture, "demo", CliMapping, Ct);
        Assert.Equal("passed", passing.Result);
        Assert.Equal(1, passing.HttpRequests);
        Assert.Equal("offline_in_memory_transport", passing.Network);
        // The same fixture through the CLI's named mappings.
        Assert.Equal("failed", (await QreDiagnosticsRebuilder.RebuildAsync(fixture with { OptionsMapping = "passthrough-empty" }, "demo", null, Ct)).Result);
        Assert.Equal("passed", (await QreDiagnosticsRebuilder.RebuildAsync(fixture, "demo", null, Ct)).Result);
    }

    [Fact]
    public async Task DirectAdapter_EmptyToolsWithRequiredToolIsInputConstraintConflict()
    {
        var (sink, analysis) = await AnalyzeDirectAsync(
            Request([], new RuntimeModelParameters(Model: ChatModel, RequiredToolName: "qre_read_file")));

        var finding = Assert.Single(analysis.Findings, static f => f.FieldPath == "toolChoice" && f.SourceStage == QreDiagnosticsAnalyzer.RuntimeStage);
        Assert.Equal(QreFindingClass.UnexpectedChange, finding.Classification);
        Assert.Contains("input_constraint_conflict", finding.Tags);
        Assert.Equal("none", finding.After);
        Assert.Equal("runtime_prepared->adapter_prepared", analysis.FirstUnexpectedBoundary);

    }

    [Fact]
    public async Task NoToolsAndNoRequiredTool_IsExpectedTransform()
    {
        var (_, analysis) = await AnalyzeDirectAsync(Request());

        Assert.Contains(analysis.Findings, static f => f.Rule == "no_tools_disables_tool_mode" && f.Classification == QreFindingClass.ExpectedTransform);
        Assert.Equal("consistent", analysis.Verdict);
    }

    [Fact]
    public async Task ModelIdentity_DescriptorDefaultIsEquivalentAndUnknownDefaultIsNotComparable()
    {
        var (_, equivalent) = await AnalyzeDirectAsync(Request());
        Assert.Contains(equivalent.Findings, static f => f.Rule == "descriptor_default_model_equivalent" && f.Classification == QreFindingClass.ExpectedTransform);
        Assert.DoesNotContain(equivalent.Findings, static f => f.FieldPath == "model" && f.Classification == QreFindingClass.UnexpectedChange);

        var (_, different) = await AnalyzeDirectAsync(Request(parameters: new RuntimeModelParameters(Model: "qwen3-other-model")));
        Assert.Contains(different.Findings, static f => f.Rule == "descriptor_default_model_differs" && f.Classification == QreFindingClass.UnexpectedChange);

        // A custom IChatClient with no metadata exposes no safe default source.
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink);
        using var client = new MeaiRuntimeModelClient(new MetadataFreeChatClient(), CliMapping, diagnostics);
        await DrainAsync(client.StreamAsync(Request(), new RuntimeModelAttemptContext(1), Ct), Ct);
        var custom = QreDiagnosticsAnalyzer.AnalyzeCall(sink.Records[0].ModelCallId!, sink.Records, versionsSupported: true);
        Assert.Contains(custom.Findings, static f => f.Rule == "model_default_source_unknown" && f.Classification == QreFindingClass.NotComparable);
        // No handler: transport capture is unavailable and nothing is claimed about HTTP.
        Assert.Contains(custom.Findings, static f => f.Rule == "http_structure_not_observed");
        Assert.Equal(QreFindingClass.InsufficientEvidence, custom.Verdict);
    }

    [Fact]
    public async Task TemperaturePrecisionAndNestedPathAreExpectedTransforms()
    {
        var (_, analysis) = await AnalyzeDirectAsync(Request(parameters: new RuntimeModelParameters(Model: ChatModel, Temperature: 0.7)));

        Assert.Contains(analysis.Findings, static f => f.Rule == "double_to_float_precision" && f.Classification == QreFindingClass.ExpectedTransform);
        Assert.Contains(analysis.Findings, static f => f.Rule == QreTransportCapabilityMatrix.TemperatureNestedOptions && f.Classification == QreFindingClass.ExpectedTransform);
        Assert.Equal("consistent", analysis.Verdict);
    }

    [Fact]
    public async Task KnownSdkLimitations_RemainUnexpectedChangesWithTags()
    {
        var parameters = new RuntimeModelParameters(Model: "gemini-3-pro", MaxOutputTokens: 64, RequireJsonObject: true);
        var (_, gemini) = await AnalyzeDirectAsync(Request(parameters: parameters), model: "gemini-3-pro");
        var maxTokens = Assert.Single(gemini.Findings, static f => f.FieldPath == "maxOutputTokens" && f.SourceStage == QreDiagnosticsAnalyzer.AdapterStage);
        Assert.Equal(QreFindingClass.UnexpectedChange, maxTokens.Classification);
        Assert.Contains("known_sdk_limitation", maxTokens.Tags);
        Assert.Equal("adapter_prepared->http_prepared", gemini.FirstUnexpectedBoundary);

        var withSystem = Request(parameters: new RuntimeModelParameters(Model: ChatModel, RequireJsonObject: true));
        withSystem = withSystem with
        {
            Messages = [new RuntimeMessage(RuntimeMessageRole.System, [new RuntimeTextItem("system rules")]), .. withSystem.Messages]
        };
        var (_, anthropic) = await AnalyzeDirectAsync(withSystem, apiMode: QreModelApiMode.AnthropicMessages);
        Assert.DoesNotContain(anthropic.Findings, static f => f.FieldPath == "messages" && f.Classification == QreFindingClass.UnexpectedChange);
        var format = Assert.Single(anthropic.Findings, static f => f.FieldPath == "responseFormat" && f.SourceStage == QreDiagnosticsAnalyzer.AdapterStage);
        Assert.Contains("unsupported_constraint", format.Tags);
        Assert.Contains(anthropic.Findings, static f => f.Rule == QreTransportCapabilityMatrix.MaxTokensDefaultWhenUnspecified && f.Classification == QreFindingClass.ExpectedTransform);
        Assert.Contains("system_hoisted_to_top_level", anthropic.Notes);

        var (_, gptOss) = await AnalyzeDirectAsync(Request(parameters: new RuntimeModelParameters(Model: "gpt-oss-20b")), model: "gpt-oss-20b");
        Assert.Contains(gptOss.Findings, static f => f.Rule == QreTransportCapabilityMatrix.SdkInjectedSystemPrompt && f.Classification == QreFindingClass.ExpectedTransform);
        Assert.DoesNotContain(gptOss.Findings, static f => f.FieldPath == "messages" && f.Classification == QreFindingClass.UnexpectedChange);
    }

    [Fact]
    public async Task ReorderedToolsAndMissingEvidenceAreNeverReportedAsConsistent()
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink);
        using var client = CreateModelClient(diagnostics, new DelegateHandler((_, _) =>
            Task.FromResult(Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("ok")))));
        await DrainAsync(client.StreamAsync(Request([Tool("alpha"), Tool("beta")]), new RuntimeModelAttemptContext(1), Ct), Ct);
        var records = sink.Records.ToList();
        var structureIndex = records.FindIndex(static r => r.EventType == QreDiagnosticEventTypes.RequestStructureObserved);
        var structure = records[structureIndex];
        records[structureIndex] = structure with
        {
            Request = structure.Request! with
            {
                Tools = structure.Request.Tools with { Aliases = structure.Request.Tools.Aliases.Reverse().ToArray() }
            }
        };

        var reordered = QreDiagnosticsAnalyzer.AnalyzeCall(records[0].ModelCallId!, records, versionsSupported: true);
        Assert.Contains(reordered.Findings, static f => f.FieldPath == "tools" && f.Classification == QreFindingClass.UnexpectedChange);

        records[structureIndex] = structure with { Request = null, CaptureStatus = QreDiagnosticCaptureStatus.Omitted, ReasonCode = "capture_limit_exceeded" };
        var omitted = QreDiagnosticsAnalyzer.AnalyzeCall(records[0].ModelCallId!, records, versionsSupported: true);
        Assert.Equal(QreFindingClass.InsufficientEvidence, omitted.Verdict);
        Assert.DoesNotContain(omitted.Findings, static f => f.Classification == QreFindingClass.UnexpectedChange);

        var unsupportedVersions = QreDiagnosticsAnalyzer.AnalyzeCall(records[0].ModelCallId!, sink.Records, versionsSupported: false);
        Assert.Equal(QreFindingClass.InsufficientEvidence, unsupportedVersions.Verdict);
    }

    [Fact]
    public async Task StepToolSelectionOmittingRequiredTool_FailsBeforeAnyModelOrHttpCall()
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink);
        var transport = new DelegateHandler((_, _) =>
            Task.FromResult(Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("never"))));
        using var client = CreateModelClient(diagnostics, transport);
        var tool = Tool("qre_read_file");
        var request = new RuntimeAgentLoopRequest(
            new RuntimeSessionId("omit-session"),
            new RuntimeTurnId("omit-turn"),
            "objective",
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("start")])],
            [tool],
            new RuntimeModelParameters(Model: ChatModel, RequiredToolName: "qre_read_file"),
            new RuntimePolicySnapshot("policy-v1", "readonly"),
            new RuntimeEnvironmentSnapshot("local", "workspace", "sha256:test"),
            new RuntimeBudgetSnapshot(3, 4, maxContinuations: 1),
            CreatedAt: DateTimeOffset.UnixEpoch)
        {
            ToolExecutor = new NoopExecutor(),
            ToolCatalogSelector = new OmittingSelector()
        };

        var result = await new AgentRuntime(client).RunAsync(new RuntimeRunRequest(request), null, Ct);

        Assert.Equal("required_tool_omitted_from_context", result.Error?.Code);
        Assert.Empty(transport.Requests);
        Assert.Empty(sink.Records);
    }

    private async Task<string> RunEmbeddedAsync(
        Func<RuntimeModelRequest, ChatOptions> mapping,
        RuntimeModelParameters parameters,
        string name)
    {
        var options = new QreOutboundDiagnosticsOptions { Mode = QreOutboundDiagnosticMode.Structure };
        var sink = new ForwardingSink();
        var diagnostics = QreOutboundDiagnostics.Create(options, sink);
        var runDirectory = Path.Combine(_root, $"diag-{name}");
        Directory.CreateDirectory(runDirectory);
        var coverage = new QreDiagnosticsCoverage { TransportCapture = "handler", AttemptCoverage = "handler_visible", RequestStructure = true };
        var store = QreDiagnosticsStore.CreateAt(runDirectory, options, diagnostics.SegmentId, coverage);
        sink.Target = store;
        var transport = new QreOfflineModelTransport(QreModelApiMode.ChatCompletions, [QreOfflineResponse.TextResponse("{\"ok\":true}")]);
        using (var client = CreateModelClient(diagnostics, transport, optionsFactory: mapping))
        {
            var loop = new RuntimeAgentLoopRequest(
                new RuntimeSessionId("demo-session"),
                new RuntimeTurnId("demo-turn"),
                "demo",
                [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("DEMO-PROMPT-CANARY return json")])],
                [],
                parameters,
                new RuntimePolicySnapshot("policy-v1", "none"),
                new RuntimeEnvironmentSnapshot("local", "workspace", "sha256:test"),
                new RuntimeBudgetSnapshot(2, 1, maxContinuations: 0),
                CreatedAt: DateTimeOffset.UnixEpoch);
            var result = await new AgentRuntime(client).RunAsync(new RuntimeRunRequest(loop), null, Ct);
            Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        }
        await store.CompleteAsync(diagnostics, coverage);
        await store.DisposeAsync();
        return runDirectory;
    }

    private static async Task<(InMemoryDiagnosticSink Sink, QreModelCallAnalysis Analysis)> AnalyzeDirectAsync(
        RuntimeModelRequest request,
        string model = ChatModel,
        QreModelApiMode apiMode = QreModelApiMode.ChatCompletions)
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = Create(sink);
        using var client = CreateModelClient(
            diagnostics,
            new DelegateHandler((_, _) => Task.FromResult(Sse(apiMode, QreOfflineResponse.TextResponse("ok")))),
            apiMode,
            model);
        await DrainAsync(client.StreamAsync(request, new RuntimeModelAttemptContext(1), Ct), Ct);
        var records = sink.Records;
        return (sink, QreDiagnosticsAnalyzer.AnalyzeCall(records[0].ModelCallId!, records, versionsSupported: true));
    }

    private sealed class ForwardingSink : IQreOutboundDiagnosticSink
    {
        public IQreOutboundDiagnosticSink? Target { get; set; }

        public bool TryWrite(QreOutboundDiagnosticRecord record, ReadOnlyMemory<byte> utf8Json)
            => Target?.TryWrite(record, utf8Json) ?? false;
    }

    private sealed class OmittingSelector : IRuntimeToolCatalogSelector
    {
        public IReadOnlyList<RuntimeToolDescriptor> SelectTools(PreparedRuntimeContext context, IReadOnlyList<RuntimeToolDescriptor> frozenCatalog, int stepIndex)
            => [];

        public void Observe(RuntimeToolCall call, RuntimeToolResult result)
        {
        }
    }

    private sealed class NoopExecutor : IRuntimeToolExecutor
    {
        public ValueTask<RuntimeToolResult> ExecuteAsync(RuntimeToolDescriptor descriptor, RuntimeToolCall call, RuntimeToolExecutionContext context, CancellationToken ct)
            => ValueTask.FromResult(new RuntimeToolResult(call.InvocationId, "ok", true));
    }

    private sealed class MetadataFreeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "custom");
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
