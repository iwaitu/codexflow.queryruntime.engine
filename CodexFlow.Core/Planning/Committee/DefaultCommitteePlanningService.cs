using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using CodexFlow.Core.Services;

namespace CodexFlow.Core.Planning.Committee;

/// <summary>
/// 委员会规划服务实现。
/// 负责多轮结构化评审流程：Phase A (准备) → Phase B (评审循环) → Phase C (结束)。
/// </summary>
public class DefaultCommitteePlanningService : ICommitteePlanningService
{
    private readonly ICommitteeRoleChatClientFactory _clientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DefaultCommitteePlanningService> _logger;

    // 每角色连续格式失败上限
    private const int MaxConsecutiveFormatFailures = 2;

    public DefaultCommitteePlanningService(
        ICommitteeRoleChatClientFactory clientFactory,
        IConfiguration configuration,
        ILogger<DefaultCommitteePlanningService> logger)
    {
        _clientFactory = clientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CommitteePlanningResult> RunCommitteePlanningAsync(
        CommitteePlanningRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var maxRounds = _configuration.GetValue("Planning:CommitteeMaxRounds", 5);
        var logRoot = _configuration.GetValue("Planning:CommitteeLogRoot", ".architect-committee-logs") ?? ".architect-committee-logs";

        var state = new CommitteeMeetingState
        {
            MeetingId = $"meeting-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
            Goal = request.Goal,
            TargetDir = request.WorkspacePath,
            SourceFile = request.ExistingBlueprintPath,
            MaxRounds = maxRounds
        };

        var artifacts = new MeetingArtifactService(_logger);
        var parser = new CommitteeJsonParser(_logger);

        try
        {
            // Phase A: 会议准备
            artifacts.InitializeMeetingDirectory(request.WorkspacePath, logRoot, state);

            var initialBlueprint = await LoadOrGenerateInitialBlueprintAsync(request, state, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(initialBlueprint))
            {
                return FailMeeting(state, artifacts, "初始蓝图生成失败");
            }

            artifacts.WriteInitialBlueprint(state, initialBlueprint);
            artifacts.AppendTranscript(state, $"## Phase A: 会议准备\n\n初始蓝图已生成，共 {initialBlueprint.Length} 字符。");

            // Phase B: 多轮结构化评审
            var analystFailCount = 0;
            var architectFailCount = 0;

            for (var round = 1; round <= maxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                state.CurrentRound = round;
                _logger.LogInformation("委员会第 {Round}/{MaxRounds} 轮评审开始", round, maxRounds);

                artifacts.WriteRoundPlanSnapshot(state, round);
                artifacts.AppendTranscript(state, $"## Round {round}\n");

                var roundRecord = new CommitteeRoundRecord { RoundNumber = round };

                // 构建历史摘要与待处理问题
                var reviewHistory = BuildReviewHistorySummary(state);
                var pendingIssues = BuildPendingIssues(state);

                // 1. Analyst 评审
                var analystFeedback = await InvokeReviewerAsync(
                    "Analyst", CommitteePrompts.AnalystReviewerPrompt,
                    state, reviewHistory, pendingIssues, parser, ct).ConfigureAwait(false);

                if (analystFeedback == null)
                {
                    analystFailCount++;
                    if (analystFailCount >= MaxConsecutiveFormatFailures)
                    {
                        return FailMeeting(state, artifacts, $"分析师连续 {MaxConsecutiveFormatFailures} 轮格式失败，模型能力不足");
                    }
                    artifacts.AppendTranscript(state, $"### Analyst: 格式失败 (连续 {analystFailCount} 次)");
                    // 使用空 approve 继续流程
                    analystFeedback = new ReviewerFeedback { Status = "changes_requested", Summary = "格式解析失败", BlockingItems = ["评审输出格式异常，需重试"] };
                }
                else
                {
                    analystFailCount = 0;
                }

                roundRecord.AnalystFeedback = analystFeedback;
                artifacts.WriteAnalystFeedback(state, round, analystFeedback);
                artifacts.AppendTranscript(state, $"### Analyst: {analystFeedback.Status} — {analystFeedback.Summary}");

                // 2. Architect 评审
                var architectFeedback = await InvokeReviewerAsync(
                    "Architect", CommitteePrompts.ArchitectReviewerPrompt,
                    state, reviewHistory, pendingIssues, parser, ct).ConfigureAwait(false);

                if (architectFeedback == null)
                {
                    architectFailCount++;
                    if (architectFailCount >= MaxConsecutiveFormatFailures)
                    {
                        return FailMeeting(state, artifacts, $"架构师连续 {MaxConsecutiveFormatFailures} 轮格式失败，模型能力不足");
                    }
                    artifacts.AppendTranscript(state, $"### Architect: 格式失败 (连续 {architectFailCount} 次)");
                    architectFeedback = new ReviewerFeedback { Status = "changes_requested", Summary = "格式解析失败", BlockingItems = ["评审输出格式异常，需重试"] };
                }
                else
                {
                    architectFailCount = 0;
                }

                roundRecord.ArchitectFeedback = architectFeedback;
                artifacts.WriteArchitectFeedback(state, round, architectFeedback);
                artifacts.AppendTranscript(state, $"### Architect: {architectFeedback.Status} — {architectFeedback.Summary}");

                // 更新历史
                state.Rounds.Add(roundRecord);
                artifacts.UpdateReviewHistory(state);

                // 3. ProjectManager 裁决
                var moderatorDecision = await InvokeModeratorAsync(
                    state, analystFeedback, architectFeedback, reviewHistory, parser, ct).ConfigureAwait(false);

                if (moderatorDecision == null)
                {
                    return FailMeeting(state, artifacts, "项目经理裁决输出格式异常");
                }

                // 强制执行裁决规则
                ApplyModeratorDecisionGuards(moderatorDecision, analystFeedback, architectFeedback, state.CurrentPlan);

                roundRecord.ModeratorDecision = moderatorDecision;
                artifacts.WriteModeratorDecision(state, round, moderatorDecision);
                artifacts.AppendTranscript(state, $"### ProjectManager: {moderatorDecision.Decision} — {moderatorDecision.Reason}");

                // 更新方案
                if (!string.IsNullOrWhiteSpace(moderatorDecision.UpdatedPlan))
                {
                    artifacts.WriteCurrentPlan(state, moderatorDecision.UpdatedPlan);
                }

                artifacts.UpdateReviewHistory(state);
                artifacts.WriteMeetingJson(state);

                // 检查是否 finalize
                if (moderatorDecision.IsFinalize)
                {
                    _logger.LogInformation("委员会第 {Round} 轮达成共识，定稿", round);
                    return FinalizeMeeting(state, artifacts);
                }
            }

            // Phase C: 达到最大轮次
            _logger.LogWarning("委员会达到最大轮次 {MaxRounds}，输出当前最新方案和未解决事项", maxRounds);
            return MaxRoundsReached(state, artifacts);
        }
        catch (OperationCanceledException)
        {
            return FailMeeting(state, artifacts, "会议被取消");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "委员会规划服务异常");
            return FailMeeting(state, artifacts, $"LLM 调用异常: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "委员会规划服务网络异常");
            return FailMeeting(state, artifacts, $"网络异常: {ex.Message}");
        }
    }

    #region Phase A: 初始蓝图

    private async Task<string> LoadOrGenerateInitialBlueprintAsync(
        CommitteePlanningRequest request, CommitteeMeetingState state, CancellationToken ct)
    {
        // 优先读取已有蓝图
        if (!string.IsNullOrEmpty(request.ExistingBlueprintPath) && File.Exists(request.ExistingBlueprintPath))
        {
            _logger.LogInformation("读取现有蓝图: {Path}", request.ExistingBlueprintPath);
            return await File.ReadAllTextAsync(request.ExistingBlueprintPath, ct).ConfigureAwait(false);
        }

        // 调用 ProjectManager 生成初始蓝图
        _logger.LogInformation("调用 ProjectManager 生成初始蓝图");
        var client = _clientFactory.GetClientForRole("ProjectManager");

        var prompt = CommitteePrompts.Fill(CommitteePrompts.InitialBlueprintPrompt, new Dictionary<string, string>
        {
            ["goal"] = request.Goal,
            ["project_summary"] = string.IsNullOrWhiteSpace(request.ProjectSummary)
                ? "无项目背景信息" : TruncateForPrompt(request.ProjectSummary, 3000)
        });

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "你是项目经理，负责生成实施蓝图。只输出 Markdown 格式蓝图，不要输出其他内容。"),
            new(ChatRole.User, prompt)
        };

        var helper = new BufferedNonStreamingLlmHelper(client, _logger);
        return await helper.GetTextAsync(
            messages,
            new ChatOptions { Temperature = 0.3f },
            "committee_initial_blueprint",
            ct).ConfigureAwait(false);
    }

    #endregion

    #region Phase B: 评审循环

    private async Task<ReviewerFeedback?> InvokeReviewerAsync(
        string role, string promptTemplate,
        CommitteeMeetingState state, string reviewHistory, string pendingIssues,
        CommitteeJsonParser parser, CancellationToken ct)
    {
        var client = _clientFactory.GetClientForRole(role);

        var prompt = CommitteePrompts.Fill(promptTemplate, new Dictionary<string, string>
        {
            ["current_plan"] = TruncateForPrompt(state.CurrentPlan, 6000),
            ["review_history"] = TruncateForPrompt(reviewHistory, 2000),
            ["pending_issues"] = string.IsNullOrWhiteSpace(pendingIssues) ? "无待处理历史问题" : pendingIssues
        });

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, $"你是专家委员会的{(role == "Analyst" ? "需求分析师" : "系统架构师")}。只输出 JSON，不要输出任何其他文字。"),
            new(ChatRole.User, prompt)
        };

        var options = new ChatOptions
        {
            Temperature = 0.2f,
            ResponseFormat = ChatResponseFormat.Json
        };

        var helper = new BufferedNonStreamingLlmHelper(client, _logger);
        var operationName = role switch
        {
            "Analyst" => "committee_reviewer_analyst",
            "Architect" => "committee_reviewer_architect",
            _ => "committee_reviewer"
        };
        var rawText = await helper.GetJsonTextAsync(messages, options, operationName, ct).ConfigureAwait(false);

        // 解析链
        var feedback = parser.ParseReviewerFeedback(rawText);
        if (feedback != null) return feedback;

        // JSON 修复（最多 1 次）
        _logger.LogWarning("{Role} JSON 解析失败，尝试修复", role);
        var repaired = await parser.RepairJsonAsync(rawText, client, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(repaired))
        {
            feedback = parser.ParseReviewerFeedback(repaired);
            if (feedback != null)
            {
                _logger.LogInformation("{Role} JSON 修复成功", role);
                return feedback;
            }
        }

        _logger.LogError("{Role} JSON 修复后仍然失败", role);
        return null;
    }

    private async Task<ModeratorDecision?> InvokeModeratorAsync(
        CommitteeMeetingState state,
        ReviewerFeedback analystFeedback, ReviewerFeedback architectFeedback,
        string reviewHistory, CommitteeJsonParser parser, CancellationToken ct)
    {
        var client = _clientFactory.GetClientForRole("ProjectManager");

        var prompt = CommitteePrompts.Fill(CommitteePrompts.ProjectManagerModeratorPrompt, new Dictionary<string, string>
        {
            ["current_plan"] = TruncateForPrompt(state.CurrentPlan, 6000),
            ["analyst_feedback"] = JsonConvert.SerializeObject(analystFeedback),
            ["architect_feedback"] = JsonConvert.SerializeObject(architectFeedback),
            ["review_history"] = TruncateForPrompt(reviewHistory, 2000)
        });

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "你是专家委员会的项目经理兼会议主持人。只输出 JSON，不要输出任何其他文字。"),
            new(ChatRole.User, prompt)
        };

        var options = new ChatOptions
        {
            Temperature = 0.3f,
            ResponseFormat = ChatResponseFormat.Json
        };

        var helper = new BufferedNonStreamingLlmHelper(client, _logger);
        var rawText = await helper.GetJsonTextAsync(messages, options, "committee_moderator", ct).ConfigureAwait(false);

        var decision = parser.ParseModeratorDecision(rawText);
        if (decision != null) return decision;

        // JSON 修复
        _logger.LogWarning("ProjectManager JSON 解析失败，尝试修复");
        var repaired = await parser.RepairJsonAsync(rawText, client, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(repaired))
        {
            decision = parser.ParseModeratorDecision(repaired);
            if (decision != null)
            {
                _logger.LogInformation("ProjectManager JSON 修复成功");
                return decision;
            }
        }

        _logger.LogError("ProjectManager JSON 修复后仍然失败");
        return null;
    }

    #endregion

    #region Moderator 规则强制执行

    /// <summary>
    /// 强制执行 Moderator 裁决规则。
    /// 蓝图§7.2 的强约束在此处硬编码校验，不依赖 LLM 自觉遵守。
    /// </summary>
    internal static void ApplyModeratorDecisionGuards(
        ModeratorDecision decision,
        ReviewerFeedback analystFeedback,
        ReviewerFeedback architectFeedback,
        string previousPlan)
    {
        var bothApprove = analystFeedback.IsApproved && architectFeedback.IsApproved;
        var hasBlockingItems = analystFeedback.BlockingItems.Count > 0 || architectFeedback.BlockingItems.Count > 0;
        var planChanged = !string.IsNullOrWhiteSpace(decision.UpdatedPlan) &&
                          !string.Equals(
                              NormalizePlanForComparison(decision.UpdatedPlan),
                              NormalizePlanForComparison(previousPlan),
                              StringComparison.Ordinal);

        // 双 reviewer 已 approve 时，只允许定稿当前已获批准的版本。
        // 若主持人擅自改稿，该版本没有经过 reviewer 复审，必须忽略改动并沿用 previousPlan 定稿。
        if (bothApprove)
        {
            if (planChanged)
            {
                decision.UpdatedPlan = previousPlan;
                decision.GptFeedback = AppendDecisionNote(
                    decision.GptFeedback,
                    "主持人本轮尝试改稿，但该版本未经 reviewer 复审，已忽略并沿用当前已批准版本定稿。");
                decision.Reason = AppendDecisionNote(
                    decision.Reason,
                    "系统强制: 双方已 approve，忽略未复审的主持人改稿");
            }
            else if (string.IsNullOrWhiteSpace(decision.UpdatedPlan))
            {
                decision.UpdatedPlan = previousPlan;
            }

            decision.Decision = "finalize";
            decision.UnresolvedItems = [];
            return;
        }

        // 规则 1: 有阻塞项时，不得 finalize
        if (hasBlockingItems && decision.IsFinalize)
        {
            decision.Decision = "continue";
            decision.Reason = AppendDecisionNote(decision.Reason, "系统强制: 存在阻塞项，不得定稿");
        }

        // 规则 2: 若修改了方案，不得同时 finalize
        if (decision.IsFinalize && planChanged)
        {
            decision.Decision = "continue";
            decision.Reason = AppendDecisionNote(decision.Reason, "系统强制: 本轮修改了方案，不得同时定稿");
        }

        // 定稿时如果主持人未回传完整方案，回落到当前版本，避免 final_plan 为空。
        if (decision.IsFinalize && string.IsNullOrWhiteSpace(decision.UpdatedPlan))
        {
            decision.UpdatedPlan = previousPlan;
        }

        // 规则 3: finalize 时 unresolved_items 必须为空
        if (decision.IsFinalize && decision.UnresolvedItems.Count > 0)
        {
            decision.Decision = "continue";
            decision.Reason = AppendDecisionNote(decision.Reason, "系统强制: 仍有未解决事项");
        }
    }

    private static string AppendDecisionNote(string current, string note)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return note;
        }

        if (current.Contains(note, StringComparison.Ordinal))
        {
            return current;
        }

        return $"{current} [{note}]";
    }

    private static string NormalizePlanForComparison(string plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) return string.Empty;
        return plan.Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Replace("\r", "\n", StringComparison.Ordinal)
                   .Trim();
    }

    #endregion

    #region 历史追踪

    private static string BuildReviewHistorySummary(CommitteeMeetingState state)
    {
        if (state.Rounds.Count == 0) return "这是首轮评审，无历史记录。";

        var lines = new List<string>();
        foreach (var round in state.Rounds)
        {
            lines.Add($"### Round {round.RoundNumber}");
            if (round.AnalystFeedback != null)
                lines.Add($"- Analyst: {round.AnalystFeedback.Status} — {round.AnalystFeedback.Summary}");
            if (round.ArchitectFeedback != null)
                lines.Add($"- Architect: {round.ArchitectFeedback.Status} — {round.ArchitectFeedback.Summary}");
            if (round.ModeratorDecision != null)
                lines.Add($"- PM Decision: {round.ModeratorDecision.Decision} — {round.ModeratorDecision.Reason}");
        }
        return string.Join("\n", lines);
    }

    private static string BuildPendingIssues(CommitteeMeetingState state)
    {
        var pending = new List<string>();
        foreach (var round in state.Rounds)
        {
            if (round.AnalystFeedback != null)
            {
                foreach (var item in round.AnalystFeedback.BlockingItems)
                {
                    if (!IsResolvedInLaterRounds(item, state.Rounds, round.RoundNumber))
                        pending.Add($"[Analyst R{round.RoundNumber}] {item}");
                }
            }
            if (round.ArchitectFeedback != null)
            {
                foreach (var item in round.ArchitectFeedback.BlockingItems)
                {
                    if (!IsResolvedInLaterRounds(item, state.Rounds, round.RoundNumber))
                        pending.Add($"[Architect R{round.RoundNumber}] {item}");
                }
            }
        }

        return pending.Count == 0 ? string.Empty : string.Join("\n", pending.Select((p, i) => $"{i + 1}. {p}"));
    }

    private static bool IsResolvedInLaterRounds(string item, List<CommitteeRoundRecord> rounds, int fromRound)
    {
        foreach (var round in rounds.Where(r => r.RoundNumber > fromRound))
        {
            var allStatuses = new List<IssueStatus>();
            if (round.AnalystFeedback?.IssueStatuses != null) allStatuses.AddRange(round.AnalystFeedback.IssueStatuses);
            if (round.ArchitectFeedback?.IssueStatuses != null) allStatuses.AddRange(round.ArchitectFeedback.IssueStatuses);

            if (allStatuses.Any(s => s.Item.Contains(item, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(s.Status, "resolved", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    #endregion

    #region 会议终结

    private static CommitteePlanningResult FinalizeMeeting(CommitteeMeetingState state, MeetingArtifactService artifacts)
    {
        state.Status = CommitteeMeetingStatus.Completed;
        state.EndedAt = DateTime.UtcNow;

        artifacts.WriteFinalPlan(state, state.CurrentPlan);
        artifacts.AppendTranscript(state, $"\n## Phase C: 会议结束\n\n状态: 已达成共识\n结束时间: {state.EndedAt:O}");
        artifacts.WriteMeetingJson(state);

        return new CommitteePlanningResult
        {
            MeetingId = state.MeetingId,
            Success = true,
            Status = CommitteeMeetingStatus.Completed,
            FinalPlan = state.CurrentPlan,
            MeetingDirectory = state.MeetingDirectory,
            TotalRounds = state.Rounds.Count
        };
    }

    private static CommitteePlanningResult MaxRoundsReached(CommitteeMeetingState state, MeetingArtifactService artifacts)
    {
        state.Status = CommitteeMeetingStatus.MaxRoundsReached;
        state.EndedAt = DateTime.UtcNow;

        artifacts.WriteFinalPlan(state, state.CurrentPlan);

        var lastRound = state.Rounds.LastOrDefault();
        var unresolved = lastRound?.ModeratorDecision?.UnresolvedItems ?? [];

        artifacts.AppendTranscript(state,
            $"\n## Phase C: 会议结束\n\n状态: 达到最大轮次\n未解决事项: {string.Join(", ", unresolved)}\n结束时间: {state.EndedAt:O}");
        artifacts.WriteMeetingJson(state);

        return new CommitteePlanningResult
        {
            MeetingId = state.MeetingId,
            Success = true,
            Status = CommitteeMeetingStatus.MaxRoundsReached,
            FinalPlan = state.CurrentPlan,
            MeetingDirectory = state.MeetingDirectory,
            UnresolvedItems = [.. unresolved],
            TotalRounds = state.Rounds.Count
        };
    }

    private static CommitteePlanningResult FailMeeting(CommitteeMeetingState state, MeetingArtifactService artifacts, string reason)
    {
        state.Status = CommitteeMeetingStatus.Failed;
        state.EndedAt = DateTime.UtcNow;

        if (!string.IsNullOrEmpty(state.MeetingDirectory) && Directory.Exists(state.MeetingDirectory))
        {
            artifacts.AppendTranscript(state, $"\n## Phase C: 会议失败\n\n原因: {reason}\n结束时间: {state.EndedAt:O}");
            artifacts.WriteMeetingJson(state);
        }

        return new CommitteePlanningResult
        {
            MeetingId = state.MeetingId,
            Success = false,
            Status = CommitteeMeetingStatus.Failed,
            FailureReason = reason,
            MeetingDirectory = state.MeetingDirectory,
            TotalRounds = state.Rounds.Count
        };
    }

    #endregion

    #region Utils

    private static string TruncateForPrompt(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength] + "\n\n... (已截断)";
    }

    #endregion
}
