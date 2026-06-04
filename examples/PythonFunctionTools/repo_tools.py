#!/usr/bin/env python3
"""Example Python functions exposed to QRE as external tools."""

from __future__ import annotations

from pathlib import Path

from qre_function_tool import main, qre_tool


@qre_tool(
    name="py_count_files",
    description="Count files under the workspace with an optional extension filter.",
    capabilities=["read_fs"],
)
def count_files(workspace_path: str, extension: str = ".py", max_files: int = 1000) -> dict[str, object]:
    root = Path(workspace_path)
    files = [
        path.relative_to(root).as_posix()
        for path in root.rglob(f"*{extension}")
        if path.is_file()
    ][:max_files]
    return {
        "extension": extension,
        "count": len(files),
        "sample": files[:10],
    }


@qre_tool(
    name="py_read_text_file",
    description="Read a UTF-8 text file from the workspace.",
    capabilities=["read_fs"],
)
def read_text_file(workspace_path: str, path: str, max_chars: int = 4000) -> dict[str, object]:
    root = Path(workspace_path).resolve()
    target = (root / path).resolve()
    if root not in target.parents and target != root:
        raise ValueError(f"Path escapes workspace: {path}")
    text = target.read_text(encoding="utf-8")[:max_chars]
    return {
        "path": path,
        "chars": len(text),
        "text": text,
    }


if __name__ == "__main__":
    raise SystemExit(main(__file__))
