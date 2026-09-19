using System;
using BannerlordVR.Interop;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Alternate-frame rendering: one scene render per frame, alternating which
    /// eye it is for.
    ///
    /// This exists because rgl 1.4.8 cannot render one scene into two views in
    /// the same frame. Measured, not assumed: with two views live, dynamic meshes
    /// come out in pieces - heads and hands without bodies - on the monitor as
    /// well as in the headset, and removing the engine's own view crashes it
    /// outright. Whatever the engine does per frame for skinned geometry, it does
    /// once, and the first view to ask claims it.
    ///
    /// So AFR does not add a view at all. It retargets the engine's OWN view into
    /// our eye texture and swings its camera between the eyes on alternate
    /// frames. One render, one claimant, whole characters.
    ///
    /// Three details separate this from the version that shimmers:
    ///
    ///   * Both eyes are submitted every frame from held copies. A swapchain
    ///     returns a different image on each acquire, so an eye left untouched
    ///     shows a frame from several acquires ago, not the last one.
    ///   * Each eye carries the pose it was truly rendered with, so the
    ///     compositor reprojects the older eye instead of smearing it.
    ///   * Only one scene render happens per frame, which buys back roughly half
    ///     the GPU cost - spend it on resolution, where it fights the shimmer
    ///     directly.
    /// </summary>
    public static class VrAfrRenderer
    {
        /// <summary>
        /// The master switch, from the config. Separate from the mode below on
        /// purpose: this says AFR is BUILT AND ALLOWED, the mode says it is the one
        /// currently running. Turning this off takes AFR off the menu entirely
        /// rather than leaving a toggle that silently does nothing.
        /// </summary>
        private static readonly bool Allowed = VrConfig.Bool("afr", false);

        /// <summary>
        /// True while alternate-frame rendering is the active strategy. Everything
        /// that used to read a fixed config value reads this instead, so selecting
        /// native stereo stands the whole AFR path down without any of its callers
        /// needing to know a second mode exists.
        /// </summary>
        /// <summary>
        /// True in AFR and in AFW. Both render one eye per frame through the
        /// engine's own view and differ only in what the native capture does with
        /// the eye that was not drawn, so everything this class owns is shared.
        /// </summary>
        public static bool Enabled =>
            Allowed && VrRenderMode.UsesAfrPipeline(VrRenderMode.Active);

        /// <summary>Whether AFR can be offered at all.</summary>
        public static bool Available => Allowed;

        /// <summary>
        /// Releases everything AFR holds, so the other stereo mode starts from a
        /// clean state. Safe to call repeatedly and safe to call when AFR was never
        /// running - which is exactly how it is called.
        /// </summary>
        /// <summary>
        /// Points the engine's own scene view back at the screen.
        ///
        /// We aim it at our eye texture to get the world, and until now nothing
        /// ever aimed it back: every stand-down simply dropped the reference and
        /// left the engine's view pointing into a texture the engine does not
        /// own and did not ask for. Whether that is survivable depends on what
        /// the engine does with the view afterwards, which is not a thing to
        /// leave to chance across a mission teardown - and a crash was observed
        /// inside TaleWorlds.Native.dll a fraction of a second after AFR stood
        /// down, which is at least consistent with it.
        ///
        /// null is the engine's own "render to the screen". Failure is swallowed
        /// deliberately: this runs on paths that are already unwinding, and a
        /// throw here would replace whatever went wrong with a different fault.
        /// </summary>
        /// <summary>Last factor handed to the engine, so this is a comparison
        /// rather than an engine call on every frame.</summary>
        private static float _appliedScale = -1.0f;

        /// <summary>
        /// Shades the scene at a fraction of the eye target and lets the engine
        /// upscale into it. See VrResolution for why this is what makes the
        /// resolution slider do something before the next launch.
        ///
        /// Applied on retarget as well as on change: a new scene gets a new view,
        /// and a view starts at whatever the engine's default is rather than at
        /// what the player last chose.
        /// </summary>
        public static void ApplyResolutionScale()
        {
            if (!_retargeted || _engineView == null)
                return;

            float factor = VrResolution.LiveFactor;
            if (Math.Abs(factor - _appliedScale) < 1e-3f)
                return;

            try
            {
                _engineView.SetScale(new Vec2(factor, factor));
                _appliedScale = factor;

                VrLog.Info(string.Format(
                    "Resolution: shading at {0:P0} of the eye target, which was allocated "
                    + "at launch. Nothing is resized - the target, the swapchain and every "
                    + "copy keep their dimensions; only the pixels the scene is shaded at "
                    + "change.{1}",
                    factor,
                    VrResolution.CappedByLaunchSize
                        ? " Asking for more than the target holds, so the extra waits for a "
                          + "relaunch."
                        : string.Empty));
            }
            catch (Exception ex)
            {
                // Not fatal and not retried into a log flood: the view keeps the
                // engine's own scaling and the slider falls back to being a
                // next-launch setting, which is what it was before.
                _appliedScale = factor;
                VrLog.Error("Resolution: SceneView.SetScale was refused; the slider is a "
                            + "next-launch setting again. render_scale_live = 0 silences "
                            + "this.", ex);
            }
        }

        private static void ReleaseEngineView()
        {
            if (_engineView == null || !_retargeted)
                return;

            try
            {
                _engineView.SetRenderTarget(null);
            }
            catch (Exception ex)
            {
                VrLog.Error("AFR: could not hand the engine's view back to the screen; "
                            + "it is still pointing at the eye texture.", ex);
            }
        }

        private static void StandDownForOtherMode()
        {
            ReleaseEngineView();
            _retargeted = false;
            _bound = false;
            _boundScene = null;
            _engineView = null;
            _loggedBind = false;

            VrVpPatch.StandDown();
            NativeMethods.bvr_set_afr_eye(-1);
            NativeMethods.bvr_set_full_afr(0);
            NativeMethods.bvr_set_draw_stereo(0);
            NativeMethods.bvr_set_sim_generation(-1);

            if (!_stoodDownLogged)
            {
                _stoodDownLogged = true;
                VrLog.Info("AFR stood down; native stereo has the scene.");
            }
        }

        private static bool _stoodDownLogged;

        /// <summary>How AFR gets the head pose into the engine's render camera.</summary>
        private enum CameraMode
        {
            /// <summary>Do not touch the camera at all. The control that proved the
            /// render target is innocent.</summary>
            Off,

            /// <summary>Move the ENGINE'S OWN CombatCamera in place. Never sets
            /// CustomCamera. The default - see the note in <see cref="Tick"/>.</summary>
            Engine,

            /// <summary>One camera of ours, bound once via CustomCamera.</summary>
            Shared,

            /// <summary>The original: two eye cameras, re-bound via CustomCamera
            /// every frame.</summary>
            Eye,
        }

        private static readonly CameraMode Mode = ResolveMode();

        private static CameraMode ResolveMode()
        {
            // afr_camera = 0 predates the mode setting and still means "hands off".
            if (!VrConfig.Bool("afr_camera", true))
                return CameraMode.Off;

            // With the view-projection patcher running, the head pose is applied
            // downstream of the camera entirely, and moving the camera as well
            // would apply it twice - once here and once in the matrix - while
            // also feeding VrCameraDriver's anchor its own output. Hands off is
            // the correct mode, and it is the mode that renders the world
            // perfectly. It can still be overridden explicitly for comparison.
            // Substituting, not Enabled: this asks "is the head pose being applied
            // downstream of the camera", which is true only when matrices are
            // actually rewritten. Under vp_measure nothing is rewritten, so the
            // camera must still be moved and the answer is "engine".
            string fallback = VrVpPatch.Substituting ? "off" : "engine";

            switch (VrConfig.String("afr_camera_mode", fallback).Trim().ToLowerInvariant())
            {
                case "off":    return CameraMode.Off;
                case "shared": return CameraMode.Shared;
                case "eye":    return CameraMode.Eye;
                default:       return CameraMode.Engine;
            }
        }

        /// <summary>Apply the headset frustum to the engine's own camera. Off until
        /// the geometry is proven; the engine's own FOV draws the world correctly
        /// and a wrong FOV is a far smaller problem than an empty world.</summary>
        private static readonly bool EngineFov = VrConfig.Bool("afr_engine_fov", false);

        private static SceneView _engineView;
        private static Scene _boundScene;
        private static int _eye;

        /// <summary>
        /// Whether the engine's camera swaps eyes between frames.
        ///
        /// WHY PURE AFW STOPS DOING IT
        ///
        /// AFR has to alternate: it has no other way to obtain the second eye.
        /// AFW does - it builds the second eye from the first - so alternating
        /// buys it only one thing, the previous real render used to fill
        /// disocclusions, and charges a large price for it.
        ///
        /// The price is that the engine's camera TELEPORTS 65 mm sideways every
        /// single frame, and Bannerlord's antialiasing is temporal
        /// (antialiasing_technique = 5 is DLSS/DLAA, which accumulates a history
        /// whether or not it is upscaling). The history is reprojected using
        /// motion vectors that describe object and camera motion within ONE eye;
        /// nothing in them says the previous frame was taken from a viewpoint
        /// 65 mm to the side. So the accumulator blends two viewpoints, and it
        /// does it worst exactly where a mismatch is largest - near, thin,
        /// high-contrast geometry against a distant background. Reins and a
        /// weapon held in front of the face are the textbook case, and that is
        /// where the doubling was reported.
        ///
        /// It also explains why the two eyes did not look alike. The alternation
        /// has a period of two frames, so each eye's history was always built on
        /// the OTHER eye's frame - a systematic difference between left and
        /// right rather than a wash, which is why one eye showed picture
        /// doubling and the other only a halo.
        ///
        /// THE REFERENCE IMPLEMENTATION AGREES ABOUT THE CAUSE
        ///
        /// REFramework carries a setting whose internal name is
        /// m_fix_upscalers_wobbling, and it works by feeding the upscaler the
        /// SAME eye's previous view matrix instead of the other eye's:
        ///
        ///     oldViewMatrix[eye] = is_fix_dlss() ? d.old_view_matrix[eye]
        ///                                        : d.old_view_matrix[other_eye];
        ///
        /// plus a CorrectMotionVectors pass with SwapCameraMotion. That is the
        /// same diagnosis - alternating eyes poison a temporal upscaler - solved
        /// by correcting what the upscaler is told.
        ///
        /// We cannot tell rgl anything of the kind; its motion vectors and its
        /// previous-view matrix are its own. What we can do is remove the cause,
        /// which is available to AFW and to nothing else.
        ///
        /// WHAT IT COSTS
        ///
        /// The synthesised eye never gets a real render of its own, so
        /// disocclusions fall back to stretched background instead of being
        /// filled from that eye's previous frame. Those slivers are narrow at a
        /// 65 mm baseline. The hybrid is excluded outright: its whole mechanism
        /// is substituting a real held render while the head is still, and with
        /// a pinned eye there would never be one to substitute.
        /// </summary>
        /// <summary>
        /// The second draw supplies the other eye out of the SAME engine frame,
        /// so the engine has no reason to alternate at all.
        ///
        /// This is what the first run through the headset was missing. The
        /// duplicate always holds the eye the engine did not draw, so with the
        /// engine still swapping sides every frame the raw eye swapped with it -
        /// reported exactly as "left eye is raw, then it switched to right eye
        /// raw". Nothing was wrong with the image; the two halves were simply
        /// trading places at frame rate.
        ///
        /// Pinning the engine to eye 0 settles it: the engine draws the left eye
        /// every frame and the duplicate is always the right. Both come from one
        /// engine frame, which is the whole point - no alternation, no held eye,
        /// no stale partner, and no need to freeze the simulation between two
        /// renders because there is only one.
        /// </summary>
        /// <summary>
        /// Whether draw-duplication stereo is BUILT AND ALLOWED. Separate from
        /// the mode for the same reason AFR and AFW are: this says the path
        /// exists, the mode says it is the one running.
        ///</summary>
        private static readonly bool DrawStereoAllowed = VrConfig.Bool("vp_second_draw", true);

        /// <summary>
        /// True only while draw-duplication stereo is the SELECTED MODE.
        ///
        /// It used to read the config key directly, which meant enabling the
        /// experiment changed what AFR did. AFR, AFW and the hybrid must behave
        /// identically whether or not this path exists, and a mode check is what
        /// guarantees that.
        /// </summary>
        private static bool SecondDrawStereo =>
            DrawStereoAllowed && VrRenderMode.DrawStereoActive;

        /// <summary>
        /// Whether native stereo is BUILT AND ALLOWED - the same separation the
        /// other paths keep: this says the code exists, the mode says it runs.
        /// </summary>
        private static readonly bool NativeStereoAllowed = VrConfig.Bool("stereo_dup", true);

        /// <summary>
        /// True only while native stereo is the SELECTED MODE. Like draw
        /// duplication it pins the engine to one eye and stops alternating,
        /// because the other eye is rendered inside the same frame rather than
        /// the next one.
        /// </summary>
        private static bool NativeStereoActive =>
            NativeStereoAllowed && VrRenderMode.NativeStereoActive;

        private static bool AlternatesEye
        {
            get
            {
                if (SecondDrawStereo || NativeStereoActive)
                    return false;

                if (VrRenderMode.Active != VrRenderMode.Mode.Afw)
                    return true;

                if (!PinAfwEye)
                    return true;

                // Pinning is only safe while the warp can actually deliver the
                // eye it takes responsibility for. A pinned eye is the ONLY
                // source of content for its partner, so if the warp declines
                // there is nothing to submit and the view goes flat - the same
                // image in both eyes.
                //
                // That is not hypothetical: after a mission teardown the depth
                // search has to work out afresh which of rgl's several
                // eye-sized depth targets is the one the scene was drawn with,
                // and it declines until one proves it contains geometry. Across
                // four mission restarts that took between 1.5 and 6 seconds,
                // and it read as a second or so of mono at the start of every
                // battle.
                //
                // Alternating in the meantime is ordinary AFR: a real render of
                // each eye every other frame. Real stereo at half the rate
                // beats no stereo at full rate, and the pin returns on its own
                // the moment the warp is live.
                return NativeMethods.bvr_afw_ready() == 0;
            }
        }

        /// <summary>
        /// OFF by default, because the trade turned out to be a bad one.
        ///
        /// Pinning does remove the 65 mm per-frame camera teleport that the
        /// game's temporal antialiasing was accumulating across, and that
        /// reasoning still holds. What it also removes is the previous REAL
        /// render of the synthesised eye - because that eye is never rendered
        /// any more - and that is what disocclusions were being filled from.
        ///
        /// The fallback it falls back TO was written for the first frame or two
        /// of a mission, and it was tuned for that: afw_gap_fill_px searches 96
        /// pixels, deliberately reduced from the old cap on the grounds that
        /// "this path only ever runs on the first frame or two". Pinning makes
        /// it the permanent path, and the holes it now has to fill are as wide
        /// as the full disparity - 146 px at half a metre, 243 px at a third of
        /// one, against a ceiling that is no longer 220 but 1457. Past 96 px the
        /// search finds nothing, gives up, and samples the source at the same
        /// column, which is the near object itself. So every object got a band
        /// of itself smeared down its right-hand side, in the synthesised eye
        /// only - which is exactly what came back.
        ///
        /// Reported as "transparent edges on the right of every object, only in
        /// the right eye". Right is where they belong: the right eye sees an
        /// object further LEFT than the left eye does, so warping left to right
        /// shifts it left and uncovers background on its right.
        ///
        /// The upscaler ghosting this was meant to fix is real and still there.
        /// The reference implementation solves it by correcting what the
        /// upscaler is TOLD - motion vectors and the previous view matrix - not
        /// by refusing to alternate. That is the honest fix and it needs motion
        /// vectors we do not have. Trading a subtle artifact on some objects for
        /// a blatant one on all of them is not a step towards it.
        /// </summary>
        private static readonly bool PinAfwEye = VrConfig.Bool("afw_pin_source_eye", false);

        private static bool _loggedPin;
        private static bool _logged;
        private static bool _retargeted;

        /// <summary>The single camera the engine is given. Ours, created once,
        /// never swapped.</summary>
        private static Camera _afrCam;
        private static bool _bound;

        /// <summary>The eye the engine is drawing this frame.</summary>
        public static int CurrentEye => _eye;

        /// <summary>
        /// Points the engine's own scene view at our eye texture. Done at the
        /// engine's scene-rendering entry point, the same place a SceneView may
        /// safely be enabled - doing view surgery mid-frame is what crashed the
        /// stereo path.
        /// </summary>
        public static void OnSceneRenderingStarted(MissionScreen screen)
        {
            if (!Enabled)
            {
                // Native stereo has the scene. Stand AFR fully down rather than
                // just declining to run: the NATIVE layer is still holding an eye
                // index from the last mission, and a stale one would have it
                // submitting held images for a mode that is no longer producing
                // them. Idempotent, so running it every scene start costs nothing.
                StandDownForOtherMode();
                return;
            }

            if (screen == null)
                return;

            // Symmetric with the above: a native run may have switched the
            // engine's own scene view off, and AFR renders THROUGH that view.
            // Without this the toggle back to the working mode goes black.
            VrStereoRenderer.RestoreEngineView(screen);
            _stoodDownLogged = false;

            try
            {
                Scene scene = Mission.Current?.Scene;
                if (scene == null || !VrStereoRenderer.TexturesReady)
                {
                    VrLog.Warn("AFR: no scene or no eye textures yet; engine view left alone.");
                    return;
                }

                _engineView = screen.SceneView;
                if (_engineView == null)
                {
                    VrLog.Warn("AFR: MissionScreen has no SceneView to retarget.");
                    return;
                }

                // Render into OUR texture, at the headset's resolution rather than
                // the game window's. Copying from the backbuffer instead would mean
                // scaling 16:9 into a nearly square eye, and CopyResource cannot
                // scale at all.
                _engineView.SetAutoDepthTargetCreation(true);
                _engineView.SetRenderTarget(VrStereoRenderer.ColorTarget(0));

                _boundScene = scene;
                _retargeted = true;
                _eye = 0;

                // A new view starts at the engine's default scaling, not at
                // what the player last chose. Re-applied here so a scene change
                // does not quietly restore full resolution.
                _appliedScale = -1.0f;
                ApplyResolutionScale();

                if (Mode == CameraMode.Engine)
                {
                    // Nothing to bind. That is the entire point - see Tick.
                    if (EngineFov && screen.CombatCamera != null)
                        VrCameraDriver.ApplyEyeProjectionTo(screen.CombatCamera, _eye);

                    // ...except for the one thing that was never said out loud.
                    //
                    // This mode moves CombatCamera and assumes the scene view is
                    // already drawing from it. Nothing ever established that. The
                    // view's render target was retargeted here and its CAMERA was
                    // left to whatever the engine had put there, which is a
                    // different object than the one being written.
                    //
                    // That is the whole symptom: the projection we set survives
                    // from frame to frame (the FOV hold counter reads exactly half
                    // its calls needing a correction, so the write lands and the
                    // engine undoes it once per frame), the pose is computed
                    // correctly, and the picture still only moves with the mouse.
                    // A camera nothing renders from can be perfectly correct.
                    //
                    // SetCamera on the view we already own says it directly. It is
                    // the ENGINE'S OWN camera object, so this is not the
                    // CustomCamera path and none of that path's costs apply - the
                    // engine keeps maintaining CombatCamera exactly as before.
                    BindEngineCamera(screen.CombatCamera, "bind");

                    _bound = true;
                    VrLog.Info("AFR: driving the ENGINE'S OWN camera in place; "
                               + "CustomCamera is never set.");
                }
                else if (Mode == CameraMode.Shared)
                {
                    // Bind our camera ONCE, here, at the same safe point the view is
                    // retargeted. Kept for comparison; see Tick for why it fails.
                    if (_afrCam == null)
                        _afrCam = Camera.CreateCamera();

                    VrCameraDriver.ApplyEyeProjectionTo(_afrCam, 0);
                    VrCameraDriver.AimAtEye(_afrCam, 0);

                    screen.CustomCamera = _afrCam;
                    screen.SceneLayer?.SetCamera(_afrCam);
                    _bound = true;

                    VrLog.Info("AFR: one shared camera bound to the engine; "
                               + "it will be moved in place, never re-bound.");
                }

                NativeMethods.bvr_set_afr_eye(0);
                VrLog.Info("AFR: engine view retargeted to the eye texture.");
            }
            catch (Exception ex)
            {
                VrLog.Error("AFR: could not retarget the engine view.", ex);
                _retargeted = false;
            }
        }

        /// <summary>
        /// Per frame: swap eyes, point the engine's camera at that eye, and tell
        /// the native layer which eye the frame it is about to draw belongs to.
        /// </summary>
        public static void Tick(MissionScreen screen)
        {
            if (!Enabled || !_retargeted || screen == null)
                return;

            if (!ReferenceEquals(Mission.Current?.Scene, _boundScene))
            {
                ReleaseEngineView();
                _retargeted = false;
                _boundScene = null;
                VrVpPatch.StandDown();
                NativeMethods.bvr_set_afr_eye(-1);
                NativeMethods.bvr_set_full_afr(0);
                NativeMethods.bvr_set_draw_stereo(0);
            NativeMethods.bvr_set_draw_stereo(0);
                NativeMethods.bvr_set_draw_stereo(0);
                VrLog.Info("AFR: scene changed; standing down until it restarts.");
                return;
            }

            try
            {
                if (AlternatesEye)
                {
                    // ONLY WHEN THE LAST EYE WAS ACTUALLY DRAWN.
                    //
                    // This toggle runs on the game tick; the capture that
                    // consumes it runs in the Present hook. They are different
                    // clocks, and a tick that produces no scene render - a
                    // hitch, a menu frame, a load - used to advance the toggle
                    // anyway. From then on every captured frame carried the
                    // WRONG eye label, and since the warp derives its direction
                    // straight from that label, the disparity was applied
                    // backwards: the synthesised eye landed two full disparities
                    // out, in both eyes at once, until the parity happened to
                    // slip back.
                    //
                    // The residual meter caught it directly. A bad window of 96
                    // near blocks came back with 81 classed "wrong way" and a
                    // mean residual of 60.6 px against a mean movement of 31.4 -
                    // a ratio of 1.93, where reversing the sign predicts exactly
                    // 2.0. A quiet window at the same distances had 47 of 72
                    // blocks correct. Same scene, same geometry, opposite sign.
                    //
                    // It also accounts for the shape of the complaint: both eyes
                    // degrade together because one sign serves both directions,
                    // it lasts for seconds at a time, it scales as 1/z so near
                    // objects flick hardest, and pinning the source eye removed
                    // it entirely because a pinned eye has no parity to lose.
                    // ASKED, NOT TOGGLED. The native capture path owns the
                    // alternation now and advances it on the same event that
                    // consumes it, so the label can no longer drift from the
                    // frame it belongs to. The consumed-handshake above was a
                    // narrowing of this race; this removes it.
                    NativeMethods.bvr_pin_afr_eye(-1);
                    _eye = NativeMethods.bvr_next_afr_eye();

                    _loggedPin = false;
                }
                else
                {
                    // Forced rather than left wherever the alternation stopped,
                    // so which eye is the real one does not depend on the parity
                    // of the frame the mode was switched on.
                    _eye = 0;
                    NativeMethods.bvr_pin_afr_eye(0);

                    if (!_loggedPin && NativeStereoActive)
                    {
                        _loggedPin = true;
                        VrLog.Info("Native stereo: the engine camera is PINNED to the "
                                   + "left eye; the right is rendered from the same "
                                   + "frame by running every view-dependent operation "
                                   + "a second time. Nothing alternates, nothing is "
                                   + "held, nothing is warped.");
                    }
                    else if (!_loggedPin && SecondDrawStereo)
                    {
                        _loggedPin = true;
                        VrLog.Info("Single-frame stereo: the engine camera is PINNED to "
                                   + "the left eye and the right comes from the "
                                   + "duplicated draws of the SAME frame. Nothing "
                                   + "alternates any more - no held eye, no stale "
                                   + "partner, and no reason to freeze the simulation "
                                   + "between two renders, because there is only one. "
                                   + "The right eye is still raw: no tonemap, no "
                                   + "upscale, no post. vp_second_draw = 0 restores the "
                                   + "ordinary alternation.");
                    }
                    else if (!_loggedPin)
                    {
                        _loggedPin = true;
                        VrLog.Info("AFW: the engine camera is PINNED to the left eye "
                                   + "instead of swapping sides every frame. AFW builds "
                                   + "the second eye rather than waiting for it, so the "
                                   + "swap bought only the previous real render used to "
                                   + "fill disocclusions - and charged a 65 mm camera "
                                   + "teleport every frame, which the game's temporal "
                                   + "antialiasing accumulates across as ghosting on "
                                   + "near, thin, high-contrast geometry. The left eye "
                                   + "is now always a real render and the right always "
                                   + "synthesised; disocclusions fall back to stretched "
                                   + "background. afw_pin_source_eye = 0 restores the "
                                   + "swap.");
                    }
                }

                Camera camera = VrCameraDriver.EyeCamera(_eye);
                if (camera == null)
                    return;

                switch (Mode)
                {
                    case CameraMode.Engine:
                    {
                        // THE FIX, and this repo wrote the reason down long before
                        // AFR existed. CameraPatch, on why the mirror path is off:
                        //
                        //   "the engine stops maintaining CombatCamera once
                        //    CustomCamera is set, so its HorizontalFov sits at 0.000
                        //    forever and ANY CAMERA WE HAND IT RENDERS THROUGH A
                        //    BROKEN FRUSTUM - NO TERRAIN, NO AGENTS"
                        //
                        // No terrain, no agents. That is the symptom, described in
                        // this file's sibling as a known consequence of setting
                        // CustomCamera - and AFR sets CustomCamera. Binding it more
                        // stably only made it cleaner: one bound camera left the
                        // visible set frozen at bind time, and the world collapsed
                        // to nothing but sky.
                        //
                        // afr_camera = 0 proved the render target is fine, so the
                        // only thing left to change is where the ENGINE'S OWN camera
                        // is. So move that, in place, and never hand the engine a
                        // camera at all. It keeps every piece of internal state it
                        // maintains - visible sets, occlusion, streaming - because as
                        // far as it knows nothing was replaced.
                        //
                        // This runs in MissionScreen.OnFrameTick's POSTFIX, so the
                        // engine has already applied its own camera update for this
                        // frame and ours is the last write before the scene renders.
                        Camera combat = screen.CombatCamera;
                        if (combat == null)
                            return;

                        // PROJECTION FIRST, POSE LAST. This order is the whole of
                        // TEST 95 and it is not cosmetic.
                        //
                        // The read-back inside AimAtEye reports 0.0 mm - LookAt
                        // puts the camera exactly on the eye, every write, 450 a
                        // window. Native then measures the camera ~280 mm from
                        // the eye by the time VrVpPatch.Publish reads it back,
                        // with the head deliberately still. Between those two
                        // points, in one synchronous method, there is only
                        // HoldEngineFov - and it calls SetFovVertical /
                        // SetViewVolume, which evidently rebuild the camera's
                        // frame rather than only its projection.
                        //
                        // So the projection is written FIRST and the pose LAST,
                        // which makes the pose the final word before the frame is
                        // published. Same two calls, same frame, opposite order.
                        HoldEngineFov(combat, _eye);
                        VrCameraDriver.AimAtEye(combat, _eye);
                        break;
                    }

                    case CameraMode.Shared:
                    {
                        // One camera of ours, bound once. Kept for comparison.
                        if (!_bound || _afrCam == null)
                            return;

                        VrCameraDriver.AimAtEye(_afrCam, _eye);

                        if (!ReferenceEquals(screen.CustomCamera, _afrCam))
                        {
                            screen.CustomCamera = _afrCam;
                            screen.SceneLayer?.SetCamera(_afrCam);
                            VrLog.Info("AFR: shared camera was displaced; re-bound.");
                        }
                        break;
                    }

                    case CameraMode.Eye:
                    {
                        // The original: a different camera object handed to the
                        // engine every frame, at 90 Hz. Kept only to reproduce.
                        screen.CustomCamera = camera;
                        screen.SceneLayer?.SetCamera(camera);
                        break;
                    }

                    case CameraMode.Off:
                    default:
                        break;
                }

                // The head pose reaches the render from here, not from the camera:
                // published now, consumed on the render thread when rgl uploads
                // its view constants for the frame we are about to draw. Must come
                // before the engine renders, which is why it lives in the
                // OnFrameTick postfix alongside everything else that has to.
                VrVpPatch.Publish(screen, _eye);

                // Native must know before the frame is drawn, because the Present
                // hook reads it afterwards to decide which eye it just captured.
                NativeMethods.bvr_set_afr_eye(_eye);

                // ...and whether this frame is one HALF of a pair rather than a
                // whole one. In full AFR the Present that follows captures its
                // eye and hands the begun XR frame back instead of ending it, so
                // the next render draws the other eye from the same locate and
                // the same world. Published every frame rather than latched at
                // the mode switch: the mode can change between a deferred frame
                // and the partner that was meant to complete it, and the native
                // side has to be told before that partner is drawn, not after.
                NativeMethods.bvr_set_full_afr(
                    VrRenderMode.RendersPairPerDisplayFrame(VrRenderMode.Active) ? 1 : 0);

                // ...and whether the native duplication path should run at all.
                // OFF for every mode but its own, which is what keeps AFR and AFW
                // behaving exactly as they did before this path was written.
                NativeMethods.bvr_set_draw_stereo(SecondDrawStereo ? 1 : 0);

                // ...and whether the whole pipeline should run twice. Off for
                // every other mode, so selecting it is the only thing that can
                // change what the renderer does.
                NativeMethods.bvr_set_native_stereo(NativeStereoActive ? 1 : 0);

                // ...and which world state that frame will be drawn from, so the
                // capture can hold this eye back until its partner comes from the
                // same one. Publishes -1 when pairing is off, which is what makes
                // the native side put every eye up the moment it arrives.
                VrSyncSequential.PublishGeneration();

                // AFW's geometry, and whether it should run at all. Also before
                // the render, because the warp reads it during the Present that
                // follows.
                VrAfw.Publish();

                if (!_logged)
                {
                    _logged = true;
                    VrLog.Info("AFR camera mode: " + Mode + ".");
                }
            }
            catch (Exception ex)
            {
                VrLog.Error("AFR tick failed; leaving AFR mode.", ex);
                _retargeted = false;
                VrVpPatch.StandDown();
                NativeMethods.bvr_set_afr_eye(-1);
                NativeMethods.bvr_set_full_afr(0);
                NativeMethods.bvr_set_draw_stereo(0);
            NativeMethods.bvr_set_draw_stereo(0);
            }
        }

        /// <summary>
        /// Puts this frame's eye pose on the engine's camera from the TAIL OF THE
        /// ENGINE'S OWN CAMERA UPDATE, which is the last moment it can be written.
        ///
        /// Tick() already does this from MissionScreen.OnFrameTick's postfix, and
        /// that was believed to be the final write of the frame. It is not.
        /// CameraPatch's own header wrote down what this looks like when it is
        /// wrong, before AFR existed:
        ///
        ///   "rate fine, pose values moving -> our camera is correct and the engine
        ///    is rendering from another"
        ///   "The gain meter reads a perfect x1.00 throughout, because our camera
        ///    was right the whole time and simply was not the one being rendered."
        ///
        /// A perfect x1.00 gain with no head tracking in the headset is that exact
        /// signature: the pose was computed correctly every frame and then thrown
        /// away. The engine reaches UpdateCamera through CheckForUpdateCamera at a
        /// point in the frame AFTER OnFrameTick's postfix has run, recomputes
        /// CombatCamera from bearing and elevation, and overwrites us.
        ///
        /// So write it here as well, in that method's postfix, which is the same
        /// seam a camera-hooking VR mod normally uses - substitute what the engine
        /// just computed rather than trying to out-order it.
        ///
        /// This does not REPLACE the write in Tick, it backs it up, and the pair
        /// covers both cases without either needing to know which one ran:
        ///
        ///   UpdateCamera ran     -> the engine overwrote us, and this rewrites.
        ///   UpdateCamera skipped -> nothing overwrote us, and Tick's write stands.
        ///
        /// The counter is the point of the exercise. If it reports roughly one call
        /// per frame, this seam is real. If it reports a handful per second, then
        /// UpdateCamera is not where the camera is being finalised and neither
        /// write is the answer - which is worth knowing in one run rather than
        /// three.
        /// </summary>
        public static void AimEngineCamera(MissionScreen screen)
        {
            if (!Enabled || !_retargeted || Mode != CameraMode.Engine || screen == null)
                return;

            Camera combat = screen.CombatCamera;
            if (combat == null)
                return;

            // Projection first, pose last - see the note at the other call site.
            HoldEngineFov(combat, _eye);
            VrCameraDriver.AimAtEye(combat, _eye);

            // Re-asserted every frame, for the same reason the pose is: the engine
            // rebuilds its view state around this point in the frame, and a binding
            // made once at scene start has already been shown not to be the last
            // word on anything. SetCamera hands over a camera reference rather than
            // reconfiguring a frustum, so it is not the expensive kind of call that
            // this file twice records as fatal at frame rate.
            BindEngineCamera(combat, "re-bind");

            // ...and the third copy of the game's camera UpdateCamera handed out,
            // the native mission's. Without it the mission kept the body heading
            // until the engine aim calibrated, and nothing behind the player was
            // drawn before the first turn. See VrMissionCamera.
            VrMissionCamera.FollowHead(screen, combat);

            _lateAims++;

            // 150 rather than 450, so this fits inside a short test run - see the
            // note on the survival probe.
            if (++_lateAimTick >= 150)
            {
                _lateAimTick = 0;
                VrLog.Info($"Engine camera re-aimed from UpdateCamera's tail: {_lateAims} "
                           + "time(s), and the scene view is bound to it.");
                _lateAims = 0;
            }
        }

        private static int _lateAims;
        private static int _lateAimTick;

        /// <summary>
        /// Points the retargeted scene view at a camera, and says so the first time.
        ///
        /// Failure here is reported once and then never again: if SetCamera is not
        /// callable in this state, a line per frame would bury the log and change
        /// nothing.
        /// </summary>
        private static bool _loggedBind;
        private static bool _bindFailed;

        private static void BindEngineCamera(Camera camera, string what)
        {
            if (_bindFailed || _engineView == null || camera == null)
                return;

            try
            {
                _engineView.SetCamera(camera);

                if (!_loggedBind)
                {
                    _loggedBind = true;
                    VrLog.Info("AFR: scene view " + what + " to the engine's own CombatCamera. "
                               + "Until now the view's render target was retargeted and its "
                               + "CAMERA was left to whatever the engine had put there - so a "
                               + "correct camera nobody was rendering from is exactly what the "
                               + "headset was showing.");
                }
            }
            catch (Exception ex)
            {
                _bindFailed = true;
                VrLog.Error("AFR: SceneView.SetCamera failed; the view keeps whatever camera "
                            + "the engine gave it.", ex);
            }
        }

        // --- keeping the headset frustum on the engine's own camera ---------------
        //
        // The engine reconfigures CombatCamera for its viewport, so a frustum we set
        // once may not survive. But rewriting it every frame is the call this repo
        // has twice recorded as fatal, so we do neither blindly: compare first, and
        // write only when it has actually drifted.
        //
        // The counter matters as much as the correction. If corrections stay at 0
        // after the first, setting it once was enough. If it climbs by ~90 a second,
        // the engine is overwriting us every frame and the projection cannot live on
        // the engine's camera at all - in which case it has to move to the native
        // side, patched into the view-projection constant buffer, which is how VR
        // injectors normally do this.
        private static int _fovCorrections;
        private static int _fovStuck;
        private static int _fovRefused;
        private static int _fovLogTick;

        // THE EYE ARGUMENT, AND WHY IT WAS A BUG WITHOUT IT.
        //
        // This used to hold eye 0's frustum on the engine camera and nothing else,
        // while the native side submitted each eye with its own. A headset's
        // frusta are MIRRORED, not equal - eye 0 is L-54.0/R40.0 degrees and eye 1
        // is L-40.0/R54.0 - so their centre axes sit about seven degrees apart.
        // Rendering both eyes through eye 0's projection and then telling the
        // compositor the right one was eye 1's means the right image is placed
        // seven degrees from where it was drawn. That is the eyes not lining up,
        // and it is a big enough offset to see immediately.
        //
        // The one rule that cannot be broken is that the fov SUBMITTED must equal
        // the fov RENDERED. Passing the eye is what makes the asymmetric path obey
        // it; the symmetric path below obeys it a different way, by making both
        // eyes identical so there is nothing left to disagree about.
        private static void HoldEngineFov(Camera combat, int eye)
        {
            if (!EngineFov)
                return;

            VrCameraDriver.EyeProjectionTarget(eye, out float wantFov, out float wantAspect);

            float haveFov = combat.HorizontalFov;
            float haveAspect = combat.GetAspectRatio();

            if (Math.Abs(haveFov - wantFov) > 0.005f || Math.Abs(haveAspect - wantAspect) > 0.005f)
            {
                VrCameraDriver.ApplyEyeProjectionTo(combat, eye);
                _fovCorrections++;

                // DID THE WRITE TAKE?
                //
                // 450 corrections in 5 s means we set the projection on almost
                // every call and it is back to the engine's 129.5 deg / 16:9 by
                // the next one. That has two completely different causes - the
                // write failing outright, or the write succeeding and the engine
                // overwriting it later in the frame - and they need opposite
                // fixes. Reading it straight back separates them, and costs a
                // property read on a path that has already decided to write.
                float afterFov = combat.HorizontalFov;
                float afterAspect = combat.GetAspectRatio();
                bool took = Math.Abs(afterFov - wantFov) <= 0.005f &&
                            Math.Abs(afterAspect - wantAspect) <= 0.005f;
                if (took) _fovStuck++; else _fovRefused++;
            }

            // ~90 fps, so roughly one line every five seconds.
            if (++_fovLogTick >= 450)
            {
                _fovLogTick = 0;

                // THE 1:1 CHECK, STATED AS A NUMBER.
                //
                // A projection rendered at one fov and submitted as another is
                // magnified by the ratio of their tangent half-widths, and that
                // magnification applies to apparent ROTATION as well as to size:
                // turn the head 40 degrees through a 1.12x projection and the
                // world sweeps 45. Reported exactly that way.
                //
                // gain 1.000 is the only correct value. Anything else is the
                // engine rendering a frustum we are not submitting, which is the
                // one rule HoldEngineFov exists to enforce.
                double haveHalf = haveFov * 0.5;
                double wantHalf = wantFov * 0.5;
                double gain = Math.Tan(haveHalf) > 1e-6
                                  ? Math.Tan(wantHalf) / Math.Tan(haveHalf)
                                  : 0.0;

                VrLog.Info($"Engine FOV hold: {_fovCorrections} correction(s) in the last "
                           + $"~5 s (have {haveFov:F3}/{haveAspect:F3}, want {wantFov:F3}/{wantAspect:F3}). "
                           + $"Head-to-world gain {gain:F3} - 1.000 is 1:1; "
                           + $"{gain:F3} means 40 deg of head reads as {40.0 * gain:F1} deg of world."
                           + $" Of those writes {_fovStuck} read back correct and {_fovRefused} "
                           + "did not - refused means the camera will not take our projection, "
                           + "stuck means it takes it and the engine overwrites it later.");
                _fovCorrections = 0;
                _fovStuck = 0;
                _fovRefused = 0;
            }
        }

        public static void Reset()
        {
            if (!Enabled)
                return;

            // Before the reference is dropped, or there is nothing left to hand
            // the view back with. See ReleaseEngineView.
            ReleaseEngineView();

            _retargeted = false;
            _boundScene = null;
            _engineView = null;
            _logged = false;

            // The camera itself deliberately SURVIVES, for the same reason the eye
            // cameras do: the engine may still hold it. Only the binding is dropped,
            // and OnSceneRenderingStarted re-binds it for the next scene.
            _bound = false;

            // Before leaving AFR mode, so no frame can be drawn against a head
            // pose belonging to a mission that has already ended.
            VrVpPatch.StandDown();

            try
            {
                NativeMethods.bvr_set_afr_eye(-1);
                NativeMethods.bvr_set_full_afr(0);
                NativeMethods.bvr_set_draw_stereo(0);
            NativeMethods.bvr_set_draw_stereo(0);
            }
            catch (Exception ex)
            {
                VrLog.Error("AFR: could not leave AFR mode.", ex);
            }
        }
    }
}
