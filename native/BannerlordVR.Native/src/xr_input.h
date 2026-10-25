/* =============================================================================
 * Controller input.
 *
 * WHAT THIS IS FOR
 *
 * Motion combat: reading where the weapon hand is and which way it is moving,
 * so a swing can become an attack direction and a raised weapon can become a
 * block. None of that is here - this file is only the part that has to exist
 * first, which is knowing where the controllers are at all.
 *
 * bvr_get_input_state has been a stub returning zeros since the ABI was
 * written. Everything downstream of it was therefore un-writable, which is why
 * this comes before the gestures rather than after them.
 *
 * WHY AN ACTION SET RATHER THAN READING BUTTONS
 *
 * OpenXR has no "read the trigger" call. Input is declared as ACTIONS, bound to
 * physical controls per interaction profile by the runtime, and then synced once
 * a frame. That indirection is the whole reason a single binding table works
 * across Touch, Index, Vive and WMR without the application knowing which is
 * plugged in - and it is why the bindings below are suggestions rather than
 * assignments. The runtime picks.
 *
 * VELOCITY IS REQUESTED, NOT DIFFERENTIATED
 *
 * XrSpaceVelocity gives the runtime's own linear velocity for the hand, which is
 * fused from the tracking system rather than reconstructed by us from two
 * positions and a frame time. For swing detection that difference matters: a
 * differentiated velocity carries every bit of tracking jitter multiplied by the
 * frame rate, and a swing threshold set above that noise is a swing you have to
 * throw far harder than a real one.
 * ========================================================================== */
#ifndef BVR_XR_INPUT_H
#define BVR_XR_INPUT_H

#include "bvr_api.h"

#include <openxr/openxr.h>

namespace bvr {

/* Declares the actions and their bindings, and attaches them to the session.
 * Must be called once, after the session exists and before the first sync.
 * Returns false on failure, which leaves input reporting inactive rather than
 * taking anything else down. eyeGaze: XR_EXT_eye_gaze_interaction is enabled
 * on the instance, so the gaze is bound too (TEST 428). */
bool input_create(XrInstance instance, XrSession session, bool eyeGaze);

/* Syncs the action set and locates both hands. Call once per frame, after the
 * display time for that frame is known. */
void input_sync(XrSession session, XrSpace baseSpace, XrTime displayTime);

/* The most recently synced state. Returns false if input never came up. */
bool input_read(BvrInputState* out);

/* TEST 452: the GAME thread locates both hands for its own moment, once per game
 * frame (the render thread's sync raced it and skipped). leadFrames display
 * periods are added. True when the hands were located fresh. Game thread only. */
bool input_latch(float leadFrames);

/* The game thread's read: this frame's latch while fresh, else input_read. */
bool input_read_game(BvrInputState* out);

/* A pulse on one controller (0 = left, 1 = right). Amplitude 0..1, duration in
 * seconds. Returns false when the session, the action set or the binding is not
 * there - a caller may retry every block without guarding anything itself.
 * Callable from the game thread: xrApplyHapticFeedback is thread-safe and the
 * session handle is guarded against shutdown. */
bool input_haptic(int hand, float amplitude, float durationSeconds);

/* A hand's grip action space (0 = left, 1 = right), for a composition layer the
 * compositor should carry with the controller (the wrist HUD, TEST 377).
 * XR_NULL_HANDLE until input is up. */
XrSpace input_grip_space(int hand);

/* TEST 428: where the eyes look, as tangents in the head's frame (x right,
 * y up) at the last sync - the space the rendered frustum is measured in.
 * False when there is no gaze (no tracker, or lost for over a third of a
 * second). Any thread. */
bool input_gaze(float* x, float* y);

/* True when the eye gaze action exists: the runtime offered the extension. */
bool input_gaze_bound();

/* TEST 482: the touch sensors at the last sync - left in bits 0-7, right in
 * 8-15, each BVR_TOUCH_* (bvr_api.h). Any thread. */
uint32_t input_finger_touch();

void input_destroy();

} // namespace bvr

#endif /* BVR_XR_INPUT_H */
