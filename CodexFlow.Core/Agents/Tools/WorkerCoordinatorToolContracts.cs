using CodexFlow.Core.Protocols;

namespace CodexFlow.Core.Agents.Tools;

public sealed record SpawnWorkerRequest
{
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public required string WorkspacePath { get; init; }
    public required WorkerType WorkerType { get; init; }
    public required string Prompt { get; init; }
    public string? Description { get; init; }
    public string? TaskId { get; init; }
    public bool RunInBackground { get; init; } = true;
    public int? MaxRounds { get; init; }
}

public sealed record SpawnWorkerResult
{
    public required bool Success { get; init; }
    public string? JobId { get; init; }
    public string? JobType { get; init; }
    public string? Status { get; init; }
    public string? Message { get; init; }
}

public enum ContinueWorkerMode
{
    Resumed = 0,
    SpawnedFollowUp = 1
}

public sealed record ContinueWorkerRequest
{
    public required string JobId { get; init; }
    public required string Prompt { get; init; }
    public string? ResumeToken { get; init; }
    public string? SessionIdFallback { get; init; }
    public string? UserIdFallback { get; init; }
    public string? WorkspacePathFallback { get; init; }
}

public sealed record ContinueWorkerResult
{
    public required bool Success { get; init; }
    public required ContinueWorkerMode Mode { get; init; }
    public required string JobId { get; init; }
    public string? NewJobId { get; init; }
    public string? Message { get; init; }
}

public sealed record StopWorkerResult
{
    public required bool Success { get; init; }
    public required string JobId { get; init; }
    public string? Message { get; init; }
}

public sealed record CleanupWorkerWorktreeResult
{
    public required bool Success { get; init; }
    public required string JobId { get; init; }
    public string? WorktreePath { get; init; }
    public bool Cleaned { get; init; }
    public string? Message { get; init; }
}

public sealed record WorkerOutputRequest
{
    public required string JobId { get; init; }
    public long? AfterSeq { get; init; }
    public int MaxEvents { get; init; } = 50;
    public bool IncludeCurrentView { get; init; } = true;
}

public sealed record WorkerOutputEvent
{
    public required long Seq { get; init; }
    public required string EventType { get; init; }
    public required string PayloadJson { get; init; }
    public required DateTime OccurredAtUtc { get; init; }
}

public sealed record WorkerOutputSnapshot
{
    public required string JobId { get; init; }
    public string? SessionId { get; init; }
    public string? TaskId { get; init; }
    public string? WorkerType { get; init; }
    public string? StateKind { get; init; }
    public string? Status { get; init; }
    public string? Summary { get; init; }
    public string? LatestMessage { get; init; }
    public long LatestSeq { get; init; }
    public bool WaitingUser { get; init; }
    public string? WaitingReason { get; init; }
    public string? ResumeToken { get; init; }
    public bool RecoveryNeeded { get; init; }
    public string? RecoveryReason { get; init; }
    public string? ResumeStrategy { get; init; }
    public string? ResumeGuidance { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed record WorkerOutputResult
{
    public required bool Success { get; init; }
    public required string JobId { get; init; }
    public WorkerOutputSnapshot? View { get; init; }
    public IReadOnlyList<WorkerOutputEvent> Events { get; init; } = Array.Empty<WorkerOutputEvent>();
    public string? Message { get; init; }
}

public sealed record WorkerJobSummary
{
    public required string JobId { get; init; }
    public required string WorkerType { get; init; }
    public required string Status { get; init; }
    public string? StateKind { get; init; }
    public string? TaskId { get; init; }
    public string? Summary { get; init; }
    public string? VerificationVerdict { get; init; }
    public string? VerificationSummary { get; init; }
    public IReadOnlyList<string>? VerificationFocusItems { get; init; }
    public string? WaitingReason { get; init; }
    public string? ResumeToken { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}
