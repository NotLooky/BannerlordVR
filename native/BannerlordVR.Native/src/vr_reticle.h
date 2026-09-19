/* =============================================================================
 * The aiming reticle.
 *
 * WHY IT HAS TO BE DRAWN HERE AND NOT TAKEN FROM THE GAME
 *
 * Bannerlord's own crosshair is a Gauntlet UI element composited into the game
 * WINDOW. The headset never sees the window - we retarget the engine's scene
 * view into our own eye texture and submit that - so the game's crosshair is
 * not something we can move into VR. It has to be drawn.
 *
 * WHERE IT GOES
 *
 * At the centre of each eye, offset by half the stereo disparity for the depth
 * it is meant to sit at. A reticle drawn at the literal centre of both eyes
 * sits at INFINITY, which is wrong in the one way that matters: it will not
 * fuse with the man you are aiming at, and the eyes give up and see two of it.
 *
 *     offset = focal_px * baseline / distance / 2
 *
 * applied outward for the left eye and inward for the right.
 *
 * WHY THE CENTRE IS THE RIGHT PLACE AT ALL
 *
 * Only because head aiming is on. The view is built as anchor * head and the
 * game's look is driven to anchor + head, so the two agree by construction and
 * the aim direction IS the centre of the view. With head_drive_yaw = 0 the
 * game aims where the mouse points while the view looks where the head does,
 * and a centre reticle would be confidently wrong. The two features belong
 * together.
 * ========================================================================== */
#ifndef BVR_VR_RETICLE_H
#define BVR_VR_RETICLE_H

#include <d3d11.h>
#include <stdint.h>

namespace bvr {

/* Whether to draw at all. Read once from the config. */
bool reticle_enabled();

/* Draws into one eye of a held image, straight after it was captured.
 *
 * Called once per capture, never per submitted frame, which is what keeps it
 * from accumulating: the CopyResource that precedes it has just overwritten
 * the whole texture, so each reticle is drawn onto a clean picture.
 *
 * focalPx and baseline are the same numbers the warp uses; a zero baseline
 * simply places the reticle at the centre of both eyes. */
void reticle_draw(ID3D11Device* device, ID3D11DeviceContext* context,
                  ID3D11Texture2D* target, int eye,
                  float focalPx, float baseline);

void reticle_release();

} // namespace bvr

#endif /* BVR_VR_RETICLE_H */
