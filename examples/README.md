# QRE v2 examples

Run commands from the repository root with .NET 10. CLI examples need `qre` on
PATH or `QRE_BIN` pointing to the built executable.

| Example | Integration |
| --- | --- |
| [EmbeddedV2](EmbeddedV2) | Direct .NET `IAgentRuntime` embedding; deterministic offline model |
| [RepoDoctor](RepoDoctor) | .NET CLI host, direct custom tool invocation, streaming and strict v2 replay |
| [PythonToolDoctor](PythonToolDoctor) | Python CLI host, required built-in tool and strict v2 replay |
| [ExternalTools](ExternalTools) | Minimal stdio tool manifest and approved model invocation |
| [PythonFunctionTools](PythonFunctionTools) | Python function tools and manifest generation |
| [NodeFunctionTools](NodeFunctionTools) | Node.js function tools and manifest generation |
| [H1CrashResume](H1CrashResume) | Intentional process crash and same-version v2 checkpoint recovery |

## Offline regression checks

Requires Python 3.9+ and Node.js in addition to .NET. No provider credentials or
external model calls are used. A loopback HTTP fixture exercises the real CLI
model adapter, tool approval, execution and replay paths.

```bash
dotnet build CodexFlow.QueryRuntime.Cli
dotnet build examples/RepoDoctor
dotnet build examples/EmbeddedV2
python3 scripts/test-examples.py
```

Set `QRE_EXAMPLE_CONFIGURATION=Release` when using Release builds. H1's intentional
crash/recovery check is documented separately. These examples target trusted
local workspaces: stdio execution and example path checks do not provide hostile
multi-user filesystem isolation.
