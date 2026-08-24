# ADR-001: Local single-process Runtime boundary

- Status: Accepted
- Date: 2026-08-24
- Owners: Runtime maintainers

## Context

QRE is a standalone CLI and embeddable library. Route two needs codex-rs/core-like execution boundaries without
turning the runtime into a distributed session platform.

## Decision

The primary deployment is one trusted host process executing one active Turn per Session. Repository content,
trace input, model output and tool input are untrusted. The host process, operating-system account and configured
provider/sandbox adapters are trusted administrative boundaries.

Route two does not add leases, fencing, network-partition recovery, distributed ownership, or automatic crash
resume. Cancellation and failure end the current physical execution; a caller may start a new execution.

## Consequences

- In-memory state is authoritative during a run.
- Audit and checkpoint projections do not become a distributed transaction log.
- Multi-process or crash-resume requirements require a route-three ADR and new threat model.
