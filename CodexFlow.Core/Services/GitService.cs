using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CodexFlow.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Services;

public class GitService : IGitService
{
    private readonly ILogger<GitService> _logger;
    private const int MaxShadowSegmentLength = 24;

    public GitService(ILogger<GitService> logger)
    {
        _logger = logger;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunGitCommandAsync(string path, string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        return (process.ExitCode, output, error);
    }

    private static string QuoteGitArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static async Task EnableLongPathsIfSupportedAsync(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await RunGitCommandAsync(path, "config core.longpaths true").ConfigureAwait(false);
    }

    public async Task<bool> InitRepositoryAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (Directory.Exists(Path.Combine(path, ".git"))) return true;

        Directory.CreateDirectory(path); // 确保目录物理存在

        var result = await RunGitCommandAsync(path, "init").ConfigureAwait(false);
        if (result.ExitCode != 0) return false;

        await RunGitCommandAsync(path, "config user.name \"IvilsonAgent\"").ConfigureAwait(false);
        await RunGitCommandAsync(path, "config user.email \"agent@ivilson.com\"").ConfigureAwait(false);
        await EnableLongPathsIfSupportedAsync(path).ConfigureAwait(false);

        // Initial commit
        await File.WriteAllTextAsync(Path.Combine(path, ".gitignore"), ".venv/\nnode_modules/\nbin/\nobj/\n").ConfigureAwait(false);
        await RunGitCommandAsync(path, "add .").ConfigureAwait(false);
        await RunGitCommandAsync(path, "commit -m \"Initial workspace setup\"").ConfigureAwait(false);

        return true;
    }

    public async Task<bool> CloneRepositoryAsync(string url, string path, string? branch = null, string? githubToken = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(path);

        StructuredLog.Information(_logger, "Cloning repository: {Url} to {Path} (branch: {Branch})", url, path, branch ?? "default");

        // 如果目标路径已存在 .git，检查是否为有效的克隆仓库
        if (Directory.Exists(Path.Combine(path, ".git")))
        {
            // 检查是否有 remote origin（区分 git init 和 git clone）
            var remoteResult = await RunGitCommandAsync(path, "remote get-url origin").ConfigureAwait(false);
            if (remoteResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(remoteResult.Output))
            {
                var existingRemote = remoteResult.Output.Trim();
                // 匹配已有 remote 与请求 URL（兼容末尾斜杠差异）
                if (string.Equals(existingRemote, url, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(existingRemote, url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    // 同一仓库，检查是否有实际的项目文件（不只是 .git + .gitignore）
                    var projectFiles = Directory.GetFiles(path)
                        .Where(f => !Path.GetFileName(f).Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    if (projectFiles.Length > 0)
                    {
                        StructuredLog.Information(_logger, "Repository already cloned at {Path} (remote: {Remote}, {FileCount} project files)", path, existingRemote, projectFiles.Length);
                        return true;
                    }

                    StructuredLog.Warning(_logger, "Repository at {Path} has matching remote but no project files. Will re-clone.", path);
                }
                else
                {
                    StructuredLog.Warning(_logger, "Repository at {Path} has different remote ({Existing} vs {Requested}). Will re-clone.", path, existingRemote, url);
                }
            }
            else
            {
                StructuredLog.Warning(_logger, "Directory at {Path} has .git but no remote origin (likely from 'git init'). Will re-clone.", path);
            }

            // 删除旧目录以进行全新克隆
            StructuredLog.Information(_logger, "Removing stale git directory at {Path} for fresh clone", path);
            try
            {
                SetAttributesNormal(new DirectoryInfo(path));
                Directory.Delete(path, true);
            }
            catch (IOException ex)
            {
                StructuredLog.Error(_logger, ex, "Failed to remove stale directory at {Path}", path);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                StructuredLog.Error(_logger, ex, "Failed to remove stale directory at {Path}", path);
                return false;
            }
        }

        var parent = Directory.GetParent(path)!.FullName;
        Directory.CreateDirectory(parent); // 确保父目录存在

        StructuredLog.Debug(_logger, "Parent directory: {Parent}", parent);
        StructuredLog.Debug(_logger, "Target directory name: {DirName}", Path.GetFileName(path));

        var branchArg = !string.IsNullOrEmpty(branch) ? $"-b {branch}" : "";
        var cloneCommand = $"clone --depth 1 {branchArg} {url} {Path.GetFileName(path)}";
        var isGithubHttps = Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl)
            && string.Equals(parsedUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(parsedUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase);

        // 策略：先匿名克隆，仅当出现鉴权失败且提供了 token 时才带凭据重试
        StructuredLog.Debug(_logger, "Executing anonymous git clone: git {Command}", cloneCommand);
        var result = await RunGitCommandAsync(parent, cloneCommand).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            var isAuthFailure = IsAuthenticationFailure(result.Error, result.Output);

            if (isAuthFailure && isGithubHttps && !string.IsNullOrWhiteSpace(githubToken))
            {
                StructuredLog.Warning(_logger, "Anonymous clone failed with auth error — retrying with user's GitHub token.");

                // 清理匿名克隆可能留下的残留目录
                var staleTarget = Path.Combine(parent, Path.GetFileName(path));
                if (Directory.Exists(staleTarget))
                {
                    try { SetAttributesNormal(new DirectoryInfo(staleTarget)); Directory.Delete(staleTarget, true); }
                    catch (IOException) { /* best effort */ }
                    catch (UnauthorizedAccessException) { /* best effort */ }
                }

                var escapedToken = githubToken!.Replace("\"", "\\\"", StringComparison.Ordinal);
                var authenticatedCloneCommand = $"-c http.extraHeader=\"Authorization: Bearer {escapedToken}\" {cloneCommand}";
                result = await RunGitCommandAsync(parent, authenticatedCloneCommand).ConfigureAwait(false);
            }
            else if (isAuthFailure)
            {
                StructuredLog.Warning(_logger, "Anonymous clone failed with auth error and no GitHub token provided. Likely a private repository.");
            }
            else
            {
                StructuredLog.Warning(_logger, "Anonymous clone failed for non-auth reason. Error: {Error}", result.Error);
            }
        }

        if (result.ExitCode != 0)
        {
            StructuredLog.Error(_logger, "Git clone failed with exit code {ExitCode}. Error: {Error}. Output: {Output}",
                result.ExitCode, result.Error, result.Output);
            return false;
        }

        StructuredLog.Information(_logger, "Git clone succeeded. Output: {Output}", result.Output);

        // 验证克隆结果
        if (!Directory.Exists(path))
        {
            StructuredLog.Error(_logger, "Clone reported success but target directory does not exist: {Path}", path);
            return false;
        }

        var gitDir = Path.Combine(path, ".git");
        if (!Directory.Exists(gitDir))
        {
            StructuredLog.Error(_logger, "Clone reported success but .git directory does not exist: {GitDir}", gitDir);
            return false;
        }

        // 配置 git 用户信息
        await RunGitCommandAsync(path, "config user.name \"IvilsonAgent\"").ConfigureAwait(false);
        await RunGitCommandAsync(path, "config user.email \"agent@ivilson.com\"").ConfigureAwait(false);
        await EnableLongPathsIfSupportedAsync(path).ConfigureAwait(false);

        StructuredLog.Information(_logger, "Repository cloned and configured successfully at {Path}", path);

        return true;
    }

    public Task<bool> CloneRepositoryAsync(Uri url, string path, string? branch = null, string? githubToken = null)
    {
        ArgumentNullException.ThrowIfNull(url);

        return CloneRepositoryAsync(url.ToString(), path, branch, githubToken);
    }

    /// <summary>
    /// 检测 git 输出是否包含鉴权失败的特征关键字。
    /// </summary>
    private static bool IsAuthenticationFailure(string? error, string? output)
    {
        var text = string.Concat(error ?? string.Empty, "\n", output ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var authMarkers = new[]
        {
            "authentication failed",
            "could not read username",
            "repository not found",
            "permission denied",
            "access denied",
            "http basic: access denied",
            "403",
            "401"
        };

        return authMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> CreateBranchAsync(string path, string branchName, string? startPoint = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(branchName);

        var startArg = !string.IsNullOrEmpty(startPoint) ? startPoint : "";
        var result = await RunGitCommandAsync(path, $"checkout -b {branchName} {startArg}").ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task<string?> GetHeadHashAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var result = await RunGitCommandAsync(path, "rev-parse HEAD").ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            return null;
        return result.Output.Trim();
    }

    public async Task<string?> GetCurrentBranchAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var result = await RunGitCommandAsync(path, "rev-parse --abbrev-ref HEAD").ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            return null;

        var branch = result.Output.Trim();
        return string.Equals(branch, "HEAD", StringComparison.OrdinalIgnoreCase) ? null : branch;
    }

    public async Task<bool?> HasUncommittedChangesAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var result = await RunGitCommandAsync(path, "status --porcelain").ConfigureAwait(false);
        if (result.ExitCode != 0)
            return null;

        return !string.IsNullOrWhiteSpace(result.Output);
    }

    public async Task<string?> CreateShadowWorktreeAsync(string mainPath, string taskId, string baseBranch)
    {
        ArgumentNullException.ThrowIfNull(mainPath);
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(baseBranch);

        var shadowPath = BuildShadowPath(mainPath, taskId);
        var branchName = $"task-{taskId}";

        // 1. 确保影子目录的父目录存在
        Directory.CreateDirectory(Path.GetDirectoryName(shadowPath)!);

        // 2. 检测实际的基础分支名（git init 可能用 master 而不是 main）
        var actualBase = baseBranch;
        var headResult = await RunGitCommandAsync(mainPath, "rev-parse --abbrev-ref HEAD").ConfigureAwait(false);
        if (headResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(headResult.Output))
        {
            actualBase = headResult.Output.Trim();
            if (actualBase != baseBranch)
            {
                StructuredLog.Information(_logger, "Adjusted baseBranch from '{Requested}' to '{Actual}'", baseBranch, actualBase);
            }
        }

        // 3. 检查工作树是否已存在
        if (Directory.Exists(shadowPath))
        {
            StructuredLog.Information(_logger, "Shadow path {Path} already exists. Attempting to prune or reuse.", shadowPath);
            await RunGitCommandAsync(mainPath, $"worktree prune").ConfigureAwait(false);
            // 如果目录还在，尝试物理删除
            if (Directory.Exists(shadowPath))
            {
                try { Directory.Delete(shadowPath, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        await RemoveAssociatedShadowWorktreesAsync(mainPath, shadowPath, branchName).ConfigureAwait(false);

        // 4. 每次任务都从最新基线重建 task 分支，避免复用旧分支导致历史漂移
        var branchDelete = await DeleteShadowBranchAsync(mainPath, branchName).ConfigureAwait(false);
        if (branchDelete.ExitCode != 0)
        {
            var err = (branchDelete.Error ?? string.Empty) + (branchDelete.Output ?? string.Empty);
            if (!err.Contains("not found", StringComparison.OrdinalIgnoreCase)
                && !err.Contains("not a valid branch", StringComparison.OrdinalIgnoreCase)
                && !err.Contains("cannot delete branch", StringComparison.OrdinalIgnoreCase))
            {
                StructuredLog.Warning(_logger, "Failed to delete stale task branch {Branch}: {Error}", branchName, err.Trim());
            }
        }

        await EnableLongPathsIfSupportedAsync(mainPath).ConfigureAwait(false);

        var worktreeCmd = $"worktree add -b {QuoteGitArgument(branchName)} {QuoteGitArgument(shadowPath)} {QuoteGitArgument(actualBase)}";

        // 5. 使用 git worktree add
        var result = await RunGitCommandAsync(mainPath, worktreeCmd).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            StructuredLog.Warning(_logger, "git worktree add failed (exit {Code}): {Err}", result.ExitCode, result.Output + result.Error);
        }

        return result.ExitCode == 0 ? shadowPath : null;
    }

    public async Task<bool> RemoveShadowWorktreeAsync(string mainPath, string taskId)
    {
        ArgumentNullException.ThrowIfNull(mainPath);
        ArgumentNullException.ThrowIfNull(taskId);

        var shadowPath = BuildShadowPath(mainPath, taskId);
        var branchName = $"task-{taskId}";

        // 1. 先從 git 中移除 worktree 記錄
        var result = await RunGitCommandAsync(mainPath, $"worktree remove {QuoteGitArgument(shadowPath)} --force").ConfigureAwait(false);

        // 2. 物理刪除殘留目錄 (如果 force remove 沒刪乾淨，處理只讀屬性和進程衝突)
        if (Directory.Exists(shadowPath))
        {
            try
            {
                SetAttributesNormal(new DirectoryInfo(shadowPath));
                Directory.Delete(shadowPath, true);
            }
            catch (IOException ex)
            {
                StructuredLog.Warning(_logger, "物理刪除影子路徑失敗（可能文件被鎖定）: {Path}, {Msg}", shadowPath, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                StructuredLog.Warning(_logger, "物理刪除影子路徑失敗（可能文件被鎖定）: {Path}, {Msg}", shadowPath, ex.Message);
            }
        }

        var branchDelete = await DeleteShadowBranchAsync(mainPath, branchName).ConfigureAwait(false);
        if (branchDelete.ExitCode != 0)
        {
            var err = (branchDelete.Error ?? string.Empty) + (branchDelete.Output ?? string.Empty);
            if (!err.Contains("not found", StringComparison.OrdinalIgnoreCase)
                && !err.Contains("not a valid branch", StringComparison.OrdinalIgnoreCase)
                && !err.Contains("branch", StringComparison.OrdinalIgnoreCase))
            {
                StructuredLog.Warning(_logger, "清理影子分支失败: {Branch}, {Error}", branchName, err.Trim());
            }
        }

        return result.ExitCode == 0;
    }

    private async Task RemoveAssociatedShadowWorktreesAsync(string mainPath, string shadowPath, string branchName)
    {
        var entries = await ListWorktreesAsync(mainPath).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            return;
        }

        var mainPathFull = Path.GetFullPath(mainPath);
        var shadowPathFull = Path.GetFullPath(shadowPath);

        foreach (var entry in entries)
        {
            var entryPathFull = Path.GetFullPath(entry.Path);
            var matchesPath = string.Equals(entryPathFull, shadowPathFull, StringComparison.OrdinalIgnoreCase);
            var matchesBranch = string.Equals(entry.Branch, branchName, StringComparison.OrdinalIgnoreCase);
            if ((!matchesPath && !matchesBranch) || string.Equals(entryPathFull, mainPathFull, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            StructuredLog.Information(_logger, "Removing stale shadow worktree {Path} for branch {Branch}", entry.Path, entry.Branch);
            await RunGitCommandAsync(mainPath, $"worktree remove {QuoteGitArgument(entry.Path)} --force").ConfigureAwait(false);

            if (Directory.Exists(entry.Path))
            {
                try
                {
                    SetAttributesNormal(new DirectoryInfo(entry.Path));
                    Directory.Delete(entry.Path, true);
                }
                catch (IOException ex)
                {
                    StructuredLog.Warning(_logger, "Failed to delete stale shadow directory {Path}: {Message}", entry.Path, ex.Message);
                }
                catch (UnauthorizedAccessException ex)
                {
                    StructuredLog.Warning(_logger, "Failed to delete stale shadow directory {Path}: {Message}", entry.Path, ex.Message);
                }
            }
        }

        await RunGitCommandAsync(mainPath, "worktree prune").ConfigureAwait(false);
    }

    private async Task<(int ExitCode, string Output, string Error)> DeleteShadowBranchAsync(string mainPath, string branchName)
    {
        await RunGitCommandAsync(mainPath, "worktree prune").ConfigureAwait(false);
        var branchDelete = await RunGitCommandAsync(mainPath, $"branch -D {branchName}").ConfigureAwait(false);
        if (branchDelete.ExitCode == 0)
        {
            return branchDelete;
        }

        await RemoveAssociatedShadowWorktreesAsync(mainPath, BuildShadowPath(mainPath, branchName["task-".Length..]), branchName).ConfigureAwait(false);
        return await RunGitCommandAsync(mainPath, $"branch -D {branchName}").ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<GitWorktreeEntry>> ListWorktreesAsync(string mainPath)
    {
        var result = await RunGitCommandAsync(mainPath, "worktree list --porcelain").ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            return [];
        }

        var entries = new List<GitWorktreeEntry>();
        string? currentPath = null;
        string? currentBranch = null;
        using var reader = new StringReader(result.Output);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (!string.IsNullOrWhiteSpace(currentPath))
                {
                    entries.Add(new GitWorktreeEntry(currentPath, currentBranch));
                }

                currentPath = null;
                currentBranch = null;
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                currentPath = line["worktree ".Length..].Trim();
                continue;
            }

            if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var branchRef = line["branch ".Length..].Trim();
                currentBranch = branchRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? branchRef["refs/heads/".Length..]
                    : branchRef;
            }
        }

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            entries.Add(new GitWorktreeEntry(currentPath, currentBranch));
        }

        return entries;
    }

    private sealed record GitWorktreeEntry(string Path, string? Branch);

    private static string BuildShadowPath(string mainPath, string taskId)
    {
        var repoName = Path.GetFileName(mainPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(repoName)) repoName = "workspace";

        var shadowRoot = ResolveShadowRoot(mainPath);
        var repoSegment = SanitizeShadowSegment(repoName, "workspace");
        var taskSegment = SanitizeShadowSegment(taskId, "task");

        return Path.Combine(shadowRoot, "shadows", repoSegment, taskSegment);
    }

    private static string ResolveShadowRoot(string mainPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var workspaceRoot = Path.GetPathRoot(Path.GetFullPath(mainPath));
            if (!string.IsNullOrWhiteSpace(workspaceRoot))
            {
                return Path.Combine(workspaceRoot, "cfw");
            }
        }

        return Path.Combine(Path.GetTempPath(), "codexflow-shadow-root");
    }

    private static string SanitizeShadowSegment(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch);
        }

        var sanitized = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = fallback;
        }

        if (sanitized.Length <= MaxShadowSegmentLength)
        {
            return sanitized;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sanitized)))[..8];
        var prefixLength = Math.Max(8, MaxShadowSegmentLength - hash.Length - 1);
        return $"{sanitized[..prefixLength]}-{hash}";
    }

    public async Task<bool> CommitAsync(string path, string message)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(message);

        await RunGitCommandAsync(path, "add .").ConfigureAwait(false);
        var result = await RunGitCommandAsync(path, $"commit -m \"{message.Replace("\"", "\\\"", StringComparison.Ordinal)}\"").ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task<bool> MergeAsync(string path, string sourceBranch, string targetBranch = "main")
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sourceBranch);
        ArgumentNullException.ThrowIfNull(targetBranch);

        // 策略升级：以影子分支内容直接覆盖目标分支，避免 merge 冲突阻塞流水线
        var checkout = await RunGitCommandAsync(path, $"checkout {targetBranch}").ConfigureAwait(false);
        if (checkout.ExitCode != 0)
        {
            StructuredLog.Warning(_logger, "Failed to checkout target branch {Target}: {Error}", targetBranch, checkout.Error);
            return false;
        }

        // 清理可能残留的未完成合并状态
        await RunGitCommandAsync(path, "merge --abort").ConfigureAwait(false);

        var reset = await RunGitCommandAsync(path, $"reset --hard {sourceBranch}").ConfigureAwait(false);
        if (reset.ExitCode != 0)
        {
            StructuredLog.Warning(_logger, "Failed to replace branch {Target} with {Source}: {Error}", targetBranch, sourceBranch, reset.Error);
            return false;
        }

        await RunGitCommandAsync(path, "clean -fd").ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RevertToLastCommitAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var result = await RunGitCommandAsync(path, "reset --hard HEAD").ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task<bool> ResetToCommitAsync(string path, string commitHash)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitHash);

        var result = await RunGitCommandAsync(path, $"reset --hard {commitHash}").ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task<string?> GetRemoteUrlAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!Directory.Exists(Path.Combine(path, ".git")))
            return null;

        var result = await RunGitCommandAsync(path, "remote get-url origin").ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            return null;

        return result.Output.Trim();
    }

    public async Task<string> GetDiffSummaryAsync(string path, string fromBranch, string toBranch)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(fromBranch);
        ArgumentNullException.ThrowIfNull(toBranch);

        var result = await RunGitCommandAsync(path, $"diff --stat {fromBranch}..{toBranch}").ConfigureAwait(false);
        return result.Output;
    }

    public async Task<IReadOnlyList<GitFileChange>> GetChangedFilesAsync(string path, string fromBranch, string toBranch)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(fromBranch);
        ArgumentNullException.ThrowIfNull(toBranch);

        var range = $"{fromBranch}..{toBranch}";
        var nameStatusResult = await RunGitCommandAsync(path, $"diff --name-status {range}").ConfigureAwait(false);
        if (nameStatusResult.ExitCode != 0 || string.IsNullOrWhiteSpace(nameStatusResult.Output))
            return Array.Empty<GitFileChange>();

        var statsByPath = new Dictionary<string, (int Additions, int Deletions)>(StringComparer.OrdinalIgnoreCase);
        var numstatResult = await RunGitCommandAsync(path, $"diff --numstat {range}").ConfigureAwait(false);
        if (numstatResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(numstatResult.Output))
        {
            foreach (var statRaw in numstatResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var statParts = statRaw.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (statParts.Length < 3) continue;

                var filePath = statParts[2].Trim();
                if (string.IsNullOrWhiteSpace(filePath)) continue;

                var additions = int.TryParse(statParts[0], out var add) ? add : 0;
                var deletions = int.TryParse(statParts[1], out var del) ? del : 0;
                statsByPath[filePath] = (additions, deletions);
            }
        }

        // 格式: "M\tpath/to/file" or "A\tpath/to/file" or "R100\told\tnew"
        var changes = new List<GitFileChange>();
        foreach (var raw in nameStatusResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var statusCode = parts[0].TrimStart(); // e.g. "M", "A", "D", "R100"
            var status = statusCode.Length > 0 ? statusCode[0].ToString() : "M"; // take first char
            var filePath = parts.Length >= 3 ? parts[2] : parts[1]; // for renames use new path
            if (!string.IsNullOrEmpty(filePath))
            {
                var normalizedPath = filePath.Trim();
                var stat = statsByPath.TryGetValue(normalizedPath, out var s) ? s : (0, 0);
                changes.Add(new GitFileChange(normalizedPath, status, stat.Item1, stat.Item2));
            }
        }
        return changes.AsReadOnly();
    }

    public async Task<IReadOnlyList<GitFileChange>> GetWorkingTreeChangedFilesAsync(string path, string baseRef)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(baseRef);

        var changes = new List<GitFileChange>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. git diff --name-status {baseRef}: captures tracked file changes (modified/deleted/renamed).
        //    Compares the working tree (staged + unstaged) against the base ref.
        var nameStatusResult = await RunGitCommandAsync(path, $"diff --name-status {baseRef}").ConfigureAwait(false);
        if (nameStatusResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(nameStatusResult.Output))
        {
            var statsByPath = new Dictionary<string, (int Additions, int Deletions)>(StringComparer.OrdinalIgnoreCase);
            var numstatResult = await RunGitCommandAsync(path, $"diff --numstat {baseRef}").ConfigureAwait(false);
            if (numstatResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(numstatResult.Output))
            {
                foreach (var statRaw in numstatResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var statParts = statRaw.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
                    if (statParts.Length < 3) continue;

                    var filePath = statParts[2].Trim();
                    if (string.IsNullOrWhiteSpace(filePath)) continue;

                    var additions = int.TryParse(statParts[0], out var add) ? add : 0;
                    var deletions = int.TryParse(statParts[1], out var del) ? del : 0;
                    statsByPath[filePath] = (additions, deletions);
                }
            }

            foreach (var raw in nameStatusResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = raw.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var statusCode = parts[0].TrimStart();
                var status = statusCode.Length > 0 ? statusCode[0].ToString() : "M";
                var filePath = parts.Length >= 3 ? parts[2] : parts[1];
                if (!string.IsNullOrEmpty(filePath))
                {
                    var normalizedPath = filePath.Trim();
                    var stat = statsByPath.TryGetValue(normalizedPath, out var s) ? s : (0, 0);
                    changes.Add(new GitFileChange(normalizedPath, status, stat.Item1, stat.Item2));
                    seenPaths.Add(normalizedPath);
                }
            }
        }

        // 2. git ls-files --others --exclude-standard: captures untracked new files.
        //    These are files created by Forge but not yet staged with `git add`, so they are
        //    invisible to `git diff` and would otherwise escape the security audit.
        var untrackedResult = await RunGitCommandAsync(path, "ls-files --others --exclude-standard").ConfigureAwait(false);
        if (untrackedResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(untrackedResult.Output))
        {
            foreach (var raw in untrackedResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var filePath = raw.Trim();
                if (string.IsNullOrEmpty(filePath) || seenPaths.Contains(filePath)) continue;
                changes.Add(new GitFileChange(filePath, "A", 0, 0));
                seenPaths.Add(filePath);
            }
        }

        return changes.AsReadOnly();
    }

    public async Task<string?> GetFileAtRefAsync(string path, string relativePath, string gitRef = "HEAD~1")
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(gitRef);

        // normalize path separators for git
        var gitPath = relativePath.Replace('\\', '/');
        var result = await RunGitCommandAsync(path, $"show {gitRef}:{gitPath}").ConfigureAwait(false);
        if (result.ExitCode != 0) return null;
        return result.Output;
    }

    public async Task<bool> ApplyPatchAsync(string path, string patchContent)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(patchContent);

        var tempPatchFile = Path.Combine(Path.GetTempPath(), $"patch_{Guid.NewGuid()}.diff");
        try
        {
            var normalizedPatch = patchContent.EndsWith('\n')
                ? patchContent
                : patchContent + "\n";
            await File.WriteAllTextAsync(tempPatchFile, normalizedPatch).ConfigureAwait(false);
            var quotedPatchFile = $"\"{tempPatchFile}\"";
            var checkResult = await RunGitCommandAsync(path, $"apply --check --recount --whitespace=nowarn {quotedPatchFile}").ConfigureAwait(false);
            if (checkResult.ExitCode != 0)
            {
                StructuredLog.Warning(_logger, "Git apply --check failed: {Error}", checkResult.Error);
                return false;
            }

            // 使用 --3way 可以在有微小偏移时自动合并。
            var result = await RunGitCommandAsync(path, $"apply --3way --recount --whitespace=nowarn {quotedPatchFile}").ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                StructuredLog.Warning(_logger, "Git apply failed: {Error}", result.Error);

                if ((result.Error ?? string.Empty).Contains("repository lacks the necessary blob to perform 3-way merge", StringComparison.OrdinalIgnoreCase))
                {
                    StructuredLog.Warning(_logger, "Git apply --3way lacked blob metadata. Falling back to direct application.");
                    var directResult = await RunGitCommandAsync(path, $"apply --recount --whitespace=nowarn {quotedPatchFile}").ConfigureAwait(false);
                    if (directResult.ExitCode != 0)
                    {
                        StructuredLog.Warning(_logger, "Git apply direct fallback failed: {Error}", directResult.Error);
                    }

                    return directResult.ExitCode == 0;
                }
            }

            return result.ExitCode == 0;
        }
        finally
        {
            if (File.Exists(tempPatchFile)) File.Delete(tempPatchFile);
        }
    }

    public async Task<bool> PushAsync(string path, string branchName)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(branchName);

        var remoteCheck = await RunGitCommandAsync(path, "remote get-url origin").ConfigureAwait(false);
        if (remoteCheck.ExitCode != 0 || string.IsNullOrWhiteSpace(remoteCheck.Output))
        {
            StructuredLog.Information(_logger, "Skip push: repository has no remote 'origin'.");
            return true;
        }

        var targetBranch = (branchName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(targetBranch))
        {
            StructuredLog.Warning(_logger, "Skip push: target branch name is empty.");
            return true;
        }

        // 将当前 HEAD 显式保存到目标分支（创建或更新），确保“先保存为分支，再推送”
        var saveBranchResult = await RunGitCommandAsync(path, $"branch -f {targetBranch} HEAD").ConfigureAwait(false);
        if (saveBranchResult.ExitCode != 0)
        {
            StructuredLog.Warning(_logger, "Failed to save HEAD to branch {Branch}: {Error}", targetBranch, saveBranchResult.Error);
            return false;
        }

        StructuredLog.Information(_logger, "Pushing branch {Branch} to origin...", targetBranch);
        var result = await RunGitCommandAsync(path, $"push origin {targetBranch}").ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            StructuredLog.Warning(_logger, "Git push failed: {Error}", result.Error);
        }
        return result.ExitCode == 0;
    }

    /// <summary>
    /// 递归将目录下所有文件属性设为 Normal，以便删除 .git 中的只读文件。
    /// </summary>
    private static void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories())
        {
            SetAttributesNormal(subDir);
        }
        foreach (var file in dir.GetFiles())
        {
            file.Attributes = FileAttributes.Normal;
        }
    }
}

