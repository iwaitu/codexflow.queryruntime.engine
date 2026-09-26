# CodexFlow QueryRuntime

**English** | [简体中文](README.zh-CN.md)

[![CI](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/ci.yml/badge.svg)](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/ci.yml)
[![Release](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/release.yml/badge.svg)](https://github.com/iwaitu/codexflow.queryruntime.engine/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.txt)

CodexFlow QueryRuntime is a cross-platform .NET runtime for model loops, tool execution, policy enforcement, audit/replay, checkpoint recovery, and sandboxed automation. It can be embedded in a host application or shipped as the standalone `qre` CLI without requiring the CodexFlow web platform.

This repository is on the **0.2 preview, v2-only** line. New integrations should use `CodexFlow.QueryRuntime.Protocol` and `CodexFlow.QueryRuntime.Engine.V2`. The earlier v1 API remains only as source-migration and historical-trace context; it is not a selectable CLI or CodexFlow backend.

## What it provides

- A typed agent loop through `IAgentRuntime`, with model, tool, continuation, budget, and termination handling.
- Stable protocol types for messages, sessions, turns, tool calls, results, usage, policy, and errors.
- A frozen tool registry with authorization, approval, sandbox, output validation, and fail-closed policy stages.
- Deterministic context preparation and compaction for longer runs.
- Durable audit events, strict replay, and public/sanitized/private trace modes.
- H1 local crash recovery through `IResumableAgentRuntime`, attempt leases, checkpoints, and compatibility validation.
- Built-in file, search, patch, process, and repository tools, plus external Python, Node.js, and MCP stdio tools.
- Local-process and Docker sandbox adapters.
- Cross-platform CLI and Native AOT release builds.

## Current boundaries

- H1 recovery is a single-host, local-filesystem design. Distributed takeover and remote checkpoint stores belong to later H2/H3 work.
- Public redacted traces are intentionally not resumable. Resume requires a sanitized or private checkpoint with matching workspace, policy, tool catalog, model, and recovery compatibility identity.
- `LocalProcessSandboxRunner` is for trusted local development; it is not a hard security boundary. Use the Docker adapter or another isolated runner for untrusted commands.
- Network allowlisting is enforced only by runners that explicitly support it.
- MCP stdio support currently covers one-shot tool calls rather than the complete MCP lifecycle.
- Usage estimates are operational metrics, not provider billing records.

## Repository layout

| Project | Responsibility |
|---|---|
| `CodexFlow.QueryRuntime.Protocol` | Stable v2 data contracts and runtime state |
| `CodexFlow.QueryRuntime.Engine` | `IAgentRuntime`, the v2 loop, tool pipeline, context, audit, replay, and recovery |
| `CodexFlow.QueryRuntime.Models` | Model-provider adapters |
| `CodexFlow.QueryRuntime.Abstractions` | Compatibility abstractions retained for migration |
| `CodexFlow.QueryRuntime.Experimental` | Host composition, built-in tools, and external-tool adapters |
| `CodexFlow.QueryRuntime.Cli` | The `qre` command-line host |
| `CodexFlow.QueryRuntime.Sandbox.LocalProcess` | Trusted local process runner |
| `CodexFlow.QueryRuntime.Sandbox.Docker` | Docker isolation adapter |
| `CodexFlow.QueryRuntime.UnitTests` | Deterministic unit and contract tests |
| `CodexFlow.QueryRuntime.IntegrationTests` | CLI, provider, sandbox, and end-to-end tests |

## Build and test

Requirements: .NET 10 SDK. Docker, Python, and Node.js are optional and needed only for their corresponding adapters and integration tests.

```bash
dotnet build CodexFlow.QueryRuntime.slnx
dotnet test CodexFlow.QueryRuntime.UnitTests/CodexFlow.QueryRuntime.UnitTests.csproj
dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj
```

Publish a self-contained Native AOT CLI by substituting a supported runtime identifier such as `win-x64`, `linux-x64`, or `osx-arm64`:

```bash
dotnet publish CodexFlow.QueryRuntime.Cli/CodexFlow.QueryRuntime.Cli.csproj \
  -c Release -r <RID> -p:PublishAot=true -p:SelfContained=true
```

## CLI quickstart

Commands can be run from a published `qre` binary or through `dotnet run --project CodexFlow.QueryRuntime.Cli --`.

```bash
qre --version
qre init --workspace . --json
qre doctor --workspace . --json

# Deterministic offline run: no provider credentials required
qre run --workspace . --trace-data sanitized --response "offline smoke" --json "analyze this repository"

# Inspect tools and run a read-only task
qre tool list --workspace . --profile readonly --json
qre run --workspace . --profile readonly --trace-data sanitized --response "offline readonly" "summarize the project structure"

# Inspect and replay the latest v2 run
qre trace latest --workspace . --json
qre replay latest --workspace . --strict --json
```

For a real OpenAI-compatible or vLLM-compatible provider:

```bash
qre run --workspace . \
  --api-url http://localhost:8000/v1 \
  --api-key <key> \
  --model <model> \
  --api-mode chat-completions \
  "inspect this repository and report the main risks"
```

The same values can be supplied through `QRE_API_URL`, `QRE_API_KEY`, `QRE_MODEL`, and `QRE_API_MODE`. Use `--response` for deterministic offline tests. `--json-output` asks the model for JSON; `--json` formats the CLI result itself as JSON.

Thinking defaults to `auto`: it is disabled when tools or model JSON output are active for broader provider compatibility. Use `--thinking on` or `--thinking preserve` only when the selected provider supports that combination.

## Trace data and recovery

New v2 runs write under `.qre/v2/` inside the workspace:

- `public`: redacted, shareable audit data; not resumable.
- `sanitized`: operational detail with sensitive values removed according to policy.
- `private`: local recovery data, including resumable checkpoints when checkpointing is enabled.

Resume an unfinished run with the same workspace and compatible runtime configuration:

```bash
qre resume latest --workspace . --json
```

The runtime refuses a resume when ownership, lease, checkpoint integrity, workspace identity, policy, tool catalog, model, or recovery compatibility checks do not match. See the [H1 crash-resume report](docs/h1-crash-resume-implementation-report.zh-CN.md) and [threat model](docs/h1-crash-resume-threat-model.md) for the precise guarantees.

## Embed in .NET

The current preview package is `CodexFlow.QueryRuntime.Engine` `0.2.0-preview.21`. Applications should depend on the v2 surface:

- `CodexFlow.QueryRuntime.Engine.V2.IAgentRuntime` for new turns.
- `CodexFlow.QueryRuntime.Engine.V2.IResumableAgentRuntime` when local checkpoint recovery is required.
- `CodexFlow.QueryRuntime.Protocol` for requests, state, events, tools, policy, audit, and checkpoint contracts.

The `Experimental` project contains optional composition helpers and tool adapters; it is not an alternative runtime loop. Follow the [0.2 preview migration guide](docs/migration-0.2-preview.md) before moving an existing v1 host.

## Security

Treat model output, tool arguments, external tool manifests, replay data, and workspace files as untrusted input. Select the narrowest tool profile, keep writes approval-gated, avoid placing secrets in public traces, and use an isolated runner for untrusted commands. See [SECURITY.md](SECURITY.md), the [runtime threat model](docs/threat-model.md), and [tool capabilities](docs/tool-capabilities.md).

## Documentation

- [Runnable v2 integration examples](examples/README.md)
- [Technical guide](docs/queryruntime-technical-guide.md) ([中文](docs/queryruntime-technical-guide.zh-CN.md))
- [0.2 preview migration guide](docs/migration-0.2-preview.md) ([中文](docs/migration-0.2-preview.zh-CN.md))
- [H1 crash-resume implementation report](docs/h1-crash-resume-implementation-report.zh-CN.md)
- [Tool search](docs/toolsearch.md) and [tool partition matrix](docs/queryruntime-tool-partition-matrix.md)
- [Package source and provenance](docs/package-source-provenance.md)

Historical roadmaps and completed implementation plans are kept under `docs/archive/`; they are not descriptions of the current runtime.

## License

[MIT](LICENSE.txt)
