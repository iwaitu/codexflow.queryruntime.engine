# Harness 清单与指纹（HarnessManifest）设计方案

状态：草案，待评审。日期：2026-10-28。基线：main `0abce24`（0.25.6）。

姊妹方案：`docs/plans/replay-harness-probe-20261028.md`（基于重放的候选 Harness 离线探测）。本方案提供其所需的"harness 身份"，应先于探测方案落地。

## 1. 目标与非目标

### 目标

1. 给"一次运行使用的 harness"一个**内容寻址、与任务无关、跨平台一致**的身份（`harness_id`），并随运行落盘。
2. 提供 `qre harness show / diff`，回答"这两次运行的行为差异，是 harness 的哪一处改动造成的"。
3. 提供可选的运行前固定（`--expect-harness-id`），在生产或 CI 中阻止 harness 悄悄漂移。
4. 把 harness 的改动分成"机制类"和"提示可见类"，供探测方案决定重放能提供多少证据。

### 非目标

- 不自动生成或修改 harness（Harness² 的 Propose 步骤不在本方案内）。
- 不在引擎内做质量评分。
- 不修改 v2 审计 schema、checkpoint schema、`RecoveryCompatibilityDigest` 的计算输入（见 §3.3）。
- 不承诺公共 NuGet API；新类型先放在 `CodexFlow.QueryRuntime.Experimental`。

## 2. 背景：现状与缺口

下表基于对源码的阅读，行号为 2026-10-28 时的位置。

| 组件 | 现在在哪里 | 是否有独立、稳定的指纹 |
|---|---|---|
| 初始消息（含 system） | 宿主构造 `InitialMessages`；落在 `RuntimeCheckpointRequestSnapshot`（`Engine/V2/RuntimeCheckpoint.cs:100`） | 否。与 `Objective` 混在同一个请求指纹里，且随任务变化 |
| 工具描述符（名称、版本、描述、`InputSchema`、副作用、幂等性） | `RuntimeToolDescriptor`（`Protocol/ModelContracts.cs:156`）；每步 `model.request` 审计载荷携带全量工具（`Engine/V2/RuntimeAudit.cs:107`） | 否。没有汇总哈希 |
| 外部工具清单 `.qre/tools/*.json` | `ExternalStdioToolPack` | 部分。`ComputeRecoveryCompatibilityDigest` 参与恢复兼容摘要（`Experimental/ExternalStdioToolPack.cs:65`），语义是"能否恢复"，不是"内容是什么" |
| 策略 | `RuntimePolicySnapshot(Version, Profile)`（`Engine/V2/RuntimeState.cs:305`）；规则在 `ExperimentalV2PolicyEvaluator`、`ExperimentalCapabilityPolicy` 代码里 | 否。只有版本字符串和 profile 名，规则本身不进任何摘要 |
| 沙箱、Tool Search 开关 | `ExperimentalV2ToolComposition` | 是，但只在 `CreateRecoveryCompatibilityDigest` 内（`Experimental/ExperimentalV2ToolComposition.cs:144`），且不含工具描述内容 |
| 上下文管理参数 | `RuntimeAgentLoopRequest.ContextManager`（`Engine/V2/RuntimeAgentLoopContracts.cs:31`） | 否。只留下压缩事件 |
| 模型参数、结构化输出 | `RuntimeModelParameters` | 部分。只有 `ResponseFormatSchemaSha256` 进审计 |
| Skills | SDK 侧：`VllmChatOptions.EnableSkills` / `SkillDirectoryPath`；QRE 只在 `QreThinkingPolicy.cs:70` 透传 | 否。QRE 引擎看不到技能内容，审计里也没有 |

三个关键结论：

1. **没有任务无关的 harness 身份。** `RequestFingerprint` 包含 `Objective` 和工作区身份，只能证明"这是同一个请求"，不能回答"这是同一个 harness"。
2. **`RecoveryCompatibilityDigest` 不能顺手扩展。** 它的语义是"checkpoint 能否恢复"，并参与 H1 恢复前的漂移校验（README 描述为校验工具注册表、策略快照与模型配置）。改变它的输入，很可能让已有 checkpoint 的兼容性比对不再通过（此点尚未逐行核实，属推断）。因此 harness 指纹必须是**新增、独立**的字段。
3. **Skills 是盲区。** 它们由 SDK 客户端装载，QRE 无法保证自己算出的目录与 SDK 实际使用的目录一致（见 §8 风险 R1）。

三种身份的关系：

| 身份 | 回答的问题 | 范围 | 现有/新增 |
|---|---|---|---|
| `harness_id` | 这是同一个 harness 吗 | 任务无关 | **新增** |
| `request_fingerprint` | 这是同一个请求吗 | 含任务与工作区 | 现有 |
| `recovery digest` | 这个 checkpoint 能恢复吗 | 组合标识 | 现有，不改 |

## 3. 设计

### 3.1 Harness 的定义

QRE 的 harness 由八类组件构成，按"对模型是否直接可见"分成两类，这个分类会被探测方案直接使用。

| 组件 | 内容 | 类别 |
|---|---|---|
| `instructions` | system / 初始指令消息（不含任务目标） | 提示可见（B） |
| `skills` | 技能目录：每个技能的 `name`、`description`、正文、`scripts/`、`references/` | 提示可见（B）；描述进目录，正文按需 |
| `tools` | 暴露给模型的工具描述符集合 | 描述与 schema 属提示可见（B）；副作用、幂等性、沙箱要求属机制（A） |
| `externalTools` | `.qre/tools/*.json` 与 MCP stdio 清单 | 同 `tools` |
| `policy` | profile、策略版本、工具到能力集合的映射 | 机制（A） |
| `sandbox` | 沙箱种类、网络模式、挂载模式 | 机制（A） |
| `toolSearch` | 是否启用、TopK、常驻与延迟工具名单 | 机制（A）；影响模型看到的工具集 |
| `context` | 窗口、压缩阈值、截断策略 | 机制（A） |
| `responseFormat` | 结构化输出种类与 schema 哈希 | 机制（A） |

模型绑定（模型 ID、温度、最大输出等）**不进入 `harness_id`**，单独记录为 `binding_id = H(harness_id + modelBinding)`。理由：同一个 harness 应当能在不同模型之间比较，这与 Harness² "固定权重、只改 harness" 的前提一致。

### 3.2 HarnessManifest

```jsonc
{
  "type": "qre.harness.manifest",
  "schemaVersion": 1,
  "harnessId": "sha256:<hex>",          // H(规范化后的 components 摘要映射)
  "bindingId": "sha256:<hex>",
  "complete": true,                      // false 表示有组件无法解析，见 skills
  "components": {
    "instructions": { "digest": "...", "chars": 1234 },
    "skills":       { "digest": "...", "resolution": "explicit|workspace|unresolved",
                      "items": [ { "name": "...", "descriptionSha256": "...", "bodySha256": "...", "files": 3 } ] },
    "tools":        { "digest": "...", "items": [ { "name": "...", "version": "...", "descriptorSha256": "...",
                      "sideEffect": "ReadOnly", "idempotency": "..." } ] },
    "externalTools":{ "digest": "...", "items": [ { "name": "...", "manifestSha256": "..." } ] },
    "policy":       { "digest": "...", "profile": "verify", "version": "...", "implementationVersion": "..." },
    "sandbox":      { "digest": "...", "kind": "Docker", "network": "Deny" },
    "toolSearch":   { "digest": "...", "enabled": true, "topK": 5 },
    "context":      { "digest": "..." },
    "responseFormat": { "digest": "...", "kind": "JsonSchema" }
  },
  "modelBinding": { "model": "...", "temperature": null, "maxOutputTokens": 4096 },
  "createdAt": "..."
}
```

`harnessId` 只由各组件的 `digest` 按组件名排序后拼接再哈希得到；`items`、`chars` 等是便于 `diff` 展示的明细，不进入哈希（它们本身已被 `digest` 覆盖）。

### 3.3 规范化规则

跨平台一致是硬要求（QRE 同时发布 Windows 与 Linux，且做 Native AOT）。

- JSON 一律规范化：属性名按序号排序，无多余空白，UTF-8 无 BOM。可复用 `Experimental/QreArgumentHash.cs` 的规范化思路（含数字归一）；`InputSchema` 是 `JsonElement`，已支持。
- 文本文件（`.md`、`.json`、`.py`、`.sh`、`.js`、`.yaml` 等）哈希前统一为 LF，去掉 BOM；其他文件按原字节。
- 路径统一为相对路径、`/` 分隔、区分大小写比较；拒绝符号链接与越界路径，复用 `Abstractions/QueryRuntimePathSafety.cs` 的 `RejectWorkspaceLinks` / `ResolveUnderRoot`。
- 排除 `__pycache__`、`*.pyc`、`.git`；单文件与目录总大小设上限，超限则 `complete=false` 并给出原因，不静默截断。
- 策略摘要为**行为摘要**：`H(policyImplementationVersion | profile | 按名称排序的 (工具名, 能力集合, 副作用, 沙箱要求))`。`policyImplementationVersion` 是代码常量，仿照现有 `experimental-v2-composition-v1`；用黄金哈希单元测试固定每个 profile 的摘要，规则改了而常量没升就让 CI 失败。
- 不改 `RecoveryCompatibilityDigest` 的任何输入。

### 3.4 落盘位置与完整性

v2 运行目录现状（`.qre/v2/runs/<run-id>/`）：`audit.v1.jsonl`、`checkpoint.v1.json`、`manifest.json`、`usage.json`、`diff.patch`、`artifacts/`、`blobs/`。

- 新增旁路文件 `harness.manifest.json`。**不**新增审计事件类型，**不**改 `manifest.json` 与 checkpoint 的 schema，避免触发"同一录制必须同一 schema"的校验和 H1 的恢复漂移校验。
- 防篡改：旁路文件写入 `runId`，并写入"由本次运行首个 `model.request` 审计载荷中的工具描述符重新计算出的 `tools.digest`"。读取时重新从审计计算并比对，不一致则该清单标记为不可信（`harness show` 显示警告，探测方案拒绝使用）。这样 `tools` 组件有审计数据作锚点；其余组件为尽力记录。
- 保留策略：`RuntimeJsonlAuditStore` 会按 `Retention` / `MaxStoredRuns` / `MaxTotalStorageBytes` 清理已结束的运行目录。旁路文件随运行目录一起被清理，这是预期行为；需要长期保存的基线必须走评测集（探测方案 §5）。
- 存储等级：
  - `public`：只写 `harnessId`、`bindingId` 与各组件 `digest`，**不写**技能名、工具名、文件名、模型 ID。
  - `sanitized` / `private`：写完整明细；文件权限沿用 `TraceStorageSecurity.RestrictPrivateFile`。
- 外部工具清单可能含参数或环境引用，只记录其内容哈希与名称，不记录内容。

### 3.5 Skills 的处理

QRE 引擎自身不加载技能，因此：

- 只在能确定目录时计算：显式 `--skills-dir`，或 `.qre/skills`、`./skills` 这类 QRE 自己能解析的位置。`resolution` 记录来源。
- `EnableSkills=true` 但 QRE 无法确定 SDK 实际使用的目录时，`skills.resolution="unresolved"`，整个清单 `complete=false`。`complete=false` 的清单**可以展示、可以 diff，但 `--expect-harness-id` 不接受它，探测方案也不把它作为"已知等价"的依据**。
- 待办（开放问题 O1）：确认 VllmChatClient 2.0.34 是否暴露实际解析出的技能目录或技能清单；若暴露，改为读取 SDK 的解析结果，以消除不一致风险。

### 3.6 `diff` 与变更分类

`qre harness diff <A> <B>`，A/B 可以是：运行 ID、`harness_id`、清单文件路径。

输出（文本与 `--json`）：

- 逐组件 `unchanged / changed / added / removed`。
- `tools`：逐工具列出变化字段（描述、schema 的键级差异、副作用、幂等性）。
- `skills`：区分"描述变化（进入目录，模型必看）"与"正文变化（按需加载）"。
- `instructions`：仅 `private` / `sanitized` 且双方都有原文时给统一 diff；`public` 只给字符数变化。
- `change_class`：
  - `A`（机制类）：只有 `policy`、`sandbox`、`toolSearch`、`context`、`responseFormat`、以及 `tools` 中的副作用/幂等性/沙箱要求发生变化。
  - `B`（提示可见类）：`instructions`、`skills`、`tools` 的描述或 schema 变化，只要出现其一即为 B。
  - 混合时按 B 处理，并分别列出两部分。
- 安全方向提示：副作用升级（如 `ReadOnly→WorkspaceWrite`）、网络由 `Deny` 变为放行、挂载由只读变读写，单独标为 `privilege_widening`。

### 3.7 运行前固定与校验

- `qre run ... --expect-harness-id sha256:<hex>`：运行开始前计算实际 `harness_id`，不一致或 `complete=false` 则**失败关闭**，不发任何模型请求。默认关闭，不改变现有行为。
- `qre harness verify [--harness-dir <dir>]`（可选，A3 阶段）：对应 Harness² 的提案校验，检查技能 frontmatter 完整性、技能是否被指令引用、脚本引用是否存在、外部工具清单 JSON 合法与名称唯一。只做静态检查。

### 3.8 代码落点

| 位置 | 内容 |
|---|---|
| `CodexFlow.QueryRuntime.Experimental/Harness/HarnessManifest.cs` | 记录类型与 `schemaVersion` 常量 |
| `.../Harness/HarnessCanonicalizer.cs` | 规范化与哈希（含换行归一） |
| `.../Harness/HarnessManifestBuilder.cs` | 从有效运行组成（工具描述符、策略、沙箱、Tool Search、技能目录）构建清单 |
| `.../Harness/HarnessDiff.cs` | 差异与 `change_class` 分类 |
| `.../Harness/HarnessManifestStore.cs` | 旁路文件读写、与审计交叉校验 |
| `CodexFlow.QueryRuntime.Cli/QreHarnessCommands.cs` | `qre harness show/diff/verify`，沿用 `QreLogCommands.cs`、`QreDiagnosticsCommands.cs` 的拆分方式，避免继续膨胀 `Program.cs`（约 4600 行） |
| `CodexFlow.QueryRuntime.Cli/Program.cs` | 仅新增 `harness` 分发、`--expect-harness-id` 参数、运行开始时写清单的调用点 |

所有 JSON 使用 System.Text.Json 源生成上下文，不引入反射，不引入新依赖，以满足 Native AOT 门禁（`scripts/qre-aot-gate.sh`、`scripts/qre-aot-approved-warnings.txt`）。

## 4. 分阶段计划

每阶段结束都是可单独合入的状态；阶段之间设闸门。

### 阶段 S0：摸底（只读，不改代码）

- 读 `RuntimeJsonlAuditStore` 的运行目录写入与清理路径，确认运行目录中新增未知文件不会被审计读取、清理、完整性检查拒绝。
- 确认 `manifest.json` / 审计读取使用的 `System.Text.Json` 设置不会因未知字段失败（本方案不加字段，但此项决定后续是否可以在 `manifest.json` 里引用 `harnessId`）。
- 读 `RuntimeToolExecutionPipeline` 与 `ExperimentalV2PolicyEvaluator`，确定"策略行为摘要"能否在不执行工具的前提下计算。
- 核对 VllmChatClient 2.0.34 的技能目录解析接口（O1）。
- 产出：一页结论追加到本文档 §9。

闸门：S0 的四项结论全部有源码证据；否则相应设计点回到评审。

### 阶段 A0：观测式落盘（零行为变化）

- 实现 `HarnessManifest`、规范化、构建器、旁路文件写入。
- 运行开始时写清单；运行结束不再修改。
- 写失败的处理：记录诊断并继续运行（观测功能不得让运行失败），除非用户显式启用 `--expect-harness-id`。

验收：

- 单元测试：同一组件集合在 Windows 与 Linux（CRLF/LF 输入）得到相同 `harness_id`；任一组件变化则 `harness_id` 变化；模型绑定变化只改 `binding_id`。
- 黄金哈希测试固定每个 profile 的策略摘要。
- `public` 模式清单不含任何名称或路径（用字符串扫描断言）。
- 篡改旁路文件的 `tools.digest`，读取时被交叉校验识别。
- `dotnet test CodexFlow.QueryRuntime.slnx --no-restore` 通过；`scripts/qre-aot-gate.sh` 不新增告警；`scripts/queryruntime-baseline-gate.sh` 通过。
- 既有行为不变：现有 `QreCliSmokeTests`、`RuntimeCheckpointTests` 全部通过且无需修改。

### 阶段 A1：`qre harness show / diff`

- 实现 §3.6，支持 `--json`。
- 验收：对"仅改策略 profile""仅改技能正文""仅改工具描述""新增工具"四类夹具，`change_class` 与 `privilege_widening` 判定正确；`public` 模式不泄露名称。

### 阶段 A2：运行前固定

- 实现 `--expect-harness-id`，`complete=false` 时失败关闭。
- 验收：不匹配时进程退出非零且**零次**模型调用（用计数模型客户端断言）；匹配时行为与不加参数完全一致。

### 阶段 A3：静态校验（可选）

- `qre harness verify`，并接入 `qre doctor` 的输出。

## 5. 与现有机制的兼容性

| 机制 | 影响 |
|---|---|
| v2 审计 `audit.v1.jsonl` | 不改 |
| checkpoint 与 H1 恢复 | 不改；不参与恢复判定 |
| `RecoveryCompatibilityDigest` | 不改输入，不改语义 |
| `qre replay --strict` 与 `replay_digest` | 不改；`replay_digest` 与 `harness_id` 无关 |
| 日志清理 `qre logs` | 旁路文件随运行目录一起清理 |
| Native AOT | 源生成 JSON，无反射 |

## 6. 测试清单

- 规范化：键序、数字归一、Unicode、CRLF/LF、BOM、空目录、符号链接、超限文件。
- 构建器：缺失技能目录（`unresolved`）、重复工具名、外部清单非法 JSON。
- 存储：旁路文件缺失、被截断、与审计交叉校验失败、`public` 与 `private` 的字段差异。
- `diff`：见 A1 验收；另加双方一侧 `complete=false` 时的展示。
- CLI：`--json` 输出稳定（黄金文件）；退出码约定与现有 `Fail()` 一致。

## 7. 验收标准（整体）

1. 同一份 harness 在不同机器、不同操作系统上得到相同 `harness_id`。
2. 任意一次运行都能回答"它用的是哪个 harness"；`complete=false` 的情况被明确标出而不是被忽略。
3. 给定两个运行，`qre harness diff` 能给出可操作的差异，并正确标出提示可见类与机制类。
4. 开启 `--expect-harness-id` 时，harness 漂移在发出第一次模型请求前被拦截。
5. 对既有用户零行为变化（默认配置下只多一个旁路文件）。

## 8. 风险与开放问题

| 编号 | 风险 / 问题 | 缓解 |
|---|---|---|
| R1 | 技能由 SDK 装载，QRE 算出的目录可能与实际不一致 | `resolution` 字段与 `complete=false`；O1 核对 SDK 接口 |
| R2 | 策略规则在代码里，行为摘要依赖人工维护的实现版本常量 | 黄金哈希测试强制联动 |
| R3 | 旁路文件不在审计完整性保护内 | 与审计中的工具描述符交叉校验；其余组件视为尽力记录 |
| R4 | 外部工具清单含敏感参数 | 只记录哈希与名称，`public` 模式不记名称 |
| R5 | 运行目录被保留策略清理，基线丢失 | 长期基线走评测集（姊妹方案） |
| R6 | 范围蔓延成通用配置系统 | 本方案只做"观测 + 比较 + 固定"，不引入新的 harness 输入格式 |

开放问题：

- O1：VllmChatClient 2.0.34 是否暴露实际技能目录或技能清单。
- O2：`harness_id` 是否需要纳入 QRE 自身版本号（实现变化可能改变同一配置下的工具描述）。倾向于不纳入，而是依赖工具描述符本身的哈希；若描述符字节不变则 harness 视为不变。
- O3：是否在 `manifest.json` 里引用 `harnessId`。取决于 S0 的未知字段结论。

## 9. S0 结论（待填）

（S0 完成后追加。）
