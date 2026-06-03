namespace CodexFlow.Core.Runtime;

/// <summary>
/// Phase 0B: 统一 Query Runtime 执行引擎接口 — 负责 query/tool loop 的统一状态机和事件产出
/// </summary>
public interface IQueryRuntimeEngine
{
    /// <summary>
    /// 执行一次完整的 query turn（包含多轮 tool loop）
    /// </summary>
    /// <param name="request">执行请求，包含初始 messages、tools、maxRounds 等</param>
    /// <param name="eventSink">内部事件消费端，用于 SSE adapter 或 telemetry 消费</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>终止结果，包含 termination reason、total rounds、tool call 统计等</returns>
    Task<QueryRuntimeResult> ExecuteAsync(
        QueryRuntimeRequest request,
        IQueryRuntimeEventSink eventSink,
        CancellationToken ct = default);
}