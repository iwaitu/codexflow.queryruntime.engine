# ExternalTools

This example demonstrates how to register and execute out-of-process custom tools in QRE without compiling third-party code into the Native AOT CLI binary.

---

## 1. Tool Manifest (`echo_tool.manifest.json`)

The manifest defines the tool command, protocol, capabilities, and arguments schema:

```json
{
  "name": "demo_echo_tool",
  "description": "Echo a message and report the workspace directory name.",
  "transport": "stdio",
  "command": "python",
  "args": ["examples/ExternalTools/echo_tool.py"],
  "capabilities": ["read_fs"],
  "timeoutSeconds": 30,
  "maxOutputBytes": 200000,
  "inputSchema": {
    "type": "object",
    "properties": {
      "message": {
        "type": "string",
        "description": "Message to echo."
      }
    }
  }
}
```

> **Note**: On Linux / macOS systems where Python 3 is installed as `python3`, specify `"command": "python3"`. On Windows or unified environments, use `"command": "python"`.

---

## 2. Register Tool

Register the manifest into the workspace registry (`.qre/tools/`):

```bash
qre tool register --workspace . --manifest examples/ExternalTools/echo_tool.manifest.json --force
```

Verify registration:
```bash
qre tool list --workspace . --profile readonly --external --json
```

---

## 3. Direct Tool Invocation (Debugging)

Test the tool in isolation without launching the LLM loop:

```bash
qre tool invoke \
  --workspace . \
  --name demo_echo_tool \
  --arguments '{"message":"hello QRE"}' \
  --json
```

Output:
```json
{
  "result": {
    "message": "hello QRE",
    "workspaceName": "codexflow.queryruntime.engine"
  }
}
```

---

## 4. Model-Driven Tool Execution

When running with `--external`, external tools require explicit plan approval (`--approve-risk`) to enforce fail-closed security:

```bash
qre run \
  --workspace . \
  --profile readonly \
  --external \
  --approve-risk "Run reviewed local echo tool" \
  --required-tool demo_echo_tool \
  --stream \
  "Call demo_echo_tool with message='hello from QRE', then summarize the result."
```

---

## Security Boundaries

- **Process Isolation**: External tools run as standalone OS child processes communicating exclusively via standard IO (JSON over stdin/stdout).
- **Environment Isolation**: Tools receive allowlisted environment variables, never provider API credentials or tokens.
- **Fail-Closed Approval**: External tool plans always require `--approve-risk "<reason>"`.
