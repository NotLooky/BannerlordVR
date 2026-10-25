using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BannerlordVR.Interop
{
    // ---------------------------------------------------------------------
    // Managed mirror of native/BannerlordVR.Native/include/bvr_api.h
    //
    // Rules for everything in this file (Mono's marshaller is less forgiving
    // than CoreCLR's, and this runs on the render thread):
    //   * blittable primitives only - no bool, no string, no class, no arrays
    //     inside structs;
    //   * LayoutKind.Sequential, Pack unset (natural alignment matches C);
    //   * every entry point returns int32 status (0 = BVR_OK), never throws;
    //   * no reverse P/Invoke - C# polls, C++ never calls back.
    // Any change here must be mirrored in bvr_api.h in the same commit.
    // ---------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct BvrQuat
    {
        public float X, Y, Z, W;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BvrVec3
    {
        public float X, Y, Z;
    }

    /// <summary>OpenXR-style asymmetric field of view, in radians.
    /// Left/Down are normally negative. Maps onto Camera.SetViewVolume.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BvrFov
    {
        public float AngleLeft, AngleRight, AngleUp, AngleDown;
    }

    /// <summary>One eye's pose + projection, in OpenXR space (Y-up, -Z forward).
    /// Convert with VrMath before touching anything TaleWorlds.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BvrEyeView
    {
        public BvrQuat Orientation;
        public BvrVec3 Position;
        public BvrFov Fov;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BvrHandState
    {
        public BvrQuat AimOrientation;
        public BvrVec3 AimPosition;
        public BvrQuat GripOrientation;
        public BvrVec3 GripPosition;
        public BvrVec3 LinearVelocity;   // metres/sec, for swing detection (Phase 6 Stage B)
        public float Trigger;            // 0..1
        public float Grip;               // 0..1
        public float ThumbstickX;        // -1..1
        public float ThumbstickY;        // -1..1
        public int Buttons;              // bitfield, see BvrButton
        public int IsActive;             // 0/1 - controller tracked this frame
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BvrInputState
    {
        public BvrHandState Left;
        public BvrHandState Right;
    }

    /// <summary>TEST 377: where the wrist HUDs' widgets are, in window pixels.
    /// Rects is [hand][region][x, y, w, h], hand 0 = left. 144 bytes, matching
    /// bvr_api.h.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BvrWristRegions
    {
        public const int MaxRegions = 4;

        public float ScreenWidth;
        public float ScreenHeight;
        public int CountLeft;
        public int CountRight;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2 * MaxRegions * 4)]
        public float[] Rects;
    }

    /// <summary>TEST 590: which parts of the interface image are the world page
    /// (at 0,0) and the HUD page (at HudX,0), in window pixels. 36 bytes, matching
    /// bvr_api.h.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BvrUiSplit
    {
        public int Active;
        public float ScreenWidth;
        public float ScreenHeight;
        public float WorldW;
        public float WorldH;
        public float HudX;
        public float HudW;
        public float HudH;
        /// <summary>The HUD quad's width in metres; it hangs at the world panel's
        /// distance, so the two never disagree in depth.</summary>
        public float HudWidthM;
    }

    /// <summary>TEST 378: where the laser landed this frame. 24 bytes, matching
    /// bvr_api.h. Target 1 = the game's interface, 2 = the VR panel.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BvrLaser
    {
        public int Valid;
        public int Hand;
        public int Target;
        public float U;
        public float V;
        public float Distance;
    }

    /// <summary>What the calibration is currently doing. Pushed to the panel;
    /// the panel only reads it, because starting and stopping are its requests
    /// rather than its decisions - see <see cref="BvrCalibCommand"/>.</summary>
    public enum BvrCalibState
    {
        Idle = 0,
        Auto = 1,     // sampling the animation over a window
        Manual = 2,   // the sticks are nudging a hand
    }

    /// <summary>What the panel is asking for. Cleared as it is read, so one click
    /// is one request - a command that stayed set would fire again every frame the
    /// panel was open.</summary>
    public enum BvrCalibCommand
    {
        None = 0,
        Auto = 1,          // start the automatic capture
        Manual = 2,        // open the stick-nudge mode
        Accept = 3,        // save and close
        Cancel = 4,        // discard and close
        Reset = 5,         // reset the selected hand
        SelectMain = 6,    // edit the weapon hand
        SelectOff = 7,     // edit the shield hand
        Adjust = 8,        // a slider moved; CalibX..CalibRoll carry the new value
    }

    /// <summary>
    /// The panel's mirror of the world.
    ///
    /// A STRUCT RATHER THAN A PARAMETER LIST
    ///
    /// This was four floats and three out-parameters, and adding calibration would
    /// have made it eleven. Every addition is then a signature change on both
    /// sides of a P/Invoke boundary, where a mismatch does not fail to compile -
    /// it reads the stack wrong and hands the game a world scale built out of
    /// whatever was in the next register. A struct moves that failure somewhere it
    /// can be checked once, and lets the panel grow without the ABI moving.
    ///
    /// Every field is four bytes wide, so the sequential layout needs no packing
    /// directive to line up with the C struct in bvr_api.h.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BvrOverlayState
    {
        public float WorldScale;
        public float RenderScale;

        /// <summary>0 = alternate-frame, 1 = native stereo, 2 = DR.</summary>
        public int StereoMode;

        /// <summary>Push-only: a mode is waiting on a scene restart.</summary>
        public int StereoPending;

        public int CalibState;      // BvrCalibState
        public int CalibHand;       // 0 = weapon hand, 1 = shield hand
        public float CalibProgress; // 0..1 while the automatic sample runs
        public float CalibSpread;   // degrees of wobble in the last auto sample
        public int CalibHave;       // bit 0 weapon hand, bit 1 shield hand

        /// <summary>
        /// The manual adjustment for the hand named by CalibHand: where it sits
        /// relative to the controller in metres, and how it is turned in degrees.
        ///
        /// Six numbers rather than a matrix because these are what a SLIDER can
        /// show. The captured orientation stays a Mat3 on this side and is never
        /// round-tripped through angles - these compose on top of it, so the
        /// gimbal case and the convention question never touch the measured part.
        /// </summary>
        public float CalibX;
        public float CalibY;
        public float CalibZ;
        public float CalibYaw;
        public float CalibPitch;
        public float CalibRoll;

        /// <summary>Poll-only: what the panel is asking for.</summary>
        public int CalibCommand;

        /// <summary>
        /// Hide the player's own body in first person. Two-way: pushed so the
        /// checkbox shows the truth, read back so clicking it does something.
        /// </summary>
        public int HideBody;

        /// <summary>
        /// Motion-control hands and melee. EXPERIMENTAL, off by default - the
        /// hand posing fights the game's own animation and the swing detection
        /// is unfinished. The gamepad emulation is a separate path and stays on.
        /// </summary>
        public int MotionHands;

        /// <summary>Whether the keyed interface layer is shown at all.</summary>
        public int UiShow;

        /// <summary>How wide it hangs, in metres, at UiDistance.</summary>
        public float UiWidth;

        /// <summary>How far from the eye it sits, in metres.</summary>
        public float UiDistance;

        /// <summary>Flat-screen width in metres - the panel the menus and the
        /// campaign map appear on.</summary>
        public float ScreenWidth;

        /// <summary>Flat-screen distance in metres.</summary>
        public float ScreenDistance;

        /// <summary>Poll-only. 1 when the panel's "Run tableau probe" button has
        /// been clicked; cleared by the native side as it is read, so one click
        /// is one run.</summary>
        public int TableauProbe;

        /// <summary>Push-only, so the button can report without the player going
        /// to the log: 0 idle, 1 running, 2 finished, 3 failed.</summary>
        public int TableauProbeState;

        /// <summary>
        /// Sharpening of the submitted eye images, 0..1, 0 = off. Two-way.
        /// Appended LAST, matching bvr_api.h, so no earlier field moves.
        /// </summary>
        public float Sharpen;

        /// <summary>
        /// Menu pointer speed, in screen widths per second at full stick. Two-way
        /// and applied live. Nothing native reads it - it is a managed setting
        /// that rides this struct because the panel is the only place it can be
        /// changed from inside a headset. Appended LAST, matching bvr_api.h.
        /// </summary>
        public float StickMouseSpeed;

        /// <summary>
        /// Poll-only. 1 when the panel's "Apply now" button under the resolution
        /// slider has been clicked; cleared by the native side as it is read, so
        /// one click is one rebuild.
        ///
        /// The slider itself is half live without this: shading the scene at a
        /// smaller fraction of the eye target is immediate, and there is nothing
        /// to rebuild for it. Raising the resolution has no detail to ask for
        /// until the targets themselves are bigger, which is what Apply does.
        /// Appended LAST, matching bvr_api.h, so no earlier field moves.
        /// </summary>
        public int ResolutionApply;

        /// <summary>Push-only, so the button can report rather than the player
        /// wondering what a black flash meant: 0 idle, 1 rebuilding, 2 live at
        /// the new size, 3 failed.</summary>
        public int ResolutionState;

        /// <summary>
        /// Physical collision combat (TEST 244): the player's swings hit, their
        /// raised shield blocks, and arrows stop on it. Two-way, like HideBody,
        /// so the panel's checkbox shows the truth and clicking it means
        /// something. Off leaves motion-control hands and weapons posed and the
        /// game's own trigger attacks in charge - nothing of this mod's physics
        /// remains. Appended LAST, matching bvr_api.h.
        /// </summary>
        public int PhysicalCombat;

        /// <summary>
        /// The full-body IK (TEST 253-257): the pelvis and torso follow the
        /// player, the legs are solved to the game's own gait, and the held body
        /// (body_anim = legs) keeps the animation off the torso. Two-way, like
        /// HideBody. Off reverts to the normal animated body: no follow, no leg
        /// solve, no held pose, and the camera anchors to the engine's own head
        /// again. Appended LAST, matching bvr_api.h.
        /// </summary>
        public int BodyIk;

        /// <summary>
        /// Motion-control combat (TEST 245-252): swings write the game's own
        /// attack direction, a raised hand is the guard, the blade lands the hit
        /// itself and the body holds still through engine attacks. Two-way, like
        /// the toggles above. Off is the game's own combat - the trigger and the
        /// movement stick - with every animation playing. Appended LAST,
        /// matching bvr_api.h.
        /// </summary>
        public int MotionMelee;

        /// <summary>
        /// Friendly fire (TEST 261): whether a physical swing can land on
        /// friends and neutrals. Off by default - the vanilla single-player
        /// rule, enemies only. Two-way, like the toggles above; missions that
        /// forbid friendly fire refuse it either way. Appended LAST, matching
        /// bvr_api.h.
        /// </summary>
        public int FriendlyFire;

        /// <summary>
        /// Which controller the grip calibration saves under - the dropdown on
        /// the Calibration section. Two-way. Appended LAST, matching bvr_api.h.
        /// </summary>
        public int GripController;

        /// <summary>Body holsters (TEST 271). Two-way. Appended LAST, matching
        /// bvr_api.h.</summary>
        public int Holster;

        /// <summary>The campaign map in VR (map_stereo). Two-way. Appended LAST,
        /// matching bvr_api.h.</summary>
        public int MapVr;

        /// <summary>The VR tab (TEST 293): the head camera's offset along the
        /// body's heading in metres, and the body-turn deadzone in degrees.
        /// Two-way. Recenter is the panel's one-shot, like ResolutionApply.
        /// Appended LAST, matching bvr_api.h.</summary>
        public float HeadUp;
        public float HeadForward;
        public float BodyTurnDeg;
        public int Recenter;

        /// <summary>
        /// Hands only. Two-way, like HideBody, and only has an effect while
        /// HideBody is on: keeps the hand meshes drawn alongside the weapon
        /// instead of hiding everything but the weapon. Appended LAST, matching
        /// bvr_api.h.
        /// </summary>
        public int HandsOnly;

        /// <summary>TEST 356, the VR tab: the player's standing height in metres
        /// (0 = the game's own size), a one-shot "measure now", and the scale it
        /// gave (push-only). Appended LAST, after HandsOnly.</summary>
        public float PlayerHeight;
        public int HeightMeasure;
        public float BodyScale;

        /// <summary>TEST 377e: the wrist HUD's width in metres. Two-way; native
        /// applies it the moment the slider moves, managed saves it.</summary>
        public float WristHudWidth;

        /// <summary>TEST 427: foveated rendering (NVIDIA VRS), 0 off, 1 wide,
        /// 2 balanced, 3 narrow. Two-way; native applies it the moment the
        /// dropdown moves, managed saves it.</summary>
        public int Vrs;

        /// <summary>TEST 428: 1 = the VRS centre follows the eye tracker's gaze.
        /// Two-way; native applies it, managed saves vrs_eye.</summary>
        public int VrsEye;

        /// <summary>TEST 442: the head camera's height in the saddle, metres -
        /// used instead of HeadUp while riding. Two-way; managed saves
        /// head_offset_mounted_up_m.</summary>
        public float HeadUpMounted;

        /// <summary>TEST 450: the arms' length against the character's own,
        /// 0.80-1.30. Two-way; managed saves arm_length_scale.</summary>
        public float ArmScale;

        /// <summary>TEST 447: seconds the chest takes to follow the head's turn,
        /// 0 = instant. Two-way; managed saves body_weight_s.</summary>
        public float BodyWeight;

        /// <summary>TEST 462: 1 = the crosshair's hit marker shows (no reticle).
        /// Two-way; managed saves hud_hit_marker.</summary>
        public int HudHitMarker;

        /// <summary>TEST 462: 1 = the pickup prompt (item name and button) shows.
        /// Two-way; managed saves hud_prompts.</summary>
        public int HudPrompts;

        /// <summary>TEST 485: 1 = five-finger hands (VrHandMesh). Two-way;
        /// managed saves finger_mesh.</summary>
        public int HandFingers;

        /// <summary>TEST 527: the campaign map palette's width in metres, apart
        /// from WristHudWidth (the battle wrists). Two-way; native applies it,
        /// managed saves hand_ui_width_m.</summary>
        public float MapHandUiWidth;

        /// <summary>1 = the left thumbrest is a D-pad modifier for the right
        /// stick too. Two-way; managed saves dpad_thumbrest.</summary>
        public int DpadThumbrest;

        /// <summary>TEST 620: battle markers / campaign map labels size, x the
        /// game's UI scale (VrUiSplit). Two-way; managed saves
        /// ui_world_scale_battle / ui_world_scale_map.</summary>
        public float UiWorldScaleBattle;
        public float UiWorldScaleMap;

        /// <summary>TEST 645: 1 = the weapon is solid against your own shield
        /// (TEST 642). Two-way; managed saves collide_own_shield.</summary>
        public int OwnShieldBash;

        /// <summary>TEST 648: the pre-battle formation markers' size (VrUiSplit).
        /// Two-way; managed saves ui_world_scale_deploy_markers.</summary>
        public float UiWorldScaleDeploy;

        /// <summary>TEST 711: re-centre a new screen (1/0), and only when the head is
        /// turned more than UiRecenterAngle degrees away. Two-way; managed saves
        /// ui_recenter_new_screen and ui_recenter_angle.</summary>
        public int UiRecenterNewScreen;
        public float UiRecenterAngle;

        /// <summary>fov_true_vertical (1/0): symmetric across, the runtime's own
        /// up/down. Two-way; managed saves fov_true_vertical.</summary>
        public int FovTrueVertical;

        /// <summary>TEST 750, the Calibration tool tab. ToolMode (0 weapon, 1 hand
        /// grip) and ToolHand (0 main, 1 off) are the panel's, echoed back.
        /// ToolLive is push-only: 1 held now, 0 dropped (sliders grey, saves
        /// stay), -1 nothing. ToolValue is x, y, z m then yaw, pitch, roll deg.
        /// ToolCommand is poll-only, see <see cref="BvrToolCommand"/>. Appended
        /// LAST, matching bvr_api.h.</summary>
        public int ToolMode;
        public int ToolHand;
        public int ToolLive;
        public int ToolCommand;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public float[] ToolValue;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ToolLabel;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ToolStatus;

        /// <summary>TEST 751: 0 Original, 1 Profile 1, 2 Profile 2 - two-way.
        /// ToolWielded: the main hand's slot 0..3 or -1, push-only. ToolSlot0..3:
        /// the weapon slots' names, "" when empty, push-only. Appended LAST.</summary>
        public int ToolProfile;
        public int ToolWielded;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 48)]
        public string ToolSlot0;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 48)]
        public string ToolSlot1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 48)]
        public string ToolSlot2;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 48)]
        public string ToolSlot3;

        /// <summary>TEST 770: fov crop tangent multipliers, 0.5-1.0. Two-way;
        /// managed saves fov_crop_h / fov_crop_v. Appended LAST.</summary>
        public float FovCropH;
        public float FovCropV;

        /// <summary>TEST 785: detailed fov crop - 1 = the four edges apply instead
        /// of FovCropH/V. Two-way; managed saves fov_crop_detailed / _left /
        /// _right / _up / _down. Appended LAST.</summary>
        public int FovCropDetailed;
        public float FovCropLeft;
        public float FovCropRight;
        public float FovCropUp;
        public float FovCropDown;

        /// <summary>TEST 790: sharpener, 0 CAS / 1 RCAS. Two-way; managed saves
        /// sharpen_mode. Appended LAST.</summary>
        public int SharpenMode;

        /// <summary>TEST 797: DLSS preset - the cfg's dlss_preset (0 auto, 10-13 J-M,
        /// -1 DLSS's own). Two-way; managed saves it. Appended LAST.</summary>
        public int DlssPreset;
        public int GripToggle;
    }

    /// <summary>TEST 750: the Calibration tool's buttons. SAME VALUES as
    /// BVR_TOOL_CMD_* in bvr_api.h.</summary>
    public enum BvrToolCommand
    {
        None = 0,
        Adjust = 1,      // a slider moved; ToolValue carries it
        SaveItem = 2,
        SaveClass = 3,
        Zero = 4,
        Revert = 5,
        SaveHand = 6,
        Equip0 = 7,      // 7..10: wield weapon slot 0..3 (TEST 751)
        Sheathe = 11,
    }

    /// <summary>
    /// Bits in <see cref="BvrHandState.Buttons"/>, PER HAND.
    ///
    /// These are what xr_input.cpp actually packs. The old version of this enum
    /// named A, B, X, Y, Menu and two thumbstick clicks in bits 0..6, and not one
    /// of them lined up: native writes primary in bit 0, secondary in bit 1, menu
    /// in bit 3, and the two analog-past-threshold bits in 4 and 5. Nothing read
    /// the enum, so nothing was visibly broken - it was simply waiting to hand the
    /// first caller a menu press when the trigger was pulled.
    ///
    /// PER HAND is the part worth saying twice. OpenXR binds an ACTION, and the
    /// same action is bound to a different physical button on each controller, so
    /// "primary" is X on the left hand and A on the right. That is not a quirk of
    /// this mod - it is how the Touch binding table in xr_input.cpp is written, and
    /// it is why these are not called A and X.
    ///
    /// Bit 2 is unbound. Touch has no third face button per hand, and leaving a
    /// hole is more honest than shifting the others up to close it: the numbers
    /// here have to match the native side exactly, and native counts from a
    /// binding table rather than from this enum.
    /// </summary>
    [Flags]
    public enum BvrButton
    {
        None = 0,

        /// <summary>Touch: X on the left hand, A on the right.</summary>
        Primary = 1 << 0,

        /// <summary>Touch: Y on the left hand, B on the right.</summary>
        Secondary = 1 << 1,

        /// <summary>The one menu button, on the left controller only.</summary>
        /// <summary>
        /// The stick pressed in - L3 and R3.
        ///
        /// Bit 2, which this enum used to record as unbound because Touch has no
        /// third face button. The click is what it was worth saving for: it is
        /// the last modifier a Touch or Sense controller has spare once the
        /// triggers and grips are spoken for, which is what makes a D-pad chord
        /// possible at all.
        /// </summary>
        ThumbClick = 1 << 2,

        Menu = 1 << 3,

        /// <summary>Trigger past 0.7. The analog value is in
        /// <see cref="BvrHandState.Trigger"/> and is usually what you want.</summary>
        Trigger = 1 << 4,

        /// <summary>Grip past 0.7. Analog value in
        /// <see cref="BvrHandState.Grip"/>.</summary>
        Grip = 1 << 5,
    }

    public enum BvrStatus
    {
        Ok = 0,
        NotInitialized = -1,
        NoRuntime = -2,
        NoHeadset = -3,
        GraphicsMismatch = -4,
        SessionNotReady = -5,
        XrError = -6,
    }

    public enum BvrEye
    {
        Left = 0,
        Right = 1,
    }

    /// <summary>Mirrors XrSessionState. Reported by bvr_poll_events; the render
    /// thread owns the actual event queue.</summary>
    public enum XrSessionState
    {
        Unknown = 0,
        Idle = 1,
        Ready = 2,
        Synchronized = 3,
        Visible = 4,
        Focused = 5,
        Stopping = 6,
        LossPending = 7,
        Exiting = 8,
    }

    internal static class NativeMethods
    {
        internal const string Dll = "BannerlordVR.Native.dll";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_get_version(byte[] buffer, int bufferLen);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_init();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_create_session();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_get_recommended_size(out uint width, out uint height);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_poll_events(out int sessionState);

        // [Out] is explicit rather than implied. Blittable arrays are pinned by
        // Mono and native writes do come back - bvr_get_version proves that in
        // this very file - but the direction is load-bearing here, so it is
        // stated rather than inferred by a future reader.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_get_predicted_views([Out] BvrEyeView[] views, out long displayTime);

        /// <summary>
        /// Begins the OpenXR frame BEFORE the engine renders it, and publishes
        /// the pose for the display time of the frame about to be drawn.
        ///
        /// Called at the top of the camera build, so bvr_get_predicted_views
        /// immediately below returns a pose meant for THIS frame rather than one
        /// located a frame ago for a moment that has passed.
        ///
        /// It BLOCKS inside xrWaitFrame, and that is the point: the compositor
        /// paces the game from here rather than the game racing ahead and being
        /// throttled later by a Present it cannot see. SessionNotReady is the
        /// normal answer outside a session and is not an error.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_prepare_frame();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_begin_texture_capture(int eye, uint width, uint height);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_end_texture_capture(int eye);

        /// <summary>The same capture bracket for the interface render target
        /// (TEST 299). See VrUiTarget.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_begin_ui_capture(uint width, uint height);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_end_ui_capture();

        /// <summary>1 while the Gauntlet views are drawing into the dense UI
        /// target, 0 while they are back on the game window. Native keys and
        /// submits whichever one this names.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_ui_source(int fromTarget);

        /// <summary>TEST 531: the campaign map bar's own render target (BVR_HAND),
        /// captured like the UI target. bvr_set_hand_source(1) while the MapBar
        /// layer draws into it; the state is 0 off, 1 checking, 2 content seen,
        /// 3 empty (swap the view back).</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_begin_hand_capture(uint width, uint height);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_end_hand_capture();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_hand_source(int live);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_get_hand_source_state();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_used_views([In] BvrEyeView[] views, long displayTime);

        /// <summary>Names the eye the engine is about to render this frame, for
        /// alternate-frame rendering. -1 leaves AFR mode.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_afr_eye(int eye);

        /// <summary>1 when the eye index last published was consumed by a real
        /// scene capture, 0 when that tick produced no render. Only advance the
        /// alternation when this reads 1: the toggle runs on the game tick and
        /// the capture runs in Present, and a tick without a render desynchronises
        /// the two permanently. A wrong eye label reverses the sign of the whole
        /// warp.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_afr_eye_consumed();

        /// <summary>The eye the engine should draw next. Owned by the native
        /// capture path, so it advances on the same event that consumes it and
        /// cannot drift from the frames actually captured. Read this instead of
        /// keeping an alternation on the game tick: the two run on different
        /// clocks, and a label out of step with its frame reverses the sign of
        /// the whole warp.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_next_afr_eye();

        /// <summary>Hold the alternation on one eye (>= 0), or release it back to
        /// the native capture path (-1). Pass 0 while the source eye is pinned.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_pin_afr_eye(int eye);

        /// <summary>
        /// 1 once the warp can actually synthesise an eye: shader built,
        /// parameters set, and a depth buffer that has PROVED it contains a
        /// drawn scene rather than one the search merely settled on.
        ///
        /// Ask before pinning the source eye - see VrAfrRenderer.AlternatesEye.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_depth_reproject_ready();

        /// <summary>Stamps which piece of managed VR code is in flight, so the
        /// native crash logger can name it. See VrBreadcrumb.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void bvr_breadcrumb(int id);

        /// <summary>
        /// The yaw of the anchor the eye frames are built on - the game's own
        /// camera rotation, which the OpenXR runtime knows nothing about.
        /// Without it the compositor reprojects a held AFR eye for head motion
        /// alone and leaves the mouse-turn uncorrected. Published every frame,
        /// before the frame renders.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_body_yaw(float yawRadians);

        /// <summary>
        /// Where the anchor is, companion to the yaw above. A held AFR eye was
        /// drawn from where the game camera was a frame ago, and riding moves it
        /// further in that frame than the IPD separates the eyes - a false
        /// disparity larger than the real one. OpenXR basis and metres.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_body_position(float x, float y, float z);

        /// <summary>
        /// The LIVE anchor, where the two above are the RENDERED one. Latching the
        /// pair camera makes the stereo correct by drawing both eyes from one
        /// anchor, at the cost of that anchor advancing only once per pair;
        /// publishing the live one lets the compositor carry each eye the rest of
        /// the way. With latching off the two are identical.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_body_target(float yawRadians, float x, float y, float z);

        /// <summary>
        /// Which simulation tick the world is on. Under AFR the native capture
        /// path holds a freshly rendered eye back until its partner has been
        /// rendered from the SAME tick, so the pair on screen always agrees
        /// with itself. Negative disables the pairing.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_sim_generation(int generation);

        /// <summary>
        /// Whether draw-duplication stereo is the running mode. The native side
        /// owns the duplication, so it has to be told - and it must be OFF for
        /// every other mode, or enabling the path would change what AFR does.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_draw_stereo(int active);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_native_stereo(int active);

        /// <summary>
        /// Whether a VR mission is rendering.
        ///
        /// 0 puts the headset on the flat screen: the game's own backbuffer on a
        /// quad layer, which is what the main menu, the campaign map, the
        /// inventory and every other non-mission screen need. Those are 2D
        /// images with no camera behind them, so there is nothing to render in
        /// stereo, and without this the headset showed a cleared colour while
        /// the game was perfectly playable on the monitor.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_mission_active(int active);

        /// <summary>
        /// Rebuilds the eye swapchains at a new render scale, live.
        ///
        /// A HANDSHAKE, not a call. The eye TEXTURES belong to the engine and
        /// only this side can release and re-ask for them; the SWAPCHAINS belong
        /// to OpenXR and only the frame-loop thread may touch them. So each half
        /// watches a stage rather than blocking on the other. VrResolution owns
        /// the sequence; see the note there and the longer one in xr_context.cpp.
        ///
        /// scale is ABSOLUTE, 0.25 to 1.0 - the same number render_scale holds -
        /// and is passed rather than re-read from the config because the write
        /// and this call would otherwise race.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_request_rescale(float scale);

        /// <summary>Managed has the new textures; rebuild the swapchains to
        /// match. The first irreversible step, deliberately last.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_rescale_swap();

        /// <summary>rgl would not allocate at the new size. Puts the view
        /// configs back to the previous scale - which the untouched swapchains
        /// still fit - so managed can re-allocate and get a picture back.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_rescale_abort(float previousScale);

        /// <summary>0 idle, 1 draining frames, 2 let go and re-sized with the
        /// swapchains untouched, 3 swapping them over, 4 failed.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_rescale_stage();

        /// <summary>Tells native the new targets are live and the normal frame
        /// path may resume.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_rescale_finish();

        /// <summary>
        /// Composite the game interface OVER the world instead of replacing it.
        /// For menus meant to be used while playing - the tactics wheel above
        /// all, which is useless without the field behind it.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_ui_overlay(int active);

        /// <summary>ui_per_eye: this frame's interface was laid out for
        /// <paramref name="eye"/>, projected from this head pose (stage, OpenXR
        /// axes). See VrUiProjection.</summary>
        /// <summary>World units per metre of the published eyes: 1 in a battle, the
        /// table's scale on the campaign map (native stereo's IPD bounds, TEST 287).</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_stereo_world_scale(float unitsPerMetre);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_ui_projection(int eye, float px, float py, float pz,
                                                         float qx, float qy, float qz, float qw);

        /// <summary>TEST 377: the wrist HUDs' widget rectangles. Native crops them
        /// onto each inner wrist and punches them off the head panel.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_wrist_regions(ref BvrWristRegions regions);

        /// <summary>TEST 590: the interface image shared out between the world
        /// panel, the HUD and the hands. Active 0 = one full-window image.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_ui_split(ref BvrUiSplit split);

        /// <summary>TEST 591: draw the interface at factor x the window (0 off).</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_ui_supersample(float factor);

        /// <summary>TEST 591: 0 off, 1 drawing into the dense target, 2 armed but
        /// the interface never reached the window's backbuffer.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_get_ui_supersample();

        /// <summary>TEST 377d: the wrist panel's pose on the DRAWN wrist, in stage
        /// space (OpenXR axes, metres). valid 0 returns that hand to the
        /// controller-relative placement.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_wrist_pose(int hand, int valid, float px, float py, float pz,
                                                      float qx, float qy, float qz, float qw);

        /// <summary>TEST 378: whether the laser may point at the game's interface,
        /// from which hand, whether to draw its beam there, and the active hand's
        /// trigger (for the VR panel).</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_laser(int enabled, int hand, int beamToUi, int click,
                                                  float groundDistance);

        /// <summary>TEST 378: where the laser landed on the last frame native drew.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_get_laser(out BvrLaser laser);

        /// <summary>TEST 651: the laser ray as drawn (stage space, smoothed), when
        /// that hand was drawn in the last 100 ms.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_get_laser_ray(int hand, out BvrVec3 origin, out BvrVec3 dir);

        /// <summary>TEST 378: pin the interface quad in the room while a menu is open.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_ui_pin(int pinned);

        /// <summary>TEST 379: ui_eye_lag per context (the map vs a mission).</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_ui_eye_lag(int lag);

        /// <summary>TEST 633: display refreshes per app frame, smoothed.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern float bvr_get_periods_per_frame();

        /// <summary>
        /// Whether a mission exists at all - distinct from whether the STEREO
        /// path is live. The two parted company once read-menus started
        /// replacing the view: the window mirror and the backbuffer clear
        /// belong to "there is a battle", not to "the headset is showing it in
        /// stereo".
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_in_mission(int active);

        /// <summary>Size and distance of the keyed interface layer, in metres.
        /// Set from the VR panel; saved and pushed back by VrUiSettings.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_ui_geometry(float width, float distance);

        /// <summary>Size and distance of the FLAT SCREEN, in metres. Changing the
        /// distance re-pins it, since its pose is fixed when it is placed.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_screen_geometry(float width, float distance);

        /// <summary>
        /// Forgets where the flat screen was pinned, so the next frame that
        /// shows it places it in front of the head again. Wired to recentre.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_repin_flat_screen();

        /// <summary>TEST 710: re-pins only if the head is turned more than
        /// <paramref name="degrees"/> away from the screen.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_repin_flat_screen_if_turned(float degrees);

        /// <summary>
        /// Selects which of the three single-render stereo modes runs, and hands
        /// over the stereo geometry they need.
        ///
        ///   0  AFR - the eye that was not rendered is HELD from its own last
        ///      real render and submitted with the pose it was drawn at.
        ///   1  DR - that eye is SYNTHESISED from the rendered one through its
        ///      depth buffer, so both eyes come from one instant.
        ///   2  DR+AFR hybrid - held while the head is still, warped once it
        ///      moves.
        ///
        /// The geometry is applied in every mode including 0; the reticle places
        /// itself from the same focal length and baseline under AFR.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_depth_reproject(int mode, float focalPx, float baseline,
                                               float zNear, float zFar);

        // --- settings panel --------------------------------------------------
        //
        // The panel is drawn natively as an OpenXR quad layer, because that is the
        // only way to put pixels in front of a player wearing the headset: the
        // headset sees the retargeted scene view and nothing else, so a menu built
        // with the game's own UI would work perfectly and be invisible.
        //
        // The values themselves stay owned here - this is the half that can apply a
        // world scale and write the config - so the two sides exchange a mirror.

        /// <summary>Shows or hides the panel.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_overlay_toggle();

        /// <summary>1 when the panel is on screen.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_overlay_visible();

        /// <summary>Mouse-wheel notches for the panel; the render thread cannot
        /// read the wheel itself.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_overlay_scroll(float notches);

        /// <summary>Shows or hides the panel outright. The calibration key opens
        /// it ON the calibration tab, and a toggle would close it for anyone who
        /// already had it open.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_overlay_show(int visible);

        /// <summary>Tells the panel what is currently true. Does not come back as
        /// a change, or every push would rewrite the config file.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_overlay_push(ref BvrOverlayState state);

        /// <summary>Returns Ok only when the PANEL moved something or is asking
        /// for a command, so an edit is applied and persisted exactly once.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_overlay_poll(out BvrOverlayState state);

        /// <summary>
        /// Publishes one frame's worth of camera state to the native
        /// view-projection patcher. See bvr_api.h for the layout of the 26
        /// floats; a flat array rather than a struct because Mono pins a
        /// blittable float[] and there is no layout left to drift.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_vp_frame([In] float[] data, int floatCount,
                                                    int eye, int valid);

        /// <summary>
        /// The eye the engine is NOT drawing this frame, in the same layout.
        ///
        /// The native side builds a second copy of every view constant buffer
        /// from this, so a duplicated draw can be issued from the other
        /// viewpoint. Publishing it costs one more call per frame and nothing
        /// else; when the second draw is off, the copy is never built.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_vp_frame_other([In] float[] data, int floatCount,
                                                          int eye, int valid);

        [DllImport(Dll, EntryPoint = "bvr_get_input_state", CallingConvention = CallingConvention.Cdecl)]
        private static extern int bvr_get_input_state_native(out BvrInputState state);

        /// <summary>TEST 452: once per game frame, before the hands are posed - the
        /// game thread locates both controllers for its own moment plus leadFrames
        /// display periods, and every bvr_get_input_state after it takes its poses
        /// from that sample (buttons stay the newest).</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_latch_input(float leadFrames);

        /// <summary>TEST 482: the touch sensors - the left hand in bits 0-7, the
        /// right in 8-15, each a VrFingerRig.Touch* set. Never swapped by
        /// controls_swap_trigger_grip: the finger hands read the physical controls.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint bvr_get_finger_touch();

        /// <summary>The controllers, with controls_swap_trigger_grip applied: on,
        /// the GRIP does everything the trigger did (two-hand hold, the slide,
        /// holsters, the bow string, menus) and the TRIGGER is the grip - the
        /// game's bumpers. Swapped here, where every reader gets its state, so no
        /// reader can miss it.</summary>
        internal static int bvr_get_input_state(out BvrInputState state, bool battleGrip = false)
        {
            int status = bvr_get_input_state_native(out state);
            if (status == (int)BvrStatus.Ok && SwapTriggerGrip)
            {
                SwapTriggerAndGrip(ref state.Left);
                SwapTriggerAndGrip(ref state.Right);
            }

            if (status == (int)BvrStatus.Ok)
                VR.VrGrip.Read(ref state, battleGrip);
            return status;
        }

        private static bool? _swapTriggerGrip;

        /// <summary>The physical grip of a hand read through bvr_get_input_state,
        /// whatever controls_swap_trigger_grip says.</summary>
        internal static float PhysicalGrip(in BvrHandState hand) =>
            SwapTriggerGrip ? hand.Trigger : hand.Grip;

        /// <summary>The physical trigger, likewise.</summary>
        internal static float PhysicalTrigger(in BvrHandState hand) =>
            SwapTriggerGrip ? hand.Grip : hand.Trigger;

        private static bool SwapTriggerGrip
        {
            get
            {
                if (!_swapTriggerGrip.HasValue)
                    _swapTriggerGrip = VR.VrConfig.Bool("controls_swap_trigger_grip", false);
                return _swapTriggerGrip.Value;
            }
        }

        private static void SwapTriggerAndGrip(ref BvrHandState hand)
        {
            float trigger = hand.Trigger;
            hand.Trigger = hand.Grip;
            hand.Grip = trigger;

            const int both = (int)BvrButton.Trigger | (int)BvrButton.Grip;
            int was = hand.Buttons;
            hand.Buttons = (was & ~both)
                           | ((was & (int)BvrButton.Trigger) != 0 ? (int)BvrButton.Grip : 0)
                           | ((was & (int)BvrButton.Grip) != 0 ? (int)BvrButton.Trigger : 0);
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_trigger_haptic(int hand, float amplitude, float durationSeconds);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void bvr_shutdown();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr LoadLibraryW(string path);
    }

    /// <summary>
    /// Front door to the native layer. Everything is null-safe: if the native DLL
    /// is missing or fails to initialize, <see cref="IsAvailable"/> stays false and
    /// the game runs flat. The mod must never brick a non-VR launch.
    /// </summary>
    public static class NativeBridge
    {
        private static bool _loadAttempted;

        public static bool IsAvailable { get; private set; }
        public static string LastError { get; private set; } = "not loaded";
        public static string NativeVersion { get; private set; } = "n/a";

        /// <summary>
        /// Loads BannerlordVR.Native.dll by absolute path. Required because the
        /// OS native search path is rooted at Bannerlord.exe, not at our module's
        /// bin folder, so a bare DllImport would not find it.
        /// </summary>
        public static bool TryLoad()
        {
            if (_loadAttempted)
                return IsAvailable;
            _loadAttempted = true;

            try
            {
                string dir = Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath);
                string path = Path.Combine(dir, NativeMethods.Dll);

                if (!File.Exists(path))
                {
                    LastError = "native DLL not present at " + path + " (Phase 0/2 not built yet)";
                    return false;
                }

                if (NativeMethods.LoadLibraryW(path) == IntPtr.Zero)
                {
                    LastError = "LoadLibrary failed, win32 error " + Marshal.GetLastWin32Error();
                    return false;
                }

                var buffer = new byte[64];
                int len = NativeMethods.bvr_get_version(buffer, buffer.Length);
                NativeVersion = len > 0
                    ? System.Text.Encoding.ASCII.GetString(buffer, 0, Math.Min(len, buffer.Length))
                    : "unknown";

                IsAvailable = true;
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                IsAvailable = false;
                return false;
            }
        }
    }
}
