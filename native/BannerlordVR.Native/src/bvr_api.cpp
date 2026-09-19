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

#ifdef BVR_ENABLE_OPENXR
#  include "bvr_log.h"
#  include "bvr_crash.h"
#  include "afw_warp.h"
#  include "d3d_hooks.h"
#  include "late_latch.h"
#  include "stereo_dup.h"
#  include "vp_patch.h"
#  include "vr_overlay.h"
#  include "xr_context.h"
#  include "xr_input.h"
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
const char kVersion[] = "0.2.0-openxr";
#else
const char kVersion[] = "0.2.0-stub";
#endif

bool g_initialized = false;

#ifdef BVR_ENABLE_OPENXR
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

int32_t BVR_CALL bvr_set_sim_generation(int32_t generation)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_sim_generation(generation);
#else
    (void)generation;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_set_full_afr(int32_t active)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_full_afr(active);
#else
    (void)active;
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

int32_t BVR_CALL bvr_set_afw(int32_t mode, float focalPx, float baseline,
                             float zNear, float zFar)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::xr_set_afw(mode, focalPx, baseline, zNear, zFar);
#else
    (void)mode; (void)focalPx; (void)baseline; (void)zNear; (void)zFar;
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_afw_ready(void)
{
#ifdef BVR_ENABLE_OPENXR
    return bvr::afw_ready() ? 1 : 0;
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
   scale built out of whatever was in the next register. Ten four-byte fields,
   no padding anywhere, so both sides can agree on one number. The managed test
   suite asserts the same 40 against Marshal.SizeOf. */
static_assert(sizeof(BvrOverlayState) == 104,
              "BvrOverlayState changed size; update BannerlordVR.Interop and its test.");

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
    settings.motionHands = state->motionHands;
    settings.uiShow = state->uiShow;
    settings.uiWidth = state->uiWidth;
    settings.uiDistance = state->uiDistance;
    settings.screenWidth = state->screenWidth;
    settings.screenDistance = state->screenDistance;
    settings.sharpen = state->sharpen;

    /* Push-only: managed reporting what the probe is doing, so the button can
       say so without the player reading a log. */
    settings.tableauProbeState = state->tableauProbeState;

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
    outState->motionHands = settings.motionHands;
    outState->uiShow = settings.uiShow;
    outState->uiWidth = settings.uiWidth;
    outState->uiDistance = settings.uiDistance;
    outState->screenWidth = settings.screenWidth;
    outState->screenDistance = settings.screenDistance;
    outState->sharpen = settings.sharpen;
    outState->calibCommand = settings.calibCommand;
    outState->tableauProbe = settings.tableauProbe;
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
    return bvr::input_read(outState) ? BVR_OK : BVR_SESSION_NOT_READY;
#else
    return BVR_SESSION_NOT_READY;
#endif
}

int32_t BVR_CALL bvr_trigger_haptic(int32_t /*hand*/, float /*amplitude*/, float /*durationSeconds*/)
{
    return BVR_SESSION_NOT_READY;
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
