#!/usr/bin/env python3
"""Generate the PythonToolDoctor live-provider tool-call streaming GIF."""

from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
import textwrap
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
ASSET_DIR = ROOT / "docs" / "assets"
GIF_PATH = ASSET_DIR / "python-tool-streaming-demo.gif"
APPSETTINGS = ROOT.parent / "codexflow" / "CodexFlow" / "appsettings.json"
DISPLAY_WORKSPACE = "/repo"


def main() -> None:
    if not APPSETTINGS.exists() and not all(os.environ.get(name) for name in ("QRE_API_URL", "QRE_API_KEY", "QRE_MODEL")):
        raise SystemExit(
            "Missing provider config. Set QRE_API_URL/QRE_API_KEY/QRE_MODEL or keep "
            f"CodexFlow appsettings at {APPSETTINGS}."
        )

    ASSET_DIR.mkdir(parents=True, exist_ok=True)
    workspace = Path(tempfile.mkdtemp(prefix="python-tool-demo-"))
    try:
        write_demo_repo(workspace)
        qre_bin = resolve_qre_binary()
        output = run_python_tool_doctor(qre_bin, workspace)
    finally:
        shutil.rmtree(workspace, ignore_errors=True)

    transcript = normalize_transcript(output)
    write_gif(transcript)
    print(f"wrote {GIF_PATH.relative_to(ROOT)}")


def write_demo_repo(workspace: Path) -> None:
    (workspace / "README.md").write_text(
        "# Python tool-call demo\n\nA tiny repository for a live QRE provider run.\n",
        encoding="utf-8",
    )
    (workspace / "src").mkdir()
    (workspace / "src" / "app.py").write_text(
        "def main():\n    print('hello from qre')\n\nif __name__ == '__main__':\n    main()\n",
        encoding="utf-8",
    )


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


def run_python_tool_doctor(qre_bin: str, workspace: Path) -> str:
    command = [
        "python3",
        str(ROOT / "examples" / "PythonToolDoctor" / "doctor.py"),
        "--qre",
        qre_bin,
        "--appsettings",
        str(APPSETTINGS),
        "--provider-section",
        "VllmAgent",
        "--prompt",
        "Call qre_list_files exactly once. Then stop calling tools and stream a two sentence repository health summary.",
        str(workspace),
    ]
    result = subprocess.run(
        command,
        cwd=ROOT,
        check=True,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    return result.stdout


def normalize_transcript(output: str) -> list[tuple[str, str]]:
    lines: list[tuple[str, str]] = [
        ("cmd", "$ python examples/PythonToolDoctor/doctor.py /repo"),
    ]
    for raw in output.splitlines():
        text = raw.strip()
        if text.startswith("workspace:"):
            text = f"workspace: {DISPLAY_WORKSPACE}"
        elif text.startswith("provider:"):
            text = "provider: codexflow/CodexFlow/appsettings.json#VllmAgent"
        elif text.startswith("trace:") and "/.qre/runs/" in text:
            text = f"trace: {DISPLAY_WORKSPACE}/.qre/runs/<run-id>/events.jsonl"
        elif text.startswith("run_directory:") and "/.qre/runs/" in text:
            text = f"run_directory: {DISPLAY_WORKSPACE}/.qre/runs/<run-id>"

        kind = "text"
        if text.startswith("PythonToolDoctor"):
            kind = "title"
        elif text.startswith("Streaming live provider answer"):
            kind = "section"
        elif text.startswith("Verified tool call"):
            kind = "tool"
        elif text.startswith("workspace:") or text.startswith("provider:") or text.startswith("model:") or text.startswith("api_mode:"):
            kind = "meta"
        elif text.startswith("run_id:") or text.startswith("termination:") or text.startswith("runner:") or text.startswith("tools:") or text.startswith("trace:") or text.startswith("run_directory:"):
            kind = "trace"

        lines.append((kind, text))
    return lines


def write_gif(transcript: list[tuple[str, str]]) -> None:
    width, height = 1080, 600
    margin_x, margin_y = 30, 54
    line_height = 22
    bg = "#101418"
    header = "#171d24"
    text_color = "#d6dee8"
    cmd_color = "#f8fafc"
    section_color = "#7dd3fc"
    stream_color = "#fbbf24"
    meta_color = "#c4b5fd"
    tool_color = "#86efac"
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
        draw.text((width - 340, 20), "PythonToolDoctor live provider demo", fill="#94a3b8", font=font)

        start = max(0, len(lines) - 24)
        y = margin_y
        for kind, text in lines[start:]:
            color = {
                "cmd": cmd_color,
                "title": title_color,
                "section": section_color,
                "meta": meta_color,
                "trace": meta_color,
                "tool": tool_color,
            }.get(kind, stream_color if not text.startswith(("run_", "termination:", "runner:", "tools:")) else text_color)
            active_font = title_font if kind in {"title", "section"} else font
            for segment in wrap_line(text, width=92):
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
        if kind == "cmd" or (kind == "text" and text):
            for partial in reveal_text(text, step=5 if kind == "cmd" else 8):
                frames.append(render(visible + [(kind, partial)]))
                durations.append(35 if kind == "text" else 45)
            visible.append((kind, text))
            frames.append(render(visible))
            durations.append(280)
        else:
            visible.append((kind, text))
            frames.append(render(visible))
            durations.append(600 if text else 180)

    durations[-1] += 2400
    frames[0].save(
        GIF_PATH,
        save_all=True,
        append_images=frames[1:],
        duration=durations,
        loop=0,
        optimize=True,
    )


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
