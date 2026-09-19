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

        /// <summary>0 = alternate-frame, 1 = native stereo, 2 = AFW.</summary>
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
        internal static extern int bvr_afw_ready();

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
        /// Whether full AFR is running: two game frames inside one display
        /// frame, so both eyes of a pair are real renders of one simulation
        /// tick at the full refresh rate.
        ///
        /// The native side needs it because the first Present of a pair must
        /// hand its begun XR frame BACK rather than end it - that is what makes
        /// the second eye render from the same locate and the same world, and
        /// what makes the pair go up together. Published every frame beside the
        /// eye index; nothing infers it from the mode, because the mode can
        /// change between a deferred frame and its partner.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_full_afr(int active);

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
        /// Composite the game interface OVER the world instead of replacing it.
        /// For menus meant to be used while playing - the tactics wheel above
        /// all, which is useless without the field behind it.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_ui_overlay(int active);

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

        /// <summary>
        /// Selects which of the three single-render stereo modes runs, and hands
        /// over the stereo geometry they need.
        ///
        ///   0  AFR - the eye that was not rendered is HELD from its own last
        ///      real render and submitted with the pose it was drawn at.
        ///   1  AFW - that eye is SYNTHESISED from the rendered one through its
        ///      depth buffer, so both eyes come from one instant.
        ///   2  AFW+AFR hybrid - held while the head is still, warped once it
        ///      moves.
        ///
        /// The geometry is applied in every mode including 0; the reticle places
        /// itself from the same focal length and baseline under AFR.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_set_afw(int mode, float focalPx, float baseline,
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

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int bvr_get_input_state(out BvrInputState state);

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
