#!/usr/bin/env python3
"""Generate the RepoDoctor live-provider custom-tool demo GIF for README.md."""

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
GIF_PATH = ASSET_DIR / "repodoctor-streaming-demo.gif"
DISPLAY_WORKSPACE = "/repo"
DEFAULT_APPSETTINGS = ROOT.parent / "codexflow" / "CodexFlow" / "appsettings.json"
DEFAULT_PROVIDER_SECTION = "VllmAgent"
REQUIRED_TOOL = "repodoctor_workspace_summary"
DEFAULT_PROMPT = (
    "Use the RepoDoctor custom tool result to stream exactly three concise bullets "
    "about the repository risks."
)


def main() -> None:
    ASSET_DIR.mkdir(parents=True, exist_ok=True)
    workspace = Path(tempfile.mkdtemp(prefix="repodoctor-demo-"))
    try:
        write_demo_workspace(workspace)
        qre_bin = resolve_qre_binary()
        output = run_repodoctor(qre_bin, workspace)
        verify_required_tool_call(output)
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
    (workspace / "src").mkdir()
    (workspace / "tests").mkdir()
    (workspace / "README.md").write_text(
        "# Demo repository\n\nA tiny repo for RepoDoctor live-provider tool-call recording.\n",
        encoding="utf-8",
    )
    (workspace / "src" / "DemoService.cs").write_text(
        textwrap.dedent(
            """
            namespace Demo;

            public sealed class DemoService
            {
                public string Describe() => "demo";
            }
            """
        ).strip()
        + "\n",
        encoding="utf-8",
    )
    (workspace / "tests" / "DemoServiceTests.cs").write_text(
        textwrap.dedent(
            """
            namespace Demo.Tests;

            public sealed class DemoServiceTests
            {
                public bool Smoke() => new DemoService().Describe() == "demo";
            }
            """
        ).strip()
        + "\n",
        encoding="utf-8",
    )


def run_repodoctor(qre_bin: str, workspace: Path) -> str:
    result = subprocess.run(
        [
            "dotnet",
            "run",
            "--project",
            str(ROOT / "examples" / "RepoDoctor" / "RepoDoctor.csproj"),
            "--",
            "--qre",
            qre_bin,
            "--appsettings",
            str(DEFAULT_APPSETTINGS),
            "--provider-section",
            DEFAULT_PROVIDER_SECTION,
            "--prompt",
            DEFAULT_PROMPT,
            str(workspace),
        ],
        cwd=ROOT,
        check=True,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    return result.stdout


def verify_required_tool_call(output: str) -> None:
    if f"invoked tool: {REQUIRED_TOOL}" not in output:
        raise RuntimeError(f"Expected {REQUIRED_TOOL} invocation was not found in RepoDoctor output.")


def normalize_transcript(output: str) -> list[tuple[str, str]]:
    lines: list[tuple[str, str]] = [
        ("cmd", "$ dotnet run --project examples/RepoDoctor -- /repo"),
    ]
    streaming = False
    for raw in output.splitlines():
        text = raw.replace(str(ROOT), "").replace(str(Path(tempfile.gettempdir())), "/tmp")
        if text.startswith("workspace:"):
            text = f"workspace: {DISPLAY_WORKSPACE}"
        elif text.startswith("provider:"):
            text = f"provider: /codexflow/CodexFlow/appsettings.json#{DEFAULT_PROVIDER_SECTION}"
        if text.startswith("trace:") and "/.qre/runs/" in text:
            text = f"trace: {DISPLAY_WORKSPACE}/.qre/runs/<run-id>/events.jsonl"
        elif text.startswith("run_directory:") and "/.qre/runs/" in text:
            text = f"run_directory: {DISPLAY_WORKSPACE}/.qre/runs/<run-id>"

        kind = "text"
        if text.startswith("RepoDoctor"):
            kind = "title"
        elif text.startswith("Streaming model answer"):
            kind = "section"
            streaming = True
        elif text.startswith("Recorded replay"):
            kind = "section"
            streaming = False
        elif text.startswith("run_id:") or text.startswith("termination:") or text.startswith("runner:") or text.startswith("tools:") or text.startswith("trace:") or text.startswith("run_directory:") or text.startswith("finalText:"):
            kind = "meta"
        elif text.startswith("provider:") or text.startswith("model:") or text.startswith("api_mode:") or text.startswith("registered tool:"):
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
        draw.text((width - 270, 20), "RepoDoctor streaming demo", fill="#94a3b8", font=font)

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
