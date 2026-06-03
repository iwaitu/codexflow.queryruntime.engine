# Planner 规划系统

> 版本：1.0
> 最后更新：2026-04-15
> 项目：CodexFlow (Level 9)
> 源码：`CodexFlow.Core/Agents/DefaultCodexPlanner.cs`、`CodexFlow.Core/Abstractions/IAgentInterfaces.cs`

---

## 1. 概述

`Planner` 是 CodexFlow 架构中的**任务分解引擎**。它将用户的高级目标转换为结构化的任务清单，包含任务描述、依赖关系、风险级别、复杂度分级和结构化验收契约，为 Orchestrator 的执行阶段提供可追踪的工作计划。

一句话总结：

**Planner 负责"将用户目标拆分为最少且完整的可执行任务清单"，而非执行任务本身或直接操作代码。**

---

## 2. 设计目标

Planner 层的设计目标有五个：

1. **最少任务原则**
   - 用最少的任务完成目标，合并同类项，绝对上限 10 个任务
2. **结构化验收契约**
   - 每个任务必须声明 `RequiredArtifacts` 和 `ForbiddenStates`，作为后续验证的硬性判据
3. **自适应效能分级**
   - 根据任务性质自动分级（Level 1 微创 → Level 2 常规 → Level 3 核心重构），决定后续 TDD/AST 的开启程度
4. **依赖一致性**
   - 脚手架/基础设施任务排在功能开发之前，依赖图保持一致
5. **进度可见性**
   - 通过 `IPlannerSummaryPublisher` 实时向用户推送规划进度摘要

---

## 3. 架构位置与边界

### 3.1 架构位置

```text
Orchestrator (Stage 2 Planning)
  -> ICodexPlanner.GeneratePlanAsync(session, goal)
    -> 发布 PlannerSummaryUpdate (Started)
    -> 读取 session.ActiveFacts（DependencyGraphSummary, ArchitectureAudit）
    -> 构造 system prompt（项目背景 + 依赖风险 + 架构审计 + 任务精简原则）
    -> 选择执行路径：
       → ShouldUseRuntimeForPlanning() ? GeneratePlanWithRuntimeAsync()
       → : GeneratePlanWithDirectStreamingAsync()
    -> 解析 JSON 数组为 List\<CodexTask\>
    -> CodexTaskClassifier.NormalizePlan(tasks)
    -> 设置所有任务 Status = Pending
    -> 发布 PlannerSummaryUpdate (Completed)
    -> 返回 tasks → session.Plan
  -> generate_dev_plan 工具暴露此能力给 Agent
```

### 3.2 Planner 的边界

Planner 负责：

- 构造规划 prompt（注入项目上下文、依赖风险、架构审计）
- 驱动 LLM 生成结构化任务清单
- 解析和清洗 LLM 返回的 JSON
- 任务分类与规范化（CodexTaskClassifier.NormalizePlan）
- 发布规划进度摘要（通过 IPlannerSummaryPublisher）

Planner 不负责：

- 执行任务（这是 Execution 阶段的职责）
- 重新规划已有计划（除非前提条件改变）
- 直接操作代码或文件系统
- 决定任务执行顺序（依赖关系由 LLM 推断，执行由 Orchestrator 调度）

换句话说：

**Planner 是任务生成层，不是执行层，也不是调度层。**

---

## 4. 核心组件

### 4.1 ICodexPlanner 接口

定义规划的唯一公共契约：

- [IAgentInterfaces.cs](../CodexFlow.Core/Abstractions/IAgentInterfaces.cs)（第 32 行）

```csharp
Task<List<CodexTask>> GeneratePlanAsync(CodexSession session, string goal, CancellationToken ct = default);
```

### 4.2 DefaultCodexPlanner 实现

`DefaultCodexPlanner` 是 `ICodexPlanner` 的默认实现。

核心依赖：

- `IChatClient`：LLM 推理
- `ILLMExecutor`（可选）：带记忆注入的流式执行器
- `IQueryRuntimeEngine`（可选）：带工具链和恢复能力的运行时引擎
- `IPlannerSummaryPublisher`（可选）：进度摘要发布
- `ILogger`：结构化日志

关键代码：

- [DefaultCodexPlanner.cs](../CodexFlow.Core/Agents/DefaultCodexPlanner.cs)

### 4.3 CodexTask 任务模型

每个任务包含以下核心字段：

| 字段 | 类型 | 说明 |
|------|------|------|
| `Id` | string | 任务唯一标识 |
| `TaskType` | string | `code` 或 `analysis` |
| `Title` | string | 任务标题（强制中文） |
| `Description` | string | 完整执行蓝图（阶段目标、范围、关键任务、执行顺序等） |
| `Dependencies` | string[] | 依赖的前置任务 ID 列表 |
| `StageId` | int | 所属阶段 ID |
| `RiskLevel` | string | `Low` / `Medium` / `High` |
| `ComplexityLevel` | int | `1` / `2` / `3` |
| `Status` | enum | 统一初始化为 `Pending` |
| `RequiredArtifacts` | ArtifactAssertion[] | 完成时必须满足的文件状态断言 |
| `ForbiddenStates` | ArtifactAssertion[] | 完成时绝对不能出现的状态 |
| `ChecklistItems` | ChecklistItem[] | 结构化子步骤清单（增量验证用） |
| `UnsafeIfDependencyFallbackPassed` | bool | 高风险任务必须为 true |

### 4.4 ArtifactAssertion 断言模型

| 字段 | 类型 | 说明 |
|------|------|------|
| `Type` | string | `file_exists` / `file_not_exists` / `file_contains` / `file_not_contains` |
| `Path` | string | 相对于项目根目录的路径 |
| `Text` | string? | 仅 `file_contains` / `file_not_contains` 需要 |

---

## 5. 规划执行流程

### 5.1 双执行路径

Planner 支持两种执行路径：

| 路径 | 条件 | 说明 |
|------|------|------|
| Runtime 路径 | `IQueryRuntimeEngine` 可用且 `PLANNER_DISABLE_RUNTIME != true` | 带工具链、恢复、诊断事件的完整运行时 |
| 直连流式路径 | 上述条件不满足 | 直接调用 `_chatClient.GetStreamingResponseAsync` 或 `_llmExecutor.StreamAsync` |

```text
ShouldUseRuntimeForPlanning()
  → true:  GeneratePlanWithRuntimeAsync()
             → _queryRuntimeEngine.ExecuteAsync(request, eventSink)
             → 捕获异常 → 降级到 GeneratePlanWithDirectStreamingAsync()
  → false: GeneratePlanWithDirectStreamingAsync()
```

### 5.2 Prompt 构造

Planner 的 system prompt 包含以下关键部分：

1. **项目模式**：Greenfield（新建）或 Brownfield（已有项目）
2. **项目背景**：`session.ProjectSummary`
3. **工程依赖风险预警**：`DependencyGraphSummary` Fact
4. **架构审计发现**：`ArchitectureAudit` Fact
5. **当前阶段**：`session.CurrentStage`
6. **最终目标**：用户输入的 `goal`
7. **任务精简原则**（最高优先级）：合并同类项、读改合一、禁止冗余、上限 10 个
8. **项目模式专属规则**：Greenfield 的脚手架先行、Brownfield 的模式对齐
9. **结构化验收契约要求**：RequiredArtifacts / ForbiddenStates 的详细规则
10. **效能分级规则**：Level 1/2/3 的定义和后续行为

### 5.3 JSON 解析与清洗

```text
TryDeserializePlan(json, out sanitizedJson)
  1. 直接 JsonConvert.DeserializeObject<List<CodexTask>>(json)
  2. 如果 JsonReaderException 且包含 "Bad JSON escape sequence"：
     → TrySanitizeJsonStringEscapes(json, ex, out sanitizedJson)
     → 状态机遍历：识别合法 JSON 转义序列，移除非法转义
     → 重新反序列化
```

### 5.4 Markdown 块处理

当 LLM 返回带有 Markdown 代码围栏的响应时：

```text
if json.StartsWith("```"):
  去除第一行（```json 或 ```）
  去除最后三个字符（```）
  trim
if !json.StartsWith('['):
  查找第一个 '[' 和最后一个 ']'
  提取中间的 JSON 数组
```

---

## 6. 任务规划规则

### 6.1 任务数量动态裁定

| 场景 | 任务数 |
|------|--------|
| 简单修复/重构 | 1-3 个 |
| 中等功能开发 | 3-6 个 |
| 大型功能或跨模块重构 | 6-10 个 |
| **绝对上限** | **10 个**（超过必须合并） |

### 6.2 任务精简原则

| 原则 | 说明 |
|------|------|
| 合并同类项 | 多个文件类似修改合并为一个任务 |
| 读改合一 | 禁止为"读取文件"单独创建任务 |
| 禁止冗余任务 | 不得生成仅做"检查"/"审查"/"验证结构"的独立任务 |
| TaskType 限制 | 只有 `code` 和 `analysis` 两种，分析任务必须无法合并到代码任务时才允许 |

### 6.3 Greenfield vs Brownfield 规则

**Greenfield（新建项目）**：

1. 第一个任务必须是脚手架命令（`dotnet new`、`mkdir + venv`、`npm init`）
2. 第二个任务建立标准目录结构
3. 禁止生成"查看空目录"的任务

**Brownfield（已有项目）**：

1. 修改任务必须遵循项目中已有的命名和架构模式
2. 修改 Top Critical Files 的任务必须标记 `RiskLevel: High`

### 6.4 结构化验收契约（BUG-002 fix）

每个任务必须声明：

| 断言类型 | 适用场景 | 规则 |
|----------|----------|------|
| `file_exists` | 新增/创建类任务 | 目标文件必须存在 |
| `file_contains` | 修改/更新类任务 | 目标文件必须包含关键内容 |
| `file_not_exists` | 迁移/删除类任务 | 旧位置文件必须不存在 |
| `file_not_contains` | 迁移/清理类任务 | 文件不能包含旧内容 |

**覆盖性规则**：凡是在 Description 中提到的会被修改的文件路径，至少要在 RequiredArtifacts 或 ForbiddenStates 中出现一次。

---

## 7. 进度摘要发布

### 7.1 发布时机

| 阶段 | Kind | Phase | Message |
|------|------|-------|---------|
| 规划启动 | Started | `planning_start` | 规划已启动 |
| 规划进行中 | InProgress | `runtime_round_started` / `runtime_thinking` | 正在整合上下文 |
| 规划完成 | Completed | `planning_completed` | 任务清单已生成（含任务数） |
| 规划失败 | Failed | `planning_parse_failed` / `planning_failed` | 规划失败 |
| 运行时回退 | Fallback | `planning_runtime_fallback` | 回退兼容模式 |

### 7.2 事件发布

通过 `IPlannerSummaryPublisher.PublishAsync(session, update, ct)` 发布。发布失败不影响规划主流程，仅记录 Warning 日志。

### 7.3 运行时事件 Sink

当使用 Runtime 路径时，两个 EventSink 提供额外能力：

- **PlannerDiagnosticEventSink**：记录详细的运行时诊断日志（round、thinking、termination）
- **PlannerSummaryEventSink**：在 round started、thinking started、round completed 时发布进度摘要

---

## 8. 故障处理

### 8.1 异常分类

| 异常类型 | 处理方式 |
|----------|----------|
| `JsonReaderException` | 记录错误日志，发布 Failed 摘要，返回空任务列表 |
| `JsonSerializationException` | 同上 |
| `InvalidOperationException` | 同上 |
| Runtime 执行异常 | 捕获后降级到直连流式路径 |

### 8.2 返回空列表的语义

当规划失败时返回 `new List<CodexTask>()`（空列表），而非 null：

- Orchestrator 检测到空计划时应通知用户
- 不应进入执行阶段（无任务可执行）
- 规划失败摘要已通过 `IPlannerSummaryPublisher` 发布

### 8.3 JSON 转义清理

`TrySanitizeJsonStringEscapes` 处理 LLM 返回的非法 JSON 转义：

- 遍历 JSON 字符串，识别合法的转义序列（`\"`, `\\`, `\/`, `\b`, `\f`, `\n`, `\r`, `\t`, `\uXXXX`）
- 移除非法转义（保留转义后的字符）
- 重新反序列化

---

## 9. 与旧执行模型的关系

当前规划已统一到 `ICodexPlanner` 接口面。

因此：

- 新的规划语义应以 `DefaultCodexPlanner` 为准
- 结构化验收契约（RequiredArtifacts / ForbiddenStates）是标准协议
- 效能分级（Level 1/2/3）决定了后续 TDD 和 AST 的开启程度

如果文档需要描述当前规划路径，应优先表述为：

- 上下文注入 → LLM 规划 → JSON 解析 → 任务规范化 → 进度发布

而不是：

- 主代理直接在对话中生成任务列表

`generate_dev_plan` 工具将此能力暴露给 Agent，允许在运行时重新规划。

---

## 11. 相关技术文档

| 文档 | 说明 |
|------|------|
| [Orchestrator 编排状态机](./orchestrator-tech.md) | 阶段状态机、规划调用点（Stage 2） |
| [Codex Controller 控制器](./codex-controller-tech.md) | 工具暴露、`generate_dev_plan` 工具 |
| [Coordinator 编排系统](./coordinator-tech.md) | 主会话编排、worker 派发 |
| [Validator 验证系统](./validator-tech.md) | 验收契约消费、结构化断言评估 |
| [TDD 适配器系统](./tdd-adapter-tech.md) | 效能分级对 TDD 的影响 |
