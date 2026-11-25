#!/usr/bin/env bash
# Spike R1 live runner & verification harness for LocalExile NativeMods on Linux/Proton.
#
# Safety guarantees:
#   1. Strictly isolated virtual screen via Xvfb (unsets WAYLAND_DISPLAY, HYPRLAND_INSTANCE_SIGNATURE,
#      XDG_CURRENT_DESKTOP, XDG_SESSION_TYPE, PROTON_ENABLE_WAYLAND; never touches the active Hyprland session).
#   2. Always launches the client ONLY through LocalExile's scripts/dev/live.ps1 (--patching-server 127.0.0.1:PORT).
#   3. Never leaves /home/nam/Git/LocalExile/Client/ modified: records SHA-256 of Game.exe and Game.patched.exe
#      before launch, removes all staged files and restores any patched executable in a trap EXIT handler,
#      and verifies exact SHA-256 restoration before exiting.
#
# Usage:
#   ./scripts/run-r1-proton.sh [options]
#
# Options:
#   --dist-dir <path>         Path to built win-x64 bundle (default: <repo>/dist/win-x64)
#   --localexile-dir <path>   Path to LocalExile checkout (default: /home/nam/Git/LocalExile)
#   --mode <proxy|import-patch>  Injection mode (default: proxy)
#   --soak-seconds <N>        Seconds to keep client running after boot for soak (default: 45)
#   --display <N>             Preferred Xvfb display number (default: 97; auto-increments if busy)
#   --log-dir <path>          Directory to store R1 output logs (default: <repo>/build/r1-proton-logs)
#   --keep-servers            Do not stop LocalExile servers on exit (still stops client & cleans Client/)
#   -h, --help                Show help

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

DIST_DIR="${REPO_ROOT}/dist/win-x64"
LOCALEXILE_DIR="/home/nam/Git/LocalExile"
MODE="proxy"
SOAK_SECONDS=45
DISPLAY_NUM=97
LOG_DIR="${REPO_ROOT}/build/r1-proton-logs"
KEEP_SERVERS=0

usage() {
    sed -n '2,25p' "$0" | sed 's/^# \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --dist-dir)       DIST_DIR="$2"; shift 2 ;;
        --localexile-dir) LOCALEXILE_DIR="$2"; shift 2 ;;
        --mode)           MODE="$2"; shift 2 ;;
        --soak-seconds)   SOAK_SECONDS="$2"; shift 2 ;;
        --display)        DISPLAY_NUM="$2"; shift 2 ;;
        --log-dir)        LOG_DIR="$2"; shift 2 ;;
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

for cmd in pwsh umu-run Xvfb sha256sum python3; do
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
        echo "==> dist directory not found; building via scripts/build-win64.sh ..."
        "${REPO_ROOT}/scripts/build-win64.sh"
    else
        echo "ERROR: dist directory not found: ${DIST_DIR} (pass --dist-dir)" >&2
        exit 1
    fi
fi

DIST_DIR="$(cd "${DIST_DIR}" && pwd)"
for req in LocalExile.ClientHost.dll version.dll hostfxr.dll coreclr.dll LocalExile.Client.Host.dll LocalExile.Client.Host.runtimeconfig.json addresses/3.29.3d.json Mods/Client/harvest-garden/harvest-garden.Client.dll; do
    if [[ ! -f "${DIST_DIR}/${req}" ]]; then
        echo "ERROR: Missing required bundle artifact: ${DIST_DIR}/${req}" >&2
        exit 1
    fi
done

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

# Staging directory inside LocalExile/data so Proton's steamrt4 container (pressure-vessel) can always mount/see it
# (steamrt4 does NOT expose host /tmp). Cleaned up unconditionally on EXIT.
STAGE_DIR="${LOCALEXILE_DIR}/data/r1-proton-stage-${RUN_ID}"
BACKUP_EXE=""
XVFB_PID=""
STAGED_CLIENT_FILES=()
CLEANED_UP=0

GAME_EXE_SHA_BEFORE="$(sha256sum "${GAME_EXE}" | awk '{print $1}')"
GAME_PATCHED_SHA_BEFORE="$(sha256sum "${GAME_PATCHED_EXE}" | awk '{print $1}')"

echo "==> Spike R1 Proton Runner (run=${RUN_ID}, mode=${MODE}, soak=${SOAK_SECONDS}s)"
echo "    LocalExile : ${LOCALEXILE_DIR}"
echo "    Dist bundle: ${DIST_DIR}"
echo "    Log dir    : ${RUN_LOG_DIR}"
echo "    Game.exe SHA-256        : ${GAME_EXE_SHA_BEFORE}"
echo "    Game.patched.exe SHA-256: ${GAME_PATCHED_SHA_BEFORE}"

to_wine_path() {
    # Convert absolute Linux path /a/b/c -> Z:\a\b\c
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
    blob = " ".join(str(c.get(k, "")) for k in ("class", "title", "initialClass", "initialTitle")).lower()
    if any(tok in blob for tok in ("path of exile", "game.patched", "pathofexile", "umu-238960")):
        cls = c.get("class")
        title = c.get("title")
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

cleanup() {
    if [[ "${CLEANED_UP}" -eq 1 ]]; then
        return
    fi
    CLEANED_UP=1
    local exit_code=$?
    set +e

    echo "==> Cleaning up Proton client, servers, Xvfb, and staged files..."
    if [[ "${KEEP_SERVERS}" -eq 1 ]]; then
        (cd "${LOCALEXILE_DIR}" && pwsh -NoProfile -File "${LIVE_PS1}" -Stop -ClientOnly) >/dev/null 2>&1 || true
    else
        (cd "${LOCALEXILE_DIR}" && pwsh -NoProfile -File "${LIVE_PS1}" -Stop) >/dev/null 2>&1 || true
    fi

    # Wait briefly for any remaining Game.patched.exe from this checkout to exit cleanly
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

    # Copy shim log before removing staging directory
    if [[ -f "${STAGE_DIR}/nativemods-r1.log" ]]; then
        cp -f "${STAGE_DIR}/nativemods-r1.log" "${RUN_LOG_DIR}/nativemods-r1.log" 2>/dev/null || true
    fi

    # Remove only staged files in Client/
    for f in "${STAGED_CLIENT_FILES[@]}"; do
        rm -f "${f}" 2>/dev/null || true
    done
    rm -f "${CLIENT_DIR}/nativemods-shim.log" 2>/dev/null || true

    # Restore Game.patched.exe if import-patch mode backed it up
    if [[ -n "${BACKUP_EXE}" && -f "${BACKUP_EXE}" ]]; then
        cp -f "${BACKUP_EXE}" "${GAME_PATCHED_EXE}"
    fi

    rm -rf "${STAGE_DIR}" 2>/dev/null || true

    # Verify Client/ integrity
    local sha_game_after sha_patched_after
    sha_game_after="$(sha256sum "${GAME_EXE}" | awk '{print $1}')"
    sha_patched_after="$(sha256sum "${GAME_PATCHED_EXE}" | awk '{print $1}')"

    local restore_ok=1
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

    if [[ "${restore_ok}" -eq 1 ]]; then
        echo "==> Client/ restored 100% cleanly (SHA-256 verified identical; zero stray DLLs)."
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
    DISPLAY_NUM=97
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

# 3. Prepare staging directory inside LocalExile/data (visible inside steamrt4 container)
mkdir -p "${STAGE_DIR}/host"
cp -a "${DIST_DIR}/." "${STAGE_DIR}/host/"
SHIM_LOG_LINUX="${STAGE_DIR}/nativemods-r1.log"
: > "${SHIM_LOG_LINUX}"

# Stage native shim DLL(s) into Client/
cp "${STAGE_DIR}/host/LocalExile.ClientHost.dll" "${CLIENT_DIR}/LocalExile.ClientHost.dll"
STAGED_CLIENT_FILES+=("${CLIENT_DIR}/LocalExile.ClientHost.dll")

if [[ "${MODE}" == "proxy" ]]; then
    cp "${STAGE_DIR}/host/version.dll" "${CLIENT_DIR}/version.dll"
    STAGED_CLIENT_FILES+=("${CLIENT_DIR}/version.dll")
else
    # Import-patch mode: back up Game.patched.exe and append .nmimp import descriptor
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

# 4. Generate isolated Game_CLIENT_RUNNER wrapper script
RUNNER_SCRIPT="${STAGE_DIR}/r1-client-runner.sh"
HOST_DIR_WINE="$(to_wine_path "${STAGE_DIR}/host")"
ADDR_DIR_WINE="$(to_wine_path "${STAGE_DIR}/host/addresses")"
MODS_DIR_WINE="$(to_wine_path "${STAGE_DIR}/host/Mods/Client")"
LOG_PATH_WINE="$(to_wine_path "${SHIM_LOG_LINUX}")"

cat > "${RUNNER_SCRIPT}" <<EOF
#!/usr/bin/env bash
# Auto-generated isolated Xvfb Proton runner for Spike R1
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
export NATIVEMODS_LOG_PATH='${LOG_PATH_WINE}'
EOF

if [[ "${MODE}" == "proxy" ]]; then
    cat >> "${RUNNER_SCRIPT}" <<'EOF'
export WINEDLLOVERRIDES="${WINEDLLOVERRIDES:+${WINEDLLOVERRIDES};}version=n,b"
EOF
fi

cat >> "${RUNNER_SCRIPT}" <<'EOF'
exec umu-run "$@" --nosound --nologo --disable-assert-box
EOF
chmod +x "${RUNNER_SCRIPT}"

# 5. Launch LocalExile stack + Proton client strictly via scripts/dev/live.ps1
CLIENT_LOG_FILE="${CLIENT_DIR}/logs/Client.txt"
CLIENT_LOG_LINES_BEFORE=0
if [[ -f "${CLIENT_LOG_FILE}" ]]; then
    CLIENT_LOG_LINES_BEFORE="$(wc -l < "${CLIENT_LOG_FILE}")"
fi

echo "==> Launching LocalExile servers + 3.29.3d Proton client via scripts/dev/live.ps1 ..."
LAUNCH_START_EPOCH="$(date +%s)"
(
    cd "${LOCALEXILE_DIR}"
    Game_CLIENT_RUNNER="${RUNNER_SCRIPT}" \
        pwsh -NoProfile -File "${LIVE_PS1}" -NoBuild -NoExtract
) 2>&1 | tee "${RUN_LOG_DIR}/live-ps1.log"

# Identify the running Game.patched.exe PID from live.ps1's client record
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
echo "==> Proton client running (Linux PID=${CLIENT_PID}); starting ${SOAK_SECONDS}s soak & telemetry capture..."

# 6. Soak loop: monitor liveness, Hyprland isolation, memory, threads, and shim log milestones
BOOTED_SEEN=0
FRAME_SEEN=0
MOD_SEEN=0
CRASHED=0

for sec in $(seq 1 "${SOAK_SECONDS}"); do
    if ! kill -0 "${CLIENT_PID}" 2>/dev/null; then
        echo "ERROR: Client PID ${CLIENT_PID} exited prematurely at t=${sec}s!" >&2
        CRASHED=1
        break
    fi

    if (( sec == 3 || sec == 15 || sec == SOAK_SECONDS )); then
        check_hyprland_leaks "soak-t${sec}s"
    fi

    if [[ "${BOOTED_SEEN}" -eq 0 ]] && grep -q "NativeEntryPoint.Initialize(nm_abi_table_t\*) returned 0" "${SHIM_LOG_LINUX}" 2>/dev/null; then
        BOOTED_SEEN=1
        echo "    [t=${sec}s] CoreCLR + NativeEntryPoint.Initialize succeeded!"
    fi
    if [[ "${FRAME_SEEN}" -eq 0 ]] && grep -q "First frame hook hit on game thread" "${SHIM_LOG_LINUX}" 2>/dev/null; then
        FRAME_SEEN=1
        echo "    [t=${sec}s] First StateStack::PassA frame hook hit on game thread!"
    fi
    if [[ "${MOD_SEEN}" -eq 0 ]] && grep -q "Loaded client mod 'harvest-garden'" "${SHIM_LOG_LINUX}" 2>/dev/null; then
        MOD_SEEN=1
        echo "    [t=${sec}s] Managed mod 'harvest-garden' loaded in collectible ALC!"
    fi

    if (( sec == 10 || sec == 30 || sec == SOAK_SECONDS )); then
        RSS_KB="$(awk '/^VmRSS:/ {print $2}' "/proc/${CLIENT_PID}/status" 2>/dev/null || echo 0)"
        THREADS="$(awk '/^Threads:/ {print $2}' "/proc/${CLIENT_PID}/status" 2>/dev/null || echo 0)"
        echo "    [t=${sec}s] alive pid=${CLIENT_PID} VmRSS=${RSS_KB}kB Threads=${THREADS}"
    fi
    sleep 1
done

# 7. Capture process maps and logs before shutdown
if kill -0 "${CLIENT_PID}" 2>/dev/null; then
    grep -iE 'LocalExile\.ClientHost|version\.dll|hostfxr|coreclr|clrjit|harvest-garden' "/proc/${CLIENT_PID}/maps" \
        > "${RUN_LOG_DIR}/proc-maps-filtered.txt" 2>/dev/null || true
    cp "/proc/${CLIENT_PID}/status" "${RUN_LOG_DIR}/proc-status.txt" 2>/dev/null || true
    cp "/proc/${CLIENT_PID}/cmdline" "${RUN_LOG_DIR}/proc-cmdline.raw" 2>/dev/null || true
fi

if [[ -f "${CLIENT_LOG_FILE}" ]]; then
    tail -n +"$((CLIENT_LOG_LINES_BEFORE + 1))" "${CLIENT_LOG_FILE}" > "${RUN_LOG_DIR}/Client-run.txt" 2>/dev/null || true
fi
cp -f "${LOCALEXILE_DIR}/logs/live-client.err" "${RUN_LOG_DIR}/live-client.err" 2>/dev/null || true
cp -f "${LOCALEXILE_DIR}/logs/live-client.log" "${RUN_LOG_DIR}/live-client.log" 2>/dev/null || true
cp -f "${SHIM_LOG_LINUX}" "${RUN_LOG_DIR}/nativemods-r1.log" 2>/dev/null || true

# 8. Evaluate Spike R1 acceptance criteria
echo ""
echo "==================== SPIKE R1 SUMMARY (${MODE}) ===================="
cat "${RUN_LOG_DIR}/nativemods-r1.log"
echo "===================================================================="

PASS=1
require_log() {
    local label="$1"
    local pattern="$2"
    if grep -qE "${pattern}" "${RUN_LOG_DIR}/nativemods-r1.log"; then
        echo "  [PASS] ${label}"
    else
        echo "  [FAIL] ${label} (missing pattern: ${pattern})" >&2
        PASS=0
    fi
}

require_log "Build 3.29.3d recognized & 0x1528fbf bytes verified" "pass_a_hook\(0x1528fbf\)=OK"
require_log "Frame hook armed on 0x1528f40" "Frame hook armed at 0x"
require_log "Frame hook fired on game thread" "First frame hook hit on game thread"
require_log "Off-game-thread CoreCLR booted & NativeEntryPoint.Initialize == 0" "NativeEntryPoint\.Initialize\(nm_abi_table_t\*\) returned 0"
require_log "JsonAddressLibrary loaded 3.29.3d.json" "Loaded JsonAddressLibrary.*3\.29\.3d"
require_log "harvest-garden.Client.dll loaded in collectible ALC" "Loaded client mod 'harvest-garden'.*collectibleAlc=True"
require_log "Managed frame ticks executed" "Managed frame tick #"

if [[ "${CRASHED}" -ne 0 ]]; then
    echo "  [FAIL] Client crashed or exited before ${SOAK_SECONDS}s soak completed" >&2
    PASS=0
else
    echo "  [PASS] Client survived full ${SOAK_SECONDS}s soak without crash or DeadlockDetector kill"
fi

if grep -iE "Deadlock|Fatal|Exception|crash" "${RUN_LOG_DIR}/Client-run.txt" 2>/dev/null | grep -vq "Irrecoverable Exception Callback: SET"; then
    echo "  [WARN] Check ${RUN_LOG_DIR}/Client-run.txt for engine warning/error lines:"
    grep -iE "Deadlock|Fatal|Exception|crash" "${RUN_LOG_DIR}/Client-run.txt" | grep -v "Irrecoverable Exception Callback: SET" | head -n 10 || true
fi

echo "==> Full R1 artifacts saved under: ${RUN_LOG_DIR}"
if [[ "${PASS}" -ne 1 ]]; then
    echo "ERROR: One or more Spike R1 checks failed." >&2
    exit 1
fi
echo "==> SPIKE R1 PASSED (${MODE})!"
