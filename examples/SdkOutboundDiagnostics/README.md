# SdkOutboundDiagnostics

A self-contained, offline example demonstrating QRE's **SDK Outbound Diagnostics** system.

Nothing here opens an external network connection: requests are processed by an in-memory loopback handler, making this suite 100% deterministic and safe.

---

## 1. Defective-Host Demonstration

The demo simulates a flawed host whose options factory correctly forwards `Temperature`, but silently drops `MaxOutputTokens` and `RequireJsonObject`.

### Run the Demo
```bash
# macOS / Linux
dotnet run --project examples/SdkOutboundDiagnostics -- demo --workspace /tmp/qre-demo
qre diagnose latest --workspace /tmp/qre-demo --json

# Windows PowerShell
dotnet run --project examples/SdkOutboundDiagnostics -- demo --workspace ./temp_qre_demo
qre diagnose latest --workspace ./temp_qre_demo --json
```

`qre diagnose` pinpoints the loss at the `runtime_prepared -> adapter_prepared` boundary:
```text
maxOutputTokens 256 => absent
responseFormat json_object => none
```

### Export Sanitized Diagnostic Bundle
```bash
qre diagnose export latest --workspace ./temp_qre_demo --output ./diagnostics.zip
```
*(The exported zip bundle contains no API keys, credentials, or proprietary source code, making it safe to share for bug reports).*

---

## 2. Request Skeleton & Offline Rebuild

Generate a test fixture skeleton and rebuild the request offline:

```bash
# Extract request skeleton
qre diagnose skeleton latest --workspace ./temp_qre_demo --output ./fixture.json

# Test reviewed fixtures
qre diagnose rebuild examples/SdkOutboundDiagnostics/fixtures/defective-host.fixture.json  # Exit code 1 (failure detected)
qre diagnose rebuild examples/SdkOutboundDiagnostics/fixtures/fixed-host.fixture.json      # Exit code 0 (verified)
```

---

## 3. Host Integration Styles

```bash
dotnet run --project examples/SdkOutboundDiagnostics -- hosts
```

| Integration Style | Stages Observed | Recommended Usage |
|---|---|---|
| **Dedicated `HttpClient`** | `runtime_prepared`, `adapter_prepared`, `http_prepared` | Standard usage. The SDK manages and disposes the dedicated client. |
| **Shared Connection Pool** | `runtime_prepared`, `adapter_prepared`, `http_prepared` | High-throughput hosts. Wrap handlers via `diagnostics.CreateHandler(shared, disposeHandler: false)`. |
| **Custom `IChatClient`** | `runtime_prepared`, `adapter_prepared` | Mock or in-memory virtual clients where low-level HTTP transport capture is unavailable. |
