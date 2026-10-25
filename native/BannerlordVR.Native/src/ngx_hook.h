#pragma once

/* THE UPSCALER'S MOTION VECTORS, WHICH ARE THE ONE THING LEFT WE CANNOT REACH
   THROUGH A CONSTANT BUFFER.
 *
   TESTs 79-106 established the whole of the remaining problem in one sentence:
   vp_patch applies a head rotation D to the IMAGE, the engine's motion vectors
   do not know about it, and a temporal upscaler reads both. Writing D into the
   per-view matrices lands it on static and dynamic geometry alike - TEST 106
   proved they share that buffer - so no arrangement of those matrices separates
   the two. Every setting from here is a choice about where to put the same half
   degree, not a fix.

   The fix is to correct the motion vectors themselves, and they are not in a
   constant buffer: they are a texture handed straight to DLSS through NGX. So
   this hooks NVSDK_NGX_D3D11_EvaluateFeature, which is the single call that
   carries every input DLSS gets.

   THIS FILE IS RECONNAISSANCE FIRST AND A CORRECTION SECOND, deliberately. The
   correction needs the motion vectors' format, their resolution relative to the
   output, their scale convention (pixels or NDC, and with which sign), and
   whether the texture can be written at all. Guessing any one of those produces
   a shader that runs, writes plausible numbers, and is wrong - which is the
   failure mode this project has hit repeatedly. So the first build only reads
   and reports. */

bool ngx_hook_install();      /* idempotent; safe to call every frame */

/* Cheeky Foveated DLSS was started this launch (bvr_api start_cheeky_host), so
   evaluations take the Cheeky path in ngx_hook.cpp. */
void ngx_set_cheeky_running();

/* TEST 797: the VR panel's dlss_preset (0 auto, 10-13 J-M, -1 DLSS's own), used
   from the next DLSS create on. */
void ngx_set_preset(int preset);

void ngx_hook_remove();

/* TEST 805: after a TEST 800 DLSS dump, saves the same corner of the image eye 0
   submits. Call with the finished eye-0 texture; does nothing otherwise. */
struct ID3D11DeviceContext;
struct ID3D11Texture2D;
void ngx_dump_final(ID3D11DeviceContext* ctx, ID3D11Texture2D* tex, int stage = 1);   /* TEST 814: 0 = before our sharpener */

/* The mode of the game's most recent DLSS evaluation, for log labels ("DLSS
   Performance", "DLAA", ...), or "no DLSS" when nothing was evaluated in the last
   second. Any thread. */
const char* ngx_mode_name();

/* THIS FRAME'S SUB-PIXEL JITTER, FROM DLSS'S OWN PARAMETER BLOCK.
 *
   In RENDER-resolution pixels, which is the resolution rgl draws the scene - and
   so the depth buffer - at, so it needs no conversion to be used as a depth-texel
   offset.

   DR reads the depth at the COLOUR's coordinates and has to take this back out,
   or every silhouette pixel is assigned the wrong surface by a fraction of a
   pixel, differently every frame, which is a shimmer. It used to get the number
   from vp_view_jitter, which derives it from the frustum inside the vp_patch
   scan - and that scan does not run under `mode = engine-camera` with
   `vp_patch = 0`, so the de-jitter has been silently off since the late latch
   replaced the patch. This is the authority in any case: it is the number DLSS
   itself was handed.

   False when no evaluation has been seen in the last second, which is also the
   answer when the upscaler is off. Any thread. */
bool ngx_jitter(float* px, float* py);
