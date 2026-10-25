/* =============================================================================
 * bvr_api.h - the ONLY surface between BannerlordVR.dll (C#/Mono) and
 *             BannerlordVR.Native.dll (C++/MSVC).
 *
 * Contract rules. Break these and Mono will corrupt memory rather than error:
 *   - extern "C", __cdecl, no C++ types, no exceptions across the boundary.
 *   - Blittable POD only. No bool (int32_t 0/1), no char* returns, no STL.
 *   - Every function returns int32_t BvrStatus except explicit void ones.
 *   - No callbacks into managed code. C# polls; C++ never calls back. Reverse
 *     P/Invoke from the render thread under Mono is a reliable way to crash.
 *   - Mirrored by managed/BannerlordVR/Interop/NativeBridge.cs. Both files
 *     change in the same commit, always.
 * ========================================================================== */

#ifndef BVR_API_H
#define BVR_API_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef BVR_BUILDING_DLL
#    define BVR_API __declspec(dllexport)
#  else
#    define BVR_API __declspec(dllimport)
#  endif
#  define BVR_CALL __cdecl
#else
#  define BVR_API
#  define BVR_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* --- status codes ------------------------------------------------------- */
typedef enum BvrStatus {
    BVR_OK                 =  0,
    BVR_NOT_INITIALIZED    = -1,
    BVR_NO_RUNTIME         = -2,   /* no OpenXR runtime registered           */
    BVR_NO_HEADSET         = -3,   /* runtime present, no HMD connected      */
    BVR_GRAPHICS_MISMATCH  = -4,   /* engine adapter LUID != runtime adapter */
    BVR_SESSION_NOT_READY  = -5,
    BVR_XR_ERROR           = -6
} BvrStatus;

typedef enum BvrEye {
    BVR_EYE_LEFT  = 0,
    BVR_EYE_RIGHT = 1
} BvrEye;

typedef enum BvrHand {
    BVR_HAND_LEFT  = 0,
    BVR_HAND_RIGHT = 1
} BvrHand;

/* --- POD types ---------------------------------------------------------- */
typedef struct BvrQuat { float x, y, z, w; } BvrQuat;
typedef struct BvrVec3 { float x, y, z;    } BvrVec3;

/* OpenXR-style signed half-angles in radians; left/down are negative. */
typedef struct BvrFov { float angleLeft, angleRight, angleUp, angleDown; } BvrFov;

/* One eye, in OpenXR space (Y up, -Z forward). Conversion to Bannerlord's
   Z-up / Y-forward basis happens exactly once, in managed VrMath. */
typedef struct BvrEyeView {
    BvrQuat orientation;
    BvrVec3 position;
    BvrFov  fov;
} BvrEyeView;

typedef struct BvrHandState {
    BvrQuat aimOrientation;
    BvrVec3 aimPosition;
    BvrQuat gripOrientation;
    BvrVec3 gripPosition;
    BvrVec3 linearVelocity;   /* m/s, for swing detection (Phase 6 Stage B) */
    float   trigger;          /* 0..1  */
    float   grip;             /* 0..1  */
    float   thumbstickX;      /* -1..1 */
    float   thumbstickY;      /* -1..1 */
    int32_t buttons;          /* bitfield */
    int32_t isActive;         /* 0/1 */
} BvrHandState;

typedef struct BvrInputState {
    BvrHandState left;
    BvrHandState right;
} BvrInputState;

/* --- lifecycle (Phase 2) ------------------------------------------------ */

/* Writes an ASCII version string into buffer. Returns bytes written, or a
   negative BvrStatus. Not null-terminated; the caller uses the length. */
BVR_API int32_t BVR_CALL bvr_get_version(uint8_t* buffer, int32_t bufferLen);

/* Creates XrInstance and queries the system. Safe to call when no runtime is
   installed - returns BVR_NO_RUNTIME rather than failing hard. */
BVR_API int32_t BVR_CALL bvr_init(void);

/* Creates XrSession once the engine's ID3D11Device has been captured by the
   Present hook. Compares adapter LUIDs and returns BVR_GRAPHICS_MISMATCH if the
   runtime wants a different GPU than the one the engine picked. */
BVR_API int32_t BVR_CALL bvr_create_session(void);

BVR_API int32_t BVR_CALL bvr_get_recommended_size(uint32_t* width, uint32_t* height);

/* Pumps the XrEventDataBuffer queue and reports the current XrSessionState.
   Must be called every frame or the runtime will consider us unresponsive. */
BVR_API int32_t BVR_CALL bvr_poll_events(int32_t* outSessionState);

/* --- per-frame (Phases 3 & 4) ------------------------------------------- */

/* Most recent poses published by the render thread's xrWaitFrame/xrLocateViews.
   views must point at 2 elements. Non-blocking: reads a double buffer. */
BVR_API int32_t BVR_CALL bvr_get_predicted_views(BvrEyeView* views, int64_t* outDisplayTime);

/* Begins the OpenXR frame before the engine renders it - waits on the
   compositor, begins the frame, locates the views for the display time of the
   frame about to be drawn, and publishes that pose for bvr_get_predicted_views.
   Call once per frame from the game thread, at the top of the camera build.

   BVR_OK when a frame was begun; BVR_SESSION_NOT_READY when there was nothing
   to begin, which is normal and not an error - the Present hook then does
   Wait/Begin/Locate itself, exactly as it did before this existed. */
BVR_API int32_t BVR_CALL bvr_prepare_frame(void);

/* Correlation hook for extracting the ID3D11Texture2D behind an engine render
   target. Arm immediately before calling Texture.CreateRenderTarget from C#,
   disarm immediately after; the CreateTexture2D hook records any matching
   texture created in that window. Avoids depending on rgl's struct layout. */
BVR_API int32_t BVR_CALL bvr_begin_texture_capture(int32_t eye, uint32_t width, uint32_t height);
BVR_API int32_t BVR_CALL bvr_end_texture_capture(int32_t eye);

/* The same bracket for the interface render target (TEST 299). C# allocates a
   BVR_UI target k times the game window, points the Gauntlet views at it, and
   native keys and submits that instead of the backbuffer - so the interface is
   rasterised at the headset's own pixel density without resizing the window.

   bvr_set_ui_source(1) each frame the views are swapped, 0 otherwise. */
BVR_API int32_t BVR_CALL bvr_begin_ui_capture(uint32_t width, uint32_t height);
BVR_API int32_t BVR_CALL bvr_end_ui_capture(void);
BVR_API int32_t BVR_CALL bvr_set_ui_source(int32_t fromTarget);

/* TEST 531: the campaign map bar drawn into its own render target (BVR_HAND),
   so it never reaches the head panel and nothing there is punched for it. The
   same capture bracket as the UI target; then bvr_set_hand_source(1) while the
   MapBar layer's view is swapped to it. bvr_get_hand_source_state: 0 off,
   1 checking, 2 content seen, 3 still empty after ~3 s (C# then swaps the view
   back and the palette is cropped from the interface as before). */
BVR_API int32_t BVR_CALL bvr_begin_hand_capture(uint32_t width, uint32_t height);
BVR_API int32_t BVR_CALL bvr_end_hand_capture(void);
BVR_API int32_t BVR_CALL bvr_set_hand_source(int32_t live);
BVR_API int32_t BVR_CALL bvr_get_hand_source_state(void);

/* Publishes the poses the frame was ACTUALLY rendered with, so xrEndFrame
   reprojects against those rather than against newer predictions. Submitting a
   fresher pose than the one used produces subtle swimming that is easy to
   misdiagnose as tracking jitter. */
BVR_API int32_t BVR_CALL bvr_set_used_views(const BvrEyeView* views, int64_t displayTime);

/* --- alternate-frame rendering ------------------------------------------
 * The engine cannot render one scene into two views at once, so AFR renders a
 * single view and alternates which eye it represents. Managed code calls this
 * BEFORE the frame the engine is about to draw, naming the eye that frame
 * belongs to; the Present hook then keeps that eye's image and re-submits the
 * other eye's last one.
 *
 * Pass -1 to leave AFR mode. */
BVR_API int32_t BVR_CALL bvr_set_afr_eye(int32_t eye);

/* 1 when the eye index last published was consumed by a real scene capture, 0
   when the tick produced no render. The caller must only advance its alternation
   when this reads 1, or the parity drifts and every warp is signed backwards. */
BVR_API int32_t BVR_CALL bvr_afr_eye_consumed(void);

/* The eye the engine should draw next. Owned by the capture path, so it advances
   on the same event that consumes it and cannot drift from the frames actually
   captured. Read this instead of keeping an alternation on the game tick. */
BVR_API int32_t BVR_CALL bvr_next_afr_eye(void);

/* Hold the alternation on one eye (>= 0), or release it back to the capture path
   (-1). Pass 0 while dr_pin_source_eye is on, -1 otherwise. */
BVR_API int32_t BVR_CALL bvr_pin_afr_eye(int32_t eye);

/* --- crash breadcrumb ------------------------------------------------------
 *
 * Which piece of MANAGED VR code was last entered, and when.
 *
 * The crash logger can already say whether our NATIVE dll is on the faulting
 * thread's stack. It cannot say the same about our managed code: Harmony
 * patches are JIT-compiled, so they appear as bare addresses in no module at
 * all - which is exactly what every report of this crash has ended with.
 *
 * So the managed side stamps what it is about to do, and the crash logger
 * prints it with its age. A breadcrumb from four hundred milliseconds ago says
 * our patches were not running when the engine faulted; one from this
 * millisecond names the patch that was. Two answers, one line, no guessing.
 *
 * Cheap by construction: one relaxed store of two integers. */
BVR_API void BVR_CALL bvr_breadcrumb(int32_t id);

/* Ids, mirrored by VrBreadcrumb on the managed side. */
#define BVR_CRUMB_IDLE            0
#define BVR_CRUMB_APP_TICK        1
#define BVR_CRUMB_CAMERA_PATCH    2
#define BVR_CRUMB_AFR_TICK        3
#define BVR_CRUMB_MISSION_TICK    4
#define BVR_CRUMB_SCENE_RENDER    5
#define BVR_CRUMB_STEREO_CREATE   6
#define BVR_CRUMB_WEAPON_HANDS    7
#define BVR_CRUMB_BODY_HIDE       8
#define BVR_CRUMB_ENGINE_AIM      9
/* The mission tick, broken down. It reported only "MissionTick" for the
   2026-09-29 mid-battle crash, which narrowed a null dereference inside
   TaleWorlds.Native to seven subsystems and no further. These cost one
   stored integer each, once a frame. */
#define BVR_CRUMB_HEAD_AIM       10
#define BVR_CRUMB_MELEE          11
#define BVR_CRUMB_PHYS_COMBAT    12
#define BVR_CRUMB_BOW            13
#define BVR_CRUMB_HOLSTER        14
#define BVR_CRUMB_ROOMSCALE      15

/* The yaw, in radians, of the anchor the VR eye frames are built on - the game's
   own camera/body rotation. The OpenXR runtime knows the head pose and nothing
   about this, so without it the compositor reprojects a held AFR eye for head
   motion only and leaves the mouse-turn uncorrected. Publish once per frame,
   BEFORE the frame renders. */
BVR_API int32_t BVR_CALL bvr_set_body_yaw(float yawRadians);

/* The anchor POSITION, companion to the yaw above. A held AFR eye was drawn from
   where the game camera was a frame ago; riding moves it further between the two
   eyes than the IPD separates them, which is a false disparity larger than the
   real one. Publishing it lets the compositor parallax that out against the
   depth layer. */
BVR_API int32_t BVR_CALL bvr_set_body_position(float x, float y, float z);

/* The LIVE anchor, where the two above are the RENDERED one. Latching the pair
   camera makes the stereo correct by drawing both eyes from one anchor, at the
   cost of that anchor only advancing once per pair; publishing the live one lets
   the compositor carry each eye the rest of the way and smooth it back out. */
BVR_API int32_t BVR_CALL bvr_set_body_target(float yawRadians, float x, float y, float z);

/* Which simulation tick the world is currently on. Under AFR the capture path
   holds a freshly rendered eye back until its partner has been rendered from
   the SAME tick, so the pair on screen always agrees with itself. Pass a
   negative value to disable the pairing and put every eye up as it arrives. */
BVR_API int32_t BVR_CALL bvr_set_sim_generation(int32_t generation);

/* Whether draw-duplication stereo is the running mode. The native side owns the
   duplication, so it must be told - and it must be OFF for every other mode, or
   enabling the experiment would change what AFR does. */
BVR_API int32_t BVR_CALL bvr_set_draw_stereo(int32_t active);

/* Whether NATIVE STEREO is the running mode: one engine frame, every
   view-dependent operation run twice, the second eye into twins of whatever it
   writes. The native side owns the duplication, so it must be told - and it
   must be OFF for every other mode, because a mode that changes what AFR does
   merely by existing is the mistake draw duplication already made once. */
BVR_API int32_t BVR_CALL bvr_set_native_stereo(int32_t active);

/* World units per metre of the published eyes: 1 in a battle, the table's scale on
   the campaign map. Native stereo's IPD sanity bounds are metres and scale by it. */
BVR_API int32_t BVR_CALL bvr_set_stereo_world_scale(float unitsPerMetre);

/* Whether a VR mission is rendering. 0 puts the headset on the flat screen -
   the game's own image on a quad - which is what the main menu, the campaign
   map, and every non-mission screen need. */
BVR_API int32_t BVR_CALL bvr_set_mission_active(int32_t active);

/* Composite the game interface over the world instead of replacing it. */
BVR_API int32_t BVR_CALL bvr_set_ui_overlay(int32_t active);

/* ui_per_eye: this frame's interface was laid out for `eye`, projected from
   this head pose (stage space, OpenXR axes). The next UI capture goes to that
   eye's own quad, placed from the same pose. eye 2 = one image for both eyes
   (TEST 401). */
BVR_API int32_t BVR_CALL bvr_set_ui_projection(int32_t eye, float px, float py, float pz,
                                               float qx, float qy, float qz, float qw);

/* TEST 377: the wrist HUDs. Where the HUD widgets that belong on each wrist sit
   in the interface, in WINDOW pixels (x, y, w, h) of a screenWidth x
   screenHeight window. Native crops them out of the captured interface onto a
   quad on that hand's inner wrist and punches them out of the head panel.
   count 0 for a hand turns its wrist off. 0 = left, 1 = right. 144 bytes. */
#define BVR_WRIST_MAX_REGIONS 4
typedef struct BvrWristRegions {
    float   screenWidth;
    float   screenHeight;
    int32_t count[2];
    float   rects[2][BVR_WRIST_MAX_REGIONS][4];
} BvrWristRegions;

BVR_API int32_t BVR_CALL bvr_set_wrist_regions(const BvrWristRegions* regions);

/* TEST 590: the interface image shared out instead of cut. In WINDOW pixels of a
   screenWidth x screenHeight window: the WORLD page (world-anchored widgets) at
   0,0 is worldW x worldH, shown on its own quad sized by bvr_set_ui_geometry and
   hung where it was laid out; the HUD page at hudX,0 is hudW x hudH, shown
   head-locked at the freshest pose, hudWidthM wide at the same distance. The
   rest of the image holds the hands' widgets and is shown on no head quad.
   active 0 = one full-window image, as before. 36 bytes. */
typedef struct BvrUiSplit {
    int32_t active;
    float   screenWidth, screenHeight;
    float   worldW, worldH;
    float   hudX, hudW, hudH;
    float   hudWidthM;
} BvrUiSplit;

BVR_API int32_t BVR_CALL bvr_set_ui_split(const BvrUiSplit* split);

/* TEST 591: draw the game's interface at `factor` x the window (1 < factor <= 4)
   by binding a dense target wherever the engine binds the backbuffer; 0 off.
   Managed scales the shaders' pixel clip rects by the same factor. The state is
   0 off, 1 drawing into the dense target, 2 armed but the interface never
   reached the backbuffer (managed stands down), 3 standing aside while a 3D
   scene layer draws into the window with depth. */
BVR_API int32_t BVR_CALL bvr_set_ui_supersample(float factor);
BVR_API int32_t BVR_CALL bvr_get_ui_supersample(void);

/* TEST 377d: where `hand`'s wrist panel hangs, worked out from the DRAWN arm, in
   stage space (OpenXR axes, metres). The quad is then placed there in stage
   space instead of at the fixed grip-space offset. valid 0 returns the hand to
   the grip-space offset. TEST 523: valid 2 floats the panel above the
   controller, facing the head (the pose is ignored); valid 3 the same, sized by
   the campaign palette's width (TEST 527); valid 4 sits it on top of the other
   hand's panel at that panel's scale - the palette's tooltip (TEST 532). */
BVR_API int32_t BVR_CALL bvr_set_wrist_pose(int32_t hand, int32_t valid,
                                            float px, float py, float pz,
                                            float qx, float qy, float qz, float qw);

/* TEST 378: the laser pointer. Native casts the ray from `hand`'s aim pose
   against whatever can be pointed at this frame - the VR panel first, then the
   game's interface (the flat screen or the interface quad) - and draws the beam
   and a dot where it lands. enabled 0 leaves only the VR panel pointable.
   beamToUi 0 hit-tests without drawing (the campaign map draws its own beam to
   the terrain). click is the active hand's trigger as managed reads it, used
   for the VR panel; the game's own clicks go through the pad. During deployment,
   groundDistance bypasses the head UI quad and extends the beam to terrain:
   positive for a terrain hit, negative for a ray with no hit, zero otherwise. */
typedef struct BvrLaser {
    int32_t valid;      /* the ray landed on something pointable */
    int32_t hand;       /* 0 = left, 1 = right */
    int32_t target;     /* 0 none, 1 the game's interface, 2 the VR panel, 3 a hand
                           panel (TEST 532; u/v are window coordinates, like 1) */
    float   u, v;       /* where, as image coordinates: 0,0 top-left */
    float   distance;   /* metres along the ray */
} BvrLaser;             /* 24 bytes */

BVR_API int32_t BVR_CALL bvr_set_laser(int32_t enabled, int32_t hand, int32_t beamToUi,
                                       int32_t click, float groundDistance);
BVR_API int32_t BVR_CALL bvr_get_laser(BvrLaser* out);

/* TEST 651: the laser ray as last drawn for `hand` - stage space, the direction
   smoothed (laser_smooth_hz / _beta). BVR_SESSION_NOT_READY when that hand has
   not been drawn in the last 100 ms. */
BVR_API int32_t BVR_CALL bvr_get_laser_ray(int32_t hand, BvrVec3* origin, BvrVec3* dir);

/* TEST 378: 1 pins the interface quad where it hangs now, level, at eye height
   (the flat screen's rule), and shows one image to both eyes while pinned - a
   menu that stays put in the room to be pointed at. 0 returns it to the head. */
BVR_API int32_t BVR_CALL bvr_set_ui_pin(int32_t pinned);

/* TEST 379: ui_eye_lag at runtime. 1 pairs each interface image with the
   PREVIOUS projection published; the campaign map and missions can differ. */
BVR_API int32_t BVR_CALL bvr_set_ui_eye_lag(int32_t lag);

/* TEST 633: display refreshes each app frame stays on screen, smoothed (~1 at
   the display rate, ~2.5 at 47 fps on a 120 Hz headset). */
BVR_API float BVR_CALL bvr_get_periods_per_frame(void);

/* --- changing the render scale without restarting the game ----------------
 *
 * A HANDSHAKE, not a call, because the two halves own different pieces: the eye
 * TEXTURES belong to the engine and only managed can release and re-ask for
 * them, while the SWAPCHAINS belong to OpenXR and only the frame-loop thread
 * may touch them. Neither side can do this alone and neither may block the
 * other, so each watches a stage instead.
 *
 * The sequence managed follows:
 *
 *   1  bvr_request_rescale(scale)          -> stage 1, draining frames in flight
 *   2  poll bvr_rescale_stage() until 2    -> the old eye textures have been let
 *                                             go and the new size is chosen. THE
 *                                             SWAPCHAINS ARE STILL THE OLD SIZE.
 *   3  release its own targets, create them at the new size, wait for rgl
 *   4a got them   -> bvr_rescale_swap()    -> stage 3; the swapchains are rebuilt
 *                                             to match, then stage returns to 0
 *   4b could not  -> bvr_rescale_abort(previousScale) -> back to stage 2 at the
 *                                             OLD size, where the untouched
 *                                             swapchains still fit, and try again
 *   5  re-point the scene view, bvr_rescale_finish()
 *
 * THE ORDER IS THE SAFETY. Destroying working swapchains before the new textures
 * exist is what turned "rgl would not give us textures" into a headset with no
 * image and no way back. Now the only irreversible step happens after the
 * reversible one has succeeded, and 4b is a real way home.
 *
 * Frames carry no layers from step 1 to step 5, so the headset is black for that
 * window - a moment of black being the honest thing to show when there is no
 * image, rather than a held pair locked to the head.
 *
 * scale is the ABSOLUTE render scale, 0.25 to 1.0, the same number the config
 * key holds. Passed explicitly rather than re-read from the file because the
 * config write and this call otherwise race, and losing that race rebuilds at
 * the old size - which looks exactly like the button doing nothing. */
BVR_API int32_t BVR_CALL bvr_request_rescale(float scale);
BVR_API int32_t BVR_CALL bvr_rescale_swap(void);
BVR_API int32_t BVR_CALL bvr_rescale_abort(float previousScale);
BVR_API int32_t BVR_CALL bvr_rescale_stage(void);
BVR_API int32_t BVR_CALL bvr_rescale_finish(void);

/* Whether a mission exists at all. Drives the window mirror. */
BVR_API int32_t BVR_CALL bvr_set_in_mission(int32_t active);

/* Size and distance of the keyed interface layer, in metres. Set from the VR
   panel; the managed side saves them and pushes them back. */
BVR_API int32_t BVR_CALL bvr_set_ui_geometry(float width, float distance);

/* Size and distance of the FLAT SCREEN, in metres. Changing the distance
   re-pins it, since the pose is computed from that when it is placed. */
BVR_API int32_t BVR_CALL bvr_set_screen_geometry(float width, float distance);

/* Re-pins the flat screen in front of the head. Called on recentre, so a screen
   that ended up somewhere awkward can be fixed without restarting. */
BVR_API int32_t BVR_CALL bvr_repin_flat_screen(void);

/* TEST 710: re-pins only if the head is turned more than `degrees` left or right
   of the screen. Used when a new screen opens. */
BVR_API int32_t BVR_CALL bvr_repin_flat_screen_if_turned(float degrees);

/* Which of the three single-render stereo modes is running, and the geometry
   they all need.

     mode 0  AFR. The eye that was not rendered is HELD from its own last real
             render and submitted with the pose it was drawn at.
     mode 1  DR. That eye is SYNTHESISED from the rendered one by reprojecting
             it through the depth buffer, so both eyes come from one instant.
     mode 2  DR+AFR hybrid. Held while the head is still - where a real render
             is exact and no warp can improve on it - and warped once it moves,
             where a held eye would be displaced by the movement.

   The geometry is applied in every mode, including 0: the reticle places itself
   from the same focal length and baseline and is drawn under AFR too.
   focalPx = (eye width / 2) / tan(hfov / 2); baseline and the planes are in game
   world units. */
BVR_API int32_t BVR_CALL bvr_set_depth_reproject(int32_t mode, float focalPx, float baseline,
                                     float zNear, float zFar);

/* 1 once the warp can actually synthesise an eye - shader built, parameters set,
   and a depth buffer that has PROVED it contains a drawn scene rather than one
   the search merely settled on.

   Ask this before pinning the source eye. Pinning leaves the synthesised eye
   with no other source of content, so while this is 0 that eye would be filled
   with a copy of the other one and the view would go flat. Alternating until it
   turns 1 gives ordinary AFR instead, which is real stereo at half the rate. */
BVR_API int32_t BVR_CALL bvr_depth_reproject_ready(void);

/* --- view-projection patching -------------------------------------------
 * Every managed route into the engine's camera was tried and measured; the
 * engine rewrites CombatCamera's pose AND frustum every single frame, and the
 * only configurations that render the world correctly are the ones where the
 * camera is entirely the engine's. So the camera is left alone and the matrix
 * is substituted instead, in the constant buffer, on the way to the GPU.
 *
 * This publishes everything the native patcher needs for one frame, as a flat
 * float array rather than a struct: Mono marshals a blittable float[] by
 * pinning it, with no layout to drift, which is the same reason
 * bvr_get_predicted_views takes an array.
 *
 *   [0..15]  eye camera local->world, row-major row-vector (v * M),
 *            rows 0,1,2 = right, up, forward and row 3 = position
 *   [16..18] engine camera world position   ) used ONLY to recognise the main
 *   [19..21] engine camera view direction   ) view among the other matrices
 *   [22..25] tanLeft, tanRight, tanUp, tanDown for this eye (OpenXR signs)
 *
 * valid = 0 stands the patcher down without unhooking anything. */
#define BVR_VP_FRAME_FLOATS 26

BVR_API int32_t BVR_CALL bvr_set_vp_frame(const float* data, int32_t floatCount,
                                          int32_t eye, int32_t valid);

/* The eye the engine is NOT drawing, for the second-eye constant buffer. Same
   payload and order as above. */
BVR_API int32_t BVR_CALL bvr_set_vp_frame_other(const float* data, int32_t floatCount,
                                                int32_t eye, int32_t valid);
/* --- settings panel ------------------------------------------------------
 *
 * The panel is drawn natively, as an OpenXR quad layer, because that is the only
 * way to put pixels in front of a player wearing the headset: the headset sees
 * the scene view and nothing else, so a menu built with the game's own UI is
 * perfectly functional and completely invisible where it is needed.
 *
 * The managed side keeps ownership of what the values MEAN - it is the half that
 * can apply a world scale and write the config file - so the two exchange a
 * mirror rather than the panel reaching into the game.
 *
 *   push  what is currently true, whenever it changes on the managed side
 *   poll  every frame; returns Ok only when the PANEL changed something, so the
 *         config file is written once per edit rather than at frame rate
 *
 * A STRUCT RATHER THAN A PARAMETER LIST
 *
 * This was four floats and two out-pointers, and adding calibration to the panel
 * would have made it eleven. Every addition is then a signature change on both
 * sides of a P/Invoke boundary, where a mismatch does not fail to compile - it
 * reads the stack wrong and hands the game a world scale built out of whatever
 * happened to be in the next register. A struct with an explicit layout moves
 * that failure into a place where it can be checked once, and lets the panel
 * grow without the ABI moving underneath it. */

/* What the calibration is currently doing. Pushed by the managed side; the panel
   only reads it, because starting and stopping are its requests rather than its
   decisions - see BvrCalibCommand. */
typedef enum BvrCalibState
{
    BVR_CALIB_IDLE = 0,
    BVR_CALIB_AUTO = 1,   /* sampling the animation over a window */
    BVR_CALIB_MANUAL = 2, /* the sticks are nudging a hand */
} BvrCalibState;

/* What the panel is asking for. Poll-only, and cleared as it is read, so one
   click is one request - a command that stayed set would fire again every frame
   the panel was open. */
typedef enum BvrCalibCommand
{
    BVR_CALIB_CMD_NONE = 0,
    BVR_CALIB_CMD_AUTO = 1,        /* start the automatic capture */
    BVR_CALIB_CMD_MANUAL = 2,      /* open the stick-nudge mode */
    BVR_CALIB_CMD_ACCEPT = 3,      /* save and close */
    BVR_CALIB_CMD_CANCEL = 4,      /* discard and close */
    BVR_CALIB_CMD_RESET = 5,       /* reset the selected hand */
    BVR_CALIB_CMD_SELECT_MAIN = 6, /* edit the weapon hand */
    BVR_CALIB_CMD_SELECT_OFF = 7,  /* edit the shield hand */
    BVR_CALIB_CMD_ADJUST = 8,      /* a slider moved; calibX..calibRoll are new */
} BvrCalibCommand;

/* The panel's mirror of the world. Push and poll both use it; a few fields are
   meaningful in only one direction and say so. */
typedef struct BvrOverlayState
{
    float worldScale;
    float renderScale;

    /* 0 = alternate-frame, 1 = native stereo, 2 = depth reprojection. */
    int32_t stereoMode;

    /* Push-only. A mode has been asked for but the scene has not restarted yet,
       so the panel can say so rather than appearing inert. */
    int32_t stereoPending;

    /* --- calibration, all push-only except calibCommand ------------------ */

    int32_t calibState;    /* BvrCalibState */
    int32_t calibHand;     /* which hand is being edited: 0 weapon, 1 shield */

    /* 0..1 while BVR_CALIB_AUTO is sampling. A progress bar rather than a
       spinner, because the window is a fixed length and the player is being
       asked to hold still for it - "how much longer" is the question. */
    float calibProgress;

    /* How much the animation wobbled across the last automatic sample, in
       degrees. This is the number that says whether to trust the result: a
       character standing still gives a degree or two, and one mid-swing gives
       tens. Reported rather than silently accepted. */
    float calibSpread;

    /* Bit 0: the weapon hand has a calibration. Bit 1: the shield hand does. */
    int32_t calibHave;

    /* The manual adjustment for the hand named by calibHand: where it sits
       relative to the controller, in metres, and how it is turned, in degrees.
       Pushed by managed so the sliders show the truth; written by the panel and
       sent back with BVR_CALIB_CMD_ADJUST.

       Six numbers rather than a matrix because these are what a SLIDER can
       show. The captured orientation stays a matrix on the managed side and is
       never round-tripped through angles - these compose on top of it, so the
       gimbal case and the convention question never arise for the part that was
       measured. */
    float calibX;
    float calibY;
    float calibZ;
    float calibYaw;
    float calibPitch;
    float calibRoll;

    /* Poll-only. BvrCalibCommand; cleared as it is read. */
    int32_t calibCommand;

    /* --- two-way toggles -------------------------------------------------
       Pushed so the checkbox shows the truth after a config load, and read
       back so clicking it means something. Unlike calibCommand these are
       STATE rather than an event, so they are not cleared on read. */

    /* Hide the player's own body in first person. On by default: in a headset
       your own torso is drawn where your real one is not, and looking down at
       someone else's shoulders is the single most reliable way to stop
       believing you are there. */
    int32_t hideBody;

    /* Motion-control hands and melee. EXPERIMENTAL and off by default - the
       hand posing fights the game's own animation and the swing detection is
       not finished. The controllers still work as a gamepad with this off;
       that path is unrelated and is what the mod is currently played with. */
    int32_t motionHands;

    /* --- the game interface in the headset -------------------------------
       Two-way, like the toggles above. The interface layer is keyed and drawn
       over the world; these decide whether it is shown at all and how big it
       hangs. */
    int32_t uiShow;
    float   uiWidth;      /* metres across, at uiDistance */
    float   uiDistance;   /* metres from the eye */

    /* --- the flat screen -------------------------------------------------
       The panel that carries the main menu, the campaign map and the pause
       menus. Separate numbers from the interface overlay because they are
       different objects: one is a screen across the room, the other is a layer
       on the view. */
    float   screenWidth;
    float   screenDistance;

    /* --- the tableau probe -----------------------------------------------
       A DIAGNOSTIC, not a stereo mode, which is why it is a button on the
       Display tab rather than a fifth entry in the radio list. Every mode in
       that list renders the headset; this one renders nothing and writes a PNG.
       Putting it there would break the one promise the list makes.

       tableauProbe is poll-only and cleared as it is read, like calibCommand:
       clicking the button is an EVENT, not a state to be kept.

       tableauProbeState is push-only, so the button can say what happened
       without the player having to go and read a log:
         0 idle   1 running   2 finished   3 failed */
    int32_t tableauProbe;
    int32_t tableauProbeState;

    /* --- sharpening (TEST 187) -------------------------------------------
       Two-way, 0..1. CAS on the finished eye images at submit, in every render
       mode; 0 is off. Appended LAST so every field above keeps its offset. */
    float   sharpen;

    /* --- the menu pointer -------------------------------------------------
       How fast the thumbstick drags the cursor across menus, in screen widths
       per second at full deflection. Two-way, and applied live: it is judged by
       moving the stick and watching, not by reading the number.

       Purely a managed setting - nothing native reads it - but it belongs on
       this struct because the panel is the only place it can be changed from
       inside a headset, which is the only place it is ever wrong.

       Appended LAST, after sharpen, so every field above keeps its offset. */
    float   stickMouseSpeed;

    /* --- applying a resolution change without a restart -------------------
       resolutionApply is poll-only and cleared as it is read, exactly like
       tableauProbe: clicking Apply is an EVENT. It exists because the slider is
       only half live on its own - lowering it is immediate, since the scene is
       simply shaded at a smaller fraction of the eye target, while RAISING it
       above what the targets were allocated for has nothing to raise. Apply
       rebuilds the pair at the new size.

       resolutionState is push-only, so the button can say what is happening
       rather than the player wondering whether it took:
         0 idle   1 rebuilding   2 live at the new size   3 failed

       Appended LAST, after stickMouseSpeed, so every field above keeps its
       offset. */
    int32_t resolutionApply;
    int32_t resolutionState;

    /* --- physical collision combat (TEST 244) -----------------------------
       Two-way, like hideBody and motionHands: pushed so the checkbox shows the
       truth after a config load, read back so clicking it means something. It
       switches the mod's own physics off - the player's swings no longer hit,
       a raised shield no longer blocks, arrows stop on nothing - while leaving
       motion-control hands, weapons and the game's own trigger attacks alone.

       Appended LAST, after resolutionState, so every field above keeps its
       offset. */
    int32_t physicalCombat;

    /* --- the full body (TEST 258) -----------------------------------------
       Two-way, like the toggles above. OFF reverts to the normal animated body:
       no pelvis or torso follow, no leg solve, no held pose, and the camera
       anchors to the engine's own head again. ON is the full-body IK, whose
       animation diet is the body_anim key (legs or full).

       Appended LAST, after physicalCombat, so every field above keeps its
       offset. */
    int32_t bodyIk;

    /* --- motion-control combat (TEST 245-252) ------------------------------
       Two-way, like the toggles above. One switch for the whole gesture melee:
       a swing writes the game's own attack direction, a raised hand is the
       guard, the blade lands the damage itself and the body holds still through
       engine attacks. OFF is the game's own combat - the trigger and the
       movement stick attack and block, and every animation plays.

       Appended LAST, after bodyIk, so every field above keeps its offset. */
    int32_t motionMelee;

    /* --- friendly fire (TEST 261) -----------------------------------------
       Two-way, like the toggles above. ON lets a physical swing land on
       friends and neutrals; OFF (the default) is the vanilla single-player
       rule, enemies only. Missions that forbid friendly fire refuse it either
       way - the managed side checks ForceNoFriendlyFire.

       Appended LAST, after motionMelee, so every field above keeps its
       offset. */
    int32_t friendlyFire;

    /* --- which controller the grip is saved for ----------------------------
       Two-way. The dropdown index on the Calibration section; managed turns it
       into the config suffix (grip_rotation_main_psvr2 and so on) and reloads
       that controller's grips. 0 is the unsuffixed generic keys.

       Appended LAST, after friendlyFire, so every field above keeps its
       offset. */
    int32_t gripController;

    /* --- body holsters (TEST 271) ------------------------------------------
       Two-way. ON: the trigger in a body zone draws or sheathes a weapon and
       the trigger's vanilla attack is off in a fight. OFF gives the trigger
       back. Appended LAST, after gripController, so every field above keeps
       its offset. */
    int32_t holster;

    /* --- the campaign map in VR (map_stereo) ----------------------------
       Two-way. Takes effect once no map is bound to the eye texture - the
       binding cannot be undone. Appended LAST, after holster. */
    int32_t mapVr;

    /* --- the VR tab (TEST 293) --------------------------------------------
       Two-way. The head camera's offset from the body, in metres along the
       body's heading (up, forward), and how far the head turns before the body
       follows (TEST 291), in degrees. recenter is a one-shot: the PANEL's to
       raise and managed's to consume, like resolutionApply. Appended LAST,
       after mapVr. */
    float headUp;
    float headForward;
    float bodyTurnDeg;
    int32_t recenter;

    /* --- hands only ---------------------------------------------------------
       Two-way, like hideBody - and only has an effect while hideBody is on: it
       is a variant of the full hide, keeping the hand meshes (matched by name,
       like hide_body_hand_meshes) drawn alongside the weapon instead of hiding
       everything but the weapon. Appended LAST, after recenter. */
    int32_t handsOnly;

    /* --- your height (TEST 356) -----------------------------------------------
       playerHeight is two-way: the player's standing height in metres, which
       the character is scaled to match (0 = the game's own size). heightMeasure
       is a one-shot like recenter: the PANEL's to raise, managed's to consume -
       measure the headset's height now. bodyScale is push-only: the scale that
       gave. Appended LAST, after handsOnly. */
    float playerHeight;
    int32_t heightMeasure;
    float bodyScale;

    /* --- the wrist HUD's size (TEST 377e) --------------------------------------
       Two-way: the wrist panel's width in metres. The panel applies it the moment
       the slider moves; managed keeps the mirror and saves it. Appended LAST. */
    float wristHudWidth;

    /* --- foveated rendering, NVIDIA VRS (TEST 427) ------------------------------
       Two-way: 0 off, 1 wide, 2 balanced, 3 narrow. The panel applies it the
       moment the dropdown moves; managed keeps the mirror and saves `vrs`.
       Appended LAST. */
    int32_t vrs;

    /* --- eye-tracked foveation (TEST 428) ---------------------------------------
       Two-way: 1 = the VRS centre follows the gaze when the runtime gives one.
       The panel applies it; managed saves `vrs_eye`. Appended LAST. */
    int32_t vrsEye;

    /* --- the head camera's height in the saddle (TEST 442) -----------------------
       Two-way, metres: used INSTEAD of headUp while the player rides. Managed
       saves `head_offset_mounted_up_m`. Appended LAST. */
    float headUpMounted;

    /* --- your body (TEST 450 / 447) -----------------------------------------------
       Two-way. armScale: the arms' length against the character's own, 0.80-1.30
       (managed saves `arm_length_scale`). bodyWeight: seconds the chest takes to
       follow the head's turn, 0 = instant (managed saves `body_weight_s`).
       Appended LAST. */
    float armScale;
    float bodyWeight;

    /* --- battle HUD extras (TEST 462) ----------------------------------------------
       Two-way, 0/1. hudHitMarker: the crosshair's hit marker without the reticle
       (managed saves `hud_hit_marker`). hudPrompts: the pickup prompt - the item's
       name and its button (managed saves `hud_prompts`). Appended LAST. */
    int32_t hudHitMarker;
    int32_t hudPrompts;

    /* --- five-finger hands (TEST 485) ------------------------------------------------
       Two-way, 0/1: the player's own gloves bend per finger (managed saves
       `finger_mesh`); 0 = the game's hands. Appended LAST. */
    int32_t handFingers;

    /* --- the campaign map's hand palette size (TEST 527) ---------------------------
       Two-way: the map bar panel's width in metres, separate from wristHudWidth
       (the battle wrists). The panel applies it; managed saves `hand_ui_width_m`.
       Appended LAST. */
    float mapHandUiWidth;

    /* --- D-pad chord on the left thumbrest -----------------------------------------
       Two-way, 0/1: a thumb resting on the LEFT thumbrest (Quest Touch) is also the
       D-pad modifier for the right stick, beside the left stick held in. Managed
       saves `dpad_thumbrest`. Appended LAST. */
    int32_t dpadThumbrest;

    /* --- TEST 620: world-anchored UI size under the split (TEST 590) -------------
       Two-way: how big the battle markers (formations, objectives) and the campaign
       map labels draw, x the game's own UI scale. Managed applies them per layer and
       saves `ui_world_scale_battle` / `ui_world_scale_map`. Appended LAST. */
    float uiWorldScaleBattle;
    float uiWorldScaleMap;

    /* --- TEST 645: your weapon is solid against your own shield (TEST 642) --------
       Two-way, 0/1. Managed saves `collide_own_shield`. Appended LAST. */
    int32_t ownShieldBash;

    /* --- TEST 648: the pre-battle formation markers' size ---------------------------
       Two-way: x the game's UI scale, capped by what still fits the cards on the
       palette (they share one layer). Managed saves `ui_world_scale_deploy_markers`.
       Appended LAST. */
    float uiWorldScaleDeploy;

    /* --- TEST 711: re-centre a new screen, and only past this turn-away angle ----
       Two-way. Managed saves `ui_recenter_new_screen` and `ui_recenter_angle`.
       Appended LAST. */
    int32_t uiRecenterNewScreen;
    float uiRecenterAngle;

    /* --- fov_true_vertical: symmetric across, the runtime's own up/down ------
       Two-way. Managed saves `fov_true_vertical`. Appended LAST. */
    int32_t fovTrueVertical;

    /* --- TEST 750: the Calibration tool tab ---------------------------------
       toolMode (0 weapon, 1 hand grip) and toolHand (0 main, 1 off) are the
       panel's, echoed back. toolLive is push-only: 1 holding it now, 0 dropped
       (the sliders grey, the saves stay), -1 nothing to edit. toolValue is
       x, y, z metres then yaw, pitch, roll degrees, two-way. toolCommand is a
       poll-only event (BVR_TOOL_CMD_*). The two texts are push-only, ANSI.
       Appended LAST. */
    int32_t toolMode;
    int32_t toolHand;
    int32_t toolLive;
    int32_t toolCommand;
    float toolValue[6];
    char toolLabel[128];
    char toolStatus[128];

    /* --- TEST 751: pose profiles and equipping from the tool -----------------
       toolProfile: 0 Original, 1 Profile 1, 2 Profile 2. Two-way, managed saves
       `skin_profile`. toolWielded: push-only, the main hand's slot 0..3 or -1.
       toolSlot: push-only names of the four weapon slots, "" when empty.
       Appended LAST. */
    int32_t toolProfile;
    int32_t toolWielded;
    char toolSlot[4][48];

    /* --- TEST 770: fov crop, VD-style tangent multipliers (0.5-1.0) ----------
       Two-way. Managed saves `fov_crop_h` / `fov_crop_v`; the eye targets
       follow through resolutionApply. Appended LAST. */
    float fovCropH;
    float fovCropV;

    /* --- TEST 785: detailed fov crop, one multiplier per edge ---------------
       Two-way. fovCropDetailed 1 = the four edges below apply instead of
       fovCropH/V. Managed saves fov_crop_detailed / _left / _right / _up /
       _down. Appended LAST. */
    int32_t fovCropDetailed;
    float fovCropLeft;
    float fovCropRight;
    float fovCropUp;
    float fovCropDown;

    /* --- TEST 790: sharpener, 0 CAS / 1 RCAS --------------------------------
       Two-way. Managed saves `sharpen_mode`. Appended LAST. */
    int32_t sharpenMode;

    /* --- TEST 797: DLSS preset ----------------------------------------------
       Two-way. The cfg value: 0 auto (driver profile, else K), 10 J, 11 K,
       12 L, 13 M, -1 DLSS's own. Managed saves `dlss_preset`. Appended LAST. */
    int32_t dlssPreset;
    int32_t gripToggle;

} BvrOverlayState;

/* TEST 750: toolCommand values. SAME VALUES as BvrToolCommand in NativeBridge.cs. */
enum
{
    BVR_TOOL_CMD_NONE = 0,
    BVR_TOOL_CMD_ADJUST = 1,
    BVR_TOOL_CMD_SAVE_ITEM = 2,
    BVR_TOOL_CMD_SAVE_CLASS = 3,
    BVR_TOOL_CMD_ZERO = 4,
    BVR_TOOL_CMD_REVERT = 5,
    BVR_TOOL_CMD_SAVE_HAND = 6,
    BVR_TOOL_CMD_EQUIP_0 = 7,      /* 7..10: wield weapon slot 0..3 (TEST 751) */
    BVR_TOOL_CMD_SHEATHE = 11
};

/* Mouse-wheel notches for the panel, from the game thread (the render thread
   has no message pump to read the wheel from). Accumulated until the panel's
   next frame. */
BVR_API int32_t BVR_CALL bvr_overlay_scroll(float notches);

BVR_API int32_t BVR_CALL bvr_overlay_toggle(void);
BVR_API int32_t BVR_CALL bvr_overlay_visible(void);
BVR_API int32_t BVR_CALL bvr_overlay_show(int32_t visible);
BVR_API int32_t BVR_CALL bvr_overlay_push(const BvrOverlayState* state);

/* Returns Ok when the panel changed something OR is asking for a command, and
   BVR_SESSION_NOT_READY when it is not - which is the common case and not an
   error. */
BVR_API int32_t BVR_CALL bvr_overlay_poll(BvrOverlayState* outState);

/* --- input (Phase 6) ---------------------------------------------------- */
BVR_API int32_t BVR_CALL bvr_get_input_state(BvrInputState* outState);

/* TEST 452: once per game frame, before anything reads input - locate both hands
 * on the game thread for this moment plus leadFrames display periods. Every
 * bvr_get_input_state after it (for up to 100 ms) returns that sample. */
BVR_API int32_t BVR_CALL bvr_latch_input(float leadFrames);

/* TEST 482: the controllers' touch sensors, for the finger hands - the left
 * hand in bits 0-7 and the right in 8-15, each a BVR_TOUCH_* set. A sensor the
 * runtime did not bind (a profile without it, or the profile declined with it)
 * reports neither its _BOUND bit nor touched; 0 before input is up. */
#define BVR_TOUCH_TRIGGER        0x1u   /* a finger rests on the trigger */
#define BVR_TOUCH_THUMB          0x2u   /* the thumb rests on the stick, a face button or the thumbrest */
#define BVR_TOUCH_TRIGGER_BOUND  0x4u
#define BVR_TOUCH_THUMB_BOUND    0x8u
#define BVR_TOUCH_THUMBREST      0x10u  /* the thumb rests on the thumbrest itself (Touch only) */
BVR_API uint32_t BVR_CALL bvr_get_finger_touch(void);

/* A pulse on one controller: 0 = left, 1 = right, amplitude 0..1, duration in
   seconds. Returns BVR_OK when the runtime took it and BVR_SESSION_NOT_READY
   when there is no session, no vibration action or no binding - a caller may
   call it on every block without guarding anything itself. Thread-safe from the
   game thread; the session handle is guarded against shutdown. */
BVR_API int32_t BVR_CALL bvr_trigger_haptic(int32_t hand, float amplitude, float durationSeconds);

/* --- teardown ----------------------------------------------------------- */
BVR_API void BVR_CALL bvr_shutdown(void);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* BVR_API_H */
