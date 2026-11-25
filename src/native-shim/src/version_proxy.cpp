// Win64 proxy-DLL fallback (version.dll).
// Forwards all 16 standard version.dll exports lazily to %SystemRoot%\System32\version.dll
// (zero LoadLibrary calls under the Windows loader lock) and spawns a worker thread on
// DLL_PROCESS_ATTACH to load LocalExile.ClientHost.dll beside this proxy DLL.
// Under Wine / Proton, launch with: WINEDLLOVERRIDES="version=n,b"

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  include <cstdint>

namespace {

INIT_ONCE g_real_version_once = INIT_ONCE_STATIC_INIT;
HMODULE   g_real_version      = nullptr;
HMODULE   g_client_host       = nullptr;
wchar_t   g_proxy_dir[MAX_PATH] = {};

BOOL CALLBACK InitRealVersionCallback(PINIT_ONCE, PVOID, PVOID*) {
    wchar_t sys_dir[MAX_PATH] = {};
    const UINT n = GetSystemDirectoryW(sys_dir, MAX_PATH);
    if (n == 0 || n + 16 >= MAX_PATH) {
        return TRUE;
    }
    lstrcatW(sys_dir, L"\\version.dll");
    g_real_version = LoadLibraryW(sys_dir);
    return TRUE;
}

HMODULE get_real_version() {
    InitOnceExecuteOnce(&g_real_version_once, InitRealVersionCallback, nullptr, nullptr);
    return g_real_version;
}

template <typename Fn>
Fn resolve_real_export(const char* name) {
    HMODULE mod = get_real_version();
    if (!mod) return nullptr;
    return reinterpret_cast<Fn>(reinterpret_cast<uintptr_t>(GetProcAddress(mod, name)));
}

DWORD WINAPI LoadHostThread(LPVOID) {
    if (g_proxy_dir[0] != L'\0') {
        wchar_t host_path[MAX_PATH * 2] = {};
        lstrcpynW(host_path, g_proxy_dir, MAX_PATH);
        lstrcatW(host_path, L"\\LocalExile.ClientHost.dll");
        g_client_host = LoadLibraryW(host_path);
    }
    if (!g_client_host) {
        g_client_host = LoadLibraryW(L"LocalExile.ClientHost.dll");
    }
    return 0;
}

} // namespace

extern "C" {

BOOL WINAPI GetFileVersionInfoA(LPCSTR lptstrFilename, DWORD dwHandle, DWORD dwLen, LPVOID lpData) {
    using Fn = BOOL(WINAPI*)(LPCSTR, DWORD, DWORD, LPVOID);
    if (auto fn = resolve_real_export<Fn>("GetFileVersionInfoA")) {
        return fn(lptstrFilename, dwHandle, dwLen, lpData);
    }
    SetLastError(ERROR_PROC_NOT_FOUND);
    return FALSE;
}

DWORD WINAPI GetFileVersionInfoByHandle(DWORD dwFlags, HANDLE hFile, LPVOID* lplpData, PDWORD pdwLen) {
    using Fn = DWORD(WINAPI*)(DWORD, HANDLE, LPVOID*, PDWORD);
    if (auto fn = resolve_real_export<Fn>("GetFileVersionInfoByHandle")) {
        return fn(dwFlags, hFile, lplpData, pdwLen);
    }
    SetLastError(ERROR_PROC_NOT_FOUND);
    return 0;
}

BOOL WINAPI GetFileVersionInfoExA(DWORD dwFlags, LPCSTR lpwstrFilename, DWORD dwHandle, DWORD dwLen, LPVOID lpData) {
    using Fn = BOOL(WINAPI*)(DWORD, LPCSTR, DWORD, DWORD, LPVOID);
    if (auto fn = resolve_real_export<Fn>("GetFileVersionInfoExA")) {
        return fn(dwFlags, lpwstrFilename, dwHandle, dwLen, lpData);
    }
    SetLastError(ERROR_PROC_NOT_FOUND);
    return FALSE;
}

BOOL WINAPI GetFileVersionInfoExW(DWORD dwFlags, LPCWSTR lpwstrFilename, DWORD dwHandle, DWORD dwLen, LPVOID lpData) {
    using Fn = BOOL(WINAPI*)(DWORD, LPCWSTR, DWORD, DWORD, LPVOID);
    if (auto fn = resolve_real_export<Fn>("GetFileVersionInfoExW")) {
        return fn(dwFlags, lpwstrFilename, dwHandle, dwLen, lpData);
    }
    SetLastError(ERROR_PROC_NOT_FOUND);
    return FALSE;
}

DWORD WINAPI GetFileVersionInfoSizeA(LPCSTR lptstrFilename, LPDWORD lpdwHandle) {
    using Fn = DWORD(WINAPI*)(LPCSTR, LPDWORD);
    if (auto fn = resolve_real_export<Fn>("GetFileVersionInfoSizeA")) {
        return fn(lptstrFilename, lpdwHandle);
    }
    SetLastError(ERROR_PROC_NOT_FOUND);
    return 0;
}

DWORD WINAPI GetFileVersionInfoSizeExA(DWORD dwFlags, LPCSTR lpwstrFilename, LPDWORD lpdwHandle) {
    using Fn = DWORD(WINAPI*)(DWORD, LPCSTR, LPDWORD);
    if (auto fn = resolve_real_export<Fn>("GetFileVersionInfoSizeExA")) {
        return fn(dwFlags, lpwstrFilename, lpdwHandle);
    }
    SetLastError(ERROR_PROC_NOT_FOUND);
    return 0;
}

DWORD WINAPI GetFileVersionInfoSizeExW(DWORD dwFlags, LPCWSTR lpwstrFilename, LPDWORD lpdwHandle) {
    using Fn = DWORD(WINAPI*)(DWORD, LPCWSTR, LPDWORD);
    if (auto fn = resolve_real_export<Fn>("GetFileVersionInfoSizeExW")) {
        return fn(dwFlags, lpwstrFilename, lpdwHandle);
    }
    SetLastError(ERROR_PROC_NOT_FOUND);
    return 0;
}

DWORD WINAPI GetFileVersionInfoSizeW(LPCWSTR lptstrFilename, LPDWORD lpdwHandle) {
    using Fn = DWORD(WINAPI*)(LPCWSTR, LPDWORD);
    if (auto fn = resolve_real_export<Fn>("GetFileVersionInfoSizeW")) {
        return fn(lptstrFilename, lpdwHandle);
    }
    SetLastError(ERROR_PROC_NOT_FOUND);
    return 0;
}

DWORD WINAPI VerFindFileA(
    DWORD uFlags, LPSTR szFileName, LPSTR szWinDir, LPSTR szAppDir,
    LPSTR szCurDir, PUINT puCurDirLen, LPSTR szDestDir, PUINT puDestDirLen) {
    using Fn = DWORD(WINAPI*)(DWORD, LPSTR, LPSTR, LPSTR, LPSTR, PUINT, LPSTR, PUINT);
    if (auto fn = resolve_real_export<Fn>("VerFindFileA")) {
        return fn(uFlags, szFileName, szWinDir, szAppDir, szCurDir, puCurDirLen, szDestDir, puDestDirLen);
    }
    return 0;
}

DWORD WINAPI VerFindFileW(
    DWORD uFlags, LPWSTR szFileName, LPWSTR szWinDir, LPWSTR szAppDir,
    LPWSTR szCurDir, PUINT puCurDirLen, LPWSTR szDestDir, PUINT puDestDirLen) {
    using Fn = DWORD(WINAPI*)(DWORD, LPWSTR, LPWSTR, LPWSTR, LPWSTR, PUINT, LPWSTR, PUINT);
    if (auto fn = resolve_real_export<Fn>("VerFindFileW")) {
        return fn(uFlags, szFileName, szWinDir, szAppDir, szCurDir, puCurDirLen, szDestDir, puDestDirLen);
    }
    return 0;
}

DWORD WINAPI VerInstallFileA(
    DWORD uFlags, LPSTR szSrcFileName, LPSTR szDestFileName, LPSTR szSrcDir,
    LPSTR szDestDir, LPSTR szCurDir, LPSTR szTmpFile, PUINT puTmpFileLen) {
    using Fn = DWORD(WINAPI*)(DWORD, LPSTR, LPSTR, LPSTR, LPSTR, LPSTR, LPSTR, PUINT);
    if (auto fn = resolve_real_export<Fn>("VerInstallFileA")) {
        return fn(uFlags, szSrcFileName, szDestFileName, szSrcDir, szDestDir, szCurDir, szTmpFile, puTmpFileLen);
    }
    return 0;
}

DWORD WINAPI VerInstallFileW(
    DWORD uFlags, LPWSTR szSrcFileName, LPWSTR szDestFileName, LPWSTR szSrcDir,
    LPWSTR szDestDir, LPWSTR szCurDir, LPWSTR szTmpFile, PUINT puTmpFileLen) {
    using Fn = DWORD(WINAPI*)(DWORD, LPWSTR, LPWSTR, LPWSTR, LPWSTR, LPWSTR, LPWSTR, PUINT);
    if (auto fn = resolve_real_export<Fn>("VerInstallFileW")) {
        return fn(uFlags, szSrcFileName, szDestFileName, szSrcDir, szDestDir, szCurDir, szTmpFile, puTmpFileLen);
    }
    return 0;
}

DWORD WINAPI VerLanguageNameA(DWORD wLang, LPSTR szLang, DWORD cchLang) {
    using Fn = DWORD(WINAPI*)(DWORD, LPSTR, DWORD);
    if (auto fn = resolve_real_export<Fn>("VerLanguageNameA")) {
        return fn(wLang, szLang, cchLang);
    }
    return 0;
}

DWORD WINAPI VerLanguageNameW(DWORD wLang, LPWSTR szLang, DWORD cchLang) {
    using Fn = DWORD(WINAPI*)(DWORD, LPWSTR, DWORD);
    if (auto fn = resolve_real_export<Fn>("VerLanguageNameW")) {
        return fn(wLang, szLang, cchLang);
    }
    return 0;
}

BOOL WINAPI VerQueryValueA(LPCVOID pBlock, LPCSTR lpSubBlock, LPVOID* lplpBuffer, PUINT puLen) {
    using Fn = BOOL(WINAPI*)(LPCVOID, LPCSTR, LPVOID*, PUINT);
    if (auto fn = resolve_real_export<Fn>("VerQueryValueA")) {
        return fn(pBlock, lpSubBlock, lplpBuffer, puLen);
    }
    return FALSE;
}

BOOL WINAPI VerQueryValueW(LPCVOID pBlock, LPCWSTR lpSubBlock, LPVOID* lplpBuffer, PUINT puLen) {
    using Fn = BOOL(WINAPI*)(LPCVOID, LPCWSTR, LPVOID*, PUINT);
    if (auto fn = resolve_real_export<Fn>("VerQueryValueW")) {
        return fn(pBlock, lpSubBlock, lplpBuffer, puLen);
    }
    return FALSE;
}

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hinst);
        // Capture our own directory safely under loader lock (never call LoadLibrary here!).
        const DWORD len = GetModuleFileNameW(hinst, g_proxy_dir, MAX_PATH);
        if (len > 0 && len < MAX_PATH) {
            for (DWORD i = len; i > 0; --i) {
                if (g_proxy_dir[i - 1] == L'\\' || g_proxy_dir[i - 1] == L'/') {
                    g_proxy_dir[i - 1] = L'\0';
                    break;
                }
            }
        }
        HANDLE th = CreateThread(nullptr, 0, LoadHostThread, nullptr, 0, nullptr);
        if (th) CloseHandle(th);
    } else if (reason == DLL_PROCESS_DETACH && reserved == nullptr) {
        // Only FreeLibrary on explicit dynamic unload; never during process termination.
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

} // extern "C"
#endif
