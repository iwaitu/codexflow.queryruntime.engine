# ADR-002: Session, Turn and Step lifecycle

- Status: Accepted
- Date: 2026-08-24
- Owners: Runtime maintainers

## Decision

The v2 logical hierarchy is `Session -> Turn -> Step`.

- Session owns bounded history and at most one active Turn.
- Turn represents one user objective and owns terminal status, usage and ordered Steps.
- Step is one immutable execution snapshot covering model input, exposed tools, policy, environment, budget and
  history version.
- RunId remains a physical execution/artifact correlation value and is not a parent domain entity.

Step phases are explicit: `Preparing -> Sampling -> ResolvingTools -> ExecutingTools -> CommittingObservation`,
then either the next Step or a terminal Turn state. Illegal transitions fail closed.

## Consequences

Route two can expose typed IDs and deterministic reducers without promising persistence or resume. A future
RunAttempt type is added only if crash resume is approved.
