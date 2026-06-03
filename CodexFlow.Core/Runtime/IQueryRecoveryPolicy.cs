namespace CodexFlow.Core.Runtime;

/// <summary>
/// Phase 0B: 查询恢复策略接口 — 统一处理 empty response、malformed protocol、stall 等
/// </summary>
public interface IQueryRecoveryPolicy
{
    /// <summary>
    /// 检测是否需要恢复
    /// </summary>
    /// <param name="state">当前 runtime 状态</param>
    /// <param name="request">原始请求</param>
    /// <param name="context">恢复检测上下文</param>
    /// <returns>恢复决策</returns>
    RecoveryDecision DetectRecoveryNeeded(
        QueryRuntimeState state,
        QueryRuntimeRequest request,
        RecoveryContext context);

    /// <summary>
    /// 执行恢复动作
    /// </summary>
    /// <param name="decision">恢复决策</param>
    /// <param name="state">当前 runtime 状态</param>
    /// <returns>恢复动作</returns>
    RecoveryAction GetRecoveryAction(
        RecoveryDecision decision,
        QueryRuntimeState state);

    /// <summary>
    /// 获取恢复提示消息（注入到 messages）
    /// </summary>
    /// <param name="action">恢复动作</param>
    /// <param name="state">当前 runtime 状态</param>
    /// <returns>注入消息内容，或 null</returns>
    string? GetRecoveryPrompt(
        RecoveryAction action,
        QueryRuntimeState state);

    /// <summary>
    /// 判断是否应终止（恢复次数超限）
    /// </summary>
    /// <param name="state">当前 runtime 状态</param>
    /// <param name="recoveryType">恢复类型</param>
    /// <returns>是否应终止 loop</returns>
    bool ShouldTerminate(
        QueryRuntimeState state,
        RecoveryType recoveryType);
}

/// <summary>
/// 恢复类型枚举
/// </summary>
public enum RecoveryType
{
    /// <summary>无需恢复（默认值）</summary>
    None,
    /// <summary>空响应恢复</summary>
    EmptyResponse,
    /// <summary>异常协议恢复</summary>
    MalformedProtocol,
    /// <summary>无工具调用恢复</summary>
    ZeroToolCall,
    /// <summary>传输失败恢复</summary>
    TransportFailure,
    /// <summary>停滞检测</summary>
    StallDetected,
    /// <summary>上下文硬限制</summary>
    ContextHardLimit,
    /// <summary>重复工具执行</summary>
    DuplicateToolExecution,
    /// <summary>自动分发继续</summary>
    AutoDispatchContinuation
}

/// <summary>
/// 恢复动作类型枚举
/// </summary>
public enum RecoveryActionType
{
    /// <summary>继续下一轮</summary>
    Continue,
    /// <summary>终止 loop</summary>
    Terminate,
    /// <summary>注入提示消息后重试本轮</summary>
    InjectMessageAndRetry,
    /// <summary>使用精简 options 重试</summary>
    RetryWithReducedOptions,
    /// <summary>压缩 context 后重试（Phase 5）</summary>
    RetryWithCompactedContext,
    /// <summary>跳过重复工具调用，返回缓存结果</summary>
    SkipRepeatedToolExecution,
    /// <summary>注入紧迫性提示（如轮次即将耗尽）</summary>
    InjectUrgencyPrompt
}