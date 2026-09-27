# CodexFlow QueryRuntime (QRE)

**English** | [简体中文](README.zh-CN.md)

[![CI](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/ci.yml/badge.svg)](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/ci.yml)
[![Release](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/release.yml/badge.svg)](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.txt)

CodexFlow QueryRuntime (**QRE**) is a cross-platform .NET agent runtime and lightweight command-line harness engineered for production-grade agentic systems. It packages model iteration loops, policy-gated tool execution, audit and replay, fault recovery (H1 Crash Recovery), sandboxing, and client-side outbound diagnostics into a cohesive, deterministic, and self-contained infrastructure.

QRE can be embedded directly into .NET host applications via official NuGet packages or published as a standalone, zero-dependency `qre` Native AOT binary, operating independently of the CodexFlow web platform.

This repository is on the **0.23.2 stable, v2-only** line. All active development and integrations target `CodexFlow.QueryRuntime.Protocol`, `CodexFlow.QueryRuntime.Engine.V2`, and `CodexFlow.QueryRuntime.Models`. The legacy v1 API has been severed from active execution and remains strictly for historical trace inspection and migration compatibility.

---

## Table of Contents

- [Why QueryRuntime (QRE)](#why-queryruntime-qre)
- [Architecture & Layering](#architecture--layering)
- [Quickstart](#quickstart)
- [CLI Command Reference](#cli-command-reference)
  - [1. Workspace Initialization & Diagnostics (`init`, `doctor`)](#1-workspace-initialization--diagnostics-init-doctor)
  - [2. Running Agent Tasks (`run`)](#2-running-agent-tasks-run)
  - [3. Tool Catalog & Invocation (`tool`)](#3-tool-catalog--invocation-tool)
  - [4. Policy & Security Pre-flight (`policy check`)](#4-policy--security-pre-flight-policy-check)
  - [5. Auditing, Replay & Rerun (`trace`, `replay`, `rerun`)](#5-auditing-replay--rerun-trace-replay-rerun)
  - [6. Fault Recovery (`resume`)](#6-fault-recovery-resume)
  - [7. Workspace Diff (`diff`)](#7-workspace-diff-diff)
  - [8. Controlled Sandbox Execution (`sandbox exec`)](#8-controlled-sandbox-execution-sandbox-exec)
  - [9. SDK Outbound Diagnostics Suite (`diagnose`)](#9-sdk-outbound-diagnostics-suite-diagnose)
- [Tool System & Ecosystem Extension](#tool-system--ecosystem-extension)
  - [Built-in Tools & Policy Profiles](#built-in-tools--policy-profiles)
  - [External Tool Manifests (Stdio & MCP)](#external-tool-manifests-stdio--mcp)
  - [Dynamic Tool Retrieval (Tool Search)](#dynamic-tool-retrieval-tool-search)
- [Skill System & Workflows](#skill-system--workflows)
  - [1. Conceptual Positioning: Atomic Tools vs. Domain Skills](#1-conceptual-positioning-atomic-tools-vs-domain-skills)
  - [2. Standard Skill Specification & Directory Structure](#2-standard-skill-specification--directory-structure)
  - [3. Two-Phase Progressive Loading & Discovery](#3-two-phase-progressive-loading--discovery)
  - [4. Configuration & Usage in QRE](#4-configuration--usage-in-qre)
  - [5. Ecosystem Skills & Typical Scenarios](#5-ecosystem-skills--typical-scenarios)
- [Sandboxing & Security Model](#sandboxing--security-model)
- [Fault Recovery & Deterministic Replay](#fault-recovery--deterministic-replay)
- [Troubleshooting & Logs](#troubleshooting--logs)
- [Model Providers & Thinking Policies](#model-providers--thinking-policies)
  - [Thinking Policy Control (`--thinking`)](#thinking-policy-control---thinking)
  - [Structured Outputs via JSON Schema](#structured-outputs-via-json-schema)
- [Embedding in .NET Applications](#embedding-in-net-applications)
- [Runnable Examples Showcase](#runnable-examples-showcase)
- [Building, Testing & Native AOT Publish](#building-testing--native-aot-publish)
- [Technical Documentation Index](#technical-documentation-index)
- [License](#license)

---

## Why QueryRuntime (QRE)

Prototype agents are easy to build, but hardening them into reliable software is notoriously difficult. Developers constantly battle inconsistent LLM API behaviors, Chain-of-Thought (CoT) colliding with tool calls, uncontained execution side effects, unreproducible runtime crashes, and SDKs silently dropping structured arguments.

QRE fills the gap between "a 50-line script" and "a heavyweight SaaS platform":

1. **Typed Agent Loop**: Formal abstraction over Session, Turn, Step, and Invocation, with strict Token/Step budget enforcement and deterministic context compaction.
2. **Fail-Closed Policy Pipeline**: Four-tier security profiles with mandatory explicit approval gates (`--approve-risk`) for write operations.
3. **Zero-Token Strict Replay**: Offline, zero-cost, deterministic replay that validates historical execution trajectories without contacting models or executing tools, yielding a byte-identical `replay_digest`.
4. **H1 Local Crash Recovery**: Robust process self-healing with attempt leases, atomic checkpoints, and drift prevention against workspace, policy, or tool changes.
5. **SDK Outbound Diagnostics**: End-to-end client-side request inspection capturing QRE intent, MEAI options, and HTTP handler payloads to pinpoint argument dropping or serialization bugs.
6. **Dual Sandbox Execution**: High-speed trusted execution (`LocalProcess`) and isolated container execution (`Docker`) with selective write-back and network restrictions.
7. **Native AOT Ready**: Built on .NET 10, publishing into self-contained native binaries with sub-second startup times and zero JIT overhead.

---

## Architecture & Layering

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

| Project | Responsibility |
|---|---|
| [`CodexFlow.QueryRuntime.Protocol`](CodexFlow.QueryRuntime.Protocol) | **Protocol Contracts**: Immutable data models for sessions, turns, steps, tools, audit events, policy profiles, and recovery checkpoints. |
| [`CodexFlow.QueryRuntime.Engine`](CodexFlow.QueryRuntime.Engine) | **Core Loop**: `IAgentRuntime` and `IResumableAgentRuntime`, state reducer, tool execution pipeline, context compaction, and H1 checkpointing. |
| [`CodexFlow.QueryRuntime.Models`](CodexFlow.QueryRuntime.Models) | **Model Adapters**: Official package based on `Microsoft.Extensions.AI` for OpenAI-compatible, vLLM, and Anthropic endpoints, with outbound diagnostic observers. |
| [`CodexFlow.QueryRuntime.Sandbox.LocalProcess`](CodexFlow.QueryRuntime.Sandbox.LocalProcess) | **Local Sandbox**: High-speed command runner for trusted developer environments. |
| [`CodexFlow.QueryRuntime.Sandbox.Docker`](CodexFlow.QueryRuntime.Sandbox.Docker) | **Docker Sandbox**: Container isolation, read-only mounts, selective write-back, and network policies. |
| [`CodexFlow.QueryRuntime.Experimental`](CodexFlow.QueryRuntime.Experimental) | **Tool Extensions**: Built-in tool packs (file ops, search, git, dotnet), external stdio/MCP tool adapters, and dynamic Tool Search. |
| [`CodexFlow.QueryRuntime.Cli`](CodexFlow.QueryRuntime.Cli) | **Command-line Host**: Standalone `qre` executable supporting cross-platform Native AOT publication. |

---

## Quickstart

### Prerequisites
- Required: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Optional: Docker (for container sandbox), Python 3.9+ / Node.js (for external tools or regression scripts)

### Build and Test
```bash
# Build the entire solution
dotnet build CodexFlow.QueryRuntime.slnx

# Run deterministic unit tests
dotnet test CodexFlow.QueryRuntime.UnitTests/CodexFlow.QueryRuntime.UnitTests.csproj
```

### 3-Minute Smoke Test
Commands can be executed via `dotnet run` or from a published `qre` binary:

```bash
# Optional alias in PowerShell:
# function qre { dotnet run --project D:/codeup/codexflow.queryruntime.engine/CodexFlow.QueryRuntime.Cli -- $args }

# Check CLI version
qre --version

# 1. Deterministic offline smoke test (no credentials or network required)
qre run --workspace . --response "Offline smoke: QRE is operational" --json "hello world"

# 2. Inspect codebase using the read-only tool profile
qre run --workspace . --profile readonly --response "read files" "summarize the repository layout"

# 3. Deterministic strict replay of the latest run (zero token cost, zero side-effects)
qre replay latest --workspace . --strict --json
```

### Connect to Live Providers (OpenAI-compatible / vLLM / Claude)

```bash
qre run --workspace . \
  --api-url "https://api.openai.com/v1" \
  --api-key "sk-..." \
  --model "gpt-4o" \
  --api-mode chat-completions \
  --profile readonly \
  --stream \
  "inspect this codebase and report architectural risks"
```

Standard environment variables are also supported:
- `QRE_API_URL`: Provider endpoint (e.g. `http://localhost:8000/v1`)
- `QRE_API_KEY`: API authentication key
- `QRE_MODEL`: Target model (e.g. `qwen2.5-coder` or `gpt-4o`)
- `QRE_API_MODE`: Protocol mode (`chat-completions`, `responses`, or `anthropic-messages`)

---

## CLI Command Reference

The `qre` CLI provides a complete suite of commands covering the full agent lifecycle:

```
qre <command> [options]
  init       Initialize workspace and .qre metadata
  doctor     Run environment and dependency diagnostics
  run        Execute the agent runtime loop
  tool       List, register, or invoke tools directly
  policy     Pre-flight check tool execution policies and risks
  trace      Inspect event logs, tokens, and execution traces
  logs       Find runs by time and preview or execute date-based cleanup
  replay     Summarize traces or execute strict offline replay
  rerun      Re-execute the latest task with existing configuration
  resume     Recover an interrupted run from a checkpoint (H1)
  diff       Inspect workspace file changes from a run
  sandbox    Execute commands inside policy-controlled sandboxes
  diagnose   Inspect, compare, and rebuild SDK outbound HTTP requests
```

### 1. Workspace Initialization & Diagnostics (`init`, `doctor`)

- **`qre init`**: Sets up `.qre/` layout and `.qre/tools/` for external manifests.
  ```bash
  qre init --workspace . [--force] [--json]
  ```
- **`qre doctor`**: Verifies .NET version, Git state, Docker availability, Python/Node.js runtimes, and provider environment configurations.
  ```bash
  qre doctor --workspace . [--json]
  ```

### 2. Running Agent Tasks (`run`)

```bash
qre run --workspace <path> [options] "<prompt>"
```

#### Core Options:

| Option | Description | Default / Values |
|---|---|---|
| `-w, --workspace <path>` | Root directory for the run | Current directory |
| `--profile, --tools <name>` | Tool security profile | `none` (`none`, `readonly`, `verify`, `repair`) |
| `--api-url <url>` | Provider API endpoint | Env `QRE_API_URL` |
| `--api-key <key>` | Provider API key | Env `QRE_API_KEY` |
| `--model <name>` | Model identifier | Env `QRE_MODEL` |
| `--api-mode <mode>` | Provider protocol dialect | `chat-completions`, `responses`, `anthropic-messages` |
| `--response <text>` | Static response for offline deterministic testing | None |
| `--runner <name>` | Sandbox execution engine | `local` (trusted process) or `docker` (container) |
| `--docker-image <img>` | Docker image for `--runner docker` | Env `QRE_DOCKER_IMAGE` |
| `--external` | Load external tool manifests from `.qre/tools/*.json` | Disabled |
| `--tool-search` | Enable dynamic lazy tool retrieval | Disabled |
| `--tool-search-top-k <n>`| Maximum tools activated per search query | `5` |
| `--required-tool <name>` | Mandate calling a specific tool on the first step | None |
| `--approve-risk <reason>`| Explicit risk approval for write/repair tools | None (fail-closed if missing) |
| `--thinking <mode>` | Chain-of-thought coordination policy | `auto` (default), `off`, `on`, `preserve` |
| `--trace-data <mode>` | Audit trace data class | `public` (redacted), `sanitized`, `private` (resumable) |
| `--stream` | Stream text and reasoning tokens to console | Disabled |
| `--json` | Format CLI final output as JSON | Disabled |
| `--json-output` | Request JSON response format from model (auto-disables thinking) | Disabled |
| `--max-rounds <n>` | Maximum rounds allowed in the loop | `3` |
| `--sdk-diagnostics <m>` | Record client-side SDK outbound evidence | `off` (default), `metadata`, `structure` |

### 3. Tool Catalog & Invocation (`tool`)

Test, debug, and manage tools independently of the agent loop:

```bash
# List tools available in a profile with parameter schemas
qre tool list --workspace . --profile readonly --json

# List both built-in and external tools
qre tool list --workspace . --profile verify --external

# Register an external tool manifest
qre tool register --workspace . --manifest my_tool.json [--force]

# Invoke a tool in isolation for testing and debugging
qre tool invoke --workspace . --name qre_read_file --arguments '{"path":"README.md"}' --json
```

### 4. Policy & Security Pre-flight (`policy check`)

Verify whether a specific command or tool call complies with runtime policies before execution:

```bash
# Check if dotnet test is allowed under the verify profile
qre policy check --workspace . --profile verify --tool qre_dotnet_test -- dotnet test --no-restore

# Verify high-risk command with explicit approval
qre policy check --workspace . --profile repair --tool qre_patch --approve-risk "Refactor" -- git apply
```

### 5. Auditing, Replay & Rerun (`trace`, `replay`, `rerun`)

- **Trace Inspection**:
  ```bash
  # View latest run summary and events
  qre trace latest --workspace . --json
  qre trace latest --workspace . --jsonl
  ```
- **Deterministic Replay**:
  ```bash
  # Read-only summary (no execution)
  qre replay latest --workspace . --summary

  # Strict deterministic replay (validates trajectory, emits replay_digest)
  qre replay latest --workspace . --strict --json
  ```
- **Rerun**:
  ```bash
  # Re-execute the latest task with existing prompt and parameters
  qre rerun latest --workspace . --trace-data sanitized
  ```

### 6. Fault Recovery (`resume`)

If a run is terminated prematurely by process crash or timeout, QRE seamlessly resumes execution using its atomic checkpoint and attempt lease model:

```bash
# Resume latest interrupted run (validates workspace, tools, policy drift)
qre resume latest --workspace . --json
```

### 7. Workspace Diff (`diff`)

```bash
# View workspace modifications from the latest run (reads run-scoped diff.patch)
qre diff latest --workspace .

# Output patch statistics
qre diff latest --workspace . --stat --json
```

### 8. Controlled Sandbox Execution (`sandbox exec`)

Execute individual commands inside the policy and sandbox pipeline with timeouts and buffer protection:

```bash
# Run locally under verify profile
qre sandbox exec --workspace . --profile verify -- dotnet build

# Run inside a Docker container
qre sandbox exec --workspace . --runner docker --docker-image mcr.microsoft.com/dotnet/sdk:10.0 -- dotnet test
```

### 9. SDK Outbound Diagnostics Suite (`diagnose`)

Diagnose why a provider dropped parameters, failed schemas, or choked on reasoning tokens:

```bash
# 1. Run task with structural SDK outbound tracing
qre run --workspace . --sdk-diagnostics structure "audit dependencies"

# 2. Inspect outbound diagnostic evidence for the latest run
qre diagnose latest --workspace . --json

# 3. Compare two runs to locate where an option was dropped
qre diagnose compare <left_run_id> <right_run_id> --workspace .

# 4. Export sanitized diagnostic bundle (safe to share; no keys or code)
qre diagnose export latest --output issue_bundle.zip --workspace .

# 5. Extract request skeleton and rebuild offline
qre diagnose skeleton latest --output request_fixture.json
qre diagnose rebuild request_fixture.json --json
```

---

## Tool System & Ecosystem Extension

### Built-in Tools & Policy Profiles

QRE enforces a strict principle of least privilege through four profiles:

```
       [ none ]        Pure reasoning; no tools permitted
          │
      [ readonly ]     File exploration & search (qre_list_files, qre_read_file, qre_search_files)
          │
       [ verify ]      Read-only verification (+ git_status, git_diff, dotnet_build, dotnet_test)
          │
       [ repair ]      Modifications & patches (+ qre_apply_patch, external writers) ──► Requires --approve-risk
```

| Profile | Tools Included | Security Policy |
|---|---|---|
| `none` | None | Disables all tools; pure chat or structured JSON completion. |
| `readonly` | `qre_list_files`, `qre_read_file`, `qre_search_files` | Safe file traversal and regex search for exploration and analysis. |
| `verify` | All `readonly` tools, plus `qre_git_status`, `qre_git_diff`, `qre_dotnet_build`, `qre_dotnet_test` | Read-only workspace inspection, test runs, and compilation checks. |
| `repair` | All `verify` tools, plus file patching and modification tools | May modify workspace files. **Requires `--approve-risk "<reason>"` or fails closed**. |

### External Tool Manifests (Stdio & MCP)

Extend QRE with Python, Node.js, or compiled binaries by placing a JSON manifest in `.qre/tools/<name>.json`:

```json
{
  "name": "calc_coverage",
  "description": "Calculates code test coverage report for the repository",
  "executable": "python",
  "arguments": ["scripts/calc_coverage.py"],
  "parameters": {
    "type": "object",
    "properties": {
      "format": {
        "type": "string",
        "enum": ["summary", "detailed"],
        "description": "Report format"
      }
    },
    "required": ["format"]
  },
  "timeoutSeconds": 30
}
```

Enable external tools via `qre run --external ...`. Minimal MCP stdio tools are also supported. See [`examples/ExternalTools`](examples/ExternalTools), [`examples/PythonFunctionTools`](examples/PythonFunctionTools), and [`examples/NodeFunctionTools`](examples/NodeFunctionTools).

### Dynamic Tool Retrieval (Tool Search)

Providing dozens of tool schemas upfront bloats prompt tokens and increases model hallucinations.

With `--tool-search` (and `--tool-search-top-k 5`):
1. QRE exposes only a single `tool_search` meta-tool on startup.
2. The agent queries `tool_search(query="coverage")` when it identifies a need.
3. QRE dynamically activates the most relevant Top-K tools into the next step's context.
4. Token overhead is reduced by up to 80% on large catalogs.

---

## Skill System & Workflows

In production engineering, relying solely on atomic tools (e.g., reading files or running shell commands) can cause LLMs to lose strategic direction in multi-phase tasks. Conversely, cramming enterprise coding guidelines and domain workflows into system prompts quickly exhausts the context window. QRE natively supports the **Skill** system to provide agents with modular, on-demand **Procedural Knowledge**.

### 1. Conceptual Positioning: Atomic Tools vs. Domain Skills

| Dimension | Atomic Tool (Tool) | Domain Skill (Skill) |
|---|---|---|
| **Core Identity** | Deterministic single-step system I/O primitive | Standardized workflow & procedural knowledge bundle |
| **Granularity** | Fine-grained, single operation (`read_file`, `dotnet_test`) | End-to-end multi-step guidance (`code-reviewer`, `csharp-scaffolder`) |
| **Composition** | Executable function/process, input JSON Schema, risk profile | `SKILL.md` (metadata + instructions) + `scripts/` + `references/` |
| **Token Budget** | Full schemas loaded per turn or via tool search | **Two-Phase Loading**: Lightweight metadata discovery first; detailed body activated on demand |
| **Security Gate** | Intercepted by QRE profiles and Fail-Closed risk gates | Procedural rules guide the agent; packaged scripts execute in controlled sandboxes |

### 2. Standard Skill Specification & Directory Structure

Each skill is organized as a self-contained directory rooted by a standard `SKILL.md` file:

```text
my-custom-skill/
├── SKILL.md                 # [Required] YAML Frontmatter metadata + markdown instructions
├── scripts/                 # [Optional] Deterministic execution scripts (Python/Bash/Node.js)
├── references/              # [Optional] Domain schemas, API specs, or reference documents
└── assets/                  # [Optional] Boilerplates, code templates, or icons
```

#### `SKILL.md` Specification Example
```markdown
---
name: code-reviewer
description: Enforces architectural boundaries, zero-warning compilation, immutable protocols, and security standards. Use when reviewing PRs or new code additions.
---

# Code Reviewer Skill

## Review Principles & Workflow
1. Verify architectural layering (Protocol contracts must never depend on Engine/UI);
2. Ensure deterministic resource lifecycle management (all `IDisposable` managed cleanly);
3. Enforce compiler zero-warning standards under `<Nullable>enable</Nullable>`.
```

### 3. Two-Phase Progressive Loading & Discovery

To minimize context window consumption, QRE and `VllmChatClient` employ a **Two-Phase Progressive Loading** mechanism:

```
┌────────────────────────────────────────────────────────┐
│  Phase 1: Lightweight Metadata Discovery (Low Tokens)  │
│  Client scans skills directory, extracts frontmatter,   │
│  and injects a concise `# Skills` catalog into prompt  │
└───────────────────────────┬────────────────────────────┘
                            │
┌───────────────────────────▼────────────────────────────┐
│  Phase 2: On-Demand Workflow Activation                │
│  Agent matches task intent -> calls `ReadSkillFile`     │
│  Loads full skill markdown only when actively needed   │
└────────────────────────────────────────────────────────┘
```

#### Multi-Tier Skill Directory Discovery
QRE automatically discovers skills across multiple priority tiers:
1. Explicitly configured `SkillDirectoryPath`;
2. Environment variable `CODEX_SKILLS_DIR`;
3. Application root `./skills`;
4. Current workspace `./skills` or `.qre/skills`;
5. Recursive parent directory scan.

### 4. Configuration & Usage in QRE

#### Enabling Skills in C# Host Applications
Configure `VllmChatOptions` with `EnableSkills = true` and the directory path. The underlying client automatically handles metadata discovery and built-in tool registration:

```csharp
using CodexFlow.QueryRuntime.Models;
using Microsoft.Extensions.AI;

var chatOptions = new VllmChatOptions
{
    EnableSkills = true,
    SkillDirectoryPath = Path.Combine(AppContext.BaseDirectory, "skills"),
    ThinkingEnabled = true
};

// VllmChatClient automatically advertises available skills in the system prompt
// and registers two built-in discovery tools:
// 1. ListSkillFiles: Lists available skills with descriptions and file paths
// 2. ReadSkillFile: Reads the full markdown instructions of a targeted skill
```

#### CLI Task Orchestration
Direct the agent to follow a specific skill from the command line:
```bash
qre run --workspace . --profile verify "Follow code-reviewer skill to audit recent architectural changes"
```

### 5. Ecosystem Skills & Typical Scenarios

| Skill Category | Examples | Value Delivered |
|---|---|---|
| **Scaffolding** | `csharp-scaffolder`, `node-scaffolder`, `python-scaffolder` | Standardized boilerplate generation following domain-driven design principles. |
| **Security & Quality** | `nodejs-security`, `python-security`, `java-security` | Standardized audit procedures wrapping Bandit, ESLint Security, or SpotBugs. |
| **Meta Skills** | `skill-creator` | Guides developers in authoring, validating, and packaging new skills. |
| **Package Governance**| `nuget-package-versions` | Verifies real upstream package versions before editing project files to avoid restore deadlocks. |

> For a complete runnable example, see [`examples/SkillsWorkflow`](examples/SkillsWorkflow).

---

## Sandboxing & Security Model

QRE follows a **Fail-Closed** security architecture, treating model output, tool arguments, external manifests, and workspace files as untrusted:

1. **`LocalProcessSandboxRunner`**:
   - Designed for trusted developer workflows.
   - Enforces timeouts, buffer truncation, and process group lifecycle management.
   - *Note: Local process execution is not an isolated security boundary.*
2. **`DockerSandboxRunner`**:
   - Hard isolation for untrusted commands and CI runners.
   - Read-only workspace volume mounts by default.
   - Selective write-back for approved artifact persistence.
   - Network allowlisting support.
   - Configurable image via `--docker-image <image_name>`.
3. **Trace Storage Classes**:
   - `public`: Redacted, shareable, no secrets, no checkpoints (not resumable).
   - `sanitized`: Preserved payloads for reviewed synthetic test fixtures.
   - `private`: Full diagnostic and recovery data with checkpoints.

---

## Fault Recovery & Deterministic Replay

### H1 Crash Recovery
For long-running tasks, QRE's V2 engine guarantees seamless fault recovery:
- **Atomic Checkpoints**: Checkpoints written to `.qre/v2/checkpoints/` after every state step.
- **Attempt Leases**: Prevents concurrent execution or double-recovery conflicts.
- **Drift Prevention**: Validates workspace identity, tool registry hashes, policy snapshots, and model configurations before resuming.
- See the [H1 Crash-Resume Implementation Report](docs/h1-crash-resume-implementation-report.zh-CN.md).

### Strict Replay
- Evaluates complete recorded trajectories completely offline.
- Injects deterministic clocks and query IDs.
- Emits a stable `replay_digest` for verifiable regressions and benchmarks.

---

## Troubleshooting & Logs

Start with the failure time in your application logs, list QRE runs overlapping that interval, then select the corresponding audit for replay. Set `--workspace` to the production workspace that actually stores `.qre`; replace `/app/data` below with that path.

### Find Runs Around a Failure

```sh
# List all matching runs in ascending start-time order (14:00–14:30 at UTC+08:00)
qre logs list --workspace /app/data --from 2026-09-27T14:00:00+08:00 --to 2026-09-27T14:30:00+08:00

# Return the same interval as JSON for scripted analysis
qre logs list --workspace /app/data --from 2026-09-27T14:00:00+08:00 --to 2026-09-27T14:30:00+08:00 --json

# Page through audits in descending start-time order
qre logs list --workspace /app/data --kind audit --descending --skip 0 --take 100

# Filter by UTC creation date
qre logs list --workspace /app/data --date 2026-09-27
```

The range includes its start and excludes its end. Timestamps require seconds and a timezone; output uses UTC. Matching uses **overlapping run intervals**, so a task that started before the failure window can still appear. Each row represents a whole run, not an individual event. Active records are treated as unfinished, including stale active records left by a crashed process. Either `--from` or `--to` may be used alone; neither can be combined with `--date` or `--before`.

Rows include start time, last update time, kind, run ID, status, directory size and absolute path. `--kind` accepts `all` (default), `audit`, `private` or `diagnostics`, covering v2 audits, private audits and SDK diagnostics. Host application logs, v1 traces and externally exported bundles are outside this scope. Pagination limits output only; queries still scan directory metadata.

### Replay a Selected Historical Audit

Append `audit.v1.jsonl` to an audit directory returned by the list command and substitute that path below:

```sh
# Inspect the selected historical audit summary
qre replay latest --workspace /app/data --audit-file /app/data/.qre/v2/runs/RUN_ID/audit.v1.jsonl --summary --json

# Strictly validate an audit that supports recorded replay
qre replay latest --workspace /app/data --audit-file /app/data/.qre/v2/runs/RUN_ID/audit.v1.jsonl --strict --json
```

`--audit-file` overrides automatic `latest` selection. The file must remain inside the specified workspace and cannot use linked paths. Public redacted audits support summaries only; strict replay requires recorded replay data. Recorded replay does not contact a model or execute real tools. Inspect the selected JSONL for individual failure events. SDK diagnostic directories must be analyzed using the commands in `qre diagnose --help`, rather than passed to audit replay.

### Clean Up Logs by Date

```sh
# Preview cleanup for one UTC creation date
qre logs delete --workspace /app/data --date 2026-09-20

# Preview runs created strictly before midnight UTC on this date
qre logs delete --workspace /app/data --before 2026-09-20

# Apply cleanup after checking the preview
qre logs delete --workspace /app/data --before 2026-09-20 --execute --json
```

Deletion requires `--date` or `--before` and only previews matches unless `--execute` is supplied. It removes entire run directories, including checkpoints, replay blobs, artifacts and patches; those records can no longer be replayed or resumed. Active, unknown, corrupt or linked records and `incomplete` SDK diagnostics are preserved. Open writers cause deletion to fail.

Unreadable records appear in JSON `warnings`; deletion failures are reported per run. Invalid arguments, unreadable records or deletion failures return exit code 1. Filesystem deletion is not transactional: permission changes or concurrent external modifications can cause partial deletion. See the [log management guide (Chinese)](docs/log-management.zh-CN.md) for details and `qre logs --help` for all options.

---

## Model Providers & Thinking Policies

### Supported Providers
QRE builds upon `Microsoft.Extensions.AI` and official `CodexFlow.QueryRuntime.Models` package, providing first-party adapters for:
- **OpenAI-compatible** APIs (OpenAI, Azure OpenAI, DeepSeek, Moonshot, Zhipu, etc.)
- **vLLM** inference clusters
- **Anthropic Messages** endpoints

### Thinking Policies (`--thinking`)
Modern models with Chain-of-Thought (Reasoning) can fail or corrupt schemas when tools or JSON formats are activated. QRE manages this automatically:
- **`auto` (Recommended)**: Automatically turns thinking off when tools or `--json-output` are active to prevent protocol conflicts; preserves thinking for pure text prompts.
- **`off`**: Forcibly disables reasoning parameters.
- **`on`**: Forcibly enables reasoning (only when the provider supports concurrent tools and CoT).
- **`preserve`**: Retains whatever defaults the model client options specify.

### Structured Outputs via JSON Schema

For production tasks requiring deterministic output shapes (such as architectural reports, security vulnerability manifests, or work item breakdowns), QRE natively supports structured outputs via strict JSON Schema.

> [!NOTE]
> **Underlying Driver**: **`VllmChatClient`** serializes schema requests. Grammar-constrained decoding, when supported, is implemented by the remote backend and model.
> **QRE Orchestration**: QRE manages loop state machines, `QreThinkingPolicy` reasoning coordination, and full-link Outbound Diagnostics.

#### 1. Strict Mode vs. Soft Prompting
- **`ChatResponseFormat.Json` (`json_object`)**: The model is prompted to emit valid JSON, but without schema enforcement; missing fields or mismatched data types can occur.
- **`ChatResponseFormat.ForJsonSchema(...)` (`json_schema`)**: Requests schema-constrained output. Backend support varies; clients must validate responses. HTTP 200 does not prove schema enforcement. See the [live validation results](examples/live-api-validation.md).

**Alibaba Cloud Model Studio configuration and live validation**

Use a model explicitly listed as supporting **JSON Schema**, rather than only JSON Object. As checked on 2026-09-27, the [official support list](https://help.aliyun.com/zh/model-studio/qwen-structured-output) includes Qwen3.7-Plus, Qwen3.8-Flash, Qwen3.7-Flash, Qwen3.7-Max, and Qwen3.8-Max. `qwen3.8-27b` is not listed for JSON Schema; disabling thinking alone does not add schema support.

The verified combination is endpoint `https://dashscope.aliyuncs.com/compatible-mode/v1`, model ID `qwen3.8-flash`, and `ThinkingEnabled = false` (serialized as top-level `enable_thinking: false`). On 2026-09-27, **5 consecutive real API calls passed**: HTTP 200, complete schema with `strict=true` in the outbound body, valid response fields/types/enums, and successful POCO deserialization. Raw SSE text matched the final QRE output without repair or mock fallback. This validates these five calls, not every possible input.

```powershell
$env:QRE_API_URL = 'https://dashscope.aliyuncs.com/compatible-mode/v1'
$env:QRE_MODEL = 'qwen3.8-flash'
$env:QRE_API_KEY = $env:VLLM_ALIYUN_API_KEY # Or use your own Model Studio API key.
dotnet run --project examples/StructuredOutputsJsonSchema -- --live --evidence-dir artifacts/live-examples/schema
```

Running without `--live` or `--endpoint` performs an offline self-test only. See the [validation record](examples/live-api-validation.md) for request/response evidence and earlier unsupported-model results.

#### 2. .NET Host Application Example
```csharp
using System.Text.Json;
using CodexFlow.QueryRuntime.Models;
using Microsoft.Extensions.AI;

// 1. Define Strict JSON Schema
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
    schemaDescription: "Enforces structural compliance for automated code review results.");

// 2. Configure Model Options
var options = new VllmChatOptions
{
    ResponseFormat = jsonSchemaFormat,
    Temperature = 0.1f,
    ThinkingEnabled = false // Verified Model Studio configuration: enable_thinking=false.
};
```

Model Studio recommends omitting `max_tokens` for structured output to avoid truncating JSON. The live comparison above retained the example's 1024-token limit; all five responses finished with `stop`.

#### 3. QRE Engineering Guarantees
- **Thinking Policy Coordination (`QreThinkingPolicy`)**: Intelligently balances Reasoning (CoT) and schema constraints, preventing premature syntax failures during internal reasoning.
- **Full-Link Outbound Diagnostics (`qre diagnose`)**: Three-tier probes track `response_format` from C# intent through to the HTTP Request Body, catching silent `response_format_dropped` degradations by intermediate proxies.

> For a complete runnable example with strong-typed C# POCO deserialization, see [`examples/StructuredOutputsJsonSchema`](examples/StructuredOutputsJsonSchema).

---

## Embedding in .NET Applications

Embed the QRE V2 engine and model providers directly into any C# / .NET 10 host application:

### 1. Package References
Add the official NuGet packages (`0.23.2`):

```xml
<ItemGroup>
  <PackageReference Include="CodexFlow.QueryRuntime.Engine" Version="0.23.2" />
  <PackageReference Include="CodexFlow.QueryRuntime.Models" Version="0.23.2" />
</ItemGroup>
```

*(Note: `CodexFlow.QueryRuntime.Protocol` is packaged internally as a dependency of the Engine package).*

### 2. Runtime Execution Example

```csharp
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;

// 1. Configure model client (offline static or online MEAI client)
var modelClient = new StaticRuntimeModelClient("Hello! I am an embedded QRE agent.");

// 2. Initialize V2 Agent Runtime
IAgentRuntime runtime = new AgentRuntime(modelClient);

// 3. Assemble loop request
var sessionId = new RuntimeSessionId(Guid.NewGuid().ToString("N"));
var turnId = new RuntimeTurnId(Guid.NewGuid().ToString("N"));
string objective = "Analyze repository structure";

var request = new RuntimeAgentLoopRequest(
    sessionId,
    turnId,
    objective,
    [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(objective)])],
    [],
    ModelParameters: new RuntimeModelParameters(),
    Policy: new RuntimePolicySnapshot("prod-policy", "readonly"),
    Environment: new RuntimeEnvironmentSnapshot("local", Path.GetFullPath("."), "my-host-app"),
    Budget: new RuntimeBudgetSnapshot(maxSteps: 5, maxToolCalls: 10)
);

// 4. Implement presentation event listener for streaming UI updates
var eventSink = new DelegateEventSink(runtimeEvent =>
{
    if (runtimeEvent.Type == RuntimePresentationEventType.TextDelta)
    {
        Console.Write(runtimeEvent.Text);
    }
    else if (runtimeEvent.Type == RuntimePresentationEventType.ToolCallRequested)
    {
        Console.WriteLine($"\n[Tool]: {runtimeEvent.ToolName}");
    }
    return ValueTask.CompletedTask;
});

// 5. Execute turn
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var result = await runtime.RunAsync(new RuntimeRunRequest(request), eventSink, cts.Token);

Console.WriteLine($"\nStatus: {result.Status}, Steps: {result.Turn.Steps.Count}");
```

See [`examples/EmbeddedV2`](examples/EmbeddedV2) for a complete working host.

---

## Runnable Examples Showcase

The [`examples/`](examples/README.md) directory contains 10 fully working integration examples:

| Example | Integration Highlights | Source |
|---|---|---|
| **EmbeddedV2** | Minimal .NET `IAgentRuntime` embedding with deterministic offline client. | [Explore](examples/EmbeddedV2) |
| **RepoDoctor** | Production .NET CLI host with custom tools, streaming, and strict replay. | [Explore](examples/RepoDoctor) |
| **PythonToolDoctor** | Python CLI host invoking built-in QRE tools with strict trace replay. | [Explore](examples/PythonToolDoctor) |
| **ExternalTools** | Minimal stdio tool manifest and approved model execution. | [Explore](examples/ExternalTools) |
| **PythonFunctionTools** | Python function tools with automatic manifest generation. | [Explore](examples/PythonFunctionTools) |
| **NodeFunctionTools** | Node.js ESM function tools with automatic manifest generation. | [Explore](examples/NodeFunctionTools) |
| **H1CrashResume** | Simulates process abort and tests checkpoint recovery via `qre resume`. | [Explore](examples/H1CrashResume) |
| **SdkOutboundDiagnostics**| Defective host simulation, parameter loss troubleshooting, and offline request rebuilds. | [Explore](examples/SdkOutboundDiagnostics) |
| **SkillsWorkflow** | Skill directory packaging (`SKILL.md`), progressive two-phase metadata injection, and `ReadSkillFile` tool dispatch. | [Explore](examples/SkillsWorkflow) |
| **StructuredOutputsJsonSchema** | Strict JSON Schema structured outputs via `VllmChatClient` guided decoding, with strong-typed C# deserialization. | [Explore](examples/StructuredOutputsJsonSchema) |

### Run All Regression Examples
```bash
python scripts/test-examples.py
```

---

## Building, Testing & Native AOT Publish

### Build Solution and Run Tests
```bash
dotnet build CodexFlow.QueryRuntime.slnx
dotnet test CodexFlow.QueryRuntime.UnitTests/CodexFlow.QueryRuntime.UnitTests.csproj
dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj
```

### Publish Self-Contained Native AOT Binary
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

## Technical Documentation Index

- [Production Log Management (Chinese)](docs/log-management.zh-CN.md)
- 📘 [Technical Guide](docs/queryruntime-technical-guide.md) ([中文](docs/queryruntime-technical-guide.zh-CN.md))
- 🧭 [0.2 Preview Migration Guide](docs/migration-0.2-preview.md) ([中文](docs/migration-0.2-preview.zh-CN.md))
- 🛡️ [Security Policy (SECURITY.md)](SECURITY.md)
- 🔒 [Threat Model](docs/threat-model.md)
- 🧰 [Tool Capabilities](docs/tool-capabilities.md)
- 🔍 [Tool Search Architecture](docs/toolsearch.md) & [Partition Matrix](docs/queryruntime-tool-partition-matrix.md)
- 🔄 [H1 Crash-Resume Implementation Report](docs/h1-crash-resume-implementation-report.zh-CN.md) & [Threat Model](docs/h1-crash-resume-threat-model.md)
- 🩺 [SDK Outbound Diagnostics Guide](docs/sdk-outbound-diagnostics.md) ([中文](docs/sdk-outbound-diagnostics.zh-CN.md)) & [Acceptance Report](docs/sdk-outbound-diagnostics-acceptance-report.zh-CN.md)
- 📦 [Package Source & Provenance](docs/package-source-provenance.md)
- 🏛️ [Architectural Decision Records (ADR)](docs/adr/):
  - [ADR-001: Local Single-Process Runtime](docs/adr/ADR-001-local-single-process-runtime.md)
  - [ADR-002: Session-Turn-Step Lifecycle](docs/adr/ADR-002-session-turn-step-lifecycle.md)
  - [ADR-003: Runtime IR and Model Adapters](docs/adr/ADR-003-runtime-ir-and-model-adapters.md)
  - [ADR-004: State Events and Data Layers](docs/adr/ADR-004-state-events-and-data-layers.md)
  - [ADR-005: Tool Execution Pipeline](docs/adr/ADR-005-tool-execution-pipeline.md)
  - [ADR-007: V2-Only Cutover](docs/adr/ADR-007-v2-only-cutover.md)
  - [ADR-008: Local Crash Resume](docs/adr/ADR-008-local-crash-resume.md)
  - [ADR-009: SDK Outbound Diagnostics](docs/adr/ADR-009-sdk-outbound-diagnostics.md)

---

## License

This project is licensed under the [MIT License](LICENSE.txt).
