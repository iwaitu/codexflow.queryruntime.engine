# Changelog

All notable changes to the `CodexFlow.QueryRuntime.Engine` and
`CodexFlow.QueryRuntime.Models` packages are documented here. Versions follow
[Semantic Versioning](https://semver.org/); prerelease builds use
`-preview.N`.

## [0.25.4]

- Consume VllmChatClient **2.0.31** with native Sonnet 5.5 thinking and signed
  reasoning replay support. Preserve opaque reasoning data intact through context
  history; reject oversized data atomically with `opaque_reasoning_item_too_large`.
- Verify real SDK/protocol/Engine history round trips in all three streaming modes
  and non-retryable forced-tool rejection before HTTP. Native Sonnet callers must
  select Auto explicitly when they need a tool action.
- The initial comparison run uses user-authorized local NuGet builds while the
  public SDK package is validating; local provenance is recorded separately.

## [0.25.3]

- Consume the published VllmChatClient **2.0.30** package. Explicit DeepSeek V4
  family names, including `deepseek-v4.1-flash-1017`, select its V4 client;
  V3/R1 and unspecified families keep their existing client.
- The V4 adapter removes leaked `ds_safety` blocks from answer/reasoning text
  and outgoing reasoning history. Raw provider captures remain unchanged;
  tool names, invocation IDs and argument values are not filtered.
- Reverify outbound transport capabilities against SDK 2.0.30. The provider-level
  DeepSeek matrix is exercised with V3; V4 request shapes and filtering have
  separate model-specific tests rather than a blanket provider transform.
- SDK 2.0.29 failed three history-replay gates and was not adopted. Published
  2.0.30 passes regular/streaming Chat and Anthropic replay, including JSON-string
  reasoning and native assistant replay blocks. Opaque signed/redacted blocks
  remain untouched; Responses replay and inherited usage/signature/streamed-text
  mappings are not claimed as fixed.

## [0.25.2]

- Upgrade runtime and example references to published VllmChatClient **2.0.28**.
- Native Anthropic JSON Schema now reaches `output_config.format` in regular and
  streaming requests. Diagnostics recognize this path as the registered
  `anthropic_json_schema_output_config_format` transform rather than a lost format.
- Verify the capability matrix against SDK 2.0.28. Native schema-free JsonObject
  requests now fail before HTTP with `NotSupportedException`; supply a schema or
  remove the format. The adapter preserves schemas and does not rewrite unsupported
  constraints. Other API modes retain their existing behavior.
- With `ClassifyProviderErrors`, SDK `NotSupportedException` is a non-retryable protocol request
  rejection (`provider_request_rejected`), rather than a transport stream failure.
- Wait for local process termination after a successful timeout/cancellation kill
  before returning, avoiding Windows working-directory cleanup races.

## [0.25.1]

- Upgrade all runtime and example references to VllmChatClient **2.0.27**. Ordinary
  JSON-array message text now stays a string instead of becoming multimedia
  content parts; typed media and native tool history retain their protocols.
- Re-verify all 27 provider/API-mode capability cells against the published SDK
  and align the diagnostics matrix and offline fixtures with 2.0.27.
- No QRE agent-loop policy or public protocol changes.

## [0.25.0]

First published release containing the ia-engine bundle integration work
previously shipped only as the local `0.24.1-ia.20261019.2`–`.4` prereleases
(details below): opt-in context hard-cap protection, durable tool-call recovery,
single-count tool-budget admission, and safe finalization/persistence of
accounted tool-budget denials. Defaults are unchanged when the new options are
not enabled.

## [0.24.1-ia.20261019.4] (local prerelease, not published)

- With an explicit AfterToolCalls finalization policy, a fully accounted hard-cap
  denial can hand over to the existing tool-free final step. The cap, denied
  results, call/result pairs, remaining-step and deadline guards stay in force.
  Without the policy or with another fatal error, failure behavior is unchanged.
- Persist and resume that finalized tool-batch boundary only with a matching
  explicit policy, recorded ToolCallBudget reason, exact hard cap, a remaining
  step and valid denied/error pairs. Durable write/read/resume performs no tool
  replay; malformed or forged boundaries remain rejected. No API/schema change.

## [0.24.1-ia.20261019.3] (local prerelease, not published)

- Fix ToolPipeline budget admission counting a prepared invocation twice. The
  transition to Executing already reserves it in ToolCallCount. Batched calls
  can use exactly the declared limit; genuine overflow remains denied and each
  admitted invocation executes at most once. No API, limit or replay change.
- Fixed-input incident sequence 3/2/3/1/1/2 now reaches twelve tool calls and
  tool-free finalization; genuine overflow and recorded replay remain checked.

## [0.24.1-ia.20261019.2] (local prerelease, not published)

Opt-in additions for the ia-engine bundle integration. With none of them enabled,
requests, audit envelopes and checkpoints serialize byte-for-byte as in 0.24.0.

- **`RuntimeContextTruncationPolicy.FailClosedOnHardCaps`** (E1): any hard cap
  or large-result replacement of any item fails closed with
  `context_truncation_rejected` before the next model request; projection
  compaction that leaves the initial messages intact stays allowed.
  Normalization still happens at append time; the guarantee is that truncated
  evidence is never sent. Every non-`Allow` policy now persists and requires
  durable history normalization events.
- **`RuntimeToolCallRepairPolicy`** (E2 / Q6) via
  `RuntimeAgentLoopRequest.ToolCallRepair`: after `malformed_tool_call` or
  `invalid_tool_call_identity`, and only when the sample accepted no tool call,
  one tool-free JSON repair request per Step (repairs plus continuations at
  most `MaxPerTurn`, 1 or 2). The reservation is recorded in
  `RuntimeTurnProgress.ToolCallRepair`, audited and checkpointed (a repeated
  `StepPrepared`) before the request, so recovery never re-opens the quota.
  Only an exact registered `ReadOnly` tool with schema-valid arguments, or a
  tool-free final JSON object, is accepted; the call then runs through the
  normal tool pipeline. Enabling it writes audit and checkpoint schema 2
  (`RuntimeAuditSchema.EnhancedVersion`, `RuntimeCheckpointSchema.EnhancedVersion`);
  readers accept 1 and 2, 0.24.0 readers reject 2. New audit kind
  `ToolCallRepairAttempted` (reservation and outcome phases).
- **`RuntimeToolRegistry(tools, allowMixedCaseNames)`**: the one-argument
  constructor keeps lower-case names; `true` also accepts ASCII upper case and
  keeps the original name everywhere (no lower-casing; case-only collisions
  still rejected).
- **Nullable unions in tool schemas**: a `type` of exactly two distinct members,
  `null` plus one of object/array/string/integer/number/boolean, in either
  order. `null` matches the union (enum still applies); non-null values are
  validated recursively as before. Other unions are rejected at registration.

## [0.24.0]

Embedded structured-extraction support for hosts that run a read-only tool
conversation and need one schema-shaped answer (driven by the ia-engine bundle
review integration, see
[docs/ia-engine-integration-work-plan.zh-CN.md](docs/ia-engine-integration-work-plan.zh-CN.md)
and [ADR-010](docs/adr/ADR-010-embedded-structured-extraction.md)).

Every new capability is opt-in. With no new parameter set, requests, model
calls, retries, context projection, checkpoints and audit records are unchanged.

### Added

- **Host `AIFunction` tools** (`Models`): `RuntimeAIFunctionTool` and
  `RuntimeAIFunctionToolOptions` register an `AIFunction` as an Engine v2
  `IRuntimeTool` with declared side effect, idempotency, concurrency, limits,
  capabilities and sandbox. Results: `string` verbatim, `JsonElement` raw text,
  other values serialized with the function's `JsonSerializerOptions`; non-ASCII
  text is never `\uXXXX`-escaped. Exceptions other than caller cancellation
  become model-visible `ToolFailed` observations.
- **First-class structured output** (`Protocol`): `RuntimeModelParameters.ResponseFormat`
  with `RuntimeResponseFormat` (`Text` / `JsonObject` / `JsonSchema`) and
  `RuntimeResponseFormatScope` (`AllSteps` / `ToolFreeStepsOnly`). The Engine
  records the format in effect for each Step; `MeaiRuntimeModelClient` maps it
  to `ChatResponseFormat` and overrides the options factory's value. The
  `model_request` audit payload adds `responseFormatKind` and
  `responseFormatSchemaSha256` (public audit records the kind only), and
  recorded replay verifies both. Outbound diagnostics recognize `json_schema`
  at the Runtime stage and flag a schema downgraded to `json_object`.
- **Budget-driven tool finalization** (`Engine`): `RuntimeAgentLoopRequest.ToolFinalization`
  (`RuntimeToolFinalizationPolicy`) moves the Turn into tool-free Steps once a
  tool-call count, a fraction of a deadline, or the last Step of the budget is
  reached, appending a host instruction. Tool calls returned on a tool-free
  Step are ignored with a warning (`IgnoreAndUseText`) or rejected
  (`FailClosed`). The decision is stored in `RuntimeTurnProgress.FinalizationReason`,
  checkpoints and a new `ToolFinalizationTriggered` audit event, so recovery
  and replay never re-read the clock.
- **`RuntimeTerminationDecision.ContinueWithoutTools(feedback)`**: a continuation
  whose next Step exposes no tools (so a `ToolFreeStepsOnly` format applies),
  for repairing invalid final JSON. Recorded as a `TerminationDecided` audit
  event (`continue_without_tools`).
- **Provider error classification** (`Models`): `QreModelErrorClassifier` maps
  SDK exceptions to typed `RuntimeModelClientException` errors (429 rate limit,
  408/5xx/network/HTTP-timeout transport — retryable; 401/403 authentication,
  400/404/422 protocol — terminal). Enabled per client with
  `MeaiRuntimeModelClientOptions.ClassifyProviderErrors`.
  `QreModelHttpErrorCaptureHandler` records the HTTP status and `Retry-After`
  for SDKs that drop them (`VllmChatClient` 2.0.25 reports every HTTP error as
  `InvalidOperationException("Vllm error: …")`). `RuntimeError.RetryAfter` is new.
- **Model retry backoff** (`Engine`): `RuntimeAgentLoopRequest.ModelRetryPolicy`
  (`RuntimeRetryPolicy`) waits with exponential backoff, injectable jitter and
  `Retry-After` between retries, using the Runtime `TimeProvider`; each retry
  adds a `model_retry` warning to the Step output.
- **Non-streaming and empty-stream fallback** (`Models`):
  `MeaiRuntimeModelClientOptions.StreamingMode` = `Streaming` (default),
  `NonStreaming`, or `StreamingWithNonStreamingFallbackOnEmpty` (resends once
  without streaming when the stream ends with no content, warning
  `empty_stream_fallback`). Diagnostics distinguish streaming and
  non-streaming calls.
- **Context budget presets and truncation policy** (`Engine`):
  `RuntimeContextOptions.ForModelWindow(maxContextTokens)` and
  `RuntimeContextOptions.TruncationPolicy` (`Allow` / `FailClosedOnInitialMessages`
  / `FailClosedOnAny`, error `context_truncation_rejected`). See the technical
  guide §5.11.
- Example `examples/EmbeddedStructuredExtraction` (offline and live) and its
  regression test in `scripts/test-examples.py`.

### Compatibility and migration

- `VllmChatClient` is upgraded to **2.0.26** across the runtime and examples.
  Array-valued tool results remain strings on the wire, and streaming tool
  calls finish only after all content and usage have been emitted.
- Fail-closed context policies retain history normalization evidence in
  checkpoints, so resuming cannot bypass an earlier evidence truncation.
  Legacy checkpoints remain resumable under `Allow`; strict policies reject
  checkpoints without the required evidence with `checkpoint_context_history_missing`.
- A content-free transient failure during empty-stream fallback remains
  retryable. The fallback warning is emitted once the resend returns events.
- Structurally equal response-format schemas now have equal hash codes even
  when object properties or numeric spellings differ. The diagnostic SHA-256
  retains its existing property-order-sensitive definition.

- **Schema versions are unchanged** (`RuntimeAuditSchema`, `RuntimeCheckpointSchema`
  and `QueryRuntimeProtocolSchema` remain `1`). New fields are optional and
  omitted when unset, so data written without the new features is
  byte-identical and readable by 0.23 readers. Recordings that use
  finalization or `ContinueWithoutTools` contain the new audit event kinds
  `ToolFinalizationTriggered` / `TerminationDecided` (payload types
  `tool_finalization` / `termination_decision`), which 0.23 readers cannot
  parse; 0.24 reads all v1 data.
- No binary-breaking change: new members are `init` properties, new types,
  new enum members, and overloads (`RuntimeStateReducer.RecordContinuation(session, requiredToolName, withoutTools)`,
  `MeaiRuntimeModelClient(chatClient, optionsFactory, MeaiRuntimeModelClientOptions, …)`).
  Source note: a call that passes a literal `null` as the third
  `MeaiRuntimeModelClient` constructor argument is now ambiguous; name the
  argument (`diagnostics: null`).
- Code that switches over `RuntimeTerminationDecisionKind` or
  `RuntimeAuditEventKind` should handle the new members
  (`ContinueWithoutTools`, `ToolFinalizationTriggered`, `TerminationDecided`).
- `RuntimeModelParameters.RequireJsonObject` is kept with unchanged behavior
  (still interpreted by the host options factory). Prefer
  `ResponseFormat = RuntimeResponseFormat.JsonObject()`; setting both is
  rejected with `RuntimeRequestValidationException` (`response_format_conflict`),
  which derives from `ArgumentException`. It is not marked `[Obsolete]` in
  this release.
- When a Runtime `ResponseFormat` is set, `MeaiRuntimeModelClient` overwrites
  `ChatOptions.ResponseFormat` from the options factory; without one the
  factory value is kept.
- `RuntimeCheckpointRequestSnapshot` now captures `ToolFinalization`, so a
  checkpoint written with a finalization policy must be resumed with the same
  policy (`ToLoopRequest()` restores it).
- `CodexFlow.QueryRuntime.Experimental` (not packaged) now references `Models`
  and uses `RuntimeAIFunctionTool`; tool results and failure codes are unchanged.

## [0.23.2]

Log management and NuGet trusted publishing. See the git history for earlier
releases.
