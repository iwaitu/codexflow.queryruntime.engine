#!/usr/bin/env python3
"""End-to-end release check for a built `qre` binary against a live model.

Every scenario runs the real CLI in a fresh copy of a generated workspace whose
facts are known, so the final answer can be checked, not just the exit code:

  fact          read-only tools find one constant in the workspace
  compaction    reads 20 files (about 48k tokens) to overflow the 32k context projection
  tool-search   the same lookup with deferred tools (`--tool-search`)
  replay        `trace latest`, `replay latest` and `replay latest --strict`
  resume        kill the process mid-run, then `qre resume latest`
  verify        the verify profile runs `git status` in the sandbox
  repair        the repair profile writes a file after `--approve-risk`
  json-output   `--json-output` returns a JSON object

Usage:
  QRE_API_URL=... QRE_API_KEY=... scripts/qre-e2e-live.py <qre-binary> --model <id> [options]

The API key is read from the environment and passed to `qre` through the
environment only; it is never written to disk or printed. Exit code is 0 when
every selected scenario passed.
"""
import argparse
import json
import os
import random
import shutil
import signal
import subprocess
import sys
import tempfile
import time

CODENAME = "amber-falcon-417"
CHAPTERS = 20
WORDS = ("river ledger copper window signal harbor matrix lantern orchard granite velvet compass "
         "thunder meadow cipher anchor ribbon summit pebble furnace garden bridge crystal shadow "
         "engine market falcon island mirror timber").split()


def chapter_marker(index):
    rng = random.Random(9000 + index)
    return f"{rng.choice(WORDS)}-{rng.randint(100, 999)}"


def build_fixture(root):
    """A small project with known facts. Each chapter is about 2,400 tokens."""
    os.makedirs(os.path.join(root, "src"))
    os.makedirs(os.path.join(root, "docs"))
    with open(os.path.join(root, "README.md"), "w") as f:
        f.write("# Inventory service\n\nA sample project used by the QRE end-to-end check.\n"
                "Configuration lives in `src/settings.py`; the handbook is under `docs/`.\n")
    with open(os.path.join(root, "src", "settings.py"), "w") as f:
        f.write('"""Service settings."""\n\nSERVICE_NAME = "inventory"\n'
                f'RELEASE_CODENAME = "{CODENAME}"\nMAX_SHELVES = 48\n')
    with open(os.path.join(root, "src", "inventory.py"), "w") as f:
        f.write("from settings import MAX_SHELVES\n\n\ndef shelves():\n    return list(range(MAX_SHELVES))\n")
    for index in range(1, CHAPTERS + 1):
        rng = random.Random(100 + index)
        lines = [f"# Handbook chapter {index:02d}", "", f"marker: {chapter_marker(index)}", ""]
        for row in range(150):
            lines.append(f"Shelf {row:03d} holds " + ", ".join(rng.choice(WORDS) for _ in range(9)) + ".")
        with open(os.path.join(root, "docs", f"chapter-{index:02d}.md"), "w") as f:
            f.write("\n".join(lines) + "\n")
    git = ["git", "-C", root, "-c", "user.name=qre-e2e", "-c", "user.email=qre-e2e@example.invalid"]
    subprocess.run(git + ["init", "-q"], check=True)
    subprocess.run(git + ["add", "-A"], check=True)
    subprocess.run(git + ["commit", "-q", "-m", "fixture"], check=True)


class Harness:
    def __init__(self, args):
        self.args = args
        self.qre = os.path.abspath(args.qre)
        self.scratch = os.path.abspath(args.scratch or tempfile.mkdtemp(prefix="qre-e2e-"))
        os.makedirs(self.scratch, exist_ok=True)
        self.fixture = os.path.join(self.scratch, "fixture")
        if not os.path.isdir(self.fixture):
            build_fixture(self.fixture)
        self.env = dict(os.environ, QRE_MODEL=args.model)
        if args.api_mode:
            self.env["QRE_API_MODE"] = args.api_mode
        self.results = []

    def workspace(self, name):
        path = os.path.join(self.scratch, name)
        shutil.rmtree(path, ignore_errors=True)
        shutil.copytree(self.fixture, path, symlinks=True)
        return path

    def qre_cmd(self, *parts):
        return [self.qre, *parts]

    def run_json(self, workspace, *parts, timeout=None):
        """Runs qre and parses the JSON object it prints; returns (exit code, object or None, stderr)."""
        completed = subprocess.run(
            self.qre_cmd(*parts), cwd=workspace, env=self.env, capture_output=True, text=True,
            timeout=timeout or self.args.timeout)
        return completed.returncode, parse_json(completed.stdout), completed.stderr.strip()

    def record(self, name, ok, detail, output=None):
        usage = ""
        if output:
            usage = (f" steps={output.get('totalSteps')} tools={output.get('totalToolCalls')}"
                     f" input={output.get('inputTokens')} cached={output.get('cachedInputTokens')}"
                     f" compactions={output.get('compactionCount')}")
        self.results.append((name, ok, detail))
        print(f"[{'PASS' if ok else 'FAIL'}] {self.args.model} {name}: {detail}{usage}", flush=True)

    def check_run(self, name, code, output, stderr, expect_text, extra=None):
        if output is None:
            return self.record(name, False, f"exit {code}, no JSON output; stderr: {stderr[-300:]}")
        problems = []
        if code != 0:
            problems.append(f"exit {code}")
        if output.get("status") != "Completed":
            problems.append(f"status {output.get('status')} ({output.get('errorCode')}: {(output.get('errorMessage') or '')[:160]})")
        if expect_text and expect_text not in (output.get("finalText") or ""):
            problems.append(f"answer lacks '{expect_text}': {(output.get('finalText') or '')[:120]!r}")
        for problem in (extra(output) if extra else []):
            problems.append(problem)
        self.record(name, not problems, "; ".join(problems) or "ok", output)
        return not problems

    # Scenarios ---------------------------------------------------------------------------------

    def fact(self):
        ws = self.workspace("fact")
        code, out, err = self.run_json(
            ws, "run", "--workspace", ws, "--profile", "readonly", "--max-rounds", "12",
            "--trace-data", "sanitized", "--json",
            "Find the value of RELEASE_CODENAME in this workspace. Reply with only the value.")
        ok = self.check_run("fact", code, out, err, CODENAME,
                            lambda o: [] if (o.get("totalToolCalls") or 0) >= 1 else ["no tool call was made"])
        return ws if ok else None

    def replay(self, ws):
        if ws is None:
            return self.record("replay", False, "skipped: the fact run it replays did not pass")
        problems = []
        for label, parts in (("trace", ["trace", "latest"]), ("replay", ["replay", "latest"]),
                             ("strict replay", ["replay", "latest", "--strict"])):
            code, out, err = self.run_json(ws, *parts, "--workspace", ws, "--json")
            if code != 0 or out is None:
                problems.append(f"{label}: exit {code} {err[-160:]}")
        self.record("replay", not problems, "; ".join(problems) or "trace, replay and strict replay ok")

    def compaction_prompt(self):
        return (f"Read docs/chapter-01.md to docs/chapter-{CHAPTERS:02d}.md with the file reading tool, one file per turn, "
                "in order, each file in full. Every file has a line starting with 'marker:'. After the last file, "
                f"reply with only the marker value of chapter {CHAPTERS:02d}.")

    def compaction(self):
        ws = self.workspace("compaction")
        code, out, err = self.run_json(
            ws, "run", "--workspace", ws, "--profile", "readonly", "--max-rounds", "64",
            "--trace-data", "sanitized", "--json", self.compaction_prompt())
        self.check_run(
            "compaction", code, out, err, chapter_marker(CHAPTERS),
            lambda o: [] if (o.get("compactionCount") or 0) >= 1 else ["the context was never compacted"])

    def tool_search(self):
        ws = self.workspace("tool-search")
        code, out, err = self.run_json(
            ws, "run", "--workspace", ws, "--profile", "readonly", "--tool-search", "--max-rounds", "14",
            "--trace-data", "sanitized", "--json",
            "Find the value of RELEASE_CODENAME in this workspace. Reply with only the value.")
        self.check_run("tool-search", code, out, err, CODENAME,
                       lambda o: [] if o.get("deferredToolSearch") else ["deferredToolSearch is not set"])

    def resume(self):
        ws = self.workspace("resume")
        command = self.qre_cmd(
            "run", "--workspace", ws, "--profile", "readonly", "--max-rounds", "64",
            "--trace-data", "private", "--json", self.compaction_prompt())
        interrupted = False
        for delay in (self.args.interrupt_after, self.args.interrupt_after / 2):
            ws = self.workspace("resume")
            command[3] = ws
            process = subprocess.Popen(command, cwd=ws, env=self.env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            time.sleep(delay)
            if process.poll() is None:
                process.send_signal(signal.SIGKILL)
                process.wait()
                interrupted = True
                break
            process.communicate()
        if not interrupted:
            return self.record("resume", False, "the run finished before it could be interrupted")
        code, out, err = self.run_json(ws, "resume", "latest", "--workspace", ws, "--trace-data", "private", "--json")
        self.check_run("resume", code, out, err, chapter_marker(CHAPTERS))

    def verify(self):
        ws = self.workspace("verify")
        with open(os.path.join(ws, "untracked-note.txt"), "w") as f:
            f.write("left by the e2e check\n")
        code, out, err = self.run_json(
            ws, "run", "--workspace", ws, "--profile", "verify", "--max-rounds", "8",
            "--trace-data", "sanitized", "--json",
            "Use the git status tool on this workspace. Reply with only the name of the untracked file.")
        self.check_run("verify", code, out, err, "untracked-note.txt")

    def repair(self):
        ws = self.workspace("repair")
        target = os.path.join(ws, "notes", "e2e.txt")
        code, out, err = self.run_json(
            ws, "run", "--workspace", ws, "--profile", "repair", "--approve-risk", "qre e2e release check",
            "--max-rounds", "10", "--trace-data", "sanitized", "--json",
            "Create the file notes/e2e.txt containing exactly the text QRE-E2E-OK, then reply with DONE.")

        def wrote(_):
            if not os.path.isfile(target):
                return ["notes/e2e.txt was not created"]
            content = open(target).read()
            return [] if "QRE-E2E-OK" in content else [f"notes/e2e.txt has unexpected content {content[:60]!r}"]
        ok = self.check_run("repair", code, out, err, None, wrote)
        if ok:
            diff = subprocess.run(self.qre_cmd("diff", "latest", "--workspace", ws), cwd=ws, env=self.env,
                                  capture_output=True, text=True, timeout=self.args.timeout)
            if diff.returncode != 0 or "e2e.txt" not in diff.stdout:
                self.record("repair-diff", False, f"qre diff latest: exit {diff.returncode}, e2e.txt not listed")
            else:
                self.record("repair-diff", True, "qre diff latest lists notes/e2e.txt")

    def json_output(self):
        ws = self.workspace("json-output")
        code, out, err = self.run_json(
            ws, "run", "--workspace", ws, "--profile", "none", "--json-output", "--max-rounds", "3",
            "--trace-data", "sanitized", "--json",
            f'Return a JSON object with one string property "codename" whose value is "{CODENAME}".')

        def is_object(o):
            try:
                value = json.loads(o.get("finalText") or "")
            except ValueError:
                return [f"final text is not JSON: {(o.get('finalText') or '')[:120]!r}"]
            return [] if isinstance(value, dict) and value.get("codename") == CODENAME else ["JSON lacks the codename"]
        self.check_run("json-output", code, out, err, None, is_object)


def parse_json(text):
    """The CLI prints one JSON object; tolerate anything before it."""
    start = text.find("{")
    if start < 0:
        return None
    try:
        return json.loads(text[start:])
    except ValueError:
        return None


SCENARIOS = ["fact", "replay", "compaction", "tool-search", "resume", "verify", "repair", "json-output"]


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("qre", help="path to the qre binary under test")
    parser.add_argument("--model", required=True, help="model id, passed to qre as QRE_MODEL")
    parser.add_argument("--api-mode", help="chat-completions, responses or anthropic-messages")
    parser.add_argument("--scratch", help="directory for the generated workspaces (kept afterwards)")
    parser.add_argument("--only", help="comma-separated scenario names; default is all: " + ", ".join(SCENARIOS))
    parser.add_argument("--skip", help="comma-separated scenario names to leave out")
    parser.add_argument("--timeout", type=int, default=420, help="seconds allowed per qre invocation")
    parser.add_argument("--interrupt-after", type=float, default=12, help="seconds before the resume scenario kills the run")
    args = parser.parse_args()
    for name in ("QRE_API_URL", "QRE_API_KEY"):
        if not os.environ.get(name):
            parser.error(f"{name} must be set in the environment")
    selected = args.only.split(",") if args.only else SCENARIOS
    selected = [name for name in selected if name not in (args.skip or "").split(",")]
    unknown = [name for name in selected if name not in SCENARIOS]
    if unknown:
        parser.error("unknown scenario: " + ", ".join(unknown))

    harness = Harness(args)
    fact_workspace = None
    for name in selected:
        try:
            if name == "fact":
                fact_workspace = harness.fact()
            elif name == "replay":
                harness.replay(fact_workspace)
            else:
                getattr(harness, name.replace("-", "_"))()
        except subprocess.TimeoutExpired:
            harness.record(name, False, f"timed out after {args.timeout}s")
    failed = [name for name, ok, _ in harness.results if not ok]
    print(f"SUMMARY {args.model}: {len(harness.results) - len(failed)}/{len(harness.results)} passed"
          + (f"; failed: {', '.join(failed)}" if failed else ""), flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
