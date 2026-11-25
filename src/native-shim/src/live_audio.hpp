#pragma once

#include "nativemods_abi.h"

#include <cstdint>
#include <mutex>
#include <string>
#include <unordered_map>

namespace nativemods {

class OverlayAndChannels;

// Live Win64 audio backend using PoE 3.29.3d's shipped fmod.dll (FMOD Core 2.03,
// dynamically loaded with zero link-time import dependency) for looping .ogg playback,
// plus PoE's own audio manager (0xffaa90 / 0xf0ae0 / 0xff86e0) for FMOD Studio events.
// Gracefully falls back to FMOD_OUTPUTTYPE_NOSOUND when no audio device is available,
// and to tracked handles when fmod.dll is absent (Linux tests / standalone Wine smoke).
class LiveAudioManager {
public:
    LiveAudioManager() = default;
    ~LiveAudioManager();

    LiveAudioManager(const LiveAudioManager&) = delete;
    LiveAudioManager& operator=(const LiveAudioManager&) = delete;

    void configure(
        const std::string& dll_dir_utf8,
        uint64_t           game_image_base,
        nm_build_id_t      build_id,
        OverlayAndChannels* overlay);

    nm_status_t play_ogg(
        const char* virtual_or_disk_path_utf8,
        float       volume,
        int32_t     loop,
        int32_t*    out_handle);

    nm_status_t play_event(
        const char* fmod_event_utf8,
        float       volume,
        int32_t*    out_handle);

    nm_status_t stop(int32_t channel_handle);
    nm_status_t set_volume(int32_t channel_handle, float volume);
    nm_status_t is_playing(int32_t channel_handle, int32_t* out_playing);

    void tick_update();
    void reset();

private:
    struct ActiveTrack {
        int32_t     handle = 0;
        void*       fmod_sound = nullptr;
        void*       fmod_channel = nullptr;
        std::string path;
        float       volume = 1.0f;
        bool        loop = false;
        bool        active = false;
    };

    bool ensure_fmod_ready_locked();
    void release_fmod_locked();

    std::mutex                             mutex_;
    std::string                            dll_dir_;
    uint64_t                               game_image_base_ = 0;
    nm_build_id_t                          build_id_ = NM_BUILD_UNKNOWN;
    OverlayAndChannels*                    overlay_ = nullptr;
    int32_t                                next_handle_ = 1;
    std::unordered_map<int32_t, ActiveTrack> tracks_;

#if defined(_WIN32)
    void* fmod_module_ = nullptr;
    void* fmod_system_ = nullptr;
    bool  fmod_init_attempted_ = false;
    bool  fmod_nosound_ = false;
#endif
};

} // namespace nativemods
