using CodexFlow.Core.Models;
using System.Collections.ObjectModel;

namespace CodexFlow.Core.Planning.Artifacts;

public enum PlanArtifactStatus
{
    Draft,
    NeedsClarification,
    ReadyForApproval,
    Approved,
    Projecting,
    ReadyToExecute,
    Rejected,
    Executing,
    Completed,
    Superseded,
    Failed
}

public enum PlanArtifactSource
{
    DefaultPlanner,
    CommitteePlanner,
    UserEdited,
    Replan,
    Imported
}

public enum PlanningMode
{
    Off,
    Shadow,
    On
}

public sealed class PlanningOptions
{
    public const string SectionName = "Planning";

    public PlanningMode PlanArtifactMode { get; set; } = PlanningMode.Off;
    public bool PlanApprovalRequired { get; set; }
    public bool PlanPermissionModeEnabled { get; set; }
    public PlanningMode CommitteePlanArtifactMode { get; set; } = PlanningMode.Off;
    public bool PlanProjectionRepairEnabled { get; set; } = true;
    public string PlanMarkdownStyle { get; set; } = "normal";
    public string PlansDirectory { get; set; } = ".codexflow/plans";
}

public sealed class PlanArtifact
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = string.Empty;
    public string Version { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public PlanArtifactStatus Status { get; set; } = PlanArtifactStatus.Draft;
    public PlanArtifactSource Source { get; set; } = PlanArtifactSource.DefaultPlanner;
    public string Goal { get; set; } = string.Empty;
    public string Markdown { get; set; } = string.Empty;
    public string? PreviewProjectedMarkdownHash { get; set; }
    public string? PlanFilePath { get; set; }
    public Collection<CodexTask> Tasks { get; } = [];
    public Collection<PlanReviewRecord> Reviews { get; } = [];
    public PlanApprovalRecord? Approval { get; set; }
    public PlanProjectionRecord? Projection { get; set; }
    public string? ParentPlanId { get; set; }
    public string? SupersededByPlanId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public void ReplaceTasks(IEnumerable<CodexTask>? tasks)
    {
        Tasks.Clear();
        if (tasks == null) return;
        foreach (var task in tasks)
        {
            Tasks.Add(task);
        }
    }

    public void ReplaceReviews(IEnumerable<PlanReviewRecord>? reviews)
    {
        Reviews.Clear();
        if (reviews == null) return;
        foreach (var review in reviews)
        {
            Reviews.Add(review);
        }
    }
}

public sealed class PlanReviewRecord
{
    public string Reviewer { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTime ReviewedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PlanApprovalRecord
{
    public string ApprovedByUserId { get; set; } = string.Empty;
    public DateTime ApprovedAtUtc { get; set; } = DateTime.UtcNow;
    public string Mode { get; set; } = "manual";
    public string? Feedback { get; set; }
    public string ApprovedMarkdownHash { get; set; } = string.Empty;
}

public sealed class PlanProjectionRecord
{
    public DateTime ProjectedAtUtc { get; set; } = DateTime.UtcNow;
    public string ProjectorVersion { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = "codex-task-v1";
    public string ProjectedMarkdownHash { get; set; } = string.Empty;
    public int TaskCount { get; set; }
    public Collection<string> TaskIds { get; } = [];
    public Collection<string> ValidationErrors { get; } = [];
    public Collection<string> ValidationWarnings { get; } = [];

    public void ReplaceTaskIds(IEnumerable<string>? taskIds)
    {
        TaskIds.Clear();
        if (taskIds == null) return;
        foreach (var taskId in taskIds)
        {
            TaskIds.Add(taskId);
        }
    }

    public void ReplaceValidationErrors(IEnumerable<string>? errors)
    {
        ValidationErrors.Clear();
        if (errors == null) return;
        foreach (var error in errors)
        {
            ValidationErrors.Add(error);
        }
    }

    public void ReplaceValidationWarnings(IEnumerable<string>? warnings)
    {
        ValidationWarnings.Clear();
        if (warnings == null) return;
        foreach (var warning in warnings)
        {
            ValidationWarnings.Add(warning);
        }
    }
}

public sealed record PlanProjectionResult(
    bool Success,
    PlanArtifact Artifact,
    IReadOnlyList<CodexTask> Tasks,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record PlanDiffResult(
    string? FromPlanId,
    string ToPlanId,
    IReadOnlyList<string> MarkdownChanges,
    IReadOnlyList<string> TaskChanges);
