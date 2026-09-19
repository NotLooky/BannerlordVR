#include "ui_key.h"
#include "bvr_config.h"
#include "bvr_log.h"

#include <d3dcompiler.h>

#include <cstring>

namespace bvr {
namespace {

/* ---------------------------------------------------------------------------
 * The shader.
 *
 * Alpha comes from the brightest channel rather than a luminance weighting,
 * and that is deliberate: Bannerlord's interface is full of saturated single
 * channel colour - red health, blue morale, gold banners - and a perceptual
 * luminance would score a pure red pixel at about a third of a white one and
 * fade the health bar to a ghost. Taking the maximum channel treats a saturated
 * colour as exactly as present as a white one, which is what it is.
 *
 * The ramp between transparent and opaque exists for text. A hard cut at one
 * threshold turns every antialiased glyph edge into a stair, and those stairs
 * crawl as the head moves because the compositor is resampling them. Ramping
 * across a band keeps the edge pixels partly transparent, which is what they
 * already are in the source.
 * ------------------------------------------------------------------------- */
const char* kKeyShader = R"HLSL(
Texture2D<float4>   Src : register(t0);
RWTexture2D<float4> Dst : register(u0);

cbuffer Params : register(b0)
{
    float2 Size;
    float  Threshold;   // below this the pixel is background
    float  Softness;    // width of the ramp above the threshold
};

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= (uint)Size.x || tid.y >= (uint)Size.y)
        return;

    float4 c = Src.Load(int3(tid.xy, 0));

    // Max channel, not a luminance weighting. See the note above.
    float lit = max(c.r, max(c.g, c.b));

    float a = saturate((lit - Threshold) / max(Softness, 1e-4));

    // The colour is left ALONE and only the alpha is authored. Pre-multiplying
    // here would darken every edge against the world, because the compositor is
    // told to blend with straight alpha.
    Dst[tid.xy] = float4(c.rgb, a);
}
)HLSL";

/* ---------------------------------------------------------------------------
 * The mirror.
 *
 * Scales the eye image to the monitor with its aspect preserved and puts the
 * keyed interface on top. Nearest-sample rather than filtered: the eye target
 * is far larger than the monitor in both axes, so this is a downscale, and a
 * point sample of a downscale is sharp where a bilinear one would be soft. It
 * does alias on thin geometry, which is a fair trade for a mirror nobody is
 * wearing.
 * ------------------------------------------------------------------------- */
const char* kMirrorShader = R"HLSL(
Texture2D<float4>   Eye : register(t0);
Texture2D<float4>   Ui  : register(t1);
RWTexture2D<float4> Dst : register(u0);

cbuffer Params : register(b0)
{
    float2 DstSize;
    float2 EyeSize;
    float2 Scale;    // destination uv -> eye uv
    float2 Offset;   // the letterbox shift
};

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= (uint)DstSize.x || tid.y >= (uint)DstSize.y)
        return;

    float2 uv = (float2(tid.xy) + 0.5) / DstSize;
    float2 euv = uv * Scale + Offset;

    // Outside the fitted rectangle is the letterbox, and it is black rather
    // than a clamped edge pixel smeared down the side of the screen.
    float3 c = float3(0, 0, 0);

    if (euv.x >= 0.0 && euv.x <= 1.0 && euv.y >= 0.0 && euv.y <= 1.0)
    {
        int2 ep = int2(euv * EyeSize);
        ep = clamp(ep, int2(0, 0), int2(EyeSize) - int2(1, 1));
        c = Eye.Load(int3(ep, 0)).rgb;
    }

    // The interface is already at the monitor's size - it came from the
    // monitor's own backbuffer - so it needs no scaling, only blending.
    float4 u = Ui.Load(int3(tid.xy, 0));
    c = lerp(c, u.rgb, u.a);

    Dst[tid.xy] = float4(c, 1.0);
}
)HLSL";

struct Params
{
    float size[2];
    float threshold;
    float softness;
};

struct MirrorParams
{
    float dstSize[2];
    float eyeSize[2];
    float scale[2];
    float offset[2];
};

ID3D11ComputeShader*       g_shader = nullptr;
ID3D11Buffer*              g_cb = nullptr;
ID3D11ShaderResourceView*  g_srv = nullptr;
ID3D11UnorderedAccessView* g_uav = nullptr;

ID3D11Texture2D* g_srvFor = nullptr;
ID3D11Texture2D* g_uavFor = nullptr;

bool g_failed = false;
bool g_logged = false;

float threshold()
{
    static float cached = -1.0f;
    if (cached < 0.0f)
    {
        cached = config_float("ui_key_threshold", 0.06f);
        if (cached < 0.0f)  cached = 0.0f;
        if (cached > 0.9f)  cached = 0.9f;
    }
    return cached;
}

float softness()
{
    static float cached = -1.0f;
    if (cached < 0.0f)
    {
        cached = config_float("ui_key_softness", 0.06f);
        if (cached < 0.001f) cached = 0.001f;
        if (cached > 0.9f)   cached = 0.9f;
    }
    return cached;
}

/* The non-SRGB view of a format. The interface is authored in display space and
   we are not blending it in linear space here - we are only writing an alpha
   channel - so reading it through an _SRGB view would linearise the colour on
   the way in and hand the compositor a washed-out menu. */
DXGI_FORMAT non_srgb(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM;
    default:                              return f;
    }
}

bool ensure_shader(ID3D11Device* device)
{
    if (g_shader != nullptr)
        return true;
    if (g_failed)
        return false;

    ID3DBlob* code = nullptr;
    ID3DBlob* errors = nullptr;

    const HRESULT hr = D3DCompile(kKeyShader, std::strlen(kKeyShader), "ui_key",
                                  nullptr, nullptr, "main", "cs_5_0", 0, 0,
                                  &code, &errors);

    if (FAILED(hr) || code == nullptr)
    {
        g_failed = true;
        BVR_ERR("UI key: shader would not compile (0x%08X)%s%s", static_cast<unsigned>(hr),
                errors != nullptr ? ": " : "",
                errors != nullptr ? static_cast<const char*>(errors->GetBufferPointer()) : "");
        if (errors != nullptr) errors->Release();
        if (code != nullptr) code->Release();
        return false;
    }

    if (errors != nullptr)
        errors->Release();

    const HRESULT sh = device->CreateComputeShader(code->GetBufferPointer(),
                                                   code->GetBufferSize(), nullptr, &g_shader);
    code->Release();

    if (FAILED(sh))
    {
        g_failed = true;
        BVR_ERR("UI key: CreateComputeShader failed (0x%08X).", static_cast<unsigned>(sh));
        return false;
    }

    D3D11_BUFFER_DESC bd = {};
    bd.ByteWidth = sizeof(Params);
    bd.Usage = D3D11_USAGE_DYNAMIC;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;

    if (FAILED(device->CreateBuffer(&bd, nullptr, &g_cb)))
    {
        g_failed = true;
        BVR_ERR("UI key: could not allocate the constant buffer.");
        return false;
    }

    return true;
}

bool ensure_srv(ID3D11Device* device, ID3D11Texture2D* src)
{
    if (g_srv != nullptr && g_srvFor == src)
        return true;

    if (g_srv != nullptr)
    {
        g_srv->Release();
        g_srv = nullptr;
    }

    D3D11_TEXTURE2D_DESC d = {};
    src->GetDesc(&d);

    D3D11_SHADER_RESOURCE_VIEW_DESC sd = {};
    sd.Format = non_srgb(d.Format);
    sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    sd.Texture2D.MipLevels = 1;

    if (FAILED(device->CreateShaderResourceView(src, &sd, &g_srv)))
    {
        BVR_WARN("UI key: the game's image cannot be read as a shader resource "
                 "(format %d); no overlay this session.", static_cast<int>(d.Format));
        g_failed = true;
        return false;
    }

    g_srvFor = src;
    return true;
}

bool ensure_uav(ID3D11Device* device, ID3D11Texture2D* dst)
{
    if (g_uav != nullptr && g_uavFor == dst)
        return true;

    if (g_uav != nullptr)
    {
        g_uav->Release();
        g_uav = nullptr;
    }

    D3D11_TEXTURE2D_DESC d = {};
    dst->GetDesc(&d);

    D3D11_UNORDERED_ACCESS_VIEW_DESC ud = {};
    ud.Format = non_srgb(d.Format);
    ud.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;

    if (FAILED(device->CreateUnorderedAccessView(dst, &ud, &g_uav)))
    {
        BVR_WARN("UI key: the overlay texture cannot be written as a UAV "
                 "(format %d); no overlay this session.", static_cast<int>(d.Format));
        g_failed = true;
        return false;
    }

    g_uavFor = dst;
    return true;
}

// --- mirror resources ------------------------------------------------------

ID3D11ComputeShader*       g_mirrorShader = nullptr;
ID3D11Buffer*              g_mirrorCb = nullptr;
ID3D11ShaderResourceView*  g_mirrorEyeSrv = nullptr;
ID3D11ShaderResourceView*  g_mirrorUiSrv = nullptr;
ID3D11UnorderedAccessView* g_mirrorUav = nullptr;

ID3D11Texture2D* g_mirrorEyeFor = nullptr;
ID3D11Texture2D* g_mirrorUiFor = nullptr;
ID3D11Texture2D* g_mirrorUavFor = nullptr;

bool g_mirrorFailed = false;
bool g_mirrorLogged = false;

bool ensure_mirror_shader(ID3D11Device* device)
{
    if (g_mirrorShader != nullptr)
        return true;
    if (g_mirrorFailed)
        return false;

    ID3DBlob* code = nullptr;
    ID3DBlob* errors = nullptr;

    const HRESULT hr = D3DCompile(kMirrorShader, std::strlen(kMirrorShader), "ui_mirror",
                                  nullptr, nullptr, "main", "cs_5_0", 0, 0,
                                  &code, &errors);

    if (FAILED(hr) || code == nullptr)
    {
        g_mirrorFailed = true;
        BVR_ERR("Monitor mirror: shader would not compile (0x%08X)%s%s",
                static_cast<unsigned>(hr), errors != nullptr ? ": " : "",
                errors != nullptr ? static_cast<const char*>(errors->GetBufferPointer()) : "");
        if (errors != nullptr) errors->Release();
        if (code != nullptr) code->Release();
        return false;
    }

    if (errors != nullptr)
        errors->Release();

    const HRESULT sh = device->CreateComputeShader(code->GetBufferPointer(),
                                                   code->GetBufferSize(), nullptr,
                                                   &g_mirrorShader);
    code->Release();

    if (FAILED(sh))
    {
        g_mirrorFailed = true;
        BVR_ERR("Monitor mirror: CreateComputeShader failed (0x%08X).",
                static_cast<unsigned>(sh));
        return false;
    }

    D3D11_BUFFER_DESC bd = {};
    bd.ByteWidth = sizeof(MirrorParams);
    bd.Usage = D3D11_USAGE_DYNAMIC;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;

    if (FAILED(device->CreateBuffer(&bd, nullptr, &g_mirrorCb)))
    {
        g_mirrorFailed = true;
        BVR_ERR("Monitor mirror: could not allocate the constant buffer.");
        return false;
    }

    return true;
}

/* One SRV cache shared by both mirror inputs - which texture it is for is the
   caller's business, so the cached pointer travels with the view. */
bool ensure_view(ID3D11Device* device, ID3D11Texture2D* tex,
                 ID3D11ShaderResourceView*& view, ID3D11Texture2D*& viewFor)
{
    if (view != nullptr && viewFor == tex)
        return true;

    if (view != nullptr)
    {
        view->Release();
        view = nullptr;
    }

    D3D11_TEXTURE2D_DESC d = {};
    tex->GetDesc(&d);

    D3D11_SHADER_RESOURCE_VIEW_DESC sd = {};
    sd.Format = non_srgb(d.Format);
    sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    sd.Texture2D.MipLevels = 1;

    if (FAILED(device->CreateShaderResourceView(tex, &sd, &view)))
    {
        BVR_WARN("Monitor mirror: a source texture (format %d) cannot be sampled; "
                 "the window keeps whatever the game put there.",
                 static_cast<int>(d.Format));
        g_mirrorFailed = true;
        return false;
    }

    viewFor = tex;
    return true;
}

bool ensure_mirror_uav(ID3D11Device* device, ID3D11Texture2D* dst)
{
    if (g_mirrorUav != nullptr && g_mirrorUavFor == dst)
        return true;

    if (g_mirrorUav != nullptr)
    {
        g_mirrorUav->Release();
        g_mirrorUav = nullptr;
    }

    D3D11_TEXTURE2D_DESC d = {};
    dst->GetDesc(&d);

    D3D11_UNORDERED_ACCESS_VIEW_DESC ud = {};
    ud.Format = non_srgb(d.Format);
    ud.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;

    if (FAILED(device->CreateUnorderedAccessView(dst, &ud, &g_mirrorUav)))
    {
        BVR_WARN("Monitor mirror: the compose target (format %d) will not take a UAV.",
                 static_cast<int>(d.Format));
        g_mirrorFailed = true;
        return false;
    }

    g_mirrorUavFor = dst;
    return true;
}

} // namespace

bool ui_key_apply(ID3D11Device* device, ID3D11DeviceContext* context,
                  ID3D11Texture2D* src, ID3D11Texture2D* dst)
{
    if (device == nullptr || context == nullptr || src == nullptr || dst == nullptr)
        return false;

    if (g_failed)
        return false;

    if (!ensure_shader(device) || !ensure_srv(device, src) || !ensure_uav(device, dst))
        return false;

    D3D11_TEXTURE2D_DESC sd = {};
    src->GetDesc(&sd);

    D3D11_MAPPED_SUBRESOURCE m = {};
    if (FAILED(context->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
        return false;

    Params p = {};
    p.size[0] = static_cast<float>(sd.Width);
    p.size[1] = static_cast<float>(sd.Height);
    p.threshold = threshold();
    p.softness = softness();
    std::memcpy(m.pData, &p, sizeof(p));
    context->Unmap(g_cb, 0);

    /* The engine owns this context and will keep using it the moment we hand it
       back, so everything bound here is unbound again below. A stray SRV left on
       a compute slot is harmless; a stray UAV is not, because the next thing to
       bind that texture as a render target finds it still bound for writing. */
    context->CSSetShader(g_shader, nullptr, 0);
    context->CSSetShaderResources(0, 1, &g_srv);
    context->CSSetUnorderedAccessViews(0, 1, &g_uav, nullptr);
    context->CSSetConstantBuffers(0, 1, &g_cb);

    context->Dispatch((sd.Width + 7) / 8, (sd.Height + 7) / 8, 1);

    ID3D11ShaderResourceView* noSrv = nullptr;
    ID3D11UnorderedAccessView* noUav = nullptr;
    ID3D11Buffer* noCb = nullptr;
    context->CSSetShaderResources(0, 1, &noSrv);
    context->CSSetUnorderedAccessViews(0, 1, &noUav, nullptr);
    context->CSSetConstantBuffers(0, 1, &noCb);
    context->CSSetShader(nullptr, nullptr, 0);

    if (!g_logged)
    {
        g_logged = true;
        BVR_INFO("UI key: the interface is being composited over the world, keyed at "
                 "%.2f with a %.2f ramp. Raise ui_key_threshold if the background "
                 "shows as a haze; lower it if dark parts of the interface vanish.",
                 p.threshold, p.softness);
    }

    return true;
}

bool ui_mirror_compose(ID3D11Device* device, ID3D11DeviceContext* context,
                       ID3D11Texture2D* eye, ID3D11Texture2D* ui,
                       ID3D11Texture2D* dst)
{
    if (device == nullptr || context == nullptr ||
        eye == nullptr || ui == nullptr || dst == nullptr)
        return false;

    if (g_mirrorFailed)
        return false;

    if (!ensure_mirror_shader(device) ||
        !ensure_view(device, eye, g_mirrorEyeSrv, g_mirrorEyeFor) ||
        !ensure_view(device, ui, g_mirrorUiSrv, g_mirrorUiFor) ||
        !ensure_mirror_uav(device, dst))
    {
        return false;
    }

    D3D11_TEXTURE2D_DESC ed = {};
    D3D11_TEXTURE2D_DESC dd = {};
    eye->GetDesc(&ed);
    dst->GetDesc(&dd);

    if (ed.Width == 0 || ed.Height == 0 || dd.Width == 0 || dd.Height == 0)
        return false;

    /* Fit, not fill. Whichever axis is relatively tighter decides the scale,
       and the other gets bars - so nothing is cut off and nothing is stretched. */
    const float dstAspect = static_cast<float>(dd.Width) / static_cast<float>(dd.Height);
    const float eyeAspect = static_cast<float>(ed.Width) / static_cast<float>(ed.Height);

    float sx = 1.0f;
    float sy = 1.0f;

    if (dstAspect > eyeAspect)
    {
        /* Monitor is wider than the eye: bars left and right. Sampling has to
           reach ACROSS more of the destination per unit of source, so the
           destination-to-source scale on x grows. */
        sx = dstAspect / eyeAspect;
    }
    else
    {
        sy = eyeAspect / dstAspect;
    }

    MirrorParams p = {};
    p.dstSize[0] = static_cast<float>(dd.Width);
    p.dstSize[1] = static_cast<float>(dd.Height);
    p.eyeSize[0] = static_cast<float>(ed.Width);
    p.eyeSize[1] = static_cast<float>(ed.Height);
    p.scale[0] = sx;
    p.scale[1] = sy;

    /* Centre the fitted rectangle: half the overshoot goes to each side. */
    p.offset[0] = (1.0f - sx) * 0.5f;
    p.offset[1] = (1.0f - sy) * 0.5f;

    D3D11_MAPPED_SUBRESOURCE m = {};
    if (FAILED(context->Map(g_mirrorCb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
        return false;
    std::memcpy(m.pData, &p, sizeof(p));
    context->Unmap(g_mirrorCb, 0);

    ID3D11ShaderResourceView* srvs[2] = { g_mirrorEyeSrv, g_mirrorUiSrv };

    context->CSSetShader(g_mirrorShader, nullptr, 0);
    context->CSSetShaderResources(0, 2, srvs);
    context->CSSetUnorderedAccessViews(0, 1, &g_mirrorUav, nullptr);
    context->CSSetConstantBuffers(0, 1, &g_mirrorCb);

    context->Dispatch((dd.Width + 7) / 8, (dd.Height + 7) / 8, 1);

    /* Unbound again, for the reason the key pass gives: the engine keeps using
       this context and a UAV left bound blocks the next use of that texture. */
    ID3D11ShaderResourceView* noSrv[2] = { nullptr, nullptr };
    ID3D11UnorderedAccessView* noUav = nullptr;
    ID3D11Buffer* noCb = nullptr;
    context->CSSetShaderResources(0, 2, noSrv);
    context->CSSetUnorderedAccessViews(0, 1, &noUav, nullptr);
    context->CSSetConstantBuffers(0, 1, &noCb);
    context->CSSetShader(nullptr, nullptr, 0);

    if (!g_mirrorLogged)
    {
        g_mirrorLogged = true;
        BVR_INFO("Monitor mirror: showing the headset's own eye (%ux%u) fitted to the "
                 "window (%ux%u) with the interface composited on top. The window was "
                 "showing the interface over a stale frame, because AFR renders the "
                 "world into the eye target instead of the swapchain.",
                 ed.Width, ed.Height, dd.Width, dd.Height);
    }

    return true;
}

void ui_key_release()
{
    if (g_mirrorUav != nullptr)    { g_mirrorUav->Release();    g_mirrorUav = nullptr; }
    if (g_mirrorUiSrv != nullptr)  { g_mirrorUiSrv->Release();  g_mirrorUiSrv = nullptr; }
    if (g_mirrorEyeSrv != nullptr) { g_mirrorEyeSrv->Release(); g_mirrorEyeSrv = nullptr; }
    if (g_mirrorCb != nullptr)     { g_mirrorCb->Release();     g_mirrorCb = nullptr; }
    if (g_mirrorShader != nullptr) { g_mirrorShader->Release(); g_mirrorShader = nullptr; }

    g_mirrorEyeFor = nullptr;
    g_mirrorUiFor = nullptr;
    g_mirrorUavFor = nullptr;


    /* The key pass owns these; released in the same call so one caller frees
       everything this file allocated. */
    if (g_uav != nullptr)    { g_uav->Release();    g_uav = nullptr; }
    if (g_srv != nullptr)    { g_srv->Release();    g_srv = nullptr; }
    if (g_cb != nullptr)     { g_cb->Release();     g_cb = nullptr; }
    if (g_shader != nullptr) { g_shader->Release(); g_shader = nullptr; }

    g_srvFor = nullptr;
    g_uavFor = nullptr;
}

} // namespace bvr
