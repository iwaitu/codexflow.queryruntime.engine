using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Cli;

/// <summary>
/// CLI-level coverage that model-adapter selection failures surface as clear,
/// non-zero-exit errors before any provider call is attempted.
/// </summary>
public sealed class QreCliModelSelectionTests
{
    [Fact]
    public async Task Run_UnknownModel_FailsWithClearError()
    {
        using var workspace = TemporaryWorkspace.Create();

        var result = await CaptureConsoleAsync(() => QreCli.RunAsync(
            [
                "run",
                "--workspace", workspace.Path,
                "--api-url", "https://example.test/v1",
                "--api-key", "test-key",
                "--model", "totally-unknown-model",
                "hello"
            ],
            TestContext.Current.CancellationToken));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("No model adapter handles model 'totally-unknown-model'", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_UnsupportedApiModeForModel_FailsWithClearError()
    {
        using var workspace = TemporaryWorkspace.Create();

        var result = await CaptureConsoleAsync(() => QreCli.RunAsync(
            [
                "run",
                "--workspace", workspace.Path,
                "--api-url", "https://example.test/v1",
                "--api-key", "test-key",
                "--model", "gemini-2.5-pro",
                "--api-mode", "anthropic-messages",
                "hello"
            ],
            TestContext.Current.CancellationToken));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("does not support api-mode", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_UnknownApiModeValue_FailsWithClearError()
    {
        using var workspace = TemporaryWorkspace.Create();

        var result = await CaptureConsoleAsync(() => QreCli.RunAsync(
            [
                "run",
                "--workspace", workspace.Path,
                "--api-url", "https://example.test/v1",
                "--api-key", "test-key",
                "--model", "qwen3-next",
                "--api-mode", "grpc",
                "hello"
            ],
            TestContext.Current.CancellationToken));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unsupported QRE api mode value 'grpc'", result.StandardError, StringComparison.Ordinal);
    }

    private static async Task<CapturedConsole> CaptureConsoleAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exitCode = await action().ConfigureAwait(false);
            return new CapturedConsole(exitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record CapturedConsole(int ExitCode, string StandardOutput, string StandardError);

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(string path) => Path = path;

        public string Path { get; }

        public static TemporaryWorkspace Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"qre-models-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryWorkspace(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
