namespace CodexFlow.Core.Gateway;

/// <summary>
/// Gateway 消息处理器 — 负责处理单条消息并生成 SSE 事件流
/// <para>
/// 每条消息的处理流程：
/// 1. UserChat → 构造 LLM 上下文 → 调用 LLM → 流式输出 → 持久化
/// 2. TaskCompleted → 构造包含任务结果的 system prompt → 调用 LLM → AI 汇报结果并启动下一步
/// 3. TaskFailed → 检查重试计数 → 自动重试 或 调用 LLM 让 AI 询问用户
/// </para>
/// </summary>
public interface IGatewayMessageProcessor
{
    /// <summary>
    /// 处理一条 Gateway 消息，通过 emitter 回调逐个产出 SSE 事件。
    /// </summary>
    /// <param name="message">待处理的消息</param>
    /// <param name="emitter">SSE 事件发射器，每产出一个事件调用一次</param>
    /// <param name="ct">取消令牌</param>
    Task ProcessAsync(GatewayMessage message, Func<GatewaySseEvent, ValueTask> emitter, CancellationToken ct);
}
