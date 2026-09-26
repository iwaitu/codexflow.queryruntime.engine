using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CodexFlow.QueryRuntime.Cli.Diagnostics;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Cli;

/// <summary>
/// End-to-end CLI verification of outbound diagnostics through the v2 CLI,
/// AgentRuntime, the locked SDK and an offline in-memory transport. The test host
/// only replaces the innermost transport; tool validation, option mapping and the
/// SDK are the production code paths.
/// </summary>
[Collection("QreCliConsole")]
public sealed class QreDiagnosticsCliTests
{
    private const string OfflineUrl = "http://offline.invalid/v1";
    private const string OfflineKey = "offline-test-key-CANARY";
    private const string Model = "qwen3-next-80b";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("QRE_READ_FILE", true)]
    [InlineData("qre_read_file", false)]
    public async Task CaseDemo_ReadonlyRequiredToolRunsThroughCliSdkAndOfflineTransport(string requiredTool, bool caseMismatch)
    {
        using var workspace = Workspace.Create();
        File.WriteAllText(Path.Combine(workspace.Path, "fixture.txt"), "non-sensitive fixture content\n");
        var transport = new QreOfflineModelTransport(
            QreModelApiMode.ChatCompletions,
            [
                QreOfflineResponse.TextResponse("I will look at the workspace first."),
                QreOfflineResponse.ToolCall("qre_read_file", "call-fixture-1", "{\"path\":\"fixture.txt\"}"),
                QreOfflineResponse.TextResponse("fixture completed")
            ]);

        var run = await RunCliAsync(transport,
            "run", "--workspace", workspace.Path,
            "--profile", "readonly",
            "--required-tool", requiredTool,
            "--max-rounds", "3",
            "--api-url", OfflineUrl, "--api-key", OfflineKey, "--model", Model,
            "--sdk-diagnostics", "structure",
            "--trace-data", "sanitized",
            "--json",
            "read the fixture file");

        Assert.Equal(0, run.ExitCode);
        using var json = JsonDocument.Parse(run.StandardOutput);
        var root = json.RootElement;
        Assert.Equal("Completed", root.GetProperty("status").GetString());
        Assert.Equal("Completed", root.GetProperty("terminationReason").GetString());
        Assert.False(root.TryGetProperty("errorCode", out _));
        Assert.Equal("fixture completed", root.GetProperty("finalText").GetString());
        Assert.Equal(3, root.GetProperty("totalSteps").GetInt32());
        Assert.Equal(1, root.GetProperty("totalToolCalls").GetInt32());
        Assert.Equal(1, root.GetProperty("continuationCount").GetInt32());

        // Model calls and HTTP sends are counted separately; this cell is verified
        // to make exactly one handler-visible send per streaming call.
        Assert.Equal(3, transport.RequestCount);
        Assert.False(transport.Exhausted);
        var diagnostics = root.GetProperty("diagnostics");
        Assert.Equal("complete", diagnostics.GetProperty("status").GetString());
        Assert.Equal(3, diagnostics.GetProperty("modelCalls").GetInt64());
        Assert.Equal(3, diagnostics.GetProperty("httpAttempts").GetInt64());

        var checkpoint = RuntimeJsonCheckpointStore.Read(root.GetProperty("checkpointPath").GetString()!);
        var turn = Assert.Single(checkpoint.Session.TerminalTurns);
        Assert.True(turn.Progress.RequiredToolSatisfied);
        Assert.Equal(3, turn.Steps.Count);
        Assert.All(turn.Steps, static step => Assert.Equal(1, step.ModelAttempts));

        var bodies = transport.RequestBodies.Select(static body => Encoding.UTF8.GetString(body)).ToArray();
        Assert.Contains($"\"tool_choice\":{{\"type\":\"function\",\"function\":{{\"name\":\"{requiredTool}\"}}}}", bodies[1], StringComparison.Ordinal);
        Assert.Contains("\"tool_call_id\":\"call-fixture-1\"", bodies[2], StringComparison.Ordinal);
        Assert.Contains("non-sensitive fixture content", bodies[2], StringComparison.Ordinal);
        Assert.DoesNotContain("\"tool_choice\":{\"type\":\"function\"", bodies[2], StringComparison.Ordinal);

        var inspect = await RunCliAsync(null, "diagnose", "latest", "--workspace", workspace.Path, "--json");
        Assert.Equal(0, inspect.ExitCode);
        using var report = JsonDocument.Parse(inspect.StandardOutput);
        var calls = report.RootElement.GetProperty("modelCalls").EnumerateArray().ToArray();
        Assert.Equal(3, calls.Length);
        Assert.All(calls, static call => Assert.Equal(1, call.GetProperty("runtimeModelAttemptOrdinal").GetInt32()));
        var requiredCalls = calls.Take(2).ToArray();
        foreach (var call in requiredCalls)
        {
            var findings = call.GetProperty("findings").EnumerateArray().ToArray();
            var caseFinding = findings.Where(static f => f.GetProperty("tags").EnumerateArray().Any(static t => t.GetString() == "case_only_mismatch")).ToArray();
            if (caseMismatch)
            {
                var finding = Assert.Single(caseFinding);
                Assert.Equal("runtime_prepared", finding.GetProperty("sourceStage").GetString());
                Assert.Equal("adapter_prepared", finding.GetProperty("targetStage").GetString());
                Assert.Equal("unexpected_change", finding.GetProperty("classification").GetString());
                Assert.Contains("http_preserves_case_only_mismatch", call.GetProperty("notes").EnumerateArray().Select(static n => n.GetString()));
                Assert.Equal("runtime_prepared->adapter_prepared", call.GetProperty("firstUnexpectedBoundary").GetString());
            }
            else
            {
                Assert.Empty(caseFinding);
            }
        }

        var records = LatestRecords(workspace.Path);
        var runtimeStart = records.First(static r => r.EventType == "model_call_started").Request!;
        Assert.Equal(caseMismatch ? "case_only_mismatch" : "declared_exact", runtimeStart.ToolChoice.RequiredToolRelation);
        Assert.Equal(caseMismatch, !runtimeStart.Tools.Aliases.Contains(runtimeStart.ToolChoice.RequiredToolAlias));
        var allJson = File.ReadAllText(Path.Combine(LatestRun(workspace.Path), QreDiagnosticsStore.EventsFileName));
        Assert.DoesNotContain("qre_read_file", allJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture content", allJson, StringComparison.Ordinal);
        Assert.DoesNotContain(OfflineKey, allJson, StringComparison.Ordinal);
        Assert.DoesNotContain("offline.invalid", allJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoToolProfileWithRequiredTool_IsRejectedBeforeAnyModelOrHttpCall()
    {
        using var workspace = Workspace.Create();
        var transport = new QreOfflineModelTransport(QreModelApiMode.ChatCompletions, [QreOfflineResponse.TextResponse("never")]);

        await Assert.ThrowsAsync<ArgumentException>(() => RunCliAsync(transport,
            "run", "--workspace", workspace.Path,
            "--profile", "none",
            "--required-tool", "qre_read_file",
            "--api-url", OfflineUrl, "--api-key", OfflineKey, "--model", Model,
            "--sdk-diagnostics", "structure",
            "--json",
            "do something"));

        Assert.Equal(0, transport.RequestCount);
        var inspect = await RunCliAsync(null, "diagnose", "latest", "--workspace", workspace.Path, "--json");
        using var report = JsonDocument.Parse(inspect.StandardOutput);
        Assert.Equal("model_call_not_started", report.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("runtime_initial_validation_rejected", report.RootElement.GetProperty("entryOutcome").GetString());
        Assert.Equal(0, report.RootElement.GetProperty("recordCount").GetInt32());
    }

    [Fact]
    public async Task DiagnosticsOff_KeepsRunOutputShapeAndCreatesNoDiagnosticFiles()
    {
        using var workspace = Workspace.Create();
        var transport = new QreOfflineModelTransport(QreModelApiMode.ChatCompletions, [QreOfflineResponse.TextResponse("plain")]);

        var run = await RunCliAsync(transport,
            "run", "--workspace", workspace.Path,
            "--api-url", OfflineUrl, "--api-key", OfflineKey, "--model", Model,
            "--json", "hello");

        Assert.Equal(0, run.ExitCode);
        using var json = JsonDocument.Parse(run.StandardOutput);
        Assert.False(json.RootElement.TryGetProperty("diagnostics", out _));
        Assert.False(Directory.Exists(QreDiagnosticsStore.DiagnosticsRoot(workspace.Path)));
        Assert.Equal(1, transport.RequestCount);
    }

    [Fact]
    public async Task MetadataMode_ReportsInsufficientEvidenceInsteadOfConsistency()
    {
        using var workspace = Workspace.Create();
        var transport = new QreOfflineModelTransport(QreModelApiMode.ChatCompletions, [QreOfflineResponse.TextResponse("meta")]);

        var run = await RunCliAsync(transport,
            "run", "--workspace", workspace.Path,
            "--api-url", OfflineUrl, "--api-key", OfflineKey, "--model", Model,
            "--sdk-diagnostics", "metadata", "hello");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("sdk_diagnostics: metadata status=complete", run.StandardOutput, StringComparison.Ordinal);
        var inspect = await RunCliAsync(null, "diagnose", "latest", "--workspace", workspace.Path, "--json");
        using var report = JsonDocument.Parse(inspect.StandardOutput);
        Assert.Equal("insufficient_evidence", report.RootElement.GetProperty("verdict").GetString());
        Assert.Contains(report.RootElement.GetProperty("limitations").EnumerateArray().Select(static l => l.GetString()!),
            static l => l.StartsWith("request_structure_not_captured", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StaticModel_ReportsTransportCaptureUnavailableAndNoCoverageClaim()
    {
        using var workspace = Workspace.Create();

        var run = await RunCliAsync(null,
            "run", "--workspace", workspace.Path, "--response", "static",
            "--sdk-diagnostics", "structure", "--json", "hello");

        Assert.Equal(0, run.ExitCode);
        using var json = JsonDocument.Parse(run.StandardOutput);
        Assert.Equal("transport_capture_unavailable", json.RootElement.GetProperty("diagnostics").GetProperty("transportCapture").GetString());
        var inspect = await RunCliAsync(null, "diagnose", "latest", "--workspace", workspace.Path, "--json");
        using var report = JsonDocument.Parse(inspect.StandardOutput);
        Assert.Equal("insufficient_evidence", report.RootElement.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task ExportCompareSkeletonAndRebuild_WorkOfflineAndStaySafe()
    {
        using var workspace = Workspace.Create();
        File.WriteAllText(Path.Combine(workspace.Path, "fixture.txt"), "non-sensitive fixture content\n");
        foreach (var required in new[] { "QRE_READ_FILE", "qre_read_file" })
        {
            var transport = new QreOfflineModelTransport(
                QreModelApiMode.ChatCompletions,
                [
                    QreOfflineResponse.TextResponse("first"),
                    QreOfflineResponse.ToolCall("qre_read_file", "call-fixture-1", "{\"path\":\"fixture.txt\"}"),
                    QreOfflineResponse.TextResponse("fixture completed")
                ]);
            var run = await RunCliAsync(transport,
                "run", "--workspace", workspace.Path, "--profile", "readonly", "--required-tool", required,
                "--max-rounds", "3", "--api-url", OfflineUrl, "--api-key", OfflineKey, "--model", Model,
                "--sdk-diagnostics", "structure", "--json", "SECRET-PROMPT-CANARY read the fixture");
            Assert.Equal(0, run.ExitCode);
            await Task.Delay(5, Ct);
        }
        var runs = Directory.GetDirectories(QreDiagnosticsStore.DiagnosticsRoot(workspace.Path), "diag-*").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, runs.Length);

        var compare = await RunCliAsync(null, "diagnose", "compare", runs[0], runs[1], "--json");
        Assert.Equal(1, compare.ExitCode);
        using (var result = JsonDocument.Parse(compare.StandardOutput))
        {
            Assert.Equal("different", result.RootElement.GetProperty("result").GetString());
            var earliest = result.RootElement.GetProperty("earliestDifference");
            Assert.Equal("runtime_prepared", earliest.GetProperty("sourceStage").GetString());
            Assert.Equal("toolChoice.relation", earliest.GetProperty("fieldPath").GetString());
            Assert.Contains("tool_aliases_not_compared_without_alias_map",
                result.RootElement.GetProperty("notes").EnumerateArray().Select(static n => n.GetString()));
        }

        var bundle = Path.Combine(workspace.Path, "bundle.zip");
        var export = await RunCliAsync(null, "diagnose", "export", runs[0], "--output", bundle, "--json");
        Assert.Equal(0, export.ExitCode);
        using (var archive = ZipFile.OpenRead(bundle))
        {
            Assert.Equal(
                [QreDiagnosticsStore.EventsFileName, QreDiagnosticsStore.ManifestFileName],
                archive.Entries.Select(static e => e.FullName).Order(StringComparer.Ordinal));
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                foreach (var canary in new[] { "SECRET-PROMPT-CANARY", OfflineKey, "offline.invalid", "qre_read_file", "QRE_READ_FILE", Model, workspace.Path.Replace("\\", "\\\\", StringComparison.Ordinal) })
                {
                    Assert.DoesNotContain(canary, text, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        var bundleInspect = await RunCliAsync(null, "diagnose", "inspect", bundle, "--json");
        Assert.Equal(0, bundleInspect.ExitCode);
        using (var report = JsonDocument.Parse(bundleInspect.StandardOutput))
        {
            Assert.Equal("export", report.RootElement.GetProperty("origin").GetString());
            Assert.Equal("unexpected_change", report.RootElement.GetProperty("verdict").GetString());
        }
        Assert.Equal(2, (await RunCliAsync(null, "diagnose", "export", runs[0], "--output", bundle)).ExitCode);

        var skeletonPath = Path.Combine(workspace.Path, "fixture.skeleton.json");
        var skeleton = await RunCliAsync(null, "diagnose", "skeleton", runs[0], "--output", skeletonPath, "--json");
        Assert.Equal(0, skeleton.ExitCode);
        var blocked = await RunCliAsync(null, "diagnose", "rebuild", skeletonPath, "--json");
        Assert.Equal(2, blocked.ExitCode);
        using (var result = JsonDocument.Parse(blocked.StandardOutput))
        {
            Assert.Equal("blocked", result.RootElement.GetProperty("result").GetString());
            Assert.Equal("fixture_incomplete", result.RootElement.GetProperty("reasonCode").GetString());
            Assert.NotEmpty(result.RootElement.GetProperty("missing").EnumerateArray());
        }

        // Human completion: synthetic content, exact-case required tool, reviewed.
        var fixture = QreDiagnosticsRebuilder.LoadFixture(skeletonPath);
        var completed = CompleteFixture(fixture, requiredTool: "qre_read_file");
        var fixturePath = Path.Combine(workspace.Path, "fixture.runnable.json");
        File.WriteAllBytes(fixturePath, JsonSerializer.SerializeToUtf8Bytes(completed, QreDiagnosticsJsonContext.Default.QreRebuildFixture));
        var passed = await RunCliAsync(null, "diagnose", "rebuild", fixturePath, "--json");
        Assert.Equal(0, passed.ExitCode);

        var caseFixturePath = Path.Combine(workspace.Path, "fixture.case.json");
        File.WriteAllBytes(caseFixturePath, JsonSerializer.SerializeToUtf8Bytes(CompleteFixture(fixture, requiredTool: "QRE_READ_FILE"), QreDiagnosticsJsonContext.Default.QreRebuildFixture));
        var failed = await RunCliAsync(null, "diagnose", "rebuild", caseFixturePath, "--json");
        Assert.Equal(1, failed.ExitCode);
        using (var result = JsonDocument.Parse(failed.StandardOutput))
        {
            Assert.Equal("failed", result.RootElement.GetProperty("result").GetString());
            Assert.Equal("offline_in_memory_transport", result.RootElement.GetProperty("network").GetString());
            Assert.Equal(1, result.RootElement.GetProperty("httpRequests").GetInt32());
        }
    }

    [Fact]
    public async Task CorruptDiagnosticSidecar_DoesNotAffectStrictReplay()
    {
        using var workspace = Workspace.Create();
        var transport = new QreOfflineModelTransport(QreModelApiMode.ChatCompletions, [QreOfflineResponse.TextResponse("replayable")]);
        var run = await RunCliAsync(transport,
            "run", "--workspace", workspace.Path,
            "--api-url", OfflineUrl, "--api-key", OfflineKey, "--model", Model,
            "--sdk-diagnostics", "structure", "--trace-data", "sanitized", "--json", "hello");
        Assert.Equal(0, run.ExitCode);
        var diagnosticRun = LatestRun(workspace.Path);
        File.WriteAllText(Path.Combine(diagnosticRun, QreDiagnosticsStore.EventsFileName), "{not json");
        File.WriteAllText(Path.Combine(diagnosticRun, QreDiagnosticsStore.ManifestFileName), "{\"manifestSchema\":\"future/9\"}");

        var replay = await RunCliAsync(null, "replay", "latest", "--workspace", workspace.Path, "--strict", "--json");
        Assert.Equal(0, replay.ExitCode);
        var inspect = await RunCliAsync(null, "diagnose", "latest", "--workspace", workspace.Path, "--json");
        Assert.Equal(2, inspect.ExitCode);
        using var error = JsonDocument.Parse(inspect.StandardOutput);
        Assert.Equal("qre.diagnose.error", error.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task InvalidDiagnoseArguments_UseExitCodeTwo()
    {
        Assert.Equal(2, (await RunCliAsync(null, "diagnose", "compare", "only-one")).ExitCode);
        Assert.Equal(2, (await RunCliAsync(null, "diagnose", "unknown")).ExitCode);
        Assert.Equal(2, (await RunCliAsync(null, "diagnose", "rebuild", "missing-fixture.json")).ExitCode);
        Assert.Equal(1, (await RunCliAsync(null, "run", "--sdk-diagnostics", "verbose", "x")).ExitCode);
        var help = await RunCliAsync(null, "diagnose", "--help");
        Assert.Contains("not proof of network bytes", help.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("never counted as a pass", help.StandardOutput, StringComparison.Ordinal);
    }

    internal static QreRebuildFixture CompleteFixture(QreRebuildFixture skeleton, string requiredTool)
    {
        var index = 0;
        return skeleton with
        {
            Status = "runnable",
            Issue = "Case-only mismatch between the required tool name and its declaration.",
            Model = Model,
            Missing = [],
            Request = skeleton.Request with
            {
                Messages = skeleton.Request.Messages.Select(message => message with
                {
                    Items = message.Items.Select(item => item with { Text = $"synthetic {message.Role} text {++index}" }).ToArray()
                }).ToArray(),
                Tools = skeleton.Request.Tools.Select(static tool => tool with
                {
                    Name = "qre_read_file",
                    InputSchemaJson = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}"
                }).ToArray(),
                RequiredToolName = skeleton.Request.RequiredToolName == null ? null : requiredTool
            }
        };
    }

    private static string LatestRun(string workspace)
        => Directory.GetDirectories(QreDiagnosticsStore.DiagnosticsRoot(workspace), "diag-*").Order(StringComparer.Ordinal).Last();

    private static IReadOnlyList<CodexFlow.QueryRuntime.Models.Diagnostics.QreOutboundDiagnosticRecord> LatestRecords(string workspace)
        => QreDiagnosticsReader.Load(LatestRun(workspace)).Records;

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCliAsync(
        QreOfflineModelTransport? transport,
        params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        QreCli.TestTransportFactory = transport == null ? null : _ => transport;
        try
        {
            var exit = await QreCli.RunAsync(args, Ct);
            return (exit, stdout.ToString().Trim(), stderr.ToString().Trim());
        }
        finally
        {
            QreCli.TestTransportFactory = null;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class Workspace : IDisposable
    {
        private Workspace(string path) => Path = path;

        public string Path { get; }

        public static Workspace Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qre-diag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new Workspace(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
