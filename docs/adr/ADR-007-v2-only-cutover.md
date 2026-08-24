# ADR-007: v2-only execution cutover

- Status: Accepted
- Date: 2026-08-24
- Owner: Project owner

## Decision

The project owner explicitly waives the two-preview observation window defined by ADR-006 and authorizes an immediate
v2-only execution cutover.

- `qre run` always executes Engine v2. `--runtime v1` fails before execution.
- `qre replay latest` and `qre trace latest` default to v2 audit data.
- v1 trace access is limited to explicit read-only summary/inspection.
- `qre rerun latest` reconstructs a new v2 run only from sanitized/private v2 audit data.
- CodexFlow accepts only `Runtime:QueryRuntime:Backend=qre-v2`; legacy `core` and `qre` values fail startup.
- The v1 public surface may remain compiled temporarily for source migration, but production dispatch cannot reach it.

## Consequences

The in-process v1 feature-flag rollback is removed. Operational rollback requires deploying the preceding application
and package release. Automated tests, fail-closed approval behavior, Native AOT, strict data-only replay and live vLLM
E2E remain mandatory. This ADR records an explicit risk acceptance; it does not claim that the skipped observation
window occurred.
