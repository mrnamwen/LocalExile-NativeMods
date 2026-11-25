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
# 3. Linux -> Windows x64 cross-compile (requires mingw-w64-gcc / x86_64-w64-mingw32 sysroot):
#      cmake -S src/native-shim -B build/native-shim-win64 \
#        -DCMAKE_SYSTEM_NAME=Windows \
#        -DCMAKE_C_COMPILER=x86_64-w64-mingw32-gcc \
#        -DCMAKE_CXX_COMPILER=x86_64-w64-mingw32-g++ \
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
    src/coreclr_bootstrap.cpp
    src/client_host_shim.cpp
)

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
    add_library(version_proxy SHARED src/version_proxy.cpp)
    set_target_properties(version_proxy PROPERTIES
        PREFIX ""
        OUTPUT_NAME "version"
        SUFFIX ".dll"
    )
endif()

if(MSVC)
    target_compile_options(LocalExile.ClientHost PRIVATE /W4 /WX /EHa)
else()
    target_compile_options(LocalExile.ClientHost PRIVATE -Wall -Wextra -Wpedantic -Werror)
endif()

include(CTest)
if(BUILD_TESTING)
    add_executable(nativemods_shim_tests tests/shim_tests.cpp)
    target_include_directories(nativemods_shim_tests PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/include
        ${CMAKE_CURRENT_SOURCE_DIR}/src
    )
    target_link_libraries(nativemods_shim_tests PRIVATE LocalExile.ClientHost)
    if(MSVC)
        target_compile_options(nativemods_shim_tests PRIVATE /W4 /WX)
    else()
        target_compile_options(nativemods_shim_tests PRIVATE -Wall -Wextra -Wpedantic -Werror)
    endif()
    add_test(NAME nativemods_shim_tests COMMAND nativemods_shim_tests)
endif()
