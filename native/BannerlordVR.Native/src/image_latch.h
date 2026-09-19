/* =============================================================================
 * IMAGE LATCH - the late latch, moved out of the engine and onto the finished
 * frame.
 *
 * WHAT IT IS FOR
 *
 * vp_patch is a late latch in MATRIX space: it rotates the main view by D at
 * constant-buffer upload time, so the geometry lands where the head is now
 * instead of where it was when the camera was set. That is what makes the head
 * feel instant, and it is also the whole of the set A problem, because the
 * upload happens AFTER the passes that already consumed the camera:
 *
 *     "D is the rigid move from the camera the engine drew with to the eye we
 *      want, and it is EXACTLY the amount by which the scene ends up displaced
 *      relative to the shadow map - because the shadow map was rendered before
 *      this ran and knows nothing about it."   - vp_patch.cpp
 *
 * The sky, the sun, the clouds, the particles and the shadow-receive pass stay
 * in the unrotated frame while the geometry moves. They slide by exactly D,
 * which is the head's own rotation, and that reads as "they move with my head".
 * With a temporal upscaler running it is averaged out of sight; with DLSS off
 * there is nothing to hide it, which is why turning DLSS off exposes it.
 *
 * THE SAME LATCH, ONE STAGE LATER
 *
 * Let the engine render the whole frame from ONE camera - vp_patch = 0, nothing
 * substituted, every pass agreeing with every other pass - and then rotate the
 * FINISHED IMAGE by D on the way to the compositor. Sky, shadows, effects and
 * geometry all move together, because by then they are the same pixels.
 *
 * WHY THIS IS CHEAP RATHER THAN A SECOND AFW
 *
 * D's translation is measured at 0.0 mm over 450 of 450 publishes: D is a PURE
 * ROTATION. A rotation about the eye is a homography - every point on a ray
 * maps to one output pixel whatever its depth - so this needs no depth buffer,
 * has no disocclusion, and cannot produce a hole. It is one full-screen pass
 * with a 3x3 matrix, not a scatter/gather reprojection.
 *
 * The only thing it can run out of is IMAGE, at the leading edge. The eye is
 * already rendered through a widened symmetric containing frustum while
 * symmetric_fov = 1, so there is margin there to be spent.
 *
 * WHAT IT DOES NOT DO
 *
 * It corrects the VIEWPOINT, not the CONTENT. The soldiers are still where the
 * engine last simulated them, and a held AFR eye still depicts a moment one
 * frame old. What goes away is the world rotating under the head with the sky
 * and the shadows left behind.
 * ========================================================================== */
#pragma once

#include <cstdint>

#include <d3d11.h>
#include <openxr/openxr.h>

namespace bvr {

/* 0 off, 1 applied, -1 applied inverted.
 *
   Three-way rather than a bool, and for the reason this file's config has
   written down three times: a sign is the easiest thing here to get backwards,
   and TESTs 101, 102 and 104 each shipped one that had been reasoned about
   rather than tested. The run decides. */
float image_latch_mode();

/* True when the latch is armed at all. */
bool image_latch_enabled();

/* Rotate `source` so that it corresponds to `nowOrientation` instead of the
   orientation it was rendered from.
 *
   Returns the texture to submit: the warped one on success, `source` itself
   unchanged on ANY failure. Every early return in here leaves the caller with
   exactly the image it would have submitted without this module, which is what
   makes it safe to leave armed. */
ID3D11Texture2D* image_latch_apply(ID3D11Device* device,
                                   ID3D11DeviceContext* context,
                                   uint32_t eye,
                                   ID3D11Texture2D* source,
                                   const XrQuaternionf& renderOrientation,
                                   const XrQuaternionf& nowOrientation,
                                   const XrFovf& fov);

/* The rotation actually applied on the last successful call for this eye, in
   degrees. 0 when nothing was applied. Used by the submitted pose so the
   compositor is told what the pixels now are rather than what they were. */
bool image_latch_applied(uint32_t eye);

/* Once-a-second summary: how many eyes were latched and by how much. Called
   from the same place as the other per-second reports. */
void image_latch_report();

/* Drops every device object. Called on session teardown, like the AFW keeps. */
void image_latch_release();

/* =============================================================================
 * HEAD OVERDRIVE - the vp_patch feel, without vp_patch.
 *
 * WHAT vp_patch = 1 ACTUALLY DOES TO THE FEEL, which took until TEST 134 to say
 * plainly:
 *
 *   The geometry is drawn from the head pose at DRAW time - the camera-build
 *   pose plus D. The pose SUBMITTED to the compositor is still the camera-build
 *   pose, because managed publishes what it built the cameras from and D is
 *   computed natively, later, at constant-buffer upload. Nothing tells the
 *   runtime about D.
 *
 *   So the compositor applies its whole correction on top of an image that has
 *   already been corrected once, and the world ends up rotated by MORE than the
 *   head moved, settling back when the head stops.
 *
 * That over-rotation is what reads as "the world is separated from my head" and
 * "no resistance on a sudden movement". It is also exactly why set A slides:
 * the sky and the shadows are rendered WITHOUT D, so they land correctly while
 * the geometry lands over-rotated, and the difference between them is D.
 *
 * ONE MECHANISM, BOTH EFFECTS - the liked one and the hated one.
 *
 * THE POINT OF DOING IT HERE INSTEAD
 *
 * The same over-rotation can be produced for the WHOLE FRAME by telling the
 * compositor the image came from a pose slightly BEHIND where it did. Sky,
 * shadows, effects and geometry are all over-rotated together, because they are
 * all one image by then and nothing inside it can disagree.
 *
 * It costs one quaternion multiply on the submitted pose. No shader, no engine
 * matrix, no second render.
 *
 * IT IS NOT "CORRECT", AND THAT IS THE POINT. Truth is k = 0, and truth is what
 * measured clean through TESTs 131-133 while still feeling wrong. This makes the
 * deviation a DIAL with a number on it instead of a side effect of a matrix
 * patch that also breaks the shadows.
 * ========================================================================== */

/* 0 = off and the submitted pose is the truth. 1 = the full render-to-now
   rotation, which is the vp_patch = 1 amount. Negative applies it the other
   way, because the composition order is the one thing here most likely to be
   backwards and this file has paid for guessing three times. */
float head_overdrive();

/* ONE DELTA PER FRAME, APPLIED TO BOTH EYES. THE SPLIT IS THE WHOLE POINT.
 *
   The first version of this took the render pose PER EYE, and under AFR the two
   eyes hold render poses from different moments - one is a frame older. The
   render-to-now rotation is therefore LARGER for the stale eye and smaller for
   the fresh one, so each eye was biased by a different amount. A stereo pair
   whose halves are rotated differently no longer differs by the IPD alone, and
   the eyes are asked to fuse two views that disagree in yaw. That is doubling,
   it shows only while the head moves, and it is the same fault image_latch = -1
   produced for the same reason.
 *
   A head rotation is RIGID: one rotation, both eyes, identically. So the delta
   is computed once from a single reference eye and then applied unchanged to
   every eye - which leaves the baseline between them exactly as rendered. */

/* =============================================================================
 * THE PREDICTOR - the half the overdrive cannot reach.
 *
 * head_overdrive is REACTIVE: it measures the rotation that happened between
 * the render and now, so at the instant a movement STARTS that quantity is
 * zero and the dial contributes nothing. That is "resistance on the first
 * sudden movement out of a stall", and no value of k has ever touched it,
 * because there is nothing yet to multiply.
 *
 * A predictor is anticipatory. At the onset of a fast movement the angular
 * VELOCITY is still near zero but the ACCELERATION is not - that is what onset
 * means - so a term in acceleration produces a bias on the first frame where
 * the head starts to move, which is exactly where the reactive dial is blind.
 *
 *     predicted rotation over horizon T  =  w * T  +  0.5 * a * T^2
 *
 * The result is used to carry the "now" end of the delta FORWARD in time, so
 * everything downstream - the rigid pair rotation, the coherence gate, the
 * bound - applies unchanged. The predictor moves one endpoint; it does not add
 * a second mechanism beside the first.
 *
 * OVER-PREDICTION OVERSHOOTS, and this file has said so since pose_lead_frames
 * was added. The horizon is therefore a dial with a bound, and the coherence
 * gate still withdraws it during a shake - where extrapolating a reversing
 * velocity is worse than doing nothing at all.
 * ========================================================================== */

/* Horizon in milliseconds. 0 disables the predictor and the build is exactly
   what it was without it. */
float head_predict_ms();

/* Carries `nowOrientation` forward by the horizon, using angular velocity and
   acceleration estimated from recent frames. `displayTimeNs` is the frame's
   predicted display time, used as the clock - it is monotonic and already in
   nanoseconds, so no separate timer is needed.

   Returns `nowOrientation` unchanged when the predictor is off, when there is
   not yet enough history, or when the estimate exceeds its bound. */
XrQuaternionf head_predict(const XrQuaternionf& nowOrientation,
                           int64_t displayTimeNs);

/* The world-space over-rotation for this frame, from ONE reference eye. Returns
   identity when the dial is at zero. */
XrQuaternionf head_overdrive_delta(const XrQuaternionf& renderOrientation,
                                   const XrQuaternionf& nowOrientation);

/* Applies the delta to the PAIR as one rigid head rotation.
 *
   Rotating the orientations alone is not a head rotation - it is two eyes
   swivelling where they stand. A real head turn orbits both eyes about the head
   centre, and the difference matters as soon as a depth layer is submitted:
   the compositor reconstructs world points from the pose it is given and
   reprojects them, so a pose whose orientation and positions disagree produces
   an error that depends on depth AND differs between the eyes. That is a
   disparity the eyes cannot fuse, which is doubling.
 *
   So both poses move together: orientation pre-multiplied, position orbited
   about the midpoint between them. The IPD is preserved exactly, because a
   rigid rotation of both endpoints about their own midpoint cannot change the
   distance between them. */
void head_overdrive_rigid(const XrQuaternionf& delta, XrPosef& left, XrPosef& right);

/* Degrees of over-rotation applied this frame, for the per-second report. */
void head_overdrive_report();

/* Once-a-second summary of the predictor. */
void head_predict_report();

} // namespace bvr
