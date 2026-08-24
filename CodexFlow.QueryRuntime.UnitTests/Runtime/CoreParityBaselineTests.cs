using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;
using Qre = CodexFlow.QueryRuntime.Engine;
using QreV2 = CodexFlow.QueryRuntime.Engine.V2;
using QreProtocol = CodexFlow.QueryRuntime.Protocol;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

[CollectionDefinition("CoreParityBaseline", DisableParallelization = true)]
public sealed class CoreParityBaselineCollection;

[Collection("CoreParityBaseline")]
public sealed class CoreParityBaselineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(25)]
    public async Task V1ToolHeavyTrajectory_RecordsRepeatableScaleBaseline(int steps)
    {
        _ = await RunSampleAsync(steps);
        var samples = new List<BaselineSample>();
        for (var iteration = 0; iteration < 5; iteration++)
        {
            samples.Add(await RunSampleAsync(steps));
        }

        var elapsed = samples.Select(static sample => sample.ElapsedMs).Order().ToArray();
        var allocated = samples.Select(static sample => sample.AllocatedBytes).Order().ToArray();
        var representative = samples[^1];
        using var process = Process.GetCurrentProcess();
        output.WriteLine(
            "steps={0}; samples=5; elapsed_median_ms={1:F3}; elapsed_max_ms={2:F3}; allocated_median_bytes={3}; allocated_max_bytes={4}; event_count={5}; event_projection_bytes={6}; process_peak_working_set_bytes={7}",
            steps,
            elapsed[elapsed.Length / 2],
            elapsed[^1],
            allocated[allocated.Length / 2],
            allocated[^1],
            representative.EventCount,
            representative.ProjectionBytes,
            process.PeakWorkingSet64);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(25)]
    public async Task C5ContextTrajectory_RecordsBoundedDeterministicProjection(int steps)
    {
        _ = await RunC5SampleAsync(steps);
        var samples = new List<C5BaselineSample>();
        for (var iteration = 0; iteration < 5; iteration++)
        {
            samples.Add(await RunC5SampleAsync(steps));
        }

        var elapsed = samples.Select(static sample => sample.ElapsedMs).Order().ToArray();
        var allocated = samples.Select(static sample => sample.AllocatedBytes).Order().ToArray();
        var representative = samples[^1];
        output.WriteLine(
            "c5_steps={0}; samples=5; elapsed_median_ms={1:F3}; elapsed_max_ms={2:F3}; allocated_median_bytes={3}; allocated_max_bytes={4}; canonical_messages={5}; context_preparations={6}; compactions={7}; max_prepared_tokens={8}; history_blob_bytes={9}",
            steps,
            elapsed[elapsed.Length / 2],
            elapsed[^1],
            allocated[allocated.Length / 2],
            allocated[^1],
            representative.CanonicalMessages,
            representative.ContextPreparations,
            representative.Compactions,
            representative.MaxPreparedTokens,
            representative.HistoryBlobBytes);
    }

    private static async Task<BaselineSample> RunSampleAsync(int steps)
    {
        var model = new ToolHeavyScriptedModel(steps);
        var engine = new Qre.QueryRuntimeEngine(model);
        var sink = new MeasuringEventSink();
        var tool = AIFunctionFactory.Create(
            () => "ok",
            new AIFunctionFactoryOptions { Name = "baseline_read", Description = "Synthetic readonly baseline tool." });
        var request = new Qre.QueryRuntimeRequest
        {
            SessionId = $"baseline-{steps}",
            InitialMessages = [new ChatMessage(ChatRole.User, "inspect")],
            MaxRounds = steps,
            EnableTools = true,
            AvailableTools = [tool]
        };

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var stopwatch = Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(
            request,
            sink,
            $"baseline-{steps}",
            Path.Combine(Path.GetTempPath(), $"baseline-{steps}", "events.jsonl"),
            workspacePath: null,
            TestContext.Current.CancellationToken);
        stopwatch.Stop();
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;

        Assert.Equal(steps, result.TotalRounds);
        Assert.Equal(steps - 1, result.TotalToolCalls);
        Assert.Equal("done", result.FinalText);
        return new BaselineSample(
            stopwatch.Elapsed.TotalMilliseconds,
            allocatedBytes,
            sink.EventCount,
            sink.ProjectionBytes);
    }

    private static async Task<C5BaselineSample> RunC5SampleAsync(int steps)
    {
        var model = new V2ToolHeavyScriptedModel(steps);
        var tool = new V2BaselineTool();
        var pipeline = new QreV2.RuntimeToolExecutionPipeline(
            new QreV2.RuntimeToolRegistry([tool]),
            new QreV2.RuntimeAllowToolPolicy());
        var options = new QreV2.RuntimeContextOptions
        {
            MaxContextTokens = 512,
            MaxItemTokens = 160,
            MaxToolResultTokens = 120,
            LargeToolResultTokens = 80,
            SummaryTokens = 96,
            RecentTrajectoryMessages = 6,
            MaxBlobBytes = 4_096,
            MaxTotalBlobBytes = 32_768
        };
        var request = new QreV2.RuntimeAgentLoopRequest(
            new QreProtocol.RuntimeSessionId($"c5-baseline-{steps}"),
            new QreProtocol.RuntimeTurnId($"c5-turn-{steps}"),
            "measure deterministic context",
            [new QreProtocol.RuntimeMessage(
                QreProtocol.RuntimeMessageRole.User,
                [new QreProtocol.RuntimeTextItem("inspect")])],
            pipeline.Descriptors,
            new QreProtocol.RuntimeModelParameters(),
            new QreV2.RuntimePolicySnapshot("c5", "readonly"),
            new QreV2.RuntimeEnvironmentSnapshot("local", "synthetic", "c5-baseline"),
            new QreV2.RuntimeBudgetSnapshot(steps, Math.Max(0, steps - 1)))
        {
            ToolPipeline = pipeline,
            ContextManager = new QreV2.RuntimeContextManager(options)
        };

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var stopwatch = Stopwatch.StartNew();
        var result = await new QreV2.AgentRuntime(model).RunAsync(
            new QreV2.RuntimeRunRequest(request),
            null,
            TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.Equal(QreV2.RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("done", result.FinalText);
        Assert.All(result.LoopResult.PreparedContexts,
            context => Assert.True(context.EstimatedTokens <= options.MaxContextTokens));
        return new C5BaselineSample(
            stopwatch.Elapsed.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore,
            result.History.Count,
            result.LoopResult.PreparedContexts.Count,
            result.LoopResult.PreparedContexts.Count(static context => context.Compacted),
            result.LoopResult.PreparedContexts.Max(static context => context.EstimatedTokens),
            result.LoopResult.HistoryBlobs.Values.Sum(static blob => (long)blob.Data.Length));
    }

    private sealed class ToolHeavyScriptedModel(int totalSteps) : Qre.IQueryRuntimeModelClient
    {
        private int _step;

        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            Qre.QueryRuntimeModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var current = _step++;
            yield return current < totalSteps - 1
                ? new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent($"call-{current}", "baseline_read", new Dictionary<string, object?>())])
                : new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")]);
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private sealed class V2ToolHeavyScriptedModel(int totalSteps) : QreProtocol.IRuntimeModelClient
    {
        private int _step;

        public async IAsyncEnumerable<QreProtocol.RuntimeModelStreamEvent> StreamAsync(
            QreProtocol.RuntimeModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var current = _step++;
            if (current < totalSteps - 1)
            {
                yield return new QreProtocol.RuntimeToolCallEvent(new QreProtocol.RuntimeToolCall(
                    new QreProtocol.RuntimeInvocationId($"call-{current}"),
                    "baseline_read",
                    JsonSerializer.SerializeToElement(new { step = current })));
                yield return new QreProtocol.RuntimeModelCompletedEvent(QreProtocol.RuntimeModelStopReason.ToolCall);
            }
            else
            {
                yield return new QreProtocol.RuntimeTextDeltaEvent("done");
                yield return new QreProtocol.RuntimeModelCompletedEvent(QreProtocol.RuntimeModelStopReason.EndTurn);
            }
            await Task.CompletedTask;
        }
    }

    private sealed class V2BaselineTool : QreV2.IRuntimeTool
    {
        public QreV2.RuntimeToolDefinition Definition { get; } = new(
            new QreProtocol.RuntimeToolDescriptor(
                "baseline_read",
                "1.0.0",
                "Synthetic C5 baseline read.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new { step = new { type = "integer" } },
                    required = new[] { "step" },
                    additionalProperties = false
                }),
                QreProtocol.RuntimeToolSideEffect.ReadOnly,
                QreProtocol.RuntimeToolIdempotency.Idempotent),
            new HashSet<string>(StringComparer.Ordinal),
            QreV2.RuntimeToolConcurrency.Serial,
            QreV2.RuntimeSandboxRequirements.None,
            new QreV2.RuntimeToolLimits(TimeSpan.FromSeconds(1), 8_192));

        public ValueTask<QreProtocol.RuntimeToolResult> InvokeAsync(
            QreV2.RuntimeToolInvocation invocation,
            QreV2.RuntimeToolExecutionContext context,
            CancellationToken ct)
            => ValueTask.FromResult(new QreProtocol.RuntimeToolResult(
                invocation.OriginalCall.InvocationId,
                "synthetic finding " + new string('f', 600),
                true,
                Details: new QreProtocol.RuntimeToolResultDetails(QreProtocol.RuntimeToolOutcome.Succeeded)));
    }

    private sealed class MeasuringEventSink : Qre.IQueryRuntimeEventSink
    {
        public int EventCount { get; private set; }

        public long ProjectionBytes { get; private set; }

        public bool IsEnabled(Qre.QueryRuntimeEventType eventType) => true;

        public ValueTask OnEventAsync(Qre.QueryRuntimeEvent runtimeEvent)
        {
            EventCount++;
            ProjectionBytes += Encoding.UTF8.GetByteCount(
                JsonSerializer.Serialize(runtimeEvent, runtimeEvent.GetType()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record BaselineSample(
        double ElapsedMs,
        long AllocatedBytes,
        int EventCount,
        long ProjectionBytes);

    private sealed record C5BaselineSample(
        double ElapsedMs,
        long AllocatedBytes,
        int CanonicalMessages,
        int ContextPreparations,
        int Compactions,
        int MaxPreparedTokens,
        long HistoryBlobBytes);
}
