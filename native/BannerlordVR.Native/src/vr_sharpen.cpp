#include "vr_sharpen.h"

#include <windows.h>
#include <d3dcompiler.h>

#include "bvr_config.h"
#include "perf_options.h"
#include "bvr_log.h"
#include "stereo_dup.h"

#include <atomic>
#include <math.h>
#include <string.h>

namespace bvr {
namespace {

const char kCas[] = R"HLSL(
Texture2D<float4>   Src : register(t0);
RWTexture2D<float4> Dst : register(u0);
cbuffer P : register(b0) { float peak; float sizeX; float sizeY; float pad; };

float3 At(int2 p, int2 mx) { return Src.Load(int3(clamp(p, int2(0, 0), mx), 0)).rgb; }

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)sizeX || id.y >= (uint)sizeY) return;
    int2 p  = int2(id.xy);
    int2 mx = int2((int)sizeX - 1, (int)sizeY - 1);

    float3 a = At(p + int2(-1, -1), mx), b = At(p + int2(0, -1), mx), c = At(p + int2(1, -1), mx);
    float3 d = At(p + int2(-1,  0), mx), e = At(p,                mx), f = At(p + int2(1,  0), mx);
    float3 g = At(p + int2(-1,  1), mx), h = At(p + int2(0,  1), mx), i = At(p + int2(1,  1), mx);

    float3 mn  = min(min(min(d, e), min(f, b)), h);
    float3 mn2 = min(mn, min(min(a, c), min(g, i)));
    mn += mn2;
    float3 mxv  = max(max(max(d, e), max(f, b)), h);
    float3 mxv2 = max(mxv, max(max(a, c), max(g, i)));
    mxv += mxv2;

    float3 amp = sqrt(saturate(min(mn, 2.0 - mxv) / max(mxv, 1e-5)));
    float3 w   = amp * peak;
    float3 o   = saturate((b * w + d * w + f * w + h * w + e) / (1.0 + 4.0 * w));

    Dst[id.xy] = float4(o, Src.Load(int3(p, 0)).a);
}
)HLSL";

/* TEST 790: AMD FidelityFX RCAS (FSR 1, MIT) - the "robust" CAS. Five taps; the
   lobe is solved from the ring's 4x min/max so the result cannot clip to black
   or white (CAS's hard outlines when pushed), one lobe for all three channels
   (no colour fringes), and it halves where the centre is a lone outlier (noise,
   DLSS grain). `peak` is RCAS's linear sharpness, 1 = its maximum. */
const char kRcas[] = R"HLSL(
Texture2D<float4>   Src : register(t0);
RWTexture2D<float4> Dst : register(u0);
cbuffer P : register(b0) { float peak; float sizeX; float sizeY; float pad; };

float3 At(int2 p, int2 mx) { return Src.Load(int3(clamp(p, int2(0, 0), mx), 0)).rgb; }
float  Luma2(float3 c) { return c.g + 0.5 * (c.r + c.b); }

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)sizeX || id.y >= (uint)sizeY) return;
    int2 p  = int2(id.xy);
    int2 mx = int2((int)sizeX - 1, (int)sizeY - 1);

    float4 ec = Src.Load(int3(p, 0));
    float3 b = At(p + int2(0, -1), mx);
    float3 d = At(p + int2(-1, 0), mx), e = ec.rgb, f = At(p + int2(1, 0), mx);
    float3 h = At(p + int2(0,  1), mx);

    float bL = Luma2(b), dL = Luma2(d), eL = Luma2(e), fL = Luma2(f), hL = Luma2(h);
    float range = max(max(max(bL, dL), max(eL, fL)), hL) - min(min(min(bL, dL), min(eL, fL)), hL);
    float nz = 1.0 - 0.5 * saturate(abs(0.25 * (bL + dL + fL + hL) - eL) / max(range, 1e-5));

    float3 mn4 = min(min(b, d), min(f, h));
    float3 mx4 = max(max(b, d), max(f, h));
    float3 hitMin = min(mn4, e) / max(4.0 * mx4, 1e-5);
    float3 hitMax = (1.0 - max(mx4, e)) / min(4.0 * mn4 - 4.0, -1e-5);
    float3 lobe3  = max(-hitMin, hitMax);
    float  L      = min(max(max(lobe3.r, lobe3.g), lobe3.b), 0.0);
    /* TEST 791: peak up to sqrt(1.5). Past 1 the extra only fills the no-clip
       room L, so edges still never clip and 4*lobe+1 stays above 0.08; at or
       below 1 this is FSR's lobe exactly. */
    float  lobe   = max(L, max(-0.1875, L) * peak) * nz;
    float3 o = saturate((lobe * (b + d + f + h) + e) / (4.0 * lobe + 1.0));

    Dst[id.xy] = float4(o, ec.a);
}
)HLSL";

struct CasParams { float peak, sizeX, sizeY, pad; };

struct EyeTex
{
    ID3D11Texture2D*           in = nullptr;
    ID3D11ShaderResourceView*  inSrv = nullptr;
    ID3D11Texture2D*           out = nullptr;
    ID3D11UnorderedAccessView* outUav = nullptr;
    D3D11_TEXTURE2D_DESC       desc = {};
};

/* TEST 423: views straight onto the images being sharpened, so CAS reads them
   in place instead of from a full-size copy (~44 MB per eye per frame at
   3400x3256). Those images are ours - the AFR hold results and the image
   latch's output - and alternate by promotion, hence a few slots. Keyed by
   pointer without a reference of its own: the view itself holds the texture,
   so its address cannot be reused while the slot lives. Cleared when the eye
   size changes, so a rescale does not keep old targets alive. */
struct DirectSrv { ID3D11Texture2D* tex; ID3D11ShaderResourceView* srv; };
constexpr int kDirect = 8;
DirectSrv g_direct[kDirect] = {};
int       g_directNext = 0;
bool      g_directOn = true;

void release_direct()
{
    for (DirectSrv& d : g_direct)
    {
        if (d.srv) d.srv->Release();
        d = {};
    }
    g_directNext = 0;
}

std::atomic<float>   g_strength{ -1.0f };     /* -1: not read from the config yet */
std::atomic<int>     g_mode{ -1 };            /* 0 CAS, 1 RCAS; -1: not read yet */
ID3D11ComputeShader* g_cs[2] = {};            /* indexed by mode */
ID3D11Buffer*        g_cb = nullptr;
EyeTex               g_eye[2];
bool                 g_failed = false;
bool                 g_logged = false;

/* Typeless family for the copy and the typed (non-sRGB) view both views use.
   Reading the encoded values raw is what CAS wants: it works on perceptual
   values, which is what a gamma-encoded image already holds. */
bool formats_for(DXGI_FORMAT f, DXGI_FORMAT* typeless, DXGI_FORMAT* view)
{
    switch (f)
    {
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
        *typeless = DXGI_FORMAT_B8G8R8A8_TYPELESS; *view = DXGI_FORMAT_B8G8R8A8_UNORM; return true;
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
        *typeless = DXGI_FORMAT_R8G8B8A8_TYPELESS; *view = DXGI_FORMAT_R8G8B8A8_UNORM; return true;
    case DXGI_FORMAT_R10G10B10A2_UNORM:
    case DXGI_FORMAT_R10G10B10A2_TYPELESS:
        *typeless = DXGI_FORMAT_R10G10B10A2_TYPELESS; *view = DXGI_FORMAT_R10G10B10A2_UNORM; return true;
    default:
        return false;
    }
}

void release_eye(EyeTex& e)
{
    if (e.inSrv)  { e.inSrv->Release();  e.inSrv = nullptr; }
    if (e.in)     { e.in->Release();     e.in = nullptr; }
    if (e.outUav) { e.outUav->Release(); e.outUav = nullptr; }
    if (e.out)    { e.out->Release();    e.out = nullptr; }
    e.desc = {};
}

bool prepare_input(ID3D11Device* dev, EyeTex& e, const D3D11_TEXTURE2D_DESC& sd)
{
    if (e.in != nullptr && e.inSrv != nullptr) return true;
    DXGI_FORMAT typeless = DXGI_FORMAT_UNKNOWN, view = DXGI_FORMAT_UNKNOWN;
    if (!formats_for(sd.Format, &typeless, &view) || sd.SampleDesc.Count != 1) return false;
    D3D11_TEXTURE2D_DESC d = {};
    d.Width = sd.Width;
    d.Height = sd.Height;
    d.MipLevels = d.ArraySize = d.SampleDesc.Count = 1;
    d.Format = typeless;
    d.Usage = D3D11_USAGE_DEFAULT;
    d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SHADER_RESOURCE_VIEW_DESC sv = {};
    sv.Format = view;
    sv.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    sv.Texture2D.MipLevels = 1;
    if (SUCCEEDED(dev->CreateTexture2D(&d, nullptr, &e.in)) &&
        SUCCEEDED(dev->CreateShaderResourceView(e.in, &sv, &e.inSrv))) return true;
    if (e.inSrv) { e.inSrv->Release(); e.inSrv = nullptr; }
    if (e.in) { e.in->Release(); e.in = nullptr; }
    return false;
}

bool prepare(ID3D11Device* dev, EyeTex& e, const D3D11_TEXTURE2D_DESC& sd)
{
    if (g_cb == nullptr)
    {
        static const char* const kSrc[2]  = { kCas, kRcas };
        static const char* const kName[2] = { "bvr_cas", "bvr_rcas" };
        for (int m = 0; m < 2; ++m)
        {
            ID3DBlob* code = nullptr;
            ID3DBlob* err = nullptr;
            HRESULT hr = D3DCompile(kSrc[m], strlen(kSrc[m]), kName[m], nullptr, nullptr, "main",
                                    "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &err);
            if (FAILED(hr) || code == nullptr)
            {
                BVR_ERR("Sharpening: %s shader compile failed (0x%08X) %s", kName[m], hr,
                        err ? static_cast<const char*>(err->GetBufferPointer()) : "");
                if (err) err->Release();
                if (code) code->Release();
                return false;
            }
            if (err) err->Release();
            hr = dev->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &g_cs[m]);
            code->Release();
            if (FAILED(hr))
            {
                BVR_ERR("Sharpening: could not create the %s compute shader.", kName[m]);
                return false;
            }
        }

        D3D11_BUFFER_DESC bd = {};
        bd.ByteWidth = sizeof(CasParams);
        bd.Usage = D3D11_USAGE_DYNAMIC;
        bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        if (FAILED(dev->CreateBuffer(&bd, nullptr, &g_cb)))
        {
            BVR_ERR("Sharpening: could not create the shader constants.");
            return false;
        }
    }

    if (e.out != nullptr && e.desc.Width == sd.Width && e.desc.Height == sd.Height &&
        e.desc.Format == sd.Format)
        return true;
    release_eye(e);
    release_direct();

    DXGI_FORMAT typeless = DXGI_FORMAT_UNKNOWN, view = DXGI_FORMAT_UNKNOWN;
    if (!formats_for(sd.Format, &typeless, &view) || sd.SampleDesc.Count != 1)
    {
        BVR_WARN("Sharpening: eye format %d (samples %u) is not one this knows how to "
                 "sharpen; the images go up unsharpened.",
                 static_cast<int>(sd.Format), sd.SampleDesc.Count);
        return false;
    }

    D3D11_TEXTURE2D_DESC d = {};
    d.Width = sd.Width;
    d.Height = sd.Height;
    d.MipLevels = 1;
    d.ArraySize = 1;
    d.Format = typeless;
    d.SampleDesc.Count = 1;
    d.Usage = D3D11_USAGE_DEFAULT;
    d.BindFlags = D3D11_BIND_SHADER_RESOURCE;

    D3D11_UNORDERED_ACCESS_VIEW_DESC uv = {};
    uv.Format = view;
    uv.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;

    bool ok = perf_lazy_sharpen() || prepare_input(dev, e, sd);
    d.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
    ok = ok && SUCCEEDED(dev->CreateTexture2D(&d, nullptr, &e.out)) &&
         SUCCEEDED(dev->CreateUnorderedAccessView(e.out, &uv, &e.outUav));
    if (!ok)
    {
        BVR_ERR("Sharpening: could not create the %ux%u textures (format %d).",
                sd.Width, sd.Height, static_cast<int>(view));
        release_eye(e);
        return false;
    }
    e.desc = sd;
    return true;
}

/* A UNORM view onto `source` itself, or null to fall back to the copy: a typed
   sRGB source cannot be viewed as UNORM, and a texture without SRV bind cannot
   be viewed at all. */
ID3D11ShaderResourceView* direct_srv(ID3D11Device* dev, ID3D11Texture2D* source,
                                     const D3D11_TEXTURE2D_DESC& sd)
{
    if (!g_directOn)
        return nullptr;

    for (const DirectSrv& d : g_direct)
        if (d.tex == source && d.srv != nullptr)
            return d.srv;

    DXGI_FORMAT typeless = DXGI_FORMAT_UNKNOWN, view = DXGI_FORMAT_UNKNOWN;
    if ((sd.BindFlags & D3D11_BIND_SHADER_RESOURCE) == 0 ||
        !formats_for(sd.Format, &typeless, &view) ||
        (sd.Format != typeless && sd.Format != view))
        return nullptr;

    D3D11_SHADER_RESOURCE_VIEW_DESC sv = {};
    sv.Format = view;
    sv.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    sv.Texture2D.MipLevels = 1;
    ID3D11ShaderResourceView* srv = nullptr;
    if (FAILED(dev->CreateShaderResourceView(source, &sv, &srv)) || srv == nullptr)
        return nullptr;

    DirectSrv& slot = g_direct[g_directNext];
    g_directNext = (g_directNext + 1) % kDirect;
    if (slot.srv) slot.srv->Release();
    slot.tex = source;
    slot.srv = srv;
    return srv;
}

} // namespace

void vr_sharpen_set(float strength)
{
    if (!(strength >= 0.0f)) strength = 0.0f;
    if (strength > 1.5f) strength = 1.5f;   /* TEST 791: past 1 is RCAS only */
    const float was = g_strength.exchange(strength, std::memory_order_relaxed);
    if (was != strength)
        BVR_INFO("Sharpening: strength %.2f%s.", strength, strength > 0.001f ? "" : " (off)");
}

void vr_sharpen_set_mode(int mode)
{
    mode = mode == 1 ? 1 : 0;
    const int was = g_mode.exchange(mode, std::memory_order_relaxed);
    if (was != mode)
    {
        BVR_INFO("Sharpening: %s.", mode == 1 ? "RCAS" : "CAS");
        g_logged = false;   /* the next LIVE line names it */
    }
}

int vr_sharpen_get_mode()
{
    int m = g_mode.load(std::memory_order_relaxed);
    if (m < 0)
    {
        vr_sharpen_set_mode(static_cast<int>(config_float("sharpen_mode", 0.0f) + 0.5f));
        m = g_mode.load(std::memory_order_relaxed);
    }
    return m;
}

float vr_sharpen_get()
{
    float s = g_strength.load(std::memory_order_relaxed);
    if (s < 0.0f)
    {
        s = config_float("sharpen", 0.0f);
        vr_sharpen_set(s);
        s = g_strength.load(std::memory_order_relaxed);
    }
    return s;
}

ID3D11Texture2D* vr_sharpen_apply(ID3D11Device* dev, ID3D11DeviceContext* ctx, uint32_t eye,
                                  ID3D11Texture2D* source, ID3D11UnorderedAccessView* destination)
{
    const float s = vr_sharpen_get();
    if (!(s > 0.001f) || g_failed || dev == nullptr || ctx == nullptr || source == nullptr ||
        eye > 1)
        return source;

    D3D11_TEXTURE2D_DESC sd = {};
    source->GetDesc(&sd);
    EyeTex& e = g_eye[eye];
    if (!prepare(dev, e, sd))
    {
        g_failed = g_cs[0] == nullptr || g_cs[1] == nullptr || g_cb == nullptr;
        return source;
    }

    ID3D11Texture2D* output = e.out;
    ID3D11UnorderedAccessView* outputUav = e.outUav;
    if (destination != nullptr)
    {
        D3D11_UNORDERED_ACCESS_VIEW_DESC uv = {};
        destination->GetDesc(&uv);
        DXGI_FORMAT family = DXGI_FORMAT_UNKNOWN, view = DXGI_FORMAT_UNKNOWN;
        if (formats_for(sd.Format, &family, &view) && uv.Format == view &&
            uv.ViewDimension == D3D11_UAV_DIMENSION_TEXTURE2D && uv.Texture2D.MipSlice == 0)
        {
            ID3D11Resource* resource = nullptr;
            destination->GetResource(&resource);
            if (resource != nullptr)
            {
                auto texture = static_cast<ID3D11Texture2D*>(resource);
                D3D11_TEXTURE2D_DESC dd = {};
                texture->GetDesc(&dd);
                // CAS must never sharpen its own input or change the encoded colour interpretation.
                if (texture != source && dd.Width == sd.Width && dd.Height == sd.Height &&
                    dd.SampleDesc.Count == 1 && dd.MipLevels == 1 && dd.ArraySize == 1 &&
                    (dd.Format == family || dd.Format == view))
                {
                    output = texture;
                    outputUav = destination;
                }
                resource->Release(); // destination keeps the selected texture alive
            }
        }
    }

    /* Our own work, never the engine's frame. */
    StereoIgnore ignore;

    static const bool directCfg = config_float("sharpen_direct", 1.0f) > 0.5f;
    g_directOn = directCfg;
    ID3D11ShaderResourceView* src = direct_srv(dev, source, sd);
    if (src == nullptr)
    {
        if (!prepare_input(dev, e, sd)) return source;
        ctx->CopyResource(e.in, source);
        src = e.inSrv;
    }

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    if (FAILED(ctx->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        return source;
    CasParams prm = {};
    const int mode = vr_sharpen_get_mode();
    /* RCAS: sqrt so the same slider position is roughly as strong as CAS. */
    const float c = s < 1.0f ? s : 1.0f;    /* CAS's lobe blows up past 1 */
    prm.peak = mode == 1 ? sqrtf(s) : -1.0f / (8.0f + (5.0f - 8.0f) * c);
    prm.sizeX = static_cast<float>(sd.Width);
    prm.sizeY = static_cast<float>(sd.Height);
    memcpy(mapped.pData, &prm, sizeof(prm));
    ctx->Unmap(g_cb, 0);

    /* Borrowed context: put back what was bound. */
    ID3D11ComputeShader*       prevCs = nullptr;
    ID3D11Buffer*              prevCb = nullptr;
    ID3D11ShaderResourceView*  prevSrv = nullptr;
    ID3D11UnorderedAccessView* prevUav = nullptr;
    ctx->CSGetShader(&prevCs, nullptr, nullptr);
    ctx->CSGetConstantBuffers(0, 1, &prevCb);
    ctx->CSGetShaderResources(0, 1, &prevSrv);
    ctx->CSGetUnorderedAccessViews(0, 1, &prevUav);

    UINT zero = 0;
    ctx->CSSetShader(g_cs[mode], nullptr, 0);
    ctx->CSSetConstantBuffers(0, 1, &g_cb);
    ctx->CSSetShaderResources(0, 1, &src);
    ctx->CSSetUnorderedAccessViews(0, 1, &outputUav, &zero);
    ctx->Dispatch((sd.Width + 7) / 8, (sd.Height + 7) / 8, 1);

    ID3D11UnorderedAccessView* nullUav = nullptr;
    ID3D11ShaderResourceView*  nullSrv = nullptr;
    ctx->CSSetUnorderedAccessViews(0, 1, &nullUav, &zero);
    ctx->CSSetShaderResources(0, 1, &nullSrv);
    ctx->CSSetShader(prevCs, nullptr, 0);
    ctx->CSSetConstantBuffers(0, 1, &prevCb);
    ctx->CSSetShaderResources(0, 1, &prevSrv);
    UINT counts = static_cast<UINT>(-1);
    ctx->CSSetUnorderedAccessViews(0, 1, &prevUav, &counts);
    if (prevCs)  prevCs->Release();
    if (prevCb)  prevCb->Release();
    if (prevSrv) prevSrv->Release();
    if (prevUav) prevUav->Release();

    if (!g_logged)
    {
        g_logged = true;
        BVR_INFO("Sharpening: LIVE on the submitted eye images (%ux%u, format %d), %s "
                 "strength %.2f - every render mode, with or without DLSS. Source read "
                 "%s (TEST 423, sharpen_direct). Destination: %s.",
                 sd.Width, sd.Height, static_cast<int>(sd.Format), mode == 1 ? "RCAS" : "CAS", s,
                 src == e.inSrv ? "through a copy" : "in place, no copy",
                 outputUav == destination ? "acquired eye image, no final copy" : "intermediate texture");
    }
    return output;
}

void vr_sharpen_release()
{
    release_direct();
    release_eye(g_eye[0]);
    release_eye(g_eye[1]);
}

} // namespace bvr
