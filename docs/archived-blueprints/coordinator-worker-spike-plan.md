# Coordinator/Worker Phase 0.5 Spike 执行计划

> 版本：1.0
> 日期：2026-04-12
> 状态：Completed
> 前置条件：通过 [query-runtime-stability-test-plan.md](query-runtime-stability-test-plan.md) 定义的稳定性门槛
> 上游文档：[coordinator-worker-runtime-upgrade-blueprint.md](coordinator-worker-runtime-upgrade-blueprint.md) / [query-runtime-stability-test-plan.md](query-runtime-stability-test-plan.md)
> 目标：用最小代码量验证两个关键假设，为 Phase 1-3 提供决策依据

---

## 总览

本文档定义两个独立 spike，可并行执行：

| Spike | 验证假设 | 交付物 | 预估工作量 |
|---|---|---|---|
| A. Explore Worker Vertical Slice | BackgroundJob 体系能承载只读 worker 全链路 | `ExploreWorkerSpikeReport.md` + 可运行代码 | 2-3 天 |
| B. Envelope Format A/B | 哪种通知格式最适合 LLM-facing 协议 | `EnvelopeFormatSpikeReport.md` + 数据表格 | 1-2 天 |

两个 spike 都不修改现有生产代码路径。所有新增代码应放在独立分支，spike 结束后归档或丢弃。

### 启动门槛

`Phase 0.5` 不是独立前进的工作流，只有在 [query-runtime-stability-test-plan.md](query-runtime-stability-test-plan.md) 通过后才启动。最低门槛如下：

| # | 门槛 | 通过条件 |
|---|---|---|
| G1 | Layer A Runtime 核心集成测试 | `QueryRuntimeStabilityTests`、`QueryRuntimeRecoveryTests`、`QueryRuntimeEventOrderingTests`、`QueryRuntimeContextGovernanceTests` 全部通过 |
| G2 | Layer B 入口适配集成测试 | `GatewayRuntimeIntegrationStabilityTests`、`KernelRuntimeStabilityTests` 全部通过 |
| G3 | 覆盖度 | 稳定性计划中的最小 20 个场景全部落地，无 unexplained flaky case |
| G4 | 补充观察 | `full-pipeline` 作为补充观察通过：完成 `20+` 样本或 `1` 周观察，未出现未解释的系统性漂移 |

若 `G1-G4` 任一项未通过，则本计划暂停执行，优先修复 `Query Runtime` 稳定性问题，不进入 `Explore Worker` 或 `Envelope Format` spike。

### 当前门槛状态

截至 `2026-04-12`，启动门槛的当前状态如下：

| 门槛 | 当前状态 | 说明 |
|---|---|---|
| G1 | 已满足 | Layer A 四个核心测试类已落地并通过 |
| G2 | 已满足 | `GatewayRuntimeIntegrationStabilityTests` 与 `KernelRuntimeStabilityTests` 已落地并通过 |
| G3 | 已满足 | 稳定性计划中的最小 `20` 个场景已全部落地，当前过滤测试通过 |
| G4 | 已满足 | Layer C real API smoke 与 soak harness 已建立，且按启动门槛定义已形成足够的补充观察证据 |

因此，当前状态已经更新为：**`Query Runtime` 稳定期通过，`Coordinator/Worker Phase 0.5` 可正式启动。**

### 当前执行进展

截至 `2026-04-12`：

- ✅ Spike A 已完成最小垂直切片，结果见 [ExploreWorkerSpikeReport.md](../spike-reports/ExploreWorkerSpikeReport.md)
- ✅ 当前已验证 `BackgroundJobRunner -> IQueryRuntimeEngine -> Outbox -> OutboxProjector` 的最小闭环
- ✅ Spike B 已完成 Envelope Format A/B 测试，结果见 [EnvelopeFormatSpikeReport.md](../spike-reports/EnvelopeFormatSpikeReport.md)
  - 推荐采用 **XML Envelope** 格式（100% 准确率，15 样本 × 3 格式 × 6 场景完整测试）

**Phase 0.5 已完成，可进入 Phase 1 评审门控。**

---

## Spike A：Explore Worker Vertical Slice

### A.1 目标

验证一个只读 `explore` worker 能否通过现有 `BackgroundJobRunner` + `JobSupervisorHostedService` 全链路运行，并把结果回流给 Coordinator 模型。

具体验证以下 5 个环节：

1. **Job 创建**：通过 `BackgroundJobService.CreateJobAsync()` 创建 `JobType = "ExploreWorker"` 的 job
2. **DI Scope 装配**：在 `BackgroundJobRunner.ExecuteAsync()` 的 `execScope` 中解析 worker 所需服务
3. **Runtime 执行**：worker 内部通过 `IQueryRuntimeEngine.ExecuteAsync()` 完成只读查询
4. **Outbox 回流**：worker 完成后产生 `OutboxEvent`，经 `OutboxProjector` 投影到 Redis/SignalR
5. **Coordinator 消费**：主代理能读取结构化的 worker 结果

### A.2 输入

- 一个真实的只读探索任务，例如："查找项目中所有实现了 `ICodexValidator` 接口的类，列出文件路径和关键方法签名"
- 一个已存在的 `CodexSession`（可用测试 session）
- 当前生产环境的 DI 注册配置

### A.3 实施步骤

#### 步骤 1：新增 ExploreWorker JobType handler

在 `BackgroundJobRunner` 中新增一个 case：

```csharp
// BackgroundJobRunner.cs — ExecuteAsync 的 switch 中新增
"ExploreWorker" => await ExecuteExploreWorkerAsync(execScope.ServiceProvider, job, ct),
```

实现 `ExecuteExploreWorkerAsync`：

```text
输入：job.PayloadJson 中包含 { sessionId, prompt, workerType: "explore" }
流程：
  1. 从 DI scope 解析：IQueryRuntimeEngine, IToolRegistry, ToolRegistryBootstrapper
  2. 构造 QueryRuntimeRequest：
     - messages: [system prompt (explore-specific), user prompt (来自 payload)]
     - tools: 只注册只读工具（search_file_index, read_file, analyze_code, grep_search 等）
     - maxRounds: 10（只读任务不需要太多轮次）
     - entryPoint: "ExploreWorker"
  3. 构造一个最小 IQueryRuntimeEventSink（仅日志，不走 Gateway SSE）
  4. 调用 engine.ExecuteAsync()
  5. 收集最终 assistant 消息作为 result
  6. 返回 JSON: { success, summary, result, workerType, durationMs }
```

#### 步骤 2：验证 DI Scope 可行性

这是本 spike 的**核心风险点**。需要确认以下服务在 `execScope` 中可正常解析：

| 服务 | 当前注册生命周期 | 预期风险 |
|---|---|---|
| `IQueryRuntimeEngine` | Scoped/Transient | 低 |
| `IToolRegistry` | Scoped | 低 |
| `ToolRegistryBootstrapper` | Scoped/Transient | 低 |
| `IChatClient` | 需确认 | 中 — 可能依赖 HttpContext |
| `ILLMExecutor` | 需确认 | 中 — 可能依赖 session state |
| `CodexSessionManager` | Scoped | 高 — explore worker 是否需要完整 session？ |
| `IMemoryContextAssembler` | 需确认 | 中 — 依赖 session 和 recall service |

**关键验证方法**：

```csharp
// 在 ExecuteExploreWorkerAsync 开头加诊断代码
try
{
    var engine = sp.GetRequiredService<IQueryRuntimeEngine>();
    var toolRegistry = sp.GetRequiredService<IToolRegistry>();
    var chatClient = sp.GetRequiredService<IChatClient>();
    // ... 逐项解析，记录成功/失败
}
catch (Exception ex)
{
    // 记录哪个服务解析失败，以及完整异常链
    return JsonConvert.SerializeObject(new { success = false, message = $"DI resolution failed: {ex}" });
}
```

**如果 DI 解析失败**：记录具体哪个服务链断裂，在报告中给出以下三选一建议：
1. 调整该服务的注册方式（如 Scoped → Transient）
2. 为 worker 构建简化版服务替代品
3. 放弃复用 BackgroundJob，改用独立 Worker Host

#### 步骤 3：触发 Job 并观察全链路

通过以下方式触发（选一种）：

- **方式 A**（推荐）：写一个集成测试 `ExploreWorkerSpikeTests`
  ```csharp
  [Fact]
  public async Task ExploreWorker_CanRunThroughBackgroundJobPipeline()
  {
      // 1. 通过 BackgroundJobService 创建 ExploreWorker job
      // 2. 等待 JobSupervisorHostedService 认领并执行
      // 3. 检查 job 最终状态为 Completed
      // 4. 检查 OutboxEvent 存在 "JobCompleted" 事件
      // 5. 检查 result payload 包含 explore 结果
  }
  ```

- **方式 B**：直接调用 API 创建 job（如果 spike 需要更真实的端到端验证）

#### 步骤 4：验证 Outbox 投影

确认 `OutboxProjector.DispatchEventAsync()` 能正确处理 `ExploreWorker` 类型的完成事件：

- Redis 投影：`JobView` 中包含 worker 结果摘要
- SignalR 投影：前端收到 worker 完成通知（可先只验证 hub 发送成功）

#### 步骤 5：模拟 Coordinator 消费

在测试中模拟主代理读取 worker 结果：

```text
1. 从 Redis 或 DB 获取已完成 worker 的 result payload
2. 将 result 格式化为 LLM-facing 文本（先用最简单的纯文本格式）
3. 注入到一个模拟 Coordinator 的 messages 列表中
4. 验证模型能读取并基于结果生成下一步指令
```

这一步不需要真正调用 LLM，只需验证数据链路完整。

### A.4 验收标准

| # | 标准 | 通过条件 |
|---|---|---|
| A1 | Job 全链路完成 | `ExploreWorker` job 从 Queued → Running → Completed，无异常 |
| A2 | DI Scope 可行 | worker 所需的核心服务全部在 `execScope` 中成功解析 |
| A3 | Runtime 执行 | `IQueryRuntimeEngine.ExecuteAsync()` 在 worker 上下文中至少完成 1 轮 tool loop |
| A4 | Outbox 回流 | `OutboxEvent` 中有 `JobCompleted` 事件，payload 包含 worker 结果 |
| A5 | 结果可消费 | worker 结果可被结构化读取，字段完整（summary, result, workerType, durationMs） |

### A.5 失败路径决策

| 失败场景 | 决策 |
|---|---|
| DI Scope 中 1-2 个服务解析失败，且可调整注册方式修复 | 记录修复方案，继续 spike |
| DI Scope 中核心服务（engine / chatClient）解析失败，需要大改注册体系 | 评估替代方案：a) 独立 Worker DI container b) Channel-based 本地任务队列 c) 简化版 worker 不经过 runtime |
| Runtime 能运行但 worker 结果无法回流到 Outbox | 检查是 handler 返回格式问题还是 Outbox 投影逻辑需要扩展 |
| 全链路通过但延迟过高（单个 explore worker > 30s） | 分析瓶颈（DI 装配 / LLM 调用 / Outbox 投影），评估是否可接受 |

### A.6 交付物

`docs/spike-reports/ExploreWorkerSpikeReport.md`，包含：

1. DI 解析结果表（每个服务的解析结果和耗时）
2. 全链路时序图（实际运行的各阶段耗时）
3. 遇到的问题及解决方案
4. 结论：BackgroundJob 是否可行，如不可行则给出替代方案建议
5. 对 Phase 1-3 的影响评估

---

## Spike B：Envelope Format A/B

### B.1 目标

用真实任务数据比较三种 LLM-facing 通知格式，选定正式协议格式。

### B.2 候选格式

#### 格式 1：JSON

```json
{
  "task_id": "job-01",
  "worker_type": "forge",
  "status": "completed",
  "summary": "完成用户注册接口的异常处理补丁",
  "result": "修改了 UserController.cs 中的 Register 方法...\n构建输出：\nBuild succeeded.\n    0 Warning(s)\n    0 Error(s)",
  "usage": { "duration_ms": 18420 }
}
```

#### 格式 2：XML

```xml
<task-notification>
  <task-id>job-01</task-id>
  <worker-type>forge</worker-type>
  <status>completed</status>
  <summary>完成用户注册接口的异常处理补丁</summary>
  <result><![CDATA[
修改了 UserController.cs 中的 Register 方法...
构建输出：
Build succeeded.
    0 Warning(s)
    0 Error(s)
  ]]></result>
  <usage>
    <duration_ms>18420</duration_ms>
  </usage>
</task-notification>
```

#### 格式 3：Markdown-fenced

```text
--- task-notification ---
task-id: job-01
worker-type: forge
status: completed
summary: 完成用户注册接口的异常处理补丁

### result
修改了 UserController.cs 中的 Register 方法...
构建输出：
Build succeeded.
    0 Warning(s)
    0 Error(s)

### usage
duration_ms: 18420
--- end ---
```

### B.3 测试样本

从仓库现有数据中收集 **至少 15 个**真实任务结果样本，覆盖以下场景分布：

| 场景 | 数量 | 特征 |
|---|---|---|
| 正常完成（短结果） | 3 | result < 200 字符 |
| 正常完成（长结果 + 多行日志） | 3 | result > 1000 字符，包含 build/test 输出 |
| 正常完成（包含 diff） | 3 | result 中包含 unified diff 格式 |
| 包含特殊字符 | 2 | result 中包含引号、尖括号、反斜杠、中文 |
| 失败结果（错误堆栈） | 2 | result 包含 .NET exception stack trace |
| WaitingUser | 2 | 包含 resumeToken 和等待原因 |

**样本来源**：

1. 优先从 `BackgroundJob` 表中 `Status = Completed / Failed` 的记录提取 `PayloadJson`
2. 如果历史数据不足，从 `CodexOrchestrator.ExecuteCodeTaskAsync()` 的实际运行中手动采集
3. 如果仍然不足，基于真实格式手工构造

### B.4 实施步骤

#### 步骤 1：准备样本数据

```text
1. 从数据库提取真实任务结果
2. 清理敏感信息（如有）
3. 统一存放到 spike 分支的 docs/spike-data/samples/ 目录
4. 每个样本保存为独立文件：sample-01.json, sample-02.json, ...
```

#### 步骤 2：生成三种格式的通知文本

对每个样本，分别生成三种格式的通知文本：

```text
sample-01.json → sample-01.format-json.txt
sample-01.json → sample-01.format-xml.txt
sample-01.json → sample-01.format-markdown.txt
```

可以写一个简单脚本或测试方法自动完成转换。

#### 步骤 3：构造 Coordinator 消费测试 prompt

为每个格式化后的通知，构造以下 prompt：

```text
System: 你是 CodexFlow 的 Coordinator 代理。你刚收到一个 worker 的完成报告。
请基于报告内容回答以下问题：
1. 这个 worker 做了什么？（用一句话概括）
2. 任务是成功还是失败？
3. 如果有错误，错误的根因是什么？
4. 你是否需要派出下一个 worker？如果是，说明类型和任务。

<worker-report>
{格式化后的通知文本}
</worker-report>
```

#### 步骤 4：执行对比

对每个样本 × 每种格式，调用当前配置的 LLM（Gemini Flash Lite 或当前默认模型），记录：

| 指标 | 说明 |
|---|---|
| 解析准确率 | 模型是否正确提取了 task-id, status, summary, result 中的关键信息 |
| 结构完整性 | 模型回答是否覆盖了 prompt 中的 4 个问题 |
| 格式干扰 | 模型是否被格式本身干扰（如把 XML 标签当作指令、JSON 引号匹配错误等） |
| 长文本稳健性 | 对于 > 1000 字符的 result，模型是否仍能正确消费 |
| 特殊字符鲁棒性 | 对于包含引号/尖括号/反斜杠的 result，格式是否破损、模型是否误读 |

#### 步骤 5：统计分析

对每种格式计算：

```text
- 总体准确率 = 正确回答数 / (样本数 × 4 个问题)
- 分场景准确率（按 B.3 中的 6 个场景分别统计）
- 格式干扰率 = 出现格式干扰的样本数 / 总样本数
- 特殊字符破损率 = 格式破损的样本数 / 含特殊字符的样本数
```

### B.5 验收标准

| # | 标准 | 通过条件 |
|---|---|---|
| B1 | 样本覆盖 | 至少 15 个样本，覆盖 B.3 中的 6 个场景 |
| B2 | 对比完成 | 三种格式 × 全部样本的对比数据收集完毕 |
| B3 | 数据充分 | 至少一种格式的总体准确率 ≥ 85% |
| B4 | 结论明确 | 报告给出唯一推荐格式，或明确说明需要更多数据 |

### B.6 决策规则

| 结果 | 决策 |
|---|---|
| 某一格式在准确率和鲁棒性上显著优于其他两种（准确率差距 > 10%） | 选定该格式作为正式协议 |
| 三种格式差异不显著（准确率差距 ≤ 10%） | 默认选 Markdown-fenced（实现成本最低，与现有 prompt 风格一致） |
| 所有格式准确率都低于 70% | 暂停协议选型，先分析模型消费能力的瓶颈，可能需要调整 prompt 策略 |
| XML 准确率最高但特殊字符破损率也最高 | 评估是否可通过 CDATA 包裹解决；如不能，降级选 Markdown |

### B.7 交付物

`docs/spike-reports/EnvelopeFormatSpikeReport.md`，包含：

1. 样本清单（编号、场景分类、字符数、特征标签）
2. 三种格式的原始对比数据表格
3. 汇总统计（总体准确率、分场景准确率、干扰率、破损率）
4. 推荐格式及理由
5. 已知局限和后续建议

---

## 执行时间线

```text
Day 0    : Spike 启动，确认 Runtime 稳定期状态
Day 0-1  : Spike B 样本收集 + 格式生成（可先行）
Day 0-2  : Spike A 步骤 1-2（ExploreWorker handler + DI 验证）
Day 1-2  : Spike B 步骤 3-5（LLM 对比 + 统计）
Day 2-3  : Spike A 步骤 3-5（全链路 + Outbox + 消费验证）
Day 3    : 两份报告撰写
Day 4    : 报告评审 → 决定是否进入 Phase 1
```

Spike A 和 Spike B 可以并行推进，无硬依赖。

---

## 分支与代码管理

- 分支命名：`spike/coordinator-worker-phase-0.5`
- 所有 spike 代码放在该分支
- spike 结束后：
  - 如果决定进入 Phase 1：spike 中的可复用代码（如 ExploreWorker handler 骨架）提交 cherry-pick 到主分支
  - 如果决定暂停：分支归档，不合并
- spike 期间不修改现有生产代码路径

---

## 风险与注意事项

1. **Spike A 的 DI 风险是最大的不确定性**。如果 `IChatClient` 或 `ILLMExecutor` 在后台 scope 中依赖 HttpContext 或 session state，会直接阻断整条链路。应在步骤 2 尽早验证，失败后立即评估替代方案。

2. **Spike B 的模型一致性**。格式对比应使用同一个模型、同一个 temperature（建议 temperature=0），避免随机性干扰结论。如果模型自身不稳定，可考虑每个样本跑 3 次取多数。

3. **样本偏差**。如果历史数据中大多数是短结果、正常完成的任务，会低估长文本和错误场景下的格式差异。B.3 的场景分布要求必须严格遵守。

4. **不要过早优化**。Spike 代码的目标是验证假设，不是写生产级实现。不需要完善的错误处理、不需要完整的 retry、不需要文档。
