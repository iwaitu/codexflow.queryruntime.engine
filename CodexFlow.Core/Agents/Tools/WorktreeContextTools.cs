using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

public static class WorktreeContextMetadataKeys
{
    public const string OriginalWorkspacePath = "WorktreeContext.OriginalWorkspacePath";
    public const string ActiveWorktreePath = "WorktreeContext.ActiveWorktreePath";
    public const string EnteredAtUtc = "WorktreeContext.EnteredAtUtc";
}

public sealed class EnterWorktreeTool(
    CodexSessionManager sessionManager,
    ILogger<EnterWorktreeTool> logger) : ICodexTool
{
    public string Name => "enter_worktree";

    public string Description => "将当前 session 的 workspace 切换到指定 worktree/repo 子目录。参数: session_id, path 或 worktree_path 或 repo_path, allow_external?。默认仅允许当前 workspace 内路径；外部路径必须 allow_external=true 且是 Git 工作树。";

    public ToolCategory Category => ToolCategory.System;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 8_192);

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        _ = ct;
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return CodexToolResult.Error("Missing session_id.");
        }

        var requestedPath = arguments.GetValueOrDefault("path")?.ToString()
            ?? arguments.GetValueOrDefault("worktree_path")?.ToString()
            ?? arguments.GetValueOrDefault("repo_path")?.ToString();
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return CodexToolResult.Error("Missing path.");
        }

        var session = await sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
        var currentWorkspace = session.WorkspacePath;
        if (string.IsNullOrWhiteSpace(currentWorkspace) || !Directory.Exists(currentWorkspace))
        {
            return CodexToolResult.Error("Current session workspace does not exist.");
        }

        var target = ResolveTargetPath(currentWorkspace, requestedPath);
        if (!Directory.Exists(target))
        {
            return CodexToolResult.Error($"Target worktree does not exist: {requestedPath}");
        }

        var allowExternal = arguments.GetValueOrDefault("allow_external")?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
        var insideCurrentWorkspace = ToolPathResolver.IsWithinRoot(target, currentWorkspace);
        if (!insideCurrentWorkspace && (!allowExternal || !IsGitWorktree(target)))
        {
            return CodexToolResult.Error("External worktree requires allow_external=true and a valid Git working tree.");
        }

        var originalWorkspace = session.Metadata.TryGetValue(WorktreeContextMetadataKeys.OriginalWorkspacePath, out var original)
            && !string.IsNullOrWhiteSpace(original)
            ? original
            : currentWorkspace;

        session.WorkspacePath = target;
        session.Metadata[WorktreeContextMetadataKeys.OriginalWorkspacePath] = originalWorkspace;
        session.Metadata[WorktreeContextMetadataKeys.ActiveWorktreePath] = target;
        session.Metadata[WorktreeContextMetadataKeys.EnteredAtUtc] = DateTime.UtcNow.ToString("O");

        try
        {
            await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
            return CodexToolResult.Succeeded(
                $"WORKTREE_ENTERED\nworkspace_path: {target}\noriginal_workspace_path: {originalWorkspace}",
                new
                {
                    SessionId = session.Id,
                    WorkspacePath = target,
                    OriginalWorkspacePath = originalWorkspace,
                    External = !insideCurrentWorkspace
                },
                summary: $"entered worktree: {target}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(logger, ex, "enter_worktree failed for session {SessionId}", session.Id);
            return CodexToolResult.Error(ex.Message);
        }
    }

    private static string ResolveTargetPath(string currentWorkspace, string requestedPath)
    {
        var normalized = requestedPath.Trim().TrimStart('/', '\\');
        var target = Path.IsPathRooted(requestedPath)
            ? requestedPath
            : Path.Combine(currentWorkspace, ToolPathResolver.NormalizeDuplicateRepoPrefix(normalized, currentWorkspace));

        return Path.GetFullPath(target);
    }

    private static bool IsGitWorktree(string path)
        => Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));
}

public sealed class ExitWorktreeTool(
    CodexSessionManager sessionManager,
    ILogger<ExitWorktreeTool> logger) : ICodexTool
{
    public string Name => "exit_worktree";

    public string Description => "恢复 enter_worktree 之前的 session workspace。参数: session_id。";

    public ToolCategory Category => ToolCategory.System;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 8_192);

    public IReadOnlyList<int> AllowedStages => [0, 1, 2, 3, 4];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        _ = ct;
        ToolArgumentNormalizer.NormalizeInPlace(arguments);
        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return CodexToolResult.Error("Missing session_id.");
        }

        var session = await sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
        if (!session.Metadata.TryGetValue(WorktreeContextMetadataKeys.OriginalWorkspacePath, out var originalWorkspace) ||
            string.IsNullOrWhiteSpace(originalWorkspace))
        {
            return CodexToolResult.Error("No active worktree context.");
        }

        var previousWorkspace = session.WorkspacePath;
        session.WorkspacePath = originalWorkspace;
        session.Metadata.Remove(WorktreeContextMetadataKeys.OriginalWorkspacePath);
        session.Metadata.Remove(WorktreeContextMetadataKeys.ActiveWorktreePath);
        session.Metadata.Remove(WorktreeContextMetadataKeys.EnteredAtUtc);

        try
        {
            await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
            return CodexToolResult.Succeeded(
                $"WORKTREE_EXITED\nworkspace_path: {originalWorkspace}\nprevious_workspace_path: {previousWorkspace}",
                new
                {
                    SessionId = session.Id,
                    WorkspacePath = originalWorkspace,
                    PreviousWorkspacePath = previousWorkspace
                },
                summary: $"exited worktree: {originalWorkspace}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(logger, ex, "exit_worktree failed for session {SessionId}", session.Id);
            return CodexToolResult.Error(ex.Message);
        }
    }
}
