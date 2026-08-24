# ADR-008: Local Same-Version Crash Resume

- Status: Accepted
- Date: 2026-08-24
- Owners: QueryRuntime maintainers
- Scope: Route 3 / H1 only

## Context

The v2 runtime has durable audit evidence but no supported way to continue an
unfinished logical Turn after the hosting process exits. H1 adds recovery for
the existing local, single-process deployment boundary. It must not imply
distributed ownership, exactly-once tool execution, cross-version migration,
or automatic recovery of uncertain side effects.

## Decision

1. A logical Turn keeps its `RuntimeTurnId`. Every fresh execution or recovery
   uses a distinct `RuntimeRunAttemptId` and records its parent/root lineage.
2. The runtime writes an atomic, integrity-protected checkpoint only at these
   stable boundaries: Turn start, Step prepared, model output committed, tool
   batch committed, text Step committed, and terminal Turn.
3. Recovery creates a new attempt and never appends to or overwrites evidence
   from the prior attempt.
4. A prepared Step can be sampled again. A committed model response without
   tool calls can be committed to history without sampling again.
5. A committed model response containing tool calls, without a durable complete
   tool-batch checkpoint, is `NeedsReconciliation`. H1 never automatically
   re-executes those calls, including tools described as read-only.
6. H1 supports only the same Runtime contract version, same Session/Turn,
   equivalent frozen request, same workspace identity, and local single-process
   execution. Validation fails closed before provider or tool execution.
7. Checkpoints contain model and history material. The JSON file adapter is
   enabled only for sanitized fixtures or explicitly private diagnostic storage.
   Public-redacted runs remain non-resumable.
8. Checkpoint writes are always fail-closed when durable recovery is enabled. Files use bounded reads,
   content length and SHA-256 verification, path containment, atomic replacement,
   a single bounded open-handle read, and private directory/file permissions before sensitive bytes are exposed.
9. A terminal checkpoint is not resumed. The caller reads the terminal result
   or starts a new Turn.

## Explicit non-goals

- Lease, heartbeat, fencing epoch, takeover, or multi-host coordination.
- Cross-version checkpoint migration.
- Non-idempotent automatic recovery or a durable five-state tool ledger.
- Generic exactly-once claims.
- Migration of in-memory approvals, steering queues, cancellation tokens, or
  active provider streams.

## Acceptance

- Resume from Turn-start and Step-prepared checkpoints without replaying a tool.
- Resume a committed text-only model response without another provider call.
- Reject a committed response with unresolved tool calls as
  `UncertainSideEffect` before provider/tool execution.
- Reject corrupt, truncated, oversized, path-escaping, wrong-version, and
  request-mismatched checkpoints before execution.
- Preserve Turn identity, create a new attempt, and retain the parent/root
  lineage in the new checkpoint.
- Pass Windows/Linux tests and Native AOT serialization smoke.

## Consequences

H1 provides conservative restart recovery for local hosts. Some recoverable work
may still require a new model call, and any ambiguous tool window stops for human
reconciliation. H2 and H3 remain independent product-triggered work.
