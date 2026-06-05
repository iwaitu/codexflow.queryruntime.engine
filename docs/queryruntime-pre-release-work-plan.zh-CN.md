# QueryRuntime Pre-Release 工作计划

日期：2026-06-04

英文版：`docs/queryruntime-pre-release-work-plan.md`

已归档的完成态开发计划：
`docs/archive/queryruntime-next-development-plan.completed-2026-06-04.zh-CN.md`

## 1. 发布定位

独立 QueryRuntime Engine 的核心 runtime harness 已经达到功能闭环：

- 已具备 `qre run`、trace、replay、rerun、diff、doctor、tool list 和
  sandbox exec。
- 已具备 `none`、`readonly`、`verify` 和 `repair` profile。
- Provider-neutral model adapters 与 thinking policy 已接入。
- Recorded replay 和 run artifacts 已成为一等 runtime surface。
- Docker sandbox hardening 已支持 opt-in 隔离执行。
- `repair` 已暴露受控 workspace write tools，并生成 run-scoped
  `diff.patch`。
- Antigravity 在 P5 repair 中发现的安全问题已修复并通过复核。

本计划把功能开发视为基本完成，后续进入 pre-release 稳定阶段。目标是让当前
QRE 可发布、可消费，并且不容易被后续改动回归破坏。

## 2. 优先级顺序

### R0：冻结发布基线

目标：定义即将作为 pre-release baseline 的代码、文档和验证面。

范围：

- 保持 QRE 独立于 `CodexFlow.Core` 和 `CodexFlow.Contracts`。
- 执行并记录 release baseline checks。
- 确认活跃文档指向本 pre-release 计划，完成态 phase 计划进入归档。
- 明确记录所有不阻塞发布的已知限制。

验收：

- `git diff --check` 通过。
- `dotnet test CodexFlow.QueryRuntime.slnx --no-restore` 通过。
- `rg -n "CodexFlow\.(Core|Contracts)" --glob "*.cs" --glob "*.csproj" --glob "*.slnx"`
  不返回 source/project 耦合。

### R1：让 Native AOT CI 成为 blocking

目标：避免 native binary 无法 publish 或 smoke 时仍能合并代码。

范围：

- 确认 `linux-x64` Native AOT lane 会 publish `qre` binary。
- 确认该 lane 会对产出 binary 执行 native smoke。
- 把 AOT lane 设置为受保护分支的 required blocking GitHub check。
- 保留本地 AOT smoke commands，方便 maintainer 复现。
- 调查并解决任何 `linux-x64` blocked 或 pending-check 状态。

验收：

- CI 显示 AOT smoke lane 是绿色 required check。
- 产出的 Linux binary 能运行 `qre --version` 和最小 static `qre run`
  smoke。
- Branch protection 要求 AOT check 通过后才能 merge。

### R2：硬化 Repair 与 Artifact 边界

目标：让新的 write-capable profile 达到 pre-release 可用的安全水平。

范围：

- 在 write tools 已存在后补 Docker repair smoke 覆盖。
- 增加 same-path dirty-baseline 测试，明确目标文件在 repair run 前已有
  uncommitted changes 时，`diff.patch` 的行为。
- 持续用 negative tests 覆盖 `.git`、`.qre`、secret-looking paths、path
  traversal 和 symlink-chain escape。
- 决定 richer patch formats 是否要阻塞 pre-release；否则 targeted text
  replacement 作为第一版支持面。

验收：

- Local repair profile tests 通过。
- Docker repair smoke 已自动化，或明确记录为 gated manual check。
- `diff.patch` 的限制已文档化，并且稳定行为有测试覆盖。

### R3：Package 与 Distribution Readiness

目标：让 CLI 可以被消费，而不是通过 source-level coupling 使用。

范围：

- 定义 pre-release artifact 形态：native binary、archive name、checksums
  和 target runtime identifiers。
- 确认首个 pre-release 的 RID matrix。
- 下游 integration contract 保持 binary-first：CodexFlow 应消费已安装的
  QRE binary 或 package adapter，而不是 project references。
- 验证 packaged output 中的 `--version`、`doctor` 和 static `run`。

验收：

- Release workflow 可以产出命名 artifacts。
- Release artifacts 包含 native `qre` binary 和 checksum metadata。
- 干净 checkout 或临时目录可以执行 packaged binary smoke。

### R4：文档与用户入口打磨

目标：让新的 maintainer 或 adopter 能理解 pre-release 的真实能力边界。

范围：

- 更新 README quick start，覆盖 provider configuration、tool profiles、
  sandbox modes 和 repair behavior。
- 保持英文/中文活跃 release plan 与核心 usage docs 对齐。
- 增加简短 limitations section，覆盖 live-provider tests、Docker-gated
  tests、usage estimation、replay determinism 和 MCP stdio 限制。
- 确保 security-sensitive docs 链接到 `docs/threat-model.md` 和
  `docs/tool-capabilities.md`。

验收：

- README 指向 pre-release 工作计划，而不是归档开发计划。
- 关键限制在发布前可见。
- 文档中不再存在 `repair` 尚未接写工具的陈旧描述。

### R5：下游集成就绪

目标：在 standalone engine 稳定后，为 CodexFlow 消费 QRE 做准备。

CodexFlow 集成前的安全与 contract 改进项见
`docs/codexflow-integration-hardening-plan.zh-CN.md`。该计划中的 host
intervention、stop gate、result metadata、trace path containment 和 package
source mapping 是继续扩大 CodexFlow 侧整合前的前置条件。

范围：

- 对任何声明“可作为 CodexFlow backend 使用”的 NuGet package 或 release notes，
  把 CodexFlow integration hardening 作为 blocking milestone。
- 在 QRE 仓库内完成 `docs/codexflow-integration-hardening-plan.zh-CN.md` 中的
  H0-H4 与 H6：host intervention contracts、engine tool-intervention
  execution、before-stop gates、downstream result/event metadata、trace path
  containment、可复用 downstream adapter tests。
- 发布 CodexFlow-consumable package 前完成 H5 package source 与 provenance 文档。
- 定义 CodexFlow 调用 QRE 的 integration contract。
- Contract 保持 CLI/binary 或 package based。
- 避免把 `CodexFlow.Core` 或 `CodexFlow.Contracts` reference 重新引回本仓库。
- 把 CodexFlow 侧迁移工作和 QRE release work 分开记录。

验收：

- QRE repo 保持 standalone。
- H0-H4 与 H6 在 `CodexFlow.QueryRuntime.slnx` 中有通过的 unit tests。
- 作为 downstream adapter test kit 的 test fixtures 或 examples 进入 CI build /
  validation。
- Integration notes 描述 inputs、outputs、errors、artifact locations，以及
  unsupported host semantics 的 fail-closed 行为。
- 本地和生产 package 消费都有 package-source mapping 指南。
- Antigravity 复核不再把 hook bypass、stop-gate bypass、trace containment 或
  package-source mapping 报为 Critical/High blocker。
- CodexFlow-specific migration code 不进入本 QRE release baseline。

## 3. Release Candidate Gate

切第一个 pre-release tag 前执行：

```bash
git diff --check
dotnet test CodexFlow.QueryRuntime.slnx --no-restore
rg -n "CodexFlow\\.(Core|Contracts)" --glob "*.cs" --glob "*.csproj" --glob "*.slnx"
dotnet publish CodexFlow.QueryRuntime.Cli -c Release -r linux-x64 -p:PublishAot=true -p:SelfContained=true
```

然后运行产出的 native binary：

```bash
./CodexFlow.QueryRuntime.Cli/bin/Release/net10.0/linux-x64/publish/qre --version
./CodexFlow.QueryRuntime.Cli/bin/Release/net10.0/linux-x64/publish/qre run --workspace . --profile none --response "pre-release smoke" "smoke"
```

如果 Docker 可用，还应执行当前 test suite 文档化的 Docker sandbox smoke 和
repair-profile smoke。

对于声明为 CodexFlow-consumable 的 pre-release package，还必须执行 R5
host-integration contract tests，并确认 downstream adapter test kit 已进入 CI。

## 4. 非阻塞后续项

除非 release reviewer 将其提升为 blocking，否则以下事项不阻塞首个
pre-release：

- Targeted text replacement 之外的 richer patch formats。
- Fully deterministic replay contract hardening。
- 完整 MCP stdio initialize lifecycle。
- Kubernetes 或 remote sandbox runners。
- Billing-grade usage accounting。
- 完整 CodexFlow 下游迁移。

## 5. 完成定义

满足以下条件时，可以准备 pre-release：

- Native AOT CI 是 required green check。
- Release artifact 可以下载，并在 source tree 外完成 smoke。
- Repair write tools 已由 containment 和 negative-path tests 覆盖。
- README 和文档描述真实支持面与限制。
- 本 repo 没有对 `CodexFlow.Core` 或 `CodexFlow.Contracts` 的 source-level
  dependency。
- 任何声明 CodexFlow-consumable 的 package 都包含 host intervention、
  before-stop gates、result/event metadata、trace path containment 和 downstream
  adapter test kit。
- 已知非阻塞缺口已明确记录。

## 6. Baseline 日志

### 2026-06-04 R0 baseline freeze

在 `/Users/iwaitu/github/codexflow.queryruntime.engine` 执行：

- `git diff --check` 通过。
- `dotnet test CodexFlow.QueryRuntime.slnx --no-restore` 通过：
  `CodexFlow.QueryRuntime.UnitTests` 报告 192 passed；gated integration
  tests 报告 13 skipped。
- `rg -n "CodexFlow\.(Core|Contracts)" --glob "*.cs" --glob "*.csproj" --glob "*.slnx"`
  没有返回 source/project 耦合。

R2 hardening 期间记录的已知 pre-release 限制：run-scoped `diff.patch` 会按
repair edits 路径收敛；但 same-path dirty baseline 会表示为该文件从 `HEAD`
到最终状态的 diff。

### 2026-06-04 R1/R3 local artifact smoke

在本地 `osx-arm64` host 执行
`scripts/queryruntime-baseline-gate.sh --include-aot`：

- `git diff --check` 通过。
- `dotnet test CodexFlow.QueryRuntime.slnx --no-restore` 通过：
  `CodexFlow.QueryRuntime.UnitTests` 报告 193 passed；gated integration
  tests 报告 13 skipped。
- `osx-arm64` Native AOT publish 完成，没有 trim/AOT warnings。
- Native binary smoke 覆盖 `qre --version`、offline `qre run`、tool list、
  recorded replay 和 strict replay digest determinism，全部通过。

另外把产出的 `osx-arm64` binary 本地打成 `qre-osx-arm64.tar.gz`，在 publish
目录外解压，并验证 packaged `qre --version`、`qre doctor --json` 和 static
`qre run --json`。
