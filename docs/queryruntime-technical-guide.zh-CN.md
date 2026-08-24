> **简体中文** | [English](queryruntime-technical-guide.md)

# CodexFlow QueryRuntime 技术说明

## 1. 定位

CodexFlow QueryRuntime 是一个面向 Agent 开发的 runtime harness。它不是
另一个完整的 AI 编程应用，也不是一套必须绑定 Web UI、账号系统、数据库和
SaaS 部署形态的平台。它的核心目标是把“模型调用、工具调用、执行策略、
trace、replay、sandbox、CLI 自动化”这些 Agent 基础设施抽出来，形成一个
可嵌入、可测试、可扩展、可跨平台发布的运行时。

当前分支上的实现仍处于实验阶段，但已经具备一个可以验证方向的最小切片：

- `CodexFlow.QueryRuntime.Abstractions`：Phase 1 第一批稳定 contract，包括
  runtime、model、tool registry、trace store、sandbox runner 以及 CLI option DTO。
- `CodexFlow.QueryRuntime.Experimental`：对现有 `QueryRuntimeEngine` 的轻量封装。
- `CodexFlow.QueryRuntime.Cli`：实验性 `qre` CLI；当前文档以 `qre ...`
  作为主入口，也已在本分支验证 `osx-arm64` Native AOT publish 后的本地二进制。
- `CodexFlow.QueryRuntime.Sandbox.LocalProcess`：可信本地开发用的
  `ISandboxRunner` 实现，不是安全隔离边界。
- run artifact：每次运行写入 `.qre/runs/<run-id>/events.jsonl`、
  `manifest.json`、`run.json`、`diff.patch`、`usage.json` 和 `artifacts/`。
  大 payload 会落到 `blobs/sha256/...`，trace 中只保留 digest metadata。
- 只读工具包：`qre_list_files`、`qre_read_file`、`qre_search_files`。
- verify 工具包雏形：`qre_git_status`、`qre_git_diff`、
  `qre_dotnet_build`、`qre_dotnet_test`。
- Provider 适配：通过 `Microsoft.Extensions.AI.IChatClient` 和 QRE CLI 自有的
  `QreVllmChatClientFactory` 接入若干已识别模型族的 OpenAI-compatible /
  Responses / Anthropic Messages 风格接口。当前还不是完全 provider-neutral
  的通用 adapter。
- 默认模型策略：当启用工具或要求模型 JSON 输出时，默认关闭 thinking，
  以提高工具调用和 schema 输出的兼容性。
- `--json` 机器输出、`qre trace latest --jsonl`，以及
  `qre replay latest --summary` 的只读摘要模式。默认 public trace 仅支持摘要；
  recorded replay 只接受显式启用的 full-fidelity private/sanitized trace。
- `qre diff latest` 优先读取 latest run 的 run-scoped `diff.patch`；没有
  run patch 时才回退到 workspace git diff。
- 外部工具 manifest：`.qre/tools/*.json` 可声明 `stdio` 或最小
  `mcp-stdio` 工具，通过 `qre run --external` 进入工具面。
- `qre --version`、`qre init --json` 和 `qre doctor --json` 的发布/环境
  诊断入口。
- `qre sandbox exec --profile verify` 的 policy-gated trusted-local 命令执行
  入口。

状态标记：

- **Today**：当前仓库中已经能运行或已经在本分支验证过的能力。
- **Planned**：开源 harness 目标能力，还需要后续实现或抽取。
- **Risk**：发布前必须讲清楚的限制、误用风险或安全边界。

命令约定：

- **Today**：用户面和技术说明中的主命令是 `qre ...`，包括 `qre run ...`、
  `qre trace ...`、`qre replay ...`、`qre sandbox exec ...`。
- 本仓库内需要重新生成 native CLI 时，先执行 `dotnet publish`，再把 publish
  目录加入 `PATH` 或通过 `QRE_BIN` 指向生成的 `qre`。
- 源码调试可以直接运行 CLI 项目，但它不再作为本文档的主路径；技术说明和
  外部集成示例都应依赖稳定的 `qre` 可执行文件。

## 2. 解决的问题

很多 Agent 项目会卡在一个相似的位置：demo 很容易写，但要变成可测试、
可审计、可复现、可安全运行的工程基础设施很难。典型问题包括：

- LLM provider 差异很大，工具调用、JSON schema、thinking 模式行为不一致。
- 工具执行缺少边界，读文件、写文件、跑命令、访问网络经常混在一起。
- Agent 运行失败后缺少可复现上下文，只能看终端日志猜测。
- CLI 自动化和应用内嵌 runtime 往往是两套逻辑，难以共享。
- 本地执行、Docker sandbox、Kubernetes runner 的抽象边界不清晰。
- Native AOT 发布、跨平台 CLI、插件加载之间存在天然张力。

QueryRuntime 的价值在于提供一个中间层：比“几十行 Agent demo”更工程化，
又比完整 SaaS 平台更轻。开发者可以把它作为自己的 Agent 产品底座，也可以
只把 `qre` 当作 CI、代码库分析、工具执行验证和 replay 调试工具。

## 3. 适用场景

### 3.1 本地代码库分析

**Today**：开发者可以在任意 repo 中通过实验 CLI 运行只读分析，让模型读取
仓库结构、搜索文件、总结架构风险或生成迁移建议。当前已实现的工具是只读
工具，所以适合做分析型工作。

**Today**：这类任务的目标命令已经是 `qre run --profile readonly ...`。

适合的问题：

- “分析这个仓库的模块边界。”
- “找出潜在的配置泄漏和安全风险。”
- “解释为什么测试结构难以维护。”
- “给出下一阶段重构计划。”

### 3.2 Agent 工具调用验证

**Today**：QueryRuntime 可以把模型请求、模型响应、工具请求、工具结果统一
记录到 JSONL trace，便于做回归测试。

模型调用工具时容易受到 provider 格式、tool schema、thinking 模式影响。

适合的问题：

- 某个模型在启用 tools 后是否仍会输出可解析工具调用。
- 某个 provider 是否支持 JSON schema / response format。
- 关闭 thinking 后工具调用稳定性是否提升。
- 工具调用失败后 runtime 是否能给出合理终止原因。

### 3.3 CI 或自动化脚本中的只读审查

**Today**：`--json` 输出已经可以被脚本消费，适合离线 smoke 和只读审查。
仓库中已经有 `.github/workflows/queryruntime-harness.yml` 作为 harness-only CI
雏形，只验证 QueryRuntime slice，不启动平台依赖。

`--json` 输出使 CLI 可以被脚本消费。CI 可以运行一次只读分析，把结果写入
artifact，后续再由别的系统决定是否阻断构建。

适合的问题：

- Pull Request 进入队列后先跑只读架构审查。
- 每晚对仓库做依赖风险、TODO、测试缺口扫描。
- 把 `.qre/runs/<run-id>/events.jsonl` 作为调试 artifact 上传，但上传前应
  做脱敏或访问控制。

### 3.4 教学、评测和 replay

**Today**：实验 CLI 默认写 `PublicRedacted / SummaryOnly` trace；
`replay latest --summary` 可安全读取摘要，而对 summary-only trace 发起 recorded replay
会 fail closed。只有显式使用 `--trace-data sanitized` 的已审查 fixture，或访问受控的
`--trace-data private` 诊断轨迹，才具备 full-fidelity recorded replay 数据；回放过程
不调用 provider，也不执行原始工具。

Agent 开发最难调试的是“这次为什么这么回答”。Recorded replay 已能重放一条
provider-free / tool-free 决策轨迹；`replay latest --strict` 进一步加入
deterministic clock + query-id 注入，以及显式的 trace `SchemaVersion`，对同一
source trace 与同一 runtime 版本的多次 strict replay 产出 byte-identical 的
canonical `replayDigest`（见 §5.8）。Strict replay 会按 schema 版本 gate：旧的
无版本 trace 和不支持的未来版本会以精确 reason 拒绝，而不是做非确定性重放。

Deterministic strict replay 可以用于：

- 复现一次 Agent 决策轨迹。
- 对比不同 runtime policy 的行为。
- 构造公开 benchmark。
- 在 issue 中附带可脱敏 trace，减少“无法复现”的沟通成本。

### 3.5 跨平台 Agent 产品底座

**Planned**：稳定 NuGet 包、独立 `qre` binary 和 sandbox runner 完成后，
QueryRuntime 才适合作为外部产品的正式 runtime 依赖。

目标形态下，QueryRuntime 可以被桌面应用、IDE 插件、CLI 工具、Web 后端、
CI runner 或企业内网平台嵌入。它适合做“Agent 开发组件”，而不是把所有
功能塞进一个应用。

典型组合：

- 桌面应用：UI 负责交互，QueryRuntime 负责模型循环和工具执行。
- IDE 插件：插件负责编辑器上下文，QueryRuntime 负责 trace、policy 和 replay。
- CI 服务：runner 负责 job 生命周期，QueryRuntime 负责分析和工具执行。
- 企业平台：平台负责权限和审计，QueryRuntime 作为可控执行引擎。

## 4. 当前架构

```text
User / CLI
  -> CodexFlow.QueryRuntime.Cli
  -> CodexFlow.QueryRuntime.Abstractions
  -> CodexFlow.QueryRuntime.Experimental
  -> CodexFlow.QueryRuntime.Engine/QueryRuntimeEngine
  -> IExperimentalModelClient
  -> Microsoft.Extensions.AI.IChatClient / Static client
  -> AIFunction tools
  -> JsonlTraceEventSink
  -> .qre/runs/<run-id>/events.jsonl
```

关键对象：

- `CodexFlow.QueryRuntime.Abstractions.IQueryRuntimeEngine`：Phase 1 第一批
  稳定 runtime contract，公开入口是
  `RunAsync(QueryRuntimeRequest, CancellationToken)`。
- `QueryRuntimeRequest` / `QueryRuntimeResult`：面向外部调用者的最小请求和
  结果 DTO，不暴露 Core runtime 的 session、worker、memory、hook 细节。
- `IModelClient`、`IToolRegistry`、`ITraceStore`、`ISandboxRunner`：目标公共
  扩展点，当前已有 contract，具体实现仍在分阶段迁移。
- `ExperimentalQueryRuntimeHarness`：实验性 facade，接收 prompt、workspace、
  max rounds、tool list、thinking policy 和 chat options；同时已经实现稳定
  `IQueryRuntimeEngine` contract，用于 Phase 1 迁移。
- `IExperimentalModelClient`：当前实验层的模型客户端抽象。
- `ChatClientExperimentalModelClient`：把 `IChatClient` 适配到实验 runtime。
- `StaticExperimentalModelClient`：离线 smoke 测试客户端，不访问网络。
- `ExperimentalReadOnlyToolPack`：当前内置只读工具包。
- `ExperimentalVerifyToolPack`：当前内置 verify 工具包，通过
  `LocalProcessSandboxRunner` 运行 `git status`、`git diff` 和
  `dotnet build/test --no-restore`。
- `ExperimentalToolRegistry`：实验性 tool registry，返回工具描述和 capability
  metadata。
- `ExperimentalCapabilityPolicy`：实验性 capability policy，在 verify 工具执行
  前判断 profile、capabilities、command、network、mount 是否允许。
- `JsonlTraceStore`：当前最小 `ITraceStore` 实现，用于读取 latest run 的
  JSONL summary。
- `JsonlTraceEventSink`：将 runtime event 写成 JSONL trace。
- `QreModelExecutionPolicy`：统一处理 thinking 策略，默认在 tools / JSON
  输出时关闭 thinking。

当前 CLI 配置对象：

- `QueryRuntimeProviderOptions`：provider endpoint、key、model、api mode
  或静态响应。
- `QueryRuntimeToolProfile`：工具 profile，当前支持 `none`、`readonly`、
  `verify` 和 `repair`；`repair` 暴露受控 workspace write tools，并生成
  run-scoped diff artifact。
- `QueryRuntimeModelPolicyOptions`：模型执行策略，当前主要是 thinking policy。
- `QueryRuntimeOutputOptions`：区分模型 JSON 输出和 CLI JSON 输出。
- `QueryRuntimeExecutionOptions`：运行轮数等 runtime 参数。

这些配置对象现在位于 `CodexFlow.QueryRuntime.Abstractions`，CLI 只是消费
同一套公共 DTO。后续外部 host 可以复用这些配置对象，而不是解析 CLI 的
内部类型。

## 5. 使用方法

### 5.1 基础环境

**Today**：当前仓库使用 `net10.0`。CLI 主入口是 `qre`；AOT smoke 时先把
publish 目录加入 `PATH`，再按普通 `qre ...` 命令运行。

当前仓库使用 `net10.0`。在仓库根目录执行：

```bash
dotnet --version
dotnet build CodexFlow.QueryRuntime.slnx --no-restore
```

查看 CLI 版本和本机诊断：

```bash
qre --version
qre init --workspace . --json
qre doctor --workspace . --json
```

本地 Native AOT publish 和基础 smoke：

```bash
dotnet publish CodexFlow.QueryRuntime.Cli \
  -c Release \
  -r osx-arm64 \
  -p:PublishAot=true \
  -p:SelfContained=true

export PATH="$PWD/CodexFlow.QueryRuntime.Cli/bin/Release/net10.0/osx-arm64/publish:$PATH"

qre --version
qre run --workspace . --response "offline smoke" --json "analyze this repo"
```

P0 baseline gate 可以用脚本统一执行：

```bash
scripts/queryruntime-baseline-gate.sh
scripts/queryruntime-baseline-gate.sh --full
```

默认 gate 运行 `git diff --check` 和
`dotnet test CodexFlow.QueryRuntime.slnx --no-restore`。`--full` 会额外运行本地
Native AOT publish 和 native `qre --version` smoke。Docker sandbox 和真实
provider 检查保持显式 gated：

```bash
scripts/queryruntime-baseline-gate.sh --include-docker
RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true \
  scripts/queryruntime-baseline-gate.sh --include-real-provider
```

`init` 会创建 `.qre/config.toml` 和 `.qre/README.md`。模板只记录环境变量名和
本地默认 profile，不写入 API key，也不会覆盖已有模板，除非传入 `--force`。
当前阶段 CLI 仍以环境变量和命令行参数为真实 provider 配置来源；
`.qre/config.toml` 是 workspace scaffold，不是已完成的配置读取链路。

`doctor` 不调用模型、不执行项目构建、不读取 API key 值本身。它只检查
workspace、`dotnet`、`git`、provider 环境变量是否齐全，以及是否存在最新
`.qre` trace。

如果只想离线验证 CLI 和 trace，不需要任何 LLM key：

```bash
qre run --workspace . --response "offline smoke" "analyze this repo"
```

### 5.2 离线 smoke 模式

**Today**：这是当前最稳定的无网络验证方式。

`--response` 会使用静态模型响应，不访问网络，适合验证 CLI、trace、JSON 输出
和脚本集成。

```bash
qre run --workspace . \
  --response "offline smoke" \
  --json \
  "analyze architecture risks"
```

输出示例：

```json
{"type":"qre.run.completed","finalText":"offline smoke","runId":"20260602145703992","termination":"NoToolCalls","profile":"none","tools":[],"workspacePath":"/repo","traceFilePath":"/repo/.qre/runs/20260602145703992/events.jsonl","runDirectory":"/repo/.qre/runs/20260602145703992","manifestPath":"/repo/.qre/runs/20260602145703992/manifest.json","totalRounds":1,"totalToolCalls":0,"totalDurationMs":52}
```

### 5.3 真实 LLM provider 模式

**Today**：CLI 真实 provider 路径由
`CodexFlow.QueryRuntime.Cli/QreVllmChatClientFactory.cs` 创建 client，不再依赖
`CodexFlow.Core` 的 provider factory。这个 factory 仍会根据 model name 识别
Qwen、OpenAI GPT、Gemini、Claude、Kimi、MiniMax、GLM、DeepSeek 等模型族，
未知模型目前会落到既有默认 client，而不是严格的 provider-neutral adapter。
因此它适合本分支 spike 和已验证模型族，不应宣传成通用 provider 抽象。

**Planned**：后续应把 provider-neutral configuration 和 concrete model
adapters 移到 `CodexFlow.QueryRuntime.Models.*` 包，并让未知 provider 失败得
更显式。

**Risk**：使用真实 provider 时，prompt、模型上下文和工具读取到的文件内容
会发送到你配置的 endpoint。不要在未评估 provider / proxy 数据策略前，对
敏感私有仓库运行真实 LLM 分析。

CLI 支持命令行参数和环境变量两种方式配置 provider：

```bash
export QRE_API_URL="https://your-provider.example/v1"
export QRE_API_KEY="..."
export QRE_MODEL="your-model"
export QRE_API_MODE="chat-completions"

qre run --workspace . "summarize the repository architecture"
```

本仓库还提供 gated real-provider integration tests。默认测试会跳过真实模型；
需要真实验证时显式开启：

```bash
RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true dotnet test \
  CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj \
  --filter "FullyQualifiedName~ExperimentalHarnessRealLlmPhaseTests" \
  --logger "console;verbosity=detailed"
```

2026-06-03 本分支验证结果：5 个 `ExperimentalHarnessRealLlmPhaseTests` 全部
通过，使用项目 appsettings 中的 `deepseek-v4-pro` / `AnthropicMessages`
配置，覆盖 provider streaming、Anthropic Messages thinking-off、无工具 trace、
readonly 工具调用。

Native AOT `qre` 真实 provider smoke 已验证两条路径：

- Anthropic Messages compatible endpoint 已通过 `VllmChatClient` 2.0.21 验证：
  `ThinkingEnabled=false` 会发送 `thinking: { "type": "disabled" }`，QRE
  `--thinking off` 的真实 smoke trace 只包含固定 assistant text。
- OpenAI-compatible `chat-completions` endpoint 已验证 `--thinking off` 下不会
  泄露 thinking 文本，trace 中 `ThinkingTextLength` 为 `null`。

Native AOT + OpenAI-compatible smoke 示例：

```bash
export QRE_API_URL="https://dashscope.aliyuncs.com/compatible-mode/v1"
export QRE_API_KEY="..."
export QRE_MODEL="deepseek-v4-pro"
export QRE_API_MODE="chat-completions"

qre run --workspace /tmp/qre-smoke \
  --profile none \
  --thinking off \
  --json \
  "只输出以下固定文本，不要添加任何其它字符：OPENAI_COMPAT_OK"
```

等价的命令行参数：

```bash
qre run --workspace . \
  --api-url "https://your-provider.example/v1" \
  --api-key "$QRE_API_KEY" \
  --model "your-model" \
  --api-mode "chat-completions" \
  "summarize the repository architecture"
```

当前 `--api-mode` 主要用于选择现有 provider factory 的调用风格，但它不会
消除模型族 client 的差异。常见值包括：

- `chat-completions`
- `responses`
- `anthropic-messages`

### 5.4 启用只读工具

当前 `readonly` profile 包含三个工具：

- `qre_list_files`
- `qre_read_file`
- `qre_search_files`

当前 `verify` profile 会包含 readonly 工具，并额外提供：

- `qre_git_status`
- `qre_git_diff`
- `qre_dotnet_build`
- `qre_dotnet_test`

`verify` profile 当前仍是 trusted local execution，不是 Docker sandbox。它的
默认 build/test 命令使用 `--no-restore`，避免在这一阶段隐式触发
restore/network/package script 行为。

verify 工具执行前会经过 `ExperimentalCapabilityPolicy`：

- `qre_git_status` 只能运行 `git status --short`。
- `qre_git_diff` 只能运行 `git diff ...`。
- `qre_dotnet_test` 只能运行 `dotnet test ... --no-restore`。
- `qre_dotnet_build` 只能运行 `dotnet build ... --no-restore`。
- network policy 必须是 `deny`。
- `readonly` profile 不允许 process execution。
- `repair` 暴露受控 file tools（`qre_write_file`、`qre_apply_patch`），不暴露
  任意 shell execution；这些工具会拒绝 workspace escape、symlink escape、
  `.git` / `.qre` artifacts 和 secret-looking paths。

这仍是应用层 policy，不是 OS 级隔离。`LocalProcessSandboxRunner` 不会真的
阻断进程网络访问或 mount 行为；这些需要 Docker/Kubernetes/VM runner 才能
成为可信执行边界。

`repair` profile 现在暴露受控 file tools：`qre_write_file` 和
`qre_apply_patch`，而不是任意 shell execution。这些工具使用 canonical
workspace path checks，拒绝 symlink escape，拒绝 `.git` / `.qre` 和
secret-looking paths，在 policy 中要求 read-write workspace mount，并且会在
写入前写出 `policy.decision` trace records。

当 verify 或 repair profile 的工具由 harness 根据 profile 构建时，policy 评估
会写入同一个 `.qre/runs/<run-id>/events.jsonl`，事件类型为
`policy.decision`。

也可以不执行工具，直接查询 policy decision：

```bash
qre policy check --workspace . \
  --profile verify \
  --tool qre_dotnet_test \
  --json \
  -- dotnet test CodexFlow.QueryRuntime.slnx --no-restore
```

如果去掉 `--no-restore`，当前 policy 会返回 `Deny`。`policy check` 本身只
表示“评估完成”，因此 JSON 输出里的 `allowed` / `decision` 才是自动化系统
应读取的判断结果。

也可以通过 CLI 直接执行受 policy 限制的 trusted-local 命令：

```bash
qre sandbox exec --workspace . \
  --profile verify \
  --json \
  -- git status --short
```

`sandbox exec` 当前不会启动 shell，也不会允许任意命令。它只把命令映射到
当前内置 verify 工具描述符，再经过 `ExperimentalCapabilityPolicy` 判断。
例如 `dotnet test` 缺少 `--no-restore` 时会被拒绝，且不会启动
`LocalProcessSandboxRunner`。

查看工具：

```bash
qre tool list --workspace . --profile readonly --json
```

查看 verify 工具和 capability metadata：

```bash
qre tool list --workspace . --profile verify --json
```

运行只读分析：

```bash
qre run --workspace . \
  --profile readonly \
  --max-rounds 3 \
  "Find the most important runtime entry points and explain them."
```

运行 trusted local verify 分析：

```bash
qre run --workspace . \
  --profile verify \
  --max-rounds 4 \
  "Run the focused QueryRuntime tests and summarize failures."
```

`--tools` 仍作为 `--profile` 的兼容别名保留，但后续文档和公开 CLI 语义应
优先使用 `--profile`。原因是 profile 不只是工具集合，还会承载 sandbox、
capability、approval 和 budget 策略。

### 5.5 外部 stdio / MCP 工具 manifest

**Today**：外部工具走 manifest-first、out-of-process 模型，不把第三方 DLL
动态加载进 Native AOT CLI path。

workspace 下可放置：

```text
.qre/tools/<tool-name>.json
```

推荐通过注册命令把 manifest 校验并复制到 workspace-local registry：

```bash
qre tool register --workspace . --manifest path/to/tool.json
```

最小 `stdio` manifest 示例：

```json
{
  "name": "demo_external_tool",
  "description": "Demo external stdio tool.",
  "transport": "stdio",
  "command": "/bin/sh",
  "args": ["-c", "cat >/tmp/qre-tool-request.json; printf '{\"result\":\"ok\"}'"],
  "capabilities": ["read_fs"],
  "timeoutSeconds": 30,
  "maxOutputBytes": 200000,
  "inputSchema": {
    "type": "object",
    "properties": {
      "message": { "type": "string" }
    }
  }
}
```

查看外部工具描述：

```bash
qre tool list --workspace . --profile readonly --external --json
```

运行时启用外部工具：

```bash
qre run --workspace . --profile readonly --external "call the external tool"
```

对于 Python 项目，普通函数应该适配到这个 manifest surface，而不是在 QRE
外部拦截 tool call。`examples/PythonFunctionTools` 提供一个最小模式：

```python
@qre_tool(name="py_count_files", capabilities=["read_fs"])
def count_files(workspace_path: str, extension: str = ".py") -> dict[str, object]:
    ...
```

Python 脚本可为每个 decorated function 生成一个 manifest：

```bash
python examples/PythonFunctionTools/repo_tools.py --manifest-dir .qre/generated-tools
qre tool register --workspace . --manifest .qre/generated-tools/py_count_files.json
```

运行时模型调用 `py_count_files`，QRE 通过 stdio 启动 Python 进程，发送
`{ name, workspacePath, arguments }`，接收 `{ "result": ... }`，把 tool event
记录进 trace，再把结果返回给模型。

当前支持两种 transport：

- `stdio`：QRE 启动外部进程，把 `{ name, workspacePath, arguments }` 写入
  stdin，读取 stdout；stdout 可以是纯文本，也可以是 `{ "result": ... }`。
- `mcp-stdio`：QRE 发送一条最小 JSON-RPC `tools/call` 消息，并解析
  `result.content[].text`。

边界和安全语义：

- 外部工具进程会清空宿主环境，只注入 `TrustedLocalSandboxEnvironment`
  中的 SDK/CLI 白名单变量，不透传 provider secret。
- 外部进程 timeout 或 cancellation 时会 kill entire process tree。
- stdout/stderr 通过 bounded buffer 实时 drain，避免工具输出过大导致宿主
  OOM 或 pipe deadlock。
- `inputSchema` 直接来自 manifest，并由显式 `AIFunction` 实现暴露，避免
  external tool schema 依赖 delegate reflection，符合 Native AOT 路径。
- 当前 `mcp-stdio` 是 one-shot `tools/call`，没有完整 `initialize` lifecycle
  negotiation；需要 stateful MCP server 的场景仍是后续项。

### 5.6 JSON 输出

**Today**：`--json` 和 `--json-output` 已经是两个独立开关。

有两个容易混淆但必须区分的开关：

- `--json`：CLI 输出 JSON，给脚本、CI、平台集成消费。
- `--json-output`：要求模型返回 JSON；这会触发 QRE 默认策略，在 `auto`
  thinking 模式下关闭 thinking。

示例：CLI JSON 输出，但不要求模型返回 JSON：

```bash
qre run --workspace . --response "plain text" --json "analyze"
```

示例：要求模型返回 JSON：

```bash
qre run --workspace . --json-output "return a JSON summary"
```

当前 `qre run` 输出契约：

- 不带 `--json` 时，CLI 在 run 完成后输出最终 assistant text，然后输出
  run metadata。
- 带 `--stream` 时，CLI 会随着 model client 产出内容实时写出 human-readable
  assistant text delta，然后输出同样的 run metadata。这个模式面向终端和宿主
  app，不面向机器解析。
- 带 `--json` 时，stdout 只输出一条 `qre.run.completed` JSON 对象，供脚本和
  CI 解析。实时文本 delta、trace event 或进度信息不应混入这个 stdout
  contract。
- 未来 `--jsonl-stream` 会用于 machine-readable event streaming，每一行都应
  是显式 event-shaped JSON，例如包含 event type、sequence、run id 和 payload。
  它不应复用 `--json` 的 final result shape。

`--stream` 不能和 `--json` 混用；CLI 会 fail fast，避免把 text delta 混进
single-final-object JSON contract。`--jsonl-stream` 仍是保留参数，会明确失败，
而不是被静默拼进 prompt。

第三方 Agent 或桌面应用集成时，如果 human-readable 终端输出已经足够，可以使用
`--stream`。需要机器可读进度事件时，仍应等待未来的 `--jsonl-stream`。

当前 human-readable stream 命令形态：

```bash
qre run --workspace . \
  --profile readonly \
  --stream \
  "Analyze this repository and list the top risks."
```

未来 JSONL event 形态示例：

```jsonl
{"type":"qre.run.event","eventType":"model.text.delta","seq":12,"runId":"20260603123000123","delta":"Reading repository structure..."}
{"type":"qre.run.event","eventType":"model.text.delta","seq":13,"runId":"20260603123000123","delta":" Found the main runtime projects."}
{"type":"qre.run.event","eventType":"tool.call.requested","seq":14,"runId":"20260603123000123","toolName":"qre_search_files","argumentsHash":"sha256:..."}
{"type":"qre.run.completed","finalText":"Reading repository structure... Found the main runtime projects.","runId":"20260603123000123","traceFilePath":"/repo/.qre/runs/20260603123000123/events.jsonl"}
```

当前 human-readable stream 的最小 .NET 调用示例：

```csharp
using System.Diagnostics;

var startInfo = new ProcessStartInfo
{
    FileName = "qre",
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};

startInfo.ArgumentList.Add("run");
startInfo.ArgumentList.Add("--workspace");
startInfo.ArgumentList.Add("/path/to/repo");
startInfo.ArgumentList.Add("--profile");
startInfo.ArgumentList.Add("readonly");
startInfo.ArgumentList.Add("--stream");
startInfo.ArgumentList.Add("Analyze this repository and list the top risks.");

using var process = Process.Start(startInfo)
    ?? throw new InvalidOperationException("Failed to start qre.");

var buffer = new char[256];
while (true)
{
    var read = await process.StandardOutput.ReadAsync(buffer);
    if (read == 0)
    {
        break;
    }

    Console.Write(buffer.AsSpan(0, read));
}

await process.WaitForExitAsync();
```

注意：当前 `--stream` 输出是 human-readable 文本加最终 run metadata。tool call、
policy decision 和 tool result 仍通过 trace 获取。未来 machine-readable
streaming 应使用完整 event-shaped 记录，不应把尚未组装完成的 partial tool-call
payload 暴露给第三方消费端。

### 5.7 Thinking 策略

**Today**：`auto` 是当前推荐默认策略。

当前策略：

- `--thinking auto`：默认值。启用 tools 或 `--json-output` 时关闭 thinking。
- `--thinking off`：强制关闭 thinking。
- `--thinking on`：强制开启 thinking。
- `--thinking preserve`：保留 provider / caller 原始选项。

推荐默认使用 `auto`。很多模型在工具调用或 schema 输出时，如果 thinking
通道没有正确隔离，会导致工具 JSON、schema 输出或 provider 参数不兼容。
因此 QRE 把关闭 thinking 作为工具调用和结构化输出的默认安全策略。

这个策略来自真实 provider 验证：当前 `deepseek-v4-pro` Anthropic Messages
endpoint 在 thinking 模式下会拒绝 required/object `tool_choice`。因此 QRE
在工具调用和 JSON/schema-constrained 输出时优先选择 provider auto-tool
模式，并默认关闭 thinking，避免把 provider-specific 限制泄漏给上层调用者。

### 5.8 Trace、run artifacts 和 replay

**Today**：trace 以 JSONL 写入，并带有显式、durable 的 `SchemaVersion`、`DataMode`
和 `ReplayCapability`。默认模式是 `PublicRedacted / SummaryOnly`，不会持久化 prompt、
模型正文、工具参数/结果、stdout/stderr 或 payload blob。`--summary` 可读取默认轨迹；
recorded/strict replay 只接受显式 full-fidelity 轨迹，否则 fail closed。Public trace 还会把
宿主 `RunId` 和运行目录名替换为不可关联的 `public-<uuid>`，并清除 `QueryId`。

每次 `qre run` 会在 workspace 下写入：

```text
.qre/runs/<run-id>/events.jsonl
.qre/runs/<run-id>/manifest.json
.qre/runs/<run-id>/run.json
.qre/runs/<run-id>/diff.patch
.qre/runs/<run-id>/usage.json
.qre/runs/<run-id>/artifacts/
.qre/runs/<run-id>/blobs/sha256/...
.qre/private/runs/<private-id>/...  # owner-only PrivateDiagnostic，默认保留 7 天
```

Private diagnostic 在 Windows 使用仅当前用户的受保护 ACL，在 Unix 使用目录 `0700`、文件
`0600`。`PrivateDiagnosticRetention` 可以缩短保留期，最长强制限制为 30 天；该模式不提供静态加密。

查看最新 trace：

```bash
qre trace latest --workspace . --json

qre trace latest --workspace . --jsonl
```

生成已审查的 sanitized fixture 并执行 recorded replay：

```bash
qre run --workspace . --trace-data sanitized --response "offline smoke" "analyze this repo"
qre replay latest --workspace . --json
```

读取 latest run 的只读 summary：

```bash
qre replay latest --workspace . --summary --json
```

当前 replay completed 输出示例：

```json
{"type":"qre.replay.completed","finalText":"offline smoke","runId":"20260603043913655","termination":"NoToolCalls","profile":"none","runner":"recorded-replay","tools":[],"workspacePath":"/repo","traceFilePath":"/repo/.qre/runs/20260603043913655/events.jsonl","runDirectory":"/repo/.qre/runs/20260603043913655","manifestPath":"/repo/.qre/runs/20260603043913655/manifest.json","totalRounds":1,"totalToolCalls":0,"totalDurationMs":0}
```

Recorded replay 的核心机制：

- `RecordedReplayModelClient` 从 JSONL 中读取已记录的 assistant text 和
  structured tool-call snapshots。
- `RecordedReplayToolPack` 按 `toolName + normalized argument hash` 匹配并
  返回已记录的工具结果，不调用原始工具。
- 大模型响应和工具输出超过 inline 阈值时会落到 `blobs/sha256/...`，trace
  中保留 digest、size 和 length metadata。
- `replay latest --summary` 仍可用于不执行 runtime 的快速 trace 摘要。

#### Strict deterministic replay（`--strict`）

`qre replay latest --workspace . --strict --json` 在 recorded replay 基础上，向
engine 注入由 source trace 种子化的 deterministic clock 和 query-id。因此对同一
source trace 与同一 runtime 版本的两次 strict replay 会产出 **byte-identical 的
canonical event projection**，以稳定的 `replayDigest`（对 engine 事件记录的
`Type`、`Seq`、`RuntimeEventType`、deterministic `QueryId`、deterministic
`Timestamp` 和 `Data` 做 SHA-256）暴露。该 digest 刻意排除 run-scoped 的
`RunId`/`SessionId`，因此跨 run 稳定。

Strict replay 输出示例：

```json
{"type":"qre.replay.completed","mode":"strict-replay","finalText":"offline smoke","sourceRunId":"20260603043913655","runId":"20260604044405928","termination":"NoToolCalls","profile":"none","schemaVersion":1,"replayDigest":"fc0a93aab02c…","providerCalls":false,"toolExecutions":false,"tools":[],"workspacePath":"/repo","traceFilePath":"/repo/.qre/runs/20260604044405928/events.jsonl","runDirectory":"/repo/.qre/runs/20260604044405928","manifestPath":"/repo/.qre/runs/20260604044405928/manifest.json","totalRounds":1,"totalToolCalls":0,"totalDurationMs":1}
```

##### Trace schema 版本与兼容性

trace 格式在 `run.started` 记录和 `manifest.json` 中携带显式、durable 的
`SchemaVersion`（当前版本 `1`，第一个公开、可确定性重放的格式）。Strict replay
按该版本 gate：

- 当前版本的 trace 可以 strict replay。
- **没有**记录 `SchemaVersion` 的 trace 视为 legacy 版本 `0`（pre-public），会被
  strict replay 以精确 reason 拒绝（`strict replay requires schema version >= 1;
  trace has no recorded schema version (pre-public legacy trace)…`）。这类 trace
  仍可用 non-strict recorded replay。
- 版本**高于** runtime 支持的 trace 会以 `unsupported trace schema version N
  (runtime supports up to M)…` 拒绝。

`replay latest --summary` 会报告 `schemaVersion`、`strictReplayCompatible`，被阻断
时还会报告 `strictReplayBlockedReason`。

##### Replay 保证与非保证

Strict replay 保证：

- 不调用 provider：model client 是 `RecordedReplayModelClient`，只出队已记录的
  assistant text 和 tool-call snapshots。
- 不执行原始工具：工具来自 `RecordedReplayToolPack`，按 `toolName + normalized
  argument hash` 返回已记录结果。
- deterministic clock 与 query id，因此对同一 source trace 和 runtime 版本的多次
  strict replay 产出 byte-identical 的 `replayDigest`。

不保证：

- 磁盘上的 replay run 目录（`RunId`、`SessionId`、envelope `run.started`/
  `run.completed` 的 wall-clock 时间戳）并非 byte-identical——只有 canonical engine
  projection / `replayDigest` 是。Run-scoping 被刻意排除。
- 跨 runtime 版本的确定性：不同 runtime 版本可以合理地改变 canonical projection。
- Live 行为：见下文 live rerun。

##### Live rerun 与 strict replay 分开

`qre rerun latest` 是 **live rerun**，不是 strict replay：它用新的 response/clock
重新执行 runtime，当 sandbox 命令依赖 clock、filesystem、network 或 host state
时，可以合理地与 source run 不同。Strict replay（`replay latest --strict`）是
确定性、provider-free / tool-free 路径；live rerun 是非确定性重执行路径。不要把
live rerun 输出当作确定性保证。

`manifest.json` 是 Phase 1 的 run artifact 索引，目的是让 CLI、CI、桌面端或
其他平台不用解析完整 JSONL 就能定位 runId、run 目录、trace 文件、profile
和终止状态。它不是安全审计摘要，也不替代原始 trace。

#### v2 C6 versioned audit 与 data-only replay

`qre run` 默认且仅使用 v2 audit schema，不再提供 v1 执行入口。默认写入
`.qre/v2/runs/<public-id>/audit.v1.jsonl`，payload 是显式 allow-list 的
`PublicRedacted / SummaryOnly` 投影；prompt、model/reasoning 正文、工具名/参数/结果、路径和宿主 ID
不会落盘。`--trace-data private` 写入 owner-only `.qre/v2/private/runs`，
`--trace-data sanitized` 用于经过审查的 fixture，两者都标记为 `Recorded`。

```bash
qre run --workspace . --trace-data sanitized \
  --response "offline v2" --json "audit this runtime"
qre replay latest --workspace . --summary --json
qre replay latest --workspace . --strict --json
```

v2 replay 是纯数据验证 reducer：API 不接收 model client、provider 或 tool executor。它验证 schema、
连续 sequence、causation/correlation、kind/payload/identity、model request/response、工具 observation 顺序、
terminal text/usage/history，以及 manifest/file/blob 的路径、长度、SHA-256 和配额一致性。输出中的
`providerCalls` 与 `toolExecutions` 固定为 `false`。`--strict` 表示完整轨迹验证并输出稳定
`replayDigest`；它不是 live rerun，也不提供 crash resume 或 exactly-once。

存储默认上限由 `RuntimeAuditStoreOptions` 控制，包括最长 30 天 retention、run 数、全部 runs、单 run、
事件数、JSON line/depth、单 blob 和总 blob。单进程 writer 共享总磁盘配额；写入失败可选择
`FailClosed`（默认）或显式 warning 的 `BestEffort`，失败 run 会进入 terminal-only GC。未知/未来 schema、
非终态 run、public summary 和任何完整性冲突均拒绝 replay。

### 5.9 Diff 输出

**Today**：`diff latest` 优先读取 latest run 的 run-scoped `diff.patch`。
没有 run patch 时才回退到当前 workspace git diff。

当前 CLI 可以读取 latest run 的 patch：

```bash
qre diff latest --workspace . --json
```

也可以只看统计：

```bash
qre diff latest --workspace . --stat --json
```

当前输出中的 `mode` 通常是 `run-diff-patch`，含义是“读取最新 run 结束时
写入的 `.qre/runs/<run-id>/diff.patch`”。该 patch 通过临时 Git index 生成：

- 覆盖 staged changes。
- 覆盖 unstaged tracked changes。
- 覆盖 deleted files。
- 覆盖 untracked non-`.qre` files。
- 不修改真实 `.git/index`。
- 同一文件同时有 staged 和 unstaged 修改时，patch 表示最终 workspace 状态。
- 对 repair run，run patch 会收敛到 `repair-edits.txt` 记录的路径。如果其中某个
  同路径文件在 run 前已经有未提交修改，当前 pre-release 行为是输出该文件从
  `HEAD` 到最终状态的完整 diff，因此会包含该同文件的 pre-existing delta。

如果当前 workspace 不是 Git 仓库，或者 latest run 没有 `diff.patch`，CLI
会回退到 `workspace-git-diff` 模式。`--stat` 当前仍读取当前 workspace 的
Git stat，而不是 run-scoped patch stat。

### 5.10 Usage 输出

**Today**：每个 run 会写入估算 usage，不是 provider-native billing 事实。

run 结束时会写入：

```text
.qre/runs/<run-id>/usage.json
```

同时会追加一条 `budget.usage` trace event。当前字段包括：

- prompt chars / estimated prompt tokens
- assistant chars / estimated completion tokens
- tool output chars / estimated tool output tokens
- total tokens
- total rounds / total tool calls / total duration
- `estimated: true`

估算规则是 `ceil(chars / 4.0)`。当 provider 后续暴露稳定 token accounting
时，usage contract 可以扩展 provider-native token 和 cost 字段；当前不应把
`usage.json` 当作计费依据。

**Risk**：`events.jsonl` 可能包含 prompt、模型响应、工具参数、工具结果和
被读取文件的内容。真实运行时，它可能包含私有代码、配置片段或密钥样式
字符串。当前仓库 `.gitignore` 已包含：

```gitignore
.qre/
```

如果在其他仓库 dogfood QRE，应同步添加该忽略规则。CI 上传 `.qre/` artifact
前需要脱敏或限制访问，不要把原始 trace 当作公开 issue 附件。

## 6. 跨平台开发应用示例

**Today**：早期集成建议把 `qre` CLI 当作子进程调用，并解析 `--json` 的最后
一行 JSON。外部应用不应依赖仓库源码路径或项目启动命令。

下面示例展示如何编写一个跨平台的 .NET Console 应用，把 `qre` 当作本地
Agent runtime CLI 调用。这个方式适合早期集成，因为它不要求外部应用直接
引用 QueryRuntime 内部程序集；后续公开稳定 API 后，可以改为进程内嵌入。

### 6.1 示例目标

构建一个简单命令：

```bash
RepoDoctor /path/to/repo
```

它会：

1. 调用 `qre run --profile readonly --json` 分析仓库。
2. 解析 JSON 输出。
3. 打印最终文本和 trace 路径。
4. 在 Windows、macOS、Linux 上使用同一套 C# 代码。

### 6.2 创建项目

完整示例已放在仓库内：

```text
examples/RepoDoctor/
```

下面的代码片段用于说明核心结构；维护时以 `examples/RepoDoctor` 中的实际代码
为准。

```bash
dotnet new console -n RepoDoctor
cd RepoDoctor
```

在运行示例前，先确保 `qre` 可执行文件在 `PATH` 中。仓库内可通过 Native AOT
publish 生成本地二进制：

```bash
cd /Users/iwaitu/github/codexflow
dotnet publish CodexFlow.QueryRuntime.Cli \
  -c Release \
  -r osx-arm64 \
  -p:PublishAot=true \
  -p:SelfContained=true
export PATH="$PWD/CodexFlow.QueryRuntime.Cli/bin/Release/net10.0/osx-arm64/publish:$PATH"
```

### 6.3 示例代码

```csharp
using System.Diagnostics;
using System.Text.Json;

var workspace = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Directory.GetCurrentDirectory();

if (!Directory.Exists(workspace))
{
    Console.Error.WriteLine($"Workspace does not exist: {workspace}");
    return 1;
}

var qrePath = Environment.GetEnvironmentVariable("QRE_BIN");
if (string.IsNullOrWhiteSpace(qrePath))
{
    qrePath = "qre";
}

var startInfo = new ProcessStartInfo
{
    FileName = qrePath,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};

startInfo.ArgumentList.Add("run");
startInfo.ArgumentList.Add("--workspace");
startInfo.ArgumentList.Add(workspace);
startInfo.ArgumentList.Add("--profile");
startInfo.ArgumentList.Add("readonly");
startInfo.ArgumentList.Add("--json");
startInfo.ArgumentList.Add("Analyze this repository and list the top three risks.");

using var process = Process.Start(startInfo)!;
var stdout = await process.StandardOutput.ReadToEndAsync();
var stderr = await process.StandardError.ReadToEndAsync();
await process.WaitForExitAsync();

if (process.ExitCode != 0)
{
    Console.Error.WriteLine(stderr);
    return process.ExitCode;
}

var jsonLine = stdout
    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
    .Select(line => line.Trim())
    .LastOrDefault(line => line.StartsWith("{", StringComparison.Ordinal));

if (jsonLine == null)
{
    Console.Error.WriteLine("qre did not produce a JSON result.");
    Console.Error.WriteLine(stdout);
    return 1;
}

using var doc = JsonDocument.Parse(jsonLine);
var root = doc.RootElement;

Console.WriteLine("Result:");
Console.WriteLine(root.GetProperty("finalText").GetString());
Console.WriteLine();
Console.WriteLine("Trace:");
Console.WriteLine(root.GetProperty("traceFilePath").GetString());

var replayInfo = new ProcessStartInfo
{
    FileName = qrePath,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};

replayInfo.ArgumentList.Add("replay");
replayInfo.ArgumentList.Add("latest");
replayInfo.ArgumentList.Add("--workspace");
replayInfo.ArgumentList.Add(workspace);
replayInfo.ArgumentList.Add("--json");

using var replay = Process.Start(replayInfo)!;
var replayStdout = await replay.StandardOutput.ReadToEndAsync();
await replay.WaitForExitAsync();

if (replay.ExitCode == 0)
{
    var replayJson = replayStdout
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .LastOrDefault(line => line.StartsWith("{", StringComparison.Ordinal));
    Console.WriteLine();
    Console.WriteLine("Replay summary:");
    Console.WriteLine(replayJson);
}

return 0;
```

### 6.4 运行示例

macOS / Linux:

```bash
export QRE_API_URL="https://your-provider.example/v1"
export QRE_API_KEY="..."
export QRE_MODEL="your-model"
export QRE_API_MODE="chat-completions"

dotnet run -- /path/to/repo
```

Windows PowerShell:

```powershell
$env:QRE_API_URL="https://your-provider.example/v1"
$env:QRE_API_KEY="..."
$env:QRE_MODEL="your-model"
$env:QRE_API_MODE="chat-completions"

dotnet run -- C:\src\my-repo
```

离线 smoke 模式可以把 `--profile readonly` 改成 `--response "offline smoke"`，
用于验证你的跨平台应用是否能正确解析 `qre` 输出。

### 6.5 外部应用调用方式

当前示例已经使用 `qre` 可执行文件；外部应用可以依赖 `PATH` 查找，也可以用
`QRE_BIN` 指向明确的 CLI 路径。

```bash
qre run --workspace . --profile readonly --json "Analyze this repository."
qre replay latest --workspace . --json
```

对于外部应用，这是重要变化：集成层不应该依赖仓库源码路径，而应该依赖
稳定的 `qre` 可执行文件或稳定的 `CodexFlow.QueryRuntime.*` NuGet 包。

## 7. Sandbox 方向

**Today**：当前已有 trusted-local runner 和 Docker runner 两条路径。
`LocalProcessSandboxRunner` 面向可信本地开发；`DockerSandboxRunner` 是 Phase 2b
的容器隔离实现，用于验证更强的文件系统、网络、用户、capability 和 cleanup
边界。

`LocalProcessSandboxRunner` 不执行 mount policy、Linux capabilities、seccomp
或 copy-in/copy-out workspace 隔离。`SandboxJobSpec.Network` 和
`SandboxJobSpec.Mounts` 对 LocalProcess 来说是 advisory contract：LocalProcess
会防御性拒绝 `Network.Allow`，但 `Network.Deny` 并不能在 OS 层阻止子进程
发起网络访问，`WorkspaceReadOnly` 也不能在 OS 层阻止写入。

LocalProcess 会默认清空子进程环境，并只注入
`SandboxJobSpec.Environment` 中显式提供的变量。Phase 1 的 verify tools 和
`qre sandbox exec` 使用 `TrustedLocalSandboxEnvironment` 注入 SDK/CLI 必需
的本地白名单变量，例如 `PATH`、`HOME`、`TMPDIR`、`DOTNET_ROOT` 和 Windows
shell/path 变量；不透传任意宿主环境变量或 provider secret。这仍不能替代
OS/container 级 credential isolation。它的价值是让上层先依赖
`ISandboxRunner` contract，并为后续 Docker runner 留出替换点。

`qre sandbox exec` 是一个低层 trusted-local 执行入口，它只根据命令 shape
映射到 verify tool descriptor 并经过 capability policy，不会做 verify tool
pack 中的全部参数级 workspace path 归一化。例如 `qre_git_diff(path)` 会先
把 path 解析到 workspace 内，而 `sandbox exec -- git diff ...` 会按用户传入
的原始参数执行。Phase 1 已将 `sandbox exec` 的 started、policy decision 和
completed 事件写入 `.qre/runs/<run-id>/events.jsonl`，便于审计，但这仍不是
不可信命令执行边界。

Docker runner 当前已经覆盖：

- read-only workspace mount。
- write-capable job 使用 staged copy-in/copy-out，而不是直接 writable host bind。
- 默认 network deny。
- non-root user。
- `no-new-privileges`。
- drop Linux capabilities。
- read-only root filesystem + tmpfs scratch。
- output limit。
- timeout 后清理容器。
- 外部 cancellation 后 kill host `docker run` process tree 并强制清理容器。
- seccomp profile enforcement 的集成测试。
- symlink staging skip。
- workspace root mount + subdirectory workdir。

Docker sandbox tests 默认不开启，需要本地 Docker daemon：

```bash
RUN_QUERY_RUNTIME_DOCKER_TESTS=true dotnet test \
  CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj \
  --filter "FullyQualifiedName~DockerSandboxRunnerIntegrationTests" \
  --logger "console;verbosity=detailed"
```

仍然需要继续补齐的 runner 方向：

- Kubernetes / remote runner：面向企业和 CI 的远程隔离执行。
- 更完整的 artifact capture：把工具执行日志、生成产物和 diff 与 run manifest
  更强绑定。
- capability policy 与 sandbox policy 的统一 public schema。

短期应避免把“本地 process allowlist”宣传成安全 sandbox。它可以是开发体验；
Docker runner 才是当前第一版可验证隔离边界，但仍需要更多平台矩阵和长期
hardening 后才能作为生产级安全承诺。

## 8. Native AOT 和跨平台发布

**Today**：Native AOT 已在本地 `osx-arm64` 路径通过 publish 和 smoke；跨平台
release / CI 矩阵仍是 Planned。

项目的长期目标之一是把 `qre` 编译为跨平台 native binary，降低用户安装和
冷启动成本。目标平台包括：

- macOS arm64 / x64
- Linux x64 / arm64
- Windows x64 / arm64

Native AOT 的第一条 CLI 路径已经通过本地 `osx-arm64` 验证，但还不能把
跨平台 Native AOT 作为完整发布能力宣传。当前状态：

- `CodexFlow.QueryRuntime.Cli` / `CodexFlow.QueryRuntime.Experimental` /
  `CodexFlow.QueryRuntime.Engine` 已切断对 `CodexFlow.Core` 的依赖。
- CLI 和 trace 的机器可读输出已转向 `System.Text.Json` source-generated
  contexts。
- `QreModelExecutionPolicy` 已在 Phase 1.5 改为显式复制 `ChatOptions`
  到 `VllmChatOptions`，避免在 CLI thinking policy 路径使用 reflection。
- 直接引用的 provider client 包已迁移为 `VllmChatClient` `2.0.21`；当前
  QRE AOT publish 路径不再出现 Newtonsoft.Json transitive warning，且
  Anthropic Messages thinking-off 行为已验证。
- Phase 1.5 已修掉 `QueryRuntimeEngine` 的反射 option normalization、
  legacy tool-call fingerprint 动态 JSON 序列化、hashline metadata 动态
  conversion，以及 `ToolArgumentNormalizer` 的 `JObject.ToObject<T>` 路径。
- 本地 `osx-arm64` AOT publish 已通过，且发布后的 native `qre` 已验证
  `--version`、`run --response ... --json`、`tool list --json`、
  `trace latest --jsonl`、`diff latest --json`、`replay latest --json`。
- Native AOT `qre` 已验证真实 provider 调用。OpenAI-compatible
  `chat-completions` endpoint 和 Anthropic Messages endpoint 都可用于 smoke；
  Anthropic Messages thinking-off 行为需要 `VllmChatClient` 2.0.21 或更新版本。
- 外部工具 schema 采用 manifest-first 设计，`inputSchema` 直接由显式
  `AIFunction` 实现暴露，不依赖 external delegate reflection。
- 仍需要在 Linux / Windows runner 上验证同等 publish 和 smoke。
- `AIFunction` 工具 schema 生成可能依赖 reflection，需要在更多内置 tool
  pack 进入 AOT CLI 前单独审计。
- 动态插件加载和 AOT 存在冲突，需要优先选择 MCP/stdio 等进程外插件模型。
- provider adapter、sandbox runner、tool packs 仍需要持续维护 trimming
  兼容性。

目标发布命令形态：

```bash
dotnet publish CodexFlow.QueryRuntime.Cli \
  -c Release \
  -r osx-arm64 \
  -p:PublishAot=true \
  -p:SelfContained=true
```

验收标准不应只是“能 publish”，而应包括：

- `qre --version` 可运行。
- `qre tool list` 可运行。
- `qre run --response ... --json` 可运行。
- `qre replay latest --json` 可运行。
- 没有关键 trimming warning。
- CI 覆盖 macOS、Linux、Windows。

## 9. 项目潜力

### 9.1 开源定位清晰

“完整 AI 编程平台”很容易和 Claude Code、Cursor、Cline、OpenHands 等产品
正面竞争，门槛高且差异化困难。而“Agent runtime harness”是更底层的定位：
它可以被这些类型的产品、插件、CI、企业平台复用。

这个定位的优势是：

- 用户不必迁移到完整平台，也能采用 runtime。
- 可以从 CLI 和 NuGet 包开始扩散。
- 更容易被开发者理解为基础设施，而不是又一个 App。
- 更适合社区贡献 tool packs、sandbox runner、provider adapter。

### 9.2 .NET 生态缺口

Python 和 TypeScript 生态有大量 Agent demo 和框架，但 .NET-native 的
coding-agent runtime harness 仍然稀缺。CodexFlow 已经有以下基础：

- ASP.NET Core 和 .NET 工程经验。
- `Microsoft.Extensions.AI` 接入方向。
- 现有 QueryRuntime loop。
- 工具调用、事件流、TDD adapter、validator、安全审计等平台积累。
- CLI 和实验 harness 切片已经能运行。

如果能把这些能力拆成轻量、稳定、可安装的组件，它有机会成为 .NET Agent
生态中的基础项目。

### 9.3 Trace / replay 是差异化关键

Agent 开发真正缺的是可复现性。单纯“能调用工具”不是壁垒；把每次模型请求、
模型响应、工具调用、工具输出、policy decision、diff、artifact 都记录成
可审计格式，才是工程基础设施。

一旦 deterministic replay 成熟，项目可以支持：

- issue 附带 trace 复现。
- provider 行为对比。
- 工具 schema 回归测试。
- Agent benchmark。
- 企业审计和合规记录。

这会明显区别于只追求交互体验的 coding assistant。

### 9.4 Sandbox 是商业化和企业采用入口

企业最关心的问题通常不是“模型能不能写代码”，而是：

- 它能访问哪些文件？
- 它能不能联网？
- 它能不能读取密钥？
- 它能不能执行破坏性命令？
- 每次操作有没有审计记录？
- 出问题能不能 replay？

QueryRuntime 如果把 capability policy、Docker sandbox、trace/replay 和 CLI
组合起来，就具备企业采用的基础，也能自然延伸到托管服务或内部平台。

### 9.5 Native AOT 可以带来分发优势

如果 `qre` 最终能以单文件 native binary 分发，开发者可以像使用 `ripgrep`、
`gh`、`kubectl` 一样安装和使用它。这对开源传播很重要：

- 安装成本低。
- CI 集成简单。
- 本地工具链体验好。
- 不要求用户先理解整个 CodexFlow 平台。

## 10. 推荐演进路径

### Phase A: 稳定当前实验 CLI

- 固化 `--json` 输出 DTO。
- 明确 `--json-output`、thinking、tools 三者关系。
- 增加 CLI smoke tests。
- 让 `.qre/runs/<run-id>` 结构稳定。

### Phase B: 抽出公共 runtime contract

- 定义 `IQueryRuntimeEngine`。
- 定义 `QueryRuntimeRequest` / `QueryRuntimeResult`。
- 定义 public trace DTO。
- 去除 Web API、Identity、数据库、SignalR 对 runtime 核心的默认依赖。

### Phase C: 工具和 capability policy

- 把 read / write / command / git / dotnet / node / python 拆成 tool packs。
- 每个 tool 声明 capability。
- profile 决定允许哪些 capability。
- 默认 profile 保守，危险操作必须显式开启。

### Phase D: AOT compatibility probe

- 状态：本地 `osx-arm64` probe 已通过，后续要变成 blocking CI。
- 继续记录 trim/AOT warning baseline，而不是把 warning 留到发布前才处理。
- 至少尝试运行 native binary 的 `qre --version`、`qre tool list`、
  `qre run --response ... --json` 和 `qre replay latest --json`。
- 保持 `QreModelExecutionPolicy` 这类 CLI 热路径为显式映射。
- 明确 CLI AOT path 不包含 MVC、SignalR、EF、dashboard 和 runtime-loaded DLL
  plugin。

### Phase E: Docker sandbox MVP

- 状态：Docker runner MVP 和 hardening first-slice 已完成。
- 后续继续补 CI runner 覆盖、平台矩阵、remote runner 和更完整 artifact capture。
- 本地 process runner 继续明确标注为 trusted-development-only。

### Phase F: deterministic replay

- 状态：Phase 3 first-slice 已完成。
- 已记录 model responses、structured tool-call snapshots、normalized argument
  hash、tool outputs 和 content-addressed blobs。
- 已实现 recorded replay model adapter 和 tool coordinator。
- full-fidelity replay 不调用 provider、不执行原始工具；默认 public trace 只允许 summary。
- 后续继续补 deterministic ID / clock、trace schema migration、public replay
  spec 和跨版本回放兼容。

### Phase G: AOT hardening

- 使用 `System.Text.Json` source generation。
- 避免 runtime-critical path 依赖 reflection-heavy 动态加载。
- 把插件模型优先设计为 MCP/stdio；当前 external tool 已采用 manifest-first
  out-of-process schema，避免 runtime DLL plugin loading。
- 将 Phase D 的 AOT probe 升级为 blocking CI，并验证多平台 native binary。

### Phase H: 独立开源发布

- 清理仓库历史和 secret。
- 明确 license。
- 准备 README、quickstart、examples、threat model、replay format spec。
- 发布 NuGet 包和 CLI binary。
- 用几个真实 example repo 展示从分析、trace 到 replay 的完整流程。

## 11. 当前限制

当前实现还不应被描述为成熟 runtime。主要限制包括：

- CLI provider 路径已从 `CodexFlow.Core` provider factory 中脱离，但仍依赖
  `QreVllmChatClientFactory` 的模型族启发式路由。
- replay 已支持 recorded replay，但还不是 benchmark 级 deterministic replay；
  deterministic ID、clock 和 trace schema migration 仍需硬化。
- sandbox 已有 Docker runner，但 Kubernetes / remote runner 和更多平台矩阵
  仍未完成。
- Native AOT 已在本地 `osx-arm64` 通过；Linux / Windows publish、签名、
  发布包和 CI 矩阵仍未完成。
- provider-native token accounting 尚未接入，`usage.json` 当前是估算 usage，
  不能作为计费依据。
- MCP-stdio 当前只支持 one-shot `tools/call`，没有完整 initialize lifecycle。
- 公开包边界、namespace、DTO、序列化策略仍需收敛。
- repo 中仍有完整平台代码，尚未完成开源 harness 独立抽取。

这些限制不影响当前方向验证，但发布时必须诚实表达：这是一个正在提炼中的
runtime harness，而不是已经完整成熟的安全执行平台。

## 12. 一句话总结

CodexFlow QueryRuntime 最有价值的方向，是成为一个跨平台、可审计、可 replay、
可 sandbox、可嵌入的 Agent runtime harness。它应该让开发者更容易构建
自己的 coding agent、CI agent、IDE agent 或企业内部 agent 平台，而不是要求
他们采用一个完整的 CodexFlow SaaS 应用。
