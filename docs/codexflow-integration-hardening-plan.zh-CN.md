# CodexFlow 集成前 QRE 改进计划

日期：2026-06-05

## 1. 背景

CodexFlow 已尝试通过 `CodexFlow.QueryRuntime.Engine` NuGet 包新增
`QreBackedQueryRuntimeEngine`，让 `CodexFlow.Core.Runtime.IQueryRuntimeEngine`
可以选择由独立 QRE engine 执行。

这次集成验证说明：QRE 的核心 query/tool loop、模型流、`AIFunction` 工具执行和
基础事件映射已经可以被 CodexFlow 调用；但 Antigravity 对消费侧 adapter 的安全审查
指出，当前 QRE package contract 还不足以安全替换 CodexFlow 内置
`QueryRuntimeEngine`。主要缺口不是 QRE 是否能运行，而是 QRE 是否能承载
CodexFlow Core 既有的安全边界、验证门和平台语义。

结论：在继续扩大 CodexFlow 集成前，应先在本仓库补强 host-facing contract 与安全
扩展点。CodexFlow 侧 adapter 应保持 fail-closed 或 fallback-to-core，直到下列能力
在 QRE package 中有明确契约和测试覆盖。

## 2. 目标

- 让 QRE package 成为 CodexFlow 可安全消费的 runtime backend，而不是只具备
  standalone CLI/harness 能力。
- 保持本仓库独立，不引入 `CodexFlow.Core` 或 `CodexFlow.Contracts` 依赖。
- 用 host-neutral contract 表达 CodexFlow 需要的 hook、stop gate、tool policy、
  trace path 和结果元数据能力。
- 让 downstream adapter 可以证明没有绕过现有 Core guardrails。

## 3. 非目标

- 不把 CodexFlow 专有类型直接搬进 QRE。
- 不在 QRE 仓库引用 CodexFlow Core 项目或复制整个旧 `QueryRuntimeEngine`。
- 不要求第一阶段完全复刻 CodexFlow 当前所有 recovery heuristics。
- 不把 NuGet 包集成当作 native binary 集成的替代方案；两条路径都应保留清晰边界。

## 4. Antigravity 发现映射

### 4.1 Intervention hook 绕过

CodexFlow 旧 runtime 会在工具调用前后通过 intervention hook 执行安全控制，例如
task file scope、guardrail 和 critique。当前 QRE package engine 直接执行
`AIFunction.InvokeAsync()`，host 无法在执行前阻断危险工具调用。

需要 QRE 提供 host-neutral 的工具干预 contract，让消费方可以：

- 在工具调用前检查 tool name、call id、arguments、round、available tools。
- 返回 allow / block / fail-closed 决策，以及给模型的策略反馈。
- 在工具结果后执行 critique 或审计。
- 将 block 决策写入 trace 和 runtime events。

第一版不支持在 pre-tool hook 中静默改写工具参数。若后续确实需要参数改写，必须
引入单独的 typed rewrite decision，并要求 schema validation、argument audit event
和测试覆盖。当前计划中的 feedback 仅表示“不执行工具并向模型返回策略反馈”。

### 4.2 Stop hook / verification gate 绕过

CodexFlow 旧 runtime 的 stop hooks 会阻止不完整验证、未满足 required-tool contract
以及伪装性安全修复。当前 QRE package engine 只根据 no-tool-call 或 max-rounds 结束，
host 无法在停止前要求追加验证轮。

需要 QRE 提供 before-stop contract，让消费方可以：

- 检查最终 assistant text、已执行工具、成功工具、工具结果摘要和证据摘要。
- 要求继续一轮，并指定 required tool 或追加 recovery prompt。
- 在达到最大 continuation 后 fail closed，并给出 terminal detail code。

### 4.3 NuGet source / package provenance

CodexFlow 消费本地 QRE NuGet 包时，如果没有 package source mapping，存在 dependency
confusion 风险。该问题主要在消费仓库修复，但 QRE release 侧也应提供明确的包源和
校验建议。

需要 QRE 文档和 release artifact 明确：

- package id、version、hash/checksum。
- 推荐 `packageSourceMapping` 示例。
- 本地开发 feed 与正式 feed 的区别。
- 不允许从未授权公共源解析内部 package。

### 4.4 Trace / workspace path 边界

当前 package engine 接收 `traceFilePath` 并返回结果；未来一旦 package 层写 trace，
必须保证 path 已规范化并位于授权 workspace / trace root 内。

需要 QRE contract 明确：

- `WorkspacePath` 和 `TraceRoot` 必须是 host 提供并规范化后的绝对路径。
- QRE 写入前必须执行 root containment check。
- root containment 不能只依赖 `Path.GetFullPath` 和字符串前缀比较；必须处理目录
  symlink / junction / reparse point。可复用或迁移 `ExperimentalWorkspacePath` 的
  segment-by-segment 检查思想到 Engine 可用层，或者使用平台可用的 link target
  resolution 验证物理路径不逃逸。
- 拒绝 `.git`、secret-looking paths、symlink escape 和 workspace escape。

### 4.5 测试缺口

当前 CodexFlow 侧 focused test 只证明 adapter 能跑通基础工具调用，没有证明安全 hook
和 stop gate 生效。QRE 本仓库应先提供对应测试夹具，使 downstream 可以复用同样的
contract 语义做集成测试。

## 5. 改进阶段

### H0：定义 host-facing 安全 contract

目标：在 `CodexFlow.QueryRuntime.Abstractions` 中补齐 host 可实现的安全扩展点。

范围：

- 新增工具调用干预 contract，例如：
  - `IQueryRuntimeToolIntervention`
  - `QueryRuntimeToolCallContext`
  - `QueryRuntimeToolInterventionDecision`
  - `QueryRuntimeToolExecutionResultContext`
- 新增停止前决策 contract，例如：
  - `IQueryRuntimeStopGate`
  - `QueryRuntimeBeforeStopContext`
  - `QueryRuntimeStopDecision`
- 新增 host request 字段，允许传入上述 hook。
- contract 只能依赖 QRE 自有 DTO、`Microsoft.Extensions.AI` 和 BCL 类型。
- 第一版 decision set 限定为 allow、block-with-feedback、fail-closed；不允许 pre-tool
  hook 改写参数后继续执行。
- 文档说明这些 contract 与 CodexFlow Core hooks 的映射关系，但不引用 Core 类型。

验收：

- `CodexFlow.QueryRuntime.Abstractions` 不引用 `CodexFlow.Core` 或
  `CodexFlow.Contracts`。
- 新 contract 有 XML docs 和 `docs/IQueryRuntimeEngine*.md` 说明。
- unit tests 验证 allow/block/continue/fail closed 决策结构可序列化或可记录。

### H1：在 engine loop 中执行工具干预

目标：工具执行前后，QRE engine 必须允许 host 检查和阻断。

范围：

- 在 `FunctionCallContent` 被执行前调用 pre-tool intervention。
- block 决策应：
  - 不调用底层 `AIFunction`。
  - 生成 tool-result message，告诉模型该调用被策略阻断。
  - 写入 trace/event，包含 reason、tool name、call id 和 round。
- fail-closed 决策应立即终止 run，并返回明确 terminal reason/detail code。
- after-tool hook 应能观察 success、result length、result summary、exception 类型。
- block 与 after-tool 失败时应 fail closed 或显式记录为 hook failure，不能静默忽略。

验收：

- 测试证明 blocked tool 不会执行。
- 测试证明 block reason 会进入下一轮模型上下文。
- 测试证明 after-tool hook 能看到成功和失败结果。
- 测试覆盖并发/多工具调用时每个 call 都独立触发 hook。

### H2：在 engine loop 中执行 stop gate

目标：QRE 不能只因 no-tool-call 就立即结束；host 必须能要求继续验证或 fail closed。

范围：

- 在 no-tool-call terminal candidate 之前调用 stop gate。
- stop gate 可以返回：
  - accept：允许结束。
  - continue：追加 host feedback 并进入下一轮。
  - require tool：下一轮限制或提示指定工具。
  - fail：以明确 terminal reason/detail 结束。
- 增加 continuation attempt 计数，防止无限循环。
- stop gate 决策写入 trace/event。
- 如果 stop gate 返回 continue / require tool，但当前 run 已达到 `MaxRounds` 或无法再
  继续，QRE 必须以未验证失败结束，例如 `TerminalDetailCode=verification_incomplete`
  或 `verification_timed_out`，不能把该 run 标记为普通成功。

验收：

- 测试证明 stop gate 可阻止未验证最终回答。
- 测试证明 required tool 未执行时会继续一轮。
- 测试证明超过最大 continuation 后 fail closed。
- 测试证明 stop gate 不会突破 `MaxRounds` 或 cancellation；达到 `MaxRounds` 且未被
  stop gate accept 的 run 必须返回未验证失败 detail code。

### H3：扩展结果与事件元数据

目标：downstream adapter 能从 QRE result 中得到足够信息来维持 CodexFlow 行为。

范围：

- 在 result 中暴露：
  - `WriteToolCalls`
  - `ZeroToolCallRounds`
  - `RecoveryCount` 或 `ContinuationCount`
  - `LastFunctionCall`
  - `TerminalDetailCode`
  - `FinalMessages`
  - `TraceFilePath` / `RunDirectory`
- 事件中补齐：
  - policy/intervention decision event
  - stop gate decision event
  - required-tool state
  - prompt assembly summary
- 保持 CLI JSON 输出稳定，新增字段应向后兼容。

验收：

- unit tests 验证 result metadata 与执行过程一致。
- trace replay 对新增事件保持兼容。
- `qre run --json` 仍输出旧消费者可接受的字段。

### H4：Trace path 与 workspace containment

目标：QRE package 层一旦负责写 artifacts，必须具备与 CLI 一致的路径安全边界。

范围：

- 对 `WorkspacePath`、`TraceRoot`、`RunDirectory` 执行 `Path.GetFullPath`。
- 强制 run artifacts 位于授权 trace root 下。
- 对每个路径段执行 symlink / junction / reparse point 检查，或解析 link target 后再
  做 physical containment；不能接受只做字符串前缀匹配的实现。
- 将可复用的 workspace path containment 逻辑放在 Engine 或 Abstractions 可用的位置，
  不让 package 层依赖 Experimental-only helper。
- 拒绝 workspace escape、symlink escape、`.git`、`.qre` 自身受保护路径和
  secret-looking paths 的写入。
- 明确 host 可以选择只传入 logical trace path，由 host 自己写 trace。

验收：

- negative tests 覆盖 `../`、绝对路径逃逸、symlink、`.git`、secret-looking path。
- negative tests 必须包含“workspace 内路径段是 symlink 且目标在 workspace 外”的
  escape case。
- Windows/macOS/Linux path 分隔符均有测试或明确限制。
- docs 更新 trace/artifact ownership 模式。

### H5：Package source 与 release provenance 文档

目标：让 CodexFlow 消费 QRE NuGet 包时有明确的安全配置模板。

范围：

- 新增 docs 示例：
  - local feed development `NuGet.config`
  - production feed `NuGet.config`
  - `packageSourceMapping`
  - checksum/hash 校验流程
- release notes 中记录 package id、version、commit、artifact hash。
- 如果未来发布到公共 feed，明确 package signing 或 provenance 策略。

验收：

- docs 中有可复制的 `NuGet.config` 安全模板。
- CodexFlow adapter 文档要求 source mapping。
- release workflow 产出 checksum metadata。

### H6：Downstream CodexFlow adapter contract test kit

目标：为 CodexFlow 提供可复用的集成测试夹具，避免只测 happy path。

范围：

- 提供 test helper 或 sample，验证：
  - pre-tool hook 能阻断写工具。
  - stop gate 能阻止未验证结束。
  - required tool contract 能触发 continuation。
  - result metadata 正确映射。
  - trace path 不越界。
- 在 `examples/` 或 `CodexFlow.QueryRuntime.UnitTests` 中保留 host integration
  示例，不引用 CodexFlow Core。
- test kit 中的 examples / fixtures 必须进入 GitHub Actions 或等价 CI validation，
  不能只作为未编译文档样例存在。

当前落点：

- `CodexFlow.QueryRuntime.UnitTests/Contracts/HostAdapterContractTestKit.cs`
  提供可复用 contract assertion suite。
- `HostAdapterContractTestKitTests` 用 `ExperimentalQueryRuntimeHarness` 执行该
  suite，覆盖 pre-tool block、stop-gate continuation、required tool、
  result metadata 和 trace path containment。

验收：

- QRE 本仓库测试覆盖 host hook contract。
- CodexFlow 消费仓库可用这些测试语义实现 adapter tests。
- test kit 在 CI 中编译或执行，避免 sample drift。
- Antigravity 复核不再报告 hook/stop gate 绕过为 blocker。

## 6. CodexFlow 侧临时策略

在 H0-H6 完成前，CodexFlow 侧不应默认启用 `Backend=qre`。

建议消费侧策略：

- 默认继续使用 `Backend=core`。
- `Backend=qre` 仅允许在明确实验配置下启用。
- 如果请求包含 `InterventionHook`、`RequiredToolContract`、
  `WorkerContext.RequiredToolContract`、`DynamicContextProvider` 等当前 QRE
  未完整支持的语义，adapter 必须 fail closed 或 fallback 到 Core backend。
- 不允许只打 warning 后继续执行高风险请求。
- NuGet source 必须启用 package source mapping。

## 7. 推荐执行顺序

1. H0：先冻结 host-facing contract，不急于实现所有旧 runtime 行为。
2. H1 + H2：优先补齐安全阻断能力和停止前验证能力。
3. H3：补齐 downstream 需要的 result/event 元数据。
4. H4：补齐 trace/artifact path containment。
5. H5：发布和包源安全文档。
6. H6：提供 downstream adapter test kit。
7. 让 Antigravity 对本仓库和 CodexFlow adapter 同时复核。
8. 再回到 CodexFlow，把 `Backend=qre` 从实验路径推进到可选生产路径。

## 8. 最小完成标准

只有当以下条件满足时，才建议继续推进 CodexFlow 侧整合：

- QRE package 支持 pre-tool intervention，并且 blocked tool 不会执行。
- QRE package 支持 before-stop gate，并能要求 continuation 或 fail closed。
- QRE result 暴露 enough metadata 供 CodexFlow 保持现有行为。
- trace/artifact path 具备 root containment。
- package source mapping 文档完成。
- QRE 本仓库测试通过：

```bash
git diff --check
dotnet test CodexFlow.QueryRuntime.slnx --no-restore
rg -n "CodexFlow\\.(Core|Contracts)" --glob "*.cs" --glob "*.csproj" --glob "*.slnx"
```

- CodexFlow adapter 测试覆盖 hook、stop gate、required tool 和 package source
  mapping。
- Antigravity 复核不再报告 Critical / High blocker。
