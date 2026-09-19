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
 * taking anything else down. */
bool input_create(XrInstance instance, XrSession session);

/* Syncs the action set and locates both hands. Call once per frame, after the
 * display time for that frame is known. */
void input_sync(XrSession session, XrSpace baseSpace, XrTime displayTime);

/* The most recently synced state. Returns false if input never came up. */
bool input_read(BvrInputState* out);

void input_destroy();

} // namespace bvr

#endif /* BVR_XR_INPUT_H */
