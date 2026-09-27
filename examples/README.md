# QRE v2 Integration Examples

This directory provides working, production-grade integration examples demonstrating how to embed or drive the **CodexFlow QueryRuntime (QRE)** in various programming languages, hosting styles, and operational scenarios.

All examples adhere to the **0.23.0 stable, v2-only** runtime architecture.

---

## Examples Catalog

| Example | Technology / Language | Primary Concepts Demonstrated | Execution Mode |
|---|---|---|---|
| **[EmbeddedV2](EmbeddedV2)** | C# / .NET 10 | Direct in-process `IAgentRuntime` embedding, typed Turn requests, and real-time event streaming (`TextDelta`, `ReasoningDelta`, `ToolCallRequested`). | Offline static model (no network needed) |
| **[RepoDoctor](RepoDoctor)** | C# / .NET 10 CLI | Cross-platform CLI host calling `qre` CLI subprocess, custom C# stdio tool registration, live streaming, and strict offline replay. | Offline smoke / Live provider |
| **[PythonToolDoctor](PythonToolDoctor)** | Python 3.9+ | Python subprocess host driving `qre`, enforcing required built-in tools (`qre_list_files`), and validating via strict replay. | Offline smoke / Live provider |
| **[ExternalTools](ExternalTools)** | Python / Stdio | External tool manifest definition (`.qre/tools/*.json`), process isolation, and plan-bound approval (`--approve-risk`). | Model-invoked tool |
| **[PythonFunctionTools](PythonFunctionTools)** | Python 3.9+ | Python `@qre_tool` decorator, automatic JSON Schema generation, and workspace path injection. | Manifest generator & tool runner |
| **[NodeFunctionTools](NodeFunctionTools)** | Node.js (ESM) | Native JavaScript `qreTool` helper, zero npm dependencies, and automatic manifest generation. | Manifest generator & tool runner |
| **[H1CrashResume](H1CrashResume)** | C# / .NET 10 | Process crash injection (`Environment.FailFast`) and same-version atomic checkpoint recovery with attempt leases. | Fault injection & recovery |
| **[SdkOutboundDiagnostics](SdkOutboundDiagnostics)** | C# / .NET 10 | SDK client-side outbound diagnostics: defective options factory demo, connection pool isolation, and offline request rebuilds. | Diagnostic capture & rebuild |
| **[SkillsWorkflow](SkillsWorkflow)** | C# / .NET 10 | Skill packaging (`SKILL.md`), progressive two-phase metadata discovery, and automated `ListSkillFiles`/`ReadSkillFile` tool injection. | Offline self-test / Live provider |
| **[StructuredOutputsJsonSchema](StructuredOutputsJsonSchema)** | C# / .NET 10 | JSON Schema requests with actual HTTP body checks, response validation and POCO deserialization; enforcement depends on the backend. | Offline self-test / Live provider |

---

## Quickstart & Prerequisites

- **.NET 10 SDK** (required for .NET projects and building the CLI)
- **Python 3.9+** (for Python examples and regression runner)
- **Node.js 18+** (for Node function tools example)
- **Docker** (optional, required only when testing `--runner docker`)

### 1. Build Solution and Examples
From the repository root:

```bash
# Build the QRE core runtime, CLI, and C# example projects
dotnet build CodexFlow.QueryRuntime.slnx
dotnet build examples/RepoDoctor/RepoDoctor.csproj
dotnet build examples/EmbeddedV2/EmbeddedV2.csproj
dotnet build examples/SdkOutboundDiagnostics/SdkOutboundDiagnostics.csproj
dotnet build examples/H1CrashResume/H1CrashResume.csproj
```

### 2. Run All Examples in Regression Mode
Run the automated test runner to verify all examples offline (using an in-process mock provider):

```bash
python scripts/test-examples.py
```

The test runner automatically detects the built `qre` executable on all platforms (Windows, Linux, macOS) and requires no live provider credentials.

---

## Individual Example Highlights

### In-Process Embedding vs Subprocess CLI Host
- If you are building a .NET application and want zero-overhead in-memory orchestration, look at **[EmbeddedV2](EmbeddedV2)**.
- If you are writing a host in Python, Go, Node.js, or want process-level decoupling, look at **[RepoDoctor](RepoDoctor)** or **[PythonToolDoctor](PythonToolDoctor)**.

### Custom Tool Development
- To write external tools without linking third-party code into the Native AOT CLI binary, see **[ExternalTools](ExternalTools)**.
- To expose existing Python or Node functions with automatically generated manifests, see **[PythonFunctionTools](PythonFunctionTools)** and **[NodeFunctionTools](NodeFunctionTools)**.

### Resilience & Diagnostics
- To understand how QRE guarantees zero state loss during crashes, see **[H1CrashResume](H1CrashResume)**.
- To inspect or troubleshoot arguments lost by model SDK serializers, see **[SdkOutboundDiagnostics](SdkOutboundDiagnostics)**.
