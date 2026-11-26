#!/usr/bin/env bash
# Isolated Xvfb Live Proton E2E Harness for NativeMods Phase 0 (0.2.0 SDK & C ABI on PoE 3.29.3d).
#
# Safety guarantees (NON-NEGOTIABLE):
#   1. Strictly isolated virtual screen via Xvfb (display >= 50, default 98; unsets WAYLAND_DISPLAY,
#      HYPRLAND_INSTANCE_SIGNATURE, HYPRLAND_CMD, XDG_CURRENT_DESKTOP, XDG_SESSION_DESKTOP,
#      XDG_SESSION_TYPE, XDG_BACKEND, SDL_VIDEODRIVER, GDK_BACKEND, QT_QPA_PLATFORM; sets
#      PROTON_ENABLE_WAYLAND=0; verifies 0 Hyprland window leaks across 6 checkpoints).
#   2. Isolated PipeWire/PulseAudio module-null-sink by default (--audible opts into default sink),
#      capturing 4s FMOD PCM output and running ffmpeg volumedetect without disturbing user audio.
#   3. Always launches the client ONLY through LocalExile's scripts/dev/live.ps1 -NoBuild -NoExtract.
#   4. Never leaves /home/nam/Git/LocalExile/ modified: stages under LocalExile/data/ (steamrt4
#      visibility), snapshots Game.exe SHA-256, Game.patched.exe SHA-256, LocalExile/Mods/ tree,
#      and data/characters.db{,-wal,-shm}, restores unconditionally in cleanup() with re-entry guard,
#      and exits non-zero if any SHA-256 or Mods/ verification mismatches.
#   5. Simultaneously stages DualMod.MaxwellPet, DualMod.SiphonOrb, and LocalExile.Client.Phase0E2E
#      with semicolon-separated NATIVEMODS_OVERLAY_DIR and Game_MODS_FOLDER, and verifies all
#      [PHASE0-E2E] ASSERT E2E-01..E2E-16 PASS markers.
#
# Usage:
#   ./scripts/run-phase0-proton.sh [options]
#
# Options:
#   --mode <proxy|import-patch>       Injection mode (default: proxy)
#   --display <N>                     Preferred Xvfb display >= 50 (default: 98; auto-increments if busy)
#   --localexile-dir <path>           Path to LocalExile checkout (default: /home/nam/Git/LocalExile)
#   --dist-dir <path>                 Path to Win64 extender bundle (default: <repo>/dist/win-x64)
#   --maxwell-assets-dir <path>       Path to Maxwell 3.29.3d overlay (default: <repo>/dist/maxwell-assets)
#   --siphon-assets-dir <path>        Path to Siphon 3.29.3d overlay (default: <repo>/dist/siphon-assets)
#   --log-dir <path>                  Directory for harness logs (default: <repo>/build/phase0-proton-logs)
#   --screenshot-dir <path>           Directory for PNG screenshots (default: <run-log-dir>/screenshots)
#   --soak-seconds <N>                Extra soak seconds after live probe (default: 10)
#   --rebuild                         Force Release rebuild of win64, assets, MaxwellPet, SiphonOrb, Phase0E2E
#   --audible                         Route audio to default sink instead of PipeWire null-sink
#   --trace-files                     Export NATIVEMODS_TRACE_FILES=1
#   --no-auto-input                   Boot + passive probe + soak only (skip xdotool login/input)
#   --keep-save                       Skip data/characters.db* restore on exit (default: snapshot & restore)
#   --keep-servers                    Call live.ps1 -Stop -ClientOnly on exit (still restores Client/)
#   --dry-run                         Validate tools, paths, clean Client/, build status; print staging,
#                                     NATIVEMODS_* env, and pwsh command; exit 0 with ZERO writes
#   -h, --help                        Show help

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

MODE="proxy"
DISPLAY_NUM=98
LOCALEXILE_DIR="/home/nam/Git/LocalExile"
DIST_DIR="${REPO_ROOT}/dist/win-x64"
MAXWELL_ASSETS_DIR="${REPO_ROOT}/dist/maxwell-assets"
SIPHON_ASSETS_DIR="${REPO_ROOT}/dist/siphon-assets"
LOG_DIR="${REPO_ROOT}/build/phase0-proton-logs"
SCREENSHOT_DIR=""
SOAK_SECONDS=10
REBUILD=0
AUDIBLE=0
TRACE_FILES=0
AUTO_INPUT=1
KEEP_SAVE=0
KEEP_SERVERS=0
DRY_RUN=0

usage() {
    sed -n '2,43p' "$0" | sed 's/^# \{0,1\}//'
}

require_arg() {
    local opt="$1"
    local val="${2:-}"
    if [[ -z "${val}" ]]; then
        echo "ERROR: Option ${opt} requires an argument." >&2
        usage >&2
        exit 2
    fi
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --mode)               require_arg "$1" "${2:-}"; MODE="$2"; shift 2 ;;
        --display)            require_arg "$1" "${2:-}"; DISPLAY_NUM="$2"; shift 2 ;;
        --localexile-dir)     require_arg "$1" "${2:-}"; LOCALEXILE_DIR="$2"; shift 2 ;;
        --dist-dir)           require_arg "$1" "${2:-}"; DIST_DIR="$2"; shift 2 ;;
        --maxwell-assets-dir) require_arg "$1" "${2:-}"; MAXWELL_ASSETS_DIR="$2"; shift 2 ;;
        --siphon-assets-dir)  require_arg "$1" "${2:-}"; SIPHON_ASSETS_DIR="$2"; shift 2 ;;
        --log-dir)            require_arg "$1" "${2:-}"; LOG_DIR="$2"; shift 2 ;;
        --screenshot-dir)     require_arg "$1" "${2:-}"; SCREENSHOT_DIR="$2"; shift 2 ;;
        --soak-seconds)       require_arg "$1" "${2:-}"; SOAK_SECONDS="$2"; shift 2 ;;
        --rebuild)            REBUILD=1; shift ;;
        --audible)            AUDIBLE=1; shift ;;
        --trace-files)        TRACE_FILES=1; shift ;;
        --no-auto-input)      AUTO_INPUT=0; shift ;;
        --keep-save)          KEEP_SAVE=1; shift ;;
        --keep-servers)       KEEP_SERVERS=1; shift ;;
        --dry-run)            DRY_RUN=1; shift ;;
        -h|--help)            usage; exit 0 ;;
        *)                    echo "ERROR: Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

if [[ "${MODE}" != "proxy" && "${MODE}" != "import-patch" ]]; then
    echo "ERROR: --mode must be 'proxy' or 'import-patch' (got '${MODE}')" >&2
    exit 2
fi

if ! [[ "${DISPLAY_NUM}" =~ ^[1-9][0-9]*$ ]] || (( 10#${DISPLAY_NUM} < 50 )); then
    echo "ERROR: --display must be a base-10 integer >= 50 without leading zeros (got '${DISPLAY_NUM}')" >&2
    exit 2
fi

if ! [[ "${SOAK_SECONDS}" =~ ^(0|[1-9][0-9]*)$ ]]; then
    echo "ERROR: --soak-seconds must be a non-negative base-10 integer without leading zeros (got '${SOAK_SECONDS}')" >&2
    exit 2
fi

# Auto-increment display if socket or lock exists (never touching :0 or :1)
while [[ -S "/tmp/.X11-unix/X${DISPLAY_NUM}" || -f "/tmp/.X${DISPLAY_NUM}-lock" ]]; do
    DISPLAY_NUM=$((10#${DISPLAY_NUM} + 1))
done

# 1. Validate required CLI tools
REQUIRED_TOOLS=(pwsh umu-run Xvfb xdotool ffmpeg import magick pactl python3 sha256sum dotnet)
for cmd in "${REQUIRED_TOOLS[@]}"; do
    if ! command -v "${cmd}" >/dev/null 2>&1; then
        echo "ERROR: Required command not found on PATH: ${cmd}" >&2
        exit 1
    fi
done

# 2. Validate LocalExile checkout & clean Client/
if [[ ! -d "${LOCALEXILE_DIR}" ]]; then
    echo "ERROR: LocalExile directory not found: ${LOCALEXILE_DIR}" >&2
    exit 1
fi
LOCALEXILE_DIR="$(cd "${LOCALEXILE_DIR}" && pwd)"

CLIENT_DIR="${LOCALEXILE_DIR}/Client"
GAME_EXE="${CLIENT_DIR}/Game.exe"
GAME_PATCHED_EXE="${CLIENT_DIR}/Game.patched.exe"
LIVE_PS1="${LOCALEXILE_DIR}/scripts/dev/live.ps1"

if [[ ! -f "${LIVE_PS1}" || ! -f "${GAME_EXE}" || ! -f "${GAME_PATCHED_EXE}" ]]; then
    echo "ERROR: LocalExile checkout or Client binaries missing under ${LOCALEXILE_DIR}" >&2
    echo "       Expected: ${LIVE_PS1}, ${GAME_EXE}, ${GAME_PATCHED_EXE}" >&2
    exit 1
fi

for check_file in "${CLIENT_DIR}/version.dll" "${CLIENT_DIR}/LocalExile.ClientHost.dll"; do
    if [[ -e "${check_file}" ]]; then
        echo "ERROR: Refusing to start: ${check_file} already exists in Client/" >&2
        exit 1
    fi
done

# 3. Project & build readiness descriptors
BUILD_WIN64_SCRIPT="${REPO_ROOT}/scripts/build-win64.sh"
BUILD_MAXWELL_ASSETS_SCRIPT="${REPO_ROOT}/scripts/build-maxwell-assets.sh"
BUILD_SIPHON_ASSETS_SCRIPT="${REPO_ROOT}/scripts/build-siphon-assets.sh"
PATCHER_CSPROJ="${REPO_ROOT}/src/LocalExile.Client.Patcher/LocalExile.Client.Patcher.csproj"

MAXWELL_ROOT="${REPO_ROOT}/examples/DualMod.MaxwellPet"
MAXWELL_SERVER_CSPROJ="${MAXWELL_ROOT}/Server/DualMod.MaxwellPet.Server.csproj"
MAXWELL_CLIENT_CSPROJ="${MAXWELL_ROOT}/Client/DualMod.MaxwellPet.Client.csproj"
MAXWELL_SERVER_MOD_DIR="${MAXWELL_ROOT}/Server/bin/Release/net10.0"
MAXWELL_CLIENT_MOD_DIR="${MAXWELL_ROOT}/Client/bin/Release/net10.0"

SIPHON_ROOT="${REPO_ROOT}/examples/DualMod.SiphonOrb"
SIPHON_SERVER_CSPROJ="${SIPHON_ROOT}/Server/DualMod.SiphonOrb.Server.csproj"
SIPHON_CLIENT_CSPROJ="${SIPHON_ROOT}/Client/DualMod.SiphonOrb.Client.csproj"
SIPHON_SERVER_MOD_DIR="${SIPHON_ROOT}/Server/bin/Release/net10.0"
SIPHON_CLIENT_MOD_DIR="${SIPHON_ROOT}/Client/bin/Release/net10.0"

PHASE0_ROOT="${REPO_ROOT}/tests/LocalExile.Client.Phase0E2E"
PHASE0_SERVER_CSPROJ="${PHASE0_ROOT}/Server/DualMod.Phase0E2E.Server.csproj"
PHASE0_CLIENT_CSPROJ="${PHASE0_ROOT}/Client/DualMod.Phase0E2E.Client.csproj"
PHASE0_SERVER_MOD_DIR="${PHASE0_ROOT}/Server/bin/Release/net10.0"
PHASE0_CLIENT_MOD_DIR="${PHASE0_ROOT}/Client/bin/Release/net10.0"
PHASE0_OVERLAY_SRC="${PHASE0_ROOT}/client"

if [[ "${MODE}" == "import-patch" && ! -f "${PATCHER_CSPROJ}" ]]; then
    echo "ERROR: Patcher project not found: ${PATCHER_CSPROJ}" >&2
    exit 1
fi

HOST_REQUIRED_FILES=(
    "LocalExile.ClientHost.dll"
    "version.dll"
    "hostfxr.dll"
    "coreclr.dll"
    "LocalExile.Client.Host.dll"
    "LocalExile.Client.Host.runtimeconfig.json"
    "addresses/3.29.3d.json"
)
MAXWELL_ASSET_REQUIRED_FILES=(
    "Data/MtxTypes.datc64"
    "Data/MtxTypeGameSpecific.datc64"
    "Data/Pet.datc64"
    "Audio/Mods/Maxwell/stock_market.ogg"
)
SIPHON_ASSET_REQUIRED_FILES=(
    "Data/BaseItemTypes.datc64"
    "Data/ItemVisualIdentity.datc64"
    "Data/CurrencyItems.datc64"
    "Metadata/Items/Currency/Mods/Siphon/SiphonOrbEmpty.it"
    "Metadata/Items/Currency/Mods/Siphon/SiphonOrbCharged.it"
    "Art/2DItems/Currency/Mods/Siphon/SiphonOrbEmpty.dds"
    "Art/2DItems/Currency/Mods/Siphon/SiphonOrbEmpty.dds.header"
    "Art/2DItems/Currency/Mods/Siphon/SiphonOrbCharged.dds"
    "Art/2DItems/Currency/Mods/Siphon/SiphonOrbCharged.dds.header"
)

host_bundle_ready() {
    for req in "${HOST_REQUIRED_FILES[@]}"; do
        [[ -f "${DIST_DIR}/${req}" ]] || return 1
    done
    return 0
}

maxwell_assets_ready() {
    for req in "${MAXWELL_ASSET_REQUIRED_FILES[@]}"; do
        [[ -f "${MAXWELL_ASSETS_DIR}/${req}" ]] || return 1
    done
    return 0
}

maxwell_mods_ready() {
    [[ -f "${MAXWELL_SERVER_MOD_DIR}/maxwell.dll" ]] \
        && [[ -f "${MAXWELL_SERVER_MOD_DIR}/MaxwellLocalExileEntry.cs" ]] \
        && [[ -f "${MAXWELL_CLIENT_MOD_DIR}/maxwell.Client.dll" ]]
}

siphon_assets_ready() {
    for req in "${SIPHON_ASSET_REQUIRED_FILES[@]}"; do
        [[ -f "${SIPHON_ASSETS_DIR}/${req}" ]] || return 1
    done
    return 0
}

siphon_mods_ready() {
    [[ -f "${SIPHON_SERVER_MOD_DIR}/siphon.dll" ]] \
        && [[ -f "${SIPHON_SERVER_MOD_DIR}/SiphonLocalExileEntry.cs" ]] \
        && [[ -f "${SIPHON_CLIENT_MOD_DIR}/siphon.Client.dll" ]]
}

phase0_mods_ready() {
    [[ -f "${PHASE0_SERVER_MOD_DIR}/phase0-e2e.dll" ]] \
        && [[ -f "${PHASE0_SERVER_MOD_DIR}/Phase0LocalExileEntry.cs" ]] \
        && [[ -f "${PHASE0_CLIENT_MOD_DIR}/phase0-e2e.Client.dll" ]] \
        && [[ -f "${PHASE0_OVERLAY_SRC}/Metadata/UI/Mods/Phase0E2E/Phase0Probe.ui" ]]
}

NEED_BUILD_WIN64=0
NEED_BUILD_MAXWELL_ASSETS=0
NEED_BUILD_MAXWELL_MODS=0
NEED_BUILD_SIPHON_ASSETS=0
NEED_BUILD_SIPHON_MODS=0
NEED_BUILD_PHASE0_MODS=0

if [[ "${REBUILD}" -eq 1 ]] || ! host_bundle_ready; then NEED_BUILD_WIN64=1; fi
if [[ "${REBUILD}" -eq 1 ]] || ! maxwell_assets_ready; then NEED_BUILD_MAXWELL_ASSETS=1; fi
if [[ "${REBUILD}" -eq 1 ]] || ! maxwell_mods_ready; then NEED_BUILD_MAXWELL_MODS=1; fi
if [[ "${REBUILD}" -eq 1 ]] || ! siphon_assets_ready; then NEED_BUILD_SIPHON_ASSETS=1; fi
if [[ "${REBUILD}" -eq 1 ]] || ! siphon_mods_ready; then NEED_BUILD_SIPHON_MODS=1; fi
if [[ "${REBUILD}" -eq 1 ]] || ! phase0_mods_ready; then NEED_BUILD_PHASE0_MODS=1; fi

to_wine_path() {
    printf 'Z:%s' "${1//\//\\}"
}

RUN_ID="$(date +%Y%m%d-%H%M%S)-${MODE}-$$"
RUN_LOG_DIR="${LOG_DIR}/${RUN_ID}"
if [[ -z "${SCREENSHOT_DIR}" ]]; then
    SCREENSHOT_DIR="${RUN_LOG_DIR}/screenshots"
fi

# Staging inside LocalExile/data/ so Proton steamrt4 container (pressure-vessel) can always mount it.
STAGE_DIR="${LOCALEXILE_DIR}/data/phase0-proton-stage-${RUN_ID}"
RUNNER_SCRIPT="${STAGE_DIR}/phase0-client-runner.sh"
SHIM_LOG_LINUX="${STAGE_DIR}/nativemods-shim.log"

HOST_DIR_WINE="$(to_wine_path "${STAGE_DIR}/host")"
ADDR_DIR_WINE="$(to_wine_path "${STAGE_DIR}/host/addresses")"
MODS_DIR_WINE="$(to_wine_path "${STAGE_DIR}/host/Mods/Client")"
MAXWELL_OVERLAY_WINE="$(to_wine_path "${STAGE_DIR}/overlay-maxwell")"
SIPHON_OVERLAY_WINE="$(to_wine_path "${STAGE_DIR}/overlay-siphon")"
PHASE0_OVERLAY_WINE="$(to_wine_path "${STAGE_DIR}/overlay-phase0")"
OVERLAY_DIR_WINE="${MAXWELL_OVERLAY_WINE};${SIPHON_OVERLAY_WINE};${PHASE0_OVERLAY_WINE}"
LOG_PATH_WINE="$(to_wine_path "${SHIM_LOG_LINUX}")"

describe_script_build() {
    local need="$1"
    local script="$2"
    local ready_dir="$3"
    local args="$4"
    if [[ "${need}" -eq 0 ]]; then
        echo "READY (${ready_dir})"
    elif [[ -x "${script}" ]]; then
        echo "WOULD BUILD (${script}${args})"
    else
        echo "PENDING SCRIPT (${script} missing or not executable)"
    fi
}

describe_csproj_build() {
    local need="$1"
    local server_proj="$2"
    local client_proj="$3"
    local server_dir="$4"
    local client_dir="$5"
    if [[ "${need}" -eq 0 ]]; then
        echo "READY (${server_dir}, ${client_dir})"
    elif [[ -f "${server_proj}" && -f "${client_proj}" ]]; then
        echo "WOULD BUILD (Server + Client, Release)"
    else
        echo "PENDING PROJECT (${server_proj} / ${client_proj} not found)"
    fi
}

# 4. --dry-run exits 0 with ZERO writes to LocalExile/, STAGE_DIR, or RUN_LOG_DIR
if [[ "${DRY_RUN}" -eq 1 ]]; then
    echo "==> NativeMods Phase 0 E2E Proton Harness [DRY RUN]"
    echo "    Mode             : ${MODE}"
    echo "    Isolated Xvfb    : DISPLAY=:${DISPLAY_NUM} (1280x720x24; >=50 enforced; Wayland/Hyprland stripped)"
    if [[ "${AUDIBLE}" -eq 1 ]]; then
        echo "    Audio sink       : system default (--audible)"
    else
        echo "    Audio sink       : isolated PipeWire module-null-sink (pactl) + 4s ffmpeg volumedetect"
    fi
    echo "    LocalExile dir   : ${LOCALEXILE_DIR}"
    echo "    Client dir       : ${CLIENT_DIR} (verified clean: version.dll & ClientHost.dll absent)"
    echo "    Staging dir      : ${STAGE_DIR} (inside LocalExile/data for steamrt4 visibility)"
    echo "    Shim log (live)  : ${SHIM_LOG_LINUX}"
    echo "    Run log dir      : ${RUN_LOG_DIR}"
    echo "    Screenshot dir   : ${SCREENSHOT_DIR}"
    echo "    Soak seconds     : ${SOAK_SECONDS}"
    echo "    Auto input       : $([[ "${AUTO_INPUT}" -eq 1 ]] && echo 'ENABLED' || echo 'DISABLED (--no-auto-input)')"
    echo "    Save DB handling : $([[ "${KEEP_SAVE}" -eq 1 ]] && echo 'KEEP (--keep-save)' || echo 'SNAPSHOT & RESTORE characters.db* on exit')"
    echo ""
    echo "==> Build readiness:"
    echo "    Win64 extender   : $(describe_script_build "${NEED_BUILD_WIN64}" "${BUILD_WIN64_SCRIPT}" "${DIST_DIR}" "")"
    echo "    Maxwell assets   : $(describe_script_build "${NEED_BUILD_MAXWELL_ASSETS}" "${BUILD_MAXWELL_ASSETS_SCRIPT}" "${MAXWELL_ASSETS_DIR}" " --client ${CLIENT_DIR} --out ${MAXWELL_ASSETS_DIR}")"
    echo "    DualMod.Maxwell  : $(describe_csproj_build "${NEED_BUILD_MAXWELL_MODS}" "${MAXWELL_SERVER_CSPROJ}" "${MAXWELL_CLIENT_CSPROJ}" "${MAXWELL_SERVER_MOD_DIR}" "${MAXWELL_CLIENT_MOD_DIR}")"
    echo "    Siphon assets    : $(describe_script_build "${NEED_BUILD_SIPHON_ASSETS}" "${BUILD_SIPHON_ASSETS_SCRIPT}" "${SIPHON_ASSETS_DIR}" " --client ${CLIENT_DIR} --out ${SIPHON_ASSETS_DIR}")"
    echo "    DualMod.Siphon   : $(describe_csproj_build "${NEED_BUILD_SIPHON_MODS}" "${SIPHON_SERVER_CSPROJ}" "${SIPHON_CLIENT_CSPROJ}" "${SIPHON_SERVER_MOD_DIR}" "${SIPHON_CLIENT_MOD_DIR}")"
    echo "    Phase0E2E        : $(describe_csproj_build "${NEED_BUILD_PHASE0_MODS}" "${PHASE0_SERVER_CSPROJ}" "${PHASE0_CLIENT_CSPROJ}" "${PHASE0_SERVER_MOD_DIR}" "${PHASE0_CLIENT_MOD_DIR}")"
    echo ""
    echo "==> Staging layout plan (no files written during --dry-run):"
    echo "    1. ${DIST_DIR}/. -> ${STAGE_DIR}/host/"
    echo "    2. ${MAXWELL_ASSETS_DIR}/. -> ${STAGE_DIR}/overlay-maxwell/"
    echo "    3. ${SIPHON_ASSETS_DIR}/. -> ${STAGE_DIR}/overlay-siphon/"
    echo "    4. ${PHASE0_OVERLAY_SRC}/. -> ${STAGE_DIR}/overlay-phase0/"
    echo "    5. Client mods -> ${STAGE_DIR}/host/Mods/Client/{maxwell,siphon,phase0-e2e}/"
    echo "    6. Server mods -> ${STAGE_DIR}/server-mods/{maxwell,siphon,phase0-e2e}/"
    if [[ "${MODE}" == "proxy" ]]; then
        echo "    7. Client/ <- LocalExile.ClientHost.dll + version.dll (removed on exit)"
    else
        echo "    7. Client/ <- LocalExile.ClientHost.dll + .nmimp patch on Game.patched.exe (restored on exit)"
    fi
    echo ""
    echo "==> Runner script preview (${RUNNER_SCRIPT}):"
    echo "    unset WAYLAND_DISPLAY"
    echo "    unset HYPRLAND_INSTANCE_SIGNATURE"
    echo "    unset HYPRLAND_CMD"
    echo "    unset XDG_CURRENT_DESKTOP"
    echo "    unset XDG_SESSION_DESKTOP"
    echo "    unset XDG_SESSION_TYPE"
    echo "    unset XDG_BACKEND"
    echo "    unset SDL_VIDEODRIVER"
    echo "    unset GDK_BACKEND"
    echo "    unset QT_QPA_PLATFORM"
    echo "    export DISPLAY=':${DISPLAY_NUM}'"
    echo "    export PROTON_ENABLE_WAYLAND=0"
    echo "    export NATIVEMODS_HOST_DIR='${HOST_DIR_WINE}'"
    echo "    export NATIVEMODS_ADDRESSES_DIR='${ADDR_DIR_WINE}'"
    echo "    export NATIVEMODS_MODS_DIR='${MODS_DIR_WINE}'"
    echo "    export NATIVEMODS_OVERLAY_DIR='${OVERLAY_DIR_WINE}'"
    echo "    export NATIVEMODS_MAXWELL_ASSETS_DIR='${MAXWELL_OVERLAY_WINE}'"
    echo "    export NATIVEMODS_SIPHON_ASSETS_DIR='${SIPHON_OVERLAY_WINE}'"
    echo "    export NATIVEMODS_LOG_PATH='${LOG_PATH_WINE}'"
    if [[ "${TRACE_FILES}" -eq 1 ]]; then
        echo "    export NATIVEMODS_TRACE_FILES=1"
    fi
    if [[ "${AUDIBLE}" -eq 0 ]]; then
        echo "    export PULSE_SINK='nativemods_phase0_<run_id>'"
    fi
    if [[ "${MODE}" == "proxy" ]]; then
        echo '    export WINEDLLOVERRIDES="${WINEDLLOVERRIDES:+${WINEDLLOVERRIDES};}version=n,b"'
    fi
    echo '    exec umu-run "$@" --nologo --disable-assert-box'
    echo ""
    echo "==> Exact launch command:"
    printf "    (cd %q && Game_MODS_FOLDER=%q Game_CLIENT_RUNNER=%q pwsh -NoProfile -File %q -NoBuild -NoExtract)\n" \
        "${LOCALEXILE_DIR}" "${STAGE_DIR}/server-mods" "${RUNNER_SCRIPT}" "${LIVE_PS1}"
    echo ""
    echo "==> Dry run passed: tools, paths, and plans validated; ZERO modifications made to ${LOCALEXILE_DIR}."
    exit 0
fi

# 5. Live run pre-build checks & optional --rebuild
if [[ "${NEED_BUILD_WIN64}" -eq 1 ]]; then
    [[ -x "${BUILD_WIN64_SCRIPT}" ]] || { echo "ERROR: Missing executable ${BUILD_WIN64_SCRIPT}" >&2; exit 1; }
    echo "==> Building Win64 NativeMods extender..."
    "${BUILD_WIN64_SCRIPT}"
fi
if [[ "${NEED_BUILD_MAXWELL_ASSETS}" -eq 1 ]]; then
    [[ -x "${BUILD_MAXWELL_ASSETS_SCRIPT}" ]] || { echo "ERROR: Missing executable ${BUILD_MAXWELL_ASSETS_SCRIPT}" >&2; exit 1; }
    echo "==> Building Maxwell 3.29.3d assets..."
    "${BUILD_MAXWELL_ASSETS_SCRIPT}" --client "${CLIENT_DIR}" --out "${MAXWELL_ASSETS_DIR}"
fi
if [[ "${NEED_BUILD_MAXWELL_MODS}" -eq 1 ]]; then
    [[ -f "${MAXWELL_SERVER_CSPROJ}" && -f "${MAXWELL_CLIENT_CSPROJ}" ]] || { echo "ERROR: Missing DualMod.MaxwellPet csproj" >&2; exit 1; }
    echo "==> Building DualMod.MaxwellPet (Release)..."
    dotnet build "${MAXWELL_SERVER_CSPROJ}" -c Release --nologo -v minimal
    dotnet build "${MAXWELL_CLIENT_CSPROJ}" -c Release --nologo -v minimal
fi
if [[ "${NEED_BUILD_SIPHON_ASSETS}" -eq 1 ]]; then
    [[ -x "${BUILD_SIPHON_ASSETS_SCRIPT}" ]] || { echo "ERROR: Missing executable ${BUILD_SIPHON_ASSETS_SCRIPT}" >&2; exit 1; }
    echo "==> Building Siphon 3.29.3d assets..."
    "${BUILD_SIPHON_ASSETS_SCRIPT}" --client "${CLIENT_DIR}" --out "${SIPHON_ASSETS_DIR}"
fi
if [[ "${NEED_BUILD_SIPHON_MODS}" -eq 1 ]]; then
    [[ -f "${SIPHON_SERVER_CSPROJ}" && -f "${SIPHON_CLIENT_CSPROJ}" ]] || { echo "ERROR: Missing DualMod.SiphonOrb csproj" >&2; exit 1; }
    echo "==> Building DualMod.SiphonOrb (Release)..."
    dotnet build "${SIPHON_SERVER_CSPROJ}" -c Release --nologo -v minimal
    dotnet build "${SIPHON_CLIENT_CSPROJ}" -c Release --nologo -v minimal
fi
if [[ "${NEED_BUILD_PHASE0_MODS}" -eq 1 ]]; then
    [[ -f "${PHASE0_SERVER_CSPROJ}" && -f "${PHASE0_CLIENT_CSPROJ}" ]] || { echo "ERROR: Missing DualMod.Phase0E2E csproj" >&2; exit 1; }
    echo "==> Building DualMod.Phase0E2E (Release)..."
    dotnet build "${PHASE0_SERVER_CSPROJ}" -c Release --nologo -v minimal
    dotnet build "${PHASE0_CLIENT_CSPROJ}" -c Release --nologo -v minimal
fi

host_bundle_ready || { echo "ERROR: Win64 host bundle incomplete in ${DIST_DIR}" >&2; exit 1; }
maxwell_assets_ready || { echo "ERROR: Maxwell assets incomplete in ${MAXWELL_ASSETS_DIR}" >&2; exit 1; }
maxwell_mods_ready || { echo "ERROR: Maxwell Release mods incomplete" >&2; exit 1; }
siphon_assets_ready || { echo "ERROR: Siphon assets incomplete in ${SIPHON_ASSETS_DIR}" >&2; exit 1; }
siphon_mods_ready || { echo "ERROR: Siphon Release mods incomplete" >&2; exit 1; }
phase0_mods_ready || { echo "ERROR: Phase0E2E Release mods incomplete" >&2; exit 1; }

DIST_DIR="$(cd "${DIST_DIR}" && pwd)"
MAXWELL_ASSETS_DIR="$(cd "${MAXWELL_ASSETS_DIR}" && pwd)"
SIPHON_ASSETS_DIR="$(cd "${SIPHON_ASSETS_DIR}" && pwd)"

# 6. Snapshot baseline hashes, Mods/ tree, and characters.db* BEFORE any mutation
mkdir -p "${RUN_LOG_DIR}" "${SCREENSHOT_DIR}"

GAME_EXE_SHA_BEFORE="$(sha256sum "${GAME_EXE}" | awk '{print $1}')"
GAME_PATCHED_SHA_BEFORE="$(sha256sum "${GAME_PATCHED_EXE}" | awk '{print $1}')"
MODS_SNAPSHOT_BEFORE="$(find "${LOCALEXILE_DIR}/Mods" -maxdepth 4 -printf '%P %y\n' 2>/dev/null | sort || true)"

CHAR_DB_BACKUP_DIR=""
if [[ "${KEEP_SAVE}" -eq 0 ]]; then
    CHAR_DB_BACKUP_DIR="${RUN_LOG_DIR}/char-db-backup"
    mkdir -p "${CHAR_DB_BACKUP_DIR}"
    for dbf in \
        "${LOCALEXILE_DIR}/data/characters.db" \
        "${LOCALEXILE_DIR}/data/characters.db-wal" \
        "${LOCALEXILE_DIR}/data/characters.db-shm"; do
        if [[ -f "${dbf}" ]]; then
            cp -a "${dbf}" "${CHAR_DB_BACKUP_DIR}/"
        fi
    done
fi

BACKUP_EXE=""
XVFB_PID=""
NULL_SINK_MODULE_ID=""
NULL_SINK_NAME=""
STAGED_CLIENT_FILES=()
LIVE_STARTED=0
CLEANED_UP=0

check_hyprland_leaks() {
    local label="$1"
    if ! command -v hyprctl >/dev/null 2>&1 || [[ -z "${HYPRLAND_INSTANCE_SIGNATURE:-}" ]]; then
        echo "    [hyprland:${label}] hyprctl unavailable or not in Hyprland"
        return 0
    fi
    local leaked
    leaked="$(hyprctl clients -j 2>/dev/null | python3 -c '
import json, sys
try:
    clients = json.load(sys.stdin)
except Exception:
    sys.exit(0)
hits = []
for c in clients:
    cls = str(c.get("class", ""))
    icls = str(c.get("initialClass", ""))
    title = str(c.get("title", ""))
    ititle = str(c.get("initialTitle", ""))
    cls_blob = f"{cls} {icls}".lower()
    if any(tok in cls_blob for tok in ("game.patched", "pathofexile", "umu-238960", "steam_app_238960")) or title.lower() == "path of exile" or ititle.lower() == "path of exile":
        pid = c.get("pid")
        hits.append(f"{cls!r}/{title!r} (pid={pid})")
if hits:
    print("; ".join(hits))
' || true)"
    if [[ -n "${leaked}" ]]; then
        echo "ERROR: Hyprland window leak detected (${label}): ${leaked}" >&2
        return 1
    fi
    echo "    [hyprland:${label}] OK (0 Path of Exile / Wine windows on Hyprland)"
    return 0
}

capture_screenshot() {
    local name="$1"
    local out_png="${SCREENSHOT_DIR}/${name}.png"
    if ! DISPLAY=":${DISPLAY_NUM}" ffmpeg -y -loglevel error -f x11grab -video_size 1280x720 \
        -i ":${DISPLAY_NUM}.0" -frames:v 1 "${out_png}" 2>/dev/null; then
        DISPLAY=":${DISPLAY_NUM}" import -window root "${out_png}" 2>/dev/null || return 1
    fi
    local sz
    sz="$(wc -c < "${out_png}" 2>/dev/null || echo 0)"
    echo "    [shot] ${out_png} (bytes=${sz})"
}

focus_poe_window() {
    local wid=""
    wid="$(DISPLAY=":${DISPLAY_NUM}" xdotool search --name "Path of Exile" 2>/dev/null | head -n 1 || true)"
    if [[ -n "${wid}" ]]; then
        DISPLAY=":${DISPLAY_NUM}" xdotool windowactivate "${wid}" 2>/dev/null || true
        DISPLAY=":${DISPLAY_NUM}" xdotool windowfocus "${wid}" 2>/dev/null || true
        echo "${wid}"
        return 0
    fi
    return 1
}

send_chat_cmd() {
    local cmd="$1"
    focus_poe_window >/dev/null || true
    DISPLAY=":${DISPLAY_NUM}" xdotool key --delay 80 Return || true
    sleep 0.35
    DISPLAY=":${DISPLAY_NUM}" xdotool type --delay 30 "${cmd}" || true
    sleep 0.25
    DISPLAY=":${DISPLAY_NUM}" xdotool key --delay 80 Return || true
}

on_signal() {
    echo ""
    echo "==> Termination signal received; tearing down cleanly..."
    exit 130
}

cleanup() {
    local exit_code=$?
    if [[ "${CLEANED_UP}" -eq 1 ]]; then
        return
    fi
    CLEANED_UP=1
    trap '' INT TERM
    set +e

    echo "==> Cleaning up Proton client, servers, Xvfb, virtual audio sink, and staged files..."
    if [[ "${LIVE_STARTED}" -eq 1 ]]; then
        if [[ "${KEEP_SERVERS}" -eq 1 ]]; then
            (cd "${LOCALEXILE_DIR}" && pwsh -NoProfile -File "${LIVE_PS1}" -Stop -ClientOnly) >/dev/null 2>&1 || true
        else
            (cd "${LOCALEXILE_DIR}" && pwsh -NoProfile -File "${LIVE_PS1}" -Stop) >/dev/null 2>&1 || true
        fi

        for _ in $(seq 1 20); do
            if ! pgrep -f "${GAME_PATCHED_EXE}" >/dev/null 2>&1; then
                break
            fi
            sleep 0.25
        done
        pkill -TERM -f "${GAME_PATCHED_EXE}" >/dev/null 2>&1 || true
        sleep 0.5
    fi

    if [[ -n "${XVFB_PID}" ]]; then
        kill "${XVFB_PID}" >/dev/null 2>&1 || true
        wait "${XVFB_PID}" >/dev/null 2>&1 || true
    fi

    if [[ -n "${NULL_SINK_MODULE_ID}" ]]; then
        pactl unload-module "${NULL_SINK_MODULE_ID}" >/dev/null 2>&1 || true
    fi

    if [[ -f "${SHIM_LOG_LINUX}" ]]; then
        cp -f "${SHIM_LOG_LINUX}" "${RUN_LOG_DIR}/nativemods-shim.log" 2>/dev/null || true
    fi

    for f in "${STAGED_CLIENT_FILES[@]}"; do
        rm -f "${f}" 2>/dev/null || true
    done
    rm -f "${CLIENT_DIR}/nativemods-shim.log" 2>/dev/null || true

    if [[ -n "${BACKUP_EXE}" && -f "${BACKUP_EXE}" ]]; then
        cp -f "${BACKUP_EXE}" "${GAME_PATCHED_EXE}"
    fi

    if [[ "${KEEP_SAVE}" -eq 0 && -n "${CHAR_DB_BACKUP_DIR}" && -d "${CHAR_DB_BACKUP_DIR}" ]]; then
        rm -f "${LOCALEXILE_DIR}/data/characters.db-wal" "${LOCALEXILE_DIR}/data/characters.db-shm" 2>/dev/null || true
        for dbf in "${CHAR_DB_BACKUP_DIR}"/characters.db*; do
            if [[ -f "${dbf}" ]]; then
                cp -a "${dbf}" "${LOCALEXILE_DIR}/data/" 2>/dev/null || true
            fi
        done
    fi

    rm -rf "${STAGE_DIR}" 2>/dev/null || true

    local sha_game_after sha_patched_after mods_after restore_ok=1
    sha_game_after="$(sha256sum "${GAME_EXE}" | awk '{print $1}')"
    sha_patched_after="$(sha256sum "${GAME_PATCHED_EXE}" | awk '{print $1}')"
    mods_after="$(find "${LOCALEXILE_DIR}/Mods" -maxdepth 4 -printf '%P %y\n' 2>/dev/null | sort || true)"

    if [[ "${sha_game_after}" != "${GAME_EXE_SHA_BEFORE}" ]]; then
        echo "CRITICAL: Game.exe SHA-256 mismatch after cleanup! before=${GAME_EXE_SHA_BEFORE} after=${sha_game_after}" >&2
        restore_ok=0
    fi
    if [[ "${sha_patched_after}" != "${GAME_PATCHED_SHA_BEFORE}" ]]; then
        echo "CRITICAL: Game.patched.exe SHA-256 mismatch after cleanup! before=${GAME_PATCHED_SHA_BEFORE} after=${sha_patched_after}" >&2
        restore_ok=0
    fi
    if [[ -e "${CLIENT_DIR}/version.dll" || -e "${CLIENT_DIR}/LocalExile.ClientHost.dll" ]]; then
        echo "CRITICAL: Staged DLLs still present in ${CLIENT_DIR}!" >&2
        restore_ok=0
    fi
    if [[ "${mods_after}" != "${MODS_SNAPSHOT_BEFORE}" ]]; then
        echo "CRITICAL: ${LOCALEXILE_DIR}/Mods changed during run!" >&2
        restore_ok=0
    fi

    if [[ "${restore_ok}" -eq 1 ]]; then
        echo "==> LocalExile Client/ & Mods/ restored 100% cleanly (SHA-256 verified identical; zero stray files)."
    else
        exit_code=1
    fi
    echo "    Run logs saved under: ${RUN_LOG_DIR}"
    exit "${exit_code}"
}

trap on_signal INT TERM
trap cleanup EXIT

echo "==> Phase 0 E2E Proton Harness (run=${RUN_ID}, mode=${MODE}, display=:${DISPLAY_NUM})"
echo "    LocalExile     : ${LOCALEXILE_DIR}"
echo "    Dist bundle    : ${DIST_DIR}"
echo "    Maxwell assets : ${MAXWELL_ASSETS_DIR}"
echo "    Siphon assets  : ${SIPHON_ASSETS_DIR}"
echo "    Phase0 overlay : ${PHASE0_OVERLAY_SRC}"
echo "    Log dir        : ${RUN_LOG_DIR}"
echo "    Game.exe SHA-256        : ${GAME_EXE_SHA_BEFORE}"
echo "    Game.patched.exe SHA-256: ${GAME_PATCHED_SHA_BEFORE}"

# 7. Pre-launch Hyprland check
check_hyprland_leaks "pre-launch"

# 8. Start isolated Xvfb
echo "==> Starting isolated virtual screen Xvfb :${DISPLAY_NUM} (1280x720x24)..."
Xvfb ":${DISPLAY_NUM}" -screen 0 1280x720x24 +extension GLX +extension RANDR +render -noreset \
    >"${RUN_LOG_DIR}/xvfb.log" 2>&1 &
XVFB_PID=$!

for _ in $(seq 1 40); do
    if [[ -S "/tmp/.X11-unix/X${DISPLAY_NUM}" ]]; then
        break
    fi
    if ! kill -0 "${XVFB_PID}" 2>/dev/null; then
        echo "ERROR: Xvfb exited unexpectedly (see ${RUN_LOG_DIR}/xvfb.log)" >&2
        exit 1
    fi
    sleep 0.1
done
if [[ ! -S "/tmp/.X11-unix/X${DISPLAY_NUM}" ]]; then
    echo "ERROR: Timed out waiting for /tmp/.X11-unix/X${DISPLAY_NUM}" >&2
    exit 1
fi
echo "    Xvfb :${DISPLAY_NUM} ready (pid=${XVFB_PID})"

# 9. PipeWire virtual null sink (unless --audible)
if [[ "${AUDIBLE}" -eq 0 ]]; then
    NULL_SINK_NAME="nativemods_phase0_${RUN_ID//[^a-zA-Z0-9_]/_}"
    if NULL_SINK_MODULE_ID="$(pactl load-module module-null-sink \
        "sink_name=${NULL_SINK_NAME}" \
        "sink_properties=device.description=NativeModsPhase0VirtualSink" 2>/dev/null)"; then
        echo "    Virtual PipeWire null sink ready: ${NULL_SINK_NAME} (module=${NULL_SINK_MODULE_ID})"
    else
        NULL_SINK_NAME=""
        NULL_SINK_MODULE_ID=""
        echo "    [warn] Could not create PipeWire null sink; FMOD will use default sink"
    fi
fi

# 10. Stage host, overlays, client mods, and server mods in LocalExile/data/ (steamrt4 visibility)
mkdir -p "${STAGE_DIR}/host" "${STAGE_DIR}/server-mods"
cp -a "${DIST_DIR}/." "${STAGE_DIR}/host/"

stage_dual_mod() {
    local mod_id="$1"
    local mod_root="$2"
    local assets_dir="$3"
    local client_bin_dir="$4"
    local server_bin_dir="$5"
    local overlay_stage="$6"

    local client_stage="${STAGE_DIR}/host/Mods/Client/${mod_id}"
    local server_stage="${STAGE_DIR}/server-mods/${mod_id}"
    mkdir -p "${overlay_stage}" "${client_stage}" "${server_stage}"

    if [[ -n "${assets_dir}" && -d "${assets_dir}" ]]; then
        cp -a "${assets_dir}/." "${overlay_stage}/"
    fi
    cp -a "${client_bin_dir}/." "${client_stage}/"
    if [[ -f "${mod_root}/mod.json" ]]; then
        cp -f "${mod_root}/mod.json" "${client_stage}/mod.json"
    fi
    if [[ -d "${mod_root}/client" ]]; then
        mkdir -p "${client_stage}/client"
        python3 - "${mod_root}/client" "${client_stage}/client" "${overlay_stage}" <<'PY'
import os, shutil, sys
src_root, mod_dst, ov_dst = sys.argv[1], sys.argv[2], sys.argv[3]
for dirpath, _, filenames in os.walk(src_root):
    rel_dir = os.path.relpath(dirpath, src_root)
    for fn in filenames:
        src = os.path.join(dirpath, fn)
        rel = fn if rel_dir == "." else os.path.join(rel_dir, fn)
        ov_path = os.path.join(ov_dst, rel)
        mod_path = os.path.join(mod_dst, rel)
        os.makedirs(os.path.dirname(mod_path), exist_ok=True)
        os.makedirs(os.path.dirname(ov_path), exist_ok=True)
        if os.path.exists(ov_path):
            shutil.copy2(ov_path, mod_path)
        else:
            shutil.copy2(src, ov_path)
            shutil.copy2(src, mod_path)
PY
    fi

    cp -a "${server_bin_dir}/." "${server_stage}/"
    for meta in mod.json settings.json; do
        if [[ -f "${mod_root}/${meta}" && ! -f "${server_stage}/${meta}" ]]; then
            cp -f "${mod_root}/${meta}" "${server_stage}/${meta}"
        fi
    done
}

stage_dual_mod "maxwell" "${MAXWELL_ROOT}" "${MAXWELL_ASSETS_DIR}" "${MAXWELL_CLIENT_MOD_DIR}" "${MAXWELL_SERVER_MOD_DIR}" "${STAGE_DIR}/overlay-maxwell"
stage_dual_mod "siphon" "${SIPHON_ROOT}" "${SIPHON_ASSETS_DIR}" "${SIPHON_CLIENT_MOD_DIR}" "${SIPHON_SERVER_MOD_DIR}" "${STAGE_DIR}/overlay-siphon"
stage_dual_mod "phase0-e2e" "${PHASE0_ROOT}" "" "${PHASE0_CLIENT_MOD_DIR}" "${PHASE0_SERVER_MOD_DIR}" "${STAGE_DIR}/overlay-phase0"

: > "${SHIM_LOG_LINUX}"

cp "${STAGE_DIR}/host/LocalExile.ClientHost.dll" "${CLIENT_DIR}/LocalExile.ClientHost.dll"
STAGED_CLIENT_FILES+=("${CLIENT_DIR}/LocalExile.ClientHost.dll")

if [[ "${MODE}" == "proxy" ]]; then
    cp "${STAGE_DIR}/host/version.dll" "${CLIENT_DIR}/version.dll"
    STAGED_CLIENT_FILES+=("${CLIENT_DIR}/version.dll")
else
    BACKUP_EXE="${STAGE_DIR}/Game.patched.exe.backup"
    cp -a "${GAME_PATCHED_EXE}" "${BACKUP_EXE}"
    echo "==> Applying PE32+ .nmimp import patch to Game.patched.exe..."
    dotnet run --project "${PATCHER_CSPROJ}" -c Release -- patch "${GAME_PATCHED_EXE}" --allow-synthetic --no-backup >"${RUN_LOG_DIR}/patcher.log" 2>&1
    dotnet run --project "${PATCHER_CSPROJ}" -c Release -- verify "${GAME_PATCHED_EXE}" --allow-synthetic >>"${RUN_LOG_DIR}/patcher.log" 2>&1
fi

# 11. Generate isolated Game_CLIENT_RUNNER script
{
    printf '%s\n' '#!/usr/bin/env bash'
    printf '# Auto-generated isolated Xvfb Proton runner for Phase 0 E2E\n'
    printf '%s\n' 'set -euo pipefail'
    printf '\n'
    printf '%s\n' 'unset WAYLAND_DISPLAY'
    printf '%s\n' 'unset HYPRLAND_INSTANCE_SIGNATURE'
    printf '%s\n' 'unset HYPRLAND_CMD'
    printf '%s\n' 'unset XDG_CURRENT_DESKTOP'
    printf '%s\n' 'unset XDG_SESSION_DESKTOP'
    printf '%s\n' 'unset XDG_SESSION_TYPE'
    printf '%s\n' 'unset XDG_BACKEND'
    printf '%s\n' 'unset SDL_VIDEODRIVER'
    printf '%s\n' 'unset GDK_BACKEND'
    printf '%s\n' 'unset QT_QPA_PLATFORM'
    printf '\n'
    printf 'export DISPLAY=%q\n' ":${DISPLAY_NUM}"
    printf 'export PROTON_ENABLE_WAYLAND=0\n'
    printf 'export NATIVEMODS_HOST_DIR=%q\n' "${HOST_DIR_WINE}"
    printf 'export NATIVEMODS_ADDRESSES_DIR=%q\n' "${ADDR_DIR_WINE}"
    printf 'export NATIVEMODS_MODS_DIR=%q\n' "${MODS_DIR_WINE}"
    printf 'export NATIVEMODS_OVERLAY_DIR=%q\n' "${OVERLAY_DIR_WINE}"
    printf 'export NATIVEMODS_MAXWELL_ASSETS_DIR=%q\n' "${MAXWELL_OVERLAY_WINE}"
    printf 'export NATIVEMODS_SIPHON_ASSETS_DIR=%q\n' "${SIPHON_OVERLAY_WINE}"
    printf 'export NATIVEMODS_LOG_PATH=%q\n' "${LOG_PATH_WINE}"
    if [[ "${TRACE_FILES}" -eq 1 ]]; then
        printf 'export NATIVEMODS_TRACE_FILES=1\n'
    fi
    if [[ -n "${NULL_SINK_NAME}" ]]; then
        printf 'export PULSE_SINK=%q\n' "${NULL_SINK_NAME}"
    fi
    if [[ "${MODE}" == "proxy" ]]; then
        printf '%s\n' 'export WINEDLLOVERRIDES="${WINEDLLOVERRIDES:+${WINEDLLOVERRIDES};}version=n,b"'
    fi
    printf '%s\n' 'exec umu-run "$@" --nologo --disable-assert-box'
} > "${RUNNER_SCRIPT}"
chmod +x "${RUNNER_SCRIPT}"

# 12. Launch strictly via scripts/dev/live.ps1 -NoBuild -NoExtract
CLIENT_LOG_FILE="${CLIENT_DIR}/logs/Client.txt"
CLIENT_LOG_LINES_BEFORE=0
if [[ -f "${CLIENT_LOG_FILE}" ]]; then
    CLIENT_LOG_LINES_BEFORE="$(wc -l < "${CLIENT_LOG_FILE}")"
fi

echo "==> Launching LocalExile Gateway + 3.29.3d Proton client via scripts/dev/live.ps1 ..."
LIVE_STARTED=1
(
    cd "${LOCALEXILE_DIR}"
    Game_MODS_FOLDER="${STAGE_DIR}/server-mods" \
    Game_CLIENT_RUNNER="${RUNNER_SCRIPT}" \
        pwsh -NoProfile -File "${LIVE_PS1}" -NoBuild -NoExtract
) 2>&1 | tee "${RUN_LOG_DIR}/live-ps1.log"

CLIENT_PID=""
for _ in $(seq 1 15); do
    if [[ -f "${LOCALEXILE_DIR}/data/clients/default.json" ]]; then
        CLIENT_PID="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["pid"])' "${LOCALEXILE_DIR}/data/clients/default.json" 2>/dev/null || true)"
    fi
    if [[ -n "${CLIENT_PID}" && -d "/proc/${CLIENT_PID}" ]]; then
        break
    fi
    CLIENT_PID="$(pgrep -n -f "${GAME_PATCHED_EXE}" || true)"
    if [[ -n "${CLIENT_PID}" ]]; then
        break
    fi
    sleep 1
done

if [[ -z "${CLIENT_PID}" ]]; then
    echo "ERROR: Could not locate running Game.patched.exe PID after live.ps1 returned!" >&2
    exit 1
fi

# 13. Boot wait & Hyprland leak checks
CRASHED=0
READY_FOR_LOGIN=0
for sec in $(seq 1 60); do
    if ! kill -0 "${CLIENT_PID}" 2>/dev/null; then
        echo "ERROR: Client PID ${CLIENT_PID} exited prematurely during boot at t=${sec}s!" >&2
        CRASHED=1
        break
    fi
    if (( sec == 5 || sec == 20 )); then
        check_hyprland_leaks "boot-t${sec}s"
    fi
    if grep -q "First frame hook hit on game thread" "${SHIM_LOG_LINUX}" 2>/dev/null \
       && grep -q "NativeEntryPoint.Initialize" "${SHIM_LOG_LINUX}" 2>/dev/null; then
        READY_FOR_LOGIN=1
        echo "    [t=${sec}s] CoreCLR + PassA frame hook live! Settling 5s..."
        sleep 5
        break
    fi
    sleep 1
done

check_hyprland_leaks "post-boot"

if [[ "${CRASHED}" -eq 0 ]]; then
    focus_poe_window >/dev/null || true
    capture_screenshot "01-login-screen" || true
fi

# 14. Automated login -> world entry -> Phase0E2E + MaxwellPet + SiphonOrb live verification
AUDIO_STATS="not-captured"
if [[ "${CRASHED}" -eq 0 && "${AUTO_INPUT}" -eq 1 && "${READY_FOR_LOGIN}" -eq 1 ]]; then
    GATEWAY_LOG="${LOCALEXILE_DIR}/logs/live-gateway.log"
    AUTH_LOG="${LOCALEXILE_DIR}/logs/live-auth.log"

    echo "==> [Phase A] Submitting saved login on Xvfb :${DISPLAY_NUM}..."
    focus_poe_window >/dev/null || true
    DISPLAY=":${DISPLAY_NUM}" xdotool key --delay 50 Return || true
    sleep 2
    for sec in $(seq 1 20); do
        if ! kill -0 "${CLIENT_PID}" 2>/dev/null; then CRASHED=1; break; fi
        if grep -qiE "login|character list|0x0015|authenticated|session" "${AUTH_LOG}" 2>/dev/null; then
            echo "    [login t=${sec}s] Auth activity detected"
            sleep 3
            break
        fi
        if (( sec == 4 )); then
            DISPLAY=":${DISPLAY_NUM}" xdotool mousemove 640 535 click 1 || true
        fi
        sleep 1
    done

    if [[ "${CRASHED}" -eq 0 ]]; then
        capture_screenshot "02-character-select" || true

        echo "==> [Phase B] Entering world with selected character..."
        focus_poe_window >/dev/null || true
        DISPLAY=":${DISPLAY_NUM}" xdotool key --delay 50 Return || true
        sleep 2

        for sec in $(seq 1 90); do
            if ! kill -0 "${CLIENT_PID}" 2>/dev/null; then CRASHED=1; break; fi
            if grep -qiE "You have joined global chat|Area generated at Seed|You have entered" "${GATEWAY_LOG}" 2>/dev/null \
               || tail -n +"$((CLIENT_LOG_LINES_BEFORE + 1))" "${CLIENT_LOG_FILE}" 2>/dev/null | grep -qiE "You have joined global chat|Area generated at Seed|You have entered"; then
                echo "    [world t=${sec}s] Character entered town"
                sleep 4
                break
            fi
            if (( sec == 4 )) && ! grep -qiE "CONNECT" "${GATEWAY_LOG}" 2>/dev/null; then
                DISPLAY=":${DISPLAY_NUM}" xdotool mousemove 1190 623 click 1 || true
            fi
            sleep 1
        done

        if [[ "${CRASHED}" -eq 0 ]]; then
            check_hyprland_leaks "in-game"
            capture_screenshot "03-in-game-entered" || true

            echo "==> [Phase C] Driving Phase0E2E probe suite (/phase0 run, close, raw, item, levelup, chat)..."
            send_chat_cmd "/phase0 run"
            sleep 4
            capture_screenshot "04-phase0-probe-ui" || true

            send_chat_cmd "/phase0 close"
            sleep 1
            send_chat_cmd "/phase0 raw"
            sleep 2
            send_chat_cmd "/phase0 item"
            sleep 2
            send_chat_cmd "/phase0 levelup"
            sleep 2
            send_chat_cmd "/phase0 chat"
            sleep 2
            capture_screenshot "05-phase0-probes-done" || true

            echo "==> [Phase D] Driving Cosmetics (K) + MaxwellPet (/maxwell) + SiphonOrb (/siphon give)..."
            focus_poe_window >/dev/null || true
            DISPLAY=":${DISPLAY_NUM}" xdotool key --delay 80 k || true
            sleep 3
            capture_screenshot "06a-cosmetics-panel" || true
            DISPLAY=":${DISPLAY_NUM}" xdotool mousemove 920 385 click 1 || true
            sleep 12
            DISPLAY=":${DISPLAY_NUM}" xdotool mousemove 873 408 mousedown 1 sleep 0.2 mousemove 873 512 sleep 0.2 mouseup 1 || true
            sleep 0.8
            DISPLAY=":${DISPLAY_NUM}" xdotool mousemove 825 560 click 1 || true
            sleep 3
            capture_screenshot "06b-maxwell-equipped" || true
            DISPLAY=":${DISPLAY_NUM}" xdotool key --delay 80 Escape || true
            sleep 0.8

            send_chat_cmd "/maxwell"
            sleep 2
            capture_screenshot "07-maxwell-panel" || true
            DISPLAY=":${DISPLAY_NUM}" xdotool mousemove 640 304 click 1 || true
            sleep 0.6
            DISPLAY=":${DISPLAY_NUM}" xdotool mousemove 814 304 click 1 || true
            sleep 1

            send_chat_cmd "/siphon give 2"
            sleep 2
            focus_poe_window >/dev/null || true
            DISPLAY=":${DISPLAY_NUM}" xdotool key --delay 80 i || true
            sleep 1.5
            capture_screenshot "08-siphon-inventory" || true
            DISPLAY=":${DISPLAY_NUM}" xdotool key --delay 80 Escape || true
            sleep 0.8

            send_chat_cmd "/phase0 finalize"
            sleep 2

            if [[ -n "${NULL_SINK_NAME}" && "${CRASHED}" -eq 0 ]]; then
                AUDIO_WAV="${RUN_LOG_DIR}/phase0-audio-capture.wav"
                echo "==> [Phase E] Recording 4s audio from ${NULL_SINK_NAME}.monitor..."
                if ffmpeg -y -loglevel error -f pulse -i "${NULL_SINK_NAME}.monitor" -t 4 "${AUDIO_WAV}" 2>/dev/null; then
                    AUDIO_STATS="$(ffmpeg -i "${AUDIO_WAV}" -af volumedetect -f null - 2>&1 | grep -E 'mean_volume|max_volume' | tr '\n' ' ' || echo "analyzed-empty")"
                    echo "    [audio] ${AUDIO_STATS}"
                else
                    AUDIO_STATS="capture-failed"
                fi
            fi
        fi
    fi
fi

# 15. Final soak
if [[ "${CRASHED}" -eq 0 && "${SOAK_SECONDS}" -gt 0 ]]; then
    echo "==> Running final ${SOAK_SECONDS}s soak..."
    for sec in $(seq 1 "${SOAK_SECONDS}"); do
        if ! kill -0 "${CLIENT_PID}" 2>/dev/null; then
            echo "ERROR: Client PID ${CLIENT_PID} exited during soak at t=${sec}s!" >&2
            CRASHED=1
            break
        fi
        sleep 1
    done
fi

check_hyprland_leaks "pre-shutdown"

# 16. Collect logs & evaluate assertions
if [[ -f "${CLIENT_LOG_FILE}" ]]; then
    tail -n +"$((CLIENT_LOG_LINES_BEFORE + 1))" "${CLIENT_LOG_FILE}" > "${RUN_LOG_DIR}/Client-run.txt" 2>/dev/null || true
fi
for srv in live-client.log live-client.err live-gateway.log live-gateway.err live-auth.log live-auth.err live-patch.log live-patch.err; do
    cp -f "${LOCALEXILE_DIR}/logs/${srv}" "${RUN_LOG_DIR}/${srv}" 2>/dev/null || true
done
cp -f "${SHIM_LOG_LINUX}" "${RUN_LOG_DIR}/nativemods-shim.log" 2>/dev/null || true

PASS=1
check_log() {
    local label="$1"
    local pattern="$2"
    local file="${3:-${RUN_LOG_DIR}/nativemods-shim.log}"
    if grep -qiE "${pattern}" "${file}" 2>/dev/null; then
        echo "  [PASS] ${label}"
    else
        echo "  [FAIL] ${label} (missing: ${pattern})" >&2
        PASS=0
    fi
}

echo ""
echo "==================== PHASE 0 E2E PROTON SUMMARY (${MODE}) ===================="
echo "Screenshots : ${SCREENSHOT_DIR}"
echo "Audio stats : ${AUDIO_STATS}"

check_log "NativeEntryPoint.Initialize returned 0" "NativeEntryPoint\.Initialize\(nm_abi_table_t\*\) returned 0"
check_log "First frame hook hit on game thread" "First frame hook hit on game thread"
check_log "Live FileSystem hooks armed" "\[OVERLAY\] Armed live FileSystem hooks"
check_log "Live network channel hooks armed" "\[NET\] Armed live channel hooks"
check_log "Loaded client mod 'maxwell'" "Loaded client mod 'maxwell'"
check_log "Loaded client mod 'siphon'" "Loaded client mod 'siphon'"
check_log "Loaded client mod 'phase0-e2e'" "Loaded client mod 'phase0-e2e'"

if [[ "${AUTO_INPUT}" -eq 1 ]]; then
    for num in $(seq -w 1 16); do
        eid="E2E-${num}"
        if grep -qE "\[PHASE0-E2E\] ASSERT ${eid} FAIL" "${RUN_LOG_DIR}/nativemods-shim.log" "${RUN_LOG_DIR}/live-gateway.log" 2>/dev/null; then
            echo "  [FAIL] ${eid} reported FAIL" >&2
            PASS=0
        elif grep -qE "\[PHASE0-E2E\] ASSERT ${eid} PASS" "${RUN_LOG_DIR}/nativemods-shim.log" "${RUN_LOG_DIR}/live-gateway.log" 2>/dev/null; then
            echo "  [PASS] ${eid}"
        else
            echo "  [FAIL] ${eid} marker missing" >&2
            PASS=0
        fi
    done

    if [[ -n "${NULL_SINK_NAME}" ]]; then
        AUDIO_MAX_DB="$(python3 - "${AUDIO_STATS}" <<'PY'
import re, sys
m = re.search(r'max_volume:\s*([-+]?\d+(?:\.\d+)?|-inf)\s*dB', sys.argv[1])
print(m.group(1) if m else "")
PY
)"
        if [[ -n "${AUDIO_MAX_DB}" && "${AUDIO_MAX_DB}" != "-inf" ]] \
           && python3 -c 'import sys; sys.exit(0 if float(sys.argv[1]) > -60.0 else 1)' "${AUDIO_MAX_DB}"; then
            echo "  [PASS] Non-silent FMOD PCM capture (max_volume=${AUDIO_MAX_DB} dB > -60 dB; ${AUDIO_STATS})"
        else
            echo "  [FAIL] Silent or missing FMOD PCM capture (expected max_volume > -60 dB; ${AUDIO_STATS})" >&2
            PASS=0
        fi
    fi
fi

if grep -qiE "DeadlockDetector|STATUS_ACCESS_VIOLATION|Unhandled exception|Fatal error" \
    "${RUN_LOG_DIR}/Client-run.txt" "${RUN_LOG_DIR}/nativemods-shim.log" \
    "${RUN_LOG_DIR}/live-gateway.log" "${RUN_LOG_DIR}/live-gateway.err" \
    "${RUN_LOG_DIR}/live-auth.log" "${RUN_LOG_DIR}/live-auth.err" 2>/dev/null; then
    echo "  [FAIL] Crash, access violation, unhandled exception, or deadlock detected in logs" >&2
    PASS=0
fi

for err_file in live-gateway.err live-auth.err; do
    if [[ -s "${RUN_LOG_DIR}/${err_file}" ]]; then
        echo "  [FAIL] Non-empty server error log ${err_file}:" >&2
        head -n 10 "${RUN_LOG_DIR}/${err_file}" >&2 || true
        PASS=0
    fi
done

if [[ "${CRASHED}" -ne 0 ]]; then
    echo "  [FAIL] Client crashed or exited prematurely" >&2
    PASS=0
else
    echo "  [PASS] Client survived full run without crash"
fi

echo "==> Run logs saved under: ${RUN_LOG_DIR}"
if [[ "${PASS}" -ne 1 ]]; then
    echo "ERROR: One or more Phase 0 E2E Proton checks failed." >&2
    exit 1
fi
echo "==> PHASE 0 E2E PROTON HARNESS COMPLETED!"
