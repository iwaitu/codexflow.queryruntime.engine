using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

public sealed class TaskStopTool(
    Func<string, CancellationToken, Task<StopWorkerResult>>? stopWorkerFunc,
    ILogger<TaskStopTool> logger) : ICodexTool
{
    public string Name => "task_stop";

    public string Description =>
        "停止后台命令任务或 worker。参数：job_id(必填，可传 command_task_id / worker job id)。" +
        "Few-shot: task_stop({\"job_id\":\"cmd_...\"})。";

    public ToolCategory Category => ToolCategory.System;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 8_192);

    public IReadOnlyList<int> AllowedStages => [1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var jobId = arguments.GetValueOrDefault("job_id")?.ToString()
            ?? arguments.GetValueOrDefault("command_task_id")?.ToString()
            ?? arguments.GetValueOrDefault("worker_id")?.ToString()
            ?? arguments.GetValueOrDefault("task_id")?.ToString();

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return CodexToolResult.Error("缺少必填参数 job_id。");
        }

        if (CommandTaskRegistry.Stop(jobId, out var snapshot))
        {
            return CodexToolResult.Succeeded(
                $"✅ command task {jobId} stop requested. status={snapshot!.Status}",
                metadata: snapshot,
                summary: $"command task stopped: {jobId}");
        }

        if (stopWorkerFunc == null)
        {
            return CodexToolResult.Error($"未找到命令任务，且当前环境未注册 worker stop 回调：{jobId}");
        }

        try
        {
            var result = await stopWorkerFunc(jobId, ct).ConfigureAwait(false);
            return result.Success
                ? CodexToolResult.Succeeded($"✅ worker {result.JobId} 已停止。", metadata: result, summary: $"worker stopped: {result.JobId}")
                : CodexToolResult.Error(result.Message ?? $"worker {jobId} 无法停止。", metadata: result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "task_stop 执行失败。jobId={JobId}", jobId);
            return CodexToolResult.Error($"停止 task 时发生异常：{ex.Message}");
        }
    }
}
