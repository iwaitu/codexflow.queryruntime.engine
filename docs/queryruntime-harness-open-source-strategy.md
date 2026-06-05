# CodexFlow QueryRuntime Harness Open Source Strategy

Companion document: `docs/queryruntime-technical-guide.md` explains the
developer-facing technical model, usage scenarios, CLI usage, cross-platform
integration example, and project potential. This strategy document remains the
source of truth for extraction phases and open-source release sequencing.

Tool partition companion: `docs/queryruntime-tool-partition-matrix.md` tracks
which tools are harness-core, optional tool packs, platform-only, or dropped
from the public harness surface.

## Positioning

CodexFlow should be repositioned from a full AI coding SaaS platform into an
open-source agent runtime harness:

> CodexFlow QueryRuntime is an open-source harness for building coding agents
> with explicit capability and sandbox surfaces.

The open-source entry point should be a small, composable runtime rather than
the current all-in-one web platform. The first public release should focus on
agent loops, CLI workflows, tool execution policy, trace/replay, and sandbox
abstractions. The Web UI, identity system, notification center, semantic memory
stack, and multi-agent committee system should move behind optional packages or
remain in the full platform.

This positioning is stronger for open source because developers can adopt the
runtime as infrastructure inside their own agents without buying into a hosted
SaaS architecture.

## Differentiation

The project should not compete head-on with "AI coding apps" as its first
message. It should compete as infrastructure.

Potential differentiators:

- A .NET-native agent runtime for coding workflows.
- A capability-first execution model for CLI tools.
- Replay and checkpointing as first-class runtime concepts.
- Sandboxed command execution as a default product surface, not an afterthought.
- Native AOT-ready CLI and workers once the core is made trim-compatible.
- Clean integration with `Microsoft.Extensions.AI` and the .NET ecosystem.

Competitors and adjacent projects to study explicitly before launch:

- Aider
- OpenHands / OpenDevin
- Cline
- Continue
- Goose
- smolagents
- Claude Code CLI
- Codex CLI-style local agents

The README should answer: "Why would I use this instead of a full coding agent
app?" The answer should be: because CodexFlow QueryRuntime is the harness for
building those apps.

## Problem Opportunity

There is a gap between simple agent demos and production SaaS coding platforms.
Developers need a middle-layer harness that provides:

- Provider-agnostic LLM tool loops.
- Sandboxed CLI execution against real repositories.
- Capability-based command and tool policies.
- Checkpointing, tracing, and deterministic replay.
- File editing, diff capture, and artifact collection.
- Embeddable runtime APIs plus a CLI.
- Optional server/dashboard integration.

CodexFlow already contains many of these primitives, but they are embedded
inside a broad ASP.NET platform with private service assumptions, leaked local
configuration, and a heavy dependency footprint. The open-source strategy is to
extract and sharpen the runtime primitives while being explicit about the gap
between current code and target product.

## Current State vs Target State

The strategy document must distinguish what exists today from what should exist
in the extracted harness.

| Area | Current state | Target state |
|---|---|---|
| Runtime engine | `CodexFlow.Core/Runtime/QueryRuntimeEngine.cs` exists, but the request model is coupled to sessions, workers, memory injection, intervention hooks, and internal recovery hints. | A small public facade such as `IQueryRuntimeEngine.RunAsync(...)` with optional adapters for sessions, memory, workers, and hosted orchestration. |
| CLI | `CodexFlow.QueryRuntime.Cli` now provides a real `qre` CLI, including run, trace, replay, tool list, policy check, diff, and sandbox exec commands. Native AOT local publish has been verified on `osx-arm64`; standalone release packaging / .NET tool packaging is still pending. | `qre run`, `qre trace`, `qre replay`, `qre tool list`, and `qre sandbox exec` as a packaged CLI. |
| Sandbox | `LocalProcessSandboxRunner` exists for trusted development, and Docker runner MVP / hardening work now covers isolated workspace staging, network deny, non-root execution, capability drop, read-only root filesystem, timeout cleanup, and output limits. Kubernetes/VM runners remain future work. | `LocalProcessSandboxRunner` for trusted development, `DockerSandboxRunner` as the first security-credible runner, and optional stronger runners later. |
| Capabilities | `RunCommandTool` uses a broad command allowlist. Some network and side-effect commands are present in the same surface as read/test commands. | Tool capability contracts with path, network, process, credential, destructive-action, and budget controls. |
| Serialization | Core runtime uses `Newtonsoft.Json` in important runtime paths. | `System.Text.Json` source-generated contexts on all public IO boundaries. |
| Native AOT | Local `osx-arm64` Native AOT publish and native `qre` smoke have been verified, including OpenAI-compatible real-provider smoke. | AOT publish CI for the CLI/worker with zero trimming warnings before marketing AOT as a shipped feature. |
| Replay | JSONL trace, run manifests, content-addressed blobs, and recorded replay are implemented for latest-run replay. Deterministic IDs, clock injection, and trace schema migration remain hardening work. | JSONL trace plus content-addressed blobs. Replay consumes recorded model responses and tool outputs without calling the provider. |
| Branding | Core public surfaces are being moved into the `CodexFlow.*` namespace and package family, while some legacy tool prefixes and domain entity names still remain. | Public packages, namespaces, examples, and CLI should use `CodexFlow.*` consistently, with compatibility aliases only where needed. |
| Repo hygiene | Runtime artifacts, logs, private endpoints, and secrets have been observed in the repository. | Sanitized extraction repository with clean history, secret scan, license scan, and generated artifacts ignored. |

## P0 Baseline Acceptance Matrix

This matrix freezes the current branch baseline before the next feature phase.
It is a current-capability record, not a promise that every target capability is
complete. Later P1/P3/P5 work should start only after the baseline gate below is
green.

| Slice | Current status | Proof commands / tests | Limitations to keep explicit |
|---|---|---|---|
| Phase -1 mechanical rename and hygiene baseline | Completed for the current harness slice. `CodexFlow.QueryRuntime.slnx` isolates the first QueryRuntime verification surface. | `dotnet test CodexFlow.QueryRuntime.slnx --no-restore`; legacy-brand `rg` check from the Phase -1 section. | Some non-archived platform docs and EF domain names still carry legacy names as tracked migration debt. |
| Phase 0 feasibility spike | Completed for the current branch. `CodexFlow.QueryRuntime.Experimental` wraps the runtime behind a small harness facade and writes `.qre/runs/<run-id>/events.jsonl`. | `dotnet test CodexFlow.QueryRuntime.UnitTests/CodexFlow.QueryRuntime.UnitTests.csproj --filter "FullyQualifiedName~ExperimentalQueryRuntimeHarnessTests"`; offline `qre run --workspace . --response "offline smoke" --json "analyze this repo"`. | The facade is still experimental and not the final public NuGet surface. |
| Phase 1 contracts and CLI | Completed for the current branch. `qre` supports run, trace, replay, rerun, diff, tool list, policy check, doctor, init, and sandbox exec. | `dotnet test CodexFlow.QueryRuntime.UnitTests/CodexFlow.QueryRuntime.UnitTests.csproj --filter "FullyQualifiedName~QreCliSmokeTests"`; workflow `.github/workflows/queryruntime-harness.yml` CLI smoke. | Packaged release / .NET tool distribution is still planned. |
| Phase 1.5 Native AOT readiness | Completed as a local `osx-arm64` proof, not a CI guarantee. | `dotnet publish CodexFlow.QueryRuntime.Cli -c Release -r osx-arm64 -p:PublishAot=true -p:SelfContained=true`; published `qre --version`; native `qre run --response ... --json`; native `qre replay latest --json`. | AOT is not yet a blocking CI gate and has not been proven across the full RID matrix. |
| Phase 2a capability policy migration | Completed for the current verify/sandbox command surface. Policy decisions are emitted before process execution. | `dotnet test CodexFlow.QueryRuntime.UnitTests/CodexFlow.QueryRuntime.UnitTests.csproj --filter "FullyQualifiedName~CommandPolicyMigrationTests"`; `qre policy check --workspace . --profile verify --tool qre_dotnet_test --json -- dotnet test --no-restore`; `qre sandbox exec --workspace . --profile verify --json -- git status --short`. | Policy is still an application contract; trusted-local execution is not OS isolation. |
| Phase 2b-MVP Docker sandbox | Completed for the first Docker runner slice. | `RUN_QUERY_RUNTIME_DOCKER_TESTS=true dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj --filter "FullyQualifiedName~DockerSandboxRunnerIntegrationTests"`; `qre sandbox exec --workspace . --profile readonly --runner docker --docker-image alpine:3.20 --json -- grep -R "Phase 2b-MVP" docs/queryruntime-harness-open-source-strategy.md`. | Requires Docker daemon and image availability; Kubernetes / VM runners remain planned. |
| Phase 2b-Hardening Docker sandbox | Completed for current hardening claims: staged workspace copy, non-root user, network deny, read-only rootfs, capability drop, timeout cleanup, output limits, and host-secret path checks. | Same Docker integration test gate plus unit coverage in `DockerSandboxRunnerTests`. | This is still scoped harness hardening, not a complete multi-tenant security guarantee. |
| Phase 3 trace/replay first slice | Completed as recorded replay, not deterministic replay hardening. | `qre run --workspace . --response "offline smoke" --json "analyze this repo"`; `qre trace latest --workspace . --jsonl`; `qre replay latest --workspace . --summary --json`; `qre replay latest --workspace . --json`. | Deterministic IDs, clock injection, public trace schema migration, and byte-identical strict replay remain planned. |
| Live provider checkpoints | Locally/gated verified checkpoints only. | `RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj --filter "FullyQualifiedName~ExperimentalHarnessRealLlmPhaseTests" --logger "console;verbosity=detailed"` with `QRE_API_URL`, `QRE_API_KEY`, `QRE_MODEL`, and `QRE_API_MODE` or equivalent appsettings. | These tests require external credentials/endpoints and are not automated CI checks. |
| CLI streaming contract | Human-readable `qre run --stream` is implemented for assistant text deltas. Normal `qre run` still prints final assistant text after completion, and `--json` still prints one final result JSON object. | `QreCliSmokeTests.Run_JsonOutputIsSingleFinalResultObject`; `QreCliSmokeTests.Run_Stream_PrintsAssistantTextAndMetadata`; `QreCliSmokeTests.Run_StreamCannotBeCombinedWithFinalJson`. | `--jsonl-stream` remains reserved until machine-readable event streaming ships. |

Baseline gate before new feature phases:

```bash
scripts/queryruntime-baseline-gate.sh
```

Use `scripts/queryruntime-baseline-gate.sh --full` when the local `osx-arm64`
Native AOT publish and native `qre --version` smoke should be included in the
same gate. Use `--include-docker` or `--include-real-provider` only for the
explicit gated checks that require Docker or provider credentials.

The GitHub workflow `queryruntime-harness` runs the harness solution, Docker
sandbox tests, framework-dependent CLI smoke, and RepoDoctor example smoke.
Native AOT publish remains a repeatable local baseline check until P3 promotes
it to a CI lane.

## Product Boundary

The QueryRuntime project should own:

- Agent loop execution.
- Tool call parsing, recovery, and validation.
- Tool registry and tool capability metadata.
- Sandbox job specification and execution abstraction.
- Workspace mounting, diff collection, and artifact collection.
- Checkpoint storage and replay format.
- Event stream and trace output.
- Model provider abstraction.
- CLI.

The QueryRuntime project should not own, at least for the first release:

- Hosted user accounts.
- Billing.
- Public sharing.
- Email notifications.
- Full web dashboard.
- Redis/PostgreSQL/MongoDB/Qdrant as required runtime dependencies.
- Production SaaS deployment topology.
- Long-term semantic memory as a default dependency.

## Target Architecture

```text
LLM provider
  -> QueryRuntime loop
  -> tool planner / parser / recovery
  -> capability policy
  -> sandboxed tool execution
  -> checkpoint / trace / replay
  -> diff and artifact output
```

Recommended project split:

```text
src/
  CodexFlow.QueryRuntime.Abstractions
  CodexFlow.QueryRuntime.Core
  CodexFlow.QueryRuntime.Cli
  CodexFlow.QueryRuntime.Sandbox.LocalProcess
  CodexFlow.QueryRuntime.Sandbox.Docker
  CodexFlow.QueryRuntime.Sandbox.Kubernetes
  CodexFlow.QueryRuntime.ToolPacks.FileSystem
  CodexFlow.QueryRuntime.ToolPacks.Git
  CodexFlow.QueryRuntime.ToolPacks.Command
  CodexFlow.QueryRuntime.ToolPacks.DotNet
  CodexFlow.QueryRuntime.ToolPacks.Node
  CodexFlow.QueryRuntime.ToolPacks.Python
  CodexFlow.QueryRuntime.Models.OpenAICompatible
  CodexFlow.QueryRuntime.Models.Anthropic
  CodexFlow.QueryRuntime.Models.Google
  CodexFlow.QueryRuntime.Models.Ollama

examples/
  simple-agent/
  dotnet-fix-tests/
  node-fix-lint/
  python-fix-tests/
  custom-tool-pack/

docs/
  architecture.md
  cli.md
  sandboxing.md
  threat-model.md
  tool-capabilities.md
  replay-format.md
  provider-config.md
```

## Core Interfaces

This is the target public surface, not the current internal surface:

```csharp
public interface IQueryRuntimeEngine
{
    Task<QueryRuntimeResult> RunAsync(QueryRuntimeRequest request, CancellationToken ct);
}

public sealed record QueryRuntimeRequest(
    string SessionId,
    string Prompt,
    string WorkspacePath,
    QueryRuntimePolicy Policy,
    IReadOnlyList<ToolDescriptor> Tools);

public interface ISandboxRunner
{
    Task<SandboxResult> RunAsync(SandboxJobSpec spec, CancellationToken ct);
}

public sealed record SandboxJobSpec(
    string Image,
    IReadOnlyList<string> Command,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    SandboxLimits Limits,
    SandboxNetworkPolicy Network,
    SandboxMountPolicy Mounts);
```

Migration rule: ship a thin facade over the existing internal engine first, then
move internal session/worker/memory concerns behind optional adapters.

## Native AOT Strategy

Native AOT is a target deployment mode, not the starting point for the entire
platform.

Good AOT targets:

- `qre` CLI.
- Sandbox worker.
- QueryRuntime core engine.
- Built-in tool packs.
- Replay and trace tooling.

Poor AOT targets:

- MVC controllers.
- SignalR-heavy UI server.
- Dynamic plugin loading.
- Reflection-heavy framework integration.
- EF-heavy administrative services.

Known blockers to resolve before claiming AOT support:

- Replace `Newtonsoft.Json` on runtime IO boundaries with source-generated
  `System.Text.Json`.
- Avoid reflection-based tool scanning in public packages.
- Replace the current reflection-based `ChatOptions` copy in
  `QreModelExecutionPolicy` with explicit, trim-friendly mapping.
- Audit `Microsoft.Extensions.AI.AIFunction` tool schema generation before
  claiming built-in tool packs are Native AOT-ready.
- Avoid runtime assembly loading for plugins.
- Remove dynamic proxy dependencies from the CLI path.
- Add AOT publish CI and fail on trimming/AOT warnings.
- Keep MVC/SignalR/dashboard in a non-AOT server package.

Recommended AOT tranche:

1. Run an early AOT compatibility probe immediately after the local harness MVP.
   This probe is allowed to fail at first; its job is to expose trim/AOT
   blockers before sandbox, replay, and plugin work multiply the surface area.
2. Track a warning baseline for `CodexFlow.QueryRuntime.Cli` and convert obvious
   problems into design constraints, especially reflection-heavy option copying,
   dynamic JSON, and runtime-loaded plugins.
3. Define public DTOs for query requests, tool descriptors, sandbox specs, trace
   events, checkpoints, and replay records with AOT in mind.
4. Add `JsonSerializerContext` source generators for stable public DTOs as they
   solidify.
5. Replace Newtonsoft usage in `Runtime/` paths that cross process or package
   boundaries.
6. Add blocking `PublishAot=true` CI only after the probe is stable enough to be
   useful.
7. Only then market the CLI as Native AOT-ready.

## Plugin and Tool-Pack Model

Native AOT and runtime-loaded DLL plugins pull in opposite directions. Pick a
clear model before public launch.

Preferred model for open source:

- Built-in tool packs are statically linked into the official `qre` binary.
- Third-party plugins run out-of-process over a stable protocol.
- MCP over stdio is the default plugin transport.
- Tool manifests declare names, schemas, capabilities, and sandbox profile
  requirements.

Alternative advanced model:

- Users build custom host binaries that reference extra NuGet tool packs and
  publish their own AOT executable.

Do not promise arbitrary runtime DLL plugin loading in the official AOT CLI.

## Sandbox Model

.NET process-level isolation should not be treated as a security boundary. The
runtime should dispatch untrusted or semi-trusted work into OS/container/VM-level
isolation.

Sandbox levels:

| Level | Use case | Implementation |
|---|---|---|
| L0 | Trusted local development | `ProcessStartInfo` |
| L1 | Single-user local harness | Docker container per job |
| L2 | Open-source security-credible default | Rootless Docker, seccomp, cap-drop, read-only rootfs, resource limits |
| L3 | Shared service | Kubernetes Job plus gVisor or Kata RuntimeClass |
| L4 | High-risk multi-tenant execution | Firecracker or equivalent microVM isolation |
| L5 | Policy/rule/plugin execution | WASM/WASI |

Versioning expectation:

- v0.1 can ship `LocalProcessSandboxRunner`, but it must be clearly labeled as
  trusted-development-only.
- v0.2 should ship `DockerSandboxRunner`.
- Multi-tenant hosted execution should require gVisor/Kata or microVM isolation.

Minimum Docker sandbox profile:

- Non-root user.
- Drop all Linux capabilities.
- `no-new-privileges`.
- Read-only root filesystem.
- Separate writable workspace mount.
- CPU, memory, process, and timeout limits.
- Network disabled by default.
- No Docker socket mount.
- Explicit environment allowlist.
- Copy-in/copy-out or overlay workspace model so host workspaces are not
  directly mutated unless a policy allows it.

## Threat Model

The runtime should publish a real threat model before claiming security
properties. The goal is reducing blast radius, not proving prompt injection is
solved.

Threats:

- Malicious repositories with hostile files, tests, scripts, hooks, or package
  metadata.
- Prompt injection in tool output.
- Malicious model output requesting unsafe tool calls.
- Malicious or compromised MCP/tool servers.
- Secret exfiltration through files, environment variables, git remotes, package
  scripts, or network calls.
- Destructive commands such as `rm`, `git reset`, `git clean`, database
  migrations, package publish, and git push.
- Dependency installation executing arbitrary package lifecycle scripts.
- Provider-side or proxy-side leakage of prompts and repository contents.

Target mitigations before public security claims:

Current Phase 2b status: readonly path containment, command capability
classification, destructive-action detection, explicit approval records, and
Docker runner isolation are implemented for the current QRE slice. Docker mode
now covers default network deny, non-root execution, capability drop,
read-only root filesystem, workspace staging, timeout cleanup, output limits,
and common host-secret path checks. These are still scoped harness controls,
not a complete public security guarantee; Kubernetes / VM isolation, broader
secret-file policy, and cross-platform hardening remain target requirements.

- Tag all file and command output as untrusted content when it returns to the
  model.
- Re-check capabilities on every tool call, not just when a plan is approved.
- Deny network egress by default in sandbox profiles.
- Never mount `~/.ssh`, `~/.aws`, cloud credentials, browser profiles, Docker
  socket, or host secret stores.
- Block or require explicit approval for `.env`, `secrets.json`, credentials,
  private keys, and token-like files.
- Require confirmation for destructive, external, or credential-bearing actions.
- Keep model/provider API keys out of sandbox child environments unless a tool
  explicitly requires them.
- Record every approved high-risk action in the trace.

## Capability-Based CLI Tools

Do not grow command support by adding a broad command whitelist. Model every
command as a capability contract.

Example command capability:

```json
{
  "name": "dotnet-test",
  "executable": "dotnet",
  "argsPattern": ["test", "*"],
  "capabilities": ["read_fs", "execute", "write_artifacts"],
  "readScope": ["**/*"],
  "writeScope": ["bin/**", "obj/**", "TestResults/**"],
  "network": {
    "mode": "deny"
  },
  "process": {
    "timeoutSeconds": 300,
    "maxProcesses": 64,
    "maxOutputBytes": 1048576
  },
  "budget": {
    "maxTokens": 200000,
    "maxUsd": 1.0
  },
  "sandboxProfile": "build",
  "approval": "auto"
}
```

Initial capability categories:

- `read_fs`
- `write_fs`
- `write_artifacts`
- `execute`
- `run_tests`
- `build`
- `install_packages`
- `network`
- `git_read`
- `git_write`
- `git_push`
- `secret_access`
- `destructive`
- `external_side_effect`

Required policy profiles:

| Profile | Purpose | Defaults |
|---|---|---|
| `readonly` | Review, planning, grep, file inspection | no writes, no network, no shell except safe listing/search commands |
| `verify` | Build/test without package installation | write only artifacts, no external side effects |
| `build` | Restore/build/test with package manager access | restricted network allowlist, lifecycle-script policy explicit |
| `repair` | Controlled file edits | workspace-only writes, no secrets, no external side effects |
| `release` | git push, package publish, deploy | manual approval required for each external side effect |

Commands such as `npm install`, `pip install`, `dotnet restore`, and `git push`
must require explicit policy approval because they involve network access,
package execution, credentials, or external side effects.

## Replay and Trace Contract

Do not claim deterministic replay until the replay contract is explicit.

Trace should be JSONL plus content-addressed blob storage:

```text
.qre/runs/<run-id>/
  run.json
  events.jsonl
  checkpoints/
  blobs/sha256/<prefix>/<hash>
  diff.patch
  artifacts/
```

Replay modes:

- `trace`: show recorded events without executing anything.
- `replay`: replay recorded model responses and tool outputs without calling the
  LLM provider or running tools.
- `rerun`: execute again from the same prompt and policy, allowing divergence.
- `strict-replay`: fail if a required recorded event or blob is missing.

Minimum event records:

- User prompt and runtime policy.
- Full model request and raw model response.
- Streamed model deltas when streaming is enabled.
- Tool call name, arguments, policy decision, start/end time, exit code.
- stdout/stderr/output blobs by content hash.
- File snapshots or diffs for workspace writes.
- Environment allowlist visible to the sandbox.
- Budget consumption: tokens, USD estimate, wall-clock, process counts.

## CLI Experience

The CLI should be the first-class entry point:

```bash
qre init
qre run --workspace . "find and fix the failing tests"
qre trace latest
qre replay latest
qre rerun latest
qre tool list
qre sandbox exec --profile build -- dotnet test
qre diff latest
```

Required CLI decisions before implementation:

- Key storage: environment variables first, optional `.qre/config.toml`, no
  plaintext secrets written by default.
- Approval modes: `--ask`, `--auto-approve readonly`, `--auto-approve verify`,
  and explicit `--dangerously-auto-approve-all` only for sandboxed CI.
- Cost cap: `--max-budget-usd`, `--max-tokens`, and wall-clock timeout.
- Interrupt behavior: Ctrl-C should produce a usable checkpoint and mark the run
  interrupted.
- Output: human-readable by default, `--json`, `--jsonl`, and `--trace-file` for
  automation.
- Telemetry: no outbound telemetry by default; opt-in only.

The README first screen should avoid real `curl | sh` until releases are signed.
Once used, publish checksums and signatures.

Example first screen:

```bash
qre init
qre run --workspace ./my-repo --profile readonly "explain why the tests are failing"
qre trace latest
```

## Implementation Phases

### Phase -1: Mechanical Rename and Hygiene Baseline

Goal: remove legacy branding and obvious runtime-artifact leakage before the
harness facade starts producing public traces and package names.

Do:

- Rename public namespaces, package names, examples, and tool-facing strings
  to the `CodexFlow.*` namespace and package family.
- Remove legacy project paths from tool descriptions and examples.
- Ensure `workspaces/`, logs, generated reports, and scratch files are ignored.
- Decide whether existing `CodexLocalClient` is retired, extended into `qre`, or
  retained only as a Gateway regression harness.
- Create a harness-only solution or test slice such as
  `CodexFlow.QueryRuntime.slnx` or `Category=Harness`.
- Current first slice: `CodexFlow.QueryRuntime.slnx` contains
  `CodexFlow.Contracts`, `CodexFlow.Core`,
  `CodexFlow.QueryRuntime.UnitTests`, and
  `CodexFlow.QueryRuntime.IntegrationTests`. `CodexFlow.Core.Tests` remains
  outside this first slice because it mixes QueryRuntime unit tests with broader
  platform regressions that need separate debt cleanup before they can become an
  extraction gate.

Do not:

- Change runtime behavior.
- Start the public extraction repository.
- Mix the rename with sandbox, CLI, or provider changes.

Acceptance criteria:

```bash
legacy_project="$(printf 'Ivilson%s' 'Codex')"
legacy_slug="$(printf 'ivilson%s' 'codex')"
rg "$legacy_project|$legacy_slug" CodexFlow* docs README.md \
  --glob '!docs/archived-blueprints/**' \
  --glob '!docs/bugfixed/**'
dotnet test CodexFlow.slnx
```

- No legacy brand appears in public package names, namespaces, tool
  descriptions, or docs that will be part of the harness.
- Workspace artifacts are ignored going forward.
- The harness test slice is identified and can run separately from private
  service-heavy platform tests.
- Current non-archived docs that still mention `Ivilson*` entity names or
  `ivilson_` tool names are tracked as explicit migration debt rather than
  silently treated as completed public docs.
- Current command for the first slice:

```bash
dotnet test CodexFlow.QueryRuntime.slnx --no-restore
```

Estimated effort: 1 week.

Phase -1 progress as of 2026-06-02:

- Completed: C# namespaces and project-facing strings were mechanically moved
  to `CodexFlow.*`; the text-service proto C# namespace and generated Python
  gRPC descriptor were regenerated; Web UI package metadata and local HTTP
  scratch file names were updated; the old absolute test workspace path was
  replaced with `CODEXFLOW_TEST_WORKSPACE_ROOT` plus a temp-directory default.
- Completed: `CodexFlow.QueryRuntime.slnx` was added as the first harness test
  slice, currently covering `CodexFlow.Contracts`, `CodexFlow.Core`,
  `CodexFlow.QueryRuntime.Abstractions`,
  `CodexFlow.QueryRuntime.Experimental`, `CodexFlow.QueryRuntime.Cli`,
  `CodexFlow.QueryRuntime.Sandbox.LocalProcess`,
  `CodexFlow.QueryRuntime.UnitTests`, and
  `CodexFlow.QueryRuntime.IntegrationTests`.
- Completed: `AGENTS.md` and `CLAUDE.md` now include a QueryRuntime harness
  work section that points future agents at this strategy document and keeps
  Web/Identity/database/UI surfaces out of harness extraction by default.
- Verified:

```bash
dotnet build CodexFlow.slnx --no-restore
dotnet test CodexFlow.QueryRuntime.slnx --no-restore
```

- Current verification result: full solution build passes with existing
  nullable/xUnit/package warnings; QueryRuntime slice has local tests and
  gated live-provider tests, with live-provider tests skipped by default unless
  explicitly enabled.
- Remaining Phase -1 debt: public tool names with the legacy `ivilson_` prefix
  still exist and should be migrated through aliases rather than a silent
  breaking rename; `Ivilson*` chat/session entity names remain in the EF model
  and should be handled as a separate domain/migration decision; non-archived
  current docs such as `docs/agent-tools-tech.md`, `docs/validator-tech.md`,
  `docs/spike-reports/ExploreWorkerSpikeReport.md`,
  `docs/上下文压缩修复实施蓝图.md`, and `docs/计费系统详细设计蓝图.md`
  still reference those legacy tool/entity names and need a planned docs
  migration; historical docs under `docs/archived-blueprints` and
  `docs/bugfixed` still preserve old names as historical records.
- Known test-slice boundary: adding all of `CodexFlow.Core.Tests` to the
  harness slice currently introduces platform-wide failures unrelated to the new
  solution file, including prompt snapshot drift, a missing
  `docs/feature/tool-surface-snapshot.md`, and stop-hook termination assertions.
  These should be cleaned before Core unit tests become a release gate for the
  extracted harness.

### Phase 0: Feasibility Spike

Goal: prove the current runtime can be wrapped as a harness without a large
rewrite.

Do:

- Keep the existing `CodexFlow.Core/Runtime/QueryRuntimeEngine.cs` in place.
- Create an experimental facade such as `CodexFlow.QueryRuntime.Experimental`.
- Wrap a minimal request shape: prompt, workspace path, model config, tool set,
  run id, and trace path.
- Implement minimal in-memory or no-op adapters for engine dependencies that
  should not leak into the harness surface: model execution, context window,
  tool execution coordinator, runtime hook dispatcher, telemetry, and recall.
- Reuse the current configured provider only where it does not pull in Web UI,
  identity, Redis, PostgreSQL, MongoDB, Qdrant, or SignalR.
- Introduce a write-only trace sink in this phase rather than waiting for the
  full `ITraceStore`.
- Emit `.qre/runs/<run-id>/events.jsonl`.
- Add a minimal CLI spike with:
  - `qre run --workspace . "analyze this repo"`
  - `qre trace latest`
- Current repository docs should use `qre ...` as the CLI form. If a local
  machine does not have `qre` on `PATH`, publish the native binary first and
  add the publish directory to `PATH`.
- Add a go/no-go checkpoint: if the facade requires more than a small adapter
  layer, stop wrapping and plan a smaller runtime core extraction instead.

Do not:

- Attempt Native AOT.
- Implement Docker sandboxing.
- Build a plugin system.
- Implement deterministic replay.

Acceptance criteria:

```bash
qre run --workspace ./some-repo "analyze architecture risks"
qre trace latest
```

- The command runs without Web UI, SignalR, or user-account dependencies.
- A trace file is created under `.qre/runs/<run-id>/events.jsonl`.
- The trace parses as JSONL and contains at least:
  - run start
  - model request
  - model response
  - one tool call when tools are used
  - run completed or run failed
- The output includes the final answer and enough tool events to debug the run.

Estimated effort: 3-4 weeks after Phase -1.

Phase 0 progress as of 2026-06-02:

- Completed: `CodexFlow.QueryRuntime.Experimental` now wraps the existing
  `CodexFlow.Core/Runtime/QueryRuntimeEngine.cs` behind
  `ExperimentalQueryRuntimeHarness`.
- Completed: the experimental facade accepts prompt, workspace path, run id,
  trace root, max rounds, tool enablement, and an explicit `AIFunction` tool
  list. It uses a replaceable `IExperimentalModelClient` so provider wiring can
  be added without pulling in Web UI, Identity, SignalR, PostgreSQL, MongoDB,
  Redis, or Qdrant.
- Completed: `JsonlTraceEventSink` writes `.qre/runs/<run-id>/events.jsonl`
  with `run.started`, `model.request`, `model.response`, tool events,
  `runtime.terminated`, and `run.completed` / `run.failed` records.
- Completed: `ChatClientExperimentalModelClient` adapts any
  `Microsoft.Extensions.AI.IChatClient` to the experimental runtime. The CLI
  can create a real provider client through the existing `VllmChatClientFactory`
  with `--api-url`, `--api-key`, `--model`, and `--api-mode`, or environment
  fallbacks `QRE_API_URL`, `QRE_API_KEY`, `QRE_MODEL`, and `QRE_API_MODE`.
  This Phase 0 provider-factory note is superseded by Phase 1.5, where the CLI
  moved to its own `QreVllmChatClientFactory` and no longer depends on
  `CodexFlow.Core` provider wiring.
- Completed: `QreModelExecutionPolicy` applies QRE's default thinking policy.
  In `auto` mode, thinking is disabled whenever tools are enabled or a JSON /
  schema response format is requested. The CLI exposes `--thinking auto|off|on|preserve`
  and `--json-output`; explicit `on` / `preserve` modes are for provider
  compatibility experiments rather than the default harness path.
- Completed: `ExperimentalReadOnlyToolPack` provides the first intentionally
  small tool surface: `qre_list_files`, `qre_read_file`, and
  `qre_search_files`. These tools resolve all paths under the workspace root
  and reject path traversal outside the workspace.
- Completed: `ExperimentalHarnessRealLlmPhaseTests` uses the repository
  `CodexFlow/appsettings.json` `VllmAgent` configuration for gated live tests
  covering provider configuration, streaming, no-tool harness tracing, and
  readonly tool execution through the experimental harness.
- Completed: `CodexFlow.QueryRuntime.Cli` provides a spike CLI:

```bash
qre run --workspace . --response "offline smoke" "analyze this repo"
qre run --workspace . --response "offline smoke" --json "analyze this repo"
qre run --workspace . --profile readonly --thinking auto "analyze this repo"
qre run --workspace . --json-output "return a JSON summary"
qre tool list --workspace . --profile readonly --json
qre trace latest --workspace . --json
qre replay latest --workspace . --json
```

- Completed: the CLI now collects raw command-line values into explicit
  configuration objects: `QueryRuntimeProviderOptions`,
  `QueryRuntimeToolProfile`, `QueryRuntimeModelPolicyOptions`,
  `QueryRuntimeOutputOptions`, and `QueryRuntimeExecutionOptions`.
  `--json-output` remains the model response-format request, while `--json`
  is the machine-readable CLI output switch.
- Completed: `qre replay latest` now provides a replay-format skeleton in
  `trace-summary` mode. It reads the latest `.qre/runs/<run-id>/events.jsonl`,
  reports event counts, model response counts, tool result counts, and the
  terminal record without calling a provider or executing tools. This is not
  deterministic replay yet; it is the compatibility shape for the future replay
  adapter.
- Current limitation at Phase 0 time: the real-provider CLI path depended on the existing
  `VllmChatClientFactory` in `CodexFlow.Core`, including model-family
  heuristics and a default fallback client for unknown model names. It is useful
  for the branch spike and verified model families but is not the final
  provider-neutral package boundary. This Core dependency was removed in
  Phase 1.5; the remaining current limitation is the QRE-local model-family
  heuristic. The next extraction pass should move provider-neutral
  configuration and concrete model adapters behind
  `CodexFlow.QueryRuntime.Models.*` packages and make unsupported providers
  fail explicitly.
- Verified:

```bash
dotnet test CodexFlow.QueryRuntime.slnx
RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj --filter "FullyQualifiedName~ExperimentalHarnessRealLlmPhaseTests" --logger "console;verbosity=detailed"
qre run --workspace .tmp-build/qre-smoke/workspace --response "CLI readonly smoke" --profile readonly --max-rounds 2 "list the files"
qre run --workspace .tmp-build/qre-smoke/workspace --response "{\"ok\":true}" --json-output "return json"
qre run --workspace .tmp-build/qre-smoke/workspace --response "CLI json smoke" --json "analyze architecture risks"
qre tool list --workspace . --profile readonly --json
qre trace latest --workspace .tmp-build/qre-smoke/workspace --json
qre replay latest --workspace .tmp-build/qre-smoke/workspace --json
dotnet build CodexFlow.slnx --no-restore
```

- Current verification result: QueryRuntime slice passes with local tests and
  skips gated live-provider tests by default; the live experimental harness
  phase test passes 5/5 when `RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true`
  using the project's configured `VllmAgent`; CLI smoke writes and reads JSONL
  trace successfully and reports the registered read-only tools; full solution
  build passes with existing warnings.
- Real-provider finding: the current `deepseek-v4-pro` Anthropic Messages
  endpoint rejects required/object `tool_choice` while in thinking mode, so the
  QRE default is to disable thinking for tool calls and JSON/schema-constrained
  outputs. The live readonly-tool phase validates provider auto-tool mode
  instead of forced provider-level tool choice. Required tool contracts remain a
  runtime-level harness capability and need provider-specific policy before
  being marketed as universally supported.
- Updated finding: `VllmChatClient` 2.0.21 fixes the Anthropic Messages
  thinking-off request path. Wire-level smoke shows `thinking: { "type":
  "disabled" }`, and QRE `--thinking off` returns fixed assistant text without
  thinking content in trace.
- Next Phase 0 work: decide whether the facade remains small enough to continue
  wrapping or should trigger a smaller runtime core extraction; then define the
  first public trace DTOs needed by deterministic replay.

### Phase 1: Local Harness MVP

Goal: create a developer-usable local harness while clearly stating that local
process execution is trusted-development-only.

Do:

- Create a real `qre` CLI project.
- Define and introduce stable runtime contracts:
  - `IQueryRuntimeEngine`
  - `IToolRegistry`
  - `IModelClient`
  - `ITraceStore`
  - `ISandboxRunner`
- Implement `LocalProcessSandboxRunner`.
- Create a tool partitioning matrix for existing tools:
  - harness-core
  - optional tool pack
  - platform-only
  - remove/drop
- Add initial tool packs:
  - file read/search
  - git status/diff
  - command execution
  - dotnet build/test
- Add policy profiles:
  - `readonly`
  - `verify`
  - `repair`
- Support:
  - `qre run`
  - `qre tool list`
  - `qre trace`
  - `qre diff`
  - `qre sandbox exec`
- Keep OpenAI-compatible and current configured providers working.
- Add one .NET example workflow.
- Add harness-only CI that does not require private services.

Do not:

- Claim hard sandboxing.
- Promise Native AOT.
- Support arbitrary third-party plugins.
- Require Redis, PostgreSQL, MongoDB, Qdrant, Web UI, or SignalR.

Acceptance criteria:

```bash
qre run --workspace ./some-repo --profile readonly "analyze architecture risks"
qre run --workspace ./some-repo --profile verify "run tests and explain failures"
qre trace latest
qre diff latest
```

- `readonly` profile does not write workspace files.
- `verify` profile can run builds/tests and collect logs.
- Runs produce traces, summaries, and diffs/artifacts when applicable.
- CI passes without private services.

Estimated effort: 4-6 weeks after Phase 0.

Phase 1 first-slice progress as of 2026-06-02:

- Completed: `CodexFlow.QueryRuntime.Abstractions` now defines the first stable
  public contract surface under the `CodexFlow.QueryRuntime.Abstractions`
  namespace:
  - `IQueryRuntimeEngine`
  - `IModelClient`
  - `IToolRegistry`
  - `ITraceStore`
  - `ISandboxRunner`
  - `QueryRuntimeRequest`
  - `QueryRuntimeResult`
  - `QueryRuntimeProviderOptions`
  - `QueryRuntimeToolProfile`
  - `QueryRuntimeModelPolicyOptions`
  - `QueryRuntimeOutputOptions`
  - `QueryRuntimeExecutionOptions`
  - sandbox DTOs such as `SandboxJobSpec`, `SandboxLimits`,
    `SandboxNetworkPolicy`, `SandboxMountPolicy`, and `SandboxResult`.
- Completed: `ExperimentalQueryRuntimeHarness` implements the stable
  `IQueryRuntimeEngine` contract while still preserving the richer
  `ExperimentalQueryRuntimeRequest` path used by the CLI spike. The stable
  contract currently maps `none` and `readonly` tool profiles into the
  experimental facade.
- Completed: `CodexFlow.QueryRuntime.Cli` now consumes the public options from
  `CodexFlow.QueryRuntime.Abstractions` instead of private sealed option
  records inside `Program.cs`.
- Completed: `CodexFlow.QueryRuntime.Sandbox.LocalProcess` introduces
  `LocalProcessSandboxRunner`, a trusted-development-only implementation of
  `ISandboxRunner` using `ProcessStartInfo`, working-directory validation,
  timeout handling, empty-by-default child environments with explicit
  environment injection, and bounded stdout/stderr capture.
- Completed: new harness tests cover the stable engine contract, CLI
  `run --json` / `trace latest --json` smoke path, and
  `LocalProcessSandboxRunner`.
- Current limitation: this first slice does not yet implement `verify` or
  `repair` profiles, command capability policy, `qre diff`, deterministic
  replay, Docker isolation, or packaged distribution of the `qre` binary.
  `LocalProcessSandboxRunner` also does not enforce network or mount policy; it
  is a contract-compatible runner for trusted local execution only.
- Verified:

```bash
dotnet test CodexFlow.QueryRuntime.slnx --no-restore
```

- Current verification result: QueryRuntime slice passes with 12 local unit
  tests, 2 local integration tests, and 12 gated real-provider tests skipped by
  default.

Phase 1 second-slice progress as of 2026-06-02:

- Completed: `QueryRuntimeToolProfile` now has explicit `None`, `ReadOnly`,
  `Verify`, and `Repair` profile constants. `Repair` is declared as a target
  profile but is not wired to write tools yet.
- Completed: `QueryRuntimeCapabilities` defines the first small capability
  vocabulary: `read_fs`, `write_artifacts`, `execute_process`, `git_read`,
  `run_tests`, and `build`.
- Completed: `ExperimentalToolRegistry` exposes tool descriptors with
  capability metadata for `readonly` and `verify` profiles. This is still an
  experimental registry, not the final policy engine.
- Completed: `ExperimentalVerifyToolPack` adds trusted-local verify tools:
  - `qre_git_status`
  - `qre_git_diff`
  - `qre_dotnet_test`
- Completed: the CLI supports `--profile verify`, and
  `qre tool list --profile verify --json` returns capability metadata. `--tools`
  remains as a backward-compatible alias for the earlier spike CLI.
- Completed: the CLI now has a first `qre diff latest` skeleton. Current mode is
  `workspace-git-diff`: it reads the current workspace `git status --short` and
  `git diff` through `LocalProcessSandboxRunner`. It does not yet read a
  run-scoped patch from the latest `.qre/runs/<run-id>/` directory.
- Current limitation: `verify` uses trusted local process execution and does not
  enforce Docker isolation, network egress policy, package lifecycle-script
  controls, or full command approval. `qre_dotnet_test` uses `--no-restore` by
  default to avoid implicit restore/network behavior in this slice.
- Verified:

```bash
dotnet test CodexFlow.QueryRuntime.slnx --no-restore
```

- Current verification result: QueryRuntime slice passes with 14 local unit
  tests, 2 local integration tests, and 12 gated real-provider tests skipped by
  default.

Phase 1 third-slice progress as of 2026-06-02:

- Completed: `CodexFlow.QueryRuntime.Abstractions` now includes
  `IQueryRuntimeCapabilityPolicy`, `QueryRuntimeCapabilityRequest`,
  `QueryRuntimeCapabilityDecision`, and `QueryRuntimeCapabilityDecisionKind`.
  This moves capability handling from descriptor-only metadata toward an
  executable policy decision boundary.
- Completed: `ExperimentalCapabilityPolicy` implements the first conservative
  policy:
  - `readonly` allows read-only capabilities and denies process execution.
  - `verify` allows the current trusted-local verify capabilities only when the
    command shape is one of the supported commands.
  - `qre_dotnet_test` must include `--no-restore`.
  - non-denied network policy is rejected.
  - `repair` now exposes controlled workspace file write/patch tools; arbitrary
    process execution remains outside the repair tool surface.
- Completed: `ExperimentalVerifyToolPack` now evaluates the capability policy
  before calling `ISandboxRunner`. A deny or approval-required decision throws
  before the local process runner is invoked.
- Completed: unit tests verify default policy allow/deny behavior and prove that
  a denied verify tool invocation does not run the sandbox command.
- Completed: `CodexFlow.QueryRuntime.Cli` now includes `qre policy check`, which
  evaluates the current experimental policy without invoking a tool or starting
  a sandbox runner. Example:

```bash
qre policy check --workspace . --profile verify --tool qre_dotnet_test --json -- \
  dotnet test CodexFlow.QueryRuntime.slnx --no-restore
```

- Current limitation: policy enforcement is still in the experimental tool
  pack, not yet centralized in a shared tool execution coordinator. The policy
  also does not provide interactive approval prompts yet; `RequireApproval`
  currently blocks execution.
- Completed: `JsonlTraceStore` implements the first `ITraceStore` reader for
  latest-run JSONL summaries, and CLI trace/replay paths now reuse it.
- Completed: `JsonlTraceEventSink` writes `policy.decision` records when verify
  tools evaluate capability policy inside the harness-built tool profile.
- Completed: `qre_dotnet_build` adds a trusted-local `dotnet build --no-restore`
  verify tool.
- Completed: `docs/queryruntime-tool-partition-matrix.md` adds the first tool
  partition matrix for Phase 1.
- Verified:

```bash
dotnet test CodexFlow.QueryRuntime.slnx --no-restore
```

- Current verification result: QueryRuntime slice passes with 26 local unit
  tests, 2 local integration tests, and 12 gated real-provider tests skipped by
  default.

Phase 1 fourth-slice progress as of 2026-06-02:

- Completed: every experimental QRE run now writes a run artifact manifest next
  to the JSONL trace:

```text
.qre/runs/<run-id>/events.jsonl
.qre/runs/<run-id>/manifest.json
```

- Completed: `manifest.json` records the run schema version, run id, session id,
  workspace path, trace file path, run directory, tool profile, status,
  termination reason, round count, tool-call count, duration, and timestamp.
  Failed runs also write a manifest after the `run.failed` trace record.
- Completed: `JsonlTraceStore` now exposes shared helpers for latest-run
  discovery, run-directory resolution, run id extraction, and best-effort
  manifest reading.
- Completed: CLI JSON output for `run`, `trace latest`, `replay latest`, and
  `diff latest` is now run-aware. The commands expose `runId`, `runDirectory`,
  and `manifestPath` where a latest run exists; `replay latest` also returns the
  parsed manifest.
- Current limitation: `diff latest` is still `workspace-git-diff`. The command
  is linked to the latest run metadata, but it does not yet read a run-scoped
  `diff.patch` artifact. That requires write/repair tooling or explicit
  artifact capture in a later slice.
- Verified:

```bash
dotnet test CodexFlow.QueryRuntime.slnx --no-restore
```

- Current verification result: QueryRuntime slice passes with 26 local unit
  tests, 2 local integration tests, and 12 gated real-provider tests skipped by
  default.

Phase 1 fifth-slice progress as of 2026-06-02:

- Completed: `.github/workflows/queryruntime-harness.yml` adds a harness-only
  GitHub Actions workflow for the QRE slice.
- Completed: the workflow restores and tests `CodexFlow.QueryRuntime.slnx`,
  builds the CLI, and runs offline CLI smoke commands for:
  - `--version`
  - `init --workspace . --json`
  - `doctor --workspace . --json`
  - `run --profile readonly --json`
  - `tool list --profile verify --json`
  - `policy check --profile verify --tool qre_dotnet_build`
  - `sandbox exec --profile verify -- git status --short`
  - `replay latest --json`
- Completed: the workflow intentionally avoids Aspire, PostgreSQL, MongoDB,
  Redis, Qdrant, SignalR, the Web UI, and real LLM credentials. This makes it a
  candidate baseline for open-source CI.
- Current limitation: this CI proves the local harness contract and CLI smoke
  path only. It does not prove Docker/Kubernetes sandbox isolation, Native AOT
  publishing, real-provider compatibility, or package distribution.
- Verified locally:

```bash
dotnet test CodexFlow.QueryRuntime.slnx --no-restore
dotnet build CodexFlow.slnx --no-restore
```

- Current local verification result: QueryRuntime slice passes with 28 local
  unit tests, 2 local integration tests, and 12 gated real-provider tests
  skipped by default. Full solution build passes with the existing
  `System.Net.Http.Json` NU1510 warning in `CodexFlow.Tests.csproj`.

Phase 1 sixth-slice progress as of 2026-06-02:

- Completed: `examples/RepoDoctor` adds a real cross-platform .NET console
  example that consumes QRE through the CLI process boundary.
- Completed: the example parses `qre run --json`, prints the final result,
  run id, trace path, and manifest path, then calls
  `qre replay latest --summary --json` to print replay statistics.
- Completed: `RepoDoctor` supports deterministic offline smoke via
  `--offline-response` and configurable CLI location via `--qre-bin` or
  `QRE_BIN`.
- Completed: the harness-only GitHub Actions workflow includes a RepoDoctor
  offline smoke step, so the example is exercised in the same CI lane as the
  QRE CLI.
- Current limitation: this is still a process-boundary example, not a stable
  SDK sample. That is intentional for Phase 1 because the public in-process API
  is still being hardened.
- Verified locally:

```bash
dotnet build examples/RepoDoctor/RepoDoctor.csproj
dotnet run --project examples/RepoDoctor --no-build -- \
  --offline-response "offline analysis" .
RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true dotnet test \
  CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj \
  --filter "FullyQualifiedName~ExperimentalHarnessRealLlmPhaseTests" \
  --logger "console;verbosity=detailed"
```

- Current real-provider verification result: 5 gated QueryRuntime real LLM
  tests pass against the project appsettings provider configuration
  (`deepseek-v4-pro`, `AnthropicMessages`), including streaming,
  Anthropic Messages thinking-off, no-tool trace, and readonly tool execution.
  Earlier 2026-06-02 runs passed 4/4 before the dedicated thinking-off
  regression was added.

Phase 1 seventh-slice progress as of 2026-06-02:

- Completed: the CLI now supports `--version` / `version`, backed by explicit
  `0.1.2` package metadata.
- Completed: the CLI now supports `qre doctor --workspace . --json`, a read-only
  environment diagnostic command that checks workspace existence, `dotnet`,
  `git`, latest trace discovery, and whether the `QRE_API_URL`, `QRE_API_KEY`,
  and `QRE_MODEL` environment variables are configured. It reports whether
  provider env is configured without printing secret values.
- Completed: `doctor` is included in the harness-only CI smoke lane and in
  `CLAUDE.md` for future agent handoff.
- Current limitation: `doctor` is a local readiness diagnostic, not a security
  audit. It does not validate Docker isolation, network egress, provider data
  policy, or Native AOT readiness.
- Verified locally:

```bash
qre --version
qre init --workspace . --json
qre doctor --workspace . --json
dotnet test CodexFlow.QueryRuntime.slnx --no-restore
dotnet test CodexFlow.QueryRuntime.slnx --configuration Release --no-restore
```

Phase 1 eighth-slice progress as of 2026-06-02:

- Completed: `qre init --workspace . --json` now creates a local `.qre`
  scaffold with `.qre/config.toml` and `.qre/README.md`.
- Completed: the generated config template does not write provider secrets. It
  documents the supported environment variables (`QRE_API_URL`, `QRE_API_KEY`,
  `QRE_MODEL`, and `QRE_API_MODE`) and local defaults such as readonly profile
  and trace root.
- Completed: `init` does not overwrite existing template files unless `--force`
  is provided. Its JSON output reports created and skipped files.
- Completed: the harness-only CI smoke lane and agent handoff docs now include
  `init`.
- Current limitation: `.qre/config.toml` is a scaffold and documentation anchor
  in Phase 1. The CLI still reads provider configuration from command-line
  options and environment variables; config-file loading is a later hardening
  task.
- Verified locally:

```bash
dotnet test CodexFlow.QueryRuntime.slnx --no-restore
qre init --workspace . --json
```

Phase 1 ninth-slice progress as of 2026-06-02:

- Completed: `qre sandbox exec --workspace . --profile verify --json -- <cmd>`
  adds a direct CLI entry point for policy-gated trusted-local process
  execution.
- Completed: `sandbox exec` does not invoke a shell and does not accept generic
  arbitrary commands. It maps the command shape to the current verify tool
  descriptors (`qre_git_status`, `qre_git_diff`, `qre_dotnet_build`,
  `qre_dotnet_test`) and then evaluates `ExperimentalCapabilityPolicy`.
- Completed: denied commands return a non-zero CLI exit code and include the
  policy decision in JSON without starting `LocalProcessSandboxRunner`.
- Completed: the harness-only CI smoke lane now exercises `sandbox exec` with
  `git status --short`.
- Current limitation: despite the command name, this is still
  trusted-local execution through `LocalProcessSandboxRunner`, not hard
  sandboxing. Docker/Kubernetes/VM runner work remains out of Phase 1.
- Verified locally:

```bash
dotnet test CodexFlow.QueryRuntime.slnx --no-restore
qre sandbox exec --workspace . --profile verify --json -- git status --short
```

Phase 1 completion checklist as of 2026-06-02:

| Area | Phase 1 status | Evidence |
|---|---|---|
| CLI project | Complete | `CodexFlow.QueryRuntime.Cli`, `--version`, `run`, `init`, `doctor`, `tool`, `policy`, `trace`, `replay`, `diff`, `sandbox exec` |
| Stable contracts | Complete for Phase 1 | `CodexFlow.QueryRuntime.Abstractions` exposes runtime, model, trace, tool, policy, and sandbox contracts |
| Trusted local runner | Complete for Phase 1 | `CodexFlow.QueryRuntime.Sandbox.LocalProcess` implements `ISandboxRunner` |
| Tool partition matrix | Complete for Phase 1 | `docs/queryruntime-tool-partition-matrix.md` |
| Readonly profile | Complete | file list/read/search tools, readonly CLI smoke, real-provider readonly tool test |
| Verify profile | Complete for trusted local | git status/diff, dotnet build/test, `policy check`, and `sandbox exec` |
| Repair profile | MVP implemented | `qre_write_file` and `qre_apply_patch` provide controlled workspace-scoped edits with path, symlink, protected-artifact, and secret-looking path guards |
| Trace/replay | Complete for Phase 1 | JSONL trace, run manifest, latest trace summary, replay summary |
| Diff | Complete for current QRE slice | `diff latest` reads run-scoped `diff.patch` first and falls back to workspace git diff |
| Provider compatibility | Complete for configured provider | gated real LLM tests pass 5/5 against `deepseek-v4-pro` / `AnthropicMessages`, including thinking-off |
| .NET example | Complete | `examples/RepoDoctor` and CI smoke |
| Harness-only CI | Complete | `.github/workflows/queryruntime-harness.yml` |
| Public README entry | Complete | README includes QueryRuntime Harness positioning, commands, docs, and boundary warning |

Items explicitly not counted as Phase 1 completion:

- hard sandbox / Docker / Kubernetes / VM isolation
- repair-mode write tools and patch apply
- deterministic replay with recorded model/tool adapters
- run-scoped `diff.patch` generation
- Native AOT publish and signed binary distribution. AOT compatibility probing
  moves to Phase 1.5 so trim/AOT issues are discovered early, but AOT is not a
  shipped Phase 1 feature.
- arbitrary third-party plugins
- config-file provider loading beyond the non-secret `.qre/config.toml` scaffold

Gemini CLI review follow-up completed as of 2026-06-02:

- Fixed: `LocalProcessSandboxRunner` no longer inherits host environment
  variables by default. Child process environment is cleared and only
  `SandboxJobSpec.Environment` entries are injected.
- Fixed: verify tools and CLI `sandbox exec` now inject a trusted-local
  environment allowlist through `TrustedLocalSandboxEnvironment`, keeping SDK
  and CLI commands functional without passing arbitrary host secrets.
- Fixed: timeout cleanup now also ignores `Win32Exception` and
  `NotSupportedException` from `Process.Kill`.
- Fixed: `.qre/config.toml` template now states that it is a Phase 1 scaffold
  and is not parsed by the CLI yet.
- Added tests for environment non-inheritance, explicit environment injection,
  trusted-local environment injection without host secrets, `sandbox exec`
  non-zero process exit-code bubbling, and real `dotnet build --no-restore`
  execution through `sandbox exec`.

Claude Code final review follow-up completed as of 2026-06-02:

- Fixed: `SandboxJobSpec.Network` and `SandboxJobSpec.Mounts` now document that
  LocalProcess treats these fields as advisory except for defensively rejecting
  `Network.Allow`.
- Fixed: `LocalProcessSandboxRunner` rejects `Network.Allow`, returns a clean
  `127` result when a process cannot start, and has tests for timeout and output
  truncation paths.
- Fixed: readonly and verify path resolution now share stricter workspace
  containment checks, including cross-root rejection and symlink escape
  rejection where the platform exposes symlink metadata.
- Fixed: `qre sandbox exec` writes a lightweight JSONL audit trace for started,
  policy decision, and completed events.
- Documented: `sandbox exec` remains lower-level than verify tool functions and
  does not perform all tool-pack argument normalization.
- Added policy regression tests for network denial, git command shape gating,
  repair approval, unknown profile denial, and capability-profile mismatch.

### Phase 1.5: AOT Compatibility Probe

Goal: make Native AOT an architectural constraint early without claiming that
the CLI is AOT-ready.

Why this phase moves earlier:

- AOT issues are cheapest to fix before Docker sandboxing, deterministic replay,
  and out-of-process plugin protocols expand the runtime surface.
- Waiting until release work risks a late rewrite of JSON DTOs, tool schema
  generation, provider options, plugin loading, and reflection-heavy helpers.
- The public positioning benefits from being honest: "AOT-aware from early
  phases" is stronger and safer than "AOT promised at the end."

Do:

- Add a non-blocking AOT publish probe for `CodexFlow.QueryRuntime.Cli`.
- Capture the initial trim/AOT warning baseline.
- Exercise the smallest useful command set from the published binary:
  - `qre --version`
  - `qre tool list --workspace . --profile readonly --json`
  - `qre run --workspace . --response "aot smoke" --json "smoke"`
  - `qre replay latest --workspace . --json`
- Replace the reflection-based `QreModelExecutionPolicy` option copy with
  explicit trim-friendly mapping.
- Identify any `Microsoft.Extensions.AI.AIFunction` schema-generation warnings
  caused by built-in tool packs.
- Document which projects are inside the AOT CLI path and which are excluded.

Do not:

- Market AOT as supported.
- Require Docker sandbox, deterministic replay, or plugin support to be AOT-ready
  in this phase.
- Try to AOT-publish MVC, SignalR, EF-heavy, AppHost, or dashboard projects.
- Hide warnings by suppressing them without a tracking issue or explicit
  rationale.

Acceptance criteria:

```bash
dotnet publish CodexFlow.QueryRuntime.Cli \
  -c Release \
  -r osx-arm64 \
  -p:PublishAot=true \
  -p:SelfContained=true
```

- The command is run locally or in an optional CI lane.
- The result is recorded as pass/fail with a warning list.
- At least `qre --version` is attempted from the native binary when publish
  succeeds.
- Known blockers are filed into the roadmap before Phase 2 implementation
  decisions depend on reflection-heavy or dynamic-loading approaches.

Phase 1.5 first-slice progress as of 2026-06-02:

- Completed: `QreModelExecutionPolicy` no longer uses reflection to copy
  `ChatOptions` into `VllmChatOptions`. It now performs explicit
  trim-friendly mapping for stable `ChatOptions` fields and the QRE-relevant
  VLLM extension fields.
- Added regression coverage for the explicit mapping so JSON response format,
  tools, stop sequences, model id, and provider metadata survive thinking
  policy normalization without reflection.
- Ran the required local AOT probe:

```bash
dotnet publish CodexFlow.QueryRuntime.Cli \
  -c Release \
  -r osx-arm64 \
  -p:PublishAot=true \
  -p:SelfContained=true
```

- Result: failed before producing a native `qre` binary, so the native
  `qre --version` / `tool list` / `run` / `replay latest` smoke commands could
  not yet be attempted.
- Current AOT CLI path pulled by the publish command:
  `CodexFlow.QueryRuntime.Cli`,
  `CodexFlow.QueryRuntime.Experimental`,
  `CodexFlow.QueryRuntime.Abstractions`,
  `CodexFlow.QueryRuntime.Sandbox.LocalProcess`,
  `CodexFlow.Contracts`, and `CodexFlow.Core`.
- Explicitly excluded from this AOT probe: `CodexFlow` Web API, Identity/JWT,
  SignalR, EF/PostgreSQL infrastructure, Aspire AppHost, dashboard/WebUI,
  integration test projects, and MCP/plugin runtime-loading surfaces.
- Initial blocker baseline:
  - `CodexFlow.Core` still contributes broad Newtonsoft.Json trim/AOT errors
    (`JsonConvert.SerializeObject`, `JsonConvert.DeserializeObject`,
    `JToken.ToObject<T>`, `JObject.FromObject`) from orchestration, planning,
    tool normalization, trace/observation, and plan artifact services.
  - `CodexFlow.Core` still contributes reflection/dynamic-code AOT errors in
    `QueryRuntimeEngine` option normalization and tool result conversion
    (`GetProperties`, `GetProperty`, `Array.CreateInstance`,
    `MakeGenericType`).
  - `RoslynCodeAnalysisService` uses `Assembly.Location`, which is invalid for
    bundled single-file native applications.
  - System.Text.Json call sites without source-generated `JsonTypeInfo` remain
    in the transitive Core path.
- Decision before Phase 2a: do not treat these as warning suppressions. Either
  split the CLI-facing QueryRuntime engine from platform-heavy `CodexFlow.Core`
  services, or replace the CLI path's JSON/reflection helpers with explicit
  AOT-compatible contracts before promoting AOT from probe to release goal.

Phase 1.5 second-slice progress as of 2026-06-02:

- Completed: `CodexFlow.Core.Runtime.QueryRuntimeEngine` no longer uses
  reflection or dynamic collection construction for runtime chat option
  normalization. `ChatOptions` / `VllmChatOptions` copying and retry option
  overrides now use explicit mappings for the CLI-relevant fields.
- Completed: `QueryRuntimeEngine` no longer uses `JToken.FromObject`,
  `JObject.FromObject`, or reflection-based metadata conversion in the
  repeated-tool signature, hashline metadata check, and legacy tool-call
  fingerprint paths. It now uses deterministic AOT-friendly string builders and
  supports metadata as `JObject`, dictionaries, or `JsonElement`.
- Completed: `ToolArgumentNormalizer` no longer uses
  `JObject.ToObject<Dictionary<string, object?>>`; JObject/JArray inputs are
  converted recursively without Newtonsoft reflection materialization.
- Verified that the previously recorded `QueryRuntimeEngine` AOT errors for
  option normalization and tool-result conversion no longer appear in the AOT
  publish output.
- Current remaining AOT blockers are now dominated by platform-heavy
  `CodexFlow.Core` surfaces still pulled transitively into the CLI:
  - workflow/notification/automation/plan tools using non-source-generated
    `System.Text.Json` serialization;
  - committee planning and meeting artifacts using Newtonsoft.Json;
  - `CodexOrchestrator`, `CodexSessionManager`, `DefaultCodexKernel`, and TDD
    services using Newtonsoft.Json serialization/deserialization;
  - audit and tool-result formatting helpers using reflection on arbitrary
    option/result objects.
- Decision for the next slice: prefer extracting or trimming the CLI-facing
  QueryRuntime dependency set before converting every platform tool and
  committee/orchestrator service. The AOT CLI should not require full
  SaaS/platform planning, notification, or dashboard-adjacent helpers.

Phase 1.5 third-slice progress as of 2026-06-02:

- Completed: introduced `CodexFlow.QueryRuntime.Engine` as the CLI-facing QRE
  runtime contract and loop implementation. The new project owns
  `QueryRuntimeRequest`, `QueryRuntimeResult`, model-client streaming,
  runtime events, and the minimal tool-call loop without depending on
  `CodexFlow.Core`.
- Completed: `CodexFlow.QueryRuntime.Experimental` now references
  `CodexFlow.QueryRuntime.Engine` instead of `CodexFlow.Core`. The experimental
  harness no longer constructs Core `QueryRuntimeEngine`, Core recovery
  policies, Core telemetry hooks, or Core required-tool contracts.
- Completed: `CodexFlow.QueryRuntime.Cli` no longer imports
  `CodexFlow.Core.Services.VllmChatClientFactory`. It owns a small
  `QreVllmChatClientFactory` for provider selection on the CLI path.
- Completed: migrated direct provider client references to the renamed
  `VllmChatClient` package at `2.0.21`, which removes the previously observed
  Newtonsoft.Json transitive warning from the native QRE publish path and fixes
  the Anthropic Messages thinking-off request path.
- Completed: QRE CLI/trace JSON output now uses source-generated
  `System.Text.Json` contexts instead of reflection-based serialization.
- Verified that CLI/Experimental/Engine contain no `CodexFlow.Core`,
  `CoreQueryRuntime`, `ILLMExecutor`, or `LLMExecutionRequest` references.
- Added post-review regression coverage for mixed assistant text plus tool
  calls, required-tool mode clearing after the required tool succeeds, and Core
  stop-hook continuation behavior.
- Verified:

```bash
dotnet test CodexFlow.QueryRuntime.slnx
dotnet publish CodexFlow.QueryRuntime.Cli \
  -c Release \
  -r osx-arm64 \
  -p:PublishAot=true \
  -p:SelfContained=true
export PATH="$PWD/CodexFlow.QueryRuntime.Cli/bin/Release/net10.0/osx-arm64/publish:$PATH"
qre --version
qre run --workspace . --response "aot final smoke" --json "analyze this repo"
qre tool list --workspace . --profile readonly --json
qre trace latest --workspace . --json
qre replay latest --workspace . --json
```

- Result: native AOT publish now succeeds on `osx-arm64` without trim/AOT
  warnings, and the published native `qre` binary runs the required offline
  smoke commands.
- Architectural decision for the next slice: reverse the dependency direction.
  `CodexFlow.Core` should consume QRE through an adapter once the QRE engine
  grows enough runtime behavior; QRE must not consume platform Core services.

Phase 1.6 first-slice progress as of 2026-06-02:

- Started the reverse-dependency migration by adding a Core-side
  `QreBackedQueryRuntimeEngine` adapter. It implements
  `CodexFlow.Core.Runtime.IQueryRuntimeEngine` while delegating the model/tool
  loop to `CodexFlow.QueryRuntime.Engine.QueryRuntimeEngine`.
- `CodexFlow.Core` now references `CodexFlow.QueryRuntime.Engine`; the
  dependency direction is Core -> QRE Engine, not QRE -> Core.
- Scope of the first adapter is intentionally narrow: messages, options,
  available tools, required-tool selection, basic model streaming, result
  mapping, and event-sink mapping. Platform-specific session memory,
  intervention hooks, context-window governance, and advanced recovery remain
  on the legacy Core engine until they are converted into explicit QRE
  adapters.
- Added focused Core tests proving the adapter can run a no-tool round and a
  tool round through the Core runtime interface.

Estimated effort: 1-2 weeks after Phase 1.

### Phase 2a: Capability Policy Migration

Goal: replace broad command allowlists with explicit capability contracts before
claiming sandboxed execution is safe.

Do:

- Implement concrete command capability schema.
- Keep legacy `CommandExecutionPolicy` and the new capability contract in
  dual-mode during migration.
- Require explicit approval for:
  - `npm install`
  - `pip install`
  - `dotnet restore`
  - `git push`
  - `rm`
  - `git reset`
  - package publish
  - deploy commands
- Add `docs/threat-model.md`.
- Add `docs/tool-capabilities.md`.
- Add negative policy tests for every profile.

Do not:

- Treat command-name allowlists as sufficient policy.
- Allow package installation, network access, git push, or destructive commands
  in default profiles.

Acceptance criteria:

```bash
qre sandbox exec --profile readonly -- rg "TODO"
qre sandbox exec --profile readonly -- sh -c "echo x > denied.txt"
qre sandbox exec --profile verify -- git push
```

- `readonly` profile cannot write workspace files.
- `verify` profile cannot push, publish, install packages, or run destructive
  commands without explicit approval.
- Blocked commands produce policy-denied trace events.

Phase 2a first-slice progress as of 2026-06-02:

- Added command-level capability constants and an experimental classifier for
  workspace reads/writes, network access, package install/restore/publish, git
  push, Git repository writes, destructive commands, deploy commands, arbitrary
  execution, and unknown processes.
- Updated `ExperimentalCapabilityPolicy` so `readonly` can run classified
  read-only commands such as `rg`, while workspace writes are denied before
  process execution.
- Updated `qre sandbox exec` so policy-gated restricted commands such as
  `git push`, `npm install`, `pip install`, `dotnet restore`, `rm`,
  `git reset`, package publish, and deploy commands produce JSON/trace policy
  decisions instead of failing early as unregistered commands.
- Added `docs/threat-model.md` and `docs/tool-capabilities.md` for the current
  local-runner threat model and command capability contract.
- Added negative policy and CLI tests for readonly write denial and verify
  approval-gated restricted commands, including shell-wrapper bypass attempts,
  package-manager aliases, zero-argument package manager installs, `dotnet run`,
  and Git repository writes.
- Added an explicit CLI approval path with `--approve-risk <reason>` for
  known restricted `verify` commands. Unknown commands remain denied even when
  approval is supplied.
- Added migration parity tests against Core's legacy
  `CommandExecutionPolicy.VerifyWorker` denied subcommands. QRE does not depend
  on Core at runtime, but the test suite prevents legacy-denied verify commands
  from becoming silently allowed in the capability policy.
- Completed negative policy coverage for `none`, `readonly`, `verify`, and
  `repair` profiles.
- Added explicit blocked trace records: `policy.denied` and
  `policy.approval_required`, in addition to the structured `policy.decision`
  event.

Estimated effort: 4-6 weeks after Phase 1.

### Phase 2b-MVP: Docker Sandbox Runner

Goal: ship a real Docker execution path without overclaiming hostile
multi-tenant security.

Do:

- Implement `DockerSandboxRunner`.
- Support a workspace mount or simple copy-in/copy-out model.
- Disable network by default.
- Enforce basic resource limits:
  - timeout
  - memory
  - CPU
  - output size
- Ensure host credential stores and Docker socket are not mounted.
- Add Linux CI coverage for the Docker path.

Do not:

- Claim gVisor/Kata/Firecracker-level isolation.
- Pass model/provider credentials into sandbox environments by default.
- Require Docker mode for Phase 1 local productivity workflows.

Acceptance criteria:

```bash
qre run --runner local --workspace ./some-repo --profile verify "run tests"
qre run --runner docker --workspace ./some-repo --profile verify "run tests"
qre sandbox exec --runner docker --profile readonly -- rg "TODO"
```

- Docker mode cannot read common host secret locations.
- Docker mode defaults to no network.
- Docker mode enforces timeout and output limits.
- Docker and local runner traces use the same event schema.

Phase 2b-MVP first-slice progress as of 2026-06-02:

- Added `CodexFlow.QueryRuntime.Sandbox.Docker` with `DockerSandboxRunner`,
  selected through `qre sandbox exec --runner docker` and `qre run --runner
  docker` for verify-tool process execution.
- Docker jobs use a direct workspace bind mount, default to `--network none`,
  set timeout/output limits through the existing sandbox contract, and pass
  memory/CPU limits to `docker run`.
- Docker jobs do not mount host credential directories or the Docker socket;
  only the requested workspace path is mounted into `/workspace`.
- Added `runner` to `qre sandbox exec` JSON and sandbox trace records so local
  and Docker executions share the same event schema with runner metadata.
- Added non-Docker-dependent unit coverage for Docker command construction and
  CLI policy denial before Docker process execution.
- Added Linux CI smoke coverage for a real `--runner docker` readonly command
  using `alpine:3.20`.
- Added gated Docker integration tests behind
  `RUN_QUERY_RUNTIME_DOCKER_TESTS=true` for read-only workspace mount
  enforcement, common host secret path non-exposure, default network denial,
  timeout enforcement, and output-size limiting.
- Current local Docker verification result:

```bash
RUN_QUERY_RUNTIME_DOCKER_TESTS=true dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj --filter "FullyQualifiedName~DockerSandboxRunnerIntegrationTests" --logger "console;verbosity=detailed"
```

- Result as of 2026-06-02: 12 Docker sandbox integration tests passed.

Estimated effort: 2-3 weeks after Phase 2a.

### Phase 2b-Hardening: Docker Sandbox Hardening

Goal: make the Docker runner security posture credible for public docs.

Do:

- Add non-root user execution.
- Drop Linux capabilities.
- Add `no-new-privileges`.
- Add read-only root filesystem.
- Add seccomp profile support.
- Add explicit environment allowlist.
- Move from direct workspace mount to overlay or copy-in/copy-out for write
  isolation where practical.
- Add negative security tests for secret reads and network egress.

Do not:

- Treat Docker as sufficient for hostile multi-tenant SaaS.
- Skip documenting residual risks.

Acceptance criteria:

```bash
qre sandbox exec --runner docker --profile readonly -- cat /root/.ssh/id_rsa
qre sandbox exec --runner docker --profile readonly -- curl https://example.com
```

- Secret read attempts fail or return no host content.
- Network egress is denied unless an allowlist grants it.
- Sandbox configuration is visible in trace metadata.
- CI exercises negative security tests.

Phase 2b-Hardening first-slice progress as of 2026-06-02:

- Docker jobs now run as numeric non-root user `65532:65532` by default.
- Docker jobs default to `--cap-drop ALL`, `--security-opt
  no-new-privileges`, `--read-only`, and a constrained `/tmp` tmpfs mount.
- Docker jobs now preserve Docker's default seccomp profile by default instead
  of replacing it with a weaker custom profile.
- `DockerSandboxOptions` exposes `SeccompProfilePath` so callers can supply a
  custom profile when needed; when configured, the runner emits Docker
  `--security-opt seccomp=<profile>` and fails before execution if that profile
  path is missing.
- Added command-construction tests for the hardening flags and seccomp profile
  support.
- Extended the gated Docker integration suite to verify real container
  hardening signals: non-root UID, `NoNewPrivs: 1`, zero effective
  capabilities, read-only root filesystem, writable tmpfs scratch space, and
  Docker seccomp enforcement with a test profile.
- Write-capable Docker jobs now default to a staged copy-in/copy-out workspace
  instead of directly bind-mounting the host workspace as writable. The runner
  mounts the staged workspace, copies successful output changes back, excludes
  `.git` and `.qre`, and removes the staging directory after execution.
- The staged copy path skips symlinks during copy-in and copy-out so host-side
  copying does not resolve links to files outside the workspace.
- Docker jobs can mount `WorkspaceRoot` while executing in a subdirectory
  workdir, preserving access to root-level config and sibling paths.
- `qre sandbox exec` now accepts `--workspace-root` so Docker can mount a root
  workspace while executing in a subdirectory workdir.
- `qre run --runner docker` now includes Docker runner configuration in JSON
  output and appends a `runner.configuration` trace event so model-loop runs
  preserve the same hardening evidence as direct sandbox executions.
- Timeout cleanup now uses an independent short cleanup token so cancellation
  does not prevent `docker rm -f` from being sent.
- External cancellation now kills the host `docker run` process tree and then
  forces container cleanup; the Docker integration suite checks no
  `qre-sandbox-*` containers remain after cancellation.

Estimated effort: 2-3 weeks after Phase 2b-MVP.

### Phase 3: Replay and Extensibility

Goal: make the harness debuggable and extensible, not just runnable.

Do:

- Define trace and replay layout:
  - `run.json`
  - `events.jsonl`
  - `diff.patch`
  - `blobs/sha256/...`
  - `artifacts/`
- Support:
  - `qre replay latest`
  - `qre rerun latest`
  - `qre trace latest --jsonl`
- Record:
  - model requests
  - raw model responses
  - tool call arguments
  - tool outputs
  - policy decisions
  - diffs
  - budget usage
- Introduce public trace DTOs that do not leak platform-internal enums or
  concepts.
- Add deterministic ID and clock injection for recorded runs.
- Add replay-mode model adapter that consumes recorded responses.
- Add replay-mode tool coordinator that matches recorded outputs by tool name
  and normalized argument hash.
- Make replay mode avoid LLM calls and tool execution.
- Select MCP/stdio as the first out-of-process plugin model.
- Add one out-of-process demo tool.
- Add Node and Python tool packs or explicitly defer them from the public
  examples.

Do not:

- Dynamically load arbitrary DLL plugins into the AOT CLI path.
- Store multi-megabyte command output inline in JSONL.
- Call a real provider during replay mode.

Acceptance criteria:

```bash
qre replay latest
qre rerun latest
qre trace latest --jsonl
qre tool list --external
```

- Replay reproduces the recorded decision trajectory without provider calls.
- Large tool outputs are stored as content-addressed blobs.
- Third-party tools can be attached out-of-process.
- Recorded runs are useful enough to debug model/tool failures.

Estimated effort: 5-7 weeks after Phase 2b-Hardening.

Phase 3 first-slice progress as of 2026-06-02:

- Completed: `qre trace latest --jsonl` streams the latest run as one public
  JSON event envelope per line. The command reads the existing local JSONL
  trace only; it does not call a provider or execute tools.
- Completed: `qre tool list --external` discovers `.qre/tools/*.json` stdio /
  MCP-stdio manifests as external tool descriptors without starting external
  processes or dynamically loading plugin assemblies.
- Completed: `qre rerun latest` reuses the latest recorded prompt and manifest
  tool profile, then delegates to the existing `run` path. `--response` keeps
  rerun smoke tests deterministic and provider-free.
- Completed: model responses now record replay-consumable assistant text and
  structured tool-call snapshots. `qre replay latest` reports a strict replay
  decision trajectory without calling providers or executing tools, and marks
  older incomplete traces as non-strict.
- Completed: large model/tool text payloads are stored under
  `.qre/runs/<run-id>/blobs/sha256/...` with digest metadata in `events.jsonl`
  instead of embedding multi-kilobyte output inline.
- Completed: tool-call arguments are normalized into stable SHA-256 hashes in
  trace records, and replay trajectory exposes the recorded hash for matching
  tool results later.
- Completed: `RecordedReplayModelClient` consumes recorded model responses from
  JSONL traces, and `RecordedReplayToolPack` substitutes recorded tool outputs
  by `toolName + argumentHash` without invoking the original tools.
- Completed: `qre replay latest` now executes the recorded replay path by
  default; `--summary` keeps the non-executing trace summary view available.
- Completed: run directories now include both `manifest.json` and `run.json`
  metadata plus an `artifacts/` directory, aligning the public Phase 3 layout
  with the existing manifest-based implementation.
- Completed: run finalization writes `diff.patch`, `usage.json`, and a
  `budget.usage` trace record. `diff.patch` is generated through a temporary
  Git index, capturing staged changes, unstaged tracked changes, and untracked
  non-`.qre` files as one workspace-state patch without mutating the real Git
  index. Token usage is estimated from recorded prompt, assistant, and
  tool-output character counts when provider token accounting is unavailable.
- Completed: `.qre/tools/*.json` manifests can execute real out-of-process
  `stdio` tools and minimal `mcp-stdio` JSON-RPC `tools/call` tools when
  `qre run --external` is used. External processes are killed on timeout or
  cancellation, and stdout/stderr are drained through bounded buffers.
- Completed: the external tool AOT schema strategy is manifest-first. Tool
  `inputSchema` is read from JSON manifests and exposed by an explicit
  `AIFunction` implementation, avoiding delegate reflection for external
  tool schemas in the Native AOT CLI path.
- Still pending: provider-native token accounting and richer MCP lifecycle
  negotiation beyond one-shot stdio `tools/call`.

### Phase 3.5: AOT Hardening and Release Candidate

Goal: turn the Phase 1.5 probe into a blocking release-quality Native AOT path.

Do:

- Resolve or explicitly approve every warning captured by Phase 1.5.
- Migrate stable public runtime DTOs to source-generated `System.Text.Json`.
- Remove `Newtonsoft.Json` from runtime and tool-pack IO boundaries.
- Audit reflection and dynamic-code use in the CLI path after replay, sandbox,
  and out-of-process plugin protocols are in place.
- Convert the AOT publish probe into blocking CI for the CLI.
- Add runtime identifier coverage for the first supported platforms.

Do not:

- Try to AOT-publish MVC, SignalR, EF-heavy, or dashboard projects.
- Treat "builds once locally" as AOT support.
- Re-open runtime DLL plugin loading for the official AOT CLI.

Acceptance criteria:

```bash
dotnet publish CodexFlow.QueryRuntime.Cli -c Release -p:PublishAot=true
qre --version
```

- AOT publish succeeds in CI.
- No trim/AOT warnings are allowed without explicit approval.
- The AOT CLI can run at least `qre --version`, `qre tool list`, and one
  recorded replay smoke test.

Estimated effort: 3-5 weeks after Phase 3, assuming Phase 1.5 already exposed
the major blockers.

### Phase 4: AOT and Open Source Release

Goal: prepare a public release with credible distribution, documentation, and
repository hygiene.

Do:

- Initialize the public extraction repository at the start of this phase from
  the `CodexFlow.QueryRuntime.*` subtree or selected history.
- Scrub runtime artifacts and secrets from extraction history.
- Publish signed single-binary releases.
- Rewrite README around the harness positioning.
- Add examples for .NET, Node, and Python.
- Add CI for:
  - build
  - tests
  - `qre` smoke tests
  - Docker sandbox tests
  - AOT publish
- Add open-source release docs:
  - `SECURITY.md`
  - `CONTRIBUTING.md`
  - `CODE_OF_CONDUCT.md`
  - license and attribution review

Do not:

- Ship public releases with unclear third-party code provenance.
- Claim support for unimplemented providers or sandbox levels.
- Enable telemetry by default.

Acceptance criteria:

```bash
qre --version
qre init
qre run --workspace ./examples/dotnet-fix-tests "fix failing tests"
qre replay latest
```

- The CLI works on a clean machine without a local .NET runtime if distributed
  as an AOT binary.
- Public docs explain security boundaries and limitations.
- Secret scans and license scans pass.
- Release artifacts include checksums and signatures.

Estimated effort: 3-4 weeks after Phase 3.5.

## Migration From Current Repository

Valuable components to extract:

- `CodexFlow.Core/Runtime/QueryRuntimeEngine.cs`
- Tool call parser and syntax recovery.
- Tool registry and tool descriptors.
- Command execution policy primitives.
- Hashline/file edit services: hash-anchored file editing and audit-friendly
  patch application.
- Planning artifact model ideas.
- Event stream and task progress model.
- Background checkpoint concepts.

Components to isolate behind optional packages:

- ASP.NET controllers and SignalR hubs.
- Identity and user management.
- Redis/PostgreSQL/MongoDB/Qdrant integrations.
- Notification system.
- Committee planning roles.
- Semantic recall stack.

Pre-extraction cleanup:

- Rename public namespaces and package names into the `CodexFlow.*` namespace and
  package family.
- Remove legacy brand strings in tool descriptions and examples.
- Move runtime workspaces outside the API project tree.
- Ensure `workspaces/`, logs, temporary reports, generated artifacts, and local
  scratch files are ignored.
- Create a sanitized extraction repository and scrub runtime artifacts from its
  history.
- Revoke and remove all leaked credentials.
- Run secret scanning and license scanning against full history.

Components to remove or sanitize before open source:

- Real secrets and private service endpoints.
- Default admin credentials.
- GitHub personal access tokens.
- Local logs and generated artifacts.
- Private or license-unclear third-party source snapshots.
- Environment-specific scripts.

## Open Source Readiness Gates

Before publishing:

- Secret scan returns clean across repository history.
- All leaked credentials are revoked.
- License and third-party attribution are reviewed.
- Public namespaces and package names are consistently `CodexFlow.*`.
- Runtime workspaces are outside source directories and ignored.
- `dotnet test CodexFlow.slnx` has a clean baseline or CI uses a documented
  stable subset while flaky/private-service tests are explicitly marked.
- `qre` can run a local no-network example without external services.
- LocalProcess mode is documented as non-secure trusted development.
- Docker sandbox profile is documented and tested before claiming sandboxed
  execution as a default.
- Threat model, capability schema, replay format, and CLI docs exist.
- `SECURITY.md`, `CONTRIBUTING.md`, and `CODE_OF_CONDUCT.md` exist.
- README avoids overclaiming maturity and clearly states security boundaries.
- Telemetry is disabled by default.
- Release artifacts have checksums and signatures.

Recommended governance:

- Prefer Apache-2.0 for explicit patent grant unless there is a strong reason to
  stay MIT.
- Use DCO for contributions unless a CLA is required later.
- Define SemVer expectations and the v1.0 compatibility bar.
- Publish a security disclosure address and response window.

## Narrative

Avoid "Level 9 autonomous coding platform" as the open-source headline. Prefer:

> A composable runtime harness for building, testing, and debugging coding
> agents with explicit capability and sandbox surfaces.

This makes the project useful even when users disagree with the built-in UI,
model provider, planning strategy, or deployment stack.

The launch message should be honest:

- v0.1 is a useful local harness.
- v0.2 is the first security-credible sandbox release.
- v0.3 is the AOT and plugin ecosystem release.

Credibility matters more than broad claims. The project can still be ambitious,
but every README claim should map to either working code, a documented
experimental flag, or a clearly labeled roadmap item.
