#!/usr/bin/env python3
"""Generate the README terminal demo assets.

The script runs the real qre CLI in offline mode, then renders a compact terminal
walkthrough as both an asciinema v2 cast and a GIF.
"""

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
GIF_PATH = ASSET_DIR / "qre-demo.gif"
CAST_PATH = ASSET_DIR / "qre-demo.cast"

MODEL_RESPONSE = "QRE turns an agent run into a recorded trace you can inspect and replay."
PROMPT = "What does QRE do?"
DISPLAY_WORKSPACE = "demo"


def main() -> None:
    ASSET_DIR.mkdir(parents=True, exist_ok=True)
    workspace = Path(tempfile.mkdtemp(prefix="qre-readme-demo-"))
    try:
        run_json = run_qre(["run", "--workspace", str(workspace), "--response", MODEL_RESPONSE, "--json", PROMPT])
        trace_lines = run_qre(["trace", "latest", "--workspace", str(workspace), "--jsonl"]).splitlines()
        replay_json = run_qre(["replay", "latest", "--workspace", str(workspace), "--json"])
    finally:
        shutil.rmtree(workspace, ignore_errors=True)

    transcript = build_transcript(run_json, trace_lines, replay_json)
    write_cast(transcript)
    write_gif(transcript)
    print(f"wrote {GIF_PATH.relative_to(ROOT)}")
    print(f"wrote {CAST_PATH.relative_to(ROOT)}")


def run_qre(args: list[str]) -> str:
    qre_bin = os.environ.get("QRE_BIN")
    if qre_bin:
        command = [qre_bin, *args]
    else:
        command = ["dotnet", "run", "--project", str(ROOT / "CodexFlow.QueryRuntime.Cli"), "--", *args]

    result = subprocess.run(
        command,
        cwd=ROOT,
        check=True,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    return result.stdout.strip()


def build_transcript(run_json: str, trace_lines: list[str], replay_json: str) -> list[tuple[str, str]]:
    run = json.loads(run_json)
    replay = json.loads(replay_json)
    trace_events = [json.loads(line) for line in trace_lines[:6]]

    lines: list[tuple[str, str]] = [
        ("title", "QRE: run -> trace -> replay"),
        ("blank", ""),
        (
            "cmd",
            f'$ qre run -w {DISPLAY_WORKSPACE} --response "offline model" --json "{PROMPT}"',
        ),
        ("json", "{"),
        ("json", f'  "type": "{run["type"]}",'),
        ("json", f'  "finalText": "{run["finalText"]}",'),
        ("json", f'  "traceFilePath": "{DISPLAY_WORKSPACE}/.qre/runs/<run-id>/events.jsonl",'),
        ("json", f'  "totalRounds": {run["totalRounds"]}, "totalToolCalls": {run["totalToolCalls"]}'),
        ("json", "}"),
        ("blank", ""),
        ("cmd", f"$ qre trace latest --workspace {DISPLAY_WORKSPACE} --jsonl"),
    ]

    for event in trace_events:
        event_type = event["eventType"]
        payload = event.get("payload", {})
        if event_type == "run.started":
            summary = {"eventType": event_type, "prompt": payload.get("Prompt")}
        elif event_type == "model.request":
            summary = {
                "eventType": event_type,
                "messageCount": payload.get("MessageCount"),
                "toolCallsAllowed": payload.get("ToolCallsAllowed"),
            }
        elif event_type == "model.response":
            summary = {
                "eventType": event_type,
                "assistantTextLength": payload.get("AssistantTextLength"),
                "toolCalls": payload.get("StructuredToolCallCount"),
            }
        elif event_type == "runtime.terminated":
            summary = {
                "eventType": event_type,
                "reason": payload.get("Reason"),
                "totalRounds": payload.get("TotalRounds"),
            }
        else:
            summary = {"eventType": event_type}
        lines.append(("jsonl", json.dumps(summary, separators=(",", ":"))))

    lines.extend(
        [
            ("blank", ""),
            ("cmd", f"$ qre replay latest --workspace {DISPLAY_WORKSPACE} --json"),
            ("json", "{"),
            ("json", f'  "type": "{replay["type"]}",'),
            ("json", f'  "runner": "{replay["runner"]}",'),
            ("json", f'  "finalText": "{replay["finalText"]}",'),
            ("json", f'  "totalRounds": {replay["totalRounds"]}, "totalToolCalls": {replay["totalToolCalls"]}'),
            ("json", "}"),
            ("blank", ""),
            ("accent", "Recorded replay: no provider call, no tool re-execution."),
        ]
    )
    return lines


def write_cast(transcript: list[tuple[str, str]]) -> None:
    header = {"version": 2, "width": 96, "height": 28, "timestamp": 1780506000, "env": {"SHELL": "/bin/bash", "TERM": "xterm-256color"}}
    elapsed = 0.0
    events: list[str] = [json.dumps(header)]
    for kind, text in transcript:
        elapsed += 0.35 if kind == "blank" else 0.8
        rendered = text + "\r\n"
        events.append(json.dumps([round(elapsed, 2), "o", rendered]))
    events.append(json.dumps([30.0, "o", ""]))
    CAST_PATH.write_text("\n".join(events) + "\n", encoding="utf-8")


def write_gif(transcript: list[tuple[str, str]]) -> None:
    width, height = 1000, 560
    margin_x, margin_y = 30, 54
    line_height = 20
    bg = "#111827"
    header = "#0b1220"
    text_color = "#d1d5db"
    cmd_color = "#f9fafb"
    json_color = "#93c5fd"
    jsonl_color = "#86efac"
    accent_color = "#fbbf24"
    prompt_color = "#38bdf8"

    font = load_font(16)
    title_font = load_font(17)
    def render_frame(width: int, height: int, margin_x: int, margin_y: int, line_height: int, lines: list[tuple[str, str]], font: ImageFont.ImageFont, title_font: ImageFont.ImageFont) -> Image.Image:
        image = Image.new("RGB", (width, height), bg)
        draw = ImageDraw.Draw(image)
        draw.rounded_rectangle((12, 12, width - 12, height - 12), radius=12, fill=bg, outline="#243244", width=2)
        draw.rounded_rectangle((12, 12, width - 12, 42), radius=12, fill=header)
        draw.rectangle((12, 30, width - 12, 42), fill=header)
        draw.ellipse((30, 23, 42, 35), fill="#ef4444")
        draw.ellipse((50, 23, 62, 35), fill="#f59e0b")
        draw.ellipse((70, 23, 82, 35), fill="#22c55e")
        draw.text((width - 206, 20), "qre demo", fill="#94a3b8", font=font)

        start = max(0, len(lines) - 23)
        y = margin_y
        for kind, text in lines[start:]:
            wrapped = wrap_line(text, width=108)
            for index, segment in enumerate(wrapped):
                if kind == "title":
                    color = prompt_color
                    active_font = title_font
                elif kind == "cmd":
                    color = cmd_color
                    active_font = font
                elif kind == "json":
                    color = json_color
                    active_font = font
                elif kind == "jsonl":
                    color = jsonl_color
                    active_font = font
                elif kind == "accent":
                    color = accent_color
                    active_font = title_font
                else:
                    color = text_color
                    active_font = font
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
        if kind == "cmd":
            for partial in reveal_text(text, step=4):
                frames.append(render_frame(width, height, margin_x, margin_y, line_height, visible + [(kind, partial)], font, title_font))
                durations.append(45)
            visible.append((kind, text))
            frames.append(render_frame(width, height, margin_x, margin_y, line_height, visible, font, title_font))
            durations.append(450)
        else:
            visible.append((kind, text))
            frames.append(render_frame(width, height, margin_x, margin_y, line_height, visible, font, title_font))
            durations.append(650 if kind != "blank" else 250)

    total_duration = sum(durations)
    if total_duration < 30000:
        durations[-1] += 30000 - total_duration

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
