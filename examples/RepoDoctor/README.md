# RepoDoctor

A complete, cross-platform example of calling the [`qre`](../../README.md) CLI as an external agent runtime from a .NET 10 console application.

It registers a custom .NET stdio tool, runs a read-only analysis over a target repository, streams the assistant's answer in real-time, and follows up with a **strict offline replay** of the recorded trajectory to verify digest stability.

The same C# code runs identically on Windows, macOS, and Linux.

---

## Architecture Boundaries

- **The Host .NET app owns**: Process management, custom stdio tool implementation (`--stdio-tool`), user console UX, and replay digest verification.
- **QRE owns**: Provider API communication, external tool invocation, audit recording, and zero-token deterministic replay.

---

## How to Run

RepoDoctor automatically searches for the built `qre` executable in the repository's `CodexFlow.QueryRuntime.Cli/bin/...` directory, or uses the `QRE_BIN` environment variable / system `PATH`.

### 1. Offline Smoke Test (No API Key Required)

Validate streaming, trace persistence, and strict replay without contacting any external provider:

```bash
# macOS / Linux
dotnet run --project examples/RepoDoctor -- --offline .

# Windows PowerShell
dotnet run --project examples/RepoDoctor -- --offline .
```

You can optionally specify a custom response:
```bash
dotnet run --project examples/RepoDoctor -- --offline --response "Custom smoke reply" .
```

### 2. Live Provider Run (OpenAI / vLLM / Claude)

Set standard environment variables:

```bash
# macOS / Linux
export QRE_API_URL="https://api.openai.com/v1"
export QRE_API_KEY="sk-..."
export QRE_MODEL="gpt-4o"
export QRE_API_MODE="chat-completions"

dotnet run --project examples/RepoDoctor -- /path/to/repo
```

```powershell
# Windows PowerShell
$env:QRE_API_URL="https://api.openai.com/v1"
$env:QRE_API_KEY="sk-..."
$env:QRE_MODEL="gpt-4o"
$env:QRE_API_MODE="chat-completions"

dotnet run --project examples/RepoDoctor -- C:\path\to\repo
```

You can also point `--appsettings` to any valid JSON configuration file:
```bash
dotnet run --project examples/RepoDoctor -- \
  --appsettings /path/to/appsettings.json \
  --provider-section VllmAgent \
  /path/to/repo
```

---

## How Custom Stdio Tools Work

1. RepoDoctor writes a manifest for `repodoctor_workspace_summary` under `.qre/repodoctor/`:
   ```bash
   qre tool register --workspace <repo> --manifest <manifest> --force --json
   ```
2. RepoDoctor executes the tool via QRE:
   ```bash
   qre tool invoke \
     --workspace <repo> \
     --name repodoctor_workspace_summary \
     --arguments '{"extension":".cs","maxFiles":1000}' \
     --json
   ```
3. QRE invokes the same executable with `--stdio-tool` via stdin/stdout:
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
4. RepoDoctor streams the final model answer:
   ```bash
   qre run --workspace <repo> --profile readonly --trace-data sanitized --stream "<prompt>"
   ```
5. Finally, RepoDoctor strictly verifies the recorded trace offline:
   ```bash
   qre replay latest --strict --workspace <repo> --json
   ```
