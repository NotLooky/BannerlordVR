/* =============================================================================
 * vp_patch - substituting the view-projection matrix at the point rgl uploads
 *            it to the GPU.
 *
 * WHY THIS EXISTS
 * ---------------
 * Every managed route into the engine's camera was tried and measured first:
 *
 *   MissionScreen.CustomCamera, new camera per frame ... geometry breaks
 *   MissionScreen.CustomCamera, one camera bound once .. sky only
 *   move CombatCamera in place ......................... geometry perfect,
 *                                                        engine overwrites us
 *   don't touch the camera ............................. geometry perfect,
 *                                                        camera is not ours
 *
 * The third route logged "Engine FOV hold: 450 correction(s) in the last ~5 s"
 * - 450 corrections in five seconds at 90 fps is every single frame. The engine
 * owns CombatCamera outright: pose and frustum both. Every route that renders
 * correctly renders from the engine's camera, and every route that injects ours
 * breaks the scene. There is no managed seam.
 *
 * So we stop fighting for the camera. The engine keeps its own camera, its own
 * visible sets, its own streaming and occlusion - everything that made route
 * four render perfectly - and we change only the matrix, in the microsecond
 * between rgl writing it into a mapped constant buffer and D3D handing that
 * buffer to the GPU. This is how VR injectors normally do this.
 *
 * WHAT MAKES IT SAFE
 * ------------------
 * The replacement is built so that no assumption about rgl's conventions can
 * be wrong, because none is made. See the derivation in vp_patch.cpp: the
 * substitution is VP' = D * VP * S, where D is a rigid world-space delta and S
 * touches clip x and y only. Depth range, handedness, reversed-Z, near and far
 * are all carried through untouched from the engine's own matrix, because the
 * engine's own matrix is the thing being multiplied.
 * ========================================================================== */
#ifndef BVR_VP_PATCH_H
#define BVR_VP_PATCH_H

#include <d3d11.h>
#include <stdint.h>

namespace bvr {

/* Published once per game tick by managed code, consumed on the render thread.
 *
 * Matrices are row-major, row-vector (v * M), and cameras are stored as
 * local->world with rows 0,1,2 = right, up, forward and row 3 = position.
 * The local-axis labelling only has to be CONSISTENT between the eye frame here
 * and the engine frame recovered from the intercepted matrix - it cancels out
 * of D exactly. It does not have to match rgl's. */
struct VpFrame
{
    float eyeCamera[16];

    /* The engine camera, as managed sees it. Used only to recognise which of
       the many matrices flowing through the constant buffers is the main view;
       never used to build the replacement, because Camera.Frame's basis is not
       the convention it looks like and the recovered matrix is authoritative. */
    float refPos[3];
    float refDir[3];

    /* The frustum this eye should end up rendering with. OpenXR sign
       convention: left and down negative. Tangents, not angles. */
    float tanLeft, tanRight, tanUp, tanDown;

    int32_t eye;     /* 0 or 1 - which eye the engine is drawing this frame */
    int32_t valid;   /* 0 stands the patcher down without unhooking         */
};

/* vp_patch = 1 in BannerlordVR.cfg. Read once. Means "rewrite matrices". */
bool vp_enabled();

/* vp_patch = 1 OR vp_measure = 1. Means "the scanner should be running": the
   hooks are installed and publications are accepted. Under vp_measure alone the
   scan runs and writes nothing, which is how the engine's true frustum gets
   measured for submitted_fov without the frame being drawn from two viewpoints.
   Anything deciding whether the machinery RUNS wants this; anything deciding
   whether matrices are REWRITTEN wants vp_enabled(). */
bool vp_active();

/* Installs the Map / Unmap / UpdateSubresource hooks on the engine's immediate
   context. Idempotent; safe to call every Present. */
bool vp_install_hooks(ID3D11DeviceContext* context);

void vp_publish(const VpFrame& frame);

/* The eye the engine is NOT drawing this frame. Kept in its own slot so the
   second-eye constant buffer is built from the same upload as the first. */
void vp_publish_other(const VpFrame& frame);

/* True when the second-eye duplicate exists and can be blitted. */
/* Whether draw-duplication stereo is the running mode. Off for every other
   mode, so AFR and AFW are untouched by this path existing. */
void vp_set_draw_stereo(bool active);

bool vp_second_eye_ready();

/* Scales the second-eye duplicate into dst - one fullscreen triangle, linear
   filtered. Raw scene colour: no tonemap, no upscaler, no post. Saves and
   restores the context state it touches. */
bool vp_blit_second_eye(ID3D11DeviceContext* ctx, ID3D11Texture2D* dst);

/* Per-Present bookkeeping: rolls the counters and emits the periodic summary. */
void vp_frame_boundary();

/* The frustum the engine was measured to be rendering with, recovered from its
   own matrix. This is what xrEndFrame must submit when we are not reshaping the
   projection, because submitting anything else is what makes the compositor
   stretch the image and the world come out the wrong size. Returns false until
   a main-view matrix has actually been seen. */
bool vp_measured_tangents(float* left, float* right, float* up, float* down);

/* True when the projection is being reshaped to the headset's own frustum, in
   which case the runtime's fov is the correct thing to submit. */
bool vp_expanding_fov();

/* THE UPSCALER'S SUB-PIXEL JITTER, IN NDC, FOR THE FRAME JUST INTERCEPTED.
 *
 * depth_upscale.h states that this "is not published by the engine and cannot
 * be recovered from the outside". The first half is true. The second is not,
 * and this is why: a temporal upscaler jitters by perturbing the PROJECTION,
 * and we hold the projection - it comes through the constant buffer every
 * frame, and `recover` already measures the frustum out of it. A jitter of j in
 * NDC is a frustum whose centre has moved by j, so it lands in the recovered
 * tangents as an asymmetry.
 *
 * What separates the jitter from a genuinely off-centre frustum is that the
 * jitter is ZERO-MEAN and the asymmetry is not: this reports the deviation of
 * the current frame's centre from a slow average of it. A camera that really
 * is off-centre averages into the baseline and reports nothing, and with the
 * upscaler off there is nothing to report in the first place - which is what
 * makes this safe to leave switched on.
 *
 * False until enough main-view matrices have been seen for the average to mean
 * anything. */
bool vp_view_jitter(float* ndcX, float* ndcY);

/* THE LATCH, AS A CLIP-SPACE HOMOGRAPHY, FOR THE PREVIOUS FRAME.
 *
   D is a rigid rotation applied to the world before the engine's projection:
   clip_latched = p * (D * VP_eng). Substituting p = clip_eng * inverse(VP_eng)
   gives clip_latched = clip_eng * (inverse(VP_eng) * D * VP_eng), so the mapping
   from the engine's screen to the one actually rendered is a 4x4 acting on clip
   coordinates - and because D is a rotation ABOUT THE CAMERA, that mapping is
   independent of depth. One matrix corrects every pixel.

   The PREVIOUS frame's is what a motion vector needs: the vectors point at where
   a surface was last frame, and last frame's image was latched by last frame's D.
   Returns false until two frames have been latched. */
bool vp_cur_latch_homography(float out16[16], float outInv16[16]);
bool vp_prev_latch_homography(float out16[16], float outInv16[16]);

/* For frame_map: the frames managed last published (the eye being drawn and the
   other one), and the engine camera the scan last recovered from a main view.
   Diagnostic reads - no ordering is promised against the render thread. */
bool vp_debug_published(VpFrame* frame, VpFrame* other);
bool vp_debug_engine_camera(float pos[3], float basis[9]);

} // namespace bvr

#endif /* BVR_VP_PATCH_H */
