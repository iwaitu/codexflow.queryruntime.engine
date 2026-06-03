using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Sandbox.Docker;
using System.Text.Json;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Sandbox;

public sealed class DockerSandboxRunnerTests
{
    [Fact]
    public void BuildDockerRunCommand_DefaultsToNoNetworkAndReadOnlyWorkspaceMount()
    {
        var command = DockerSandboxRunner.BuildDockerRunCommand(
            new DockerSandboxOptions { Image = "qre-test:latest" },
            new SandboxJobSpec
            {
                Command = ["rg", "TODO", "."],
                WorkingDirectory = "/repo",
                Environment = new Dictionary<string, string>
                {
                    ["PATH"] = "/usr/bin"
                },
                Limits = new SandboxLimits
                {
                    Timeout = TimeSpan.FromSeconds(15),
                    MaxOutputBytes = 4096,
                    MemoryBytes = 128L * 1024 * 1024,
                    CpuCount = 0.5
                },
                Network = SandboxNetworkPolicy.Deny,
                Mounts = SandboxMountPolicy.WorkspaceReadOnly
            },
            "/repo",
            "qre-sandbox-test");

        Assert.Equal("docker", command[0]);
        Assert.Contains("--network", command);
        Assert.Contains("none", command);
        Assert.Contains("type=bind,src=/repo,dst=/workspace,readonly", command);
        Assert.Contains("--user", command);
        Assert.Contains("65532:65532", command);
        Assert.Contains("--cap-drop", command);
        Assert.Contains("ALL", command);
        Assert.Contains("--security-opt", command);
        Assert.Contains("no-new-privileges", command);
        Assert.DoesNotContain(command, argument => argument.StartsWith("seccomp=", StringComparison.Ordinal));
        Assert.Contains("--read-only", command);
        Assert.Contains("--tmpfs", command);
        Assert.Contains("/tmp:rw,noexec,nosuid,size=64m", command);
        Assert.Contains("134217728b", command);
        Assert.Contains("0.5", command);
        Assert.Contains("qre-test:latest", command);
        Assert.True(new DockerSandboxOptions().CopyWorkspaceForWriteJobs);
        Assert.Equal(["qre-test:latest", "rg", "TODO", "."], command.Skip(command.Count - 4).ToArray());
    }

    [Fact]
    public void BuildDockerRunCommand_UsesReadWriteWorkspaceMountWhenRequested()
    {
        var command = DockerSandboxRunner.BuildDockerRunCommand(
            new DockerSandboxOptions(),
            new SandboxJobSpec
            {
                Command = ["dotnet", "build", "--no-restore"],
                WorkingDirectory = "/repo",
                Mounts = SandboxMountPolicy.WorkspaceReadWrite
            },
            "/repo",
            "qre-sandbox-test");

        Assert.Contains("type=bind,src=/repo,dst=/workspace", command);
        Assert.DoesNotContain("type=bind,src=/repo,dst=/workspace,readonly", command);
    }

    [Fact]
    public void BuildDockerRunCommand_CanUseContainerSubdirectoryWorkdir()
    {
        var command = DockerSandboxRunner.BuildDockerRunCommand(
            new DockerSandboxOptions(),
            new SandboxJobSpec
            {
                Command = ["pwd"],
                WorkingDirectory = "/repo/src/app",
                WorkspaceRoot = "/repo"
            },
            "/repo",
            "qre-sandbox-test",
            "/workspace/src/app");

        var workdirIndex = command.ToList().IndexOf("--workdir");
        Assert.True(workdirIndex >= 0);
        Assert.Equal("/workspace/src/app", command[workdirIndex + 1]);
        Assert.Contains("type=bind,src=/repo,dst=/workspace,readonly", command);
    }

    [Fact]
    public void BuildDockerRunCommand_AddsSeccompProfileWhenConfigured()
    {
        var profilePath = DockerSandboxSeccompProfile.ResolveBundledProfilePath();
        Assert.False(string.IsNullOrWhiteSpace(profilePath));

        var command = DockerSandboxRunner.BuildDockerRunCommand(
            new DockerSandboxOptions
            {
                SeccompProfilePath = profilePath
            },
            new SandboxJobSpec
            {
                Command = ["true"],
                WorkingDirectory = "/repo"
            },
            "/repo",
            "qre-sandbox-test");

        Assert.Contains("seccomp=" + Path.GetFullPath(profilePath!), command);
    }

    [Fact]
    public void BuildDockerRunCommand_FailsWhenRequiredSeccompProfileIsMissing()
    {
        Assert.Throws<FileNotFoundException>(() =>
            DockerSandboxRunner.BuildDockerRunCommand(
                new DockerSandboxOptions
                {
                    SeccompProfilePath = "profiles/missing-seccomp.json",
                    RequireSeccompProfile = true
                },
                new SandboxJobSpec
                {
                    Command = ["true"],
                    WorkingDirectory = "/repo"
                },
                "/repo",
                "qre-sandbox-test"));
    }

    [Fact]
    public void ResolveBundledProfilePath_ReturnsValidSeccompJson()
    {
        var path = DockerSandboxSeccompProfile.ResolveBundledProfilePath();

        Assert.False(string.IsNullOrWhiteSpace(path));
        using var json = JsonDocument.Parse(File.ReadAllText(path!));
        Assert.Equal("SCMP_ACT_ALLOW", json.RootElement.GetProperty("defaultAction").GetString());
        var deniedSyscalls = json.RootElement
            .GetProperty("syscalls")[0]
            .GetProperty("names")
            .EnumerateArray()
            .Select(static name => name.GetString())
            .ToArray();
        Assert.Contains("unshare", deniedSyscalls);
        Assert.Contains("keyctl", deniedSyscalls);
        Assert.Contains("bpf", deniedSyscalls);
    }
}
