#include "bvr_crash.h"
#include "bvr_log.h"
#include "d3d_hooks.h"

#include <windows.h>
#include <psapi.h>

#include <atomic>
#include <cstdio>
#include <cstring>

namespace bvr {
namespace {

PVOID g_handle = nullptr;

/* The managed breadcrumb: what our C# last entered, and when. Written from the
   game thread, read from whatever thread faults - relaxed atomics are exactly
   enough, and a handler must not take a lock. */
std::atomic<int>      g_crumb{ 0 };
std::atomic<uint64_t> g_crumbAt{ 0 };
std::atomic<uint64_t> g_crumbCount{ 0 };

/* Mirrors the BVR_CRUMB_* ids in bvr_api.h. */
const char* crumb_name(int id)
{
    switch (id)
    {
    case 0:  return "idle (nothing of ours in flight)";
    case 1:  return "OnApplicationTick";
    case 2:  return "CameraPatch (MissionScreen.UpdateCamera)";
    case 3:  return "VrAfrRenderer.Tick";
    case 4:  return "MissionTickPatch (AfterMissionTick)";
    case 5:  return "SceneRenderingPatch (OnSceneRenderingStarted)";
    case 6:  return "VrStereoRenderer.EnsureCreated";
    case 7:  return "VrWeaponHands";
    case 8:  return "VrBodyHide";
    case 9:  return "VrEngineAim";
    default: return "unknown";
    }
}

/* Mono uses access violations for its own null checks, so the log would drown
   without a filter. Only faults inside the engine's own module are of interest,
   and only the first few of those. */
std::atomic<int> g_reported{0};
constexpr int kMaxReports = 3;

/* Resolves an address to "module+0xRVA". Never throws, never allocates. */
void describe(void* address, char* out, size_t outSize)
{
    HMODULE module = nullptr;

    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                           GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           static_cast<LPCSTR>(address), &module) == 0 || module == nullptr)
    {
        _snprintf_s(out, outSize, _TRUNCATE, "%p (no module)", address);
        return;
    }

    char path[MAX_PATH] = {};
    if (GetModuleFileNameA(module, path, sizeof(path)) == 0)
    {
        _snprintf_s(out, outSize, _TRUNCATE, "%p (unnamed)", address);
        return;
    }

    /* 92 is the backslash, spelled numerically so no layer of quoting can eat it. */
    const char kSep = 92;
    const char* name = std::strrchr(path, kSep);
    name = (name != nullptr) ? name + 1 : path;

    const uintptr_t rva = reinterpret_cast<uintptr_t>(address) -
                          reinterpret_cast<uintptr_t>(module);

    _snprintf_s(out, outSize, _TRUNCATE, "%s+0x%llX", name,
                static_cast<unsigned long long>(rva));
}

bool address_in_engine(void* address)
{
    HMODULE module = nullptr;
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                           GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           static_cast<LPCSTR>(address), &module) == 0 || module == nullptr)
        return false;

    char path[MAX_PATH] = {};
    if (GetModuleFileNameA(module, path, sizeof(path)) == 0)
        return false;

    /* Ours counts too: if we ever fault, that is worth every bit as much. */
    return std::strstr(path, "TaleWorlds.Native.dll") != nullptr ||
           std::strstr(path, "BannerlordVR.Native.dll") != nullptr;
}

/* Reports the mapped-module return addresses lying on the faulting thread's
   stack. See the note at the call site for why this exists and what it is and
   is not worth. Bounded hard: a stack can be a megabyte and this runs inside an
   exception handler. */
void scan_stack(EXCEPTION_POINTERS* info)
{
    if (info == nullptr || info->ContextRecord == nullptr)
        return;

    const uintptr_t rsp = static_cast<uintptr_t>(info->ContextRecord->Rsp);
    if (rsp == 0)
        return;

    /* The real extent of this thread's stack, so the scan cannot walk off it. */
    MEMORY_BASIC_INFORMATION mbi = {};
    if (VirtualQuery(reinterpret_cast<LPCVOID>(rsp), &mbi, sizeof(mbi)) == 0)
        return;

    const uintptr_t base = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
    const uintptr_t top  = base + mbi.RegionSize;

    constexpr int kMaxWords  = 4096;   /* 32 KB of stack, deep enough for any frame */
    constexpr int kMaxPrint  = 24;

    int printed = 0;
    bool sawOurs = false;

    BVR_ERR("  stack scan (return addresses lying on the stack; some are stale):");

    for (int i = 0; i < kMaxWords && printed < kMaxPrint; ++i)
    {
        const uintptr_t slot = rsp + static_cast<uintptr_t>(i) * sizeof(void*);
        if (slot + sizeof(void*) > top)
            break;

        void* value = *reinterpret_cast<void* const*>(slot);
        if (value == nullptr)
            continue;

        HMODULE module = nullptr;
        if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                               GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                               static_cast<LPCSTR>(value), &module) == 0 ||
            module == nullptr)
            continue;

        char frame[256] = {};
        describe(value, frame, sizeof(frame));

        if (std::strstr(frame, "BannerlordVR.Native.dll") != nullptr)
            sawOurs = true;

        BVR_ERR("    [%04d] %s", i, frame);
        ++printed;
    }

    BVR_ERR("  BannerlordVR.Native.dll %s in this thread's stack.",
            sawOurs ? "DOES appear" : "does NOT appear anywhere");
}

LONG CALLBACK on_exception(EXCEPTION_POINTERS* info)
{
    if (info == nullptr || info->ExceptionRecord == nullptr)
        return EXCEPTION_CONTINUE_SEARCH;

    const EXCEPTION_RECORD* record = info->ExceptionRecord;

    if (record->ExceptionCode != EXCEPTION_ACCESS_VIOLATION)
        return EXCEPTION_CONTINUE_SEARCH;

    if (!address_in_engine(record->ExceptionAddress))
        return EXCEPTION_CONTINUE_SEARCH;

    if (g_reported.fetch_add(1, std::memory_order_relaxed) >= kMaxReports)
        return EXCEPTION_CONTINUE_SEARCH;

    char where[256] = {};
    describe(record->ExceptionAddress, where, sizeof(where));

    const ULONG_PTR kind = record->NumberParameters >= 1 ? record->ExceptionInformation[0] : 0;
    const ULONG_PTR addr = record->NumberParameters >= 2 ? record->ExceptionInformation[1] : 0;

    BVR_ERR("ACCESS VIOLATION at %s on thread %lu: tried to %s address 0x%llX.",
            where, GetCurrentThreadId(),
            kind == 0 ? "read" : (kind == 1 ? "write" : "execute"),
            static_cast<unsigned long long>(addr));

    /* WHERE THE FAULTING THREAD WAS, WHICH IS THE PART THAT WAS MISSING.
     *
     * A fault offset inside rgl has now been chased twice on nothing but the
     * offset, and the offset does not distinguish the two cases that matter:
     * the engine faulting underneath OUR render-thread work, or the engine
     * faulting on its own thread while we merely happened to be running. Those
     * have completely different suspects and the log said nothing either way.
     *
     * These three lines settle it, and they are free. */
    const uint32_t me = GetCurrentThreadId();
    const uint32_t presentThread = present_thread_id();

    BVR_ERR("  thread: faulting %lu, Present hook runs on %lu -> %s",
            me, presentThread,
            presentThread == 0 ? "Present has never run"
            : (me == presentThread ? "SAME THREAD as our frame work"
                                   : "a DIFFERENT thread from our frame work"));

    BVR_ERR("  inside our Present tick on this thread: %s",
            in_present_on_this_thread() ? "YES - the fault is underneath our own "
                                          "D3D work, not merely alongside it"
                                        : "no - we were not on the stack here");

    BVR_ERR("  presented frames so far: %llu",
            static_cast<unsigned long long>(present_frame_count()));

    /* THE MANAGED HALF OF THE SAME QUESTION.
     *
       The stack scan below can only see NATIVE modules. Our Harmony patches are
       JIT-compiled, so on the stack they are bare addresses in no module at all
       - indistinguishable from the game's own managed code, which is exactly
       what every report of this crash has ended with. The scan saying our DLL
       is absent therefore clears our native code and says nothing whatever
       about our C#.
     *
       This does. An age of hundreds of milliseconds means our patches were not
       running when the engine faulted; an age of nought names the one that was. */
    const uint64_t crumbAt = g_crumbAt.load(std::memory_order_relaxed);
    const uint64_t now = GetTickCount64();

    BVR_ERR("  last managed VR code entered: %s, %llu ms ago (%llu stamps total)",
            crumb_name(g_crumb.load(std::memory_order_relaxed)),
            static_cast<unsigned long long>(crumbAt == 0 ? 0 : now - crumbAt),
            static_cast<unsigned long long>(g_crumbCount.load(std::memory_order_relaxed)));

    /* The handler runs on the faulting thread with its frames still on the
       stack, so this walks out through whatever called into the engine. */
    void* frames[32] = {};
    const USHORT count = RtlCaptureStackBackTrace(0, 32, frames, nullptr);

    for (USHORT i = 0; i < count; ++i)
    {
        char frame[256] = {};
        describe(frames[i], frame, sizeof(frame));
        BVR_ERR("    #%02u  %s", i, frame);
    }

    BVR_ERR("  (end of stack, %u frames)", count);

    /* THE UNWINDER GIVES UP AT THE FIRST JIT FRAME, AND THAT IS WHERE THE
     * INTERESTING PART STARTS.
     *
     * Every report so far has ended at a "no module" address - Mono's JIT
     * output, which carries no unwind data, so RtlCaptureStackBackTrace cannot
     * step past it. Six frames, four of them our own handler and ntdll's
     * dispatch. Useless.
     *
     * So this reads the raw stack instead and reports the words that look like
     * return addresses into a MAPPED module. It is a heuristic - stale slots
     * from earlier calls survive on a stack and will show up too - which is why
     * it is labelled a scan and not a stack. But it answers the one question
     * the real unwinder could not: does BannerlordVR.Native.dll appear in this
     * thread's history at all? If it never does, our code is not in the call
     * chain and the fault is the engine's own, reached some other way. */
    scan_stack(info);

    /* Let the engine's own crash handler proceed as usual. */
    return EXCEPTION_CONTINUE_SEARCH;
}

} // namespace

void set_breadcrumb(int id)
{
    g_crumb.store(id, std::memory_order_relaxed);
    g_crumbAt.store(GetTickCount64(), std::memory_order_relaxed);
    g_crumbCount.fetch_add(1, std::memory_order_relaxed);
}

void install_crash_logger()
{
    if (g_handle != nullptr)
        return;

    /* First in the chain, so the stack is untouched when we see it. */
    g_handle = AddVectoredExceptionHandler(1, &on_exception);

    if (g_handle != nullptr)
        BVR_INFO("Crash logger installed (access violations inside the engine).");
    else
        BVR_WARN("AddVectoredExceptionHandler failed; no crash stacks will be logged.");
}

void remove_crash_logger()
{
    if (g_handle == nullptr)
        return;

    RemoveVectoredExceptionHandler(g_handle);
    g_handle = nullptr;
}

} // namespace bvr
