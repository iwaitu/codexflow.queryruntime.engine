using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

public sealed class StopWorkerTool(
    Func<string, CancellationToken, Task<StopWorkerResult>> stopWorkerFunc,
    ILogger<StopWorkerTool> logger) : ICodexTool
{
    public string Name => "stop_worker";

    public string Description =>
        "停止一个正在运行或等待用户输入的 worker。参数：job_id(必填)。"
        + "Few-shot: stop_worker({\"job_id\":\"01K...\"})。";

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
            ?? arguments.GetValueOrDefault("worker_id")?.ToString();

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return CodexToolResult.Error("缺少必填参数 job_id。");
        }

        try
        {
            var result = await stopWorkerFunc(jobId, ct).ConfigureAwait(false);
            return result.Success
                ? CodexToolResult.Succeeded($"✅ worker {result.JobId} 已停止。完成状态会通过通知回流到主代理。")
                : CodexToolResult.Error(result.Message ?? $"worker {jobId} 无法停止。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "stop_worker 执行失败。jobId={JobId}", jobId);
            return CodexToolResult.Error($"停止 worker 时发生异常：{ex.Message}");
        }
    }
}
