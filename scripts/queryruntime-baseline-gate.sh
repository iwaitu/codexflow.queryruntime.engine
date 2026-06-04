#!/usr/bin/env bash
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

RID="${QRE_AOT_RID:-$(detect_native_rid)}"
CONFIGURATION="${QRE_CONFIGURATION:-Release}"
INCLUDE_AOT=false
INCLUDE_DOCKER=false
INCLUDE_REAL_PROVIDER=false

usage() {
  cat <<'EOF'
Usage:
  scripts/queryruntime-baseline-gate.sh [options]

Options:
  --full                 Run the fast gate plus local Native AOT publish/smoke.
  --include-aot          Run local Native AOT publish/smoke.
  --include-docker       Run gated Docker sandbox integration tests.
  --include-real-provider
                         Run gated live provider integration tests.
  --rid <rid>            Native AOT RID. Defaults to QRE_AOT_RID or host RID.
  -h, --help             Show this help.

Default gate:
  - git diff --check
  - dotnet test CodexFlow.QueryRuntime.slnx --no-restore

Environment:
  QRE_AOT_RID                         Override AOT RID.
  QRE_CONFIGURATION                   Override configuration for AOT publish.
  RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true
                                       Required by real-provider tests.
  RUN_QUERY_RUNTIME_DOCKER_TESTS=true  Set automatically for --include-docker.
EOF
}

run() {
  echo
  echo "Running: $*"
  "$@"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --full)
      INCLUDE_AOT=true
      ;;
    --include-aot)
      INCLUDE_AOT=true
      ;;
    --include-docker)
      INCLUDE_DOCKER=true
      ;;
    --include-real-provider)
      INCLUDE_REAL_PROVIDER=true
      ;;
    --rid)
      shift
      if [[ $# -eq 0 || -z "${1:-}" ]]; then
        echo "--rid requires a value." >&2
        exit 1
      fi
      RID="$1"
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
  shift
done

cd "${ROOT_DIR}"

echo "QueryRuntime baseline gate"
echo "root: ${ROOT_DIR}"
echo "configuration: ${CONFIGURATION}"
echo "aot_rid: ${RID}"
echo "include_aot: ${INCLUDE_AOT}"
echo "include_docker: ${INCLUDE_DOCKER}"
echo "include_real_provider: ${INCLUDE_REAL_PROVIDER}"

run git diff --check
run dotnet test CodexFlow.QueryRuntime.slnx --no-restore

if [[ "${INCLUDE_AOT}" == "true" ]]; then
  # Reuse the same publish + trim/AOT warning gate and native smoke that the
  # P3 CI lane runs, so the local baseline check and CI stay in lockstep.
  run "${ROOT_DIR}/scripts/qre-aot-gate.sh" "${RID}" "${CONFIGURATION}"

  QRE_BIN="${ROOT_DIR}/CodexFlow.QueryRuntime.Cli/bin/${CONFIGURATION}/net10.0/${RID}/publish/qre"
  if [[ "${RID}" == win-* ]]; then
    QRE_BIN="${QRE_BIN}.exe"
  fi
  if [[ ! -f "${QRE_BIN}" ]]; then
    echo "Published qre binary was not found: ${QRE_BIN}" >&2
    exit 1
  fi

  run "${ROOT_DIR}/scripts/qre-aot-smoke.sh" "${QRE_BIN}"
fi

if [[ "${INCLUDE_DOCKER}" == "true" ]]; then
  run env RUN_QUERY_RUNTIME_DOCKER_TESTS=true \
    dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj \
      --filter "FullyQualifiedName~DockerSandboxRunnerIntegrationTests" \
      --logger "console;verbosity=detailed"
fi

if [[ "${INCLUDE_REAL_PROVIDER}" == "true" ]]; then
  if [[ "${RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS:-}" != "true" ]]; then
    echo "Set RUN_QUERY_RUNTIME_REAL_INTEGRATION_TESTS=true before --include-real-provider." >&2
    exit 1
  fi

  run dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj \
    --filter "FullyQualifiedName~ExperimentalHarnessRealLlmPhaseTests" \
    --logger "console;verbosity=detailed"
fi

echo
echo "QueryRuntime baseline gate passed."
