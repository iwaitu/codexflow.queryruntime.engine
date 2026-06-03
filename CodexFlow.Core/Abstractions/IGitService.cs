namespace CodexFlow.Core.Abstractions;

/// <summary>单个文件的 git 变更记录</summary>
public record GitFileChange(string FilePath, string Status, int Additions = 0, int Deletions = 0);

public interface IGitService
{
    Task<bool> InitRepositoryAsync(string path);
    Task<bool> CloneRepositoryAsync(Uri url, string path, string? branch = null, string? githubToken = null);
    Task<bool> CloneRepositoryAsync(string url, string path, string? branch = null, string? githubToken = null);
    Task<bool> CreateBranchAsync(string path, string branchName, string? startPoint = null);
    Task<bool> CommitAsync(string path, string message);
    Task<bool> MergeAsync(string path, string sourceBranch, string targetBranch = "main");
    Task<bool> RevertToLastCommitAsync(string path);
    Task<string> GetDiffSummaryAsync(string path, string fromBranch, string toBranch);
    Task<bool> ApplyPatchAsync(string path, string patchContent);
    Task<bool> PushAsync(string path, string branchName);

    /// <summary>
    /// 获取仓库的 remote origin URL。如果不存在则返回 null。
    /// </summary>
    Task<string?> GetRemoteUrlAsync(string path);

    /// <summary>
    /// 获取两个分支之间变更的文件列表（相对路径），附带变更状态：A=新增, M=修改, D=删除, R=重命名。
    /// </summary>
    Task<IReadOnlyList<GitFileChange>> GetChangedFilesAsync(string path, string fromBranch, string toBranch);

    /// <summary>
    /// 获取工作树相对于指定 baseRef 的变更文件列表，包含：
    /// (1) git diff --name-status {baseRef} — staged/unstaged 的已跟踪文件变更；
    /// (2) git ls-files --others --exclude-standard — 新建但尚未 git add 的 untracked 文件。
    /// 两者合并确保 Forge 阶段新建的文件不会逃逸安全审计。
    /// </summary>
    Task<IReadOnlyList<GitFileChange>> GetWorkingTreeChangedFilesAsync(string path, string baseRef);

    /// <summary>
    /// 获取指定文件在某个 commit/branch 中的内容；若不存在则返回 null。
    /// </summary>
    Task<string?> GetFileAtRefAsync(string path, string relativePath, string gitRef = "HEAD~1");

    /// <summary>
    /// 获取当前 HEAD 的完整 commit hash。
    /// </summary>
    Task<string?> GetHeadHashAsync(string path);

    /// <summary>
    /// 获取当前分支名；若处于 detached HEAD 或失败则返回 null。
    /// </summary>
    Task<string?> GetCurrentBranchAsync(string path);

    /// <summary>
    /// 检测工作区是否存在未提交变更。
    /// </summary>
    Task<bool?> HasUncommittedChangesAsync(string path);

    /// <summary>
    /// 将工作区硬重置到指定 commit hash，丢弃该 commit 之后的所有变更。
    /// 用于非影子模式下 scope guard 失败时回滚 agent 的所有提交。
    /// </summary>
    Task<bool> ResetToCommitAsync(string path, string commitHash);

    // 影子路径支持 (Worktree)
    Task<string?> CreateShadowWorktreeAsync(string mainPath, string taskId, string baseBranch);
    Task<bool> RemoveShadowWorktreeAsync(string mainPath, string taskId);
}
