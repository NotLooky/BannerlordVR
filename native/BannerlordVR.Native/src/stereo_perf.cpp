#include "stereo_perf.h"

#include <windows.h>
#include <dxgi1_4.h>
#include <tlhelp32.h>

#include "bvr_config.h"
#include "bvr_log.h"
#include "d3d_hooks.h"
#include "stereo_dup.h"

#include <atomic>
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
   frames later without ever waiting - the same ring AFW's cost line uses. */
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

    if (ctx != nullptr)
    {
        ContextLock lock;
        if (g_gpuOpen >= 0 && g_gpu[g_gpuOpen].stage == 1)
        {
            ctx->End(g_gpu[g_gpuOpen].t1);
            g_gpu[g_gpuOpen].stage = 2;
        }
        gpu_collect(ctx);
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
    if (g_gpuOpen < 0 || g_gpu[g_gpuOpen].stage != 1)
        return;
    GpuSlot& s = g_gpu[g_gpuOpen];
    /* one evaluation per frame, in order */
    if (s.nHave[point] || (point > 0 && !s.nHave[point - 1]))
        return;
    ctx->End(s.n[point]);
    s.nHave[point] = true;
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

    if (usedGb > 0.0 && budgetGb > 0.0 && usedGb > budgetGb)
        BVR_WARN("Stereo perf: VRAM in use (%.2f GB) is OVER the budget Windows gives this "
                 "process (%.2f GB). Surfaces are being paged out of video memory, and that "
                 "alone can halve the frame rate.", usedGb, budgetGb);

    reset_window();
}

} // namespace bvr
