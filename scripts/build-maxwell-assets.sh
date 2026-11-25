#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

MAXWELL_ZIP="${MAXWELL_ZIP:-/home/nam/Downloads/maxwell-the-cat.zip}"
MAXWELL_AUDIO="${MAXWELL_AUDIO:-/home/nam/Downloads/Mr Weebl - Stock Market (Maxwell Cat) [4PJo1q_zr0M].opus}"
POE_CLIENT_DIR="${POE_CLIENT_DIR:-/home/nam/Git/LocalExile/Client}"
OUT_DIR="${OUT_DIR:-${REPO_ROOT}/dist/maxwell-assets}"

usage() {
  cat <<EOF
Usage: $(basename "$0") [options]

Builds the complete Path of Exile 3.29.3d Maxwell the Cat Pet MTX asset overlay
(mesh, spinning+floating skeleton, materials, DDS + .dds.header textures, looping
Vorbis .ogg audio, and appended .datc64 tables) into gitignored dist/maxwell-assets/.

Options:
  --zip <path>     Path to maxwell-the-cat.zip (default: ${MAXWELL_ZIP})
  --audio <path>   Path to Stock Market .opus  (default: ${MAXWELL_AUDIO})
  --client <dir>   Read-only PoE 3.29 Client dir or loose Data/*.datc64 dir (default: ${POE_CLIENT_DIR})
  --out <dir>      Output overlay directory    (default: ${OUT_DIR})
  -h, --help       Show this help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --zip)    MAXWELL_ZIP="$2"; shift 2 ;;
    --audio)  MAXWELL_AUDIO="$2"; shift 2 ;;
    --client) POE_CLIENT_DIR="$2"; shift 2 ;;
    --out)    OUT_DIR="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 1 ;;
  esac
done

for req in dotnet magick ffmpeg; do
  if ! command -v "$req" >/dev/null 2>&1; then
    echo "ERROR: Required tool '$req' not found in PATH." >&2
    exit 1
  fi
done

if [[ ! -f "${MAXWELL_ZIP}" ]]; then
  echo "ERROR: Maxwell model archive not found: ${MAXWELL_ZIP}" >&2
  exit 1
fi
if [[ ! -f "${MAXWELL_AUDIO}" ]]; then
  echo "ERROR: Maxwell audio file not found: ${MAXWELL_AUDIO}" >&2
  exit 1
fi
if [[ ! -d "${POE_CLIENT_DIR}" ]]; then
  echo "ERROR: PoE Client / stock dat directory not found: ${POE_CLIENT_DIR}" >&2
  exit 1
fi

PIPELINE_PROJ="${REPO_ROOT}/src/LocalExile.Client.AssetPipeline/LocalExile.Client.AssetPipeline.csproj"

echo "[1/3] Building LocalExile.Client.AssetPipeline (Release)..."
dotnet build "${PIPELINE_PROJ}" -c Release --nologo -v minimal

echo "[2/3] Generating PoE 3.29.3d Maxwell assets into ${OUT_DIR}..."
rm -rf "${OUT_DIR}"
mkdir -p "${OUT_DIR}"

dotnet run --project "${PIPELINE_PROJ}" -c Release --no-build -- \
  build-maxwell \
  --zip "${MAXWELL_ZIP}" \
  --audio "${MAXWELL_AUDIO}" \
  --client "${POE_CLIENT_DIR}" \
  --out "${OUT_DIR}"

echo "[3/3] Running standalone verification pass..."
dotnet run --project "${PIPELINE_PROJ}" -c Release --no-build -- \
  verify-maxwell \
  --dir "${OUT_DIR}"

echo "Done! Generated asset tree (${OUT_DIR}):"
find "${OUT_DIR}" -type f | sort | sed "s|^${OUT_DIR}/|  |"
