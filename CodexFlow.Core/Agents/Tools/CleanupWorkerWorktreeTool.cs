using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

public sealed class CleanupWorkerWorktreeTool(
    Func<string, CancellationToken, Task<CleanupWorkerWorktreeResult>> cleanupWorkerWorktreeFunc,
    ILogger<CleanupWorkerWorktreeTool> logger) : ICodexTool
{
    public string Name => "cleanup_worker_worktree";

    public string Description =>
        "清理 forge worker 持有的 shadow worktree。仅适用于已完成、失败或已取消的 forge worker。参数：job_id(必填)。"
        + "Few-shot: cleanup_worker_worktree({\"job_id\":\"01K...\"})。";

    public ToolCategory Category => ToolCategory.System;
    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: true,
        InterruptBehavior: ToolInterruptBehavior.RequiresConfirmation,
        ResultSizeSoftLimitChars: 8_192);

    public IReadOnlyList<int> AllowedStages => [1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var jobId = arguments.GetValueOrDefault("job_id")?.ToString()
            ?? arguments.GetValueOrDefault("worker_id")?.ToString();

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return CodexToolResult.Error("缺少必填参数 job_id。");
        }

        try
        {
            var result = await cleanupWorkerWorktreeFunc(jobId, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return CodexToolResult.Error(result.Message ?? $"worker {jobId} 的 shadow worktree 无法清理。");
            }

            return result.Cleaned
                ? CodexToolResult.Succeeded($"✅ forge worker {result.JobId} 的 shadow worktree 已清理。")
                : CodexToolResult.Succeeded(result.Message ?? $"forge worker {result.JobId} 当前没有需要清理的 shadow worktree。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "cleanup_worker_worktree 执行失败。jobId={JobId}", jobId);
            return CodexToolResult.Error($"清理 worker shadow worktree 时发生异常：{ex.Message}");
        }
    }
}
