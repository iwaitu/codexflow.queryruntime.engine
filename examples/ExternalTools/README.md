# ExternalTools

This example shows how to register a new out-of-process QRE tool without linking
third-party code into the Native AOT CLI.

## Register

Install the manifest into the workspace-local registry at `.qre/tools`:

```bash
qre tool register --workspace . --manifest examples/ExternalTools/echo_tool.manifest.json
```

The command copies the manifest to `.qre/tools/demo_echo_tool.json`. Re-run with
`--force` when you intentionally want to overwrite an existing registration.

The manifest points at a stdio process:

```json
{
  "name": "demo_echo_tool",
  "transport": "stdio",
  "command": "python3",
  "args": ["examples/ExternalTools/echo_tool.py"],
  "capabilities": ["read_fs"]
}
```

For `stdio`, QRE writes this JSON shape to the tool's stdin:

```json
{
  "name": "demo_echo_tool",
  "workspacePath": "/path/to/workspace",
  "arguments": {
    "message": "hello"
  }
}
```

The tool returns either plain text or:

```json
{ "result": { "message": "hello", "workspaceName": "repo" } }
```

## Discover

```bash
qre tool list --workspace . --profile readonly --external --json
```

## Invoke Through QRE

Use `--external` to include manifests in the runtime tool surface. Use
`--required-tool` when you want a deterministic smoke that forces a specific tool
call before normal tool mode resumes:

```bash
qre run \
  --workspace . \
  --profile readonly \
  --external \
  --approve-risk "Run the reviewed local example tool" \
  --required-tool demo_echo_tool \
  --stream \
  "Call demo_echo_tool with message='hello from QRE', then summarize the result."
```

## Boundaries

External tools are intentionally process-isolated:

- QRE does not load tool DLLs into the CLI process.
- The tool process receives an allowlisted environment, not provider secrets.
- The process is killed on timeout or cancellation.
- `mcp-stdio` is also supported for one-shot JSON-RPC `tools/call`, but the full
  MCP initialize lifecycle is not implemented yet.

External tools in v2 require plan-bound approval even with `read_fs`. The
`--approve-risk` reason authorizes external tool plans for this run; use a
workspace containing only manifests you have reviewed. Stdio processes run as
the local user, so these examples are for trusted local workspaces.

The current QRE external adapter exposes a fixed argument envelope
(`extension`, `max_files`, `max_chars`, `message`, `path`, `pattern`). Manifest
`inputSchema` is recorded for compatibility but is not the model-facing schema.
