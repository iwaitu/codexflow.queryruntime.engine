#!/usr/bin/env python3
"""Small helper for exposing Python functions as QRE stdio tools."""

from __future__ import annotations

import argparse
import inspect
import json
import sys
import traceback
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, get_args, get_origin


@dataclass(frozen=True)
class ToolDefinition:
    name: str
    description: str
    capabilities: tuple[str, ...]
    function: Callable[..., Any]
    timeout_seconds: int
    max_output_bytes: int


_TOOLS: dict[str, ToolDefinition] = {}


def qre_tool(
    *,
    name: str | None = None,
    description: str | None = None,
    capabilities: tuple[str, ...] | list[str] = (),
    timeout_seconds: int = 30,
    max_output_bytes: int = 200_000,
) -> Callable[[Callable[..., Any]], Callable[..., Any]]:
    """Register a Python function in this process-local QRE tool registry."""

    def decorator(function: Callable[..., Any]) -> Callable[..., Any]:
        tool_name = name or function.__name__
        if tool_name in _TOOLS:
            raise ValueError(f"Duplicate QRE tool name: {tool_name}")
        _TOOLS[tool_name] = ToolDefinition(
            name=tool_name,
            description=description or inspect.getdoc(function) or "Python QRE tool.",
            capabilities=tuple(capabilities),
            function=function,
            timeout_seconds=timeout_seconds,
            max_output_bytes=max_output_bytes,
        )
        return function

    return decorator


def dispatch() -> int:
    """Read one QRE stdio request from stdin, invoke the target function, print JSON."""

    request = json.load(sys.stdin)
    tool_name = request.get("name")
    if not isinstance(tool_name, str) or tool_name not in _TOOLS:
        raise ValueError(f"Unknown QRE tool: {tool_name}")

    arguments = request.get("arguments") or {}
    if not isinstance(arguments, dict):
        raise ValueError("QRE tool arguments must be a JSON object.")

    workspace_path = request.get("workspacePath")
    result = _invoke(_TOOLS[tool_name], arguments, workspace_path)
    print(json.dumps({"result": result}, ensure_ascii=False))
    return 0


def write_manifests(script_path: str | Path, manifest_dir: str | Path) -> list[Path]:
    """Write one QRE manifest per registered Python function."""

    target = Path(manifest_dir)
    target.mkdir(parents=True, exist_ok=True)
    script = Path(script_path).resolve()
    written: list[Path] = []
    for tool in _TOOLS.values():
        manifest = {
            "name": tool.name,
            "description": tool.description,
            "transport": "stdio",
            "command": sys.executable,
            "args": [str(script)],
            "capabilities": list(tool.capabilities),
            "timeoutSeconds": tool.timeout_seconds,
            "maxOutputBytes": tool.max_output_bytes,
            "inputSchema": _schema_for(tool.function),
        }
        path = target / f"{tool.name}.json"
        path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        written.append(path)
    return written


def main(script_path: str | Path) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest-dir", help="Write QRE manifests for decorated functions.")
    args = parser.parse_args()

    if args.manifest_dir:
        for path in write_manifests(script_path, args.manifest_dir):
            print(path)
        return 0

    try:
        return dispatch()
    except Exception:
        traceback.print_exc(file=sys.stderr)
        return 1


def _invoke(tool: ToolDefinition, arguments: dict[str, Any], workspace_path: Any) -> Any:
    signature = inspect.signature(tool.function)
    kwargs: dict[str, Any] = {}
    for name, parameter in signature.parameters.items():
        if parameter.kind in (inspect.Parameter.VAR_POSITIONAL, inspect.Parameter.VAR_KEYWORD):
            continue
        if name in arguments:
            kwargs[name] = arguments[name]
        elif name == "workspace_path":
            kwargs[name] = str(workspace_path or ".")
        elif parameter.default is inspect.Parameter.empty:
            raise ValueError(f"Missing required argument: {name}")
    return tool.function(**kwargs)


def _schema_for(function: Callable[..., Any]) -> dict[str, Any]:
    signature = inspect.signature(function)
    properties: dict[str, Any] = {}
    required: list[str] = []
    for name, parameter in signature.parameters.items():
        if name == "workspace_path":
            continue
        if parameter.kind in (inspect.Parameter.VAR_POSITIONAL, inspect.Parameter.VAR_KEYWORD):
            continue
        properties[name] = _schema_for_annotation(parameter.annotation)
        if parameter.default is inspect.Parameter.empty:
            required.append(name)

    schema: dict[str, Any] = {
        "type": "object",
        "properties": properties,
        "additionalProperties": False,
    }
    if required:
        schema["required"] = required
    return schema


def _schema_for_annotation(annotation: Any) -> dict[str, Any]:
    if annotation is inspect.Parameter.empty:
        return {"type": "string"}

    origin = get_origin(annotation)
    if origin is not None:
        args = [arg for arg in get_args(annotation) if arg is not type(None)]
        if len(args) == 1:
            return _schema_for_annotation(args[0])

    if annotation is str:
        return {"type": "string"}
    if annotation is int:
        return {"type": "integer"}
    if annotation is float:
        return {"type": "number"}
    if annotation is bool:
        return {"type": "boolean"}
    if annotation in (dict, list):
        return {"type": "object" if annotation is dict else "array"}
    return {"type": "string"}
