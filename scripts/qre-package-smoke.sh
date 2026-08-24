#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PACKAGE_DIR="${1:-${ROOT_DIR}/artifacts/nuget}"
PACKAGE_DIR="$(cd "${PACKAGE_DIR}" && pwd)"
SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/qre-package-smoke.XXXXXX")"
trap 'rm -rf "${SCRATCH}"' EXIT

cd "${PACKAGE_DIR}"
sha256sum -c SHA256SUMS
PACKAGE="$(find . -maxdepth 1 -name 'CodexFlow.QueryRuntime.Engine.*.nupkg' ! -name '*.snupkg' -print -quit)"
if [[ -z "${PACKAGE}" ]]; then
  echo "QueryRuntime Engine package was not found in ${PACKAGE_DIR}." >&2
  exit 1
fi
PACKAGE_VERSION="${PACKAGE#./CodexFlow.QueryRuntime.Engine.}"
PACKAGE_VERSION="${PACKAGE_VERSION%.nupkg}"

unzip -l "${PACKAGE}" > "${SCRATCH}/contents.txt"
for required in \
  'lib/net10.0/CodexFlow.QueryRuntime.Engine.dll' \
  'lib/net10.0/CodexFlow.QueryRuntime.Abstractions.dll' \
  'lib/net10.0/CodexFlow.QueryRuntime.Protocol.dll' \
  'README.md' \
  'icon.png'; do
  if ! grep -Fq "${required}" "${SCRATCH}/contents.txt"; then
    echo "Package is missing required asset: ${required}" >&2
    exit 1
  fi
done

dotnet new classlib --framework net10.0 --output "${SCRATCH}/Consumer" --no-restore
dotnet new nugetconfig --output "${SCRATCH}/Consumer" --force
dotnet nuget add source "${PACKAGE_DIR}" \
  --name qre-local \
  --configfile "${SCRATCH}/Consumer/nuget.config"
dotnet add "${SCRATCH}/Consumer/Consumer.csproj" package CodexFlow.QueryRuntime.Engine \
  --version "${PACKAGE_VERSION}"
dotnet build "${SCRATCH}/Consumer/Consumer.csproj" --configuration Release --no-restore

echo "Clean package checksum, contents, restore, and consumer build smoke passed."
