# Migrating to QueryRuntime 0.2 preview

This guide covers source and operational migration from the stable v1 `0.1.2` surface to the v2-only runtime
shipped in `0.2.0-preview.*` packages.

## Compatibility boundary

- `0.1.2` packages and v1 trace schema are not republished or mutated.
- CLI execution is v2-only. `--runtime v2` remains optional syntax; `--runtime v1` is rejected before execution.
- CodexFlow accepts only `Runtime:QueryRuntime:Backend=qre-v2` and fails startup for `core` or `qre`.
- Existing v1 trace summary readers remain available for historical inspection. Unknown v2 audit schemas fail explicitly.

## API changes

v2 uses the provider-neutral contracts under `CodexFlow.QueryRuntime.Protocol` and the hosting/runtime types under
`CodexFlow.QueryRuntime.Engine.V2`. The main entry point is `IAgentRuntime.RunAsync(RuntimeRunRequest, ...)`.
Session, Turn, Step, invocation and audit identities are typed. Model streams distinguish text, reasoning, usage,
warnings, tool calls and completion. Tool execution requires a frozen registry and execution plan.

Old v1 public types remain compiled for source migration and historical consumers, but no CLI or CodexFlow production
entry point dispatches work to the duplicate v1 Agent Loop. New code uses the v2 hosting facade and Engine-owned policy,
context and audit contracts.

## CLI migration

```bash
qre run --profile readonly --response "offline smoke" --json "inspect this repository"
qre replay latest --summary --json
```

v2 is the only execution path and supports `none`, `readonly`, `verify` and `repair`.
Write/high-risk work still requires a frozen approval binding. Public audit is redacted and summary-only; use
`--trace-data sanitized` only for reviewed fixtures or `--trace-data private` for access-controlled diagnostics.

## Host migration

1. Upgrade to `0.2.0-preview.17` or later and set the backend to `qre-v2`.
2. Run the host contract kit, starting with readonly and verify.
3. Enable repair only after write approval and sandbox/write-back gates pass.
4. Compare policy decisions, tool order, normalized terminal reason and side-effect count with zero tolerance. Apply
   final-text tolerance separately.
5. Rollback to v1 requires redeploying an older package/application release; there is no runtime feature-flag fallback.

Unsupported backend names and non-positive model-stream timeouts should fail host startup instead of silently falling
back to another engine.

## Cutover decision

The owner explicitly waived the two-preview observation gate on 2026-08-24 and authorized a v2-only cutover. Automated
tests, Native AOT and live E2E remain required. See ADR-007. The operational rollback unit is now a prior deployment,
not an in-process backend flag.
