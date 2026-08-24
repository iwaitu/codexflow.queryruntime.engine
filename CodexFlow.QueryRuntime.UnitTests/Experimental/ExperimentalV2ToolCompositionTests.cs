using System.Text.Json;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Experimental;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Experimental;

public sealed class ExperimentalV2ToolCompositionTests
{
    [Fact]
    public void ReadonlyProfile_ExposesOnlyFrozenExecutableCatalog()
    {
        using var workspace = TemporaryWorkspace.Create();

        var pipeline = ExperimentalV2ToolComposition.Create(
            QueryRuntimeToolProfile.ReadOnly,
            workspace.Path);

        Assert.Equal(
            ["qre_list_files", "qre_read_file", "qre_search_files"],
            pipeline.Descriptors.Select(static descriptor => descriptor.CanonicalName));
        Assert.All(pipeline.Descriptors, static descriptor =>
            Assert.Equal(RuntimeToolSideEffect.ReadOnly, descriptor.SideEffect));
    }

    [Fact]
    public async Task RepairProfile_BindsWriteToolToApprovalAndReadWritePlan()
    {
        using var workspace = TemporaryWorkspace.Create();
        var pipeline = ExperimentalV2ToolComposition.Create(
            QueryRuntimeToolProfile.Repair,
            workspace.Path);

        var prepared = await pipeline.PrepareAsync(
            Call("qre_write_file", "{\"path\":\"result.txt\",\"content\":\"ok\"}"),
            Context(workspace.Path, "repair"),
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeToolPreparationKind.Ready, prepared.Kind);
        Assert.True(prepared.RequiresApproval);
        Assert.Equal(RuntimeWorkspaceMountMode.ReadWrite, prepared.Plan!.Sandbox.WorkspaceMount);
        Assert.Equal(RuntimeToolConcurrency.ExclusiveWorkspace, prepared.Plan.Concurrency);
    }

    [Fact]
    public async Task VerifyProfile_BindsProcessToolToSelectedSandbox()
    {
        using var workspace = TemporaryWorkspace.Create();
        var runner = new RecordingSandboxRunner();
        var pipeline = ExperimentalV2ToolComposition.Create(
            QueryRuntimeToolProfile.Verify,
            workspace.Path,
            runner,
            RuntimeSandboxKind.Docker);

        var prepared = await pipeline.PrepareAsync(
            Call("qre_git_status", "{}"),
            Context(workspace.Path, "verify"),
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeToolPreparationKind.Ready, prepared.Kind);
        Assert.Equal(RuntimeSandboxKind.Docker, prepared.Plan!.Sandbox.Kind);
        Assert.Equal(RuntimeWorkspaceMountMode.ReadOnly, prepared.Plan.Sandbox.WorkspaceMount);

        var result = await pipeline.ExecuteAsync(
            prepared,
            Context(workspace.Path, "verify"),
            TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Equal(RuntimeToolOutcome.Succeeded, result.Details!.Outcome);
        Assert.Single(runner.Jobs);
        Assert.Equal(["git", "status", "--short"], runner.Jobs[0].Command);
    }

    [Fact]
    public async Task ReadonlyProfile_MalformedArgumentsBecomeObservationBeforeInvocation()
    {
        using var workspace = TemporaryWorkspace.Create();
        var pipeline = ExperimentalV2ToolComposition.Create(
            QueryRuntimeToolProfile.ReadOnly,
            workspace.Path);

        var prepared = await pipeline.PrepareAsync(
            Call("qre_read_file", "{}"),
            Context(workspace.Path, "readonly"),
            TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeToolPreparationKind.Denied, prepared.Kind);
        Assert.Equal(RuntimeErrorCategory.MalformedToolArguments, prepared.Observation!.Error!.Category);
    }

    [Fact]
    public async Task C5DeferredCatalog_ExposesSearchThenActivatesSelectedFrozenTool()
    {
        using var workspace = TemporaryWorkspace.Create();
        var composition = ExperimentalV2ToolComposition.CreateRuntime(
            QueryRuntimeToolProfile.ReadOnly,
            workspace.Path,
            toolSearch: new QueryRuntimeToolSearchOptions { Enabled = true });
        var context = new RuntimeContextManager().Prepare(
            RuntimeHistory.Create(
                [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("read a file")])],
                0).Snapshot(),
            "read a file",
            null);

        var firstCatalog = composition.ToolCatalogSelector!.SelectTools(
            context,
            composition.Pipeline.Descriptors,
            0);
        Assert.Equal(["tool_search"], firstCatalog.Select(static tool => tool.CanonicalName));

        var prepared = await composition.Pipeline.PrepareAsync(
            Call("tool_search", "{\"query\":\"select:qre_read_file\"}"),
            Context(workspace.Path, "readonly"),
            TestContext.Current.CancellationToken);
        var observation = await composition.Pipeline.ExecuteAsync(
            prepared,
            Context(workspace.Path, "readonly"),
            TestContext.Current.CancellationToken);
        Assert.True(observation.Success, observation.Error?.Message ?? observation.Text);
        composition.ToolCatalogSelector.Observe(prepared.Call, observation);

        var secondCatalog = composition.ToolCatalogSelector.SelectTools(
            context,
            composition.Pipeline.Descriptors,
            1);
        Assert.Equal(
            ["tool_search", "qre_read_file"],
            secondCatalog.Select(static tool => tool.CanonicalName));
        Assert.Contains("qre_read_file", observation.Text, StringComparison.Ordinal);
    }

    private static RuntimeToolExecutionContext Context(string workspace, string profile)
        => new(
            new RuntimeSessionId("session"),
            new RuntimeTurnId("turn"),
            new RuntimeStepId("step"),
            new RuntimePolicySnapshot("c4", profile),
            new RuntimeEnvironmentSnapshot("local", workspace, "capabilities"),
            new RuntimeBudgetSnapshot(3, 5));

    private static RuntimeToolCall Call(string name, string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new RuntimeToolCall(
            new RuntimeInvocationId("invocation"),
            name,
            document.RootElement.Clone());
    }

    private sealed class RecordingSandboxRunner : ISandboxRunner
    {
        public List<SandboxJobSpec> Jobs { get; } = [];

        public Task<SandboxResult> RunAsync(SandboxJobSpec spec, CancellationToken ct = default)
        {
            Jobs.Add(spec);
            return Task.FromResult(new SandboxResult(0, string.Empty, string.Empty, false, 1));
        }
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(string path) => Path = path;

        public string Path { get; }

        public static TemporaryWorkspace Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "qre-c4-tests-" + Guid.NewGuid().ToString("N"));
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
