using System.Text;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Agents.Tools;

public sealed class WorkerOutputTool(
    string name,
    Func<WorkerOutputRequest, CancellationToken, Task<WorkerOutputResult>> workerOutputFunc,
    ILogger<WorkerOutputTool> logger) : ICodexTool
{
    public string Name => name;

    public string Description => Name.Equals("task_output", StringComparison.OrdinalIgnoreCase)
        ? "读取后台 worker/task 的当前状态和增量事件输出。参数：job_id(必填), after_seq(可选), max_events(可选，默认50), include_current_view(可选，默认true)。"
        : "读取后台 worker 的当前状态和增量事件输出。参数：job_id(必填), after_seq(可选), max_events(可选，默认50), include_current_view(可选，默认true)。";

    public ToolCategory Category => ToolCategory.System;

    public ToolExecutionMetadata Metadata => new(
        IsConcurrencySafe: true,
        IsReadOnly: true,
        IsDestructive: false,
        InterruptBehavior: ToolInterruptBehavior.CancelSafe,
        ResultSizeSoftLimitChars: 16_384);

    public IReadOnlyList<int> AllowedStages => [1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var jobId = arguments.GetValueOrDefault("job_id")?.ToString()
            ?? arguments.GetValueOrDefault("worker_id")?.ToString()
            ?? arguments.GetValueOrDefault("task_id")?.ToString();
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return CodexToolResult.Error("缺少必填参数 job_id。");
        }

        var afterSeq = ReadInt64(arguments, "after_seq");
        var maxEvents = Math.Clamp(ReadInt32(arguments, "max_events") ?? 50, 1, 200);
        var includeCurrentView = ReadBoolean(arguments, "include_current_view", defaultValue: true);

        if (CommandTaskRegistry.TryGet(jobId, afterSeq, maxEvents, out var commandSnapshot))
        {
            return CodexToolResult.Succeeded(
                FormatCommandOutput(commandSnapshot),
                metadata: commandSnapshot,
                summary: BuildCommandSummary(commandSnapshot));
        }

        try
        {
            var result = await workerOutputFunc(
                new WorkerOutputRequest
                {
                    JobId = jobId,
                    AfterSeq = afterSeq,
                    MaxEvents = maxEvents,
                    IncludeCurrentView = includeCurrentView
                },
                ct).ConfigureAwait(false);

            if (!result.Success)
            {
                return CodexToolResult.Error(result.Message ?? $"未能读取 worker 输出：{jobId}");
            }

            return CodexToolResult.Succeeded(
                FormatOutput(result),
                metadata: result,
                summary: BuildSummary(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{ToolName} 执行失败。jobId={JobId}", Name, jobId);
            return CodexToolResult.Error($"读取 worker 输出时发生异常：{ex.Message}");
        }
    }

    private static string FormatOutput(WorkerOutputResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"worker output: {result.JobId}");

        if (result.View != null)
        {
            builder.Append("status=").Append(result.View.Status ?? "<unknown>");
            if (!string.IsNullOrWhiteSpace(result.View.WorkerType))
            {
                builder.Append(" worker=").Append(result.View.WorkerType);
            }
            if (!string.IsNullOrWhiteSpace(result.View.StateKind))
            {
                builder.Append(" state=").Append(result.View.StateKind);
            }
            if (result.View.LatestSeq > 0)
            {
                builder.Append(" latest_seq=").Append(result.View.LatestSeq);
            }
            builder.AppendLine();

            if (!string.IsNullOrWhiteSpace(result.View.Summary))
            {
                builder.AppendLine("summary:");
                builder.AppendLine(result.View.Summary.Trim());
            }

            if (!string.IsNullOrWhiteSpace(result.View.LatestMessage) &&
                !string.Equals(result.View.LatestMessage, result.View.Summary, StringComparison.Ordinal))
            {
                builder.AppendLine("latest_message:");
                builder.AppendLine(result.View.LatestMessage.Trim());
            }

            if (result.View.WaitingUser)
            {
                builder.AppendLine($"waiting_user: {result.View.WaitingReason ?? "waiting for user input"}");
                if (!string.IsNullOrWhiteSpace(result.View.ResumeToken))
                {
                    builder.AppendLine($"resume_token: {result.View.ResumeToken}");
                }
            }

            if (result.View.RecoveryNeeded)
            {
                builder.AppendLine($"recovery_needed: {result.View.RecoveryReason ?? "true"}");
                if (!string.IsNullOrWhiteSpace(result.View.ResumeStrategy))
                {
                    builder.AppendLine($"resume_strategy: {result.View.ResumeStrategy}");
                }
                if (!string.IsNullOrWhiteSpace(result.View.ResumeGuidance))
                {
                    builder.AppendLine("resume_guidance:");
                    builder.AppendLine(result.View.ResumeGuidance.Trim());
                }
            }
        }

        if (result.Events.Count > 0)
        {
            builder.AppendLine("events:");
            foreach (var evt in result.Events)
            {
                builder.Append("- #").Append(evt.Seq)
                    .Append(' ')
                    .Append(evt.EventType)
                    .Append(' ')
                    .Append(evt.OccurredAtUtc.ToString("u"))
                    .Append(": ");
                builder.AppendLine(Trim(evt.PayloadJson, 1000));
            }
        }
        else
        {
            builder.AppendLine("events: none");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatCommandOutput(CommandTaskSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"command output: {snapshot.CommandTaskId}");
        builder.Append("status=").Append(snapshot.Status);
        if (snapshot.ExitCode.HasValue)
        {
            builder.Append(" exit_code=").Append(snapshot.ExitCode.Value);
        }
        builder.Append(" latest_seq=").Append(snapshot.LatestSeq);
        builder.AppendLine();
        builder.AppendLine($"cwd: {snapshot.WorkingDirectory}");
        builder.AppendLine($"$ {snapshot.Command}");

        if (snapshot.Events.Count > 0)
        {
            builder.AppendLine("events:");
            foreach (var evt in snapshot.Events)
            {
                builder.Append("- #").Append(evt.Seq)
                    .Append(' ')
                    .Append(evt.Stream)
                    .Append(' ')
                    .Append(evt.OccurredAtUtc.ToString("u"))
                    .Append(": ")
                    .AppendLine(Trim(evt.Text, 1000));
            }
        }
        else
        {
            builder.AppendLine("events: none");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildCommandSummary(CommandTaskSnapshot snapshot)
        => $"command {snapshot.CommandTaskId}: status={snapshot.Status}, events={snapshot.Events.Count}, latest_seq={snapshot.LatestSeq}";

    private static string BuildSummary(WorkerOutputResult result)
    {
        var status = result.View?.Status ?? "unknown";
        var latestSeq = result.View?.LatestSeq ?? 0;
        return $"worker {result.JobId}: status={status}, events={result.Events.Count}, latest_seq={latestSeq}";
    }

    private static string Trim(string text, int maxChars)
        => text.Length <= maxChars ? text : text[..maxChars] + "...";

    private static long? ReadInt64(Dictionary<string, object?> arguments, string key)
        => arguments.TryGetValue(key, out var raw) && raw != null
            ? raw switch
            {
                long value => value,
                int value => value,
                string text when long.TryParse(text, out var parsed) => parsed,
                _ => null
            }
            : null;

    private static int? ReadInt32(Dictionary<string, object?> arguments, string key)
        => arguments.TryGetValue(key, out var raw) && raw != null
            ? raw switch
            {
                int value => value,
                long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
                string text when int.TryParse(text, out var parsed) => parsed,
                _ => null
            }
            : null;

    private static bool ReadBoolean(Dictionary<string, object?> arguments, string key, bool defaultValue)
        => arguments.TryGetValue(key, out var raw) && raw != null
            ? raw switch
            {
                bool value => value,
                string text when bool.TryParse(text, out var parsed) => parsed,
                _ => defaultValue
            }
            : defaultValue;
}

public sealed class AskUserQuestionTool : ICodexTool
{
    public string Name => "ask_user_question";
    public string Description => "向用户发起结构化问题。参数：question(必填), header(可选), options(可选字符串数组), preview(可选)。用于 coordinator 在继续 worker 或退出计划前收集用户确认。";
    public ToolCategory Category => ToolCategory.System;
    public ToolExecutionMetadata Metadata => new(false, false, false, ToolInterruptBehavior.CancelSafe, 8_192);
    public IReadOnlyList<int> AllowedStages => [1, 2, 3];

    public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var question = arguments.GetValueOrDefault("question")?.ToString();
        if (string.IsNullOrWhiteSpace(question))
        {
            return Task.FromResult(CodexToolResult.Error("缺少必填参数 question。"));
        }

        var header = arguments.GetValueOrDefault("header")?.ToString();
        var preview = arguments.GetValueOrDefault("preview")?.ToString();
        var options = ReadStringArray(arguments.GetValueOrDefault("options"));
        var metadata = new
        {
            kind = "ask_user_question",
            ui_event = "ask_user_question",
            contract_version = 1,
            requires_user_response = true,
            header,
            question,
            options,
            preview
        };

        var builder = new StringBuilder();
        builder.AppendLine("USER_QUESTION_REQUEST");
        if (!string.IsNullOrWhiteSpace(header))
        {
            builder.AppendLine($"header: {header}");
        }
        builder.AppendLine($"question: {question.Trim()}");
        if (options.Length > 0)
        {
            builder.AppendLine("options:");
            foreach (var option in options)
            {
                builder.Append("- ").AppendLine(option);
            }
        }
        if (!string.IsNullOrWhiteSpace(preview))
        {
            builder.AppendLine("preview:");
            builder.AppendLine(preview.Trim());
        }

        return Task.FromResult(CodexToolResult.Succeeded(
            builder.ToString().TrimEnd(),
            metadata,
            summary: $"question: {question.Trim()}"));
    }

    internal static string[] ReadStringArray(object? raw)
    {
        if (raw == null)
        {
            return [];
        }

        if (raw is string text)
        {
            return string.IsNullOrWhiteSpace(text) ? [] : [text.Trim()];
        }

        if (raw is IEnumerable<string> strings)
        {
            return strings
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim())
                .ToArray();
        }

        if (raw is Newtonsoft.Json.Linq.JArray array)
        {
            return array
                .Values<string?>()
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item!.Trim())
                .ToArray();
        }

        return [];
    }
}

public sealed class EnterPlanModeTool(CodexSessionManager? sessionManager = null) : ICodexTool
{
    public const string ActiveMetadataKey = "PlanModeActive";
    public const string ObjectiveMetadataKey = "PlanModeObjective";
    public const string ReasonMetadataKey = "PlanModeReason";
    public const string EnteredAtMetadataKey = "PlanModeEnteredAtUtc";
    public const string ExitedAtMetadataKey = "PlanModeExitedAtUtc";
    public const string ApprovedMetadataKey = "PlanModeApproved";
    public const string PlanSummaryMetadataKey = "PlanModeSummary";

    public string Name => "enter_plan_mode";
    public string Description => "进入计划模式，声明本轮只规划和请求确认，不直接改代码。参数：session_id(可选), reason(可选), objective(可选)。传入 session_id 时会写入 session metadata。";
    public ToolCategory Category => ToolCategory.System;
    public ToolExecutionMetadata Metadata => new(false, false, false, ToolInterruptBehavior.CancelSafe, 4_096);
    public IReadOnlyList<int> AllowedStages => [1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        var reason = arguments.GetValueOrDefault("reason")?.ToString();
        var objective = arguments.GetValueOrDefault("objective")?.ToString();
        var enteredAtUtc = DateTime.UtcNow;

        if (sessionManager != null && !string.IsNullOrWhiteSpace(sessionId))
        {
            var session = await sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
            session.Metadata[ActiveMetadataKey] = bool.TrueString;
            session.Metadata[EnteredAtMetadataKey] = enteredAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            session.Metadata.Remove(ExitedAtMetadataKey);
            session.Metadata.Remove(ApprovedMetadataKey);
            if (!string.IsNullOrWhiteSpace(objective))
            {
                session.Metadata[ObjectiveMetadataKey] = objective.Trim();
            }
            if (!string.IsNullOrWhiteSpace(reason))
            {
                session.Metadata[ReasonMetadataKey] = reason.Trim();
            }
            await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
        }

        return CodexToolResult.Succeeded(
            "PLAN_MODE_ENTERED",
            metadata: new
            {
                kind = "plan_mode",
                ui_event = "plan_mode_changed",
                contract_version = 1,
                active = true,
                session_id = sessionId,
                reason,
                objective,
                entered_at_utc = enteredAtUtc
            },
            summary: string.IsNullOrWhiteSpace(objective) ? "entered plan mode" : $"entered plan mode: {objective}");
    }
}

public sealed class ExitPlanModeTool(CodexSessionManager? sessionManager = null) : ICodexTool
{
    public string Name => "exit_plan_mode";
    public string Description => "退出计划模式并提交计划审批结果。参数：approved(必填), session_id(可选), plan_summary(可选), reason(可选)。传入 session_id 时会写入 session metadata。";
    public ToolCategory Category => ToolCategory.System;
    public ToolExecutionMetadata Metadata => new(false, false, false, ToolInterruptBehavior.CancelSafe, 8_192);
    public IReadOnlyList<int> AllowedStages => [1, 2, 3];

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var approved = ReadBoolean(arguments.GetValueOrDefault("approved"));
        if (!approved.HasValue)
        {
            return CodexToolResult.Error("缺少必填参数 approved。");
        }

        var sessionId = arguments.GetValueOrDefault("session_id")?.ToString();
        var planSummary = arguments.GetValueOrDefault("plan_summary")?.ToString();
        var reason = arguments.GetValueOrDefault("reason")?.ToString();
        var exitedAtUtc = DateTime.UtcNow;

        if (sessionManager != null && !string.IsNullOrWhiteSpace(sessionId))
        {
            var session = await sessionManager.GetOrCreateSessionAsync(sessionId, string.Empty, string.Empty, (Uri?)null).ConfigureAwait(false);
            session.Metadata[EnterPlanModeTool.ActiveMetadataKey] = bool.FalseString;
            session.Metadata[EnterPlanModeTool.ApprovedMetadataKey] = approved.Value.ToString();
            session.Metadata[EnterPlanModeTool.ExitedAtMetadataKey] = exitedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(planSummary))
            {
                session.Metadata[EnterPlanModeTool.PlanSummaryMetadataKey] = planSummary.Trim();
            }
            if (!string.IsNullOrWhiteSpace(reason))
            {
                session.Metadata[EnterPlanModeTool.ReasonMetadataKey] = reason.Trim();
            }
            await sessionManager.UpdateSessionAsync(session).ConfigureAwait(false);
        }

        var output = approved.Value ? "PLAN_MODE_APPROVED" : "PLAN_MODE_REJECTED";
        if (!string.IsNullOrWhiteSpace(planSummary))
        {
            output += Environment.NewLine + planSummary.Trim();
        }

        return CodexToolResult.Succeeded(
            output,
            metadata: new
            {
                kind = "plan_mode",
                ui_event = "plan_mode_changed",
                contract_version = 1,
                active = false,
                approved = approved.Value,
                session_id = sessionId,
                planSummary,
                reason,
                exited_at_utc = exitedAtUtc
            },
            summary: approved.Value ? "plan approved" : "plan rejected");
    }

    private static bool? ReadBoolean(object? raw)
        => raw switch
        {
            bool value => value,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null
        };
}

public sealed class SyntheticOutputTool : ICodexTool
{
    public string Name => "synthetic_output";
    public string Description => "输出 coordinator 汇总内容，不执行文件写入、命令或 worker 操作。参数：content(必填), summary(可选)。";
    public ToolCategory Category => ToolCategory.System;
    public ToolExecutionMetadata Metadata => new(true, true, false, ToolInterruptBehavior.CancelSafe, 16_384);
    public IReadOnlyList<int> AllowedStages => [1, 2, 3];

    public Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var content = arguments.GetValueOrDefault("content")?.ToString();
        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult(CodexToolResult.Error("缺少必填参数 content。"));
        }

        var summary = arguments.GetValueOrDefault("summary")?.ToString();
        return Task.FromResult(CodexToolResult.Succeeded(
            content.Trim(),
            metadata: new { kind = "synthetic_output", summary },
            summary: string.IsNullOrWhiteSpace(summary) ? "synthetic output" : summary.Trim()));
    }
}
