namespace CodexFlow.Core.Constants;

public static class ExecutionGuardMetadataKeys
{
    public const string ExecuteCodeTaskRetryRequired = "ExecuteCodeTaskRetryRequired";
    public const string ExecuteCodeTaskLastFailure = "ExecuteCodeTaskLastFailure";

    // Bug-Fix-F: prefix for per-task outer-loop retry counters.
    // Full key = ExternalRetryCountPrefix + taskId
    public const string ExternalRetryCountPrefix = "ECT_ExternalRetry_";

    public static string ExternalRetryCountKey(string taskId) => ExternalRetryCountPrefix + taskId;
}
