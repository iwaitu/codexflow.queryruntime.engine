# EmbeddedV2

Minimal .NET 10 integration using `Engine.V2.IAgentRuntime` directly, without a
CLI subprocess or CodexFlow platform dependency. Run from the repository root:

```bash
dotnet run --project examples/EmbeddedV2
```

The static model returns a fixed response without credentials or network calls.
The example constructs typed Session/Turn IDs, input history, policy/environment
snapshots and a budget, streams presentation events, and checks terminal status.
It exposes no tools and writes no persistent audit or checkpoint files.

For a live provider, supply an `IRuntimeModelClient` adapter (the Models project
provides `MeaiRuntimeModelClient`). Keep history/context preparation in QRE.
For tool execution, provide a frozen `ToolPipeline` and a plan-bound
`ToolApproval` implementation when required. For checkpoint recovery, see
`../H1CrashResume`; that example intentionally crashes its own process.
