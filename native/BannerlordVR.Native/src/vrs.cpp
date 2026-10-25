#include "vrs.h"

#include "bvr_config.h"
#include "bvr_log.h"
#include "stereo_dup.h"
#include "vp_patch.h"
#include "xr_input.h"

#include <windows.h>    /* GetTickCount64 */

#include <atomic>
#include <cmath>
#include <stdio.h>
#include <utility>
#include <vector>

/* NVIDIA's header, not held to /W4. Needs d3d11.h first (vrs.h). */
#pragma warning(push, 0)
#include "nvapi.h"
#pragma warning(pop)

namespace bvr {
namespace {

const char* const kPresetName[4] = { "off", "wide", "balanced", "narrow" };

/* FIXED (TEST 427): ring radii in units of the frustum's horizontal half-width
   out from the optical axis - inside `full` every pixel is shaded, out to `half`
   one per 2x2, beyond that one per 4x4. The eye roams inside these, so they are
   generous. */
struct Rings { float full, half; };
const Rings kRings[4] = {
    { 0.00f, 0.00f },
    { 0.60f, 0.90f },   /* wide */
    { 0.45f, 0.75f },   /* balanced */
    { 0.30f, 0.60f },   /* narrow */
};

/* EYE-TRACKED (TEST 428): the same rings in degrees from where the eyes look.
   Acuity falls off within a few degrees of the fovea; the rest is margin for
   the tracker's lag and error. `vrs_gaze_scale` scales them all. */
const Rings kGazeRings[4] = {
    {  0.0f,  0.0f },
    { 25.0f, 45.0f },   /* wide */
    { 18.0f, 35.0f },   /* balanced */
    { 12.0f, 25.0f },   /* narrow */
};

/* Map value -> rate. 0 must stay full rate: it is also what a pixel outside
   the map reads. */
enum : uint8_t { kFull = 0, kHalf = 1, kQuarter = 2 };

/* TEST 429: the outer ring is 2x2 too unless `vrs_4x4 = 1`. DLSS Performance
   doubles a coarse pixel on the way out, so 4x4 showed as 8x8 blocks - and the
   shading it saved over 2x2 never reached the frame time: at ~8% of the G-buffer's
   shading left, the G-buffer took the same 4.1-4.5 ms as with VRS off. */
bool quarter_rate()
{
    static const bool on = config_bool("vrs_4x4", false);
    return on;
}

/* A redraw when the gaze has moved this far (tangent; ~1 deg at the centre). */
constexpr float kGazeStep = 0.0175f;

std::atomic<int>      g_preset{ -1 };       /* -1 = not read from the cfg yet */
std::atomic<int>      g_eye{ -1 };
std::atomic<int>      g_supported{ -1 };
std::atomic<uint64_t> g_cDraws{ 0 }, g_cGazeDraws{ 0 }, g_cRefills{ 0 };
std::atomic<float>    g_shadingLeft{ 1.0f };

/* Render thread only. One map per eye-shaped viewport: DLSS's input and its
   output are both shaped like the eye and alternate within a frame. */
struct Map
{
    D3D11_VIEWPORT vp = {};
    bool tangents = false;      /* laid out on the measured frustum */
    float half = 1.0f;          /* tangent of one horizontal half-width */
    UINT tw = 0, th = 0;
    std::vector<float> tile;    /* per tile: tangent x, y, 1/|(x, y, 1)|; x NaN = outside the viewport */
    std::vector<uint8_t> rates;
    ID3D11Texture2D* tex = nullptr;
    ID3D11NvShadingRateResourceView* view = nullptr;
    int  preset = 0;            /* what the contents were made for */
    bool gaze = false;
    float gx = 0.0f, gy = 0.0f;
    uint64_t lastUse = 0;       /* GetTickCount64 of the last draw that used it */
};

/* TEST 432: a map is only ever replaced after no draw has used it for this long.
   TEST 427 replaced them round robin in four slots; with DLSS off the game draws
   six eye-shaped viewport sizes or more, so every frame freed a map that frames
   still on the GPU were reading - the GPU hung (LiveKernelEvent 141) and the game
   went down. Half a second is many frames past anything in flight. */
constexpr int kMaps = 8;
constexpr uint64_t kMapIdleMs = 500;
Map  g_maps[kMaps];
bool g_mapsFullLogged = false;
bool g_on = false;
ID3D11NvShadingRateResourceView* g_bound = nullptr;

void give_up(const char* what, NvAPI_Status st)
{
    BVR_WARN("NVIDIA VRS (TEST 427): %s failed (NvAPI %d); foveated rendering is off "
             "for this session.", what, static_cast<int>(st));
    g_supported.store(0, std::memory_order_relaxed);
}

bool available(ID3D11DeviceContext* c)
{
    const int known = g_supported.load(std::memory_order_relaxed);
    if (known >= 0)
        return known == 1;

    /* Loads the driver's nvapi64.dll; fails cleanly where there is none. */
    NvAPI_Status st = NvAPI_Initialize();
    if (st != NVAPI_OK)
    {
        BVR_INFO("NVIDIA VRS (TEST 427): unavailable - no NVIDIA driver (NvAPI_Initialize %d).",
                 static_cast<int>(st));
        g_supported.store(0, std::memory_order_relaxed);
        return false;
    }

    NV_D3D1x_GRAPHICS_CAPS caps = {};
    ID3D11Device* dev = nullptr;
    c->GetDevice(&dev);
    st = dev != nullptr
       ? NvAPI_D3D1x_GetGraphicsCapabilities(dev, NV_D3D1x_GRAPHICS_CAPS_VER, &caps)
       : NVAPI_INVALID_ARGUMENT;
    if (dev) dev->Release();

    const bool ok = st == NVAPI_OK && caps.bVariablePixelRateShadingSupported != 0;
    if (ok)
        BVR_INFO("NVIDIA VRS (TEST 427): available.");
    else
        BVR_INFO("NVIDIA VRS (TEST 427): unavailable - this GPU has no variable rate "
                 "shading (needs RTX 20 / GTX 16 or newer; caps query %d).",
                 static_cast<int>(st));
    g_supported.store(ok ? 1 : 0, std::memory_order_relaxed);
    return ok;
}

void set_rates(ID3D11DeviceContext* c, bool on)
{
    NV_D3D11_VIEWPORT_SHADING_RATE_DESC rate = {};
    rate.enableVariablePixelShadingRate = true;
    for (NV_PIXEL_SHADING_RATE& r : rate.shadingRateTable)
        r = NV_PIXEL_X1_PER_RASTER_PIXEL;
    rate.shadingRateTable[kHalf] = NV_PIXEL_X1_PER_2X2_RASTER_PIXELS;
    rate.shadingRateTable[kQuarter] = quarter_rate() ? NV_PIXEL_X1_PER_4X4_RASTER_PIXELS
                                                     : NV_PIXEL_X1_PER_2X2_RASTER_PIXELS;

    NV_D3D11_VIEWPORTS_SHADING_RATE_DESC desc = {};
    desc.version = NV_D3D11_VIEWPORTS_SHADING_RATE_DESC_VER;
    desc.numViewports = on ? 1u : 0u;       /* 0 turns it off on every viewport */
    desc.pViewports = &rate;

    const NvAPI_Status st = NvAPI_D3D11_RSSetViewportsPixelShadingRates(c, &desc);
    if (st != NVAPI_OK)
        give_up("RSSetViewportsPixelShadingRates", st);
    g_on = on && st == NVAPI_OK;
}

/* Each tile's direction, once per map. On the measured frustum it is a true
   tangent (an off-centre axis lands right, and the gaze can be compared as an
   angle); before the frustum is measured, pixels from the viewport's centre in
   half-widths, with no gaze. */
void lay_out(Map& m, const D3D11_VIEWPORT& vp, bool tangents, float l, float r, float u, float d)
{
    m.vp = vp;
    m.tangents = tangents;
    m.half = tangents ? 0.5f * (r - l) : 1.0f;
    m.tw = static_cast<UINT>(ceilf((vp.TopLeftX + vp.Width) / NV_VARIABLE_PIXEL_SHADING_TILE_WIDTH));
    m.th = static_cast<UINT>(ceilf((vp.TopLeftY + vp.Height) / NV_VARIABLE_PIXEL_SHADING_TILE_HEIGHT));
    m.tile.assign(static_cast<size_t>(m.tw) * m.th * 3, 0.0f);
    m.rates.assign(static_cast<size_t>(m.tw) * m.th, kFull);

    for (UINT y = 0; y < m.th; ++y)
    {
        for (UINT x = 0; x < m.tw; ++x)
        {
            /* The tile's centre, as a fraction across the viewport. */
            const float sx = ((x + 0.5f) * NV_VARIABLE_PIXEL_SHADING_TILE_WIDTH - vp.TopLeftX) / vp.Width;
            const float sy = ((y + 0.5f) * NV_VARIABLE_PIXEL_SHADING_TILE_HEIGHT - vp.TopLeftY) / vp.Height;
            float* t = &m.tile[(static_cast<size_t>(y) * m.tw + x) * 3];
            if (tangents)
            {
                t[0] = l + sx * (r - l);
                t[1] = u - sy * (u - d);
            }
            else
            {
                t[0] = (sx - 0.5f) * 2.0f;
                t[1] = (0.5f - sy) * 2.0f * vp.Height / vp.Width;
            }
            t[2] = 1.0f / sqrtf(t[0] * t[0] + t[1] * t[1] + 1.0f);
            if (sx < 0.0f || sx > 1.0f || sy < 0.0f || sy > 1.0f)
                t[0] = std::nanf("");    /* never rasterised; stays full rate */
        }
    }
}

/* The rates for a preset, around the gaze or the fixed centre. Returns the
   share of the pixel shading left (full = 1, 2x2 = 1/4, 4x4 = 1/16). */
float fill(Map& m, int preset, bool gaze, float gx, float gy)
{
    m.preset = preset;
    m.gaze = gaze;
    m.gx = gx;
    m.gy = gy;

    float cosFull = 0.0f, cosHalf = 0.0f, gn = 0.0f;
    float full2 = 0.0f, half2 = 0.0f;
    if (gaze)
    {
        static const float scale = config_float("vrs_gaze_scale", 1.0f);
        const float k = 0.0174533f * scale;
        cosFull = cosf(kGazeRings[preset].full * k);
        cosHalf = cosf(kGazeRings[preset].half * k);
        gn = 1.0f / sqrtf(gx * gx + gy * gy + 1.0f);
    }
    else
    {
        full2 = kRings[preset].full * m.half;
        half2 = kRings[preset].half * m.half;
        full2 *= full2;
        half2 *= half2;
    }

    uint32_t count[3] = {};
    const size_t n = m.rates.size();
    for (size_t i = 0; i < n; ++i)
    {
        const float* t = &m.tile[i * 3];
        if (std::isnan(t[0]))
        {
            m.rates[i] = kFull;
            continue;
        }
        uint8_t v;
        if (gaze)
        {
            const float c = (t[0] * gx + t[1] * gy + 1.0f) * t[2] * gn;  /* cos of the angle */
            v = c > cosFull ? kFull : c > cosHalf ? kHalf : kQuarter;
        }
        else
        {
            const float r2 = t[0] * t[0] + t[1] * t[1];
            v = r2 < full2 ? kFull : r2 < half2 ? kHalf : kQuarter;
        }
        m.rates[i] = v;
        ++count[v];
    }

    const float total = static_cast<float>(count[0] + count[1] + count[2]);
    const float outer = quarter_rate() ? 16.0f : 4.0f;
    return total > 0.0f ? (count[0] + count[1] / 4.0f + count[2] / outer) / total : 1.0f;
}

ID3D11NvShadingRateResourceView* map_for(ID3D11DeviceContext* c, const D3D11_VIEWPORT& vp,
                                         int preset, bool gaze, float gx, float gy)
{
    float l = 0.0f, r = 0.0f, u = 0.0f, d = 0.0f;
    const bool tangents = vp_measured_tangents(&l, &r, &u, &d) && r > l && u > d;
    gaze = gaze && tangents;    /* a gaze needs a frustum to land on */
    const uint64_t now = GetTickCount64();

    for (Map& m : g_maps)
    {
        if (m.view == nullptr || m.tangents != tangents ||
            m.vp.TopLeftX != vp.TopLeftX || m.vp.TopLeftY != vp.TopLeftY ||
            m.vp.Width != vp.Width || m.vp.Height != vp.Height)
            continue;
        m.lastUse = now;

        if (m.preset != preset || m.gaze != gaze ||
            (gaze && fabsf(gx - m.gx) + fabsf(gy - m.gy) > kGazeStep))
        {
            g_shadingLeft.store(fill(m, preset, gaze, gx, gy), std::memory_order_relaxed);
            StereoIgnore own;   /* ours, not the engine's: no twin bookkeeping */
            c->UpdateSubresource(m.tex, 0, nullptr, m.rates.data(), m.tw, 0);
            g_cRefills.fetch_add(1, std::memory_order_relaxed);
        }
        return m.view;
    }

    /* A slot: an empty one, else the longest idle one past kMapIdleMs. None
       means this viewport goes without VRS rather than freeing a map in flight. */
    Map* slot = nullptr;
    for (Map& m : g_maps)
    {
        if (m.view == nullptr)
        {
            slot = &m;
            break;
        }
        if (now - m.lastUse >= kMapIdleMs && (slot == nullptr || m.lastUse < slot->lastUse))
            slot = &m;
    }
    if (slot == nullptr)
    {
        if (!g_mapsFullLogged)
        {
            g_mapsFullLogged = true;
            BVR_INFO("NVIDIA VRS (TEST 432): all %d maps are in use; the %.0fx%.0f eye viewport "
                     "goes without VRS until one has been idle %llu ms.", kMaps, vp.Width,
                     vp.Height, static_cast<unsigned long long>(kMapIdleMs));
        }
        return nullptr;
    }

    Map fresh;
    lay_out(fresh, vp, tangents, l, r, u, d);
    if (fresh.tw == 0 || fresh.th == 0 || fresh.tw > 1024 || fresh.th > 1024)
        return nullptr;
    const float left = fill(fresh, preset, gaze, gx, gy);

    ID3D11Device* dev = nullptr;
    c->GetDevice(&dev);
    if (dev == nullptr)
        return nullptr;

    D3D11_TEXTURE2D_DESC td = {};
    td.Width = fresh.tw;
    td.Height = fresh.th;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_R8_UINT;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    const D3D11_SUBRESOURCE_DATA init = { fresh.rates.data(), fresh.tw, 0 };

    NvAPI_Status st = NVAPI_ERROR;
    if (SUCCEEDED(dev->CreateTexture2D(&td, &init, &fresh.tex)) && fresh.tex != nullptr)
    {
        NV_D3D11_SHADING_RATE_RESOURCE_VIEW_DESC vd = {};
        vd.version = NV_D3D11_SHADING_RATE_RESOURCE_VIEW_DESC_VER;
        vd.Format = DXGI_FORMAT_R8_UINT;
        vd.ViewDimension = NV_SRRV_DIMENSION_TEXTURE2D;
        vd.Texture2D.MipSlice = 0;
        st = NvAPI_D3D11_CreateShadingRateResourceView(dev, fresh.tex, &vd, &fresh.view);
    }
    dev->Release();

    if (st != NVAPI_OK || fresh.view == nullptr)
    {
        if (fresh.tex) fresh.tex->Release();
        give_up("CreateShadingRateResourceView", st);
        return nullptr;
    }

    /* Idle for kMapIdleMs, so no frame in flight reads it; unbound first in case
       it is still the context's. */
    if (slot->view != nullptr && slot->view == g_bound)
    {
        NvAPI_D3D11_RSSetShadingRateResourceView(c, nullptr);
        g_bound = nullptr;
    }
    if (slot->view) slot->view->Release();
    if (slot->tex) slot->tex->Release();
    *slot = std::move(fresh);
    slot->lastUse = now;

    g_shadingLeft.store(left, std::memory_order_relaxed);
    BVR_INFO("NVIDIA VRS (TEST 427): %s map for the %.0fx%.0f eye viewport (%ux%u tiles, %s, "
             "%s) - about %.0f%% of the pixel shading left.",
             kPresetName[preset], vp.Width, vp.Height, slot->tw, slot->th,
             tangents ? "on the measured frustum" : "frustum not measured yet, pixel centre",
             gaze ? "around the gaze" : "fixed centre", 100.0 * left);
    return slot->view;
}

} // namespace

void vrs_set_preset(int preset)
{
    g_preset.store(preset < 0 ? 0 : preset > 3 ? 3 : preset, std::memory_order_relaxed);
}

int vrs_preset()
{
    int p = g_preset.load(std::memory_order_relaxed);
    if (p < 0)
    {
        vrs_set_preset(static_cast<int>(config_float("vrs", 0.0f)));
        p = g_preset.load(std::memory_order_relaxed);
    }
    return p;
}

void vrs_set_eye(bool follow)
{
    g_eye.store(follow ? 1 : 0, std::memory_order_relaxed);
}

bool vrs_eye()
{
    int e = g_eye.load(std::memory_order_relaxed);
    if (e < 0)
    {
        e = config_bool("vrs_eye", true) ? 1 : 0;
        g_eye.store(e, std::memory_order_relaxed);
    }
    return e != 0;
}

int vrs_supported()
{
    return g_supported.load(std::memory_order_relaxed);
}

void vrs_draw(ID3D11DeviceContext* c, const D3D11_VIEWPORT* eyeVp)
{
    if (c == nullptr)
        return;

    const int preset = eyeVp != nullptr ? vrs_preset() : 0;
    float gx = 0.0f, gy = 0.0f;
    const bool gaze = preset > 0 && vrs_eye() && input_gaze(&gx, &gy);
    ID3D11NvShadingRateResourceView* view =
        preset > 0 && available(c) ? map_for(c, *eyeVp, preset, gaze, gx, gy) : nullptr;

    if (view == nullptr)
    {
        if (g_on)
            set_rates(c, false);
        return;
    }

    if (view != g_bound)
    {
        const NvAPI_Status st = NvAPI_D3D11_RSSetShadingRateResourceView(c, view);
        if (st != NVAPI_OK)
        {
            give_up("RSSetShadingRateResourceView", st);
            if (g_on)
                set_rates(c, false);
            return;
        }
        g_bound = view;
    }
    if (!g_on)
        set_rates(c, true);
    if (g_on)
    {
        g_cDraws.fetch_add(1, std::memory_order_relaxed);
        if (gaze)
            g_cGazeDraws.fetch_add(1, std::memory_order_relaxed);
    }
}

void vrs_report(uint64_t frames)
{
    const int preset = vrs_preset();
    if (preset <= 0 || frames == 0)
        return;

    const double f = static_cast<double>(frames);
    const uint64_t draws = g_cDraws.exchange(0, std::memory_order_relaxed);
    const uint64_t gazeDraws = g_cGazeDraws.exchange(0, std::memory_order_relaxed);
    const uint64_t refills = g_cRefills.exchange(0, std::memory_order_relaxed);

    char eye[160];
    float gx = 0.0f, gy = 0.0f;
    if (!vrs_eye())
        _snprintf_s(eye, _TRUNCATE, "eye tracking off (fixed centre)");
    else if (!input_gaze_bound())
        _snprintf_s(eye, _TRUNCATE, "no eye tracker from the runtime (fixed centre)");
    else if (input_gaze(&gx, &gy))
        _snprintf_s(eye, _TRUNCATE, "following the gaze (now %.1f deg right, %.1f up), "
                    "%.0f%% of its draws, map redrawn %.1f times a frame",
                    atanf(gx) * 57.2958f, atanf(gy) * 57.2958f,
                    draws ? 100.0 * gazeDraws / draws : 0.0, refills / f);
    else
        _snprintf_s(eye, _TRUNCATE, "eye tracker bound, NO gaze now (fixed centre)");

    BVR_INFO("NVIDIA VRS (TEST 427/428): %s, %.1f draws a frame shaded at a variable rate; "
             "%s; ~%.0f%% of the pixel shading left on the last map drawn%s.",
             kPresetName[preset], draws / f, eye,
             100.0 * g_shadingLeft.load(std::memory_order_relaxed),
             vrs_supported() == 0 ? " - UNAVAILABLE on this GPU" : "");
}

} // namespace bvr
