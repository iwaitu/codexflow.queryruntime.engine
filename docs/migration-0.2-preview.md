# Migrating to QueryRuntime 0.2 preview

This guide covers source and operational migration from the stable v1 `0.1.2` surface to the opt-in v2 runtime
shipped in `0.2.0-preview.*` packages.

## Compatibility boundary

- `0.1.2` packages and v1 trace schema are not republished or mutated.
- v2 is opt-in: CLI callers pass `--runtime v2`; CodexFlow hosts select `Runtime:QueryRuntime:Backend=qre-v2`.
- Existing v1 readers remain available. Unknown v2 audit schema versions fail explicitly.
- A Turn belongs to the backend that started it. Changing the feature flag starts subsequent requests on the selected
  backend; an in-flight Turn is never resumed on another backend.

## API changes

v2 uses the provider-neutral contracts under `CodexFlow.QueryRuntime.Protocol` and the hosting/runtime types under
`CodexFlow.QueryRuntime.Engine.V2`. The main entry point is `IAgentRuntime.RunAsync(RuntimeRunRequest, ...)`.
Session, Turn, Step, invocation and audit identities are typed. Model streams distinguish text, reasoning, usage,
warnings, tool calls and completion. Tool execution requires a frozen registry and execution plan.

The old `IQueryRuntimeEngine` and Experimental harness remain available during preview for rollback. New code should
avoid depending on Experimental Agent Loop internals; use the v2 hosting facade and Engine-owned policy, context and
audit contracts.

## CLI migration

```bash
qre run --runtime v2 --profile readonly --response "offline smoke" --json "inspect this repository"
qre replay latest --runtime v2 --summary --json
```

The v1 path remains the default during the observation window. v2 supports `none`, `readonly`, `verify` and `repair`.
Write/high-risk work still requires a frozen approval binding. Public audit is redacted and summary-only; use
`--trace-data sanitized` only for reviewed fixtures or `--trace-data private` for access-controlled diagnostics.

## Host migration and rollback

1. Upgrade the package without changing the backend flag.
2. Run the host contract kit against `qre-v2`, starting with readonly and verify.
3. Enable repair only after write approval and sandbox/write-back gates pass.
4. Compare policy decisions, tool order, normalized terminal reason and side-effect count with zero tolerance. Apply
   final-text tolerance separately.
5. To roll back, set the backend to `qre` (packaged v1) or `core` for new requests. Do not copy an active Turn or v2
   audit state into v1.

Unsupported backend names and non-positive model-stream timeouts should fail host startup instead of silently falling
back to another engine.

## Default-switch gate

The v2 default and deletion of the duplicate v1 Agent Loop require all automated gates, at least two preview releases,
and the agreed observation window without Critical/High execution-semantic regressions. Building the code and a local
preview package does not satisfy that time-based gate by itself.
