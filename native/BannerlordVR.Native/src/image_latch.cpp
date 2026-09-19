#include "image_latch.h"

#include "bvr_config.h"
#include "bvr_log.h"

#include <d3dcompiler.h>

#include <atomic>
#include <cmath>
#include <cstring>

namespace bvr {
namespace {

/* -----------------------------------------------------------------------------
 * THE SHADER.
 *
 * One full-screen triangle. For each OUTPUT pixel it builds the ray that pixel
 * looks along in the NOW frame, rotates that ray into the frame the image was
 * RENDERED in, and reads the image there.
 *
 * The frustum is described by its four tangents rather than by a focal length,
 * because the eye frustum is asymmetric and a single focal length cannot say
 * where the optical centre is. Tan.x/.y are left/right, Tan.z/.w are up/down -
 * and up is v = 0, because that is where the top of a texture is.
 *
 * OpenXR's eye space is right-handed with -Z forward, so a ray through the
 * frustum is (tan_x, tan_y, -1) and the way back out is a divide by -z.
 * -------------------------------------------------------------------------- */
const char* kLatchShader = R"HLSL(
Texture2D<float4> Src : register(t0);
SamplerState      LinearClamp : register(s0);

cbuffer Params : register(b0)
{
    float4 R0;   /* rotation, row 0 in xyz                                    */
    float4 R1;
    float4 R2;
    float4 Tan;  /* tanLeft, tanRight, tanUp, tanDown                         */
};

void vs(uint id : SV_VertexID,
        out float4 pos : SV_Position,
        out float2 uv  : TEXCOORD0)
{
    /* The standard oversized triangle: three vertices covering the whole
       viewport, so there is no vertex buffer and no input layout to bind. */
    uv  = float2((id << 1) & 2, id & 2);
    pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
}

float4 ps(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    const float tx = lerp(Tan.x, Tan.y, uv.x);
    const float ty = lerp(Tan.z, Tan.w, uv.y);

    const float3 d = float3(tx, ty, -1.0);
    const float3 r = float3(dot(R0.xyz, d), dot(R1.xyz, d), dot(R2.xyz, d));

    /* Rotated past the horizon of the source image. Only reachable for
       rotations far larger than one frame of head movement, and black is the
       honest answer - inventing a colour here would be inventing geometry. */
    if (r.z > -1e-6)
        return float4(0.0, 0.0, 0.0, 1.0);

    const float2 t2 = float2(r.x / -r.z, r.y / -r.z);

    const float2 suv = float2((t2.x - Tan.x) / (Tan.y - Tan.x),
                              (t2.y - Tan.z) / (Tan.w - Tan.z));

    /* Clamped rather than bordered: at the leading edge the nearest real pixel
       is a far better answer than black, and with the widened containing
       frustum there is margin before it is reached at all. */
    return Src.SampleLevel(LinearClamp, suv, 0);
}
)HLSL";

struct Params
{
    float r0[4];
    float r1[4];
    float r2[4];
    float tan[4];
};
static_assert(sizeof(Params) == 64, "the constant buffer must stay 16-byte aligned");

constexpr uint32_t kEyes = 2;
constexpr uint32_t kSrvCacheSize = 4;   /* result + stage, per eye */

ID3D11VertexShader*  g_vs = nullptr;
ID3D11PixelShader*   g_ps = nullptr;
ID3D11SamplerState*  g_sampler = nullptr;
ID3D11Buffer*        g_cb = nullptr;
bool                 g_shaderFailed = false;

ID3D11Texture2D*        g_out[kEyes]    = { nullptr, nullptr };
ID3D11RenderTargetView* g_outRtv[kEyes] = { nullptr, nullptr };

struct SrvEntry
{
    ID3D11Texture2D*          texture = nullptr;
    ID3D11ShaderResourceView* srv     = nullptr;
};
SrvEntry g_srvs[kSrvCacheSize];

bool g_applied[kEyes] = { false, false };

/* Per-second reporting, in the shape the rest of this project's logs use. */
std::atomic<uint32_t> g_count{ 0 };
std::atomic<uint32_t> g_degSum{ 0 };   /* hundredths of a degree */
std::atomic<uint32_t> g_degMax{ 0 };
std::atomic<uint32_t> g_skipped{ 0 };

/* Head overdrive, reported separately: it is a different mechanism from the
   latch and mixing their numbers would hide which one is doing what. */
std::atomic<uint32_t> g_odCount{ 0 };
std::atomic<uint32_t> g_odSum{ 0 };
std::atomic<uint32_t> g_odMax{ 0 };

/* Direction coherence, averaged for the per-second report: 100 = a steady turn,
   near 0 = a shake the gate is suppressing. */
std::atomic<uint32_t> g_cohSum{ 0 };
std::atomic<uint32_t> g_cohCount{ 0 };

/* Predictor, reported on its own line: it is the anticipatory half and mixing
   it into the overdrive numbers would hide which one is doing the work. */
std::atomic<uint32_t> g_predSum{ 0 };
std::atomic<uint32_t> g_predCount{ 0 };
std::atomic<uint32_t> g_predClamped{ 0 };
std::atomic<uint32_t> g_predCut{ 0 };   /* horizon cut short by the decel guard */

/* --------------------------------------------------------------------------
 * Small maths. Local copies rather than shared helpers: this file is the only
 * place in the native layer that needs a quaternion turned into a matrix, and
 * a private twenty lines is cheaper than a dependency.
 * ----------------------------------------------------------------------- */
XrQuaternionf quat_conj(const XrQuaternionf& q)
{
    return XrQuaternionf{ -q.x, -q.y, -q.z, q.w };
}

XrQuaternionf quat_mul(const XrQuaternionf& a, const XrQuaternionf& b)
{
    return XrQuaternionf{
        a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
        a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
        a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
        a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z,
    };
}

/* Row-major 3x3 from a unit quaternion; out[row * 3 + col]. */
void mat_from_quat(const XrQuaternionf& q, float out[9])
{
    const float x = q.x, y = q.y, z = q.z, w = q.w;
    const float xx = x * x, yy = y * y, zz = z * z;
    const float xy = x * y, xz = x * z, yz = y * z;
    const float wx = w * x, wy = w * y, wz = w * z;

    out[0] = 1.0f - 2.0f * (yy + zz);
    out[1] =        2.0f * (xy - wz);
    out[2] =        2.0f * (xz + wy);

    out[3] =        2.0f * (xy + wz);
    out[4] = 1.0f - 2.0f * (xx + zz);
    out[5] =        2.0f * (yz - wx);

    out[6] =        2.0f * (xz - wy);
    out[7] =        2.0f * (yz + wx);
    out[8] = 1.0f - 2.0f * (xx + yy);
}

/* The rotation angle of a unit quaternion, in degrees. Used for the report and
   for the runaway guard. */
float quat_angle_deg(const XrQuaternionf& q)
{
    float w = q.w;
    if (w > 1.0f)  w = 1.0f;
    if (w < -1.0f) w = -1.0f;
    return 2.0f * std::acos(std::fabs(w)) * 57.29577951f;
}

/* How far the image may be rotated before the latch declines. A hitch, a
   teleport or a recentre can put a whole different orientation on the other
   side of one frame boundary, and rotating the image by that would fling the
   world sideways for one frame - far worse than the lag it removes. */
float max_deg()
{
    static float cached = 0.0f;
    static bool  have   = false;
    if (!have) { have = true; cached = config_float("image_latch_max_deg", 8.0f); }
    return cached;
}

DXGI_FORMAT typed_format_for(DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:     return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:     return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS:  return DXGI_FORMAT_R10G10B10A2_UNORM;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    default:                                return format;
    }
}

bool ensure_shaders(ID3D11Device* device)
{
    if (g_vs != nullptr && g_ps != nullptr && g_sampler != nullptr && g_cb != nullptr)
        return true;
    if (g_shaderFailed || device == nullptr)
        return false;

    struct Entry { const char* name; const char* target; };
    const Entry entries[2] = { { "vs", "vs_5_0" }, { "ps", "ps_5_0" } };

    for (int i = 0; i < 2; ++i)
    {
        ID3DBlob* code = nullptr;
        ID3DBlob* errors = nullptr;

        const HRESULT hr = D3DCompile(kLatchShader, std::strlen(kLatchShader),
                                      "image_latch.hlsl", nullptr, nullptr,
                                      entries[i].name, entries[i].target,
                                      D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                                      &code, &errors);

        if (FAILED(hr) || code == nullptr)
        {
            BVR_ERR("Image latch: shader '%s' failed to compile (0x%08X): %s",
                    entries[i].name, hr,
                    errors != nullptr ? static_cast<const char*>(errors->GetBufferPointer())
                                      : "no compiler output");
            g_shaderFailed = true;
            if (errors != nullptr) errors->Release();
            if (code != nullptr)   code->Release();
            return false;
        }
        if (errors != nullptr) errors->Release();

        HRESULT create = S_OK;
        if (i == 0)
            create = device->CreateVertexShader(code->GetBufferPointer(),
                                                code->GetBufferSize(), nullptr, &g_vs);
        else
            create = device->CreatePixelShader(code->GetBufferPointer(),
                                               code->GetBufferSize(), nullptr, &g_ps);
        code->Release();

        if (FAILED(create))
        {
            BVR_ERR("Image latch: could not create shader '%s' (0x%08X).",
                    entries[i].name, create);
            g_shaderFailed = true;
            return false;
        }
    }

    D3D11_SAMPLER_DESC sd = {};
    sd.Filter   = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.ComparisonFunc = D3D11_COMPARISON_NEVER;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    if (FAILED(device->CreateSamplerState(&sd, &g_sampler)))
    {
        BVR_ERR("Image latch: could not create the sampler.");
        g_shaderFailed = true;
        return false;
    }

    D3D11_BUFFER_DESC bd = {};
    bd.ByteWidth      = sizeof(Params);
    bd.Usage          = D3D11_USAGE_DYNAMIC;
    bd.BindFlags      = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    if (FAILED(device->CreateBuffer(&bd, nullptr, &g_cb)))
    {
        BVR_ERR("Image latch: could not create the constant buffer.");
        g_shaderFailed = true;
        return false;
    }

    BVR_INFO("Image latch: ready. The finished eye is rotated to the live head "
             "pose on the way to the compositor, so the engine can render every "
             "pass from one camera and nothing inside the frame disagrees. "
             "image_latch = 0 disables it.");
    return true;
}

ID3D11ShaderResourceView* ensure_srv(ID3D11Device* device, ID3D11Texture2D* texture)
{
    for (uint32_t i = 0; i < kSrvCacheSize; ++i)
        if (g_srvs[i].texture == texture && g_srvs[i].srv != nullptr)
            return g_srvs[i].srv;

    D3D11_TEXTURE2D_DESC desc = {};
    texture->GetDesc(&desc);

    D3D11_SHADER_RESOURCE_VIEW_DESC vd = {};
    vd.Format = typed_format_for(desc.Format);
    vd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    vd.Texture2D.MipLevels = 1;

    ID3D11ShaderResourceView* srv = nullptr;
    if (FAILED(device->CreateShaderResourceView(texture, &vd, &srv)))
        return nullptr;

    /* The hold and staging textures swap POINTERS on promotion, so both sides
       of both eyes turn up here over time - four, which is the cache size. If
       it ever fills, the oldest slot is replaced rather than leaking. */
    for (uint32_t i = 0; i < kSrvCacheSize; ++i)
    {
        if (g_srvs[i].srv == nullptr)
        {
            g_srvs[i].texture = texture;
            g_srvs[i].srv = srv;
            return srv;
        }
    }

    if (g_srvs[0].srv != nullptr) g_srvs[0].srv->Release();
    g_srvs[0].texture = texture;
    g_srvs[0].srv = srv;
    return srv;
}

bool ensure_output(ID3D11Device* device, uint32_t eye, const D3D11_TEXTURE2D_DESC& model)
{
    if (g_out[eye] != nullptr && g_outRtv[eye] != nullptr)
        return true;

    /* The SAME description as the source, so the CopyResource that follows into
       the swapchain is the identical operation it was before this module
       existed - same size, same format, same family. Only the bind flags differ,
       and only by dropping the unordered access AFW would have asked for. */
    D3D11_TEXTURE2D_DESC desc = model;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    desc.CPUAccessFlags = 0;
    desc.MiscFlags = 0;
    desc.MipLevels = 1;
    desc.ArraySize = 1;

    if (g_out[eye] == nullptr &&
        FAILED(device->CreateTexture2D(&desc, nullptr, &g_out[eye])))
    {
        BVR_ERR("Image latch: could not create the output texture for eye %u.", eye);
        return false;
    }

    D3D11_RENDER_TARGET_VIEW_DESC rd = {};
    rd.Format = typed_format_for(desc.Format);
    rd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;

    if (g_outRtv[eye] == nullptr &&
        FAILED(device->CreateRenderTargetView(g_out[eye], &rd, &g_outRtv[eye])))
    {
        BVR_ERR("Image latch: could not create the render target view for eye %u.", eye);
        return false;
    }
    return true;
}

/* --------------------------------------------------------------------------
 * The engine owns this context and will keep drawing with it after we return.
 * Everything touched is put back.
 * ----------------------------------------------------------------------- */
struct StateGuard
{
    ID3D11DeviceContext* ctx;

    ID3D11RenderTargetView*   rtv[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
    ID3D11DepthStencilView*   dsv = nullptr;
    D3D11_VIEWPORT            viewports[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE] = {};
    UINT                      viewportCount = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
    D3D11_PRIMITIVE_TOPOLOGY  topology = D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED;
    ID3D11InputLayout*        layout = nullptr;
    ID3D11VertexShader*       vs = nullptr;
    ID3D11PixelShader*        ps = nullptr;
    ID3D11GeometryShader*     gs = nullptr;
    ID3D11ShaderResourceView* srv0 = nullptr;
    ID3D11SamplerState*       samp0 = nullptr;
    ID3D11Buffer*             cb0 = nullptr;
    ID3D11BlendState*         blend = nullptr;
    FLOAT                     blendFactor[4] = {};
    UINT                      sampleMask = 0;
    ID3D11DepthStencilState*  depth = nullptr;
    UINT                      stencilRef = 0;
    ID3D11RasterizerState*    raster = nullptr;

    explicit StateGuard(ID3D11DeviceContext* c) : ctx(c)
    {
        ctx->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, rtv, &dsv);
        ctx->RSGetViewports(&viewportCount, viewports);
        ctx->IAGetPrimitiveTopology(&topology);
        ctx->IAGetInputLayout(&layout);
        ctx->VSGetShader(&vs, nullptr, nullptr);
        ctx->PSGetShader(&ps, nullptr, nullptr);
        ctx->GSGetShader(&gs, nullptr, nullptr);
        ctx->PSGetShaderResources(0, 1, &srv0);
        ctx->PSGetSamplers(0, 1, &samp0);
        ctx->PSGetConstantBuffers(0, 1, &cb0);
        ctx->OMGetBlendState(&blend, blendFactor, &sampleMask);
        ctx->OMGetDepthStencilState(&depth, &stencilRef);
        ctx->RSGetState(&raster);
    }

    ~StateGuard()
    {
        ctx->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, rtv, dsv);
        if (viewportCount > 0)
            ctx->RSSetViewports(viewportCount, viewports);
        ctx->IASetPrimitiveTopology(topology);
        ctx->IASetInputLayout(layout);
        ctx->VSSetShader(vs, nullptr, 0);
        ctx->PSSetShader(ps, nullptr, 0);
        ctx->GSSetShader(gs, nullptr, 0);
        ctx->PSSetShaderResources(0, 1, &srv0);
        ctx->PSSetSamplers(0, 1, &samp0);
        ctx->PSSetConstantBuffers(0, 1, &cb0);
        ctx->OMSetBlendState(blend, blendFactor, sampleMask);
        ctx->OMSetDepthStencilState(depth, stencilRef);
        ctx->RSSetState(raster);

        for (UINT i = 0; i < D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT; ++i)
            if (rtv[i] != nullptr) rtv[i]->Release();
        if (dsv    != nullptr) dsv->Release();
        if (layout != nullptr) layout->Release();
        if (vs     != nullptr) vs->Release();
        if (ps     != nullptr) ps->Release();
        if (gs     != nullptr) gs->Release();
        if (srv0   != nullptr) srv0->Release();
        if (samp0  != nullptr) samp0->Release();
        if (cb0    != nullptr) cb0->Release();
        if (blend  != nullptr) blend->Release();
        if (depth  != nullptr) depth->Release();
        if (raster != nullptr) raster->Release();
    }

    StateGuard(const StateGuard&) = delete;
    StateGuard& operator=(const StateGuard&) = delete;
};

} // namespace

float image_latch_mode()
{
    static float cached = 0.0f;
    static bool  have = false;
    if (!have)
    {
        have = true;
        cached = config_float("image_latch", 0.0f);
        if (cached >  1.0f) cached =  1.0f;
        if (cached < -1.0f) cached = -1.0f;
    }
    return cached;
}

bool image_latch_enabled()
{
    return image_latch_mode() != 0.0f;
}

bool image_latch_applied(uint32_t eye)
{
    return eye < kEyes && g_applied[eye];
}

ID3D11Texture2D* image_latch_apply(ID3D11Device* device,
                                   ID3D11DeviceContext* context,
                                   uint32_t eye,
                                   ID3D11Texture2D* source,
                                   const XrQuaternionf& renderOrientation,
                                   const XrQuaternionf& nowOrientation,
                                   const XrFovf& fov)
{
    if (eye < kEyes)
        g_applied[eye] = false;

    const float sign = image_latch_mode();
    if (sign == 0.0f || device == nullptr || context == nullptr ||
        source == nullptr || eye >= kEyes)
        return source;

    /* M takes a ray in the NOW frame to the same world ray expressed in the
       frame the pixels were RENDERED in, which is where they have to be read
       from: M = R_render^-1 * R_now.
     *
       The sign switch swaps it for its inverse. There is no third possibility
       and no axis convention hiding in here - both forms are built from the
       same two quaternions by the same multiply, so the run can simply be
       asked which way round it goes. */
    const XrQuaternionf delta =
        (sign > 0.0f) ? quat_mul(quat_conj(renderOrientation), nowOrientation)
                      : quat_mul(quat_conj(nowOrientation), renderOrientation);

    const float deg = quat_angle_deg(delta);

    /* Nothing to do. Below a hundredth of a degree the resample would cost a
       full-screen pass and one bilinear tap of softness to move the image by
       less than a pixel, which is a bad trade in both directions. */
    if (deg < 0.01f)
        return source;

    if (deg > max_deg())
    {
        g_skipped.fetch_add(1, std::memory_order_relaxed);
        return source;
    }

    if (!ensure_shaders(device))
        return source;

    D3D11_TEXTURE2D_DESC desc = {};
    source->GetDesc(&desc);

    if (!ensure_output(device, eye, desc))
        return source;

    ID3D11ShaderResourceView* srv = ensure_srv(device, source);
    if (srv == nullptr)
        return source;

    Params p = {};

    float m[9] = {};
    mat_from_quat(delta, m);
    p.r0[0] = m[0]; p.r0[1] = m[1]; p.r0[2] = m[2]; p.r0[3] = 0.0f;
    p.r1[0] = m[3]; p.r1[1] = m[4]; p.r1[2] = m[5]; p.r1[3] = 0.0f;
    p.r2[0] = m[6]; p.r2[1] = m[7]; p.r2[2] = m[8]; p.r2[3] = 0.0f;

    p.tan[0] = std::tan(fov.angleLeft);
    p.tan[1] = std::tan(fov.angleRight);
    p.tan[2] = std::tan(fov.angleUp);
    p.tan[3] = std::tan(fov.angleDown);

    /* A degenerate frustum would divide by zero in the shader and fill the eye
       with NaN, which the compositor shows as garbage rather than as nothing. */
    if (!(p.tan[1] - p.tan[0] > 1e-4f) || !(p.tan[3] - p.tan[2] < -1e-4f))
        return source;

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    if (FAILED(context->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        return source;
    std::memcpy(mapped.pData, &p, sizeof(p));
    context->Unmap(g_cb, 0);

    {
        StateGuard guard(context);

        /* The source is almost certainly still bound somewhere as a render
           target from the capture that filled it; binding it as an SRV while
           that stands would silently unbind it and produce a black eye. */
        ID3D11RenderTargetView* nothing[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
        context->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, nothing, nullptr);

        D3D11_VIEWPORT vp = {};
        vp.Width    = static_cast<float>(desc.Width);
        vp.Height   = static_cast<float>(desc.Height);
        vp.MinDepth = 0.0f;
        vp.MaxDepth = 1.0f;

        context->OMSetRenderTargets(1, &g_outRtv[eye], nullptr);
        context->RSSetViewports(1, &vp);
        context->RSSetState(nullptr);
        context->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFFu);
        context->OMSetDepthStencilState(nullptr, 0);
        context->IASetInputLayout(nullptr);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(g_vs, nullptr, 0);
        context->PSSetShader(g_ps, nullptr, 0);
        context->GSSetShader(nullptr, nullptr, 0);
        context->PSSetShaderResources(0, 1, &srv);
        context->PSSetSamplers(0, 1, &g_sampler);
        context->PSSetConstantBuffers(0, 1, &g_cb);
        context->Draw(3, 0);

        /* Unbound before the guard restores the engine's own targets, so the
           source texture is free to be a render target again next frame. */
        ID3D11ShaderResourceView* none = nullptr;
        context->PSSetShaderResources(0, 1, &none);
    }

    g_applied[eye] = true;
    g_count.fetch_add(1, std::memory_order_relaxed);
    g_degSum.fetch_add(static_cast<uint32_t>(deg * 100.0f), std::memory_order_relaxed);

    uint32_t prev = g_degMax.load(std::memory_order_relaxed);
    const uint32_t cur = static_cast<uint32_t>(deg * 100.0f);
    while (cur > prev && !g_degMax.compare_exchange_weak(prev, cur, std::memory_order_relaxed))
    { }

    return g_out[eye];
}

void image_latch_report()
{
    const uint32_t n = g_count.exchange(0, std::memory_order_relaxed);
    const uint32_t sum = g_degSum.exchange(0, std::memory_order_relaxed);
    const uint32_t max = g_degMax.exchange(0, std::memory_order_relaxed);
    const uint32_t skipped = g_skipped.exchange(0, std::memory_order_relaxed);

    if (n == 0 && skipped == 0)
        return;

    BVR_INFO("Image latch: %u eye(s) rotated to the live head pose this second, "
             "mean %.2f deg, max %.2f deg%s. This is the rotation vp_patch used "
             "to apply to the geometry alone.",
             n,
             n > 0 ? (static_cast<double>(sum) / 100.0) / n : 0.0,
             static_cast<double>(max) / 100.0,
             skipped > 0 ? " (some declined as too large - see image_latch_max_deg)" : "");
}

/* How much of the overdrive survives, given how coherent the recent head motion
   has been. 1.0 for a steady turn, towards 0.0 for a shake.

   The smoothing is deliberately short - about five frames at 90 Hz. Long enough
   that a reversal shows up as cancellation, short enough that a real turn is at
   full strength within ~50 ms of starting, which is well inside the time it
   takes to notice a turn has begun. */
float coherence_gain(const XrQuaternionf& raw)
{
    static float sumX = 0.0f, sumY = 0.0f, sumZ = 0.0f;  /* smoothed VECTOR    */
    static float sumMag = 0.0f;                          /* smoothed MAGNITUDE */

    static float alpha = -1.0f;
    static float strength = -1.0f;
    if (alpha < 0.0f)
    {
        alpha = config_float("head_overdrive_coherence_alpha", 0.2f);
        if (alpha <= 0.0f || alpha > 1.0f) alpha = 0.2f;

        /* 0 disables the gate entirely and restores the plain dial, which is
           the control this needs to be judged against. */
        strength = config_float("head_overdrive_coherence", 1.0f);
        if (strength < 0.0f) strength = 0.0f;
        if (strength > 1.0f) strength = 1.0f;
    }

    /* The rotation as an axis-angle VECTOR, which is what can be averaged with
       sign. Small angles here, so the half-angle approximation is not worth the
       trouble of avoiding: the vector part of a unit quaternion already is the
       axis scaled by sin(theta/2). */
    const float vx = raw.x, vy = raw.y, vz = raw.z;
    const float mag = std::sqrt(vx * vx + vy * vy + vz * vz);

    /* UNIT DIRECTIONS, NOT RAW VECTORS.
     *
       The first version smoothed the raw vector and divided by the smoothed
       MAGNITUDE, and that conflates two different things: a change of direction
       and a change of speed. Head speed varies through any real turn, and by
       Jensen the length of a mean is below the mean of the lengths whenever the
       magnitudes differ - so a perfectly straight turn read 0.92 rather than
       1.00 and the gate quietly shaved a fifth off the dial exactly when it was
       wanted. Measured across a battle: coherence never once reached 1.00.
     *
       Averaging UNIT vectors removes speed from the question entirely. A
       constant direction at any varying speed gives 1.00; a reversal gives
       cancellation whatever the speed. sumMag is kept only as the noise gate. */
    sumMag = sumMag + alpha * (mag - sumMag);

    if (mag > 1e-7f)
    {
        const float inv = 1.0f / mag;
        sumX = sumX + alpha * (vx * inv - sumX);
        sumY = sumY + alpha * (vy * inv - sumY);
        sumZ = sumZ + alpha * (vz * inv - sumZ);
    }

    if (strength <= 0.0f)
        return 1.0f;

    /* Below the tracker's own noise floor there is nothing to be coherent
       about, and a direction derived from noise is a random unit vector. */
    if (sumMag < 1e-6f)
        return 1.0f;

    float c = std::sqrt(sumX * sumX + sumY * sumY + sumZ * sumZ);
    if (c > 1.0f) c = 1.0f;
    if (c < 0.0f) c = 0.0f;

    g_cohSum.fetch_add(static_cast<uint32_t>(c * 100.0f), std::memory_order_relaxed);
    g_cohCount.fetch_add(1, std::memory_order_relaxed);

    /* strength blends between the raw dial (0) and full gating (1). */
    return 1.0f + strength * (c - 1.0f);
}

/* --------------------------------------------------------------------------
 * Rotation vectors. A quaternion's vector part is the axis scaled by
 * sin(theta/2), so twice it is the axis-angle vector to first order - which is
 * what can be differentiated, averaged and extrapolated with a sign.
 * ----------------------------------------------------------------------- */
void quat_to_rotvec(const XrQuaternionf& q, float out[3])
{
    /* Shortest arc: a quaternion and its negation are the same rotation, and
       differencing across that seam would read a small turn as a nearly full
       one. */
    const float sign = (q.w < 0.0f) ? -1.0f : 1.0f;
    const float vx = q.x * sign, vy = q.y * sign, vz = q.z * sign;
    float w = q.w * sign;
    if (w > 1.0f) w = 1.0f;

    const float len = std::sqrt(vx * vx + vy * vy + vz * vz);
    if (len < 1e-9f)
    {
        out[0] = 2.0f * vx; out[1] = 2.0f * vy; out[2] = 2.0f * vz;
        return;
    }

    const float angle = 2.0f * std::atan2(len, w);
    const float s = angle / len;
    out[0] = vx * s; out[1] = vy * s; out[2] = vz * s;
}

XrQuaternionf rotvec_to_quat(const float v[3])
{
    const float angle = std::sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
    if (angle < 1e-9f)
        return XrQuaternionf{ 0.0f, 0.0f, 0.0f, 1.0f };

    const float half = angle * 0.5f;
    const float s = std::sin(half) / angle;
    return XrQuaternionf{ v[0] * s, v[1] * s, v[2] * s, std::cos(half) };
}

float head_overdrive()
{
    static float cached = 0.0f;
    static bool  have = false;
    if (!have)
    {
        have = true;
        cached = config_float("head_overdrive", 0.0f);

        /* CEILING RAISED FROM 2 TO 4, ON A MEASUREMENT.
         *
           Pairing the overdrive against "Pose lag by axis" second by second -
           which controls for how much the head actually moved - gives the share
           of the lag that is being cancelled:
         *
               k = 1.0   0.26 of the measured yaw lag
               k = 2.0   0.63
         *
           Linear, and 2.0 was the old ceiling, so full cancellation was not
           reachable at all. k ~= 3.2 is where the applied rotation meets the
           measured one. 4 leaves headroom above that without letting a typo
           become a whole revolution. */
        if (cached >  4.0f) cached =  4.0f;
        if (cached < -4.0f) cached = -4.0f;
    }
    return cached;
}

float head_predict_ms()
{
    static float cached = 0.0f;
    static bool  have = false;
    if (!have)
    {
        have = true;
        cached = config_float("head_predict_ms", 0.0f);
        if (cached < 0.0f)   cached = 0.0f;
        if (cached > 60.0f)  cached = 60.0f;
    }
    return cached;
}

XrQuaternionf head_predict(const XrQuaternionf& nowOrientation,
                           int64_t displayTimeNs)
{
    const float horizonMs = head_predict_ms();
    if (horizonMs <= 0.0f)
        return nowOrientation;

    static XrQuaternionf prevQ{ 0.0f, 0.0f, 0.0f, 1.0f };
    static int64_t prevT = 0;
    static float   prevW[3] = { 0.0f, 0.0f, 0.0f };   /* smoothed velocity     */
    static float   accel[3] = { 0.0f, 0.0f, 0.0f };   /* smoothed acceleration */
    static bool    havePrev = false;

    static float accelWeight = -1.0f;
    static float wAlpha = -1.0f;
    static float maxDeg = -1.0f;
    if (accelWeight < 0.0f)
    {
        accelWeight = config_float("head_predict_accel", 1.0f);
        if (accelWeight < 0.0f) accelWeight = 0.0f;
        if (accelWeight > 2.0f) accelWeight = 2.0f;

        /* Light smoothing only. Velocity has to be trusted almost immediately
           or the predictor is late for exactly the movement it exists for, and
           the coherence gate is what protects against believing a shake. */
        wAlpha = config_float("head_predict_alpha", 0.45f);
        if (wAlpha <= 0.0f || wAlpha > 1.0f) wAlpha = 0.45f;

        maxDeg = config_float("head_predict_max_deg", 6.0f);
    }

    const float dt = (prevT != 0)
                        ? static_cast<float>(displayTimeNs - prevT) * 1e-9f
                        : 0.0f;

    XrQuaternionf out = nowOrientation;

    /* A gap, a hitch or a first frame: reseed rather than differentiate across
       it. Extrapolating a velocity computed over a 500 ms stall would fling the
       world across the room. */
    if (!havePrev || dt < 1e-4f || dt > 0.1f)
    {
        havePrev = true;
        prevQ = nowOrientation;
        prevT = displayTimeNs;
        prevW[0] = prevW[1] = prevW[2] = 0.0f;
        accel[0] = accel[1] = accel[2] = 0.0f;
        return nowOrientation;
    }

    /* World-frame step between the last live pose and this one. */
    const XrQuaternionf step = quat_mul(nowOrientation, quat_conj(prevQ));
    float r[3];
    quat_to_rotvec(step, r);

    const float invDt = 1.0f / dt;
    const float wNow[3] = { r[0] * invDt, r[1] * invDt, r[2] * invDt };

    for (int i = 0; i < 3; ++i)
    {
        /* THE DERIVATIVE, NOT THE SMOOTHING STEP.
         *
           This was aNow = (wSmoothed - prevW) / dt, and wSmoothed - prevW is
           by definition alpha * (wNow - prevW) - so the acceleration came out
           scaled by alpha, under-read by 55 per cent at the default 0.45. It
           starved both things that depend on it: the onset term, which is the
           whole reason the predictor exists, and the deceleration guard, which
           fired on 3 frames a second out of 90 because w . a was rarely
           negative enough to notice a stop.
         *
           The true derivative is the NEW velocity against the SMOOTHED previous
           one, taken before prevW is advanced. */
        const float aNow = (wNow[i] - prevW[i]) * invDt;
        accel[i] = accel[i] + wAlpha * (aNow - accel[i]);
        prevW[i] = prevW[i] + wAlpha * (wNow[i] - prevW[i]);
    }

    prevQ = nowOrientation;
    prevT = displayTimeNs;

    /* w*T + 0.5*a*T^2. The acceleration term is the whole point: at the onset
       of a movement w is still near zero and a is not, so this is the only
       term that is awake when the reactive dial is blind. */
    float T = horizonMs * 0.001f;

    /* DO NOT PREDICT PAST A STOP.
     *
       Overshoot when the head STOPS is the classic failure of extrapolation and
       it is asymmetric: at an onset the velocity and the acceleration point the
       SAME way, while at a stop they OPPOSE. Shortening the horizon would fix
       the stop by giving back the onset response the predictor exists for -
       a compromise rather than a fix.
     *
       So the horizon is cut only where it would overshoot. If the head is
       decelerating, the time until the motion ceases is |w| / |a|; predicting
       beyond that is predicting movement that will not happen. Accelerating,
       the dot product is positive, nothing is cut, and the onset is untouched.
     *
       head_predict_decel_guard = 0 disables it, which is the control for
       whether this is what stops the lurch. */
    static float decelGuard = -1.0f;
    if (decelGuard < 0.0f)
        decelGuard = config_bool("head_predict_decel_guard", true) ? 1.0f : 0.0f;

    if (decelGuard > 0.0f)
    {
        const float wa = prevW[0] * accel[0] + prevW[1] * accel[1] + prevW[2] * accel[2];
        if (wa < 0.0f)
        {
            const float wMag = std::sqrt(prevW[0] * prevW[0] + prevW[1] * prevW[1] +
                                         prevW[2] * prevW[2]);
            const float aMag = std::sqrt(accel[0] * accel[0] + accel[1] * accel[1] +
                                         accel[2] * accel[2]);
            if (aMag > 1e-6f)
            {
                const float tStop = wMag / aMag;
                if (tStop < T)
                {
                    T = tStop;
                    g_predCut.fetch_add(1, std::memory_order_relaxed);
                }
            }
        }
    }

    /* THE TWO TERMS DO DIFFERENT JOBS AND WANT DIFFERENT WEIGHTS.
     *
       w * T is a SUSTAINED lead: it holds for as long as the turn continues,
       which is felt as the prediction being "too long" - the world runs ahead
       for the whole movement rather than helping it start.
     *
       0.5 * a * T^2 is a TRANSIENT: acceleration is non-zero only while the
       head is speeding up or slowing down, so it produces a kick at the onset
       and then vanishes of its own accord once the turn is steady. Short and
       sudden by construction.
     *
       Weighting them separately makes that a choice. head_predict_vel = 0
       leaves the onset kick alone with no sustained lead at all. */
    static float velWeight = -1.0f;
    if (velWeight < 0.0f)
    {
        velWeight = config_float("head_predict_vel", 1.0f);
        if (velWeight < 0.0f) velWeight = 0.0f;
        if (velWeight > 2.0f) velWeight = 2.0f;
    }

    float pred[3];
    for (int i = 0; i < 3; ++i)
        pred[i] = velWeight * prevW[i] * T + 0.5f * accelWeight * accel[i] * T * T;

    const float mag = std::sqrt(pred[0] * pred[0] + pred[1] * pred[1] + pred[2] * pred[2]);
    const float cap = maxDeg * 0.01745329f;
    if (mag > cap && mag > 1e-9f)
    {
        const float s = cap / mag;
        pred[0] *= s; pred[1] *= s; pred[2] *= s;
        g_predClamped.fetch_add(1, std::memory_order_relaxed);
    }

    g_predSum.fetch_add(static_cast<uint32_t>(mag * 57.29577951f * 100.0f),
                        std::memory_order_relaxed);
    g_predCount.fetch_add(1, std::memory_order_relaxed);

    out = quat_mul(rotvec_to_quat(pred), nowOrientation);
    return out;
}

void head_predict_report()
{
    const uint32_t n = g_predCount.exchange(0, std::memory_order_relaxed);
    const uint32_t sum = g_predSum.exchange(0, std::memory_order_relaxed);
    const uint32_t clamped = g_predClamped.exchange(0, std::memory_order_relaxed);
    if (n == 0)
        return;

    BVR_INFO("Head predictor: %u frame(s), mean %.2f deg carried forward over a "
             "%.0f ms horizon%s. This is the term that is awake at the ONSET of a "
             "movement, where the reactive overdrive is still zero.",
             n, (static_cast<double>(sum) / 100.0) / n, head_predict_ms(),
             clamped > 0 ? " (some clamped - see head_predict_max_deg)" : "");

    const uint32_t cut = g_predCut.exchange(0, std::memory_order_relaxed);
    if (cut > 0)
        BVR_INFO("Head predictor: the deceleration guard shortened the horizon on "
                 "%u frame(s) this second - that is the head SLOWING, and "
                 "predicting past a stop is what makes the world lurch and "
                 "settle back.", cut);
}

XrQuaternionf head_overdrive_delta(const XrQuaternionf& renderOrientation,
                                   const XrQuaternionf& nowOrientation)
{
    const XrQuaternionf identity{ 0.0f, 0.0f, 0.0f, 1.0f };

    const float k = head_overdrive();
    if (k == 0.0f)
        return identity;

    /* DIRECTION COHERENCE, and it is the fix TEST 78 wrote down and never built.
     *
       A single gain cannot serve both reports at once, because they are errors
       of opposite sign in different frequency bands:
     *
         a head SHAKE reverses direction between the render and the display, so
         the delta measured over that interval frequently points BACKWARDS.
         Over-rotating by it injects motion that never happened - "the world
         shakes with my head", and it gets worse as the dial goes up.
     *
         a sudden movement out of stillness has no delta yet at all, so the same
         dial contributes nothing exactly when it is wanted most.
     *
       Speed gating cannot separate them - a deliberate slow lean and a tremor
       overlap completely in speed. DIRECTION does separate them: tremor reverses
       every frame or two, deliberate motion holds its heading.
     *
       So a smoothed direction vector is compared against the smoothed magnitude
       of the instantaneous ones. For coherent motion the two agree and the ratio
       is near 1; for oscillation the vectors cancel while the magnitudes do not,
       and it falls to near 0. The overdrive is scaled by that ratio: full for a
       real turn, nothing for a vibration, and no threshold to sit on top of. */
    const XrQuaternionf raw = quat_mul(nowOrientation, quat_conj(renderOrientation));
    const float coherence = coherence_gain(raw);

    /* WORLD SPACE, not the eye's own frame: nowOrientation * conj(render).
     *
       A world-space rotation can be applied identically to both eyes and leaves
       the baseline between them untouched, which is what keeps the pair fusing.
       The eye-local form cannot - it would have to be conjugated into each eye's
       frame, and under AFR the two eyes are from different moments anyway. */
    const XrQuaternionf d = quat_mul(nowOrientation, quat_conj(renderOrientation));

    /* Scaled by angle rather than by component, so k = 0.5 really is half the
       rotation and not a shortened quaternion that has to be renormalised into
       something else. Axis-free in the sense that matters: the axis is carried
       through untouched and only the angle is multiplied, so there is no sign
       convention to get backwards inside the scaling itself. */
    float vx = d.x, vy = d.y, vz = d.z;
    const float len = std::sqrt(vx * vx + vy * vy + vz * vz);

    XrQuaternionf scaled{ 0.0f, 0.0f, 0.0f, 1.0f };
    if (len > 1e-9f)
    {
        float w = d.w;
        if (w >  1.0f) w =  1.0f;
        if (w < -1.0f) w = -1.0f;

        /* BOUNDED, so no single frame can be flung. The delta is one pipeline's
           worth of head rotation and belongs in fractions of a degree; anything
           much larger is a hitch, a dropped frame or a reference pose that was
           picked wrongly, and applying it in full is a visible pop rather than
           a crisper head. Clamped rather than skipped, because skipping is
           itself a discontinuity. */
        float angle = std::acos(w) * 2.0f * k * coherence;
        const float cap = config_float("head_overdrive_max_deg", 3.0f) * 0.01745329f;
        if (angle >  cap) angle =  cap;
        if (angle < -cap) angle = -cap;

        const float half = angle * 0.5f;
        const float s = std::sin(half) / len;

        scaled.x = vx * s;
        scaled.y = vy * s;
        scaled.z = vz * s;
        scaled.w = std::cos(half);
    }

    /* Reported so the run can say what it actually applied, rather than what
       the dial was set to. */
    const float deg = quat_angle_deg(scaled);
    g_odCount.fetch_add(1, std::memory_order_relaxed);
    g_odSum.fetch_add(static_cast<uint32_t>(deg * 100.0f), std::memory_order_relaxed);
    uint32_t prevOd = g_odMax.load(std::memory_order_relaxed);
    const uint32_t curOd = static_cast<uint32_t>(deg * 100.0f);
    while (curOd > prevOd &&
           !g_odMax.compare_exchange_weak(prevOd, curOd, std::memory_order_relaxed))
    { }

    return scaled;
}

void head_overdrive_rigid(const XrQuaternionf& delta, XrPosef& left, XrPosef& right)
{
    if (head_overdrive() == 0.0f)
        return;

    /* CLAIM A POSE BEHIND THE ONE THE PIXELS CAME FROM.
     *
       The compositor carries the image from what we submit to the true pose at
       photon time. Submit a head turned backwards by its own recent movement
       and it carries the whole frame forwards by that much extra - the
       vp_patch = 1 over-rotation, applied to every pixel at once rather than to
       the geometry alone. */
    const XrQuaternionf q = quat_conj(delta);

    const float cx = (left.position.x + right.position.x) * 0.5f;
    const float cy = (left.position.y + right.position.y) * 0.5f;
    const float cz = (left.position.z + right.position.z) * 0.5f;

    XrPosef* poses[2] = { &left, &right };
    for (int i = 0; i < 2; ++i)
    {
        XrPosef& p = *poses[i];

        p.orientation = quat_mul(q, p.orientation);

        /* v' = q * v * conj(q), written out rather than through a matrix
           because it is two vectors a frame and the matrix would be built and
           thrown away for each. */
        const float vx = p.position.x - cx;
        const float vy = p.position.y - cy;
        const float vz = p.position.z - cz;

        const float tx = 2.0f * (q.y * vz - q.z * vy);
        const float ty = 2.0f * (q.z * vx - q.x * vz);
        const float tz = 2.0f * (q.x * vy - q.y * vx);

        p.position.x = cx + vx + q.w * tx + (q.y * tz - q.z * ty);
        p.position.y = cy + vy + q.w * ty + (q.z * tx - q.x * tz);
        p.position.z = cz + vz + q.w * tz + (q.x * ty - q.y * tx);
    }
}

void head_overdrive_report()
{
    const uint32_t n = g_odCount.exchange(0, std::memory_order_relaxed);
    const uint32_t sum = g_odSum.exchange(0, std::memory_order_relaxed);
    const uint32_t max = g_odMax.exchange(0, std::memory_order_relaxed);

    if (n == 0)
        return;

    const uint32_t cn = g_cohCount.exchange(0, std::memory_order_relaxed);
    const uint32_t cs = g_cohSum.exchange(0, std::memory_order_relaxed);

    BVR_INFO("Head overdrive: %u frame(s) biased this second, mean %.2f deg, "
             "max %.2f deg, coherence %.2f. Coherence is 1.00 for a steady turn "
             "and falls towards 0 for a shake, which is the share of the dial "
             "that survives - a reversal between the render and the display "
             "makes the measured delta point the wrong way, and over-rotating "
             "by it is what puts the world ON the head.",
             n, (static_cast<double>(sum) / 100.0) / n,
             static_cast<double>(max) / 100.0,
             cn > 0 ? (static_cast<double>(cs) / 100.0) / cn : 1.0);
}

void image_latch_release()
{
    for (uint32_t i = 0; i < kSrvCacheSize; ++i)
    {
        if (g_srvs[i].srv != nullptr) g_srvs[i].srv->Release();
        g_srvs[i].srv = nullptr;
        g_srvs[i].texture = nullptr;
    }
    for (uint32_t eye = 0; eye < kEyes; ++eye)
    {
        if (g_outRtv[eye] != nullptr) { g_outRtv[eye]->Release(); g_outRtv[eye] = nullptr; }
        if (g_out[eye]    != nullptr) { g_out[eye]->Release();    g_out[eye]    = nullptr; }
        g_applied[eye] = false;
    }
    if (g_cb      != nullptr) { g_cb->Release();      g_cb      = nullptr; }
    if (g_sampler != nullptr) { g_sampler->Release(); g_sampler = nullptr; }
    if (g_ps      != nullptr) { g_ps->Release();      g_ps      = nullptr; }
    if (g_vs      != nullptr) { g_vs->Release();      g_vs      = nullptr; }
    g_shaderFailed = false;
}

} // namespace bvr
