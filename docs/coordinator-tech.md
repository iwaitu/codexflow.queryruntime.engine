# Coordinator 编排系统

> 版本：1.0
> 最后更新：2026-04-14
> 项目：CodexFlow (Level 9)
> 源码：`CodexFlow/Gateway/`, `CodexFlow/Services/Background/WorkerCoordinatorService.cs`, `CodexFlow/Services/Notifications/MainSessionInjectionService.cs`, `CodexFlow.Core/Workers/`

---

## 1. 概述

`Coordinator` 是 CodexFlow 当前主执行架构中的**编排层**。它不直接承担所有代码执行，而是负责：

- 解释用户目标
- 维持主会话上下文
- 选择当前应走串行任务还是并行 worker
- 通过正式工具面派发 `explore / plan / forge / verify` worker
- 消费 worker 回流结果并决定下一步动作

它对应的不是单一类，而是一组协作组件：

- `GatewayMessageProcessor`：主会话入口与决策核心
- `WorkerCoordinatorService`：worker 生命周期后端实现
- `MainSessionInjectionService`：把 worker 结果注回主会话
- `CodexGateway / SessionChannel`：为 Coordinator 提供单 Session 顺序消费的消息总线

一句话总结：

**Coordinator 负责“决定做什么、让谁做、结果如何回到主会话”，而不是亲自完成所有执行。**

---

## 2. 设计目标

Coordinator 层的设计目标有四个：

1. **主会话单点编排**
   - 同一 `session` 内的主决策必须串行、可追踪、可重放
2. **执行职责外移**
   - 探索、规划、写入、验证等细粒度工作下沉给正式 worker
3. **结果统一回流**
   - worker 完成、失败、等待用户、恢复需求都必须通过统一协议回到主会话
4. **恢复优先**
   - 模型空响应、流式中断、worker 租约过期等情况不能只写日志，必须进入可恢复编排路径

---

## 3. 架构位置与边界

### 3.1 架构位置

```text
User / Client
  -> CodexGatewayController
    -> CodexGateway / SessionChannel
      -> GatewayMessageProcessor   ← Coordinator 决策核心
        -> QueryRuntimeEngine
        -> Worker tools
          -> WorkerCoordinatorService
            -> BackgroundJobService / BackgroundJobRunner
              -> Explore / Plan / Forge / Verify worker
                -> Worker result / XML envelope / job view
                  -> MainSessionInjectionService
                    -> CodexGateway / SessionChannel
                      -> GatewayMessageProcessor
```

### 3.2 Coordinator 的边界

Coordinator 负责：

- 主会话 prompt 组织
- 当前工具面投递
- 决定是否调用 `start_next_task`
- 决定是否 `spawn_worker`
- 决定是否 `continue_worker`
- 消费 worker 结果并更新主会话语义

Coordinator 不负责：

- 直接实现 worker 运行时
- 直接执行后台 job 调度
- 直接定义 worker 工具白名单
- 直接实现底层流式恢复
- 直接维护 job view / outbox / SignalR 投影

换句话说：

**Coordinator 是编排层，不是 runtime 层，也不是 job 宿主层。**

---

## 4. 核心组件

### 4.1 GatewayMessageProcessor

`GatewayMessageProcessor` 是当前 Coordinator 的主入口。

核心职责：

- 为用户消息、任务完成通知、失败通知构造主会话上下文
- 构造 system prompt
- 注入 worker 工具面
- 调用 `IQueryRuntimeEngine`
- 将 runtime 事件映射为 Gateway SSE
- 消费 worker completion / recovery 通知并继续编排

关键代码：

- [GatewayMessageProcessor.cs](../CodexFlow/Gateway/GatewayMessageProcessor.cs)
- [IGatewayMessageProcessor.cs](../CodexFlow.Core/Gateway/IGatewayMessageProcessor.cs)

### 4.2 WorkerCoordinatorService

`WorkerCoordinatorService` 是 Coordinator 到 worker/job 系统之间的后端桥接层。

核心职责：

- 创建 worker job
- 继续已有 worker
- 停止 worker
- 整理 worker payload 和 resume 信息
- 管理 forge worker 的工作区清理等辅助操作

关键代码：

- [WorkerCoordinatorService.cs](../CodexFlow/Services/Background/WorkerCoordinatorService.cs)

### 4.3 MainSessionInjectionService

`MainSessionInjectionService` 负责把 worker 结果重新注入主会话，让 Coordinator 在后续轮次能“看到”后台结果。

注入内容包括：

- worker 完成摘要
- worker 失败摘要
- XML worker envelope
- recovery-needed 结果
- waiting-user 提示

关键代码：

- [MainSessionInjectionService.cs](../CodexFlow/Services/Notifications/MainSessionInjectionService.cs)

### 4.4 SessionChannel / CodexGateway

Coordinator 并不直接消费 HTTP 请求，而是建立在 session 级消息总线之上。

这样做的价值是：

- 同一会话消息顺序可控
- 用户消息与后台通知共用同一编排入口
- SSE 重放、多订阅者广播和断线恢复统一处理

关键代码：

- [SessionChannel.cs](../CodexFlow/Gateway/SessionChannel.cs)
- [CodexGateway.cs](../CodexFlow/Gateway/CodexGateway.cs)

---

## 5. Coordinator 的对外工具面

当前主会话面向模型暴露的编排工具主要包括：

| 工具 | 作用 | 典型场景 |
|------|------|----------|
| `start_next_task` | 启动计划中的下一个串行任务 | 当前已有 task list，继续按计划推进 |
| `spawn_worker` | 启动新的 worker | 需要并行探索、规划、验证，或启动隔离写型 worker |
| `continue_worker` | 继续已有 worker | worker `WaitingUser`、`FailedRecoveryNeeded`、follow-up 续跑 |
| `stop_worker` | 中止 worker | 用户取消、worker 偏航、长时间无价值执行 |
| `list_workers` | 查询当前 worker | 需要了解后台执行面状态 |
| `cleanup_worker_worktree` | 清理 forge worker 工作区 | worker 完成或放弃后清理隔离环境 |

设计原则：

1. Coordinator 不直接暴露底层 job 操作，而是只暴露语义级 worker 工具
2. 恢复入口统一优先用 `continue_worker`
3. 并行只读与隔离写入通过 `worker_type` 区分，而不是通过 prompt 自行约定

---

## 6. 编排状态流转

### 6.1 主会话层

Coordinator 主会话视角的最小状态机可以抽象为：

```text
Idle
  -> ReadingUserIntent
  -> DecidingNextAction
     -> StartNextTask
     -> SpawnWorker
     -> ContinueWorker
     -> AskUser
     -> FinishTurn
```

### 6.2 Worker 回流层

Coordinator 消费 worker 结果时，重点看的是这些状态：

| Worker 状态 | Coordinator 默认动作 |
|-------------|----------------------|
| `Completed` | 读取摘要/结果，决定是否继续主线 |
| `Failed` | 生成失败解释，必要时转用户确认 |
| `WaitingUser` | 把阻塞原因转成主会话可读内容 |
| `FailedRecoveryNeeded` | 消费 `resumePlaybook`，决定是否 `continue_worker` |
| `Cancelled` | 记录取消结果并回收执行上下文 |

### 6.3 计划驱动关系

Coordinator 与 task list 的关系必须稳定：

- `Plan` 阶段以 task list snapshot 落地为成功判据
- `Execute` 只消费已落地计划
- 当 `planCount > 0` 时，不应无条件重新 `generate_dev_plan`
- 若 worker 结果改变计划前提，应通过明确的重规划决策进入新计划，而不是静默覆盖

---

## 7. 结果回流协议

Coordinator 消费的不是任意自然语言，而是统一的 worker 结果协议。

最关键的回流字段包括：

| 字段 | 作用 |
|------|------|
| `workerType` | 识别 worker 类型 |
| `summary` | 给主会话和 UI 的短摘要 |
| `result` | 详细正文 |
| `recoveryNeeded` | 是否进入恢复治理 |
| `recoveryReason` | 恢复原因 |
| `resumeStrategy` | 当前建议恢复动作 |
| `resumeGuidance` | 人和模型都可读的恢复建议 |
| `resumePlaybook` | 结构化恢复剧本 |
| `workerNotificationXml` | 注入主会话的正式 XML 信封 |

Coordinator 读取这些字段后，才决定：

- 继续主线
- 继续某个 worker
- 请求用户确认
- 中止某个路径
- 进入验证

---

## 8. 恢复与治理职责划分

Coordinator 不是所有恢复逻辑的执行者，但它必须消费恢复结果并做出下一步决策。

职责划分如下：

| 层级 | 职责 |
|------|------|
| `VllmBaseChatClient` | 处理流式协议和非标准工具输出 |
| `QueryRuntimeEngine` | 处理 round、tool、termination、transport / empty / malformed recovery |
| `BackgroundJobRunner` | 把 worker runtime 结果映射到 job 语义 |
| `Coordinator` | 基于 `recoveryNeeded + resumePlaybook` 做编排决策 |

这意味着 Coordinator 应当做到：

1. 不吞掉底层恢复信号
2. 不把 recovery-needed 误当普通失败
3. 优先通过统一入口 `continue_worker` 恢复
4. 在主会话里保留足够的用户可理解解释

---

## 9. 与旧执行模型的关系

当前主链已经切到 Coordinator/Worker 协议面。

因此：

- `execute_code_task` 类模型属于兼容资产
- 新的主会话、worker、恢复、通知语义都应以 Coordinator/Worker 为准
- 旧模型不应继续作为正式技术文档的主叙事中心

如果文档需要描述当前主执行路径，应优先表述为：

- Coordinator 决策
- Worker 执行
- 结果回流
- 恢复治理

而不是：

- 单主代理直接执行所有任务

---

## 10. 关键源码索引

| 文件 | 职责 |
|------|------|
| [GatewayMessageProcessor.cs](../CodexFlow/Gateway/GatewayMessageProcessor.cs) | Coordinator 主会话入口 |
| [WorkerCoordinatorService.cs](../CodexFlow/Services/Background/WorkerCoordinatorService.cs) | worker 生命周期操作 |
| [MainSessionInjectionService.cs](../CodexFlow/Services/Notifications/MainSessionInjectionService.cs) | worker 结果回流主会话 |
| [SessionChannel.cs](../CodexFlow/Gateway/SessionChannel.cs) | Session 顺序消费与广播 |
| [WorkerDefinitions.cs](../CodexFlow.Core/Workers/WorkerDefinitions.cs) | worker 类型与工具白名单定义 |
| [BackgroundJobRunner.cs](../CodexFlow/Services/Background/BackgroundJobRunner.cs) | worker 实际执行宿主 |
| [WorkerHookDispatcher.cs](../CodexFlow/Services/Hooks/WorkerHookDispatcher.cs) | worker 生命周期 hook 分发 |

---

## 11. 相关技术文档

| 文档 | 说明 |
|------|------|
| [统一会话消息网关](./gateway-tech.md) | Gateway、SessionChannel、SSE |
| [后台作业调度器](./job-supervisor-tech.md) | Job、租约、Outbox、后台执行 |
| [Agent 工具链系统](./agent-tools-tech.md) | 工具注册、权限矩阵、阶段过滤 |
| [会话上下文与记忆管理](./session-context-tech.md) | 记忆、事实、召回与上下文治理 |

