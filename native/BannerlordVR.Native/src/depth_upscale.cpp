#include "depth_upscale.h"
#include "bvr_log.h"

#include <d3dcompiler.h>
#include <cstring>

namespace bvr {

namespace {

/* WHY POINT SAMPLING AND NOT BILINEAR.
 *
 * Bilinear across a silhouette averages a foreground depth with a background
 * one and produces a value that exists nowhere in the scene - a ramp from the
 * near surface to the far one, a couple of pixels wide, all the way round every
 * object. The compositor would then displace that ramp as though it were real
 * geometry, and a near object would drag a skirt of stretched background with
 * it whenever the head moved. That is the classic rubber-sheet artifact of
 * depth-based reprojection and it is far more visible than the thing it would
 * be smoothing.
 *
 * Point sampling can only ever emit depths the renderer actually produced. At a
 * 2x upscale each output block of four pixels shares one, which is an honest
 * statement of what is known: the scene was shaded at half resolution and the
 * silhouettes are no finer than that. The error is bounded by one source pixel
 * and it does not invent surfaces.
 *
 * The tempting middle ground - take the NEAREST of the four candidates, so
 * foreground wins at edges - was left out on purpose. It dilates every object
 * by a pixel and drags a one-pixel rim of background along with it, trading a
 * symmetric error for a biased one, and the bias is in the direction that
 * moves. */
const char* kUpscaleShader = R"HLSL(
Texture2D<float>   Src : register(t0);
RWTexture2D<float> Dst : register(u0);

cbuffer Params : register(b0)
{
    uint2 DstSize;
    uint2 SrcSize;
};

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= DstSize.x || tid.y >= DstSize.y)
        return;

    /* Centre of the destination pixel, mapped into source space. The +0.5 and
       the floor together are what make this a resampling rather than an index
       scaling: without it the mapping is biased half a source pixel towards the
       origin, which at a 2x ratio is a whole output pixel of shift across the
       entire image. */
    float2 uv = ((float2)tid.xy + 0.5) * ((float2)SrcSize / (float2)DstSize);

    int2 s = (int2)uv;
    s = clamp(s, int2(0, 0), (int2)SrcSize - 1);

    Dst[tid.xy] = Src[s];
}
)HLSL";

struct Params
{
    uint32_t dstWidth;
    uint32_t dstHeight;
    uint32_t srcWidth;
    uint32_t srcHeight;
};

ID3D11ComputeShader* g_shader = nullptr;
ID3D11Buffer*        g_params = nullptr;
bool                 g_failed = false;

ID3D11Texture2D*           g_dst = nullptr;
ID3D11UnorderedAccessView* g_dstUav = nullptr;
uint32_t                   g_dstWidth = 0;
uint32_t                   g_dstHeight = 0;

ID3D11ShaderResourceView* g_srcSrv = nullptr;
ID3D11Texture2D*          g_srcSrvFor = nullptr;

/* The readable member of a depth format's family. A depth texture cannot be
   sampled through its own DXGI_FORMAT_D* name; the SRV has to be created with
   the colour-typed sibling that shares its bits. */
DXGI_FORMAT depth_srv_format(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_R32G8X24_TYPELESS:
    case DXGI_FORMAT_D32_FLOAT_S8X24_UINT:
        return DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS;
    case DXGI_FORMAT_R32_TYPELESS:
    case DXGI_FORMAT_D32_FLOAT:
        return DXGI_FORMAT_R32_FLOAT;
    case DXGI_FORMAT_R24G8_TYPELESS:
    case DXGI_FORMAT_D24_UNORM_S8_UINT:
        return DXGI_FORMAT_R24_UNORM_X8_TYPELESS;
    case DXGI_FORMAT_R16_TYPELESS:
    case DXGI_FORMAT_D16_UNORM:
        return DXGI_FORMAT_R16_UNORM;
    default:
        return DXGI_FORMAT_UNKNOWN;
    }
}

bool ensure_shader(ID3D11Device* device)
{
    if (g_shader != nullptr)
        return true;
    if (g_failed || device == nullptr)
        return false;

    ID3DBlob* code = nullptr;
    ID3DBlob* errors = nullptr;

    const HRESULT hr = D3DCompile(kUpscaleShader, std::strlen(kUpscaleShader),
                                  "depth_upscale.hlsl", nullptr, nullptr,
                                  "main", "cs_5_0",
                                  D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                                  &code, &errors);

    if (FAILED(hr) || code == nullptr)
    {
        BVR_ERR("Depth upscale: shader failed to compile (0x%08X): %s", hr,
                errors != nullptr ? static_cast<const char*>(errors->GetBufferPointer())
                                  : "no compiler output");
        g_failed = true;
        if (errors != nullptr) errors->Release();
        if (code != nullptr) code->Release();
        return false;
    }

    if (errors != nullptr)
        errors->Release();

    const HRESULT csHr = device->CreateComputeShader(code->GetBufferPointer(),
                                                     code->GetBufferSize(),
                                                     nullptr, &g_shader);
    code->Release();

    if (FAILED(csHr))
    {
        BVR_ERR("Depth upscale: CreateComputeShader failed (0x%08X).", csHr);
        g_failed = true;
        return false;
    }

    D3D11_BUFFER_DESC bd = {};
    bd.ByteWidth = sizeof(Params);
    bd.Usage = D3D11_USAGE_DEFAULT;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;

    if (FAILED(device->CreateBuffer(&bd, nullptr, &g_params)))
    {
        BVR_ERR("Depth upscale: could not allocate the parameter buffer.");
        g_failed = true;
        return false;
    }

    return true;
}

/* R32_TYPELESS, not R32_FLOAT.
 *
 * The result has to reach a D32_FLOAT swapchain image, and CopyResource will
 * only cross formats inside one type group. R32_TYPELESS is the parent of both
 * R32_FLOAT and D32_FLOAT, so the UAV can be typed R32_FLOAT for the shader to
 * write through while the copy out stays legal. A texture created as R32_FLOAT
 * would compile, run, and then fail the copy silently - CopyResource returns
 * void, so the first sign of it would be a depth layer full of nothing. */
bool ensure_dst(ID3D11Device* device, uint32_t width, uint32_t height)
{
    if (g_dst != nullptr && g_dstWidth == width && g_dstHeight == height)
        return true;

    if (g_dstUav != nullptr) { g_dstUav->Release(); g_dstUav = nullptr; }
    if (g_dst != nullptr)    { g_dst->Release();    g_dst = nullptr; }

    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R32_TYPELESS;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;

    if (FAILED(device->CreateTexture2D(&desc, nullptr, &g_dst)))
    {
        BVR_ERR("Depth upscale: could not allocate the %ux%u destination.",
                width, height);
        g_failed = true;
        return false;
    }

    D3D11_UNORDERED_ACCESS_VIEW_DESC uav = {};
    uav.Format = DXGI_FORMAT_R32_FLOAT;
    uav.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;

    if (FAILED(device->CreateUnorderedAccessView(g_dst, &uav, &g_dstUav)))
    {
        BVR_ERR("Depth upscale: could not create a writable view of the "
                "destination.");
        g_failed = true;
        return false;
    }

    g_dstWidth = width;
    g_dstHeight = height;

    BVR_INFO("Depth upscale ready: the game's depth is resampled to %ux%u so the "
             "depth layer can be submitted with the same rect as its colour. "
             "Point sampling - every value is one the renderer really produced, "
             "and no depth is invented between a silhouette and its background.",
             width, height);
    return true;
}

bool ensure_src_srv(ID3D11Device* device, ID3D11Texture2D* texture)
{
    if (g_srcSrv != nullptr && g_srcSrvFor == texture)
        return true;

    if (g_srcSrv != nullptr) { g_srcSrv->Release(); g_srcSrv = nullptr; }
    g_srcSrvFor = nullptr;

    D3D11_TEXTURE2D_DESC desc = {};
    texture->GetDesc(&desc);

    if ((desc.BindFlags & D3D11_BIND_SHADER_RESOURCE) == 0)
    {
        BVR_ERR("Depth upscale: the held depth was allocated without a shader "
                "resource bind, so it cannot be read.");
        g_failed = true;
        return false;
    }

    D3D11_SHADER_RESOURCE_VIEW_DESC view = {};
    view.Format = depth_srv_format(desc.Format);
    view.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    view.Texture2D.MipLevels = 1;

    if (view.Format == DXGI_FORMAT_UNKNOWN)
    {
        BVR_ERR("Depth upscale: depth format %d is not one this can read.",
                static_cast<int>(desc.Format));
        g_failed = true;
        return false;
    }

    if (FAILED(device->CreateShaderResourceView(texture, &view, &g_srcSrv)))
    {
        BVR_ERR("Depth upscale: could not create a view of the source depth.");
        g_failed = true;
        return false;
    }

    g_srcSrvFor = texture;
    return true;
}

} // namespace

bool depth_upscale_failed()
{
    return g_failed;
}

ID3D11Texture2D* depth_upscale(ID3D11Device* device, ID3D11DeviceContext* context,
                               ID3D11Texture2D* src,
                               uint32_t dstWidth, uint32_t dstHeight)
{
    if (g_failed || device == nullptr || context == nullptr || src == nullptr)
        return nullptr;

    if (!ensure_shader(device) ||
        !ensure_dst(device, dstWidth, dstHeight) ||
        !ensure_src_srv(device, src))
        return nullptr;

    D3D11_TEXTURE2D_DESC srcDesc = {};
    src->GetDesc(&srcDesc);

    Params params = {};
    params.dstWidth = dstWidth;
    params.dstHeight = dstHeight;
    params.srcWidth = srcDesc.Width;
    params.srcHeight = srcDesc.Height;
    context->UpdateSubresource(g_params, 0, nullptr, &params, 0, 0);

    /* EVERYTHING SET HERE IS PUT BACK BEFORE RETURNING.
     *
       This runs on the engine's own immediate context, in the middle of its
       frame. A compute stage left bound is not a leak that shows up later in a
       log - it is the engine's next dispatch reading our depth, and this
       project has spent enough sessions on crashes that came from exactly this
       kind of borrowed state. */
    ID3D11ComputeShader* prevShader = nullptr;
    ID3D11ClassInstance* prevInstances[256] = {};
    UINT prevInstanceCount = 256;
    context->CSGetShader(&prevShader, prevInstances, &prevInstanceCount);

    ID3D11ShaderResourceView* prevSrv = nullptr;
    context->CSGetShaderResources(0, 1, &prevSrv);

    ID3D11UnorderedAccessView* prevUav = nullptr;
    context->CSGetUnorderedAccessViews(0, 1, &prevUav);

    ID3D11Buffer* prevCb = nullptr;
    context->CSGetConstantBuffers(0, 1, &prevCb);

    context->CSSetShader(g_shader, nullptr, 0);
    context->CSSetShaderResources(0, 1, &g_srcSrv);
    context->CSSetConstantBuffers(0, 1, &g_params);

    const UINT noOffset = 0;
    context->CSSetUnorderedAccessViews(0, 1, &g_dstUav, &noOffset);

    context->Dispatch((dstWidth + 7) / 8, (dstHeight + 7) / 8, 1);

    /* Unbound BEFORE the restore, because a view cannot be bound as a UAV here
       and as something else there at the same time, and the engine's own state
       may well want this texture back. */
    ID3D11UnorderedAccessView* nullUav = nullptr;
    context->CSSetUnorderedAccessViews(0, 1, &nullUav, &noOffset);
    ID3D11ShaderResourceView* nullSrv = nullptr;
    context->CSSetShaderResources(0, 1, &nullSrv);

    context->CSSetShader(prevShader, prevInstances, prevInstanceCount);
    context->CSSetShaderResources(0, 1, &prevSrv);
    context->CSSetUnorderedAccessViews(0, 1, &prevUav, &noOffset);
    context->CSSetConstantBuffers(0, 1, &prevCb);

    if (prevShader != nullptr) prevShader->Release();
    for (UINT i = 0; i < prevInstanceCount; ++i)
        if (prevInstances[i] != nullptr) prevInstances[i]->Release();
    if (prevSrv != nullptr) prevSrv->Release();
    if (prevUav != nullptr) prevUav->Release();
    if (prevCb != nullptr) prevCb->Release();

    return g_dst;
}

void depth_upscale_release()
{
    if (g_srcSrv != nullptr) { g_srcSrv->Release(); g_srcSrv = nullptr; }
    g_srcSrvFor = nullptr;
    if (g_dstUav != nullptr) { g_dstUav->Release(); g_dstUav = nullptr; }
    if (g_dst != nullptr)    { g_dst->Release();    g_dst = nullptr; }
    if (g_params != nullptr) { g_params->Release(); g_params = nullptr; }
    if (g_shader != nullptr) { g_shader->Release(); g_shader = nullptr; }
    g_dstWidth = 0;
    g_dstHeight = 0;
    g_failed = false;
}

} // namespace bvr
