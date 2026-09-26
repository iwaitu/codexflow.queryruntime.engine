using System.IO.Compression;
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

public sealed class QreDiagnosticsStorageTests : IDisposable
{
    private static readonly QreDiagnosticsCoverage Coverage = new() { TransportCapture = "handler", AttemptCoverage = "handler_visible" };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qre-diag-store-" + Guid.NewGuid().ToString("N"));

    public QreDiagnosticsStorageTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public async Task Store_WritesCompleteJsonlAndAtomicManifest()
    {
        var options = new QreOutboundDiagnosticsOptions { Mode = QreOutboundDiagnosticMode.Structure };
        var (diagnostics, store) = Open(options);

        await RunOneCallAsync(diagnostics);
        var manifest = await store.CompleteAsync(diagnostics, Coverage);
        await store.DisposeAsync();

        Assert.Equal("complete", manifest.CompletionStatus);
        Assert.False(manifest.Counters!.EvidenceIncomplete);
        var document = QreDiagnosticsReader.Load(store.RunDirectory);
        Assert.Equal(manifest.Counters.RecordsWritten, document.Records.Count);
        Assert.False(document.Integrity.EvidenceIncomplete);
        Assert.Empty(Directory.GetFiles(store.RunDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Store_QueueFullDropsRecordsAndReportsIncomplete()
    {
        var options = new QreOutboundDiagnosticsOptions
        {
            Mode = QreOutboundDiagnosticMode.Structure,
            MaxPendingRecords = 1,
            MaxPendingBytes = 2048,
            MaxRecordBytes = 2048
        };
        var (diagnostics, store) = Open(options);
        var bytes = new byte[1500];
        var accepted = 0;
        for (var i = 0; i < 50; i++)
        {
            accepted += store.TryWrite(diagnostics.NewRecord("model_call_started", "runtime", null), bytes) ? 1 : 0;
        }

        var manifest = await store.CompleteAsync(diagnostics, Coverage);
        await store.DisposeAsync();

        Assert.True(accepted < 50);
        Assert.True(manifest.Counters!.QueueDropped > 0);
    }

    [Fact]
    public async Task Store_RunQuotaAndDiskFailureNeverBreakTheModelCall()
    {
        var quota = new QreOutboundDiagnosticsOptions { Mode = QreOutboundDiagnosticMode.Structure, MaxRunBytes = 64 * 1024 };
        var (quotaDiagnostics, quotaStore) = Open(quota);
        for (var i = 0; i < 40; i++)
        {
            await RunOneCallAsync(quotaDiagnostics);
        }
        var quotaManifest = await quotaStore.CompleteAsync(quotaDiagnostics, Coverage);
        await quotaStore.DisposeAsync();
        Assert.True(quotaManifest.Counters!.QuotaDropped > 0);
        Assert.Equal("incomplete", quotaManifest.CompletionStatus);
        Assert.True(new FileInfo(Path.Combine(quotaStore.RunDirectory, QreDiagnosticsStore.EventsFileName)).Length <= quota.MaxRunBytes);

        // Simulated disk failure: the events file path is occupied by a directory.
        var failing = Path.Combine(_root, "diag-failing");
        Directory.CreateDirectory(Path.Combine(failing, QreDiagnosticsStore.EventsFileName));
        var options = new QreOutboundDiagnosticsOptions { Mode = QreOutboundDiagnosticMode.Structure };
        var sink = new ForwardingSink();
        var diagnostics = QreOutboundDiagnostics.Create(options, sink);
        var store = QreDiagnosticsStore.CreateAt(failing, options, diagnostics.SegmentId, Coverage);
        sink.Target = store;

        var events = await RunOneCallAsync(diagnostics);
        var manifest = await store.CompleteAsync(diagnostics, Coverage);
        await store.DisposeAsync();

        Assert.IsType<RuntimeModelCompletedEvent>(events[^1]);
        Assert.Equal("incomplete", manifest.CompletionStatus);
        Assert.True(manifest.Counters!.WriteFailures > 0);
    }

    [Fact]
    public void Reader_ReportsTruncatedTailAndSequenceGapsWithoutFailing()
    {
        var run = Path.Combine(_root, "diag-truncated");
        Directory.CreateDirectory(run);
        var record = Record(1);
        var gap = Record(3);
        File.WriteAllText(
            Path.Combine(run, QreDiagnosticsStore.EventsFileName),
            Serialize(record) + "\n" + Serialize(gap) + "\n" + Serialize(Record(4))[..40]);

        var document = QreDiagnosticsReader.Load(run);

        Assert.Equal(2, document.Records.Count);
        Assert.True(document.Integrity.TruncatedTail);
        Assert.Equal(1, document.Integrity.SequenceGaps);
        Assert.False(document.Integrity.ManifestPresent);
        Assert.True(document.Integrity.EvidenceIncomplete);
    }

    [Theory]
    [InlineData("../escape.jsonl", "bundle_path_rejected")]
    [InlineData("nested/events.jsonl", "bundle_path_rejected")]
    [InlineData("checkpoint.v1.json", "bundle_entry_rejected")]
    public void Reader_RejectsUnsafeBundleEntries(string entryName, string reason)
    {
        var bundle = Path.Combine(_root, $"bad-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(bundle, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryName).Open());
            writer.Write("{}");
        }

        var exception = Assert.Throws<QreDiagnosticsInputException>(() => QreDiagnosticsReader.Load(bundle));
        Assert.Equal(reason, exception.ReasonCode);
    }

    [Fact]
    public void Reader_RejectsCompressionBombsOversizeAndFutureSchemas()
    {
        var bomb = Path.Combine(_root, "bomb.zip");
        using (var archive = ZipFile.Open(bomb, ZipArchiveMode.Create))
        using (var stream = archive.CreateEntry(QreDiagnosticsStore.EventsFileName, CompressionLevel.SmallestSize).Open())
        {
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 20; i++)
            {
                stream.Write(zeros);
            }
        }
        Assert.Equal("bundle_compression_rejected", Assert.Throws<QreDiagnosticsInputException>(() => QreDiagnosticsReader.Load(bomb)).ReasonCode);

        var future = Path.Combine(_root, "diag-future");
        Directory.CreateDirectory(future);
        File.WriteAllText(Path.Combine(future, QreDiagnosticsStore.EventsFileName), Serialize(Record(1) with { SchemaVersion = "qre.outbound-diagnostics/9" }) + "\n");
        Assert.Equal("schema_unsupported", Assert.Throws<QreDiagnosticsInputException>(() => QreDiagnosticsReader.Load(future)).ReasonCode);

        var futureManifest = Path.Combine(_root, "diag-future-manifest");
        Directory.CreateDirectory(futureManifest);
        File.WriteAllText(Path.Combine(futureManifest, QreDiagnosticsStore.ManifestFileName), "{\"manifestSchema\":\"future/2\",\"eventSchema\":\"x\"}");
        Assert.Equal("schema_unsupported", Assert.Throws<QreDiagnosticsInputException>(() => QreDiagnosticsReader.Load(futureManifest)).ReasonCode);

        var oversize = Path.Combine(_root, "diag-oversize");
        Directory.CreateDirectory(oversize);
        using (var stream = File.Create(Path.Combine(oversize, QreDiagnosticsStore.ManifestFileName)))
        {
            stream.SetLength(QreDiagnosticsReader.MaxManifestBytes + 1);
        }
        Assert.Equal("input_too_large", Assert.Throws<QreDiagnosticsInputException>(() => QreDiagnosticsReader.Load(oversize)).ReasonCode);
    }

    [Fact]
    public async Task Export_KeepsCleanRecordsIntactAndDropsUnknownVocabulary()
    {
        var options = new QreOutboundDiagnosticsOptions { Mode = QreOutboundDiagnosticMode.Structure };
        var (diagnostics, store) = Open(options);
        await RunOneCallAsync(diagnostics);
        await store.CompleteAsync(diagnostics, Coverage);
        await store.DisposeAsync();
        var clean = QreDiagnosticsReader.Load(store.RunDirectory);

        var (records, dropped) = QreDiagnosticsExporter.Export(clean, Path.Combine(_root, "clean.zip"), force: false);
        Assert.Equal(clean.Records.Count, records);
        Assert.Equal(0, dropped);

        var tampered = clean with
        {
            Records = clean.Records.Select(static r => r.EventType == QreDiagnosticEventTypes.ModelCallEnded
                ? r with
                {
                    ProviderCategory = "tenant-SECRET-deployment",
                    ReasonCode = "Contains Spaces SECRET",
                    ModelOutcome = r.ModelOutcome! with { StopReason = "123", ErrorCode = "SECRET/path" }
                }
                : r).ToArray()
        };
        var bundle = Path.Combine(_root, "tampered.zip");
        var (_, tamperedDrops) = QreDiagnosticsExporter.Export(tampered, bundle, force: false);
        Assert.Equal(4, tamperedDrops);
        using var archive = ZipFile.OpenRead(bundle);
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var text = reader.ReadToEnd();
            Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"stopReason\":\"123\"", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task HandWrittenInputsWithOmittedOptionalFields_KeepSafeDefaults()
    {
        var fixturePath = Path.Combine(_root, "minimal.fixture.json");
        File.WriteAllText(fixturePath, """
            {"schema":"qre.sdk-rebuild-fixture/1","status":"runnable","apiMode":"chat_completions","model":"qwen3-next-80b",
             "request":{"messages":[{"role":"user","items":[{"kind":"text","text":"hi"}]}]},
             "assertions":[{"kind":"no_unexpected_change"}]}
            """);
        var fixture = QreDiagnosticsRebuilder.LoadFixture(fixturePath);
        Assert.Equal("qre-cli", fixture.OptionsMapping);
        Assert.Empty(fixture.Request.Tools);
        Assert.Empty(fixture.Missing);
        Assert.Equal("passed", (await QreDiagnosticsRebuilder.RebuildAsync(fixture, "minimal", null, Ct)).Result);

        var run = Path.Combine(_root, "diag-minimal-manifest");
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, QreDiagnosticsStore.ManifestFileName), $$$"""
            {"manifestSchema":"{{{QreDiagnosticsManifest.CurrentSchema}}}","eventSchema":"{{{QreOutboundDiagnosticSchema.SchemaVersion}}}",
             "projectionPolicyVersion":"{{{QreOutboundDiagnosticSchema.ProjectionPolicyVersion}}}","normalizerVersion":"{{{QreOutboundDiagnosticSchema.NormalizerVersion}}}",
             "diagnosticRunId":"x","segmentId":"s","mode":"metadata","completionStatus":"complete","createdUtc":"2026-01-01T00:00:00Z",
             "coverage":{"transportCapture":"handler","attemptCoverage":"handler_visible"},
             "quotas":{"maxRequestCaptureBytes":1,"maxRecordBytes":1,"maxRunBytes":1,"maxPendingRecords":1,"maxPendingBytes":1,"maxJsonDepth":1,"retentionDays":1}}
            """);
        var manifest = QreDiagnosticsReader.Load(run).Manifest!;
        Assert.Equal("local", manifest.Origin);
        Assert.Equal("client_side_handler", manifest.ObservationScope);
    }

    [Fact]
    public void Prune_AppliesRetentionAndRunCountOnlyInsideTheRoot()
    {
        var outside = Path.Combine(_root, "not-a-diagnostic-run");
        Directory.CreateDirectory(outside);
        var old = Path.Combine(_root, "diag-000-old");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "events.jsonl"), "{}");
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-10));
        var recent = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var path = Path.Combine(_root, $"diag-10{i}-recent");
            Directory.CreateDirectory(path);
            Directory.SetCreationTimeUtc(path, DateTime.UtcNow.AddMinutes(-10 + i));
            recent.Add(path);
        }

        QreDiagnosticsStore.Prune(_root, new QreOutboundDiagnosticsOptions { Retention = TimeSpan.FromDays(7), MaxStoredRuns = 3 });

        Assert.False(Directory.Exists(old));
        Assert.False(Directory.Exists(recent[0]));
        Assert.True(Directory.Exists(recent[1]));
        Assert.True(Directory.Exists(recent[2]));
        Assert.True(Directory.Exists(outside));
    }

    private (QreOutboundDiagnostics Diagnostics, QreDiagnosticsStore Store) Open(QreOutboundDiagnosticsOptions options)
    {
        var run = Path.Combine(_root, "diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        var sink = new ForwardingSink();
        var diagnostics = QreOutboundDiagnostics.Create(options, sink);
        var store = QreDiagnosticsStore.CreateAt(run, options, diagnostics.SegmentId, Coverage);
        sink.Target = store;
        return (diagnostics, store);
    }

    private static async Task<List<RuntimeModelStreamEvent>> RunOneCallAsync(QreOutboundDiagnostics diagnostics)
    {
        using var client = CreateModelClient(diagnostics, new DelegateHandler((_, _) =>
            Task.FromResult(Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("stored")))));
        return await DrainAsync(client.StreamAsync(Request(), new RuntimeModelAttemptContext(1), Ct), Ct);
    }

    private static QreOutboundDiagnosticRecord Record(long sequence)
        => QreOutboundDiagnostics.Create(new QreOutboundDiagnosticsOptions { Mode = QreOutboundDiagnosticMode.Metadata }, new InMemoryDiagnosticSink())
            .NewRecord(QreDiagnosticEventTypes.ModelCallStarted, QreDiagnosticObservationPoints.Runtime, null) with { Sequence = sequence };

    private static string Serialize(QreOutboundDiagnosticRecord record)
        => JsonSerializer.Serialize(record, QreOutboundDiagnosticsJsonContext.Default.QreOutboundDiagnosticRecord);

    private sealed class ForwardingSink : IQreOutboundDiagnosticSink
    {
        public IQreOutboundDiagnosticSink? Target { get; set; }

        public bool TryWrite(QreOutboundDiagnosticRecord record, ReadOnlyMemory<byte> utf8Json)
            => Target?.TryWrite(record, utf8Json) ?? false;
    }
}
