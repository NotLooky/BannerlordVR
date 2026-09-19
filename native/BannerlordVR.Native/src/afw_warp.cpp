#include "afw_warp.h"
#include "bvr_config.h"
#include "bvr_log.h"
#include "d3d_hooks.h"
#include "vp_patch.h"

#include <d3dcompiler.h>

#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>

namespace bvr {

constexpr uint32_t kAfwEyes = 2;
namespace {

/* ---------------------------------------------------------------------------
 * The shader.
 *
 * A FORWARD SCATTER, in two passes. It began as a backward search - iterate
 * x_src <- x_dst + dir*disparity(x_src) six times and take where it lands - on
 * the reasoning that a 63 mm baseline gives disparities small enough to land on
 * the right surface almost everywhere, and that the scatter was the upgrade
 * path if edge artefacts ever dominated.
 *
 * Edge artefacts dominated. The note below the passes has the full account; the
 * short version is that the search's equation has SEVERAL solutions wherever
 * one thing hides another, which is not an edge case but the definition of
 * occlusion, and a search that returns one of them at random draws the same
 * edge two or three times over.
 *
 * So this is the upgrade path, taken. Three things it does that the search
 * could not:
 *
 *   1. OCCLUSION, exactly. Every source pixel claims where it lands and
 *      InterlockedMax keeps the nearest claim. The max IS the depth test.
 *
 *   2. DISOCCLUSIONS filled from the other eye's own previous render, which
 *      actually contains the geometry being revealed. The source eye cannot -
 *      the whole point of a disocclusion is that the source never saw it.
 *
 *   3. A SOFT KNEE instead of a hard disparity clamp, so the near field
 *      compresses smoothly instead of flattening onto one plane.
 * ------------------------------------------------------------------------- */
const char* kWarpShader = R"HLSL(
Texture2D<float4>   SrcColor : register(t0);
Texture2D<float>    SrcDepth : register(t1);

/* Linear, clamped. The warp lands on fractional source positions and the gather
   used to snap them to whole pixels - see the note in the gather. */
SamplerState        LinearClamp : register(s0);

/* The last REAL render of the eye being synthesised, one frame old, already in
   that eye's own screen space. This is what a disocclusion gets filled with -
   see the note in the gather. */
Texture2D<float4>   PrevEye  : register(t2);

RWTexture2D<float4> Dst      : register(u0);

/* One uint per destination pixel: which source pixel claimed it, and how near
   that source pixel was. Written by the scatter pass, read by the gather pass.
   See the note above the two of them. */
RWTexture2D<uint>   Lookup   : register(u1);

cbuffer Params : register(b0)
{
    float2 Size;          // destination width, height in pixels
    float  DispScale;     // focal_px * baseline, so disparity = DispScale / z
    float  Dir;           // +1 when the source is the LEFT eye, -1 when right

    float  ZNear;
    float  ZFar;
    float  Reversed;      // 1 when depth 0 means far
    float  MaxDisparity;  // pixels; clamps the near plane's blow-up

    // Depth pixels per colour pixel. rgl renders the scene at HALF the eye
    // size and upscales into our target, so this is 0.5 and not 1, and
    // ignoring it would halve every disparity - a world at twice the
    // distance, everywhere, which reads as the stereo simply being weak.
    float2 DepthScale;
    float2 DepthLimit;    // last addressable depth texel

    float  GapFill;       // how far a disocclusion may reach for a filler, px
    float  HavePrev;      // 1 when PrevEye holds a real render of this eye
    float  Knee;          // disparity where the soft roll-off starts, px
    float  MaxSpan;       // widest stretch one source pixel may claim, px

    float  DepthEdge;     // disparity jump that counts as a silhouette, px
    float  SpanFrac;      // ...or this fraction of the disparity ABOVE SpanFree
    float  SpanFree;      // below this disparity the fixed threshold stands alone
    float  EdgeHoleMin;   // disparity below which an ambiguous pixel still moves

    // Where the previous-frame fill has to be read from, given that the head
    // turned between that render and this one. See the gather's HavePrev branch.
    float4 PrevRot0;      // rows of the rotation from THIS view into the one the
    float4 PrevRot1;      // previous real render of this eye was drawn from
    float4 PrevRot2;
    float4 PrevCam;       // fx, fy, cx, cy in destination pixels
    float  HavePrevRot;   // 1 when both poses were known
    float  MirrorMax;     // ping-pong period for the disocclusion fill, px; 0 = off
    float  MirrorReach;   // furthest the reflection may reach into the source, px
    float  FillInvert;    // 0 reflect, 1 run the background forward, 2 continue past it

    float  InvertRun;     // length of one forward run before it repeats, px
    float  BlendMax;      // holes this wide or narrower are interpolated, px
    float  ColourPick;    // 1 = let the colour decide which surface an edge pixel is
    float  ColourPad;     // written out because HLSL would insert it anyway: a
                          // float2 may not straddle a 16-byte row, so Jitter
                          // starts the next one and this slot exists whether or
                          // not it is named. Named, the C++ mirror can match it.

    float2 Jitter;        // the upscaler's offset, in DEPTH TEXELS, to be added
                          // to every depth lookup
    float  ColourSoft;    // disparity gap up to which the colour weighs rather
                          // than votes, px
    float  Taa;           // how much of the previous synthesised frame to keep
};

/* THE THRESHOLDS CANNOT BE CONSTANTS, BECAUSE WHAT THEY MEASURE IS NOT.
 *
 * Two places ask the same question - "is this big jump a stretched surface or a
 * different object?" - and both answered it with a fixed 8 px. That works while
 * the disparity is tens of pixels. At ten centimetres it is pinned at the 220 px
 * ceiling, a mildly tilted surface changes by a dozen pixels from one pixel to
 * the next, and both tests start answering "different object" for a surface that
 * is merely close. The scatter then claims one pixel instead of a span, leaving
 * the surface full of cracks, and the depth filter snaps instead of
 * interpolating, putting the staircase back. Every crack is then filled from the
 * previous frame - which at ten centimetres is displaced enormously by the
 * smallest head movement. That is the tripling, and it appears only when very
 * close because that is the only place the fixed threshold is wrong.
 *
 * A genuine silhouette at that range jumps by nearly the WHOLE disparity - 220
 * to about 5 for the background behind it. Magnification of one surface produces
 * a fraction of it. So the threshold scales with the disparity, and the two
 * cases separate again at every distance. */
/* MEASURED FROM SpanFree, NOT FROM ZERO - and that correction matters.
 *
 * Scaling the threshold by a flat quarter of the disparity fixed ten
 * centimetres and quietly broke the middle distance. At a metre the disparity is
 * about 65 px, so a quarter is 16 - and a real silhouette between two surfaces
 * a little way apart jumps by rather less than that. The test therefore called
 * genuine edges "the same surface" and interpolated across them, inventing a
 * half-depth that belongs to neither and flinging those pixels between the two.
 * That is a smear where there should be a clean disocclusion, in exactly the
 * range most of the scene lives in.
 *
 * The fixed threshold was never wrong in the mid field; it was only wrong very
 * close. So the fraction now applies to the disparity ABOVE SpanFree and the
 * fixed value stands alone below it. Under about two thirds of a metre nothing
 * changes from the behaviour that worked, and the scaling appears only where a
 * fixed 8 px genuinely cannot tell magnification from occlusion. */
float SpanLimit(float disparity)
{
    return max(MaxSpan, SpanFrac * max(0.0, abs(disparity) - SpanFree));
}

// Window-space depth back to view-space z, for a standard perspective divide.
// Reversed-Z is a flip of the stored value and nothing more, which is why it is
// a parameter rather than a second shader.
float LinearZ(float d)
{
    d = Reversed > 0.5 ? (1.0 - d) : d;

    // Clamp before the divide. A depth of exactly 1 is the far plane and gives
    // a zero denominator; a depth of exactly 0 is the near plane and gives a
    // disparity that would swing the search across the whole image.
    d = clamp(d, 1e-6, 1.0 - 1e-6);

    return (ZNear * ZFar) / (ZFar - d * (ZFar - ZNear));
}

/* THE CAP IS A SOFT KNEE, NOT A CLAMP.
 *
 * A hard clamp does not just limit the disparity, it DELETES the depth
 * information beyond it. Everything closer than the clamp distance comes out at
 * one disparity, which is a flat plane pasted across the nearest part of the
 * scene - and the nearest part is what the player is looking at. The report was
 * "when getting very close to an object the 3D feel gets less", and with 51.7 px
 * per unit against a 96 px clamp, everything inside 0.54 m was being flattened
 * into a single depth. Exactly there.
 *
 * Below the knee the disparity is untouched, so the mid-field keeps its true
 * geometry. Above it the excess is compressed through 1 - exp(-x), which is
 * smooth, never reaches the ceiling, and above all stays MONOTONIC: nearer is
 * still nearer, all the way in. The stereo compresses as things approach rather
 * than falling off a cliff, and there is no plane where depth stops. */
float SoftCap(float d)
{
    float s = sign(d);
    float a = abs(d);

    if (a <= Knee)
        return d;

    float headroom = max(MaxDisparity - Knee, 1e-3);
    return s * (Knee + headroom * (1.0 - exp(-(a - Knee) / headroom)));
}

)HLSL"
/* Fourth seam, same 16 KB cap as the other three. The depth reader and its
   reasoning about upscalers is what crossed it this time. Adjacent literals
   concatenate, so the shader text is unchanged. */
R"HLSL(
/* THE DEPTH IS HALF THE COLOUR'S RESOLUTION, AND SNAPPING TO IT SHOWS.
 *
 * rgl shades the scene at half the eye target in each axis, so two colour pixels
 * share one depth texel and the disparity field is a STAIRCASE with two-pixel
 * treads. The height of a tread is 2 * dD/dx, and D goes as 1/z - so the tread
 * grows quadratically as something approaches. Far away the steps are a fraction
 * of a pixel and invisible; up close a surface is torn into two-pixel strips
 * each displaced by a different amount, which is doubling that gets stronger the
 * nearer you get and disappears with distance. Exactly the report.
 *
 * Interpolating along the surface removes the staircase. Interpolating ACROSS a
 * silhouette does not - it invents a half-depth that belongs to neither surface
 * and flings those pixels somewhere between the two, which is a smear where
 * there should be a clean disocclusion for the previous render to fill.
 *
 * So the filter is edge-aware: lerp when the two texels agree about what surface
 * they are on, snap to the nearer one when they do not. Two depth fetches
 * instead of one, and the tread disappears from every smooth surface while every
 * genuine edge stays exactly as sharp as the depth buffer can express it.
 */
float DisparityAt(float x, int y, out bool ambiguous)
{
    /* Written before any branch can return: an out parameter left unset on one
       path is a warning the compiler is right to give, and the value it would
       carry is whatever the caller had. */
    ambiguous = false;

    /* Position in DEPTH texels; texel centres sit at i + 0.5.
     *
       Plus the upscaler's jitter, because the two buffers do not agree about
       where anything is. DLSS renders each frame through a projection offset by
       a fraction of a pixel and takes that offset back out of the COLOUR on the
       way through - that is how it accumulates detail. Nothing takes it out of
       the depth. So the depth is a picture of the same scene shifted by up to
       about a pixel, on a sequence that changes every frame, and reading it at
       the colour's coordinates lands a fraction of a pixel off - in a different
       direction each frame.

       In the middle of a surface that is nothing: the depth a fraction of a
       pixel over is the same depth. At a silhouette it is the whole problem.
       The edge in the depth buffer sits a little to one side of the edge in the
       colour, so the pixels right at the boundary are assigned the wrong
       surface's disparity and warp to the wrong place - and since the offset
       changes every frame, they do it differently every frame. A one-pixel
       error that will not sit still is read as crawling, shimmering edges, and
       that is the part of this that survived giving the colour the vote: the
       colour resolves WHICH surface, and this resolves WHERE it is.

       vp_view_jitter measures it off the engine's own projection - see the note
       there. Zero when there is no upscaler running, which makes this line a
       no-op rather than a special case. */
    float dxf = x * DepthScale.x - 0.5 + Jitter.x;
    int   dy  = clamp((int)(y * DepthScale.y + Jitter.y), 0, (int)DepthLimit.y);

    /* A DEPTH THE SAME SIZE AS THE COLOUR HAS NOTHING TO BE AMBIGUOUS ABOUT.
     *
       Everything below - the two-texel read, the edge test, the ambiguous flag -
       exists for ONE case: rgl rendering the scene at half the eye size under an
       upscaler, where a single depth texel covers two colour pixels and genuinely
       cannot say which surface either belongs to. That is a real problem and the
       machinery is the right answer to it.

       At 1:1 it is not merely unnecessary, it is destructive. Every pixel has its
       own exact depth, but the code still reads texels x-1 and x and compares
       them, so at EVERY silhouette the two disagree, the pixel is declared
       ambiguous, and the scatter declines to move it. The result is a hole down
       the side of every object, which the gather fills from another frame - a
       stale band on every edge, which is the transparent edge that appeared and
       was never there when AFW was first working.

       The half-texel offset is wrong here too: with DepthScale 1 the lerp sits at
       t = 0.5, so every pixel's depth is averaged with its LEFT neighbour's.
       Harmless in open surfaces, a smeared silhouette everywhere else.

       So when the depth is full resolution, read the one texel that belongs to
       this pixel and report no ambiguity, because there is none. */
    if (DepthScale.x > 0.99 && DepthScale.y > 0.99)
    {
        int dxi = clamp((int)(x + Jitter.x), 0, (int)DepthLimit.x);
        ambiguous = false;
        return SoftCap(DispScale / max(LinearZ(SrcDepth.Load(int3(dxi, dy, 0))), 1e-4));
    }

    float base = floor(dxf);
    float t    = saturate(dxf - base);

    int d0 = clamp((int)base,     0, (int)DepthLimit.x);
    int d1 = clamp((int)base + 1, 0, (int)DepthLimit.x);

    float za = LinearZ(SrcDepth.Load(int3(d0, dy, 0)));
    float zb = LinearZ(SrcDepth.Load(int3(d1, dy, 0)));

    float da = SoftCap(DispScale / max(za, 1e-4));
    float db = SoftCap(DispScale / max(zb, 1e-4));

    /* Scaled, not fixed - see SpanLimit. At ten centimetres a surface legitimately
       changes by more than a fixed threshold from one depth texel to the next, and
       snapping there is what puts the staircase back exactly where it hurts most. */
    /* Its own floor, so afw_depth_edge_px stays a separate knob, but the same
       above-SpanFree scaling - see SpanLimit for why it is measured from there
       and not from zero. */
    float near = max(abs(da), abs(db));
    float edge = max(DepthEdge, SpanFrac * max(0.0, near - SpanFree));

    if (abs(da - db) > edge)
    {
        /* NOT KNOWABLE FROM THE DEPTH BUFFER. ASK THE COLOUR ONE.
         *
           This whole branch exists because the depth is smaller than the colour,
           and that is only ever true under an upscaler - DLSS at Quality renders
           depth at 0.67 of the eye, so a texel spans a pixel and a half and a
           silhouette falls inside one. Which is also why the aliasing along far
           edges appears WITH DLSS AND NOT WITHOUT IT: at 1:1 the fast path above
           runs, every pixel has its own exact depth, and there is nothing to
           guess. Turn the upscaler on and every silhouette pixel in the frame is
           a guess.

           The guess was positional - whichever texel centre is nearer. That is
           the best answer available from depth alone, and it is wrong about half
           the time at exactly the pixels where being wrong is visible: the pixel
           moves by the other surface's disparity, lands a few pixels off, and
           strings one-pixel errors along every edge. Which is what aliasing IS.

           But the depth is not the only buffer we have, and the COLOUR is full
           resolution - that is the entire point of the upscaler. A silhouette
           pixel looks like the surface it belongs to, so comparing it against
           the colour at each candidate texel's centre answers the question the
           depth cannot. The near surface and the far one look different at a
           silhouette worth seeing; where they do not, nothing that follows from
           the choice is visible either.

           A pixel the upscaler ANTIALIASED is a blend of both, and it lands
           between the two candidates in proportion to the blend - so the
           majority surface wins, which is the right answer for a pixel that is
           mostly one of them.

           Too close to call falls back to declaring it ambiguous, as before: a
           threshold on the difference of the two distances, not on either
           alone, because what matters is whether the colour DISCRIMINATES. */
        if (ColourPick > 0.5)
        {
            const int xi = clamp((int)x, 0, (int)Size.x - 1);
            const int xa = clamp((int)(((float)d0 + 0.5) / DepthScale.x), 0, (int)Size.x - 1);
            const int xb = clamp((int)(((float)d1 + 0.5) / DepthScale.x), 0, (int)Size.x - 1);

            const float3 me = SrcColor.Load(int3(xi, y, 0)).rgb;
            const float3 ca = SrcColor.Load(int3(xa, y, 0)).rgb;
            const float3 cb = SrcColor.Load(int3(xb, y, 0)).rgb;

            const float na = dot(abs(me - ca), float3(1.0, 1.0, 1.0));
            const float nb = dot(abs(me - cb), float3(1.0, 1.0, 1.0));

            /* Three channels summed, so 0.03 is one percent of one channel's
               range - below the noise the upscaler itself leaves behind, and
               far below any silhouette that could be seen. */
            if (abs(na - nb) > 0.03)
            {
                ambiguous = false;

                /* WEIGHED, NOT VOTED, WHERE BETWEEN THEM IS A REAL PLACE TO BE.
                 *
                   A vote is a step function, and the thing casting it MOVES.
                   DLSS resolves a little more detail into every edge pixel each
                   frame - that is what it is for - so a pixel sitting near the
                   middle of the two candidates changes its mind on its own,
                   frame after frame, with nothing in the scene moving. Its
                   disparity then jumps by the whole gap between the surfaces,
                   and a pixel that jumps every few frames is a shimmer. The
                   vote fixed the edges that were consistently wrong and left
                   the ones that were undecided flickering instead.

                   Weighing removes the step. A pixel that looks 70% like the
                   near surface gets 70% of the way to its disparity, so a small
                   change in colour is a small change in position rather than a
                   jump - and the silhouette lands where the coverage says it
                   should, between two texel centres, which is where an
                   antialiased edge actually is.

                   Only where the two disparities are CLOSE. Between them is a
                   real place when the surfaces are a few pixels apart, which is
                   the far field this is for. Across a near silhouette it is not:
                   half of a 200 px gap is a pixel flung into the middle of a
                   hole, belonging to neither surface, which is the rubber-sheet
                   smear the snap exists to prevent. So the vote still decides
                   the near field, and it is the right tool there - a wide gap
                   makes the colours plainly different, and a plain difference
                   does not flicker. */
                if (ColourSoft >= 1.0 && abs(da - db) <= ColourSoft)
                {
                    const float w = saturate(na / max(na + nb, 1e-6));
                    return lerp(da, db, w);
                }

                return (na < nb) ? da : db;
            }
        }

        /* AMBIGUOUS: this colour pixel's depth texel straddles a silhouette, so
           which surface the pixel belongs to is not knowable from this buffer.
           Reported so the scatter can decline to move it - see the note there.
           The value still returned is the nearest texel's, for the callers that
           only want a number. */
        ambiguous = true;
        return t < 0.5 ? da : db;
    }

    ambiguous = false;
    return lerp(da, db, t);
}

/* For callers that do not care which side of an edge they are on. */
float DisparityAt(float x, int y)
{
    bool ignored;
    return DisparityAt(x, y, ignored);
}

)HLSL"
/* MSVC caps a SINGLE string literal at 16 KB, and the shader plus its reasoning
   went past it - C2026, "string too big". Adjacent literals are concatenated by
   the compiler and the limit applies to each one separately, so this is a seam
   in the source and not in the shader: what D3DCompile receives is unchanged. */
R"HLSL(
/* ---------------------------------------------------------------------------
 * WHY THIS IS TWO PASSES AND NOT A SEARCH
 *
 * The first version gathered: for each DESTINATION pixel it solved
 * sx = dst + Dir*D(sx) by iterating six times and took wherever it landed.
 *
 * That equation does not have one answer. At the edge of a near object several
 * source pixels satisfy it - the object's own edge at a large disparity, and
 * the background behind it at a small one - and having several answers is not a
 * flaw in the scene, it IS what occlusion means. The iteration walks into
 * whichever branch its starting point leads to, that branch changes from one
 * destination pixel to the next along the edge, and the edge therefore comes
 * out drawn at two or three places at once. Distant geometry has a disparity
 * near zero, so there is only ever one branch and it converges on the first
 * step - which is exactly why far objects looked right while anything close
 * tripled at its edges.
 *
 * No iteration count fixes that. A gather cannot pick the nearest of several
 * surfaces because it never sees the others.
 *
 * So the direction is reversed. Each SOURCE pixel computes where it lands and
 * claims that destination, and competing claims are resolved with
 * InterlockedMax on a key whose high bits are the disparity. Of two surfaces
 * landing on one pixel the larger disparity is the nearer, and the nearer is
 * the one that occludes - so the max IS the occlusion test: exact,
 * order-independent, no sorting and no search window. One atomic per pixel
 * replaces six dependent texture reads, so it is cheaper than what it replaces.
 * ------------------------------------------------------------------------ */

/* The source x rides in the low bits and is never compared for its own sake;
   the disparity sits above it so the ordering is by depth alone. Zero means
   "nothing landed here", which is why the disparity is stored one higher than
   it is - a genuinely zero disparity (the far plane) must still beat empty. */
#define AFW_SX_BITS 13
#define AFW_SX_MASK ((1u << AFW_SX_BITS) - 1u)

uint PackCandidate(float disp, int sx)
{
    uint q = (uint)clamp(disp * 16.0 + 0.5, 0.0, 131000.0);
    return ((q + 1u) << AFW_SX_BITS) | ((uint)sx & AFW_SX_MASK);
}

int   UnpackSx(uint v)   { return (int)(v & AFW_SX_MASK); }
float UnpackDisp(uint v) { return (float)((v >> AFW_SX_BITS) - 1u) * (1.0 / 16.0); }

[numthreads(8, 8, 1)]
void scatter(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= (uint)Size.x || tid.y >= (uint)Size.y)
        return;

    int   sx = (int)tid.x;
    int   y  = (int)tid.y;

    bool  ambiguous;
    float d0 = DisparityAt(sx, y, ambiguous);

    /* THE HALO, AND WHY THE ANSWER IS TO MOVE NOTHING.
     *
       The colour is full resolution and the depth is half, so along every
       silhouette there is a band of colour pixels whose depth texel straddles
       the edge. Which surface those pixels belong to is not knowable from this
       buffer - and whichever way the texel resolves, some of them are BACKGROUND
       pixels carrying the FOREGROUND's disparity. Those fly with the object,
       land detached from where they belong, and trace its outline in the wrong
       colour. That is the halo.
     *
       Both available answers are wrong for half the band, so guessing harder
       cannot help. What can: refuse to move a pixel whose depth is ambiguous.
       It then has no claim, the gather treats that destination as a
       disocclusion, and the fill comes from the previous REAL render of the eye
       being synthesised - which saw those pixels properly, from the right place,
       and needs no depth to be right about them.
     *
       So the halo is replaced by a band that is one frame old. Standing still -
       when halos get noticed - the hybrid is already showing real eyes and the
       band is exact. Moving, it is a frame stale at a silhouette, which is the
       quietest artifact on offer here.
     *
       Only where it matters. Below a few pixels of disparity the whole band
       moves by less than a pixel, so declining to move it would open holes for
       nothing. */
    if (ambiguous && abs(d0) > EdgeHoleMin)
        return;

    // The forward map, in the SAME convention the old search used: it solved
    // sx = dst + Dir*D(sx), so a source pixel lands at sx - Dir*D(sx). Keeping
    // that identical is not cosmetic - the calibration measured both the gain
    // and the direction against this convention, against the two real eyes, and
    // flipping it here would quietly invalidate that measurement.
    float fx0 = (float)sx - Dir * d0;
    uint  key = PackCandidate(d0, sx);

    /* THE SPAN IS MEASURED, NOT ASSUMED TO BE TWO PIXELS.
     *
       It was a fixed two, which is enough only while the disparity gradient is
       gentle. Up close it is not: disparity goes as 1/z, so across a near
       surface it changes fast, adjacent source pixels land three or four apart,
       and the destination pixels between them get no writer. Those cracks are
       then filled as if they were disocclusions - from the previous frame's
       render, which is displaced by whatever the head did in 11 ms. A lattice
       of one-pixel holes each showing a slightly shifted copy is read as a
       ghost, and that is the triple image that appears under a metre and
       nowhere else.
     *
       So the span is where the NEXT source pixel lands. Everything between the
       two belongs to this surface, and claiming all of it is the correct
       rasterisation of the segment between two samples. */
    float d1  = DisparityAt(sx + 1, y);
    float fx1 = (float)(sx + 1) - Dir * d1;

    float lo = min(fx0, fx1);
    float hi = max(fx0, fx1);

    /* Wider than MaxSpan is not a stretched surface, it is a depth
       DISCONTINUITY - the neighbouring pixel belongs to something else
       entirely, and painting across the gap would smear this surface over
       whatever stands behind it. That gap is a disocclusion, and the gather
       fills it with geometry that actually saw it. */
    float limit = SpanLimit(d0);

    if (hi - lo > limit)
    {
        lo = fx0;
        hi = fx0 + 1.0;
    }

    int a = (int)floor(lo);
    int b = (int)floor(hi);

    // Bounded regardless of what the config says, so the loop cannot run away.
    // The cost is the ACTUAL span, not the limit: this only caps it.
    b = min(b, a + (int)limit);

    for (int dx = a; dx <= b; ++dx)
    {
        if (dx >= 0 && dx < (int)Size.x)
            InterlockedMax(Lookup[int2(dx, y)], key);
    }
}

/* WHERE A CLAIM REALLY CAME FROM, TO A FRACTION OF A PIXEL.
 *
   The claim packs an integer source column, but the forward map that produced
   it is fractional: sx = dst + Dir*D. The filled part of the image has always
   reconstructed that - see the gather's first branch - and the disocclusion
   fill has always ignored it and stamped whole source pixels with Load.

   That difference is invisible on a wide hole, where the fill is invented
   anyway. It is exactly what is visible on a NARROW one. A distant object's
   hole is a pixel or three across, so the fill is a thin ribbon following the
   silhouette - and a ribbon of unfiltered, half-a-pixel-displaced texture laid
   against a filtered image is read as the edge being pixelated and aliased,
   which is the one place in the frame where a stair-step shows most.

   Held to within a pixel of the claim for the same reason the gather holds its
   own: magnified surfaces stamp one claim across many destinations, and the
   reconstruction must refine that claim, not wander off it. */
float SubPixelSource(int nx, int claimedSx, float disp)
{
    const float f = (float)nx + Dir * disp;
    return clamp(f, (float)claimedSx - 1.0, (float)claimedSx + 1.0);
}

/* WHERE THIS PIXEL WAS IN THE PREVIOUS SYNTHESISED FRAME.
 *
   The previous frame was drawn from where the head was pointing then, so
   reading it at the same coordinate assumes the head has not turned since. It
   always has. At this focal length half a degree of turn is about thirteen
   pixels, and thirteen pixels of misregistration is not a stabiliser, it is a
   smear. Rotation is depth-free - every ray turns by the same amount whatever
   it hits - so it costs one matrix and needs nothing this shader lacks.

   Factored out because two callers now want it: the disocclusion fill, which
   reads real geometry into a hole, and the temporal blend, which reads the same
   pixel to hold it still. */
float2 PrevPixel(int2 dst)
{
    float2 srcPx = float2(dst) + 0.5;

    if (HavePrevRot > 0.5)
    {
        const float3 ray = float3( (srcPx.x - PrevCam.z) / PrevCam.x,
                                  -(srcPx.y - PrevCam.w) / PrevCam.y,
                                  -1.0);

        const float3 r = float3(dot(PrevRot0.xyz, ray),
                                dot(PrevRot1.xyz, ray),
                                dot(PrevRot2.xyz, ray));

        /* Behind the eye after the turn: nothing to read, keep the straight
           lookup rather than projecting through the origin. */
        if (r.z < -1e-6)
        {
            srcPx = float2(PrevCam.z + PrevCam.x * (r.x / -r.z),
                           PrevCam.w - PrevCam.y * (r.y / -r.z));
        }
    }

    return srcPx;
}

/* One filtered fetch at a fractional column, clamped to the image. The fill's
   equivalent of the claimed branch's SampleLevel, so the two agree. */
float4 FetchSource(float sxf, int y)
{
    const float x = clamp(sxf, 0.0, Size.x - 1.0);

    return SrcColor.SampleLevel(
        LinearClamp,
        float2((x + 0.5) / Size.x, ((float)y + 0.5) / Size.y),
        0.0);
}

[numthreads(8, 8, 1)]
)HLSL"
/* Second seam, same reason as the first: MSVC caps each string literal at
   16 KB and the scatter half plus its reasoning had grown past it again.
   Adjacent literals concatenate, so the shader text is unchanged. */
R"HLSL(
void gather(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= (uint)Size.x || tid.y >= (uint)Size.y)
        return;

    int2 dst = int2(tid.xy);
    uint v   = Lookup[dst];

    // Seeded, not merely declared. The disocclusion branch below can return
    // early, and fxc is right to refuse to prove every path assigns this.
    int   sx  = dst.x;
    float sxf = (float)dst.x;

    if (v != 0u)
    {
        /* SUB-PIXEL, NOT SNAPPED TO THE CLAIMING PIXEL.
         *
           The scatter records which INTEGER source pixel won this destination,
           and this used to sample that pixel with Load. That is
           nearest-neighbour resampling of a warp whose scale factor is never
           exactly one - so along any surface some source columns get sampled
           twice and others get skipped. A duplicated column IS a doubled edge,
           one pixel wide, everywhere, and the skipped ones are what read as
           softness. Together: "blurry, or very slightly doubled, not clear".
         *
           The true source position is fractional and we already know it to a
           sixteenth of a pixel, because the claim packs the disparity: the
           forward map is dst = sx - Dir*D, so sx = dst + Dir*D. Sampling there
           with a linear filter reconstructs the surface instead of quantising
           it, and it costs one filtered fetch in place of one unfiltered one.
         *
           It also fixes the span claim for free. A magnified surface stamps one
           key across several destination pixels, and each of those now resolves
           to a slightly different source position - so the stretch interpolates
           smoothly rather than repeating the same pixel in a block. */
        float sxf = (float)dst.x + Dir * UnpackDisp(v);

        /* HELD TO THE PIXEL THAT ACTUALLY CLAIMED THIS DESTINATION.
         *
           The line above reconstructs the source position by assuming the local
           mapping is one-to-one: advance the destination by a pixel and the
           source advances by a pixel. That holds wherever the surface is not
           being magnified, which is everywhere except close up.
         *
           Up close it is badly wrong. A magnified surface stretches ONE source
           pixel across a span of destinations - eight of them at ten
           centimetres - and every one of those carries the same claim, so every
           one of them should resolve back to within a pixel of the same source.
           Instead sxf swept across eight source pixels, dragging in whatever
           geometry happened to sit beside the one that was actually visible.
           That is the breaking when getting very close, and it appeared nowhere
           else because magnification is one everywhere else.
         *
           So the refinement stays a refinement. The claim names the correct
           source pixel; this may adjust within it, and may not wander to a
           different one. The sub-pixel gain against nearest-neighbour is kept
           and the runaway is gone. */
        float claimed = (float)UnpackSx(v);
        sxf = clamp(sxf, claimed - 1.0, claimed + 1.0);

        float4 colour = SrcColor.SampleLevel(
            LinearClamp,
            float2((sxf + 0.5) / Size.x, ((float)dst.y + 0.5) / Size.y),
            0.0);

        /* THE HISTORY THE WARPED EYE NEVER HAD.
         *
           Measured, across both modes: dynamic shadows are stable in AFR WITH
           DLSS and crawl in every other combination. Cascaded shadow maps
           re-fit to the camera every frame and their edges crawl; what hides it
           in the real eye is temporal accumulation, and DLSS is doing that
           accumulation. The synthesised eye is not a render, so DLSS never sees
           it and it accumulates nothing - it is rebuilt from scratch every
           frame, complete with whatever the depth buffer decided about
           silhouettes THIS frame. On a shadow edge that decision changes from
           frame to frame, one eye is steady and the other is not, and the eyes
           disagreeing about where an edge is reads as swimming.
         *
           So the warp gets its own accumulation. Not a copy of DLSS - it has no
           motion vectors and no jitter sequence to resolve - just the one
           property that matters here: a pixel that should not have changed is
           held rather than recomputed.
         *
           THE CLAMP IS WHAT MAKES IT SAFE. Blending the previous frame in
           unconditionally is how a temporal filter becomes a ghost. The
           previous value is first confined to the range the SOURCE holds around
           this pixel's own source position - four taps, left, right, above and
           below. Where the pixel genuinely changed, that range has moved and the
           clamp discards the history; where it did not, the history survives and
           the crawl with it. The neighbourhood comes from the source rather
           than the destination because the warp is a resample of the source,
           so the source is what says which values are plausible here.
         *
           Only in this branch. The disocclusion fill below is invented content
           and holding it steady would preserve an invention rather than a
           measurement. afw_taa = 0 turns it off. */
        if (Taa > 0.0 && HavePrev > 0.5)
        {
            const float2 pp = PrevPixel(dst);
            const int2   ppi = int2(clamp(pp.x, 0.0, Size.x - 1.0),
                                    clamp(pp.y, 0.0, Size.y - 1.0));

            float4 prev = PrevEye.Load(int3(ppi, 0));

            const float4 l = FetchSource(sxf - 1.0, dst.y);
            const float4 r = FetchSource(sxf + 1.0, dst.y);
            const float4 u = FetchSource(sxf, max(dst.y - 1, 0));
            const float4 d = FetchSource(sxf, min(dst.y + 1, (int)Size.y - 1));

            const float4 lo = min(colour, min(min(l, r), min(u, d)));
            const float4 hi = max(colour, max(max(l, r), max(u, d)));

            prev = clamp(prev, lo, hi);
            colour = lerp(colour, prev, saturate(Taa));
        }

        Dst[dst] = colour;
        return;
    }
    else if (HavePrev > 0.5)
    {
        /* A DISOCCLUSION, FILLED WITH SOMETHING THAT ACTUALLY SAW IT.
         *
           Whatever covered this pixel has moved aside and revealed background
           the SOURCE eye never rendered - so no amount of searching the source
           can produce it. It is not in there. Stretching the nearest background
           pixel sideways across the hole is a guess, and behind a near object
           the hole is as wide as the disparity step across its edge - up to the
           whole cap. A 96 px horizontal smear is the comb of stripes down the
           edge of a near column.
         *
           But this eye WAS rendered, one frame ago, from its own position - and
           that render is in this eye's own screen space, so the missing pixel is
           simply the one at the same coordinate. No search, no warp, no guess:
           real geometry, one frame stale.
         *
           A frame of staleness in the holes is a trade AFR already makes for
           entire eyes, and it is bounded by exactly the same 11 ms. It shows up
           as a slight lag at a silhouette while turning, which is a far quieter
           artifact than a smear that is wrong even standing still.
         *
           One thing has to be undone first. That render was drawn from where the
           head was pointing a frame ago, and reading it at the SAME coordinate
           assumes the head has not turned since. It always has. At this focal
           length half a degree of turn moves the whole image about thirteen
           pixels, so the real geometry gets pasted thirteen pixels from where it
           belongs - a correct edge in the wrong place, which is a doubled edge.
           That is the doubling that showed up on right-hand edges without the
           head going anywhere, because idle micro-motion is already that big.

           Rotation is depth-free: every ray turns by the same amount whatever it
           hits, so it is a homography and needs nothing the shader does not have.
           Translation is not corrected - it would need the depth of the very
           background the near edge is hiding, which is the one thing the depth
           buffer cannot contain, and over one frame it is far smaller. */
        /* One implementation, shared with the temporal blend above. */
        const float2 srcPx = PrevPixel(dst);

        const int2 prevPx = int2(clamp(srcPx.x, 0.0, Size.x - 1.0),
                                 clamp(srcPx.y, 0.0, Size.y - 1.0));

        Dst[dst] = PrevEye.Load(int3(prevPx, 0));
        return;
    }
    else
    {
        /* No previous render yet - the first AFW frame of a mission, before any
           real render of this eye has been recorded. Fall back to stretching the
           background: the FARTHEST filled neighbour, never the nearest. The gap
           is opened BY the near object, so filling it from the near object
           paints a second copy of that object's edge across the hole. */
        float bestDisp = 1e30;
        int   bestSx   = dst.x;

        /* EACH SIDE IS SEARCHED INDEPENDENTLY, AND THAT IS THE WHOLE FIX.
         *
           This used to stop at the first distance k that had a neighbour on
           EITHER side. The intent was to pay only for the width of the actual
           hole, and the comment above it claimed the farthest neighbour always
           won - but it cannot win a comparison it is never entered into.

           A disocclusion is opened BY a near object: the object moves aside and
           uncovers what was behind it. So the two sides of the hole are never
           alike. One side is the object's own edge, a few pixels away. The other
           is the background, the full width of the hole away. Stopping at the
           first k that finds anything therefore found the OBJECT almost every
           time, and painted its edge across the gap.

           That is a second copy of the silhouette, stretched sideways, on one
           side of every object - reported exactly as "doubling of stretched
           edge on the right side". Right is where it belongs: warping the left
           eye into the right shifts content left, so the hole opens on an
           object's right, and the object's edge sits immediately to its left.

           Searching the sides separately lets both candidates exist before
           either is chosen, so the smaller disparity - the background, which is
           what was actually revealed - wins on merit. A side that runs off the
           image counts as exhausted rather than as unfinished, or the loop
           would run the full window every time near a border. */
        /* Which side won, as a source-space offset. See the mirror note at the
           end of this branch. */
        int  bestDelta = 0;

        bool foundL = false;
        bool foundR = false;

        /* How far each side's neighbour turned out to be, kept rather than
           discarded: the two together are the WIDTH of this hole, which is what
           a fill that must not repeat itself needs to know. Zero means that
           side ran off the image without finding anything. */
        int  kL = 0;
        int  kR = 0;

        /* And what each side found, not just which of them won. A hole narrow
           enough to interpolate across needs BOTH ends of the interpolation. */
        int   sxL   = 0;
        int   sxR   = 0;
        float dispL = 0.0;
        float dispR = 0.0;

        for (int k = 1; k <= (int)GapFill && !(foundL && foundR); ++k)
        {
            if (!foundL)
            {
                int nxL = dst.x - k;
                if (nxL < 0)
                {
                    foundL = true;      // off the edge: nothing more to find
                }
                else
                {
                    uint nv = Lookup[int2(nxL, dst.y)];
                    if (nv != 0u)
                    {
                        foundL = true;
                        kL     = k;
                        float nd = UnpackDisp(nv);
                        dispL = nd; sxL = UnpackSx(nv);
                        if (nd < bestDisp) { bestDisp = nd; bestSx = UnpackSx(nv); bestDelta = -k; }
                    }
                }
            }

            if (!foundR)
            {
                int nxR = dst.x + k;
                if (nxR >= (int)Size.x)
                {
                    foundR = true;
                }
                else
                {
                    uint nv = Lookup[int2(nxR, dst.y)];
                    if (nv != 0u)
                    {
                        foundR = true;
                        kR     = k;
                        float nd = UnpackDisp(nv);
                        dispR = nd; sxR = UnpackSx(nv);
                        if (nd < bestDisp) { bestDisp = nd; bestSx = UnpackSx(nv); bestDelta = k; }
                    }
                }
            }
        }

)HLSL"
/* Third seam, same 16 KB cap as the other two, this time reached by the
   disocclusion fill's own reasoning. Adjacent literals concatenate, so the
   shader text is unchanged - and the split sits between two comments so no
   statement is ever cut in half by it. */
R"HLSL(
        /* A NARROW HOLE IS A CRACK, AND A CRACK IS INTERPOLATED, NOT INVENTED.
         *
           Everything below this reasons about a hole as a DISOCCLUSION - a
           band the near object uncovered, whose contents are genuinely absent
           from the source eye and must be invented. That is what a hole beside
           a near object is. It is not what a hole beside a DISTANT one is.

           Disparity goes as 1/z, so at twenty metres the step across a
           silhouette is two or three pixels and the hole it opens is the same.
           Nothing is meaningfully hidden behind a two-pixel gap: whatever
           belongs there is within a pixel or two of what stands on either side
           of it, and the two sides are usually the same surface seen around a
           thin edge. Inventing there is not just unnecessary, it is worse than
           the alternative - a stamped copy of one side, phase-shifted and
           unfiltered, laid in a ribbon along every distant silhouette. That
           ribbon is what reads as the far edges being pixelated and aliased.

           So a hole up to BlendMax wide with real geometry on BOTH sides is
           closed by interpolating between them, weighted by where in the gap
           this pixel sits, both ends reconstructed sub-pixel. That is what the
           rasteriser would have produced across a gap that size, and it is an
           antialiased edge rather than a stair-stepped one.

           Both sides must exist: with only one, the width is unknown and this
           may be the near margin of something far wider. BlendMax 0 turns it
           off and sends every hole down the invented path again. */
        if (BlendMax >= 1.0 && kL > 0 && kR > 0 && (kL + kR - 1) <= (int)BlendMax)
        {
            const float lf = SubPixelSource(dst.x - kL, sxL, dispL);
            const float rf = SubPixelSource(dst.x + kR, sxR, dispR);
            const float t  = (float)kL / (float)(kL + kR);

            Dst[dst] = lerp(FetchSource(lf, dst.y), FetchSource(rf, dst.y), t);
            return;
        }

        /* MIRRORED, NOT REPEATED.

           bestSx alone is ONE source column, and writing it into every pixel
           of the hole paints that column across the whole width. Against a
           near object the hole is as wide as its disparity - the horse sits
           at about 0.4 m, so the band beside it is around 180 px - and a
           single column of sand stretched 180 px reads as exactly what it is:
           a vertical smear with no texture in it.

           The pixels that belong in the hole are genuinely absent. They are
           what the object hides in the SOURCE eye, so no offset into the
           source can find them - sampling where they ought to be lands on the
           object itself, which is the doubled silhouette this branch has
           already been fixed once for producing.

           What is available is the background on the visible side of the
           hole, and reflecting it inwards is the oldest trick for this. Our
           pixel sits bestDelta from the neighbour that won, so it takes the
           source column bestDelta BEYOND that neighbour: the texture folds
           back on itself, continuously at the seam because a reflection has
           no discontinuity there, and every pixel of the hole gets a
           different real sample instead of the same one.

           It is still invented. On sand, grass or stone - which is most of
           what a near object occludes on a battlefield - an invented texture
           of the right kind disappears, where a smear never does. */
        /* ONE ORIENTATION ALL THE WAY ACROSS, NOT A FOLD THAT ALTERNATES IT.
         *
           A straight reflection is continuous at the seam and invents plausible
           texture, but against a wide hole it paints a single large MIRROR
           IMAGE of the background - and a mirror image is recognisable as one.
           Reported as "the fill is inverted, what is filled is inverted", which
           was exactly right: it is inverted, by construction.

           Folding it every MirrorMax pixels cut that run short, and it also
           turned the texture back around. Each fold has two halves: on the way
           out the source column walks AWAY from the seam, so the background
           runs backwards, and on the way back it walks toward it, so the
           background runs forwards. A hole wide enough to reach the turn
           therefore shows both - reported as the fill splitting in two, one
           half inverted and the other not. Neither half is more correct than
           the other; both are invented. What is visible is the CREASE BETWEEN
           THEM, because nothing in a real image changes handedness across a
           vertical line, and the eye finds that instantly even when it cannot
           say what the texture is.

           So the fold is off by default and the reflection runs the full width
           of the hole in ONE direction. Uniform by construction: whatever the
           hole's width, the fill has a single orientation and a single seam -
           the one at the object's edge, where a reflection is continuous
           anyway.

           What the fold was defending against is real, though: a 1:1
           reflection across a 180 px hole is a legible flipped copy of the
           ground. MirrorReach answers that WITHOUT a change of handedness. The
           offset is compressed toward a ceiling instead of being folded back -
           exponentially, so the first tens of pixels are still an exact 1:1
           reflection with all their texture, and the far side of a wide hole
           squashes smoothly into a soft stretch of distant background. A
           compressed reflection is not recognisable as a copy of anything, and
           a smear only offends where it is sharp texture that ought to be
           moving; this is the deep end of a hole behind a near object, which is
           the one place in the image where nothing is meant to be read.

           afw_fill_mirror_px above 0 restores the fold, for comparison.
           afw_fill_mirror_reach_px 0 restores the uncompressed reflection. */
        int delta = bestDelta;

        if (MirrorMax >= 1.0)
        {
            const int period = 2 * (int)MirrorMax;
            int       fold   = abs(bestDelta) % period;

            if (fold > (int)MirrorMax)
                fold = period - fold;

            delta = (bestDelta < 0) ? -fold : fold;
        }
        else if (MirrorReach >= 1.0)
        {
            /* k px into the hole reaches R*(1 - e^(-k/R)) px into the source:
               slope 1 at the seam, never past R however wide the hole gets. */
            const float k = (float)abs(bestDelta);
            const int   m = (int)(MirrorReach * (1.0 - exp(-k / MirrorReach)) + 0.5);

            delta = (bestDelta < 0) ? -m : m;
        }

        /* INVERTED: THE BACKGROUND RUNS FORWARD THROUGH THE HOLE.
         *
           A reflection is the safe fill - continuous at the seam, and it cannot
           reach anything but background - but it is still a reflection, and the
           handedness is what gives it away against anything with structure in
           it: a wall, a fence, a rank of shields. Running the background the
           right way round instead trades that for a step, and which of the two
           is less visible is a question about the scene, not about the maths.
           So it is a switch, not a rewrite.

           1 - FORWARD, OUT OF THE BACKGROUND. The band of background just
               outside the hole is laid across it in its own order. Every
               sample is still background - the run walks TOWARD the neighbour
               and stops there, never past it - so the occluder cannot get in.

               The run is the WIDTH OF THIS HOLE by default, which is the whole
               point of measuring kL and kR above. A fixed run shorter than the
               hole has to repeat to cover it, and a repeat is a seam, and a
               row of seams down the side of an object is read as the fill
               being cut into pieces - which is what a fixed 24 px run was
               reported as. One run the width of the hole cannot repeat: there
               is exactly one copy, one orientation, and one discontinuity, at
               the background seam, where it is a change of PHASE in the same
               texture rather than a change of handedness or a cut.

               InvertRun above 0 pins the run to a fixed length instead, which
               is only worth it to see the difference.

           2 - FORWARD, STRAIGHT ON PAST THE NEIGHBOUR. The literal inversion of
               the reflection: same distance, opposite direction, so it is
               continuous at the seam and never repeats. It is also the one
               thing this branch has been fixed twice for doing - the source
               columns past the neighbour are the OCCLUDER, because that is
               what the hole was cut out from behind, so this paints a stretched
               copy of the object's own edge across the gap. Kept because it is
               the direct comparison, and because a hole against a flat wall may
               well prefer it. Expect the doubled silhouette anywhere else. */
        if (FillInvert >= 1.5)
        {
            delta = -delta;
        }
        else if (FillInvert >= 0.5)
        {
            int run = (int)InvertRun;

            if (run < 1)
            {
                /* Both sides found: the hole is everything between them, and
                   this pixel is inside it. One side only - the other ran off
                   the image - leaves no width to measure, so fall back to the
                   reflection's reach, which is the same judgement about how far
                   into the background it is reasonable to go. */
                run = (kL > 0 && kR > 0) ? (kL + kR - 1) : (int)MirrorReach;
                run = max(run, 1);
            }

            const int off = run - 1 - ((abs(bestDelta) - 1) % run);

            delta = (bestDelta < 0) ? -off : off;
        }

        sx = bestSx + delta;

        /* And the same reconstruction the rest of the image gets. The offset
           above is a whole number of pixels because it is a count of them, but
           the column it counts FROM is not: the neighbour's true source sits
           wherever its own disparity puts it, a fraction of a pixel off the
           column its claim names. Adding the offset to the fraction rather than
           to the integer keeps the fill in register with the image it borders,
           and one filtered fetch resamples it instead of stamping it.

           Nothing found on either side leaves nothing to be in register with,
           so that case keeps the pixel straight ahead. */
        sxf = (bestDisp < 1e29)
                  ? SubPixelSource(dst.x + bestDelta, bestSx, bestDisp) + (float)delta
                  : (float)sx;
    }

    Dst[dst] = FetchSource(sxf, dst.y);
}
)HLSL";

struct Params
{
    float size[2];
    float dispScale;
    float dir;
    float zNear;
    float zFar;
    float reversed;
    float maxDisparity;
    float depthScale[2];
    float depthLimit[2];
    float gapFill;
    float havePrev;
    float knee;
    float maxSpan;
    float depthEdge;
    float spanFrac;
    float spanFree;
    float edgeHoleMin;

    /* Mirrors the tail of the cbuffer above, float for float. */
    float prevRot0[4];
    float prevRot1[4];
    float prevRot2[4];
    float prevCam[4];
    float havePrevRot;
    float mirrorMax;
    float mirrorReach;
    float fillInvert;
    float invertRun;
    float blendMax;
    float colourPick;
    float colourPad;
    float jitter[2];
    float colourSoft;
    float taa;
};

/* A CONSTANT BUFFER IS ALLOCATED IN WHOLE 16-BYTE ROWS, AND D3D REFUSES ANY
 * OTHER SIZE OUTRIGHT.
 *
 * This struct has been extended once per fix for several fixes now, and the one
 * that added two floats to a full row took it to 188 bytes. CreateBuffer failed,
 * AFW switched itself off for the session, and the only trace was one ERROR line
 * scrolling past in a log that prints thousands. Something that can be checked
 * at compile time should never be discovered by reading a log.
 *
 * Failing here also catches half of the OTHER way this goes wrong. The two
 * declarations are packed by different rules - HLSL will not let a float2
 * straddle a row and C++ will - so a field added to one and not padded in the
 * other silently shifts every field after it, and the shader reads a knob's
 * value out of a different knob. Matching sizes does not prove the layouts
 * agree, but a mismatch here is proof that they do not. */
static_assert(sizeof(AfwParams) % 16 == 0,
              "AfwParams must be a whole number of 16-byte constant-buffer rows "
              "- add a pad float, and mirror it in the HLSL cbuffer above.");

AfwParams        g_params = {};
std::atomic<bool> g_haveParams{ false };

ID3D11ComputeShader*       g_shader = nullptr;      /* scatter */
ID3D11ComputeShader*       g_gatherShader = nullptr;
ID3D11Buffer*              g_cb = nullptr;
ID3D11ShaderResourceView*  g_srcSrv = nullptr;
ID3D11ShaderResourceView*  g_depthSrv = nullptr;   /* the one in use; owned by g_depthSrvSlot */
/* rgl ping-pongs between two scene depths, so a single cached view was rebuilt
   every frame (TEST 175). One view per buffer; g_depthSrv points at the one
   bound this frame. */
ID3D11ShaderResourceView*  g_depthSrvSlot[2]    = { nullptr, nullptr };
ID3D11Texture2D*           g_depthSrvSlotFor[2] = { nullptr, nullptr };
int                        g_depthSrvNext       = 0;
ID3D11ShaderResourceView*  g_prevSrv = nullptr;   /* last real render of the synthesised eye */
ID3D11Texture2D*           g_prevSrvFor = nullptr;
ID3D11UnorderedAccessView* g_dstUav = nullptr;

/* Linear + clamp, for the sub-pixel source fetch in the gather. Clamp rather
   than wrap: a warp that runs off the left edge must repeat the edge pixel, not
   fetch the far side of the image. */
ID3D11SamplerState*        g_sampler = nullptr;

/* The claim buffer the two passes talk through: one uint per destination pixel,
   R32_UINT because InterlockedMax needs an integer UAV. Allocated once per
   destination size and reused, since it is cleared wholesale every frame. */
ID3D11Texture2D*           g_lookup = nullptr;
ID3D11UnorderedAccessView* g_lookupUav = nullptr;
uint32_t                   g_lookupW = 0;
uint32_t                   g_lookupH = 0;

ID3D11Texture2D* g_srvFor = nullptr;     /* which textures the views above are for */
ID3D11Texture2D* g_depthSrvFor = nullptr;

/* The depth texture whose contents have already been checked for near
   geometry, so the readback is paid once per texture and not once a frame. */
ID3D11Texture2D* g_depthChecked = nullptr;

/* The search generation that pointer was proved under.
 *
   The pointer alone is not enough, because what was proved is a fact about the
   buffer's CONTENTS - that a scene had been drawn into it - and contents
   change while an address does not. rgl hands back the same depth texture
   after a mission restart with everything cleared out of it, so the pointer
   still matched and the proof was still believed, and the warp ran on frame
   one of the new mission against an empty buffer. Restarting a mission showed
   a broken eye even though the session's first mission was fine.

   The generation moves whenever the depth search is re-opened, which a scene
   teardown does, so the proof has to be earned again per mission. */
uint32_t g_depthCheckedGen = 0;

/* The shape the proof was earned against. See the gate in afw_warp_eye: the
   depth POINTER alternates between the two eyes' buffers, so it cannot be the
   identity, but a change of size or format is a genuinely different target. */
uint32_t    g_depthCheckedW   = 0;
uint32_t    g_depthCheckedH   = 0;
DXGI_FORMAT g_depthCheckedFmt = DXGI_FORMAT_UNKNOWN;

/* Whether that texture has yet shown it contains a drawn scene. True until
   proved, so the warp never runs against a buffer that has not answered. */
bool g_depthUnproven   = true;
int  g_depthWait       = 0;   /* frames to skip before measuring again */
int  g_depthEmptyTries = 0;   /* consecutive unmeasurable attempts */
int  g_depthFarTries   = 0;   /* consecutive attempts with nothing near YET */
ID3D11Texture2D* g_uavFor = nullptr;

bool g_shaderFailed = false;
bool g_loggedFirst = false;
bool g_loggedPrevFill = false;

uint32_t g_warps = 0;
uint32_t g_failures = 0;


/* ---------------------------------------------------------------------------
 * Which way round is the depth? Measured, once, instead of guessed a third time.
 *
 * "Everything has a double image even without moving" is what a globally wrong
 * disparity looks like, and the arithmetic says the convention is the cause.
 * Read a reversed-Z buffer as though it were conventional and the sky - stored
 * at 0.0 - linearises to the NEAR PLANE, 0.05 m, which asks for a disparity of
 * some thousands of pixels. Everything then clamps at afw_max_disparity_px and
 * the whole image is displaced by a constant 96 px. Uniform, static, doubled:
 * exactly the report.
 *
 * So this reads the buffer back ONCE and decides from the numbers. The test is
 * content-independent, which the obvious sky-is-far heuristic is not: linearise
 * a grid of samples under BOTH conventions and count how many land at a
 * plausible distance for a battlefield. The wrong convention piles almost
 * everything against the near plane; the right one spreads it across metres to
 * kilometres. There is no scene where that comparison is ambiguous.
 *
 * The cost is a GPU sync inside the Present hook, which this repo has recorded
 * as a hazard - but once, at a known moment, is a very different thing from
 * every frame, and it buys the end of a guess that has now cost two rounds.
 * ------------------------------------------------------------------------- */
float sample_depth(const uint8_t* row, uint32_t x, DXGI_FORMAT format)
{
    switch (format)
    {
    case DXGI_FORMAT_R16_TYPELESS:
    case DXGI_FORMAT_D16_UNORM:
        return reinterpret_cast<const uint16_t*>(row)[x] / 65535.0f;

    case DXGI_FORMAT_R32_TYPELESS:
    case DXGI_FORMAT_D32_FLOAT:
        return reinterpret_cast<const float*>(row)[x];

    case DXGI_FORMAT_R24G8_TYPELESS:
    case DXGI_FORMAT_D24_UNORM_S8_UINT:
        return (reinterpret_cast<const uint32_t*>(row)[x] & 0x00FFFFFFu) / 16777215.0f;

    case DXGI_FORMAT_R32G8X24_TYPELESS:
    case DXGI_FORMAT_D32_FLOAT_S8X24_UINT:
        /* 8 bytes per texel: float depth, then stencil and padding. */
        return reinterpret_cast<const float*>(row + x * 8u)[0];

    default:
        return -1.0f;
    }
}

float linearise(float d, bool reversed, float zNear, float zFar)
{
    if (reversed)
        d = 1.0f - d;

    if (d < 1e-6f) d = 1e-6f;
    if (d > 1.0f - 1e-6f) d = 1.0f - 1e-6f;

    return (zNear * zFar) / (zFar - d * (zFar - zNear));
}

int g_probed = 1;   /* 1 until measured: see reversed_depth. */
/* The depth texture the convention probe was last run against.

   IT USED TO BE A PLAIN "HAVE WE PROBED" FLAG, AND THAT WAS A BUG THE MOMENT
   THE SEARCH LEARNED TO CHANGE ITS MIND.

   The probe decides whether the buffer is reversed-Z, which inverts the
   meaning of every value in it. Running it once per session was fine while
   the first depth found was the only one ever used. Now a candidate can be
   rejected and another adopted - and here that swap goes from a 32-bit float
   buffer to a 24-bit UNORM one, which are exactly the two formats whose
   conventions differ most: reversed-Z needs float precision, so a D24_UNORM
   buffer is conventional far more often than not.

   Carrying the first buffer's verdict onto the second meant reading the whole
   range backwards - the sky at arm's length and the horse at infinity - which
   is why the warp came out twice as bad as not warping at all rather than
   merely no better. Keyed on the texture, the verdict now belongs to the
   buffer it was measured from. */
ID3D11Texture2D* g_probedFor = nullptr;
uint32_t         g_probedGen = 0;

/* ...and the SHAPE it was measured from, which is the real key now (TEST 171).
 *
   rgl ping-pongs between two scene depths of identical size and format, so the
   pointer alternates every frame and a pointer key missed on every call: the
   log showed "guard MISSES (probes)" on each of them, cached pointer always the
   other buffer. Every AFW frame paid a 37 MB staging allocation, a copy and a
   CPU map - a hard pipeline sync, which is what took AFW from 90 fps to ~70.
   The acceptance check below was moved to shape + generation for exactly this
   reason; the probe was not.
 *
   A format change (the D32 -> D24 swap the note above is about) still changes
   the shape, and a rejected same-shape decoy bumps the generation, so both
   cases that made the verdict per-buffer still force a fresh probe. */
uint32_t         g_probedW   = 0;
uint32_t         g_probedH   = 0;
DXGI_FORMAT      g_probedFmt = DXGI_FORMAT_UNKNOWN;

/* Frames to let pass before probing again after an inconclusive read.
 *
   Declining un-remembers the texture so the probe will look again, and with
   nothing to space the attempts that meant a full-resolution staging copy and
   a CPU map EVERY FRAME for as long as the buffer stayed empty - 815 of them
   in fifteen seconds in the log that found this. A map is a hard pipeline
   sync, so the cost lands as stutter on the one eye the player is being shown
   a real render of. Waiting between attempts costs a few frames of latency on
   a decision that only has to be made once. */
int  g_probeWait = 0;
bool g_loggedEmptyProbe = false;

/* The distance to the NEAREST thing in a depth buffer, in world units, or -1
 * if it could not be read.
 *
 * WHAT THIS IS FOR: TELLING THE SCENE DEPTH FROM ITS NEIGHBOURS.
 *
 * rgl binds SEVERAL eye-sized depth targets per frame - this machine logs at
 * least two of them, one R32G8X24 and one R24G8 - and until now the choice
 * between them was made on size alone. They are all the same size, so that
 * choice was really "whichever was bound first", which is not a criterion at
 * all.
 *
 * The one that was being picked contains nothing nearer than about eight
 * metres. The player is sitting on a horse whose neck fills the bottom of the
 * frame at half a metre and holding a sword at one; neither is in it. So every
 * near object - the exact ones that need the most disparity - was being warped
 * by the depth of the terrain behind it, which is why the warp could never
 * beat leaving the eye alone, and why it was the weapon and the horse that
 * tripled.
 *
 * Nearest-geometry is a criterion that actually separates them. A real scene
 * depth in a first-person view always contains something within arm's reach,
 * because the player's own body is always there. A prepass, a terrain-only
 * pass or a shadow view does not. */
float depth_nearest_metres(ID3D11Device* device, ID3D11DeviceContext* context,
                           ID3D11Texture2D* depth, float zNear, float zFar,
                           bool reversed)
{
    if (device == nullptr || context == nullptr || depth == nullptr)
        return -1.0f;

    D3D11_TEXTURE2D_DESC desc = {};
    depth->GetDesc(&desc);

    D3D11_TEXTURE2D_DESC staging = desc;
    staging.Usage = D3D11_USAGE_STAGING;
    staging.BindFlags = 0;
    staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    staging.MiscFlags = 0;

    ID3D11Texture2D* copy = nullptr;
    if (FAILED(device->CreateTexture2D(&staging, nullptr, &copy)) || copy == nullptr)
        return -1.0f;

    context->CopyResource(copy, depth);

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    if (FAILED(context->Map(copy, 0, D3D11_MAP_READ, 0, &mapped)))
    {
        copy->Release();
        return -1.0f;
    }

    float nearest = 1e30f;
    const uint32_t stepX = desc.Width / 64u + 1u;
    const uint32_t stepY = desc.Height / 64u + 1u;

    for (uint32_t y = 0; y < desc.Height; y += stepY)
    {
        const uint8_t* row =
            static_cast<const uint8_t*>(mapped.pData) + y * mapped.RowPitch;

        for (uint32_t x = 0; x < desc.Width; x += stepX)
        {
            const float raw = sample_depth(row, x, desc.Format);
            if (raw < 0.0f)
                continue;

            /* An empty buffer reads as its cleared value everywhere, and those
               samples say nothing about what the pass contains. BOTH extremes
               are skipped, not just the far one for the convention we think is
               in play, because the convention is exactly what is not yet known
               here - this measurement is what the search uses to decide which
               buffer is even the scene's.

               Skipping only the far end let an empty buffer be accepted as a
               scene. rgl clears some of its depth targets to 1.0, which is the
               CONVENTIONAL far value; read as reversed-Z that is the NEAR
               plane, so a uniformly-cleared buffer reported "nearest geometry
               0.05 m" and passed the "a first-person view always has the
               player's body close by" test with the most convincing number it
               could have produced. The warp then ran with maximum disparity on
               every pixel, which piles the whole scatter into one clamped strip
               and leaves the gather to mirror the image out of the few columns
               that survived.

               Nothing is lost by skipping the near end too. Under either
               convention that extreme is the near plane, 5 cm here, and real
               geometry is not drawn at the near plane - it is clipped there.
               The first mission's genuine scene depth spans 0.00003..0.31, far
               from both extremes. */
            if (raw <= 1e-7f || raw >= 1.0f - 1e-7f)
                continue;

            const float z = linearise(raw, reversed, zNear, zFar);
            if (z > 0.0f && z < nearest)
                nearest = z;
        }
    }

    context->Unmap(copy, 0);
    copy->Release();

    return nearest < 1e29f ? nearest : -1.0f;
}

void probe_depth(ID3D11Device* device, ID3D11DeviceContext* context,
                 ID3D11Texture2D* depth, float zNear, float zFar)
{
    if (device == nullptr || context == nullptr || depth == nullptr)
        return;

    /* Once per TEXTURE, not once per session - see g_probedFor. Set before
       anything below can fail, so a buffer that cannot be read is not probed
       again every frame.

       Keyed on the search generation as well, for the same reason the
       acceptance test is: this verdict is read out of the buffer's contents,
       and rgl hands the same texture back after a mission restart with those
       contents cleared. A verdict reached from one mission's pixels should not
       be inherited by the next mission's. */
    const uint32_t probeGen = depth_generation();

    D3D11_TEXTURE2D_DESC keyDesc = {};
    depth->GetDesc(&keyDesc);

    /* g_probedFor is only "a verdict exists" now; which buffer it names does not
       matter, since the two ping-pong buffers are one scene. See g_probedW. */
    const bool guardHits = g_probedFor != nullptr &&
                           g_probedGen == probeGen &&
                           keyDesc.Width  == g_probedW &&
                           keyDesc.Height == g_probedH &&
                           keyDesc.Format == g_probedFmt;

    /* TEMPORARY INSTRUMENTATION - remove once the cache key is settled.
     *
     * A 73-second session logged 5153 probes, roughly one per frame, every one
     * returning the same reversed-Z verdict. So the guard below never matches.
     * The generation is already ruled out - no depth rejection was logged all
     * session, and only depth_reject_current and the scene reset bump it - which
     * leaves the texture pointer. If rgl cycles a pool of eye-sized depth
     * targets, g_probedFor never equals the one in hand and every frame pays a
     * full staging copy and CPU readback.
     *
     * This records the distinct pointers and how often each recurs: a small
     * repeating set is a pool and the fix is to key on format and size instead;
     * a pointer that is new every single time says something else is wrong. */
    {
        static ID3D11Texture2D* seen[16]   = {};
        static uint32_t         hits[16]   = {};
        static int              seenCount  = 0;
        static int              lines      = 0;
        static uint32_t         calls      = 0;
        static bool             warnedFull = false;

        ++calls;

        int slot = -1;
        for (int i = 0; i < seenCount; ++i)
            if (seen[i] == depth) { slot = i; break; }

        const bool isNew = (slot < 0);
        if (isNew && seenCount < 16)
        {
            slot = seenCount++;
            seen[slot] = depth;
        }
        if (slot >= 0)
            ++hits[slot];

        if (isNew && seenCount >= 16 && !warnedFull)
        {
            warnedFull = true;
            BVR_INFO("AFW probe key: over 16 DISTINCT depth textures seen in %u "
                     "call(s). The pointer is not a stable key at all.", calls);
        }
        else if ((isNew && slot >= 0) || lines < 24)
        {
            ++lines;
            D3D11_TEXTURE2D_DESC pd = {};
            depth->GetDesc(&pd);
            BVR_INFO("AFW probe key: call %u, texture %p %s (slot %d, %u hit(s)), "
                     "cached %p, gen %u vs %u, %ux%u fmt %u -> guard %s",
                     calls, (void*)depth, isNew ? "NEW" : "repeat", slot,
                     slot >= 0 ? hits[slot] : 0u, (void*)g_probedFor,
                     probeGen, g_probedGen, pd.Width, pd.Height,
                     (unsigned)pd.Format,
                     guardHits ? "HITS (skips)" : "MISSES (probes)");
        }
    }

    if (guardHits)
        return;

    /* An earlier attempt on this buffer was inconclusive; let a few frames pass
       rather than paying a full readback again immediately. See g_probeWait. */
    if (g_probeWait > 0)
    {
        --g_probeWait;
        return;
    }

    g_probedFor = depth;
    g_probedGen = probeGen;
    g_probedW   = keyDesc.Width;
    g_probedH   = keyDesc.Height;
    g_probedFmt = keyDesc.Format;

    D3D11_TEXTURE2D_DESC desc = keyDesc;

    D3D11_TEXTURE2D_DESC staging = desc;
    staging.Usage = D3D11_USAGE_STAGING;
    staging.BindFlags = 0;
    staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    staging.MiscFlags = 0;

    ID3D11Texture2D* copy = nullptr;
    if (FAILED(device->CreateTexture2D(&staging, nullptr, &copy)) || copy == nullptr)
    {
        BVR_WARN("AFW: could not stage the depth buffer to read it back; the depth "
                 "convention stays at whatever afw_depth_reversed says.");
        return;
    }

    context->CopyResource(copy, depth);

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    if (FAILED(context->Map(copy, 0, D3D11_MAP_READ, 0, &mapped)))
    {
        copy->Release();
        BVR_WARN("AFW: could not map the staged depth buffer.");
        return;
    }

    int plausibleNormal = 0;
    int plausibleReversed = 0;
    int samples = 0;
    float minRaw = 1.0f, maxRaw = 0.0f;
    float sumNormal = 0.0f, sumReversed = 0.0f;

    const uint32_t stepX = desc.Width / 48u + 1u;
    const uint32_t stepY = desc.Height / 48u + 1u;

    for (uint32_t y = 0; y < desc.Height; y += stepY)
    {
        const uint8_t* row = static_cast<const uint8_t*>(mapped.pData) + y * mapped.RowPitch;

        for (uint32_t x = 0; x < desc.Width; x += stepX)
        {
            const float raw = sample_depth(row, x, desc.Format);
            if (raw < 0.0f)
                continue;

            ++samples;
            if (raw < minRaw) minRaw = raw;
            if (raw > maxRaw) maxRaw = raw;

            const float zn = linearise(raw, false, zNear, zFar);
            const float zr = linearise(raw, true, zNear, zFar);

            sumNormal += zn;
            sumReversed += zr;

            /* A battlefield is metres to kilometres. Anything pinned against the
               near plane is the signature of reading the buffer backwards. */
            if (zn > 0.5f && zn < 5000.0f) ++plausibleNormal;
            if (zr > 0.5f && zr < 5000.0f) ++plausibleReversed;
        }
    }

    context->Unmap(copy, 0);
    copy->Release();

    if (samples == 0)
    {
        BVR_WARN("AFW: depth format %d could not be read back; leaving the "
                 "convention as configured.", static_cast<int>(desc.Format));
        return;
    }

    /* NO EVIDENCE IS NOT EVIDENCE FOR CONVENTIONAL Z.

       This line used to run unconditionally, so a buffer in which NEITHER
       reading was plausible still produced a verdict - and since a tie goes
       to the else branch, that verdict was always "conventional".

       It happened. A candidate was probed while it still held its cleared
       value everywhere:

         raw 1.000000..1.000000. Plausible: 0%% as normal, 0%% as REVERSED.
         -> conventional Z.

       Zero percent either way, and it picked one. The warp then went live
       reading every depth backwards until a later probe happened to correct
       it. A uniform buffer says only that nothing has been drawn into it yet,
       which is a reason to look again rather than to decide.

       Declining also has to un-remember the texture, or the guard at the top
       would take this one probe as the last word on it for ever. */
    if (plausibleNormal == 0 && plausibleReversed == 0)
    {
        /* The staging copy was already unmapped and released above; releasing
           it here again was a double free on every empty probe. */
        g_probedFor = nullptr;
        g_probeWait = 15;

        /* Logged once per spell of emptiness rather than once per attempt. The
           unthrottled version buried a fifteen-second mission under 815 copies
           of this line, which hid the far more interesting fact that the
           acceptance test had meanwhile swallowed the very same buffer. */
        if (!g_loggedEmptyProbe)
        {
            g_loggedEmptyProbe = true;
            BVR_INFO("AFW depth probe: %d samples and every one of them reads as "
                     "impossible under BOTH conventions (raw %.6f..%.6f). That is an "
                     "empty buffer, not a convention - nothing has been drawn into "
                     "it yet. Keeping the previous answer and probing again later. "
                     "Further empty probes on this buffer are not logged.",
                     samples, minRaw, maxRaw);
        }
        return;
    }

    g_loggedEmptyProbe = false;

    g_probed = (plausibleReversed > plausibleNormal) ? 1 : 0;

    BVR_INFO("AFW depth probe: %d samples, raw %.6f..%.6f. Plausible distances: "
             "%d%% read as normal (mean %.1f m), %d%% read as REVERSED (mean %.1f m). "
             "-> %s.",
             samples, minRaw, maxRaw,
             plausibleNormal * 100 / samples, sumNormal / samples,
             plausibleReversed * 100 / samples, sumReversed / samples,
             g_probed != 0 ? "reversed-Z" : "conventional Z");
}

bool reversed_depth()
{
    /* -1 (the default) means "ask the probe". 0 and 1 force it, for anyone who
       wants to see the other answer without arguing with the measurement. */
    static int configured = -2;
    if (configured == -2)
    {
        const float v = config_float("afw_depth_reversed", -1.0f);
        configured = (v < -0.5f) ? -1 : (v > 0.5f ? 1 : 0);
    }

    if (configured >= 0)
        return configured != 0;

    /* Whatever the probe last measured, for the buffer actually in use. It is
       re-measured whenever the search adopts a different depth target, because
       the convention is a property of the buffer and not of the session: a
       32-bit float buffer is usually reversed and a 24-bit UNORM one usually
       is not, and this search can move between the two. */
    return g_probed != 0;
}

/* THE PHYSICAL CEILING: the disparity of something sitting exactly on the near
 * plane. Nothing real can exceed it, because nothing nearer is drawn at all.
 *
 * WHY A CONSTANT CEILING PRODUCED A SECOND COPY OF EVERY CLOSE OBJECT
 *
 * Reported as "close objects have visible tripling, very close objects break
 * and give strong tripling". Tripling is what a doubled synthesised eye looks
 * like once the brain fuses it with a correct one, so the question is where the
 * second copy in one eye comes from - and it is this.
 *
 * At 220 px the ceiling bites at about 0.38 m and the knee below it at 0.45 m.
 * An object at 0.1 m has a true disparity of ~840 px and was moved 220. But the
 * band it uncovers behind itself is as wide as its TRUE disparity, and that
 * band is filled from prevReal - the destination eye's own last real render,
 * which shows the object where it really is. So the eye ends up holding the
 * warped copy at +220 px and the real one at +840 px, six hundred pixels apart.
 * Two copies, from two sources that disagree, and the disagreement is exactly
 * the amount the cap removed.
 *
 * Uncapping makes them agree instead. The warp puts the object where prevReal
 * already has it, the two sources reinforce rather than compete, and the seam
 * between them stops being visible.
 *
 * WHAT THE REFERENCE IMPLEMENTATIONS DO
 *
 * Neither has a disparity ceiling at all. PureDark's frame-warp interface
 * (dependencies/pd-afwmod/include/PDAFWPlugin.h, as driven by REFramework and
 * by the Witcher 3 mod's bridge) takes full camera matrices - srcClipToView,
 * destViewToClip and the rest - and reprojects each pixel through them. A
 * matrix reprojection has nowhere to put a ceiling: displacement falls out of
 * the geometry. The only thresholds either of them exposes are about MOTION
 * (IgnoreMotionThreshold, in pixels), never about disparity.
 *
 * That is the right division and this file already has both halves of it. How
 * far a pixel MOVES is geometry and must not be clamped. How far apart two
 * NEIGHBOURING pixels land is a sampling question, and SpanLimit is what
 * answers it - the cap was the wrong tool doing SpanLimit's job badly.
 *
 * The soft knee is kept because it still earns its place: it holds the last
 * fraction before the near plane, where a single bad depth sample would
 * otherwise fling a pixel across the screen. It just no longer sits in front of
 * anything a player looks at. */
float physical_max_disparity_px()
{
    const float zNear = afw_z_near();
    const float focal = afw_focal_px();
    const float base  = afw_baseline();

    if (!(zNear > 0.0f) || !(focal > 0.0f) || !(base > 0.0f))
        return 0.0f;

    return focal * base / zNear;
}

float max_disparity_px()
{
    /* A cap in pixels. Without it, geometry at the near plane produces a
       disparity of thousands of pixels and the search wanders the whole
       scanline looking for it. Nothing genuinely visible in a battle sits close
       enough to exceed this, and the things that do - the player's own weapon
       swinging through the near plane - are exactly where an uncapped search
       would smear the most. */
    /* Not cached: the ceiling is now derived from the near plane, the focal
       length and the baseline, and the engine moves its near plane between
       scenes while the world-scale slider moves the baseline under the player's
       hands. A value latched on the first mission would be the wrong ceiling
       for every one after it. */
    const float configured = config_float("afw_max_disparity_px", -1.0f);
    if (configured > 0.0f)
        return configured;

    const float physical = physical_max_disparity_px();
    if (physical > 0.0f)
        return physical;

    /* Only before the geometry has been published - the first frames of a
       session. Generous on purpose: a ceiling that bites is the failure this
       whole function exists to avoid. */
    /* The history worth keeping: this was 96, which bit at 0.65 m and put the
       knee at 1.1 m, so everything from arm's length inward had its disparity
       reduced. Reduced disparity reads as greater distance while the angular
       size on screen is untouched, which is the size-distance paradox and was
       the whole of "the closer it gets the bigger it looks". Raising it to 220
       moved the problem to 0.38 m rather than solving it. */
    return 2048.0f;
}


/* ---------------------------------------------------------------------------
 * CALIBRATION: measuring the disparity against the two REAL eyes.
 *
 * WHY DERIVING IT KEEPS NOT BEING ENOUGH
 *
 * disparity = focal_px * baseline / z has four ways to be wrong and only one
 * symptom. The focal length, the baseline and the sign all check out on paper -
 * eye 1 sits at XR +X, which is the camera's right, so content shifts left
 * between the eyes and the search direction is what it is. What cannot be
 * checked on paper is the NEAR PLANE. The linearisation is roughly z ~ n/(1-d),
 * so z scales with n: if rgl is rendering with a near plane other than the 0.05
 * we hand its camera - and it only takes the engine resetting it once a frame
 * while leaving the fov alone, which the fov hold would never notice - then
 * every distance is out by that ratio and every disparity with it.
 *
 * "Reducing world scale makes it less visible" is the shape of that: world
 * scale is a multiplier on the baseline, so it is a multiplier on the applied
 * disparity, so it scales the ERROR too. The error is a factor, not an offset.
 *
 * WHAT IS MEASURED INSTEAD
 *
 * AFR renders both eyes for real, on consecutive frames. That is ground truth
 * sitting right there. So before the warp is allowed to run, two real eyes are
 * captured, and then eye 0 is warped to eye 1 on the CPU under a grid of
 * candidate signs and gains and each result is compared against the REAL eye 1.
 * Whichever candidate matches the real thing best is correct, by definition,
 * and it settles the sign, the near plane, the focal length and the baseline
 * together without needing any of them to be individually right.
 *
 * It runs once, on a band of rows through the middle, and it costs one hitch of
 * a few tens of milliseconds at the start of the first mission.
 * ------------------------------------------------------------------------- */

struct Staged
{
    ID3D11Texture2D*         tex = nullptr;
    D3D11_TEXTURE2D_DESC     desc = {};
    D3D11_MAPPED_SUBRESOURCE map = {};
};

bool stage_and_map(ID3D11Device* device, ID3D11DeviceContext* context,
                   ID3D11Texture2D* source, Staged& out)
{
    source->GetDesc(&out.desc);

    D3D11_TEXTURE2D_DESC staging = out.desc;
    staging.Usage = D3D11_USAGE_STAGING;
    staging.BindFlags = 0;
    staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    staging.MiscFlags = 0;

    if (FAILED(device->CreateTexture2D(&staging, nullptr, &out.tex)) || out.tex == nullptr)
        return false;

    context->CopyResource(out.tex, source);

    if (FAILED(context->Map(out.tex, 0, D3D11_MAP_READ, 0, &out.map)))
    {
        out.tex->Release();
        out.tex = nullptr;
        return false;
    }

    return true;
}

void unstage(ID3D11DeviceContext* context, Staged& s)
{
    if (s.tex == nullptr)
        return;

    context->Unmap(s.tex, 0);
    s.tex->Release();
    s.tex = nullptr;
}

/* Green channel as a luminance stand-in. It carries most of the luma in any
   RGB layout and needs no branch on whether the texture is BGRA or RGBA - the
   green byte sits in the middle either way. */
inline int luma_at(const Staged& s, int x, int y)
{
    const uint8_t* row = static_cast<const uint8_t*>(s.map.pData) + y * s.map.RowPitch;
    return row[x * 4 + 1];
}

/* Sum of absolute differences between the real destination eye and eye 0 warped
   into it with this sign and gain. Lower is a better model of reality. */
/* HOW FAR APART ARE THE TWO REAL EYES, ACCORDING TO THE PIXELS ALONE?
 *
   The gain ladder cannot answer this, because every rung of it goes through the
   depth buffer: "no gain beat a flat copy" means either the depth is wrong for
   this eye OR the two eyes are not a baseline apart, and those need different
   fixes. So measure the thing directly, with no depth in the loop at all - slide
   one eye horizontally across the other and find the offset that matches best.

   Whatever the scene, a genuine 65 mm separation puts the best global offset
   somewhere in the tens of pixels, and matching there is clearly better than
   matching at zero. If instead the best offset IS zero, the two renders are of
   the same viewpoint - the engine never moved its camera between them - and no
   amount of work on the warp can matter, because the input it is being asked to
   reproduce does not contain the parallax it is supposed to explain.

   One global offset is a compromise across a scene with depth in it. That is
   fine: the question here is "zero or not zero", not the exact profile. */
bool measure_raw_disparity(const Staged& src, const Staged& dst,
                           const Staged& depth, const Params& base)
{
    const int W = static_cast<int>(base.size[0]);
    const int H = static_cast<int>(base.size[1]);
    const int maxShift = 320;
    const int block = 64;

    if (W < 8 * block || H < 4 * block)
        return false;

    char line[900];
    int  used = 0;
    int  reported = 0;
    int  agree = 0;
    int  zeroed = 0;

    line[0] = '\0';

    /* THE BLOCKS ARE HUNTED FOR, NOT LAID OUT ON A GRID.
     *
       A fixed grid samples wherever it happens to land, and the last run landed
       entirely on terrain 200 m away - where the predicted parallax is under a
       pixel, so measuring zero says nothing at all. Only NEAR geometry carries a
       shift big enough to have a sign, and where the near geometry sits on
       screen depends on where the player is looking, which is not something this
       can assume.

       So scan the whole frame and keep the blocks the DEPTH says are close. The
       depth buffer is right about distance whatever else is wrong - the far
       blocks measuring exactly zero last time confirms that much - so it is a
       reliable way to find the pixels worth measuring, even while the disparity
       SIGN derived from it is the thing under test. */
    for (int by = block; by + block < H && reported < 8; by += block)
    {
        for (int bx = 0; bx + block < W && reported < 8; bx += block)
        {
            {
                const uint8_t* prow = static_cast<const uint8_t*>(depth.map.pData) +
                    static_cast<int>((by + block / 2) * base.depthScale[1]) * depth.map.RowPitch;
                int pdx = static_cast<int>((bx + block / 2) * base.depthScale[0]);
                if (pdx < 0) pdx = 0;
                if (pdx > static_cast<int>(base.depthLimit[0])) pdx = static_cast<int>(base.depthLimit[0]);

                const float praw = sample_depth(prow, static_cast<uint32_t>(pdx), depth.desc.Format);
                const float pz   = linearise(praw, reversed_depth(), base.zNear, base.zFar);
                const float pd   = base.dispScale / (pz > 1e-4f ? pz : 1e-4f);

                /* Too far to carry a measurable shift, or so close the true
                   shift would run past the search window and clamp - which is
                   what made the last magnitude unreadable. */
                if (pd < 25.0f || pd > static_cast<float>(maxShift) * 0.85f)
                    continue;
            }

            /* A flat block matches equally well at every shift, so its argmin is
               noise that would read as a confident zero. Only blocks with real
               texture in them can answer this. */
            double mean = 0.0;
            int    n    = 0;
            for (int y = by; y < by + block; y += 4)
                for (int x = bx; x < bx + block; x += 4)
                { mean += luma_at(dst, x, y); ++n; }
            if (n == 0) continue;
            mean /= n;

            double dev = 0.0;
            for (int y = by; y < by + block; y += 4)
                for (int x = bx; x < bx + block; x += 4)
                    dev += std::abs(luma_at(dst, x, y) - mean);
            dev /= n;

            if (dev < 6.0)          /* featureless: sky, fog, flat ground */
                continue;

            double bestCost  = 1e30;
            int    bestShift = 0;

            for (int s = -maxShift; s <= maxShift; ++s)
            {
                double cost = 0.0;
                int    m    = 0;

                for (int y = by; y < by + block; y += 4)
                    for (int x = bx; x < bx + block; x += 4)
                    {
                        /* Bounded per SAMPLE rather than by insetting the grid.
                           Insetting by the search range would have excluded the
                           only blocks that carry near geometry - the player's own
                           body at the edge of frame - which are the ones with a
                           parallax large enough to measure at all. */
                        const int sx = x + s;
                        if (sx < 0 || sx >= W)
                            continue;

                        cost += std::abs(luma_at(src, sx, y) - luma_at(dst, x, y));
                        ++m;
                    }

                if (m == 0)
                    continue;

                cost /= m;   /* per-sample, so shifts with fewer valid samples
                                are not flattered by a smaller total */

                if (cost < bestCost) { bestCost = cost; bestShift = s; }
            }

            /* What the depth says the shift at this block ought to be. */
            const uint8_t* drow = static_cast<const uint8_t*>(depth.map.pData) +
                static_cast<int>((by + block / 2) * base.depthScale[1]) * depth.map.RowPitch;
            int dx = static_cast<int>((bx + block / 2) * base.depthScale[0]);
            if (dx < 0) dx = 0;
            if (dx > static_cast<int>(base.depthLimit[0])) dx = static_cast<int>(base.depthLimit[0]);

            const float raw = sample_depth(drow, static_cast<uint32_t>(dx), depth.desc.Format);
            const float z   = linearise(raw, reversed_depth(), base.zNear, base.zFar);
            const float pred = base.dispScale / (z > 1e-4f ? z : 1e-4f);

            ++used;
            if (bestShift == 0) ++zeroed;
            if (std::abs(static_cast<float>(bestShift)) > 0.5f * pred &&
                std::abs(static_cast<float>(bestShift)) < 2.0f * pred) ++agree;

            const int wrote = _snprintf_s(line + std::strlen(line), sizeof(line) - std::strlen(line),
                                          _TRUNCATE, "[%d,%d z=%.1fm meas %+d pred %+.0f] ",
                                          bx, by, z, bestShift, pred);
            (void)wrote;
            ++reported;
        }
    }

    BVR_INFO("AFW eye parallax, measured per block against what the depth "
             "predicts: %d textured block(s), %d measured a shift of exactly "
             "zero, %d agreed with the prediction to within a factor of two. %s"
             "-- if every block reads zero while the predictions do not, the two "
             "eyes were rendered from ONE viewpoint and there is no parallax for "
             "the warp to reproduce. If the measurements track the predictions, "
             "the camera is moving and the depth model is sound.",
             used, zeroed, agree, line);

    /* Only a run that actually found near geometry has answered anything. Two
       blocks minimum, so one ambiguous match cannot carry the verdict on its
       own - and the caller keeps sampling until it gets them rather than
       latching a run that happened to be looking at the sky. */
    return used >= 2;
}

/* DID THE WARP ACTUALLY LAND WHERE IT SHOULD HAVE, FOR THIS DIRECTION?
 *
   The geometry is now measured correct - camera, depth, focal, baseline and sign
   all agree with the pixels to within a couple of px - so whatever is left is
   not the model. It is whether the SYNTHESIS carries it out faithfully, and that
   can differ between the two directions because only one of them is ever
   exercised while the source eye is pinned.
 *
   afwSynth[E] holds the frame in which E was SYNTHESISED. One frame later the
   eyes swap and E is RENDERED, so at that moment there is a prediction and a
   truth for the same eye, and the question "how far out was it" has a number.
 *
   The number to want is the residual SHIFT, not the raw difference. A faithful
   warp leaves the predicted eye aligned with the real one, so every near block
   should match best at zero offset. A direction that is systematically wrong
   leaves a residual, and its size and sign say by how much and which way - which
   a plain difference score cannot, because it conflates "displaced" with
   "different".
 *
   Contaminated by one frame of time, unavoidably: the two images are from
   consecutive frames. That contamination is the same for both directions, so
   comparing eye 0's residual against eye 1's stays meaningful even though
   neither is clean on its own. */
void measure_synth_residual(const Staged& pred, const Staged& real,
                            const Staged& depth, const Params& base, int eye)
{
    const int W = static_cast<int>(base.size[0]);
    const int H = static_cast<int>(base.size[1]);
    const int maxShift = 160;
    const int block = 64;

    if (W < 8 * block || H < 4 * block)
        return;

    char line[700];
    line[0] = '\0';

    int    used = 0;
    int    reported = 0;
    double residualSum = 0.0;

    for (int by = block; by + block < H && reported < 6; by += block)
    {
        for (int bx = 0; bx + block < W && reported < 6; bx += block)
        {
            /* Near blocks only: this is asking whether the warp MOVED things
               correctly, and a block the warp barely moves cannot answer. */
            const uint8_t* prow = static_cast<const uint8_t*>(depth.map.pData) +
                static_cast<int>((by + block / 2) * base.depthScale[1]) * depth.map.RowPitch;
            int pdx = static_cast<int>((bx + block / 2) * base.depthScale[0]);
            if (pdx < 0) pdx = 0;
            if (pdx > static_cast<int>(base.depthLimit[0])) pdx = static_cast<int>(base.depthLimit[0]);

            const float praw = sample_depth(prow, static_cast<uint32_t>(pdx), depth.desc.Format);
            const float pz   = linearise(praw, reversed_depth(), base.zNear, base.zFar);
            const float pd   = base.dispScale / (pz > 1e-4f ? pz : 1e-4f);

            if (pd < 25.0f || pd > 260.0f)
                continue;

            /* Featureless blocks match equally everywhere - see the note in
               measure_raw_disparity, where taking their argmin as an answer is
               exactly what produced a confident wrong result. */
            double mean = 0.0;
            int    n    = 0;
            for (int y = by; y < by + block; y += 4)
                for (int x = bx; x < bx + block; x += 4)
                { mean += luma_at(real, x, y); ++n; }
            if (n == 0) continue;
            mean /= n;

            double dev = 0.0;
            for (int y = by; y < by + block; y += 4)
                for (int x = bx; x < bx + block; x += 4)
                    dev += std::abs(luma_at(real, x, y) - mean);
            dev /= n;

            if (dev < 6.0)
                continue;

            double bestCost  = 1e30;
            int    bestShift = 0;

            for (int s = -maxShift; s <= maxShift; ++s)
            {
                double cost = 0.0;
                int    m    = 0;

                for (int y = by; y < by + block; y += 4)
                    for (int x = bx; x < bx + block; x += 4)
                    {
                        const int sx = x + s;
                        if (sx < 0 || sx >= W) continue;
                        cost += std::abs(luma_at(pred, sx, y) - luma_at(real, x, y));
                        ++m;
                    }

                if (m == 0) continue;
                cost /= m;
                if (cost < bestCost) { bestCost = cost; bestShift = s; }
            }

            ++used;
            residualSum += bestShift;

            _snprintf_s(line + std::strlen(line), sizeof(line) - std::strlen(line),
                        _TRUNCATE, "[z=%.1fm moved %.0f resid %+d] ", pz, pd, bestShift);
            ++reported;
        }
    }

    if (used == 0)
        return;

    BVR_INFO("AFW synthesis residual, eye %d (synthesised from eye %d): %d near "
             "block(s), mean residual %+.1f px. %s-- a faithful warp leaves ~0: "
             "the predicted eye should already sit where the real one does. A "
             "residual near the 'moved' figure means the warp did not move that "
             "block at all; a residual of about TWICE it means it moved the "
             "block the wrong way.",
             eye, 1 - eye, used, residualSum / used, line);
}

double trial_cost(const Staged& src, const Staged& dst, const Staged& depth,
                  const Params& base, float dir, float gain)
{
    const int W = static_cast<int>(base.size[0]);
    const int H = static_cast<int>(base.size[1]);

    const int y0 = H / 2 - 128;
    const int y1 = H / 2 + 128;

    double cost = 0.0;
    int counted = 0;

    for (int y = y0; y < y1; y += 2)
    {
        const uint8_t* depthRow = static_cast<const uint8_t*>(depth.map.pData) +
            static_cast<int>(y * base.depthScale[1]) * depth.map.RowPitch;

        for (int x = 16; x < W - 16; x += 2)
        {
            float fx = static_cast<float>(x);

            for (int i = 0; i < 4; ++i)
            {
                int dx = static_cast<int>(fx * base.depthScale[0]);
                if (dx < 0) dx = 0;
                if (dx > static_cast<int>(base.depthLimit[0])) dx = static_cast<int>(base.depthLimit[0]);

                const float raw = sample_depth(depthRow, static_cast<uint32_t>(dx), depth.desc.Format);
                const float z = linearise(raw, reversed_depth(), base.zNear, base.zFar);

                float d = base.dispScale * gain / (z > 1e-4f ? z : 1e-4f);
                if (d > base.maxDisparity) d = base.maxDisparity;
                if (d < -base.maxDisparity) d = -base.maxDisparity;

                fx = static_cast<float>(x) + dir * d;
            }

            int sx = static_cast<int>(fx + 0.5f);
            if (sx < 0 || sx >= W)
                continue;

            cost += std::abs(luma_at(src, sx, y) - luma_at(dst, x, y));
            ++counted;
        }
    }

    return counted > 0 ? cost / counted : 1e30;
}


/* The measured correction. gain multiplies dispScale and dirOverride replaces
   the derived sign; together they absorb a wrong near plane, a wrong focal
   length, a wrong baseline and a wrong direction without needing to know which
   of them it was. */
float g_gain = 1.0f;
float g_dirOverride = 0.0f;    /* 0 = use the derived sign */
bool  g_calibrated = false;
bool  g_calibrating = false;

/* Set once, never cleared: the calibration decided this scene's depth cannot
   predict the second eye. AFW then declines every frame rather than degrading
   to a copy - see the note where it is set. The caller reads it through
   afw_stood_down() and stops asking, so the per-frame fallback counter does not
   fill the log with a decision that was made once. */
bool  g_standDown = false;

/* Calibration takes more than one sample now - see the ladder note. These carry
   the previous sample between attempts; ordinary AFR runs in between, which is
   what keeps supplying real eyes to measure against. */
int   g_calibAttempts = 0;
float g_calibPrevGain = 0.0f;
constexpr int kMaxCalibAttempts = 8;
ID3D11Texture2D* g_calibEye[kAfwEyes] = { nullptr, nullptr };
bool g_calibHas[kAfwEyes] = { false, false };

/* Applies a hand-pinned afw_gain, if there is one, and reports whether it did.
 *
 * Its own function because it has to be reachable from BOTH the point where the
 * measurement would be run and the point where evidence for it is collected -
 * see the note in calibrating() for what happened when only one of those knew
 * about it. */
bool apply_pinned_gain();

void release_calibration_textures()
{
    for (uint32_t e = 0; e < kAfwEyes; ++e)
    {
        if (g_calibEye[e] != nullptr)
        {
            g_calibEye[e]->Release();
            g_calibEye[e] = nullptr;
        }
        g_calibHas[e] = false;
    }
}

bool apply_pinned_gain()
{
    const float pinned = static_cast<float>(config_float("afw_gain", 1.0f));
    if (!(pinned > 0.0f))
        return false;

    g_calibrated = true;
    g_gain = pinned;
    BVR_INFO("AFW calibration: skipped - afw_gain = %.3f pins it by hand.", pinned);
    release_calibration_textures();
    return true;
}

void run_calibration(ID3D11Device* device, ID3D11DeviceContext* context,
                     ID3D11Texture2D* depth, const Params& base)
{
    /* NO LONGER "once, whatever happens". A single sample is what let a gain of
       4.0 through, so each attempt now either agrees with the one before it or
       sends us round again; g_calibrated is set where that is decided. */

    /* DEFAULTS TO 1.0 - THE PHYSICALLY CORRECT VALUE - RATHER THAN TO A SEARCH.
     *
       For rectified stereo the disparity is exactly focal * baseline / z. There
       is no free scale factor in that, so a correct implementation has no gain
       knob at all: PureDark's frame-warp interface exposes scales for MOTION
       vectors and nothing whatever for disparity, because it reprojects through
       camera matrices where the displacement simply falls out of the geometry.

       This search existed because the model was NOT exact while a constant
       ceiling was clamping the near field. With everything close to the camera
       having its disparity truncated, no single gain could satisfy both halves
       of the image, and the search settled on a compromise that was wrong
       everywhere rather than wrong only up close. It was unstable twice and got
       pinned at 0.9 to stop it moving.

       That 0.9 then became the whole of the remaining artifact. Measured
       against this session's own numbers: the symmetric focal length is 1116 px
       and the baseline 0.0653 m, so the true disparity is 72.9 px at one metre,
       and the log reported 65.6 - exactly 0.9 of it. A fixed fractional
       undershoot puts the second eye 7 px out at a metre and 15 px out at half
       a metre, while staying under a pixel past five metres. That is precisely
       "objects near me have visible doubling, far away is fine".

       With the ceiling now derived from the near plane the model is exact
       again, so the right scale is 1. Set afw_gain = 0 to run the search
       instead, which is now a diagnostic rather than a necessity. */
    if (apply_pinned_gain())
        return;

    Staged src = {}, dst = {}, dep = {};

    if (stage_and_map(device, context, g_calibEye[0], src) &&
        stage_and_map(device, context, g_calibEye[1], dst) &&
        stage_and_map(device, context, depth, dep))
    {
        /* Asked once, and before anything that depends on the depth, because it
           is the one measurement here that does not. */
        static bool measuredSeparation = false;
        static int  measureTries = 0;
        if (!measuredSeparation && measureTries < 40)
        {
            ++measureTries;
            /* Latched only once it has found near geometry to measure. A sample
               taken while the view holds nothing but distant terrain reports
               zeros that mean "nothing to see here", not "no parallax". */
            measuredSeparation = measure_raw_disparity(src, dst, dep, base);
        }

        /* THE LADDER USED TO SPAN A FACTOR OF SIXTEEN EITHER WAY, AND THAT WAS
           THE BUG.
         *
           The gain is a CORRECTION to focal_px * baseline / z, not a free
           parameter. With a 108 degree eye over 2628 px the focal length is
           ~955 px and the baseline is 65 mm, so the model predicts ~62 px of
           disparity at one metre and the honest gain is close to 1.0. A gain of
           4, let alone 16, is not a correction - it is the search finding a
           spurious minimum and the model being overruled by it.
         *
           Which is exactly what happened. Two runs of the SAME arena scene
           picked 0.750 and then 4.000, giving 51.7 px and 275.9 px per unit.
           At 4.0 an object twenty metres away gets 14 px of separation where it
           should have under 3, so the distance doubles - "far away is worse",
           precisely, and from a number that should be a physical constant.
         *
           The cause is that the two eyes it measures against are captured on
           CONSECUTIVE frames, because AFR alternates them. Any head motion in
           that 11 ms is added to the apparent disparity and the search has no
           way to tell the two apart, so it absorbs the motion into the gain.
           Hold still and it reads 0.75; move while it samples and it reads 4.
         *
           So the ladder is now bounded to what a correction can plausibly be,
           and landing on either END of it is treated as "the model does not fit
           this scene" rather than as an answer. */
        /* NARROWED AGAIN, from 0.50..2.00, and for a reason the last round
           proved rather than assumed.
         *
           Bounding the ladder to a factor of two either way was still too much
           rope. A run calibrated to 1.600 - the second rung from the top, with
           two other samples rejected for sitting ON the top - and produced
           116.6 px per unit where the physical model says about 65 and where
           the run that actually looked right measured 62.0. Nearly double, at
           every distance, which is doubling in the FAR field as well as the
           near one: the one place the earlier failures never reached.
         *
           The agreement check did not catch it because both samples agreed at
           1.600. Agreement rules out RANDOM contamination - a single sample
           taken mid-flinch - and not SYSTEMATIC contamination, which is what
           steady head movement through both samples produces. That was the flaw
           in the reasoning: two measurements of the same wrong thing agree
           perfectly.
         *
           So the ladder is now tight around 1.0, because the gain is a
           correction to a model whose two inputs - focal length and baseline -
           are both known exactly. A correction of 1.6 is not a correction. With
           this range a contaminated run runs off the end of the ladder instead,
           which is already treated as "the model does not fit", and the fallback
           is the model's own 1.0 - the right answer, arrived at by declining to
           guess. */
        const float gains[] = { 0.75f, 0.80f, 0.85f, 0.90f, 0.95f, 1.00f,
                                1.05f, 1.10f, 1.15f, 1.20f, 1.30f };
        const int   gainCount = static_cast<int>(sizeof(gains) / sizeof(gains[0]));
        const float dirs[] = { 1.0f, -1.0f };

        double best = 1e30;
        float bestGain = 1.0f;
        float bestDir = base.dir;

        /* And the reference: no warp at all. If nothing beats simply copying the
           rendered eye across, the model is not describing this scene and a flat
           copy is the honest output - two identical eyes read as a flat picture,
           which is far better than a confidently wrong double image. */
        const double flat = trial_cost(src, dst, dep, base, base.dir, 0.0f);

        for (float dir : dirs)
        {
            for (float gain : gains)
            {
                const double cost = trial_cost(src, dst, dep, base, dir, gain);
                if (cost < best)
                {
                    best = cost;
                    bestGain = gain;
                    bestDir = dir;
                }
            }
        }

        ++g_calibAttempts;

        /* Landing on either end of the ladder is not a measurement, it is the
           search running out of room. Treated as a miss so it gets sampled
           again rather than applied. */
        const bool atEdge = bestGain <= gains[0] + 1e-4f ||
                            bestGain >= gains[gainCount - 1] - 1e-4f;

        /* ONE MEASUREMENT IS NOT A MEASUREMENT.
         *
           A sample taken while the head was moving is indistinguishable from a
           larger baseline, and that is how 4.000 got in. Motion contamination
           is random, so two independent samples agreeing is strong evidence
           that neither was contaminated - while one sample, however good its
           cost looks, carries no such evidence at all. */
        const bool usable = (best < flat * 0.98) && !atEdge;
        const bool agrees = usable && g_calibPrevGain > 0.0f &&
                            fabsf(bestGain - g_calibPrevGain) <=
                                0.25f * (bestGain > g_calibPrevGain ? bestGain
                                                                    : g_calibPrevGain);

        if (usable && !agrees && g_calibAttempts < kMaxCalibAttempts)
        {
            /* Remembered, not applied. The next sample either confirms it or
               replaces it; ordinary AFR runs meanwhile, which is what makes the
               next pair of REAL eyes available to measure against. */
            g_calibPrevGain = bestGain;
            BVR_INFO("AFW calibration: sample %d reads gain %.3f (cost %.2f vs "
                     "%.2f flat). Held, not applied - a single sample taken while "
                     "the head was moving looks exactly like a wider baseline, so "
                     "it takes two that agree.",
                     g_calibAttempts, bestGain, best, flat);

            unstage(context, dep);
            unstage(context, dst);
            unstage(context, src);
            release_calibration_textures();
            return;
        }

        if (!usable && g_calibAttempts < kMaxCalibAttempts)
        {
            BVR_INFO("AFW calibration: sample %d rejected (%s). Sampling again.",
                     g_calibAttempts,
                     atEdge ? "the best gain sat on the end of the ladder, so the "
                              "model does not fit rather than needing correction"
                            : "nothing beat leaving the eye unwarped");

            unstage(context, dep);
            unstage(context, dst);
            unstage(context, src);
            release_calibration_textures();
            return;
        }

        if (agrees)
        {
            g_calibrated = true;

            /* The mean of two agreeing samples, which is a better estimate than
               either and costs nothing. */
            g_gain = 0.5f * (bestGain + g_calibPrevGain);
            g_dirOverride = bestDir;

            BVR_INFO("AFW calibration: two samples agree - %.3f and %.3f, using "
                     "%.3f, direction %+.0f (cost %.2f against %.2f for no warp "
                     "at all). Agreement is the point: it is what says neither "
                     "sample was taken mid-movement, which is the failure that "
                     "produced a gain of 4.0 and doubled the distance.",
                     g_calibPrevGain, bestGain, g_gain, bestDir, best, flat);
        }
        else if (usable)
        {
            /* Out of attempts, but the samples were individually sane - they
               simply never agreed twice running. The physical model is the
               better answer than any one of them: it predicts ~62 px at a metre
               from a focal length and a baseline that are both known, and every
               clean sample so far has landed near it. So take the model. */
            g_calibrated = true;
            g_gain = 1.0f;
            g_dirOverride = bestDir;

            BVR_WARN("AFW calibration: %d samples and no two agreed (last %.3f). "
                     "Falling back to the physical model at gain 1.000, direction "
                     "%+.0f - focal length and baseline are both known, so the "
                     "model is a better estimate than a measurement that will not "
                     "repeat. Set afw_gain to pin it if this is wrong.",
                     g_calibAttempts, bestGain, bestDir);
        }
        else
        {
            /* "FLAT, BUT HONEST" WAS THE BUG, AND IT IS WORTH SPELLING OUT.
             *
               This used to set the gain to zero and carry on warping. A zero
               gain copies the rendered eye across unchanged, and the reasoning
               was that two identical images are at worst flat - no stereo, but
               nothing actively wrong.
             *
               That is only true if the two images are also SUBMITTED from the
               same viewpoint, and they are not. afw_capture tags each eye with
               its own pose from the locate, so the compositor was handed one
               picture twice, at two viewpoints an IPD apart. Every object in the
               scene then sits 65 mm to the side of where its own eye says it
               should be, with no parallax anywhere to justify the shift. That
               does not read as flat. It reads as two pictures that will not
               fuse - the exact "confident double image" this branch was written
               to avoid, produced by the branch itself.
             *
               So the warp does not degrade to a copy. It stands down, and the
               caller falls back to AFR - where the second eye is a REAL render
               from that eye's own position, one frame old. Stale and correct
               beats simultaneous and displaced. */
            g_gain = 0.0f;
            g_standDown = true;

            BVR_WARN("AFW calibration: NO candidate beat copying the rendered eye "
                     "across unchanged (best %.2f vs %.2f). The depth is not "
                     "predicting this scene, so AFW has STOOD DOWN and the mod is "
                     "running ordinary AFR for the rest of the session - each eye "
                     "a real render from its own position. AFW cannot copy an eye "
                     "across instead: both eyes are submitted with their own pose, "
                     "so a copy arrives an IPD away from where its eye expects it "
                     "and the two will not fuse. Send this log.",
                     best, flat);
        }
    }
    else
    {
        /* Must settle it: run_calibration no longer marks itself done on entry,
           so leaving this open would retry the readback every frame forever. */
        g_calibrated = true;
        g_gain = 1.0f;

        BVR_WARN("AFW calibration: could not read the eyes back; running "
                 "uncalibrated at the physical model's gain 1.0.");
    }

    unstage(context, dep);
    unstage(context, dst);
    unstage(context, src);
    release_calibration_textures();
}


/* Collects the two real eyes and calibrates against them, returning false until
   it is done. False makes the caller fall back to AFR for those frames, which
   is exactly what we want - the eyes have to be REAL renders for the
   measurement to mean anything, and the player sees ordinary AFR meanwhile. */
bool calibrating(ID3D11Device* device, ID3D11DeviceContext* context,
                 ID3D11Texture2D* srcColor, int srcEye,
                 ID3D11Texture2D* depth, const Params& base)
{
    if (g_calibrated)
        return false;

    /* THE PIN IS CHECKED BEFORE THE COLLECTION, NOT AFTER IT.
     *
       It used to be checked only inside run_calibration, which is reached solely
       once BOTH real eyes have been captured below. That was harmless while the
       renderer alternated eyes, because the second one always turned up a frame
       later.

       With afw_pin_source_eye the renderer stops alternating, so a real render
       of the second eye never arrives. g_calibHas[1] stayed false for ever,
       run_calibration was never called, g_calibrated never became true, and this
       function reported "still calibrating" on every frame of the session - so
       the warp never ran once. The mode fell back to AFR ninety times a second,
       AFR could only ever fill the one eye it was given, and the other went up
       as the flat proof-of-life colour. A red right eye, all the way down from a
       decision made two functions away.

       A pinned gain needs no measurement at all, so waiting to collect evidence
       for a decision already taken was always pointless; it was merely harmless
       until something stopped supplying the evidence. */
    if (apply_pinned_gain())
        return false;

    const uint32_t eye = static_cast<uint32_t>(srcEye);
    if (eye >= kAfwEyes)
        return true;

    if (!g_calibHas[eye])
    {
        if (g_calibEye[eye] == nullptr)
        {
            D3D11_TEXTURE2D_DESC desc = {};
            srcColor->GetDesc(&desc);
            desc.Usage = D3D11_USAGE_DEFAULT;
            desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            desc.CPUAccessFlags = 0;
            desc.MiscFlags = 0;

            if (FAILED(device->CreateTexture2D(&desc, nullptr, &g_calibEye[eye])))
            {
                BVR_WARN("AFW calibration: could not allocate a reference eye; "
                         "running uncalibrated.");
                g_calibrated = true;
                release_calibration_textures();
                return false;
            }
        }

        context->CopyResource(g_calibEye[eye], srcColor);
        g_calibHas[eye] = true;

        if (!g_calibrating)
        {
            g_calibrating = true;
            BVR_INFO("AFW: measuring the disparity against both real eyes before "
                     "switching the warp on. A frame or two of ordinary AFR.");
        }
    }

    if (g_calibHas[0] && g_calibHas[1])
        run_calibration(device, context, depth, base);

    return !g_calibrated;
}

bool ensure_shader(ID3D11Device* device)
{
    if (g_shader != nullptr)
        return true;
    if (g_shaderFailed || device == nullptr)
        return false;

    /* Two entry points out of one source, so the packing helpers and the
       depth-to-disparity maths cannot drift apart between the pass that writes
       a claim and the pass that reads it. */
    struct Entry { const char* name; ID3D11ComputeShader** out; };
    const Entry entries[2] = {
        { "scatter", &g_shader },
        { "gather",  &g_gatherShader },
    };

    for (int i = 0; i < 2; ++i)
    {
        ID3DBlob* code = nullptr;
        ID3DBlob* errors = nullptr;

        const HRESULT hr = D3DCompile(kWarpShader, std::strlen(kWarpShader),
                                      "afw_warp.hlsl", nullptr, nullptr,
                                      entries[i].name, "cs_5_0",
                                      D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                                      &code, &errors);

        if (FAILED(hr) || code == nullptr)
        {
            BVR_ERR("AFW: warp shader '%s' failed to compile (0x%08X): %s",
                    entries[i].name, hr,
                    errors != nullptr ? static_cast<const char*>(errors->GetBufferPointer())
                                      : "no compiler output");
            g_shaderFailed = true;
            if (errors != nullptr) errors->Release();
            if (code != nullptr) code->Release();
            return false;
        }

        if (errors != nullptr)
            errors->Release();

        const HRESULT csHr = device->CreateComputeShader(code->GetBufferPointer(),
                                                         code->GetBufferSize(),
                                                         nullptr, entries[i].out);
        code->Release();

        if (FAILED(csHr))
        {
            BVR_ERR("AFW: CreateComputeShader failed for '%s' (0x%08X).",
                    entries[i].name, csHr);
            g_shaderFailed = true;
            return false;
        }
    }

    D3D11_SAMPLER_DESC sampDesc = {};
    sampDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sampDesc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampDesc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampDesc.MaxLOD = D3D11_FLOAT32_MAX;

    if (FAILED(device->CreateSamplerState(&sampDesc, &g_sampler)))
    {
        BVR_ERR("AFW: could not create the warp's sampler.");
        g_shaderFailed = true;
        return false;
    }

    D3D11_BUFFER_DESC cbDesc = {};
    cbDesc.ByteWidth = sizeof(Params);
    cbDesc.Usage = D3D11_USAGE_DYNAMIC;
    cbDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    cbDesc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;

    if (FAILED(device->CreateBuffer(&cbDesc, nullptr, &g_cb)))
    {
        BVR_ERR("AFW: could not create the warp constant buffer.");
        g_shaderFailed = true;
        return false;
    }

    BVR_INFO("AFW: warp shader compiled. The second eye is now synthesised from "
             "the first rather than held from the previous frame.");
    return true;
}

/* The depth buffer is typeless - rgl allocates R16_TYPELESS or R32_TYPELESS so
   it can be both a depth target and a texture - so the SRV has to name the
   readable half explicitly. */
DXGI_FORMAT depth_srv_format(DXGI_FORMAT textureFormat)
{
    switch (textureFormat)
    {
    case DXGI_FORMAT_R16_TYPELESS:
    case DXGI_FORMAT_D16_UNORM:          return DXGI_FORMAT_R16_UNORM;
    case DXGI_FORMAT_R32_TYPELESS:
    case DXGI_FORMAT_D32_FLOAT:          return DXGI_FORMAT_R32_FLOAT;
    case DXGI_FORMAT_R24G8_TYPELESS:
    case DXGI_FORMAT_D24_UNORM_S8_UINT:  return DXGI_FORMAT_R24_UNORM_X8_TYPELESS;
    case DXGI_FORMAT_R32G8X24_TYPELESS:
    case DXGI_FORMAT_D32_FLOAT_S8X24_UINT:
        return DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS;
    default:                             return DXGI_FORMAT_UNKNOWN;
    }
}

bool ensure_source_srv(ID3D11Device* device, ID3D11Texture2D* texture)
{
    if (g_srcSrv != nullptr && g_srvFor == texture)
        return true;

    if (g_srcSrv != nullptr) { g_srcSrv->Release(); g_srcSrv = nullptr; }
    g_srvFor = nullptr;

    if (FAILED(device->CreateShaderResourceView(texture, nullptr, &g_srcSrv)))
    {
        BVR_ERR("AFW: could not create a view of the rendered eye.");
        return false;
    }

    g_srvFor = texture;
    return true;
}

/* Defined below, next to the destination view that has to agree with it. */
DXGI_FORMAT uav_format_for(DXGI_FORMAT textureFormat);

/* A view of the previous real render of the eye being synthesised.
 *
 * NOT a null-descriptor view like the source's. The AFR hold textures are
 * created TYPELESS so the warp can take a typed UAV on them, and a typeless
 * texture has no default interpretation - a null desc fails outright. So the
 * view names the UNORM member of the family, matching the destination view
 * exactly. Not the _SRGB member, for the same reason as the UAV: the fill is
 * copied byte for byte and a conversion on only one of the two paths would make
 * the holes a different brightness from the rest of the eye. */
bool ensure_prev_srv(ID3D11Device* device, ID3D11Texture2D* texture)
{
    if (texture == nullptr)
        return false;

    if (g_prevSrv != nullptr && g_prevSrvFor == texture)
        return true;

    if (g_prevSrv != nullptr) { g_prevSrv->Release(); g_prevSrv = nullptr; }
    g_prevSrvFor = nullptr;

    D3D11_TEXTURE2D_DESC desc = {};
    texture->GetDesc(&desc);

    if ((desc.BindFlags & D3D11_BIND_SHADER_RESOURCE) == 0)
        return false;

    D3D11_SHADER_RESOURCE_VIEW_DESC view = {};
    view.Format = uav_format_for(desc.Format);
    view.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    view.Texture2D.MipLevels = 1;

    if (view.Format == DXGI_FORMAT_UNKNOWN)
        return false;

    if (FAILED(device->CreateShaderResourceView(texture, &view, &g_prevSrv)))
        return false;

    g_prevSrvFor = texture;
    return true;
}

bool ensure_depth_srv(ID3D11Device* device, ID3D11Texture2D* texture)
{
    for (int i = 0; i < 2; ++i)
    {
        if (g_depthSrvSlot[i] != nullptr && g_depthSrvSlotFor[i] == texture)
        {
            g_depthSrv     = g_depthSrvSlot[i];
            g_depthSrvFor  = texture;
            g_depthSrvNext = 1 - i;
            return true;
        }
    }

    const int slot = g_depthSrvNext;
    if (g_depthSrvSlot[slot] != nullptr) { g_depthSrvSlot[slot]->Release(); g_depthSrvSlot[slot] = nullptr; }
    g_depthSrvSlotFor[slot] = nullptr;
    g_depthSrv    = nullptr;
    g_depthSrvFor = nullptr;

    D3D11_TEXTURE2D_DESC desc = {};
    texture->GetDesc(&desc);

    if ((desc.BindFlags & D3D11_BIND_SHADER_RESOURCE) == 0)
        return false;

    D3D11_SHADER_RESOURCE_VIEW_DESC view = {};
    view.Format = depth_srv_format(desc.Format);
    view.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    view.Texture2D.MipLevels = 1;

    if (view.Format == DXGI_FORMAT_UNKNOWN)
    {
        BVR_ERR("AFW: depth format %d is not one this can read.",
                static_cast<int>(desc.Format));
        return false;
    }

    if (FAILED(device->CreateShaderResourceView(texture, &view, &g_depthSrvSlot[slot])))
    {
        BVR_ERR("AFW: could not create a view of the eye depth buffer.");
        return false;
    }

    g_depthSrv              = g_depthSrvSlot[slot];
    g_depthSrvSlotFor[slot] = texture;
    g_depthSrvNext          = 1 - slot;

    g_depthSrvFor = texture;
    return true;
}

DXGI_FORMAT uav_format_for(DXGI_FORMAT textureFormat)
{
    switch (textureFormat)
    {
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:
    case DXGI_FORMAT_B8G8R8A8_UNORM:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:
    case DXGI_FORMAT_R8G8B8A8_UNORM:
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        return DXGI_FORMAT_R8G8B8A8_UNORM;
    default:
        return DXGI_FORMAT_UNKNOWN;
    }
}

/* The claim buffer the scatter writes and the gather reads.
 *
 * R32_UINT because InterlockedMax needs an integer UAV, and typed atomic
 * support on R32_UINT is REQUIRED of every D3D11 device - unlike the typed
 * store to B8G8R8A8_UNORM below, this one needs no capability check.
 *
 * One allocation per destination size, reused for the life of the mission: it
 * is cleared wholesale each frame, so there is nothing in it worth keeping and
 * nothing to invalidate. */
bool ensure_lookup(ID3D11Device* device, uint32_t width, uint32_t height)
{
    if (g_lookupUav != nullptr && g_lookupW == width && g_lookupH == height)
        return true;

    /* The claim packs the source x into 13 bits below the disparity, so a wider
       eye than this would wrap it and hand the gather a source pixel from the
       wrong side of the image. The runtime's own maximum here is 5440, so this
       is a cliff nothing reaches - but it is a SILENT one, and a wrapped index
       reads as scattered wrong-coloured pixels rather than as a limit. */
    if (width > (1u << 13))
    {
        BVR_ERR("AFW: an eye %u pixels wide exceeds the %u the claim buffer can "
                "index, so the warp cannot address its own source. Staying on AFR.",
                width, 1u << 13);
        return false;
    }

    if (g_lookupUav != nullptr) { g_lookupUav->Release(); g_lookupUav = nullptr; }
    if (g_lookup != nullptr)    { g_lookup->Release();    g_lookup = nullptr; }
    g_lookupW = 0;
    g_lookupH = 0;

    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R32_UINT;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_UNORDERED_ACCESS;

    if (FAILED(device->CreateTexture2D(&desc, nullptr, &g_lookup)))
    {
        BVR_ERR("AFW: could not allocate the %ux%u claim buffer the warp "
                "resolves occlusion through. Staying on AFR.", width, height);
        return false;
    }

    D3D11_UNORDERED_ACCESS_VIEW_DESC view = {};
    view.Format = DXGI_FORMAT_R32_UINT;
    view.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;

    if (FAILED(device->CreateUnorderedAccessView(g_lookup, &view, &g_lookupUav)))
    {
        BVR_ERR("AFW: could not create the claim buffer's unordered-access view.");
        g_lookup->Release();
        g_lookup = nullptr;
        return false;
    }

    g_lookupW = width;
    g_lookupH = height;
    return true;
}

bool ensure_dst_uav(ID3D11Device* device, ID3D11Texture2D* texture)
{
    if (g_dstUav != nullptr && g_uavFor == texture)
        return true;

    if (g_dstUav != nullptr) { g_dstUav->Release(); g_dstUav = nullptr; }
    g_uavFor = nullptr;

    D3D11_TEXTURE2D_DESC desc = {};
    texture->GetDesc(&desc);

    if ((desc.BindFlags & D3D11_BIND_UNORDERED_ACCESS) == 0)
    {
        BVR_ERR("AFW: the destination eye texture was created without "
                "UNORDERED_ACCESS, so the warp cannot write to it.");
        return false;
    }

    /* A UAV cannot name a TYPELESS format, and the destination is deliberately
       typeless - that is the whole reason it can be written at all, since a
       typed UAV is not allowed on B8G8R8A8_UNORM directly. So the view names the
       UNORM member of the family the texture was created from.
     *
       Not the _SRGB member. The shader writes the colour it read, byte for byte,
       and an sRGB view would apply a conversion on the write that the read never
       undid - which shows up as the synthesised eye being visibly brighter than
       the rendered one, a difference between the eyes that is far more
       objectionable than being wrong in the same way twice. */
    D3D11_UNORDERED_ACCESS_VIEW_DESC view = {};
    view.Format = uav_format_for(desc.Format);
    view.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;

    if (view.Format == DXGI_FORMAT_UNKNOWN)
    {
        BVR_ERR("AFW: destination format %d cannot take an unordered-access view.",
                static_cast<int>(desc.Format));
        return false;
    }

    /* Typed UAV stores are only REQUIRED for a short list of formats, and
       B8G8R8A8_UNORM is not on it - support is optional and up to the driver.
       Every desktop GPU worth running this on has it, but "optional" plus a
       silent failure is how a mode ends up looking broken for reasons nobody can
       see, so ask first and say so plainly. */
    D3D11_FEATURE_DATA_FORMAT_SUPPORT2 support2 = {};
    support2.InFormat = view.Format;

    if (SUCCEEDED(device->CheckFeatureSupport(D3D11_FEATURE_FORMAT_SUPPORT2,
                                              &support2, sizeof(support2))) &&
        (support2.OutFormatSupport2 & D3D11_FORMAT_SUPPORT2_UAV_TYPED_STORE) == 0)
    {
        BVR_ERR("AFW: this driver does not support typed UAV stores to format %d, "
                "so the synthesised eye cannot be written. Staying on AFR.",
                static_cast<int>(view.Format));
        return false;
    }

    if (FAILED(device->CreateUnorderedAccessView(texture, &view, &g_dstUav)))
    {
        BVR_ERR("AFW: could not create a writable view of the destination eye.");
        return false;
    }

    g_uavFor = texture;
    return true;
}

} // namespace

void afw_set_params(const AfwParams& params)
{
    if (params.focalPx <= 0.0f || params.baseline <= 0.0f ||
        params.zNear <= 0.0f || params.zFar <= params.zNear)
        return;

    g_params = params;
    g_haveParams.store(true, std::memory_order_release);
}

bool afw_have_params()
{
    return g_haveParams.load(std::memory_order_acquire);
}

float afw_focal_px()
{
    return afw_have_params() ? g_params.focalPx : 0.0f;
}

float afw_baseline()
{
    return afw_have_params() ? g_params.baseline : 0.0f;
}

float afw_z_near()
{
    return afw_have_params() ? g_params.zNear : 0.0f;
}

float afw_z_far()
{
    return afw_have_params() ? g_params.zFar : 0.0f;
}

/* Exposed for the depth composition layer, which has to tell the compositor
   which end of the 0..1 range is near. This is the same answer the warp uses,
   deliberately: two independent guesses at the depth direction is how one of
   them ends up inverted and only shows it as an artefact. */
bool afw_depth_is_reversed()
{
    return reversed_depth();
}

/* Set once per frame by the capture, just before the warp. See afw_warp.h. */
float g_prevRot[9]  = { 1,0,0, 0,1,0, 0,0,1 };
float g_prevTan[4]  = { 0,0,0,0 };   /* left, right, up, down */
bool  g_prevRotValid = false;

void afw_set_prev_reproject(const float rot[9], float tanLeft, float tanRight,
                            float tanUp, float tanDown, bool valid)
{
    if (rot != nullptr)
        for (int i = 0; i < 9; ++i)
            g_prevRot[i] = rot[i];

    g_prevTan[0] = tanLeft;
    g_prevTan[1] = tanRight;
    g_prevTan[2] = tanUp;
    g_prevTan[3] = tanDown;

    /* A degenerate frustum would divide by zero building the intrinsics, and a
       frame with no previous pose has nothing to correct towards. */
    g_prevRotValid = valid && (tanRight - tanLeft) > 1e-6f &&
                     (tanUp - tanDown) > 1e-6f;
}

void afw_check_synthesis(ID3D11Device* device, ID3D11DeviceContext* context,
                         ID3D11Texture2D* predicted, ID3D11Texture2D* real,
                         int eye)
{
    /* A budget per eye, SPREAD OUT rather than taken as fast as the frames
       arrive.
     *
       The first run took its whole allowance inside 122 ms - eight consecutive
       frames at AFW startup, on a static scene - which measures one instant
       thoroughly and the session not at all. The first sample of each eye was
       also visibly unsettled, so a burst at startup is the worst window to
       choose. Spacing them shows whether a residual is a constant or something
       that grows with movement, which is the difference between a fixed offset
       to correct and an error that scales.

       Still budgeted: two full-frame staging copies and a search is not a cost
       to leave running per frame, which the depth probe already taught once. */
    static int      taken[2] = { 0, 0 };
    static uint64_t lastMs[2] = { 0, 0 };

    if (device == nullptr || context == nullptr ||
        predicted == nullptr || real == nullptr || eye < 0 || eye > 1)
        return;
    /* HOW MANY SAMPLES THE RESIDUAL METER IS ALLOWED, PER EYE.
     *
       Twelve was right while this was a one-off check that the warp carried the
       geometry faithfully. It is wrong for diagnosing an artefact that depends
       on where the player is standing and which way they are facing: twelve
       samples are all spent in the first fifteen seconds of a mission, from one
       spot, looking one way - and the shaking being chased happens somewhere
       else entirely.

       Raise afw_residual_samples to measure for the whole mission instead. It
       costs two full-frame staging copies and a CPU map per sample, so this is a
       DIAGNOSTIC setting: leave it at 12 for play. */
    static int budget = -1;
    if (budget < 0)
        budget = static_cast<int>(config_float("afw_residual_samples", 12.0f));

    if (taken[eye] >= budget || !afw_have_params() || g_depthUnproven)
        return;

    const uint64_t now = GetTickCount64();

    /* Two seconds in, so the first measurement is not of the warp's first
       frames, then once a second. */
    if (lastMs[eye] == 0)
    {
        lastMs[eye] = now + 2000;
        return;
    }
    if (now < lastMs[eye])
        return;
    lastMs[eye] = now + 1000;

    ID3D11Texture2D* depth = eye_depth_texture();
    if (depth == nullptr)
        return;

    D3D11_TEXTURE2D_DESC desc = {};
    real->GetDesc(&desc);

    uint32_t dw = 0, dh = 0;
    eye_depth_size(&dw, &dh);
    if (dw == 0 || dh == 0)
        return;

    Params p = {};
    p.size[0] = static_cast<float>(desc.Width);
    p.size[1] = static_cast<float>(desc.Height);
    p.dispScale = g_params.focalPx * g_params.baseline * g_gain;
    p.zNear = g_params.zNear;
    p.zFar = g_params.zFar;
    p.depthScale[0] = static_cast<float>(dw) / static_cast<float>(desc.Width);
    p.depthScale[1] = static_cast<float>(dh) / static_cast<float>(desc.Height);
    p.depthLimit[0] = static_cast<float>(dw - 1);
    p.depthLimit[1] = static_cast<float>(dh - 1);

    Staged pr = {}, re = {}, dep = {};

    if (stage_and_map(device, context, predicted, pr) &&
        stage_and_map(device, context, real, re) &&
        stage_and_map(device, context, depth, dep))
    {
        ++taken[eye];
        measure_synth_residual(pr, re, dep, p, eye);
    }

    unstage(context, pr);
    unstage(context, re);
    unstage(context, dep);
}

bool afw_stood_down()
{
    return g_standDown;
}

bool afw_ready()
{
    /* Having a depth texture is not the same as having a USABLE one. The search
       hands one back as soon as it settles, but the warp does not run until that
       buffer has proved it contains a drawn scene - see the acceptance gate in
       afw_warp_eye - and between a mission teardown and that proof the answer
       here has to be no.

       It matters because the renderer PINS the source eye off this. Pinning
       means only one eye is ever really rendered and the other is produced
       solely by the warp, so while the warp is still declining the second eye
       has no content from any source and the submit path shows the first eye's
       image in both. Correct, but FLAT, for as long as the search takes: 1.5 to
       6 seconds across the four mission restarts in the log this came from.

       Answering no lets the renderer alternate instead, which is ordinary AFR -
       a real render of each eye every other frame. Proper stereo at half the
       rate beats no stereo at full rate, and the pin comes back by itself the
       moment the warp is live. */
    return afw_have_params() && !g_shaderFailed && !g_standDown &&
           eye_depth_texture() != nullptr && !g_depthUnproven;
}

bool afw_warp_eye(ID3D11Device* device, ID3D11DeviceContext* context,
                  ID3D11Texture2D* srcColor, int srcEye, ID3D11Texture2D* dst,
                  ID3D11Texture2D* prevReal)
{
    /* Declined before anything else, and WITHOUT counting a failure. This is not
       a frame that went wrong - it is a mode that measured itself, found it had
       nothing to offer this scene, and got out of the way. Counting it would
       report ninety failures a second for a decision taken once. */
    if (g_standDown)
        return false;

    if (device == nullptr || context == nullptr ||
        srcColor == nullptr || dst == nullptr || !afw_have_params())
    {
        ++g_failures;
        return false;
    }

    ID3D11Texture2D* depth = eye_depth_texture();
    if (depth == nullptr)
    {
        ++g_failures;
        return false;
    }

    probe_depth(device, context, depth, g_params.zNear, g_params.zFar);

    D3D11_TEXTURE2D_DESC desc = {};
    dst->GetDesc(&desc);

    if (!ensure_shader(device) ||
        !ensure_source_srv(device, srcColor) ||
        !ensure_depth_srv(device, depth) ||
        !ensure_dst_uav(device, dst) ||
        !ensure_lookup(device, desc.Width, desc.Height))
    {
        ++g_failures;
        return false;
    }

    Params p = {};
    p.size[0] = static_cast<float>(desc.Width);
    p.size[1] = static_cast<float>(desc.Height);
    p.dispScale = g_params.focalPx * g_params.baseline;

    /* Which way the destination pixels move.
     *
     * Rectified stereo: a point at view depth z sits at x in the left image and
     * at x - disparity in the right. So going LEFT -> RIGHT the source is found
     * to the RIGHT of the destination pixel, and going the other way it is to
     * the left. Everything else about the two directions is identical, which is
     * why this is a sign rather than a branch. */
    p.dir = (srcEye == 0) ? 1.0f : -1.0f;

    /* The measured sign, once it exists, outranks the derived one. */
    if (g_dirOverride != 0.0f)
        p.dir = (srcEye == 0) ? g_dirOverride : -g_dirOverride;

    p.zNear = g_params.zNear;
    p.zFar = g_params.zFar;
    p.reversed = reversed_depth() ? 1.0f : 0.0f;
    p.maxDisparity = max_disparity_px();

    /* The depth is not the same size as the colour. rgl renders the scene at
       half the eye in each dimension and upscales into our target, so a depth
       lookup has to be scaled or every disparity comes out halved. */
    uint32_t dw = 0, dh = 0;
    eye_depth_size(&dw, &dh);
    if (dw == 0 || dh == 0)
    {
        ++g_failures;
        return false;
    }

    p.depthScale[0] = static_cast<float>(dw) / static_cast<float>(desc.Width);
    p.depthScale[1] = static_cast<float>(dh) / static_cast<float>(desc.Height);
    p.depthLimit[0] = static_cast<float>(dw - 1);
    p.depthLimit[1] = static_cast<float>(dh - 1);

    /* IS THIS ACTUALLY THE SCENE'S DEPTH? ASKED ONCE PER TEXTURE, BEFORE
       ANYTHING IS BUILT ON THE ANSWER.
     *
       rgl binds more than one eye-sized depth target per frame, and the search
       that picks between them can only see size - which is identical across
       them - so it was really picking whichever was bound first. The one it
       picked contained nothing nearer than 8.4 metres while the player's own
       horse sat at half a metre, so every near object was warped by the depth
       of the terrain behind it. That is why the calibration reported that
       nothing beat leaving the eye unwarped, and why it was the weapon and the
       horse that tripled rather than the distant infantry.

       A first-person view always has the player's own body within arm's reach.
       A depth buffer that does not contain anything within a few metres is
       therefore not the one the scene was drawn with, whatever its size says.
       Rejecting it puts the search back on and blacklists this texture, so the
       next candidate gets its turn instead of this one being handed back. */
    const uint32_t depthGen = depth_generation();

    /* IDENTIFIED BY SHAPE AND GENERATION, NOT BY POINTER.
     *
       The pointer alternates now. rgl keeps a depth per render target, so under
       alternating eyes there are two scene depths of identical size and format
       and eye_depth_texture hands back whichever this frame drew into - which is
       the whole point of that change, and correct.

       Keyed on the pointer, each swap would look like a brand new buffer and the
       proof would be re-earned every single frame: a full-frame staging copy and
       a CPU map at frame rate, which is exactly the stall that the probe's own
       back-off was added to stop. The two buffers are the same scene at the same
       instant from two eyes; a proof that one contains drawn geometry is a proof
       for both, and the generation still forces a fresh proof per mission. */
    D3D11_TEXTURE2D_DESC dd = {};
    depth->GetDesc(&dd);

    const bool sameShape = dd.Width  == g_depthCheckedW &&
                           dd.Height == g_depthCheckedH &&
                           dd.Format == g_depthCheckedFmt;

    if (!sameShape || depthGen != g_depthCheckedGen)
    {
        g_depthChecked    = depth;
        g_depthCheckedGen = depthGen;
        g_depthCheckedW   = dd.Width;
        g_depthCheckedH   = dd.Height;
        g_depthCheckedFmt = dd.Format;
        g_depthUnproven   = true;
        g_depthWait       = 0;
        g_depthEmptyTries = 0;
        g_depthFarTries   = 0;

        /* A new mission gets to say "empty" out loud once of its own, rather
           than inheriting the last one's suppression. */
        g_loggedEmptyProbe = false;
    }

    /* NOTHING IS WARPED UNTIL THE DEPTH HAS PROVED ITSELF.
     *
       The check used to fall through when it could not measure anything, and
       falling through meant using the buffer. depth_nearest_metres skips
       samples still holding the cleared value, so a depth target that has been
       allocated but not yet drawn into returns "no measurement" - and that was
       read as consent.

       It is the first battle of a session that pays for this. The search latches
       a candidate the moment one is bound, which is before the scene has drawn
       anything into it, so the warp went live against an empty depth buffer and
       the synthesised eye was garbage. Restarting the battle fixed it because by
       then the buffers were full and the next candidate measured properly -
       which is exactly the "broken on the first try, fine after a restart" this
       was reported as.

       Unmeasurable now means NOT YET, which is what it actually means. The warp
       declines for the frame, the mode falls back to AFR - a real render in both
       eyes, which is the correct thing to show while waiting - and the buffer is
       asked again shortly. Only a buffer that stays empty across a dozen
       spaced-out attempts is written off as never drawn.

       Spaced out because the measurement is a full-frame staging copy and a map.
       Paying that every frame while waiting would trade a broken eye for a
       stalled one. */
    if (g_depthUnproven)
    {
        if (g_depthWait > 0)
        {
            --g_depthWait;
            return false;
        }

        const float nearest = depth_nearest_metres(device, context, depth,
                                                   g_params.zNear, g_params.zFar,
                                                   reversed_depth());
        const float floorM = static_cast<float>(
            config_float("afw_depth_max_near_m", 3.0f));

        if (nearest <= 0.0f)
        {
            if (++g_depthEmptyTries > 12)
            {
                depth_reject_current("every sample in it is still the cleared "
                                     "value after a dozen tries, so nothing is "
                                     "ever drawn into it");
                g_depthChecked    = nullptr;
                g_depthEmptyTries = 0;
            }
            else
            {
                g_depthWait = 15;
            }
            return false;
        }

        /* NOTHING NEAR **YET** IS A TRANSIENT, NOT A DISQUALIFICATION.
         *
           This used to reject and blacklist on the FIRST look, while the empty
           case above got a dozen spaced-out retries. That asymmetry is what made
           the mode a coin flip across mission loads.

           During deployment the real scene depth legitimately holds nothing
           within a few metres: the camera is above an empty field and the
           player's horse is not under them yet. Probed at that instant, the
           TRUE buffer was written off for the whole mission and a decoy of
           identical size and format inherited the slot - and a decoy has no near
           geometry in it, so the horse at 0.4 m gets a near-zero disparity
           instead of ~180 px and goes up as a doubled, half-transparent copy.

           The logs show both outcomes on consecutive loads of the same battle:
           one mission rejects the first candidate and settles on the second,
           the next accepts the first and never rejects anything. Which texture
           wins is decided purely by whether near geometry happened to exist at
           the moment the probe ran, which is why reloading flipped the result
           and why it flipped back on reloading again.

           So the far case now gets exactly the patience the empty case has. A
           buffer is only written off once it has stayed far across a dozen
           spaced-out attempts - by which time the battle is live, the player is
           mounted, and a genuine scene depth cannot still be empty in front. */
        if (nearest > floorM)
        {
            if (++g_depthFarTries > 12)
            {
                char why[200];
                snprintf(why, sizeof(why),
                         "nothing in it is nearer than %.1f m across a dozen "
                         "spaced-out tries, and a first-person view always has "
                         "the player's own body closer than %.1f",
                         nearest, floorM);
                depth_reject_current(why);
                g_depthChecked  = nullptr;
                g_depthFarTries = 0;
            }
            else
            {
                g_depthWait = 15;
            }
            return false;
        }

        g_depthUnproven   = false;
        g_depthEmptyTries = 0;
        g_depthFarTries   = 0;

        BVR_INFO("AFW: depth accepted - its nearest geometry is %.2f m, which is "
                 "the player's own body where it should be. This is the buffer "
                 "the scene was drawn with, and nothing was warped before this "
                 "line.", nearest);
    }

    /* IS THE DEPTH WE SETTLED ON STILL BEING DRAWN INTO?
     *
       The search proves a buffer once and then holds it for the mission - the
       watch switches off after 180 stable frames precisely so it stops costing
       anything. That is correct only if the engine keeps rendering the scene
       into the SAME texture. If it rotates targets, or if the one we latched is
       written once and then left alone, the warp goes on reading a depth map of
       wherever the player was standing when it was proven.

       That would look exactly like the report: correct at the spawn point, and
       wrong by more and more the further you walk from it, because the warp's
       ONLY positional input is this buffer. Depth is what turns into disparity;
       nothing else in the shader knows where the player is.

       So re-measure the held buffer once a second and say what its nearest
       geometry is. Standing in one place it should hold roughly steady; walking
       it must move, because the ground and the bodies around you are at
       different distances than they were. A figure that does not budge while
       the player crosses the field is a dead buffer, and the warp is being fed
       a photograph. */
    {
        /* OFF BY DEFAULT (TEST 176). It is a full-frame staging copy and a CPU
           map once a second, and the TEST 175 cost line caught it: CPU 0.15 ms
           mean per AFW frame with a max of 11-12.6 ms, once every second - a
           frame-sized stall on the render thread. afw_depth_liveness = 1 brings
           the diagnostic back when a dead depth buffer is suspected. */
        static int liveOn = -1;
        if (liveOn < 0)
            liveOn = config_float("afw_depth_liveness", 0.0f) > 0.5f ? 1 : 0;

        static uint64_t lastLive = 0;
        const uint64_t nowLive = GetTickCount64();

        if (lastLive == 0)
            lastLive = nowLive;
        else if (liveOn != 0 && nowLive - lastLive >= 1000)
        {
            lastLive = nowLive;

            const float liveNear = depth_nearest_metres(device, context, depth,
                                                        g_params.zNear, g_params.zFar,
                                                        reversed_depth());
            BVR_INFO("AFW depth liveness: nearest geometry now %.2f m. This must "
                     "MOVE as the player walks; a value that stays put means the "
                     "held buffer is no longer being drawn into.", liveNear);
        }
    }

    /* Ground truth first. Until both real eyes have been seen and compared, the
       caller is told the warp could not run and falls back to AFR - which is
       what makes the measurement possible, since it needs REAL renders. */
    if (calibrating(device, context, srcColor, srcEye, depth, p))
    {
        ++g_failures;
        return false;
    }

    /* The gain the calibration measured against the two real eyes. A zero gain
       no longer reaches here at all - it sets the stand-down instead, because a
       zero-disparity copy is submitted from the OTHER eye's pose and cannot
       fuse. See the calibration's failure branch. */
    p.dispScale *= g_gain;

    /* How far a disocclusion may reach for something to fill itself with.
       Bounded because the scan is per-pixel: wide enough to cross the hole a
       near object opens, narrow enough that a genuinely empty row does not walk
       the whole width looking for company. */
    /* The stretch-the-background fallback, used only before the first real
       render of this eye exists. Wide enough to cross a hole the size of the
       whole cap: a window narrower than the disparity step across a near edge
       leaves the far side of the hole finding nothing, falling back to the pixel
       straight ahead - the occluder - and filled and unfilled columns then
       alternate down the edge. That comb of stripes down a near column was a
       24 px window against a 96 px hole.

       Bounded well below the disparity ceiling on purpose. It used to track the
       cap, but the cap is now 220 and this loop runs per gap pixel - and since
       disocclusions are filled from the previous render, this path only ever
       runs on the first frame or two of a mission, before one exists. Paying
       220 iterations for that is not worth it. */
    p.gapFill = static_cast<float>(config_float("afw_gap_fill_px", 96.0f));

    /* Where the soft roll-off starts. Below it the disparity is EXACT, and that
       band is now nearly the whole useful range: 0.85 of a 220 px ceiling is
       187 px, which is about 0.33 m. At 0.6 it started at 1.1 m and quietly
       inflated everything an arm's length away. The roll-off still exists - it
       is what keeps the last few centimetres from diverging - but it now lives
       where nothing is meant to be looked at. */
    /* Derived from the ceiling unless pinned, and a pin of zero or less means
       "derive it" rather than "put the knee at zero" - which would soft-cap the
       entire range from nothing upward and flatten the whole image. The ceiling
       now tracks the near plane, so this tracks it too: 0.85 of the near-plane
       disparity is a roll-off that begins a few centimetres from the eye and
       governs only geometry clipping through the near plane itself. */
    const double kneeConfigured = config_float("afw_disparity_knee_px", -1.0f);
    p.knee = kneeConfigured > 0.0
                 ? static_cast<float>(kneeConfigured)
                 : p.maxDisparity * 0.85f;

    p.havePrev = ensure_prev_srv(device, prevReal) ? 1.0f : 0.0f;

    /* Pixel intrinsics for this eye, built here because this is where the
       destination size is known. General asymmetric form, which collapses to the
       symmetric one on its own - AFW submits a symmetric frustum, but nothing
       here needs to assume that. x = cx + fx * (X / -Z), y = cy - fy * (Y / -Z). */
    p.havePrevRot = 0.0f;
    if (g_prevRotValid)
    {
        const float fx = p.size[0] / (g_prevTan[1] - g_prevTan[0]);
        const float fy = p.size[1] / (g_prevTan[2] - g_prevTan[3]);

        p.prevCam[0] = fx;
        p.prevCam[1] = fy;
        p.prevCam[2] = -g_prevTan[0] * fx;
        p.prevCam[3] =  g_prevTan[2] * fy;

        for (int c = 0; c < 3; ++c)
        {
            p.prevRot0[c] = g_prevRot[c];
            p.prevRot1[c] = g_prevRot[3 + c];
            p.prevRot2[c] = g_prevRot[6 + c];
        }

        p.havePrevRot = 1.0f;
    }

    /* How far one source pixel may stretch before the gap is treated as a depth
       discontinuity rather than a magnified surface. Clamped here rather than
       trusted, because it bounds a loop on the GPU. */
    float span = static_cast<float>(config_float("afw_max_span_px", 8.0f));
    if (span < 1.0f)  span = 1.0f;
    if (span > 32.0f) span = 32.0f;
    p.maxSpan = span;

    /* How big a disparity jump between two neighbouring depth texels counts as a
       silhouette rather than a steep surface. Defaults to the same number the
       span uses, because they are the same judgement made twice: below it the
       neighbouring texel is the same surface and may be interpolated, above it
       it is something else and must not be. */
    p.depthEdge = static_cast<float>(config_float("afw_depth_edge_px", span));

    /* The same two tests again, as a FRACTION of the disparity. Whichever of the
       two is larger wins, so the fixed value governs the mid field where the
       disparity is small and this governs the near field where it is not.
       A quarter: a real silhouette up close jumps by nearly the whole disparity,
       so a quarter separates the two cases with room to spare either way. */
    float frac = static_cast<float>(config_float("afw_span_fraction", 0.25f));
    if (frac < 0.0f)  frac = 0.0f;
    if (frac > 1.0f)  frac = 1.0f;
    p.spanFrac = frac;

    /* Below this disparity the fixed threshold stands alone. ~96 px is about
       0.68 m at this scale: everything from there outward keeps the behaviour
       that worked, and the scaling exists only nearer than that. */
    p.spanFree = static_cast<float>(config_float("afw_span_free_px", 96.0f));

    /* Below this disparity an ambiguous edge pixel is still allowed to move: the
       whole band shifts by less than a pixel there, so leaving a hole for the
       previous render to fill would cost more than the halo it prevents. Set
       very high to disable the edge holes entirely and take the halo back. */
    p.edgeHoleMin = static_cast<float>(config_float("afw_edge_hole_min_px", 4.0f));

    /* How long a mirrored run the disocclusion fill may paint before it folds
       back on itself. OFF by default: the fold reverses the texture's handedness
       every MirrorMax pixels, so a wide hole shows an inverted half against an
       upright one and the crease between them is the artifact. Set above 0 to
       put it back and compare. */
    p.mirrorMax = static_cast<float>(config_float("afw_fill_mirror_px", 0.0f));

    /* How far into the source the fold-free reflection may reach. Under it the
       fill is an exact 1:1 reflection; past it the offset compresses smoothly
       toward this ceiling, so a hole wider than the reach ends in a squashed
       stretch of far background rather than a legible flipped copy of it. 0
       reflects 1:1 the whole width of the hole, however wide.

       64 px because that is roughly where a reflection stops being read as
       "texture" and starts being read as "that rock again, backwards" - a hole
       narrower than this is filled exactly, and only the wide ones behind a
       near object are compressed at all. */
    p.mirrorReach = static_cast<float>(config_float("afw_fill_mirror_reach_px", 64.0f));

    /* Which way the fill runs. 0 reflects the background into the hole, 1 lays
       it across forward out of the band beside the hole, 2 continues forward
       straight past the neighbour - which is the literal inversion of the
       reflection and reaches into the occluder to do it. See the note in the
       gather; 2 is expected to double the object's edge. */
    p.fillInvert = static_cast<float>(config_float("afw_fill_invert", 1.0f));

    /* How long one forward run is under afw_fill_invert = 1. 0 measures the
       hole and uses its width, so the run covers it exactly once and cannot
       repeat - a fixed run shorter than the hole tiles, and the tiling is
       itself read as the fill being split into pieces. Pin it only to compare. */
    p.invertRun = static_cast<float>(config_float("afw_fill_invert_run_px", 0.0f));

    /* The widest hole that is treated as a CRACK and interpolated across from
       both sides rather than invented from one. It scales with distance without
       being told to: disparity goes as 1/z, so this catches the two- and
       three-pixel gaps that distant silhouettes open and never touches the wide
       band beside something near.

       4 px, which at 63 px of disparity per unit is everything past about
       fifteen metres. Higher starts interpolating real disocclusions, which
       blends the occluder into the gap - a halo. 0 turns it off. */
    p.blendMax = static_cast<float>(config_float("afw_fill_blend_px", 4.0f));

    /* Whether a silhouette pixel whose depth texel straddles two surfaces is
       assigned by COLOUR rather than by which texel centre is nearer. Costs
       three texture reads at those pixels and nothing anywhere else, and does
       nothing at all when the depth is the same size as the colour - there is
       no ambiguity to resolve then. Under an upscaler it is the difference
       between guessing at every edge in the frame and knowing. */
    p.colourPick = static_cast<float>(config_float("afw_depth_colour_pick", 1.0f));

    /* THE UPSCALER'S JITTER, TAKEN BACK OUT OF THE DEPTH LOOKUP.
     *
       Measured in NDC off the engine's own projection - see vp_view_jitter -
       and converted here, because this is where the depth's size is known. NDC
       spans 2 across the image, so one NDC unit is half the width in texels.

       Y is negated: NDC counts upward and texel rows count downward.

       The config value is a SIGN as much as a switch. The conversion above is
       derived, not measured, and a derivation with two sign conventions in it
       (NDC handedness, and which way a jittered projection moves the image)
       can be right in every step and still come out backwards. -1 applies the
       opposite shift, which either fixes the edges or doubles the crawl, and
       either answer settles it in one run. 0 is the old behaviour. */
    /* How wide a disparity gap the colour may WEIGH across instead of voting.
       Below it an edge pixel gets a disparity in proportion to how much it looks
       like each surface, which is both where an antialiased edge belongs and a
       decision that cannot flicker; above it the vote decides, because half of a
       wide gap is a pixel in mid-air. 8 px is about two metres of separation at
       the current disparity - comfortably the far field, nowhere near the band
       beside something close. 0 restores the plain vote everywhere. */
    p.colourSoft = static_cast<float>(config_float("afw_depth_colour_soft_px", 8.0f));

    /* How much of the previous synthesised frame a warped pixel keeps.
     *
       The real eye is stabilised by DLSS's temporal accumulation; the
       synthesised one is rebuilt from scratch every frame and accumulates
       nothing, which is why shadow edges crawl in it and not in the eye beside
       it. This is the warp's own accumulation, confined by a neighbourhood
       clamp so a pixel that really changed cannot ghost.

       0.5 keeps half of a value the clamp has already agreed with. Higher holds
       harder and risks trails on anything the clamp fails to catch; 0 is off. */
    p.taa = static_cast<float>(config_float("afw_taa", 0.5f));

    const double dejitter = config_float("afw_depth_dejitter", 1.0f);

    p.jitter[0] = 0.0f;
    p.jitter[1] = 0.0f;

    float jx = 0.0f, jy = 0.0f;
    if (dejitter != 0.0 && vp_view_jitter(&jx, &jy))
    {
        const float sign     = dejitter < 0.0 ? -1.0f : 1.0f;
        const float depthW   = p.size[0] * p.depthScale[0];
        const float depthH   = p.size[1] * p.depthScale[1];

        p.jitter[0] = sign *  jx * depthW * 0.5f;
        p.jitter[1] = sign * -jy * depthH * 0.5f;

        /* Once a second, because the number is the evidence. A real jitter is a
           fraction of a texel and a different fraction every frame; a reading
           that is pinned near zero means the projection we intercept is not the
           one being jittered, and a reading of several texels means this has
           locked onto something that is not jitter at all. */
        static uint32_t s_lastLog = 0;
        const uint32_t  now = GetTickCount();

        if (now - s_lastLog > 1000u)
        {
            s_lastLog = now;
            BVR_INFO("AFW de-jitter: frustum centre is off by %.4f, %.4f NDC "
                     "this frame = %.2f, %.2f depth texels, taken back out of "
                     "every depth read. Expect a fraction of a texel, changing "
                     "every frame; near-zero means the upscaler is off or its "
                     "jitter is not in the matrix we see.",
                     jx, jy, p.jitter[0], p.jitter[1]);
        }
    }

    D3D11_MAPPED_SUBRESOURCE mapped = {};
    if (FAILED(context->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
    {
        ++g_failures;
        return false;
    }
    std::memcpy(mapped.pData, &p, sizeof(p));
    context->Unmap(g_cb, 0);

    /* State is saved and restored around the dispatch. We are borrowing rgl's
       immediate context in the middle of its frame, and leaving a compute
       shader or a UAV bound behind us is the kind of thing that surfaces three
       systems away as a texture that renders black. */
    ID3D11ComputeShader*       prevShader = nullptr;
    ID3D11Buffer*              prevCb = nullptr;
    ID3D11ShaderResourceView*  prevSrv[3] = { nullptr, nullptr, nullptr };
    /* TWO slots now, not one. The warp binds the claim buffer alongside the
       destination, and saving one while overwriting two leaves rgl's own u1
       unbound afterwards - the kind of thing that surfaces three systems away. */
    ID3D11UnorderedAccessView* prevUav[2] = { nullptr, nullptr };

    context->CSGetShader(&prevShader, nullptr, nullptr);
    context->CSGetConstantBuffers(0, 1, &prevCb);
    context->CSGetShaderResources(0, 3, prevSrv);
    context->CSGetUnorderedAccessViews(0, 2, prevUav);

    ID3D11SamplerState* prevSampler = nullptr;
    context->CSGetSamplers(0, 1, &prevSampler);

    ID3D11ShaderResourceView* srvs[3] = { g_srcSrv, g_depthSrv, g_prevSrv };
    const UINT noOffset = static_cast<UINT>(-1);
    const UINT offsets[2] = { noOffset, noOffset };

    ID3D11UnorderedAccessView* uavs[2] = { g_dstUav, g_lookupUav };

    /* Zero is "nothing landed here", so the claim buffer starts empty every
       frame. Wholesale, on the GPU - a stale claim from the previous frame
       would place last frame's geometry in this one, which is the same class of
       bug as a held image submitted with the wrong pose. */
    const UINT clearTo[4] = { 0u, 0u, 0u, 0u };
    context->ClearUnorderedAccessViewUint(g_lookupUav, clearTo);

    context->CSSetConstantBuffers(0, 1, &g_cb);
    context->CSSetShaderResources(0, 3, srvs);
    context->CSSetSamplers(0, 1, &g_sampler);
    context->CSSetUnorderedAccessViews(0, 2, uavs, offsets);

    /* PASS 1 - every source pixel claims where it lands, InterlockedMax keeps
       the nearest claim. PASS 2 - every destination reads its winner. D3D11
       orders two dispatches on the same context and inserts the UAV barrier
       between them, so the gather cannot observe a half-written buffer. */
    const UINT groupsX = (desc.Width + 7) / 8;
    const UINT groupsY = (desc.Height + 7) / 8;

    context->CSSetShader(g_shader, nullptr, 0);
    context->Dispatch(groupsX, groupsY, 1);

    context->CSSetShader(g_gatherShader, nullptr, 0);
    context->Dispatch(groupsX, groupsY, 1);

    /* Unbind OURS before putting rgl's back: the destination is bound as a UAV
       here and may be bound as a shader resource there, and D3D will silently
       drop one of the two if both are set. */
    ID3D11UnorderedAccessView* nullUav[2] = { nullptr, nullptr };
    ID3D11ShaderResourceView*  nullSrv[3] = { nullptr, nullptr, nullptr };
    context->CSSetUnorderedAccessViews(0, 2, nullUav, offsets);
    context->CSSetShaderResources(0, 3, nullSrv);

    context->CSSetShader(prevShader, nullptr, 0);
    context->CSSetConstantBuffers(0, 1, &prevCb);
    context->CSSetShaderResources(0, 3, prevSrv);
    context->CSSetSamplers(0, 1, &prevSampler);
    context->CSSetUnorderedAccessViews(0, 2, prevUav, offsets);

    if (prevShader != nullptr) prevShader->Release();
    if (prevCb != nullptr) prevCb->Release();
    if (prevSrv[0] != nullptr) prevSrv[0]->Release();
    if (prevSrv[1] != nullptr) prevSrv[1]->Release();
    if (prevSrv[2] != nullptr) prevSrv[2]->Release();
    if (prevUav[0] != nullptr) prevUav[0]->Release();
    if (prevUav[1] != nullptr) prevUav[1]->Release();
    if (prevSampler != nullptr) prevSampler->Release();

    ++g_warps;

    if (!g_loggedFirst)
    {
        g_loggedFirst = true;
        BVR_INFO("AFW live: eye %d rendered, eye %d synthesised from its depth. "
                 "disparity = %.1f px at 1 unit, capped at %.0f px, depth %s, "
                 "depth buffer %.0f%% of the colour. Occlusion resolved by "
                 "SCATTER (nearest claim wins); soft knee at %.0f px so the near "
                 "field compresses instead of flattening; disocclusions filled "
                 "from %s.",
                 srcEye, 1 - srcEye, p.dispScale, p.maxDisparity,
                 p.reversed > 0.5f ? "reversed" : "normal",
                 p.depthScale[0] * 100.0f, p.knee,
                 p.havePrev > 0.5f
                     ? "this eye's own previous render - real geometry, one frame old"
                     : "stretched background (no previous render of this eye yet)");
    }

    /* The line above fires on the FIRST warped frame, when no eye has been
       recorded yet - so it always said "stretched background" no matter what
       happened afterwards, which made it useless for the one thing it was added
       to report. This one fires when the real fill actually starts. */
    if (!g_loggedPrevFill && p.havePrev > 0.5f)
    {
        g_loggedPrevFill = true;
        BVR_INFO("AFW: disocclusions are now filled from this eye's own previous "
                 "render - real geometry, one frame old, instead of background "
                 "stretched sideways across the hole.");
    }

    return true;
}

void afw_log_second()
{
    if (g_warps == 0 && g_failures == 0)
        return;

    BVR_INFO("AFW: %u eye(s) synthesised this second, %u failed. Failures should "
             "be 0; anything else means the mode fell back to AFR's held image "
             "for those frames.", g_warps, g_failures);

    g_warps = 0;
    g_failures = 0;
}

void afw_release()
{
    if (g_lookupUav != nullptr){ g_lookupUav->Release();g_lookupUav = nullptr; }
    if (g_lookup != nullptr)   { g_lookup->Release();   g_lookup = nullptr; }
    if (g_dstUav != nullptr)   { g_dstUav->Release();   g_dstUav = nullptr; }
    for (int i = 0; i < 2; ++i)
    {
        if (g_depthSrvSlot[i] != nullptr) { g_depthSrvSlot[i]->Release(); g_depthSrvSlot[i] = nullptr; }
        g_depthSrvSlotFor[i] = nullptr;
    }
    g_depthSrv     = nullptr;
    g_depthSrvNext = 0;
    if (g_prevSrv != nullptr)  { g_prevSrv->Release();  g_prevSrv = nullptr; }
    if (g_srcSrv != nullptr)   { g_srcSrv->Release();   g_srcSrv = nullptr; }
    if (g_sampler != nullptr)  { g_sampler->Release();  g_sampler = nullptr; }
    if (g_cb != nullptr)       { g_cb->Release();       g_cb = nullptr; }
    if (g_gatherShader != nullptr) { g_gatherShader->Release(); g_gatherShader = nullptr; }
    if (g_shader != nullptr)   { g_shader->Release();   g_shader = nullptr; }

    g_srvFor = nullptr;
    g_depthSrvFor = nullptr;
    g_uavFor = nullptr;
    g_prevSrvFor = nullptr;
    g_lookupW = 0;
    g_lookupH = 0;
}

} // namespace bvr
