#include "stereo_perf.h"

#include <windows.h>
#include <dxgi1_4.h>
#include <tlhelp32.h>
#include <psapi.h>

#include "bvr_config.h"
#include "bvr_log.h"
#include "d3d_hooks.h"
#include "stereo_dup.h"
#include "water_spec_aa.h"

#include <atomic>
#include <vector>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>

namespace bvr {
namespace {

bool perf_enabled()
{
    static bool cached = true;
    static bool have = false;
    if (!have)
    {
        have = true;
        cached = config_bool("stereo_perf", true);
    }
    return cached;
}

bool perf_live()
{
    return stereo_active() && perf_enabled();
}

int64_t qpc()
{
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}

double qpc_to_ms(int64_t ticks)
{
    static const double perMs = [] {
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        return static_cast<double>(f.QuadPart) / 1000.0;
    }();
    return static_cast<double>(ticks) / perMs;
}

/* The engine's immediate context is shared; same discipline as xr_context. */
struct ContextLock
{
    ID3D11Multithread* mt;
    ContextLock() : mt(engine_multithread())
    {
        if (mt != nullptr)
            mt->Enter();
    }
    ~ContextLock()
    {
        if (mt != nullptr)
            mt->Leave();
    }
    ContextLock(const ContextLock&) = delete;
    ContextLock& operator=(const ContextLock&) = delete;
};

// ---------------------------------------------------------------------------
// render-thread timing - only ever touched from the Present hook
// ---------------------------------------------------------------------------

bool    g_wasLive = false;
int64_t g_lastTickBegin = 0;
int64_t g_tickBegin = 0;
int64_t g_tickEnd = 0;

double   g_sumPeriod = 0.0, g_maxPeriod = 0.0;
uint64_t g_nPeriod = 0;
double   g_sumTick = 0.0;
uint64_t g_nTick = 0;
double   g_sumPresent = 0.0, g_maxPresent = 0.0;
uint64_t g_nPresent = 0;

constexpr int kMapFrames = 6;
constexpr int kMapSites = 16;
struct MapSite
{
    uint64_t hash;
    void*    frames[kMapFrames];
    int      nFrames;
    int      dim;
    UINT     w, h, fmt, usage;
    uint64_t count;
    double   ms;
};
MapSite g_mapSites[kMapSites] = {};
int     g_mapSiteCount = 0;
SRWLOCK g_mapSiteLock = SRWLOCK_INIT;

void addr_name(void* a, char* out, size_t n)
{
    HMODULE mod = nullptr;
    if (!GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            static_cast<LPCSTR>(a), &mod) || mod == nullptr)
    {
        _snprintf_s(out, n, _TRUNCATE, "%p", a);
        return;
    }
    char path[MAX_PATH] = {};
    GetModuleFileNameA(mod, path, MAX_PATH);
    const char* name = strrchr(path, '\\');
    name = name ? name + 1 : path;
    _snprintf_s(out, n, _TRUNCATE, "%s+0x%llX", name,
                static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(a) -
                                                reinterpret_cast<uintptr_t>(mod)));
}

void report_map_sites(double frames)
{
    MapSite rows[kMapSites];
    int n = 0;
    AcquireSRWLockExclusive(&g_mapSiteLock);
    n = g_mapSiteCount;
    memcpy(rows, g_mapSites, sizeof(rows));
    for (int i = 0; i < g_mapSiteCount; ++i) { g_mapSites[i].count = 0; g_mapSites[i].ms = 0.0; }
    ReleaseSRWLockExclusive(&g_mapSiteLock);

    for (int i = 0; i < n; ++i)
    {
        if (rows[i].count == 0)
            continue;
        char chain[640] = {};
        size_t used = 0;
        for (int k = 0; k < rows[i].nFrames && used + 2 < sizeof(chain); ++k)
        {
            char one[128];
            addr_name(rows[i].frames[k], one, sizeof(one));
            used += _snprintf_s(chain + used, sizeof(chain) - used, _TRUNCATE, "%s%s",
                                k ? " < " : "", one);
        }
        BVR_INFO("Stereo perf: blocking Map(READ) site #%d: %.2f ms a frame over %.2f call(s) "
                 "a frame, %s %ux%u fmt %u usage %u; caller %s",
                 i, rows[i].ms / frames, static_cast<double>(rows[i].count) / frames,
                 rows[i].dim == D3D11_RESOURCE_DIMENSION_BUFFER ? "buffer" : "texture",
                 rows[i].w, rows[i].h, rows[i].fmt, rows[i].usage, chain);
    }
}

std::atomic<int64_t>  g_mapTicks{ 0 };
std::atomic<uint64_t> g_mapCount{ 0 };

/* GPU timestamps. A slot measures one frame: t0 when our tick ENDS (the engine's
   frame starts), t1 when the next tick BEGINS (the engine's frame is submitted),
   t2 when that tick ends. The disjoint query brackets all three. Collected a few
   frames later without ever waiting - the same ring DR's cost line uses. */
struct GpuSlot
{
    ID3D11Query* dj = nullptr;
    ID3D11Query* t0 = nullptr;
    ID3D11Query* t1 = nullptr;
    ID3D11Query* t2 = nullptr;
    /* DLSS inside the engine frame (TEST 183): before the game's evaluation,
       after it, after eye 1's. Only the frame's first evaluation is stamped. */
    ID3D11Query* n[3] = { nullptr, nullptr, nullptr };
    bool         nHave[3] = { false, false, false };
    int          stage = 0;       /* 0 free, 1 opened, 2 t1 stamped, 3 pending */
    int          probe = -1;      /* TEST 548: the water probe phase this frame ran in */
};

constexpr int kGpuSlots = 8;
GpuSlot  g_gpu[kGpuSlots];
int      g_gpuOpen = -1;
bool     g_gpuFailed = false;
double   g_sumGpuEngine = 0.0, g_maxGpuEngine = 0.0, g_sumGpuTick = 0.0;
uint64_t g_nGpu = 0;
double   g_sumNgx0 = 0.0, g_sumNgx1 = 0.0;
uint64_t g_nNgx0 = 0, g_nNgx1 = 0;
std::atomic<int64_t>  g_ngxCpuTicks{ 0 };
std::atomic<uint64_t> g_ngxCpuCount{ 0 };

void reset_window()
{
    g_sumPeriod = g_maxPeriod = 0.0;
    g_nPeriod = 0;
    g_sumTick = 0.0;
    g_nTick = 0;
    g_sumPresent = g_maxPresent = 0.0;
    g_nPresent = 0;
    g_sumGpuEngine = g_maxGpuEngine = g_sumGpuTick = 0.0;
    g_nGpu = 0;
    g_sumNgx0 = g_sumNgx1 = 0.0;
    g_nNgx0 = g_nNgx1 = 0;
    g_ngxCpuTicks.store(0, std::memory_order_relaxed);
    g_ngxCpuCount.store(0, std::memory_order_relaxed);
    g_mapTicks.store(0, std::memory_order_relaxed);
    g_mapCount.store(0, std::memory_order_relaxed);
}

void release_slot(GpuSlot& s)
{
    if (s.dj) { s.dj->Release(); s.dj = nullptr; }
    if (s.t0) { s.t0->Release(); s.t0 = nullptr; }
    if (s.t1) { s.t1->Release(); s.t1 = nullptr; }
    if (s.t2) { s.t2->Release(); s.t2 = nullptr; }
    for (ID3D11Query*& q : s.n)
        if (q) { q->Release(); q = nullptr; }
    s.stage = 0;
}

void gpu_collect(ID3D11DeviceContext* ctx)
{
    for (GpuSlot& s : g_gpu)
    {
        if (s.stage != 3)
            continue;

        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT dj = {};
        UINT64 a = 0, b = 0, c = 0;
        if (ctx->GetData(s.dj, &dj, sizeof(dj), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK ||
            ctx->GetData(s.t0, &a, sizeof(a), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK ||
            ctx->GetData(s.t1, &b, sizeof(b), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK ||
            ctx->GetData(s.t2, &c, sizeof(c), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK)
            continue;

        s.stage = 0;
        if (dj.Disjoint || dj.Frequency == 0 || b < a || c < b)
            continue;

        const double toMs = 1000.0 / static_cast<double>(dj.Frequency);
        UINT64 n[3] = {};
        bool nOk[3] = {};
        for (int k = 0; k < 3; ++k)
            nOk[k] = s.nHave[k] &&
                     ctx->GetData(s.n[k], &n[k], sizeof(n[k]), D3D11_ASYNC_GETDATA_DONOTFLUSH) == S_OK;
        if (nOk[0] && nOk[1] && n[1] >= n[0])
        {
            g_sumNgx0 += static_cast<double>(n[1] - n[0]) * toMs;
            ++g_nNgx0;
        }
        if (nOk[1] && nOk[2] && n[2] >= n[1])
        {
            g_sumNgx1 += static_cast<double>(n[2] - n[1]) * toMs;
            ++g_nNgx1;
        }

        const double engine = static_cast<double>(b - a) * 1000.0 / static_cast<double>(dj.Frequency);
        water_probe_sample(s.probe, engine);
        const double tick = static_cast<double>(c - b) * 1000.0 / static_cast<double>(dj.Frequency);
        g_sumGpuEngine += engine;
        g_sumGpuTick += tick;
        if (engine > g_maxGpuEngine)
            g_maxGpuEngine = engine;
        ++g_nGpu;
    }
}

void gpu_open(ID3D11DeviceContext* ctx)
{
    g_gpuOpen = -1;
    if (g_gpuFailed)
        return;

    for (int i = 0; i < kGpuSlots; ++i)
    {
        GpuSlot& s = g_gpu[i];
        if (s.stage != 0)
            continue;

        if (s.dj == nullptr)
        {
            ID3D11Device* dev = engine_device();
            const D3D11_QUERY_DESC dj = { D3D11_QUERY_TIMESTAMP_DISJOINT, 0 };
            const D3D11_QUERY_DESC ts = { D3D11_QUERY_TIMESTAMP, 0 };
            if (dev == nullptr || FAILED(dev->CreateQuery(&dj, &s.dj)) ||
                FAILED(dev->CreateQuery(&ts, &s.t0)) || FAILED(dev->CreateQuery(&ts, &s.t1)) ||
                FAILED(dev->CreateQuery(&ts, &s.t2)) || FAILED(dev->CreateQuery(&ts, &s.n[0])) ||
                FAILED(dev->CreateQuery(&ts, &s.n[1])) || FAILED(dev->CreateQuery(&ts, &s.n[2])))
            {
                release_slot(s);
                g_gpuFailed = true;
                BVR_WARN("Stereo perf: timestamp queries unavailable; the GPU side of the "
                         "breakdown will read 0.");
                return;
            }
        }

        ctx->Begin(s.dj);
        ctx->End(s.t0);
        s.nHave[0] = s.nHave[1] = s.nHave[2] = false;
        s.probe = water_probe_open();
        s.stage = 1;
        g_gpuOpen = i;
        return;
    }
    /* every slot still waiting on the GPU: this frame goes unmeasured */
}

/* Abandons a measurement in flight, leaving no query active. */
void gpu_close_open(ID3D11DeviceContext* ctx)
{
    if (g_gpuOpen < 0)
        return;
    GpuSlot& s = g_gpu[g_gpuOpen];
    if ((s.stage == 1 || s.stage == 2) && ctx != nullptr)
        ctx->End(s.dj);
    s.stage = 0;
    g_gpuOpen = -1;
}

// ---------------------------------------------------------------------------
// GPU time by pass (TEST 196)
//
// DLSS Performance, Balanced and Quality ran within a few fps of each other
// (18:16 run: ~66, ~66, ~55), GPU-bound, with the render thread waiting ~8 ms of
// every frame for it. So most of the GPU's frame does not depend on the render
// size, and the two numbers above (engine frame, our tick) cannot say which part.
// On one frame in kPassEvery, every change of the surface the engine writes to
// ends a timestamp: the time until the next one is that pass - with its second
// eye, which is issued straight after each operation. Summed by what the pass
// writes (size, format, how many targets), so the same pass lines up across
// frames. Work that runs on another queue (DLSS's own) lands in whichever pass
// the GPU was in when it ran, not in the DLSS call's own interval.
// ---------------------------------------------------------------------------

constexpr int kPassStamps = 256;
constexpr int kPassSlots = 4;
constexpr int kPassRows = 48;
constexpr int kPassLabel = 64;
constexpr uint64_t kPassEvery = 23;     /* not a multiple of anything periodic */

struct PassSlot
{
    ID3D11Query* dj = nullptr;
    ID3D11Query* ts[kPassStamps + 1] = {};
    char         label[kPassStamps][kPassLabel];
    int          n = 0;          /* passes opened; ts[n] closes the last */
    int          stage = 0;      /* 0 free, 1 recording, 2 waiting on the GPU */
};

PassSlot*       g_pass = nullptr;            /* kPassSlots, made on first use */
int             g_passRec = -1;
ID3D11Resource* g_passKey = nullptr;
int             g_passKeyKind = -1;
int             g_passKeyCount = -1;
bool            g_passFailed = false;
uint64_t        g_passFrameNo = 0;

struct PassRow { char label[kPassLabel]; double ms; };
PassRow  g_passRows[kPassRows];
int      g_passRowCount = 0;
uint64_t g_passFrames = 0;
double   g_passTotalMs = 0.0;
int      g_passMost = 0;

/* Appends to a fixed buffer; a truncated write fills it and later ones stop. */
void append_to(char* buf, size_t cap, size_t& used, const char* fmt, ...)
{
    if (used + 1 >= cap)
        return;
    va_list args;
    va_start(args, fmt);
    const int wrote = _vsnprintf_s(buf + used, cap - used, _TRUNCATE, fmt, args);
    va_end(args);
    used = wrote < 0 ? cap - 1 : used + static_cast<size_t>(wrote);
}

void pass_describe(ID3D11Resource* r, int kind, int count, char* out, size_t n)
{
    switch (kind)
    {
    case 3: strcpy_s(out, n, "DLSS call, eye 0"); return;
    case 4: strcpy_s(out, n, "DLSS call, eye 1 (with its setup)"); return;
    case 5: strcpy_s(out, n, "after DLSS, before the next pass"); return;
    default: break;
    }
    const char* verb = kind == 2 ? "compute into" : kind == 1 ? "depth-only draw into" : "draw into";
    if (r == nullptr)
    {
        _snprintf_s(out, n, _TRUNCATE, "%s nothing", verb);
        return;
    }
    D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
    r->GetType(&dim);
    if (dim == D3D11_RESOURCE_DIMENSION_TEXTURE2D)
    {
        D3D11_TEXTURE2D_DESC d = {};
        static_cast<ID3D11Texture2D*>(r)->GetDesc(&d);
        char arr[16] = "";
        if (d.ArraySize > 1)
            _snprintf_s(arr, _TRUNCATE, " [%u]", d.ArraySize);
        if (count > 1)
            _snprintf_s(out, n, _TRUNCATE, "%s %d targets %ux%u%s fmt %u", verb, count, d.Width,
                        d.Height, arr, static_cast<unsigned>(d.Format));
        else
            _snprintf_s(out, n, _TRUNCATE, "%s %ux%u%s fmt %u", verb, d.Width, d.Height, arr,
                        static_cast<unsigned>(d.Format));
    }
    else if (dim == D3D11_RESOURCE_DIMENSION_TEXTURE3D)
    {
        D3D11_TEXTURE3D_DESC d = {};
        static_cast<ID3D11Texture3D*>(r)->GetDesc(&d);
        _snprintf_s(out, n, _TRUNCATE, "%s 3D %ux%ux%u fmt %u", verb, d.Width, d.Height, d.Depth,
                    static_cast<unsigned>(d.Format));
    }
    else if (dim == D3D11_RESOURCE_DIMENSION_BUFFER)
    {
        D3D11_BUFFER_DESC d = {};
        static_cast<ID3D11Buffer*>(r)->GetDesc(&d);
        _snprintf_s(out, n, _TRUNCATE, "%s a %u KB buffer", verb, (d.ByteWidth + 1023) / 1024);
    }
    else
    {
        _snprintf_s(out, n, _TRUNCATE, "%s a surface of dimension %d", verb, static_cast<int>(dim));
    }
}

void pass_add(const char* label, double ms)
{
    for (int i = 0; i < g_passRowCount; ++i)
        if (strcmp(g_passRows[i].label, label) == 0)
        {
            g_passRows[i].ms += ms;
            return;
        }
    if (g_passRowCount < kPassRows - 1)
    {
        PassRow& row = g_passRows[g_passRowCount++];
        strcpy_s(row.label, label);
        row.ms = ms;
        return;
    }
    /* the last row takes whatever did not get one of its own */
    PassRow& rest = g_passRows[kPassRows - 1];
    if (g_passRowCount == kPassRows - 1)
    {
        strcpy_s(rest.label, "every other pass");
        rest.ms = 0.0;
        g_passRowCount = kPassRows;
    }
    rest.ms += ms;
}

void pass_release_all()
{
    if (g_pass == nullptr)
        return;
    for (int k = 0; k < kPassSlots; ++k)
    {
        PassSlot& s = g_pass[k];
        if (s.dj) { s.dj->Release(); s.dj = nullptr; }
        for (ID3D11Query*& q : s.ts)
            if (q) { q->Release(); q = nullptr; }
    }
}

/* At the end of our tick: the engine's next frame starts here. */
void pass_open(ID3D11DeviceContext* ctx)
{
    g_passRec = -1;
    if (g_passFailed || (++g_passFrameNo % kPassEvery) != 0)
        return;

    if (g_pass == nullptr)
    {
        ID3D11Device* dev = engine_device();
        if (dev == nullptr)
            return;
        g_pass = new PassSlot[kPassSlots];
        const D3D11_QUERY_DESC dj = { D3D11_QUERY_TIMESTAMP_DISJOINT, 0 };
        const D3D11_QUERY_DESC ts = { D3D11_QUERY_TIMESTAMP, 0 };
        bool ok = true;
        for (int k = 0; k < kPassSlots && ok; ++k)
        {
            PassSlot& s = g_pass[k];
            ok = SUCCEEDED(dev->CreateQuery(&dj, &s.dj));
            for (int i = 0; i <= kPassStamps && ok; ++i)
                ok = SUCCEEDED(dev->CreateQuery(&ts, &s.ts[i]));
        }
        if (!ok)
        {
            pass_release_all();
            g_passFailed = true;
            BVR_WARN("GPU by pass: could not create timestamp queries; no breakdown.");
            return;
        }
    }

    for (int k = 0; k < kPassSlots; ++k)
    {
        PassSlot& s = g_pass[k];
        if (s.stage != 0)
            continue;
        ctx->Begin(s.dj);
        ctx->End(s.ts[0]);
        strcpy_s(s.label[0], "frame start, before the engine's first pass");
        s.n = 1;
        s.stage = 1;
        g_passRec = k;
        g_passKey = nullptr;
        g_passKeyKind = -1;
        g_passKeyCount = -1;
        return;
    }
    /* every slot still waiting on the GPU: skip this sample */
}

/* At the start of our tick: the engine's frame has been issued. */
void pass_close(ID3D11DeviceContext* ctx, bool keep)
{
    if (g_passRec < 0)
        return;
    PassSlot& s = g_pass[g_passRec];
    g_passRec = -1;
    ctx->End(s.ts[s.n]);
    ctx->End(s.dj);
    s.stage = keep ? 2 : 0;
}

void pass_collect(ID3D11DeviceContext* ctx)
{
    if (g_pass == nullptr)
        return;
    for (int k = 0; k < kPassSlots; ++k)
    {
        PassSlot& s = g_pass[k];
        if (s.stage != 2)
            continue;

        UINT64 stamps[kPassStamps + 1] = {};
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT dj = {};
        if (ctx->GetData(s.ts[s.n], &stamps[s.n], sizeof(UINT64), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK ||
            ctx->GetData(s.dj, &dj, sizeof(dj), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK)
            continue;
        bool ready = true;
        for (int i = 0; i < s.n && ready; ++i)
            ready = ctx->GetData(s.ts[i], &stamps[i], sizeof(UINT64),
                                 D3D11_ASYNC_GETDATA_DONOTFLUSH) == S_OK;
        if (!ready)
            continue;

        s.stage = 0;
        if (dj.Disjoint || dj.Frequency == 0)
            continue;
        bool ordered = true;
        for (int i = 0; i < s.n && ordered; ++i)
            ordered = stamps[i + 1] >= stamps[i];
        if (!ordered)
            continue;

        const double toMs = 1000.0 / static_cast<double>(dj.Frequency);
        for (int i = 0; i < s.n; ++i)
            pass_add(s.label[i], static_cast<double>(stamps[i + 1] - stamps[i]) * toMs);
        g_passTotalMs += static_cast<double>(stamps[s.n] - stamps[0]) * toMs;
        if (s.n > g_passMost)
            g_passMost = s.n;
        ++g_passFrames;
    }
}

void pass_report()
{
    if (g_passFrames == 0)
        return;

    const double frames = static_cast<double>(g_passFrames);
    const double total = g_passTotalMs / frames;
    char line[3072] = {};
    size_t used = 0;
    append_to(line, sizeof(line), used,
              "GPU by pass (%llu sampled frame(s), both eyes, up to %d passes a frame): %.2f ms a "
              "frame from the engine's frame start to our tick. Largest:",
              static_cast<unsigned long long>(g_passFrames), g_passMost, total);

    bool taken[kPassRows] = {};
    for (int rank = 0; rank < 16 && rank < g_passRowCount; ++rank)
    {
        int best = -1;
        for (int i = 0; i < g_passRowCount; ++i)
            if (!taken[i] && (best < 0 || g_passRows[i].ms > g_passRows[best].ms))
                best = i;
        if (best < 0)
            break;
        taken[best] = true;
        const double ms = g_passRows[best].ms / frames;
        append_to(line, sizeof(line), used, "%s %s %.2f ms (%.0f%%)", rank ? " |" : "",
                  g_passRows[best].label, ms, total > 0.0 ? 100.0 * ms / total : 0.0);
    }
    BVR_INFO("%s", line);

    g_passRowCount = 0;
    g_passFrames = 0;
    g_passTotalMs = 0.0;
    g_passMost = 0;
}

// ---------------------------------------------------------------------------
// VRAM
// ---------------------------------------------------------------------------

IDXGIAdapter3* adapter3()
{
    static IDXGIAdapter3* cached = nullptr;
    static bool tried = false;
    if (tried)
        return cached;

    ID3D11Device* dev = engine_device();
    if (dev == nullptr)
        return nullptr;
    tried = true;

    IDXGIDevice* dxgi = nullptr;
    if (SUCCEEDED(dev->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgi))) &&
        dxgi != nullptr)
    {
        IDXGIAdapter* adapter = nullptr;
        if (SUCCEEDED(dxgi->GetAdapter(&adapter)) && adapter != nullptr)
        {
            adapter->QueryInterface(__uuidof(IDXGIAdapter3), reinterpret_cast<void**>(&cached));
            adapter->Release();
        }
        dxgi->Release();
    }
    return cached;
}

// ---------------------------------------------------------------------------
// the busiest threads - sampled off the render thread, every ~5 s
// ---------------------------------------------------------------------------

SRWLOCK g_threadLock = SRWLOCK_INIT;
char    g_threadLine[768] = "(first sample pending)";
bool    g_samplerStarted = false;       /* render thread only */

using PFN_NtQueryInformationThread = LONG(NTAPI*)(HANDLE, int, PVOID, ULONG, PULONG);
using PFN_GetThreadDescription = HRESULT(WINAPI*)(HANDLE, LPWSTR*);

/* The thread's own name when it has one, else the module its start address is
   in - enough to tell rgl's threads from the NVIDIA driver's and from Mono's. */
void thread_label(DWORD tid, char* out, size_t n)
{
    out[0] = 0;
    HANDLE h = OpenThread(THREAD_QUERY_INFORMATION | THREAD_QUERY_LIMITED_INFORMATION, FALSE, tid);
    if (h == nullptr)
        h = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, tid);
    if (h == nullptr)
    {
        _snprintf_s(out, n, _TRUNCATE, "?");
        return;
    }

    static const PFN_GetThreadDescription getDescription =
        reinterpret_cast<PFN_GetThreadDescription>(
            GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "GetThreadDescription"));
    if (getDescription != nullptr)
    {
        LPWSTR desc = nullptr;
        if (SUCCEEDED(getDescription(h, &desc)) && desc != nullptr)
        {
            if (desc[0] != 0)
                WideCharToMultiByte(CP_UTF8, 0, desc, -1, out, static_cast<int>(n), nullptr, nullptr);
            LocalFree(desc);
        }
    }

    if (out[0] == 0)
    {
        static const PFN_NtQueryInformationThread query =
            reinterpret_cast<PFN_NtQueryInformationThread>(
                GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationThread"));
        PVOID start = nullptr;
        constexpr int kThreadQuerySetWin32StartAddress = 9;
        if (query != nullptr &&
            query(h, kThreadQuerySetWin32StartAddress, &start, sizeof(start), nullptr) >= 0 &&
            start != nullptr)
        {
            HMODULE mod = nullptr;
            if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                   static_cast<LPCWSTR>(start), &mod) && mod != nullptr)
            {
                wchar_t path[MAX_PATH];
                const DWORD len = GetModuleFileNameW(mod, path, MAX_PATH);
                if (len > 0)
                {
                    const wchar_t* slash = wcsrchr(path, L'\\');
                    WideCharToMultiByte(CP_UTF8, 0, slash ? slash + 1 : path, -1, out,
                                        static_cast<int>(n), nullptr, nullptr);
                }
            }
        }
    }

    CloseHandle(h);
    if (out[0] == 0)
        _snprintf_s(out, n, _TRUNCATE, "?");
}

struct ThreadCpu
{
    DWORD    tid;
    uint64_t cpu;       /* kernel + user, 100 ns */
};

constexpr int kMaxThreads = 1024;
ThreadCpu g_prev[kMaxThreads];
ThreadCpu g_cur[kMaxThreads];

DWORD WINAPI sampler_main(LPVOID)
{
    const DWORD pid = GetCurrentProcessId();
    int nPrev = 0;
    int64_t prevWall = 0;

    for (;;)
    {
        Sleep(5000);
        if (!perf_live())
        {
            nPrev = 0;
            prevWall = 0;
            continue;
        }

        HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snap == INVALID_HANDLE_VALUE)
            continue;

        const int64_t wall = qpc();
        struct Top { DWORD tid; uint64_t delta; };
        Top top[6] = {};
        int nCur = 0;

        THREADENTRY32 te = {};
        te.dwSize = sizeof(te);
        if (Thread32First(snap, &te))
        {
            do
            {
                if (te.th32OwnerProcessID != pid || nCur >= kMaxThreads)
                    continue;
                HANDLE h = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, te.th32ThreadID);
                if (h == nullptr)
                    continue;
                FILETIME created = {}, exited = {}, kernel = {}, user = {};
                if (GetThreadTimes(h, &created, &exited, &kernel, &user))
                {
                    const uint64_t cpu =
                        ((static_cast<uint64_t>(kernel.dwHighDateTime) << 32) | kernel.dwLowDateTime) +
                        ((static_cast<uint64_t>(user.dwHighDateTime) << 32) | user.dwLowDateTime);
                    g_cur[nCur].tid = te.th32ThreadID;
                    g_cur[nCur].cpu = cpu;
                    ++nCur;

                    for (int i = 0; i < nPrev; ++i)
                    {
                        if (g_prev[i].tid != te.th32ThreadID)
                            continue;
                        const uint64_t d = cpu > g_prev[i].cpu ? cpu - g_prev[i].cpu : 0;
                        for (int t = 0; t < 6; ++t)
                        {
                            if (d > top[t].delta)
                            {
                                for (int m = 5; m > t; --m)
                                    top[m] = top[m - 1];
                                top[t].tid = te.th32ThreadID;
                                top[t].delta = d;
                                break;
                            }
                        }
                        break;
                    }
                }
                CloseHandle(h);
            } while (Thread32Next(snap, &te));
        }
        CloseHandle(snap);

        if (nPrev > 0 && prevWall != 0)
        {
            const double wall100ns = qpc_to_ms(wall - prevWall) * 10000.0;
            const DWORD render = present_thread_id();
            char line[768];
            line[0] = 0;
            int used = 0;
            for (int t = 0; t < 6 && top[t].delta > 0; ++t)
            {
                char label[96];
                thread_label(top[t].tid, label, sizeof(label));
                const double pct = wall100ns > 0.0
                                       ? 100.0 * static_cast<double>(top[t].delta) / wall100ns
                                       : 0.0;
                const int w = _snprintf_s(line + used, sizeof(line) - used, _TRUNCATE,
                                          "%s%s%s#%lu %.0f%%", t ? ", " : "",
                                          top[t].tid == render ? "RENDER " : "", label,
                                          static_cast<unsigned long>(top[t].tid), pct);
                if (w < 0)
                    break;
                used += w;
            }
            AcquireSRWLockExclusive(&g_threadLock);
            strcpy_s(g_threadLine, line[0] ? line : "(no thread used measurable CPU)");
            ReleaseSRWLockExclusive(&g_threadLock);
        }

        memcpy(g_prev, g_cur, sizeof(ThreadCpu) * static_cast<size_t>(nCur));
        nPrev = nCur;
        prevWall = wall;
    }
}

/* ===========================================================================
   WHERE THE RENDER THREAD'S TIME GOES (TEST 188).

   "Native stereo ~35 fps with or without DLSS, GPU at 50%." The perf line had
   the render thread at 94-99% of a core with the stereo code's own decide and
   issue under 2 ms of a 29 ms frame - CPU-bound, and not in anything the
   counters name. Thread CPU says THAT it is busy; only a profile says WHERE.

   So a sampling profiler on that one thread: about a thousand times a second
   it is suspended, its instruction pointer and a page of its stack are read,
   and it is resumed. Nothing between Suspend and Resume takes a user-mode lock
   (GetThreadContext and ReadProcessMemory are system calls), so it cannot
   deadlock on a lock the render thread holds. Module lookups happen after the
   resume, against a module list refreshed between reports.

   Reported every ~5 s: time by module at the instruction pointer; how often
   our DLL is anywhere on the stack (a scan for return addresses, so an
   over-estimate); and the hottest addresses, plus the nearest caller inside
   TaleWorlds.Native and inside BannerlordVR.Native. stereo_profile = 0 off.
   =========================================================================== */
struct ModRange
{
    uintptr_t base;
    uintptr_t end;
    char      name[48];
};

constexpr int kMods = 256;
ModRange g_mods[kMods];
int      g_modCount = 0;
int      g_modEngine = -1;
int      g_modOurs = -1;

void refresh_modules()
{
    HMODULE list[kMods];
    DWORD needed = 0;
    g_modCount = 0;
    g_modEngine = -1;
    g_modOurs = -1;
    if (!K32EnumProcessModules(GetCurrentProcess(), list, sizeof(list), &needed))
        return;
    const int n = static_cast<int>(needed / sizeof(HMODULE)) < kMods
                      ? static_cast<int>(needed / sizeof(HMODULE)) : kMods;
    for (int i = 0; i < n; ++i)
    {
        MODULEINFO mi = {};
        if (!K32GetModuleInformation(GetCurrentProcess(), list[i], &mi, sizeof(mi)))
            continue;
        char path[MAX_PATH] = {};
        GetModuleFileNameA(list[i], path, MAX_PATH);
        const char* name = strrchr(path, '\\');
        name = name ? name + 1 : path;
        ModRange& m = g_mods[g_modCount];
        m.base = reinterpret_cast<uintptr_t>(mi.lpBaseOfDll);
        m.end = m.base + mi.SizeOfImage;
        strncpy_s(m.name, name, _TRUNCATE);
        if (_stricmp(m.name, "TaleWorlds.Native.dll") == 0) g_modEngine = g_modCount;
        if (_stricmp(m.name, "BannerlordVR.Native.dll") == 0) g_modOurs = g_modCount;
        ++g_modCount;
    }
}

int module_of(uintptr_t a)
{
    for (int i = 0; i < g_modCount; ++i)
        if (a >= g_mods[i].base && a < g_mods[i].end)
            return i;
    return -1;
}

struct Hit
{
    uintptr_t key;
    uint32_t  n;
};

constexpr int kHitSlots = 4096;

void hit_add(Hit* table, uintptr_t key)
{
    size_t h = (key * 0x9E3779B97F4A7C15ull) >> 52;       /* 12 bits */
    for (int probe = 0; probe < kHitSlots; ++probe)
    {
        Hit& e = table[(h + probe) & (kHitSlots - 1)];
        if (e.key == key) { ++e.n; return; }
        if (e.key == 0) { e.key = key; e.n = 1; return; }
    }
}

void hit_top(const Hit* table, Hit* out, int k)
{
    for (int i = 0; i < k; ++i) out[i] = Hit{ 0, 0 };
    for (int s = 0; s < kHitSlots; ++s)
    {
        if (table[s].key == 0) continue;
        for (int i = 0; i < k; ++i)
        {
            if (table[s].n > out[i].n)
            {
                for (int m = k - 1; m > i; --m) out[m] = out[m - 1];
                out[i] = table[s];
                break;
            }
        }
    }
}

void describe_addr(uintptr_t a, char* out, size_t n)
{
    const int m = module_of(a);
    if (m < 0)
        _snprintf_s(out, n, _TRUNCATE, "%p", reinterpret_cast<void*>(a));
    else
        _snprintf_s(out, n, _TRUNCATE, "%s+0x%llX", g_mods[m].name,
                    static_cast<unsigned long long>(a - g_mods[m].base));
}

Hit g_hitRip[kHitSlots];
Hit g_hitEngine[kHitSlots];
Hit g_hitOurs[kHitSlots];

void report_list(const char* title, const Hit* table, uint64_t samples, bool round)
{
    Hit top[10];
    hit_top(table, top, 10);
    char line[1400] = {};
    size_t used = 0;
    for (int i = 0; i < 10 && top[i].n > 0; ++i)
    {
        char a[96];
        describe_addr(top[i].key, a, sizeof(a));
        const int w = _snprintf_s(line + used, sizeof(line) - used, _TRUNCATE, "%s%s%s %.1f%%",
                                  i ? ", " : "", a, round ? "(~)" : "",
                                  100.0 * top[i].n / static_cast<double>(samples));
        if (w < 0) break;
        used += static_cast<size_t>(w);
    }
    BVR_INFO("Render thread profile: %s: %s", title, line[0] ? line : "(none)");
}

/* Fraction of each 5 s report period spent sampling. Re-read at every report,
   so it can be changed without restarting the game. */
double duty_now()
{
    double d = static_cast<double>(config_float("stereo_profile_duty", 0.1f));
    if (d < 0.01) d = 0.01;
    if (d > 1.0)  d = 1.0;
    return d;
}

/* THE PROFILER SAMPLES IN WINDOWS, NOT CONTINUOUSLY.
 *
 * One sample is SuspendThread / GetThreadContext / ReadProcessMemory of 8 KB of
 * stack / ResumeThread, on the PRESENT thread. At the 1 ms cadence below that is
 * several hundred suspensions a second, for as long as native stereo runs, and
 * SuspendThread is asynchronous - it can land inside a D3D call. The 2026-09-17
 * run carried it the whole time (3186 samples per 5 s report). That is a lot of
 * jitter to pay in normal play for an instrument that is only read when
 * something is being diagnosed.
 *
 * A histogram does not need anything like that many samples. The 5 s report
 * cadence and every line of the report are unchanged; sampling now runs for
 * stereo_profile_duty of each period (0.1 = the first ~500 ms, ~600 samples)
 * and idles for the rest. The window always extends until the existing
 * 50-sample floor is cleared, so a report is never starved.
 *
 * stereo_profile_duty = 1.0 restores the old continuous behaviour for a
 * measurement session. stereo_profile = 0 is still the off switch. */
DWORD WINAPI profiler_main(LPVOID)
{
    HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr,
                                          CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                                          TIMER_ALL_ACCESS);
    DWORD tid = 0;
    HANDLE th = nullptr;
    uint64_t samples = 0, ours = 0, engineOnStack = 0;
    uint64_t byMod[kMods + 1] = {};
    int64_t lastReport = qpc();
    std::vector<uint8_t> stack(8192);

    double duty = duty_now();

    refresh_modules();

    /* One suspend-and-read of the Present thread. False means it could not be
       taken this time, which is not worth reporting - the next one will be. */
    auto take_sample = [&]() -> bool
    {
        const DWORD want = present_thread_id();
        if (want == 0)
            return false;
        if (want != tid)
        {
            if (th) CloseHandle(th);
            th = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION,
                            FALSE, want);
            tid = th ? want : 0;
            if (!th)
                return false;
        }

        CONTEXT c = {};
        c.ContextFlags = CONTEXT_CONTROL;
        SIZE_T got = 0;
        bool ok = false;
        if (SuspendThread(th) != static_cast<DWORD>(-1))
        {
            if (GetThreadContext(th, &c))
            {
                ok = true;
                ReadProcessMemory(GetCurrentProcess(), reinterpret_cast<LPCVOID>(c.Rsp),
                                  stack.data(), stack.size(), &got);
            }
            ResumeThread(th);
        }
        if (!ok)
            return false;

        ++samples;
        const uintptr_t rip = static_cast<uintptr_t>(c.Rip);
        const int m = module_of(rip);
        ++byMod[m < 0 ? kMods : m];
        hit_add(g_hitRip, rip & ~static_cast<uintptr_t>(0x3F));

        bool sawOurs = (m == g_modOurs && m >= 0);
        bool sawEngine = (m == g_modEngine && m >= 0);
        uintptr_t firstEngine = sawEngine ? rip : 0;
        uintptr_t firstOurs = sawOurs ? rip : 0;
        const size_t words = got / sizeof(uintptr_t);
        const uintptr_t* q = reinterpret_cast<const uintptr_t*>(stack.data());
        for (size_t i = 0; i < words && (!firstEngine || !firstOurs); ++i)
        {
            const uintptr_t v = q[i];
            if (g_modOurs >= 0 && !firstOurs && v >= g_mods[g_modOurs].base &&
                v < g_mods[g_modOurs].end)
            {
                firstOurs = v;
                sawOurs = true;
            }
            if (g_modEngine >= 0 && !firstEngine && v >= g_mods[g_modEngine].base &&
                v < g_mods[g_modEngine].end)
            {
                firstEngine = v;
                sawEngine = true;
            }
        }
        if (sawOurs) ++ours;
        if (sawEngine) ++engineOnStack;
        if (firstEngine) hit_add(g_hitEngine, firstEngine);
        if (firstOurs) hit_add(g_hitOurs, firstOurs);
        return true;
    };

    for (;;)
    {
        /* Sampling for the first duty-fraction of the period, and beyond it if
           the 50-sample floor has not been cleared yet. */
        const bool inWindow = qpc_to_ms(qpc() - lastReport) < 5000.0 * duty ||
                              samples < 50;

        if (!inWindow)
        {
            /* Idling out the rest of the period. Nothing is being suspended. */
            Sleep(10);
        }
        else if (timer != nullptr)
        {
            LARGE_INTEGER due = {};
            due.QuadPart = -10000;                      /* 1 ms */
            SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE);
            WaitForSingleObject(timer, 50);
        }
        else
        {
            Sleep(1);
        }

        if (!perf_live() || !config_bool("stereo_profile", true))
        {
            if (th) { CloseHandle(th); th = nullptr; tid = 0; }
            samples = ours = engineOnStack = 0;
            memset(byMod, 0, sizeof(byMod));
            memset(g_hitRip, 0, sizeof(g_hitRip));
            memset(g_hitEngine, 0, sizeof(g_hitEngine));
            memset(g_hitOurs, 0, sizeof(g_hitOurs));
            Sleep(200);
            lastReport = qpc();
            continue;
        }

        if (inWindow)
            take_sample();

        const int64_t now = qpc();
        if (qpc_to_ms(now - lastReport) < 5000.0 || samples < 50)
            continue;
        lastReport = now;
        duty = duty_now();

        char mods[900] = {};
        size_t used = 0;
        for (int pass = 0; pass < 8; ++pass)
        {
            int best = -2;
            uint64_t bestN = 0;
            for (int i = 0; i <= kMods; ++i)
                if (byMod[i] > bestN) { bestN = byMod[i]; best = i; }
            if (best == -2)
                break;
            const int w = _snprintf_s(mods + used, sizeof(mods) - used, _TRUNCATE, "%s%s %.1f%%",
                                      pass ? ", " : "",
                                      best == kMods ? "(no module)" : g_mods[best].name,
                                      100.0 * bestN / static_cast<double>(samples));
            byMod[best] = 0;
            if (w < 0) break;
            used += static_cast<size_t>(w);
        }
        BVR_INFO("Render thread profile over %llu sample(s): by module at the instruction "
                 "pointer: %s | BannerlordVR.Native anywhere on the stack in %.1f%%, "
                 "TaleWorlds.Native in %.1f%%.",
                 static_cast<unsigned long long>(samples), mods,
                 100.0 * ours / static_cast<double>(samples),
                 100.0 * engineOnStack / static_cast<double>(samples));
        report_list("hottest instruction pointers (64-byte buckets)", g_hitRip, samples, true);
        report_list("nearest TaleWorlds.Native caller", g_hitEngine, samples, false);
        report_list("nearest BannerlordVR.Native caller", g_hitOurs, samples, false);

        samples = ours = engineOnStack = 0;
        memset(byMod, 0, sizeof(byMod));
        memset(g_hitRip, 0, sizeof(g_hitRip));
        memset(g_hitEngine, 0, sizeof(g_hitEngine));
        memset(g_hitOurs, 0, sizeof(g_hitOurs));
        refresh_modules();
    }
}

} // namespace

void stereo_perf_tick_begin(ID3D11DeviceContext* ctx)
{
    if (!perf_live())
    {
        if (g_wasLive)
        {
            g_wasLive = false;
            if (ctx != nullptr)
            {
                ContextLock lock;
                gpu_close_open(ctx);
                pass_close(ctx, false);
            }
            reset_window();
        }
        return;
    }

    const int64_t now = qpc();

    if (!g_wasLive)
    {
        g_wasLive = true;
        reset_window();
        g_lastTickBegin = now;
        g_tickBegin = now;
        g_tickEnd = 0;
        if (!g_samplerStarted)
        {
            g_samplerStarted = true;
            HANDLE th = CreateThread(nullptr, 0, &sampler_main, nullptr, 0, nullptr);
            if (th != nullptr)
                CloseHandle(th);
            HANDLE pr = CreateThread(nullptr, 0, &profiler_main, nullptr, 0, nullptr);
            if (pr != nullptr)
                CloseHandle(pr);
        }
        return;
    }

    const double period = qpc_to_ms(now - g_lastTickBegin);
    g_sumPeriod += period;
    if (period > g_maxPeriod)
        g_maxPeriod = period;
    ++g_nPeriod;
    g_lastTickBegin = now;
    g_tickBegin = now;
    water_probe_frame();

    if (ctx != nullptr)
    {
        ContextLock lock;
        if (g_gpuOpen >= 0 && g_gpu[g_gpuOpen].stage == 1)
        {
            ctx->End(g_gpu[g_gpuOpen].t1);
            g_gpu[g_gpuOpen].stage = 2;
        }
        pass_close(ctx, true);
        gpu_collect(ctx);
        pass_collect(ctx);
    }
}

void stereo_perf_tick_end(ID3D11DeviceContext* ctx)
{
    if (!g_wasLive || !perf_live())
        return;

    const int64_t now = qpc();
    g_sumTick += qpc_to_ms(now - g_tickBegin);
    ++g_nTick;
    g_tickEnd = now;

    if (ctx != nullptr)
    {
        ContextLock lock;
        if (g_gpuOpen >= 0 && g_gpu[g_gpuOpen].stage == 2)
        {
            GpuSlot& s = g_gpu[g_gpuOpen];
            ctx->End(s.t2);
            ctx->End(s.dj);
            s.stage = 3;
            g_gpuOpen = -1;
        }
        else
        {
            gpu_close_open(ctx);
        }
        gpu_open(ctx);
        pass_open(ctx);
    }
}

void stereo_perf_present_returned()
{
    if (!g_wasLive || g_tickEnd == 0 || !perf_live())
        return;
    const double ms = qpc_to_ms(qpc() - g_tickEnd);
    g_sumPresent += ms;
    if (ms > g_maxPresent)
        g_maxPresent = ms;
    ++g_nPresent;
}

void stereo_perf_ngx_stamp(ID3D11DeviceContext* ctx, int point)
{
    if (!g_wasLive || !perf_live() || ctx == nullptr || point < 0 || point > 2)
        return;
    stereo_perf_pass(ctx, nullptr, 3 + point, 0);
    if (g_gpuOpen < 0 || g_gpu[g_gpuOpen].stage != 1)
        return;
    GpuSlot& s = g_gpu[g_gpuOpen];
    /* one evaluation per frame, in order */
    if (s.nHave[point] || (point > 0 && !s.nHave[point - 1]))
        return;
    ctx->End(s.n[point]);
    s.nHave[point] = true;
}

void stereo_perf_pass(ID3D11DeviceContext* ctx, ID3D11Resource* out, int kind, int count)
{
    if (g_passRec < 0 || ctx == nullptr)
        return;
    if (out == g_passKey && kind == g_passKeyKind && count == g_passKeyCount)
        return;
    g_passKey = out;
    g_passKeyKind = kind;
    g_passKeyCount = count;

    PassSlot& s = g_pass[g_passRec];
    if (s.n >= kPassStamps)
        return;            /* the rest of the frame stays in the last pass */
    pass_describe(out, kind, count, s.label[s.n], kPassLabel);
    ctx->End(s.ts[s.n]);
    ++s.n;
}

void stereo_perf_ngx_cpu(int64_t ticks)
{
    if (!perf_live())
        return;
    g_ngxCpuTicks.fetch_add(ticks, std::memory_order_relaxed);
    g_ngxCpuCount.fetch_add(1, std::memory_order_relaxed);
}

int64_t stereo_perf_map_begin(D3D11_MAP type)
{
    if (type != D3D11_MAP_READ && type != D3D11_MAP_READ_WRITE)
        return 0;
    if (!perf_live())
        return 0;
    return qpc();
}

void stereo_perf_map_end(int64_t t0, ID3D11Resource* r)
{
    if (t0 == 0)
        return;
    const int64_t dt = qpc() - t0;
    g_mapTicks.fetch_add(dt, std::memory_order_relaxed);
    g_mapCount.fetch_add(1, std::memory_order_relaxed);

    const double ms = qpc_to_ms(dt);
    if (ms < 1.0)
        return;

    /* WHO WAITS (TEST 185). Frames 0 and 1 are this function and hooked_map; the
       rest is the caller - the engine, NGX, or us. */
    void* frames[kMapFrames + 2] = {};
    const USHORT n = RtlCaptureStackBackTrace(2, kMapFrames, frames, nullptr);
    uint64_t h = 1469598103934665603ull;
    for (USHORT i = 0; i < n; ++i)
        h = (h ^ reinterpret_cast<uintptr_t>(frames[i])) * 1099511628211ull;

    UINT w = 0, ht = 0, fmt = 0, usage = 0;
    D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
    if (r != nullptr)
    {
        r->GetType(&dim);
        if (dim == D3D11_RESOURCE_DIMENSION_TEXTURE2D)
        {
            D3D11_TEXTURE2D_DESC d = {};
            static_cast<ID3D11Texture2D*>(r)->GetDesc(&d);
            w = d.Width; ht = d.Height; fmt = d.Format; usage = d.Usage;
        }
        else if (dim == D3D11_RESOURCE_DIMENSION_BUFFER)
        {
            D3D11_BUFFER_DESC d = {};
            static_cast<ID3D11Buffer*>(r)->GetDesc(&d);
            w = d.ByteWidth; usage = d.Usage;
        }
    }

    AcquireSRWLockExclusive(&g_mapSiteLock);
    int row = -1;
    for (int i = 0; i < g_mapSiteCount; ++i)
        if (g_mapSites[i].hash == h) { row = i; break; }
    if (row < 0 && g_mapSiteCount < kMapSites)
    {
        row = g_mapSiteCount++;
        MapSite& m = g_mapSites[row];
        m = MapSite{};
        m.hash = h;
        m.nFrames = n;
        for (USHORT i = 0; i < n; ++i) m.frames[i] = frames[i];
        m.dim = static_cast<int>(dim); m.w = w; m.h = ht; m.fmt = fmt; m.usage = usage;
    }
    if (row >= 0)
    {
        ++g_mapSites[row].count;
        g_mapSites[row].ms += ms;
    }
    ReleaseSRWLockExclusive(&g_mapSiteLock);
}

void stereo_perf_report(double decideMsPerFrame, double issueMsPerFrame, uint64_t frames,
                        uint64_t twinBytes)
{
    if (!g_wasLive || !perf_live())
        return;

    const double n = g_nPeriod ? static_cast<double>(g_nPeriod)
                               : static_cast<double>(frames ? frames : 1);
    const double period = g_nPeriod ? g_sumPeriod / static_cast<double>(g_nPeriod) : 0.0;
    const double tick = g_nTick ? g_sumTick / static_cast<double>(g_nTick) : 0.0;
    const double present = g_nPresent ? g_sumPresent / static_cast<double>(g_nPresent) : 0.0;
    const double mapMs = qpc_to_ms(g_mapTicks.exchange(0, std::memory_order_relaxed)) / n;
    const double maps = static_cast<double>(g_mapCount.exchange(0, std::memory_order_relaxed)) / n;
    const double gpuEngine = g_nGpu ? g_sumGpuEngine / static_cast<double>(g_nGpu) : 0.0;
    const double gpuTick = g_nGpu ? g_sumGpuTick / static_cast<double>(g_nGpu) : 0.0;

    double usedGb = 0.0, budgetGb = 0.0;
    if (IDXGIAdapter3* a = adapter3())
    {
        DXGI_QUERY_VIDEO_MEMORY_INFO info = {};
        if (SUCCEEDED(a->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info)))
        {
            usedGb = static_cast<double>(info.CurrentUsage) / (1024.0 * 1024.0 * 1024.0);
            budgetGb = static_cast<double>(info.Budget) / (1024.0 * 1024.0 * 1024.0);
        }
    }

    char threads[768];
    AcquireSRWLockShared(&g_threadLock);
    strcpy_s(threads, g_threadLine);
    ReleaseSRWLockShared(&g_threadLock);

    BVR_INFO("Stereo perf: frame period %.1f ms mean / %.1f max (%.1f fps) | render thread "
             "per frame: our Present tick %.2f ms, the real Present call %.2f ms mean / %.1f "
             "max, stereo deciding %.2f ms, issuing %.2f ms, blocked in Map(READ) %.2f ms over "
             "%.1f map(s) | GPU timestamps: engine frame %.2f ms mean / %.1f max, our Present "
             "tick %.2f ms (%llu frame(s) measured) | VRAM %.2f of %.2f GB budget in use, "
             "twins ~%llu MB | busiest threads over ~5 s: %s",
             period, g_maxPeriod, period > 0.0 ? 1000.0 / period : 0.0,
             tick, present, g_maxPresent, decideMsPerFrame, issueMsPerFrame, mapMs, maps,
             gpuEngine, g_maxGpuEngine, gpuTick, static_cast<unsigned long long>(g_nGpu),
             usedGb, budgetGb, static_cast<unsigned long long>(twinBytes >> 20), threads);

    {
        const uint64_t cpuN = g_ngxCpuCount.exchange(0, std::memory_order_relaxed);
        const double cpuMs = qpc_to_ms(g_ngxCpuTicks.exchange(0, std::memory_order_relaxed));
        if (g_nNgx0 != 0 || cpuN != 0)
            BVR_INFO("Stereo DLSS cost: GPU %.2f ms for the game's eye (%llu frame(s)), %.2f ms "
                     "for eye 1 (%llu frame(s)); CPU %.2f ms per evaluation call (%llu call(s)), "
                     "%.2f a frame. GPU numbers are D3D11 timestamps around the calls - if DLSS "
                     "finishes its work on another queue they read low and the wait shows up "
                     "as CPU time or Map(READ) instead.",
                     g_nNgx0 ? g_sumNgx0 / static_cast<double>(g_nNgx0) : 0.0,
                     static_cast<unsigned long long>(g_nNgx0),
                     g_nNgx1 ? g_sumNgx1 / static_cast<double>(g_nNgx1) : 0.0,
                     static_cast<unsigned long long>(g_nNgx1),
                     cpuN ? cpuMs / static_cast<double>(cpuN) : 0.0,
                     static_cast<unsigned long long>(cpuN), static_cast<double>(cpuN) / n);
    }

    report_map_sites(n);
    pass_report();

    if (usedGb > 0.0 && budgetGb > 0.0 && usedGb > budgetGb)
        BVR_WARN("Stereo perf: VRAM in use (%.2f GB) is OVER the budget Windows gives this "
                 "process (%.2f GB). Surfaces are being paged out of video memory, and that "
                 "alone can halve the frame rate.", usedGb, budgetGb);

    reset_window();
}

} // namespace bvr
