> **English** | [简体中文](queryruntime-technical-guide.zh-CN.md)

# CodexFlow QueryRuntime Technical Guide

## 1. Positioning

CodexFlow QueryRuntime is a runtime harness aimed at agent development. It is not
another complete AI coding application, nor a platform that must be bound to a web
UI, an account system, a database, and a SaaS deployment shape. Its core goal is to
extract the agent infrastructure — "model calls, tool calls, execution policy,
trace, replay, sandbox, CLI automation" — into a runtime that is embeddable,
testable, extensible, and cross-platform shippable.

The implementation on the current branch is still experimental, but already has a
minimal slice that can validate the direction:

- `CodexFlow.QueryRuntime.Abstractions`: the first batch of Phase 1 stable
  contracts, including runtime, model, tool registry, trace store, sandbox runner,
  and CLI option DTOs.
- `CodexFlow.QueryRuntime.Experimental`: a lightweight wrapper over the existing
  `QueryRuntimeEngine`.
- `CodexFlow.QueryRuntime.Cli`: the experimental `qre` CLI; the docs use `qre ...`
  as the main entry point, and `osx-arm64` Native AOT publish of the local binary
  has been validated on this branch.
- `CodexFlow.QueryRuntime.Sandbox.LocalProcess`: the `ISandboxRunner`
  implementation for trusted local development — not a security isolation boundary.
- Run artifacts: each run writes `.qre/runs/<run-id>/events.jsonl`,
  `manifest.json`, `run.json`, `diff.patch`, `usage.json`, and `artifacts/`. Large
  payloads spill to `blobs/sha256/...`, keeping only digest metadata in the trace.
- Read-only tool pack: `qre_list_files`, `qre_read_file`, `qre_search_files`.
- Verify tool pack prototype: `qre_git_status`, `qre_git_diff`, `qre_dotnet_build`,
  `qre_dotnet_test`.
- Provider adaptation: connects to several recognized model families'
  OpenAI-compatible / Responses / Anthropic Messages style interfaces via
  `Microsoft.Extensions.AI.IChatClient` and the QRE CLI's own
  `QreVllmChatClientFactory`. It is not yet a fully provider-neutral universal
  adapter.
- Default model policy: when tools are enabled or the model is asked to output
  JSON, thinking is disabled by default to improve tool-call and schema-output
  compatibility.
- `--json` machine output, `qre trace latest --jsonl`, and the read-only
  `qre replay latest --summary` mode. Default public traces are summary-only;
  recorded replay accepts only explicitly enabled full-fidelity private/sanitized traces.
- `qre diff latest` prefers the latest run's run-scoped `diff.patch`; it falls back
  to the workspace git diff only when there is no run patch.
- External tool manifest: `.qre/tools/*.json` can declare `stdio` or minimal
  `mcp-stdio` tools, entered into the tool surface via `qre run --external`.
- The publish/environment diagnostic entry points `qre --version`,
  `qre init --json`, and `qre doctor --json`.
- The policy-gated trusted-local command execution entry point
  `qre sandbox exec --profile verify`.

Status markers:

- **Today**: capabilities in the current repo that already run or have been
  validated on this branch.
- **Planned**: open-source harness target capabilities that still require later
  implementation or extraction.
- **Risk**: limitations, misuse risks, or security boundaries that must be stated
  clearly before release.

Command conventions:

- **Today**: the main command in the user-facing surface and technical docs is
  `qre ...`, including `qre run ...`, `qre trace ...`, `qre replay ...`,
  `qre sandbox exec ...`.
- When you need to regenerate the native CLI inside this repo, run `dotnet publish`
  first, then add the publish directory to `PATH` or point `QRE_BIN` at the
  generated `qre`.
- Source debugging can run the CLI project directly, but that is no longer the main
  path in this document; the technical guide and external integration examples
  should depend on the stable `qre` executable.

## 2. The Problems It Solves

Many agent projects get stuck in a similar place: the demo is easy to write, but
turning it into testable, auditable, reproducible, safely-runnable engineering
infrastructure is hard. Typical problems include:

- LLM providers differ greatly; tool calls, JSON schema, and thinking-mode behavior
  are inconsistent.
- Tool execution lacks a boundary; reading files, writing files, running commands,
  and accessing the network are often mixed together.
- After an agent run fails, there is no reproducible context — you can only guess
  from terminal logs.
- CLI automation and an in-app embedded runtime are often two separate code paths,
  hard to share.
- The abstraction boundary between local execution, a Docker sandbox, and a
  Kubernetes runner is unclear.
- There is natural tension between Native AOT publishing, a cross-platform CLI, and
  plugin loading.

QueryRuntime's value is providing a middle layer: more engineered than a "few-dozen-
line agent demo," yet lighter than a complete SaaS platform. Developers can use it
as the foundation of their own agent product, or just use `qre` as a tool for CI,
codebase analysis, tool-execution validation, and replay debugging.

## 3. Applicable Scenarios

### 3.1 Local codebase analysis

**Today**: developers can run read-only analysis in any repo via the experimental
CLI, letting the model read repo structure, search files, summarize architecture
risks, or generate migration suggestions. The currently implemented tools are
read-only, so this suits analysis-style work.

**Today**: the target command for such tasks is already `qre run --profile readonly
...`.

Suitable questions:

- "Analyze the module boundaries of this repo."
- "Find potential config leaks and security risks."
- "Explain why the test structure is hard to maintain."
- "Give a refactoring plan for the next phase."

### 3.2 Agent tool-call validation

**Today**: QueryRuntime can record model requests, model responses, tool requests,
and tool results into a unified JSONL trace, convenient for regression testing.

When the model calls tools, it is easily affected by provider format, tool schema,
and thinking mode.

Suitable questions:

- Whether a model still outputs parseable tool calls after tools are enabled.
- Whether a provider supports JSON schema / response format.
- Whether tool-call stability improves after thinking is disabled.
- Whether the runtime gives a reasonable termination reason after a tool call fails.

### 3.3 Read-only review in CI or automation scripts

**Today**: `--json` output can already be consumed by scripts, suitable for offline
smoke and read-only review. The repo already has
`.github/workflows/queryruntime-harness.yml` as a harness-only CI prototype that
validates only the QueryRuntime slice without starting platform dependencies.

`--json` output makes the CLI consumable by scripts. CI can run a single read-only
analysis, write the result to an artifact, and let another system decide later
whether to block the build.

Suitable questions:

- Run a read-only architecture review first when a Pull Request enters the queue.
- Nightly scan of the repo for dependency risk, TODOs, and test gaps.
- Upload `.qre/runs/<run-id>/events.jsonl` as a debug artifact — but redact or
  access-control it before uploading.

### 3.4 Teaching, evaluation, and replay

**Today**: the experimental CLI writes `PublicRedacted / SummaryOnly` traces by
default. `replay latest --summary` safely reads those summaries, while a recorded
replay against summary-only data fails closed. Only reviewed fixtures explicitly
written with `--trace-data sanitized`, or access-controlled diagnostics written with
`--trace-data private`, contain full-fidelity replay data. Replay does not call the
provider or execute the original tools.

The hardest thing to debug in agent development is "why did it answer this way this
time." Recorded replay reproduces a provider-free / tool-free decision trajectory,
and `replay latest --strict` adds deterministic clock + query-id injection and an
explicit trace `SchemaVersion`, producing a byte-identical canonical `replayDigest`
across repeated strict replays of the same source trace and runtime version (see
§5.8). Strict replay gates on schema version: legacy unversioned traces and
unsupported-future versions are rejected with precise reasons rather than replayed
non-deterministically.

Deterministic strict replay can be used to:

- Reproduce an agent decision trajectory.
- Compare the behavior of different runtime policies.
- Construct a public benchmark.
- Attach a redactable trace in an issue, reducing the "cannot reproduce"
  communication cost.

### 3.5 A cross-platform agent product foundation

**Planned**: only after stable NuGet packages, a standalone `qre` binary, and the
sandbox runner are complete is QueryRuntime suitable as a formal runtime dependency
of an external product.

In the target shape, QueryRuntime can be embedded in desktop apps, IDE plugins, CLI
tools, web backends, CI runners, or enterprise intranet platforms. It suits being an
"agent development component" rather than cramming all functionality into a single
app.

Typical combinations:

- Desktop app: the UI handles interaction, QueryRuntime handles the model loop and
  tool execution.
- IDE plugin: the plugin handles editor context, QueryRuntime handles trace, policy,
  and replay.
- CI service: the runner handles the job lifecycle, QueryRuntime handles analysis
  and tool execution.
- Enterprise platform: the platform handles permissions and auditing, QueryRuntime
  is the controllable execution engine.

## 4. Current Architecture

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

Key objects:

- `CodexFlow.QueryRuntime.Abstractions.IQueryRuntimeEngine`: the first batch of
  Phase 1 stable runtime contracts; the public entry point is
  `RunAsync(QueryRuntimeRequest, CancellationToken)`.
- `QueryRuntimeRequest` / `QueryRuntimeResult`: minimal request and result DTOs for
  external callers, not exposing the Core runtime's session, worker, memory, or hook
  details.
- `IModelClient`, `IToolRegistry`, `ITraceStore`, `ISandboxRunner`: the target
  public extension points; contracts exist now, but the concrete implementations are
  still being migrated in phases.
- `ExperimentalQueryRuntimeHarness`: the experimental facade, taking prompt,
  workspace, max rounds, tool list, thinking policy, and chat options; it also
  already implements the stable `IQueryRuntimeEngine` contract, used for Phase 1
  migration.
- `IExperimentalModelClient`: the model client abstraction of the current
  experimental layer.
- `ChatClientExperimentalModelClient`: adapts `IChatClient` to the experimental
  runtime.
- `StaticExperimentalModelClient`: an offline smoke-test client that does not access
  the network.
- `ExperimentalReadOnlyToolPack`: the current built-in read-only tool pack.
- `ExperimentalVerifyToolPack`: the current built-in verify tool pack, running
  `git status`, `git diff`, and `dotnet build/test --no-restore` via
  `LocalProcessSandboxRunner`.
- `ExperimentalToolRegistry`: the experimental tool registry, returning tool
  descriptions and capability metadata.
- `ExperimentalCapabilityPolicy`: the experimental capability policy, deciding
  before verify-tool execution whether profile, capabilities, command, network, and
  mount are allowed.
- `JsonlTraceStore`: the current minimal `ITraceStore` implementation, used to read
  the latest run's JSONL summary.
- `JsonlTraceEventSink`: writes runtime events as a JSONL trace.
- `QreModelExecutionPolicy`: uniformly handles the thinking policy, disabling
  thinking by default for tools / JSON output.

Current CLI configuration objects:

- `QueryRuntimeProviderOptions`: provider endpoint, key, model, api mode, or static
  response.
- `QueryRuntimeToolProfile`: the tool profile, currently supporting `none`,
  `readonly`, `verify`, and `repair`; `repair` exposes controlled workspace
  write tools with run-scoped diff artifact generation.
- `QueryRuntimeModelPolicyOptions`: the model execution policy, currently mainly the
  thinking policy.
- `QueryRuntimeOutputOptions`: distinguishes model JSON output from CLI JSON output.
- `QueryRuntimeExecutionOptions`: runtime parameters such as the number of run rounds.

These configuration objects now live in `CodexFlow.QueryRuntime.Abstractions`; the
CLI just consumes the same set of public DTOs. A future external host can reuse these
configuration objects rather than parsing the CLI's internal types.

## 5. Usage

### 5.1 Base environment

**Today**: the current repo uses `net10.0`. The main CLI entry point is `qre`; for
an AOT smoke, add the publish directory to `PATH` first, then run normal `qre ...`
commands.

The current repo uses `net10.0`. From the repo root:

```bash
dotnet --version
dotnet build CodexFlow.QueryRuntime.slnx --no-restore
```

Check the CLI version and local diagnostics:

```bash
qre --version
qre init --workspace . --json
qre doctor --workspace . --json
```

Local Native AOT publish and basic smoke:

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

The P0 baseline gate can be run uniformly via script:

```bash
scripts/queryruntime-baseline-gate.sh
scripts/queryruntime-baseline-gate.sh --full
```

The default gate runs `git diff --check` and
`dotnet test CodexFlow.QueryRuntime.slnx --no-restore`. `--full` additionally runs a
local Native AOT publish and a native `qre --version` smoke. Docker sandbox and real
provider checks stay explicitly gated:

```bash
scripts/queryruntime-baseline-gate.sh --include-docker
RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true \
  scripts/queryruntime-baseline-gate.sh --include-real-provider
```

`init` creates `.qre/config.toml` and `.qre/README.md`. The template only records
environment variable names and the local default profile; it does not write the API
key, and it does not overwrite an existing template unless `--force` is passed. At
the current stage the CLI still uses environment variables and command-line
arguments as the real provider configuration source; `.qre/config.toml` is a
workspace scaffold, not a completed config-reading path.

`doctor` does not call the model, does not run a project build, and does not read the
API key value itself. It only checks the workspace, `dotnet`, `git`, whether the
provider environment variables are complete, and whether a latest `.qre` trace
exists.

If you only want to verify the CLI and trace offline, you need no LLM key:

```bash
qre run --workspace . --response "offline smoke" "analyze this repo"
```

### 5.2 Offline smoke mode

**Today**: this is currently the most stable network-free validation method.

`--response` uses a static model response, does not access the network, and suits
validating the CLI, trace, JSON output, and script integration.

```bash
qre run --workspace . \
  --response "offline smoke" \
  --json \
  "analyze architecture risks"
```

Example output:

```json
{"type":"qre.run.completed","finalText":"offline smoke","runId":"20260602145703992","termination":"NoToolCalls","profile":"none","tools":[],"workspacePath":"/repo","traceFilePath":"/repo/.qre/runs/20260602145703992/events.jsonl","runDirectory":"/repo/.qre/runs/20260602145703992","manifestPath":"/repo/.qre/runs/20260602145703992/manifest.json","totalRounds":1,"totalToolCalls":0,"totalDurationMs":52}
```

### 5.3 Real LLM provider mode

**Today**: the CLI real-provider path creates the client via
`CodexFlow.QueryRuntime.Cli/QreVllmChatClientFactory.cs` and no longer depends on
`CodexFlow.Core`'s provider factory. This factory still recognizes model families
such as Qwen, OpenAI GPT, Gemini, Claude, Kimi, MiniMax, GLM, and DeepSeek from the
model name; unknown models currently fall back to the existing default client rather
than a strict provider-neutral adapter. So it suits this branch's spikes and
already-validated model families, and should not be advertised as a universal
provider abstraction.

**Planned**: provider-neutral configuration and concrete model adapters should later
move to a `CodexFlow.QueryRuntime.Models.*` package, and unknown providers should
fail more explicitly.

**Risk**: when using a real provider, the prompt, model context, and file contents
read by tools are sent to the endpoint you configure. Do not run real LLM analysis
on sensitive private repos before evaluating the provider/proxy data policy.

The CLI supports configuring the provider via both command-line arguments and
environment variables:

```bash
export QRE_API_URL="https://your-provider.example/v1"
export QRE_API_KEY="..."
export QRE_MODEL="your-model"
export QRE_API_MODE="chat-completions"

qre run --workspace . "summarize the repository architecture"
```

The repo also provides gated real-provider integration tests. By default the tests
skip the real model; enable explicitly when real validation is needed:

```bash
RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true dotnet test \
  CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj \
  --filter "FullyQualifiedName~ExperimentalHarnessRealLlmPhaseTests" \
  --logger "console;verbosity=detailed"
```

2026-06-03 validation result on this branch: all 5
`ExperimentalHarnessRealLlmPhaseTests` passed, using the project appsettings'
`deepseek-v4-pro` / `AnthropicMessages` configuration, covering provider streaming,
Anthropic Messages thinking-off, no-tool trace, and readonly tool calls.

Native AOT `qre` real-provider smoke has been validated on two paths:

- Anthropic Messages compatible endpoint, validated via `VllmChatClient` 2.0.21:
  `ThinkingEnabled=false` sends `thinking: { "type": "disabled" }`, and the real
  smoke trace of QRE `--thinking off` contains only fixed assistant text.
- OpenAI-compatible `chat-completions` endpoint, validated that `--thinking off`
  does not leak thinking text, and `ThinkingTextLength` in the trace is `null`.

Native AOT + OpenAI-compatible smoke example:

```bash
export QRE_API_URL="https://dashscope.aliyuncs.com/compatible-mode/v1"
export QRE_API_KEY="..."
export QRE_MODEL="deepseek-v4-pro"
export QRE_API_MODE="chat-completions"

qre run --workspace /tmp/qre-smoke \
  --profile none \
  --thinking off \
  --json \
  "Output exactly the following fixed text and nothing else: OPENAI_COMPAT_OK"
```

The equivalent command-line arguments:

```bash
qre run --workspace . \
  --api-url "https://your-provider.example/v1" \
  --api-key "$QRE_API_KEY" \
  --model "your-model" \
  --api-mode "chat-completions" \
  "summarize the repository architecture"
```

Currently `--api-mode` is mainly used to select the call style of the existing
provider factory, but it does not eliminate differences between model-family
clients. Common values include:

- `chat-completions`
- `responses`
- `anthropic-messages`

### 5.4 Enabling read-only tools

The current `readonly` profile contains three tools:

- `qre_list_files`
- `qre_read_file`
- `qre_search_files`

The current `verify` profile includes the readonly tools and additionally provides:

- `qre_git_status`
- `qre_git_diff`
- `qre_dotnet_build`
- `qre_dotnet_test`

The `verify` profile is still trusted local execution, not a Docker sandbox. Its
default build/test commands use `--no-restore` to avoid implicitly triggering
restore/network/package-script behavior at this stage.

Verify tools pass through `ExperimentalCapabilityPolicy` before execution:

- `qre_git_status` can only run `git status --short`.
- `qre_git_diff` can only run `git diff ...`.
- `qre_dotnet_test` can only run `dotnet test ... --no-restore`.
- `qre_dotnet_build` can only run `dotnet build ... --no-restore`.
- The network policy must be `deny`.
- The `readonly` profile does not allow process execution.
- `repair` exposes controlled file tools (`qre_write_file`, `qre_apply_patch`)
  rather than arbitrary shell execution. Those tools use canonical workspace path
  checks, reject symlink escape, deny `.git` / `.qre` and secret-looking paths,
  require a read-write workspace mount in policy, and emit `policy.decision`
  trace records before writing.

This is still an application-layer policy, not OS-level isolation.
`LocalProcessSandboxRunner` does not actually block a process's network access or
mount behavior; those require a Docker/Kubernetes/VM runner to become a trusted
execution boundary.

When the verify or repair profile tools are built by the harness based on the
profile, policy evaluation is written to the same
`.qre/runs/<run-id>/events.jsonl`, with event type `policy.decision`.

You can also query the policy decision directly without executing tools:

```bash
qre policy check --workspace . \
  --profile verify \
  --tool qre_dotnet_test \
  --json \
  -- dotnet test CodexFlow.QueryRuntime.slnx --no-restore
```

If you drop `--no-restore`, the current policy returns `Deny`. `policy check` itself
only means "evaluation complete," so the `allowed` / `decision` in the JSON output
is the judgment an automation system should read.

You can also directly execute a policy-restricted trusted-local command via the CLI:

```bash
qre sandbox exec --workspace . \
  --profile verify \
  --json \
  -- git status --short
```

`sandbox exec` currently does not start a shell, nor does it allow arbitrary
commands. It only maps the command to the current built-in verify tool descriptor,
then runs it through `ExperimentalCapabilityPolicy`. For example, `dotnet test`
without `--no-restore` is rejected and does not start `LocalProcessSandboxRunner`.

View tools:

```bash
qre tool list --workspace . --profile readonly --json
```

View verify tools and capability metadata:

```bash
qre tool list --workspace . --profile verify --json
```

Run read-only analysis:

```bash
qre run --workspace . \
  --profile readonly \
  --max-rounds 3 \
  "Find the most important runtime entry points and explain them."
```

Run trusted-local verify analysis:

```bash
qre run --workspace . \
  --profile verify \
  --max-rounds 4 \
  "Run the focused QueryRuntime tests and summarize failures."
```

`--tools` is still kept as a compatibility alias for `--profile`, but later docs and
public CLI semantics should prefer `--profile`. The reason is that a profile is not
just a tool set; it also carries sandbox, capability, approval, and budget policies.

### 5.5 External stdio / MCP tool manifests

**Today**: external tools follow a manifest-first, out-of-process model, and do not
dynamically load third-party DLLs into the Native AOT CLI path.

Under the workspace you can place:

```text
.qre/tools/<tool-name>.json
```

The supported registration command copies and validates a manifest into that
workspace-local registry:

```bash
qre tool register --workspace . --manifest path/to/tool.json
```

A minimal `stdio` manifest example:

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

View external tool descriptions:

```bash
qre tool list --workspace . --profile readonly --external --json
```

Enable external tools at runtime:

```bash
qre run --workspace . --profile readonly --external "call the external tool"
```

For Python projects, ordinary functions should be adapted into this manifest
surface instead of trying to intercept tool calls outside QRE. The
`examples/PythonFunctionTools` helper provides a minimal pattern:

```python
@qre_tool(name="py_count_files", capabilities=["read_fs"])
def count_files(workspace_path: str, extension: str = ".py") -> dict[str, object]:
    ...
```

The Python script can generate one manifest per decorated function:

```bash
python examples/PythonFunctionTools/repo_tools.py --manifest-dir .qre/generated-tools
qre tool register --workspace . --manifest .qre/generated-tools/py_count_files.json
```

At runtime the model calls `py_count_files`, QRE starts the Python process over
stdio, sends `{ name, workspacePath, arguments }`, receives `{ "result": ... }`,
records the tool event in the trace, and returns the result to the model.

Two transports are currently supported:

- `stdio`: QRE starts the external process, writes `{ name, workspacePath,
  arguments }` to stdin, and reads stdout; stdout can be plain text or
  `{ "result": ... }`.
- `mcp-stdio`: QRE sends one minimal JSON-RPC `tools/call` message and parses
  `result.content[].text`.

Boundary and security semantics:

- The external tool process clears the host environment and only injects the
  SDK/CLI allowlist variables in `TrustedLocalSandboxEnvironment`; it does not pass
  through provider secrets.
- On external-process timeout or cancellation, the entire process tree is killed.
- stdout/stderr are drained in real time through a bounded buffer to avoid oversized
  tool output causing host OOM or pipe deadlock.
- `inputSchema` comes directly from the manifest and is exposed by an explicit
  `AIFunction` implementation, avoiding external tool schema depending on delegate
  reflection — consistent with the Native AOT path.
- The current `mcp-stdio` is a one-shot `tools/call` with no full `initialize`
  lifecycle negotiation; scenarios needing a stateful MCP server are still a later
  item.

### 5.6 JSON output

**Today**: `--json` and `--json-output` are already two independent switches.

There are two easily confused but must-be-distinguished switches:

- `--json`: the CLI outputs JSON, for scripts, CI, and platform integration.
- `--json-output`: requires the model to return JSON; this triggers QRE's default
  policy to disable thinking in `auto` thinking mode.

Example: CLI JSON output without requiring the model to return JSON:

```bash
qre run --workspace . --response "plain text" --json "analyze"
```

Example: require the model to return JSON:

```bash
qre run --workspace . --json-output "return a JSON summary"
```

The current `qre run` output contract:

- Without `--json`, the CLI outputs the final assistant text after the run
  completes, then outputs run metadata.
- With `--stream`, the CLI writes human-readable assistant text deltas as the
  model client produces them, then writes the same run metadata. This mode is for
  terminals and host apps, not machine parsing.
- With `--json`, stdout outputs only one `qre.run.completed` JSON object for scripts
  and CI to parse. Real-time text deltas, trace events, or progress info should not
  be mixed into this stdout contract.
- In the future, `--jsonl-stream` will be used for machine-readable event
  streaming, where each line should be explicit event-shaped JSON, e.g. containing
  event type, sequence, run id, and payload. It should not reuse `--json`'s
  final-result shape.

`--stream` cannot be combined with `--json`; the CLI fails fast instead of mixing
text deltas into the single-final-object JSON contract. `--jsonl-stream` remains
reserved and fails explicitly rather than being silently concatenated into the
prompt.

For third-party agents or desktop-app integration, `--stream` is suitable when a
human-readable terminal surface is enough. Prefer the future `--jsonl-stream` for
machine-readable progress events.

Current human-readable stream command shape:

```bash
qre run --workspace . \
  --profile readonly \
  --stream \
  "Analyze this repository and list the top risks."
```

Future JSONL event shape example:

```jsonl
{"type":"qre.run.event","eventType":"model.text.delta","seq":12,"runId":"20260603123000123","delta":"Reading repository structure..."}
{"type":"qre.run.event","eventType":"model.text.delta","seq":13,"runId":"20260603123000123","delta":" Found the main runtime projects."}
{"type":"qre.run.event","eventType":"tool.call.requested","seq":14,"runId":"20260603123000123","toolName":"qre_search_files","argumentsHash":"sha256:..."}
{"type":"qre.run.completed","finalText":"Reading repository structure... Found the main runtime projects.","runId":"20260603123000123","traceFilePath":"/repo/.qre/runs/20260603123000123/events.jsonl"}
```

A minimal .NET invocation example for the current human-readable stream:

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

Note: current `--stream` output is human-readable text plus final run metadata. Tool
calls, policy decisions, and tool results remain available through the trace. Future
machine-readable streaming should use complete event-shaped records, and a
not-yet-assembled partial tool-call payload should not be exposed to third-party
consumers.

### 5.7 Thinking policy

**Today**: `auto` is the currently recommended default policy.

Current policy:

- `--thinking auto`: the default. Disables thinking when tools or `--json-output` is
  enabled.
- `--thinking off`: force-disable thinking.
- `--thinking on`: force-enable thinking.
- `--thinking preserve`: keep the provider's / caller's original options.

Default to `auto`. Many models, during tool calls or schema output, will produce
incompatible tool JSON, schema output, or provider parameters if the thinking
channel is not properly isolated. So QRE makes disabling thinking the default safe
policy for tool calls and structured output.

This policy comes from real provider validation: the current `deepseek-v4-pro`
Anthropic Messages endpoint rejects required/object `tool_choice` in thinking mode.
So QRE prefers provider auto-tool mode for tool calls and JSON/schema-constrained
output, disabling thinking by default to avoid leaking provider-specific
restrictions to upper-layer callers.

### 5.8 Trace, run artifacts, and replay

**Today**: the trace is written as JSONL with explicit, durable `SchemaVersion`,
`DataMode`, and `ReplayCapability` fields. The default is
`PublicRedacted / SummaryOnly`; it does not persist prompts, model text, tool
arguments/results, stdout/stderr, or payload blobs. `--summary` reads default traces;
recorded and strict replay accept only explicitly full-fidelity traces and otherwise
fail closed. Public traces also replace the host `RunId` and run-directory name with
an unlinkable `public-<uuid>` id and redact `QueryId`.

Each `qre run` writes under the workspace:

```text
.qre/runs/<run-id>/events.jsonl
.qre/runs/<run-id>/manifest.json
.qre/runs/<run-id>/run.json
.qre/runs/<run-id>/diff.patch
.qre/runs/<run-id>/usage.json
.qre/runs/<run-id>/artifacts/
.qre/runs/<run-id>/blobs/sha256/...
.qre/private/runs/<private-id>/...  # owner-only PrivateDiagnostic, 7-day default retention
```

Private diagnostic directories use protected current-user ACLs on Windows and
`0700` directories / `0600` files on Unix. `PrivateDiagnosticRetention` may shorten
retention or extend it up to the enforced 30-day maximum. It does not add encryption.

View the latest trace:

```bash
qre trace latest --workspace . --json

qre trace latest --workspace . --jsonl
```

Create a reviewed sanitized fixture and execute its recorded replay:

```bash
qre run --workspace . --trace-data sanitized --response "offline smoke" "analyze this repo"
qre replay latest --workspace . --json
```

Read the latest run's read-only summary:

```bash
qre replay latest --workspace . --summary --json
```

Current replay-completed output example:

```json
{"type":"qre.replay.completed","finalText":"offline smoke","runId":"20260603043913655","termination":"NoToolCalls","profile":"none","runner":"recorded-replay","tools":[],"workspacePath":"/repo","traceFilePath":"/repo/.qre/runs/20260603043913655/events.jsonl","runDirectory":"/repo/.qre/runs/20260603043913655","manifestPath":"/repo/.qre/runs/20260603043913655/manifest.json","totalRounds":1,"totalToolCalls":0,"totalDurationMs":0}
```

Core mechanism of recorded replay:

- `RecordedReplayModelClient` reads recorded assistant text and structured tool-call
  snapshots from the JSONL.
- `RecordedReplayToolPack` matches by `toolName + normalized argument hash` and
  returns recorded tool results, without calling the original tools.
- When a large model response or tool output exceeds the inline threshold, it spills
  to `blobs/sha256/...`, keeping digest, size, and length metadata in the trace.
- `replay latest --summary` is still usable for a quick trace summary without
  executing the runtime.

#### Strict deterministic replay (`--strict`)

`qre replay latest --workspace . --strict --json` runs the recorded replay with a
deterministic clock and query-id injected into the engine, seeded from the source
trace. Two strict replays of the same source trace and runtime version therefore
produce a **byte-identical canonical event projection**, surfaced as a stable
`replayDigest` (SHA-256 over the engine event records: `Type`, `Seq`,
`RuntimeEventType`, deterministic `QueryId`, deterministic `Timestamp`, and `Data`).
The digest deliberately excludes run-scoped `RunId`/`SessionId`, so it is stable
across runs.

Strict replay output example:

```json
{"type":"qre.replay.completed","mode":"strict-replay","finalText":"offline smoke","sourceRunId":"20260603043913655","runId":"20260604044405928","termination":"NoToolCalls","profile":"none","schemaVersion":1,"replayDigest":"fc0a93aab02c…","providerCalls":false,"toolExecutions":false,"tools":[],"workspacePath":"/repo","traceFilePath":"/repo/.qre/runs/20260604044405928/events.jsonl","runDirectory":"/repo/.qre/runs/20260604044405928","manifestPath":"/repo/.qre/runs/20260604044405928/manifest.json","totalRounds":1,"totalToolCalls":0,"totalDurationMs":1}
```

##### Trace schema versioning and compatibility

The trace format carries an explicit, durable `SchemaVersion` on the `run.started`
record and in `manifest.json` (current version `1`, the first public,
deterministically-replayable format). Strict replay gates on this version:

- A trace at the current version replays strictly.
- A trace with **no** recorded `SchemaVersion` is treated as legacy version `0`
  (pre-public) and is rejected from strict replay with a precise reason
  (`strict replay requires schema version >= 1; trace has no recorded schema
  version (pre-public legacy trace)…`). Such traces remain usable via non-strict
  recorded replay.
- A trace recorded at a version **newer** than the runtime supports is rejected
  with `unsupported trace schema version N (runtime supports up to M)…`.

`replay latest --summary` reports `schemaVersion`, `strictReplayCompatible`, and,
when blocked, `strictReplayBlockedReason`.

##### Replay guarantees and non-guarantees

Guaranteed by strict replay:

- No provider is called: the model client is `RecordedReplayModelClient`, which only
  dequeues recorded assistant text and tool-call snapshots.
- No original tool executes: tools come from `RecordedReplayToolPack`, which returns
  recorded results keyed by `toolName + normalized argument hash`.
- Deterministic clock and query id, hence byte-identical `replayDigest` across
  repeated strict replays of the same source trace and runtime version.

Not guaranteed:

- The on-disk replay run directory (`RunId`, `SessionId`, envelope `run.started`/
  `run.completed` wall-clock timestamps) is not byte-identical — only the canonical
  engine projection / `replayDigest` is. Run-scoping is intentionally excluded.
- Cross-runtime-version determinism: a different runtime version may legitimately
  change the canonical projection.
- Live behavior: see live rerun below.

##### Live rerun is separate from strict replay

`qre rerun latest` is a **live rerun**, not a strict replay: it re-executes the
runtime with a fresh response/clock and may legitimately differ from the source run
whenever sandbox commands depend on the clock, filesystem, network, or host state.
Strict replay (`replay latest --strict`) is the deterministic, provider-free /
tool-free path; live rerun is the non-deterministic re-execution path. Do not treat
live rerun output as a determinism guarantee.

`manifest.json` is the Phase 1 run-artifact index, intended to let the CLI, CI,
desktop, or other platforms locate runId, the run directory, the trace file, the
profile, and the termination status without parsing the full JSONL. It is not a
security audit summary, nor a replacement for the raw trace.

#### v2 C6 versioned audit and data-only replay

`--runtime v2` uses a separate C6 audit schema; it neither reuses nor replaces
the v1 trace. The default `.qre/v2/runs/<public-id>/audit.v1.jsonl` contains an
explicit allow-list `PublicRedacted / SummaryOnly` projection. Prompts, model or
reasoning text, tool names/arguments/results, paths, and host IDs are not persisted.
`--trace-data private` writes owner-only data under `.qre/v2/private/runs`, while
`--trace-data sanitized` is for reviewed fixtures; both are marked `Recorded`.

```bash
qre run --runtime v2 --workspace . --trace-data sanitized \
  --response "offline v2" --json "audit this runtime"
qre replay latest --runtime v2 --workspace . --summary --json
qre replay latest --runtime v2 --workspace . --strict --json
```

The v2 replay is a data-only validation reducer: its API accepts no model client,
provider, or tool executor. It validates schema, contiguous sequence,
causation/correlation, kind/payload/identity shape, model request/response pairs,
tool-observation order, terminal text/usage/history, and manifest/file/blob path,
length, SHA-256, and quota consistency. `providerCalls` and `toolExecutions` are
always `false`. `--strict` requests complete trajectory validation and a stable
`replayDigest`; it is not a live rerun and provides no crash-resume or exactly-once
guarantee.

`RuntimeAuditStoreOptions` bounds retention (at most 30 days), run count, all-run
storage, per-run bytes, event count, JSON line/depth, and individual/aggregate blob
bytes. Single-process writers share the total storage quota. Write failures use
either `FailClosed` (default) or `BestEffort` with explicit warnings, and failed runs
are eligible for terminal-only GC. Unknown/future schemas, non-terminal runs, public
summaries, and any integrity conflict are rejected for replay.

### 5.9 Diff output

**Today**: `diff latest` prefers the latest run's run-scoped `diff.patch`. It falls
back to the current workspace git diff only when there is no run patch.

The current CLI can read the latest run's patch:

```bash
qre diff latest --workspace . --json
```

You can also view only stats:

```bash
qre diff latest --workspace . --stat --json
```

The `mode` in the current output is usually `run-diff-patch`, meaning "read the
`.qre/runs/<run-id>/diff.patch` written at the end of the latest run." This patch is
generated through a temporary Git index:

- Covers staged changes.
- Covers unstaged tracked changes.
- Covers deleted files.
- Covers untracked non-`.qre` files.
- Does not modify the real `.git/index`.
- When a file has both staged and unstaged modifications, the patch represents the
  final workspace state.
- For repair runs, the run patch is narrowed to paths recorded in
  `repair-edits.txt`. If one of those same paths already had uncommitted changes
  before the run, the current pre-release behavior is to emit the full `HEAD` to
  final-state diff for that file, including the pre-existing same-file delta.

If the current workspace is not a Git repo, or the latest run has no `diff.patch`,
the CLI falls back to `workspace-git-diff` mode. `--stat` currently still reads the
current workspace's Git stat, not the run-scoped patch stat.

### 5.10 Usage output

**Today**: each run writes estimated usage, not provider-native billing fact.

At run end it writes:

```text
.qre/runs/<run-id>/usage.json
```

It also appends a `budget.usage` trace event. Current fields include:

- prompt chars / estimated prompt tokens
- assistant chars / estimated completion tokens
- tool output chars / estimated tool output tokens
- total tokens
- total rounds / total tool calls / total duration
- `estimated: true`

The estimation rule is `ceil(chars / 4.0)`. When the provider later exposes stable
token accounting, the usage contract can extend provider-native token and cost
fields; for now `usage.json` should not be treated as a billing basis.

**Risk**: `events.jsonl` may contain prompts, model responses, tool arguments, tool
results, and the contents of files that were read. In a real run, it may contain
private code, config fragments, or secret-shaped strings. The current repo
`.gitignore` already includes:

```gitignore
.qre/
```

If you dogfood QRE in other repos, add the same ignore rule. Before CI uploads a
`.qre/` artifact, redact or restrict access; do not attach a raw trace as a public
issue attachment.

## 6. Cross-Platform Development Application Example

**Today**: for early integration, prefer invoking the `qre` CLI as a subprocess and
parsing the last line of `--json`. External apps should not depend on the repo's
source paths or project startup commands.

The example below shows how to write a cross-platform .NET console app that invokes
`qre` as a local agent-runtime CLI. This approach suits early integration because it
does not require the external app to reference QueryRuntime's internal assemblies
directly; after a stable public API is later released, you can switch to in-process
embedding.

### 6.1 Example goal

Build a simple command:

```bash
RepoDoctor /path/to/repo
```

It will:

1. Call `qre run --profile readonly --json` to analyze the repo.
2. Parse the JSON output.
3. Print the final text and trace path.
4. Use the same C# code on Windows, macOS, and Linux.

### 6.2 Create the project

A complete example is in the repo:

```text
examples/RepoDoctor/
```

The snippets below illustrate the core structure; when maintaining, treat the actual
code in `examples/RepoDoctor` as authoritative.

```bash
dotnet new console -n RepoDoctor
cd RepoDoctor
```

Before running the example, ensure the `qre` executable is on `PATH`. Inside the
repo you can produce a local binary via Native AOT publish:

```bash
dotnet publish CodexFlow.QueryRuntime.Cli \
  -c Release \
  -r osx-arm64 \
  -p:PublishAot=true \
  -p:SelfContained=true
export PATH="$PWD/CodexFlow.QueryRuntime.Cli/bin/Release/net10.0/osx-arm64/publish:$PATH"
```

### 6.3 Example code

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

### 6.4 Run the example

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

For offline smoke mode, change `--profile readonly` to `--response "offline smoke"`
to verify that your cross-platform app can correctly parse the `qre` output.

### 6.5 How external apps invoke it

The current example already uses the `qre` executable; external apps can rely on
`PATH` lookup, or use `QRE_BIN` to point at an explicit CLI path.

```bash
qre run --workspace . --profile readonly --json "Analyze this repository."
qre replay latest --workspace . --json
```

For external apps, this is an important change: the integration layer should not
depend on the repo's source paths, but on the stable `qre` executable or the stable
`CodexFlow.QueryRuntime.*` NuGet packages.

## 7. Sandbox Direction

**Today**: there are now two paths, a trusted-local runner and a Docker runner.
`LocalProcessSandboxRunner` targets trusted local development; `DockerSandboxRunner`
is the Phase 2b container isolation implementation, used to validate stronger file
system, network, user, capability, and cleanup boundaries.

`LocalProcessSandboxRunner` does not enforce mount policy, Linux capabilities,
seccomp, or copy-in/copy-out workspace isolation. `SandboxJobSpec.Network` and
`SandboxJobSpec.Mounts` are advisory contracts for LocalProcess: LocalProcess
defensively rejects `Network.Allow`, but `Network.Deny` cannot block a child process
from initiating network access at the OS level, and `WorkspaceReadOnly` cannot block
writes at the OS level.

LocalProcess clears the child-process environment by default and only injects the
variables explicitly provided in `SandboxJobSpec.Environment`. The Phase 1 verify
tools and `qre sandbox exec` use `TrustedLocalSandboxEnvironment` to inject the
local allowlist variables the SDK/CLI needs, such as `PATH`, `HOME`, `TMPDIR`,
`DOTNET_ROOT`, and Windows shell/path variables; it does not pass through arbitrary
host environment variables or provider secrets. This still does not replace
OS/container-level credential isolation. Its value is letting the upper layers depend
on the `ISandboxRunner` contract first, leaving a replacement point for a later
Docker runner.

`qre sandbox exec` is a low-level trusted-local execution entry point. It only maps
to a verify tool descriptor based on command shape and passes through the capability
policy; it does not perform all of the argument-level workspace-path normalization in
the verify tool pack. For example, `qre_git_diff(path)` first resolves the path
inside the workspace, while `sandbox exec -- git diff ...` executes with the raw
arguments the user passed. Phase 1 already writes the started, policy-decision, and
completed events of `sandbox exec` into `.qre/runs/<run-id>/events.jsonl` for
auditing, but this is still not an untrusted-command execution boundary.

The Docker runner currently already covers:

- Read-only workspace mount.
- Write-capable jobs use staged copy-in/copy-out rather than a direct writable host
  bind.
- Default network deny.
- Non-root user.
- `no-new-privileges`.
- Drop Linux capabilities.
- Read-only root filesystem + tmpfs scratch.
- Output limit.
- Container cleanup after timeout.
- After external cancellation, kill the host `docker run` process tree and
  force-clean the container.
- Integration tests for seccomp profile enforcement.
- Symlink staging skip.
- Workspace root mount + subdirectory workdir.

Docker sandbox tests are off by default and require a local Docker daemon:

```bash
RUN_QUERY_RUNTIME_DOCKER_TESTS=true dotnet test \
  CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj \
  --filter "FullyQualifiedName~DockerSandboxRunnerIntegrationTests" \
  --logger "console;verbosity=detailed"
```

Runner directions that still need to be filled in:

- Kubernetes / remote runner: remote isolated execution for enterprises and CI.
- More complete artifact capture: bind tool-execution logs, generated outputs, and
  diff more strongly to the run manifest.
- A unified public schema for capability policy and sandbox policy.

In the short term, avoid advertising a "local process allowlist" as a security
sandbox. It can be a development experience; the Docker runner is the current first
verifiable isolation boundary, but it still needs more platform matrix and long-term
hardening before it can serve as a production-grade security promise.

## 8. Native AOT and Cross-Platform Publishing

**Today**: Native AOT has passed publish and smoke on the local `osx-arm64` path,
and a CI `aot` lane is configured to publish and smoke the real native `qre`
across a `linux-x64` (blocking) + `osx-arm64` (non-blocking, being stabilized)
RID matrix with an unapproved-trim/AOT-warning gate. A full cross-platform
blocking release matrix is still being promoted lane by lane.

One long-term goal of the project is to compile `qre` into a cross-platform native
binary, lowering install and cold-start cost. Target platforms include:

- macOS arm64 / x64
- Linux x64 / arm64
- Windows x64 / arm64

The first CLI path of Native AOT has been validated locally on `osx-arm64`, but
cross-platform Native AOT cannot yet be advertised as a complete release capability.
Current status:

- `CodexFlow.QueryRuntime.Cli` / `CodexFlow.QueryRuntime.Experimental` /
  `CodexFlow.QueryRuntime.Engine` have cut their dependency on `CodexFlow.Core`.
- The machine-readable output of the CLI and trace has moved to `System.Text.Json`
  source-generated contexts.
- `QreModelExecutionPolicy` was changed in Phase 1.5 to explicitly copy
  `ChatOptions` into `VllmChatOptions`, avoiding reflection on the CLI thinking-policy
  path.
- The directly referenced provider client package has migrated to `VllmChatClient`
  `2.0.21`; the QRE AOT publish path no longer produces a Newtonsoft.Json transitive
  warning, and the Anthropic Messages thinking-off behavior has been validated.
- Phase 1.5 fixed `QueryRuntimeEngine`'s reflective option normalization, the legacy
  tool-call fingerprint dynamic JSON serialization, the hashline metadata dynamic
  conversion, and `ToolArgumentNormalizer`'s `JObject.ToObject<T>` path.
- The local `osx-arm64` AOT publish has passed, and the published native `qre` has
  validated `--version`, `run --response ... --json`, `tool list --json`,
  `trace latest --jsonl`, `diff latest --json`, and `replay latest --json`.
- CI is configured to run the same publish + smoke through
  `scripts/qre-aot-gate.sh` (publish with `PublishAot=true` and fail on any
  unapproved trim/AOT warning, checked against
  `scripts/qre-aot-approved-warnings.txt`) and `scripts/qre-aot-smoke.sh` (native
  `qre --version`, offline `run`, `tool list`, recorded `replay latest`, and a
  strict `replay latest --strict` determinism check over two isolated workspace
  copies of one source trace). `scripts/queryruntime-baseline-gate.sh
  --include-aot` runs the identical scripts locally.
- The Native AOT `qre` has validated real-provider calls. Both an OpenAI-compatible
  `chat-completions` endpoint and an Anthropic Messages endpoint can be used for
  smoke; Anthropic Messages thinking-off behavior requires `VllmChatClient` 2.0.21 or
  newer.
- External tool schema uses a manifest-first design; `inputSchema` is exposed
  directly by an explicit `AIFunction` implementation, not depending on external
  delegate reflection.
- The CI `aot` lane now validates equivalent publish + smoke on `linux-x64`
  (blocking) and `osx-arm64` (non-blocking until stabilized); `win-x64` /
  `linux-arm64` / `osx-x64` are still release-only (release.yml) and not yet part
  of the blocking CI smoke matrix.
- `AIFunction` tool schema generation may depend on reflection and needs a separate
  audit before more built-in tool packs enter the AOT CLI.
- Dynamic plugin loading conflicts with AOT; prefer out-of-process plugin models such
  as MCP/stdio.
- Provider adapters, sandbox runners, and tool packs still need ongoing trimming-
  compatibility maintenance.

Target publish command shape:

```bash
dotnet publish CodexFlow.QueryRuntime.Cli \
  -c Release \
  -r osx-arm64 \
  -p:PublishAot=true \
  -p:SelfContained=true
```

The acceptance criteria should not be only "can publish," but should include:

- `qre --version` runs.
- `qre tool list` runs.
- `qre run --response ... --json` runs.
- `qre replay latest --json` runs.
- No critical trimming warnings.
- CI covers macOS, Linux, and Windows.

## 9. Project Potential

### 9.1 Clear open-source positioning

A "complete AI coding platform" easily competes head-on with products like Claude
Code, Cursor, Cline, and OpenHands — a high bar and hard to differentiate. An "agent
runtime harness" is a more foundational positioning: it can be reused by those kinds
of products, plugins, CI, and enterprise platforms.

The advantages of this positioning:

- Users don't have to migrate to a complete platform to adopt the runtime.
- It can spread starting from the CLI and NuGet packages.
- It's easier for developers to understand as infrastructure rather than yet another
  app.
- It's more suited to community contributions of tool packs, sandbox runners, and
  provider adapters.

### 9.2 The .NET ecosystem gap

The Python and TypeScript ecosystems have a large number of agent demos and
frameworks, but a .NET-native coding-agent runtime harness is still scarce. CodexFlow
already has the following foundations:

- ASP.NET Core and .NET engineering experience.
- A `Microsoft.Extensions.AI` integration direction.
- An existing QueryRuntime loop.
- Platform accumulation in tool calls, event streams, TDD adapters, validators, and
  security auditing.
- A CLI and experimental harness slice that already runs.

If these capabilities can be split into lightweight, stable, installable components,
it has a chance to become a foundational project in the .NET agent ecosystem.

### 9.3 Trace / replay is the key differentiator

What agent development truly lacks is reproducibility. Merely "being able to call
tools" is not a moat; recording every model request, model response, tool call, tool
output, policy decision, diff, and artifact into an auditable format is the
engineering infrastructure.

Once deterministic replay matures, the project can support:

- Issue reproduction with an attached trace.
- Provider behavior comparison.
- Tool schema regression testing.
- Agent benchmarks.
- Enterprise audit and compliance records.

This clearly differentiates it from a coding assistant that only chases interaction
experience.

### 9.4 Sandbox is the entry point for commercialization and enterprise adoption

What enterprises care about most is usually not "can the model write code," but:

- Which files can it access?
- Can it reach the network?
- Can it read secrets?
- Can it run destructive commands?
- Is there an audit record for every operation?
- Can it be replayed when something goes wrong?

If QueryRuntime combines capability policy, the Docker sandbox, trace/replay, and the
CLI, it has the foundation for enterprise adoption, and can naturally extend to a
hosted service or internal platform.

### 9.5 Native AOT can bring a distribution advantage

If `qre` can ultimately be distributed as a single-file native binary, developers can
install and use it like `ripgrep`, `gh`, or `kubectl`. This matters a lot for
open-source spread:

- Low install cost.
- Simple CI integration.
- Good local toolchain experience.
- Does not require users to first understand the whole CodexFlow platform.

## 10. Recommended Evolution Path

### Phase A: stabilize the current experimental CLI

- Solidify the `--json` output DTO.
- Clarify the relationship between `--json-output`, thinking, and tools.
- Add CLI smoke tests.
- Stabilize the `.qre/runs/<run-id>` structure.

### Phase B: extract the public runtime contract

- Define `IQueryRuntimeEngine`.
- Define `QueryRuntimeRequest` / `QueryRuntimeResult`.
- Define the public trace DTO.
- Remove the default dependency of the runtime core on the Web API, Identity, the
  database, and SignalR.

### Phase C: tools and capability policy

- Split read / write / command / git / dotnet / node / python into tool packs.
- Each tool declares its capability.
- The profile decides which capabilities are allowed.
- The default profile is conservative; dangerous operations must be explicitly
  enabled.

### Phase D: AOT compatibility probe

- Status: the local `osx-arm64` probe has passed; it should become a blocking CI
  next.
- Keep recording the trim/AOT warning baseline rather than leaving warnings to be
  handled right before release.
- At least try running the native binary's `qre --version`, `qre tool list`,
  `qre run --response ... --json`, and `qre replay latest --json`.
- Keep CLI hot paths like `QreModelExecutionPolicy` as explicit mappings.
- Make clear that the CLI AOT path does not include MVC, SignalR, EF, dashboards, and
  runtime-loaded DLL plugins.

### Phase E: Docker sandbox MVP

- Status: the Docker runner MVP and the first hardening slice are complete.
- Continue adding CI runner coverage, the platform matrix, a remote runner, and more
  complete artifact capture.
- Keep clearly labeling the local process runner as trusted-development-only.

### Phase F: deterministic replay

- Status: the Phase 3 first slice is complete.
- It already records model responses, structured tool-call snapshots, the normalized
  argument hash, tool outputs, and content-addressed blobs.
- A recorded replay model adapter and tool coordinator are implemented.
- Full-fidelity replay does not call the provider or execute the original tools;
  the default public trace permits summaries only.
- Continue adding deterministic ID / clock, trace schema migration, a public replay
  spec, and cross-version replay compatibility.

### Phase G: AOT hardening

- Use `System.Text.Json` source generation.
- Avoid runtime-critical paths depending on reflection-heavy dynamic loading.
- Prefer designing the plugin model as MCP/stdio; the current external tool already
  uses a manifest-first out-of-process schema, avoiding runtime DLL plugin loading.
- Upgrade the Phase D AOT probe to blocking CI and validate multi-platform native
  binaries.

### Phase H: standalone open-source release

- Clean the repo history and secrets.
- Clarify the license.
- Prepare the README, quickstart, examples, threat model, and replay format spec.
- Publish NuGet packages and the CLI binary.
- Use a few real example repos to show the full flow from analysis to trace to
  replay.

## 11. Current Limitations

The current implementation should not yet be described as a mature runtime. Main
limitations include:

- The CLI provider path has separated from the `CodexFlow.Core` provider factory,
  but still depends on `QreVllmChatClientFactory`'s model-family heuristic routing.
- Replay supports recorded replay, but is not yet benchmark-grade deterministic
  replay; deterministic IDs, clock, and trace schema migration still need hardening.
- The sandbox has a Docker runner, but Kubernetes / remote runners and a broader
  platform matrix are not done.
- Native AOT has passed locally on `osx-arm64`; Linux / Windows publish, signing,
  release packages, and the CI matrix are not done.
- Provider-native token accounting is not yet integrated; `usage.json` is currently
  estimated usage and cannot be used as a billing basis.
- MCP-stdio currently supports only a one-shot `tools/call`, without a full
  initialize lifecycle.
- The public package boundary, namespaces, DTOs, and serialization policy still need
  to converge.
- The repo still contains full platform code; the standalone extraction of the
  open-source harness is not complete.

These limitations do not affect validating the current direction, but at release time
they must be stated honestly: this is a runtime harness being refined, not an
already-complete, mature, secure execution platform.

## 12. In One Sentence

CodexFlow QueryRuntime's most valuable direction is to become a cross-platform,
auditable, replayable, sandboxable, embeddable agent runtime harness. It should make
it easier for developers to build their own coding agent, CI agent, IDE agent, or
internal enterprise agent platform — rather than requiring them to adopt a complete
CodexFlow SaaS app.
