# QueryRuntime Integration Tests

这个项目专门承载 `QueryRuntime` 的真实模型集成测试与 spike 实验，不与现有 `CodexFlow.Tests` / `CodexFlow.Core.Tests` 混放。

## 配置来源

- 直接读取 `CodexFlow/appsettings.json` 中的 `VllmAgent` 配置
- 不单独维护第二份模型配置

## 运行方式

```bash
export RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true
dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj
```

如果未设置 `RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true`，测试会被 `Skip`，不触发真实模型调用。

## Docker Sandbox Tests

Docker sandbox tests do not require a live LLM provider. They require a local
Docker daemon and are enabled separately:

```bash
docker pull alpine:3.20
RUN_QUERY_RUNTIME_DOCKER_TESTS=true dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj \
  --filter "FullyQualifiedName~DockerSandboxRunnerIntegrationTests" \
  --logger "console;verbosity=detailed"
```

These tests validate the Phase 2b Docker runner path: read-only workspace
mounts, no host secret exposure from common root credential paths, default
network denial, timeout enforcement, output-size limiting, non-root execution,
`no-new-privileges`, dropped Linux capabilities, read-only root filesystem, and
tmpfs scratch space. They also verify that Docker enforces a supplied seccomp
profile and that write-capable jobs use staged copy-in/copy-out rather than a
direct writable host bind mount. The staging tests also cover symlink skipping
and subdirectory execution with a mounted workspace root.

## Soak 观察

Layer C 的长时间补充观察默认不开启，需要额外显式设置：

```bash
export RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true
export RUN_QUERY_RUNTIME_REAL_SOAK_TESTS=true
export QUERY_RUNTIME_REAL_SOAK_ITERATIONS=10
dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj
```

- `RUN_QUERY_RUNTIME_REAL_SOAK_TESTS=true`
  - 开启 real-API soak test
- `QUERY_RUNTIME_REAL_SOAK_ITERATIONS`
  - 控制 soak test 的迭代次数，默认 `5`

## Envelope Format Spike

Spike B 使用真实模型比较 `JSON / XML / Markdown-fenced` 三种 LLM-facing 通知格式：

```bash
export RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true
export RUN_ENVELOPE_FORMAT_SPIKE_TESTS=true
dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj --logger "console;verbosity=detailed" --filter "FullyQualifiedName~EnvelopeFormatSpikeTests"
```

开发时可选地限制样本数：

```bash
export ENVELOPE_FORMAT_SPIKE_SAMPLE_LIMIT=3
```

最近一次运行会把结果写到：

- `docs/spike-data/results/envelope-format-spike-latest.json`
- `docs/spike-data/results/envelope-format-spike-latest.md`

如果想看实时进度，建议加 `--logger "console;verbosity=detailed"`；项目内已启用 xUnit `showLiveOutput`，每个样本/格式的开始、结束、超时都会直接打印。

## 当前范围

- Experimental harness 分阶段真实门槛：
  - Phase 0：读取项目 `VllmAgent` 配置
  - Phase 1：真实 provider streaming
  - Phase 2：experimental harness no-tool run 写入 JSONL trace
  - Phase 3：experimental harness + readonly tool pack 真实工具调用
- 无工具场景的稳定终止
- 单工具调用场景的事件流与结果稳定性
- Context window completion hook 的真实链路验证
- 可配置迭代次数的 no-tool soak 观察
- Envelope format A/B spike 的真实模型比较

只运行 experimental harness 分阶段真实测试：

```bash
export RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true
dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj \
  --filter "FullyQualifiedName~ExperimentalHarnessRealLlmPhaseTests" \
  --logger "console;verbosity=detailed"
```

## Smoke 判定说明

- `NoToolPrompt` 与 `ContextWindowManager` 用例属于稳定门槛，预期直接通过。
- 工具相关 live smoke 仍受真实模型配合度影响。
  - 当模型确实进入工具链路时，测试会校验工具事件顺序与最终语义。
  - 当模型/API 未走出预期工具路径时，测试会以 observational skip 结束，而不是把模型波动误判为 runtime 回归。
- 当前 `deepseek-v4-pro` + Anthropic Messages endpoint 在 thinking mode 下不接受
  required/object `tool_choice`。Experimental harness 的真实 readonly 工具阶段因此使用
  provider auto-tool 模式验证工具链路，而不是强制 provider-level required tool choice。
- QRE 默认 `QreThinkingPolicy.Auto`：当启用工具或请求 JSON / schema 输出时自动关闭
  thinking。真实工具阶段依赖这个默认策略；只有做 provider 兼容性实验时才应显式开启
  thinking 或 preserve 调用方原始选项。

2026-06-03 真实 provider 验证补充：

- `ExperimentalHarnessRealLlmPhaseTests` 使用项目 `CodexFlow/appsettings.json`
  中的 `deepseek-v4-pro` / `AnthropicMessages` 配置通过 5/5。
- `VllmChatClient` 2.0.21 已验证 Anthropic Messages request body 会发送
  `thinking: { "type": "disabled" }`；`ThinkingEnabled=false` 的 streaming
  smoke 中 thinking 长度为 0。
- `qre --thinking off` 使用同一 Anthropic Messages endpoint 可以完成真实
  provider 调用，trace 中 assistant text 只包含固定输出文本。
- Native AOT `qre` 二进制使用 DashScope OpenAI-compatible endpoint
  `https://dashscope.aliyuncs.com/compatible-mode/v1`、`QRE_API_MODE=chat-completions`
  通过硬回显 smoke，`--thinking off` 下 trace 中 `ThinkingTextLength=null`。

Native AOT + OpenAI-compatible smoke 示例：

```bash
dotnet publish CodexFlow.QueryRuntime.Cli \
  -c Release \
  -r osx-arm64 \
  -p:PublishAot=true \
  -p:SelfContained=true
export PATH="$PWD/CodexFlow.QueryRuntime.Cli/bin/Release/net10.0/osx-arm64/publish:$PATH"

export QRE_API_URL="https://dashscope.aliyuncs.com/compatible-mode/v1"
export QRE_API_KEY="..."
export QRE_MODEL="deepseek-v4-pro"
export QRE_API_MODE="chat-completions"

qre run --workspace /tmp/qre-native-openai-real \
  --profile none \
  --thinking off \
  --json \
  "只输出以下固定文本，不要添加任何其它字符：OPENAI_COMPAT_OK"
```

后续可在此项目内继续扩展 `QueryRuntimeRecoveryTests`、`QueryRuntimeEventOrderingTests`、`GatewayRuntimeIntegrationStabilityTests` 对应的真实集成版本。

## TASK_BUG_001 Forge Worker 回归

该用例会创建临时工作区，使用真实 `VllmAgent`、真实 `QueryRuntimeEngine`、Forge worker context、`hs_read` / `hs_write` 工具链完成一次既有文件修改。默认不开启：

```bash
export RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true
export RUN_TASK_BUG_001_REAL_LLM_TESTS=true
dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj --logger "console;verbosity=detailed" --filter "FullyQualifiedName~TaskBug001ForgeWorkerRealLlmTests"
```

调试失败现场时可保留临时工作区：

```bash
export KEEP_TASK_BUG_001_WORKSPACE=true
```
