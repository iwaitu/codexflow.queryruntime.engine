# CodexFlow QueryRuntime (QRE)

[English](README.md) | **简体中文**

[![CI](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/ci.yml/badge.svg)](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/ci.yml)
[![Release](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/release.yml/badge.svg)](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.txt)

CodexFlow QueryRuntime（简称 **QRE**）是一个面向工业级 Agent 开发的跨平台 .NET 核心运行时与轻量级命令行 Harness。它专注于将模型循环、工具执行、策略门禁、严格审计回放、本地故障恢复（H1 Crash Recovery）、沙箱隔离与 SDK 出站诊断沉淀为高内聚、可复现、可跨平台发布的工程基础设施。

QRE 既可以通过官方 NuGet 包以原生 C# 形式嵌入到宿主应用中，也可以编译为零外部依赖的独立 `qre` Native AOT 命令行工具直接运行，完全解耦于 CodexFlow Web 平台。

当前仓库处于 **0.23.1 正式版（纯 V2 架构）**。所有新集成均构建在 `CodexFlow.QueryRuntime.Protocol`、`CodexFlow.QueryRuntime.Engine.V2` 与 `CodexFlow.QueryRuntime.Models` 之上；旧版 v1 API 已完全从执行面切断，仅保留数据层契约用于历史 Trace 兼容与迁移参考。

---

## 目录

- [为什么选择 QRE](#为什么选择-qre)
- [核心架构与代码分层](#核心架构与代码分层)
- [快速上手](#快速上手)
- [CLI 完整命令参考](#cli-完整命令参考)
  - [1. 工作区初始化与环境诊断 (`init`, `doctor`)](#1-工作区初始化与环境诊断-init-doctor)
  - [2. 执行 Agent 任务 (`run`)](#2-执行-agent-任务-run)
  - [3. 工具管理与单步调试 (`tool`)](#3-工具管理与单步调试-tool)
  - [4. 策略门禁静态核验 (`policy check`)](#4-策略门禁静态核验-policy-check)
  - [5. 审计轨迹与严格回放 (`trace`, `replay`, `rerun`)](#5-审计轨迹与严格回放-trace-replay-rerun)
  - [6. 故障断点续跑 (`resume`)](#6-故障断点续跑-resume)
  - [7. 文件变更对比 (`diff`)](#7-文件变更对比-diff)
  - [8. 受控沙箱命令执行 (`sandbox exec`)](#8-受控沙箱命令执行-sandbox-exec)
  - [9. SDK 出站诊断套件 (`diagnose`)](#9-sdk-出站诊断套件-diagnose)
- [工具体系与扩展机制](#工具体系与扩展机制)
  - [内置工具与安全分区 (Tool Profiles)](#内置工具与安全分区-tool-profiles)
  - [外部工具 Manifest 扩展 (Stdio & MCP)](#外部工具-manifest-扩展-stdio--mcp)
  - [动态工具检索 (Tool Search)](#动态工具检索-tool-search)
- [技能系统与工作流扩展 (Skill System & Workflows)](#技能系统与工作流扩展-skill-system--workflows)
  - [1. 概念定位：原子工具 (Tool) vs 领域技能 (Skill)](#1-概念定位原子工具-tool-vs-领域技能-skill)
  - [2. 标准 Skill 规范与工程结构](#2-标准-skill-规范与工程结构)
  - [3. 两阶段按需加载与发现机制](#3-两阶段按需加载与发现机制)
  - [4. 在 QRE 中的配置与使用](#4-在-qre-中的配置与使用)
  - [5. 典型生态技能与示例](#5-典型生态技能与示例)
- [沙箱隔离与安全模型](#沙箱隔离与安全模型)
- [故障自愈与确定性回放](#故障自愈与确定性回放)
- [模型支持与智能思考策略](#模型支持与智能思考策略)
  - [思考策略控制 (`--thinking`)](#思考策略控制---thinking)
  - [结构化输出与 JSON Schema 请求](#结构化输出与-json-schema-请求)
- [嵌入 .NET 应用开发指南](#嵌入-net-应用开发指南)
- [示例工程全景 (Examples)](#示例工程全景-examples)
- [构建、测试与 Native AOT 发布](#构建测试与-native-aot-发布)
- [技术文档导航](#技术文档导航)
- [开源许可证](#开源许可证)

---

## 为什么选择 QRE

许多 Agent 演示项目能快速搭建原型，但往往难以跨越“工程化”的鸿沟。模型接口行为不一、思考链（CoT）与工具调用冲突、缺乏执行权限边界、运行崩溃无法复现、SDK 序列化静默丢弃参数等问题屡见不鲜。

QRE 提供了介于“几十行轻量 Demo”与“庞大庞杂 SaaS 平台”之间的标准中间层：

1. **类型化 Agent 状态循环**：统一抽象 Turn、Step、Invocation，内置严谨的 Token/Step 预算控制与确定性上下文压缩。
2. **Fail-Closed 策略门禁**：四级安全工具分区，针对高危修改操作实施显式审批门禁（`--approve-risk`）。
3. **零成本确定性回放 (Strict Replay)**：不消耗 Token、不触发真实网络与工具，通过严格数据驱动校验历史轨迹，输出 byte-identical 的 `replay_digest`。
4. **H1 本地崩溃故障自愈 (Local Crash-Resume)**：单机环境下进程突发中断后，可通过检查点（Checkpoint）与租约（Attempt Lease）无损续跑，具备完善的防漂移校验。
5. **首创 SDK 出站诊断体系 (Outbound Diagnostics)**：捕获从 QRE 意图到模型 SDK 最终发送至 HTTP 通道的三层真实快照，精准排查模型厂商丢字段、格式错误等疑难问题。
6. **双沙箱执行环境**：受信任本地极速执行（LocalProcess）与容器化强隔离执行（Docker）无缝切换。
7. **极轻量、跨平台与 AOT**：基于 .NET 10，支持 Native AOT 编译为单文件原生二进制，启动毫秒级、无 JIT 开销。

---

## 核心架构与代码分层

```
┌────────────────────────────────────────────────────────┐
│               Host Applications / qre CLI              │
└───────────────────────────┬────────────────────────────┘
                            │
┌───────────────────────────▼────────────────────────────┐
│             CodexFlow.QueryRuntime.Engine (V2)         │
│  ┌──────────────────┐  ┌──────────────────┐  ┌───────┐ │
│  │  IAgentRuntime   │  │  Tool Pipeline   │  │ Audit │ │
│  │ State Reducer    │  │  Fail-Closed Gate│  │ & H1  │ │
│  └────────┬─────────┘  └────────┬─────────┘  └───────┘ │
└───────────┼─────────────────────┼──────────────────────┘
            │                     │
┌───────────▼──────────┐ ┌────────▼──────────┐ ┌─────────▼───────────┐
│     Models (MEAI)    │ │   Experimental    │ │   Sandbox Runners   │
│ OpenAI / vLLM / Claude│ │ Built-in Tools   │ │ LocalProcess        │
│ Outbound Diagnostics │ │ Tool Search / Stdio│ Docker Container    │
└──────────────────────┘ └───────────────────┘ └─────────────────────┘
            │                     │                      │
┌───────────┴─────────────────────┴──────────────────────┴───────────┐
│                 CodexFlow.QueryRuntime.Protocol                    │
│      Immutable Turn / Step / Tool / Event / Checkpoint Types       │
└────────────────────────────────────────────────────────────────────┘
```

| 工程模块 | 职责定位 |
|---|---|
| [`CodexFlow.QueryRuntime.Protocol`](CodexFlow.QueryRuntime.Protocol) | **协议层**：纯数据契约，定义会话、Turn、Step、工具输入输出、审计事件、策略模型及检查点等不可变状态。 |
| [`CodexFlow.QueryRuntime.Engine`](CodexFlow.QueryRuntime.Engine) | **核心引擎**：实现 `IAgentRuntime` 与 `IResumableAgentRuntime`，主导 V2 事件循环、状态机转移（State Reducer）、上下文裁剪压缩、审计存储与 H1 崩溃恢复。 |
| [`CodexFlow.QueryRuntime.Models`](CodexFlow.QueryRuntime.Models) | **模型适配层**：官方 NuGet 包，基于 `Microsoft.Extensions.AI` 适配 OpenAI-compatible、vLLM、Anthropic Messages 等接口协议，提供 SDK 出站诊断探针与语义映射。 |
| [`CodexFlow.QueryRuntime.Sandbox.LocalProcess`](CodexFlow.QueryRuntime.Sandbox.LocalProcess) | **本地沙箱**：面向受信任本地开发环境的高性能命令执行器。 |
| [`CodexFlow.QueryRuntime.Sandbox.Docker`](CodexFlow.QueryRuntime.Sandbox.Docker) | **容器沙箱**：提供真正的容器级隔离、工作区只读挂载、选择性写回与网络白名单控制。 |
| [`CodexFlow.QueryRuntime.Experimental`](CodexFlow.QueryRuntime.Experimental) | **工具生态层**：内置工具包（文件读写、搜索、Git、.NET 构建测试）、外部 Stdio/MCP 插件驱动、动态工具检索（Tool Search）。 |
| [`CodexFlow.QueryRuntime.Cli`](CodexFlow.QueryRuntime.Cli) | **命令行宿主**：提供原生跨平台 `qre` CLI 工具，原生支持 Native AOT 发布。 |

---

## 快速上手

### 1. 环境准备
- 必需：[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- 可选：Docker（用于容器沙箱）、Python 3.9+ / Node.js（用于运行外部脚本工具或运行测试套件）

### 2. 构建与运行验证
在仓库根目录直接编译整个解决方案：

```bash
# 构建整个解决方案
dotnet build CodexFlow.QueryRuntime.slnx

# 执行单元测试
dotnet test CodexFlow.QueryRuntime.UnitTests/CodexFlow.QueryRuntime.UnitTests.csproj
```

### 3. 三分钟极速体验

你可以直接通过 `dotnet run` 执行 CLI，也可以将其发布为本地 `qre` 二进制使用。

```bash
# 别名设置（Windows PowerShell 示例）
function qre { dotnet run --project D:/codeup/codexflow.queryruntime.engine/CodexFlow.QueryRuntime.Cli -- $args }

# 查看版本
qre --version

# 1. 确定性离线 Smoke 测试（无需任何 API Key 或网络）
qre run --workspace . --response "离线响应：QRE 运行正常" --json "你好，请自我介绍"

# 2. 以只读工具模式分析当前代码库
qre run --workspace . --profile readonly --response "已读取目录结构" "列出当前仓库的核心模块"

# 3. 严格离线回放刚刚的执行轨迹（零 Token、零副作用）
qre replay latest --workspace . --strict --json
```

### 4. 连接真实大语言模型 (OpenAI-compatible / vLLM / Claude)

```bash
# 通过命令行参数接入真实模型
qre run --workspace . \
  --api-url "https://api.openai.com/v1" \
  --api-key "sk-..." \
  --model "gpt-4o" \
  --api-mode chat-completions \
  --profile readonly \
  --stream \
  "分析当前项目的架构特点并总结主要风险"
```

你也可以配置标准环境变量，免去每次输入：
- `QRE_API_URL`：接口地址（例如 `http://localhost:8000/v1`）
- `QRE_API_KEY`：API 密钥
- `QRE_MODEL`：模型名称（例如 `qwen2.5-coder` 或 `gpt-4o`）
- `QRE_API_MODE`：接口模式，支持 `chat-completions`、`responses` 或 `anthropic-messages`

---

## CLI 完整命令参考

`qre` 提供了一套完备且严谨的子命令族，覆盖 Agent 的完整生命周期：

```
qre <command> [options]
  init       初始化当前工作区与 .qre 规范目录
  doctor     体检系统运行环境、依赖工具链与服务连通性
  run        启动 Agent 执行模型循环任务
  tool       枚举、注册或单步调试调用工具
  policy     静态核查工具命令的安全策略与审批门禁
  trace      审查执行轨迹、事件日志与 Token 用量
  replay     执行轨迹摘要阅读或确定性严格重放校验
  rerun      依据历史配置重新发起真实运行
  resume     读取检查点恢复中断的 Agent 任务 (H1 Recovery)
  diff       对比 Agent 运行产生的工作区文件补丁
  sandbox    在沙箱与策略门禁约束下执行单条命令
  diagnose   SDK 出站网络请求诊断与排查工具箱
```

### 1. 工作区初始化与环境诊断 (`init`, `doctor`)

- **`qre init`**：在工作区建立 `.qre/` 基础配置目录与外部工具目录 `.qre/tools/`。
  ```bash
  qre init --workspace . [--force] [--json]
  ```
- **`qre doctor`**：一键排查当前宿主环境状态，包括 .NET 运行时版本、Git 状态、Docker 守护进程连通性、Python/Node.js 可用性及供应商环境变量配置。
  ```bash
  qre doctor --workspace . [--json]
  ```

### 2. 执行 Agent 任务 (`run`)

`qre run` 是驱动 Agent 核心循环的主入口：

```bash
qre run --workspace <path> [options] "<prompt>"
```

#### 关键参数一览：

| 参数 | 说明 | 默认值 / 选项 |
|---|---|---|
| `-w, --workspace <path>` | 设定目标工作区根路径 | 当前工作目录 |
| `--profile, --tools <name>` | 工具安全策略分区 | `none`（可选：`none`, `readonly`, `verify`, `repair`） |
| `--api-url <url>` | 模型服务地址（支持 OpenAI / vLLM / 兼容端点） | 环境变量 `QRE_API_URL` |
| `--api-key <key>` | 模型访问凭证 | 环境变量 `QRE_API_KEY` |
| `--model <name>` | 目标模型标识 | 环境变量 `QRE_API_MODEL` / `QRE_MODEL` |
| `--api-mode <mode>` | 协议模式 | `chat-completions` / `responses` / `anthropic-messages` |
| `--response <text>` | 设定静态模型响应（用于无网络确定性回归测试） | 无 |
| `--runner <name>` | 命令沙箱执行器类型 | `local`（受信任本地执行）或 `docker`（容器隔离） |
| `--docker-image <img>` | 沙箱容器镜像名称（当 runner 为 docker 时使用） | 环境变量 `QRE_DOCKER_IMAGE` |
| `--external` | 自动载入 `.qre/tools/*.json` 定义的外部工具 | 默认关闭 |
| `--tool-search` | 开启动态工具按需检索，防止过多工具污染 Context | 默认关闭 |
| `--tool-search-top-k <n>`| 动态激活工具的最大数量 | 默认 `5` |
| `--required-tool <name>` | 强制 Agent 在首轮必须率先调用指定工具 | 无 |
| `--approve-risk <reason>`| 显式授权风险写操作（`repair` 分区写工具必填） | 无（未提供且触发高危工具时将直接阻断） |
| `--thinking <mode>` | 思考模式管理策略 | `auto`（推荐，有工具或要求 JSON 时自动关闭），可选 `off`, `on`, `preserve` |
| `--trace-data <mode>` | Trace 存储数据隐私级别 | `public`（默认脱敏，不可恢复）、`sanitized`（保真测试数据）、`private`（含恢复 checkpoint） |
| `--stream` | 实时流式输出模型思考内容与文本回答 | 默认关闭 |
| `--json` | 将 CLI 的最终执行结果以 JSON 格式输出到控制台 | 默认关闭 |
| `--json-output` | 提示模型返回纯 JSON 对象（此时会自动关闭 thinking） | 默认关闭 |
| `--max-rounds <n>` | 限制 Agent 循环的最大执行轮数 | 默认 `3` |
| `--sdk-diagnostics <m>` | 开启 SDK 出站网络诊断探针 | `off`（默认），可选 `metadata`, `structure` |

### 3. 工具管理与单步调试 (`tool`)

无需启动完整 Agent 循环，即可直接测试或管理工具：

```bash
# 查看指定 Profile 下的所有可用工具及其 JSON Schema
qre tool list --workspace . --profile readonly --json

# 同时列出内置工具与外部注册的工具
qre tool list --workspace . --profile verify --external

# 注册一个新的外部工具 Manifest
qre tool register --workspace . --manifest my_tool.manifest.json [--force]

# 独立单步执行指定工具（用于单元调试与行为验证）
qre tool invoke --workspace . --name qre_read_file --arguments '{"path":"README.md"}' --json
```

### 4. 策略门禁静态核验 (`policy check`)

在执行任何高危命令前，使用 policy check 静态判定当前规则是否放行：

```bash
# 验证在 verify 策略下是否允许执行测试
qre policy check --workspace . --profile verify --tool qre_dotnet_test -- dotnet test --no-restore

# 验证带审批原因的高危命令
qre policy check --workspace . --profile repair --tool qre_patch --approve-risk "重构代码" -- git apply
```

### 5. 审计轨迹与严格回放 (`trace`, `replay`, `rerun`)

- **查看轨迹**：
  ```bash
  # 查看最近一次运行的详细概要与事件流
  qre trace latest --workspace . --json
  
  # 流式输出事件全量 JSONL
  qre trace latest --workspace . --jsonl
  ```
- **确定性回放 (Replay)**：
  ```bash
  # 只读查看回放轨迹摘要（不运行任何代码）
  qre replay latest --workspace . --summary
  
  # 严格模式执行确定性回放（校验每一步状态转移并输出不可篡改的 replay_digest）
  qre replay latest --workspace . --strict --json
  ```
- **原样重跑 (Rerun)**：
  ```bash
  # 读取上一轮任务的 Prompt 与配置重新执行一次全新的任务
  qre rerun latest --workspace . --trace-data sanitized
  ```

### 6. 故障断点续跑 (`resume`)

当 Agent 运行由于断电、宿主崩溃或被意外中断时，若此前使用了 `--trace-data private` 或 `sanitized`，QRE 会通过检查点与租约锁定机制实现故障无损自愈：

```bash
# 从最新中断的检查点恢复执行（自动校验 workspace、tools、policy 防漂移一致性）
qre resume latest --workspace . --json
```

### 7. 文件变更对比 (`diff`)

```bash
# 查看最近一次 Agent 执行对工作区产生的代码修改（优先展示独立 diff.patch）
qre diff latest --workspace .

# 仅输出变更统计
qre diff latest --workspace . --stat --json
```

### 8. 受控沙箱命令执行 (`sandbox exec`)

通过 QRE 沙箱和安全策略管道执行命令，自动获得审计、超时与输出截断保护：

```bash
# 本地受控执行
qre sandbox exec --workspace . --profile verify -- dotnet build

# 在 Docker 容器沙箱内执行构建
qre sandbox exec --workspace . --runner docker --docker-image mcr.microsoft.com/dotnet/sdk:10.0 -- dotnet test
```

### 9. SDK 出站诊断套件 (`diagnose`)

排查模型调用“参数为何丢失”、“为什么厂商返回格式错误”的终极利器：

```bash
# 1. 运行任务并捕获结构化出站探针数据
qre run --workspace . --sdk-diagnostics structure "分析依赖"

# 2. 检查最近一次运行的出站记录
qre diagnose latest --workspace . --json

# 3. 对比两次运行的请求序列化差异（定位参数在哪里丢失）
qre diagnose compare <left_run_id> <right_run_id> --workspace .

# 4. 导出完全脱敏的诊断包（不包含密钥和代码正文，可安全分享排查）
qre diagnose export latest --output issue_diag.zip --workspace .

# 5. 离线生成请求骨架并重构 SDK 发送包（无需连接网络）
qre diagnose skeleton latest --output request_fixture.json
qre diagnose rebuild request_fixture.json --json
```

---

## 工具体系与扩展机制

### 内置工具与安全分区 (Tool Profiles)

QRE 遵循最小权限原则，通过四级 Profile 实施隔离：

```
       [ none ]        纯模型推理，无工具调用权限
          │
      [ readonly ]     文件遍历与检索 (qre_list_files, qre_read_file, qre_search_files)
          │
       [ verify ]      只读状态与环境验证 (+ git_status, git_diff, dotnet_build, dotnet_test)
          │
       [ repair ]      危险写入与修补操作 (+ qre_apply_patch, 外部写入工具) ──► 必须显式 --approve-risk
```

| Profile 分区 | 适用工具列表 | 说明与安全策略 |
|---|---|---|
| `none` | 无 | 禁用一切工具调用，仅做纯文本或 JSON 问答。 |
| `readonly` | `qre_list_files`, `qre_read_file`, `qre_search_files` | 只读文件与搜索工具，适用于代码走查、架构梳理、静态风险排查。 |
| `verify` | 包含 `readonly` 全部工具，外加 `qre_git_status`, `qre_git_diff`, `qre_dotnet_build`, `qre_dotnet_test` | 只读状态查看、编译构建与单元测试，用于构建合规审查与自动化验证。 |
| `repair` | 包含 `verify` 全部工具，外加写文件、打补丁等具备变更能力的工具 | 允许对工作区造成永久修改。**触发时必须携带 `--approve-risk "<理由>"`，否则 Fail-Closed 阻断**。 |

### 外部工具 Manifest 扩展 (Stdio & MCP)

QRE 原生支持通过外部独立脚本或程序来扩展工具集。只需在工作区创建 `.qre/tools/<tool_name>.json` 声明 Manifest：

```json
{
  "name": "calc_coverage",
  "description": "计算当前项目的代码测试覆盖率报告",
  "executable": "python",
  "arguments": ["scripts/calc_coverage.py"],
  "parameters": {
    "type": "object",
    "properties": {
      "format": {
        "type": "string",
        "enum": ["summary", "detailed"],
        "description": "报告格式"
      }
    },
    "required": ["format"]
  },
  "timeoutSeconds": 30
}
```
运行 `qre run --external ...` 即可自动识别并挂载这些外部工具。同时支持 minimal MCP stdio 工具。详细编写示例见 [`examples/ExternalTools`](examples/ExternalTools)、[`examples/PythonFunctionTools`](examples/PythonFunctionTools) 及 [`examples/NodeFunctionTools`](examples/NodeFunctionTools)。

### 动态工具检索 (Tool Search)

当挂载数十甚至上百个工具时，将所有 Schema 注入 Prompt 会导致模型上下文（Context）急剧膨胀甚至模型幻觉。

通过指定 `--tool-search`（配合 `--tool-search-top-k 5`）：
1. QRE 初始化时仅向模型暴露一个精简的元工具 `tool_search`；
2. 模型根据当前目标调用 `tool_search(query="coverage")` 进行语义检索；
3. QRE 动态将召回的最相关 Top-K 工具激活并注入下一轮上下文；
4. 有效节省 80% 以上的工具元数据 Token 消耗。

---

## 技能系统与工作流扩展 (Skill System & Workflows)

在工业级复杂研发任务中，单纯依靠底层原子工具（如读写文件、执行命令）往往容易导致模型在长链条规划中迷失方向，而将所有企业规范和技术栈步骤一股脑塞入 System Prompt 又会迅速耗尽上下文窗口。QRE 原生支持 **Skill（技能包）** 扩展机制，为 Agent 注入垂直领域的程序性知识（Procedural Knowledge）。

### 1. 概念定位：原子工具 (Tool) vs 领域技能 (Skill)

| 维度 | 原子工具 (Tool) | 领域技能包 (Skill) |
|---|---|---|
| **本质定位** | 确定性的单次系统 I/O 原语 | 封装垂直领域程序性知识的标准化工作流与指导包 |
| **执行粒度** | 单步、细粒度（如 `read_file`, `dotnet_test`） | 端到端、多阶段（如 `code-reviewer`, `csharp-scaffolder`） |
| **内容构成** | 可执行函数/进程、输入 JSON Schema、四级安全分区 | `SKILL.md` (规范元数据+详细工作流) + 脚本 (`scripts/`) + 参考手册 (`references/`) |
| **Token 预算** | Schema 随对话轮次全量加载或按需检索 | **两阶段加载**：初次仅加载轻量元数据，触发命中后动态激活详细正文 |
| **安全机制** | 受 QRE 四级 Profile 与 Fail-Closed 策略门禁严格拦截 | 规程指导在上下文中执行；附带的脚本在受控沙箱环境下运行 |

### 2. 标准 Skill 规范与工程结构

每个技能均以自包含目录形式组织，核心契约是一个标准的 `SKILL.md` 文件：

```text
my-custom-skill/
├── SKILL.md                 # 【必选】YAML Frontmatter 元数据 + Markdown 指导规程
├── scripts/                 # 【可选】确定性自动化脚本 (Python/Bash/Node.js 等)
├── references/              # 【可选】按需载入的领域规范、Schema 字典或 API 契约
└── assets/                  # 【可选】项目初始化模板、图标或样板代码资源
```

#### `SKILL.md` 规范示例
```markdown
---
name: code-reviewer
description: 针对企业级架构边界、零警告编译、不可变协议与安全性进行全方位代码走查。当需要评估 PR 或审查新增代码时使用。
---

# Code Reviewer Skill

## 审查原则与阶段
1. 架构分层合规性检查（Protocol 契约层不可反向依赖 Engine/UI）；
2. 资源生命周期管理（所有 IDisposable 均需显式管理）；
3. 编译器零警告标准（Nullable enable 严格检查）。
```

### 3. 两阶段按需加载与发现机制

为了最大化节省上下文 Token 预算，QRE 与 `VllmChatClient` 采用了**渐进式两阶段加载（Two-Phase Progressive Loading）**策略：

```
┌────────────────────────────────────────────────────────┐
│  阶段 1: 扫描与轻量元数据发现 (Low Token Cost)         │
│  客户端扫描技能目录，提取 SKILL.md Frontmatter，       │
│  将精炼的 `# Skills` 目录注入 System Prompt            │
└───────────────────────────┬────────────────────────────┘
                            │
┌───────────────────────────▼────────────────────────────┐
│  阶段 2: 动态激活与按需规程加载 (On-Demand Loading)    │
│  Agent 推理命中意图 -> 自动调度 `ReadSkillFile` 工具    │
│  仅在需要时将完整规程加载入上下文，指导后续多步执行    │
└────────────────────────────────────────────────────────┘
```

#### 多级技能目录发现路径
QRE 会自动按以下优先级多级探测技能根目录：
1. 显式配置的 `SkillDirectoryPath`；
2. 环境变量 `CODEX_SKILLS_DIR`；
3. 本地应用目录 `./skills`；
4. 当前工作区 `./skills` 或 `.qre/skills`；
5. 逐级向父目录扫描探测。

### 4. 在 QRE 中的配置与使用

#### 在 C# 宿主应用中启用技能支持
通过 `VllmChatOptions` 开启技能并指定目录，底层客户端会自动完成技能目录索引与内置工具注册：

```csharp
using CodexFlow.QueryRuntime.Models;
using Microsoft.Extensions.AI;

var chatOptions = new VllmChatOptions
{
    EnableSkills = true,
    SkillDirectoryPath = Path.Combine(AppContext.BaseDirectory, "skills"),
    ThinkingEnabled = true
};

// VllmChatClient 会自动在系统提示词中声明技能清单，并注入两个核心工具：
// 1. ListSkillFiles: 列出所有可用技能名称与描述
// 2. ReadSkillFile: 按需读取指定技能的完整 Markdown 指导内容
```

#### CLI 任务调度联动
通过命令行启动任务时，可直接在任务目标中指定技能规程：
```bash
qre run --workspace . --profile verify "遵循 code-reviewer 技能规范对本次改动执行架构走查"
```

### 5. 典型生态技能与示例

| 技能类别 | 典型代表 | 核心价值 |
|---|---|---|
| **工程脚手架** | `csharp-scaffolder`, `node-scaffolder`, `python-scaffolder` | 按照规范模板与分层原则初始化聚合根、Controller 或模块骨架。 |
| **质量与安全审计** | `nodejs-security`, `python-security`, `java-security` | 封装 Bandit、ESLint Security、SpotBugs 等垂直工具扫描规程。 |
| **元技能体系** | `skill-creator` | 指导开发者以标准化规范自动创建、测试和打包新技能包。 |
| **依赖与包治理** | `nuget-package-versions` | 在编辑项目工程文件前检索真实的官方包版本，避免解析还原死锁。 |

> 完整可运行代码与技能目录示范请参见 [`examples/SkillsWorkflow`](examples/SkillsWorkflow)。

---

## 沙箱隔离与安全模型

QRE 坚持 **Fail-Closed（默认拒绝）** 的安全理念，并将模型输出、外部工具参数、外部 Manifest 均视为不可信输入：

1. **`LocalProcessSandboxRunner`**：
   - 适用于受信任的个人开发环境；
   - 具备进程超时终止、最大输出缓冲截断、参数注入转义防护；
   - *注意：本地进程 runner 不是硬安全隔离边界。*
2. **`DockerSandboxRunner`**：
   - 面向不可信指令或自动化流水线的强隔离方案；
   - 支持只读绑定挂载（Read-Only Mount）；
   - 支持工作区修改选择性回写（Selective Write-Back）；
   - 支持网络禁用或白名单策略（需结合支持的容器网络驱动）；
   - 自定义镜像隔离（通过 `--docker-image <image_name>` 指定）。
3. **审计与数据脱敏等级**：
   - `public`：所有敏感信息脱敏，不保留 Checkpoint，适合公开提交或作为公共 Issue 附件；
   - `sanitized`：用于离线合成受审查的测试用例（Synthetic Fixtures），保真保留结构；
   - `private`：本地诊断与故障恢复模式，受系统权限保护，支持故障断点恢复。

---

## 故障自愈与确定性回放

### H1 Crash Recovery（单机故障自愈）
当执行长时间运行的任务或复杂的修复流程时，若进程意外退出，QRE 的 V2 状态机允许任务无损恢复：
- **原子检查点 (Checkpointing)**：每一步关键状态转移均持久化写入 `.qre/v2/checkpoints/`；
- **租约锁 (Attempt Lease)**：防止并发或双重恢复导致状态污染；
- **防漂移严格校验**：恢复时严格对比 Workspace 路径、Tool Registry 散列值、Policy 规格与 Model 配置，一旦环境被篡改则立即拒绝恢复以确保安全。
- 详细设计参见 [H1 崩溃恢复实施报告](docs/h1-crash-resume-implementation-report.zh-CN.md)。

### Strict Replay（确定性回放）
QRE 拥有出色的离线调试能力：
- 严格回放不会向任何大模型发送网络请求，也不会执行本地工具；
- 注入确定性时钟（Deterministic Clock）与 Query ID；
- 比对整个状态转移序列，输出唯一的 `replay_digest`；
- 彻底解决 Agent 开发中“难以在本地稳定复现生产 Bug”的痛点。

---

## 模型支持与智能思考策略

### 模型供应商支持
QRE 底层基于 `Microsoft.Extensions.AI` 构建，并通过官方 `CodexFlow.QueryRuntime.Models` 包对各主流形态进行标准化：
- **OpenAI-compatible**（OpenAI, Azure OpenAI, DeepSeek, 月之暗面, 智谱等）；
- **vLLM**（本地部署的高性能推理集群）；
- **Anthropic Messages** 风格端点。

### 思考策略控制 (`--thinking`)
许多新型大模型具备思考链（CoT / Reasoning）功能，但在同时启用工具调用或强制指定 JSON Schema 输出时，部分模型厂商容易出现格式损坏。QRE 内置智能协调策略：
- **`auto`（默认推荐）**：当检测到任务启用了工具调用，或者开启了 `--json-output` 时，QRE 会主动关闭思考链参数，确保工具调用稳定与 JSON 解析可靠；在纯文本无工具任务时保留模型默认表现。
- **`off`**：强制关闭模型的思考链。
- **`on`**：强制开启思考链（确保目标模型厂商支持工具与思考链混用）。
- **`preserve`**：完全不干预，保留模型客户端原始选项。

### 结构化输出与 JSON Schema 请求

在需要 Agent 产出具备确定性格式（如架构报告、漏洞清单、任务分解表）的工业场景中，QRE 原生支持基于严格 JSON Schema 的结构化输出。

> [!NOTE]
> **底层驱动**：Logits 级别的严格文法状态机引导解码（Guided Decoding / Grammar-constrained Sampling）底层由 **`VllmChatClient`**（面向 OpenAI-compatible、vLLM、SGLang 等服务）驱动。
> **QRE 协同**：QRE 运行时负责上层 Agent 循环编排、`QreThinkingPolicy` 思考策略协同，以及全链路出站诊断（Outbound Diagnostics）监控。

#### 1. 强模式 vs 弱提示模式
- **`ChatResponseFormat.Json` (`json_object`)**：模型仅在 Prompt 中被提示输出合法 JSON，但无字段模式校验，可能出现漏字段或类型偏差；
- **`ChatResponseFormat.ForJsonSchema(...)` (`json_schema`)**：由 `VllmChatClient` 在采样解码（Logits）层面施加状态机语法树强约束，保证生成的 Token 序列 **100% 严格符合指定的 JSON Schema**。

#### 2. .NET 宿主应用使用示例
```csharp
using System.Text.Json;
using CodexFlow.QueryRuntime.Models;
using Microsoft.Extensions.AI;

// 1. 定义 JSON Schema
var schemaJson = """
{
  "type": "object",
  "properties": {
    "summary": { "type": "string" },
    "severity": { "type": "string", "enum": ["Low", "Medium", "High", "Critical"] },
    "approved": { "type": "boolean" },
    "violations": { "type": "array", "items": { "type": "string" } }
  },
  "required": ["summary", "severity", "approved", "violations"],
  "additionalProperties": false
}
""";

using var doc = JsonDocument.Parse(schemaJson);
var jsonSchemaFormat = ChatResponseFormat.ForJsonSchema(
    doc.RootElement,
    schemaName: "code_review_report",
    schemaDescription: "严格约束自动化代码审查结果结构");

// 2. 配置模型请求
var options = new VllmChatOptions
{
    ResponseFormat = jsonSchemaFormat,
    Temperature = 0.1f,
    MaxOutputTokens = 1024
};
```

#### 3. QRE 运行时的配套工程保障
- **思考策略协同 (`QreThinkingPolicy`)**：智能协同模型思考链（Reasoning / CoT）与 JSON Schema 约束解码的共存，避免强约束拦截模型的前置推理过程；
- **全链路出站诊断 (`qre diagnose` / Outbound Diagnostics)**：通过三层探针（意图 $\rightarrow$ 适配层 $\rightarrow$ 传输层 HTTP Request）深度检测 `response_format`，精准捕获中间网关或模型代理可能发生的 `response_format_dropped`（字段丢弃）静默降级风险。

> 完整可运行代码与强类型 POCO 反序列化示范请参见 [`examples/StructuredOutputsJsonSchema`](examples/StructuredOutputsJsonSchema)。

---

## 嵌入 .NET 应用开发指南

你可以直接将 QRE 核心与模型库作为 NuGet 包集成到任意 C# / .NET 10 应用中（Web API、后台服务或桌面客户端）。

### 1. 引用核心包
在你的 `.csproj` 中引入官方 NuGet 包（`0.23.1`）：

```xml
<ItemGroup>
  <PackageReference Include="CodexFlow.QueryRuntime.Engine" Version="0.23.1" />
  <PackageReference Include="CodexFlow.QueryRuntime.Models" Version="0.23.1" />
</ItemGroup>
```

*(说明：`CodexFlow.QueryRuntime.Protocol` 契约程序集已作为内部依赖打包在 Engine 包中)。*

### 2. 初始化运行时与发起 Agent 任务

```csharp
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;

// 1. 初始化模型客户端 (支持 Static 离线客户端或基于 MEAI 的在线客户端)
var modelClient = new StaticRuntimeModelClient("你好！我是 QRE 嵌入式 Agent。");
// 在线场景可使用:
// var modelClient = new MeaiRuntimeModelClient(chatClient, QreModelApiMode.ChatCompletions);

// 2. 构造 V2 运行时实例
IAgentRuntime runtime = new AgentRuntime(modelClient);

// 3. 构建请求上下文 (定义会话ID、目标任务、策略快照与预算)
var sessionId = new RuntimeSessionId(Guid.NewGuid().ToString("N"));
var turnId = new RuntimeTurnId(Guid.NewGuid().ToString("N"));
string userGoal = "分析当前目录的代码结构";

var request = new RuntimeAgentLoopRequest(
    sessionId,
    turnId,
    userGoal,
    [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(userGoal)])],
    [], // 传入工具列表
    ModelParameters: new RuntimeModelParameters(),
    Policy: new RuntimePolicySnapshot("prod-policy", "readonly"),
    Environment: new RuntimeEnvironmentSnapshot("local", Path.GetFullPath("."), "my-host-app"),
    Budget: new RuntimeBudgetSnapshot(maxSteps: 5, maxToolCalls: 10)
);

// 4. 实现事件监听器以实现实时 UI 流式更新
var eventSink = new DelegateEventSink(runtimeEvent =>
{
    if (runtimeEvent.Type == RuntimePresentationEventType.TextDelta)
    {
        Console.Write(runtimeEvent.Text);
    }
    else if (runtimeEvent.Type == RuntimePresentationEventType.ToolCallRequested)
    {
        Console.WriteLine($"\n[调用工具]: {runtimeEvent.ToolName}");
    }
    return ValueTask.CompletedTask;
});

// 5. 启动执行
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var result = await runtime.RunAsync(new RuntimeRunRequest(request), eventSink, cts.Token);

Console.WriteLine($"\n任务状态: {result.Status}，总步数: {result.Turn.Steps.Count}");
```

详细嵌入范例代码请参考 [`examples/EmbeddedV2`](examples/EmbeddedV2)。

---

## 示例工程全景 (Examples)

仓库下的 [`examples/`](examples/README.md) 目录提供了 10 个开箱即用的完整工程，展示了 QRE 在不同语言、不同场景下的集成最佳实践：

| 示例工程 | 核心亮点与场景 | 文档与代码 |
|---|---|---|
| **EmbeddedV2** | 原生 .NET 代码嵌入 `IAgentRuntime` 极简示范，离线确定性模型。 | [查看示例](examples/EmbeddedV2) |
| **RepoDoctor** | 生产级 .NET CLI 宿主应用，集成自定义诊断工具、实时流式输出与严格回放验证。 | [查看示例](examples/RepoDoctor) |
| **PythonToolDoctor** | Python 编写的外部 CLI 宿主，集成 QRE 核心内置工具并验证严格回放。 | [查看示例](examples/PythonToolDoctor) |
| **ExternalTools** | 最小化 Stdio 外部工具 Manifest 配置，演示模型如何调用本地 Python 脚本工具。 | [查看示例](examples/ExternalTools) |
| **PythonFunctionTools** | 演示如何在 Python 中编写原生函数并通过注解自动生成 QRE Manifest。 | [查看示例](examples/PythonFunctionTools) |
| **NodeFunctionTools** | 演示在 Node.js (ESM) 中编写函数工具并自动生成标准 Manifest。 | [查看示例](examples/NodeFunctionTools) |
| **H1CrashResume** | 模拟进程执行中断，演练通过 `qre resume` 携带检查点无缝恢复任务状态。 | [查看示例](examples/H1CrashResume) |
| **SdkOutboundDiagnostics**| 出站诊断综合演示：模拟缺陷宿主、排查请求变形丢失、离线重建请求体。 | [查看示例](examples/SdkOutboundDiagnostics) |
| **SkillsWorkflow** | 技能包组织 (`SKILL.md`)、渐进式两阶段元数据注入与 `ReadSkillFile` 自动调度。 | [查看示例](examples/SkillsWorkflow) |
| **StructuredOutputsJsonSchema** | 基于 `VllmChatClient` 引导解码的强约束 JSON Schema 结构化请求与 POCO 反序列化。 | [查看示例](examples/StructuredOutputsJsonSchema) |

### 一键回归所有示例
仓库提供了自动化脚本一次性运行全量 10 个示例进行回归校验：
```bash
python scripts/test-examples.py
```

---

## 构建、测试与 Native AOT 发布

### 基础构建与单元测试
```bash
# 编译整个解决方案
dotnet build CodexFlow.QueryRuntime.slnx

# 运行单元测试
dotnet test CodexFlow.QueryRuntime.UnitTests/CodexFlow.QueryRuntime.UnitTests.csproj

# 运行集成测试 (需具备本地环境依赖)
dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj
```

### Native AOT 单文件发布
QRE CLI 针对 .NET 10 Native AOT 进行了深度优化与修剪剪裁配置，可直接发布为无宿主依赖、毫秒级冷启动的原生可执行单文件：

```bash
# Windows (x64)
dotnet publish CodexFlow.QueryRuntime.Cli/CodexFlow.QueryRuntime.Cli.csproj \
  -c Release -r win-x64 -p:PublishAot=true -p:SelfContained=true

# Linux (x64)
dotnet publish CodexFlow.QueryRuntime.Cli/CodexFlow.QueryRuntime.Cli.csproj \
  -c Release -r linux-x64 -p:PublishAot=true -p:SelfContained=true

# macOS Apple Silicon (ARM64)
dotnet publish CodexFlow.QueryRuntime.Cli/CodexFlow.QueryRuntime.Cli.csproj \
  -c Release -r osx-arm64 -p:PublishAot=true -p:SelfContained=true
```

---

## 技术文档导航

- 📘 [QRE 架构技术指南 (中文)](docs/queryruntime-technical-guide.zh-CN.md) ｜ [English](docs/queryruntime-technical-guide.md)
- 🧭 [0.2 预览版迁移指南 (中文)](docs/migration-0.2-preview.zh-CN.md) ｜ [English](docs/migration-0.2-preview.md)
- 🛡️ [安全政策与漏洞提报 (SECURITY.md)](SECURITY.md)
- 🔒 [运行时威胁模型分析 (Threat Model)](docs/threat-model.md)
- 🧰 [工具能力说明 (Tool Capabilities)](docs/tool-capabilities.md)
- 🔍 [动态工具检索设计 (Tool Search)](docs/toolsearch.md) 与 [工具分区矩阵](docs/queryruntime-tool-partition-matrix.md)
- 🔄 [H1 崩溃恢复实施报告](docs/h1-crash-resume-implementation-report.zh-CN.md) 与 [威胁模型](docs/h1-crash-resume-threat-model.md)
- 🩺 [SDK 出站诊断指南 (中文)](docs/sdk-outbound-diagnostics.zh-CN.md) ｜ [English](docs/sdk-outbound-diagnostics.md) ｜ [验收报告](docs/sdk-outbound-diagnostics-acceptance-report.zh-CN.md)
- 📦 [包来源与分发溯源说明 (Package Provenance)](docs/package-source-provenance.md)
- 🏛️ [架构决策记录 (ADR 索引)](docs/adr/)：
  - [ADR-001：本地单进程运行时](docs/adr/ADR-001-local-single-process-runtime.md)
  - [ADR-002：Session-Turn-Step 生命周期](docs/adr/ADR-002-session-turn-step-lifecycle.md)
  - [ADR-003：运行时 IR 与模型适配器](docs/adr/ADR-003-runtime-ir-and-model-adapters.md)
  - [ADR-004：状态事件与数据分层](docs/adr/ADR-004-state-events-and-data-layers.md)
  - [ADR-005：工具执行流水线](docs/adr/ADR-005-tool-execution-pipeline.md)
  - [ADR-007：V2 架构彻底收口](docs/adr/ADR-007-v2-only-cutover.md)
  - [ADR-008：本地崩溃恢复 (H1 Crash-Resume)](docs/adr/ADR-008-local-crash-resume.md)
  - [ADR-009：SDK 出站诊断探针系统](docs/adr/ADR-009-sdk-outbound-diagnostics.md)

---

## 开源许可证

本项目采用 [MIT 许可证](LICENSE.txt)。
