#!/usr/bin/env python3
"""Generate the Node.js live-provider tool demo GIF for README.md."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import tempfile
import textwrap
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
ASSET_DIR = ROOT / "docs" / "assets"
GIF_PATH = ASSET_DIR / "node-tool-streaming-demo.gif"
DISPLAY_WORKSPACE = "/node-repo"
DEFAULT_APPSETTINGS = ROOT.parent / "codexflow" / "CodexFlow" / "appsettings.json"
DEFAULT_PROVIDER_SECTION = "VllmAgent"
REQUIRED_ENV = ("QRE_API_URL", "QRE_API_KEY", "QRE_MODEL")
DEFAULT_PROMPT = (
    "Use the Node tool result below to stream exactly three concise bullets about "
    "JavaScript repository risks."
)


def main() -> None:
    ASSET_DIR.mkdir(parents=True, exist_ok=True)
    workspace = Path(tempfile.mkdtemp(prefix="node-tool-demo-"))
    try:
        write_demo_workspace(workspace)
        qre_bin = resolve_qre_binary()
        env, provider_source = load_provider_env()
        output = run_node_demo(qre_bin, workspace, env, provider_source)
        verify_demo_output(output)
    finally:
        shutil.rmtree(workspace, ignore_errors=True)

    transcript = normalize_transcript(output)
    write_gif(transcript)
    print(f"wrote {GIF_PATH.relative_to(ROOT)}")


def resolve_qre_binary() -> str:
    if os.environ.get("QRE_BIN"):
        return os.environ["QRE_BIN"]

    subprocess.run(
        ["dotnet", "build", str(ROOT / "CodexFlow.QueryRuntime.Cli" / "CodexFlow.QueryRuntime.Cli.csproj")],
        cwd=ROOT,
        check=True,
    )
    binary = ROOT / "CodexFlow.QueryRuntime.Cli" / "bin" / "Debug" / "net10.0" / ("qre.exe" if os.name == "nt" else "qre")
    if not binary.exists():
        raise FileNotFoundError(f"qre binary was not found: {binary}")
    return str(binary)


def write_demo_workspace(workspace: Path) -> None:
    (workspace / "src").mkdir(parents=True)
    (workspace / "tests").mkdir()
    (workspace / "package.json").write_text(
        json.dumps(
            {
                "name": "node-tool-demo",
                "type": "module",
                "scripts": {"test": "node tests/smoke.test.js"},
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )
    (workspace / "README.md").write_text(
        "# Node tool demo\n\nA tiny repository for recording QRE Node tool integration.\n",
        encoding="utf-8",
    )
    (workspace / "src" / "index.js").write_text(
        textwrap.dedent(
            """
            export function summarizeUser(user) {
              return `${user.name}:${user.role}`;
            }
            """
        ).strip()
        + "\n",
        encoding="utf-8",
    )
    (workspace / "tests" / "smoke.test.js").write_text(
        textwrap.dedent(
            """
            import { summarizeUser } from "../src/index.js";

            if (summarizeUser({ name: "ada", role: "admin" }) !== "ada:admin") {
              throw new Error("unexpected summary");
            }
            """
        ).strip()
        + "\n",
        encoding="utf-8",
    )


def load_provider_env() -> tuple[dict[str, str], str]:
    env = os.environ.copy()
    if all(env.get(name) for name in REQUIRED_ENV):
        return env, "environment"

    if not DEFAULT_APPSETTINGS.exists():
        missing = ", ".join(name for name in REQUIRED_ENV if not env.get(name))
        raise RuntimeError(f"Missing provider environment ({missing}) and appsettings was not found: {DEFAULT_APPSETTINGS}")

    settings = json.loads(strip_json_comments(DEFAULT_APPSETTINGS.read_text(encoding="utf-8")))
    section = settings.get(DEFAULT_PROVIDER_SECTION)
    if not isinstance(section, dict):
        raise RuntimeError(f"Missing provider section {DEFAULT_PROVIDER_SECTION} in {DEFAULT_APPSETTINGS}")

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

    missing = [name for name in REQUIRED_ENV if not env.get(name)]
    if missing:
        raise RuntimeError("Missing provider settings: " + ", ".join(missing))

    return env, f"{DEFAULT_APPSETTINGS}#{DEFAULT_PROVIDER_SECTION}"


def run_node_demo(qre_bin: str, workspace: Path, env: dict[str, str], provider_source: str) -> str:
    manifest_dir = workspace / ".qre" / "generated-tools"
    manifest_output = subprocess.run(
        ["node", str(ROOT / "examples" / "NodeFunctionTools" / "repo_tools.mjs"), "--manifest-dir", str(manifest_dir)],
        cwd=ROOT,
        check=True,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=env,
    ).stdout

    register_output = subprocess.run(
        [
            qre_bin,
            "tool",
            "register",
            "--workspace",
            str(workspace),
            "--manifest",
            str(manifest_dir / "node_count_files.json"),
            "--force",
            "--json",
        ],
        cwd=ROOT,
        check=True,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=env,
    ).stdout

    invoke_output = subprocess.run(
        [
            qre_bin,
            "tool",
            "invoke",
            "--workspace",
            str(workspace),
            "--name",
            "node_count_files",
            "--arguments",
            '{"extension":".js","max_files":1000}',
            "--json",
        ],
        cwd=ROOT,
        check=True,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=env,
    ).stdout
    invoke_json = json.loads(last_json_line(invoke_output))
    tool_result = invoke_json["result"]

    live_prompt = f"{DEFAULT_PROMPT}\n\nNode tool result:\n{tool_result}"
    run_output = subprocess.run(
        [
            qre_bin,
            "run",
            "--workspace",
            str(workspace),
            "--profile",
            "readonly",
            "--stream",
            live_prompt,
        ],
        cwd=ROOT,
        check=True,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=env,
    ).stdout

    return "\n".join(
        [
            "NodeToolDoctor",
            f"workspace: {workspace}",
            f"provider: {provider_source}",
            f"model: {env['QRE_MODEL']}",
            f"api_mode: {env.get('QRE_API_MODE', '')}".rstrip(),
            "",
            "Generate Node tool manifests:",
            manifest_output.strip(),
            "",
            "Register Node tool:",
            sanitize_register_output(register_output),
            "",
            "Invoke Node tool through QRE:",
            summarize_tool_result(tool_result),
            "",
            "Streaming live provider answer:",
            run_output.strip(),
        ]
    )


def verify_demo_output(output: str) -> None:
    if "node_count_files" not in output:
        raise RuntimeError("Expected node_count_files in demo output.")
    if "Streaming live provider answer:" not in output:
        raise RuntimeError("Expected live-provider streaming section in demo output.")


def sanitize_register_output(output: str) -> str:
    data = json.loads(last_json_line(output))
    return f"registered {data['toolName']} -> {DISPLAY_WORKSPACE}/.qre/tools/{data['toolName']}.json"


def summarize_tool_result(result_text: str) -> str:
    data = json.loads(result_text)
    sample = ", ".join(data.get("sample", [])[:3])
    return f"node_count_files: count={data.get('count')}, extension={data.get('extension')}, sample=[{sample}]"


def normalize_transcript(output: str) -> list[tuple[str, str]]:
    lines: list[tuple[str, str]] = [
        ("cmd", "$ python scripts/generate-node-tool-demo.py"),
    ]
    streaming = False
    for raw in output.splitlines():
        text = raw.replace(str(ROOT), "").replace(str(Path(tempfile.gettempdir())), "/tmp")
        if text.startswith("workspace:"):
            text = f"workspace: {DISPLAY_WORKSPACE}"
        elif text.startswith("provider:"):
            text = f"provider: /codexflow/CodexFlow/appsettings.json#{DEFAULT_PROVIDER_SECTION}"
        elif text.startswith(str(Path(tempfile.gettempdir()))):
            text = text.replace(str(Path(tempfile.gettempdir())), "/tmp")
        if text.startswith("trace:") and "/.qre/runs/" in text:
            text = f"trace: {DISPLAY_WORKSPACE}/.qre/runs/<run-id>/events.jsonl"
        elif text.startswith("run_directory:") and "/.qre/runs/" in text:
            text = f"run_directory: {DISPLAY_WORKSPACE}/.qre/runs/<run-id>"

        kind = "text"
        if text.startswith("NodeToolDoctor"):
            kind = "title"
        elif text.startswith("Generate Node tool manifests") or text.startswith("Register Node tool") or text.startswith("Invoke Node tool") or text.startswith("Streaming live provider answer"):
            kind = "section"
            streaming = text.startswith("Streaming live provider answer")
        elif text.startswith("provider:") or text.startswith("model:") or text.startswith("api_mode:") or text.startswith("registered ") or text.startswith("node_count_files:"):
            kind = "meta"
        elif text.startswith("run_id:") or text.startswith("termination:") or text.startswith("runner:") or text.startswith("tools:") or text.startswith("trace:") or text.startswith("run_directory:"):
            kind = "meta"
        elif streaming and text:
            kind = "stream"

        lines.append((kind, text))
    return lines


def write_gif(transcript: list[tuple[str, str]]) -> None:
    width, height = 1040, 540
    margin_x, margin_y = 30, 54
    line_height = 22
    bg = "#101418"
    header = "#171d24"
    text_color = "#d6dee8"
    cmd_color = "#f8fafc"
    section_color = "#7dd3fc"
    stream_color = "#86efac"
    meta_color = "#c4b5fd"
    title_color = "#fbbf24"

    font = load_font(16)
    title_font = load_font(18)

    def render(lines: list[tuple[str, str]]) -> Image.Image:
        image = Image.new("RGB", (width, height), bg)
        draw = ImageDraw.Draw(image)
        draw.rounded_rectangle((12, 12, width - 12, height - 12), radius=10, fill=bg, outline="#28313d", width=2)
        draw.rounded_rectangle((12, 12, width - 12, 42), radius=10, fill=header)
        draw.rectangle((12, 30, width - 12, 42), fill=header)
        draw.ellipse((30, 23, 42, 35), fill="#ef4444")
        draw.ellipse((50, 23, 62, 35), fill="#f59e0b")
        draw.ellipse((70, 23, 82, 35), fill="#22c55e")
        draw.text((width - 285, 20), "Node tool live-provider demo", fill="#94a3b8", font=font)

        start = max(0, len(lines) - 21)
        y = margin_y
        for kind, text in lines[start:]:
            color = {
                "cmd": cmd_color,
                "title": title_color,
                "section": section_color,
                "stream": stream_color,
                "meta": meta_color,
            }.get(kind, text_color)
            active_font = title_font if kind in {"title", "section"} else font
            for segment in wrap_line(text, width=88):
                draw.text((margin_x, y), segment, fill=color, font=active_font)
                y += line_height
                if y > height - 34:
                    break
            if y > height - 34:
                break
        return image

    frames: list[Image.Image] = []
    durations: list[int] = []
    visible: list[tuple[str, str]] = []
    for kind, text in transcript:
        if kind in {"cmd", "stream"}:
            for partial in reveal_text(text, step=5 if kind == "cmd" else 7):
                frames.append(render(visible + [(kind, partial)]))
                durations.append(35 if kind == "stream" else 45)
            visible.append((kind, text))
            frames.append(render(visible))
            durations.append(350)
        else:
            visible.append((kind, text))
            frames.append(render(visible))
            durations.append(550 if text else 180)

    durations[-1] += 2200
    frames[0].save(
        GIF_PATH,
        save_all=True,
        append_images=frames[1:],
        duration=durations,
        loop=0,
        optimize=True,
    )


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


def last_json_line(output: str) -> str:
    for line in reversed(output.splitlines()):
        if line.strip().startswith("{"):
            return line.strip()
    raise RuntimeError("No JSON object found in output.")


def reveal_text(text: str, step: int) -> list[str]:
    return [text[:i] for i in range(step, len(text) + step, step)]


def wrap_line(text: str, width: int) -> list[str]:
    if not text:
        return [""]
    return textwrap.wrap(text, width=width, subsequent_indent="  ", break_long_words=False, break_on_hyphens=False) or [text]


def load_font(size: int) -> ImageFont.ImageFont:
    candidates = [
        "/System/Library/Fonts/Menlo.ttc",
        "/System/Library/Fonts/Monaco.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
    ]
    for candidate in candidates:
        if Path(candidate).exists():
            return ImageFont.truetype(candidate, size)
    return ImageFont.load_default()


if __name__ == "__main__":
    main()

