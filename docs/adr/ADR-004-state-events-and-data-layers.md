# ADR-004: Runtime state, audit, presentation and checkpoints

- Status: Accepted
- Date: 2026-08-24
- Owners: Runtime maintainers

## Decision

Route two separates four concerns:

1. In-memory reducer state is authoritative while the process runs.
2. Versioned audit events describe stable transitions and support inspection/recorded replay.
3. Presentation events carry streaming deltas and may be dropped or coalesced for a slow consumer.
4. Optional checkpoints are written only at stable boundaries and do not imply crash resume.

Public persistence uses an explicit redacted projection. Sanitized fixtures may support deterministic recorded
replay. Private diagnostic data remains explicit opt-in with route-one ACL and retention controls.

## Consequences

Text deltas do not require per-event fsync or durable subscriber cursors. Audit writer failure policy is explicit
per mode; public telemetry cannot be treated as full-fidelity replay input.
