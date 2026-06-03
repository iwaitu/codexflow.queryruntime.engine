namespace CodexFlow.Core.Models.Jobs
{
    /// <summary>
    /// Redis-compatible model for job hot-state visualization
    /// </summary>
    public class JobViewCache
    {
        public string JobId { get; set; } = string.Empty;
        public string? SessionId { get; set; }
        public string? TaskId { get; set; }
        public string JobType { get; set; } = string.Empty;
        public string? WorkerType { get; set; }
        public string? StateKind { get; set; }
        public string? Summary { get; set; }
        public string Status { get; set; } = string.Empty;
        public long LatestSeq { get; set; }
        public bool WaitingUser { get; set; }
        public string? WaitingReason { get; set; }
        public bool RecoveryNeeded { get; set; }
        public string? RecoveryReason { get; set; }
        public string? ResumeStrategy { get; set; }
        public string? ResumeGuidance { get; set; }
        public int Progress { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
