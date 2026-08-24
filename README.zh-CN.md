# CodexFlow QueryRuntime

[English](README.md) | **简体中文**

[![CI](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/ci.yml/badge.svg)](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/ci.yml)
[![Release](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/release.yml/badge.svg)](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.txt)

CodexFlow QueryRuntime 是一个跨平台 .NET Agent Runtime，负责模型循环、工具执行、策略门禁、审计/回放、检查点恢复与沙箱自动化。它既可以嵌入宿主应用，也可以作为独立的 `qre` CLI 运行，不依赖 CodexFlow Web 平台。

当前仓库处于 **0.2 preview、仅 v2** 阶段。新集成应使用 `CodexFlow.QueryRuntime.Protocol` 和 `CodexFlow.QueryRuntime.Engine.V2`。早期 v1 API 仅用于源码迁移和历史 trace 兼容，不再是 CLI 或 CodexFlow 可选后端。

## 当前能力

- 通过 `IAgentRuntime` 提供类型化 Agent 循环，统一处理模型、工具、继续条件、预算和终止逻辑。
- 为消息、会话、Turn、工具调用、结果、用量、策略和错误提供稳定协议类型。
- 冻结工具注册表，以及授权、审批、沙箱、输出校验和 fail-closed 策略管线。
- 面向长任务的确定性上下文准备与压缩。
- 持久化审计事件、严格回放，以及 public/sanitized/private 三种 trace 数据模式。
- 通过 `IResumableAgentRuntime`、attempt lease、检查点和兼容性校验实现 H1 本地崩溃恢复。
- 内置文件、搜索、补丁、进程和仓库工具，并支持外部 Python、Node.js 与 MCP stdio 工具。
- LocalProcess 与 Docker 两类沙箱适配器。
- 跨平台 CLI 与 Native AOT 发布构建。

## 当前边界

- H1 是单宿主、本地文件系统恢复方案；分布式接管和远程检查点存储属于后续 H2/H3 范围。
- 公开脱敏 trace 按设计不可恢复。恢复需要 sanitized 或 private 检查点，并要求 workspace、策略、工具目录、模型和 recovery compatibility identity 一致。
- `LocalProcessSandboxRunner` 只适用于可信本地开发，不是强安全边界。执行不可信命令时应使用 Docker 或其他隔离 runner。
- 网络白名单只有在 runner 明确支持时才会强制执行。
- MCP stdio 当前支持一次性工具调用，尚未覆盖完整 MCP 生命周期。
- 用量估算用于运行观测，不应作为供应商账单依据。

## 项目结构

| 项目 | 职责 |
|---|---|
| `CodexFlow.QueryRuntime.Protocol` | 稳定的 v2 数据契约与运行状态 |
| `CodexFlow.QueryRuntime.Engine` | `IAgentRuntime`、v2 循环、工具管线、上下文、审计、回放和恢复 |
| `CodexFlow.QueryRuntime.Models` | 模型供应商适配器 |
| `CodexFlow.QueryRuntime.Abstractions` | 为迁移保留的兼容抽象 |
| `CodexFlow.QueryRuntime.Experimental` | 宿主组合、内置工具和外部工具适配器 |
| `CodexFlow.QueryRuntime.Cli` | `qre` 命令行宿主 |
| `CodexFlow.QueryRuntime.Sandbox.LocalProcess` | 可信本地进程 runner |
| `CodexFlow.QueryRuntime.Sandbox.Docker` | Docker 隔离适配器 |
| `CodexFlow.QueryRuntime.UnitTests` | 确定性单元与契约测试 |
| `CodexFlow.QueryRuntime.IntegrationTests` | CLI、供应商、沙箱和端到端测试 |

## 构建与测试

必需环境为 .NET 10 SDK。Docker、Python 和 Node.js 是可选依赖，只在使用对应适配器或集成测试时需要。

```bash
dotnet build CodexFlow.QueryRuntime.slnx
dotnet test CodexFlow.QueryRuntime.UnitTests/CodexFlow.QueryRuntime.UnitTests.csproj
dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj
```

发布自包含 Native AOT CLI 时，将 `<RID>` 替换为 `win-x64`、`linux-x64` 或 `osx-arm64` 等受支持的运行时标识：

```bash
dotnet publish CodexFlow.QueryRuntime.Cli/CodexFlow.QueryRuntime.Cli.csproj \
  -c Release -r <RID> -p:PublishAot=true -p:SelfContained=true
```

## CLI 快速开始

以下命令既可通过已发布的 `qre` 执行，也可以在前面加上 `dotnet run --project CodexFlow.QueryRuntime.Cli --` 从源码运行。

```bash
qre --version
qre init --workspace . --json
qre doctor --workspace . --json

# 确定性离线运行，不需要供应商凭据
qre run --workspace . --trace-data sanitized --response "offline smoke" --json "分析这个仓库"

# 查看工具并运行只读任务
qre tool list --workspace . --profile readonly --json
qre run --workspace . --profile readonly --trace-data sanitized --response "offline readonly" "总结项目结构"

# 查看并严格回放最近一次 v2 运行
qre trace latest --workspace . --json
qre replay latest --workspace . --strict --json
```

连接真实 OpenAI-compatible 或 vLLM-compatible 服务：

```bash
qre run --workspace . \
  --api-url http://localhost:8000/v1 \
  --api-key <key> \
  --model <model> \
  --api-mode chat-completions \
  "检查这个仓库并报告主要风险"
```

也可以使用 `QRE_API_URL`、`QRE_API_KEY`、`QRE_MODEL` 和 `QRE_API_MODE` 环境变量。`--response` 用于确定性离线测试；`--json-output` 要求模型输出 JSON，`--json` 则控制 CLI 自身以 JSON 格式输出结果。

thinking 默认使用 `auto`：启用工具或模型 JSON 输出时会关闭 thinking，以兼容更多供应商。只有确认供应商支持相应组合时才使用 `--thinking on` 或 `--thinking preserve`。

## Trace 数据与恢复

新的 v2 运行数据写入 workspace 下的 `.qre/v2/`：

- `public`：可分享的脱敏审计数据，不可用于恢复。
- `sanitized`：按策略移除敏感值后的运行细节。
- `private`：本地恢复数据；启用检查点后包含可恢复检查点。

使用相同 workspace 和兼容的 Runtime 配置恢复未完成运行：

```bash
qre resume latest --workspace . --json
```

当 ownership、lease、检查点完整性、workspace identity、策略、工具目录、模型或 recovery compatibility 校验不一致时，Runtime 会拒绝恢复。精确保证请参阅 [H1 崩溃恢复实施报告](docs/h1-crash-resume-implementation-report.zh-CN.md)和[威胁模型](docs/h1-crash-resume-threat-model.md)。

## 嵌入 .NET 应用

当前预览包为 `CodexFlow.QueryRuntime.Engine` `0.2.0-preview.21`。应用应依赖 v2 接口：

- 使用 `CodexFlow.QueryRuntime.Engine.V2.IAgentRuntime` 发起新 Turn。
- 需要本地检查点恢复时使用 `CodexFlow.QueryRuntime.Engine.V2.IResumableAgentRuntime`。
- 使用 `CodexFlow.QueryRuntime.Protocol` 中的请求、状态、事件、工具、策略、审计和检查点契约。

`Experimental` 项目只提供可选的组合帮助器和工具适配器，不是另一套 Runtime 循环。现有 v1 宿主升级前请先阅读 [0.2 preview 迁移指南](docs/migration-0.2-preview.zh-CN.md)。

## 安全

应将模型输出、工具参数、外部工具 manifest、回放数据和 workspace 文件视为不可信输入。选择最小工具 profile，为写操作保留审批门禁，不要把密钥写入公开 trace，并使用隔离 runner 执行不可信命令。详见 [SECURITY.md](SECURITY.md)、[Runtime 威胁模型](docs/threat-model.md)和[工具能力说明](docs/tool-capabilities.md)。

## 文档

- [技术指南](docs/queryruntime-technical-guide.zh-CN.md)（[English](docs/queryruntime-technical-guide.md)）
- [0.2 preview 迁移指南](docs/migration-0.2-preview.zh-CN.md)（[English](docs/migration-0.2-preview.md)）
- [H1 崩溃恢复实施报告](docs/h1-crash-resume-implementation-report.zh-CN.md)
- [工具搜索](docs/toolsearch.md)与[工具分区矩阵](docs/queryruntime-tool-partition-matrix.md)
- [包来源与溯源](docs/package-source-provenance.md)

历史路线图和已完成实施计划保存在 `docs/archive/`，不应视为当前 Runtime 行为说明。

## 许可证

[MIT](LICENSE.txt)
