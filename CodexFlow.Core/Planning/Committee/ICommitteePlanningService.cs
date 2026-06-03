namespace CodexFlow.Core.Planning.Committee;

/// <summary>
/// 委员会规划服务接口。
/// 负责多轮结构化评审流程，产出最终蓝图。
/// </summary>
public interface ICommitteePlanningService
{
    /// <summary>
    /// 执行委员会规划会议。
    /// </summary>
    Task<CommitteePlanningResult> RunCommitteePlanningAsync(
        CommitteePlanningRequest request,
        CancellationToken ct = default);
}
