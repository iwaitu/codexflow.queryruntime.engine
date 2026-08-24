# QueryRuntime vNext Core Parity 执行计划

状态：C0–C7 完成；经 owner 明确豁免观察门禁后已切换为 v2-only 执行
日期：2026-08-24  
版本目标：`0.2.0-preview.*`

## 治理关系

本文是 Runtime 仓库内的路线二执行入口。总体范围、路线边界和 Core Parity Gate 以
CodexFlow 仓库的 `codex-rs-architecture-refactor-work-plan.zh-CN.md` 1.2 为批准来源；当前
pre-release 安全策略继续约束 v1 和 v2，不被本文替换。

路线二只交付本地、单进程、可嵌入、provider-neutral、可审计和可安全 replay 的核心执行层。
crash resume、分布式 ownership、跨崩溃 exactly-once、生产 shadow tape 和 full-fidelity 敏感
replay 属于条件路线三，不得提前进入核心。

## 阶段与状态

| 阶段 | 交付 | 状态 | 进入门禁 |
| --- | --- | --- | --- |
| C0 | ADR、现状基线、API/CLI/trace/package 冻结 | 完成 | 路线一仓库侧 PASS |
| C1 | 最小 Protocol、typed IDs、Runtime IR、Model 协议、依赖防火墙 | 完成 | C0 ADR 生效 |
| C2 | Session/Turn/Step、不可变 snapshot、小型 phase loop | 完成 | C1 contract tests |
| C3 | CLI static/recorded 与 CodexFlow readonly 纵向切片 | 完成 | C2 最小 loop |
| C4 | ToolRegistry、Router、Policy、Approval、Sandbox | 完成 | C3 contract kit |
| C5 | RuntimeHistory、Context、确定性截断和基础 compaction | 完成 | C4 tool lifecycle |
| C6 | versioned audit、recorded replay、数据分层和配额 | 完成 | C4/C5 稳定边界 |
| C7 | 灰度、清理、`0.2.0-preview.*` | 完成；ADR-007 记录门禁豁免与 v2-only 切换 | Core Parity Gate + owner exception |

## C2 完成证据

- `RuntimeAgentLoop` 已以 provider-neutral 协议完成 model → tool → continuation → terminal 的小型
  phase loop；Session/Turn/Step、usage、required-tool、continuation、stop reason 和工具生命周期均由
  显式 state/reducer 管理。
- 支持纯文本、reasoning/text 分离、mixed text/tool、多工具顺序提交、required-tool、max-output
  continuation、受预算约束的有限模型重试，以及 model/approval/tool 全链路取消。
- Step 冻结 model request、tool catalog、policy、environment、budget 和 history version；调用方后续修改
  集合不会改变当前 Step 的执行目录。
- provisional handle 仅提供 approval、steer 和 cancel；未公开 resume、durable ownership 或多进程语义。
- 空响应、malformed stream、未知工具、拒绝、异常、无效 hook 返回和非法状态转换均 fail closed，并为
  工具失败生成显式 observation。
- 最新 VllmChatClient 的 `ReasoningChatResponseUpdate` 与 `UsageChatResponseUpdate` 已在 MEAI adapter
  映射为独立 reasoning/usage 事件；真实 qwen3.8 vLLM E2E 验证最终文本不再混入思考内容。
- Linux Release 全量 unit/security regression 为 336/336；Windows solution build 0 warning/0 error；
  Linux/Windows Native AOT、Linux 原生 smoke、依赖漏洞/许可证门禁及 NuGet clean-consumer smoke 均通过。

C2 只完成核心 loop，不代表路线二整体完成。CLI/CodexFlow v2 垂直切片、完整 ToolRegistry/Policy/
Sandbox 闭环、Context/Compaction 与 versioned audit/replay 仍分别属于 C3–C6。

## C3 完成证据

- 新增 `IAgentRuntime.RunAsync` Hosting facade、`RuntimeRunRequest`、`RuntimeTurnResult` 和有序的
  ephemeral presentation events；事件明确区分 reasoning、answer、tool、usage、warning 和 Turn 终态，
  不提前承诺 C6 durable audit/cursor 语义。
- CLI 增加显式 `--runtime v2` preview 路径，static 和真实 OpenAI-compatible provider 均走 Hosting
  facade → C2 loop；默认仍为 v1，C3 CLI 切片只允许 `--profile none`，工具 CLI 迁移留在 C4。
- CodexFlow 新增 `Backend=qre-v2` DI 分支，现有 `core` 与 `qre` 路径保持不变；v2 切片只接受零工具或
  一个只读工具，写工具、多工具、动态工具目录和动态 context 在模型调用前 fail closed。
- CodexFlow adapter 已映射 pre-tool intervention、tool result critique、required-tool、before-stop
  continuation、answer/reasoning streaming、usage、终止原因、轮数、工具数和 prompt snapshot metadata。
- CodexFlow contract tests 覆盖只读工具、intervention、required-tool、stop gate、streaming、metadata 和
  写工具拒绝；Core 全量回归 964/964 通过。
- 真实 qwen3.8 CLI v2 E2E 最终文本为 `C3_CLI_V2_OK`，usage 为 input 61 / output 45 / total 106，
  reasoning 未混入 final text。
- Runtime Linux 全量 unit/security regression 为 343/343；Windows solution build 0 warning/0 error；
  Windows/Linux Native AOT、包含 v2 路径的原生 smoke、依赖门禁和 NuGet `0.2.0-preview.3`
  clean-consumer smoke 均通过。

C3 证明 v2 最小协议可被 CLI 和真实 CodexFlow 宿主消费；C4 已在此基础上交付正式工具闭环。

## C4 完成证据

- Engine 新增冻结的 `RuntimeToolRegistry`、canonical name/SemVer/case-insensitive collision 校验、
  JSON schema 参数验证与规范化摘要，以及不按工具名分支的 `RuntimeToolRouter`。
- 工具生命周期固定为 normalize → route → policy/plan → plan-bound approval → sandbox → tool →
  structured observation。`ResolvedExecutionPlan` 绑定 attempt、工具版本、参数摘要、workspace、policy、
  capabilities、sandbox、limits、concurrency 与 approval nonce/expiry；重新评估会产生新 attempt 和 nonce。
- 正式 C4 管线只接受获得完整冻结计划的 `IRuntimeToolApproval`。仅按 invocation ID 的 provisional
  `RuntimeTurnHandle` 预批准不再能授权 C4 执行，避免在参数、策略或 sandbox 变化后复用旧批准。
- `RuntimeToolScheduler` 支持 `Serial`、`ParallelSafe`、`ExclusiveWorkspace`；并行执行的 observation、
  history 与 presentation event 仍严格按模型调用顺序提交，同 workspace 写工具可跨并发 Turn 互斥。
- LocalProcess/Docker sandbox 已通过显式 router 选择；timeout、UTF-8 output cap、truncation、exit code、
  stdout/stderr、duration、outcome、retryable 与 workspace-change evidence 进入结构化工具结果。
- CLI v2 已迁移 none/readonly/verify/repair 和 external stdio 工具；readonly/verify/repair 延续现有 hardened
  tool pack，repair 与高风险操作要求绑定审批；当时将 `--tool-search` 明确留给 C5，没有假装已支持动态目录。
- CodexFlow `Backend=qre-v2` 适配器已迁移多工具、只读/写工具、intervention、required-tool、stop gate、
  usage 与结构化结果；写工具无 intervention 时 fail closed，有 intervention 时审批绑定冻结计划。
- MEAI/Vllm adapter 将 frozen descriptors 投影为真实 model tool declarations，并在 required-tool 时设置
  required mode；finish reason 后的 trailing usage/warning 被安全缓冲，finish 后内容和冲突 finish fail closed。
- Runtime Linux 全量 unit/security regression 367/367、Windows Release build 0 warning/0 error、
  CodexFlow Core 967/967、CodexFlow v2 adapter 9/9、Windows/Linux Native AOT、Linux 原生 smoke、依赖
  漏洞/许可证门禁和 NuGet `0.2.0-preview.7` clean-consumer smoke 全部通过。
- 真实 qwen3.8 vLLM C4 E2E 强制 `qre_list_files`：2 Step、1 次真实工具调用，observation 回灌后最终文本
  为 `C4_TOOL_E2E_OK`；usage 为 input 1194 / output 297 / total 1491，reasoning 未进入 final text。

C4 完成的是本地单进程工具安全执行闭环，不等于路线二整体完成。RuntimeHistory、确定性 context/
compaction 已在 C5 完成；versioned audit 与安全 recorded replay 收口属于 C6，默认切换与清理属于 C7。

## C5 完成证据

- Engine 新增 Runtime-owned `RuntimeHistory`：消息/条目具有稳定 ID，历史版本只在 Step 提交边界单调递增；
  context preparation 只生成模型投影，不改写 canonical history。
- append 边界统一处理重复 system、重复/孤立 tool call/result、空项和未知 item；tool call/result 在选择时
  原子保留配对关系，避免模型看到无法解释的半条轨迹。
- 单项文本、reasoning、工具参数和工具结果均有 hard cap。大型工具输出转为带 SHA-256、长度和
  `runtime-history://sha256/...` 引用的 bounded observation，原文保存在受单项/总量配额约束的内存 blob store。
- `RuntimeContextManager` 使用版本化 `utf8-bytes-div4-v2` 估算器，预算同时覆盖消息和本 Step 暴露的
  tool schema；目录本身耗尽预算时在采样前以 `tool_catalog_context_budget_exhausted` fail closed。
- 超预算时执行确定性本地选择与非权威 summary，显式记录 included/omitted/replaced item、分区用量、
  estimator 和 compaction event；3/10/25 Step 数据证明无需在 C5 引入额外 model compactor。
- deferred tool search 使用冻结执行全集与逐 Step 可见子集：首 Step 只暴露 `tool_search`，选择结果从下一
  Step 生效；注册但未暴露的工具即使存在于执行 registry，也会生成 `tool_not_exposed_in_context` observation。
- CLI `--runtime v2 --tool-search`、Hosting facade 和 CodexFlow `Backend=qre-v2` 已贯通。CodexFlow 复用现有
  `ContextCompressionTriggerOptions` 映射预算，dynamic context 为逐 Step ephemeral 注入；动态工具提供器因
  无冻结执行全集仍在模型调用前 fail closed。
- Runtime Linux Release 全量回归 384/384、C5/tool/presentation/baseline 定向 44/44、CodexFlow Core
  970/970、CodexFlow v2 adapter 12/12 通过；Windows Release solution build 0 warning/0 error。
- Windows/Linux Native AOT 均无新增 trim/AOT warning，Linux 原生 C5/tool-search smoke、依赖漏洞/许可证
  门禁及 NuGet `0.2.0-preview.10` checksum/content/clean-consumer smoke 均通过。
- 真实 qwen3.8 vLLM C5 E2E 完成 `tool_search` → `qre_list_files` → 最终答案：3 Step、2 次工具调用，
  final text 为 `C5_TOOL_SEARCH_E2E_OK`，usage 为 input 1950 / output 291 / total 2241，reasoning 未混入答案。

C5 完成 Runtime-owned history 与 bounded model context，但 C5 event 仍是进程内审计候选，不宣称 C6 的
durable/versioned audit、数据分层、配额持久化或安全 recorded replay 语义。

## C6 完成证据

- Engine 新增 schema v1 的 `RuntimeAuditEnvelope`，稳定记录 sequence、event/session/turn/step/invocation
  ID、timestamp、kind、causation/correlation、sensitivity 与 typed payload/blob reference。Agent Loop 在
  Turn start、context prepared、model request/response commit、每个 tool observation commit 和 terminal
  边界产生日志；presentation/telemetry 仍是独立事件面。
- `RuntimeJsonlAuditStore` 默认写 `PublicRedacted / SummaryOnly` allow-list projection，不保存 prompt、
  model/reasoning 正文、工具名/参数/结果、路径或宿主 ID。只有显式 `SanitizedFixture` 或 owner-only
  `PrivateDiagnostic` 具备 `Recorded` 能力；private 目录延续 Windows ACL 与 Unix `0700/0600`。
- JSONL、manifest 与 content-addressed SHA-256 blob 均受事件数、行长、JSON depth、单 blob、总 blob、
  单 run、全部 runs、保留期和 run 数硬配额约束。读路径校验 containment、reparse、manifest/file 长度、
  schema/metadata、digest、blob length 与总量；失败 run 可观察且可被 retention GC 回收。
- `RuntimeRecordedReplay` 是纯数据验证 reducer，没有 provider/tool 入口；校验 schema/version、连续 sequence、
  causation/correlation、kind/payload/ID 形状、model request/response、工具 observation 顺序与身份、终态
  step/tool/text/usage/history 一致性，并输出稳定 SHA-256 `replayDigest`。public summary 和损坏/未来版本均
  fail closed。
- CLI v2 默认持久化公开脱敏审计；`--trace-data sanitized|private` 显式启用可回放数据，
  `qre replay latest --runtime v2 [--summary|--strict]` 完成 inspect/data-only replay。v1 trace reader 和默认
  v1 CLI 行为保持不变。CodexFlow `Backend=qre-v2` 通过可注入 `IRuntimeAuditSink` 消费同一 C6 envelope，
  未把持久化或 reducer 逻辑复制进宿主。
- Runtime Linux Release 全量回归 397/397；C6/CLI 定向 17/17；CodexFlow Core 971/971、v2 adapter
  13/13；Runtime Windows Release build 0 warning/0 error，CodexFlow solution build 0 error（既有 warning）。
- Windows/Linux Native AOT 均无新增 trim/AOT warning；Linux 原生 smoke 覆盖 v1 replay 和 C6 v2
  sanitized recorded replay；依赖漏洞/许可证门禁及 NuGet `0.2.0-preview.14` checksum/content/
  clean-consumer smoke 全部通过。
- 真实 qwen3.8 vLLM C6 E2E：2 Step、1 次 `qre_list_files` 工具执行、9 个 audit events，最终文本为
  `C6_AUDIT_REPLAY_E2E_OK`，usage 1154/312/1466；随后两次严格 recorded replay 均报告
  `providerCalls=false`、`toolExecutions=false`，digest 相同。

C6 完成了本地单进程 durable audit 与安全 recorded replay，不承诺 crash resume、跨进程并发写、
exactly-once、加密 full-fidelity tape 或生产 shadow；这些仍受路线三触发条件约束。默认切换与重复 loop
清理只在 C7 Core Parity Gate 后进行。

## C7 代码完成证据

- Engine 新增 `RuntimeCoreParityGate` 与统一 projection。policy decision、tool order、归一化 terminal reason、
  side-effect count 始终零容忍；final text 使用 Exact、NormalizedWhitespace 或 Ignore 的独立比较，文本容差
  不能覆盖执行语义差异。
- CodexFlow contract kit 对真实 v1/v2 adapter 运行 readonly、verify、获批 repair 和被拒 repair fixture，
  四类轨迹的 policy、工具顺序、终态与副作用计数完全一致；CLI v2 增补 verify/repair frozen capability 覆盖。
- CLI 将 v2 JSON output、replay human presentation 与 stream sink 从 4000 多行 `Program.cs` 拆到独立呈现文件；
  没有为拆分新增 package，也没有复制 Agent Loop。
- CodexFlow backend 继续显式支持 `core`、`qre`、`qre-v2`，非法 backend 和非正 model timeout 通过
  `ValidateOnStart` 启动失败，不再静默回落。in-flight Turn 不跨 backend 恢复。
- 新增中英文 `0.2` preview 迁移指南；本地 `0.2.0-preview.17` package 通过 checksum、内容和 clean consumer，
  旧 `0.1.2` 未被重发或修改。
- Runtime Linux unit/security regression 410/410、CodexFlow Core 975/975、Runtime Windows Release build
  0 warning/0 error、CodexFlow solution 0 error、Windows/Linux Native AOT 和原生 v2 audit/replay smoke、
  dependency vulnerability/license gate 均通过。
- 真实 qwen3.8 vLLM verify E2E 强制 `qre_git_status`：2 Step、1 次工具执行、9 个 audit events，usage
  3003/145/3148，最终文本经单独 whitespace 容差为 `C7_VERIFY_E2E_OK`；两次 data-only strict replay
  均为 provider/tool false，digest 相同。
- C7 修复后使用最终 Windows Native AOT 二进制重新执行真实 qwen3.8 vLLM 门禁矩阵：v1/v2 verify 与
  获批 repair 均为 2 个 phase、1 次工具执行并成功结束；拒绝 repair 均以 exit 1 fail-closed，工具执行和
  workspace 写入均为 0。repair 文件为 UTF-8 无 BOM；verify 的非零子进程退出会进入工具失败，而不是伪成功。
- v2 verify、repair、deny 各执行两次 strict replay：digest 分别稳定一致，且均为
  `providerCalls=false`、`toolExecutions=false`；deny 轨迹允许保留拒绝 observation，但执行计数保持 0。

2026-08-24，owner 明确要求跳过“两次正式 preview＋观察窗口”运营门禁并立即完成 v2 切换，ADR-007 记录
该风险接受。CLI run/trace/replay/rerun 与 CodexFlow 宿主生产入口现均使用 v2；`--runtime v1` 不再执行，
CodexFlow 的 `core`/`qre` backend 配置会启动失败。v1 public types 和只读 trace summary reader 暂留用于源码
迁移与历史诊断，但不存在生产调度回退。回滚必须部署上一应用/package 版本，不能在进程内切换 backend。
最终 Windows Native AOT 在不传 `--runtime v2` 时完成真实 qwen3.8 verify：2 Step、1 次 `qre_git_status`、
最终标记 `V2_CUTOVER_LIVE_OK`；同一 audit 两次 strict replay digest 均为
`f9fb546fd8017ecd7e6b0e440da2120abcdc7c09f42754bcf79d40d955eb122a`，provider/tool execution 均为 false。

## C0 可运行门禁

责任方：Runtime maintainers。样本固定为纯文本、单工具、多工具、required-tool、stop-gate、
malformed stream、3/10/25-step scripted trajectory。

```powershell
dotnet build CodexFlow.QueryRuntime.slnx -c Release
dotnet test CodexFlow.QueryRuntime.UnitTests/CodexFlow.QueryRuntime.UnitTests.csproj -c Release
dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj -c Release
dotnet publish CodexFlow.QueryRuntime.Cli/CodexFlow.QueryRuntime.Cli.csproj -c Release -r win-x64 -p:PublishAot=true -p:SelfContained=true
```

C0 退出条件：ADR-001 至 ADR-006 为 Accepted；现有 public API、CLI JSON、trace v1、NuGet surface
有明确冻结方式；3/10/25-step 基线可重复运行并记录环境；C1 依赖防火墙有可执行测试入口。

## 变更规则

- v1 继续位于现有 Abstractions/Engine/Experimental 路径，直到 C7 明确移除重复 loop。
- v2 从 provider-free Protocol 开始，以 compatibility adapter 连接 MEAI；不在 Protocol 中引用 MEAI。
- 每个阶段都必须继续运行路线一 security negative tests。
- 路线二允许 `0.2.0-preview.*` source breaking change；ADR-007 后必须提供迁移说明、历史只读 trace 与部署级回滚。
- 任何路线三能力必须先新增独立 ADR、owner、预算、威胁模型和触发证据。
