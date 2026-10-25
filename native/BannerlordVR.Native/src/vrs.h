/* =============================================================================
 * vrs - foveated rendering with NVIDIA Variable Rate Shading (TEST 427).
 *
 * WHY
 * ---
 * Crowded towns are pixel-bound: the G-buffer costs 5.5-7.5 ms while the depth
 * prepass draws the same geometry for ~0.5 ms (TEST 420). A VR eye spends most of
 * its pixels at the edges, where the lens blurs them anyway. VRS shades a 16x16
 * tile there once per 2x2 or 4x4 pixels; the centre stays at full rate.
 *
 * HOW IT SHIPS
 * ------------
 * D3D11 has no VRS of its own; it is NVAPI's, on Turing (RTX 20 / GTX 16) and
 * newer. nvapi64.lib (MIT, native/thirdparty/nvapi) is compiled in and loads the
 * driver's own nvapi64.dll when asked, so nothing ships beside the mod. On AMD,
 * Intel or an older card NvAPI_Initialize or the caps query fails, and this
 * stays off.
 *
 * WHICH DRAWS
 * -----------
 * Native stereo's re-issued operations whose viewport is shaped like the eye:
 * the scene, its lighting and post - never a shadow cascade, a lookup table or
 * our own work. Both eyes share one symmetric projection (stereo_dup.h), so one
 * map serves both and nothing changes between the engine's draw and the
 * re-issue. NVAPI is called only when the state changes, not per draw.
 *
 * EYE TRACKING (TEST 428)
 * -----------------------
 * With a gaze from the runtime (XR_EXT_eye_gaze_interaction; PSVR2 through
 * PSVR2Toolkit) the full-rate centre sits where the eyes look, and its rings are
 * degrees from the gaze rather than fractions of the view - much smaller, since
 * the eye no longer roams inside them. The map is redrawn in place when the gaze
 * moves a degree. Without a gaze it is the fixed map above.
 *
 * The edges are coarse, not culled: 4x4 costs a sixteenth of full rate, and a
 * culled corner would show black wherever reprojection or a taller-FOV headset
 * reaches it.
 * ========================================================================== */
#ifndef BVR_VRS_H
#define BVR_VRS_H

#include <d3d11.h>
#include <stdint.h>

namespace bvr {

/* 0 off, 1 wide, 2 balanced, 3 narrow - the size of the full-rate centre, from
   the largest to the smallest. Set by the VR panel and by managed's push;
   starts from cfg `vrs`. */
void vrs_set_preset(int preset);
int  vrs_preset();

/* TEST 428: the full-rate centre follows the eye tracker's gaze
   (XR_EXT_eye_gaze_interaction) instead of sitting on the lens axis. Falls back
   to the fixed centre whenever there is no gaze. Set by the VR panel and by
   managed's push; starts from cfg `vrs_eye` (default on). */
void vrs_set_eye(bool follow);
bool vrs_eye();

/* -1 not known yet (no draw has asked), 0 unavailable on this GPU, 1 available. */
int vrs_supported();

/* Per draw, before it runs. `eyeVp` is the viewport of a re-issued draw shaped
   like the eye; null for every other draw. Render thread. */
void vrs_draw(ID3D11DeviceContext* c, const D3D11_VIEWPORT* eyeVp);

/* The periodic log line, over `frames` frames; resets the counters. */
void vrs_report(uint64_t frames);

} // namespace bvr

#endif /* BVR_VRS_H */
