using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 重试已失败的后台作业（fire-and-forget）。
/// 调用后立即返回确认，不阻塞等待任务执行完成。
/// 后台调度器异步接管作业并通过 SignalR 通知推送进展。
/// </summary>
public sealed class RetryTaskTool(
    Func<string, Task<bool>> retryJobFunc,
    ILogger<RetryTaskTool> logger) : ICodexTool
{
    public string Name => "retry_failed_task";

    public string Description =>
        "重试一个已失败（Failed / FailedRecoveryNeeded）的后台作业。" +
        "工具调用后立即返回，不等待任务执行完成——作业将被重新排入调度队列，完成后通过通知推送结果。" +
        "参数：job_id（作业 ID，必填）。" +
        "Few-shot: retry_failed_task({\"job_id\":\"01KMB9G3TAY4YH6G9THYJG5Q9Z\"})。" +
        "注意：请先确认 job_id 正确，若 job 状态非 Failed 则会返回错误。";

    public ToolCategory Category => ToolCategory.System;
    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 8_192);

    public IReadOnlyList<int> AllowedStages => [1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(
        Dictionary<string, object?> arguments,
        CancellationToken ct = default)
    {
        var jobId = arguments.GetValueOrDefault("job_id")?.ToString();

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return CodexToolResult.Error("缺少必填参数 job_id。请提供要重试的作业 ID。");
        }

        logger.LogInformation("retry_failed_task: 请求重试 Job {JobId}", jobId);

        bool success;
        try
        {
            success = await retryJobFunc(jobId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "retry_failed_task: 重试 Job {JobId} 时发生异常", jobId);
            return CodexToolResult.Error($"重试作业时发生异常：{ex.Message}");
        }

        if (!success)
        {
            logger.LogWarning("retry_failed_task: Job {JobId} 重试失败（作业不存在或当前状态不允许重试）", jobId);
            return CodexToolResult.Error(
                $"作业 {jobId} 无法重试：作业不存在，或当前状态不是 Failed / FailedRecoveryNeeded / Cancelled。");
        }

        logger.LogInformation("retry_failed_task: Job {JobId} 已成功重新排入队列", jobId);

        return CodexToolResult.Succeeded(
            $"✅ 作业 {jobId} 已重新排入调度队列。系统将在后台继续执行，完成后通过通知推送结果。请通知用户等待即可，不要再次调用 start_next_task 或 retry_failed_task。");
    }
}
