using System.Globalization;
using System.Runtime.InteropServices;
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
        string? stagedContainerUser = null;
        IReadOnlyDictionary<string, DockerWorkspaceFileSnapshot>? baseline = null;
        try
        {
            if (useStagedWorkspace)
            {
                stagedWorkspace = CreateStagedWorkspacePath(workspaceRoot);
                baseline = DockerWorkspaceWriteBack.CaptureBaseline(workspaceRoot);
                CopyWorkspace(workspaceRoot, stagedWorkspace);
                DockerWorkspaceWriteBack.ValidateStagedBaseline(stagedWorkspace, baseline);
                stagedContainerUser = ResolveStagedContainerUser(stagedWorkspace, _options.ContainerUser);
            }

            containerWorkspaceSource = stagedWorkspace ?? workspaceRoot;
            var containerName = "qre-sandbox-" + Guid.NewGuid().ToString("N");
            var dockerCommand = BuildDockerRunCommand(
                _options,
                spec,
                containerWorkspaceSource,
                containerName,
                containerWorkingDirectory,
                stagedContainerUser);
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
                DockerWorkspaceWriteBack.Apply(
                    stagedWorkspace,
                    workspaceRoot,
                    baseline!,
                    _options);
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
        string? containerWorkingDirectory = null,
        string? containerUserOverride = null)
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

        var containerUser = string.IsNullOrWhiteSpace(containerUserOverride)
            ? options.ContainerUser
            : containerUserOverride;
        if (!string.IsNullOrWhiteSpace(containerUser))
        {
            args.Add("--user");
            args.Add(containerUser);
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

    internal static bool ShouldSkipWorkspacePath(string relativePath)
    {
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(static segment =>
            segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".qre", StringComparison.OrdinalIgnoreCase));
    }

    internal static IEnumerable<string> EnumerateWorkspaceDirectories(string root)
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

    internal static IEnumerable<string> EnumerateWorkspaceFiles(string root)
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

    internal static bool IsSymbolicLink(string path)
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

        File.SetUnixFileMode(
            path,
            isDirectory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string? ResolveStagedContainerUser(string stagedWorkspace, string? configuredContainerUser)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var effectiveUserId = GetEffectiveUserId();
            var effectiveGroupId = GetEffectiveGroupId();
            if (effectiveUserId != 0)
            {
                return $"{effectiveUserId}:{effectiveGroupId}";
            }

            if (!TryParseContainerIdentity(configuredContainerUser, out var containerUserId, out var containerGroupId) ||
                containerUserId == 0)
            {
                throw new InvalidOperationException(
                    "Root-owned Docker staging requires a configured non-root numeric ContainerUser.");
            }

            foreach (var path in EnumerateWorkspaceDirectories(stagedWorkspace)
                         .Append(stagedWorkspace)
                         .Concat(EnumerateWorkspaceFiles(stagedWorkspace)))
            {
                if (ChangeOwner(path, containerUserId, containerGroupId) != 0)
                {
                    throw new IOException(
                        $"Unable to assign private Docker staging ownership (errno {Marshal.GetLastWin32Error()}).");
                }
            }

            return $"{containerUserId}:{containerGroupId}";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(
                "Unable to resolve the host UID/GID required for a private Docker staging workspace.",
                ex);
        }
    }

    private static bool TryParseContainerIdentity(string? value, out uint userId, out uint groupId)
    {
        userId = 0;
        groupId = 0;
        var segments = value?.Split(':', StringSplitOptions.TrimEntries);
        return segments is { Length: 2 } &&
               uint.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out userId) &&
               uint.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out groupId);
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "getegid")]
    private static extern uint GetEffectiveGroupId();

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int ChangeOwner(string path, uint owner, uint group);
}
