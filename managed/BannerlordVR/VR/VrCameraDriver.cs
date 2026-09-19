using System;
using BannerlordVR.Interop;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Phase 3. Turns the OpenXR head pose into two Bannerlord eye cameras.
    ///
    /// The chain, innermost first:
    ///
    ///     eyeWorld = body * recentre * eyeStage
    ///
    ///   eyeStage   per-eye pose from xrLocateViews, converted to Bannerlord's
    ///              basis by VrMath. Encodes the runtime's real IPD, eye
    ///              canting and eye relief - we never synthesize those.
    ///   recentre   cancels the player's yaw and position at the moment they
    ///              pressed recentre. Pitch and roll are deliberately NOT
    ///              cancelled: they are absolute with respect to gravity and
    ///              the runtime already reports them correctly. Cancelling them
    ///              bakes whatever head tilt existed at recentre time into every
    ///              subsequent frame.
    ///   body       the engine camera's position with its rotation flattened to
    ///              heading. The player's neck owns pitch and roll; the game
    ///              owns yaw. Feeding engine pitch to the HMD is the single
    ///              fastest route to motion sickness.
    ///
    /// Only the LEFT camera is handed to MissionScreen.CustomCamera. Culling,
    /// LOD, the audio listener and every ScreenSpaceRayProjection caller then
    /// share one coherent answer instead of disagreeing per eye. The right
    /// camera is built here but stays unused until Phase 4 gives it a SceneView.
    /// </summary>
    public static class VrCameraDriver
    {
        /// <summary>
        /// Near plane the OpenXR FOV tangents are evaluated at. Deliberately
        /// closer than the engine's default: in VR your own hands and any wall
        /// you lean into are centimetres from your face, and the engine's flat
        /// near plane clips them away.
        /// </summary>
        private static readonly float NearPlane = VrConfig.Float("eye_near", 0.05f);

        /// <summary>
        /// Give the eye cameras the ENGINE's own projection - its horizontal FOV,
        /// its aspect - instead of the headset's, while still moving them with the
        /// head.
        ///
        /// afr_camera = 0 proved the camera is what loses the geometry, because
        /// the identical render target with the engine's own camera draws the world
        /// correctly. "The camera" is still two things though: where we put it, and
        /// what frustum we give it. This separates them.
        ///
        ///   geometry draws => the PROJECTION was the fault - the 108 degree FOV,
        ///     or the near plane, and eye_near narrows it further from there.
        ///   still missing   => the POSE was the fault - where we put the camera,
        ///     not what we asked it to see.
        ///
        /// The image will be stretched, because the engine's 1.778 aspect is being
        /// rendered into a 0.964 target. Ignore that; judge only what DRAWS.
        /// </summary>
        private static readonly bool EngineProjection =
            string.Equals(VrConfig.String("eye_projection", "runtime"), "engine",
                          StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Multiplies every stage-space translation: eye separation and the head's
        /// own movement, together and by the same factor.
        ///
        /// The plan assumed one Bannerlord unit is one metre; the qualifier in it
        /// was "approximately", and perceived scale is the test that settles it.
        /// Scaling both terms together is what keeps this a SCALE change rather
        /// than a distortion - alter IPD alone and the world changes size while
        /// leaning stops matching, which is far worse than either error by itself.
        ///
        /// Larger values make the world look SMALLER (your head is bigger relative
        /// to it). Tune it by eye against something of known size - a horse, a
        /// doorway - and expect to want something within 10-20% of 1.0.
        /// </summary>
        /// <summary>
        /// Settable, because it is the one number that can only be judged by
        /// wearing the headset.
        ///
        /// Nothing is reconfigured when it changes - it multiplies two stage-space
        /// translations on the way through, per frame - so it is safe to move while
        /// the game is running, and moving it while looking at a horse is worth more
        /// than any number of restarts. <see cref="VrMenu"/> drives it.
        /// </summary>
        public static float WorldScale { get; private set; } =
            ClampScale(VrConfig.Float("world_scale", 1.0f));

        /// <summary>Applies a new world scale, clamped. Returns what was actually
        /// set, which is what a caller should display.</summary>
        public static float SetWorldScale(float value)
        {
            WorldScale = ClampScale(value);
            return WorldScale;
        }

        /// <summary>
        /// symmetric_fov: below zero follows the render mode (the default),
        /// zero forces the runtime's true asymmetric frustum, above zero forces
        /// the containing symmetric one.
        ///
        /// Numeric rather than a word because the native half reads the same key
        /// through atof, which turns anything unparseable into 0 - and 0 here is
        /// not "no opinion", it is "force asymmetric". A sentinel that survives
        /// both parsers is worth more than a readable one.
        /// </summary>
        private static readonly float FovChoice = VrConfig.Float("symmetric_fov", -1f);

        /// <summary>
        /// Whether to render and submit a containing SYMMETRIC frustum instead
        /// of the runtime's true asymmetric one.
        ///
        /// WHY THIS FOLLOWS THE MODE RATHER THAN BEING SET ONCE
        ///
        /// The two answers are right for different modes, and neither is right
        /// for both.
        ///
        /// Asymmetric is the honest frustum and it is a large win when every eye
        /// is really rendered: the containing symmetric frustum measured on this
        /// headset is 105.2 x 104.2 degrees against a true 91.2 x 93.1, so a
        /// third of every rendered pixel is drawn and then cropped away by the
        /// compositor. Reclaiming it is worth about 1.22x of linear sharpness on
        /// what actually reaches the eye.
        ///
        /// AFW cannot have it. The warp shows the destination eye what the
        /// SOURCE eye saw, and on a canted headset the runtime hands out
        /// mirrored asymmetric fovs, so each eye holds around 709 pixels of its
        /// own outer field - a quarter of the width - that the other eye never
        /// saw. Under a symmetric containing frustum both eyes render the same
        /// field and the only difference between them is the small horizontal
        /// disparity the warp exists to apply. That is exactly the rectified
        /// stereo case the warp is built on, and it is why afw_warp.h says
        /// symmetric_fov earns its keep.
        ///
        /// This was a real regression: asymmetric was turned on globally for the
        /// sharpness, which silently broke AFW - the two eyes stopped sharing a
        /// frustum and the warp had no way to say so.
        /// </summary>
        private static bool SymmetricFov
        {
            get
            {
                if (FovChoice >= 0f)
                    return FovChoice > 0f;

                return VrRenderMode.WarpsSecondEye(VrRenderMode.Active);
            }
        }

        private static float ClampScale(float value)
        {
            if (value < 0.1f || value > 10f || float.IsNaN(value))
            {
                VrLog.Warn($"world_scale {value} out of range (0.1-10); using 1.0.");
                return 1.0f;
            }

            if (Math.Abs(value - 1.0f) > 1e-4f)
                VrLog.Info($"World scale {value:F3}.");

            return value;
        }

        private static BvrVec3 Scale(BvrVec3 v)
        {
            return WorldScale == 1.0f
                ? v
                : new BvrVec3 { X = v.X * WorldScale, Y = v.Y * WorldScale, Z = v.Z * WorldScale };
        }

        private static readonly BvrEyeView[] Views = new BvrEyeView[2];
        private static readonly Camera[] EyeCameras = new Camera[2];

        private static MatrixFrame _recentre = MatrixFrame.Identity;

        // Recentre on the first valid pose, so the player is never dropped into
        // a mission facing an arbitrary direction with no idea which key fixes it.
        private static bool _recentreRequested = true;

        private static MissionScreen _boundScreen;
        private static bool _loggedFirstPose;
        private static bool _loggedRawPose;
        private static bool _unusablePoseLogged;

        /// <summary>True when the cameras below hold a real, current head pose.</summary>
        public static bool HasPose { get; private set; }

        /// <summary>Head (eye midpoint) in world space. Phase 5 aims the UI quad
        /// with this; the audio listener will want it too.</summary>
        public static MatrixFrame HeadFrame { get; private set; }

        /// <summary>
        /// The frame every stage-space pose is composed onto to reach the world -
        /// the body heading and position the headset is anchored to.
        ///
        /// Published because the controllers need exactly the same transform the
        /// eyes get. A hand located in stage space and a head located in stage
        /// space have to arrive in the world through the SAME anchor, or the
        /// weapon will sit at an angle to the view that changes as you turn.
        /// </summary>
        public static MatrixFrame Anchor { get; private set; }

        /// <summary>
        /// The head's pose in stage space, latched with the stereo pair.
        ///
        /// The hands are measured FROM this rather than from the stage origin -
        /// see <see cref="StageToHand"/> - so it has to be the same head pose the
        /// eyes were rendered from.
        /// </summary>
        public static MatrixFrame HeadStage { get; private set; }

        /// <summary>predictedDisplayTime of the pose the cameras were built from.</summary>
        public static long DisplayTime { get; private set; }

        public static Camera LeftEye => EyeCameras[0];
        public static Camera RightEye => EyeCameras[1];

        /// <summary>Eye camera by index, for the stereo SceneViews. These belong
        /// to us alone and are never handed to the engine.</summary>
        public static Camera EyeCamera(int eye) => EyeCameras[eye];

        /// <summary>The camera given to the engine for the desktop mirror. Tracks
        /// the left eye, and is the only one the engine may reconfigure.</summary>
        public static Camera MirrorCam { get; private set; }

        // --- eye poses, for callers that drive their own camera ------------------
        //
        // AFR needs to AIM a camera at each eye without owning the eye cameras and
        // without being handed a different camera object every frame. It gets the
        // frames; it applies them itself.
        private static readonly MatrixFrame[] EyeWorld = new MatrixFrame[2];
        private static float _lastFar = 1000f;

        /// <summary>The near and far planes the eye cameras are built with. AFW
        /// needs both to turn a depth-buffer value back into a distance.</summary>
        public static float NearPlaneValue => NearPlane;
        public static float FarPlaneValue => _lastFar;

        /// <summary>
        /// Prints a projection line only when it CHANGES.
        ///
        /// ApplyEyeProjectionTo used to log unconditionally, which was harmless
        /// while it ran once at bind time. Under afr_engine_fov it runs whenever
        /// the engine has overwritten the frustum - which turned out to be every
        /// single frame - and produced a 10 MB log of one repeated line that
        /// buried everything else in it.
        /// </summary>
        private static string _lastProjectionLog;

        private static void LogProjectionOnce(string message)
        {
            if (message == _lastProjectionLog)
                return;

            _lastProjectionLog = message;
            VrLog.Info(message);
        }

        /// <summary>True once <see cref="EyeFrame"/> holds a real pose.</summary>
        public static bool HaveEyeFrames { get; private set; }

        /// <summary>Head yaw and pitch in BODY space - after recentring, before
        /// the anchor - in radians. Yaw is measured from +Y toward +X, matching
        /// the world convention <see cref="VrEngineAim"/> reads the engine camera
        /// in, so the two can be compared without a conversion nobody verified.
        /// </summary>
        public static float HeadYaw { get; private set; }
        public static float HeadPitch { get; private set; }
        public static bool HaveHeadAngles { get; private set; }

        private static void PublishHeadAngles(MatrixFrame headStage)
        {
            MatrixFrame headInBody = _recentre.TransformToParent(headStage);
            Vec3 f = headInBody.rotation.f;

            float z = f.z;
            if (z > 1f) z = 1f;
            else if (z < -1f) z = -1f;

            HeadYaw = (float)Math.Atan2(f.x, f.y);
            HeadPitch = (float)Math.Asin(z);
            HaveHeadAngles = true;
        }

        /// <summary>World-space frame of an eye: origin is where it is, rotation.f
        /// is where it looks, rotation.u is its up.</summary>
        public static MatrixFrame EyeFrame(int eye) => EyeWorld[eye];

        /// <summary>
        /// A stage-space pose - a controller, typically - brought into the world
        /// through exactly the transform the eyes use.
        ///
        /// The world scale is applied to the position here, which is not a
        /// detail: it is the same scaling the eye separation gets, so a hand
        /// stays where it looks like it is when the slider moves. Skipping it
        /// would leave the weapon drifting away from the hand you can see as
        /// soon as world scale is anything but 1.
        /// </summary>
        public static MatrixFrame StageToWorld(BvrQuat orientation, BvrVec3 position)
        {
            MatrixFrame stage = VrMath.ToBannerlord(orientation, Scale(position));
            return Anchor.TransformToParent(stage);
        }

        /// <summary>
        /// How far the hands move for a given movement of the real ones. 1.0 is
        /// one-to-one and is the default.
        ///
        /// SEPARATE FROM WORLD SCALE, AND WHY
        ///
        /// World scale multiplies every stage translation, which is what makes the
        /// world feel bigger or smaller - and it applied to the hands too, so at
        /// world_scale 1.10 a hand moved ten centimetres moved eleven. That reads
        /// exactly as reported: the virtual hand runs ahead of the real one.
        ///
        /// The two are not the same question. World scale is about how big the
        /// world is around you; this is about whether your hand is where your hand
        /// is. Proprioception has an opinion about the second and none about the
        /// first, and it is not a forgiving one - a hand that consistently
        /// overshoots is more disorienting than a world that is slightly the wrong
        /// size. So the hands get their own number, and it defaults to honest.
        /// </summary>
        public static float HandScale { get; private set; } =
            ClampScale(VrConfig.Float("hand_scale", 1.0f));

        /// <summary>
        /// A controller's stage pose brought into the world, placed relative to
        /// the HEAD rather than to the stage origin.
        ///
        /// Written as head + (hand - head) rather than folded into the anchor
        /// because that is the sentence being asserted: a hand is somewhere
        /// relative to your head, and that offset is what should survive into the
        /// world at hand scale. Scaling the absolute stage position instead makes
        /// the offset depend on how far you have walked around the room since the
        /// last recentre, which is not a thing your arm knows about.
        ///
        /// When hand scale and world scale agree this is exactly StageToWorld -
        /// the ratio is 1 and the head terms cancel - so the default costs
        /// nothing and changes nothing.
        /// </summary>
        public static MatrixFrame StageToHand(BvrQuat orientation, BvrVec3 position)
        {
            MatrixFrame stage = VrMath.ToBannerlord(orientation, Scale(position));

            // Both positions are already multiplied by world scale, so dividing it
            // back out of the difference leaves the real offset in metres, which
            // is then taken at hand scale.
            float ratio = WorldScale <= 1e-4f ? 1f : HandScale / WorldScale;

            if (Math.Abs(ratio - 1f) > 1e-4f)
            {
                Vec3 head = HeadStage.origin;
                stage.origin = head + (stage.origin - head) * ratio;
            }

            return Anchor.TransformToParent(stage);
        }

        /// <summary>
        /// Points <paramref name="cam"/> at one eye. Pose only - no projection, no
        /// reconfiguration. This is the cheap per-frame call.
        /// </summary>
        public static void AimAtEye(Camera cam, int eye)
        {
            if (cam == null || !HaveEyeFrames || eye < 0 || eye > 1)
                return;

            MatrixFrame f = EyeWorld[eye];

            // What actually reaches the engine's camera, recorded by the code
            // that puts it there. The echo guard used to record eye 0 in the
            // render loop while THIS wrote whichever eye AFR was on, so on half
            // the frames it compared two points an IPD apart and concluded the
            // camera had genuinely moved.
            _lastEyeOrigin = f.origin;
            _haveSubmitted = true;

            // Same LookAt reasoning as the eye cameras: a Bannerlord camera's basis
            // is not the object convention, so hand it position/target/up and let it
            // build whatever internal basis it wants.
            Vec3 target = new Vec3(
                f.origin.x + f.rotation.f.x,
                f.origin.y + f.rotation.f.y,
                f.origin.z + f.rotation.f.z, -1f);

            cam.LookAt(f.origin, target, f.rotation.u);

            // DID THE POSE TAKE? The projection has had a read-back since
            // HoldEngineFov was written, and it earned its keep immediately -
            // "225 read back correct" separated a refused write from an
            // overwritten one in a single run. The POSE never had one, and the
            // question has now cost several tests.
            //
            // Native measures D = eyeCamera - refPos, where refPos is
            // combat.Frame.origin read a few lines after this call. It reports
            // ~280 mm on the MAIN VIEW with the head deliberately held still,
            // rising to tens of metres. Either LookAt does not put the camera
            // where it is told, or something moves it between here and
            // VrVpPatch.Publish. Those need opposite fixes and only a read-back
            // taken RIGHT HERE can tell them apart: a gap visible on this line
            // is LookAt; a gap that appears only in D is something in between.
            try
            {
                Vec3 got = cam.Frame.origin;
                float ex = got.x - f.origin.x;
                float ey = got.y - f.origin.y;
                float ez = got.z - f.origin.z;
                float err = (float)Math.Sqrt(ex * ex + ey * ey + ez * ez);

                _aimErrSum += err;
                _aimErrCount++;
                if (err > _aimErrMax) _aimErrMax = err;

                if (++_aimLogTick >= 450)
                {
                    _aimLogTick = 0;
                    float mean = _aimErrCount > 0 ? _aimErrSum / _aimErrCount : 0f;
                    VrLog.Info(string.Format(
                        "Camera pose read-back: LookAt asked for the eye and the camera "
                        + "came back {0:F1} mm away on average, {1:F1} mm worst, over {2} "
                        + "write(s) in ~5 s. Near zero means LookAt lands it and any D "
                        + "native reports is something moving the camera AFTER this "
                        + "call. Hundreds of mm means LookAt itself is not putting the "
                        + "camera where it is told, and D is that, not head motion.",
                        mean * 1000f, _aimErrMax * 1000f, _aimErrCount));
                    _aimErrSum = 0f; _aimErrCount = 0; _aimErrMax = 0f;
                }
            }
            catch
            {
                // A camera that will not report its frame is not worth crashing
                // the render loop over; the instrument simply goes quiet.
            }
        }

        private static float _aimErrSum;
        private static float _aimErrMax;
        private static int _aimErrCount;
        private static int _aimLogTick;

        /// <summary>
        /// Applies an eye's frustum to <paramref name="cam"/>. EXPENSIVE - rgl does
        /// real work in here and does not tolerate it at frame rate. Call it once,
        /// when binding a camera, never per frame.
        ///
        /// Note that under symmetric_fov both eyes get the IDENTICAL frustum - only
        /// their positions differ, by the eye separation. That is what lets AFR
        /// drive both eyes from a single camera object with nothing but LookAt.
        /// </summary>
        /// <summary>
        /// The horizontal FOV and aspect <see cref="ApplyEyeProjectionTo"/> would
        /// set. Lets a caller compare before writing, so a camera the engine keeps
        /// reconfiguring is corrected only when it has actually drifted rather than
        /// blindly rewritten at frame rate.
        /// </summary>
        public static void EyeProjectionTarget(int eye, out float hfov, out float aspect)
        {
            BvrFov fov = Views[eye].Fov;

            if (SymmetricFov)
            {
                float hHalf = Math.Max(Math.Abs(fov.AngleLeft), Math.Abs(fov.AngleRight));
                float vHalf = Math.Max(Math.Abs(fov.AngleUp), Math.Abs(fov.AngleDown));

                hfov = hHalf * 2f;
                aspect = (float)(Math.Tan(hHalf) / Math.Tan(vHalf));
                return;
            }

            // THE ASYMMETRIC TARGET, WHICH THIS USED TO GET WRONG.
            //
            // It computed the containing SYMMETRIC frustum unconditionally, even
            // with symmetric_fov = 0 - so with the asymmetric path selected,
            // HoldEngineFov compared an off-axis camera against an on-axis target,
            // never matched, and "corrected" on every single frame for the whole
            // mission. The correction happened to be idempotent, so it worked by
            // luck rather than by design, and the counter that exists to say
            // whether the engine is fighting us read 100% either way - which is
            // the diagnostic that matters here going permanently blind.
            //
            // The real frustum's total angles, in the same terms Camera reports:
            // the horizontal span is the two half-angles added, and the aspect is
            // the tangent width over the tangent height. Off-axis is exactly what
            // those numbers already describe.
            float left  = Math.Abs(fov.AngleLeft);
            float right = Math.Abs(fov.AngleRight);
            float up    = Math.Abs(fov.AngleUp);
            float down  = Math.Abs(fov.AngleDown);

            hfov = left + right;

            double width  = Math.Tan(left) + Math.Tan(right);
            double height = Math.Tan(up) + Math.Tan(down);

            aspect = height > 1e-6 ? (float)(width / height) : 1f;
        }

        /// <summary>
        /// The runtime's frustum for one eye as tangents rather than angles, in
        /// OpenXR's sign convention (left and down negative). Tangents are what
        /// the native patcher's clip-space reshape works in, and converting once
        /// here keeps the two halves of the mod from disagreeing about which
        /// direction "left" is.
        /// </summary>
        public static void EyeTangents(int eye, out float left, out float right,
                                       out float up, out float down)
        {
            BvrFov fov = Views[eye].Fov;

            left = (float)Math.Tan(fov.AngleLeft);
            right = (float)Math.Tan(fov.AngleRight);
            up = (float)Math.Tan(fov.AngleUp);
            down = (float)Math.Tan(fov.AngleDown);

            // SYMMETRIC WHEN THE RENDER MODE IS, WHICH THIS USED TO IGNORE.
            //
            // These tangents are what vp_patch reshapes clip space to, so they
            // ARE the frustum the eye gets rendered with - which is what the
            // summary above claims and what the native side then reports back
            // through vp_measured_tangents for submission.
            //
            // Reading the runtime's raw asymmetric fov here was harmless only
            // while vp_patch was off. Switched on under AFW it would render the
            // canted frustum while submitted_fov submits the containing
            // symmetric one, which is the render-versus-submit mismatch this
            // project has already been bitten by three times.
            //
            // It would also break the warp outright. AFW is a pure horizontal
            // shift only because both eyes share one frustum; give the rendered
            // eye a canted one and the synthesised eye inherits a frustum that
            // is not its own, mirrored the wrong way.
            //
            // EyeProjectionTarget has honoured SymmetricFov all along. This is
            // the same decision, applied to the other consumer of it.
            if (SymmetricFov)
            {
                float h = Math.Max(Math.Abs(left), Math.Abs(right));
                float v = Math.Max(Math.Abs(up), Math.Abs(down));

                left = -h;
                right = h;
                up = v;
                down = -v;
            }
        }

        /// <summary>
        /// The horizontal focal length, in pixels, of the frustum eye
        /// <paramref name="eye"/> is actually RENDERED with, for an eye image
        /// <paramref name="widthPx"/> pixels across.
        ///
        /// WHY THIS IS NOT (widthPx / 2) / tan(hfov / 2)
        ///
        /// That form assumes the optical axis runs through the middle of the
        /// image, which is only true of a symmetric frustum. Off-axis it is
        /// wrong by the ratio between the two halves - about 3% on this headset,
        /// which is small but real, and it is wrong in the one place where a
        /// small error shows: focal length scales disparity directly, so the
        /// warp puts everything at slightly the wrong depth and the reticle sits
        /// slightly off the thing it is aiming at.
        ///
        /// The general form holds either way. The image spans tan(left) +
        /// tan(right) at unit distance, so a pixel is that span divided by the
        /// width, and the focal length is its reciprocal.
        /// </summary>
        public static float EyeFocalPx(int eye, float widthPx)
        {
            if (eye < 0 || eye > 1 || widthPx <= 0f)
                return 0f;

            BvrFov fov = Views[eye].Fov;

            double left  = Math.Abs(fov.AngleLeft);
            double right = Math.Abs(fov.AngleRight);

            if (SymmetricFov)
            {
                // The containing frustum is what gets rendered in this mode, so
                // it is what the focal length has to describe.
                double half = Math.Max(left, right);
                left = half;
                right = half;
            }

            double span = Math.Tan(left) + Math.Tan(right);
            return span > 1e-6 ? (float)(widthPx / span) : 0f;
        }

        public static void ApplyEyeProjectionTo(Camera cam, int eye)
        {
            if (cam == null || eye < 0 || eye > 1)
                return;

            BvrFov fov = Views[eye].Fov;

            if (SymmetricFov)
            {
                float hHalf = Math.Max(Math.Abs(fov.AngleLeft), Math.Abs(fov.AngleRight));
                float vHalf = Math.Max(Math.Abs(fov.AngleUp), Math.Abs(fov.AngleDown));
                float aspect = (float)(Math.Tan(hHalf) / Math.Tan(vHalf));

                cam.SetFovVertical(vHalf * 2f, aspect, NearPlane, _lastFar);
                LogProjectionOnce($"Shared AFR camera: SYMMETRIC vfov {(vHalf * 2f):F3} rad, " +
                           $"aspect {aspect:F3}, near {NearPlane:F3}, far {_lastFar:F1}.");
            }
            else
            {
                VrMath.FovToFrustum(fov, NearPlane,
                    out float left, out float right, out float bottom, out float top);
                cam.SetViewVolume(true, left, right, bottom, top, NearPlane, _lastFar);
                LogProjectionOnce($"Shared AFR camera: asymmetric view volume, far {_lastFar:F1}.");
            }
        }

        /// <summary>Re-anchors forward and position to wherever the player is
        /// facing right now. Bound to F10 in SubModule.</summary>
        public static void RequestRecentre()
        {
            _recentreRequested = true;

            // The flat screen re-pins too. Someone pressing recentre is saying
            // "put things back in front of me", and outside a mission the screen
            // IS the thing in front of them - so a screen left somewhere awkward
            // is fixable without restarting the game.
            VrFlatScreen.RequestRepin();
        }

        /// <summary>
        /// Drives both eye cameras from the latest predicted pose.
        /// Returns false when VR is not currently able to drive the camera, in
        /// which case the caller must leave the engine's own camera alone.
        /// </summary>
        public static bool Update(MissionScreen screen)
        {
            if (screen == null || !VrSystem.IsActive || !VrSystem.IsStereoReady)
                return false;

            // Deployment is the game's, not ours. Returning false here is what
            // makes CameraPatch hand the camera back, so the free-flying army
            // placement camera behaves exactly as it does without the mod. See
            // VrMissionPhase.
            if (!VrMissionPhase.VrOwnsCamera)
                return false;

            Camera combat = screen.CombatCamera;
            if (combat == null)
                return false;

            // A new MissionScreen means a new mission. The old eye cameras belong
            // to a scene that no longer exists.
            if (!ReferenceEquals(_boundScreen, screen))
            {
                Reset();
                _boundScreen = screen;
            }

            if (NativeMethods.bvr_get_predicted_views(Views, out long displayTime) != (int)BvrStatus.Ok)
                return false;

            if (!_loggedRawPose)
            {
                _loggedRawPose = true;
                LogRawPose(displayTime);
            }

            // A pose that is zeroed, NaN, or degenerate must never reach the
            // engine. SetViewVolume with a zero-extent frustum divides by
            // (right - left) == 0, and a zero-norm quaternion produces a
            // collapsed rotation matrix; both take the process down rather than
            // raising anything catchable.
            if (!PoseIsUsable(displayTime))
            {
                ReportUnusablePose(displayTime);
                return false;
            }

            if (_unusablePoseLogged)
            {
                _unusablePoseLogged = false;
                VrLog.Info("Head pose is valid again; resuming VR camera.");
            }

            LogCameraConvention(combat);

            // ---- body anchor -----------------------------------------------
            MatrixFrame body = ResolveAnchor(combat);

            // ---- head pose in stage space ----------------------------------
            // Midpoint of the two eye positions. Orientation is taken from the
            // left eye: OpenXR runtimes report both eyes with the same
            // orientation in every case that matters, and averaging quaternions
            // properly is more machinery than the difference justifies.
            MatrixFrame headStage = VrMath.ToBannerlord(
                Views[0].Orientation,
                Scale(VrMath.Midpoint(Views[0].Position, Views[1].Position)));

            if (_recentreRequested)
            {
                _recentre = ComputeRecentre(headStage);
                _recentreRequested = false;
                VrLog.Info("Recentred.");
            }

            // Published for VrEngineAim, which steers the ENGINE's camera - and so
            // its culling - toward where the head is looking. Read from the next
            // frame's UpdateCamera prefix, which runs ahead of this postfix; one
            // frame of lag is irrelevant for deciding what to submit, and the
            // rendered view never goes through here at all.
            PublishHeadAngles(headStage);

            MatrixFrame anchor = body.TransformToParent(_recentre);

            // Both eyes of a pair must come from ONE CAMERA as well as one world.
            //
            // TEST 48 froze the simulation between the two eyes of a pair, which
            // stopped the soldiers moving between them. It did not stop the
            // CAMERA moving between them, and on a turning horse the camera is
            // the thing moving fastest of all. So a "consistent" pair was still
            // being rendered from two anchors several degrees apart.
            //
            // That is much worse than a double image, and the report says so
            // exactly: "the 3d effect and the object scale stop working until I
            // slow down". A stereo pair is two views separated by the IPD and
            // NOTHING ELSE. Let the anchor rotate by dTheta between the two
            // captures and the effective baseline stops being the IPD - it picks
            // up the pivot swing, changes length and points somewhere else. The
            // brain reads baseline as scale, so the world silently resizes while
            // turning and snaps back when it stops. Depth going wrong while
            // turning was never a reprojection problem; the pair was not a
            // stereo pair.
            //
            // So the whole camera state is latched for the life of a pair: the
            // anchor, the head, and both eye offsets. The second eye of a pair is
            // rendered from exactly the state the first was, and the pair differs
            // by the IPD alone - which is the definition of a stereo pair.
            //
            // Keyed on the generation, not on a frame count or an eye index, so
            // it groups frames the same way the native promotion does. The two
            // cannot disagree about where a pair starts, because they are reading
            // the same number.
            //
            // Head tracking is NOT slowed by this. The latched pose is what gets
            // RENDERED and what gets SUBMITTED, and the compositor reprojects
            // both eyes to the live head pose at display time - which is the same
            // arrangement that already carries AFR's held eye.
            BuildEyeStages();

            // Kept before the latch overwrites it. The latch replaces `anchor`
            // with the one the whole PAIR is drawn from, which is what makes the
            // stereo correct - but it also means everything downstream sees an
            // anchor that only advances once per pair. Publishing that as the
            // correction's target compared it against itself and produced a delta
            // of exactly zero, which is why the turn correction read 0.00 deg for
            // an entire latched run while the camera was visibly stuttering.
            //
            // This is where the camera actually IS. The latched one says what the
            // pixels are; this says where they belong now.
            MatrixFrame liveAnchor = anchor;

            LatchPairCamera(ref anchor, ref headStage);

            // After the latch, so anything reading it gets the frame actually
            // rendered from rather than a fresher one nothing was drawn with.
            Anchor = anchor;

            // Published for the hands. They are placed RELATIVE TO THE HEAD, so
            // they need the same head pose the eyes were rendered from - not a
            // fresher one, or a hand would be measured against a head the frame
            // was not drawn with and would swim by that difference.
            HeadStage = headStage;

            // The rotation the compositor cannot see.
            //
            // Every eye frame below is anchor * eyeStage. The runtime is told
            // eyeStage - that is the head pose it located itself - and it is
            // told nothing whatsoever about the anchor. Standing still that is
            // harmless. Turning, it is the whole problem: AFR always has one
            // eye a frame old, and while the mouse is moving that eye was drawn
            // with the anchor pointing somewhere the fresh eye no longer agrees
            // with, so the pair stops fusing exactly while turning and starts
            // again the moment you stop.
            //
            // ANCHOR, not body: the recentre is folded in here, so a recentre
            // is reprojected correctly too rather than arriving as a step.
            PublishBodyYaw(anchor, liveAnchor);

            float far = combat.Far;
            _lastFar = far;

            // Read eye 0 back BEFORE we touch it this frame: this is the state the
            // engine left it in after rendering with it. See ProbeCameras.
            ProbeCameras(combat);

            for (int eye = 0; eye < 2; eye++)
            {
                if (EyeCameras[eye] == null)
                    EyeCameras[eye] = Camera.CreateCamera();

                // Built above, and possibly LATCHED from the first eye of this
                // pair - see LatchPairCamera. Recomputing it here from Views
                // would quietly undo the latch and put the turning-horse
                // baseline error straight back.
                MatrixFrame eyeStage = EyeStage[eye];
                MatrixFrame eyeWorld = anchor.TransformToParent(eyeStage);
                Camera cam = EyeCameras[eye];

                // Published for AFR, which aims its own single camera at these
                // rather than being handed a different camera object every frame.
                EyeWorld[eye] = eyeWorld;

                // LookAt rather than assigning Frame directly, because a Bannerlord
                // camera's basis is NOT the object convention. MatrixFrame.CreateLookAt
                // for a level view along +Y returns f = world up and u = the view
                // direction - so assigning a frame built the natural way (f = gaze)
                // aims the camera along the player's up axis, and the view ends up
                // pointing at the ground however you hold your head.
                //
                // Handing the engine a position, a target and an up vector sidesteps
                // the convention entirely: it builds whichever internal basis it wants.
                Vec3 target = new Vec3(
                    eyeWorld.origin.x + eyeWorld.rotation.f.x,
                    eyeWorld.origin.y + eyeWorld.rotation.f.y,
                    eyeWorld.origin.z + eyeWorld.rotation.f.z, -1f);

                cam.LookAt(eyeWorld.origin, target, eyeWorld.rotation.u);

                if (eye == 0)
                {
                    // _lastEyeOrigin is NOT recorded here any more. This ran for
                    // eye 0 every frame while the thing it guards against writes
                    // whichever eye AFR is currently on, so half the time the
                    // guard held the wrong one. AimAtEye records it instead,
                    // being the code that actually performs the write.

                    // The monitor gets its OWN camera, tracking the left eye.
                    //
                    // MissionScreen.CustomCamera and SceneLayer.SetCamera hand a
                    // camera to the engine, which then configures it for the game
                    // window - viewport, aspect, view volume. Passing an eye camera
                    // there means the engine overwrites the asymmetric VR frustum on
                    // the very object our SceneView renders from, every frame. A
                    // separate object costs nothing and makes that impossible.
                    if (MirrorCam == null)
                        MirrorCam = Camera.CreateCamera();

                    MirrorCam.LookAt(eyeWorld.origin, target, eyeWorld.rotation.u);

                    UpdateMirrorProjection(combat, Views[eye].Fov, far);
                }

                // Asymmetric frustum, straight from the runtime. SetFovVertical
                // would force symmetry and throw away the off-axis FOV that both
                // Quest 3 and Pico 4 actually have.
                //
                // Applied ONLY when it actually changes. rgl does real work inside
                // these reconfiguration calls and does not tolerate them at frame
                // rate: doing this every frame is what turned a 74-second session
                // into a one-second one. The runtime's FOV is fixed for the life of
                // the session, so this fires once and then never again.
                if (EngineProjection)
                {
                    // The engine's own frustum, on our head-tracked camera. Guarded
                    // the same way the mirror is: CombatCamera reads 0.000 for the
                    // first frames of a mission and a degenerate projection is far
                    // worse than a late one.
                    float eFov = combat.HorizontalFov;
                    float eAspect = combat.GetAspectRatio();

                    if (eFov > 0.01f && eFov < 3.13f && eAspect > 0.1f && eAspect < 10f &&
                        EngineProjectionChanged(eye, eFov, eAspect, far))
                    {
                        cam.SetFovHorizontal(eFov, eAspect, NearPlane, far);
                        RecordSet(eye, eFov, eAspect, far);
                        VrLog.Info($"Eye {eye} ENGINE projection: hfov {eFov:F3} rad, " +
                                   $"aspect {eAspect:F3}, near {NearPlane:F3}, far {far:F1}.");
                    }
                }
                else if (ProjectionChanged(eye, Views[eye].Fov, far))
                {
                    if (SymmetricFov)
                    {
                        // A symmetric frustum that CONTAINS the runtime's
                        // asymmetric one, applied through the engine's own
                        // SetFovVertical rather than SetViewVolume.
                        //
                        // The missing geometry survived every change to view
                        // count - three views, two, one, and finally AFR's single
                        // render - so it was never about competing views. The one
                        // thing constant throughout is that we hand the engine an
                        // off-axis view volume. If its culling derives a symmetric
                        // cone from the camera instead of reading the volume, then
                        // everything outside that cone is discarded: present near
                        // the centre, gone toward the edges. Widening to a
                        // containing symmetric frustum cannot cull less than the
                        // truth, so if the geometry returns, that is the mechanism.
                        //
                        // The cost is a slightly off-centre image per eye, which
                        // is a diagnosis, not a shipping configuration.
                        BvrFov fov = Views[eye].Fov;

                        float hHalf = Math.Max(Math.Abs(fov.AngleLeft), Math.Abs(fov.AngleRight));
                        float vHalf = Math.Max(Math.Abs(fov.AngleUp), Math.Abs(fov.AngleDown));

                        float aspect = (float)(Math.Tan(hHalf) / Math.Tan(vHalf));

                        cam.SetFovVertical(vHalf * 2f, aspect, NearPlane, far);
                        RecordSet(eye, hHalf * 2f, aspect, far);
                        VrLog.Info($"Eye {eye} SYMMETRIC projection: vfov {(vHalf * 2f):F3} rad, " +
                                   $"aspect {aspect:F3}, far {far:F1}.");
                    }
                    else
                    {
                        VrMath.FovToFrustum(Views[eye].Fov, NearPlane,
                            out float left, out float right, out float bottom, out float top);
                        cam.SetViewVolume(true, left, right, bottom, top, NearPlane, far);
                        RecordSet(eye,
                            (float)(Math.Atan2(right, NearPlane) - Math.Atan2(left, NearPlane)),
                            (right - left) / (top - bottom), far);
                        VrLog.Info($"Eye {eye} projection set (far {far:F1}).");
                    }
                }

            }

            _haveSubmitted = true;
            HaveEyeFrames = true;

            HeadFrame = anchor.TransformToParent(headStage);
            DisplayTime = displayTime;
            HasPose = true;

            TrackGain(headStage, body);

            // Tell the compositor which pose we actually rendered with, so it
            // reprojects against that rather than a newer prediction.
            //
            // THE LATCHED ONES WHEN THERE ARE LATCHED ONES. Both eyes of a pair
            // are drawn from a single head pose; each is captured on its own
            // frame, so publishing the live locate would label the two eyes with
            // poses from different moments and the compositor would move them by
            // different amounts. A difference between the eyes is not a wobble
            // that averages out, it is a disparity the eyes are asked to fuse and
            // cannot - which is why it reads as strain and as the world changing
            // size, and only once something is moving.
            //
            // Unlatched, PairView is never populated and this is Views exactly as
            // before.
            int rc = NativeMethods.bvr_set_used_views(
                _havePairLatch ? PairView : Views, displayTime);
            if (rc != (int)BvrStatus.Ok && rc != (int)BvrStatus.SessionNotReady)
                VrLog.Warn("bvr_set_used_views returned " + rc);

            if (!_loggedFirstPose)
            {
                _loggedFirstPose = true;
                VrLog.Info(string.Format(
                    "First head pose. Eye separation {0:F4} m, FOV L/R/U/D {1:F1}/{2:F1}/{3:F1}/{4:F1} deg.",
                    EyeSeparation(),
                    Deg(Views[0].Fov.AngleLeft), Deg(Views[0].Fov.AngleRight),
                    Deg(Views[0].Fov.AngleUp), Deg(Views[0].Fov.AngleDown)));
            }

            return true;
        }

        /// <summary>Drops the eye cameras. Called when the mission changes and on
        /// shutdown; the engine hands out real native cameras, so they are
        /// released rather than abandoned.</summary>
        public static void Reset()
        {
            // The eye cameras deliberately SURVIVE a mission change.
            //
            // VrStereoRenderer's SceneViews hold these exact Camera objects. If
            // this released and recreated them - which it used to, on every
            // MissionScreen change - the engine would be left rendering a stereo
            // view through a freed camera, and pressing Ready in a custom battle
            // took the game down instantly. They are cheap; they now live until
            // ReleaseCameras() at shutdown.
            _boundScreen = null;
            _loggedFirstPose = false;
            HasPose = false;

            // Gain is measured per mission; carrying totals across a scene change
            // would fold the engine's own repositioning into the ratio.
            ResetWindow();
            _firstWindowDone = false;
            _haveSubmitted = false;
            _echoLogged = false;
            _haveAnchor = false;

            // Body yaw and the input-sign calibration belong to one mission.
            VrEngineAim.Reset();

            // Which eye point is in use is a process-wide config decision, but the
            // mounted comparison is per-mission: the first horse of the next
            // battle should measure itself rather than inherit a stale line.
            VrEyePoint.Reset();

            // A camera captured from the mission that just ended must not anchor
            // the one starting. The next UpdateCamera refills it within a frame.
            VrGameCamera.Reset();

            // Mount state belongs to one mission. Carrying "was mounted" across a
            // scene change would fire a dismount recentre on the first frame of
            // the next battle, for a horse that no longer exists.
            VrMissionPhase.Reset();

            // The pointer re-seeds from the engine cursor in the next menu
            // rather than from a position that belonged to a closed screen.
            VrStickMouse.Reset();

            VrWeaponHands.Reset();
            VrBodyHide.Reset();
            VrWeaponEntity.Reset();
            VrHandCalibration.Reset();
            _anchorSource = AnchorSource.None;
            _loggedCameraConvention = false;

            // Cameras survive a mission change, so their projections do too.
            // Nothing to invalidate here.

            // The next mission gets a fresh anchor rather than inheriting the
            // orientation the player happened to have in the previous one.
            _recentreRequested = true;
        }

        /// <summary>Releases the eye cameras. Shutdown only - anything holding
        /// them (the stereo SceneViews) must be torn down first.</summary>
        public static void ReleaseCameras()
        {
            for (int eye = 0; eye < 2; eye++)
            {
                if (EyeCameras[eye] == null)
                    continue;

                try
                {
                    EyeCameras[eye].ReleaseCamera();
                }
                catch (Exception ex)
                {
                    VrLog.Error("ReleaseCamera threw.", ex);
                }

                EyeCameras[eye] = null;
            }

            if (MirrorCam != null)
            {
                try
                {
                    MirrorCam.ReleaseCamera();
                }
                catch (Exception ex)
                {
                    VrLog.Error("ReleaseCamera threw for the mirror camera.", ex);
                }

                MirrorCam = null;
            }
        }

        /// <summary>
        /// Builds the frame that cancels the head's current yaw and position.
        ///
        /// Uses the flattened rotation's own inverse (a transpose, since it is
        /// orthonormal) rather than reconstructing R(-yaw) with RotateAboutUp.
        /// That keeps the result exactly inverse and sidesteps having to guess
        /// which sign convention RotateAboutUp uses - a guess that fails as
        /// "recentre turns me 180 degrees the wrong way".
        /// </summary>
        private static MatrixFrame ComputeRecentre(MatrixFrame head)
        {
            Mat3 flat = VrMath.YawOnly(head.rotation);

            MatrixFrame r = default;
            r.rotation = flat.TransformToLocal(Mat3.Identity);   // flat transposed

            Vec3 p = flat.TransformToLocal(head.origin);
            r.origin = new Vec3(-p.x, -p.y, -p.z, -1f);
            return r;
        }

        // --- rotation-gain instrumentation -----------------------------------
        // Measures how far the rendered view turns per degree the headset turns.
        // 1.0 is correct. Anything else is a bug, and the ratio says which kind:
        // a constant multiplier is a maths error, while a figure that climbs with
        // frame rate is a feedback loop through engine state.
        // --- projection caching -----------------------------------------------
        // rgl's camera reconfiguration calls are expensive and, at frame rate,
        // fatal. Everything below exists so SetViewVolume and SetFovHorizontal
        // fire once per session instead of ninety times a second.
        private static readonly BvrFov[] LastFov = new BvrFov[2];
        private static readonly float[] LastFar = new float[2];
        private static readonly bool[] HaveProjection = new bool[2];

        /// <summary>Which frustum each eye was last written with, so a mode
        /// change that flips SymmetricFov forces the projection to be applied
        /// again even though the runtime fov has not moved.</summary>
        private static readonly bool[] LastSymmetric = new bool[2];

        private static float _mirrorFov = float.NaN;
        private static float _mirrorAspect = float.NaN;
        private static float _mirrorFar = float.NaN;
        private static bool _mirrorUsingEyeFrustum;

        private static bool Changed(float previous, float current)
        {
            return float.IsNaN(previous) || Math.Abs(previous - current) > 1e-4f;
        }

        /// <summary>
        /// Gives the desktop mirror a projection that is always valid.
        ///
        /// CombatCamera.HorizontalFov reads 0.000 on the first frames of a mission,
        /// and feeding that to SetFovHorizontal builds a degenerate projection -
        /// NaN matrices, no terrain on the monitor, and the renderer taking the
        /// process down about a second later. Caching made it permanent, because
        /// the bad value was recorded as "already applied".
        ///
        /// So the engine's FOV is used only once it is sane, and until then the
        /// mirror borrows the left eye's frustum. That is always well-formed; it
        /// just looks horizontally stretched on a 16:9 window, which is exactly how
        /// the monitor behaved before this camera existed.
        /// </summary>
        private static void UpdateMirrorProjection(Camera combat, BvrFov eyeFov, float far)
        {
            float fov = combat.HorizontalFov;
            float aspect = combat.GetAspectRatio();

            bool usable = fov > 0.01f && fov < 3.13f && aspect > 0.1f && aspect < 10f && far > NearPlane;

            if (usable)
            {
                if (!Changed(_mirrorFov, fov) && !Changed(_mirrorAspect, aspect) && !Changed(_mirrorFar, far))
                    return;

                _mirrorFov = fov;
                _mirrorAspect = aspect;
                _mirrorFar = far;
                _mirrorUsingEyeFrustum = false;

                MirrorCam.SetFovHorizontal(fov, aspect, NearPlane, far);
                VrLog.Info($"Mirror projection: engine FOV {fov:F3}, aspect {aspect:F3}, far {far:F1}.");
                return;
            }

            if (_mirrorUsingEyeFrustum)
                return;

            _mirrorUsingEyeFrustum = true;
            _mirrorFov = float.NaN;   // retry the engine FOV as soon as it is sane

            float safeFar = far > NearPlane ? far : 1000f;
            VrMath.FovToFrustum(eyeFov, NearPlane,
                out float left, out float right, out float bottom, out float top);
            MirrorCam.SetViewVolume(true, left, right, bottom, top, NearPlane, safeFar);

            VrLog.Warn($"Engine FOV not usable yet (fov {fov:F3}, aspect {aspect:F3}); " +
                       "mirror is borrowing the eye frustum. The monitor will look stretched.");
        }

        /// <summary>
        /// True when this eye's projection has to be written again.
        ///
        /// SymmetricFov is part of the key, not just the fov and the far plane.
        /// It follows the render mode now, so it moves when the player picks a
        /// different mode from the panel - at which point the runtime's fov and
        /// the far plane are both exactly what they were, and a cache keyed on
        /// those alone would report "nothing changed" and leave the eye on the
        /// frustum the previous mode wanted. Switching to AFW would then appear
        /// to do nothing until the next mission loaded, which is a worse bug
        /// than the one it would be hiding.
        /// </summary>
        private static bool ProjectionChanged(int eye, BvrFov fov, float far)
        {
            bool symmetric = SymmetricFov;

            if (!HaveProjection[eye] ||
                LastSymmetric[eye] != symmetric ||
                Changed(LastFar[eye], far) ||
                Changed(LastFov[eye].AngleLeft, fov.AngleLeft) ||
                Changed(LastFov[eye].AngleRight, fov.AngleRight) ||
                Changed(LastFov[eye].AngleUp, fov.AngleUp) ||
                Changed(LastFov[eye].AngleDown, fov.AngleDown))
            {
                LastFov[eye] = fov;
                LastFar[eye] = far;
                LastSymmetric[eye] = symmetric;
                HaveProjection[eye] = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Same "only when it actually changes" discipline as
        /// <see cref="ProjectionChanged"/>, but keyed on the ENGINE's frustum -
        /// which, unlike the runtime's, really does move: it was 1.695, then
        /// 1.885, then 2.260 rad within one mission.
        /// </summary>
        private static readonly float[] LastEngineFov = new float[2];
        private static readonly float[] LastEngineAspect = new float[2];
        private static readonly bool[] HaveEngineProjection = new bool[2];

        private static bool EngineProjectionChanged(int eye, float fov, float aspect, float far)
        {
            if (!HaveEngineProjection[eye] ||
                Changed(LastEngineFov[eye], fov) ||
                Changed(LastEngineAspect[eye], aspect) ||
                Changed(LastFar[eye], far))
            {
                LastEngineFov[eye] = fov;
                LastEngineAspect[eye] = aspect;
                LastFar[eye] = far;
                HaveEngineProjection[eye] = true;
                return true;
            }

            return false;
        }

        // --- camera probe -------------------------------------------------------
        //
        // This mod's own comment, at the point where MirrorCam is created, says:
        //
        //     "MissionScreen.CustomCamera and SceneLayer.SetCamera hand a camera to
        //      the engine, which then configures it for the game window - viewport,
        //      aspect, view volume. Passing an eye camera there means the engine
        //      overwrites the asymmetric VR frustum on the very object our SceneView
        //      renders from, every frame."
        //
        // A separate MirrorCam was created precisely to avoid that. AFR then does
        // the forbidden thing by design - it hands an EYE camera to CustomCamera
        // and SceneLayer.SetCamera, because making the engine's own view render our
        // eye is the whole point of AFR.
        //
        // If that comment is right, the projection we set is overwritten by the
        // engine every frame, and the frustum the scene is actually culled and
        // rendered with is not the one we asked for. The engine FOV in the log
        // climbing 1.695 -> 1.885 -> 2.260 rad within one mission is what a
        // read-modify-write loop between the two cameras would look like.
        //
        // So stop asserting it and measure it: read the eye camera back at the top
        // of the frame, before we write to it, and print it next to what we last
        // set. Once a second, so it costs nothing.
        private static readonly bool CameraProbe = VrConfig.Bool("camera_probe", true);

        private static float _setHFov, _setAspect, _setFar;
        private static int _probeTick;

        /// <summary>Remembers what we asked for, so the probe can say whether it
        /// survived the frame. Eye 0 only - the probe reads eye 0.</summary>
        private static void RecordSet(int eye, float hfov, float aspect, float far)
        {
            if (eye != 0)
                return;

            _setHFov = hfov;
            _setAspect = aspect;
            _setFar = far;
        }

        private static void ProbeCameras(Camera combat)
        {
            if (!CameraProbe || EyeCameras[0] == null)
                return;

            // ~90 fps, so this is roughly one line a second.
            if (++_probeTick < 90)
                return;
            _probeTick = 0;

            try
            {
                Camera cam = EyeCameras[0];
                MatrixFrame f = cam.Frame;

                VrLog.Info(string.Format(
                    "Probe eye0 READBACK hfov {0:F3} aspect {1:F3} far {2:F1} pos ({3:F2},{4:F2},{5:F2}) | "
                    + "WE SET hfov {6:F3} aspect {7:F3} far {8:F1} | combat hfov {9:F3} aspect {10:F3} far {11:F1}",
                    cam.HorizontalFov, cam.GetAspectRatio(), cam.Far,
                    f.origin.x, f.origin.y, f.origin.z,
                    _setHFov, _setAspect, _setFar,
                    combat.HorizontalFov, combat.GetAspectRatio(), combat.Far));
            }
            catch (Exception ex)
            {
                VrLog.Error("Camera probe failed; disabling it.", ex);
                _probeTick = int.MinValue / 2;
            }
        }

        private static bool _loggedCameraConvention;

        /// <summary>
        /// Records which basis vector of a live camera's frame is its actual view
        /// direction, by comparing Camera.Direction against the frame it came from.
        ///
        /// This cannot be worked out offline - Camera's static helpers need the
        /// engine's native layer - and Phase 4 needs the answer to build its own
        /// SceneView cameras. Logged once from the engine's own CombatCamera,
        /// which is authoritative by construction.
        /// </summary>
        private static void LogCameraConvention(Camera combat)
        {
            if (_loggedCameraConvention)
                return;

            _loggedCameraConvention = true;

            try
            {
                MatrixFrame f = combat.Frame;
                Vec3 dir = combat.Direction;

                VrLog.Info(string.Format(
                    "Camera convention: Direction=({0:F3},{1:F3},{2:F3}) vs frame " +
                    "s=({3:F3},{4:F3},{5:F3}) f=({6:F3},{7:F3},{8:F3}) u=({9:F3},{10:F3},{11:F3}) " +
                    "-> dot(s)={12:F3} dot(f)={13:F3} dot(u)={14:F3}",
                    dir.x, dir.y, dir.z,
                    f.rotation.s.x, f.rotation.s.y, f.rotation.s.z,
                    f.rotation.f.x, f.rotation.f.y, f.rotation.f.z,
                    f.rotation.u.x, f.rotation.u.y, f.rotation.u.z,
                    Dot(dir, f.rotation.s), Dot(dir, f.rotation.f), Dot(dir, f.rotation.u)));
            }
            catch (Exception ex)
            {
                VrLog.Error("Could not read the camera convention.", ex);
            }
        }

        private static float Dot(Vec3 a, Vec3 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }

        // --- body anchor ------------------------------------------------------
        private static Vec3 _lastEyeOrigin;
        private static bool _haveSubmitted;
        private static bool _echoLogged;
        private static MatrixFrame _lastAnchor;
        private static bool _haveAnchor;
        private enum AnchorSource { None, Agent, Camera }

        private static AnchorSource _anchorSource = AnchorSource.None;

        /// <summary>
        /// anchor = camera keeps the body anchored to the engine camera even after
        /// the player has spawned, so the agent path can be ruled in or out as the
        /// cause of the crash on Ready. Pressing Ready is exactly when MainAgent
        /// first exists, so that path switches on at the moment things break.
        /// </summary>
        private static readonly bool UseAgentAnchor =
            !string.Equals(VrConfig.String("anchor", "agent"), "camera",
                           StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Where the player's virtual body is, and which way it faces.
        ///
        /// Anchored to the player agent, NOT to CombatCamera. Assigning
        /// MissionScreen.CustomCamera is what makes the engine render from our
        /// camera, but the engine then folds that camera back into CombatCamera -
        /// so reading CombatCamera as the anchor means our own head offset is
        /// added to itself every frame. Lean forward and the viewpoint accelerates
        /// forward; stand at normal height and it sinks through the terrain at a
        /// steady rate. The agent's eye position and look direction are driven by
        /// input alone and we never write to either, so no loop can form.
        ///
        /// The camera fallback exists for spectating and death, where there is no
        /// main agent. It rejects frames in which CombatCamera has become an echo
        /// of our own last output.
        /// </summary>
        private static MatrixFrame ResolveAnchor(Camera combat)
        {
            Agent agent = UseAgentAnchor ? Mission.Current?.MainAgent : null;

            if (agent != null)
            {
                // Pressing Ready spawns the player, so this path switches on at
                // exactly the moment the game has been crashing. Reading an agent
                // mid-spawn is the suspicion; anchor = camera in the config keeps
                // us off it entirely so the two can be told apart.
                MatrixFrame frame = default;

                // THE GAME'S OWN FIRST-PERSON CAMERA, when there is one.
                //
                // The mod no longer synthesises a first person of its own. The
                // engine is held in its native first person and the camera it
                // builds off the head bone is captured at the tail of its own
                // UpdateCamera - so this is the point TaleWorlds places a
                // first-person camera at, not our reconstruction of it. See
                // VrGameCamera.
                //
                // VrEyePoint remains the fallback, and it is a real one rather
                // than a formality: there is no game first-person camera before
                // the first UpdateCamera of a mission, nor on any frame the
                // engine builds a spectator or third-person camera instead.
                //
                // NOT Agent.GetEyeGlobalPosition in either case. That is where
                // the game puts the audio listener, not where it puts a
                // first-person camera, and it is built from the agent's ground
                // position - which on a horse is the HORSE's. See VrEyePoint.
                if (!VrGameCamera.TryGetEyeOrigin(out frame.origin))
                    frame.origin = VrEyePoint.Resolve(agent);

                // THE FEEDBACK CUT. Once VrEngineAim is steering the engine camera
                // at the head, the agent turns to follow it, so LookDirection now
                // contains the head yaw we ourselves injected. Building the anchor
                // from it and then composing the head pose on top would apply that
                // yaw a second time, and the world would turn twice as far as the
                // head does. VrEngineAim's body yaw is integrated from the mouse
                // alone and cannot contain our own contribution, so it is the only
                // safe heading to anchor to.
                frame.rotation = VrEngineAim.AnchorMustIgnoreLook
                    ? VrEngineAim.BodyHeading()
                    : VrMath.HeadingFromForward(agent.LookDirection);

                if (_anchorSource != AnchorSource.Agent)
                {
                    _anchorSource = AnchorSource.Agent;
                    VrLog.Info("Anchor -> player agent's eyes.");
                }

                _lastAnchor = frame;
                _haveAnchor = true;
                return frame;
            }

            // The camera the ENGINE built, captured before our own writes landed
            // on it. combat.Frame at this point in the frame is our last eye
            // pose, and anchoring to that adds the head offset to itself every
            // frame - the pre-Ready backward drift. See VrGameCamera.
            MatrixFrame engineFrame;
            if (!VrGameCamera.TryGetCameraFrame(out engineFrame))
                engineFrame = combat.Frame;

            if (IsEcho(engineFrame.origin))
            {
                DetectAnchorEcho();
                if (_haveAnchor)
                    return _lastAnchor;
            }

            MatrixFrame fallback = default;
            fallback.origin = engineFrame.origin;

            // Same feedback cut as the agent path above: the engine camera is the
            // thing we are steering, so its own yaw is the last place to read a
            // body heading from.
            fallback.rotation = VrEngineAim.AnchorMustIgnoreLook
                ? VrEngineAim.BodyHeading()
                : VrMath.YawOnly(engineFrame.rotation);

            if (_anchorSource != AnchorSource.Camera)
            {
                _anchorSource = AnchorSource.Camera;
                VrLog.Info(UseAgentAnchor
                    ? "Anchor -> engine camera (no main agent)."
                    : "Anchor -> engine camera (anchor = camera in config).");
            }

            _lastAnchor = fallback;
            _haveAnchor = true;
            return fallback;
        }

        private static bool IsEcho(Vec3 anchorOrigin)
        {
            if (!_haveSubmitted)
                return false;

            float dx = anchorOrigin.x - _lastEyeOrigin.x;
            float dy = anchorOrigin.y - _lastEyeOrigin.y;
            float dz = anchorOrigin.z - _lastEyeOrigin.z;
            return dx * dx + dy * dy + dz * dz <= 1e-4f;
        }

        /// <summary>Records that the camera fallback hit the feedback case, once.
        /// The frame is discarded either way; this only explains why.</summary>
        private static void DetectAnchorEcho()
        {
            if (_echoLogged)
                return;

            _echoLogged = true;
            VrLog.Warn(
                "Engine camera is echoing our VR camera: CombatCamera.origin equals the " +
                "eye camera written last frame. Holding the previous anchor instead; " +
                "without this the viewpoint accelerates in whatever direction you lean.");
        }

        private const int GainWindow = 600;

        private static float _prevHeadYaw, _prevViewYaw, _prevBodyYaw;
        private static float _headTravel, _viewTravel, _bodyTravel, _residualTravel;
        private static Vec3 _windowStartOrigin;
        private static int _gainSamples;
        private static bool _windowOpen;
        private static bool _firstWindowDone;

        private static void TrackGain(MatrixFrame headStage, MatrixFrame body)
        {
            float headYaw = YawOf(headStage.rotation);
            float viewYaw = YawOf(HeadFrame.rotation);
            float bodyYaw = YawOf(body.rotation);

            if (!_windowOpen)
            {
                // Baseline at the START of each window, not at mission load. The
                // engine camera is still being placed on the first frames, so a
                // baseline taken there is a near-origin value and every later
                // delta reads as a huge, fictitious movement.
                _windowStartOrigin = body.origin;
                _windowOpen = true;
            }
            else
            {
                _headTravel += Math.Abs(WrapPi(headYaw - _prevHeadYaw));
                _viewTravel += Math.Abs(WrapPi(viewYaw - _prevViewYaw));
                _bodyTravel += Math.Abs(WrapPi(bodyYaw - _prevBodyYaw));

                // THE RESIDUAL, and it is the only honest gain measurement.
                //
                // view = anchor * head, so viewYaw - bodyYaw should track the
                // head EXACTLY and nothing else. Comparing raw view travel to
                // head travel instead counts every degree the mouse and the
                // horse contribute as though the head had caused it - and on a
                // turning horse that is most of them. The logs show it plainly:
                // "headset 33.0 view 300.9 (x9.13) | engine yaw 278.9". The head
                // moved 33 degrees, the horse turned 279, and the safety net
                // read a runaway feedback loop that was not there and switched
                // head aiming off - repeatedly, all session.
                //
                // Subtracting the body first leaves the transfer we actually
                // care about: how far the view turns per degree of HEAD.
                _residualTravel += Math.Abs(WrapPi(
                    WrapPi(viewYaw - bodyYaw) - WrapPi(_prevViewYaw - _prevBodyYaw)));
            }

            _prevHeadYaw = headYaw;
            _prevViewYaw = viewYaw;
            _prevBodyYaw = bodyYaw;
            _gainSamples++;

            if (_gainSamples < GainWindow)
                return;

            // The first window covers the battle camera being placed, which is a
            // large engine-driven swing that has nothing to do with head tracking
            // and reads as a huge bogus gain. Measure from the second window on.
            if (!_firstWindowDone)
            {
                _firstWindowDone = true;
                ResetWindow();
                return;
            }

            float dx = body.origin.x - _windowStartOrigin.x;
            float dy = body.origin.y - _windowStartOrigin.y;
            float dz = body.origin.z - _windowStartOrigin.z;

            // How far the head sits from the body anchor. Physically this is
            // bounded by how far you can lean - well under 1.5 m. If it grows
            // without limit, something in OUR chain is integrating instead of
            // being recomputed each frame, and the engine's own camera movement
            // is not to blame.
            float ox = HeadFrame.origin.x - body.origin.x;
            float oy = HeadFrame.origin.y - body.origin.y;
            float oz = HeadFrame.origin.z - body.origin.z;
            float offset = (float)Math.Sqrt(ox * ox + oy * oy + oz * oz);

            float gainRatio = _headTravel > 1e-3f ? _viewTravel / _headTravel : 0f;

            // What the safety net reads. See the note where it is accumulated:
            // the raw ratio counts the mouse and the horse as head movement,
            // this one does not.
            float transfer = _headTravel > 1e-3f ? _residualTravel / _headTravel : 0f;

            VrLog.Info(string.Format(
                "Gain/{0}f: headset {1:F1} view {2:F1} (x{3:F2}) | engine yaw {4:F1} | " +
                "engine moved ({5:F2},{6:F2},{7:F2}) | head-to-body {8:F2} m | transfer x{9:F2}",
                GainWindow, Deg(_headTravel), Deg(_viewTravel), gainRatio,
                Deg(_bodyTravel), dx, dy, dz, offset, transfer));

            // This ratio is the direct measurement of the one hazard in steering
            // the engine camera from the head: if the agent turns to follow the
            // camera and the anchor reads the agent, our own head yaw comes back
            // round and the world turns twice as far as the head. Two windows
            // above 1.4 and VrEngineAim shuts that steering off by itself.
            if (_headTravel > 0.3f)
                VrEngineAim.ReportGain(transfer);


            // Repeat every window. A single sample taken at mission start lands
            // exactly when the battle camera is still settling.
            ResetWindow();
        }

        private static void ResetWindow()
        {
            _gainSamples = 0;
            _headTravel = _viewTravel = _bodyTravel = _residualTravel = 0f;
            _windowOpen = false;
        }

        /// <summary>Heading of a Bannerlord basis, measured from +Y about +Z.</summary>
        private static float YawOf(Mat3 rotation)
        {
            Mat3 flat = YawOnlySafe(rotation);
            return (float)Math.Atan2(flat.f.x, flat.f.y);
        }

        private static bool _bodyYawPublished;
        private static bool _bodyYawFailed;

        /// <summary>
        /// Hands the anchor's yaw to the native layer, which records it against
        /// each AFR capture and uses the difference to rotate a held eye into
        /// agreement with the fresh one before submitting it.
        ///
        /// Ordering is the whole contract, and it is satisfied by where this is
        /// called from rather than by anything here. It runs inside the pose
        /// update, which CameraPatch drives from MissionScreen.OnFrameTick's
        /// postfix - ahead of VrAfrRenderer.Tick and well ahead of the render
        /// and the Present that captures it. So the value the capture reads is
        /// the anchor the frame was genuinely drawn with, which is the only
        /// property that makes the correction correct rather than merely
        /// plausible.
        ///
        /// A failure is reported once and then never again: this is called at
        /// frame rate, and the correction going quiet costs the turning comfort
        /// it was added for and nothing else. AFR itself is untouched by it.
        /// </summary>
        private static void PublishBodyYaw(MatrixFrame anchor, MatrixFrame liveAnchor)
        {
            if (_bodyYawFailed)
                return;

            float yaw = YawOf(anchor.rotation);
            if (float.IsNaN(yaw) || float.IsInfinity(yaw))
                return;

            try
            {
                NativeMethods.bvr_set_body_yaw(yaw);

                // ...and where the anchor IS, not just which way it faces.
                //
                // The yaw covers the game camera turning between the two eyes of
                // an AFR pair. Riding covers far more ground than that: at ten
                // metres a second the camera travels 110 mm in the 11 ms between
                // one eye and the next, which is more than the 65 mm the eyes are
                // meant to be separated by. The pair then carries a false
                // disparity larger than the real one, which is not a depth error
                // so much as the stereo being wrong outright - and it clears the
                // instant you stop, because there is then nothing to be wrong
                // about.
                //
                // Converted back to OpenXR's basis and metres, because native adds
                // it straight onto a submitted pose position. Bannerlord is Z-up
                // and OpenXR is Y-up, so this is a basis change and not just a
                // scale - VrMath.ToOpenXr is the exact inverse of the conversion
                // the eye frames were built through, rather than an axis swap
                // written out again here and able to disagree with it.
                //
                // Divided by WorldScale for the same reason: Scale() multiplies on
                // the way in, so the way out has to divide, or a non-default world
                // scale would publish a displacement in the wrong units and the
                // correction would over- or under-shoot by exactly that factor.
                BvrVec3 xr = VrMath.ToOpenXr(anchor.origin);
                float inv = WorldScale == 0.0f ? 1.0f : 1.0f / WorldScale;
                NativeMethods.bvr_set_body_position(xr.X * inv, xr.Y * inv, xr.Z * inv);

                // ...and where the camera is NOW, which with the pair camera
                // latched is not the same place. The two above describe the image;
                // this describes where it should be carried to. Without it the
                // correction compares the rendered anchor against itself and can
                // only ever produce zero.
                //
                // With latching off these are the same value, so nothing changes.
                float liveYaw = YawOf(liveAnchor.rotation);
                if (!float.IsNaN(liveYaw) && !float.IsInfinity(liveYaw))
                {
                    BvrVec3 lxr = VrMath.ToOpenXr(liveAnchor.origin);
                    NativeMethods.bvr_set_body_target(
                        liveYaw, lxr.X * inv, lxr.Y * inv, lxr.Z * inv);
                }

                if (!_bodyYawPublished)
                {
                    _bodyYawPublished = true;
                    VrLog.Info("Publishing the anchor yaw to the compositor path; a held "
                               + "AFR eye is now reprojected for the GAME camera's turn as "
                               + "well as the head's. Standing still this changes nothing "
                               + "at all - it only does work while turning.");
                }
            }
            catch (Exception ex)
            {
                _bodyYawFailed = true;
                VrLog.Error("Could not publish the anchor yaw; AFR keeps working, but a "
                            + "held eye will only be reprojected for head motion and "
                            + "turning will stay as rough as it was.", ex);
            }
        }

        // --- the camera state a stereo pair is rendered from ---------------------

        /// <summary>This frame's eye offsets in stage space, or the ones latched
        /// from the first eye of the current pair.</summary>
        private static readonly MatrixFrame[] EyeStage = new MatrixFrame[2];

        private static readonly MatrixFrame[] PairEyeStage = new MatrixFrame[2];
        private static readonly BvrEyeView[] PairView = new BvrEyeView[2];
        private static MatrixFrame _pairAnchor;
        private static MatrixFrame _pairHeadStage;
        private static int _pairGeneration = int.MinValue;
        private static bool _havePairLatch;
        private static bool _loggedLatch;

        /// <summary>
        /// Whether the camera is held still across a pair as well as the world.
        ///
        /// Separate from sync_sequential so the two halves can be told apart: 1
        /// is world + camera, 0 is the TEST 48 behaviour of freezing the world
        /// and letting the camera drift between the eyes. If turning is still
        /// wrong with this on, the two settings answer different questions.
        /// </summary>
        private static readonly bool LatchCamera = VrConfig.Bool("sync_sequential_camera", true);

        private static void BuildEyeStages()
        {
            for (int eye = 0; eye < 2; eye++)
            {
                EyeStage[eye] = VrMath.ToBannerlord(
                    Views[eye].Orientation, Scale(Views[eye].Position));
            }
        }

        /// <summary>
        /// Holds the whole camera state still for the life of one stereo pair.
        ///
        /// The first frame of a pair latches; the second reuses. A pair is a run
        /// of frames sharing a simulation generation, which is the same rule the
        /// native promotion uses, so the two cannot disagree about where a pair
        /// begins.
        ///
        /// Everything the rendered view is built from has to be in here. The
        /// anchor, because that is what turns. The head, because it moves too and
        /// a pair rendered from two head poses has the same broken baseline as
        /// one rendered from two anchors. And both eye offsets, because they come
        /// from a fresh locate each frame and would otherwise smuggle the
        /// difference back in through the one door left open.
        /// </summary>
        private static void LatchPairCamera(ref MatrixFrame anchor, ref MatrixFrame headStage)
        {
            // Engaged, not Enabled - and full AFR needs this at least as much as
            // the mode it was written for. Freezing the world does NOT freeze
            // the camera: Mission.OnTick gets dt = 0, but the camera update runs
            // on realDt, so a mouse turn advances the anchor between the two
            // eyes of a pair whatever the simulation is doing. Unlatched, the
            // pair then differs by the IPD PLUS however far the turn got, which
            // is a disparity error rather than a wobble - the thing this file
            // records as reading like eye strain.
            //
            // Its documented cost disappears here for the same reason the
            // freeze's does. "The anchor advances only once per pair" is a
            // half-rate camera when a pair spans two display frames; full AFR
            // fits the pair inside one, so the anchor advances once per display
            // frame and the latch is free.
            if (!LatchCamera || !VrSyncSequential.Engaged || !VrAfrRenderer.Enabled)
            {
                _havePairLatch = false;
                return;
            }

            int generation = VrSyncSequential.Generation;

            if (!_havePairLatch || generation != _pairGeneration)
            {
                // First frame of a new pair. This is the state the pair is FOR.
                _pairGeneration = generation;
                _pairAnchor = anchor;
                _pairHeadStage = headStage;
                PairEyeStage[0] = EyeStage[0];
                PairEyeStage[1] = EyeStage[1];

                // The raw OpenXR views too, not just the Bannerlord frames built
                // from them. These are what gets published as the poses the frame
                // was RENDERED from, and they have to be the latched ones: both
                // eyes are drawn from this pair's head pose, so labelling each
                // with its own capture frame's live locate makes the compositor
                // reproject the two by different amounts - a disparity error
                // rather than a wobble, which is why it reads as eye strain.
                PairView[0] = Views[0];
                PairView[1] = Views[1];
                _havePairLatch = true;

                if (!_loggedLatch)
                {
                    _loggedLatch = true;
                    VrLog.Info("Stereo pairs are now rendered from ONE camera state as well "
                               + "as one world state. The second eye of a pair reuses the "
                               + "first's anchor, head and eye offsets, so the pair differs "
                               + "by the IPD alone. Head tracking is unaffected - the "
                               + "compositor still reprojects both eyes to the live pose.");
                }

                return;
            }

            // Second frame of the pair. Render it from where the first one was.
            anchor = _pairAnchor;
            headStage = _pairHeadStage;
            EyeStage[0] = PairEyeStage[0];
            EyeStage[1] = PairEyeStage[1];
        }

        private static Mat3 YawOnlySafe(Mat3 rotation)
        {
            return VrMath.YawOnly(rotation);
        }

        /// <summary>Wraps to (-pi, pi] so a turn across the seam is not counted
        /// as a full revolution.</summary>
        private static float WrapPi(float radians)
        {
            const float twoPi = (float)(2.0 * Math.PI);
            while (radians > Math.PI) radians -= twoPi;
            while (radians <= -Math.PI) radians += twoPi;
            return radians;
        }

        /// <summary>
        /// Rejects poses that would produce a degenerate camera.
        ///
        /// displayTime is checked first and deliberately: it crosses the P/Invoke
        /// boundary as an out parameter rather than through the struct array, so
        /// "displayTime is sane but the views are zero" means the array write-back
        /// failed, whereas "both are zero" means the native layer reported success
        /// without ever having published a pose. Those are different bugs in
        /// different languages and the log needs to tell them apart.
        /// </summary>
        private static bool PoseIsUsable(long displayTime)
        {
            if (displayTime <= 0)
                return false;

            for (int eye = 0; eye < 2; eye++)
            {
                BvrQuat q = Views[eye].Orientation;
                if (!Finite(q.X) || !Finite(q.Y) || !Finite(q.Z) || !Finite(q.W))
                    return false;

                // A zeroed quaternion is the failure this exists to catch.
                float norm = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
                if (norm < 0.9f || norm > 1.1f)
                    return false;

                BvrVec3 p = Views[eye].Position;
                if (!Finite(p.X) || !Finite(p.Y) || !Finite(p.Z))
                    return false;

                BvrFov f = Views[eye].Fov;
                if (!Finite(f.AngleLeft) || !Finite(f.AngleRight) ||
                    !Finite(f.AngleUp) || !Finite(f.AngleDown))
                    return false;

                // Must enclose a non-empty solid angle, or the frustum has no
                // width and the projection matrix divides by zero.
                if (f.AngleRight - f.AngleLeft < 1e-4f || f.AngleUp - f.AngleDown < 1e-4f)
                    return false;
            }

            return true;
        }

        /// <summary>Logs the first bad pose and then goes quiet. Runs every frame
        /// while the headset is idle, so it must never accumulate.</summary>
        private static void ReportUnusablePose(long displayTime)
        {
            if (_unusablePoseLogged)
                return;

            _unusablePoseLogged = true;
            VrLog.Warn("Unusable head pose; holding the flat camera.");
            LogRawPose(displayTime);
        }

        private static void LogRawPose(long displayTime)
        {
            VrLog.Info("  displayTime = " + displayTime +
                       (displayTime <= 0
                           ? "  <- zero: native published no pose"
                           : "  <- non-zero: the native frame loop is publishing"));

            for (int eye = 0; eye < 2; eye++)
            {
                BvrEyeView v = Views[eye];
                VrLog.Info(string.Format(
                    "  eye{0} quat({1:F4},{2:F4},{3:F4},{4:F4}) pos({5:F4},{6:F4},{7:F4}) " +
                    "fov L{8:F4} R{9:F4} U{10:F4} D{11:F4}",
                    eye,
                    v.Orientation.X, v.Orientation.Y, v.Orientation.Z, v.Orientation.W,
                    v.Position.X, v.Position.Y, v.Position.Z,
                    v.Fov.AngleLeft, v.Fov.AngleRight, v.Fov.AngleUp, v.Fov.AngleDown));
            }
        }

        private static bool Finite(float f)
        {
            return !float.IsNaN(f) && !float.IsInfinity(f);
        }

        private static float EyeSeparation()
        {
            float dx = Views[1].Position.X - Views[0].Position.X;
            float dy = Views[1].Position.Y - Views[0].Position.Y;
            float dz = Views[1].Position.Z - Views[0].Position.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static float Deg(float radians)
        {
            return radians * (180f / (float)Math.PI);
        }
    }
}
