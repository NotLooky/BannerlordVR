/* =============================================================================
 * bvr_api.cpp - the exported C ABI. Thin glue only; the work lives in
 * xr_context.cpp (OpenXR) and d3d_hooks.cpp (D3D11 interception).
 *
 * Two invariants hold for every function here:
 *   1. Never throws. The callers are Mono P/Invoke and the engine's render
 *      thread; an exception crossing this boundary terminates the process.
 *   2. Never blocks the game. Every failure path returns a status code and
 *      leaves Bannerlord running flat.
 *
 * Building without the submodules (BVR_ENABLE_OPENXR=OFF) still produces a
 * loadable DLL that reports BVR_NO_RUNTIME, so the managed pipeline stays
 * testable end to end without a headset.
 * ========================================================================== */

/* BVR_BUILDING_DLL comes from CMake (target_compile_definitions). */
#include "bvr_api.h"

#include <cstring>
#include <string>

#ifdef BVR_ENABLE_OPENXR
#  include "bvr_config.h"
#  include "bvr_log.h"
#  include "bvr_crash.h"
#  include "depth_reproject.h"
#  include "d3d_hooks.h"
#  include "late_latch.h"
#  include "ngx_hook.h"
#  include "stereo_dup.h"
#  include "vp_patch.h"
#  include "vr_overlay.h"
#  include "xr_context.h"
#  include "xr_input.h"
#  include "ui_supersample.h"
#endif

/* -----------------------------------------------------------------------------
 * Layout guards. NativeBridge.cs mirrors these structs, and Mono does not
 * validate layout across P/Invoke - a mismatch silently reads past the end of a
 * buffer instead of erroring. If one of these fires, the C# side changed without
 * this header changing (or vice versa). Fix both, never just one.
 * -------------------------------------------------------------------------- */
static_assert(sizeof(BvrQuat)       == 16,  "BvrQuat layout drifted");
static_assert(sizeof(BvrVec3)       == 12,  "BvrVec3 layout drifted");
static_assert(sizeof(BvrFov)        == 16,  "BvrFov layout drifted");
static_assert(sizeof(BvrEyeView)    == 44,  "BvrEyeView layout drifted");
static_assert(sizeof(BvrHandState)  == 92,  "BvrHandState layout drifted");
static_assert(sizeof(BvrInputState) == 184, "BvrInputState layout drifted");

namespace {

#ifdef BVR_ENABLE_OPENXR
const char kVersion[] = "0.2.7-openxr";
#else
const char kVersion[] = "0.2.7-stub";
#endif

bool g_initialized = false;

#ifdef BVR_ENABLE_OPENXR
/* Cheeky Foveated DLSS without its dxgi.dll proxy (cheeky_foveated = 1). The
   proxy fail-fasts in the launcher: AcGenral's DXGI compat shim calls
   SetAppCompatStringPointer on whatever module is named dxgi.dll, and the
   proxy's System32 LoadLibraryExW inside that callback returns
   STATUS_DLL_NOT_FOUND. All the proxy does on success is load the host and
   call CheekyHost_Start(1), so do that here. Host stays in
   <game bin>\CheekyFoveatedDLSS\; no dxgi.dll or version.dll in the game folder.

   The user installs only that folder (Cheeky is GPLv3 and not redistributed
   with the mod). Everything else is set here, every launch. */
const char kCheekyTested[] = "0.5.5";

/* "Cheeky 0.5.5 %s initializing" is in the runtime DLL; it carries no version
   resource. Empty when the file is unreadable or the string moved. */
std::string cheeky_version(const wchar_t* runtimePath)
{
    std::string version;
    FILE* f = nullptr;
    if (_wfopen_s(&f, runtimePath, L"rb") != 0 || f == nullptr)
        return version;
    std::string bytes;
    char buf[65536];
    size_t got;
    while ((got = fread(buf, 1, sizeof(buf), f)) > 0)
        bytes.append(buf, got);
    fclose(f);

    const std::string tag = "Cheeky ";
    for (size_t at = bytes.find(tag); at != std::string::npos; at = bytes.find(tag, at + 1))
    {
        size_t end = at + tag.size();
        while (end < bytes.size() && (isdigit(static_cast<unsigned char>(bytes[end])) || bytes[end] == '.'))
            ++end;
        if (end > at + tag.size() && bytes.compare(end, 4, " %s ") == 0)
            return bytes.substr(at + tag.size(), end - at - tag.size());
    }
    return version;
}

/* cheeky_foveated: below zero (default) starts Cheeky when its folder is in the
   game's bin - copying the folder in is the opt-in - 0 never, 1 always (and say
   so if the folder is missing). */
void start_cheeky_host()
{
    const float choice = bvr::config_float("cheeky_foveated", -1.0f);
    if (choice > -0.5f && choice < 0.5f)
        return;
    const bool forced = choice >= 0.5f;

    wchar_t dir[MAX_PATH];
    DWORD n = GetModuleFileNameW(nullptr, dir, MAX_PATH);
    wchar_t* slash = (n > 0 && n < MAX_PATH) ? wcsrchr(dir, L'\\') : nullptr;
    if (slash == nullptr)
    {
        BVR_WARN("Cheeky: could not find the game folder.");
        return;
    }
    wcscpy_s(slash + 1, MAX_PATH - (slash + 1 - dir), L"CheekyFoveatedDLSS\\");
    wchar_t path[MAX_PATH], runtime[MAX_PATH], ini[MAX_PATH];
    _snwprintf_s(path, _TRUNCATE, L"%sCheekyFoveatedDLSSHost.dll", dir);
    _snwprintf_s(runtime, _TRUNCATE, L"%sCheekyFoveatedDLSSRuntime.dll", dir);
    _snwprintf_s(ini, _TRUNCATE, L"%sCheekyFoveatedDLSS.ini", dir);

    if (GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES)
    {
        if (forced)
            BVR_WARN("Cheeky: cheeky_foveated = 1 but %ls is missing. Copy Cheeky's "
                     "CheekyFoveatedDLSS folder (only that folder) next to the game exe.", path);
        return;
    }

    /* Tested only with native stereo: AFR hands it one eye per frame through one
       feature. The mode can change in the VR menu later; Cheeky cannot be
       unloaded, so this is decided at launch. */
    if (!bvr::config_is("stereo_mode", "nativestereo"))
    {
        BVR_WARN("Cheeky: found but not started - it is supported with stereo_mode = "
                 "nativestereo only.");
        return;
    }

    const std::string version = cheeky_version(runtime);
    if (version != kCheekyTested && !bvr::config_bool("cheeky_allow_untested", false))
    {
        BVR_WARN("Cheeky: version '%s' found, %s is the tested one - not started "
                 "(cheeky_allow_untested = 1 starts it anyway).",
                 version.empty() ? "unknown" : version.c_str(), kCheekyTested);
        return;
    }

    /* Cheeky rewrites its ini and once saved the periphery DLAA and the DX11
       transport off by itself: raw jittered periphery, flicker. The marker eye
       calibration drifts with late latch (window jumps, history resets, 3D
       breaks on every feature re-create); call order is always eye 0 then 1.
       A partial ini is accepted - Cheeky fills in the rest and keeps these. */
    static const wchar_t* const kForced[][2] = {
        { L"PeripheralDlaa",      L"1" },
        { L"D3D11D3D12Transport", L"1" },
        { L"AutoStereoAlignment", L"0" },
        { L"ManualStereoMapping", L"1" },
    };
    bool written = true;
    for (const auto& kv : kForced)
        written &= WritePrivateProfileStringW(L"CheekyFoveatedDLSS", kv[0], kv[1], ini) != FALSE;
    if (!written)
        BVR_WARN("Cheeky: could not write %ls (Win32 %lu); its own settings are used.", ini, GetLastError());

    HMODULE host = LoadLibraryExW(path, nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (host == nullptr)
    {
        BVR_WARN("Cheeky: %ls did not load (Win32 %lu).", path, GetLastError());
        return;
    }

    typedef bool(__cdecl* PFN_CheekyStart)(unsigned mode);
    auto start = reinterpret_cast<PFN_CheekyStart>(GetProcAddress(host, "CheekyHost_Start"));
    if (start == nullptr)
    {
        BVR_WARN("Cheeky: host has no CheekyHost_Start export.");
        return;
    }
    const bool started = start(1);
    if (started)
        ngx_set_cheeky_running();
    BVR_INFO("Cheeky %s: CheekyHost_Start(1) = %d (%s); forced PeripheralDlaa=1 D3D11D3D12Transport=1 "
             "AutoStereoAlignment=0 ManualStereoMapping=1%s. Its own logs are in CheekyFoveatedDLSS\\. "
             "cheeky_foveated = 0 keeps it off.",
             version.empty() ? "(version unknown)" : version.c_str(), started ? 1 : 0,
             forced ? "cheeky_foveated = 1" : "its folder is in bin", written ? "" : " (NOT written)");
}

/* Render thread. Installed as the Present hook's per-frame callback. */
void present_tick(IDXGISwapChain* swapChain,
                  ID3D11Device* device,
                  ID3D11DeviceContext* context)
{
    /* The constant-buffer hooks need the engine's immediate context, which does
       not exist until the first Present has been seen - the same reason the
       CreateTexture2D hook is installed late. Idempotent, so this is a load and
       a compare on every frame after the first. */
    if (bvr::vp_active())
    {
        bvr::vp_install_hooks(context);
        bvr::vp_frame_boundary();
    }

    /* Independent of vp_patch: the late latch hooks the engine's own matrix
       builder, not the constant buffers. See late_latch.h. */
    if (bvr::late_latch_active())
    {
        bvr::late_latch_install();
        bvr::late_latch_frame_boundary();
    }

    bvr::xr_tick(swapChain, device, context);
}
#endif

} // namespace

extern "C" {

int32_t BVR_CALL bvr_get_version(uint8_t* buffer, int32_t bufferLen)
{
    if (buffer == nullptr || bufferLen <= 0)
        return BVR_NOT_INITIALIZED;

    const int32_t len = static_cast<int32_t>(sizeof(kVersion) - 1);
    const int32_t n = (len < bufferLen) ? len : bufferLen;
    std::memcpy(buffer, kVersion, static_cast<size_t>(n));
    return n;
}

int32_t BVR_CALL bvr_init(void)
{
#ifdef BVR_ENABLE_OPENXR
    if (g_initialized)
        return BVR_OK;

    bvr::log_open();
    BVR_INFO("bvr_init: BannerlordVR.Native %s", kVersion);

    /* Before anything else can fault. */
    bvr::install_crash_logger();

    /* Instance first. If there is no runtime or no headset there is no reason to
       touch the game's render path at all - an unnecessary Present hook is pure
       risk in flat mode. */
    const int32_t rc = bvr::xr_create_instance();
    if (rc != BVR_OK)
    {
        BVR_WARN("No usable OpenXR system (status %d); leaving the render path untouched.", rc);
        return rc;
    }

    start_cheeky_host();

    if (!bvr::install_present_hook(&present_tick))
    {
        BVR_ERR("Present hook installation failed; cannot drive the frame loop.");
        bvr::xr_shutdown();
        return BVR_XR_ERROR;
    }

    g_initialized = true;
    return BVR_OK;
#else
    g_initialized = true;
    return BVR_NO_RUNTIME;
#endif
}

int32_t BVR_CALL bvr_create_session(void)
{
    if (!g_initialized)
        return BVR_NOT_INITIALIZED;

#ifdef BVR_ENABLE_OPENXR
    /* The session is actually created on the render thread, inside the Present
       hook, because that is the first moment the engine's D3D11 device is known.
       This call only records the intent, so nothing ever touches OpenXR from two
       threads. Expect BVR_SESSION_NOT_READY here and poll bvr_poll_events. */
    return bvr::xr_request_session();
#else
    return BVR_NO_RUNTIME;
#endif
}

int32_t BVR_CALL bvr_get_recommended_size(uint32_t* width, uint32_t* height)
{
    if (width == nullptr || height == nullptr)
        return BVR_NOT_INITIALIZED;

    *width = 0;
    *height = 0;

#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_recommended_size(width, height) ? BVR_OK : BVR_NOT_INITIALIZED;
#else
    return BVR_NO_RUNTIME;
#endif
}

int32_t BVR_CALL bvr_poll_events(int32_t* outSessionState)
{
    if (outSessionState == nullptr)
        return BVR_NOT_INITIALIZED;

#ifdef BVR_ENABLE_OPENXR
    /* The event queue itself is pumped on the render thread; this only reports
       the latest state, so the managed side never races the runtime. */
    *outSessionState = bvr::xr_session_state();
    return bvr::xr_instance_ready() ? BVR_OK : BVR_NOT_INITIALIZED;
#else
    *outSessionState = 0;
    return BVR_NO_RUNTIME;
#endif
}
int32_t BVR_CALL bvr_prepare_frame(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_prepare_render_frame() ? BVR_OK : BVR_SESSION_NOT_READY;
#else
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_get_predicted_views(BvrEyeView* views, int64_t* outDisplayTime)
{
    if (views == nullptr || outDisplayTime == nullptr)
        return BVR_NOT_INITIALIZED;

    std::memset(views, 0, sizeof(BvrEyeView) * 2);
    *outDisplayTime = 0;

#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_get_predicted_views(views, outDisplayTime) ? BVR_OK : BVR_SESSION_NOT_READY;
#else
    return BVR_SESSION_NOT_READY;
#endif
}

/* --- Phase 4 ------------------------------------------------------------- */

int32_t BVR_CALL bvr_begin_texture_capture(int32_t eye, uint32_t width, uint32_t height)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::arm_texture_capture(eye, width, height) ? BVR_OK : BVR_NOT_INITIALIZED;
#else
    (void)eye; (void)width; (void)height;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_end_texture_capture(int32_t eye)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::finish_texture_capture(eye);
#else
    (void)eye;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_begin_ui_capture(uint32_t width, uint32_t height)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::arm_ui_capture(width, height) ? BVR_OK : BVR_NOT_INITIALIZED;
#else
    (void)width; (void)height;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_end_ui_capture(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::finish_ui_capture();
#else
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_ui_source(int32_t fromTarget)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_ui_source(fromTarget);
#else
    (void)fromTarget;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_begin_hand_capture(uint32_t width, uint32_t height)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::arm_hand_capture(width, height) ? BVR_OK : BVR_NOT_INITIALIZED;
#else
    (void)width; (void)height;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_end_hand_capture(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::finish_hand_capture();
#else
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_hand_source(int32_t live)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_hand_source(live);
#else
    (void)live;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_get_hand_source_state(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_hand_source_state();
#else
    return 0;
#endif
}

int32_t BVR_CALL bvr_set_used_views(const BvrEyeView* views, int64_t displayTime)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_used_views(views, displayTime);
#else
    (void)views; (void)displayTime;
    return BVR_SESSION_NOT_READY;
#endif
}

void BVR_CALL bvr_breadcrumb(int32_t id)
{
#ifdef BVR_ENABLE_OPENXR
    bvr::set_breadcrumb(id);
#else
    (void)id;
#endif
}

int32_t BVR_CALL bvr_set_afr_eye(int32_t eye)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_afr_eye(eye);
#else
    (void)eye;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_afr_eye_consumed(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_afr_eye_consumed();
#else
    return 0;
#endif
}

int32_t BVR_CALL bvr_next_afr_eye(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_next_afr_eye();
#else
    return 0;
#endif
}

int32_t BVR_CALL bvr_pin_afr_eye(int32_t eye)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_pin_afr_eye(eye);
#else
    (void)eye;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_body_yaw(float yawRadians)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_body_yaw(yawRadians);
#else
    (void)yawRadians;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_body_target(float yawRadians, float x, float y, float z)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_body_target(yawRadians, x, y, z);
#else
    (void)yawRadians; (void)x; (void)y; (void)z;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_body_position(float x, float y, float z)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_body_position(x, y, z);
#else
    (void)x; (void)y; (void)z;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_repin_flat_screen(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_repin_flat_screen();
#else
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_repin_flat_screen_if_turned(float degrees)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_repin_flat_screen_if_turned(degrees);
#else
    (void)degrees;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_screen_geometry(float width, float distance)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_screen_geometry(width, distance);
#else
    (void)width; (void)distance;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_ui_geometry(float width, float distance)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_ui_geometry(width, distance);
#else
    (void)width; (void)distance;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_in_mission(int32_t active)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_in_mission(active);
#else
    (void)active;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_ui_projection(int32_t eye, float px, float py, float pz,
                                       float qx, float qy, float qz, float qw)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_ui_projection(eye, px, py, pz, qx, qy, qz, qw);
#else
    (void)eye; (void)px; (void)py; (void)pz; (void)qx; (void)qy; (void)qz; (void)qw;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_wrist_regions(const BvrWristRegions* regions)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_wrist_regions(regions);
#else
    (void)regions;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_ui_supersample(float factor)
{
#ifdef BVR_ENABLE_OPENXR
    bvr::ui_ss_set_factor(factor);
    return BVR_OK;
#else
    (void)factor;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_get_ui_supersample(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::ui_ss_state();
#else
    return 0;
#endif
}

int32_t BVR_CALL bvr_set_ui_split(const BvrUiSplit* split)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_ui_split(split);
#else
    (void)split;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_wrist_pose(int32_t hand, int32_t valid, float px, float py, float pz,
                                    float qx, float qy, float qz, float qw)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_wrist_pose(hand, valid, px, py, pz, qx, qy, qz, qw);
#else
    (void)hand; (void)valid; (void)px; (void)py; (void)pz; (void)qx; (void)qy; (void)qz; (void)qw;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_laser(int32_t enabled, int32_t hand, int32_t beamToUi, int32_t click,
                                float groundDistance)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_laser(enabled, hand, beamToUi, click, groundDistance);
#else
    (void)enabled; (void)hand; (void)beamToUi; (void)click; (void)groundDistance;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_get_laser(BvrLaser* out)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_get_laser(out);
#else
    (void)out;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_get_laser_ray(int32_t hand, BvrVec3* origin, BvrVec3* dir)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_get_laser_ray(hand, origin, dir);
#else
    (void)hand; (void)origin; (void)dir;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_ui_pin(int32_t pinned)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_ui_pin(pinned);
#else
    (void)pinned;
    return BVR_SESSION_NOT_READY;
#endif
}

float BVR_CALL bvr_get_periods_per_frame(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_get_periods_per_frame();
#else
    return 1.0f;
#endif
}

int32_t BVR_CALL bvr_set_ui_eye_lag(int32_t lag)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_ui_eye_lag(lag);
#else
    (void)lag;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_ui_overlay(int32_t active)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_ui_overlay(active);
#else
    (void)active;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_mission_active(int32_t active)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_mission_active(active);
#else
    (void)active;
    return BVR_SESSION_NOT_READY;
#endif
}

/* --- the live resolution rescale ----------------------------------------- */

int32_t BVR_CALL bvr_request_rescale(float scale)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_request_rescale(scale);
#else
    (void)scale;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_rescale_swap(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_rescale_swap();
#else
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_rescale_abort(float previousScale)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_rescale_abort(previousScale);
#else
    (void)previousScale;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_rescale_stage(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_rescale_stage();
#else
    return 0;
#endif
}

int32_t BVR_CALL bvr_rescale_finish(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_rescale_finish();
#else
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_sim_generation(int32_t generation)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_sim_generation(generation);
#else
    (void)generation;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_draw_stereo(int32_t active)
{
#ifdef BVR_ENABLE_OPENXR
    bvr::vp_set_draw_stereo(active != 0);
    return BVR_OK;
#else
    (void)active;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_stereo_world_scale(float unitsPerMetre)
{
#ifdef BVR_ENABLE_OPENXR
    bvr::stereo_set_world_scale(unitsPerMetre);
    return BVR_OK;
#else
    (void)unitsPerMetre;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_native_stereo(int32_t active)
{
#ifdef BVR_ENABLE_OPENXR
    bvr::stereo_set_active(active != 0);
    return BVR_OK;
#else
    (void)active;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_depth_reproject(int32_t mode, float focalPx, float baseline,
                             float zNear, float zFar)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_depth_reproject(mode, focalPx, baseline, zNear, zFar);
#else
    (void)mode; (void)focalPx; (void)baseline; (void)zNear; (void)zFar;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_depth_reproject_ready(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::dr_ready() ? 1 : 0;
#else
    return 0;
#endif
}

int32_t BVR_CALL bvr_overlay_toggle(void)
{
#ifdef BVR_ENABLE_OPENXR
    bvr::overlay_toggle();
    return BVR_OK;
#else
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_overlay_visible(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::overlay_visible() ? 1 : 0;
#else
    return 0;
#endif
}

/* The overlay struct crosses a P/Invoke boundary, where a layout mismatch does
   not fail to compile - it reads the stack wrong and hands the game a world
   scale built out of whatever was in the next register. All forty-seven fields
   are four bytes wide and there is no padding anywhere, so both sides can agree
   on one number. The managed test suite asserts the same 176 against
   Marshal.SizeOf. 188 since TEST 428 (vrsEye), 192 since TEST 442 (headUpMounted),
   200 since TEST 450 (armScale, bodyWeight), 208 since TEST 462 (hudHitMarker,
   hudPrompts), 212 since TEST 485 (handFingers), 216 since TEST 527 (mapHandUiWidth),
   220 for dpadThumbrest, 228 since TEST 620 (uiWorldScaleBattle/Map), 232 since
   TEST 645 (ownShieldBash), 236 since TEST 648 (uiWorldScaleDeploy), 244 since
   TEST 711 (uiRecenterNewScreen, uiRecenterAngle), 248 for fovTrueVertical, 544 since
   TEST 750 (the Calibration tool: four ints, six floats, two 128-byte texts), 744
   since TEST 751 (toolProfile, toolWielded, four 48-byte slot names), 752 since
   TEST 770 (fovCropH, fovCropV), 772 since TEST 785 (fovCropDetailed + four edges),
   776 since TEST 790 (sharpenMode), 780 since TEST 797 (dlssPreset), 784 with gripToggle. */
static_assert(sizeof(BvrOverlayState) == 784,
              "BvrOverlayState changed size; update BannerlordVR.Interop and its test.");

#ifdef BVR_ENABLE_OPENXR
/* TEST 785: simple mode crops each axis evenly; detailed mode per edge. */
static void apply_fov_crop(int32_t detailed, float h, float v, float l, float r, float u, float d)
{
    if (detailed != 0)
        bvr::xr_set_fov_crop(l, r, u, d, true);    /* TEST 785c: Left/Right = outer edges */
    else
        bvr::xr_set_fov_crop(h, h, v, v, false);
}
#endif

int32_t BVR_CALL bvr_overlay_show(int32_t visible)
{
#ifdef BVR_ENABLE_OPENXR
    bvr::overlay_show(visible != 0);
    return BVR_OK;
#else
    (void)visible;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_overlay_scroll(float notches)
{
#ifdef BVR_ENABLE_OPENXR
    bvr::overlay_scroll(notches);
    return BVR_OK;
#else
    (void)notches;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_overlay_push(const BvrOverlayState* state)
{
#ifdef BVR_ENABLE_OPENXR
    if (state == nullptr)
        return BVR_NOT_INITIALIZED;

    bvr::OverlaySettings settings;
    settings.worldScale = state->worldScale;
    settings.renderScale = state->renderScale;
    settings.stereoMode = state->stereoMode;
    settings.stereoPending = state->stereoPending;
    settings.calibState = state->calibState;
    settings.calibHand = state->calibHand;
    settings.calibProgress = state->calibProgress;
    settings.calibSpread = state->calibSpread;
    settings.calibHave = state->calibHave;
    settings.hideBody = state->hideBody;
    settings.handsOnly = state->handsOnly;
    settings.motionHands = state->motionHands;
    settings.uiShow = state->uiShow;
    settings.uiWidth = state->uiWidth;
    settings.uiDistance = state->uiDistance;
    settings.screenWidth = state->screenWidth;
    settings.screenDistance = state->screenDistance;
    settings.sharpen = state->sharpen;
    settings.sharpenMode = state->sharpenMode;
    settings.dlssPreset = state->dlssPreset;
    settings.gripToggle = state->gripToggle;
    settings.stickMouseSpeed = state->stickMouseSpeed;

    /* Two-way toggle like hideBody above: managed tells the panel what physical
       combat is set to, and the panel's checkbox tells managed to change it. */
    settings.physicalCombat = state->physicalCombat;

    /* The full-body IK, the same way: managed says whether it is on, the
       panel's checkbox can turn it off and get the animated body back. */
    settings.bodyIk = state->bodyIk;

    /* Motion-control combat, the same way again: managed says whether the
       gesture melee is on, the panel's checkbox can put the game's own combat
       back. */
    settings.motionMelee = state->motionMelee;

    /* Friendly fire (TEST 261), the same way: managed says whether swings can
       land on friends and neutrals, the panel's checkbox changes it live. */
    settings.friendlyFire = state->friendlyFire;

    /* The grip's controller, the same way: managed says which one is loaded,
       the panel's dropdown switches it. */
    settings.gripController = state->gripController;

    /* Body holsters (TEST 271), the same two-way way. */
    settings.holster = state->holster;
    settings.mapVr = state->mapVr;

    /* The VR tab (TEST 293). recenter deliberately not copied, like
       resolutionApply below. */
    settings.headUp = state->headUp;
    settings.headForward = state->headForward;
    settings.bodyTurnDeg = state->bodyTurnDeg;
    /* TEST 356; heightMeasure deliberately not copied, like recenter. */
    settings.playerHeight = state->playerHeight;
    settings.bodyScale = state->bodyScale;
    settings.wristHudWidth = state->wristHudWidth;
    settings.vrs = state->vrs;
    settings.vrsEye = state->vrsEye;
    settings.headUpMounted = state->headUpMounted;
    settings.armScale = state->armScale;
    settings.bodyWeight = state->bodyWeight;
    settings.hudHitMarker = state->hudHitMarker;
    settings.hudPrompts = state->hudPrompts;
    settings.handFingers = state->handFingers;
    settings.mapHandUiWidth = state->mapHandUiWidth;
    settings.dpadThumbrest = state->dpadThumbrest;
    settings.uiWorldScaleBattle = state->uiWorldScaleBattle;
    settings.uiWorldScaleMap = state->uiWorldScaleMap;
    settings.ownShieldBash = state->ownShieldBash;
    settings.uiWorldScaleDeploy = state->uiWorldScaleDeploy;
    settings.uiRecenterNewScreen = state->uiRecenterNewScreen;
    settings.uiRecenterAngle = state->uiRecenterAngle;
    settings.fovTrueVertical = state->fovTrueVertical;
    settings.fovCropH = state->fovCropH;
    settings.fovCropV = state->fovCropV;
    settings.fovCropDetailed = state->fovCropDetailed;
    settings.fovCropLeft = state->fovCropLeft;
    settings.fovCropRight = state->fovCropRight;
    settings.fovCropUp = state->fovCropUp;
    settings.fovCropDown = state->fovCropDown;

    /* TEST 750: the Calibration tool. toolCommand deliberately not copied, like
       calibCommand below. */
    settings.toolMode = state->toolMode;
    settings.toolHand = state->toolHand;
    settings.toolLive = state->toolLive;
    for (int i = 0; i < 6; ++i)
        settings.toolValue[i] = state->toolValue[i];
    memcpy(settings.toolLabel, state->toolLabel, sizeof settings.toolLabel);
    memcpy(settings.toolStatus, state->toolStatus, sizeof settings.toolStatus);
    settings.toolLabel[sizeof settings.toolLabel - 1] = 0;
    settings.toolStatus[sizeof settings.toolStatus - 1] = 0;
    settings.toolProfile = state->toolProfile;
    settings.toolWielded = state->toolWielded;
    memcpy(settings.toolSlot, state->toolSlot, sizeof settings.toolSlot);
    for (int i = 0; i < 4; ++i)
        settings.toolSlot[i][sizeof settings.toolSlot[i] - 1] = 0;
#ifdef BVR_ENABLE_OPENXR
    bvr::xr_set_fov_true_vertical(state->fovTrueVertical != 0);
    apply_fov_crop(state->fovCropDetailed, state->fovCropH, state->fovCropV, state->fovCropLeft,
                   state->fovCropRight, state->fovCropUp, state->fovCropDown);
#endif

    /* Push-only: managed reporting what the probe is doing, so the button can
       say so without the player reading a log. */
    settings.tableauProbeState = state->tableauProbeState;

    /* Push-only too: managed telling the panel how the rebuild is going, so
       the button can say "rebuilding" rather than looking inert while the
       headset is black. */
    settings.resolutionState = state->resolutionState;

    /* resolutionApply deliberately not copied, exactly like tableauProbe
       below: it is the PANEL's to raise and managed's to consume. */

    /* tableauProbe deliberately not copied, for the same reason as
       calibCommand below: it is the PANEL's to raise and managed's to consume,
       and echoing a stale zero back would swallow a click. */

    /* calibCommand deliberately not copied. It belongs to the panel, and
       overlay_set_settings preserves whatever is pending there. */

    bvr::overlay_set_settings(settings);
    return BVR_OK;
#else
    (void)state;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_overlay_poll(BvrOverlayState* outState)
{
#ifdef BVR_ENABLE_OPENXR
    if (outState == nullptr)
        return BVR_NOT_INITIALIZED;

    bvr::OverlaySettings settings;
    if (!bvr::overlay_take_settings(&settings))
        return BVR_SESSION_NOT_READY;   /* nothing changed; not an error */

    outState->worldScale = settings.worldScale;
    outState->renderScale = settings.renderScale;
    outState->stereoMode = settings.stereoMode;
    outState->stereoPending = settings.stereoPending;
    outState->calibState = settings.calibState;
    outState->calibHand = settings.calibHand;
    outState->calibProgress = settings.calibProgress;
    outState->calibSpread = settings.calibSpread;
    outState->calibHave = settings.calibHave;
    outState->hideBody = settings.hideBody;
    outState->handsOnly = settings.handsOnly;
    outState->motionHands = settings.motionHands;
    outState->uiShow = settings.uiShow;
    outState->uiWidth = settings.uiWidth;
    outState->uiDistance = settings.uiDistance;
    outState->screenWidth = settings.screenWidth;
    outState->screenDistance = settings.screenDistance;
    outState->sharpen = settings.sharpen;
    outState->sharpenMode = settings.sharpenMode;
    outState->dlssPreset = settings.dlssPreset;
    outState->gripToggle = settings.gripToggle;
    outState->stickMouseSpeed = settings.stickMouseSpeed;
    outState->physicalCombat = settings.physicalCombat;
    outState->bodyIk = settings.bodyIk;
    outState->motionMelee = settings.motionMelee;
    outState->friendlyFire = settings.friendlyFire;
    outState->gripController = settings.gripController;
    outState->holster = settings.holster;
    outState->mapVr = settings.mapVr;
    outState->headUp = settings.headUp;
    outState->headForward = settings.headForward;
    outState->bodyTurnDeg = settings.bodyTurnDeg;
    outState->recenter = settings.recenter;
    outState->playerHeight = settings.playerHeight;
    outState->heightMeasure = settings.heightMeasure;
    outState->bodyScale = settings.bodyScale;
    outState->wristHudWidth = settings.wristHudWidth;
    outState->vrs = settings.vrs;
    outState->vrsEye = settings.vrsEye;
    outState->headUpMounted = settings.headUpMounted;
    outState->armScale = settings.armScale;
    outState->bodyWeight = settings.bodyWeight;
    outState->hudHitMarker = settings.hudHitMarker;
    outState->hudPrompts = settings.hudPrompts;
    outState->handFingers = settings.handFingers;
    outState->mapHandUiWidth = settings.mapHandUiWidth;
    outState->dpadThumbrest = settings.dpadThumbrest;
    outState->uiWorldScaleBattle = settings.uiWorldScaleBattle;
    outState->uiWorldScaleMap = settings.uiWorldScaleMap;
    outState->ownShieldBash = settings.ownShieldBash;
    outState->uiWorldScaleDeploy = settings.uiWorldScaleDeploy;
    outState->uiRecenterNewScreen = settings.uiRecenterNewScreen;
    outState->uiRecenterAngle = settings.uiRecenterAngle;
    outState->fovTrueVertical = settings.fovTrueVertical;
    /* Native-side consumers, applied here: managed pushes only at start and on
       a few events, so a panel edit never comes back through bvr_overlay_push. */
    bvr::xr_set_fov_true_vertical(settings.fovTrueVertical != 0);
    outState->fovCropH = settings.fovCropH;
    outState->fovCropV = settings.fovCropV;
    outState->fovCropDetailed = settings.fovCropDetailed;
    outState->fovCropLeft = settings.fovCropLeft;
    outState->fovCropRight = settings.fovCropRight;
    outState->fovCropUp = settings.fovCropUp;
    outState->fovCropDown = settings.fovCropDown;
    apply_fov_crop(settings.fovCropDetailed, settings.fovCropH, settings.fovCropV, settings.fovCropLeft,
                   settings.fovCropRight, settings.fovCropUp, settings.fovCropDown);
    outState->toolMode = settings.toolMode;
    outState->toolHand = settings.toolHand;
    outState->toolLive = settings.toolLive;
    outState->toolCommand = settings.toolCommand;
    for (int i = 0; i < 6; ++i)
        outState->toolValue[i] = settings.toolValue[i];
    memcpy(outState->toolLabel, settings.toolLabel, sizeof outState->toolLabel);
    memcpy(outState->toolStatus, settings.toolStatus, sizeof outState->toolStatus);
    outState->toolProfile = settings.toolProfile;
    outState->toolWielded = settings.toolWielded;
    memcpy(outState->toolSlot, settings.toolSlot, sizeof outState->toolSlot);
    outState->calibCommand = settings.calibCommand;
    outState->tableauProbe = settings.tableauProbe;
    outState->resolutionApply = settings.resolutionApply;
    return BVR_OK;
#else
    (void)outState;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_vp_frame(const float* data, int32_t floatCount,
                                  int32_t eye, int32_t valid)
{
#ifdef BVR_ENABLE_OPENXR
    /* vp_active, not vp_enabled: the measurement path needs the publication too -
       the scan uses the engine camera it carries to recognise the main view. */
    if (!bvr::vp_active())
        return BVR_SESSION_NOT_READY;

    if (data == nullptr || floatCount != BVR_VP_FRAME_FLOATS || eye < 0 || eye > 1)
        return BVR_NOT_INITIALIZED;

    bvr::VpFrame frame = {};
    std::memcpy(frame.eyeCamera, data + 0,  16 * sizeof(float));
    std::memcpy(frame.refPos,    data + 16,  3 * sizeof(float));
    std::memcpy(frame.refDir,    data + 19,  3 * sizeof(float));
    frame.tanLeft  = data[22];
    frame.tanRight = data[23];
    frame.tanUp    = data[24];
    frame.tanDown  = data[25];
    frame.eye      = eye;
    frame.valid    = valid;

    bvr::vp_publish(frame);
    return BVR_OK;
#else
    (void)data; (void)floatCount; (void)eye; (void)valid;
    return BVR_SESSION_NOT_READY;
#endif
}

/* The eye the engine is NOT drawing. Same payload, same order, published into a
   slot of its own so the second-eye constant buffer can be built from the same
   upload as the first rather than from a frame-old pose.
 *
 * A separate entry point rather than an extra argument on the one above,
 * because that one is called from the AFR path on every frame in every mode and
 * this is only wanted while the second draw is running. Nothing changes for
 * callers that never make this call. */
int32_t BVR_CALL bvr_set_vp_frame_other(const float* data, int32_t floatCount,
                                        int32_t eye, int32_t valid)
{
#ifdef BVR_ENABLE_OPENXR
    if (!bvr::vp_active())
        return BVR_SESSION_NOT_READY;

    if (data == nullptr || floatCount != BVR_VP_FRAME_FLOATS || eye < 0 || eye > 1)
        return BVR_NOT_INITIALIZED;

    bvr::VpFrame frame = {};
    std::memcpy(frame.eyeCamera, data + 0,  16 * sizeof(float));
    std::memcpy(frame.refPos,    data + 16,  3 * sizeof(float));
    std::memcpy(frame.refDir,    data + 19,  3 * sizeof(float));
    frame.tanLeft  = data[22];
    frame.tanRight = data[23];
    frame.tanUp    = data[24];
    frame.tanDown  = data[25];
    frame.eye      = eye;
    frame.valid    = valid;

    bvr::vp_publish_other(frame);
    return BVR_OK;
#else
    (void)data; (void)floatCount; (void)eye; (void)valid;
    return BVR_SESSION_NOT_READY;
#endif
}


/* --- Phase 6 ------------------------------------------------------------- */

int32_t BVR_CALL bvr_get_input_state(BvrInputState* outState)
{
    if (outState == nullptr)
        return BVR_NOT_INITIALIZED;

    std::memset(outState, 0, sizeof(BvrInputState));

#ifdef BVR_ENABLE_OPENXR
    return bvr::input_read_game(outState) ? BVR_OK : BVR_SESSION_NOT_READY;   /* TEST 452 */
#else
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_latch_input(float leadFrames)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::input_latch(leadFrames) ? BVR_OK : BVR_SESSION_NOT_READY;
#else
    (void)leadFrames;
    return BVR_SESSION_NOT_READY;
#endif
}

uint32_t BVR_CALL bvr_get_finger_touch(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::input_finger_touch();
#else
    return 0;
#endif
}

int32_t BVR_CALL bvr_trigger_haptic(int32_t hand, float amplitude, float durationSeconds)
{
#ifdef BVR_ENABLE_OPENXR
    /* A pulse is a request, not a guarantee: no session, no binding or a runtime
       that ignores haptics all return SESSION_NOT_READY and the caller carries
       on. The managed side reports the first failure once. */
    return bvr::input_haptic(hand, amplitude, durationSeconds)
        ? BVR_OK
        : BVR_SESSION_NOT_READY;
#else
    (void)hand;
    (void)amplitude;
    (void)durationSeconds;
    return BVR_SESSION_NOT_READY;
#endif
}

/* --- teardown ------------------------------------------------------------ */

void BVR_CALL bvr_shutdown(void)
{
#ifdef BVR_ENABLE_OPENXR
    if (g_initialized)
    {
        /* Hooks go first: once they are gone the render thread can no longer
           re-enter OpenXR, so tearing the session down afterwards is safe. */
        bvr::remove_hooks();
        bvr::xr_shutdown();
    }

    /* Unconditional: bvr_init opens the log before it can fail, so an early
       return (no runtime, no headset) still leaves a handle to release. */
    bvr::log_close();
#endif
    g_initialized = false;
}

} // extern "C"
