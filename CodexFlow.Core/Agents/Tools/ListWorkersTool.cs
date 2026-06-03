using System.Text;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

public sealed class ListWorkersTool(
    Func<string, CancellationToken, Task<IReadOnlyList<WorkerJobSummary>>> listWorkersFunc,
    ILogger<ListWorkersTool> logger) : ICodexTool
{
    public string Name => "list_workers";

    public string Description =>
        "列出当前 session 下的 worker 作业及状态。参数：session_id(必填)。"
        + "Few-shot: list_workers({\"session_id\":\"session-123\"})。";

    public ToolCategory Category => ToolCategory.System;
    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: true,
        IsReadOnly: true,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 8_192);

    public IReadOnlyList<int> AllowedStages => [1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return CodexToolResult.Error("缺少必填参数 session_id。");
        }

        try
        {
            var workers = await listWorkersFunc(sessionId, ct).ConfigureAwait(false);
            if (workers.Count == 0)
            {
                return CodexToolResult.Succeeded("当前会话没有 worker 作业。");
            }

            var builder = new StringBuilder();
            builder.AppendLine($"当前会话共有 {workers.Count} 个 worker：");
            foreach (var worker in workers)
            {
                builder.Append("- ");
                builder.Append(worker.JobId);
                builder.Append(" | ");
                builder.Append(worker.WorkerType);
                builder.Append(" | ");
                builder.Append(worker.Status);
                if (!string.IsNullOrWhiteSpace(worker.StateKind))
                {
                    builder.Append(" | state=");
                    builder.Append(worker.StateKind);
                }
                if (!string.IsNullOrWhiteSpace(worker.TaskId))
                {
                    builder.Append(" | task=");
                    builder.Append(worker.TaskId);
                }
                if (!string.IsNullOrWhiteSpace(worker.Summary))
                {
                    builder.Append(" | summary=");
                    builder.Append(worker.Summary);
                }
                if (!string.IsNullOrWhiteSpace(worker.VerificationVerdict))
                {
                    builder.Append(" | verify=");
                    builder.Append(worker.VerificationVerdict);
                }
                if (!string.IsNullOrWhiteSpace(worker.VerificationSummary))
                {
                    builder.Append(" | verify_summary=");
                    builder.Append(worker.VerificationSummary);
                }
                if (worker.VerificationFocusItems is { Count: > 0 })
                {
                    builder.Append(" | verify_focus=");
                    builder.Append(string.Join("; ", worker.VerificationFocusItems));
                }
                if (!string.IsNullOrWhiteSpace(worker.WaitingReason))
                {
                    builder.Append(" | waiting=");
                    builder.Append(worker.WaitingReason);
                }
                builder.AppendLine();
            }

            return CodexToolResult.Succeeded(builder.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "list_workers 执行失败。sessionId={SessionId}", sessionId);
            return CodexToolResult.Error($"列出 workers 时发生异常：{ex.Message}");
        }
    }
}
