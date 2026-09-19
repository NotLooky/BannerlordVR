#include "vr_reticle.h"
#include "bvr_config.h"
#include "bvr_log.h"

#include <d3dcompiler.h>

#include <cmath>
#include <cstring>

namespace bvr {
namespace {

/* A cross with a gap in the middle and a dark outline.
 *
 * The gap matters more than it looks: a solid cross covers the thing you are
 * aiming at, which in a melee is the only part of the screen you need. The
 * outline matters for the same reason a real gunsight is not painted white -
 * a plain bright reticle disappears against snow and sky, and a plain dark one
 * disappears against armour and mud. One of each, and it survives both. */
const char* kReticleShader = R"HLSL(
RWTexture2D<float4> Dst : register(u0);

cbuffer Params : register(b0)
{
    float2 Centre;     // where the reticle sits, in pixels, for THIS eye
    float2 Origin;     // top-left of the dispatched box
    float2 Limit;      // last addressable pixel

    float  HalfLen;    // arm half-length
    float  HalfThick;  // arm half-thickness
    float  Gap;        // clear radius in the middle
    float  Alpha;
};

bool InCross(float ax, float ay, float thick, float len)
{
    bool horiz = ay <= thick && ax <= len && ax >= Gap;
    bool vert  = ax <= thick && ay <= len && ay >= Gap;
    return horiz || vert;
}

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    int2 px = int2(Origin) + int2(tid.xy);

    if (px.x < 0 || px.y < 0 || px.x > (int)Limit.x || px.y > (int)Limit.y)
        return;

    float2 d = float2(px) + 0.5 - Centre;
    float ax = abs(d.x);
    float ay = abs(d.y);

    if (InCross(ax, ay, HalfThick, HalfLen))
    {
        Dst[px] = float4(1.0, 1.0, 1.0, Alpha);
        return;
    }

    // One pixel of dark border on every side of the white.
    if (InCross(ax, ay, HalfThick + 1.5, HalfLen + 1.5))
        Dst[px] = float4(0.0, 0.0, 0.0, Alpha);
}
)HLSL";

struct Params
{
    float centre[2];
    float origin[2];
    float limit[2];
    float halfLen;
    float halfThick;
    float gap;
    float alpha;
    float pad[2];
};

ID3D11ComputeShader*       g_shader = nullptr;
ID3D11Buffer*              g_cb = nullptr;
/* One view per texture, cached.
 *
 * There are FOUR textures in play, not one: each eye has a held image and a
 * staging image, and promotion swaps the two pointers. A single cached view
 * would therefore miss on every call and rebuild itself at frame rate, which is
 * exactly the kind of per-frame resource creation this repo has already been
 * bitten by. Four entries covers every texture that can ever appear here. */
struct UavSlot
{
    ID3D11Texture2D*           texture = nullptr;
    ID3D11UnorderedAccessView* view = nullptr;
};

UavSlot g_uavs[4];
ID3D11UnorderedAccessView* g_uav = nullptr;   /* the one for this call */

bool g_failed = false;
bool g_logged = false;

float cfg(const char* key, float fallback)
{
    return config_float(key, fallback);
}

DXGI_FORMAT uav_format_for(DXGI_FORMAT textureFormat)
{
    switch (textureFormat)
    {
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        return DXGI_FORMAT_R8G8B8A8_UNORM;
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

    const HRESULT hr = D3DCompile(kReticleShader, std::strlen(kReticleShader),
                                  "vr_reticle.hlsl", nullptr, nullptr,
                                  "main", "cs_5_0",
                                  D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                                  &code, &errors);

    if (FAILED(hr) || code == nullptr)
    {
        BVR_ERR("Reticle: shader failed to compile (0x%08X): %s", hr,
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
        BVR_ERR("Reticle: CreateComputeShader failed (0x%08X).", csHr);
        g_failed = true;
        return false;
    }

    D3D11_BUFFER_DESC cbDesc = {};
    cbDesc.ByteWidth = sizeof(Params);
    cbDesc.Usage = D3D11_USAGE_DYNAMIC;
    cbDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    cbDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;

    if (FAILED(device->CreateBuffer(&cbDesc, nullptr, &g_cb)))
    {
        BVR_ERR("Reticle: could not create the constant buffer.");
        g_failed = true;
        return false;
    }

    return true;
}

bool ensure_uav(ID3D11Device* device, ID3D11Texture2D* texture)
{
    for (UavSlot& slot : g_uavs)
    {
        if (slot.texture == texture && slot.view != nullptr)
        {
            g_uav = slot.view;
            return true;
        }
    }

    D3D11_TEXTURE2D_DESC desc = {};
    texture->GetDesc(&desc);

    if ((desc.BindFlags & D3D11_BIND_UNORDERED_ACCESS) == 0)
    {
        BVR_WARN("Reticle: the held eye texture is not writable (no UNORDERED_ACCESS), "
                 "so no reticle can be drawn. Set afw = 1 - the hold textures are "
                 "allocated writable only when it is.");
        g_failed = true;
        return false;
    }

    D3D11_UNORDERED_ACCESS_VIEW_DESC view = {};
    view.Format = uav_format_for(desc.Format);
    view.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;

    ID3D11UnorderedAccessView* created = nullptr;
    if (view.Format == DXGI_FORMAT_UNKNOWN ||
        FAILED(device->CreateUnorderedAccessView(texture, &view, &created)))
    {
        BVR_ERR("Reticle: could not create a writable view of the eye texture.");
        g_failed = true;
        return false;
    }

    for (UavSlot& slot : g_uavs)
    {
        if (slot.view == nullptr)
        {
            slot.texture = texture;
            slot.view = created;
            g_uav = created;
            return true;
        }
    }

    /* More than four distinct textures means something upstream is
       reallocating them, and caching a fifth would just hide it. */
    created->Release();
    BVR_WARN("Reticle: more eye textures than expected; not drawing.");
    g_failed = true;
    return false;
}

} // namespace

bool reticle_enabled()
{
    static int cached = -1;
    if (cached < 0)
        cached = cfg("crosshair", 1.0f) > 0.5f ? 1 : 0;
    return cached != 0;
}

void reticle_draw(ID3D11Device* device, ID3D11DeviceContext* context,
                  ID3D11Texture2D* target, int eye,
                  float focalPx, float baseline)
{
    if (!reticle_enabled() || g_failed ||
        device == nullptr || context == nullptr || target == nullptr)
        return;

    if (!ensure_shader(device) || !ensure_uav(device, target))
        return;

    D3D11_TEXTURE2D_DESC desc = {};
    target->GetDesc(&desc);

    const float distance = cfg("crosshair_distance", 12.0f);
    const float sizePx = cfg("crosshair_size_px", 34.0f);
    const float thickPx = cfg("crosshair_thickness_px", 3.0f);
    const float gapPx = cfg("crosshair_gap_px", 9.0f);
    const float alpha = cfg("crosshair_alpha", 1.0f);

    /* Half the disparity, outward for the left eye and inward for the right.
       A point straight ahead of the HEAD sits to the right of the left eye's
       optical centre and to the left of the right eye's, by exactly this. */
    float half = 0.0f;
    if (focalPx > 0.0f && baseline > 0.0f && distance > 0.01f)
        half = (focalPx * baseline / distance) * 0.5f;

    Params p = {};
    p.centre[0] = desc.Width * 0.5f + (eye == 0 ? half : -half);
    p.centre[1] = desc.Height * 0.5f;
    p.halfLen = sizePx * 0.5f;
    p.halfThick = thickPx * 0.5f;
    p.gap = gapPx * 0.5f;
    p.alpha = alpha;
    p.limit[0] = static_cast<float>(desc.Width - 1);
    p.limit[1] = static_cast<float>(desc.Height - 1);

    /* Only the box the reticle can possibly touch is dispatched. At 3072x3264 a
       full-screen pass for forty pixels of cross would be absurd; this is a few
       thousand threads. */
    const float reach = p.halfLen + 3.0f;
    p.origin[0] = std::floor(p.centre[0] - reach);
    p.origin[1] = std::floor(p.centre[1] - reach);

    const UINT box = static_cast<UINT>(reach * 2.0f) + 2u;

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    if (FAILED(context->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        return;
    std::memcpy(mapped.pData, &p, sizeof(p));
    context->Unmap(g_cb, 0);

    /* Saved and restored, for the same reason the warp does it: this is rgl's
       own immediate context and leaving a UAV bound behind us surfaces three
       systems away as a texture that renders black. */
    ID3D11ComputeShader*       prevShader = nullptr;
    ID3D11Buffer*              prevCb = nullptr;
    ID3D11UnorderedAccessView* prevUav = nullptr;

    context->CSGetShader(&prevShader, nullptr, nullptr);
    context->CSGetConstantBuffers(0, 1, &prevCb);
    context->CSGetUnorderedAccessViews(0, 1, &prevUav);

    const UINT noOffset = static_cast<UINT>(-1);

    context->CSSetShader(g_shader, nullptr, 0);
    context->CSSetConstantBuffers(0, 1, &g_cb);
    context->CSSetUnorderedAccessViews(0, 1, &g_uav, &noOffset);

    context->Dispatch((box + 7) / 8, (box + 7) / 8, 1);

    ID3D11UnorderedAccessView* nullUav = nullptr;
    context->CSSetUnorderedAccessViews(0, 1, &nullUav, &noOffset);

    context->CSSetShader(prevShader, nullptr, 0);
    context->CSSetConstantBuffers(0, 1, &prevCb);
    context->CSSetUnorderedAccessViews(0, 1, &prevUav, &noOffset);

    if (prevShader != nullptr) prevShader->Release();
    if (prevCb != nullptr) prevCb->Release();
    if (prevUav != nullptr) prevUav->Release();

    if (!g_logged)
    {
        g_logged = true;
        BVR_INFO("Reticle on: %.0f px cross at %.1f m, offset %.1f px per eye. "
                 "It sits at the centre of the view, which is the aim direction "
                 "only because head aiming is driving the game's look.",
                 sizePx, distance, half);
    }
}

void reticle_release()
{
    for (UavSlot& slot : g_uavs)
    {
        if (slot.view != nullptr)
            slot.view->Release();
        slot.view = nullptr;
        slot.texture = nullptr;
    }

    g_uav = nullptr;

    if (g_cb != nullptr)     { g_cb->Release();     g_cb = nullptr; }
    if (g_shader != nullptr) { g_shader->Release(); g_shader = nullptr; }
}

} // namespace bvr
