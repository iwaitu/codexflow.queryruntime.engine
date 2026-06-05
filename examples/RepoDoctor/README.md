# RepoDoctor

A minimal, cross-platform example of calling the [`qre`](../../README.md) CLI as a
local agent runtime from a .NET app.

It registers a custom .NET stdio tool, runs a read-only analysis over a
repository, streams the model answer from `qre run --stream` to the host app
console, then follows up with a recorded replay of the same run. The same C#
code works on Windows, macOS, and Linux.

The example demonstrates two boundaries:

- The host .NET app owns process management and user experience.
- QRE owns provider calls, required tool selection, external tool execution,
  trace recording, and replay.

## Prerequisites

Make sure `qre` is on your `PATH` (or point `QRE_BIN` at it). From the repo root you
can produce a local Native AOT binary:

```bash
dotnet publish ../../CodexFlow.QueryRuntime.Cli \
  -c Release -r osx-arm64 \
  -p:PublishAot=true -p:SelfContained=true
export PATH="$PWD/../../CodexFlow.QueryRuntime.Cli/bin/Release/net10.0/osx-arm64/publish:$PATH"
```

…or download a prebuilt binary from the
[Releases page](https://github.com/iwaitu/codexflow.queryruntime.engine/releases).

## Run

```bash
# macOS / Linux
export QRE_API_URL="https://your-provider.example/v1"
export QRE_API_KEY="sk-..."
export QRE_MODEL="your-model"
export QRE_API_MODE="chat-completions"

dotnet run -- /path/to/repo
```

```powershell
# Windows PowerShell
$env:QRE_API_URL="https://your-provider.example/v1"
$env:QRE_API_KEY="sk-..."
$env:QRE_MODEL="your-model"
$env:QRE_API_MODE="chat-completions"

dotnet run -- C:\src\my-repo
```

If the environment variables are not set, RepoDoctor reads provider settings
from the sibling CodexFlow checkout by default:

```text
/Users/iwaitu/github/codexflow/CodexFlow/appsettings.json#VllmAgent
```

You can point it at a different provider source:

```bash
dotnet run -- \
  --appsettings /path/to/codexflow/CodexFlow/appsettings.json \
  --provider-section VllmAgent \
  /path/to/repo
```

In live-provider mode, RepoDoctor writes a manifest for
`repodoctor_workspace_summary`, registers it with:

```bash
qre tool register --workspace <repo> --manifest <manifest> --force --json
```

Then it invokes the custom tool through QRE:

```bash
qre tool invoke \
  --workspace <repo> \
  --name repodoctor_workspace_summary \
  --arguments '{"extension":".cs","maxFiles":1000}' \
  --json
```

The custom tool is implemented by the same C# program behind the hidden
`--stdio-tool` entry point. QRE starts it as a separate stdio process, sends:

```json
{
  "name": "repodoctor_workspace_summary",
  "workspacePath": "/path/to/repo",
  "arguments": {
    "extension": ".cs",
    "maxFiles": 1000
  }
}
```

RepoDoctor appends that result to the prompt and then starts a real-provider QRE
run with:

```bash
qre run --workspace <repo> --profile readonly --stream "<prompt + tool result>"
```

and receives:

```json
{
  "result": {
    "workspace": "my-repo",
    "extension": ".cs",
    "inspectedFileCount": 42,
    "topLevelDirectories": ["src", "tests"],
    "sampleFiles": ["src/App.cs"]
  }
}
```

## Offline (no LLM key)

To validate streaming, trace, and replay handling without calling a provider:

```bash
dotnet run -- --offline /path/to/repo
```

You can also choose the deterministic response text:

```bash
dotnet run -- --offline --response "offline smoke" /path/to/repo
```

`--offline` still exercises the same `qre run --stream` subprocess path; it only
adds `--response` so no provider key is required. Because `--response` is a
static model reply, offline mode does not force a real tool call. Use live mode
to validate provider-driven custom tool calling.

To run the same .NET host without registering the custom tool:

```bash
dotnet run -- --skip-custom-tool /path/to/repo
```

## Recording the GIF

The README GIF is generated from a live-provider run, not from offline text:

```bash
python scripts/generate-repodoctor-demo.py
```

The script builds `qre`, runs RepoDoctor against a temporary repository using the
CodexFlow appsettings provider, verifies the output includes
`repodoctor_workspace_summary`, and writes
`docs/assets/repodoctor-streaming-demo.gif`.
