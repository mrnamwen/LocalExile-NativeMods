#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

EMPTY_ICON="${EMPTY_ICON:-${REPO_ROOT}/examples/DualMod.SiphonOrb/assets-src/siphon_orb_empty.png}"
CHARGED_ICON="${CHARGED_ICON:-${REPO_ROOT}/examples/DualMod.SiphonOrb/assets-src/siphon_orb_charged.png}"
POE_CLIENT_DIR="${POE_CLIENT_DIR:-/home/nam/Git/LocalExile/Client}"
OUT_DIR="${OUT_DIR:-${REPO_ROOT}/dist/siphon-assets}"
BASE_OVERLAY_DIR="${BASE_OVERLAY_DIR:-}"
ICON_SIZE="${ICON_SIZE:-78}"

usage() {
  cat <<EOF
Usage: $(basename "$0") [options]

Builds the complete Path of Exile 3.29.3d Orb of Siphoning custom currency asset overlay
(78x78 flood-fill alpha-cutout RGBA8 .dds + .dds.header icons, .it metadata files, and
non-destructively appended BaseItemTypes / ItemVisualIdentity / CurrencyItems .datc64 tables)
into gitignored dist/siphon-assets/.

Options:
  --empty-icon <path>    Path to empty orb PNG       (default: ${EMPTY_ICON})
  --charged-icon <path>  Path to charged orb PNG     (default: ${CHARGED_ICON})
  --client <dir>         Read-only PoE 3.29 Client dir or loose Data/*.datc64 dir (default: ${POE_CLIENT_DIR})
  --base-overlay <dir>   Optional existing patched overlay dir to layer currency .datc64 onto
  --icon-size <px>       Square icon dimension in px (default: ${ICON_SIZE})
  --out <dir>            Output overlay directory    (default: ${OUT_DIR})
  -h, --help             Show this help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --empty-icon)    EMPTY_ICON="$2"; shift 2 ;;
    --charged-icon)  CHARGED_ICON="$2"; shift 2 ;;
    --client)        POE_CLIENT_DIR="$2"; shift 2 ;;
    --base-overlay)  BASE_OVERLAY_DIR="$2"; shift 2 ;;
    --icon-size)     ICON_SIZE="$2"; shift 2 ;;
    --out)           OUT_DIR="$2"; shift 2 ;;
    -h|--help)       usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 1 ;;
  esac
done

for req in dotnet magick; do
  if ! command -v "$req" >/dev/null 2>&1; then
    echo "ERROR: Required tool '$req' not found in PATH." >&2
    exit 1
  fi
done

if [[ ! -f "${EMPTY_ICON}" ]]; then
  echo "ERROR: Empty Siphon Orb icon not found: ${EMPTY_ICON}" >&2
  exit 1
fi
if [[ ! -f "${CHARGED_ICON}" ]]; then
  echo "ERROR: Charged Siphon Orb icon not found: ${CHARGED_ICON}" >&2
  exit 1
fi
if [[ ! -d "${POE_CLIENT_DIR}" ]]; then
  echo "ERROR: PoE Client / stock dat directory not found: ${POE_CLIENT_DIR}" >&2
  exit 1
fi

PIPELINE_PROJ="${REPO_ROOT}/src/LocalExile.Client.AssetPipeline/LocalExile.Client.AssetPipeline.csproj"

echo "[1/3] Building LocalExile.Client.AssetPipeline (Release)..."
dotnet build "${PIPELINE_PROJ}" -c Release --nologo -v minimal

echo "[2/3] Generating PoE 3.29.3d Orb of Siphoning assets into ${OUT_DIR}..."
rm -rf "${OUT_DIR}"
mkdir -p "${OUT_DIR}"

BUILD_ARGS=(
  build-siphon
  --empty-icon "${EMPTY_ICON}"
  --charged-icon "${CHARGED_ICON}"
  --client "${POE_CLIENT_DIR}"
  --icon-size "${ICON_SIZE}"
  --out "${OUT_DIR}"
)
if [[ -n "${BASE_OVERLAY_DIR}" ]]; then
  BUILD_ARGS+=(--base-overlay "${BASE_OVERLAY_DIR}")
fi

dotnet run --project "${PIPELINE_PROJ}" -c Release --no-build -- "${BUILD_ARGS[@]}"

echo "[3/3] Running standalone verification pass..."
dotnet run --project "${PIPELINE_PROJ}" -c Release --no-build -- \
  verify-siphon \
  --dir "${OUT_DIR}"

echo "Done! Generated asset tree (${OUT_DIR}):"
find "${OUT_DIR}" -type f | sort | sed "s|^${OUT_DIR}/|  |"
