#include "bvr_physics_api.h"
#include <windows.h>
#include <cstdint>
#include <cstring>
#include <cmath>

namespace {
using Release = void(__fastcall*)(void*, void*);
Release release_body = nullptr;
DWORD owner_thread = 0;
uintptr_t module_base = 0;
size_t module_bytes = 0;

bool readable(const void* pointer, size_t bytes)
{
    const uintptr_t start = reinterpret_cast<uintptr_t>(pointer);
    if (!start || start + bytes < start) return false;
    MEMORY_BASIC_INFORMATION info{};
    if (!VirtualQuery(pointer, &info, sizeof(info)) || info.State != MEM_COMMIT ||
        (info.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
    const uintptr_t end = reinterpret_cast<uintptr_t>(info.BaseAddress) + info.RegionSize;
    return start + bytes <= end;
}
template<class T> T read(const void* object, size_t offset)
{
    T value{};
    std::memcpy(&value, static_cast<const uint8_t*>(object) + offset, sizeof(T));
    return value;
}
}

// Called only after the managed layer verifies the complete loaded module's
// on-disk SHA-256. The prefix additionally refuses a hook at the selected entry.
int32_t BVR_CALL bvr_ragdoll_prepare(void)
{
    release_body = nullptr;
    owner_thread = 0;
    module_base = 0; module_bytes = 0;
    HMODULE module = GetModuleHandleW(L"TaleWorlds.Native.dll");
    if (!module) return BVR_NOT_INITIALIZED;
    auto entry = reinterpret_cast<const uint8_t*>(module) + 0x5d33f0;
    const uint8_t prefix[] = {
        0x48,0x89,0x5c,0x24,0x08,0x57,0x48,0x83,0xec,0x20,0x4c,0x8b,0x82,0x10,0x08,0x00,
        0x00,0x48,0x8b,0xda,0x48,0x8b,0xf9,0x4d,0x85,0xc0,0x74,0x5e,0x41,0x0f,0xb7,0x40,
        0x40,0x66,0x83,0xe8,0x02,0x66,0x83,0xf8,0x02,0x77,0x4f,0xf3,0x41,0x0f,0x10,0x48
    };
    if (!readable(entry, sizeof(prefix)) || std::memcmp(entry, prefix, sizeof(prefix)))
        return BVR_GRAPHICS_MISMATCH;
    if (!readable(module, sizeof(IMAGE_DOS_HEADER))) return BVR_GRAPHICS_MISMATCH;
    const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(module);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew < 0 || dos->e_lfanew > 0x1000)
        return BVR_GRAPHICS_MISMATCH;
    const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(reinterpret_cast<const uint8_t*>(module) + dos->e_lfanew);
    if (!readable(nt, sizeof(*nt)) || nt->Signature != IMAGE_NT_SIGNATURE || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64)
        return BVR_GRAPHICS_MISMATCH;
    module_base = reinterpret_cast<uintptr_t>(module);
    module_bytes = nt->OptionalHeader.SizeOfImage;
    release_body = reinterpret_cast<Release>(const_cast<uint8_t*>(entry));
    owner_thread = GetCurrentThreadId();
    return BVR_OK;
}

int32_t BVR_CALL bvr_ragdoll_force_api(uintptr_t mission, uintptr_t agent, uintptr_t skeleton_value,
    int32_t bone, uintptr_t* vector_rva, uintptr_t* scalar_rva)
{
    if (!vector_rva || !scalar_rva) return BVR_SESSION_NOT_READY;
    *vector_rva = *scalar_rva = 0;
    const int32_t validation = bvr_ragdoll_validate(mission, agent, skeleton_value);
    if (validation != BVR_OK) return validation;
    const auto skeleton = reinterpret_cast<void*>(skeleton_value);
    if (bone < 0 || bone >= 64 || read<uint16_t>(skeleton, 0x40) != 3) return -28;
    void* data = read<void*>(skeleton, 0xd0);
    if (!data || !readable(static_cast<uint8_t*>(data) + 0x1000, 8)) return -30;
    void* bones = read<void*>(data, 0x1000);
    if (!bones || !readable(static_cast<uint8_t*>(bones) + 0x209 + bone, 1)) return -30;
    const int32_t index = read<int8_t>(bones, 0x209 + bone);
    if (index < 0 || index >= 64 || !readable(static_cast<uint8_t*>(bones) + 8 + index * 8, 8)) return -31;
    void* holder = read<void*>(bones, 8 + index * 8);
    if (!readable(holder, 8)) return -32;
    void* body = read<void*>(holder, 0);
    if (!readable(body, 8)) return -32;
    void* table = read<void*>(body, 0);
    if (!readable(table, 0x1a0)) return -32;
    const uintptr_t vector_method = read<uintptr_t>(table, 0x190);
    const uintptr_t scalar_method = read<uintptr_t>(table, 0x198);
    if (vector_method < module_base || vector_method - module_base >= module_bytes ||
        scalar_method < module_base || scalar_method - module_base >= module_bytes) return -33;
    *vector_rva = vector_method - module_base;
    *scalar_rva = scalar_method - module_base;
    return BVR_OK;
}

// Shared walk over the active ragdoll's unique PhysX bodies. bone_mass, when
// supplied, receives each body's mass on the first bone mapped to it and zero
// on every other bone, so managed code applies one force per physical body.
static int32_t ragdoll_bodies(uintptr_t mission, uintptr_t agent, uintptr_t skeleton_value,
    int32_t bone_count, float* bone_mass, float* total_mass, int32_t* body_count)
{
    if (!total_mass || !body_count) return BVR_SESSION_NOT_READY;
    *total_mass = 0; *body_count = 0;
    const int32_t validation = bvr_ragdoll_validate(mission, agent, skeleton_value);
    if (validation != BVR_OK) return validation;
    const auto skeleton = reinterpret_cast<void*>(skeleton_value);
    if (bone_count < 1 || bone_count > 127 || read<uint16_t>(skeleton, 0x40) != 3) return -28;
    // Exact getter identified through the live vtable and REA: reads PhysX
    // getMass under the engine scene lock, without applying any force.
    const uintptr_t mass_entry = module_base + 0x11ade0;
    const uint8_t prefix[] = {0x40,0x57,0x41,0x56,0x41,0x57,0x48,0x83,0xec,0x40,0x48,0xc7,
        0x44,0x24,0x20,0xfe,0xff,0xff,0xff,0x48,0x89,0x5c,0x24,0x60};
    if (!readable(reinterpret_cast<void*>(mass_entry), sizeof(prefix)) ||
        std::memcmp(reinterpret_cast<void*>(mass_entry), prefix, sizeof(prefix))) return BVR_GRAPHICS_MISMATCH;
    void* data = read<void*>(skeleton, 0xd0);
    if (!data || !readable(static_cast<uint8_t*>(data) + 0x1000, 8)) return -30;
    void* bones = read<void*>(data, 0x1000);
    if (!bones || !readable(bones, 0x209 + bone_count)) return -30;
    if (bone_mass) for (int bone = 0; bone < bone_count; ++bone) bone_mass[bone] = 0;
    void* seen[64]{};
    int count = 0;
    float total = 0;
    using GetMass = float(__fastcall*)(void*);
    const auto get_mass = reinterpret_cast<GetMass>(mass_entry);
    for (int bone = 0; bone < bone_count; ++bone) {
        const int index = read<int8_t>(bones, 0x209 + bone);
        if (index < 0) continue;
        if (index >= 64) return -31;
        void* holder = read<void*>(bones, 8 + index * 8);
        if (!holder) continue;
        if (!readable(holder, 8)) return -32;
        void* body = read<void*>(holder, 0);
        if (!readable(body, 0x98)) return -32;
        bool duplicate = false;
        for (int i = 0; i < count; ++i) if (seen[i] == body) { duplicate = true; break; }
        if (duplicate) continue;
        if (count >= 64) return -31;
        void* table = read<void*>(body, 0);
        if (!readable(table, 0x1a0) || read<uintptr_t>(table, 0x198) != mass_entry ||
            read<uintptr_t>(table, 0x190) != module_base + 0x11acc0) return -33;
        const float mass = get_mass(body);
        if (!std::isfinite(mass) || mass <= 0 || mass > 500) return -34;
        if (bone_mass) bone_mass[bone] = mass;
        total += mass; seen[count++] = body;
    }
    if (count == 0 || !std::isfinite(total) || total > 500) return -34;
    *total_mass = total; *body_count = count;
    return BVR_OK;
}

// Torque per physical body through the engine body method that wraps
// PxRigidBody::addTorque (vtable slot 47, +0x178) under the scene lock. The
// body's vtable must also carry the known force and mass methods.
int32_t BVR_CALL bvr_ragdoll_add_torques(uintptr_t mission, uintptr_t agent, uintptr_t skeleton_value,
    int32_t bone_count, const float* torques, int32_t mode, int32_t* applied)
{
    if (!torques || !applied) return BVR_SESSION_NOT_READY;
    *applied = 0;
    if (mode != 0 && mode != 3) return -35;
    const int32_t validation = bvr_ragdoll_validate(mission, agent, skeleton_value);
    if (validation != BVR_OK) return validation;
    const auto skeleton = reinterpret_cast<void*>(skeleton_value);
    if (bone_count < 1 || bone_count > 127 || read<uint16_t>(skeleton, 0x40) != 3) return -28;
    const uintptr_t torque_entry = module_base + 0x11aa90;
    const uint8_t prefix[] = {0x57,0x48,0x83,0xec,0x50,0x48,0xc7,0x44,0x24,0x20,0xfe,0xff,0xff,0xff};
    // push rdi is 0x40 0x57 (REX) at the entry.
    if (!readable(reinterpret_cast<void*>(torque_entry), sizeof(prefix) + 1) ||
        std::memcmp(reinterpret_cast<uint8_t*>(torque_entry) + 1, prefix, sizeof(prefix))) return BVR_GRAPHICS_MISMATCH;
    void* data = read<void*>(skeleton, 0xd0);
    if (!data || !readable(static_cast<uint8_t*>(data) + 0x1000, 8)) return -30;
    void* bones = read<void*>(data, 0x1000);
    if (!bones || !readable(bones, 0x209 + bone_count)) return -30;
    using AddTorque = void(__fastcall*)(void*, const float*, int8_t);
    const auto add_torque = reinterpret_cast<AddTorque>(torque_entry);
    void* seen[64]{};
    int count = 0, done = 0;
    for (int bone = 0; bone < bone_count; ++bone) {
        const int index = read<int8_t>(bones, 0x209 + bone);
        if (index < 0) continue;
        if (index >= 64) return -31;
        void* holder = read<void*>(bones, 8 + index * 8);
        if (!holder) continue;
        if (!readable(holder, 8)) return -32;
        void* body = read<void*>(holder, 0);
        if (!readable(body, 0x98)) return -32;
        bool duplicate = false;
        for (int i = 0; i < count; ++i) if (seen[i] == body) { duplicate = true; break; }
        if (duplicate) continue;
        if (count >= 64) return -31;
        seen[count++] = body;
        void* table = read<void*>(body, 0);
        if (!readable(table, 0x1a0) || read<uintptr_t>(table, 0x178) != torque_entry ||
            read<uintptr_t>(table, 0x190) != module_base + 0x11acc0 ||
            read<uintptr_t>(table, 0x198) != module_base + 0x11ade0) return -33;
        // The engine method dereferences the PhysX actor at +0x40 and only
        // accepts a rigid dynamic (concrete type 5); check before calling.
        void* actor = read<void*>(body, 0x40);
        if (!readable(actor, 0x10) || read<uint16_t>(actor, 8) != 5) continue;
        const float* t = torques + bone * 3;
        if (!std::isfinite(t[0]) || !std::isfinite(t[1]) || !std::isfinite(t[2])) continue;
        if (t[0] == 0 && t[1] == 0 && t[2] == 0) continue;
        add_torque(body, t, static_cast<int8_t>(mode));
        ++done;
    }
    *applied = done;
    return BVR_OK;
}

int32_t BVR_CALL bvr_ragdoll_mass(uintptr_t mission, uintptr_t agent, uintptr_t skeleton_value,
    int32_t bone_count, float* total_mass, int32_t* body_count)
{
    return ragdoll_bodies(mission, agent, skeleton_value, bone_count, nullptr, total_mass, body_count);
}

int32_t BVR_CALL bvr_ragdoll_bone_masses(uintptr_t mission, uintptr_t agent, uintptr_t skeleton_value,
    int32_t bone_count, float* bone_mass, float* total_mass, int32_t* body_count)
{
    if (!bone_mass) return BVR_SESSION_NOT_READY;
    return ragdoll_bodies(mission, agent, skeleton_value, bone_count, bone_mass, total_mass, body_count);
}

int32_t BVR_CALL bvr_ragdoll_release(uintptr_t mission_value, uintptr_t agent_value, uintptr_t skeleton_value)
{
    const int32_t validation = bvr_ragdoll_validate(mission_value, agent_value, skeleton_value);
    if (validation != BVR_OK) return validation;
    auto mission = reinterpret_cast<void*>(mission_value);
    auto agent = reinterpret_cast<void*>(agent_value);
    void* body = read<void*>(agent, 0x810);
    const uint16_t state = read<uint16_t>(body, 0x40);
    if (state < 2 || state > 4) return -28;
    // The existing engine helper marks NeedsDeactivation and removes this
    // agent from its mission ragdoll list. The engine owns actual body teardown.
    release_body(mission, agent);
    // A callback may complete the transition immediately. Do not dereference
    // the old body after invoking engine code; managed observes the new state.
    return BVR_OK;
}

int32_t BVR_CALL bvr_ragdoll_validate(uintptr_t mission_value, uintptr_t agent_value, uintptr_t skeleton_value)
{
    if (!release_body || GetCurrentThreadId() != owner_thread) return BVR_NOT_INITIALIZED;
    auto mission = reinterpret_cast<void*>(mission_value);
    auto agent = reinterpret_cast<void*>(agent_value);
    auto skeleton = reinterpret_cast<void*>(skeleton_value);
    if (!readable(agent, 0xc60)) return -20;
    if (!readable(skeleton, 0x180)) return -21;
    // Validate the actual two mission fields read by the helper. Requiring the
    // entire 18 MB mission to share one VirtualQuery region rejects valid heaps.
    if (!mission_value || mission_value + 0x11849f0 < mission_value ||
        !readable(reinterpret_cast<void*>(mission_value + 0x11849e0), 16)) return -22;
    // The recovered helper is only admitted for the live agent, mission and
    // skeleton whose identities were checked by managed ownership immediately
    // before this call. Never change AgentState or health to reach corpse code.
    if (read<int32_t>(agent, 0x560) != 1) return -23;
    if (read<uintptr_t>(agent, 0xab8) != mission_value) return -24;
    void* body = read<void*>(agent, 0x810);
    // MBAgentVisuals.GetSkeleton already returns entity+0x298's value. The
    // agent's cached skeleton at +0x810 must equal that returned pointer itself.
    // Applying the entity offset to the skeleton again compares unrelated data.
    if (!body || body != skeleton)
        return -25;
    const uintptr_t begin = read<uintptr_t>(mission, 0x11849e0);
    const uintptr_t end = read<uintptr_t>(mission, 0x11849e8);
    if (end < begin || (end - begin) % sizeof(uintptr_t) || (end - begin) / sizeof(uintptr_t) > 16384 ||
        (end != begin && !readable(reinterpret_cast<void*>(begin), end - begin)))
        return -27;
    return BVR_OK;
}
