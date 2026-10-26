/* BannerlordVR.Physics.Native - living ragdoll bridge for the physics module.
   Independent of OpenXR and of the BannerlordVR VR layer: it only reads and
   drives TaleWorlds.Native.dll ragdoll state, after the managed side has
   verified the complete loaded module's SHA-256. Main thread only. */
#ifndef BVR_PHYSICS_API_H
#define BVR_PHYSICS_API_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef BVR_PHYSICS_BUILD
#    define BVR_API __declspec(dllexport)
#  else
#    define BVR_API __declspec(dllimport)
#  endif
#  define BVR_CALL __cdecl
#else
#  define BVR_API
#  define BVR_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef enum BvrStatus {
    BVR_OK                 =  0,
    BVR_NOT_INITIALIZED    = -1,
    BVR_GRAPHICS_MISMATCH  = -4,   /* reused: an expected engine code prefix did not match */
    BVR_SESSION_NOT_READY  = -5    /* reused: a required output pointer was null */
} BvrStatus;

BVR_API int32_t BVR_CALL bvr_ragdoll_prepare(void);
/* Skeleton bone records (0x100 bytes per bone at skeleton+0x18), for hiding a
   severed limb: read one, or write floats into one under its lock. */
BVR_API int32_t BVR_CALL bvr_skeleton_bone_count(uintptr_t skeleton);
BVR_API int32_t BVR_CALL bvr_skeleton_bone_record(uintptr_t skeleton, int32_t bone, uint8_t* out, int32_t bytes);
BVR_API int32_t BVR_CALL bvr_skeleton_write_record(uintptr_t skeleton, int32_t bone, int32_t offset, const float* values, int32_t count);
/* The engine's palette refresh for one bone (TaleWorlds.Native 0x361010), and
   a bone's palette matrix collapsed onto one entity-space point. */
BVR_API int32_t BVR_CALL bvr_skeleton_refresh_bone(uintptr_t skeleton, int32_t bone);
BVR_API int32_t BVR_CALL bvr_skeleton_collapse_bone(uintptr_t skeleton, int32_t bone, float x, float y, float z);
/* Severed limbs kept collapsed through a hook on the engine's palette refresh. */
BVR_API int32_t BVR_CALL bvr_limb_sever(uintptr_t skeleton, int32_t root, uint64_t bones);
BVR_API int32_t BVR_CALL bvr_limb_forget(uintptr_t skeleton);
BVR_API int32_t BVR_CALL bvr_limb_sever_sunk(uintptr_t skeleton, int32_t root, uint64_t bones, int32_t parent, int32_t grand, float sink);
BVR_API int32_t BVR_CALL bvr_ragdoll_release(uintptr_t mission, uintptr_t agent, uintptr_t skeleton);
BVR_API int32_t BVR_CALL bvr_ragdoll_validate(uintptr_t mission, uintptr_t agent, uintptr_t skeleton);
/* Read-only developer evidence: module-relative implementations reached by
   ApplyForceOnRagdoll's two virtual calls. Does not call either method. */
BVR_API int32_t BVR_CALL bvr_ragdoll_force_api(uintptr_t mission, uintptr_t agent, uintptr_t skeleton,
    int32_t bone, uintptr_t* vectorMethodRva, uintptr_t* scalarMethodRva);
BVR_API int32_t BVR_CALL bvr_ragdoll_mass(uintptr_t mission, uintptr_t agent, uintptr_t skeleton,
    int32_t bone_count, float* total_mass, int32_t* body_count);
BVR_API int32_t BVR_CALL bvr_ragdoll_bone_masses(uintptr_t mission, uintptr_t agent, uintptr_t skeleton,
    int32_t bone_count, float* bone_mass, float* total_mass, int32_t* body_count);
/* Adds one torque per physical body: torques holds 3 floats per bone and is
   read on the first bone mapped to each body (the bones bvr_ragdoll_bone_masses
   reports a mass on). mode is PhysX PxForceMode (0 force, 3 acceleration).
   Goes through the engine's own body method (TaleWorlds.Native+0x11aa90),
   which takes the scene write lock and calls PxRigidBody::addTorque. */
BVR_API int32_t BVR_CALL bvr_ragdoll_add_torques(uintptr_t mission, uintptr_t agent, uintptr_t skeleton,
    int32_t bone_count, const float* torques, int32_t mode, int32_t* applied);

#ifdef __cplusplus
}
#endif

#endif /* BVR_PHYSICS_API_H */
