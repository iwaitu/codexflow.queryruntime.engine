using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Experimental;
using Microsoft.Extensions.AI;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Experimental;

public sealed class ExperimentalVerifyToolPackTests
{
    [Fact]
    public async Task VerifyTool_DeniedByPolicy_DoesNotRunSandboxCommand()
    {
        using var workspace = TemporaryWorkspace.Create();
        var runner = new RecordingSandboxRunner();
        var tools = ExperimentalVerifyToolPack.Create(
            workspace.Path,
            runner,
            new DenyAllCapabilityPolicy());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(tools, "qre_git_status", new()
            {
                ["max_output_chars"] = 1_000
            }));

        Assert.Contains("Capability policy Deny", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task VerifyTool_AllowedByPolicy_RunsSandboxCommand()
    {
        using var workspace = TemporaryWorkspace.Create();
        var runner = new RecordingSandboxRunner();
        var tools = ExperimentalVerifyToolPack.Create(
            workspace.Path,
            runner,
            new ExperimentalCapabilityPolicy());

        var result = await InvokeAsync(tools, "qre_git_status", new()
        {
            ["max_output_chars"] = 1_000
        });

        Assert.Equal(1, runner.Calls);
        Assert.Equal(["git", "status", "--short"], runner.LastSpec?.Command);
        Assert.Contains("exit_code: 0", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyTool_NonZeroExit_IsReportedAsToolFailure()
    {
        using var workspace = TemporaryWorkspace.Create();
        var runner = new RecordingSandboxRunner
        {
            Result = new SandboxResult(1, string.Empty, "compile failed", false, 12)
        };
        var tools = ExperimentalVerifyToolPack.Create(
            workspace.Path,
            runner,
            new ExperimentalCapabilityPolicy());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(tools, "qre_dotnet_build", new()
            {
                ["target"] = string.Empty,
                ["timeout_seconds"] = 30,
                ["max_output_chars"] = 1_000
            }));

        Assert.Contains("exit_code: 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("compile failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyTool_InjectsTrustedLocalEnvironmentWithoutHostSecrets()
    {
        using var workspace = TemporaryWorkspace.Create();
        var secretName = $"QRE_VERIFY_TOOL_SECRET_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(secretName, "host-secret");
        try
        {
            var runner = new RecordingSandboxRunner();
            var tools = ExperimentalVerifyToolPack.Create(
                workspace.Path,
                runner,
                new ExperimentalCapabilityPolicy());

            await InvokeAsync(tools, "qre_dotnet_build", new()
            {
                ["target"] = string.Empty,
                ["timeout_seconds"] = 30,
                ["max_output_chars"] = 1_000
            });

            Assert.NotNull(runner.LastSpec);
            Assert.DoesNotContain(secretName, runner.LastSpec.Environment.Keys);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PATH")))
            {
                Assert.True(runner.LastSpec.Environment.ContainsKey("PATH"));
            }
            foreach (var name in new[] { "APPDATA", "LOCALAPPDATA" })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    Assert.Equal(value, runner.LastSpec.Environment[name]);
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, null);
        }
    }

    [Fact]
    public async Task VerifyTool_RejectsWorkspacePathTraversalBeforeRunningSandboxCommand()
    {
        using var workspace = TemporaryWorkspace.Create();
        var runner = new RecordingSandboxRunner();
        var tools = ExperimentalVerifyToolPack.Create(
            workspace.Path,
            runner,
            new ExperimentalCapabilityPolicy());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InvokeAsync(tools, "qre_git_diff", new()
            {
                ["path"] = "../outside.txt",
                ["max_output_chars"] = 1_000
            }));

        Assert.Contains("outside workspace", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.Calls);
    }

    private static async Task<string> InvokeAsync(
        IReadOnlyList<AIFunction> tools,
        string name,
        Dictionary<string, object?> arguments)
    {
        var tool = tools.Single(tool => tool.Name == name);
        var result = await tool.InvokeAsync(new AIFunctionArguments(arguments), TestContext.Current.CancellationToken);
        return result?.ToString() ?? string.Empty;
    }

    private sealed class DenyAllCapabilityPolicy : IQueryRuntimeCapabilityPolicy
    {
        public QueryRuntimeCapabilityDecision Evaluate(QueryRuntimeCapabilityRequest request)
            => QueryRuntimeCapabilityDecision.Deny("test denied");
    }

    private sealed class RecordingSandboxRunner : ISandboxRunner
    {
        public int Calls { get; private set; }

        public SandboxJobSpec? LastSpec { get; private set; }

        public SandboxResult Result { get; init; } =
            new(0, "runner-ok", string.Empty, false, 12);

        public Task<SandboxResult> RunAsync(SandboxJobSpec spec, CancellationToken ct = default)
        {
            Calls++;
            LastSpec = spec;
            return Task.FromResult(Result);
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
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codexflow-qre-verify-tools", Guid.NewGuid().ToString("N"));
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
