using CodexFlow.QueryRuntime.Sandbox.LocalProcess;
using Qre = CodexFlow.QueryRuntime.Abstractions;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Sandbox;

public sealed class LocalProcessSandboxRunnerTests
{
    [Fact]
    public async Task RunAsync_ExecutesTrustedLocalProcess()
    {
        using var workspace = TemporaryWorkspace.Create();
        var runner = new LocalProcessSandboxRunner();

        var result = await runner.RunAsync(
            new Qre.SandboxJobSpec
            {
                Command = ["dotnet", "--version"],
                WorkingDirectory = workspace.Path,
                Limits = new Qre.SandboxLimits
                {
                    Timeout = TimeSpan.FromSeconds(30),
                    MaxOutputBytes = 1024
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
    }

    [Fact]
    public async Task RunAsync_DoesNotInheritHostEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create();
        var variableName = $"QRE_SANDBOX_SECRET_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variableName, "host-secret");
        try
        {
            var result = await new LocalProcessSandboxRunner().RunAsync(
                new Qre.SandboxJobSpec
                {
                    Command = ["/usr/bin/env"],
                    WorkingDirectory = workspace.Path,
                    Limits = new Qre.SandboxLimits
                    {
                        Timeout = TimeSpan.FromSeconds(30),
                        MaxOutputBytes = 4096
                    }
                },
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain(variableName, result.StandardOutput);
            Assert.DoesNotContain("host-secret", result.StandardOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [Fact]
    public async Task RunAsync_InjectsOnlyExplicitEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create();
        var result = await new LocalProcessSandboxRunner().RunAsync(
            new Qre.SandboxJobSpec
            {
                Command = ["/usr/bin/env"],
                WorkingDirectory = workspace.Path,
                Environment = new Dictionary<string, string>
                {
                    ["QRE_ALLOWED_VALUE"] = "explicit-value"
                },
                Limits = new Qre.SandboxLimits
                {
                    Timeout = TimeSpan.FromSeconds(30),
                    MaxOutputBytes = 4096
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("QRE_ALLOWED_VALUE=explicit-value", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_RejectsNetworkAllow()
    {
        using var workspace = TemporaryWorkspace.Create();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new LocalProcessSandboxRunner().RunAsync(
                new Qre.SandboxJobSpec
                {
                    Command = ["dotnet", "--version"],
                    WorkingDirectory = workspace.Path,
                    Network = Qre.SandboxNetworkPolicy.Allow
                },
                TestContext.Current.CancellationToken));

        Assert.Contains("does not support network-allowed jobs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReturnsCleanFailure_WhenExecutableCannotStart()
    {
        using var workspace = TemporaryWorkspace.Create();

        var result = await new LocalProcessSandboxRunner().RunAsync(
            new Qre.SandboxJobSpec
            {
                Command = [$"qre-missing-command-{Guid.NewGuid():N}"],
                WorkingDirectory = workspace.Path,
                Limits = new Qre.SandboxLimits
                {
                    Timeout = TimeSpan.FromSeconds(10),
                    MaxOutputBytes = 4096
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(127, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("Failed to start sandbox command", result.StandardError);
    }

    [Fact]
    public async Task RunAsync_TimesOutAndKillsProcess()
    {
        using var workspace = TemporaryWorkspace.Create();

        var result = await new LocalProcessSandboxRunner().RunAsync(
            new Qre.SandboxJobSpec
            {
                Command = LongRunningCommand(),
                WorkingDirectory = workspace.Path,
                Limits = new Qre.SandboxLimits
                {
                    Timeout = TimeSpan.FromMilliseconds(300),
                    MaxOutputBytes = 4096
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_TruncatesOutputByMaxOutputBytes()
    {
        using var workspace = TemporaryWorkspace.Create();

        var result = await new LocalProcessSandboxRunner().RunAsync(
            new Qre.SandboxJobSpec
            {
                Command = PrintCommand("1234567890"),
                WorkingDirectory = workspace.Path,
                Limits = new Qre.SandboxLimits
                {
                    Timeout = TimeSpan.FromSeconds(10),
                    MaxOutputBytes = 5
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("12345", result.StandardOutput);
    }

    private static IReadOnlyList<string> LongRunningCommand()
        => OperatingSystem.IsWindows()
            ? ["cmd.exe", "/c", "ping -n 6 127.0.0.1 > nul"]
            : ["/bin/sh", "-c", "sleep 5"];

    private static IReadOnlyList<string> PrintCommand(string value)
        => OperatingSystem.IsWindows()
            ? ["cmd.exe", "/c", $"<nul set /p={value}"]
            : ["/bin/sh", "-c", $"printf {value}"];

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryWorkspace Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codexflow-qre-sandbox-tests", Guid.NewGuid().ToString("N"));
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
