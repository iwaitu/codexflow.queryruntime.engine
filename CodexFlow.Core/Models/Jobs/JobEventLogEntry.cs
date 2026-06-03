namespace CodexFlow.Core.Models.Jobs
{
    /// <summary>
    /// MongoDB document for persistent job event history
    /// </summary>
    public class JobEventLogEntry
    {
        public string EventId { get; set; } = string.Empty;

        public string? JobId { get; set; }
        
        public string? SessionId { get; set; }
        
        public string? TaskId { get; set; }
        
        public long Seq { get; set; }
        
        public string EventType { get; set; } = string.Empty;
        
        public string PayloadJson { get; set; } = "{}";
        
        public DateTime OccurredAtUtc { get; set; }
        
        public DateTime LoggedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
