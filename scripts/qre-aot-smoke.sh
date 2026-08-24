#!/usr/bin/env bash
# Smoke-test a produced native `qre` binary (the AOT-published CLI, not a
# framework-dependent build). Exercises the P3 smoke surface:
#   - qre --version
#   - qre run (offline, deterministic response)
#   - qre run --runtime v2 (C6 audit + C5 context/deferred catalog path)
#   - qre replay latest --runtime v2 (data-only C6 replay)
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
"${QRE_BIN}" run --workspace "${RUN_WS}" --trace-data sanitized --response "offline smoke" --json "analyze this repo" > "${SCRATCH}/run.json"
cat "${SCRATCH}/run.json"; echo
[[ "$(json_str "${SCRATCH}/run.json" type)" == "qre.run.completed" ]] || fail "run did not report qre.run.completed"

echo
echo "== qre run --runtime v2 (offline) =="
"${QRE_BIN}" run --runtime v2 --workspace "${RUN_WS}" --profile readonly --tool-search --trace-data sanitized --response "offline v2 smoke" --json "exercise the C6 audit and C5 context" > "${SCRATCH}/run-v2.json"
cat "${SCRATCH}/run-v2.json"; echo
[[ "$(json_str "${SCRATCH}/run-v2.json" type)" == "qre.v2.run.completed" ]] || fail "v2 run did not report qre.v2.run.completed"
[[ "$(json_str "${SCRATCH}/run-v2.json" status)" == "Completed" ]] || fail "v2 run did not complete"
[[ "$(json_str "${SCRATCH}/run-v2.json" profile)" == "readonly" ]] || fail "v2 run did not use readonly profile"
grep -aq '"qre_read_file"' "${SCRATCH}/run-v2.json" || fail "v2 run did not expose the frozen readonly tool catalog"
[[ "$(json_raw "${SCRATCH}/run-v2.json" deferredToolSearch)" == "true" ]] || fail "v2 run did not enable deferred tool selection"
[[ "$(json_str "${SCRATCH}/run-v2.json" contextEstimator)" == "utf8-bytes-div4-v2" ]] || fail "v2 run did not report the C5 context estimator"
[[ "$(json_raw "${SCRATCH}/run-v2.json" auditSchemaVersion)" == "1" ]] || fail "v2 run did not report audit schema v1"
[[ "$(json_str "${SCRATCH}/run-v2.json" auditDataMode)" == "SanitizedFixture" ]] || fail "v2 run did not persist a sanitized fixture"
[[ "$(json_str "${SCRATCH}/run-v2.json" auditReplayCapability)" == "Recorded" ]] || fail "v2 run did not report recorded replay capability"

echo
echo "== qre replay latest --runtime v2 (C6 data-only replay) =="
"${QRE_BIN}" replay latest --runtime v2 --workspace "${RUN_WS}" --strict --json > "${SCRATCH}/replay-v2.json"
cat "${SCRATCH}/replay-v2.json"; echo
[[ "$(json_str "${SCRATCH}/replay-v2.json" type)" == "qre.v2.replay.completed" ]] || fail "v2 recorded replay did not complete"
[[ "$(json_str "${SCRATCH}/replay-v2.json" mode)" == "strict-recorded-replay" ]] || fail "v2 replay did not use strict recorded mode"
[[ "$(json_raw "${SCRATCH}/replay-v2.json" providerCalls)" == "false" ]] || fail "v2 replay reported provider calls"
[[ "$(json_raw "${SCRATCH}/replay-v2.json" toolExecutions)" == "false" ]] || fail "v2 replay reported tool executions"
[[ -n "$(json_str "${SCRATCH}/replay-v2.json" replayDigest)" ]] || fail "v2 replay produced no replayDigest"

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
