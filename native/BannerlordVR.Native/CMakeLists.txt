cmake_minimum_required(VERSION 3.25)
project(BannerlordVR.Native LANGUAGES CXX)

# =============================================================================
# BannerlordVR.Native - OpenXR + D3D11 interop layer.
#
# Build (x64 only - the game is x64 and P/Invoke must match):
#   cmake -S native/BannerlordVR.Native -B build/native-cmake -A x64
#   cmake --build build/native-cmake --config Release
#
# Output lands in build/native/ where the managed project's DeployToGame target
# picks it up.
# =============================================================================

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Dynamic CRT (the MSVC default), deliberately. A static CRT would drop the
# MSVCP140/VCRUNTIME140 imports, but TaleWorlds.Native.dll imports them too - the
# redist is already a hard prerequisite of the game, so there is nothing to gain,
# and openxr_loader builds against the dynamic CRT anyway (mixing them is an
# LNK2038 wall).

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "BannerlordVR.Native must be built for x64. Configure with -A x64.")
endif()

# Default to whatever the tree can actually support, rather than to OFF.
#
# A hardcoded OFF default means anyone who reconfigures without remembering
# -DBVR_ENABLE_OPENXR=ON silently gets a stub DLL that loads, initialises, and
# reports NoRuntime - which reads exactly like a missing headset or a runtime
# problem and sends you debugging the wrong machine entirely. The submodules
# being present is the honest signal that the real build is wanted.
set(BVR_OPENXR_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../thirdparty/OpenXR-SDK)
set(BVR_MINHOOK_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../thirdparty/minhook)
set(BVR_IMGUI_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../thirdparty/imgui)

if(EXISTS ${BVR_OPENXR_DIR}/CMakeLists.txt AND EXISTS ${BVR_MINHOOK_DIR}/CMakeLists.txt)
    set(BVR_OPENXR_DEFAULT ON)
else()
    set(BVR_OPENXR_DEFAULT OFF)
    message(STATUS
        "OpenXR/MinHook submodules not found - building the STUB DLL. "
        "It will report NoRuntime at startup. Run: git submodule update --init --recursive")
endif()

option(BVR_ENABLE_OPENXR "Link the OpenXR loader and build the real session" ${BVR_OPENXR_DEFAULT})

# Say which one is being built, every configure, so it is never a surprise.
if(BVR_ENABLE_OPENXR)
    message(STATUS "BannerlordVR.Native: building WITH OpenXR (0.2.0-openxr)")
else()
    message(STATUS "BannerlordVR.Native: building the STUB (0.2.0-stub)")
endif()

# The OpenXR/D3D sources only compile when the submodules are present, so a
# fresh clone without them still produces a loadable stub DLL.
set(BVR_SOURCES src/bvr_api.cpp)
if(BVR_ENABLE_OPENXR)
    list(APPEND BVR_SOURCES
        src/bvr_config.cpp
        src/bvr_crash.cpp
        src/bvr_log.cpp
        src/d3d_hooks.cpp
        src/frame_map.cpp
        src/ngx_hook.cpp
        src/stereo_dup.cpp
        src/stereo_perf.cpp
        src/vr_sharpen.cpp
        src/vp_patch.cpp
        src/late_latch.cpp
        src/vr_overlay.cpp
        src/xr_context.cpp
        src/afw_warp.cpp
        src/image_latch.cpp
        src/depth_upscale.cpp
        src/ui_key.cpp
        src/vr_reticle.cpp
        src/xr_input.cpp
    )

    # ImGui, compiled straight into the DLL rather than as a library of its own.
    # It ships no CMakeLists, its build is five translation units, and vendoring
    # it as sources keeps the whole thing visible in one place.
    #
    # No platform backend. imgui_impl_win32 wants the window's message loop,
    # which would mean hooking a WndProc to read a mouse position we can already
    # ask Windows for directly - a much larger commitment for no gain here.
    list(APPEND BVR_SOURCES
        ${BVR_IMGUI_DIR}/imgui.cpp
        ${BVR_IMGUI_DIR}/imgui_draw.cpp
        ${BVR_IMGUI_DIR}/imgui_tables.cpp
        ${BVR_IMGUI_DIR}/imgui_widgets.cpp
        ${BVR_IMGUI_DIR}/backends/imgui_impl_dx11.cpp
    )
endif()

add_library(BannerlordVR.Native SHARED ${BVR_SOURCES})

target_include_directories(BannerlordVR.Native PRIVATE include src)

target_compile_definitions(BannerlordVR.Native PRIVATE
    BVR_BUILDING_DLL
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    UNICODE
    _UNICODE
)

if(MSVC)
    # /W4 not /Wall - /Wall is unusable noise on the Windows SDK headers.
    target_compile_options(BannerlordVR.Native PRIVATE /W4 /permissive- /EHsc)
    # Exceptions must never cross the C ABI; catching that at build time is
    # cheaper than debugging a Mono crash dump.
    target_compile_options(BannerlordVR.Native PRIVATE /wd4100)
endif()

target_link_libraries(BannerlordVR.Native PRIVATE d3d11 dxgi d3dcompiler)

# ---------------------------------------------------------------------------
# Phase 2 dependencies. Kept inert until the submodules are vendored so that a
# fresh clone still builds the stub.
# ---------------------------------------------------------------------------
if(BVR_ENABLE_OPENXR)
    if(NOT EXISTS ${BVR_OPENXR_DIR}/CMakeLists.txt)
        message(FATAL_ERROR
            "BVR_ENABLE_OPENXR=ON but ${BVR_OPENXR_DIR} is empty. "
            "Run: git submodule update --init --recursive")
    endif()
    if(NOT EXISTS ${BVR_MINHOOK_DIR}/CMakeLists.txt)
        message(FATAL_ERROR "minhook submodule missing at ${BVR_MINHOOK_DIR}")
    endif()

    add_subdirectory(${BVR_OPENXR_DIR} ${CMAKE_BINARY_DIR}/openxr EXCLUDE_FROM_ALL)
    add_subdirectory(${BVR_MINHOOK_DIR} ${CMAKE_BINARY_DIR}/minhook EXCLUDE_FROM_ALL)

    target_compile_definitions(BannerlordVR.Native PRIVATE BVR_ENABLE_OPENXR XR_USE_GRAPHICS_API_D3D11 XR_USE_PLATFORM_WIN32)
    target_link_libraries(BannerlordVR.Native PRIVATE openxr_loader minhook)

    # Stated explicitly rather than relying on the dependencies to export usable
    # INTERFACE include dirs - MinHook in particular does not.
    target_include_directories(BannerlordVR.Native PRIVATE
        ${BVR_OPENXR_DIR}/include
        ${BVR_MINHOOK_DIR}/include
        ${BVR_IMGUI_DIR})

    # ImGui is third-party and is not held to /W4. Its warnings are not ours to
    # fix and burying our own output under them is the real cost.
    set_source_files_properties(
        ${BVR_IMGUI_DIR}/imgui.cpp
        ${BVR_IMGUI_DIR}/imgui_draw.cpp
        ${BVR_IMGUI_DIR}/imgui_tables.cpp
        ${BVR_IMGUI_DIR}/imgui_widgets.cpp
        ${BVR_IMGUI_DIR}/backends/imgui_impl_dx11.cpp
        PROPERTIES COMPILE_OPTIONS "/W0")
endif()

# Single, predictable output location across all configurations.
set(BVR_OUT ${CMAKE_CURRENT_SOURCE_DIR}/../../build/native)
foreach(cfg "" _DEBUG _RELEASE _RELWITHDEBINFO _MINSIZEREL)
    set_target_properties(BannerlordVR.Native PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY${cfg} ${BVR_OUT}
        LIBRARY_OUTPUT_DIRECTORY${cfg} ${BVR_OUT})
endforeach()
