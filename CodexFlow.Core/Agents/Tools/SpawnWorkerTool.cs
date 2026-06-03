using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Workers;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

public sealed class SpawnWorkerTool(
    Func<SpawnWorkerRequest, CancellationToken, Task<SpawnWorkerResult>> spawnWorkerFunc,
    ILogger<SpawnWorkerTool> logger) : ICodexTool
{
    public string Name => "spawn_worker";

    public string Description =>
        "启动一个后台 worker。explore / plan / verify 为只读 worker；forge 会在隔离 shadow worktree 中执行写入任务。"
        + "参数：worker_type(必填), prompt(必填), description(可选), task_id(可选), run_in_background(可选，默认 true), max_rounds(可选)。"
        + "prompt 应像给刚加入的同事做 briefing：写清目标、原因、已知上下文、明确范围（文件/模块）和期望输出。"
        + "不要写“根据你的发现再修复”“先看看然后处理”这类把理解责任外包给 worker 的笼统提示。"
        + "需要只读研究时明确只返回结论/证据；需要真正写代码时使用 forge。"
        + "Few-shot: spawn_worker({\"worker_type\":\"explore\",\"prompt\":\"请分析认证模块的调用链，范围只看 AuthService.cs、JwtTokenService.cs 和相关接口，返回关键入口、依赖关系和风险点。\",\"task_id\":\"TASK_AUTH_001\"})。";

    public ToolCategory Category => ToolCategory.System;
    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: false,
        IsReadOnly: false,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 12_288);

    public IReadOnlyList<int> AllowedStages => [1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        var userId = arguments.GetValueOrDefault("user_id")?.ToString();
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var workerTypeRaw = arguments.GetValueOrDefault("worker_type")?.ToString();
        var prompt = arguments.GetValueOrDefault("prompt")?.ToString();
        var description = arguments.GetValueOrDefault("description")?.ToString();
        var taskId = arguments.GetValueOrDefault("task_id")?.ToString();
        var runInBackground = ReadBoolean(arguments, "run_in_background", defaultValue: true);
        var maxRounds = ReadInt32(arguments, "max_rounds");

        if (string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(workspacePath))
        {
            return CodexToolResult.Error("缺少会话上下文。请在 coordinator 会话中调用该工具。");
        }

        if (!WorkerJobTypes.TryParseWorkerType(workerTypeRaw, out var workerType))
        {
            return CodexToolResult.Error("worker_type 仅支持 explore / plan / verify / forge。");
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = description;
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return CodexToolResult.Error("缺少必填参数 prompt。请明确告诉 worker 要完成的分析、规划或验证任务。");
        }

        var lintWarnings = WorkerDelegationLint.Analyze(
            workerType.ToString(),
            prompt,
            description,
            isContinue: false);

        try
        {
            var result = await spawnWorkerFunc(
                new SpawnWorkerRequest
                {
                    SessionId = sessionId,
                    UserId = userId,
                    WorkspacePath = workspacePath,
                    WorkerType = workerType,
                    Prompt = prompt,
                    Description = description,
                    TaskId = taskId,
                    RunInBackground = runInBackground,
                    MaxRounds = maxRounds
                },
                ct).ConfigureAwait(false);

            if (!result.Success || string.IsNullOrWhiteSpace(result.JobId))
            {
                return CodexToolResult.Error(result.Message ?? "worker 启动失败。");
            }

            var backgroundModeNote = runInBackground
                ? string.Empty
                : " 当前阶段仅支持后台执行，已自动按后台模式提交。";
            return WorkerDelegationLint.Decorate(
                CodexToolResult.Succeeded(
                $"✅ 已启动 {workerType.ToString().ToLowerInvariant()} worker。作业 ID: {result.JobId}，状态: {result.Status ?? "queued"}。"
                + $"{backgroundModeNote} 完成后系统会通过通知回流结果。",
                summary: $"已启动 {workerType.ToString().ToLowerInvariant()} worker，作业 ID: {result.JobId}。"),
                lintWarnings);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "spawn_worker 执行失败。workerType={WorkerType}", workerTypeRaw);
            return CodexToolResult.Error($"启动 worker 时发生异常：{ex.Message}");
        }
    }

    private static bool ReadBoolean(Dictionary<string, object?> arguments, string key, bool defaultValue)
    {
        if (!arguments.TryGetValue(key, out var raw) || raw == null)
        {
            return defaultValue;
        }

        return raw switch
        {
            bool boolean => boolean,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private static int? ReadInt32(Dictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var raw) || raw == null)
        {
            return null;
        }

        return raw switch
        {
            int integer => integer,
            long integer64 when integer64 is >= int.MinValue and <= int.MaxValue => (int)integer64,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }
}
