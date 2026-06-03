# RepoDoctor

A minimal, cross-platform example of calling the [`qre`](../../README.md) CLI as a
local agent runtime from a .NET app.

It runs a read-only analysis over a repository, parses the single-line JSON result
from `qre run --json`, prints the final text and trace path, then follows up with a
recorded replay of the same run. The same C# code works on Windows, macOS, and Linux.

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

## Offline (no LLM key)

To validate that your app parses `qre` output without calling a provider, swap the
`--profile readonly` flag in [Program.cs](Program.cs) for `--response "offline smoke"`.
