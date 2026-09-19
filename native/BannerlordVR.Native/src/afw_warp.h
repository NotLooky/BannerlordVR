/* =============================================================================
 * AFW - Alternate Frame Warping.
 *
 * WHAT IT IS
 *
 * One scene render per frame, exactly as AFR. The difference is what happens to
 * the eye that was not rendered: instead of showing it a held image from the
 * previous frame, we SYNTHESISE it from the one just drawn, by reprojecting
 * that image through its own depth buffer.
 *
 * WHY THAT IS THE WHOLE ARGUMENT
 *
 * Every artefact this mod has fought for the last several rounds comes from the
 * same root: under AFR the two eyes on screen were drawn at different moments.
 * A frame apart means the soldiers moved, the camera turned, and the stereo
 * baseline stopped being the IPD. We answered that with a pose correction (TEST
 * 47), then by pairing eyes from one simulation tick (TEST 48), then by holding
 * the camera still across a pair (TEST 49) - three rounds of making two moments
 * agree with each other.
 *
 * AFW removes the second moment. Both eyes come from ONE render of ONE frame:
 * one is the render, the other is a reprojection of it. They cannot disagree
 * about where the soldiers are, where the camera is pointing, or how far apart
 * the eyes are, because there is only one of each.
 *
 * WHAT IT COSTS INSTEAD
 *
 * Disocclusion. The synthesised eye can only show what the rendered eye saw,
 * and a few pixels behind the left edge of a near object are genuinely absent
 * from the source. The saving grace is the baseline: eyes are ~63 mm apart, so
 * the missing slivers are narrow, and they sit exactly where a real stereo pair
 * has its least reliable information anyway.
 *
 * WHY THE WARP IS ONE-DIMENSIONAL
 *
 * With symmetric_fov the two eyes have IDENTICAL frusta and differ by a purely
 * horizontal translation, which is the rectified stereo case. A world point
 * moves along a scanline and nowhere else, by
 *
 *     disparity = focal_px * baseline / z
 *
 * so the whole reprojection is a per-pixel horizontal shift. That makes it a
 * cheap 1D search rather than a general 3D scatter, and it is another reason
 * symmetric_fov earns its keep.
 * ========================================================================== */
#ifndef BVR_AFW_WARP_H
#define BVR_AFW_WARP_H

#include <d3d11.h>
#include <stdint.h>

namespace bvr {

/* The geometry the warp needs, published from the managed side because that is
   where all of it is already known. Lengths are in game world units, which is
   the space the depth buffer linearises into. */
struct AfwParams
{
    float focalPx;    /* (width / 2) / tan(hfov / 2) */
    float baseline;   /* distance between the eyes */
    float zNear;
    float zFar;
};

void afw_set_params(const AfwParams& params);
bool afw_have_params();

/* The stereo geometry, for anything that needs it - the reticle needs it in
   AFR too, where the warp itself never runs. Zero until it has been published. */
float afw_focal_px();
float afw_baseline();

/* The near and far planes the depth buffer was rendered with, and which end of
   the stored 0..1 range is the near one. The depth composition layer needs all
   three to tell the compositor what the values it is about to read MEAN; see
   submit_depth() in xr_context.cpp. Zero until parameters are published. */
float afw_z_near();
float afw_z_far();
bool  afw_depth_is_reversed();

/* True when everything the warp needs exists: parameters, a depth buffer that
   can be sampled, and a compiled shader. */
bool afw_ready();

/* True once the calibration has concluded that this scene's depth cannot
   predict the second eye, and AFW has taken itself out of the frame path for
   good.
 *
 * The caller must check this BEFORE asking for a warp, so the per-frame
 * fallback stops being counted and logged: standing down is one decision, not
 * ninety failures a second. Ordinary AFR runs from then on, which is the right
 * answer - a real render of the other eye, one frame old, beats a copy of THIS
 * eye submitted from the other eye's position, which is what a zero-gain warp
 * used to produce and which no pair of eyes can fuse. */
bool afw_stood_down();

/* Synthesises the eye OPPOSITE srcEye from the image just rendered.
 *
 * `srcColor` is the eye texture rgl drew into and `dst` receives the
 * reprojection. Both must be the same size and format. Returns false if
 * anything was missing, in which case the caller should fall back to AFR's
 * held-image behaviour rather than showing whatever `dst` happens to contain. */
/* `prevReal` is the last REAL render of the eye being synthesised - the one
   `dst` is about to become - or null if none has been recorded yet. It is what
   disocclusions are filled from: the source eye physically cannot contain the
   geometry a disocclusion reveals, but the other eye's own render can, and does.
   One frame stale, which is the same trade AFR makes for whole eyes. */
bool afw_warp_eye(ID3D11Device* device, ID3D11DeviceContext* context,
                  ID3D11Texture2D* srcColor, int srcEye, ID3D11Texture2D* dst,
                  ID3D11Texture2D* prevReal);

/* How to move the previous-frame disocclusion fill so it lands where it belongs.
 *
   The fill reads the destination eye's own last REAL render, which is the only
   place the geometry behind a near edge actually exists. It was read at the same
   screen coordinate, which is right only if the head has not moved since - and a
   head is never still. Rotation is the term that hurts: at this focal length half
   a degree between one frame and the next displaces the whole image by about
   thirteen pixels, uniformly and regardless of depth, so a real edge gets pasted
   thirteen pixels from where it belongs and reads as a doubled edge. Half a
   degree a frame is idle micro-motion, which is why the doubling was visible
   while "not moving".

   `rot` is row-major 3x3, mapping a direction in THIS frame's destination view
   into the view the previous real render was drawn from. The tangents are that
   eye's frustum, OpenXR signs (left and down negative); the pixel intrinsics are
   derived here, where the destination size is known.

   Translation is deliberately not corrected. It needs the depth of the revealed
   background, which is by definition absent from the depth we have, and its
   magnitude over one frame is a fraction of the rotation term. */
void afw_set_prev_reproject(const float rot[9], float tanLeft, float tanRight,
                            float tanUp, float tanDown, bool valid);

/* Measures how far out the warp actually landed, separately for each direction.
 *
   `predicted` is the frame in which this eye was SYNTHESISED; `real` is the
   engine's own render of the same eye, which arrives a frame later when the eyes
   swap. A faithful warp leaves the two aligned, so the residual shift between
   them should be about zero - and a direction that is systematically wrong
   leaves a residual whose size and sign say by how much and which way.

   Exists because the geometry is now measured correct end to end, so what is
   left is whether the synthesis carries it out faithfully - and that can differ
   between the two directions, since only one of them is ever exercised while the
   source eye is pinned.

   Costs two full-frame staging copies and a search, so it is budgeted to a few
   samples per eye and then stops. */
void afw_check_synthesis(ID3D11Device* device, ID3D11DeviceContext* context,
                         ID3D11Texture2D* predicted, ID3D11Texture2D* real,
                         int eye);

/* Once per second, so a run can be read without a headset. */
void afw_log_second();

void afw_release();

} // namespace bvr

#endif /* BVR_AFW_WARP_H */
