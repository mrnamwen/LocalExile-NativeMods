cmake_minimum_required(VERSION 3.20)
project(LocalExileClientHostShim LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# ==============================================================================
# Build Commands Reference
# ==============================================================================
# 1. Linux host-side library + C++20 unit test build:
#      cmake -S src/native-shim -B build/native-shim -DCMAKE_BUILD_TYPE=Release
#      cmake --build build/native-shim
#      ctest --test-dir build/native-shim --output-on-failure
#
# 2. Windows x64 native MSVC build (produces LocalExile.ClientHost.dll + version.dll):
#      cmake -S src/native-shim -B build/native-shim-win64 -G "Visual Studio 17 2022" -A x64
#      cmake --build build/native-shim-win64 --config Release
#
# 3. Linux -> Windows x64 MinGW-w64 cross-compile:
#      cmake -S src/native-shim -B build/native-shim-win64 \
#        -DCMAKE_TOOLCHAIN_FILE=src/native-shim/cmake/mingw-w64-x86_64.cmake \
#        -DCMAKE_BUILD_TYPE=Release
#      cmake --build build/native-shim-win64
# ==============================================================================

set(SHIM_SOURCES
    src/address_table.cpp
    src/hook_engine.cpp
    src/exception_wall.cpp
    src/game_allocator.cpp
    src/frame_queue.cpp
    src/overlay_and_channels.cpp
    src/live_audio.cpp
    src/live_engine_hooks.cpp
    src/coreclr_bootstrap.cpp
    src/shim_logger.cpp
    src/client_host_shim.cpp
)

if(WIN32)
    list(APPEND SHIM_SOURCES src/LocalExile.ClientHost.def)
endif()

add_library(LocalExile.ClientHost SHARED ${SHIM_SOURCES})
target_include_directories(LocalExile.ClientHost
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src
)
target_compile_definitions(LocalExile.ClientHost PRIVATE NATIVEMODS_SHIM_EXPORTS)

if(WIN32)
    set_target_properties(LocalExile.ClientHost PROPERTIES
        PREFIX ""
        OUTPUT_NAME "LocalExile.ClientHost"
        SUFFIX ".dll"
    )
    add_library(version_proxy SHARED src/version_proxy.cpp src/version_proxy.def)
    set_target_properties(version_proxy PROPERTIES
        PREFIX ""
        OUTPUT_NAME "version"
        SUFFIX ".dll"
    )
    if(MINGW)
        # Statically link libgcc, libstdc++, and winpthread so BOTH DLLs depend ONLY
        # on standard Windows system DLLs (kernel32.dll / UCRT / msvcrt.dll).
        set(MINGW_STATIC_LINK_FLAGS
            -static
            -static-libgcc
            -static-libstdc++
            -Wl,--enable-stdcall-fixup
        )
        target_link_options(LocalExile.ClientHost PRIVATE ${MINGW_STATIC_LINK_FLAGS})
        target_link_options(version_proxy PRIVATE ${MINGW_STATIC_LINK_FLAGS})
        target_compile_options(version_proxy PRIVATE -Wall -Wextra -Wpedantic -Werror)
    elseif(MSVC)
        target_compile_options(version_proxy PRIVATE /W4 /WX)
    endif()
endif()

if(MSVC)
    target_compile_options(LocalExile.ClientHost PRIVATE /W4 /WX /EHa)
else()
    target_compile_options(LocalExile.ClientHost PRIVATE -Wall -Wextra -Wpedantic -Werror)
endif()

include(CTest)
if(BUILD_TESTING)
    if(WIN32)
        # On Win32, LocalExile.ClientHost.dll exports strictly the C ABI (.def), so white-box
        # unit tests calling internal nativemods:: helpers link the core sources statically.
        set(SHIM_CORE_SOURCES
            src/address_table.cpp
            src/hook_engine.cpp
            src/exception_wall.cpp
            src/game_allocator.cpp
            src/frame_queue.cpp
            src/overlay_and_channels.cpp
            src/live_audio.cpp
            src/live_engine_hooks.cpp
            src/coreclr_bootstrap.cpp
            src/shim_logger.cpp
            src/client_host_shim.cpp
        )
        add_executable(nativemods_shim_tests tests/shim_tests.cpp ${SHIM_CORE_SOURCES})
        target_compile_definitions(nativemods_shim_tests PRIVATE NATIVEMODS_SHIM_STATIC)
    else()
        add_executable(nativemods_shim_tests tests/shim_tests.cpp)
        target_link_libraries(nativemods_shim_tests PRIVATE LocalExile.ClientHost)
    endif()
    target_include_directories(nativemods_shim_tests PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/include
        ${CMAKE_CURRENT_SOURCE_DIR}/src
    )
    if(MSVC)
        target_compile_options(nativemods_shim_tests PRIVATE /W4 /WX)
    else()
        target_compile_options(nativemods_shim_tests PRIVATE -Wall -Wextra -Wpedantic -Werror)
    endif()
    if(MINGW)
        target_link_options(nativemods_shim_tests PRIVATE
            -static
            -static-libgcc
            -static-libstdc++
        )
    endif()
    add_test(NAME nativemods_shim_tests COMMAND nativemods_shim_tests)

    if(WIN32)
        add_executable(r1_wine_smoke tests/r1_wine_smoke.cpp)
        target_include_directories(r1_wine_smoke PRIVATE
            ${CMAKE_CURRENT_SOURCE_DIR}/include
        )
        if(MSVC)
            target_compile_options(r1_wine_smoke PRIVATE /W4 /WX)
        else()
            target_compile_options(r1_wine_smoke PRIVATE -Wall -Wextra -Wpedantic -Werror)
        endif()
        if(MINGW)
            target_link_options(r1_wine_smoke PRIVATE
                -static
                -static-libgcc
                -static-libstdc++
            )
        endif()
    endif()
endif()
