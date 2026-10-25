// Per-bone records of a TaleWorlds.Native skeleton, for hiding a severed
// limb. A skeleton keeps one 0x100-byte record per bone in an array at +0x18,
// its bone count in the byte at +0x20 (the rest-pose reset at 0x35d3f0 walks
// them, writing each record under the spin lock in its first dword). Which
// record fields the renderer draws from is found in game by the managed
// probe (LimbHide), which matches them against GetBoneEntitialFrameWithIndex.
#include "bvr_physics_api.h"
#include <windows.h>
#include <intrin.h>
#include <cstring>
#include <cmath>

namespace
{
    bool readable(const void* pointer, size_t bytes)
    {
        if (!pointer) return false;
        MEMORY_BASIC_INFORMATION info{};
        if (!VirtualQuery(pointer, &info, sizeof info) || info.State != MEM_COMMIT) return false;
        if (info.Protect & (PAGE_NOACCESS | PAGE_GUARD)) return false;
        const auto begin = reinterpret_cast<uintptr_t>(pointer);
        const auto end = reinterpret_cast<uintptr_t>(info.BaseAddress) + info.RegionSize;
        return begin + bytes <= end;
    }

    constexpr size_t RecordBytes = 0x100;

    uint8_t* record_of(uintptr_t skeleton_value, int32_t bone)
    {
        auto skeleton = reinterpret_cast<uint8_t*>(skeleton_value);
        if (!readable(skeleton, 0x28)) return nullptr;
        const int32_t count = *reinterpret_cast<int8_t*>(skeleton + 0x20);
        if (bone < 0 || bone >= count || count > 128) return nullptr;
        auto records = *reinterpret_cast<uint8_t**>(skeleton + 0x18);
        if (!records) return nullptr;
        uint8_t* record = records + static_cast<size_t>(bone) * RecordBytes;
        return readable(record, RecordBytes) ? record : nullptr;
    }
}

int32_t BVR_CALL bvr_skeleton_bone_count(uintptr_t skeleton_value)
{
    auto skeleton = reinterpret_cast<uint8_t*>(skeleton_value);
    if (!readable(skeleton, 0x28)) return -20;
    return *reinterpret_cast<int8_t*>(skeleton + 0x20);
}

int32_t BVR_CALL bvr_skeleton_bone_record(uintptr_t skeleton_value, int32_t bone, uint8_t* out, int32_t bytes)
{
    if (!out || bytes <= 0) return BVR_SESSION_NOT_READY;
    uint8_t* record = record_of(skeleton_value, bone);
    if (!record) return -21;
    const size_t n = static_cast<size_t>(bytes) < RecordBytes ? static_cast<size_t>(bytes) : RecordBytes;
    std::memcpy(out, record, n);
    return static_cast<int32_t>(n);
}

// Writes count floats at offset (4-byte aligned, past the lock dword) under the
// record's lock, as the engine itself writes it.
int32_t BVR_CALL bvr_skeleton_write_record(uintptr_t skeleton_value, int32_t bone, int32_t offset,
    const float* values, int32_t count)
{
    if (!values || count <= 0 || offset < 4 || (offset & 3) != 0 ||
        static_cast<size_t>(offset) + static_cast<size_t>(count) * 4 > RecordBytes) return BVR_SESSION_NOT_READY;
    uint8_t* record = record_of(skeleton_value, bone);
    if (!record) return -21;
    auto lock = reinterpret_cast<volatile long*>(record);
    int spins = 0;
    while (_interlockedbittestandset(lock, 0))
    {
        _mm_pause();
        if (++spins > 1000000) return -22;
    }
    std::memcpy(record + offset, values, static_cast<size_t>(count) * 4);
    _InterlockedExchange(lock, 0);
    return BVR_OK;
}

// The skinning palette. After writing a bone's record the engine calls
// 0x361010(skeleton, bone): it turns the record's drawn frame (quaternion +
// position) into a matrix, multiplies it by the record's rest matrix at +0x60
// and stores the result, the matrix the GPU skins the bone with, in a global
// table of 64-byte entries: [module+0xd96d18] + 0xc28, buffer
// [+0x250] x 0x128, pages of 0x2000 entries, at the skeleton's base index for
// this frame (+0x44, allocated by 0x35cac0 on the frame's first write, tagged
// with the frame number at +0x4c from [[module+0xd96cf0] + 0x28c]).
namespace
{
    using RefreshBone = void(__fastcall*)(void* skeleton, int8_t bone);
    const uint8_t RefreshCode[] = { 0x48, 0x8b, 0xc4, 0x48, 0x89, 0x58, 0x18, 0x48, 0x89, 0x78, 0x20, 0x55, 0x48, 0x8d, 0x68, 0xc8 };
    const uint8_t PaletteIndexCode[] = { 0x48, 0x89, 0x5c, 0x24, 0x08, 0x57, 0x48, 0x83, 0xec, 0x20, 0x48, 0x8b, 0x05, 0x1f, 0xa2, 0xa3 };
    constexpr uintptr_t RefreshRva = 0x361010, PaletteIndexRva = 0x35cac0, PaletteGlobalRva = 0xd96d18, FrameGlobalRva = 0xd96cf0;

    uint8_t* engine_base()
    {
        static uint8_t* base = nullptr;
        static bool checked = false;
        if (checked) return base;
        checked = true;
        auto module = reinterpret_cast<uint8_t*>(GetModuleHandleW(L"TaleWorlds.Native.dll"));
        if (!module || !readable(module + RefreshRva, sizeof RefreshCode) || !readable(module + PaletteIndexRva, sizeof PaletteIndexCode)) return nullptr;
        if (std::memcmp(module + RefreshRva, RefreshCode, sizeof RefreshCode) != 0 ||
            std::memcmp(module + PaletteIndexRva, PaletteIndexCode, sizeof PaletteIndexCode) != 0) return nullptr;
        base = module;
        return base;
    }

    float* palette_entry(uint8_t* module, uint8_t* skeleton, int32_t bone)
    {
        if (!readable(module + FrameGlobalRva, 8) || !readable(module + PaletteGlobalRva, 8)) return nullptr;
        auto frames = *reinterpret_cast<uint8_t**>(module + FrameGlobalRva);
        auto palette = *reinterpret_cast<uint8_t**>(module + PaletteGlobalRva);
        if (!readable(frames + 0x28c, 4) || !palette || !readable(palette + 0xc28 + 0x250, 4)) return nullptr;
        if (!readable(skeleton + 0x44, 12)) return nullptr;
        // Only this frame's slots: written by the refresh just before.
        if (*reinterpret_cast<int32_t*>(skeleton + 0x4c) != *reinterpret_cast<int32_t*>(frames + 0x28c)) return nullptr;
        uint8_t* table = palette + 0xc28;
        const int32_t buffer = *reinterpret_cast<int32_t*>(table + 0x250);
        if (buffer < 0 || buffer > 8) return nullptr;
        uint8_t* pages = table + static_cast<intptr_t>(buffer) * 0x128;
        const uint32_t index = static_cast<uint32_t>(*reinterpret_cast<int32_t*>(skeleton + 0x44) + bone);
        const uint32_t page = index >> 13, slot = index & 0x1fff;
        if (page > 32 || !readable(pages + 8 + page * 8, 8)) return nullptr;
        auto entries = *reinterpret_cast<uint8_t**>(pages + 8 + page * 8);
        float* entry = reinterpret_cast<float*>(entries + static_cast<size_t>(slot) * 64);
        return readable(entry, 64) ? entry : nullptr;
    }

    // A frozen skeleton (flag 8 at +0xa8: a ragdoll at rest) is drawn from a
    // snapshot of its palette, 64 matrices at [+0xd0] indexed by bone, that
    // the frame's slot allocation (0x35cb20) copies back every frame; no
    // palette writer runs, so no hook either. A body frozen before the cut
    // kept drawing its head until knocked awake. The snapshot is collapsed too.
    void collapse_snapshot(uint8_t* skeleton, int32_t bone, const float* at)
    {
        if (bone < 0 || bone >= 64 || !readable(skeleton + 0xd0, 8)) return;
        auto snapshot = *reinterpret_cast<uint8_t**>(skeleton + 0xd0);
        if (!snapshot || !readable(snapshot + bone * 64, 64)) return;
        auto entry = reinterpret_cast<float*>(snapshot + bone * 64);
        for (int k = 0; k < 12; k++) entry[k] = 0;
        entry[12] = at[0]; entry[13] = at[1]; entry[14] = at[2];
    }
}

// The engine's own follow-up to a record write: the bone's palette matrix.
int32_t BVR_CALL bvr_skeleton_refresh_bone(uintptr_t skeleton_value, int32_t bone)
{
    uint8_t* module = engine_base();
    if (!module) return -23;
    if (!record_of(skeleton_value, bone)) return -21;
    reinterpret_cast<RefreshBone>(module + RefreshRva)(reinterpret_cast<void*>(skeleton_value), static_cast<int8_t>(bone));
    return BVR_OK;
}

// A bone's palette matrix collapsed onto one point (entity space): every
// vertex it skins is drawn there. Refreshes the bone first so this frame's
// slot exists, then overwrites its three axes with zero.
int32_t BVR_CALL bvr_skeleton_collapse_bone(uintptr_t skeleton_value, int32_t bone, float x, float y, float z)
{
    uint8_t* module = engine_base();
    if (!module) return -23;
    if (!record_of(skeleton_value, bone)) return -21;
    auto skeleton = reinterpret_cast<uint8_t*>(skeleton_value);
    reinterpret_cast<RefreshBone>(module + RefreshRva)(skeleton, static_cast<int8_t>(bone));
    const float at[3] = { x, y, z };
    collapse_snapshot(skeleton, bone, at);
    float* entry = palette_entry(module, skeleton, bone);
    if (!entry) return -24;
    for (int k = 0; k < 12; k++) entry[k] = 0;
    entry[12] = x; entry[13] = y; entry[14] = z;
    return BVR_OK;
}

// Hook on the palette refresh (0x361010). The engine fills a bone's palette
// matrix there from its record at a point of the frame that depends on how the
// body moves: a collapse written from the mission tick was drawn for half a
// second, then the ragdoll's own later refresh drew the head again. The hook
// lets the engine compute the matrix, then collapses it again for every bone
// of a severed limb, whoever called and whenever.
//
// The function starts 16-byte aligned with 12 bytes of position-independent
// prologue (mov rax,rsp / mov [rax+18h],rbx / mov [rax+20h],rdi / push rbp):
// they are copied to a trampoline that jumps back to +12, and replaced by
// mov rax, hook / jmp rax in one 16-byte compare-exchange, so no thread can
// run a half-written prologue. rax is free: the original sets it first.
namespace
{
    struct Severed
    {
        volatile uintptr_t skeleton;   // 0 = free
        volatile int32_t root;
        volatile uint64_t bones;       // bit per bone index < 64
        volatile int32_t parent, grand; // the stump's bone and the one above
        volatile float sink;           // > 0: the root is sunk this far down the stump
    };
    constexpr int MaxSevered = 32;
    Severed severed[MaxSevered];
    RefreshBone original_refresh = nullptr;
    uint8_t* trampoline = nullptr;
    bool hooked = false;

    // The root's drawn position sunk down the stump's axis, in its record
    // (under its lock): what follows the bone rather than the palette (a
    // helmet's simulated coif, skinned from the record) goes into the chest
    // with it. Rewritten after every engine pose, which reposes the head.
    void sink_root(uintptr_t skeleton, const Severed& s)
    {
        if (s.sink <= 0) return;
        uint8_t* root = record_of(skeleton, s.root);
        uint8_t* parent = record_of(skeleton, s.parent);
        uint8_t* grand = s.grand >= 0 ? record_of(skeleton, s.grand) : nullptr;
        if (!root || !parent) return;
        const float* p = reinterpret_cast<const float*>(parent + 0x14);
        float ux = 0, uy = 0, uz = 1;
        if (grand)
        {
            const float* g = reinterpret_cast<const float*>(grand + 0x14);
            float dx = p[0] - g[0], dy = p[1] - g[1], dz = p[2] - g[2];
            float n = std::sqrt(dx * dx + dy * dy + dz * dz);
            if (n > 1e-3f) { ux = dx / n; uy = dy / n; uz = dz / n; }
        }
        const float target[3] = { p[0] - ux * s.sink, p[1] - uy * s.sink, p[2] - uz * s.sink };
        auto lock = reinterpret_cast<volatile long*>(root);
        int spins = 0;
        while (_interlockedbittestandset(lock, 0)) { _mm_pause(); if (++spins > 100000) return; }
        std::memcpy(root + 0x14, target, sizeof target);
        _InterlockedExchange(lock, 0);
    }

    // Collapses one bone's palette matrix onto its severed limb's root.
    void collapse_severed(void* skeleton, int8_t bone)
    {
        const auto value = reinterpret_cast<uintptr_t>(skeleton);
        if (bone < 0 || bone >= 64) return;
        for (int i = 0; i < MaxSevered; i++)
        {
            if (severed[i].skeleton != value || !(severed[i].bones & (1ull << bone))) continue;
            if (bone == severed[i].root) sink_root(value, severed[i]);
            uint8_t* module = engine_base();
            uint8_t* root = module ? record_of(value, severed[i].root) : nullptr;
            if (!root) return;
            const float* p = reinterpret_cast<const float*>(root + 0x14);
            collapse_snapshot(static_cast<uint8_t*>(skeleton), bone, p);
            float* entry = palette_entry(module, static_cast<uint8_t*>(skeleton), bone);
            if (!entry) return;
            for (int k = 0; k < 12; k++) entry[k] = 0;
            entry[12] = p[0]; entry[13] = p[1]; entry[14] = p[2];
            return;
        }
    }

    void __fastcall refresh_hook(void* skeleton, int8_t bone)
    {
        original_refresh(skeleton, bone);
        collapse_severed(skeleton, bone);
    }

    // The other palette writer: 0x35e450(skeleton, frame, flag) poses every
    // bone of an animated skeleton and fills their palette matrices itself,
    // without 0x361010. A beheaded body still in its death animation was drawn
    // with its helmet until something (the player's blade) moved it into the
    // ragdoll path. Three register arguments, no stack argument, one return.
    using PoseBones = void(__fastcall*)(void* skeleton, void* frame, uint8_t flag);
    const uint8_t PoseCode[] = { 0x48, 0x8b, 0xc4, 0x55, 0x57, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8d, 0xa8, 0x08, 0xfc, 0xff, 0xff };
    constexpr uintptr_t PoseRva = 0x35e450;
    PoseBones original_pose = nullptr;
    bool pose_hooked = false;

    void __fastcall pose_hook(void* skeleton, void* frame, uint8_t flag)
    {
        original_pose(skeleton, frame, flag);
        const auto value = reinterpret_cast<uintptr_t>(skeleton);
        for (int i = 0; i < MaxSevered; i++)
        {
            if (severed[i].skeleton != value) continue;
            const uint64_t bones = severed[i].bones;
            for (int b = 0; b < 64; b++) if (bones & (1ull << b)) collapse_severed(skeleton, static_cast<int8_t>(b));
        }
    }

    // Detour: copy `copy` bytes of position-independent prologue to a
    // trampoline that jumps back, then swap the 16-byte-aligned head for
    // mov rax, hook / jmp rax in one compare-exchange.
    uint8_t* detour(uint8_t* target, size_t copy, const void* hook)
    {
        if ((reinterpret_cast<uintptr_t>(target) & 15) != 0 || copy < 12 || copy > 16) return nullptr;
        auto tramp = static_cast<uint8_t*>(VirtualAlloc(nullptr, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE));
        if (!tramp) return nullptr;
        std::memcpy(tramp, target, copy);
        const uint8_t back[] = { 0xff, 0x25, 0, 0, 0, 0 }; // jmp qword ptr [rip+0]
        std::memcpy(tramp + copy, back, sizeof back);
        const uintptr_t resume = reinterpret_cast<uintptr_t>(target) + copy;
        std::memcpy(tramp + copy + sizeof back, &resume, 8);
        FlushInstructionCache(GetCurrentProcess(), tramp, 64);

        alignas(16) uint8_t patch[16];
        std::memcpy(patch, target, 16);
        patch[0] = 0x48; patch[1] = 0xb8;   // mov rax, imm64
        const uintptr_t address = reinterpret_cast<uintptr_t>(hook);
        std::memcpy(patch + 2, &address, 8);
        patch[10] = 0xff; patch[11] = 0xe0; // jmp rax
        for (size_t k = 12; k < copy; k++) patch[k] = 0xcc;
        DWORD old = 0;
        if (!VirtualProtect(target, 16, PAGE_EXECUTE_READWRITE, &old)) { VirtualFree(tramp, 0, MEM_RELEASE); return nullptr; }
        alignas(16) long long expected[2], desired[2];
        std::memcpy(expected, target, 16);
        std::memcpy(desired, patch, 16);
        const bool swapped = _InterlockedCompareExchange128(reinterpret_cast<volatile long long*>(target),
            desired[1], desired[0], expected) != 0;
        DWORD ignored = 0;
        VirtualProtect(target, 16, old, &ignored);
        FlushInstructionCache(GetCurrentProcess(), target, 16);
        if (!swapped) { VirtualFree(tramp, 0, MEM_RELEASE); return nullptr; }
        return tramp;
    }

    bool install_hook()
    {
        uint8_t* module = engine_base();
        if (!module) return false;
        if (!hooked)
        {
            trampoline = detour(module + RefreshRva, 12, reinterpret_cast<const void*>(&refresh_hook));
            if (!trampoline) return false;
            original_refresh = reinterpret_cast<RefreshBone>(trampoline);
            hooked = true;
        }
        if (!pose_hooked && readable(module + PoseRva, sizeof PoseCode) &&
            std::memcmp(module + PoseRva, PoseCode, sizeof PoseCode) == 0)
        {
            uint8_t* tramp = detour(module + PoseRva, 16, reinterpret_cast<const void*>(&pose_hook));
            if (tramp) { original_pose = reinterpret_cast<PoseBones>(tramp); pose_hooked = true; }
        }
        return hooked;
    }
}

// A severed limb: these bones of this skeleton are collapsed onto the root
// bone's drawn position each time the engine refreshes them. Installs the
// hook on first use.
int32_t BVR_CALL bvr_limb_sever_sunk(uintptr_t skeleton_value, int32_t root, uint64_t bones, int32_t parent, int32_t grand, float sink);
int32_t BVR_CALL bvr_limb_sever(uintptr_t skeleton_value, int32_t root, uint64_t bones)
{
    return bvr_limb_sever_sunk(skeleton_value, root, bones, -1, -1, 0);
}

// As bvr_limb_sever, and the root also sunk `sink` metres down the stump
// (parent bone, along grand -> parent) in its record each time it is drawn.
int32_t BVR_CALL bvr_limb_sever_sunk(uintptr_t skeleton_value, int32_t root, uint64_t bones, int32_t parent, int32_t grand, float sink)
{
    if (sink > 0 && !record_of(skeleton_value, parent)) sink = 0;
    if (!record_of(skeleton_value, root)) return -21;
    if (!install_hook()) return -25;
    int free_slot = -1;
    for (int i = 0; i < MaxSevered; i++)
    {
        if (severed[i].skeleton == skeleton_value && severed[i].root == root)
        {
            severed[i].parent = parent; severed[i].grand = grand; severed[i].sink = sink; severed[i].bones = bones;
            return BVR_OK;
        }
        if (severed[i].skeleton == 0 && free_slot < 0) free_slot = i;
    }
    if (free_slot < 0) return -26;
    severed[free_slot].root = root;
    severed[free_slot].bones = bones;
    severed[free_slot].parent = parent; severed[free_slot].grand = grand; severed[free_slot].sink = sink;
    _InterlockedExchange64(reinterpret_cast<volatile long long*>(&severed[free_slot].skeleton), static_cast<long long>(skeleton_value));
    return BVR_OK;
}

// Forgets every severed limb of a skeleton (before its visuals go away).
int32_t BVR_CALL bvr_limb_forget(uintptr_t skeleton_value)
{
    for (int i = 0; i < MaxSevered; i++)
        if (severed[i].skeleton == skeleton_value)
            _InterlockedExchange64(reinterpret_cast<volatile long long*>(&severed[i].skeleton), 0);
    return BVR_OK;
}
