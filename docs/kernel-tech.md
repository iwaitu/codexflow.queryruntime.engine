# Agent Kernel 推理引擎

> 版本：1.0
> 最后更新：2026-04-15
> 项目：CodexFlow (Level 8/9)
> 源码：`CodexFlow.Core/Agents/DefaultCodexKernel.cs`, `CodexFlow.Core/Agents/Adapters/KernelRuntimeEventAdapter.cs`, `CodexFlow.Core/Runtime/`

---

## 1. 概述

`DefaultCodexKernel` 是 CodexFlow 的**核心推理引擎**。它实现了 LLM Agent 的完整 reasoning cycle：接收用户 prompt，调用 LLM 获取响应，解析工具调用，执行工具，收集结果，循环推理，直到任务完成。

Kernel 支持两种执行路径：
- **Runtime 路径** — 通过 `IQueryRuntimeEngine` 驱动的现代化推理循环（默认启用）
- **Manual 路径** — 原有手动推理循环（Runtime 失败时自动 fallback）

一句话总结：

**Kernel 是 Agent 的大脑 — 负责"想（LLM 推理）、做（工具执行）、判断（Critique 反馈）、循环（多轮推理）"的完整认知循环。**

---

## 2. 设计目标

1. **双路径执行** — 优先使用 `IQueryRuntimeEngine`，失败时自动 fallback 到手动实现（通过 `KERNEL_DISABLE_RUNTIME` 环境变量控制）
2. **Critique 反馈循环** — 每次工具调用前进行同行评审，最多重试 3 次
3. **传输层恢复** — 自动重试瞬态传输故障（HTTP 网络中断），指数退避最多 3 次
4. **Malformed 协议恢复** — 检测并自动修复格式错误的工具调用协议，前 3 次 silent retry 不污染上下文
5. **工具执行统计** — 进程级工具调用成功率追踪（total / failed / failureRate）
6. **Guardrail 门控** — 敏感操作拦截（文件路径白名单、命令白名单等）
7. **Legacy 兼容** — 支持旧版文本工具调用格式自动检测和转换
8. **重复工具调用缓存** — 连续 3 次相同签名调用直接返回缓存结果
9. **遥测追踪** — 每轮 query 级别的完整遥测记录（queryId、rounds、termination reason、token 统计）
10. **角色工具过滤** — 根据 CodexAgentRole 动态过滤可用工具集

---

## 3. 架构位置与边界

### 3.1 架构位置

```text
CodexOrchestrator
  -> CodexSessionManager
    -> DefaultCodexKernel                  ← 核心推理引擎
      -> IChatClient                       ← LLM 通信（流式）
      -> IQueryRuntimeEngine               ← 现代化推理循环（优先）
      -> IToolRegistry                     ← 工具发现与执行
      -> ICodexCritiqueService             ← 同行评审反馈
      -> ICodexGuardrail                   ← 安全门控
      -> IAgentRoleRegistry                ← 角色系统提示
      -> IQueryLoopTelemetry               ← 遥测记录
      -> ICodeAnalysisService              ← 代码分析
```

### 3.2 Kernel 的边界

Kernel 负责：

- LLM 流式调用与 thinking chain 收集
- 工具调用解析与执行
- Critique 反馈循环
- 传输层和协议层恢复
- 工具调用统计和遥测

Kernel 不负责：

- 多阶段编排（这是 Orchestrator 的职责）
- 任务规划和分解（这是 Planner 的职责）
- 安全审计（这是 SecurityAuditor 的职责）
- 质量验证（这是 Validator 的职责）
- 会话持久化（这是 SessionManager 的职责）

换句话说：

**Kernel 是单一 reasoning cycle 的执行者，不是多阶段工作流的编排器。**

---

## 4. 核心组件

### 4.1 RunLoopAsync — 主入口

```csharp
Task<CodexResponse> RunLoopAsync(
    CodexSession session,
    string userPrompt,
    CodexAgentRole role = CodexAgentRole.Forge,
    CancellationToken ct = default,
    bool enableTools = true)
```

执行流程：
1. 检查 `IQueryRuntimeEngine` 是否可用（默认路径）
2. 如果 Runtime 失败，fallback 到手动推理循环
3. 手动循环：构建 messages、调用 LLM、解析工具调用、执行、循环
4. 最多 `MaxInternalRounds = 60` 轮内部推理
5. 返回 `CodexResponse`（包含文本响应、thinking 内容、完成状态）

### 4.2 双路径执行

| 路径 | 触发条件 | 优势 |
|------|----------|------|
| Runtime 路径 | `IQueryRuntimeEngine != null` 且 `KERNEL_DISABLE_RUNTIME != true` | 现代化协议、更好的恢复、结构化终止 |
| Manual 路径 | Runtime 不可用或失败 | 向后兼容、经过充分验证 |

### 4.3 角色工具过滤

Kernel 根据角色动态过滤可用工具：

| 角色 | 工具过滤规则 |
|------|-------------|
| `Security` | 仅限 Read 和 Analysis 类别工具 |
| `Forge` | 排除 `execute_code_task` 和 `generate_dev_plan`（防止递归） |
| 其他 | 使用完整工具集 |

### 4.4 流式响应处理

`StreamResponseAsync` 实现流式 LLM 调用：
- 收集 thinking chain 内容（`<think>` 标签内）
- 收集正式响应内容
- 处理空响应和异常终止

---

## 5. 恢复机制

### 5.1 传输层恢复

| 场景 | 策略 |
|------|------|
| HTTP 瞬态故障 | 最多 3 次重试，指数退避（500ms * attempt，最多 1500ms） |
| 超过重试上限 | 返回明确的连接失败消息，不吞掉错误 |

### 5.2 Malformed 协议恢复

| 场景 | 策略 |
|------|------|
| 首次 malformed | Silent retry — 不追加纠正消息到上下文 |
| 第 2-3 次 | 正常重试，追加纠正反馈 |
| 超过 3 次 | 终止推理循环，记录终止原因 |

### 5.3 Critique 反馈循环

| 场景 | 策略 |
|------|------|
| Critique 失败 | 将反馈追加到 messages，重新推理 |
| 连续 3 次失败 | 终止推理循环，记录最后提出的行动和反馈 |

### 5.4 重复工具调用缓存

| 场景 | 策略 |
|------|------|
| 连续 3 次相同签名 | 直接返回缓存结果，不实际执行 |
| 签名计算 | 基于工具名 + 参数哈希 |

---

## 6. 遥测系统

Kernel 实现完整的 query 级别遥测追踪：

| 指标 | 说明 |
|------|------|
| `_ql_queryId` | 唯一查询 ID |
| `_ql_stopwatch` | 查询耗时 |
| `_ql_zeroToolCallRounds` | 零工具调用轮次 |
| `_ql_emptyResponseCount` | 空响应计数 |
| `_ql_recoveryCount` | 恢复次数 |
| `_ql_termination` | 终止原因（Normal / RecoveryExhausted / 等） |
| `_ql_totalPromptTokens` | 总 prompt token 数 |
| `_ql_totalCompletionTokens` | 总 completion token 数 |
| `_ql_initialContextChars` | 初始上下文大小 |

---

## 7. 日志事件索引

Kernel 定义了 43 个结构化日志事件（EventId 1000-1042）：

| EventId | 事件 | 级别 |
|---------|------|------|
| 1000 | 开始推理循环 | Info |
| 1001-1002 | 传输故障与重试 | Error/Warning |
| 1003 | 截断 JSON 自动修复 | Warning |
| 1004-1006 | Malformed 工具调用协议 | Error/Warning |
| 1007 | 响应失败重试 | Warning |
| 1008-1010 | Legacy 工具调用检测 | Warning |
| 1011 | Guardrail 触发 | Warning |
| 1012 | 提出的行动 | Info |
| 1013-1018 | Critique 循环 | Debug/Error |
| 1019-1025 | 空工具名处理 | Warning/Error |
| 1026 | 重复工具调用缓存 | Warning |
| 1027-1032 | 工具执行追踪 | Info/Warning/Error |
| 1033 | 关键错误 | Error |
| 1034-1040 | Legacy 工具调用统计 | Warning/Error |
| 1041-1042 | 动态索引同步 | Info/Warning |

---

## 8. 关键源码索引

| 文件 | 职责 |
|------|------|
| [DefaultCodexKernel.cs](../CodexFlow.Core/Agents/DefaultCodexKernel.cs) | 核心推理引擎 — 2281 行 |
| [KernelRuntimeEventAdapter.cs](../CodexFlow.Core/Agents/Adapters/KernelRuntimeEventAdapter.cs) | Runtime 事件适配器 |
| [ICodexAgentKernel.cs](../CodexFlow.Core/Abstractions/ICodexAgentKernel.cs) | Kernel 接口定义 |
| [CodexToolFunctionAdapterFactory.cs](../CodexFlow.Core/Agents/CodexToolFunctionAdapterFactory.cs) | 工具函数适配 |
| [ToolRegistryBootstrapper.cs](../CodexFlow.Core/Agents/ToolRegistryBootstrapper.cs) | 工具注册引导 |

---

## 11. 相关技术文档

| 文档 | 说明 |
|------|------|
| [Orchestrator 多智能体编排](./orchestrator-tech.md) | 多阶段工作流编排 |
| [Agent 工具链系统](./agent-tools-tech.md) | 工具注册、权限、阶段过滤 |
| [统一会话消息网关](./gateway-tech.md) | Gateway、SessionChannel、SSE |
| [Coordinator 编排系统](./coordinator-tech.md) | Worker 编排、结果回流 |
| [CodexController API 网关](./codex-controller-tech.md) | HTTP API 入口 |
