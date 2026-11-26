#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

OUTPUT_DIR="${OUTPUT_DIR:-${REPO_ROOT}/dist/nuget}"
CONFIGURATION="Release"
NO_BUILD=0

usage() {
  cat <<'EOF'
Usage: scripts/pack-sdk-nuget.sh [options] [output_dir]

Packs LocalExile.Client.Sdk and LocalExile.Client.ServerBridge as versioned
local NuGet packages (.nupkg + .snupkg) with XML documentation included.

Options:
  -o, --output <dir>         Output directory (default: dist/nuget)
  -c, --configuration <cfg>  Build configuration (default: Release)
      --no-build             Skip building projects before packing
  -h, --help                 Show this help message
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -o|--output)
      OUTPUT_DIR="$2"
      shift 2
      ;;
    -c|--configuration)
      CONFIGURATION="$2"
      shift 2
      ;;
    --no-build)
      NO_BUILD=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    -*)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 1
      ;;
    *)
      OUTPUT_DIR="$1"
      shift
      ;;
  esac
done

mkdir -p "${OUTPUT_DIR}"

PACK_ARGS=(
  -c "${CONFIGURATION}"
  -o "${OUTPUT_DIR}"
  --nologo
  -p:TreatWarningsAsErrors=true
)

if [[ "${NO_BUILD}" -eq 1 ]]; then
  PACK_ARGS+=(--no-build)
fi

PROJECTS=(
  "${REPO_ROOT}/src/LocalExile.Client.Sdk/LocalExile.Client.Sdk.csproj"
  "${REPO_ROOT}/src/LocalExile.Client.ServerBridge/LocalExile.Client.ServerBridge.csproj"
)

echo "Packing LocalExile NativeMods NuGet packages (${CONFIGURATION}) -> ${OUTPUT_DIR}"
for proj in "${PROJECTS[@]}"; do
  echo "  -> dotnet pack $(basename "${proj}")"
  dotnet pack "${proj}" "${PACK_ARGS[@]}"
done

SDK_NUPKG="$(find "${OUTPUT_DIR}" -maxdepth 1 -type f -name 'LocalExile.Client.Sdk.*.nupkg' | sort | tail -n 1)"
BRIDGE_NUPKG="$(find "${OUTPUT_DIR}" -maxdepth 1 -type f -name 'LocalExile.Client.ServerBridge.*.nupkg' | sort | tail -n 1)"

if [[ -z "${SDK_NUPKG}" || -z "${BRIDGE_NUPKG}" ]]; then
  echo "ERROR: Expected both LocalExile.Client.Sdk and LocalExile.Client.ServerBridge .nupkg files in ${OUTPUT_DIR}" >&2
  exit 1
fi

echo "Packed NuGet artifacts:"
ls -lh "${OUTPUT_DIR}"/LocalExile.Client.*.nupkg "${OUTPUT_DIR}"/LocalExile.Client.*.snupkg 2>/dev/null || ls -lh "${OUTPUT_DIR}"/LocalExile.Client.*.nupkg
