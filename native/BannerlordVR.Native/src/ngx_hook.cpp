#include "ngx_hook.h"

#include "bvr_config.h"
#include "bvr_log.h"
#include "frame_map.h"
#include "stereo_dup.h"
#include "stereo_perf.h"
#include "vp_patch.h"
#include "xr_context.h"

#include <d3d11.h>
#include <d3dcompiler.h>
#include <MinHook.h>
#include <atomic>
#include <windows.h>

using bvr::config_bool;
using bvr::vp_prev_latch_homography;
namespace
{

/* ---------------------------------------------------------------------------
   THE NGX ABI, DECLARED RATHER THAN INCLUDED.
 *
   The NGX SDK is not vendored here and pulling it in for four declarations
   would be a large dependency for a small surface. What we need is the vtable
   layout of NVSDK_NGX_Parameter and the signature of one function, both of
   which are ABI-stable across the SDK - every DLSS integration in the wild
   depends on that, because the class is constructed inside the driver DLL and
   used by the application.

   The ORDER of these virtuals is load-bearing. Set() and Get() are overloaded
   on the value type and the compiler lays them out in declaration order, so a
   missing or reordered overload silently calls the wrong one. Taken from
   nvsdk_ngx_params.h and kept in the same order as the header.
   --------------------------------------------------------------------------- */

typedef int NVSDK_NGX_Result;
#define NGX_SUCCEED(r) (((r) & 0xFFF00000) == 0x00000000)

struct NVSDK_NGX_Handle;
struct ID3D12Resource;   /* never dereferenced - only needed so the two void-ish
                            overloads below are distinct types, which is what
                            keeps the vtable slots in the header's order */

class NVSDK_NGX_Parameter
{
public:
    virtual void Set(const char* name, unsigned long long v) = 0;
    virtual void Set(const char* name, float v) = 0;
    virtual void Set(const char* name, double v) = 0;
    virtual void Set(const char* name, unsigned int v) = 0;
    virtual void Set(const char* name, int v) = 0;
    virtual void Set(const char* name, ID3D11Resource* v) = 0;
    virtual void Set(const char* name, ID3D12Resource* v) = 0;
    virtual void Set(const char* name, void* v) = 0;
    virtual NVSDK_NGX_Result Get(const char* name, unsigned long long* v) const = 0;
    virtual NVSDK_NGX_Result Get(const char* name, float* v) const = 0;
    virtual NVSDK_NGX_Result Get(const char* name, double* v) const = 0;
    virtual NVSDK_NGX_Result Get(const char* name, unsigned int* v) const = 0;
    virtual NVSDK_NGX_Result Get(const char* name, int* v) const = 0;
    virtual NVSDK_NGX_Result Get(const char* name, ID3D11Resource** v) const = 0;
    virtual NVSDK_NGX_Result Get(const char* name, ID3D12Resource** v) const = 0;
    virtual NVSDK_NGX_Result Get(const char* name, void** v) const = 0;
    virtual void Reset() = 0;
};

typedef NVSDK_NGX_Result(__cdecl* PFN_EvaluateFeature)(
    ID3D11DeviceContext* ctx,
    const NVSDK_NGX_Handle* feature,
    const NVSDK_NGX_Parameter* params,
    void* callback);

PFN_EvaluateFeature g_realEvaluate = nullptr;
std::atomic<bool>   g_installed{ false };
std::atomic<uint64_t> g_evalCount{ 0 };

bool hook_enabled()
{
    static bool cached = true;
    static bool have = false;
    if (!have) { have = true; cached = config_bool("ngx_hook", true); }
    return cached;
}

const char* fmt_name(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_R16G16_FLOAT:       return "R16G16_FLOAT";
    case DXGI_FORMAT_R32G32_FLOAT:       return "R32G32_FLOAT";
    case DXGI_FORMAT_R16G16_SNORM:       return "R16G16_SNORM";
    case DXGI_FORMAT_R8G8_SNORM:         return "R8G8_SNORM";
    case DXGI_FORMAT_R16G16B16A16_FLOAT: return "R16G16B16A16_FLOAT";
    case DXGI_FORMAT_R32G32B32A32_FLOAT: return "R32G32B32A32_FLOAT";
    case DXGI_FORMAT_R11G11B10_FLOAT:    return "R11G11B10_FLOAT";
    case DXGI_FORMAT_R32_FLOAT:          return "R32_FLOAT";
    case DXGI_FORMAT_R24G8_TYPELESS:     return "R24G8_TYPELESS";
    case DXGI_FORMAT_R32_TYPELESS:       return "R32_TYPELESS";
    default:                             return "other";
    }
}

/* Describe one bound resource, or say why it could not be described. Everything
   here is read-only; nothing is retained past the call. */
void describe(const char* label, ID3D11Resource* res)
{
    if (res == nullptr)
    {
        BVR_INFO("  NGX %-14s : (null)", label);
        return;
    }

    D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
    res->GetType(&dim);
    if (dim != D3D11_RESOURCE_DIMENSION_TEXTURE2D)
    {
        BVR_INFO("  NGX %-14s : not a Texture2D (dimension %d)", label, (int)dim);
        return;
    }

    ID3D11Texture2D* tex = nullptr;
    if (FAILED(res->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&tex)) || tex == nullptr)
    {
        BVR_INFO("  NGX %-14s : QueryInterface(Texture2D) failed", label);
        return;
    }

    D3D11_TEXTURE2D_DESC d = {};
    tex->GetDesc(&d);
    tex->Release();

    /* BIND FLAGS ARE THE WHOLE QUESTION FOR THE WRITE PATH. A motion-vector
       texture created without UNORDERED_ACCESS cannot be corrected in place; it
       has to be copied through a texture of ours that has it, and the NGX
       parameter re-pointed at the copy. Which of those two we are in is decided
       here rather than assumed. */
    BVR_INFO("  NGX %-14s : %ux%u  %s  bind=%s%s%s%s  usage=%d  mips=%u  samples=%u",
             label, d.Width, d.Height, fmt_name(d.Format),
             (d.BindFlags & D3D11_BIND_SHADER_RESOURCE)  ? "SRV "  : "",
             (d.BindFlags & D3D11_BIND_UNORDERED_ACCESS) ? "UAV "  : "",
             (d.BindFlags & D3D11_BIND_RENDER_TARGET)    ? "RTV "  : "",
             (d.BindFlags & D3D11_BIND_DEPTH_STENCIL)    ? "DSV "  : "",
             (int)d.Usage, d.MipLevels, d.SampleDesc.Count);
}

/* Defined below, after the shader it needs. Corrects the motion vectors into a
   texture of ours and re-points the NGX parameter at it; returns true if it did,
   in which case the parameter must be put back afterwards. */
bool apply_mv_fix(ID3D11DeviceContext* ctx, NVSDK_NGX_Parameter* p,
                  ID3D11Resource** outOriginal);

/* Defined below. Chooses which of the two DLSS features this evaluation belongs
   to, so each eye accumulates only its own history. */
const NVSDK_NGX_Handle* pick_feature(const NVSDK_NGX_Handle* incoming);

/* And the two feature calls, all defined below beside pick_feature. Declared
   here because ngx_hook_install sits above them and has to name them. */
typedef NVSDK_NGX_Result(__cdecl* PFN_CreateFeature)(
    ID3D11DeviceContext* ctx, int featureId,
    NVSDK_NGX_Parameter* params, NVSDK_NGX_Handle** outHandle);
typedef NVSDK_NGX_Result(__cdecl* PFN_ReleaseFeature)(NVSDK_NGX_Handle* handle);

extern PFN_CreateFeature  g_realCreate;
extern PFN_ReleaseFeature g_realRelease;

NVSDK_NGX_Result __cdecl CreateFeature_hook(ID3D11DeviceContext* ctx, int featureId,
                                            NVSDK_NGX_Parameter* params,
                                            NVSDK_NGX_Handle** outHandle);
NVSDK_NGX_Result __cdecl ReleaseFeature_hook(NVSDK_NGX_Handle* handle);

/* Defined below. Native stereo only: evaluates DLSS a second time for eye 1. */
void stereo_second_eye(ID3D11DeviceContext* ctx, const NVSDK_NGX_Handle* feature,
                       const NVSDK_NGX_Parameter* params, void* callback,
                       NVSDK_NGX_Result first);

NVSDK_NGX_Result __cdecl EvaluateFeature_hook(ID3D11DeviceContext* ctx,
                                              const NVSDK_NGX_Handle* feature,
                                              const NVSDK_NGX_Parameter* params,
                                              void* callback)
{
    g_evalCount.fetch_add(1, std::memory_order_relaxed);

    /* ONCE PER CONFIGURATION, NOT ONCE PER PROCESS.
     *
     * The first version of this logged the first evaluation only, and the first
     * evaluation is the wrong one: it reported 2560x1440, which is the DESKTOP
     * window, with Reset = 1 - a menu or loading frame, before the mission's eye
     * target exists. The eye is 3400x3468, so the numbers that matter had not
     * happened yet.
     *
     * So it reports whenever the motion-vector geometry CHANGES, a few times.
     * That catches the menu, the mission, and any resolution change between
     * them, and it still costs nothing per frame - two integer compares on a
     * path that is already doing an upscale. */
    static uint32_t s_lastW = 0, s_lastH = 0;
    static unsigned s_lastSubW = 0, s_lastSubH = 0;
    static int      s_lastQuality = -1;
    static int      s_reports = 0;
    bool report = false;

    /* THE RENDER SIZE, NOT ONLY THE TEXTURE SIZE (TEST 182). An engine can keep
       its targets at full size and render a smaller sub-rectangle for DLSS to
       upscale - then every texture here reads 3040x3100 whatever the quality mode.
       The sub-rectangle and the quality value are what say how many pixels are
       really drawn, so a change in either reports too. */
    unsigned subW = 0, subH = 0;
    int quality = -1;
    if (params != nullptr)
    {
        const_cast<NVSDK_NGX_Parameter*>(params)->Get("DLSS.Render.Subrect.Dimensions.Width", &subW);
        const_cast<NVSDK_NGX_Parameter*>(params)->Get("DLSS.Render.Subrect.Dimensions.Height", &subH);
        const_cast<NVSDK_NGX_Parameter*>(params)->Get("PerfQualityValue", &quality);
    }

    if (params != nullptr && s_reports < 12)
    {
        ID3D11Resource* probe = nullptr;
        const_cast<NVSDK_NGX_Parameter*>(params)->Get("MotionVectors", &probe);
        if (probe != nullptr)
        {
            ID3D11Texture2D* t = nullptr;
            if (SUCCEEDED(probe->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&t)) && t)
            {
                D3D11_TEXTURE2D_DESC td = {};
                t->GetDesc(&td);
                t->Release();
                if (td.Width != s_lastW || td.Height != s_lastH || subW != s_lastSubW ||
                    subH != s_lastSubH || quality != s_lastQuality)
                {
                    s_lastW = td.Width;
                    s_lastH = td.Height;
                    s_lastSubW = subW;
                    s_lastSubH = subH;
                    s_lastQuality = quality;
                    ++s_reports;
                    report = true;
                }
            }
        }
    }

    if (report)
    {
        NVSDK_NGX_Parameter* p = const_cast<NVSDK_NGX_Parameter*>(params);

        ID3D11Resource* color = nullptr;
        ID3D11Resource* output = nullptr;
        ID3D11Resource* depth = nullptr;
        ID3D11Resource* mvec = nullptr;

        p->Get("Color", &color);
        p->Get("Output", &output);
        p->Get("Depth", &depth);
        p->Get("MotionVectors", &mvec);

        float jitX = 0.0f, jitY = 0.0f, mvScaleX = 0.0f, mvScaleY = 0.0f, sharp = 0.0f;
        unsigned int createFlags = 0;
        int reset = 0;
        p->Get("Jitter.Offset.X", &jitX);
        p->Get("Jitter.Offset.Y", &jitY);
        p->Get("MV.Scale.X", &mvScaleX);
        p->Get("MV.Scale.Y", &mvScaleY);
        p->Get("Sharpness", &sharp);
        p->Get("DLSS.Feature.Create.Flags", &createFlags);
        p->Get("Reset", &reset);

        BVR_INFO("NGX EvaluateFeature intercepted - this is DLSS's whole input set, "
                 "and the motion vectors below are the buffer TESTs 79-106 could not "
                 "reach through any constant buffer.");
        describe("Color",         color);
        describe("Output",        output);
        describe("Depth",         depth);
        describe("MotionVectors", mvec);

        /* MV.Scale IS THE CONVENTION, AND IT DECIDES THE WHOLE CORRECTION.
         *
           DLSS multiplies the sampled motion vector by this before use. A scale
           of the render width/height means the buffer holds NDC or UV-relative
           motion; a scale of 1 (or -1) means it already holds pixels. The SIGN
           says which direction "motion" points - towards the previous frame or
           away from it - and getting that backwards would double the error
           instead of removing it, which is exactly the class of mistake TESTs
           101/102/104 made three times with a rotation axis.

           So it is read and printed, and the shader is written against what it
           says rather than against what is usual. */
        BVR_INFO("  NGX MV.Scale       : %.4f, %.4f   (the units the vectors are in: "
                 "+/-render size means NDC or UV, +/-1 means pixels; the SIGN is the "
                 "direction convention)", mvScaleX, mvScaleY);
        BVR_INFO("  NGX Jitter         : %.4f, %.4f   (sub-pixel offset this frame)",
                 jitX, jitY);
        /* Decoded by name. The first version of this line named the wrong bits -
           MVLowRes is bit 1, not bit 3 - and a mislabelled flag is exactly the
           kind of thing that gets built into a shader and believed. */
        BVR_INFO("  NGX CreateFlags    : 0x%08X = %s%s%s%s%s%s",
                 createFlags,
                 (createFlags & 0x01) ? "IsHDR "         : "",
                 (createFlags & 0x02) ? "MVLowRes "      : "",
                 (createFlags & 0x04) ? "MVJittered "    : "",
                 (createFlags & 0x08) ? "DepthInverted " : "",
                 (createFlags & 0x10) ? "DoSharpening "  : "",
                 (createFlags & 0x20) ? "AutoExposure "  : "");
        BVR_INFO("  NGX -> MVLowRes %s, MVJittered %s. LowRes set means the vectors are "
                 "at the COLOUR resolution above, not the Output one; MVJittered clear "
                 "means they do NOT carry the sub-pixel jitter and the correction must "
                 "not add it either.",
                 (createFlags & 0x02) ? "SET" : "clear",
                 (createFlags & 0x04) ? "SET" : "clear");
        BVR_INFO("  NGX Sharpness %.3f, Reset %d", sharp, reset);

        unsigned inW = 0, inH = 0, outW = 0, outH = 0;
        p->Get("Width", &inW);
        p->Get("Height", &inH);
        p->Get("OutWidth", &outW);
        p->Get("OutHeight", &outH);
        BVR_INFO("  NGX render size    : sub-rectangle %ux%u, created for %ux%u -> %ux%u, "
                 "PerfQualityValue %d (0 performance, 1 balanced, 2 quality, 3 ultra "
                 "performance, 5 DLAA). A sub-rectangle smaller than the colour texture is "
                 "DLSS really drawing fewer pixels; equal or 0x0 is the whole texture.",
                 subW, subH, inW, inH, outW, outH, quality);
        BVR_INFO("NGX: reconnaissance only in this build - nothing is written. The "
                 "correction needs the format, the resolution ratio, the scale "
                 "convention and the bind flags above, and every one of them was a "
                 "guess until now.");
    }

    /* THE CORRECTION, IF IT IS ARMED. The engine's own motion-vector texture is
       never written; the parameter is pointed at ours for this call and put back
       immediately after, so a failure anywhere leaves DLSS exactly as it was. */
    ID3D11Resource* originalMv = nullptr;
    const bool swapped = (params != nullptr) &&
                         apply_mv_fix(ctx, const_cast<NVSDK_NGX_Parameter*>(params),
                                      &originalMv);

    /* THE SUBSTITUTION THAT ACTUALLY MATCHES THE DIAGNOSIS. Same parameters,
       same context, different feature - and the feature is what owns the
       temporal history. */
    const NVSDK_NGX_Handle* useFeature = pick_feature(feature);

    /* In a frame-map capture, DLSS's whole input set goes into the op stream at
       the point it is consumed. NGX's Get hands back its own pointers, not
       AddRef'd ones, so nothing here is released. */
    if (bvr::frame_map_capturing() && params != nullptr)
    {
        static const char* const kNames[] = {
            "Color", "Output", "Depth", "MotionVectors", "ExposureTexture",
            "TransparencyMask", "DLSS.Input.Bias.Current.Color.Mask",
        };
        constexpr int kN = static_cast<int>(sizeof(kNames) / sizeof(kNames[0]));
        ID3D11Resource* res[kN] = {};
        NVSDK_NGX_Parameter* p = const_cast<NVSDK_NGX_Parameter*>(params);
        for (int i = 0; i < kN; ++i)
            p->Get(kNames[i], &res[i]);
        float jx = 0.0f, jy = 0.0f;
        int reset = 0;
        p->Get("Jitter.Offset.X", &jx);
        p->Get("Jitter.Offset.Y", &jy);
        p->Get("Reset", &reset);
        bvr::frame_map_ngx(ctx, kNames, res, kN, jx, jy, reset);
    }

    const bool timeIt = bvr::stereo_active();
    LARGE_INTEGER e0 = {}, e1 = {};
    if (timeIt)
    {
        bvr::stereo_perf_ngx_stamp(ctx, 0);
        QueryPerformanceCounter(&e0);
    }
    const NVSDK_NGX_Result r = g_realEvaluate(ctx, useFeature, params, callback);
    if (timeIt)
    {
        QueryPerformanceCounter(&e1);
        bvr::stereo_perf_ngx_cpu(e1.QuadPart - e0.QuadPart);
        bvr::stereo_perf_ngx_stamp(ctx, 1);
    }

    if (swapped && originalMv != nullptr)
        const_cast<NVSDK_NGX_Parameter*>(params)->Set("MotionVectors", originalMv);

    /* Native stereo's second eye. A load and a return in every other mode. */
    stereo_second_eye(ctx, feature, params, callback, r);

    return r;
}

} /* namespace */

bool ngx_hook_install()
{
    if (!hook_enabled())
        return false;
    if (g_installed.load(std::memory_order_acquire))
        return true;

    /* The NGX core is the DRIVER's DLL, loaded by the game's statically linked
       NGX shim once DLSS is actually initialised - so it is not present at
       startup and this has to be retried. Cheap: two module lookups. */
    HMODULE ngx = GetModuleHandleW(L"_nvngx.dll");
    if (ngx == nullptr)
        ngx = GetModuleHandleW(L"nvngx.dll");
    if (ngx == nullptr)
        return false;

    FARPROC target = GetProcAddress(ngx, "NVSDK_NGX_D3D11_EvaluateFeature");
    if (target == nullptr)
    {
        static bool warned = false;
        if (!warned)
        {
            warned = true;
            BVR_WARN("NGX: _nvngx.dll is loaded but does not export "
                     "NVSDK_NGX_D3D11_EvaluateFeature. Either this title drives DLSS "
                     "through D3D12, or the driver routes evaluation somewhere else. "
                     "Nothing is hooked; the motion vectors stay out of reach.");
        }
        return false;
    }

    if (MH_CreateHook(reinterpret_cast<LPVOID>(target),
                      reinterpret_cast<LPVOID>(&EvaluateFeature_hook),
                      reinterpret_cast<LPVOID*>(&g_realEvaluate)) != MH_OK)
    {
        BVR_WARN("NGX: MH_CreateHook on NVSDK_NGX_D3D11_EvaluateFeature refused.");
        return false;
    }

    if (MH_EnableHook(reinterpret_cast<LPVOID>(target)) != MH_OK)
    {
        BVR_WARN("NGX: MH_EnableHook on NVSDK_NGX_D3D11_EvaluateFeature refused.");
        return false;
    }

    /* The two feature calls, for per-eye history. Not fatal if either is
       missing - the evaluation hook simply never substitutes. */
    FARPROC crt = GetProcAddress(ngx, "NVSDK_NGX_D3D11_CreateFeature");
    FARPROC rel = GetProcAddress(ngx, "NVSDK_NGX_D3D11_ReleaseFeature");
    if (crt != nullptr && rel != nullptr)
    {
        if (MH_CreateHook(reinterpret_cast<LPVOID>(rel),
                          reinterpret_cast<LPVOID>(&ReleaseFeature_hook),
                          reinterpret_cast<LPVOID*>(&g_realRelease)) == MH_OK &&
            MH_EnableHook(reinterpret_cast<LPVOID>(rel)) == MH_OK &&
            MH_CreateHook(reinterpret_cast<LPVOID>(crt),
                          reinterpret_cast<LPVOID>(&CreateFeature_hook),
                          reinterpret_cast<LPVOID*>(&g_realCreate)) == MH_OK &&
            MH_EnableHook(reinterpret_cast<LPVOID>(crt)) == MH_OK)
        {
            BVR_INFO("NGX: CreateFeature and ReleaseFeature hooked too - per-eye "
                     "DLSS history is available. ngx_per_eye = 0 disables it.");
        }
        else
        {
            BVR_WARN("NGX: could not hook CreateFeature/ReleaseFeature; per-eye "
                     "history is unavailable and one shared history stays.");
        }
    }
    else
    {
        BVR_WARN("NGX: _nvngx.dll exports no CreateFeature/ReleaseFeature pair; "
                 "per-eye history is unavailable.");
    }

    g_installed.store(true, std::memory_order_release);
    BVR_INFO("NGX: hooked NVSDK_NGX_D3D11_EvaluateFeature at %p. The next DLSS "
             "evaluation will report what it is being handed. ngx_hook = 0 disables.",
             (void*)target);
    return true;
}

void ngx_hook_remove()
{
    g_installed.store(false, std::memory_order_release);
    g_realEvaluate = nullptr;
}

/* ===========================================================================
   THE CORRECTION.

   Measured facts this is written against, none of them assumed (TEST 107/108):

     MotionVectors  3040x3100  R16G16_FLOAT  bind = SRV | RTV   (NO UAV)
     MV.Scale       1.0, 1.0            -> the vectors are in PIXELS
     Colour == Output == MotionVectors  -> no upscale here; one grid throughout
     MVJittered     clear               -> they carry no sub-pixel jitter, and
                                           the correction must not add any

   No UAV on the engine's texture means it cannot be written in place, so the
   corrected vectors go into a texture of ours - created with SRV | UAV - and the
   NGX "MotionVectors" parameter is re-pointed at it for the duration of the
   call. The engine's own texture is never touched.

   WHAT IS CORRECTED. A motion vector says where this surface was in the previous
   frame's image. The previous frame's image was LATCHED by that frame's D, and
   the engine's vector does not know it. So the previous position is pushed
   through last frame's latch homography and the vector rebuilt from it:

     prev_px      = uv_px + mv            (where the engine says it was)
     prev_clip    = to_clip(prev_px)
     prev_latched = prev_clip * M_prev    (where it actually was on screen)
     mv_corrected = to_px(prev_latched) - uv_px

   M_prev is depth-independent because D is a rotation about the camera, which is
   the whole reason this is a shader over a texture and not a per-pixel reprojection
   needing depth.
   =========================================================================== */

namespace
{

const char* kMvFixShader = R"(
Texture2D<float2>   SrcMv : register(t0);
RWTexture2D<float2> DstMv : register(u0);

cbuffer Params : register(b0)
{
    float4x4 gPrevLatch;      // clip-space homography of last frame's D
    float4x4 gPrevLatchInv;   // and its inverse, for the direction test
    float4x4 gCurLatchInv;    // THIS frame's latch, inverted: latched -> engine
    float2   gSize;           // motion-vector texture size, pixels
    float    gMode;           // 0 passthrough, 1..4 the four convention pairings
    float    gPad;
};

float2 to_ndc(float2 px, float2 size)
{
    return float2( px.x / size.x * 2.0 - 1.0, 1.0 - px.y / size.y * 2.0 );
}

float2 to_px(float2 ndc, float2 size)
{
    return float2( (ndc.x * 0.5 + 0.5) * size.x, (0.5 - ndc.y * 0.5) * size.y );
}

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= (uint)gSize.x || tid.y >= (uint)gSize.y)
        return;

    float2 mv = SrcMv[tid.xy];

    // MODE 0 - passthrough. Proves the plumbing without touching a value: if
    // this looks identical to ngx_mv_fix = 0 then the texture, the format and
    // the grid are all right and only the maths below is in question.
    if (gMode < 0.5)
    {
        DstMv[tid.xy] = mv;
        return;
    }

    // The pixel centre. Needed by every path below, so it is declared once here
    // rather than inside the mode 1-4 branch - which is where it used to live,
    // and this shader is compiled at RUNTIME, so using it before that point
    // would have failed as a D3DCompile error in the log rather than a build
    // error, with the correction silently never arming.
    float2 here = float2(tid.xy) + 0.5;

    // MODE 7 - THE CADENCE SCALE, and it only makes sense beside ngx_per_eye.
    //
    // Per-eye history gives each eye the RIGHT viewpoint and the WRONG
    // cadence: its history is two frames old, because the engine draws that eye
    // every other frame, while the engine's motion vectors always describe ONE
    // frame of motion. Every pixel is then reprojected only halfway to where it
    // belongs - smearing that needs no head motion at all, and two eyes
    // converging differently, which is what "not synced between both eyes" is.
    //
    // TEST 80 hit this exact trap from the other side and wrote the lesson down:
    // the eye was negotiable, the CADENCE was not. Doubling the vectors pays the
    // cadence back, and the two halves together are right in both.
    //
    // Exact only for constant velocity over two frames, which at 90 Hz is 22 ms -
    // short enough that the linear term dominates.
    if (gMode > 6.5)
    {
        DstMv[tid.xy] = mv * 2.0;
        return;
    }

    // MODES 5 and 6 - THE TWO-HOMOGRAPHY FORM, and the one that fixes what
    // modes 1-4 all had wrong.
    //
    // Those four computed prev = here +/- mv, with `here` the pixel's RASTER
    // position - which is in the LATCHED image. The engine's vector, though,
    // was computed in ENGINE space. Adding an engine-space displacement to a
    // latched-space position is a category error, and it is IDENTICAL in all
    // four: no flip of the vector sign and no inversion of the homography can
    // repair it, which is why three of them failed in three different ways.
    //
    // Done properly the pixel has to be carried into engine space first, moved
    // by the engine's own vector there, and carried back through LAST frame's
    // latch - because that is the frame the vector points into.
    if (gMode > 4.5)
    {
        float2 hc = to_ndc(here, gSize);
        float4 he = mul(float4(hc, 0.0, 1.0), gCurLatchInv);
        if (abs(he.w) < 1e-6) { DstMv[tid.xy] = mv; return; }
        float2 hereEng = to_px(he.xy / he.w, gSize);

        float  s6 = (gMode < 5.5) ? 1.0 : -1.0;
        float2 prevEng = hereEng + s6 * mv;

        float2 pc = to_ndc(prevEng, gSize);
        float4 pl = mul(float4(pc, 0.0, 1.0), gPrevLatch);
        if (abs(pl.w) < 1e-6) { DstMv[tid.xy] = mv; return; }

        DstMv[tid.xy] = to_px(pl.xy / pl.w, gSize) - here;
        return;
    }

    // Pixel centre, and where the engine says this surface was last frame.
    // WHICH WAY THE VECTOR POINTS IS A CONVENTION AND IT IS NOT GUESSED HERE.
    // Backward (prev = here + mv) is the usual DLSS convention; forward is not
    // rare. Getting it wrong does not soften the correction, it DOUBLES the
    // error - which is what "smear everywhere" looked like on the first run.
    float sgn  = (gMode < 2.5) ? 1.0 : -1.0;      // modes 1,2 = +mv; 3,4 = -mv
    float2 prev = here + sgn * mv;

    // Pixels -> NDC. y is flipped: pixel y grows downward, NDC y upward.
    float2 ndc = float2( prev.x / gSize.x * 2.0 - 1.0,
                         1.0 - prev.y / gSize.y * 2.0 );

    // Through last frame's latch. Row-vector convention, matching the rest of
    // this project: v * M, not M * v.
    // And which DIRECTION the homography runs is the second unknown: M maps the
    // engine's screen to the latched one, but only if the previous position in
    // the buffer is in engine space. Modes 2 and 4 try the inverse.
    float4x4 H = (gMode < 1.5 || (gMode > 2.5 && gMode < 3.5)) ? gPrevLatch : gPrevLatchInv;
    float4 c = mul(float4(ndc, 0.0, 1.0), H);
    if (abs(c.w) < 1e-6)
    {
        DstMv[tid.xy] = mv;
        return;
    }
    float2 ndcL = c.xy / c.w;

    // NDC -> pixels, and back to a vector.
    float2 prevL = float2( (ndcL.x * 0.5 + 0.5) * gSize.x,
                           (0.5 - ndcL.y * 0.5) * gSize.y );

    DstMv[tid.xy] = prevL - here;
}
)";

struct MvFixParams
{
    float prevLatch[16];
    float prevLatchInv[16];
    float curLatchInv[16];
    float sizeX, sizeY, mode, pad1;
};

ID3D11ComputeShader*       g_mvCs = nullptr;
ID3D11Buffer*              g_mvCb = nullptr;
ID3D11Texture2D*           g_mvDst = nullptr;
ID3D11UnorderedAccessView* g_mvDstUav = nullptr;
ID3D11ShaderResourceView*  g_mvDstSrv = nullptr;
ID3D11ShaderResourceView*  g_mvSrcSrv = nullptr;
ID3D11Resource*            g_mvSrcFor = nullptr;   /* which source the SRV is for */
uint32_t                   g_mvW = 0, g_mvH = 0;
bool                       g_mvFailed = false;
std::atomic<uint64_t>      g_mvFixed{ 0 };

float mv_ab_seconds()
{
    static float cached = 0.0f;
    static bool  have = false;
    if (!have) { have = true; cached = bvr::config_float("ngx_mv_ab", 0.0f); }
    return cached;
}

int mv_mode()
{
    static int  cached = 1;
    static bool have = false;
    if (!have)
    {
        have = true;
        cached = static_cast<int>(bvr::config_float("ngx_mv_mode", 1.0f));
        if (cached < 0) cached = 0;
        if (cached > 7) cached = 7;
    }
    return cached;
}

bool mv_fix_enabled()
{
    static bool cached = false;
    static bool have = false;
    if (!have) { have = true; cached = config_bool("ngx_mv_fix", false); }
    return cached;
}

} /* namespace */

namespace
{

/* Build, once, and rebuild if the motion-vector geometry changes. */
bool mv_fix_prepare(ID3D11Device* dev, uint32_t w, uint32_t h)
{
    if (g_mvFailed)
        return false;
    if (g_mvCs != nullptr && g_mvDst != nullptr && w == g_mvW && h == g_mvH)
        return true;

    if (g_mvDstSrv) { g_mvDstSrv->Release(); g_mvDstSrv = nullptr; }
    if (g_mvDstUav) { g_mvDstUav->Release(); g_mvDstUav = nullptr; }
    if (g_mvDst)    { g_mvDst->Release();    g_mvDst = nullptr; }
    if (g_mvSrcSrv) { g_mvSrcSrv->Release(); g_mvSrcSrv = nullptr; }
    g_mvSrcFor = nullptr;

    if (g_mvCs == nullptr)
    {
        ID3DBlob* code = nullptr;
        ID3DBlob* err = nullptr;
        const HRESULT hr = D3DCompile(kMvFixShader, strlen(kMvFixShader),
                                      "bvr_ngx_mvfix", nullptr, nullptr,
                                      "main", "cs_5_0", 0, 0, &code, &err);
        if (FAILED(hr) || code == nullptr)
        {
            BVR_ERR("NGX MV fix: shader compile failed (0x%08X) %s", hr,
                    err ? static_cast<const char*>(err->GetBufferPointer()) : "");
            if (err) err->Release();
            if (code) code->Release();
            g_mvFailed = true;
            return false;
        }
        if (err) err->Release();

        const HRESULT csHr = dev->CreateComputeShader(code->GetBufferPointer(),
                                                      code->GetBufferSize(),
                                                      nullptr, &g_mvCs);
        code->Release();
        if (FAILED(csHr))
        {
            BVR_ERR("NGX MV fix: CreateComputeShader failed (0x%08X).", csHr);
            g_mvFailed = true;
            return false;
        }

        D3D11_BUFFER_DESC bd = {};
        bd.ByteWidth = sizeof(MvFixParams);
        bd.Usage = D3D11_USAGE_DYNAMIC;
        bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        if (FAILED(dev->CreateBuffer(&bd, nullptr, &g_mvCb)))
        {
            BVR_ERR("NGX MV fix: constant buffer creation failed.");
            g_mvFailed = true;
            return false;
        }
    }

    /* OURS gets the UAV the engine's texture does not have. Same format and
       size, so nothing downstream can tell the difference except the values. */
    D3D11_TEXTURE2D_DESC td = {};
    td.Width = w;
    td.Height = h;
    td.MipLevels = 1;
    td.ArraySize = 1;
    /* CONCRETE, not typeless: NGX is handed the RESOURCE and builds its own
       shader view from it, so a typeless resource gives it nothing to build
       from. Whether the typed UAV store this needs is actually supported is
       QUERIED below rather than assumed. */
    td.Format = DXGI_FORMAT_R16G16_FLOAT;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
    if (FAILED(dev->CreateTexture2D(&td, nullptr, &g_mvDst)))
    {
        BVR_ERR("NGX MV fix: could not create the %ux%u destination.", w, h);
        g_mvFailed = true;
        return false;
    }
    if (FAILED(dev->CreateUnorderedAccessView(g_mvDst, nullptr, &g_mvDstUav)))
    {
        BVR_ERR("NGX MV fix: could not create the destination UAV.");
        g_mvFailed = true;
        return false;
    }

    /* IS THE STORE THIS SHADER MAKES EVEN DEFINED? D3D11 guarantees typed UAV
       stores only for the R32 formats; anything else is a per-device capability,
       and an unsupported store is UNDEFINED rather than failed. That is the
       difference between "the maths is wrong" and "the write never landed", and
       it decides whether the four correction modes were ever testing anything. */
    D3D11_FEATURE_DATA_FORMAT_SUPPORT2 fs2 = {};
    fs2.InFormat = DXGI_FORMAT_R16G16_FLOAT;
    if (SUCCEEDED(dev->CheckFeatureSupport(D3D11_FEATURE_FORMAT_SUPPORT2, &fs2, sizeof(fs2))))
        BVR_INFO("NGX MV fix: R16G16_FLOAT typed UAV store is %s on this device. "
                 "NOT supported would mean every correction mode so far wrote "
                 "undefined values and none of them tested the maths at all.",
                 (fs2.OutFormatSupport2 & D3D11_FORMAT_SUPPORT2_UAV_TYPED_STORE) ? "SUPPORTED"
                                                                : "NOT supported");

    g_mvW = w;
    g_mvH = h;
    BVR_INFO("NGX MV fix: ready at %ux%u R16G16_FLOAT. The engine's own motion "
             "vectors are read and a corrected copy is handed to DLSS; the "
             "engine's texture is never written.", w, h);
    return true;
}

bool apply_mv_fix(ID3D11DeviceContext* ctx, NVSDK_NGX_Parameter* p,
                  ID3D11Resource** outOriginal)
{
    if (!mv_fix_enabled() || ctx == nullptr || p == nullptr || outOriginal == nullptr)
        return false;

    /* A/B IN ONE SITTING, because "is this different from the build I ran twenty
       minutes ago" is not a question anyone can answer from memory - and being
       unable to answer it is what left the passthrough result ambiguous.

       With ngx_mv_ab set, the swap is applied for that many seconds and then not
       applied for that many, over and over. A difference that is invisible when
       compared across launches is obvious when the same surface pulses between
       the two states while you look at it. Under ngx_mv_mode = 0 this tests the
       APPARATUS alone: a passthrough copy against no copy at all. If the ground
       changes at the switch, handing DLSS our texture is itself the fault; if
       nothing changes, the plumbing is innocent and only the maths is left. */
    const float abS = mv_ab_seconds();
    if (abS > 0.01f)
    {
        const uint64_t ms = GetTickCount64();
        const uint64_t period = static_cast<uint64_t>(abS * 1000.0f);
        const bool on = ((ms / (period > 0 ? period : 1)) & 1ull) == 0ull;

        static bool s_lastOn = false;
        static bool s_haveLast = false;
        if (!s_haveLast || on != s_lastOn)
        {
            s_haveLast = true;
            s_lastOn = on;
            BVR_INFO("NGX MV A/B: correction %s", on ? "ON" : "OFF");
        }
        if (!on)
            return false;
    }

    /* Nothing to correct until two frames have been latched. */
    float homo[16], homoInv[16], cur[16], curInv[16];
    if (!vp_prev_latch_homography(homo, homoInv))
        return false;
    if (!bvr::vp_cur_latch_homography(cur, curInv))
        return false;

    ID3D11Resource* src = nullptr;
    p->Get("MotionVectors", &src);
    if (src == nullptr)
        return false;

    ID3D11Texture2D* srcTex = nullptr;
    if (FAILED(src->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&srcTex)) || !srcTex)
        return false;
    D3D11_TEXTURE2D_DESC sd = {};
    srcTex->GetDesc(&sd);
    srcTex->Release();

    /* Only the format actually measured. Anything else and the correction would
       be arithmetic on a layout it has never seen. */
    if (sd.Format != DXGI_FORMAT_R16G16_FLOAT ||
        (sd.BindFlags & D3D11_BIND_SHADER_RESOURCE) == 0)
        return false;

    ID3D11Device* dev = nullptr;
    ctx->GetDevice(&dev);
    if (dev == nullptr)
        return false;

    const bool ok = mv_fix_prepare(dev, sd.Width, sd.Height);
    if (!ok) { dev->Release(); return false; }

    if (g_mvSrcSrv == nullptr || g_mvSrcFor != src)
    {
        if (g_mvSrcSrv) { g_mvSrcSrv->Release(); g_mvSrcSrv = nullptr; }
        if (FAILED(dev->CreateShaderResourceView(src, nullptr, &g_mvSrcSrv)))
        {
            dev->Release();
            return false;
        }
        g_mvSrcFor = src;
    }
    dev->Release();

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    if (FAILED(ctx->Map(g_mvCb, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        return false;
    MvFixParams prm = {};
    memcpy(prm.prevLatch, homo, sizeof(prm.prevLatch));
    memcpy(prm.prevLatchInv, homoInv, sizeof(prm.prevLatchInv));
    memcpy(prm.curLatchInv, curInv, sizeof(prm.curLatchInv));
    prm.sizeX = static_cast<float>(sd.Width);
    prm.sizeY = static_cast<float>(sd.Height);
    prm.mode  = static_cast<float>(mv_mode());
    memcpy(mapped.pData, &prm, sizeof(prm));
    ctx->Unmap(g_mvCb, 0);

    /* Borrowed context, so everything is put back. Same discipline as the AFW
       dispatch: a compute shader or a UAV left bound surfaces three systems away
       as something rendering black. */
    ID3D11ComputeShader*       prevCs = nullptr;
    ID3D11Buffer*              prevCb = nullptr;
    ID3D11ShaderResourceView*  prevSrv = nullptr;
    ID3D11UnorderedAccessView* prevUav = nullptr;
    UINT counts = static_cast<UINT>(-1);
    ctx->CSGetShader(&prevCs, nullptr, nullptr);
    ctx->CSGetConstantBuffers(0, 1, &prevCb);
    ctx->CSGetShaderResources(0, 1, &prevSrv);
    ctx->CSGetUnorderedAccessViews(0, 1, &prevUav);

    UINT init = 0;
    ctx->CSSetShader(g_mvCs, nullptr, 0);
    ctx->CSSetConstantBuffers(0, 1, &g_mvCb);
    ctx->CSSetShaderResources(0, 1, &g_mvSrcSrv);
    ctx->CSSetUnorderedAccessViews(0, 1, &g_mvDstUav, &init);
    ctx->Dispatch((sd.Width + 7) / 8, (sd.Height + 7) / 8, 1);

    ID3D11UnorderedAccessView* nullUav = nullptr;
    ID3D11ShaderResourceView*  nullSrv = nullptr;
    ctx->CSSetUnorderedAccessViews(0, 1, &nullUav, &init);
    ctx->CSSetShaderResources(0, 1, &nullSrv);
    ctx->CSSetShader(prevCs, nullptr, 0);
    ctx->CSSetConstantBuffers(0, 1, &prevCb);
    ctx->CSSetShaderResources(0, 1, &prevSrv);
    ctx->CSSetUnorderedAccessViews(0, 1, &prevUav, &counts);

    if (prevCs)  prevCs->Release();
    if (prevCb)  prevCb->Release();
    if (prevSrv) prevSrv->Release();
    if (prevUav) prevUav->Release();

    *outOriginal = src;
    p->Set("MotionVectors", static_cast<ID3D11Resource*>(g_mvDst));

    const uint64_t n = g_mvFixed.fetch_add(1, std::memory_order_relaxed);
    if (n == 0)
        BVR_INFO("NGX MV fix: LIVE. DLSS is now reading corrected motion vectors - "
                 "the previous position of every pixel pushed through last frame's "
                 "latch, so the vectors describe the image that was actually "
                 "rendered instead of the one the engine thought it drew.");
    return true;
}

} /* namespace */

/* ===========================================================================
   PER-EYE DLSS HISTORY.

   TEST 116 refuted the motion-vector correction and, in doing so, confirmed what
   TEST 79 said before any of it: DLSS keeps ONE history, AFR alternates the eye
   every frame, so this frame's history is the OTHER eye's image, 65 mm across.
   The vectors were never wrong. The CONTENT of the history is.

   Nothing written into a matrix or a vector can fix that, which is why thirty
   tests of both did not. What owns the history is the NGX FEATURE, so the fix is
   two features - one per eye - alternating with the eye the engine is drawing.
   Each then accumulates only its own viewpoint and never blends across the
   baseline.

   The second feature is created by calling the driver's own CreateFeature a
   second time with the parameters the game just used, so it is identical in
   every respect the game chose. The game never learns about it: it is handed its
   own handle back, and the substitution happens inside EvaluateFeature.
   =========================================================================== */

namespace
{

PFN_CreateFeature  g_realCreate = nullptr;
PFN_ReleaseFeature g_realRelease = nullptr;

/* The game's handle and our twin. One pair is enough: this title creates one
   DLSS feature for the eye render, and the menu one is torn down before the
   mission's is made. */
NVSDK_NGX_Handle* g_featureGame = nullptr;
NVSDK_NGX_Handle* g_featureTwin = nullptr;
std::atomic<uint64_t> g_twinUsed{ 0 };

bool per_eye_enabled()
{
    static bool cached = true;
    static bool have = false;
    if (!have) { have = true; cached = config_bool("ngx_per_eye", true); }
    return cached;
}

/* ===========================================================================
   NATIVE STEREO: DLSS FOR THE SECOND EYE (TEST 181).

   NGX writes its output without a single draw or dispatch that stereo_dup's
   hooks can see - the frame map of the 20:28 run has the NGX-EVALUATE op with
   nothing around it. So the eye-1 copy of the DLSS output never existed: the
   post passes and the final composite read the ORIGINAL for both eyes, and the
   second eye was eye 0's resolved image with only its bloom redone. No
   parallax, no depth - and every native-stereo depth report so far was of that.

   So while native stereo runs, the evaluation is made a second time for eye 1:
   each input that differs between the eyes swapped for its twin, the output for
   the output's twin, and a FEATURE of its own, because DLSS's history belongs to
   the feature and eye 1 must accumulate only eye 1.

   This is the one mode where two features are structurally right. TEST 117-119
   split ONE eye stream between two features under AFR: each saw every other
   frame, so its history was two frames old and its jitter sequence decimated.
   Here both eyes are drawn every frame with the same jitter, so each feature
   sees exactly the stream DLSS is built for.

   The feature is created lazily, from the evaluation's own parameter block, the
   first time native stereo evaluates - AFR and AFW never cause one to exist - and
   released when the game releases the feature it is paired with, or on the first
   evaluation after native stereo is switched off. None of this touches the AFR
   per-eye pair above (g_featureTwin / pick_feature).
   =========================================================================== */

NVSDK_NGX_Handle*       g_stereoTwin = nullptr;
const NVSDK_NGX_Handle* g_stereoPairedWith = nullptr;
const NVSDK_NGX_Handle* g_stereoFailedFor = nullptr;
int                     g_stereoFeatureId = 1;      /* NVSDK_NGX_Feature_SuperSampling */
bool                    g_stereoReset = true;
uint64_t                g_stereoEvals = 0;
uint64_t                g_stereoMissed = 0;
const char*             g_stereoWhy = "";
double                  g_stereoCpuMs = 0.0;
uint64_t                g_stereoLastLog = 0;

/* WHAT THE GAME CREATED ITS FEATURE WITH (TEST 183).
 *
   TEST 182 created eye 1's feature from the EVALUATION's parameter block, which
   only worked while DLSS was forced to DLAA. With a real upscale the block at
   evaluation holds Width/Height = the OUTPUT size and OutWidth/OutHeight = the
   RENDER size - the layout of DLSS's optimal-settings query, which the game ran
   on the same block - so CreateFeature was asked to upscale 3040x3100 into
   2027x2067 and refused with 0xBAD00005, every time. Eye 1 then showed eye 0's
   image: flat.
 *
   So the creation values are recorded here, at the game's own CreateFeature,
   and put into the block only for the duration of eye 1's create. */
struct CreateField
{
    const char* name;
    bool        isInt;
    bool        have;
    unsigned    u;
    int         i;
};

struct CreateSnapshot
{
    const NVSDK_NGX_Handle* handle;
    CreateField f[8];
};

CreateSnapshot g_createSnap = {
    nullptr,
    { { "Width", false }, { "Height", false }, { "OutWidth", false }, { "OutHeight", false },
      { "PerfQualityValue", true }, { "DLSS.Feature.Create.Flags", true },
      { "DLSS.Enable.Output.Subrects", true }, { "RTXValue", true } }
};

void snapshot_read(const NVSDK_NGX_Parameter* p, CreateField* f, int n)
{
    for (int k = 0; k < n; ++k)
    {
        f[k].have = f[k].isInt ? NGX_SUCCEED(p->Get(f[k].name, &f[k].i))
                               : NGX_SUCCEED(p->Get(f[k].name, &f[k].u));
    }
}

void snapshot_write(NVSDK_NGX_Parameter* p, const CreateField* f, int n)
{
    for (int k = 0; k < n; ++k)
    {
        if (!f[k].have)
            continue;
        if (f[k].isInt)
            p->Set(f[k].name, f[k].i);
        else
            p->Set(f[k].name, f[k].u);
    }
}

bool stereo_ngx_enabled()
{
    static bool cached = true;
    static bool have = false;
    if (!have) { have = true; cached = config_bool("stereo_ngx_eye1", true); }
    return cached;
}

void stereo_twin_release(const char* why)
{
    if (g_stereoTwin != nullptr && g_realRelease != nullptr)
    {
        g_realRelease(g_stereoTwin);
        BVR_INFO("Stereo DLSS: the second eye's DLSS feature was released (%s).", why);
    }
    g_stereoTwin = nullptr;
    g_stereoPairedWith = nullptr;
}

void stereo_missed(const char* why)
{
    ++g_stereoMissed;
    g_stereoWhy = why;
}

bool stereo_evaluate_eye1(ID3D11DeviceContext* ctx, const NVSDK_NGX_Handle* feature,
                          NVSDK_NGX_Parameter* p, void* callback, ID3D11Resource* output)
{
    ID3D11Resource* color = nullptr;
    p->Get("Color", &color);
    ID3D11Resource* colorTwin = bvr::stereo_twin_for_read(color);
    if (colorTwin == nullptr)
    {
        stereo_missed("the colour input does not differ between the eyes");
        return false;
    }
    colorTwin->Release();       /* fetched again with the other inputs below */

    if (g_stereoTwin != nullptr && g_stereoPairedWith != feature)
        stereo_twin_release("the game is evaluating a different DLSS feature");

    if (g_stereoTwin == nullptr)
    {
        if (g_stereoFailedFor == feature)
        {
            stereo_missed("no eye-1 feature (its creation failed)");
            return false;
        }
        if (g_realCreate == nullptr)
        {
            stereo_missed("CreateFeature is not hooked");
            return false;
        }

        constexpr int kFields = static_cast<int>(sizeof(g_createSnap.f) / sizeof(g_createSnap.f[0]));
        const bool useSnap = g_createSnap.handle == feature;
        CreateField saved[kFields];
        memcpy(saved, g_createSnap.f, sizeof(saved));
        snapshot_read(p, saved, kFields);
        if (useSnap)
            snapshot_write(p, g_createSnap.f, kFields);

        /* THE SIZES FROM THE TEXTURES THEMSELVES (TEST 187). Neither block can be
           trusted for them: TEST 184's create-time record had Width = render, and
           TEST 186's came back output-first and failed 0xBAD00005 again - so the
           game fills that block in more than one order. What DLSS is actually
           handed cannot lie: the colour input is the render size, the output is
           the output size. */
        {
            auto size_of = [](ID3D11Resource* r, unsigned* w, unsigned* h) {
                ID3D11Texture2D* t = nullptr;
                if (r == nullptr ||
                    FAILED(r->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&t)) || !t)
                    return false;
                D3D11_TEXTURE2D_DESC d = {};
                t->GetDesc(&d);
                t->Release();
                *w = d.Width;
                *h = d.Height;
                return true;
            };
            unsigned rw = 0, rh = 0, ow = 0, oh = 0;
            ID3D11Resource* colorRes = nullptr;
            p->Get("Color", &colorRes);
            if (size_of(colorRes, &rw, &rh) && size_of(output, &ow, &oh))
            {
                p->Set("Width", rw);
                p->Set("Height", rh);
                p->Set("OutWidth", ow);
                p->Set("OutHeight", oh);
            }
        }

        NVSDK_NGX_Handle* made = nullptr;
        const NVSDK_NGX_Result cr = g_realCreate(ctx, g_stereoFeatureId, p, &made);

        unsigned w = 0, h = 0, ow = 0, oh = 0;
        p->Get("Width", &w);
        p->Get("Height", &h);
        p->Get("OutWidth", &ow);
        p->Get("OutHeight", &oh);
        snapshot_write(p, saved, kFields);       /* the game's block as it was */

        if (!NGX_SUCCEED(cr) || made == nullptr)
        {
            g_stereoFailedFor = feature;
            BVR_WARN("Stereo DLSS: could not create a DLSS feature for the second eye "
                     "(0x%08X) from %ux%u -> %ux%u (%s). Eye 1 will show eye 0's DLSS "
                     "image - flat, no depth.", cr, w, h, ow, oh,
                     useSnap ? "the game's own creation values"
                             : "NO record of the game's creation - the evaluation block as is");
            stereo_missed("eye-1 feature creation failed");
            return false;
        }

        g_stereoTwin = made;
        g_stereoPairedWith = feature;
        g_stereoFailedFor = nullptr;
        g_stereoReset = true;
        BVR_INFO("Stereo DLSS: created the second eye's own DLSS feature (%p, beside the "
                 "game's %p) for %ux%u -> %ux%u. Eye 1 is now resolved from its own colour, "
                 "depth and motion vectors, with a history of its own.", (void*)made,
                 (void*)feature, w, h, ow, oh);
    }

    static const char* const kInputs[] = {
        "Color", "Depth", "MotionVectors", "ExposureTexture",
        "TransparencyMask", "DLSS.Input.Bias.Current.Color.Mask",
    };
    constexpr int kN = static_cast<int>(sizeof(kInputs) / sizeof(kInputs[0]));

    /* NGX's Get hands back its own pointers, not AddRef'd ones. */
    ID3D11Resource* orig[kN] = {};
    ID3D11Resource* twin[kN] = {};
    for (int i = 0; i < kN; ++i)
    {
        p->Get(kInputs[i], &orig[i]);
        if (orig[i] == nullptr)
            continue;
        twin[i] = bvr::stereo_twin_for_read(orig[i]);
        if (twin[i] != nullptr)
            p->Set(kInputs[i], twin[i]);
    }

    bool ok = false;
    ID3D11Resource* outTwin = bvr::stereo_twin_for_write(ctx, output);
    if (outTwin != nullptr)
    {
        p->Set("Output", outTwin);
        int reset = 0;
        p->Get("Reset", &reset);
        const bool forceReset = g_stereoReset;
        if (forceReset)
            p->Set("Reset", 1);

        if (bvr::frame_map_capturing())
        {
            static const char* const kNames[] = {
                "Color", "Output", "Depth", "MotionVectors", "ExposureTexture",
                "TransparencyMask", "DLSS.Input.Bias.Current.Color.Mask",
            };
            constexpr int kNames_n = static_cast<int>(sizeof(kNames) / sizeof(kNames[0]));
            ID3D11Resource* res[kNames_n] = {};
            for (int i = 0; i < kNames_n; ++i)
                p->Get(kNames[i], &res[i]);
            float jx = 0.0f, jy = 0.0f;
            p->Get("Jitter.Offset.X", &jx);
            p->Get("Jitter.Offset.Y", &jy);
            bvr::frame_map_ngx(ctx, kNames, res, kNames_n, jx, jy, forceReset ? 1 : reset,
                               "NGX-EVALUATE-EYE1");
        }

        LARGE_INTEGER t0 = {}, t1 = {}, freq = {};
        QueryPerformanceCounter(&t0);
        NVSDK_NGX_Result r2 = 0;
        {
            /* Anything NGX does on the context in here is ours, not the engine's
               frame - not tracked, not duplicated. */
            bvr::StereoIgnore ignore;
            r2 = g_realEvaluate(ctx, g_stereoTwin, p, callback);
        }
        QueryPerformanceCounter(&t1);
        bvr::stereo_perf_ngx_cpu(t1.QuadPart - t0.QuadPart);
        bvr::stereo_perf_ngx_stamp(ctx, 2);
        QueryPerformanceFrequency(&freq);
        if (freq.QuadPart > 0)
            g_stereoCpuMs += static_cast<double>(t1.QuadPart - t0.QuadPart) * 1000.0 /
                             static_cast<double>(freq.QuadPart);

        if (forceReset)
            p->Set("Reset", reset);
        p->Set("Output", output);
        outTwin->Release();

        ok = NGX_SUCCEED(r2);
        if (ok)
        {
            g_stereoReset = false;
            ++g_stereoEvals;
        }
        else
        {
            static bool warned = false;
            if (!warned)
            {
                warned = true;
                BVR_WARN("Stereo DLSS: the second eye's evaluation returned 0x%08X; eye 1 "
                         "falls back to eye 0's DLSS image for those frames.", r2);
            }
            stereo_missed("the eye-1 evaluation returned an error");
        }
    }
    else
    {
        stereo_missed("the DLSS output cannot have a twin");
    }

    for (int i = 0; i < kN; ++i)
    {
        if (twin[i] != nullptr)
        {
            p->Set(kInputs[i], orig[i]);
            twin[i]->Release();
        }
    }
    return ok;
}

void stereo_second_eye(ID3D11DeviceContext* ctx, const NVSDK_NGX_Handle* feature,
                       const NVSDK_NGX_Parameter* params, void* callback,
                       NVSDK_NGX_Result first)
{
    if (!bvr::stereo_active() || !stereo_ngx_enabled())
    {
        if (g_stereoTwin != nullptr)
            stereo_twin_release("native stereo is off");
        g_stereoReset = true;
        g_stereoLastLog = 0;
        return;
    }
    if (ctx == nullptr || params == nullptr)
        return;

    NVSDK_NGX_Parameter* p = const_cast<NVSDK_NGX_Parameter*>(params);
    ID3D11Resource* output = nullptr;
    p->Get("Output", &output);
    if (output == nullptr)
        return;

    bool produced = false;
    if (!NGX_SUCCEED(first))
        stereo_missed("the engine's own evaluation failed");
    else
        produced = stereo_evaluate_eye1(ctx, feature, p, callback, output);

    /* Whatever happened, eye 1 must never read a STALE eye-1 output: without a
       fresh one it reads the shared original, which is this frame's eye 0. */
    bvr::stereo_mark_output(output, produced);

    const uint64_t now = GetTickCount64();
    if (g_stereoLastLog == 0)
        g_stereoLastLog = now;
    else if (now - g_stereoLastLog >= 5000)
    {
        g_stereoLastLog = now;
        BVR_INFO("Stereo DLSS: %llu second-eye evaluation(s) in ~5 s, %llu frame(s) without "
                 "one (last reason: %s); %.2f ms CPU per evaluation; eye-1 feature %p.",
                 static_cast<unsigned long long>(g_stereoEvals),
                 static_cast<unsigned long long>(g_stereoMissed),
                 *g_stereoWhy ? g_stereoWhy : "none",
                 g_stereoEvals ? g_stereoCpuMs / static_cast<double>(g_stereoEvals) : 0.0,
                 (void*)g_stereoTwin);
        g_stereoEvals = 0;
        g_stereoMissed = 0;
        g_stereoWhy = "";
        g_stereoCpuMs = 0.0;
    }
}

NVSDK_NGX_Result __cdecl CreateFeature_hook(ID3D11DeviceContext* ctx, int featureId,
                                            NVSDK_NGX_Parameter* params,
                                            NVSDK_NGX_Handle** outHandle)
{
    const NVSDK_NGX_Result r = g_realCreate(ctx, featureId, params, outHandle);

    /* Which feature kind the game makes, for native stereo's own. Recording it
       creates nothing and changes nothing below. */
    if (NGX_SUCCEED(r) && outHandle != nullptr && *outHandle != nullptr)
    {
        g_stereoFeatureId = featureId;
        if (params != nullptr)
        {
            snapshot_read(params, g_createSnap.f,
                          static_cast<int>(sizeof(g_createSnap.f) / sizeof(g_createSnap.f[0])));
            g_createSnap.handle = *outHandle;
        }
    }

    if (!per_eye_enabled() || !NGX_SUCCEED(r) || outHandle == nullptr || *outHandle == nullptr)
        return r;

    /* A previous pair is stale the moment a new feature is created - the eye
       target has been resized or the mission has changed - so it goes first. */
    if (g_featureTwin != nullptr && g_realRelease != nullptr)
    {
        g_realRelease(g_featureTwin);
        g_featureTwin = nullptr;
    }

    g_featureGame = *outHandle;

    NVSDK_NGX_Handle* twin = nullptr;
    const NVSDK_NGX_Result tr = g_realCreate(ctx, featureId, params, &twin);
    if (NGX_SUCCEED(tr) && twin != nullptr)
    {
        g_featureTwin = twin;
        BVR_INFO("NGX per-eye: created a SECOND DLSS feature (%p beside the game's "
                 "%p). Each eye now accumulates its own history, so a frame is "
                 "never resolved against the other eye's image - which is what the "
                 "65 mm alternation has been doing since AFR was written.",
                 (void*)twin, (void*)*outHandle);
    }
    else
    {
        BVR_WARN("NGX per-eye: the second CreateFeature failed (0x%08X). Falling "
                 "back to one shared history - the smear stays.", tr);
    }

    return r;
}

NVSDK_NGX_Result __cdecl ReleaseFeature_hook(NVSDK_NGX_Handle* handle)
{
    /* Native stereo's eye-1 feature goes with the game feature it was paired with. */
    if (handle != nullptr && handle == g_stereoPairedWith)
        stereo_twin_release("the game released its DLSS feature");
    if (handle != nullptr && handle == g_stereoFailedFor)
        g_stereoFailedFor = nullptr;

    if (handle != nullptr && handle == g_featureGame && g_featureTwin != nullptr)
    {
        g_realRelease(g_featureTwin);
        g_featureTwin = nullptr;
        g_featureGame = nullptr;
    }
    return g_realRelease(handle);
}

/* Which handle this evaluation should use. Eye 1 gets the twin; eye 0 and any
   unknown eye get the game's own, so a failure anywhere is just the old
   behaviour. */
const NVSDK_NGX_Handle* pick_feature(const NVSDK_NGX_Handle* incoming)
{
    if (!per_eye_enabled() || g_featureTwin == nullptr || incoming != g_featureGame)
        return incoming;

    if (bvr::xr_current_afr_eye() != 1)
        return incoming;

    const uint64_t n = g_twinUsed.fetch_add(1, std::memory_order_relaxed);
    if (n == 0)
        BVR_INFO("NGX per-eye: LIVE. Eye 1 is being resolved against its own "
                 "history now, eye 0 against the game's.");
    return g_featureTwin;
}

} /* namespace */
