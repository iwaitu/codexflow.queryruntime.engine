# PythonToolDoctor

A Python subprocess example that drives [`qre`](../../README.md), streams a real-time provider response, enforces a **required built-in tool call** (`qre_list_files`), and validates the execution trajectory with **strict trace replay**.

The script runs:

```bash
qre run \
  --workspace <repo> \
  --profile readonly \
  --required-tool qre_list_files \
  --trace-data sanitized \
  --stream \
  "<prompt>"
```

After the run, it verifies the run using:
```bash
qre replay latest --strict --workspace <repo> --json
```

---

## How to Run

PythonToolDoctor automatically resolves `qre` from the local build outputs (`CodexFlow.QueryRuntime.Cli/bin/...`), the `QRE_BIN` environment variable, or system `PATH`.

### 1. Set Provider Environment Variables

```bash
# macOS / Linux
export QRE_API_URL="https://api.openai.com/v1"
export QRE_API_KEY="sk-..."
export QRE_MODEL="gpt-4o"
export QRE_API_MODE="chat-completions"

python examples/PythonToolDoctor/doctor.py /path/to/repo
```

```powershell
# Windows PowerShell
$env:QRE_API_URL="https://api.openai.com/v1"
$env:QRE_API_KEY="sk-..."
$env:QRE_MODEL="gpt-4o"
$env:QRE_API_MODE="chat-completions"

python examples/PythonToolDoctor/doctor.py .
```

### 2. Custom AppSettings File (Optional)

You can also load credentials from any JSON file with a provider section:

```bash
python examples/PythonToolDoctor/doctor.py \
  --appsettings /path/to/appsettings.json \
  --provider-section VllmAgent \
  /path/to/repo
```

---

## Key Guarantees Demonstrated

1. **Required Tool Enforcement**: `--required-tool qre_list_files` guarantees the model invokes the tool before producing any final answer.
2. **Real-Time Streaming**: Text output is streamed directly to stdout token by token.
3. **Strict Zero-Token Replay**: `qre replay latest --strict` verifies tool execution records, status, and digest consistency offline without extra API costs.
