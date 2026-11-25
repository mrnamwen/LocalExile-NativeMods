#include "live_audio.hpp"

#include "address_table.hpp"
#include "exception_wall.hpp"
#include "overlay_and_channels.hpp"
#include "shim_logger.hpp"

#include <algorithm>
#include <cstring>
#include <vector>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#endif

namespace nativemods {

namespace {

float clamp_volume(float v) {
    if (v < 0.0f) return 0.0f;
    if (v > 1.0f) return 1.0f;
    return v;
}

#if defined(_WIN32)
// Exact FMOD Core 2.03 constants verified against PoE 3.29.3d's shipped fmod.dll:
//   FMOD_System_Create checks (headerversion - 0x00020300u) <= 0xFFu
//   FMOD_System_CreateSound checks exinfo->cbsize <= 0xE0 (224 bytes) & multiple of 4
constexpr uint32_t kFmodVersion203       = 0x00020300u;
constexpr uint32_t kFmodInitNormal       = 0x00000000u;
constexpr uint32_t kFmodOutputNoSound    = 3u;
constexpr uint32_t kFmodModeLoopOff      = 0x00000001u;
constexpr uint32_t kFmodModeLoopNormal   = 0x00000002u;
constexpr uint32_t kFmodMode2D           = 0x00000008u;
constexpr uint32_t kFmodModeCreateSample = 0x00000020u;
constexpr uint32_t kFmodModeOpenMemory   = 0x00000800u;

struct FmodCreateSoundExInfo203 {
    int32_t  cbsize;
    uint32_t length;
    uint8_t  reserved[224 - 8];
};
static_assert(sizeof(FmodCreateSoundExInfo203) == 224, "FMOD 2.03 x64 FMOD_CREATESOUNDEXINFO must be 0xE0 bytes");

using FmodSystemCreateFn      = int32_t(NM_CALL*)(void** system, uint32_t header_version);
using FmodSystemSetOutputFn   = int32_t(NM_CALL*)(void* system, uint32_t output_type);
using FmodSystemInitFn        = int32_t(NM_CALL*)(void* system, int32_t max_channels, uint32_t flags, void* extra);
using FmodSystemUpdateFn      = int32_t(NM_CALL*)(void* system);
using FmodSystemReleaseFn     = int32_t(NM_CALL*)(void* system);
using FmodSystemCreateSoundFn = int32_t(NM_CALL*)(void* system, const char* name_or_data, uint32_t mode, FmodCreateSoundExInfo203* exinfo, void** out_sound);
using FmodSystemPlaySoundFn   = int32_t(NM_CALL*)(void* system, void* sound, void* channel_group, int32_t paused, void** out_channel);
using FmodSoundSetLoopCountFn = int32_t(NM_CALL*)(void* sound, int32_t loop_count);
using FmodSoundReleaseFn      = int32_t(NM_CALL*)(void* sound);
using FmodChannelSetVolumeFn  = int32_t(NM_CALL*)(void* channel, float volume);
using FmodChannelStopFn       = int32_t(NM_CALL*)(void* channel);
using FmodChannelIsPlayingFn  = int32_t(NM_CALL*)(void* channel, int32_t* out_is_playing);

struct FmodApi {
    FmodSystemCreateFn      System_Create = nullptr;
    FmodSystemSetOutputFn   System_SetOutput = nullptr;
    FmodSystemInitFn        System_Init = nullptr;
    FmodSystemUpdateFn      System_Update = nullptr;
    FmodSystemReleaseFn     System_Release = nullptr;
    FmodSystemCreateSoundFn System_CreateSound = nullptr;
    FmodSystemPlaySoundFn   System_PlaySound = nullptr;
    FmodSoundSetLoopCountFn Sound_SetLoopCount = nullptr;
    FmodSoundReleaseFn      Sound_Release = nullptr;
    FmodChannelSetVolumeFn  Channel_SetVolume = nullptr;
    FmodChannelStopFn       Channel_Stop = nullptr;
    FmodChannelIsPlayingFn  Channel_IsPlaying = nullptr;
};

FmodApi g_fmod{};

template <typename Fn>
Fn load_fmod_proc(HMODULE mod, const char* name) {
    return reinterpret_cast<Fn>(reinterpret_cast<uintptr_t>(GetProcAddress(mod, name)));
}

std::wstring utf8_to_wide_audio(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), nullptr, 0);
    if (n <= 0) return {};
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

bool read_file_bytes_win32(const std::wstring& wpath, std::vector<uint8_t>& out) {
    out.clear();
    HANDLE h = CreateFileW(
        wpath.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        return false;
    }
    LARGE_INTEGER li{};
    if (!GetFileSizeEx(h, &li) || li.QuadPart <= 0 || li.QuadPart > 256LL * 1024 * 1024) {
        CloseHandle(h);
        return false;
    }
    const size_t total = static_cast<size_t>(li.QuadPart);
    out.resize(total);
    size_t offset = 0;
    while (offset < total) {
        DWORD chunk = static_cast<DWORD>(std::min<size_t>(total - offset, 0x1000000u));
        DWORD got = 0;
        if (!ReadFile(h, out.data() + offset, chunk, &got, nullptr) || got == 0) {
            CloseHandle(h);
            out.clear();
            return false;
        }
        offset += got;
    }
    CloseHandle(h);
    return true;
}

// MSVC 2019 std::wstring layout (32 bytes)
struct MsvcWString {
    union {
        wchar_t  buf[8];
        wchar_t* ptr;
    };
    uint64_t size;
    uint64_t cap;
};

#endif

} // namespace

LiveAudioManager::~LiveAudioManager() {
    reset();
}

void LiveAudioManager::configure(
    const std::string&  dll_dir_utf8,
    uint64_t            game_image_base,
    nm_build_id_t       build_id,
    OverlayAndChannels* overlay) {
    std::lock_guard<std::mutex> lock(mutex_);
    dll_dir_ = dll_dir_utf8;
    game_image_base_ = game_image_base;
    build_id_ = build_id;
    overlay_ = overlay;
}

bool LiveAudioManager::ensure_fmod_ready_locked() {
#if defined(_WIN32)
    if (fmod_system_ != nullptr) {
        return true;
    }
    if (fmod_init_attempted_) {
        return false;
    }
    fmod_init_attempted_ = true;

    HMODULE mod = GetModuleHandleW(L"fmod.dll");
    if (!mod && !dll_dir_.empty()) {
        const std::wstring candidate = utf8_to_wide_audio(dll_dir_ + "\\fmod.dll");
        mod = LoadLibraryW(candidate.c_str());
    }
    if (!mod) {
        mod = LoadLibraryW(L"fmod.dll");
    }
    if (!mod) {
        logger_logf("INFO", "[AUDIO] fmod.dll not present in process/directory; using tracked stub audio handles");
        return false;
    }

    g_fmod.System_Create      = load_fmod_proc<FmodSystemCreateFn>(mod, "FMOD_System_Create");
    g_fmod.System_SetOutput   = load_fmod_proc<FmodSystemSetOutputFn>(mod, "FMOD_System_SetOutput");
    g_fmod.System_Init        = load_fmod_proc<FmodSystemInitFn>(mod, "FMOD_System_Init");
    g_fmod.System_Update      = load_fmod_proc<FmodSystemUpdateFn>(mod, "FMOD_System_Update");
    g_fmod.System_Release     = load_fmod_proc<FmodSystemReleaseFn>(mod, "FMOD_System_Release");
    g_fmod.System_CreateSound = load_fmod_proc<FmodSystemCreateSoundFn>(mod, "FMOD_System_CreateSound");
    g_fmod.System_PlaySound   = load_fmod_proc<FmodSystemPlaySoundFn>(mod, "FMOD_System_PlaySound");
    g_fmod.Sound_SetLoopCount = load_fmod_proc<FmodSoundSetLoopCountFn>(mod, "FMOD_Sound_SetLoopCount");
    g_fmod.Sound_Release      = load_fmod_proc<FmodSoundReleaseFn>(mod, "FMOD_Sound_Release");
    g_fmod.Channel_SetVolume  = load_fmod_proc<FmodChannelSetVolumeFn>(mod, "FMOD_Channel_SetVolume");
    g_fmod.Channel_Stop       = load_fmod_proc<FmodChannelStopFn>(mod, "FMOD_Channel_Stop");
    g_fmod.Channel_IsPlaying  = load_fmod_proc<FmodChannelIsPlayingFn>(mod, "FMOD_Channel_IsPlaying");

    if (!g_fmod.System_Create || !g_fmod.System_Init || !g_fmod.System_CreateSound ||
        !g_fmod.System_PlaySound || !g_fmod.Sound_Release || !g_fmod.Channel_Stop) {
        logger_logf("WARN", "[AUDIO] fmod.dll loaded at %p but missing required FMOD_System_* exports", static_cast<void*>(mod));
        return false;
    }

    void* sys = nullptr;
    int32_t rc = g_fmod.System_Create(&sys, kFmodVersion203);
    if (rc != 0 || !sys) {
        logger_logf("WARN", "[AUDIO] FMOD_System_Create(0x%08X) failed rc=%d", kFmodVersion203, rc);
        return false;
    }

    bool nosound = false;
    rc = g_fmod.System_Init(sys, 64, kFmodInitNormal, nullptr);
    if (rc != 0 && g_fmod.System_SetOutput) {
        logger_logf("WARN", "[AUDIO] FMOD_System_Init(default) failed rc=%d; retrying with FMOD_OUTPUTTYPE_NOSOUND", rc);
        (void)g_fmod.System_SetOutput(sys, kFmodOutputNoSound);
        rc = g_fmod.System_Init(sys, 64, kFmodInitNormal, nullptr);
        nosound = true;
    }

    if (rc != 0) {
        logger_logf("ERROR", "[AUDIO] FMOD_System_Init failed even on NOSOUND fallback (rc=%d)", rc);
        if (g_fmod.System_Release) {
            (void)g_fmod.System_Release(sys);
        }
        return false;
    }

    fmod_module_ = static_cast<void*>(mod);
    fmod_system_ = sys;
    fmod_nosound_ = nosound;
    logger_logf("INFO", "[AUDIO] FMOD 2.03 initialized (output=%s, sys=%p)",
                nosound ? "nosound" : "default", sys);
    return true;
#else
    return false;
#endif
}

void LiveAudioManager::release_fmod_locked() {
#if defined(_WIN32)
    for (auto& kv : tracks_) {
        if (kv.second.fmod_channel && g_fmod.Channel_Stop) {
            (void)g_fmod.Channel_Stop(kv.second.fmod_channel);
        }
        if (kv.second.fmod_sound && g_fmod.Sound_Release) {
            (void)g_fmod.Sound_Release(kv.second.fmod_sound);
        }
    }
    tracks_.clear();
    if (fmod_system_ && g_fmod.System_Release) {
        (void)g_fmod.System_Release(fmod_system_);
    }
    fmod_system_ = nullptr;
    fmod_init_attempted_ = false;
    fmod_nosound_ = false;
#else
    tracks_.clear();
#endif
}

nm_status_t LiveAudioManager::play_ogg(
    const char* virtual_or_disk_path_utf8,
    float       volume,
    int32_t     loop,
    int32_t*    out_handle) {
    if (!virtual_or_disk_path_utf8 || virtual_or_disk_path_utf8[0] == '\0' || !out_handle) {
        return NM_ERR_INVALID_ARG;
    }
    *out_handle = 0;

    const float clamped_vol = clamp_volume(volume);
    const bool do_loop = (loop != 0);

    // Resolve audio bytes from overlay first (outside lock to avoid deadlock with managed callbacks).
    std::vector<uint8_t> audio_bytes;
    bool has_bytes = false;
    if (overlay_) {
        has_bytes = overlay_->resolve_file_overlay_owned(virtual_or_disk_path_utf8, audio_bytes);
    }
    (void)has_bytes;

#if defined(_WIN32)
    if (!has_bytes) {
        // Fallback: try direct disk path or relative to dll_dir.
        std::wstring direct_w = utf8_to_wide_audio(virtual_or_disk_path_utf8);
        has_bytes = read_file_bytes_win32(direct_w, audio_bytes);
        if (!has_bytes && !dll_dir_.empty()) {
            std::string rel = virtual_or_disk_path_utf8;
            std::replace(rel.begin(), rel.end(), '/', '\\');
            has_bytes = read_file_bytes_win32(utf8_to_wide_audio(dll_dir_ + "\\" + rel), audio_bytes);
        }
    }
#endif

    std::lock_guard<std::mutex> lock(mutex_);
    const int32_t handle = next_handle_++;

#if defined(_WIN32)
    if (ensure_fmod_ready_locked() && has_bytes && !audio_bytes.empty()) {
        const uint32_t mode = kFmodMode2D | kFmodModeCreateSample | kFmodModeOpenMemory |
                              (do_loop ? kFmodModeLoopNormal : kFmodModeLoopOff);
        FmodCreateSoundExInfo203 exinfo{};
        std::memset(&exinfo, 0, sizeof(exinfo));
        exinfo.cbsize = static_cast<int32_t>(sizeof(exinfo));
        exinfo.length = static_cast<uint32_t>(audio_bytes.size());

        void* sound = nullptr;
        int32_t rc = g_fmod.System_CreateSound(
            fmod_system_,
            reinterpret_cast<const char*>(audio_bytes.data()),
            mode,
            &exinfo,
            &sound);
        if (rc == 0 && sound) {
            if (do_loop && g_fmod.Sound_SetLoopCount) {
                (void)g_fmod.Sound_SetLoopCount(sound, -1);
            }
            void* channel = nullptr;
            rc = g_fmod.System_PlaySound(fmod_system_, sound, nullptr, 0, &channel);
            if (rc == 0 && channel) {
                if (g_fmod.Channel_SetVolume) {
                    (void)g_fmod.Channel_SetVolume(channel, clamped_vol);
                }
                if (g_fmod.System_Update) {
                    (void)g_fmod.System_Update(fmod_system_);
                }
                tracks_[handle] = ActiveTrack{handle, sound, channel, virtual_or_disk_path_utf8, clamped_vol, do_loop, true};
                *out_handle = handle;
                logger_logf("INFO", "[AUDIO] Playing OGG '%s' (bytes=%zu, loop=%d, volume=%.2f, handle=%d, output=%s, fmod_rc=0)",
                            virtual_or_disk_path_utf8,
                            audio_bytes.size(),
                            do_loop ? 1 : 0,
                            clamped_vol,
                            handle,
                            fmod_nosound_ ? "nosound" : "default");
                return NM_OK;
            }
            logger_logf("WARN", "[AUDIO] FMOD_System_PlaySound failed for '%s' rc=%d", virtual_or_disk_path_utf8, rc);
            (void)g_fmod.Sound_Release(sound);
        } else {
            logger_logf("WARN", "[AUDIO] FMOD_System_CreateSound failed for '%s' (bytes=%zu) rc=%d",
                        virtual_or_disk_path_utf8, audio_bytes.size(), rc);
        }
    } else if (fmod_system_ != nullptr && !has_bytes) {
        logger_logf("WARN", "[AUDIO] OGG '%s' not found in overlay or disk; returning fallback handle=%d",
                    virtual_or_disk_path_utf8, handle);
    }
#endif

    // Deterministic fallback handle (Linux tests, standalone Wine smoke without fmod.dll, or missing file).
    tracks_[handle] = ActiveTrack{handle, nullptr, nullptr, virtual_or_disk_path_utf8, clamped_vol, do_loop, true};
    *out_handle = handle;
    logger_logf("INFO", "[AUDIO] Playing OGG '%s' (bytes=%zu, loop=%d, volume=%.2f, handle=%d, mode=fallback)",
                virtual_or_disk_path_utf8,
                audio_bytes.size(),
                do_loop ? 1 : 0,
                clamped_vol,
                handle);
    return NM_OK;
}

nm_status_t LiveAudioManager::play_event(
    const char* fmod_event_utf8,
    float       volume,
    int32_t*    out_handle) {
    if (!fmod_event_utf8 || fmod_event_utf8[0] == '\0' || !out_handle) {
        return NM_ERR_INVALID_ARG;
    }

    const float clamped_vol = clamp_volume(volume);
    int32_t handle = 0;
    uint64_t base = 0;
    nm_build_id_t build = NM_BUILD_UNKNOWN;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        handle = next_handle_++;
        base = game_image_base_;
        build = build_id_;
        tracks_[handle] = ActiveTrack{handle, nullptr, nullptr, fmod_event_utf8, clamped_vol, false, true};
    }
    (void)base;
    (void)build;

#if defined(_WIN32)
    // Route named FMOD Studio events through PoE's native Audio::GetHolder -> WrapRequest -> SubmitRequest
    // when running inside a live 3.29 client process (08-deep-analysis.md §6).
    if (build != NM_BUILD_SYNTHETIC && build != NM_BUILD_UNKNOWN && base != 0) {
        const EmbeddedSymbol* sym_holder = find_symbol(build, "audio.get_holder");
        const EmbeddedSymbol* sym_wrap   = find_symbol(build, "audio.wrap_request");
        const EmbeddedSymbol* sym_submit = find_symbol(build, "audio.submit_request");
        const EmbeddedSymbol* sym_assign = find_symbol(build, "runtime.wstring_assign");
        if (sym_holder && sym_wrap && sym_submit && sym_assign &&
            sym_holder->rva && sym_wrap->rva && sym_submit->rva && sym_assign->rva) {
            struct EventCallCtx {
                uint64_t    holder_addr;
                uint64_t    wrap_addr;
                uint64_t    submit_addr;
                uint64_t    assign_addr;
                std::wstring wide_name;
                float       vol;
            };
            EventCallCtx ctx{
                base + sym_holder->rva,
                base + sym_wrap->rva,
                base + sym_submit->rva,
                base + sym_assign->rva,
                utf8_to_wide_audio(fmod_event_utf8),
                clamped_vol,
            };
            auto call_fn = [](void* raw) -> int64_t {
                auto* c = static_cast<EventCallCtx*>(raw);
                using GetHolderFn = void*(NM_CALL*)();
                using AssignFn    = void*(NM_CALL*)(void*, const wchar_t*, uint64_t);
                using WrapFn      = void(NM_CALL*)(void*, void*);
                using SubmitFn    = void(NM_CALL*)(void*, void*);

                auto get_holder = reinterpret_cast<GetHolderFn>(static_cast<uintptr_t>(c->holder_addr));
                auto w_assign   = reinterpret_cast<AssignFn>(static_cast<uintptr_t>(c->assign_addr));
                auto wrap_req   = reinterpret_cast<WrapFn>(static_cast<uintptr_t>(c->wrap_addr));
                auto submit_req = reinterpret_cast<SubmitFn>(static_cast<uintptr_t>(c->submit_addr));

                void* holder = get_holder();
                if (!holder) return -1;
                void* mgr = *reinterpret_cast<void**>(static_cast<uint8_t*>(holder) + 0x08);
                if (!mgr) return -2;

                MsvcWString ws{};
                std::memset(&ws, 0, sizeof(ws));
                ws.cap = 7;
                w_assign(&ws, c->wide_name.c_str(), static_cast<uint64_t>(c->wide_name.size()));

                alignas(16) uint8_t req[0xA0] = {};
                wrap_req(req, &ws);
                std::memcpy(req + 0x64, &c->vol, sizeof(float));
                submit_req(mgr, req);
                return 0;
            };

            int64_t rc = 0;
            nm_exception_info_t ex{};
            const nm_status_t st = invoke_guarded(call_fn, &ctx, &rc, &ex);
            logger_logf("INFO", "[AUDIO] Submitted engine audio event '%s' (vol=%.2f, handle=%d, st=%d, rc=%lld)",
                        fmod_event_utf8, clamped_vol, handle, static_cast<int>(st), static_cast<long long>(rc));
        }
    }
#endif

    *out_handle = handle;
    return NM_OK;
}

nm_status_t LiveAudioManager::stop(int32_t channel_handle) {
    if (channel_handle <= 0) {
        return NM_ERR_INVALID_ARG;
    }
    std::lock_guard<std::mutex> lock(mutex_);
    auto it = tracks_.find(channel_handle);
    if (it == tracks_.end()) {
        return NM_OK;
    }
#if defined(_WIN32)
    int32_t stop_rc = 0;
    int32_t release_rc = 0;
    if (it->second.fmod_channel) {
        if (g_fmod.Channel_SetVolume) {
            (void)g_fmod.Channel_SetVolume(it->second.fmod_channel, 0.0f);
        }
        if (g_fmod.Channel_Stop) {
            stop_rc = g_fmod.Channel_Stop(it->second.fmod_channel);
        }
    }
    if (it->second.fmod_sound && g_fmod.Sound_Release) {
        release_rc = g_fmod.Sound_Release(it->second.fmod_sound);
    }
    if (fmod_system_ && g_fmod.System_Update) {
        (void)g_fmod.System_Update(fmod_system_);
    }
    tracks_.erase(it);
    logger_logf("INFO", "[AUDIO] Stopped handle=%d (fmod_rc=%d, release_rc=%d)",
                channel_handle, stop_rc, release_rc);
#else
    tracks_.erase(it);
    logger_logf("INFO", "[AUDIO] Stopped handle=%d (fmod_rc=0)", channel_handle);
#endif
    return NM_OK;
}

nm_status_t LiveAudioManager::set_volume(int32_t channel_handle, float volume) {
    if (channel_handle <= 0) {
        return NM_ERR_INVALID_ARG;
    }
    const float clamped = clamp_volume(volume);
    std::lock_guard<std::mutex> lock(mutex_);
    auto it = tracks_.find(channel_handle);
    if (it == tracks_.end()) {
        return NM_ERR_INVALID_ARG;
    }
    it->second.volume = clamped;
#if defined(_WIN32)
    if (it->second.fmod_channel && g_fmod.Channel_SetVolume) {
        (void)g_fmod.Channel_SetVolume(it->second.fmod_channel, clamped);
    }
#endif
    return NM_OK;
}

nm_status_t LiveAudioManager::is_playing(int32_t channel_handle, int32_t* out_playing) {
    if (!out_playing) {
        return NM_ERR_INVALID_ARG;
    }
    *out_playing = 0;
    if (channel_handle <= 0) {
        return NM_OK;
    }
    std::lock_guard<std::mutex> lock(mutex_);
    auto it = tracks_.find(channel_handle);
    if (it == tracks_.end() || !it->second.active) {
        return NM_OK;
    }
#if defined(_WIN32)
    if (it->second.fmod_channel && g_fmod.Channel_IsPlaying) {
        int32_t playing = 0;
        if (g_fmod.Channel_IsPlaying(it->second.fmod_channel, &playing) == 0) {
            *out_playing = playing ? 1 : 0;
            return NM_OK;
        }
        *out_playing = 0;
        return NM_OK;
    }
#endif
    *out_playing = 1;
    return NM_OK;
}

void LiveAudioManager::tick_update() {
#if defined(_WIN32)
    std::lock_guard<std::mutex> lock(mutex_);
    if (fmod_system_ && g_fmod.System_Update) {
        (void)g_fmod.System_Update(fmod_system_);
    }
#endif
}

void LiveAudioManager::reset() {
    std::lock_guard<std::mutex> lock(mutex_);
    release_fmod_locked();
    dll_dir_.clear();
    game_image_base_ = 0;
    build_id_ = NM_BUILD_UNKNOWN;
    overlay_ = nullptr;
    next_handle_ = 1;
}

} // namespace nativemods
