using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace CodexFlow.Core.Planning.Committee;

/// <summary>
/// 会议工件落盘服务。
/// 负责创建会议目录、写入各阶段文件、更新 meeting.json。
/// </summary>
public class MeetingArtifactService
{
    private readonly ILogger _logger;

    public MeetingArtifactService(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 创建会议目录并初始化基础工件。
    /// </summary>
    public string InitializeMeetingDirectory(string workspacePath, string logRoot, CommitteeMeetingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var logDir = Path.Combine(workspacePath, logRoot);
        Directory.CreateDirectory(logDir);

        var meetingDirName = $"meeting-{state.StartedAt:yyyyMMdd}-{state.StartedAt:HHmmss}-{Environment.ProcessId}";
        var meetingDir = Path.Combine(logDir, meetingDirName);
        Directory.CreateDirectory(meetingDir);

        var runtimeDir = Path.Combine(meetingDir, CommitteeConstants.RuntimeDir);
        Directory.CreateDirectory(runtimeDir);

        state.MeetingDirectory = meetingDir;

        // 初始化 meeting.json
        WriteMeetingJson(state);

        // 初始化 transcript.md
        var transcriptPath = Path.Combine(meetingDir, CommitteeConstants.TranscriptFileName);
        File.WriteAllText(transcriptPath, $"# 委员会会议记录\n\n- 会议 ID: {state.MeetingId}\n- 目标: {state.Goal}\n- 开始时间: {state.StartedAt:O}\n\n");

        // 初始化 review_history.json
        var historyPath = Path.Combine(meetingDir, CommitteeConstants.RuntimeDir, CommitteeConstants.ReviewHistoryFileName);
        File.WriteAllText(historyPath, "[]");

        _logger.LogInformation("会议目录已创建: {MeetingDir}", meetingDir);
        return meetingDir;
    }

    /// <summary>
    /// 写入初始蓝图。
    /// </summary>
    public void WriteInitialBlueprint(CommitteeMeetingState state, string blueprint)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.InitialBlueprintFileName);
        File.WriteAllText(path, blueprint);

        WriteCurrentPlan(state, blueprint);
    }

    /// <summary>
    /// 写入当前方案到 runtime/current_plan.md。
    /// </summary>
    public void WriteCurrentPlan(CommitteeMeetingState state, string plan)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.RuntimeDir, CommitteeConstants.CurrentPlanFileName);
        File.WriteAllText(path, plan);
        state.CurrentPlan = plan;
    }

    /// <summary>
    /// 写入某一轮评审前的方案快照。
    /// </summary>
    public void WriteRoundPlanSnapshot(CommitteeMeetingState state, int round)
    {
        ArgumentNullException.ThrowIfNull(state);

        var fileName = $"round-{round:D2}-plan-before-review.md";
        var path = Path.Combine(state.MeetingDirectory, fileName);
        File.WriteAllText(path, state.CurrentPlan);
    }

    /// <summary>
    /// 写入分析师评审反馈。
    /// </summary>
    public void WriteAnalystFeedback(CommitteeMeetingState state, int round, ReviewerFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(state);

        var fileName = $"round-{round:D2}-analyst-feedback.md";
        var path = Path.Combine(state.MeetingDirectory, fileName);
        File.WriteAllText(path, JsonConvert.SerializeObject(feedback, Formatting.Indented));
    }

    /// <summary>
    /// 写入架构师评审反馈。
    /// </summary>
    public void WriteArchitectFeedback(CommitteeMeetingState state, int round, ReviewerFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(state);

        var fileName = $"round-{round:D2}-architect-feedback.md";
        var path = Path.Combine(state.MeetingDirectory, fileName);
        File.WriteAllText(path, JsonConvert.SerializeObject(feedback, Formatting.Indented));
    }

    /// <summary>
    /// 写入项目经理裁决。
    /// </summary>
    public void WriteModeratorDecision(CommitteeMeetingState state, int round, ModeratorDecision decision)
    {
        ArgumentNullException.ThrowIfNull(state);

        var fileName = $"round-{round:D2}-project-manager-feedback.md";
        var path = Path.Combine(state.MeetingDirectory, fileName);
        File.WriteAllText(path, JsonConvert.SerializeObject(decision, Formatting.Indented));
    }

    /// <summary>
    /// 写入最终蓝图。
    /// </summary>
    public void WriteFinalPlan(CommitteeMeetingState state, string finalPlan)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.FinalPlanFileName);
        File.WriteAllText(path, finalPlan);
    }

    /// <summary>
    /// 更新 review_history.json。
    /// </summary>
    public void UpdateReviewHistory(CommitteeMeetingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.RuntimeDir, CommitteeConstants.ReviewHistoryFileName);
        File.WriteAllText(path, JsonConvert.SerializeObject(state.Rounds, Formatting.Indented));
    }

    /// <summary>
    /// 追加 transcript。
    /// </summary>
    public void AppendTranscript(CommitteeMeetingState state, string entry)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.TranscriptFileName);
        File.AppendAllText(path, entry + "\n\n");
    }

    /// <summary>
    /// 更新 meeting.json 状态，包含轮次详情、事件和工件索引。
    /// </summary>
    public void WriteMeetingJson(CommitteeMeetingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var meetingData = new
        {
            meeting_id = state.MeetingId,
            goal = state.Goal,
            target_dir = state.TargetDir,
            source_file = state.SourceFile,
            started_at = state.StartedAt,
            ended_at = state.EndedAt,
            status = state.Status.ToString().ToLowerInvariant(),
            current_round = state.CurrentRound,
            max_rounds = state.MaxRounds,
            total_rounds = state.Rounds.Count,
            rounds = state.Rounds.Select(r => new
            {
                round = r.RoundNumber,
                analyst = r.AnalystFeedback != null ? new { r.AnalystFeedback.Status, r.AnalystFeedback.Summary, blocking_count = r.AnalystFeedback.BlockingItems.Count } : null,
                architect = r.ArchitectFeedback != null ? new { r.ArchitectFeedback.Status, r.ArchitectFeedback.Summary, blocking_count = r.ArchitectFeedback.BlockingItems.Count } : null,
                moderator = r.ModeratorDecision != null ? new { r.ModeratorDecision.Decision, r.ModeratorDecision.Reason, unresolved_count = r.ModeratorDecision.UnresolvedItems.Count } : null
            }).ToList(),
            events = BuildEventList(state),
            artifacts = BuildArtifactIndex(state)
        };

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.MeetingJsonFileName);
        File.WriteAllText(path, JsonConvert.SerializeObject(meetingData, Formatting.Indented));
    }

    /// <summary>
    /// 构建结构化事件列表。
    /// </summary>
    private static List<object> BuildEventList(CommitteeMeetingState state)
    {
        var events = new List<object>
        {
            new { type = "meeting_started", timestamp = state.StartedAt, detail = state.Goal }
        };

        foreach (var round in state.Rounds)
        {
            events.Add(new { type = "round_started", timestamp = (object?)null, detail = $"Round {round.RoundNumber}" });

            if (round.AnalystFeedback != null)
                events.Add(new { type = "analyst_review", timestamp = (object?)null, detail = round.AnalystFeedback.Status });
            if (round.ArchitectFeedback != null)
                events.Add(new { type = "architect_review", timestamp = (object?)null, detail = round.ArchitectFeedback.Status });
            if (round.ModeratorDecision != null)
                events.Add(new { type = "moderator_decision", timestamp = (object?)null, detail = round.ModeratorDecision.Decision });
        }

        if (state.EndedAt.HasValue)
        {
            events.Add(new { type = "meeting_ended", timestamp = (object?)state.EndedAt.Value, detail = state.Status.ToString().ToLowerInvariant() });
        }

        return events;
    }

    /// <summary>
    /// 构建会议工件索引。
    /// </summary>
    private static List<object> BuildArtifactIndex(CommitteeMeetingState state)
    {
        var artifacts = new List<object>();

        if (!string.IsNullOrEmpty(state.MeetingDirectory) && Directory.Exists(state.MeetingDirectory))
        {
            // 索引会议目录中的所有文件
            foreach (var file in Directory.GetFiles(state.MeetingDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(state.MeetingDirectory, file);
                artifacts.Add(new
                {
                    path = relativePath,
                    type = ClassifyArtifact(relativePath)
                });
            }
        }

        return artifacts;
    }

    private static string ClassifyArtifact(string relativePath)
    {
        if (relativePath.Contains("analyst", StringComparison.OrdinalIgnoreCase)) return "analyst_feedback";
        if (relativePath.Contains("architect", StringComparison.OrdinalIgnoreCase)) return "architect_feedback";
        if (relativePath.Contains("project-manager", StringComparison.OrdinalIgnoreCase)) return "moderator_decision";
        if (relativePath.Contains("plan-before-review", StringComparison.OrdinalIgnoreCase)) return "round_plan_snapshot";
        if (relativePath.Contains("initial_blueprint", StringComparison.OrdinalIgnoreCase)) return "initial_blueprint";
        if (relativePath.Contains("final_plan", StringComparison.OrdinalIgnoreCase)) return "final_plan";
        if (relativePath.Contains("transcript", StringComparison.OrdinalIgnoreCase)) return "transcript";
        if (relativePath.Contains("review_history", StringComparison.OrdinalIgnoreCase)) return "review_history";
        if (relativePath.Contains("shadow-plan-diff", StringComparison.OrdinalIgnoreCase)) return "shadow_diff";
        if (relativePath.Contains("shadow-metrics", StringComparison.OrdinalIgnoreCase)) return "shadow_metrics";
        if (relativePath.Contains("meeting.json", StringComparison.OrdinalIgnoreCase)) return "meeting_metadata";
        return "other";
    }

    /// <summary>
    /// 写入 shadow-plan-diff.json。
    /// </summary>
    public void WriteShadowPlanDiff(CommitteeMeetingState state, object diffData)
    {
        ArgumentNullException.ThrowIfNull(state);

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.ShadowPlanDiffFileName);
        File.WriteAllText(path, JsonConvert.SerializeObject(diffData, Formatting.Indented));
    }

    /// <summary>
    /// 写入 shadow-metrics.json，记录灰度决策所需的对比指标。
    /// </summary>
    public void WriteShadowMetrics(
        CommitteeMeetingState state,
        List<CodexTask> baselinePlan,
        List<CodexTask>? committeePlan,
        CommitteePlanningResult committeeResult)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(baselinePlan);
        ArgumentNullException.ThrowIfNull(committeeResult);

        var metrics = new
        {
            generated_at = DateTime.UtcNow,
            meeting_id = !string.IsNullOrWhiteSpace(committeeResult.MeetingId)
                ? committeeResult.MeetingId
                : state.MeetingId,
            committee_status = committeeResult.Status.ToString(),
            committee_total_rounds = committeeResult.TotalRounds,
            committee_unresolved_count = committeeResult.UnresolvedItems.Count,
            baseline = new
            {
                task_count = baselinePlan.Count,
                high_risk_count = baselinePlan.Count(t => string.Equals(t.RiskLevel, "High", StringComparison.OrdinalIgnoreCase)),
                avg_complexity = baselinePlan.Count > 0 ? baselinePlan.Average(t => t.ComplexityLevel) : 0
            },
            committee = committeePlan != null ? new
            {
                task_count = committeePlan.Count,
                high_risk_count = committeePlan.Count(t => string.Equals(t.RiskLevel, "High", StringComparison.OrdinalIgnoreCase)),
                avg_complexity = committeePlan.Count > 0 ? committeePlan.Average(t => t.ComplexityLevel) : 0
            } : null,
            projection_succeeded = committeePlan is { Count: > 0 }
        };

        var path = Path.Combine(state.MeetingDirectory, CommitteeConstants.ShadowMetricsFileName);
        File.WriteAllText(path, JsonConvert.SerializeObject(metrics, Formatting.Indented));

        _logger.LogInformation("Shadow 指标已写入: {Path}", path);
    }
}
