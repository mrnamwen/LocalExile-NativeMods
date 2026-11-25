#!/usr/bin/env bash
# Interactive Desktop Launcher for Path of Exile 3.29.3d + LocalExile + NativeMods + DualMod.MaxwellPet.
#
# Launches the real 3.29.3d Path of Exile client and LocalExile Gateway directly on your live
# desktop session (Hyprland/Wayland/X11) with native resolution and real audio output so you can
# play interactively, equip Maxwell the Cat via Cosmetics (K), hear Weebl's "Stock Market", and
# take screenshots normally.
#
# Safety & teardown guarantees:
#   - Stages all NativeMods host, overlay, and server-mod files under
#     <localexile-dir>/data/nativemods-desktop-<run-id> (visible inside Proton's steamrt4 container,
#     never touching LocalExile/Mods/).
#   - Records baseline SHA-256 hashes of Client/Game.exe and Client/Game.patched.exe before launch.
#   - Snapshots data/characters.db* by default and restores it on exit (unless --keep-save is passed)
#     so equipping custom Pet MTX never leaves a modded pet slot ID in your vanilla LocalExile save.
#   - On PoE exit or Ctrl+C, automatically stops LocalExile (live.ps1 -Stop), removes staged DLLs
#     from Client/, restores Game.patched.exe (in import-patch mode), deletes the staging directory,
#     and verifies Game.exe, Game.patched.exe, and Mods/ match baseline 100%.
#
# Usage:
#   ./scripts/launch-desktop.sh [options]
#
# Options:
#   --mode <proxy|import-patch>  Injection mode (default: proxy)
#   --localexile-dir <path>      Path to LocalExile checkout (default: /home/nam/Git/LocalExile)
#   --rebuild                    Force rebuild of Win64 extender, Maxwell assets, and DualMod.MaxwellPet
#   --keep-save                  Keep data/characters.db* changes on exit (default: snapshot & restore)
#   --show-logo                  Show the GGG intro video on startup (default passes --nologo for faster boot)
#   --trace-files                Enable NATIVEMODS_TRACE_FILES=1 diagnostic tracing
#   --dry-run                    Validate tools/paths/staging plan and print exact launch command
#                                without starting LocalExile/Proton or modifying LocalExile/
#   -h, --help                   Show this help

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

MODE="proxy"
LOCALEXILE_DIR="/home/nam/Git/LocalExile"
REBUILD=0
KEEP_SAVE=0
SHOW_LOGO=0
TRACE_FILES=0
DRY_RUN=0

usage() {
    sed -n '2,32p' "$0" | sed 's/^# \{0,1\}//'
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
        --mode)
            require_arg "$1" "${2:-}"
            MODE="$2"
            shift 2
            ;;
        --localexile-dir)
            require_arg "$1" "${2:-}"
            LOCALEXILE_DIR="$2"
            shift 2
            ;;
        --rebuild)
            REBUILD=1
            shift
            ;;
        --keep-save)
            KEEP_SAVE=1
            shift
            ;;
        --show-logo)
            SHOW_LOGO=1
            shift
            ;;
        --trace-files)
            TRACE_FILES=1
            shift
            ;;
        --dry-run)
            DRY_RUN=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "ERROR: Unknown option: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ "${MODE}" != "proxy" && "${MODE}" != "import-patch" ]]; then
    echo "ERROR: --mode must be 'proxy' or 'import-patch' (got '${MODE}')" >&2
    exit 2
fi

if [[ ! -d "${LOCALEXILE_DIR}" ]]; then
    echo "ERROR: LocalExile directory not found: ${LOCALEXILE_DIR}" >&2
    exit 1
fi
LOCALEXILE_DIR="$(cd "${LOCALEXILE_DIR}" && pwd)"

CLIENT_DIR="${LOCALEXILE_DIR}/Client"
GAME_EXE="${CLIENT_DIR}/Game.exe"
GAME_PATCHED_EXE="${CLIENT_DIR}/Game.patched.exe"
LIVE_PS1="${LOCALEXILE_DIR}/scripts/dev/live.ps1"

DIST_DIR="${REPO_ROOT}/dist/win-x64"
ASSETS_DIR="${REPO_ROOT}/dist/maxwell-assets"
BUILD_WIN64_SCRIPT="${REPO_ROOT}/scripts/build-win64.sh"
BUILD_ASSETS_SCRIPT="${REPO_ROOT}/scripts/build-maxwell-assets.sh"
SERVER_CSPROJ="${REPO_ROOT}/examples/DualMod.MaxwellPet/Server/DualMod.MaxwellPet.Server.csproj"
CLIENT_CSPROJ="${REPO_ROOT}/examples/DualMod.MaxwellPet/Client/DualMod.MaxwellPet.Client.csproj"
PATCHER_CSPROJ="${REPO_ROOT}/src/LocalExile.Client.Patcher/LocalExile.Client.Patcher.csproj"
SERVER_MOD_DIR="${REPO_ROOT}/examples/DualMod.MaxwellPet/Server/bin/Release/net10.0"
CLIENT_MOD_DIR="${REPO_ROOT}/examples/DualMod.MaxwellPet/Client/bin/Release/net10.0"

# 1. Validate required CLI tools & LocalExile checkout
REQUIRED_TOOLS=(pwsh umu-run sha256sum python3 dotnet)
for cmd in "${REQUIRED_TOOLS[@]}"; do
    if ! command -v "${cmd}" >/dev/null 2>&1; then
        echo "ERROR: Required command not found on PATH: ${cmd}" >&2
        exit 1
    fi
done

if [[ ! -f "${LIVE_PS1}" || ! -f "${GAME_EXE}" || ! -f "${GAME_PATCHED_EXE}" ]]; then
    echo "ERROR: LocalExile checkout or Client binaries missing under ${LOCALEXILE_DIR}" >&2
    echo "       Expected: ${LIVE_PS1}, ${GAME_EXE}, ${GAME_PATCHED_EXE}" >&2
    exit 1
fi

if [[ "${MODE}" == "import-patch" && ! -f "${PATCHER_CSPROJ}" ]]; then
    echo "ERROR: Patcher project not found: ${PATCHER_CSPROJ}" >&2
    exit 1
fi

# Refuse to overwrite pre-existing proxy/host files in Client/
for check_file in "${CLIENT_DIR}/version.dll" "${CLIENT_DIR}/LocalExile.ClientHost.dll"; do
    if [[ -e "${check_file}" ]]; then
        echo "ERROR: Refusing to start: ${check_file} already exists in Client/" >&2
        echo "       Remove stray NativeMods files from ${CLIENT_DIR} before launching." >&2
        exit 1
    fi
done

# 2. Check build completeness (auto-build missing components, or force with --rebuild)
HOST_REQUIRED_FILES=(
    "LocalExile.ClientHost.dll"
    "version.dll"
    "hostfxr.dll"
    "coreclr.dll"
    "LocalExile.Client.Host.dll"
    "LocalExile.Client.Host.runtimeconfig.json"
    "addresses/3.29.3d.json"
)
ASSET_REQUIRED_FILES=(
    "Data/MtxTypes.datc64"
    "Data/MtxTypeGameSpecific.datc64"
    "Data/Pet.datc64"
    "Audio/Mods/Maxwell/stock_market.ogg"
)

host_bundle_ready() {
    for req in "${HOST_REQUIRED_FILES[@]}"; do
        [[ -f "${DIST_DIR}/${req}" ]] || return 1
    done
    return 0
}

maxwell_assets_ready() {
    for req in "${ASSET_REQUIRED_FILES[@]}"; do
        [[ -f "${ASSETS_DIR}/${req}" ]] || return 1
    done
    return 0
}

maxwell_mods_ready() {
    [[ -f "${SERVER_MOD_DIR}/maxwell.dll" ]] \
        && [[ -f "${SERVER_MOD_DIR}/MaxwellLocalExileEntry.cs" ]] \
        && [[ -f "${CLIENT_MOD_DIR}/maxwell.Client.dll" ]]
}

NEED_BUILD_WIN64=0
NEED_BUILD_ASSETS=0
NEED_BUILD_MODS=0

if [[ "${REBUILD}" -eq 1 ]] || ! host_bundle_ready; then
    NEED_BUILD_WIN64=1
fi
if [[ "${REBUILD}" -eq 1 ]] || ! maxwell_assets_ready; then
    NEED_BUILD_ASSETS=1
fi
if [[ "${REBUILD}" -eq 1 ]] || ! maxwell_mods_ready; then
    NEED_BUILD_MODS=1
fi

if [[ "${NEED_BUILD_WIN64}" -eq 1 && ! -x "${BUILD_WIN64_SCRIPT}" ]]; then
    echo "ERROR: Missing executable build script: ${BUILD_WIN64_SCRIPT}" >&2
    exit 1
fi
if [[ "${NEED_BUILD_ASSETS}" -eq 1 && ! -x "${BUILD_ASSETS_SCRIPT}" ]]; then
    echo "ERROR: Missing executable build script: ${BUILD_ASSETS_SCRIPT}" >&2
    exit 1
fi
if [[ "${NEED_BUILD_MODS}" -eq 1 && ( ! -f "${SERVER_CSPROJ}" || ! -f "${CLIENT_CSPROJ}" ) ]]; then
    echo "ERROR: Missing DualMod.MaxwellPet project files under ${REPO_ROOT}/examples/DualMod.MaxwellPet" >&2
    exit 1
fi

to_wine_path() {
    printf 'Z:%s' "${1//\//\\}"
}

RUN_ID="$(date +%Y%m%d-%H%M%S)-${MODE}-$$"
STAGE_DIR="${LOCALEXILE_DIR}/data/nativemods-desktop-${RUN_ID}"
RUNNER_SCRIPT="${STAGE_DIR}/desktop-client-runner.sh"
SHIM_LOG_LINUX="${STAGE_DIR}/nativemods-shim.log"
LOG_DIR="${REPO_ROOT}/build/desktop-logs"
RUN_LOG_DIR="${LOG_DIR}/${RUN_ID}"

HOST_DIR_WINE="$(to_wine_path "${STAGE_DIR}/host")"
ADDR_DIR_WINE="$(to_wine_path "${STAGE_DIR}/host/addresses")"
MODS_DIR_WINE="$(to_wine_path "${STAGE_DIR}/host/Mods/Client")"
OVERLAY_DIR_WINE="$(to_wine_path "${STAGE_DIR}/overlay")"
LOG_PATH_WINE="$(to_wine_path "${SHIM_LOG_LINUX}")"

# 3. Dry-run mode: validate everything, display full staging & launch plan, and exit 0 with zero side effects
if [[ "${DRY_RUN}" -eq 1 ]]; then
    echo "==> NativeMods Interactive Desktop Launcher [DRY RUN]"
    echo "    Mode             : ${MODE}"
    echo "    Desktop session  : XDG_CURRENT_DESKTOP=${XDG_CURRENT_DESKTOP:-<unset>} WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-<unset>} DISPLAY=${DISPLAY:-<unset>} (preserved)"
    echo "    Audio sink       : default desktop sink (PULSE_SINK=${PULSE_SINK:-<system default>}, preserved)"
    echo "    LocalExile dir   : ${LOCALEXILE_DIR}"
    echo "    Client dir       : ${CLIENT_DIR}"
    echo "    Staging dir      : ${STAGE_DIR} (inside LocalExile/data for steamrt4 visibility)"
    echo "    Shim log (live)  : ${SHIM_LOG_LINUX}"
    echo "    Saved log dir    : ${RUN_LOG_DIR}"
    if [[ "${KEEP_SAVE}" -eq 1 ]]; then
        echo "    Save DB handling : KEEP changes to ${LOCALEXILE_DIR}/data/characters.db* (--keep-save)"
    else
        echo "    Save DB handling : SNAPSHOT & RESTORE ${LOCALEXILE_DIR}/data/characters.db* on exit (default)"
    fi
    echo "    Intro video      : $([[ "${SHOW_LOGO}" -eq 1 ]] && echo 'ENABLED (--show-logo)' || echo 'SKIPPED (--nologo default; pass --show-logo to play GGG intro)')"
    echo "    File tracing     : $([[ "${TRACE_FILES}" -eq 1 ]] && echo 'ON (NATIVEMODS_TRACE_FILES=1)' || echo 'OFF')"
    echo ""
    echo "==> Build status:"
    echo "    Win64 extender   : $([[ "${NEED_BUILD_WIN64}" -eq 1 ]] && echo "WOULD BUILD (${BUILD_WIN64_SCRIPT})" || echo "READY (${DIST_DIR})")"
    echo "    Maxwell assets   : $([[ "${NEED_BUILD_ASSETS}" -eq 1 ]] && echo "WOULD BUILD (${BUILD_ASSETS_SCRIPT} --client ${CLIENT_DIR} --out ${ASSETS_DIR})" || echo "READY (${ASSETS_DIR})")"
    echo "    DualMod.Maxwell  : $([[ "${NEED_BUILD_MODS}" -eq 1 ]] && echo "WOULD BUILD (Server + Client, Release)" || echo "READY (${SERVER_MOD_DIR}, ${CLIENT_MOD_DIR})")"
    echo ""
    echo "==> Staging plan (no files written during --dry-run):"
    echo "    1. Copy ${DIST_DIR} -> ${STAGE_DIR}/host"
    echo "    2. Copy ${ASSETS_DIR} -> ${STAGE_DIR}/overlay"
    echo "    3. Stage Maxwell client mod -> ${STAGE_DIR}/host/Mods/Client/maxwell"
    echo "    4. Stage Maxwell server mod -> ${STAGE_DIR}/server-mods/maxwell"
    if [[ "${MODE}" == "proxy" ]]; then
        echo "    5. Copy LocalExile.ClientHost.dll + version.dll -> ${CLIENT_DIR}/ (removed on exit)"
    else
        echo "    5. Copy LocalExile.ClientHost.dll -> ${CLIENT_DIR}/, back up Game.patched.exe, and apply .nmimp import patch (restored on exit)"
    fi
    echo ""
    echo "==> Runner script preview (${RUNNER_SCRIPT}):"
    echo "    export NATIVEMODS_HOST_DIR='${HOST_DIR_WINE}'"
    echo "    export NATIVEMODS_ADDRESSES_DIR='${ADDR_DIR_WINE}'"
    echo "    export NATIVEMODS_MODS_DIR='${MODS_DIR_WINE}'"
    echo "    export NATIVEMODS_OVERLAY_DIR='${OVERLAY_DIR_WINE}'"
    echo "    export NATIVEMODS_MAXWELL_ASSETS_DIR='${OVERLAY_DIR_WINE}'"
    echo "    export NATIVEMODS_LOG_PATH='${LOG_PATH_WINE}'"
    if [[ "${TRACE_FILES}" -eq 1 ]]; then
        echo "    export NATIVEMODS_TRACE_FILES=1"
    fi
    if [[ "${MODE}" == "proxy" ]]; then
        echo '    export WINEDLLOVERRIDES="${WINEDLLOVERRIDES:+${WINEDLLOVERRIDES};}version=n,b"'
    fi
    if [[ "${SHOW_LOGO}" -eq 1 ]]; then
        echo '    exec umu-run "$@" --disable-assert-box'
    else
        echo '    exec umu-run "$@" --nologo --disable-assert-box'
    fi
    echo ""
    echo "==> Exact launch command:"
    printf "    (cd %q && Game_MODS_FOLDER=%q Game_CLIENT_RUNNER=%q pwsh -NoProfile -File %q -NoBuild -NoExtract)\n" \
        "${LOCALEXILE_DIR}" "${STAGE_DIR}/server-mods" "${RUNNER_SCRIPT}" "${LIVE_PS1}"
    echo ""
    echo "==> Dry run passed: all tools, paths, and plans validated; zero modifications made to ${LOCALEXILE_DIR}."
    exit 0
fi

# Soft desktop check for live runs (do not block if user exports DISPLAY/WAYLAND inside a wrapper)
if [[ -z "${WAYLAND_DISPLAY:-}" && -z "${DISPLAY:-}" ]]; then
    echo "WARN: Neither WAYLAND_DISPLAY nor DISPLAY is set; Proton may fail to open a desktop window." >&2
fi

# 4. Build missing components (or rebuild all if --rebuild)
if [[ "${NEED_BUILD_WIN64}" -eq 1 ]]; then
    echo "==> Building Win64 NativeMods extender via scripts/build-win64.sh ..."
    "${BUILD_WIN64_SCRIPT}"
fi
if [[ "${NEED_BUILD_ASSETS}" -eq 1 ]]; then
    echo "==> Building Maxwell 3.29.3d assets via scripts/build-maxwell-assets.sh ..."
    "${BUILD_ASSETS_SCRIPT}" --client "${CLIENT_DIR}" --out "${ASSETS_DIR}"
fi
if [[ "${NEED_BUILD_MODS}" -eq 1 ]]; then
    echo "==> Building DualMod.MaxwellPet (Server + Client, Release)..."
    dotnet build "${SERVER_CSPROJ}" -c Release --nologo -v minimal
    dotnet build "${CLIENT_CSPROJ}" -c Release --nologo -v minimal
fi

# Re-verify all built artifacts before touching LocalExile/
if ! host_bundle_ready; then
    echo "ERROR: Win64 host bundle under ${DIST_DIR} is still incomplete after build." >&2
    exit 1
fi
if ! maxwell_assets_ready; then
    echo "ERROR: Maxwell asset overlay under ${ASSETS_DIR} is still incomplete after build." >&2
    exit 1
fi
if ! maxwell_mods_ready; then
    echo "ERROR: DualMod.MaxwellPet Release binaries missing after build (${SERVER_MOD_DIR}, ${CLIENT_MOD_DIR})." >&2
    exit 1
fi

DIST_DIR="$(cd "${DIST_DIR}" && pwd)"
ASSETS_DIR="$(cd "${ASSETS_DIR}" && pwd)"

# 5. Record baseline integrity snapshots & arm guaranteed cleanup trap
GAME_EXE_SHA_BEFORE="$(sha256sum "${GAME_EXE}" | awk '{print $1}')"
GAME_PATCHED_SHA_BEFORE="$(sha256sum "${GAME_PATCHED_EXE}" | awk '{print $1}')"
MODS_SNAPSHOT_BEFORE="$(find "${LOCALEXILE_DIR}/Mods" -maxdepth 4 -printf '%P %y\n' 2>/dev/null | sort || true)"

mkdir -p "${RUN_LOG_DIR}"

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
STAGED_CLIENT_FILES=()
LIVE_STARTED=0
CLEANED_UP=0

on_signal() {
    echo ""
    echo "==> Ctrl+C / termination signal received; tearing down cleanly..."
    exit 0
}

cleanup() {
    local exit_code=$?
    if [[ "${CLEANED_UP}" -eq 1 ]]; then
        return
    fi
    CLEANED_UP=1
    trap '' INT TERM
    set +e

    echo ""
    echo "==> Stopping LocalExile + Proton client and restoring ${LOCALEXILE_DIR}..."
    if [[ "${LIVE_STARTED}" -eq 1 ]]; then
        (cd "${LOCALEXILE_DIR}" && pwsh -NoProfile -File "${LIVE_PS1}" -Stop) >/dev/null 2>&1 || true

        for _ in $(seq 1 20); do
            if ! pgrep -f "${GAME_PATCHED_EXE}" >/dev/null 2>&1; then
                break
            fi
            sleep 0.25
        done
        pkill -TERM -f "${GAME_PATCHED_EXE}" >/dev/null 2>&1 || true
        sleep 0.5
    fi

    # Preserve shim log before removing staging directory
    if [[ -f "${SHIM_LOG_LINUX}" ]]; then
        cp -f "${SHIM_LOG_LINUX}" "${RUN_LOG_DIR}/nativemods-shim.log" 2>/dev/null || true
    fi

    # Remove staged DLLs from Client/
    for f in "${STAGED_CLIENT_FILES[@]}"; do
        rm -f "${f}" 2>/dev/null || true
    done
    rm -f "${CLIENT_DIR}/nativemods-shim.log" 2>/dev/null || true

    # Restore Game.patched.exe if import-patch mode modified it
    if [[ -n "${BACKUP_EXE}" && -f "${BACKUP_EXE}" ]]; then
        cp -f "${BACKUP_EXE}" "${GAME_PATCHED_EXE}"
    fi

    # Restore characters.db* snapshot unless --keep-save was passed
    if [[ "${KEEP_SAVE}" -eq 0 && -n "${CHAR_DB_BACKUP_DIR}" && -d "${CHAR_DB_BACKUP_DIR}" ]]; then
        rm -f "${LOCALEXILE_DIR}/data/characters.db-wal" "${LOCALEXILE_DIR}/data/characters.db-shm" 2>/dev/null || true
        for dbf in "${CHAR_DB_BACKUP_DIR}"/characters.db*; do
            if [[ -f "${dbf}" ]]; then
                cp -a "${dbf}" "${LOCALEXILE_DIR}/data/" 2>/dev/null || true
            fi
        done
        echo "    Restored data/characters.db* snapshot (pass --keep-save to persist character DB changes)."
    elif [[ "${KEEP_SAVE}" -eq 1 ]]; then
        echo "    Kept data/characters.db* changes (--keep-save)."
    fi

    # Remove staging directory inside LocalExile/data/
    rm -rf "${STAGE_DIR}" 2>/dev/null || true

    # Verify Game.exe, Game.patched.exe, Client/, and Mods/ integrity
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
        echo "==> Clean teardown verified: Game.exe & Game.patched.exe SHA-256 identical; zero stray files."
    else
        exit_code=1
    fi
    echo "    Session logs saved under: ${RUN_LOG_DIR}"
    exit "${exit_code}"
}

trap on_signal INT TERM
trap cleanup EXIT

# 6. Stage host, overlay, client mod, and server mod inside LocalExile/data/ (visible in steamrt4)
echo "==> Staging NativeMods + DualMod.MaxwellPet into ${STAGE_DIR} ..."
mkdir -p "${STAGE_DIR}/host" "${STAGE_DIR}/overlay" "${STAGE_DIR}/server-mods/maxwell"
cp -a "${DIST_DIR}/." "${STAGE_DIR}/host/"
cp -a "${ASSETS_DIR}/." "${STAGE_DIR}/overlay/"

MAXWELL_CLIENT_STAGE="${STAGE_DIR}/host/Mods/Client/maxwell"
mkdir -p "${MAXWELL_CLIENT_STAGE}"
cp -a "${CLIENT_MOD_DIR}/." "${MAXWELL_CLIENT_STAGE}/"
if [[ -f "${REPO_ROOT}/examples/DualMod.MaxwellPet/mod.json" ]]; then
    cp -f "${REPO_ROOT}/examples/DualMod.MaxwellPet/mod.json" "${MAXWELL_CLIENT_STAGE}/mod.json"
fi
if [[ -d "${REPO_ROOT}/examples/DualMod.MaxwellPet/client" ]]; then
    mkdir -p "${MAXWELL_CLIENT_STAGE}/client"
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

MAXWELL_SERVER_STAGE="${STAGE_DIR}/server-mods/maxwell"
cp -a "${SERVER_MOD_DIR}/." "${MAXWELL_SERVER_STAGE}/"
for meta in mod.json settings.json; do
    if [[ -f "${REPO_ROOT}/examples/DualMod.MaxwellPet/${meta}" && ! -f "${MAXWELL_SERVER_STAGE}/${meta}" ]]; then
        cp -f "${REPO_ROOT}/examples/DualMod.MaxwellPet/${meta}" "${MAXWELL_SERVER_STAGE}/${meta}"
    fi
done

: > "${SHIM_LOG_LINUX}"

# Stage native shim DLL(s) into Client/
cp "${STAGE_DIR}/host/LocalExile.ClientHost.dll" "${CLIENT_DIR}/LocalExile.ClientHost.dll"
STAGED_CLIENT_FILES+=("${CLIENT_DIR}/LocalExile.ClientHost.dll")

if [[ "${MODE}" == "proxy" ]]; then
    cp "${STAGE_DIR}/host/version.dll" "${CLIENT_DIR}/version.dll"
    STAGED_CLIENT_FILES+=("${CLIENT_DIR}/version.dll")
else
    BACKUP_EXE="${STAGE_DIR}/Game.patched.exe.backup"
    cp -a "${GAME_PATCHED_EXE}" "${BACKUP_EXE}"
    echo "==> Applying PE32+ .nmimp import patch to Game.patched.exe (exact backup will be restored on exit)..."
    dotnet run --project "${PATCHER_CSPROJ}" -c Release -- \
        patch "${GAME_PATCHED_EXE}" --allow-synthetic --no-backup \
        >"${RUN_LOG_DIR}/patcher.log" 2>&1
    dotnet run --project "${PATCHER_CSPROJ}" -c Release -- \
        verify "${GAME_PATCHED_EXE}" --allow-synthetic \
        >>"${RUN_LOG_DIR}/patcher.log" 2>&1
    cat "${RUN_LOG_DIR}/patcher.log"
fi

# 7. Generate desktop-native Game_CLIENT_RUNNER (preserves Hyprland/Wayland/X11 + real audio sink)
{
    printf '%s\n' '#!/usr/bin/env bash'
    printf '%s\n' '# Auto-generated desktop-native Proton runner for LocalExile + NativeMods + DualMod.MaxwellPet'
    printf '%s\n' 'set -euo pipefail'
    printf '\n'
    printf 'export NATIVEMODS_HOST_DIR=%q\n' "${HOST_DIR_WINE}"
    printf 'export NATIVEMODS_ADDRESSES_DIR=%q\n' "${ADDR_DIR_WINE}"
    printf 'export NATIVEMODS_MODS_DIR=%q\n' "${MODS_DIR_WINE}"
    printf 'export NATIVEMODS_OVERLAY_DIR=%q\n' "${OVERLAY_DIR_WINE}"
    printf 'export NATIVEMODS_MAXWELL_ASSETS_DIR=%q\n' "${OVERLAY_DIR_WINE}"
    printf 'export NATIVEMODS_LOG_PATH=%q\n' "${LOG_PATH_WINE}"
    if [[ "${TRACE_FILES}" -eq 1 ]]; then
        printf 'export NATIVEMODS_TRACE_FILES=1\n'
    fi
    if [[ "${MODE}" == "proxy" ]]; then
        printf '%s\n' 'export WINEDLLOVERRIDES="${WINEDLLOVERRIDES:+${WINEDLLOVERRIDES};}version=n,b"'
    fi
    if [[ "${SHOW_LOGO}" -eq 1 ]]; then
        printf '%s\n' 'exec umu-run "$@" --disable-assert-box'
    else
        printf '%s\n' 'exec umu-run "$@" --nologo --disable-assert-box'
    fi
} > "${RUNNER_SCRIPT}"
chmod +x "${RUNNER_SCRIPT}"

# 8. Launch LocalExile + Proton client via scripts/dev/live.ps1
echo "==> Launching LocalExile Gateway + Path of Exile 3.29.3d on your live desktop (mode=${MODE})..."
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
        CLIENT_PID="$(python3 - "${LOCALEXILE_DIR}/data/clients/default.json" <<'PY' 2>/dev/null || true
import json, sys
print(json.load(open(sys.argv[1]))["pid"])
PY
)"
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

# 9. Print interactive cheat sheet
echo ""
echo "============================================================================"
echo "  LocalExile + NativeMods + DualMod.MaxwellPet is LIVE on your desktop!"
echo "============================================================================"
echo "  Client PID : ${CLIENT_PID} (mode=${MODE})"
if [[ "${KEEP_SAVE}" -eq 1 ]]; then
    echo "  Save DB    : KEEP changes to data/characters.db* on exit (--keep-save)"
else
    echo "  Save DB    : AUTO-RESTORE data/characters.db* on exit (pass --keep-save to keep)"
fi
echo ""
echo "  In-Game Quick Controls:"
echo "    - Cosmetics menu : Press K -> Pet Slot 1 -> Maxwell the Cat Pet"
echo "    - Control panel  : /maxwell panel   (or /maxwell)"
echo "    - Chat commands  : /maxwell summon | dismiss | dance | spin <0..720> | float <0..60>"
echo "                       /maxwell music <on|off|mute|unmute|vol 0..1>"
echo ""
echo "  Live Diagnostics:"
echo "    tail -f ${SHIM_LOG_LINUX}"
echo ""
echo "  Close Path of Exile normally or press Ctrl+C here for automatic clean teardown."
echo "============================================================================"
echo ""

# 10. Wait until Game.patched.exe exits or user presses Ctrl+C
while kill -0 "${CLIENT_PID}" 2>/dev/null || pgrep -f "${GAME_PATCHED_EXE}" >/dev/null 2>&1; do
    sleep 1 &
    wait "$!" || true
done

echo "==> Path of Exile client exited."
