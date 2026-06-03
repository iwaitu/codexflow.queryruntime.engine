# Query Runtime 稳定性集成测试计划

> 版本：0.2
> 日期：2026-04-12
> 状态：Passed
> 适用范围：`IQueryRuntimeEngine` 稳定期观察
> 文档目标：定义 `Query Runtime` 稳定性的集成测试范围、分层、通过门槛与补充观察方式，为 `Coordinator/Worker` 蓝图提供准入依据

---

## 1. 文档结论

`Query Runtime` 的稳定性不应仅通过 `full-pipeline` 观察判断，更适合以**集成测试**作为主验证方式，以少量端到端 `full-pipeline` 作为补充观察。

本计划的核心结论如下：

1. `IQueryRuntimeEngine` 的稳定性判断以集成测试为主
2. `full-pipeline` 只作为补充观察，不作为唯一准入依据
3. 稳定性验证必须覆盖：
   - 终止语义
   - recovery 语义
   - tool loop 语义
   - 事件顺序
   - context governance
4. 若本计划未通过，则 `Coordinator/Worker Phase 0.5` 不应启动

一句话总结：

**先证明 runtime 语义稳定，再在它之上叠加 worker 系统。**

### 1.1 当前进展

截至 `2026-04-12`，`Layer A` 和 `Layer B` 已有首轮实现并通过当前过滤测试：

| 层级 | 测试类 | 当前状态 |
|---|---|---|
| Layer A | `QueryRuntimeStabilityTests` | 已实现，green |
| Layer A | `QueryRuntimeRecoveryTests` | 已实现，green |
| Layer A | `QueryRuntimeEventOrderingTests` | 已实现，green |
| Layer A | `QueryRuntimeContextGovernanceTests` | 已实现，green |
| Layer B | `GatewayRuntimeIntegrationStabilityTests` | 已实现，green |
| Layer B | `KernelRuntimeStabilityTests` | 已实现，green |
| Layer C | `QueryRuntimeRealApiSmokeTests` | 已实现，作为补充观察保留 |

当前判断：

1. `G1` 已满足：Layer A 核心集成测试已落地
2. `G2` 已满足：Layer B 入口适配集成测试已落地
3. `G3` 已满足：最小 `20` 个场景已全部落地，当前过滤测试通过
4. `G4` 已满足：根据 [coordinator-worker-spike-plan.md](coordinator-worker-spike-plan.md) 的启动门槛判定，Layer C 已提供足够的补充观察证据

补充说明：

- Layer C 当前已具备两种运行模式：
  - live smoke：`RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true`
  - soak 观察：额外设置 `RUN_QUERY_RUNTIME_REAL_SOAK_TESTS=true`
- Layer C 仍建议持续保留，用于后续真实链路回归与 soak 观察；但它不再阻塞 `Coordinator/Worker Phase 0.5` 启动
- 因此，本计划当前结论为：**`Query Runtime` 稳定期已通过，可以进入 `Coordinator/Worker Phase 0.5`**

---

## 2. 为什么用集成测试，而不是只跑 Full Pipeline

`full-pipeline` 的优点是：

- 接近真实链路
- 能看到多个子系统拼装后的效果
- 能暴露一些单测/集成测试看不到的装配问题

但它不适合作为唯一稳定性依据，原因如下：

1. 失败定位成本高
   - 很难快速判断是 runtime、gateway、context、tool 还是业务层问题
2. 波动大
   - 容易混入外部依赖、环境差异和任务内容差异
3. 可重复性弱
   - 很难形成可稳定回归的 CI 门槛

相比之下，集成测试更适合作为主验证方式，因为它具备：

- 可重复
- 可定位
- 可自动化
- 可做语义级断言

因此，本计划采用以下策略：

1. 以集成测试作为主门槛
2. 以 `full-pipeline` 作为补充观察

---

## 3. 验证范围

本计划不评估“具体业务任务是否实现正确”，只评估 `Query Runtime` 自身是否稳定。

重点验证以下 5 个维度：

### 3.1 终止语义稳定

必须验证：

- 无 tool call 时正确终止
- 达到 `MaxRounds` 时正确终止
- tool loop 正常收口
- 终止原因与结果一致

### 3.2 Recovery 语义稳定

必须验证：

- 空响应 recovery
- malformed protocol recovery
- transport failure retry
- recovery 耗尽后的终止行为

### 3.3 Tool Loop 语义稳定

必须验证：

- tool call 被正确收集
- tool result 被正确回写
- guardrail block 语义正确
- critique reject 语义正确
- dedupe 不产生误伤

### 3.4 Runtime Event 顺序稳定

必须验证：

- `RoundStarted`
- `ThinkingStarted / ThinkingDelta / ThinkingEnded`
- `AssistantDelta`
- `ToolCallRequested`
- `ToolExecutionStarted / Completed`
- `RoundCompleted`
- `Terminated`

这些事件的顺序、次数和关键字段都必须稳定。

### 3.5 Context Governance 稳定

必须验证：

- turn 完成后的上下文回写
- context window manager 的挂点执行
- compression 后消息不丢失、不重复、不乱序

---

## 4. 测试分层

本计划采用三层测试结构。

## 4.1 Layer A：Runtime 核心集成测试

测试目标：

- 直接验证 `IQueryRuntimeEngine` 的 loop 语义

测试方式：

- 使用 fake / stub `ILLMExecutor`
- 使用 fake tools
- 使用 fake `IQueryRuntimeEventSink`
- 最小化外部依赖

建议新增测试类：

- `QueryRuntimeStabilityTests`
- `QueryRuntimeRecoveryTests`
- `QueryRuntimeEventOrderingTests`
- `QueryRuntimeContextGovernanceTests`

这一层是主战场，必须成为是否“通过稳定期”的核心依据。

## 4.2 Layer B：入口适配集成测试

测试目标：

- 验证入口接入 runtime 后没有语义漂移

建议覆盖：

- `GatewayMessageProcessor`
- `GatewayRuntimeEventAdapter`
- `DefaultCodexKernel` runtime path

建议新增测试类：

- `GatewayRuntimeIntegrationStabilityTests`
- `KernelRuntimeStabilityTests`

这一层关注的是“runtime 能否被外部系统正确消费”，不是业务任务正确性。

## 4.3 Layer C：Full Pipeline 补充观察

测试目标：

- 观察真实链路上的协同稳定性

建议做法：

- 只保留少量 smoke / soak 类型样本
- 不要求覆盖所有 recovery 分支
- 不拿单次波动直接否定 runtime 设计

建议新增测试类或脚本：

- `QueryRuntimeFullPipelineSmokeTests`
- 或独立观察脚本 / 手工观测报告

这一层的定位是“补充证据”，不是唯一门槛。

---

## 5. 最小测试矩阵

第一轮稳定性验证至少应覆盖以下场景。

### 5.1 正常终止场景

1. 单轮无工具调用，正常结束
2. 单轮有工具调用，下一轮无工具调用结束
3. 多轮工具调用后正常结束

### 5.2 终止边界场景

4. 达到 `MaxRounds`
5. 连续空响应后终止
6. recovery 耗尽后终止

### 5.3 Recovery 场景

7. 空响应后注入恢复提示并重试成功
8. malformed tool protocol 后恢复成功
9. transport failure 后重试成功
10. transport failure 超过上限后终止

### 5.4 Tool Loop 场景

11. tool result 被正确 append
12. critique reject 后 tool result 被跳过
13. guardrail block 后注入反馈消息
14. dedupe 跳过重复搜索类工具

### 5.5 Event 顺序场景

15. thinking 事件顺序正确
16. tool 事件顺序正确
17. round 事件与 terminated 事件顺序正确

### 5.6 Context 治理场景

18. context window manager 被调用
19. conversation capture 被正确回写
20. compression 后 recent messages 顺序不乱

### 5.7 当前覆盖状态

| # | 场景 | 当前状态 | 说明 |
|---|---|---|---|
| 1 | 单轮无工具调用，正常结束 | 已覆盖 | Layer A |
| 2 | 单轮有工具调用，下一轮无工具调用结束 | 已覆盖 | Layer A |
| 3 | 多轮工具调用后正常结束 | 已覆盖 | Layer A |
| 4 | 达到 `MaxRounds` | 已覆盖 | Layer A |
| 5 | 连续空响应后终止 | 已覆盖 | Layer A |
| 6 | recovery 耗尽后终止 | 已覆盖 | Layer A |
| 7 | 空响应后注入恢复提示并重试成功 | 已覆盖 | Layer A |
| 8 | malformed tool protocol 后恢复成功 | 已覆盖 | Layer A |
| 9 | transport failure 后重试成功 | 已覆盖 | Layer A |
| 10 | transport failure 超过上限后终止 | 已覆盖 | Layer A |
| 11 | tool result 被正确 append | 已覆盖 | Layer A |
| 12 | critique reject 后 tool result 被跳过 | 已覆盖 | Layer A |
| 13 | guardrail block 后注入反馈消息 | 已覆盖 | Layer A |
| 14 | dedupe 跳过重复搜索类工具 | 已覆盖 | Layer A |
| 15 | thinking 事件顺序正确 | 已覆盖 | Layer A / Layer B |
| 16 | tool 事件顺序正确 | 已覆盖 | Layer A / Layer B |
| 17 | round / terminated 事件顺序正确 | 已覆盖 | Layer A |
| 18 | context window manager 被调用 | 已覆盖 | Layer A |
| 19 | conversation capture 被正确回写 | 已覆盖 | Layer A / Layer B |
| 20 | compression 后 recent messages 顺序不乱 | 已覆盖 | Layer A context governance 顺序断言 |

---

## 6. 建议测试类与职责

以下测试类建议作为第一轮最小交付物。

### 6.1 `QueryRuntimeStabilityTests`

职责：

- 覆盖正常 loop 终止语义
- 覆盖 `MaxRoundsReached`
- 覆盖多轮 tool loop 收口

状态：

- 已实现并纳入 `CodexFlow.Core.Tests/Runtime`

### 6.2 `QueryRuntimeRecoveryTests`

职责：

- 覆盖 empty response
- 覆盖 malformed protocol
- 覆盖 transport failure retry / exhausted

状态：

- 已实现并纳入 `CodexFlow.Core.Tests/Runtime`

### 6.3 `QueryRuntimeEventOrderingTests`

职责：

- 验证 event sink 收到的事件顺序和关键字段

状态：

- 已实现并纳入 `CodexFlow.Core.Tests/Runtime`

### 6.4 `QueryRuntimeContextGovernanceTests`

职责：

- 验证 context window manager 与 conversation capture

状态：

- 已实现并纳入 `CodexFlow.Core.Tests/Runtime`

### 6.5 `GatewayRuntimeIntegrationStabilityTests`

职责：

- 验证 `GatewayMessageProcessor` 接入 runtime 后的行为稳定性
- 验证 Gateway SSE event 适配未漂移

状态：

- 已实现，当前覆盖 request projection / runtime failure fallback / cancellation fallback / SSE ordering

### 6.6 `KernelRuntimeStabilityTests`

职责：

- 验证 `DefaultCodexKernel` runtime 路径未出现 guardrail / critique 语义退化

状态：

- 已实现，当前覆盖 request projection / runtime failure fallback / cancellation propagation / runtime event forwarding

---

## 7. 通过门槛

`Query Runtime` 稳定期通过，至少需要满足以下条件：

### 7.1 集成测试门槛

1. Layer A 和 Layer B 的新增测试全部 green
2. 不允许存在 flaky case 未解释
3. 新增测试总数不少于 20 个场景

当前状态：

- 第 1 条：已满足
- 第 2 条：暂未发现未解释 flaky case
- 第 3 条：已满足

### 7.2 语义门槛

必须确认以下语义没有明显退化：

1. 终止原因与预期一致
2. recovery 触发与终止条件一致
3. critique reject 不污染消息历史
4. guardrail block 不导致 loop 死锁
5. event 顺序稳定

### 7.3 Full Pipeline 观察门槛

作为补充观察，建议至少满足以下条件之一：

1. 20+ 次真实样本运行中未出现结构性卡死
2. 或连续 1 周观察期内无新增 runtime 级 blocker

注意：

- `full-pipeline` 结果仅作补充证据
- 不应以单次 full-pipeline 波动推翻已通过的集成测试结论

---

## 8. 失败判定与处置

若测试未通过，处理方式必须分层。

### 8.1 Layer A 失败

说明：

- runtime 内核本身不稳定

处理：

- 暂停 `Coordinator/Worker` 后续阶段
- 优先修复 runtime 内核
- 不进入 `Phase 0.5`

### 8.2 Layer B 失败

说明：

- runtime 本身可能稳定，但入口适配存在漂移

处理：

- 修复 adapter / entrypoint 集成问题
- 不进入 `Phase 0.5`

### 8.3 Layer C 失败

说明：

- 真实链路中有环境级或拼装级问题

处理：

- 不直接否定 runtime 内核
- 需结合 Layer A/B 结果判断：
  - 若 A/B 通过，则优先定位 full-pipeline 装配问题
  - 若 A/B 也失败，则回归 runtime 主问题

---

## 9. 与 Coordinator/Worker 蓝图的关系

本计划直接服务于：

- [coordinator-worker-runtime-upgrade-blueprint.md](/Users/iwaitu/github/codexflow/docs/feature/coordinator-worker-runtime-upgrade-blueprint.md)

关系如下：

1. 本计划是 `Coordinator/Worker Phase 0.5` 之前的准入门槛
2. 本计划通过后，才允许进入：
   - `Explore Worker Vertical Slice Spike`
   - `Envelope Format A/B Spike`
3. 本计划未通过，则 `Coordinator/Worker` 主线暂停

---

## 10. 推荐执行顺序

建议按以下顺序推进：

1. 保持 Layer A 现有测试集为稳定性主门槛
2. 保持 Layer B 现有测试集为入口适配门槛
3. 持续进行少量 Layer C real API / full-pipeline 观察
4. 形成稳定期结论
5. 通过后再启动 `Coordinator/Worker Phase 0.5`

---

## 11. 最终判断标准

当以下条件全部满足时，可以认为：

**`Query Runtime` 稳定期通过，可以进入 `Coordinator/Worker` 的技术 spike 阶段。**

条件如下：

1. 集成测试通过
2. 关键语义无漂移
3. 入口适配无明显退化
4. full-pipeline 无结构性 blocker

如果上述 4 条有任一不满足，则：

**暂停 `Coordinator/Worker`，先继续修复 `Query Runtime`。**
