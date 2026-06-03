# Phase 8 补充：后端 Runtime 稳定性加固计划

> 版本：1.0
> 日期：2026-04-14
> 状态：Completed - Archived
> 适用范围：`CodexFlow` 后端 API 主路径
> 文档目标：记录 `Phase 8` 后端 runtime 稳定性加固的最终收口结果、实现范围与验证证据
> 上游文档：[coordinator-worker-runtime-upgrade-blueprint.md](../feature/coordinator-worker-runtime-upgrade-blueprint.md)
> 归档说明：原活动路径 `docs/feature/backend-runtime-stability-hardening-plan.md` 已关闭并迁入归档目录

---

## 0. 归档结论

本补充文档已完成归档，不再作为活动执行入口。

本轮回到代码核对后确认，原计划中有一部分风险已经在此前多轮优化中被主干实现吸收，真正仍需补齐的 blocking gap 比文档初稿更小。剩余缺口已在本轮完成收口，因此该文档结束活动状态并转入归档。

后续如果继续推进更广义的 worker 终止语义矩阵、写路径样本抽检或非流式调用收口，应回到以下活动文档继续推进：

1. [coordinator-worker-runtime-upgrade-blueprint.md](../feature/coordinator-worker-runtime-upgrade-blueprint.md)
2. [non-streaming-llm-migration-plan.md](../feature/non-streaming-llm-migration-plan.md)

---

## 1. 核对后确认已被主干实现吸收的内容

在本轮开始前，以下内容已经不是新的主阻塞项：

1. `Gateway` 与 `generate_dev_plan` 已具备基础 `plan-loss guard` 判断，能识别 `PlanGeneratedAtUtc.HasValue && Plan.Count == 0`
2. worker 侧已经具备 `RecoveryExhausted -> FailedRecoveryNeeded` 的基础状态投影
3. `ForgeWorker` 已具备 shadow worktree、`continue_worker` 与已提交产物复用的基础边界

这意味着本轮重点不再是“证明链路能跑通”，而是把仍然真实存在的 runtime 语义缺口补齐。

---

## 2. 本轮实际完成的稳定性加固

### 2.1 Gateway runtime 失败不再静默回退 legacy loop

已收口 `GatewayMessageProcessor` 的 runtime 异常路径：

1. runtime 抛错后不再隐式切回 legacy manual loop
2. 统一产出显式错误结论、审计信息和用户可见说明
3. 若发生 `plan-loss`，在 `Gateway` 手动路径中进入显式 guard，而不是继续自动重规划

结果是 `Gateway` 不再出现“用户以为还在同一条 runtime 链路上，实际上已切到另一套 loop”的隐式语义切换。

### 2.2 Gateway 与 worker 的 AdapterHints 已对齐

`Gateway` runtime hints 已与 worker 恢复面保持一致，当前统一开启：

1. `EnableEmptyResponseRecovery`
2. `EnableMalformedProtocolRecovery`
3. `EnableTransportFailureRecovery`
4. `EnableStallDetection`

这样同类流式问题在 `Gateway / Worker` 入口的恢复意图和可观测口径不再分叉。

### 2.3 planning-intent recovery 已增加硬约束

`QueryRuntimeEngine` 的 planning-intent recovery 已收紧为：

1. 只允许在 `stage <= 2`
2. 只允许在 `Plan.Count == 0`
3. 命中 `PlanGeneratedAtUtc.HasValue && Plan.Count == 0` 时进入显式 `plan_loss_guard`
4. `plan-loss` 不再触发自动 replan

这直接阻断了阶段漂移、计划已存在时重复规划，以及“有计划历史但当前计划丢失”时的错误自动恢复。

### 2.4 Plan 成功判据已补齐 snapshot 持久化验证

`generate_dev_plan` 现在不再把“模型给出计划 + session 已更新”视为充分成功条件，而是要求：

1. `generate_dev_plan` 返回成功
2. session plan 已更新
3. `task list snapshot` 已成功持久化

如果 snapshot 发布后反查失败，系统会回滚到旧的 `Plan / Stage / Version / GeneratedAt / Metadata`，阻断半成功状态。

---

## 3. 主要代码落点

本轮收口主要集中在以下实现：

1. `CodexFlow/Gateway/GatewayMessageProcessor.cs`
2. `CodexFlow.Core/Runtime/QueryRuntimeEngine.cs`
3. `CodexFlow.Core/Agents/Tools/GenerateDevPlanTool.cs`
4. `CodexFlow.Core/Agents/ToolRegistryBootstrapper.cs`
5. `CodexFlow/Program.cs`
6. `CodexFlow/Controllers/CodexController.cs`

对应回归测试位于：

1. `CodexFlow.Gateway.IntegrationTests/GatewayRuntimeIntegrationStabilityTests.cs`
2. `CodexFlow.Core.Tests/Runtime/QueryRuntimeRecoveryTests.cs`
3. `CodexFlow.Core.Tests/StageManagementTests.cs`

---

## 4. 验证证据

本轮已执行并通过以下回归测试：

1. `dotnet test CodexFlow.Gateway.IntegrationTests/CodexFlow.Gateway.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~GatewayRuntimeIntegrationStabilityTests"`
   - 结果：`6/6` 通过
2. `dotnet test CodexFlow.Core.Tests/CodexFlow.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~QueryRuntimeRecoveryTests|FullyQualifiedName~StageManagementTests"`
   - 结果：`24/24` 通过

覆盖的关键行为包括：

1. runtime failure / cancellation 不再 fallback 到 legacy stream
2. `Gateway` hints 与 worker 恢复面一致
3. planning-intent recovery 在 `stage > 2`、`Plan.Count > 0` 时被阻断
4. `plan-loss guard` 会阻断自动 replan
5. `task list snapshot` 缺失时会触发 plan rollback

---

## 5. 与原计划的关系

从执行结果看，这份补充计划里真正需要独立收口的 blocking 项已经完成，主要对应原计划中的以下核心判据：

1. `Gateway` runtime 异常后的 fallback policy 已明确，并取消静默回退
2. `Plan` 成功的唯一判据已补齐为 `session + snapshot` 双成功
3. `Plan` 阶段的重复规划与 `plan-loss` 自动恢复风险已收口
4. 关键恢复行为已具备测试与日志证据

原计划中更宽泛的后续增强项，例如：

1. `Explore / Plan / Forge / Verify` 的完整终止语义矩阵统一
2. 写路径 worker 更广义的幂等样本抽检
3. 更大范围的 telemetry/观测口径整合

不再作为这份补充文档的独立阻塞项，后续应并入上游蓝图或其他活动文档继续推进，而不是继续保留一份单独的 `Phase 8` 活动补充文档。

---

## 6. 归档说明

本文件用于保留本轮后端 runtime 稳定性加固的最终决策与完成证据。

如需追溯本主题的历史背景，可继续参考同目录下的归档文档：

1. [query-runtime-upgrade.md](./query-runtime-upgrade.md)
2. [query-runtime-stability-test-plan.md](./query-runtime-stability-test-plan.md)
3. [coordinator-worker-spike-plan.md](./coordinator-worker-spike-plan.md)
