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
    public void BuildDockerRunCommand_UsesExplicitStagingUserOverride()
    {
        var command = DockerSandboxRunner.BuildDockerRunCommand(
            new DockerSandboxOptions(),
            new SandboxJobSpec
            {
                Command = ["true"],
                WorkingDirectory = "/repo",
                Mounts = SandboxMountPolicy.WorkspaceReadWrite
            },
            "/staged",
            "qre-sandbox-test",
            containerUserOverride: "1001:1002");

        var userIndex = command.ToList().IndexOf("--user");
        Assert.True(userIndex >= 0);
        Assert.Equal("1001:1002", command[userIndex + 1]);
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData(".Git/config")]
    [InlineData("nested/.QRE/events.jsonl")]
    public void WorkspaceEnumeration_ProtectsInternalDirectoriesCaseInsensitively(string path)
        => Assert.True(DockerSandboxRunner.ShouldSkipWorkspacePath(path));

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

    [Fact]
    public void WorkspaceWriteBack_AppliesValidatedChangesAndReturnsManifest()
    {
        using var workspace = TemporaryDirectory.Create();
        using var staged = TemporaryDirectory.Create();
        File.WriteAllText(Path.Combine(workspace.Path, "existing.txt"), "before");
        File.WriteAllText(Path.Combine(staged.Path, "existing.txt"), "before");
        var baseline = DockerWorkspaceWriteBack.CaptureBaseline(workspace.Path);

        File.WriteAllText(Path.Combine(staged.Path, "existing.txt"), "after");
        Directory.CreateDirectory(Path.Combine(staged.Path, "src"));
        File.WriteAllText(Path.Combine(staged.Path, "src", "new.txt"), "new");

        var manifest = DockerWorkspaceWriteBack.Apply(
            staged.Path,
            workspace.Path,
            baseline,
            new DockerSandboxOptions());

        Assert.Equal(2, manifest.Changes.Count);
        Assert.Contains("M existing.txt", manifest.BoundedDiff, StringComparison.Ordinal);
        Assert.Contains("A src/new.txt", manifest.BoundedDiff, StringComparison.Ordinal);
        Assert.False(manifest.DiffTruncated);
        Assert.Equal("after", File.ReadAllText(Path.Combine(workspace.Path, "existing.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(workspace.Path, "src", "new.txt")));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(workspace.Path, "*", SearchOption.AllDirectories),
            static path => path.Contains(".qre-", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkspaceWriteBack_RejectsDeletion()
    {
        using var workspace = TemporaryDirectory.Create();
        using var staged = TemporaryDirectory.Create();
        File.WriteAllText(Path.Combine(workspace.Path, "delete-me.txt"), "before");
        var baseline = DockerWorkspaceWriteBack.CaptureBaseline(workspace.Path);

        var ex = Assert.Throws<InvalidOperationException>(() => DockerWorkspaceWriteBack.Apply(
            staged.Path,
            workspace.Path,
            baseline,
            new DockerSandboxOptions()));

        Assert.Contains("deletion", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("before", File.ReadAllText(Path.Combine(workspace.Path, "delete-me.txt")));
    }

    [Fact]
    public void WorkspaceWriteBack_RejectsOversizedFileBeforeApplyingAnyChange()
    {
        using var workspace = TemporaryDirectory.Create();
        using var staged = TemporaryDirectory.Create();
        var baseline = DockerWorkspaceWriteBack.CaptureBaseline(workspace.Path);
        File.WriteAllText(Path.Combine(staged.Path, "small.txt"), "ok");
        File.WriteAllText(Path.Combine(staged.Path, "large.txt"), new string('x', 32));

        var ex = Assert.Throws<InvalidOperationException>(() => DockerWorkspaceWriteBack.Apply(
            staged.Path,
            workspace.Path,
            baseline,
            new DockerSandboxOptions { MaxWriteBackFileBytes = 16 }));

        Assert.Contains("file exceeds", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "small.txt")));
    }

    [Fact]
    public void WorkspaceWriteBack_RejectsFileCountAndAggregateSizeBeforeApplyingAnyChange()
    {
        using var workspace = TemporaryDirectory.Create();
        using var staged = TemporaryDirectory.Create();
        var baseline = DockerWorkspaceWriteBack.CaptureBaseline(workspace.Path);
        File.WriteAllText(Path.Combine(staged.Path, "one.txt"), "1234");
        File.WriteAllText(Path.Combine(staged.Path, "two.txt"), "5678");

        Assert.Throws<InvalidOperationException>(() => DockerWorkspaceWriteBack.Apply(
            staged.Path,
            workspace.Path,
            baseline,
            new DockerSandboxOptions { MaxWriteBackFileCount = 1 }));
        Assert.Empty(Directory.EnumerateFiles(workspace.Path));

        Assert.Throws<InvalidOperationException>(() => DockerWorkspaceWriteBack.Apply(
            staged.Path,
            workspace.Path,
            baseline,
            new DockerSandboxOptions { MaxWriteBackTotalBytes = 7 }));
        Assert.Empty(Directory.EnumerateFiles(workspace.Path));
    }

    [Fact]
    public void WorkspaceWriteBack_BoundsDiffWithoutSkippingValidatedApply()
    {
        using var workspace = TemporaryDirectory.Create();
        using var staged = TemporaryDirectory.Create();
        var baseline = DockerWorkspaceWriteBack.CaptureBaseline(workspace.Path);
        File.WriteAllText(Path.Combine(staged.Path, "new.txt"), "content");

        var manifest = DockerWorkspaceWriteBack.Apply(
            staged.Path,
            workspace.Path,
            baseline,
            new DockerSandboxOptions { MaxWriteBackDiffBytes = 1 });

        Assert.True(manifest.DiffTruncated);
        Assert.Empty(manifest.BoundedDiff);
        Assert.Equal("content", File.ReadAllText(Path.Combine(workspace.Path, "new.txt")));
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.staging")]
    [InlineData(".env.qa")]
    public void WorkspaceWriteBack_RejectsProtectedPath(string path)
    {
        using var workspace = TemporaryDirectory.Create();
        using var staged = TemporaryDirectory.Create();
        var baseline = DockerWorkspaceWriteBack.CaptureBaseline(workspace.Path);
        File.WriteAllText(Path.Combine(staged.Path, path), "must-not-copy");

        var error = Assert.Throws<InvalidOperationException>(() => DockerWorkspaceWriteBack.Apply(
            staged.Path,
            workspace.Path,
            baseline,
            new DockerSandboxOptions()));

        Assert.Contains("cannot be written", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(workspace.Path, path)));
    }

    [Theory]
    [InlineData("src/TokenService.cs")]
    [InlineData("tests/SecretMaskerTests.cs")]
    [InlineData("docs/credentials-guide.md")]
    [InlineData(".env.example")]
    [InlineData(".env.sample")]
    [InlineData(".env.template")]
    public void WorkspaceWriteBack_AllowsFuzzySecretLookingSourcePaths(string path)
    {
        using var workspace = TemporaryDirectory.Create();
        using var staged = TemporaryDirectory.Create();
        var baseline = DockerWorkspaceWriteBack.CaptureBaseline(workspace.Path);
        var stagedFile = Path.Combine(staged.Path, path);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedFile)!);
        File.WriteAllText(stagedFile, "normal source content");

        var manifest = DockerWorkspaceWriteBack.Apply(
            staged.Path,
            workspace.Path,
            baseline,
            new DockerSandboxOptions());

        Assert.Single(manifest.Changes);
        Assert.True(File.Exists(Path.Combine(workspace.Path, path)));
    }

    [Fact]
    public void WorkspaceWriteBack_RollsBackAlreadyAppliedFilesWhenCommitFails()
    {
        using var workspace = TemporaryDirectory.Create();
        using var staged = TemporaryDirectory.Create();
        File.WriteAllText(Path.Combine(workspace.Path, "one.txt"), "one-before");
        File.WriteAllText(Path.Combine(workspace.Path, "two.txt"), "two-before");
        File.WriteAllText(Path.Combine(staged.Path, "one.txt"), "one-before");
        File.WriteAllText(Path.Combine(staged.Path, "two.txt"), "two-before");
        var baseline = DockerWorkspaceWriteBack.CaptureBaseline(workspace.Path);
        File.WriteAllText(Path.Combine(staged.Path, "one.txt"), "one-after");
        File.WriteAllText(Path.Combine(staged.Path, "two.txt"), "two-after");

        Assert.Throws<IOException>(() => DockerWorkspaceWriteBack.Apply(
            staged.Path,
            workspace.Path,
            baseline,
            new DockerSandboxOptions(),
            beforeCommit: index =>
            {
                if (index == 1)
                {
                    throw new IOException("injected commit failure");
                }
            }));

        Assert.Equal("one-before", File.ReadAllText(Path.Combine(workspace.Path, "one.txt")));
        Assert.Equal("two-before", File.ReadAllText(Path.Combine(workspace.Path, "two.txt")));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(workspace.Path, "*", SearchOption.AllDirectories),
            static path => path.Contains(".qre-", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkspaceWriteBack_RejectsHostConcurrencyConflict()
    {
        using var workspace = TemporaryDirectory.Create();
        using var staged = TemporaryDirectory.Create();
        var hostFile = Path.Combine(workspace.Path, "file.txt");
        File.WriteAllText(hostFile, "baseline");
        File.WriteAllText(Path.Combine(staged.Path, "file.txt"), "baseline");
        var baseline = DockerWorkspaceWriteBack.CaptureBaseline(workspace.Path);
        File.WriteAllText(Path.Combine(staged.Path, "file.txt"), "container-change");
        File.WriteAllText(hostFile, "host-change");

        var ex = Assert.Throws<InvalidOperationException>(() => DockerWorkspaceWriteBack.Apply(
            staged.Path,
            workspace.Path,
            baseline,
            new DockerSandboxOptions()));

        Assert.Contains("conflict", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("host-change", File.ReadAllText(hostFile));
    }

    [Fact]
    public void WorkspaceWriteBack_RejectsConflictIntroducedImmediatelyBeforeCommit()
    {
        using var workspace = TemporaryDirectory.Create();
        using var staged = TemporaryDirectory.Create();
        var hostFile = Path.Combine(workspace.Path, "file.txt");
        File.WriteAllText(hostFile, "baseline");
        File.WriteAllText(Path.Combine(staged.Path, "file.txt"), "baseline");
        var baseline = DockerWorkspaceWriteBack.CaptureBaseline(workspace.Path);
        File.WriteAllText(Path.Combine(staged.Path, "file.txt"), "container-change");

        var ex = Assert.Throws<InvalidOperationException>(() => DockerWorkspaceWriteBack.Apply(
            staged.Path,
            workspace.Path,
            baseline,
            new DockerSandboxOptions(),
            beforeCommit: _ => File.WriteAllText(hostFile, "racing-host-change")));

        Assert.Contains("conflict", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("racing-host-change", File.ReadAllText(hostFile));
    }

    [Fact]
    public void WorkspaceWriteBack_RejectsSymlinkEntry()
    {
        using var workspace = TemporaryDirectory.Create();
        using var staged = TemporaryDirectory.Create();
        using var outside = TemporaryDirectory.Create();
        var outsideFile = Path.Combine(outside.Path, "outside.txt");
        File.WriteAllText(outsideFile, "outside");
        var link = Path.Combine(staged.Path, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, outsideFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var baseline = DockerWorkspaceWriteBack.CaptureBaseline(workspace.Path);
        var error = Assert.Throws<InvalidOperationException>(() => DockerWorkspaceWriteBack.Apply(
            staged.Path,
            workspace.Path,
            baseline,
            new DockerSandboxOptions()));

        Assert.Contains("links", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "qre-docker-writeback-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
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
