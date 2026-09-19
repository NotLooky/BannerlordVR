/* =============================================================================
 * frame_map - one battle frame, written down completely.
 *
 * WHY THIS EXISTS
 * ---------------
 * Native stereo means running every view-dependent GPU operation of a frame
 * twice, once per eye, with the second eye's constants and its own copies of
 * every surface those operations write. Deciding which operations are
 * view-dependent, which constants carry the camera, and which surfaces carry
 * history from one frame to the next has, until now, been done one census at a
 * time - a counter here, a lane ranking there - and every one of those answered
 * a narrower question than the one being asked.
 *
 * This answers all of them at once, from the engine's own frame:
 *
 *   ops.txt        every draw, dispatch, clear, copy, resolve, indirect draw,
 *                  query, map and DLSS evaluate, in submission order, with
 *                  every binding live at that moment: render targets, depth,
 *                  UAVs, shaders, constant buffers (with their offsets),
 *                  shader resources, viewport, depth/blend/raster state
 *   resources.txt  what every resource id in ops.txt is
 *   views.txt      what every view id is, and which resource it views
 *   shaders\       the disassembly of every shader the frame used
 *   cb.bin         the ORIGINAL bytes of every constant-buffer upload - before
 *                  vp_patch rewrites anything - keyed to its place in ops.txt
 *
 * The disassembly is the part nothing here has had before: it says which
 * constant-buffer lanes a shader actually reads and what it does with them, so
 * "where does the lighting pass reconstruct world position from" stops being a
 * census question and becomes something to read.
 *
 * COST AND SAFETY
 * ---------------
 * Off unless frame_map = 1. When on, it adds inline hooks on the context
 * methods vp_patch does not already own and keeps each shader's bytecode as
 * private data on the shader object (the runtime frees it with the shader, so
 * nothing of ours can outlive what it describes). Outside a capture every hook
 * is one relaxed load and a forward. A capture is a few frames long, and the
 * files are written on a worker thread afterwards.
 *
 * TRIGGER
 * -------
 * frame_map_delay seconds after a mission goes active, once per mission - or
 * whenever Logs\BannerlordVR.FrameMap.trigger exists (it is deleted when seen).
 * ========================================================================== */
#ifndef BVR_FRAME_MAP_H
#define BVR_FRAME_MAP_H

#include <d3d11.h>
#include <stdint.h>

namespace bvr {

/* frame_map = 1 in the config. Read once. */
bool frame_map_enabled();

/* Installs the extra context hooks and the shader-bytecode retention. Idempotent;
   called from vp_install_hooks once the engine's device and context exist. */
void frame_map_install(ID3D11Device* device, ID3D11DeviceContext* context);

/* Once per Present, on the render thread: re-scans the hooked slots (the runtime
   rotates implementations), advances or ends a capture, and starts one when the
   trigger fires. */
void frame_map_present(ID3D11DeviceContext* context);

/* True while frames are being recorded. Every notification below is only worth
   calling when this is true; they re-check it themselves regardless. */
bool frame_map_capturing();

/* Asks for one capture at the next Present, as the trigger file would. `why` is
   logged. Only honoured when frame_map = 1. */
void frame_map_request_capture(const char* why);

/* A draw, with the graphics pipeline's bindings. `args` is preformatted. */
void frame_map_draw(ID3D11DeviceContext* c, const char* kind, const char* args);

/* A dispatch, with the compute pipeline's bindings. */
void frame_map_compute(ID3D11DeviceContext* c, const char* kind, const char* args);

void frame_map_clear_dsv(ID3D11DeviceContext* c, ID3D11DepthStencilView* dsv,
                         UINT flags, float depth, UINT stencil);

/* The ORIGINAL bytes of a constant-buffer upload, before anything rewrites
   them. `offset` is the byte offset of `data` within the buffer. */
void frame_map_cb_upload(ID3D11Resource* r, const void* data, uint32_t bytes,
                         uint32_t offset, const char* how);

/* A Map of anything that is not a constant buffer (those arrive through
   frame_map_cb_upload with their contents). */
void frame_map_map(ID3D11Resource* r, UINT sub, D3D11_MAP type);

/* UpdateSubresource of anything. Constant buffers are dumped with their bytes. */
void frame_map_update(ID3D11Resource* r, UINT sub, const D3D11_BOX* box,
                      const void* data);

/* A DLSS evaluation. The resources are NGX's, not AddRef'd, and not released.
   `kind` names the op: native stereo's second-eye evaluation is recorded as
   NGX-EVALUATE-EYE1 beside the engine's own. */
void frame_map_ngx(ID3D11DeviceContext* c, const char* const* names,
                   ID3D11Resource* const* res, int n, float jitterX, float jitterY,
                   int reset, const char* kind = "NGX-EVALUATE");

} // namespace bvr

#endif /* BVR_FRAME_MAP_H */
