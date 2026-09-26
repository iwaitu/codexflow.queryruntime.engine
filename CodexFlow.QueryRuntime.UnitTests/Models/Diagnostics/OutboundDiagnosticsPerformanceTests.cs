using System.Diagnostics;
using CodexFlow.QueryRuntime.Cli.Diagnostics;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Models.Diagnostics;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;
using static CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics.DiagnosticsTestSupport;

namespace CodexFlow.QueryRuntime.UnitTests.Models.Diagnostics;

/// <summary>
/// P5 performance acceptance on a fixed, network-free workload (real SDK plus
/// in-memory transport). Metadata p95 overhead must stay within
/// max(1 ms, 5% of baseline p95); Structure within max(2 ms, 10%).
/// The median of three measured rounds is used after warm-up.
/// </summary>
[Trait("Category", "Performance")]
public sealed class OutboundDiagnosticsPerformanceTests
{
    private const int Warmup = 60;
    private const int Calls = 300;
    private const int Rounds = 3;

    [Fact]
    public async Task DiagnosticsOverhead_StaysWithinAcceptanceThresholds()
    {
        var output = TestContext.Current.TestOutputHelper;
        var request = Request(
            [Tool("qre_read_file"), Tool("qre_list_files"), Tool("qre_search")],
            new RuntimeModelParameters(Model: ChatModel, Temperature: 0.2, MaxOutputTokens: 512, RequireJsonObject: true, RequiredToolName: "qre_read_file"),
            text: new string('x', 4000));

        var baseline = new List<double>();
        var metadata = new List<double>();
        var structure = new List<double>();
        var allocations = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var round = 0; round < Rounds; round++)
        {
            baseline.Add(await MeasureP95Async(null, request, allocations, "off"));
            metadata.Add(await MeasureP95Async(QreOutboundDiagnosticMode.Metadata, request, allocations, "metadata"));
            structure.Add(await MeasureP95Async(QreOutboundDiagnosticMode.Structure, request, allocations, "structure"));
        }

        var baseP95 = Median(baseline);
        var metaOverhead = Median(metadata) - baseP95;
        var structureOverhead = Median(structure) - baseP95;
        output?.WriteLine($"baseline_p95_ms={baseP95:F3} metadata_p95_ms={Median(metadata):F3} structure_p95_ms={Median(structure):F3}");
        output?.WriteLine($"metadata_overhead_ms={metaOverhead:F3} structure_overhead_ms={structureOverhead:F3}");
        foreach (var (mode, bytes) in allocations)
        {
            output?.WriteLine($"alloc_bytes_per_call_{mode}={bytes / (Calls * Rounds):F0}");
        }

        Assert.True(metaOverhead <= Math.Max(1.0, baseP95 * 0.05), $"Metadata p95 overhead {metaOverhead:F3} ms exceeds threshold.");
        Assert.True(structureOverhead <= Math.Max(2.0, baseP95 * 0.10), $"Structure p95 overhead {structureOverhead:F3} ms exceeds threshold.");
    }

    private static async Task<double> MeasureP95Async(
        QreOutboundDiagnosticMode? mode,
        RuntimeModelRequest request,
        Dictionary<string, double> allocations,
        string label)
    {
        var sink = new InMemoryDiagnosticSink();
        var diagnostics = mode == null ? null : Create(sink, mode.Value);
        var transport = new DelegateHandler((_, _) =>
            Task.FromResult(Sse(QreModelApiMode.ChatCompletions, QreOfflineResponse.TextResponse("measured"))));
        using var client = CreateModelClient(diagnostics, transport);
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < Warmup; i++)
        {
            await DrainAsync(client.StreamAsync(request, new RuntimeModelAttemptContext(1), ct), ct);
        }
        var samples = new double[Calls];
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < Calls; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await DrainAsync(client.StreamAsync(request, new RuntimeModelAttemptContext(1), ct), ct);
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        allocations[label] = allocations.GetValueOrDefault(label) + (GC.GetTotalAllocatedBytes(precise: true) - before);
        Array.Sort(samples);
        return samples[(int)Math.Ceiling(samples.Length * 0.95) - 1];
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
