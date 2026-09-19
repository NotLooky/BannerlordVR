/* =============================================================================
 * late_latch - putting the head pose into rgl's own camera, at the one point
 *              where rgl turns that camera into every matrix of the frame.
 *
 * WHY (TEST 166)
 * --------------
 * vp_patch substitutes the head pose into constant buffers AFTER the engine has
 * derived the frame from its own camera, and only into the uploads it recognises.
 * Measured: the rotation it applies is 2-6 deg while the head moves, and some
 * passes get it while others do not. A frame then holds two viewpoints, and the
 * dynamic-shadow mask - which every sun lighting pass reads - is reconstructed in
 * one and drawn in the other. That is "shadows, reflections, hair and sky follow
 * my head, faster the faster I move".
 *
 * WHERE (reverse engineered, TaleWorlds.Native.dll 2026-08-16)
 * -----------------------------------------------------------
 *   sub_18028C210 (rva 0x28C210)  the view's matrix builder. Called from
 *     rglScene_view::render (vf28) through sub_180364840 / sub_180365120. It
 *     takes the view's CAMERA FRAME at view+0xF0 (three axis rows, then the
 *     position at +0x120, 64 bytes), inverts it into g_view (+0x18080), builds
 *     the projection (+0x180C0), and multiplies them into the unjittered and
 *     jittered view-projections (+0x18140, +0x18100). The previous frame's VP
 *     (+0x181C0) comes from a history object, not from anything written here.
 *   sub_1800D4AD0 then copies those into per_framef, and computes
 *     g_view_proj_inverse from +0x18100 on the spot.
 *
 *   So a camera frame changed on ENTRY to the builder reaches every matrix the
 *   frame is drawn with, and all of them agree - which is the whole point.
 *
 * PHASES
 * ------
 *   late_latch_probe = 1   read-only: hook, verify, and report what the engine
 *                          is about to build from, against the published camera.
 *   late_latch = 1         (not yet) write the fresh eye pose into the frame.
 * ========================================================================== */
#ifndef BVR_LATE_LATCH_H
#define BVR_LATE_LATCH_H

namespace bvr {

/* late_latch_probe = 1 or late_latch = 1. Read once. */
bool late_latch_active();

/* Hooks the builder after checking its bytes. Idempotent; call every Present.
   A signature mismatch (a game update) refuses the hook and says so once. */
void late_latch_install();

/* Present thread: the periodic report. */
void late_latch_frame_boundary();

/* Render thread, every constant-buffer upload (vp_patch's Unmap hook): a
   per_framef upload whose camera rows match a latched build names the build the
   frame being drawn came from. TEST 178. */
void late_latch_note_upload(const void* data, unsigned int bytes);

/* Render thread, at an AFW capture: the latched stage orientation (x, y, z, w)
   of the build this frame was drawn from, if one was matched since the last
   call for this eye. False means the caller falls back to the slot. */
bool late_latch_drawn_orientation(unsigned int eye, float outXyzw[4]);

} // namespace bvr

#endif /* BVR_LATE_LATCH_H */
