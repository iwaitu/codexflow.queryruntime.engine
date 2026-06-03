namespace CodexFlow.Core.Runtime;

/// <summary>
/// Options that control streaming-first tool execution.
/// </summary>
public sealed class StreamingToolExecutionOptions
{
    public const string SectionName = "Runtime:StreamingToolExecution";

    /// <summary>Whether streaming-first tool execution is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether only read-only tools may be started before the model stream ends.</summary>
    public bool ReadOnlyOnly { get; set; } = true;

    /// <summary>Maximum number of in-flight streaming-first tools per model round.</summary>
    public int MaxConcurrentStreamingTools { get; set; } = 4;

    /// <summary>Optional allow-list. Empty means any eligible tool is allowed.</summary>
    public List<string> AllowToolNames { get; set; } = [];

    /// <summary>Optional deny-list that always wins over the allow-list.</summary>
    public List<string> DenyToolNames { get; set; } = [];

    /// <summary>Whether the runtime should emit diagnostic decision events.</summary>
    public bool EmitDecisionEvents { get; set; }

    /// <summary>Whether skip decisions should be logged at debug level.</summary>
    public bool LogSkippedDecisions { get; set; } = true;
}
