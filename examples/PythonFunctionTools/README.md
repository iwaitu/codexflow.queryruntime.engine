# PythonFunctionTools

This example shows how a Python project can expose ordinary Python functions as
QRE tools while keeping QRE in charge of the LLM tool-call loop.

## Write Functions

Decorate functions with `@qre_tool`. The helper infers a JSON schema from the
function signature and treats `workspace_path` as an injected QRE context value,
not as a model-supplied argument.

```python
@qre_tool(
    name="py_count_files",
    description="Count files under the workspace with an optional extension filter.",
    capabilities=["read_fs"],
)
def count_files(workspace_path: str, extension: str = ".py", max_files: int = 1000) -> dict[str, object]:
    ...
```

## Generate Manifests

```bash
python examples/PythonFunctionTools/repo_tools.py --manifest-dir .qre/generated-tools
```

The command writes one manifest per decorated function:

- `.qre/generated-tools/py_count_files.json`
- `.qre/generated-tools/py_read_text_file.json`

Each manifest points back to the Python script as a `stdio` process. QRE starts
that process when the LLM calls the tool.

## Register With QRE

```bash
qre tool register --workspace . --manifest .qre/generated-tools/py_count_files.json
qre tool register --workspace . --manifest .qre/generated-tools/py_read_text_file.json
qre tool list --workspace . --profile readonly --external --json
```

## Invoke Through QRE

```bash
qre run \
  --workspace . \
  --profile readonly \
  --external \
  --required-tool py_count_files \
  --stream \
  "Count Python files in this repo, then summarize the sample paths."
```

## Ownership Boundary

The Python application owns the function implementation and schema generation.
QRE owns provider calls, tool-call selection, process isolation, timeout handling,
trace recording, and returning tool results to the model. Code outside QRE does
not need to manually intercept or execute LLM tool calls.
