namespace CodexFlow.QueryRuntime.Sandbox.Docker;

public sealed record DockerSandboxOptions
{
    public string DockerExecutable { get; init; } = "docker";

    public string Image { get; init; } = "mcr.microsoft.com/dotnet/sdk:10.0";

    public string ContainerWorkspacePath { get; init; } = "/workspace";

    public string ContainerUser { get; init; } = "65532:65532";

    public bool DropAllCapabilities { get; init; } = true;

    public bool NoNewPrivileges { get; init; } = true;

    public bool ReadOnlyRootFilesystem { get; init; } = true;

    public string TmpfsMount { get; init; } = "/tmp:rw,noexec,nosuid,size=64m";

    public string? SeccompProfilePath { get; init; }

    public bool RequireSeccompProfile { get; init; }

    public bool CopyWorkspaceForWriteJobs { get; init; } = true;
}
