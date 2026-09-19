/* =============================================================================
 * stereo_dup - both eyes from ONE engine frame, by running every view-dependent
 *              GPU operation twice.
 *
 * WHAT THE FRAME MAP ESTABLISHED (TEST 163, capture 20260913_174929)
 * -----------------------------------------------------------------
 * The engine renders one frame on ONE thread: a depth prepass, a six-target
 * G-buffer, four shadow cascades, light culling, lighting into an HDR target, a
 * post chain, and a final composite into the eye texture. Three constant buffers
 * carry the camera, at sites recovered from the buffers themselves:
 *
 *     1760 B  camera position (+0,+16), view (+112), view-projection (+240),
 *             previous view-projection (+304), an inverse/ray basis (+688,
 *             holding the position at +720), camera-to-world (+928, position
 *             at +976)
 *      128 B  view-projection (+0)          - re-uploaded per pass
 *      224 B  view (+64), inverse (+128, position at +176)  - the compute passes
 *
 * Everything else the engine uploads is WORLD space: object matrices, bones,
 * instances, lights. The projection matrices consume world positions - verified
 * numerically, the camera maps to w = 0 - so moving the eye moves only the
 * camera matrices. That is the whole of the constant work.
 *
 * THE SECOND EYE IS A TRANSLATION. The runtime hands out two eye poses with
 * IDENTICAL orientation and identical symmetric fov, 65 mm apart. So for an eye
 * offset d, in row-vector convention:
 *
 *     world->clip, world->view      M' = T(-d) * M      (row 3 only)
 *     clip->world, camera->world    M' = M * T(+d)
 *     camera position               v' = v + d
 *
 * WHICH OPERATIONS RUN TWICE (TEST 165 - the rule as the first run corrected it)
 * ---------------------------------------------------------------------------
 * An operation is view-dependent when the shader bound to it DECLARES a
 * constant buffer whose current contents are the MAIN VIEW, or DECLARES a
 * texture whose contents differ between the eyes, or tests against / writes
 * into a surface whose contents differ between the eyes.
 *
 * TEST 164 duplicated 98.6% of the frame, and the offline replay of its capture
 * (scratchpad taint_sim.py) named both causes:
 *
 *   - "Main view" meant "any camera site verified". The shadow cascades upload
 *     the 1760-byte buffer with the LIGHT's view-projection but the PLAYER's
 *     position still in it (for LOD), so every cascade draw counted as the main
 *     view and all 877 of them ran twice. Only a verified world->clip or
 *     world->view site makes an upload the main view now: 38% of the frame's
 *     draws go back to running once, and the shadow atlas is shared again.
 *   - A surface, once twinned, tainted every reader forever; and every BOUND
 *     slot counted, including textures the engine had left bound and the shader
 *     never reads. Now only the slots the shader's bytecode declares count
 *     (parsed at creation), and a surface carries a state:
 *
 *       NONE     never differed; no twin
 *       SYNC     twin holds exactly what the original holds
 *       STALE    twin is out of date; the original's contents are shared
 *       DIVERGED twin holds the OTHER eye's version - reading it is per-eye
 *
 *     A clear of the whole surface returns it to SYNC; a write by an operation
 *     that is NOT view-dependent makes a SYNC twin STALE; a view-dependent write
 *     re-syncs a STALE twin BEFORE the original runs, then marks it DIVERGED.
 *
 * PER SUBRESOURCE (TEST 181). The state is kept per mip level and array slice,
 * not per resource. The shadow atlas is a four-slice array cleared one slice at
 * a time; tracked as one surface, a single diverged write left it DIVERGED for
 * the rest of the mission - no whole-resource clear ever comes - so every
 * cascade draw ran twice. Measured: 93-97% of the frame re-issued live, 57.7%
 * when the same rule replays the same capture from clean states.
 *
 * DLSS WRITES OUTSIDE ALL OF THIS (TEST 181). NGX produces its output with no
 * draw or dispatch these hooks see, so the eye-1 copy of it never existed and
 * the second eye was composited from the FIRST eye's resolved image. ngx_hook
 * now evaluates DLSS a second time for eye 1 through the calls below.
 *
 * OUR OWN WORK IS NOT THE ENGINE'S. Everything issued inside our Present tick
 * (staging copies, the mirror, the warp) is ignored here: copying a diverged
 * eye image into a staging texture of ours must not twin that texture.
 *
 * TWINS BELONG TO THE RESOURCE. A twin is attached to its original with
 * SetPrivateDataInterface, so the runtime frees it when the original dies and a
 * recycled pointer can never hand back a stale twin. That is the failure this
 * project has hit repeatedly with pointer-keyed tables.
 * ========================================================================== */
#ifndef BVR_STEREO_DUP_H
#define BVR_STEREO_DUP_H

#include <d3d11.h>
#include <d3d11_1.h>
#include <stddef.h>
#include <stdint.h>

namespace bvr {

/* Published from managed: true only while single-frame native stereo is the
   selected mode. Everything below is inert otherwise. */
void stereo_set_active(bool active);
bool stereo_active();

/* A constant-buffer upload, with the bytes the GPU will see. Builds the second
   eye's copy when the upload is the main view, and records whether it is -
   which is what makes the operations that bind it view-dependent. */
void stereo_on_cb_upload(ID3D11Resource* resource, const void* data, uint32_t bytes);

/* A CPU write into a non-constant resource. Mirrored onto the twin when the
   surface has one, so the CPU's part of it stays the same in both eyes. */
void stereo_on_update(ID3D11DeviceContext* c, ID3D11Resource* r, UINT sub,
                      const D3D11_BOX* box, const void* data, UINT rowPitch,
                      UINT depthPitch);

/* A CPU write (Map/Unmap) into a buffer that is NOT a constant buffer.
   stereo_wants_buffer_write says, at Map, whether the write is one native stereo
   corrects for eye 1 - today the per-mesh previous-frame transforms
   (g_meshf1_buffer, 304-byte stride), whose previous camera lands in every
   dynamic mesh's velocity. stereo_on_buffer_write is called at Unmap with the
   bytes; the corrected copy reaches the shaders when the engine copies this
   buffer into the one they read. */
bool stereo_wants_buffer_write(ID3D11Resource* r);

/* DELAYED TINY READBACKS (TEST 185). A copy into a small CPU-read staging texture
   is redirected into one of three of ours (stereo_readback_copy_dst returns the
   destination to use, not AddRef'd); a Map(READ) of that staging texture is
   served from the newest one the GPU has finished, without waiting
   (stereo_readback_map returns the texture actually mapped - Unmap that one -
   or null to map normally). Native stereo only. */
typedef HRESULT (STDMETHODCALLTYPE* StereoMapFn)(ID3D11DeviceContext*, ID3D11Resource*, UINT,
                                                 D3D11_MAP, UINT, D3D11_MAPPED_SUBRESOURCE*);
ID3D11Resource* stereo_readback_copy_dst(ID3D11Resource* dst);
ID3D11Resource* stereo_readback_map(ID3D11DeviceContext* c, ID3D11Resource* r, UINT sub,
                                    D3D11_MAP type, UINT flags, D3D11_MAPPED_SUBRESOURCE* m,
                                    StereoMapFn origMap, HRESULT* hr);
void stereo_on_buffer_write(ID3D11Resource* r, const void* data, uint32_t bytes);

/* Called from shader creation with the bytecode: records which constant
   buffer, texture and UAV slots the shader actually declares. */
void stereo_note_shader(ID3D11DeviceChild* shader, const void* code, size_t len);

constexpr int kStereoSwapMax = 96;
constexpr int kStereoOutSlots = D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT;   /* 8 */

struct StereoSwap
{
    uint8_t stage;      /* 0 VS 1 HS 2 DS 3 GS 4 PS 5 CS */
    uint8_t isSrv;
    uint8_t slot;
    uint8_t pad;
    void*   orig;       /* held: released by stereo_end */
    void*   twin;       /* SRV: held; constant buffer: owned by the table */
};

/* State carried between the halves of one duplicated operation. POD; lives on
   the caller's stack inside a draw hook. */
struct StereoOp
{
    bool duplicate;
    bool compute;

    ID3D11RenderTargetView*    rtv[kStereoOutSlots];
    ID3D11DepthStencilView*    dsv;
    ID3D11UnorderedAccessView* uav[kStereoOutSlots];

    ID3D11RenderTargetView*    trtv[kStereoOutSlots];
    ID3D11DepthStencilView*    tdsv;
    ID3D11UnorderedAccessView* tuav[kStereoOutSlots];

    int        nSwap;
    StereoSwap swap[kStereoSwapMax];

    int64_t    t0;
};

/* Reads the bindings, decides whether this operation is view-dependent, and -
   if it is - makes sure every surface it is about to write has a twin holding
   the right contents. Call BEFORE the operation runs.

   Returns false when the operation is not view-dependent; nothing is held and
   the caller issues it once, as normal. */
bool stereo_begin(ID3D11DeviceContext* c, StereoOp& op, bool compute);

/* Swaps in the second eye's targets, twins and constants. The caller then
   re-issues the identical draw or dispatch. */
void stereo_bind(ID3D11DeviceContext* c, StereoOp& op);

/* Puts the engine's own bindings back and releases everything held. */
void stereo_end(ID3D11DeviceContext* c, StereoOp& op);

/* The operations that are not draws but still write surfaces. Each keeps the
   twin and the surface state right. All are called BEFORE the original runs. */
void stereo_clear_rtv(ID3D11DeviceContext* c, ID3D11RenderTargetView* v, const FLOAT col[4]);
void stereo_clear_dsv(ID3D11DeviceContext* c, ID3D11DepthStencilView* v, UINT flags,
                      FLOAT depth, UINT8 stencil);
void stereo_clear_uav_f(ID3D11DeviceContext* c, ID3D11UnorderedAccessView* v, const FLOAT v4[4]);
void stereo_clear_uav_u(ID3D11DeviceContext* c, ID3D11UnorderedAccessView* v, const UINT v4[4]);
void stereo_copy_resource(ID3D11DeviceContext* c, ID3D11Resource* dst, ID3D11Resource* src);
void stereo_copy_region(ID3D11DeviceContext* c, ID3D11Resource* dst, UINT dstSub, UINT x,
                        UINT y, UINT z, ID3D11Resource* src, UINT srcSub, const D3D11_BOX* box);
void stereo_generate_mips(ID3D11DeviceContext* c, ID3D11ShaderResourceView* v);

/* ClearView (D3D11.1). A clear of the whole view when rects is null or n is 0. */
void stereo_clear_view(ID3D11DeviceContext1* c, ID3D11View* v, const FLOAT col[4],
                       const D3D11_RECT* rects, UINT n);

/* The other eye's version of a texture the engine rendered this frame - its
   twin - or null when that texture does not differ between the eyes. AddRef'd.
   The eye image and the eye's scene depth are both read through this. */
ID3D11Texture2D* stereo_twin_texture(ID3D11Texture2D* original);

/* --- for work that writes outside the hooked calls (DLSS) -------------------

   The twin holding eye 1's contents, AddRef'd, or null when no part of the
   resource differs between the eyes. */
ID3D11Resource* stereo_twin_for_read(ID3D11Resource* original);

/* The twin to write eye 1's result into, created when missing (not filled).
   AddRef'd, or null when the resource cannot have one. */
ID3D11Resource* stereo_twin_for_write(ID3D11DeviceContext* c, ID3D11Resource* original);

/* After an external write of the whole resource: `produced` = its twin now holds
   eye 1's version (DIVERGED); otherwise the twin is out of date (STALE) and eye 1
   reads the original. */
void stereo_mark_output(ID3D11Resource* original, bool produced);

/* Suppresses all tracking and duplication on this thread while alive. */
struct StereoIgnore
{
    StereoIgnore();
    ~StereoIgnore();
    StereoIgnore(const StereoIgnore&) = delete;
    StereoIgnore& operator=(const StereoIgnore&) = delete;
};

/* True only while this thread is inside a re-issue, a twin sync or a twin
   write of ours. The render-target hooks skip their bookkeeping then: those
   binds are transient and are undone before the engine draws again. Never true
   outside native stereo. */
bool stereo_in_dup();

/* Per-Present: rolls counters and reports. */
void stereo_frame_boundary();

} // namespace bvr

#endif /* BVR_STEREO_DUP_H */
