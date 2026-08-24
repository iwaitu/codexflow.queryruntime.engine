using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class RuntimeToolPipelineTests
{
    [Fact]
    public void Registry_RejectsCaseInsensitiveCanonicalNameCollision()
    {
        var error = Assert.Throws<ArgumentException>(() => new RuntimeToolRegistry(
        [
            Tool("read_file", "1.0.0"),
            Tool("read_file", "2.0.0")
        ]));

        Assert.Contains("collision", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ReadFile")]
    [InlineData("read file")]
    [InlineData("_read_file")]
    public void Registry_RejectsNonCanonicalNames(string name)
    {
        Assert.Throws<ArgumentException>(() => new RuntimeToolRegistry([Tool(name)]));
    }

    [Fact]
    public void ArgumentNormalizer_ValidatesSchemaAndProducesStableDigest()
    {
        var schema = Json("""
            {
              "type":"object",
              "properties":{
                "path":{"type":"string"},
                "max":{"type":"integer"}
              },
              "required":["path"],
              "additionalProperties":false
            }
            """);

        var first = RuntimeToolArgumentNormalizer.NormalizeAndValidate(
            schema,
            Json("{" + "\"max\":3,\"path\":\"a.txt\"}"));
        var second = RuntimeToolArgumentNormalizer.NormalizeAndValidate(
            schema,
            Json("{" + "\"path\":\"a.txt\",\"max\":3}"));

        Assert.Equal(first.Sha256Digest, second.Sha256Digest);
        Assert.Equal("{\"max\":3,\"path\":\"a.txt\"}", first.Value.GetRawText());
    }

    [Fact]
    public async Task Pipeline_UnknownAndMalformedCallsReturnStructuredDeniedObservations()
    {
        var pipeline = Pipeline([Tool("read_file", schema: ObjectSchema(required: "path"))]);
        var context = Context();

        var unknown = await pipeline.PrepareAsync(
            Call("1", "missing", "{}"), context, TestContext.Current.CancellationToken);
        var malformed = await pipeline.PrepareAsync(
            Call("2", "read_file", "{}"), context, TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeToolPreparationKind.Denied, unknown.Kind);
        Assert.Equal(RuntimeErrorCategory.UnknownTool, unknown.Observation!.Error!.Category);
        Assert.Equal(RuntimeToolOutcome.Denied, unknown.Observation.Details!.Outcome);
        Assert.Equal(RuntimeErrorCategory.MalformedToolArguments, malformed.Observation!.Error!.Category);
        Assert.Equal("tool_argument_required_missing", malformed.Observation.Error.Code);
    }

    [Fact]
    public async Task Pipeline_BindsImmutableApprovalPlanToArgumentsWorkspacePolicyAndSandbox()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero));
        var policy = new DelegatePolicy(_ => new RuntimeToolPolicyDecision(
            RuntimeToolPolicyDecisionKind.RequireApproval,
            "write requires approval",
            new HashSet<string>(["write_fs"], StringComparer.Ordinal),
            new RuntimeSandboxRequirements(
                RuntimeSandboxKind.Docker,
                RuntimeNetworkMode.Deny,
                RuntimeWorkspaceMountMode.ReadWrite),
            TimeSpan.FromMinutes(2)));
        var pipeline = Pipeline(
            [Tool("write_file", sideEffect: RuntimeToolSideEffect.WorkspaceWrite,
                sandbox: new RuntimeSandboxRequirements(RuntimeSandboxKind.Docker, RuntimeNetworkMode.Deny, RuntimeWorkspaceMountMode.ReadWrite),
                capabilities: new HashSet<string>(["write_fs"], StringComparer.Ordinal))],
            policy,
            new RuntimeSandboxRouter([new RecordingSandbox(RuntimeSandboxKind.Docker)]),
            clock,
            Sequence("attempt-1", "approval-1"));

        var prepared = await pipeline.PrepareAsync(
            Call("call-1", "write_file", "{\"path\":\"a.txt\"}"),
            Context(workspace: "workspace-123", policyVersion: "policy-7", profile: "repair"),
            TestContext.Current.CancellationToken);

        var plan = Assert.IsType<ResolvedExecutionPlan>(prepared.Plan);
        Assert.Equal("write_file", plan.ToolCanonicalName);
        Assert.Equal("1.0.0", plan.ToolVersion);
        Assert.Equal("workspace-123", plan.WorkspaceIdentity);
        Assert.Equal("policy-7", plan.PolicyVersion);
        Assert.Equal(RuntimeSandboxKind.Docker, plan.Sandbox.Kind);
        Assert.Equal("attempt-1", plan.AttemptId);
        Assert.Equal("approval-1", plan.Approval!.Nonce);
        Assert.Equal(clock.GetUtcNow().AddMinutes(2), plan.Approval.ExpiresAt);
        Assert.Contains(plan.NormalizedArgumentsDigest, plan.Approval.Scope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pipeline_ReevaluationCreatesNewAttemptAndApprovalBinding()
    {
        var pipeline = Pipeline(
            [Tool("write_file")],
            new DelegatePolicy(_ => RuntimeToolPolicyDecision.RequireApproval("approve")),
            nonceFactory: Sequence("attempt-1", "approval-1", "attempt-2", "approval-2"));
        var call = Call("call", "write_file", "{}");

        var first = await pipeline.PrepareAsync(
            call, Context(), TestContext.Current.CancellationToken);
        var second = await pipeline.PrepareAsync(
            call, Context(), TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Plan!.AttemptId, second.Plan!.AttemptId);
        Assert.NotEqual(first.Plan.Approval!.Nonce, second.Plan.Approval!.Nonce);
    }

    [Fact]
    public async Task RegistryAndPlan_DefensivelyFreezeToolCapabilities()
    {
        var capabilities = new HashSet<string>(["read_fs"], StringComparer.Ordinal);
        var pipeline = Pipeline([Tool("read_file", capabilities: capabilities)]);
        capabilities.Add("write_fs");

        var prepared = await pipeline.PrepareAsync(
            Call("call", "read_file", "{}"),
            Context(),
            TestContext.Current.CancellationToken);

        Assert.Equal(["read_fs"], prepared.Plan!.EffectiveCapabilities);
    }

    [Fact]
    public async Task Pipeline_MissingSandboxFailsClosedBeforeToolInvocation()
    {
        var tool = Tool(
            "verify",
            sandbox: new RuntimeSandboxRequirements(
                RuntimeSandboxKind.LocalProcess,
                RuntimeNetworkMode.Deny,
                RuntimeWorkspaceMountMode.ReadOnly));
        var pipeline = Pipeline([tool]);

        var prepared = await pipeline.PrepareAsync(
            Call("1", "verify", "{}"), Context(), TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeToolPreparationKind.Denied, prepared.Kind);
        Assert.Equal("sandbox_unavailable", prepared.Observation!.Error!.Code);
        Assert.Equal(0, tool.Invocations);
    }

    [Fact]
    public async Task Pipeline_ExecutesWithSelectedSandboxAndStructuredResult()
    {
        var sandbox = new RecordingSandbox(RuntimeSandboxKind.LocalProcess);
        var tool = Tool(
            "verify",
            sandbox: new RuntimeSandboxRequirements(
                RuntimeSandboxKind.LocalProcess,
                RuntimeNetworkMode.Deny,
                RuntimeWorkspaceMountMode.ReadOnly),
            invoke: async (invocation, context, ct) =>
            {
                var result = await context.Sandbox!.ExecuteAsync(
                    new RuntimeSandboxCommand(
                        ["dotnet", "test", "--no-restore"],
                        ".",
                        ".",
                        new Dictionary<string, string>(),
                        invocation.Plan.Limits,
                        invocation.Plan.Sandbox.Network,
                        invocation.Plan.Sandbox.WorkspaceMount),
                    ct);
                return new RuntimeToolResult(
                    invocation.OriginalCall.InvocationId,
                    result.StandardOutput,
                    result.ExitCode == 0,
                    Details: new RuntimeToolResultDetails(
                        RuntimeToolOutcome.Succeeded,
                        result.StandardOutput,
                        result.StandardError,
                        result.ExitCode,
                        result.DurationMs,
                        result.Truncated));
            });
        var pipeline = Pipeline(
            [tool],
            sandboxes: new RuntimeSandboxRouter([sandbox]));
        var prepared = await pipeline.PrepareAsync(
            Call("1", "verify", "{}"), Context(), TestContext.Current.CancellationToken);

        var result = await pipeline.ExecuteAsync(
            prepared, Context(), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("verified", result.Text);
        Assert.Equal(0, result.Details!.ExitCode);
        Assert.Single(sandbox.Commands);
        Assert.Equal(1, tool.Invocations);
    }

    [Fact]
    public async Task Pipeline_EnforcesTimeoutAndUtf8OutputBudget()
    {
        var bounded = Pipeline([Tool(
            "bounded",
            limits: new RuntimeToolLimits(TimeSpan.FromSeconds(1), 5),
            invoke: (invocation, _, _) => ValueTask.FromResult(new RuntimeToolResult(
                invocation.OriginalCall.InvocationId,
                "123456789",
                true))) ]);
        var boundedPrepared = await bounded.PrepareAsync(
            Call("bounded", "bounded", "{}"), Context(), TestContext.Current.CancellationToken);
        var boundedResult = await bounded.ExecuteAsync(
            boundedPrepared, Context(), TestContext.Current.CancellationToken);

        Assert.Equal("12345", boundedResult.Text);
        Assert.True(boundedResult.Details!.Truncated);

        var timed = Pipeline([Tool(
            "timed",
            limits: new RuntimeToolLimits(TimeSpan.FromMilliseconds(10), 100),
            invoke: async (invocation, _, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Success(invocation.OriginalCall.InvocationId.Value, "unreachable");
            })]);
        var timedPrepared = await timed.PrepareAsync(
            Call("timed", "timed", "{}"), Context(), TestContext.Current.CancellationToken);
        var timedResult = await timed.ExecuteAsync(
            timedPrepared, Context(), TestContext.Current.CancellationToken);

        Assert.False(timedResult.Success);
        Assert.Equal(RuntimeErrorCategory.SandboxTimeout, timedResult.Error!.Category);
        Assert.Equal(RuntimeToolOutcome.TimedOut, timedResult.Details!.Outcome);
    }

    [Fact]
    public async Task Scheduler_RunsParallelSafeBatchConcurrentlyButReturnsModelOrder()
    {
        var active = 0;
        var maxActive = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<RuntimeToolResult> Execute(string id, int delay, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, current);
            if (current == 2)
            {
                gate.TrySetResult();
            }
            await gate.Task.WaitAsync(ct);
            await Task.Delay(delay, ct);
            Interlocked.Decrement(ref active);
            return Success(id, id);
        }

        var results = await new RuntimeToolScheduler().ExecuteAsync(
        [
            new(RuntimeToolConcurrency.ParallelSafe, ct => Execute("first", 20, ct)),
            new(RuntimeToolConcurrency.ParallelSafe, ct => Execute("second", 1, ct))
        ], TestContext.Current.CancellationToken);

        Assert.Equal(2, maxActive);
        Assert.Equal(["first", "second"], results.Select(static result => result.InvocationId.Value));
    }

    [Fact]
    public async Task Scheduler_SerialAndExclusiveWorkspaceNeverOverlap()
    {
        var order = new ConcurrentQueue<string>();
        async ValueTask<RuntimeToolResult> Execute(string id, CancellationToken ct)
        {
            order.Enqueue("start-" + id);
            await Task.Yield();
            order.Enqueue("end-" + id);
            return Success(id, id);
        }

        await new RuntimeToolScheduler().ExecuteAsync(
        [
            new(RuntimeToolConcurrency.Serial, ct => Execute("serial", ct)),
            new(RuntimeToolConcurrency.ExclusiveWorkspace, ct => Execute("exclusive", ct))
        ], TestContext.Current.CancellationToken);

        Assert.Equal(
            ["start-serial", "end-serial", "start-exclusive", "end-exclusive"],
            order);
    }

    [Fact]
    public async Task Scheduler_ExclusiveWorkspaceSerializesConcurrentTurnsForSameWorkspace()
    {
        var active = 0;
        var maxActive = 0;
        async ValueTask<RuntimeToolResult> Execute(string id, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, current);
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref active);
            return Success(id, id);
        }
        var first = new RuntimeToolScheduler().ExecuteAsync(
        [
            new(
                RuntimeToolConcurrency.ExclusiveWorkspace,
                ct => Execute("first", ct),
                "write_file",
                "workspace-a")
        ], TestContext.Current.CancellationToken);
        var second = new RuntimeToolScheduler().ExecuteAsync(
        [
            new(
                RuntimeToolConcurrency.ExclusiveWorkspace,
                ct => Execute("second", ct),
                "apply_patch",
                "workspace-a")
        ], TestContext.Current.CancellationToken);

        await Task.WhenAll(first, second);

        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task AgentRuntime_PipelineCommitsParallelObservationsAndPresentationEventsInModelOrder()
    {
        var completions = new ConcurrentQueue<string>();
        TestTool ParallelTool(string name, int delay) => Tool(
            name,
            concurrency: RuntimeToolConcurrency.ParallelSafe,
            invoke: async (invocation, _, ct) =>
            {
                await Task.Delay(delay, ct);
                completions.Enqueue(invocation.OriginalCall.InvocationId.Value);
                return Success(invocation.OriginalCall.InvocationId.Value, name);
            });
        var pipeline = Pipeline([ParallelTool("first_tool", 30), ParallelTool("second_tool", 1)]);
        var model = new QueueModelClient(
        [
            [
                new RuntimeToolCallEvent(Call("first", "first_tool", "{}")),
                new RuntimeToolCallEvent(Call("second", "second_tool", "{}")),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)
            ],
            [
                new RuntimeTextDeltaEvent("done"),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)
            ]
        ]);
        var sink = new RecordingEventSink();
        var request = new RuntimeAgentLoopRequest(
            new RuntimeSessionId("session"),
            new RuntimeTurnId("turn"),
            "test",
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("test")])],
            pipeline.Descriptors,
            new RuntimeModelParameters(),
            new RuntimePolicySnapshot("c4", "readonly"),
            new RuntimeEnvironmentSnapshot("local", "workspace", "capabilities"),
            new RuntimeBudgetSnapshot(3, 4))
        {
            ToolPipeline = pipeline
        };

        var result = await new AgentRuntime(model).RunAsync(
            new RuntimeRunRequest(request),
            sink,
            TestContext.Current.CancellationToken);

        Assert.Equal(["second", "first"], completions);
        var observations = result.History
            .Where(static message => message.Role == RuntimeMessageRole.Tool)
            .SelectMany(static message => message.Items)
            .OfType<RuntimeToolResultItem>()
            .Select(static item => item.Result.InvocationId.Value);
        Assert.Equal(["first", "second"], observations);
        Assert.Equal(
            ["first", "second"],
            sink.Events
                .Where(static item => item.Type == RuntimePresentationEventType.ToolExecutionCompleted)
                .Select(static item => item.InvocationId!.Value.Value));
    }

    [Fact]
    public async Task AgentRuntime_ApprovalReceivesAndAuthorizesExactFrozenPlan()
    {
        var pipeline = Pipeline(
            [Tool(
                "write_file",
                sideEffect: RuntimeToolSideEffect.WorkspaceWrite,
                sandbox: new RuntimeSandboxRequirements(
                    RuntimeSandboxKind.None,
                    RuntimeNetworkMode.Deny,
                    RuntimeWorkspaceMountMode.ReadWrite))],
            new DelegatePolicy(_ => RuntimeToolPolicyDecision.RequireApproval("approve")),
            nonceFactory: Sequence("attempt-1", "approval-1"));
        var approval = new RecordingApproval();
        var model = new QueueModelClient(
        [
            [
                new RuntimeToolCallEvent(Call("write-1", "write_file", "{}")),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)
            ],
            [
                new RuntimeTextDeltaEvent("done"),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)
            ]
        ]);
        var request = new RuntimeAgentLoopRequest(
            new RuntimeSessionId("session"),
            new RuntimeTurnId("turn"),
            "test",
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("test")])],
            pipeline.Descriptors,
            new RuntimeModelParameters(),
            new RuntimePolicySnapshot("policy-7", "repair"),
            new RuntimeEnvironmentSnapshot("local", "workspace-123", "capabilities"),
            new RuntimeBudgetSnapshot(3, 2))
        {
            ToolPipeline = pipeline,
            ToolApproval = approval
        };

        var result = await new AgentRuntime(model).RunAsync(
            new RuntimeRunRequest(request),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        var plan = Assert.IsType<ResolvedExecutionPlan>(approval.Plan);
        Assert.Equal("attempt-1", plan.AttemptId);
        Assert.Equal("approval-1", plan.Approval!.Nonce);
        Assert.Equal("workspace-123", plan.WorkspaceIdentity);
        Assert.Equal("policy-7", plan.PolicyVersion);
        Assert.Equal("write-1", approval.Call!.InvocationId.Value);
    }

    [Fact]
    public async Task AgentRuntime_PipelineDoesNotUseInvocationOnlyTurnHandleForApproval()
    {
        var pipeline = Pipeline(
            [Tool(
                "write_file",
                sideEffect: RuntimeToolSideEffect.WorkspaceWrite,
                sandbox: new RuntimeSandboxRequirements(
                    RuntimeSandboxKind.None,
                    RuntimeNetworkMode.Deny,
                    RuntimeWorkspaceMountMode.ReadWrite))],
            new DelegatePolicy(_ => RuntimeToolPolicyDecision.RequireApproval("approve")));
        var model = new QueueModelClient(
        [[
            new RuntimeToolCallEvent(Call("write-1", "write_file", "{}")),
            new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)
        ]]);
        using var handle = new RuntimeTurnHandle();
        handle.Approve(new RuntimeInvocationId("write-1"));
        var request = new RuntimeAgentLoopRequest(
            new RuntimeSessionId("session"),
            new RuntimeTurnId("turn"),
            "test",
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("test")])],
            pipeline.Descriptors,
            new RuntimeModelParameters(),
            new RuntimePolicySnapshot("policy", "repair"),
            new RuntimeEnvironmentSnapshot("local", "workspace", "capabilities"),
            new RuntimeBudgetSnapshot(1, 1))
        {
            ToolPipeline = pipeline
        };

        var result = await new AgentRuntime(model).RunAsync(
            new RuntimeRunRequest(request) { Handle = handle },
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeToolInvocationStatus.Denied, result.Turn.Steps[0].ToolInvocations![0].Status);
        Assert.Equal("bound_approval_unavailable", result.Turn.Steps[0].ToolInvocations![0].Result!.Error!.Code);
    }

    [Fact]
    public async Task AgentRuntime_C5CatalogRejectsRegisteredToolThatWasNotExposedInStep()
    {
        var visible = Tool("visible_tool");
        var hidden = Tool("hidden_tool");
        var pipeline = Pipeline([visible, hidden]);
        var model = new QueueModelClient(
        [
            [
                new RuntimeToolCallEvent(Call("hidden-1", "hidden_tool", "{}")),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall)
            ],
            [
                new RuntimeTextDeltaEvent("handled"),
                new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn)
            ]
        ]);
        var request = new RuntimeAgentLoopRequest(
            new RuntimeSessionId("session"),
            new RuntimeTurnId("turn"),
            "test",
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("test")])],
            pipeline.Descriptors,
            new RuntimeModelParameters(),
            new RuntimePolicySnapshot("c5", "readonly"),
            new RuntimeEnvironmentSnapshot("local", "workspace", "c5"),
            new RuntimeBudgetSnapshot(3, 2))
        {
            ToolPipeline = pipeline,
            ToolCatalogSelector = new FixedCatalogSelector("visible_tool")
        };

        var result = await new AgentRuntime(model).RunAsync(
            new RuntimeRunRequest(request),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(0, hidden.Invocations);
        Assert.Equal(
            "tool_not_exposed_in_context",
            result.Turn.Steps[0].ToolInvocations![0].Result!.Error!.Code);
    }

    private static RuntimeToolExecutionPipeline Pipeline(
        IReadOnlyList<TestTool> tools,
        IRuntimeToolPolicyEvaluator? policy = null,
        IRuntimeSandboxRouter? sandboxes = null,
        TimeProvider? clock = null,
        Func<string>? nonceFactory = null)
        => new(
            new RuntimeToolRegistry(tools),
            policy ?? new RuntimeAllowToolPolicy(),
            sandboxes,
            clock,
            nonceFactory);

    private static TestTool Tool(
        string name,
        string version = "1.0.0",
        JsonElement? schema = null,
        RuntimeToolSideEffect sideEffect = RuntimeToolSideEffect.ReadOnly,
        RuntimeSandboxRequirements? sandbox = null,
        IReadOnlySet<string>? capabilities = null,
        RuntimeToolConcurrency concurrency = RuntimeToolConcurrency.Serial,
        RuntimeToolLimits? limits = null,
        Func<RuntimeToolInvocation, RuntimeToolExecutionContext, CancellationToken, ValueTask<RuntimeToolResult>>? invoke = null)
        => new(
            new RuntimeToolDefinition(
                new RuntimeToolDescriptor(
                    name,
                    version,
                    name,
                    schema ?? ObjectSchema(),
                    sideEffect,
                    RuntimeToolIdempotency.Idempotent),
                capabilities ?? new HashSet<string>(StringComparer.Ordinal),
                concurrency,
                sandbox ?? RuntimeSandboxRequirements.None,
                limits ?? RuntimeToolLimits.Default),
            invoke);

    private static RuntimeToolExecutionContext Context(
        string workspace = "workspace",
        string policyVersion = "policy-1",
        string profile = "readonly")
        => new(
            new RuntimeSessionId("session"),
            new RuntimeTurnId("turn"),
            new RuntimeStepId("step"),
            new RuntimePolicySnapshot(policyVersion, profile),
            new RuntimeEnvironmentSnapshot("local", workspace, "capabilities"),
            new RuntimeBudgetSnapshot(3, 5));

    private static RuntimeToolCall Call(string id, string name, string arguments)
        => new(new RuntimeInvocationId(id), name, Json(arguments));

    private static RuntimeToolResult Success(string id, string text)
        => new(new RuntimeInvocationId(id), text, true,
            Details: new RuntimeToolResultDetails(RuntimeToolOutcome.Succeeded));

    private static JsonElement ObjectSchema(string? required = null)
        => Json(required == null
            ? "{\"type\":\"object\",\"additionalProperties\":true}"
            : $"{{\"type\":\"object\",\"properties\":{{\"{required}\":{{\"type\":\"string\"}}}},\"required\":[\"{required}\"],\"additionalProperties\":false}}");

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static Func<string> Sequence(params string[] values)
    {
        var index = -1;
        return () => values[Interlocked.Increment(ref index)];
    }

    private sealed class TestTool(
        RuntimeToolDefinition definition,
        Func<RuntimeToolInvocation, RuntimeToolExecutionContext, CancellationToken, ValueTask<RuntimeToolResult>>? invoke = null)
        : IRuntimeTool
    {
        private int _invocations;

        public RuntimeToolDefinition Definition { get; } = definition;

        public int Invocations => Volatile.Read(ref _invocations);

        public async ValueTask<RuntimeToolResult> InvokeAsync(
            RuntimeToolInvocation invocation,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _invocations);
            return invoke == null
                ? Success(invocation.OriginalCall.InvocationId.Value, "ok")
                : await invoke(invocation, context, ct);
        }
    }

    private sealed class DelegatePolicy(Func<RuntimeToolPolicyContext, RuntimeToolPolicyDecision> evaluate)
        : IRuntimeToolPolicyEvaluator
    {
        public ValueTask<RuntimeToolPolicyDecision> EvaluateAsync(RuntimeToolPolicyContext context, CancellationToken ct)
            => ValueTask.FromResult(evaluate(context));
    }

    private sealed class RecordingSandbox(RuntimeSandboxKind kind) : IRuntimeSandbox
    {
        public RuntimeSandboxKind Kind { get; } = kind;

        public List<RuntimeSandboxCommand> Commands { get; } = [];

        public ValueTask<RuntimeSandboxResult> ExecuteAsync(RuntimeSandboxCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            return ValueTask.FromResult(new RuntimeSandboxResult(0, "verified", string.Empty, false, 12));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class QueueModelClient(
        IReadOnlyList<IReadOnlyList<RuntimeModelStreamEvent>> responses) : IRuntimeModelClient
    {
        private int _index;

        public async IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
            RuntimeModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in responses[Interlocked.Increment(ref _index) - 1])
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class RecordingEventSink : IRuntimeEventSink
    {
        public List<RuntimePresentationEvent> Events { get; } = [];

        public ValueTask OnEventAsync(RuntimePresentationEvent runtimeEvent, CancellationToken ct)
        {
            Events.Add(runtimeEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingApproval : IRuntimeToolApproval
    {
        public ResolvedExecutionPlan? Plan { get; private set; }

        public RuntimeToolCall? Call { get; private set; }

        public ValueTask<RuntimeToolApprovalDecision> DecideAsync(
            ResolvedExecutionPlan plan,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
        {
            Plan = plan;
            Call = call;
            return ValueTask.FromResult(RuntimeToolApprovalDecision.Approve("test approved exact plan"));
        }
    }

    private sealed class FixedCatalogSelector(params string[] names) : IRuntimeToolCatalogSelector
    {
        private readonly HashSet<string> _names = new(names, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<RuntimeToolDescriptor> SelectTools(
            PreparedRuntimeContext context,
            IReadOnlyList<RuntimeToolDescriptor> frozenCatalog,
            int stepIndex)
            => frozenCatalog.Where(tool => _names.Contains(tool.CanonicalName)).ToArray();

        public void Observe(RuntimeToolCall call, RuntimeToolResult result)
        {
        }
    }
}
