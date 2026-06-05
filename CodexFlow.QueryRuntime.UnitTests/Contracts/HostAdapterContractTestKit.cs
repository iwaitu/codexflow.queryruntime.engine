using System.Runtime.CompilerServices;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Experimental;
using Microsoft.Extensions.AI;
using Xunit;
using EngineModelRequest = CodexFlow.QueryRuntime.Engine.QueryRuntimeModelRequest;

namespace CodexFlow.QueryRuntime.UnitTests.Contracts;

/// <summary>
/// Reusable host-adapter assertions for downstream consumers that embed QRE as
/// an <see cref="IQueryRuntimeHostEngine"/>. The local tests run this suite
/// against <see cref="ExperimentalQueryRuntimeHarness"/> so the sample remains
/// compiled and executable in CI.
/// </summary>
internal static class HostAdapterContractTestKit
{
    public static async Task AssertPreToolHookBlocksWriteToolAsync(
        Func<IExperimentalModelClient, IQueryRuntimeHostEngine> createEngine,
        CancellationToken ct)
    {
        using var workspace = TemporaryWorkspace.Create();
        var model = new ScriptedModelClient(
            [new FunctionCallContent("call-write", "dangerous_write", new Dictionary<string, object?>())],
            [new TextContent("blocked handled")]);
        var engine = createEngine(model);
        var toolCalls = 0;
        var writeTool = AIFunctionFactory.Create(
            () =>
            {
                toolCalls++;
                return "write-complete";
            },
            new AIFunctionFactoryOptions { Name = "dangerous_write", Description = "Writes to the workspace." });
        var intervention = new RecordingToolIntervention(
            static _ => QueryRuntimeToolInterventionDecision.BlockWithFeedback(
                "Host policy blocked dangerous_write.",
                "writes require approval",
                "tool_blocked"));

        var result = await engine.RunAsync(
            new QueryRuntimeHostRequest
            {
                InitialMessages = [new ChatMessage(ChatRole.User, "modify the workspace")],
                WorkspacePath = workspace.Path,
                RunId = "contract-block-write",
                Tools = [writeTool],
                WriteToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dangerous_write" },
                ToolIntervention = intervention,
                Execution = new QueryRuntimeExecutionOptions { MaxRounds = 2 }
            },
            ct).ConfigureAwait(false);

        Assert.Equal("blocked handled", result.FinalText);
        Assert.Equal(0, toolCalls);
        Assert.Equal(0, result.TotalToolCalls);
        Assert.Equal(0, result.WriteToolCalls);
        Assert.Single(intervention.BeforeCalls);
        Assert.Empty(intervention.AfterCalls);
        Assert.Contains(
            model.Requests[1].Messages,
            message => message.Role == ChatRole.Tool &&
                       ReadText(message).Contains("Host policy blocked dangerous_write.", StringComparison.Ordinal));
    }

    public static async Task AssertStopGateRequiresContinuationAsync(
        Func<IExperimentalModelClient, IQueryRuntimeHostEngine> createEngine,
        CancellationToken ct)
    {
        using var workspace = TemporaryWorkspace.Create();
        var model = new ScriptedModelClient(
            [new TextContent("draft answer")],
            [new TextContent("verified answer")]);
        var engine = createEngine(model);
        var stopGate = new ScriptedStopGate(
            QueryRuntimeStopDecision.Continue(
                "Run verification before accepting this answer.",
                detailCode: "verification_incomplete"),
            QueryRuntimeStopDecision.Accept());

        var result = await engine.RunAsync(
            new QueryRuntimeHostRequest
            {
                InitialMessages = [new ChatMessage(ChatRole.User, "answer with evidence")],
                WorkspacePath = workspace.Path,
                RunId = "contract-stop-gate",
                EnableTools = false,
                StopGate = stopGate,
                Execution = new QueryRuntimeExecutionOptions
                {
                    MaxRounds = 2,
                    MaxStopGateContinuations = 1
                }
            },
            ct).ConfigureAwait(false);

        Assert.Equal("verified answer", result.FinalText);
        Assert.Equal(2, result.TotalRounds);
        Assert.Equal(1, result.ContinuationCount);
        Assert.Equal(2, stopGate.Calls.Count);
        Assert.Contains(
            model.Requests[1].Messages,
            message => message.Role == ChatRole.Assistant &&
                       ReadText(message) == "draft answer");
        Assert.Contains(
            model.Requests[1].Messages,
            message => message.Role == ChatRole.User &&
                       ReadText(message).Contains("Run verification", StringComparison.Ordinal));
    }

    public static async Task AssertRequiredToolContractTriggersContinuationAsync(
        Func<IExperimentalModelClient, IQueryRuntimeHostEngine> createEngine,
        CancellationToken ct)
    {
        using var workspace = TemporaryWorkspace.Create();
        var model = new ScriptedModelClient(
            [new TextContent("draft without verification")],
            [new FunctionCallContent("call-verify", "verify_state", new Dictionary<string, object?>())],
            [new TextContent("verified final")]);
        var engine = createEngine(model);
        var verifyCalls = 0;
        var verifyTool = AIFunctionFactory.Create(
            () =>
            {
                verifyCalls++;
                return "verified";
            },
            new AIFunctionFactoryOptions { Name = "verify_state", Description = "Verifies final state." });
        var stopGate = new ScriptedStopGate(
            QueryRuntimeStopDecision.RequireTool(
                "verify_state",
                "Call verify_state before final answer.",
                detailCode: "required_tool_missing"),
            QueryRuntimeStopDecision.Accept());

        var result = await engine.RunAsync(
            new QueryRuntimeHostRequest
            {
                InitialMessages = [new ChatMessage(ChatRole.User, "repair and verify")],
                WorkspacePath = workspace.Path,
                RunId = "contract-required-tool",
                Tools = [verifyTool],
                StopGate = stopGate,
                Execution = new QueryRuntimeExecutionOptions
                {
                    MaxRounds = 3,
                    MaxStopGateContinuations = 1
                }
            },
            ct).ConfigureAwait(false);

        Assert.Equal("verified final", result.FinalText);
        Assert.Equal(1, verifyCalls);
        Assert.Equal(1, result.TotalToolCalls);
        Assert.Equal(1, result.ContinuationCount);
        Assert.Equal("verify_state", result.LastFunctionCall);
        Assert.Equal("verify_state", result.RequiredToolName);
        Assert.True(result.RequiredToolSatisfied);
        Assert.Equal(ChatToolMode.RequireSpecific("verify_state"), model.Requests[1].Options?.ToolMode);
    }

    public static async Task AssertResultMetadataMapsHostSemanticsAsync(
        Func<IExperimentalModelClient, IQueryRuntimeHostEngine> createEngine,
        CancellationToken ct)
    {
        using var workspace = TemporaryWorkspace.Create();
        var model = new ScriptedModelClient(
            [
                new FunctionCallContent("call-read", "read_state", new Dictionary<string, object?>()),
                new FunctionCallContent("call-write", "write_state", new Dictionary<string, object?>())
            ],
            [new TextContent("metadata final")]);
        var engine = createEngine(model);
        var readTool = AIFunctionFactory.Create(
            () => "read-ok",
            new AIFunctionFactoryOptions { Name = "read_state", Description = "Reads state." });
        var writeTool = AIFunctionFactory.Create(
            () => "write-ok",
            new AIFunctionFactoryOptions { Name = "write_state", Description = "Writes state." });

        var result = await engine.RunAsync(
            new QueryRuntimeHostRequest
            {
                InitialMessages = [new ChatMessage(ChatRole.User, "collect metadata")],
                WorkspacePath = workspace.Path,
                RunId = "contract-metadata",
                Tools = [readTool, writeTool],
                WriteToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "write_state" },
                Execution = new QueryRuntimeExecutionOptions { MaxRounds = 2 }
            },
            ct).ConfigureAwait(false);

        Assert.Equal("metadata final", result.FinalText);
        Assert.Equal(2, result.TotalToolCalls);
        Assert.Equal(1, result.WriteToolCalls);
        Assert.Equal(["read_state", "write_state"], result.ExecutedToolNames);
        Assert.Equal(["read_state", "write_state"], result.SuccessfulToolNames);
        Assert.Equal("write_state", result.LastFunctionCall);
        Assert.Contains(result.FinalMessages, message => message.Role == ChatRole.Tool);
        Assert.Contains(
            result.FinalMessages,
            message => message.Role == ChatRole.Assistant &&
                       ReadText(message) == "metadata final");
        Assert.True(File.Exists(result.TraceFilePath));
        Assert.False(string.IsNullOrWhiteSpace(result.RunDirectory));
    }

    public static async Task AssertTracePathContainmentAsync(
        Func<IExperimentalModelClient, IQueryRuntimeHostEngine> createEngine,
        CancellationToken ct)
    {
        using var workspace = TemporaryWorkspace.Create();
        var engine = createEngine(new ScriptedModelClient([new TextContent("should not run")]));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await engine.RunAsync(
                new QueryRuntimeHostRequest
                {
                    InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                    WorkspacePath = workspace.Path,
                    RunId = "../escape",
                    Execution = new QueryRuntimeExecutionOptions { MaxRounds = 1 }
                },
                ct).ConfigureAwait(false));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.RunAsync(
                new QueryRuntimeHostRequest
                {
                    InitialMessages = [new ChatMessage(ChatRole.User, "test")],
                    WorkspacePath = workspace.Path,
                    TraceRoot = ".git/qre",
                    RunId = "safe-run",
                    Execution = new QueryRuntimeExecutionOptions { MaxRounds = 1 }
                },
                ct).ConfigureAwait(false));
    }

    private static string ReadText(ChatMessage message)
        => string.Concat(message.Contents.Select(static content => content switch
        {
            TextContent text => text.Text,
            FunctionResultContent result => result.Result?.ToString() ?? string.Empty,
            _ => string.Empty
        }));

    private sealed class ScriptedModelClient(params IReadOnlyList<AIContent>[] steps) : IExperimentalModelClient
    {
        private readonly Queue<IReadOnlyList<AIContent>> _steps = new(steps);

        public List<EngineModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            EngineModelRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new ChatResponseUpdate
            {
                Contents = _steps.Dequeue().ToList()
            };
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private sealed class RecordingToolIntervention(
        Func<QueryRuntimeToolCallContext, QueryRuntimeToolInterventionDecision> before)
        : IQueryRuntimeToolIntervention
    {
        public List<QueryRuntimeToolCallContext> BeforeCalls { get; } = [];

        public List<QueryRuntimeToolExecutionResultContext> AfterCalls { get; } = [];

        public ValueTask<QueryRuntimeToolInterventionDecision> BeforeToolCallAsync(
            QueryRuntimeToolCallContext context,
            CancellationToken ct = default)
        {
            BeforeCalls.Add(context);
            return ValueTask.FromResult(before(context));
        }

        public ValueTask AfterToolExecutionAsync(
            QueryRuntimeToolExecutionResultContext context,
            CancellationToken ct = default)
        {
            AfterCalls.Add(context);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedStopGate(params QueryRuntimeStopDecision[] decisions) : IQueryRuntimeStopGate
    {
        private readonly Queue<QueryRuntimeStopDecision> _decisions = new(decisions);

        public List<QueryRuntimeBeforeStopContext> Calls { get; } = [];

        public ValueTask<QueryRuntimeStopDecision> BeforeStopAsync(
            QueryRuntimeBeforeStopContext context,
            CancellationToken ct = default)
        {
            Calls.Add(context);
            return ValueTask.FromResult(_decisions.Count == 0
                ? QueryRuntimeStopDecision.Accept()
                : _decisions.Dequeue());
        }
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryWorkspace Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "codexflow-qre-host-contract-kit",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryWorkspace(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
