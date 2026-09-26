# ADR-009: SDK Outbound Diagnostics Evidence Chain

- Status: Accepted
- Date: 2026-09-26
- Owners: QueryRuntime maintainers
- Plan: [SDK outbound diagnostics plan](../sdk-outbound-diagnostics-plan.zh-CN.md)
- Verified dependency: `VllmChatClient` 2.0.25, `Microsoft.Extensions.AI` 10.10.0

## Context

A model call passes through four transformations: the Runtime request, the
host's options factory, the MEAI adapter (tool declarations and `ToolMode`), and
the SDK's HTTP serialization. Audit events describe only the first. Failures such
as a host forgetting to map `MaxOutputTokens`, an SDK dropping `tool_choice`, or
a required tool name that differs from its declaration only by case could not be
located, compared, or turned into offline regression cases.

## Decision

1. **Authoritative attempt information comes from the Runtime.** Protocol adds
   the optional, provider-free `IRuntimeModelAttemptClient` with a
   `StreamAsync(request, RuntimeModelAttemptContext, ct)` overload. After
   `RecordModelAttempt`, `RuntimeAgentLoop` passes the reducer's `ModelAttempts`
   and the run attempt id to clients that implement it; legacy clients keep the
   original call. `PresentingModelClient` implements the interface for both
   `RunAsync` and `ResumeAsync` and forwards the context only when its inner
   client supports it. Clients never count attempts themselves; an absent
   ordinal is recorded as `unavailable`, never defaulted to 1.
   `RuntimeModelRequest`, checkpoint schema and retry semantics are unchanged.
2. **Three observation points, one semantic shape.** `runtime_prepared`
   (Runtime request), `adapter_prepared` (final options after Tools/ToolMode are
   set) and `http_prepared` (the serialized body, observed while the transport
   writes it). All are projected to the same allow-listed `QreSemanticRequest`.
3. **Correlation.** An `AsyncLocal` scope is set only around each SDK
   `MoveNextAsync`/`DisposeAsync`. The diagnostic `DelegatingHandler` snapshots
   it once at `SendAsync` entry and binds the snapshot explicitly to the request
   observer, response stream wrapper and end callback. Unscoped sends are
   `uncorrelated`; ownership is never guessed.
4. **Evidence, not inference.** Failure phases and classifications come from
   independent observations (handler status, stream wrapper, exception type,
   upstream token state). `HttpClient.Timeout` is classified by the
   `OperationCanceledException` + inner `TimeoutException` type chain in the
   adapter while the upstream token is not cancelled; the handler cannot see
   that chain and reports `unknown`. Exception messages are never read or stored.
5. **Placement (ADR-003/004).** Models contains DTOs, sink interface, scope,
   projection and handler only. The CLI contains storage, normalizer/comparer,
   exporter and rebuilder. Diagnostics are an independent sidecar under
   `.qre/v2/diagnostics/`; no `RuntimeAuditEventKind` is added and replay or
   checkpoint recovery never depends on the sidecar.
6. **Best effort.** Diagnostics failures never change model calls or run exit
   codes. Bounded queue, per-record, per-run and total storage quotas apply;
   drops, oversize records, projection failures and flush timeouts are reported
   as `evidence_incomplete`.
7. **Shared HttpClient instances are unsupported.** See evidence E3. Hosts share
   a handler/connection pool instead, giving each model client its own
   `HttpClient(..., disposeHandler: false)`.

## P0 evidence

All facts below are asserted by tests against the locked dependency, so an SDK
upgrade that changes them fails CI instead of silently overstating coverage.

| # | Finding | Evidence |
| --- | --- | --- |
| E1 | Every selector-accepted Provider × API-mode cell sends through the injected `HttpClient`, once per streaming call, to the mode's route. Gemini accepts ChatCompletions only; the other two Gemini cells are rejected by the selector. | `SdkTransportCapabilityTests.Cell_MatchesLockedSdkBehavior` (27 cells) |
| E2 | Request content is `JsonContent` (deferred serialization) with no computed length; on HTTP/1.1 it is sent chunked, on HTTP/2 without `Content-Length`. The observing wrapper preserves bytes and framing in both. | `SdkTransportCapabilityTests`, `OutboundTransportEquivalenceTests` (loopback Kestrel) |
| E3 | `IChatClient.Dispose` disposes the injected `HttpClient`, and the SDK writes auth/default headers onto it. | `Cell_MatchesLockedSdkBehavior` |
| E4 | The streaming path has no retry: an HTTP 500 produces exactly one send. The SDK's malformed-tool-call retry exists only in non-streaming `GetResponseAsync`, which QRE does not use. | `StreamingPath_DoesNotRetryHttpFailureAndPutsErrorBodyIntoExceptionMessage` |
| E5 | The SDK copies the provider error body into `InvalidOperationException.Message`. | same test |
| E6 | Streaming surfaces a finish reason only for tool calls; text completions never carry `Stop`, so the adapter reports `missing_provider_finish_reason` for every text response through this SDK. | `StreamingPath_SurfacesToolCallFinishReasonOnly`, `Cell_MatchesLockedSdkBehavior` |
| E7 | Required tool names reach the wire with their original case. | `ChatCompletions_PreservesRequiredToolNameCaseExactly`, CLI case demo |
| E8 | Registered request rewrites: chat temperature nested under `options`, legacy `format` and `structured_outputs` fields, `max_tokens: 8192` default for Claude chat and all Anthropic cells, Anthropic system hoisting, gpt-oss leading system prompt. | `Cell_MatchesLockedSdkBehavior`, `QreDiagnosticsAnalysisTests` |
| E9 | Observed SDK losses: Gemini chat drops `max_tokens`, `response_format` and `tool_choice`; DeepSeek drops `response_format` (chat and responses); Anthropic mode has no JSON format field. These stay `unexpected_change` (tagged `known_sdk_limitation` / `unsupported_constraint`). | same |
| E10 | Recovery boundary: a `StepPrepared` checkpoint persists `ModelAttempts = 0`, so the recovered Step samples again with ordinal 1. The ordinal repeats across recovery; run attempt + segment + model call id identify a call. | `RuntimeModelAttemptContractTests.Facade_ResumeAsync_PassesContextAndOrdinalRestartsFromRecoveredState` |

## Consequences

- R7 review fixes use projection and normalizer version 2. Comparisons require
  observed structures, mapped package-local identities, complete call alignment,
  and evidence from every HTTP attempt. Readers verify record counts, sequence
  integrity and span closure independently of a manifest's completion claim.
  Explicit JSON null remains present; invalid scalar types are marked invalid.
  Version 1 evidence remains readable but is not certified by the new rules.
- Queue capacity uses non-blocking TryWrite with Wait full mode: rejected records
  release their byte reservations immediately. DropWrite must not be used without
  accounting for its successful return on a dropped item.

- The capability matrix (`QreTransportCapabilityMatrix`) marks accepted cells
  `verified` only for SDK 2.0.25; any other SDK version reports `unverified`
  and adapter→HTTP rules become `not_comparable`.
- E6 means text-only fixtures cannot demonstrate a provider-surfaced `Stop`
  through this SDK, even though the offline transport sends one.
- Out-of-scope follow-ups: Runtime raw exception message governance (E5),
  shared `ChatOptions` ownership, resolving an accepted `RequiredToolName` to the
  declared canonical name, an SDK ownership switch for injected clients (E3),
  surfacing text finish reasons (E6) and the Gemini/DeepSeek losses (E9).
