#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="${1:-Release}"
OUTPUT_DIR="${QRE_NUGET_OUTPUT:-artifacts/nuget}"
PACKAGE_VERSION="${QRE_PACKAGE_VERSION:-}"

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

mkdir -p "$OUTPUT_DIR"
find "$OUTPUT_DIR" -maxdepth 1 \( -name '*.nupkg' -o -name '*.snupkg' \) -delete

PACK_ARGS=(
  --configuration "$CONFIGURATION"
  --output "$OUTPUT_DIR"
)

if [[ -n "$PACKAGE_VERSION" ]]; then
  PACK_ARGS+=(
    -p:PackageVersion="$PACKAGE_VERSION"
    -p:Version="$PACKAGE_VERSION"
    -p:AssemblyInformationalVersion="$PACKAGE_VERSION"
  )
fi

projects=(
  CodexFlow.QueryRuntime.Engine/CodexFlow.QueryRuntime.Engine.csproj
)

dotnet restore CodexFlow.QueryRuntime.slnx

for project in "${projects[@]}"; do
  dotnet pack "$project" "${PACK_ARGS[@]}" --no-restore
done

find "$OUTPUT_DIR" -maxdepth 1 \( -name '*.nupkg' -o -name '*.snupkg' \) -print | sort
