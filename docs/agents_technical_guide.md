# CodexFlow Coordinator/Worker 技术指南

本文档描述的是 **CodexFlow 当前仓库已经落地的 Coordinator/Worker 执行系统**，不是外部产品的通用多 Agent 设想，也不是历史上的“单主代理 + 原子代码任务执行器”模型。

---

## 1. 核心结论

CodexFlow 当前的真实执行模型可以概括为：

```text
Coordinator（主会话编排）
  + Worker（正式运行时对象）
  + Unified Query Runtime（统一 loop / tool / recovery）
  + BackgroundJob / Outbox / Gateway / WebUI（统一事件与可观测性）
```

这意味着：

- 主会话负责用户交互、计划推进、worker 派工和结果综合
- worker 负责具体探索、规划、实现或验证
- worker 不是“换一段 prompt 再调用一次模型”，而是有独立类型、工具面、生命周期、结果协议和恢复语义
- 主会话、后台任务、网关、通知和前端消费的是同一套 worker 结果协议

---

## 2. 角色分工

### 2.1 Coordinator

Coordinator 的职责：

- 理解用户目标与当前会话上下文
- 根据 task list 与系统状态决定下一步
- 选择是继续串行任务，还是派发并行 worker
- 理解 worker XML envelope、checklist、resume playbook
- 基于结果决定继续、停止、追问用户或派发下一个 worker

Coordinator 当前并不等于“什么都自己做”。相反，它的价值在于：

- 收敛上下文
- 控制节奏
- 保持主会话稳定
- 减少单轮对话被长时执行、验证细节和恢复噪声拖垮

### 2.2 Worker

Worker 是一等运行时对象，当前已落地四类：

| Worker | 角色 | 默认语义 |
|--------|------|----------|
| `explore` | 并行只读探索 | 查结构、找证据、做局部理解 |
| `plan` | 只读规划补充 | 输出更细的实施策略和边界 |
| `forge` | 写型执行 worker | 在 shadow worktree 中完成代码实现 |
| `verify` | 证据型验证 worker | 给出 verification report 与 checklist |

这些 worker 都通过 `BackgroundJobRunner` 执行，但它们不再只是泛化的 `BackgroundJob`。它们具备：

- 独立 `WorkerType`
- 独立工具白名单
- 独立 prompt / runtime context
- 独立结果摘要
- 独立恢复策略

---

## 3. 对外工具面

Coordinator 当前可见的关键工具如下：

- `start_next_task`
- `spawn_worker`
- `continue_worker`
- `stop_worker`
- `list_workers`
- `cleanup_worker_worktree`

推荐使用方式：

| 场景 | 推荐工具 |
|------|----------|
| 顺序推进计划中的下一个代码任务 | `start_next_task` |
| 并行做只读探索 / 规划 / 验证 | `spawn_worker` |
| 在隔离环境中执行实现工作 | `spawn_worker(worker_type=\"forge\")` |
| worker 已完成、等待用户或进入 recovery-needed 后继续 | `continue_worker` |
| 中止不再需要的 worker | `stop_worker` |
| 清理已结束 forge worker 的 shadow worktree | `cleanup_worker_worktree` |

兼容说明：

- `execute_code_task` 仍然存在，但属于兼容层，不是当前推荐主路径

---

## 4. 生命周期与状态机

### 4.1 Worker 生命周期

```text
Queued
  -> Running
     -> Completed
     -> Failed
     -> WaitingUser
     -> FailedRecoveryNeeded
     -> Cancelled
```

### 4.2 状态的统一投影

同一个 worker 状态会被同时投影到：

- PostgreSQL `BackgroundJob`
- `JobCheckpoint`
- Outbox 事件
- Redis hot view
- SignalR `OnJobUpdate`
- Gateway SSE worker lifecycle events
- 主会话中的 XML worker envelope
- WebUI worker badge / recent workers / recovery bar

因此，worker 不再是“后台黑盒”。

---

## 5. 结果协议

### 5.1 最小结构化字段

当前 worker 结果至少会包含以下字段中的一部分：

- `workerType`
- `summary`
- `result`
- `waitingReason`
- `recoveryNeeded`
- `recoveryReason`
- `resumeStrategy`
- `resumeGuidance`
- `resumePlaybook`
- `workerNotificationXml`

### 5.2 XML Envelope

主会话、通知系统和后续 worker follow-up 优先消费 XML envelope，而不是依赖自由文本推断。

典型 envelope 会表达：

- worker 类型
- 状态
- summary
- result
- usage
- verification 报告
- recovery 信息

这使得主会话可以在不重新解析大量自然语言的情况下决定下一步。

---

## 6. `forge` Worker

`forge` worker 是当前默认的写型执行模型。

### 6.1 关键特性

- 默认在 shadow worktree 中执行
- 结果会带回 `changedFiles`、shadow 路径、commit hash、summary
- follow-up worker 可以复用同一个 worktree
- 完成后可显式清理 worktree

### 6.2 为什么这样设计

目标不是“为了炫技做 Git worktree”，而是为了解决两个现实问题：

1. 长时实现和修复过程不能污染主工作区  
2. 后续追问和修复必须能延续同一执行现场

---

## 7. `verify` Worker

`verify` worker 当前已经与 repair 闭环打通。

### 7.1 输出内容

- `verification-report` XML
- `verificationSummary`
- `issues`
- `evidence`
- `verificationChecklist`

### 7.2 Checklist 结构

当前 checklist 会收口为：

- `completed`
- `failed`
- `pending`
- `focusItems`

### 7.3 为什么 checklist 很重要

如果没有结构化 checklist，主会话继续修复时只能把整段报告回灌给模型，容易出现：

- 丢失重点
- 重复修已经通过的项
- follow-up prompt 噪声过大

现在 `continue_worker` 会优先读取 checklist，而不是把 raw report 当成唯一输入。

---

## 8. 恢复与治理

### 8.1 当前已治理的故障面

Unified Runtime 已对以下情况做最小恢复治理：

- 空响应
- stall / 重复工具调用
- malformed protocol
- transport 闪断
- host cancellation
- lease expired

### 8.2 Recovery-Needed 语义

当 runtime 自动恢复耗尽时，worker 不再直接等价于普通失败，而会尽量进入：

- `FailedRecoveryNeeded`
- `recoveryNeeded=true`
- `recoveryReason`
- `resumeStrategy=continue_worker`
- `resumePlaybook`

### 8.3 Resume Playbook

resume playbook 是恢复层的结构化资产，当前会包含：

- 失败原因
- 恢复建议
- 下一步动作
- 建议 prompt
- 分步检查项
- runtime flags

这份信息会同时投影给：

- `continue_worker`
- 主会话
- `/api/jobs`
- WebUI recovery bar

---

## 9. Gateway 与前端事件面

### 9.1 Gateway SSE

当前 Gateway 已补齐 worker 专用事件：

- `worker_started`
- `worker_waiting_user`
- `worker_completed`
- `worker_failed`

这些事件由 `GatewayRuntimeEventAdapter` 统一映射。

### 9.2 WebUI

WebUI 当前已经具备最小 worker 可视态：

- 运行中的 worker
- WaitingUser worker
- 最近完成的 worker
- FailedRecoveryNeeded recovery bar

前端同时消费：

- Gateway SSE
- `/api/jobs`
- SignalR `OnJobUpdate`

这是当前的兼容方案，不是重复设计。

---

## 10. 当前边界

### 10.1 已经完成的事情

- Coordinator/Worker 协议面对主会话正式可见
- 四类 worker 已稳定实例化
- verify checklist 与 repair follow-up 已闭环
- Gateway 与 WebUI 已理解 worker 生命周期
- resume playbook 与 recovery-needed 已成为正式状态

### 10.2 尚未完全迁出的兼容资产

以下能力仍然存在，但不应再作为新设计的中心：

- `execute_code_task`
- `ExecuteCodeTask` background job
- 只识别传统 job 成功/失败文本的旧客户端脚本

因此，任何新的入口、客户端或流程验证都应优先围绕 Worker 协议面建设。

---

## 11. 参考文档

- [技术白皮书](../TECHNICAL_WHITEPAPER.md)
- [Coordinator/Worker Runtime 升级蓝图](archived-blueprints/coordinator-worker-runtime-upgrade-blueprint.md)
- [统一会话消息网关](gateway-tech.md)
- [后台作业调度器](job-supervisor-tech.md)

