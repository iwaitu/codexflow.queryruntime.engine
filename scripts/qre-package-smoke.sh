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

MODELS_PACKAGE="./CodexFlow.QueryRuntime.Models.${PACKAGE_VERSION}.nupkg"
if [[ ! -f "${MODELS_PACKAGE}" ]]; then
  echo "QueryRuntime Models package ${PACKAGE_VERSION} was not found in ${PACKAGE_DIR}." >&2
  exit 1
fi
unzip -l "${MODELS_PACKAGE}" > "${SCRATCH}/models-contents.txt"
if ! grep -Fq 'lib/net10.0/CodexFlow.QueryRuntime.Models.dll' "${SCRATCH}/models-contents.txt"; then
  echo "Models package is missing lib/net10.0/CodexFlow.QueryRuntime.Models.dll" >&2
  exit 1
fi
# Protocol must come only from the Engine package, or consumers get two copies.
if grep -Eq 'CodexFlow\.QueryRuntime\.(Protocol|Abstractions|Engine)\.dll' "${SCRATCH}/models-contents.txt"; then
  echo "Models package must not bundle Engine, Protocol or Abstractions assemblies." >&2
  exit 1
fi
unzip -p "${MODELS_PACKAGE}" CodexFlow.QueryRuntime.Models.nuspec > "${SCRATCH}/models.nuspec"
if ! grep -Fq "<dependency id=\"CodexFlow.QueryRuntime.Engine\" version=\"${PACKAGE_VERSION}\"" "${SCRATCH}/models.nuspec"; then
  echo "Models package must depend on CodexFlow.QueryRuntime.Engine ${PACKAGE_VERSION}." >&2
  exit 1
fi
if grep -Fq 'CodexFlow.QueryRuntime.Protocol' "${SCRATCH}/models.nuspec"; then
  echo "Models package must not depend on an unpublished Protocol package." >&2
  exit 1
fi

dotnet new classlib --framework net10.0 --output "${SCRATCH}/Consumer" --no-restore
dotnet new nugetconfig --output "${SCRATCH}/Consumer" --force
dotnet nuget add source "${PACKAGE_DIR}" \
  --name qre-local \
  --configfile "${SCRATCH}/Consumer/nuget.config"
dotnet add "${SCRATCH}/Consumer/Consumer.csproj" package CodexFlow.QueryRuntime.Engine \
  --version "${PACKAGE_VERSION}"
dotnet add "${SCRATCH}/Consumer/Consumer.csproj" package CodexFlow.QueryRuntime.Models   --version "${PACKAGE_VERSION}"
cat > "${SCRATCH}/Consumer/ModelsSmoke.cs" <<'CS'
using CodexFlow.QueryRuntime.Models;
using CodexFlow.QueryRuntime.Protocol;

public static class ModelsSmoke
{
    public static string ProviderFor(string model) => QreModelProviderSelector.CreateDefault().Select(model).Id;

    public static IRuntimeModelClient? Client => null;
}
CS
dotnet build "${SCRATCH}/Consumer/Consumer.csproj" --configuration Release --no-restore

echo "Clean package checksum, contents, restore, and consumer build smoke passed."
