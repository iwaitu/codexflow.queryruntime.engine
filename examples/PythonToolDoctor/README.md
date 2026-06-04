# PythonToolDoctor

A Python subprocess example that calls [`qre`](../../README.md), streams a live
provider response, and requires one read-only tool call before the model answers.

The example runs:

```bash
qre run --profile readonly --required-tool qre_list_files --stream ...
```

After the run, it reads `qre trace latest --jsonl` and verifies that the trace
contains a `qre_list_files` tool call.

## Provider Configuration

By default, the script reads the `VllmAgent` section from the sibling CodexFlow
checkout:

```text
/Users/iwaitu/github/codexflow/CodexFlow/appsettings.json
```

You can override this or use environment variables:

```bash
export QRE_API_URL="https://your-provider.example/v1"
export QRE_API_KEY="sk-..."
export QRE_MODEL="your-model"
export QRE_API_MODE="chat-completions"
```

## Run

```bash
python examples/PythonToolDoctor/doctor.py /path/to/repo
```

With explicit config:

```bash
python examples/PythonToolDoctor/doctor.py \
  --appsettings /path/to/codexflow/CodexFlow/appsettings.json \
  --provider-section VllmAgent \
  /path/to/repo
```
