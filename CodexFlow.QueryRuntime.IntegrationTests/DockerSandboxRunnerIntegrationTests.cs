using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Sandbox.Docker;
using System.Diagnostics;
using Xunit;

namespace CodexFlow.QueryRuntime.IntegrationTests;

public sealed class DockerSandboxRunnerIntegrationTests
{
    private const string DockerImage = "alpine:3.20";

    [Fact]
    public async Task RunAsync_ReadOnlyMountPreventsWorkspaceWrites()
    {
        SkipUnlessEnabled();
        using var workspace = TemporaryWorkspace.Create();

        var result = await RunDockerAsync(
            workspace.Path,
            ["sh", "-c", "echo x > denied.txt"],
            SandboxMountPolicy.WorkspaceReadOnly);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "denied.txt")));
    }

    [Fact]
    public async Task RunAsync_DoesNotExposeCommonHostSecretLocations()
    {
        SkipUnlessEnabled();
        using var workspace = TemporaryWorkspace.Create();

        var result = await RunDockerAsync(
            workspace.Path,
            ["sh", "-c", "cat /root/.ssh/id_rsa /root/.aws/credentials 2>/dev/null || true"],
            SandboxMountPolicy.WorkspaceReadOnly);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("BEGIN OPENSSH PRIVATE KEY", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("aws_access_key_id", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrWhiteSpace(result.StandardOutput));
    }

    [Fact]
    public async Task RunAsync_DefaultNetworkDenyBlocksEgress()
    {
        SkipUnlessEnabled();
        using var workspace = TemporaryWorkspace.Create();

        var result = await RunDockerAsync(
            workspace.Path,
            ["wget", "-qO-", "https://example.com"],
            SandboxMountPolicy.WorkspaceReadOnly);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_EnforcesTimeoutAndCleansContainer()
    {
        SkipUnlessEnabled();
        using var workspace = TemporaryWorkspace.Create();

        var result = await RunDockerAsync(
            workspace.Path,
            ["sh", "-c", "sleep 10"],
            SandboxMountPolicy.WorkspaceReadOnly,
            timeout: TimeSpan.FromSeconds(1));

        Assert.Equal(-1, result.ExitCode);
        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_CancellationCleansContainer()
    {
        SkipUnlessEnabled();
        using var workspace = TemporaryWorkspace.Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var runner = new DockerSandboxRunner(new DockerSandboxOptions { Image = DockerImage });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await runner.RunAsync(
                new SandboxJobSpec
                {
                    Command = ["sh", "-c", "sleep 60"],
                    WorkingDirectory = workspace.Path,
                    Limits = new SandboxLimits
                    {
                        Timeout = TimeSpan.FromSeconds(90),
                        MaxOutputBytes = 64 * 1024,
                        MemoryBytes = 128L * 1024 * 1024,
                        CpuCount = 0.5
                    },
                    Network = SandboxNetworkPolicy.Deny,
                    Mounts = SandboxMountPolicy.WorkspaceReadOnly
                },
                cts.Token));

        var runningQreContainers = await RunHostProcessAsync(
            "docker",
            ["ps", "--format", "{{.Names}}", "--filter", "name=qre-sandbox-"],
            workspace.Path);
        Assert.Equal(0, runningQreContainers.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(runningQreContainers.StandardOutput));
    }

    [Fact]
    public async Task RunAsync_EnforcesOutputLimit()
    {
        SkipUnlessEnabled();
        using var workspace = TemporaryWorkspace.Create();

        var result = await RunDockerAsync(
            workspace.Path,
            ["sh", "-c", "yes x | head -c 2048"],
            SandboxMountPolicy.WorkspaceReadOnly,
            maxOutputBytes: 32);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(32, result.StandardOutput.Length);
    }

    [Fact]
    public async Task RunAsync_ReadWriteMountUsesStagedWorkspaceAndCopiesChangesBack()
    {
        SkipUnlessEnabled();
        using var workspace = TemporaryWorkspace.Create();
        File.WriteAllText(Path.Combine(workspace.Path, "input.txt"), "original");

        var result = await RunDockerAsync(
            workspace.Path,
            [
                "sh",
                "-c",
                "cat input.txt > output.txt && mkdir -p .qre && echo trace > .qre/should-not-copy.txt"
            ],
            SandboxMountPolicy.WorkspaceReadWrite);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("original", File.ReadAllText(Path.Combine(workspace.Path, "output.txt")));
        Assert.False(File.Exists(Path.Combine(workspace.Path, ".qre", "should-not-copy.txt")));
    }

    [Fact]
    public async Task RunAsync_ReadWriteStagingDoesNotFollowSymlinks()
    {
        SkipUnlessEnabled();
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Symlink staging test is not enabled on Windows.");
        }

        using var workspace = TemporaryWorkspace.Create();
        var outsideSecret = Path.Combine(workspace.Path, "..", Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outsideSecret, "host-secret", TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(Path.Combine(workspace.Path, "outside-link.txt"), outsideSecret);
        try
        {
            var result = await RunDockerAsync(
                workspace.Path,
                [
                    "sh",
                    "-c",
                    "test ! -e outside-link.txt && ln -s /etc/passwd leaked-passwd.txt"
                ],
                SandboxMountPolicy.WorkspaceReadWrite);

            Assert.Equal(0, result.ExitCode);
            Assert.False(File.Exists(Path.Combine(workspace.Path, "leaked-passwd.txt")));
        }
        finally
        {
            if (File.Exists(outsideSecret))
            {
                File.Delete(outsideSecret);
            }
        }
    }

    [Fact]
    public async Task RunAsync_MountsWorkspaceRootWhileExecutingInSubdirectory()
    {
        SkipUnlessEnabled();
        using var workspace = TemporaryWorkspace.Create();
        var appDirectory = Path.Combine(workspace.Path, "src", "app");
        Directory.CreateDirectory(appDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "root.txt"),
            "root-visible",
            TestContext.Current.CancellationToken);

        var runner = new DockerSandboxRunner(new DockerSandboxOptions { Image = DockerImage });
        var result = await runner.RunAsync(
            new SandboxJobSpec
            {
                Command = ["sh", "-c", "pwd && cat ../../root.txt"],
                WorkspaceRoot = workspace.Path,
                WorkingDirectory = appDirectory,
                Limits = new SandboxLimits
                {
                    Timeout = TimeSpan.FromSeconds(30),
                    MaxOutputBytes = 64 * 1024,
                    MemoryBytes = 128L * 1024 * 1024,
                    CpuCount = 0.5
                },
                Network = SandboxNetworkPolicy.Deny,
                Mounts = SandboxMountPolicy.WorkspaceReadOnly
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("/workspace/src/app", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("root-visible", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RunsAsNonRootWithNoNewPrivilegesAndNoCapabilities()
    {
        SkipUnlessEnabled();
        using var workspace = TemporaryWorkspace.Create();

        var result = await RunDockerAsync(
            workspace.Path,
            ["sh", "-c", "id -u; awk '/NoNewPrivs|CapEff/ {print}' /proc/self/status"],
            SandboxMountPolicy.WorkspaceReadOnly);

        Assert.Equal(0, result.ExitCode);
        var lines = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEqual("0", lines[0]);
        Assert.Contains(lines, line => string.Equals(line, "NoNewPrivs:\t1", StringComparison.Ordinal));
        Assert.Contains(lines, line => string.Equals(line, "CapEff:\t0000000000000000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_UsesReadOnlyRootFilesystemWithTmpfsScratch()
    {
        SkipUnlessEnabled();
        using var workspace = TemporaryWorkspace.Create();

        var result = await RunDockerAsync(
            workspace.Path,
            ["sh", "-c", "touch /qre-root-denied 2>/dev/null; denied=$?; touch /tmp/qre-tmp-allowed; echo denied=$denied tmp=$?"],
            SandboxMountPolicy.WorkspaceReadOnly);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("denied=1 tmp=0", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CustomSeccompProfileIsEnforcedByDocker()
    {
        SkipUnlessEnabled();
        using var workspace = TemporaryWorkspace.Create();
        var profilePath = Path.Combine(workspace.Path, "deny-write-seccomp.json");
        File.WriteAllText(profilePath, BuildDenyWriteSeccompProfile());

        var result = await RunDockerAsync(
            workspace.Path,
            ["sh", "-c", "echo should-not-print"],
            SandboxMountPolicy.WorkspaceReadOnly,
            options: new DockerSandboxOptions
            {
                Image = DockerImage,
                SeccompProfilePath = profilePath
            });

        Assert.NotEqual(0, result.ExitCode);
    }

    private static async Task<SandboxResult> RunDockerAsync(
        string workspacePath,
        IReadOnlyList<string> command,
        SandboxMountPolicy mount,
        TimeSpan? timeout = null,
        int maxOutputBytes = 64 * 1024,
        DockerSandboxOptions? options = null)
    {
        var runner = new DockerSandboxRunner(options ?? new DockerSandboxOptions { Image = DockerImage });
        return await runner.RunAsync(
            new SandboxJobSpec
            {
                Command = command,
                WorkingDirectory = workspacePath,
                Limits = new SandboxLimits
                {
                    Timeout = timeout ?? TimeSpan.FromSeconds(30),
                    MaxOutputBytes = maxOutputBytes,
                    MemoryBytes = 128L * 1024 * 1024,
                    CpuCount = 0.5
                },
                Network = SandboxNetworkPolicy.Deny,
                Mounts = mount
            },
            TestContext.Current.CancellationToken);
    }

    private static string BuildDenyWriteSeccompProfile()
        => """
           {
             "defaultAction": "SCMP_ACT_ALLOW",
             "architectures": [
               "SCMP_ARCH_X86_64",
               "SCMP_ARCH_X86",
               "SCMP_ARCH_X32",
               "SCMP_ARCH_AARCH64",
               "SCMP_ARCH_ARM"
             ],
             "syscalls": [
               {
                 "names": [ "write" ],
                 "action": "SCMP_ACT_ERRNO"
               }
             ]
           }
           """;

    private static async Task<SandboxResult> RunHostProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new SandboxResult(process.ExitCode, stdout, stderr, false, 0);
    }

    private static void SkipUnlessEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_QUERY_RUNTIME_DOCKER_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("Set RUN_QUERY_RUNTIME_DOCKER_TESTS=true to enable Docker sandbox integration tests.");
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
                "codexflow-qre-docker",
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
