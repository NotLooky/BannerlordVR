#include "stereo_dup.h"

#include <windows.h>
#include <d3d11shader.h>

#include "bvr_config.h"
#include "bvr_log.h"
#include "d3d_hooks.h"
#include "frame_map.h"
#include "stereo_perf.h"
#include "vp_patch.h"

#include <atomic>
#include <initializer_list>
#include <intrin.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vector>

namespace bvr {
namespace {

/* Twins and shadows are attached to what they copy, so the runtime frees them
   with it. A pointer-keyed table cannot do that safely - D3D11 recycles
   addresses, and this project has been bitten by exactly that before. */
/* {0D0A9C41-6F3B-4E52-9C7A-2B1E4D8F6A30} */
const GUID kTwinRes  = { 0x0d0a9c41, 0x6f3b, 0x4e52, { 0x9c, 0x7a, 0x2b, 0x1e, 0x4d, 0x8f, 0x6a, 0x30 } };
/* {1E1BAD52-7A4C-4F63-8D6B-3C2F5E907B41} */
const GUID kTwinView = { 0x1e1bad52, 0x7a4c, 0x4f63, { 0x8d, 0x6b, 0x3c, 0x2f, 0x5e, 0x90, 0x7b, 0x41 } };
/* {2F2CBE63-8B5D-4A74-9E7C-4D3F6FA18C52} */
const GUID kCbShadow = { 0x2f2cbe63, 0x8b5d, 0x4a74, { 0x9e, 0x7c, 0x4d, 0x3f, 0x6f, 0xa1, 0x8c, 0x52 } };
/* Per-subresource surface state (TEST 181). A new GUID rather than TEST 165's:
   the payload changed shape. {5C5FE196-BE80-4DA7-81AF-706C9CD4BF85} */
const GUID kSurfState = { 0x5c5fe196, 0xbe80, 0x4da7, { 0x81, 0xaf, 0x70, 0x6c, 0x9c, 0xd4, 0xbf, 0x85 } };
/* {4B4ED085-AD7F-4C96-B09E-6F5B8BC3AE74} */
const GUID kShaderDecl = { 0x4b4ed085, 0xad7f, 0x4c96, { 0xb0, 0x9e, 0x6f, 0x5b, 0x8b, 0xc3, 0xae, 0x74 } };
/* Which subresources a view covers, cached on the view.
   {6D60F2A7-CF91-4EB8-92B0-817DADE5C096} */
const GUID kViewInfo = { 0x6d60f2a7, 0xcf91, 0x4eb8, { 0x92, 0xb0, 0x81, 0x7d, 0xad, 0xe5, 0xc0, 0x96 } };
/* IID_ID3D11ShaderReflection as d3dcompiler_47 knows it. */
const GUID kIidReflect = { 0x8d536ca1, 0x0cca, 0x4956, { 0xa8, 0x37, 0x78, 0x69, 0x63, 0x75, 0x55, 0x84 } };

std::atomic<bool> g_active{ false };

/* Bumped every time native stereo is switched ON. A surface state written under
   an older epoch reads as untracked, so nothing that diverged in an earlier
   session - or while the mode was off and nothing kept the twins in step - is
   trusted: the twin is refilled from the original the first time it is needed. */
std::atomic<uint32_t> g_epoch{ 1 };

thread_local bool t_inDup = false;
thread_local int  t_ignore = 0;

uint64_t g_frame = 0;                 /* render thread only */

std::atomic<uint64_t> g_cOps{0}, g_cTainted{0}, g_cDup{0};
std::atomic<uint64_t> g_cTwins{0}, g_twinBytes{0};
std::atomic<uint64_t> g_cCbUpload{0}, g_cCbCamera{0}, g_cCbLight{0};
std::atomic<uint64_t> g_cTwinFail{0}, g_cRefused{0}, g_cSync{0}, g_cReadback{0};
std::atomic<uint64_t> g_cUnknownDecl{0}, g_cSwapOverflow{0}, g_cCbFull{0};
std::atomic<uint64_t> g_cCpuMirror{0};
std::atomic<uint64_t> g_cCbMain1760{0}, g_cCbMain128{0}, g_cCbMain224{0}, g_cCbFar{0};
std::atomic<uint64_t> g_cOffsetFallback{0}, g_cLightShared{0}, g_cReadSync{0};
std::atomic<uint64_t> g_syncBytes{0};
std::atomic<uint64_t> g_tExamine{0}, g_tDup{0};
std::atomic<uint64_t> g_declParsed{0}, g_declFailed{0}, g_declAgree{0}, g_declDiffer{0};
std::atomic<uint64_t> g_declChecked{0};

int64_t qpc_now()
{
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}

double qpc_ms(uint64_t ticks)
{
    static const double perMs = [] {
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        return static_cast<double>(f.QuadPart) / 1000.0;
    }();
    return static_cast<double>(ticks) / perMs;
}

/* NOTHING OF OURS IS THE ENGINE'S FRAME.
 *
   Not a re-issue or a twin sync (t_inDup), not an external write we are making
   for the second eye (t_ignore - the DLSS evaluation), and not anything issued
   inside our Present tick: the AFR staging copies, the mirror, the warp. Before
   TEST 181 those were tracked like engine passes, so copying the diverged eye
   image into a staging texture of ours twinned the staging texture and copied
   the second eye into it too, every frame. */
bool ignored()
{
    return t_inDup || t_ignore > 0 || in_present_on_this_thread();
}

// ---------------------------------------------------------------------------
// the eye offset, and the camera the engine is rendering from
// ---------------------------------------------------------------------------

/* The two eyes managed last published. Used for the DISTANCE a camera may be
   from the head and for the IPD - never, since TEST 182, as the exact camera an
   upload is tested against: the engine builds a frame from a pose one
   publication old (TEST 167), so while the player moves, the published eye is
   centimetres to metres from the camera actually drawn, and the exact test
   failed for whole windows at a time ("0 of 22440 camera uploads were the main
   view"). Each failed upload left the second eye drawn from the FIRST eye's
   camera for that pass. */
struct PubEyes
{
    float cur[3];    /* the eye the engine is drawing */
    float other[3];
    float d[3];      /* other - cur */
    float dir[3];    /* unit */
    float ipd;
    bool  valid;
};

PubEyes published_eyes()
{
    PubEyes p = {};
    VpFrame cur = {}, other = {};
    if (!vp_debug_published(&cur, &other) || !cur.valid || !other.valid)
        return p;

    for (int i = 0; i < 3; ++i)
    {
        p.cur[i] = cur.eyeCamera[12 + i];
        p.other[i] = other.eyeCamera[12 + i];
        p.d[i] = p.other[i] - p.cur[i];
    }
    p.ipd = sqrtf(p.d[0] * p.d[0] + p.d[1] * p.d[1] + p.d[2] * p.d[2]);
    p.valid = p.ipd > 0.005f && p.ipd < 0.5f;      /* a plausible IPD and nothing else */
    if (p.valid)
        for (int i = 0; i < 3; ++i)
            p.dir[i] = p.d[i] / p.ipd;
    return p;
}

float dist3(const float* a, const float* b)
{
    const float x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
    return sqrtf(x * x + y * y + z * z);
}

float near_head_m()
{
    static float cached = 2.0f;
    static bool have = false;
    if (!have)
    {
        have = true;
        cached = config_float("stereo_near_m", 2.0f);
    }
    return cached;
}

bool light_passes_shared()
{
    static bool cached = true;
    static bool have = false;
    if (!have)
    {
        have = true;
        cached = config_bool("stereo_light_shared", true);
    }
    return cached;
}

/* The main camera of the frame being drawn, and the one before - what the
   128- and 224-byte buffers, which carry no position of their own, are verified
   against. Render thread only. */
float    g_mainCam[3] = {};
float    g_mainD[3] = {};
uint64_t g_mainFrame = 0;
bool     g_mainValid = false;
float    g_prevMainCam[3] = {};
float    g_prevMainD[3] = {};
bool     g_prevMainValid = false;

/* The main view's g_viewproj_unjittered (+304), engine bytes - what the
   per-mesh previous-frame transforms are corrected with (see
   stereo_on_buffer_write). Render thread only. */
float    g_mainVpU[16] = {};
bool     g_mainVpUValid = false;

/* How the eye offset agreed with the published one, for the log. */
double   g_offsetCosSum = 0.0;
double   g_offsetCosMin = 1.0;
uint64_t g_offsetCosN = 0;

void note_main_camera(const float cam[3], const float d[3])
{
    if (g_mainValid && g_mainFrame != g_frame)
    {
        memcpy(g_prevMainCam, g_mainCam, sizeof(g_prevMainCam));
        memcpy(g_prevMainD, g_mainD, sizeof(g_prevMainD));
        g_prevMainValid = true;
    }
    memcpy(g_mainCam, cam, sizeof(g_mainCam));
    memcpy(g_mainD, d, sizeof(g_mainD));
    g_mainFrame = g_frame;
    g_mainValid = true;
}

// ---------------------------------------------------------------------------
// the camera sites, and how the second eye's copy is built
//
// Row-vector convention: p' = p * M, translation in row 3. The sites are the
// ones the frame map recovered from the engine's own buffers; each is VERIFIED
// against the live camera on every upload.
// ---------------------------------------------------------------------------

enum SiteKind { SITE_FWD, SITE_INV, SITE_POS };

struct Site { uint32_t offset; SiteKind kind; };
struct Layout { uint32_t bytes; Site sites[8]; int count; };

const Layout kLayouts[] = {
    /* per-view: position, position, view, view-projection, previous
       view-projection, ray basis (row 2 is the position), camera-to-world
       (row 3 is the position). The two positions inside those last matrices are
       NOT separate sites - the matrix rule already moves them. */
    { 1760, { {0, SITE_POS}, {16, SITE_POS}, {112, SITE_FWD}, {240, SITE_FWD},
              {304, SITE_FWD}, {688, SITE_INV}, {928, SITE_INV} }, 7 },
    { 128,  { {0, SITE_FWD} }, 1 },                       /* per pass */
    { 224,  { {64, SITE_FWD}, {128, SITE_INV} }, 2 },     /* the compute passes */
};

const Layout* layout_for(uint32_t bytes)
{
    for (const Layout& l : kLayouts)
        if (l.bytes == bytes)
            return &l;
    return nullptr;
}

float mat_at(const float* m, int r, int c) { return m[r * 4 + c]; }

/* p * M, and the sum of |terms| per component - a zero is only a zero measured
   against the size of what cancelled to produce it. */
void mul_point(const float* m, const float p[4], float out[4], float mag[4])
{
    for (int c = 0; c < 4; ++c)
    {
        float v = 0.0f, g = 0.0f;
        for (int r = 0; r < 4; ++r)
        {
            const float t = p[r] * mat_at(m, r, c);
            v += t;
            g += fabsf(t);
        }
        out[c] = v;
        mag[c] = g;
    }
}

bool finite16f(const float* m)
{
    for (int i = 0; i < 16; ++i)
        if (!isfinite(m[i]))
            return false;
    return true;
}

/* Does this matrix take WORLD positions into a space centred on the camera? */
bool is_forward_camera(const float* m, const float c[3])
{
    if (!finite16f(m))
        return false;

    const float p[4] = { c[0], c[1], c[2], 1.0f };
    float v[4], g[4];
    mul_point(m, p, v, g);

    const float tx = 3e-5f * g[0] + 1e-4f;
    const float ty = 3e-5f * g[1] + 1e-4f;
    const float tz = 3e-5f * g[2] + 1e-4f;
    const float tw = 3e-5f * g[3] + 1e-4f;

    /* world -> clip: the camera is where w vanishes, and x and y with it */
    if (fabsf(v[3]) < tw && fabsf(v[0]) < tx && fabsf(v[1]) < ty && g[3] > 1e-3f)
        return true;

    /* world -> view: the camera is the origin */
    if (fabsf(v[0]) < tx && fabsf(v[1]) < ty && fabsf(v[2]) < tz &&
        fabsf(v[3] - 1.0f) < 1e-3f && (g[0] + g[1] + g[2]) > 1e-3f)
        return true;

    return false;
}

/* Does this matrix take clip positions back to world - does the ray through
   NDC (0,0) pass through the camera? */
bool is_inverse_camera(const float* m, const float c[3])
{
    if (!finite16f(m))
        return false;

    const float pa[4] = { 0.0f, 0.0f, 0.2f, 1.0f };
    const float pb[4] = { 0.0f, 0.0f, 0.8f, 1.0f };
    float a[4], b[4], g[4];
    mul_point(m, pa, a, g);
    mul_point(m, pb, b, g);
    if (fabsf(a[3]) < 1e-12f || fabsf(b[3]) < 1e-12f)
        return false;

    float p0[3], p1[3];
    for (int i = 0; i < 3; ++i)
    {
        p0[i] = a[i] / a[3];
        p1[i] = b[i] / b[3];
    }

    float u[3] = { p1[0] - p0[0], p1[1] - p0[1], p1[2] - p0[2] };
    const float len = sqrtf(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]);
    if (!(len > 1e-3f) || !isfinite(len))
        return false;
    for (int i = 0; i < 3; ++i)
        u[i] /= len;

    const float w[3] = { c[0] - p0[0], c[1] - p0[1], c[2] - p0[2] };
    const float t = w[0] * u[0] + w[1] * u[1] + w[2] * u[2];
    float perp = 0.0f;
    for (int i = 0; i < 3; ++i)
    {
        const float e = w[i] - t * u[i];
        perp += e * e;
    }
    return sqrtf(perp) < 0.02f && fabsf(t) < 1e5f;
}

bool is_camera_position(const float* v, const float c[3])
{
    return fabsf(v[0] - c[0]) < 0.02f && fabsf(v[1] - c[1]) < 0.02f &&
           fabsf(v[2] - c[2]) < 0.02f;
}

/* M' = T(-d) * M : only row 3 moves. */
void apply_forward(float* m, const float d[3])
{
    for (int c = 0; c < 4; ++c)
        m[3 * 4 + c] -= d[0] * mat_at(m, 0, c) + d[1] * mat_at(m, 1, c) +
                        d[2] * mat_at(m, 2, c);
}

/* M' = M * T(+d) : every row gains its own w times d. */
void apply_inverse(float* m, const float d[3])
{
    for (int r = 0; r < 4; ++r)
    {
        const float w = mat_at(m, r, 3);
        for (int c = 0; c < 3; ++c)
            m[r * 4 + c] += w * d[c];
    }
}

// ---------------------------------------------------------------------------
// constant buffers
// ---------------------------------------------------------------------------

struct CbEntry
{
    ID3D11Resource* original;
    ID3D11Buffer*   shadow;
    uint32_t        bytes;
    uint64_t        cameraFrame;    /* last frame its contents were the main view */
    uint64_t        lightFrame;     /* last frame they were a light view at the player */
};

constexpr int kCbTableSlots = 1024;
CbEntry g_cbTable[kCbTableSlots] = {};

CbEntry* cb_find(ID3D11Resource* r, bool create)
{
    uintptr_t h = reinterpret_cast<uintptr_t>(r);
    h ^= h >> 16;
    const int start = static_cast<int>((h >> 4) & (kCbTableSlots - 1));

    for (int i = 0; i < kCbTableSlots; ++i)
    {
        CbEntry& e = g_cbTable[(start + i) & (kCbTableSlots - 1)];
        if (e.original == r)
            return &e;
        if (e.original == nullptr)
        {
            if (!create)
                return nullptr;
            e.original = r;
            e.shadow = nullptr;
            e.bytes = 0;
            e.cameraFrame = 0;
            e.lightFrame = 0;
            return &e;
        }
    }
    if (create)
        g_cCbFull.fetch_add(1, std::memory_order_relaxed);
    return nullptr;
}

thread_local uint8_t* t_scratch = nullptr;
thread_local size_t   t_scratchCap = 0;

bool scratch_for(size_t n)
{
    if (t_scratchCap >= n)
        return true;
    uint8_t* p = static_cast<uint8_t*>(realloc(t_scratch, n));
    if (p == nullptr)
        return false;
    t_scratch = p;
    t_scratchCap = n;
    return true;
}

/* The shadow to bind in place of this buffer, or null when what it holds now is
   not the main view's. */
ID3D11Buffer* cb_shadow_live(ID3D11Buffer* b)
{
    if (b == nullptr)
        return nullptr;
    CbEntry* e = cb_find(static_cast<ID3D11Resource*>(b), false);
    if (e == nullptr || e->shadow == nullptr || e->cameraFrame != g_frame)
        return nullptr;
    return e->shadow;
}

/* Does this buffer hold, right now, a LIGHT view taken at the player's position -
   the shadow cascades' per-view constants? */
bool cb_light_live(ID3D11Buffer* b)
{
    if (b == nullptr)
        return false;
    CbEntry* e = cb_find(static_cast<ID3D11Resource*>(b), false);
    return e != nullptr && e->lightFrame == g_frame && e->lightFrame != 0;
}

// ---------------------------------------------------------------------------
// shader declarations - which slots a shader can actually read or write
// ---------------------------------------------------------------------------

enum : uint32_t { DECL_KNOWN = 1u, DECL_UAV_HIGH = 2u };

struct ShaderDecl
{
    uint32_t flags;
    uint32_t cb;         /* bit per constant-buffer slot, 0..13 */
    uint32_t uav;        /* bit per UAV slot, 0..31 */
    uint32_t pad;
    uint64_t t[2];       /* bit per texture/buffer slot, 0..127 */
};

uint32_t rd32(const uint8_t* p)
{
    uint32_t v;
    memcpy(&v, p, 4);
    return v;
}

/* The declarations, straight out of the tokenized program - present whether or
   not the reflection chunk was stripped. SM 5.1 register spaces make the first
   index a range id rather than a slot, so those are not guessed at. */
bool parse_program(const uint32_t* p, uint32_t n, ShaderDecl& out)
{
    if (n < 2)
        return false;
    const uint32_t major = (p[0] >> 4) & 0xF;
    const uint32_t minor = p[0] & 0xF;
    if (major > 5 || (major == 5 && minor >= 1))
        return false;

    uint32_t len = p[1];
    if (len > n)
        len = n;

    uint32_t i = 2;
    while (i < len)
    {
        const uint32_t tok = p[i];
        const uint32_t opc = tok & 0x7FF;
        uint32_t ilen;
        if (opc == 0x35)                       /* customdata: length follows */
        {
            if (i + 1 >= len)
                return false;
            ilen = p[i + 1];
        }
        else
        {
            ilen = (tok >> 24) & 0x7F;
        }
        if (ilen == 0 || i + ilen > len)
            return false;

        const bool isCb  = opc == 0x59;
        const bool isRes = opc == 0x58 || opc == 0xA1 || opc == 0xA2;
        const bool isUav = opc == 0x9C || opc == 0x9D || opc == 0x9E;
        if (isCb || isRes || isUav)
        {
            uint32_t j = i + 1;
            uint32_t t = tok;
            while ((t & 0x80000000u) != 0 && j < i + ilen)   /* extended opcodes */
                t = p[j++];
            if (j >= i + ilen)
                return false;

            const uint32_t op = p[j++];
            const uint32_t type = (op >> 12) & 0xFF;
            const uint32_t idxDim = (op >> 20) & 3;
            const uint32_t rep0 = (op >> 22) & 7;
            if ((op & 0x80000000u) != 0)
                ++j;                                          /* extended operand */
            if (idxDim < 1 || rep0 != 0 || j >= i + ilen)
                return false;

            const uint32_t reg = p[j];
            if (isCb)
            {
                if (type != 8 || reg >= 14)
                    return false;
                out.cb |= 1u << reg;
            }
            else if (isRes)
            {
                if (type != 7 || reg >= 128)
                    return false;
                out.t[reg >> 6] |= 1ull << (reg & 63);
            }
            else
            {
                if (type != 30 || reg >= 32)
                    return false;
                if (reg >= kStereoOutSlots)
                    out.flags |= DECL_UAV_HIGH;
                out.uav |= 1u << reg;
            }
        }
        i += ilen;
    }
    return true;
}

bool parse_decl(const void* code, size_t len, ShaderDecl& out)
{
    memset(&out, 0, sizeof(out));
    const uint8_t* b = static_cast<const uint8_t*>(code);
    if (b == nullptr || len < 32 || memcmp(b, "DXBC", 4) != 0)
        return false;

    const uint32_t nChunks = rd32(b + 28);
    if (32ull + 4ull * nChunks > len)
        return false;

    for (uint32_t k = 0; k < nChunks; ++k)
    {
        const uint32_t off = rd32(b + 32 + 4 * k);
        if (off + 8ull > len)
            continue;
        const uint32_t size = rd32(b + off + 4);
        if (off + 8ull + size > len)
            continue;
        if (memcmp(b + off, "SHEX", 4) == 0 || memcmp(b + off, "SHDR", 4) == 0)
        {
            if (!parse_program(reinterpret_cast<const uint32_t*>(b + off + 8), size / 4, out))
                return false;
            out.flags |= DECL_KNOWN;
            return true;
        }
    }
    return false;
}

/* The reflection chunk's view of the same thing - used only to check the parse
   above against, on the first few hundred shaders, and say so in the log. */
using PFN_D3DReflect = HRESULT(WINAPI*)(LPCVOID, SIZE_T, REFIID, void**);

bool reflect_decl(const void* code, size_t len, ShaderDecl& out)
{
    static PFN_D3DReflect fn = [] {
        HMODULE m = LoadLibraryW(L"d3dcompiler_47.dll");
        return m ? reinterpret_cast<PFN_D3DReflect>(GetProcAddress(m, "D3DReflect")) : nullptr;
    }();
    if (fn == nullptr)
        return false;

    ID3D11ShaderReflection* r = nullptr;
    if (FAILED(fn(code, len, kIidReflect, reinterpret_cast<void**>(&r))) || r == nullptr)
        return false;

    memset(&out, 0, sizeof(out));
    D3D11_SHADER_DESC sd = {};
    bool ok = SUCCEEDED(r->GetDesc(&sd));
    for (UINT i = 0; ok && i < sd.BoundResources; ++i)
    {
        D3D11_SHADER_INPUT_BIND_DESC bd = {};
        if (FAILED(r->GetResourceBindingDesc(i, &bd)))
            continue;
        const UINT count = bd.BindCount ? bd.BindCount : 1;
        for (UINT k = 0; k < count; ++k)
        {
            const UINT slot = bd.BindPoint + k;
            switch (bd.Type)
            {
            case D3D_SIT_CBUFFER:
                if (slot < 14) out.cb |= 1u << slot;
                break;
            case D3D_SIT_SAMPLER:
                break;
            case D3D_SIT_TBUFFER:
            case D3D_SIT_TEXTURE:
            case D3D_SIT_STRUCTURED:
            case D3D_SIT_BYTEADDRESS:
                if (slot < 128) out.t[slot >> 6] |= 1ull << (slot & 63);
                break;
            default:
                if (slot < 32) out.uav |= 1u << slot;
                break;
            }
        }
    }
    r->Release();
    return ok;
}

bool decl_of(ID3D11DeviceChild* sh, ShaderDecl& d)
{
    UINT size = sizeof(d);
    if (FAILED(sh->GetPrivateData(kShaderDecl, &size, &d)) || size != sizeof(d) ||
        (d.flags & DECL_KNOWN) == 0)
        return false;
    return true;
}

// ---------------------------------------------------------------------------
// surface state and twins - PER SUBRESOURCE (TEST 181)
//
// TEST 165 kept one state per RESOURCE. The shadow atlas is a four-slice
// Texture2DArray that the engine clears one slice at a time, and "a clear of the
// whole surface returns it to SYNC" never fired for it: no single clear covers
// all four slices. One diverged write at any moment since the mission began left
// the atlas DIVERGED for good, and every cascade draw depth-tested against it
// counted as view-dependent and ran twice. The 20:28 log re-issued 93-97% of the
// frame; the same rule replayed on that run's own capture, from clean states,
// re-issues 57.7%. The ~800 ops a frame between the two are the cascades.
//
// So the state is one bit per mip level and array slice, in D3D11CalcSubresource
// order, and every view is resolved to the subresources it covers. A resource
// with more than 64 subresources keeps one bit for the lot - TEST 165's
// behaviour, and nothing that large appears in the measured frame.
// ---------------------------------------------------------------------------

uint32_t format_bits(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_R32G32B32A32_TYPELESS: case DXGI_FORMAT_R32G32B32A32_FLOAT:
    case DXGI_FORMAT_R32G32B32A32_UINT:     case DXGI_FORMAT_R32G32B32A32_SINT:
        return 128;
    case DXGI_FORMAT_R32G32B32_TYPELESS: case DXGI_FORMAT_R32G32B32_FLOAT:
    case DXGI_FORMAT_R32G32B32_UINT:     case DXGI_FORMAT_R32G32B32_SINT:
        return 96;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: case DXGI_FORMAT_R16G16B16A16_FLOAT:
    case DXGI_FORMAT_R16G16B16A16_UNORM:    case DXGI_FORMAT_R16G16B16A16_UINT:
    case DXGI_FORMAT_R16G16B16A16_SNORM:    case DXGI_FORMAT_R16G16B16A16_SINT:
    case DXGI_FORMAT_R32G32_TYPELESS: case DXGI_FORMAT_R32G32_FLOAT:
    case DXGI_FORMAT_R32G32_UINT:     case DXGI_FORMAT_R32G32_SINT:
    case DXGI_FORMAT_R32G8X24_TYPELESS:        case DXGI_FORMAT_D32_FLOAT_S8X24_UINT:
    case DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS: case DXGI_FORMAT_X32_TYPELESS_G8X24_UINT:
        return 64;
    case DXGI_FORMAT_R8G8_TYPELESS: case DXGI_FORMAT_R8G8_UNORM: case DXGI_FORMAT_R8G8_UINT:
    case DXGI_FORMAT_R8G8_SNORM:    case DXGI_FORMAT_R8G8_SINT:
    case DXGI_FORMAT_R16_TYPELESS:  case DXGI_FORMAT_R16_FLOAT: case DXGI_FORMAT_D16_UNORM:
    case DXGI_FORMAT_R16_UNORM:     case DXGI_FORMAT_R16_UINT:  case DXGI_FORMAT_R16_SNORM:
    case DXGI_FORMAT_R16_SINT:      case DXGI_FORMAT_B5G6R5_UNORM:
    case DXGI_FORMAT_B5G5R5A1_UNORM: case DXGI_FORMAT_B4G4R4A4_UNORM:
        return 16;
    case DXGI_FORMAT_R8_TYPELESS: case DXGI_FORMAT_R8_UNORM: case DXGI_FORMAT_R8_UINT:
    case DXGI_FORMAT_R8_SNORM:    case DXGI_FORMAT_R8_SINT:  case DXGI_FORMAT_A8_UNORM:
        return 8;
    default:
        return 32;
    }
}

/* What a twin really costs. TEST 164/165 counted every texel as 4 bytes, which
   undercounts the 64-bit depth and half-float targets that most twins are. */
uint64_t texture2d_bytes(const D3D11_TEXTURE2D_DESC& d)
{
    const uint64_t bits = format_bits(d.Format);
    const UINT mips = d.MipLevels ? d.MipLevels : 1;
    uint64_t total = 0;
    for (UINT m = 0; m < mips; ++m)
    {
        const uint64_t w = (d.Width >> m) ? (d.Width >> m) : 1;
        const uint64_t h = (d.Height >> m) ? (d.Height >> m) : 1;
        total += (w * h * bits + 7) / 8;
    }
    return total * (d.ArraySize ? d.ArraySize : 1) *
           (d.SampleDesc.Count ? d.SampleDesc.Count : 1);
}

struct ResInfo
{
    uint32_t mips;
    uint32_t slices;
    bool     collapsed;   /* more than 64 subresources: one bit stands for all */
};

bool res_info(ID3D11Resource* r, ResInfo& ri)
{
    ri.mips = 1;
    ri.slices = 1;
    ri.collapsed = false;
    if (r == nullptr)
        return false;

    D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
    r->GetType(&dim);
    switch (dim)
    {
    case D3D11_RESOURCE_DIMENSION_TEXTURE2D:
    {
        ID3D11Texture2D* t = nullptr;
        if (FAILED(r->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&t))) ||
            t == nullptr)
            return false;
        D3D11_TEXTURE2D_DESC d = {};
        t->GetDesc(&d);
        t->Release();
        ri.mips = d.MipLevels ? d.MipLevels : 1;
        ri.slices = d.ArraySize ? d.ArraySize : 1;
        break;
    }
    case D3D11_RESOURCE_DIMENSION_TEXTURE1D:
    {
        ID3D11Texture1D* t = nullptr;
        if (FAILED(r->QueryInterface(__uuidof(ID3D11Texture1D), reinterpret_cast<void**>(&t))) ||
            t == nullptr)
            return false;
        D3D11_TEXTURE1D_DESC d = {};
        t->GetDesc(&d);
        t->Release();
        ri.mips = d.MipLevels ? d.MipLevels : 1;
        ri.slices = d.ArraySize ? d.ArraySize : 1;
        break;
    }
    case D3D11_RESOURCE_DIMENSION_TEXTURE3D:
    {
        ID3D11Texture3D* t = nullptr;
        if (FAILED(r->QueryInterface(__uuidof(ID3D11Texture3D), reinterpret_cast<void**>(&t))) ||
            t == nullptr)
            return false;
        D3D11_TEXTURE3D_DESC d = {};
        t->GetDesc(&d);
        t->Release();
        ri.mips = d.MipLevels ? d.MipLevels : 1;
        break;
    }
    case D3D11_RESOURCE_DIMENSION_BUFFER:
        break;
    default:
        return false;
    }
    ri.collapsed = static_cast<uint64_t>(ri.mips) * ri.slices > 64u;
    return true;
}

uint64_t all_bits(const ResInfo& ri)
{
    if (ri.collapsed)
        return 1ull;
    const uint32_t n = ri.mips * ri.slices;
    return n >= 64u ? ~0ull : ((1ull << n) - 1ull);
}

/* Mips [mip0, mip0+nmip) of slices [slice0, slice0+nslice), clamped to what the
   resource has. A count of 0 or past the end (a view's -1) means "to the end". */
uint64_t range_bits(const ResInfo& ri, uint32_t mip0, uint32_t nmip, uint32_t slice0,
                    uint32_t nslice)
{
    if (ri.collapsed)
        return 1ull;
    if (mip0 >= ri.mips)
        mip0 = 0;
    if (nmip == 0 || nmip > ri.mips - mip0)
        nmip = ri.mips - mip0;
    if (slice0 >= ri.slices)
        slice0 = 0;
    if (nslice == 0 || nslice > ri.slices - slice0)
        nslice = ri.slices - slice0;

    uint64_t m = 0;
    for (uint32_t s = slice0; s < slice0 + nslice; ++s)
        for (uint32_t k = mip0; k < mip0 + nmip; ++k)
            m |= 1ull << (k + s * ri.mips);
    return m;
}

uint64_t sub_bit(const ResInfo& ri, UINT sub)
{
    if (ri.collapsed)
        return 1ull;
    return sub < 64u ? (1ull << sub) : 0ull;
}

int bit_index(uint64_t m)
{
    unsigned long i;
    _BitScanForward64(&i, m);
    return static_cast<int>(i);
}

/* Which subresources a view covers, worked out once and kept on the view - it
   can never change, and the view keeps its resource alive, so the pointer here
   stays valid for exactly as long as the view does. */
struct ViewInfo
{
    ID3D11Resource* res;    /* not AddRef'd: the view holds it */
    uint64_t        mask;   /* the subresources this view covers */
    uint64_t        all;    /* every tracked bit of the resource */
};

/* kind: 0 RTV, 1 DSV, 2 UAV, 3 SRV - the same numbering as twin_view_generic. */
bool view_info(ID3D11View* v, int kind, ViewInfo& vi)
{
    vi.res = nullptr;
    vi.mask = 0;
    vi.all = 0;
    if (v == nullptr)
        return false;

    UINT size = sizeof(vi);
    if (SUCCEEDED(v->GetPrivateData(kViewInfo, &size, &vi)) && size == sizeof(vi) &&
        vi.res != nullptr)
        return true;

    ID3D11Resource* r = nullptr;
    v->GetResource(&r);
    if (r == nullptr)
        return false;
    r->Release();                 /* the view keeps it alive */

    ResInfo ri;
    const bool known = res_info(r, ri);
    const uint64_t all = known ? all_bits(ri) : 1ull;
    uint64_t mask = all;

    if (known && !ri.collapsed)
    {
        switch (kind)
        {
        case 0:
        {
            D3D11_RENDER_TARGET_VIEW_DESC d = {};
            static_cast<ID3D11RenderTargetView*>(v)->GetDesc(&d);
            switch (d.ViewDimension)
            {
            case D3D11_RTV_DIMENSION_TEXTURE1D:
                mask = range_bits(ri, d.Texture1D.MipSlice, 1, 0, 1);
                break;
            case D3D11_RTV_DIMENSION_TEXTURE1DARRAY:
                mask = range_bits(ri, d.Texture1DArray.MipSlice, 1,
                                  d.Texture1DArray.FirstArraySlice, d.Texture1DArray.ArraySize);
                break;
            case D3D11_RTV_DIMENSION_TEXTURE2D:
                mask = range_bits(ri, d.Texture2D.MipSlice, 1, 0, 1);
                break;
            case D3D11_RTV_DIMENSION_TEXTURE2DARRAY:
                mask = range_bits(ri, d.Texture2DArray.MipSlice, 1,
                                  d.Texture2DArray.FirstArraySlice, d.Texture2DArray.ArraySize);
                break;
            case D3D11_RTV_DIMENSION_TEXTURE2DMS:
                mask = range_bits(ri, 0, 1, 0, 1);
                break;
            case D3D11_RTV_DIMENSION_TEXTURE2DMSARRAY:
                mask = range_bits(ri, 0, 1, d.Texture2DMSArray.FirstArraySlice,
                                  d.Texture2DMSArray.ArraySize);
                break;
            case D3D11_RTV_DIMENSION_TEXTURE3D:
                mask = range_bits(ri, d.Texture3D.MipSlice, 1, 0, 1);
                break;
            default:
                break;
            }
            break;
        }
        case 1:
        {
            D3D11_DEPTH_STENCIL_VIEW_DESC d = {};
            static_cast<ID3D11DepthStencilView*>(v)->GetDesc(&d);
            switch (d.ViewDimension)
            {
            case D3D11_DSV_DIMENSION_TEXTURE1D:
                mask = range_bits(ri, d.Texture1D.MipSlice, 1, 0, 1);
                break;
            case D3D11_DSV_DIMENSION_TEXTURE1DARRAY:
                mask = range_bits(ri, d.Texture1DArray.MipSlice, 1,
                                  d.Texture1DArray.FirstArraySlice, d.Texture1DArray.ArraySize);
                break;
            case D3D11_DSV_DIMENSION_TEXTURE2D:
                mask = range_bits(ri, d.Texture2D.MipSlice, 1, 0, 1);
                break;
            case D3D11_DSV_DIMENSION_TEXTURE2DARRAY:
                mask = range_bits(ri, d.Texture2DArray.MipSlice, 1,
                                  d.Texture2DArray.FirstArraySlice, d.Texture2DArray.ArraySize);
                break;
            case D3D11_DSV_DIMENSION_TEXTURE2DMS:
                mask = range_bits(ri, 0, 1, 0, 1);
                break;
            case D3D11_DSV_DIMENSION_TEXTURE2DMSARRAY:
                mask = range_bits(ri, 0, 1, d.Texture2DMSArray.FirstArraySlice,
                                  d.Texture2DMSArray.ArraySize);
                break;
            default:
                break;
            }
            break;
        }
        case 2:
        {
            D3D11_UNORDERED_ACCESS_VIEW_DESC d = {};
            static_cast<ID3D11UnorderedAccessView*>(v)->GetDesc(&d);
            switch (d.ViewDimension)
            {
            case D3D11_UAV_DIMENSION_TEXTURE1D:
                mask = range_bits(ri, d.Texture1D.MipSlice, 1, 0, 1);
                break;
            case D3D11_UAV_DIMENSION_TEXTURE1DARRAY:
                mask = range_bits(ri, d.Texture1DArray.MipSlice, 1,
                                  d.Texture1DArray.FirstArraySlice, d.Texture1DArray.ArraySize);
                break;
            case D3D11_UAV_DIMENSION_TEXTURE2D:
                mask = range_bits(ri, d.Texture2D.MipSlice, 1, 0, 1);
                break;
            case D3D11_UAV_DIMENSION_TEXTURE2DARRAY:
                mask = range_bits(ri, d.Texture2DArray.MipSlice, 1,
                                  d.Texture2DArray.FirstArraySlice, d.Texture2DArray.ArraySize);
                break;
            case D3D11_UAV_DIMENSION_TEXTURE3D:
                mask = range_bits(ri, d.Texture3D.MipSlice, 1, 0, 1);
                break;
            default:
                break;
            }
            break;
        }
        default:
        {
            D3D11_SHADER_RESOURCE_VIEW_DESC d = {};
            static_cast<ID3D11ShaderResourceView*>(v)->GetDesc(&d);
            switch (d.ViewDimension)
            {
            case D3D11_SRV_DIMENSION_TEXTURE1D:
                mask = range_bits(ri, d.Texture1D.MostDetailedMip, d.Texture1D.MipLevels, 0, 1);
                break;
            case D3D11_SRV_DIMENSION_TEXTURE1DARRAY:
                mask = range_bits(ri, d.Texture1DArray.MostDetailedMip,
                                  d.Texture1DArray.MipLevels, d.Texture1DArray.FirstArraySlice,
                                  d.Texture1DArray.ArraySize);
                break;
            case D3D11_SRV_DIMENSION_TEXTURE2D:
                mask = range_bits(ri, d.Texture2D.MostDetailedMip, d.Texture2D.MipLevels, 0, 1);
                break;
            case D3D11_SRV_DIMENSION_TEXTURE2DARRAY:
                mask = range_bits(ri, d.Texture2DArray.MostDetailedMip,
                                  d.Texture2DArray.MipLevels, d.Texture2DArray.FirstArraySlice,
                                  d.Texture2DArray.ArraySize);
                break;
            case D3D11_SRV_DIMENSION_TEXTURE2DMS:
                mask = range_bits(ri, 0, 1, 0, 1);
                break;
            case D3D11_SRV_DIMENSION_TEXTURE2DMSARRAY:
                mask = range_bits(ri, 0, 1, d.Texture2DMSArray.FirstArraySlice,
                                  d.Texture2DMSArray.ArraySize);
                break;
            case D3D11_SRV_DIMENSION_TEXTURE3D:
                mask = range_bits(ri, d.Texture3D.MostDetailedMip, d.Texture3D.MipLevels, 0, 1);
                break;
            case D3D11_SRV_DIMENSION_TEXTURECUBE:
                mask = range_bits(ri, d.TextureCube.MostDetailedMip, d.TextureCube.MipLevels, 0, 6);
                break;
            case D3D11_SRV_DIMENSION_TEXTURECUBEARRAY:
                mask = range_bits(ri, d.TextureCubeArray.MostDetailedMip,
                                  d.TextureCubeArray.MipLevels,
                                  d.TextureCubeArray.First2DArrayFace,
                                  d.TextureCubeArray.NumCubes * 6u);
                break;
            default:
                break;
            }
            break;
        }
        }
    }

    vi.res = r;
    vi.mask = mask != 0 ? mask : all;
    vi.all = all;
    v->SetPrivateData(kViewInfo, sizeof(vi), &vi);
    return true;
}

/* One bit per tracked subresource in each of three sets; a bit in none of them
   has no twin contents to speak of (NONE):
     div    the twin holds the OTHER eye's version - reading it is per-eye
     sync   the twin holds exactly what the original holds
     stale  the twin is out of date; the original's contents are shared */
struct SurfState
{
    uint32_t epoch;
    uint32_t pad;
    uint64_t div;
    uint64_t sync;
    uint64_t stale;
};

/* The state under the current epoch, or all-NONE (and false). */
bool state_get(ID3D11Resource* r, SurfState& s)
{
    UINT size = sizeof(s);
    if (r == nullptr || FAILED(r->GetPrivateData(kSurfState, &size, &s)) ||
        size != sizeof(s) || s.epoch != g_epoch.load(std::memory_order_relaxed))
    {
        memset(&s, 0, sizeof(s));
        return false;
    }
    return true;
}

void state_put(ID3D11Resource* r, SurfState& s)
{
    s.epoch = g_epoch.load(std::memory_order_relaxed);
    s.pad = 0;
    r->SetPrivateData(kSurfState, sizeof(s), &s);
}

uint64_t div_of(ID3D11Resource* r)
{
    SurfState s;
    return state_get(r, s) ? s.div : 0ull;
}

ID3D11Resource* twin_lookup(ID3D11Resource* r)
{
    if (r == nullptr)
        return nullptr;
    ID3D11Resource* twin = nullptr;
    UINT size = sizeof(twin);
    if (SUCCEEDED(r->GetPrivateData(kTwinRes, &size, &twin)) && twin != nullptr)
        return twin;      /* AddRef'd by GetPrivateData - the caller releases */
    return nullptr;
}

void copy_raw(ID3D11DeviceContext* ctx, ID3D11Resource* dst, ID3D11Resource* src)
{
    const bool was = t_inDup;
    t_inDup = true;
    ctx->CopyResource(dst, src);
    t_inDup = was;
}

void copy_sub_raw(ID3D11DeviceContext* ctx, ID3D11Resource* dst, UINT dstSub, UINT x, UINT y,
                  UINT z, ID3D11Resource* src, UINT srcSub, const D3D11_BOX* box)
{
    const bool was = t_inDup;
    t_inDup = true;
    ctx->CopySubresourceRegion(dst, dstSub, x, y, z, src, srcSub, box);
    t_inDup = was;
}

/* The given subresources of the twin brought up to the original's contents. */
/* WHAT THE TWINS COST TO KEEP IN STEP (TEST 182). Every copy from an original
   into its twin lands here. TEST 181 re-synced 150-200 surfaces a second, and a
   full-size copy does not get cheaper when DLSS renders fewer pixels - so these
   are counted by surface, with the bytes, and printed beside the pass census. */
struct SyncRow
{
    ID3D11Resource* key;      /* compared, never dereferenced after insertion */
    uint32_t        w, h, fmt;
    uint64_t        copies;
    uint64_t        bytes;
};

constexpr int kSyncRows = 32;
SyncRow  g_syncRows[kSyncRows] = {};
int      g_syncRowCount = 0;

void sync_note(ID3D11Resource* r, const ResInfo& ri, uint64_t bits)
{
    uint32_t w = 0, h = 0, fmt = 0;
    uint64_t bytes = 0;

    D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
    r->GetType(&dim);
    if (dim == D3D11_RESOURCE_DIMENSION_TEXTURE2D)
    {
        ID3D11Texture2D* t = nullptr;
        if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&t))) &&
            t != nullptr)
        {
            D3D11_TEXTURE2D_DESC d = {};
            t->GetDesc(&d);
            t->Release();
            w = d.Width;
            h = d.Height;
            fmt = static_cast<uint32_t>(d.Format);
            const uint64_t bpp = format_bits(d.Format);
            if (ri.collapsed || bits == all_bits(ri))
                bytes = texture2d_bytes(d);
            else
            {
                uint64_t m = bits;
                while (m != 0)
                {
                    const uint32_t i = static_cast<uint32_t>(bit_index(m));
                    m &= m - 1;
                    const uint32_t mip = i % ri.mips;
                    const uint64_t mw = (d.Width >> mip) ? (d.Width >> mip) : 1;
                    const uint64_t mh = (d.Height >> mip) ? (d.Height >> mip) : 1;
                    bytes += (mw * mh * bpp + 7) / 8;
                }
            }
        }
    }
    else if (dim == D3D11_RESOURCE_DIMENSION_BUFFER)
    {
        ID3D11Buffer* b = nullptr;
        if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Buffer), reinterpret_cast<void**>(&b))) &&
            b != nullptr)
        {
            D3D11_BUFFER_DESC d = {};
            b->GetDesc(&d);
            b->Release();
            w = d.ByteWidth;
            bytes = d.ByteWidth;
        }
    }

    g_syncBytes.fetch_add(bytes, std::memory_order_relaxed);

    const uint32_t renderThread = present_thread_id();
    if (renderThread != 0 && GetCurrentThreadId() != renderThread)
        return;
    for (int i = 0; i < g_syncRowCount; ++i)
    {
        if (g_syncRows[i].key == r)
        {
            ++g_syncRows[i].copies;
            g_syncRows[i].bytes += bytes;
            return;
        }
    }
    if (g_syncRowCount >= kSyncRows)
        return;
    SyncRow& row = g_syncRows[g_syncRowCount++];
    row.key = r;
    row.w = w;
    row.h = h;
    row.fmt = fmt;
    row.copies = 1;
    row.bytes = bytes;
}

void twin_fill(ID3D11DeviceContext* ctx, ID3D11Resource* twin, ID3D11Resource* r,
               const ResInfo& ri, uint64_t bits)
{
    if (bits == 0)
        return;
    sync_note(r, ri, bits);
    if (ri.collapsed || bits == all_bits(ri))
    {
        copy_raw(ctx, twin, r);
        return;
    }
    uint64_t m = bits;
    while (m != 0)
    {
        const UINT i = static_cast<UINT>(bit_index(m));
        m &= m - 1;
        copy_sub_raw(ctx, twin, i, 0, 0, 0, r, i, nullptr);
    }
}

/* The second eye's copy of a resource, created on first need and NOT filled -
   the caller says which subresources must hold the original's contents. AddRef'd.
   Null when it cannot exist; `readback` is then set when that is because the
   resource is the CPU's end of a readback (staging or dynamic) - expected, and
   the engine then reads eye 0's value, which is what its CPU logic wants - rather
   than a failure. */
ID3D11Resource* twin_create(ID3D11Resource* r, bool& readback)
{
    readback = false;
    if (r == nullptr)
        return nullptr;

    ID3D11Resource* twin = twin_lookup(r);
    if (twin != nullptr)
        return twin;

    ID3D11Device* dev = engine_device();
    if (dev == nullptr)
        return nullptr;

    D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
    r->GetType(&dim);

    constexpr UINT kStripMisc = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX |
                                D3D11_RESOURCE_MISC_GDI_COMPATIBLE | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;

    ID3D11Resource* made = nullptr;
    uint64_t bytes = 0;

    if (dim == D3D11_RESOURCE_DIMENSION_TEXTURE2D)
    {
        ID3D11Texture2D* t = nullptr;
        if (FAILED(r->QueryInterface(__uuidof(ID3D11Texture2D),
                                     reinterpret_cast<void**>(&t))) || t == nullptr)
            return nullptr;
        D3D11_TEXTURE2D_DESC d = {};
        t->GetDesc(&d);
        t->Release();
        if (d.Usage != D3D11_USAGE_DEFAULT)
        {
            readback = d.Usage == D3D11_USAGE_STAGING || d.Usage == D3D11_USAGE_DYNAMIC;
            if (!readback)
                g_cTwinFail.fetch_add(1, std::memory_order_relaxed);
            return nullptr;
        }
        d.MiscFlags &= ~kStripMisc;

        ID3D11Texture2D* copy = nullptr;
        if (FAILED(dev->CreateTexture2D(&d, nullptr, &copy)) || copy == nullptr)
        {
            g_cTwinFail.fetch_add(1, std::memory_order_relaxed);
            return nullptr;
        }
        made = copy;
        bytes = texture2d_bytes(d);
    }
    else if (dim == D3D11_RESOURCE_DIMENSION_BUFFER)
    {
        ID3D11Buffer* b = nullptr;
        if (FAILED(r->QueryInterface(__uuidof(ID3D11Buffer),
                                     reinterpret_cast<void**>(&b))) || b == nullptr)
            return nullptr;
        D3D11_BUFFER_DESC d = {};
        b->GetDesc(&d);
        b->Release();
        if (d.Usage != D3D11_USAGE_DEFAULT)
        {
            readback = d.Usage == D3D11_USAGE_STAGING || d.Usage == D3D11_USAGE_DYNAMIC;
            if (!readback)
                g_cTwinFail.fetch_add(1, std::memory_order_relaxed);
            return nullptr;
        }
        d.MiscFlags &= ~kStripMisc;

        ID3D11Buffer* copy = nullptr;
        if (FAILED(dev->CreateBuffer(&d, nullptr, &copy)) || copy == nullptr)
        {
            g_cTwinFail.fetch_add(1, std::memory_order_relaxed);
            return nullptr;
        }
        made = copy;
        bytes = d.ByteWidth;
    }
    else
    {
        g_cTwinFail.fetch_add(1, std::memory_order_relaxed);
        return nullptr;
    }

    r->SetPrivateDataInterface(kTwinRes, made);
    g_cTwins.fetch_add(1, std::memory_order_relaxed);
    g_twinBytes.fetch_add(bytes, std::memory_order_relaxed);
    return made;      /* the reference CreateX gave us belongs to the caller */
}

/* EVERY SUBRESOURCE A VIEW-DEPENDENT OPERATION WRITES MUST HOLD THE RIGHT PIXELS
   BEFORE IT RUNS. One that is untracked or STALE gets the original's contents
   now, while the original still holds what the frame put there before this
   operation - the clear, an earlier pass, the base a blend adds to. DIVERGED and
   SYNC ones are already right. False when the twin cannot exist. */
bool prepare_write(ID3D11DeviceContext* c, ID3D11Resource* r, uint64_t mask)
{
    SurfState s;
    state_get(r, s);
    const uint64_t need = mask & ~(s.div | s.sync);
    if (need == 0)
        return true;

    ResInfo ri;
    if (!res_info(r, ri))
        return false;

    bool readback = false;
    ID3D11Resource* twin = twin_create(r, readback);
    if (twin == nullptr)
        return false;
    twin_fill(c, twin, r, ri, need);
    twin->Release();

    s.sync |= need;
    s.stale &= ~need;
    state_put(r, s);
    g_cSync.fetch_add(1, std::memory_order_relaxed);
    return true;
}

void mark_div(ID3D11Resource* r, uint64_t mask)
{
    SurfState s;
    state_get(r, s);
    s.div |= mask;
    s.sync &= ~mask;
    s.stale &= ~mask;
    state_put(r, s);
}

/* A write the second eye does not repeat: the twin's copy of those subresources
   is out of date from here on. */
void mark_shared_write(ID3D11Resource* r, uint64_t mask)
{
    SurfState s;
    if (!state_get(r, s))
        return;
    const uint64_t moved = (s.div | s.sync) & mask;
    if (moved == 0)
        return;
    s.stale |= moved;
    s.div &= ~moved;
    s.sync &= ~moved;
    state_put(r, s);
}

/* A view of the twin, matching the view given, attached to that view so it dies
   with it. The twin resource must already exist. */
ID3D11View* twin_view_generic(ID3D11View* v, int kind)
{
    if (v == nullptr)
        return nullptr;

    ID3D11View* twin = nullptr;
    UINT size = sizeof(twin);
    if (SUCCEEDED(v->GetPrivateData(kTwinView, &size, &twin)) && twin != nullptr)
        return twin;

    ID3D11Resource* res = nullptr;
    v->GetResource(&res);
    if (res == nullptr)
        return nullptr;

    ID3D11Resource* twinRes = twin_lookup(res);
    res->Release();
    if (twinRes == nullptr)
        return nullptr;

    ID3D11Device* dev = engine_device();
    ID3D11View* made = nullptr;

    if (dev != nullptr)
    {
        switch (kind)
        {
        case 0:
        {
            D3D11_RENDER_TARGET_VIEW_DESC d = {};
            static_cast<ID3D11RenderTargetView*>(v)->GetDesc(&d);
            ID3D11RenderTargetView* out = nullptr;
            dev->CreateRenderTargetView(twinRes, &d, &out);
            made = out;
            break;
        }
        case 1:
        {
            D3D11_DEPTH_STENCIL_VIEW_DESC d = {};
            static_cast<ID3D11DepthStencilView*>(v)->GetDesc(&d);
            ID3D11DepthStencilView* out = nullptr;
            dev->CreateDepthStencilView(twinRes, &d, &out);
            made = out;
            break;
        }
        case 2:
        {
            D3D11_UNORDERED_ACCESS_VIEW_DESC d = {};
            static_cast<ID3D11UnorderedAccessView*>(v)->GetDesc(&d);
            ID3D11UnorderedAccessView* out = nullptr;
            dev->CreateUnorderedAccessView(twinRes, &d, &out);
            made = out;
            break;
        }
        default:
        {
            D3D11_SHADER_RESOURCE_VIEW_DESC d = {};
            static_cast<ID3D11ShaderResourceView*>(v)->GetDesc(&d);
            ID3D11ShaderResourceView* out = nullptr;
            dev->CreateShaderResourceView(twinRes, &d, &out);
            made = out;
            break;
        }
        }
    }

    twinRes->Release();

    if (made == nullptr)
    {
        g_cTwinFail.fetch_add(1, std::memory_order_relaxed);
        return nullptr;
    }

    v->SetPrivateDataInterface(kTwinView, made);
    return made;
}

ID3D11RenderTargetView* twin_rtv(ID3D11RenderTargetView* v)
{
    return static_cast<ID3D11RenderTargetView*>(twin_view_generic(v, 0));
}

ID3D11DepthStencilView* twin_dsv(ID3D11DepthStencilView* v)
{
    return static_cast<ID3D11DepthStencilView*>(twin_view_generic(v, 1));
}

ID3D11UnorderedAccessView* twin_uav(ID3D11UnorderedAccessView* v)
{
    return static_cast<ID3D11UnorderedAccessView*>(twin_view_generic(v, 2));
}

ID3D11ShaderResourceView* twin_srv(ID3D11ShaderResourceView* v)
{
    return static_cast<ID3D11ShaderResourceView*>(twin_view_generic(v, 3));
}

// ---------------------------------------------------------------------------
// WHY IT RAN TWICE - a census by pass, so a sticky taint shows up in the log.
//
// TEST 181's performance fault was only found by replaying a capture offline:
// the live counters said "96% view-dependent" and nothing said which passes or
// why. This keeps, per first output target, how many operations were re-issued
// and the reason the first of them was - printed every 10 s.
// ---------------------------------------------------------------------------

enum : uint32_t { WHY_CB = 1, WHY_SRV = 2, WHY_RTV = 3, WHY_DSV = 4, WHY_UAV = 5 };

uint32_t why_code(uint32_t kind, int stage, int slot)
{
    return kind | (static_cast<uint32_t>(stage) << 4) | (static_cast<uint32_t>(slot) << 8);
}

struct CensusRow
{
    ID3D11Resource* key;    /* compared, never dereferenced after insertion */
    uint32_t        w, h, fmt, why;
    uint64_t        ops;
};

constexpr int kCensusRows = 48;
CensusRow g_census[kCensusRows] = {};
int       g_censusRows = 0;
uint64_t  g_censusOverflow = 0;

void census_note(ID3D11Resource* key, uint32_t why)
{
    const uint32_t renderThread = present_thread_id();
    if (renderThread != 0 && GetCurrentThreadId() != renderThread)
        return;

    for (int i = 0; i < g_censusRows; ++i)
    {
        if (g_census[i].key == key)
        {
            ++g_census[i].ops;
            return;
        }
    }
    if (g_censusRows >= kCensusRows)
    {
        ++g_censusOverflow;
        return;
    }

    CensusRow& row = g_census[g_censusRows++];
    memset(&row, 0, sizeof(row));
    row.key = key;
    row.why = why;
    row.ops = 1;
    if (key == nullptr)
        return;

    D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
    key->GetType(&dim);
    if (dim == D3D11_RESOURCE_DIMENSION_TEXTURE2D)
    {
        ID3D11Texture2D* t = nullptr;
        if (SUCCEEDED(key->QueryInterface(__uuidof(ID3D11Texture2D),
                                          reinterpret_cast<void**>(&t))) && t != nullptr)
        {
            D3D11_TEXTURE2D_DESC d = {};
            t->GetDesc(&d);
            t->Release();
            row.w = d.Width;
            row.h = d.Height;
            row.fmt = static_cast<uint32_t>(d.Format);
        }
    }
    else if (dim == D3D11_RESOURCE_DIMENSION_BUFFER)
    {
        ID3D11Buffer* b = nullptr;
        if (SUCCEEDED(key->QueryInterface(__uuidof(ID3D11Buffer),
                                          reinterpret_cast<void**>(&b))) && b != nullptr)
        {
            D3D11_BUFFER_DESC d = {};
            b->GetDesc(&d);
            b->Release();
            row.w = d.ByteWidth;
        }
    }
}

void census_report()
{
    if (g_censusRows == 0)
        return;

    int order[kCensusRows];
    for (int i = 0; i < g_censusRows; ++i)
        order[i] = i;
    for (int i = 1; i < g_censusRows; ++i)
    {
        const int k = order[i];
        int j = i - 1;
        while (j >= 0 && g_census[order[j]].ops < g_census[k].ops)
        {
            order[j + 1] = order[j];
            --j;
        }
        order[j + 1] = k;
    }

    static const char* const kStage[6] = { "VS", "HS", "DS", "GS", "PS", "CS" };
    char text[2048];
    int used = 0;
    const int show = g_censusRows < 8 ? g_censusRows : 8;
    for (int n = 0; n < show && used < static_cast<int>(sizeof(text)) - 1; ++n)
    {
        const CensusRow& r = g_census[order[n]];
        const uint32_t kind = r.why & 0xFu;
        const uint32_t st = (r.why >> 4) & 0xFu;
        const uint32_t slot = r.why >> 8;
        const char* sn = st < 6u ? kStage[st] : "?";

        char reason[64];
        switch (kind)
        {
        case WHY_CB:  _snprintf_s(reason, _TRUNCATE, "camera constants %s b%u", sn, slot); break;
        case WHY_SRV: _snprintf_s(reason, _TRUNCATE, "per-eye texture %s t%u", sn, slot); break;
        case WHY_RTV: _snprintf_s(reason, _TRUNCATE, "draws into a per-eye target"); break;
        case WHY_DSV: _snprintf_s(reason, _TRUNCATE, "tests a per-eye depth"); break;
        case WHY_UAV: _snprintf_s(reason, _TRUNCATE, "reads a per-eye UAV u%u", slot); break;
        default:      _snprintf_s(reason, _TRUNCATE, "?"); break;
        }

        char shape[48];
        if (r.key == nullptr)
            _snprintf_s(shape, _TRUNCATE, "(no output)");
        else if (r.h == 0)
            _snprintf_s(shape, _TRUNCATE, "buffer %u B", r.w);
        else
            _snprintf_s(shape, _TRUNCATE, "%ux%u fmt %u", r.w, r.h, r.fmt);

        const int wrote = _snprintf_s(text + used, sizeof(text) - used, _TRUNCATE,
                                      "%s%s: %llu (first: %s)", n ? " | " : "", shape,
                                      static_cast<unsigned long long>(r.ops), reason);
        if (wrote < 0)
            break;
        used += wrote;
    }

    BVR_INFO("Stereo passes re-issued in ~10 s, by first target: %s%s", text,
             g_censusOverflow ? " (more targets than the table holds)" : "");

    g_censusRows = 0;
    g_censusOverflow = 0;

    /* ...and what keeping the twins in step copied, largest first. */
    if (g_syncRowCount > 0)
    {
        int ord[kSyncRows];
        for (int i = 0; i < g_syncRowCount; ++i)
            ord[i] = i;
        for (int i = 1; i < g_syncRowCount; ++i)
        {
            const int k = ord[i];
            int j = i - 1;
            while (j >= 0 && g_syncRows[ord[j]].bytes < g_syncRows[k].bytes)
            {
                ord[j + 1] = ord[j];
                --j;
            }
            ord[j + 1] = k;
        }

        char sync[1200];
        int usedSync = 0;
        uint64_t total = 0;
        for (int i = 0; i < g_syncRowCount; ++i)
            total += g_syncRows[i].bytes;
        const int showSync = g_syncRowCount < 6 ? g_syncRowCount : 6;
        for (int n = 0; n < showSync && usedSync < static_cast<int>(sizeof(sync)) - 1; ++n)
        {
            const SyncRow& r = g_syncRows[ord[n]];
            const int wrote = _snprintf_s(sync + usedSync, sizeof(sync) - usedSync, _TRUNCATE,
                                          "%s%ux%u fmt %u: %llu copies, %llu MB", n ? " | " : "",
                                          r.w, r.h, r.fmt,
                                          static_cast<unsigned long long>(r.copies),
                                          static_cast<unsigned long long>(r.bytes >> 20));
            if (wrote < 0)
                break;
            usedSync += wrote;
        }
        BVR_INFO("Stereo twin re-syncs in ~10 s: %llu MB copied from originals into twins; "
                 "largest: %s", static_cast<unsigned long long>(total >> 20), sync);
        g_syncRowCount = 0;
    }
}

// --- stage plumbing ---------------------------------------------------------

ID3D11DeviceChild* get_stage_shader(ID3D11DeviceContext* c, int st)
{
    switch (st)
    {
    case 0: { ID3D11VertexShader* s = nullptr; c->VSGetShader(&s, nullptr, nullptr); return s; }
    case 1: { ID3D11HullShader* s = nullptr; c->HSGetShader(&s, nullptr, nullptr); return s; }
    case 2: { ID3D11DomainShader* s = nullptr; c->DSGetShader(&s, nullptr, nullptr); return s; }
    case 3: { ID3D11GeometryShader* s = nullptr; c->GSGetShader(&s, nullptr, nullptr); return s; }
    case 4: { ID3D11PixelShader* s = nullptr; c->PSGetShader(&s, nullptr, nullptr); return s; }
    default: { ID3D11ComputeShader* s = nullptr; c->CSGetShader(&s, nullptr, nullptr); return s; }
    }
}

void get_stage_cbs(ID3D11DeviceContext* c, int st, UINT lo, UINT n, ID3D11Buffer** out)
{
    switch (st)
    {
    case 0: c->VSGetConstantBuffers(lo, n, out); break;
    case 1: c->HSGetConstantBuffers(lo, n, out); break;
    case 2: c->DSGetConstantBuffers(lo, n, out); break;
    case 3: c->GSGetConstantBuffers(lo, n, out); break;
    case 4: c->PSGetConstantBuffers(lo, n, out); break;
    default: c->CSGetConstantBuffers(lo, n, out); break;
    }
}

void set_stage_cb(ID3D11DeviceContext* c, int st, UINT slot, ID3D11Buffer* b)
{
    switch (st)
    {
    case 0: c->VSSetConstantBuffers(slot, 1, &b); break;
    case 1: c->HSSetConstantBuffers(slot, 1, &b); break;
    case 2: c->DSSetConstantBuffers(slot, 1, &b); break;
    case 3: c->GSSetConstantBuffers(slot, 1, &b); break;
    case 4: c->PSSetConstantBuffers(slot, 1, &b); break;
    default: c->CSSetConstantBuffers(slot, 1, &b); break;
    }
}

void get_stage_srvs(ID3D11DeviceContext* c, int st, UINT lo, UINT n,
                    ID3D11ShaderResourceView** out)
{
    switch (st)
    {
    case 0: c->VSGetShaderResources(lo, n, out); break;
    case 1: c->HSGetShaderResources(lo, n, out); break;
    case 2: c->DSGetShaderResources(lo, n, out); break;
    case 3: c->GSGetShaderResources(lo, n, out); break;
    case 4: c->PSGetShaderResources(lo, n, out); break;
    default: c->CSGetShaderResources(lo, n, out); break;
    }
}

void set_stage_srv(ID3D11DeviceContext* c, int st, UINT slot, ID3D11ShaderResourceView* v)
{
    switch (st)
    {
    case 0: c->VSSetShaderResources(slot, 1, &v); break;
    case 1: c->HSSetShaderResources(slot, 1, &v); break;
    case 2: c->DSSetShaderResources(slot, 1, &v); break;
    case 3: c->GSSetShaderResources(slot, 1, &v); break;
    case 4: c->PSSetShaderResources(slot, 1, &v); break;
    default: c->CSSetShaderResources(slot, 1, &v); break;
    }
}

int lowest_bit(uint64_t m) { unsigned long i; _BitScanForward64(&i, m); return static_cast<int>(i); }
int highest_bit(uint64_t m) { unsigned long i; _BitScanReverse64(&i, m); return static_cast<int>(i); }

bool slot_declared(const ShaderDecl& d, int slot)
{
    return (d.t[slot >> 6] >> (slot & 63)) & 1ull;
}

void release_all(StereoOp& op)
{
    for (auto& v : op.rtv) if (v) v->Release();
    if (op.dsv) op.dsv->Release();
    for (auto& v : op.uav) if (v) v->Release();
    for (auto& v : op.trtv) if (v) v->Release();
    if (op.tdsv) op.tdsv->Release();
    for (auto& v : op.tuav) if (v) v->Release();
    for (int i = 0; i < op.nSwap; ++i)
    {
        StereoSwap& s = op.swap[i];
        if (s.orig)
            static_cast<IUnknown*>(s.orig)->Release();
        if (s.isSrv && s.twin)
            static_cast<IUnknown*>(s.twin)->Release();
    }
    op.nSwap = 0;
    memset(op.rtv, 0, sizeof(op.rtv));
    op.dsv = nullptr;
    memset(op.uav, 0, sizeof(op.uav));
    memset(op.trtv, 0, sizeof(op.trtv));
    op.tdsv = nullptr;
    memset(op.tuav, 0, sizeof(op.tuav));
}

struct DsUse { bool read; bool write; };

DsUse depth_use(ID3D11DeviceContext* c)
{
    DsUse u = { false, false };
    ID3D11DepthStencilState* s = nullptr;
    UINT ref = 0;
    c->OMGetDepthStencilState(&s, &ref);
    if (s == nullptr)
    {
        /* The default state: depth test on, writes on, stencil off. */
        u.read = u.write = true;
        return u;
    }
    D3D11_DEPTH_STENCIL_DESC d = {};
    s->GetDesc(&d);
    s->Release();
    u.read = d.DepthEnable || d.StencilEnable;
    u.write = (d.DepthEnable && d.DepthWriteMask == D3D11_DEPTH_WRITE_MASK_ALL) ||
              (d.StencilEnable && d.StencilWriteMask != 0);
    return u;
}

} // namespace

// ---------------------------------------------------------------------------
// public surface
// ---------------------------------------------------------------------------

void stereo_set_active(bool active)
{
    const bool was = g_active.exchange(active, std::memory_order_relaxed);
    if (was != active)
    {
        /* Nothing kept the twins in step while the mode was off, so no state from
           before is trusted: every twin is refilled from its original on first
           use. See g_epoch. */
        if (active)
            g_epoch.fetch_add(1, std::memory_order_relaxed);

        BVR_INFO(active ? "Stereo duplication ON: every view-dependent operation now "
                          "runs twice, the second eye into twins of whatever it writes."
                        : "Stereo duplication off.");
    }
}

StereoIgnore::StereoIgnore() { ++t_ignore; }
StereoIgnore::~StereoIgnore() { --t_ignore; }

bool stereo_in_dup() { return t_inDup; }

bool stereo_active() { return g_active.load(std::memory_order_relaxed); }

void stereo_note_shader(ID3D11DeviceChild* shader, const void* code, size_t len)
{
    if (shader == nullptr || code == nullptr || len == 0)
        return;

    ShaderDecl d;
    if (parse_decl(code, len, d))
        g_declParsed.fetch_add(1, std::memory_order_relaxed);
    else
    {
        g_declFailed.fetch_add(1, std::memory_order_relaxed);
        memset(&d, 0, sizeof(d));   /* unknown: every bound slot counts */
    }
    shader->SetPrivateData(kShaderDecl, sizeof(d), &d);

    /* Cross-check the parse against the reflection chunk on the first few
       hundred shaders. Disagreement would mean a slot the second eye reads from
       the wrong eye, silently - so it is counted, and the first one described. */
    if ((d.flags & DECL_KNOWN) != 0 &&
        g_declChecked.fetch_add(1, std::memory_order_relaxed) < 400)
    {
        ShaderDecl r;
        if (reflect_decl(code, len, r))
        {
            const bool same = r.cb == d.cb && r.uav == d.uav && r.t[0] == d.t[0] &&
                              r.t[1] == d.t[1];
            if (same)
                g_declAgree.fetch_add(1, std::memory_order_relaxed);
            else if (g_declDiffer.fetch_add(1, std::memory_order_relaxed) == 0)
                BVR_WARN("Stereo: shader declaration parse DISAGREES with reflection - "
                         "cb %04X/%04X uav %X/%X t %016llX%016llX / %016llX%016llX "
                         "(parsed/reflected). A slot missing here is read from the "
                         "wrong eye.",
                         d.cb, r.cb, d.uav, r.uav,
                         static_cast<unsigned long long>(d.t[1]),
                         static_cast<unsigned long long>(d.t[0]),
                         static_cast<unsigned long long>(r.t[1]),
                         static_cast<unsigned long long>(r.t[0]));
        }
    }
}

void stereo_on_cb_upload(ID3D11Resource* resource, const void* data, uint32_t bytes)
{
    /* ignored() matters here too: the second eye's own constant-buffer copies are
       written with UpdateSubresource, which comes straight back through this hook
       and was being re-verified - and counted - as if the engine had uploaded it. */
    if (!g_active.load(std::memory_order_relaxed) || resource == nullptr || data == nullptr ||
        ignored())
        return;

    const Layout* layout = layout_for(bytes);
    if (layout == nullptr)
        return;               /* not a layout that has ever carried a camera */

    g_cCbUpload.fetch_add(1, std::memory_order_relaxed);

    /* Whatever this upload turns out to be, the marks the buffer carried from its
       previous contents no longer describe it. (Before TEST 182 an invalid
       publication returned without clearing them.) */
    if (CbEntry* prior = cb_find(resource, false))
    {
        prior->cameraFrame = 0;
        prior->lightFrame = 0;
    }

    const PubEyes pub = published_eyes();
    if (!pub.valid)
        return;

    const uint8_t* src = static_cast<const uint8_t*>(data);

    /* THE MAIN VIEW, RECOGNISED FROM THE UPLOAD ITSELF (TEST 182).
     *
       TEST 164/165 tested every camera site against the PUBLISHED eye. But the
       engine builds a frame from a pose a publication old, so the moment the
       player moves the published eye is no longer the camera being drawn, and
       the exact site test fails: "0 of 22440 camera uploads were the main view"
       for whole windows of the 22:19 run, "244 of 26210" at 22:23. Every failed
       upload drew that pass's second eye from the FIRST eye's camera - stereo
       that came and went frame to frame.
     *
       per_framef carries its own camera: g_camera_position at +0. The main view
       is the upload whose own view-projection maps that position to w = 0 - a
       test with no timing in it - and whose position is within a couple of
       metres of the published head, which keeps out the far perspective cameras
       the capture also contains (probes ~1 km away). The shadow cascades carry
       the player's position with a LIGHT's orthographic matrices: near, but no
       site verifies - a light view, marked so its passes stay shared.
     *
       The 128- and 224-byte buffers carry no position; they are verified
       against this frame's main camera, then the last frame's, then the
       published eye. Replayed on the 20:28 capture: 32 main, 64 light, 32 far
       per_framef uploads over 4 frames, and on the 22:19 capture the old test
       recognised no main view at all on 3 frames of 4 where this finds 50%. */
    float cam[3] = {};
    float d[3] = {};
    bool main = false;

    if (bytes == 1760)
    {
        memcpy(cam, src, sizeof(cam));
        const float nearM = near_head_m();
        if (!(dist3(cam, pub.cur) <= nearM || dist3(cam, pub.other) <= nearM))
        {
            g_cCbFar.fetch_add(1, std::memory_order_relaxed);
            return;
        }

        for (int i = 0; i < layout->count && !main; ++i)
        {
            const Site& s = layout->sites[i];
            if (s.kind != SITE_FWD || s.offset + 64u > bytes)
                continue;
            main = is_forward_camera(reinterpret_cast<const float*>(src + s.offset), cam);
        }

        if (!main)
        {
            if (CbEntry* e = cb_find(resource, true))
                e->lightFrame = g_frame;
            g_cCbLight.fetch_add(1, std::memory_order_relaxed);
            return;
        }

        /* THE EYE OFFSET FROM THE FRAME'S OWN RIGHT AXIS. g_inverse_view (+928)
           row 0 is the camera's right vector in world space - measured equal to
           the published eye delta's direction (cos 1.000, 65 mm) on the 20:28
           capture. It is the LATCHED camera's, so the second eye sits beside the
           orientation the frame was actually drawn with rather than beside a
           pose a publication old. The published delta only chooses the side. */
        const float* r = reinterpret_cast<const float*>(src + 928);
        const float len = sqrtf(r[0] * r[0] + r[1] * r[1] + r[2] * r[2]);
        if (isfinite(len) && len > 0.9f && len < 1.1f)
        {
            const float cosPub = (r[0] * pub.dir[0] + r[1] * pub.dir[1] + r[2] * pub.dir[2]) / len;
            const float sign = cosPub >= 0.0f ? 1.0f : -1.0f;
            for (int i = 0; i < 3; ++i)
                d[i] = r[i] / len * pub.ipd * sign;
            g_offsetCosSum += fabs(cosPub);
            if (fabs(cosPub) < g_offsetCosMin)
                g_offsetCosMin = fabs(cosPub);
            ++g_offsetCosN;
        }
        else
        {
            memcpy(d, pub.d, sizeof(d));
            g_cOffsetFallback.fetch_add(1, std::memory_order_relaxed);
        }
        note_main_camera(cam, d);
        if (is_forward_camera(reinterpret_cast<const float*>(src + 304), cam))
        {
            memcpy(g_mainVpU, src + 304, sizeof(g_mainVpU));
            g_mainVpUValid = true;
        }
        g_cCbMain1760.fetch_add(1, std::memory_order_relaxed);
    }
    else
    {
        const float* camCand[3] = {};
        const float* dCand[3] = {};
        int n = 0;
        if (g_mainValid)
        {
            camCand[n] = g_mainCam;
            dCand[n++] = g_mainD;
        }
        if (g_prevMainValid)
        {
            camCand[n] = g_prevMainCam;
            dCand[n++] = g_prevMainD;
        }
        camCand[n] = pub.cur;
        dCand[n++] = pub.d;

        for (int k = 0; k < n && !main; ++k)
        {
            for (int i = 0; i < layout->count && !main; ++i)
            {
                const Site& s = layout->sites[i];
                if (s.kind != SITE_FWD || s.offset + 64u > bytes)
                    continue;
                if (is_forward_camera(reinterpret_cast<const float*>(src + s.offset), camCand[k]))
                {
                    main = true;
                    memcpy(cam, camCand[k], sizeof(cam));
                    memcpy(d, dCand[k], sizeof(d));
                }
            }
        }
        if (!main)
            return;
        (bytes == 128 ? g_cCbMain128 : g_cCbMain224).fetch_add(1, std::memory_order_relaxed);
    }

    ID3D11Device* dev = engine_device();
    ID3D11DeviceContext* ctx = engine_context();
    if (dev == nullptr || ctx == nullptr || !scratch_for(bytes))
        return;

    memcpy(t_scratch, data, bytes);

    int verified = 0;
    for (int i = 0; i < layout->count; ++i)
    {
        const Site& s = layout->sites[i];
        if (s.offset + (s.kind == SITE_POS ? 12u : 64u) > bytes)
            continue;

        const float* orig = reinterpret_cast<const float*>(src + s.offset);
        float* m = reinterpret_cast<float*>(t_scratch + s.offset);

        if (s.kind == SITE_FWD)
        {
            if (is_forward_camera(orig, cam))
            {
                apply_forward(m, d);
                ++verified;
            }
        }
        else if (s.kind == SITE_INV)
        {
            if (is_inverse_camera(orig, cam))
            {
                apply_inverse(m, d);
                ++verified;
            }
        }
        else if (is_camera_position(orig, cam))
        {
            m[0] += d[0];
            m[1] += d[1];
            m[2] += d[2];
            ++verified;
        }
    }

    CbEntry* e = cb_find(resource, true);
    if (e == nullptr)
        return;

    if (e->shadow != nullptr && e->bytes != bytes)
    {
        e->shadow->Release();
        e->shadow = nullptr;
    }

    if (e->shadow == nullptr)
    {
        D3D11_BUFFER_DESC bd = {};
        bd.ByteWidth = bytes;
        bd.Usage = D3D11_USAGE_DEFAULT;
        bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;

        ID3D11Buffer* buf = nullptr;
        if (FAILED(dev->CreateBuffer(&bd, nullptr, &buf)) || buf == nullptr)
            return;

        /* Owned by the buffer it shadows, so it dies with it; the table's
           reference is our own. */
        resource->SetPrivateDataInterface(kCbShadow, buf);
        e->shadow = buf;
        e->bytes = bytes;
        BVR_INFO("Stereo: second-eye copy created for a %u-byte constant buffer "
                 "(%d camera site(s) verified in it).", bytes, verified);
    }

    const bool was = t_inDup;
    t_inDup = true;
    ctx->UpdateSubresource(e->shadow, 0, nullptr, t_scratch, 0, 0);
    t_inDup = was;

    e->cameraFrame = g_frame;
    g_cCbCamera.fetch_add(1, std::memory_order_relaxed);
}

void stereo_on_update(ID3D11DeviceContext* c, ID3D11Resource* r, UINT sub,
                      const D3D11_BOX* box, const void* data, UINT rowPitch, UINT depthPitch)
{
    if (!g_active.load(std::memory_order_relaxed) || ignored() || c == nullptr ||
        r == nullptr || data == nullptr)
        return;

    SurfState s;
    if (!state_get(r, s))
        return;       /* no twin contents to keep in step */

    ResInfo ri;
    if (!res_info(r, ri) || ((s.div | s.sync) & sub_bit(ri, sub)) == 0)
        return;       /* untracked, or stale and re-synced in full anyway */

    ID3D11Resource* twin = twin_lookup(r);
    if (twin == nullptr)
        return;
    t_inDup = true;
    c->UpdateSubresource(twin, sub, box, data, rowPitch, depthPitch);
    t_inDup = false;
    twin->Release();
    g_cCpuMirror.fetch_add(1, std::memory_order_relaxed);
}

/* ===========================================================================
   THE PER-MESH PREVIOUS-FRAME TRANSFORMS (TEST 183).

   TEST 182: "more aliasing on characters and horses in the right eye". The
   G-buffer writes a velocity for every dynamic mesh, and the reflection in the
   capture (S015 and ~30 others) shows how:

       current   = world position * g_viewproj_unjittered   (per_framef +304)
       previous  = object position * mesh_prev_frame_transform
       velocity  = current - previous (screen space)

   mesh_prev_frame_transform is a whole local -> previous-CLIP matrix, the
   previous camera baked in, stored per mesh in g_meshf1_buffer (t78: a 304-byte
   stride structured buffer, filled by Map(WRITE_DISCARD) on a dynamic buffer
   and CopySubresourceRegion'd into the default one the shaders read, once a
   frame). +304 gets the eye offset for eye 1; this buffer never did. So every
   dynamic mesh in eye 1 moved by the eye separation as far as its velocity
   said, DLSS/TAA rejected its history, and characters and horses aliased in
   that eye only - the AFR character-shimmer class of fault, in native stereo.

   The correction is exact and needs no inverse. The shader uses row vectors
   and a position with w = 1, and a world matrix is affine, so
       local * W * T(-d) * VP  =  local * (W * VP)  +  (-d, 0) * VP
   - only ROW 3 of each element's matrix changes, by the same row apply_forward
   takes off the main view. VP should be the previous frame's; the newest main
   view seen before the upload is used, and the difference is the eye offset
   times one frame's head rotation - under a millimetre of disparity.

   Offsets inside the element: mesh_prev_frame_transform at +128, row 3 at +176.
   =========================================================================== */
namespace {

constexpr uint32_t kMeshf1Stride = 304;
constexpr uint32_t kMeshPrevRow3 = 128 + 48;

std::vector<uint8_t> g_meshPatched;          /* render thread only */
ID3D11Resource*      g_meshPendingSrc = nullptr;   /* compared, never dereferenced */
uint32_t             g_meshPendingBytes = 0;

std::atomic<uint64_t> g_cMeshWrites{0}, g_cMeshElems{0}, g_cMeshApplied{0};
std::atomic<uint64_t> g_cMeshNoView{0}, g_cMeshSane{0};

bool mesh_prev_enabled()
{
    static bool cached = true;
    static bool have = false;
    if (!have) { have = true; cached = config_bool("stereo_prev_transform", true); }
    return cached;
}

/* The engine is copying `src` into `dst`; when src is the buffer whose eye-1
   version was just built, that version goes into dst's twin and dst becomes
   per-eye. True when handled - the caller must do nothing else with this copy. */
bool mesh_prev_copy(ID3D11DeviceContext* c, ID3D11Resource* dst, UINT dstSub, UINT x,
                    ID3D11Resource* src, const D3D11_BOX* box)
{
    if (src == nullptr || src != g_meshPendingSrc || dst == nullptr || dstSub != 0)
        return false;

    D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
    dst->GetType(&dim);
    if (dim != D3D11_RESOURCE_DIMENSION_BUFFER)
        return false;
    D3D11_BUFFER_DESC dd = {};
    static_cast<ID3D11Buffer*>(dst)->GetDesc(&dd);

    const uint32_t left = box ? box->left : 0u;
    const uint32_t right = box ? box->right : g_meshPendingBytes;
    if (right > g_meshPendingBytes || left >= right ||
        static_cast<uint64_t>(x) + (right - left) > dd.ByteWidth)
        return false;

    bool readback = false;
    ID3D11Resource* twin = twin_create(dst, readback);
    if (twin == nullptr)
        return false;

    SurfState ds;
    state_get(dst, ds);
    const bool whole = x == 0 && (right - left) == dd.ByteWidth;
    if (!whole && ((ds.div | ds.sync) & 1ull) == 0)
        copy_raw(c, twin, dst);       /* the rest of the buffer keeps what it has */

    D3D11_BOX b = { x, 0, 0, x + (right - left), 1, 1 };
    const bool was = t_inDup;
    t_inDup = true;
    c->UpdateSubresource(twin, 0, whole ? nullptr : &b, g_meshPatched.data() + left, 0, 0);
    t_inDup = was;
    twin->Release();

    memset(&ds, 0, sizeof(ds));
    ds.div = 1ull;
    state_put(dst, ds);

    g_meshPendingSrc = nullptr;
    g_cMeshApplied.fetch_add(1, std::memory_order_relaxed);
    return true;
}

} // namespace

/* ===========================================================================
   THE READBACK THAT SERIALISES THE FRAME (TEST 185).

   With DLSS on, "GPU at 70%, fewer fps than without DLSS". The perf line had
   the render thread blocked 8-12 ms a frame in Map(READ), and the frame maps of
   the DLSS runs name the call: right after NGX evaluates, the game copies the
   1x1 R16_FLOAT exposure texture into a 1x1 STAGING texture, and maps that for
   reading at the very START of the next frame. The map waits for the GPU to
   finish the whole previous frame, so the CPU can never build frame N+1 while
   the GPU draws frame N: every frame runs CPU-then-GPU in series and the GPU
   idles for the CPU's half. Without DLSS the readback does not exist.

   So tiny staging readbacks are delayed: the copy lands in one of three staging
   textures of ours, attached to the game's, and the map is handed the newest of
   them the GPU has ALREADY finished (D3D11_MAP_FLAG_DO_NOT_WAIT), falling back
   to waiting only on the oldest. The exposure the game reads is a frame or two
   old, which nothing can see. stereo_readback_delay = 0 turns it off.
   =========================================================================== */
namespace {

/* {7E71A3B8-2C4D-4E5F-9A6B-1C2D3E4F5A60} .. 62 */
const GUID kRbSlot[3] = {
    { 0x7e71a3b8, 0x2c4d, 0x4e5f, { 0x9a, 0x6b, 0x1c, 0x2d, 0x3e, 0x4f, 0x5a, 0x60 } },
    { 0x7e71a3b8, 0x2c4d, 0x4e5f, { 0x9a, 0x6b, 0x1c, 0x2d, 0x3e, 0x4f, 0x5a, 0x61 } },
    { 0x7e71a3b8, 0x2c4d, 0x4e5f, { 0x9a, 0x6b, 0x1c, 0x2d, 0x3e, 0x4f, 0x5a, 0x62 } },
};
/* {7E71A3B8-2C4D-4E5F-9A6B-1C2D3E4F5A6F} */
const GUID kRbState = { 0x7e71a3b8, 0x2c4d, 0x4e5f, { 0x9a, 0x6b, 0x1c, 0x2d, 0x3e, 0x4f, 0x5a, 0x6f } };

struct RbState
{
    uint32_t next;
    uint32_t pad;
    uint64_t seq[3];     /* 0 = never written; higher = newer copy */
    uint64_t counter;
};

std::atomic<uint64_t> g_cRbCopies{0}, g_cRbFresh{0}, g_cRbWaited{0};

bool readback_enabled()
{
    static bool cached = true;
    static bool have = false;
    if (!have) { have = true; cached = config_bool("stereo_readback_delay", true); }
    return cached;
}

bool readback_candidate(ID3D11Resource* r, D3D11_TEXTURE2D_DESC* out)
{
    if (!g_active.load(std::memory_order_relaxed) || r == nullptr || !readback_enabled())
        return false;
    D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
    r->GetType(&dim);
    if (dim != D3D11_RESOURCE_DIMENSION_TEXTURE2D)
        return false;
    D3D11_TEXTURE2D_DESC d = {};
    static_cast<ID3D11Texture2D*>(r)->GetDesc(&d);
    if (d.Usage != D3D11_USAGE_STAGING || (d.CPUAccessFlags & D3D11_CPU_ACCESS_READ) == 0 ||
        d.CPUAccessFlags & D3D11_CPU_ACCESS_WRITE || d.MipLevels != 1 || d.ArraySize != 1 ||
        static_cast<uint64_t>(d.Width) * d.Height > 64u * 64u)
        return false;
    if (out) *out = d;
    return true;
}

ID3D11Texture2D* rb_slot(ID3D11Resource* r, int i)
{
    ID3D11Texture2D* t = nullptr;
    UINT size = sizeof(t);
    if (SUCCEEDED(r->GetPrivateData(kRbSlot[i], &size, &t)) && t != nullptr)
        return t;   /* AddRef'd */
    return nullptr;
}

} // namespace

ID3D11Resource* stereo_readback_copy_dst(ID3D11Resource* dst)
{
    D3D11_TEXTURE2D_DESC d = {};
    if (ignored() || !readback_candidate(dst, &d))
        return dst;

    ID3D11Device* dev = engine_device();
    if (dev == nullptr)
        return dst;

    RbState st = {};
    UINT size = sizeof(st);
    if (FAILED(dst->GetPrivateData(kRbState, &size, &st)) || size != sizeof(st))
    {
        memset(&st, 0, sizeof(st));
        for (int i = 0; i < 3; ++i)
        {
            ID3D11Texture2D* t = nullptr;
            if (FAILED(dev->CreateTexture2D(&d, nullptr, &t)) || t == nullptr)
                return dst;
            dst->SetPrivateDataInterface(kRbSlot[i], t);
            t->Release();       /* the private data holds it */
        }
    }

    const uint32_t i = st.next % 3;
    ID3D11Texture2D* slot = rb_slot(dst, static_cast<int>(i));
    if (slot == nullptr)
        return dst;
    slot->Release();            /* dst holds it for as long as the call needs it */

    st.seq[i] = ++st.counter;
    st.next = (i + 1) % 3;
    dst->SetPrivateData(kRbState, sizeof(st), &st);
    g_cRbCopies.fetch_add(1, std::memory_order_relaxed);
    return slot;
}

ID3D11Resource* stereo_readback_map(ID3D11DeviceContext* c, ID3D11Resource* r, UINT sub,
                                    D3D11_MAP type, UINT flags, D3D11_MAPPED_SUBRESOURCE* m,
                                    StereoMapFn origMap, HRESULT* hr)
{
    if (c == nullptr || origMap == nullptr || hr == nullptr || sub != 0 ||
        (type != D3D11_MAP_READ) || !readback_candidate(r, nullptr))
        return nullptr;

    RbState st = {};
    UINT size = sizeof(st);
    if (FAILED(r->GetPrivateData(kRbState, &size, &st)) || size != sizeof(st))
        return nullptr;

    /* newest first */
    int order[3] = { 0, 1, 2 };
    for (int a = 0; a < 3; ++a)
        for (int b = a + 1; b < 3; ++b)
            if (st.seq[order[b]] > st.seq[order[a]])
            {
                const int t = order[a]; order[a] = order[b]; order[b] = t;
            }

    int oldest = -1;
    for (int k = 0; k < 3; ++k)
    {
        const int i = order[k];
        if (st.seq[i] == 0)
            continue;
        oldest = i;
        ID3D11Texture2D* slot = rb_slot(r, i);
        if (slot == nullptr)
            continue;
        const HRESULT h = origMap(c, slot, 0, type, flags | D3D11_MAP_FLAG_DO_NOT_WAIT, m);
        slot->Release();
        if (SUCCEEDED(h))
        {
            *hr = h;
            g_cRbFresh.fetch_add(1, std::memory_order_relaxed);
            return slot;
        }
    }

    if (oldest < 0)
        return nullptr;
    ID3D11Texture2D* slot = rb_slot(r, oldest);
    if (slot == nullptr)
        return nullptr;
    slot->Release();
    *hr = origMap(c, slot, 0, type, flags, m);
    g_cRbWaited.fetch_add(1, std::memory_order_relaxed);
    return SUCCEEDED(*hr) ? slot : nullptr;
}

bool stereo_wants_buffer_write(ID3D11Resource* r)
{
    if (!g_active.load(std::memory_order_relaxed) || r == nullptr || !mesh_prev_enabled())
        return false;
    D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
    r->GetType(&dim);
    if (dim != D3D11_RESOURCE_DIMENSION_BUFFER)
        return false;
    D3D11_BUFFER_DESC d = {};
    static_cast<ID3D11Buffer*>(r)->GetDesc(&d);
    return d.StructureByteStride == kMeshf1Stride &&
           (d.MiscFlags & D3D11_RESOURCE_MISC_BUFFER_STRUCTURED) != 0 &&
           d.ByteWidth >= kMeshf1Stride && d.ByteWidth % kMeshf1Stride == 0;
}

void stereo_on_buffer_write(ID3D11Resource* r, const void* data, uint32_t bytes)
{
    g_meshPendingSrc = nullptr;
    if (!g_active.load(std::memory_order_relaxed) || ignored() || r == nullptr ||
        data == nullptr || bytes < kMeshf1Stride)
        return;
    g_cMeshWrites.fetch_add(1, std::memory_order_relaxed);

    if (!g_mainValid || !g_mainVpUValid)
    {
        g_cMeshNoView.fetch_add(1, std::memory_order_relaxed);
        return;
    }

    /* b = (-d, 0) * VP, in double: VP carries a ~1 km battle origin. */
    double b[4];
    for (int c = 0; c < 4; ++c)
        b[c] = -(static_cast<double>(g_mainD[0]) * g_mainVpU[0 * 4 + c] +
                 static_cast<double>(g_mainD[1]) * g_mainVpU[1 * 4 + c] +
                 static_cast<double>(g_mainD[2]) * g_mainVpU[2 * 4 + c]);

    g_meshPatched.resize(bytes);
    memcpy(g_meshPatched.data(), data, bytes);

    const uint32_t n = bytes / kMeshf1Stride;
    uint64_t sane = 0;
    for (uint32_t k = 0; k < n; ++k)
    {
        float* row = reinterpret_cast<float*>(g_meshPatched.data() + k * kMeshf1Stride +
                                              kMeshPrevRow3);
        /* An element past the ones the engine wrote this frame is whatever the
           allocation held; shifting it is harmless and nothing reads it. */
        if (isfinite(row[3]) && row[3] > 0.0f)
            ++sane;
        for (int c = 0; c < 4; ++c)
            row[c] = static_cast<float>(row[c] + b[c]);
    }

    g_meshPendingSrc = r;
    g_meshPendingBytes = bytes;
    g_cMeshElems.fetch_add(n, std::memory_order_relaxed);
    g_cMeshSane.fetch_add(sane, std::memory_order_relaxed);
}

bool stereo_begin(ID3D11DeviceContext* c, StereoOp& op, bool compute)
{
    op.duplicate = false;
    op.compute = compute;
    op.nSwap = 0;
    memset(op.rtv, 0, sizeof(op.rtv));
    op.dsv = nullptr;
    memset(op.uav, 0, sizeof(op.uav));
    memset(op.trtv, 0, sizeof(op.trtv));
    op.tdsv = nullptr;
    memset(op.tuav, 0, sizeof(op.tuav));

    if (!g_active.load(std::memory_order_relaxed) || c == nullptr || ignored())
        return false;

    const int64_t t0 = qpc_now();
    g_cOps.fetch_add(1, std::memory_order_relaxed);

    static const int kDrawStages[5] = { 0, 1, 2, 3, 4 };
    static const int kCsStage[1] = { 5 };
    const int* stages = compute ? kCsStage : kDrawStages;
    const int nStages = compute ? 1 : 5;

    bool vd = false;
    bool refuse = false;
    bool mainCb = false;          /* a declared constant slot holds the main view */
    bool lightCb = false;         /* one holds a light view at the player (shadows) */
    uint32_t csUav = 0xFFu;
    uint32_t why = 0;

    for (int si = 0; si < nStages; ++si)
    {
        const int st = stages[si];
        ID3D11DeviceChild* sh = get_stage_shader(c, st);
        if (sh == nullptr)
            continue;

        ShaderDecl d;
        const bool known = decl_of(sh, d);
        sh->Release();
        if (!known)
        {
            g_cUnknownDecl.fetch_add(1, std::memory_order_relaxed);
            d.flags = 0;
            d.cb = (1u << 14) - 1;
            d.uav = 0xFFu;
            d.t[0] = d.t[1] = ~0ull;
        }

        if (st == 5)
            csUav = d.uav;
        else if (st == 4 && known && d.uav != 0)
            refuse = true;        /* pixel-shader UAVs: measured absent; not handled */
        if ((d.flags & DECL_UAV_HIGH) != 0)
            refuse = true;

        /* constants: the declared slots, fetched as one range */
        if (d.cb != 0)
        {
            const int lo = lowest_bit(d.cb), hi = highest_bit(d.cb);
            ID3D11Buffer* b[14] = {};
            get_stage_cbs(c, st, static_cast<UINT>(lo), static_cast<UINT>(hi - lo + 1), b);
            for (int i = 0; i <= hi - lo; ++i)
            {
                if (b[i] == nullptr)
                    continue;
                const int slot = lo + i;
                ID3D11Buffer* shadow = ((d.cb >> slot) & 1u) ? cb_shadow_live(b[i]) : nullptr;
                if (shadow != nullptr && op.nSwap < kStereoSwapMax)
                {
                    StereoSwap& s = op.swap[op.nSwap++];
                    s.stage = static_cast<uint8_t>(st);
                    s.isSrv = 0;
                    s.slot = static_cast<uint8_t>(slot);
                    s.orig = b[i];          /* keeps the Get's reference */
                    s.twin = shadow;
                    if (why == 0)
                        why = why_code(WHY_CB, st, slot);
                    vd = true;
                    mainCb = true;
                    continue;
                }
                if (shadow != nullptr)
                    g_cSwapOverflow.fetch_add(1, std::memory_order_relaxed), refuse = true;
                else if (((d.cb >> slot) & 1u) != 0 && cb_light_live(b[i]))
                    lightCb = true;
                b[i]->Release();
            }
        }

        /* textures: the declared slots whose subresources differ between the eyes */
        if ((d.t[0] | d.t[1]) != 0)
        {
            const int lo = d.t[0] ? lowest_bit(d.t[0]) : 64 + lowest_bit(d.t[1]);
            const int hi = d.t[1] ? 64 + highest_bit(d.t[1]) : highest_bit(d.t[0]);
            ID3D11ShaderResourceView* v[D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT] = {};
            get_stage_srvs(c, st, static_cast<UINT>(lo), static_cast<UINT>(hi - lo + 1), v);
            for (int i = 0; i <= hi - lo; ++i)
            {
                if (v[i] == nullptr)
                    continue;
                const int slot = lo + i;
                ViewInfo vi;
                if (slot_declared(d, slot) && view_info(v[i], 3, vi) &&
                    (div_of(vi.res) & vi.mask) != 0)
                {
                    if (op.nSwap < kStereoSwapMax)
                    {
                        StereoSwap& s = op.swap[op.nSwap++];
                        s.stage = static_cast<uint8_t>(st);
                        s.isSrv = 1;
                        s.slot = static_cast<uint8_t>(slot);
                        s.orig = v[i];
                        s.twin = nullptr;   /* made below, only if duplicating */
                        if (why == 0)
                            why = why_code(WHY_SRV, st, slot);
                        vd = true;
                        continue;
                    }
                    g_cSwapOverflow.fetch_add(1, std::memory_order_relaxed);
                    refuse = true;
                }
                v[i]->Release();
            }
        }
    }

    /* outputs */
    DsUse ds = { false, false };
    ViewInfo rtvInfo[kStereoOutSlots] = {};
    ViewInfo uavInfo[kStereoOutSlots] = {};
    ViewInfo dsvInfo = {};
    if (compute)
    {
        c->CSGetUnorderedAccessViews(0, kStereoOutSlots, op.uav);
        for (int i = 0; i < kStereoOutSlots; ++i)
        {
            if (op.uav[i] != nullptr && ((csUav >> i) & 1u) == 0)
            {
                op.uav[i]->Release();      /* bound but not declared: untouched */
                op.uav[i] = nullptr;
            }
            if (op.uav[i] != nullptr && view_info(op.uav[i], 2, uavInfo[i]) &&
                (div_of(uavInfo[i].res) & uavInfo[i].mask) != 0)
            {
                vd = true;                  /* read-write: reads the other eye's data */
                if (why == 0)
                    why = why_code(WHY_UAV, 5, i);
            }
        }
    }
    else
    {
        c->OMGetRenderTargets(kStereoOutSlots, op.rtv, &op.dsv);
        for (int i = 0; i < kStereoOutSlots; ++i)
        {
            if (op.rtv[i] != nullptr && view_info(op.rtv[i], 0, rtvInfo[i]) &&
                (div_of(rtvInfo[i].res) & rtvInfo[i].mask) != 0)
            {
                vd = true;                  /* blending into, or partly over, a per-eye image */
                if (why == 0)
                    why = why_code(WHY_RTV, 4, i);
            }
        }
        if (op.dsv != nullptr)
        {
            ds = depth_use(c);
            if (view_info(op.dsv, 1, dsvInfo) && (ds.read || ds.write) &&
                (div_of(dsvInfo.res) & dsvInfo.mask) != 0)
            {
                vd = true;                  /* depth-tested against a per-eye depth */
                if (why == 0)
                    why = why_code(WHY_DSV, 4, 0);
            }
        }
    }

    const bool dsvOut = op.dsv != nullptr && ds.write;

    /* A LIGHT-SPACE PASS IS SHARED, WHATEVER IT SAMPLES (TEST 182).
     *
       The shadow cascades are one for both eyes by definition - the sun does not
       move with the eye. TEST 181's census still showed ~100k cascade operations
       per 10 s re-issued, "first: per-eye texture PS t47": alpha-tested foliage
       and terrain casters sample the virtual-texture page table (t47) and cache
       (t44), which went per-eye because the passes updating them bind the camera
       constants. One such caster made its cascade slice per-eye, every later
       caster in that slice then depth-tested "a per-eye depth" and ran twice, and
       the lighting pass read a shadow atlas whose other slices were stale in the
       second eye - the missing right-eye shadows. Replayed with the page table
       tainted: 162 cascade draws a frame re-issued before, 0 with this.
     *
       A pass whose declared per-view constants are a light view at the player's
       position, and none the main view, runs once and reads the first eye's
       inputs. stereo_light_shared = 0 restores TEST 181. */
    if (vd && lightCb && !mainCb && light_passes_shared())
    {
        g_cLightShared.fetch_add(1, std::memory_order_relaxed);
        vd = false;
    }

    if (!vd)
    {
        /* A write the second eye does not repeat leaves the twin's copy of those
           subresources out of date. */
        for (int i = 0; i < kStereoOutSlots; ++i)
        {
            if (rtvInfo[i].res != nullptr)
                mark_shared_write(rtvInfo[i].res, rtvInfo[i].mask);
            if (uavInfo[i].res != nullptr)
                mark_shared_write(uavInfo[i].res, uavInfo[i].mask);
        }
        if (dsvOut && dsvInfo.res != nullptr)
            mark_shared_write(dsvInfo.res, dsvInfo.mask);
        release_all(op);
        g_tExamine.fetch_add(static_cast<uint64_t>(qpc_now() - t0), std::memory_order_relaxed);
        return false;
    }

    g_cTainted.fetch_add(1, std::memory_order_relaxed);
    {
        ID3D11Resource* key = nullptr;
        for (int i = 0; i < kStereoOutSlots && key == nullptr; ++i)
            key = compute ? uavInfo[i].res : rtvInfo[i].res;
        if (key == nullptr && !compute)
            key = dsvInfo.res;
        census_note(key, why);
    }

    /* What this operation writes, subresource by subresource. */
    struct Out { ID3D11Resource* r; uint64_t mask; };
    Out outs[kStereoOutSlots * 2 + 1] = {};
    int nOut = 0;
    for (int i = 0; i < kStereoOutSlots; ++i)
    {
        if (rtvInfo[i].res != nullptr)
            outs[nOut++] = { rtvInfo[i].res, rtvInfo[i].mask };
        if (uavInfo[i].res != nullptr)
            outs[nOut++] = { uavInfo[i].res, uavInfo[i].mask };
    }
    if (dsvOut && dsvInfo.res != nullptr)
        outs[nOut++] = { dsvInfo.res, dsvInfo.mask };

    if (refuse)
    {
        /* The engine's own write still happens; the second eye's does not. A twin
           left as it was would silently miss this operation, so it is marked out
           of date instead and eye 1 reads the shared result. */
        for (int i = 0; i < nOut; ++i)
            mark_shared_write(outs[i].r, outs[i].mask);
        g_cRefused.fetch_add(1, std::memory_order_relaxed);
        release_all(op);
        g_tExamine.fetch_add(static_cast<uint64_t>(qpc_now() - t0), std::memory_order_relaxed);
        return false;
    }

    /* EVERY SUBRESOURCE THIS WRITES MUST HOLD THE RIGHT PIXELS BEFORE IT RUNS -
       see prepare_write. Doing it after the original ran (TEST 164) copied this
       operation's own eye-0 result into the second eye's base.
     *
       Outputs are unbound around the copy: copying a surface that is bound for
       writing is not something D3D11 promises anything about. */
    bool needSync = false;
    for (int i = 0; i < nOut && !needSync; ++i)
    {
        SurfState s;
        state_get(outs[i].r, s);
        if ((outs[i].mask & ~(s.div | s.sync)) != 0)
            needSync = true;
    }

    /* A TWIN THAT IS READ MUST BE WHOLE (TEST 182). A texture view swapped to its
       twin is read in EVERY subresource it covers, not only the diverged ones -
       and since TEST 181 a view can span subresources in different states: one
       shadow cascade slice per-eye, three shared and stale in the twin. Those
       were read as they were in the twin (cleared, never drawn): no shadows in
       those cascades in the second eye. Any subresource of a read view that is
       not in step is filled from the original first. */
    struct Read { ID3D11Resource* r; uint64_t mask; };
    Read reads[kStereoSwapMax] = {};
    int nRead = 0;
    for (int i = 0; i < op.nSwap; ++i)
    {
        const StereoSwap& s = op.swap[i];
        ViewInfo vi;
        if (!s.isSrv || !view_info(static_cast<ID3D11View*>(s.orig), 3, vi))
            continue;
        SurfState st;
        state_get(vi.res, st);
        if ((vi.mask & ~(st.div | st.sync)) == 0)
            continue;
        reads[nRead++] = { vi.res, vi.mask };
        needSync = true;
    }

    if (needSync)
    {
        t_inDup = true;
        ID3D11UnorderedAccessView* noUav[kStereoOutSlots] = {};
        UINT keep[kStereoOutSlots];
        for (auto& k : keep)
            k = static_cast<UINT>(-1);
        if (compute)
            c->CSSetUnorderedAccessViews(0, kStereoOutSlots, noUav, keep);
        else
            c->OMSetRenderTargets(0, nullptr, nullptr);

        for (int i = 0; i < nOut; ++i)
            prepare_write(c, outs[i].r, outs[i].mask);
        for (int i = 0; i < nRead; ++i)
            if (prepare_write(c, reads[i].r, reads[i].mask))
                g_cReadSync.fetch_add(1, std::memory_order_relaxed);

        if (compute)
            c->CSSetUnorderedAccessViews(0, kStereoOutSlots, op.uav, keep);
        else
            c->OMSetRenderTargets(kStereoOutSlots, op.rtv, op.dsv);
        t_inDup = false;
    }

    /* Twin views for everything the second pass binds. Any one missing means the
       second pass would write into the ORIGINAL - eye 0's image - so the whole
       operation is refused rather than half done. */
    bool ok = true;
    for (int i = 0; i < kStereoOutSlots && ok; ++i)
    {
        if (op.rtv[i] != nullptr)
            ok = (op.trtv[i] = twin_rtv(op.rtv[i])) != nullptr;
        if (ok && op.uav[i] != nullptr)
            ok = (op.tuav[i] = twin_uav(op.uav[i])) != nullptr;
    }
    if (ok && op.dsv != nullptr)
    {
        const bool dsvDiv = dsvInfo.res != nullptr && (div_of(dsvInfo.res) & dsvInfo.mask) != 0;
        if (dsvOut || dsvDiv)
            ok = (op.tdsv = twin_dsv(op.dsv)) != nullptr;
        else
        {
            op.tdsv = op.dsv;          /* read-only and shared: the original serves */
            op.tdsv->AddRef();
        }
    }
    for (int i = 0; i < op.nSwap && ok; ++i)
    {
        StereoSwap& s = op.swap[i];
        if (s.isSrv)
            ok = (s.twin = twin_srv(static_cast<ID3D11ShaderResourceView*>(s.orig))) != nullptr;
    }

    if (!ok)
    {
        for (int i = 0; i < nOut; ++i)
            mark_shared_write(outs[i].r, outs[i].mask);
        g_cRefused.fetch_add(1, std::memory_order_relaxed);
        release_all(op);
        g_tExamine.fetch_add(static_cast<uint64_t>(qpc_now() - t0), std::memory_order_relaxed);
        return false;
    }

    for (int i = 0; i < nOut; ++i)
        mark_div(outs[i].r, outs[i].mask);

    op.duplicate = true;
    g_tExamine.fetch_add(static_cast<uint64_t>(qpc_now() - t0), std::memory_order_relaxed);
    return true;
}

void stereo_bind(ID3D11DeviceContext* c, StereoOp& op)
{
    if (!op.duplicate)
        return;

    op.t0 = qpc_now();
    t_inDup = true;

    if (op.compute)
    {
        UINT keep[kStereoOutSlots];
        for (auto& k : keep)
            k = static_cast<UINT>(-1);
        c->CSSetUnorderedAccessViews(0, kStereoOutSlots, op.tuav, keep);
    }
    else
    {
        c->OMSetRenderTargets(kStereoOutSlots, op.trtv, op.tdsv);
    }

    for (int i = 0; i < op.nSwap; ++i)
    {
        const StereoSwap& s = op.swap[i];
        if (s.isSrv)
            set_stage_srv(c, s.stage, s.slot, static_cast<ID3D11ShaderResourceView*>(s.twin));
        else
            set_stage_cb(c, s.stage, s.slot, static_cast<ID3D11Buffer*>(s.twin));
    }

    g_cDup.fetch_add(1, std::memory_order_relaxed);
}

void stereo_end(ID3D11DeviceContext* c, StereoOp& op)
{
    if (op.duplicate)
    {
        if (op.compute)
        {
            UINT keep[kStereoOutSlots];
            for (auto& k : keep)
                k = static_cast<UINT>(-1);
            c->CSSetUnorderedAccessViews(0, kStereoOutSlots, op.uav, keep);
        }
        else
        {
            c->OMSetRenderTargets(kStereoOutSlots, op.rtv, op.dsv);
        }

        for (int i = 0; i < op.nSwap; ++i)
        {
            const StereoSwap& s = op.swap[i];
            if (s.isSrv)
                set_stage_srv(c, s.stage, s.slot, static_cast<ID3D11ShaderResourceView*>(s.orig));
            else
                set_stage_cb(c, s.stage, s.slot, static_cast<ID3D11Buffer*>(s.orig));
        }
        t_inDup = false;
        g_tDup.fetch_add(static_cast<uint64_t>(qpc_now() - op.t0), std::memory_order_relaxed);
    }

    release_all(op);
    op.duplicate = false;
}

// --- the operations that write without drawing ------------------------------
//
// All of these are called BEFORE the original operation runs.

namespace {

/* A clear of the subresources a view covers: repeated on the twin, and - when it
   leaves nothing of the old contents behind - both copies of those subresources
   hold exactly the same thing again. Per subresource, which is what lets the
   shadow atlas's one-slice-at-a-time clears release it every frame. */
template <typename ClearFn>
void clear_common(ID3D11View* v, int kind, bool whole, ClearFn fn)
{
    ViewInfo vi;
    if (!view_info(v, kind, vi))
        return;

    SurfState s;
    if (!state_get(vi.res, s))
        return;                   /* no twin contents to keep in step */

    const uint64_t tracked = (s.div | s.sync | s.stale) & vi.mask;
    if (tracked == 0)
        return;
    if (!whole && ((s.div | s.sync) & vi.mask) == 0)
        return;                   /* stale: re-synced in full before it is next needed */

    if (!fn())
        return;

    if (whole)
    {
        s.sync |= vi.mask;
        s.div &= ~vi.mask;
        s.stale &= ~vi.mask;
        state_put(vi.res, s);
    }
}

} // namespace

void stereo_clear_rtv(ID3D11DeviceContext* c, ID3D11RenderTargetView* v, const FLOAT col[4])
{
    if (!g_active.load(std::memory_order_relaxed) || ignored() || v == nullptr || c == nullptr)
        return;
    clear_common(v, 0, true, [&]() -> bool {
        ID3D11RenderTargetView* twin = twin_rtv(v);
        if (twin == nullptr)
            return false;
        t_inDup = true;
        c->ClearRenderTargetView(twin, col);
        t_inDup = false;
        twin->Release();
        return true;
    });
}

void stereo_clear_dsv(ID3D11DeviceContext* c, ID3D11DepthStencilView* v, UINT flags,
                      FLOAT depth, UINT8 stencil)
{
    if (!g_active.load(std::memory_order_relaxed) || ignored() || v == nullptr || c == nullptr)
        return;

    /* Whole only when nothing of the old contents survives: depth AND stencil,
       or depth on a format that has no stencil. */
    D3D11_DEPTH_STENCIL_VIEW_DESC vd = {};
    v->GetDesc(&vd);
    const bool hasStencil = vd.Format == DXGI_FORMAT_D24_UNORM_S8_UINT ||
                            vd.Format == DXGI_FORMAT_D32_FLOAT_S8X24_UINT;
    const bool whole = (flags & D3D11_CLEAR_DEPTH) != 0 &&
                       (!hasStencil || (flags & D3D11_CLEAR_STENCIL) != 0);

    clear_common(v, 1, whole, [&]() -> bool {
        ID3D11DepthStencilView* twin = twin_dsv(v);
        if (twin == nullptr)
            return false;
        t_inDup = true;
        c->ClearDepthStencilView(twin, flags, depth, stencil);
        t_inDup = false;
        twin->Release();
        return true;
    });
}

void stereo_clear_uav_f(ID3D11DeviceContext* c, ID3D11UnorderedAccessView* v, const FLOAT v4[4])
{
    if (!g_active.load(std::memory_order_relaxed) || ignored() || v == nullptr || c == nullptr)
        return;
    clear_common(v, 2, true, [&]() -> bool {
        ID3D11UnorderedAccessView* twin = twin_uav(v);
        if (twin == nullptr)
            return false;
        t_inDup = true;
        c->ClearUnorderedAccessViewFloat(twin, v4);
        t_inDup = false;
        twin->Release();
        return true;
    });
}

void stereo_clear_uav_u(ID3D11DeviceContext* c, ID3D11UnorderedAccessView* v, const UINT v4[4])
{
    if (!g_active.load(std::memory_order_relaxed) || ignored() || v == nullptr || c == nullptr)
        return;
    clear_common(v, 2, true, [&]() -> bool {
        ID3D11UnorderedAccessView* twin = twin_uav(v);
        if (twin == nullptr)
            return false;
        t_inDup = true;
        c->ClearUnorderedAccessViewUint(twin, v4);
        t_inDup = false;
        twin->Release();
        return true;
    });
}

/* ClearView takes render-target and unordered-access views (never depth). The
   frame map has seen it once a frame, on the DLSS output. */
void stereo_clear_view(ID3D11DeviceContext1* c, ID3D11View* v, const FLOAT col[4],
                       const D3D11_RECT* rects, UINT n)
{
    if (!g_active.load(std::memory_order_relaxed) || ignored() || v == nullptr || c == nullptr)
        return;

    int kind = -1;
    ID3D11RenderTargetView* asRtv = nullptr;
    ID3D11UnorderedAccessView* asUav = nullptr;
    if (SUCCEEDED(v->QueryInterface(__uuidof(ID3D11RenderTargetView),
                                    reinterpret_cast<void**>(&asRtv))) && asRtv != nullptr)
    {
        kind = 0;
        asRtv->Release();
    }
    else if (SUCCEEDED(v->QueryInterface(__uuidof(ID3D11UnorderedAccessView),
                                         reinterpret_cast<void**>(&asUav))) && asUav != nullptr)
    {
        kind = 2;
        asUav->Release();
    }
    if (kind < 0)
        return;

    const bool whole = rects == nullptr || n == 0;
    clear_common(v, kind, whole, [&]() -> bool {
        ID3D11View* twin = twin_view_generic(v, kind);
        if (twin == nullptr)
            return false;
        t_inDup = true;
        c->ClearView(twin, col, rects, n);
        t_inDup = false;
        twin->Release();
        return true;
    });
}

void stereo_copy_resource(ID3D11DeviceContext* c, ID3D11Resource* dst, ID3D11Resource* src)
{
    if (!g_active.load(std::memory_order_relaxed) || ignored() || dst == nullptr ||
        src == nullptr || c == nullptr)
        return;

    if (mesh_prev_copy(c, dst, 0, 0, src, nullptr))
        return;

    SurfState ss;
    if (state_get(src, ss) && ss.div != 0)
    {
        /* A per-eye image copied somewhere makes that somewhere per-eye too, in
           the same subresources; the rest carries the shared contents. */
        bool readback = false;
        ID3D11Resource* dstTwin = twin_create(dst, readback);
        if (dstTwin == nullptr)
        {
            if (readback)
                g_cReadback.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        ID3D11Resource* srcTwin = twin_lookup(src);
        ResInfo ri;
        if (srcTwin != nullptr && res_info(src, ri))
        {
            const uint64_t all = all_bits(ri);
            if (ri.collapsed || (ss.div & all) == all)
                copy_raw(c, dstTwin, srcTwin);
            else
            {
                copy_raw(c, dstTwin, src);
                uint64_t m = ss.div;
                while (m != 0)
                {
                    const UINT i = static_cast<UINT>(bit_index(m));
                    m &= m - 1;
                    copy_sub_raw(c, dstTwin, i, 0, 0, 0, srcTwin, i, nullptr);
                }
            }
            SurfState ds;
            memset(&ds, 0, sizeof(ds));
            ds.div = ss.div & all;
            ds.sync = all & ~ss.div;
            state_put(dst, ds);
        }
        if (srcTwin) srcTwin->Release();
        dstTwin->Release();
        return;
    }

    /* Shared contents replace whatever the twin held. */
    SurfState ds;
    if (state_get(dst, ds) && (ds.div | ds.sync) != 0)
    {
        ds.stale |= ds.div | ds.sync;
        ds.div = 0;
        ds.sync = 0;
        state_put(dst, ds);
    }
}

void stereo_copy_region(ID3D11DeviceContext* c, ID3D11Resource* dst, UINT dstSub, UINT x,
                        UINT y, UINT z, ID3D11Resource* src, UINT srcSub, const D3D11_BOX* box)
{
    if (!g_active.load(std::memory_order_relaxed) || ignored() || dst == nullptr ||
        src == nullptr || c == nullptr)
        return;

    if (srcSub == 0 && y == 0 && z == 0 && mesh_prev_copy(c, dst, dstSub, x, src, box))
        return;

    SurfState ss;
    SurfState ds;
    const bool srcTracked = state_get(src, ss);
    const bool dstTracked = state_get(dst, ds);
    if (!srcTracked && !dstTracked)
        return;

    ResInfo sri, dri;
    if (!res_info(src, sri) || !res_info(dst, dri))
        return;
    const uint64_t sbit = sub_bit(sri, srcSub);
    const uint64_t dbit = sub_bit(dri, dstSub);

    if ((ss.div & sbit) != 0)
    {
        /* The region becomes per-eye; the rest of that destination subresource
           keeps what it has, so the twin must hold that too before the region
           lands in it. */
        bool readback = false;
        ID3D11Resource* dstTwin = twin_create(dst, readback);
        if (dstTwin == nullptr)
        {
            if (readback)
                g_cReadback.fetch_add(1, std::memory_order_relaxed);
            return;
        }
        ID3D11Resource* srcTwin = twin_lookup(src);
        if (srcTwin != nullptr && dbit != 0)
        {
            if (((ds.div | ds.sync) & dbit) == 0)
            {
                if (dri.collapsed)
                    copy_raw(c, dstTwin, dst);
                else
                    copy_sub_raw(c, dstTwin, dstSub, 0, 0, 0, dst, dstSub, nullptr);
            }
            copy_sub_raw(c, dstTwin, dstSub, x, y, z, srcTwin, srcSub, box);
            ds.div |= dbit;
            ds.sync &= ~dbit;
            ds.stale &= ~dbit;
            state_put(dst, ds);
        }
        if (srcTwin) srcTwin->Release();
        dstTwin->Release();
        return;
    }

    if (((ds.div | ds.sync) & dbit) != 0)
    {
        /* Shared contents into one region: the twin gets the same region, and
           keeps its own everywhere else. */
        ID3D11Resource* dstTwin = twin_lookup(dst);
        if (dstTwin != nullptr)
        {
            copy_sub_raw(c, dstTwin, dstSub, x, y, z, src, srcSub, box);
            dstTwin->Release();
        }
    }
}

void stereo_generate_mips(ID3D11DeviceContext* c, ID3D11ShaderResourceView* v)
{
    if (!g_active.load(std::memory_order_relaxed) || ignored() || v == nullptr || c == nullptr)
        return;

    ViewInfo vi;
    if (!view_info(v, 3, vi))
        return;
    SurfState s;
    if (!state_get(vi.res, s))
        return;
    ResInfo ri;
    if (!res_info(vi.res, ri))
        return;

    if (ri.collapsed)
    {
        /* One bit for the lot: a twin in step regenerates its own mips too. */
        if ((s.div | s.sync) != 0)
        {
            ID3D11ShaderResourceView* twin = twin_srv(v);
            if (twin != nullptr)
            {
                t_inDup = true;
                c->GenerateMips(twin);
                t_inDup = false;
                twin->Release();
            }
        }
        return;
    }

    /* Every mip below a slice's most detailed one inherits that level's state -
       DIVERGED and SYNC because the twin regenerates its own, anything else out
       of date. */
    uint64_t lowerAll = 0, newDiv = 0, newSync = 0;
    bool twinNeeded = false;
    uint64_t rest = vi.mask;
    while (rest != 0)
    {
        const int idx = bit_index(rest);
        const uint32_t mip0 = static_cast<uint32_t>(idx) % ri.mips;
        const uint32_t slice = static_cast<uint32_t>(idx) / ri.mips;
        uint64_t sliceBits = 0;
        for (uint32_t k = 0; k < ri.mips; ++k)
            sliceBits |= 1ull << (k + slice * ri.mips);
        const uint64_t inView = vi.mask & sliceBits;
        rest &= ~sliceBits;

        const uint64_t base = 1ull << idx;          /* lowest bit in the slice = most detailed mip */
        const uint64_t lower = inView & ~base;
        (void)mip0;
        lowerAll |= lower;
        if ((s.div & base) != 0)
        {
            newDiv |= lower;
            twinNeeded = true;
        }
        else if ((s.sync & base) != 0)
        {
            newSync |= lower;
            twinNeeded = true;
        }
    }

    if (twinNeeded)
    {
        ID3D11ShaderResourceView* twin = twin_srv(v);
        if (twin != nullptr)
        {
            t_inDup = true;
            c->GenerateMips(twin);
            t_inDup = false;
            twin->Release();
        }
        else
        {
            newDiv = 0;
            newSync = 0;
        }
    }

    const uint64_t trackedLower = (s.div | s.sync | s.stale) & lowerAll;
    s.div = (s.div & ~lowerAll) | newDiv;
    s.sync = (s.sync & ~lowerAll) | newSync;
    s.stale = (s.stale & ~lowerAll) | (trackedLower & ~(newDiv | newSync));
    state_put(vi.res, s);
}

ID3D11Texture2D* stereo_twin_texture(ID3D11Texture2D* original)
{
    if (original == nullptr || !g_active.load(std::memory_order_relaxed))
        return nullptr;

    ID3D11Resource* r = static_cast<ID3D11Resource*>(original);
    if (div_of(r) == 0)
        return nullptr;

    ID3D11Resource* twin = twin_lookup(r);
    if (twin == nullptr)
        return nullptr;

    ID3D11Texture2D* tex = nullptr;
    if (FAILED(twin->QueryInterface(__uuidof(ID3D11Texture2D),
                                    reinterpret_cast<void**>(&tex))))
        tex = nullptr;
    twin->Release();
    return tex;     /* AddRef'd */
}

ID3D11Resource* stereo_twin_for_read(ID3D11Resource* original)
{
    if (original == nullptr || !g_active.load(std::memory_order_relaxed))
        return nullptr;
    if (div_of(original) == 0)
        return nullptr;
    return twin_lookup(original);
}

ID3D11Resource* stereo_twin_for_write(ID3D11DeviceContext* c, ID3D11Resource* original)
{
    (void)c;
    if (original == nullptr || !g_active.load(std::memory_order_relaxed))
        return nullptr;
    bool readback = false;
    return twin_create(original, readback);
}

void stereo_mark_output(ID3D11Resource* original, bool produced)
{
    if (original == nullptr || !g_active.load(std::memory_order_relaxed))
        return;

    SurfState s;
    const bool tracked = state_get(original, s);
    if (produced)
    {
        ResInfo ri;
        if (!res_info(original, ri))
            return;
        memset(&s, 0, sizeof(s));
        s.div = all_bits(ri);
        state_put(original, s);
    }
    else if (tracked && (s.div | s.sync) != 0)
    {
        s.stale |= s.div | s.sync;
        s.div = 0;
        s.sync = 0;
        state_put(original, s);
    }
}

void stereo_frame_boundary()
{
    ++g_frame;

    static uint64_t frames = 0;
    ++frames;

    static bool wasActive = false;
    const bool active = g_active.load(std::memory_order_relaxed);
    if (!active)
    {
        wasActive = false;
        return;
    }

    static uint64_t lastMs = 0;
    static uint64_t lastCensusMs = 0;
    static uint64_t captureAtMs = 0;
    const uint64_t now = GetTickCount64();

    /* Switched on: the first report covers this session, not everything since the
       process started. */
    if (!wasActive)
    {
        wasActive = true;
        frames = 0;
        lastMs = now;
        lastCensusMs = now;
        g_censusRows = 0;
        g_censusOverflow = 0;
        g_syncRowCount = 0;
        g_mainValid = false;
        g_prevMainValid = false;
        g_offsetCosSum = 0.0;
        g_offsetCosMin = 1.0;
        g_offsetCosN = 0;
        for (auto* a : { &g_cOps, &g_cTainted, &g_cDup, &g_cCbUpload, &g_cCbCamera, &g_cCbLight,
                         &g_cRefused, &g_cSync, &g_cUnknownDecl, &g_cSwapOverflow, &g_cCpuMirror,
                         &g_tExamine, &g_tDup, &g_cReadback, &g_cCbMain1760, &g_cCbMain128,
                         &g_cCbMain224, &g_cCbFar, &g_cOffsetFallback, &g_cLightShared,
                         &g_cReadSync, &g_syncBytes ,
                         &g_cMeshWrites, &g_cMeshElems, &g_cMeshApplied, &g_cMeshNoView,
                         &g_cMeshSane })
            a->store(0, std::memory_order_relaxed);
        captureAtMs = 0;
        const float after = config_float("stereo_capture_after", 15.0f);
        if (after > 0.0f)
            captureAtMs = now + static_cast<uint64_t>(after * 1000.0f);
        return;
    }

    /* One frame map of THIS mode per activation, so the next replay has native
       stereo's own frame to work on rather than whichever mode ran 20 s into the
       mission (TEST 181's capture caught AFR). stereo_capture_after = 0 skips it. */
    if (captureAtMs != 0 && now >= captureAtMs)
    {
        captureAtMs = 0;
        frame_map_request_capture("native stereo has been running for a while");
    }

    if (now - lastCensusMs >= 10000)
    {
        lastCensusMs = now;
        census_report();
    }

    if (now - lastMs < 5000)
        return;
    lastMs = now;

    const uint64_t nFrames = frames ? frames : 1;
    frames = 0;

    const uint64_t ops = g_cOps.exchange(0, std::memory_order_relaxed);
    const uint64_t tainted = g_cTainted.exchange(0, std::memory_order_relaxed);
    const uint64_t dup = g_cDup.exchange(0, std::memory_order_relaxed);
    const uint64_t cbUp = g_cCbUpload.exchange(0, std::memory_order_relaxed);
    const uint64_t cbCam = g_cCbCamera.exchange(0, std::memory_order_relaxed);
    const uint64_t cbLight = g_cCbLight.exchange(0, std::memory_order_relaxed);
    const uint64_t refused = g_cRefused.exchange(0, std::memory_order_relaxed);
    const uint64_t syncs = g_cSync.exchange(0, std::memory_order_relaxed);
    const uint64_t unknown = g_cUnknownDecl.exchange(0, std::memory_order_relaxed);
    const uint64_t overflow = g_cSwapOverflow.exchange(0, std::memory_order_relaxed);
    const uint64_t mirror = g_cCpuMirror.exchange(0, std::memory_order_relaxed);
    const uint64_t readbacks = g_cReadback.exchange(0, std::memory_order_relaxed);
    const uint64_t tEx = g_tExamine.exchange(0, std::memory_order_relaxed);
    const uint64_t tDup = g_tDup.exchange(0, std::memory_order_relaxed);
    const uint64_t twinBytes = g_twinBytes.load(std::memory_order_relaxed);
    const uint64_t main1760 = g_cCbMain1760.exchange(0, std::memory_order_relaxed);
    const uint64_t main128 = g_cCbMain128.exchange(0, std::memory_order_relaxed);
    const uint64_t main224 = g_cCbMain224.exchange(0, std::memory_order_relaxed);
    const uint64_t farCams = g_cCbFar.exchange(0, std::memory_order_relaxed);
    const uint64_t offsetFallback = g_cOffsetFallback.exchange(0, std::memory_order_relaxed);
    const uint64_t lightShared = g_cLightShared.exchange(0, std::memory_order_relaxed);
    const uint64_t readSyncs = g_cReadSync.exchange(0, std::memory_order_relaxed);
    const uint64_t syncBytes = g_syncBytes.exchange(0, std::memory_order_relaxed);
    const double cosMean = g_offsetCosN ? g_offsetCosSum / static_cast<double>(g_offsetCosN) : 0.0;
    const double cosMin = g_offsetCosN ? g_offsetCosMin : 0.0;
    g_offsetCosSum = 0.0;
    g_offsetCosMin = 1.0;
    g_offsetCosN = 0;

    /* TEST 182: how the main view was recognised. main 1760 should be about one
       or two a frame; a light count near zero while shadows are on, or far
       cameras where there should be none, says the gate is wrong. */
    BVR_INFO("Stereo main view: per-frame buffer %llu (%.1f a frame), per-pass %llu, compute "
             "%llu; %llu light view(s) at the player; %llu camera(s) beyond %.1f m of the head "
             "left alone; eye offset along the frame's right axis, |cos| to the published "
             "eyes mean %.4f / min %.4f (%llu from the published delta instead); %llu "
             "light-space op(s) kept shared; %llu read view(s) brought into step; %.1f MB a "
             "frame copied into twins.",
             static_cast<unsigned long long>(main1760),
             static_cast<double>(main1760) / static_cast<double>(nFrames),
             static_cast<unsigned long long>(main128),
             static_cast<unsigned long long>(main224),
             static_cast<unsigned long long>(cbLight),
             static_cast<unsigned long long>(farCams), near_head_m(), cosMean, cosMin,
             static_cast<unsigned long long>(offsetFallback),
             static_cast<unsigned long long>(lightShared),
             static_cast<unsigned long long>(readSyncs),
             static_cast<double>(syncBytes) / (1024.0 * 1024.0) / static_cast<double>(nFrames));

    BVR_INFO("Stereo: %llu op(s) examined, %llu view-dependent (%.0f%%), %llu re-issued "
             "for the second eye, %llu refused; %llu of %llu camera upload(s) were the "
             "main view, %llu a light view carrying our position (left shared); %llu "
             "twin re-sync(s); %llu twin surface(s) ever made, ~%llu MB at their real "
             "formats; %llu twin creation(s) failed; %llu readback(s) of a per-eye "
             "surface left with eye 0's value.",
             static_cast<unsigned long long>(ops),
             static_cast<unsigned long long>(tainted),
             ops ? 100.0 * static_cast<double>(tainted) / static_cast<double>(ops) : 0.0,
             static_cast<unsigned long long>(dup),
             static_cast<unsigned long long>(refused),
             static_cast<unsigned long long>(cbCam),
             static_cast<unsigned long long>(cbUp),
             static_cast<unsigned long long>(cbLight),
             static_cast<unsigned long long>(syncs),
             static_cast<unsigned long long>(g_cTwins.load(std::memory_order_relaxed)),
             static_cast<unsigned long long>(twinBytes >> 20),
             static_cast<unsigned long long>(g_cTwinFail.load(std::memory_order_relaxed)),
             static_cast<unsigned long long>(readbacks));

    BVR_INFO("Stereo cost: %.2f ms/frame deciding, %.2f ms/frame issuing the second eye "
             "(CPU, over %llu frames). Shader declarations: %llu parsed, %llu unreadable, "
             "cross-check %llu agree / %llu differ; %llu op(s) bound a shader with none "
             "(every bound slot counted); %llu swap overflow(s); %llu CPU write(s) "
             "mirrored; constant-buffer table full %llu time(s).",
             qpc_ms(tEx) / static_cast<double>(nFrames),
             qpc_ms(tDup) / static_cast<double>(nFrames),
             static_cast<unsigned long long>(nFrames),
             static_cast<unsigned long long>(g_declParsed.load(std::memory_order_relaxed)),
             static_cast<unsigned long long>(g_declFailed.load(std::memory_order_relaxed)),
             static_cast<unsigned long long>(g_declAgree.load(std::memory_order_relaxed)),
             static_cast<unsigned long long>(g_declDiffer.load(std::memory_order_relaxed)),
             static_cast<unsigned long long>(unknown),
             static_cast<unsigned long long>(overflow),
             static_cast<unsigned long long>(mirror),
             static_cast<unsigned long long>(g_cCbFull.load(std::memory_order_relaxed)));

    /* TEST 183: healthy is one write and one twin copy a frame, "no main view"
       at zero, and a stable in-front share (the engine's live meshes; the rest
       of the 8192 slots are whatever the allocation held). */
    {
        const uint64_t writes = g_cMeshWrites.exchange(0, std::memory_order_relaxed);
        const uint64_t elems = g_cMeshElems.exchange(0, std::memory_order_relaxed);
        const uint64_t applied = g_cMeshApplied.exchange(0, std::memory_order_relaxed);
        const uint64_t noView = g_cMeshNoView.exchange(0, std::memory_order_relaxed);
        const uint64_t sane = g_cMeshSane.exchange(0, std::memory_order_relaxed);
        BVR_INFO("Stereo motion vectors: %.2f per-mesh previous-transform buffer write(s) a "
                 "frame corrected for eye 1, %.2f a frame reached its shader copy; %llu "
                 "skipped before any main view; %.0f of %.0f element(s) a write in front of "
                 "the previous camera.%s",
                 static_cast<double>(writes - noView) / static_cast<double>(nFrames),
                 static_cast<double>(applied) / static_cast<double>(nFrames),
                 static_cast<unsigned long long>(noView),
                 writes > noView ? static_cast<double>(sane) / static_cast<double>(writes - noView) : 0.0,
                 writes > noView ? static_cast<double>(elems) / static_cast<double>(writes - noView) : 0.0,
                 mesh_prev_enabled() ? "" : " (stereo_prev_transform = 0: off)");

        /* TEST 185: with DLSS on, copies ~1 a frame and nearly all maps fresh;
           "waited" climbing means the GPU is more than three frames behind. */
        const uint64_t rbCopies = g_cRbCopies.exchange(0, std::memory_order_relaxed);
        const uint64_t rbFresh = g_cRbFresh.exchange(0, std::memory_order_relaxed);
        const uint64_t rbWaited = g_cRbWaited.exchange(0, std::memory_order_relaxed);
        if (rbCopies != 0 || rbFresh != 0 || rbWaited != 0)
            BVR_INFO("Stereo readback delay: %llu small staging copies redirected, %llu map(s) "
                     "served without waiting, %llu had to wait for the oldest copy.",
                     static_cast<unsigned long long>(rbCopies),
                     static_cast<unsigned long long>(rbFresh),
                     static_cast<unsigned long long>(rbWaited));
    }

    stereo_perf_report(qpc_ms(tEx) / static_cast<double>(nFrames),
                       qpc_ms(tDup) / static_cast<double>(nFrames), nFrames, twinBytes);
}

} // namespace bvr
