# QueryRuntime 下一阶段开发计划

日期：2026-06-03

英文版：`docs/queryruntime-next-development-plan.md`

来源说明：本计划来自 Claude Code 对当前 QueryRuntime 分支的最终外部审查，
并合并了 Antigravity 对该计划文档的后续核查意见。本文档是
`docs/queryruntime-harness-open-source-strategy.md` 的伴随计划；策略文档
仍然是 phase 边界和发布顺序的 source of truth。

`P0` 到 `P6` 是外部审查周期和后续基线讨论中形成的工作包编号，不代表执行
顺序。第 11 节定义了合并 Claude Code、Antigravity 和基线硬化反馈后的最终
执行顺序。

## 1. 当前基线

当前分支已经完成开源 harness 基础工作的 Phase 3 第一切片：

- Phase -1、Phase 0、Phase 1、Phase 1.5、Phase 2a、Phase 2b-MVP 和
  Phase 2b-Hardening 在当前分支范围内视为已完成。
- Phase 1.6 反向依赖工作已经开始，但 CodexFlow.Core 仍需要继续迁移，才能
  完全通过稳定 adapter 消费 QRE。
- Phase 3 已经实现 trace/replay 的第一切片，但 deterministic replay hardening
  还未完成。
- 真实 `qre` CLI 路径已经成为文档中的主入口。
- CLI provider 路径通过 QRE 自有的 `QreVllmChatClientFactory` 构建 provider，
  不再依赖 Core provider factory。
- 本地 `osx-arm64` Native AOT publish 和 native `qre` smoke 已验证，但 AOT
  还不是 blocking CI gate。
- `VllmChatClient` 2.0.21 是当前预期 provider 包。Anthropic Messages
  thinking-off 回归应继续由 gated real provider tests 覆盖。
- Model clients 和 QRE engine 内部已经通过 streaming APIs 消费 provider
  responses，但 `qre run` 当前仍是在 run 完成后才输出最终 assistant text。
  Real-time CLI streaming 仍是一个 baseline UX 和 event-surface 缺口。

## 2. 已验证能力

后续 phase 应保留以下已经存在的能力：

- CLI 已提供 `qre run`、`qre trace`、`qre replay`、`qre diff`、
  `qre tool list`、`qre doctor` 和 `qre sandbox exec`。
- `.qre/runs/<run-id>/` 包含 run artifact，包括 JSONL trace、manifest、
  run summary、usage、diff、blobs 和 collected artifacts。
- Recorded replay 会读取已有 trace 数据，不调用 provider，也不执行工具。
- Docker sandbox runner 的 MVP 和 hardening 已覆盖 isolated workspace staging、
  non-root execution、denied network、read-only root filesystem、dropped
  capabilities、timeout cleanup 和 output limits。
- Tool capability policy 会在 process execution 前输出 machine-readable denial
  和 approval metadata。
- Gated live LLM tests 覆盖 OpenAI-compatible 与 Anthropic Messages 行为，
  包括 Anthropic Messages thinking-off 行为。
- Live provider tests 需要外部 credentials/endpoints，目前是 manual gated
  checkpoints，不是自动 CI gates。

## 3. 规划原则

后续工作应继续保持 QRE 是 runtime harness，而不是平台 surface：

- QRE 不应拉入 Web API、Identity/JWT、SignalR、PostgreSQL、MongoDB、Redis、
  Qdrant、notification 或 React UI 依赖，除非它们位于明确的 optional adapter
  后面。
- Public contracts 应保持小型、可序列化、AOT-compatible。
- Provider 行为必须显式。未知 provider/model 组合应清晰失败，不应静默 fallback
  到某个假定 endpoint shape。
- Replay 和 trace schemas 应在开源发布前成为 durable public contracts。
- 安全敏感的 write、process、network 和 sandbox capabilities 应通过 policy
  表达，并由 negative cases 测试覆盖。

## 4. P0：Baseline Freeze And Hardening ✅ 已完成（2026-06-03）

目标：在开始新功能开发前，把当前分支基线变成可重复验证、文档一致、并逐步
受 CI 保护的稳定起点。

状态：截至 2026-06-03，当前分支 baseline 的 P0 已完成。
current-capability acceptance matrix 位于
`docs/queryruntime-harness-open-source-strategy.md`；可执行 entrance gate 是
`scripts/queryruntime-baseline-gate.sh`。

这个阶段不应扩展成“完成所有未完成项”。它只冻结已经声称完成或本地验证过的
能力，澄清哪些内容仍是 manual 或 partial，并定义 P1/P3/P5 等后续工作的进入
条件。

范围：

- 为 Phase -1、Phase 0、Phase 1、Phase 1.5、Phase 2a、Phase 2b-MVP、
  Phase 2b-Hardening 和 Phase 3 first-slice 增加 current-capability
  acceptance matrix。
- 记录证明每个 completed slice 的精确 smoke commands 和 tests。
- 澄清 live provider tests 是 locally/gated verified checkpoints，不是自动
  CI checks。
- 澄清 QRE/Core 边界：QRE CLI/provider path 已不依赖 Core provider factory，
  但 CodexFlow.Core 还没有完全迁移为通过 stable adapters 消费 QRE。
- 澄清 replay 语义：当前 recorded replay 是第一切片，不是 fully deterministic
  replay contract。
- 澄清 CLI streaming contract：普通 `qre run` 保持稳定 final output，
  `qre run --stream` 应实时输出 assistant text，任何 machine-readable streaming
  mode 都应采用 event-safe 形态，例如 `--jsonl-stream`。
- 定义一个小的 baseline gate，后续 feature phases 开始前必须保持 green。

验收标准：

- Technical guide 和 strategy document 对 completed、partial、planned QRE
  capabilities 的描述一致。
- Baseline matrix 把每个 completed slice 映射到具体 tests、commands 或
  documented limitations。
- `dotnet test CodexFlow.QueryRuntime.slnx --no-restore` 保持 green。
- 当前 native AOT local publish/smoke command 被记录为可重复 baseline check。
- Gated live provider tests 记录所需 environment variables，并明确标记为
  non-CI checks。
- 当前 non-streaming CLI behavior 已文档化，并且 target streaming behavior
  应在 provider adapter work 开始前定义清楚。
- Streaming output 不破坏 `--json` stdout contracts；JSON event streaming
  使用单独的显式 mode 或 output channel。
- 下一步执行顺序把 P0 作为后续工作的 entrance gate。

主要风险：

- 把 partial capabilities 当成 completed，会导致后续 phase 建立在错误假设上。
- 过度扩展 baseline work 会拖慢真实 QRE 开发，却不增加新的确定性。
- 如果不区分 local/gated checks 与 CI guarantees，provider 和 replay claims
  容易被过度承诺。
- 如果实时文本直接写入 stdout 并与 machine-readable JSON output 混在一起，
  会破坏脚本消费。
- 如果 text deltas 与 tool-call assembly 不分离，tool-call streaming 可能暴露
  partial 或 malformed structured tool-call payloads。

建议测试：

- `git diff --check`。
- `dotnet test CodexFlow.QueryRuntime.slnx --no-restore`。
- Local AOT publish 加 `qre --version` smoke。
- `--stream` flag 存在后，运行 offline `qre run --stream --response ...` smoke。
- JSON contract tests 证明 `--json` 仍只输出 final result，而 `--jsonl-stream`
  输出 event-shaped 内容。
- 当 credentials/endpoints 存在时，可选运行 gated real-provider test。

## 5. P1：Provider-Neutral Model Adapters ✅ 已完成（2026-06-03）

目标：用显式 provider adapters 替代 CLI-local model-family heuristics，并把它们
放在 QRE 自有的 model adapter surface 下。

状态：截至 2026-06-03，P1 已完成。新增 `CodexFlow.QueryRuntime.Models` 项目
承载 `IQreModelProvider` adapter 抽象、`QreModelProviderSelector` 和
`QreModelApiMode` provider-neutral surface（对 `CodexFlow.Core` zero project
dependency）。CLI 的 `QreVllmChatClientFactory` 和 integration test host 都改为
委托同一个 selector，silent unknown-model fallback 已移除：未知 `--model` 或
不兼容的 `--api-mode` 会返回清晰 CLI error（exit 1，不触发 provider 调用）。
Adapter contract 由 `CodexFlow.QueryRuntime.UnitTests/Models/*` 和
`Cli/QreCliModelSelectionTests.cs` 覆盖；thinking policy / JSON output 行为仍由
既有 harness tests 守护。Native AOT publish 在变更后保持 clean（无 trim/AOT
warning）。OpenAI-compatible 与 Anthropic Messages 的 real-provider smoke 仍是
gated checkpoint（需外部 credentials/endpoints），保持 non-CI。

§12 的 packaging 开放问题已解决：adapters 先以单一 `CodexFlow.QueryRuntime.Models`
项目落地，后续可按 provider 再拆分。

范围：

- 引入 provider adapter abstraction，例如 `CodexFlow.QueryRuntime.Models.*`。
- 将 OpenAI-compatible、Anthropic Messages、Responses 和其他 provider shape
  拆分为显式 adapter。
- 从 production path 移除 silent unknown-model fallback。
- `QreVllmChatClientFactory` 只能作为临时 bridge，并且必须委托给显式 provider
  adapters。
- 保留 tools 和 JSON output 下的 thinking policy 行为。

验收标准：

- 未知 `--model` 或不兼容的 `--api-mode` 会给出清晰 CLI error。
- OpenAI-compatible real-provider smoke 通过。
- Anthropic Messages real-provider smoke 通过，并能在请求时关闭 thinking。
- Gated real LLM phase tests 保持通过。
- Adapter contract tests 覆盖 thinking policy、tool-call compatibility、
  JSON output 和 unsupported provider behavior。
- 每个 `CodexFlow.QueryRuntime.Models.*` adapter package 对 `CodexFlow.Core`
  都是 zero project dependency。
- Adapter 变更在 Native AOT publish analysis 下保持 clean，包括没有未批准的
  trim/AOT warnings。

主要风险：

- Provider 在 `tool_choice`、thinking、JSON schema 和 streaming 上的差异可能
  静默回归。
- Provider adapter 变更可能意外重新引入 Core dependencies。
- Provider adapter 变更可能引入 reflection-heavy serialization path，这类问题
  只有 AOT publish 时才会暴露。

建议测试：

- Adapter selection 和 unknown provider failure 的 focused unit tests。
- OpenAI-compatible 与 Anthropic Messages 的 gated real-provider tests。
- Adapter 变更后的 AOT CI publish smoke。

## 6. P2：Deterministic Replay Hardening ✅ 已完成（2026-06-03）

目标：完成 Phase 3，使 trace/replay 足够稳定，能用于 regression testing、
issue reproduction 和 public format documentation。

状态：截至 2026-06-03，P2 已完成。`QueryRuntimeEngine` 现在接受可注入的
`TimeProvider` 和 query-id factory（默认仍是 system clock + `Guid.NewGuid`）；
`DeterministicReplayClock` 为 strict replay 提供完全确定的时钟与时长。trace 在
`run.started` 记录和 `manifest.json` 上携带显式 `SchemaVersion`，由 Abstractions
中的 public `QueryRuntimeTraceSchema`（`CurrentVersion = 1`）定义，并配套
`QueryRuntimeReplayMode` 与 `QueryRuntimeTraceCompatibility` public DTO。新增的
`qre replay latest --strict` 以 source trace 种子化 clock/id，输出 byte-stable 的
`replayDigest`（`DeterministicReplay.ComputeCanonicalDigest`，排除 run-scoped
RunId/SessionId），并按 schema 版本 gate：legacy 无版本 trace 会以精确 reason
拒绝 strict replay，但仍可走 non-strict recorded replay；unsupported 未来版本会在
strict 与 non-strict replay 下都以面向升级的精确 reason 拒绝。Strict replay 经
`RecordedReplayModelClient` / `RecordedReplayToolPack` 保证 provider-free / tool-free。
覆盖测试：`StrictReplay_ProducesByteIdenticalDigest_AndNeverExecutesOriginalTool`、
`TraceSchema_GatesStrictReplayByVersion`、
`ReplayRecorded_RejectsUnsupportedFutureSchema_WithPreciseReason`、CLI `ReplayStrict_*`
（确定性 digest 形态 + legacy 拒绝），全部 179 个 unit tests 通过。20 分钟
Antigravity 复核在 future-schema replay gate 修复后未发现 blocking issue。

范围：

- 增加 deterministic ID generation 和 clock injection。
- 增加明确的 trace schema versioning。
- 定义 public trace/replay DTOs。
- 为旧 traces 增加 migration 或 compatibility handling。
- 硬化 `strict-replay`，确保它不会调用 providers 或执行 tools。
- 文档化 replay guarantees 和 non-guarantees。

验收标准：

- Recorded run 可以在没有 provider access 且不执行 tools 的情况下 replay。
- 对于相同 trace、runtime version 和 replay settings，strict replay output
  byte-identical。
- 旧 trace schema 要么被迁移，要么以精确的 non-strict/unsupported-version
  reason 拒绝。
- Public docs 解释 trace fields、blob references、tool result capture 和
  replay modes。
- Live rerun mode 与 strict replay 分开文档化；当 sandbox commands 依赖 clock、
  filesystem、network 或 host state 时，live rerun 允许产生差异。

主要风险：

- Schema churn 可能破坏已有 `.qre/runs` artifacts。
- 如果 trace 仍包含环境相关字段，deterministic replay 容易被过度承诺。
- 在 write tools 存在前冻结 replay，可能迫使后续为 file mutation、patch 和
  content hash events 再做 schema revision。

建议测试：

- Golden trace replay tests。
- 使用 injected clock 和 ID providers 的 cross-run determinism tests。
- Negative tests，证明 strict replay 不调用 model clients 或 sandbox runners。

## 7. P3：Native AOT Blocking CI

目标：把 Native AOT 从本地 proof 推进为 CI-protected release constraint。

范围：

- 添加 CI lane，用 `-p:PublishAot=true` publish `CodexFlow.QueryRuntime.Cli`。
- 用产出的 binary 执行 `qre --version`、`qre tool list` 和 recorded replay smoke。
- lane 稳定后，在至少两个相关 RID 上运行。
- 跟踪并阻断未批准的 trim/AOT warnings。

验收标准：

- CI 能成功 publish AOT binary。
- CI smoke 使用产出的 binary，而不是 framework-dependent CLI。
- 该 lane 先以 non-blocking 方式运行，稳定后转为 blocking。
- QRE public IO paths 没有未批准的 trim/AOT warnings。

主要风险：

- Provider 或 serialization dependencies 的 transitive reflection 可能重新出现。
- Cross-platform AOT 可能与本地 `osx-arm64` smoke 产生不同失败。

建议测试：

- CI AOT publish 和 `qre --version` smoke。
- CI recorded replay smoke。
- Linux 和 macOS 行为稳定后，增加 optional RID matrix。

## 8. P4：完成 Phase 1.6 反向依赖

目标：让 CodexFlow.Core 消费 QRE 作为 runtime，而不是让 QRE 依赖 Core
orchestration internals。

范围：

- 将 session memory、runtime hooks、context-window governance 和 recovery
  concerns 移到 QRE-facing adapters 后面。
- 确保 QRE-to-Core project references 保持不存在。
- 平台功能应作为 QRE engine 周围的 adapter，而不是放入 harness 内部。
- 通过 targeted regression tests 保留当前 Core orchestrator behavior。

验收标准：

- `CodexFlow.QueryRuntime.*` projects 不引用 `CodexFlow.Core`。
- 适用场景下，Core orchestrator tests 通过 QRE-backed runtime path。
- Public QRE contracts 不暴露 platform-only session、user、database 或
  hosted-service types。
- Reverse dependency behavior 已在 strategy 和 technical guide 中记录。

主要风险：

- 移动 runtime concerns 可能细微改变 orchestrator behavior。
- Adapter boundary 可能变得过宽，以新名字重建旧 Core surface。

建议测试：

- Project reference audit。
- Focused Core orchestrator regression tests。
- QRE contract serialization 和 AOT smoke tests。

## 9. P5：Repair Profile Write Tools 与 Run-Scoped Diff

目标：让 `--profile repair` 变得可用，同时保持 write capability 显式、
workspace-scoped 且可审计。

范围：

- 实现 workspace-only write 和 patch-apply tools。
- 默认拒绝 secret paths、parent directory writes、external mounts 和 destructive
  commands。
- 从真实 edits 生成 run-scoped `diff.patch`。
- 保留 risky operations 的 approval records。
- 当 repair work 不可信时，优先使用 Docker sandbox execution。

验收标准：

- `qre run --profile repair` 可以修改 workspace 内文件。
- Workspace 外写入会被拒绝并记录。
- Write 和 patch tools 会拒绝 symlink traversal，当 evaluated target 逃逸出
  workspace boundary 时必须失败。
- Secret-looking files 和 protected workspace artifacts 保持 guarded。
- `qre diff latest` 返回来自真实 edits 的 run-scoped patch。
- Negative policy tests 覆盖 path escape、secret paths、destructive commands
  和 approval-required operations。

主要风险：

- Write tools 会扩大模型错误的 blast radius。
- Diff generation 可能意外包含已有的 unrelated workspace changes。
- 如果不做 canonicalize 和 revalidate，symlink traversal 可绕过 naive workspace
  path checks。

建议测试：

- Workspace write allow/deny tests。
- 带 path traversal attempts 的 patch apply tests。
- Dirty worktrees 下的 run-scoped diff tests。
- Write tools 存在后的 Docker repair smoke。

## 10. P6：Phase 4 Open-Source Release Readiness

目标：让 QRE 以 runtime harness 项目形态准备公开开源发布。

范围：

- 最终确定 extraction repository shape。
- 运行 full-history secret scanning 和 license scanning。
- 添加或完成 `SECURITY.md`、`CONTRIBUTING.md`、`CODE_OF_CONDUCT.md` 和
  release docs。
- 准备 signed single-binary release artifacts 和 checksums。
- 围绕 QRE runtime harness 重新编写 README，而不是 SaaS product。
- 提供 clean-machine install 和 smoke instructions。

验收标准：

- Secret 和 license scans clean，或有 documented resolved exceptions。
- Release artifacts 已签名并生成 checksums。
- Clean machine 可以安装并运行 `qre`。
- README 说明 QRE 为什么存在、包含什么、不包含什么，以及不承诺哪些 security
  guarantees。
- Platform-only surfaces 不存在于 public harness package graph 中。

主要风险：

- Repository history 可能包含 credentials 或 local endpoints，需要在发布前
  revoke 并清理。
- Public messaging 可能在 sandbox、replay、provider-neutral 或 AOT guarantees
  尚未 CI-protected 前过度承诺。

建议测试：

- Clean checkout smoke。
- Secret/license scan gates。
- Release artifact verification。
- Package graph audit。

## 11. 建议执行顺序

合并后的推荐顺序是：

1. ✅ P0 baseline freeze and hardening。（已完成 2026-06-03）
2. ◻ P3 Native AOT blocking CI。（AOT CI lane 已存在于 `.github/workflows/ci.yml`
   的 `aot-smoke` job，会 publish AOT binary 并对产出 binary 跑 smoke；尚需确认
   该 lane 是否已作为 required/blocking check。）
3. ✅ P1 provider-neutral model adapters。（已完成 2026-06-03）
4. ◻ P5 repair profile write tools and run-scoped diff。
5. ✅ P2 deterministic replay hardening。（已完成 2026-06-03）
6. ◻ P4 complete Phase 1.6 reverse dependency。
7. ◻ P6 open-source release readiness。

该顺序是刻意安排的：

- Baseline freeze 应先执行，确保后续 phase 建立在验证过的当前事实上，而不是
  乐观文档上。
- AOT CI 应在 provider adapters、trace DTOs 或 public serialization contracts
  重构前成为 blocking。
- Provider adapters 应在 AOT checks 已经守住 provider selection、serialization
  和 dependency graph changes 后再稳定下来。
- Repair write tools 应在 replay schema 冻结前存在，因为 workspace mutation、
  file hashes 和 patch events 必须成为 durable trace model 的一部分。
- Replay hardening 应在 write-tool event surface 已知后执行，并早于 public
  trace format documentation 和 benchmark claims。
- Core reverse dependency completion 应在 extraction 前完成。
- Release readiness 应最后执行，因为它依赖 clean package graph、security
  posture 和稳定 public messaging。

## 12. 开放问题

- P0 已解决：acceptance matrix 位于
  `docs/queryruntime-harness-open-source-strategy.md`，本文档只链接该 source，
  不重复维护矩阵。
- P1 已解决：Provider adapters 先落在单一 `CodexFlow.QueryRuntime.Models` 项目
  中（对 Core zero project dependency），后续可按 provider 再拆成 separate
  packages。
- 第一个 AOT CI gate 应强制哪些 RIDs？
- 旧 traces 应自动迁移，还是第一个 public trace version 应以清晰 reason 拒绝
  pre-public traces？
- 是否应在 write tools 前先做最小 replay schema compatibility pass，而最终
  deterministic replay freeze 等到 write tools 后再执行？
- `repair` profile 对 `PROJECT_SUMMARY.md`、`.env*` 和 generated artifacts 等
  protected files 应有多严格？
- Write-capable repair work 是否必须使用 Docker sandbox，还是 Docker 作为
  recommended stronger mode，而 local repair 在 explicit approval 下仍可允许？
