#!/usr/bin/env bash
# Reproducible Win64 cross-compilation and .NET 10 self-contained staging script
# for the LocalExile NativeMods Script Extender.
#
# Outputs into: <repo>/dist/win-x64/
#   - LocalExile.ClientHost.dll
#   - version.dll
#   - r1_wine_smoke.exe
#   - hostfxr.dll, coreclr.dll, LocalExile.Client.Host.dll, LocalExile.Client.Host.runtimeconfig.json, ...
#   - addresses/{3.29.3d.json,3.29.3c.json}
#   - Mods/Client/harvest-garden/{mod.json,harvest-garden.Client.dll,client/...}

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NATIVE_SRC="${REPO_ROOT}/src/native-shim"
NATIVE_BUILD="${REPO_ROOT}/build/native-shim-win64"
STAGE_DIR="${REPO_ROOT}/dist/win-x64"

echo "==> [1/5] Cross-compiling Win64 native shim (MinGW-w64 x86_64)..."
cmake -S "${NATIVE_SRC}" -B "${NATIVE_BUILD}" \
    -DCMAKE_TOOLCHAIN_FILE="${NATIVE_SRC}/cmake/mingw-w64-x86_64.cmake" \
    -DCMAKE_BUILD_TYPE=Release \
    -DBUILD_TESTING=ON
cmake --build "${NATIVE_BUILD}" --parallel "$(nproc)"

echo "==> [2/5] Publishing self-contained win-x64 .NET 10 managed host..."
rm -rf "${STAGE_DIR}"
mkdir -p "${STAGE_DIR}"
dotnet publish "${REPO_ROOT}/src/LocalExile.Client.Host/LocalExile.Client.Host.csproj" \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -o "${STAGE_DIR}"

echo "==> [3/5] Staging Win64 native binaries, address libraries, and HarvestGarden client mod..."
cp "${NATIVE_BUILD}/LocalExile.ClientHost.dll" "${STAGE_DIR}/LocalExile.ClientHost.dll"
cp "${NATIVE_BUILD}/version.dll" "${STAGE_DIR}/version.dll"
cp "${NATIVE_BUILD}/r1_wine_smoke.exe" "${STAGE_DIR}/r1_wine_smoke.exe"

mkdir -p "${STAGE_DIR}/addresses"
cp "${REPO_ROOT}/data/addresses/3.29.3d.json" "${STAGE_DIR}/addresses/3.29.3d.json"
cp "${REPO_ROOT}/data/addresses/3.29.3c.json" "${STAGE_DIR}/addresses/3.29.3c.json"

dotnet build "${REPO_ROOT}/examples/DualMod.HarvestGarden/Client/DualMod.HarvestGarden.Client.csproj" -c Release
GARDEN_MOD_DIR="${STAGE_DIR}/Mods/Client/harvest-garden"
mkdir -p "${GARDEN_MOD_DIR}"
cp "${REPO_ROOT}/examples/DualMod.HarvestGarden/mod.json" "${GARDEN_MOD_DIR}/mod.json"
cp "${REPO_ROOT}/examples/DualMod.HarvestGarden/Client/bin/Release/net10.0/harvest-garden.Client.dll" \
   "${GARDEN_MOD_DIR}/harvest-garden.Client.dll"
cp -r "${REPO_ROOT}/examples/DualMod.HarvestGarden/client" "${GARDEN_MOD_DIR}/client"

echo "==> [4/5] Verifying staged Win64 bundle completeness..."
REQUIRED_FILES=(
    "LocalExile.ClientHost.dll"
    "version.dll"
    "r1_wine_smoke.exe"
    "hostfxr.dll"
    "coreclr.dll"
    "LocalExile.Client.Host.dll"
    "LocalExile.Client.Host.runtimeconfig.json"
    "addresses/3.29.3d.json"
    "Mods/Client/harvest-garden/mod.json"
    "Mods/Client/harvest-garden/harvest-garden.Client.dll"
)
for rel in "${REQUIRED_FILES[@]}"; do
    if [[ ! -f "${STAGE_DIR}/${rel}" ]]; then
        echo "ERROR: Missing required staged artifact: ${STAGE_DIR}/${rel}" >&2
        exit 1
    fi
done

echo "==> [5/5] Auditing Win64 PE imports & exports with x86_64-w64-mingw32-objdump -p..."
for dll in "LocalExile.ClientHost.dll" "version.dll"; do
    echo "--- ${dll} imports ---"
    IMPORTS="$(x86_64-w64-mingw32-objdump -p "${STAGE_DIR}/${dll}" | grep -E "DLL Name:" || true)"
    echo "${IMPORTS}"
    if echo "${IMPORTS}" | grep -iE "libgcc|libstdc\+\+|libwinpthread" >/dev/null; then
        echo "ERROR: ${dll} has forbidden non-system MinGW runtime DLL dependencies!" >&2
        exit 1
    fi
done

SHIM_PE="$(x86_64-w64-mingw32-objdump -p "${STAGE_DIR}/LocalExile.ClientHost.dll")"
VER_PE="$(x86_64-w64-mingw32-objdump -p "${STAGE_DIR}/version.dll")"
grep -q "LocalExileClientHost_ImportAnchor" <<< "${SHIM_PE}"
grep -q "nm_get_abi_table" <<< "${SHIM_PE}"
grep -q "GetFileVersionInfoSizeW" <<< "${VER_PE}"

echo "==> Win64 build & staging complete: ${STAGE_DIR}"
