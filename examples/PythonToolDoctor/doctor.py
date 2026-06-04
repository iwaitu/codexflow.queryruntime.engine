#!/usr/bin/env python3
"""Python example: stream a real-provider QRE answer with a required tool call.

Environment:
  QRE_API_URL   provider endpoint
  QRE_API_KEY   provider API key
  QRE_MODEL     model name
  QRE_API_MODE  optional: chat-completions, responses, or anthropic-messages
  QRE_BIN       optional qre binary path

Usage:
  python examples/PythonToolDoctor/doctor.py /path/to/repo
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path


REQUIRED_ENV = ("QRE_API_URL", "QRE_API_KEY", "QRE_MODEL")
DEFAULT_APPSETTINGS = Path(__file__).resolve().parents[2].parent / "codexflow" / "CodexFlow" / "appsettings.json"
DEFAULT_PROVIDER_SECTION = "VllmAgent"
DEFAULT_PROMPT = (
    "First call qre_list_files on the workspace root exactly once. After that "
    "stop calling tools and stream a concise repository health summary with exactly three bullets."
)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("workspace", nargs="?", default=".", help="Repository/workspace to inspect.")
    parser.add_argument("--qre", default=os.environ.get("QRE_BIN", "qre"), help="qre binary path.")
    parser.add_argument("--prompt", default=DEFAULT_PROMPT, help="Prompt passed to qre run.")
    parser.add_argument("--max-rounds", default="6", help="QRE runtime round limit.")
    parser.add_argument("--appsettings", default=str(DEFAULT_APPSETTINGS), help="CodexFlow appsettings.json provider source.")
    parser.add_argument("--provider-section", default=DEFAULT_PROVIDER_SECTION, help="Provider section inside appsettings.json.")
    args = parser.parse_args()

    env = os.environ.copy()
    provider_source = load_provider_env(
        Path(args.appsettings).expanduser(),
        args.provider_section,
        env,
    )

    missing = [name for name in REQUIRED_ENV if not env.get(name)]
    if missing:
        print(
            "Missing real-provider environment: " + ", ".join(missing),
            file=sys.stderr,
        )
        print(
            "Set QRE_API_URL, QRE_API_KEY, QRE_MODEL, or point --appsettings at a CodexFlow appsettings.json.",
            file=sys.stderr,
        )
        return 2

    workspace = Path(args.workspace).expanduser().resolve()
    if not workspace.is_dir():
        print(f"Workspace does not exist: {workspace}", file=sys.stderr)
        return 1

    print("PythonToolDoctor")
    print(f"workspace: {workspace}")
    print(f"provider: {provider_source}")
    print(f"model: {env['QRE_MODEL']}")
    if env.get("QRE_API_MODE"):
        print(f"api_mode: {env['QRE_API_MODE']}")
    print()
    print("Streaming live provider answer:")

    command = [
        args.qre,
        "run",
        "--workspace",
        str(workspace),
        "--profile",
        "readonly",
        "--required-tool",
        "qre_list_files",
        "--max-rounds",
        args.max_rounds,
        "--stream",
        args.prompt,
    ]
    exit_code = run_streaming(command, env)
    if exit_code != 0:
        return exit_code

    trace_events = read_latest_trace(args.qre, workspace, env)
    tool_names = [
        event.get("payload", {}).get("ToolName")
        for event in trace_events
        if event.get("eventType") == "tool.call.requested"
    ]
    if "qre_list_files" not in tool_names:
        print("Expected qre_list_files tool call was not found in the latest trace.", file=sys.stderr)
        return 1

    print()
    print("Verified tool call from trace: qre_list_files")
    return 0


def load_provider_env(appsettings_path: Path, section_name: str, env: dict[str, str]) -> str:
    if all(env.get(name) for name in REQUIRED_ENV):
        return "environment"

    if not appsettings_path.exists():
        return f"missing appsettings ({appsettings_path})"

    settings = json.loads(strip_json_comments(appsettings_path.read_text(encoding="utf-8")))
    section = settings.get(section_name)
    if not isinstance(section, dict):
        return f"missing section {section_name} in {appsettings_path}"

    mappings = {
        "QRE_API_URL": "ApiUrl",
        "QRE_API_KEY": "ApiKey",
        "QRE_MODEL": "Model",
        "QRE_API_MODE": "ApiMode",
    }
    for env_name, config_name in mappings.items():
        value = section.get(config_name)
        if value and not env.get(env_name):
            env[env_name] = str(value)

    return f"{appsettings_path}#{section_name}"


def strip_json_comments(text: str) -> str:
    result: list[str] = []
    in_string = False
    escape = False
    index = 0
    while index < len(text):
        char = text[index]
        next_char = text[index + 1] if index + 1 < len(text) else ""
        if in_string:
            result.append(char)
            if escape:
                escape = False
            elif char == "\\":
                escape = True
            elif char == '"':
                in_string = False
            index += 1
            continue

        if char == '"':
            in_string = True
            result.append(char)
            index += 1
            continue

        if char == "/" and next_char == "/":
            while index < len(text) and text[index] not in "\r\n":
                index += 1
            continue

        result.append(char)
        index += 1

    return "".join(result)


def run_streaming(command: list[str], env: dict[str, str]) -> int:
    with subprocess.Popen(
        command,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1,
        env=env,
    ) as process:
        assert process.stdout is not None
        assert process.stderr is not None
        while True:
            chunk = process.stdout.read(1)
            if not chunk:
                break
            print(chunk, end="", flush=True)

        stderr = process.stderr.read()
        process.wait()
        if process.returncode != 0:
            print(stderr, file=sys.stderr, end="")
        return process.returncode


def read_latest_trace(qre: str, workspace: Path, env: dict[str, str]) -> list[dict]:
    result = subprocess.run(
        [qre, "trace", "latest", "--workspace", str(workspace), "--jsonl"],
        check=True,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=env,
    )
    return [json.loads(line) for line in result.stdout.splitlines() if line.strip()]


if __name__ == "__main__":
    raise SystemExit(main())
