using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexFlow.Core.Agents;

public class GitWorkspaceTool : ICodexTool
{
    private readonly IGitService _gitService;
    private readonly ILogger<GitWorkspaceTool> _logger;

    public string Name { get; }
    public string Description { get; }
    public ToolCategory Category => ToolCategory.System;
    public ToolExecutionMetadata Metadata => Name switch
    {
        "git_clone" => new ToolExecutionMetadata(
            IsConcurrencySafe: false,
            IsReadOnly: false,
            IsDestructive: false,
            InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
            ResultSizeSoftLimitChars: 12_288),
        "openspec_create_checkpoint" => new ToolExecutionMetadata(
            IsConcurrencySafe: false,
            IsReadOnly: false,
            IsDestructive: false,
            InterruptBehavior: ToolInterruptBehavior.CancelSafe,
            ResultSizeSoftLimitChars: 8_192),
        "openspec_revert_changes" => new ToolExecutionMetadata(
            IsConcurrencySafe: false,
            IsReadOnly: false,
            IsDestructive: true,
            InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
            ResultSizeSoftLimitChars: 8_192),
        _ => ToolExecutionMetadata.ForCategory(Category)
    };
    public IReadOnlyList<int> AllowedStages { get; }

    public GitWorkspaceTool(string name, string description, IReadOnlyList<int> allowedStages, IGitService gitService, ILogger<GitWorkspaceTool> logger)
    {
        Name = name;
        Description = description;
        AllowedStages = allowedStages;
        _gitService = gitService;
        _logger = logger;
    }

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var workspacePath = arguments.TryGetValue("workspace_path", out object? value) ? value?.ToString() : null;
        if (string.IsNullOrEmpty(workspacePath))
        {
            return CodexToolResult.Error("No workspace_path provided. Cannot perform Git operation.");
        }

        try
        {
            switch (Name)
            {
                case "openspec_revert_changes":
                    var revertTarget = ResolveGitTargetPath(workspacePath, arguments, out var revertError);
                    if (string.IsNullOrEmpty(revertTarget))
                        return CodexToolResult.Error(revertError ?? "无法确定 Git 仓库路径。请提供 repo_name 或 repo_path。");

                    var revSuccess = await _gitService.RevertToLastCommitAsync(revertTarget).ConfigureAwait(false);
                    return revSuccess
                        ? CodexToolResult.Succeeded($"✅ 撤销成功：仓库 [{Path.GetFileName(revertTarget)}] 已恢复到最近一个健康提交。", new { Action = "Revert", RepoPath = revertTarget })
                        : CodexToolResult.Error("❌ 撤销失败：无法执行回滚操作，请检查 Git 仓库状态。");

                case "openspec_create_checkpoint":
                    var message = arguments.TryGetValue("reason", out var reasonValue) ? reasonValue?.ToString() : "Manual checkpoint";
                    var checkpointTarget = ResolveGitTargetPath(workspacePath, arguments, out var checkpointError);
                    if (string.IsNullOrEmpty(checkpointTarget))
                        return CodexToolResult.Error(checkpointError ?? "无法确定 Git 仓库路径。请提供 repo_name 或 repo_path。");

                    var cpSuccess = await _gitService.CommitAsync(checkpointTarget, $"[CHECKPOINT] {message}").ConfigureAwait(false);
                    return cpSuccess
                        ? CodexToolResult.Succeeded($"✅ 快照创建成功：仓库 [{Path.GetFileName(checkpointTarget)}] 已记录当前状态。备注：{message}", new { Action = "Checkpoint", Message = message, RepoPath = checkpointTarget })
                        : CodexToolResult.Error("❌ 快照创建失败：可能是没有检测到任何文件变动。");

                case "git_clone":
                    var repoUrl = arguments.TryGetValue("url", out var urlValue) ? urlValue?.ToString() : null;
                    if (string.IsNullOrEmpty(repoUrl)) return CodexToolResult.Error("Missing 'url' parameter for git_clone.");
                    var githubToken = arguments.TryGetValue("github_token", out var tokenValue) ? tokenValue?.ToString() : null;

                    var requestedFolder = arguments.TryGetValue("folder", out var folderValue) ? folderValue?.ToString() : null;
                    var normalizedFolder = NormalizeRepoFolderName(requestedFolder);
                    if (string.IsNullOrWhiteSpace(normalizedFolder))
                    {
                        normalizedFolder = NormalizeRepoFolderName(ExtractRepoNameFromUrl(repoUrl));
                    }
                    if (string.IsNullOrWhiteSpace(normalizedFolder))
                    {
                        normalizedFolder = $"repo-{DateTime.UtcNow:yyyyMMddHHmmss}";
                    }

                    var targetPath = GetUniqueTargetPath(workspacePath, normalizedFolder);
                    StructuredLog.Information(_logger, "Cloning repository {Url} to {Path}", repoUrl, targetPath);

                    // 策略：先匿名克隆 → 鉴权失败时带 token 重试 → 无 token 则提示用户
                    var repoUri = CodexSession.CreateProjectUri(repoUrl);
                    if (repoUri == null)
                    {
                        return CodexToolResult.Error($"Missing or invalid 'url' parameter for git_clone: {repoUrl}");
                    }

                    var cloneSuccess = await _gitService.CloneRepositoryAsync(repoUri, targetPath, null, githubToken).ConfigureAwait(false);

                    if (!cloneSuccess)
                    {
                        StructuredLog.Error(_logger, "Failed to clone repository {Url} to {Path}", repoUrl, targetPath);

                        // 判断用户是否有 token：如果有 token 但仍失败，说明 token 无效或网络问题
                        // 如果无 token，可能是私有仓库需要授权
                        if (string.IsNullOrWhiteSpace(githubToken))
                        {
                            return CodexToolResult.Error(
                                $"❌ 仓库克隆失败。\n" +
                                $"仓库 URL: {repoUrl}\n" +
                                $"可能原因：该仓库为私有仓库，需要授权才能访问。\n" +
                                $"请用户在设置中配置 GitHub Token（Personal Access Token），然后重新尝试。\n" +
                                $"如果是 Public 仓库，请检查：1) Git 是否已安装；2) URL 是否正确；3) 网络是否正常。");
                        }
                        else
                        {
                            return CodexToolResult.Error(
                                $"❌ 仓库克隆失败。\n" +
                                $"仓库 URL: {repoUrl}\n" +
                                $"已尝试使用用户的 GitHub Token 进行认证克隆，仍然失败。\n" +
                                $"请检查：1) Token 是否有效且拥有 repo 权限；2) 仓库 URL 是否正确；3) 网络是否正常。");
                        }
                    }

                    // 验证克隆结果
                    if (!Directory.Exists(targetPath))
                    {
                        StructuredLog.Error(_logger, "Clone reported success but directory does not exist: {Path}", targetPath);
                        return CodexToolResult.Error($"❌ 克隆过程异常：虽然 Git 命令返回成功，但目标目录 {targetPath} 不存在。");
                    }

                    var gitFolder = Path.Combine(targetPath, ".git");
                    if (!Directory.Exists(gitFolder))
                    {
                        StructuredLog.Error(_logger, "Clone reported success but .git folder does not exist: {Path}", gitFolder);
                        return CodexToolResult.Error($"❌ 克隆不完整：目录存在但缺少 .git 文件夹。目标路径：{targetPath}");
                    }

                    // 统计克隆的文件（排除 .git 内部文件）
                    var fileCount = Directory.GetFiles(targetPath, "*.*", SearchOption.AllDirectories)
                        .Count(f => !f.Replace('\\', '/').Contains("/.git/", StringComparison.Ordinal)
                            && !f.Replace('\\', '/').Contains("\\.git\\", StringComparison.Ordinal));
                    var dirCount = Directory.GetDirectories(targetPath, "*", SearchOption.AllDirectories)
                        .Count(d => !d.Replace('\\', '/').Contains("/.git", StringComparison.Ordinal)
                            && !Path.GetFileName(d).Equals(".git", StringComparison.Ordinal));

                    if (fileCount == 0)
                    {
                        StructuredLog.Error(_logger, "Clone completed but no project files found (only .git internals) at {Path}", targetPath);
                        return CodexToolResult.Error($"❌ 仓库克隆后没有项目文件。仓库可能为空或克隆不完整。目标路径：{targetPath}");
                    }

                    StructuredLog.Information(_logger, "Repository cloned successfully: {FileCount} files, {DirCount} directories", fileCount, dirCount);

                    return CodexToolResult.Succeeded(
                        $"✅ 仓库已成功拉取至 {targetPath}\n" +
                        $"- 项目目录: {Path.GetFileName(targetPath)}\n" +
                        $"- 文件数: {fileCount}\n" +
                        $"- 目录数: {dirCount}\n" +
                        $"- 仓库 URL: {repoUrl}\n" +
                        $"请使用 ivilson_ls 工具查看项目结构。",
                        new
                        {
                            Action = "Clone",
                            Url = repoUrl,
                            Path = targetPath,
                            ProjectFolder = Path.GetFileName(targetPath),
                            FileCount = fileCount,
                            DirectoryCount = dirCount
                        });

                default:
                    return CodexToolResult.Error($"Unknown tool {Name}");
            }
        }
        catch (IOException ex)
        {
            StructuredLog.Error(_logger, ex, "Git tool execution failed: {ToolName}", Name);
            return CodexToolResult.Error($"Error executing Git tool: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(_logger, ex, "Git tool execution failed: {ToolName}", Name);
            return CodexToolResult.Error($"Error executing Git tool: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            StructuredLog.Error(_logger, ex, "Git tool execution failed: {ToolName}", Name);
            return CodexToolResult.Error($"Error executing Git tool: {ex.Message}");
        }
    }

    private static string? ExtractRepoNameFromUrl(string repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl)) return null;
        var cleaned = repoUrl.Trim().TrimEnd('/');
        var last = cleaned.Split('/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(last)) return null;
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            last = last[..^4];
        return last;
    }

    private static string NormalizeRepoFolderName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var candidate = raw.Trim().Replace('\\', '-').Replace('/', '-');
        candidate = Regex.Replace(candidate, "[^a-zA-Z0-9._-]", "-");
        candidate = candidate.Trim('.', '-', ' ');
        return candidate;
    }

    private static string GetUniqueTargetPath(string workspacePath, string folderName)
    {
        var basePath = Path.Combine(workspacePath, folderName);
        if (!Directory.Exists(basePath) && !File.Exists(basePath))
            return basePath;

        var index = 2;
        while (true)
        {
            var candidate = Path.Combine(workspacePath, $"{folderName}-{index}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                return candidate;
            index++;
        }
    }

    private static string? ResolveGitTargetPath(string workspacePath, Dictionary<string, object?> arguments, out string? error)
    {
        error = null;

        // 1) Explicit repo_path (relative to workspace or absolute within workspace)
        var repoPathArg = arguments.TryGetValue("repo_path", out var repoPathValue) ? repoPathValue?.ToString() : null;
        if (!string.IsNullOrWhiteSpace(repoPathArg))
        {
            var candidate = BuildCandidatePath(workspacePath, repoPathArg!);
            if (!IsPathInsideWorkspace(candidate, workspacePath))
            {
                error = "repo_path 超出工作区范围。";
                return null;
            }
            if (!Directory.Exists(Path.Combine(candidate, ".git")))
            {
                error = $"指定 repo_path 不是 Git 仓库：{repoPathArg}";
                return null;
            }
            return candidate;
        }

        // 2) Explicit repo_name maps to workspace/repo_name
        var repoNameArg = arguments.TryGetValue("repo_name", out var repoNameValue) ? repoNameValue?.ToString() : null;
        if (!string.IsNullOrWhiteSpace(repoNameArg))
        {
            var safeName = NormalizeRepoFolderName(repoNameArg);
            var namedPath = Path.Combine(workspacePath, safeName);
            if (Directory.Exists(Path.Combine(namedPath, ".git")))
                return namedPath;

            error = $"未找到名为 {repoNameArg} 的仓库目录。请提供正确的 repo_name 或 repo_path。";
            return null;
        }

        // 3) Auto-resolve when only one repo exists
        var repositories = DiscoverGitRepositories(workspacePath).ToList();
        if (repositories.Count == 0)
        {
            error = "当前工作区未发现 Git 仓库。请先执行 git_clone，或提供 repo_path。";
            return null;
        }

        if (repositories.Count == 1)
            return repositories[0];

        var relList = repositories
            .Select(p => Path.GetRelativePath(workspacePath, p).Replace('\\', '/'))
            .OrderBy(p => p)
            .ToList();

        error = "检测到多个仓库，请显式指定 repo_name 或 repo_path。可选仓库：\n- " + string.Join("\n- ", relList);
        return null;
    }

    private static HashSet<string> DiscoverGitRepositories(string workspacePath)
    {
        var workspaceFull = Path.GetFullPath(workspacePath);
        var queue = new Queue<(DirectoryInfo Dir, int Depth)>();
        queue.Enqueue((new DirectoryInfo(workspaceFull), 0));

        var ignoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", "node_modules", "bin", "obj", ".next", "dist", "build", ".venv"
        };

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int maxDepth = 3; // supports layouts like apps/web, apps/api

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            var current = dir.FullName;

            if (Directory.Exists(Path.Combine(current, ".git")))
            {
                results.Add(current);
                continue; // nested repos are uncommon; skip descending this repo
            }

            if (depth >= maxDepth) continue;

            DirectoryInfo[] children;
            try { children = dir.GetDirectories(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var child in children)
            {
                if (ignoredNames.Contains(child.Name)) continue;
                queue.Enqueue((child, depth + 1));
            }
        }

        return results;
    }

    private static string BuildCandidatePath(string workspacePath, string repoPathArg)
    {
        if (Path.IsPathRooted(repoPathArg))
            return Path.GetFullPath(repoPathArg);
        return Path.GetFullPath(Path.Combine(workspacePath, repoPathArg));
    }

    private static bool IsPathInsideWorkspace(string candidate, string workspacePath)
    {
        var workspaceFull = Path.GetFullPath(workspacePath);
        var candidateFull = Path.GetFullPath(candidate);
        return candidateFull.StartsWith(workspaceFull, StringComparison.OrdinalIgnoreCase);
    }
}

