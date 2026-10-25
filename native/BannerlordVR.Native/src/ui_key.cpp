#include "ui_key.h"
#include "bvr_config.h"
#include "bvr_log.h"
#include "ui_quad_math.h"

#include <d3dcompiler.h>

#include <shlobj.h>

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <vector>

namespace bvr {
namespace {

/* TEST 602: the compute slots these passes use are handed back as the engine
   left them, not cleared. rgl skips binding a compute shader it believes is
   still bound, so clearing made its next dispatch after our Present run with no
   shader. In a frame with no other compute - loading, menus - that dispatch was
   the banner BC1 compression (dxt1_compress, t0 -> u2), which then wrote
   nothing, and an all-zero BC1 block is opaque black: the black banners of
   TESTs 597-601. Ours are unbound first, as vr_sharpen does. */
struct CsGuard
{
    ID3D11DeviceContext* c;
    ID3D11ComputeShader* cs = nullptr;
    ID3D11Buffer* cb = nullptr;
    ID3D11ShaderResourceView* srv[2] = {};
    ID3D11UnorderedAccessView* uav = nullptr;
    ID3D11SamplerState* smp = nullptr;

    explicit CsGuard(ID3D11DeviceContext* ctx) : c(ctx)
    {
        c->CSGetShader(&cs, nullptr, nullptr);
        c->CSGetConstantBuffers(0, 1, &cb);
        c->CSGetShaderResources(0, 2, srv);
        c->CSGetUnorderedAccessViews(0, 1, &uav);
        c->CSGetSamplers(0, 1, &smp);
    }

    ~CsGuard()
    {
        ID3D11UnorderedAccessView* noUav = nullptr;
        ID3D11ShaderResourceView* noSrv[2] = {};
        const UINT keep = static_cast<UINT>(-1);
        c->CSSetUnorderedAccessViews(0, 1, &noUav, &keep);
        c->CSSetShaderResources(0, 2, noSrv);

        c->CSSetShader(cs, nullptr, 0);
        c->CSSetConstantBuffers(0, 1, &cb);
        c->CSSetSamplers(0, 1, &smp);
        c->CSSetShaderResources(0, 2, srv);
        c->CSSetUnorderedAccessViews(0, 1, &uav, &keep);

        if (cs) cs->Release();
        if (cb) cb->Release();
        for (ID3D11ShaderResourceView* s : srv)
            if (s) s->Release();
        if (uav) uav->Release();
        if (smp) smp->Release();
    }

    CsGuard(const CsGuard&) = delete;
    CsGuard& operator=(const CsGuard&) = delete;
};

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
    float  PunchCount;
    float  UseAlpha;    // TEST 590: the game's own alpha is coverage (see the probe)
    float2 Pad;
    float4 Punch[8];    // x, y, w, h in source pixels: moved to a wrist (TEST 377)
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

    // TEST 590: brightness cannot tell a BLACK part of a window (a video's dark
    // frame, a dark panel) from no window at all, so those were keyed out and the
    // world showed through them. Where the game writes real coverage into alpha,
    // that is the answer; brightness still keeps anything lit.
    if (UseAlpha > 0.5)
        a = max(a, c.a);

    // Widgets shown on a wrist are taken off the head panel, so they are not
    // in both places. The COLOUR goes too (TEST 377h): the compositor blends this
    // layer as premultiplied, adding the colour and using alpha only to dim what
    // is behind, so alpha 0 alone left a transparent ghost of the widget.
    float2 p = float2(tid.xy) + 0.5;
    for (uint i = 0; i < (uint)PunchCount; ++i)
    {
        if (all(p >= Punch[i].xy) && all(p < Punch[i].xy + Punch[i].zw))
        {
            c.rgb = float3(0.0, 0.0, 0.0);
            a = 0.0;
        }
    }

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
    float2 UiSize;   // the interface's own pixels, which are no longer the monitor's
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

    // TEST 299: the interface is no longer necessarily the monitor's size. When
    // it is drawn into our own dense target it covers the same rectangle in far
    // more pixels, so the monitor samples it by ratio rather than one to one.
    // UiSize == DstSize gives back exactly the old Load.
    int2 up = int2((float2(tid.xy) + 0.5) * (UiSize / DstSize));
    up = clamp(up, int2(0, 0), int2(UiSize) - int2(1, 1));
    float4 u = Ui.Load(int3(up, 0));
    c = lerp(c, u.rgb, u.a);

    Dst[tid.xy] = float4(c, 1.0);
}
)HLSL";

/* ---------------------------------------------------------------------------
 * The wrist HUD (TEST 377).
 *
 * Crops up to four rectangles of the interface and stacks them top-down into a
 * wrist texture, keyed exactly like the head panel and faded as a whole. It
 * reads the UNKEYED copy because the key pass punches these same rectangles out
 * of the keyed one. Bilinear, which is exact at the 1:1 scale it normally runs
 * at and only filters when the regions had to shrink to fit.
 * ------------------------------------------------------------------------- */
const char* kWristShader = R"HLSL(
Texture2D<float4>   Src : register(t0);
RWTexture2D<float4> Dst : register(u0);
SamplerState        Lin : register(s0);

cbuffer Params : register(b0)
{
    float2 DstSize;
    float2 SrcSize;
    float  Threshold;
    float  Softness;
    float  Fade;
    float  Count;
    float4 SrcRect[4];   // source pixels
    float4 DstRect[4];   // destination pixels
    float  UseAlpha;     // TEST 590, as in the key
    float3 Pad;
};

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= (uint)DstSize.x || tid.y >= (uint)DstSize.y)
        return;

    float2 p = float2(tid.xy) + 0.5;
    float4 o = float4(0, 0, 0, 0);

    for (uint i = 0; i < (uint)Count; ++i)
    {
        if (all(p >= DstRect[i].xy) && all(p < DstRect[i].xy + DstRect[i].zw))
        {
            float2 s = SrcRect[i].xy + (p - DstRect[i].xy) * (SrcRect[i].zw / DstRect[i].zw);
            float4 c = Src.SampleLevel(Lin, s / SrcSize, 0);
            float lit = max(c.r, max(c.g, c.b));
            float a = saturate((lit - Threshold) / max(Softness, 1e-4));
            if (UseAlpha > 0.5)
                a = max(a, c.a);
            o = float4(c.rgb, a * Fade);
        }
    }

    Dst[tid.xy] = o;
}
)HLSL";

struct Params
{
    float size[2];
    float threshold;
    float softness;
    float punchCount;
    float useAlpha;
    float pad[2];
    float punch[8][4];
};

struct WristParams
{
    float dstSize[2];
    float srcSize[2];
    float threshold;
    float softness;
    float fade;
    float count;
    float srcRect[4][4];
    float dstRect[4][4];
    float useAlpha;
    float pad[3];
};

/* ---------------------------------------------------------------------------
 * TEST 590: IS THE GAME'S ALPHA REAL COVERAGE?
 *
 * Nobody had ever looked. The interface is drawn over black, so if the engine
 * blends alpha as well as colour, an empty pixel is alpha 0 and a widget -
 * black or not - is alpha 1, and keying by that is exact. If it does not (alpha
 * left at the clear value, 1 or 0), keying by it would paint a black sheet over
 * the battle or erase the HUD. So every 30th keyed frame one pixel in 64 is
 * sorted into four bins, read back a few frames later without waiting, and
 * alpha is used only while the answer is yes:
 *
 *   0 dark, alpha < 0.5    1 dark, alpha >= 0.5
 *   2 lit, alpha >= 0.5    3 lit, alpha < 0.5
 *
 * Yes = the lit pixels carry alpha (bin 2 >= 90% of lit) AND the empty screen
 * does not (bin 0 >= 15% of dark). ui_key_alpha 0 never, 1 always, -1 probe.
 * ------------------------------------------------------------------------- */
const char* kProbeShader = R"HLSL(
Texture2D<float4>   Src    : register(t0);
RWByteAddressBuffer Counts : register(u0);

cbuffer Params : register(b0)
{
    float2 Size;
    float  Threshold;
    float  Softness;
};

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint2 p = tid.xy * 8;
    if (p.x >= (uint)Size.x || p.y >= (uint)Size.y)
        return;

    float4 c = Src.Load(int3(p, 0));
    float lit = max(c.r, max(c.g, c.b));
    uint bin;
    if (lit < Threshold)
        bin = c.a < 0.5 ? 0 : 1;
    else if (lit >= Threshold + Softness)
        bin = c.a >= 0.5 ? 2 : 3;
    else
        return;
    Counts.InterlockedAdd(bin * 4, 1);
}
)HLSL";

struct MirrorParams
{
    float dstSize[2];
    float eyeSize[2];
    float scale[2];
    float offset[2];
    float uiSize[2];
    float pad[2];
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

// --- TEST 590: the alpha probe ---------------------------------------------

ID3D11ComputeShader*       g_probeShader = nullptr;
ID3D11Buffer*              g_probeCb = nullptr;
ID3D11Buffer*              g_probeBuf = nullptr;
ID3D11UnorderedAccessView* g_probeUav = nullptr;
ID3D11Buffer*              g_probeStaging = nullptr;
bool     g_probeFailed = false;
bool     g_probePending = false;
uint32_t g_probeTick = 0;
int      g_probeStreak = 0;
int      g_probeLogged = 0;
bool     g_alphaValid = false;
bool      g_dumpWanted = false;   /* TEST 593, see dump_ui */
int       g_dumps = 0;
ULONGLONG g_lastDump = 0;

/* ui_key_alpha: 0 never, 1 always, anything else (default -1) the probe decides. */
int alpha_setting()
{
    static const int v = static_cast<int>(std::lround(config_float("ui_key_alpha", -1.0f)));
    return v;
}

bool use_alpha()
{
    const int s = alpha_setting();
    return s == 0 ? false : s == 1 ? true : g_alphaValid;
}

bool ensure_probe(ID3D11Device* device)
{
    if (g_probeStaging != nullptr)
        return true;
    if (g_probeFailed)
        return false;
    g_probeFailed = true;   /* cleared at the end */

    ID3DBlob* code = nullptr;
    ID3DBlob* errors = nullptr;
    const HRESULT hr = D3DCompile(kProbeShader, std::strlen(kProbeShader), "ui_alpha_probe",
                                  nullptr, nullptr, "main", "cs_5_0", 0, 0, &code, &errors);
    if (errors != nullptr)
        errors->Release();
    if (FAILED(hr) || code == nullptr)
    {
        if (code != nullptr) code->Release();
        BVR_WARN("UI key (TEST 590): the alpha probe would not compile; keying by brightness.");
        return false;
    }
    const HRESULT sh = device->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(),
                                                   nullptr, &g_probeShader);
    code->Release();
    if (FAILED(sh))
        return false;

    D3D11_BUFFER_DESC cd = {};
    cd.ByteWidth = 16;
    cd.Usage = D3D11_USAGE_DYNAMIC;
    cd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    cd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    if (FAILED(device->CreateBuffer(&cd, nullptr, &g_probeCb)))
        return false;

    D3D11_BUFFER_DESC bd = {};
    bd.ByteWidth = 16;
    bd.Usage = D3D11_USAGE_DEFAULT;
    bd.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
    bd.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS;
    if (FAILED(device->CreateBuffer(&bd, nullptr, &g_probeBuf)))
        return false;

    D3D11_UNORDERED_ACCESS_VIEW_DESC ud = {};
    ud.Format = DXGI_FORMAT_R32_TYPELESS;
    ud.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
    ud.Buffer.NumElements = 4;
    ud.Buffer.Flags = D3D11_BUFFER_UAV_FLAG_RAW;
    if (FAILED(device->CreateUnorderedAccessView(g_probeBuf, &ud, &g_probeUav)))
        return false;

    D3D11_BUFFER_DESC rd = {};
    rd.ByteWidth = 16;
    rd.Usage = D3D11_USAGE_STAGING;
    rd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    if (FAILED(device->CreateBuffer(&rd, nullptr, &g_probeStaging)))
        return false;

    g_probeFailed = false;
    return true;
}

/* The last probe's counts, if the GPU has finished with them - never waits. */
void probe_read(ID3D11DeviceContext* context)
{
    if (!g_probePending)
        return;

    D3D11_MAPPED_SUBRESOURCE m = {};
    if (FAILED(context->Map(g_probeStaging, 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &m)))
        return;   /* still drawing; next frame */
    uint32_t c[4];
    std::memcpy(c, m.pData, sizeof(c));
    context->Unmap(g_probeStaging, 0);
    g_probePending = false;

    const uint32_t dark = c[0] + c[1];
    const uint32_t lit = c[2] + c[3];
    if (dark < 100 || lit < 100)
        return;   /* not enough interface or not enough empty screen to say */

    const bool yes = c[2] >= lit * 9 / 10 && c[0] >= dark * 15 / 100;
    const bool before = g_alphaValid;

    /* TEST 593: a quarter of the whole image opaque and dark - log it, and take
       a picture of it (at most three, a minute apart). */
    if (g_alphaValid && c[1] * 4 > dark + lit && g_dumps < 3 && GetTickCount64() - g_lastDump > 60000)
    {
        g_lastDump = GetTickCount64();
        g_dumpWanted = true;
        BVR_WARN("UI key (TEST 593): %u of %u sampled pixels are opaque and dark - a black area in the "
                 "interface image; saving a picture of it.", c[1], dark + lit);
    }

    /* Three conclusive yeses to start using it; only alpha missing from the lit
       pixels themselves stops it again - a dark full-screen backdrop (a popup's
       dim) is exactly what alpha keying is for, and must not switch it off. */
    if (!g_alphaValid)
    {
        g_probeStreak = yes ? g_probeStreak + 1 : 0;
        if (g_probeStreak >= 3)
            g_alphaValid = true;
    }
    else if (c[3] >= lit / 2)
    {
        g_alphaValid = false;
        g_probeStreak = 0;
    }

    if (before != g_alphaValid || g_probeLogged < 2)
    {
        ++g_probeLogged;
        BVR_INFO("UI key (TEST 590): alpha probe - empty-looking pixels %u (%u clear, %u covered), lit %u "
                 "(%u covered, %u clear). The game's alpha %s coverage%s",
                 dark, c[0], c[1], lit, c[2], c[3], yes ? "reads as" : "does NOT read as",
                 g_alphaValid ? "; keying by it now - dark parts of windows stay solid."
                              : (alpha_setting() == 1 ? "; ui_key_alpha = 1 forces it anyway."
                                                      : "; keying by brightness."));
    }
}

/* TEST 593: a picture of the interface image when a large part of it turns
   opaque BLACK - the "black box" after leaving the smithy, which brightness
   keying would have hidden. Composited over magenta at half size, so clear
   reads magenta and opaque black reads black. At most three a session. */
void dump_ui(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Texture2D* src)
{
    D3D11_TEXTURE2D_DESC d = {};
    src->GetDesc(&d);
    D3D11_TEXTURE2D_DESC sd = d;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.BindFlags = 0;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    sd.MiscFlags = 0;
    ID3D11Texture2D* st = nullptr;
    if (FAILED(device->CreateTexture2D(&sd, nullptr, &st)))
        return;
    context->CopyResource(st, src);

    D3D11_MAPPED_SUBRESOURCE m = {};
    if (FAILED(context->Map(st, 0, D3D11_MAP_READ, 0, &m)))   /* one stall, once */
    {
        st->Release();
        return;
    }

    const bool bgra = d.Format == DXGI_FORMAT_B8G8R8A8_TYPELESS || d.Format == DXGI_FORMAT_B8G8R8A8_UNORM ||
                      d.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
    const UINT w = d.Width / 2, h = d.Height / 2;
    const UINT row = (w * 3 + 3) & ~3u;
    std::vector<uint8_t> px(static_cast<size_t>(row) * h);
    for (UINT y = 0; y < h; ++y)
    {
        const uint8_t* in = static_cast<const uint8_t*>(m.pData) + static_cast<size_t>(m.RowPitch) * (y * 2);
        uint8_t* out = px.data() + static_cast<size_t>(row) * (h - 1 - y);   /* BMP rows bottom-up */
        for (UINT x = 0; x < w; ++x)
        {
            const uint8_t* p = in + x * 8;
            const uint8_t r = bgra ? p[2] : p[0], g = p[1], b = bgra ? p[0] : p[2], a = p[3];
            const int bg = 255 - a;   /* premultiplied over magenta */
            out[x * 3 + 0] = static_cast<uint8_t>(std::min(255, b + bg));
            out[x * 3 + 1] = g;
            out[x * 3 + 2] = static_cast<uint8_t>(std::min(255, r + bg));
        }
    }
    context->Unmap(st, 0);
    st->Release();

    wchar_t docs[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_MYDOCUMENTS, nullptr, 0, docs)))
        return;
    wchar_t path[MAX_PATH] = {};
    _snwprintf_s(path, _TRUNCATE, L"%s\\Mount and Blade II Bannerlord\\Logs\\BVR_UI_%d.bmp", docs, g_dumps);
    FILE* f = nullptr;
    if (_wfopen_s(&f, path, L"wb") != 0 || f == nullptr)
        return;
    const uint32_t size = static_cast<uint32_t>(54 + px.size());
    uint8_t hdr[54] = { 'B', 'M' };
    auto put32 = [&](int at, uint32_t v) { std::memcpy(hdr + at, &v, 4); };
    put32(2, size);
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
    BVR_WARN("UI key (TEST 593): wrote Logs\\BVR_UI_%d.bmp (%ux%u, interface over magenta - black there is "
             "opaque black in the interface image).", g_dumps, w, h);
    ++g_dumps;
}

void probe_issue(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11ShaderResourceView* src,
                 float w, float h, float thr, float soft)
{
    if (g_probePending || (g_probeTick++ % 30) != 0 || !ensure_probe(device))
        return;

    const float params[4] = { w, h, thr, soft };
    D3D11_MAPPED_SUBRESOURCE m = {};
    if (FAILED(context->Map(g_probeCb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
        return;
    std::memcpy(m.pData, params, sizeof(params));
    context->Unmap(g_probeCb, 0);

    const UINT zero[4] = { 0, 0, 0, 0 };
    context->ClearUnorderedAccessViewUint(g_probeUav, zero);
    {
        CsGuard guard(context);
        context->CSSetShader(g_probeShader, nullptr, 0);
        context->CSSetShaderResources(0, 1, &src);
        context->CSSetUnorderedAccessViews(0, 1, &g_probeUav, nullptr);
        context->CSSetConstantBuffers(0, 1, &g_probeCb);
        const UINT gx = (static_cast<UINT>(w) / 8 + 7) / 8, gy = (static_cast<UINT>(h) / 8 + 7) / 8;
        context->Dispatch(gx, gy, 1);
    }

    context->CopyResource(g_probeStaging, g_probeBuf);
    g_probePending = true;
}

void probe_release()
{
    if (g_probeStaging != nullptr) { g_probeStaging->Release(); g_probeStaging = nullptr; }
    if (g_probeUav != nullptr)     { g_probeUav->Release();     g_probeUav = nullptr; }
    if (g_probeBuf != nullptr)     { g_probeBuf->Release();     g_probeBuf = nullptr; }
    if (g_probeCb != nullptr)      { g_probeCb->Release();      g_probeCb = nullptr; }
    if (g_probeShader != nullptr)  { g_probeShader->Release();  g_probeShader = nullptr; }
    g_probePending = false;
    g_probeFailed = false;
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

// --- wrist resources (TEST 377) --------------------------------------------

ID3D11ComputeShader*       g_wristShader = nullptr;
ID3D11Buffer*              g_wristCb = nullptr;
ID3D11SamplerState*        g_wristSampler = nullptr;
ID3D11UnorderedAccessView* g_wristUav[2] = {};
ID3D11Texture2D*           g_wristUavFor[2] = {};
/* TEST 531: each hand reads its own source - the left palette may come from the
   map bar's own target while the right one is cropped from the interface. */
ID3D11ShaderResourceView*  g_wristSrv[2] = {};
ID3D11Texture2D*           g_wristSrvFor[2] = {};
bool g_wristFailed = false;

bool ensure_wrist_shader(ID3D11Device* device)
{
    if (g_wristShader != nullptr)
        return true;
    if (g_wristFailed)
        return false;

    g_wristFailed = true;   /* cleared at the end; every early return is a failure */

    ID3DBlob* code = nullptr;
    ID3DBlob* errors = nullptr;
    const HRESULT hr = D3DCompile(kWristShader, std::strlen(kWristShader), "ui_wrist",
                                  nullptr, nullptr, "main", "cs_5_0", 0, 0, &code, &errors);
    if (FAILED(hr) || code == nullptr)
    {
        BVR_ERR("Wrist HUD: shader would not compile (0x%08X)%s%s", static_cast<unsigned>(hr),
                errors != nullptr ? ": " : "",
                errors != nullptr ? static_cast<const char*>(errors->GetBufferPointer()) : "");
        if (errors != nullptr) errors->Release();
        if (code != nullptr) code->Release();
        return false;
    }
    if (errors != nullptr)
        errors->Release();

    const HRESULT sh = device->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(),
                                                   nullptr, &g_wristShader);
    code->Release();
    if (FAILED(sh))
    {
        BVR_ERR("Wrist HUD: CreateComputeShader failed (0x%08X).", static_cast<unsigned>(sh));
        return false;
    }

    D3D11_BUFFER_DESC bd = {};
    bd.ByteWidth = sizeof(WristParams);
    bd.Usage = D3D11_USAGE_DYNAMIC;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    if (FAILED(device->CreateBuffer(&bd, nullptr, &g_wristCb)))
    {
        BVR_ERR("Wrist HUD: could not allocate the constant buffer.");
        return false;
    }

    D3D11_SAMPLER_DESC sd = {};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    if (FAILED(device->CreateSamplerState(&sd, &g_wristSampler)))
    {
        BVR_ERR("Wrist HUD: could not create the sampler.");
        return false;
    }

    g_wristFailed = false;
    return true;
}

bool ensure_wrist_uav(ID3D11Device* device, ID3D11Texture2D* dst, int hand)
{
    if (g_wristUav[hand] != nullptr && g_wristUavFor[hand] == dst)
        return true;

    if (g_wristUav[hand] != nullptr)
    {
        g_wristUav[hand]->Release();
        g_wristUav[hand] = nullptr;
    }

    D3D11_TEXTURE2D_DESC d = {};
    dst->GetDesc(&d);

    D3D11_UNORDERED_ACCESS_VIEW_DESC ud = {};
    ud.Format = non_srgb(d.Format);
    ud.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
    if (FAILED(device->CreateUnorderedAccessView(dst, &ud, &g_wristUav[hand])))
    {
        BVR_WARN("Wrist HUD: the wrist texture (format %d) will not take a UAV.",
                 static_cast<int>(d.Format));
        g_wristFailed = true;
        return false;
    }

    g_wristUavFor[hand] = dst;
    return true;
}

/* ---------------------------------------------------------------------------
 * TEST 535: the menu screen drawn INTO the eye images.
 *
 * A menu frame used to be one quad layer and nothing else, which Virtual
 * Desktop composes and re-projects itself - and there it dragged against the
 * head, though its pose never changed. Battle frames are images rendered from
 * the eye poses we submit with them, and those hold still. So each eye pixel
 * here casts its ray from the eye, through the pixel's tangent, at the screen's
 * fixed rectangle in the room and samples the menu image where it lands; black
 * elsewhere. Raw values in, raw values out (no sRGB conversion either way),
 * exactly as the eye copies are.
 * ------------------------------------------------------------------------- */
const char* kFlatEyeShader = R"HLSL(
Texture2D<float4>   Src : register(t0);
Texture2D<float4>   Base : register(t1);   // TEST 635, BLEND only
RWTexture2D<float4> Dst : register(u0);
SamplerState        Lin : register(s0);

cbuffer Params : register(b0)
{
    float4 EyePos;
    float4 AxX, AxY, AxZ;   // the eye's axes in the room
    float4 Tan;             // left, right, up, down tangents
    float4 QC, QX, QY, QN;  // screen centre, right/width, up/height, normal
    float4 Size;            // dst w, h
    float4 Uv;              // TEST 635: src sub-rect u0, v0, du, dv
};

// Where the ray through eye pixel `pix` meets the screen, in its image's 0..1.
bool Hit(float2 pix, out float2 q)
{
    float2 uv = pix / Size.xy;
    float3 v = float3(lerp(Tan.x, Tan.y, uv.x), lerp(Tan.z, Tan.w, uv.y), -1.0);
    float3 d = v.x * AxX.xyz + v.y * AxY.xyz + v.z * AxZ.xyz;
    q = float2(0, 0);
    float den = dot(d, QN.xyz);
    if (abs(den) < 1e-6)
        return false;
    float t = dot(QC.xyz - EyePos.xyz, QN.xyz) / den;
    if (t <= 0)
        return false;
    float3 p = EyePos.xyz + d * t - QC.xyz;
    q = float2(dot(p, QX.xyz) + 0.5, 0.5 - dot(p, QY.xyz));
    return true;
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)Size.x || id.y >= (uint)Size.y)
        return;

    float2 pix = float2(id.xy) + 0.5;
#ifdef BLEND
    float4 o = Base.Load(int3(id.xy, 0));
#else
    float4 o = float4(0, 0, 0, 1);
#endif
    float2 q;
    if (Hit(pix, q) && all(q >= 0) && all(q <= 1))
    {
        // TEST 538: the pixel's real footprint on the screen (the next pixel
        // across and down, worked out the same way), so the sampler picks the
        // mip and the anisotropy a compositor would - plain bilinear on a
        // squeezed image was "grainy and noisy".
        float2 qx, qy;
        float2 gx = Hit(pix + float2(1, 0), qx) ? qx - q : float2(0, 0);
        float2 gy = Hit(pix + float2(0, 1), qy) ? qy - q : float2(0, 0);
        float4 c = Src.SampleGrad(Lin, Uv.xy + q * Uv.zw, gx * Uv.zw, gy * Uv.zw);
#ifdef BLEND
        // TEST 635: the quad layer is premultiplied (no UNPREMULTIPLIED bit).
        o = float4(o.rgb * (1.0 - c.a) + c.rgb, o.a);
#else
        o = float4(c.rgb, 1);
#endif
    }
    Dst[id.xy] = o;
}
)HLSL";

ID3D11ComputeShader*       g_flatShader = nullptr;
ID3D11Buffer*              g_flatCb = nullptr;
ID3D11SamplerState*        g_flatSampler = nullptr;
ID3D11ShaderResourceView*  g_flatSrv = nullptr;
ID3D11Texture2D*           g_flatSrvFor = nullptr;
ID3D11UnorderedAccessView* g_flatUav[2] = {};
ID3D11Texture2D*           g_flatUavFor[2] = {};
bool                       g_flatFailed = false;

ID3D11ComputeShader*       g_lblShader = nullptr;
bool                       g_lblFailed = false;
ID3D11ShaderResourceView*  g_lblUiSrv[2] = {};
ID3D11Texture2D*           g_lblUiFor[2] = {};
ID3D11ShaderResourceView*  g_lblBaseSrv[2] = {};
ID3D11Texture2D*           g_lblBaseFor[2] = {};
ID3D11UnorderedAccessView* g_lblUav[2] = {};
ID3D11Texture2D*           g_lblUavFor[2] = {};

bool ensure_flat_shader(ID3D11Device* device)
{
    if (g_flatShader != nullptr)
        return true;
    if (g_flatFailed)
        return false;
    g_flatFailed = true;   /* cleared at the end */

    ID3DBlob* code = nullptr;
    ID3DBlob* errors = nullptr;
    const HRESULT hr = D3DCompile(kFlatEyeShader, std::strlen(kFlatEyeShader), "ui_flat_eye",
                                  nullptr, nullptr, "main", "cs_5_0", 0, 0, &code, &errors);
    if (FAILED(hr) || code == nullptr)
    {
        BVR_ERR("Flat screen (TEST 535): shader would not compile (0x%08X)%s%s",
                static_cast<unsigned>(hr), errors != nullptr ? ": " : "",
                errors != nullptr ? static_cast<const char*>(errors->GetBufferPointer()) : "");
        if (errors != nullptr) errors->Release();
        if (code != nullptr) code->Release();
        return false;
    }
    if (errors != nullptr)
        errors->Release();

    const HRESULT sh = device->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(),
                                                   nullptr, &g_flatShader);
    code->Release();
    if (FAILED(sh))
        return false;

    D3D11_BUFFER_DESC bd = {};
    bd.ByteWidth = sizeof(FlatEyeParams);
    bd.Usage = D3D11_USAGE_DYNAMIC;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    if (FAILED(device->CreateBuffer(&bd, nullptr, &g_flatCb)))
        return false;

    D3D11_SAMPLER_DESC sd = {};
    sd.Filter = D3D11_FILTER_ANISOTROPIC;          /* TEST 538 */
    sd.MaxAnisotropy = 16;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    if (FAILED(device->CreateSamplerState(&sd, &g_flatSampler)))
        return false;

    g_flatFailed = false;
    return true;
}

} // namespace

bool ui_key_apply(ID3D11Device* device, ID3D11DeviceContext* context,
                  ID3D11Texture2D* src, ID3D11Texture2D* dst,
                  const float (*punch)[4], int punchCount)
{
    if (device == nullptr || context == nullptr || src == nullptr || dst == nullptr)
        return false;

    if (g_failed)
        return false;

    if (!ensure_shader(device) || !ensure_srv(device, src) || !ensure_uav(device, dst))
        return false;

    D3D11_TEXTURE2D_DESC sd = {};
    src->GetDesc(&sd);

    probe_read(context);   /* TEST 590: last probe's verdict, if it is in */

    D3D11_MAPPED_SUBRESOURCE m = {};
    if (FAILED(context->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
        return false;

    Params p = {};
    p.size[0] = static_cast<float>(sd.Width);
    p.size[1] = static_cast<float>(sd.Height);
    p.threshold = threshold();
    p.softness = softness();
    p.useAlpha = use_alpha() ? 1.0f : 0.0f;
    if (punch != nullptr && punchCount > 0)
    {
        const int n = punchCount > 8 ? 8 : punchCount;
        p.punchCount = static_cast<float>(n);
        std::memcpy(p.punch, punch, sizeof(float) * 4 * n);
    }
    std::memcpy(m.pData, &p, sizeof(p));
    context->Unmap(g_cb, 0);

    /* The engine owns this context and will keep using it the moment we hand it
       back, so the guard puts its compute slots back as they were (TEST 602). A
       stray UAV of ours would block the next use of that texture as a render
       target; a cleared shader made the engine's next dispatch a no-op. */
    {
        CsGuard guard(context);
        context->CSSetShader(g_shader, nullptr, 0);
        context->CSSetShaderResources(0, 1, &g_srv);
        context->CSSetUnorderedAccessViews(0, 1, &g_uav, nullptr);
        context->CSSetConstantBuffers(0, 1, &g_cb);

        context->Dispatch((sd.Width + 7) / 8, (sd.Height + 7) / 8, 1);
    }

    /* TEST 590: the image BEFORE the punch (the source), so the hands' regions
       count too. */
    if (alpha_setting() != 0 && alpha_setting() != 1)
        probe_issue(device, context, g_srv, p.size[0], p.size[1], p.threshold, p.softness);

    if (g_dumpWanted)
    {
        g_dumpWanted = false;
        if (sd.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS || sd.Format == DXGI_FORMAT_R8G8B8A8_UNORM ||
            sd.Format == DXGI_FORMAT_B8G8R8A8_TYPELESS || sd.Format == DXGI_FORMAT_B8G8R8A8_UNORM)
            dump_ui(device, context, src);
    }

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
    D3D11_TEXTURE2D_DESC ud = {};
    eye->GetDesc(&ed);
    dst->GetDesc(&dd);
    ui->GetDesc(&ud);

    if (ed.Width == 0 || ed.Height == 0 || dd.Width == 0 || dd.Height == 0 ||
        ud.Width == 0 || ud.Height == 0)
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

    p.uiSize[0] = static_cast<float>(ud.Width);
    p.uiSize[1] = static_cast<float>(ud.Height);

    D3D11_MAPPED_SUBRESOURCE m = {};
    if (FAILED(context->Map(g_mirrorCb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
        return false;
    std::memcpy(m.pData, &p, sizeof(p));
    context->Unmap(g_mirrorCb, 0);

    ID3D11ShaderResourceView* srvs[2] = { g_mirrorEyeSrv, g_mirrorUiSrv };

    {
        CsGuard guard(context);
        context->CSSetShader(g_mirrorShader, nullptr, 0);
        context->CSSetShaderResources(0, 2, srvs);
        context->CSSetUnorderedAccessViews(0, 1, &g_mirrorUav, nullptr);
        context->CSSetConstantBuffers(0, 1, &g_mirrorCb);

        context->Dispatch((dd.Width + 7) / 8, (dd.Height + 7) / 8, 1);
    }

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

bool ui_wrist_blit(ID3D11Device* device, ID3D11DeviceContext* context,
                   ID3D11Texture2D* src, ID3D11Texture2D* dst, int hand,
                   const float (*rects)[4], int count, float fade, float maxScale,
                   float pixelAspect)
{
    if (device == nullptr || context == nullptr || src == nullptr || dst == nullptr ||
        hand < 0 || hand > 1 || rects == nullptr || count <= 0)
        return false;

    if (g_failed || g_wristFailed)
        return false;

    /* Each hand's source has its own cached view (TEST 531). */
    if (!ensure_shader(device) || !ensure_wrist_shader(device) ||
        !ensure_wrist_uav(device, dst, hand))
        return false;

    if (g_wristSrv[hand] == nullptr || g_wristSrvFor[hand] != src)
    {
        if (g_wristSrv[hand] != nullptr) { g_wristSrv[hand]->Release(); g_wristSrv[hand] = nullptr; }
        D3D11_TEXTURE2D_DESC d = {};
        src->GetDesc(&d);
        D3D11_SHADER_RESOURCE_VIEW_DESC vd = {};
        vd.Format = non_srgb(d.Format);
        vd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        vd.Texture2D.MipLevels = 1;
        if (FAILED(device->CreateShaderResourceView(src, &vd, &g_wristSrv[hand])))
        {
            BVR_WARN("Wrist HUD: a wrist source (format %d) cannot be sampled.", static_cast<int>(d.Format));
            g_wristSrvFor[hand] = nullptr;
            return false;
        }
        g_wristSrvFor[hand] = src;
    }

    D3D11_TEXTURE2D_DESC sd = {};
    D3D11_TEXTURE2D_DESC dd = {};
    src->GetDesc(&sd);
    dst->GetDesc(&dd);

    WristParams p = {};
    p.dstSize[0] = static_cast<float>(dd.Width);
    p.dstSize[1] = static_cast<float>(dd.Height);
    p.srcSize[0] = static_cast<float>(sd.Width);
    p.srcSize[1] = static_cast<float>(sd.Height);
    p.threshold = threshold();
    p.softness = softness();
    p.fade = fade;
    p.useAlpha = use_alpha() ? 1.0f : 0.0f;

    const int n = pixelAspect == 2.0f
        ? uiq::deployment_layout(rects, count, p.dstSize[0], p.dstSize[1], maxScale,
                                 p.dstRect, pixelAspect)
        : uiq::wrist_layout(rects, count, p.dstSize[0], p.dstSize[1], maxScale,
                            p.dstRect, pixelAspect);
    std::memcpy(p.srcRect, rects, sizeof(float) * 4 * n);
    p.count = static_cast<float>(n);

    D3D11_MAPPED_SUBRESOURCE m = {};
    if (FAILED(context->Map(g_wristCb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
        return false;
    std::memcpy(m.pData, &p, sizeof(p));
    context->Unmap(g_wristCb, 0);

    CsGuard guard(context);
    context->CSSetShader(g_wristShader, nullptr, 0);
    context->CSSetShaderResources(0, 1, &g_wristSrv[hand]);
    context->CSSetUnorderedAccessViews(0, 1, &g_wristUav[hand], nullptr);
    context->CSSetConstantBuffers(0, 1, &g_wristCb);
    context->CSSetSamplers(0, 1, &g_wristSampler);

    context->Dispatch((dd.Width + 7) / 8, (dd.Height + 7) / 8, 1);
    return true;
}

bool ui_flat_to_eye(ID3D11Device* device, ID3D11DeviceContext* context,
                    ID3D11Texture2D* src, ID3D11Texture2D* dst, int eye, FlatEyeParams p)
{
    if (device == nullptr || context == nullptr || src == nullptr || dst == nullptr ||
        eye < 0 || eye > 1 || !ensure_flat_shader(device))
        return false;

    D3D11_TEXTURE2D_DESC sd = {};
    D3D11_TEXTURE2D_DESC dd = {};
    src->GetDesc(&sd);
    dst->GetDesc(&dd);

    if (g_flatSrv == nullptr || g_flatSrvFor != src)
    {
        if (g_flatSrv != nullptr) { g_flatSrv->Release(); g_flatSrv = nullptr; }
        D3D11_SHADER_RESOURCE_VIEW_DESC vd = {};
        vd.Format = non_srgb(sd.Format);
        vd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        vd.Texture2D.MipLevels = static_cast<UINT>(-1);   /* every mip (TEST 538) */
        if (FAILED(device->CreateShaderResourceView(src, &vd, &g_flatSrv)))
        {
            g_flatSrvFor = nullptr;
            return false;
        }
        g_flatSrvFor = src;
    }

    /* TEST 538: the menu image's mips, once a frame (eye 0 comes first). */
    if (eye == 0 && sd.MipLevels > 1 && (sd.MiscFlags & D3D11_RESOURCE_MISC_GENERATE_MIPS) != 0)
        context->GenerateMips(g_flatSrv);

    if (g_flatUav[eye] == nullptr || g_flatUavFor[eye] != dst)
    {
        if (g_flatUav[eye] != nullptr) { g_flatUav[eye]->Release(); g_flatUav[eye] = nullptr; }
        D3D11_UNORDERED_ACCESS_VIEW_DESC ud = {};
        ud.Format = non_srgb(dd.Format);
        ud.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
        if (FAILED(device->CreateUnorderedAccessView(dst, &ud, &g_flatUav[eye])))
        {
            g_flatUavFor[eye] = nullptr;
            return false;
        }
        g_flatUavFor[eye] = dst;
    }

    p.size[0] = static_cast<float>(dd.Width);
    p.size[1] = static_cast<float>(dd.Height);
    if (p.uv[2] == 0.0f && p.uv[3] == 0.0f)
    {
        p.uv[0] = p.uv[1] = 0.0f;
        p.uv[2] = p.uv[3] = 1.0f;
    }

    D3D11_MAPPED_SUBRESOURCE m = {};
    if (FAILED(context->Map(g_flatCb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
        return false;
    std::memcpy(m.pData, &p, sizeof(p));
    context->Unmap(g_flatCb, 0);

    CsGuard guard(context);
    context->CSSetShader(g_flatShader, nullptr, 0);
    context->CSSetShaderResources(0, 1, &g_flatSrv);
    context->CSSetUnorderedAccessViews(0, 1, &g_flatUav[eye], nullptr);
    context->CSSetConstantBuffers(0, 1, &g_flatCb);
    context->CSSetSamplers(0, 1, &g_flatSampler);
    context->Dispatch((dd.Width + 7) / 8, (dd.Height + 7) / 8, 1);
    return true;
}

namespace
{
bool lbl_srv(ID3D11Device* device, ID3D11Texture2D* t, ID3D11ShaderResourceView*& srv,
             ID3D11Texture2D*& forT, bool allMips)
{
    if (srv != nullptr && forT == t)
        return true;
    if (srv != nullptr) { srv->Release(); srv = nullptr; }
    forT = nullptr;
    D3D11_TEXTURE2D_DESC d = {};
    t->GetDesc(&d);
    D3D11_SHADER_RESOURCE_VIEW_DESC vd = {};
    vd.Format = non_srgb(d.Format);
    vd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    vd.Texture2D.MipLevels = allMips ? static_cast<UINT>(-1) : 1;
    if (FAILED(device->CreateShaderResourceView(t, &vd, &srv)))
        return false;
    forT = t;
    return true;
}
}

bool ui_labels_to_eye(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Texture2D* ui,
                      bool regenMips, ID3D11Texture2D* base, ID3D11Texture2D* dst, int eye,
                      FlatEyeParams p)
{
    if (device == nullptr || context == nullptr || ui == nullptr || base == nullptr ||
        dst == nullptr || eye < 0 || eye > 1 || !ensure_flat_shader(device))
        return false;

    if (g_lblShader == nullptr)
    {
        if (g_lblFailed)
            return false;
        g_lblFailed = true;
        const D3D_SHADER_MACRO defs[] = { { "BLEND", "1" }, { nullptr, nullptr } };
        ID3DBlob* code = nullptr;
        ID3DBlob* errors = nullptr;
        const HRESULT hr = D3DCompile(kFlatEyeShader, std::strlen(kFlatEyeShader), "ui_labels_eye",
                                      defs, nullptr, "main", "cs_5_0", 0, 0, &code, &errors);
        if (FAILED(hr) || code == nullptr)
        {
            BVR_ERR("World labels into the eyes (TEST 635): shader would not compile (0x%08X)%s%s",
                    static_cast<unsigned>(hr), errors != nullptr ? ": " : "",
                    errors != nullptr ? static_cast<const char*>(errors->GetBufferPointer()) : "");
            if (errors != nullptr) errors->Release();
            if (code != nullptr) code->Release();
            return false;
        }
        if (errors != nullptr)
            errors->Release();
        const HRESULT sh = device->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(),
                                                       nullptr, &g_lblShader);
        code->Release();
        if (FAILED(sh))
            return false;
        g_lblFailed = false;
    }

    if (!lbl_srv(device, ui, g_lblUiSrv[eye], g_lblUiFor[eye], true) ||
        !lbl_srv(device, base, g_lblBaseSrv[eye], g_lblBaseFor[eye], false))
        return false;

    if (g_lblUav[eye] == nullptr || g_lblUavFor[eye] != dst)
    {
        if (g_lblUav[eye] != nullptr) { g_lblUav[eye]->Release(); g_lblUav[eye] = nullptr; }
        g_lblUavFor[eye] = nullptr;
        D3D11_TEXTURE2D_DESC dd = {};
        dst->GetDesc(&dd);
        D3D11_UNORDERED_ACCESS_VIEW_DESC ud = {};
        ud.Format = non_srgb(dd.Format);
        ud.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
        if (FAILED(device->CreateUnorderedAccessView(dst, &ud, &g_lblUav[eye])))
            return false;
        g_lblUavFor[eye] = dst;
    }

    if (regenMips)
        context->GenerateMips(g_lblUiSrv[eye]);

    D3D11_TEXTURE2D_DESC dd = {};
    dst->GetDesc(&dd);
    p.size[0] = static_cast<float>(dd.Width);
    p.size[1] = static_cast<float>(dd.Height);

    D3D11_MAPPED_SUBRESOURCE m = {};
    if (FAILED(context->Map(g_flatCb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
        return false;
    std::memcpy(m.pData, &p, sizeof(p));
    context->Unmap(g_flatCb, 0);

    CsGuard guard(context);
    ID3D11ShaderResourceView* srvs[2] = { g_lblUiSrv[eye], g_lblBaseSrv[eye] };
    context->CSSetShader(g_lblShader, nullptr, 0);
    context->CSSetShaderResources(0, 2, srvs);
    context->CSSetUnorderedAccessViews(0, 1, &g_lblUav[eye], nullptr);
    context->CSSetConstantBuffers(0, 1, &g_flatCb);
    context->CSSetSamplers(0, 1, &g_flatSampler);
    context->Dispatch((dd.Width + 7) / 8, (dd.Height + 7) / 8, 1);
    return true;
}

void ui_key_release()
{
    probe_release();

    for (int e = 0; e < 2; ++e)
    {
        for (ID3D11ShaderResourceView** v : { &g_lblUiSrv[e], &g_lblBaseSrv[e] })
            if (*v != nullptr) { (*v)->Release(); *v = nullptr; }
        if (g_lblUav[e] != nullptr) { g_lblUav[e]->Release(); g_lblUav[e] = nullptr; }
        g_lblUiFor[e] = g_lblBaseFor[e] = g_lblUavFor[e] = nullptr;
    }
    if (g_lblShader != nullptr) { g_lblShader->Release(); g_lblShader = nullptr; }

    for (int e = 0; e < 2; ++e)
    {
        if (g_flatUav[e] != nullptr) { g_flatUav[e]->Release(); g_flatUav[e] = nullptr; }
        g_flatUavFor[e] = nullptr;
    }
    if (g_flatSrv != nullptr)     { g_flatSrv->Release();     g_flatSrv = nullptr; }
    g_flatSrvFor = nullptr;
    if (g_flatSampler != nullptr) { g_flatSampler->Release(); g_flatSampler = nullptr; }
    if (g_flatCb != nullptr)      { g_flatCb->Release();      g_flatCb = nullptr; }
    if (g_flatShader != nullptr)  { g_flatShader->Release();  g_flatShader = nullptr; }

    for (int h = 0; h < 2; ++h)
    {
        if (g_wristUav[h] != nullptr) { g_wristUav[h]->Release(); g_wristUav[h] = nullptr; }
        g_wristUavFor[h] = nullptr;
        if (g_wristSrv[h] != nullptr) { g_wristSrv[h]->Release(); g_wristSrv[h] = nullptr; }
        g_wristSrvFor[h] = nullptr;
    }
    if (g_wristSampler != nullptr) { g_wristSampler->Release(); g_wristSampler = nullptr; }
    if (g_wristCb != nullptr)      { g_wristCb->Release();      g_wristCb = nullptr; }
    if (g_wristShader != nullptr)  { g_wristShader->Release();  g_wristShader = nullptr; }

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
