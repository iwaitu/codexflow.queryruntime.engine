# ToolSearch Lazy Tool Activation

`tool_search` is QRE's opt-in lazy tool activation layer. It keeps the initial
tool schema small, lets the model search by capability, and injects activated
tools on later runtime rounds.

## Runtime Shape

ToolSearch has three layers:

1. Capability metadata: compact discovery fields on `QueryRuntimeToolDescriptor`.
2. Search index: safe text search by default, explicit `regex:` only when needed.
3. Activation: TopK, stage, active-state, and risk-aware deferred tool activation.

The engine supports this through a dynamic tool provider. Each round resolves the
current active tool set, so a `tool_search` call in round N can expose newly
activated tools in round N+1.

When enabled, the prompt includes a compact deferred-tool directory. Deferred
tools are visible by name and capability, but their full schemas are not loaded
until activation.

## CLI Usage

```bash
qre run --workspace . --profile readonly --tool-search \
  "Find the runtime entry points."
```

Useful options:

- `--tool-search` starts with the `tool_search` meta tool and defers profile tools.
- `--tool-search-top-k <n>` limits search hits and activation count. Default: `5`.
- `--external` can be combined with tool search; external manifest tools become
  searchable deferred candidates.

Without `--tool-search`, QRE keeps the existing behavior and injects profile tools
directly.

## Library Usage

```csharp
var result = await runtime.RunAsync(
    new QueryRuntimeHostRequest
    {
        InitialMessages = history,
        WorkspacePath = workspacePath,
        ToolProfile = QueryRuntimeToolProfile.ReadOnly,
        EnableTools = true,
        ToolSearch = new QueryRuntimeToolSearchOptions
        {
            Enabled = true,
            TopK = 3,
            AlwaysOnToolNames = ["qre_list_files"]
        }
    },
    ct);
```

`AlwaysOnToolNames` keeps selected tools visible from round 1.
`DeferredToolNames` forces selected tools behind `tool_search`.

When a host supplies `InitialMessages`, QRE prepends a small system message with
the ToolSearch instructions and deferred-tool directory. The host's existing
messages remain intact after that QRE-owned discovery message.

## Search Semantics

Plain queries are ordinary keyword searches:

```json
{"query":"read file","top_k":3}
```

If the model already knows the exact deferred tool name from the prompt catalog,
it can select it directly:

```json
{"query":"select:qre_read_file"}
```

Regex is only enabled with the `regex:` prefix:

```json
{"query":"regex:^qre_git_","top_k":3}
```

Search results include:

- `score`
- `matched`
- `capability`
- `requiredArgs`
- `optionalArgs`
- `risk`
- `availableNow`
- `activated`
- `reason`

ToolSearch returns compact invocation guidance, not the full tool schema.

## Activation Rules

- Read-only and command verification tools can be activated from matching queries.
- Local write tools require explicit write intent such as `write`, `edit`, `patch`,
  `apply`, or `modify`.
- Stage-unavailable tools are not activated unless returned for diagnostics with
  `include_unavailable`.
- Already-active tools are marked as such and are not reactivated.
- `RequiredToolName` is kept visible from round 1 so provider APIs never receive
  a required tool mode for a tool whose schema is not declared.

This keeps the model's active tool surface small while still allowing large
profile, plugin, and external-tool catalogs to be discoverable.

## Provider Cache Note

Claude Code on Anthropic can preserve prompt-cache prefixes by using
provider-specific `tool_reference` blocks and `defer_loading` markers. QRE's
current implementation is provider-neutral: after activation, the next round
injects the activated `AIFunction` schema through `ChatOptions.Tools`.

That means QRE has the same functional lazy-activation behavior, but it does not
yet implement Anthropic-specific cache preservation. A future provider adapter can
map QRE activation events to native `tool_reference` / `defer_loading` semantics
when the selected provider supports them.
