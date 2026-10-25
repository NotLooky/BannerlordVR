/* =============================================================================
 * TEST 591: THE GAME'S INTERFACE DRAWN AT k x THE WINDOW.
 *
 * Every Gauntlet mesh is positioned in NDC from its own ScreenSize
 * (two_dimensional_ui.rs: pos / g_ui_screen_size * 2 - 1), so the same draws
 * rasterise into a larger target unchanged - only sharper. rgl will not draw a
 * TwoDimensionView into a render target of ours (TESTs 299, 531), so this goes
 * one level down: while armed, binding the swapchain's backbuffer RTV binds
 * ours instead (k x the size), with viewports and scissors scaled to match and
 * the backbuffer's clears mirrored onto it. The one thing the shaders do in
 * pixels is the clip test (SV_Position against g_ui_clip_rect_info); managed
 * scales those rects by the same k (UiSupersamplePatch).
 *
 * Downstream already takes a larger interface (TEST 299): ui_source_of hands
 * this target to the key, the UI swapchains follow its size, the mirror samples
 * it by ratio.
 * ========================================================================== */
#ifndef BVR_UI_SUPERSAMPLE_H
#define BVR_UI_SUPERSAMPLE_H

#include <d3d11.h>
#include <stdint.h>

namespace bvr {

/* Managed arms it with k > 1 while the interface goes on the overlay; 0 off. */
void  ui_ss_set_factor(float k);

/* 0 off, 1 the interface is being drawn into the dense target, 2 armed but no
   draw has reached the backbuffer in ~2 s (managed then stands down). */
int32_t ui_ss_state();

/* The dense target, when the interface has been drawn into it recently; null
   when the backbuffer is the source. */
ID3D11Texture2D* ui_ss_source();

/* Once per Present, after the interface was read: remembers the backbuffer,
   counts the frame, clears the target for the next one. */
void ui_ss_frame(ID3D11DeviceContext* context, IDXGISwapChain* swapChain);

/* --- from the context detours (vp_patch) --------------------------------- */

/* The RTVs to bind: true and `out` filled (and *dsv dropped) when the first is
   the backbuffer's, or our own being re-bound. */
bool ui_ss_substitute(ID3D11DeviceContext* c, UINT num, ID3D11RenderTargetView* const* rtvs,
                      ID3D11RenderTargetView** out, ID3D11DepthStencilView** dsv);

/* After the bind went through: scaled viewport/scissors on entering our target,
   the engine's own back on leaving it. */
void ui_ss_after_bind(ID3D11DeviceContext* c, bool substituted);

/* The viewports/scissors to pass on: `scratch` scaled while ours is bound. */
const D3D11_VIEWPORT* ui_ss_viewports(ID3D11DeviceContext* c, UINT n, const D3D11_VIEWPORT* vps,
                                      D3D11_VIEWPORT* scratch);
const D3D11_RECT* ui_ss_scissors(ID3D11DeviceContext* c, UINT n, const D3D11_RECT* rects,
                                 D3D11_RECT* scratch);

/* A clear of the backbuffer is also a clear of ours: returns ours, or null. */
ID3D11RenderTargetView* ui_ss_clear_twin(ID3D11RenderTargetView* rtv);

} // namespace bvr

#endif /* BVR_UI_SUPERSAMPLE_H */
