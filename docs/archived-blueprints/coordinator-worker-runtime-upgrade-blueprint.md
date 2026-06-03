# CodexFlow Coordinator/Worker Runtime 升级蓝图

> 版本：0.3
> 日期：2026-04-13
> 状态：Reviewed - Spike Authorized
> 适用范围：当前主干代码
> 文档目标：基于 `codexflow` 当前真实实现，规划从“统一 runtime + 单主代理执行器”升级到“Coordinator/Worker 协议化执行系统”

---

## 1. 文档结论

CodexFlow 当前已经具备一套很强的底层能力：

- 统一 query/tool loop：`IQueryRuntimeEngine` + `QueryRuntimeEngine`
- 后台异步执行：`JobSupervisorHostedService` + `BackgroundJobRunner`
- 任务与事件投影：`BackgroundJob` + `OutboxEvent` + Redis/SignalR/SSE
- 会话上下文与记忆：`CodexSessionManager` + `HistorySummary` + Semantic Recall
- 多语言分析资产：`Roslyn` + `Python / Node / Java` semantic diff / inspector 资产
- 角色提示词：`Architect / Forge / Sentry`
- Shadow Worktree：`GitService.CreateShadowWorktreeAsync`

但系统当前的主形态仍然是：

- 单主代理主导
- `execute_code_task` 驱动的串行原子任务执行
- `Architect / Forge / Sentry` 主要体现为 prompt 角色，而不是一等 worker 类型
- 4 种语言的 LSP / inspector 资产存在，但尚未进入 Forge/Worker 主协议面
- 后台 Job 与主对话之间缺少面向模型的标准回流协议

因此，本轮升级的核心不是“再造一个多 agent 概念”，而是把现有底座真正串成一个可持续运行的 Coordinator/Worker 闭环：

1. 主代理只负责规划、派工、综合与用户沟通
2. Worker 成为真正独立的执行单元，具备隔离上下文、隔离工具面、可继续会话和标准结果回流
3. 后台 Job、Gateway SSE、Runtime Event、TaskList、WaitingUser 全部收敛到统一的任务通知协议
4. 验证从“给 JSON 结论”升级为“给命令证据 + 对抗式结论”

一句话总结：

**CodexFlow 不需要复制 Claude Code 的全部实现，但非常值得借鉴它的三件事：角色隔离、任务协议化、异步通知回流。**

本轮蓝图在原有 Coordinator/Worker 主线之外，新增一个必须补齐的能力层：

**LSP 接入。**

这里的 “LSP 接入” 不要求所有语言都立刻切到纯 JSON-RPC LSP 客户端实现，但要求把当前散落在 Roslyn、semantic diff provider、`skills/*-inspector` 里的多语言能力，统一收口为 worker runtime 可消费、可回退、可观测的语言服务层。

本蓝图进一步收敛后的执行策略是：

1. 第一阶段只批准 `Phase 0-3 + 正式评审门控`
2. `Phase 4-8` 不默认自动启动，必须基于真实任务数据复审
3. 本蓝图不替代 [query-runtime-upgrade.md](../archived-blueprints/query-runtime-upgrade.md)，而是在其稳定期之上增量演进
4. `Phase 8` 的后续后端稳定性加固已完成并归档，收口记录见：[backend-runtime-stability-hardening-plan.md](../archived-blueprints/backend-runtime-stability-hardening-plan.md)

### 1.1 与 Query Runtime 升级的依赖关系

本蓝图与统一 Runtime 升级的关系必须先说清楚，否则两套计划会交叉冲突。

当前 [query-runtime-upgrade.md](../archived-blueprints/query-runtime-upgrade.md) 状态是：

- Phase 0A-4A 已完成
- `Query Runtime` 稳定期已通过，`Phase 0.5` 可启动
- Phase 5 `tool result budget` 尚未开始
- Phase 6 `Prompt/Context 协同增强` 尚未开始

本蓝图的判断如下：

1. `QueryRuntime` 的稳定期观察结果是本蓝图的硬前置条件
   - 该前置条件已满足；若后续出现系统性回归，应重新评估并暂停后续阶段
   - 不在未稳定的 loop 内核之上叠加 worker 系统
2. `QueryRuntime Phase 5/6` 不是本蓝图 `Phase 0-3` 的硬前置条件
   - 原因：第一阶段主要做协议层、只读 worker 和派工接口
   - 这些能力依赖“loop 稳定”，但不依赖预算治理和高级 prompt/context 协同
3. `QueryRuntime Phase 5` 是本蓝图 `Phase 5+` 的强依赖
   - 尤其影响 verify worker、长报告注入、tool result budget
4. `QueryRuntime Phase 6` 是本蓝图 `Phase 6+` 的推荐依赖
   - 尤其影响更复杂的 checklist repair prompt、worker 结果回流后的上下文治理
   - 但当前本蓝图 `Phase 6` 的最小可用闭环已先在现有 runtime 能力上落地

换句话说：

- `Phase 0-3`：可以在 Runtime 稳定期通过后启动
- `Phase 4-8`：应尽量等 Runtime Phase 5 起步后再推进

### 1.2 第一阶段边界

为避免范围蔓延，第一阶段明确只做以下目标：

1. 验证 BackgroundJob 是否能承载只读 worker
2. 选定一套 LLM-facing 通知格式
3. 定义最小 worker 类型系统
4. 落地 `spawn_worker / continue_worker / stop_worker`
5. 实现只读 worker 并行和结果回流
6. 建立 4 语言 Language Service / LSP 接入基线

第一阶段明确不承诺：

1. forge worker 并发写
2. 完整 Hook 体系
3. verify worker 全量重做
4. checklist 全链路落地
5. UI 完整 worker 面板
6. 原始 LSP JSON-RPC 对模型直通

第一阶段的核心价值不是“把所有 worker 做完”，而是：

**证明 Coordinator 能稳定并行调度只读 worker，并通过统一协议消费结果。**

在 `Phase 1.5` 完成后，下一步不应直接跳到更复杂的 Forge 并发或 UI 扩展，而应先补上 LSP 接入基线。原因很直接：

- 当前 4 语言能力虽然存在，但没有进入 worker 主路径
- 若继续推进 worker 类型系统而不补 LSP，`explore / plan / forge / verify` 的语言智能仍然会停留在“有资产、没接线”
- 这会让后续 worker 工具白名单、shadow worktree 语义和验证证据面再次分裂

### 1.3 文档阅读顺序与维护约定

为了避免同一主题在多份文档里交叉覆盖，当前文档层级约定如下：

1. 本文档是总蓝图
   - 负责阶段划分、目标态、边界、依赖关系和主里程碑
2. [backend-runtime-stability-hardening-plan.md](../archived-blueprints/backend-runtime-stability-hardening-plan.md) 是 `Phase 8` 的归档收口记录
   - 负责保留 `Gateway / QueryRuntime / Worker` 后端稳定性加固的完成结果与决策证据
3. `docs/archived-blueprints/` 下的文档仅作为历史背景和决策来源
   - 默认不再作为当前执行入口，除非需要追溯上下文

维护原则：

1. 新的阶段性目标、里程碑调整、范围边界变化，优先更新本文档
2. 某一阶段的具体落地顺序、测试策略、验收清单，优先更新对应补充文档
3. 已完成且不再作为执行入口的文档，应移入 `docs/archived-blueprints/`

---

## 2. 当前现状

本节只描述仓库里已经存在的能力与边界，不做理想化推演。

### 2.1 当前已经具备的底座

#### A. 统一 Runtime 已存在

当前项目已经有统一执行内核：

- [IQueryRuntimeEngine](/Users/iwaitu/github/codexflow/CodexFlow.Core/Runtime/IQueryRuntimeEngine.cs)
- [QueryRuntimeEngine](/Users/iwaitu/github/codexflow/CodexFlow.Core/Runtime/QueryRuntimeEngine.cs)
- [DefaultToolExecutionCoordinator](/Users/iwaitu/github/codexflow/CodexFlow.Core/Runtime/DefaultToolExecutionCoordinator.cs)
- [DefaultQueryRecoveryPolicy](/Users/iwaitu/github/codexflow/CodexFlow.Core/Runtime/DefaultQueryRecoveryPolicy.cs)
- ✅ [ADR-001: Kernel Runtime 语义基线](../adr/ADR-001-kernel-runtime-semantics.md)

它已经支持：

- round loop
- 流式 thinking / content 事件
- tool 调用与结果归并
- 基础 dedupe
- transport recovery
- intervention hook（guardrail / critique）

这意味着 CodexFlow 已经不缺“主循环”，缺的是“主循环之外的多执行体协议”。

#### B. 后台 Job 系统已经比本地任务系统更强

当前项目已经有企业级后台执行底座：

- [JobSupervisorHostedService](/Users/iwaitu/github/codexflow/CodexFlow/Services/Background/JobSupervisorHostedService.cs)
- [BackgroundJobRunner](/Users/iwaitu/github/codexflow/CodexFlow/Services/Background/BackgroundJobRunner.cs)
- [BackgroundJobService](/Users/iwaitu/github/codexflow/CodexFlow/Services/Background/BackgroundJobService.cs)
- [OutboxProjector](/Users/iwaitu/github/codexflow/CodexFlow/Services/Background/OutboxProjector.cs)
- [job-supervisor-tech.md](/Users/iwaitu/github/codexflow/docs/job-supervisor-tech.md)

它已经支持：

- claim / lease / heartbeat
- WaitingUser / Resume
- checkpoint
- outbox projection
- Redis / SignalR / Mongo 投影

这套能力天然适合承载 worker，而不是只承载“单个后台长任务”。

#### C. Shadow Worktree 已经存在

当前项目已经有成熟的隔离工作区机制：

- [GitService](/Users/iwaitu/github/codexflow/CodexFlow.Core/Services/GitService.cs)
- [CodexOrchestrator](/Users/iwaitu/github/codexflow/CodexFlow.Core/Agents/CodexOrchestrator.cs)

当前 `ExecuteCodeTaskAsync()` 已经在 shadow worktree 中执行代码类任务，并配合 policy / semantic diff / validator 进行闭环。

这意味着 CodexFlow 在“写型 worker 默认隔离执行”这一点上，已经比很多系统更接近目标态。

#### D. 角色 prompt 已经存在，但尚未协议化

当前已有：

- [CodexPrompts.cs](/Users/iwaitu/github/codexflow/CodexFlow.Core/Constants/CodexPrompts.cs)
- `ArchitectPromptTemplate`
- `ForgePromptTemplate`
- `SentryPromptTemplate`

但这些角色当前主要还是：

- system prompt 层面的行为引导
- 不是独立的 worker 类型
- 没有独立任务协议
- 没有稳定的“spawn / continue / stop / report back”机制

#### E. 任务修复链已有增量化方向

当前已有明确的增量修复设计：

- [task-incremental-validation-repair-plan.md](/Users/iwaitu/github/codexflow/docs/feature/task-incremental-validation-repair-plan.md)

这说明项目已经意识到：

- repair 不能一直围绕整段 task.Description 重跑
- validator 需要结构化产物
- orchestrator 需要 merge checklist evaluation

这个方向与 Coordinator/Worker 的思路高度一致。

#### F. 多语言分析资产已存在，但尚未接入 Worker 主路径

当前仓库已经注册了 4 种语言的多语言分析能力：

- C#：`RoslynSemanticDiffProvider`
- Python：`PythonSemanticDiffProvider`
- Node/TypeScript：`NodeSemanticDiffProvider`
- Java：`JavaSemanticDiffProvider`

同时仓库内也存在：

- `skills/python-inspector`
- `skills/node-inspector`
- `skills/csharp-inspector`
- `skills/java-inspector`

但这些能力当前的问题是：

- Forge 主工具面没有显式暴露 LSP / inspector 工具
- `ExecuteCodeTaskAsync()` 主闭环不会主动在 Forge 施工阶段调用语言服务
- 多语言资产更多是“semantic diff 后处理”或“skill 目录里的脚本”，不是正式 runtime 能力
- shadow worktree 中的文件修改与诊断刷新之间没有统一协议

换句话说：

**CodexFlow 已经有多语言语言智能资产，但还没有语言服务接入层。**

### 2.2 当前的主要短板

#### A. 缺少真正的一等 Worker 协议

当前虽然有后台 Job 和原子任务，但没有“显式 worker”这一层概念。

表现为：

- 主代理无法显式启动多个不同职责的 worker
- 不能继续某个既有 worker 的上下文
- 不能标准化中止 / 追问 / 回收某个 worker
- `Architect / Forge / Sentry` 仍偏角色切换，不是子执行体

#### B. 缺少模型可消费的标准回流协议

当前系统内部有：

- JSON DTO
- OutboxEvent
- Gateway SSE
- TaskList snapshot

但缺少一层“专门喂给模型看的通知信封”。

这会导致：

- 后台任务完成后，主代理难以稳定读取结构化结果
- 多行日志、diff、错误输出混入 JSON 时更容易破格式
- WaitingUser、验证失败、任务完成等系统事件缺少统一上下文注入格式

#### C. Validator 仍然偏“判断器”，不够像“对抗式验证者”

当前 [DefaultCodexValidator.cs](/Users/iwaitu/github/codexflow/CodexFlow.Core/Agents/DefaultCodexValidator.cs) 的优点是：

- 有 deterministic fallback
- 有执行日志证据回退
- 对空响应 / 协议错误有处理

但当前短板也明显：

- 输出目标仍是 `ValidationResult JSON`
- 缺少“每个检查项都必须附命令与观察结果”的硬约束
- 缺少系统性的边界值 / 并发 / 幂等 / 回归探测要求
- 更像“严格审查器”，还不是“破坏性验证器”

#### D. 当前 Task / Job / Runtime / Gateway 之间协议分裂

当前存在多套并行但未统一的协议面：

- Runtime Event
- Gateway SSE Event
- TaskList Snapshot
- JobView / Outbox Projection
- ValidationResult
- Retry feedback / session metadata

程序内部可以工作，但对于“主代理如何理解 worker 完成了什么”这一层，仍然没有单一协议。

#### E. 文档中的 agent 架构描述已超前于实现

[docs/agents_technical_guide.md](/Users/iwaitu/github/codexflow/docs/agents_technical_guide.md) 描绘的是一个接近 Claude 风格的多 agent 系统，但当前代码还没有完整实现对应的 worker 生命周期和回流闭环。

本轮升级应把这份文档从“目标态描述”变成“可落地系统”。

#### F. 多语言 LSP 能力仍停留在“资产存在”，还不是“协议化能力”

这也是当前蓝图里必须补上的缺口。

当前缺的不是：

- 再写一个 `lsp_inspector.py`
- 再加一个语言专用脚本目录

当前真正缺的是：

- 统一的语言服务抽象
- workspace / shadow worktree 级会话管理
- 对模型暴露的 typed LSP 工具面
- 诊断刷新、引用查询、定义跳转的统一降级语义

如果这层不补，后续 worker 类型系统会出现明显断层：

- `explore` 仍主要靠 grep / search
- `plan` 仍缺少稳定的符号级调用链视角
- `forge` 在多语言项目里仍只能靠文本搜索和编译报错回推
- `verify` 也无法把静态诊断纳入统一证据面

---

## 3. 改进思路

### 3.1 总体思路

本轮不建议重写整套 runtime，也不建议先从“做更多 prompt”入手。

建议的主路径是：

1. 保留当前 `QueryRuntimeEngine` 作为统一 loop 内核
2. 基于现有 `BackgroundJob + Outbox` 体系新增 Worker 语义层
3. 把 `Architect / Forge / Sentry / Explore / Plan` 提升为正式 worker 类型
4. 给所有 worker 回流结果定义统一的 LLM-facing XML envelope
5. 让主代理通过这些 envelope 进行综合、追问、修复和继续派工
6. 在 worker/runtime 之间补上统一语言服务层，让 4 种语言能力从“仓库资产”升级为“正式工具面”

### 3.2 目标架构

目标形态如下：

```text
User
  -> Coordinator Runtime
      -> Spawn Worker Job
          -> Worker Runtime
              -> Tool Loop / Language Service / Worktree / Validation
          -> OutboxEvent
          -> LLM-facing XML Notification
      -> Coordinator reads notification
      -> Synthesize / continue worker / spawn next worker
```

### 3.3 建议的 Worker 类型

第一阶段建议只落 4 种：

1. `explore`
   - 只读
   - 快速搜索、定位文件、归纳调用链
   - 优先消费符号、定义、引用、诊断等语言服务能力
   - 不允许写入

2. `plan`
   - 只读
   - 输出实现路线与关键文件
   - 可消费跨文件符号关系与影响范围
   - 不允许写入

3. `forge`
   - 写型
   - 默认在 shadow worktree 运行
   - 负责修改代码、执行局部验证
   - 写后可请求诊断刷新、引用检查与符号级风险确认

4. `verify`
   - 严格只读
   - 可运行命令、测试、临时脚本
   - 必须输出证据化报告
   - 可把 diagnostics 作为证据的一部分

这 4 类已经足以覆盖当前最常见的 Coordinator/Worker 场景。

### 3.4 关于通知协议选型

当前推荐方向仍然偏向 XML envelope，但这不是无需验证的既定事实。

本节结论应理解为：

- XML 是默认优先候选
- 但必须先经过 A/B spike，再决定是否成为正式协议

#### 为什么 XML 是优先候选

适合改成 XML 的部分：

- Worker 完成通知
- Worker 失败通知
- WaitingUser 通知
- 验证报告注入
- 系统级任务状态回流到主代理的文本协议

原因：

- 这些内容要喂给模型看
- 内容中可能混有多行日志、终端输出、diff、代码块
- XML 对自由文本包裹更稳
- 比 JSON 更不容易被模型搞坏引号、转义、逗号

#### 为什么不能直接拍板

当前仓库还没有足够数据证明：

1. 现有 JSON/文本格式在真实任务中破坏频率有多高
2. XML 对当前主力模型的消费准确率一定优于其他格式
3. XML 相比 Markdown-fenced 结构化文本的收益足够大

因此在 Phase 1 前，必须做格式选型 spike：

- JSON
- XML
- Markdown-fenced 结构化文本

基于真实任务样本比较主代理消费准确率，再定最终格式。

#### 不应改成 XML 的部分

- 数据库存储
- OutboxEvent payload
- SSE / SignalR 内部 DTO
- tool/function call 参数
- C# 内部强类型对象

原因：

- 这些是给程序读的，不是给模型读的
- JSON / DTO 更适合版本化、测试和演进

### 3.5 建议的最小 LLM-facing 协议面

第一阶段建议定义 4 个信封：

1. `task-notification`
2. `verification-report`
3. `waiting-user`
4. `system-notice`

示例：

```xml
<task-notification>
  <task-id>job-01</task-id>
  <worker-type>forge</worker-type>
  <status>completed</status>
  <summary>完成用户注册接口的异常处理补丁</summary>
  <result>...</result>
  <usage>
    <duration_ms>18420</duration_ms>
  </usage>
</task-notification>
```

注意：

- XML 只作为“注入给模型看的 envelope”
- 内部仍保留 JSON/DTO
- 由 adapter 把内部事件投影成 XML 文本块

### 3.6 LSP 接入原则

本蓝图新增的 LSP 接入，不建议理解成“把原始 LSP JSON-RPC 直接暴露给模型”。

正确的方向应是：

1. 对外统一称为 `Language Service`
   - 设计目标以 LSP 为主
   - 但允许 Roslyn、Pyright、JavaParser、现有 inspector 脚本作为过渡或 fallback
2. 对模型只暴露 typed 工具
   - 例如：
     - `lsp_get_diagnostics`
     - `lsp_document_symbols`
     - `lsp_find_references`
     - `lsp_go_to_definition`
     - `lsp_workspace_symbols`
   - 不暴露原始 `lsp_request(method, params)` 这类无边界接口
3. Language Service 必须是 workspace / worktree 级作用域
   - 主工作区与 shadow worktree 不能共享脏状态
   - `forge` worker 默认绑定 shadow worktree 语言服务会话
4. 写后同步必须是自动的
   - `write_file / smart_patch / delete_file` 后，语言服务需收到刷新或重建指令
   - 至少支持 `didChange / didSave` 语义，或等价 fallback 刷新
5. 诊断必须能进入证据面，而不是只停留在日志里
   - `verify` worker 可把 diagnostics 作为证据附件
   - `forge` worker 可把关键 diagnostics 作为 repair 约束
6. 所有语言服务都必须有降级路径
   - server 初始化失败
   - workspace 过大
   - 二进制缺失
   - 协议错误
   - 以上情况都不能直接拖死 worker 主路径

第一阶段新增 LSP 接入时，建议优先支持 4 种语言：

1. C#：优先复用 Roslyn / 现有分析能力
2. Python：优先接 Pyright；不可用时回退到现有 inspector / script
3. Node/TypeScript：优先接 tsserver / TypeScript language service；不可用时回退到现有 node-inspector
4. Java：优先接 jdtls；未具备时回退到现有 Java inspector / JavaParser 工具

这意味着蓝图里的 “LSP” 在实现期更准确地说应是：

**以 LSP 为目标形态、允许 LSP-equivalent 过渡实现的语言服务层。**

### 3.7 Hook 扩展体系设计

Claude Code 很值得借鉴的一点，不是“功能很多”，而是它在主循环外预留了大量 hook/extension 面，使很多原本会硬编码进 loop 的策略能够后置演进。

对 CodexFlow 来说，这个方向并不陌生。当前已经存在：

- [QueryRuntimeEngine.cs](/Users/iwaitu/github/codexflow/CodexFlow.Core/Runtime/QueryRuntimeEngine.cs)
- [QueryRuntimeIntervention.cs](/Users/iwaitu/github/codexflow/CodexFlow.Core/Runtime/QueryRuntimeIntervention.cs)

当前 `IQueryRuntimeInterventionHook` 已经覆盖了：

- `OnToolCallRequestedAsync`
- `OnToolExecutionCompletedAsync`

这说明 CodexFlow 已经具备 hook 化的雏形。但当前 hook 面仍偏窄，主要服务于：

- guardrail
- critique

如果后续要支持 Coordinator/Worker、通知协议和扩展策略，建议把 hook 体系显式升级为两层。

但必须强调：

**本轮第一阶段不落完整 hook 矩阵。**

第一阶段只允许落“本轮 worker 闭环必需的最小 hook”。

#### A. Runtime Hooks

作用范围：

- 单次 query/tool loop 生命周期

长期建议包含以下节点：

1. `OnPromptComposed`
   - system prompt 已组装完成，但尚未发给模型
   - 用于追加动态策略、实验提示、worker-specific patch

2. `OnBeforeModelRequest`
   - LLM 请求即将发出
   - 用于预算检查、日志注入、审计、上下文裁剪提示

3. `OnAfterModelResponse`
   - LLM 输出已回来，但尚未进入 tool loop
   - 用于空响应修复、格式修正、协议探测

4. `OnToolCallRequested`
   - 已存在
   - 用于 guardrail、危险调用拦截、自动补参、策略改写

5. `OnToolExecutionCompleted`
   - 已存在
   - 用于 critique、结果拒收、自动注入反馈、工具结果摘要

6. `OnRecoveryTriggered`
   - 发生 empty response / malformed protocol / stall / max rounds 时触发
   - 用于决定 retry、terminate、inject feedback、转后台

7. `OnRoundCompleted`
   - 单轮结束
   - 用于低成本统计、状态采样、worker summary 更新

8. `OnQueryCompleted`
   - 整个 query 结束
   - 用于转录收口、摘要生成、最终通知投影

#### B. Worker Hooks

作用范围：

- worker/job 生命周期

长期建议包含以下节点：

1. `OnWorkerSpawned`
   - worker/job 创建完成
   - 用于登记元数据、输出初始事件、设置默认隔离策略

2. `OnWorkerResumed`
   - WaitingUser / retry / continue 场景恢复
   - 用于恢复上下文、补写 resume notice

3. `OnWorkerHeartbeat`
   - 长任务保活期间
   - 用于进度摘要、状态采样、告警

4. `OnWorkerCompleted`
   - worker 正常完成
   - 用于生成 `task-notification` XML envelope

5. `OnWorkerFailed`
   - worker 异常失败
   - 用于生成失败 envelope、补充故障摘要

6. `OnWorkerWaitingUser`
   - worker 进入 WaitingUser
   - 用于生成 `waiting-user` envelope，串接 resumeToken

7. `OnWorkerCancelled`
   - worker 被 stop / cancel
   - 用于状态回流和资源清理

#### C. 第一阶段最小 Hook 落点

第一阶段只建议落以下 4 个：

1. `OnAfterModelResponse`
   - 用于 response 修正、worker 摘要抽取、最终结果转 envelope 前置处理

2. `OnWorkerCompleted`
   - 用于生成完成通知

3. `OnWorkerFailed`
   - 用于生成失败通知

4. `OnWorkerWaitingUser`
   - 用于生成 WaitingUser 通知

其他 hook 节点本轮只做概念预留，不做完整实现，不做全量埋点，不做全量测试矩阵。

#### D. 设计原则

如果把 hook 体系纳入正式架构，必须遵守以下原则：

1. 主循环只负责时序，不负责策略细节
2. hook 输入输出必须是强类型，避免在 hook 中拼接临时 JSON
3. 每个 hook 都必须定义失败语义：
   - fail-open
   - fail-closed
   - fail-log-and-continue
4. hook 必须可观测：
   - 是否执行
   - 是否修改了结果
   - 是否阻断了流程
5. hook 必须可组合，但执行顺序要固定

#### E. 与当前实现的衔接方式

本轮不建议直接废弃 `IQueryRuntimeInterventionHook`，而应采用兼容演进策略：

1. 保留 `IQueryRuntimeInterventionHook` 作为 Phase 1 兼容层
2. 新增更通用的 `IRuntimeHook` / `IWorkerHook`
3. 让原有 intervention hook 适配到新的 runtime hook 生命周期节点
4. 在新功能上优先使用新接口，旧功能逐步迁移

#### F. 为什么 Hook 设计仍值得纳入蓝图

本轮如果只做 Coordinator/Worker 和 XML envelope，而不顺手把 hook 面补齐，后面很容易再次出现：

- XML 包装逻辑散落在 Gateway / BackgroundJob / Validator
- recovery 策略继续写回主循环
- worker 生命周期通知继续散落在 OutboxProjector / Controller / Gateway
- 新能力只能继续堆条件分支

因此，建议把 hook 体系视为本轮升级的“结构性预留能力”，与 Coordinator/Worker 一起设计，但只落最小闭环所需部分。

---

## 4. 详细实施计划

本节按“先协议、再 worker、后优化”的顺序推进。

### Phase 0：现状收口与命名冻结

> ✅ **已完成**（2026-04-13）
>
> 实现同步：
> - 术语、Hook 分层、通知边界、与 Query Runtime 的依赖关系已通过 ADR 固化
> - `Phase 0-3` 的启动边界已在本文档与评审门控中冻结

目标：

- 冻结术语与边界，避免后续同一概念多种叫法

实施内容：

1. 统一术语
   - `Coordinator`
   - `Worker`
   - `WorkerType`
   - `WorkerNotification`
   - `WaitingUser`
2. 明确第一阶段只做 4 种 worker
3. 明确 XML 只用于 LLM-facing envelope
4. 明确 Runtime / Outbox / Gateway / TaskList 不做大规模重命名
5. 冻结 hook 术语：
   - `Runtime Hook`
   - `Worker Hook`
   - `Intervention Hook`（兼容层）
6. 与 Query Runtime 蓝图对齐时间线：
   - 确认稳定期观察状态
   - 确认 `Phase 0-3` 是否允许启动

建议产物：

- 本文档
- ✅ [ADR-002: XML 仅用于 LLM-facing 通知层](../adr/ADR-002-llm-facing-notification-boundary.md)
- ✅ [ADR-003: Hook 体系分层边界](../adr/ADR-003-hook-layering-boundary.md)
- ✅ [ADR-004: 与 Query Runtime 升级的依赖关系](../adr/ADR-004-coordinator-worker-runtime-dependency.md)
- ✅ [Phase 1 评审门控](../review-gates/phase-1-review-gate.md)

验收标准：

- ✅ 仓库内新文档和新增类型不再混用 `agent / subagent / task / job` 作为同义词
- ✅ 仓库内不再把所有扩展都继续塞进 `IQueryRuntimeInterventionHook`
- ✅ 与 Query Runtime 升级负责人的时间线对齐完成

### Phase 0.5：技术 Spike 与格式验证

> ✅ **已完成**：
> - [Explore Worker Spike Report](../spike-reports/ExploreWorkerSpikeReport.md)
> - [Envelope Format Spike Report](../spike-reports/EnvelopeFormatSpikeReport.md)
> - [Phase 1 评审门控](../review-gates/phase-1-review-gate.md)

目标：

- 在投入 Worker 系统主线开发前，先验证最关键的两件事：
  - BackgroundJob 是否足以承载只读 worker
  - 哪种通知格式最适合作为 LLM-facing 协议

实施内容：

1. `Explore Worker Vertical Slice Spike`
   - 选一个只读探索任务
   - 通过 `BackgroundJobRunner` 跑通：
     - worker context 注入
     - runtime 执行
     - outbox 回流
     - gateway 注入
2. `Envelope Format A/B Spike`
   - 选取 10-20 个真实任务结果样本
   - 分别用：
     - JSON
     - XML
     - Markdown-fenced
   - 注入到 Coordinator 模型
   - 比较消费准确率

建议产物：

- `ExploreWorkerSpikeReport.md`
- `EnvelopeFormatSpikeReport.md`

验收标准：

- 至少 1 个只读 worker 通过 BackgroundJob 全链路跑通
- 至少 10 个真实样本完成格式对比
- 明确选定正式格式，或明确继续保留候选

Spike 异常路径处理：

1. 若 `Explore Worker Vertical Slice Spike` 结论为 `BackgroundJobRunner` 无法稳定承载 worker 上下文：
   - 不直接进入 Phase 1
   - 先输出替代方案评估：
     - `Worker Host` 独立执行宿主
     - Channel-based 本地任务队列
     - 专用 WorkerRunner scope 适配层
   - 经评审后再决定是否继续复用 `BackgroundJob`
2. 若 `Envelope Format A/B Spike` 结论为三种格式消费准确率差异不显著：
   - 默认选择实现成本最低、与现有系统最一致的方案
   - 当前默认回退为 `Markdown-fenced 结构化文本`
   - 不在“无显著收益”的情况下强推 XML

### Phase 1：定义 LLM-facing Worker 通知协议

> ✅ **已完成**：[Phase 1 评审门控报告](../review-gates/phase-1-review-gate.md)（2026-04-13）
> 
> 实现同步：
> - `BackgroundJobRunner` 已在 `JobCompleted` / `JobFailed` / `JobWaitingUser` 时构造 XML envelope，并写入 payload 的 `workerNotificationXml`
> - `NotificationDispatcher` / `MainSessionInjectionService` / `GatewayMessageProcessor` 已接通 XML 回流链路
> - serializer 已补齐 `waiting` 状态映射与 `]]>` CDATA 防御

目标：

- 给主代理一套稳定可读的结果回流格式

前提：

- Phase 0.5 已完成格式 spike

实施内容：

1. 根据 spike 结果选定正式格式
   - XML 优先
   - 若 XML 未显著优于其他格式，则以数据结论为准
2. 新增通知模型
   - `WorkerNotificationEnvelope`
   - `VerificationReportEnvelope`
   - `WaitingUserEnvelope`
3. 新增 serializer / formatter
   - 输入：内部强类型对象
   - 输出：正式选定的 LLM-facing 文本格式
4. 约定字段
   - `task-id`
   - `worker-type`
   - `status`
   - `summary`
   - `result`
   - `usage`
   - `resume-token`（仅 WaitingUser）
5. 明确 escaping / fenced / 长文本裁剪规则
6. 为 Gateway / BackgroundJob / Validator 增加 adapter 层

建议落点：

- `CodexFlow.Core/Protocols/`
- `CodexFlow.Application/Notifications/`
- `CodexFlow/Services/Notifications/`

验收标准：

- 新增 `WorkerNotificationEnvelopeTests`
- 覆盖不少于 10 个边界场景：
  - 多行日志
  - 特殊字符
  - 超长输出
  - 空 summary
  - WaitingUser resumeToken
- 全部 green
- `BackgroundJobRunner -> outbox payload -> NotificationDispatcher -> GatewayMessage.Content` 链路打通
- Spike 报告中选定的正式格式被唯一实现，文档同步更新

### Phase 1.5：Hook 基础设施落点

> ✅ **已完成**（2026-04-13）
>
> 实现同步：
> - `IWorkerHook` / `IWorkerHookDispatcher` 已落地，worker completed / failed / waiting-user 均已通过 hook 接入
> - 通知协议生成已经从业务主路径抽离到 hook + projector
> - 兼容层仍保留 `IQueryRuntimeInterventionHook`

目标：

- 给后续 Coordinator/Worker 和通知协议留出统一扩展面

实施内容：

1. 仅定义最小 hook 面
   - `IWorkerHook`
   - 可选最小 `IRuntimeHook`
3. 定义 hook manager / dispatcher
4. 第一阶段只接以下调用点：
   - after model response
   - worker completed / failed / waiting-user
5. 让当前 `IQueryRuntimeInterventionHook` 作为兼容适配层存在

验收标准：

- ✅ 不修改主执行语义的前提下，可以挂接新的 worker hook
- ✅ 通知协议生成逻辑通过 hook 接入，而不是写死在业务类中
- ✅ 本阶段不引入 15 个 hook 节点，不引入全量 hook 埋点项目
- ✅ 已新增 `WorkerHookDispatcherTests`
- ✅ 已覆盖：
  - 注册单个 `IWorkerHook` 后 `OnWorkerCompleted` 被触发
  - 多个 hook 的固定顺序执行
  - 单个 hook 失败不污染主 worker 完成路径

### Phase 1.6：LSP 接入基线

> ✅ **已完成**（2026-04-13）
>
> 实现同步：
> - 统一语言服务注册表、typed LSP tools、shadow worktree 写后刷新已接通
> - 4 语言 provider 注册、初始化降级、diagnostics 裁剪与刷新语义均已落地

目标：

- 把当前仓库里已有的 4 语言分析资产，从“散落在 semantic diff / skill 脚本里的能力”升级为 worker runtime 可消费的正式语言服务层

实施内容：

1. 新增统一语言服务抽象
   - `ILanguageServiceRegistry`
   - `ILanguageServiceSessionFactory`
   - `ILanguageServiceClient` 或等价接口
2. 定义最小能力面
   - `get_diagnostics`
   - `document_symbols`
   - `workspace_symbols`
   - `go_to_definition`
   - `find_references`
3. 建立 4 语言适配注册
   - C#
   - Python
   - Node/TypeScript
   - Java
4. 明确 workspace / worktree 作用域
   - 语言服务会话至少以 `workspacePath + workerId` 为隔离单位
   - `forge` 默认绑定 shadow worktree 会话
5. 在写工具后补上语言服务同步
   - `write_file`
   - `smart_patch`
   - `delete_file`
   - 至少触发一次 `didChange / didSave` 或等价刷新
6. 新增对模型暴露的 typed LSP 工具
   - `lsp_get_diagnostics`
   - `lsp_document_symbols`
   - `lsp_find_references`
   - `lsp_go_to_definition`
   - 可选：`lsp_workspace_symbols`
7. 增加统一降级语义
   - 二进制不存在
   - server 启动失败
   - 初始化超时
   - 协议异常
   - 以上场景回退到 search / semantic diff / build diagnostics，不直接中止 worker
8. 增加遥测与通知字段
   - language
   - provider
   - diagnostics count
   - initialization latency
   - degraded reason

建议落点：

- `CodexFlow.Core/LanguageServices/`
- `CodexFlow.Application/LanguageServices/`
- `CodexFlow/Services/LanguageServices/`

验收标准：

- ✅ 已新增统一的语言服务注册表，能够解析 4 种语言的 provider 能力
- ✅ `Explore / Plan` 可调用只读 LSP typed tools
- ✅ `Forge` 在 shadow worktree 写入后可触发诊断刷新
- ✅ 任一语言服务初始化失败时，worker 主路径仍可继续并进入既定降级路线
- ✅ 已新增 `LanguageServiceRegistryTests`
- ✅ 已新增 `LspToolAdapterTests`
- ✅ 已新增 `ShadowWorktreeLspRefreshTests`
- ✅ 已覆盖：
  - workspace 中不存在对应语言 server
  - 多语言仓库仅部分语言初始化成功
  - 单文件修改后 diagnostics 刷新
  - diagnostics 输出过长时的裁剪与摘要
  - `forge` 与 `verify` 对同一 worktree 的语言服务会话隔离
  - `explore` worker 只读调用不会误触发写侧同步

### Phase 2：Worker 类型系统

> ✅ **已完成**（2026-04-13）
>
> 实现同步：
> - `WorkerType` / `WorkerDefinition` / tool whitelist / isolation mode / output contract 已转为正式运行时配置
> - `Explore / Plan / Forge / Verify` 四种 worker 均可通过注册表实例化

目标：

- 把当前角色 prompt 升级成正式 worker 类型

前提：

- Phase 1.6 已完成基础语言服务接入；`WorkerDefinition` 中的工具白名单需要明确 LSP typed tools 的暴露范围

实施内容：

1. 新增 `WorkerType` 枚举或注册表
   - `Explore`
   - `Plan`
   - `Forge`
   - `Verify`
2. 定义 `WorkerDefinition`
   - system prompt builder
   - allowed tool categories
   - default isolation mode
   - output contract
3. 将当前 prompt 模板映射到 worker
   - Architect prompt 中的探索/规划部分拆给 `explore/plan`
   - Forge 保留为写型 worker
   - Sentry 升级为 verify worker
4. 增加每种 worker 的工具白名单
5. 明确每种 worker 的语言服务权限矩阵
   - `explore / plan`：只读 LSP
   - `forge`：只读 LSP + 写后刷新
   - `verify`：只读 LSP + diagnostics 证据读取

建议边界：

- Worker 类型优先做运行时配置，不先追求插件化

验收标准：

- ✅ worker 类型不再仅体现在 prompt 文本中
- ✅ 同一 runtime 请求可明确知道当前 worker 类型及允许工具面
- ✅ `Explore / Plan / Verify` 三种只读 worker 类型可被实例化
- ✅ 已新增 `WorkerDefinitionRegistryTests`
- ✅ 已验证 `Explore / Plan / Verify` 三种 worker 可通过注册表获取：
  - prompt builder
  - tool whitelist
  - isolation mode
  - output contract

### Phase 3：Coordinator 派工接口

> ✅ **已完成**（2026-04-13）
>
> 实现同步：
> - `spawn_worker / continue_worker / stop_worker` 已注册到主代理工具面
> - completed / waiting-user / cancelled worker 的继续与停止路径均已接通

目标：

- 让主代理能显式启动、继续和停止 worker

实施内容：

1. 新增工具
   - `spawn_worker`
   - `continue_worker`
   - `stop_worker`
   - 可选：`list_workers`
2. `spawn_worker` 参数建议
   - `worker_type`
   - `description`
   - `prompt`
   - `task_id`
   - `run_in_background`
3. `continue_worker`
   - 基于 `jobId` 或 `workerId`
   - 复用上次 checkpoint / context
4. `stop_worker`
   - 进入 cancelled / killed 终态

实现方式建议：

- 直接复用 `BackgroundJobService`
- Worker 作为特化 JobType
- 不单独再造一套本地 task registry

验收标准：

- ✅ 已新增 `CoordinatorSpawnWorkerTests`
- ✅ 单次对话中可并行启动 3 个只读 worker，并全部正常回流
- ✅ 已完成 worker 可以被继续追问
- ✅ 被中止 worker 状态可回流给主代理

### Milestone M1：Phase 0-3 评审门控

这是本蓝图的正式 Go/No-Go 检查点。

> ✅ **已通过**（2026-04-13）

通过条件：

1. Runtime 稳定期观察通过
2. Phase 0.5 的两个 spike 均完成
3. Phase 1.6 的语言服务基线已接通，至少 4 语言注册与降级语义明确
4. 只读 worker 可并行调度并稳定回流
5. 通知格式已定稿
6. 未发现与现有 Orchestrator / Gateway / Shadow Worktree 的结构性冲突

若未通过：

- 暂停 Phase 4-8
- 仅保留已完成的 Phase 0-3 成果
- 必要时回滚到“无 Worker 工具暴露”的状态

### Phase 4：Forge Worker 接入 Shadow Worktree

> ✅ **已完成**（2026-04-13）
>
> 实现同步：
> - forge worker 默认在 shadow worktree 中执行
> - worktree path / changed files / commit hash 已进入 worker 结果与 follow-up payload
> - follow-up forge worker 可复用既有 shadow worktree

目标：

- 让写型 worker 成为真正隔离的执行体

实施内容：

1. `forge` worker 默认创建 shadow worktree
2. 明确主工作区与 shadow worktree 的职责边界
3. 把当前 `ExecuteCodeTaskAsync()` 中已有 shadow 路径逻辑提炼成 worker 可复用能力
4. worker 返回结果中加入：
   - worktree path
   - changed files
   - optional commit hash

注意：

- 第一阶段不以 forge 并行为目标
- 第一阶段的并行价值主要来自：
  - explore
  - plan
  - verify
- forge 第一阶段先安全落地为串行写型 worker
- 若未来需要多 forge 并发，必须追加：
  - 冲突检测
  - 合并策略
  - worktree 级调度器

验收标准：

- ✅ forge worker 的写操作默认不污染主工作区
- ✅ worktree 生命周期可追踪、可清理、可恢复
- ✅ 本阶段仍不承诺多 forge 并发收益
- ✅ 已新增 `BackgroundJobRunnerForgeWorkerIntegrationTests`

### Phase 5：Verify Worker 升级为证据化验证协议

> ✅ **已完成**（2026-04-13）
>
> 实现同步：
> - verify prompt / `verification-report` XML / happy path + adversarial probe 要求均已接通
> - 缺少命令证据、缺少 happy path、缺少 adversarial probe 时都会自动降级 FAIL
> - `ValidationResult` 与 `VerificationReport` 双轨兼容已落地

目标：

- 让验证结果足够可靠，能真正成为 repair loop 的依据

实施内容：

1. 升级 verify prompt
   - 强制命令证据
   - 强制输出观察结果
   - 强制 PASS / FAIL / PARTIAL
2. 新增 `verification-report` XML envelope
3. 验证策略显式要求：
   - happy path
   - 至少一项 adversarial probe
4. 将 `ValidationResult` 与 `VerificationReport` 做双轨兼容
   - 程序内部仍产出 `ValidationResult`
   - 主代理看到的是 XML envelope

验收标准：

- ✅ 已新增 `VerifyWorkerEvidenceTests`
- ✅ 缺少命令证据的验证报告自动判定 FAIL
- ✅ 主代理可直接基于验证报告决定 repair 或 finish

### Phase 6：Task / Checklist / Worker 结果收口

目标：

- 把当前 repair loop 设计与 worker 闭环真正接起来

当前状态：

- ✅ 已完成最小可用闭环
- `ChecklistEvaluation`、orchestrator merge、repair prompt 聚焦 failed/pending、verify worker 结果收口均已落地
- 已补齐 worker、gateway、notification 三条链路的集成测试

实施内容：

1. ✅ 落地 `ChecklistEvaluation`
2. ✅ orchestrator merge checklist
3. ✅ forge repair prompt 仅聚焦 failed / pending items
4. ✅ verify worker 的报告映射到 checklist evidence
   - verify worker 现在会额外产出 `verificationChecklist`
   - `continue_worker` 会优先消费该结构化结果，而不是退回整段 raw result
   - 主会话注入链路保留 `verification-report` XML，不回退为 markdown fallback

这一步与现有文档直接衔接：

- [task-incremental-validation-repair-plan.md](/Users/iwaitu/github/codexflow/docs/archived-blueprints/task-incremental-validation-repair-plan.md)

验收标准：

- ✅ 已新增 repair loop 回归测试
  - `CodexFlow.Core.Tests/Agents/OrchestratorRetryLoopTests.cs`
- ✅ 已验证 repair prompt 仅聚焦 failed / pending items
  - `RetryLoop_ShouldCarryTaskArch003FailuresIntoScopedHighRiskRepairPrompt`
- ✅ 主代理可以根据 worker 证据继续派工
  - `CodexFlow.BackgroundJbobRunner.IntergrationTests/VerifyWorkerEvidenceTests.cs`
  - `CodexFlow.Gateway.IntegrationTests/CoordinatorSpawnWorkerTests.cs`
  - `CodexFlow.Notifications.IntegrationTests/NotificationSubsystemRegressionTests.cs`

已落地说明：

- `DefaultCodexValidator` 已产出 `ChecklistEvaluation`
- `CodexOrchestrator` 已在 validator 返回后 merge checklist，并据此生成 repair prompt
- `BackgroundJobRunner` 已把 `verification-report` 收口为 `verificationChecklist`
- `WorkerCoordinatorService` 已把该结构化 checklist 注入 `continue_worker` follow-up prompt
- `MainSessionInjectionService` 已保证主会话回流优先使用 `workerNotificationXml`

### Phase 7：Gateway / UI / 事件面适配

目标：

- 让前端和网关理解 worker 生命周期

当前状态：

- 🚧 进行中（2026-04-13）
- `JobViewService` / Redis hot view / `OnJobUpdate` 已新增 `WorkerType` 与 `Summary` 投影
- WebUI 已补最小 worker 可视态：运行中 worker、WaitingUser worker、最近完成的 worker
- `GatewayRuntimeEventAdapter` / `GatewaySseEventType` 已补齐 worker 专用 SSE 事件
  - `worker_started`
  - `worker_waiting_user`
  - `worker_completed`
  - `worker_failed`
- WebUI `useGateway` / `Chat` 已开始直接消费专用 worker SSE 事件，并把它们投影到本地过程日志、TaskList 状态与后台作业刷新
- `OnJobUpdate` 仍保留，当前前端采用 `Gateway SSE + OnJobUpdate` 双路径兼容

实施内容：

1. ✅ `GatewayRuntimeEventAdapter` 增加 worker 相关事件
2. ✅ JobView / Redis 投影增加：
   - `WorkerType`
   - `Summary`
   - `WaitingUser`
3. ✅ 前端增加 worker 面板或最小列表态
4. ✅ 支持查看：
   - 正在运行的 worker
   - 最近完成的 worker
   - WaitingUser worker
5. ✅ 前端开始消费 worker 专用 SSE 事件：
   - `worker_started`
   - `worker_waiting_user`
   - `worker_completed`
   - `worker_failed`

验收标准：

- 用户可以看到 worker 级别状态
- 主对话和后台 worker 不再是黑盒关系

当前已落地说明：

- `BackgroundJobDto` 已公开 `WorkerType` / `Summary`
- `OutboxProjector` 已把 worker 元信息写入 Redis hot view，并随 `OnJobUpdate` 一并广播
- `JobViewService` 已把 worker 元信息稳定返回给 `/api/jobs` 查询
- WebUI `BackgroundJobBadge` 已展示 worker 类型与摘要，并新增 `Recent Workers` 最小列表态
- `GatewayRuntimeEventAdapter` 已把 worker notice 映射为专用 SSE 事件，普通 `system_message` 路径保持兼容
- WebUI `useGateway` 已新增 typed worker lifecycle callback；`Chat` 已将专用 SSE 事件映射到过程日志、WaitingUser 提示与任务状态刷新

当前验收证据：

- `CodexFlow.Tests/JobViewServiceTests.cs`
- `CodexFlow.BackgroundJbobRunner.IntergrationTests/ExploreWorkerSpikeTests.cs`
- `CodexFlow.Gateway.IntegrationTests/GatewayRuntimeIntegrationStabilityTests.cs`
- `CodexFlow.WebUI/src/lib/useGateway.ts`
- `CodexFlow.WebUI/src/pages/Chat.tsx`

### Phase 8：恢复与治理

状态：已完成最小可用闭环

补充说明：

- 本节描述的是 `Coordinator/Worker` 主升级计划中 `Phase 8` 已落地的最小闭环能力。
- 在此基础上，`Gateway / QueryRuntime / Worker` 的后端稳定性加固补充项已完成并归档，见：[backend-runtime-stability-hardening-plan.md](../archived-blueprints/backend-runtime-stability-hardening-plan.md)
- 后续若继续推进更广义的恢复语义、观测口径或写路径样本工作，应回到本蓝图和当前活动文档继续推进，而不是继续沿用一份独立的 `Phase 8` 活动补充文档。

目标：

- 补齐 worker 级恢复语义
- 让 worker 卡死、空响应、重复工具调用、租约过期都进入统一可恢复路径

已落地内容：

1. `DefaultQueryRecoveryPolicy` 已真正接入主执行路径
   - `QueryRuntimeEngine` 已在主循环内维护连续工具调用签名
   - `stall detection` 不再是仅有 policy 无执行器状态支撑的死配置
   - `empty response recovery exhausted` 已走统一 `RecoveryExhausted` 终止，而不是错误退化为普通 `NoToolCalls`
2. worker runtime 已开启 stall / empty / malformed / transport recovery 治理
   - `Explore` / `Verify` / `Forge` worker 的 `AdapterHints` 已统一打开 `EnableStallDetection`
   - 既有 `EnableEmptyResponseRecovery` / `EnableMalformedProtocolRecovery` / `EnableTransportFailureRecovery` / `EnableToolDeduplication` 保持开启
3. `RecoveryExhausted` 已升级为 worker 级恢复语义
   - `BackgroundJobRunner` 现在会把 runtime 恢复耗尽投影为 `FailedRecoveryNeeded`
   - 结果负载会带上 `recoveryNeeded` / `canResume` / `resumeStrategy=continue_worker` / `recoveryReason` / `runtimeFlags`
   - 每种恢复原因都会附带结构化 `resumePlaybook` 与 `resumeGuidance`，供主会话、前端和 `continue_worker` 直接消费
   - 会同步写入 `JobRecoveryNeeded` outbox 事件与 worker notification XML，供主会话和后续 `continue_worker` 消费
4. worker resume 策略已最小落地
   - 当前统一恢复入口为 `continue_worker`
   - `host shutdown` 与 `runtime recovery exhausted` 都会走同一条可恢复语义，而不是静默失败
   - `continue_worker` 现在会把 `resumePlaybook` 注入 follow-up prompt，而不是只携带一句失败摘要
5. worker 超时 / orphan 清理已补齐最小治理
   - `BackgroundJobLeaseService.MarkExpiredJobsAsync()` 不再把所有过期运行任务一律回退 `Queued`
   - 过期 worker 现在会标记为 `FailedRecoveryNeeded`，并带 `lease_expired` 恢复原因
   - 非 worker 任务仍保留原先的重新入队语义
6. 可观测性已补齐到 API / Redis / SignalR / WebUI
   - `BackgroundJobDto`、Redis hot view 与 `OnJobUpdate` 现在都会携带 `RecoveryNeeded` / `RecoveryReason` / `ResumeStrategy` / `ResumeGuidance`
   - WebUI 已新增 recovery bar，可直接看到 `FailedRecoveryNeeded` worker 并一键继续
   - worker notification XML 已新增 `<recovery>` 块，保留 reason / strategy / guidance / runtime-flags / steps / checks
7. chaos 回归已开始覆盖真实故障面
   - 已覆盖 `transport_failure` 恢复耗尽
   - 已覆盖 `stall_detected` 恢复耗尽
   - 已覆盖 `lease_expired` orphan / timeout 回收后的恢复投影

当前验收证据：

- `CodexFlow.Core.Tests/Runtime/QueryRuntimeRecoveryTests.cs`
- `CodexFlow.BackgroundJbobRunner.IntergrationTests/ExploreWorkerSpikeTests.cs`
- `CodexFlow.Tests/BackgroundJobLeaseServiceTests.cs`
- `CodexFlow.Tests/JobViewServiceTests.cs`
- `CodexFlow.Gateway.IntegrationTests/CoordinatorSpawnWorkerTests.cs`
- `CodexFlow.Notifications.IntegrationTests/WorkerNotificationProjectorTests.cs`
- `CodexFlow.Core/Runtime/QueryRuntimeEngine.cs`
- `CodexFlow/Services/Background/BackgroundJobRunner.cs`
- `CodexFlow/Services/Background/BackgroundJobLeaseService.cs`
- `CodexFlow/Services/Background/WorkerRecoveryPlaybookFactory.cs`
- `CodexFlow.WebUI/src/pages/Chat.tsx`

验收标准：

- worker 不会因空响应、重复工具调用、网络闪断而长期卡死
- 恢复耗尽、宿主关停、租约过期都会进入统一 `RecoveryNeeded` 语义，而不是静默失败或无限重排队
- 恢复原因必须能投影成主会话、前端和后续 `continue_worker` 都可直接消费的 playbook

---

## 5. 优先级建议

当前主线阶段已全部落地，剩余工作不再是 `Phase 0-8` 主链路补洞，而是围绕恢复质量和自治策略做增强。

其中，后端 API 主路径相关的执行计划已经单独整理为补充文档：

- [backend-runtime-stability-hardening-plan.md](../archived-blueprints/backend-runtime-stability-hardening-plan.md)

建议后续顺序如下：

1. 恢复策略增强
2. 更细粒度的 resume playbook
3. 生命周期可观测性与运维面板
4. 真实 LLM smoke / chaos 验证

原因：

- 主链路已经打通，当前最值钱的是把恢复动作从“能恢复”提升到“恢复得稳定、可观测、可运营”
- `continue_worker` 已经可作为统一恢复入口，下一步应把不同失败原因拆成更明确的恢复剧本
- 真实 provider 波动、长时运行、孤儿任务与多轮 repair 仍需要更强的 chaos/soak 验证

---

## 6. 不建议本轮做的事

以下能力可以参考 Claude Code，但不建议在本轮纳入主计划：

1. DreamTask / AutoDream
2. 主动式 KAIROS 自治模式
3. Buddy / 宠物系统
4. 浏览器自动化作为 worker 默认能力
5. 全量插件化 worker marketplace
6. 全面替换内部 JSON/DTO 为 XML

这些方向要么是体验增强，要么是后期自治能力，不是当前最短板。

---

## 7. 回滚策略

本蓝图必须有明确回滚方案。

### 7.1 回滚边界

以下能力必须可以通过 feature flag、配置或工具注册开关关闭：

1. `spawn_worker / continue_worker / stop_worker`
2. worker 类型系统对外暴露
3. LLM-facing 通知注入
4. LSP typed tools 与语言服务注入

建议的落点如下：

1. 工具注册开关
   - 落在 `ToolRegistryBootstrapper` 或对应 Tool Registry 注册入口
   - 控制 `spawn_worker / continue_worker / stop_worker` 是否注册
2. 通知注入开关
   - 落在 `appsettings` / `IOptions` / 环境变量
   - 控制 LLM-facing envelope 是否注入主对话
3. worker 类型暴露开关
   - 落在 `WorkerDefinitionRegistry` 或对应定义加载器
   - 控制 worker 类型是否对主代理可见
4. 语言服务开关
   - 落在 `LanguageServiceRegistry` / `ToolRegistryBootstrapper` / `appsettings`
   - 控制 LSP typed tools 是否注册，以及是否启用 worktree 级语言服务会话

### 7.2 最小回滚目标

如果 Phase 0-3 后评审失败，系统必须能够退回到：

```text
统一 runtime + 现有单主代理 + execute_code_task
```

也就是说：

- 不影响既有 QueryRuntimeEngine 主路径
- 不影响现有 BackgroundJob 基础设施
- 只禁用新暴露的 worker 协议面

### 7.3 稳定期要求

在 Milestone M1 通过前，建议至少满足以下观察条件之一：

1. 真实任务 20+ 个
2. 或连续 1 周稳定运行

任何结构性异常都应暂停 Phase 4+。

---

## 8. 最终目标态

完成本蓝图后，CodexFlow 的目标形态应为：

- 主代理是 Coordinator，不再承担所有细节执行
- Worker 是正式运行时对象，而不是“换个 prompt 再跑一次”
- 后台 Job、Runtime、Gateway、Task Repair 使用统一的通知协议
- 多语言 Language Service / LSP 能力成为正式 runtime 组成，而不是孤立 skill 资产
- 写型 worker 默认 worktree 隔离
- 验证型 worker 默认给证据，而不是只给结论
- Checklist / Validation / Repair 与 worker 执行链真正打通

最终系统会从：

```text
统一 runtime + 单主代理 + 原子代码任务执行器
```

升级为：

```text
统一 runtime + Coordinator/Worker 协议化执行系统 + 多语言语言服务层 + 证据化验证闭环
```

这一步是 CodexFlow 从“能执行任务”走向“能稳定编排复杂任务”的关键升级。
