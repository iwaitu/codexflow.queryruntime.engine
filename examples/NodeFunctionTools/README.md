# NodeFunctionTools

This example shows how a Node.js project can expose ordinary JavaScript
functions as QRE tools while keeping QRE in charge of registration, process
execution, trace, and provider orchestration.

It uses only built-in Node.js modules. No `npm install` step is required.

## Write Functions

Register functions with `qreTool`. JavaScript has no runtime type annotations, so
the input schema is declared explicitly:

```javascript
qreTool(
  {
    name: "node_count_files",
    description: "Count files under the workspace with an optional extension filter.",
    capabilities: ["read_fs"],
    inputSchema: {
      type: "object",
      properties: {
        extension: { type: "string", default: ".js" },
        max_files: { type: "integer", default: 1000 },
      },
      additionalProperties: false,
    },
  },
  async ({ workspacePath, extension = ".js", max_files = 1000 }) => {
    // Inspect workspacePath and return JSON-serializable data.
  },
);
```

## Generate Manifests

```bash
node examples/NodeFunctionTools/repo_tools.mjs --manifest-dir .qre/generated-tools
```

The command writes one manifest per registered function:

- `.qre/generated-tools/node_count_files.json`
- `.qre/generated-tools/node_read_text_file.json`

Each manifest points back to the Node script as a `stdio` process. QRE starts
that process when the tool is invoked.

## Register With QRE

```bash
qre tool register --workspace . --manifest .qre/generated-tools/node_count_files.json
qre tool register --workspace . --manifest .qre/generated-tools/node_read_text_file.json
qre tool list --workspace . --profile readonly --external --json
```

## Invoke Through QRE

For a deterministic smoke that does not require an LLM provider:

```bash
qre tool invoke \
  --workspace . \
  --name node_count_files \
  --arguments '{"extension":".js","max_files":1000}' \
  --json
```

For a real model run, include registered tools with `--external`:

```bash
qre run \
  --workspace . \
  --profile readonly \
  --external \
  --approve-risk "Run the reviewed local example tool" \
  --stream \
  "Use node_count_files to inspect JavaScript files, then summarize the result."
```

## Ownership Boundary

The Node.js application owns function implementation and JSON schema definition.
QRE owns manifest registration, process isolation, timeout handling, trace
recording, and returning tool results to the model or caller.

## Recording the GIF

The root README GIF is generated from a live-provider run:

```bash
python scripts/generate-node-tool-demo.py
```

The script builds `qre`, creates a temporary Node.js repository, generates and
registers the `node_count_files` manifest, invokes that tool through QRE, streams
a real provider response using the tool result, and writes
`docs/assets/node-tool-streaming-demo.gif`.

External tools in v2 require plan-bound approval even with `read_fs`. The
`--approve-risk` reason authorizes external tool plans for this run; use a
workspace containing only manifests you have reviewed. Stdio processes run as
the local user, so these examples are for trusted local workspaces.

The current QRE external adapter exposes a fixed argument envelope
(`extension`, `max_files`, `max_chars`, `message`, `path`, `pattern`). Manifest
`inputSchema` is recorded for compatibility but is not the model-facing schema.
