# CodexFlow.QueryRuntime.Engine

Standalone repository for the minimal CodexFlow QueryRuntime engine.

This repository was split from `CodexFlow.QueryRuntime.Engine` in the original `codexflow` repository. It contains the model-loop engine contracts and implementation, without the higher-level CodexFlow application, CLI, sandbox runners, or experimental harness projects.

## Build

```bash
dotnet build CodexFlow.QueryRuntime.Engine.slnx
```

## Scope

- `QueryRuntimeEngine` executes model/tool rounds.
- `RuntimeContracts` defines model client, request/result, event sink, and runtime event contracts.
- The only package dependency is `Microsoft.Extensions.AI`.

The broader QueryRuntime stack currently remains in `codexflow`:

- `CodexFlow.QueryRuntime.Abstractions`
- `CodexFlow.QueryRuntime.Cli`
- `CodexFlow.QueryRuntime.Experimental`
- `CodexFlow.QueryRuntime.Sandbox.LocalProcess`
- `CodexFlow.QueryRuntime.Sandbox.Docker`
- QueryRuntime unit and integration test projects
