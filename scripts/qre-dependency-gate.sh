#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPORT="$(mktemp "${TMPDIR:-/tmp}/qre-vulnerabilities.XXXXXX.json")"
trap 'rm -f "${REPORT}"' EXIT

cd "${ROOT_DIR}"
dotnet restore CodexFlow.QueryRuntime.slnx
dotnet package list \
  --project CodexFlow.QueryRuntime.slnx \
  --include-transitive \
  --vulnerable \
  --format json \
  --output-version 1 \
  --no-restore > "${REPORT}"

VULNERABILITY_COUNT="$(jq '[.. | objects | select(has("vulnerabilities")) | .vulnerabilities[]] | length' "${REPORT}")"
if [[ "${VULNERABILITY_COUNT}" -ne 0 ]]; then
  echo "Dependency vulnerability gate failed: ${VULNERABILITY_COUNT} vulnerable package record(s)." >&2
  jq '[.. | objects | select(has("vulnerabilities")) | {id: (.id // .name), vulnerabilities}]' "${REPORT}" >&2
  exit 1
fi

dotnet tool restore
dotnet tool run nuget-license -- \
  --input CodexFlow.QueryRuntime.slnx \
  --include-transitive \
  --allowed-license-types scripts/qre-allowed-licenses.json \
  --error-only

echo "Dependency vulnerability and license gates passed."
