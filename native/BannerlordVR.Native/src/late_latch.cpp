#include "late_latch.h"

#include "bvr_config.h"
#include "bvr_log.h"
#include "d3d_hooks.h"
#include "vp_patch.h"
#include "xr_context.h"

#include <windows.h>
#include <intrin.h>

#include <MinHook.h>

#include <atomic>
#include <math.h>
#include <string.h>

#pragma intrinsic(_ReturnAddress)

namespace bvr {
namespace {

/* See late_latch.h for where these come from. */
constexpr uintptr_t kRvaBuilder       = 0x28C210;
constexpr uintptr_t kRvaSceneViewVtbl = 0xB03360;

/* THE SCENE CAMERA IS NAMED BY ITS CALLER, NOT ITS CLASS (TEST 167, run 2).
   The object the builder receives is a plain render-view struct - its first
   qword is flags, not a vptr (0x38006BA3B0001) - so no vtable can match it.
   What identifies the scene exactly is the call site: the return address
   +0x3656F0 is inside sub_180365120, called by rglScene_view::render, and the
   frame built there sat 0.06 m from the published engine camera on every
   report while every other caller was hundreds of metres away. */
constexpr uintptr_t kRvaSceneCallerRet = 0x3656F0;
constexpr size_t    kOffFrame         = 0xF0;     /* 4 x float4 */

/* The builder's first 19 bytes: mov r11,rsp / push rbp / push rbx /
   lea rbp,[r11-298h] / sub rsp,388h. A game update that moves or rewrites the
   function fails this, and the hook is refused rather than planted in the
   middle of whatever now lives there. */
const uint8_t kSig[] = { 0x4C, 0x8B, 0xDC, 0x55, 0x53, 0x49, 0x8D, 0xAB, 0x68, 0xFD,
                         0xFF, 0xFF, 0x48, 0x81, 0xEC, 0x88, 0x03, 0x00, 0x00 };

/* Four register arguments passed straight through: the builder is only seen
   using rcx, but forwarding what it does not use costs nothing and assuming it
   uses nothing else would be a guess. */
using PFN_Builder = uintptr_t (*)(void*, void*, void*, void*);

PFN_Builder g_orig = nullptr;
uintptr_t   g_base = 0;
bool        g_tried = false;
bool        g_hooked = false;

bool probe_on()
{
    static bool cached = false, have = false;
    if (!have) { have = true; cached = config_bool("late_latch_probe", false); }
    return cached;
}

bool latch_on()
{
    static bool cached = false, have = false;
    if (!have) { have = true; cached = config_bool("late_latch", false); }
    return cached;
}

/* ---- what the detour saw, for the report --------------------------------- */

std::atomic<uint64_t> g_calls{0};
std::atomic<uint64_t> g_sceneCalls{0};
std::atomic<uint64_t> g_sceneMaxPerFrame{0};
std::atomic<uint64_t> g_sceneThisFrame{0};
std::atomic<uint64_t> g_sceneFrameId{~0ull};

/* Tiny spinlock around the tables below. The detour may run on the engine's
   main thread or its render thread - finding out which is one of the things
   this probe is for - so nothing here may assume one writer. */
std::atomic_flag g_lock = ATOMIC_FLAG_INIT;
struct SpinGuard
{
    SpinGuard()  { while (g_lock.test_and_set(std::memory_order_acquire)) { YieldProcessor(); } }
    ~SpinGuard() { g_lock.clear(std::memory_order_release); }
};

constexpr int kRows = 8;
struct Caller { uintptr_t ret; uint32_t thread; uint64_t hits; };
Caller g_callers[kRows] = {};
int    g_callerCount = 0;

float    g_lastFrame[16] = {};
uint64_t g_lastFrameAt = 0;
bool     g_haveFrame = false;
uint32_t g_lastBuilderThread = 0;

/* EVERY CLASS THE BUILDER IS HANDED, NOT ONLY THE ONE WE EXPECTED.
 *
   The first probe run filtered on rglScene_view's vtable and matched nothing in
   3600 builds per 5 s - so the object passed to the builder is not that class
   (a derived view, or a render-view sub-object with its own vptr). Rather than
   guess the next class, list them all with where their camera sits; the one at
   the published engine camera is the scene. */
struct VtRow
{
    uintptr_t vt;
    uint64_t  hits;
    uintptr_t ret;
    uint32_t  thread;
    float     frame[16];
};
constexpr int kVtRows = 16;
VtRow g_vts[kVtRows] = {};
int   g_vtCount = 0;

void note_vtable(void* view, uintptr_t vt, uintptr_t ret)
{
    const float* f = reinterpret_cast<const float*>(static_cast<uint8_t*>(view) + kOffFrame);
    SpinGuard guard;
    int i = 0;
    for (; i < g_vtCount; ++i)
        if (g_vts[i].vt == vt)
            break;
    if (i == g_vtCount)
    {
        if (g_vtCount >= kVtRows)
            return;
        g_vts[g_vtCount++] = VtRow{ vt, 0, 0, 0, {} };
    }
    VtRow& row = g_vts[i];
    ++row.hits;
    row.ret = ret;
    row.thread = GetCurrentThreadId();
    memcpy(row.frame, f, sizeof(row.frame));
}

void note_scene_call(void* view, uintptr_t ret)
{
    g_sceneCalls.fetch_add(1, std::memory_order_relaxed);

    const uint64_t frame = present_frame_count();
    if (g_sceneFrameId.exchange(frame, std::memory_order_relaxed) != frame)
        g_sceneThisFrame.store(0, std::memory_order_relaxed);
    const uint64_t n = g_sceneThisFrame.fetch_add(1, std::memory_order_relaxed) + 1;
    uint64_t seen = g_sceneMaxPerFrame.load(std::memory_order_relaxed);
    while (n > seen && !g_sceneMaxPerFrame.compare_exchange_weak(seen, n)) { }

    const uint32_t thread = GetCurrentThreadId();
    const float* f = reinterpret_cast<const float*>(static_cast<uint8_t*>(view) + kOffFrame);

    SpinGuard guard;
    int i = 0;
    for (; i < g_callerCount; ++i)
        if (g_callers[i].ret == ret && g_callers[i].thread == thread)
            break;
    if (i == g_callerCount && g_callerCount < kRows)
        g_callers[g_callerCount++] = Caller{ ret, thread, 0 };
    if (i < g_callerCount)
        ++g_callers[i].hits;

    memcpy(g_lastFrame, f, sizeof(g_lastFrame));
    g_lastFrameAt = frame;
    g_haveFrame = true;
    g_lastBuilderThread = thread;
}

/* ---- the latch (TEST 168) --------------------------------------------------
 *
   Measured by the probe (TEST 167): the engine frame's rows are RIGHT, UP,
   BACKWARD - the OpenXR view axes x, y, z exactly, right-handed. So with
   rotations written as matrices whose COLUMNS are an eye's local x/y/z axes:
 *
     world  = W * stage          for every eye of one publication, where
     W      = Mworld(pub eye) * Rstage(used pose of that eye)^T
 *
   W carries the anchor (body yaw), the stage->world axis change and nothing
   else, so the fresh eye rotation in world is W * Rstage(fresh). That replaces
   the frame's three rows; the position is left as the engine has it (the
   position error inside one frame is millimetres, the rotation is degrees). */
float dot3f(const float* a, const float* b) { return a[0]*b[0] + a[1]*b[1] + a[2]*b[2]; }
float len3f(const float* a) { return sqrtf(dot3f(a, a)); }

struct M3 { float m[3][3]; };

M3 m3_from_quat(const BvrQuat& q)
{
    const float x = q.x, y = q.y, z = q.z, w = q.w;
    M3 r;
    r.m[0][0] = 1 - 2*(y*y + z*z); r.m[0][1] = 2*(x*y - z*w);     r.m[0][2] = 2*(x*z + y*w);
    r.m[1][0] = 2*(x*y + z*w);     r.m[1][1] = 1 - 2*(x*x + z*z); r.m[1][2] = 2*(y*z - x*w);
    r.m[2][0] = 2*(x*z - y*w);     r.m[2][1] = 2*(y*z + x*w);     r.m[2][2] = 1 - 2*(x*x + y*y);
    return r;
}

M3 m3_from_cols(const float* cx, const float* cy, const float* cz, float zSign)
{
    M3 r;
    for (int i = 0; i < 3; ++i)
    {
        r.m[i][0] = cx[i];
        r.m[i][1] = cy[i];
        r.m[i][2] = zSign * cz[i];
    }
    return r;
}

M3 m3_mul(const M3& a, const M3& b)
{
    M3 r;
    for (int i = 0; i < 3; ++i)
        for (int j = 0; j < 3; ++j)
            r.m[i][j] = a.m[i][0]*b.m[0][j] + a.m[i][1]*b.m[1][j] + a.m[i][2]*b.m[2][j];
    return r;
}

M3 m3_mul_bt(const M3& a, const M3& b)   /* a * transpose(b) */
{
    M3 r;
    for (int i = 0; i < 3; ++i)
        for (int j = 0; j < 3; ++j)
            r.m[i][j] = a.m[i][0]*b.m[j][0] + a.m[i][1]*b.m[j][1] + a.m[i][2]*b.m[j][2];
    return r;
}

/* Angle of the rotation taking a to b, degrees. */
float m3_angle_deg(const M3& a, const M3& b)
{
    float tr = 0.0f;
    for (int i = 0; i < 3; ++i)
        for (int j = 0; j < 3; ++j)
            tr += a.m[i][j] * b.m[i][j];
    float c = (tr - 1.0f) * 0.5f;
    if (c > 1.0f) c = 1.0f;
    if (c < -1.0f) c = -1.0f;
    return acosf(c) * 57.29578f;
}

bool finite_m3(const M3& a)
{
    for (int i = 0; i < 3; ++i)
        for (int j = 0; j < 3; ++j)
            if (!isfinite(a.m[i][j]))
                return false;
    return true;
}

constexpr float kLatchEyeRadius = 0.2f;   /* metres: frame must sit at a published eye */
constexpr float kLatchMaxDeg    = 10.0f;  /* anything larger is a mismatch, not latency */

std::atomic<uint64_t> g_latched{0};
std::atomic<uint64_t> g_skipNoPub{0}, g_skipFar{0}, g_skipBase{0}, g_skipBig{0}, g_skipNoPose{0};
std::atomic<uint64_t> g_baseSumUdeg{0}, g_baseMaxUdeg{0}, g_latchSumUdeg{0}, g_latchMaxUdeg{0};
std::atomic<bool>     g_latchLogged{false};

void atomic_max(std::atomic<uint64_t>& a, uint64_t v)
{
    uint64_t seen = a.load(std::memory_order_relaxed);
    while (v > seen && !a.compare_exchange_weak(seen, v)) { }
}

/* ---- WHICH BUILD WAS THIS FRAME DRAWN FROM? (TEST 178) ----------------------
 *
   The builder runs a frame ahead of the capture: frame N is captured only after
   frame N+1 has been built. Under AFR the next build is the other eye, so one
   slot per eye still holds the right one. Under AFW every build is eye 0, the
   slot holds frame N+1's pose when frame N is captured, and the label is a
   frame too new - the world lagging every turn. TEST 176's FIFO fixed that only
   while the pipeline depth held still; a mode switch moved it and the float
   stuck until a restart (TEST 177).
 *
   So nothing is inferred from ORDER any more. Each latched build is kept with
   the rows it wrote, and the render thread identifies the build it is drawing
   by matching the camera rows of the frame's own per_framef upload
   (g_inverse_view at offset 928 - measured from a frame-map capture: rows are
   right, up, back exactly as written at view+0xF0, position in world). The
   capture labels with that build's orientation: exact at any pipeline depth,
   and nothing survives a mode switch or a restart to go stale. */
struct LatchBuild
{
    float    rows[9];   /* right, up, back as written into the frame */
    BvrQuat  fresh;     /* the stage orientation those rows were set to */
    uint32_t eye;
    uint64_t seq;       /* 0 = empty */
};

constexpr int kBuildRing = 8;
LatchBuild g_builds[kBuildRing] = {};
uint64_t   g_buildSeq = 0;

/* The build the render thread most recently matched, and whether a capture has
   taken it yet. Both guarded by g_lock, like the ring. */
LatchBuild g_drawn = {};
bool       g_drawnTaken = true;

std::atomic<uint64_t> g_pairMatched{0}, g_pairFallback{0};

void record_build(const float* f, uint32_t eye, const BvrQuat& fresh)
{
    SpinGuard guard;
    LatchBuild& b = g_builds[g_buildSeq % kBuildRing];
    for (int r = 0; r < 3; ++r)
        for (int c = 0; c < 3; ++c)
            b.rows[r * 3 + c] = f[r * 4 + c];
    b.fresh = fresh;
    b.eye   = eye;
    b.seq   = ++g_buildSeq;
}

void apply_latch(void* view)
{
    float* f = reinterpret_cast<float*>(static_cast<uint8_t*>(view) + kOffFrame);

    VpFrame pub = {}, other = {};
    BvrEyeView used[2] = {}, fresh[2] = {};
    int64_t freshTime = 0;
    if (!vp_debug_published(&pub, &other) || !pub.valid || !other.valid ||
        pub.eye < 0 || pub.eye > 1 || other.eye < 0 || other.eye > 1)
    {
        g_skipNoPub.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    if (!xr_get_used_views(used) || !xr_get_predicted_views(fresh, &freshTime))
    {
        g_skipNoPose.fetch_add(1, std::memory_order_relaxed);
        return;
    }

    /* Which eye is this frame? The published eye whose position it sits at. */
    const float* pos = f + 12;
    const float dPub[3]   = { pos[0] - pub.eyeCamera[12],   pos[1] - pub.eyeCamera[13],   pos[2] - pub.eyeCamera[14] };
    const float dOther[3] = { pos[0] - other.eyeCamera[12], pos[1] - other.eyeCamera[13], pos[2] - other.eyeCamera[14] };
    const float lp = len3f(dPub), lo = len3f(dOther);
    const int   eye = (lp <= lo) ? pub.eye : other.eye;
    if ((lp <= lo ? lp : lo) > kLatchEyeRadius)
    {
        g_skipFar.fetch_add(1, std::memory_order_relaxed);
        return;
    }

    /* W from the newest publication's own eye: world frame and the stage pose it
       was built from came out of the same managed tick. eyeCamera rows are
       right, up, FORWARD, so the z column is the negated forward. */
    const M3 worldPub = m3_from_cols(pub.eyeCamera + 0, pub.eyeCamera + 4, pub.eyeCamera + 8, -1.0f);
    const M3 W = m3_mul_bt(worldPub, m3_from_quat(used[pub.eye].orientation));

    const M3 engine = m3_from_cols(f + 0, f + 4, f + 8, 1.0f);   /* rows are x, y, z already */
    const M3 base   = m3_mul(W, m3_from_quat(used[eye].orientation));
    const M3 target = m3_mul(W, m3_from_quat(fresh[eye].orientation));
    if (!finite_m3(W) || !finite_m3(target))
    {
        g_skipNoPose.fetch_add(1, std::memory_order_relaxed);
        return;
    }

    /* Base check: the frame the engine is about to build from must BE the used
       pose seen through W, up to the latency between them. If it is not, W or the
       axis mapping is wrong, and writing would be damage. */
    const float baseDeg = m3_angle_deg(engine, base);
    const float latchDeg = m3_angle_deg(engine, target);
    if (!(baseDeg < kLatchMaxDeg))
    {
        g_skipBase.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    if (!(latchDeg < kLatchMaxDeg))
    {
        g_skipBig.fetch_add(1, std::memory_order_relaxed);
        return;
    }

    for (int k = 0; k < 3; ++k)
    {
        f[k * 4 + 0] = target.m[0][k];
        f[k * 4 + 1] = target.m[1][k];
        f[k * 4 + 2] = target.m[2][k];
        f[k * 4 + 3] = 0.0f;
    }

    record_build(f, static_cast<uint32_t>(eye), fresh[eye].orientation);
    xr_set_latched_orientation(static_cast<uint32_t>(eye), fresh[eye].orientation);

    g_latched.fetch_add(1, std::memory_order_relaxed);
    const uint64_t bu = static_cast<uint64_t>(baseDeg * 1e6f);
    const uint64_t lu = static_cast<uint64_t>(latchDeg * 1e6f);
    g_baseSumUdeg.fetch_add(bu, std::memory_order_relaxed);
    g_latchSumUdeg.fetch_add(lu, std::memory_order_relaxed);
    atomic_max(g_baseMaxUdeg, bu);
    atomic_max(g_latchMaxUdeg, lu);

    if (!g_latchLogged.exchange(true))
        BVR_INFO("Late latch: FIRST WRITE - eye %d frame rotated %.3f deg to the fresh "
                 "pose (engine vs used pose through W: %.3f deg).", eye, latchDeg, baseDeg);
}

uintptr_t detour(void* a, void* b, void* c, void* d)
{
    g_calls.fetch_add(1, std::memory_order_relaxed);

    /* rglScene_view only: loading screens, tableaus, texture and video views run
       the same builder with cameras that are none of our business. The vtable is
       the class, exactly, with no heuristic about where the camera points. */
    if (a != nullptr)
    {
        const uintptr_t vtbl = *reinterpret_cast<const uintptr_t*>(a);
        const uintptr_t ret = reinterpret_cast<uintptr_t>(_ReturnAddress());
        note_vtable(a, vtbl, ret);
        if (ret - g_base == kRvaSceneCallerRet)
        {
            note_scene_call(a, ret);   /* the engine's frame, before we touch it */
            if (latch_on())
                apply_latch(a);
        }
    }

    return g_orig(a, b, c, d);
}

/* ---- the report ----------------------------------------------------------- */

uint64_t g_nextReport = 0;

void report()
{
    float frame[16] = {};
    Caller callers[kRows] = {};
    int ncallers = 0;
    bool haveFrame = false;
    uint64_t frameAt = 0;
    {
        SpinGuard guard;
        memcpy(frame, g_lastFrame, sizeof(frame));
        haveFrame = g_haveFrame;
        frameAt = g_lastFrameAt;
        ncallers = g_callerCount;
        memcpy(callers, g_callers, sizeof(callers));
        for (int i = 0; i < g_callerCount; ++i) g_callers[i].hits = 0;
    }

    const uint64_t calls = g_calls.exchange(0);
    const uint64_t scene = g_sceneCalls.exchange(0);
    const uint64_t maxPer = g_sceneMaxPerFrame.exchange(0);

    if (latch_on())
    {
        const uint64_t n = g_latched.exchange(0);
        const double baseMean  = n ? g_baseSumUdeg.exchange(0) / 1e6 / n : 0.0;
        const double latchMean = n ? g_latchSumUdeg.exchange(0) / 1e6 / n : 0.0;
        if (!n) { g_baseSumUdeg.store(0); g_latchSumUdeg.store(0); }
        BVR_INFO("Late latch: %llu frame(s) latched in ~5 s; rotation applied mean %.3f / "
                 "max %.3f deg; engine-vs-used check mean %.3f / max %.3f deg; %llu capture(s) "
                 "labelled with the latched pose. Skipped: %llu no publication, %llu no pose, "
                 "%llu not at an eye, %llu base check failed, %llu rotation too large.",
                 static_cast<unsigned long long>(n), latchMean,
                 g_latchMaxUdeg.exchange(0) / 1e6, baseMean, g_baseMaxUdeg.exchange(0) / 1e6,
                 static_cast<unsigned long long>(xr_latched_captures()),
                 static_cast<unsigned long long>(g_skipNoPub.exchange(0)),
                 static_cast<unsigned long long>(g_skipNoPose.exchange(0)),
                 static_cast<unsigned long long>(g_skipFar.exchange(0)),
                 static_cast<unsigned long long>(g_skipBase.exchange(0)),
                 static_cast<unsigned long long>(g_skipBig.exchange(0)));

        const uint64_t matched  = g_pairMatched.exchange(0);
        const uint64_t fellBack = g_pairFallback.exchange(0);
        if (matched != 0 || fellBack != 0)
        {
            BVR_INFO("Late latch pairing (AFW): %llu capture(s) labelled with the build "
                     "they were drawn from, %llu fell back to the newest build. Fallbacks "
                     "should be ~0; a steady stream means the frame's camera upload was "
                     "not matched to a build.",
                     static_cast<unsigned long long>(matched),
                     static_cast<unsigned long long>(fellBack));
        }
    }

    BVR_INFO("Late latch probe: %llu matrix build(s) in ~5 s, %llu of them the scene "
             "view, at most %llu scene build(s) in one frame. Present thread %lu.",
             static_cast<unsigned long long>(calls), static_cast<unsigned long long>(scene),
             static_cast<unsigned long long>(maxPer),
             static_cast<unsigned long>(present_thread_id()));

    for (int i = 0; i < ncallers; ++i)
    {
        const uintptr_t rva = callers[i].ret - g_base;
        BVR_INFO("Late latch probe:   caller TaleWorlds.Native.dll+0x%llX (va 0x%llX) on "
                 "thread %lu%s, %llu call(s) this window",
                 static_cast<unsigned long long>(rva),
                 static_cast<unsigned long long>(0x180000000ull + rva),
                 static_cast<unsigned long>(callers[i].thread),
                 callers[i].thread == present_thread_id() ? " (= Present/render thread)"
                                                          : " (NOT the render thread)",
                 static_cast<unsigned long long>(callers[i].hits));
    }

    /* Every class seen this window, and where its camera sits relative to the
       engine camera managed published. */
    VtRow vts[kVtRows] = {};
    int nvts = 0;
    {
        SpinGuard guard;
        nvts = g_vtCount;
        memcpy(vts, g_vts, sizeof(vts));
        for (int i = 0; i < g_vtCount; ++i) g_vts[i].hits = 0;
    }
    VpFrame pubv = {}, otherv = {};
    const bool havePub = vp_debug_published(&pubv, &otherv) && pubv.valid;
    for (int i = 0; i < nvts; ++i)
    {
        if (vts[i].hits == 0)
            continue;
        const float* p = vts[i].frame + 12;
        float dist = -1.0f;
        if (havePub)
        {
            const float d3[3] = { p[0] - pubv.refPos[0], p[1] - pubv.refPos[1], p[2] - pubv.refPos[2] };
            dist = len3f(d3);
        }
        const uintptr_t vtRva = vts[i].vt - g_base;
        const uintptr_t retRva = vts[i].ret - g_base;
        BVR_INFO("Late latch probe:   class vptr %s0x%llX (va 0x%llX): %llu build(s), caller "
                 "+0x%llX on thread %lu; frame pos (%.3f %.3f %.3f) |rows| %.3f %.3f %.3f; "
                 "%s%.3f m from the published engine camera%s",
                 vts[i].vt >= g_base && vtRva < 0x2000000 ? "TaleWorlds.Native.dll+" : "",
                 static_cast<unsigned long long>(vts[i].vt >= g_base ? vtRva : vts[i].vt),
                 static_cast<unsigned long long>(0x180000000ull + vtRva),
                 static_cast<unsigned long long>(vts[i].hits),
                 static_cast<unsigned long long>(retRva),
                 static_cast<unsigned long>(vts[i].thread),
                 p[0], p[1], p[2],
                 len3f(vts[i].frame), len3f(vts[i].frame + 4), len3f(vts[i].frame + 8),
                 havePub ? "" : "(nothing published) ", dist,
                 havePub && dist >= 0.0f && dist < 0.5f ? "  <-- THE SCENE CAMERA" : "");
    }

    if (!haveFrame)
        return;

    const float* r0 = frame + 0;
    const float* r1 = frame + 4;
    const float* r2 = frame + 8;
    const float* pos = frame + 12;

    BVR_INFO("Late latch probe: camera frame at view+0xF0 (frame %llu): "
             "row0 (%.4f %.4f %.4f %.4f) row1 (%.4f %.4f %.4f %.4f) "
             "row2 (%.4f %.4f %.4f %.4f) pos (%.3f %.3f %.3f %.3f); "
             "|rows| %.4f %.4f %.4f, dots r0r1 %.4f r0r2 %.4f r1r2 %.4f",
             static_cast<unsigned long long>(frameAt),
             r0[0], r0[1], r0[2], r0[3], r1[0], r1[1], r1[2], r1[3],
             r2[0], r2[1], r2[2], r2[3], pos[0], pos[1], pos[2], pos[3],
             len3f(r0), len3f(r1), len3f(r2), dot3f(r0, r1), dot3f(r0, r2), dot3f(r1, r2));

    /* Against what managed published: the engine camera it saw (refPos/refDir)
       and the eye it wanted (eyeCamera, rows right/up/forward/position in its
       own labelling). Which engine row is which axis is ANSWERED here, by the
       best |dot| per row, rather than assumed - the Camera.Frame convention has
       fooled this project before. */
    VpFrame pub = {}, other = {};
    if (!vp_debug_published(&pub, &other) || !pub.valid)
    {
        BVR_INFO("Late latch probe: nothing published to compare against (vp_patch "
                 "or vp_measure must be on for managed to publish).");
        return;
    }

    const float dp[3] = { pos[0] - pub.refPos[0], pos[1] - pub.refPos[1], pos[2] - pub.refPos[2] };
    BVR_INFO("Late latch probe: published engine camera at (%.3f %.3f %.3f) looking "
             "(%.4f %.4f %.4f): frame position is %.3f m from it; dot(refDir, row0/1/2) "
             "= %.4f %.4f %.4f",
             pub.refPos[0], pub.refPos[1], pub.refPos[2],
             pub.refDir[0], pub.refDir[1], pub.refDir[2], len3f(dp),
             dot3f(pub.refDir, r0), dot3f(pub.refDir, r1), dot3f(pub.refDir, r2));

    const float* eyeRows[3] = { pub.eyeCamera + 0, pub.eyeCamera + 4, pub.eyeCamera + 8 };
    const char*  eyeName[3] = { "right", "up", "forward" };
    const float* engRows[3] = { r0, r1, r2 };
    for (int e = 0; e < 3; ++e)
    {
        int best = 0;
        float bestAbs = -1.0f, bestDot = 0.0f;
        for (int k = 0; k < 3; ++k)
        {
            const float dd = dot3f(eyeRows[e], engRows[k]);
            if (fabsf(dd) > bestAbs) { bestAbs = fabsf(dd); bestDot = dd; best = k; }
        }
        const float c = bestAbs > 1.0f ? 1.0f : bestAbs;
        BVR_INFO("Late latch probe:   published eye %-7s ~ engine row%d (dot %+.5f, "
                 "%.3f deg apart) - the rotation a latch would still apply here",
                 eyeName[e], best, bestDot, acosf(c) * 57.29578f);
    }

    const float de[3] = { pos[0] - pub.eyeCamera[12], pos[1] - pub.eyeCamera[13],
                          pos[2] - pub.eyeCamera[14] };
    /* The other eye too. Under AFR the eye drawn alternates every frame, and the
       builder runs on a different thread from the render thread, so the frame
       being built may belong to the OTHER publication - 65 mm is the tell. */
    const float dOther[3] = { pos[0] - other.eyeCamera[12], pos[1] - other.eyeCamera[13],
                              pos[2] - other.eyeCamera[14] };
    BVR_INFO("Late latch probe:   published eye %d position is %.1f mm from the frame "
             "position; the other eye (%d) is %.1f mm from it. Builder on thread %lu, "
             "render thread %lu.",
             pub.eye, len3f(de) * 1000.0f, other.eye,
             other.valid ? len3f(dOther) * 1000.0f : -1.0f,
             static_cast<unsigned long>(g_lastBuilderThread),
             static_cast<unsigned long>(present_thread_id()));
}

} // namespace

bool late_latch_active() { return probe_on() || latch_on(); }

void late_latch_install()
{
    if (g_tried || !late_latch_active())
        return;
    g_tried = true;

    HMODULE mod = GetModuleHandleA("TaleWorlds.Native.dll");
    if (mod == nullptr)
    {
        g_tried = false;   /* not loaded yet - try again next Present */
        return;
    }
    g_base = reinterpret_cast<uintptr_t>(mod);

    void* target = reinterpret_cast<void*>(g_base + kRvaBuilder);
    if (memcmp(target, kSig, sizeof(kSig)) != 0)
    {
        const uint8_t* t = static_cast<const uint8_t*>(target);
        BVR_WARN("Late latch REFUSED: the view matrix builder at TaleWorlds.Native.dll+0x%llX "
                 "does not match its signature (found %02X %02X %02X %02X %02X %02X ...). The game "
                 "has probably been updated; the offsets in late_latch.cpp must be re-derived "
                 "with tools/native_xref.py before this can run. Nothing was hooked.",
                 static_cast<unsigned long long>(kRvaBuilder), t[0], t[1], t[2], t[3], t[4], t[5]);
        return;
    }

    void* tramp = nullptr;
    if (MH_CreateHook(target, reinterpret_cast<void*>(&detour), &tramp) != MH_OK ||
        MH_EnableHook(target) != MH_OK)
    {
        BVR_WARN("Late latch: MinHook refused the hook on the view matrix builder at %p.", target);
        return;
    }

    g_orig = reinterpret_cast<PFN_Builder>(tramp);
    g_hooked = true;
    BVR_INFO("Late latch %s: hooked the view matrix builder TaleWorlds.Native.dll+0x%llX "
             "(signature verified). rglScene_view vtable expected at +0x%llX.",
             latch_on() ? "ARMED" : "probe (read-only)",
             static_cast<unsigned long long>(kRvaBuilder),
             static_cast<unsigned long long>(kRvaSceneViewVtbl));
}

void late_latch_frame_boundary()
{
    if (!g_hooked)
        return;

    const uint64_t frame = present_frame_count();
    if (g_nextReport == 0)
        g_nextReport = frame + 450;
    if (frame < g_nextReport)
        return;
    g_nextReport = frame + 450;

    report();
}

void late_latch_note_upload(const void* data, unsigned int bytes)
{
    /* per_framef only: 1760 bytes, camera frame at 928. Every other upload
       leaves on the size check, so the hook costs nothing for the rest. */
    if (!latch_on() || data == nullptr || bytes != 1760u)
        return;

    const float* v = reinterpret_cast<const float*>(static_cast<const uint8_t*>(data) + 928);
    float rows[9];
    for (int r = 0; r < 3; ++r)
        for (int c = 0; c < 3; ++c)
            rows[r * 3 + c] = v[r * 4 + c];

    /* A unit right axis, or this is not a camera frame at all. */
    const float lr = rows[0] * rows[0] + rows[1] * rows[1] + rows[2] * rows[2];
    if (!(lr > 0.98f && lr < 1.02f))
        return;

    SpinGuard guard;
    int   best = -1;
    float bestErr = 1e30f;
    for (int i = 0; i < kBuildRing; ++i)
    {
        const LatchBuild& b = g_builds[i];
        if (b.seq == 0)
            continue;

        float err = 0.0f;
        for (int k = 0; k < 9; ++k)
        {
            const float d = fabsf(b.rows[k] - rows[k]);
            if (d > err)
                err = d;
        }
        if (err < bestErr || (err == bestErr && best >= 0 && b.seq > g_builds[best].seq))
        {
            bestErr = err;
            best = i;
        }
    }

    /* 2e-4 per element is about 0.01 deg: the engine carries the frame through
       float maths, and one frame of head motion moves the rows far more than
       that. Shadow and other views never come close, so they never match. */
    if (best >= 0 && bestErr < 2e-4f)
    {
        g_drawn = g_builds[best];
        g_drawnTaken = false;
    }
}

bool late_latch_drawn_orientation(unsigned int eye, float outXyzw[4])
{
    if (!latch_on() || outXyzw == nullptr)
        return false;

    SpinGuard guard;
    if (g_drawnTaken || g_drawn.seq == 0 || g_drawn.eye != eye)
    {
        g_pairFallback.fetch_add(1, std::memory_order_relaxed);
        return false;
    }

    g_drawnTaken = true;
    outXyzw[0] = g_drawn.fresh.x;
    outXyzw[1] = g_drawn.fresh.y;
    outXyzw[2] = g_drawn.fresh.z;
    outXyzw[3] = g_drawn.fresh.w;
    g_pairMatched.fetch_add(1, std::memory_order_relaxed);
    return true;
}

} // namespace bvr
