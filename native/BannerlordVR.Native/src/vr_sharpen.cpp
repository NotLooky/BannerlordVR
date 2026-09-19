#include "vr_sharpen.h"

#include <windows.h>
#include <d3dcompiler.h>

#include "bvr_config.h"
#include "bvr_log.h"
#include "stereo_dup.h"

#include <atomic>
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

struct CasParams { float peak, sizeX, sizeY, pad; };

struct EyeTex
{
    ID3D11Texture2D*           in = nullptr;
    ID3D11ShaderResourceView*  inSrv = nullptr;
    ID3D11Texture2D*           out = nullptr;
    ID3D11UnorderedAccessView* outUav = nullptr;
    D3D11_TEXTURE2D_DESC       desc = {};
};

std::atomic<float>   g_strength{ -1.0f };     /* -1: not read from the config yet */
ID3D11ComputeShader* g_cs = nullptr;
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

bool prepare(ID3D11Device* dev, EyeTex& e, const D3D11_TEXTURE2D_DESC& sd)
{
    if (g_cs == nullptr)
    {
        ID3DBlob* code = nullptr;
        ID3DBlob* err = nullptr;
        HRESULT hr = D3DCompile(kCas, strlen(kCas), "bvr_cas", nullptr, nullptr, "main",
                                "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &err);
        if (FAILED(hr) || code == nullptr)
        {
            BVR_ERR("Sharpening: shader compile failed (0x%08X) %s", hr,
                    err ? static_cast<const char*>(err->GetBufferPointer()) : "");
            if (err) err->Release();
            if (code) code->Release();
            return false;
        }
        if (err) err->Release();
        hr = dev->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &g_cs);
        code->Release();

        D3D11_BUFFER_DESC bd = {};
        bd.ByteWidth = sizeof(CasParams);
        bd.Usage = D3D11_USAGE_DYNAMIC;
        bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        if (FAILED(hr) || FAILED(dev->CreateBuffer(&bd, nullptr, &g_cb)))
        {
            BVR_ERR("Sharpening: could not create the compute shader or its constants.");
            return false;
        }
    }

    if (e.out != nullptr && e.desc.Width == sd.Width && e.desc.Height == sd.Height &&
        e.desc.Format == sd.Format)
        return true;
    release_eye(e);

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

    D3D11_SHADER_RESOURCE_VIEW_DESC sv = {};
    sv.Format = view;
    sv.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    sv.Texture2D.MipLevels = 1;

    D3D11_UNORDERED_ACCESS_VIEW_DESC uv = {};
    uv.Format = view;
    uv.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;

    bool ok = SUCCEEDED(dev->CreateTexture2D(&d, nullptr, &e.in)) &&
              SUCCEEDED(dev->CreateShaderResourceView(e.in, &sv, &e.inSrv));
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

} // namespace

void vr_sharpen_set(float strength)
{
    if (!(strength >= 0.0f)) strength = 0.0f;
    if (strength > 1.0f) strength = 1.0f;
    const float was = g_strength.exchange(strength, std::memory_order_relaxed);
    if (was != strength)
        BVR_INFO("Sharpening: strength %.2f%s.", strength, strength > 0.001f ? "" : " (off)");
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
                                  ID3D11Texture2D* source)
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
        g_failed = g_cs == nullptr || g_cb == nullptr;
        return source;
    }

    /* Our own work, never the engine's frame. */
    StereoIgnore ignore;

    ctx->CopyResource(e.in, source);

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    if (FAILED(ctx->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        return source;
    CasParams prm = {};
    prm.peak = -1.0f / (8.0f + (5.0f - 8.0f) * s);
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
    ctx->CSSetShader(g_cs, nullptr, 0);
    ctx->CSSetConstantBuffers(0, 1, &g_cb);
    ctx->CSSetShaderResources(0, 1, &e.inSrv);
    ctx->CSSetUnorderedAccessViews(0, 1, &e.outUav, &zero);
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
        BVR_INFO("Sharpening: LIVE on the submitted eye images (%ux%u, format %d), CAS "
                 "strength %.2f - every render mode, with or without DLSS.",
                 sd.Width, sd.Height, static_cast<int>(sd.Format), s);
    }
    return e.out;
}

void vr_sharpen_release()
{
    release_eye(g_eye[0]);
    release_eye(g_eye[1]);
}

} // namespace bvr
