#pragma once

#include <cstddef>

#include <d3d11.h>

namespace bvr {

/* TEST 544: the engine's sea pixel shader (water_surface.rs main_ps) rebuilt with
 * specular anti-aliasing. When `code` is one of its 14 shipped variants, returns
 * the replacement bytecode (compiled once from the game's own Shaders\Sources,
 * kept for the life of the process) and true; otherwise false and the engine's
 * shader is created untouched. */
bool water_spec_aa_code(const void* code, size_t len, const void** outCode, size_t* outLen);

/* TEST 548: what the water really costs, measured by NOT doing it. With
 * water_cost_probe = 1, every 3 s the probe moves to the next of four phases -
 * nothing skipped, the ocean's compute passes skipped, the sea's draws skipped,
 * both - and each frame's engine GPU time is credited to the phase it ran in.
 * Per-op timestamps overlap on the GPU and once blamed a 0.05 ms pass for 2 ms;
 * the frame-time difference is the saving that is really there to take.
 *
 * water_probe_tag   at shader creation, with the ENGINE's bytecode
 * water_probe_skip  before a draw/dispatch: true = do not issue it
 * water_probe_open  when a frame's GPU timing opens: the phase to credit, or -1
 * water_probe_sample / water_probe_frame  the timing result; once per frame */
void water_probe_tag(void* shader, const void* code, size_t len);
bool water_probe_skip(ID3D11DeviceContext* c, bool compute);
int  water_probe_open();
void water_probe_sample(int phase, double engineMs);
void water_probe_frame();

} // namespace bvr
