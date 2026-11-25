// Optional Win64 proxy-DLL fallback (version.dll).
// When the player deploys NativeMods in --proxy-dll mode instead of PE32+ import patching,
// this lightweight proxy forwards standard VERSION.dll APIs to %SystemRoot%\System32\version.dll
// and loads LocalExile.ClientHost.dll beside the client executable.
// Under Wine / Proton, launch with: WINEDLLOVERRIDES="version=n,b"

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>

namespace {

HMODULE g_real_version = nullptr;
HMODULE g_client_host  = nullptr;

HMODULE load_real_version() {
    if (g_real_version) return g_real_version;
    wchar_t sys_dir[MAX_PATH] = {};
    const UINT n = GetSystemDirectoryW(sys_dir, MAX_PATH);
    if (n == 0 || n + 16 >= MAX_PATH) return nullptr;
    lstrcatW(sys_dir, L"\\version.dll");
    g_real_version = LoadLibraryW(sys_dir);
    return g_real_version;
}

DWORD WINAPI LoadHostThread(LPVOID) {
    g_client_host = LoadLibraryW(L"LocalExile.ClientHost.dll");
    return 0;
}

} // namespace

#  if defined(_MSC_VER)
#    pragma comment(linker, "/export:GetFileVersionInfoA=C:\\Windows\\System32\\version.GetFileVersionInfoA")
#    pragma comment(linker, "/export:GetFileVersionInfoW=C:\\Windows\\System32\\version.GetFileVersionInfoW")
#    pragma comment(linker, "/export:GetFileVersionInfoSizeA=C:\\Windows\\System32\\version.GetFileVersionInfoSizeA")
#    pragma comment(linker, "/export:GetFileVersionInfoSizeW=C:\\Windows\\System32\\version.GetFileVersionInfoSizeW")
#    pragma comment(linker, "/export:VerQueryValueA=C:\\Windows\\System32\\version.VerQueryValueA")
#    pragma comment(linker, "/export:VerQueryValueW=C:\\Windows\\System32\\version.VerQueryValueW")
#  endif

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hinst);
        (void)load_real_version();
        HANDLE th = CreateThread(nullptr, 0, LoadHostThread, nullptr, 0, nullptr);
        if (th) CloseHandle(th);
    } else if (reason == DLL_PROCESS_DETACH) {
        if (g_client_host) {
            FreeLibrary(g_client_host);
            g_client_host = nullptr;
        }
        if (g_real_version) {
            FreeLibrary(g_real_version);
            g_real_version = nullptr;
        }
    }
    return TRUE;
}
#endif
