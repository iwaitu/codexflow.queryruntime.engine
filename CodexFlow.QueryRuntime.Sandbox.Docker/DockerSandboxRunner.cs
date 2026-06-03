using System.Globalization;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Sandbox.LocalProcess;

namespace CodexFlow.QueryRuntime.Sandbox.Docker;

public sealed class DockerSandboxRunner(DockerSandboxOptions? options = null) : ISandboxRunner
{
    private readonly DockerSandboxOptions _options = options ?? new DockerSandboxOptions();
    private readonly LocalProcessSandboxRunner _hostRunner = new();

    public async Task<SandboxResult> RunAsync(
        SandboxJobSpec spec,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Command.Count == 0 || string.IsNullOrWhiteSpace(spec.Command[0]))
        {
            throw new ArgumentException("Sandbox command must include an executable.", nameof(spec));
        }

        var workingDirectory = Path.GetFullPath(spec.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Sandbox working directory does not exist: {workingDirectory}");
        }
        var workspaceRoot = ResolveWorkspaceRoot(spec, workingDirectory);
        var relativeWorkingDirectory = Path.GetRelativePath(workspaceRoot, workingDirectory);
        if (relativeWorkingDirectory.StartsWith("..", StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relativeWorkingDirectory))
        {
            throw new InvalidOperationException($"Sandbox working directory must be under workspace root: {workingDirectory}");
        }

        var useStagedWorkspace = ShouldUseStagedWorkspace(_options, spec);
        var containerWorkspaceSource = workspaceRoot;
        var containerWorkingDirectory = ResolveContainerWorkingDirectory(_options, relativeWorkingDirectory);
        string? stagedWorkspace = null;
        try
        {
            if (useStagedWorkspace)
            {
                stagedWorkspace = CreateStagedWorkspacePath(workspaceRoot);
                CopyWorkspace(workspaceRoot, stagedWorkspace);
            }

            containerWorkspaceSource = stagedWorkspace ?? workspaceRoot;
            var containerName = "qre-sandbox-" + Guid.NewGuid().ToString("N");
            var dockerCommand = BuildDockerRunCommand(
                _options,
                spec,
                containerWorkspaceSource,
                containerName,
                containerWorkingDirectory);
            SandboxResult result;
            try
            {
                result = await _hostRunner.RunAsync(
                    new SandboxJobSpec
                    {
                        Command = dockerCommand,
                        WorkingDirectory = workingDirectory,
                        Environment = TrustedLocalSandboxEnvironment.Create(),
                        Limits = new SandboxLimits
                        {
                            Timeout = spec.Limits.Timeout,
                            MaxOutputBytes = spec.Limits.MaxOutputBytes
                        },
                        Network = SandboxNetworkPolicy.Deny,
                        Mounts = SandboxMountPolicy.WorkspaceReadOnly
                    },
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await TryRemoveContainerAsync(containerName, workingDirectory, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            if (result.TimedOut)
            {
                await TryRemoveContainerAsync(containerName, workingDirectory, ct).ConfigureAwait(false);
            }

            if (result.ExitCode == 0 && stagedWorkspace != null)
            {
                CopyWorkspaceChangesBack(stagedWorkspace, workspaceRoot);
            }

            return result;
        }
        finally
        {
            if (stagedWorkspace != null && Directory.Exists(stagedWorkspace))
            {
                Directory.Delete(stagedWorkspace, recursive: true);
            }
        }
    }

    internal static IReadOnlyList<string> BuildDockerRunCommand(
        DockerSandboxOptions options,
        SandboxJobSpec spec,
        string hostWorkspaceSource,
        string containerName,
        string? containerWorkingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(spec);

        var workspaceMountSuffix = string.Equals(
                spec.Mounts.Mode,
                SandboxMountPolicy.WorkspaceReadWrite.Mode,
                StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : ",readonly";
        var args = new List<string>
        {
            options.DockerExecutable,
            "run",
            "--rm",
            "--name",
            containerName,
            "--workdir",
            containerWorkingDirectory ?? options.ContainerWorkspacePath,
            "--mount",
            $"type=bind,src={hostWorkspaceSource},dst={options.ContainerWorkspacePath}{workspaceMountSuffix}",
            "--memory",
            FormatMemory(spec.Limits.MemoryBytes),
            "--cpus",
            FormatCpu(spec.Limits.CpuCount),
            "--stop-timeout",
            "1"
        };

        if (!string.IsNullOrWhiteSpace(options.ContainerUser))
        {
            args.Add("--user");
            args.Add(options.ContainerUser);
        }

        if (options.DropAllCapabilities)
        {
            args.Add("--cap-drop");
            args.Add("ALL");
        }

        if (options.NoNewPrivileges)
        {
            args.Add("--security-opt");
            args.Add("no-new-privileges");
        }

        if (string.IsNullOrWhiteSpace(options.SeccompProfilePath))
        {
            if (options.RequireSeccompProfile)
            {
                throw new FileNotFoundException("Docker sandbox seccomp profile is required but was not resolved.");
            }
        }
        else
        {
            if (!File.Exists(options.SeccompProfilePath))
            {
                throw new FileNotFoundException(
                    "Docker sandbox seccomp profile does not exist.",
                    options.SeccompProfilePath);
            }

            args.Add("--security-opt");
            args.Add($"seccomp={Path.GetFullPath(options.SeccompProfilePath)}");
        }

        if (options.ReadOnlyRootFilesystem)
        {
            args.Add("--read-only");
            if (!string.IsNullOrWhiteSpace(options.TmpfsMount))
            {
                args.Add("--tmpfs");
                args.Add(options.TmpfsMount);
            }
        }

        if (string.Equals(spec.Network.Mode, SandboxNetworkPolicy.Deny.Mode, StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--network");
            args.Add("none");
        }
        else if (!string.Equals(spec.Network.Mode, SandboxNetworkPolicy.Allow.Mode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported Docker sandbox network policy: {spec.Network.Mode}");
        }

        foreach (var pair in spec.Environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            args.Add("--env");
            args.Add($"{pair.Key}={pair.Value}");
        }

        args.Add(options.Image);
        args.AddRange(spec.Command);
        return args;
    }

    private async Task TryRemoveContainerAsync(
        string containerName,
        string workingDirectory,
        CancellationToken ct)
    {
        try
        {
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _hostRunner.RunAsync(
                new SandboxJobSpec
                {
                    Command = [_options.DockerExecutable, "rm", "-f", containerName],
                    WorkingDirectory = workingDirectory,
                    Environment = TrustedLocalSandboxEnvironment.Create(),
                    Limits = new SandboxLimits
                    {
                        Timeout = TimeSpan.FromSeconds(10),
                        MaxOutputBytes = 64 * 1024
                    },
                    Network = SandboxNetworkPolicy.Deny,
                    Mounts = SandboxMountPolicy.WorkspaceReadOnly
                },
                cleanupCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string FormatMemory(long memoryBytes)
        => Math.Max(4L * 1024 * 1024, memoryBytes).ToString(CultureInfo.InvariantCulture) + "b";

    private static string FormatCpu(double cpuCount)
        => Math.Max(0.1, cpuCount).ToString("0.###", CultureInfo.InvariantCulture);

    private static bool ShouldUseStagedWorkspace(DockerSandboxOptions options, SandboxJobSpec spec)
        => options.CopyWorkspaceForWriteJobs &&
           string.Equals(
               spec.Mounts.Mode,
               SandboxMountPolicy.WorkspaceReadWrite.Mode,
               StringComparison.OrdinalIgnoreCase);

    private static string ResolveWorkspaceRoot(SandboxJobSpec spec, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(spec.WorkspaceRoot))
        {
            return workingDirectory;
        }

        var workspaceRoot = Path.GetFullPath(spec.WorkspaceRoot);
        if (!Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException($"Sandbox workspace root does not exist: {workspaceRoot}");
        }

        return workspaceRoot;
    }

    private static string ResolveContainerWorkingDirectory(
        DockerSandboxOptions options,
        string relativeWorkingDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeWorkingDirectory) ||
            relativeWorkingDirectory == ".")
        {
            return options.ContainerWorkspacePath;
        }

        var normalizedRelativePath = relativeWorkingDirectory.Replace(
            Path.DirectorySeparatorChar,
            '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            normalizedRelativePath = normalizedRelativePath.Replace(
                Path.AltDirectorySeparatorChar,
                '/');
        }

        return options.ContainerWorkspacePath.TrimEnd('/') + "/" + normalizedRelativePath;
    }

    private static string CreateStagedWorkspacePath(string workspaceRoot)
    {
        var path = Path.Combine(
            workspaceRoot,
            ".qre",
            "docker-workspaces",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        MakeWritableByContainerUser(path, isDirectory: true);
        return path;
    }

    private static void CopyWorkspace(string sourceRoot, string targetRoot)
    {
        foreach (var directory in EnumerateWorkspaceDirectories(sourceRoot))
        {
            var relative = Path.GetRelativePath(sourceRoot, directory);
            if (ShouldSkipWorkspacePath(relative))
            {
                continue;
            }

            var target = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(target);
            MakeWritableByContainerUser(target, isDirectory: true);
        }

        foreach (var file in EnumerateWorkspaceFiles(sourceRoot))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            if (ShouldSkipWorkspacePath(relative))
            {
                continue;
            }

            var target = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            MakeWritableByContainerUser(target, isDirectory: false);
        }
    }

    private static void CopyWorkspaceChangesBack(string stagedRoot, string destinationRoot)
    {
        foreach (var directory in EnumerateWorkspaceDirectories(stagedRoot))
        {
            var relative = Path.GetRelativePath(stagedRoot, directory);
            if (ShouldSkipWorkspacePath(relative))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
        }

        foreach (var file in EnumerateWorkspaceFiles(stagedRoot))
        {
            var relative = Path.GetRelativePath(stagedRoot, file);
            if (ShouldSkipWorkspacePath(relative))
            {
                continue;
            }

            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static bool ShouldSkipWorkspacePath(string relativePath)
    {
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(static segment => segment is ".git" or ".qre");
    }

    private static IEnumerable<string> EnumerateWorkspaceDirectories(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                var relative = Path.GetRelativePath(root, directory);
                if (ShouldSkipWorkspacePath(relative) || IsSymbolicLink(directory))
                {
                    continue;
                }

                yield return directory;
                pending.Push(directory);
            }
        }
    }

    private static IEnumerable<string> EnumerateWorkspaceFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                var relative = Path.GetRelativePath(root, directory);
                if (ShouldSkipWorkspacePath(relative) || IsSymbolicLink(directory))
                {
                    continue;
                }

                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current))
            {
                if (!IsSymbolicLink(file))
                {
                    yield return file;
                }
            }
        }
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void MakeWritableByContainerUser(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                isDirectory
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                      UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                      UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite |
                      UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                      UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }
}
