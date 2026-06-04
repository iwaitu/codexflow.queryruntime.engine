#!/usr/bin/env python3
"""Minimal QRE stdio tool.

QRE writes a JSON object to stdin:
  { "name": "...", "workspacePath": "...", "arguments": { ... } }

The tool writes either plain text or { "result": ... } to stdout.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path


def main() -> int:
    request = json.load(sys.stdin)
    args = request.get("arguments", {})
    message = args.get("message", "hello from external tool")
    workspace = Path(request.get("workspacePath", "."))
    result = {
        "message": message,
        "workspaceName": workspace.name,
    }
    print(json.dumps({"result": result}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
