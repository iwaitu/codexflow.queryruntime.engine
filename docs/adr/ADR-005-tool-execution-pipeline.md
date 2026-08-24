# ADR-005: Tool execution pipeline and approval binding

- Status: Accepted
- Date: 2026-08-24
- Owners: Runtime maintainers

## Decision

Every v2 tool invocation follows one order:

`normalize -> route -> policy/evaluate -> resolve immutable execution plan -> approve -> sandbox -> execute -> observation`

The plan binds canonical tool name/version, normalized argument digest, workspace, policy version, execution mode,
network/mount capability and a nonce when approval is required. A permission-changing retry creates a new plan and
requires re-evaluation/approval.

Each invocation has a stable ID, side-effect classification and idempotency classification. Unknown tools,
malformed arguments, denial, cancellation and tool failure all commit a structured observation. Route two does
not add a durable five-state side-effect ledger or claim exactly-once execution.

## Consequences

The Engine does not branch on tool names. Readonly/verify are migrated before repair; repair continues to use the
route-one staged write-back controls.
