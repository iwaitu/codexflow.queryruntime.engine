# Explore Worker Spike Report

> 日期：2026-04-12
> 状态：Initial Slice Complete
> 对应计划：[coordinator-worker-spike-plan.md](../feature/coordinator-worker-spike-plan.md)
> 范围：Spike A `Explore Worker Vertical Slice`

---

## 1. 结论

`ExploreWorker` 的最小垂直切片已经打通，并补上了 outbox 投影验证：

1. `BackgroundJobRunner` 已支持新的 `JobType = "ExploreWorker"`
2. worker 结果可以通过 `BackgroundJob -> ResultPayloadJson -> OutboxEvent(JobCompleted/JobFailed)` 回流
3. worker 会构造只读 `QueryRuntimeRequest`，并把工具面限制在只读探索工具集合
4. 缺少 `IQueryRuntimeEngine` 时，job 会稳定失败并产出结构化失败 payload
5. `OutboxProjector` 可以消费 `ExploreWorker` 的完成事件，并完成 Redis / SignalR / TaskList 投影

当前判断：

**BackgroundJob 体系可以承载只读 worker 的最小执行闭环。**

但也必须明确：

**本轮自动化验证证明的是 `BackgroundJobRunner -> IQueryRuntimeEngine -> Outbox` 这条垂直链路，不是“生产 DI 图 + 真正 QueryRuntimeEngine + 真 LLM” 已全部关闭风险。**

---

## 2. 已落地内容

### 2.1 代码变更

- 新增 `BackgroundJobRunner` 对 `ExploreWorker` 的 handler
- 新增只读工具过滤逻辑，仅暴露：
  - `tool_search`
  - `search_file_index`
  - `search_in_files`
  - `ivilson_read`
  - `ivilson_ls`
  - `list_workspace`
  - `analyze_project`
  - `analyze_code`
- `BackgroundJobRunner` 现在会把结果写回 `BackgroundJob.ResultPayloadJson`
- 新增 `QueryLoopEntryPoint.ExploreWorker`
- 已验证 `OutboxProjector` 可处理 `ExploreWorker` 完成事件

### 2.2 自动化测试

新增测试文件：

- [ExploreWorkerSpikeTests.cs](/Users/iwaitu/github/codexflow/CodexFlow.Tests/ExploreWorkerSpikeTests.cs)

覆盖场景：

1. `ExploreWorker_CanRunThroughBackgroundJobPipeline`
   - 验证 `Queued -> Running -> Completed`
   - 验证 `IQueryRuntimeEngine` 被调用
   - 验证 request 投影正确
   - 验证 `JobCompleted` outbox payload 可消费
2. `ExploreWorker_MarksJobFailed_WhenRuntimeServiceIsMissing`
   - 验证缺失 runtime 时 `Queued -> Running -> Failed`
   - 验证 `JobFailed` outbox payload 可消费
3. `ExploreWorker_OutboxProjector_ProjectsCompletedEvent_ToRedisAndSignalR`
   - 验证 `OutboxProjector.ProjectPendingAsync()` 可消费 `ExploreWorker` 产生的 pending outbox
   - 验证 Redis hot view 被更新为 `Completed`
   - 验证 SignalR `OnJobUpdate`
   - 验证 `taskListUpdated`

---

## 3. DI 解析结果

在当前最小 spike harness 下，worker 侧服务解析结果如下：

| 服务 | 结果 | 说明 |
|---|---|---|
| `IQueryRuntimeEngine` | resolved | 通过 fake runtime 验证 runner 可解析并调用 |
| `IToolRegistry` | resolved | 使用 scoped registry，已预注册只读工具 |
| `CodexSessionManager` | resolved | 可创建/恢复 worker session |
| `IChatClient` | resolved | 当前用于验证 DI 完整性 |
| `ToolRegistryBootstrapper` | not_registered | 本轮最小 harness 未装入真实 bootstrapper |

当前解释：

- 对 `ExploreWorker` 最小切片而言，`IQueryRuntimeEngine / IToolRegistry / CodexSessionManager / IChatClient` 这组核心服务已足够证明闭环可行
- `ToolRegistryBootstrapper` 仍未在该最小 harness 中验证真实装配

---

## 4. 全链路验证结果

### 4.1 已验证

1. 可以创建 `ExploreWorker` background job
2. `BackgroundJobRunner.ExecuteAsync()` 可以识别并分发该 job type
3. worker 会构造 `QueryRuntimeRequest`
4. request 的 `EntryPoint` 为 `ExploreWorker`
5. request 会携带只读工具集
6. worker 结果会写入：
   - `BackgroundJob.ResultPayloadJson`
   - `OutboxEvent.Payload`
7. 失败场景会稳定收敛为 `JobFailed`

### 4.2 尚未验证

1. 真实生产 DI 图下的 `ToolRegistryBootstrapper` 装配
2. 真实 `QueryRuntimeEngine` 在 background worker scope 中跑完整 tool loop
3. Coordinator 模型对 worker 结果的实际消费

---

## 5. 测试结果

已执行：

```bash
dotnet test CodexFlow.Tests/CodexFlow.Tests.csproj --no-restore --filter "FullyQualifiedName~ExploreWorkerSpikeTests"
```

结果：

- `ExploreWorkerSpikeTests`: `Passed 3`

---

## 6. 风险与下一步

### 6.1 当前残余风险

1. 本轮成功路径使用的是 capturing fake runtime，不是真实 `QueryRuntimeEngine`
2. `ToolRegistryBootstrapper` 未在 spike harness 中验证
3. `OutboxProjector` 还没有针对 `ExploreWorker` 做显式投影验证

### 6.2 建议下一步

1. 补一个 `ExploreWorker + OutboxProjector` 集成测试
2. 在更接近生产 DI 的 host 中补一次 `ToolRegistryBootstrapper` 解析验证
3. 再做一轮 “真实 `QueryRuntimeEngine` + deterministic stub LLM” 的 worker-scope 验证

当前建议状态：

**Spike A 可以继续推进，不需要回滚；但还不应宣布 “worker 运行时风险已全部关闭”。**
