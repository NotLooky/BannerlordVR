#include "vp_patch.h"
#include <d3dcompiler.h>
#include "bvr_config.h"
#include "bvr_log.h"
#include "d3d_hooks.h"
#include "frame_map.h"
#include "late_latch.h"
#include "stereo_dup.h"
#include "stereo_perf.h"
#include "ngx_hook.h"

#include <d3d11_1.h>

#include <MinHook.h>

#include <atomic>
#include <math.h>
#include <stdio.h>
#include <string.h>
#include <vector>

namespace bvr {
namespace {

/* =============================================================================
 * THE DERIVATION
 *
 * rgl uploads a world->clip matrix VP = V * P, where V is the engine camera's
 * world->view transform and P its projection. We want the engine to render from
 * OUR eye, with OUR frustum, and we want that WITHOUT knowing anything about P -
 * not its depth range, not its handedness, not whether it is reversed-Z.
 * Guessing any of those wrong is a black screen or a z-fighting mess, and there
 * is no way to guess them from outside the engine.
 *
 * Pose first. Write M for a camera's local->world matrix, so V = M^-1.
 *
 *     V_eye * P  =  M_eye^-1 * P
 *                =  M_eye^-1 * M_eng * (M_eng^-1 * P)
 *                =  (M_eye^-1 * M_eng) * VP
 *                =  D * VP
 *
 * D is built entirely from two rigid world-space frames. P never appears, so
 * every property of P survives the substitution untouched. And D does not depend
 * on which local axes we call right/up/forward: relabelling them is a fixed
 * orthogonal A applied to both frames, and
 *
 *     (A^-1 M_eye)^-1 * (A^-1 M_eng)  =  M_eye^-1 * A * A^-1 * M_eng
 *                                     =  M_eye^-1 * M_eng
 *
 * so A cancels exactly. The one thing that MUST hold is that both frames use the
 * SAME labelling - which is why the engine frame is recovered from the
 * intercepted matrix here rather than read from Camera.Frame in managed code,
 * whose basis is famously not the convention it appears to be.
 *
 * Frustum second. Changing the field of view is a scale and a shift of clip x
 * and y, post-multiplied:
 *
 *         | sx  0  0  0 |
 *     S = |  0 sy  0  0 |     clip' = clip * S
 *         |  0  0  1  0 |     clip'.x = clip.x*sx + clip.w*ox
 *         | ox oy  0  1 |     clip'.w = clip.w      (z and w untouched)
 *
 * NDC x is clip.x/clip.w, so this is exactly an affine remap of NDC x and y,
 * which is what a field-of-view change is. z and w are not touched, so once
 * again the depth convention is carried through rather than reconstructed.
 *
 * The full substitution is therefore
 *
 *     VP' = D * VP * S
 *
 * and the only unknowns left are the engine's own frustum tangents, which are
 * MEASURED off VP itself by extracting its frustum planes. Nothing is assumed.
 * ========================================================================== */

// ---------------------------------------------------------------------------
// small matrix / vector helpers - row-major, row-vector (v * M)
// ---------------------------------------------------------------------------

struct Vec3 { float x, y, z; };

inline Vec3  v3(float x, float y, float z) { return Vec3{ x, y, z }; }
inline float dot3(const Vec3& a, const Vec3& b) { return a.x*b.x + a.y*b.y + a.z*b.z; }
inline Vec3  sub3(const Vec3& a, const Vec3& b) { return v3(a.x-b.x, a.y-b.y, a.z-b.z); }
inline Vec3  mul3(const Vec3& a, float s) { return v3(a.x*s, a.y*s, a.z*s); }
inline float len3(const Vec3& a) { return sqrtf(dot3(a, a)); }

inline bool normalize3(Vec3& a)
{
    const float l = len3(a);
    if (!(l > 1e-8f))
        return false;
    a = mul3(a, 1.0f / l);
    return true;
}

struct Mat4 { float m[16]; };

inline float at(const Mat4& a, int r, int c) { return a.m[r * 4 + c]; }

void mat_mul(Mat4& out, const Mat4& a, const Mat4& b)
{
    Mat4 t;
    for (int r = 0; r < 4; ++r)
        for (int c = 0; c < 4; ++c)
            t.m[r*4+c] = a.m[r*4+0]*b.m[0*4+c] + a.m[r*4+1]*b.m[1*4+c]
                       + a.m[r*4+2]*b.m[2*4+c] + a.m[r*4+3]*b.m[3*4+c];
    out = t;
}

void mat_transpose(Mat4& out, const Mat4& a)
{
    Mat4 t;
    for (int r = 0; r < 4; ++r)
        for (int c = 0; c < 4; ++c)
            t.m[r*4+c] = a.m[c*4+r];
    out = t;
}

void mat_identity(Mat4& out)
{
    memset(out.m, 0, sizeof(out.m));
    out.m[0] = out.m[5] = out.m[10] = out.m[15] = 1.0f;
}

/* Inverse of a rigid local->world frame: rows 0-2 orthonormal, row 3 the
   position, column 3 = (0,0,0,1). Transpose the rotation, re-express the
   translation. Cheaper and far better conditioned than a general inverse. */
void mat_rigid_inverse(Mat4& out, const Mat4& a)
{
    Mat4 t;
    memset(t.m, 0, sizeof(t.m));

    for (int r = 0; r < 3; ++r)
        for (int c = 0; c < 3; ++c)
            t.m[r*4+c] = a.m[c*4+r];

    /* Row c of the inverse's translation is -dot(position, ROW c of the
       rotation) - the position re-expressed along each local axis. Reading down
       the columns instead is the classic slip here, and it produces a matrix
       that still looks orthonormal and still inverts cleanly while placing the
       camera somewhere else entirely. The self-test in tests/vp_math_test.cpp
       caught exactly that. */
    const float px = a.m[12], py = a.m[13], pz = a.m[14];
    for (int c = 0; c < 3; ++c)
        t.m[12+c] = -(px * a.m[c*4+0] + py * a.m[c*4+1] + pz * a.m[c*4+2]);

    t.m[15] = 1.0f;
    out = t;
}

/* General 4x4 inverse, for the clip->world matrices the deferred passes carry. */
bool mat_inverse(Mat4& out, const Mat4& a)
{
    const float* m = a.m;
    float inv[16];

    inv[0]  =  m[5]*m[10]*m[15] - m[5]*m[11]*m[14] - m[9]*m[6]*m[15]
             + m[9]*m[7]*m[14] + m[13]*m[6]*m[11] - m[13]*m[7]*m[10];
    inv[4]  = -m[4]*m[10]*m[15] + m[4]*m[11]*m[14] + m[8]*m[6]*m[15]
             - m[8]*m[7]*m[14] - m[12]*m[6]*m[11] + m[12]*m[7]*m[10];
    inv[8]  =  m[4]*m[9]*m[15] - m[4]*m[11]*m[13] - m[8]*m[5]*m[15]
             + m[8]*m[7]*m[13] + m[12]*m[5]*m[11] - m[12]*m[7]*m[9];
    inv[12] = -m[4]*m[9]*m[14] + m[4]*m[10]*m[13] + m[8]*m[5]*m[14]
             - m[8]*m[6]*m[13] - m[12]*m[5]*m[10] + m[12]*m[6]*m[9];
    inv[1]  = -m[1]*m[10]*m[15] + m[1]*m[11]*m[14] + m[9]*m[2]*m[15]
             - m[9]*m[3]*m[14] - m[13]*m[2]*m[11] + m[13]*m[3]*m[10];
    inv[5]  =  m[0]*m[10]*m[15] - m[0]*m[11]*m[14] - m[8]*m[2]*m[15]
             + m[8]*m[3]*m[14] + m[12]*m[2]*m[11] - m[12]*m[3]*m[10];
    inv[9]  = -m[0]*m[9]*m[15] + m[0]*m[11]*m[13] + m[8]*m[1]*m[15]
             - m[8]*m[3]*m[13] - m[12]*m[1]*m[11] + m[12]*m[3]*m[9];
    inv[13] =  m[0]*m[9]*m[14] - m[0]*m[10]*m[13] - m[8]*m[1]*m[14]
             + m[8]*m[2]*m[13] + m[12]*m[1]*m[10] - m[12]*m[2]*m[9];
    inv[2]  =  m[1]*m[6]*m[15] - m[1]*m[7]*m[14] - m[5]*m[2]*m[15]
             + m[5]*m[3]*m[14] + m[13]*m[2]*m[7] - m[13]*m[3]*m[6];
    inv[6]  = -m[0]*m[6]*m[15] + m[0]*m[7]*m[14] + m[4]*m[2]*m[15]
             - m[4]*m[3]*m[14] - m[12]*m[2]*m[7] + m[12]*m[3]*m[6];
    inv[10] =  m[0]*m[5]*m[15] - m[0]*m[7]*m[13] - m[4]*m[1]*m[15]
             + m[4]*m[3]*m[13] + m[12]*m[1]*m[7] - m[12]*m[3]*m[5];
    inv[14] = -m[0]*m[5]*m[14] + m[0]*m[6]*m[13] + m[4]*m[1]*m[14]
             - m[4]*m[2]*m[13] - m[12]*m[1]*m[6] + m[12]*m[2]*m[5];
    inv[3]  = -m[1]*m[6]*m[11] + m[1]*m[7]*m[10] + m[5]*m[2]*m[11]
             - m[5]*m[3]*m[10] - m[9]*m[2]*m[7] + m[9]*m[3]*m[6];
    inv[7]  =  m[0]*m[6]*m[11] - m[0]*m[7]*m[10] - m[4]*m[2]*m[11]
             + m[4]*m[3]*m[10] + m[8]*m[2]*m[7] - m[8]*m[3]*m[6];
    inv[11] = -m[0]*m[5]*m[11] + m[0]*m[7]*m[9] + m[4]*m[1]*m[11]
             - m[4]*m[3]*m[9] - m[8]*m[1]*m[7] + m[8]*m[3]*m[5];
    inv[15] =  m[0]*m[5]*m[10] - m[0]*m[6]*m[9] - m[4]*m[1]*m[10]
             + m[4]*m[2]*m[9] + m[8]*m[1]*m[6] - m[8]*m[2]*m[5];

    float det = m[0]*inv[0] + m[1]*inv[4] + m[2]*inv[8] + m[3]*inv[12];
    if (!isfinite(det) || !(fabsf(det) > 1e-20f))
        return false;

    det = 1.0f / det;
    for (int i = 0; i < 16; ++i)
        out.m[i] = inv[i] * det;
    return true;
}

/* Intersection of three planes, each (a,b,c,d) with inside = ax+by+cz+d >= 0.
   The side planes of a frustum all pass through its apex, so this IS the camera
   position. Cramer's rule; well conditioned for any real field of view. */
bool intersect3(const float p0[4], const float p1[4], const float p2[4], Vec3& out)
{
    const float a1=p0[0], b1=p0[1], c1=p0[2], r1=-p0[3];
    const float a2=p1[0], b2=p1[1], c2=p1[2], r2=-p1[3];
    const float a3=p2[0], b3=p2[1], c3=p2[2], r3=-p2[3];

    const float det = a1*(b2*c3 - b3*c2) - b1*(a2*c3 - a3*c2) + c1*(a2*b3 - a3*b2);
    if (!isfinite(det) || !(fabsf(det) > 1e-12f))
        return false;

    const float inv = 1.0f / det;
    out.x = (r1*(b2*c3 - b3*c2) - b1*(r2*c3 - r3*c2) + c1*(r2*b3 - r3*b2)) * inv;
    out.y = (a1*(r2*c3 - r3*c2) - r1*(a2*c3 - a3*c2) + c1*(a2*r3 - a3*r2)) * inv;
    out.z = (a1*(b2*r3 - b3*r2) - b1*(a2*r3 - a3*r2) + r1*(a2*b3 - a3*b2)) * inv;
    return isfinite(out.x) && isfinite(out.y) && isfinite(out.z);
}

// ---------------------------------------------------------------------------
// configuration
// ---------------------------------------------------------------------------

struct Settings
{
    bool  enabled      = false;

    /* MEASURE THE ENGINE'S FRUSTUM WITHOUT SUBSTITUTING ANYTHING.
     *
       vp_patch was buying two separate things through one switch, and only one
       of them was wanted.
     *
       The first is SUBSTITUTION: rewriting the main view's matrix in the
       constant buffer. Under mode = engine-camera that is redundant - the engine
       camera is already at the eye - and it is actively harmful, because the
       scanner recognises the main view and nothing else. Every pass whose matrix
       it does not recognise keeps the engine's, so the frame is drawn from two
       viewpoints at once and the unrecognised half - sky, sun, cloud, hair,
       particles, screen-space shadows - reads as glued to the head.
     *
       The second is MEASUREMENT: recovering the frustum the engine is ACTUALLY
       rendering with, from its own matrix, so submitted_fov can hand the
       compositor the same frustum the pixels were drawn with. Without it
       submitted_fov falls back to a computed frustum that does not match what
       rgl rendered, the compositor stretches the image to fit, and head motion
       comes out scaled - measured at a head-to-world gain of 0.617, which is
       what "the view resists my head" is. Nothing else in the process measures
       this; it can only be read off the matrix as it goes past.
     *
       Turning vp_patch off to stop the first took the second with it, which is
       why the split frame and the resistance traded places instead of one of
       them going away. So they are separate switches now. vp_measure installs
       the same hooks and runs the same scan, and writes nothing at all. */
    bool  measure      = false;

    bool  scan         = true;
    bool  fov          = true;    /* correct the field of view at all       */
    bool  expand       = false;   /* reshape the render to the headset fov, */
                                  /* rather than submitting the engine's    */
    bool  patchInverse = true;
    bool  updateSub    = true;

    /* Match view matrices against the previous frame as well as the current one.
     *
     * OFF, and the site census is why. The 1760-byte buffer pair - the only
     * place a genuine motion-vector matrix could live - was classified
     * "previous" ZERO times in a whole run:
     *
     *     cb 1760 offset 240   hits 812 (previous-frame 0)
     *     cb 1760 offset 304   hits 812 (previous-frame 0)
     *     cb  128 offset   0   hits 5158 (previous-frame 186)
     *
     * The only matrix it ever fired on was the 128-byte per-draw view matrix,
     * which is a SINGLE matrix and always the live one. Those 186 hits are 3.6%
     * of every draw in the frame rendered from the other eye - because under AFR
     * the previous frame IS the other eye - and 3.6% of a battle drawn from the
     * wrong eye is exactly the shaking and flickering. It only fires while the
     * camera is turning, which is when a live matrix can briefly fail the
     * direction test and pass it against the previous camera instead.
     *
     * So it costs a partial eye swap and buys nothing measurable. */
    bool  matchPrev    = false;
    /* vp_reproj_patch = 0 runs the whole of pass three and suppresses only the
       write. See the note at the write site. */
    bool  reprojPatch  = true;
    /* Now on by default and STRICT. The first working run recovered camera
       positions that matched the engine's to two decimal places - "d(ref) 0.00"
       on every site - which proves rgl renders in absolute world space and makes
       the position an exact, free discriminator. The old lenient form also
       accepted matrices whose camera sits at the origin, which is precisely what
       a translation-stripped skybox matrix looks like. */
    bool  requirePos   = true;
    float dirTol       = 0.990f;  /* cosine of the allowed direction error  */
    float posTol       = 6.0f;    /* metres                                 */

    /* THE SKYBOX EXCEPTION, WHICH IS NOT THE SAME AS DROPPING THE POSITION TEST.
     *
       The note above is right that a translation-stripped skybox matrix looks
       like a camera at the origin, and right that the lenient form let it in.
       What it did not say is that the skybox NEEDS to be let in: left
       unpatched, the world renders through our frustum and the sky through the
       engine's, so the sky slides as the head turns.
     *
       Turning requirePos off entirely was tried and was clearly worse - the sun,
       the clouds and the dust all started moving with the head, because plenty
       of passes happen to look the way the camera looks and direction alone does
       not distinguish them.
     *
       Sitting AT THE ORIGIN does. It is the skybox's actual signature rather
       than a property it merely shares, and a pass drawn at the player's
       position hundreds of metres away cannot satisfy it. So accept a matrix
       that matches on direction AND has its camera within skyRadius of the
       origin, and go on rejecting everything else the position test rejects.
     *
       build_replacement is safe on it: it places the eye by its OFFSET from the
       engine camera, so a skybox at the origin picks up the IPD and nothing
       else - centimetres, on geometry that is effectively at infinity. */
    bool  sky          = true;
    float skyRadius    = 5.0f;    /* metres from the world origin           */
};

Settings g_cfg;

/* EVERY INSTRUMENT ADDED DURING THE TESTs 63-78 INVESTIGATION, behind one
 * switch, off by default.
 *
 * These run on the PATCH path, which is hot: with vp_patch = 1 the lane census,
 * the pass census and the substitution-size accumulators would each fire around
 * twenty-five thousand times every five seconds. They answered their questions
 * and have no standing job, and TEST 78 established what the cost of leaving
 * them on really is - CPU spent here comes back as head-to-photon latency,
 * because the pose path runs at the frame rate.
 *
 * vp_census = 1 brings all of them back together, for the next investigation. */
/* Whether the motion-vector slot gets the previous frame of the SAME eye rather
   than simply the previous frame. See the note in vp_publish. Only AFR
   alternates, so this is a no-op under AFW and drawstereo. */
bool prev_same_eye()
{
    static bool cached = true;
    static bool have = false;
    if (!have) { have = true; cached = config_bool("vp_prev_same_eye", true); }
    return cached;
}

/* THE SAME QUESTION, ASKED OF PASS THREE - AND IT HAD BEEN ANSWERED THE OTHER
   WAY WITHOUT ANYONE NOTICING.
 *
   prev_same_eye above governs the main-view previous slot at offsets 240/304.
   The precomposed reprojection matrix at offset 752 is built from a SEPARATE
   history - g_reproj, staged per FRAME - and the note over ReprojPair argues
   explicitly for per-frame: "the engine's history buffer holds whatever was
   drawn last, so the matrix that reprojects it has to be built from whatever was
   drawn last."

   Both notes are reasonable and they contradict each other, so under AFR the
   engine was handed two different accounts of where the previous image was
   taken: the main-view slot said "this eye, two frames ago" and the
   reprojection matrix said "the other eye, one frame ago". They differ by the
   whole 65 mm stereo baseline, and a temporal upscaler reads both.

   Disagreement in metres is the wrong unit; on screen it is a disparity, which
   is why the artefact sorts geometry by DISTANCE:

       disparity = b * f / z,   b = 0.065 m,  f = (W/2) / tan(hfov/2)

   At 3400 px across a 123 deg frustum, f ~= 923 px, so the two accounts differ
   by ~60/z pixels: 0.3 px on a hillside at 200 m, ~1 px on terrain at 50 m,
   20 px on a character at 3 m, 40 px on reins at 1.5 m. That is the reported
   split exactly - "shimmering around only characters, not objects" - and it is
   why TEST 79's fix cleaned the world up and left the characters alone.

   So the two are tied together here rather than left independent, which is the
   same failure this file has already recorded twice for stereo_mode/afw. The
   default follows prev_same_eye; vp_reproj_same_eye overrides it explicitly for
   the A/B, because WHICH way they should agree is a measurement, not an
   argument - the upscaler wants one viewpoint, the engine's own history buffer
   genuinely does hold the other eye, and only a run can say which consumer
   costs more when it is the one told the lie. */
bool reproj_same_eye()
{
    static bool cached = true;
    static bool have = false;
    if (!have) { have = true; cached = config_bool("vp_reproj_same_eye", prev_same_eye()); }
    return cached;
}

/* The deadzone on the substitution. See the note in build_replacement.
   4 mm and 0.04 deg: comfortably above head-tracker noise and micro-tremor,
   and far below anything a deliberate head movement produces in one frame. */
/* OFF by default now. TEST 91 armed this at 4 mm against a translation that has
   since turned out not to be a translation; with D's position delta measured at
   0.0 mm the gate would swallow EVERY substitution and silently turn the patch
   off. It stays available because a deadzone on the ROTATION may yet be worth
   having, but it is not armed on a premise that has been withdrawn. */
float dead_m()
{
    static float cached = 0.0f;
    static bool  have = false;
    if (!have) { have = true; cached = config_float("vp_dead_m", 0.0f); }
    return cached;
}

float dead_deg()
{
    static float cached = 0.0f;
    static bool  have = false;
    if (!have) { have = true; cached = config_float("vp_dead_deg", 0.04f); }
    return cached;
}

float latch_gain()
{
    static float cached = 1.0f;
    static bool  have = false;
    if (!have)
    {
        have = true;
        cached = config_float("vp_latch", 1.0f);
        if (cached < 0.0f) cached = 0.0f;
        if (cached > 1.0f) cached = 1.0f;
    }
    return cached;
}

bool prev_apply_d()
{
    static bool cached = true;
    static bool have = false;
    if (!have) { have = true; cached = config_bool("vp_prev_apply_d", true); }
    return cached;
}

bool write_prev_slot()
{
    static bool cached = true;
    static bool have = false;
    if (!have) { have = true; cached = config_bool("vp_write_prev_slot", true); }
    return cached;
}

bool census_wanted()
{
    static bool cached = false;
    static bool have = false;
    if (!have) { have = true; cached = config_bool("vp_census", false); }
    return cached;
}

bool     g_cfgLoaded = false;

const Settings& cfg()
{
    if (!g_cfgLoaded)
    {
        g_cfgLoaded = true;
        g_cfg.enabled      = config_bool("vp_patch", false);
        g_cfg.measure      = config_bool("vp_measure", false);
        g_cfg.scan         = config_bool("vp_scan", true);
        g_cfg.fov          = config_bool("vp_fov", true);
        g_cfg.expand       = config_bool("vp_fov_expand", false);
        g_cfg.patchInverse = config_bool("vp_patch_inverse", true);
        g_cfg.updateSub    = config_bool("vp_update_subresource", true);
        g_cfg.matchPrev    = config_bool("vp_match_prev", false);
        g_cfg.reprojPatch  = config_bool("vp_reproj_patch", true);
        g_cfg.requirePos   = config_bool("vp_require_pos", true);
        g_cfg.dirTol       = config_float("vp_dir_tol", 0.990f);
        g_cfg.posTol       = config_float("vp_pos_tol", 2.0f);
        g_cfg.sky          = config_bool("vp_sky", true);
        g_cfg.skyRadius    = config_float("vp_sky_radius_m", 5.0f);

        if (g_cfg.enabled)
        {
            BVR_INFO("vp_patch = 1: substituting the view-projection matrix in the "
                     "constant buffers. fov %s, inverse %s.",
                     !g_cfg.fov ? "off"
                                : (g_cfg.expand ? "expand the render to the headset frustum"
                                                : "submit the engine's own frustum"),
                     g_cfg.patchInverse ? "on" : "off");
        }
        else if (g_cfg.measure)
        {
            BVR_INFO("vp_measure = 1: scanning the constant buffers to MEASURE the "
                     "frustum rgl is rendering with, and writing nothing. This is "
                     "what makes submitted_fov agree with the render; the head-to-"
                     "world gain is wrong without it. No matrix is substituted, so "
                     "every pass keeps the one camera mode = engine-camera gave it.");
        }
    }
    return g_cfg;
}

// ---------------------------------------------------------------------------
// what managed publishes, and how the render thread reads it
// ---------------------------------------------------------------------------

struct Published
{
    VpFrame  frame = {};

    /* The frame published immediately before this one.
     *
     * rgl's per-view constant buffer carries TWO main-view matrices 64 bytes
     * apart (offsets 240 and 304 of the 1760-byte buffer). The second is almost
     * certainly the PREVIOUS frame's view-projection, which is what a temporal
     * renderer uses to compute motion vectors for TAA and motion blur.
     *
     * Rewriting both with the CURRENT eye's matrix tells the engine that nothing
     * moved between the two frames, which is false in every frame and
     * catastrophically false under AFR, where consecutive frames are different
     * EYES. Zeroed motion vectors are how "my character and army are shaking"
     * happens while the terrain looks stable: static geometry reprojects fine,
     * anything the engine tracks temporally does not.
     *
     * So each matrix is matched against the current AND the previous camera, and
     * mapped to whichever eye it actually belongs to. */
    VpFrame  prev = {};
    int32_t  havePrev = 0;

    /* The eye the engine is NOT drawing this frame.
     *
     * Published alongside the current one so the second-eye constant buffer can
     * be built from the same upload, rather than from a frame-old pose. It
     * carries its own camera and its own frustum tangents, which on a canted
     * headset are mirrored rather than identical - using eye 0's tangents for
     * eye 1 would shear the second image by the difference. */
    VpFrame  other = {};

    uint64_t stampMs = 0;
};

Published             g_pub[2];
std::atomic<uint32_t> g_pubIndex{0};

/* A frame older than this means managed has stopped driving - leaving the
   mission, loading, or an exception in the tick. The patcher stands down rather
   than pinning the whole world to a stale head pose. */
constexpr uint64_t kStaleMs = 250;

bool read_published(Published& out)
{
    const uint32_t i = g_pubIndex.load(std::memory_order_acquire) & 1u;
    const Published& p = g_pub[i];

    if (p.frame.valid == 0)
        return false;

    const uint64_t now = GetTickCount64();
    if (now > p.stampMs && (now - p.stampMs) > kStaleMs)
        return false;

    out = p;
    return true;
}

// ---------------------------------------------------------------------------
// the camera recovered from an intercepted matrix
// ---------------------------------------------------------------------------

struct Frustum
{
    Vec3  right, up, forward;   /* world space, orthonormal, true directions */
    Vec3  position;             /* world space                               */
    float tanLeft, tanRight;    /* signed, OpenXR convention                 */
    float tanUp, tanDown;
};

/* Reads a candidate as a world->clip matrix and recovers the camera behind it.
 *
 * The w column of VP is the direction along which clip.w grows, and clip.w grows
 * FORWARD from the camera - so its direction is the camera's true forward, with
 * no sign to guess at, whatever handedness P uses. Likewise clip.x grows to the
 * right and clip.y grows upward, which pins the other two axes. That is the
 * whole recovery, and none of it touches z. */
bool recover(const Mat4& vp, Frustum& f)
{
    const Vec3 c3xyz = v3(at(vp,0,3), at(vp,1,3), at(vp,2,3));
    const float wLen = len3(c3xyz);

    /* An orthographic projection - every shadow cascade - has a zero w column,
       and a matrix that is not world->clip at all has an arbitrary one.
       Requiring unit length is what keeps shadow, UI and post passes out. */
    if (!isfinite(wLen) || fabsf(wLen - 1.0f) > 0.02f)
        return false;

    f.forward = mul3(c3xyz, 1.0f / wLen);

    const Vec3 c0xyz = v3(at(vp,0,0), at(vp,1,0), at(vp,2,0));
    const Vec3 c1xyz = v3(at(vp,0,1), at(vp,1,1), at(vp,2,1));

    f.right = sub3(c0xyz, mul3(f.forward, dot3(c0xyz, f.forward)));
    if (!normalize3(f.right))
        return false;

    f.up = sub3(c1xyz, mul3(f.forward, dot3(c1xyz, f.forward)));
    f.up = sub3(f.up, mul3(f.right, dot3(f.up, f.right)));
    if (!normalize3(f.up))
        return false;

    /* Gribb-Hartmann: the side planes are the w column plus or minus the x and
       y columns, and all four pass through the apex. */
    const float col0[4] = { at(vp,0,0), at(vp,1,0), at(vp,2,0), at(vp,3,0) };
    const float col1[4] = { at(vp,0,1), at(vp,1,1), at(vp,2,1), at(vp,3,1) };
    const float col3[4] = { at(vp,0,3), at(vp,1,3), at(vp,2,3), at(vp,3,3) };

    float planeL[4], planeR[4], planeT[4], planeB[4];
    for (int i = 0; i < 4; ++i)
    {
        planeL[i] = col3[i] + col0[i];
        planeR[i] = col3[i] - col0[i];
        planeT[i] = col3[i] - col1[i];
        planeB[i] = col3[i] + col1[i];
    }

    if (!intersect3(planeL, planeR, planeT, f.position))
        return false;

    /* Each side plane's normal is (distance along forward)*f +/- (1/tan)*s, so
       the half-angle tangent falls out of two dot products. Both scale with the
       normal's length, so the ratio needs no normalisation. */
    const Vec3 nL = v3(planeL[0], planeL[1], planeL[2]);
    const Vec3 nR = v3(planeR[0], planeR[1], planeR[2]);
    const Vec3 nT = v3(planeT[0], planeT[1], planeT[2]);
    const Vec3 nB = v3(planeB[0], planeB[1], planeB[2]);

    const float dL =  dot3(nL, f.right);
    const float dR = -dot3(nR, f.right);
    const float dT = -dot3(nT, f.up);
    const float dB =  dot3(nB, f.up);

    if (!(dL > 1e-6f) || !(dR > 1e-6f) || !(dT > 1e-6f) || !(dB > 1e-6f))
        return false;

    f.tanLeft  = -(dot3(nL, f.forward) / dL);
    f.tanRight =  (dot3(nR, f.forward) / dR);
    f.tanUp    =  (dot3(nT, f.forward) / dT);
    f.tanDown  = -(dot3(nB, f.forward) / dB);

    /* Roughly 6 to 175 degrees across. Anything outside that is a false
       positive dressed up as a camera. */
    const float t[4] = { -f.tanLeft, f.tanRight, f.tanUp, -f.tanDown };
    for (int i = 0; i < 4; ++i)
        if (!isfinite(t[i]) || t[i] < 0.05f || t[i] > 20.0f)
            return false;

    return true;
}

// ---------------------------------------------------------------------------
// building the replacement
// ---------------------------------------------------------------------------

// --- diagnostics ------------------------------------------------------------
//
// The first run reported "0 view + 0 inverse matrices replaced over 0 constant
// buffer uploads", which does not say WHERE the chain broke: the hook may never
// fire, or it may fire and be filtered out, or the filter may pass and the
// publication read fail. These separate those cases. They are plain increments
// on an already-serialised path and cost nothing worth measuring.

std::atomic<uint64_t> g_cMap{0};            /* Map calls seen at all          */
std::atomic<uint64_t> g_cMapBuffer{0};      /* ... on a buffer                */
std::atomic<uint64_t> g_cMapConstant{0};    /* ... on a constant buffer       */
std::atomic<uint64_t> g_cMapRecorded{0};    /* ... and recorded for patching  */
std::atomic<uint64_t> g_cUnmap{0};
std::atomic<uint64_t> g_cUnmapMatched{0};
std::atomic<uint64_t> g_cUpdate{0};
std::atomic<uint64_t> g_cUpdateConstant{0};
std::atomic<uint64_t> g_cNoPublication{0};  /* filter passed, nothing to use  */
std::atomic<uint64_t> g_cRecordFull{0};
std::atomic<uint32_t> g_biggestCb{0};

std::atomic<int32_t> g_statVp{0};
std::atomic<int32_t> g_statInv{0};
std::atomic<int32_t> g_statScanned{0};
std::atomic<int32_t> g_statPrev{0};

/* Publication cadence. published.prev is simply "whatever was published last",
   so the engine's motion vectors are only correct if that is exactly ONE
   rendered frame ago. Nothing in the call path guarantees it: vp_publish is
   driven from the managed AFR tick, and if that ticks less often than the
   engine renders, every motion vector is scaled up by the ratio. On screen that
   is temporally tracked content - dust, shadows, anything with a history buffer
   - overshooting head motion in the direction the world is already moving, then
   relaxing back once the head slows. Measured here rather than assumed. */
std::atomic<int32_t> g_statPub{0};
std::atomic<int32_t> g_statPubEyeFlip{0};
std::atomic<int64_t> g_pubLastQpc{0};
std::atomic<int64_t> g_pubGapSumUs{0};
std::atomic<int32_t> g_pubGapMaxUs{0};

std::atomic<float> g_measTanL{0.0f}, g_measTanR{0.0f}, g_measTanU{0.0f}, g_measTanD{0.0f};

/* Consecutive sightings agreeing with the stored frustum. See the note at the
   store site and measurement_settled. */
std::atomic<uint32_t> g_measAgree{0};
std::atomic<bool>     g_measSettledLogged{false};

/* HAS THE SCAN LEARNED EVERYTHING IT IS GOING TO?
 *
 * The scan exists to find and rewrite the main view. With vp_patch = 0 it writes
 * nothing, and the only thing it still produces is the measured frustum that
 * submitted_fov needs - a CONSTANT, re-derived from 76,000 constant-buffer
 * uploads a second. Measured: 381,308 uploads scanned per 5 s producing 0.0
 * view/frame, at 60 fps against a 90 Hz headset.
 *
 * That cost is not free of consequences either, and this is the part that
 * matters: the head-to-photon path runs at the frame rate, so CPU spent here
 * comes back as latency. "The worse the fps, the worse the latency or
 * resistance" is the same statement from the other end.
 *
 * So once the frustum has been confirmed enough times, stand the scan down and
 * keep serving the stored value. Writes are unaffected - if vp_patch is on, the
 * scan keeps running, because then it has a job beyond measuring.
 *
 * vp_measure_settle = 0 disables the stand-down and restores continuous
 * scanning. */
uint32_t measure_settle_hits()
{
    static uint32_t cached = 0;
    static bool     have = false;
    if (!have)
    {
        have = true;
        const float v = config_float("vp_measure_settle", 2000.0f);
        cached = (v > 0.0f) ? static_cast<uint32_t>(v) : 0u;
    }
    return cached;
}
std::atomic<bool>  g_measValid{false};

/* The upscaler's jitter, as the deviation of the frustum's centre from its own
   slow average. See the note over vp_view_jitter in the header for why that
   deviation IS the jitter. Written on the render thread only, in the one place
   the measured tangents are; read from wherever the warp runs. */
std::atomic<float> g_jitterX{0.0f}, g_jitterY{0.0f};
std::atomic<bool>  g_jitterValid{false};

float g_centreAvgX = 0.0f;
float g_centreAvgY = 0.0f;
int   g_centreSeen = 0;

/* ~1 s at 90 fps. Long against any jitter sequence - Halton repeats in tens of
   frames - and short enough to follow a frustum that genuinely changes, which
   is what an fov slider or a zoom does. */
const float kCentreAlpha = 0.01f;

/* Enough frames for the average to be an average. Below this the deviation is
   mostly the baseline still settling, and shifting a depth lookup by that would
   be worse than not shifting it at all. */
const int kCentreWarmup = 90;

int g_logBudget = 16;
int g_nearMissBudget = 8;

/* A skybox is drawn with the camera TRANSLATION STRIPPED - the matrix places the
   camera at the world origin, because a sky is infinitely far away and only its
   orientation matters. Such a matrix is not the main view and is correctly not
   patched, but it is the prime suspect for "the sky is black": everything else
   now renders from the eye, so if the sky still renders from the engine's
   orientation it lands somewhere else entirely.

   Nothing in the log has ever reported one, so they are being guessed at rather
   than known. This reports them and only them. */
int g_originBudget = 12;

/* The reprojection probe logs a handful of lines and stops. Its job is to name
   the engine's composition order once, not to narrate every frame. */
int g_reprojBudget = 40;

/* Log budget for secondary reprojection sites - a handful is enough to name
   them; after that they are substituted silently like the primary. */
int g_reprojAltBudget = 8;

/* This frame's latched candidate pair, engine and ours, cached so the buffers
   that carry no main-view matrix can still be corrected - see the block after
   pass three. Render thread only. */
Mat4 g_reprojEngCached = {};
Mat4 g_reprojOurCached = {};
std::atomic<bool> g_reprojCandCached{ false };

/* THE PREVIOUS FRAME'S PAIR, CARRIED ACROSS FRAMES RATHER THAN READ OUT OF THE
 * SAME BUFFER.
 *
   Pass three used to take its "previous" matrix from the second main-view block
   in the same upload - offset 304, on the note on Published::prev, which reasons
   that a temporal renderer keeps last frame's view-projection beside this one's.
 *
   The probe MEASURED that, and the answer is no. Every probe line in the log ends
   "Slots 240 vs 304 differ by 0.000000": the two blocks are bit-identical, every
   frame, at every camera. 304 is not the previous frame. It is the same matrix
   twice - the jittered/unjittered pair the note guessed at, unjittered here, or a
   plain duplicate.
 *
   That degeneracy silently destroyed the probe. With engPrev == engCur all four
   candidate products collapse to identity, so the "closest engine form" was being
   chosen among four identical matrices by float noise - which is exactly what the
   log shows, the answer flipping between inv(VPcur)*VPprev and VPprev*inv(VPcur)
   from one second to the next - and the reported max element error came out EQUAL
   to the block's drift from identity on every line, which is the signature of
   comparing against identity rather than against a real candidate. The note above
   pass three predicted this failure and asked for it to be measured rather than
   inferred; it was, and this is what the measurement asks for.
 *
   So the previous frame now comes from the previous FRAME. The render thread
   fills a staging slot as it patches the main view; the Present thread publishes
   it at the frame boundary, which is the only place in this process that knows
   where one rendered frame ends. Under AFR consecutive frames are different EYES,
   and that is correct rather than a complication: the engine's history buffer
   holds whatever was drawn last, so the matrix that reprojects it has to be built
   from whatever was drawn last. */
struct ReprojPair
{
    Mat4                  eng;        /* the engine's own VP, before we patched */
    Mat4                  our;        /* what we substituted for it             */
    int32_t               eye{-1};    /* which side drew it; -1 = never written */
    std::atomic<uint64_t> frame{0};   /* g_reprojFrame when written; 0 = never  */
};

ReprojPair            g_reproj[2];
std::atomic<uint32_t> g_reprojIndex{0};   /* names the COMPLETE slot */
std::atomic<uint64_t> g_reprojFrame{1};

/* THE SAME PAIR, KEPT PER EYE. See reproj_same_eye for why this exists at all.
 *
   Published from reproj_roll on the Present thread and read by pass three on the
   render thread, which is the same benign race the note over reproj_stage
   already accepts and for the same reason: the cost of losing it is one frame
   reprojected with a torn matrix, and a seqlock is not worth the hot-path cost.
   The frame stamp is the only thing that has to be honest, and it is stored last
   with release ordering so a pair is never read as newer than it is.

   Under AFR this eye's previous frame is TWO frames back, so the age gate that
   accepts a per-frame pair at <= 2 has to allow one more generation here. It is
   deliberately not opened wider than that: at three frames of slack a pair from
   before a load screen or a stall can still be refused, which is the whole point
   of stamping them. */
ReprojPair            g_reprojByEye[2];

/* Rebuilding the engine's reprojection matrix for our camera means knowing which
   of the four orderings it stores it in, and whether it is transposed. That is
   identified from the data - see pass three - and then LATCHED, so the hot path
   does one product rather than eight comparisons.
 *
   Identification is deliberately hard to satisfy. Near-identity is a weak
   signature and several blocks carry it; writing our matrix into the wrong one
   corrupts a pass we have not even identified. So a candidate must EXPLAIN the
   drift - bestErr well under the block's own deviation from identity, which is
   what rules out the degeneracy above - and it must do so at the same cb, offset,
   form and transposition kReprojAgree times running. Until then nothing is
   written and this is a probe, exactly as before. */
constexpr float kReprojMinDrift = 0.002f;  /* below this a block cannot discriminate */
constexpr float kReprojExplain  = 0.25f;   /* bestErr <= this * drift                */
constexpr int   kReprojAgree    = 8;

std::atomic<int32_t>  g_reprojForm{-1};       /* 0..3, -1 until identified   */
std::atomic<int32_t>  g_reprojCandidate{-1};  /* the form currently agreeing  */
std::atomic<int32_t>  g_reprojXpose{0};
std::atomic<uint32_t> g_reprojCb{0};
std::atomic<uint32_t> g_reprojOffset{0};
std::atomic<int32_t>  g_reprojAgree{0};
std::atomic<uint64_t> g_statReproj{0};
/* The size of the substitution D - see the note in build_replacement. Fixed
   point: millimetres and hundredths of a degree, so the accumulators can be
   plain atomics on a path that runs inside the driver's Unmap. */
std::atomic<uint64_t> g_dTransSum{0};
std::atomic<uint64_t> g_dDegSum{0};
std::atomic<uint64_t> g_dCount{0};
std::atomic<uint32_t> g_dTransMax{0};
std::atomic<uint32_t> g_dDegMax{0};

std::atomic<uint64_t> g_statReprojSkip{0};
std::atomic<uint64_t> g_statReprojStill{0};
/* Would have been written, but vp_reproj_patch = 0 held it back. */
std::atomic<uint64_t> g_statReprojHeld{0};

/* HOW BIG THE DISAGREEMENT ACTUALLY WAS, because this file has already paid once
   for arguing about the size of a delta instead of printing it.
 *
   g_reprojEyeDelta is the largest element difference between the two accounts of
   the previous camera - the per-eye pair and the per-frame one - as a fixed
   point in units of 1e-6. Zero means they agree and vp_reproj_same_eye changes
   nothing (no alternation: AFW, drawstereo, or a paused engine). A number that
   grows with the head's speed is the 65 mm baseline plus one frame of motion,
   which is the thing being removed.

   g_statReprojEyeAged counts frames where the SAME eye's pair was too old to
   use and pass three fell back to the per-frame one. It should be a trickle. If
   it is most of the frames, the age gate is starving the write and that is a
   regression, not a fix - the per-eye pair is two frames back and a mode that
   drops frames can push it past the gate. */
std::atomic<uint64_t> g_reprojEyeDelta{0};
std::atomic<uint64_t> g_statReprojEyeAged{0};

/* How often the deadzone swallowed a substitution. A large count with the head
   still is the instrument working; a large count while the head MOVES means the
   threshold is too wide and head tracking is being eaten. */
std::atomic<uint64_t> g_statDeadzone{0};
std::atomic<uint64_t> g_statPrevSkipped{0};
std::atomic<uint64_t> g_statPrevCarried{0};

/* The D applied to the LIVE view in this buffer, so the previous-frame slot can
   be moved into the same frame rather than replaced with a different pose.
   Thread-local: the scan runs on whichever thread is unmapping. */
/* The latch homography, this frame and last. Written on the render thread,
   read by the NGX hook on the same thread inside the upscaler's own call. */
float g_latchHomoCur[16] = {};
bool  g_latchHomoCurValid = false;
float g_latchHomoPrev[16] = {};
float g_latchHomoCurInv[16] = {};
float g_latchHomoPrevInv[16] = {};
bool  g_latchHomoPrevValid = false;

thread_local Mat4 t_latchD = {};
thread_local bool t_haveLatchD = false;

/* The SMALLEST D seen in the window, in millimetres. The mean and the max never
   answered the question a deadzone actually asks - how close does D get to zero
   when the head is doing nothing - and that is the number that says whether the
   engine camera is ever really where we put it. Starts at "no sample". */
constexpr uint32_t kNoMin = 0xFFFFFFFFu;
std::atomic<uint32_t> g_dTransMin{ kNoMin };

/* D split by which buffer it was written into: cb 1760 is the per-view buffer
   that carries the main view, everything else is not. See build_replacement. */
std::atomic<uint64_t> g_dPrimSum{0},  g_dPrimCount{0};
std::atomic<uint32_t> g_dPrimMax{0};
std::atomic<uint64_t> g_dOtherSum{0}, g_dOtherCount{0};
std::atomic<uint32_t> g_dOtherMax{0};

/* The previous-frame slot, kept apart from the live view. Its D is meant to be
   nonzero - see the note in build_replacement. */
std::atomic<uint64_t> g_dPrevSum{0}, g_dPrevCount{0};
std::atomic<uint32_t> g_dPrevMax{0};
std::atomic<uint64_t> g_dPrimDeg{0}, g_dPrevDeg{0}, g_dOtherDeg{0};   /* millidegrees */

/* Written and declined are counts; they say how often, never how far. The first
   run's "written 1, declined 441" could equally have been a still head, a layout
   change or a threshold set wrong, and the counts alone cannot separate those.
   These carry the actual drift and error of the last write and the last decline
   into the 5-second summary, which can. */
std::atomic<float> g_reprojLastDev{0.0f}, g_reprojLastErr{0.0f};
std::atomic<float> g_reprojBadDev{0.0f},  g_reprojBadErr{0.0f};

void reproj_sample(float dev, float err, bool wrote)
{
    if (wrote)
    {
        g_reprojLastDev.store(dev, std::memory_order_relaxed);
        g_reprojLastErr.store(err, std::memory_order_relaxed);
    }
    else
    {
        g_reprojBadDev.store(dev, std::memory_order_relaxed);
        g_reprojBadErr.store(err, std::memory_order_relaxed);
    }
}

const char* const kReprojNames[4] = { "inv(VPcur)*VPprev", "inv(VPprev)*VPcur",
                                      "VPprev*inv(VPcur)", "VPcur*inv(VPprev)" };

/* The four orderings, built once from a (current, previous) pair. The index
   matches g_reprojForm and kReprojNames. */
void reproj_candidates(const Mat4& cur, const Mat4& prev, const Mat4& invCur,
                       const Mat4& invPrev, Mat4 out[4])
{
    mat_mul(out[0], invCur,  prev);
    mat_mul(out[1], invPrev, cur);
    mat_mul(out[2], prev,    invCur);
    mat_mul(out[3], cur,     invPrev);
}

float mat_max_diff(const Mat4& a, const Mat4& b)
{
    float err = 0.0f;
    for (int k = 0; k < 16; ++k)
    {
        const float d = fabsf(a.m[k] - b.m[k]);
        if (d > err) err = d;
    }
    return err;
}

/* Render thread: fill the slot the frame boundary has NOT published. `frame` is
   written last and with release, so a reader that sees this frame number sees a
   complete pair rather than half of one.
 *
   The residual race is the same one read_published lives with: the boundary can
   flip the index between a reader's load and its copy, and the render thread
   then writes the slot that reader is walking. The window is a few hundred
   nanoseconds once per frame and the cost of losing it is ONE frame reprojected
   with a torn matrix, which is invisible next to the artefact this is here to
   remove. A seqlock would close it and is not worth the hot-path cost. */
void reproj_stage(const Mat4& eng, const Mat4& our, int32_t eye)
{
    const uint32_t staging = 1u - (g_reprojIndex.load(std::memory_order_relaxed) & 1u);
    ReprojPair& p = g_reproj[staging];
    p.eng = eng;
    p.our = our;

    /* Which side drew it, for the per-eye copy reproj_roll takes. It lives IN
       the pair rather than beside it so the frame stamp below publishes the eye
       and the matrices together - a stamp that could be seen before the eye it
       describes would file a pair under the OTHER eye, which is precisely the
       error this is here to remove. */
    p.eye = eye;

    p.frame.store(g_reprojFrame.load(std::memory_order_relaxed),
                  std::memory_order_release);
}

/* Present thread: publish what this frame staged, then open the next frame.
 *
   A frame that drew no main view leaves the previous pair standing instead of
   publishing a stale one - the frame counter moving on is what ages it out, and
   pass three refuses a pair more than two frames old. */
void reproj_roll()
{
    const uint32_t staging = 1u - (g_reprojIndex.load(std::memory_order_relaxed) & 1u);
    const uint64_t frame = g_reprojFrame.load(std::memory_order_relaxed);

    if (g_reproj[staging].frame.load(std::memory_order_acquire) == frame)
    {
        g_reprojIndex.store(staging, std::memory_order_release);

        /* File the same pair under the eye that drew it, so pass three can ask
           for "this side, last time" instead of only "last frame". A frame that
           staged nothing leaves both standing, exactly as the per-frame slot
           does - the frame counter moving on is what ages them out. */
        const int32_t eye = g_reproj[staging].eye;
        if (eye == 0 || eye == 1)
        {
            ReprojPair& e = g_reprojByEye[eye];
            e.eng = g_reproj[staging].eng;
            e.our = g_reproj[staging].our;
            e.eye = eye;
            e.frame.store(frame, std::memory_order_release);
        }
    }

    g_reprojFrame.store(frame + 1, std::memory_order_relaxed);
}

/* ONE BUFFER, EVERY VIEW MATRIX IN IT.
 *
   Slot meanings have been guessed at from aggregate tables for too long - a min
   distance here, a max dot there, each from a different moment - and the guesses
   have been wrong twice. This dumps every block of a single cb 1760 that decodes
   as a frustum, in one upload, against the same reference camera: offset,
   camera position, forward, field of view, and whether we patch it. The passes
   that follow the head - sky, sun, cloud, hair, effects, shadows - are drawn
   from slots we reject, and this says what those slots actually are.

   Budgeted in BUFFERS, not lines, so each dump is one coherent snapshot.

   RE-ARMED PERIODICALLY rather than spent at startup. The first run burned the
   whole budget during battle load - before the AFR hold textures and the warp
   shader were even created - and a loading camera says nothing about the pass
   that follows the head in gameplay. A few buffers every 5 s for the first
   minute covers the steady state. */
int g_anatomyBuffers = 3;
int g_anatomyRearms  = 12;

/* VP' = D * VP * S for one recovered main-view matrix. */
bool build_replacement(const Mat4& vp, const Frustum& f, const VpFrame& pub, Mat4& out,
                       bool primaryBuffer, bool prevSlot, Mat4* outD = nullptr)
{
    /* --- D: the rigid world-space delta ---------------------------------- */
    Mat4 eng;
    memset(eng.m, 0, sizeof(eng.m));
    eng.m[0]  = f.right.x;   eng.m[1]  = f.right.y;   eng.m[2]  = f.right.z;
    eng.m[4]  = f.up.x;      eng.m[5]  = f.up.y;      eng.m[6]  = f.up.z;
    eng.m[8]  = f.forward.x; eng.m[9]  = f.forward.y; eng.m[10] = f.forward.z;
    eng.m[12] = f.position.x; eng.m[13] = f.position.y; eng.m[14] = f.position.z;
    eng.m[15] = 1.0f;

    /* The eye is placed by its OFFSET from the engine camera, not by its
       absolute world position. Identical arithmetic when rgl renders in world
       space, and still correct if it renders camera-relative - a common trick at
       Bannerlord's map scale, which would otherwise drop every eye thousands of
       metres away from the geometry. */
    Mat4 eye;
    memcpy(eye.m, pub.eyeCamera, sizeof(eye.m));
    eye.m[12] = f.position.x + (pub.eyeCamera[12] - pub.refPos[0]);
    eye.m[13] = f.position.y + (pub.eyeCamera[13] - pub.refPos[1]);
    eye.m[14] = f.position.z + (pub.eyeCamera[14] - pub.refPos[2]);
    eye.m[3] = eye.m[7] = eye.m[11] = 0.0f;
    eye.m[15] = 1.0f;

    Mat4 eyeInv, d;
    mat_rigid_inverse(eyeInv, eye);
    mat_mul(d, eyeInv, eng);

    /* HOW BIG IS THE SUBSTITUTION, ACTUALLY.
     *
     * This number has been argued about all session and never measured. D is the
     * rigid move from the camera the engine drew with to the eye we want, and it
     * is EXACTLY the amount by which the scene ends up displaced relative to the
     * shadow map - because the shadow map was rendered before this ran and knows
     * nothing about it.
     *
     * So D is the slide. If it is a couple of centimetres and a fraction of a
     * degree, it is the residual it was designed to be and the shadows cannot be
     * sliding because of it. If it is tens of centimetres or tens of degrees,
     * it is a whole head pose and the shadows are sliding by precisely that.
     *
     * Reported as max and mean over the window, because a mean near zero with a
     * large max is a transient and a large mean is the steady state. */
    /* Measured unconditionally now, because the deadzone below needs the same
       two numbers the census reports and they cost a sqrt and an acos. */
    /* THE TRANSLATION ROW OF D IS NOT A DISPLACEMENT, AND READING IT AS ONE
       COST TESTS 84 THROUGH 97.
     *
       D = inverse(eye) * eng. In row-vector form its translation row is
     *
           d.t = -t_eye * R_eye^T * R_eng + t_eng
     *
       and when the two positions are equal - which managed now MEASURES at
       0.0 mm mean and 0.0 mm worst, 450 of 450 publishes - that collapses to
     *
           d.t = t * (I - R_eye^T * R_eng)
     *
       which is not zero. It is the WORLD POSITION multiplied by the rotation
       mismatch. Bannerlord's battle origin sits about 1090 m away, so an angular
       error of a hundredth of a degree prints as 227 mm and one degree prints as
       19 m. Every "D is metres" reading in this file is that product, reported in
       metres and called a slide.
     *
       It also explains the 2:1 between the live and previous slots: both are the
       same |t| times their own rotation error, and the previous slot's is one
       frame of head rotation while the live slot's is two.
     *
       So the honest translation is the one the two poses actually disagree by,
       differenced directly. The rotation below is measured from D's 3x3 and was
       always valid; it is the number that matters, and it is now reported beside
       a translation that means what its name says. */
    const float tx = pub.eyeCamera[12] - pub.refPos[0];
    const float ty = pub.eyeCamera[13] - pub.refPos[1];
    const float tz = pub.eyeCamera[14] - pub.refPos[2];
    const float tr = sqrtf(tx * tx + ty * ty + tz * tz);

    /* Rotation angle of the 3x3, from its trace. */
    float dc = (d.m[0] + d.m[5] + d.m[10] - 1.0f) * 0.5f;
    if (dc > 1.0f) dc = 1.0f;
    if (dc < -1.0f) dc = -1.0f;
    const float degs = acosf(dc) * 57.29578f;

    /* THE DEADZONE, AND WHY IT GOES ON D RATHER THAN ON THE HEAD POSE.
     *
     * Standing still, a character's shake is not driven by anything moving. It
     * is driven by D CHANGING. The image is displaced by D; a dynamic entity's
     * motion vectors are composed by the engine from its own camera and know
     * nothing about D. So the upscaler reprojects those entities wrong by
     * exactly the frame-to-frame change in D, and with the head held still that
     * change is head-tracker noise and micro-tremor - millimetres, but present
     * every frame and alternating in sign, which is what shaking looks like.
     *
     * Filtering the head POSE would work and would be the wrong place: it adds
     * latency to real movement, and the late latch exists to remove latency.
     * The deadzone belongs on the CORRECTION. When D is smaller than the
     * threshold the engine's camera is already where we want it to within a few
     * millimetres, so the matrix is left alone - the substitution is skipped
     * entirely, D stops changing frame to frame, and the entities the upscaler
     * mis-reprojects stop being mis-reprojected. When the head genuinely moves,
     * D crosses the threshold immediately and the latch applies in full, so
     * nothing is taken away from head tracking.
     *
     * The discontinuity at the threshold is itself only the size of the
     * threshold - a few millimetres and a few hundredths of a degree - which is
     * below what a headset can resolve. That is what makes a hard gate
     * acceptable here rather than needing a ramp.
     *
     * vp_dead_m = 0 disables it and restores the previous behaviour. */
    /* D IS NOW MEASURED ON EVERY SUBSTITUTION, NOT BEHIND vp_census.
     *
     * It was gated for CPU (TEST 78) and the gate cost more than it saved: D is
     * the quantity every symptom in this file turns on, and it was argued about
     * across TESTs 53-91 while being printable at any moment. A handful of
     * atomics on 65 substitutions a frame is not measurable next to the matrix
     * work already here. The MINIMUM matters as much as the mean - it says how
     * close D ever gets to zero, which is what a deadzone needs to know and
     * what no previous instrument reported. */
    if (isfinite(tr) && isfinite(degs))
    {
        const uint32_t curT = static_cast<uint32_t>(tr * 1000.0f);

        uint32_t prevMin = g_dTransMin.load(std::memory_order_relaxed);
        while (curT < prevMin &&
               !g_dTransMin.compare_exchange_weak(prevMin, curT,
                                                  std::memory_order_relaxed)) {}

        /* SPLIT BY BUFFER, because one average over both was meaningless.
         *
         * The last run read "min 2 mm, mean 447 mm" in the same window. Those
         * cannot be one population. Roughly 60 matrices a frame are being
         * substituted and only TWO of them are the main view - so the mean is
         * dominated by whatever the other fifty-eight are, and the min was
         * probably the main view all along.
         *
         * cb 1760 is the per-view buffer; everything else is something else,
         * and cb 128 alone was patched five times as often as the main view.
         * Averaging them together hid exactly the thing worth seeing. */
        /* AND SPLIT THE MAIN VIEW AGAIN, BY WHICH SLOT.
         *
         * Exactly half of the main view's substitutions are the PREVIOUS-frame
         * matrix at offset 304, and that one is handed published.prev - a pose
         * deliberately one frame old. Its D is therefore SUPPOSED to be large:
         * it is one frame of head motion plus, under AFR, the eye offset. Only
         * offset 240 carries the live view, and only its D is a fault when it is
         * nonzero.
         *
         * Averaging the two gave "MAIN VIEW mean 195-254 mm" - a correct value
         * and an intentionally stale one, mixed. That is the third time in this
         * investigation that a mean has been reported over two populations, and
         * it is the reason the reorder in TEST 95 looked like it barely helped. */
        if (primaryBuffer && !prevSlot)
        {
            g_dPrimSum.fetch_add(curT, std::memory_order_relaxed);

            g_dPrimDeg.fetch_add(static_cast<uint64_t>(degs * 1000.0f), std::memory_order_relaxed);
            g_dPrimCount.fetch_add(1, std::memory_order_relaxed);
            uint32_t pm = g_dPrimMax.load(std::memory_order_relaxed);
            while (curT > pm &&
                   !g_dPrimMax.compare_exchange_weak(pm, curT,
                                                     std::memory_order_relaxed)) {}
        }
        else if (primaryBuffer)
        {
            g_dPrevSum.fetch_add(curT, std::memory_order_relaxed);

            g_dPrevDeg.fetch_add(static_cast<uint64_t>(degs * 1000.0f), std::memory_order_relaxed);
            g_dPrevCount.fetch_add(1, std::memory_order_relaxed);
            uint32_t vm = g_dPrevMax.load(std::memory_order_relaxed);
            while (curT > vm &&
                   !g_dPrevMax.compare_exchange_weak(vm, curT,
                                                     std::memory_order_relaxed)) {}
        }
        else
        {
            g_dOtherSum.fetch_add(curT, std::memory_order_relaxed);

            g_dOtherDeg.fetch_add(static_cast<uint64_t>(degs * 1000.0f), std::memory_order_relaxed);
            g_dOtherCount.fetch_add(1, std::memory_order_relaxed);
            uint32_t om = g_dOtherMax.load(std::memory_order_relaxed);
            while (curT > om &&
                   !g_dOtherMax.compare_exchange_weak(om, curT,
                                                      std::memory_order_relaxed)) {}
        }

        const float deadM   = dead_m();
        const float deadDeg = dead_deg();

        if (deadM > 0.0f && tr < deadM && degs < deadDeg)
        {
            g_statDeadzone.fetch_add(1, std::memory_order_relaxed);
            return false;      /* leave the engine's own matrix standing */
        }
    }

    {
        if (isfinite(tr) && isfinite(degs))
        {
            g_dTransSum.fetch_add(static_cast<uint64_t>(tr * 1000.0f),
                                  std::memory_order_relaxed);
            g_dDegSum.fetch_add(static_cast<uint64_t>(degs * 100.0f),
                                std::memory_order_relaxed);
            g_dCount.fetch_add(1, std::memory_order_relaxed);

            uint32_t prevT = g_dTransMax.load(std::memory_order_relaxed);
            const uint32_t curT = static_cast<uint32_t>(tr * 1000.0f);
            while (curT > prevT &&
                   !g_dTransMax.compare_exchange_weak(prevT, curT,
                                                      std::memory_order_relaxed)) {}

            uint32_t prevD = g_dDegMax.load(std::memory_order_relaxed);
            const uint32_t curD = static_cast<uint32_t>(degs * 100.0f);
            while (curD > prevD &&
                   !g_dDegMax.compare_exchange_weak(prevD, curD,
                                                    std::memory_order_relaxed)) {}
        }
    }

    /* HOW MUCH OF THE LATCH TO APPLY. See TEST 101.
     *
     * D is now measured as 0.0 mm and a quarter to one degree of ROTATION - the
     * head turn between the camera being set and this matrix being written. That
     * rotation is the whole value of vp_patch: it is why the head feels crisp.
     *
     * It is also, unavoidably, a motion-vector error. Dynamic entities get their
     * velocities from per-object matrices the engine composes against ITS camera,
     * which never saw this rotation, so the image is rotated by an amount their
     * motion vectors do not describe. Write the previous-view slot as well and
     * static geometry cancels it while dynamic entities do not - that is the
     * character shake. Leave the slot alone and everything carries the error
     * equally - that is the uniform smear, which is where this now stands.
     *
     * Both endpoints are one quantity at full strength, so the useful control is
     * its STRENGTH rather than another slot. Scaling D's rotation toward identity
     * scales the crispness and the smear together, and lets the trade be dialled
     * instead of chosen:
     *
     *   vp_latch = 1.0   full late latch - crispest head, most smear
     *   vp_latch = 0.0   no substitution at all - no smear, floats
     *
     * The rotation is well under a degree, so a small-angle Rodrigues rebuild is
     * exact enough and costs a sqrt and two multiplies. */
    const float latch = latch_gain();
    if (latch < 0.999f)
    {
        /* SCALED WITHOUT AN AXIS, BECAUSE THE AXIS IS WHERE THE SIGN ERRORS LIVE.
         *
         * TEST 101 extracted an axis and rebuilt with Rodrigues. TEST 102 then
         * "corrected" the extraction's signs - and the correction was itself
         * wrong, which is what turned vp_latch = 0.5 into half the rotation
         * applied BACKWARDS. On screen that is a view that both lags the head and
         * under-rotates: "delayed and has resistance", exactly as reported.
         *
         * Checked properly this time, against the rebuild's own storage. For a
         * rotation about z the rebuild writes m[1] = +s and m[4] = -s, so
         * (m6-m9, m8-m2, m1-m4) gives +2s on z and (m9-m6, ...) gives -2s. The
         * ORIGINAL extraction was right and the fix negated it.
         *
         * Rather than flip the signs back and leave a convention trap in the
         * file, the scaling is now done in a form that has no axis and no
         * convention at all:
         *
         *     R(a) = orthonormalise( I + a * (R - I) )
         *
         * a = 1 returns R exactly, a = 0 returns I exactly, and there is no
         * handedness to get backwards. The first-order error is O(theta^2), and
         * theta here is under one degree - about three parts in a hundred
         * thousand of a radian, which is far below anything a headset resolves.
         *
         * The translation is still rebuilt as c*(I - R(a)) so the rotation keeps
         * pivoting on the camera; that part of TEST 102 was correct and is why
         * the sky no longer tears. */
        float r[9] = {
            d.m[0], d.m[1], d.m[2],
            d.m[4], d.m[5], d.m[6],
            d.m[8], d.m[9], d.m[10]
        };

        /* I + a*(R - I), elementwise. */
        for (int i = 0; i < 9; ++i)
        {
            const float ident = (i == 0 || i == 4 || i == 8) ? 1.0f : 0.0f;
            r[i] = ident + latch * (r[i] - ident);
        }

        /* Gram-Schmidt the three rows back to an orthonormal basis. A linear
           blend of two rotations is not quite a rotation; at this angle it is
           within a rounding of one, but a matrix that is not orthonormal would
           put a shear into every view matrix in the frame. */
        auto norm3 = [](float* v) {
            const float n = sqrtf(v[0]*v[0] + v[1]*v[1] + v[2]*v[2]);
            if (n > 1e-8f) { v[0] /= n; v[1] /= n; v[2] /= n; }
        };
        norm3(&r[0]);
        const float d01 = r[0]*r[3] + r[1]*r[4] + r[2]*r[5];
        r[3] -= d01*r[0]; r[4] -= d01*r[1]; r[5] -= d01*r[2];
        norm3(&r[3]);
        r[6] = r[1]*r[5] - r[2]*r[4];
        r[7] = r[2]*r[3] - r[0]*r[5];
        r[8] = r[0]*r[4] - r[1]*r[3];

        d.m[0] = r[0]; d.m[1] = r[1]; d.m[2]  = r[2];
        d.m[4] = r[3]; d.m[5] = r[4]; d.m[6]  = r[5];
        d.m[8] = r[6]; d.m[9] = r[7]; d.m[10] = r[8];

        const float cx = f.position.x, cy = f.position.y, cz = f.position.z;
        d.m[12] = cx - (cx*d.m[0] + cy*d.m[4] + cz*d.m[8]);
        d.m[13] = cy - (cx*d.m[1] + cy*d.m[5] + cz*d.m[9]);
        d.m[14] = cz - (cx*d.m[2] + cy*d.m[6] + cz*d.m[10]);
    }

    /* D as pass four will use it - AFTER the latch above has scaled it.

       Capturing it at the mat_mul would hand back the UNLATCHED rotation, and
       the direction lanes would then be rotated by more than pass one rotated
       the world. The two have to move by the same D, or the sky is merely
       wrong in a new direction - which is the failure this whole exercise is
       about. */
    if (outD)
        *outD = d;

    /* THE LATCH AS A CLIP-SPACE HOMOGRAPHY, for the NGX motion-vector correction.
       M = inverse(VP_eng) * D * VP_eng, computed on the live view only and once
       per frame - see vp_prev_latch_homography. Depth-independent because D is a
       rotation about the camera. */
    if (!prevSlot && primaryBuffer)
    {
        Mat4 invVp, t1, m;
        if (mat_inverse(invVp, vp))
        {
            mat_mul(t1, invVp, d);
            mat_mul(m, t1, vp);
            bool okM = true;
            for (int i = 0; i < 16; ++i)
                if (!isfinite(m.m[i])) { okM = false; break; }
            if (okM)
            {
                memcpy(g_latchHomoCur, m.m, sizeof(m.m));
                /* The inverse comes free: M = inv(VP)*D*VP, so M^-1 =
                   inv(VP)*inverse(D)*VP, and D is rigid. Cheaper and more
                   accurate than inverting the product. */
                Mat4 dInv, t2, mInv;
                mat_rigid_inverse(dInv, d);
                mat_mul(t2, invVp, dInv);
                mat_mul(mInv, t2, vp);
                memcpy(g_latchHomoCurInv, mInv.m, sizeof(mInv.m));
                g_latchHomoCurValid = true;
            }
        }
    }

    /* Remember this frame's D so the previous-frame slot can be carried into the
       same frame instead of being replaced with a different pose. Only the LIVE
       view defines it; the prev slot must not overwrite it with its own. */
    if (!prevSlot)
    {
        t_latchD = d;
        t_haveLatchD = true;
    }

    mat_mul(out, d, vp);

    /* --- S: the clip-space reshape --------------------------------------- */
    if (!cfg().fov || !cfg().expand)
        return true;   /* engine frustum kept; xrEndFrame submits it instead */

    const float wantW = pub.tanRight - pub.tanLeft;
    const float wantH = pub.tanUp - pub.tanDown;
    if (!(wantW > 1e-4f) || !(wantH > 1e-4f))
        return true;

    const float haveW = f.tanRight - f.tanLeft;
    const float haveH = f.tanUp - f.tanDown;

    Mat4 s;
    mat_identity(s);
    s.m[0]  = haveW / wantW;
    s.m[5]  = haveH / wantH;
    s.m[12] = ((f.tanRight + f.tanLeft) - (pub.tanRight + pub.tanLeft)) / wantW;
    s.m[13] = ((f.tanUp + f.tanDown) - (pub.tanUp + pub.tanDown)) / wantH;

    mat_mul(out, out, s);
    return true;
}

// ---------------------------------------------------------------------------
// scanning a constant buffer
// ---------------------------------------------------------------------------

bool finite16(const float* v)
{
    for (int i = 0; i < 16; ++i)
        if (!isfinite(v[i]))
            return false;
    return true;
}

/* Is c the inverse of vp? One two-sided test is enough for square matrices, and
   the first element rejects nearly everything for four flops. */
bool is_inverse_of(const Mat4& c, const Mat4& vp)
{
    const float p00 = c.m[0]*vp.m[0] + c.m[1]*vp.m[4] + c.m[2]*vp.m[8] + c.m[3]*vp.m[12];
    if (!isfinite(p00) || fabsf(p00 - 1.0f) > 0.02f)
        return false;

    Mat4 p;
    mat_mul(p, c, vp);
    for (int r = 0; r < 4; ++r)
        for (int col = 0; col < 4; ++col)
        {
            const float want = (r == col) ? 1.0f : 0.0f;
            if (!isfinite(p.m[r*4+col]) || fabsf(p.m[r*4+col] - want) > 0.02f)
                return false;
        }
    return true;
}

/* Distinct patch sites, so the shape of what we are rewriting is on the record
   rather than inferred from a handful of sampled log lines. */
struct SiteRow { uint32_t bytes; uint32_t offset; uint32_t transposed; uint64_t hits; uint64_t prevHits; };
constexpr int    kSiteRows = 16;
SiteRow          g_sites[kSiteRows] = {};
std::atomic<int> g_siteCount{0};

/* WHAT WE ARE NOT PATCHING.
 *
   The patched-site table only records successes, so a view matrix we reject
   leaves no trace at all - and a whole class of passes reading an unpatched
   matrix looks exactly like a correctly patched frame that simply misbehaves.
   Two censuses close that blind spot.

   REJECTED: a block that is shaped like a world->clip matrix but failed the
   direction or position predicate. If a previous-frame view matrix for the
   effects passes lives in a buffer we throw away, it shows up here.

   NEAR-IDENTITY: engines that cannot use per-pixel velocity - skybox, sun,
   clouds, hair, particles, screen-space shadows, everything with no motion
   vectors of its own - reproject the history with a single PRECOMPOSED matrix,
   inverseVP_current * VP_previous. That product has no camera position and no
   forward vector, so every predicate we own rejects it and we never touch it.
   It keeps compensating for the flat camera while ignoring the head, which is
   what "it tries not to move with my head, but follows when I move fast" is.
   Such a matrix sits NEAR IDENTITY and drifts by an amount proportional to how
   far the camera moved between the two frames, so that is what we look for. */
struct ProbeRow { uint32_t bytes; uint32_t offset; uint32_t transposed; uint64_t hits;
                  float bestDot; float minPos; float maxDev; };
constexpr int    kProbeRows = 24;
ProbeRow         g_rejects[kProbeRows] = {};
std::atomic<int> g_rejectCount{0};
ProbeRow         g_nearId[kProbeRows] = {};

/* Per-site motion, for the anatomy line: where this block's own camera was
   the last time the line printed, and where OUR eye was at the same moment.
   The ratio of the two says whether a pass is riding the head. */
struct SiteMotion {
    bool     used;
    uint32_t bytes;
    uint32_t offset;
    float    px, py, pz;
    float    ex, ey, ez;
};
SiteMotion g_siteMotion[kSiteRows] = {};

/* Orthographic blocks - the shadow cascades - and how far each one's
   translation travels between sightings. Identified only, never patched. */
struct OrthoMotion {
    bool     used;
    uint32_t bytes;
    uint32_t offset;
    uint64_t hits;
    float    tx, ty, tz;
    float    maxMoved;
};
OrthoMotion g_ortho[kProbeRows] = {};
std::atomic<int> g_nearIdCount{0};

/* =============================================================================
 * THE PER-CASCADE OFFSET, WHICH IS NOT IN ANY MATRIX
 * =============================================================================
 *
 * TEST 62 measured the orthographic blocks in cb 6656 as PERFECTLY static while
 * the head moved 12 cm, and stopped there because of what it found next: those
 * blocks carry rotation and scale only, translation (0,0,0). That is not a
 * cascade that ignores the camera. It is a cascade whose ORIGIN is factored out
 * of the matrix and packed as a separate float3 beside it, which is the ordinary
 * way to store a cascade set - one shared light rotation, one scale per cascade,
 * one offset per cascade - and a scan that looks for 4x4 matrices cannot see an
 * offset whatever signature it is given. The journal called that the boundary of
 * constant-buffer scanning and sent the next attempt to RenderDoc.
 *
 * It is not the boundary. It is the boundary of looking for MATRICES. A float3
 * that tracks the camera is just as measurable as a matrix that does: difference
 * it between uploads and compare the step against the head's own step over the
 * same interval. That is the identical measurement the site census already makes
 * for view matrices, applied to a lane instead of a block.
 *
 * Whole-buffer rather than a table of rows, and that is deliberate: a 6656-byte
 * buffer is 416 lanes, and a 24-row table fills with the first 24 offsets in the
 * buffer - which are exactly the ones nothing has any reason to suspect. Every
 * lane is differenced; only the movers are reported.
 *
 * WHAT THE NUMBERS MEAN
 *
 *   ratio   lane travel / head travel over the same sightings. Near 1 is a lane
 *           riding the head. Near 0 is anchored to the world.
 *   align   |cos| between the lane's step and the head's step. Near 1 means the
 *           lane moves in the DIRECTION the head moved, which is what separates
 *           a cascade origin from a lane that merely changes a lot.
 *   step    the largest single step. A texel-snapped cascade origin does not
 *           move smoothly - it jumps - and this is the size of the jump, which
 *           is the world-space size of a shadow texel. That number is the whole
 *           diagnosis: a jump you can see is a shadow edge you can see move.
 */
/* ONE LATCHED BUFFER WAS THE WRONG SHAPE, AND IT COST A WHOLE TEST.
 *
 * The first version latched to the first orthographic carrier it saw and watched
 * only that for the life of the run. It picked cb 6656 - which is a bit-static
 * table - reported "no lane moved" eight times over, and that was read as
 * "nothing in any constant buffer follows the head".
 *
 * It was not. Two buffers it never looked at were right there in the same log:
 * cb 15360, and a largest-seen of 40960 bytes. The old cap of 16384 excluded the
 * second outright and the latch excluded the first, so the census answered a
 * question about cb 6656 and the answer was read as being about the renderer.
 *
 * The other half of that mistake was reading "largest constant buffer seen 1760"
 * off the idle census, which only prints while the patch has nothing published -
 * during the MENU. The mission's buffers are an order of magnitude larger and
 * none of them were in that number.
 *
 * So: several buffers at once, admitted on their own merits, up to the same
 * 65536 the scan itself caps at. A buffer earns a slot by carrying an
 * orthographic block OR by being large enough to be a per-view or per-pass
 * table rather than a per-object one. */
constexpr uint32_t kLaneMaxBytes = 65536;
constexpr uint32_t kLaneMaxLanes = kLaneMaxBytes / 16;
constexpr int      kLaneSlots    = 6;
constexpr uint32_t kLaneBigBytes = 4096;   /* large enough to be worth watching */

struct LaneAcc {
    float    travel;    /* summed |step| over sightings where the head moved */
    float    align;     /* summed |cos| against the head's own step */
    float    maxStep;   /* largest single step - the snap granularity */
    uint32_t samples;
};

struct LaneBuf {
    uint32_t bytes;       /* 0 means the slot is free */
    bool     primed;
    bool     hadOrtho;
    float    prevRef[3];
    float    headTravel;
    uint64_t lastFrame;   /* one sample per present - see the churn note */
    uint32_t samples;
};

LaneBuf g_laneBuf[kLaneSlots] = {};
float   g_lanePrev[kLaneSlots][kLaneMaxBytes / 4] = {};
LaneAcc g_laneAcc[kLaneSlots][kLaneMaxLanes] = {};

/* rgl uploads from more than one thread. The row-based censuses above accept the
   race because a torn row costs one sighting; a whole-buffer difference cannot,
   because two threads interleaving would difference each upload against the
   other's and report movement that never happened. */
std::atomic_flag g_laneLock = ATOMIC_FLAG_INIT;

struct LaneGuard {
    bool held;
    explicit LaneGuard()
    {
        held = !g_laneLock.test_and_set(std::memory_order_acquire);
    }
    ~LaneGuard()
    {
        if (held) g_laneLock.clear(std::memory_order_release);
    }
};

inline bool finite3(const float* v)
{
    return v[0] == v[0] && v[1] == v[1] && v[2] == v[2] &&
           fabsf(v[0]) < 1e12f && fabsf(v[1]) < 1e12f && fabsf(v[2]) < 1e12f;
}

/* One upload's worth of lane differences, for the buffer that carries cascades.
 *
 * Measurement only. It runs after the caller's own passes have finished, reads
 * the bytes and writes nothing back, and its whole output is the report at the
 * bottom of this file. */
void lane_census(const uint8_t* data, uint32_t bytes, uint32_t byteWidth,
                 bool hasOrtho, const Vec3& refPos)
{
    if (!census_wanted())
        return;

    if (bytes < 32 || bytes > kLaneMaxBytes)
        return;

    /* A buffer earns a slot by carrying a cascade OR by being big enough to be a
       per-view or per-pass table. Per-object buffers - the thousands-per-frame
       ones - are neither, and letting them in would spend all six slots in the
       first millisecond of a mission. */
    if (!hasOrtho && byteWidth < kLaneBigBytes)
        return;

    LaneGuard guard;
    if (!guard.held)
        return;   /* another thread is mid-difference; skip rather than tear */

    int slot = -1;
    for (int i = 0; i < kLaneSlots; ++i)
    {
        if (g_laneBuf[i].bytes == byteWidth) { slot = i; break; }
    }
    if (slot < 0)
    {
        for (int i = 0; i < kLaneSlots; ++i)
        {
            if (g_laneBuf[i].bytes == 0)
            {
                g_laneBuf[i].bytes = byteWidth;
                slot = i;
                break;
            }
        }
    }
    if (slot < 0)
        return;   /* every slot spoken for; the report names which */

    LaneBuf& lb = g_laneBuf[slot];
    if (hasOrtho)
        lb.hadOrtho = true;

    /* ONE SAMPLE PER PRESENT, AND THIS IS WHAT MADE THE FIRST READING USELESS.
     *
     * A per-object buffer is uploaded hundreds of times a frame with a DIFFERENT
     * object's data each time. Differencing consecutive uploads then measures the
     * distance between two unrelated objects, which is metres to hundreds of
     * metres, and the report filled up with lanes whose travel was four or five
     * orders of magnitude larger than the head's - cb 32768 offset 10784 moved
     * 20,347 m while the head moved 0.5 m.
     *
     * Sampling once per frame turns each lane into a proper time series: the same
     * slot, one frame apart, which is the only comparison that means anything.
     * A cascade origin is written once per frame and survives this untouched. */
    const uint64_t frame = present_count();
    if (lb.primed && frame == lb.lastFrame)
        return;
    lb.lastFrame = frame;

    const float* cur = reinterpret_cast<const float*>(data);

    if (!lb.primed)
    {
        memcpy(g_lanePrev[slot], cur, bytes);
        lb.prevRef[0] = refPos.x;
        lb.prevRef[1] = refPos.y;
        lb.prevRef[2] = refPos.z;
        lb.primed = true;
        return;
    }

    const Vec3 head = { refPos.x - lb.prevRef[0],
                        refPos.y - lb.prevRef[1],
                        refPos.z - lb.prevRef[2] };
    const float headLen = len3(head);

    /* A STILL HEAD CANNOT ANSWER THE QUESTION, so it does not get to vote.
       Below this the head has not really moved, and differencing anyway fills
       every accumulator with whatever the engine animates for its own reasons -
       which is the noise the ratio is supposed to be measured against. The
       previous contents are deliberately NOT refreshed here: the interval keeps
       growing until the head has actually gone somewhere. */
    if (headLen < 0.004f)
        return;

    const uint32_t lanes = bytes / 16;

    for (uint32_t l = 0; l < lanes; ++l)
    {
        const float* p = cur + l * 4;
        const float* q = g_lanePrev[slot] + l * 4;

        if (!finite3(p) || !finite3(q))
            continue;

        const Vec3 d = { p[0] - q[0], p[1] - q[1], p[2] - q[2] };
        const float dLen = len3(d);

        /* A lane that jumped by kilometres did not MOVE, it changed meaning -
           the engine reused the slot for another pass. Counting it would put a
           number at the top of the report that no cascade could ever reach. */
        if (dLen > 1e4f)
            continue;

        LaneAcc& a = g_laneAcc[slot][l];
        a.travel += dLen;
        if (dLen > a.maxStep)
            a.maxStep = dLen;
        if (dLen > 1e-6f)
            a.align += fabsf(dot3(d, head)) / (dLen * headLen);
        ++a.samples;
    }

    lb.headTravel += headLen;
    ++lb.samples;

    memcpy(g_lanePrev[slot], cur, bytes);
    lb.prevRef[0] = refPos.x;
    lb.prevRef[1] = refPos.y;
    lb.prevRef[2] = refPos.z;
}

/* =============================================================================
 * THE DIRECTION LANES - what the sky and the deferred lighting actually read.
 * =============================================================================
 *
 * WHY THE MATRIX SCAN CANNOT SEE THEM
 *
 * vp_sky was investigated to a conclusion and correctly left off: the only
 * origin-centred projections in the buffers are cube faces, and the one
 * promising candidate (offset 688) has never reappeared in any run since. That
 * is not a scanner looking badly. It is a sky that HAS NO MATRIX to find - and
 * the same is true of the shadow-receive pass, which is why every attempt to
 * fix set A by finding one more 4x4 has failed.
 *
 * A modern sky is a full-screen pass whose view ray is interpolated from FOUR
 * CORNER DIRECTIONS. A deferred lighting pass reconstructs world position from
 * depth the same way: position = camera + ray * linearDepth. Neither needs a
 * matrix, and neither one IS a matrix, so no signature built out of rows,
 * forwards and frusta can ever match it.
 *
 * This file already made exactly this argument once, about the cascade origin:
 *
 *     "It is not the boundary. It is the boundary of looking for MATRICES. A
 *      float3 that tracks the camera is just as measurable as a matrix that
 *      does."   - the note on the per-cascade offset
 *
 * The same sentence applies to a corner ray. This is that measurement pointed
 * at rotation instead of translation.
 *
 * THE TEST IS PREDICTIVE, NOT DESCRIPTIVE
 *
 * The lane census asks "how far did this lane move, and was it the way the head
 * moved" - a description, scored after the fact. That works for a translation
 * because there is only one direction to be right about. A direction lane needs
 * something stronger, because ANY lane holding a unit vector wanders by roughly
 * the right amount when the camera turns; getting the amount right is not
 * evidence of anything.
 *
 * So this predicts. If a lane holds a world-space direction rigidly attached to
 * the camera, then
 *
 *     w_now  ==  w_prev * R_delta,     R_delta = R_prev^T * R_now
 *
 * exactly, for the whole 3-vector, whatever the lane means and whatever it is
 * scaled by. Two residuals are then measured against each other:
 *
 *     residual CAMERA-LOCKED   |w_now - w_prev * R_delta|
 *     residual WORLD-ANCHORED  |w_now - w_prev|
 *
 * and the statistic is how much of the second the first explains away:
 *
 *     explained = 1 - residualCam / residualWorld
 *
 * 1.0 is a lane that rotates with the camera and does nothing else. 0.0 is a
 * lane that did not move, or moved for its own reasons. NEGATIVE is a lane that
 * moved AGAINST the camera - which is what a compensating term looks like, and
 * worth seeing rather than clamping away.
 *
 * Scale-free, no guess at the convention, and it handles ROLL - which a
 * forward-vector-only test cannot.
 *
 * THE ROTATION COMES FROM THE EYE CAMERA, and that is deliberate: it is the
 * only full basis published to this file. VpFrame carries refDir alone for the
 * engine, which is an axis, not a frame. Over one frame the eye and engine
 * cameras differ by D, which is small beside the turn being measured, so it is
 * the right rotation to within the thing being detected.
 * ========================================================================== */

/* 0 off. 1 rotates the named lanes into our eye's frame. -1 the other way.
 *
   Three-way for the reason image_latch.h gives and this file has paid for three
   times over: the composition order here is the single easiest thing to get
   backwards, and TESTs 101, 102 and 104 each shipped a sign that had been
   reasoned about instead of run. The derivation says a camera-derived DIRECTION
   wants the opposite rotation from the one world POINTS want - w * D3^T, not
   w * D3 - which is exactly the kind of claim that has been wrong here before.
   The run decides. */
float ray_patch()
{
    static float cached = 0.0f;
    static bool have = false;
    if (!have) { have = true; cached = config_float("vp_ray_patch", 0.0f); }
    return cached;
}

bool ray_census_on()
{
    static bool cached = false;
    static bool have = false;
    if (!have) { have = true; cached = config_bool("vp_ray_census", false); }
    return cached;
}

/* Which lanes to write, once the census has named them. Deliberately explicit
   rather than "write whatever scored well this second": a census result is read
   by a person, checked against a second run, and only then aimed. That is the
   order the reprojection matrix went in, and the order cascade_snap went in. */
uint32_t ray_cb()
{
    static uint32_t cached = 0;
    static bool have = false;
    if (!have) { have = true; cached = static_cast<uint32_t>(config_float("vp_ray_cb", 0.0f)); }
    return cached;
}

int ray_offset()
{
    static int cached = -1;
    static bool have = false;
    if (!have) { have = true; cached = static_cast<int>(config_float("vp_ray_offset", -1.0f)); }
    return cached;
}

/* A first offset and a lane count to print unconditionally. -1 prints none. */
int ray_window()
{
    static int cached = -1;
    static bool have = false;
    if (!have) { have = true; cached = static_cast<int>(config_float("vp_ray_window", -1.0f)); }
    return cached;
}

int ray_window_lanes()
{
    static int cached = 8;
    static bool have = false;
    if (!have) { have = true; cached = static_cast<int>(config_float("vp_ray_window_lanes", 8.0f)); }
    return cached;
}

int ray_count()
{
    static int cached = 4;
    static bool have = false;
    if (!have) { have = true; cached = static_cast<int>(config_float("vp_ray_count", 4.0f)); }
    return cached;
}

/* A SECOND RANGE, because the census found TWO camera-derived blocks in cb
   1760 and they are 240 bytes apart with unrelated things in between:

       688  right * 1.842   704  up * 1.329   720  (none)   736  forward * 0.98
       928  right           944  up           960  -forward

   The first is a ray basis already scaled by the engine's own tangents; the
   second is the same basis unscaled. Both are world directions built from the
   engine camera, so both want the same rotation, and writing one without the
   other would leave whichever pass reads the other still pointing at the flat
   camera. They are separate keys rather than one wide range because the 12
   lanes between them are fog and light constants that have nothing to do with
   the camera - 1026.2190, 597.2595, 321.2065 sat there unchanged all run. */
/* Range B's buffer width. 0 means "the same as range A's".

   Needed because the two blocks worth writing are not in the same buffer any
   more. cb 1760 at 688/928 is the big per-frame buffer and writing it changed
   nothing that renders - TEST 157. cb 224 at 128 carries a BYTE-IDENTICAL copy
   of the same basis (moved 4.0163 against 4.0165 on the same report) in a
   small per-pass buffer, and a per-pass buffer is uploaded BY the pass that
   reads it. That is the one timing window a write at upload time can still be
   early enough to matter, and it is the last thing the data points at. */
/* WHAT KIND OF QUANTITY THE LANE HOLDS. 0 direction, 1 point.

   THIS IS THE DISTINCTION TESTS 150-153 WERE MISSING, and it is measurable
   rather than arguable. Offset 720 in cb 1760 is

       raw (10255.86, 8916.65, 295.00, w 19.999920)   |xyz| 13593.24

   which is a POSITION, thirteen thousand units from the world origin. It was
   being handed to rot3, which applies D's 3x3 only - a rotation about the
   ORIGIN. Rotating a point 13593 out by the half-degree D of a moderate head
   turn displaces it by 13593 * sin(0.5 deg), about 118 units, every frame, in
   whichever direction the sign selected. That is "dynamic shadows move very
   fast with every tiny movement" with a number attached, and it is why both
   signs looked equally wrong: 118 units of displacement does not care which
   way it points.

   D ALREADY CONTAINS THE FIX. Its translation row is built as

       d.m[12] = cx - (cx*d.m[0] + cy*d.m[4] + cz*d.m[8])

   about f.position, which makes D a rotation ABOUT THE CAMERA rather than about
   the origin. Applied as a full 4x4 to a world point it moves that point
   exactly the way pass one moves the world - same D, same fixed point, same
   magnitude. A point 20 m from the camera moves 17 cm, not 118 units.

   So a direction lane takes the 3x3 and a point lane takes all four rows. One
   third of a matrix was being applied to a lane that needed the whole of it. */
int ray_kind()
{
    static int cached = 0;
    static bool have = false;
    if (!have) { have = true; cached = static_cast<int>(config_float("vp_ray_kind", 0.0f)); }
    return cached;
}

int ray_kind_b()
{
    static int cached = 0;
    static bool have = false;
    if (!have) { have = true; cached = static_cast<int>(config_float("vp_ray_kind_b", 0.0f)); }
    return cached;
}

uint32_t ray_cb_b()
{
    static uint32_t cached = 0;
    static bool have = false;
    if (!have) { have = true; cached = static_cast<uint32_t>(config_float("vp_ray_cb_b", 0.0f)); }
    return cached;
}

int ray_offset_b()
{
    static int cached = -1;
    static bool have = false;
    if (!have) { have = true; cached = static_cast<int>(config_float("vp_ray_offset_b", -1.0f)); }
    return cached;
}

int ray_count_b()
{
    static int cached = 3;
    static bool have = false;
    if (!have) { have = true; cached = static_cast<int>(config_float("vp_ray_count_b", 3.0f)); }
    return cached;
}

/* EIGHT SLOTS, AND FOUR WAS ALREADY TOO FEW.

   The first run put cb 800, cb 40960, cb 1760 and cb 528 in the four slots and
   the second put cb 528 and cb 6656 in them - the set CHANGED between runs,
   because a slot is claimed first-come and never released. Whichever buffer
   lost the race was not reported as empty; it was not reported at all, and an
   absent buffer reads exactly like a buffer with nothing in it.

   That is the same mistake the lane census made and wrote down - "two buffers
   it never looked at were right there in the same log" - arrived at from the
   other direction. The sky is still unaccounted for, and the one thing worse
   than not finding it is concluding it is absent from a buffer that was never
   actually watched.

   The floor comes down to 64 bytes with it. cb 128 carries a view matrix and
   is patched 23,436 times in five seconds, so it is a per-pass view buffer -
   precisely the shape a sky pass would be given, and excluded outright by a
   256-byte floor.

   Cost is 8 * 64 KB of previous contents plus 8 * 4096 accumulators, about
   1.4 MB static. The once-per-present gate means the work does not scale with
   upload count, only with buffer size. */
/* IS THIS A BUFFER PASS FOUR IS AIMED AT?

   Needed to keep such a buffer out of the write-off below it. A buffer that
   never carries a main-view matrix scores no hits, retires after kMissLimit
   uploads and is then scanned once in every 256 - which is correct for the
   matrix passes and ruinous for pass four, because cb 224 is exactly that kind
   of buffer. It would have been written on one upload in 256 while the report
   showed a healthy-looking rotate count, and the run would have read as "cb
   224 changes nothing" when what it actually showed is "cb 224 was barely
   written". A null result dressed as a negative one.

   The census already sidesteps this by running ahead of the write-off. Pass
   four cannot - it needs D, and D comes from the scan - so the buffer is
   exempted from retirement instead. Only while pass four is actually armed and
   pointed at it, and cb 224 is 14 lanes, so scanning it every upload costs
   nothing worth measuring. */
bool ray_target(uint32_t byteWidth);

constexpr int      kRaySlots    = 8;
constexpr uint32_t kRayMaxBytes = 65536;
constexpr uint32_t kRayMaxLanes = kRayMaxBytes / 16;
constexpr uint32_t kRayBigBytes = 64;

struct RayAcc {
    float    explained;  /* summed (1 - residCam/residWorld) */
    float    moved;      /* summed |w_now - w_prev| */
    float    lenSum;     /* summed |w| - separates a unit ray from a scaled one */
    uint32_t samples;

    /* THE POSITION CENSUS, WHICH IS THE DIRECTION ONE WITH THE w PUT BACK.

       A camera-derived POSITION is invisible to everything this file owns. The
       lane census differences raw xyz and would score a position stored as
       pos*20 at a ratio of 20 against the camera's own step, which its
       min(ratio,1/ratio) shaping then buries at 0.05. The direction census
       above rejects it outright for not being a unit-ish vector. And the lane
       census has been switched OFF all session anyway - vp_census = 0 - so the
       one tool built to find a cascade origin has collected nothing.

       Lane 720 proved the convention: positions in these buffers are stored
       HOMOGENEOUS, pre-multiplied by w. So divide by w first and the scale
       stops mattering - pos*20 with w=20 and pos*1 with w=1 both come back as
       pos, and a ratio against the camera's own step is then meaningful for
       either.

       ratio near 1 with align near 1 is a lane riding the camera. A cascade
       origin is exactly that, and it is the next thing worth writing. */
    float    posTravel;  /* summed |delta(xyz/w)| */
    float    posAlign;   /* summed |cos| against the camera's own step */
    float    posMax;     /* largest single step - the texel snap, if it snaps */
    uint32_t posSamples;

    /* The lane as it last stood. A length alone already named two of these -
       1.842 and 1.329 are the engine tangents to four figures - but a length
       cannot say WHICH axis, and "it is probably the right vector" is exactly
       the kind of inference this file has paid for. Kept so the report can
       resolve the lane against the recovered camera basis and print the answer
       instead of a hint. */
    float    last[3];
};

struct RayBuf {
    uint32_t bytes;
    bool     primed;
    float    prevRot[9];  /* the eye camera's 3x3 at the previous sighting */
    float    turned;      /* summed radians of camera rotation */
    uint64_t lastFrame;
    uint32_t samples;

    /* For the position census below. */
    float    prevRef[3];
    float    camTravel;
    uint32_t posSamples;
};

/* THE WINDOW LANES, RAW, WITH NO FILTER OF ANY KIND IN FRONT OF THEM.

   The accumulators only ever see a lane that passed the length test - between
   1e-3 and 1e4 - because a lane outside that has no direction to judge and
   voting it would be noise. That is right for the census and WRONG for the
   report, and the difference cost a run: offset 720 printed "no vector" and
   was read as harmless, while pass four - which checks only for NaN - went on
   rotating it nine times a frame. "No vector" covers a lane of zeroes AND a
   lane holding a packed scalar of 1e6, and those are opposite problems.

   So the window keeps the raw float4 of every lane in its range, taken before
   any test, INCLUDING the w component the rest of this file deliberately never
   touches. Twenty-four lanes of it, which is nothing, and it is the difference
   between a lane being unexplained and a lane being unseen. */
constexpr int kWinMax = 64;
float g_winRaw[kWinMax][4] = {};
bool  g_winSeen[kWinMax] = {};

RayBuf g_rayBuf[kRaySlots] = {};
float  g_rayPrev[kRaySlots][kRayMaxBytes / 4] = {};
RayAcc g_rayAcc[kRaySlots][kRayMaxLanes] = {};

/* Same reason as the lane census: rgl uploads from more than one thread, and a
   whole-buffer difference cannot tolerate two of them interleaving. */
std::atomic_flag g_rayLock = ATOMIC_FLAG_INIT;

struct RayGuard {
    bool held;
    explicit RayGuard() { held = !g_rayLock.test_and_set(std::memory_order_acquire); }
    ~RayGuard() { if (held) g_rayLock.clear(std::memory_order_release); }
};

/* out = a row-vector direction rotated by a row-major 3x3. */
inline void rot3(float* out, const float* w, const float* r)
{
    out[0] = w[0]*r[0] + w[1]*r[3] + w[2]*r[6];
    out[1] = w[0]*r[1] + w[1]*r[4] + w[2]*r[7];
    out[2] = w[0]*r[2] + w[1]*r[5] + w[2]*r[8];
}

/* One upload's worth of direction-lane residuals.
 *
   Measurement only: reads the bytes, writes nothing back, and its whole output
   is the report at the bottom of this file. */
void ray_census(const uint8_t* data, uint32_t bytes, uint32_t byteWidth,
                const VpFrame& pub)
{
    if (!ray_census_on())
        return;

    if (bytes < 32 || bytes > kRayMaxBytes || byteWidth < kRayBigBytes)
        return;

    RayGuard guard;
    if (!guard.held)
        return;

    int slot = -1;
    for (int i = 0; i < kRaySlots; ++i)
        if (g_rayBuf[i].bytes == byteWidth) { slot = i; break; }
    if (slot < 0)
    {
        for (int i = 0; i < kRaySlots; ++i)
            if (g_rayBuf[i].bytes == 0) { g_rayBuf[i].bytes = byteWidth; slot = i; break; }
    }
    if (slot < 0)
        return;

    RayBuf& rb = g_rayBuf[slot];

    /* One sample per present, for the reason LaneBuf::lastFrame gives: a
       per-object buffer re-uploaded hundreds of times a frame would otherwise
       be differenced against another object's data. */
    const uint64_t frame = present_count();
    if (rb.primed && frame == rb.lastFrame)
        return;
    rb.lastFrame = frame;

    /* Rows 0,1,2 of the eye camera - right, up, forward. The labelling does not
       have to match rgl's; it cancels out of R_delta exactly. */
    const float rot[9] = {
        pub.eyeCamera[0], pub.eyeCamera[1], pub.eyeCamera[2],
        pub.eyeCamera[4], pub.eyeCamera[5], pub.eyeCamera[6],
        pub.eyeCamera[8], pub.eyeCamera[9], pub.eyeCamera[10]
    };
    for (int i = 0; i < 9; ++i)
        if (!(rot[i] == rot[i]) || fabsf(rot[i]) > 1e3f)
            return;

    const float* cur = reinterpret_cast<const float*>(data);

    if (!rb.primed)
    {
        memcpy(g_rayPrev[slot], cur, bytes);
        memcpy(rb.prevRot, rot, sizeof(rot));
        rb.prevRef[0] = pub.refPos[0];
        rb.prevRef[1] = pub.refPos[1];
        rb.prevRef[2] = pub.refPos[2];
        rb.primed = true;
        return;
    }

    /* THE POSITION HALF, run before the rotation gate below - a camera that
       TRANSLATES without turning is exactly the case that answers this
       question, and the rotation gate would have thrown it away. */
    {
        const Vec3 camStep = { pub.refPos[0] - rb.prevRef[0],
                               pub.refPos[1] - rb.prevRef[1],
                               pub.refPos[2] - rb.prevRef[2] };
        const float camLen = len3(camStep);

        if (camLen >= 0.004f)
        {
            const uint32_t planes = bytes / 16;
            const float* pcur = reinterpret_cast<const float*>(data);

            for (uint32_t l = 0; l < planes; ++l)
            {
                const float* p = pcur + l * 4;
                const float* q = g_rayPrev[slot] + l * 4;

                if (!finite3(p) || !finite3(q))
                    continue;
                if (!(p[3] == p[3]) || !(q[3] == q[3]))
                    continue;

                /* Divide by w so the stored scale drops out. w near zero means
                   this lane is a direction, not a position, and the direction
                   census already has it. */
                const float wn = p[3], wo = q[3];
                if (fabsf(wn) < 1e-6f || fabsf(wo) < 1e-6f)
                    continue;

                const Vec3 pn = { p[0]/wn, p[1]/wn, p[2]/wn };
                const Vec3 po = { q[0]/wo, q[1]/wo, q[2]/wo };
                const Vec3 st = { pn.x - po.x, pn.y - po.y, pn.z - po.z };
                const float sl = len3(st);

                if (!(sl == sl) || sl > 1e4f)
                    continue;

                RayAcc& a = g_rayAcc[slot][l];
                a.posTravel += sl;
                if (sl > a.posMax)
                    a.posMax = sl;
                if (sl > 1e-9f)
                    a.posAlign += fabsf(dot3(st, camStep)) / (sl * camLen);
                ++a.posSamples;
            }

            rb.camTravel += camLen;
            ++rb.posSamples;

            rb.prevRef[0] = pub.refPos[0];
            rb.prevRef[1] = pub.refPos[1];
            rb.prevRef[2] = pub.refPos[2];
        }
    }

    /* R_delta = R_prev^T * R_now, row-major, applied to a row vector. */
    float rd[9];
    for (int r = 0; r < 3; ++r)
        for (int c = 0; c < 3; ++c)
            rd[r*3+c] = rb.prevRot[0*3+r] * rot[0*3+c]
                      + rb.prevRot[1*3+r] * rot[1*3+c]
                      + rb.prevRot[2*3+r] * rot[2*3+c];

    /* The angle, from the trace. This is the rotational analogue of the lane
       census's "a still head cannot answer the question": below a fraction of a
       degree R_delta is indistinguishable from identity, the two hypotheses
       being compared collapse into each other, and differencing anyway fills
       the accumulators with whatever the engine animates for its own reasons.
       The previous contents are deliberately NOT refreshed, so the interval
       keeps growing until the camera has actually turned. */
    const float tr = rd[0] + rd[4] + rd[8];
    float cosA = (tr - 1.0f) * 0.5f;
    if (cosA > 1.0f) cosA = 1.0f;
    if (cosA < -1.0f) cosA = -1.0f;
    const float theta = acosf(cosA);

    if (!(theta == theta) || theta < 0.0035f)   /* ~0.2 degrees */
        return;

    const uint32_t lanes = bytes / 16;

    /* The window, raw and unfiltered, before the loop below starts rejecting
       lanes for not having a direction. See the note on g_winRaw. */
    if (ray_window() >= 0 && (ray_cb() == 0 || byteWidth == ray_cb()))
    {
        const uint32_t w0 = static_cast<uint32_t>(ray_window()) / 16u;
        for (int k = 0; k < ray_window_lanes() && k < kWinMax; ++k)
        {
            const uint32_t li = w0 + static_cast<uint32_t>(k);
            if (li >= lanes)
                break;
            memcpy(g_winRaw[k], cur + li * 4, sizeof(float) * 4);
            g_winSeen[k] = true;
        }
    }

    for (uint32_t l = 0; l < lanes; ++l)
    {
        const float* p = cur + l * 4;
        const float* q = g_rayPrev[slot] + l * 4;

        if (!finite3(p) || !finite3(q))
            continue;

        const Vec3 wPrev = { q[0], q[1], q[2] };
        const float lenPrev = len3(wPrev);

        /* Too small to have a direction, or large enough that the slot changed
           meaning between uploads rather than rotating. */
        if (lenPrev < 1e-3f || lenPrev > 1e4f)
            continue;

        float pred[3];
        rot3(pred, q, rd);

        const Vec3 dWorld = { p[0] - q[0],    p[1] - q[1],    p[2] - q[2]    };
        const Vec3 dCam   = { p[0] - pred[0], p[1] - pred[1], p[2] - pred[2] };

        const float residWorld = len3(dWorld);
        const float residCam   = len3(dCam);

        /* How far a camera-locked lane of this length WOULD have moved. Used as
           the yardstick for both the noise floor and the stationary test, so
           the numbers below are in units of the effect being looked for rather
           than in metres. */
        const float wouldHaveMoved = lenPrev * theta;
        if (wouldHaveMoved < 1e-5f)
            continue;

        RayAcc& a = g_rayAcc[slot][l];
        if (residWorld < wouldHaveMoved * 0.02f)
        {
            /* Stationary while the camera turned: decisively NOT camera-locked.
               Scored as zero explicitly rather than skipped, so a lane anchored
               to the world says so in the report instead of being absent from
               it. The lane census learned that one the hard way - an empty list
               under a confident header reads as a measurement rather than as
               the absence of one. */
            a.explained += 0.0f;
        }
        else
        {
            float e = 1.0f - residCam / residWorld;
            if (e < -1.0f) e = -1.0f;
            a.explained += e;
        }

        a.moved  += residWorld;
        a.lenSum += lenPrev;
        a.last[0] = p[0];
        a.last[1] = p[1];
        a.last[2] = p[2];
        ++a.samples;
    }

    rb.turned += theta;
    ++rb.samples;

    memcpy(g_rayPrev[slot], cur, bytes);
    memcpy(rb.prevRot, rot, sizeof(rot));
}
std::atomic<uint64_t> g_statRay{0};
std::atomic<uint64_t> g_statRaySkip{0};
std::atomic<uint64_t> g_statRayNotBasis{0};
std::atomic<uint64_t> g_statRayBorrowed{0};
std::atomic<uint64_t> g_statRayPoint{0};
std::atomic<uint64_t> g_statRayPointBad{0};
std::atomic<uint64_t> g_statRayPointMoved{0};   /* millimetres, summed */
std::atomic<uint64_t> g_statRayProbeDeg{0};     /* milli-degrees, summed */
std::atomic<uint64_t> g_statRayProbeMm{0};      /* millimetres, summed */
std::atomic<uint64_t> g_statRayProbeN{0};
std::atomic<uint64_t> g_statRayNoD{0};

/* THE LAST D WE BUILT, AND WHY IT HAS TO OUTLIVE ITS OWN UPLOAD.

   Pass four has always required haveD, which is set only when a main-view
   matrix was recognised IN THE SAME UPLOAD. That is exactly right for cb 1760,
   which carries the main view nine times a frame. It is fatal for cb 224,
   which is not a patch site at all: no main view in the buffer, no D, and pass
   four would have written nothing while reporting nothing wrong - a null
   result indistinguishable from a negative one, which is the worst kind.

   So D is also kept here, stamped with the frame it was built for, and pass
   four falls back to it for a buffer that carries no main view of its own. The
   AGE IS REPORTED rather than assumed: if cb 224 uploads before the first
   cb 1760 of the frame, this is a frame stale, and a stale D is the precise
   bug that made TEST 150 flicker. The log says which, instead of me guessing a
   second time.

   Unsynchronised on purpose, like g_engBasis beside it: rgl uploads from
   several threads, a torn read costs one frame of one lane, and a lock on the
   hot upload path costs every frame. */
Mat4 g_lastD;
std::atomic<uint64_t> g_lastDFrame{0};
std::atomic<bool> g_lastDValid{false};

/* WHAT WE LAST WROTE INTO EACH PATCHED LANE, AND WHY IT HAS TO BE REMEMBERED.

   cb 1760 is uploaded about NINE times per frame - 4050 scans in five seconds
   at 450 frames - and pass four fired on every one of them, rotating whatever
   it found. That is only correct if the engine rewrites these lanes on every
   upload. If it rewrites the view matrix each pass and leaves the ray basis
   from the first write standing, then uploads two through nine rotate a value
   WE ALREADY ROTATED, and one frame of D lands nine times.

   Nine times a fraction of a degree is several degrees per frame, it grows
   with the smallest head movement, and it is FAST IN BOTH DIRECTIONS - which
   is exactly what both signs produced. A sign error changes which way set A
   slides. It cannot make both ways slide faster. Compounding can, and this is
   the only mechanism on the table that does.

   So the value we wrote is kept. If the lane comes back bit-identical to it,
   the engine did not touch it and it is ALREADY the value we want - skip, do
   not rotate again. If it differs by a single bit it is fresh engine data and
   gets the rotation. Exact comparison is right here rather than an epsilon:
   these are the bits we wrote ourselves, and nothing else in the frame has any
   business writing them.

   Correct under BOTH hypotheses, which is the point - it needs no answer to
   the question in advance, and the skip count in the report IS the answer. */
bool ray_target(uint32_t byteWidth)
{
    if (ray_patch() == 0.0f)
        return false;

    const uint32_t cbB = ray_cb_b() != 0 ? ray_cb_b() : ray_cb();

    if (ray_offset() >= 0 && (ray_cb() == 0 || byteWidth == ray_cb()))
        return true;
    if (ray_offset_b() >= 0 && (cbB == 0 || byteWidth == cbB))
        return true;

    return false;
}

constexpr int kRayLaneSlots = 16;
struct RayWritten { float v[3]; bool valid; };
RayWritten g_rayWritten[kRayLaneSlots] = {};

/* The ENGINE camera basis as recovered from its own matrix - not the eye
   basis, and not managed's refDir, which is one axis rather than a frame.
   The lanes are built by the engine from the engine's camera, so this is the
   frame they have to be resolved against for the answer to mean anything.

   Written on the render thread wherever a main view is recognised, read by the
   report. Racy by construction and deliberately unsynchronised: it is a label
   on a diagnostic line, and a torn one costs a misprinted dot on one report. */
float g_engBasis[9] = {};
float g_engPos[3] = {};
std::atomic<bool> g_engBasisValid{false};

void probe_note(ProbeRow* table, std::atomic<int>& count, uint32_t byteWidth,
                size_t offset, bool transposed, float dot, float pos, float dev)
{
    const int have = count.load(std::memory_order_relaxed);
    for (int i = 0; i < have && i < kProbeRows; ++i)
    {
        if (table[i].bytes == byteWidth && table[i].offset == offset &&
            table[i].transposed == (transposed ? 1u : 0u))
        {
            ++table[i].hits;
            if (dot > table[i].bestDot) table[i].bestDot = dot;
            if (pos < table[i].minPos) table[i].minPos = pos;
            if (dev > table[i].maxDev) table[i].maxDev = dev;
            return;
        }
    }

    if (have >= kProbeRows)
        return;

    const int slot = count.fetch_add(1, std::memory_order_relaxed);
    if (slot >= kProbeRows)
        return;

    table[slot].bytes = byteWidth;
    table[slot].offset = static_cast<uint32_t>(offset);
    table[slot].transposed = transposed ? 1u : 0u;
    table[slot].hits = 1;
    table[slot].bestDot = dot;
    table[slot].minPos = pos;
    table[slot].maxDev = dev;
}

/* Max deviation from identity, or a negative number if this block is too far
   from identity to be a reprojection matrix. Cheap rejects come first: this
   runs on every 16-byte offset of every scanned buffer. */
/* HOW FAR FROM IDENTITY A BLOCK MAY STRAY AND STILL BE SIFTED.
 *
 * This was a hard 0.25, and the two halves of the identification were pulling
 * against each other because of it.
 *
 * The sift asks "could these bytes be a reprojection matrix" and answers it by
 * how close to identity they are - reasonable, because a frame-to-frame
 * reprojection IS near identity when little has moved. The decisive test then
 * asks "does a candidate built from our camera EXPLAIN this block's drift",
 * and it needs the drift to be large: bestErr <= 0.25 * dev cannot be met when
 * dev is nearly zero, because then everything matches identity equally well and
 * nothing discriminates.
 *
 * So identification requires frames where the head moves FAST - and those are
 * exactly the frames a tight band throws away. The census reported this run's
 * one suspect at a peak drift of 0.23633 against a cap of 0.25, which is the
 * two thresholds meeting: the block was admitted only while the head was slow,
 * where it can never be decisive, and rejected the moment it had something to
 * say. Eight consecutive agreements were never going to happen.
 *
 * A wider band admits more candidates, which is not the risk it sounds like:
 * the discrimination is done by the decisive test against four known forms, and
 * that test gets STRONGER as the drift grows. The band only has to be loose
 * enough not to discard the evidence. */
/* THE PASS THE ENGINE IS CURRENTLY SET UP TO DRAW, as far as its bound targets
 * reveal it. Written by the OMSetRenderTargets hooks on every binding change,
 * read here at constant-buffer upload time.
 *
 * rgl binds its targets and THEN uploads the constants for that pass, so the
 * target set at Unmap time is the pass the buffer is about to serve. That is the
 * only handle we have on "which pass is this upload for", and it is exactly the
 * question cb 1760 needs answered: it is uploaded about five times a frame and
 * we have been patching all five.
 *
 * A shadow map is drawn DEPTH ONLY - a depth-stencil view with no colour target
 * bound - which is what makes hasRtv worth recording on its own. */
std::atomic<uint32_t> g_curTgtW{ 0 };
std::atomic<uint32_t> g_curTgtH{ 0 };
std::atomic<bool>     g_curHasRtv{ false };

/* WHICH PASS EACH PATCH LANDED IN, counted by the target set that was bound.
 *
 * cb 1760 is uploaded about five times a frame and vp_patch_only_bytes = 1760
 * still writes every one of them - which is why restricting to that buffer fixed
 * the floating and brought the shadows straight back. One of those five is the
 * main view; the rest are other passes that share the camera, and a shadow map
 * is drawn depth-only, with no colour target bound.
 *
 * This counts them so the split is a number rather than another theory. */
struct TgtCtx { uint32_t w, h; uint8_t hasRtv; uint64_t hits; uint8_t used; };
constexpr int kTgtCtxRows = 12;
TgtCtx g_tgtCtx[kTgtCtxRows] = {};

void tgt_ctx_note()
{
    if (!census_wanted())
        return;

    const uint32_t w = g_curTgtW.load(std::memory_order_relaxed);
    const uint32_t h = g_curTgtH.load(std::memory_order_relaxed);
    const uint8_t  r = g_curHasRtv.load(std::memory_order_relaxed) ? 1u : 0u;

    for (int i = 0; i < kTgtCtxRows; ++i)
    {
        TgtCtx& c = g_tgtCtx[i];
        if (c.used && (c.w != w || c.h != h || c.hasRtv != r))
            continue;
        if (!c.used) { c.used = 1; c.w = w; c.h = h; c.hasRtv = r; }
        ++c.hits;
        return;
    }
}

/* vp_patch_rtv_only = 1 refuses to write while no COLOUR target is bound.
 *
 * MEASURED WRONG, KEPT BECAUSE IT COST A RUN. It took the floating with it: the
 * main view's own upload also happens with no colour target bound, so "has a
 * colour target" separates nothing here. The census row that matters is finer -
 * see patch_skip_shadow_px. */
bool patch_rtv_only()
{
    static bool cached = false;
    static bool have = false;
    if (!have) { have = true; cached = config_bool("vp_patch_rtv_only", false); }
    return cached;
}

/* THE SHADOW MAP, BY ITS OWN SHAPE.
 *
 * The pass census named it outright. cb 1760 is uploaded under six different
 * target sets, and only one of them is a square depth-only surface:
 *
 *     depth 1536 x 1536  colour NO   hits 5052    ~11 per frame - the cascades
 *     depth 2048 x 2176  colour yes  hits  421    ~1 per frame  - the main view
 *     depth    0 x 0     colour NO   hits 5473
 *     depth    0 x 0     colour yes  hits 2526
 *
 * A square depth target with no colour attachment is a shadow map and nothing
 * else - the scene depth is 2048x2176 and never square. So skip writes while
 * exactly that surface is bound, and leave every other upload alone. The main
 * view keeps its substitution wherever it happens to be uploaded, which is what
 * vp_patch_rtv_only got wrong.
 *
 * 0 disables the rule. The size is configurable because a different shadow
 * quality setting changes it, and the census prints whatever it actually is. */
uint32_t patch_skip_shadow_px()
{
    static uint32_t cached = 0;
    static bool     have = false;
    if (!have)
    {
        have = true;
        const float v = config_float("vp_patch_skip_shadow_px", 0.0f);
        cached = (v > 0.0f) ? static_cast<uint32_t>(v) : 0u;
    }
    return cached;
}

/* Is the pass about to consume this upload the shadow map? */
bool pass_is_shadow()
{
    const uint32_t px = patch_skip_shadow_px();
    if (px == 0)
        return false;

    if (g_curHasRtv.load(std::memory_order_relaxed))
        return false;

    return g_curTgtW.load(std::memory_order_relaxed) == px &&
           g_curTgtH.load(std::memory_order_relaxed) == px;
}

/* The one constant-buffer width the patcher may WRITE to, or 0 for all of them.
   Read once; see the note at writeAllowed. */
uint32_t patch_only_bytes()
{
    static uint32_t cached = 0;
    static bool     have = false;
    if (!have)
    {
        have = true;
        const float v = config_float("vp_patch_only_bytes", 0.0f);
        cached = (v > 0.0f) ? static_cast<uint32_t>(v) : 0u;
    }
    return cached;
}

float reproj_band()
{
    static float cached = 0.0f;
    static bool  have = false;
    if (!have)
    {
        have = true;
        cached = config_float("vp_reproj_band", 0.75f);
        if (cached < 0.05f) cached = 0.05f;
        if (cached > 2.0f)  cached = 2.0f;
    }
    return cached;
}

float identity_deviation(const float* m)
{
    const float band = reproj_band();

    if (fabsf(m[0] - 1.0f) > band || fabsf(m[5] - 1.0f) > band ||
        fabsf(m[10] - 1.0f) > band || fabsf(m[15] - 1.0f) > band)
        return -1.0f;

    float dev = 0.0f;
    for (int r = 0; r < 4; ++r)
        for (int c = 0; c < 4; ++c)
        {
            const float want = (r == c) ? 1.0f : 0.0f;
            const float d = fabsf(m[r * 4 + c] - want);
            if (d > band)
                return -1.0f;
            if (d > dev)
                dev = d;
        }

    /* Exact identity is a placeholder the engine uploads constantly, not a
       reprojection. Only a matrix that actually MOVES is a candidate. */
    return dev > 1e-6f ? dev : -1.0f;
}

/* The same measure with no band limit, for matrices we BUILT and therefore know
   the provenance of. identity_deviation returns -1 outside its band, which is
   the right answer when sifting unknown bytes and the wrong one when reporting
   how far our own reprojection has moved. */
float raw_identity_drift(const float* m)
{
    float dev = 0.0f;
    for (int r = 0; r < 4; ++r)
        for (int c = 0; c < 4; ++c)
        {
            const float d = fabsf(m[r * 4 + c] - ((r == c) ? 1.0f : 0.0f));
            if (d > dev)
                dev = d;
        }
    return dev;
}

void site_note(uint32_t byteWidth, size_t offset, bool transposed, bool usedPrev)
{
    const int have = g_siteCount.load(std::memory_order_relaxed);
    for (int i = 0; i < have && i < kSiteRows; ++i)
    {
        if (g_sites[i].bytes == byteWidth && g_sites[i].offset == offset &&
            g_sites[i].transposed == (transposed ? 1u : 0u))
        {
            ++g_sites[i].hits;
            if (usedPrev)
                ++g_sites[i].prevHits;
            return;
        }
    }

    if (have >= kSiteRows)
        return;

    const int slot = g_siteCount.fetch_add(1, std::memory_order_relaxed);
    if (slot >= kSiteRows)
        return;

    g_sites[slot].bytes = byteWidth;
    g_sites[slot].offset = static_cast<uint32_t>(offset);
    g_sites[slot].transposed = transposed ? 1u : 0u;
    g_sites[slot].hits = 1;
    g_sites[slot].prevHits = usedPrev ? 1u : 0u;
}

void log_site(uint32_t byteWidth, size_t offset, bool transposed,
              const Frustum& f, const VpFrame& pub, float dirDot, bool usedPrev)
{
    site_note(byteWidth, offset, transposed, usedPrev);

    if (g_logBudget <= 0)
        return;
    --g_logBudget;

    const Vec3 dp = sub3(f.position, v3(pub.refPos[0], pub.refPos[1], pub.refPos[2]));

    BVR_INFO("VP site: cb %u bytes, offset %u, %s%s | pos (%.2f,%.2f,%.2f) "
             "d(ref) %.2f |pos| %.1f | fwd dot %.4f | tan L%.3f R%.3f U%.3f D%.3f "
             "= %.1f x %.1f deg",
             byteWidth, static_cast<unsigned>(offset),
             transposed ? "transposed" : "row-vector",
             usedPrev ? " [PREVIOUS frame]" : "",
             f.position.x, f.position.y, f.position.z, len3(dp), len3(f.position),
             dirDot, f.tanLeft, f.tanRight, f.tanUp, f.tanDown,
             (atanf(f.tanRight) - atanf(f.tanLeft)) * 57.2957795f,
             (atanf(f.tanUp) - atanf(f.tanDown)) * 57.2957795f);
}

/* The hot path's first question, asked once per 16-byte offset for every
   constant buffer the engine uploads. It has to be cheap.
 *
 * A world->clip matrix has a unit-length w column; almost nothing else does. The
 * squared length answers that without a square root, and it rejects NaN for
 * free, because every comparison against NaN is false. */
inline bool unit_w_column(float x, float y, float z)
{
    const float lengthSquared = x*x + y*y + z*z;
    return lengthSquared > 0.9604f && lengthSquared < 1.0404f;   /* 1 +/- 0.02 */
}

/* THE SHADOW CASCADES, WHICH THIS SCAN HAS NEVER ONCE LOOKED AT.
 *
 * recover() opens by demanding a unit w column, and its comment says exactly
 * what that excludes: "An orthographic projection - every shadow cascade - has
 * a zero w column... Requiring unit length is what keeps shadow, UI and post
 * passes out." Keeping them out was right while the job was finding the main
 * view. It is why five theories about shadows following the head all died: the
 * matrices that draw them were never in the search space.
 *
 * An orthographic world->clip matrix in this engine's row-vector convention has
 * its last column (0,0,0,1) - clip.w is constant, which is what makes it
 * orthographic - and an upper-left 3x3 whose rows are mutually perpendicular,
 * because it is a rotation with a per-axis scale. A cascade's scale is small:
 * it maps tens of metres onto the unit cube, so the rows are far shorter than
 * one, which is what separates a cascade from the identity-ish affine blocks
 * that also have a constant w.
 *
 * This only IDENTIFIES them. Nothing is patched: the question on the table is
 * whether the cascade's own translation tracks the camera, and that is answered
 * by watching it, not by writing to it. */
/* WHICH SLOT HOLDS THE TRANSLATION DEPENDS ON THE CONVENTION, AND THE FIRST
 * VERSION OF THIS ONLY EVER FOUND THE ONES WITH NONE.
 *
 * Row-vector puts the translation in the last ROW, m[12..14], and the constant
 * w in the last column. Column-vector puts it in the last COLUMN, m[3],m[7],
 * m[11], with the constant w in the last row. The first cut of ortho_signature
 * demanded m[3]=m[7]=m[11]=0, which admits a row-vector matrix with any
 * translation - and a column-vector one only when its translation is ZERO.
 *
 * The run found twelve blocks and every one reported (0,0,0), which is not
 * twelve untranslated cascades. It is the filter selecting for exactly the
 * matrices that cannot answer the question, and discarding the ones that can. */
inline bool ortho_translation(const float* m, float* out)
{
    const bool rowVector =
        fabsf(m[3]) < 1e-4f && fabsf(m[7]) < 1e-4f && fabsf(m[11]) < 1e-4f;
    const bool colVector =
        fabsf(m[12]) < 1e-4f && fabsf(m[13]) < 1e-4f && fabsf(m[14]) < 1e-4f;

    if (fabsf(m[15] - 1.0f) > 1e-3f)
        return false;
    if (!rowVector && !colVector)
        return false;

    /* When both are zero the matrix is linear and either reading is the same
       answer; take the row-vector one. */
    if (rowVector)
    {
        out[0] = m[12]; out[1] = m[13]; out[2] = m[14];
    }
    else
    {
        out[0] = m[3]; out[1] = m[7]; out[2] = m[11];
    }
    return true;
}

inline bool ortho_signature(const float* m)
{
    float ignored[3];
    if (!ortho_translation(m, ignored))
        return false;

    const float r0 = m[0]*m[0] + m[1]*m[1] + m[2]*m[2];
    const float r1 = m[4]*m[4] + m[5]*m[5] + m[6]*m[6];
    const float r2 = m[8]*m[8] + m[9]*m[9] + m[10]*m[10];

    /* Real scales, not a degenerate or an identity. A cascade compresses the
       world hard; identity and near-identity affines are excluded above by
       requiring at least one axis well under unit length. */
    if (!(r0 > 1e-12f && r1 > 1e-12f && r2 > 1e-12f))
        return false;
    if (r0 > 0.25f && r1 > 0.25f && r2 > 0.25f)
        return false;

    /* Rows mutually perpendicular: a rotation with scale, not arbitrary bytes. */
    const float d01 = m[0]*m[4] + m[1]*m[5] + m[2]*m[6];
    const float d02 = m[0]*m[8] + m[1]*m[9] + m[2]*m[10];
    const float d12 = m[4]*m[8] + m[5]*m[9] + m[6]*m[10];

    const float scale = sqrtf(r0) * sqrtf(r1);
    return fabsf(d01) < 0.02f * scale &&
           fabsf(d02) < 0.02f * sqrtf(r0) * sqrtf(r2) &&
           fabsf(d12) < 0.02f * sqrtf(r1) * sqrtf(r2);
}

/* =============================================================================
 * THE FIX: SNAP THE CASCADE ORIGIN TO ITS OWN TEXEL GRID
 * =============================================================================
 *
 * WHY A CASCADE CRAWLS, AND WHY VR MAKES IT UNMISSABLE
 *
 * A cascade is a shadow map of fixed resolution covering a slice of the camera
 * frustum. Move the camera and the slice moves, so the world lands on different
 * texels and every shadow edge redraws one texel over - the edge shimmers along
 * with the camera. The standard cure is to quantise the cascade's origin to
 * whole texels: the slice then moves in texel steps, the world keeps landing on
 * the SAME texels, and the edge holds still.
 *
 * Two things make this worse here than in the flat game, and both are ours:
 *
 *   1. THE FRUSTUM. The engine is rendering at vfov 129.5 deg (see the gain note
 *      in the config). A cascade is fitted to the frustum, so the same texel
 *      count is now stretched across roughly three times the width. Every texel
 *      is three times bigger in world terms, and so is every step an edge takes.
 *
 *   2. THE CAMERA NEVER STOPS. A flat game's camera is still whenever the player
 *      is. A head is never still - it drifts continuously, by millimetres, for
 *      as long as the player is alive. So a cascade fitted to it re-fits
 *      continuously, and whatever crawl exists is on display permanently instead
 *      of during mouse movement.
 *
 * That is the whole shape of the complaint, and it explains the measurements
 * that killed the earlier theories. TEST 53's table has shadows moving with the
 * head in AFR WITHOUT DLSS as well as in both AFW columns: the fault is in the
 * engine's own render, in every mode, and DLSS's temporal accumulation was only
 * ever smearing it out of sight. TEST 55 then found the crawl in BOTH eyes,
 * which says the same thing from the other end - it was never the warp. Nothing
 * aimed at the warp, the reprojection matrix or the motion vectors could have
 * touched it, and none of them did.
 *
 * WHAT THIS WRITES
 *
 * The origin lane, quantised along the light's two lateral axes to the world
 * size of one shadow texel. The axes and the scale both come from the cascade's
 * own matrix - the rows of an orthographic block are the light basis scaled by
 * how much world it maps onto the unit cube - so the only thing that has to be
 * supplied from outside is the shadow map's resolution.
 *
 * The depth axis is left alone. Quantising it moves the near plane rather than
 * the texel grid, and buys nothing.
 *
 * DISARMED BY DEFAULT, AND IT HAS TO BE. The lane it writes to is named by hand
 * from the census report above, and a wrong offset means writing a rounded
 * float3 over something that is not a cascade origin. Nothing here guesses:
 * vp_cascade_snap_offset = -1 writes nothing at all. */
struct SnapSettings {
    int   offset;   /* byte offset of the FIRST cascade origin lane, -1 off */
    int   stride;   /* bytes between cascade entries */
    int   count;    /* how many cascades in the set */
    float texels;   /* shadow map resolution, one axis */
};

const SnapSettings& snap_cfg()
{
    static SnapSettings s;
    static bool loaded = false;
    if (!loaded)
    {
        loaded = true;
        s.offset = static_cast<int>(config_float("vp_cascade_snap_offset", -1.0f));
        s.stride = static_cast<int>(config_float("vp_cascade_snap_stride", 416.0f));
        s.count  = static_cast<int>(config_float("vp_cascade_snap_count", 6.0f));
        s.texels = config_float("vp_cascade_snap_texels", 2048.0f);
        if (s.stride < 16)   s.stride = 16;
        if (s.count  < 1)    s.count  = 1;
        if (s.texels < 16.0f) s.texels = 16.0f;
    }
    return s;
}

std::atomic<uint64_t> g_cSnapped{0};
std::atomic<uint64_t> g_cSnapNoMatrix{0};

/* The cascade's own matrix, found inside its entry. Returns the block offset or
   -1. Searched rather than configured: the origin is what the census names, and
   the matrix beside it is whichever 64 bytes in the same entry read as
   orthographic. */
int entry_matrix(const uint8_t* data, uint32_t bytes, uint32_t entry, uint32_t stride)
{
    const uint32_t end = (entry + stride <= bytes) ? entry + stride : bytes;

    for (uint32_t off = entry; off + 64 <= end; off += 16)
    {
        const float* m = reinterpret_cast<const float*>(data + off);
        if (finite16(m) && ortho_signature(m))
            return static_cast<int>(off);
    }
    return -1;
}

void cascade_snap(uint8_t* data, uint32_t bytes)
{
    const SnapSettings& s = snap_cfg();

    for (int c = 0; c < s.count; ++c)
    {
        const uint32_t lane  = static_cast<uint32_t>(s.offset) +
                               static_cast<uint32_t>(c) * static_cast<uint32_t>(s.stride);
        const uint32_t entry = static_cast<uint32_t>(c) *
                               static_cast<uint32_t>(s.stride);

        if (lane + 16 > bytes)
            break;

        const int mo = entry_matrix(data, bytes, entry,
                                    static_cast<uint32_t>(s.stride));
        if (mo < 0)
        {
            g_cSnapNoMatrix.fetch_add(1, std::memory_order_relaxed);
            continue;
        }

        const float* m = reinterpret_cast<const float*>(data + mo);
        float* o = reinterpret_cast<float*>(data + lane);

        if (!finite3(o))
            continue;

        /* The light basis, and how much world one unit of clip is worth along
           each of its axes. An orthographic row's LENGTH is clip units per
           metre, so its reciprocal is metres per clip unit, and the unit cube is
           two clip units across. */
        const Vec3 origin = { o[0], o[1], o[2] };
        Vec3 axis[3];
        float step[3];

        bool ok = true;
        for (int i = 0; i < 3; ++i)
        {
            const Vec3 row = { m[i * 4 + 0], m[i * 4 + 1], m[i * 4 + 2] };
            const float len = len3(row);
            if (!(len > 1e-9f))
            {
                ok = false;
                break;
            }
            axis[i].x = row.x / len;
            axis[i].y = row.y / len;
            axis[i].z = row.z / len;
            step[i] = (2.0f / s.texels) / len;
        }
        if (!ok)
            continue;

        /* Quantise laterally, leave the depth axis, rebuild. The basis is
           orthonormal by ortho_signature's own perpendicularity test, so
           projecting onto it and summing back is a round trip. */
        float coord[3];
        for (int i = 0; i < 3; ++i)
            coord[i] = dot3(origin, axis[i]);

        for (int i = 0; i < 2; ++i)
            coord[i] = floorf(coord[i] / step[i] + 0.5f) * step[i];

        o[0] = coord[0] * axis[0].x + coord[1] * axis[1].x + coord[2] * axis[2].x;
        o[1] = coord[0] * axis[0].y + coord[1] * axis[1].y + coord[2] * axis[2].y;
        o[2] = coord[0] * axis[0].z + coord[1] * axis[1].z + coord[2] * axis[2].z;

        g_cSnapped.fetch_add(1, std::memory_order_relaxed);
    }
}

/* Constant buffers that have never carried a main-view matrix stop being
   scanned. Without this, a per-object buffer uploaded a thousand times a frame
   is rescanned a thousand times to reach the same answer, and the scan shows up
   as frame time rather than as a bug.
 *
 * Direct-mapped and thread-local: collisions just evict, and a per-thread table
 * needs no synchronisation on a path that runs inside the driver's Unmap. */
constexpr int      kNoteSlots = 512;
constexpr uint32_t kMissLimit = 600;

/* THE RETIREMENT GATE IS WHY THE CASCADE CENSUS ONLY EVER SAW TWO BUFFERS.
 *
 * `hits` counts one thing: a MAIN-VIEW matrix found and patched in this buffer,
 * scored at the end of pass two. A buffer that carries no main-view matrix
 * therefore never scores, retires after kMissLimit uploads, and is scanned once
 * every 256 uploads thereafter.
 *
 * The orthographic census added in TEST 62 sits INSIDE that scan, downstream of
 * the gate. So it never had a chance at a dedicated shadow buffer - the exact
 * place a cascade set is most likely to live - and the two buffers it did report
 * are the only two that could have been reported: cb 1760, which is the main-view
 * buffer, and cb 6656, which holds the second reprojection site. Both stay
 * resident because of what else is in them, not because of their cascades.
 *
 * That is enough to put an asterisk on "the cascades are not fitted to the
 * camera - measured false rather than unproven". What was measured is that the
 * ortho blocks in the two buffers the scan can see do not move. A shadow buffer
 * retired 600 uploads into the run would look exactly the same from here: silent.
 *
 * `orthoHits` keeps a buffer resident for carrying CASCADES, on its own terms.
 * Deliberately not folded into `hits`: that counter also gates
 * build_other_eye_buffer, and a shadow buffer is not something to build a second
 * eye's copy of. Two reasons to stay, one reason to be duplicated. */
struct BufferNote
{
    const void* resource;
    uint32_t    misses;
    uint32_t    hits;
    uint32_t    orthoHits;
};

thread_local BufferNote t_notes[kNoteSlots] = {};

BufferNote& note_for(const void* resource)
{
    uintptr_t h = reinterpret_cast<uintptr_t>(resource);
    h ^= h >> 16;
    BufferNote& note = t_notes[(h >> 4) & (kNoteSlots - 1)];

    if (note.resource != resource)
    {
        note.resource = resource;
        note.misses = 0;
        note.hits = 0;
        note.orthoHits = 0;
    }
    return note;
}

/* Set for the duration of one second-eye pass over a private copy of a constant
   buffer. Thread-local because rgl uploads from more than one thread and the
   two passes must never see each other's selection. */
/* Where the second eye's copy of this buffer is written, and the camera to
   build it from. Both set only for the duration of one upload, by on_unmap.
   Null means the second eye is not wanted and nothing extra happens. */
thread_local uint8_t*        t_shadowOut = nullptr;
thread_local const VpFrame*  t_shadowOther = nullptr;

void scan_and_patch(uint8_t* data, size_t bytes, uint32_t byteWidth,
                    const void* resource)
{
    if (data == nullptr || bytes < 64)
        return;

    /* The live view's D belongs to ONE buffer and one upload. It was never
       cleared, so a buffer whose previous-frame slot was scanned WITHOUT a live
       view ahead of it would carry a D from an earlier frame - a stale rotation
       applied to a matrix it never belonged to. That is a plausible half of what
       TEST 105 saw as head resistance, and it is a landmine whether or not
       vp_prev_apply_d is on, so it is cleared per upload rather than left. */
    t_haveLatchD = false;

    Published published;
    if (!read_published(published))
    {
        g_cNoPublication.fetch_add(1, std::memory_order_relaxed);
        return;
    }

    /* THE SECOND EYE'S MATRICES, BUILT FROM THE SAME BYTES.
     *
     * When this is set, everything below runs exactly as it does for the eye the
     * engine is drawing, except that the replacement is built from the OTHER
     * eye's camera. It is set only by build_other_eye_buffer, which hands this a
     * private copy of the ORIGINAL bytes - never the engine's own buffer, and
     * never after the first patch has already run over them.
     *
     * That last point is the whole reason the copy is taken before the in-place
     * patch rather than after: substituting on top of an already-substituted
     * matrix would compose two eye deltas and put the second eye twice as far
     * out as it belongs. */
    const VpFrame& pub = published.frame;

    /* NOTHING LEFT TO LEARN AND NOTHING TO WRITE. See measure_settle_hits.
       Checked before note_for so a settled session does no per-buffer work at
       all - not the hash, not the miss accounting, nothing. */
    if (!cfg().enabled)
    {
        const uint32_t settle = measure_settle_hits();
        if (settle != 0 &&
            g_measAgree.load(std::memory_order_relaxed) >= settle)
        {
            if (!g_measSettledLogged.exchange(true, std::memory_order_relaxed))
            {
                float l = 0, r = 0, u = 0, d = 0;
                l = g_measTanL.load(std::memory_order_relaxed);
                r = g_measTanR.load(std::memory_order_relaxed);
                u = g_measTanU.load(std::memory_order_relaxed);
                d = g_measTanD.load(std::memory_order_relaxed);
                BVR_INFO("VP scan STOOD DOWN: the engine frustum agreed with "
                         "itself %u times running (%.1f x %.1f deg), and with "
                         "vp_patch = 0 there is nothing to write. The stored "
                         "frustum goes on serving xrEndFrame; the per-upload scan "
                         "does not run again. This is CPU handed back to the "
                         "frame, which is head-to-photon latency handed back with "
                         "it. vp_measure_settle = 0 keeps it scanning.",
                         settle,
                         (atanf(r) - atanf(l)) * 57.29578f,
                         (atanf(u) - atanf(d)) * 57.29578f);
            }
            return;
        }
    }

    /* THE DIRECTION LANES, MEASURED BEFORE ANYTHING ELSE HAPPENS TO THIS
       BUFFER - and ahead of the write-off below, which is the whole point.

       A buffer carrying corner rays and no view matrix never scores a hit, so
       it retires after kMissLimit and is scanned once in every 256 uploads
       from then on. That is exactly the buffer being looked for here, and
       running the census after the write-off would starve it of samples of
       precisely the thing it exists to find.

       Running it first also guarantees it differences the ENGINE's bytes
       against the engine's bytes. After pass one this buffer may hold our
       substituted view, and a census that differenced our writes against
       their own previous generation would be measuring this file. */
    ray_census(data, static_cast<uint32_t>(bytes), byteWidth, pub);

    BufferNote& note = note_for(resource);
    if (note.hits == 0 && note.orthoHits == 0 && note.misses >= kMissLimit &&
        !ray_target(byteWidth))
    {
        ++note.misses;

        /* Not written off for the whole run: re-probed every 256th upload, so a
           buffer that only carries the view matrix under some conditions still
           gets found eventually. */
        if ((note.misses & 0xFFu) != 0)
            return;
    }

    const Vec3 refDir = v3(pub.refDir[0], pub.refDir[1], pub.refDir[2]);
    const Vec3 refPos = v3(pub.refPos[0], pub.refPos[1], pub.refPos[2]);

    g_statScanned.fetch_add(1, std::memory_order_relaxed);

    bool cacheValid = false;
    Mat4 cacheEngine;
    Mat4 cachePatchedInverse;
    mat_identity(cacheEngine);
    mat_identity(cachePatchedInverse);

    /* The same D pass one substituted with, kept for pass four. */
    Mat4 cacheD;
    bool haveD = false;
    mat_identity(cacheD);

    int patchedHere = 0;

    /* Measure-only runs every step of the scan except the ones that WRITE. The
       recovery, the predicates and the frustum record are all still wanted; the
       memcpy back into the mapped buffer is the only thing that splits the frame,
       so it is the only thing suppressed. See Settings::measure. */
    /* WHICH BUFFERS MAY BE WRITTEN, AND WHY THIS IS NOT A DETAIL.
     *
     * The site census settles it with a number. Three sites are ever patched:
     *
     *     cb 1760 offset 240   hits  2361   the main view
     *     cb 1760 offset 304   hits  2361   the main view
     *     cb  128 offset   0   hits 12506
     *
     * cb 128 is written FIVE TIMES more often than the main view. It is a small
     * per-pass view buffer, re-uploaded for pass after pass, and every one of
     * those passes was having its camera replaced with the eye camera because
     * the matrix inside happens to look like the main view - same direction,
     * same position. The predicate cannot tell "the main view" from "a pass that
     * legitimately shares the main view's camera", and a shadow or screen-space
     * pass is exactly the second thing.
     *
     * That is consistent with what the headset reports: vp_patch = 0 makes the
     * sky and shadows stable and brings the floating back, because the floating
     * fix lives in the main-view substitution (cb 1760) and the shadow damage
     * lives in everything else the same switch turns on.
     *
     * vp_patch_only_bytes names the one buffer width allowed to be written.
     * 0 keeps the old behaviour of writing wherever the predicate matches. */
    const uint32_t onlyBytes = patch_only_bytes();
    const bool bufferOk = (onlyBytes == 0 || byteWidth == onlyBytes);

    /* A depth-only binding is a shadow map. Writing the eye camera into the
       constants that pass is about to consume fits the shadow map to the head,
       which is the reported symptom - and it is ours, not the engine's. */
    const bool passOk = !patch_rtv_only() ||
                        g_curHasRtv.load(std::memory_order_relaxed);

    const bool writeAllowed = cfg().enabled && bufferOk && passOk;

    /* Pass one: the world->clip matrices. Pass two needs one of these to exist
       before it can recognise a clip->world matrix, and the two are not
       guaranteed to be stored in that order within the buffer. */
    /* WHICH MAIN-VIEW MATRIX THIS IS, BY POSITION IN THE BUFFER.
     *
       Direction cannot tell the two apart. Requiring the current camera to FAIL
       never fired at all - "previous-frame 0" on every slot, every run - and
       taking whichever camera scored the higher dot went straight to the other
       extreme: 2551 of 2865 hits on BOTH slots classed as previous, because one
       frame at 90 Hz leaves the two dots inside noise of each other and the
       comparison is then a coin toss. Both slots getting the previous matrix is
       as wrong as both getting the current one.
     *
       The layout is the discriminator, and it has been completely stable: rgl
       writes the two main-view matrices 64 bytes apart in the same buffer, at
       240 and 304, and the note on Published::prev works out that the second is
       the previous frame's. This loop walks offsets in order, so the FIRST
       main-view match in a buffer is the current frame and any later one is the
       previous. A buffer carrying only one - cb 128 - keeps the current, which
       is what it should have.
     *
       Counted per scan, not per buffer, so a buffer that is re-uploaded several
       times a frame starts from the current matrix every time. */
    int mainViewSeen = 0;

    /* Orthographic blocks seen in this upload. Keeps a shadow buffer resident
       past kMissLimit - see the note on BufferNote::orthoHits. */
    int orthoHere = 0;

    /* Kept for pass three, which needs THIS frame's main view both as the engine
       wrote it and as we replaced it: the reprojection matrix is a product of a
       current and a previous view-projection, so reproducing it for our camera
       means rebuilding it from ours. The previous half comes from the carrier -
       see the ReprojPair note - not from the second slot in this buffer. */
    Mat4 engCur, ourCur;
    bool haveEngCur = false;

    /* See g_anatomyBuffers. Claim the whole buffer up front so the rows below
       belong to one upload rather than being interleaved with other scans. */
    const bool anatomy = (byteWidth == 1760) && (g_anatomyBuffers > 0);
    if (anatomy)
    {
        --g_anatomyBuffers;
        BVR_INFO("VP buffer anatomy: cb %u, reference camera (%.1f,%.1f,%.1f) "
                 "looking (%.3f,%.3f,%.3f). Every block below decodes as a view; "
                 "PATCH means we substitute it, SKIP means the engine's own "
                 "matrix survives and that pass keeps the flat camera.",
                 byteWidth, refPos.x, refPos.y, refPos.z,
                 refDir.x, refDir.y, refDir.z);
    }

    for (size_t off = 0; off + 64 <= bytes; off += 16)
    {
        float* raw = reinterpret_cast<float*>(data + off);

        /* Row-vector storage puts the w column at 3, 7, 11; transposed storage
           puts it at 12, 13, 14. If neither reads as a unit vector this block is
           not a world->clip matrix in either order, which is the answer for
           essentially every 16 bytes the engine ever uploads. */
        const bool maybeRow = unit_w_column(raw[3], raw[7], raw[11]);
        const bool maybeCol = unit_w_column(raw[12], raw[13], raw[14]);

        /* Before the view-matrix test, and independent of it: a precomposed
           reprojection matrix is near identity and passes neither w-column
           check. See the note on ProbeRow. */
        if (finite16(raw))
        {
            const float dev = identity_deviation(raw);
            if (dev >= 0.0f)
                probe_note(g_nearId, g_nearIdCount, byteWidth, off, false,
                           0.0f, 0.0f, dev);

            /* The cascades, recorded with how far their translation moved since
               this block was last seen. That number against the main view's own
               movement is the whole question: a cascade fitted to the camera
               moves with it, and a shadow map built from a camera that follows
               the head is shadows that follow the head. */
            if (ortho_signature(raw))
            {
                /* Scored whatever the row table decides below: a buffer earns
                   its place in the scan by carrying a cascade, even when all
                   kProbeRows rows are already spoken for. */
                ++orthoHere;

                float moved = 0.0f;

                for (int s = 0; s < kProbeRows; ++s)
                {
                    OrthoMotion& o = g_ortho[s];

                    if (o.used && (o.bytes != byteWidth || o.offset != off))
                        continue;

                    /* Read BEFORE the row is claimed: on a first sighting there
                       is no previous translation, and differencing against a
                       zeroed one would report the block's whole distance from
                       the origin as a single frame of movement. */
                    const bool seenBefore = o.used;

                    if (!o.used)
                    {
                        o.used = true;
                        o.bytes = byteWidth;
                        o.offset = static_cast<uint32_t>(off);
                    }

                    float t[3] = { 0.0f, 0.0f, 0.0f };
                    ortho_translation(raw, t);

                    if (seenBefore)
                    {
                        const float dx = t[0] - o.tx;
                        const float dy = t[1] - o.ty;
                        const float dz = t[2] - o.tz;
                        moved = sqrtf(dx * dx + dy * dy + dz * dz);
                        if (moved > o.maxMoved)
                            o.maxMoved = moved;
                    }

                    o.tx = t[0]; o.ty = t[1]; o.tz = t[2];
                    ++o.hits;
                    break;
                }
            }
        }

        if (!maybeRow && !maybeCol)
            continue;

        if (!finite16(raw))
            continue;

        for (int variant = 0; variant < 2; ++variant)
        {
            if (variant == 0 ? !maybeRow : !maybeCol)
                continue;

            Mat4 vp;
            memcpy(vp.m, raw, sizeof(vp.m));
            if (variant == 1)
                mat_transpose(vp, vp);

            Frustum f;
            if (!recover(vp, f))
                continue;

            /* Match against the current camera AND the previous one, and use
               whichever this matrix actually belongs to. A per-view buffer
               carries both; see the note on Published::prev. */
            const float dirDot = dot3(f.forward, refDir);
            const float dPos = len3(sub3(f.position, refPos));
            /* At the world origin means skybox - see Settings::sky. */
            const bool atOrigin = cfg().sky && len3(f.position) <= cfg().skyRadius;

            const bool curOk = dirDot >= cfg().dirTol &&
                               (!cfg().requirePos || dPos <= cfg().posTol || atOrigin);

            float prevDot = -1.0f;
            float prevPos = 1e30f;
            bool prevOk = false;
            if (published.havePrev != 0)
            {
                const Vec3 pDir = v3(published.prev.refDir[0], published.prev.refDir[1],
                                     published.prev.refDir[2]);
                const Vec3 pPos = v3(published.prev.refPos[0], published.prev.refPos[1],
                                     published.prev.refPos[2]);
                prevDot = dot3(f.forward, pDir);
                prevPos = len3(sub3(f.position, pPos));
                prevOk = prevDot >= cfg().dirTol &&
                         (!cfg().requirePos || prevPos <= cfg().posTol || atOrigin);
            }

            if (anatomy)
            {
                /* DOES THIS BLOCK FOLLOW THE HEAD?
                 *
                   Five theories about the shadows have died, and every one of
                   them was reasoning about a pass nobody had measured. The
                   anatomy names the suspect plainly - a camera kilometres away
                   with a narrow frustum is a directional light's cascade - and
                   prints where it is, once. What it has never printed is
                   whether it MOVES, which is the entire question: a cascade
                   fitted to the camera tracks the head, and a shadow map built
                   from a camera that tracks the head is shadows that follow it.

                   So: how far this block's own camera travelled since the last
                   time this line was printed, next to how far OUR eye travelled
                   over the same interval. Two numbers, and the ratio between
                   them answers it without a frame capture. Near zero means the
                   block is anchored to the world and is not the shadow camera.
                   Near the eye's own movement means it is riding the head, and
                   the fix is to give that pass a camera that does not. */
                float moved = -1.0f;
                float eyeMoved = -1.0f;

                for (int s = 0; s < kSiteRows; ++s)
                {
                    SiteMotion& m = g_siteMotion[s];

                    if (m.used && (m.bytes != byteWidth || m.offset != off))
                        continue;
                    if (!m.used)
                    {
                        m.used = true;
                        m.bytes = byteWidth;
                        m.offset = static_cast<uint32_t>(off);
                    }
                    else
                    {
                        const float dx = f.position.x - m.px;
                        const float dy = f.position.y - m.py;
                        const float dz = f.position.z - m.pz;
                        moved = sqrtf(dx * dx + dy * dy + dz * dz);

                        const float ex = pub.eyeCamera[12] - m.ex;
                        const float ey = pub.eyeCamera[13] - m.ey;
                        const float ez = pub.eyeCamera[14] - m.ez;
                        eyeMoved = sqrtf(ex * ex + ey * ey + ez * ez);
                    }

                    m.px = f.position.x; m.py = f.position.y; m.pz = f.position.z;
                    m.ex = pub.eyeCamera[12];
                    m.ey = pub.eyeCamera[13];
                    m.ez = pub.eyeCamera[14];
                    break;
                }

                BVR_INFO("    offset %4u %-10s pos (%8.1f,%8.1f,%8.1f) d(ref) %8.2f m | "
                         "fwd (%6.3f,%6.3f,%6.3f) dot %.4f | %5.1f x %5.1f deg | %s"
                         " | moved %.3f m vs eye %.3f m",
                         static_cast<unsigned>(off),
                         variant == 1 ? "transposed" : "row-vector",
                         f.position.x, f.position.y, f.position.z, dPos,
                         f.forward.x, f.forward.y, f.forward.z, dirDot,
                         (atanf(f.tanRight) - atanf(f.tanLeft)) * 57.2957795f,
                         (atanf(f.tanUp) - atanf(f.tanDown)) * 57.2957795f,
                         (curOk || prevOk) ? "PATCH" : "SKIP",
                         moved, eyeMoved);
            }

            if (!curOk && !prevOk)
            {
                /* On the record, so a view matrix we decline is visible as a
                   decision rather than as silence. See the note on ProbeRow. */
                probe_note(g_rejects, g_rejectCount, byteWidth, off, variant == 1,
                           dirDot > prevDot ? dirDot : prevDot,
                           dPos < prevPos ? dPos : prevPos, 0.0f);

                /* The skybox suspect: a projection whose camera sits at the world
                   ORIGIN. Reported on its own budget and without the dot > 0.5
                   filter the near-miss line uses, because the whole point is that
                   we do not yet know which way this thing faces. */
                if (g_originBudget > 0 && len3(f.position) < 5.0f)
                {
                    --g_originBudget;
                    BVR_INFO("VP ORIGIN-CENTRED candidate (skybox suspect): cb %u offset %u "
                             "%s | fwd (%.3f,%.3f,%.3f) dot(ref) %.4f | tan L%.3f R%.3f "
                             "U%.3f D%.3f = %.1f x %.1f deg | ref dir (%.3f,%.3f,%.3f)",
                             byteWidth, static_cast<unsigned>(off),
                             variant == 1 ? "transposed" : "row-vector",
                             f.forward.x, f.forward.y, f.forward.z, dirDot,
                             f.tanLeft, f.tanRight, f.tanUp, f.tanDown,
                             (atanf(f.tanRight) - atanf(f.tanLeft)) * 57.2957795f,
                             (atanf(f.tanUp) - atanf(f.tanDown)) * 57.2957795f,
                             refDir.x, refDir.y, refDir.z);
                }

                /* Worth seeing once or twice if nothing ever matches: it says the
                   matrices are there and only the predicate is wrong. */
                if (g_nearMissBudget > 0 && dirDot > 0.5f)
                {
                    --g_nearMissBudget;
                    BVR_INFO("VP near miss: cb %u offset %u dot %.4f (need %.4f), "
                             "pos (%.1f,%.1f,%.1f) d(ref) %.2f vs ref (%.1f,%.1f,%.1f).",
                             byteWidth, static_cast<unsigned>(off), dirDot,
                             cfg().dirTol, f.position.x, f.position.y, f.position.z,
                             dPos, refPos.x, refPos.y, refPos.z);
                }
                continue;
            }

            /* The current frame wins whenever it fits AT ALL. The first version
               of this line broke the tie on which reference was nearer, with the
               reasoning that when both fit "the two frames are the same pose and
               the choice does not matter anyway".
             *
             * That reasoning is wrong, and the run that followed proved it. Under
               AFR consecutive frames are different EYES, so the two references
               are never the same pose - they are half an IPD and one head-motion
               step apart. With a still camera both distances are near zero and
               the comparison is decided by float noise, so about half of every
               frame's view matrices were rewritten with the OTHER EYE's camera.
               The site census showed it exactly: 52% "previous" at both offsets,
               where a real motion-vector matrix would be ~100% at one offset and
               ~0% at the other. Half a frame drawn from each eye is the black
               flicker.
             *
               When the camera really is still, the engine's two matrices are
               numerically identical anyway and patching both as "current" is
               correct. When it is moving, the previous-frame matrix genuinely
               fails the current test, and only then is it treated as previous. */
            /* WHICHEVER IT MATCHES BETTER, NOT ONLY WHEN THE CURRENT FAILS.
             *
               This read `prevOk && !curOk`, which cannot fire. The two cameras
               are one frame apart at 90 Hz, so a previous-frame matrix still
               clears the current camera's 0.990 tolerance comfortably - curOk is
               true for both slots, and the site table proved it: offset 240 and
               offset 304 with identical hit counts and "previous-frame 0" on
               every single run.
             *
               So both slots were getting the CURRENT eye's matrix, the engine was
               told nothing moved between frames, and everything it tracks
               temporally - sun, sky, cloud, dust, the sun's shadows - lost its
               motion vectors while static terrain stayed correct. That is the
               failure the note on Published::prev predicted in as many words.
             *
               Comparing the two dots decides it properly. A matrix belonging to
               the previous frame aligns better with the previous camera than with
               this one, and vice versa. With a still head the two are within
               noise of each other and the choice does not matter, because the
               matrices are equally close then too. With a moving head - which is
               the only time motion vectors matter - the difference is real and
               the answer is right. */
            /* The first main-view matrix in this buffer is the current frame's,
               any later one is the previous frame's. See mainViewSeen. */
            const bool usePrev = cfg().matchPrev && prevOk && mainViewSeen > 0;
            ++mainViewSeen;
            const VpFrame& use = usePrev ? published.prev : pub;

            /* LEAVE THE PREVIOUS-FRAME SLOT ALONE ENTIRELY.
             *
             * "It only happens when vp_patch is on" is the constraint that
             * settles what this bug is, and it rules out the eye alternation on
             * its own: the engine's camera alternates 65 mm every frame at
             * vp_patch = 0 as well, and characters are stable there.
             *
             * So it is not the alternation, and TEST 98 has already shown it is
             * not displacement either - the position delta is 0.0 mm and the
             * substitution is a near-no-op by design, because afr_camera_mode =
             * engine has already put the camera on the eye. Which leaves the
             * only other thing vp_patch does: it OVERWRITES THE TEMPORAL SLOTS.
             * The previous-frame matrix here, and the reprojection matrix in
             * pass three. Both are motion-vector machinery and nothing else.
             *
             * And that is an asymmetry the mod cannot see. Overwriting the
             * camera half of a motion vector while the engine keeps its own
             * per-object cached previous transforms makes the two halves
             * describe different moments. Static geometry survives it - both its
             * ends come from these two matrices. A dynamic entity does not: its
             * camera term is now ours and its object term is still the engine's.
             *
             * Which is exactly the reported set - characters and everything a
             * character interacts with - exactly the reported condition - only
             * with a temporal upscaler to consume the vectors - and exactly the
             * reported switch - only with vp_patch on.
             *
             * vp_write_prev_slot = 0 stops writing it at all. Note that this is
             * NOT vp_match_prev = 0, which writes the CURRENT matrix into the
             * slot and tells the engine nothing moved - the failure recorded at
             * the top of this file, where sun, sky, cloud and dust lost their
             * motion vectors. Leaving the slot untouched keeps the engine's own
             * previous matrix, which is already consistent with its own caches. */
            /* CARRY THE SAME D ONTO THE ENGINE'S OWN PREVIOUS MATRIX.
             *
             * The ground smears because static motion vectors are built from
             * this buffer's two matrices and only ONE of them is in the latched
             * frame. We rewrite the current view with D and leave the previous
             * view as the engine wrote it, so every static pixel gets a motion
             * vector carrying a head turn that never happened. It shows on the
             * ground first because that is where depth changes fastest per pixel
             * and where the detail is finest - this file's own depth-layer note
             * says the same thing about the same surface.
             *
             * The obvious repair - write the previous slot too - is what TEST 99
             * had and what shook the characters, but NOT for the reason it
             * looked like. That code built the slot from published.prev, a
             * different pose entirely: a frame older AND, under AFR, the other
             * eye. It did not move the previous view into the latched frame, it
             * replaced it with something else.
             *
             * This does the actual thing instead. Take the engine's OWN previous
             * matrix, exactly as it wrote it, and apply the SAME D the current
             * view got. Both ends of the motion vector are then in one frame:
             *
             *     MV = D*VPcur_eng - D*VPprev_eng = D*(VPcur_eng - VPprev_eng)
             *
             * which is the engine's true camera motion expressed in the frame
             * the image was actually rendered in. Static geometry - the ground -
             * cancels exactly and stops smearing.
             *
             * And it leaves dynamic entities alone. Their velocities never come
             * from this buffer; they are composed per object against the engine's
             * camera, so they mismatch the latched image by D whether or not this
             * slot is written. Unchanged, therefore, from the state that made
             * them stable.
             *
             * The current view is always scanned before the previous one - see
             * mainViewSeen - so t_latchD is this frame's, from this buffer. */
            if (usePrev && prev_apply_d() && t_haveLatchD)
            {
                if (writeAllowed && !pass_is_shadow())
                {
                    Mat4 carried;
                    mat_mul(carried, t_latchD, vp);
                    if (finite16(carried.m))
                    {
                        Mat4 outPrev = carried;
                        if (variant == 1)
                            mat_transpose(outPrev, outPrev);
                        memcpy(raw, outPrev.m, sizeof(outPrev.m));
                        g_statVp.fetch_add(1, std::memory_order_relaxed);
                        g_statPrevCarried.fetch_add(1, std::memory_order_relaxed);
                    }
                }
                continue;
            }

            if (usePrev && !write_prev_slot())
            {
                g_statPrevSkipped.fetch_add(1, std::memory_order_relaxed);
                continue;
            }

            if (usePrev)
                g_statPrev.fetch_add(1, std::memory_order_relaxed);

            if (cfg().scan)
                log_site(byteWidth, off, variant == 1, f, use,
                         usePrev ? prevDot : dirDot, usePrev);

            /* The engine's real frustum, for xrEndFrame to submit. Published from
               here rather than derived in managed code because this is measured
               off the matrix the GPU is about to use, not off a camera property
               the engine may have already overwritten.
             *
             * Guarded on the camera POSITION, and hard-coded rather than left to
             * vp_require_pos. One run matched a transposed matrix sitting at the
             * world origin with a 29.4 x 50.1 degree frustum - a sub-view of some
             * kind - and because it was scanned last it became the "engine
             * frustum" the compositor was told about. Submitting a 29 degree fov
             * for an image rendered at 150 is a catastrophe, and it is not the
             * patch predicate's job to protect against it. Only a matrix whose
             * camera is genuinely WHERE THE ENGINE CAMERA IS gets to define what
             * we submit. */
            if (dPos <= 5.0f)
            {
                /* THE FRUSTUM IS A CONSTANT, AND WE WERE RE-DERIVING IT 76,000
                   TIMES A SECOND.
                 *
                   Count consecutive agreements with what is already stored. Once
                   the engine has told us the same frustum often enough, there is
                   nothing left for this scan to learn, and with writes disabled
                   there is nothing for it to do either - see measurement_settled.
                   The stored tangents keep serving submitted_fov exactly as
                   before, so the frustum stays correct with none of the cost. */
                const float pl = g_measTanL.load(std::memory_order_relaxed);
                const float pr = g_measTanR.load(std::memory_order_relaxed);
                const float pu = g_measTanU.load(std::memory_order_relaxed);
                const float pd = g_measTanD.load(std::memory_order_relaxed);

                const bool same = g_measValid.load(std::memory_order_relaxed) &&
                                  fabsf(pl - f.tanLeft)  < 1e-4f &&
                                  fabsf(pr - f.tanRight) < 1e-4f &&
                                  fabsf(pu - f.tanUp)    < 1e-4f &&
                                  fabsf(pd - f.tanDown)  < 1e-4f;

                if (same)
                    g_measAgree.fetch_add(1, std::memory_order_relaxed);
                else
                    g_measAgree.store(0, std::memory_order_relaxed);

                g_measTanL.store(f.tanLeft,  std::memory_order_relaxed);
                g_measTanR.store(f.tanRight, std::memory_order_relaxed);
                g_measTanU.store(f.tanUp,    std::memory_order_relaxed);
                g_measTanD.store(f.tanDown,  std::memory_order_relaxed);
                g_measValid.store(true, std::memory_order_release);

                /* Where this frame's frustum centre sits, as a fraction of the
                   half-width - which is NDC, because NDC is exactly that
                   fraction. Deviation from the running average is the jitter;
                   the average absorbs any real off-centring. */
                const float spanX = f.tanRight - f.tanLeft;
                const float spanY = f.tanUp    - f.tanDown;

                if (spanX > 1e-6f && spanY > 1e-6f)
                {
                    const float cx = (f.tanRight + f.tanLeft) / spanX;
                    const float cy = (f.tanUp    + f.tanDown) / spanY;

                    if (g_centreSeen == 0)
                    {
                        g_centreAvgX = cx;
                        g_centreAvgY = cy;
                    }

                    const float devX = cx - g_centreAvgX;
                    const float devY = cy - g_centreAvgY;

                    g_centreAvgX += kCentreAlpha * devX;
                    g_centreAvgY += kCentreAlpha * devY;

                    if (g_centreSeen < kCentreWarmup)
                    {
                        ++g_centreSeen;
                    }
                    else
                    {
                        g_jitterX.store(devX, std::memory_order_relaxed);
                        g_jitterY.store(devY, std::memory_order_relaxed);
                        g_jitterValid.store(true, std::memory_order_release);
                    }
                }
            }

            Mat4 replacement;
            Mat4 dHere;
            mat_identity(dHere);
            if (!build_replacement(vp, f, use, replacement, byteWidth == 1760, usePrev,
                                   &dHere) ||
                !finite16(replacement.m))
                break;

            if (!cacheValid)
            {
                cacheEngine = vp;
                cacheValid = mat_inverse(cachePatchedInverse, replacement);
            }

            /* mainViewSeen was incremented above, so 1 is the first main view in
               this buffer. In the engine's own storage order, already
               un-transposed.
             *
               The second one is NOT taken as the previous frame any more. The
               probe measured offsets 240 and 304 as bit-identical on every line
               it ever logged, so treating 304 as a one-frame-old view was feeding
               pass three its own current matrix twice. See the ReprojPair note. */
            if (mainViewSeen == 1) { engCur = vp; ourCur = replacement; haveEngCur = true; }

            /* AND D, FROM THE SAME SLOT, FOR THE SAME REASON.

               This took cacheD on EVERY main-view match, so it ended the loop
               holding whatever the LAST match wrote - offset 304, the previous-
               frame slot, which build_replacement builds differently because it
               is passed usePrev. Pass four then rotated the ray basis by a D
               that was a frame stale and a different size, and did it across
               the ~9 uploads of cb 1760 each frame with no guarantee which one
               won.

               In the headset that is "dynamic shadows flicker and move faster":
               over-rotation from the wrong D, and flicker from it not being
               the same wrong D twice running. The lanes are the CURRENT frame's
               rays and they want the CURRENT frame's D, which is the first main
               view in the buffer - the same slot, chosen the same way, as the
               engCur above. */
            if (mainViewSeen == 1)
            {
                cacheD = dHere;
                haveD  = true;

                /* And for the buffers that carry no main view of their own. */
                g_lastD = dHere;
                g_lastDFrame.store(present_count(), std::memory_order_relaxed);
                g_lastDValid.store(true, std::memory_order_release);

                /* The recovered basis, for the ray report to decode lanes
                   with - also the current frame's, so the R/U/F dots in the
                   report describe the camera the lanes were built from. */
                g_engBasis[0] = f.right.x;   g_engBasis[1] = f.right.y;   g_engBasis[2] = f.right.z;
                g_engBasis[3] = f.up.x;      g_engBasis[4] = f.up.y;      g_engBasis[5] = f.up.z;
                g_engBasis[6] = f.forward.x; g_engBasis[7] = f.forward.y; g_engBasis[8] = f.forward.z;
                g_engPos[0] = f.position.x;
                g_engPos[1] = f.position.y;
                g_engPos[2] = f.position.z;
                g_engBasisValid.store(true, std::memory_order_relaxed);
            }

            /* Counted HERE, on a confirmed main-view match, not once per upload.
               Counting uploads put 13,472 rows a report into the table when only
               2,434 of them carried a matrix worth patching, so the contexts that
               mattered were buried under the ones that were never candidates. */
            tgt_ctx_note();

            /* The shadow map gets the engine's own camera. Everything else that
               reached this point still gets the eye. */
            if (writeAllowed && !pass_is_shadow())
            {
                Mat4 outMat = replacement;
                if (variant == 1)
                    mat_transpose(outMat, outMat);
                memcpy(raw, outMat.m, sizeof(outMat.m));

                g_statVp.fetch_add(1, std::memory_order_relaxed);

                /* THE SECOND EYE, AT THE SAME SITE, IN THE SAME PASS.
                 *
                   This used to be a second call to scan_and_patch over a private
                   copy, with a thread-local telling it to build from the other
                   eye. It produced a right eye that was raw but FROZEN - stuck
                   at the spawn point while the character walked away - and the
                   reason is that this function is not a pure transform. It
                   carries the buffer's hit and miss counts, the reprojection
                   cache, the site census and the upscaler's jitter baseline, and
                   running it twice per upload corrupted every one of them. The
                   patcher was being fed its own second pass as though it were
                   new engine data.

                   Doing it here costs one extra build_replacement at a site the
                   scan has ALREADY identified, and mutates nothing. The offset
                   is known to hold a main view because the first eye was just
                   written to it. */
                if (t_shadowOut != nullptr && t_shadowOther != nullptr)
                {
                    Mat4 otherRep;
                    if (build_replacement(vp, f, *t_shadowOther, otherRep, byteWidth == 1760, false) &&
                        finite16(otherRep.m))
                    {
                        Mat4 otherOut = otherRep;
                        if (variant == 1)
                            mat_transpose(otherOut, otherOut);

                        const size_t at = static_cast<size_t>(
                            reinterpret_cast<const uint8_t*>(raw) - data);
                        if (at + sizeof(otherOut.m) <= bytes)
                            memcpy(t_shadowOut + at, otherOut.m, sizeof(otherOut.m));
                    }
                }
            }

            /* Counted either way: it is what keeps this buffer off the miss cache,
               and a measured frame is still a frame we got what we came for. */
            ++patchedHere;

            /* Past the whole matrix, not just past this 16-byte row. The next
               three offsets overlap what was just written, and a window
               straddling a patched matrix is not data the engine ever wrote. */
            off += 48;
            break;
        }
    }

    if (patchedHere > 0)
        ++note.hits;
    else
        ++note.misses;

    /* Keeping a shadow buffer resident costs scan time on every one of its
       uploads, and the only consumer of that was the cascade census. With the
       census off, retirement behaves exactly as it did before TEST 63. */
    if (census_wanted() && orthoHere > 0 && note.orthoHits < kMissLimit)
        ++note.orthoHits;

    /* The lane difference, over the whole buffer. Runs after the passes above so
       it reads the ORIGINAL bytes wherever nothing was patched, and it is the
       only measurement here that needs a buffer's previous contents rather than
       one block's. See the note on LaneAcc. */
    {
        const bool hasOrtho = (orthoHere > 0 || note.orthoHits > 0);

        lane_census(data, static_cast<uint32_t>(bytes), byteWidth, hasOrtho, refPos);

        /* And the write, when a lane has been named. Last thing to touch the
           buffer, after every measurement has read the engine's own bytes -
           snapping first would have the census differencing our rounding
           against itself and reporting a cascade that has stopped moving
           because we stopped it. */
        if (hasOrtho && writeAllowed && snap_cfg().offset >= 0)
            cascade_snap(data, static_cast<uint32_t>(bytes));
    }

    /* Pass three: the precomposed reprojection matrix - identified from the data,
       then substituted.
     *
       The census found exactly one near-identity block that drifts with head
       speed, cb 1760 offset 752, and the note on ProbeRow works out what such a
       block has to be: the single matrix an engine uses to reproject its history
       buffer when it has no per-pixel velocity to do it with. Everything that
       reads it - sky, sun, cloud, hair, particles, screen-space shadows - keeps
       compensating for the camera the engine thinks it has while ignoring the
       head, which is what "it tries not to move with my head, but follows when I
       move fast" is on screen.
     *
       It is a product of a current and a previous view-projection, so making it
       ours means rebuilding it from ours. Which product is not guessed at: the
       four orderings are tested against what is actually stored, in both
       transpositions, and the engine names its own convention. See kReprojAgree
       for why a match has to be decisive and repeated before anything is written,
       and the ReprojPair note for why the previous matrix now comes from the
       previous FRAME rather than from offset 304. */
    if (haveEngCur && writeAllowed)
    {
        /* The previous frame's pair, published at the last frame boundary. Two
           frames of slack: one is the normal case, two covers a Present that
           landed between this buffer and the roll. Anything older is a load
           screen or a stall, and reprojecting across one is worse than leaving
           the engine's own matrix alone. */
        const uint32_t slot = g_reprojIndex.load(std::memory_order_acquire) & 1u;

        /* PER EYE OR PER FRAME - the two accounts this file used to give at
           once. See reproj_same_eye. Same side means the pair is two frames
           back rather than one, so the age gate opens by one generation. */
        const bool sameEye = reproj_same_eye();
        const int  eyeIdx  = (pub.eye == 1) ? 1 : 0;

        const uint64_t now = g_reprojFrame.load(std::memory_order_relaxed);

        /* Measure the disagreement before choosing a side of it. Both pairs are
           in hand here for nothing, so the number that decides whether this
           setting matters at all gets printed rather than reasoned about. */
        {
            const uint64_t eyeBorn = g_reprojByEye[eyeIdx].frame.load(std::memory_order_acquire);
            const uint64_t frmBorn = g_reproj[slot].frame.load(std::memory_order_acquire);
            if (eyeBorn != 0 && frmBorn != 0)
            {
                float worst = 0.0f;
                for (int i = 0; i < 16; ++i)
                {
                    const float d = fabsf(g_reprojByEye[eyeIdx].our.m[i] -
                                          g_reproj[slot].our.m[i]);
                    if (d > worst) worst = d;
                }
                const uint64_t fixed = static_cast<uint64_t>(worst * 1000000.0f);
                uint64_t seen = g_reprojEyeDelta.load(std::memory_order_relaxed);
                while (fixed > seen &&
                       !g_reprojEyeDelta.compare_exchange_weak(seen, fixed,
                                                               std::memory_order_relaxed))
                { }
            }
        }

        /* The same eye's pair when it is fresh enough, the previous frame's when
           it is not - a stale pair is worse than the wrong convention, and this
           is counted so the fallback cannot hide. */
        bool useEye = sameEye;
        if (sameEye)
        {
            const uint64_t eyeBorn = g_reprojByEye[eyeIdx].frame.load(std::memory_order_acquire);
            if (eyeBorn == 0 || now < eyeBorn || (now - eyeBorn) > 3u)
            {
                useEye = false;
                g_statReprojEyeAged.fetch_add(1, std::memory_order_relaxed);
            }
        }

        const ReprojPair& carried = useEye ? g_reprojByEye[eyeIdx] : g_reproj[slot];
        const uint64_t maxAge = useEye ? 3u : 2u;

        const uint64_t born = carried.frame.load(std::memory_order_acquire);

        if (born != 0 && now >= born && (now - born) <= maxAge)
        {
            Mat4 invEngCur, invEngPrev, invOurCur, invOurPrev;
            if (mat_inverse(invEngCur, engCur) && mat_inverse(invEngPrev, carried.eng) &&
                mat_inverse(invOurCur, ourCur) && mat_inverse(invOurPrev, carried.our))
            {
                Mat4 engCand[4], ourCand[4];
                reproj_candidates(engCur, carried.eng, invEngCur, invEngPrev, engCand);
                reproj_candidates(ourCur, carried.our, invOurCur, invOurPrev, ourCand);

                const int32_t latched = g_reprojForm.load(std::memory_order_acquire);

                /* CACHED FOR THE BUFFERS THAT CARRY NO MAIN VIEW.
                 *
                   Everything in this block needs engCur, which is recovered
                   from a main-view matrix found in the buffer being uploaded -
                   and only cb 1760 ever carries one. cb 6656 does not, so this
                   whole pass is skipped for it, and the second reprojection site
                   living there could never be reached from in here.

                   The candidates do not belong to the buffer, though. They are
                   this frame's cameras, engine and ours, and they are just as
                   valid for a sky pass uploaded moments later. Cached here and
                   applied out there, once per buffer, wherever the census says
                   the same form is stored. */
                if (latched >= 0)
                {
                    g_reprojEngCached = engCand[latched];
                    g_reprojOurCached = ourCand[latched];
                    g_reprojCandCached.store(true, std::memory_order_release);
                }

                /* LATCHED: go straight to the identified block. Searching for it
                   again every upload would put an identity test on every 16-byte
                   offset of every buffer carrying a main view, forever - the
                   search below is affordable precisely because it stops. */
                if (latched >= 0)
                {
                    const size_t off = g_reprojOffset.load(std::memory_order_relaxed);
                    float* raw = reinterpret_cast<float*>(data + off);

                    if (byteWidth == g_reprojCb.load(std::memory_order_relaxed) &&
                        off + 64 <= bytes && finite16(raw))
                    {
                        /* Still the form we identified? A layout change has to stop
                           the substitution rather than corrupt whatever moved in,
                           so the engine's own value is re-checked every time.
                         *
                           RAW drift, not identity_deviation. That one answers "could
                           these unknown bytes be a reprojection matrix" and returns
                           -1 outside a +/-0.25 band - the right question while
                           SEARCHING and the wrong one here, where the site is already
                           identified and the only question is how far it has moved.
                           Its own comment says exactly this. The band was rejecting
                           the block precisely when the head moved far enough to push
                           it past 0.25, which is when the correction matters most,
                           and it is the reason the first run wrote once in five
                           seconds and declined 441 times. */
                        const float dev = raw_identity_drift(raw);
                        Mat4 obs;  memcpy(obs.m, raw, sizeof(obs.m));
                        Mat4 obsT; mat_transpose(obsT, obs);

                        const int xpose = g_reprojXpose.load(std::memory_order_relaxed);
                        const float err = mat_max_diff(engCand[latched], xpose ? obsT : obs);

                        if (dev < kReprojMinDrift)
                        {
                            /* A still head. The block sits at identity and there is
                               nothing to correct. */
                            g_statReprojStill.fetch_add(1, std::memory_order_relaxed);
                        }
                        else if (err > kReprojExplain * dev)
                        {
                            /* The engine is no longer storing what we identified -
                               a layout change, or our own write being read back. */
                            g_statReprojSkip.fetch_add(1, std::memory_order_relaxed);
                            reproj_sample(dev, err, false);
                        }
                        else if (!cfg().reprojPatch)
                        {
                            /* IDENTIFIED, EXPLAINED, AND DELIBERATELY NOT WRITTEN.
                             *
                               vp_reproj_patch = 0 keeps the whole of pass three -
                               identification, the decisive test, the census and
                               this per-frame agreement check - and suppresses only
                               the memcpy. That is what makes it a clean single
                               variable against the "sky and shadows follow the
                               head" regression: everything still measures, and the
                               one new write from 135b928 that lands on those exact
                               passes stops.
                             *
                               vp_reproj_band = 0.25 was the old way to get here and
                               it is not equivalent - it works by starving
                               identification, so it also blinds the instrument, and
                               a run where identification happened to succeed would
                               substitute anyway. */
                            g_statReprojHeld.fetch_add(1, std::memory_order_relaxed);
                            reproj_sample(dev, err, true);
                        }
                        else
                        {
                            Mat4 outMat = ourCand[latched];
                            if (xpose)
                                mat_transpose(outMat, outMat);
                            memcpy(raw, outMat.m, sizeof(outMat.m));
                            g_statReproj.fetch_add(1, std::memory_order_relaxed);
                            reproj_sample(dev, err, true);
                        }
                    }

                }
                /* IDENTIFYING: walk the buffer looking for the one block whose
                   drift a candidate explains. Runs only until the form latches. */
                else for (size_t off = 0; off + 64 <= bytes; off += 16)
                {
                    float* raw = reinterpret_cast<float*>(data + off);
                    if (!finite16(raw))
                        continue;

                    const float dev = identity_deviation(raw);
                    if (dev < 0.0f)
                        continue;

                    Mat4 obs;  memcpy(obs.m, raw, sizeof(obs.m));
                    Mat4 obsT; mat_transpose(obsT, obs);

                    int   bestI = -1, bestT = 0;
                    float bestErr = 1e30f;
                    for (int i = 0; i < 4; ++i)
                        for (int t = 0; t < 2; ++t)
                        {
                            const float err = mat_max_diff(engCand[i], t ? obsT : obs);
                            if (err < bestErr) { bestErr = err; bestI = i; bestT = t; }
                        }

                    /* Decisive means the candidate ACCOUNTS FOR the drift. A block
                       that has barely moved cannot tell four near-identity matrices
                       apart, and a "match" whose error is the size of the drift is a
                       match against identity - the exact way this probe fooled
                       itself while engPrev came from offset 304. */
                    const bool decisive = bestI >= 0 && dev >= kReprojMinDrift &&
                                          bestErr <= kReprojExplain * dev;

                    if (decisive)
                    {
                        /* Agreement has to be CONSECUTIVE at one site, so a stray
                           near-identity block cannot accumulate a score in between
                           the real one's frames. */
                        const bool same =
                            g_reprojAgree.load(std::memory_order_relaxed) > 0 &&
                            g_reprojCb.load(std::memory_order_relaxed) == byteWidth &&
                            g_reprojOffset.load(std::memory_order_relaxed) ==
                                static_cast<uint32_t>(off) &&
                            g_reprojCandidate.load(std::memory_order_relaxed) == bestI &&
                            g_reprojXpose.load(std::memory_order_relaxed) == (bestT ? 1 : 0);

                        if (!same)
                        {
                            g_reprojCb.store(byteWidth, std::memory_order_relaxed);
                            g_reprojOffset.store(static_cast<uint32_t>(off),
                                                 std::memory_order_relaxed);
                            g_reprojCandidate.store(bestI, std::memory_order_relaxed);
                            g_reprojXpose.store(bestT ? 1 : 0, std::memory_order_relaxed);
                            g_reprojAgree.store(1, std::memory_order_relaxed);
                        }
                        else
                        {
                            const int32_t n =
                                g_reprojAgree.fetch_add(1, std::memory_order_relaxed) + 1;
                            if (n >= kReprojAgree)
                            {
                                BVR_INFO("VP reprojection matrix IDENTIFIED: cb %u offset %u "
                                         "%s stores %s, agreeing %d samples running with max "
                                         "element error %.6f against a drift of %.5f. "
                                         "Substituting the same form built from OUR camera "
                                         "from here on - this is the matrix the sky, sun, "
                                         "cloud, hair, particles and screen-space shadows "
                                         "reproject their history with.",
                                         byteWidth, static_cast<unsigned>(off),
                                         bestT ? "transposed" : "row-vector",
                                         kReprojNames[bestI], n, bestErr, dev);
                                g_reprojForm.store(bestI, std::memory_order_release);
                            }
                        }
                    }

                    if (g_reprojBudget > 0)
                    {
                        --g_reprojBudget;
                        BVR_INFO("VP reprojection probe: cb %u offset %u drifts %.5f from "
                                 "identity; closest engine form is %s%s with max element "
                                 "error %.6f - %s. Previous frame is %llu frame(s) back.",
                                 byteWidth, static_cast<unsigned>(off), dev,
                                 bestI >= 0 ? kReprojNames[bestI] : "none",
                                 bestT ? " (transposed)" : "", bestErr,
                                 decisive ? "DECISIVE" : "not decisive",
                                 static_cast<unsigned long long>(now - born));
                    }
                }
            }
        }

        /* Hand this frame's pair to the next frame. Staged here, published by
           vp_frame_boundary, which is the only place that knows where a rendered
           frame ends. */
        reproj_stage(engCur, ourCur, (pub.eye == 1) ? 1 : 0);
    }

    /* THE FORM IS THE ENGINE'S CONVENTION. THE SITE IS JUST WHERE IT PUT IT.
     *
       Pass three above substitutes at ONE latched (buffer, offset), and it can
       only run at all for a buffer that carries a main-view matrix - which, in
       this game, means cb 1760 and nothing else. The census has been naming a
       second reprojection site for as long as it has been printing:

         cb 6656 offset 480   hits   604   drift 0.70711
         cb 1760 offset 752   hits 48616   drift 0.66667

       Two blocks, both near identity, both drifting with head speed, and only
       the busy one ever written - because the other lives in a buffer with no
       main view in it, so everything that could have corrected it was skipped
       before it was reached. A few hundred uploads a second against fifty
       thousand is what a sky or particle pass looks like beside the main scene,
       and those are exactly the things still moving with the head.

       So this runs for EVERY buffer, on the cached candidates rather than on
       ones recovered from the buffer in hand. Each site is validated the same
       way the primary is - a block that has stopped storing what we think is
       left alone rather than corrupted - and the census keeps the search down
       to a handful of known offsets instead of a walk. */
    if (writeAllowed && g_reprojForm.load(std::memory_order_acquire) >= 0 &&
        g_reprojCandCached.load(std::memory_order_acquire))
    {
        const int    nearRows = g_nearIdCount.load(std::memory_order_relaxed);
        const size_t primary  = g_reprojOffset.load(std::memory_order_relaxed);
        const uint32_t primaryCb = g_reprojCb.load(std::memory_order_relaxed);
        const int    xposeAll = g_reprojXpose.load(std::memory_order_relaxed);

        for (int i = 0; i < nearRows && i < kProbeRows; ++i)
        {
            const ProbeRow& row = g_nearId[i];

            if (row.bytes != byteWidth)
                continue;
            if (byteWidth == primaryCb && row.offset == primary)
                continue;   /* pass three owns that one */
            if (static_cast<size_t>(row.offset) + 64 > bytes)
                continue;

            float* alt = reinterpret_cast<float*>(data + row.offset);
            if (!finite16(alt))
                continue;

            const float altDev = raw_identity_drift(alt);
            if (altDev < kReprojMinDrift)
                continue;   /* still head, nothing to correct */

            Mat4 altObs;  memcpy(altObs.m, alt, sizeof(altObs.m));
            Mat4 altObsT; mat_transpose(altObsT, altObs);

            const float altErr =
                mat_max_diff(g_reprojEngCached, xposeAll ? altObsT : altObs);

            if (altErr > kReprojExplain * altDev)
                continue;   /* not storing our form after all */

            Mat4 altOut = g_reprojOurCached;
            if (xposeAll)
                mat_transpose(altOut, altOut);
            memcpy(alt, altOut.m, sizeof(altOut.m));
            g_statReproj.fetch_add(1, std::memory_order_relaxed);

            if (g_reprojAltBudget > 0)
            {
                --g_reprojAltBudget;
                BVR_INFO("VP reprojection: a SECOND site stores the same form - "
                         "cb %u offset %u, drift %.5f, error %.6f - and it is in a "
                         "buffer with no main view, which is why nothing had ever "
                         "reached it. Substituted with ours. This is the class of "
                         "pass that kept following the head after the first site "
                         "was corrected.",
                         byteWidth, row.offset, altDev, altErr);
            }
        }
    }



    /* Pass four: the direction lanes. See the note on ray_census for why the
       sky and the shadow-receive pass have nothing this file can recognise as a
       matrix, and why a lane is measurable anyway.
     *
       Aimed, not searched. The census names a (cb, offset, count) and a person
       reads it, checks it against a second run and writes it into the config -
       the same order the reprojection matrix and cascade_snap went in. Nothing
       here hunts for a lane to write while the frame is being built. */
    if (writeAllowed && ray_patch() != 0.0f &&
        (ray_offset() >= 0 || ray_offset_b() >= 0))
    {
        /* This upload's own D when it has one, the frame's otherwise. See the
           note on g_lastD - and note that the buffer gate has moved INTO the
           range loop, because the two ranges no longer name the same buffer. */
        Mat4 useD = cacheD;
        bool haveUse = haveD;
        if (!haveUse && g_lastDValid.load(std::memory_order_acquire))
        {
            useD    = g_lastD;
            haveUse = true;
            const uint64_t born = g_lastDFrame.load(std::memory_order_relaxed);
            const uint64_t now  = present_count();
            g_statRayBorrowed.fetch_add(1, std::memory_order_relaxed);
            if (now != born)
                g_statRayNoD.fetch_add(1, std::memory_order_relaxed);
        }
        if (!haveUse)
            return;

        /* D's 3x3 is R_eye^T * R_eng.
         *
           A world POINT wants w * D3 - that is pass one's VP' = D * VP * S seen
           from the other end. A camera-derived DIRECTION wants the opposite: it
           is stored as l * R_eng, and the value we want is l * R_eye, which is
           w * R_eng^T * R_eye = w * D3^T.
         *
           So the two candidate rotations are a transpose apart, which is the
           smallest possible difference between right and backwards, and exactly
           why ray_patch is a three-way dial. */
        const float d3[9] = { useD.m[0], useD.m[1], useD.m[2],
                              useD.m[4], useD.m[5], useD.m[6],
                              useD.m[8], useD.m[9], useD.m[10] };
        const float d3t[9] = { d3[0], d3[3], d3[6],
                               d3[1], d3[4], d3[7],
                               d3[2], d3[5], d3[8] };
        const float* R = (ray_patch() > 0.0f) ? d3t : d3;

        /* THE POINT FORM, AND ITS TRANSLATION ROW IS REBUILT HERE RATHER THAN
           TRUSTED.

           build_replacement only anchors D's rotation on the camera inside

               const float latch = latch_gain();
               if (latch < 0.999f)   { ... d.m[12] = cx - (...) ... }

           and vp_latch is 1.0, so that block NEVER RUNS in the shipping
           configuration. D therefore arrives with its raw translation row,
           which this file has a standing warning about:

             "THE TRANSLATION ROW OF D IS NOT A DISPLACEMENT, AND READING IT AS
              ONE COST TESTS 84 THROUGH 97 ... It is the WORLD POSITION
              multiplied by the rotation mismatch ... one degree prints as 19 m."

           Harmless to pass one, which multiplies whole matrices and never reads
           that row as a distance. Fatal to a point transform, and the report
           said so in one run: mean 264718 mm of displacement on a lane that is
           the camera position and should not have moved at all.

           So the pivot is rebuilt from the recovered camera, unconditionally:

               t = c - c * R      i.e.   p -> (p - c) * R + c

           which is a rotation about c by construction, for any R and any c, and
           cannot inherit whatever vp_latch did or did not do. */
        Mat4 dPt = useD;
        if (ray_patch() < 0.0f)
            mat_rigid_inverse(dPt, useD);

        if (g_engBasisValid.load(std::memory_order_relaxed))
        {
            const float cx = g_engPos[0], cy = g_engPos[1], cz = g_engPos[2];
            dPt.m[12] = cx - (cx*dPt.m[0] + cy*dPt.m[4] + cz*dPt.m[8]);
            dPt.m[13] = cy - (cx*dPt.m[1] + cy*dPt.m[5] + cz*dPt.m[9]);
            dPt.m[14] = cz - (cx*dPt.m[2] + cy*dPt.m[6] + cz*dPt.m[10]);
        }

        /* D's own size, as a property of D and not of whatever lane happens to
           be configured. A probe 20 m in front of the camera, carried through
           the same matrix: that is what a shadow receiver at typical battle
           distance actually gets. Reported so the next run grades the matrix
           before it grades the picture - the mistake in TESTs 150-160 was
           always visible in a number nobody had printed yet. */
        {
            const float ang = acosf(fmaxf(-1.0f, fminf(1.0f,
                                   (dPt.m[0] + dPt.m[5] + dPt.m[10] - 1.0f) * 0.5f)));
            const float qx = g_engPos[0] + g_engBasis[6] * 20.0f;
            const float qy = g_engPos[1] + g_engBasis[7] * 20.0f;
            const float qz = g_engPos[2] + g_engBasis[8] * 20.0f;
            const float rx = qx*dPt.m[0] + qy*dPt.m[4] + qz*dPt.m[8]  + dPt.m[12];
            const float ry = qx*dPt.m[1] + qy*dPt.m[5] + qz*dPt.m[9]  + dPt.m[13];
            const float rz = qx*dPt.m[2] + qy*dPt.m[6] + qz*dPt.m[10] + dPt.m[14];
            const float pm = sqrtf((rx-qx)*(rx-qx) + (ry-qy)*(ry-qy) + (rz-qz)*(rz-qz));
            if (ang == ang && pm == pm)
            {
                g_statRayProbeDeg.fetch_add(static_cast<uint64_t>(ang * 57.29578f * 1000.0f),
                                            std::memory_order_relaxed);
                g_statRayProbeMm.fetch_add(static_cast<uint64_t>(pm * 1000.0f),
                                           std::memory_order_relaxed);
                g_statRayProbeN.fetch_add(1, std::memory_order_relaxed);
            }
        }

        const int starts[2] = { ray_offset(),  ray_offset_b() };
        const int counts[2] = { ray_count(),   ray_count_b()  };

        /* Range B falls back to range A's width, so a single-buffer setup keeps
           working with vp_ray_cb_b unset. */
        const uint32_t cbs[2] = { ray_cb(),
                                  ray_cb_b() != 0 ? ray_cb_b() : ray_cb() };

        for (int range = 0; range < 2; ++range)
        {
            if (starts[range] < 0)
                continue;
            if (cbs[range] != 0 && byteWidth != cbs[range])
                continue;

            const size_t base = static_cast<size_t>(starts[range]);
            const int    n    = counts[range];

            for (int i = 0; i < n; ++i)
            {
                const size_t off = base + static_cast<size_t>(i) * 16;
                if (off + 16 > bytes)
                    break;

                float* w = reinterpret_cast<float*>(data + off);
                if (!finite3(w))
                    continue;

                const int kind = (range == 0) ? ray_kind() : ray_kind_b();

                if (kind == 1)
                {
                    /* A POINT. The axis predicate below is meaningless here -
                       a position is not near any camera axis and never will be
                       - so the guard is on MAGNITUDE instead: a lane that has
                       stopped being a position of the expected size is a lane
                       whose meaning changed between uploads, which is the same
                       hazard the direction predicate exists for.

                       The band is wide on purpose. It is there to catch the
                       slot being reused for something of a different order -
                       a unit vector, a zero, a 1e8 sentinel - not to second-
                       guess the engine within a factor of a few. */
                    const float pl = sqrtf(w[0]*w[0] + w[1]*w[1] + w[2]*w[2]);
                    if (!(pl > 1.0f && pl < 1e6f))
                    {
                        g_statRayPointBad.fetch_add(1, std::memory_order_relaxed);
                        continue;
                    }

                    const int pslot = range * 8 + i;
                    if (pslot < kRayLaneSlots)
                    {
                        RayWritten& rw = g_rayWritten[pslot];
                        if (rw.valid &&
                            rw.v[0] == w[0] && rw.v[1] == w[1] && rw.v[2] == w[2])
                        {
                            g_statRaySkip.fetch_add(1, std::memory_order_relaxed);
                            continue;
                        }
                    }

                    /* HOMOGENEOUS. THE w IS NOT DECORATION, AND ASSUMING IT WAS
                       ONE COST A WHOLE RUN.

                       Lane 720 of cb 1760 is

                         (13586.70, 18021.99, 466.43)  w 19.999920

                       and the engine camera at the same moment is

                         (679.4, 901.1, 23.4)

                       13586.70/19.99992 = 679.34, 18021.99/19.99992 = 901.10,
                       466.43/19.99992 = 23.32. Three axes, exact. It is the
                       CAMERA POSITION stored homogeneous and pre-multiplied by
                       its own w.

                       The first version of this added dPt.m[12..14] as though w
                       were 1, so the translation was under-applied twentyfold
                       and what remained was a rotation about nearly the origin
                       with a 22000-unit lever arm. The report measured the
                       consequence rather than leaving it to be argued: mean
                       1669292 mm of displacement, 1.7 km, against the
                       centimetres a correct camera-anchored rotation gives.

                       The row-vector form for a homogeneous row is

                         out.xyz = xyz * D3 + w * D.translation

                       which reduces to the familiar one only when w is 1. */
                    const float px = w[0], py = w[1], pz = w[2], pw = w[3];
                    const float ox = px*dPt.m[0] + py*dPt.m[4] + pz*dPt.m[8]  + pw*dPt.m[12];
                    const float oy = px*dPt.m[1] + py*dPt.m[5] + pz*dPt.m[9]  + pw*dPt.m[13];
                    const float oz = px*dPt.m[2] + py*dPt.m[6] + pz*dPt.m[10] + pw*dPt.m[14];

                    if (!(ox == ox && oy == oy && oz == oz))
                        continue;

                    /* How far the point actually moved, in millimetres. This is
                       the number that distinguishes a correct point transform
                       from the origin-rotation that preceded it: about the
                       camera it is centimetres, about the origin it was tens of
                       metres. Summed so the report can print a mean. */
                    const float dx = ox - px, dy = oy - py, dz = oz - pz;
                    const float moved = sqrtf(dx*dx + dy*dy + dz*dz) * 1000.0f;
                    g_statRayPointMoved.fetch_add(static_cast<uint64_t>(moved),
                                                  std::memory_order_relaxed);

                    w[0] = ox; w[1] = oy; w[2] = oz;

                    if (pslot < kRayLaneSlots)
                    {
                        RayWritten& rw = g_rayWritten[pslot];
                        rw.v[0] = ox; rw.v[1] = oy; rw.v[2] = oz;
                        rw.valid = true;
                    }

                    g_statRayPoint.fetch_add(1, std::memory_order_relaxed);
                    continue;
                }

                /* IS THIS LANE THE CAMERA BASIS ON THIS UPLOAD? PASS ONE ASKS.
                   PASS FOUR DID NOT, AND THAT IS THE WHOLE BUG.

                   cb 1760 is uploaded about nine times a frame and the content
                   at a given offset is NOT the same every time. The rejected
                   and orthographic tables catch offset 688 red-handed in the
                   same report the census calls it right * tanX:

                     offset 688 transposed  best dot 1.0000  nearest camera 679.34 m
                     offset 688             translation (0.5,0.5,400.0)

                   A transposed view whose camera is 679 m away, and an
                   ORTHOGRAPHIC block with a translation - which is a shadow
                   cascade. Writing a rotation into that offset on every upload
                   rotates the cascade on the uploads that carry one, and a
                   rotated cascade is dynamic shadows swinging with the head.
                   That is TESTs 150 through 153 in one line, and lane 720 was
                   only ever part of it.

                   Pass one has never had this problem because pass one is
                   PREDICATE-driven: it writes a block because the block looks
                   like the main view, not because of where the block sits.
                   Pass four was offset-driven and wrote blind. So it now asks
                   the same kind of question: this lane is rotated only if it
                   still IS one of the engine camera axes, to within a fraction
                   of a degree.

                   A cascade row, a skybox row and a 679 m view all fail this by
                   a mile - they are not near any camera axis at all - while the
                   true basis passes at |dot| ~ 1.0 whatever its scale. The
                   threshold does not need to be delicate, because the things
                   being excluded are nowhere near the thing being kept. */
                if (g_engBasisValid.load(std::memory_order_relaxed))
                {
                    const float ln = sqrtf(w[0]*w[0] + w[1]*w[1] + w[2]*w[2]);
                    bool isAxis = false;
                    if (ln > 1e-4f)
                    {
                        const float ux = w[0]/ln, uy = w[1]/ln, uz = w[2]/ln;
                        for (int ax = 0; ax < 3 && !isAxis; ++ax)
                        {
                            const float d = ux*g_engBasis[ax*3+0] +
                                            uy*g_engBasis[ax*3+1] +
                                            uz*g_engBasis[ax*3+2];
                            if (fabsf(d) > 0.999f)
                                isAxis = true;
                        }
                    }
                    if (!isAxis)
                    {
                        g_statRayNotBasis.fetch_add(1, std::memory_order_relaxed);
                        continue;
                    }
                }

                /* Already ours? Then the engine did not rewrite this lane on
                   this upload, and rotating it again would apply D twice. See
                   the note on g_rayWritten. */
                const int slot = range * 8 + i;
                if (slot < kRayLaneSlots)
                {
                    RayWritten& rw = g_rayWritten[slot];
                    if (rw.valid &&
                        rw.v[0] == w[0] && rw.v[1] == w[1] && rw.v[2] == w[2])
                    {
                        g_statRaySkip.fetch_add(1, std::memory_order_relaxed);
                        continue;
                    }
                }

                float out[3];
                rot3(out, w, R);
                if (!finite3(out))
                    continue;

                w[0] = out[0];
                w[1] = out[1];
                w[2] = out[2];

                if (slot < kRayLaneSlots)
                {
                    RayWritten& rw = g_rayWritten[slot];
                    rw.v[0] = out[0];
                    rw.v[1] = out[1];
                    rw.v[2] = out[2];
                    rw.valid = true;
                }

                /* w[3] is left alone on purpose. These lanes routinely carry
                   something else in the fourth component - a depth scale, a
                   fog parameter, the far distance the ray is normalised to -
                   and none of that is part of the direction. Rotating it would
                   be writing into a field whose meaning we have not measured. */

                g_statRay.fetch_add(1, std::memory_order_relaxed);
            }
        }
    }

    /* Pass two: clip->world. A deferred renderer reconstructs world position from
       depth with this, so leaving it pointing at the engine's camera while the
       geometry is drawn from ours puts the lighting, fog and screen-space effects
       in a different world from the pixels they are shading. */
    if (!writeAllowed || !cfg().patchInverse || !cacheValid)
        return;

    for (size_t off = 0; off + 64 <= bytes; off += 16)
    {
        float* raw = reinterpret_cast<float*>(data + off);
        if (!finite16(raw))
            continue;

        for (int variant = 0; variant < 2; ++variant)
        {
            Mat4 c;
            memcpy(c.m, raw, sizeof(c.m));
            if (variant == 1)
                mat_transpose(c, c);

            if (!is_inverse_of(c, cacheEngine))
                continue;

            Mat4 outMat = cachePatchedInverse;
            if (variant == 1)
                mat_transpose(outMat, outMat);
            memcpy(raw, outMat.m, sizeof(outMat.m));

            g_statInv.fetch_add(1, std::memory_order_relaxed);
            off += 48;
            break;
        }
    }
}

// ---------------------------------------------------------------------------
// hooks
// ---------------------------------------------------------------------------

using PFN_Map = HRESULT (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*,
                                             UINT, D3D11_MAP, UINT, D3D11_MAPPED_SUBRESOURCE*);
using PFN_Unmap = void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*, UINT);
using PFN_UpdateSubresource = void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11Resource*,
                                                        UINT, const D3D11_BOX*, const void*,
                                                        UINT, UINT);
using PFN_UpdateSubresource1 = void (STDMETHODCALLTYPE*)(ID3D11DeviceContext1*, ID3D11Resource*,
                                                         UINT, const D3D11_BOX*, const void*,
                                                         UINT, UINT, UINT);

/* One set per vtable. Immediate and deferred contexts are different classes in
   the D3D11 runtime with different vtables, so a hook on one does not catch the
   other - which is a leading candidate for "0 constant buffer uploads". */
PFN_Map                g_origMap = nullptr;
PFN_Unmap              g_origUnmap = nullptr;
PFN_UpdateSubresource  g_origUpdate = nullptr;
PFN_UpdateSubresource1 g_origUpdate1 = nullptr;

PFN_Map                g_origMapDef = nullptr;
PFN_Unmap              g_origUnmapDef = nullptr;
PFN_UpdateSubresource  g_origUpdateDef = nullptr;
PFN_UpdateSubresource1 g_origUpdate1Def = nullptr;

std::atomic<bool> g_hooked{false};

/* 65536 bytes is the D3D11 limit for a single constant buffer (4096 float4
   registers), so this cap now excludes nothing that could legally hold view
   constants. The first version capped at 8192 on the assumption that view
   constants live in a small buffer - but a single large per-frame buffer, or a
   ring buffer sub-allocated per draw, is just as common, and the cap would have
   rejected it silently. The per-resource miss cache and the unit-w fast reject
   are what keep the cost down now, not the size limit. */
uint32_t g_maxCbBytes = 65536;

/* A census of the distinct constant buffers the engine actually maps. If the
   view matrix is not arriving, this says what IS arriving - and if it is empty
   while g_cMap is large, the buffers are not constant buffers at all. */
struct CensusRow
{
    uint32_t bytes;
    uint32_t bind;
    uint32_t usage;
    uint32_t cpuAccess;
    uint32_t mapType;
};

constexpr int kCensusRows = 24;
CensusRow            g_census[kCensusRows] = {};
std::atomic<int>     g_censusCount{0};
std::atomic<bool>    g_censusLogged{false};

/* Races here duplicate a row at worst, which is harmless for a diagnostic and
   far cheaper than a lock on the driver's Map path. */
void census_note(const D3D11_BUFFER_DESC& desc, uint32_t mapType)
{
    const int have = g_censusCount.load(std::memory_order_relaxed);
    for (int i = 0; i < have && i < kCensusRows; ++i)
    {
        if (g_census[i].bytes == desc.ByteWidth && g_census[i].bind == desc.BindFlags &&
            g_census[i].usage == static_cast<uint32_t>(desc.Usage) &&
            g_census[i].mapType == mapType)
            return;
    }

    if (have >= kCensusRows)
        return;

    const int slot = g_censusCount.fetch_add(1, std::memory_order_relaxed);
    if (slot >= kCensusRows)
        return;

    g_census[slot].bytes = desc.ByteWidth;
    g_census[slot].bind = desc.BindFlags;
    g_census[slot].usage = static_cast<uint32_t>(desc.Usage);
    g_census[slot].cpuAccess = desc.CPUAccessFlags;
    g_census[slot].mapType = mapType;
}

struct MapRecord
{
    ID3D11Resource* resource;
    UINT            subresource;
    void*           data;
    uint32_t        bytes;
};

constexpr int kMaxMapped = 16;
thread_local MapRecord t_mapped[kMaxMapped];
thread_local int       t_mappedCount = 0;

bool constant_buffer_size(ID3D11Resource* resource, uint32_t& outBytes, uint32_t mapType)
{
    if (resource == nullptr)
        return false;

    D3D11_RESOURCE_DIMENSION dim = D3D11_RESOURCE_DIMENSION_UNKNOWN;
    resource->GetType(&dim);
    if (dim != D3D11_RESOURCE_DIMENSION_BUFFER)
        return false;

    g_cMapBuffer.fetch_add(1, std::memory_order_relaxed);

    D3D11_BUFFER_DESC desc = {};
    static_cast<ID3D11Buffer*>(resource)->GetDesc(&desc);

    if ((desc.BindFlags & D3D11_BIND_CONSTANT_BUFFER) == 0)
        return false;

    g_cMapConstant.fetch_add(1, std::memory_order_relaxed);
    census_note(desc, mapType);

    uint32_t biggest = g_biggestCb.load(std::memory_order_relaxed);
    while (desc.ByteWidth > biggest &&
           !g_biggestCb.compare_exchange_weak(biggest, desc.ByteWidth,
                                              std::memory_order_relaxed))
        ;

    if (desc.ByteWidth < 64 || desc.ByteWidth > g_maxCbBytes)
        return false;

    outBytes = desc.ByteWidth;
    return true;
}

// --- the shared bodies, one per vtable's worth of detours -------------------

/* ---------------------------------------------------------------------------
 * THE SECOND EYE'S CONSTANT BUFFER.
 *
 * The redirect can already put a draw into a target of our choosing - 535 of
 * them a frame, stable, no crash. What it cannot yet do is draw them from a
 * different viewpoint, because by the time a draw is issued its constants are
 * already on the GPU.
 *
 * Rewriting the engine's buffer between the two draws is the wrong answer. A
 * DYNAMIC buffer mapped with DISCARD cannot be partially rewritten, and doing it
 * twice per scene draw would be a thousand extra uploads a frame.
 *
 * So the second eye's version is made ONCE, at the moment the engine uploads
 * the first: the original bytes are copied aside, patched with the other eye's
 * camera, and uploaded into a buffer of ours. The redirect then only has to BIND
 * it - one call, no allocation, no upload.
 *
 * Keyed by the engine buffer's address, because the mapping is one-to-one: each
 * of rgl's constant buffers gets a shadow of the same size, reused for the life
 * of the process.
 * ------------------------------------------------------------------------- */
/* Defined with the redirect further down; needed here because the shadow is
   only built when the second draw is going to use it. */
void ensure_dup_set(ID3D11RenderTargetView* const* rtvs, UINT num, int sceneSlot);
void ensure_always_depth();
bool redirect_enabled();

struct ShadowCb
{
    ID3D11Resource* engineBuffer;   /* identity only, never dereferenced */
    ID3D11Buffer*   shadow;
    uint32_t        bytes;
};

constexpr int kShadowCbs = 16;
ShadowCb g_shadowCb[kShadowCbs] = {};
int      g_shadowCbCount = 0;

std::atomic<uint64_t> g_cShadowBuilt{0};
std::atomic<uint64_t> g_cShadowBound{0};

/* CPU scratch for the second eye's copy. Per thread, grown once, never freed -
   the same reasoning as the UpdateSubresource scratch above. */
thread_local uint8_t* t_otherBytes = nullptr;
thread_local size_t   t_otherCap = 0;
thread_local VpFrame  t_shadowOtherFrame = {};

ID3D11Buffer* shadow_for(ID3D11Resource* engineBuffer, uint32_t bytes)
{
    for (int i = 0; i < g_shadowCbCount; ++i)
        if (g_shadowCb[i].engineBuffer == engineBuffer)
            return g_shadowCb[i].bytes == bytes ? g_shadowCb[i].shadow : nullptr;

    if (g_shadowCbCount >= kShadowCbs)
        return nullptr;

    ID3D11Device* dev = engine_device();
    if (dev == nullptr)
        return nullptr;

    D3D11_BUFFER_DESC d = {};
    d.ByteWidth = bytes;
    d.Usage = D3D11_USAGE_DEFAULT;
    d.BindFlags = D3D11_BIND_CONSTANT_BUFFER;

    ID3D11Buffer* buf = nullptr;
    if (FAILED(dev->CreateBuffer(&d, nullptr, &buf)) || buf == nullptr)
        return nullptr;

    ShadowCb& s = g_shadowCb[g_shadowCbCount++];
    s.engineBuffer = engineBuffer;
    s.shadow = buf;
    s.bytes = bytes;

    BVR_INFO("Second eye: shadow constant buffer %d created for %p (%u bytes).",
             g_shadowCbCount - 1, static_cast<void*>(engineBuffer), bytes);
    return buf;
}

void begin_other_eye(ID3D11Resource* resource, const uint8_t* original,
                     uint32_t bytes)
{
    if (!redirect_enabled() || original == nullptr || bytes < 64)
        return;

    Published published;
    if (!read_published(published) || !published.other.valid)
        return;

    /* ONLY BUFFERS THAT ACTUALLY CARRY THE VIEW MATRIX.
     *
       Without this, every constant buffer rgl uploads gets a shadow. The first
       run made fourteen of them - 15360, 800 and 256 bytes among them, against
       the 1760-byte one that holds the view - rebuilt 491,753 times in five
       seconds, roughly 1,093 a frame. All of that was wasted copying and
       uploading.

       Worse, it broke the slot detection outright. note_vs_constant_buffers
       records the slot of any buffer it has a shadow for, so with fourteen
       shadows it latched onto whichever was bound most recently and the target
       slot thrashed between b0, b1, b2, b4, b5, b6, b7 and b9 many times a
       frame - and the duplicated draw was then swapping a shadow into a slot
       holding something else entirely.

       note.hits is the patcher's own record of having found a main-view matrix
       in this buffer, so it is exactly the right question. It is zero on the
       very first upload, because the shadow is built before the patch runs;
       that costs one frame of delay and nothing else. */
    if (note_for(resource).hits == 0)
        return;

    if (shadow_for(resource, bytes) == nullptr)
        return;

    if (t_otherCap < bytes)
    {
        uint8_t* grown = static_cast<uint8_t*>(realloc(t_otherBytes, bytes));
        if (grown == nullptr)
            return;
        t_otherBytes = grown;
        t_otherCap = bytes;
    }

    /* The copy starts as the engine's own bytes so everything that is NOT a view
       matrix - and that is nearly all of it - is present and correct. The scan
       then overwrites only the view sites, with the other eye's version. */
    memcpy(t_otherBytes, original, bytes);

    /* A copy, not a pointer into `published`: that is a local and the scan runs
       after this function's frame would have gone. */
    t_shadowOtherFrame = published.other;
    t_shadowOther = &t_shadowOtherFrame;
    t_shadowOut = t_otherBytes;

    /* IS THERE ANY SEPARATION TO RENDER?
     *
       "Both eyes display, but the depth is mono" has exactly two causes, and
       this tells them apart without guessing. Either the two published cameras
       are the same - in which case there is no stereo to draw and the fault is
       upstream in managed - or they differ by the IPD and the second eye's
       matrices are correct but are not reaching the shader that consumes them.
       65 mm is the answer that says the problem is downstream of here. */
    {
        static uint64_t lastLog = 0;
        const uint64_t nowMs = GetTickCount64();
        if (nowMs - lastLog >= 5000)
        {
            lastLog = nowMs;
            const float dx = published.other.eyeCamera[12] - published.frame.eyeCamera[12];
            const float dy = published.other.eyeCamera[13] - published.frame.eyeCamera[13];
            const float dz = published.other.eyeCamera[14] - published.frame.eyeCamera[14];
            BVR_INFO("Second eye separation: %.4f m between the drawn eye (%d) and the "
                     "duplicated one (%d). Near the IPD means the cameras are right and "
                     "a mono picture is the matrices not reaching the shader; near zero "
                     "means managed is publishing the same eye twice.",
                     sqrtf(dx * dx + dy * dy + dz * dz),
                     published.frame.eye, published.other.eye);
        }
    }
}

/* Uploads what the single pass wrote, and disarms. Must be called after every
   begin_other_eye, including the paths where nothing was written, or the next
   buffer's scan would keep writing into this one's scratch. */
void finish_other_eye(ID3D11Resource* resource, uint32_t bytes)
{
    uint8_t* const built = t_shadowOut;
    t_shadowOut = nullptr;
    t_shadowOther = nullptr;

    if (built == nullptr)
        return;

    ID3D11Buffer* shadow = shadow_for(resource, bytes);
    ID3D11DeviceContext* ctx = engine_context();
    if (shadow == nullptr || ctx == nullptr)
        return;

    ctx->UpdateSubresource(shadow, 0, nullptr, built, 0, 0);
    g_cShadowBuilt.fetch_add(1, std::memory_order_relaxed);
}

/* WHICH SLOT THE VIEW BUFFER IS BOUND TO.
 *
 * The redirect has to put the shadow where the shader will look for it, and
 * that register index is the engine's choice, not something to assume. It is
 * observed instead: whenever rgl binds a buffer we have built a shadow for, the
 * slot it went into is recorded.
 *
 * Only the buffer that actually carries the view matrix has a shadow, so this
 * cannot latch onto some unrelated constant buffer that happened to be bound
 * first - the shadow table is built by the patcher, from buffers it found a
 * main-view matrix in. */
std::atomic<int>            g_viewCbSlot{ -1 };
std::atomic<ID3D11Buffer*>  g_viewCbShadow{ nullptr };

ID3D11Buffer* current_shadow_cb()
{
    return g_viewCbShadow.load(std::memory_order_relaxed);
}

/* EVERY shadowed buffer's slot, not just the last one seen.
 *
 * The single-slot version is why the picture came back stereo-less. Three
 * buffers carry a main-view matrix in this engine - 6656, 1760 and 128 bytes -
 * and each is bound to a slot of its own. Recording only the most recent match
 * meant the redirect swapped ONE of them and left the others holding the drawn
 * eye's matrices, so whichever buffer the vertex shader actually read from was
 * as likely as not the unmodified one. Both eyes then drew the same viewpoint,
 * which on screen is a picture in both eyes with no depth in it.
 *
 * Indexed by VS slot so the redirect can swap all of them and put all of them
 * back, without searching. */
constexpr int kMaxVsSlots = D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT;
ID3D11Buffer* g_slotShadow[kMaxVsSlots] = {};

void note_vs_constant_buffers(UINT start, UINT num, ID3D11Buffer* const* buffers)
{
    if (buffers == nullptr || g_shadowCbCount == 0)
        return;

    for (UINT i = 0; i < num; ++i)
    {
        ID3D11Buffer* b = buffers[i];
        const UINT slotIdx = start + i;

        if (b == nullptr)
        {
            if (slotIdx < kMaxVsSlots)
                g_slotShadow[slotIdx] = nullptr;
            continue;
        }

        bool shadowed = false;

        for (int s = 0; s < g_shadowCbCount; ++s)
        {
            if (g_shadowCb[s].engineBuffer != static_cast<ID3D11Resource*>(b))
                continue;

            shadowed = true;
            if (slotIdx < kMaxVsSlots)
                g_slotShadow[slotIdx] = g_shadowCb[s].shadow;
            break;
        }

        /* A slot that stops holding a shadowed buffer must stop being swapped,
           or the redirect would put a stale view into whatever took its place. */
        if (!shadowed && slotIdx < kMaxVsSlots)
            g_slotShadow[slotIdx] = nullptr;
    }

    for (UINT i = 0; i < num; ++i)
    {
        ID3D11Buffer* b = buffers[i];
        if (b == nullptr)
            continue;

        for (int s = 0; s < g_shadowCbCount; ++s)
        {
            if (g_shadowCb[s].engineBuffer != static_cast<ID3D11Resource*>(b))
                continue;

            const int slot = static_cast<int>(start + i);

            /* Rate-limited, not merely change-triggered. The first version
               logged on every change and the change was constant - 51,000 lines
               in one session - because fourteen buffers had shadows and each
               wanted a different slot. That is fixed above, but a log line that
               can fire at bind rate needs a ceiling of its own regardless. */
            if (g_viewCbSlot.exchange(slot, std::memory_order_relaxed) != slot)
            {
                static uint64_t lastLog = 0;
                const uint64_t nowMs = GetTickCount64();
                if (nowMs - lastLog >= 5000)
                {
                    lastLog = nowMs;
                    BVR_INFO("Second eye: the view constant buffer is bound to VS slot "
                             "b%d. The duplicate draw swaps the shadow in there and puts "
                             "the engine's own back straight afterwards. A slot that "
                             "keeps moving means more than one buffer is shadowed.",
                             slot);
                }
            }

            g_viewCbShadow.store(g_shadowCb[s].shadow, std::memory_order_relaxed);
            return;
        }
    }
}

void on_map(ID3D11Resource* resource, UINT subresource, D3D11_MAP mapType,
            D3D11_MAPPED_SUBRESOURCE* mapped)
{
    g_cMap.fetch_add(1, std::memory_order_relaxed);

    if (mapped == nullptr || mapped->pData == nullptr)
        return;

    if (mapType != D3D11_MAP_WRITE_DISCARD &&
        mapType != D3D11_MAP_WRITE_NO_OVERWRITE &&
        mapType != D3D11_MAP_WRITE)
        return;

    uint32_t bytes = 0;
    if (!constant_buffer_size(resource, bytes, static_cast<uint32_t>(mapType)))
        return;

    /* Dropping the OLDEST record rather than refusing to record. The first
       version returned early when the table was full, so a single unmatched
       Map would wedge the table at 16 and silently stop all patching for the
       rest of the run - a failure with no symptom but "nothing happened". */
    if (t_mappedCount >= kMaxMapped)
    {
        g_cRecordFull.fetch_add(1, std::memory_order_relaxed);
        for (int i = 1; i < kMaxMapped; ++i)
            t_mapped[i - 1] = t_mapped[i];
        t_mappedCount = kMaxMapped - 1;
    }

    MapRecord& rec = t_mapped[t_mappedCount++];
    rec.resource = resource;
    rec.subresource = subresource;
    rec.data = mapped->pData;
    rec.bytes = bytes;
    g_cMapRecorded.fetch_add(1, std::memory_order_relaxed);
}

/* WHO FILLS per_framef - THE CALL-SITE CENSUS (TEST 166, late latch, phase 0).
 *
   The late latch has to put the head pose into rgl's camera BEFORE rgl derives
   per_framef, the cascade fit and the reprojection matrices from it. That needs
   the engine function doing the deriving, and the static route is closed: the
   only reference to the string "per_framef" in TaleWorlds.Native.dll is the
   shader-header generator (definitions_shader_resource_indices.rsh). The member
   names are not in the binary at all - the buffer is a fixed C++ struct.
 *
   So ask the one place that sees every per_framef upload. This runs inside the
   engine's own Unmap call, so the stack above us IS the filler, its caller, and
   the per-view render entry. Each distinct chain is logged once, as
   module+RVA, labelled by what the upload carries: a perspective main view or
   an orthographic light view (a cascade caster pass). Feed the RVAs to
   tools/native_xref.py func/dis/callers.
 *
   vp_callsite_census = 1. Costs one stack walk per 1760 B upload (~9 a frame)
   while on; off by default. */
struct CallsiteRow
{
    uint64_t hash;
    uint64_t hits;
    bool     ortho;
};

constexpr int kCallsiteRows   = 32;
constexpr int kCallsiteFrames = 14;
CallsiteRow   g_callsites[kCallsiteRows] = {};
int           g_callsiteCount = 0;

bool callsite_census_on()
{
    static bool cached = false;
    static bool have = false;
    if (!have) { have = true; cached = config_bool("vp_callsite_census", false); }
    return cached;
}

void describe_address(void* address, char* out, size_t outSize)
{
    HMODULE module = nullptr;
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                           GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           static_cast<LPCSTR>(address), &module) == 0 || module == nullptr)
    {
        _snprintf_s(out, outSize, _TRUNCATE, "%p (no module)", address);
        return;
    }

    char path[MAX_PATH] = {};
    if (GetModuleFileNameA(module, path, sizeof(path)) == 0)
    {
        _snprintf_s(out, outSize, _TRUNCATE, "%p (unnamed)", address);
        return;
    }

    const char kSep = 92;   /* backslash, spelled numerically - see bvr_crash.cpp */
    const char* name = strrchr(path, kSep);
    name = (name != nullptr) ? name + 1 : path;

    /* As a VA at the DLL's preferred base too, because that is what
       native_xref.py and dumpbin print. */
    const uintptr_t rva = reinterpret_cast<uintptr_t>(address) -
                          reinterpret_cast<uintptr_t>(module);
    _snprintf_s(out, outSize, _TRUNCATE, "%s+0x%llX (va 0x%llX)", name,
                static_cast<unsigned long long>(rva),
                static_cast<unsigned long long>(0x180000000ull + rva));
}

/* An orthographic world->clip keeps w = 1 for every point: column 3 is
   (0,0,0,1). A perspective one does not. Either storage order is checked, so
   the label cannot depend on the transposition. */
bool looks_orthographic(const float* m)
{
    const bool rowForm = fabsf(m[3]) < 1e-6f && fabsf(m[7]) < 1e-6f &&
                         fabsf(m[11]) < 1e-6f && fabsf(m[15] - 1.0f) < 1e-4f;
    const bool colForm = fabsf(m[12]) < 1e-6f && fabsf(m[13]) < 1e-6f &&
                         fabsf(m[14]) < 1e-6f && fabsf(m[15] - 1.0f) < 1e-4f;
    return rowForm || colForm;
}

void callsite_census(const void* data, size_t bytes)
{
    if (bytes != 1760 || !callsite_census_on())
        return;

    void* frames[kCallsiteFrames + 2] = {};
    /* Skip ours: this function, on_unmap and the Unmap detour. */
    const USHORT n = RtlCaptureStackBackTrace(3, kCallsiteFrames, frames, nullptr);
    if (n == 0)
        return;

    const float* f = static_cast<const float*>(data);
    const bool ortho = looks_orthographic(f + 240 / 4);

    uint64_t h = 1469598103934665603ull ^ (ortho ? 1u : 0u);
    for (USHORT i = 0; i < n; ++i)
        h = (h ^ reinterpret_cast<uintptr_t>(frames[i])) * 1099511628211ull;

    /* Render thread only (the Unmap hook), so no lock. */
    for (int i = 0; i < g_callsiteCount; ++i)
    {
        if (g_callsites[i].hash == h)
        {
            ++g_callsites[i].hits;
            return;
        }
    }

    if (g_callsiteCount >= kCallsiteRows)
        return;

    CallsiteRow& row = g_callsites[g_callsiteCount++];
    row.hash = h;
    row.hits = 1;
    row.ortho = ortho;

    BVR_INFO("VP callsite census: NEW chain #%d for a per_framef upload carrying %s "
             "(g_view_proj %s) on thread %lu (Present thread %lu), %u frame(s):",
             g_callsiteCount, ortho ? "a LIGHT view (cascade caster pass)" : "the MAIN view",
             ortho ? "orthographic" : "perspective",
             static_cast<unsigned long>(GetCurrentThreadId()),
             static_cast<unsigned long>(present_thread_id()), static_cast<unsigned>(n));

    for (USHORT i = 0; i < n; ++i)
    {
        char line[256] = {};
        describe_address(frames[i], line, sizeof(line));
        BVR_INFO("VP callsite census:     #%02u  %s", i, line);
    }
}

void on_unmap(ID3D11Resource* resource, UINT subresource)
{
    g_cUnmap.fetch_add(1, std::memory_order_relaxed);

    for (int i = t_mappedCount - 1; i >= 0; --i)
    {
        if (t_mapped[i].resource != resource || t_mapped[i].subresource != subresource)
            continue;

        g_cUnmapMatched.fetch_add(1, std::memory_order_relaxed);

        callsite_census(t_mapped[i].data, t_mapped[i].bytes);

        /* Which latched build this frame is drawn from (TEST 178) - read from
           the engine's own bytes, before anything below may rewrite them. */
        late_latch_note_upload(t_mapped[i].data, t_mapped[i].bytes);

        if (frame_map_capturing())
            frame_map_cb_upload(resource, t_mapped[i].data, t_mapped[i].bytes, 0, "map");

        /* THE ORIGINAL BYTES, TAKEN BEFORE ANYTHING IS SUBSTITUTED.
           The second eye has to be built from the engine's own matrix, not from
           the first eye's replacement - see the note in scan_and_patch. */
        begin_other_eye(resource,
                        static_cast<const uint8_t*>(t_mapped[i].data),
                        t_mapped[i].bytes);

        /* Patch while the memory is still ours to write. Once Unmap returns the
           driver may have renamed or copied the allocation. */
        scan_and_patch(static_cast<uint8_t*>(t_mapped[i].data),
                       t_mapped[i].bytes, t_mapped[i].bytes, resource);

        /* AFTER the substitution, deliberately. Native stereo builds the second
           eye from what the FIRST eye is actually being drawn with, so whatever
           vp_patch just did to the camera lands in BOTH eyes rather than one -
           two eyes latched to different head poses would not fuse. */
        stereo_on_cb_upload(resource, t_mapped[i].data, t_mapped[i].bytes);

        /* Uploads whatever the pass wrote for the other eye, and disarms. */
        finish_other_eye(resource, t_mapped[i].bytes);

        t_mapped[i] = t_mapped[t_mappedCount - 1];
        --t_mappedCount;
        return;
    }
}

/* UpdateSubresource hands us const memory, so the patch goes through a scratch
   copy. Allocated lazily per thread and never freed - the render thread lives as
   long as the process, and a free here would race the hook being removed.
   Returns the buffer to submit instead, or null to pass the original through. */
thread_local std::vector<uint8_t>* t_scratch = nullptr;

const void* on_update(ID3D11Resource* resource, const D3D11_BOX* box, const void* srcData)
{
    g_cUpdate.fetch_add(1, std::memory_order_relaxed);

    if (!cfg().updateSub || srcData == nullptr)
        return nullptr;

    uint32_t bytes = 0;
    if (!constant_buffer_size(resource, bytes, 0xFFu))
        return nullptr;

    uint32_t span = bytes;
    if (box != nullptr)
        span = (box->right > box->left) ? (box->right - box->left) : 0u;

    if (span < 64 || span > g_maxCbBytes)
        return nullptr;

    g_cUpdateConstant.fetch_add(1, std::memory_order_relaxed);

    if (t_scratch == nullptr)
        t_scratch = new std::vector<uint8_t>();

    t_scratch->resize(span);
    memcpy(t_scratch->data(), srcData, span);
    scan_and_patch(t_scratch->data(), span, bytes, resource);
    return t_scratch->data();
}

/* Two identical sets of detours, one per vtable. They cannot share a body
   because each needs its own trampoline back to the original. */

/* Defined further down, beside the draw hooks it inspects. */
void note_drawing_context(ID3D11DeviceContext* c);

/* NATIVE STEREO'S NON-CONSTANT BUFFER WRITES (TEST 183). One pending write per
   thread: the engine maps, fills and unmaps a buffer before touching the next.
   Kept apart from t_mapped so the constant-buffer path is exactly as it was. */
thread_local ID3D11Resource* t_stereoMapRes = nullptr;
thread_local UINT            t_stereoMapSub = 0;
thread_local void*           t_stereoMapData = nullptr;
thread_local uint32_t        t_stereoMapBytes = 0;

/* A delayed readback in flight (TEST 185): the game's staging texture, and the
   copy of ours that was mapped in its place. */
thread_local ID3D11Resource* t_rbOrig = nullptr;
thread_local ID3D11Resource* t_rbMapped = nullptr;

HRESULT STDMETHODCALLTYPE hooked_map(ID3D11DeviceContext* c, ID3D11Resource* r, UINT s,
                                     D3D11_MAP t, UINT f, D3D11_MAPPED_SUBRESOURCE* m)
{
    /* A readback can wait for the GPU; native stereo's breakdown counts how long
       (stereo_perf - nothing is timed in any other mode). */
    const int64_t mapT0 = stereo_perf_map_begin(t);

    if (stereo_active() && t == D3D11_MAP_READ)
    {
        HRESULT rbHr = S_OK;
        if (ID3D11Resource* used = stereo_readback_map(c, r, s, t, f, m, g_origMap, &rbHr))
        {
            stereo_perf_map_end(mapT0, r);
            t_rbOrig = r;
            t_rbMapped = used;
            return rbHr;
        }
    }

    const HRESULT hr = g_origMap(c, r, s, t, f, m);
    stereo_perf_map_end(mapT0, r);
    if (SUCCEEDED(hr))
    {
        if (frame_map_capturing())
            frame_map_map(r, s, t);
        on_map(r, s, t, m);

        if (stereo_active() && m != nullptr && m->pData != nullptr &&
            (t == D3D11_MAP_WRITE_DISCARD || t == D3D11_MAP_WRITE ||
             t == D3D11_MAP_WRITE_NO_OVERWRITE) &&
            stereo_wants_buffer_write(r))
        {
            D3D11_BUFFER_DESC bd = {};
            static_cast<ID3D11Buffer*>(r)->GetDesc(&bd);
            t_stereoMapRes = r;
            t_stereoMapSub = s;
            t_stereoMapData = m->pData;
            t_stereoMapBytes = bd.ByteWidth;
        }
    }
    return hr;
}

void STDMETHODCALLTYPE hooked_unmap(ID3D11DeviceContext* c, ID3D11Resource* r, UINT s)
{
    note_drawing_context(c);
    if (t_rbOrig != nullptr && r == t_rbOrig)
    {
        const ID3D11Resource* mapped = t_rbMapped;
        t_rbOrig = nullptr;
        t_rbMapped = nullptr;
        g_origUnmap(c, const_cast<ID3D11Resource*>(mapped), s);
        return;
    }
    if (t_stereoMapRes != nullptr && r == t_stereoMapRes && s == t_stereoMapSub)
    {
        /* Before the real Unmap: the memory is only ours to read until then. */
        stereo_on_buffer_write(r, t_stereoMapData, t_stereoMapBytes);
        t_stereoMapRes = nullptr;
    }
    on_unmap(r, s);
    g_origUnmap(c, r, s);
}

/* frame_map sees the ORIGINAL bytes: it is told before on_update builds the
   patched copy. */
void STDMETHODCALLTYPE hooked_update(ID3D11DeviceContext* c, ID3D11Resource* r, UINT s,
                                     const D3D11_BOX* b, const void* d, UINT rp, UINT dp)
{
    if (frame_map_capturing())
        frame_map_update(r, s, b, d);
    if (stereo_active())
    {
        uint32_t cbBytes = 0;
        if (b == nullptr && constant_buffer_size(r, cbBytes, 0xFFu))
            stereo_on_cb_upload(r, d, cbBytes);
        else
            stereo_on_update(c, r, s, b, d, rp, dp);
    }
    const void* patched = on_update(r, b, d);
    note_drawing_context(c);
    g_origUpdate(c, r, s, b, patched ? patched : d, rp, dp);
}

void STDMETHODCALLTYPE hooked_update1(ID3D11DeviceContext1* c, ID3D11Resource* r, UINT s,
                                      const D3D11_BOX* b, const void* d, UINT rp, UINT dp,
                                      UINT flags)
{
    if (frame_map_capturing())
        frame_map_update(r, s, b, d);
    if (stereo_active())
    {
        uint32_t cbBytes = 0;
        if (b == nullptr && constant_buffer_size(r, cbBytes, 0xFFu))
            stereo_on_cb_upload(r, d, cbBytes);
        else
            stereo_on_update(c, r, s, b, d, rp, dp);
    }
    const void* patched = on_update(r, b, d);
    g_origUpdate1(c, r, s, b, patched ? patched : d, rp, dp, flags);
}

HRESULT STDMETHODCALLTYPE hooked_map_def(ID3D11DeviceContext* c, ID3D11Resource* r, UINT s,
                                         D3D11_MAP t, UINT f, D3D11_MAPPED_SUBRESOURCE* m)
{
    const HRESULT hr = g_origMapDef(c, r, s, t, f, m);
    if (SUCCEEDED(hr))
    {
        if (frame_map_capturing())
            frame_map_map(r, s, t);
        on_map(r, s, t, m);
    }
    return hr;
}

void STDMETHODCALLTYPE hooked_unmap_def(ID3D11DeviceContext* c, ID3D11Resource* r, UINT s)
{
    note_drawing_context(c);
    on_unmap(r, s);
    g_origUnmapDef(c, r, s);
}

void STDMETHODCALLTYPE hooked_update_def(ID3D11DeviceContext* c, ID3D11Resource* r, UINT s,
                                         const D3D11_BOX* b, const void* d, UINT rp, UINT dp)
{
    if (frame_map_capturing())
        frame_map_update(r, s, b, d);
    const void* patched = on_update(r, b, d);
    note_drawing_context(c);
    g_origUpdateDef(c, r, s, b, patched ? patched : d, rp, dp);
}

void STDMETHODCALLTYPE hooked_update1_def(ID3D11DeviceContext1* c, ID3D11Resource* r, UINT s,
                                          const D3D11_BOX* b, const void* d, UINT rp, UINT dp,
                                          UINT flags)
{
    if (frame_map_capturing())
        frame_map_update(r, s, b, d);
    const void* patched = on_update(r, b, d);
    g_origUpdate1Def(c, r, s, b, patched ? patched : d, rp, dp, flags);
}

/* MH_ERROR_ALREADY_CREATED means this vtable slot holds the same function as one
   already hooked - which is the answer to "do immediate and deferred contexts
   share an implementation?". Not a failure: the existing hook already covers it,
   and the second detour simply never gets installed. */
bool hook_slot(void** vtable, int index, void* detour, void** original,
               const char* name, const char* which)
{
    void* target = vtable[index];

    if (!vtable_hook(vtable, index, detour, original))
    {
        BVR_ERR("Failed to hook %s::%s (vtable write refused at slot %d).",
                which, name, index);
        return false;
    }

    BVR_INFO("%s::%s vtable-hooked at slot %d (was %p).", which, name, index, target);
    return true;
}

/* ID3D11DeviceContext vtable: 0-2 IUnknown, 3-6 ID3D11DeviceChild, then the 108
   context methods from index 7. Map is the 8th of those, Unmap the 9th,
   UpdateSubresource the 42nd. ID3D11DeviceContext1's own methods follow at 115,
   so UpdateSubresource1 - its second - is 116. */
// ---------------------------------------------------------------------------
// second-round instrumentation
//
// The run after the first reported, for the whole session:
//
//   Map 0 (buffer 2, constant 0, recorded 0) | Unmap 0 (matched 0)
//   | UpdateSubresource 70 (constant 0)
//
// Map called ZERO times across 6561 present frames. That is not a plausible
// number for any working D3D11 renderer - a frame that draws anything maps
// something - so the interesting question is no longer "how does rgl upload its
// constants" but "does our interception work at all". The UpdateSubresource
// count being nonzero says MinHook and the vtable indices are right, which makes
// the Map result stranger, not clearer.
//
// So: stop inferring. Three measurements, each of which rules out a whole class
// of explanation on its own.
// ---------------------------------------------------------------------------

std::atomic<uint64_t> g_cDraw{0};       /* DrawIndexed - rgl never calls it   */
/* The draw entries rgl DOES use. Declared here beside g_cDraw rather than
   further down with the self-test counters, because the hooks that feed them
   are defined above that point. */
std::atomic<uint64_t> g_cDrawInstanced{0};      /* DrawIndexedInstanced        */
std::atomic<uint64_t> g_cDrawPlain{0};         /* Draw                        */
std::atomic<uint64_t> g_cDrawInstancedPlain{0}; /* DrawInstanced               */

/* The scene/not-scene split. Declared here with the other counters because the
   draw trampolines above read g_rtIsEye and are defined before the render
   target tracking that writes it. */
std::atomic<bool>     g_rtIsEye{ false };
std::atomic<uint64_t> g_cDrawScene{0};   /* draws while the eye target is bound */
std::atomic<uint64_t> g_cRtChanges{0};   /* how often the target moved at all   */

/* The per-target draw table. Declared here for the same reason as the counters
   above: the draw trampolines read it and are defined before the render-target
   tracking that fills it. */
/* KEYED ON SHAPE, NOT ON POINTER.
 *
 * The pointer version filled all 128 slots and then attributed 559,962 draws to
 * nothing: rgl hands out transient render targets from a pool, so the addresses
 * churn continuously and a table of them fills with dead entries during the
 * menu before a battle ever starts.
 *
 * Dimensions and format do not churn. There are a handful of distinct shapes in
 * a frame - the scene buffer, the shadow cascades, the post chain, the eye
 * texture - and shape is also the thing actually worth knowing: "the scene is
 * drawn at 2560x1440 and scaled into a 3400x3468 eye" and "the scene is drawn
 * at eye size" are different worlds, and only the second is a good place to
 * attach a second eye.
 *
 * A sample pointer is kept for reference only, never as the key. */
struct RtStat
{
    ID3D11Resource* sample;
    uint32_t width;
    uint32_t height;
    uint32_t format;
    uint64_t draws;
};

/* 128, not 24. The first run filled the table and then could not record any
   further target, so ~1,400 of the 1,465 draws a frame were attributed to
   nothing at all and the one line it printed was whichever target happened to
   be seen early. A deferred renderer with shadow cascades, a G-buffer, a post
   chain and per-light targets goes well past two dozen. */
constexpr int kRtStats = 128;
RtStat g_rtStats[kRtStats] = {};
int    g_rtStatCount = 0;

/* Index into the table for the target currently bound, or -1. Read by every
   draw; written only when the binding moves. */
std::atomic<int> g_curRtStat{ -1 };

/* ---------------------------------------------------------------------------
 * THE SECOND DRAW.
 *
 * Everything so far has been measurement. This is the first piece that renders
 * anything: when a draw lands on the scene colour target, issue it a SECOND
 * time into a duplicate target of our own.
 *
 * It deliberately does NOT change the view matrix yet. A second eye needs the
 * camera moved 65 mm sideways, but that means rewriting a constant buffer
 * between two draws, and a buffer rgl mapped with DISCARD cannot be partially
 * rewritten - the whole thing has to be reconstructed. That is real work and it
 * is worth nothing until the far simpler question is settled:
 *
 *     can a draw be re-issued into a target of our choosing at all?
 *
 * So the duplicate is drawn with the identical view, and success looks like an
 * exact copy of the scene. That is a result which cannot be faked by accident:
 * an empty or garbage duplicate says the redirect does not work, and a correct
 * copy says every remaining piece is plumbing.
 *
 * WHICH TARGET IS THE SCENE
 *
 * Chosen by measurement rather than hardcoded: the busiest colour target of the
 * previous window that is not the desktop mirror. The census made that
 * unambiguous - 2267x2312 fmt 28 took 481 draws a frame where the mirror took
 * 47 - and deriving it each window means a resolution or upscaler change moves
 * it without anyone editing a constant.
 *
 * OFF by default. It doubles GPU geometry cost for no visible benefit until the
 * eye offset exists, and it is the first thing here that can affect what the
 * headset shows.
 * ------------------------------------------------------------------------- */
/* THE SCENE TARGET IS REMEMBERED BY SHAPE, NOT BY TABLE INDEX.
 *
 * The index version never fired once. The census table is rebuilt at every
 * window boundary so its indices mean nothing across one, and the code stored
 * the chosen index and then cleared it four lines later as part of that same
 * rebuild - so every draw that looked found -1. Shape survives the rebuild for
 * the same reason it was the right census key: it is what the target IS. */
std::atomic<uint32_t> g_sceneW{0}, g_sceneH{0}, g_sceneFmt{0};

/* Set when the currently bound target matches that shape. Read by every draw;
   written only when the binding moves, exactly like g_curRtStat. */
std::atomic<bool>     g_rtIsScene{ false };

/* Which of the bound targets is the scene one, or -1. The redirect replaces
   THAT slot and leaves the rest of the G-buffer bound. */
std::atomic<int>      g_sceneRtSlot{ -1 };

/* The scene depth buffer's shape, learned while the colour targets are bound,
   and whether the current binding is a depth-only pass on it - the prepass. */
std::atomic<uint32_t> g_sceneDepthW{0}, g_sceneDepthH{0}, g_sceneDepthF{0};
std::atomic<bool>     g_rtIsSceneDepth{ false };

/* THE ENGINE DOES NOT ALWAYS RENDER INTO THE WHOLE TARGET.
 *
 * With dynamic resolution the scene is drawn into a SUB-RECTANGLE of the scene
 * target and the upscaler is told which part is live. Our blit stretched the
 * ENTIRE texture, so the unwritten margin came with it - a band down the side of
 * the second eye, which is what is left in the photographs now that the colour
 * is right.
 *
 * The viewport the scene draws actually used is recorded, and the blit samples
 * only that fraction. */
std::atomic<float> g_sceneVpW{ 0.0f }, g_sceneVpH{ 0.0f };
std::atomic<uint64_t> g_cRedirected{0};     /* draws successfully re-issued    */
std::atomic<uint64_t> g_cRedirectFailed{0};
std::atomic<uint64_t> g_cPrepassRedirected{0};
std::atomic<uint64_t> g_cDepthCleared{0};

/* A DUPLICATE OF EVERY TARGET IN THE SET, NOT JUST THE SCENE COLOUR.
 *
 * Two attempts failed here and each failed in a way that named the next one.
 *
 * Binding our single duplicate ALONE left the right eye with soldiers on a
 * black field: a deferred draw writes several outputs at once, and with the rest
 * unbound everything not in our one target was discarded.
 *
 * Binding ours PLUS the engine's other targets fixed nothing and broke the left
 * eye into random colour: the second eye's draws then wrote their normals,
 * motion vectors and depth straight into the engine's own G-buffer, corrupting
 * the eye the engine was drawing.
 *
 * Both are the same mistake from opposite sides. The second eye needs somewhere
 * of its own to write EVERYTHING, so every colour target in the set gets a
 * duplicate of matching shape and the redirect binds our whole set.
 *
 * Depth is the exception and is handled separately - see the read-only view in
 * redirect_second_draw. */
constexpr int kDupSlots = D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT;

ID3D11Texture2D*        g_dupTexN[kDupSlots] = {};
ID3D11RenderTargetView* g_dupRtvN[kDupSlots] = {};
uint32_t g_dupW[kDupSlots] = {}, g_dupH[kDupSlots] = {}, g_dupF[kDupSlots] = {};

/* Redirected draws per scene slot, so "which duplicate holds the soldiers" is
   measured rather than assumed. */
uint64_t g_drawsPerSceneSlot[kDupSlots] = {};

/* Duplicates live in a shape-keyed pool; the per-slot arrays above only point
   into it, so the same surface is reused wherever it is bound. */
constexpr int kShapesPerSlot = 4;
ID3D11Texture2D*        g_slotCacheTex[kDupSlots][kShapesPerSlot] = {};
ID3D11RenderTargetView* g_slotCacheRtv[kDupSlots][kShapesPerSlot] = {};
uint32_t g_slotCacheW[kDupSlots][kShapesPerSlot] = {};
uint32_t g_slotCacheH[kDupSlots][kShapesPerSlot] = {};
uint32_t g_slotCacheF[kDupSlots][kShapesPerSlot] = {};
int g_dupCount = 0;

/* The scene colour slot's duplicate, which is the one the blit reads. Aliases
   into the array above rather than being a separate texture. */
ID3D11Texture2D*        g_dupTex = nullptr;
ID3D11RenderTargetView* g_dupRtv = nullptr;
uint32_t g_dupWidth = 0, g_dupHeight = 0, g_dupFormat = 0;

/* Re-entrancy guard. The redirect calls OMSetRenderTargets, which is itself
   inline-hooked, which would call note_render_target and move g_curRtStat out
   from under the very draw being duplicated. */
thread_local bool g_inRedirect = false;

/* THE MODE, NOT A CONFIG KEY.
 *
 * This read the config directly at first, which meant the experiment could not
 * be built into the mod without changing what AFR did - it pinned the camera,
 * took over the second eye's staging, and altered a path people actually play.
 * Published from managed now, true only while draw-duplication stereo is the
 * SELECTED mode, so every other mode behaves exactly as it did before any of
 * this existed. */
std::atomic<bool> g_drawStereo{ false };

bool redirect_enabled()
{
    return g_drawStereo.load(std::memory_order_relaxed);
}

/* Draws that landed on a target the table had no room for, or none at all.
   Non-zero means the census is incomplete and its shares cannot be trusted. */
std::atomic<uint64_t> g_cDrawUnattributed{0};
std::atomic<uint64_t> g_cVsSetCb{0};    /* are constant buffers ever bound?   */
std::atomic<uint64_t> g_cCreateBuffer{0};
std::atomic<uint64_t> g_cCreateCb{0};

std::atomic<bool> g_selfTestRun{false};
std::atomic<bool> g_selfTestPassed{false};

/* Buffer descriptions at creation time, from the DEVICE vtable - which is known
   to work, because the CreateTexture2D hook on the same vtable has been
   capturing eye textures since Phase 4. A constant buffer created DYNAMIC with
   CPU_ACCESS_WRITE can only be updated through Map; created DEFAULT, only
   through UpdateSubresource or a copy. So this says how the engine INTENDS to
   update its constants, independently of whether our Map hook ever fires. */
struct BufferKind
{
    uint32_t bytes;
    uint32_t bind;
    uint32_t usage;
    uint32_t cpuAccess;
};

constexpr int    kBufferKinds = 24;
BufferKind       g_bufferKinds[kBufferKinds] = {};
std::atomic<int> g_bufferKindCount{0};
std::atomic<bool> g_bufferKindsLogged{false};

void buffer_kind_note(const D3D11_BUFFER_DESC& desc)
{
    const int have = g_bufferKindCount.load(std::memory_order_relaxed);
    for (int i = 0; i < have && i < kBufferKinds; ++i)
    {
        if (g_bufferKinds[i].bytes == desc.ByteWidth &&
            g_bufferKinds[i].bind == desc.BindFlags &&
            g_bufferKinds[i].usage == static_cast<uint32_t>(desc.Usage) &&
            g_bufferKinds[i].cpuAccess == desc.CPUAccessFlags)
            return;
    }

    if (have >= kBufferKinds)
        return;

    const int slot = g_bufferKindCount.fetch_add(1, std::memory_order_relaxed);
    if (slot >= kBufferKinds)
        return;

    g_bufferKinds[slot].bytes = desc.ByteWidth;
    g_bufferKinds[slot].bind = desc.BindFlags;
    g_bufferKinds[slot].usage = static_cast<uint32_t>(desc.Usage);
    g_bufferKinds[slot].cpuAccess = desc.CPUAccessFlags;
}

using PFN_DrawIndexed = void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT, INT);
using PFN_VSSetConstantBuffers = void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT,
                                                           ID3D11Buffer* const*);
using PFN_CreateBuffer = HRESULT (STDMETHODCALLTYPE*)(ID3D11Device*, const D3D11_BUFFER_DESC*,
                                                      const D3D11_SUBRESOURCE_DATA*,
                                                      ID3D11Buffer**);

using PFN_DrawIndexedInstanced = void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT,
                                                           UINT, INT, UINT);
using PFN_Draw = void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT);
using PFN_DrawInstanced = void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT,
                                                    UINT, UINT);

PFN_DrawIndexed g_origDrawIndexed = nullptr;
PFN_DrawIndexed g_origDrawIndexedDef = nullptr;
PFN_DrawIndexedInstanced g_origDrawIndexedInstanced = nullptr;
PFN_DrawIndexedInstanced g_origDrawIndexedInstancedDef = nullptr;
PFN_Draw g_origDraw = nullptr;
PFN_Draw g_origDrawDef = nullptr;
PFN_DrawInstanced g_origDrawInstanced = nullptr;
PFN_DrawInstanced g_origDrawInstancedDef = nullptr;
PFN_VSSetConstantBuffers g_origVsSetCb = nullptr;
PFN_VSSetConstantBuffers g_origVsSetCbDef = nullptr;
PFN_CreateBuffer g_origCreateBuffer = nullptr;

/* WHY ZERO DRAWS AND SIXTY THOUSAND CONSTANT UPLOADS CANNOT BOTH BE TRUE.
 *
 * All four draw entries hooked cleanly on both vtables and counted nothing,
 * while Map and UpdateSubresource on THE SAME vtables counted 61,605 in five
 * seconds. Only two things can produce that, and they lead opposite ways:
 *
 *   1. The context doing the uploading is not the context doing the drawing.
 *      Then there is a vtable we never hooked, and finding it is the whole job.
 *
 *   2. It is the same context, and its vtable no longer holds our detour by the
 *      time a draw goes out - the runtime reclaimed the slot, or rgl calls
 *      through function pointers it cached before we ever patched. Then vtable
 *      hooking cannot intercept draws in this process at all, and the answer is
 *      inline hooks or nothing.
 *
 * This tells them apart. It runs from inside a hook that DOES fire, looks at
 * the vtable of the context that is really uploading, and reports whether our
 * draw detours are still sitting in it. One shot per distinct context, a
 * handful at most, so it costs a compare on the hot path and nothing else. */
/* Defined below; named here because this runs from a hook that sits above them. */
void STDMETHODCALLTYPE hooked_draw_indexed(ID3D11DeviceContext*, UINT, UINT, INT);
void STDMETHODCALLTYPE hooked_draw_indexed_def(ID3D11DeviceContext*, UINT, UINT, INT);
void STDMETHODCALLTYPE hooked_draw_indexed_instanced(ID3D11DeviceContext*, UINT, UINT, UINT, INT, UINT);
void STDMETHODCALLTYPE hooked_draw_indexed_instanced_def(ID3D11DeviceContext*, UINT, UINT, UINT, INT, UINT);

void note_drawing_context(ID3D11DeviceContext* c)
{
    if (c == nullptr)
        return;

    static ID3D11DeviceContext* seen[8] = {};
    static int seenCount = 0;

    for (int i = 0; i < seenCount; ++i)
        if (seen[i] == c)
            return;

    if (seenCount >= 8)
        return;
    seen[seenCount++] = c;

    void** vt = *reinterpret_cast<void***>(c);

    /* Literal slots: the kIdx constants are declared further down. */
    const bool drawIndexedOurs = vt[12] ==
                                 reinterpret_cast<void*>(&hooked_draw_indexed) ||
                                 vt[12] ==
                                 reinterpret_cast<void*>(&hooked_draw_indexed_def);
    const bool instancedOurs = vt[20] ==
                               reinterpret_cast<void*>(&hooked_draw_indexed_instanced) ||
                               vt[20] ==
                               reinterpret_cast<void*>(&hooked_draw_indexed_instanced_def);

    /* The vtable answer is now expected to be "no" and is no longer the
       question - we stopped contesting those slots on purpose. What is worth
       recording is WHICH context does the uploading, because if a second one
       ever appears the draws may be somewhere we are not looking at all. */
    BVR_INFO("Uploading context #%d: %p, type %s, vtable %p (our detours in the draw "
             "slots: DrawIndexed %s, DrawIndexedInstanced %s - both expected to be "
             "'no' now, since the draws are inline-hooked at their implementations "
             "instead and the runtime is left to own the slots).",
             seenCount - 1, static_cast<void*>(c),
             c->GetType() == D3D11_DEVICE_CONTEXT_IMMEDIATE ? "IMMEDIATE" : "DEFERRED",
             static_cast<void*>(vt),
             drawIndexedOurs ? "YES" : "no",
             instancedOurs ? "YES" : "no");
}

/* ---------------------------------------------------------------------------
 * INLINE HOOKS ON THE DRAW IMPLEMENTATIONS THEMSELVES.
 *
 * The vtable route is dead for draws and the log said so twice over: all four
 * entries hooked cleanly, all four counted ZERO across whole battles, and the
 * probe in Unmap then found our detours gone from slots 12 and 20 on the one
 * context that uploads sixty thousand constants a second. The runtime owns
 * those slots. It reclaimed them 23 times in one session and never touched
 * Map, Unmap or UpdateSubresource.
 *
 * WHY THIS IS NOT THE MINHOOK ATTEMPT THAT ALREADY FAILED
 *
 * The note above records one: MH_CreateHook per flip, 693 re-hooks in twenty
 * seconds, MinHook's trampoline pool exhausted, and every hook in the process
 * failing after that - including the eye textures. That is a real hazard and
 * this must not repeat it.
 *
 * But the same note contains the way out, in its own words: the slots
 * "alternate between the SAME PAIR of implementations". A pair. Two. The
 * failure was not that inline hooking is wrong here, it was that the code
 * chased the flips instead of recognising there is a small fixed set of targets
 * behind them. Hook each ADDRESS once, ever, and the runtime can swap the slot
 * as often as it likes - both destinations are already detoured, and no further
 * allocation is needed no matter how many times it flips.
 *
 * So: a registry of addresses already hooked, an absolute cap, and never a
 * second MH_CreateHook for an address seen before. Eight targets buys four draw
 * entries with two implementations each, which is what the log describes.
 * ------------------------------------------------------------------------- */

constexpr int kDrawKinds = 4;    /* DrawIndexed, DrawIndexedInstanced, Draw, DrawInstanced */
/* Four, not two. The log found a THIRD DrawIndexed implementation within two
   seconds of the first pair, so "the same pair" in the older note was an
   undercount of what the runtime rotates through.

   This matters more for stereo than it did for counting: a draw whose
   implementation we failed to hook is a draw that happens once, and an object
   drawn once appears in ONE EYE. Undercounting a census is a cosmetic problem;
   missing a draw when doubling is a rendering bug that looks exactly like the
   bug this whole effort exists to remove.

   Still hard-capped, and still one MH_CreateHook per distinct address ever, so
   the pool drain that broke the earlier attempt remains impossible. */
constexpr int kDrawVariants = 4;

void* g_drawTramp[kDrawKinds][kDrawVariants] = {};
void* g_drawTarget[kDrawKinds][kDrawVariants] = {};
int   g_drawHooksMade = 0;
bool  g_drawPoolWarned = false;

/* One counter per KIND, so the census still reads as it did. */
std::atomic<uint64_t>* const g_drawCounter[kDrawKinds] = {
    &g_cDraw, &g_cDrawInstanced, &g_cDrawPlain, &g_cDrawInstancedPlain
};

template <int K, int V>
void STDMETHODCALLTYPE tramp_draw_indexed(ID3D11DeviceContext* c, UINT a, UINT b, INT d)
{
    g_drawCounter[K]->fetch_add(1, std::memory_order_relaxed);
    if (g_rtIsEye.load(std::memory_order_relaxed))
        g_cDrawScene.fetch_add(1, std::memory_order_relaxed);
    {
        const int rt = g_curRtStat.load(std::memory_order_relaxed);
        if (rt >= 0)
            ++g_rtStats[rt].draws;
        else
            g_cDrawUnattributed.fetch_add(1, std::memory_order_relaxed);
    }
    if (frame_map_capturing())
    {
        char fa[96];
        _snprintf_s(fa, _TRUNCATE, "n=%u si=%u bv=%d", a, b, d);
        frame_map_draw(c, "DrawIndexed", fa);
    }
    StereoOp sop;
    const bool sdup = stereo_begin(c, sop, false);
    reinterpret_cast<PFN_DrawIndexed>(g_drawTramp[K][V])(c, a, b, d);
    if (sdup)
    {
        stereo_bind(c, sop);
        reinterpret_cast<PFN_DrawIndexed>(g_drawTramp[K][V])(c, a, b, d);
        stereo_end(c, sop);
    }
    redirect_second_draw(c, [&]{ reinterpret_cast<PFN_DrawIndexed>(g_drawTramp[K][V])(c, a, b, d); });
}

template <int K, int V>
void STDMETHODCALLTYPE tramp_draw_indexed_instanced(ID3D11DeviceContext* c, UINT ipi,
                                                    UINT ic, UINT sil, INT bvl, UINT sil2)
{
    g_drawCounter[K]->fetch_add(1, std::memory_order_relaxed);
    if (g_rtIsEye.load(std::memory_order_relaxed))
        g_cDrawScene.fetch_add(1, std::memory_order_relaxed);
    {
        const int rt = g_curRtStat.load(std::memory_order_relaxed);
        if (rt >= 0)
            ++g_rtStats[rt].draws;
        else
            g_cDrawUnattributed.fetch_add(1, std::memory_order_relaxed);
    }
    if (frame_map_capturing())
    {
        char fa[96];
        _snprintf_s(fa, _TRUNCATE, "n=%u inst=%u si=%u bv=%d sinst=%u", ipi, ic, sil, bvl, sil2);
        frame_map_draw(c, "DrawIndexedInstanced", fa);
    }
    StereoOp sop;
    const bool sdup = stereo_begin(c, sop, false);
    reinterpret_cast<PFN_DrawIndexedInstanced>(g_drawTramp[K][V])(c, ipi, ic, sil, bvl, sil2);
    if (sdup)
    {
        stereo_bind(c, sop);
        reinterpret_cast<PFN_DrawIndexedInstanced>(g_drawTramp[K][V])(c, ipi, ic, sil, bvl, sil2);
        stereo_end(c, sop);
    }
    redirect_second_draw(c, [&]{ reinterpret_cast<PFN_DrawIndexedInstanced>(g_drawTramp[K][V])(c, ipi, ic, sil, bvl, sil2); });
}

template <int K, int V>
void STDMETHODCALLTYPE tramp_draw(ID3D11DeviceContext* c, UINT vc, UINT svl)
{
    g_drawCounter[K]->fetch_add(1, std::memory_order_relaxed);
    if (g_rtIsEye.load(std::memory_order_relaxed))
        g_cDrawScene.fetch_add(1, std::memory_order_relaxed);
    {
        const int rt = g_curRtStat.load(std::memory_order_relaxed);
        if (rt >= 0)
            ++g_rtStats[rt].draws;
        else
            g_cDrawUnattributed.fetch_add(1, std::memory_order_relaxed);
    }
    if (frame_map_capturing())
    {
        char fa[64];
        _snprintf_s(fa, _TRUNCATE, "n=%u sv=%u", vc, svl);
        frame_map_draw(c, "Draw", fa);
    }
    StereoOp sop;
    const bool sdup = stereo_begin(c, sop, false);
    reinterpret_cast<PFN_Draw>(g_drawTramp[K][V])(c, vc, svl);
    if (sdup)
    {
        stereo_bind(c, sop);
        reinterpret_cast<PFN_Draw>(g_drawTramp[K][V])(c, vc, svl);
        stereo_end(c, sop);
    }
    redirect_second_draw(c, [&]{ reinterpret_cast<PFN_Draw>(g_drawTramp[K][V])(c, vc, svl); });
}

template <int K, int V>
void STDMETHODCALLTYPE tramp_draw_instanced(ID3D11DeviceContext* c, UINT vpi, UINT ic,
                                            UINT svl, UINT sil)
{
    g_drawCounter[K]->fetch_add(1, std::memory_order_relaxed);
    if (g_rtIsEye.load(std::memory_order_relaxed))
        g_cDrawScene.fetch_add(1, std::memory_order_relaxed);
    {
        const int rt = g_curRtStat.load(std::memory_order_relaxed);
        if (rt >= 0)
            ++g_rtStats[rt].draws;
        else
            g_cDrawUnattributed.fetch_add(1, std::memory_order_relaxed);
    }
    if (frame_map_capturing())
    {
        char fa[96];
        _snprintf_s(fa, _TRUNCATE, "n=%u inst=%u sv=%u sinst=%u", vpi, ic, svl, sil);
        frame_map_draw(c, "DrawInstanced", fa);
    }
    StereoOp sop;
    const bool sdup = stereo_begin(c, sop, false);
    reinterpret_cast<PFN_DrawInstanced>(g_drawTramp[K][V])(c, vpi, ic, svl, sil);
    if (sdup)
    {
        stereo_bind(c, sop);
        reinterpret_cast<PFN_DrawInstanced>(g_drawTramp[K][V])(c, vpi, ic, svl, sil);
        stereo_end(c, sop);
    }
    redirect_second_draw(c, [&]{ reinterpret_cast<PFN_DrawInstanced>(g_drawTramp[K][V])(c, vpi, ic, svl, sil); });
}

/* True if this exact address has already been given to MinHook. THE guard
   against repeating the 693-re-hook failure - every path into inline_hook_draw
   goes through it, including the reclaim handler that fires every frame. */
bool draw_target_known(void* target)
{
    for (int k = 0; k < kDrawKinds; ++k)
        for (int v = 0; v < kDrawVariants; ++v)
            if (g_drawTarget[k][v] == target)
                return true;
    return false;
}

void inline_hook_draw(int kind, void* target, void* const* detours, const char* name)
{
    if (target == nullptr || kind < 0 || kind >= kDrawKinds)
        return;
    if (draw_target_known(target))
        return;

    int v = -1;
    for (int i = 0; i < kDrawVariants; ++i)
        if (g_drawTarget[kind][i] == nullptr) { v = i; break; }

    if (v < 0)
    {
        if (!g_drawPoolWarned)
        {
            g_drawPoolWarned = true;
            BVR_WARN("%s has shown a THIRD implementation. Only two are hooked; the "
                     "census will undercount rather than allocate without bound - "
                     "which is the failure that emptied MinHook's pool before.", name);
        }
        return;
    }

    void* const detour = detours[v];

    void* tramp = nullptr;
    if (MH_CreateHook(target, detour, &tramp) != MH_OK || MH_EnableHook(target) != MH_OK)
    {
        BVR_WARN("Inline hook on %s at %p refused by MinHook.", name, target);
        return;
    }

    g_drawTarget[kind][v] = target;
    g_drawTramp[kind][v] = tramp;
    ++g_drawHooksMade;

    BVR_INFO("Inline-hooked %s implementation %d at %p (%d draw hook(s) made, hard "
             "cap %d). The runtime may swap the vtable slot as often as it likes now; "
             "both destinations are detoured and no further allocation happens.",
             name, v, target, g_drawHooksMade, kDrawKinds * kDrawVariants);
}

/* ---------------------------------------------------------------------------
 * WHICH OF THE 1700 DRAWS ARE THE SCENE.
 *
 * Doubling every draw would be wrong as well as wasteful. Shadow passes are
 * built in light space and are shared by both eyes; the interface and the
 * post-processing operate on a finished image. Only the draws that write the
 * eye colour target are the ones that have to happen twice.
 *
 * The cheap way to know is NOT to ask per draw. OMGetRenderTargets AddRefs what
 * it hands back, and paying a call plus two interlocked operations 245,000
 * times a second to answer a question that changes a few dozen times a frame is
 * the wrong shape entirely.
 *
 * So the target is tracked when it CHANGES. OMSetRenderTargets is inline-hooked
 * exactly like the draws - the runtime owns that vtable slot too - and it
 * resolves the bound view to its resource once, compares it against the eye
 * texture, and leaves the answer in a flag. The draw detours then read a
 * relaxed atomic, which costs nothing.
 * ------------------------------------------------------------------------- */

/* WHERE THE SCENE IS ACTUALLY DRAWN.
 *
 * The eye texture turned out to receive exactly ONE draw per frame - the final
 * resolve - while 1,465 draws went somewhere else. That is an ordinary deferred
 * renderer: the scene is drawn into rgl's own G-buffer and HDR targets and only
 * the finished image is blitted out to the target we own.
 *
 * So "is this the eye texture" was the wrong question. The right one is which
 * target carries the bulk of the scene draws, and how big it is. This keeps a
 * small table of every render target seen, with its dimensions and how many
 * draws landed on it, and reports the busiest few. The scene colour target is
 * the one with thousands of draws at or near eye resolution; shadow atlases
 * give themselves away by being square and off-size, and post passes by having
 * a handful of draws each.
 *
 * The per-draw cost stays one relaxed load and one increment: the table is only
 * searched when the target CHANGES, which is 64 times a frame against 1,465
 * draws.
 *
 * Resource pointers can be recycled by D3D after a free, so a long-lived entry
 * could in principle accrue counts from two different textures. For a census
 * read over five seconds inside one mission that is not worth defending
 * against, and the dimensions would make the collision obvious. */
/* One duplicate per bound slot, created on demand and reused. Shapes are
   compared before anything is allocated, so this is a handful of integer
   compares on the vast majority of binding changes. */
void ensure_dup_set(ID3D11RenderTargetView* const* rtvs, UINT num, int sceneSlot)
{
    ID3D11Device* dev = engine_device();
    if (dev == nullptr || rtvs == nullptr)
        return;

    const int count = static_cast<int>(num < kDupSlots ? num : kDupSlots);
    g_dupCount = count;

    for (int i = 0; i < count; ++i)
    {
        if (rtvs[i] == nullptr)
            continue;

        ID3D11Resource* r = nullptr;
        rtvs[i]->GetResource(&r);
        if (r == nullptr)
            continue;

        ID3D11Texture2D* t = nullptr;
        if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D),
                                        reinterpret_cast<void**>(&t))) && t != nullptr)
        {
            D3D11_TEXTURE2D_DESC td = {};
            t->GetDesc(&td);
            t->Release();

            const uint32_t fmt = static_cast<uint32_t>(td.Format);

            /* KEYED BY SHAPE, NOT BY SLOT.
             *
               Slot-keyed duplicates were about to become a real hazard. Now
               that a scene target is recognised by dimensions alone, the HDR
               target can bind at slot 0 where the albedo usually sits - and the
               slot-keyed version would have destroyed the albedo duplicate and
               rebuilt it in the other format, mid-frame, throwing away the very
               picture the blit reads.
             *
               A duplicate belongs to a SHAPE. Whichever slot that shape turns up
               in, it gets the same texture, and nothing another pass did is
               lost. */
            /* PER SLOT, NOT SHARED BY SHAPE.
             *
               A shape-keyed pool handed the same texture to every slot with
               that shape, and several of this G-buffer's targets share one.
               Binding one render target view to more than one output slot at
               once is invalid in D3D11: the runtime refuses the whole binding,
               the ENGINE'S targets stay bound, and the duplicated draws land in
               them. That is the left eye showing every object with a second
               copy inside it, and the right eye black because our duplicate
               never received anything.
             *
               Each slot therefore owns its surfaces. The cache is per slot and
               keyed by shape within it, which keeps the property the pool was
               introduced for: a slot that changes format does not destroy the
               duplicate the blit is reading. */
            int pool = -1;
            for (int k = 0; k < kShapesPerSlot; ++k)
            {
                if (g_slotCacheRtv[i][k] != nullptr &&
                    g_slotCacheW[i][k] == td.Width &&
                    g_slotCacheH[i][k] == td.Height &&
                    g_slotCacheF[i][k] == fmt)
                {
                    pool = k;
                    break;
                }
            }

            if (pool < 0)
            {
                for (int k = 0; k < kShapesPerSlot; ++k)
                {
                    if (g_slotCacheRtv[i][k] == nullptr) { pool = k; break; }
                }
            }

            if (pool >= 0 && g_slotCacheRtv[i][pool] == nullptr)
            {
                D3D11_TEXTURE2D_DESC d = td;
                d.MipLevels = 1;
                d.ArraySize = 1;
                d.Usage = D3D11_USAGE_DEFAULT;
                d.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
                d.CPUAccessFlags = 0;
                d.MiscFlags = 0;

                if (SUCCEEDED(dev->CreateTexture2D(&d, nullptr, &g_slotCacheTex[i][pool])) &&
                    SUCCEEDED(dev->CreateRenderTargetView(g_slotCacheTex[i][pool], nullptr,
                                                          &g_slotCacheRtv[i][pool])))
                {
                    g_slotCacheW[i][pool] = td.Width;
                    g_slotCacheH[i][pool] = td.Height;
                    g_slotCacheF[i][pool] = fmt;
                    BVR_INFO("Second eye: duplicate for slot %d, %ux%u fmt %u.",
                             i, td.Width, td.Height, fmt);
                }
                else
                {
                    if (g_slotCacheTex[i][pool]) { g_slotCacheTex[i][pool]->Release();
                                                   g_slotCacheTex[i][pool] = nullptr; }
                    g_slotCacheRtv[i][pool] = nullptr;
                }
            }

            if (pool >= 0 && g_slotCacheRtv[i][pool] != nullptr)
            {
                g_dupTexN[i] = g_slotCacheTex[i][pool];
                g_dupRtvN[i] = g_slotCacheRtv[i][pool];
                g_dupW[i] = td.Width;
                g_dupH[i] = td.Height;
                g_dupF[i] = fmt;
            }
        }
        r->Release();
    }

    /* WHICH DRAWS TO DUPLICATE AND WHICH DUPLICATE TO SHOW ARE DIFFERENT
       QUESTIONS, AND ANSWERING THEM WITH ONE VALUE BROKE THE COLOUR.
     *
       Duplication is decided by DIMENSIONS, because the sky and the ground are
       drawn into a same-sized target in another format and matching the format
       there lost them.
     *
       Display is not. The moment dimensions alone chose the blit source too,
       sceneSlot began picking whichever same-sized target happened to be first
       in the bound set - frequently the normals or the motion vectors - and the
       second eye came back as flat fields of green and red. That is a G-buffer
       channel shown as if it were a picture.
     *
       So the blit is pinned to the shape the census named BUSIEST, format
       included: 2048x2176 fmt 28, the scene colour, the one that produced a
       recognisable image. */
    const uint32_t showW = g_sceneW.load(std::memory_order_relaxed);
    const uint32_t showH = g_sceneH.load(std::memory_order_relaxed);
    const uint32_t showF = g_sceneFmt.load(std::memory_order_relaxed);

    g_dupTex = nullptr;

    /* WHICH OF THE SAME-SHAPED TARGETS IS THE SCENE COLOUR IS NOT DERIVABLE.
     *
       Three slots in this G-buffer are 2048x2176 fmt 28 - 0, 1 and 5 - and
       nothing about the shape says which of them carries the picture. Taking
       the first put a channel on screen that is not colour: flat fields of
       green and red with the geometry correct, which is what a normal or a
       motion-vector buffer looks like displayed as an image.
     *
       vp_second_draw_slot names it instead of inferring it. -1 keeps the
       first-match behaviour; 0, 1 or 5 pick one directly. */
    static int showSlot = -2;
    if (showSlot == -2)
    {
        showSlot = static_cast<int>(config_float("vp_second_draw_slot", -1.0f));
        BVR_INFO("Second eye: displaying %s. Three slots share the scene shape and only "
                 "one is the colour; vp_second_draw_slot = 0, 1 or 5 picks between "
                 "them.",
                 showSlot < 0 ? "the first slot matching the scene shape"
                              : "the slot named by vp_second_draw_slot");
    }

    if (showSlot >= 0 && showSlot < kDupSlots)
    {
        for (int k = 0; k < kShapesPerSlot; ++k)
        {
            if (g_slotCacheRtv[showSlot][k] == nullptr)
                continue;
            if (g_slotCacheW[showSlot][k] != showW ||
                g_slotCacheH[showSlot][k] != showH)
                continue;

            g_dupTex = g_slotCacheTex[showSlot][k];
            g_dupRtv = g_slotCacheRtv[showSlot][k];
            g_dupWidth = g_slotCacheW[showSlot][k];
            g_dupHeight = g_slotCacheH[showSlot][k];
            g_dupFormat = g_slotCacheF[showSlot][k];
            break;
        }
    }

    for (int i2 = 0; i2 < kDupSlots && g_dupTex == nullptr && showSlot < 0; ++i2)
        for (int k = 0; k < kShapesPerSlot; ++k)
        {
            if (g_slotCacheRtv[i2][k] == nullptr)
                continue;
            if (g_slotCacheW[i2][k] != showW || g_slotCacheH[i2][k] != showH ||
                g_slotCacheF[i2][k] != showF)
                continue;

            g_dupTex = g_slotCacheTex[i2][k];
            g_dupRtv = g_slotCacheRtv[i2][k];
            g_dupWidth = showW;
            g_dupHeight = showH;
            g_dupFormat = showF;
            break;
        }
}

/* Makes, once, a colour target with the same shape as the scene one. Failure is
   permanent and quiet after the first report: without it the redirect simply
   does not run, which is the pre-existing behaviour. */
bool ensure_dup_target(uint32_t w, uint32_t h, uint32_t fmt)
{
    if (g_dupRtv != nullptr && g_dupWidth == w && g_dupHeight == h && g_dupFormat == fmt)
        return true;

    if (g_dupRtv != nullptr) { g_dupRtv->Release(); g_dupRtv = nullptr; }
    if (g_dupTex != nullptr) { g_dupTex->Release(); g_dupTex = nullptr; }

    ID3D11Device* dev = engine_device();
    if (dev == nullptr)
        return false;

    D3D11_TEXTURE2D_DESC d = {};
    d.Width = w;
    d.Height = h;
    d.MipLevels = 1;
    d.ArraySize = 1;
    d.Format = static_cast<DXGI_FORMAT>(fmt);
    d.SampleDesc.Count = 1;
    d.Usage = D3D11_USAGE_DEFAULT;
    d.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;

    if (FAILED(dev->CreateTexture2D(&d, nullptr, &g_dupTex)) || g_dupTex == nullptr)
    {
        static bool warned = false;
        if (!warned)
        {
            warned = true;
            BVR_WARN("Second draw: could not create a %ux%u fmt %u duplicate target; "
                     "the redirect stays off.", w, h, fmt);
        }
        return false;
    }

    if (FAILED(dev->CreateRenderTargetView(g_dupTex, nullptr, &g_dupRtv)))
    {
        g_dupTex->Release();
        g_dupTex = nullptr;
        return false;
    }

    g_dupWidth = w;
    g_dupHeight = h;
    g_dupFormat = fmt;

    BVR_INFO("Second draw: duplicate scene target created, %ux%u fmt %u. Scene draws "
             "will now be issued into it as well as into the engine's own.",
             w, h, fmt);
    return true;
}

/* A read-only view of whatever depth buffer the engine has bound, cached by the
   view it was made from. Null in, null out - a pass with no depth needs none.
   D3D11_DSV_READ_ONLY_DEPTH is the whole of it: same texture, same format, same
   comparison, writes forbidden. */
/* THE SECOND EYE NEEDS ITS OWN DEPTH, NOT A READ-ONLY VIEW OF THE ENGINE'S.
 *
 * The read-only view was wrong and the right eye went black because of it. The
 * redirect fires IMMEDIATELY AFTER each original draw, so by the time the
 * duplicate is issued the engine's depth buffer already contains that very
 * geometry. The duplicate then tests the same surfaces at essentially the same
 * depth, fails, and writes nothing - every draw rejected, all frame.
 *
 * Read-only was right about one thing: writing to the engine's depth corrupts
 * the frame it is drawing, which was half of what turned the left eye to noise.
 * So the answer is neither sharing nor read-only sharing. It is a depth buffer
 * of our own, matching the engine's shape, cleared once a frame, which the
 * duplicated draws build up exactly as the engine builds its own.
 *
 * THE CLEAR VALUE IS MEASURED, NOT ASSUMED.
 *
 * Clearing to the wrong end of the range would reject everything just as
 * thoroughly, and Bannerlord's depth direction is not something to guess at -
 * reversed-Z clears to 0 and conventional depth to 1, and picking wrong looks
 * exactly like this bug. So ClearDepthStencilView is hooked and whatever the
 * engine clears ITS depth to is what we clear ours to. */
ID3D11Texture2D*        g_dupDepthTex = nullptr;
ID3D11DepthStencilView* g_dupDsv = nullptr;
ID3D11DepthStencilView* g_dupDsvFor = nullptr;

std::atomic<float>    g_engineDepthClear{ 1.0f };
std::atomic<uint32_t> g_engineStencilClear{ 0 };
std::atomic<uint32_t> g_engineClearFlags{ D3D11_CLEAR_DEPTH };

/* The diagnostic state: depth on, comparison ALWAYS, writes off. Built once if
   vp_second_draw_nodepth is set, left null otherwise so the redirect skips the
   save/restore entirely. */
ID3D11DepthStencilState* g_alwaysDepth = nullptr;

void ensure_always_depth()
{
    static bool tried = false;
    if (tried)
        return;
    tried = true;

    if (config_float("vp_second_draw_nodepth", 0.0f) <= 0.5f)
        return;

    ID3D11Device* dev = engine_device();
    if (dev == nullptr)
        return;

    D3D11_DEPTH_STENCIL_DESC d = {};
    d.DepthEnable = TRUE;
    d.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
    d.DepthFunc = D3D11_COMPARISON_ALWAYS;

    if (SUCCEEDED(dev->CreateDepthStencilState(&d, &g_alwaysDepth)))
        BVR_WARN("Second eye: DEPTH TEST DISABLED for the duplicated draws "
                 "(vp_second_draw_nodepth = 1). Occlusion will be wrong - far things "
                 "will paint over near ones. This is a diagnostic: a picture appearing "
                 "means depth was the blocker, and a still-black eye means it was not.");
}

/* Cached by SHAPE, not by the view pointer.
 *
 * Keying on the view recreated the buffer 737 times in one session against six
 * colour targets created once: the engine binds many different depth views -
 * every shadow cascade has its own - and each unfamiliar pointer threw away the
 * depth and built a new one. A freshly created depth texture is UNINITIALISED,
 * so the duplicated draws were testing against garbage, which under reversed-Z
 * rejects nearly everything. That alone would produce a black second eye.
 *
 * One buffer per shape, made once and kept. */
uint32_t g_dupDepthW = 0, g_dupDepthH = 0, g_dupDepthF = 0;

ID3D11DepthStencilView* dup_depth_for(ID3D11DepthStencilView* src)
{
    if (src == nullptr)
        return nullptr;

    ID3D11Device* dev = engine_device();
    if (dev == nullptr)
        return nullptr;

    D3D11_DEPTH_STENCIL_VIEW_DESC sd = {};
    src->GetDesc(&sd);

    ID3D11Resource* res = nullptr;
    src->GetResource(&res);
    if (res == nullptr)
        return nullptr;

    ID3D11Texture2D* srcTex = nullptr;
    D3D11_TEXTURE2D_DESC td = {};
    if (SUCCEEDED(res->QueryInterface(__uuidof(ID3D11Texture2D),
                                      reinterpret_cast<void**>(&srcTex))) && srcTex != nullptr)
    {
        srcTex->GetDesc(&td);
        srcTex->Release();
    }
    res->Release();

    if (td.Width == 0)
        return nullptr;

    /* Already have one of this shape. This is the common case by an enormous
       margin and is what stops the buffer being rebuilt for every cascade. */
    if (g_dupDsv != nullptr &&
        g_dupDepthW == td.Width && g_dupDepthH == td.Height &&
        g_dupDepthF == static_cast<uint32_t>(td.Format))
    {
        g_dupDsvFor = src;
        return g_dupDsv;
    }

    if (g_dupDsv != nullptr) { g_dupDsv->Release(); g_dupDsv = nullptr; }
    if (g_dupDepthTex != nullptr) { g_dupDepthTex->Release(); g_dupDepthTex = nullptr; }

    D3D11_TEXTURE2D_DESC d = td;
    d.MipLevels = 1;
    d.ArraySize = 1;
    d.Usage = D3D11_USAGE_DEFAULT;
    d.BindFlags = D3D11_BIND_DEPTH_STENCIL;
    d.CPUAccessFlags = 0;
    d.MiscFlags = 0;

    /* The view keeps the engine's own description - same typed depth format,
       same dimension - so the comparison behaves identically. Only the Flags
       are cleared: this one is written to. */
    D3D11_DEPTH_STENCIL_VIEW_DESC vd = sd;
    vd.Flags = 0;

    if (FAILED(dev->CreateTexture2D(&d, nullptr, &g_dupDepthTex)) ||
        FAILED(dev->CreateDepthStencilView(g_dupDepthTex, &vd, &g_dupDsv)))
    {
        if (g_dupDepthTex) { g_dupDepthTex->Release(); g_dupDepthTex = nullptr; }
        g_dupDsv = nullptr;
        return nullptr;
    }

    g_dupDsvFor = src;
    g_dupDepthW = td.Width;
    g_dupDepthH = td.Height;
    g_dupDepthF = static_cast<uint32_t>(td.Format);
    BVR_INFO("Second eye: own depth buffer created, %ux%u fmt %u, cleared to %.3f like "
             "the engine's. The read-only view it replaces rejected every duplicated "
             "draw, because the engine's depth already held the same geometry.",
             td.Width, td.Height, static_cast<uint32_t>(td.Format),
             g_engineDepthClear.load(std::memory_order_relaxed));
    return g_dupDsv;
}

/* Issues the draw a second time into the duplicate target. Called from all four
   trampolines with a lambda that re-invokes the original with its own
   arguments, because each draw entry has a different signature and none of that
   detail belongs here. */
/* THE TESSELLATION STAGES NEED THE SECOND EYE'S MATRICES TOO.
 *
 * The ground is the one thing still absent from the second eye, and this is the
 * likeliest reason. Bannerlord's terrain is a tessellated heightmap: its
 * vertices are produced in the DOMAIN shader, and if the view-projection is
 * consumed there rather than in the vertex shader, swapping only VS constant
 * buffers leaves the terrain positioned for the FIRST eye. Drawn at the wrong
 * place into the second eye's target, where a depth prepass that also ran with
 * second-eye matrices then rejects it - so it is absent rather than merely
 * displaced, which is exactly how it looks.
 *
 * Each stage is asked what it has bound and only swapped when that buffer is
 * one we hold a shadow for. A stage that does not use the view buffer is
 * therefore untouched, and a slot index that means something different in the
 * domain stage than in the vertex stage cannot be mixed up.
 */
struct StageSwap
{
    ID3D11Buffer* saved[kMaxVsSlots];
    bool          swapped[kMaxVsSlots];
};

ID3D11Buffer* shadow_of(ID3D11Buffer* engineBuffer)
{
    if (engineBuffer == nullptr)
        return nullptr;

    for (int s = 0; s < g_shadowCbCount; ++s)
        if (g_shadowCb[s].engineBuffer == static_cast<ID3D11Resource*>(engineBuffer))
            return g_shadowCb[s].shadow;

    return nullptr;
}

enum StageKind { StageHull, StageDomain };

void swap_stage_cbs(ID3D11DeviceContext* c, StageKind kind, StageSwap& st)
{
    for (int s = 0; s < kMaxVsSlots; ++s)
    {
        st.saved[s] = nullptr;
        st.swapped[s] = false;

        if (g_slotShadow[s] == nullptr)
            continue;   /* only slots the view buffer is known to use */

        ID3D11Buffer* cur = nullptr;
        if (kind == StageHull)
            c->HSGetConstantBuffers(static_cast<UINT>(s), 1, &cur);
        else
            c->DSGetConstantBuffers(static_cast<UINT>(s), 1, &cur);

        ID3D11Buffer* shadow = shadow_of(cur);
        if (shadow == nullptr)
        {
            if (cur != nullptr)
                cur->Release();
            continue;
        }

        st.saved[s] = cur;          /* reference kept, released on restore */
        st.swapped[s] = true;

        if (kind == StageHull)
            c->HSSetConstantBuffers(static_cast<UINT>(s), 1, &shadow);
        else
            c->DSSetConstantBuffers(static_cast<UINT>(s), 1, &shadow);
    }
}

void restore_stage_cbs(ID3D11DeviceContext* c, StageKind kind, StageSwap& st)
{
    for (int s = 0; s < kMaxVsSlots; ++s)
    {
        if (!st.swapped[s])
            continue;

        if (kind == StageHull)
            c->HSSetConstantBuffers(static_cast<UINT>(s), 1, &st.saved[s]);
        else
            c->DSSetConstantBuffers(static_cast<UINT>(s), 1, &st.saved[s]);

        if (st.saved[s] != nullptr)
            st.saved[s]->Release();
    }
}

template <typename Reissue>
void redirect_second_draw(ID3D11DeviceContext* c, Reissue&& reissue)
{
    if (g_inRedirect || !redirect_enabled() || g_dupRtv == nullptr)
        return;

    const bool isScene = g_rtIsScene.load(std::memory_order_relaxed);
    const bool isPrepass = g_rtIsSceneDepth.load(std::memory_order_relaxed);

    if (!isScene && !isPrepass)
        return;

    /* The prepass writes DEPTH ONLY, into our depth, with no colour bound - the
       same shape the engine's own prepass has. Without this our depth buffer
       stays at its cleared value and the EQUAL-tested G-buffer pass that
       follows rejects every single draw, which is a completely black eye. */
    if (isPrepass)
    {
        if (g_dupDsv == nullptr)
            return;

        g_inRedirect = true;

        ID3D11RenderTargetView* noRtv[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
        ID3D11RenderTargetView* oldRtvP[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
        ID3D11DepthStencilView* oldDsvP = nullptr;
        c->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, oldRtvP, &oldDsvP);

        ID3D11Buffer* savedP[kMaxVsSlots] = {};
        bool swappedP[kMaxVsSlots] = {};
        for (int s = 0; s < kMaxVsSlots; ++s)
        {
            if (g_slotShadow[s] == nullptr)
                continue;
            c->VSGetConstantBuffers(static_cast<UINT>(s), 1, &savedP[s]);
            c->VSSetConstantBuffers(static_cast<UINT>(s), 1, &g_slotShadow[s]);
            swappedP[s] = true;
        }

        /* The tessellation stages as well - the prepass draws the terrain too,
           and a depth prepass laid down with first-eye terrain is what would
           reject the second eye's ground even once the geometry is right. */
        StageSwap hsP, dsP;
        swap_stage_cbs(c, StageHull, hsP);
        swap_stage_cbs(c, StageDomain, dsP);

        c->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, noRtv, g_dupDsv);
        reissue();

        restore_stage_cbs(c, StageDomain, dsP);
        restore_stage_cbs(c, StageHull, hsP);
        c->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, oldRtvP, oldDsvP);

        for (int s = 0; s < kMaxVsSlots; ++s)
        {
            if (!swappedP[s])
                continue;
            c->VSSetConstantBuffers(static_cast<UINT>(s), 1, &savedP[s]);
            if (savedP[s] != nullptr)
                savedP[s]->Release();
        }

        for (auto& r : oldRtvP)
            if (r != nullptr)
                r->Release();
        if (oldDsvP != nullptr)
            oldDsvP->Release();

        g_inRedirect = false;
        g_cPrepassRedirected.fetch_add(1, std::memory_order_relaxed);
        return;
    }

    g_inRedirect = true;

    /* The depth buffer is kept. Both eyes are drawn from the same viewpoint for
       now, so the engine's own depth is exactly right for the duplicate; when
       the eye offset arrives this is the next thing that has to be its own. */
    ID3D11RenderTargetView* oldRtv[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
    ID3D11DepthStencilView* oldDsv = nullptr;
    c->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, oldRtv, &oldDsv);

    /* THE OTHER EYE'S CONSTANTS, FOR THE DURATION OF ONE DRAW.
     *
       Which slot to swap is not guessed: the VSSetConstantBuffers hook records
       where the engine put the buffer that carries the view matrix, so this
       binds the shadow into that same slot and puts the engine's own back
       immediately afterwards. Binding is one call and no allocation - the
       shadow's contents were built once, when the engine uploaded the original.

       When there is no shadow yet the draw still happens, and the duplicate is
       simply a copy of the first eye. That is the state this arrived in and it
       remains a truthful fallback rather than a broken one. */
    /* EVERY slot holding a shadowed buffer, swapped together.
     *
       Swapping one and leaving the rest is what produced two eyes with no depth
       between them: the shader read whichever view buffer it wanted, and most of
       them still held the drawn eye's matrices. */
    ID3D11Buffer* saved[kMaxVsSlots] = {};
    bool swapped[kMaxVsSlots] = {};
    bool swappedAny = false;

    for (int s = 0; s < kMaxVsSlots; ++s)
    {
        ID3D11Buffer* shadow = g_slotShadow[s];
        if (shadow == nullptr)
            continue;

        c->VSGetConstantBuffers(static_cast<UINT>(s), 1, &saved[s]);
        c->VSSetConstantBuffers(static_cast<UINT>(s), 1, &shadow);

        /* Tracked separately from `saved`, because a slot can legitimately have
           had NOTHING bound. Keying the restore on saved[s] being non-null left
           our shadow in place on exactly those slots, and every ENGINE draw
           after it in that frame then read the second eye's matrices - which
           corrupts the eye the engine is drawing rather than the duplicate. */
        swapped[s] = true;
        swappedAny = true;
    }

    if (swappedAny)
        g_cShadowBound.fetch_add(1, std::memory_order_relaxed);

    /* WHERE DO THE DUPLICATED DRAWS ACTUALLY LAND?
     *
       The right eye shows ground and terrain and no soldiers, which is the
       exact inverse of what it showed when only one target was bound. Both
       cannot be explained by the draws failing - they are landing somewhere,
       and the blit reads one fixed slot.
     *
       A deferred renderer does not have to put its scene colour in the same
       slot for every pass. If the terrain pass writes it at slot 0 and the
       skinned-mesh pass writes it at slot 2, then duplicating both is correct
       and displaying only slot 0 loses half of it - which is what is on screen.
     *
       This counts draws per scene slot so the answer is a number rather than a
       theory. */
    {
        const int ss = g_sceneRtSlot.load(std::memory_order_relaxed);
        if (ss >= 0 && ss < kDupSlots)
            ++g_drawsPerSceneSlot[ss];
    }

    /* The live sub-rectangle, from the draw that is using it. */
    {
        UINT nvp = 1;
        D3D11_VIEWPORT cur = {};
        c->RSGetViewports(&nvp, &cur);
        if (nvp > 0 && cur.Width > 0.0f)
        {
            g_sceneVpW.store(cur.Width, std::memory_order_relaxed);
            g_sceneVpH.store(cur.Height, std::memory_order_relaxed);
        }
    }

    /* ONLY THE SCENE TARGET IS REPLACED. EVERY OTHER ONE STAYS BOUND.
     *
       Binding ours alone is what left the right eye with soldiers on a black
       field. A deferred renderer writes several outputs at once, and a draw
       issued with the rest of the G-buffer unbound loses whatever it wrote to
       them - so the passes whose visible result comes from another target
       contributed nothing to the duplicate. Terrain, sand and sky went that
       way; the skinned characters survived because their colour lands in the
       target we had. */
    /* OUR WHOLE SET. Nothing of the engine's is bound, so nothing the second eye
       writes can reach the frame the engine is drawing. */
    ID3D11RenderTargetView* dupSet[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
    for (int i = 0; i < D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT; ++i)
        dupSet[i] = (i < g_dupCount) ? g_dupRtvN[i] : nullptr;

    /* THE DEPTH BUFFER, READ-ONLY.
     *
       The engine's depth is the right thing to TEST against - the two eyes are
       65 mm apart, so the occlusion it describes is very nearly the second eye's
       - but writing to it would corrupt the depth the engine's own frame
       depends on, which is the other half of what turned the left eye to noise.
       A read-only view gives the test and forbids the write, and it leaves the
       engine's depth-stencil STATE untouched, so a reversed-Z comparison keeps
       working without this having to know which way round it is. */
    ID3D11DepthStencilView* dsv = dup_depth_for(oldDsv);

    /* WHAT STATE ARE THESE DRAWS ACTUALLY RUNNING UNDER?
     *
       The duplicate is black while 488 draws a frame are being re-issued into
       it, so they execute and write nothing. Depth was the obvious suspect and
       is now ours, cleared to the engine's own value - so the next question is
       what the depth COMPARISON is, and that decides everything.
     *
       A deferred renderer very often runs a depth prepass and then draws its
       G-buffer with the test set to EQUAL. Our depth never receives that
       prepass, because those draws bind no colour target and the redirect skips
       them - so an EQUAL test would reject every single duplicated draw, which
       is exactly this symptom. GREATER or LESS would mean the fault is
       elsewhere: blend state, colour write masks, or the draws being culled.
     *
       Once every five seconds, so it costs nothing. */
    {
        static uint64_t lastLog = 0;
        const uint64_t nowMs = GetTickCount64();
        if (nowMs - lastLog >= 5000)
        {
            lastLog = nowMs;

            ID3D11DepthStencilState* dss = nullptr;
            UINT stencilRef = 0;
            c->OMGetDepthStencilState(&dss, &stencilRef);

            ID3D11BlendState* bs = nullptr;
            FLOAT bf[4] = {};
            UINT mask = 0;
            c->OMGetBlendState(&bs, bf, &mask);

            D3D11_DEPTH_STENCIL_DESC dd2 = {};
            if (dss != nullptr) dss->GetDesc(&dd2);

            D3D11_BLEND_DESC bd = {};
            if (bs != nullptr) bs->GetDesc(&bd);

            BVR_INFO("Second eye draw state: depth enable %d, write mask %d, func %d "
                     "(4 = EQUAL, which with a depth prepass we never receive would "
                     "reject everything); blend enable %d, RT0 write mask 0x%X, sample "
                     "mask 0x%X.",
                     dd2.DepthEnable, static_cast<int>(dd2.DepthWriteMask),
                     static_cast<int>(dd2.DepthFunc),
                     bd.RenderTarget[0].BlendEnable,
                     bd.RenderTarget[0].RenderTargetWriteMask, mask);

            if (dss) dss->Release();
            if (bs) bs->Release();
        }
    }

    /* DEPTH TEST OFF, AS A DIAGNOSTIC, WHEN ASKED.
     *
       The duplicate is black while hundreds of draws a frame are re-issued into
       it. Depth is the leading suspect and the measured state says why: the
       G-buffer is drawn with func 3 - EQUAL - and writes disabled, which is the
       second half of a depth prepass. An EQUAL test only passes where the depth
       buffer already holds that exact surface, so unless our depth received the
       prepass with the SAME second-eye matrices, every one of those draws
       fails.
     *
       vp_second_draw_nodepth = 1 replaces the comparison with ALWAYS for the
       duplicated draws only. It gives wrong occlusion - far things will paint
       over near ones - and that is fine, because it is not a way to render, it
       is a way to find out:
     *
         picture appears  -> depth was the blocker, and the fix is to make our
                             depth receive the prepass properly.
         still black      -> depth is innocent and the fault is elsewhere
                             entirely, which saves building the prepass path for
                             nothing. */
    ID3D11DepthStencilState* oldDss = nullptr;
    UINT oldStencilRef = 0;
    if (g_alwaysDepth != nullptr)
    {
        c->OMGetDepthStencilState(&oldDss, &oldStencilRef);
        c->OMSetDepthStencilState(g_alwaysDepth, 0);
    }

    /* Hull and domain as well as vertex. The terrain is tessellated, so its
       vertices are produced in the domain stage; swapping only the vertex
       stage leaves the ground at the first eye's position. See swap_stage_cbs. */
    StageSwap hsSwap, dsSwap;
    swap_stage_cbs(c, StageHull, hsSwap);
    swap_stage_cbs(c, StageDomain, dsSwap);

    c->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, dupSet, dsv);
    reissue();
    c->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, oldRtv, oldDsv);

    restore_stage_cbs(c, StageDomain, dsSwap);
    restore_stage_cbs(c, StageHull, hsSwap);

    if (g_alwaysDepth != nullptr)
    {
        c->OMSetDepthStencilState(oldDss, oldStencilRef);
        if (oldDss != nullptr)
            oldDss->Release();
    }

    /* Every slot we touched, restored to exactly what was there - including the
       ones that held nothing, which is what binding null puts back. */
    for (int s = 0; s < kMaxVsSlots; ++s)
    {
        if (!swapped[s])
            continue;

        c->VSSetConstantBuffers(static_cast<UINT>(s), 1, &saved[s]);
        if (saved[s] != nullptr)
            saved[s]->Release();
    }

    for (auto& r : oldRtv)
        if (r != nullptr)
            r->Release();
    if (oldDsv != nullptr)
        oldDsv->Release();

    g_inRedirect = false;
    g_cRedirected.fetch_add(1, std::memory_order_relaxed);
}

/* WHICH of the bound targets is the scene one, and at which index.
 *
 * Only rtvs[0] used to be looked at. This is a deferred renderer: it binds a
 * G-buffer of several targets at once, and the scene colour is not always the
 * first of them. The right eye came back with the soldiers and the horse drawn
 * and the terrain, sand and sky missing - the draws whose scene output was not
 * in slot 0 were never recognised, so they were never duplicated.
 *
 * The index is remembered so the redirect can replace THAT target and leave
 * every other one bound, instead of binding ours alone and dropping the rest. */
void note_render_targets(ID3D11RenderTargetView* const* rtvs, UINT num,
                        ID3D11DepthStencilView* dsv)
{
    /* Our own rebinding, not the engine's. Counting it would corrupt the census
       and moving g_curRtStat would strand the draw being duplicated. */
    if (g_inRedirect)
        return;

    g_cRtChanges.fetch_add(1, std::memory_order_relaxed);

    /* Every bound target, not just the first. The scene colour is whichever one
       matches the shape the census settled on, wherever it sits in the set. */
    ID3D11RenderTargetView* rtv = nullptr;
    int sceneSlot = -1;

    if (rtvs != nullptr)
    {
        const uint32_t wantW = g_sceneW.load(std::memory_order_relaxed);
        const uint32_t wantH = g_sceneH.load(std::memory_order_relaxed);
        const uint32_t wantF = g_sceneFmt.load(std::memory_order_relaxed);

        for (UINT i = 0; i < num && i < D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT; ++i)
        {
            if (rtvs[i] == nullptr)
                continue;

            if (rtv == nullptr)
                rtv = rtvs[i];   /* for the census and the eye-texture check */

            if (wantW == 0 || sceneSlot >= 0)
                continue;

            ID3D11Resource* r = nullptr;
            rtvs[i]->GetResource(&r);
            if (r == nullptr)
                continue;

            ID3D11Texture2D* t = nullptr;
            if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D),
                                            reinterpret_cast<void**>(&t))) && t != nullptr)
            {
                D3D11_TEXTURE2D_DESC td = {};
                t->GetDesc(&td);
                t->Release();

                /* Dimensions only. See the note at the scene pick: a second target
                   of the same size in a different format carries the sky and
                   the ground, and matching the format left it out. */
                if (td.Width == wantW && td.Height == wantH)
                    sceneSlot = static_cast<int>(i);
            }
            r->Release();
        }
    }

    g_sceneRtSlot.store(sceneSlot, std::memory_order_relaxed);

    /* THE DEPTH PREPASS HAS TO BE DUPLICATED TOO.
     *
       With a real depth test the second eye went completely black, and this is
       why. The G-buffer is drawn with the comparison set to EQUAL and depth
       writes off - it only passes where the depth buffer ALREADY holds that
       exact surface, laid down by an earlier depth-only pass. Those draws bind
       no colour target at all, so everything above skipped them, our depth
       stayed at its cleared value, and every EQUAL test failed.
     *
       Telling the prepass from the shadow cascades is a matter of shape: the
       prepass uses the scene's own depth buffer, the cascades use their own
       square atlases. So the scene depth's shape is learned here, while the
       colour targets ARE bound and there is no ambiguity, and a depth-only
       binding of that same shape is then recognisable as the prepass.
     *
       One frame of delay before the shape is known, and nothing else. */
    uint32_t dW = 0, dH = 0, dF = 0;
    if (dsv != nullptr)
    {
        ID3D11Resource* dres = nullptr;
        dsv->GetResource(&dres);
        if (dres != nullptr)
        {
            ID3D11Texture2D* dt = nullptr;
            if (SUCCEEDED(dres->QueryInterface(__uuidof(ID3D11Texture2D),
                                               reinterpret_cast<void**>(&dt))) && dt != nullptr)
            {
                D3D11_TEXTURE2D_DESC dtd = {};
                dt->GetDesc(&dtd);
                dt->Release();
                dW = dtd.Width; dH = dtd.Height; dF = static_cast<uint32_t>(dtd.Format);
            }
            dres->Release();
        }
    }

    if (sceneSlot >= 0 && dW != 0)
    {
        g_sceneDepthW.store(dW, std::memory_order_relaxed);
        g_sceneDepthH.store(dH, std::memory_order_relaxed);
        g_sceneDepthF.store(dF, std::memory_order_relaxed);
    }

    /* A depth-only binding on the scene's depth buffer: the prepass. */
    const uint32_t wantDW = g_sceneDepthW.load(std::memory_order_relaxed);
    g_rtIsSceneDepth.store(sceneSlot < 0 && wantDW != 0 && dW == wantDW &&
                           dH == g_sceneDepthH.load(std::memory_order_relaxed) &&
                           dF == g_sceneDepthF.load(std::memory_order_relaxed),
                           std::memory_order_relaxed);

    /* With the scene bound, make sure we own a target of matching shape for
       every slot in the set - so the duplicated draws have somewhere of their
       own to put every output, and touch none of the engine's. */
    if (sceneSlot >= 0 && redirect_enabled())
        ensure_dup_set(rtvs, num, sceneSlot);

    if (sceneSlot >= 0 && redirect_enabled())
        ensure_always_depth();

    /* The pass context, for the constant-buffer patcher. Stored before every
       early return below, because a depth-only binding takes the first of them
       and a depth-only binding is exactly the shadow map. */
    g_curHasRtv.store(rtv != nullptr, std::memory_order_relaxed);
    g_curTgtW.store(dW, std::memory_order_relaxed);
    g_curTgtH.store(dH, std::memory_order_relaxed);

    if (rtv == nullptr)
    {
        g_rtIsEye.store(false, std::memory_order_relaxed);
        g_rtIsScene.store(false, std::memory_order_relaxed);
        g_curRtStat.store(-1, std::memory_order_relaxed);
        return;
    }

    ID3D11Resource* res = nullptr;
    rtv->GetResource(&res);
    if (res == nullptr)
    {
        g_rtIsEye.store(false, std::memory_order_relaxed);
        g_rtIsScene.store(false, std::memory_order_relaxed);
        g_curRtStat.store(-1, std::memory_order_relaxed);
        return;
    }

    /* Pointer identity against the texture the eye is rendered into. Kept even
       though it is now known to be the resolve target and not the scene one:
       one draw a frame is itself the finding, and a change in it would matter. */
    ID3D11Texture2D* eye0 = eye_texture(0);
    g_rtIsEye.store(eye0 != nullptr && res == static_cast<ID3D11Resource*>(eye0),
                    std::memory_order_relaxed);

    /* Measured on every binding change rather than once per pointer, because
       the pointer is no longer the identity. That is 64 QueryInterface/GetDesc
       pairs a frame against 1,465 draws - the whole reason this is not done in
       the draw itself. */
    int found = -1;
    ID3D11Texture2D* tex = nullptr;
    if (SUCCEEDED(res->QueryInterface(__uuidof(ID3D11Texture2D),
                                      reinterpret_cast<void**>(&tex))) &&
        tex != nullptr)
    {
        D3D11_TEXTURE2D_DESC d = {};
        tex->GetDesc(&d);
        tex->Release();

        const uint32_t fmt = static_cast<uint32_t>(d.Format);

        /* Is this the surface the scene is drawn into? Decided here, once per
           binding change, so the draw path is a single relaxed load. The shape
           comes from the previous window's census, so it is empty for the first
           five seconds of a session and correct from then on. */
        /* The scene is bound if ANY of the targets matched above, not merely if
           the first one did. */
        g_rtIsScene.store(sceneSlot >= 0, std::memory_order_relaxed);
        (void)fmt;

        for (int i = 0; i < g_rtStatCount; ++i)
        {
            if (g_rtStats[i].width == d.Width &&
                g_rtStats[i].height == d.Height &&
                g_rtStats[i].format == fmt)
            {
                found = i;
                break;
            }
        }

        if (found < 0 && g_rtStatCount < kRtStats)
        {
            found = g_rtStatCount++;
            g_rtStats[found].sample = res;
            g_rtStats[found].width = d.Width;
            g_rtStats[found].height = d.Height;
            g_rtStats[found].format = fmt;
            g_rtStats[found].draws = 0;
        }
    }

    g_curRtStat.store(found, std::memory_order_relaxed);
    res->Release();
}

using PFN_OMSetRenderTargets = void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT,
                                                         ID3D11RenderTargetView* const*,
                                                         ID3D11DepthStencilView*);

constexpr int kRtVariants = 4;
void* g_rtTramp[kRtVariants] = {};
void* g_rtTarget[kRtVariants] = {};

template <int V>
void STDMETHODCALLTYPE tramp_om_set_rt(ID3D11DeviceContext* c, UINT num,
                                       ID3D11RenderTargetView* const* rtvs,
                                       ID3D11DepthStencilView* dsv)
{
    /* Native stereo's transient twin binds are undone before the engine draws
       again, so the pass context stays the engine's (TEST 181). Never true in
       any other mode. */
    if (!stereo_in_dup())
        note_render_targets(rtvs, num, dsv);
    reinterpret_cast<PFN_OMSetRenderTargets>(g_rtTramp[V])(c, num, rtvs, dsv);
}

/* THE VARIANT THIS ENGINE ACTUALLY BINDS THROUGH.
 *
 * Slot 33 was inline-hooked three times over and fired ZERO times, while the
 * draw hooks on the same vtable counted 1,465 per frame. An engine that uses
 * compute binds its targets and its unordered-access views in one call, and
 * that is a different entry point - slot 34. Exactly the same mistake as
 * watching DrawIndexed while rgl instanced everything, and as watching
 * UpdateSubresource while it uploaded through Map.
 *
 * NumRTVs may be D3D11_KEEP_RENDER_TARGETS_AND_DEPTH_STENCIL, which means
 * "leave the colour targets as they are and only change the UAVs". That is not
 * a target change and must not be treated as one - reading rtvs[0] on that path
 * would be reading a pointer the caller never supplied. */
/* Whatever the engine clears its depth to is what ours gets. Reversed-Z clears
   to 0 and conventional depth to 1, and choosing wrong rejects every duplicated
   draw - which is indistinguishable from the read-only-depth bug this replaced.
   Measuring it costs one hook and removes the guess entirely. */
/* IS THE LIGHTING A COMPUTE PASS?
 *
 * The right eye now shows a correctly lit, tonemapped sky and distant terrain,
 * and the characters as black silhouettes. That split is the architecture
 * speaking: forward-drawn things carry their own colour and duplicate fine,
 * deferred things get their colour from a lighting pass that reads the G-buffer
 * afterwards.
 *
 * If that pass were an ordinary fullscreen DRAW it would already be duplicated -
 * it writes a scene-shaped target, so the redirect would catch it - and the
 * characters would come out lit for the wrong eye rather than black. Black says
 * it is not being duplicated at all, and the likeliest reason is that modern
 * deferred lighting is a COMPUTE dispatch writing through an unordered-access
 * view. Nothing here hooks Dispatch, so such a pass is invisible to all of it.
 *
 * This counts them and reports what the compute stage has bound, which decides
 * whether the next piece of work is "redirect some more draws" or "duplicate a
 * compute pass with its own inputs and outputs". */
using PFN_Dispatch = void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT, UINT);

constexpr int kDispatchVariants = 4;
void* g_dispatchTramp[kDispatchVariants] = {};
void* g_dispatchTarget[kDispatchVariants] = {};
std::atomic<uint64_t> g_cDispatch{0};


void report_compute_bindings(ID3D11DeviceContext* c);

template <int V>
void STDMETHODCALLTYPE tramp_dispatch(ID3D11DeviceContext* c, UINT x, UINT y, UINT z)
{
    if (!g_inRedirect)
    {
        g_cDispatch.fetch_add(1, std::memory_order_relaxed);
        report_compute_bindings(c);

        if (frame_map_capturing())
        {
            char fa[64];
            _snprintf_s(fa, _TRUNCATE, "groups=%u,%u,%u", x, y, z);
            frame_map_compute(c, "Dispatch", fa);
        }
    }
    StereoOp sop;
    const bool sdup = stereo_begin(c, sop, true);
    reinterpret_cast<PFN_Dispatch>(g_dispatchTramp[V])(c, x, y, z);
    if (sdup)
    {
        stereo_bind(c, sop);
        reinterpret_cast<PFN_Dispatch>(g_dispatchTramp[V])(c, x, y, z);
        stereo_end(c, sop);
    }
}

using PFN_ClearDepthStencilView =
    void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11DepthStencilView*, UINT,
                              FLOAT, UINT8);

/* What a compute dispatch has bound, once every five seconds. Shapes only - it
   is the sizes that identify a full-screen lighting pass over the G-buffer. */
void report_compute_bindings(ID3D11DeviceContext* c)
{
    static uint64_t lastLog = 0;
    const uint64_t nowMs = GetTickCount64();
    if (nowMs - lastLog < 5000)
        return;
    lastLog = nowMs;

    const uint32_t sceneW = g_sceneW.load(std::memory_order_relaxed);

    char line[512];
    int n = 0;
    n += _snprintf_s(line + n, sizeof(line) - n, _TRUNCATE, "UAV:");

    for (UINT i = 0; i < 4; ++i)
    {
        ID3D11UnorderedAccessView* uav = nullptr;
        c->CSGetUnorderedAccessViews(i, 1, &uav);
        if (uav == nullptr)
            continue;

        ID3D11Resource* r = nullptr;
        uav->GetResource(&r);
        if (r != nullptr)
        {
            ID3D11Texture2D* t = nullptr;
            if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D),
                                            reinterpret_cast<void**>(&t))) && t != nullptr)
            {
                D3D11_TEXTURE2D_DESC d = {};
                t->GetDesc(&d);
                t->Release();
                n += _snprintf_s(line + n, sizeof(line) - n, _TRUNCATE,
                                 " u%u=%ux%u/%u%s", i, d.Width, d.Height,
                                 static_cast<uint32_t>(d.Format),
                                 d.Width == sceneW ? "*" : "");
            }
            r->Release();
        }
        uav->Release();
    }

    n += _snprintf_s(line + n, sizeof(line) - n, _TRUNCATE, "  SRV:");

    for (UINT i = 0; i < 6; ++i)
    {
        ID3D11ShaderResourceView* srv = nullptr;
        c->CSGetShaderResources(i, 1, &srv);
        if (srv == nullptr)
            continue;

        ID3D11Resource* r = nullptr;
        srv->GetResource(&r);
        if (r != nullptr)
        {
            ID3D11Texture2D* t = nullptr;
            if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D),
                                            reinterpret_cast<void**>(&t))) && t != nullptr)
            {
                D3D11_TEXTURE2D_DESC d = {};
                t->GetDesc(&d);
                t->Release();
                n += _snprintf_s(line + n, sizeof(line) - n, _TRUNCATE,
                                 " t%u=%ux%u/%u%s", i, d.Width, d.Height,
                                 static_cast<uint32_t>(d.Format),
                                 d.Width == sceneW ? "*" : "");
            }
            r->Release();
        }
        srv->Release();
    }

    BVR_INFO("Compute dispatch bindings: %s. A star marks the scene width - a pass "
             "reading several starred SRVs and writing a starred UAV is the deferred "
             "lighting, and duplicating it is what lights the second eye's "
             "characters.", line);
}

/* Is this the scene depth buffer, by the shape learned while the colour targets
   were bound? The cascades have their own and must not drag ours with them. */
bool is_scene_depth(ID3D11DepthStencilView* dsv)
{
    if (dsv == nullptr)
        return false;

    const uint32_t w = g_sceneDepthW.load(std::memory_order_relaxed);
    if (w == 0)
        return false;

    ID3D11Resource* r = nullptr;
    dsv->GetResource(&r);
    if (r == nullptr)
        return false;

    bool match = false;
    ID3D11Texture2D* t = nullptr;
    if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D),
                                    reinterpret_cast<void**>(&t))) && t != nullptr)
    {
        D3D11_TEXTURE2D_DESC td = {};
        t->GetDesc(&td);
        t->Release();
        match = td.Width == w &&
                td.Height == g_sceneDepthH.load(std::memory_order_relaxed) &&
                static_cast<uint32_t>(td.Format) ==
                    g_sceneDepthF.load(std::memory_order_relaxed);
    }
    r->Release();
    return match;
}

constexpr int kClearVariants = 4;
void* g_clearDsvTramp[kClearVariants] = {};
void* g_clearDsvTarget[kClearVariants] = {};

template <int V>
void STDMETHODCALLTYPE tramp_clear_dsv(ID3D11DeviceContext* c, ID3D11DepthStencilView* dsv,
                                       UINT flags, FLOAT depth, UINT8 stencil)
{
    if (!g_inRedirect)
    {
        if (frame_map_capturing())
            frame_map_clear_dsv(c, dsv, flags, depth, stencil);

        stereo_clear_dsv(c, dsv, flags, depth, static_cast<UINT8>(stencil));

        g_engineDepthClear.store(depth, std::memory_order_relaxed);
        g_engineStencilClear.store(stencil, std::memory_order_relaxed);
        g_engineClearFlags.store(flags, std::memory_order_relaxed);

        /* OURS IS CLEARED WHEN THE ENGINE CLEARS ITS OWN, NOT AT PRESENT.
         *
           Clearing at Present was too late by a whole frame. The order was:
           prepass writes our depth, G-buffer tests it, Present, THEN clear - so
           during every frame our buffer still held the PREVIOUS frame's values
           underneath the current one. The prepass compares GREATER_EQUAL before
           it writes, so a stale value from last frame that was nearer than this
           frame's blocked the write outright, and the EQUAL test that followed
           then had nothing matching to find. The prepass ran, 141 draws a frame
           of it, and still produced a black eye.
         *
           Hooking the engine's own clear puts ours at exactly the right moment
           in the frame, with exactly the values the engine uses, without this
           having to know where a frame begins. */
        if (g_dupDsv != nullptr && dsv != nullptr && is_scene_depth(dsv))
        {
            g_inRedirect = true;
            reinterpret_cast<PFN_ClearDepthStencilView>(g_clearDsvTramp[V])(
                c, g_dupDsv, flags, depth, stencil);
            g_inRedirect = false;
            g_cDepthCleared.fetch_add(1, std::memory_order_relaxed);
        }
    }
    reinterpret_cast<PFN_ClearDepthStencilView>(g_clearDsvTramp[V])(c, dsv, flags,
                                                                    depth, stencil);
}

using PFN_OMSetRenderTargetsAndUAVs =
    void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, ID3D11RenderTargetView* const*,
                              ID3D11DepthStencilView*, UINT, UINT,
                              ID3D11UnorderedAccessView* const*, const UINT*);

void* g_rtUavTramp[kRtVariants] = {};
void* g_rtUavTarget[kRtVariants] = {};

template <int V>
void STDMETHODCALLTYPE tramp_om_set_rt_uav(ID3D11DeviceContext* c, UINT numRtv,
                                           ID3D11RenderTargetView* const* rtvs,
                                           ID3D11DepthStencilView* dsv,
                                           UINT uavStart, UINT numUav,
                                           ID3D11UnorderedAccessView* const* uavs,
                                           const UINT* counts)
{
    if (numRtv != D3D11_KEEP_RENDER_TARGETS_AND_DEPTH_STENCIL && !stereo_in_dup())
        note_render_targets(rtvs, numRtv, dsv);

    reinterpret_cast<PFN_OMSetRenderTargetsAndUAVs>(g_rtUavTramp[V])(
        c, numRtv, rtvs, dsv, uavStart, numUav, uavs, counts);
}

/* The four draw slots, and the four detours standing ready for each one's
   implementations. Read every frame; a slot whose current address we have not
   seen before gets inline-hooked once and then never again. */
struct DrawWatch
{
    int         index;
    int         kind;
    const char* name;
    void*       d[kDrawVariants];
};

const DrawWatch g_drawWatch[kDrawKinds] = {
    { 12, 0, "DrawIndexed",
      { reinterpret_cast<void*>(&tramp_draw_indexed<0, 0>),
        reinterpret_cast<void*>(&tramp_draw_indexed<0, 1>),
        reinterpret_cast<void*>(&tramp_draw_indexed<0, 2>),
        reinterpret_cast<void*>(&tramp_draw_indexed<0, 3>) } },
    { 20, 1, "DrawIndexedInstanced",
      { reinterpret_cast<void*>(&tramp_draw_indexed_instanced<1, 0>),
        reinterpret_cast<void*>(&tramp_draw_indexed_instanced<1, 1>),
        reinterpret_cast<void*>(&tramp_draw_indexed_instanced<1, 2>),
        reinterpret_cast<void*>(&tramp_draw_indexed_instanced<1, 3>) } },
    { 13, 2, "Draw",
      { reinterpret_cast<void*>(&tramp_draw<2, 0>),
        reinterpret_cast<void*>(&tramp_draw<2, 1>),
        reinterpret_cast<void*>(&tramp_draw<2, 2>),
        reinterpret_cast<void*>(&tramp_draw<2, 3>) } },
    { 21, 3, "DrawInstanced",
      { reinterpret_cast<void*>(&tramp_draw_instanced<3, 0>),
        reinterpret_cast<void*>(&tramp_draw_instanced<3, 1>),
        reinterpret_cast<void*>(&tramp_draw_instanced<3, 2>),
        reinterpret_cast<void*>(&tramp_draw_instanced<3, 3>) } },
};

/* Shared by both target-binding slots. Same contract as inline_hook_draw: an
   address already given to MinHook is never given to it again, so no amount of
   the runtime swapping implementations can allocate a second trampoline. */
void hook_rt_slot(void* target, void** targets, void** tramps,
                  void* const* detours, const char* name)
{
    if (target == nullptr)
        return;

    for (int v = 0; v < kRtVariants; ++v)
        if (targets[v] == target)
            return;

    for (int v = 0; v < kRtVariants; ++v)
    {
        if (targets[v] != nullptr)
            continue;

        void* tramp = nullptr;
        if (MH_CreateHook(target, detours[v], &tramp) != MH_OK ||
            MH_EnableHook(target) != MH_OK)
        {
            BVR_WARN("Inline hook on %s at %p refused by MinHook.", name, target);
            return;
        }

        targets[v] = target;
        tramps[v] = tramp;
        BVR_INFO("Inline-hooked %s implementation %d at %p.", name, v, target);
        return;
    }
}

/* Called at install and once per frame afterwards. Cheap: four pointer reads
   and, in the steady state, four compares that all say "already known". The
   runtime is left to own the slots - we never write to them - so there is
   nothing for it to reclaim and nothing to fight over. */
void scan_draw_slots(void** vtable)
{
    if (vtable == nullptr)
        return;

    for (int i = 0; i < kDrawKinds; ++i)
    {
        const DrawWatch& w = g_drawWatch[i];
        inline_hook_draw(w.kind, vtable[w.index], w.d, w.name);
    }

    /* Both target-binding entries, slots 33 and 34, by the same rules as the
       draws: the runtime owns them, so we detour the implementation rather than
       fight for the entry, once per distinct address, hard-capped.

       BOTH, because slot 33 was hooked three times over and fired zero times
       while draws on the same vtable counted 1,465 a frame. An engine using
       compute binds colour targets and UAVs in one call - slot 34 - and slot 33
       is then simply a door it never opens. Hooking only the one we expected is
       the same error that made DrawIndexed and UpdateSubresource read zero. */
    static void* const rtDetours[kRtVariants] = {
        reinterpret_cast<void*>(&tramp_om_set_rt<0>),
        reinterpret_cast<void*>(&tramp_om_set_rt<1>),
        reinterpret_cast<void*>(&tramp_om_set_rt<2>),
        reinterpret_cast<void*>(&tramp_om_set_rt<3>),
    };
    static void* const rtUavDetours[kRtVariants] = {
        reinterpret_cast<void*>(&tramp_om_set_rt_uav<0>),
        reinterpret_cast<void*>(&tramp_om_set_rt_uav<1>),
        reinterpret_cast<void*>(&tramp_om_set_rt_uav<2>),
        reinterpret_cast<void*>(&tramp_om_set_rt_uav<3>),
    };

    hook_rt_slot(vtable[33], g_rtTarget, g_rtTramp, rtDetours, "OMSetRenderTargets");
    hook_rt_slot(vtable[34], g_rtUavTarget, g_rtUavTramp, rtUavDetours,
                 "OMSetRenderTargetsAndUnorderedAccessViews");

    /* ClearDepthStencilView, slot 53, so the second eye can clear its own depth
       to whatever value the engine uses rather than to a guess. */
    static void* const clearDetours[kClearVariants] = {
        reinterpret_cast<void*>(&tramp_clear_dsv<0>),
        reinterpret_cast<void*>(&tramp_clear_dsv<1>),
        reinterpret_cast<void*>(&tramp_clear_dsv<2>),
        reinterpret_cast<void*>(&tramp_clear_dsv<3>),
    };
    hook_rt_slot(vtable[53], g_clearDsvTarget, g_clearDsvTramp, clearDetours,
                 "ClearDepthStencilView");

    /* Dispatch, slot 41. If the deferred lighting is compute, nothing else here
       can see it - which is what a black character on a correctly lit sky looks
       like. */
    static void* const dispatchDetours[kDispatchVariants] = {
        reinterpret_cast<void*>(&tramp_dispatch<0>),
        reinterpret_cast<void*>(&tramp_dispatch<1>),
        reinterpret_cast<void*>(&tramp_dispatch<2>),
        reinterpret_cast<void*>(&tramp_dispatch<3>),
    };
    hook_rt_slot(vtable[41], g_dispatchTarget, g_dispatchTramp, dispatchDetours,
                 "Dispatch");
}

void STDMETHODCALLTYPE hooked_draw_indexed(ID3D11DeviceContext* c, UINT a, UINT b, INT d)
{
    g_cDraw.fetch_add(1, std::memory_order_relaxed);
    g_origDrawIndexed(c, a, b, d);
}

void STDMETHODCALLTYPE hooked_draw_indexed_def(ID3D11DeviceContext* c, UINT a, UINT b, INT d)
{
    g_cDraw.fetch_add(1, std::memory_order_relaxed);
    g_origDrawIndexedDef(c, a, b, d);
}

/* THE DRAWS THIS ENGINE ACTUALLY MAKES.
 *
 * DrawIndexed - slot 12, the only draw entry hooked until now - counted ZERO
 * over whole battles while VSSetConstantBuffers on the SAME vtable counted ten
 * thousand. The hook was live and re-asserted every time the runtime reclaimed
 * the slot; it was simply watching a door rgl does not use. An engine that
 * instances its crowds and its foliage draws through DrawIndexedInstanced, and
 * nothing was looking there.
 *
 * That mattered only for a diagnostic before. It is now the measurement the
 * whole stereo question rests on: to draw the scene twice from one engine frame
 * we have to SEE every scene draw, and "how many are there" decides whether
 * doubling them is affordable at all. A census that reads zero answers neither.
 */
void STDMETHODCALLTYPE hooked_draw_indexed_instanced(
    ID3D11DeviceContext* c, UINT ipi, UINT ic, UINT sil, INT bvl, UINT sil2)
{
    g_cDrawInstanced.fetch_add(1, std::memory_order_relaxed);
    g_origDrawIndexedInstanced(c, ipi, ic, sil, bvl, sil2);
}

void STDMETHODCALLTYPE hooked_draw_indexed_instanced_def(
    ID3D11DeviceContext* c, UINT ipi, UINT ic, UINT sil, INT bvl, UINT sil2)
{
    g_cDrawInstanced.fetch_add(1, std::memory_order_relaxed);
    g_origDrawIndexedInstancedDef(c, ipi, ic, sil, bvl, sil2);
}

void STDMETHODCALLTYPE hooked_draw(ID3D11DeviceContext* c, UINT vc, UINT svl)
{
    g_cDrawPlain.fetch_add(1, std::memory_order_relaxed);
    g_origDraw(c, vc, svl);
}

void STDMETHODCALLTYPE hooked_draw_def(ID3D11DeviceContext* c, UINT vc, UINT svl)
{
    g_cDrawPlain.fetch_add(1, std::memory_order_relaxed);
    g_origDrawDef(c, vc, svl);
}

void STDMETHODCALLTYPE hooked_draw_instanced(
    ID3D11DeviceContext* c, UINT vpi, UINT ic, UINT svl, UINT sil)
{
    g_cDrawInstancedPlain.fetch_add(1, std::memory_order_relaxed);
    g_origDrawInstanced(c, vpi, ic, svl, sil);
}

void STDMETHODCALLTYPE hooked_draw_instanced_def(
    ID3D11DeviceContext* c, UINT vpi, UINT ic, UINT svl, UINT sil)
{
    g_cDrawInstancedPlain.fetch_add(1, std::memory_order_relaxed);
    g_origDrawInstancedDef(c, vpi, ic, svl, sil);
}

void STDMETHODCALLTYPE hooked_vs_set_cb(ID3D11DeviceContext* c, UINT start, UINT num,
                                        ID3D11Buffer* const* buffers)
{
    g_cVsSetCb.fetch_add(1, std::memory_order_relaxed);
    note_vs_constant_buffers(start, num, buffers);
    g_origVsSetCb(c, start, num, buffers);
}

void STDMETHODCALLTYPE hooked_vs_set_cb_def(ID3D11DeviceContext* c, UINT start, UINT num,
                                            ID3D11Buffer* const* buffers)
{
    g_cVsSetCb.fetch_add(1, std::memory_order_relaxed);
    note_vs_constant_buffers(start, num, buffers);
    g_origVsSetCbDef(c, start, num, buffers);
}

HRESULT STDMETHODCALLTYPE hooked_create_buffer(ID3D11Device* device,
                                               const D3D11_BUFFER_DESC* desc,
                                               const D3D11_SUBRESOURCE_DATA* initial,
                                               ID3D11Buffer** out)
{
    const HRESULT hr = g_origCreateBuffer(device, desc, initial, out);

    if (SUCCEEDED(hr) && desc != nullptr)
    {
        g_cCreateBuffer.fetch_add(1, std::memory_order_relaxed);
        if ((desc->BindFlags & D3D11_BIND_CONSTANT_BUFFER) != 0)
        {
            g_cCreateCb.fetch_add(1, std::memory_order_relaxed);
            buffer_kind_note(*desc);
        }
    }
    return hr;
}

/* THE decisive test, and it needs nothing from the engine.
 *
 * We create a dynamic constant buffer of our own and Map/Unmap it on the
 * engine's immediate context. If the counter does not move, our detour is not on
 * the code path that an ordinary Map call takes - the hook is installed
 * somewhere that nothing reaches, and no amount of studying rgl will help. If it
 * does move, interception works and rgl genuinely never calls Map, which is a
 * completely different problem with completely different next steps.
 *
 * Either way this collapses the guesswork to one line in the log. */
void run_hook_selftest(ID3D11DeviceContext* context)
{
    if (g_selfTestRun.exchange(true, std::memory_order_acq_rel))
        return;

    ID3D11Device* device = engine_device();
    if (device == nullptr || context == nullptr)
        return;

    D3D11_BUFFER_DESC desc = {};
    desc.ByteWidth = 256;
    desc.Usage = D3D11_USAGE_DYNAMIC;
    desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;

    ID3D11Buffer* probe = nullptr;
    if (FAILED(device->CreateBuffer(&desc, nullptr, &probe)) || probe == nullptr)
    {
        BVR_WARN("Hook self-test: could not create the probe buffer.");
        return;
    }

    const uint64_t before = g_cMap.load(std::memory_order_relaxed);

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    if (SUCCEEDED(context->Map(probe, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)) &&
        mapped.pData != nullptr)
    {
        memset(mapped.pData, 0, 256);
        context->Unmap(probe, 0);
    }

    const uint64_t after = g_cMap.load(std::memory_order_relaxed);
    probe->Release();

    if (after > before)
    {
        g_selfTestPassed.store(true, std::memory_order_relaxed);
        BVR_INFO("Hook self-test PASSED: our own Map call was intercepted. "
                 "Interception works; if the engine's Map count stays at zero it "
                 "is because rgl does not call Map, not because we cannot see it. "
                 "(The 256-byte constant buffer in the census below is this probe.)");
    }
    else
    {
        BVR_ERR("Hook self-test FAILED: our own Map call was NOT intercepted, even "
                "though MinHook reported the hook installed. The detour is not on "
                "the path an ordinary Map takes - something else has re-patched "
                "d3d11, or the vtable the engine uses is not the one we read.");
    }
}

// ---------------------------------------------------------------------------
// third-round instrumentation: is the interception DECAYING?
//
// The self-test passed - our own Map call was intercepted - and DrawIndexed
// counted 10041. So MinHook works and context calls do reach us. But in the same
// run:
//
//   * our own Unmap call, made two lines after the Map that WAS intercepted,
//     did not register at all (Unmap 0);
//   * VSSetConstantBuffers counted 0 across 8000 frames;
//   * DrawIndexed counted ~1.25 per frame, which is a UI overlay, not a battle.
//
// Those cannot all be true of a healthy set of hooks on a static vtable. The
// hypothesis this round tests is that the vtable is NOT static: the D3D11
// runtime swaps its context method implementations as device state changes
// (amortized vs non-amortized, single-threaded vs multithread-protected), and a
// hook byte-patched into the function that WAS in a slot stops being called the
// moment the runtime points that slot somewhere else.
//
// That would explain every number above, including why some slots work and
// others do not: the ones that still work are the ones that were never
// re-pointed.
//
// So: remember the address behind every slot we patched, re-check them all every
// Present, say so loudly when one moves, and re-hook it. If the hypothesis is
// right this both proves it and fixes it.
// ---------------------------------------------------------------------------

/* The runtime does not set these slots once and settle. Two of them
 * (DrawIndexed and UpdateSubresource) alternate between the SAME PAIR of
 * implementations on essentially every frame, as D3D11 swaps between its
 * amortized and non-amortized paths.
 *
 * The MinHook version chased that with MH_CreateHook per flip - 693 "re-hooks"
 * in twenty seconds - and since MinHook never releases a trampoline, that was
 * an unbounded drain on its allocation pool. An empty pool is
 * MH_ERROR_MEMORY_ALLOC, status 9, which is what eventually failed every hook
 * in the process including the eye textures. Restoring a vtable pointer costs
 * one store and no allocation, so it can be repeated forever. */

struct WatchedSlot
{
    int         index;
    void*       detour;
    void**      original;
    const char* name;
    void*       patched;    /* the address currently in the slot */
    int         rehooks;
    bool        exhausted;
};

constexpr int kWatchedSlots = 8;
WatchedSlot g_watched[kWatchedSlots] = {};
int         g_watchedCount = 0;

ID3D11DeviceContext* g_watchedContext = nullptr;
void**               g_watchedVtable = nullptr;
ID3D11Buffer*        g_probeBuffer = nullptr;

std::atomic<uint64_t> g_cVsSetCb1{0};
std::atomic<uint64_t> g_selfTestPasses{0};
std::atomic<uint64_t> g_selfTestFails{0};

void verify_hooks();
void selftest_tick();

constexpr int kIdxVSSetConstantBuffers = 7;
constexpr int kIdxDrawIndexed = 12;
constexpr int kIdxDraw = 13;
constexpr int kIdxDrawIndexedInstanced = 20;
constexpr int kIdxDrawInstanced = 21;
constexpr int kIdxMap = 14;
constexpr int kIdxUnmap = 15;
constexpr int kIdxUpdateSubresource = 48;
constexpr int kIdxUpdateSubresource1 = 116;

void watch_slot(void** vtable, int index, void* detour, void** original, const char* name)
{
    if (g_watchedCount >= kWatchedSlots)
        return;

    WatchedSlot& w = g_watched[g_watchedCount++];
    w.index = index;
    w.detour = detour;
    w.original = original;
    w.name = name;
    w.patched = vtable[index];
    w.rehooks = 0;
    w.exhausted = false;
}

void hook_context(ID3D11DeviceContext* context, const char* which, bool deferred)
{
    void** vtable = *reinterpret_cast<void***>(context);

    /* Only the immediate context is watched: it is the one that stays alive, and
       the deferred probe is released the moment it has served its purpose. */
    if (!deferred)
    {
        g_watchedContext = context;
        g_watchedVtable = vtable;
        g_watchedCount = 0;
        watch_slot(vtable, kIdxMap, reinterpret_cast<void*>(&hooked_map),
                   reinterpret_cast<void**>(&g_origMap), "Map");
        watch_slot(vtable, kIdxUnmap, reinterpret_cast<void*>(&hooked_unmap),
                   reinterpret_cast<void**>(&g_origUnmap), "Unmap");
        watch_slot(vtable, kIdxUpdateSubresource, reinterpret_cast<void*>(&hooked_update),
                   reinterpret_cast<void**>(&g_origUpdate), "UpdateSubresource");
        /* THE DRAW SLOTS ARE DELIBERATELY NOT WATCHED ANY MORE.
         *
           Watching them meant writing our detour back every time the runtime
           took the slot, which it did 23 times in one session - and it still
           counted zero draws, because it owns those entries and holds them at
           the moment a draw actually goes out. The Unmap probe confirmed it:
           our detours were absent from slots 12 and 20 on the very context
           uploading sixty thousand constants a second.
         *
           So we stop contesting them. scan_draw_slots inline-hooks whatever
           address the runtime has put there, once per distinct address, and
           then leaves the slot alone forever. Nothing to reclaim, nothing to
           put back, and no allocation after the first sighting of each
           implementation. */
        watch_slot(vtable, kIdxVSSetConstantBuffers, reinterpret_cast<void*>(&hooked_vs_set_cb),
                   reinterpret_cast<void**>(&g_origVsSetCb), "VSSetConstantBuffers");
    }

    hook_slot(vtable, kIdxMap,
              reinterpret_cast<void*>(deferred ? &hooked_map_def : &hooked_map),
              reinterpret_cast<void**>(deferred ? &g_origMapDef : &g_origMap),
              "Map", which);

    hook_slot(vtable, kIdxUnmap,
              reinterpret_cast<void*>(deferred ? &hooked_unmap_def : &hooked_unmap),
              reinterpret_cast<void**>(deferred ? &g_origUnmapDef : &g_origUnmap),
              "Unmap", which);

    hook_slot(vtable, kIdxUpdateSubresource,
              reinterpret_cast<void*>(deferred ? &hooked_update_def : &hooked_update),
              reinterpret_cast<void**>(deferred ? &g_origUpdateDef : &g_origUpdate),
              "UpdateSubresource", which);

    /* THE DRAWS ARE INLINE-HOOKED, NOT VTABLE-HOOKED.

       Every vtable attempt on these four counted zero while the runtime took
       the slots back 23 times a session. scan_draw_slots detours the
       IMPLEMENTATIONS instead - once per distinct address, capped - and leaves
       the entries to the runtime. See the note above it. */
    scan_draw_slots(vtable);

    hook_slot(vtable, kIdxVSSetConstantBuffers,
              reinterpret_cast<void*>(deferred ? &hooked_vs_set_cb_def : &hooked_vs_set_cb),
              reinterpret_cast<void**>(deferred ? &g_origVsSetCbDef : &g_origVsSetCb),
              "VSSetConstantBuffers", which);

    /* Only if the runtime really is 11.1 - reading vtable[116] on a plain
       ID3D11DeviceContext would be off the end of the table. */
    ID3D11DeviceContext1* context1 = nullptr;
    if (SUCCEEDED(context->QueryInterface(__uuidof(ID3D11DeviceContext1),
                                          reinterpret_cast<void**>(&context1))) &&
        context1 != nullptr)
    {
        void** vtable1 = *reinterpret_cast<void***>(context1);
        hook_slot(vtable1, kIdxUpdateSubresource1,
                  reinterpret_cast<void*>(deferred ? &hooked_update1_def : &hooked_update1),
                  reinterpret_cast<void**>(deferred ? &g_origUpdate1Def : &g_origUpdate1),
                  "UpdateSubresource1", which);
        context1->Release();
    }
}

/* Runs every Present. A handful of pointer compares, and the thing that keeps
   the hooks alive: the runtime reclaims these slots as it swaps between its
   amortized and non-amortized implementations, and this puts ours back. */
void verify_hooks()
{
    if (g_watchedContext == nullptr || g_watchedVtable == nullptr)
        return;

    void** current = *reinterpret_cast<void***>(g_watchedContext);
    if (current != g_watchedVtable)
    {
        BVR_WARN("The immediate context's VTABLE POINTER changed (%p -> %p). The "
                 "runtime swapped the whole method table; every hook was on the "
                 "old one. Re-hooking against the new table.",
                 static_cast<void*>(g_watchedVtable), static_cast<void*>(current));
        g_watchedVtable = current;
        for (int i = 0; i < g_watchedCount; ++i)
            g_watched[i].patched = nullptr;   /* force the per-slot path below */
    }

    /* The draw slots, every frame. The runtime swaps between its amortized and
       non-amortized implementations constantly, and the SECOND of each pair can
       only be discovered by looking while it is installed. This is four pointer
       reads and, once both have been seen, four compares that all say "known" -
       inline_hook_draw refuses an address it has hooked before, which is the
       guard that keeps this from becoming the 693-re-hook failure again. */
    scan_draw_slots(g_watchedVtable);

    /* With a vtable hook the question is no longer "did the address move" but
       "is our pointer still in the slot". The runtime overwrites these entries
       as it swaps implementations; putting ours back is a single pointer store,
       so it is safe to do every frame forever - which is exactly what the
       MinHook version could not do. */
    for (int i = 0; i < g_watchedCount; ++i)
    {
        WatchedSlot& w = g_watched[i];
        void** slot = &g_watchedVtable[w.index];

        if (*slot == w.detour)
            continue;

        w.patched = *slot;
        if (!vtable_hook(g_watchedVtable, w.index, w.detour, w.original))
        {
            if (!w.exhausted)
            {
                w.exhausted = true;
                BVR_ERR("Slot %d (%s): the vtable page refused a write; leaving it "
                        "alone.", w.index, w.name);
            }
            continue;
        }

        /* Only the first few, then silence. This fires whenever the runtime
           reclaims the slot, which for two of them is most frames. */
        if (++w.rehooks <= 4)
        {
            BVR_INFO("Slot %d (%s) was reclaimed by the runtime (now forwarding to "
                     "%p); ours put back.", w.index, w.name, w.patched);
        }
    }
}

/* The self-test, repeated. Passing once at startup and failing later is exactly
   what a decaying hook looks like, and a single test at frame zero cannot tell
   the difference between that and a hook that works forever. */
void selftest_tick()
{
    if (g_watchedContext == nullptr)
        return;

    static int tick = 0;
    if (++tick < 300)          /* a little over three seconds at 90 fps */
        return;
    tick = 0;

    if (g_probeBuffer == nullptr)
    {
        ID3D11Device* device = engine_device();
        if (device == nullptr)
            return;

        D3D11_BUFFER_DESC desc = {};
        desc.ByteWidth = 256;
        desc.Usage = D3D11_USAGE_DYNAMIC;
        desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        if (FAILED(device->CreateBuffer(&desc, nullptr, &g_probeBuffer)))
        {
            g_probeBuffer = nullptr;
            return;
        }
    }

    const uint64_t mapBefore = g_cMap.load(std::memory_order_relaxed);
    const uint64_t unmapBefore = g_cUnmap.load(std::memory_order_relaxed);

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    if (SUCCEEDED(g_watchedContext->Map(g_probeBuffer, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)) &&
        mapped.pData != nullptr)
    {
        memset(mapped.pData, 0, 256);
        g_watchedContext->Unmap(g_probeBuffer, 0);
    }

    const bool mapSeen = g_cMap.load(std::memory_order_relaxed) > mapBefore;
    const bool unmapSeen = g_cUnmap.load(std::memory_order_relaxed) > unmapBefore;

    if (mapSeen && unmapSeen)
        g_selfTestPasses.fetch_add(1, std::memory_order_relaxed);
    else
        g_selfTestFails.fetch_add(1, std::memory_order_relaxed);
}

} // namespace

// ---------------------------------------------------------------------------
// public surface
// ---------------------------------------------------------------------------

bool vp_enabled() { return cfg().enabled; }

/* THE HOOKS EXIST FOR EITHER JOB, AND THIS HAS TO BE VISIBLE OUTSIDE THIS FILE.
 *
   Substitution needs the constant-buffer hooks; so does measurement, because the
   engine's true frustum can only be read off the matrix in transit. Splitting
   Settings::measure out of vp_patch was not enough on its own: bvr_api gates BOTH
   the hook installation AND bvr_set_vp_frame on vp_enabled(), so with vp_patch = 0
   the installer was never called and the publication was refused with
   SESSION_NOT_READY before it reached the ring. The scan therefore never ran, no
   frustum was ever measured, and submitted_fov went on using the computed one -
   which is the 0.650 gain, unchanged, twice.
 *
   vp_enabled() still means "rewrite matrices" and nothing else. This is the one
   that means "the scanner should be running at all". */
bool vp_active() { return cfg().enabled || cfg().measure; }

bool vp_install_hooks(ID3D11DeviceContext* context)
{
    if (!vp_active())
        return false;
    if (g_hooked.load(std::memory_order_acquire))
        return true;
    if (context == nullptr)
        return false;

    /* Wait for install_present_hook to RETURN, not merely for its detour to go
       live. Those are several statements apart, and in between them the render
       thread is already running our tick while the main thread is still inside
       MinHook. One run hit that window - its "Present hooked" line printed after
       these hooks - and every MinHook call in the process failed with
       MH_ERROR_MEMORY_ALLOC from then on, including the eye-texture capture,
       which left the headset with no image at all. A frame's wait costs
       nothing. */
    if (!present_hook_ready())
        return false;

    g_maxCbBytes = static_cast<uint32_t>(config_float("vp_max_cb_bytes", 65536.0f));

    hook_context(context, "ID3D11DeviceContext(immediate)", false);

    /* The deferred vtable as well, via a throwaway context of our own - the same
       trick the swapchain vtable probe already uses, and for the same reason:
       the vtable belongs to the class, not the instance, so the hook survives
       the probe being released and covers every deferred context rgl creates.
     *
     * This is here because the first run hooked the immediate context, logged
     * three successful hooks, and then saw ZERO constant buffer uploads. A
     * multithreaded renderer recording its draws on deferred contexts would
     * produce exactly that: the hooks are real, they are just on the vtable
     * nothing calls. */
    ID3D11Device* device = engine_device();
    if (device != nullptr)
    {
        ID3D11DeviceContext* deferred = nullptr;
        const HRESULT hr = device->CreateDeferredContext(0, &deferred);
        if (SUCCEEDED(hr) && deferred != nullptr)
        {
            hook_context(deferred, "ID3D11DeviceContext(deferred)", true);
            deferred->Release();
        }
        else
        {
            BVR_WARN("CreateDeferredContext failed (hr 0x%08lX); only the immediate "
                     "context is hooked.", static_cast<unsigned long>(hr));
        }

        /* ID3D11Device vtable: 0-2 IUnknown, 3 CreateBuffer. The same vtable the
           CreateTexture2D hook has been using successfully since Phase 4, so if
           THIS one also never fires, the problem is not specific to contexts. */
        void** deviceVtable = *reinterpret_cast<void***>(device);
        hook_slot(deviceVtable, 3, reinterpret_cast<void*>(&hooked_create_buffer),
                  reinterpret_cast<void**>(&g_origCreateBuffer),
                  "CreateBuffer", "ID3D11Device");
    }

    g_hooked.store(true, std::memory_order_release);

    /* After every hook is live, and on the engine's own context. */
    run_hook_selftest(context);

    /* The frame map rides on the draw, dispatch and upload hooks above, so it is
       installed only once they exist. Inert unless frame_map = 1. */
    if (device != nullptr)
        frame_map_install(device, context);
    return true;
}

/* ---------------------------------------------------------------------------
 * PUTTING THE SECOND EYE ON SCREEN.
 *
 * The duplicate target holds the scene from the other eye's viewpoint, at the
 * upscaler's internal size (2267x2312) and with none of the frame's later work
 * on it - no tonemap, no DLSS, no post. Getting it into an eye image therefore
 * needs a scale, and a scale needs a shader; CopyResource cannot resize and
 * CopySubresourceRegion cannot convert format.
 *
 * So: a fullscreen triangle, generated from SV_VertexID with no vertex or index
 * buffer at all, sampling the duplicate with a linear sampler. Three vertices,
 * one draw.
 *
 * WHAT IT WILL LOOK LIKE, AND WHY THAT IS STILL THE RIGHT TEST
 *
 * Bad. Raw scene colour with no tonemapping, bilinear-scaled up from two thirds
 * resolution, missing every post pass. It is not a shippable image and is not
 * meant to be one.
 *
 * It answers the only question left that counters cannot: is the duplicated
 * geometry actually CORRECT, and actually offset by the IPD? A bind count says
 * the other eye's matrix was bound; it cannot say the resulting image is right.
 * If this comes out as the battlefield from slightly to the side, the mechanism
 * is proven and everything after it is engineering. If it comes out sheared,
 * doubled, inside-out or empty, the picture says which - and no amount of
 * further instrumentation would have.
 * ------------------------------------------------------------------------- */
const char kBlitShader[] =
    "Texture2D    gSrc : register(t0);\n"
    "SamplerState gSmp : register(s0);\n"
    "struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };\n"
    "VSOut VSMain(uint id : SV_VertexID)\n"
    "{\n"
    "    VSOut o;\n"
    "    float2 uv = float2((id << 1) & 2, id & 2);\n"
    "    o.uv = uv;\n"
    "    o.pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);\n"
    "    return o;\n"
    "}\n"
    "cbuffer BlitCb : register(b0) { float2 gUvScale; float2 gPad; };\n"
    "float4 PSMain(VSOut i) : SV_Target\n"
    "{\n"
    "    return float4(gSrc.SampleLevel(gSmp, i.uv * gUvScale, 0).rgb, 1.0);\n"
    "}\n";

ID3D11VertexShader*   g_blitVs = nullptr;
ID3D11PixelShader*    g_blitPs = nullptr;
ID3D11SamplerState*   g_blitSampler = nullptr;
ID3D11DepthStencilState* g_blitNoDepth = nullptr;
ID3D11RasterizerState*   g_blitRaster = nullptr;
ID3D11BlendState*        g_blitBlend = nullptr;
ID3D11Texture2D*         g_probeTex = nullptr;
ID3D11Texture2D*         g_dupSrvFor = nullptr;
ID3D11Buffer*            g_blitCb = nullptr;
ID3D11ShaderResourceView* g_dupSrv = nullptr;
ID3D11RenderTargetView*   g_blitDstRtv = nullptr;
ID3D11Texture2D*          g_blitDstFor = nullptr;
bool g_blitFailed = false;

bool ensure_blit_pipeline()
{
    if (g_blitFailed)
        return false;
    if (g_blitVs != nullptr && g_blitPs != nullptr)
        return true;

    ID3D11Device* dev = engine_device();
    if (dev == nullptr)
        return false;

    ID3DBlob* vsCode = nullptr;
    ID3DBlob* psCode = nullptr;
    ID3DBlob* err = nullptr;

    const UINT flags = D3DCOMPILE_OPTIMIZATION_LEVEL3;

    if (FAILED(D3DCompile(kBlitShader, sizeof(kBlitShader) - 1, "bvr_blit", nullptr,
                          nullptr, "VSMain", "vs_5_0", flags, 0, &vsCode, &err)) ||
        FAILED(D3DCompile(kBlitShader, sizeof(kBlitShader) - 1, "bvr_blit", nullptr,
                          nullptr, "PSMain", "ps_5_0", flags, 0, &psCode, &err)))
    {
        g_blitFailed = true;
        BVR_ERR("Second eye: the blit shader would not compile%s%s.",
                err != nullptr ? ": " : "",
                err != nullptr ? static_cast<const char*>(err->GetBufferPointer()) : "");
        if (vsCode) vsCode->Release();
        if (psCode) psCode->Release();
        if (err) err->Release();
        return false;
    }

    const bool ok =
        SUCCEEDED(dev->CreateVertexShader(vsCode->GetBufferPointer(),
                                          vsCode->GetBufferSize(), nullptr, &g_blitVs)) &&
        SUCCEEDED(dev->CreatePixelShader(psCode->GetBufferPointer(),
                                         psCode->GetBufferSize(), nullptr, &g_blitPs));

    vsCode->Release();
    psCode->Release();
    if (err) err->Release();

    D3D11_SAMPLER_DESC sd = {};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    dev->CreateSamplerState(&sd, &g_blitSampler);

    D3D11_DEPTH_STENCIL_DESC dsd = {};
    dsd.DepthEnable = FALSE;
    dsd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
    dev->CreateDepthStencilState(&dsd, &g_blitNoDepth);

    if (!ok || g_blitSampler == nullptr)
    {
        g_blitFailed = true;
        BVR_ERR("Second eye: the blit pipeline could not be created.");
        return false;
    }

    BVR_INFO("Second eye: blit pipeline ready. The duplicate will be scaled into the "
             "eye image - raw scene colour, no tonemap and no post, which is expected "
             "and is not what is being tested.");
    return true;
}

bool vp_second_eye_ready()
{
    return redirect_enabled() && g_dupTex != nullptr && !g_blitFailed;
}

/* A silent false is what made the first attempt read as "nothing happened": the
   blit refused, afr_capture fell back to the engine's own eye, and the headset
   looked exactly as it does with the feature switched off. Every way out of the
   blit now says which one it took, once. */
void blit_failed_once(const char* what)
{
    static bool said = false;
    if (said)
        return;
    said = true;
    BVR_ERR("Second eye: the blit could not run - %s. The right eye falls back to the "
            "engine's own render, which is indistinguishable from this being off.",
            what);
}

bool vp_blit_second_eye(ID3D11DeviceContext* ctx, ID3D11Texture2D* dst)
{
    if (ctx == nullptr || dst == nullptr || g_dupTex == nullptr)
        return false;
    if (!ensure_blit_pipeline())
        return false;

    ID3D11Device* dev = engine_device();
    if (dev == nullptr)
        return false;

    /* THE VIEW HAS TO FOLLOW THE TEXTURE, AND THAT IS WHAT WAS WRONG.
     *
       g_dupTex is not fixed. ensure_dup_target makes a standalone one when the
       census first names the scene shape, and ensure_dup_set then repoints
       g_dupTex at the scene slot of the full G-buffer duplicate a moment later.
       The shader resource view was built once, from whichever texture happened
       to be there first - the standalone one, which nothing ever draws into.
     *
       So the probe read the real duplicate and found a picture, while the blit
       sampled a different texture that was empty. Both were true at once, which
       is why every earlier theory fitted the evidence and none of them fixed it.
     *
       Rebuilt whenever the texture underneath it changes. */
    if (g_dupSrv != nullptr && g_dupSrvFor != g_dupTex)
    {
        g_dupSrv->Release();
        g_dupSrv = nullptr;
    }

    if (g_dupSrv == nullptr)
    {
        if (FAILED(dev->CreateShaderResourceView(g_dupTex, nullptr, &g_dupSrv)))
        {
            blit_failed_once("CreateShaderResourceView on the duplicate target");
            return false;
        }

        g_dupSrvFor = g_dupTex;
        BVR_INFO("Second eye: sampling view rebuilt for duplicate texture %p.",
                 static_cast<void*>(g_dupTex));
    }

    D3D11_TEXTURE2D_DESC dd = {};
    dst->GetDesc(&dd);

    if (g_blitDstRtv == nullptr || g_blitDstFor != dst)
    {
        if (g_blitDstRtv != nullptr) { g_blitDstRtv->Release(); g_blitDstRtv = nullptr; }

        /* AN EXPLICIT FORMAT, BECAUSE THE DESTINATION IS TYPELESS.
         *
           The eye staging textures are created in the TYPELESS member of their
           family so AFW's compute shader can take an unordered-access view of
           them - a typed UAV is not allowed on B8G8R8A8_UNORM. A null view
           description cannot infer a format from a typeless resource, so the
           first attempt failed here even after the render-target bind flag was
           added, and reported the bind flag because that was the likelier of
           the two.

           The typed format is the eye texture's own: the staging copies are
           built from its description with nothing but the format swapped. */
        D3D11_RENDER_TARGET_VIEW_DESC rtvd = {};
        rtvd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        rtvd.Format = dd.Format;

        if (ID3D11Texture2D* eye0 = eye_texture(0))
        {
            D3D11_TEXTURE2D_DESC ed = {};
            eye0->GetDesc(&ed);
            rtvd.Format = ed.Format;
        }

        if (FAILED(dev->CreateRenderTargetView(dst, &rtvd, &g_blitDstRtv)))
        {
            blit_failed_once("CreateRenderTargetView on the destination eye image "
                             "even with an explicit typed format");
            return false;
        }

        BVR_INFO("Second eye: render target view on the eye image created as format "
                 "%d over a %d texture.", static_cast<int>(rtvd.Format),
                 static_cast<int>(dd.Format));
        g_blitDstFor = dst;
    }

    /* State is saved and restored around this. It runs inside the Present hook,
       where the engine has finished its frame but its context state is still
       live, and a renderer that finds its shaders swapped out from under it
       fails in ways that look nothing like their cause. */
    ID3D11RenderTargetView* oldRtv[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
    ID3D11DepthStencilView* oldDsv = nullptr;
    ctx->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, oldRtv, &oldDsv);

    UINT numVp = 1;
    D3D11_VIEWPORT oldVp = {};
    ctx->RSGetViewports(&numVp, &oldVp);

    /* THE BLIT WAS DRAWING WITH WHATEVER STATE THE ENGINE LEFT BEHIND.
     *
       With the depth test forced to ALWAYS the second eye was still black, which
       clears the duplicated draws of suspicion and puts it on this - the one
       step whose output actually reaches the headset. It set shaders, a sampler,
       a topology and a depth state, and left the rasteriser and the blend state
       to chance.
     *
       Either can blank a fullscreen triangle completely. A SCISSOR RECT left
       enabled by the engine clips our draw to whatever region it last cared
       about - frequently a small one, sometimes an empty one. A blend state
       left set to something that multiplies by source alpha discards the whole
       output, because the shader writes alpha 1 into a target the blend reads
       as zero. Neither produces an error; both produce black.
     *
       So the blit now states everything it depends on rather than inheriting
       it: no culling, no scissor, opaque blend, all four channels written. */
    if (g_blitRaster == nullptr)
    {
        D3D11_RASTERIZER_DESC rd = {};
        rd.FillMode = D3D11_FILL_SOLID;
        rd.CullMode = D3D11_CULL_NONE;
        rd.DepthClipEnable = TRUE;
        rd.ScissorEnable = FALSE;
        dev->CreateRasterizerState(&rd, &g_blitRaster);
    }

    if (g_blitBlend == nullptr)
    {
        D3D11_BLEND_DESC bd = {};
        bd.RenderTarget[0].BlendEnable = FALSE;
        bd.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
        dev->CreateBlendState(&bd, &g_blitBlend);
    }

    ID3D11RasterizerState* oldRaster = nullptr;
    ctx->RSGetState(&oldRaster);

    ID3D11BlendState* oldBlend = nullptr;
    FLOAT oldBlendFactor[4] = {};
    UINT oldSampleMask = 0;
    ctx->OMGetBlendState(&oldBlend, oldBlendFactor, &oldSampleMask);

    ID3D11DepthStencilState* oldDss = nullptr;
    UINT oldRef = 0;
    ctx->OMGetDepthStencilState(&oldDss, &oldRef);

    D3D11_VIEWPORT vp = {};
    vp.Width = static_cast<float>(dd.Width);
    vp.Height = static_cast<float>(dd.Height);
    vp.MaxDepth = 1.0f;

    ID3D11RenderTargetView* nullDepthRtv = g_blitDstRtv;
    ctx->OMSetRenderTargets(1, &nullDepthRtv, nullptr);
    ctx->RSSetViewports(1, &vp);
    ctx->IASetInputLayout(nullptr);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->VSSetShader(g_blitVs, nullptr, 0);
    ctx->PSSetShader(g_blitPs, nullptr, 0);
    ctx->PSSetShaderResources(0, 1, &g_dupSrv);
    /* Only the part of the duplicate the engine actually rendered into. */
    {
        if (g_blitCb == nullptr)
        {
            D3D11_BUFFER_DESC cbd = {};
            cbd.ByteWidth = 16;
            cbd.Usage = D3D11_USAGE_DEFAULT;
            cbd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
            dev->CreateBuffer(&cbd, nullptr, &g_blitCb);
        }

        if (g_blitCb != nullptr)
        {
            const float vw = g_sceneVpW.load(std::memory_order_relaxed);
            const float vh = g_sceneVpH.load(std::memory_order_relaxed);
            float uv[4] = { 1.0f, 1.0f, 0.0f, 0.0f };
            if (vw > 0.0f && g_dupWidth > 0)
                uv[0] = vw / static_cast<float>(g_dupWidth);
            if (vh > 0.0f && g_dupHeight > 0)
                uv[1] = vh / static_cast<float>(g_dupHeight);
            if (uv[0] <= 0.0f || uv[0] > 1.0f) uv[0] = 1.0f;
            if (uv[1] <= 0.0f || uv[1] > 1.0f) uv[1] = 1.0f;

            ctx->UpdateSubresource(g_blitCb, 0, nullptr, uv, 0, 0);
            ctx->PSSetConstantBuffers(0, 1, &g_blitCb);
        }
    }

    ctx->PSSetSamplers(0, 1, &g_blitSampler);
    ctx->OMSetDepthStencilState(g_blitNoDepth, 0);
    ctx->RSSetState(g_blitRaster);

    const FLOAT one[4] = { 1.0f, 1.0f, 1.0f, 1.0f };
    ctx->OMSetBlendState(g_blitBlend, one, 0xFFFFFFFFu);

    ctx->Draw(3, 0);

    /* Unbind the source before it can be bound as a target again. */
    ID3D11ShaderResourceView* nullSrv = nullptr;
    ctx->PSSetShaderResources(0, 1, &nullSrv);

    /* IS THE DUPLICATE ITSELF EMPTY, OR IS THE BLIT LOSING IT?
     *
       Both produce a black eye and nothing so far has told them apart. This
       reads one pixel back from the duplicate - a staging copy of a single
       texel from the middle of it, which is where the world is if there is a
       world - and reports what is actually in the texture.
     *
       A stall, so once every five seconds and never otherwise. */
    {
        static uint64_t lastProbe = 0;
        const uint64_t nowMs = GetTickCount64();
        if (nowMs - lastProbe >= 5000)
        {
            lastProbe = nowMs;

            if (g_probeTex == nullptr)
            {
                D3D11_TEXTURE2D_DESC pd = {};
                pd.Width = 1;
                pd.Height = 1;
                pd.MipLevels = 1;
                pd.ArraySize = 1;
                pd.Format = static_cast<DXGI_FORMAT>(g_dupFormat);
                pd.SampleDesc.Count = 1;
                pd.Usage = D3D11_USAGE_STAGING;
                pd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                dev->CreateTexture2D(&pd, nullptr, &g_probeTex);
            }

            if (g_probeTex != nullptr)
            {
                D3D11_BOX box = {};
                box.left = g_dupWidth / 2;
                box.right = box.left + 1;
                box.top = g_dupHeight / 2;
                box.bottom = box.top + 1;
                box.back = 1;

                ctx->CopySubresourceRegion(g_probeTex, 0, 0, 0, 0, g_dupTex, 0, &box);

                D3D11_MAPPED_SUBRESOURCE m = {};
                if (SUCCEEDED(ctx->Map(g_probeTex, 0, D3D11_MAP_READ, 0, &m)) &&
                    m.pData != nullptr)
                {
                    const uint32_t px = *static_cast<const uint32_t*>(m.pData);
                    ctx->Unmap(g_probeTex, 0);

                    {
                char line[256];
                int n = 0;
                for (int i = 0; i < kDupSlots; ++i)
                {
                    if (g_drawsPerSceneSlot[i] == 0)
                        continue;
                    n += _snprintf_s(line + n, sizeof(line) - n, _TRUNCATE,
                                     "  slot %d: %llu", i,
                                     static_cast<unsigned long long>(
                                         g_drawsPerSceneSlot[i]));
                    g_drawsPerSceneSlot[i] = 0;
                }
                BVR_INFO("Second eye draws by scene slot (the blit reads slot %d):%s. "
                         "More than one busy slot means the scene colour moves "
                         "between passes and displaying a single one loses the rest.",
                         g_sceneRtSlot.load(std::memory_order_relaxed),
                         n > 0 ? line : "  none");
            }

            BVR_INFO("Second eye probe: centre pixel of the duplicate reads "
                             "0x%08X. Zero means the duplicated draws are writing "
                             "nothing and the blit is innocent; anything else means the "
                             "duplicate has a picture in it and the blit is losing it.",
                             px);
                }
            }
        }
    }

    /* CLEARED FOR THE NEXT FRAME, AND THAT IS WHAT THE TRAILS WERE.
     *
       The engine clears its own scene buffer at the top of every frame. Our
       duplicate receives only the REDIRECTED DRAWS - never that clear - so
       whatever a frame did not overwrite stayed on it, frame after frame.
       Moving things left their previous positions behind, which is exactly the
       "when anything moves it creates trails" that came back from the headset.
       Static geometry hid it because it repaints the same pixels every frame.
     *
       Done here, immediately after the blit has consumed it, so each frame's
       draws start on a clean surface. Black rather than anything else: the sky
       is drawn by the scene pass like everything else, so nothing that should be
       visible depends on the clear colour. */
    /* Every duplicate, not only the scene colour: each one accumulates the same
       way, and a stale normal or motion vector feeds the next frame as surely as
       a stale pixel does. */
    /* The whole pool, once each. Clearing through the per-slot pointers would
       clear a shared surface several times and miss any not currently bound. */
    const float clear[4] = { 0.0f, 0.0f, 0.0f, 1.0f };
    for (int i2 = 0; i2 < kDupSlots; ++i2)
        for (int k = 0; k < kShapesPerSlot; ++k)
            if (g_slotCacheRtv[i2][k] != nullptr)
                ctx->ClearRenderTargetView(g_slotCacheRtv[i2][k], clear);

    /* The DEPTH is no longer cleared here. Present is a frame too late: the
       prepass and the G-buffer pass both run before it, so our buffer still
       held the previous frame underneath the current one and the GREATER_EQUAL
       prepass refused to overwrite anything nearer. It is cleared now where the
       engine clears its own - see tramp_clear_dsv. */

    ctx->RSSetState(oldRaster);
    if (oldRaster != nullptr) oldRaster->Release();

    ctx->OMSetBlendState(oldBlend, oldBlendFactor, oldSampleMask);
    if (oldBlend != nullptr) oldBlend->Release();

    ctx->OMSetDepthStencilState(oldDss, oldRef);
    if (oldDss != nullptr) oldDss->Release();

    ctx->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, oldRtv, oldDsv);
    if (numVp > 0)
        ctx->RSSetViewports(1, &oldVp);

    for (auto& r : oldRtv)
        if (r != nullptr)
            r->Release();
    if (oldDsv != nullptr)
        oldDsv->Release();

    return true;
}

void vp_set_draw_stereo(bool active)
{
    g_drawStereo.store(active, std::memory_order_relaxed);
}

void vp_publish_other(const VpFrame& frame)
{
    /* Written into BOTH slots, because vp_publish flips between them and this
       call does not know which one the next scan will read. The other eye's
       pose changes at the same rate as the current one, so there is nothing to
       be gained by pairing them any more carefully than this. */
    g_pub[0].other = frame;
    g_pub[1].other = frame;
}

void vp_publish(const VpFrame& frame)
{
    const uint32_t cur = g_pubIndex.load(std::memory_order_relaxed) & 1u;
    const uint32_t next = 1u - cur;

    /* Carry the outgoing frame forward as "previous", so the two travel together
       and the render thread can never see a current frame paired with a previous
       one from a different moment. */
    /* Cadence, before the swap, while g_pub[cur] is still the outgoing frame. */
    g_statPub.fetch_add(1, std::memory_order_relaxed);
    if (g_pub[cur].frame.valid && g_pub[cur].frame.eye != frame.eye)
        g_statPubEyeFlip.fetch_add(1, std::memory_order_relaxed);

    LARGE_INTEGER freq, now;
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&now);
    const int64_t last = g_pubLastQpc.exchange(now.QuadPart, std::memory_order_relaxed);
    if (last != 0 && freq.QuadPart > 0)
    {
        const int64_t us = (now.QuadPart - last) * 1000000 / freq.QuadPart;
        if (us >= 0 && us < 1000000)
        {
            g_pubGapSumUs.fetch_add(us, std::memory_order_relaxed);
            int32_t seen = g_pubGapMaxUs.load(std::memory_order_relaxed);
            while (us > seen &&
                   !g_pubGapMaxUs.compare_exchange_weak(seen, static_cast<int32_t>(us),
                                                        std::memory_order_relaxed))
            { }
        }
    }

    /* THE PREVIOUS FRAME OF *THIS* EYE, NOT JUST THE PREVIOUS FRAME.
     *
     * Under AFR the engine alternates sides every frame, so "the previous frame"
     * is the OTHER eye, 65 mm away. Feeding that to the motion-vector slot tells
     * a temporal upscaler that the camera teleported sideways between the two
     * images it is blending - and nothing in the per-object velocities says so
     * either. DLSS then accumulates across two viewpoints, worst on near, thin,
     * high-contrast geometry: characters, weapons, reins. That is the shimmer
     * and the ghosting, and VrAfrRenderer's own note diagnoses it exactly.
     *
     * It also names the reference fix. REFramework's m_fix_upscalers_wobbling:
     *
     *     oldViewMatrix[eye] = is_fix_dlss() ? d.old_view_matrix[eye]
     *                                        : d.old_view_matrix[other_eye];
     *
     * - feed the upscaler the SAME eye's previous view matrix. That is what this
     * keeps: a per-eye history, so the previous matrix handed to the engine is
     * two frames old and from this side, instead of one frame old and from the
     * other. The upscaler is then blending two images of the same viewpoint,
     * which is the case its history was designed for.
     *
     * vp_prev_same_eye = 0 restores the old immediate-previous behaviour. */
    static VpFrame s_prevByEye[2] = {};
    static bool    s_havePrevByEye[2] = { false, false };

    const int eyeIdx = (frame.eye == 1) ? 1 : 0;

    if (prev_same_eye() && s_havePrevByEye[eyeIdx])
    {
        g_pub[next].prev = s_prevByEye[eyeIdx];
        g_pub[next].havePrev = 1;
    }
    else
    {
        g_pub[next].prev = g_pub[cur].frame;
        g_pub[next].havePrev = g_pub[cur].frame.valid;
    }

    if (frame.valid)
    {
        s_prevByEye[eyeIdx] = frame;
        s_havePrevByEye[eyeIdx] = true;
    }

    g_pub[next].frame = frame;
    g_pub[next].stampMs = GetTickCount64();

    g_pubIndex.store(next, std::memory_order_release);
}

void vp_frame_boundary()
{
    if (!vp_active())
        return;

    /* Per-FRAME, so it has to precede the 5-second gate below. This is the only
       place in the process that knows where one rendered frame ends, which is
       what makes "the previous frame's view-projection" a definable thing at
       all. See the ReprojPair note. */
    reproj_roll();

    /* Roll the latch homography. The NGX correction wants LAST frame's, because
       that is the frame the motion vectors point back into. */
    if (g_latchHomoCurValid)
    {
        memcpy(g_latchHomoPrev, g_latchHomoCur, sizeof(g_latchHomoPrev));
        memcpy(g_latchHomoPrevInv, g_latchHomoCurInv, sizeof(g_latchHomoPrevInv));
        g_latchHomoPrevValid = true;
        g_latchHomoCurValid = false;
    }

    /* The NGX core is loaded by the game's DLSS shim, not at startup, so the
       attach is retried until it takes. Two module lookups once it has. */
    ngx_hook_install();

    verify_hooks();
    selftest_tick();

    /* Every Present: re-scans its inline hooks, advances a capture in progress,
       and starts one when the trigger fires. A load and a return when off. */
    frame_map_present(engine_context());
    stereo_frame_boundary();

    static int tick = 0;
    if (++tick < 450)          /* ~5 s at 90 fps */
        return;
    tick = 0;

    const int32_t vp      = g_statVp.exchange(0, std::memory_order_relaxed);
    const int32_t inv     = g_statInv.exchange(0, std::memory_order_relaxed);
    const int32_t scanned = g_statScanned.exchange(0, std::memory_order_relaxed);

    float l = 0.0f, r = 0.0f, u = 0.0f, d = 0.0f;
    const bool measured = vp_measured_tangents(&l, &r, &u, &d);

    const int32_t prev = g_statPrev.exchange(0, std::memory_order_relaxed);

    /* See g_anatomyBuffers: re-arm in gameplay, not just at load. */
    if (g_anatomyRearms > 0)
    {
        --g_anatomyRearms;
        g_anatomyBuffers = 3;

        /* THE SAME TRAP, AND IT ALREADY COST A WRONG ANSWER.
         *
           g_originBudget was a fixed 12 lines spent from the first frame, so the
           entire skybox survey came from battle LOAD - all twelve lines inside
           one second, before gameplay had a camera worth surveying. The survey
           saw only cubemap faces (axis-aligned forwards, square 90-degree
           frusta), concluded there was no skybox to catch, and vp_sky was turned
           off on that evidence.
         *
           The anatomy dump, which re-arms, then found what the survey never got
           to: cb 1760 offset 688, camera AT THE ORIGIN, forward tracking the
           reference camera at dot 0.995-0.997 across samples pointing in
           completely different directions, and a 75 x 76 degree frustum that is
           nothing like a cube face. That is the skybox signature the note on
           Settings::sky describes, and it was being skipped. Re-arm this one too
           so the next survey is of gameplay rather than of a loading screen. */
        g_originBudget = 6;
    }

    /* This window is exactly 450 frames. One publish per frame is the only
       cadence at which "prev" means one frame back, so anything below 450 is a
       motion-vector scale error of 450/pubs, not a rounding detail. */
    const int32_t pubs     = g_statPub.exchange(0, std::memory_order_relaxed);
    const int32_t eyeFlips = g_statPubEyeFlip.exchange(0, std::memory_order_relaxed);
    const int64_t gapSum   = g_pubGapSumUs.exchange(0, std::memory_order_relaxed);
    const int32_t gapMax   = g_pubGapMaxUs.exchange(0, std::memory_order_relaxed);
    if (pubs > 0)
    {
        BVR_INFO("VP publish cadence: %d publications across 450 frames (%.2f per "
                 "frame), mean gap %.2f ms, worst %.2f ms, eye changed %d times. "
                 "Motion vectors are scaled by %.2fx unless this is 1.00 per frame.",
                 pubs, pubs / 450.0f,
                 pubs > 1 ? (gapSum / 1000.0f) / (pubs - 1) : 0.0f,
                 gapMax / 1000.0f, eyeFlips, 450.0f / pubs);
    }

    if (measured)
    {
        BVR_INFO("VP patch: %d view (%d of them the PREVIOUS frame's, for motion "
                 "vectors) + %d inverse matrices replaced over %d constant buffer "
                 "uploads in ~5 s (%.1f view/frame). Engine frustum %.1f x %.1f deg.",
                 vp, prev, inv, scanned, vp / 450.0f,
                 (atanf(r) - atanf(l)) * 57.2957795f,
                 (atanf(u) - atanf(d)) * 57.2957795f);

        /* THE DEADZONE, as a count rather than a claim. See build_replacement.
           Head held still, this should be most of the substitutions - that is
           the tremor being refused. Head moving, it should fall to near zero;
           if it does not, vp_dead_m is eating real head movement and is set too
           wide. */
        {
            const uint64_t dead = g_statDeadzone.exchange(0, std::memory_order_relaxed);
            const uint64_t dn   = g_dCount.exchange(0, std::memory_order_relaxed);
            const uint64_t dts  = g_dTransSum.exchange(0, std::memory_order_relaxed);
            const uint32_t dtx  = g_dTransMax.exchange(0, std::memory_order_relaxed);
            const uint32_t dtn  = g_dTransMin.exchange(kNoMin, std::memory_order_relaxed);

            const uint64_t pc = g_dPrimCount.exchange(0, std::memory_order_relaxed);
            const uint64_t ps = g_dPrimSum.exchange(0, std::memory_order_relaxed);
            const uint32_t px = g_dPrimMax.exchange(0, std::memory_order_relaxed);
            const uint64_t oc = g_dOtherCount.exchange(0, std::memory_order_relaxed);
            const uint64_t os = g_dOtherSum.exchange(0, std::memory_order_relaxed);
            const uint32_t ox = g_dOtherMax.exchange(0, std::memory_order_relaxed);

            BVR_INFO("VP D: overall min %.0f mm, mean %.0f mm, max %.0f mm over %llu "
                     "substitution(s) in ~5 s; deadzone (%.1f mm / %.2f deg) refused "
                     "%llu.",
                     dtn == kNoMin ? 0.0 : dtn / 1.0,
                     dn > 0 ? (static_cast<double>(dts) / dn) : 0.0,
                     dtx / 1.0,
                     static_cast<unsigned long long>(dn),
                     dead_m() * 1000.0f, dead_deg(),
                     static_cast<unsigned long long>(dead));

            /* THE LINE THAT ACTUALLY DECIDES IT. If MAIN VIEW is millimetres and
               OTHER is hundreds of millimetres, the camera drive is fine and the
               damage is being done to passes that are not the main view - which
               is a matrix-SELECTION problem, fixed by not writing them. If MAIN
               VIEW is itself hundreds of millimetres, the drive is not putting
               the engine camera on the eye and that is the bug. */
            const uint64_t vc = g_dPrevCount.exchange(0, std::memory_order_relaxed);
            const uint64_t vs = g_dPrevSum.exchange(0, std::memory_order_relaxed);
            const uint32_t vx = g_dPrevMax.exchange(0, std::memory_order_relaxed);

            const uint64_t pd = g_dPrimDeg.exchange(0, std::memory_order_relaxed);
            const uint64_t vd = g_dPrevDeg.exchange(0, std::memory_order_relaxed);
            const uint64_t od = g_dOtherDeg.exchange(0, std::memory_order_relaxed);

            {
                const uint64_t skipped = g_statPrevSkipped.exchange(0, std::memory_order_relaxed);
                const uint64_t carried = g_statPrevCarried.exchange(0, std::memory_order_relaxed);
                if (carried > 0)
                    BVR_INFO("VP prev slot: %llu matrix(es) CARRIED in ~5 s - the engine's "
                             "own previous view moved into the latched frame with the same "
                             "D as the live view, so static motion vectors cancel and the "
                             "ground stops smearing. Dynamic entities are untouched: their "
                             "velocities never come from this buffer.",
                             static_cast<unsigned long long>(carried));
                if (skipped > 0 || !write_prev_slot())
                    BVR_INFO("VP prev slot: %llu write(s) SKIPPED in ~5 s "
                             "(vp_write_prev_slot = 0). The engine keeps its own "
                             "previous-frame matrix, so its camera term and its "
                             "per-object cached transforms describe the same moment "
                             "again. Dynamic entities are the only things that can "
                             "tell the difference.",
                             static_cast<unsigned long long>(skipped));
            }

            BVR_INFO("VP D split (position is the TRUE pose delta now, not D's "
                     "translation row - see build_replacement): LIVE VIEW mean "
                     "%.1f mm / %.4f deg over %llu | PREV SLOT (frame-old pose, "
                     "large is CORRECT) mean %.1f mm / %.4f deg over %llu | OTHER "
                     "mean %.1f mm / %.4f deg over %llu. Position near zero means "
                     "the camera IS on the eye and the ANGLE is all that is left; "
                     "at a 1090 m origin a hundredth of a degree used to print as "
                     "227 mm of 'displacement'.",
                     pc > 0 ? (static_cast<double>(ps) / pc) : 0.0,
                     pc > 0 ? (static_cast<double>(pd) / pc) / 1000.0 : 0.0,
                     static_cast<unsigned long long>(pc),
                     vc > 0 ? (static_cast<double>(vs) / vc) : 0.0,
                     vc > 0 ? (static_cast<double>(vd) / vc) / 1000.0 : 0.0,
                     static_cast<unsigned long long>(vc),
                     oc > 0 ? (static_cast<double>(os) / oc) : 0.0,
                     oc > 0 ? (static_cast<double>(od) / oc) / 1000.0 : 0.0,
                     static_cast<unsigned long long>(oc));
        }

        /* THE DRAW CENSUS - the number the stereo question turns on.
         *
           Rendering the scene twice out of ONE engine frame means issuing every
           scene draw a second time with the other eye's matrices. Whether that
           is affordable is entirely a question of how many there are, and until
           now nothing here could answer it: the only draw hook was DrawIndexed,
           which this engine never calls.
         *
           Per frame is the figure to read. A few thousand is ordinary for a
           battle and doubling it costs draw submission only - no tick, no
           animation, no culling, no second visible set - which is the whole
           reason this is cheaper than rendering a second engine frame. Tens of
           thousands would say otherwise. */
        {
            const unsigned long long di =
                g_cDrawInstanced.exchange(0, std::memory_order_relaxed);
            const unsigned long long dp =
                g_cDrawPlain.exchange(0, std::memory_order_relaxed);
            const unsigned long long dip =
                g_cDrawInstancedPlain.exchange(0, std::memory_order_relaxed);
            const unsigned long long dix =
                g_cDraw.exchange(0, std::memory_order_relaxed);

            const unsigned long long total = di + dp + dip + dix;

            const unsigned long long scene =
                g_cDrawScene.exchange(0, std::memory_order_relaxed);
            const unsigned long long rtc =
                g_cRtChanges.exchange(0, std::memory_order_relaxed);

            BVR_INFO("Draw census over the same ~5 s: %llu total (%.0f per frame) - "
                     "DrawIndexedInstanced %llu, Draw %llu, DrawInstanced %llu, "
                     "DrawIndexed %llu.",
                     total, total / 450.0f, di, dp, dip, dix);

            /* THE NUMBER THE BUILD IS SIZED AGAINST.
             *
               Only the draws writing the eye colour target have to be issued a
               second time. Shadow passes are light-space and shared; the
               interface and post-processing work on a finished image. So this,
               not the total above, is what doubling actually costs - and it is
               the figure to compare against a whole second engine frame, which
               pays for all of the above PLUS the tick, the animation and the
               culling.
             *
               A scene count near zero would mean the eye target is not what the
               scene draws write, and the classification has to move before any
               of this can be built on. */
            BVR_INFO("Of those, %llu wrote the eye texture (%.0f per frame) across "
                     "%llu render-target changes. One a frame is the final resolve, "
                     "not the scene - rgl draws into its own buffers and blits the "
                     "finished image out.",
                     scene, scene / 450.0f, rtc);

            /* THE BUSIEST TARGETS, WHICH IS WHERE THE SCENE REALLY IS.
             *
               The scene colour buffer is the one carrying draws in the
               thousands at or near eye resolution. A shadow atlas is square and
               usually a different size; a post stage takes a handful of draws;
               the eye texture takes exactly one. Whichever line here has the
               bulk of the 1,465 is the surface the doubling has to attach to,
               and its dimensions say whether it is per-eye sized already. */
            int order[kRtStats];
            const int n = g_rtStatCount;
            for (int i = 0; i < n; ++i) order[i] = i;
            for (int i = 1; i < n; ++i)
            {
                const int key = order[i];
                int j = i - 1;
                while (j >= 0 && g_rtStats[order[j]].draws < g_rtStats[key].draws)
                {
                    order[j + 1] = order[j];
                    --j;
                }
                order[j + 1] = key;
            }

            const unsigned long long orphan =
                g_cDrawUnattributed.exchange(0, std::memory_order_relaxed);

            BVR_INFO("Render targets by draw count (%d of %d slots used, %llu draw(s) "
                     "attributed to no target). The one with thousands of draws at "
                     "eye-ish size is the scene colour buffer - that is what a second "
                     "eye has to be drawn into:", n, kRtStats, orphan);

            for (int i = 0; i < n && i < 10; ++i)
            {
                const RtStat& s = g_rtStats[order[i]];
                if (s.draws == 0)
                    break;   /* sorted, so the first idle one ends the list */

                BVR_INFO("    %4ux%-4u fmt %-3u  %llu draw(s) (%.0f/frame)%s   [eg %p]",
                         s.width, s.height, s.format,
                         static_cast<unsigned long long>(s.draws), s.draws / 450.0f,
                         (eye_texture(0) != nullptr &&
                          s.sample == static_cast<ID3D11Resource*>(eye_texture(0)))
                             ? "   <- OUR EYE TEXTURE" : "",
                         static_cast<void*>(s.sample));
            }

            /* THE SCENE TARGET, CHOSEN BY MEASUREMENT.
             *
               The busiest colour target that is not the desktop mirror. The
               mirror is excluded by shape: it is the game window's size, which
               is not the eye size and not the upscaler's internal size. Derived
               fresh each window so a resolution or upscaler change moves it
               without anyone editing a constant. */
            /* BY ASPECT, because there is no accessor for the mirror's size and
               aspect separates them cleanly anyway. The eye target is 3400x3468
               - very nearly square, 0.98 - and the scene buffer shares that
               shape because it is the same view at the upscaler's internal
               scale: 2267x2312 is also 0.98. The desktop mirror is the game
               window, 2560x1440, which is 1.78. Nothing else in the frame is
               both busy and eye-shaped. */
            float eyeAspect = 0.0f;
            if (ID3D11Texture2D* eye0 = eye_texture(0))
            {
                D3D11_TEXTURE2D_DESC ed = {};
                eye0->GetDesc(&ed);
                if (ed.Height != 0)
                    eyeAspect = static_cast<float>(ed.Width) / static_cast<float>(ed.Height);
            }

            int sceneIdx = -1;
            for (int i = 0; i < n; ++i)
            {
                const RtStat& s = g_rtStats[order[i]];
                if (s.draws == 0)
                    break;
                if (s.height == 0)
                    continue;

                const float a = static_cast<float>(s.width) / static_cast<float>(s.height);
                if (eyeAspect > 0.0f && (a < eyeAspect - 0.05f || a > eyeAspect + 0.05f))
                    continue;   /* the mirror, or a shadow atlas */

                sceneIdx = order[i];
                break;
            }

            /* THE SCENE IS A SIZE, NOT A SIZE AND A FORMAT.
             *
               Matching the format as well left a second target of exactly the
               same dimensions untouched - 2048x2176 fmt 26, R11G11B10_FLOAT,
               taking 57 draws a frame. That is the HDR target, and in a
               deferred renderer it is where the sky is drawn and often the
               ground with it. The right eye came back with soldiers, distant
               terrain and vegetation, and no ground and no sky, which is
               exactly that set of draws going unduplicated.
             *
               So the scene is recognised by DIMENSIONS from here on. The format
               still decides which duplicate a target gets - see the shape-keyed
               pool - but it no longer decides whether a draw is scene at all. */
            if (sceneIdx >= 0)
            {
                const RtStat& s = g_rtStats[sceneIdx];
                g_sceneW.store(s.width, std::memory_order_relaxed);
                g_sceneH.store(s.height, std::memory_order_relaxed);
                g_sceneFmt.store(s.format, std::memory_order_relaxed);

                /* The full G-buffer duplicate is made by ensure_dup_set when the
                   scene is next bound, and it owns g_dupTex. The standalone
                   target that used to be created here owned it too, and the two
                   fought: whichever ran last won the pointer, and the sampling
                   view was left pointing at the loser. Nothing needs it now. */;

                const unsigned long long done =
                    g_cRedirected.exchange(0, std::memory_order_relaxed);

                BVR_INFO("Scene target picked: %ux%u fmt %u with %llu draw(s) this "
                         "window (%.0f/frame). Second draw is %s%s.",
                         s.width, s.height, s.format,
                         static_cast<unsigned long long>(s.draws), s.draws / 450.0f,
                         redirect_enabled() ? "ON" : "off (vp_second_draw = 1)",
                         redirect_enabled()
                             ? (done > 0 ? " and re-issuing" : " but re-issued NOTHING")
                             : "");

                if (redirect_enabled() && done > 0)
                {
                    const unsigned long long built =
                        g_cShadowBuilt.exchange(0, std::memory_order_relaxed);
                    const unsigned long long bound =
                        g_cShadowBound.exchange(0, std::memory_order_relaxed);

                    const unsigned long long pre =
                        g_cPrepassRedirected.exchange(0, std::memory_order_relaxed);
                    const unsigned long long dc =
                        g_cDepthCleared.exchange(0, std::memory_order_relaxed);
                    BVR_INFO("Second eye: our depth cleared alongside the engine's %llu "
                             "time(s) this window (%.0f/frame). Zero means the clear is "
                             "not being matched and the prepass writes into last "
                             "frame's depth.", dc, dc / 450.0f);

                    const unsigned long long disp =
                        g_cDispatch.exchange(0, std::memory_order_relaxed);
                    BVR_INFO("Compute dispatches this window: %llu (%.0f/frame). A "
                             "deferred renderer that lights with compute does it here, "
                             "and nothing in the draw path can see it.",
                             disp, disp / 450.0f);

                    BVR_INFO("Second eye: %llu depth-prepass draw(s) duplicated into "
                             "our own depth this window (%.0f/frame). Zero here with a "
                             "black eye means the EQUAL-tested G-buffer pass has nothing "
                             "to match against.", pre, pre / 450.0f);

                    BVR_INFO("Second draw: %llu re-issued (%.0f/frame). Second-eye "
                             "constants: %llu buffer(s) built, %llu bind(s) into VS "
                             "slot b%d. Binds matching the re-issue count means every "
                             "duplicated draw really used the OTHER eye - the two "
                             "images should now differ by the IPD instead of being "
                             "identical.",
                             done, done / 450.0f, built, bound,
                             g_viewCbSlot.load(std::memory_order_relaxed));
                }
            }

            for (int i = 0; i < n; ++i)
                g_rtStats[i].draws = 0;

            /* The table is rebuilt from scratch each window. Shapes come and go
               with the upscaler and the post chain, and a stale entry would
               keep a dead shape alive at the top of the list forever. */
            g_rtStatCount = 0;
            g_curRtStat.store(-1, std::memory_order_relaxed);
        }

        /* Once, and only once there is something to say: every distinct place a
           main-view matrix lives, and how many of the hits at each were the
           previous frame's. A site that is ALWAYS previous is the motion-vector
           matrix; one that never is, is the live view. */
        static bool sitesLogged = false;
        const int rows = g_siteCount.load(std::memory_order_relaxed);
        if (!sitesLogged && rows > 0)
        {
            sitesLogged = true;
            BVR_INFO("VP patch sites (%d distinct):", rows);
            for (int i = 0; i < rows && i < kSiteRows; ++i)
            {
                BVR_INFO("    cb %6u bytes  offset %4u  %s  hits %llu (previous-frame %llu)",
                         g_sites[i].bytes, g_sites[i].offset,
                         g_sites[i].transposed ? "transposed" : "row-vector",
                         static_cast<unsigned long long>(g_sites[i].hits),
                         static_cast<unsigned long long>(g_sites[i].prevHits));
            }
        }

        /* Every 5 s, not once, because the numbers that matter here only appear
           while the head is MOVING: a precomposed reprojection matrix sits at
           identity with a still head and drifts further the faster you turn. A
           table printed at t=30 s and never again would show neither. */
        const int rejRows = g_rejectCount.load(std::memory_order_relaxed);
        if (rejRows > 0)
        {
            BVR_INFO("VP rejected view-shaped matrices (%d distinct) - these are "
                     "NOT being patched:", rejRows);
            for (int i = 0; i < rejRows && i < kProbeRows; ++i)
                BVR_INFO("    cb %6u bytes  offset %4u  %s  hits %llu  best dot %.4f  "
                         "nearest camera %.2f m",
                         g_rejects[i].bytes, g_rejects[i].offset,
                         g_rejects[i].transposed ? "transposed" : "row-vector",
                         static_cast<unsigned long long>(g_rejects[i].hits),
                         g_rejects[i].bestDot, g_rejects[i].minPos);
        }

        const int idRows = g_nearIdCount.load(std::memory_order_relaxed);
        if (idRows > 0)
        {
            BVR_INFO("VP near-identity blocks (%d distinct) - reprojection-matrix "
                     "suspects, drift grows with head speed:", idRows);
            for (int i = 0; i < idRows && i < kProbeRows; ++i)
                BVR_INFO("    cb %6u bytes  offset %4u  hits %llu  max drift from "
                         "identity %.5f",
                         g_nearId[i].bytes, g_nearId[i].offset,
                         static_cast<unsigned long long>(g_nearId[i].hits),
                         g_nearId[i].maxDev);
        }

        /* THE CASCADES, WHICH NOTHING HERE HAS EVER SEEN.
           recover() excludes orthographic matrices on purpose - that is what
           kept shadow and post passes out of the main-view search - so five
           rounds of theorising about shadows following the head were arguing
           about matrices that were never in the search space. These are found by
           their own signature and only WATCHED: what matters is whether a
           cascade's translation travels with the camera. */
        int orthoRows = 0;
        for (int i = 0; i < kProbeRows; ++i)
            if (g_ortho[i].used)
                ++orthoRows;

        if (orthoRows > 0)
        {
            BVR_INFO("VP orthographic blocks (%d distinct) - SHADOW CASCADE "
                     "candidates, never patched. 'moved' is how far the block's "
                     "own translation travelled between sightings: compare it "
                     "with the main view's own movement above. Tracking the "
                     "camera means the cascade is fitted to the head, and a "
                     "shadow map built from a head-tracked camera is shadows "
                     "that follow the head.", orthoRows);

            for (int i = 0; i < kProbeRows; ++i)
            {
                if (!g_ortho[i].used)
                    continue;

                BVR_INFO("    cb %6u bytes  offset %4u  hits %llu  translation "
                         "(%.1f,%.1f,%.1f)  max moved %.4f",
                         g_ortho[i].bytes, g_ortho[i].offset,
                         static_cast<unsigned long long>(g_ortho[i].hits),
                         g_ortho[i].tx, g_ortho[i].ty, g_ortho[i].tz,
                         g_ortho[i].maxMoved);

                g_ortho[i].maxMoved = 0.0f;
            }
        }
        else
        {
            BVR_INFO("VP orthographic blocks: none seen. Either the cascades are "
                     "not in a constant buffer this scan reaches, or they are not "
                     "stored in the row-vector form ortho_signature looks for.");
        }

        /* THE LANES. This is the measurement TEST 62 stopped one step short of.
           Its own conclusion was that the ortho blocks carry rotation and scale
           with translation (0,0,0), so the per-cascade origin is packed beside
           the matrix as a float3 - and then it sent the next attempt to
           RenderDoc on the grounds that a matrix scan cannot see a float3. It
           cannot. A lane difference can, and it is the same measurement the
           site census already makes for view matrices. */
        {
            LaneGuard guard;
            if (guard.held)
            {
                int watched = 0;
                for (int i = 0; i < kLaneSlots; ++i)
                    if (g_laneBuf[i].bytes != 0)
                        ++watched;

                BVR_INFO("VP cascade lanes: watching %d of %d buffer slot(s), up "
                         "to %u bytes each. Every 16-byte lane differenced as a "
                         "float3 against the head's own step. ratio near 1 with "
                         "align near 1 is a lane RIDING THE HEAD; 'step' is how "
                         "far it jumps at once, which for a cascade origin is the "
                         "world size of a shadow texel. A buffer reported as "
                         "STATIC moved nothing at all and is not a suspect.",
                         watched, kLaneSlots, kLaneMaxBytes);

                for (int i = 0; i < kLaneSlots; ++i)
                {
                    LaneBuf& lb = g_laneBuf[i];
                    if (lb.bytes == 0)
                        continue;

                    if (lb.headTravel <= 0.02f)
                    {
                        BVR_INFO("  cb %6u%s: head barely moved (%.3f m) - no verdict.",
                                 lb.bytes, lb.hadOrtho ? " [ortho]" : "",
                                 lb.headTravel);
                        continue;
                    }

                    const uint32_t lanes = (lb.bytes / 16) < kLaneMaxLanes
                                         ? (lb.bytes / 16) : kLaneMaxLanes;

                    /* Printed BEFORE the ranking, so a buffer that moved nothing
                       says so out loud. The first cut of this printed a header
                       and then silently nothing, and an empty list under a
                       confident header was read as a measurement rather than as
                       the absence of one. */
                    int moved = 0;
                    for (uint32_t li = 0; li < lanes; ++li)
                        if (g_laneAcc[i][li].travel > 1e-6f)
                            ++moved;

                    BVR_INFO("  cb %6u%s: head travelled %.3f m over %u frame "
                             "sample(s), %d of %u lane(s) moved.%s",
                             lb.bytes, lb.hadOrtho ? " [ortho]" : "",
                             lb.headTravel, lb.samples, moved, lanes,
                             moved == 0 ? "  STATIC - not a suspect." : "");

                    /* RANKED BY WHETHER IT RIDES THE HEAD, NOT BY HOW FAR IT WENT.
                     *
                     * Ranking by travel put the noisiest lane in the renderer at
                     * the top of every list - per-object data whose "movement" is
                     * the distance between two unrelated objects. What is wanted
                     * is the opposite: a lane that moves EXACTLY as far as the
                     * head, in the SAME direction. So:
                     *
                     *     score = align * min(ratio, 1/ratio)
                     *
                     * which is 1 only when the lane tracks the head one-for-one
                     * and falls away for a lane that moves too far, too little,
                     * or in an unrelated direction. Anything above ~0.8 is a
                     * lane riding the head; the churn that dominated the first
                     * report scores under 0.1 no matter how large its travel. */
                    for (int rank = 0; rank < 8 && moved > 0; ++rank)
                    {
                        uint32_t best = kLaneMaxLanes;
                        float bestScore = 0.0f;

                        for (uint32_t li = 0; li < lanes; ++li)
                        {
                            const LaneAcc& c = g_laneAcc[i][li];
                            if (c.samples < 4 || c.travel <= 1e-6f)
                                continue;

                            const float ratio = c.travel / lb.headTravel;
                            const float align = c.align / static_cast<float>(c.samples);
                            const float shape = ratio > 1.0f ? 1.0f / ratio : ratio;
                            const float score = align * shape;

                            if (score > bestScore)
                            {
                                bestScore = score;
                                best = li;
                            }
                        }

                        if (best == kLaneMaxLanes || bestScore <= 0.02f)
                            break;

                        const LaneAcc& a = g_laneAcc[i][best];
                        BVR_INFO("      offset %5u  score %.3f  travel %9.3f  "
                                 "ratio %8.3f  align %.3f  step %8.4f  n %u",
                                 best * 16u, bestScore, a.travel,
                                 a.travel / lb.headTravel,
                                 a.align / static_cast<float>(a.samples),
                                 a.maxStep, a.samples);

                        /* Claimed, so the next rank finds the next one down. */
                        g_laneAcc[i][best].samples = 0;
                    }

                    memset(g_laneAcc[i], 0, sizeof(g_laneAcc[i]));
                    lb.headTravel = 0.0f;
                    lb.samples = 0;
                }
            }
        }


        /* The direction lanes. See the note on ray_census. */
        if (ray_census_on())
        {
            BVR_INFO("VP ray census: which lanes hold a world direction bolted to "
                     "the camera. explained 1.0 = the lane rotated with the camera "
                     "and did nothing else; 0.0 = it is anchored to the world or "
                     "moving for its own reasons; negative = it moved AGAINST the "
                     "camera. Four consecutive lanes near 1.0 in one buffer are "
                     "frustum corner rays - the sky's, and the deferred pass's.");

            for (int i = 0; i < kRaySlots; ++i)
            {
                RayBuf& rb = g_rayBuf[i];
                if (rb.bytes == 0)
                    continue;

                if (rb.turned <= 0.02f)
                {
                    BVR_INFO("  cb %6u: camera barely turned (%.2f deg) - no verdict.",
                             rb.bytes, rb.turned * 57.29578f);
                    continue;
                }

                const uint32_t lanes = (rb.bytes / 16) < kRayMaxLanes
                                     ? (rb.bytes / 16) : kRayMaxLanes;

                /* Printed before the ranking, for the reason the lane census
                   gives: a header followed by silence reads as a measurement
                   rather than as the absence of one. */
                int voted = 0;
                for (uint32_t li = 0; li < lanes; ++li)
                    if (g_rayAcc[i][li].samples >= 4)
                        ++voted;

                BVR_INFO("  cb %6u: camera turned %.1f deg over %u frame sample(s), "
                         "%d of %u lane(s) had a direction to judge.%s",
                         rb.bytes, rb.turned * 57.29578f, rb.samples, voted, lanes,
                         voted == 0 ? "  NOTHING TO JUDGE - no lane holds a vector."
                                    : "");

                /* THE WHOLE BLOCK, RANKED OR NOT.

                   The ranking BELOW stops at eight lanes and at 0.5, and it
                   CLAIMS what it prints by zeroing the lane, so this has to
                   read the table FIRST or it reports the ranking's leavings as
                   an absence of data - which it did, printing "no vector" for
                   the very lanes the ranking had just named. vp_ray_window is
                   a first offset and vp_ray_window_lanes a count, and every
                   lane in it prints whatever it scored, ranked or not. */
                const int wnd = ray_window();
                if (wnd >= 0 && (ray_cb() == 0 || rb.bytes == ray_cb()))
                {
                    BVR_INFO("    window at offset %d, %d lane(s) - every lane, "
                             "ranked or not:", wnd, ray_window_lanes());
                    for (int k = 0; k < ray_window_lanes(); ++k)
                    {
                        const uint32_t li = static_cast<uint32_t>(wnd) / 16u + static_cast<uint32_t>(k);
                        if (li >= lanes)
                            break;
                        const RayAcc& a = g_rayAcc[i][li];
                        if (a.samples == 0)
                        {
                            /* Unseen by the accumulators is not unseen. Print
                               what is actually in it, w included - the length
                               says at once whether this is a lane of zeroes
                               that costs nothing to rotate, or a packed scalar
                               that must never be touched. */
                            if (k < kWinMax && g_winSeen[k])
                            {
                                const float* rw = g_winRaw[k];
                                const float rl = sqrtf(rw[0]*rw[0] + rw[1]*rw[1] +
                                                       rw[2]*rw[2]);
                                BVR_INFO("      offset %5u  NO DIRECTION  raw "
                                         "(%.6f,%.6f,%.6f, w %.6f)  |xyz| %.6f - "
                                         "%s",
                                         li * 16u, rw[0], rw[1], rw[2], rw[3], rl,
                                         rl < 1e-3f ? "zero-ish, harmless to rotate"
                                                    : "NOT a direction - do not rotate this");
                            }
                            else
                            {
                                BVR_INFO("      offset %5u  (never sampled)", li * 16u);
                            }
                            continue;
                        }
                        char dec[96];
                        dec[0] = 0;
                        if (g_engBasisValid.load(std::memory_order_relaxed))
                        {
                            const float len = sqrtf(a.last[0]*a.last[0] +
                                                    a.last[1]*a.last[1] +
                                                    a.last[2]*a.last[2]);
                            if (len > 1e-6f)
                            {
                                const float ux = a.last[0]/len, uy = a.last[1]/len, uz = a.last[2]/len;
                                _snprintf_s(dec, _TRUNCATE, "  R %+.3f U %+.3f F %+.3f",
                                            ux*g_engBasis[0] + uy*g_engBasis[1] + uz*g_engBasis[2],
                                            ux*g_engBasis[3] + uy*g_engBasis[4] + uz*g_engBasis[5],
                                            ux*g_engBasis[6] + uy*g_engBasis[7] + uz*g_engBasis[8]);
                            }
                        }
                        const float wcomp = (k < kWinMax && g_winSeen[k])
                                          ? g_winRaw[k][3] : 0.0f;
                        BVR_INFO("      offset %5u  explained %6.3f  |w| %8.3f  "
                                 "raw (%.4f,%.4f,%.4f, w %.4f)  n %u%s",
                                 li * 16u, a.explained / static_cast<float>(a.samples),
                                 a.lenSum / static_cast<float>(a.samples),
                                 a.last[0], a.last[1], a.last[2], wcomp,
                                 a.samples, dec);
                    }
                }


                for (int rank = 0; rank < 8 && voted > 0; ++rank)
                {
                    uint32_t best = kRayMaxLanes;
                    float bestScore = -2.0f;

                    for (uint32_t li = 0; li < lanes; ++li)
                    {
                        const RayAcc& c = g_rayAcc[i][li];
                        if (c.samples < 4)
                            continue;

                        const float e = c.explained / static_cast<float>(c.samples);
                        if (e > bestScore)
                        {
                            bestScore = e;
                            best = li;
                        }
                    }

                    /* Below a half the lane is not explained by the camera at
                       all, and everything under it is worse. Corner rays come
                       out at 0.9 and up; there is no useful middle to print. */
                    if (best == kRayMaxLanes || bestScore < 0.5f)
                        break;

                    const RayAcc& a = g_rayAcc[i][best];

                    /* Resolve the lane against the engine's own basis. A lane
                       that is k * right prints R 1.000 U 0.000 F 0.000, and
                       there is then nothing left to infer: the axis is named,
                       the scale is |w|, and a scale that equals a measured
                       tangent says the lane is a frustum extent vector. */
                    char decoded[96];
                    decoded[0] = 0;
                    if (g_engBasisValid.load(std::memory_order_relaxed))
                    {
                        const float len = sqrtf(a.last[0]*a.last[0] +
                                                a.last[1]*a.last[1] +
                                                a.last[2]*a.last[2]);
                        if (len > 1e-6f)
                        {
                            const float ux = a.last[0]/len, uy = a.last[1]/len, uz = a.last[2]/len;
                            const float dr = ux*g_engBasis[0] + uy*g_engBasis[1] + uz*g_engBasis[2];
                            const float du = ux*g_engBasis[3] + uy*g_engBasis[4] + uz*g_engBasis[5];
                            const float df = ux*g_engBasis[6] + uy*g_engBasis[7] + uz*g_engBasis[8];
                            _snprintf_s(decoded, _TRUNCATE,
                                        "  R %+.3f U %+.3f F %+.3f", dr, du, df);
                        }
                    }

                    BVR_INFO("      offset %5u  explained %.3f  |w| %7.3f  "
                             "moved %8.4f  n %u%s",
                             best * 16u, bestScore,
                             a.lenSum / static_cast<float>(a.samples),
                             a.moved, a.samples, decoded);

                    /* Claimed, so the next rank finds the next one down. */
                    g_rayAcc[i][best].samples = 0;
                }

                /* THE POSITIONS. See the note on RayAcc::posTravel. */
                if (rb.camTravel > 0.05f)
                {
                    BVR_INFO("    positions in cb %u - lanes whose xyz/w rides the "
                             "camera. Camera moved %.3f m over %u sample(s). ratio "
                             "near 1 and align near 1 is a lane bolted to the "
                             "camera; a cascade origin is exactly that.",
                             rb.bytes, rb.camTravel, rb.posSamples);

                    for (int rank = 0; rank < 6; ++rank)
                    {
                        uint32_t best = kRayMaxLanes;
                        float bestScore = 0.0f;

                        for (uint32_t li = 0; li < lanes; ++li)
                        {
                            const RayAcc& c = g_rayAcc[i][li];
                            if (c.posSamples < 4 || c.posTravel <= 1e-6f)
                                continue;
                            const float ratio = c.posTravel / rb.camTravel;
                            const float align = c.posAlign / static_cast<float>(c.posSamples);
                            const float shape = ratio > 1.0f ? 1.0f / ratio : ratio;
                            const float score = align * shape;
                            if (score > bestScore) { bestScore = score; best = li; }
                        }

                        if (best == kRayMaxLanes || bestScore <= 0.20f)
                            break;

                        const RayAcc& a = g_rayAcc[i][best];
                        BVR_INFO("      offset %5u  score %.3f  ratio %8.3f  "
                                 "align %.3f  step %8.4f  n %u",
                                 best * 16u, bestScore,
                                 a.posTravel / rb.camTravel,
                                 a.posAlign / static_cast<float>(a.posSamples),
                                 a.posMax, a.posSamples);

                        g_rayAcc[i][best].posSamples = 0;
                    }
                }

                memset(g_rayAcc[i], 0, sizeof(g_rayAcc[i]));
                rb.turned = 0.0f;
                rb.samples = 0;
                rb.camTravel = 0.0f;
                rb.posSamples = 0;
            }
        }

        {
            const uint64_t rays    = g_statRay.exchange(0, std::memory_order_relaxed);
            const uint64_t skipped = g_statRaySkip.exchange(0, std::memory_order_relaxed);
            const uint64_t notBasis = g_statRayNotBasis.exchange(0, std::memory_order_relaxed);
            const uint64_t borrowed = g_statRayBorrowed.exchange(0, std::memory_order_relaxed);
            const uint64_t staleD   = g_statRayNoD.exchange(0, std::memory_order_relaxed);
            const uint64_t pts      = g_statRayPoint.exchange(0, std::memory_order_relaxed);
            const uint64_t ptsBad   = g_statRayPointBad.exchange(0, std::memory_order_relaxed);
            const uint64_t ptsMoved = g_statRayPointMoved.exchange(0, std::memory_order_relaxed);

            const uint64_t prbN = g_statRayProbeN.exchange(0, std::memory_order_relaxed);
            const uint64_t prbD = g_statRayProbeDeg.exchange(0, std::memory_order_relaxed);
            const uint64_t prbM = g_statRayProbeMm.exchange(0, std::memory_order_relaxed);
            if (prbN != 0)
                BVR_INFO("VP ray D probe: D is %.3f deg and moves a point 20 m in "
                         "front of the camera by %.1f mm, mean over %llu frame(s). "
                         "These two must agree - 20 m at 0.5 deg is 175 mm. If the "
                         "millimetres are hundreds of thousands, the pivot is not "
                         "the camera and the point transform is wrong however "
                         "plausible it looks.",
                         static_cast<double>(prbD) / 1000.0 / static_cast<double>(prbN),
                         static_cast<double>(prbM) / static_cast<double>(prbN),
                         static_cast<unsigned long long>(prbN));

            if (pts != 0 || ptsBad != 0)
                BVR_INFO("VP ray points: %llu position lane(s) moved about the "
                         "CAMERA by the full D, mean %.1f mm each, %llu refused "
                         "for being outside the expected magnitude. Compare with "
                         "rotating the same lane about the world ORIGIN, which "
                         "is what TESTs 150-153 did: 13593 units out at half a "
                         "degree is ~118000 mm. If this mean is centimetres, the "
                         "transform is finally the right one.",
                         static_cast<unsigned long long>(pts),
                         pts ? static_cast<double>(ptsMoved) / static_cast<double>(pts) : 0.0,
                         static_cast<unsigned long long>(ptsBad));
            if (rays != 0 || skipped != 0 || notBasis != 0 || borrowed != 0)
                BVR_INFO("VP ray patch: %llu direction lane(s) rotated into the eye "
                         "frame at cb %u, offsets %d x%d and %d x%d, sign %+.0f. "
                         "%llu upload(s) skipped as ALREADY OURS - that is the "
                         "engine re-uploading the buffer without rewriting these "
                         "lanes, and each one is a compounded D that no longer "
                         "happens. A skip count several times the rotate count "
                         "means compounding was the whole fault. %llu lane(s) "
                         "REFUSED because the offset did not hold a camera axis "
                         "on that upload - cascades and skybox rows sharing the "
                         "same offset, which is what pass four used to rotate. "
                         "%llu upload(s) BORROWED the frame's D for want of a "
                         "main view of their own, %llu of those from an EARLIER "
                         "frame - a nonzero stale count is the TEST 150 flicker "
                         "bug returning by the back door, and means this buffer "
                         "uploads before the main view does.",
                         static_cast<unsigned long long>(rays),
                         ray_cb(), ray_offset(), ray_count(),
                         ray_offset_b(), ray_count_b(), ray_patch(),
                         static_cast<unsigned long long>(skipped),
                         static_cast<unsigned long long>(notBasis),
                         static_cast<unsigned long long>(borrowed),
                         static_cast<unsigned long long>(staleD));
        }

        /* The snap, once it has been armed. A cascade whose matrix could not be
           found in its own entry is the stride or the count being wrong, and it
           says so rather than silently writing to whatever is at that offset. */
        if (snap_cfg().offset >= 0)
        {
            const uint64_t snapped = g_cSnapped.exchange(0, std::memory_order_relaxed);
            const uint64_t noMat   = g_cSnapNoMatrix.exchange(0, std::memory_order_relaxed);

            BVR_INFO("VP cascade snap: %llu origin(s) quantised to a %.0f-texel "
                     "grid, %llu skipped for want of an orthographic matrix in "
                     "the entry. Skips mean vp_cascade_snap_stride or "
                     "vp_cascade_snap_count is wrong.",
                     static_cast<unsigned long long>(snapped),
                     snap_cfg().texels,
                     static_cast<unsigned long long>(noMat));
        }


        /* WHICH PASSES THE PATCHED BUFFER IS SERVING. A depth-only row - "colour
           no" - is a shadow map, and its hit count is how many times a frame we
           were handing the shadow pass the eye camera. */
        {
            int ctxRows = 0;
            for (int i = 0; i < kTgtCtxRows; ++i)
                if (g_tgtCtx[i].used)
                    ++ctxRows;

            if (ctxRows > 0)
            {
                BVR_INFO("VP patch passes (%d distinct target set(s), counted on "
                         "CONFIRMED main-view matches only). A SQUARE depth "
                         "target with no colour is the shadow map - the scene "
                         "depth is 2048x2176 and never square. "
                         "vp_patch_skip_shadow_px = %u is skipping that one; 0 "
                         "means the rule is off and every row below is being "
                         "written.", ctxRows, patch_skip_shadow_px());

                for (int i = 0; i < kTgtCtxRows; ++i)
                {
                    if (!g_tgtCtx[i].used)
                        continue;
                    BVR_INFO("    depth target %5u x %-5u  colour %-3s  hits %llu",
                             g_tgtCtx[i].w, g_tgtCtx[i].h,
                             g_tgtCtx[i].hasRtv ? "yes" : "NO",
                             static_cast<unsigned long long>(g_tgtCtx[i].hits));
                    g_tgtCtx[i].hits = 0;
                }
            }
        }

        /* Whether the head-following passes are actually being corrected now, or
           whether identification is still waiting. "written 0, declined N" with a
           latched form means the engine stopped storing the form we identified,
           which is a layout change and not a silent success. */
        const int32_t form = g_reprojForm.load(std::memory_order_relaxed);
        const uint64_t rWrote = g_statReproj.exchange(0, std::memory_order_relaxed);
        const uint64_t rSkip  = g_statReprojSkip.exchange(0, std::memory_order_relaxed);
        const uint64_t rStill = g_statReprojStill.exchange(0, std::memory_order_relaxed);
        const uint64_t rHeld  = g_statReprojHeld.exchange(0, std::memory_order_relaxed);
        if (rHeld > 0)
            BVR_INFO("VP reprojection HELD: vp_reproj_patch = 0, so %llu "
                     "substitution(s) that would have been written were not. Pass "
                     "three is otherwise running in full. This is the single "
                     "variable against the sky/shadow regression from 135b928 - "
                     "if they settle now, that write was the cause.",
                     static_cast<unsigned long long>(rHeld));
        /* THE TWO ACCOUNTS OF THE PREVIOUS CAMERA, AND HOW FAR APART THEY WERE.
           See reproj_same_eye. A delta near zero means nothing alternates and
           this setting is a no-op; a delta that tracks head speed is the 65 mm
           baseline the upscaler was being told about in one place and not the
           other. "fell back" should be a trickle - if it is most of the frames
           the same eye's pair is aging out and pass three is running on the
           per-frame pair regardless of the setting. */
        const uint64_t eyeAged  = g_statReprojEyeAged.exchange(0, std::memory_order_relaxed);
        const uint64_t eyeDelta = g_reprojEyeDelta.exchange(0, std::memory_order_relaxed);
        BVR_INFO("VP reproj history: %s eye, previous-camera accounts differed by "
                 "%.6f at most (max matrix element), fell back to the per-frame "
                 "pair %llu time(s) in ~5 s. Zero difference means nothing "
                 "alternates and vp_reproj_same_eye changes nothing; a difference "
                 "that grows with head speed is the 65 mm baseline that the "
                 "main-view slot and this matrix used to describe differently.",
                 reproj_same_eye() ? "SAME" : "previous-frame",
                 static_cast<double>(eyeDelta) / 1000000.0,
                 static_cast<unsigned long long>(eyeAged));

        if (form >= 0)
            BVR_INFO("VP reprojection: cb %u offset %u %s as %s - written %llu "
                     "(last drift %.5f, error %.6f), still %llu, mismatched %llu "
                     "(last drift %.5f, error %.6f) in ~5 s.",
                     g_reprojCb.load(std::memory_order_relaxed),
                     g_reprojOffset.load(std::memory_order_relaxed),
                     g_reprojXpose.load(std::memory_order_relaxed) ? "transposed"
                                                                   : "row-vector",
                     kReprojNames[form],
                     static_cast<unsigned long long>(rWrote),
                     g_reprojLastDev.load(std::memory_order_relaxed),
                     g_reprojLastErr.load(std::memory_order_relaxed),
                     static_cast<unsigned long long>(rStill),
                     static_cast<unsigned long long>(rSkip),
                     g_reprojBadDev.load(std::memory_order_relaxed),
                     g_reprojBadErr.load(std::memory_order_relaxed));
        else
            BVR_INFO("VP reprojection: not identified yet (%d/%d consecutive "
                     "agreements, sift band %.2f). Until it is, the sky, sun, "
                     "cloud, hair, particles and screen-space shadows keep "
                     "reprojecting their history with the engine's own camera. "
                     "Identification needs frames where the head moves FAST, "
                     "because only a large drift can tell the four candidate "
                     "forms apart - so if the suspect above peaks near the band, "
                     "the evidence is being sifted out and vp_reproj_band wants "
                     "raising.",
                     g_reprojAgree.load(std::memory_order_relaxed), kReprojAgree,
                     reproj_band());
    }
    else
    {
        BVR_INFO("VP patch: %d view + %d inverse matrices replaced over %d constant "
                 "buffer uploads in ~5 s; no main view frustum measured yet.",
                 vp, inv, scanned);
    }

    /* When nothing is being patched, say WHERE the chain broke rather than only
       that it did. Each of these numbers rules out a different cause, and the
       first run cost a launch precisely because it reported the end of the chain
       and nothing about the middle. */
    if (vp == 0)
    {
        int rehooked = 0;
        for (int i = 0; i < g_watchedCount; ++i)
            rehooked += g_watched[i].rehooks;

        BVR_WARN("VP patch hooks. Self-test since start: %llu ok / %llu broken "
                 "| slot moves re-hooked: %d",
                 static_cast<unsigned long long>(g_selfTestPasses.load(std::memory_order_relaxed)),
                 static_cast<unsigned long long>(g_selfTestFails.load(std::memory_order_relaxed)),
                 rehooked);

        BVR_WARN("VP patch idle. Self-test %s | DrawIndexed %llu | VSSetConstantBuffers "
                 "%llu | CreateBuffer %llu (constant %llu)",
                 g_selfTestRun.load(std::memory_order_relaxed)
                     ? (g_selfTestPassed.load(std::memory_order_relaxed) ? "PASSED" : "FAILED")
                     : "not run",
                 static_cast<unsigned long long>(g_cDraw.load(std::memory_order_relaxed)),
                 static_cast<unsigned long long>(g_cVsSetCb.load(std::memory_order_relaxed)),
                 static_cast<unsigned long long>(g_cCreateBuffer.load(std::memory_order_relaxed)),
                 static_cast<unsigned long long>(g_cCreateCb.load(std::memory_order_relaxed)));

        BVR_WARN("VP patch idle. Map %llu (buffer %llu, constant %llu, recorded %llu) "
                 "| Unmap %llu (matched %llu) | UpdateSubresource %llu (constant %llu) "
                 "| scanned %d, no publication %llu, record table full %llu "
                 "| largest constant buffer seen %u bytes, cap %u.",
                 static_cast<unsigned long long>(g_cMap.load(std::memory_order_relaxed)),
                 static_cast<unsigned long long>(g_cMapBuffer.load(std::memory_order_relaxed)),
                 static_cast<unsigned long long>(g_cMapConstant.load(std::memory_order_relaxed)),
                 static_cast<unsigned long long>(g_cMapRecorded.load(std::memory_order_relaxed)),
                 static_cast<unsigned long long>(g_cUnmap.load(std::memory_order_relaxed)),
                 static_cast<unsigned long long>(g_cUnmapMatched.load(std::memory_order_relaxed)),
                 static_cast<unsigned long long>(g_cUpdate.load(std::memory_order_relaxed)),
                 static_cast<unsigned long long>(g_cUpdateConstant.load(std::memory_order_relaxed)),
                 scanned,
                 static_cast<unsigned long long>(g_cNoPublication.load(std::memory_order_relaxed)),
                 static_cast<unsigned long long>(g_cRecordFull.load(std::memory_order_relaxed)),
                 g_biggestCb.load(std::memory_order_relaxed), g_maxCbBytes);

        /* How to read it:
             Map 0                    - the hooks are on a vtable nothing calls
             Map large, buffer 0      - only textures come through Map
             buffer large, constant 0 - the view constants are not in a constant
                                        buffer (a structured buffer, perhaps)
             constant large, scanned 0- the size cap or the publication is at fault
             scanned large, vp 0      - the matrices are there; the predicate is
                                        wrong, and the VP near miss lines say how */
    }

    /* What the engine CREATES, as opposed to what we see it update. usage 0 is
       DEFAULT (updated by UpdateSubresource or a copy), 2 is DYNAMIC (updated
       only through Map). If DYNAMIC constant buffers exist in quantity and the
       Map count is still zero, the two facts cannot both be true and the
       self-test says which one to believe. */
    if (!g_bufferKindsLogged.load(std::memory_order_relaxed) &&
        g_bufferKindCount.load(std::memory_order_relaxed) > 0)
    {
        g_bufferKindsLogged.store(true, std::memory_order_relaxed);

        const int rows = g_bufferKindCount.load(std::memory_order_relaxed);
        BVR_INFO("Constant buffers CREATED (%d distinct kinds):", rows);
        for (int i = 0; i < rows && i < kBufferKinds; ++i)
        {
            BVR_INFO("    %6u bytes  bind 0x%X  usage %u (%s)  cpu 0x%X",
                     g_bufferKinds[i].bytes, g_bufferKinds[i].bind, g_bufferKinds[i].usage,
                     g_bufferKinds[i].usage == 0 ? "DEFAULT"
                         : g_bufferKinds[i].usage == 1 ? "IMMUTABLE"
                         : g_bufferKinds[i].usage == 2 ? "DYNAMIC" : "STAGING",
                     g_bufferKinds[i].cpuAccess);
        }
    }

    /* Printed once, when there is something to print. This is the shape of what
       the engine actually uploads - the thing that was being guessed at. */
    if (!g_censusLogged.load(std::memory_order_relaxed) &&
        g_censusCount.load(std::memory_order_relaxed) > 0)
    {
        g_censusLogged.store(true, std::memory_order_relaxed);

        const int rows = g_censusCount.load(std::memory_order_relaxed);
        BVR_INFO("Constant buffer census (%d distinct kinds mapped):", rows);
        for (int i = 0; i < rows && i < kCensusRows; ++i)
        {
            BVR_INFO("    %6u bytes  bind 0x%X  usage %u  cpu 0x%X  maptype %u",
                     g_census[i].bytes, g_census[i].bind, g_census[i].usage,
                     g_census[i].cpuAccess, g_census[i].mapType);
        }
    }
}

bool vp_measured_tangents(float* left, float* right, float* up, float* down)
{
    if (!g_measValid.load(std::memory_order_acquire))
        return false;

    if (left)  *left  = g_measTanL.load(std::memory_order_relaxed);
    if (right) *right = g_measTanR.load(std::memory_order_relaxed);
    if (up)    *up    = g_measTanU.load(std::memory_order_relaxed);
    if (down)  *down  = g_measTanD.load(std::memory_order_relaxed);
    return true;
}

bool vp_cur_latch_homography(float out16[16], float outInv16[16])
{
    if (!g_latchHomoCurValid || out16 == nullptr || outInv16 == nullptr)
        return false;
    memcpy(out16, g_latchHomoCur, sizeof(g_latchHomoCur));
    memcpy(outInv16, g_latchHomoCurInv, sizeof(g_latchHomoCurInv));
    return true;
}

bool vp_prev_latch_homography(float out16[16], float outInv16[16])
{
    if (!g_latchHomoPrevValid || out16 == nullptr || outInv16 == nullptr)
        return false;
    memcpy(out16, g_latchHomoPrev, sizeof(g_latchHomoPrev));
    memcpy(outInv16, g_latchHomoPrevInv, sizeof(g_latchHomoPrevInv));
    return true;
}

bool vp_expanding_fov()
{
    return cfg().enabled && cfg().fov && cfg().expand;
}

bool vp_debug_published(VpFrame* frame, VpFrame* other)
{
    Published p;
    if (!read_published(p))
        return false;
    if (frame != nullptr)
        *frame = p.frame;
    if (other != nullptr)
        *other = p.other;
    return true;
}

bool vp_debug_engine_camera(float pos[3], float basis[9])
{
    if (!g_engBasisValid.load(std::memory_order_relaxed))
        return false;
    memcpy(pos, g_engPos, sizeof(float) * 3);
    memcpy(basis, g_engBasis, sizeof(float) * 9);
    return true;
}

bool vp_view_jitter(float* ndcX, float* ndcY)
{
    if (!g_jitterValid.load(std::memory_order_acquire))
        return false;

    if (ndcX) *ndcX = g_jitterX.load(std::memory_order_relaxed);
    if (ndcY) *ndcY = g_jitterY.load(std::memory_order_relaxed);
    return true;
}

} // namespace bvr
