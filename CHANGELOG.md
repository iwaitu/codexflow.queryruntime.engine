# Changelog

All notable changes to the `CodexFlow.QueryRuntime.Engine` and
`CodexFlow.QueryRuntime.Models` packages are documented here. Versions follow
[Semantic Versioning](https://semver.org/); prerelease builds use
`-preview.N`.

## [0.24.0-preview.1] - Unreleased

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
