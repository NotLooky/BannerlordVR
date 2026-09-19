#include "frame_map.h"

#include <windows.h>
#include <shlobj.h>

#include "bvr_config.h"
#include "bvr_log.h"
#include "d3d_hooks.h"
#include "vp_patch.h"
#include "stereo_dup.h"
#include "xr_context.h"

#include <d3d11_1.h>
#include <d3dcompiler.h>
#include <MinHook.h>

#include <atomic>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

namespace bvr {
namespace {

/* {3C0E5B71-2F4A-4B8E-9D61-7A0C1E5F9B23}: the bytecode retained on every shader
   object created after install. Private data belongs to the object, so the
   runtime frees it with the shader and a recycled pointer can never hand back
   another shader's code. */
const GUID kFmBytecode = { 0x3c0e5b71, 0x2f4a, 0x4b8e,
                           { 0x9d, 0x61, 0x7a, 0x0c, 0x1e, 0x5f, 0x9b, 0x23 } };

struct FmSettings
{
    bool  enabled  = false;
    float delaySec = 20.0f;
    int   frames   = 2;
};

const FmSettings& fm_cfg()
{
    static FmSettings s;
    static bool loaded = false;
    if (!loaded)
    {
        loaded = true;
        s.enabled  = config_bool("frame_map", false);
        s.delaySec = config_float("frame_map_delay", 20.0f);
        const float f = config_float("frame_map_frames", 2.0f);
        s.frames = f < 1.0f ? 1 : (f > 8.0f ? 8 : static_cast<int>(f));
    }
    return s;
}

// ---------------------------------------------------------------------------
// capture state
// ---------------------------------------------------------------------------

std::atomic<bool> g_installed{ false };
std::atomic<bool> g_capturing{ false };
std::atomic<bool> g_writing{ false };
std::atomic<uint64_t> g_shRetained{ 0 };

/* Guards everything below. NEVER held across a call into the device context:
   another thread may be inside a D3D11 implementation - holding the runtime's
   own lock - and waiting for this one, which would deadlock. Context state is
   read first, into a Snap, without it; only the formatting takes it. */
std::mutex g_mu;

int      g_frame = 0;
uint32_t g_seq = 0;
std::string g_ops;
std::vector<uint8_t> g_cb;
uint32_t g_cbCount = 0;

std::unordered_map<const void*, int> g_resIds;
std::vector<std::string>             g_resText;
std::unordered_map<const void*, int> g_viewIds;
std::vector<std::string>             g_viewText;

struct ShaderRec
{
    char                 stage;
    uint64_t             hash;
    std::vector<uint8_t> code;
};
std::unordered_map<const void*, int> g_shIds;
std::vector<ShaderRec>               g_sh;

ID3D11DeviceContext*  g_immediate = nullptr;
ID3D11DeviceContext1* g_ctx1 = nullptr;

// ---------------------------------------------------------------------------
// ids - all called with g_mu held; only resource/view getters, no context calls
// ---------------------------------------------------------------------------

const char* dim_name(D3D11_RESOURCE_DIMENSION d)
{
    switch (d)
    {
    case D3D11_RESOURCE_DIMENSION_BUFFER:    return "BUF";
    case D3D11_RESOURCE_DIMENSION_TEXTURE1D: return "TEX1D";
    case D3D11_RESOURCE_DIMENSION_TEXTURE2D: return "TEX2D";
    case D3D11_RESOURCE_DIMENSION_TEXTURE3D: return "TEX3D";
    default:                                 return "UNKNOWN";
    }
}

int res_id(ID3D11Resource* r)
{
    if (r == nullptr)
        return -1;

    auto it = g_resIds.find(r);
    if (it != g_resIds.end())
        return it->second;

    const int id = static_cast<int>(g_resText.size());
    g_resIds.emplace(r, id);

    char line[512];
    D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
    r->GetType(&dim);

    switch (dim)
    {
    case D3D11_RESOURCE_DIMENSION_BUFFER:
    {
        ID3D11Buffer* b = nullptr;
        D3D11_BUFFER_DESC d = {};
        if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Buffer), reinterpret_cast<void**>(&b))) && b)
        {
            b->GetDesc(&d);
            b->Release();
        }
        _snprintf_s(line, _TRUNCATE,
                    "R%d BUF bytes=%u usage=%d bind=0x%X cpu=0x%X misc=0x%X stride=%u",
                    id, d.ByteWidth, static_cast<int>(d.Usage), d.BindFlags,
                    d.CPUAccessFlags, d.MiscFlags, d.StructureByteStride);
        break;
    }
    case D3D11_RESOURCE_DIMENSION_TEXTURE2D:
    {
        ID3D11Texture2D* t = nullptr;
        D3D11_TEXTURE2D_DESC d = {};
        if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&t))) && t)
        {
            t->GetDesc(&d);
            t->Release();
        }

        const char* tag = "";
        for (int e = 0; e < 2; ++e)
        {
            ID3D11Texture2D* eye = eye_texture(e);
            if (eye != nullptr)
            {
                if (static_cast<ID3D11Resource*>(eye) == r)
                    tag = e == 0 ? " EYE0" : " EYE1";
                eye->Release();
            }
        }

        _snprintf_s(line, _TRUNCATE,
                    "R%d TEX2D %ux%u mips=%u arr=%u fmt=%d samples=%u usage=%d bind=0x%X "
                    "cpu=0x%X misc=0x%X%s",
                    id, d.Width, d.Height, d.MipLevels, d.ArraySize,
                    static_cast<int>(d.Format), d.SampleDesc.Count,
                    static_cast<int>(d.Usage), d.BindFlags, d.CPUAccessFlags,
                    d.MiscFlags, tag);
        break;
    }
    case D3D11_RESOURCE_DIMENSION_TEXTURE3D:
    {
        ID3D11Texture3D* t = nullptr;
        D3D11_TEXTURE3D_DESC d = {};
        if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture3D), reinterpret_cast<void**>(&t))) && t)
        {
            t->GetDesc(&d);
            t->Release();
        }
        _snprintf_s(line, _TRUNCATE,
                    "R%d TEX3D %ux%ux%u mips=%u fmt=%d usage=%d bind=0x%X cpu=0x%X misc=0x%X",
                    id, d.Width, d.Height, d.Depth, d.MipLevels,
                    static_cast<int>(d.Format), static_cast<int>(d.Usage),
                    d.BindFlags, d.CPUAccessFlags, d.MiscFlags);
        break;
    }
    case D3D11_RESOURCE_DIMENSION_TEXTURE1D:
    {
        ID3D11Texture1D* t = nullptr;
        D3D11_TEXTURE1D_DESC d = {};
        if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture1D), reinterpret_cast<void**>(&t))) && t)
        {
            t->GetDesc(&d);
            t->Release();
        }
        _snprintf_s(line, _TRUNCATE,
                    "R%d TEX1D %u mips=%u arr=%u fmt=%d usage=%d bind=0x%X",
                    id, d.Width, d.MipLevels, d.ArraySize, static_cast<int>(d.Format),
                    static_cast<int>(d.Usage), d.BindFlags);
        break;
    }
    default:
        _snprintf_s(line, _TRUNCATE, "R%d %s", id, dim_name(dim));
        break;
    }

    g_resText.emplace_back(line);
    return id;
}

/* The resource behind a view, as an id. GetResource AddRefs. */
int view_res(ID3D11View* v)
{
    ID3D11Resource* r = nullptr;
    v->GetResource(&r);
    const int id = res_id(r);
    if (r != nullptr)
        r->Release();
    return id;
}

/* "V<id>:R<id>" for any view, describing it the first time it is seen. */
std::string view_ref(ID3D11View* v, char kind)
{
    if (v == nullptr)
        return "-";

    const int rid = view_res(v);

    int id;
    auto it = g_viewIds.find(v);
    if (it != g_viewIds.end())
    {
        id = it->second;
    }
    else
    {
        id = static_cast<int>(g_viewText.size());
        g_viewIds.emplace(v, id);

        char line[320];
        switch (kind)
        {
        case 'S':
        {
            D3D11_SHADER_RESOURCE_VIEW_DESC d = {};
            static_cast<ID3D11ShaderResourceView*>(v)->GetDesc(&d);
            _snprintf_s(line, _TRUNCATE,
                        "V%d SRV R%d fmt=%d dim=%d a=%u b=%u c=%u d=%u",
                        id, rid, static_cast<int>(d.Format),
                        static_cast<int>(d.ViewDimension),
                        d.Texture2DArray.MostDetailedMip, d.Texture2DArray.MipLevels,
                        d.Texture2DArray.FirstArraySlice, d.Texture2DArray.ArraySize);
            break;
        }
        case 'R':
        {
            D3D11_RENDER_TARGET_VIEW_DESC d = {};
            static_cast<ID3D11RenderTargetView*>(v)->GetDesc(&d);
            _snprintf_s(line, _TRUNCATE,
                        "V%d RTV R%d fmt=%d dim=%d mip=%u first=%u size=%u",
                        id, rid, static_cast<int>(d.Format),
                        static_cast<int>(d.ViewDimension), d.Texture2DArray.MipSlice,
                        d.Texture2DArray.FirstArraySlice, d.Texture2DArray.ArraySize);
            break;
        }
        case 'D':
        {
            D3D11_DEPTH_STENCIL_VIEW_DESC d = {};
            static_cast<ID3D11DepthStencilView*>(v)->GetDesc(&d);
            _snprintf_s(line, _TRUNCATE,
                        "V%d DSV R%d fmt=%d dim=%d flags=0x%X mip=%u first=%u size=%u",
                        id, rid, static_cast<int>(d.Format),
                        static_cast<int>(d.ViewDimension), d.Flags,
                        d.Texture2DArray.MipSlice, d.Texture2DArray.FirstArraySlice,
                        d.Texture2DArray.ArraySize);
            break;
        }
        case 'U':
        {
            D3D11_UNORDERED_ACCESS_VIEW_DESC d = {};
            static_cast<ID3D11UnorderedAccessView*>(v)->GetDesc(&d);
            _snprintf_s(line, _TRUNCATE,
                        "V%d UAV R%d fmt=%d dim=%d a=%u b=%u c=%u (buffer: first=%u n=%u "
                        "flags=0x%X)",
                        id, rid, static_cast<int>(d.Format),
                        static_cast<int>(d.ViewDimension), d.Texture2DArray.MipSlice,
                        d.Texture2DArray.FirstArraySlice, d.Texture2DArray.ArraySize,
                        d.Buffer.FirstElement, d.Buffer.NumElements, d.Buffer.Flags);
            break;
        }
        default:
            _snprintf_s(line, _TRUNCATE, "V%d ??? R%d", id, rid);
            break;
        }
        g_viewText.emplace_back(line);
    }

    char out[40];
    _snprintf_s(out, _TRUNCATE, "V%d:R%d", id, rid);
    return out;
}

uint64_t fnv1a(const uint8_t* p, size_t n)
{
    uint64_t h = 1469598103934665603ull;
    for (size_t i = 0; i < n; ++i)
    {
        h ^= p[i];
        h *= 1099511628211ull;
    }
    return h;
}

int shader_id(ID3D11DeviceChild* s, char stage)
{
    if (s == nullptr)
        return -1;

    auto it = g_shIds.find(s);
    if (it != g_shIds.end())
        return it->second;

    const int id = static_cast<int>(g_sh.size());
    g_shIds.emplace(s, id);

    ShaderRec rec;
    rec.stage = stage;
    rec.hash = 0;

    UINT size = 0;
    if (SUCCEEDED(s->GetPrivateData(kFmBytecode, &size, nullptr)) && size > 0)
    {
        rec.code.resize(size);
        if (FAILED(s->GetPrivateData(kFmBytecode, &size, rec.code.data())))
            rec.code.clear();
        else
            rec.hash = fnv1a(rec.code.data(), rec.code.size());
    }

    g_sh.push_back(std::move(rec));
    return id;
}

// ---------------------------------------------------------------------------
// snapshots - taken WITHOUT g_mu, released after formatting
// ---------------------------------------------------------------------------

constexpr int kSrv = 48;
constexpr int kCb = D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT;

enum { SV = 0, SH, SD, SG, SP, SC, kStages };
const char        kStageChar[kStages] = { 'V', 'H', 'D', 'G', 'P', 'C' };
const char* const kStageName[kStages] = { "VS", "HS", "DS", "GS", "PS", "CS" };

struct Snap
{
    bool compute = false;
    ID3D11RenderTargetView*    rtv[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
    ID3D11DepthStencilView*    dsv = nullptr;
    ID3D11UnorderedAccessView* uav[8] = {};
    ID3D11DeviceChild*         sh[kStages] = {};
    ID3D11Buffer*              cb[kStages][kCb] = {};
    UINT                       cbFirst[kStages][kCb] = {};
    UINT                       cbNum[kStages][kCb] = {};
    ID3D11ShaderResourceView*  srv[kStages][kSrv] = {};
    D3D11_VIEWPORT             vp = {};
    UINT                       nvp = 0;
    D3D11_DEPTH_STENCIL_DESC   dss = {};
    bool                       haveDss = false;
    UINT                       stencilRef = 0;
    D3D11_BLEND_DESC           bl = {};
    bool                       haveBl = false;
    D3D11_RASTERIZER_DESC      rs = {};
    bool                       haveRs = false;
    D3D11_PRIMITIVE_TOPOLOGY   topo = D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED;
};

void get_cbs(ID3D11DeviceContext* c, int st, Snap& s)
{
    if (g_ctx1 != nullptr && c == g_immediate)
    {
        ID3D11DeviceContext1* c1 = g_ctx1;
        switch (st)
        {
        case SV: c1->VSGetConstantBuffers1(0, kCb, s.cb[st], s.cbFirst[st], s.cbNum[st]); break;
        case SH: c1->HSGetConstantBuffers1(0, kCb, s.cb[st], s.cbFirst[st], s.cbNum[st]); break;
        case SD: c1->DSGetConstantBuffers1(0, kCb, s.cb[st], s.cbFirst[st], s.cbNum[st]); break;
        case SG: c1->GSGetConstantBuffers1(0, kCb, s.cb[st], s.cbFirst[st], s.cbNum[st]); break;
        case SP: c1->PSGetConstantBuffers1(0, kCb, s.cb[st], s.cbFirst[st], s.cbNum[st]); break;
        case SC: c1->CSGetConstantBuffers1(0, kCb, s.cb[st], s.cbFirst[st], s.cbNum[st]); break;
        default: break;
        }
        return;
    }

    switch (st)
    {
    case SV: c->VSGetConstantBuffers(0, kCb, s.cb[st]); break;
    case SH: c->HSGetConstantBuffers(0, kCb, s.cb[st]); break;
    case SD: c->DSGetConstantBuffers(0, kCb, s.cb[st]); break;
    case SG: c->GSGetConstantBuffers(0, kCb, s.cb[st]); break;
    case SP: c->PSGetConstantBuffers(0, kCb, s.cb[st]); break;
    case SC: c->CSGetConstantBuffers(0, kCb, s.cb[st]); break;
    default: break;
    }
    for (int i = 0; i < kCb; ++i)
    {
        s.cbFirst[st][i] = 0;
        s.cbNum[st][i] = 4096;
    }
}

void get_srvs(ID3D11DeviceContext* c, int st, Snap& s)
{
    switch (st)
    {
    case SV: c->VSGetShaderResources(0, kSrv, s.srv[st]); break;
    case SH: c->HSGetShaderResources(0, kSrv, s.srv[st]); break;
    case SD: c->DSGetShaderResources(0, kSrv, s.srv[st]); break;
    case SG: c->GSGetShaderResources(0, kSrv, s.srv[st]); break;
    case SP: c->PSGetShaderResources(0, kSrv, s.srv[st]); break;
    case SC: c->CSGetShaderResources(0, kSrv, s.srv[st]); break;
    default: break;
    }
}

void snapshot_gfx(ID3D11DeviceContext* c, Snap& s)
{
    s.compute = false;
    c->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, s.rtv, &s.dsv);
    c->OMGetRenderTargetsAndUnorderedAccessViews(0, nullptr, nullptr, 0, 8, s.uav);

    ID3D11VertexShader*   vs = nullptr;
    ID3D11HullShader*     hs = nullptr;
    ID3D11DomainShader*   ds = nullptr;
    ID3D11GeometryShader* gs = nullptr;
    ID3D11PixelShader*    ps = nullptr;
    c->VSGetShader(&vs, nullptr, nullptr);
    c->HSGetShader(&hs, nullptr, nullptr);
    c->DSGetShader(&ds, nullptr, nullptr);
    c->GSGetShader(&gs, nullptr, nullptr);
    c->PSGetShader(&ps, nullptr, nullptr);
    s.sh[SV] = vs;
    s.sh[SH] = hs;
    s.sh[SD] = ds;
    s.sh[SG] = gs;
    s.sh[SP] = ps;

    for (int st = SV; st <= SP; ++st)
    {
        if (s.sh[st] == nullptr)
            continue;
        get_cbs(c, st, s);
        get_srvs(c, st, s);
    }

    s.nvp = 1;
    c->RSGetViewports(&s.nvp, &s.vp);

    ID3D11DepthStencilState* dss = nullptr;
    c->OMGetDepthStencilState(&dss, &s.stencilRef);
    if (dss != nullptr)
    {
        dss->GetDesc(&s.dss);
        s.haveDss = true;
        dss->Release();
    }

    ID3D11BlendState* bs = nullptr;
    FLOAT factor[4] = {};
    UINT mask = 0;
    c->OMGetBlendState(&bs, factor, &mask);
    if (bs != nullptr)
    {
        bs->GetDesc(&s.bl);
        s.haveBl = true;
        bs->Release();
    }

    ID3D11RasterizerState* rs = nullptr;
    c->RSGetState(&rs);
    if (rs != nullptr)
    {
        rs->GetDesc(&s.rs);
        s.haveRs = true;
        rs->Release();
    }

    c->IAGetPrimitiveTopology(&s.topo);
}

void snapshot_cs(ID3D11DeviceContext* c, Snap& s)
{
    s.compute = true;
    ID3D11ComputeShader* cs = nullptr;
    c->CSGetShader(&cs, nullptr, nullptr);
    s.sh[SC] = cs;
    if (cs != nullptr)
    {
        get_cbs(c, SC, s);
        get_srvs(c, SC, s);
    }
    c->CSGetUnorderedAccessViews(0, 8, s.uav);
}

void release_snap(Snap& s)
{
    for (auto& v : s.rtv) if (v) v->Release();
    if (s.dsv) s.dsv->Release();
    for (auto& v : s.uav) if (v) v->Release();
    for (int st = 0; st < kStages; ++st)
    {
        if (s.sh[st]) s.sh[st]->Release();
        for (auto& b : s.cb[st]) if (b) b->Release();
        for (auto& v : s.srv[st]) if (v) v->Release();
    }
}

void appendf(std::string& s, const char* fmt, ...)
{
    char buf[512];
    va_list args;
    va_start(args, fmt);
    _vsnprintf_s(buf, _TRUNCATE, fmt, args);
    va_end(args);
    s += buf;
}

/* With g_mu held. */
void format_snap(std::string& out, const Snap& s)
{
    out += " ||";
    for (int st = 0; st < kStages; ++st)
        if (s.sh[st] != nullptr)
            appendf(out, " %s=S%d", kStageName[st], shader_id(s.sh[st], kStageChar[st]));

    if (!s.compute)
    {
        out += " | OM";
        for (int i = 0; i < D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT; ++i)
            if (s.rtv[i] != nullptr)
                appendf(out, " rt%d=%s", i, view_ref(s.rtv[i], 'R').c_str());
        if (s.dsv != nullptr)
            appendf(out, " ds=%s", view_ref(s.dsv, 'D').c_str());
        for (int i = 0; i < 8; ++i)
            if (s.uav[i] != nullptr)
                appendf(out, " pu%d=%s", i, view_ref(s.uav[i], 'U').c_str());

        if (s.nvp > 0)
            appendf(out, " | VP %.0f,%.0f %.0fx%.0f z%.2f-%.2f", s.vp.TopLeftX, s.vp.TopLeftY,
                    s.vp.Width, s.vp.Height, s.vp.MinDepth, s.vp.MaxDepth);
        if (s.haveDss)
            appendf(out, " | DSS en%d wr%d fn%d st%d ref%u", s.dss.DepthEnable,
                    static_cast<int>(s.dss.DepthWriteMask), static_cast<int>(s.dss.DepthFunc),
                    s.dss.StencilEnable, s.stencilRef);
        if (s.haveBl)
            appendf(out, " | BL en%d src%d dst%d op%d mask0x%X ind%d",
                    s.bl.RenderTarget[0].BlendEnable,
                    static_cast<int>(s.bl.RenderTarget[0].SrcBlend),
                    static_cast<int>(s.bl.RenderTarget[0].DestBlend),
                    static_cast<int>(s.bl.RenderTarget[0].BlendOp),
                    s.bl.RenderTarget[0].RenderTargetWriteMask, s.bl.IndependentBlendEnable);
        if (s.haveRs)
            appendf(out, " | RS cull%d fill%d sc%d dclip%d bias%d",
                    static_cast<int>(s.rs.CullMode), static_cast<int>(s.rs.FillMode),
                    s.rs.ScissorEnable, s.rs.DepthClipEnable, s.rs.DepthBias);
        appendf(out, " | TOPO %d", static_cast<int>(s.topo));
    }

    for (int st = 0; st < kStages; ++st)
    {
        if (s.sh[st] == nullptr)
            continue;

        bool any = false;
        for (int i = 0; i < kCb; ++i)
        {
            if (s.cb[st][i] == nullptr)
                continue;
            if (!any) { appendf(out, " | cb.%s", kStageName[st]); any = true; }
            appendf(out, " b%d=R%d", i, res_id(s.cb[st][i]));
            if (s.cbFirst[st][i] != 0 || s.cbNum[st][i] != 4096)
                appendf(out, "[%u+%u]", s.cbFirst[st][i], s.cbNum[st][i]);
        }

        any = false;
        for (int i = 0; i < kSrv; ++i)
        {
            if (s.srv[st][i] == nullptr)
                continue;
            if (!any) { appendf(out, " | srv.%s", kStageName[st]); any = true; }
            appendf(out, " t%d=%s", i, view_ref(s.srv[st][i], 'S').c_str());
        }
    }

    if (s.compute)
    {
        bool any = false;
        for (int i = 0; i < 8; ++i)
        {
            if (s.uav[i] == nullptr)
                continue;
            if (!any) { out += " | uav.CS"; any = true; }
            appendf(out, " u%d=%s", i, view_ref(s.uav[i], 'U').c_str());
        }
    }
}

/* With g_mu held. */
void op_prefix(std::string& s, const char* kind)
{
    appendf(s, "#%06u f%d th%lu%s %s", g_seq++, g_frame,
            static_cast<unsigned long>(GetCurrentThreadId()),
            in_present_on_this_thread() ? " [ours]" : "", kind);
}

void record_pipeline(ID3D11DeviceContext* c, bool compute, const char* kind,
                     const char* args)
{
    if (!g_capturing.load(std::memory_order_relaxed) || c == nullptr)
        return;

    Snap snap;
    if (compute)
        snapshot_cs(c, snap);
    else
        snapshot_gfx(c, snap);

    {
        std::lock_guard<std::mutex> lock(g_mu);
        if (g_capturing.load(std::memory_order_relaxed))
        {
            std::string line;
            line.reserve(1024);
            op_prefix(line, kind);
            if (args != nullptr && args[0] != '\0')
            {
                line += ' ';
                line += args;
            }
            format_snap(line, snap);
            line += '\n';
            g_ops += line;
        }
    }

    release_snap(snap);
}

/* A plain op: its text is built under the lock from views and resources only. */
template <typename F>
void record_plain(const char* kind, F&& body)
{
    if (!g_capturing.load(std::memory_order_relaxed))
        return;

    std::lock_guard<std::mutex> lock(g_mu);
    if (!g_capturing.load(std::memory_order_relaxed))
        return;

    std::string line;
    op_prefix(line, kind);
    body(line);
    line += '\n';
    g_ops += line;
}

bool is_constant_buffer(ID3D11Resource* r, uint32_t* bytes)
{
    if (r == nullptr)
        return false;

    D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
    r->GetType(&dim);
    if (dim != D3D11_RESOURCE_DIMENSION_BUFFER)
        return false;

    ID3D11Buffer* b = nullptr;
    if (FAILED(r->QueryInterface(__uuidof(ID3D11Buffer), reinterpret_cast<void**>(&b))) || !b)
        return false;

    D3D11_BUFFER_DESC d = {};
    b->GetDesc(&d);
    b->Release();

    if (bytes != nullptr)
        *bytes = d.ByteWidth;
    return (d.BindFlags & D3D11_BIND_CONSTANT_BUFFER) != 0;
}

void frame_header()
{
    appendf(g_ops, "=== FRAME %d present#%llu ===\n", g_frame,
            static_cast<unsigned long long>(present_count()));

    VpFrame cur = {}, other = {};
    if (vp_debug_published(&cur, &other))
    {
        const VpFrame* f[2] = { &cur, &other };
        const char* label[2] = { "published", "other" };
        for (int k = 0; k < 2; ++k)
        {
            appendf(g_ops, "CAMERA %s eye=%d valid=%d eyeCamera=[", label[k], f[k]->eye,
                    f[k]->valid);
            for (int i = 0; i < 16; ++i)
                appendf(g_ops, i ? " %.6f" : "%.6f", f[k]->eyeCamera[i]);
            appendf(g_ops, "] refPos=(%.4f %.4f %.4f) refDir=(%.5f %.5f %.5f) "
                    "tan=(%.5f %.5f %.5f %.5f)\n",
                    f[k]->refPos[0], f[k]->refPos[1], f[k]->refPos[2],
                    f[k]->refDir[0], f[k]->refDir[1], f[k]->refDir[2],
                    f[k]->tanLeft, f[k]->tanRight, f[k]->tanUp, f[k]->tanDown);
        }
    }
    else
    {
        g_ops += "CAMERA (no publication)\n";
    }

    float pos[3] = {}, basis[9] = {};
    if (vp_debug_engine_camera(pos, basis))
        appendf(g_ops, "CAMERA engine-recovered pos=(%.4f %.4f %.4f) right=(%.5f %.5f %.5f) "
                "up=(%.5f %.5f %.5f) fwd=(%.5f %.5f %.5f)\n",
                pos[0], pos[1], pos[2], basis[0], basis[1], basis[2], basis[3], basis[4],
                basis[5], basis[6], basis[7], basis[8]);
}

// ---------------------------------------------------------------------------
// the context methods vp_patch does not own - inline-hooked per implementation
// ---------------------------------------------------------------------------

enum FmSlotId
{
    kBegin, kEnd, kSetPred, kDrawAuto, kDrawIdxInstIndirect, kDrawInstIndirect,
    kDispatchIndirect, kCopyRegion, kCopyRes, kCopyStructCount, kClearRtv,
    kClearUavU, kClearUavF, kGenMips, kResolve, kExecCl, kCopyRegion1,
    kDiscardRes, kDiscardView, kClearView, kDiscardView1, kSlotCount
};

constexpr int kVariants = 4;

struct FmSlot
{
    int         index;
    bool        ctx1;
    const char* name;
    void*       detour[kVariants];
    void*       target[kVariants];
    void*       tramp[kVariants];
};

FmSlot g_slots[kSlotCount] = {};

template <typename T>
T tramp_of(int slot, int v) { return reinterpret_cast<T>(g_slots[slot].tramp[v]); }

bool capturing() { return g_capturing.load(std::memory_order_relaxed); }

void note_async(const char* kind, ID3D11Asynchronous* a)
{
    int type = -1;
    if (a != nullptr)
    {
        ID3D11Query* q = nullptr;
        if (SUCCEEDED(a->QueryInterface(__uuidof(ID3D11Query), reinterpret_cast<void**>(&q))) && q)
        {
            D3D11_QUERY_DESC d = {};
            q->GetDesc(&d);
            type = static_cast<int>(d.Query);
            q->Release();
        }
    }
    record_plain(kind, [&](std::string& s) {
        appendf(s, " Q%p type=%d", static_cast<void*>(a), type);
    });
}

template <int V>
void STDMETHODCALLTYPE d_begin(ID3D11DeviceContext* c, ID3D11Asynchronous* a)
{
    if (capturing()) note_async("Begin", a);
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Asynchronous*)>(kBegin, V)(c, a);
}

template <int V>
void STDMETHODCALLTYPE d_end(ID3D11DeviceContext* c, ID3D11Asynchronous* a)
{
    if (capturing()) note_async("End", a);
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Asynchronous*)>(kEnd, V)(c, a);
}

template <int V>
void STDMETHODCALLTYPE d_set_pred(ID3D11DeviceContext* c, ID3D11Predicate* p, BOOL value)
{
    if (capturing())
        record_plain("SetPredication", [&](std::string& s) {
            appendf(s, " P%p value=%d", static_cast<void*>(p), value);
        });
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Predicate*, BOOL)>(kSetPred, V)(c, p, value);
}

template <int V>
void STDMETHODCALLTYPE d_draw_auto(ID3D11DeviceContext* c)
{
    if (capturing()) record_pipeline(c, false, "DrawAuto", "");
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*)>(kDrawAuto, V)(c);
}

void indirect_args(ID3D11Buffer* b, UINT off, char* out, size_t n)
{
    std::lock_guard<std::mutex> lock(g_mu);
    _snprintf_s(out, n, _TRUNCATE, "args=R%d+%u", res_id(b), off);
}

template <int V>
void STDMETHODCALLTYPE d_draw_idx_inst_indirect(ID3D11DeviceContext* c, ID3D11Buffer* b, UINT off)
{
    if (capturing())
    {
        char a[64];
        indirect_args(b, off, a, sizeof(a));
        record_pipeline(c, false, "DrawIndexedInstancedIndirect", a);
    }
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Buffer*, UINT)>(kDrawIdxInstIndirect, V)(c, b, off);
}

template <int V>
void STDMETHODCALLTYPE d_draw_inst_indirect(ID3D11DeviceContext* c, ID3D11Buffer* b, UINT off)
{
    if (capturing())
    {
        char a[64];
        indirect_args(b, off, a, sizeof(a));
        record_pipeline(c, false, "DrawInstancedIndirect", a);
    }
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Buffer*, UINT)>(kDrawInstIndirect, V)(c, b, off);
}

template <int V>
void STDMETHODCALLTYPE d_dispatch_indirect(ID3D11DeviceContext* c, ID3D11Buffer* b, UINT off)
{
    if (capturing())
    {
        char a[64];
        indirect_args(b, off, a, sizeof(a));
        record_pipeline(c, true, "DispatchIndirect", a);
    }
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Buffer*, UINT)>(kDispatchIndirect, V)(c, b, off);
}

void note_copy_region(const char* kind, ID3D11Resource* dst, UINT dstSub, UINT x, UINT y,
                      UINT z, ID3D11Resource* src, UINT srcSub, const D3D11_BOX* box)
{
    record_plain(kind, [&](std::string& s) {
        appendf(s, " dst=R%d/%u at=%u,%u,%u src=R%d/%u", res_id(dst), dstSub, x, y, z,
                res_id(src), srcSub);
        if (box != nullptr)
            appendf(s, " box=%u,%u,%u-%u,%u,%u", box->left, box->top, box->front,
                    box->right, box->bottom, box->back);
    });
}

template <int V>
void STDMETHODCALLTYPE d_copy_region(ID3D11DeviceContext* c, ID3D11Resource* dst, UINT dstSub,
                                     UINT x, UINT y, UINT z, ID3D11Resource* src, UINT srcSub,
                                     const D3D11_BOX* box)
{
    if (capturing()) note_copy_region("CopySubresourceRegion", dst, dstSub, x, y, z, src, srcSub, box);
    stereo_copy_region(c, dst, dstSub, x, y, z, src, srcSub, box);
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, UINT, UINT, UINT,
                                       UINT, ID3D11Resource*, UINT, const D3D11_BOX*)>(
        kCopyRegion, V)(c, dst, dstSub, x, y, z, src, srcSub, box);
}

template <int V>
void STDMETHODCALLTYPE d_copy_res(ID3D11DeviceContext* c, ID3D11Resource* dst, ID3D11Resource* src)
{
    if (capturing())
        record_plain("CopyResource", [&](std::string& s) {
            appendf(s, " dst=R%d src=R%d", res_id(dst), res_id(src));
        });
    stereo_copy_resource(c, dst, src);
    /* Native stereo: a tiny staging readback lands in a copy of ours (TEST 185). */
    ID3D11Resource* to = stereo_readback_copy_dst(dst);
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, ID3D11Resource*)>(
        kCopyRes, V)(c, to, src);
}

template <int V>
void STDMETHODCALLTYPE d_copy_struct_count(ID3D11DeviceContext* c, ID3D11Buffer* dst, UINT off,
                                           ID3D11UnorderedAccessView* src)
{
    if (capturing())
        record_plain("CopyStructureCount", [&](std::string& s) {
            appendf(s, " dst=R%d+%u src=%s", res_id(dst), off, view_ref(src, 'U').c_str());
        });
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Buffer*, UINT,
                                       ID3D11UnorderedAccessView*)>(kCopyStructCount, V)(c, dst, off, src);
}

template <int V>
void STDMETHODCALLTYPE d_clear_rtv(ID3D11DeviceContext* c, ID3D11RenderTargetView* v,
                                   const FLOAT col[4])
{
    if (capturing())
        record_plain("ClearRTV", [&](std::string& s) {
            appendf(s, " %s col=%.3f,%.3f,%.3f,%.3f", view_ref(v, 'R').c_str(),
                    col ? col[0] : 0.0f, col ? col[1] : 0.0f, col ? col[2] : 0.0f,
                    col ? col[3] : 0.0f);
        });
    stereo_clear_rtv(c, v, col);
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11RenderTargetView*,
                                       const FLOAT*)>(kClearRtv, V)(c, v, col);
}

template <int V>
void STDMETHODCALLTYPE d_clear_uav_u(ID3D11DeviceContext* c, ID3D11UnorderedAccessView* v,
                                     const UINT val[4])
{
    if (capturing())
        record_plain("ClearUAVUint", [&](std::string& s) {
            appendf(s, " %s val=%u,%u,%u,%u", view_ref(v, 'U').c_str(), val ? val[0] : 0u,
                    val ? val[1] : 0u, val ? val[2] : 0u, val ? val[3] : 0u);
        });
    stereo_clear_uav_u(c, v, val);
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11UnorderedAccessView*,
                                       const UINT*)>(kClearUavU, V)(c, v, val);
}

template <int V>
void STDMETHODCALLTYPE d_clear_uav_f(ID3D11DeviceContext* c, ID3D11UnorderedAccessView* v,
                                     const FLOAT val[4])
{
    if (capturing())
        record_plain("ClearUAVFloat", [&](std::string& s) {
            appendf(s, " %s val=%.3f,%.3f,%.3f,%.3f", view_ref(v, 'U').c_str(),
                    val ? val[0] : 0.0f, val ? val[1] : 0.0f, val ? val[2] : 0.0f,
                    val ? val[3] : 0.0f);
        });
    stereo_clear_uav_f(c, v, val);
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11UnorderedAccessView*,
                                       const FLOAT*)>(kClearUavF, V)(c, v, val);
}

template <int V>
void STDMETHODCALLTYPE d_gen_mips(ID3D11DeviceContext* c, ID3D11ShaderResourceView* v)
{
    if (capturing())
        record_plain("GenerateMips", [&](std::string& s) {
            appendf(s, " %s", view_ref(v, 'S').c_str());
        });
    stereo_generate_mips(c, v);
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11ShaderResourceView*)>(
        kGenMips, V)(c, v);
}

template <int V>
void STDMETHODCALLTYPE d_resolve(ID3D11DeviceContext* c, ID3D11Resource* dst, UINT dstSub,
                                 ID3D11Resource* src, UINT srcSub, DXGI_FORMAT fmt)
{
    if (capturing())
        record_plain("ResolveSubresource", [&](std::string& s) {
            appendf(s, " dst=R%d/%u src=R%d/%u fmt=%d", res_id(dst), dstSub, res_id(src),
                    srcSub, static_cast<int>(fmt));
        });
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, UINT,
                                       ID3D11Resource*, UINT, DXGI_FORMAT)>(
        kResolve, V)(c, dst, dstSub, src, srcSub, fmt);
}

template <int V>
void STDMETHODCALLTYPE d_exec_cl(ID3D11DeviceContext* c, ID3D11CommandList* cl, BOOL restore)
{
    if (capturing())
        record_plain("ExecuteCommandList", [&](std::string& s) {
            appendf(s, " CL%p restore=%d", static_cast<void*>(cl), restore);
        });
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11CommandList*, BOOL)>(
        kExecCl, V)(c, cl, restore);
}

template <int V>
void STDMETHODCALLTYPE d_copy_region1(ID3D11DeviceContext1* c, ID3D11Resource* dst, UINT dstSub,
                                      UINT x, UINT y, UINT z, ID3D11Resource* src, UINT srcSub,
                                      const D3D11_BOX* box, UINT flags)
{
    if (capturing()) note_copy_region("CopySubresourceRegion1", dst, dstSub, x, y, z, src, srcSub, box);
    stereo_copy_region(c, dst, dstSub, x, y, z, src, srcSub, box);
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext1*, ID3D11Resource*, UINT, UINT, UINT,
                                       UINT, ID3D11Resource*, UINT, const D3D11_BOX*, UINT)>(
        kCopyRegion1, V)(c, dst, dstSub, x, y, z, src, srcSub, box, flags);
}

template <int V>
void STDMETHODCALLTYPE d_discard_res(ID3D11DeviceContext1* c, ID3D11Resource* r)
{
    if (capturing())
        record_plain("DiscardResource", [&](std::string& s) { appendf(s, " R%d", res_id(r)); });
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext1*, ID3D11Resource*)>(kDiscardRes, V)(c, r);
}

template <int V>
void STDMETHODCALLTYPE d_discard_view(ID3D11DeviceContext1* c, ID3D11View* v)
{
    if (capturing())
        record_plain("DiscardView", [&](std::string& s) {
            appendf(s, " view-of-R%d", v ? view_res(v) : -1);
        });
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext1*, ID3D11View*)>(kDiscardView, V)(c, v);
}

template <int V>
void STDMETHODCALLTYPE d_clear_view(ID3D11DeviceContext1* c, ID3D11View* v, const FLOAT col[4],
                                    const D3D11_RECT* rects, UINT n)
{
    if (capturing())
        record_plain("ClearView", [&](std::string& s) {
            appendf(s, " view-of-R%d rects=%u", v ? view_res(v) : -1, n);
        });
    stereo_clear_view(c, v, col, rects, n);
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext1*, ID3D11View*, const FLOAT*,
                                       const D3D11_RECT*, UINT)>(kClearView, V)(c, v, col, rects, n);
}

template <int V>
void STDMETHODCALLTYPE d_discard_view1(ID3D11DeviceContext1* c, ID3D11View* v,
                                       const D3D11_RECT* rects, UINT n)
{
    if (capturing())
        record_plain("DiscardView1", [&](std::string& s) {
            appendf(s, " view-of-R%d rects=%u", v ? view_res(v) : -1, n);
        });
    tramp_of<void (STDMETHODCALLTYPE*)(ID3D11DeviceContext1*, ID3D11View*, const D3D11_RECT*,
                                       UINT)>(kDiscardView1, V)(c, v, rects, n);
}

#define FM_V(fn) { reinterpret_cast<void*>(&fn<0>), reinterpret_cast<void*>(&fn<1>), \
                   reinterpret_cast<void*>(&fn<2>), reinterpret_cast<void*>(&fn<3>) }

void set_slot(int id, int index, bool ctx1, const char* name, std::initializer_list<void*> d)
{
    FmSlot& s = g_slots[id];
    s.index = index;
    s.ctx1 = ctx1;
    s.name = name;
    int v = 0;
    for (void* p : d)
        if (v < kVariants)
            s.detour[v++] = p;
}

void init_slots()
{
    set_slot(kBegin,               27, false, "Begin",                        FM_V(d_begin));
    set_slot(kEnd,                 28, false, "End",                          FM_V(d_end));
    set_slot(kSetPred,             30, false, "SetPredication",               FM_V(d_set_pred));
    set_slot(kDrawAuto,            38, false, "DrawAuto",                     FM_V(d_draw_auto));
    set_slot(kDrawIdxInstIndirect, 39, false, "DrawIndexedInstancedIndirect", FM_V(d_draw_idx_inst_indirect));
    set_slot(kDrawInstIndirect,    40, false, "DrawInstancedIndirect",        FM_V(d_draw_inst_indirect));
    set_slot(kDispatchIndirect,    42, false, "DispatchIndirect",             FM_V(d_dispatch_indirect));
    set_slot(kCopyRegion,          46, false, "CopySubresourceRegion",        FM_V(d_copy_region));
    set_slot(kCopyRes,             47, false, "CopyResource",                 FM_V(d_copy_res));
    set_slot(kCopyStructCount,     49, false, "CopyStructureCount",           FM_V(d_copy_struct_count));
    set_slot(kClearRtv,            50, false, "ClearRenderTargetView",        FM_V(d_clear_rtv));
    set_slot(kClearUavU,           51, false, "ClearUnorderedAccessViewUint", FM_V(d_clear_uav_u));
    set_slot(kClearUavF,           52, false, "ClearUnorderedAccessViewFloat",FM_V(d_clear_uav_f));
    set_slot(kGenMips,             54, false, "GenerateMips",                 FM_V(d_gen_mips));
    set_slot(kResolve,             57, false, "ResolveSubresource",           FM_V(d_resolve));
    set_slot(kExecCl,              58, false, "ExecuteCommandList",           FM_V(d_exec_cl));
    set_slot(kCopyRegion1,        115, true,  "CopySubresourceRegion1",       FM_V(d_copy_region1));
    set_slot(kDiscardRes,         117, true,  "DiscardResource",              FM_V(d_discard_res));
    set_slot(kDiscardView,        118, true,  "DiscardView",                  FM_V(d_discard_view));
    set_slot(kClearView,          132, true,  "ClearView",                    FM_V(d_clear_view));
    set_slot(kDiscardView1,       133, true,  "DiscardView1",                 FM_V(d_discard_view1));
}

/* Addresses MinHook refused (or that someone else already hooked). Remembered
   so a refusal costs one log line, not one per frame. */
void* g_refused[64] = {};
int   g_refusedCount = 0;

bool is_refused(void* t)
{
    for (int i = 0; i < g_refusedCount; ++i)
        if (g_refused[i] == t)
            return true;
    return false;
}

void refuse(void* t)
{
    if (g_refusedCount < 64)
        g_refused[g_refusedCount++] = t;
}

/* Same contract as vp_patch's draw hooks: one MH_CreateHook per distinct
   address, ever, capped - so the runtime swapping implementations cannot drain
   MinHook's trampoline pool. */
void hook_slot(FmSlot& s, void** table)
{
    if (table == nullptr)
        return;

    void* t = table[s.index];
    if (t == nullptr || is_refused(t))
        return;

    for (int v = 0; v < kVariants; ++v)
        if (s.target[v] == t)
            return;

    int v = -1;
    for (int i = 0; i < kVariants; ++i)
        if (s.target[i] == nullptr) { v = i; break; }

    if (v < 0)
    {
        refuse(t);
        BVR_WARN("Frame map: %s has shown a fifth implementation; it is not hooked, and "
                 "calls through it will be missing from captures.", s.name);
        return;
    }

    /* The trampoline is written by MH_CreateHook, before MH_EnableHook makes the
       detour reachable - so no call can find the detour with a null forward. */
    const MH_STATUS st = MH_CreateHook(t, s.detour[v], &s.tramp[v]);
    if (st != MH_OK)
    {
        refuse(t);
        BVR_WARN("Frame map: MinHook refused %s at %p (status %d)%s.", s.name, t,
                 static_cast<int>(st),
                 st == MH_ERROR_ALREADY_CREATED
                     ? " - already hooked elsewhere in this process; calls through it "
                       "reach that hook, not this one"
                     : "");
        return;
    }
    if (MH_EnableHook(t) != MH_OK)
    {
        MH_RemoveHook(t);
        s.tramp[v] = nullptr;
        refuse(t);
        BVR_WARN("Frame map: MH_EnableHook refused %s at %p.", s.name, t);
        return;
    }

    s.target[v] = t;
    BVR_INFO("Frame map: inline-hooked %s implementation %d at %p.", s.name, v, t);
}

void scan_slots(ID3D11DeviceContext* c)
{
    if (c == nullptr)
        return;

    void** vt = *reinterpret_cast<void***>(c);
    void** vt1 = g_ctx1 != nullptr ? *reinterpret_cast<void***>(g_ctx1) : nullptr;

    for (int i = 0; i < kSlotCount; ++i)
        hook_slot(g_slots[i], g_slots[i].ctx1 ? vt1 : vt);
}

// ---------------------------------------------------------------------------
// shader bytecode retention - device vtable, which the runtime does not reclaim
// ---------------------------------------------------------------------------

using PFN_CreateShader = HRESULT (STDMETHODCALLTYPE*)(ID3D11Device*, const void*, SIZE_T,
                                                      ID3D11ClassLinkage*, void**);
using PFN_CreateGsSo = HRESULT (STDMETHODCALLTYPE*)(ID3D11Device*, const void*, SIZE_T,
                                                    const D3D11_SO_DECLARATION_ENTRY*, UINT,
                                                    const UINT*, UINT, UINT,
                                                    ID3D11ClassLinkage*, void**);

/* VS, GS, PS, HS, DS, CS in vtable order: 12, 13, 15, 16, 17, 18. */
constexpr int kCreateSlots[6] = { 12, 13, 15, 16, 17, 18 };
PFN_CreateShader g_origCreate[6] = {};
PFN_CreateGsSo   g_origCreateGsSo = nullptr;

void retain_bytecode(void* shader, const void* code, SIZE_T len)
{
    if (shader == nullptr || code == nullptr || len == 0 || len > 0x7FFFFFFF)
        return;
    ID3D11DeviceChild* child = reinterpret_cast<ID3D11DeviceChild*>(shader);
    if (SUCCEEDED(child->SetPrivateData(kFmBytecode, static_cast<UINT>(len), code)))
        g_shRetained.fetch_add(1, std::memory_order_relaxed);

    /* Native stereo counts only the slots a shader DECLARES - read here, once,
       from the bytecode, rather than guessed from whatever is bound. */
    stereo_note_shader(child, code, static_cast<size_t>(len));
}

template <int K>
HRESULT STDMETHODCALLTYPE d_create_shader(ID3D11Device* d, const void* code, SIZE_T len,
                                          ID3D11ClassLinkage* link, void** out)
{
    const HRESULT hr = g_origCreate[K](d, code, len, link, out);
    if (SUCCEEDED(hr) && out != nullptr)
        retain_bytecode(*out, code, len);
    return hr;
}

HRESULT STDMETHODCALLTYPE d_create_gs_so(ID3D11Device* d, const void* code, SIZE_T len,
                                         const D3D11_SO_DECLARATION_ENTRY* decl, UINT nDecl,
                                         const UINT* strides, UINT nStrides, UINT stream,
                                         ID3D11ClassLinkage* link, void** out)
{
    const HRESULT hr = g_origCreateGsSo(d, code, len, decl, nDecl, strides, nStrides,
                                        stream, link, out);
    if (SUCCEEDED(hr) && out != nullptr)
        retain_bytecode(*out, code, len);
    return hr;
}

void hook_shader_creation(ID3D11Device* device)
{
    void** vt = *reinterpret_cast<void***>(device);
    void* const detours[6] = {
        reinterpret_cast<void*>(&d_create_shader<0>), reinterpret_cast<void*>(&d_create_shader<1>),
        reinterpret_cast<void*>(&d_create_shader<2>), reinterpret_cast<void*>(&d_create_shader<3>),
        reinterpret_cast<void*>(&d_create_shader<4>), reinterpret_cast<void*>(&d_create_shader<5>),
    };

    int ok = 0;
    for (int k = 0; k < 6; ++k)
        if (vtable_hook(vt, kCreateSlots[k], detours[k], reinterpret_cast<void**>(&g_origCreate[k])))
            ++ok;
    if (vtable_hook(vt, 14, reinterpret_cast<void*>(&d_create_gs_so),
                    reinterpret_cast<void**>(&g_origCreateGsSo)))
        ++ok;

    BVR_INFO("Frame map: %d of 7 shader-creation entries hooked. Only shaders created "
             "from now on carry their bytecode; the capture reports how many of those "
             "it used had none.", ok);
}

// ---------------------------------------------------------------------------
// writing a capture out - on a worker thread, after the capture has ended
// ---------------------------------------------------------------------------

struct Capture
{
    std::string              ops;
    std::vector<std::string> res;
    std::vector<std::string> views;
    std::vector<ShaderRec>   sh;
    std::vector<uint8_t>     cb;
    uint32_t                 cbCount = 0;
};

bool write_file(const std::wstring& path, const void* data, size_t n)
{
    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"wb") != 0 || f == nullptr)
        return false;
    const bool ok = n == 0 || fwrite(data, 1, n, f) == n;
    fclose(f);
    return ok;
}

void write_capture(std::unique_ptr<Capture> cap)
{
    wchar_t docs[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_MYDOCUMENTS, nullptr, 0, docs)))
    {
        BVR_ERR("Frame map: no Documents folder; capture discarded.");
        return;
    }

    std::wstring root = std::wstring(docs) + L"\\Mount and Blade II Bannerlord\\Logs\\BannerlordVR.FrameMap";
    CreateDirectoryW(root.c_str(), nullptr);

    SYSTEMTIME st;
    GetLocalTime(&st);
    wchar_t stamp[64];
    _snwprintf_s(stamp, _TRUNCATE, L"%04d%02d%02d_%02d%02d%02d", st.wYear, st.wMonth, st.wDay,
                 st.wHour, st.wMinute, st.wSecond);
    const std::wstring dir = root + L"\\" + stamp;
    CreateDirectoryW(dir.c_str(), nullptr);
    const std::wstring shDir = dir + L"\\shaders";
    CreateDirectoryW(shDir.c_str(), nullptr);

    write_file(dir + L"\\ops.txt", cap->ops.data(), cap->ops.size());

    std::string text;
    for (const auto& r : cap->res) { text += r; text += '\n'; }
    write_file(dir + L"\\resources.txt", text.data(), text.size());

    text.clear();
    for (const auto& v : cap->views) { text += v; text += '\n'; }
    write_file(dir + L"\\views.txt", text.data(), text.size());

    write_file(dir + L"\\cb.bin", cap->cb.data(), cap->cb.size());

    int withCode = 0;
    text.clear();
    for (size_t i = 0; i < cap->sh.size(); ++i)
    {
        const ShaderRec& s = cap->sh[i];
        char line[160];
        if (s.code.empty())
        {
            _snprintf_s(line, _TRUNCATE, "S%zu %cS no-bytecode\n", i, s.stage);
            text += line;
            continue;
        }

        ++withCode;
        _snprintf_s(line, _TRUNCATE, "S%zu %cS hash=%016llx bytes=%zu\n", i, s.stage,
                    static_cast<unsigned long long>(s.hash), s.code.size());
        text += line;

        ID3DBlob* dis = nullptr;
        if (SUCCEEDED(D3DDisassemble(s.code.data(), s.code.size(), 0, nullptr, &dis)) && dis)
        {
            wchar_t name[96];
            _snwprintf_s(name, _TRUNCATE, L"\\S%03zu_%cS_%016llx.asm", i,
                         static_cast<wchar_t>(s.stage),
                         static_cast<unsigned long long>(s.hash));
            write_file(shDir + name, dis->GetBufferPointer(), dis->GetBufferSize() > 0
                                                                  ? dis->GetBufferSize() - 1
                                                                  : 0);
            dis->Release();
        }
    }
    write_file(dir + L"\\shaders.txt", text.data(), text.size());

    size_t lines = 0;
    for (char ch : cap->ops)
        if (ch == '\n')
            ++lines;

    char narrow[MAX_PATH * 2] = {};
    WideCharToMultiByte(CP_UTF8, 0, dir.c_str(), -1, narrow, sizeof(narrow), nullptr, nullptr);
    BVR_INFO("Frame map WRITTEN to %s: %zu lines, %u constant-buffer upload(s) (%zu bytes), "
             "%zu resources, %zu views, %zu shaders (%d with bytecode, %zu without).",
             narrow, lines, cap->cbCount, cap->cb.size(), cap->res.size(), cap->views.size(),
             cap->sh.size(), withCode, cap->sh.size() - static_cast<size_t>(withCode));
}

void begin_capture()
{
    std::lock_guard<std::mutex> lock(g_mu);

    g_frame = 0;
    g_seq = 0;
    g_ops.clear();
    g_ops.reserve(8u << 20);
    g_cb.clear();
    g_cb.reserve(8u << 20);
    g_cbCount = 0;
    g_resIds.clear();
    g_resText.clear();
    g_viewIds.clear();
    g_viewText.clear();
    g_shIds.clear();
    g_sh.clear();

    appendf(g_ops, "BannerlordVR frame map. vp_patch=%.0f vp_latch=%.2f afr=%.0f afw=%.0f "
            "frames=%d. Shaders with retained bytecode so far: %llu.\n",
            config_float("vp_patch", -1.0f), config_float("vp_latch", -1.0f),
            config_float("afr", -1.0f), config_float("afw", -1.0f), fm_cfg().frames,
            static_cast<unsigned long long>(g_shRetained.load(std::memory_order_relaxed)));
    g_ops += "Op line: #seq f<frame> th<thread> [ours]=issued inside our Present tick, "
             "then the op, its args, and after '||' every binding live at that moment.\n";
    frame_header();

    g_capturing.store(true, std::memory_order_release);
    BVR_INFO("Frame map: capturing %d frame(s) now.", fm_cfg().frames);
}

void end_capture()
{
    std::unique_ptr<Capture> cap(new Capture());
    {
        std::lock_guard<std::mutex> lock(g_mu);
        g_capturing.store(false, std::memory_order_release);
        cap->ops.swap(g_ops);
        cap->res.swap(g_resText);
        cap->views.swap(g_viewText);
        cap->sh.swap(g_sh);
        cap->cb.swap(g_cb);
        cap->cbCount = g_cbCount;
        g_resIds.clear();
        g_viewIds.clear();
        g_shIds.clear();
    }

    g_writing.store(true, std::memory_order_release);
    std::thread([c = std::move(cap)]() mutable {
        write_capture(std::move(c));
        g_writing.store(false, std::memory_order_release);
    }).detach();
}

bool     g_wasMission = false;
uint64_t g_missionStartMs = 0;
bool     g_capturedThisMission = false;
int      g_triggerPoll = 0;
std::atomic<const char*> g_captureRequest{ nullptr };

bool trigger_file_present()
{
    wchar_t docs[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_MYDOCUMENTS, nullptr, 0, docs)))
        return false;
    wchar_t path[MAX_PATH] = {};
    _snwprintf_s(path, _TRUNCATE,
                 L"%s\\Mount and Blade II Bannerlord\\Logs\\BannerlordVR.FrameMap.trigger", docs);
    if (GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES)
        return false;
    DeleteFileW(path);
    return true;
}

} // namespace

// ---------------------------------------------------------------------------
// public surface
// ---------------------------------------------------------------------------

bool frame_map_enabled() { return fm_cfg().enabled; }

bool frame_map_capturing() { return g_capturing.load(std::memory_order_relaxed); }

void frame_map_request_capture(const char* why)
{
    if (!fm_cfg().enabled)
        return;
    g_captureRequest.store(why != nullptr ? why : "requested", std::memory_order_release);
}

void frame_map_install(ID3D11Device* device, ID3D11DeviceContext* context)
{
    /* The taps below are what stereo duplication rides on as well, so they go
       in whenever either wants them. Capturing stays its own switch. */
    if ((!fm_cfg().enabled && !config_bool("stereo_dup", true)) ||
        g_installed.load(std::memory_order_acquire))
        return;
    if (device == nullptr || context == nullptr)
        return;

    g_immediate = context;
    if (FAILED(context->QueryInterface(__uuidof(ID3D11DeviceContext1),
                                       reinterpret_cast<void**>(&g_ctx1))))
        g_ctx1 = nullptr;   /* kept for the life of the process, like the context */

    init_slots();
    scan_slots(context);
    hook_shader_creation(device);

    g_installed.store(true, std::memory_order_release);
    BVR_INFO("Frame map ARMED: a capture of %d frame(s) is taken %.0f s after each mission "
             "goes active, or whenever Logs\\BannerlordVR.FrameMap.trigger appears. Output "
             "goes to Logs\\BannerlordVR.FrameMap\\<time>\\.",
             fm_cfg().frames, fm_cfg().delaySec);
}

void frame_map_present(ID3D11DeviceContext* context)
{
    if (!g_installed.load(std::memory_order_acquire))
        return;

    scan_slots(context);

    const bool mission = xr_mission_active();
    const uint64_t now = GetTickCount64();
    if (mission && !g_wasMission)
    {
        g_missionStartMs = now;
        g_capturedThisMission = false;
    }
    g_wasMission = mission;

    if (g_capturing.load(std::memory_order_acquire))
    {
        bool done = false;
        {
            std::lock_guard<std::mutex> lock(g_mu);
            ++g_frame;
            if (g_frame >= fm_cfg().frames)
                done = true;
            else
                frame_header();
        }
        if (done)
            end_capture();
        return;
    }

    if (g_writing.load(std::memory_order_acquire))
        return;

    bool trigger = false;
    if (++g_triggerPoll >= 45)
    {
        g_triggerPoll = 0;
        trigger = trigger_file_present();
    }

    if (const char* why = g_captureRequest.exchange(nullptr, std::memory_order_acq_rel))
    {
        BVR_INFO("Frame map: capture requested - %s.", why);
        trigger = true;
    }

    if (mission && !g_capturedThisMission &&
        now - g_missionStartMs >= static_cast<uint64_t>(fm_cfg().delaySec * 1000.0f))
    {
        g_capturedThisMission = true;
        trigger = true;
    }

    if (trigger)
        begin_capture();
}

void frame_map_draw(ID3D11DeviceContext* c, const char* kind, const char* args)
{
    record_pipeline(c, false, kind, args);
}

void frame_map_compute(ID3D11DeviceContext* c, const char* kind, const char* args)
{
    record_pipeline(c, true, kind, args);
}

void frame_map_clear_dsv(ID3D11DeviceContext* c, ID3D11DepthStencilView* dsv, UINT flags,
                         float depth, UINT stencil)
{
    (void)c;
    record_plain("ClearDSV", [&](std::string& s) {
        appendf(s, " %s flags=0x%X depth=%.4f stencil=%u", view_ref(dsv, 'D').c_str(), flags,
                depth, stencil);
    });
}

void frame_map_cb_upload(ID3D11Resource* r, const void* data, uint32_t bytes, uint32_t offset,
                         const char* how)
{
    if (!g_capturing.load(std::memory_order_relaxed) || data == nullptr || bytes == 0)
        return;

    std::lock_guard<std::mutex> lock(g_mu);
    if (!g_capturing.load(std::memory_order_relaxed))
        return;

    const int rid = res_id(r);
    const uint32_t blob = g_cbCount++;
    const uint32_t seq = g_seq;

    std::string line;
    op_prefix(line, "CB-UPLOAD");
    appendf(line, " R%d bytes=%u off=%u via=%s blob=%u\n", rid, bytes, offset,
            how != nullptr ? how : "?", blob);
    g_ops += line;

    /* Record: 'CBUP', seq, frame, resource id, byte offset, byte count, bytes. */
    const uint32_t hdr[6] = { 0x50554243u, seq, static_cast<uint32_t>(g_frame),
                              static_cast<uint32_t>(rid), offset, bytes };
    const uint8_t* h = reinterpret_cast<const uint8_t*>(hdr);
    g_cb.insert(g_cb.end(), h, h + sizeof(hdr));
    const uint8_t* p = static_cast<const uint8_t*>(data);
    g_cb.insert(g_cb.end(), p, p + bytes);
}

void frame_map_map(ID3D11Resource* r, UINT sub, D3D11_MAP type)
{
    if (!g_capturing.load(std::memory_order_relaxed))
        return;
    if (is_constant_buffer(r, nullptr))
        return;   /* its contents arrive through frame_map_cb_upload */

    record_plain("MAP", [&](std::string& s) {
        appendf(s, " R%d sub=%u type=%d", res_id(r), sub, static_cast<int>(type));
    });
}

void frame_map_update(ID3D11Resource* r, UINT sub, const D3D11_BOX* box, const void* data)
{
    if (!g_capturing.load(std::memory_order_relaxed))
        return;

    uint32_t bytes = 0;
    if (is_constant_buffer(r, &bytes))
    {
        uint32_t off = 0;
        uint32_t span = bytes;
        if (box != nullptr)
        {
            off = box->left;
            span = box->right > box->left ? box->right - box->left : 0u;
        }
        frame_map_cb_upload(r, data, span, off, "update");
        return;
    }

    record_plain("UPDATE", [&](std::string& s) {
        appendf(s, " R%d sub=%u", res_id(r), sub);
        if (box != nullptr)
            appendf(s, " box=%u,%u,%u-%u,%u,%u", box->left, box->top, box->front,
                    box->right, box->bottom, box->back);
    });
}

void frame_map_ngx(ID3D11DeviceContext* c, const char* const* names, ID3D11Resource* const* res,
                   int n, float jitterX, float jitterY, int reset, const char* kind)
{
    (void)c;
    record_plain(kind != nullptr ? kind : "NGX-EVALUATE", [&](std::string& s) {
        for (int i = 0; i < n; ++i)
            appendf(s, " %s=R%d", names[i], res[i] != nullptr ? res_id(res[i]) : -1);
        appendf(s, " jitter=%.4f,%.4f reset=%d", jitterX, jitterY, reset);
    });
}

} // namespace bvr
