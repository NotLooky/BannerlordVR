using System;
using System.Diagnostics;
using System.Globalization;
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

        /// <summary>TEST 293. The head camera's offset from the body, in metres
        /// along the body's heading: up and forward. Added to the anchor after the
        /// recentre, so a recentre does not cancel it; the hands share the anchor
        /// and move with it. Live: the VR tab sets them.</summary>
        /// <summary>TEST 352d: the head offset the anchor was moved by this frame,
        /// world; the saddle body puts its head where the camera really is.</summary>
        public static Vec3 HeadOffsetWorld { get; private set; }

        public static float HeadOffsetUp { get; set; } = ClampOffset(VrConfig.Float("head_offset_up_m", 0f));
        public static float HeadOffsetForward { get; set; } = ClampOffset(VrConfig.Float("head_offset_forward_m", 0f));

        public static float ClampOffset(float v) => Math.Max(-0.30f, Math.Min(0.30f, v));

        /// <summary>TEST 298: the head camera's height while the player rides, in
        /// metres (the user asked for 0.10). TEST 442: a height of its own, used
        /// INSTEAD of HeadOffsetUp in the saddle rather than added to it ("separate
        /// camera height when mounted") - with the live 0.00 on foot the saddle view
        /// stays where it was. Live: the VR tab's "Height on a mount".</summary>
        public static float HeadOffsetMountedUp { get; set; } =
            ClampOffset(VrConfig.Float("head_offset_mounted_up_m", 0.10f));

        /// <summary>TEST 307: added to HeadOffsetForward only while the player
        /// rides, in metres; negative is back. The user asked for 0.10 back.</summary>
        private static readonly float MountedForward =
            ClampOffset(VrConfig.Float("head_offset_mounted_forward_m", -0.10f));

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
        /// DR cannot have it. The warp shows the destination eye what the
        /// SOURCE eye saw, and on a canted headset the runtime hands out
        /// mirrored asymmetric fovs, so each eye holds around 709 pixels of its
        /// own outer field - a quarter of the width - that the other eye never
        /// saw. Under a symmetric containing frustum both eyes render the same
        /// field and the only difference between them is the small horizontal
        /// disparity the warp exists to apply. That is exactly the rectified
        /// stereo case the warp is built on, and it is why depth_reproject.h says
        /// symmetric_fov earns its keep.
        ///
        /// This was a real regression: asymmetric was turned on globally for the
        /// sharpness, which silently broke DR - the two eyes stopped sharing a
        /// frustum and the warp had no way to say so.
        /// </summary>
        private static bool SymmetricFov
        {
            get
            {
                if (FovChoice >= 0f)
                    return FovChoice > 0f;

                // The map runs AFR and publishes DR off whatever the mode is, and
                // native follows that flag for the fov it submits.
                if (VrMapStereo.Live)
                    return false;

                return VrRenderMode.WarpsSecondEye(VrRenderMode.Active);
            }
        }

        /// <summary>
        /// Extra horizontal half-angle rendered purely so the WARP has something
        /// to read at the edges of the frame. Degrees per side.
        ///
        /// WHY A GUARD BAND EXISTS AT ALL
        ///
        /// The symmetric frustum makes both eyes span the same ANGLES, which is
        /// what lets depth reprojection warp one into the other. It does not make
        /// them span the same SPACE: the eyes are 66 mm apart, so a point sitting
        /// just inside the target eye's left edge can be just outside the source
        /// eye's, and the warp reaches for a sample that was never rendered. The
        /// gather clamps, and the result is a narrow band down the outer edge of
        /// each eye with nothing in it - which is what "a bit of missing world at
        /// the left and right edges, and it moves with my head" describes.
        ///
        /// It is worst at the OUTER edges specifically, and that is the geometry
        /// rather than a coincidence. max(|left|, |right|) makes the symmetric
        /// frustum as wide as the WIDER side, so each eye already over-renders by
        /// about 18 degrees on its nose side and by nothing at all on its temple
        /// side. The side with no slack is the side the band appears on.
        ///
        /// HOW WIDE IT NEEDS TO BE
        ///
        /// The reach is the parallax of the nearest thing at the edge, atan(IPD /
        /// distance): 1.9 degrees at two metres, 0.8 at five, and it keeps
        /// shrinking. Three degrees covers everything past about 1.3 m, which is
        /// past arm's reach. Beyond that it is paying angular resolution for
        /// pixels the compositor crops anyway, so this is deliberately small.
        ///
        /// DEFAULT 0, BECAUSE THE THEORY IT WAS BUILT FOR WAS WRONG.
        ///
        /// It was written to explain a band of missing world down the outer edge
        /// of each eye. Native stereo renders both eyes and warps nothing, so a
        /// warp running out of source cannot survive that mode - and the band
        /// does. The mechanism above is therefore not what is happening, and
        /// paying angular resolution for it is paying for nothing.
        ///
        /// Kept rather than deleted because the reasoning is sound for the case
        /// it describes, and because the plumbing is the useful part: every
        /// consumer of the symmetric frustum now goes through one function
        /// instead of four copies of max(|left|, |right|), which is what makes a
        /// guard band expressible at all. Set stereo_guard_deg to try it.
        /// </summary>
        private static readonly float GuardBand =
            (float)(Math.Max(VrConfig.Float("stereo_guard_deg", 0f), 0f) * Math.PI / 180.0);

        /// <summary>
        /// The half-angles the symmetric modes actually RENDER with.
        ///
        /// Every consumer goes through here, and that is the point rather than
        /// tidiness. EyeFocalPx's own note says why: focal length scales disparity
        /// directly, so a frustum the warp believes in that is a few degrees wider
        /// or narrower than the one rgl drew puts the whole image at the wrong
        /// depth. There were four copies of max(|left|, |right|) in this file and
        /// a guard band added to three of them would have been exactly that bug.
        /// </summary>
        private static void SymmetricHalfAngles(BvrFov fov, out float hHalf, out float vHalf)
        {
            hHalf = Math.Max(Math.Abs(fov.AngleLeft), Math.Abs(fov.AngleRight));
            vHalf = Math.Max(Math.Abs(fov.AngleUp), Math.Abs(fov.AngleDown));

            // TEST 785d: the frustum is SHARED, so it holds both eyes. The eyes used
            // to be mirror images and either one gave the same answer; the detailed
            // fov crop cuts only the left eye's outer edge (Left) or the right eye's
            // (Right), and a frustum from the left eye alone then shrank with Left
            // and cut the right eye's right side off ("left moves the right").
            for (int i = 0; i < 2; i++)
            {
                BvrFov other = Views[i].Fov;
                if (other.AngleRight <= other.AngleLeft)
                    continue;   // not located yet
                hHalf = Math.Max(hHalf, Math.Max(Math.Abs(other.AngleLeft), Math.Abs(other.AngleRight)));
                vHalf = Math.Max(vHalf, Math.Max(Math.Abs(other.AngleUp), Math.Abs(other.AngleDown)));
            }
            hHalf += GuardBand;
        }

        /// <summary>
        /// fov_true_vertical = 1: the shared frustum stays symmetric only ACROSS,
        /// where the eyes are mirror images and one projection has to hold both.
        /// Up and down are the runtime's own - both eyes share them - so the
        /// strip above the lens is no longer drawn and cropped (Quest 3: 46.2 up
        /// against 57.0 down, ~16% of the pixels). Through SetViewVolume, the
        /// call the asymmetric path already uses. Not under DR or the hybrid:
        /// the warp is built on the old shape. Must match xr_context.cpp's
        /// containing_symmetric_fov.
        /// </summary>
        internal static bool TrueVerticalChoice = VrConfig.Float("fov_true_vertical", 0f) > 0f;

        private static bool TrueVertical =>
            TrueVerticalChoice && SymmetricFov && !VrRenderMode.WarpsSecondEye(VrRenderMode.Active);

        /// <summary>The symmetric-across frustum as tangents (left and down negative).</summary>
        private static void SymmetricTangents(BvrFov fov, out float left, out float right,
                                              out float up, out float down)
        {
            SymmetricHalfAngles(fov, out float hHalf, out float vHalf);
            right = (float)Math.Tan(hHalf);
            left = -right;
            up = (float)Math.Tan(TrueVertical ? Math.Abs(fov.AngleUp) : vHalf);
            down = -(float)Math.Tan(TrueVertical ? Math.Abs(fov.AngleDown) : vHalf);
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

        /// <summary>Map units per room metre while the campaign map is a table in
        /// the headset, 0 otherwise. Set by <see cref="UpdateMap"/>, cleared by
        /// VrMapStereo when the map is not live.</summary>
        internal static float MapScale;

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

        /// <summary>Whether the pending recentre was asked for by the player (F10)
        /// rather than being the automatic first-pose one. A manual recentre means
        /// "this pose is my zero", so the standing neutral is taken immediately;
        /// the automatic one happens with the headset wherever it was, and the
        /// neutral waits for the player to be standing still.</summary>
        private static bool _manualRecentre;

        // TEST 389: the height (stage z of the level eye) of the last F10 / panel
        // recentre, which automatic recentres keep.
        private static bool _keepHeight;
        private static float _heightZ;
        private static bool _haveHeightZ;

        /// <summary>
        /// TEST 661: "after pre-battle I sometimes spawn in the wrong position on the
        /// horse" - and the logs show the left stick held for a recentre 8-10 s after
        /// those spawns. Taking control recentres on the frame Ready is pressed: the
        /// rider was spawned that frame, the anchor was still the eye formula (the
        /// game camera 0.3-1.5 m off it), and the saddle offset the mounted view sits
        /// on (VrHeadBody.SaddleLean/CameraLean) was taken in the next half second -
        /// or, from the second battle on, on the spawn frame itself (its calm timer
        /// carried over). This many seconds after a mounted spawn the stick's
        /// recentre is done once more for you - position and saddle offset only; the
        /// yaw stays the spawn's, so a glance aside then cannot turn the horse. 0: off.
        /// </summary>
        private static readonly float SaddleSettleS = VrConfig.Float("mounted_spawn_settle_s", 1.0f);
        private static long _settleAt;
        private static bool _keepYaw;

        // TEST 356: an explicit recentre (F10, the panel) also measures the
        // player's height from the same head pose; the panel can ask alone.
        private static bool _measureHeight;
        private static bool _heightMeasureRequested;

        /// <summary>TEST 356: measure the player's height on the next frame.</summary>
        public static void RequestHeightMeasure() => _heightMeasureRequested = true;

        /// <summary>Whether VR was driving the player's look last frame, so the
        /// moment control is taken can be seen exactly once per mission.</summary>
        private static bool _wasDrivingLook;

        // --- room scale --------------------------------------------------------
        //
        // The player's own translation, in the BODY's axes, kept as three separate
        // things because they answer three different questions:
        //
        //   raw      the head since the recentre, which is what the eyes are
        //            rendered from - unchanged from the day it was written.
        //   neutral  where the player was standing when the body was calibrated.
        //            The recentre can happen with the headset on a desk; without
        //            this, standing up would read as a half-metre crouch and walk
        //            the avatar across the room.
        //   shift    what the avatar has already walked off. Subtracted from the
        //            anchor, so a physical step that the game then walks out is
        //            not added twice - see VrRoomscale.
        //
        // RoomOffset is raw - neutral - shift: the player relative to their own
        // body, which is what the movement input and the body IK both act on.
        private static Vec3 _roomShift;
        private static Vec3 _roomNeutral;
        private static bool _neutralCaptured;
        private static float _stillTime;

        private static Vec3 _roomRaw;
        private static bool _haveRoomRaw;
        private static Vec3 _roomVelocity;
        private static long _roomStamp;

        /// <summary>The player's body-frame offset from their own avatar, in
        /// metres: how far ahead, to the side, or crouched below it they are.
        /// Body axes are the entitial ones - X right, Y forward, Z up - so this
        /// feeds VrRoomscale and VrBodyIk without a conversion.</summary>
        public static Vec3 RoomOffset { get; private set; }

        /// <summary>The raw head velocity in body axes, metres per second,
        /// smoothed. The feed-forward term in VrRoomscale: the player's own
        /// walking speed, not the residual after the avatar catches up.</summary>
        public static Vec3 RoomVelocity => _roomVelocity;

        /// <summary>The room/body frame's rotation in the world - the agent's
        /// heading. RoomOffset's components are along THIS frame's axes, so
        /// anything converting a world vector into room space (the consumption,
        /// VrRoomscale) needs this rather than the anchor's rotation, which also
        /// carries the recentre's own yaw.</summary>
        public static Mat3 RoomRotation { get; private set; } = Mat3.Identity;

        /// <summary>Whether the standing neutral has been measured this mission.
        /// Until it has, RoomOffset is uncalibrated - the recentre may have
        /// happened with the headset on a desk - so nothing should act on it.</summary>
        public static bool RoomCalibrated => _neutralCaptured;

        /// <summary>Records that the avatar has walked off <paramref name="bodyLocal"/>
        /// metres of the player's offset. Called by VrRoomscale as the agent moves;
        /// the anchor shifts by the same amount so the world stays where the player
        /// left it.</summary>
        public static void ConsumeRoomOffset(Vec3 bodyLocal)
        {
            _roomShift = _roomShift + bodyLocal;

            // TEST 297: the offset reads the consumption at once. The mission tick
            // can run twice per rendered frame, and an instant step reading the
            // last frame's offset would take the same step again.
            RoomOffset = RoomOffset - bodyLocal;
        }

        private static MissionScreen _boundScreen;
        private static bool _loggedFirstPose;
        private static bool _loggedRawPose;
        private static bool _unusablePoseLogged;

        /// <summary>True when the cameras below hold a real, current head pose.</summary>
        public static bool HasPose { get; private set; }

        /// <summary>Head (eye midpoint) in world space. Phase 5 aims the UI quad
        /// with this; the audio listener will want it too.</summary>
        public static MatrixFrame HeadFrame { get; private set; }

        /// <summary>TEST 325: the main agent's position when this frame's anchor
        /// was resolved - before the mission tick moves it. The body is written
        /// after that tick, so the difference is how far the body leads the view.
        /// </summary>
        public static Vec3 AnchorAgentPosition { get; private set; }

        /// <summary>
        /// TEST 391. The view is placed here, BEFORE the mission tick; the body is
        /// drawn after it, one tick's travel further on (3-4.5 cm at 2-3 m/s). So
        /// walking with the stick the whole body sat that far ahead of the eyes -
        /// "the chest gets pushed by left stick movement, backward too, normal when
        /// stopping" - and the arms reached back for hands a tick behind. Walking on
        /// foot under the head-driven body, the anchor goes ahead by the travel of
        /// the last tick, where the agent will be when the body is drawn. Standing,
        /// room scale's own steps (TEST 297) are left as they are.
        ///
        /// TEST 410: the last tick's travel is stretched to THIS tick's dt - with
        /// uneven frames (70 fps on a 120 Hz PSVR2) the old lead was off by speed x
        /// the dt change every frame, ~2.5 cm running and ~8 cm galloping. And it
        /// leads mounted too: the saddle camera is the game eye captured before the
        /// tick, the rider's head is drawn at the animated eye after it, so at a
        /// gallop the body sat a tick of horse travel (14-17 cm) ahead of the view
        /// - "only when running with the horse". Mounted needs no Moving gate (a
        /// horse standing still travels nothing) but must have been mounted last
        /// tick too, so the mount itself is not led.
        /// </summary>
        private static Vec3 WalkLead(Agent agent)
        {
            Mission mission = Mission.Current;
            if (agent == null || !agent.IsActive() || mission == null)
            {
                _haveLeadPos = false;
                _leadOn = false;
                return Lead = Vec3.Zero;
            }

            // Once per tick, however often the camera is asked.
            if (_haveLeadPos && mission.CurrentTime == _leadTime)
                return Lead;

            Vec3 pos = agent.Position;
            Vec3 tick = _haveLeadPos ? pos - _leadPos : Vec3.Zero;
            bool mounted = agent.HasMount;
            bool wasMounted = _leadMounted;
            _leadPos = pos;
            _leadTime = mission.CurrentTime;
            _leadMounted = mounted;
            _haveLeadPos = true;

            bool on = WalkLeadOn && VrBodyIk.HeadDrives
                      && (mounted
                          ? MountedLeadOn && wasMounted && tick.LengthSquared < 0.6f * 0.6f
                          : VrHeadBody.Moving(agent) && tick.LengthSquared < 0.15f * 0.15f);
            // TEST 446: the last tick's travel as it is. TEST 410 stretched it by
            // FrameDt / the previous FrameDt, but OnFrameTick runs after the tick and
            // its dt is that tick's, not the coming one's - the stretch was a frame
            // late and doubled every hitch (TEST 445). FinishView corrects the guess
            // exactly before the render; this is only the starting point.
            Lead = on ? tick : Vec3.Zero;
            _leadOn = on;
            return Lead;
        }

        /// <summary>TEST 410: MissionScreen.OnFrameTick's dt, set by CameraPatch - the dt
        /// of the tick that has just run (TEST 446).</summary>
        internal static float FrameDt;

        /// <summary>TEST 410: this tick's lead (zero when off), for VrHeadBody's probe.</summary>
        public static Vec3 Lead { get; private set; }

        private static readonly bool WalkLeadOn = VrConfig.Bool("camera_walk_lead", true);
        private static readonly bool MountedLeadOn = VrConfig.Bool("camera_mounted_lead", true);
        private static Vec3 _leadPos;
        private static float _leadTime;
        private static bool _haveLeadPos, _leadMounted, _leadOn;

        /// <summary>
        /// TEST 446 (replaces 440 and 444). WHERE A FRAME'S CAMERA COMES FROM.
        /// MissionState.TickMission, decompiled, runs every frame as: Mission.Tick
        /// (native: the agents move) -> OnPreDisplayMissionTick -> UpdateCamera ->
        /// MBWindowManager.PreDisplay (the render starts) -> OnMissionTick ->
        /// AfterMissionTick. MissionScreen.OnFrameTick, where Update builds the
        /// anchor, comes after all of that. So the camera a frame is rendered with was
        /// built from the PREVIOUS frame's position, a whole tick before the agents
        /// moved, and the lead had to guess the coming tick from the last one. Its dt
        /// ratio was a frame late (OnFrameTick's dt belongs to the tick just done,
        /// not the coming one), so every uneven frame mis-led it, and around a hitch
        /// - the engine runs a long tick, then a short catch-up one - by 0.3-0.7 m
        /// (TEST 445): the camel's "camera goes back, body goes forward", and the
        /// body's shake "only with full body IK" (the lead is the IK body's alone).
        ///
        /// Here, at UpdateCamera's tail, the agents have moved and the render has
        /// not started: the view moves by exactly what the guess missed, before
        /// AimEngineCamera puts the engine camera on the eye, and the head target
        /// and the hands posed after it (AfterMissionTick) use the same anchor.
        /// view_finish = 0 leaves the guess alone.
        /// </summary>
        internal static void FinishView()
        {
            if (!_finishArmed)
                return;
            _finishArmed = false;

            Vec3 d = Vec3.Zero;
            Agent agent = Mission.Current?.MainAgent;
            if (FinishOn && _leadOn && AnchorAgentValid && agent != null && agent.IsActive())
            {
                Vec3 miss = agent.Position - AnchorAgentPosition - Lead;
                if (agent == _finishSaddleRider && agent.MountAgent == _finishSaddleMount
                    && VrHeadBody.TryCamelSaddleEye(agent, out Vec3 saddleEye))
                {
                    // Agent travel misses the camel gait's movement of the rider within the saddle.
                    Vec3 gait = SaddleAnimationTravel(_finishSaddleEye, saddleEye,
                        agent.Position - AnchorAgentPosition);
                    miss += gait;
                    _finishSaddleMax = Math.Max(_finishSaddleMax, gait.Length);
                }
                if (miss.LengthSquared > 1f)
                {
                    _finishRefused++;   // a teleport or a mount, not a tick's travel
                }
                else
                {
                    d = miss;
                    _finishSq += d.LengthSquared;
                    _finishMax = Math.Max(_finishMax, d.Length);
                    _finishN++;
                }
            }

            // TEST 514: the view's height on stairs, after the miss so it smooths the
            // height actually rendered.
            StairShift = StairDz(agent, Anchor.origin.z + d.z);
            d.z += StairShift;

            ViewFinish = d;
            if (d.LengthSquared > 0f)
            {
                MatrixFrame a = Anchor;
                a.origin += d;
                Anchor = a;
                MatrixFrame h = HeadFrame;
                h.origin += d;
                HeadFrame = h;
                EyeWorld[0].origin += d;
                EyeWorld[1].origin += d;
            }

            if (++_finishTick >= 450)
            {
                if (_finishN > 0 || _finishRefused > 0)
                    VrLog.Info(string.Format(
                        "TEST 446 view finished at UpdateCamera: the lead's guess was off by {0:F4} m RMS "
                        + "(largest {1:F3} m) over {2} moving frame(s) and the view was moved by exactly that "
                        + "before the render; {3} refused as a teleport.",
                        _finishN > 0 ? Math.Sqrt(_finishSq / _finishN) : 0.0, _finishMax, _finishN,
                        _finishRefused));
                if (_finishSaddleMax > 0.001f)
                    VrLog.Info(string.Format("Camel camera: followed up to {0:F3} m of saddle animation "
                        + "beyond agent travel before rendering.", _finishSaddleMax));
                _finishTick = _finishN = _finishRefused = 0;
                _finishSq = 0.0;
                _finishMax = 0f;
                _finishSaddleMax = 0f;
            }
        }

        internal static Vec3 SaddleAnimationTravel(Vec3 previousEye, Vec3 currentEye, Vec3 agentTravel)
            => currentEye - previousEye - agentTravel;

        internal static bool CamelSaddleActive(Agent agent) =>
            _anchorKey == 3 && agent != null && agent == _finishSaddleRider
            && agent.MountAgent == _finishSaddleMount;

        /// <summary>
        /// TEST 521 (replaces 514's spring): on foot, the view's HEIGHT is a straight
        /// line fitted to the agent's height against the distance walked over the
        /// last view_stairs_m - a staircase reads as the ramp through its steps. The
        /// engine lifts the agent up stairs in jumps (514: up to 0.75 m in a frame);
        /// 514 put the height on a spring in TIME, which trails a climb by its rate:
        /// the 11:58 castle run had the view (and, after 518, the body) up to 0.44 m
        /// off the agent - "stairs break the head camera". A fit over distance has no
        /// lag on a slope and is off by at most about half a step on stairs (never
        /// past StairMaxM). Level ground and slopes are exact. Standing still it eases
        /// back onto the agent over StairBlendS. Mounted, using a machine, airborne, on a ladder, a
        /// hitch or a teleport: exact, from there. Returns the shift to add to the
        /// view's z.
        /// </summary>
        private static float StairDz(Agent agent, float rawZ)
        {
            long now = Stopwatch.GetTimestamp();
            float dt = _stairAt == 0L ? 0f : (float)((now - _stairAt) / (double)Stopwatch.Frequency);
            _stairAt = now;

            Vec3 pos = agent != null ? agent.Position : Vec3.Zero;
            float dx = pos.x - _stairPos.x, dy = pos.y - _stairPos.y;
            float step = (float)Math.Sqrt(dx * dx + dy * dy);
            _stairPos = pos;

            // A ship carrying a machine user is not a staircase.
            if (StairWindowM <= 0f || agent == null || !agent.IsActive() || agent.HasMount || agent.IsUsingGameObject || !AnchorAgentValid
                || dt <= 0f || dt > 0.25f || !_haveStair || step > 1f || Math.Abs(rawZ - _stairLastZ) > 1.5f
                || Airborne(agent) || VrLadder.Climbing(agent))
            {
                _stairN = 0;
                _stairWeight = 0f;
                _stairTravel = 0f;
                _haveStair = agent != null;
                _stairLastZ = rawZ;
                return 0f;
            }
            _stairLastZ = rawZ;

            // A sample every StairSampleM of travel, only the last window of it.
            _stairTravel += step;
            if (_stairN == 0 || _stairTravel - _stairPath[_stairN - 1] >= StairSampleM)
            {
                if (_stairN == _stairPath.Length)
                    DropStairs(1);
                _stairPath[_stairN] = _stairTravel;
                _stairHeight[_stairN] = rawZ;
                _stairN++;
            }
            int old = 0;
            while (old < _stairN && _stairPath[old] < _stairTravel - StairWindowM)
                old++;
            if (old > 0)
                DropStairs(old);

            bool fits = _stairN >= 3 && _stairTravel - _stairPath[0] >= StairWindowM * 0.25f;
            bool moving = step > 0.3f * dt;
            _stairWeight = Math.Max(0f, Math.Min(1f, _stairWeight + (moving && fits ? dt : -dt) / StairBlendS));
            if (_stairWeight <= 0f || !fits)
                return 0f;

            float off = FitAt(_stairPath, _stairHeight, _stairN, _stairTravel) - rawZ;
            return _stairWeight * Math.Max(-StairMaxM, Math.Min(StairMaxM, off));
        }

        /// <summary>The least-squares line through (s, z) for the first n samples,
        /// at <paramref name="at"/>. Pure.</summary>
        internal static float FitAt(float[] s, float[] z, int n, float at)
        {
            double ms = 0.0, mz = 0.0;
            for (int i = 0; i < n; i++)
            {
                ms += s[i];
                mz += z[i];
            }
            ms /= n;
            mz /= n;

            double sz = 0.0, ss = 0.0;
            for (int i = 0; i < n; i++)
            {
                double ds = s[i] - ms;
                sz += ds * (z[i] - mz);
                ss += ds * ds;
            }
            return (float)(mz + (ss > 1e-8 ? sz / ss : 0.0) * (at - ms));
        }

        private static void DropStairs(int k)
        {
            Array.Copy(_stairPath, k, _stairPath, 0, _stairN - k);
            Array.Copy(_stairHeight, k, _stairHeight, 0, _stairN - k);
            _stairN -= k;
        }

        /// <summary>Jumping or falling (Agent.ActionCodeType Fall .. JumpEndHard).</summary>
        private static bool Airborne(Agent agent)
        {
            int t = (int)agent.GetCurrentActionType(0);
            return t >= (int)Agent.ActionCodeType.Fall && t <= (int)Agent.ActionCodeType.JumpEndHard;
        }

        /// <summary>TEST 514b: the height StairDz added to this frame's view (0 until
        /// FinishView runs). TEST 518: the head-driven body rides it too, whole.</summary>
        internal static float StairShift { get; private set; }

        private static readonly float StairWindowM = Math.Max(0f, VrConfig.Float("view_stairs_m", 0.8f));
        private const float StairBlendS = 0.2f, StairMaxM = 0.25f, StairSampleM = 0.01f;
        private static long _stairAt;
        private static Vec3 _stairPos;
        private static float _stairLastZ, _stairTravel, _stairWeight;
        private static int _stairN;
        private static readonly float[] _stairPath = new float[128], _stairHeight = new float[128];
        private static bool _haveStair;

        /// <summary>TEST 446: this frame's finish (zero when none), for VrHeadBody's
        /// probe and the re-publish in VrAfrRenderer.AimEngineCamera.</summary>
        public static Vec3 ViewFinish { get; private set; }

        private static readonly bool FinishOn = VrConfig.Bool("view_finish", true);
        private static bool _finishArmed;
        private static int _finishTick, _finishN, _finishRefused;
        private static double _finishSq;
        private static float _finishMax;
        private static Agent _finishSaddleRider, _finishSaddleMount;
        private static Vec3 _finishSaddleEye;
        private static float _finishSaddleMax;
        public static bool AnchorAgentValid { get; private set; }

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

        /// <summary>The near and far planes the eye cameras are built with. DR
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
                SymmetricHalfAngles(fov, out float hHalf, out float _);
                SymmetricTangents(fov, out float tl, out float tr, out float tu, out float td);

                hfov = hHalf * 2f;
                aspect = (tr - tl) / (tu - td);
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
            // while vp_patch was off. Switched on under DR it would render the
            // canted frustum while submitted_fov submits the containing
            // symmetric one, which is the render-versus-submit mismatch this
            // project has already been bitten by three times.
            //
            // It would also break the warp outright. DR is a pure horizontal
            // shift only because both eyes share one frustum; give the rendered
            // eye a canted one and the synthesised eye inherits a frustum that
            // is not its own, mirrored the wrong way.
            //
            // EyeProjectionTarget has honoured SymmetricFov all along. This is
            // the same decision, applied to the other consumer of it.
            if (SymmetricFov)
            {
                // Same guard band as the render, for the same reason: this is the
                // frustum handed to whoever asks what the eye was drawn with, and
                // an answer that disagrees with rgl by three degrees is the
                // disparity error EyeFocalPx warns about.
                float h = Math.Max(Math.Abs(left), Math.Abs(right)) + GuardBand;
                float v = Math.Max(Math.Abs(up), Math.Abs(down));

                left = -h;
                right = h;
                if (!TrueVertical)
                {
                    up = v;
                    down = -v;
                }
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
                // it is what the focal length has to describe - GUARD BAND AND
                // ALL. The band widens the rendered span, so a focal length taken
                // without it says the image is narrower than it is and every
                // disparity computed from it comes out too large.
                double half = Math.Max(left, right) + GuardBand;
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

            if (SymmetricFov && TrueVertical)
            {
                SymmetricTangents(fov, out float tl, out float tr, out float tu, out float td);
                cam.SetViewVolume(true, tl * NearPlane, tr * NearPlane, td * NearPlane, tu * NearPlane,
                                  NearPlane, _lastFar);
                LogProjectionOnce($"Shared AFR camera: symmetric across, TRUE VERTICAL (fov_true_vertical) " +
                           $"tan L{tl:F3} R{tr:F3} U{tu:F3} D{td:F3}, far {_lastFar:F1}.");
            }
            else if (SymmetricFov)
            {
                SymmetricHalfAngles(fov, out float hHalf, out float vHalf);
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
        public static void RequestRecentre(bool measureHeight = false, bool keepHeight = false)
        {
            _recentreRequested = true;
            _manualRecentre = true;
            _measureHeight |= measureHeight;
            _keepHeight = keepHeight && !measureHeight;

            VrLog.Info("Recentre requested: the view will move to the character and the "
                       + "physical offset will start again from zero.");

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

            // TAKING CONTROL RECENTRES ONCE.
            //
            // The session's automatic recentre happens with the main menu up - at
            // a desk, in a chair, wherever the headset was when the game started.
            // Everything after it is measured from that pose, so a player who then
            // stands up and walks to the middle of their room arrives in the battle
            // physically offset from their character, with nothing to say so. One
            // recentre at the moment the mission becomes theirs puts the character
            // under them and starts the offset from there.
            if (VrMissionPhase.VrDrivesLook)
            {
                if (!_wasDrivingLook)
                {
                    _wasDrivingLook = true;

                    if (Mission.Current?.MainAgent != null)
                    {
                        _recentreRequested = true;
                        _manualRecentre = true;
                        _keepHeight = true;     // TEST 389: automatic - the F10 height stands
                        _settleAt = SaddleSettleS > 0f
                            ? Stopwatch.GetTimestamp() + (long)(SaddleSettleS * Stopwatch.Frequency)
                            : 0L;
                        VrLog.Info("Taking control of the mission: recentring once, so the "
                                   + "character starts under you rather than wherever the "
                                   + "game was launched from.");
                    }
                }
            }
            else
            {
                _wasDrivingLook = false;
            }

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

            Agent anchorAgent = Mission.Current?.MainAgent;
            AnchorAgentValid = anchorAgent != null && anchorAgent.IsActive();
            if (AnchorAgentValid)
                AnchorAgentPosition = anchorAgent.Position;
            _finishSaddleRider = _finishSaddleMount = null;
            if (AnchorAgentValid && UseAgentAnchor && VrMissionPhase.VrDrivesLook && _anchorKey == 3
                && VrHeadBody.TryCamelSaddleEye(anchorAgent, out _finishSaddleEye))
            {
                _finishSaddleRider = anchorAgent;
                _finishSaddleMount = anchorAgent.MountAgent;
            }

            // TEST 326: a crouch we started lowered the eye the anchor is built
            // from; the headset already carries that drop, so give it back.
            if (AnchorAgentValid)
                body.origin.z += VrCrouch.Lift(anchorAgent, body.origin.z - anchorAgent.Position.z);

            // TEST 391: walking, ahead by one tick's travel - see WalkLead.
            body.origin += WalkLead(anchorAgent);

            // ---- head pose in stage space ----------------------------------
            // Midpoint of the two eye positions. Orientation is taken from the
            // left eye: OpenXR runtimes report both eyes with the same
            // orientation in every case that matters, and averaging quaternions
            // properly is more machinery than the difference justifies.
            MatrixFrame headStage = VrMath.ToBannerlord(
                Views[0].Orientation,
                Scale(VrMath.Midpoint(Views[0].Position, Views[1].Position)));

            if (_heightMeasureRequested)
            {
                _heightMeasureRequested = false;
                VrBodyHeight.Measure(headStage, "Measure my height");
            }

            if (_settleAt != 0L && Stopwatch.GetTimestamp() >= _settleAt)
            {
                _settleAt = 0L;
                if (Mission.Current?.MainAgent?.HasMount == true)
                {
                    _recentreRequested = _manualRecentre = _keepHeight = _keepYaw = true;
                    VrHeadBody.Recapture();
                    VrLog.Info("TEST 661: mounted spawn settled - recentred once more (position and saddle "
                               + "offset; the yaw stays the spawn's). mounted_spawn_settle_s = 0 turns it off.");
                }
            }

            if (_recentreRequested)
            {
                _recentreRequested = false;

                // TEST 389: an automatic recentre (taking control, a dismount) keeps
                // the height of the last F10 / panel recentre - a dismount while
                // looking down set the standing height low and the body could not
                // reach the head standing straight ("head missed by 0.093 m").
                bool keep = _keepHeight && _haveHeightZ;
                _recentre = ComputeRecentre(headStage, keep ? _heightZ : (float?)null,
                                            _keepYaw ? _recentre.rotation : (Mat3?)null);
                _keepHeight = _keepYaw = false;

                if (_measureHeight)
                {
                    _measureHeight = false;
                    _heightZ = LevelEye(headStage).z;
                    _haveHeightZ = true;
                    // TEST 640: the character's size is manual only (the panel's
                    // Measure button or slider) - a recentre no longer measures.
                    VrHeadBody.Recapture();     // TEST 389: "this is my stance"
                }

                // THE ROOM STARTS AGAIN HERE, AND ALL OF IT IN ONE PLACE.
                //
                // With the recentre applied above, raw = recentre * headStage is
                // zero by construction, so this pose IS the player's zero: the
                // consumed shift, the standing neutral and the head's velocity all
                // start from nothing, and the body's smoothing is dropped so it
                // snaps to the neutral instead of gliding there from wherever it
                // was. Doing this at the request instead of at the application left
                // one frame where the shift was zero and raw was not - a body that
                // jumped, every time the key was pressed.
                _roomShift = Vec3.Zero;
                _roomVelocity = Vec3.Zero;
                _haveRoomRaw = false;

                if (_manualRecentre)
                {
                    // "This is my body now": the neutral is the pose the key was
                    // pressed in, taken immediately rather than waiting half a
                    // second for the player to hold still.
                    _roomNeutral = Vec3.Zero;
                    _neutralCaptured = true;
                }
                else
                {
                    // The automatic first pose can happen with the headset on a
                    // desk; the neutral waits for a still, on-foot frame so the act
                    // of standing up does not read as a crouch.
                    _roomNeutral = Vec3.Zero;
                    _neutralCaptured = false;
                }

                _stillTime = 0f;
                _manualRecentre = false;

                // A held attack anchor is a snapshot from before the recentre and
                // would put the view where the body is not - which is exactly
                // "I recentred and I am behind my own body" during a fight.
                _anchorHeld = false;
                _anchorHoldStill = false;

                VrBodyIk.ForgetOffset();
                VrHeadAim.SnapBody();   // TEST 291d

                Agent recentred = Mission.Current?.MainAgent;
                float aboveFeet = recentred != null ? body.origin.z - recentred.Position.z : 0f;
                float fromEye = recentred != null
                    ? (body.origin - VrEyePoint.Resolve(recentred)).Length
                    : 0f;

                VrLog.Info(string.Format(
                    "Recentred. The view sits {0:F2} m above the character's feet and {1:F2} m "
                    + "from the eye-height point. {2} The physical offset starts from zero "
                    + "here, so walking off and back is measured from this spot.",
                    aboveFeet, fromEye,
                    VrBodyIk.Drives
                        ? "The full-body IK drives from that point, so the second number "
                          + "should be ~0."
                        : "With the full-body IK off, the second number is simply how far "
                          + "the game's own animated head sits from that point."));
            }

            // Published for VrEngineAim, which steers the ENGINE's camera - and so
            // its culling - toward where the head is looking. Read from the next
            // frame's UpdateCamera prefix, which runs ahead of this postfix; one
            // frame of lag is irrelevant for deciding what to submit, and the
            // rendered view never goes through here at all.
            PublishHeadAngles(headStage);

            MatrixFrame anchor = body.TransformToParent(_recentre);

            // TEST 293: the VR tab's head offset, along the body's heading.
            // TEST 298/307: plus head_offset_mounted_forward_m while riding; TEST 442:
            // the saddle's own height instead of the on-foot one.
            // TEST 449: Saddled - the saddle's offsets hold until the dismount is done.
            bool riding = VrHeadBody.Saddled(Mission.Current?.MainAgent);
            float headUp = riding ? HeadOffsetMountedUp : HeadOffsetUp;
            float headForward = HeadOffsetForward + (riding ? MountedForward : 0f);
            HeadOffsetWorld = body.rotation.TransformToParent(new Vec3(0f, headForward, headUp, -1f));
            if (headUp != 0f || headForward != 0f)
                anchor.origin += HeadOffsetWorld;

            // TEST 449: where the anchor comes from changing (the saddle's animated
            // eye <-> the standing body's eye, on a mount or a dismount's hand-over)
            // glides over anchor_seam_s instead of jumping the view.
            anchor.origin += AnchorSeam(anchor.origin);

            // Room scale, before the pair is latched: the consumed shift moves the
            // anchor the world over so a physical step the avatar has walked out is
            // not counted twice, and both eyes of the pair must be drawn from the
            // one room origin this produces. See UpdateRoomScale.
            UpdateRoomScale(body, headStage, ref anchor);

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
                    if (SymmetricFov && TrueVertical)
                    {
                        SymmetricTangents(Views[eye].Fov, out float tl, out float tr, out float tu, out float td);
                        cam.SetViewVolume(true, tl * NearPlane, tr * NearPlane, td * NearPlane, tu * NearPlane,
                                          NearPlane, far);
                        RecordSet(eye, (float)(2.0 * Math.Atan(tr)), (tr - tl) / (tu - td), far);
                        VrLog.Info($"Eye {eye} symmetric-across projection, TRUE VERTICAL: tan L{tl:F3} " +
                                   $"R{tr:F3} U{tu:F3} D{td:F3}, far {far:F1}.");
                    }
                    else if (SymmetricFov)
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

                        SymmetricHalfAngles(fov, out float hHalf, out float vHalf);

                        float aspect = (float)(Math.Tan(hHalf) / Math.Tan(vHalf));

                        cam.SetFovVertical(vHalf * 2f, aspect, NearPlane, far);
                        RecordSet(eye, hHalf * 2f, aspect, far);
                        VrLog.Info($"Eye {eye} SYMMETRIC projection: vfov {(vHalf * 2f):F3} rad, " +
                                   $"hfov {(hHalf * 2f):F3} rad, aspect {aspect:F3}, far {far:F1}" +
                                   (GuardBand > 0f
                                        ? $", including {(GuardBand * 57.29578f):F1} deg of guard "
                                          + "band per side for the warp to read at the edges "
                                          + "(stereo_guard_deg)."
                                        : "."));
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
            StairShift = 0f;    // TEST 514b: a fresh, unfinished view
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
            // TEST 446: the next UpdateCamera may now finish this view.
            _finishArmed = true;

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

        /// <summary>Table height above the floor, metres (stage space is floor-based).</summary>
        private static readonly float TableHeight = VrConfig.Float("map_table_height", 0.75f);

        /// <summary>How far in front of the head the table centre is placed, metres.</summary>
        private static readonly float TableDistance = VrConfig.Float("map_table_distance", 0.45f);

        /// <summary>The table in stage space: centre on its surface, heading.</summary>
        private static MatrixFrame _table;
        private static bool _haveTable;

        /// <summary>Where the table centre is in the map, and which way it faces.</summary>
        private static MatrixFrame _mapTable;

        /// <summary>The head's height above the table when it was placed, metres.
        /// The standing eye, not the live one, so leaning in stays free (TEST 399).</summary>
        internal static float MapEyeAboveTable { get; private set; }

        /// <summary>
        /// The campaign map's version of <see cref="Update"/>: the eye frames only.
        ///
        /// TABLETOP. The map is a model on a table in the room. <paramref name="mapTable"/>
        /// is the map point that sits on the table's centre (the game camera's focus)
        /// with the game camera's heading; <paramref name="unitsPerMetre"/> is the
        /// model's scale. Every stage pose goes table-local, gets scaled, and lands
        /// in the map - so walking round the table, leaning in and crouching all do
        /// exactly what they would with a real model. No agent, no pair latch.
        /// </summary>
        public static bool UpdateMap(MatrixFrame mapTable, float unitsPerMetre, float far, bool recentre)
        {
            if (!VrSystem.IsActive || !VrSystem.IsStereoReady)
                return false;

            if (NativeMethods.bvr_get_predicted_views(Views, out long displayTime) != (int)BvrStatus.Ok)
                return false;

            if (!PoseIsUsable(displayTime))
            {
                ReportUnusablePose(displayTime);
                return false;
            }

            MatrixFrame headStage = VrMath.ToBannerlord(
                Views[0].Orientation,
                VrMath.Midpoint(Views[0].Position, Views[1].Position));

            // The table goes where you are looking, at table height - on entering
            // the map and on every recentre.
            if (recentre || _recentreRequested || !_haveTable)
            {
                _recentreRequested = false;
                _manualRecentre = false;
                _measureHeight = false;   // TEST 356: the map is no place to measure a stance

                Mat3 heading = VrMath.YawOnly(headStage.rotation);
                Vec3 centre = headStage.origin + heading.f * TableDistance;
                centre.z = TableHeight;

                _table = default;
                _table.rotation = heading;
                _table.origin = centre;
                _haveTable = true;

                // TEST 399: the standing eye the zoom floor keeps above the ground.
                MapEyeAboveTable = headStage.origin.z - TableHeight;

                VrLog.Info($"Map table placed {TableDistance:F2} m ahead, {TableHeight:F2} m "
                           + $"above the floor; head at {headStage.origin.z:F2} m.");
            }

            MapScale = unitsPerMetre;
            _mapTable = mapTable;
            _havePairLatch = false;

            MatrixFrame anchor = MapStageToWorld(MatrixFrame.Identity);
            Anchor = anchor;
            HeadStage = headStage;

            // A FIXED anchor for native, not the table's. Native moves a held AFR
            // eye by how far the anchor moved, in metres = world / scale - and the
            // table's scale follows the game's zoom smoothing every frame, so the
            // world-to-metres of a point ~700 units out jumps by centimetres with
            // no real motion at all. That pulled the eyes apart (TEST 275:
            // "eyes not aligned"). The table's own motion between two eyes is a
            // few millimetres; leaving it uncorrected is the smaller error.
            PublishBodyYaw(MatrixFrame.Identity, MatrixFrame.Identity);
            _lastFar = far;

            for (int eye = 0; eye < 2; eye++)
                EyeWorld[eye] = MapStageToWorld(
                    VrMath.ToBannerlord(Views[eye].Orientation, Views[eye].Position));

            HaveEyeFrames = true;
            HeadFrame = MapStageToWorld(headStage);
            DisplayTime = displayTime;
            HasPose = true;

            int rc = NativeMethods.bvr_set_used_views(Views, displayTime);
            if (rc != (int)BvrStatus.Ok && rc != (int)BvrStatus.SessionNotReady)
                VrLog.Warn("bvr_set_used_views returned " + rc);

            return true;
        }

        /// <summary>
        /// The head and one eye from the latest located views, in stage METRES and
        /// Bannerlord axes, plus the head as OpenXR raw values for native. The head
        /// is what the per-eye interface quad hangs off (native places it
        /// ui_overlay_distance ahead of this pose); the eye is what a widget is
        /// projected from. See VrUiProjection.
        /// </summary>
        internal static bool UiEyeGeometry(int eye, out MatrixFrame head, out Vec3 eyePos,
                                           out BvrVec3 headXr, out BvrQuat headQ)
        {
            headXr = VrMath.Midpoint(Views[0].Position, Views[1].Position);
            headQ = Views[0].Orientation;
            head = VrMath.ToBannerlord(headQ, headXr);
            eyePos = VrMath.ToBannerlord(Views[eye].Position);
            return HaveEyeFrames && eye >= 0 && eye <= 1;
        }

        /// <summary>A stage frame in metres into the world, the way the eyes go:
        /// the map table when it is up, the mission anchor at world scale otherwise.</summary>
        internal static MatrixFrame StageMetresToWorld(MatrixFrame stage)
        {
            if (MapScale > 0f)
                return MapStageToWorld(stage);

            stage.origin = stage.origin * WorldScale;
            return Anchor.TransformToParent(stage);
        }

        /// <summary>A stick push (x right, y forward, as the player faces) in the
        /// table's axes (x right, y forward along the table).</summary>
        public static Vec2 MapStickToTable(float x, float y)
        {
            Mat3 head = VrMath.YawOnly(HeadStage.rotation);
            Vec3 f = _table.rotation.TransformToLocal(head.f);
            Vec3 r = _table.rotation.TransformToLocal(head.s);
            return new Vec2(r.x * x + f.x * y, r.y * x + f.y * y);
        }

        /// <summary>A stage pose (metres, Bannerlord axes) on the map table, in the
        /// map: table-local, scaled by <see cref="MapScale"/>, placed on the map.</summary>
        public static MatrixFrame MapStageToWorld(MatrixFrame stage)
        {
            MatrixFrame local = _table.TransformToLocal(stage);
            local.origin = local.origin * MapScale;
            return _mapTable.TransformToParent(local);
        }

        /// <summary>Drops the eye cameras. Called when the mission changes and on
        /// shutdown; the engine hands out real native cameras, so they are
        /// released rather than abandoned.</summary>
        public static void Reset()
        {
            _finishArmed = false;
            _finishSaddleRider = _finishSaddleMount = null;
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

            // TEST 249/251: the held anchor belongs to one fight.
            _anchorHeld = false;
            _anchorHoldStill = false;
            _anchorHoldOrigin = Vec3.Zero;
            _anchorHoldStable = Vec3.Zero;
            _anchorHoldSwing = 0L;
            _holdStamp = 0L;
            _motionSwing = 0L;

            // Room scale starts again with the mission: the neutral is where the
            // player is standing when this battle begins.
            _roomShift = Vec3.Zero;
            _roomNeutral = Vec3.Zero;
            _neutralCaptured = false;
            _stillTime = 0f;
            _roomRaw = Vec3.Zero;
            _haveRoomRaw = false;
            _roomVelocity = Vec3.Zero;
            _roomStamp = 0L;
            RoomOffset = Vec3.Zero;

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
            VrBow.Forget();
            VrBodyHide.Reset();
            VrWeaponEntity.Reset();
            VrWeaponAttach.Reset();
            VrHandCalibration.Reset();
            VrBlade.Reset();
            VrBodyIk.Reset();
            VrRoomscale.Reset();
            VrCrouch.Reset();
            _anchorSource = AnchorSource.None;
            _settleAt = 0L;     // TEST 661: not into the next mission
            _anchorKey = _seamKey = -1;
            _seam = Vec3.Zero;
            _seamStamp = 0L;
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
        private static MatrixFrame ComputeRecentre(MatrixFrame head, float? keepZ = null, Mat3? keepYaw = null)
        {
            // TEST 661: keepYaw - a recentre's own rotation (flat transposed) kept;
            // transposed back it is that recentre's flat.
            Mat3 flat = keepYaw.HasValue ? keepYaw.Value.TransformToLocal(Mat3.Identity) : VrMath.YawOnly(head.rotation);

            MatrixFrame r = default;
            r.rotation = flat.TransformToLocal(Mat3.Identity);   // flat transposed

            // TEST 389: from where the eyes would be with the head LEVEL on the same
            // neck pivot - a recentre taken looking down sat ~0.1 m low.
            Vec3 eye = LevelEye(head);
            if (keepZ.HasValue)
                eye.z = keepZ.Value;

            Vec3 p = flat.TransformToLocal(eye);
            r.origin = new Vec3(-p.x, -p.y, -p.z, -1f);
            return r;
        }

        /// <summary>
        /// TEST 389: where the eyes would be with the head LEVEL - the same yaw,
        /// the same neck pivot. The eyes sit forward of and above the head's pivot
        /// (VrHeadBody.EyeFromPivot: ~0.10 m, ~0.14 m), so looking 45 deg down
        /// drops them ~0.11 m and pushes them ~0.07 m forward; anything taking the
        /// head's height (a recentre, the height measure) takes this instead.
        /// Stage or world space alike: f forward, u up.
        /// </summary>
        internal static Vec3 LevelEye(MatrixFrame head)
        {
            Vec3 o = VrHeadBody.EyeFromPivot;
            Mat3 flat = VrMath.YawOnly(head.rotation);
            Vec3 now = head.rotation.f * o.y + head.rotation.u * o.z;
            Vec3 level = flat.f * o.y + flat.u * o.z;
            return head.origin - now + level;
        }

        /// <summary>
        /// The room-scale state: where the player is and how fast, relative to
        /// their own avatar, in body axes; the standing-pose neutral; and the
        /// shift subtracted from the anchor.
        ///
        /// WHY THE ANCHOR IS SHIFTED AT ALL. The view has always been
        /// body * recentre * headStage, so a physical step moved the eyes and the
        /// hands while the avatar stayed put. VrRoomscale then walks the avatar
        /// after the player (through the game's own movement input). Without this
        /// shift, that would count the step twice: the agent would move one step
        /// and the head offset would move the view another, so the world would
        /// slide past at double speed while the player walked at one.
        ///
        /// The shift is the amount the avatar has walked off, published by
        /// VrRoomscale as it consumes the offset. The neutral is the standing
        /// reference: the recentre can happen with the headset on a desk, and
        /// without a neutral the act of putting it on would read as a crouch.
        /// </summary>
        private static void UpdateRoomScale(MatrixFrame body, MatrixFrame headStage,
                                            ref MatrixFrame anchor)
        {
            Vec3 raw = _recentre.TransformToParent(headStage).origin;

            // Published for anything converting between the world and room space.
            RoomRotation = body.rotation;

            long now = Stopwatch.GetTimestamp();
            float dt = _roomStamp == 0L
                ? 1f / 90f
                : (float)((now - _roomStamp) / (double)Stopwatch.Frequency);
            _roomStamp = now;

            if (dt <= 0f || dt > 0.25f)
                dt = 1f / 90f;

            bool hadRaw = _haveRoomRaw;
            Vec3 previous = _roomRaw;
            _roomRaw = raw;
            _haveRoomRaw = true;

            // The player's physical velocity, from the RAW offset. The neutral and
            // the shift are references rather than motion; differentiating after
            // them would report the avatar's own catch-up as the player reversing.
            Vec3 velocity = Vec3.Zero;

            if (hadRaw)
            {
                velocity = (raw - previous) * (1f / dt);
                if (velocity.Length > 5f)
                    velocity = Vec3.Zero;      // a teleport, not a step
            }

            float alpha = 1f - (float)Math.Exp(-dt / 0.06f);
            _roomVelocity = _roomVelocity + (velocity - _roomVelocity) * alpha;

            // The standing neutral: captured once per mission, and again after a
            // recentre, from a head that has been still for half a second. Nothing
            // is asked of the player - standing still for a moment does it - and
            // F10 redoes it deliberately.
            if (!_neutralCaptured)
            {
                // Still means a HEAD SPEED below 8 cm/s, not a per-frame
                // distance: at 90 Hz a 1 cm frame is nearly a metre a second, and
                // a per-frame test would calibrate while the player is walking.
                float moved = hadRaw ? (raw - previous).Length : 1f;
                _stillTime = moved < 0.08f * dt ? _stillTime + dt : 0f;

                Agent agent = Mission.Current?.MainAgent;
                if (_stillTime > 0.5f && agent != null && !agent.HasMount
                    && VrMissionPhase.VrDrivesLook)
                {
                    _roomNeutral = raw;
                    _neutralCaptured = true;
                    _roomVelocity = Vec3.Zero;

                    VrLog.Info(string.Format(
                        "Room scale: body reference set at ({0:F2}, {1:F2}, {2:F2}) m of "
                        + "head offset. Movement and the body IK measure from here now. "
                        + "Stand roughly still when this happens; F10 recentres and "
                        + "re-runs it.", raw.x, raw.y, raw.z));
                }
            }

            RoomOffset = raw - _roomNeutral - _roomShift;

            if (_roomShift.LengthSquared > 0f)
                anchor.origin = anchor.origin - body.rotation.TransformToParent(_roomShift);
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
        private static readonly bool[] LastTrueVertical = new bool[2];

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
        /// frustum the previous mode wanted. Switching to DR would then appear
        /// to do nothing until the next mission loaded, which is a worse bug
        /// than the one it would be hiding.
        /// </summary>
        private static bool ProjectionChanged(int eye, BvrFov fov, float far)
        {
            bool symmetric = SymmetricFov;
            bool trueVertical = TrueVertical;

            if (!HaveProjection[eye] ||
                LastSymmetric[eye] != symmetric ||
                LastTrueVertical[eye] != trueVertical ||
                Changed(LastFar[eye], far) ||
                Changed(LastFov[eye].AngleLeft, fov.AngleLeft) ||
                Changed(LastFov[eye].AngleRight, fov.AngleRight) ||
                Changed(LastFov[eye].AngleUp, fov.AngleUp) ||
                Changed(LastFov[eye].AngleDown, fov.AngleDown))
            {
                LastFov[eye] = fov;
                LastFar[eye] = far;
                LastSymmetric[eye] = symmetric;
                LastTrueVertical[eye] = trueVertical;
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

        // TEST 249/251: the anchor held through an engine attack animation. See
        // ResolveAnchor.
        private static readonly bool MotionProbe = VrConfig.Bool("melee_motion_probe", true);

        private static long _holdStamp;
        private static bool _loggedHold;

        /// <summary>One-shot: said when the body is drawn and the attack hold is
        /// skipped because of it. See ResolveAnchor.</summary>
        private static bool _loggedVisibleNoHold;

        private static bool _anchorHeld;
        private static bool _anchorHoldStill;
        private static Vec3 _anchorHoldOrigin;
        private static Vec3 _anchorHoldStable;
        private static long _anchorHoldSwing;

        // TEST 251's probe: what a swing does to the body and to the anchor.
        private static long _motionSwing;
        private static float _motionNextMs;
        private static Vec3 _motionAgentAt;
        private static Vec3 _motionAnchorAt;
        private static float _motionYawAt;
        private enum AnchorSource { None, Agent, Camera }

        private static AnchorSource _anchorSource = AnchorSource.None;

        // ---- TEST 449: the anchor seam ------------------------------------------
        // Which branch of ResolveAnchor's agent path built the anchor: 0 the game's
        // camera (the saddle, an attack hold or its melt), 1 the standing body's
        // held eye, 2 the stable formula, 3 the camel's calibrated saddle; -1 no agent.
        private static int _anchorKey = -1, _seamKey = -1;
        private static Vec3 _seam, _seamLast;
        private static long _seamStamp;
        private static readonly float SeamS = Math.Max(0f, VrConfig.Float("anchor_seam_s", 0.3f));

        /// <summary>TEST 449: the glide for this frame's anchor. Measured against
        /// the agent, so the travel of the frame the source changed on is not taken
        /// for a jump.</summary>
        private static Vec3 AnchorSeam(Vec3 origin)
        {
            int key = AnchorAgentValid ? _anchorKey : -1;
            Vec3 rel = AnchorAgentValid ? origin - AnchorAgentPosition : origin;
            bool changed = key >= 0 && _seamKey >= 0 && key != _seamKey;
            if (key < 0)
                _seam = Vec3.Zero;

            long now = Stopwatch.GetTimestamp();
            float dt = _seamStamp == 0L ? 1f / 90f : (float)((now - _seamStamp) / (double)Stopwatch.Frequency);
            _seamStamp = now;
            if (dt <= 0f || dt > 0.25f)
                dt = 1f / 90f;

            Vec3 jump = _seamLast - rel;
            Vec3 seam = Glide(ref _seam, ref _seamLast, rel, changed, dt, SeamS);
            if (changed)
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 449: the anchor's source changed ({0} -> {1}; 0 saddle/game camera, 1 standing body, "
                    + "2 eye formula, 3 camel saddle); the view glides {2:F3} m over {3:F2} s instead of jumping.",
                    _seamKey, key, jump.Length, SeamS));
            _seamKey = key;
            return seam;
        }

        /// <summary>TEST 449: a source change's jump goes into the seam, which then
        /// decays over tau - so the output does not move on the frame the source
        /// changes, then eases onto the new one. Pure; the math tests call it.</summary>
        internal static Vec3 Glide(ref Vec3 seam, ref Vec3 last, Vec3 now, bool changed, float dt, float tau)
        {
            seam *= tau > 1e-4f ? (float)Math.Exp(-dt / tau) : 0f;
            if (changed && tau > 1e-4f)
                seam += last - now;
            last = now;
            return seam;
        }

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
            // DEPLOYMENT ANCHORS TO THE CAMERA, NOT TO THE PLAYER.
            //
            // Everything else that writes the camera already stands down for
            // deployment - CameraAimPatch returns before both ForceFirstPerson
            // and VrEngineAim.Apply, on VrDrivesLook. This was the one write that
            // did not, and it is the one that decides WHERE THE EYES ARE.
            //
            // The agent exists during deployment: it spawns about a second in,
            // mounted, while the player is still flying the free camera. So this
            // switched to the agent mid-phase and pinned the view to the
            // character's head for the rest of it. The log of the run that
            // reported it says both halves out loud:
            //
            //   Anchor -> engine camera (no main agent).     <- correct, briefly
            //   Anchor -> player agent's eyes.               <- 0.7 s later
            //   Game camera 17.54 m from the agent, past the 3.50 m first-person
            //   limit, so the engine built a third-person or spectator camera.
            //
            // Seventeen metres. The engine's free camera was off flying where the
            // stick sent it - and culling, LOD and the audio listener went with it,
            // which is why the two eyes disagreed about which hillside was in
            // front of them - while we rendered from a head that had not moved.
            // The camera was never stuck; the eyes were.
            //
            // With no injection there is nothing for the camera anchor to echo,
            // which is the condition the feedback cut below was written for, so
            // riding the engine camera here is both safe and the only answer that
            // follows the camera the player is actually flying.
            Agent agent = UseAgentAnchor && VrMissionPhase.VrDrivesLook
                ? Mission.Current?.MainAgent
                : null;

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
                //
                // TEST 249: THE VIEW DOES NOT RIDE AN ATTACK ANIMATION.
                //
                // That camera is built off the ANIMATED head bone. That is what
                // makes it right for walking, crouching and a horse's gait - and
                // what makes every vanilla swing lean the whole view, because the
                // engine's attack animation moves the head. Those are animations
                // the player is not watching: the arms and the blade are the
                // player's own, and the engine's swing is state and sound (TEST
                // 248). So while the engine is in a melee attack action the
                // anchor's offset from the STABLE eye point - VrEyePoint's own
                // formula, the agent's position and its eye height, which carries
                // no animation at all - is frozen at what it was when the swing
                // began. Nothing snaps, the anchor still travels with the agent,
                // and only the animation's lean is dropped. When the attack ends
                // the offset eases back, so the return is smooth too.
                //
                // TEST 251: AND IT DOES NOT MOVE AT ALL IF THE PLAYER IS STANDING.
                //
                // TEST 249 held the animation's deviation out but still followed
                // the stable eye point, which moves with agent.Position - and the
                // engine's attack step moves exactly that. Standing still and
                // swinging, the only thing moving the eye then is a step the
                // player did not take, and "the camera moves when I swing" is what
                // that reads as. So:
                //
                //   still   - movement input at rest: the anchor does not move for
                //             the length of the swing, at all.
                //   moving  - walking or running: it follows the player's travel
                //             through the stable point, but never the animation.
                //             Freezing it outright would slide the world under a
                //             running player.
                //
                // Both start from the anchor as it was the instant the swing
                // began, and both ease back to the game's own camera when the
                // action ends. The probe below prints what actually moved, so the
                // next report is numbers.
                Vec3 stable = VrEyePoint.Resolve(agent);

                // THE ATTACK HOLD IS FOR AN INVISIBLE BODY.
                //
                // TEST 249/251 freeze the camera through the engine's attack
                // animation so the view does not ride the lean - which was a pure
                // win when the body was not drawn, because there was nothing to
                // disagree with. With the body drawn the disagreement is the whole
                // complaint: the freeze holds the CAMERA while the body keeps
                // moving with the agent (the attack step, and walking), so the body
                // ends up in front of the player, faster than they are.
                //
                // TEST 266: the hold now applies with the body VISIBLE too, as
                // long as melee_freeze_body is on - VrBodyFreeze then holds the
                // WHOLE skeleton (TEST 252), including the head the engine's own
                // camera is built from, so a held camera and a held body can no
                // longer disagree. That disagreement was the whole reason TEST
                // 259 turned the hold off for a visible body; with the skeleton
                // frozen there is nothing left to disagree with. A visible body
                // with melee_freeze_body = 0 still gets the old behaviour: the
                // camera follows the engine's own (unfrozen, animating) head live.
                bool bodyVisible = !VrBodyHide.Hiding;
                bool attacking = (!bodyVisible || VrBodyFreeze.Enabled) && VrMelee.EngineActing(agent);

                if (bodyVisible && !VrBodyFreeze.Enabled && !_loggedVisibleNoHold
                    && VrMelee.EngineActing(agent))
                {
                    _loggedVisibleNoHold = true;
                    VrLog.Info("The body is drawn and melee_freeze_body is off, so the view keeps "
                               + "following the character through attacks instead of being held: "
                               + "body and camera stay aligned. melee_freeze_body = 1 (the default) "
                               + "holds both.");
                }
                // TEST 267: with the body drawn, the anchor always follows the
                // agent's travel and is never frozen in place. TEST 251's "still"
                // hold assumed an invisible body, where a frozen anchor and a
                // walking body could not disagree; visible, they can, and did - at
                // 16:39:58 the body had moved 0.23 m against the anchor's 0.02 m,
                // while a raised shield (DefendShield) held "still" from
                // MovementInputVector although the agent was plainly walking. This
                // closes that on the paths legs-only's camera-eye-point anchor
                // (VrBodyIk.Drives, above) does not cover: body_anim = full, and a
                // visible body while Drives is false.
                bool still = !bodyVisible && !agent.HasMount
                             && agent.MovementInputVector.LengthSquared < 0.04f;

                long stamp = Stopwatch.GetTimestamp();
                float dt = _holdStamp == 0L
                    ? 0.016f
                    : (float)((stamp - _holdStamp) / (double)Stopwatch.Frequency);
                _holdStamp = stamp;

                if (dt > 0.1f)
                    dt = 0.1f;

                _anchorKey = 0;     // TEST 449: the game's camera, unless a branch below says otherwise

                bool haveGameEye = VrGameCamera.TryGetEyeOrigin(out Vec3 animated);
                if (VrHeadBody.TryCamelSaddleEye(agent, out Vec3 saddleEye))
                {
                    // One calibrated saddle eye; no independent camera lean or head-bone feedback.
                    frame.origin = saddleEye;
                    _anchorHeld = false;
                    _anchorKey = 3;
                }
                else if (!haveGameEye)
                {
                    // No game camera this frame - spectator, death, first frames
                    // of a mission. The stable point is all there is, and there is
                    // nothing to hold against.
                    _anchorHeld = false;
                    frame.origin = stable;
                    _anchorKey = 2;
                }
                // TEST 449: Saddled, not HasMount - through a dismount the view keeps
                // riding the game's animated eye down, as it rides it up on a mount.
                else if (VrBodyIk.Drives && !VrHeadBody.Saddled(agent))
                {
                    // TEST 353: in the saddle the head-driven body follows the
                    // rider's camera as it is today; only on foot does the anchor
                    // switch to the formula.
                    // THE BODY PASS OWNS THE HEAD BONE'S ANCESTORS.
                    //
                    // The engine's own camera is built from the head bone, and the
                    // body IK moves the chest that head hangs off - so anchoring
                    // here would read our own writing back a frame later and add
                    // the player's head offset to itself on top. TEST 249's hold
                    // is a no-op here, since there is no animated deviation left
                    // to hold out.
                    //
                    // TEST 267: a hold started before the body pass took over -
                    // mounting with an attack anchor still easing back, say - would
                    // otherwise glide in from a stale point once Drives drops this
                    // out from under it.
                    _anchorHeld = false;

                    // TEST 267 (re-run): NOT the stable eye-height formula while
                    // legs-only is actually holding a captured pose. That formula
                    // is a pure vertical offset from agent.Position, with no
                    // forward term - a real standing eye sits some way forward of
                    // that, and the first version of this "fix" tried to cancel
                    // the gap by shifting the held PELVIS back to match the
                    // formula instead. That pulled the hip out from over the feet
                    // (which are never held, only the animation's) by the same
                    // amount, and the body looked wrong even on a run where the
                    // capture itself measured perfectly sane. Reading the held
                    // body's own eye point instead means there is nothing to
                    // reconcile: whatever the held torso's eye actually is, that
                    // is where the camera goes, every frame - stable is only the
                    // fallback before the first capture has landed.
                    bool held = VrBodyIk.TryGetHeldEyeWorld(agent, out Vec3 heldEye);
                    animated = held ? heldEye : stable;
                    frame.origin = animated;
                    _anchorKey = held ? 1 : 2;
                }
                else if (attacking)
                {
                    // Where the anchor is now, before this frame's decision: the
                    // held value continued by the player's own travel while moving.
                    Vec3 held = _anchorHeld
                        ? _anchorHoldOrigin
                          + (_anchorHoldStill ? Vec3.Zero : (stable - _anchorHoldStable))
                        : animated;

                    if (!_anchorHeld || _anchorHoldSwing != VrMelee.LastSwingAt)
                    {
                        // A new swing: start from exactly where the anchor is, so
                        // the seam does not move it.
                        _anchorHoldOrigin = held;
                        _anchorHoldStable = stable;
                        _anchorHoldSwing = VrMelee.LastSwingAt;
                        _anchorHeld = true;

                        if (!_loggedHold)
                        {
                            _loggedHold = true;
                            VrLog.Info("TEST 251: the view no longer moves with the engine's attack. "
                                       + "Standing and swinging, the camera anchor does not move at "
                                       + "all for the length of the swing; walking or running, it "
                                       + "follows your own travel but not the animation's lean. The "
                                       + "swing still plays for the AI, the sound and the hit "
                                       + "reaction.");
                        }
                    }
                    else if (still != _anchorHoldStill)
                    {
                        // The player started or stopped moving mid-swing: keep the
                        // anchor where it is and change only what it follows.
                        _anchorHoldOrigin = held;
                        _anchorHoldStable = stable;
                    }

                    _anchorHoldStill = still;
                    frame.origin = _anchorHoldOrigin
                                   + (still ? Vec3.Zero : (stable - _anchorHoldStable));
                }
                else if (_anchorHeld)
                {
                    // The attack is over: melt the held anchor back into the
                    // animated one and hand over once they agree to a millimetre.
                    float k = 1f - (float)Math.Exp(-dt / 0.10f);
                    _anchorHoldOrigin = _anchorHoldOrigin + (animated - _anchorHoldOrigin) * k;
                    _anchorHoldStable = stable;
                    frame.origin = _anchorHoldOrigin;

                    if ((animated - _anchorHoldOrigin).Length < 0.001f)
                        _anchorHeld = false;
                }
                else
                {
                    // Normal: the anchor is exactly the game's own camera - moved,
                    // with a shield up, onto the upright head (TEST 295). TEST 414/415:
                    // in the saddle, less the lean this camera itself carries against
                    // the hips - the rider's head target drops its own (SaddleLean).
                    frame.origin = animated + VrUpperBody.HeadShiftWorld
                                   - (agent.HasMount && VrBodyIk.HeadDrives ? VrHeadBody.CameraLean(agent, animated) : Vec3.Zero);
                }

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

                // TEST 251's measurement, with the frame finished so it can report
                // the anchor's yaw as well as its origin - a view can move by
                // turning, and the probe has to be able to say so.
                ProbeAttackMotion(agent, frame);

                if (_anchorSource != AnchorSource.Agent)
                {
                    _anchorSource = AnchorSource.Agent;
                    VrLog.Info("Anchor -> player agent's eyes.");
                }

                ProbeMountedLag(agent, frame, held: false);

                _lastAnchor = frame;
                _haveAnchor = true;
                return frame;
            }

            _anchorKey = -1;    // TEST 449: no agent, no seam

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
                {
                    // Held, NOT tracking. If this is what a gallop runs on, the
                    // view stands still while the horse does not - which is the
                    // camera sliding backward and re-centring when you stop.
                    ProbeMountedLag(agent, _lastAnchor, held: true);
                    return _lastAnchor;
                }
            }

            MatrixFrame fallback = default;
            fallback.origin = engineFrame.origin;

            // Same feedback cut as the agent path above: the engine camera is the
            // thing we are steering, so its own yaw is the last place to read a
            // body heading from.
            //
            // UNLESS WE ARE NOT STEERING IT, WHICH IS DEPLOYMENT.
            //
            // The cut exists because our own injected yaw comes back round in the
            // engine's look; with no injection there is nothing to come back, and
            // taking the cut anyway costs the player the camera. BodyHeading is
            // integrated from the MOUSE alone, so during deployment it does not
            // follow the free camera at all - rotate that camera with the stick
            // and the anchor would hold its old heading while the world turned
            // underneath it. Reading the engine's yaw is both safe and the only
            // answer that tracks the camera the player is actually flying.
            bool cutFeedback = VrEngineAim.AnchorMustIgnoreLook
                            && VrMissionPhase.VrDrivesLook;

            // A camera looks down -u, so its f is its UP: f's heading is forward
            // only while the camera tilts down, and flips 180 deg once the right
            // stick lifts it past the horizon (TEST 580: "the stick reverses").
            // The side vector is level at any pitch.
            Vec3 side = engineFrame.rotation.s;
            fallback.rotation = cutFeedback
                ? VrEngineAim.BodyHeading()
                : VrMath.HeadingFromForward(new Vec3(-side.y, side.x, 0f, -1f));

            if (_anchorSource != AnchorSource.Camera)
            {
                _anchorSource = AnchorSource.Camera;
                _jumpHave = false;
                _easeHave = false;
                VrLog.Info(UseAgentAnchor
                    ? "Anchor -> engine camera (no main agent)."
                    : "Anchor -> engine camera (anchor = camera in config).");
            }

            fallback = EaseDeployment(fallback, out float rawStep);
            ProbeCameraJump(fallback, rawStep);
            ProbeMountedLag(agent, fallback, held: false);

            _lastAnchor = fallback;
            _haveAnchor = true;
            return fallback;
        }

        /// <summary>
        /// How many frames the echo guard discarded the fresh anchor on and held
        /// the previous one. Counted rather than logged: the explanation below is
        /// worth saying once, the RATE is worth knowing every time.
        /// </summary>
        private static int _echoHolds;

        private static Vec3 _lagAgent;
        private static Vec3 _lagAnchor;
        private static bool _lagHave;
        private static float _lagAgentTravel;
        private static float _lagAnchorTravel;
        private static int _lagFrames;
        private static int _lagHeld;

        /// <summary>
        /// MOUNTED CAMERA LAG. "While running with a horse my camera slides
        /// backward; after stopping it gets centred" (2026-09-29).
        ///
        /// A backward drift that grows with speed and recovers at rest is a
        /// position LAG, not an offset - so the question is only which term is
        /// not keeping up. This measures it directly: how far the horse actually
        /// travelled against how far the view's anchor travelled over the same
        /// frames, and on how many of those frames the echo guard threw the fresh
        /// anchor away and held the previous one.
        ///
        /// Travel is summed as per-frame DISTANCE, not end-to-end displacement, so
        /// a circling horse still reads as having moved.
        ///
        /// If the anchor's travel matches the horse's, the camera is not the thing
        /// lagging and the next suspect is the eye point that anchor is built from.
        /// If it falls short by roughly the drift being felt, the shortfall names
        /// the cause: a held count near the frame count means the echo guard, and
        /// a low held count means the anchor source itself is stale.
        /// </summary>
        private static void ProbeMountedLag(Agent agent, MatrixFrame frame, bool held)
        {
            if (agent == null || !agent.HasMount)
            {
                _lagHave = false;
                return;
            }

            Vec3 at = agent.Position;

            if (_lagHave)
            {
                float moved = (at - _lagAgent).Length;

                // A remount or a teleport is not travel.
                if (moved < 2f)
                {
                    _lagAgentTravel += moved;
                    _lagAnchorTravel += (frame.origin - _lagAnchor).Length;
                    _lagFrames++;
                    if (held)
                        _lagHeld++;
                }
            }

            _lagAgent = at;
            _lagAnchor = frame.origin;
            _lagHave = true;

            if (_lagFrames < 450)
                return;

            // Only when there was real movement to measure; a stationary horse
            // would report a meaningless 0 against 0 every five seconds.
            if (_lagAgentTravel > 1f)
            {
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "Mounted camera: over {0} frames the horse travelled {1:F2} m and "
                    + "the view's anchor {2:F2} m - a shortfall of {3:F2} m ({4:P0}). The "
                    + "echo guard held the previous anchor on {5} of those frames "
                    + "({6} total this session). A shortfall with a high held count is "
                    + "the guard; a shortfall without one is the anchor source.",
                    _lagFrames, _lagAgentTravel, _lagAnchorTravel,
                    _lagAgentTravel - _lagAnchorTravel,
                    _lagAgentTravel > 0f
                        ? (_lagAgentTravel - _lagAnchorTravel) / _lagAgentTravel : 0f,
                    _lagHeld, _echoHolds));
            }

            _lagAgentTravel = 0f;
            _lagAnchorTravel = 0f;
            _lagFrames = 0;
            _lagHeld = 0;
        }

        /// <summary>
        /// TEST 251. What a swing actually moves: the agent's position, the camera
        /// anchor's origin and its yaw, measured from the instant of the gesture,
        /// every 200 ms for a second. Six lines per swing, and they are the answer
        /// to "the camera still moves" - which of the three moved, and by how much.
        /// </summary>
        private static void ProbeAttackMotion(Agent agent, MatrixFrame frame)
        {
            if (!MotionProbe || agent == null)
                return;

            long swing = VrMelee.LastSwingAt;
            if (swing == 0L)
                return;

            long now = Stopwatch.GetTimestamp();
            float ms = (float)((now - swing) * 1000.0 / Stopwatch.Frequency);

            if (ms < 0f || ms > 1000f)
                return;

            if (swing != _motionSwing)
            {
                _motionSwing = swing;
                _motionNextMs = 0f;
                _motionAgentAt = agent.Position;
                _motionAnchorAt = frame.origin;
                _motionYawAt = (float)Math.Atan2(frame.rotation.f.x, frame.rotation.f.y);
            }

            if (ms < _motionNextMs)
                return;

            _motionNextMs = ms + 200f;

            Vec3 body = agent.Position - _motionAgentAt;
            Vec3 eye = frame.origin - _motionAnchorAt;
            float yaw = (float)Math.Atan2(frame.rotation.f.x, frame.rotation.f.y) - _motionYawAt;

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 251 camera: +{0:F0} ms after the swing the body has moved "
                + "({1:F3}, {2:F3}, {3:F3}) m, the camera anchor ({4:F3}, {5:F3}, {6:F3}) m, "
                + "and turned {7:F2} deg.",
                ms, body.x, body.y, body.z, eye.x, eye.y, eye.z, yaw * 180.0 / Math.PI));
        }

        // --- TEST 650: "the camera snaps suddenly" (pre-battle) ------------------
        //
        // The 19:47 run logged nothing for it - no recentre, no hitch, no echo hold
        // after the start. Every engine-camera step that jumps (over 0.5 m and 5x the
        // recent pace, or over 6 deg in one frame) is logged with what was going on:
        // flying, lifting, where the laser was, how far the game's cursor moved.

        private static bool _jumpHave;
        private static Vec3 _jumpPrev, _jumpPrevF;
        private static float _jumpPace;
        private static int _jumpLogs;
        private static TaleWorlds.Library.Vec2 _jumpMouse;

        // --- TEST 652: the pre-battle camera eased ------------------------------
        //
        // TEST 650's answer to "snapped three times": vanilla's free camera goes from
        // rest to full speed in ONE frame and stops the same way - 0.03 -> 1.35 m,
        // 0.00 -> 1.41 m, 0.09 -> 1.46 m per frame (~68 m/s at 47 fps, faster with
        // height), the trigger lift too. On a monitor that is a fast camera; in a
        // headset the world jumps. The engine keeps flying its own camera exactly as
        // before (it is handed back each frame); only the VR view follows it with an
        // exponential glide of deployment_camera_ease_s, position and heading, so a
        // start or stop ramps over a few frames. 0 = the old instant view. A jump past
        // 50 m (the deployment start, a reset) is taken at once.
        private static readonly float DeployEaseS = Math.Max(0f, VrConfig.Float("deployment_camera_ease_s", 0.15f));

        private static bool _easeHave;
        private static Vec3 _easeAt, _easeF, _easeRaw;
        private static long _easeTick;

        private static MatrixFrame EaseDeployment(MatrixFrame anchor, out float rawStep)
        {
            rawStep = _easeHave ? (anchor.origin - _easeRaw).Length : 0f;
            _easeRaw = anchor.origin;
            long now = Stopwatch.GetTimestamp();
            float dt = _easeTick == 0L ? 0f : (float)((now - _easeTick) / (double)Stopwatch.Frequency);
            _easeTick = now;

            if (DeployEaseS <= 0f || !VrMissionPhase.Deploying || !_easeHave || dt <= 0f || dt > 0.25f
                || (anchor.origin - _easeAt).LengthSquared > 50f * 50f)
            {
                _easeAt = anchor.origin;
                _easeF = anchor.rotation.f;
                _easeHave = true;
                return anchor;
            }

            float a = 1f - (float)Math.Exp(-dt / DeployEaseS);
            _easeAt += (anchor.origin - _easeAt) * a;
            Vec3 f = _easeF + (anchor.rotation.f - _easeF) * a;
            f.z = 0f;
            if (f.LengthSquared > 1e-6f)
                _easeF = f.NormalizedCopy();

            anchor.origin = _easeAt;
            anchor.rotation = VrMath.HeadingFromForward(_easeF);
            return anchor;
        }

        private static void ProbeCameraJump(MatrixFrame anchor, float rawStep)
        {
            TaleWorlds.Library.Vec2 mouse = TaleWorlds.InputSystem.Input.MousePositionPixel;
            if (_jumpHave)
            {
                float step = (anchor.origin - _jumpPrev).Length;
                float cos = Math.Max(-1f, Math.Min(1f, Vec3.DotProduct(anchor.rotation.f, _jumpPrevF)));
                float turn = (float)(Math.Acos(cos) * 180.0 / Math.PI);
                if (((step > 0.5f && step > 5f * _jumpPace) || turn > 6f) && _jumpLogs < 40)
                {
                    _jumpLogs++;
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 650 camera jump: {0:F2} m (recent pace {1:F2} m/frame; the engine camera moved {11:F2} m) and {2:F1} deg in one frame - "
                        + "deploying {3}, flying {4}, lift {5}, laser {6}, cursor moved {7:F0} px to ({8:F0}, {9:F0}), "
                        + "echo holds so far {10}.",
                        step, _jumpPace, turn, VrMissionPhase.Deploying, VrGamepad.DeploymentFlying,
                        VrGamepad.DeploymentLift,
                        VrLaser.Active ? "on the interface" : VrLaser.GroundActive ? "on the ground" : "off",
                        (mouse - _jumpMouse).Length, mouse.x, mouse.y, _echoHolds, rawStep));
                }
                _jumpPace += (step - _jumpPace) * 0.1f;
            }
            _jumpPrev = anchor.origin;
            _jumpPrevF = anchor.rotation.f;
            _jumpMouse = mouse;
            _jumpHave = true;
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
            _echoHolds++;

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
                float scale = MapScale > 0f ? MapScale : WorldScale;
                float inv = scale == 0.0f ? 1.0f : 1.0f / scale;
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
            // Freezing the world does NOT freeze
            // the camera: Mission.OnTick gets dt = 0, but the camera update runs
            // on realDt, so a mouse turn advances the anchor between the two
            // eyes of a pair whatever the simulation is doing. Unlatched, the
            // pair then differs by the IPD PLUS however far the turn got, which
            // is a disparity error rather than a wobble - the thing this file
            // records as reading like eye strain.
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
