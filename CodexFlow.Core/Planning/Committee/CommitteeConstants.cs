namespace CodexFlow.Core.Planning.Committee;

/// <summary>
/// 委员会相关常量。
/// </summary>
public static class CommitteeConstants
{
    // 会议工件文件名
    public const string TranscriptFileName = "transcript.md";
    public const string MeetingJsonFileName = "meeting.json";
    public const string InitialBlueprintFileName = "initial_blueprint.md";
    public const string FinalPlanFileName = "final_plan.md";
    public const string RuntimeDir = "runtime";
    public const string CurrentPlanFileName = "current_plan.md";
    public const string ReviewHistoryFileName = "review_history.json";
    public const string ShadowPlanDiffFileName = "shadow-plan-diff.json";
    public const string ShadowMetricsFileName = "shadow-metrics.json";

    // 每轮文件名模板
    public const string RoundPlanBeforeReviewTemplate = "round-{0:D2}-plan-before-review.md";
    public const string RoundAnalystFeedbackTemplate = "round-{0:D2}-analyst-feedback.md";
    public const string RoundArchitectFeedbackTemplate = "round-{0:D2}-architect-feedback.md";
    public const string RoundProjectManagerFeedbackTemplate = "round-{0:D2}-project-manager-feedback.md";

    // 会议目录名模板
    public const string MeetingDirTemplate = "meeting-{0:yyyyMMdd}-{1:HHmmss}-{2}";

    // Session Metadata Keys
    public const string CommitteeConfirmationPendingKey = "CommitteeConfirmationPending";
    public const string CommitteeConfirmationResultKey = "CommitteeConfirmationResult";
    public const string CommitteeComplexityReasonKey = "CommitteeComplexityReason";

    // 用户确认文案
    public const string CommitteeConfirmationMessage =
        "检测到当前需求属于**复杂需求**，建议召开专家委员会进行多轮结构化评审，以确保实施方案的质量和完整性。\n\n" +
        "**触发原因**: {0}\n\n" +
        "如果您同意，系统将启动由分析师、架构师和项目经理组成的委员会进行方案评审。\n" +
        "如果您不同意，系统将使用常规单次规划流程。\n\n" +
        "请回复 **\"同意\"** 或 **\"agree\"** 启动委员会，回复其他内容将继续使用常规规划。";

    // 用户拒绝关键词（优先匹配，防止"不同意"被子串匹配为"同意"）
    public static readonly string[] RejectKeywords =
    [
        "不同意", "不要", "不需要", "不用", "别", "拒绝", "取消",
        "不好", "不可以", "不行", "算了", "跳过", "不是",
        "disagree", "no", "nope", "skip", "cancel", "reject", "decline"
    ];

    // 用户确认关键词（仅在未命中 RejectKeywords 时匹配）
    public static readonly string[] AcceptKeywords =
    [
        "同意", "agree", "yes", "好", "好的", "可以", "启动", "开始",
        "确认", "是", "是的", "ok", "OK", "确定", "赞成"
    ];
}
