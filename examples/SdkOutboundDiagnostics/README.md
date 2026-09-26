# SdkOutboundDiagnostics

Offline example for QRE outbound SDK diagnostics. Nothing here opens a network
connection: the SDK sends into an in-process terminal handler.

## Defective-host demo

The demo host's options factory forwards `Temperature` but forgets
`MaxOutputTokens` and `RequireJsonObject`. The defect exists only in this example;
the CLI mapping is correct.

```bash
dotnet build CodexFlow.QueryRuntime.Cli
dotnet run --project examples/SdkOutboundDiagnostics -- demo --workspace /tmp/qre-demo
qre diagnose latest --workspace /tmp/qre-demo
```

`diagnose` reports the first loss at `runtime_prepared -> adapter_prepared`
(`maxOutputTokens 256 => absent`, `responseFormat json_object => none`). Then:

```bash
qre diagnose export latest --workspace /tmp/qre-demo --output /tmp/qre-demo.zip
qre diagnose skeleton latest --workspace /tmp/qre-demo --output /tmp/fixture.json
```

The skeleton contains `<<fill: ...>>` placeholders and `status: skeleton`; rebuild
refuses it (exit 2) until a person fills in synthetic, non-sensitive content,
reviews it and sets `status: runnable`. The two reviewed fixtures in `fixtures/`
show the result:

```bash
qre diagnose rebuild examples/SdkOutboundDiagnostics/fixtures/defective-host.fixture.json  # exit 1
qre diagnose rebuild examples/SdkOutboundDiagnostics/fixtures/fixed-host.fixture.json      # exit 0
```

`passthrough-empty` stands in for a host that maps nothing; `qre-cli` is the CLI
mapping. Hosts can pass their own options factory to the rebuilder API instead.
Run `demo --fixed` to see the corrected demo host produce no Runtime -> adapter loss.

## Host integration styles

```bash
dotnet run --project examples/SdkOutboundDiagnostics -- hosts
```

| Style | What is observed | Ownership |
| --- | --- | --- |
| QRE-owned `HttpClient` | Runtime, adapter and HTTP (handler-visible) | Dedicated to the model client; the locked SDK disposes it with the chat client |
| Host-shared connection pool | Runtime, adapter and HTTP | The host keeps its handler; each model client gets its own `HttpClient(diagnostics.CreateHandler(shared), disposeHandler: false)` |
| Custom `IChatClient` | Runtime and adapter only (`transport_capture_unavailable`) | Host-owned |

Passing a shared `HttpClient` instance itself to the locked SDK is **unsupported**:
disposing the chat client disposes that `HttpClient`, and the SDK writes auth and
default headers onto it. Add the diagnostic handler while building a pipeline;
it cannot be inserted into an already built `HttpClient`.

`MinimalDiagnosticSink` writes the same `manifest.json` + `events.jsonl` contract
the CLI store uses, so `qre diagnose` can read it. Production embedders should
also apply private permissions, a bounded queue, quotas and retention.
