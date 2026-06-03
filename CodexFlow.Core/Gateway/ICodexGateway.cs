using System.Threading.Channels;

namespace CodexFlow.Core.Gateway;

/// <summary>
/// CodexGateway — 所有消息的统一入口和 SSE 订阅出口
/// <para>
/// 职责：
/// 1. 接收来自 codex/chat (用户消息) 和后台系统 (任务完成/失败) 的消息
/// 2. 按 Session 维度排队，保证单 Session 内消息按序处理
/// 3. 处理消息时调用 LLM 生成 AI 回复，通过 SSE 事件通道推送给订阅者
/// 4. 处理失败任务的自动重试逻辑（最多 3 次，之后询问用户）
/// 5. 处理成功任务的自动续行逻辑（启动下一个任务）
/// </para>
/// </summary>
public interface ICodexGateway
{
    /// <summary>
    /// 将消息入队到指定 Session 的处理队列。
    /// 消息将按入队顺序被逐个处理。
    /// </summary>
    /// <returns>消息 ID，可用于追踪</returns>
    ValueTask<string> EnqueueAsync(GatewayMessage message, CancellationToken ct = default);

    /// <summary>
    /// 订阅指定 Session 的 SSE 事件流。
    /// 多个客户端可同时订阅同一 Session（多 tab 场景）。
    /// </summary>
    /// <returns>可异步枚举的事件流，断开时自动取消</returns>
    IAsyncEnumerable<GatewaySseEvent> SubscribeAsync(string sessionId, string userId, long lastEventId = 0, CancellationToken ct = default);

    /// <summary>
    /// 向指定 Session 主动推送一个外部 SSE 事件。
    /// 用于规划摘要、系统通知等不需要入队 GatewayMessage 的旁路进度信号。
    /// </summary>
    ValueTask PublishEventAsync(string sessionId, GatewaySseEvent sseEvent, CancellationToken ct = default);

    /// <summary>
    /// 检查指定 Session 当前是否有待处理或正在处理的消息。
    /// </summary>
    bool IsSessionActive(string sessionId);

    /// <summary>
    /// 检查指定 Session 当前是否存在活跃的 SSE 订阅者。
    /// 仅用于在线态兜底判定，不代表 SignalR presence 已建立。
    /// </summary>
    bool HasActiveSubscribers(string sessionId);

    /// <summary>
    /// 关闭指定 Session 的处理循环和所有 SSE 订阅
    /// </summary>
    ValueTask CloseSessionAsync(string sessionId);
}
