/* =============================================================================
 * vr_sharpen - sharpening of the finished eye images, in every render mode.
 *
 * WHY HERE (TEST 187)
 * -------------------
 * The game's sharpening slider feeds DLSS's Sharpness parameter, which DLSS has
 * ignored since 2.5.1 (the game ships 310.7). TEST 186 sharpened DLSS's output
 * inside native stereo; the player asked for a slider of our own that works in
 * EVERY renderer - AFR, DR and native stereo, with or without DLSS.
 *
 * The one point all of them share is the submit. The held image is final -
 * tonemapped, post-processed, gamma-encoded - which is what CAS is designed for.
 * CAS writes into the acquired swapchain image when it supports a compatible
 * UAV, otherwise into our intermediate texture followed by a copy. The held
 * image is never written, so an AFR eye shown for two
 * display frames is sharpened from the same source twice, never twice over.
 *
 * AMD FidelityFX CAS or RCAS (both MIT, TEST 790). Strength 0 is off and costs nothing.
 * ========================================================================== */
#ifndef BVR_VR_SHARPEN_H
#define BVR_VR_SHARPEN_H

#include <d3d11.h>
#include <stdint.h>

namespace bvr {

/* 0..1.5 (CAS stops at 1, TEST 791). Set by the VR panel and by managed's push; starts from cfg `sharpen`. */
void  vr_sharpen_set(float strength);
float vr_sharpen_get();

/* TEST 790: 0 CAS, 1 RCAS. Set by the VR panel; starts from cfg `sharpen_mode`. */
void vr_sharpen_set_mode(int mode);
int  vr_sharpen_get_mode();

/* The image to submit for this eye: `source` itself when sharpening is off or
   unavailable, otherwise a sharpened image (not AddRef'd), with the
   same size and format family. An optional destination UAV writes directly to
   an acquired eye image when compatible; otherwise the usual output is used.
   Returns borrowed textures; the caller must keep the destination view alive.
   Render thread. */
ID3D11Texture2D* vr_sharpen_apply(ID3D11Device* device, ID3D11DeviceContext* ctx,
                                  uint32_t eye, ID3D11Texture2D* source,
                                  ID3D11UnorderedAccessView* destination = nullptr);

/* Drops the textures (session teardown). */
void vr_sharpen_release();

} // namespace bvr

#endif /* BVR_VR_SHARPEN_H */
