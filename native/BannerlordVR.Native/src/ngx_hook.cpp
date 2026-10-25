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
#include <algorithm>
#include <atomic>
#include <cmath>
#include <windows.h>
#include <mutex>
#include <cstdio>
#include <vector>

/* NVIDIA's header, not held to /W4. */
#pragma warning(push, 0)
#include "nvapi.h"
#pragma warning(pop)


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

/* For ngx_mode_name. */
std::atomic<int>      g_lastQuality{ -1 };
std::atomic<uint64_t> g_lastEvalMs{ 0 };

/* For ngx_jitter. Render-resolution pixels, as DLSS was handed them. */
std::atomic<float>    g_jitterX{ 0.0f };
std::atomic<float>    g_jitterY{ 0.0f };

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
/* TEST 794-799: the DLSS preset (defined beside CreateFeature_hook). */
unsigned preset_now();
unsigned live_preset();   /* TEST 812 */
void set_preset_hints(NVSDK_NGX_Parameter* p, unsigned preset);
extern unsigned g_gamePreset;
/* TEST 800, defined at the end. */
void dlss_dump_output(ID3D11DeviceContext* ctx, const NVSDK_NGX_Parameter* params, const void* feature);

/* Defined below (TEST 594). Our feature when the game's was made for other sizes;
   nullptr = the game's fits. */
const NVSDK_NGX_Handle* fit_feature(ID3D11DeviceContext* ctx, const NVSDK_NGX_Handle* feature,
                                    const NVSDK_NGX_Parameter* params);

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

/* Defined below (TEST 430). Resolves one eye as a DLAA periphery plus a DLSS
   window instead of the game's full-size upscale; false = not done, so the
   caller evaluates as it always did. */
bool tex2d_size(ID3D11Resource* r, unsigned* w, unsigned* h);   /* defined below */
bool fovea_enabled();
bool fovea_eval(ID3D11DeviceContext* ctx, const NVSDK_NGX_Handle* game,
                NVSDK_NGX_Parameter* p, void* callback, int eye, ID3D11Resource* out);
bool g_fovGameStale = false;   /* the game's eye-0 feature idled while foveated */

/* TEST 777: THE GAME'S DLSS SETTINGS QUERY AND OUR EVALUATIONS TAKE TURNS.
 *
   TEST 776 caught it: the game thread runs its "optimal settings" query on the
   very block the render thread evaluates with - Width/Height = display from
   TaleWorlds.Native+0x2F243A, then NVIDIA's callback writes OutWidth/OutHeight =
   render size - several times a frame, 4796 of ~42000 writes DURING a DLSS call.
   Cheeky then reads torn sizes and rejects (code 6), and our size fix landing
   mid-query feeds the game a wrong optimal size. The query (TaleWorlds.Native
   +0x2F2390 in this build: Get the callback, Set Width/Height/PerfQualityValue/
   RTXValue, call it, Get the results - no waits) is found by its bytes and run
   under one lock, which every Cheeky evaluation and eye-1 create also hold. A
   build whose bytes differ is left unhooked and says so. ngx_query_lock = 0 off.
   CONFIRMED 2026-10-18: no rejections, no stutter. */
std::recursive_mutex g_ngxBlockLock;
typedef uintptr_t(__fastcall* PFN_Query)(void*, void*, void*, uintptr_t);
PFN_Query g_realQuery = nullptr;

/* TEST 793: DOES THE RENDERED JITTER MATCH THE ONE DLSS IS TOLD?
   DLSS gets Jitter.Offset in render pixels; the projection moved the image by
   ndc = 2 * px / width. So 2 * px / ndc is the width the engine converted with -
   it must be the colour's own width (2005 in Quality). Medians over ~5 s, with
   the frames the jitter takes to repeat. Log only; ngx_jitter_probe = 0 is off. */
void jitter_probe(NVSDK_NGX_Parameter* p, float jx, float jy, int quality)
{
    static const bool on = bvr::config_bool("ngx_jitter_probe", false);
    if (!on) return;
    constexpr int kN = 256;
    static float    s_w[kN], s_h[kN], s_ring[kN];
    static int      s_nw = 0, s_nh = 0, s_ringN = 0;
    static uint64_t s_last = 0;
    float nx = 0.0f, ny = 0.0f;
    if (bvr::stereo_render_jitter(&nx, &ny))
    {
        if (fabsf(nx) > 1e-7f && fabsf(jx) > 0.02f && s_nw < kN) s_w[s_nw++] = 2.0f * jx / nx;
        if (fabsf(ny) > 1e-7f && fabsf(jy) > 0.02f && s_nh < kN) s_h[s_nh++] = 2.0f * jy / ny;
    }
    s_ring[s_ringN++ % kN] = jx;

    const uint64_t now = GetTickCount64();
    if (now - s_last < 5000) return;
    s_last = now;
    ID3D11Resource* color = nullptr;
    p->Get("Color", &color);
    unsigned cw = 0, ch = 0;
    tex2d_size(color, &cw, &ch);
    auto median = [](float* v, int n) { std::sort(v, v + n); return n ? v[n / 2] : 0.0f; };
    /* The smallest p for which the last 64 jitters repeat every p frames. */
    int period = 0;
    if (s_ringN >= 128)
        for (int q = 1; q <= 64 && period == 0; ++q)
        {
            bool same = true;
            for (int i = 0; i < 64 && same; ++i)
                same = s_ring[(s_ringN - 1 - i) % kN] == s_ring[(s_ringN - 1 - i - q) % kN];
            if (same) period = q;
        }
    BVR_INFO("Jitter probe (TEST 793, PerfQualityValue %d, colour %ux%u): the engine converted "
             "the jitter with width %.1f (%d frames), height %.1f (%d frames) - both should be the "
             "colour size, a negative is only the sign convention. Jitter repeats every %d frames "
             "(0 = not within 64; DLSS wants ~8 x ratio^2: 18 Quality, 32 Performance).",
             quality, cw, ch, median(s_w, s_nw), s_nw, median(s_h, s_nh), s_nh, period);
    s_nw = s_nh = 0;
}

uintptr_t __fastcall query_hook(void* self, void* out, void* in, uintptr_t quality)
{
    std::lock_guard<std::recursive_mutex> guard(g_ngxBlockLock);
    return g_realQuery(self, out, in, quality);
}

/* One match of `sig` (-1 = any byte) in TaleWorlds.Native's code, or null with the
   count in *matches. A build whose bytes differ finds nothing and is left alone. */
const uint8_t* find_native_code(const int* sig, size_t len, int* matches, const uint8_t** base_out)
{
    *matches = 0;
    HMODULE mod = GetModuleHandleW(L"TaleWorlds.Native.dll");
    if (mod == nullptr)
        return nullptr;
    const auto* base = reinterpret_cast<const uint8_t*>(mod);
    *base_out = base;
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(
        base + reinterpret_cast<const IMAGE_DOS_HEADER*>(base)->e_lfanew);
    const IMAGE_SECTION_HEADER* sec = IMAGE_FIRST_SECTION(nt);
    const uint8_t* found = nullptr;
    for (unsigned i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++sec)
    {
        if ((sec->Characteristics & IMAGE_SCN_MEM_EXECUTE) == 0)
            continue;
        const uint8_t* b = base + sec->VirtualAddress;
        const size_t n = sec->Misc.VirtualSize;
        for (size_t at = 0; at + len <= n; ++at)
        {
            size_t k = 0;
            while (k < len && (sig[k] < 0 || b[at + k] == sig[k]))
                ++k;
            if (k == len)
            {
                found = b + at;
                ++*matches;
            }
        }
    }
    return *matches == 1 ? found : nullptr;
}

void install_query_lock()
{
    static bool tried = false;
    if (tried)
        return;
    tried = true;
    if (!bvr::config_bool("ngx_query_lock", true))
        return;
    /* -1 = any byte (the rip-relative lea). */
    static const int kSig[] = {
        0x40,0x53,0x56,0x57,0x41,0x56,0x48,0x83,0xEC,0x48,0x33,0xC0,0x45,0x8B,0xF1,0x49,0x8B,0xF0,
        0x48,0x8B,0xFA,0x48,0x8B,0xD9,0x48,0x89,0x44,0x24,0x20,0x48,0x89,0x44,0x24,0x28,0x48,0x89,
        0x44,0x24,0x70,0x38,0x81,0xD8,0x00,0x00,0x00,0x75,0x1D,0xC7,0x81,0xF0,0x00,0x00,0x00,0x0A,
        0xD7,0x23,0x3C,0x49,0x8B,0x00,0x48,0x89,0x02,0x48,0x8B,0xC2,0x48,0x83,0xC4,0x48,0x41,0x5E,
        0x5F,0x5E,0x5B,0xC3,0x48,0x89,0x6C,0x24,0x78,0x48,0x8D,0x15,-1,-1,-1,-1,0x48,0x8B,0xA9,0xE0,
        0x00,0x00,0x00 };
    int matches = 0;
    const uint8_t* base = nullptr;
    const uint8_t* found = find_native_code(kSig, sizeof(kSig) / sizeof(kSig[0]), &matches, &base);
    if (found == nullptr)
    {
        BVR_WARN("Cheeky TEST 777: the game's DLSS settings query was found %d time(s), not "
                 "once (another game build?); it is NOT locked.", matches);
        return;
    }
    if (MH_CreateHook(const_cast<uint8_t*>(found), reinterpret_cast<LPVOID>(&query_hook),
                      reinterpret_cast<LPVOID*>(&g_realQuery)) != MH_OK ||
        MH_EnableHook(const_cast<uint8_t*>(found)) != MH_OK)
    {
        BVR_WARN("Cheeky TEST 777: MinHook refused the game's DLSS settings query; NOT locked.");
        return;
    }
    BVR_INFO("Cheeky TEST 777: the game's DLSS settings query (TaleWorlds.Native.dll+0x%llX) now "
             "takes turns with DLSS evaluations.",
             static_cast<unsigned long long>(found - base));
}

/* TEST 818: THE ENGINE'S POST TARGETS AT THE VIEW'S OWN SIZE.
 *
   The engine sizes every render-scaled target (descriptor +0x4C set) in its
   allocator (TaleWorlds.Native+0x22FA60, the descriptor's vtable slot 0x38, called
   with the view) as ceil(view render size +0x8590/+0x8594 x the descriptor's double
   scale +0x58/+0x60). That scale is (re)computed first, every call, by +0x22F990:
   the target's own float scale (+0x08/+0x0C) x ONE upscale factor per DLSS mode,
   taken from the flat window (2560x1440: 2560/1707, 1440/960, ...). For an eye
   whose own ratio differs by a hair, the round-up lands a pixel off its output
   (+0x8588/+0x858C): 2380 -> 2381. DLSS's correct 2380 output is then resampled
   into 2381 and the final pass squeezes it back into the 2380 eye - two linear
   resamples, the "clean but blurry" Quality/Balanced that came and went with the
   crop value (1 in 3 Quality sizes; Balanced its own set), VD's crop and the game
   build alike (TESTs 792-817). So after +0x22F990, a scale whose result lands
   within 2 px of the view's output but not ON it becomes exactly output/render (a
   hair under, so the round-up lands on it). Half/quarter targets, fixed-size ones
   and views without DLSS (render = output) are untouched. RESULT 818a: hooking the
   allocator itself did nothing - +0x22F990 rewrote the scale inside it.
   ngx_post_size_fix = 0 is off. */
typedef uintptr_t(__fastcall* PFN_PostScale)(uint8_t*, uint8_t*, uintptr_t, uintptr_t);
PFN_PostScale g_realPostScale = nullptr;

bool fit_scale(double* scale, int render, int output)
{
    if (render <= 0 || output <= 0 || render == output || !(*scale > 0.0))
        return false;
    const double size = render * *scale;
    if (fabs(size - output) >= 2.0 || ceil(size) == static_cast<double>(output))
        return false;
    *scale = (output - 1e-6) / render;
    return true;
}

uintptr_t __fastcall post_scale_hook(uint8_t* desc, uint8_t* view, uintptr_t a3, uintptr_t a4)
{
    const uintptr_t r = g_realPostScale(desc, view, a3, a4);
    if (desc == nullptr || view == nullptr || desc[0x4C] == 0)
        return r;

    double* sw = reinterpret_cast<double*>(desc + 0x58);
    double* sh = reinterpret_cast<double*>(desc + 0x60);
    const double ow = *sw, oh = *sh;
    const int outW = *reinterpret_cast<const int*>(view + 0x8588);
    const int outH = *reinterpret_cast<const int*>(view + 0x858C);
    const int rw = *reinterpret_cast<const int*>(view + 0x8590);
    const int rh = *reinterpret_cast<const int*>(view + 0x8594);
    const bool fw = fit_scale(sw, rw, outW);
    const bool fh = fit_scale(sh, rh, outH);
    if (fw || fh)
    {
        static std::atomic<int> s_logged{0};
        if (s_logged.fetch_add(1, std::memory_order_relaxed) < 12)
            BVR_INFO("Post size fix (TEST 818): a target the engine would make %.0fx%.0f for a %dx%d "
                     "view (render %dx%d) is made %.0fx%.0f.", ceil(rw * ow), ceil(rh * oh), outW, outH,
                     rw, rh, ceil(rw * *sw), ceil(rh * *sh));
    }
    return r;
}

void install_post_size_fix()
{
    static bool tried = false;
    if (tried)
        return;
    tried = true;
    if (!bvr::config_bool("ngx_post_size_fix", true))
        return;
    /* +0x22F990's head: mov [rsp+8],rbx / push rdi / sub rsp,30h / cmp byte [rcx+38h],0 /
       mov rbx,rcx / jz / cmp qword [rcx+40h],0 / jz / mov rcx,[rdx+8580h] / mov rax,[rcx] /
       call [rax+80h]. */
    static const int kSig[] = {
        0x48,0x89,0x5C,0x24,0x08,0x57,0x48,0x83,0xEC,0x30,0x80,0x79,0x38,0x00,0x48,0x8B,0xD9,0x74,
        0x5D,0x48,0x83,0x79,0x40,0x00,0x74,0x56,0x48,0x8B,0x8A,0x80,0x85,0x00,0x00,0x48,0x8B,0x01,
        0xFF,0x90,0x80,0x00,0x00,0x00 };
    int matches = 0;
    const uint8_t* base = nullptr;
    const uint8_t* found = find_native_code(kSig, sizeof(kSig) / sizeof(kSig[0]), &matches, &base);
    if (found == nullptr)
    {
        BVR_WARN("Post size fix (TEST 818): the engine's post-target scale function was found %d "
                 "time(s), not once (another game build?). A cropped eye may stay soft in DLSS "
                 "Quality/Balanced.", matches);
        return;
    }
    if (MH_CreateHook(const_cast<uint8_t*>(found), reinterpret_cast<LPVOID>(&post_scale_hook),
                      reinterpret_cast<LPVOID*>(&g_realPostScale)) != MH_OK ||
        MH_EnableHook(const_cast<uint8_t*>(found)) != MH_OK)
    {
        BVR_WARN("Post size fix (TEST 818): MinHook refused the engine's post-target scale function.");
        return;
    }
    BVR_INFO("Post size fix (TEST 818): the engine's post-target scale (TaleWorlds.Native.dll+0x%llX) "
             "now fits full-size targets to each view's output.",
             static_cast<unsigned long long>(found - base));
}

/* g_realEvaluate, made safe for Cheeky Foveated DLSS (when it was started).
   Cheeky reads the evaluate block's sizes; the game fills them in more than one
   order (TEST 187) - its own optimal-settings query leaves Width = display and
   OutWidth = render - and Cheeky rejected those calls (0xBAD00005, its code 6
   "Invalid crop dimensions or resource bounds"): the game re-created DLSS, a
   stutter, a white flash and a flat right eye. So the block gets the sizes the
   textures really are, and keeps them (TEST 774: putting the game's back made
   Cheeky read them), under the lock the game's query takes too (TEST 777). */
std::atomic<bool> g_cheekyRunning{ false };

/* TEST 797: the VR panel's dlss_preset, read at the next DLSS create. */
constexpr int    kPresetUnset = -1000;   /* the panel has not said: the cfg's */
std::atomic<int> g_presetChoice{ kPresetUnset };

NVSDK_NGX_Result evaluate_sized(ID3D11DeviceContext* ctx, const NVSDK_NGX_Handle* feature,
                                const NVSDK_NGX_Parameter* params, void* callback, int eye)
{
    if (!g_cheekyRunning.load(std::memory_order_relaxed) || params == nullptr)
        return g_realEvaluate(ctx, feature, params, callback);

    install_query_lock();
    std::lock_guard<std::recursive_mutex> turn(g_ngxBlockLock);
    NVSDK_NGX_Parameter* p = const_cast<NVSDK_NGX_Parameter*>(params);
    ID3D11Resource* color = nullptr;
    ID3D11Resource* out = nullptr;
    p->Get("Color", &color);
    p->Get("Output", &out);
    unsigned tex[4] = {};
    if (tex2d_size(color, &tex[0], &tex[1]) && tex2d_size(out, &tex[2], &tex[3]))
    {
        p->Set("Width", tex[0]);
        p->Set("Height", tex[1]);
        p->Set("OutWidth", tex[2]);
        p->Set("OutHeight", tex[3]);
    }
    const NVSDK_NGX_Result r = g_realEvaluate(ctx, feature, params, callback);
    static int s_fails[2] = {};
    if (!NGX_SUCCEED(r) && s_fails[eye & 1]++ < 3)
        BVR_WARN("Cheeky: eye %d DLSS evaluation returned 0x%08X (textures %ux%u -> %ux%u).",
                 eye, static_cast<unsigned>(r), tex[0], tex[1], tex[2], tex[3]);
    return r;
}

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
    g_lastQuality.store(quality, std::memory_order_relaxed);
    g_lastEvalMs.store(GetTickCount64(), std::memory_order_relaxed);

    /* EVERY FRAME, NOT ONLY IN THE REPORT BLOCK BELOW.
     *
       The reconnaissance block reads these too, but it runs a dozen times in a
       run and the value is different every single frame - that is what a jitter
       sequence IS. DR needs THIS frame's, so it is read on the hot path: two
       parameter fetches on a call that is already doing an upscale. */
    if (params != nullptr)
    {
        float jx = 0.0f, jy = 0.0f;
        NVSDK_NGX_Parameter* jp = const_cast<NVSDK_NGX_Parameter*>(params);
        jp->Get("Jitter.Offset.X", &jx);
        jp->Get("Jitter.Offset.Y", &jy);
        g_jitterX.store(jx, std::memory_order_relaxed);
        g_jitterY.store(jy, std::memory_order_relaxed);
        jitter_probe(jp, jx, jy, quality);
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
    const NVSDK_NGX_Handle* useFeature = fit_feature(ctx, feature, params);   /* TEST 594 */
    if (useFeature == nullptr)
        useFeature = pick_feature(feature);

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

    /* TEST 430: the game's eye, foveated. The game's own feature idles meanwhile,
       so the first evaluation it gets back is a Reset - its history is stale. */
    NVSDK_NGX_Result r = 1;   /* NVSDK_NGX_Result_Success */
    ID3D11Resource* fovOut = nullptr;
    if (params != nullptr && fovea_enabled())
        const_cast<NVSDK_NGX_Parameter*>(params)->Get("Output", &fovOut);
    if (fovOut != nullptr &&
        fovea_eval(ctx, feature, const_cast<NVSDK_NGX_Parameter*>(params), callback, 0, fovOut))
    {
        g_fovGameStale = true;
    }
    else
    {
        int gameReset = 0;
        const bool resetGame = g_fovGameStale && params != nullptr;
        if (resetGame)
        {
            const_cast<NVSDK_NGX_Parameter*>(params)->Get("Reset", &gameReset);
            const_cast<NVSDK_NGX_Parameter*>(params)->Set("Reset", 1);
        }
        r = evaluate_sized(ctx, useFeature, params, callback, 0);
        dlss_dump_output(ctx, params, useFeature);
        if (resetGame)
            const_cast<NVSDK_NGX_Parameter*>(params)->Set("Reset", gameReset);
        g_fovGameStale = false;
    }
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

const char* ngx_mode_name()
{
    const uint64_t t = g_lastEvalMs.load(std::memory_order_relaxed);
    if (t == 0 || GetTickCount64() - t > 1000)
        return "no DLSS";
    switch (g_lastQuality.load(std::memory_order_relaxed))
    {
    case 0:  return "DLSS Performance";
    case 1:  return "DLSS Balanced";
    case 2:  return "DLSS Quality";
    case 3:  return "DLSS Ultra Performance";
    case 4:  return "DLSS Ultra Quality";
    case 5:  return "DLAA";
    default: return "DLSS, mode unknown";
    }
}

bool ngx_jitter(float* px, float* py)
{
    /* The same staleness test ngx_mode_name uses. A jitter from a second ago is
       not this frame's, and applying it would move the depth lookup by a
       fraction of a pixel in an arbitrary direction - which is the artifact, not
       the fix. */
    const uint64_t t = g_lastEvalMs.load(std::memory_order_relaxed);
    if (t == 0 || GetTickCount64() - t > 1000)
        return false;

    if (px) *px = g_jitterX.load(std::memory_order_relaxed);
    if (py) *py = g_jitterY.load(std::memory_order_relaxed);
    return true;
}

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

    /* Borrowed context, so everything is put back. Same discipline as the DR
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
   first time native stereo evaluates - AFR and DR never cause one to exist - and
   released when the game releases the feature it is paired with, or on the first
   evaluation after native stereo is switched off. None of this touches the AFR
   per-eye pair above (g_featureTwin / pick_feature).
   =========================================================================== */

NVSDK_NGX_Handle*       g_stereoTwin = nullptr;
unsigned                g_stereoTwinW = 0;     /* the colour size it was made for */
unsigned                g_stereoTwinPreset = 0;   /* TEST 812: the preset it was made with */
unsigned                g_stereoTwinH = 0;
const NVSDK_NGX_Handle* g_stereoPairedWith = nullptr;
const NVSDK_NGX_Handle* g_stereoFailedFor = nullptr;
uint64_t                g_stereoRetryAt = 0;   /* GetTickCount64; ~0 = never */
int                     g_stereoFeatureId = 1;      /* NVSDK_NGX_Feature_SuperSampling */
bool                    g_stereoReset = true;
uint64_t                g_stereoEvals = 0;
uint64_t                g_stereoMissed = 0;
const char*             g_stereoWhy = "";
double                  g_stereoCpuMs = 0.0;
/* TEST 192: evaluations where an input had NO eye-1 twin, so eye 1 got eye 0's.
   Indexed like kInputs in stereo_evaluate_eye1. */
uint64_t                g_stereoShared[6] = {};
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

/* TEST 594b: the game's last few creations, newest first by g_snapNext. A
   released feature's values are still the right recipe for its sizes. */
CreateSnapshot g_snapHist[4] = {};
int            g_snapNext = 0;

bool snapshot_sizes_match(const CreateField* f, const unsigned s[4])
{
    if (!f[0].have || !f[1].have || !f[2].have || !f[3].have)
        return false;
    return (s[0] == f[0].u && s[1] == f[1].u && s[2] == f[2].u && s[3] == f[3].u) ||
           (s[0] == f[2].u && s[1] == f[3].u && s[2] == f[0].u && s[3] == f[1].u);
}

/* s = colour w/h, output w/h. Newest creation made for exactly those sizes. */
const CreateField* snapshot_for_sizes(const unsigned s[4])
{
    constexpr int n = static_cast<int>(sizeof(g_snapHist) / sizeof(g_snapHist[0]));
    for (int k = 1; k <= n; ++k)
    {
        const CreateSnapshot& c = g_snapHist[(g_snapNext - k + n) % n];
        if (c.handle != nullptr && snapshot_sizes_match(c.f, s))
            return c.f;
    }
    return nullptr;
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
    g_stereoTwinW = g_stereoTwinH = 0;
    g_stereoPairedWith = nullptr;
}

void stereo_missed(const char* why)
{
    ++g_stereoMissed;
    g_stereoWhy = why;
}

bool tex2d_size(ID3D11Resource* r, unsigned* w, unsigned* h)
{
    ID3D11Texture2D* t = nullptr;
    if (r == nullptr || FAILED(r->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&t)) || !t)
        return false;
    D3D11_TEXTURE2D_DESC d = {};
    t->GetDesc(&d);
    t->Release();
    *w = d.Width;
    *h = d.Height;
    return true;
}

/* ===========================================================================
   FOVEATED DLSS (TEST 430).

   DLSS costs by the pixels it writes: ~6 ms of TEST 428's 18 ms frame for two
   3516x3368 outputs, and VRS could not touch it. Most of those pixels are where
   the eye is not looking and the lens blurs them anyway. So each eye is resolved
   twice, small:

     - the whole view by DLSS in DLAA mode at the RENDER size - anti-aliased and
       temporally stable, a quarter of the pixels - then stretched to the eye;
     - a window around the lens axis by DLSS at the full output density, through
       NVIDIA's subrect parameters: the inputs read from an offset, the result
       written into a texture of the window's size;

   and one compute pass blends the window into the stretched periphery with a
   feathered edge, into the output the game handed DLSS. Each is a feature of
   its own, so each keeps its own history. The game's features stay alive and
   idle, and take over again - with a Reset - on any failure.

   The window is FIXED here. TEST 431 moves it with the gaze, which also means
   moving its history with it.
   =========================================================================== */

/* Every input DLSS can be given a subrect for. */
const char* const kFovBases[][2] = {
    { "DLSS.Input.Color.Subrect.Base.X", "DLSS.Input.Color.Subrect.Base.Y" },
    { "DLSS.Input.Depth.Subrect.Base.X", "DLSS.Input.Depth.Subrect.Base.Y" },
    { "DLSS.Input.MV.Subrect.Base.X", "DLSS.Input.MV.Subrect.Base.Y" },
    { "DLSS.Input.Translucency.Subrect.Base.X", "DLSS.Input.Translucency.Subrect.Base.Y" },
    { "DLSS.Input.Bias.Current.Color.Subrect.Base.X", "DLSS.Input.Bias.Current.Color.Subrect.Base.Y" },
};
constexpr int kFovBaseCount = static_cast<int>(sizeof(kFovBases) / sizeof(kFovBases[0]));
const char* const kFovDimW = "DLSS.Render.Subrect.Dimensions.Width";
const char* const kFovDimH = "DLSS.Render.Subrect.Dimensions.Height";

struct FovEye
{
    NVSDK_NGX_Handle* periph = nullptr;     /* DLAA, render -> render */
    NVSDK_NGX_Handle* fovea = nullptr;      /* the window, render -> output density */
    ID3D11Texture2D* pTex = nullptr;
    ID3D11ShaderResourceView* pSrv = nullptr;
    ID3D11Texture2D* fTex = nullptr;
    ID3D11ShaderResourceView* fSrv = nullptr;
    unsigned rw = 0, rh = 0, ow = 0, oh = 0;   /* render and output sizes they were made for */
    unsigned ww = 0, wh = 0;                   /* the window, render pixels */
    unsigned fw = 0, fh = 0;                   /* the window, output pixels */
    DXGI_FORMAT fmt = DXGI_FORMAT_UNKNOWN;
    bool fresh = true;                         /* the next evaluation resets history */
};
FovEye                  g_fov[2];
const NVSDK_NGX_Handle* g_fovPairedWith = nullptr;
const NVSDK_NGX_Handle* g_fovFailedFor = nullptr;
int                     g_fovFailures = 0;      /* 3 and it stays off for the session */

/* OUR OWN PARAMETER BLOCK (TEST 430b). TEST 430 wrote the window's subrect into
   the GAME's block and "restored" it to 0 - but a parameter the game never set
   cannot be unset (NGX has no remove), and DLSS rejected the leftovers: every
   evaluation after the first, the game's own included, failed 0xBAD00005 and the
   eyes froze, then went black. Ours are created and evaluated through a block
   of ours, which the game's inputs are copied into; the game's is only read. */
typedef NVSDK_NGX_Result(__cdecl* PFN_AllocateParameters)(NVSDK_NGX_Parameter** out);
NVSDK_NGX_Parameter* g_fovParams = nullptr;   /* ponytail: never destroyed - one per process */

NVSDK_NGX_Parameter* fov_params()
{
    static bool tried = false;
    if (g_fovParams != nullptr || tried)
        return g_fovParams;
    tried = true;

    HMODULE ngx = GetModuleHandleW(L"_nvngx.dll");
    if (ngx == nullptr)
        ngx = GetModuleHandleW(L"nvngx.dll");
    const auto alloc = ngx ? reinterpret_cast<PFN_AllocateParameters>(
                                 GetProcAddress(ngx, "NVSDK_NGX_D3D11_AllocateParameters"))
                           : nullptr;
    NVSDK_NGX_Parameter* made = nullptr;
    const NVSDK_NGX_Result r = alloc ? alloc(&made) : 0;
    if (!NGX_SUCCEED(r) || made == nullptr)
    {
        BVR_WARN("Foveated DLSS (TEST 430b): no parameter block of our own (%s, 0x%08X); "
                 "foveated DLSS stays off.", alloc ? "AllocateParameters failed"
                                                   : "AllocateParameters not exported", r);
        return nullptr;
    }
    g_fovParams = made;
    return made;
}

/* This frame's inputs from the game's block into ours. A resource the game does
   not give is cleared in ours, so last frame's never lingers. */
void fov_copy_inputs(NVSDK_NGX_Parameter* game, NVSDK_NGX_Parameter* ours)
{
    static const char* const kRes[] = {
        "Color", "Depth", "MotionVectors", "ExposureTexture", "TransparencyMask",
        "DLSS.Input.Bias.Current.Color.Mask",
    };
    for (const char* name : kRes)
    {
        ID3D11Resource* r = nullptr;
        if (!NGX_SUCCEED(game->Get(name, &r)))
            r = nullptr;
        ours->Set(name, r);
    }

    static const char* const kFloat[] = {
        "Jitter.Offset.X", "Jitter.Offset.Y", "MV.Scale.X", "MV.Scale.Y", "Sharpness",
        "DLSS.Pre.Exposure", "DLSS.Exposure.Scale",
    };
    for (const char* name : kFloat)
    {
        float v = 0.0f;
        if (NGX_SUCCEED(game->Get(name, &v)))
            ours->Set(name, v);
    }
}
ID3D11ComputeShader*    g_fovCs = nullptr;
ID3D11Buffer*           g_fovCb = nullptr;
ID3D11SamplerState*     g_fovSampler = nullptr;
bool                    g_fovShaderFailed = false;
uint64_t                g_fovEvals = 0, g_fovMissed = 0;
const char*             g_fovWhy = "";

/* The output's UAV, kept on the output so it goes when the output does.
   {5C1E7A20-9D4B-4F3E-8B61-2A7D0E9C4F18} */
const GUID kFovOutUav = { 0x5c1e7a20, 0x9d4b, 0x4f3e, { 0x8b, 0x61, 0x2a, 0x7d, 0x0e, 0x9c, 0x4f, 0x18 } };

const char* kFovShader = R"(
Texture2D<float4>   Periph : register(t0);
Texture2D<float4>   Fovea  : register(t1);
RWTexture2D<float4> Out    : register(u0);
SamplerState        Lin    : register(s0);

cbuffer Params : register(b0)
{
    float2 gOutSize;    // the eye's output, pixels
    float2 gWinBase;    // the window's top-left, output pixels
    float2 gWinSize;    // the window's size, output pixels
    float  gFeather;    // share of the window's half-size the blend takes
    float  gPad;
};

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= (uint)gOutSize.x || tid.y >= (uint)gOutSize.y)
        return;

    float2 here = float2(tid.xy) + 0.5;
    float4 c = Periph.SampleLevel(Lin, here / gOutSize, 0);

    float2 lp = here - gWinBase;
    if (all(lp > 0.0) && all(lp < gWinSize))
    {
        // 0 at the window's edge, 1 at its centre; smooth over the feather.
        float2 edge = min(lp, gWinSize - lp) / (0.5 * gWinSize);
        float  w = saturate(min(edge.x, edge.y) / max(gFeather, 0.001));
        w = w * w * (3.0 - 2.0 * w);
        c = lerp(c, Fovea[uint2(lp)], w);
    }
    Out[tid.xy] = c;
}
)";

struct FovParams
{
    float outW, outH, winX, winY, winW, winH, feather, pad;
};

bool fov_miss(const char* why)
{
    ++g_fovMissed;
    g_fovWhy = why;
    return false;
}

void fov_release_eye(FovEye& e)
{
    if (e.periph && g_realRelease) g_realRelease(e.periph);
    if (e.fovea && g_realRelease) g_realRelease(e.fovea);
    if (e.pSrv) e.pSrv->Release();
    if (e.pTex) e.pTex->Release();
    if (e.fSrv) e.fSrv->Release();
    if (e.fTex) e.fTex->Release();
    e = FovEye();
}

void fov_release(const char* why)
{
    if (g_fov[0].periph || g_fov[1].periph)
        BVR_INFO("Foveated DLSS (TEST 430): features released (%s).", why);
    fov_release_eye(g_fov[0]);
    fov_release_eye(g_fov[1]);
    g_fovPairedWith = nullptr;
}

bool fov_enabled_cfg()
{
    static const bool on = config_bool("dlss_foveated", false);
    return on;
}

/* A DLSS feature with the game's own creation values and our sizes, made through
   OUR block (`ours`); the game's (`game`) is only read for its preset hints. */
NVSDK_NGX_Handle* fov_create(ID3D11DeviceContext* ctx, NVSDK_NGX_Parameter* game,
                             NVSDK_NGX_Parameter* ours, const NVSDK_NGX_Handle* gameFeature,
                             unsigned rw, unsigned rh, unsigned ow, unsigned oh, int quality,
                             const char* what)
{
    constexpr int kFields = static_cast<int>(sizeof(g_createSnap.f) / sizeof(g_createSnap.f[0]));
    if (g_createSnap.handle == gameFeature)
        snapshot_write(ours, g_createSnap.f, kFields);

    static const char* const kHints[] = {
        "DLSS.Hint.Render.Preset.DLAA", "DLSS.Hint.Render.Preset.Quality",
        "DLSS.Hint.Render.Preset.Balanced", "DLSS.Hint.Render.Preset.Performance",
        "DLSS.Hint.Render.Preset.UltraPerformance", "DLSS.Hint.Render.Preset.UltraQuality",
    };
    for (const char* name : kHints)
    {
        unsigned v = 0;
        if (NGX_SUCCEED(game->Get(name, &v)))
            ours->Set(name, v);
    }
    ours->Set("CreationNodeMask", 1u);
    ours->Set("VisibilityNodeMask", 1u);
    ours->Set("DLSS.Enable.Output.Subrects", 0);
    ours->Set("Width", rw);
    ours->Set("Height", rh);
    ours->Set("OutWidth", ow);
    ours->Set("OutHeight", oh);
    if (quality >= 0)
        ours->Set("PerfQualityValue", quality);
    else
    {
        int q = 0;
        if (NGX_SUCCEED(game->Get("PerfQualityValue", &q)))
            ours->Set("PerfQualityValue", q);
    }

    NVSDK_NGX_Handle* made = nullptr;
    const NVSDK_NGX_Result r = g_realCreate(ctx, g_stereoFeatureId, ours, &made);

    if (!NGX_SUCCEED(r) || made == nullptr)
    {
        BVR_WARN("Foveated DLSS (TEST 430): could not create the %s feature %ux%u -> %ux%u "
                 "(0x%08X); the game's own DLSS stays in charge.", what, rw, rh, ow, oh, r);
        return nullptr;
    }
    return made;
}

bool fov_texture(ID3D11Device* dev, unsigned w, unsigned h, DXGI_FORMAT f,
                 ID3D11Texture2D** tex, ID3D11ShaderResourceView** srv)
{
    D3D11_TEXTURE2D_DESC td = {};
    td.Width = w;
    td.Height = h;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = f;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;   /* DLSS writes it */
    return SUCCEEDED(dev->CreateTexture2D(&td, nullptr, tex)) && *tex != nullptr &&
           SUCCEEDED(dev->CreateShaderResourceView(*tex, nullptr, srv)) && *srv != nullptr;
}

bool fov_shader(ID3D11Device* dev)
{
    if (g_fovCs != nullptr)
        return true;
    if (g_fovShaderFailed)
        return false;

    ID3DBlob* code = nullptr;
    ID3DBlob* err = nullptr;
    const HRESULT hr = D3DCompile(kFovShader, strlen(kFovShader), "bvr_fov_blend", nullptr,
                                  nullptr, "main", "cs_5_0", 0, 0, &code, &err);
    if (FAILED(hr) || code == nullptr)
    {
        BVR_ERR("Foveated DLSS (TEST 430): blend shader compile failed (0x%08X) %s", hr,
                err ? static_cast<const char*>(err->GetBufferPointer()) : "");
        if (err) err->Release();
        if (code) code->Release();
        g_fovShaderFailed = true;
        return false;
    }
    if (err) err->Release();

    D3D11_BUFFER_DESC bd = {};
    bd.ByteWidth = sizeof(FovParams);
    bd.Usage = D3D11_USAGE_DYNAMIC;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;

    D3D11_SAMPLER_DESC sd = {};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.MaxLOD = D3D11_FLOAT32_MAX;

    const bool ok =
        SUCCEEDED(dev->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(),
                                           nullptr, &g_fovCs)) &&
        SUCCEEDED(dev->CreateBuffer(&bd, nullptr, &g_fovCb)) &&
        SUCCEEDED(dev->CreateSamplerState(&sd, &g_fovSampler));
    code->Release();
    if (!ok)
    {
        BVR_ERR("Foveated DLSS (TEST 430): could not create the blend pass.");
        g_fovShaderFailed = true;
        return false;
    }
    return true;
}

/* The output's UAV, made once and kept on the output itself. Not AddRef'd. */
ID3D11UnorderedAccessView* fov_out_uav(ID3D11Device* dev, ID3D11Resource* out)
{
    UINT size = sizeof(void*);
    IUnknown* held = nullptr;
    if (SUCCEEDED(out->GetPrivateData(kFovOutUav, &size, &held)) && held != nullptr)
    {
        held->Release();   /* the output keeps it alive */
        return static_cast<ID3D11UnorderedAccessView*>(held);
    }
    ID3D11UnorderedAccessView* uav = nullptr;
    if (FAILED(dev->CreateUnorderedAccessView(out, nullptr, &uav)) || uav == nullptr)
        return nullptr;
    out->SetPrivateDataInterface(kFovOutUav, uav);
    uav->Release();
    return uav;
}

/* The window blended into the stretched periphery, into `out`. The context is
   borrowed, so everything bound is put back. */
bool fov_blend(ID3D11DeviceContext* ctx, ID3D11Device* dev, const FovEye& e,
               ID3D11Resource* out, float winX, float winY)
{
    ID3D11UnorderedAccessView* uav = fov_out_uav(dev, out);
    if (uav == nullptr)
        return false;

    D3D11_MAPPED_SUBRESOURCE m = {};
    if (FAILED(ctx->Map(g_fovCb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
        return false;
    static const float feather = bvr::config_float("dlss_fovea_feather", 0.30f);
    const FovParams prm = { static_cast<float>(e.ow), static_cast<float>(e.oh), winX, winY,
                            static_cast<float>(e.fw), static_cast<float>(e.fh), feather, 0.0f };
    memcpy(m.pData, &prm, sizeof(prm));
    ctx->Unmap(g_fovCb, 0);

    ID3D11ComputeShader*       prevCs = nullptr;
    ID3D11Buffer*              prevCb = nullptr;
    ID3D11ShaderResourceView*  prevSrv[2] = {};
    ID3D11UnorderedAccessView* prevUav = nullptr;
    ID3D11SamplerState*        prevSampler = nullptr;
    ctx->CSGetShader(&prevCs, nullptr, nullptr);
    ctx->CSGetConstantBuffers(0, 1, &prevCb);
    ctx->CSGetShaderResources(0, 2, prevSrv);
    ctx->CSGetUnorderedAccessViews(0, 1, &prevUav);
    ctx->CSGetSamplers(0, 1, &prevSampler);

    ID3D11ShaderResourceView* srvs[2] = { e.pSrv, e.fSrv };
    UINT keep = static_cast<UINT>(-1);
    ctx->CSSetShader(g_fovCs, nullptr, 0);
    ctx->CSSetConstantBuffers(0, 1, &g_fovCb);
    ctx->CSSetShaderResources(0, 2, srvs);
    ctx->CSSetSamplers(0, 1, &g_fovSampler);
    ctx->CSSetUnorderedAccessViews(0, 1, &uav, &keep);
    ctx->Dispatch((e.ow + 7) / 8, (e.oh + 7) / 8, 1);

    ID3D11UnorderedAccessView* noUav = nullptr;
    ID3D11ShaderResourceView*  noSrv[2] = {};
    ctx->CSSetUnorderedAccessViews(0, 1, &noUav, &keep);
    ctx->CSSetShaderResources(0, 2, noSrv);
    ctx->CSSetShader(prevCs, nullptr, 0);
    ctx->CSSetConstantBuffers(0, 1, &prevCb);
    ctx->CSSetShaderResources(0, 2, prevSrv);
    ctx->CSSetSamplers(0, 1, &prevSampler);
    ctx->CSSetUnorderedAccessViews(0, 1, &prevUav, &keep);

    if (prevCs) prevCs->Release();
    if (prevCb) prevCb->Release();
    if (prevSrv[0]) prevSrv[0]->Release();
    if (prevSrv[1]) prevSrv[1]->Release();
    if (prevUav) prevUav->Release();
    if (prevSampler) prevSampler->Release();
    return true;
}

bool fovea_enabled()
{
    return fov_enabled_cfg() && bvr::stereo_active();
}

bool fovea_eval(ID3D11DeviceContext* ctx, const NVSDK_NGX_Handle* game,
                NVSDK_NGX_Parameter* p, void* callback, int eye, ID3D11Resource* out)
{
    if (ctx == nullptr || p == nullptr || out == nullptr || eye < 0 || eye > 1)
        return false;
    if (g_realCreate == nullptr || g_realRelease == nullptr)
        return fov_miss("CreateFeature is not hooked");
    if (g_fovFailures >= 3)
        return fov_miss("it failed 3 times this session, so it stays off");
    if (g_fovFailedFor == game)
        return fov_miss("creating its features failed for this DLSS feature");
    NVSDK_NGX_Parameter* ours = fov_params();
    if (ours == nullptr)
        return fov_miss("no parameter block of our own");
    if (g_fovPairedWith != nullptr && g_fovPairedWith != game)
        fov_release("the game is evaluating a different DLSS feature");

    /* The sizes from the textures (TEST 187: the parameter block cannot be
       trusted for them), the render size from the game's subrect when it has one. */
    ID3D11Resource* color = nullptr;
    p->Get("Color", &color);
    unsigned rw = 0, rh = 0, ow = 0, oh = 0;
    if (!tex2d_size(color, &rw, &rh) || !tex2d_size(out, &ow, &oh))
        return fov_miss("no colour or output texture");
    unsigned subW = 0, subH = 0;
    p->Get(kFovDimW, &subW);
    p->Get(kFovDimH, &subH);
    if (subW != 0 && subH != 0)
    {
        rw = subW;
        rh = subH;
    }
    for (int i = 0; i < kFovBaseCount; ++i)
    {
        unsigned bx = 0, by = 0;
        p->Get(kFovBases[i][0], &bx);
        p->Get(kFovBases[i][1], &by);
        if (bx != 0 || by != 0)
            return fov_miss("the game already reads its inputs from an offset");
    }
    D3D11_TEXTURE2D_DESC od = {};
    {
        ID3D11Texture2D* ot = nullptr;
        if (FAILED(out->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&ot)) || !ot)
            return fov_miss("the output is not a 2D texture");
        ot->GetDesc(&od);
        ot->Release();
    }
    if ((od.BindFlags & D3D11_BIND_UNORDERED_ACCESS) == 0)
        return fov_miss("the output has no UAV");

    /* THE WINDOW, in render pixels, centred on the lens axis (where the measured
       frustum puts tangent 0). Even sizes and origins, so a 1.5x or 2x scale
       lands it on whole output pixels. */
    static const float size = std::fmin(0.9f, std::fmax(0.1f, bvr::config_float("dlss_fovea_size", 0.40f)));
    float cx = 0.5f, cy = 0.5f;
    {
        float l = 0.0f, r = 0.0f, u = 0.0f, d = 0.0f;
        if (bvr::vp_measured_tangents(&l, &r, &u, &d) && r > l && u > d)
        {
            cx = -l / (r - l);
            cy = u / (u - d);
        }
    }
    const unsigned ww = static_cast<unsigned>(rw * size) & ~1u;
    const unsigned wh = static_cast<unsigned>(rh * size) & ~1u;
    if (ww < 64 || wh < 64)
        return fov_miss("the window would be too small");
    const int bx = std::max(0, std::min(static_cast<int>(rw - ww),
                                        static_cast<int>(std::lround(cx * rw - ww * 0.5f)) & ~1));
    const int by = std::max(0, std::min(static_cast<int>(rh - wh),
                                        static_cast<int>(std::lround(cy * rh - wh * 0.5f)) & ~1));
    const float sx = static_cast<float>(ow) / static_cast<float>(rw);
    const float sy = static_cast<float>(oh) / static_cast<float>(rh);
    const unsigned fw = static_cast<unsigned>(std::lround(ww * sx));
    const unsigned fh = static_cast<unsigned>(std::lround(wh * sy));

    ID3D11Device* dev = nullptr;
    ctx->GetDevice(&dev);
    if (dev == nullptr)
        return fov_miss("no device");

    FovEye& e = g_fov[eye];
    if (e.periph != nullptr &&
        (e.rw != rw || e.rh != rh || e.ow != ow || e.oh != oh || e.ww != ww || e.wh != wh ||
         e.fmt != od.Format))
        fov_release_eye(e);

    if (e.periph == nullptr)
    {
        bool ok = fov_shader(dev);
        if (ok)
        {
            e.periph = fov_create(ctx, p, ours, game, rw, rh, rw, rh, 5 /* DLAA */,
                                  "periphery (DLAA)");
            e.fovea = e.periph ? fov_create(ctx, p, ours, game, ww, wh, fw, fh, -1, "window")
                               : nullptr;
            ok = e.fovea != nullptr &&
                 fov_texture(dev, rw, rh, od.Format, &e.pTex, &e.pSrv) &&
                 fov_texture(dev, fw, fh, od.Format, &e.fTex, &e.fSrv);
        }
        if (!ok)
        {
            dev->Release();
            fov_release_eye(e);
            g_fovFailedFor = game;
            ++g_fovFailures;
            return fov_miss("creating its features failed");
        }
        e.rw = rw; e.rh = rh; e.ow = ow; e.oh = oh;
        e.ww = ww; e.wh = wh; e.fw = fw; e.fh = fh;
        e.fmt = od.Format;
        e.fresh = true;
        BVR_INFO("Foveated DLSS (TEST 430): eye %d - periphery DLAA %ux%u stretched to %ux%u; "
                 "window %ux%u at (%d, %d) -> %ux%u, %.0f%% of the output at full density "
                 "(DLSS used to write all %ux%u).", eye, rw, rh, ow, oh, ww, wh, bx, by, fw, fh,
                 100.0 * fw * fh / (static_cast<double>(ow) * oh), ow, oh);
    }
    g_fovPairedWith = game;

    /* Everything below goes through OUR block; the game's is only read. Each
       evaluation states its whole subrect, so neither inherits the other's. */
    fov_copy_inputs(p, ours);
    int gameReset = 0;
    p->Get("Reset", &gameReset);
    ours->Set("Reset", (gameReset != 0 || e.fresh) ? 1 : 0);

    NVSDK_NGX_Result r1 = 0, r2 = 0;
    bool blended = false;
    {
        bvr::StereoIgnore ignore;   /* ours, not the engine's frame */

        for (int i = 0; i < kFovBaseCount; ++i)
        {
            ours->Set(kFovBases[i][0], 0u);
            ours->Set(kFovBases[i][1], 0u);
        }
        ours->Set(kFovDimW, rw);
        ours->Set(kFovDimH, rh);
        ours->Set("Output", static_cast<ID3D11Resource*>(e.pTex));
        r1 = g_realEvaluate(ctx, e.periph, ours, callback);

        if (NGX_SUCCEED(r1))
        {
            for (int i = 0; i < kFovBaseCount; ++i)
            {
                ours->Set(kFovBases[i][0], static_cast<unsigned>(bx));
                ours->Set(kFovBases[i][1], static_cast<unsigned>(by));
            }
            ours->Set(kFovDimW, ww);
            ours->Set(kFovDimH, wh);
            ours->Set("Output", static_cast<ID3D11Resource*>(e.fTex));
            r2 = g_realEvaluate(ctx, e.fovea, ours, callback);
        }

        blended = NGX_SUCCEED(r1) && NGX_SUCCEED(r2) &&
                  fov_blend(ctx, dev, e, out, bx * sx, by * sy);
    }
    dev->Release();

    if (!blended)
    {
        ++g_fovFailures;
        BVR_WARN("Foveated DLSS (TEST 430b): eye %d failed (periphery 0x%08X, window 0x%08X, "
                 "blend %s), failure %d of 3; the game's own DLSS takes over.", eye, r1, r2,
                 NGX_SUCCEED(r1) && NGX_SUCCEED(r2) ? "FAILED" : "not run", g_fovFailures);
        fov_release("an evaluation failed");
        g_fovFailedFor = game;
        return fov_miss("an evaluation failed");
    }

    e.fresh = false;
    ++g_fovEvals;
    return true;
}

/* A feature like the game's `feature`, made for the sizes this evaluation really
   has: the game's creation values where recorded, the four sizes from the
   textures. The game's block is put back as it was. rw/rh = the colour size used
   (0 if unreadable); w/h/ow/oh = the block's sizes at creation, for the log. */
NVSDK_NGX_Result create_from_textures(ID3D11DeviceContext* ctx, const NVSDK_NGX_Handle* feature,
                                      NVSDK_NGX_Parameter* p, ID3D11Resource* output,
                                      NVSDK_NGX_Handle** made, unsigned* rw, unsigned* rh,
                                      unsigned* w, unsigned* h, unsigned* ow, unsigned* oh)
{
    std::lock_guard<std::recursive_mutex> turn(g_ngxBlockLock);   /* TEST 777 */
    constexpr int kFields = static_cast<int>(sizeof(g_createSnap.f) / sizeof(g_createSnap.f[0]));
    CreateField saved[kFields];
    memcpy(saved, g_createSnap.f, sizeof(saved));
    snapshot_read(p, saved, kFields);

    /* TEST 594b: the game's own creation for THESE sizes if it ever made one - the
       feature it is evaluating may have been made for another view (the smithy's),
       with other flags, and those flags made the right-sized copy trail. */
    ID3D11Resource* sizeColor = nullptr;
    p->Get("Color", &sizeColor);
    unsigned s[4] = {};
    const CreateField* snap = nullptr;
    if (tex2d_size(sizeColor, &s[0], &s[1]) && tex2d_size(output, &s[2], &s[3]))
        snap = snapshot_for_sizes(s);
    if (snap == nullptr && g_createSnap.handle == feature)
        snap = g_createSnap.f;
    if (snap != nullptr)
        snapshot_write(p, snap, kFields);

    /* THE SIZES FROM THE TEXTURES THEMSELVES (TEST 187). Neither block can be
       trusted for them: TEST 184's create-time record had Width = render, and
       TEST 186's came back output-first and failed 0xBAD00005 again - so the
       game fills that block in more than one order. What DLSS is actually
       handed cannot lie: the colour input is the render size, the output is
       the output size. */
    unsigned tw = 0, th = 0;
    {
        ID3D11Resource* colorRes = nullptr;
        p->Get("Color", &colorRes);
        if (tex2d_size(colorRes, rw, rh) && tex2d_size(output, &tw, &th))
        {
            p->Set("Width", *rw);
            p->Set("Height", *rh);
            p->Set("OutWidth", tw);
            p->Set("OutHeight", th);
        }
        else
        {
            *rw = *rh = 0;
        }
    }

    const NVSDK_NGX_Result cr = g_realCreate(ctx, g_stereoFeatureId, p, made);

    p->Get("Width", w);
    p->Get("Height", h);
    p->Get("OutWidth", ow);
    p->Get("OutHeight", oh);
    snapshot_write(p, saved, kFields);       /* the game's block as it was */

    /* TEST 774: except the sizes, which stay the textures' - see evaluate_sized. */
    if (*rw != 0 && tw != 0)
    {
        p->Set("Width", *rw);
        p->Set("Height", *rh);
        p->Set("OutWidth", tw);
        p->Set("OutHeight", th);
    }
    return cr;
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

    /* THE COLOUR SIZE MOVED UNDER THE FEATURE (TEST 195). Switching to a SMALLER
       DLSS mode, the game makes its new feature one frame before it resizes its
       targets, and its own evaluation of that frame succeeds on the old, larger
       colour. Eye 1's feature was built on that frame from the old size; the next
       frame's smaller colour failed it with 0xBAD00005 and the TEST 189 retry kept
       eye 1 flat for half a second (33 and 47 frames in the 17:53 run, switching
       to Balanced and to Performance). A size change is not a failure burst, so
       it is rebuilt now, from the colour it is about to be given. */
    unsigned cw = 0, ch = 0;
    if (g_stereoTwin != nullptr && g_stereoTwinW != 0 && tex2d_size(color, &cw, &ch) &&
        (cw != g_stereoTwinW || ch != g_stereoTwinH))
        stereo_twin_release("the game's colour input changed size");
    if (g_stereoTwin != nullptr && g_stereoTwinPreset != live_preset())   /* TEST 812 */
        stereo_twin_release("the DLSS preset changed");

    if (g_stereoTwin == nullptr)
    {
        if (g_stereoFailedFor == feature && GetTickCount64() < g_stereoRetryAt)
        {
            stereo_missed("no eye-1 feature (its creation or evaluation failed)");
            return false;
        }
        if (g_realCreate == nullptr)
        {
            stereo_missed("CreateFeature is not hooked");
            return false;
        }

        const bool useSnap = g_createSnap.handle == feature;
        unsigned rw = 0, rh = 0, w = 0, h = 0, ow = 0, oh = 0;
        NVSDK_NGX_Handle* made = nullptr;
        const unsigned preset = live_preset();   /* TEST 812 */
        set_preset_hints(p, preset);
        const NVSDK_NGX_Result cr = create_from_textures(ctx, feature, p, output, &made,
                                                         &rw, &rh, &w, &h, &ow, &oh);

        if (!NGX_SUCCEED(cr) || made == nullptr)
        {
            g_stereoFailedFor = feature;
            g_stereoRetryAt = ~0ull;
            BVR_WARN("Stereo DLSS: could not create a DLSS feature for the second eye "
                     "(0x%08X) from %ux%u -> %ux%u (%s). Eye 1 will show eye 0's DLSS "
                     "image - flat, no depth.", cr, w, h, ow, oh,
                     useSnap ? "the game's own creation values"
                             : "NO record of the game's creation - the evaluation block as is");
            stereo_missed("eye-1 feature creation failed");
            return false;
        }

        g_stereoTwin = made;
        g_stereoTwinW = rw;
        g_stereoTwinPreset = preset;
        g_stereoTwinH = rh;
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
        else
            ++g_stereoShared[i];
    }

    bool ok = false;
    ID3D11Resource* outTwin = bvr::stereo_twin_for_write(ctx, output);
    if (outTwin != nullptr)
    {
        p->Set("Output", outTwin);
        int reset = 0;
        p->Get("Reset", &reset);

        /* TEST 430: eye 1 foveated, like eye 0. Its full-size feature idles and
           is reset when it is next used (g_stereoReset below). */
        const bool fov = fovea_enabled() && fovea_eval(ctx, feature, p, callback, 1, outTwin);

        const bool forceReset = !fov && g_stereoReset;
        if (forceReset)
            p->Set("Reset", 1);

        if (!fov && bvr::frame_map_capturing())
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
        NVSDK_NGX_Result r2 = 1;   /* NVSDK_NGX_Result_Success: the foveated pass did it */
        if (!fov)
        {
            /* Anything NGX does on the context in here is ours, not the engine's
               frame - not tracked, not duplicated. */
            bvr::StereoIgnore ignore;
            r2 = evaluate_sized(ctx, g_stereoTwin, p, callback, 1);
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
            g_stereoReset = fov;    /* idle while foveated: reset when used again */
            ++g_stereoEvals;
        }
        else
        {
            /* TEST 189: the game rebuilds its targets a frame or more after a new
               feature, so a feature made in between can be wrong for every frame
               that follows - 20 s of a flat right eye in the 16:48 run. Drop it and
               let the next evaluation build it from the textures as they are then. */
            ID3D11Resource* col = nullptr;
            p->Get("Color", &col);
            D3D11_TEXTURE2D_DESC cd = {};
            ID3D11Texture2D* ct = nullptr;
            if (col && SUCCEEDED(col->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&ct)) && ct)
            {
                ct->GetDesc(&cd);
                ct->Release();
            }
            BVR_WARN("Stereo DLSS: the second eye's evaluation returned 0x%08X (colour now "
                     "%ux%u); its feature is dropped and rebuilt on the next frame. Eye 1 "
                     "shows eye 0's DLSS image until then.", r2, cd.Width, cd.Height);
            stereo_twin_release("its evaluation failed");
            g_stereoFailedFor = feature;
            g_stereoRetryAt = GetTickCount64() + 500;
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
        fov_release("native stereo is off");
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
        BVR_INFO("Stereo DLSS inputs eye 1 took from eye 0 (no twin) in ~5 s: colour %llu, "
                 "depth %llu, motion vectors %llu, exposure %llu, transparency %llu, "
                 "bias mask %llu. Anything but ~0 on colour/depth/motion is a flat or "
                 "flickering right eye.",
                 static_cast<unsigned long long>(g_stereoShared[0]),
                 static_cast<unsigned long long>(g_stereoShared[1]),
                 static_cast<unsigned long long>(g_stereoShared[2]),
                 static_cast<unsigned long long>(g_stereoShared[3]),
                 static_cast<unsigned long long>(g_stereoShared[4]),
                 static_cast<unsigned long long>(g_stereoShared[5]));
        if (fov_enabled_cfg())
            BVR_INFO("Foveated DLSS (TEST 430): %llu eye(s) resolved as DLAA periphery + DLSS "
                     "window in ~5 s, %llu fell back to the full-size DLSS (last reason: %s).",
                     static_cast<unsigned long long>(g_fovEvals),
                     static_cast<unsigned long long>(g_fovMissed),
                     *g_fovWhy ? g_fovWhy : "none");
        g_fovEvals = 0;
        g_fovMissed = 0;
        g_fovWhy = "";
        memset(g_stereoShared, 0, sizeof(g_stereoShared));
        g_stereoEvals = 0;
        g_stereoMissed = 0;
        g_stereoWhy = "";
        g_stereoCpuMs = 0.0;
    }
}

/* ===========================================================================
   THE GAME'S FEATURE, THE WRONG SIZE (TEST 594).

   rgl makes its one DLSS feature for the view that is being created, and never
   again for a view that already exists. The smithy's crafting scene is window-
   sized, so after leaving it the map's 1778x1686 -> 3556x3372 eye was upscaled
   by a feature made for 1280x720 -> 2560x1440: DLSS filled the eye's top-left
   2560x1440 and the rest stayed black, with PAUSED drawn over it. That was the
   left eye in native stereo (eye 1's feature is built from the textures) and
   both eyes under AFR. Vanilla never meets it: every view there is window-sized.

   So when an evaluation's colour and output are not the sizes the game created
   its feature for (in either order, see TEST 187), it runs on a feature of ours
   made from the textures - one per AFR eye, as pick_feature splits them. The
   game's feature idles until the sizes fit again. ngx_fit_feature = 0 is off.
   =========================================================================== */
const NVSDK_NGX_Handle* g_fitFor = nullptr;        /* the game feature the ones below stand in for */
NVSDK_NGX_Handle*       g_fit[2] = {};
unsigned                g_fitSize[2][4] = {};      /* colour w/h, output w/h each was made for */
unsigned                g_fitPreset[2] = {};       /* TEST 812: the preset each was made with */
bool                    g_fitFailed = false;
uint64_t                g_fitUses = 0;
uint64_t                g_fitLastLog = 0;

bool fit_enabled()
{
    static const bool v = config_bool("ngx_fit_feature", true);
    return v;
}

void fit_release(const char* why)
{
    bool any = false;
    for (NVSDK_NGX_Handle*& f : g_fit)
    {
        if (f != nullptr && g_realRelease != nullptr)
        {
            g_realRelease(f);
            any = true;
        }
        f = nullptr;
    }
    if (any)
        BVR_INFO("DLSS fit (TEST 594): our right-sized feature(s) released (%s).", why);
    g_fitFor = nullptr;
    g_fitFailed = false;
}

const NVSDK_NGX_Handle* fit_feature(ID3D11DeviceContext* ctx, const NVSDK_NGX_Handle* feature,
                                    const NVSDK_NGX_Parameter* params)
{
    if (!fit_enabled() || feature == nullptr || params == nullptr || g_realCreate == nullptr ||
        g_createSnap.handle != feature)
        return nullptr;

    const CreateField* f = g_createSnap.f;   /* Width, Height, OutWidth, OutHeight first */
    if (!f[0].have || !f[1].have || !f[2].have || !f[3].have)
        return nullptr;

    NVSDK_NGX_Parameter* p = const_cast<NVSDK_NGX_Parameter*>(params);
    ID3D11Resource* color = nullptr;
    ID3D11Resource* output = nullptr;
    p->Get("Color", &color);
    p->Get("Output", &output);
    unsigned s[4] = {};
    if (!tex2d_size(color, &s[0], &s[1]) || !tex2d_size(output, &s[2], &s[3]))
        return nullptr;

    if (g_fitFor != feature)
        fit_release("the game made a new DLSS feature");

    if (g_fitFailed)
        return nullptr;
    const unsigned want = live_preset();   /* TEST 812 */
    if (snapshot_sizes_match(f, s) && want == g_gamePreset)
    {
        if (g_fit[0] != nullptr || g_fit[1] != nullptr)
            fit_release("the game's own feature fits again");
        return nullptr;
    }

    const int eye = (per_eye_enabled() && g_featureTwin != nullptr && feature == g_featureGame &&
                     bvr::xr_current_afr_eye() == 1) ? 1 : 0;
    if (g_fit[eye] != nullptr && (memcmp(g_fitSize[eye], s, sizeof(s)) != 0 || g_fitPreset[eye] != want) &&
        g_realRelease != nullptr)
    {
        g_realRelease(g_fit[eye]);
        g_fit[eye] = nullptr;
    }

    if (g_fit[eye] == nullptr)
    {
        unsigned rw = 0, rh = 0, w = 0, h = 0, ow = 0, oh = 0;
        set_preset_hints(p, want);
        const NVSDK_NGX_Result cr = create_from_textures(ctx, feature, p, output, &g_fit[eye],
                                                         &rw, &rh, &w, &h, &ow, &oh);
        g_fitFor = feature;
        if (!NGX_SUCCEED(cr) || g_fit[eye] == nullptr)
        {
            g_fit[eye] = nullptr;
            g_fitFailed = true;
            BVR_WARN("DLSS fit (TEST 594): the game's DLSS feature was made for %ux%u -> %ux%u but "
                     "is given %ux%u -> %ux%u, and a feature for those sizes was refused (0x%08X). "
                     "Part of the eye stays black until the game makes a new one.",
                     f[0].u, f[1].u, f[2].u, f[3].u, s[0], s[1], s[2], s[3], cr);
            return nullptr;
        }
        memcpy(g_fitSize[eye], s, sizeof(s));
        g_fitPreset[eye] = want;
        if (snapshot_sizes_match(f, s))
            BVR_INFO("DLSS preset (TEST 812): eye %d now runs on our own feature %p with preset %u "
                     "(the game's was made with %u) until the game makes a new one.",
                     eye, (void*)g_fit[eye], want, g_gamePreset);
        else
            BVR_INFO("DLSS fit (TEST 594): the game's DLSS feature was made for %ux%u -> %ux%u but is "
                     "given %ux%u -> %ux%u (it never rebuilds it for a view that already exists - the "
                     "map after the smithy). Eye %d now runs on our own feature %p made for the real "
                     "sizes; without it DLSS fills only part of the eye and the rest is black.",
                     f[0].u, f[1].u, f[2].u, f[3].u, s[0], s[1], s[2], s[3], eye, (void*)g_fit[eye]);
    }

    ++g_fitUses;
    const uint64_t now = GetTickCount64();
    if (now - g_fitLastLog >= 5000)
    {
        if (g_fitLastLog != 0)
            BVR_INFO("DLSS fit (TEST 594): %llu evaluation(s) in ~5 s ran on our right-sized feature.",
                     static_cast<unsigned long long>(g_fitUses));
        g_fitLastLog = now;
        g_fitUses = 0;
    }
    return g_fit[eye];
}

/* TEST 795: the SR preset DLSS Swapper / the NVIDIA App / Profile Inspector put
   in this exe's driver profile ("Override DLSS-SR presets"), 1-26 = A-Z, or 0
   for none / Default / Latest (those stay the driver's to resolve). Read once. */
unsigned driver_profile_preset()
{
    static int s_cached = -1;
    if (s_cached >= 0)
        return static_cast<unsigned>(s_cached);
    s_cached = 0;
    if (NvAPI_Initialize() != NVAPI_OK)
        return 0;
    NvDRSSessionHandle s = nullptr;
    if (NvAPI_DRS_CreateSession(&s) != NVAPI_OK)
        return 0;
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    const wchar_t* exe = wcsrchr(path, L'\\');
    exe = exe ? exe + 1 : path;
    NvDRSProfileHandle profile = nullptr;
    NVDRS_APPLICATION app = {};
    app.version = NVDRS_APPLICATION_VER;
    NVDRS_SETTING st = {};
    st.version = NVDRS_SETTING_VER;
    if (NvAPI_DRS_LoadSettings(s) == NVAPI_OK &&
        NvAPI_DRS_FindApplicationByName(s, reinterpret_cast<NvU16*>(const_cast<wchar_t*>(exe)),
                                        &profile, &app) == NVAPI_OK &&
        NvAPI_DRS_GetSetting(s, profile, 0x10E41DF3 /* NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION */,
                             &st) == NVAPI_OK &&
        st.u32CurrentValue >= 1 && st.u32CurrentValue <= 26)
        s_cached = static_cast<int>(st.u32CurrentValue);
    NvAPI_DRS_DestroySession(s);
    BVR_INFO("NGX preset (TEST 795): the driver profile for %ls asks for preset %c.", exe,
             s_cached ? static_cast<char>('A' + s_cached - 1) : '-');
    return static_cast<unsigned>(s_cached);
}

const char* const kPresetHints[6] = {
    "DLSS.Hint.Render.Preset.DLAA", "DLSS.Hint.Render.Preset.Quality",
    "DLSS.Hint.Render.Preset.Balanced", "DLSS.Hint.Render.Preset.Performance",
    "DLSS.Hint.Render.Preset.UltraPerformance", "DLSS.Hint.Render.Preset.UltraQuality",
};

/* The preset the game's feature was made with. */
unsigned g_gamePreset = 0;
bool     g_presetRefused = false;   /* this DLSS took no preset (TEST 796) */

void set_preset_hints(NVSDK_NGX_Parameter* p, unsigned preset)
{
    /* Always: the hints stay on the game's block, so 0 must clear ours. */
    for (const char* name : kPresetHints)
        p->Set(name, preset);
}

unsigned preset_now()
{
    /* TEST 794/795: THE DLSS PRESET, SET HERE ON EVERY MODE. The driver applies
       its profile's preset override only in Performance here (live state: SR
       feedback 0x205, no ENABLED or PRESET bit), so Quality/Balanced ran DLSS's
       own pick and read soft. So the preset goes in as the game's hints, which
       eye 1's feature gets too (they stay on the game's block). dlss_preset
       11 = K, 12 = L, 13 = M wins; 0 = the driver profile's (DLSS Swapper, NVIDIA
       App), else K (TEST 796: shipped, so nobody has to override it); -1 = DLSS's
       own choice. */
    static const int cfgPreset = static_cast<int>(lroundf(bvr::config_float("dlss_preset", 0.0f)));
    const int choice = g_presetChoice.load(std::memory_order_relaxed) == kPresetUnset
                     ? cfgPreset : g_presetChoice.load(std::memory_order_relaxed);   /* TEST 797: the panel's */
    return choice > 0 ? static_cast<unsigned>(choice)
         : choice < 0 ? 0u
         : driver_profile_preset() != 0 ? driver_profile_preset() : 11u;
}

/* TEST 812: THE PANEL'S PRESET, LIVE. Our own features (eye 1's twin, and eye 0's
   fit while it differs from the game's feature) are built with this and rebuilt
   when it changes, so a dropdown change shows at once; the game's next create
   takes it too. TEST 799's blur was the engine's post-target rounding (TEST 818).
   A DLSS that refused a preset keeps the game's. */
unsigned live_preset()
{
    return g_presetRefused ? g_gamePreset : preset_now();
}

NVSDK_NGX_Result __cdecl CreateFeature_hook(ID3D11DeviceContext* ctx, int featureId,
                                            NVSDK_NGX_Parameter* params,
                                            NVSDK_NGX_Handle** outHandle)
{
    /* TEST 815: THE CREATE GETS ITS SIZES THE RIGHT WAY ROUND. The game's settings
       query (TEST 776/777) writes Width/Height = display and OutWidth/OutHeight =
       render into this same block, from the game thread, at any time - so a create
       could go in as render 3540 -> output 2360: a DLSS that thinks it shrinks the
       image. Which create got hit was luck ("random; Quality is sharp if the game
       STARTS in Quality"), and after every mode switch or crop rebuild it usually
       was. Upscaling never has render > output, so that is put back, under the
       query's lock so it cannot be re-torn before DLSS reads it. */
    install_query_lock();
    install_post_size_fix();   /* TEST 818 */
    std::lock_guard<std::recursive_mutex> turn(g_ngxBlockLock);
    if (params != nullptr)
    {
        unsigned w = 0, h = 0, ow = 0, oh = 0;
        params->Get("Width", &w);
        params->Get("Height", &h);
        params->Get("OutWidth", &ow);
        params->Get("OutHeight", &oh);
        const bool swapped = ow != 0 && oh != 0 && w > ow && h > oh;
        if (swapped)
        {
            params->Set("Width", ow);
            params->Set("Height", oh);
            params->Set("OutWidth", w);
            params->Set("OutHeight", h);
        }
        BVR_INFO("NGX create (TEST 815): the block came in as Width %u Height %u -> OutWidth %u "
                 "OutHeight %u%s.", w, h, ow, oh,
                 swapped ? " - SWAPPED by the game's settings query, put back to render -> output" : "");
    }

    unsigned preset = preset_now();
    if (params != nullptr)
    {
        unsigned was[6] = {};
        for (int k = 0; k < 6; ++k)
            params->Get(kPresetHints[k], &was[k]);
        set_preset_hints(params, preset);
        BVR_INFO("NGX preset (TEST 794): the block's hints were DLAA %u Quality %u Balanced %u "
                 "Performance %u UltraPerf %u UltraQuality %u (0 = DLSS picks); now preset %u "
                 "on every mode.", was[0], was[1], was[2], was[3], was[4], was[5], preset);
    }

    NVSDK_NGX_Result r = g_realCreate(ctx, featureId, params, outHandle);
    if (!NGX_SUCCEED(r) && preset != 0 && params != nullptr)
    {
        /* An older DLSS without this preset: DLSS's own choice instead. */
        const NVSDK_NGX_Result refused = r;
        set_preset_hints(params, 0u);
        r = g_realCreate(ctx, featureId, params, outHandle);
        BVR_WARN("NGX preset (TEST 796): DLSS refused preset %u (0x%08X); created with its own "
                 "choice instead -> 0x%08X.", preset, static_cast<unsigned>(refused),
                 static_cast<unsigned>(r));
        preset = 0;
    }
    if (NGX_SUCCEED(r))
    {
        g_gamePreset = preset;
        g_presetRefused = preset == 0 && preset_now() != 0;
    }

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

            g_snapHist[g_snapNext] = g_createSnap;
            g_snapNext = (g_snapNext + 1) % static_cast<int>(sizeof(g_snapHist) / sizeof(g_snapHist[0]));
            const CreateField* f = g_createSnap.f;
            BVR_INFO("NGX create (TEST 594b): the game made DLSS feature %p - Width %u Height %u "
                     "OutWidth %u OutHeight %u, PerfQualityValue %d, Create.Flags 0x%X, "
                     "Output.Subrects %d, RTXValue %d (a field not set reads 0).",
                     (void*)*outHandle, f[0].have ? f[0].u : 0, f[1].have ? f[1].u : 0,
                     f[2].have ? f[2].u : 0, f[3].have ? f[3].u : 0,
                     f[4].have ? f[4].i : 0, f[5].have ? f[5].i : 0,
                     f[6].have ? f[6].i : 0, f[7].have ? f[7].i : 0);
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
    /* Native stereo's eye-1 feature goes with the game feature it was paired with,
       and so do the foveated ones (TEST 430). */
    if (handle != nullptr && handle == g_stereoPairedWith)
        stereo_twin_release("the game released its DLSS feature");
    if (handle != nullptr && handle == g_fovPairedWith)
        fov_release("the game released its DLSS feature");
    if (handle != nullptr && handle == g_fovFailedFor)
        g_fovFailedFor = nullptr;
    if (handle != nullptr && handle == g_stereoFailedFor)
        g_stereoFailedFor = nullptr;
    if (handle != nullptr && handle == g_fitFor)
        fit_release("the game released its DLSS feature");

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

void ngx_set_cheeky_running() { g_cheekyRunning.store(true); }

void ngx_set_preset(int preset) { g_presetChoice.store(preset, std::memory_order_relaxed); }

/* TEST 800: WHICH PRESET DOES DLSS REALLY RUN? With ShowDlssIndicator = 0x400 in
   HKLM\SOFTWARE\NVIDIA Corporation\Global\NGXCore, DLSS writes its version, mode
   and preset into the bottom-left of its output - where no headset lens shows it.
   So ~8 s into each of the first two DLSS features, eye 0's output is saved:
   Logs\BVR_DLSS_<n>_corner.bmp (bottom-left 1200x500, full size) and
   Logs\BVR_DLSS_<n>_full.bmp (a quarter size). ngx_dump_output = 0 is off. */
#include <shlobj.h>
namespace {

std::atomic<int> g_finalPending[2] = { -1, -1 };   /* TEST 805: the dump number the next eye-0 submit saves; TEST 814: [0] before our sharpener, [1] after */

float half_to_float(uint16_t h)
{
    const uint32_t s = (h & 0x8000u) << 16, e = (h >> 10) & 0x1F, m = h & 0x3FF;
    uint32_t bits;
    if (e == 0)
        return (s ? -1.0f : 1.0f) * ldexpf(static_cast<float>(m), -24);
    if (e == 31)
        bits = s | 0x7F800000u | (m << 13);
    else
        bits = s | ((e + 112) << 23) | (m << 13);
    float f;
    memcpy(&f, &bits, 4);
    return f;
}

uint8_t to_srgb8(float v)
{
    if (!(v > 0.0f)) return 0;
    v = v / (1.0f + v);                          /* HDR -> 0..1 */
    return static_cast<uint8_t>(fminf(255.0f, 255.0f * powf(v, 1.0f / 2.2f) + 0.5f));
}

void write_bmp(const wchar_t* name, const std::vector<uint8_t>& px, UINT w, UINT h)
{
    wchar_t docs[MAX_PATH] = {}, path[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_MYDOCUMENTS, nullptr, 0, docs)))
        return;
    _snwprintf_s(path, _TRUNCATE, L"%s\\Mount and Blade II Bannerlord\\Logs\\%s", docs, name);
    FILE* f = nullptr;
    if (_wfopen_s(&f, path, L"wb") != 0 || f == nullptr)
        return;
    uint8_t hdr[54] = { 'B', 'M' };
    auto put32 = [&](int at, uint32_t v) { memcpy(hdr + at, &v, 4); };
    put32(2, static_cast<uint32_t>(54 + px.size()));
    put32(10, 54);
    put32(14, 40);
    put32(18, w);
    put32(22, h);
    hdr[26] = 1;
    hdr[28] = 24;
    put32(34, static_cast<uint32_t>(px.size()));
    fwrite(hdr, 1, sizeof(hdr), f);
    fwrite(px.data(), 1, px.size(), f);
    fclose(f);
}

/* One region of a mapped RGBA16F image, every `step` pixels, as a bottom-up BMP. */
std::vector<uint8_t> region(const D3D11_MAPPED_SUBRESOURCE& m, UINT x0, UINT y0, UINT w, UINT h, UINT step,
                            UINT* ow, UINT* oh)
{
    *ow = w / step;
    *oh = h / step;
    const UINT row = (*ow * 3 + 3) & ~3u;
    std::vector<uint8_t> px(static_cast<size_t>(row) * *oh);
    for (UINT y = 0; y < *oh; ++y)
    {
        const uint16_t* in = reinterpret_cast<const uint16_t*>(
            static_cast<const uint8_t*>(m.pData) + static_cast<size_t>(m.RowPitch) * (y0 + y * step));
        uint8_t* out = px.data() + static_cast<size_t>(row) * (*oh - 1 - y);
        for (UINT x = 0; x < *ow; ++x)
        {
            const uint16_t* p = in + (x0 + x * step) * 4;
            out[x * 3 + 0] = to_srgb8(half_to_float(p[2]));
            out[x * 3 + 1] = to_srgb8(half_to_float(p[1]));
            out[x * 3 + 2] = to_srgb8(half_to_float(p[0]));
        }
    }
    return px;
}

/* TEST 809: one R11G11B10_FLOAT channel (mantissa bits mb) to float. */
float small_float(uint32_t v, int mb)
{
    const uint32_t m = v & ((1u << mb) - 1), e = v >> mb;
    if (e == 0) return ldexpf(static_cast<float>(m), -14 - mb);
    if (e == 31) return 65504.0f;
    return ldexpf(1.0f + static_cast<float>(m) / static_cast<float>(1u << mb), static_cast<int>(e) - 15);
}

/* TEST 809: the centre 1024x1024 of DLSS's INPUT colour, at the render size -
   whether the game's own render is already soft with the fov crop on. */
void dump_color_center(ID3D11DeviceContext* ctx, const NVSDK_NGX_Parameter* params, int n)
{
    ID3D11Resource* res = nullptr;
    const_cast<NVSDK_NGX_Parameter*>(params)->Get("Color", &res);
    ID3D11Texture2D* tex = nullptr;
    if (res == nullptr || FAILED(res->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&tex))))
        return;
    D3D11_TEXTURE2D_DESC d = {};
    tex->GetDesc(&d);
    ID3D11Device* dev = nullptr;
    ctx->GetDevice(&dev);
    D3D11_TEXTURE2D_DESC sd = d;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.BindFlags = 0;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    sd.MiscFlags = 0;
    sd.MipLevels = 1;
    sd.ArraySize = 1;
    ID3D11Texture2D* st = nullptr;
    if (d.Format != DXGI_FORMAT_R11G11B10_FLOAT || d.Width < 1024 || d.Height < 1024 || dev == nullptr ||
        FAILED(dev->CreateTexture2D(&sd, nullptr, &st)))
    {
        BVR_INFO("DLSS input dump (TEST 809): colour format %d %ux%u not handled.", d.Format, d.Width, d.Height);
        if (dev) dev->Release();
        tex->Release();
        return;
    }
    ctx->CopySubresourceRegion(st, 0, 0, 0, 0, tex, 0, nullptr);
    D3D11_MAPPED_SUBRESOURCE m = {};
    if (SUCCEEDED(ctx->Map(st, 0, D3D11_MAP_READ, 0, &m)))
    {
        const UINT w = 1024, h = 1024, x0 = d.Width / 2 - 512, y0 = d.Height / 2 - 512, row = w * 3;
        std::vector<uint8_t> px(static_cast<size_t>(row) * h);
        for (UINT y = 0; y < h; ++y)
        {
            const uint32_t* in = reinterpret_cast<const uint32_t*>(
                static_cast<const uint8_t*>(m.pData) + static_cast<size_t>(m.RowPitch) * (y0 + y)) + x0;
            uint8_t* out = px.data() + static_cast<size_t>(row) * (h - 1 - y);
            for (UINT x = 0; x < w; ++x)
            {
                const uint32_t v = in[x];
                out[x * 3 + 0] = to_srgb8(small_float(v >> 22, 5));
                out[x * 3 + 1] = to_srgb8(small_float((v >> 11) & 0x7FF, 6));
                out[x * 3 + 2] = to_srgb8(small_float(v & 0x7FF, 6));
            }
        }
        ctx->Unmap(st, 0);
        wchar_t name[64];
        swprintf_s(name, L"BVR_DLSSIN_%d_center.bmp", n);
        write_bmp(name, px, w, h);
        BVR_INFO("DLSS input dump (TEST 809): wrote Logs\\BVR_DLSSIN_%d_center.bmp from eye 0's %ux%u colour.",
                 n, d.Width, d.Height);
    }
    st->Release();
    dev->Release();
    tex->Release();
}

void dlss_dump_output(ID3D11DeviceContext* ctx, const NVSDK_NGX_Parameter* params, const void* feature)
{
    static const bool on = bvr::config_bool("ngx_dump_output", false);
    static const void* s_for = nullptr;
    static uint64_t s_since = 0;
    static int s_dumps = 0;
    if (!on || s_dumps >= 8 || params == nullptr || ctx == nullptr)   /* TEST 808: 8, was 2 */
        return;
    const uint64_t now = GetTickCount64();
    if (feature != s_for)
    {
        s_for = feature;
        s_since = now;
        return;
    }
    if (s_since == 0 || now - s_since < 8000)
        return;
    s_since = 0;   /* once per feature */
    dump_color_center(ctx, params, s_dumps);   /* TEST 809 */

    ID3D11Resource* res = nullptr;
    const_cast<NVSDK_NGX_Parameter*>(params)->Get("Output", &res);
    ID3D11Texture2D* tex = nullptr;
    if (res == nullptr || FAILED(res->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&tex))))
        return;
    D3D11_TEXTURE2D_DESC d = {};
    tex->GetDesc(&d);
    if (d.Format != DXGI_FORMAT_R16G16B16A16_FLOAT || d.Width < 1200 || d.Height < 500)
    {
        BVR_INFO("DLSS dump (TEST 800): output format %d %ux%u not handled.", d.Format, d.Width, d.Height);
        tex->Release();
        return;
    }
    ID3D11Device* dev = nullptr;
    ctx->GetDevice(&dev);
    D3D11_TEXTURE2D_DESC sd = d;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.BindFlags = 0;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    sd.MiscFlags = 0;
    sd.MipLevels = 1;
    sd.ArraySize = 1;
    ID3D11Texture2D* st = nullptr;
    if (dev == nullptr || FAILED(dev->CreateTexture2D(&sd, nullptr, &st)))
    {
        if (dev) dev->Release();
        tex->Release();
        return;
    }
    ctx->CopySubresourceRegion(st, 0, 0, 0, 0, tex, 0, nullptr);
    D3D11_MAPPED_SUBRESOURCE m = {};
    if (SUCCEEDED(ctx->Map(st, 0, D3D11_MAP_READ, 0, &m)))   /* one stall, twice a session */
    {
        UINT w = 0, h = 0;
        wchar_t name[64];
        std::vector<uint8_t> corner = region(m, 0, d.Height - 500, 1200, 500, 1, &w, &h);
        swprintf_s(name, L"BVR_DLSS_%d_corner.bmp", s_dumps);
        write_bmp(name, corner, w, h);
        /* TEST 808: the centre at full size - the same view whatever the fov crop,
           so crop on/off and each DLSS mode compare on identical content. */
        std::vector<uint8_t> centre = region(m, d.Width / 2 - 512, d.Height / 2 - 512, 1024, 1024, 1, &w, &h);
        swprintf_s(name, L"BVR_DLSS_%d_center.bmp", s_dumps);
        write_bmp(name, centre, w, h);
        std::vector<uint8_t> full = region(m, 0, 0, d.Width, d.Height, 4, &w, &h);
        swprintf_s(name, L"BVR_DLSS_%d_full.bmp", s_dumps);
        write_bmp(name, full, w, h);
        ctx->Unmap(st, 0);
        BVR_INFO("DLSS dump (TEST 800): wrote Logs\\BVR_DLSS_%d_corner.bmp and _full.bmp from eye 0's "
                 "%ux%u output (PerfQualityValue %d).", s_dumps, d.Width, d.Height, g_lastQuality.load());
        g_finalPending[0].store(s_dumps);
        g_finalPending[1].store(s_dumps);
        /* TEST 816: the engine's post-DLSS passes (sizes, uv scale cb2[33]) of this
           very state, beside its sharpness numbers. Needs frame_map = 1. */
        bvr::frame_map_request_capture("TEST 816: beside a DLSS dump");
        ++s_dumps;
    }
    st->Release();
    dev->Release();
    tex->Release();
}

} /* namespace */

/* TEST 805: THE IMAGE THE RUNTIME GETS, BESIDE THE TEST 800 DUMP. With the fov
   crop on, DLSS's own output was sharp while the headset was not, so the next
   submit of eye 0 after a DLSS dump saves the same corner of what it hands the
   runtime - after the engine's passes and the sharpener - as
   Logs\BVR_FINAL_<n>_corner.bmp. Same frame or the next; stand still.
   TEST 813: every single-frame dump was sharp, but the headset sees a STREAM - a
   frame-to-frame wobble (jitter left in, a sub-pixel shift) reads as blur and no
   still can show it. So the next 8 eye-0 submits also save their centre 1024x1024
   (BVR_FINAL_<n>_seq<k>.bmp) and the log gives each frame's mean |Laplacian|
   (sharpness) and mean |change| from the frame before (wobble). Game paused, head still. */
void ngx_dump_final(ID3D11DeviceContext* ctx, ID3D11Texture2D* tex, int stage)
{
    static int s_nn[2] = { -1, -1 }, s_kk[2] = {};   /* TEST 813: the sequence in progress, per stage (TEST 814) */
    static std::vector<uint8_t> s_pp[2];             /* its previous frame's centre luma */
    stage = stage != 0 ? 1 : 0;
    int& s_n = s_nn[stage];
    int& s_k = s_kk[stage];
    std::vector<uint8_t>& s_prev = s_pp[stage];
    const char* const tag = stage == 0 ? "PRE" : "FINAL";
    if (ctx == nullptr || tex == nullptr)
        return;
    if (s_n < 0)
    {
        if (g_finalPending[stage].load(std::memory_order_relaxed) < 0)
            return;
        s_n = g_finalPending[stage].exchange(-1);
        s_k = 0;
        s_prev.clear();
    }
    const int n = s_n;
    const int k = s_k++;
    if (s_k >= 8)
        s_n = -1;
    D3D11_TEXTURE2D_DESC d = {};
    tex->GetDesc(&d);
    const bool bgra = d.Format == DXGI_FORMAT_B8G8R8A8_UNORM || d.Format == DXGI_FORMAT_B8G8R8A8_TYPELESS ||
                      d.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
    const bool rgba = d.Format == DXGI_FORMAT_R8G8B8A8_UNORM || d.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS ||
                      d.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
    if ((!bgra && !rgba) || d.Width < 1200 || d.Height < 500 || d.SampleDesc.Count != 1)
    {
        BVR_INFO("Final dump (TEST 805): format %d %ux%u not handled.", d.Format, d.Width, d.Height);
        return;
    }
    ID3D11Device* dev = nullptr;
    ctx->GetDevice(&dev);
    D3D11_TEXTURE2D_DESC sd = d;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.BindFlags = 0;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    sd.MiscFlags = 0;
    sd.MipLevels = 1;
    sd.ArraySize = 1;
    ID3D11Texture2D* st = nullptr;
    if (dev == nullptr || FAILED(dev->CreateTexture2D(&sd, nullptr, &st)))
    {
        if (dev) dev->Release();
        return;
    }
    ctx->CopySubresourceRegion(st, 0, 0, 0, 0, tex, 0, nullptr);
    D3D11_MAPPED_SUBRESOURCE m = {};
    if (SUCCEEDED(ctx->Map(st, 0, D3D11_MAP_READ, 0, &m)) && d.Width >= 1024 && d.Height >= 1024)
    {
        /* TEST 813: the centre, full size, plus its sharpness and its change. */
        const UINT cw = 1024, cx = d.Width / 2 - 512, cy = d.Height / 2 - 512;
        std::vector<uint8_t> cpx(static_cast<size_t>(cw) * 3 * cw), luma(static_cast<size_t>(cw) * cw);
        for (UINT y = 0; y < cw; ++y)
        {
            const uint8_t* in = static_cast<const uint8_t*>(m.pData) + static_cast<size_t>(m.RowPitch) * (cy + y) + cx * 4;
            uint8_t* out = cpx.data() + static_cast<size_t>(cw) * 3 * (cw - 1 - y);
            for (UINT x = 0; x < cw; ++x)
            {
                const uint8_t b = in[x * 4 + (bgra ? 0 : 2)], g = in[x * 4 + 1], r = in[x * 4 + (bgra ? 2 : 0)];
                out[x * 3 + 0] = b;
                out[x * 3 + 1] = g;
                out[x * 3 + 2] = r;
                luma[static_cast<size_t>(y) * cw + x] = static_cast<uint8_t>((r * 54 + g * 183 + b * 19) >> 8);
            }
        }
        double lap = 0.0, diff = 0.0;
        for (UINT y = 1; y + 1 < cw; ++y)
            for (UINT x = 1; x + 1 < cw; ++x)
            {
                const size_t i = static_cast<size_t>(y) * cw + x;
                lap += abs(4 * luma[i] - luma[i - 1] - luma[i + 1] - luma[i - cw] - luma[i + cw]);
            }
        lap /= static_cast<double>(cw - 2) * (cw - 2);
        if (!s_prev.empty())
        {
            for (size_t i = 0; i < luma.size(); ++i)
                diff += abs(luma[i] - s_prev[i]);
            diff /= static_cast<double>(luma.size());
        }
        s_prev.swap(luma);
        wchar_t cname[64];
        swprintf_s(cname, L"BVR_%hs_%d_seq%d.bmp", tag, n, k);
        write_bmp(cname, cpx, cw, cw);
        BVR_INFO("Final sequence (TEST 813, %s): dump %d frame %d - sharpness %.2f, change from the frame before %.3f "
                 "(PerfQualityValue %d, %ux%u).", tag, n, k, lap, diff, g_lastQuality.load(), d.Width, d.Height);
    }
    if (k > 0 || stage == 0)   /* TEST 814: the corner only after the sharpener, as before */
    {
        if (m.pData != nullptr)
            ctx->Unmap(st, 0);
        st->Release();
        dev->Release();
        return;
    }
    if (m.pData != nullptr)
    {
        const UINT w = 1200, h = 500, y0 = d.Height - h, row = (w * 3 + 3) & ~3u;
        std::vector<uint8_t> px(static_cast<size_t>(row) * h);
        for (UINT y = 0; y < h; ++y)
        {
            const uint8_t* in = static_cast<const uint8_t*>(m.pData) + static_cast<size_t>(m.RowPitch) * (y0 + y);
            uint8_t* out = px.data() + static_cast<size_t>(row) * (h - 1 - y);
            for (UINT x = 0; x < w; ++x)
            {
                out[x * 3 + 0] = in[x * 4 + (bgra ? 0 : 2)];
                out[x * 3 + 1] = in[x * 4 + 1];
                out[x * 3 + 2] = in[x * 4 + (bgra ? 2 : 0)];
            }
        }
        ctx->Unmap(st, 0);
        wchar_t name[64];
        swprintf_s(name, L"BVR_FINAL_%d_corner.bmp", n);
        write_bmp(name, px, w, h);
        BVR_INFO("Final dump (TEST 805): wrote Logs\\BVR_FINAL_%d_corner.bmp from eye 0's submitted "
                 "%ux%u image (format %d).", n, d.Width, d.Height, d.Format);
    }
    st->Release();
    dev->Release();
}
