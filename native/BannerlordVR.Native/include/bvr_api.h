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
   (-1). Pass 0 while afw_pin_source_eye is on, -1 otherwise. */
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

/* Full AFR: one eye per GAME frame, two game frames inside one DISPLAY frame.
   The first Present of a pair captures its eye and hands the begun XR frame
   back rather than ending it, so the engine draws the second eye from the same
   locate and the same frozen world and the pair goes up together. Both eyes are
   then real renders of one simulation tick at the full display rate, with
   nothing held and nothing warped - at the cost of asking the engine for twice
   the frame rate. Publish it every frame alongside the eye index. */
BVR_API int32_t BVR_CALL bvr_set_full_afr(int32_t active);

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

/* Whether a VR mission is rendering. 0 puts the headset on the flat screen -
   the game's own image on a quad - which is what the main menu, the campaign
   map, and every non-mission screen need. */
BVR_API int32_t BVR_CALL bvr_set_mission_active(int32_t active);

/* Composite the game interface over the world instead of replacing it. */
BVR_API int32_t BVR_CALL bvr_set_ui_overlay(int32_t active);

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

/* Which of the three single-render stereo modes is running, and the geometry
   they all need.

     mode 0  AFR. The eye that was not rendered is HELD from its own last real
             render and submitted with the pose it was drawn at.
     mode 1  AFW. That eye is SYNTHESISED from the rendered one by reprojecting
             it through the depth buffer, so both eyes come from one instant.
     mode 2  AFW+AFR hybrid. Held while the head is still - where a real render
             is exact and no warp can improve on it - and warped once it moves,
             where a held eye would be displaced by the movement.

   The geometry is applied in every mode, including 0: the reticle places itself
   from the same focal length and baseline and is drawn under AFR too.
   focalPx = (eye width / 2) / tan(hfov / 2); baseline and the planes are in game
   world units. */
BVR_API int32_t BVR_CALL bvr_set_afw(int32_t mode, float focalPx, float baseline,
                                     float zNear, float zFar);

/* 1 once the warp can actually synthesise an eye - shader built, parameters set,
   and a depth buffer that has PROVED it contains a drawn scene rather than one
   the search merely settled on.

   Ask this before pinning the source eye. Pinning leaves the synthesised eye
   with no other source of content, so while this is 0 that eye would be filled
   with a copy of the other one and the view would go flat. Alternating until it
   turns 1 gives ordinary AFR instead, which is real stereo at half the rate. */
BVR_API int32_t BVR_CALL bvr_afw_ready(void);

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

    /* 0 = alternate-frame, 1 = native stereo, 2 = alternate-frame warping. */
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
} BvrOverlayState;

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
BVR_API int32_t BVR_CALL bvr_trigger_haptic(int32_t hand, float amplitude, float durationSeconds);

/* --- teardown ----------------------------------------------------------- */
BVR_API void BVR_CALL bvr_shutdown(void);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* BVR_API_H */
