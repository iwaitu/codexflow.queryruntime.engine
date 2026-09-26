#!/usr/bin/env bash
# Publish CodexFlow.QueryRuntime.Cli with Native AOT and fail on any
# unapproved trim/AOT (IL2xxx / IL3xxx / trim) warning.
#
# This is the executable core of P3 "Native AOT Blocking CI": it produces the
# native `qre` binary that the smoke step exercises, and it enforces the
# "no unapproved trim/AOT warnings" acceptance criterion against an explicit
# allowlist (scripts/qre-aot-approved-warnings.txt).
#
# Usage:
#   scripts/qre-aot-gate.sh <rid> [configuration]
#
# Environment:
#   QRE_AOT_RID            Default RID when <rid> is omitted (default native RID).
#   QRE_CONFIGURATION      Default configuration (default Release).
#   QRE_APPROVED_WARNINGS  Path to the approved-warnings allowlist.
#   QRE_PACKAGE_VERSION    Optional package/assembly version stamped into the binary.
#
# On success it prints "qre_binary=<path>" as the last line so callers can
# locate the produced native binary.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

detect_native_rid() {
  local os arch
  os="$(uname -s)"
  arch="$(uname -m)"

  case "${os}" in
    Darwin) os="osx" ;;
    Linux) os="linux" ;;
    MINGW*|MSYS*|CYGWIN*) os="win" ;;
    *) echo "Unsupported OS for default Native AOT RID: ${os}" >&2; exit 1 ;;
  esac

  case "${arch}" in
    arm64|aarch64) arch="arm64" ;;
    x86_64|amd64) arch="x64" ;;
    *) echo "Unsupported architecture for default Native AOT RID: ${arch}" >&2; exit 1 ;;
  esac

  printf '%s-%s\n' "${os}" "${arch}"
}

export DOTNET_CLI_UI_LANGUAGE="${DOTNET_CLI_UI_LANGUAGE:-en}"

RID="${1:-${QRE_AOT_RID:-$(detect_native_rid)}}"
CONFIGURATION="${2:-${QRE_CONFIGURATION:-Release}}"
APPROVED="${QRE_APPROVED_WARNINGS:-${ROOT_DIR}/scripts/qre-aot-approved-warnings.txt}"
PUBLISH_PROPERTIES=()
if [[ -n "${QRE_PACKAGE_VERSION:-}" ]]; then
  PUBLISH_PROPERTIES+=(
    "-p:Version=${QRE_PACKAGE_VERSION}"
    "-p:AssemblyInformationalVersion=${QRE_PACKAGE_VERSION}"
  )
fi

PROJECT="${ROOT_DIR}/CodexFlow.QueryRuntime.Cli"
LOG_FILE="$(mktemp "${TMPDIR:-/tmp}/qre-aot-publish.XXXXXX")"
trap 'rm -f "${LOG_FILE}"' EXIT

echo "== Native AOT publish gate =="
echo "rid:           ${RID}"
echo "configuration: ${CONFIGURATION}"
echo "approved list: ${APPROVED}"
echo

# TrimmerSingleWarn=false reports every trim/AOT warning individually instead of
# collapsing them into one per-assembly warning, so the allowlist can reason
# about each warning rather than an opaque rollup.
set +e
dotnet publish "${PROJECT}" \
  -c "${CONFIGURATION}" \
  -r "${RID}" \
  -p:PublishAot=true \
  -p:SelfContained=true \
  -p:TrimmerSingleWarn=false \
  ${PUBLISH_PROPERTIES[@]+"${PUBLISH_PROPERTIES[@]}"} \
  2>&1 | tee "${LOG_FILE}"

PIPE_STATUSES=("${PIPESTATUS[@]}")
set -e
PUBLISH_STATUS="${PIPE_STATUSES[0]}"
if [[ "${PUBLISH_STATUS}" -ne 0 ]]; then
  echo "Native AOT publish failed (exit ${PUBLISH_STATUS})." >&2
  exit "${PUBLISH_STATUS}"
fi

if [[ "${PIPE_STATUSES[1]}" -ne 0 ]]; then
  echo "Could not capture the Native AOT publish log." >&2
  exit "${PIPE_STATUSES[1]}"
fi

# Normalize trim/AOT warnings: keep only the "ILxxxx: message" tail and drop the
# trailing " [/path/to/project.csproj]" MSBuild suffix so entries are stable
# across machines and absolute paths.
OBSERVED="$(grep -aoE "warning (IL[0-9]+|TRIM[0-9]+).*" "${LOG_FILE}" \
  | sed -E 's/^warning //' \
  | sed -E 's/[[:space:]]*\[[^][]*\.(csproj|fsproj|vbproj)\][[:space:]]*$//' \
  | sed -E 's/[[:space:]]*$//' \
  | sort -u || true)"

# Read the allowlist, stripping comments and blank lines.
if [[ -f "${APPROVED}" ]]; then
  ALLOWED="$(grep -avE '^[[:space:]]*(#|$)' "${APPROVED}" | sed -E 's/[[:space:]]*$//' | sort -u || true)"
else
  ALLOWED=""
fi

echo
if [[ -z "${OBSERVED}" ]]; then
  echo "No trim/AOT warnings observed."
else
  echo "Observed trim/AOT warnings:"
  echo "${OBSERVED}" | sed 's/^/  /'
fi

# Any observed warning that is not in the allowlist is unapproved.
UNAPPROVED=""
if [[ -n "${OBSERVED}" ]]; then
  while IFS= read -r WARNING_LINE; do
    [[ -n "${WARNING_LINE}" ]] || continue
    if ! grep -Fxq -- "${WARNING_LINE}" <<< "${ALLOWED}"; then
      UNAPPROVED+="${WARNING_LINE}"$'\n'
    fi
  done <<< "${OBSERVED}"
fi
UNAPPROVED="${UNAPPROVED%$'\n'}"

if [[ -n "${UNAPPROVED}" ]]; then
  echo
  echo "ERROR: unapproved trim/AOT warnings detected:" >&2
  echo "${UNAPPROVED}" | sed 's/^/  /' >&2
  echo >&2
  echo "Review the warning, then either fix the AOT/trim incompatibility or add" >&2
  echo "the normalized line to ${APPROVED} with a rationale." >&2
  exit 1
fi

QRE_BIN="${PROJECT}/bin/${CONFIGURATION}/net10.0/${RID}/publish/qre"
if [[ "${RID}" == win-* ]]; then
  QRE_BIN="${QRE_BIN}.exe"
fi
if [[ ! -f "${QRE_BIN}" ]]; then
  echo "Published qre binary was not found: ${QRE_BIN}" >&2
  exit 1
fi

echo
echo "Native AOT publish gate passed (no unapproved trim/AOT warnings)."
echo "qre_binary=${QRE_BIN}"
