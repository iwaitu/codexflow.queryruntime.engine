# 基于重放的候选 Harness 离线探测（Replay Probe）设计方案

状态：草案，待评审。日期：2026-10-28。基线：main `0abce24`（0.25.6）。

前置方案：`docs/plans/harness-manifest-fingerprint-20261028.md`（`harness_id`、`change_class`、`privilege_widening` 来自该方案，本方案依赖其阶段 A0 与 A1）。

## 1. 要解决的问题与边界

目标：给定一份**基线运行的录制**和一个**候选 harness**，在不调用模型、不执行任何真实工具的前提下，回答四个问题：

1. 候选与录制的轨迹在结构上是否兼容（工具能否解析、参数是否仍合法、策略是否仍放行）。
2. 候选会让模型"看到"什么不同（每步请求的差异）。
3. 安全方向有没有回退（权限放宽、沙箱收紧变松）。
4. 这次探测的证据覆盖到轨迹的哪一步为止。

### 1.1 重放能证明什么、不能证明什么

录制的模型回复是在**基线 harness 的输入**下产生的。候选 harness 一旦改变了模型可见的输入，同一条回复就不再是候选下的有效样本。因此：

| 变更类别（来自前置方案 §3.6） | 重放能提供的证据 | 重放不能提供的证据 |
|---|---|---|
| A 类（机制）：策略、沙箱、Tool Search、上下文压缩、结构化输出、工具副作用与沙箱要求 | 在忠实前缀内，对录制调用的放行、阻断、可见性、预算有**确定性结论** | 模型行为在新机制下是否更好 |
| B 类（提示可见）：指令、技能目录、工具描述与 schema | 结构兼容性、体积与预算变化、请求差异 | **任何质量结论**。第一步请求就已不同，录制回复不是有效样本 |

这条边界是本方案最重要的约束。探测报告必须**显式输出覆盖范围**，不得用"通过"暗示"更好"。对 B 类变更，探测只是门禁，不是评估；真正评估质量需要真实运行（§9 的 P3，本方案只定义接口）。

### 1.2 非目标

- 不做质量评分；不引入裁判模型。
- 不自动生成候选（Harness² 的 Propose 不在范围内）。
- 不改 v2 审计 schema 与 checkpoint；不替换现有 `qre replay`。
- 不执行任何真实工具、不联网。

## 2. 现有重放为什么不够

| 现有机制 | 做什么 | 为什么不能直接当探测 |
|---|---|---|
| `qre replay --strict`（v2） | `ReplayLatestV2` 调用 `RuntimeRecordedReplay.Replay`（`Cli/Program.cs:3314`，`Engine/V2/RuntimeAudit.cs:375`）。它是一个**数据校验归约器**：检查序列连续、因果链、请求响应配对、工具调用与观测配对等 | 不构造 `RuntimeAgentLoop`，不查工具注册表、不跑策略、不跑上下文管理。候选是什么对它没有任何影响 |
| v1 实验性重放 | `RecordedReplayModelClient` + `RecordedReplayToolPack` 驱动 `ExperimentalQueryRuntimeHarness`（`Experimental/RecordedReplayAdapters.cs`） | v1 已在 v2-only 切换后停用（`Cli/Program.cs:3106`）。并且工具输出按"工具名 + 参数哈希"取**第一条匹配**（`RecordedReplayAdapters.cs:173` 的 `FirstOrDefault`），同一参数重复调用但结果不同的情形会被错配（例如补丁前后读同一文件） |

结论：需要一个**新的、驱动真实 v2 循环**的反事实重放，复用 v2 的 `IRuntimeModelClient` 与工具接口作为替换点，而不是修改现有重放。

## 3. 设计

### 3.1 总体流程

```
基线录制 (audit.v1.jsonl + blobs)        候选 harness (目录或 harness_id)
          │                                         │
          ▼                                         ▼
   RecordedRunSource  ──────────►  ProbeSession（驱动真实 RuntimeAgentLoop）
   （解析每步的请求、回复、工具调用与结果）            ├─ RecordedRuntimeModelClient : IRuntimeModelClient
                                                         ├─ 候选的工具注册表 / 策略 / 上下文管理 / 工具目录选择器
                                                         └─ RecordedToolHost（只回放结果，永不执行）
                                                                    │
                                                                    ▼
                                                      逐步比对 + 不变量检查 + 覆盖范围
                                                                    │
                                                                    ▼
                                                      ProbeReport（JSON + 文本）+ probe_digest
```

### 3.2 驱动真实循环

`RuntimeAgentLoopRequest` 暴露了替换点（`Engine/V2/RuntimeAgentLoopContracts.cs:8-65`）：`ToolPipeline`、`ContextManager`、`ToolCatalogSelector`、`ToolAuthorization`、`ToolApproval`、`AuditSink`，循环构造函数接收 `IRuntimeModelClient` 与 `TimeProvider`（`Engine/V2/RuntimeAgentLoop.cs:15`）。

做法：

- **模型端**：`RecordedRuntimeModelClient : IRuntimeModelClient`（`Protocol/ModelContracts.cs:6`）。第 N 次 `StreamAsync(request)` 被调用时，先把**候选实际发出的请求** `RuntimeModelRequest` 交给比对器，再按录制返回第 N 步的回复流。
- **工具端**：保留候选的 `RuntimeToolDescriptor` 与 `Definition`（schema、副作用、沙箱要求），使注册表解析、参数校验、策略评估照常发生；把**执行层**换成只回放。
  - 方案 a（推荐）：为候选的每个工具包一层 `RecordedRuntimeTool : IRuntimeTool`（`Engine/V2/RuntimeTools.cs:63`），`Definition` 取自候选；同时用一个 `RecordedRuntimeSandbox : IRuntimeSandbox`（`:96`）替换沙箱路由，使需要沙箱的工具（如 `qre_dotnet_test`）也只回放。
  - 方案 b：装饰 `IRuntimeToolExecutionPipeline`（`:605`），在"计划已解析、尚未执行"处截断。需要流水线暴露该切面，改动面更大。
  - 选择依据见 S1 摸底。无论哪种，**构造上**保证不可能触达真实沙箱：沙箱路由里只有 `RecordedRuntimeSandbox`，另加断言 `RealExecutions == 0`。
- **时间与 ID**：沿用 `DeterministicReplayClock`（`Experimental/DeterministicReplayClock.cs`）与固定 query id，使 `probe_digest` 可复现。
- **审计**：探测会话使用内存审计 sink（`InMemoryRuntimeAuditSink`），不写入工作区，避免污染 `.qre/v2/runs`。

### 3.3 工具结果的匹配键

键为 `(规范工具名, 规范参数哈希, 第几次出现)`，参数哈希复用 `QreArgumentHash`（`Experimental/QreArgumentHash.cs`），**第几次出现**用于消除 §2 所述的重复调用错配。

- 候选发起的调用在录制里找不到对应项：不报错终止，而是记为 `unmatched_call` 并结束探测，覆盖范围到此为止。
- 录制里有、候选永远没发起的调用：记为 `unexercised_recorded_call`（信息级）。

### 3.4 请求比对与"忠实前缀"

对每一步，把候选请求与录制中的 `RuntimeModelRequestAuditPayload.Request`（`Engine/V2/RuntimeAudit.cs:107`）做字段级比对：

| 比对项 | 方法 | 是否对模型可见 |
|---|---|---|
| 消息序列 | 逐条（角色 + 规范化内容）哈希 | 是 |
| 工具集合 | 描述符哈希集合（名称、描述、schema） | 是 |
| 工具顺序 | 序列比较 | 视为非可见差异，单独记录 |
| 模型参数 | 去除模型 ID 后比较 | 是（温度、最大输出、结构化输出） |
| 历史版本号 | `HistoryVersion` | 否 |

定义：**首个分歧步** `D` = 第一个"模型可见项"不同的步。

- 步骤 `< D`：**忠实**。候选收到的输入与录制完全相同，录制回复是有效样本，这些步骤上的策略与工具结论是实证。
- 步骤 `≥ D`：**反事实**。仍可继续用录制回复驱动循环以检查机制类不变量（如上下文压缩是否稳定），但每条结论标记 `coverage: "assumed"`，不得作质量解读。

对 B 类变更，`D` 通常是第 1 步，忠实前缀长度为 0。报告会如实给出 `faithful_steps / total_steps = 0 / N`。

### 3.5 检查项（不变量）

严重级别：`critical` / `error` / `warn` / `info`。阈值由门禁配置决定（§3.7）。

| 编号 | 检查 | 覆盖 | 默认级别 |
|---|---|---|---|
| P-000 | 候选清单可信：前置方案的旁路清单与审计里的工具描述符交叉校验一致 | 全部 | error |
| P-001 | 录制中每个工具调用的名称在候选注册表中可解析；对延迟工具，候选在该步确实已激活 | 忠实前缀 | error |
| P-002 | 录制的调用参数仍通过候选 `InputSchema` 校验 | 忠实前缀 | error |
| P-003 | 基线放行的调用，候选策略不得新增阻断（功能回退） | 忠实前缀 | warn |
| P-004 | 基线被阻断或需批准的调用，候选不得变为放行（权限放宽） | 忠实前缀 + 静态 | **critical** |
| P-005 | 工具副作用、幂等性、沙箱要求、网络模式、挂载模式不得变弱（静态比较两个清单） | 静态 | **critical** |
| P-006 | 结构化输出种类与 schema 哈希不变，除非报告声明预期变更 | 每步 | error |
| P-007 | 上下文管理：每步请求不超窗口；压缩与硬上限事件的步次与基线对比 | 全部（标注忠实/反事实） | warn |
| P-008 | Tool Search 召回：后续录制调用所需的工具，在候选的目录选择结果里是否已暴露 | 忠实前缀 | error |
| P-009 | 预算形态：估算输入 token 的逐步变化与总量变化（复用运行时的估算口径，不自造口径） | 全部 | info |
| P-010 | 常驻指令体积变化 | 静态 | info |
| P-011 | 自检：同一输入连续探测两次，`probe_digest` 相同 | 全部 | error |

其中 P-004、P-005 是本方案的安全底线：这是 fail-closed 在"改 harness"这件事上的落地，也是 QRE 区别于单纯做 prompt 优化的地方。P-008 对 Tool Search 配置变更是真正可由重放决定的信号（召回率）。

### 3.6 报告

`qre.harness.probe.report`（`schemaVersion: 1`）：

```jsonc
{
  "type": "qre.harness.probe.report",
  "schemaVersion": 1,
  "baseline": { "runId": "...", "harnessId": "sha256:...", "complete": true },
  "candidate": { "harnessId": "sha256:...", "complete": true },
  "changeClass": "A|B|mixed",
  "coverage": { "totalSteps": 7, "faithfulSteps": 3, "firstDivergentStep": 4,
                "divergenceReason": "tools-changed", "endedBy": "completed|unmatched_call|loop_error" },
  "steps": [ { "step": 1, "requestDiff": "none", "recordedCalls": 1, "policy": "same",
               "estimatedInputTokens": { "baseline": 1200, "candidate": 1180 } } ],
  "findings": [ { "id": "P-004", "severity": "critical", "step": 2, "coverage": "faithful",
                  "message": "...", "evidence": { "tool": "qre_apply_patch", "baseline": "blocked", "candidate": "allowed" } } ],
  "realExecutions": 0,
  "probeDigest": "sha256:..."
}
```

- `probeDigest` 对报告的规范化形式（去掉时间戳与路径）求 SHA-256，仿照现有 `replay_digest`。
- `public` 存储等级下，报告只输出编号、级别、步次与计数，不输出工具名与消息内容。
- `realExecutions` 必须为 0，否则探测本身判定失败。

### 3.7 门禁配置

`gate.json`（可选，缺省使用内置默认）：

```jsonc
{
  "failOn": ["critical", "error"],
  "requireMinFaithfulSteps": 0,
  "requireFaithfulStepsFor": "A",
  "allow": [ { "id": "P-006", "reason": "迁移到 JsonSchema" } ]
}
```

- 默认只对 `critical`/`error` 失败。
- B 类变更**不得**因 `faithfulSteps = 0` 而被判通过"质量"：报告顶部固定输出一行 `evidence: structural-only`，下游工具必须据此区分。
- 例外 `allow` 必须带 `reason`，并写入报告。

退出码（提议，需与现有 `Fail()` 约定对齐）：`0` 无超过阈值的发现；`2` 门禁失败；`1` 探测自身出错（如基线不可重放、清单不可信）。

## 4. CLI

```text
qre harness probe --baseline <run-id|evalset:NAME> --candidate <harness-dir|harness-id>
                  [--gate gate.json] [--json] [--workspace .]
qre harness evalset add <run-id> --name NAME [--tag TAG]
qre harness evalset list
qre harness evalset verify NAME
```

`--candidate` 需要能物化为一份候选清单与可构造的运行组成（工具、策略、上下文、目录选择器）。MVP 先支持"由当前工作区加上显式覆盖项构造候选"：`--profile`、`--tool-search*`、`--external` 等现有 CLI 开关，以及技能目录。独立的"harness 目录"输入格式放到 P2，避免过早引入新的配置面。

## 5. 评测集（Evalset）

基线运行目录会被保留策略清理（`RuntimeJsonlAuditStore.PruneTerminalRuns` 按 `Retention` / `MaxStoredRuns` / `MaxTotalStorageBytes`），所以长期基线必须拷出。

- 位置：`.qre/harness/evalsets/<name>/`，每个基线一个子目录，内含：`audit.v1.jsonl`、所需 `blobs/`、`harness.manifest.json`、`meta.json`（标签、来源 run id、数据模式、创建时间）。
- 入集条件：`ReplayCapability == Recorded` 且数据模式为 `SanitizedFixture` 或 `PrivateDiagnostic`；`public` 脱敏录制是仅摘要，**拒绝入集**并说明原因。
- 入集时先用 `RuntimeJsonlAuditStore.Read` 校验完整性，再复制；目录权限沿用 `TraceStorageSecurity.RestrictPrivateFile`。`PrivateDiagnostic` 录制含完整内容，入集给出警告，且 `evalset` 目录默认不进入版本库（`.qre` 已被忽略）。
- 评测集选择应覆盖：不同工具轮次数、含写操作的任务、含 Tool Search 的任务、含结构化输出的任务、含失败重试的任务。
- 评测集本身不含评分。评分（若有）在探测之外，由独立进程消费运行目录，**绝不回流到候选生成**，沿用 Harness² 的"评测与改进分离"原则。

## 6. 闭环

```
 ① 记录基线   qre run --trace-data sanitized ...        （已有能力）
 ② 入集       qre harness evalset add <run> --name core
 ③ 制作候选   人工或外部工具修改 harness                 （本方案不自动生成）
 ④ 离线探测   qre harness probe --baseline evalset:core --candidate ...
 ⑤ 判定
       ├─ 任一 critical/error → 拒绝，附报告
       ├─ A 类通过 → 可采纳；忠实前缀给出的结论是实证
       └─ B 类通过 → 仅表示结构安全，进入 ⑥
 ⑥ 真实评估（P3，可选）：固定任务集上对基线与候选各跑 K 次，预算上限、交错执行，
    由外部评分器打分；结果与 probe 报告并列保存
 ⑦ 采纳       记录候选的 harness_id；以 --expect-harness-id 固定
```

说明：⑥ 是唯一能回答"B 类变更是否更好"的环节，成本与 Harness² 的真实 rollout 同量级，所以放在最后并设预算闸门，不放进 MVP。

## 7. 代码落点

| 位置 | 内容 |
|---|---|
| `Experimental/Harness/Probe/RecordedRunSource.cs` | 从审计录制抽取每步的请求、回复、工具调用与结果；复用 `RuntimeJsonlAuditStore.Read` |
| `.../Probe/RecordedRuntimeModelClient.cs` | `IRuntimeModelClient` 的回放实现，并把候选请求交给比对器 |
| `.../Probe/RecordedToolHost.cs` | `RecordedRuntimeTool`、`RecordedRuntimeSandbox`、按 §3.3 键匹配 |
| `.../Probe/RequestComparer.cs` | §3.4 的字段级比对与首个分歧步判定 |
| `.../Probe/Checks/*.cs` | P-000 至 P-011，每项一个类，输入统一的 `ProbeContext` |
| `.../Probe/ProbeReport.cs` | 报告记录类型、规范化与 `probe_digest` |
| `.../Probe/EvalsetStore.cs` | 评测集读写与校验 |
| `Cli/QreHarnessCommands.cs` | `probe` 与 `evalset` 子命令（与前置方案共用文件） |

Engine 项目的目标是**零改动**，只使用已公开的接口。若 S1 摸底发现必须新增切面（例如流水线的"计划已解析、未执行"回调），在引擎里以内部可见性的最小接口提供，并单独评审。

## 8. 阶段计划

### S1：摸底与可行性验证（只读 + 一个临时脚手架测试）

- 通读 `RuntimeTools.cs` 的 `RuntimeToolExecutionPipeline`（约 605 到 993 行）与 `ExperimentalV2PolicyEvaluator`，确定 §3.2 的方案 a 或 b。
- 通读 `RuntimeContext.cs` 的 `IRuntimeContextManager` 与 `IRuntimeToolCatalogSelector`，确认它们可由候选配置独立构造，且不依赖活的模型客户端。
- 写一个一次性的单元测试：用仓库内现有的一份已录制 v2 审计夹具，驱动真实 `RuntimeAgentLoop`，使用回放模型与回放工具，断言产生的审计与录制一致。**这一步是整个方案的可行性证明**，不通过则回到评审。
- 确认 `unmatched_call` 终止的方式（循环对"工具结果缺失"是报错还是继续）。

闸门：一次性测试对"零分歧"夹具复现录制；否则不进入 P0。

### P0：单基线、A 类变更

- 实现 `RecordedRunSource`、`RecordedRuntimeModelClient`、`RecordedToolHost`、`RequestComparer`，检查项 P-000 至 P-006、P-011。
- CLI：`qre harness probe --baseline <run-id> --candidate`（候选由现有 CLI 开关构造）。
- 验收：
  - 零分歧：候选等于基线时，`faithfulSteps == totalSteps`，无发现，`probeDigest` 稳定。
  - 注入：把策略 profile 从 `repair` 降到 `readonly`，基线含写调用 → P-003；反向升级 → P-004 为 `critical`。
  - 注入：工具 schema 收紧使录制参数失效 → P-002。
  - `realExecutions == 0` 在所有用例成立；用一个"一旦被调用就抛异常"的沙箱做对照。
  - 重复调用且结果不同的夹具：按"第几次出现"得到正确结果（回归 §2 的错配问题）。

### P1：评测集与更多检查

- `evalset add/list/verify`，探测支持 `evalset:NAME` 并输出汇总。
- 检查项 P-007、P-008、P-009、P-010。
- 验收：Tool Search 的 `TopK` 调小时，P-008 能报告召回下降；上下文窗口调小时，P-007 报告额外压缩事件。

### P2：B 类变更与 harness 目录输入

- 完整的请求差异报告；`evidence: structural-only` 标记；门禁配置文件。
- 可选：`--candidate <harness-dir>` 输入格式（与前置方案的 A2/A3 协同，单独评审后再做）。

### P3：真实评估通道（可选，单独立项）

- 只定义接口：固定任务集、K 次交错运行、预算上限、外部评分器协议。引擎内不实现评分。
- 单独方案与评审，不在本方案的交付范围内。

## 9. 测试策略

- 夹具：使用已入库的合成录制（`SanitizedFixture`）。**不得**把含真实内容的 `PrivateDiagnostic` 录制提交进仓库。
- 单元测试放在 `CodexFlow.QueryRuntime.UnitTests/Experimental/Harness/`，沿用现有目录组织；夹具生成脚本放 `scripts/`。
- 回归门禁：`dotnet test CodexFlow.QueryRuntime.slnx --no-restore`、`scripts/qre-aot-gate.sh`、`scripts/queryruntime-baseline-gate.sh`。
- 跨平台：Windows 与 Linux 上相同输入得到相同 `probeDigest`（CI 双平台已存在）。

## 10. 风险与开放问题

| 编号 | 风险 / 问题 | 缓解 |
|---|---|---|
| R1 | 对 B 类变更被误读为"探测通过即更好" | 报告固定标注覆盖范围与 `evidence` 字段；门禁不提供"质量通过"语义；文档与 CLI 帮助重复强调 |
| R2 | 循环对缺失工具结果或提前结束的行为不确定 | S1 先验证；`unmatched_call` 作为显式终止原因 |
| R3 | 工具有隐含状态（先写后读），回放结果顺序依赖 | 键含"第几次出现"；候选调用序列分歧后立刻结束忠实前缀 |
| R4 | 基线含 `PrivateDiagnostic` 的敏感内容 | 评测集默认不入版本库；报告在 `public` 等级下脱敏；入集时警告 |
| R5 | 估算 token 口径与真实计费不同 | 只报告相对变化并标注 `estimated`，复用运行时现有估算口径 |
| R6 | 为让探测通过而在引擎里加切面，污染核心 | 引擎零改动为默认；必须新增时最小化、内部可见、单独评审 |
| R7 | 评测集过小造成虚假信心 | 报告输出基线数量与覆盖标签；不对"通过率"做统计推断 |
| R8 | 探测结论被用作采纳 B 类变更的唯一依据 | 闭环中 B 类必须经过 ⑥；文档明确禁止仅凭 ④ 采纳 |

开放问题：

- O1：候选的 Tool Search 目录选择器是否依赖前一步里模型发出的 `tool_search` 查询内容。若是，则忠实前缀内的召回判定是可靠的；需要在 S1 用例中确认。
- O2：退出码约定是否沿用现有 `Fail()`（返回 1）并为门禁失败新增 2，需与现有 CLI 习惯核对。
- O3：评测集是否要允许跨 QRE 版本复用。审计 schema 有 `CurrentVersion` 与 `EnhancedVersion` 两个版本，探测需要对不支持的版本给出明确拒绝原因，而不是静默跳过。

## 11. 与 Harness² 的对应关系

| Harness² | 本方案 |
|---|---|
| Probe 要真跑每个候选（`4k` 次 rollout） | 机制类变更先用零 token 重放预筛；只有 B 类才需要真实运行 |
| 提案校验（技能必须被触发、脚本必须存在、hooks 合法） | 前置方案的 `harness verify`（A3）与本方案的 P-000/P-001/P-002 |
| 评测不进入改进上下文，评分在选择提交之后 | 评测集不含评分；评分器独立进程，结果不回流候选生成 |
| 盲选、固定种子 | 不在本方案内；如做 P3 的 A/B，再采用 |
| 经验库 | 不在本方案内 |
