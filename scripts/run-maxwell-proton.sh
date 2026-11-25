#!/usr/bin/env bash
# Live Proton Integration & Visual Capture Harness for DualMod.MaxwellPet on Path of Exile 3.29.3d.
#
# Safety guarantees (NON-NEGOTIABLE):
#   1. Strictly isolated virtual screen via Xvfb (unsets WAYLAND_DISPLAY, HYPRLAND_INSTANCE_SIGNATURE,
#      XDG_CURRENT_DESKTOP, XDG_SESSION_TYPE, PROTON_ENABLE_WAYLAND; verifies 0 Hyprland window leaks).
#   2. Isolated audio sink via temporary PipeWire/PulseAudio module-null-sink by default (--audible opts into default sink),
#      allowing FMOD 2.03 to open a real 48kHz stereo stream for stock_market.ogg and capture verifiable PCM output
#      without disturbing the user's speakers/headphones.
#   3. Always launches the client ONLY through LocalExile's scripts/dev/live.ps1.
#   4. Never leaves /home/nam/Git/LocalExile/ modified: stages server mods under a temporary directory via
#      Game_MODS_FOLDER (never touching LocalExile/Mods/), records baseline SHA-256 of Client/Game.exe and
#      Client/Game.patched.exe, cleans up all staged files/processes/sinks in trap EXIT INT TERM, and verifies
#      exact SHA-256 and Mods/ restoration before exiting.
#
# Usage:
#   ./scripts/run-maxwell-proton.sh [options]
#
# Options:
#   --dist-dir <path>         Path to built win-x64 bundle (default: <repo>/dist/win-x64)
#   --assets-dir <path>       Path to built maxwell-assets overlay (default: <repo>/dist/maxwell-assets)
#   --server-mod-dir <path>   Path to built Maxwell server mod dir (auto-detected if omitted)
#   --client-mod-dir <path>   Path to built Maxwell client mod dir (auto-detected if omitted)
#   --localexile-dir <path>   Path to LocalExile checkout (default: /home/nam/Git/LocalExile)
#   --mode <proxy|import-patch>  Injection mode (default: proxy)
#   --display <N>             Preferred Xvfb display number >= 50 (default: 96; auto-increments if busy)
#   --log-dir <path>          Directory to store run logs (default: <repo>/build/maxwell-proton-logs)
#   --screenshot-dir <path>   Directory to store virtual-screen PNG screenshots (default: <run-log-dir>/screenshots)
#   --trigger-cmd <cmd>       In-game chat command to summon Maxwell (default: /maxwell)
#   --spin-frames <N>         Number of consecutive spin/hover screenshots to capture (default: 5)
#   --spin-interval <sec>     Seconds between spin screenshots (default: 0.7)
#   --soak-seconds <N>        Extra soak seconds after capture before shutdown (default: 10)
#   --audible                 Route audio to user's default audio sink instead of isolated PipeWire null sink
#   --trace-files             Enable NATIVEMODS_TRACE_FILES=1 diagnostic tracing
#   --no-auto-input           Skip xdotool login/command automation (boot + soak + screenshot only)
#   --keep-servers            Do not stop LocalExile servers on exit (still stops client & restores Client/)
#   -h, --help                Show help

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

DIST_DIR="${REPO_ROOT}/dist/win-x64"
ASSETS_DIR="${REPO_ROOT}/dist/maxwell-assets"
SERVER_MOD_DIR=""
CLIENT_MOD_DIR=""
LOCALEXILE_DIR="/home/nam/Git/LocalExile"
MODE="proxy"
DISPLAY_NUM=96
LOG_DIR="${REPO_ROOT}/build/maxwell-proton-logs"
SCREENSHOT_DIR=""
TRIGGER_CMD="/maxwell"
SPIN_FRAMES=5
SPIN_INTERVAL="0.7"
SOAK_SECONDS=10
AUDIBLE=0
TRACE_FILES=0
AUTO_INPUT=1
KEEP_SERVERS=0
SKIP_ASSET_CHECK=0

usage() {
    sed -n '2,38p' "$0" | sed 's/^# \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --dist-dir)       DIST_DIR="$2"; shift 2 ;;
        --assets-dir)     ASSETS_DIR="$2"; shift 2 ;;
        --server-mod-dir) SERVER_MOD_DIR="$2"; shift 2 ;;
        --client-mod-dir) CLIENT_MOD_DIR="$2"; shift 2 ;;
        --localexile-dir) LOCALEXILE_DIR="$2"; shift 2 ;;
        --mode)           MODE="$2"; shift 2 ;;
        --display)        DISPLAY_NUM="$2"; shift 2 ;;
        --log-dir)        LOG_DIR="$2"; shift 2 ;;
        --screenshot-dir) SCREENSHOT_DIR="$2"; shift 2 ;;
        --trigger-cmd)    TRIGGER_CMD="$2"; shift 2 ;;
        --spin-frames)    SPIN_FRAMES="$2"; shift 2 ;;
        --spin-interval)  SPIN_INTERVAL="$2"; shift 2 ;;
        --soak-seconds)   SOAK_SECONDS="$2"; shift 2 ;;
        --audible)        AUDIBLE=1; shift ;;
        --trace-files)    TRACE_FILES=1; shift ;;
        --no-auto-input)  AUTO_INPUT=0; shift ;;
        --skip-asset-check) SKIP_ASSET_CHECK=1; shift ;;
        --keep-servers)   KEEP_SERVERS=1; shift ;;
        -h|--help)        usage; exit 0 ;;
        *)                echo "ERROR: Unknown option: $1" >&2; usage; exit 2 ;;
    esac
done

if [[ "${MODE}" != "proxy" && "${MODE}" != "import-patch" ]]; then
    echo "ERROR: --mode must be 'proxy' or 'import-patch' (got '${MODE}')" >&2
    exit 2
fi

CLIENT_DIR="${LOCALEXILE_DIR}/Client"
GAME_EXE="${CLIENT_DIR}/Game.exe"
GAME_PATCHED_EXE="${CLIENT_DIR}/Game.patched.exe"
LIVE_PS1="${LOCALEXILE_DIR}/scripts/dev/live.ps1"

for cmd in pwsh umu-run Xvfb xdotool ffmpeg import sha256sum python3; do
    if ! command -v "${cmd}" >/dev/null 2>&1; then
        echo "ERROR: Required command not found on PATH: ${cmd}" >&2
        exit 1
    fi
done

if [[ ! -f "${LIVE_PS1}" || ! -f "${GAME_EXE}" || ! -f "${GAME_PATCHED_EXE}" ]]; then
    echo "ERROR: LocalExile checkout or Client binaries missing under ${LOCALEXILE_DIR}" >&2
    exit 1
fi

if [[ ! -d "${DIST_DIR}" ]]; then
    if [[ -x "${REPO_ROOT}/scripts/build-win64.sh" ]]; then
        echo "==> dist/win-x64 not found; building via scripts/build-win64.sh ..."
        "${REPO_ROOT}/scripts/build-win64.sh"
    else
        echo "ERROR: dist directory not found: ${DIST_DIR} (pass --dist-dir)" >&2
        exit 1
    fi
fi

if [[ ! -d "${ASSETS_DIR}" ]]; then
    if [[ -x "${REPO_ROOT}/scripts/build-maxwell-assets.sh" ]]; then
        echo "==> dist/maxwell-assets not found; building via scripts/build-maxwell-assets.sh ..."
        "${REPO_ROOT}/scripts/build-maxwell-assets.sh"
    else
        echo "ERROR: Maxwell assets directory not found: ${ASSETS_DIR} (pass --assets-dir)" >&2
        exit 1
    fi
fi

DIST_DIR="$(cd "${DIST_DIR}" && pwd)"
ASSETS_DIR="$(cd "${ASSETS_DIR}" && pwd)"

for req in LocalExile.ClientHost.dll version.dll hostfxr.dll coreclr.dll LocalExile.Client.Host.dll LocalExile.Client.Host.runtimeconfig.json addresses/3.29.3d.json; do
    if [[ ! -f "${DIST_DIR}/${req}" ]]; then
        echo "ERROR: Missing required host bundle artifact: ${DIST_DIR}/${req}" >&2
        exit 1
    fi
done

if [[ "${SKIP_ASSET_CHECK}" -eq 0 ]]; then
    for req in Data/MtxTypes.datc64 Data/MtxTypeGameSpecific.datc64 Data/Pet.datc64 Audio/Mods/Maxwell/stock_market.ogg; do
        if [[ ! -f "${ASSETS_DIR}/${req}" ]]; then
            echo "ERROR: Missing required Maxwell asset artifact: ${ASSETS_DIR}/${req}" >&2
            exit 1
        fi
    done
fi

# Auto-detect built DualMod.MaxwellPet server/client mod directories if not explicitly provided
if [[ -z "${SERVER_MOD_DIR}" ]]; then
    for cand in \
        "${REPO_ROOT}/examples/DualMod.MaxwellPet/Server/bin/Release/net10.0" \
        "${REPO_ROOT}/examples/DualMod.MaxwellPet/Server/bin/Debug/net10.0" \
        "${REPO_ROOT}/dist/maxwell-mod/server"; do
        if [[ -d "${cand}" ]] && ls "${cand}"/*.dll >/dev/null 2>&1; then
            SERVER_MOD_DIR="${cand}"
            break
        fi
    done
fi

if [[ -z "${CLIENT_MOD_DIR}" ]]; then
    for cand in \
        "${DIST_DIR}/Mods/Client/maxwell" \
        "${REPO_ROOT}/examples/DualMod.MaxwellPet/Client/bin/Release/net10.0" \
        "${REPO_ROOT}/examples/DualMod.MaxwellPet/Client/bin/Debug/net10.0" \
        "${REPO_ROOT}/dist/maxwell-mod/client"; do
        if [[ -d "${cand}" ]] && ls "${cand}"/*.dll >/dev/null 2>&1; then
            CLIENT_MOD_DIR="${cand}"
            break
        fi
    done
fi

# Refuse to overwrite pre-existing proxy/host files in Client/
for check_file in "${CLIENT_DIR}/version.dll" "${CLIENT_DIR}/LocalExile.ClientHost.dll"; do
    if [[ -e "${check_file}" ]]; then
        echo "ERROR: Refusing to start: ${check_file} already exists in Client/" >&2
        exit 1
    fi
done

mkdir -p "${LOG_DIR}"
RUN_ID="$(date +%Y%m%d-%H%M%S)-${MODE}-$$"
RUN_LOG_DIR="${LOG_DIR}/${RUN_ID}"
mkdir -p "${RUN_LOG_DIR}"

if [[ -z "${SCREENSHOT_DIR}" ]]; then
    SCREENSHOT_DIR="${RUN_LOG_DIR}/screenshots"
fi
mkdir -p "${SCREENSHOT_DIR}"

# Staging directory inside LocalExile/data so Proton's steamrt4 container (pressure-vessel) can always mount/see it
# (steamrt4 does NOT expose host /tmp). Cleaned up unconditionally on EXIT.
STAGE_DIR="${LOCALEXILE_DIR}/data/maxwell-proton-stage-${RUN_ID}"
BACKUP_EXE=""
XVFB_PID=""
NULL_SINK_MODULE_ID=""
NULL_SINK_NAME=""
STAGED_CLIENT_FILES=()
CLEANED_UP=0

GAME_EXE_SHA_BEFORE="$(sha256sum "${GAME_EXE}" | awk '{print $1}')"
GAME_PATCHED_SHA_BEFORE="$(sha256sum "${GAME_PATCHED_EXE}" | awk '{print $1}')"
MODS_SNAPSHOT_BEFORE="$(find "${LOCALEXILE_DIR}/Mods" -maxdepth 4 -printf '%P %y\n' 2>/dev/null | sort || true)"

# Snapshot LocalExile character DB before run so equipping Maxwell via Cosmetics (K) never mutates user save state
CHAR_DB_BACKUP_DIR="${RUN_LOG_DIR}/char-db-backup"
mkdir -p "${CHAR_DB_BACKUP_DIR}"
for dbf in "${LOCALEXILE_DIR}/data/characters.db" "${LOCALEXILE_DIR}/data/characters.db-wal" "${LOCALEXILE_DIR}/data/characters.db-shm"; do
    if [[ -f "${dbf}" ]]; then
        cp -a "${dbf}" "${CHAR_DB_BACKUP_DIR}/"
    fi
done

echo "==> Maxwell the Cat Live Proton Harness (run=${RUN_ID}, mode=${MODE})"
echo "    LocalExile     : ${LOCALEXILE_DIR}"
echo "    Dist bundle    : ${DIST_DIR}"
echo "    Maxwell assets : ${ASSETS_DIR}"
echo "    Server mod dir : ${SERVER_MOD_DIR:-<none>}"
echo "    Client mod dir : ${CLIENT_MOD_DIR:-<from dist>}"
echo "    Log dir        : ${RUN_LOG_DIR}"
echo "    Screenshot dir : ${SCREENSHOT_DIR}"
echo "    Game.exe SHA-256        : ${GAME_EXE_SHA_BEFORE}"
echo "    Game.patched.exe SHA-256: ${GAME_PATCHED_SHA_BEFORE}"

to_wine_path() {
    printf 'Z:%s' "${1//\//\\}"
}

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
    echo "    [hyprland:${label}] OK (0 Path of Exile / Wine windows on user Hyprland monitors)"
    return 0
}

capture_screenshot() {
    local name="$1"
    local out_png="${SCREENSHOT_DIR}/${name}.png"
    # Prefer ffmpeg x11grab for full 24-bit RGB PNG; fall back to ImageMagick import
    if ! DISPLAY=":${DISPLAY_NUM}" ffmpeg -y -loglevel error -f x11grab -video_size 1280x720 \
        -i ":${DISPLAY_NUM}.0" -frames:v 1 "${out_png}" 2>/dev/null; then
        DISPLAY=":${DISPLAY_NUM}" import -window root "${out_png}" 2>/dev/null || return 1
    fi
    local stats
    stats="$(python3 - "${out_png}" <<'PY' 2>/dev/null || echo "size=?"
import os, sys, zlib, struct
p = sys.argv[1]
sz = os.path.getsize(p)
print(f"bytes={sz}")
PY
)"
    echo "    [shot] ${out_png} (${stats})"
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

cleanup() {
    if [[ "${CLEANED_UP}" -eq 1 ]]; then
        return
    fi
    CLEANED_UP=1
    local exit_code=$?
    set +e

    echo "==> Cleaning up Proton client, servers, Xvfb, virtual audio sink, and staged files..."
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

    if [[ -n "${XVFB_PID}" ]]; then
        kill "${XVFB_PID}" >/dev/null 2>&1 || true
        wait "${XVFB_PID}" >/dev/null 2>&1 || true
    fi

    if [[ -n "${NULL_SINK_MODULE_ID}" ]] && command -v pactl >/dev/null 2>&1; then
        pactl unload-module "${NULL_SINK_MODULE_ID}" >/dev/null 2>&1 || true
    fi

    # Preserve shim log before deleting staging directory
    if [[ -f "${STAGE_DIR}/nativemods-shim.log" ]]; then
        cp -f "${STAGE_DIR}/nativemods-shim.log" "${RUN_LOG_DIR}/nativemods-shim.log" 2>/dev/null || true
    fi

    for f in "${STAGED_CLIENT_FILES[@]}"; do
        rm -f "${f}" 2>/dev/null || true
    done
    rm -f "${CLIENT_DIR}/nativemods-shim.log" 2>/dev/null || true

    if [[ -n "${BACKUP_EXE}" && -f "${BACKUP_EXE}" ]]; then
        cp -f "${BACKUP_EXE}" "${GAME_PATCHED_EXE}"
    fi

    # Restore character save DB snapshot so Cosmetics (K) equip testing leaves zero save mutations
    if [[ -d "${CHAR_DB_BACKUP_DIR:-}" ]]; then
        rm -f "${LOCALEXILE_DIR}/data/characters.db-wal" "${LOCALEXILE_DIR}/data/characters.db-shm" 2>/dev/null || true
        for dbf in "${CHAR_DB_BACKUP_DIR}"/characters.db*; do
            if [[ -f "${dbf}" ]]; then
                cp -a "${dbf}" "${LOCALEXILE_DIR}/data/" 2>/dev/null || true
            fi
        done
    fi

    rm -rf "${STAGE_DIR}" 2>/dev/null || true

    # Verify Client/ and Mods/ integrity
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

    exit "${exit_code}"
}
trap cleanup EXIT INT TERM

# 1. Verify no Hyprland leak before starting
check_hyprland_leaks "pre-launch"

# 2. Pick a free Xvfb display >= 50 (never touching :0 or :1)
if [[ "${DISPLAY_NUM}" -lt 50 ]]; then
    DISPLAY_NUM=96
fi
while [[ -S "/tmp/.X11-unix/X${DISPLAY_NUM}" || -f "/tmp/.X${DISPLAY_NUM}-lock" ]]; do
    DISPLAY_NUM=$((DISPLAY_NUM + 1))
done

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
    echo "ERROR: Timed out waiting for Xvfb socket /tmp/.X11-unix/X${DISPLAY_NUM}" >&2
    exit 1
fi
echo "    Xvfb :${DISPLAY_NUM} ready (pid=${XVFB_PID})"

# 3. Create isolated PipeWire/PulseAudio virtual null sink (unless --audible)
if [[ "${AUDIBLE}" -eq 0 ]] && command -v pactl >/dev/null 2>&1; then
    NULL_SINK_NAME="nativemods_maxwell_${RUN_ID//[^a-zA-Z0-9_]/_}"
    if NULL_SINK_MODULE_ID="$(pactl load-module module-null-sink \
        "sink_name=${NULL_SINK_NAME}" \
        "sink_properties=device.description=NativeModsMaxwellVirtualSink" 2>/dev/null)"; then
        echo "    Virtual PipeWire null sink ready: ${NULL_SINK_NAME} (module=${NULL_SINK_MODULE_ID})"
    else
        NULL_SINK_NAME=""
        NULL_SINK_MODULE_ID=""
        echo "    [warn] Could not create PipeWire null sink; FMOD will use default/fallback sink"
    fi
fi

# 4. Prepare temporary staging directory inside LocalExile/data (visible inside steamrt4)
mkdir -p "${STAGE_DIR}/host" "${STAGE_DIR}/overlay" "${STAGE_DIR}/server-mods"
cp -a "${DIST_DIR}/." "${STAGE_DIR}/host/"
cp -a "${ASSETS_DIR}/." "${STAGE_DIR}/overlay/"

# Stage Maxwell client mod into host/Mods/Client/maxwell/
MAXWELL_CLIENT_STAGE="${STAGE_DIR}/host/Mods/Client/maxwell"
mkdir -p "${MAXWELL_CLIENT_STAGE}"
if [[ -n "${CLIENT_MOD_DIR}" && -d "${CLIENT_MOD_DIR}" ]]; then
    cp -a "${CLIENT_MOD_DIR}/." "${MAXWELL_CLIENT_STAGE}/"
fi
if [[ -f "${REPO_ROOT}/examples/DualMod.MaxwellPet/mod.json" ]]; then
    cp -f "${REPO_ROOT}/examples/DualMod.MaxwellPet/mod.json" "${MAXWELL_CLIENT_STAGE}/mod.json"
fi
if [[ -d "${REPO_ROOT}/examples/DualMod.MaxwellPet/client" ]]; then
    mkdir -p "${MAXWELL_CLIENT_STAGE}/client"
    # Stage UTF-8 mod client files (e.g. MaxwellPanel.ui) directly — native shim's OverlayAndChannels
    # auto-transcodes UTF-8 .ui/.ot/.ao/.act/.amd/.sm to UTF-16LE+BOM+CRLF on the fly!
    # Preserve AssetPipeline-generated files already present in overlay/ (e.g. MaxwellPet.ot).
    python3 - "${REPO_ROOT}/examples/DualMod.MaxwellPet/client" "${MAXWELL_CLIENT_STAGE}/client" "${STAGE_DIR}/overlay" <<'PY'
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

# Stage Maxwell server mod into temporary server-mods/maxwell/ (loaded via Game_MODS_FOLDER)
if [[ -n "${SERVER_MOD_DIR}" && -d "${SERVER_MOD_DIR}" ]]; then
    MAXWELL_SERVER_STAGE="${STAGE_DIR}/server-mods/maxwell"
    mkdir -p "${MAXWELL_SERVER_STAGE}"
    cp -a "${SERVER_MOD_DIR}/." "${MAXWELL_SERVER_STAGE}/"
    for meta in mod.json settings.json; do
        if [[ -f "${REPO_ROOT}/examples/DualMod.MaxwellPet/${meta}" && ! -f "${MAXWELL_SERVER_STAGE}/${meta}" ]]; then
            cp -f "${REPO_ROOT}/examples/DualMod.MaxwellPet/${meta}" "${MAXWELL_SERVER_STAGE}/${meta}"
        fi
    done
    echo "    Staged Maxwell server mod into ${MAXWELL_SERVER_STAGE} (Game_MODS_FOLDER)"
fi

SHIM_LOG_LINUX="${STAGE_DIR}/nativemods-shim.log"
: > "${SHIM_LOG_LINUX}"

# Stage native shim DLL(s) into Client/ (always removed in trap cleanup)
cp "${STAGE_DIR}/host/LocalExile.ClientHost.dll" "${CLIENT_DIR}/LocalExile.ClientHost.dll"
STAGED_CLIENT_FILES+=("${CLIENT_DIR}/LocalExile.ClientHost.dll")

if [[ "${MODE}" == "proxy" ]]; then
    cp "${STAGE_DIR}/host/version.dll" "${CLIENT_DIR}/version.dll"
    STAGED_CLIENT_FILES+=("${CLIENT_DIR}/version.dll")
else
    BACKUP_EXE="${STAGE_DIR}/Game.patched.exe.backup"
    cp -a "${GAME_PATCHED_EXE}" "${BACKUP_EXE}"
    echo "==> Applying PE32+ .nmimp import patch to Game.patched.exe (with guaranteed restore)..."
    dotnet run --project "${REPO_ROOT}/src/LocalExile.Client.Patcher/LocalExile.Client.Patcher.csproj" \
        -c Release -- patch "${GAME_PATCHED_EXE}" --allow-synthetic --no-backup \
        >"${RUN_LOG_DIR}/patcher.log" 2>&1
    dotnet run --project "${REPO_ROOT}/src/LocalExile.Client.Patcher/LocalExile.Client.Patcher.csproj" \
        -c Release -- verify "${GAME_PATCHED_EXE}" --allow-synthetic \
        >>"${RUN_LOG_DIR}/patcher.log" 2>&1
    cat "${RUN_LOG_DIR}/patcher.log"
fi

# 5. Generate isolated Game_CLIENT_RUNNER wrapper script
RUNNER_SCRIPT="${STAGE_DIR}/maxwell-client-runner.sh"
HOST_DIR_WINE="$(to_wine_path "${STAGE_DIR}/host")"
ADDR_DIR_WINE="$(to_wine_path "${STAGE_DIR}/host/addresses")"
MODS_DIR_WINE="$(to_wine_path "${STAGE_DIR}/host/Mods/Client")"
OVERLAY_DIR_WINE="$(to_wine_path "${STAGE_DIR}/overlay")"
LOG_PATH_WINE="$(to_wine_path "${SHIM_LOG_LINUX}")"

cat > "${RUNNER_SCRIPT}" <<EOF
#!/usr/bin/env bash
# Auto-generated isolated Xvfb Proton runner for DualMod.MaxwellPet
set -euo pipefail

# Hard-strip every Wayland / Hyprland variable so Proton/Wine/Vulkan can NEVER touch the user's desktop
unset WAYLAND_DISPLAY
unset HYPRLAND_INSTANCE_SIGNATURE
unset HYPRLAND_CMD
unset XDG_CURRENT_DESKTOP
unset XDG_SESSION_DESKTOP
unset XDG_SESSION_TYPE
unset XDG_BACKEND
unset SDL_VIDEODRIVER
unset GDK_BACKEND
unset QT_QPA_PLATFORM

export DISPLAY=":${DISPLAY_NUM}"
export PROTON_ENABLE_WAYLAND=0
export NATIVEMODS_HOST_DIR='${HOST_DIR_WINE}'
export NATIVEMODS_ADDRESSES_DIR='${ADDR_DIR_WINE}'
export NATIVEMODS_MODS_DIR='${MODS_DIR_WINE}'
export NATIVEMODS_OVERLAY_DIR='${OVERLAY_DIR_WINE}'
export NATIVEMODS_MAXWELL_ASSETS_DIR='${OVERLAY_DIR_WINE}'
export NATIVEMODS_LOG_PATH='${LOG_PATH_WINE}'
EOF

if [[ "${TRACE_FILES}" -eq 1 ]]; then
    echo "export NATIVEMODS_TRACE_FILES=1" >> "${RUNNER_SCRIPT}"
fi

if [[ -n "${MAXWELL_WINEDEBUG:-}" ]]; then
    echo "export WINEDEBUG='${MAXWELL_WINEDEBUG}'" >> "${RUNNER_SCRIPT}"
fi

if [[ -n "${NULL_SINK_NAME}" ]]; then
    echo "export PULSE_SINK='${NULL_SINK_NAME}'" >> "${RUNNER_SCRIPT}"
fi

if [[ "${MODE}" == "proxy" ]]; then
    cat >> "${RUNNER_SCRIPT}" <<'EOF'
export WINEDLLOVERRIDES="${WINEDLLOVERRIDES:+${WINEDLLOVERRIDES};}version=n,b"
EOF
fi

# NOTE: Do NOT pass --nosound so FMOD 2.03 genuinely initializes and plays Audio/Mods/Maxwell/stock_market.ogg!
cat >> "${RUNNER_SCRIPT}" <<'EOF'
exec umu-run "$@" --nologo --disable-assert-box
EOF
chmod +x "${RUNNER_SCRIPT}"

# 6. Launch LocalExile stack + Proton client strictly via scripts/dev/live.ps1
CLIENT_LOG_FILE="${CLIENT_DIR}/logs/Client.txt"
CLIENT_LOG_LINES_BEFORE=0
if [[ -f "${CLIENT_LOG_FILE}" ]]; then
    CLIENT_LOG_LINES_BEFORE="$(wc -l < "${CLIENT_LOG_FILE}")"
fi

echo "==> Launching LocalExile servers + 3.29.3d Proton client via scripts/dev/live.ps1 ..."
(
    cd "${LOCALEXILE_DIR}"
    if [[ -n "${SERVER_MOD_DIR}" && -d "${STAGE_DIR}/server-mods/maxwell" ]]; then
        Game_MODS_FOLDER="${STAGE_DIR}/server-mods" \
        Game_CLIENT_RUNNER="${RUNNER_SCRIPT}" \
            pwsh -NoProfile -File "${LIVE_PS1}" -NoBuild -NoExtract
    else
        Game_CLIENT_RUNNER="${RUNNER_SCRIPT}" \
            pwsh -NoProfile -File "${LIVE_PS1}" -NoBuild -NoExtract
    fi
) 2>&1 | tee "${RUN_LOG_DIR}/live-ps1.log"

CLIENT_PID=""
if [[ -f "${LOCALEXILE_DIR}/data/clients/default.json" ]]; then
    CLIENT_PID="$(python3 -c 'import json; print(json.load(open("'"${LOCALEXILE_DIR}/data/clients/default.json"'"))["pid"])' 2>/dev/null || true)"
fi
if [[ -z "${CLIENT_PID}" || ! -d "/proc/${CLIENT_PID}" ]]; then
    CLIENT_PID="$(pgrep -n -f "${GAME_PATCHED_EXE}" || true)"
fi
if [[ -z "${CLIENT_PID}" ]]; then
    echo "ERROR: Could not locate running Game.patched.exe PID after live.ps1 returned!" >&2
    exit 1
fi
echo "==> Proton client running (Linux PID=${CLIENT_PID}); waiting for CoreCLR + UI root + window..."

# 7. Wait for CoreCLR boot, PassA frame hook, and initial login screen render
CRASHED=0
BOOT_WAIT_MAX=60
READY_FOR_LOGIN=0
for sec in $(seq 1 "${BOOT_WAIT_MAX}"); do
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
        echo "    [t=${sec}s] CoreCLR + PassA frame hook live! Settling 5s for login screen render..."
        sleep 5
        break
    fi
    sleep 1
done

check_hyprland_leaks "post-boot"

if [[ "${CRASHED}" -eq 0 ]]; then
    POE_WID="$(focus_poe_window || true)"
    echo "    X11 PoE window ID: ${POE_WID:-<not found, using root>}"
    capture_screenshot "01-login-screen" || true
fi

# 8. Automated login -> character select -> in-game -> Maxwell trigger -> multi-frame capture + audio capture
AUDIO_STATS="not-captured"
if [[ "${CRASHED}" -eq 0 && "${AUTO_INPUT}" -eq 1 && "${READY_FOR_LOGIN}" -eq 1 ]]; then
    GATEWAY_LOG="${LOCALEXILE_DIR}/logs/live-gateway.log"
    AUTH_LOG="${LOCALEXILE_DIR}/logs/live-auth.log"

    echo "==> [Phase A] Submitting saved login on Xvfb :${DISPLAY_NUM}..."
    focus_poe_window >/dev/null || true
    # Click center-bottom "LOG IN" button area (1280x720) or press Return on focused login form
    DISPLAY=":${DISPLAY_NUM}" xdotool key --delay 50 Return || true
    sleep 2
    # Wait up to 20s for Auth login / character list
    LOGGED_IN=0
    for sec in $(seq 1 20); do
        if ! kill -0 "${CLIENT_PID}" 2>/dev/null; then CRASHED=1; break; fi
        if grep -qiE "login|character list|0x0015|authenticated|session" "${AUTH_LOG}" 2>/dev/null; then
            LOGGED_IN=1
            echo "    [login t=${sec}s] Auth activity detected in live-auth.log"
            sleep 3
            break
        fi
        # Fallback click on standard 1280x720 LOG IN button if Return alone didn't submit
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

        IN_WORLD=0
        for sec in $(seq 1 90); do
            if ! kill -0 "${CLIENT_PID}" 2>/dev/null; then CRASHED=1; break; fi
            if grep -qiE "You have joined global chat|Area generated at Seed|grant.*Maxwell|You have entered" "${GATEWAY_LOG}" 2>/dev/null \
               || tail -n +"$((CLIENT_LOG_LINES_BEFORE + 1))" "${CLIENT_LOG_FILE}" 2>/dev/null | grep -qiE "You have joined global chat|Area generated at Seed|You have entered"; then
                IN_WORLD=1
                echo "    [world t=${sec}s] Character fully entered town (PlayerEntered / You have entered)!"
                sleep 4
                break
            fi
            if (( sec == 4 )) && ! grep -qiE "CONNECT" "${GATEWAY_LOG}" 2>/dev/null; then
                # Click PLAY button (bottom-right on 1280x720 character select)
                DISPLAY=":${DISPLAY_NUM}" xdotool mousemove 1190 623 click 1 || true
            fi
            if (( sec % 15 == 0 )); then
                echo "    [world t=${sec}s] still loading area..."
            fi
            sleep 1
        done

        if [[ "${CRASHED}" -eq 0 ]]; then
            check_hyprland_leaks "in-game"
            capture_screenshot "03-in-game-before" || true

            # Open Cosmetics panel (K) FIRST to inspect & equip brand-new Maxwell Pet MTX directly from Pet Slot 1!
            echo "==> [Phase C1] Opening Cosmetics panel (K), scrolling Pet Slot 1 picker to Maxwell, and equipping via Cosmetics..."
            focus_poe_window >/dev/null || true
            DISPLAY=":${DISPLAY_NUM}" xdotool key --delay 80 k || true
            sleep 3
            capture_screenshot "04a-cosmetics-panel" || true

            # Click Pet Slot 1 (920, 385 at 1280x720) to open the Pet MTX picker flyout and wait 12s for all 7850 records to populate
            DISPLAY=":${DISPLAY_NUM}" xdotool mousemove 920 385 click 1 || true
            sleep 12
            capture_screenshot "04b-cosmetics-pet-picker-top" || true

            # Drag Pet picker scrollbar thumb from (873, 408) to ~62% down (873, 512) where "Maxwell the Cat Pet" sits alphabetically
            DISPLAY=":${DISPLAY_NUM}" xdotool mousemove 873 408 mousedown 1 sleep 0.2 mousemove 873 512 sleep 0.2 mouseup 1 || true
            sleep 0.8
            # Hover over Maxwell the Cat Pet icon (bottom-right cell 825, 560) to display native PoE tooltip + custom 2D icon
            DISPLAY=":${DISPLAY_NUM}" xdotool mousemove 825 560 || true
            sleep 0.8
            capture_screenshot "04c-cosmetics-maxwell-tooltip" || true

            # CLICK Maxwell the Cat Pet directly in the native Cosmetics (K) Pet Slot 1 picker!
            # Sends C->S 0x01f9 -> LocalExile CosmeticsFeature.SpawnPets spawns Maxwell -> LocalExilePetMtxRegistrar syncs UI + looping FMOD audio!
            DISPLAY=":${DISPLAY_NUM}" xdotool mousemove 825 560 click 1 || true
            sleep 3
            capture_screenshot "04d-cosmetics-maxwell-equipped" || true

            # Close Cosmetics panel cleanly before opening MaxwellPanel.ui
            DISPLAY=":${DISPLAY_NUM}" xdotool key --delay 80 Escape || true
            sleep 0.8

            echo "==> [Phase C2] Opening Maxwell control panel (/maxwell) — verifying sync with Cosmetics equip & live button clicks..."
            focus_poe_window >/dev/null || true
            DISPLAY=":${DISPLAY_NUM}" xdotool key --delay 80 Return || true
            sleep 0.4
            DISPLAY=":${DISPLAY_NUM}" xdotool type --delay 35 "/maxwell" || true
            sleep 0.3
            DISPLAY=":${DISPLAY_NUM}" xdotool key --delay 80 Return || true
            sleep 2
            capture_screenshot "05-maxwell-panel-synced" || true

            # Click native "MAXWELL DANCE" (640, 304) and Spin "+30" (744, 185) buttons directly on MaxwellPanel.ui!
            DISPLAY=":${DISPLAY_NUM}" xdotool mousemove 640 304 click 1 || true
            sleep 0.6
            DISPLAY=":${DISPLAY_NUM}" xdotool mousemove 744 185 click 1 || true
            sleep 1.5
            capture_screenshot "06-maxwell-panel-clicked" || true

            # Click native "CLOSE" button (814, 304) so Maxwell 3D model is unobstructed for spin/hover burst
            DISPLAY=":${DISPLAY_NUM}" xdotool mousemove 814 304 click 1 || true
            sleep 1

            # Capture multi-timestamp spin + hover proof burst
            echo "==> [Phase D] Capturing ${SPIN_FRAMES} multi-timestamp screenshots (interval=${SPIN_INTERVAL}s)..."
            for i in $(seq 0 "$((SPIN_FRAMES - 1))"); do
                if ! kill -0 "${CLIENT_PID}" 2>/dev/null; then CRASHED=1; break; fi
                capture_screenshot "07-maxwell-spin-${i}" || true
                sleep "${SPIN_INTERVAL}"
            done

            # Auto-generate zoomed 5-frame spin/hover strip montage around the player & Maxwell
            if command -v magick >/dev/null 2>&1 && [[ -f "${SCREENSHOT_DIR}/07-maxwell-spin-0.png" ]]; then
                STRIP_TMP="${SCREENSHOT_DIR}/.strip-tmp"
                mkdir -p "${STRIP_TMP}"
                for i in $(seq 0 "$((SPIN_FRAMES - 1))"); do
                    if [[ -f "${SCREENSHOT_DIR}/07-maxwell-spin-${i}.png" ]]; then
                        magick "${SCREENSHOT_DIR}/07-maxwell-spin-${i}.png" -crop 200x180+350+190 +repage -scale 200% \
                            "${STRIP_TMP}/spin-${i}.png" 2>/dev/null || true
                    fi
                done
                magick montage "${STRIP_TMP}"/spin-*.png -tile "${SPIN_FRAMES}x1" -geometry +6+6 -background white \
                    "${SCREENSHOT_DIR}/08-maxwell-spin-strip.png" 2>/dev/null || true
                rm -rf "${STRIP_TMP}"
                if [[ -f "${SCREENSHOT_DIR}/08-maxwell-spin-strip.png" ]]; then
                    echo "    [shot] ${SCREENSHOT_DIR}/08-maxwell-spin-strip.png (zoomed ${SPIN_FRAMES}-frame spin+hover strip)"
                fi
            fi

            # Capture 4s audio sample from PipeWire virtual sink monitor (verifies real FMOD PCM output)
            if [[ -n "${NULL_SINK_NAME}" && "${CRASHED}" -eq 0 ]]; then
                AUDIO_WAV="${RUN_LOG_DIR}/maxwell-audio-capture.wav"
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

# 9. Final soak & telemetry
if [[ "${CRASHED}" -eq 0 && "${SOAK_SECONDS}" -gt 0 ]]; then
    echo "==> Running final ${SOAK_SECONDS}s soak..."
    for sec in $(seq 1 "${SOAK_SECONDS}"); do
        if ! kill -0 "${CLIENT_PID}" 2>/dev/null; then
            echo "ERROR: Client PID ${CLIENT_PID} exited during final soak at t=${sec}s!" >&2
            CRASHED=1
            break
        fi
        sleep 1
    done
fi

check_hyprland_leaks "pre-shutdown"

# 10. Collect all logs & diagnostics
if kill -0 "${CLIENT_PID}" 2>/dev/null; then
    grep -iE 'LocalExile\.ClientHost|version\.dll|hostfxr|coreclr|fmod|maxwell' "/proc/${CLIENT_PID}/maps" \
        > "${RUN_LOG_DIR}/proc-maps-filtered.txt" 2>/dev/null || true
    cp "/proc/${CLIENT_PID}/status" "${RUN_LOG_DIR}/proc-status.txt" 2>/dev/null || true
fi

if [[ -f "${CLIENT_LOG_FILE}" ]]; then
    tail -n +"$((CLIENT_LOG_LINES_BEFORE + 1))" "${CLIENT_LOG_FILE}" > "${RUN_LOG_DIR}/Client-run.txt" 2>/dev/null || true
fi
for srv in live-client.log live-client.err live-gateway.log live-gateway.err live-auth.log live-auth.err live-patch.log live-patch.err; do
    cp -f "${LOCALEXILE_DIR}/logs/${srv}" "${RUN_LOG_DIR}/${srv}" 2>/dev/null || true
done
cp -f "${SHIM_LOG_LINUX}" "${RUN_LOG_DIR}/nativemods-shim.log" 2>/dev/null || true

# 11. Summary & acceptance assertions
echo ""
echo "==================== MAXWELL PROTON SUMMARY (${MODE}) ===================="
echo "Screenshots : ${SCREENSHOT_DIR}"
ls -lh "${SCREENSHOT_DIR}"/*.png 2>/dev/null || echo "  (no screenshots captured)"
echo "Audio stats : ${AUDIO_STATS}"
echo "--- nativemods-shim.log (first 120 lines) ---"
head -n 120 "${RUN_LOG_DIR}/nativemods-shim.log" 2>/dev/null || true
echo "=========================================================================="

PASS=1
check_log() {
    local label="$1"
    local pattern="$2"
    local required="${3:-1}"
    if grep -qiE "${pattern}" "${RUN_LOG_DIR}/nativemods-shim.log" 2>/dev/null; then
        echo "  [PASS] ${label}"
    elif [[ "${required}" -eq 1 ]]; then
        echo "  [FAIL] ${label} (missing: ${pattern})" >&2
        PASS=0
    else
        echo "  [WARN] ${label} (not seen: ${pattern})"
    fi
}

check_log "CoreCLR booted & NativeEntryPoint.Initialize == 0" "NativeEntryPoint\.Initialize\(nm_abi_table_t\*\) returned 0" 1
check_log "PassA frame hook fired on game thread" "First frame hook hit on game thread" 1
check_log "Live FileSystem overlay hooks armed" "\[OVERLAY\] Armed live FileSystem hooks" 1
check_log "Patched MtxTypes.datc64 served" "\[OVERLAY\] Served '.*MtxTypes\.datc64'" 1
check_log "Patched MtxTypeGameSpecific.datc64 served" "\[OVERLAY\] Served '.*MtxTypeGameSpecific\.datc64'" 1
check_log "Patched Pet.datc64 served" "\[OVERLAY\] Served '.*Pet\.datc64'" 1
check_log "Maxwell client mod loaded" "Loaded client mod 'maxwell'" 1
check_log "FMOD 2.03 audio initialized" "\[AUDIO\] FMOD 2\.03 initialized" 1
check_log "Maxwell OGG audio played on loop" "\[AUDIO\] Playing OGG '.*stock_market\.ogg'.*loop=1.*fmod_rc=0" 1
check_log "Maxwell custom 2D icon served to Cosmetics picker" "\[OVERLAY\] Served '.*MaxwellPet\.dds'" 1
check_log "Maxwell custom .ui loaded natively" "\[UI\] Loaded native window '.*MaxwellPanel\.ui'" 1
check_log "Maxwell 3D pet mesh/textures served via overlay" "\[OVERLAY\] Served '.*maxwell\.smd'" 1
check_log "Live native UI click triggered GEAL MaxwellDance" "GEAL MaxwellDance triggered" 1
check_log "Live network channel hooks armed" "\[NET\] Armed live channel hooks" 1

if grep -qiE "0x01f9.*8558|Equipped Maxwell|pick.*8558|0x02bb.*MaxwellPet" "${RUN_LOG_DIR}/live-gateway.log" 2>/dev/null; then
    echo "  [PASS] Cosmetics (K) Pet Slot 1 equip / Maxwell pet entity spawn observed in live-gateway.log"
else
    echo "  [WARN] Cosmetics (K) Pet Slot 1 equip not seen in live-gateway.log"
fi

if [[ "${CRASHED}" -ne 0 ]]; then
    echo "  [FAIL] Client crashed or exited prematurely" >&2
    PASS=0
else
    echo "  [PASS] Client survived full run without crash or DeadlockDetector kill"
fi

echo "==> Run logs saved under: ${RUN_LOG_DIR}"
if [[ "${PASS}" -ne 1 ]]; then
    echo "ERROR: One or more required Maxwell Proton checks failed." >&2
    exit 1
fi
echo "==> MAXWELL PROTON HARNESS RUN COMPLETED!"
