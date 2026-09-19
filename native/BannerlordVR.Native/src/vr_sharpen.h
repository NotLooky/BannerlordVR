/* =============================================================================
 * vr_sharpen - sharpening of the finished eye images, in every render mode.
 *
 * WHY HERE (TEST 187)
 * -------------------
 * The game's sharpening slider feeds DLSS's Sharpness parameter, which DLSS has
 * ignored since 2.5.1 (the game ships 310.7). TEST 186 sharpened DLSS's output
 * inside native stereo; the player asked for a slider of our own that works in
 * EVERY renderer - AFR, AFW and native stereo, with or without DLSS.
 *
 * The one point all of them share is the submit: each displayed frame copies
 * the eye's held image into its swapchain. The image there is final - tonemapped,
 * post-processed, gamma-encoded - which is what CAS is designed for. So the held
 * image is sharpened into a texture of ours and THAT is copied into the
 * swapchain. The held image is never written, so an AFR eye shown for two
 * display frames is sharpened from the same source twice, never twice over.
 *
 * AMD FidelityFX CAS (MIT). Strength 0 is off and costs nothing.
 * ========================================================================== */
#ifndef BVR_VR_SHARPEN_H
#define BVR_VR_SHARPEN_H

#include <d3d11.h>
#include <stdint.h>

namespace bvr {

/* 0..1. Set by the VR panel and by managed's push; starts from cfg `sharpen`. */
void  vr_sharpen_set(float strength);
float vr_sharpen_get();

/* The image to submit for this eye: `source` itself when sharpening is off or
   unavailable, otherwise a sharpened copy owned here (not AddRef'd), with the
   same size and format family. Render thread. */
ID3D11Texture2D* vr_sharpen_apply(ID3D11Device* device, ID3D11DeviceContext* ctx,
                                  uint32_t eye, ID3D11Texture2D* source);

/* Drops the textures (session teardown). */
void vr_sharpen_release();

} // namespace bvr

#endif /* BVR_VR_SHARPEN_H */
