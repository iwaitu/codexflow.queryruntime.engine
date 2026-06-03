# ADR-003: Hook 体系分层边界

> **状态**: Accepted
> **日期**: 2026-04-12
> **决策者**: CTO Review + Claude Code
> **适用范围**: Coordinator/Worker 升级蓝图中的 Hook 扩展体系设计

---

## 背景

当前 CodexFlow 已有 `IQueryRuntimeInterventionHook`，提供 2 个 hook 节点：

- `OnToolCallRequestedAsync`（Guardrail）
- `OnToolExecutionCompletedAsync`（Critique）

Coordinator/Worker 升级需要更多的扩展点（worker 生命周期通知、response 修正、envelope 生成等）。长期规划包含 15 个 hook 节点，但一次性全部实现的成本过高。

本 ADR 划定 hook 体系的分层边界和第一阶段落地范围。

---

## 决策

### 1. Hook 分为三层

| 层 | 接口 | 作用范围 | 状态 |
|---|---|---|---|
| **Intervention Hook** | `IQueryRuntimeInterventionHook` | 工具执行前后的安全干预 | 已有，保留为兼容层 |
| **Runtime Hook** | `IRuntimeHook`（计划新增） | 单次 query/tool loop 生命周期 | 长期规划 8 个节点，第一阶段落 1 个 |
| **Worker Hook** | `IWorkerHook`（计划新增） | Worker/Job 生命周期 | 长期规划 7 个节点，第一阶段落 3 个 |

### 2. 第一阶段只落 4 个 hook

| Hook | 所属层 | 触发时机 | 第一阶段用途 |
|---|---|---|---|
| `OnAfterModelResponse` | Runtime Hook | LLM 响应返回后、进入 tool loop 前 | response 修正、worker 摘要抽取 |
| `OnWorkerCompleted` | Worker Hook | Worker 正常完成 | 生成完成通知 envelope |
| `OnWorkerFailed` | Worker Hook | Worker 异常失败 | 生成失败通知 envelope |
| `OnWorkerWaitingUser` | Worker Hook | Worker 进入 WaitingUser | 生成 WaitingUser 通知 envelope |

其余 11 个 hook 节点（`OnPromptComposed`, `OnBeforeModelRequest`, `OnRecoveryTriggered`, `OnRoundCompleted`, `OnQueryCompleted`, `OnWorkerSpawned`, `OnWorkerResumed`, `OnWorkerHeartbeat`, `OnWorkerCancelled` 等）本轮只做概念预留：

- 在文档和术语表中定义其语义
- 不定义接口
- 不实现 dispatcher
- 不编写测试

### 3. 现有 Intervention Hook 保留为兼容层

- `IQueryRuntimeInterventionHook` 继续工作，不废弃
- 不再向其新增功能
- 新的 guardrail / critique 需求优先考虑新 Runtime Hook 接口
- 未来可通过适配器将旧 hook 映射到新接口

### 4. 每个 hook 必须定义失败语义

每个已实现的 hook 必须明确以下三选一的失败行为：

| 失败策略 | 含义 | 适用场景 |
|---|---|---|
| fail-open | hook 异常时视为"无干预"，继续执行 | 统计、采样、日志类 hook |
| fail-closed | hook 异常时阻断流程 | 安全类 hook（如 guardrail） |
| fail-log-and-continue | hook 异常时记录日志，继续执行 | 通知生成、摘要类 hook |

第一阶段 4 个 hook 的失败策略：

| Hook | 失败策略 | 理由 |
|---|---|---|
| `OnAfterModelResponse` | fail-log-and-continue | 修正失败不应阻断主循环 |
| `OnWorkerCompleted` | fail-log-and-continue | 通知生成失败不应影响 worker 完成状态 |
| `OnWorkerFailed` | fail-log-and-continue | 同上 |
| `OnWorkerWaitingUser` | fail-log-and-continue | 同上 |

### 5. Hook 可观测性要求

每个已实现的 hook 必须记录：

- 是否被执行
- 执行耗时
- 是否修改了输入/输出
- 是否发生异常

通过标准日志（`ILogger`）和现有 telemetry 体系记录，不引入独立监控。

---

## 理由

1. **渐进式演进**：15 个 hook 一次性落地的成本（接口 × 实现 × 注册 × 测试 × 可观测性）远超 worker 系统本身。先落最小必需集，后续按需扩展。
2. **兼容性**：不废弃现有 Intervention Hook，避免回归。
3. **明确失败语义**：防止 hook 异常导致不可预期的系统行为。第一阶段的 4 个 hook 全部是 fail-log-and-continue，因为它们都是通知/修正类，不是安全关键路径。
4. **可观测性先行**：hook 行为必须可追踪，否则出问题时无从排查。

---

## 影响

- Phase 1.5 新增 `IWorkerHook` 接口（3 个方法）和可选的最小 `IRuntimeHook`（1 个方法）
- 新增 hook dispatcher / manager
- 新增 `WorkerHookDispatcherTests`，验证注册 hook 后 `OnWorkerCompleted` 被正确触发
- 现有 `IQueryRuntimeInterventionHook` 保持不变，不增不减

---

## 替代方案（已否决）

| 方案 | 否决理由 |
|---|---|
| 第一阶段落全部 15 个 hook | 工作量大于 worker 系统本身，投入产出比太低 |
| 不做 hook，直接在业务代码中写通知逻辑 | 导致 envelope 生成散落在 Gateway / BackgroundJob / Validator 多处 |
| 废弃 Intervention Hook，全部迁移到新接口 | 风险太高，guardrail/critique 是生产关键路径 |
| 用 .NET event / delegate 代替 hook 接口 | 失去强类型约束和 DI 注入能力 |
