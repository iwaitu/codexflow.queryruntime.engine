# SDK Outbound Diagnostics

Outbound diagnostics connect one model call from the QRE Runtime request, through
the host options factory and MEAI adapter, to the request the model SDK hands to
HTTP. They let you locate where a constraint was lost, compare runs, export a
package that contains no secrets or bodies, and turn a case into an offline
regression fixture. Design and evidence: [ADR-009](adr/ADR-009-sdk-outbound-diagnostics.md).
Chinese version: [sdk-outbound-diagnostics.zh-CN.md](sdk-outbound-diagnostics.zh-CN.md).

Diagnostics are **off by default**. They are client-side observations: they show
what passed the diagnostic handler, not network bytes or server receipt.

## CLI

```bash
qre run --sdk-diagnostics structure --api-url ... --model ... "task"
qre diagnose latest --workspace . [--json]
qre diagnose inspect <run|bundle.zip> [--json]
qre diagnose compare <left> <right> [--step-map L=R] [--alias-map L=R] [--json]
qre diagnose export <run|latest> --output bundle.zip [--force]
qre diagnose skeleton <run|latest> --output fixture.json [--call mc-0001]
qre diagnose rebuild fixture.json [--json]
```

| Mode | Captures | Never captures |
| --- | --- | --- |
| `off` (default) | nothing; no files, scopes or content wrappers | - |
| `metadata` | correlation ids, route template, status, content-type category, retry-after, stream termination, model outcome | request bodies |
| `structure` | metadata plus an allow-listed projection of the Runtime request, final adapter options and the serialized HTTP body | prompts, arguments, response bodies, headers, URLs, plaintext tool/model names, exception messages |

`qre run --json` gains an optional `diagnostics` summary; existing fields keep
their meaning and diagnostic failures never change the run exit code.
`diagnose` is read-only and offline; `rebuild` uses an in-memory terminal
transport and never resends a request. `compare` and `rebuild` exit with 0
(verified), 1 (unexpected difference) or 2 (invalid input, insufficient evidence
or unsupported capability).

## Observation points and records

| Stage | When | Event |
| --- | --- | --- |
| `runtime_prepared` | adapter receives the `RuntimeModelRequest` | `model_call_started` |
| `adapter_prepared` | after the options factory and after Tools/ToolMode are set | `adapter_prepared` |
| `http_prepared` | the handler forwards; the body is observed as the transport writes it | `http_attempt_started`, `request_structure_observed` |
| response | headers and stream end | `http_headers_received`, `http_attempt_ended` |
| model call | adapter finishes, fails or is abandoned | `model_call_ended` |

Identifiers: `RuntimeModelAttemptOrdinal` (authoritative, from the Runtime; or
`unavailable`), `ModelCallId` (one adapter `StreamAsync`), `HttpAttemptId` and
`AttemptOrdinal` (each handler-visible send), `RunAttemptAlias`, `SegmentId`,
`StepAlias`. Capture status is `complete`, `partial`, `omitted`, `unsupported`,
`failed` or `not_enabled`; field state is `present`, `absent`, `redacted` or
`unobserved`. HTTP success, stream completion and model-protocol completion are
reported separately.

## Comparison rules

Cross-layer findings are `expected_transform`, `unexpected_change`,
`not_comparable` or `insufficient_evidence`. A field is reported lost only when
both sides were completely observed and a rule supports the combination.

| Situation | Classification |
| --- | --- |
| No tools, no required tool, adapter `ToolMode.None` | expected_transform |
| No tools but a required tool, adapter `ToolMode.None` | unexpected_change + `input_constraint_conflict` |
| Required name differs from the declaration only by case | unexpected_change + `case_only_mismatch` (Runtime → adapter) |
| Explicit Temperature / MaxOutputTokens / RequireJsonObject not mapped | unexpected_change |
| `ModelId` empty, SDK falls back to an equivalent descriptor model | expected_transform; unknown default source → not_comparable |
| `double` → `float` temperature within float precision | expected_transform |
| Registered SDK rewrite (see matrix) | expected_transform |
| Observed SDK loss of an explicit constraint | unexpected_change + `known_sdk_limitation` |
| Protocol cannot carry the constraint (JSON format on Anthropic) | unexpected_change + `unsupported_constraint` |
| Unverified Provider × API-mode cell | not_comparable |

`RequireJsonObject = false` equals "unspecified". Tool and model names are
package-local, case-sensitive aliases; cross-package alias comparison needs an
explicit `--alias-map`. The earliest cross-run difference is not a proven root cause.

## Capability matrix (VllmChatClient 2.0.25)

All accepted cells: injected transport used, one handler-visible send per
streaming call, no streaming retry, deferred `JsonContent` with unknown length,
the SDK disposes the injected `HttpClient` and writes default headers onto it,
and only tool-call finish reasons are surfaced.

| Provider | chat_completions | responses | anthropic_messages |
| --- | --- | --- | --- |
| openai-gpt-oss | verified (injected system prompt) | verified | verified |
| openai-gpt, claude, kimi, minimax, glm, qwen | verified | verified | verified |
| gemini | verified; drops `max_tokens`, `response_format`, `tool_choice` | unsupported | unsupported |
| deepseek | verified; drops `response_format` | verified; drops `response_format` | verified |

Anthropic cells default `max_tokens` to 8192 and have no JSON response format.
Other SDK versions report every cell as `unverified`. Sharing one `HttpClient`
instance with the SDK is **unsupported**; share a handler instead.

## Embedding

```csharp
var diagnostics = QreOutboundDiagnostics.Create(
    new QreOutboundDiagnosticsOptions { Mode = QreOutboundDiagnosticMode.Structure },
    mySink);                                         // IQreOutboundDiagnosticSink
var http = new HttpClient(diagnostics.CreateHandler(new SocketsHttpHandler()));
var selector = QreModelProviderSelector.CreateDefault();
var descriptor = new QreModelClientDescriptor { /* ... */ HttpClient = http };
var client = new MeaiRuntimeModelClient(
    selector.CreateClient(descriptor),
    request => new ChatOptions { /* new instance per call */ },
    diagnostics,
    QreOutboundDiagnosticsTarget.ForProvider(selector.Select(descriptor.Model), descriptor.ApiMode));
```

The sink receives already-projected records and must not block. See
[examples/SdkOutboundDiagnostics](../examples/SdkOutboundDiagnostics) for the
QRE-owned client, shared-handler and custom `IChatClient` shapes and a minimal
sink that writes the CLI package contract.

## Storage and safety

`.qre/v2/diagnostics/<run>/` holds `manifest.json` (atomic), `events.jsonl`
(flushed per record; a truncated tail is reported) and `local-index.json`
(restricted local link to the audit run, never exported). Directories and files
are owner-only. Defaults: 64 KiB request capture, 32 KiB per record, 8 MiB per
run, 256 records / 2 MiB queued, JSON depth 32, 7-day retention (maximum 30),
100 runs / 128 MiB total. Export re-projects every record, regenerates aliases
and writes only `manifest.json` and `events.jsonl`. Readers reject path escapes,
links, unexpected or oversized entries, compression bombs and unknown schemas.

## Known limits

- Coverage is `handler_visible`: redirects, authentication retries and
  retransmits below the handler are not observed.
- Response bodies are never captured; a missing record does not mean no request
  was sent; memory buffered at a crash may be lost.
- The Runtime and CLI may still print raw provider exception messages, which the
  locked SDK fills with error bodies. Diagnostics never store them, but that does
  not make all program output redacted.
- Diagnostics cannot make a shared, mutable `ChatOptions` instance race-free.
- Strict recorded replay never calls the SDK; passing replay does not prove SDK
  serialization. Use `diagnose rebuild` for that.
- `ChatClientExperimentalModelClient` is not instrumented.
