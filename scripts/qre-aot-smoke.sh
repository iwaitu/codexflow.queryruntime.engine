#!/usr/bin/env bash
# Smoke-test a produced native `qre` binary (the AOT-published CLI, not a
# framework-dependent build). Exercises the P3 smoke surface:
#   - qre --version
#   - qre run (offline, deterministic response)
#   - qre tool list
#   - qre replay latest (recorded / non-strict)
#   - qre replay latest --strict, including a same-source-trace determinism
#     check (two isolated workspace copies must yield the same replayDigest)
#
# Usage:
#   scripts/qre-aot-smoke.sh <path-to-qre-binary> [scratch-dir]
#
# Uses only grep/sed for JSON field extraction so it needs no jq/python.
set -euo pipefail

QRE_BIN="${1:?usage: qre-aot-smoke.sh <path-to-qre-binary> [scratch-dir]}"
SCRATCH="${2:-$(mktemp -d "${TMPDIR:-/tmp}/qre-aot-smoke.XXXXXX")}"

if [[ ! -f "${QRE_BIN}" ]]; then
  echo "qre binary not found: ${QRE_BIN}" >&2
  exit 1
fi
if [[ ! -x "${QRE_BIN}" ]]; then
  echo "qre binary is not executable: ${QRE_BIN}" >&2
  exit 1
fi

mkdir -p "${SCRATCH}"

# Extract a compact-JSON string field: json_str <file> <key>
json_str() {
  grep -aoE "\"$2\":\"[^\"]*\"" "$1" | head -n1 | sed -E "s/^\"$2\":\"//; s/\"$//" || true
}
# Extract a compact-JSON boolean/number field: json_raw <file> <key>
json_raw() {
  grep -aoE "\"$2\":(true|false|[0-9]+)" "$1" | head -n1 | sed -E "s/^\"$2\"://" || true
}

fail() { echo "SMOKE FAILED: $*" >&2; exit 1; }

echo "== qre --version =="
VERSION_OUT="$("${QRE_BIN}" --version)"
echo "${VERSION_OUT}"
[[ -n "${VERSION_OUT}" ]] || fail "--version produced no output"

echo
echo "== qre run (offline) =="
RUN_WS="${SCRATCH}/run"
mkdir -p "${RUN_WS}"
"${QRE_BIN}" run --workspace "${RUN_WS}" --response "offline smoke" --json "analyze this repo" > "${SCRATCH}/run.json"
cat "${SCRATCH}/run.json"; echo
[[ "$(json_str "${SCRATCH}/run.json" type)" == "qre.run.completed" ]] || fail "run did not report qre.run.completed"

echo
echo "== qre tool list =="
"${QRE_BIN}" tool list --workspace "${RUN_WS}" --profile readonly --json > "${SCRATCH}/tools.json"
cat "${SCRATCH}/tools.json"; echo
grep -aq "\"tools\"" "${SCRATCH}/tools.json" || fail "tool list did not emit a tools array"

echo
echo "== qre replay latest (recorded) =="
"${QRE_BIN}" replay latest --workspace "${RUN_WS}" --json > "${SCRATCH}/replay.json"
cat "${SCRATCH}/replay.json"; echo
[[ "$(json_str "${SCRATCH}/replay.json" type)" == "qre.replay.completed" ]] || fail "recorded replay did not complete"

echo
echo "== qre replay latest --strict (determinism) =="
# Copy the recorded run into two isolated workspaces so each strict replay reads
# a byte-identical source trace. The replayDigest excludes run-scoped IDs, so two
# replays of the same source trace must produce an identical digest.
W1="${SCRATCH}/strict-w1"
W2="${SCRATCH}/strict-w2"
rm -rf "${W1}" "${W2}"
cp -R "${RUN_WS}" "${W1}"
cp -R "${RUN_WS}" "${W2}"

"${QRE_BIN}" replay latest --workspace "${W1}" --strict --json > "${SCRATCH}/strict1.json"
"${QRE_BIN}" replay latest --workspace "${W2}" --strict --json > "${SCRATCH}/strict2.json"
cat "${SCRATCH}/strict1.json"; echo

D1="$(json_str "${SCRATCH}/strict1.json" replayDigest)"
D2="$(json_str "${SCRATCH}/strict2.json" replayDigest)"
MODE="$(json_str "${SCRATCH}/strict1.json" mode)"
PROVIDER_CALLS="$(json_raw "${SCRATCH}/strict1.json" providerCalls)"
TOOL_EXECS="$(json_raw "${SCRATCH}/strict1.json" toolExecutions)"

echo
echo "strict mode:       ${MODE}"
echo "provider_calls:    ${PROVIDER_CALLS}"
echo "tool_executions:   ${TOOL_EXECS}"
echo "replay_digest #1:  ${D1}"
echo "replay_digest #2:  ${D2}"

[[ "${MODE}" == "strict-replay" ]] || fail "strict replay mode was '${MODE}'"
[[ "${PROVIDER_CALLS}" == "false" ]] || fail "strict replay reported provider calls"
[[ "${TOOL_EXECS}" == "false" ]] || fail "strict replay reported tool executions"
[[ -n "${D1}" ]] || fail "strict replay produced no replayDigest"
[[ "${D1}" == "${D2}" ]] || fail "strict replay digest is not deterministic (${D1} != ${D2})"

echo
echo "Native AOT smoke passed."
