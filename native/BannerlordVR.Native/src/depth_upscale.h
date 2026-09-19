/* =============================================================================
 * depth_upscale - makes a game depth buffer the same size as its colour image.
 *
 * WHY THIS EXISTS
 *
 * The depth composition layer lets the compositor reproject a held AFR eye for
 * head MOVEMENT rather than rotation alone, and translation is the only error
 * AFR actually leaves on the screen. To do it the compositor needs a depth map
 * for the image it is about to move.
 *
 * With DLSS on, the game does not render one at the size of that image. The
 * scene is drawn at DLSS's internal resolution - half the eye in each axis at
 * Performance - and only the COLOUR is upscaled on the way out. So the depth
 * that exists is 1460x1470 against a 2920x2940 eye.
 *
 * OpenXR is explicit that the depth carries its own XrSwapchainSubImage, and on
 * a careful reading a smaller depth rect ought to be sampled across the same
 * normalised rectangle as the colour. Submitting it that way is what made the
 * ground shake, and the reading is not worth defending against a runtime that
 * disagrees: a compositor that pairs the two rects pixel-for-pixel instead
 * reads depth from the top-left quarter of the view and applies it to all of
 * it, which is a large, geometry-dependent reprojection error that comes and
 * goes with head movement. That is what a slowly shaking world looks like.
 *
 * So rather than argue about whose reading is right, this removes the question.
 * The depth is resampled to the eye's exact size and submitted with a rect that
 * matches the colour's, and no runtime has anything left to interpret.
 *
 * WHAT IT DOES NOT FIX
 *
 * DLSS renders each frame with a sub-pixel JITTER offset and de-jitters the
 * colour on the way out. Nothing de-jitters the depth, so it stays offset from
 * the colour by up to about one output pixel, on a slowly repeating pattern.
 * The offset is not published by the engine and cannot be recovered from the
 * outside.
 *
 * That residual is small where it matters. The compositor's displacement is
 * focal * translation / z, and over one frame of head movement that is a couple
 * of pixels at arm's length and a fraction of one further out - so a depth that
 * is right in the interior of every surface and uncertain by a pixel or two at
 * silhouettes gives a correction that is right across the whole of the ground
 * and slightly soft at edges. That is the trade, and it is worth taking.
 * ========================================================================== */
#ifndef BVR_DEPTH_UPSCALE_H
#define BVR_DEPTH_UPSCALE_H

#include <d3d11.h>
#include <stdint.h>

namespace bvr {

/* Resamples `src` to dstWidth x dstHeight and returns the texture holding the
   result, or null if anything was missing. The returned texture is owned here,
   is R32_TYPELESS, and is valid only until the next call - the caller is
   expected to CopyResource out of it immediately.

   R32_TYPELESS so that a CopyResource into a D32_FLOAT swapchain image is a
   same-group copy, which is the only kind D3D11 allows. That is also why the
   depth swapchain must be created as D32_FLOAT when this path is in use: see
   ensure_depth_swapchains. */
ID3D11Texture2D* depth_upscale(ID3D11Device* device, ID3D11DeviceContext* context,
                               ID3D11Texture2D* src,
                               uint32_t dstWidth, uint32_t dstHeight);

/* True once the shader has failed to compile or a resource could not be made,
   so the caller can stop asking rather than failing silently every frame. */
bool depth_upscale_failed();

void depth_upscale_release();

} // namespace bvr

#endif /* BVR_DEPTH_UPSCALE_H */
