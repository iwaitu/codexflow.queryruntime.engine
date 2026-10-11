# ADR-010: Embedded Structured Extraction

- Status: Accepted
- Date: 2026-10-11
- Owners: QueryRuntime maintainers
- Plan: [ia-engine integration work plan](../ia-engine-integration-work-plan.zh-CN.md)
- Verified dependency: `VllmChatClient` 2.0.25, `Microsoft.Extensions.AI` 10.10.0

## Context

ia-engine runs one read-only tool conversation per review rule and expects a
single JSON answer. Its hand-written loop already does things the V2 Runtime
could not: JSON Schema output only on tool-free rounds, a forced tool-free
"answer now" round when the tool budget or the deadline is nearly spent, a
tool-free repair round for invalid JSON, backoff on transient provider errors,
non-streaming fallback, and large evidence that must not be cut silently.
Replacing that loop with `RuntimeAgentLoop` must keep audit, replay and
recovery guarantees (ADR-004, ADR-008) and must not change the behavior of
existing hosts.

## Decision

1. **Everything is opt-in.** Each capability is a new nullable/`init` member
   whose default reproduces the 0.23 behavior byte for byte: requests, model
   calls, retries, context projection, checkpoints and audit records. New
   optional JSON members are omitted when unset (`WhenWritingNull`, or
   `WhenWritingDefault` for the `NextStepWithoutTools` flag), so schema
   versions stay at 1 and fingerprints of existing requests are unchanged.
2. **Decisions in the Engine, mappings in Models.** Protocol gains the
   provider-free `RuntimeResponseFormat`. `RuntimeAgentLoop` decides the format
   in effect for each Step (`ToolFreeStepsOnly` is withheld while tools are
   exposed) and records that effective value in the Step request, so audit,
   checkpoints and replay observe the real request. `MeaiRuntimeModelClient`
   only maps it to `ChatResponseFormat`. Engine/V2 and Protocol stay free of
   Microsoft.Extensions.AI.
3. **Clock- and environment-driven branches are recorded, not recomputed.**
   Tool finalization is evaluated once after each committed tool batch using
   the Runtime `TimeProvider`; when it triggers, the reason is written to
   `RuntimeTurnProgress.FinalizationReason` before the `ToolBatchCommitted`
   checkpoint and emitted as `ToolFinalizationTriggered`. Recovery reads the
   state, never the clock; finalization is sticky for the rest of the Turn and
   triggers at most once. `ContinueWithoutTools` sets a one-shot
   `NextStepWithoutTools` flag that is cleared when the next Step commits its
   output, so a crash between preparing and committing that Step keeps it
   tool-free on resume.
4. **New decisions get new audit events; legacy ones do not.** The two new
   event kinds (`ToolFinalizationTriggered`, `TerminationDecided` for
   `continue_without_tools`) appear only when the new features are used, so
   0.23 recordings and readers are unaffected. `RuntimeRecordedReplay` (the
   strict, data-only replay) validates them: finalization must follow a
   committed tool batch and occur once, every later model request must expose
   no tools, and the audited response format kind and schema digest must match
   the recorded request.
5. **Tool calls on a tool-free Step never execute.** By policy they are either
   dropped with a `*_tool_calls_ignored` warning while the text answer is kept,
   or rejected; a Step with only ignored calls and no text fails closed
   (`*_empty`). The committed model output is the filtered one, so audit and
   replay stay consistent.
6. **Error classification is evidence-based and opt-in.** `VllmChatClient`
   2.0.25 reports every HTTP error as `InvalidOperationException("Vllm error:
   <body>")` without a status code (verified against 400/401/403/404/408/429/500/503
   in both Chat Completions and Responses modes, streaming and not). The status
   therefore comes from, in order: the exception
   (`HttpRequestException.StatusCode`), `QreModelHttpErrorCaptureHandler`
   (scoped to the model call with the same `AsyncLocal` pattern as ADR-009),
   or the `EnsureSuccessStatusCode` message. Unlike the diagnostics sidecar,
   the classifier may read exception messages to recognize authentication
   failures and malformed tool-call errors; it never persists them beyond the
   `RuntimeError` message the Runtime already recorded. Classification changes
   error categories and retryability, so it is enabled per client
   (`ClassifyProviderErrors`).
7. **Backoff stays inside the existing retry contract.** Retries still require
   a retryable error, no events from the failed attempt, and remaining
   `MaxModelRetries`. `RuntimeRetryPolicy` only adds the wait (exponential,
   capped, injectable jitter, `Retry-After` up to `MaxDelay`) through the
   Runtime `TimeProvider`, and records a `model_retry` warning per retry in
   the successful Step's output.
8. **Context truncation is observable and can fail closed.** Budget-driven
   truncation (item hard caps, large-result replacement, compaction) already
   emits context events with item ids. `TruncationPolicy` checks them after
   each Step's projection is prepared and before the model is called, so
   rejected evidence never reaches the model. Default event kinds are kept to
   preserve existing output. `RuntimeTokenEstimator` is unchanged because it
   feeds replay and checkpoints; Chinese-heavy hosts reserve a larger margin
   instead (technical guide §5.11).

## Consequences

- ia-engine can replace its hand-written loop with `RuntimeAgentLoop` +
  `MeaiRuntimeModelClient` and gains audit, recorded replay, checkpoints and
  outbound diagnostics with equivalent behavior.
- Recordings that use finalization or `ContinueWithoutTools` need a 0.24
  reader. A checkpoint written with a finalization policy is resumable only
  with the same policy because it is part of the request fingerprint.
- Hosts using `VllmChatClient` must add `QreModelHttpErrorCaptureHandler` to
  their `HttpClient` to get status-based classification; without it, only
  network failures, timeouts and message-recognizable errors are classified.
- Malformed tool-call repair (plan item Q6) is deferred; the classifier
  already labels the trigger (`malformed_tool_call`) for a later release.
