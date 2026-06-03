# QueryRuntime Tool Partition Matrix

This document tracks how existing and new CodexFlow tools should be treated for
the QueryRuntime harness extraction.

Status legend:

- `harness-core`: should ship in the small local harness.
- `optional-tool-pack`: useful, but should live in a separate package or be
  disabled unless requested.
- `platform-only`: belongs to the full CodexFlow platform, not the local
  harness.
- `drop`: should not be part of the public harness surface.

## Phase 1 Tool Surface

| Tool | Partition | Profile | Capabilities | Status |
|---|---|---|---|---|
| `qre_list_files` | `harness-core` | `readonly`, `verify` | `read_fs` | Implemented |
| `qre_read_file` | `harness-core` | `readonly`, `verify` | `read_fs` | Implemented |
| `qre_search_files` | `harness-core` | `readonly`, `verify` | `read_fs` | Implemented |
| `qre_git_status` | `harness-core` | `verify` | `git_read`, `execute_process` | Implemented; trusted local only |
| `qre_git_diff` | `harness-core` | `verify` | `git_read`, `execute_process` | Implemented; trusted local only |
| `qre_dotnet_build` | `harness-core` | `verify` | `read_fs`, `write_artifacts`, `execute_process`, `build` | Implemented with `--no-restore` |
| `qre_dotnet_test` | `harness-core` | `verify` | `read_fs`, `write_artifacts`, `execute_process`, `run_tests`, `build` | Implemented with `--no-restore` |
| `qre sandbox exec` for known verify commands | `harness-core` | `verify` | command-specific capabilities | Implemented; maps command shape to current verify descriptors and policy |
| generic command execution | `optional-tool-pack` | `verify`, future `repair` | `execute_process`, plus command-specific capabilities | Not implemented; arbitrary commands still require command schema and approval |
| file write / patch apply | `harness-core` candidate | future `repair` | `write_fs`, `write_artifacts` | Not implemented |

## Existing Platform Tool Guidance

| Tool family | Partition | Reason |
|---|---|---|
| Web UI / SignalR session tools | `platform-only` | Depend on hosted app state and UI session semantics. |
| Identity, billing, account, email tools | `platform-only` | Not part of local runtime harness. |
| PostgreSQL, MongoDB, Redis, Qdrant administration tools | `platform-only` | Require private service topology and credentials. |
| Semantic recall / long-term memory tools | `optional-tool-pack` | Useful later, but must not be a default dependency. |
| MCP stdio tools | `optional-tool-pack` | Good plugin path, but needs manifest and capability policy. |
| package install / publish / deploy commands | `drop` from default profiles | Network, scripts, credentials, or external side effects require explicit approval and stronger sandboxing. |

## Phase 1 Boundary

Phase 1 keeps local process execution explicitly trusted-development-only. Tool
partitioning is not a security boundary; every command-capable tool must still
pass capability policy and, later, sandbox runner enforcement.
