using Newtonsoft.Json;

namespace CodexFlow.Core.Planning.Committee;

/// <summary>
/// 委员会模式开关。
/// </summary>
public enum CommitteeMode
{
    Off,
    Shadow,
    On
}

/// <summary>
/// 委员会会议状态。
/// </summary>
public enum CommitteeMeetingStatus
{
    Running,
    Completed,
    MaxRoundsReached,
    Failed
}

/// <summary>
/// 委员会规划请求。
/// </summary>
public class CommitteePlanningRequest
{
    /// <summary>用户目标。</summary>
    public string Goal { get; set; } = string.Empty;

    /// <summary>会话 ID。</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>目标仓库根目录。</summary>
    public string WorkspacePath { get; set; } = string.Empty;

    /// <summary>可选：现有蓝图文件路径。</summary>
    public string? ExistingBlueprintPath { get; set; }

    /// <summary>项目摘要（Stage 1 产物）。</summary>
    public string ProjectSummary { get; set; } = string.Empty;
}

/// <summary>
/// 委员会规划结果。
/// </summary>
public class CommitteePlanningResult
{
    /// <summary>会议 ID。</summary>
    public string? MeetingId { get; set; }

    /// <summary>会议是否成功完成（Completed 或 MaxRoundsReached）。</summary>
    public bool Success { get; set; }

    /// <summary>会议最终状态。</summary>
    public CommitteeMeetingStatus Status { get; set; }

    /// <summary>最终蓝图内容（final_plan.md）。</summary>
    public string? FinalPlan { get; set; }

    /// <summary>会议目录路径。</summary>
    public string? MeetingDirectory { get; set; }

    /// <summary>未解决事项（MaxRoundsReached 时非空）。</summary>
    public List<string> UnresolvedItems { get; set; } = [];

    /// <summary>总轮次数。</summary>
    public int TotalRounds { get; set; }

    /// <summary>失败原因（Failed 时非空）。</summary>
    public string? FailureReason { get; set; }
}

/// <summary>
/// 会议运行态。
/// </summary>
public class CommitteeMeetingState
{
    public string MeetingId { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public string TargetDir { get; set; } = string.Empty;
    public string? SourceFile { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public CommitteeMeetingStatus Status { get; set; } = CommitteeMeetingStatus.Running;
    public int CurrentRound { get; set; }
    public int MaxRounds { get; set; } = 5;
    public string MeetingDirectory { get; set; } = string.Empty;
    public string CurrentPlan { get; set; } = string.Empty;
    public List<CommitteeRoundRecord> Rounds { get; set; } = [];
}

/// <summary>
/// 单轮会议记录。
/// </summary>
public class CommitteeRoundRecord
{
    public int RoundNumber { get; set; }
    public ReviewerFeedback? AnalystFeedback { get; set; }
    public ReviewerFeedback? ArchitectFeedback { get; set; }
    public ModeratorDecision? ModeratorDecision { get; set; }
}

/// <summary>
/// 评审者（Analyst / Architect）结构化反馈。
/// </summary>
public class ReviewerFeedback
{
    /// <summary>"approve" 或 "changes_requested"。</summary>
    [JsonProperty("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>一句话总结。</summary>
    [JsonProperty("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>阻塞定稿的问题。</summary>
    [JsonProperty("blocking_items")]
    public List<string> BlockingItems { get; set; } = [];

    /// <summary>非阻塞优化建议。</summary>
    [JsonProperty("non_blocking_items")]
    public List<string> NonBlockingItems { get; set; } = [];

    /// <summary>本轮确认已解决的历史问题。</summary>
    [JsonProperty("resolved_previous_items")]
    public List<string> ResolvedPreviousItems { get; set; } = [];

    /// <summary>历史问题逐项表态。</summary>
    [JsonProperty("issue_statuses")]
    public List<IssueStatus> IssueStatuses { get; set; } = [];

    /// <summary>是否为 approve 状态。</summary>
    [JsonIgnore]
    public bool IsApproved =>
        string.Equals(Status, "approve", StringComparison.OrdinalIgnoreCase)
        && BlockingItems.Count == 0;
}

/// <summary>
/// 历史问题状态。
/// </summary>
public class IssueStatus
{
    [JsonProperty("item")]
    public string Item { get; set; } = string.Empty;

    [JsonProperty("status")]
    public string Status { get; set; } = string.Empty; // resolved | still_blocking | withdrawn

    [JsonProperty("note")]
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// 项目经理裁决。
/// </summary>
public class ModeratorDecision
{
    /// <summary>"continue" 或 "finalize"。</summary>
    [JsonProperty("decision")]
    public string Decision { get; set; } = string.Empty;

    /// <summary>一句话说明判断原因。</summary>
    [JsonProperty("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>本轮采纳/拒绝/保留的阻塞问题摘要。</summary>
    [JsonProperty("gpt_feedback")]
    public string GptFeedback { get; set; } = string.Empty;

    /// <summary>仍需下一轮讨论的问题。</summary>
    [JsonProperty("unresolved_items")]
    public List<string> UnresolvedItems { get; set; } = [];

    /// <summary>完整最新蓝图全文。</summary>
    [JsonProperty("updated_plan")]
    public string UpdatedPlan { get; set; } = string.Empty;

    /// <summary>是否为 finalize 决定。</summary>
    [JsonIgnore]
    public bool IsFinalize =>
        string.Equals(Decision, "finalize", StringComparison.OrdinalIgnoreCase);
}
