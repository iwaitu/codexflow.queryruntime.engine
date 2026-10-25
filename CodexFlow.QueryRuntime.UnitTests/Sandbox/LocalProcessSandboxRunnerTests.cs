using CodexFlow.QueryRuntime.Sandbox.LocalProcess;
using CodexFlow.QueryRuntime.UnitTests.Infrastructure;
using System.Diagnostics;
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
                    Command = TestProcess.Command("environment"),
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
                Command = TestProcess.Command("environment"),
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
                Command = TestProcess.Command("sleep"),
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
    public async Task RunAsync_CallerCancellationWaitsForProcessExitBeforeWorkspaceCleanup()
    {
        using var workspace = TemporaryWorkspace.Create();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = new LocalProcessSandboxRunner().RunAsync(new Qre.SandboxJobSpec
        {
            Command = TestProcess.Command("sleep", "ready.pid"), WorkingDirectory = workspace.Path,
            Limits = new Qre.SandboxLimits { Timeout = TimeSpan.FromSeconds(30), MaxOutputBytes = 4096 }
        }, cancel.Token);
        try
        {
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            ready.CancelAfter(TimeSpan.FromSeconds(10));
            var pidFile = System.IO.Path.Combine(workspace.Path, "ready.pid");
            int pid;
            while (!File.Exists(pidFile) || !int.TryParse(await File.ReadAllTextAsync(pidFile, ready.Token), out pid))
                await Task.Delay(10, ready.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
            try
            {
                using var process = Process.GetProcessById(pid);
                Assert.True(process.HasExited, "The runner returned before its fixture process exited.");
            }
            catch (ArgumentException) { /* PID is already gone. */ }
        }
        finally
        {
            cancel.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
        // Disposal immediately deletes the process working directory on Windows.
    }

    [Fact]
    public async Task RunAsync_TruncatesOutputByMaxOutputBytes()
    {
        using var workspace = TemporaryWorkspace.Create();

        var result = await new LocalProcessSandboxRunner().RunAsync(
            new Qre.SandboxJobSpec
            {
                Command = TestProcess.Command("print", "1234567890"),
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
                // Windows directory deletion can race transient handles even
                // after the runner confirmed process exit. Retry only sharing/
                // lock violations, bounded to one second; persistent locks fail.
                var cleanup = Stopwatch.StartNew();
                var delay = 10;
                for (;;)
                {
                    try
                    {
                        Directory.Delete(Path, recursive: true);
                        return;
                    }
                    catch (IOException error) when (OperatingSystem.IsWindows() &&
                        (error.HResult & 0xffff) is 32 or 33 && cleanup.Elapsed < TimeSpan.FromSeconds(1))
                    {
                        Thread.Sleep(delay);
                        delay = Math.Min(delay * 2, 100);
                    }
                }
            }
        }
    }
}
