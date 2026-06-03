using CodexFlow.QueryRuntime.Experimental;
using Qre = CodexFlow.QueryRuntime.Abstractions;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Abstractions;

public sealed class QueryRuntimeContractTests
{
    [Fact]
    public async Task StableEngineContract_RunsThroughExperimentalHarness()
    {
        using var workspace = TemporaryWorkspace.Create();
        Qre.IQueryRuntimeEngine engine = new ExperimentalQueryRuntimeHarness(
            new StaticExperimentalModelClient("stable contract response"));

        var result = await engine.RunAsync(
            new Qre.QueryRuntimeRequest
            {
                Prompt = "explain the workspace",
                WorkspacePath = workspace.Path,
                RunId = "stable-contract-run",
                Execution = new Qre.QueryRuntimeExecutionOptions { MaxRounds = 1 }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("stable-contract-run", result.RunId);
        Assert.Equal("stable contract response", result.FinalText);
        Assert.True(File.Exists(result.TraceFilePath));
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
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codexflow-qre-contract-tests", Guid.NewGuid().ToString("N"));
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
