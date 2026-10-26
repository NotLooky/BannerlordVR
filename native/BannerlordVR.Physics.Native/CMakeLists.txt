cmake_minimum_required(VERSION 3.25)
project(BannerlordVR.Physics.Native LANGUAGES CXX)

# =============================================================================
# BannerlordVR.Physics.Native - living ragdoll bridge for the enemy physics
# (VR/Physics). No OpenXR, no hooks: it validates and drives TaleWorlds.Native.dll
# ragdoll state; loaded only when enemy_physics = 1.
#
#   cmake -S native/BannerlordVR.Physics.Native -B build/physics-native-cmake -A x64
#   cmake --build build/physics-native-cmake --config Release
#
# Output lands in build/native/, deployed with BannerlordVR.Native.dll by
# BannerlordVR.csproj DeployToGame.
# =============================================================================

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "BannerlordVR.Physics.Native must be built for x64. Configure with -A x64.")
endif()

add_library(BannerlordVR.Physics.Native SHARED src/living_ragdoll.cpp src/skeleton_bones.cpp)
target_include_directories(BannerlordVR.Physics.Native PRIVATE include)
target_compile_definitions(BannerlordVR.Physics.Native PRIVATE BVR_PHYSICS_BUILD WIN32_LEAN_AND_MEAN NOMINMAX)
if(MSVC)
    target_compile_options(BannerlordVR.Physics.Native PRIVATE /W4 /permissive-)
endif()

set(BVR_PHYSICS_OUT ${CMAKE_CURRENT_SOURCE_DIR}/../../build/native)
set_target_properties(BannerlordVR.Physics.Native PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY_RELEASE ${BVR_PHYSICS_OUT}
    RUNTIME_OUTPUT_DIRECTORY_DEBUG   ${BVR_PHYSICS_OUT}
    RUNTIME_OUTPUT_DIRECTORY         ${BVR_PHYSICS_OUT})
