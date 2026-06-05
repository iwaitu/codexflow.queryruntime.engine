using System.Collections.Concurrent;
using System.Text;
using CodexFlow.QueryRuntime.Abstractions;
using Microsoft.Extensions.AI;

namespace CodexFlow.QueryRuntime.Experimental;

public static class ExperimentalRepairToolPack
{
    private const int MaxWriteChars = 1_000_000;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> EditLogLocks = new(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> RepairFileCapabilities = CapabilitySet(
        QueryRuntimeCapabilities.ReadFileSystem,
        QueryRuntimeCapabilities.WriteFileSystem);

    public static IReadOnlyList<AIFunction> Create(
        string workspacePath,
        string? runDirectory = null,
        IQueryRuntimeCapabilityPolicy? capabilityPolicy = null,
        IQueryRuntimePolicyDecisionSink? policyDecisionSink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var workspaceRoot = QueryRuntimePathSafety.NormalizeRoot(workspacePath);
        if (!Directory.Exists(workspaceRoot))
        {
            throw new DirectoryNotFoundException($"Workspace does not exist: {workspaceRoot}");
        }

        capabilityPolicy ??= new ExperimentalCapabilityPolicy();
        return
        [
            AIFunctionFactory.Create(
                (string path, string content, bool overwrite = true) =>
                    WriteFileAsync(workspaceRoot, runDirectory, capabilityPolicy, policyDecisionSink, path, content, overwrite),
                new AIFunctionFactoryOptions
                {
                    Name = "qre_write_file",
                    Description = "Write UTF-8 text to a workspace file. Arguments: path, content, overwrite."
                }),
            AIFunctionFactory.Create(
                (string path, string old_text, string new_text, bool replace_all = false) =>
                    ApplyPatchAsync(workspaceRoot, runDirectory, capabilityPolicy, policyDecisionSink, path, old_text, new_text, replace_all),
                new AIFunctionFactoryOptions
                {
                    Name = "qre_apply_patch",
                    Description = "Apply a targeted text replacement patch to a workspace file. Arguments: path, old_text, new_text, replace_all."
                })
        ];
    }

    private static async Task<string> WriteFileAsync(
        string workspaceRoot,
        string? runDirectory,
        IQueryRuntimeCapabilityPolicy capabilityPolicy,
        IQueryRuntimePolicyDecisionSink? policyDecisionSink,
        string path,
        string content,
        bool overwrite)
    {
        content ??= string.Empty;
        if (content.Length > MaxWriteChars)
        {
            throw new InvalidOperationException($"qre_write_file content exceeds {MaxWriteChars} characters.");
        }

        var target = await ResolveWritablePathAsync(policyDecisionSink, workspaceRoot, "qre_write_file", path).ConfigureAwait(false);
        await EnsureAllowedAsync(capabilityPolicy, policyDecisionSink, workspaceRoot, "qre_write_file").ConfigureAwait(false);
        if (File.Exists(target) && !overwrite)
        {
            throw new InvalidOperationException($"File already exists and overwrite is false: {path}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, content, Encoding.UTF8).ConfigureAwait(false);
        await RecordEditedPathAsync(workspaceRoot, runDirectory, target).ConfigureAwait(false);
        return $"wrote {Path.GetRelativePath(workspaceRoot, target).Replace('\\', '/')}";
    }

    private static async Task<string> ApplyPatchAsync(
        string workspaceRoot,
        string? runDirectory,
        IQueryRuntimeCapabilityPolicy capabilityPolicy,
        IQueryRuntimePolicyDecisionSink? policyDecisionSink,
        string path,
        string oldText,
        string newText,
        bool replaceAll)
    {
        if (string.IsNullOrEmpty(oldText))
        {
            throw new InvalidOperationException("qre_apply_patch requires non-empty old_text.");
        }

        newText ??= string.Empty;
        var target = await ResolveWritablePathAsync(policyDecisionSink, workspaceRoot, "qre_apply_patch", path).ConfigureAwait(false);
        await EnsureAllowedAsync(capabilityPolicy, policyDecisionSink, workspaceRoot, "qre_apply_patch").ConfigureAwait(false);
        if (!File.Exists(target))
        {
            throw new FileNotFoundException($"File not found: {path}", target);
        }

        var original = await File.ReadAllTextAsync(target).ConfigureAwait(false);
        if (!original.Contains(oldText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("qre_apply_patch old_text was not found.");
        }

        var updated = replaceAll
            ? original.Replace(oldText, newText, StringComparison.Ordinal)
            : ReplaceFirst(original, oldText, newText);
        await File.WriteAllTextAsync(target, updated, Encoding.UTF8).ConfigureAwait(false);
        await RecordEditedPathAsync(workspaceRoot, runDirectory, target).ConfigureAwait(false);
        return $"patched {Path.GetRelativePath(workspaceRoot, target).Replace('\\', '/')}";
    }

    private static async Task EnsureAllowedAsync(
        IQueryRuntimeCapabilityPolicy capabilityPolicy,
        IQueryRuntimePolicyDecisionSink? policyDecisionSink,
        string workspaceRoot,
        string toolName)
    {
        var decision = capabilityPolicy.Evaluate(
            new QueryRuntimeCapabilityRequest
            {
                Profile = QueryRuntimeToolProfile.Repair,
                ToolName = toolName,
                Capabilities = RepairFileCapabilities,
                Command = [],
                WorkspacePath = workspaceRoot,
                Network = SandboxNetworkPolicy.Deny,
                Mounts = SandboxMountPolicy.WorkspaceReadWrite
            });
        await WritePolicyDecisionAsync(policyDecisionSink, toolName, decision).ConfigureAwait(false);

        if (decision.Kind != QueryRuntimeCapabilityDecisionKind.Allow)
        {
            throw new InvalidOperationException($"Capability policy {decision.Kind}: {decision.Reason}");
        }
    }

    private static async Task<string> ResolveWritablePathAsync(
        IQueryRuntimePolicyDecisionSink? policyDecisionSink,
        string workspaceRoot,
        string toolName,
        string path)
    {
        try
        {
            return ResolveWritablePath(workspaceRoot, path);
        }
        catch (InvalidOperationException ex)
        {
            await WritePolicyDecisionAsync(
                policyDecisionSink,
                toolName,
                QueryRuntimeCapabilityDecision.Deny(ex.Message)).ConfigureAwait(false);
            throw;
        }
    }

    private static string ResolveWritablePath(string workspaceRoot, string path)
    {
        var target = QueryRuntimePathSafety.ResolveUnderRoot(workspaceRoot, path);
        QueryRuntimePathSafety.RejectProtectedWorkspacePath(workspaceRoot, target, "modified by repair tools");
        return target;
    }

    private static async Task RecordEditedPathAsync(string workspaceRoot, string? runDirectory, string target)
    {
        if (string.IsNullOrWhiteSpace(runDirectory))
        {
            return;
        }

        var relative = Path.GetRelativePath(workspaceRoot, target).Replace('\\', '/');
        Directory.CreateDirectory(runDirectory);
        var editLogPath = Path.Combine(runDirectory, "repair-edits.txt");
        var editLogLock = EditLogLocks.GetOrAdd(editLogPath, static _ => new SemaphoreSlim(1, 1));
        await editLogLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(
                editLogPath,
                relative + Environment.NewLine,
                Encoding.UTF8).ConfigureAwait(false);
        }
        finally
        {
            editLogLock.Release();
        }
    }

    private static Task WritePolicyDecisionAsync(
        IQueryRuntimePolicyDecisionSink? policyDecisionSink,
        string toolName,
        QueryRuntimeCapabilityDecision decision)
    {
        if (policyDecisionSink == null)
        {
            return Task.CompletedTask;
        }

        var decisionRecord = new QueryRuntimePolicyDecisionRecord(
            QueryRuntimeToolProfile.Repair.Name,
            toolName,
            RepairFileCapabilities,
            [],
            SandboxNetworkPolicy.Deny.Mode,
            SandboxMountPolicy.WorkspaceReadWrite.Mode,
            decision.Kind.ToString(),
            decision.Kind == QueryRuntimeCapabilityDecisionKind.Allow,
            decision.Reason,
            DateTimeOffset.UtcNow);
        return policyDecisionSink.OnPolicyDecisionAsync(decisionRecord);
    }

    private static string ReplaceFirst(string original, string oldText, string newText)
    {
        var index = original.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0
            ? original
            : string.Concat(original.AsSpan(0, index), newText, original.AsSpan(index + oldText.Length));
    }

    private static IReadOnlySet<string> CapabilitySet(params string[] capabilities)
        => new HashSet<string>(capabilities, StringComparer.Ordinal);
}
