namespace CodexFlow.Core.Runtime;

/// <summary>
/// Query Runtime 的最小上下文治理挂点。
/// 用于在一个 query turn 完成后统一回写会话窗口，并在需要时触发压缩。
/// </summary>
public interface IContextWindowManager
{
    /// <summary>
    /// 在 query turn 开始前预持久化输入消息，确保中途中断时用户输入仍可恢复。
    /// </summary>
    Task OnTurnStartedAsync(
        QueryRuntimeRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// 处理一个已完成的 query turn。
    /// </summary>
    Task OnTurnCompletedAsync(
        QueryRuntimeRequest request,
        QueryRuntimeResult result,
        CancellationToken ct = default);
}
