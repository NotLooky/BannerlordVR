#include "ui_supersample.h"
#include "bvr_log.h"

#include <atomic>
#include <cmath>
#include <mutex>
#include <vector>

namespace bvr {
namespace {

std::atomic<float> g_factor{ 0.0f };

/* The swapchain's backbuffer as of the last Present. Raw, not owned: only ever
   compared against what an RTV points at. */
std::atomic<ID3D11Texture2D*> g_backbuffer{ nullptr };

/* The dense target and the RTVs made on it, one per engine RTV format seen.
   Created on whatever thread first binds; read by the Present thread. A target
   replaced after a resize is retired, not freed - the engine may still have its
   RTV bound for the rest of that frame. */
// ponytail: retired targets live to process exit; a few per session at most (window resizes).
std::mutex g_lock;
std::atomic<ID3D11Texture2D*> g_tex{ nullptr };
UINT g_texW = 0, g_texH = 0;
float g_texK = 0.0f;
std::vector<ID3D11Texture2D*> g_retired;

struct RtvPair
{
    ID3D11RenderTargetView* engine;
    ID3D11RenderTargetView* ours;
    ID3D11Texture2D*        forTex;
};
constexpr int kPairs = 8;
RtvPair g_pairs[kPairs] = {};
std::atomic<int> g_pairCount{ 0 };

std::atomic<uint32_t> g_subs{ 0 };          /* substitutions this frame */
std::atomic<int>      g_sinceSub{ 1 << 20 }; /* frames since one */

/* Frames left standing aside: a pass bound the backbuffer WITH a depth buffer -
   a 3D scene layer (a conversation's characters) drawing into the window - and
   that cannot move to a target its depth buffer does not match. */
std::atomic<int>      g_suspend{ 0 };
bool                  g_loggedSuspend = false;
int                   g_armedFrames = 0;
bool                  g_everSub = false;
bool                  g_loggedState = false;

/* Per context: is our target bound, and the engine's own last viewports and
   scissors (unscaled) to put back when it leaves. Each context is used by one
   thread at a time, so its slot needs no lock once claimed. */
struct CtxState
{
    std::atomic<ID3D11DeviceContext*> ctx{ nullptr };
    bool redirected = false;
    UINT vpCount = 0;
    D3D11_VIEWPORT vp[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE] = {};
    UINT scCount = 0;
    D3D11_RECT sc[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE] = {};
};
constexpr int kContexts = 16;
CtxState g_ctx[kContexts];

/* Our own RSSet* calls, so the detours pass them straight through. */
thread_local bool t_internal = false;

CtxState* state_for(ID3D11DeviceContext* c)
{
    for (CtxState& s : g_ctx)
        if (s.ctx.load(std::memory_order_acquire) == c)
            return &s;
    for (CtxState& s : g_ctx)
    {
        ID3D11DeviceContext* none = nullptr;
        if (s.ctx.compare_exchange_strong(none, c, std::memory_order_acq_rel))
            return &s;
    }
    return nullptr;
}

bool is_ours(ID3D11RenderTargetView* rtv);

/* TEST 593: the flag says our target was bound; the context may have been reset
   since (ClearState, a command list executed without restoring state) without
   an OMSetRenderTargets we could see. Scaling a viewport for a pass that is NOT
   drawing into our target would wreck it, so the binding is checked, and a
   stale flag dropped. Only while the flag is up - the interface's own pass. */
bool still_ours(ID3D11DeviceContext* c, CtxState* s)
{
    ID3D11RenderTargetView* rtv = nullptr;
    c->OMGetRenderTargets(1, &rtv, nullptr);
    const bool yes = rtv != nullptr && is_ours(rtv);
    if (rtv != nullptr)
        rtv->Release();
    if (!yes)
        s->redirected = false;
    return yes;
}

DXGI_FORMAT typeless_of(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:   return DXGI_FORMAT_R8G8B8A8_TYPELESS;
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:   return DXGI_FORMAT_B8G8R8A8_TYPELESS;
    case DXGI_FORMAT_R10G10B10A2_UNORM:
    case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_TYPELESS;
    case DXGI_FORMAT_R16G16B16A16_FLOAT:
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_TYPELESS;
    default:                              return DXGI_FORMAT_UNKNOWN;
    }
}

bool is_ours(ID3D11RenderTargetView* rtv)
{
    const int n = g_pairCount.load(std::memory_order_acquire);
    for (int i = 0; i < n; ++i)
        if (g_pairs[i].ours == rtv)
            return true;
    return false;
}

bool is_backbuffer(ID3D11RenderTargetView* rtv)
{
    ID3D11Texture2D* bb = g_backbuffer.load(std::memory_order_acquire);
    if (bb == nullptr || rtv == nullptr)
        return false;
    ID3D11Resource* r = nullptr;
    rtv->GetResource(&r);
    const bool yes = r == bb;
    if (r != nullptr)
        r->Release();
    return yes;
}

/* Our RTV standing in for the engine's backbuffer RTV, (re)building the target
   when the backbuffer or k changed. */
ID3D11RenderTargetView* ours_for(ID3D11RenderTargetView* engine, float k)
{
    ID3D11Texture2D* bb = g_backbuffer.load(std::memory_order_acquire);
    if (bb == nullptr)
        return nullptr;

    std::lock_guard<std::mutex> guard(g_lock);

    D3D11_TEXTURE2D_DESC bd = {};
    bb->GetDesc(&bd);
    const UINT w = static_cast<UINT>(std::lround(bd.Width * k));
    const UINT h = static_cast<UINT>(std::lround(bd.Height * k));
    if (w > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION || h > D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION)
        return nullptr;

    ID3D11Device* device = nullptr;
    engine->GetDevice(&device);
    if (device == nullptr)
        return nullptr;
    struct DeviceRelease { ID3D11Device* d; ~DeviceRelease() { d->Release(); } } releaseDevice{ device };

    ID3D11Texture2D* tex = g_tex.load(std::memory_order_acquire);
    if (tex == nullptr || g_texW != w || g_texH != h || g_texK != k)
    {
        D3D11_TEXTURE2D_DESC td = {};
        td.Width = w;
        td.Height = h;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = typeless_of(bd.Format);
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        ID3D11Texture2D* made = nullptr;
        if (td.Format == DXGI_FORMAT_UNKNOWN || FAILED(device->CreateTexture2D(&td, nullptr, &made)))
        {
            static bool warned = false;
            if (!warned)
            {
                warned = true;
                BVR_WARN("UI supersample (TEST 591): could not make a %ux%u target (backbuffer format %d); "
                         "the interface stays at the window's size.", w, h, static_cast<int>(bd.Format));
            }
            return nullptr;
        }
        if (tex != nullptr)
            g_retired.push_back(tex);
        g_tex.store(made, std::memory_order_release);
        tex = made;
        g_texW = w;
        g_texH = h;
        g_texK = k;
        BVR_INFO("UI supersample (TEST 591): the interface is drawn into a %ux%u target (%.2fx the %ux%u "
                 "window) - sharper in the headset, same layout.", w, h, k, bd.Width, bd.Height);
    }

    const int n = g_pairCount.load(std::memory_order_acquire);
    for (int i = 0; i < n; ++i)
        if (g_pairs[i].engine == engine && g_pairs[i].forTex == tex)
            return g_pairs[i].ours;

    D3D11_RENDER_TARGET_VIEW_DESC ed = {};
    engine->GetDesc(&ed);
    D3D11_RENDER_TARGET_VIEW_DESC od = {};
    od.Format = ed.Format;
    od.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
    ID3D11RenderTargetView* rtv = nullptr;
    if (FAILED(device->CreateRenderTargetView(tex, &od, &rtv)))
        return nullptr;

    /* A slot of a retired target is reused; its RTV is kept alive with it. */
    int slot = -1;
    for (int i = 0; i < n && slot < 0; ++i)
        if (g_pairs[i].forTex != tex && g_pairs[i].engine == engine)
            slot = i;
    if (slot < 0)
    {
        if (n >= kPairs)
        {
            rtv->Release();
            return nullptr;
        }
        slot = n;
        g_pairs[slot] = { engine, rtv, tex };
        g_pairCount.store(n + 1, std::memory_order_release);
    }
    else
    {
        g_pairs[slot] = { engine, rtv, tex };   // the old RTV leaks with its retired target
    }
    return rtv;
}

} // namespace

void ui_ss_set_factor(float k)
{
    const float v = k > 1.0f ? (k > 4.0f ? 4.0f : k) : 0.0f;
    if (v != g_factor.exchange(v, std::memory_order_acq_rel))
    {
        g_armedFrames = 0;
        g_loggedState = false;
    }
}

int32_t ui_ss_state()
{
    if (g_factor.load(std::memory_order_acquire) <= 1.0f)
        return 0;
    if (g_suspend.load(std::memory_order_acquire) > 0)
        return 3;
    if (g_sinceSub.load(std::memory_order_acquire) < 30)
        return 1;
    return g_armedFrames > 180 ? 2 : 1;
}

ID3D11Texture2D* ui_ss_source()
{
    if (g_factor.load(std::memory_order_acquire) <= 1.0f ||
        g_suspend.load(std::memory_order_acquire) > 0 ||
        g_sinceSub.load(std::memory_order_acquire) >= 30)
        return nullptr;
    return g_tex.load(std::memory_order_acquire);
}

void ui_ss_frame(ID3D11DeviceContext* context, IDXGISwapChain* swapChain)
{
    ID3D11Texture2D* bb = nullptr;
    if (swapChain != nullptr &&
        SUCCEEDED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&bb))) &&
        bb != nullptr)
    {
        g_backbuffer.store(bb, std::memory_order_release);
        bb->Release();
    }

    const uint32_t subs = g_subs.exchange(0, std::memory_order_acq_rel);
    if (g_suspend.load(std::memory_order_acquire) > 0)
        g_suspend.fetch_sub(1, std::memory_order_acq_rel);
    const float k = g_factor.load(std::memory_order_acquire);
    if (subs > 0)
    {
        g_sinceSub.store(0, std::memory_order_release);
        g_everSub = true;
    }
    else
    {
        g_sinceSub.fetch_add(1, std::memory_order_acq_rel);
    }
    if (k > 1.0f)
        ++g_armedFrames;

    /* TEST 593: binds a frame over ~5 s - the UI pass binds the window a few
       times a frame; a jump means something else now draws into it. */
    {
        static uint64_t binds = 0, frames = 0, depthFrames = 0;
        binds += subs;
        ++frames;
        if (g_suspend.load(std::memory_order_acquire) > 0)
            ++depthFrames;
        if (frames >= 450)
        {
            if (k > 1.0f)
                BVR_INFO("UI supersample (TEST 593): %.1f window bind(s) a frame redirected over %llu frame(s); "
                         "%llu frame(s) stood aside for a depth-bound pass.",
                         static_cast<double>(binds) / static_cast<double>(frames),
                         static_cast<unsigned long long>(frames), static_cast<unsigned long long>(depthFrames));
            binds = frames = depthFrames = 0;
        }
    }

    if (k > 1.0f && !g_loggedState && (subs > 0 || g_armedFrames == 181))
    {
        g_loggedState = true;
        if (subs > 0)
            BVR_INFO("UI supersample (TEST 591): the interface reached our %ux%u target (%u bind(s) this frame).",
                     g_texW, g_texH, subs);
        else
            BVR_WARN("UI supersample (TEST 591): armed for 3 s and the interface never bound the window's "
                     "backbuffer%s - the engine draws it somewhere else; standing down.",
                     g_everSub ? " again" : "");
    }

    /* Read for this frame already (the key copied it); the next frame's
       interface starts from empty even if the engine never clears. Only after a
       frame that drew into it - a frame without would otherwise show nothing. */
    ID3D11Texture2D* tex = g_tex.load(std::memory_order_acquire);
    if (context != nullptr && tex != nullptr && k > 1.0f && subs > 0)
    {
        const int n = g_pairCount.load(std::memory_order_acquire);
        for (int i = 0; i < n; ++i)
        {
            if (g_pairs[i].forTex == tex)
            {
                const float zero[4] = { 0.0f, 0.0f, 0.0f, 0.0f };
                context->ClearRenderTargetView(g_pairs[i].ours, zero);
                break;
            }
        }
    }
}

bool ui_ss_substitute(ID3D11DeviceContext* c, UINT num, ID3D11RenderTargetView* const* rtvs,
                      ID3D11RenderTargetView** out, ID3D11DepthStencilView** dsv)
{
    (void)c;
    const float k = g_factor.load(std::memory_order_acquire);
    if (k <= 1.0f || rtvs == nullptr || num < 1 || num > D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT ||
        rtvs[0] == nullptr)
        return false;

    ID3D11RenderTargetView* ours = nullptr;
    if (is_ours(rtvs[0]))
        ours = rtvs[0];                       // the engine re-binding what it read back
    else if (is_backbuffer(rtvs[0]))
    {
        if (*dsv != nullptr)
        {
            g_suspend.store(60, std::memory_order_release);
            if (!g_loggedSuspend)
            {
                g_loggedSuspend = true;
                BVR_INFO("UI supersample (TEST 591): something drew into the window WITH depth (a 3D scene "
                         "layer); the interface stays at the window's size while it does.");
            }
            return false;
        }
        if (g_suspend.load(std::memory_order_acquire) > 0)
            return false;
        ours = ours_for(rtvs[0], k);
    }
    if (ours == nullptr)
        return false;

    out[0] = ours;
    for (UINT i = 1; i < num; ++i)
        out[i] = rtvs[i];
    *dsv = nullptr;   /* a window-sized depth buffer cannot sit beside it; the interface needs none */
    g_subs.fetch_add(1, std::memory_order_acq_rel);
    return true;
}

void ui_ss_after_bind(ID3D11DeviceContext* c, bool substituted)
{
    CtxState* s = state_for(c);
    if (s == nullptr)
        return;

    if (substituted)
    {
        s->redirected = true;
        D3D11_VIEWPORT full = { 0.0f, 0.0f, static_cast<float>(g_texW), static_cast<float>(g_texH), 0.0f, 1.0f };
        D3D11_RECT scaled[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE];
        const D3D11_RECT* sc = ui_ss_scissors(c, s->scCount, s->sc, scaled);
        t_internal = true;
        c->RSSetViewports(1, &full);
        if (s->scCount > 0)
            c->RSSetScissorRects(s->scCount, sc);
        t_internal = false;
    }
    else if (s->redirected)
    {
        s->redirected = false;
        t_internal = true;
        if (s->vpCount > 0)
            c->RSSetViewports(s->vpCount, s->vp);
        if (s->scCount > 0)
            c->RSSetScissorRects(s->scCount, s->sc);
        t_internal = false;
    }
}

const D3D11_VIEWPORT* ui_ss_viewports(ID3D11DeviceContext* c, UINT n, const D3D11_VIEWPORT* vps,
                                      D3D11_VIEWPORT* scratch)
{
    if (t_internal || vps == nullptr)
        return vps;
    CtxState* s = state_for(c);
    if (s == nullptr)
        return vps;
    if (n > D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE)
        n = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
    s->vpCount = n;
    for (UINT i = 0; i < n; ++i)
        s->vp[i] = vps[i];
    if (!s->redirected || !still_ours(c, s))
        return vps;

    const float k = g_texK;
    for (UINT i = 0; i < n; ++i)
    {
        scratch[i] = vps[i];
        scratch[i].TopLeftX *= k;
        scratch[i].TopLeftY *= k;
        scratch[i].Width *= k;
        scratch[i].Height *= k;
    }
    return scratch;
}

const D3D11_RECT* ui_ss_scissors(ID3D11DeviceContext* c, UINT n, const D3D11_RECT* rects,
                                 D3D11_RECT* scratch)
{
    if (t_internal || rects == nullptr)
        return rects;
    CtxState* s = state_for(c);
    if (s == nullptr)
        return rects;
    if (n > D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE)
        n = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
    if (rects != s->sc)
    {
        s->scCount = n;
        for (UINT i = 0; i < n; ++i)
            s->sc[i] = rects[i];
    }
    if (!s->redirected || !still_ours(c, s))
        return rects;

    const float k = g_texK;
    for (UINT i = 0; i < n; ++i)
    {
        scratch[i].left = static_cast<LONG>(std::floor(rects[i].left * k));
        scratch[i].top = static_cast<LONG>(std::floor(rects[i].top * k));
        scratch[i].right = static_cast<LONG>(std::ceil(rects[i].right * k));
        scratch[i].bottom = static_cast<LONG>(std::ceil(rects[i].bottom * k));
    }
    return scratch;
}

ID3D11RenderTargetView* ui_ss_clear_twin(ID3D11RenderTargetView* rtv)
{
    const float k = g_factor.load(std::memory_order_acquire);
    if (k <= 1.0f || rtv == nullptr || !is_backbuffer(rtv))
        return nullptr;
    return ours_for(rtv, k);
}

} // namespace bvr
