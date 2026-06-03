# Coordinator/Worker 术语表（Phase 0 冻结版）

> 状态：Frozen
> 日期：2026-04-12
> 适用范围：coordinator-worker-runtime-upgrade-blueprint.md 及其所有后续实施文档和代码

---

## 目的

冻结 Coordinator/Worker 升级中使用的核心术语，避免同一概念出现多种叫法。
本表是所有新增文档和新增类型的命名权威来源。

---

## 核心术语

| 术语 | 定义 | 代码中的对应物（已有或计划新增） | 禁止的同义替代 |
|---|---|---|---|
| **Coordinator** | 主代理角色。负责规划、派工、综合和用户沟通。不直接执行代码任务 | `CodexOrchestrator`（现有，在 Coordinator 语境下使用时特指其编排职责） | ~~主 agent~~、~~master~~ |
| **Worker** | 被 Coordinator 派出的独立执行体。具有隔离上下文、隔离工具面、标准结果回流 | 计划新增：`WorkerDefinition`, `WorkerType` | ~~subagent~~、~~子代理~~、~~sub-task executor~~ |
| **WorkerType** | Worker 的类型标识。第一阶段共 4 种：`Explore`, `Plan`, `Forge`, `Verify` | 计划新增：`WorkerType` 枚举或注册表 | ~~role~~、~~persona~~（角色 prompt 是 worker 的实现细节，不是类型本身） |
| **WorkerNotification** | Worker 完成/失败/等待时，投影给 Coordinator 模型消费的结构化通知 | 计划新增：`WorkerNotificationEnvelope` | ~~event~~（Event 是内部系统事件）、~~message~~（Message 是 LLM 对话消息） |
| **WaitingUser** | Worker 进入需要用户确认的暂停状态 | 已有：`BackgroundJobStatus.WaitingUserConfirmation` | ~~paused~~、~~blocked~~（这些是通用状态词，不够精确） |
| **Runtime Hook** | `QueryRuntimeEngine` 执行循环中的扩展点 | 计划新增：`IRuntimeHook` | ~~middleware~~、~~interceptor~~ |
| **Worker Hook** | Worker/Job 生命周期中的扩展点 | 计划新增：`IWorkerHook` | ~~callback~~、~~listener~~ |
| **Intervention Hook** | 现有的 tool-level 干预机制（兼容层） | 已有：`IQueryRuntimeInterventionHook` | 不废弃，但新功能不继续往这里塞 |

---

## Worker 类型定义

| WorkerType | 读写性 | 隔离模式 | 典型职责 | 对应现有 prompt |
|---|---|---|---|---|
| `Explore` | 只读 | 无 worktree | 搜索、定位文件、归纳调用链 | `ArchitectPromptTemplate` 中的探索部分 |
| `Plan` | 只读 | 无 worktree | 输出实现路线与关键文件 | `ArchitectPromptTemplate` 中的规划部分 |
| `Forge` | 读写 | Shadow Worktree | 修改代码、执行局部验证 | `ForgePromptTemplate` |
| `Verify` | 只读（可运行命令） | 无 worktree | 运行测试/命令、输出证据化报告 | `SentryPromptTemplate` |

---

## 与现有术语的共存规则

以下现有术语保持不变，不做重命名：

| 现有术语 | 所在层 | 说明 |
|---|---|---|
| `BackgroundJob` / `JobType` | 后台执行基础设施 | Worker 是 Job 的一种特化 JobType，不取代 Job 概念 |
| `OutboxEvent` | 事件投影基础设施 | Worker 通知是从 OutboxEvent 投影而来，不取代 Outbox |
| `QueryRuntimeEngine` | 统一执行循环 | Worker 和 Coordinator 都使用同一个 Runtime 引擎 |
| `DefaultToolExecutionCoordinator` | 工具执行层 | 这里的 "Coordinator" 指工具调用协调，不是主代理编排层的 Coordinator |
| `CodexOrchestrator` | 编排层 | 继续作为主编排器，未来承担 Coordinator 职责 |
| `VllmAgent` | LLM 配置 | 配置命名空间，不改 |
| `DefaultAgentRoleRegistry` | 角色注册 | 保留，Worker 类型系统是其上层扩展 |

---

## 命名纪律

新增代码和文档必须遵守以下规则：

1. 描述 "Coordinator 派出执行体" 时，统一用 **Worker**，不用 agent / subagent / task executor
2. 描述 "Worker 完成后通知 Coordinator" 时，统一用 **WorkerNotification**，不用 event / message / callback
3. 描述 "Worker 的类型" 时，统一用 **WorkerType**，不用 role / persona / agent type
4. 描述 "Runtime 循环中的扩展点" 时，统一用 **Runtime Hook**，不用 middleware / interceptor
5. 描述 "Worker 生命周期扩展点" 时，统一用 **Worker Hook**，不用 callback / listener
6. 现有代码中的 `IQueryRuntimeInterventionHook` 继续称为 **Intervention Hook**，新功能不继续往里加
