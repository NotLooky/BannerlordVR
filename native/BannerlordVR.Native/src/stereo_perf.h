/* =============================================================================
 * stereo_perf - where a native-stereo frame's time goes (TEST 181).
 *
 * TEST 164 ran at 27 fps and TEST 165's run at 25, against an engine eye that
 * holds 90-120 fps alone. Running ~58% of a frame twice explains ~50-65 fps; the
 * other ~20 ms a frame had no number attached to it, and this project has spent
 * too many tests arguing about quantities nobody measured. So one line every
 * ~5 s, while native stereo is running, says:
 *
 *   - the frame period, and per frame on the render thread: our Present tick,
 *     the real Present call, the stereo code's own decide/issue cost, and time
 *     blocked in Map(READ) - a readback waiting on the GPU
 *   - GPU timestamps for the engine's frame and for our Present tick
 *   - VRAM in use against the budget, and what the twins take
 *   - the busiest threads of the process by CPU, so a render thread or a driver
 *     thread pinned at 100% names itself
 *
 * Nothing here runs outside native stereo: every entry point is one relaxed load
 * and a return, and no query, timer or thread exists until the mode is used.
 * stereo_perf = 0 turns it off.
 * ========================================================================== */
#ifndef BVR_STEREO_PERF_H
#define BVR_STEREO_PERF_H

#include <d3d11.h>
#include <stdint.h>

namespace bvr {

/* Present hook: around our tick, then once the real Present has returned. */
void stereo_perf_tick_begin(ID3D11DeviceContext* ctx);
void stereo_perf_tick_end(ID3D11DeviceContext* ctx);
void stereo_perf_present_returned();

/* Around a Map. Returns 0 and times nothing unless native stereo is running and
   the map reads GPU data back. */
int64_t stereo_perf_map_begin(D3D11_MAP type);
void    stereo_perf_map_end(int64_t t0, ID3D11Resource* r);

/* Around NGX evaluations (ngx_hook): point 0 before the game's, 1 after it, 2
   after eye 1's; and the CPU time of each evaluation call. Nothing outside native
   stereo. */
void stereo_perf_ngx_stamp(ID3D11DeviceContext* ctx, int point);
void stereo_perf_ngx_cpu(int64_t ticks);
/* Every ~5 s from stereo_frame_boundary, with the stereo code's own numbers. */
void stereo_perf_report(double decideMsPerFrame, double issueMsPerFrame, uint64_t frames,
                        uint64_t twinBytes);

} // namespace bvr

#endif /* BVR_STEREO_PERF_H */
