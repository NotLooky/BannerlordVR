#include "d3d_hooks.h"
#include "bvr_api.h"
#include "bvr_log.h"
#include "bvr_config.h"
#include "stereo_dup.h"
#include "stereo_perf.h"

#include <MinHook.h>

#include <cmath>
#include <atomic>
#include <mutex>

namespace bvr {
namespace {

using PFN_Present = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);

PFN_Present    g_originalPresent = nullptr;
PresentTickFn  g_tick            = nullptr;

ID3D11Device*        g_device  = nullptr;
ID3D11DeviceContext* g_context = nullptr;
ID3D11Multithread*   g_multithread = nullptr;
LUID                 g_luid    = {};

std::atomic<uint64_t> g_presentCount{0};
std::atomic<bool>     g_hookInstalled{false};

/* Re-entrancy guard. Our own dummy swapchain (and anything the XR runtime does
   internally) can land back in Present; running the frame loop recursively
   would deadlock the runtime. */
thread_local bool t_inPresent = false;

/* Which thread Present last ran on. Written every frame, read only by the crash
   logger - a plain atomic is exactly enough. */
std::atomic<uint32_t> g_presentThread{ 0 };

/* Set by xr_context when a session starts or stops. Only while this is true is
   xrWaitFrame pacing us, and only then may the engine's own sync interval be
   dropped - outside a session the window is the only display there is and
   presenting it unthrottled would spin the GPU for nothing. */
std::atomic<bool> g_xrPacing{ false };

bool xr_session_running()
{
    return g_xrPacing.load(std::memory_order_relaxed);
}

bool desktop_vsync_wanted()
{
    static int cached = -1;
    if (cached < 0)
        cached = config_float("desktop_vsync", 0.0f) > 0.5f ? 1 : 0;
    return cached != 0;
}

void capture_device(IDXGISwapChain* swapChain)
{
    if (g_device != nullptr || swapChain == nullptr)
        return;

    ID3D11Device* device = nullptr;
    if (FAILED(swapChain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(&device))) ||
        device == nullptr)
    {
        BVR_ERR("Present hook: GetDevice failed; cannot bind OpenXR to the engine device.");
        return;
    }

    g_device = device; /* keep the reference GetDevice gave us */
    g_device->GetImmediateContext(&g_context);

    /* ID3D11DeviceContext is NOT thread safe, and we touch the engine's immediate
       context from inside the Present hook while rgl is driving its own rendering
       (including our two eye SceneViews) elsewhere. That is a data race on the
       context, and it presents exactly as it did here: stable for a minute with
       little else running, then crashing within a second once two extra scene
       renders widen the window - reported by the engine as a memory crash, with
       nothing useful in its own log.

       SetMultithreadProtected makes D3D serialise access internally. It costs a
       lock per call, which is nothing next to a scene render, and it is the
       supported way for injected code to share a device it does not own. */
    if (SUCCEEDED(g_context->QueryInterface(__uuidof(ID3D11Multithread),
                                            reinterpret_cast<void**>(&g_multithread))) &&
        g_multithread != nullptr)
    {
        const BOOL was = g_multithread->SetMultithreadProtected(TRUE);
        BVR_INFO("D3D11 multithread protection enabled (was %s).", was ? "on" : "off");
    }
    else
    {
        g_multithread = nullptr;
        BVR_WARN("ID3D11Multithread unavailable; context access cannot be serialised.");
    }

    /* Adapter LUID, so bvr_create_session can compare against what the OpenXR
       runtime demands. A mismatch here is the difference between "works" and
       "black screen in the headset while the monitor looks fine". */
    IDXGIDevice* dxgiDevice = nullptr;
    if (SUCCEEDED(g_device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgiDevice))) &&
        dxgiDevice != nullptr)
    {
        IDXGIAdapter* adapter = nullptr;
        if (SUCCEEDED(dxgiDevice->GetAdapter(&adapter)) && adapter != nullptr)
        {
            DXGI_ADAPTER_DESC desc = {};
            if (SUCCEEDED(adapter->GetDesc(&desc)))
            {
                g_luid = desc.AdapterLuid;
                BVR_INFO("Engine device captured. Adapter: %ls (LUID %08lX:%08lX, %llu MB)",
                         desc.Description,
                         static_cast<unsigned long>(g_luid.HighPart),
                         static_cast<unsigned long>(g_luid.LowPart),
                         static_cast<unsigned long long>(desc.DedicatedVideoMemory / (1024ull * 1024ull)));
            }
            adapter->Release();
        }

        /* THREE FRAMES OF QUEUED WORK IS THE D3D11 DEFAULT, AND IN VR IT IS
         * LATENCY NOTHING DOWNSTREAM CAN TAKE BACK OUT.
         *
           A flat game wants a deep queue: the CPU runs ahead, the GPU always has
           work waiting, and the only cost is that what you see is a few frames
           behind what the CPU thought. On a monitor that is a throughput win and
           nobody can feel it.
         *
           In a headset it is the whole complaint. Every queued frame is a frame
           between the pose the scene was built from and the photons, and the
           compositor cannot correct for it: reprojection fixes where an image is
           POINTED, using the pose we submit with it, but the image's contents
           were fixed when the engine drew them. Three frames at 72 Hz is 42 ms
           of the world being where it was, arriving with the shape of now. That
           is felt as the view resisting the head and then catching up - the
           thing that separates injected VR from a native title, which asks for
           one frame precisely so this cannot happen.
         *
           1 is what native VR renderers use. The cost is throughput: the CPU can
           no longer run ahead, so a GPU-bound scene may lose frames rather than
           queue them. That is the right trade here - a dropped frame the
           compositor reprojects over is cheaper than every frame arriving late.
         *
           frame_latency = 0 leaves the engine's own setting alone. */
        IDXGIDevice1* dxgi1 = nullptr;
        if (SUCCEEDED(dxgiDevice->QueryInterface(__uuidof(IDXGIDevice1),
                                                 reinterpret_cast<void**>(&dxgi1))) &&
            dxgi1 != nullptr)
        {
            const int want = static_cast<int>(config_float("frame_latency", 1.0f));

            UINT had = 0;
            const bool readBack = SUCCEEDED(dxgi1->GetMaximumFrameLatency(&had));

            if (want > 0)
            {
                if (SUCCEEDED(dxgi1->SetMaximumFrameLatency(static_cast<UINT>(want))))
                {
                    BVR_INFO("Frame latency: %u -> %d queued frame(s). The engine's "
                             "default queues work so the CPU can run ahead, which in "
                             "a headset is that many frames between the pose the "
                             "scene was drawn from and the photons - and the "
                             "compositor cannot reproject it away, because it is the "
                             "image's CONTENTS that are old, not its aim. At %d it "
                             "may drop a frame under GPU load rather than queue one, "
                             "which is the cheaper of the two. frame_latency = 0 "
                             "leaves the engine's own setting alone.",
                             readBack ? had : 0u, want, want);
                }
                else
                {
                    BVR_WARN("Could not set the maximum frame latency; the engine's "
                             "queue depth of %u stands, and that many frames of it "
                             "are photon latency no reprojection can remove.",
                             readBack ? had : 0u);
                }
            }
            else
            {
                BVR_INFO("Frame latency left at the engine's own %u queued frame(s) "
                         "by config.", readBack ? had : 0u);
            }

            dxgi1->Release();
        }

        dxgiDevice->Release();
    }
}

void depth_grave_collect();

HRESULT STDMETHODCALLTYPE hooked_present(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags)
{
    if (!t_inPresent)
    {
        t_inPresent = true;
        depth_grave_collect();

        g_presentThread.store(GetCurrentThreadId(), std::memory_order_relaxed);
        g_presentCount.fetch_add(1, std::memory_order_relaxed);
        capture_device(swapChain);

        /* Native stereo's frame-time breakdown (TEST 181). A load and a return in
           every other mode. */
        stereo_perf_tick_begin(g_context);

        if (g_tick != nullptr && g_device != nullptr)
        {
            /* The tick owns all OpenXR frame calls. It must not throw - the C ABI
               boundary and the engine's render thread both assume noexcept. */
            g_tick(swapChain, g_device, g_context);
        }

        stereo_perf_tick_end(g_context);

        t_inPresent = false;
    }

    /* TWO THINGS CANNOT PACE ONE FRAME LOOP.
     *
       The tick above has just called xrWaitFrame, which is the compositor
       saying "start the next frame now" - that is the whole point of it, and it
       is the only pacer a VR application should have. Then this returns into
       the engine's own Present, which blocks again on the MONITOR's refresh if
       the game is running with vsync.
     *
       Two pacers at different rates do not average out. The headset asks for a
       frame every 13.9 ms and the desktop grants one every 16.7 or 6.9,
       whichever the monitor happens to be, so frames are delivered on the beat
       between the two - regular in neither. What reaches the head is a pose-to
       -photon time that changes from frame to frame, and a latency that varies
       is felt as the view being loose or heavy in a way a constant one is not.
     *
       The headset is the display now, and the window is a courtesy. Presenting
       it with no wait costs a tear nobody is looking at.
     *
       Both reference VR injections on this machine do exactly this - Witcher 3
       VR forces the interval to 0 whenever its session is running, under
       disable_desktop_vsync. desktop_vsync = 1 restores the engine's own
       interval for comparison. */
    UINT effective = syncInterval;

    if (syncInterval != 0 && xr_session_running() && !desktop_vsync_wanted())
    {
        effective = 0;

        static bool logged = false;
        if (!logged)
        {
            logged = true;
            BVR_INFO("Desktop vsync suppressed: the engine asked to Present with "
                     "interval %u while an OpenXR session is running. xrWaitFrame "
                     "is the pacer now; a second one at the monitor's rate only "
                     "adds a wait of a different length to every frame. Set "
                     "desktop_vsync = 1 to hand the interval back.", syncInterval);
        }
    }

    const HRESULT hr = g_originalPresent(swapChain, effective, flags);
    /* Only the engine's own Present: one that lands back in here from inside our
       tick (the dummy swapchain, the runtime) is not the frame's. */
    if (!t_inPresent)
        stereo_perf_present_returned();
    return hr;
}

// ---------------------------------------------------------------------------
// Eye texture correlation
// ---------------------------------------------------------------------------

using PFN_CreateTexture2D = HRESULT(STDMETHODCALLTYPE*)(
    ID3D11Device*, const D3D11_TEXTURE2D_DESC*, const D3D11_SUBRESOURCE_DATA*, ID3D11Texture2D**);

PFN_CreateTexture2D g_originalCreateTexture2D = nullptr;
std::atomic<bool>   g_textureHookInstalled{false};

/* The capture window stays open across frames rather than closing at the end of
   the C# call. rgl queues resource creation onto its own render thread, so the
   CreateTexture2D for an eye target lands well after Texture.CreateRenderTarget
   has returned - a window scoped to that call sees nothing at all.

   Both eyes are captured in ONE window, in creation order: arming per eye would
   race, because a late-arriving eye 0 texture would be recorded as eye 1. */
constexpr int32_t kMaxObservedLogged = 40;

/* Depth targets get their own, larger budget - see the note at the log site. */
constexpr int32_t kMaxDepthsLogged = 24;

struct CaptureState
{
    std::atomic<bool> armed{false};
    uint32_t width = 0;
    uint32_t height = 0;
    int32_t  matches = 0;
    int32_t  observed = 0;          /* every CreateTexture2D seen while armed */
    int32_t  depthsLogged = 0;      /* depth targets, logged past the cap above */
    DXGI_FORMAT capturedFormat = DXGI_FORMAT_UNKNOWN;
    ID3D11Texture2D* textures[2] = { nullptr, nullptr };
};

CaptureState     g_capture;
ID3D11Texture2D* g_eyeTextures[2] = { nullptr, nullptr };

HRESULT STDMETHODCALLTYPE hooked_create_texture2d(
    ID3D11Device* device,
    const D3D11_TEXTURE2D_DESC* desc,
    const D3D11_SUBRESOURCE_DATA* initialData,
    ID3D11Texture2D** outTexture)
{
    const HRESULT hr = g_originalCreateTexture2D(device, desc, initialData, outTexture);

    if (FAILED(hr) || outTexture == nullptr || *outTexture == nullptr || desc == nullptr)
        return hr;

    if (!g_capture.armed.load(std::memory_order_acquire))
        return hr;

    /* Log what the engine really allocates while we are waiting. If the eye
       targets never appear here at all, the engine is not routing through
       ID3D11Device::CreateTexture2D and the whole correlation approach needs
       rethinking - which is worth knowing precisely rather than inferring. */
    const int32_t seen = ++g_capture.observed;
    if (seen <= kMaxObservedLogged)
    {
        BVR_INFO("  CreateTexture2D #%d: %ux%u fmt %d mips %u arr %u bind 0x%X usage %d samples %u",
                 seen, desc->Width, desc->Height, static_cast<int>(desc->Format),
                 desc->MipLevels, desc->ArraySize, desc->BindFlags,
                 static_cast<int>(desc->Usage), desc->SampleDesc.Count);
    }

    /* Depth targets are logged past the general cap, and for as long as we stay
       armed, because AFR's retarget happens long after the first forty textures
       have gone by and its depth buffer is the one we most need to see.

       Why it matters: Texture.CreateDepthTarget hands back D16_UNORM - sixteen
       bits, no stencil - and rgl's own auto-created depth may or may not be
       better. Sixteen bits of depth across a 0.05 m near plane and a 12500 m far
       plane is 250,000:1 of range in 65,536 steps. Which end of the scene that
       starves depends on whether rgl uses a reversed-Z projection, and that is
       exactly the question "far army visible, vanishes as it gets close" is
       asking. Print the format rather than reasoning about it. */
    const bool isDepth = (desc->BindFlags & D3D11_BIND_DEPTH_STENCIL) != 0;
    if (isDepth)
    {
        /* Eye-sized depth is NEVER capped. The previous run burned the whole
           budget on shadow maps during scene load and hit the cap four seconds
           before AFR retargeted, which left the one question we were asking
           unanswered. Everything else still shares a small budget, because the
           engine allocates shadow cascades by the dozen. */
        const bool eyeSized = desc->Width == g_capture.width &&
                              desc->Height == g_capture.height;

        if (eyeSized || g_capture.depthsLogged < kMaxDepthsLogged)
        {
            if (!eyeSized)
                ++g_capture.depthsLogged;

            BVR_INFO("  DEPTH target: %ux%u fmt %d bind 0x%X samples %u%s",
                     desc->Width, desc->Height, static_cast<int>(desc->Format),
                     desc->BindFlags, desc->SampleDesc.Count,
                     eyeSized ? "   <- EYE SIZE" : "");
        }
    }

    const bool isRenderTarget = (desc->BindFlags & D3D11_BIND_RENDER_TARGET) != 0;
    if (!isRenderTarget || desc->Width != g_capture.width || desc->Height != g_capture.height)
        return hr;

    if (g_capture.matches < 2)
    {
        g_capture.textures[g_capture.matches] = *outTexture;
        g_capture.capturedFormat = desc->Format;
        (*outTexture)->AddRef();
    }
    ++g_capture.matches;

    /* The window stays open on purpose, to answer one question: does rgl create
       ANOTHER render target of the eye size after we have taken ours?
       If it does, the texture our SceneView actually draws into is not the one we
       copy from, and the headset shows a frozen image of a texture nothing writes
       to - which is exactly the symptom. */
    /* Only a target of the SAME format counts as a reallocation. The SceneViews
       allocate their own G-buffer and postfx intermediates at eye resolution -
       formats 28, 34, 49 - and matching on size alone reported every one of them
       as a stale-pointer warning. */
    if (g_capture.matches > 2 && g_capture.matches <= 12 &&
        desc->Format == g_capture.capturedFormat)
    {
        BVR_WARN("Eye-sized render target #%d of the SAME format created AFTER "
                 "capture (%p). rgl has reallocated it; our pointer is stale.",
                 g_capture.matches, static_cast<void*>(*outTexture));

        /* THIS USED TO BE A WARNING AND NOTHING ELSE, AND THAT WAS THE WHOLE
           BUG BEHIND "TOGGLING DLSS BROKE THE VIEW AND DISABLING IT AGAIN DID
           NOT FIX IT".
         *
           Changing an upscaler makes rgl tear down its render targets and
           build new ones at a new size. The depth search, however, switches
           itself off after 180 stable frames and holds one texture for the
           rest of the session - so from that moment we were reading a depth
           buffer that nothing writes to any more. A frozen depth map is not a
           subtle error: every disparity comes from geometry that is no longer
           there, so everything doubles, and the holes it opens sit at fixed
           screen positions and look welded to the view. Switching the
           upscaler back could not undo it, because nothing was ever going to
           look for the new buffer.

           A reallocation is exactly the signal that the old answer expired,
           so it re-opens the search rather than merely mentioning it. */
        depth_rearm("rgl reallocated its eye-sized render targets");
    }

    return hr;
}


// ---------------------------------------------------------------------------
// Eye depth capture (AFW)
// ---------------------------------------------------------------------------

using PFN_OMSetRenderTargets = void (STDMETHODCALLTYPE*)(
    ID3D11DeviceContext*, UINT, ID3D11RenderTargetView* const*, ID3D11DepthStencilView*);
using PFN_OMSetRenderTargetsAndUAVs = void (STDMETHODCALLTYPE*)(
    ID3D11DeviceContext*, UINT, ID3D11RenderTargetView* const*, ID3D11DepthStencilView*,
    UINT, UINT, ID3D11UnorderedAccessView* const*, const UINT*);

PFN_OMSetRenderTargets       g_originalOMSetRT = nullptr;
PFN_OMSetRenderTargetsAndUAVs g_originalOMSetRTUAV = nullptr;

std::atomic<bool> g_depthWanted{false};
std::atomic<bool> g_depthHookInstalled{false};

/* THAT COMMENT USED TO SAY "written only from the render thread, read only from
   the Present hook on that same thread. Not atomic because it never crosses
   one." It was wrong, and it cost a crash on entering a mission.
 *
 * There is no such thing as "the" render thread here. These are written from
 * inside the OMSetRenderTargets detour, and the note above
 * clear_backbuffer_if_bound already says what that means: the ENGINE calls it,
 * on whatever threads it likes, hundreds of times a frame. All D3D11 contexts
 * share one vtable, so hooking the immediate context's slot hooks every
 * DEFERRED context with it - and a deferred context is precisely a thing worker
 * threads record into. Scene loading is when rgl has the most of them running
 * at once, which is exactly when the crash landed.
 *
 * The unguarded line was g_eyeDepth->Release(). Two threads that both got past
 * the candidate test both released the same rgl-owned depth texture, taking its
 * refcount one below what rgl still held. D3D freed it, rgl went on using it,
 * and the fault surfaced later as an access violation reading a heap address
 * inside TaleWorlds.Native.dll - nowhere near this file, and with a stack that
 * named nothing. An over-release never faults where it happens.
 *
 * So: one mutex around the whole capture decision. It is taken only while the
 * watch is armed - a few seconds at the start of a mission - and never on the
 * hot path afterwards, because g_depthWanted goes false as soon as the buffer
 * is found and the detour returns before reaching any of this. */
std::mutex g_depthMutex;
ID3D11Texture2D* g_eyeDepth = nullptr;

/* The most recently bound depth that matched the scene's shape, which under AFR
   alternates between the two eyes' buffers. See eye_depth_texture.
 *
   HELD BY REFERENCE (TEST 183). It used to be a bare pointer on the grounds that
   it is read in the frame it was bound in, while the engine still holds it. A
   DLSS preset change breaks that: rgl rebuilds its targets and frees the old
   depth, and the next Present copied from the freed texture - an access
   violation inside nvwgf2umx.dll under afr_capture's depth CopyResource
   (crash dump 02:42:24, TaleWorlds.MountAndBlade.Launcher.exe.24016.dmp).
 *
   A replaced reference is not released on the spot: the bind that replaces it
   can run on another thread while our Present tick is using the old one. It is
   parked in g_depthGrave and released at the start of the next Present, when
   nothing of ours can still be holding the pointer. Guarded by g_depthMutex. */
ID3D11Texture2D* g_depthRecent = nullptr;
constexpr int    kDepthGrave = 32;
ID3D11Texture2D* g_depthGrave[kDepthGrave] = {};
int              g_depthGraveCount = 0;

/* Caller holds g_depthMutex. */
void set_depth_recent(ID3D11Texture2D* tex)
{
    if (tex == g_depthRecent)
        return;
    if (tex != nullptr)
        tex->AddRef();
    if (g_depthRecent != nullptr)
    {
        if (g_depthGraveCount < kDepthGrave)
            g_depthGrave[g_depthGraveCount++] = g_depthRecent;
        else
            g_depthRecent->Release();   /* cannot happen within one frame's binds */
    }
    g_depthRecent = tex;
}

/* Start of every Present: the references replaced since the last one go. */
void depth_grave_collect()
{
    std::lock_guard<std::mutex> lock(g_depthMutex);
    for (int i = 0; i < g_depthGraveCount; ++i)
    {
        g_depthGrave[i]->Release();
        g_depthGrave[i] = nullptr;
    }
    g_depthGraveCount = 0;
}
bool g_loggedDepth = false;
int  g_depthArmedFrames = 0;
bool g_depthGaveUp = false;

/* How long the current choice has survived without a larger one appearing.
   The search no longer stops at the first match - it keeps looking for a bigger
   one - so something has to end it, or the detour would inspect every bind for
   the rest of the mission. */
int  g_depthStableFrames = 0;

/* Depth targets that were tried and found not to be the scene's. Raw pointers
   are only meaningful while the textures live, which is why depth_rearm clears
   this: after a scene change the addresses may be reused by something else
   entirely, and a stale entry would blacklist the RIGHT buffer. */
ID3D11Texture2D* g_depthRejected[8] = {};
int              g_depthRejectedCount = 0;

/* Bumped every time the search is re-opened or a candidate is thrown out, so
   anything CACHED about a depth buffer can tell that its conclusion has
   expired.
 *
   The warp proves a depth buffer contains a drawn scene and remembers the
   answer against the texture's address. But that proof is a statement about
   the buffer's CONTENTS at one moment, and the address only identifies the
   container. rgl reuses the same depth texture across a mission restart: same
   address, same texture, contents cleared back to empty. The pointer compared
   equal, so the proof looked like it still applied, and the warp went live on
   frame one of the new mission against a buffer nothing had drawn into yet.
   That is a mission restart showing a broken eye when the session's first
   mission was fine.

   Pairing the pointer with a counter that only ever moves forwards gives an
   identity that is valid in TIME as well as in space, which is what a claim
   about contents needs. It also covers the case the pointer cannot see at all -
   a genuinely new texture landing at a recycled address. */
std::atomic<uint32_t> g_depthGeneration{0};

/* See the census comment at the bind site. Reported once a second, because the
   question is a ratio over a run and not an event. */
uint32_t g_depthHeldBinds  = 0;
uint32_t g_depthOtherBinds = 0;

void depth_census_tick()
{
    static uint64_t lastMs = 0;

    const uint64_t now = GetTickCount64();
    if (lastMs == 0)
    {
        lastMs = now;
        return;
    }
    if (now - lastMs < 1000)
        return;

    lastMs = now;

    if (g_depthOtherBinds != 0)
    {
        /* INFO, NOT WARN, AND NO LONGER A FAULT REPORT.
         *
           This text outlived the bug it was written for. It says we are "frozen
           on one of them" and therefore reading the wrong eye's depth - which was
           true when the census was added and stopped being true the moment
           eye_depth_texture started handing back g_depthRecent, the buffer bound
           THIS frame, instead of the settled one. The alternation is now expected
           and handled, so a perfect 1:1 split is the healthy reading rather than
           the symptom it used to be.
         *
           It fired 53 times in a single run as a WARN, describing a fault that
           could not happen, which is exactly the kind of line that sends the next
           session debugging the wrong thing. Kept, because "how many scene depths
           does rgl have" is worth knowing; demoted, because the answer is no
           longer alarming. */
        BVR_INFO("AFW depth census: the buffer we settled on was bound %u time(s) "
                 "this second and a DIFFERENT one of identical size and format %u "
                 "time(s) - rgl keeps one scene depth per eye and alternates. "
                 "Expected under AFR, and handled: the warp is given whichever was "
                 "bound this frame, not the settled one. A ~1:1 split is healthy.",
                 g_depthHeldBinds, g_depthOtherBinds);
    }
    else
    {
        BVR_INFO("AFW depth census: the buffer we hold was bound %u time(s) this "
                 "second and no same-sized alternative was ever seen, so it is "
                 "the only scene depth there is and freezing on it is correct.",
                 g_depthHeldBinds);
    }

    g_depthHeldBinds  = 0;
    g_depthOtherBinds = 0;
}

bool depth_is_rejected(ID3D11Resource* tex)
{
    for (int i = 0; i < g_depthRejectedCount; ++i)
        if (static_cast<ID3D11Resource*>(g_depthRejected[i]) == tex)
            return true;
    return false;
}

bool g_depthSettled = false;

/* Does this binding pair our eye colour target with a depth buffer? If so, keep
   the depth. Cheap on purpose - one interface call and a pointer compare, and
   only for the first few binds of each frame. */
/* How many bindings to describe before falling silent. Enough to cover a whole
   frame of a heavy scene without filling the log. */
const int kSurveyLines = 60;
int g_surveyed = 0;

void describe(const char* what, ID3D11Resource* resource)
{
    if (resource == nullptr)
    {
        BVR_INFO("      %s: none", what);
        return;
    }

    ID3D11Texture2D* tex = nullptr;
    if (FAILED(resource->QueryInterface(__uuidof(ID3D11Texture2D),
                                        reinterpret_cast<void**>(&tex))))
    {
        BVR_INFO("      %s: not a 2D texture", what);
        return;
    }

    D3D11_TEXTURE2D_DESC d = {};
    tex->GetDesc(&d);
    tex->Release();

    const bool eyeSized = d.Width == g_capture.width && d.Height == g_capture.height;

    BVR_INFO("      %s: %ux%u fmt %d bind 0x%X%s%s",
             what, d.Width, d.Height, static_cast<int>(d.Format), d.BindFlags,
             eyeSized ? "  <- EYE SIZE" : "",
             (resource == static_cast<ID3D11Resource*>(g_eyeTextures[0]) ||
              resource == static_cast<ID3D11Resource*>(g_eyeTextures[1]))
                 ? "  <- OUR EYE TEXTURE" : "");
}

/* Finds the depth buffer the scene was drawn with.
 *
 * THE FIRST ATTEMPT AT THIS WAS TOO NARROW, and the run said so: not one
 * "depth buffer found" line in the whole session, and the warp declined on
 * every single frame.
 *
 * It looked only for a depth-stencil bound in the same call as OUR eye texture.
 * That assumes rgl renders the scene straight into the target we hand it. It
 * almost certainly does not - a renderer with shadows and postfx draws the
 * scene into its own buffer and only the last resolve lands in ours, and that
 * resolve needs no depth at all. So the one binding we watched for was the one
 * binding guaranteed to have a null depth.
 *
 * So: match on SIZE, not identity. Whatever depth is bound alongside a
 * render target with the eye's dimensions is the scene depth, whoever owns the
 * colour buffer. Our own texture still wins if it ever does appear with a
 * depth, because that is the unambiguous case.
 *
 * And before deciding anything, DESCRIBE what goes past. Sixty bindings of
 * sizes and formats settle where the depth actually lives, and - just as
 * usefully - a survey that prints nothing at all says the detour is never
 * called, which is a completely different problem with a completely different
 * fix. Guessing at this once has already cost a round.
 */
void note_render_targets(ID3D11DeviceContext* context,
                         ID3D11RenderTargetView* const* rtvs, UINT count,
                         ID3D11DepthStencilView* dsv)
{
    if (count == 0 || rtvs == nullptr || rtvs[0] == nullptr || context == nullptr)
        return;

    /* THE IMMEDIATE CONTEXT ONLY - the same rule clear_backbuffer_if_bound
       follows, and for a stronger reason here.
     *
       Two reasons, either of which would be enough. The lesser one is that the
       scene pass we are hunting for is recorded on the immediate context, so
       every deferred bind inspected is work done to reject it. The greater one
       is that deferred contexts are what worker threads record into, and this
       function takes a reference on an engine-owned texture - which is the
       thing that must not happen concurrently from several threads at once.

       The mutex below makes that safe regardless. This makes it rare. */
    if (context->GetType() != D3D11_DEVICE_CONTEXT_IMMEDIATE)
        return;

    /* Held across the whole decision, not just the assignment: the candidate
       test reads g_eyeDepth and the tail releases it, and a lock that spanned
       only one of those would leave the same over-release it is here to stop. */
    std::lock_guard<std::mutex> lock(g_depthMutex);

    /* Re-checked under the lock. The caller tests g_depthWanted unlocked, so
       several threads can arrive here after the winner has already found the
       buffer and cleared it. */
    if (!g_depthWanted.load(std::memory_order_acquire))
        return;

    ID3D11Resource* colour = nullptr;
    rtvs[0]->GetResource(&colour);
    if (colour == nullptr)
        return;

    ID3D11Resource* depth = nullptr;
    if (dsv != nullptr)
        dsv->GetResource(&depth);

    ID3D11Texture2D* colourTex = nullptr;
    colour->QueryInterface(__uuidof(ID3D11Texture2D),
                           reinterpret_cast<void**>(&colourTex));

    D3D11_TEXTURE2D_DESC colourDesc = {};
    if (colourTex != nullptr)
        colourTex->GetDesc(&colourDesc);

    const bool eyeSized = colourTex != nullptr &&
                          colourDesc.Width == g_capture.width &&
                          colourDesc.Height == g_capture.height;
    const bool isOurEye = colour == static_cast<ID3D11Resource*>(g_eyeTextures[0]);

    /* SPENT ON CANDIDATES ONLY, WHICH IT WAS NOT.
     *
       The condition used to be "anything with a depth bound", and a mission
       start binds hundreds of those before the scene pass appears. Every survey
       so far came back sixty lines of 1280x720 - the loading screen and the
       character tableaux - and the whole budget was gone before the buffer we
       actually latch onto was ever reached. So the log could never answer the
       one question it exists for: whether a FULL-resolution scene depth goes
       past that we are declining in favour of a half-resolution one.
     *
       Matching the eye's aspect is what makes a bind a candidate at all, so
       that is what earns a line. Off-aspect buffers are rejected in a moment
       anyway, and describing them costs the only sixty lines we have. */
    const float surveyEyeAspect = g_capture.height != 0
        ? static_cast<float>(g_capture.width) / static_cast<float>(g_capture.height)
        : 0.0f;
    const float surveyAspect = (colourTex != nullptr && colourDesc.Height != 0)
        ? static_cast<float>(colourDesc.Width) / static_cast<float>(colourDesc.Height)
        : 0.0f;
    /* SIZE AS WELL AS ASPECT, because aspect alone was not enough either.
     *
       Filtering on aspect replaced sixty lines of 1280x720 with sixty lines of
       128x128 - shadow cascades and probe faces, which are SQUARE, and the eye
       is square to within 0.7% so they sail through a 1% test. The budget went
       again before a single real candidate was described. A buffer under a
       quarter of the eye's width is not the scene at any resolution, so it does
       not get a line. */
    const bool surveyBigEnough = colourTex != nullptr &&
                                 colourDesc.Width * 4 >= g_capture.width;

    const bool surveyWorthy =
        isOurEye || eyeSized ||
        (depth != nullptr && surveyBigEnough &&
         surveyEyeAspect > 0.0f && surveyAspect > 0.0f &&
         std::fabs(surveyAspect - surveyEyeAspect) < surveyEyeAspect * 0.01f);

    if (g_surveyed < kSurveyLines && surveyWorthy)
    {
        ++g_surveyed;
        BVR_INFO("  bind #%d (%u target(s)):", g_surveyed, count);
        describe("colour", colour);
        describe("depth ", depth);

        if (g_surveyed == kSurveyLines)
            BVR_INFO("  ...survey complete; no more bindings will be described.");
    }

    /* MATCH ON ASPECT RATIO, not on size.
     *
       The survey settled it. rgl renders the scene at 1536x1632 - exactly HALF
       the eye in each dimension - and only the final upscale passes touch our
       3072x3264 target, with no depth bound at all:

           bind #1   colour 1536x1632 fmt 26   depth 1536x1632 fmt 19
           bind #19  colour 3072x3264 fmt 26   depth none    <- EYE SIZE
           bind #21  colour 3072x3264 fmt 87   depth none    <- OUR EYE TEXTURE

       So matching on the eye SIZE was still wrong, for the same reason matching
       on identity was: the pass that owns the depth is not the pass that writes
       our texture, and it never will be.

       Aspect ratio is what actually identifies it. 1536/1632 and 3072/3264 are
       the same number to seven places, because one is a scaled render of the
       other. Shadow maps - the other big depth consumers here - are square, so
       the ratio separates the scene pass from them cleanly, and the size floor
       rejects the small reflection and UI passes.

       First match wins, and then we stop looking for the rest of the frame: the
       scene pass is bound first, and inspecting several hundred more bindings
       would cost real time for nothing. */
    const float eyeAspect = g_capture.height != 0
        ? static_cast<float>(g_capture.width) / static_cast<float>(g_capture.height)
        : 0.0f;
    const float thisAspect = (colourTex != nullptr && colourDesc.Height != 0)
        ? static_cast<float>(colourDesc.Width) / static_cast<float>(colourDesc.Height)
        : 0.0f;

    const bool aspectMatches = eyeAspect > 0.0f && thisAspect > 0.0f &&
                               std::fabs(thisAspect - eyeAspect) < eyeAspect * 0.01f;
    const bool bigEnough = colourTex != nullptr &&
                           colourDesc.Width * 4 >= g_capture.width;

    const bool candidate = depth != nullptr && aspectMatches && bigEnough &&
                           !depth_is_rejected(depth);

    if (colourTex != nullptr)
        colourTex->Release();
    colour->Release();

    if (!candidate)
    {
        if (depth != nullptr)
            depth->Release();
        return;
    }

    ID3D11Texture2D* tex = nullptr;
    if (FAILED(depth->QueryInterface(__uuidof(ID3D11Texture2D),
                                     reinterpret_cast<void**>(&tex))))
    {
        depth->Release();
        return;
    }
    depth->Release();

    /* CENSUS: IS THE BUFFER WE HOLD THE ONE THIS FRAME ACTUALLY DREW INTO?
     *
       The search settles on one texture and then only ever replaces it with a
       LARGER one, so a different buffer with identical dimensions is turned away
       for the rest of the mission. That is invisible while a single eye is
       rendered - it is always the same buffer - and becomes wrong the moment the
       eyes alternate, because rgl draws each eye into its own target and may
       well give each its own depth.

       If it does, the frozen choice belongs to one eye, and on the other eye's
       frames the warp reads a depth that describes the WRONG viewpoint. The
       parallax is then wrong by the full baseline, which is a static doubling
       that does not need the head to move - and it would appear in exactly one
       eye, which is what was reported the moment pinning was turned off.

       So count what is actually bound: how often the held buffer appears, and
       how often a same-sized DIFFERENT one is refused. Both numbers are needed,
       because "we hold the only one there is" and "we hold one of several" look
       identical from anywhere else. */
    if (tex == g_eyeDepth)
    {
        ++g_depthHeldBinds;
        set_depth_recent(tex);
        depth_census_tick();
        tex->Release();
        return;
    }

    /* THE LARGEST CANDIDATE WINS, NOT THE FIRST ONE BOUND.
     *
       This used to take the first match and stop looking, which assumes the
       first eye-shaped depth of a frame is the scene's. A modern renderer binds
       several: particles, screen-space reflections and volumetric fog are all
       commonly drawn at half resolution against a downsampled depth, and any of
       those matches the aspect test just as well as the real thing does. Bind
       one of them early in the frame and the search stops there, on a buffer
       with half the silhouette precision of the one it was looking for.
     *
       Every run so far has latched onto something exactly 50% of the eye in
       both axes - 1224x1338, 1314x1322, 1460x1470 - which is either the scene
       genuinely rendering at half, or exactly this mistake. Preferring the
       largest costs nothing and settles it either way: if a full-resolution
       scene depth is bound anywhere in the frame, it now wins, and the log says
       so when it replaces a smaller one. */
    D3D11_TEXTURE2D_DESC incoming = {};
    tex->GetDesc(&incoming);

    if (g_eyeDepth != nullptr)
    {
        D3D11_TEXTURE2D_DESC held = {};
        g_eyeDepth->GetDesc(&held);

        if (incoming.Width == held.Width && incoming.Height == held.Height &&
            incoming.Format == held.Format)
        {
            /* Same shape, same format, different texture - the case the census
               above exists to count, and under AFR it is the OTHER EYE'S depth
               rather than a stray. It is still refused as the settled choice,
               because swapping the held pointer every frame would restart the
               proof and the probe endlessly; but it is recorded as the most
               recent, because for the frame being captured right now it is the
               correct one. See eye_depth_texture. */
            ++g_depthOtherBinds;
            set_depth_recent(tex);
            depth_census_tick();
        }

        if (incoming.Width <= held.Width)
        {
            /* Smaller or equal: keep what we have and stop paying for the rest
               of this frame's binds. */
            tex->Release();
            return;
        }

        BVR_INFO("AFW: a LARGER scene depth appeared - %ux%u replaces %ux%u. The "
                 "first eye-shaped depth of a frame is not always the scene's.",
                 incoming.Width, incoming.Height, held.Width, held.Height);

        g_eyeDepth->Release();
        g_loggedDepth = false;   /* say what we settled on, not what we tried */
        g_depthStableFrames = 0; /* and let it settle again from here */
    }

    g_eyeDepth = tex;

    if (!g_loggedDepth)
    {
        g_loggedDepth = true;

        const D3D11_TEXTURE2D_DESC& d = incoming;
        BVR_INFO("AFW: eye depth buffer found by its binding: %ux%u fmt %d "
                 "bind 0x%X samples %u (%.0f%% of the eye in each axis).",
                 d.Width, d.Height, static_cast<int>(d.Format),
                 d.BindFlags, d.SampleDesc.Count,
                 g_capture.width != 0
                     ? 100.0f * static_cast<float>(d.Width) /
                       static_cast<float>(g_capture.width)
                     : 0.0f);

        if ((d.BindFlags & D3D11_BIND_SHADER_RESOURCE) == 0)
        {
            BVR_WARN("AFW: that depth buffer has no SHADER_RESOURCE bind flag, so "
                     "it cannot be sampled and the warp cannot run. AFW will stay "
                     "off; AFR is unaffected.");
        }
    }
}

/* --------------------------------------------------------------------------
 * CLEARING THE WINDOW BEFORE THE INTERFACE IS DRAWN INTO IT
 *
 * Under AFR the engine's scene view is pointed at our eye texture, so nothing
 * draws the world into the game's own swapchain any more - only the interface
 * lands there, over whatever was left in that buffer.
 *
 * "Whatever was left" is the whole problem. On a flip swapchain the buffer
 * handed back is one from several frames ago, so the interface is painted over
 * a stale picture. Worse, once the monitor mirror started WRITING its composite
 * into that buffer, the stale picture became the mirror's own older output -
 * and keying the interface out of it by brightness then kept everything,
 * because a picture of a battlefield is not dark. The mirror was feeding on
 * itself, which is why the window only looked right while the escape menu
 * dimmed the background and broke the loop.
 *
 * Clearing at the moment the buffer is bound fixes it at the root: the
 * interface is then drawn onto black, which is what the key has always assumed
 * and can now rely on.
 *
 * Once per frame, on the FIRST bind. The interface is drawn in several passes
 * and clearing at each would erase all but the last.
 * ------------------------------------------------------------------------ */
/* ATOMIC, like every other cross-thread flag in this file.
 *
 * These are written from the render thread inside the Present hook and read
 * inside the OMSetRenderTargets detour, which the ENGINE calls - on whatever
 * threads it likes, hundreds of times a frame, for the whole of a mission.
 * They were added as a plain pointer and a plain bool, which is a data race
 * against the busiest detour in the mod and out of step with g_depthWanted and
 * g_textureHookInstalled sitting a few lines away. */
std::atomic<ID3D11Texture2D*> g_clearTarget{ nullptr };
std::atomic<bool> g_clearedThisFrame{ false };

void clear_backbuffer_if_bound(ID3D11DeviceContext* context,
                               ID3D11RenderTargetView* const* rtvs, UINT count)
{
    ID3D11Texture2D* target = g_clearTarget.load(std::memory_order_acquire);
    if (target == nullptr || rtvs == nullptr || context == nullptr)
        return;

    if (g_clearedThisFrame.load(std::memory_order_acquire))
        return;

    /* THE IMMEDIATE CONTEXT ONLY.
     *
     * A deferred context records into a command list that is executed later, at
     * a moment we do not choose - so a clear recorded there could land after the
     * interface had already been drawn and erase it. The engine is free to build
     * command lists on worker threads, and the detour sees those calls too. */
    if (context->GetType() != D3D11_DEVICE_CONTEXT_IMMEDIATE)
        return;

    for (UINT i = 0; i < count; ++i)
    {
        if (rtvs[i] == nullptr)
            continue;

        ID3D11Resource* res = nullptr;
        rtvs[i]->GetResource(&res);
        if (res == nullptr)
            continue;

        const bool isTarget = (res == static_cast<ID3D11Resource*>(target));
        res->Release();

        if (!isTarget)
            continue;

        /* Legal on a view that is being bound - a clear does not require the
           resource to be unbound, which is exactly why this is a clear and not
           the CopyResource it would otherwise want to be. */
        /* EXCHANGE, not a store. Two threads can both have passed the check
           above before either got here, and a second clear would erase the
           interface the first one had already let the game draw. Exactly one
           wins the flag; the loser leaves without clearing. */
        if (g_clearedThisFrame.exchange(true, std::memory_order_acq_rel))
            return;

        const float black[4] = { 0.0f, 0.0f, 0.0f, 0.0f };
        context->ClearRenderTargetView(rtvs[i], black);
        return;
    }
}

void STDMETHODCALLTYPE hooked_om_set_render_targets(
    ID3D11DeviceContext* context, UINT count,
    ID3D11RenderTargetView* const* rtvs, ID3D11DepthStencilView* dsv)
{
    /* Native stereo's own transient binds (its twin targets, bound and unbound
       around each re-issued draw) are not the engine choosing a target: the
       depth watch would count the twin depth buffers as rgl's, and the window
       clear has nothing to find in them. stereo_in_dup() is never true outside
       native stereo, so AFR and AFW take exactly the path they always did. */
    if (stereo_in_dup())
    {
        g_originalOMSetRT(context, count, rtvs, dsv);
        return;
    }

    if (g_depthWanted.load(std::memory_order_acquire))
        note_render_targets(context, rtvs, count, dsv);

    g_originalOMSetRT(context, count, rtvs, dsv);

    /* AFTER the bind, so the clear lands on the target the game just selected
       rather than on whatever was bound before it. */
    clear_backbuffer_if_bound(context, rtvs, count);
}

void STDMETHODCALLTYPE hooked_om_set_render_targets_uav(
    ID3D11DeviceContext* context, UINT count,
    ID3D11RenderTargetView* const* rtvs, ID3D11DepthStencilView* dsv,
    UINT uavStart, UINT uavCount, ID3D11UnorderedAccessView* const* uavs,
    const UINT* counts)
{
    /* See hooked_om_set_render_targets. */
    if (stereo_in_dup())
    {
        g_originalOMSetRTUAV(context, count, rtvs, dsv, uavStart, uavCount, uavs, counts);
        return;
    }

    if (g_depthWanted.load(std::memory_order_acquire))
        note_render_targets(context, rtvs, count, dsv);

    g_originalOMSetRTUAV(context, count, rtvs, dsv, uavStart, uavCount, uavs, counts);

    clear_backbuffer_if_bound(context, rtvs, count);
}

bool install_depth_hook()
{
    if (g_depthHookInstalled.load(std::memory_order_acquire))
        return true;

    if (g_context == nullptr)
        return false;

    /* ID3D11DeviceContext vtable: 0-2 IUnknown, 3-6 ID3D11DeviceChild, then the
       context methods from 7. OMSetRenderTargets is the 27th of those (33) and
       OMSetRenderTargetsAndUnorderedAccessViews the 28th (34). Both are hooked
       because a renderer may bind through either and missing the one rgl uses
       looks exactly like "the depth buffer does not exist". */
    void** vtable = *reinterpret_cast<void***>(g_context);

    const bool a = vtable_hook(vtable, 33,
                               reinterpret_cast<void*>(&hooked_om_set_render_targets),
                               reinterpret_cast<void**>(&g_originalOMSetRT));
    const bool b = vtable_hook(vtable, 34,
                               reinterpret_cast<void*>(&hooked_om_set_render_targets_uav),
                               reinterpret_cast<void**>(&g_originalOMSetRTUAV));

    if (!a || !b)
    {
        BVR_ERR("AFW: could not hook OMSetRenderTargets; the eye depth buffer "
                "cannot be found and AFW will stay off.");
        return false;
    }

    g_depthHookInstalled.store(true, std::memory_order_release);
    BVR_INFO("AFW: OMSetRenderTargets vtable-hooked; watching for the depth bound "
             "with the eye target.");
    return true;
}


bool install_texture_hook()
{
    if (g_textureHookInstalled.load(std::memory_order_acquire))
        return true;

    if (g_device == nullptr)
    {
        BVR_ERR("Texture capture armed before the engine device was captured.");
        return false;
    }

    /* ID3D11Device vtable: 0-2 IUnknown, 3 CreateBuffer, 4 CreateTexture1D,
       5 CreateTexture2D.
     *
       Vtable-hooked rather than MinHooked. This is the hook the eye textures
       depend on, and a run where MinHook could not allocate a trampoline for it
       left the headset with no image at all - see the note on vtable_hook. */
    void** vtable = *reinterpret_cast<void***>(g_device);
    void* target = vtable[5];

    if (!vtable_hook(vtable, 5, reinterpret_cast<void*>(&hooked_create_texture2d),
                     reinterpret_cast<void**>(&g_originalCreateTexture2D)))
    {
        BVR_ERR("Failed to hook ID3D11Device::CreateTexture2D (vtable write refused).");
        return false;
    }

    g_textureHookInstalled.store(true, std::memory_order_release);
    BVR_INFO("ID3D11Device::CreateTexture2D vtable-hooked (was %p).", target);
    return true;
}

/* Reads IDXGISwapChain's vtable by standing up a throwaway device + swapchain on
   a hidden message-only-ish window. Cheaper and far more robust than pattern
   scanning dxgi.dll, and it is the standard approach for this. */
bool probe_swapchain_vtable(void**& outVtable, IDXGISwapChain*& outChain,
                            ID3D11Device*& outDevice, HWND& outWindow)
{
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"BvrProbeWindow";
    RegisterClassExW(&wc);

    outWindow = CreateWindowExW(0, wc.lpszClassName, L"", WS_OVERLAPPEDWINDOW,
                                0, 0, 8, 8, nullptr, nullptr, wc.hInstance, nullptr);
    if (outWindow == nullptr)
    {
        BVR_ERR("Probe window creation failed (win32 %lu).", GetLastError());
        return false;
    }

    DXGI_SWAP_CHAIN_DESC scd = {};
    scd.BufferCount = 1;
    scd.BufferDesc.Width = 8;
    scd.BufferDesc.Height = 8;
    scd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    scd.OutputWindow = outWindow;
    scd.SampleDesc.Count = 1;
    scd.Windowed = TRUE;
    scd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0 };
    D3D_FEATURE_LEVEL obtained = {};

    HRESULT hr = D3D11CreateDeviceAndSwapChain(
        nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
        levels, 1, D3D11_SDK_VERSION,
        &scd, &outChain, &outDevice, &obtained, nullptr);

    if (FAILED(hr) || outChain == nullptr)
    {
        BVR_ERR("Probe swapchain creation failed (hr 0x%08lX).", static_cast<unsigned long>(hr));
        DestroyWindow(outWindow);
        outWindow = nullptr;
        return false;
    }

    outVtable = *reinterpret_cast<void***>(outChain);
    return true;
}

} // namespace

bool install_present_hook(PresentTickFn tick)
{
    if (g_hookInstalled.load(std::memory_order_acquire))
        return true;

    g_tick = tick;

    if (MH_Initialize() != MH_OK)
    {
        BVR_ERR("MH_Initialize failed.");
        return false;
    }

    void** vtable = nullptr;
    IDXGISwapChain* probeChain = nullptr;
    ID3D11Device* probeDevice = nullptr;
    HWND probeWindow = nullptr;

    if (!probe_swapchain_vtable(vtable, probeChain, probeDevice, probeWindow))
    {
        MH_Uninitialize();
        return false;
    }

    /* IDXGISwapChain vtable: 0-2 IUnknown, 3-6 IDXGIObject, 7 GetDevice(IDXGIDeviceSubObject),
       8 Present. */
    void* presentTarget = vtable[8];

    bool ok = (MH_CreateHook(presentTarget,
                             reinterpret_cast<void*>(&hooked_present),
                             reinterpret_cast<void**>(&g_originalPresent)) == MH_OK) &&
              (MH_EnableHook(presentTarget) == MH_OK);

    /* The probe objects have served their purpose; the vtable is per-class, not
       per-instance, so the hook survives their destruction. */
    if (probeChain != nullptr)  probeChain->Release();
    if (probeDevice != nullptr) probeDevice->Release();
    if (probeWindow != nullptr) DestroyWindow(probeWindow);

    if (!ok)
    {
        BVR_ERR("Failed to hook IDXGISwapChain::Present.");
        MH_Uninitialize();
        return false;
    }

    g_hookInstalled.store(true, std::memory_order_release);
    BVR_INFO("IDXGISwapChain::Present hooked at %p.", presentTarget);
    return true;
}

void remove_hooks()
{
    if (!g_hookInstalled.exchange(false))
        return;

    MH_DisableHook(MH_ALL_HOOKS);
    MH_Uninitialize();
    g_textureHookInstalled.store(false, std::memory_order_release);

    release_eye_textures();

    if (g_multithread != nullptr) { g_multithread->Release(); g_multithread = nullptr; }
    if (g_context != nullptr) { g_context->Release(); g_context = nullptr; }
    if (g_device != nullptr)  { g_device->Release();  g_device = nullptr; }

    g_tick = nullptr;
    BVR_INFO("Hooks removed.");
}

/* Opens one window for both eyes. Called once, before either render target is
   created; C# then polls finish_texture_capture per eye until they arrive. */
bool arm_texture_capture(int32_t /*eye*/, uint32_t width, uint32_t height)
{
    if (width == 0 || height == 0)
        return false;

    if (!install_texture_hook())
        return false;

    for (int i = 0; i < 2; ++i)
    {
        if (g_capture.textures[i] != nullptr)
        {
            g_capture.textures[i]->Release();
            g_capture.textures[i] = nullptr;
        }
    }

    g_capture.width = width;
    g_capture.height = height;
    g_capture.matches = 0;
    g_capture.observed = 0;
    g_capture.depthsLogged = 0;

    g_capture.armed.store(true, std::memory_order_release);
    BVR_INFO("Texture capture armed for %ux%u render targets.", width, height);
    return true;
}

/* Poll. BVR_SESSION_NOT_READY means "not yet", not "failed" - creation is
   deferred onto rgl's render thread and takes an unknown number of frames. */
int32_t finish_texture_capture(int32_t eye)
{
    if (eye < 0 || eye > 1)
        return BVR_NOT_INITIALIZED;

    if (g_eyeTextures[eye] != nullptr)
        return BVR_OK;

    if (g_capture.matches <= eye || g_capture.textures[eye] == nullptr)
        return BVR_SESSION_NOT_READY;

    g_eyeTextures[eye] = g_capture.textures[eye];   /* transfer the AddRef */
    g_capture.textures[eye] = nullptr;

    D3D11_TEXTURE2D_DESC desc = {};
    g_eyeTextures[eye]->GetDesc(&desc);
    BVR_INFO("Eye %d texture captured: %p, %ux%u, format %d, samples %u, bind 0x%X.",
             eye, static_cast<void*>(g_eyeTextures[eye]),
             desc.Width, desc.Height, static_cast<int>(desc.Format),
             desc.SampleDesc.Count, desc.BindFlags);

    if (g_eyeTextures[0] != nullptr && g_eyeTextures[1] != nullptr)
    {
        /* Deliberately still armed - see the note in the hook. */
        BVR_INFO("Both eye textures captured after %d CreateTexture2D calls; "
                 "watching for reallocation.", g_capture.observed);
    }

    return BVR_OK;
}

ID3D11Texture2D* eye_texture(int32_t eye)
{
    return (eye >= 0 && eye <= 1) ? g_eyeTextures[eye] : nullptr;
}

void arm_backbuffer_clear(ID3D11Texture2D* backbuffer)
{
    g_clearTarget.store(backbuffer, std::memory_order_release);
}

void begin_backbuffer_frame()
{
    g_clearedThisFrame.store(false, std::memory_order_release);
}

void arm_depth_capture()
{
    /* Called from the Present hook, while the detour may be running on the
       engine's own threads - so it reads g_eyeDepth and g_depthGaveUp under the
       same lock that guards them there. Uncontended in the normal case: the
       detour only takes it while the watch is armed. */
    std::lock_guard<std::mutex> lock(g_depthMutex);

    if (g_depthGaveUp || g_depthSettled)
        return;

    if (!install_depth_hook())
        return;

    /* Settled: a choice has stood for long enough that a larger candidate is
       not coming. Two seconds of frames is far more than a scene needs to bind
       everything it binds, and stopping here is what keeps "prefer the largest"
       from costing an inspection on every bind for the whole mission. */
    if (g_eyeDepth != nullptr && ++g_depthStableFrames > 180)
    {
        g_depthSettled = true;

        D3D11_TEXTURE2D_DESC d = {};
        g_eyeDepth->GetDesc(&d);
        BVR_INFO("AFW: depth choice settled on %ux%u after %d frames of looking "
                 "for a larger one. The watch is now off and costs nothing.",
                 d.Width, d.Height, g_depthStableFrames);
        return;
    }

    /* Stop asking if it is never going to arrive.
     *
       While armed and unsatisfied the detour does real work - two GetResource
       calls and a QueryInterface - on EVERY OMSetRenderTargets, of which a
       heavy scene makes hundreds per frame. That is a fine price for a few
       seconds of looking and a bad one to pay forever. Two seconds of frames
       is far more than enough for a scene to bind its depth; if none has
       appeared by then, none is going to. */
    if (g_eyeDepth == nullptr && ++g_depthArmedFrames > 240)
    {
        g_depthGaveUp = true;
        BVR_WARN("AFW: no depth buffer was ever bound alongside an eye-sized "
                 "render target in %d frames of looking. AFW cannot run and the "
                 "watch is being switched off so it stops costing anything. The "
                 "survey above shows what WAS bound.", g_depthArmedFrames);
        return;
    }

    g_depthWanted.store(true, std::memory_order_release);
}

/* Rejects the depth target we are holding and keeps looking. Called when a
   candidate turns out not to describe the scene - see depth_nearest_metres.
   The rejection is remembered so the very next bind does not hand back the
   same texture and settle on it again. */
void depth_reject_current(const char* why)
{
    std::lock_guard<std::mutex> lock(g_depthMutex);

    if (g_eyeDepth == nullptr)
        return;

    if (g_depthRejectedCount < 8)
        g_depthRejected[g_depthRejectedCount++] = g_eyeDepth;

    g_depthGeneration.fetch_add(1, std::memory_order_acq_rel);

    D3D11_TEXTURE2D_DESC d = {};
    g_eyeDepth->GetDesc(&d);

    g_eyeDepth->Release();
    g_eyeDepth = nullptr;
    set_depth_recent(nullptr);

    g_depthSettled      = false;
    g_depthGaveUp       = false;
    g_depthStableFrames = 0;
    g_depthArmedFrames  = 0;
    g_loggedDepth       = false;

    BVR_WARN("AFW: the %ux%u fmt %d depth target is not the scene's - %s. It is "
             "blacklisted and the search continues; rgl binds more than one "
             "eye-sized depth per frame and they cannot be told apart by size.",
             d.Width, d.Height, static_cast<int>(d.Format), why);
}

uint32_t depth_generation()
{
    return g_depthGeneration.load(std::memory_order_acquire);
}

void depth_rearm(const char* why)
{
    std::lock_guard<std::mutex> lock(g_depthMutex);

    const bool had = g_eyeDepth != nullptr;
    if (had)
    {
        g_eyeDepth->Release();
        g_eyeDepth = nullptr;
    }

    /* Cleared whether or not one was held: a stale "most recent" pointer must
       not outlive the scene it was bound in. */
    set_depth_recent(nullptr);

    for (int i = 0; i < g_depthRejectedCount; ++i)
        g_depthRejected[i] = nullptr;
    g_depthRejectedCount = 0;

    g_depthGeneration.fetch_add(1, std::memory_order_acq_rel);

    g_depthSettled      = false;
    g_depthGaveUp       = false;
    g_depthStableFrames = 0;
    g_depthArmedFrames  = 0;
    g_loggedDepth       = false;

    BVR_INFO("AFW: depth search re-opened - %s. The buffer we were holding %s "
             "and would have gone on being read after the engine stopped "
             "writing to it.", why,
             had ? "has been dropped" : "was already gone");
}
/* Published by xr_context; see the note in hooked_present. */
void set_xr_pacing(bool running)
{
    g_xrPacing.store(running, std::memory_order_relaxed);
}


ID3D11Texture2D* eye_depth_texture()
{
    /* THE ONE BOUND THIS FRAME, NOT THE ONE WE SETTLED ON.
     *
       rgl keeps a depth buffer PER RENDER TARGET, and under AFR the two eyes
       have a target each - so there are two scene depths of identical size and
       format, used on alternate frames. The search settles on whichever it saw
       first and refuses the other for the rest of the mission, which means half
       of every second the depth handed out belongs to the eye that is NOT being
       captured.

       The census measured it exactly: 810 binds of the held buffer against 810
       of a same-sized different one, a perfect alternation. It reads zero while
       a single eye is rendered - pinned AFW - which is why this went unseen: the
       fault needs both eyes rendering to exist at all.

       Submitting the wrong eye's depth to the compositor makes its positional
       reprojection wrong, and wrong in a way that only shows while the view is
       moving, because a still view is reprojected by nothing. Near geometry is
       where it shows most, since that is where a depth error moves a pixel
       furthest. "The horse loses scale while moving and settles when I stop" is
       that, exactly.

       So prefer the most recently bound matching buffer, which is by definition
       the one this frame drew into. g_eyeDepth stays as the fallback for the
       frames before anything has been seen. */
    return g_depthRecent != nullptr ? g_depthRecent : g_eyeDepth;
}

void eye_depth_size(uint32_t* width, uint32_t* height)
{
    if (width == nullptr || height == nullptr)
        return;

    *width = 0;
    *height = 0;

    if (g_eyeDepth == nullptr)
        return;

    D3D11_TEXTURE2D_DESC d = {};
    g_eyeDepth->GetDesc(&d);
    *width = d.Width;
    *height = d.Height;
}

bool eye_textures_ready()
{
    return g_eyeTextures[0] != nullptr && g_eyeTextures[1] != nullptr;
}

void release_eye_textures()
{
    for (int i = 0; i < 2; ++i)
    {
        if (g_eyeTextures[i] != nullptr)
        {
            g_eyeTextures[i]->Release();
            g_eyeTextures[i] = nullptr;
        }
    }

    for (int i = 0; i < 2; ++i)
    {
        if (g_capture.textures[i] != nullptr)
        {
            g_capture.textures[i]->Release();
            g_capture.textures[i] = nullptr;
        }
    }
    g_capture.armed.store(false, std::memory_order_release);
}

uint32_t present_thread_id()
{
    return g_presentThread.load(std::memory_order_relaxed);
}

bool in_present_on_this_thread()
{
    return t_inPresent;
}

uint64_t present_frame_count()
{
    return g_presentCount.load(std::memory_order_relaxed);
}

ID3D11Multithread*   engine_multithread() { return g_multithread; }
ID3D11Device*        engine_device()  { return g_device; }
ID3D11DeviceContext* engine_context() { return g_context; }
LUID                 engine_adapter_luid() { return g_luid; }
bool vtable_write(void** slot, void* value)
{
    DWORD previous = 0;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &previous))
        return false;

    InterlockedExchangePointer(slot, value);

    DWORD ignored = 0;
    VirtualProtect(slot, sizeof(void*), previous, &ignored);
    return true;
}

bool vtable_hook(void** vtable, int index, void* detour, void** original)
{
    if (vtable == nullptr || detour == nullptr || original == nullptr)
        return false;

    void** slot = &vtable[index];
    void* current = *slot;

    if (current == detour)
        return true;   /* already ours */

    /* Original first, then the detour. The other order leaves a window where a
       call can find our detour installed and a stale (or null) original to
       forward to, which is an access violation on the render thread. */
    *original = current;
    MemoryBarrier();

    return vtable_write(slot, detour);
}

bool                 present_hook_ready() { return g_hookInstalled.load(std::memory_order_acquire); }
uint64_t             present_count()  { return g_presentCount.load(std::memory_order_relaxed); }

} // namespace bvr
