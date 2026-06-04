# QueryRuntime Next Development Plan

Date: 2026-06-03

Archive status: completed and archived on 2026-06-04 after P5 completion.
The active release stabilization plan is
`docs/queryruntime-pre-release-work-plan.md`.

Chinese version:
`docs/archive/queryruntime-next-development-plan.completed-2026-06-04.zh-CN.md`

Source review: this plan is derived from the external Claude Code final review
of the current QueryRuntime branch, then merged with the follow-up Antigravity
review of this planning document. It is a companion to
`docs/queryruntime-harness-open-source-strategy.md`; the strategy document
remains the source of truth for phase boundaries and release sequencing.

The `P0` through `P6` labels identify work packages from the external review
cycle and the follow-up baseline discussion. They are not the execution order.
Section 11 defines the consolidated execution order after merging Claude Code,
Antigravity, and baseline-hardening feedback.

## 1. Current Baseline

The current branch has completed the open-source harness groundwork through
Phase 3 first-slice work:

- Phase -1, Phase 0, Phase 1, Phase 1.5, Phase 2a, Phase 2b-MVP, and
  Phase 2b-Hardening are treated as completed for the current branch scope.
- Phase 1.6 reverse dependency work has started, but CodexFlow.Core still needs
  additional migration before it fully consumes QRE through stable adapters.
- Phase 3 has implemented the first trace/replay slice, but deterministic replay
  hardening is not complete.
- The real `qre` CLI path is now the primary documented entry point.
- The CLI provider path uses QRE-owned provider construction through
  `QreVllmChatClientFactory`; it no longer depends on the Core provider factory.
- Local `osx-arm64` Native AOT publish and native `qre` smoke have been
  verified, but AOT is not yet a blocking CI gate.
- `VllmChatClient` 2.0.21 is the expected provider package. The current
  Anthropic Messages thinking-off regression should stay covered by gated real
  provider tests.
- Model clients and the QRE engine already consume provider responses through
  streaming APIs internally, but `qre run` currently prints the final assistant
  text only after the run completes. Real-time CLI streaming remains a baseline
  UX and event-surface gap.

## 2. Verified Capabilities

The current plan assumes these capabilities already exist and should be
preserved by future phases:

- `qre run`, `qre trace`, `qre replay`, `qre diff`, `qre tool list`,
  `qre doctor`, and `qre sandbox exec` are available from the CLI.
- `.qre/runs/<run-id>/` contains run artifacts, including JSONL trace,
  manifest, run summary, usage, diff, blobs, and collected artifacts.
- Recorded replay reads prior trace data without calling the provider or
  executing tools.
- The Docker sandbox runner has MVP and hardening coverage for isolated
  workspace staging, non-root execution, denied network, read-only root
  filesystem, dropped capabilities, timeout cleanup, and output limits.
- Tool capability policy emits machine-readable denial and approval metadata
  before process execution.
- Gated live LLM tests cover OpenAI-compatible and Anthropic Messages behavior,
  including thinking-off behavior for Anthropic Messages.
- Live provider tests require external credentials/endpoints and are currently
  manual gated checkpoints rather than automated CI gates.

## 3. Planning Principles

Future work should keep QRE as a runtime harness, not a platform surface:

- QRE must not pull in Web API, Identity/JWT, SignalR, PostgreSQL, MongoDB,
  Redis, Qdrant, notification, or React UI dependencies unless they are behind
  explicit optional adapters.
- Public contracts should remain small, serializable, and AOT-compatible.
- Provider behavior must be explicit. Unknown provider/model combinations should
  fail loudly instead of silently falling back to an assumed endpoint shape.
- Replay and trace schemas should become durable public contracts before open
  source release.
- Security-sensitive write, process, network, and sandbox capabilities should
  be expressed through policy and tested with negative cases.

## 4. P0: Baseline Freeze And Hardening

Objective: turn the current branch baseline into a repeatable, documented, and
CI-protected starting point before beginning new feature development.

Status: complete for the current branch baseline as of 2026-06-03. The current
capability acceptance matrix lives in
`docs/queryruntime-harness-open-source-strategy.md`; the executable entrance
gate is `scripts/queryruntime-baseline-gate.sh`.

This phase should not expand into finishing every open item. It should freeze
what is already claimed as complete or locally verified, clarify what remains
manual or partial, and define the entrance criteria for P1/P3/P5 work.

Scope:

- Add a current-capability acceptance matrix for Phase -1, Phase 0, Phase 1,
  Phase 1.5, Phase 2a, Phase 2b-MVP, Phase 2b-Hardening, and Phase 3
  first-slice.
- Record the exact smoke commands and tests that prove each completed slice.
- Clarify that live provider tests are locally/gated verified checkpoints, not
  automated CI checks.
- Clarify the QRE/Core boundary: QRE CLI/provider path no longer depends on the
  Core provider factory, but CodexFlow.Core has not fully migrated to consume
  QRE through stable adapters.
- Clarify replay semantics: current recorded replay is a first slice, not a
  fully deterministic replay contract.
- Clarify the CLI streaming contract: ordinary `qre run` remains stable final
  output, `qre run --stream` should stream assistant text in real time, and any
  machine-readable streaming mode should use an event-safe shape such as
  `--jsonl-stream`.
- Define a small baseline gate that must stay green before feature phases begin.

Acceptance criteria:

- The technical guide and strategy document agree on completed, partial, and
  planned QRE capabilities.
- The baseline matrix maps each completed slice to concrete tests, commands, or
  documented limitations.
- `dotnet test CodexFlow.QueryRuntime.slnx --no-restore` remains green.
- The current native AOT local publish/smoke command is documented as a
  repeatable baseline check.
- Gated live provider tests are documented with required environment variables
  and are explicitly marked as non-CI checks.
- The current non-streaming CLI behavior is documented, and the target
  streaming behavior is defined before provider adapter work begins.
- Streaming output does not corrupt `--json` stdout contracts; JSON event
  streaming uses a separate explicit mode or output channel.
- The next execution order treats P0 as the entrance gate for subsequent work.

Main risks:

- Treating partial capabilities as complete can cause later phases to build on
  false assumptions.
- Over-expanding baseline work can delay actual QRE development without adding
  new certainty.
- Provider and replay claims can be overstated if local/gated checks are not
  separated from CI guarantees.
- Streaming text directly to stdout can break scripts if it is mixed with
  machine-readable JSON output.
- Tool-call streaming can expose partial or malformed structured tool-call
  payloads if text deltas and tool-call assembly are not separated.

Suggested tests:

- `git diff --check`.
- `dotnet test CodexFlow.QueryRuntime.slnx --no-restore`.
- Local AOT publish plus `qre --version` smoke.
- Offline `qre run --stream --response ...` smoke once the flag exists.
- JSON contract tests proving `--json` remains final-result only and
  `--jsonl-stream` emits event-shaped output.
- Optional gated real-provider test run when credentials/endpoints are present.

## 5. P1: Provider-Neutral Model Adapters

Objective: replace CLI-local model-family heuristics with explicit provider
adapters under a QRE-owned model adapter surface.

Scope:

- Introduce a provider adapter abstraction such as
  `CodexFlow.QueryRuntime.Models.*`.
- Split OpenAI-compatible, Anthropic Messages, Responses, and other provider
  shapes into explicit adapters.
- Remove silent unknown-model fallback from the production path.
- Keep `QreVllmChatClientFactory` as a temporary bridge only if it delegates to
  explicit provider adapters.
- Preserve thinking policy behavior for tools and JSON output.

Acceptance criteria:

- Unknown `--model` or incompatible `--api-mode` fails with a clear CLI error.
- OpenAI-compatible real-provider smoke passes.
- Anthropic Messages real-provider smoke passes with thinking disabled when
  requested.
- Gated real LLM phase tests remain green.
- Adapter contract tests cover thinking policy, tool-call compatibility, JSON
  output, and unsupported provider behavior.
- Each `CodexFlow.QueryRuntime.Models.*` adapter package has zero project
  dependency on `CodexFlow.Core`.
- Adapter changes remain clean under Native AOT publish analysis, including
  unapproved trim/AOT warnings.

Main risks:

- Provider quirks around `tool_choice`, thinking, JSON schema, and streaming can
  regress silently.
- Provider adapter changes can accidentally reintroduce Core dependencies.
- Provider adapter changes can introduce reflection-heavy serialization paths
  that only fail under AOT publish.

Suggested tests:

- Focused unit tests for adapter selection and unknown provider failure.
- Gated real-provider tests for OpenAI-compatible and Anthropic Messages.
- AOT CI publish smoke after adapter changes.

## 6. P2: Deterministic Replay Hardening — complete (2026-06-03)

Objective: finish Phase 3 by making trace/replay durable enough for regression
testing, issue reproduction, and public format documentation.

Status: complete as of 2026-06-03. `QueryRuntimeEngine` now takes an injectable
`TimeProvider` and query-id factory (defaults remain system clock + `Guid.NewGuid`),
with `DeterministicReplayClock` providing a fully deterministic clock/duration for
strict replay. Traces carry an explicit `SchemaVersion` on the `run.started` record
and in `manifest.json`, governed by the public `QueryRuntimeTraceSchema`
(`CurrentVersion = 1`) plus `QueryRuntimeReplayMode` / `QueryRuntimeTraceCompatibility`
DTOs in Abstractions. The new `qre replay latest --strict` seeds the clock/id from the
source trace and emits a byte-stable `replayDigest`
(`DeterministicReplay.ComputeCanonicalDigest`, excluding run-scoped RunId/SessionId).
It gates on schema version: legacy unversioned traces are rejected from strict
replay with precise reasons but remain available through non-strict recorded replay,
while unsupported future schema versions are rejected in both strict and non-strict
replay with an upgrade-oriented reason. Strict replay stays provider-free / tool-free via
`RecordedReplayModelClient` and `RecordedReplayToolPack`. Covered by
`StrictReplay_ProducesByteIdenticalDigest_AndNeverExecutesOriginalTool`,
`TraceSchema_GatesStrictReplayByVersion`, `ReplayRecorded_RejectsUnsupportedFutureSchema_WithPreciseReason`,
and CLI `ReplayStrict_*` tests; all 179 unit tests pass. A 20-minute Antigravity
re-review found no blocking issues after the future-schema replay gate fix. Note:
schema v1 was frozen before P5 write-tool events exist, so file
mutation / patch / content-hash events will arrive as an additive v2 bump, which the
new versioning + compatibility machinery is designed to absorb.

Scope:

- Add deterministic ID generation and clock injection.
- Add explicit trace schema versioning.
- Define public trace/replay DTOs.
- Add migration or compatibility handling for older traces.
- Harden `strict-replay` so it never calls providers or executes tools.
- Document replay guarantees and non-guarantees.

Acceptance criteria:

- A recorded run can replay without provider access and without tool execution.
- Strict replay output is byte-identical for the same trace, runtime version,
  and replay settings.
- Old trace schemas are either migrated or rejected with a precise
  non-strict/unsupported-version reason.
- Public docs explain trace fields, blob references, tool result capture, and
  replay modes.
- Live rerun mode is documented separately from strict replay and may diverge
  when sandbox commands depend on clock, filesystem, network, or host state.

Main risks:

- Schema churn can break existing `.qre/runs` artifacts.
- Deterministic replay can be overstated if environment-dependent fields remain
  in the trace.
- Freezing replay before write tools exist can force a later schema revision for
  file mutation, patch, and content hash events.

Suggested tests:

- Golden trace replay tests.
- Cross-run determinism tests with injected clock and ID providers.
- Negative tests proving strict replay does not invoke model clients or sandbox
  runners.

## 7. P3: Native AOT Blocking CI

Objective: promote Native AOT from local proof to CI-protected release
constraint.

Status: in progress as of 2026-06-04. The CI `aot` lane is configured to publish
the real `CodexFlow.QueryRuntime.Cli` with `-p:PublishAot=true` across a RID matrix
(`linux-x64`, `osx-arm64`) on matching native runners, fails on unapproved
trim/AOT warnings via `scripts/qre-aot-gate.sh` (allowlist
`scripts/qre-aot-approved-warnings.txt`, currently empty), and smokes the
produced native binary via `scripts/qre-aot-smoke.sh`
(`qre --version`, offline `qre run`, `qre tool list`, recorded `qre replay
latest`, and a strict `qre replay latest --strict` determinism check that
replays a byte-identical source trace in two isolated workspaces and asserts an
identical `replayDigest` with no provider calls or tool executions). The same
scripts back the local `--include-aot` baseline gate. `linux-x64` is blocking;
`osx-arm64` is non-blocking (`continue-on-error`) until it is observed stable on
CI, then it flips to blocking. The local `osx-arm64` publish + gate + smoke is
warning-free and green. Remaining: confirm Linux/macOS CI behavior, then promote
`osx-arm64` (and optionally `win-x64`) to blocking.

Scope:

- Add a CI lane that publishes `CodexFlow.QueryRuntime.Cli` with
  `-p:PublishAot=true`.
- Smoke the produced binary with `qre --version`, `qre tool list`, and recorded
  replay.
- Run on at least two relevant RIDs once the lane is stable.
- Track and fail on unapproved trim/AOT warnings.

Acceptance criteria:

- CI publishes an AOT binary successfully.
- CI smokes the produced binary instead of a framework-dependent CLI.
- The lane is first allowed to run non-blocking, then becomes blocking once it
  is stable.
- There are no unapproved trim/AOT warnings in QRE public IO paths.

Main risks:

- Transitive reflection from provider or serialization dependencies can
  reappear.
- Cross-platform AOT can fail differently from local `osx-arm64` smoke.

Suggested tests:

- CI AOT publish and `qre --version` smoke.
- CI recorded replay smoke.
- Optional RID matrix once Linux and macOS behavior is stable.

## 8. P4: Complete Phase 1.6 Reverse Dependency

Objective: make CodexFlow.Core consume QRE as the runtime instead of QRE
depending on Core orchestration internals.

Scope:

- Move session memory, runtime hooks, context-window governance, and recovery
  concerns behind QRE-facing adapters.
- Ensure QRE-to-Core project references remain absent.
- Keep platform features as adapters around the QRE engine instead of inside the
  harness.
- Preserve current Core orchestrator behavior through targeted regression tests.

Acceptance criteria:

- `CodexFlow.QueryRuntime.*` projects do not reference `CodexFlow.Core`.
- Core orchestrator tests pass through a QRE-backed runtime path where
  applicable.
- Public QRE contracts do not expose platform-only session, user, database, or
  hosted-service types.
- Reverse dependency behavior is documented in the strategy and technical guide.

Main risks:

- Moving runtime concerns can change orchestrator behavior in subtle ways.
- Adapter boundaries can become too wide and recreate the old Core surface under
  a new name.

Suggested tests:

- Project reference audit.
- Focused Core orchestrator regression tests.
- QRE contract serialization and AOT smoke tests.

## 9. P5: Repair Profile Write Tools And Run-Scoped Diff

Objective: make `--profile repair` useful while keeping write capability
explicit, workspace-scoped, and auditable.

Scope:

- Implement workspace-only write and patch-apply tools.
- Keep secret paths, parent directory writes, external mounts, and destructive
  commands denied by default.
- Emit run-scoped `diff.patch` from actual edits.
- Preserve approval records for risky operations.
- Prefer Docker sandbox execution when repair work is untrusted.

Status: MVP implemented as of 2026-06-04. `--profile repair` now exposes
`qre_write_file` and `qre_apply_patch` as controlled workspace file tools. The
tools use canonical workspace path resolution, reject path traversal and symlink
escape, deny protected `.git` / `.qre` artifacts and secret-looking paths, emit
`policy.decision` records through the same trace sink, and record successful
repair edits in the run directory. Run finalization writes `diff.patch` only for
paths recorded as repair edits, so unrelated dirty worktree changes that existed
before the run are not swept into the run-scoped patch. Remaining hardening:
same-path pre-existing dirty baselines, richer patch formats beyond targeted text
replacement, and Docker repair smoke.

Acceptance criteria:

- `qre run --profile repair` can modify files inside the workspace.
- Writes outside the workspace are denied and recorded.
- Write and patch tools reject symlink traversal when the evaluated target
  escapes the workspace boundary.
- Secret-looking files and protected workspace artifacts remain guarded.
- `qre diff latest` returns the run-scoped patch from actual edits.
- Negative policy tests cover path escape, secret paths, destructive commands,
  and approval-required operations.

Main risks:

- Write tools increase the blast radius of model mistakes.
- Diff generation can accidentally include unrelated pre-existing workspace
  changes.
- Symlink traversal can bypass naive workspace path checks if targets are not
  canonicalized and revalidated.

Suggested tests:

- Workspace write allow/deny tests. (covered in MVP)
- Patch apply tests with path traversal attempts. (covered in MVP)
- Run-scoped diff tests in dirty worktrees. (covered for unrelated pre-existing
  dirty worktree changes)
- Docker repair smoke once write tools exist.

## 10. P6: Phase 4 Open-Source Release Readiness

Objective: prepare QRE for a public open-source release as a harness project.

Scope:

- Finalize extraction repository shape.
- Run full-history secret scanning and license scanning.
- Add or finalize `SECURITY.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, and
  release docs.
- Prepare signed single-binary release artifacts and checksums.
- Rewrite README around QRE as a runtime harness, not a SaaS product.
- Provide clean-machine install and smoke instructions.

Acceptance criteria:

- Secret and license scans are clean or documented with resolved exceptions.
- Release artifacts are signed and checksummed.
- A clean machine can install and run `qre`.
- README explains why QRE exists, what is included, what is excluded, and what
  security guarantees it does not make.
- Platform-only surfaces are absent from the public harness package graph.

Main risks:

- Repository history may contain credentials or local endpoints that require
  revocation and cleanup before release.
- Public messaging can overclaim sandbox, replay, provider-neutral, or AOT
  guarantees before they are CI-protected.

Suggested tests:

- Clean checkout smoke.
- Secret/license scan gates.
- Release artifact verification.
- Package graph audit.

## 11. Suggested Execution Order

The consolidated recommended sequence is:

1. P0 baseline freeze and hardening. (complete 2026-06-03)
2. P3 Native AOT blocking CI. (in progress 2026-06-04)
3. P1 provider-neutral model adapters. (complete 2026-06-03)
4. P5 repair profile write tools and run-scoped diff. (complete 2026-06-04)
5. P2 deterministic replay hardening. (complete 2026-06-03)
6. P4 complete Phase 1.6 reverse dependency.
7. P6 open-source release readiness.

The order is intentional:

- Baseline freeze should happen first so later phases build on verified current
  facts instead of optimistic documentation.
- AOT CI should become blocking before provider adapters, trace DTOs, or public
  serialization contracts are refactored.
- Provider adapters should be stabilized while AOT checks are already guarding
  provider selection, serialization, and dependency graph changes.
- Repair write tools should exist before the replay schema is frozen, because
  workspace mutation, file hashes, and patch events must be part of the durable
  trace model.
- Replay hardening should happen after the write-tool event surface is known and
  before public trace format documentation and benchmark claims.
- Core reverse dependency completion should land before extraction.
- Release readiness should be last because it depends on clean package graph,
  security posture, and stable public messaging.

## 12. Open Questions

- Resolved for P0: the acceptance matrix lives in
  `docs/queryruntime-harness-open-source-strategy.md`, and this plan links to
  that source instead of duplicating the matrix.
- Should provider adapters live in separate packages immediately, or first land
  in the current QRE project graph and split later?
- Which RIDs should be mandatory for the first AOT CI gate?
- Should old traces be migrated automatically, or should the first public trace
  version reject pre-public traces with a clear reason?
- Should a minimal replay schema compatibility pass happen before write tools,
  while the final deterministic replay freeze waits until after write tools?
- How strict should `repair` profile be about protected files such as
  `PROJECT_SUMMARY.md`, `.env*`, and generated artifacts?
- Should Docker sandbox be required for write-capable repair work, or remain a
  recommended stronger mode while local repair is still allowed with explicit
  approval?
