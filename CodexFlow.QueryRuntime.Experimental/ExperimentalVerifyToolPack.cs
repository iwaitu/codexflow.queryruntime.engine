using System.Text;
using CodexFlow.QueryRuntime.Abstractions;
using CodexFlow.QueryRuntime.Sandbox.LocalProcess;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Experimental;

public static class ExperimentalVerifyToolPack
{
    private const int DefaultMaxOutputChars = 20_000;

    private static readonly IReadOnlySet<string> GitReadCapabilities = CapabilitySet(
        QueryRuntimeCapabilities.GitRead,
        QueryRuntimeCapabilities.ExecuteProcess);

    private static readonly IReadOnlySet<string> DotnetTestCapabilities = CapabilitySet(
        QueryRuntimeCapabilities.ReadFileSystem,
        QueryRuntimeCapabilities.WriteArtifacts,
        QueryRuntimeCapabilities.ExecuteProcess,
        QueryRuntimeCapabilities.RunTests,
        QueryRuntimeCapabilities.Build);

    private static readonly IReadOnlySet<string> DotnetBuildCapabilities = CapabilitySet(
        QueryRuntimeCapabilities.ReadFileSystem,
        QueryRuntimeCapabilities.WriteArtifacts,
        QueryRuntimeCapabilities.ExecuteProcess,
        QueryRuntimeCapabilities.Build);

    public static IReadOnlyList<AIFunction> Create(
        string workspacePath,
        ISandboxRunner? sandboxRunner = null,
        IQueryRuntimeCapabilityPolicy? capabilityPolicy = null,
        IQueryRuntimePolicyDecisionSink? policyDecisionSink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var workspaceRoot = ExperimentalWorkspacePath.NormalizeRoot(workspacePath);
        if (!Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException($"Workspace does not exist: {workspaceRoot}");
        }

        sandboxRunner ??= new LocalProcessSandboxRunner();
        capabilityPolicy ??= new ExperimentalCapabilityPolicy();
        return
        [
            AIFunctionFactory.Create(
                (int max_output_chars = DefaultMaxOutputChars) => GitStatusAsync(workspaceRoot, sandboxRunner, capabilityPolicy, policyDecisionSink, max_output_chars),
                new AIFunctionFactoryOptions
                {
                    Name = "qre_git_status",
                    Description = "Run git status --short in the workspace. Trusted local verify profile only."
                }),
            AIFunctionFactory.Create(
                (string path = ".", int max_output_chars = DefaultMaxOutputChars) => GitDiffAsync(workspaceRoot, sandboxRunner, capabilityPolicy, policyDecisionSink, path, max_output_chars),
                new AIFunctionFactoryOptions
                {
                    Name = "qre_git_diff",
                    Description = "Run git diff for a workspace path. Trusted local verify profile only."
                }),
            AIFunctionFactory.Create(
                (string target = "", string filter = "", int timeout_seconds = 300, int max_output_chars = DefaultMaxOutputChars) =>
                    DotnetTestAsync(workspaceRoot, sandboxRunner, capabilityPolicy, policyDecisionSink, target, filter, timeout_seconds, max_output_chars),
                new AIFunctionFactoryOptions
                {
                    Name = "qre_dotnet_test",
                    Description = "Run dotnet test --no-restore for trusted local verification. Arguments: target, filter, timeout_seconds, max_output_chars."
                }),
            AIFunctionFactory.Create(
                (string target = "", int timeout_seconds = 300, int max_output_chars = DefaultMaxOutputChars) =>
                    DotnetBuildAsync(workspaceRoot, sandboxRunner, capabilityPolicy, policyDecisionSink, target, timeout_seconds, max_output_chars),
                new AIFunctionFactoryOptions
                {
                    Name = "qre_dotnet_build",
                    Description = "Run dotnet build --no-restore for trusted local verification. Arguments: target, timeout_seconds, max_output_chars."
                })
        ];
    }

    private static async Task<string> GitStatusAsync(
        string workspaceRoot,
        ISandboxRunner sandboxRunner,
        IQueryRuntimeCapabilityPolicy capabilityPolicy,
        IQueryRuntimePolicyDecisionSink? policyDecisionSink,
        int maxOutputChars)
    {
        var command = new[] { "git", "status", "--short" };
        var result = await RunAsync(
            sandboxRunner,
            capabilityPolicy,
            policyDecisionSink,
            workspaceRoot,
            "qre_git_status",
            GitReadCapabilities,
            command,
            TimeSpan.FromSeconds(30),
            SandboxMountPolicy.WorkspaceReadOnly,
            maxOutputChars).ConfigureAwait(false);

        return FormatResult(string.Join(' ', command), result, maxOutputChars);
    }

    private static async Task<string> GitDiffAsync(
        string workspaceRoot,
        ISandboxRunner sandboxRunner,
        IQueryRuntimeCapabilityPolicy capabilityPolicy,
        IQueryRuntimePolicyDecisionSink? policyDecisionSink,
        string path,
        int maxOutputChars)
    {
        var resolvedPath = ResolveWorkspacePath(workspaceRoot, path);
        var relativePath = Path.GetRelativePath(workspaceRoot, resolvedPath);
        var command = relativePath == "."
            ? new[] { "git", "diff" }
            : ["git", "diff", "--", relativePath];
        var result = await RunAsync(
            sandboxRunner,
            capabilityPolicy,
            policyDecisionSink,
            workspaceRoot,
            "qre_git_diff",
            GitReadCapabilities,
            command,
            TimeSpan.FromSeconds(30),
            SandboxMountPolicy.WorkspaceReadOnly,
            maxOutputChars).ConfigureAwait(false);

        return FormatResult(string.Join(' ', command), result, maxOutputChars);
    }

    private static async Task<string> DotnetTestAsync(
        string workspaceRoot,
        ISandboxRunner sandboxRunner,
        IQueryRuntimeCapabilityPolicy capabilityPolicy,
        IQueryRuntimePolicyDecisionSink? policyDecisionSink,
        string target,
        string filter,
        int timeoutSeconds,
        int maxOutputChars)
    {
        var command = new List<string> { "dotnet", "test" };
        if (!string.IsNullOrWhiteSpace(target))
        {
            var resolvedTarget = ResolveWorkspacePath(workspaceRoot, target);
            command.Add(Path.GetRelativePath(workspaceRoot, resolvedTarget));
        }

        command.Add("--no-restore");
        if (!string.IsNullOrWhiteSpace(filter))
        {
            command.Add("--filter");
            command.Add(filter);
        }

        var result = await RunAsync(
            sandboxRunner,
            capabilityPolicy,
            policyDecisionSink,
            workspaceRoot,
            "qre_dotnet_test",
            DotnetTestCapabilities,
            command,
            TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 1_800)),
            SandboxMountPolicy.WorkspaceReadWrite,
            maxOutputChars).ConfigureAwait(false);

        return FormatResult(string.Join(' ', command), result, maxOutputChars);
    }

    private static async Task<string> DotnetBuildAsync(
        string workspaceRoot,
        ISandboxRunner sandboxRunner,
        IQueryRuntimeCapabilityPolicy capabilityPolicy,
        IQueryRuntimePolicyDecisionSink? policyDecisionSink,
        string target,
        int timeoutSeconds,
        int maxOutputChars)
    {
        var command = new List<string> { "dotnet", "build" };
        if (!string.IsNullOrWhiteSpace(target))
        {
            var resolvedTarget = ResolveWorkspacePath(workspaceRoot, target);
            command.Add(Path.GetRelativePath(workspaceRoot, resolvedTarget));
        }

        command.Add("--no-restore");
        var result = await RunAsync(
            sandboxRunner,
            capabilityPolicy,
            policyDecisionSink,
            workspaceRoot,
            "qre_dotnet_build",
            DotnetBuildCapabilities,
            command,
            TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 1_800)),
            SandboxMountPolicy.WorkspaceReadWrite,
            maxOutputChars).ConfigureAwait(false);

        return FormatResult(string.Join(' ', command), result, maxOutputChars);
    }

    private static async Task<SandboxResult> RunAsync(
        ISandboxRunner sandboxRunner,
        IQueryRuntimeCapabilityPolicy capabilityPolicy,
        IQueryRuntimePolicyDecisionSink? policyDecisionSink,
        string workspaceRoot,
        string toolName,
        IReadOnlySet<string> capabilities,
        IReadOnlyList<string> command,
        TimeSpan timeout,
        SandboxMountPolicy mounts,
        int maxOutputChars)
    {
        var spec = new SandboxJobSpec
        {
            Command = command,
            WorkingDirectory = workspaceRoot,
            Environment = TrustedLocalSandboxEnvironment.Create(),
            Limits = new SandboxLimits
            {
                Timeout = timeout,
                MaxOutputBytes = Math.Clamp(maxOutputChars, 1_000, 200_000) * 4
            },
            Network = SandboxNetworkPolicy.Deny,
            Mounts = mounts
        };
        var decision = capabilityPolicy.Evaluate(
            new QueryRuntimeCapabilityRequest
            {
                Profile = QueryRuntimeToolProfile.Verify,
                ToolName = toolName,
                Capabilities = capabilities,
                Command = command,
                WorkspacePath = workspaceRoot,
                Network = spec.Network,
                Mounts = spec.Mounts
            });
        var decisionRecord = new QueryRuntimePolicyDecisionRecord(
            QueryRuntimeToolProfile.Verify.Name,
            toolName,
            capabilities,
            command,
            spec.Network.Mode,
            spec.Mounts.Mode,
            decision.Kind.ToString(),
            decision.Kind == QueryRuntimeCapabilityDecisionKind.Allow,
            decision.Reason,
            DateTimeOffset.UtcNow);
        if (policyDecisionSink != null)
        {
            await policyDecisionSink.OnPolicyDecisionAsync(decisionRecord).ConfigureAwait(false);
        }

        if (decision.Kind != QueryRuntimeCapabilityDecisionKind.Allow)
        {
            throw new InvalidOperationException($"Capability policy {decision.Kind}: {decision.Reason}");
        }

        return await sandboxRunner.RunAsync(spec).ConfigureAwait(false);
    }

    private static string FormatResult(string command, SandboxResult result, int maxOutputChars)
    {
        maxOutputChars = Math.Clamp(maxOutputChars, 1_000, 200_000);
        var sb = new StringBuilder();
        sb.AppendLine($"$ {command}");
        sb.AppendLine($"exit_code: {result.ExitCode}");
        sb.AppendLine($"timed_out: {result.TimedOut.ToString().ToLowerInvariant()}");
        sb.AppendLine($"duration_ms: {result.DurationMs}");
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            sb.AppendLine("stdout:");
            sb.AppendLine(Trim(result.StandardOutput, maxOutputChars));
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            sb.AppendLine("stderr:");
            sb.AppendLine(Trim(result.StandardError, maxOutputChars));
        }

        return sb.ToString();
    }

    private static string Trim(string value, int maxChars)
        => value.Length <= maxChars
            ? value
            : value[..maxChars] + "\n(output truncated)";

    private static string ResolveWorkspacePath(string workspaceRoot, string? path)
    {
        return ExperimentalWorkspacePath.ResolveUnderRoot(workspaceRoot, path);
    }

    private static IReadOnlySet<string> CapabilitySet(params string[] capabilities)
        => new HashSet<string>(capabilities, StringComparer.Ordinal);
}
