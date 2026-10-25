#include "water_spec_aa.h"

#include "bvr_config.h"
#include "bvr_log.h"

#include <windows.h>
#include <d3dcompiler.h>

#include <cstdint>
#include <cstdio>
#include <atomic>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>

/* TEST 544: WHY THE SEA SPARKLES AND CRAWLS.
 *
 * water_surface.rs shades the War Sails sea (and the coast seen from a village)
 * with a sun highlight of gloss 0.96 through compute_specular_lighting - GGX
 * alpha^2 ~ 2.6e-6, a peak near 390000 - and a sky reflection read from the
 * cubemap at mip 0. Nothing widens either when one pixel covers many waves, so
 * the far sea is fireflies, and the temporal filter cannot settle them: the sea
 * writes colour only, so DLSS/TAA see the depth and motion of what lies UNDER it.
 * The refraction is also point-sampled at normal-distorted coordinates.
 *
 * First try (Tokuyoshi & Kaplanyan, roughness from ddx/ddy of the normal) made it
 * WORSE: the far sea's normals already change at pixel rate, so the derivative
 * read that noise as variance and widened the glint over the whole far sea while
 * the same noisy normals kept varying under it - a bigger, grainier glint field.
 *
 * So the widening follows DISTANCE, which is smooth across pixels and steady over
 * time. Past 20 m, toward water_spec_aa_far_m, the wave normal fades to
 * water_spec_aa_far_normal of its strength (water_quality 0 never weakens it at
 * any range; the high path only to 0.85), and water_spec_aa_far_alpha2 is added
 * to GGX alpha^2 for the sun glint and sets the sky reflection's cube mip. Near
 * water is untouched. The refraction reads bilinear.
 *
 * THE GREY GRAIN ITSELF (TEST 547, measured with water_debug_bands at night):
 * the water adds fresnel * GGX * G / (4 N.V + 0.1) to its specular - a highlight
 * with no light colour, no shadow and no exposure, amplified at grazing angles.
 * It was 2/3 of the sea's brightness and twice the noise of the sky reflection,
 * glowing grey under a night sky. Both earlier tries WIDENED it, which is why
 * each looked worse. It is scaled by water_ggx_extra (0 = gone, 1 = the engine's);
 * the sun and moon keep their lit, shadowed glint through sun_lighting.
 *
 * The shader is rebuilt from the game's own Shaders\Sources\water_surface.rs at
 * runtime - patched as text, never copied into this repo. fxc /O3 with the
 * reflection stripped reproduces every shipped variant byte for byte, which is how
 * the table below was made and how a variant is recognised. A game patch that
 * changes the shader changes its hash, and then nothing here runs. */

namespace bvr {
namespace {

constexpr unsigned kLowQuality = 1, kProjectedGrid = 2, kGlobalReflection = 4, kPcss = 16;

struct Variant { uint64_t hash; unsigned flags; };

/* Every distinct main_ps of water_surface.rs over LOW_QUALITY, PROJECTED_GRID,
   GLOBAL_WATER_REFLECTION and USE_PCSS (USE_TESSELATION does not reach the pixel
   shader). 2b95affe374e16f9 is the one captured at water_quality 0. */
constexpr Variant kVariants[] = {
    { 0x55e2612113016e35ull, 0 },
    { 0x727f1b8ced36b560ull, kProjectedGrid },
    { 0x3a936f74ab4c9c37ull, kLowQuality | kProjectedGrid },
    { 0x83c67a186c2f44a5ull, kGlobalReflection },
    { 0x2000ffb110e59146ull, kLowQuality | kGlobalReflection },
    { 0x8d2b5fccf01b5a5eull, kProjectedGrid | kGlobalReflection },
    { 0x2b95affe374e16f9ull, kLowQuality | kProjectedGrid | kGlobalReflection },
    { 0x7a9108022abfb6deull, kPcss },
    { 0x0debe4b1eb1b3aa2ull, kPcss | kProjectedGrid },
    { 0x1af42c3b800a29ffull, kPcss | kLowQuality | kProjectedGrid },
    { 0x16edf116e0edc1d7ull, kPcss | kGlobalReflection },
    { 0x5757d689535a2e1dull, kPcss | kLowQuality | kGlobalReflection },
    { 0x2bcc44ce54494011ull, kPcss | kProjectedGrid | kGlobalReflection },
    { 0x2b799e7f1332f2b3ull, kPcss | kLowQuality | kProjectedGrid | kGlobalReflection },
};

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

bool read_file(const std::wstring& path, std::string* out)
{
    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"rb") != 0 || f == nullptr)
        return false;
    char buf[16384];
    size_t n;
    out->clear();
    while ((n = fread(buf, 1, sizeof(buf), f)) > 0)
        out->append(buf, n);
    fclose(f);
    return true;
}

/* <game>\Shaders\Sources, from the game exe in <game>\bin\Win64_Shipping_Client. */
std::wstring sources_dir()
{
    wchar_t path[MAX_PATH] = {};
    if (GetModuleFileNameW(nullptr, path, MAX_PATH) == 0)
        return std::wstring();
    for (int up = 0; up < 3; ++up)
    {
        wchar_t* slash = wcsrchr(path, L'\\');
        if (slash == nullptr)
            return std::wstring();
        *slash = L'\0';
    }
    return std::wstring(path) + L"\\Shaders\\Sources";
}

/* Every include in these sources is relative to Sources (one is ../ of it). */
class SourcesInclude : public ID3DInclude
{
public:
    explicit SourcesInclude(std::wstring dir) : m_dir(std::move(dir)) {}

    HRESULT __stdcall Open(D3D_INCLUDE_TYPE, LPCSTR name, LPCVOID, LPCVOID* data, UINT* bytes) override
    {
        wchar_t wide[MAX_PATH] = {};
        MultiByteToWideChar(CP_UTF8, 0, name, -1, wide, MAX_PATH);
        std::string text;
        if (!read_file(m_dir + L"\\" + wide, &text))
            return E_FAIL;
        char* copy = new char[text.size() + 1];
        memcpy(copy, text.data(), text.size());
        *data = copy;
        *bytes = static_cast<UINT>(text.size());
        return S_OK;
    }

    HRESULT __stdcall Close(LPCVOID data) override
    {
        delete[] static_cast<const char*>(data);
        return S_OK;
    }

private:
    std::wstring m_dir;
};

bool replace_once(std::string& s, const char* from, const std::string& to)
{
    const size_t at = s.find(from);
    if (at == std::string::npos || s.find(from, at + 1) != std::string::npos)
        return false;
    s.replace(at, strlen(from), to);
    return true;
}

/* The shipped source with specular AA spliced in, or empty when any anchor is
   missing or ambiguous - a changed shader is left to the engine. */
std::string patched_source(const std::string& src, float farM, float farNormal, float farAlpha2,
                           float ggxExtra)
{
    std::string s = src;
    const bool ok =
        replace_once(s, "\tview_dir.z = abs(-view_dir.z);",
                     "\tfloat bvr_far = smoothstep(BVR_NEAR_M, BVR_FAR_M, distance(In.WorldPos.xyz, g_camera_position.xyz));\n"
                     "\tsurface_normal = normalize(float3(surface_normal.xy * lerp(1.0, BVR_FAR_NORMAL, bvr_far), surface_normal.z));\n"
                     "\tfloat bvr_kernel = BVR_FAR_ALPHA2 * bvr_far;\n"
                     "\tview_dir.z = abs(-view_dir.z);") &&
        replace_once(s, "compute_specular_lighting(lerp(float2(1, 0.96), float2(0, 0.3), step(0.5, foam))",
                     "compute_specular_lighting(bvr_gloss(lerp(float2(1, 0.96), float2(0, 0.3), step(0.5, foam)), bvr_kernel)") &&
        replace_once(s, "sample_cube_texture_array(specular_dir, 0, (uint)probe_index)",
                     "sample_cube_texture_array(specular_dir, (ENVMAP_LEVEL - 1) * sqrt(sqrt(bvr_kernel)), (uint)probe_index)") &&
        replace_once(s, "specular_ambient_term = fresnel * specular_sample + fresnel * microfacet_distribution",
                     "specular_ambient_term = fresnel * specular_sample + BVR_GGX_EXTRA * fresnel * microfacet_distribution") &&
        replace_once(s, "texture9.SampleLevel(point_clamp_sampler, distorted_ss_pos.xy * g_rc_scale, 0)",
                     "texture9.SampleLevel(linear_clamp_sampler, distorted_ss_pos.xy * g_rc_scale, 0)");
    if (!ok)
        return std::string();

    /* water_debug_bands: each eye's sea split into six vertical bands, left to
       right - under the water, sky reflection, sun glint, reflection weight R,
       all of it before the haze, and the final image - so one screenshot names
       the term that carries the noise. */
    if (config_bool("water_debug_bands", false) &&
        !replace_once(s, "\tcolor = apply_atmospheric_scatter(view_dir, view_unorm.z",
                      "\t{\n"
                      "\t\tfloat bvr_band = ss_pos.x * 6;\n"
                      "\t\tif (bvr_band < 5)\n"
                      "\t\t{\n"
                      "\t\t\tif (bvr_band < 1) color = underwater_color;\n"
                      "\t\t\telse if (bvr_band < 2) color = specular_sample;\n"
                      "\t\t\telse if (bvr_band < 3) color = sun_lighting;\n"
                      "\t\t\telse if (bvr_band < 4) color = R * dot(specular_sample, 0.333) * 2;\n"
                      "\t\t\tOutput.RGBColor.rgb = output_color(color);\n"
                      "\t\t\treturn Output;\n"
                      "\t\t}\n"
                      "\t}\n"
                      "\tcolor = apply_atmospheric_scatter(view_dir, view_unorm.z"))
        return std::string();

    /* gloss -> GGX alpha^2 = (1 - gloss)^4 in compute_specular_lighting; widen it
       by the kernel and hand back the gloss that gives the wider lobe. */
    char head[640];
    _snprintf_s(head, _TRUNCATE,
                "#define BVR_NEAR_M 20.0\n"
                "#define BVR_FAR_M %.1f\n"
                "#define BVR_FAR_NORMAL %.3f\n"
                "#define BVR_FAR_ALPHA2 %.5f\n"
                "#define BVR_GGX_EXTRA %.3f\n"
                "float2 bvr_gloss(float2 s, float k) { float r = max(1e-4, 1 - s.y); "
                "float a2 = r * r * r * r; return float2(s.x, 1 - sqrt(sqrt(saturate(a2 + k)))); }\n",
                farM, farNormal, farAlpha2, ggxExtra);
    return head + s;
}

ID3DBlob* build(const Variant& v)
{
    const std::wstring dir = sources_dir();
    const std::wstring file = dir + L"\\water_surface.rs";
    std::string src;
    if (dir.empty() || !read_file(file, &src))
    {
        BVR_WARN("Water specular AA (TEST 545): could not read the game's water_surface.rs; "
                 "the engine's sea shader stays.");
        return nullptr;
    }

    const float farM = config_float("water_spec_aa_far_m", 250.0f);
    const float farNormal = config_float("water_spec_aa_far_normal", 0.4f);
    const float farAlpha2 = config_float("water_spec_aa_far_alpha2", 0.01f);
    const float ggxExtra = config_float("water_ggx_extra", 0.0f);
    const std::string patched = patched_source(src, farM > 21.0f ? farM : 21.0f, farNormal, farAlpha2,
                                               ggxExtra);
    if (patched.empty())
    {
        BVR_WARN("Water specular AA (TEST 545): water_surface.rs no longer has the lines it "
                 "patches; the engine's sea shader stays.");
        return nullptr;
    }

    D3D_SHADER_MACRO macros[10] = {};
    int m = 0;
    macros[m++] = { "PIXEL_SHADER", "1" };
    macros[m++] = { "VERTEX_DECLARATION", "0" };
    macros[m++] = { "RGL_TARGET_SYSTEM_WINDOWS", "1" };
    macros[m++] = { "RGL_BUILD_SHIPPING", "1" };
    if (v.flags & kLowQuality)       macros[m++] = { "LOW_QUALITY", "1" };
    if (v.flags & kProjectedGrid)    macros[m++] = { "PROJECTED_GRID", "1" };
    if (v.flags & kGlobalReflection) macros[m++] = { "GLOBAL_WATER_REFLECTION", "1" };
    if (v.flags & kPcss)             macros[m++] = { "USE_PCSS", "1" };

    SourcesInclude include(dir);
    ID3DBlob* code = nullptr;
    ID3DBlob* err = nullptr;
    const HRESULT hr = D3DCompile(patched.data(), patched.size(), "water_surface.rs", macros,
                                  &include, "main_ps", "ps_5_0",
                                  D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &err);
    if (FAILED(hr) || code == nullptr)
    {
        BVR_ERR("Water specular AA (TEST 545): compile failed (0x%08X), the engine's sea "
                "shader stays. %s", static_cast<unsigned>(hr),
                err ? static_cast<const char*>(err->GetBufferPointer()) : "");
        if (err) err->Release();
        if (code) code->Release();
        return nullptr;
    }
    if (err) err->Release();

    BVR_INFO("Water specular AA (TEST 547): sea shader %016llx (flags 0x%X) rebuilt from the "
             "game's water_surface.rs - from 20 m to %.0f m the wave normal fades to %.2f and "
             "the sun glint/reflection widen by alpha^2 %.4f; the unlit GGX highlight x%.2f; "
             "refraction bilinear.",
             static_cast<unsigned long long>(v.hash), v.flags, farM, farNormal, farAlpha2, ggxExtra);
    return code;
}

} // namespace

bool water_spec_aa_code(const void* code, size_t len, const void** outCode, size_t* outLen)
{
    static const bool on = config_bool("water_spec_aa", true);
    if (!on || code == nullptr || len < 25000 || len > 34000)
        return false;

    const uint64_t h = fnv1a(static_cast<const uint8_t*>(code), len);
    const Variant* v = nullptr;
    for (const Variant& x : kVariants)
        if (x.hash == h)
            v = &x;
    if (v == nullptr)
        return false;

    /* Built once per variant; a failure is remembered as null. Blobs live as long
       as the process, like the shaders made from them. */
    static std::mutex lock;
    static std::unordered_map<uint64_t, ID3DBlob*> built;
    std::lock_guard<std::mutex> guard(lock);
    auto it = built.find(h);
    if (it == built.end())
        it = built.emplace(h, build(*v)).first;
    if (it->second == nullptr)
        return false;

    *outCode = it->second->GetBufferPointer();
    *outLen = it->second->GetBufferSize();
    return true;
}

// --- TEST 548: the water's cost, measured by skipping it -----------------------

namespace {

const GUID kProbeTag = { 0x6d1a4c2e, 0x8b37, 0x4f05, { 0x9e, 0x21, 0x5a, 0x7c, 0x33, 0x0b, 0xd4, 0x62 } };

constexpr BYTE kOcean = 1, kSea = 2;   /* also the phase bits that skip them */

/* The War Sails ocean's compute chain as captured (20261012_192652): wave
   spectrum, FFT, displacement, normals/fold, mips, the two 2048x2048 passes,
   and the agent deform pair. */
constexpr uint64_t kOceanCs[] = {
    0xcc39515b38dfbe81ull, 0xcd881b23a54b2960ull, 0xc30c053d4c419e64ull, 0x94c987ea02868ea6ull,
    0x259fec511fee50adull, 0x4dbd7708e8e3fb3full, 0xd34b38a5fdfe9e0bull, 0x2f86dc32c82e69b6ull,
    0x831752706409eb11ull, 0x55c30e528a26f1feull, 0xa99be6e39e15ed6aull,
};
/* The sea's depth/normal prepass at water_quality 0; its colour pass is any of
   the 14 water_surface.rs variants above. */
constexpr uint64_t kSeaPrepassPs = 0xc9ef5f9326603897ull;

bool probe_on()
{
    static const bool on = config_bool("water_cost_probe", false);
    return on;
}

std::atomic<int> g_phase{ 0 };
int64_t g_phaseStart = 0;
double  g_sum[4] = {};
int     g_n[4] = {};

int64_t qpc_now()
{
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}

double qpc_s(int64_t ticks)
{
    static const double freq = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return double(f.QuadPart); }();
    return double(ticks) / freq;
}

} // namespace

void water_probe_tag(void* shader, const void* code, size_t len)
{
    if (!probe_on() || shader == nullptr || code == nullptr)
        return;
    const uint64_t h = fnv1a(static_cast<const uint8_t*>(code), len);
    BYTE group = 0;
    for (uint64_t x : kOceanCs)
        if (x == h)
            group = kOcean;
    if (h == kSeaPrepassPs)
        group = kSea;
    for (const Variant& v : kVariants)
        if (v.hash == h)
            group = kSea;
    if (group != 0)
        static_cast<ID3D11DeviceChild*>(shader)->SetPrivateData(kProbeTag, 1, &group);
}

bool water_probe_skip(ID3D11DeviceContext* c, bool compute)
{
    const int phase = g_phase.load(std::memory_order_relaxed);
    if (phase == 0 || c == nullptr)
        return false;

    ID3D11DeviceChild* sh = nullptr;
    if (compute)
    {
        ID3D11ComputeShader* cs = nullptr;
        c->CSGetShader(&cs, nullptr, nullptr);
        sh = cs;
    }
    else
    {
        ID3D11PixelShader* ps = nullptr;
        c->PSGetShader(&ps, nullptr, nullptr);
        sh = ps;
    }
    if (sh == nullptr)
        return false;
    BYTE group = 0;
    UINT size = 1;
    const bool tagged = SUCCEEDED(sh->GetPrivateData(kProbeTag, &size, &group));
    sh->Release();
    return tagged && (group & phase) != 0;
}

int water_probe_open()
{
    /* Half a second after a switch, so no frame straddles two phases. */
    if (!probe_on() || g_phaseStart == 0 || qpc_s(qpc_now() - g_phaseStart) < 0.5)
        return -1;
    return g_phase.load(std::memory_order_relaxed);
}

void water_probe_sample(int phase, double engineMs)
{
    if (phase < 0 || phase > 3)
        return;
    g_sum[phase] += engineMs;
    ++g_n[phase];
}

void water_probe_frame()
{
    if (!probe_on())
        return;
    const int64_t now = qpc_now();
    if (g_phaseStart == 0)
    {
        g_phaseStart = now;
        BVR_INFO("Water cost probe (TEST 548): on - every 3 s the ocean's compute, the sea's "
                 "draws, or both are skipped (the sea freezes or vanishes for those seconds). "
                 "Stand still looking at the sea.");
        return;
    }
    if (qpc_s(now - g_phaseStart) < 3.0)
        return;

    const int next = (g_phase.load(std::memory_order_relaxed) + 1) & 3;
    g_phase.store(next, std::memory_order_relaxed);
    g_phaseStart = now;
    if (next != 0 || g_n[0] == 0)
        return;

    auto mean = [](int p) { return g_n[p] ? g_sum[p] / g_n[p] : 0.0; };
    const double base = mean(0);
    BVR_INFO("Water cost probe (TEST 548): engine GPU frame %.2f ms with all the water (%d "
             "frames) | ocean compute skipped %.2f ms, saves %.2f | sea draws skipped %.2f ms, "
             "saves %.2f | both skipped %.2f ms, saves %.2f.",
             base, g_n[0], mean(1), base - mean(1), mean(2), base - mean(2), mean(3), base - mean(3));
    for (int p = 0; p < 4; ++p)
    {
        g_sum[p] = 0.0;
        g_n[p] = 0;
    }
}

} // namespace bvr
