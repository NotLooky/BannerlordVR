/* =============================================================================
 * OpenXR session lifetime and frame loop.
 *
 * Threading contract, which everything here depends on:
 *   - xr_create_instance() runs on the managed thread (SubModule load).
 *   - EVERYTHING else runs on the render thread, inside the Present hook.
 *     Session creation is deferred there too, because the engine's D3D11 device
 *     only becomes known on the first Present. That also means no OpenXR call is
 *     ever made from two threads, so no locking is needed around the runtime.
 *   - Poses cross back to the managed thread through a seqlock-free double
 *     buffer (single writer, single reader, atomic index swap).
 * ========================================================================== */
#ifndef BVR_XR_CONTEXT_H
#define BVR_XR_CONTEXT_H

#include "bvr_api.h"

#include <d3d11.h>
#include <dxgi.h>
#include <stdint.h>

namespace bvr {

/* Managed thread. Creates XrInstance + queries the system.
   Returns BVR_OK, BVR_NO_RUNTIME (nothing registered / no D3D11 support) or
   BVR_NO_HEADSET (runtime is there, HMD is not). */
int32_t xr_create_instance();

/* Managed thread. Marks the session as wanted; the render thread creates it on
   the next Present, once the device is available. */
int32_t xr_request_session();

/* Render thread. Drives the whole per-frame cycle. Never throws. */
void xr_tick(IDXGISwapChain* swapChain, ID3D11Device* device, ID3D11DeviceContext* context);

/* Whether a VR mission is rendering. False puts the headset on the flat screen
   layer instead - the main menu, the campaign map, loading, and every menu that
   is not a mission. See flat_screen_submit(). */
int32_t xr_set_mission_active(int32_t active);

/* What xr_set_mission_active last set. Any thread. */
bool xr_mission_active();

/* Whether a menu should be composited OVER the world rather than replacing it.
   The tactics wheel and anything else meant to be used mid-fight. */
int32_t xr_set_ui_overlay(int32_t active);

/* Whether a mission exists, as distinct from whether the stereo path is live.
   Drives the window mirror and the backbuffer clear. */
int32_t xr_set_in_mission(int32_t active);

/* Size and distance of the keyed interface layer, in metres. */
int32_t xr_set_ui_geometry(float width, float distance);

/* Size and distance of the flat screen, in metres. */
int32_t xr_set_screen_geometry(float width, float distance);

/* Forgets where the flat screen was pinned, so the next frame that shows it
   places it in front of the head again. Wired to recentre. */
int32_t xr_repin_flat_screen();

bool    xr_recommended_size(uint32_t* width, uint32_t* height);
int32_t xr_session_state();
bool    xr_instance_ready();

/* Managed thread. Names the eye the engine is about to render, for AFR.
   -1 leaves AFR mode. */
int32_t xr_set_afr_eye(int32_t eye);

/* 1 when the eye index last published was actually consumed by a scene capture,
   0 when it was not. See XrState::afrEyeUsed. */
int32_t xr_afr_eye_consumed();

/* The eye the engine should draw next, owned by the capture path so it cannot
   drift from the frames actually captured. */
int32_t xr_next_afr_eye();

/* Hold the sequence on one eye (>= 0) or release it back to the capture path
   (-1). Used by afw_pin_source_eye. */
int32_t xr_pin_afr_eye(int32_t eye);

/* Which eye the engine is drawing right now, or -1. Read by the NGX hook to
   decide which of the two DLSS features an evaluation belongs to. */
int32_t xr_current_afr_eye();

/* The yaw of the anchor the eye frames are built on - the rotation the OpenXR
   runtime cannot see. Feeds the AFR turn correction; see turn_corrected_pose. */
int32_t xr_set_body_yaw(float yawRadians);

/* Where the anchor - the game camera - is this frame, in the same space the eye
   poses are submitted in. Pairs with the yaw above: that one corrects a held eye
   for the game camera TURNING between the two eyes of an AFR pair, this one for
   it MOVING, which on a horse is much the larger error. See
   move_corrected_pose. */
int32_t xr_set_body_position(float x, float y, float z);

/* Where the camera is NOW, as opposed to where the frame was drawn from. With
   the pair camera latched the two differ, and only their difference can smooth a
   45 Hz camera back to display rate. See XrState::targetYaw. */
int32_t xr_set_body_target(float yawRadians, float x, float y, float z);

/* The poses the frame was actually RENDERED from. See XrState::usedPose. */
int32_t xr_set_used_views(const BvrEyeView* views, int64_t displayTime);

/* Which simulation tick the world is on. Lets the AFR capture path hold a
   fresh eye back until its partner comes from the same tick; negative
   disables the pairing entirely. See afr_capture. */
int32_t xr_set_sim_generation(int32_t generation);

/* Full AFR: one eye per GAME frame, two game frames per DISPLAY frame. The
   first Present of a pair captures its eye and hands the begun XR frame back
   instead of ending it, so the second eye is rendered from the same locate and
   the same world state and the pair is submitted together - both eyes real, at
   the full display rate. See XrState::fullAfr and the defer branch in
   render_frame. */
int32_t xr_set_full_afr(int32_t active);

/* Selects AFW and hands it the stereo geometry the warp needs. Lengths are in
   game world units. See afw_warp.h. */
int32_t xr_set_afw(int32_t mode, float focalPx, float baseline,
                   float zNear, float zFar);

/* Managed thread. Latest poses published by the render thread. */
bool xr_get_predicted_views(BvrEyeView* outViews, int64_t* outDisplayTime);

/* LATE LATCH (TEST 168). Any thread, no OpenXR call.
 *
   xr_get_used_views: the stage poses managed last reported rendering with
   (XrState::usedPose), orientation and position only. False until one exists.
 *
   xr_set_latched_orientation: the engine is about to build this eye's frame
   from THIS stage orientation instead of the used one - the late latch wrote it
   into the camera. afr_capture labels the next capture of that eye with it, so
   the compositor is told the pose the pixels were really drawn from.
 *
   xr_latched_captures: how many captures carried a latched orientation. */
bool     xr_get_used_views(BvrEyeView* outViews);
void     xr_set_latched_orientation(uint32_t eye, const BvrQuat& orientation);
uint64_t xr_latched_captures();

/* Begins the OpenXR frame BEFORE the engine renders it: waits on the
   compositor, begins the frame, locates the views for the display time of the
   frame about to be drawn, and publishes that pose. Called from the game thread
   at the top of the camera build. False when the session is not running, a
   frame is already in flight, or pre_render_frame is off - in which case the
   Present hook does Wait/Begin/Locate itself, as it always did. */
bool xr_prepare_render_frame();

void xr_shutdown();

} // namespace bvr

#endif /* BVR_XR_CONTEXT_H */
