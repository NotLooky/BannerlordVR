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
void ngx_hook_remove();
