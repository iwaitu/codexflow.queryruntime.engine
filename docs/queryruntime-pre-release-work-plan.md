# QueryRuntime Pre-Release Work Plan

Date: 2026-06-04

Chinese version: `docs/queryruntime-pre-release-work-plan.zh-CN.md`

Archived completed development plan:
`docs/archive/queryruntime-next-development-plan.completed-2026-06-04.md`

## 1. Release Position

The independent QueryRuntime Engine has reached functional closure for the
core runtime harness:

- `qre run`, trace, replay, rerun, diff, doctor, tool listing, and sandbox
  execution are present.
- `none`, `readonly`, `verify`, and `repair` profiles are available.
- Provider-neutral model adapters and thinking-policy handling are in place.
- Recorded replay and run artifacts exist as first-class runtime surfaces.
- Docker sandbox hardening exists for opt-in isolated execution.
- `repair` exposes controlled workspace write tools and run-scoped
  `diff.patch` generation.
- P5 repair security issues found by Antigravity were fixed and re-reviewed.

This plan treats feature development as substantially complete and moves the
project into pre-release stabilization. The goal is to make the current QRE
safe to publish, easy to consume, and hard to regress.

## 2. Priority Order

### R0: Freeze The Release Baseline

Objective: define the exact code, docs, and verification surface that will be
called the pre-release baseline.

Scope:

- Keep QRE independent from `CodexFlow.Core` and `CodexFlow.Contracts`.
- Run and record the release baseline checks.
- Confirm the active docs point to this pre-release plan, while the completed
  phase plan stays archived.
- Record any known non-release-blocking limitations explicitly.

Acceptance:

- `git diff --check` passes.
- `dotnet test CodexFlow.QueryRuntime.slnx --no-restore` passes.
- `rg -n "CodexFlow\.(Core|Contracts)" --glob "*.cs" --glob "*.csproj" --glob "*.slnx"`
  returns no project/source coupling.

### R1: Make Native AOT CI Blocking

Objective: prevent changes from merging when the native binary cannot be
published and smoked.

Scope:

- Confirm the `linux-x64` Native AOT lane publishes the `qre` binary.
- Confirm the lane runs a native smoke against the produced binary.
- Make the AOT lane a required blocking GitHub check for the protected branch.
- Keep local AOT smoke commands documented for maintainers.
- Investigate and resolve any `linux-x64` blocked or pending-check state.

Acceptance:

- CI shows a green required check for the AOT smoke lane.
- The produced Linux binary can run `qre --version` and a minimal static
  `qre run` smoke.
- Branch protection requires the AOT check before merge.

### R2: Harden Repair And Artifact Boundaries

Objective: make the new write-capable profile safe enough for pre-release use.

Scope:

- Add Docker repair smoke coverage once write tools are available in the
  sandboxed runtime path.
- Add a same-path dirty-baseline test to document how `diff.patch` behaves when
  the target file already had uncommitted changes before a repair run.
- Keep `.git`, `.qre`, secret-looking paths, path traversal, and symlink-chain
  escape protections covered by negative tests.
- Decide whether richer patch formats are required for pre-release, or whether
  targeted text replacement remains the first supported patch surface.

Acceptance:

- Local repair profile tests pass.
- Docker repair smoke is either automated or explicitly documented as a gated
  manual check.
- `diff.patch` limitations are documented and tested where behavior is stable.

### R3: Package And Distribution Readiness

Objective: make the CLI consumable without source-level coupling.

Scope:

- Define the pre-release artifact shape: native binary, archive name,
  checksums, and target runtime identifiers.
- Confirm the expected RID matrix for the first pre-release.
- Keep the downstream integration contract binary-first: CodexFlow should
  consume an installed QRE binary or packaged adapter, not project references.
- Verify `--version`, `doctor`, and static `run` work from the packaged output.

Acceptance:

- Release workflow can produce named artifacts.
- Release artifacts include a native `qre` binary and checksum metadata.
- A clean checkout or temp directory can execute the packaged binary smoke.

### R4: Documentation And User-Facing Polish

Objective: make the pre-release understandable to a new maintainer or adopter.

Scope:

- Update README quick start around provider configuration, tool profiles,
  sandbox modes, and repair behavior.
- Keep English and Chinese docs aligned for the active release plan and core
  usage docs.
- Add a short limitations section for live-provider tests, Docker-gated tests,
  usage estimation, replay determinism, and MCP stdio limitations.
- Ensure security-sensitive docs link to `docs/threat-model.md` and
  `docs/tool-capabilities.md`.

Acceptance:

- README points to the pre-release work plan, not the archived development
  plan.
- Key limitations are visible before release.
- There are no stale claims saying `repair` has no write tools.

### R5: Downstream Integration Readiness

Objective: prepare the path for CodexFlow to consume QRE after the standalone
engine stabilizes.

Scope:

- Define the integration contract for invoking QRE from CodexFlow.
- Keep the contract CLI/binary or package based.
- Avoid introducing `CodexFlow.Core` or `CodexFlow.Contracts` references back
  into this repo.
- List the CodexFlow-side work separately from QRE release work.

Acceptance:

- QRE repo stays standalone.
- Integration notes describe inputs, outputs, errors, and artifact locations.
- CodexFlow-specific migration work is tracked outside this release baseline.

## 3. Release Candidate Gate

Before cutting the first pre-release tag, run:

```bash
git diff --check
dotnet test CodexFlow.QueryRuntime.slnx --no-restore
rg -n "CodexFlow\\.(Core|Contracts)" --glob "*.cs" --glob "*.csproj" --glob "*.slnx"
dotnet publish CodexFlow.QueryRuntime.Cli -c Release -r linux-x64 -p:PublishAot=true -p:SelfContained=true
```

Then run the produced native binary:

```bash
./CodexFlow.QueryRuntime.Cli/bin/Release/net10.0/linux-x64/publish/qre --version
./CodexFlow.QueryRuntime.Cli/bin/Release/net10.0/linux-x64/publish/qre run --workspace . --profile none --response "pre-release smoke" "smoke"
```

If Docker is available, also run the Docker sandbox smoke and repair-profile
smoke documented by the current test suite.

## 4. Non-Blocking Follow-Ups

These should not block the first pre-release unless a release reviewer upgrades
them to blocking:

- Rich patch formats beyond targeted text replacement.
- Fully deterministic replay contract hardening.
- Full MCP stdio initialize lifecycle.
- Kubernetes or remote sandbox runners.
- Billing-grade usage accounting.
- Complete downstream CodexFlow migration.

## 5. Definition Of Done

The pre-release is ready when:

- Native AOT CI is a required green check.
- The release artifact can be downloaded and smoked outside the source tree.
- Repair write tools are covered by containment and negative-path tests.
- README and docs describe the real supported surface and limitations.
- No source-level dependency on `CodexFlow.Core` or `CodexFlow.Contracts`
  exists in this repo.
- Known non-blocking gaps are explicitly documented.

## 6. Baseline Log

### 2026-06-04 R0 baseline freeze

Executed in `/Users/iwaitu/github/codexflow.queryruntime.engine`:

- `git diff --check` passed.
- `dotnet test CodexFlow.QueryRuntime.slnx --no-restore` passed:
  `CodexFlow.QueryRuntime.UnitTests` reported 192 passed; gated integration
  tests reported 13 skipped.
- `rg -n "CodexFlow\.(Core|Contracts)" --glob "*.cs" --glob "*.csproj" --glob "*.slnx"`
  returned no source/project coupling.

Known pre-release limitation recorded during R2 hardening: run-scoped
`diff.patch` is path-scoped to repair edits, but same-path dirty baselines are
represented as `HEAD` to final-state diffs for that file.

### 2026-06-04 R1/R3 local artifact smoke

Executed `scripts/queryruntime-baseline-gate.sh --include-aot` on the local
`osx-arm64` host:

- `git diff --check` passed.
- `dotnet test CodexFlow.QueryRuntime.slnx --no-restore` passed:
  `CodexFlow.QueryRuntime.UnitTests` reported 193 passed; gated integration
  tests reported 13 skipped.
- Native AOT publish for `osx-arm64` completed with no trim/AOT warnings.
- Native binary smoke passed for `qre --version`, offline `qre run`, tool list,
  recorded replay, and strict replay digest determinism.

Also locally packaged the produced `osx-arm64` binary as `qre-osx-arm64.tar.gz`,
extracted it outside the publish directory, and verified packaged `qre
--version`, `qre doctor --json`, and static `qre run --json`.
