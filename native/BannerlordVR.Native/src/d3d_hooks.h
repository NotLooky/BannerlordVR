/* =============================================================================
 * D3D11 / DXGI interception.
 *
 * The engine creates its ID3D11Device during startup, long before modules load,
 * so we cannot pre-empt it. Instead we hook IDXGISwapChain::Present and take the
 * device from the first swapchain that comes through. Present is also the only
 * place with a guaranteed "this frame's GPU work is recorded" signal, which is
 * exactly the cadence the OpenXR frame loop needs - so the same hook serves both
 * purposes.
 * ========================================================================== */
#ifndef BVR_D3D_HOOKS_H
#define BVR_D3D_HOOKS_H

#include <d3d11.h>
/* ID3D11Multithread lives here, not in d3d11.h. */
#include <d3d11_4.h>
#include <dxgi.h>
#include <stdint.h>

namespace bvr {

/* Called on the render thread from inside the Present hook, before the original
   Present runs. Never called with a null device. */
using PresentTickFn = void (*)(IDXGISwapChain* swapChain,
                               ID3D11Device* device,
                               ID3D11DeviceContext* context);

/* Installs the Present hook. Safe to call twice. Returns false if MinHook or the
   dummy-swapchain probe failed - the caller must then stay in flat mode. */
bool install_present_hook(PresentTickFn tick);

void remove_hooks();

/* The engine's device, or null until the first Present has been observed. */
ID3D11Device* engine_device();
ID3D11DeviceContext* engine_context();

/* Serialises access to the engine's immediate context, which we share with rgl's
   own rendering. Null if the interface was unavailable. */
ID3D11Multithread* engine_multithread();

/* Adapter LUID of engine_device(). Zeroed until the device is captured. */
LUID engine_adapter_luid();

/* True only once install_present_hook has RETURNED.
 *
 * The Present detour goes live the instant MH_EnableHook succeeds, several
 * statements before install_present_hook finishes - so the render thread can
 * enter our tick and start calling MH_CreateHook while the main thread is
 * still inside MinHook finishing the Present hook. One run did exactly that
 * (its "Present hooked" line printed AFTER the context hooks) and every single
 * MinHook call in the process failed with MH_ERROR_MEMORY_ALLOC, taking the
 * eye-texture capture down with it. Anything that installs hooks from inside
 * the tick must wait for this. */
bool present_hook_ready();

/* --- what the crash logger needs to place a fault -------------------------
 *
 * A fault offset inside rgl says nothing on its own; the same one has now been
 * reported from two different investigations. What separates them is WHERE the
 * faulting thread was: our render-thread work, or the engine's own. These three
 * answer that, and all are safe to call from a vectored exception handler -
 * no allocation, no locks, no D3D calls. */

/* The thread the Present hook last ran on, or 0 if it never has. */
uint32_t present_thread_id();

/* True when THIS thread is currently inside our Present tick. Thread-local, so
   asking it from the faulting thread says whether the fault happened underneath
   our own frame work or merely alongside it. */
bool in_present_on_this_thread();

/* Frames presented so far, for a rough "how long had this been running". */
uint64_t present_frame_count();

/* --- vtable hooking ------------------------------------------------------
 * Replaces one COM vtable entry, saving what was there as the "original".
 *
 * Why not MinHook for these. MinHook patches the FUNCTION, which needs a
 * trampoline allocated within reach of the target. That works while the targets
 * live in d3d11.dll - one run hooked Map at 00007FF8B74A35B0 and everything
 * worked. But the D3D11 runtime does not always dispatch through module code:
 * later runs found Map at 000001DC5E182CD0, a HEAP address, and every
 * MH_CreateHook returned MH_ERROR_MEMORY_ALLOC because there was no room for a
 * trampoline near it. That took down the eye-texture capture too, and the
 * headset went blank.
 *
 * Writing the vtable entry allocates nothing, so it cannot fail that way. It
 * also matches what we are actually fighting: the runtime swaps these entries
 * between two implementations as device state changes, and re-writing a pointer
 * costs nothing to repeat every frame - where re-creating a MinHook trampoline
 * every frame is what drained the pool in the first place.
 *
 * Writes *original before publishing the detour, so a call arriving mid-install
 * can never see a hooked slot with a stale original. */
bool vtable_hook(void** vtable, int index, void* detour, void** original);

/* Puts `value` back into a vtable slot, handling page protection. */
bool vtable_write(void** slot, void* value);

/* Present calls seen so far. Used to detect the "engine uses Present1, not
   Present" case, which otherwise looks like a silent hang. */
uint64_t present_count();

/* --- eye texture correlation (Phase 4) ----------------------------------
 * Texture.Pointer gives an rgl-internal address, not an ID3D11Texture2D, and
 * reverse-engineering rgl's layout would break on every game patch. Instead we
 * hook ID3D11Device::CreateTexture2D and watch a narrow window:
 *
 *     C#: bvr_begin_texture_capture(eye, w, h)
 *     C#: Texture.CreateRenderTarget(...)      <- the hook sees this one
 *     C#: bvr_end_texture_capture(eye)
 *
 * Any render-target texture of exactly the requested size created while armed is
 * recorded. Exactly one match is expected; 0 or >1 is reported rather than
 * guessed at, because picking the wrong texture shows up as a black or garbage
 * eye rather than as an error.
 */
bool arm_texture_capture(int32_t eye, uint32_t width, uint32_t height);

/* Disarms and returns BVR_OK when exactly one texture matched. */
int32_t finish_texture_capture(int32_t eye);

/* AddRef'd eye texture, or null. */
ID3D11Texture2D* eye_texture(int32_t eye);

/* True once both eyes hold a texture - the gate for the copy path. */
bool eye_textures_ready();

void release_eye_textures();

/* --- the eye's depth buffer (AFW) ----------------------------------------
 *
 * AFW synthesises the second eye by reprojecting the first one, and that needs
 * the depth the first was drawn with. We cannot ask for it: AFR retargets the
 * ENGINE'S OWN scene view and lets rgl auto-create the depth target, so the
 * texture is never named on our side of the boundary.
 *
 * So watch the binding instead. When rgl binds our eye colour target as render
 * target 0, whatever depth-stencil view goes with it in the same call is, by
 * definition, the depth for that render. That is true regardless of how rgl
 * allocates it or how many eye-sized depth buffers exist, which is what makes
 * this better than matching on the size of a CreateTexture2D.
 *
 * Cost is kept near zero by only looking once per frame: the Present hook arms
 * it, the first matching bind disarms it, and every other call through this
 * detour - hundreds per frame - falls out on an atomic load.
 */
void arm_depth_capture();

/* Clear the game window to black the first time it is bound each frame, so the
   interface is drawn onto black instead of onto a stale picture. Pass nullptr
   to stop. See clear_backbuffer_if_bound. */
void arm_backbuffer_clear(ID3D11Texture2D* backbuffer);

/* Call once per frame; re-arms the one-shot clear. */
void begin_backbuffer_frame();

/* Dimensions of the depth buffer above. rgl renders the scene at half the eye
   size and upscales, so this is NOT the eye size and the warp has to scale its
   lookups. Zero when no depth has been found. */
void eye_depth_size(uint32_t* width, uint32_t* height);

/* AddRef'd depth texture that was bound with the eye target, or null. */
/* Drops the depth buffer currently held and re-opens the search for one.
   Called when rgl reallocates its render targets - changing an upscaler does
   this - because the held pointer is then a buffer nothing writes to any
   more, and a frozen depth map makes every disparity wrong at once. */
void depth_rearm(const char* why);

/* Rejects the depth target currently held and keeps looking, remembering it so
   the next bind does not hand back the same one. For when a candidate turns out
   not to describe the scene - rgl binds several eye-sized depth targets per
   frame and they are indistinguishable by size. */
void depth_reject_current(const char* why);

/* Increments whenever the held depth is dropped, by either of the two calls
   above. Anything that caches a conclusion about a depth texture must store
   this alongside the pointer and re-derive when it moves: D3D11 reuses freed
   addresses, so a pointer that still compares equal after a scene change can
   be a different texture entirely. */
uint32_t depth_generation();

ID3D11Texture2D* eye_depth_texture();

/* Whether an OpenXR session is currently pacing the frame loop, published by
   xr_context because only it knows. While it is true the Present hook drops the
   engine's sync interval to 0: xrWaitFrame is then the only pacer, instead of
   two of them at different rates handing the head a latency that changes every
   frame. See the note in hooked_present. */
void set_xr_pacing(bool running);

} // namespace bvr

#endif /* BVR_D3D_HOOKS_H */
