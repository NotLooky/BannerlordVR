using System;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 253. The full body: the pelvis and the chest follow the player's own
    /// body offset, and the legs are solved back to the engine's own feet.
    ///
    /// WHERE THIS SITS. The arms are already the player's (VrArmIk writes the
    /// shoulder-to-wrist chain from the chest out) and the chest is already pinned
    /// upright (VrUpperBody). What was missing was everything the chest hangs
    /// from: with only the chest pinned, a physical crouch moved the camera and
    /// nothing else, and the legs kept whatever the locomotion animation was
    /// doing no matter where the player actually was.
    ///
    /// THE ENGINE'S ANIMATION IS THE BASE, NOT THE COMPETITION
    ///
    /// The lower body is the one part of the player the game is genuinely good at:
    /// the gait cycles with the agent's real speed, the feet land where the engine
    /// says the ground is, and a horse places the whole rider. The physical body
    /// is layered on top of that as an OFFSET, never as a replacement:
    ///
    ///     pelvis = the animation's pelvis + the player's offset x hip gain
    ///     chest  = the animation's chest  + the player's offset x chest gain
    ///     feet   = the animation's feet, untouched
    ///
    /// and the legs solve from the moved pelvis to those untouched feet. With the
    /// player still, the offsets are zero and the solve reproduces the animation
    /// exactly. Crouch and it bends the knees while the feet stay planted. Lean
    /// and the torso moves over the hips. Step and the movement input walks the
    /// agent, so the animation walks the feet: see VrRoomscale.
    ///
    /// WHAT IS LEFT ALONE
    ///
    /// * The head and neck. The camera lives inside the head, so nothing here
    ///   needs it to be right; the anchor is deliberately NOT built from the head
    ///   bone (see VrCameraDriver), so the animation can keep owning it without
    ///   feeding anything back into the view.
    /// * The toes. They follow the foot.
    /// * The clavicles and the arm twist helpers: VrUpperBody pins those before
    ///   this runs, and their locals are relative to spine2, so they ride the
    ///   moved chest for free.
    /// * Mounted, hidden body, deployment, and the TEST 252 attack freeze: the
    ///   body pass stands down and the animation has it.
    ///
    /// THE ORDER IS LOAD-BEARING. It runs after VrBodyFreeze and VrUpperBody and
    /// BEFORE the arm solve, with the entitial frames rebuilt in between - the arm
    /// solve reads the shoulder from those frames, so the chest has to have moved
    /// first. The write itself does not rebuild: the caller owns the single
    /// rebuild, exactly as it already does for the pin.
    /// </summary>
    public static class VrBodyIk
    {
        public static bool Enabled { get; private set; } = VrConfig.Bool("body_ik", true);

        public static bool Debug { get; } = VrConfig.Bool("body_ik_debug", false);

        /// <summary>Set from the VR panel. Off is the game's own animated body:
        /// the pass stops, the camera anchor returns to the engine's own head,
        /// and the held pose is forgotten so switching back on re-captures from
        /// a current, still frame.</summary>
        public static void SetEnabled(bool enabled)
        {
            if (Enabled == enabled)
                return;

            Enabled = enabled;

            _heldValid = false;
            _recaptureHeld = true;
            _haveSmoothed = false;
            _smoothed = Vec3.Zero;
            _lastTick = 0;

            VrLog.Info(enabled
                ? "Full-body IK ON: the pelvis and torso follow you, the legs are solved "
                  + "to the game's own gait, and the camera anchors to the stable eye "
                  + "point while it runs."
                : "Full-body IK off: the body stops following your physical offset - no "
                  + "crouch, no lean - but with body_anim = legs (the default) it still "
                  + "holds the upper body at its captured pose and only the legs animate, "
                  + "and the camera still anchors to the eye point that pose is centred "
                  + "under. The arms and weapons still follow your hands and the room "
                  + "scale is untouched.");
        }

        /// <summary>
        /// Which animations are allowed through to the body:
        ///
        ///     legs  (default) everything above the legs is HELD at the pose it
        ///           had when the body calibrated. Only the legs animate - they
        ///           keep the engine's own gait - and the arms are still solved
        ///           from the controllers, so the visible body is yours, walking.
        ///     full  the animation drives the pelvis and the spine chain as it
        ///           always did, with the player's offset layered on top.
        ///
        /// The held pose is captured at the first calibrated frame - the moment
        /// the standing neutral is taken, which is by definition a still, neutral
        /// pose - and re-captured on every recentre (F10), so standing normally
        /// when you press it is all it asks.
        /// </summary>
        private static readonly string AnimMode =
            VrConfig.String("body_anim", "legs").Trim().ToLowerInvariant();

        public static bool LegsOnly => AnimMode == "legs";

        /// <summary>TEST 352: the body is solved from the headset (VrHeadBody).
        /// Needs body_ik = 1. TEST 353: it also runs in the saddle unless
        /// body_head_mounted = 0.</summary>
        public static bool HeadMode => AnimMode == "head";

        private static readonly bool HeadMounted = VrConfig.Bool("body_head_mounted", true);

        /// <summary>Head mode is driving this frame.</summary>
        public static bool HeadDrives => HeadMode && Drives;

        /// <summary>Whether the upper body is held this frame, i.e. the mode is
        /// on and the pass is running. The attack freeze is skipped while this is
        /// true: the held body already is the freeze, and freezing would stop the
        /// legs, which are the one thing meant to keep moving. Legs-only no longer
        /// needs the Full body switch (see Drives), so this is true whenever the
        /// held body is actually running - mounted, Drives is false and the freeze
        /// behaves as it always did.</summary>
        public static bool HoldsUpperBody => (LegsOnly || HeadMode) && Drives;

        /// <summary>How much of the player's offset the pelvis follows. A real
        /// crouch drops the hips most of the way to the heels, so this is high;
        /// the clamp below is what stops a session-long drift.</summary>
        private static readonly float PelvisGain =
            VrConfig.Float("body_ik_pelvis_gain", 0.6f);

        private static readonly float PelvisClamp =
            Math.Max(0.01f, VrConfig.Float("body_ik_pelvis_clamp", 0.30f));

        /// <summary>The chest follows a little further than the hips: that is what
        /// turns a translation into a lean instead of a slide.</summary>
        private static readonly float ChestGain =
            VrConfig.Float("body_ik_chest_gain", 0.9f);

        private static readonly float ChestClamp =
            Math.Max(0.01f, VrConfig.Float("body_ik_chest_clamp", 0.50f));

        /// <summary>Time constant for the offset's own low-pass, in seconds. The
        /// offset comes from a headset at the end of an arm; a few tens of
        /// milliseconds of tracking jitter through a knee is a bounce.</summary>
        private static readonly float SmoothSeconds =
            Math.Max(0f, VrConfig.Float("body_ik_smooth_ms", 60f) * 0.001f);

        /// <summary>How far a physical lean tilts the chest, degrees at the
        /// reference offset below. A crouch that comes with a lean should fold
        /// the torso, not just lower it.</summary>
        private static readonly float LeanDegrees =
            Math.Max(0f, VrConfig.Float("body_ik_lean_deg", 20f));

        /// <summary>The horizontal offset at which the chest is at its full lean.
        /// Metres; the vertical offset contributes nothing, so a straight-down
        /// crouch lowers the body without pitching it.</summary>
        private static readonly float LeanAt =
            Math.Max(0.05f, VrConfig.Float("body_ik_lean_m", 0.35f));

        private static bool _broken;
        private static bool _logged;
        private static int _debugTick;

        // The worst leg reach ratio this frame: 1.00 means the hip is exactly a
        // leg's length from the ankle. Above 1 the leg is stretching, which is
        // how a too-deep crouch or a bad bone mapping shows up in the numbers.
        private static float _legReach = -1f;

        private static Vec3 _smoothed;
        private static bool _haveSmoothed;
        private static long _lastTick;

        // The held upper body, in the agent entity's own frame, captured from the
        // animation at the first calibrated frame and re-captured on a recentre.
        // Only used when AnimMode is "legs": the torso bones are written from
        // these instead of from the animation, and the legs are not, which is the
        // whole of "only the legs animate".
        private static bool _heldValid;
        private static bool _recaptureHeld;
        private static UIntPtr _heldSkeleton = UIntPtr.Zero;

        /// <summary>body_ik_capture_settle: how long a skeleton must have been
        /// seen (and, separately, calm - see CaptureHeld) before it is trusted
        /// for a capture. A fresh spawn can still be blending in from its rest
        /// pose for the first frame or two, with the animation graph not yet in
        /// charge - captured then, a held torso comes out wildly wrong (TEST
        /// 267's second capture: leg reach over 2x, the hip nearly a metre past
        /// where it should be) even though nothing about the capture itself was
        /// unusual otherwise. 0.5 s matches the dwell VrCameraDriver already
        /// uses for the standing-neutral capture.</summary>
        private static readonly float CaptureSettle =
            Math.Max(0f, VrConfig.Float("body_ik_capture_settle", 0.5f));

        /// <summary>body_ik_capture_reach_max: the currently-animated thigh-to-
        /// foot span, divided by the leg's own length, above which a capture is
        /// rejected and retried next frame. 1.00 is a leg at full stretch - a
        /// standing, ground-contacted skeleton is never much past that - so
        /// anything higher is a blend-in transient, not a pose worth holding.
        /// The healthy sessions logged 0.89-1.12 here; TEST 267's bad capture
        /// hit 1.66 rising to 2.38 right after the settle timer let it through.
        /// </summary>
        private static readonly float CaptureReachMax =
            Math.Max(1f, VrConfig.Float("body_ik_capture_reach_max", 1.2f));

        private static UIntPtr _pendingSkeleton = UIntPtr.Zero;
        private static long _pendingSince;
        private static MatrixFrame _heldPelvis;
        private static MatrixFrame _heldSpine;
        private static MatrixFrame _heldSpine1;
        private static MatrixFrame _heldSpine2;
        private static MatrixFrame _heldThighLeft;
        private static MatrixFrame _heldThighRight;
        private static MatrixFrame _heldNeckLocal;
        private static MatrixFrame _heldHeadLocal;
        private static bool _heldHaveNeck;
        private static bool _loggedHeld;
        private static bool _loggedEyeFollow;

        /// <summary>
        /// Whether the body pass is the one that owns the torso this frame. The
        /// camera asks this before anchoring to the engine's own head bone: if we
        /// are about to write the chest, the head follows it, and an anchor built
        /// from the head would read our own writing back and add the player's head
        /// offset to itself every frame.
        /// </summary>
        public static bool Drives
        {
            get
            {
                if (!(Enabled || LegsOnly) || (HeadMode && !Enabled) || !VrMotionHands.Enabled || VrBodyHide.Hiding)
                    return false;

                Agent agent = Mission.Current?.MainAgent;
                return agent != null && agent.IsActive() && (!agent.HasMount || (HeadMode && HeadMounted));
            }
        }

        /// <summary>
        /// Writes the pelvis, the spine and both legs. Returns true when anything
        /// was written, which the caller treats exactly like the chest pin: the
        /// entitial frames then need rebuilding before anything reads them.
        ///
        /// rootWanted is the frame the caller is about to put back on the root; the
        /// body pass moves it by the pelvis offset, and the caller's own restore
        /// then lands the moved pelvis rather than the animation's.
        /// </summary>
        public static bool Apply(MBAgentVisuals visuals, Skeleton skeleton,
                                 bool frozen, ref MatrixFrame rootWanted)
        {
            if (!Drives || frozen || _broken || visuals == null || skeleton == null)
                return false;

            if (HeadMode)
                return ApplyHead(visuals, skeleton, ref rootWanted);

            // Nothing to follow until the standing neutral has been measured: the
            // raw offset before that is relative to whatever pose the recentre
            // happened in, and acting on it would snap the body across the room for
            // half a second at mission start.
            if (!VrCameraDriver.RoomCalibrated)
                return false;

            try
            {
                VrSpineChain spine;
                VrLegChain left, right;
                if (!VrBodyBones.TryResolve(visuals, skeleton, out spine, out left, out right))
                    return false;

                // The root is the pelvis and everything below hangs off it. Without
                // it there is no body to move, only limbs to bend.
                if (spine.Pelvis < 0)
                    return false;

                // ---- read the animation, all of it, before any write -----------
                //
                // The entitial cache does not follow a write until it is rebuilt,
                // so a read after the first write returns the animation's value for
                // one bone and ours for the next. Every source frame is taken here,
                // and none is re-read below.
                MatrixFrame animPelvis = skeleton.GetBoneEntitialFrameWithIndex(spine.Pelvis);

                MatrixFrame animSpine = default(MatrixFrame);
                MatrixFrame animSpine1 = default(MatrixFrame);
                MatrixFrame animSpine2 = default(MatrixFrame);

                if (spine.Valid)
                {
                    animSpine = skeleton.GetBoneEntitialFrameWithIndex(spine.Spine);
                    animSpine1 = skeleton.GetBoneEntitialFrameWithIndex(spine.Spine1);
                    animSpine2 = skeleton.GetBoneEntitialFrameWithIndex(spine.Spine2);
                }

                // ---- the held upper body, when only the legs animate -----------
                //
                // Captured from the animation on the first calibrated frame - which
                // is when the standing neutral is taken, so it is a still, neutral
                // pose - and re-captured when the body is asked to forget its offset
                // (a recentre). Held in the ENTITY's frame, so the body still turns
                // with the agent; it is the animation's motion that stops, not the
                // body's.
                if (LegsOnly)
                    CaptureHeld(skeleton, spine, left, right, animPelvis,
                                animSpine, animSpine1, animSpine2);

                MatrixFrame srcPelvis = LegsOnly && _heldValid ? _heldPelvis : animPelvis;
                MatrixFrame srcSpine = LegsOnly && _heldValid ? _heldSpine : animSpine;
                MatrixFrame srcSpine1 = LegsOnly && _heldValid ? _heldSpine1 : animSpine1;
                MatrixFrame srcSpine2 = LegsOnly && _heldValid ? _heldSpine2 : animSpine2;

                // ---- the player's offset, eased ---------------------------------
                //
                // ponytail: the offset is in world metres and the bones it is added
                // to are in the agent entity's own space. On a scaled agent those
                // differ by the entity's scale, so the offsets are off by that
                // factor - a few centimetres on a half-metre crouch at the 1.06 a
                // scaled agent measured. The player agent is 1.0000 in every run so
                // far; divide by the entity scale here if a scaled player ever
                // shows up and the crouch depth looks wrong.
                // The Full body switch now means only "the body follows my
                // physical moves": off, the offset is zero, the pelvis and chest
                // deltas are zero and the tilt is identity, so this pass only
                // holds the upper body (legs-only) and solves the legs from the
                // held hips to the animated feet.
                Vec3 offset = Enabled ? Smooth(VrCameraDriver.RoomOffset, Dt()) : Vec3.Zero;

                Vec3 pelvisDelta = ClampAxis(offset * PelvisGain, PelvisClamp);
                Vec3 chestDelta = ClampAxis(offset * ChestGain, ChestClamp);

                MatrixFrame newPelvis = srcPelvis;
                newPelvis.origin = srcPelvis.origin + pelvisDelta;
                rootWanted = newPelvis;

                // The lean: the horizontal offset tips the chest toward where the
                // player actually is, so a crouch that comes with a lean folds the
                // torso rather than sliding it straight down. The pelvis keeps the
                // source orientation, which is what keeps every child's rigid
                // offset a pure translation.
                Mat3 chestTilt = Tilt(offset);

                bool wrote = false;
                _legReach = -1f;

                // ---- the torso: a two-bone solve from the spine's own base to the
                //      chest target, with the source curve as the pole ------------
                if (spine.Valid)
                    wrote |= SolveTorso(skeleton, spine, srcSpine, srcSpine1, srcSpine2,
                                        newPelvis, pelvisDelta, chestDelta, chestTilt);

                // The neck and head are not part of the solve and would otherwise
                // keep animating on top of the held chest. Written from the held
                // relative frames, which ride the chest's move and tilt for free.
                if (LegsOnly && _heldValid && _heldHaveNeck)
                {
                    wrote |= SetLocal(skeleton, spine.Neck, _heldNeckLocal);
                    wrote |= SetLocal(skeleton, spine.Head, _heldHeadLocal);
                }

                // ---- the legs: the moved pelvis to the animation's own feet -----
                //
                // The hips come from the SOURCE pelvis - the held one when only the
                // legs animate - so a pelvis captured mid-stride does not drag the
                // hips to where the animation had them. The ankles and feet are the
                // animation's, which is what keeps the gait.
                if (left.Valid)
                {
                    MatrixFrame srcThigh = LegsOnly && _heldValid
                        ? _heldThighLeft
                        : skeleton.GetBoneEntitialFrameWithIndex(left.Thigh);

                    wrote |= SolveLeg(skeleton, left, newPelvis, srcThigh.origin + pelvisDelta);
                }

                if (right.Valid)
                {
                    MatrixFrame srcThigh = LegsOnly && _heldValid
                        ? _heldThighRight
                        : skeleton.GetBoneEntitialFrameWithIndex(right.Thigh);

                    wrote |= SolveLeg(skeleton, right, newPelvis, srcThigh.origin + pelvisDelta);
                }

                if (wrote)
                {
                    LogOnce();
                    Trace(offset, pelvisDelta, chestDelta);
                }

                return wrote;
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("The full-body IK failed; the body goes back to the game's own "
                            + "animation and the arms keep working. Nothing else is "
                            + "affected.", ex);
                return false;
            }
        }

        /// <summary>TEST 352: body_anim = head. No capture and no room offset:
        /// the body is solved from the headset itself (VrHeadBody).</summary>
        private static bool ApplyHead(MBAgentVisuals visuals, Skeleton skeleton, ref MatrixFrame rootWanted)
        {
            try
            {
                Agent agent = Mission.Current?.MainAgent;
                if (agent == null
                    || !VrBodyBones.TryResolve(visuals, skeleton, out VrSpineChain spine,
                                               out VrLegChain left, out VrLegChain right))
                    return false;

                return VrHeadBody.Apply(agent, visuals, skeleton, spine, left, right, ref rootWanted);
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("TEST 352: the head-driven body failed; the body goes back to the game's own "
                            + "animation and the arms keep working. Nothing else is affected.", ex);
                return false;
            }
        }

        private static readonly bool FitProbe = VrConfig.Bool("body_fit_probe", false);
        private static long _fitLogAt;

        /// <summary>
        /// TEST 325, a probe: how far the drawn head's eye point is from the
        /// camera this frame, in the body's own axes, and how far the agent
        /// travelled between the camera's anchor and this write. Called after
        /// the last rebuild, so the head read here is the one that is drawn.
        /// Changes nothing. The numbers are what "the camera sits in the head"
        /// is judged on: under 1 cm standing, under 2 cm walking.
        /// </summary>
        internal static void ProbeFit(Agent agent, MBAgentVisuals visuals, Skeleton skeleton)
        {
            if (!FitProbe || !VrCameraDriver.HasPose || agent == null || visuals == null || skeleton == null)
                return;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now < _fitLogAt)
                return;
            _fitLogAt = now + System.Diagnostics.Stopwatch.Frequency / 2;

            Monster monster = agent.Monster;
            sbyte bone = monster != null ? monster.HeadLookDirectionBoneIndex : (sbyte)-1;
            if (bone < 0)
                return;

            // Not ForceUpdateBoneFrames (VrEyePoint.HeadPoint's way): that would
            // rebuild from the animation and read back a head we did not draw.
            MatrixFrame head = skeleton.GetBoneEntitialFrameWithIndex(bone);
            Vec3 eyeEntity = head.TransformToParent(monster.FirstPersonCameraOffsetWrtHead);
            MatrixFrame entity = visuals.GetFrame();
            Vec3 drawn = entity.TransformToParentDouble(eyeEntity);

            Vec3 gap = VrCameraDriver.HeadFrame.origin - drawn;
            Vec3 local = entity.rotation.TransformToLocal(gap);

            Vec3 travel = VrCameraDriver.AnchorAgentValid
                ? agent.Position - VrCameraDriver.AnchorAgentPosition
                : Vec3.Zero;
            travel.z = 0f;

            VrLog.Info("TEST 325 fit: camera minus drawn eye (right " + F(local.x) + ", fwd " + F(local.y)
                       + ", up " + F(local.z) + ") = " + F(gap.Length) + " m; agent moved "
                       + F(travel.Length) + " m since the anchor at " + F(agent.Velocity.Length)
                       + " m/s; room offset up " + F(VrCameraDriver.RoomOffset.z) + "; body "
                       + (Drives ? AnimMode : "engine") + (agent.CrouchMode ? ", crouched" : "") + ".");
        }

        private static string F(float v) => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

        // ---- TEST 380: how much the drawn body moves while the player does not --

        private static readonly bool JitterProbe = VrConfig.Bool("body_jitter_probe", true);
        private const int JitterPoints = 6;       // pelvis, knee L, knee R, elbow L, elbow R, chest facing
        private static readonly Vec3[] _jitterPrev = new Vec3[JitterPoints];
        private static readonly Vec3[] _jitterNow = new Vec3[JitterPoints];
        private static readonly double[] _jitterSum = new double[JitterPoints];
        private static int _jitterFrames;
        private static bool _jitterHave;
        private static long _jitterLogAt;

        /// <summary>
        /// TEST 380, a probe: the frame-to-frame RMS motion of the drawn pelvis,
        /// knees, elbows and chest while the stick is idle, in the entity's frame
        /// (so room-scale steps do not count), plus how close each leg and arm is
        /// to full stretch - where a two-bone joint amplifies noise. Logged every
        /// 5 s. Called after the last rebuild: what is measured is what is drawn.
        /// Changes nothing.
        /// </summary>
        internal static void ProbeJitter(Agent agent, MBAgentVisuals visuals, Skeleton skeleton)
        {
            if (!JitterProbe || agent == null || visuals == null || skeleton == null)
                return;

            // The HumanBone layout (28 bones), for the arm indices below; the
            // accessors are unchecked passthroughs.
            if (skeleton.GetBoneCount() != 28 || VrHeadBody.Moving(agent)
                || !VrBodyBones.TryResolve(visuals, skeleton, out VrSpineChain spine,
                                           out VrLegChain left, out VrLegChain right)
                || !spine.Valid || !left.Valid || !right.Valid)
            {
                _jitterHave = false;
                return;
            }

            _jitterNow[0] = skeleton.GetBoneEntitialFrameWithIndex(spine.Pelvis).origin;
            _jitterNow[1] = skeleton.GetBoneEntitialFrameWithIndex(left.Calf).origin;
            _jitterNow[2] = skeleton.GetBoneEntitialFrameWithIndex(right.Calf).origin;
            _jitterNow[3] = skeleton.GetBoneEntitialFrameWithIndex((sbyte)HumanBone.ForearmL).origin;
            _jitterNow[4] = skeleton.GetBoneEntitialFrameWithIndex((sbyte)HumanBone.ForearmR).origin;
            Vec3 chest = skeleton.GetBoneEntitialFrameWithIndex(spine.Spine2).rotation.f;
            _jitterNow[5] = chest.LengthSquared > 1e-8f ? chest.NormalizedCopy() : chest;

            if (_jitterHave)
            {
                for (int i = 0; i < JitterPoints; i++)
                    _jitterSum[i] += (_jitterNow[i] - _jitterPrev[i]).LengthSquared;
                _jitterFrames++;
            }

            Array.Copy(_jitterNow, _jitterPrev, JitterPoints);
            _jitterHave = true;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now < _jitterLogAt || _jitterFrames < 30)
                return;
            _jitterLogAt = now + 5 * System.Diagnostics.Stopwatch.Frequency;

            Func<int, string> mm = i => (Math.Sqrt(_jitterSum[i] / _jitterFrames) * 1000.0)
                .ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            string chestDeg = (Math.Sqrt(_jitterSum[5] / _jitterFrames) * 57.29578)
                .ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

            VrLog.Info("TEST 380 jitter, standing still (RMS per frame over " + _jitterFrames + " frames): pelvis "
                       + mm(0) + " mm, knee L " + mm(1) + " R " + mm(2) + " mm, elbow L " + mm(3) + " R " + mm(4)
                       + " mm, chest " + chestDeg + " deg; reach (1.00 = straight) leg L "
                       + F(LegReach(skeleton, left)) + " R " + F(LegReach(skeleton, right)) + ", arm L "
                       + F(ArmReach(skeleton, HumanBone.UpperarmL, HumanBone.ForearmL, HumanBone.HandL)) + " R "
                       + F(ArmReach(skeleton, HumanBone.UpperarmR, HumanBone.ForearmR, HumanBone.HandR))
                       + "; ik_soft " + F(VrArmIk.Soft) + ", idle hold " + (VrBodyFreeze.IdleHeld ? "on" : "off") + ".");

            Array.Clear(_jitterSum, 0, JitterPoints);
            _jitterFrames = 0;
        }

        private static float LegReach(Skeleton skeleton, VrLegChain leg)
        {
            float len = leg.ThighLen + leg.CalfLen;
            return len > 1e-4f
                ? (skeleton.GetBoneEntitialFrameWithIndex(leg.Foot).origin
                   - skeleton.GetBoneEntitialFrameWithIndex(leg.Thigh).origin).Length / len
                : 0f;
        }

        private static float ArmReach(Skeleton skeleton, HumanBone upper, HumanBone fore, HumanBone hand)
        {
            // The rest pose is unscaled and the drawn bones carry the character's
            // scale: the forearm's rest length is scaled by the live upper arm's
            // (the upper arm is rigid; a pinned hand can stretch the forearm).
            Vec3 u = skeleton.GetBoneEntitialRestFrame((sbyte)upper, false).origin;
            Vec3 f = skeleton.GetBoneEntitialRestFrame((sbyte)fore, false).origin;
            Vec3 h = skeleton.GetBoneEntitialRestFrame((sbyte)hand, false).origin;
            Vec3 lu = skeleton.GetBoneEntitialFrameWithIndex((sbyte)upper).origin;
            Vec3 lf = skeleton.GetBoneEntitialFrameWithIndex((sbyte)fore).origin;
            Vec3 lh = skeleton.GetBoneEntitialFrameWithIndex((sbyte)hand).origin;
            float restUpper = (f - u).Length, liveUpper = (lf - lu).Length;
            float len = restUpper > 1e-4f ? liveUpper + (h - f).Length * liveUpper / restUpper : 0f;
            return len > 1e-4f ? (lh - lu).Length / len : 0f;
        }

        /// <summary>
        /// The held body's own eye point, in WORLD space, this frame - or false
        /// if nothing has been captured yet. VrCameraDriver's anchor calls this
        /// instead of a generic eye-height formula while the body pass is
        /// driving: the held torso does not move with the animation, so its eye
        /// does not either, and the only thing that changes it frame to frame is
        /// the agent's own position and yaw - exactly what TransformToParentDouble
        /// picks up from visuals.GetFrame(). Camera and body read the same
        /// number this way, so nothing needs to bend one to fit the other.
        /// </summary>
        public static bool TryGetHeldEyeWorld(Agent agent, out Vec3 world)
        {
            world = Vec3.Zero;

            // TEST 352b: under the head-driven body, the body's own upright eye.
            if (HeadMode)
                return VrHeadBody.TryNeutralEyeWorld(agent, out world);

            if (!LegsOnly || !_heldValid || !_heldHaveNeck || agent == null)
                return false;

            MBAgentVisuals visuals = agent.AgentVisuals;
            Monster monster = agent.Monster;

            if (visuals == null || !visuals.IsValid() || monster == null
                || monster.HeadLookDirectionBoneIndex < 0)
                return false;

            try
            {
                MatrixFrame neck = _heldSpine2.TransformToParent(_heldNeckLocal);
                MatrixFrame head = neck.TransformToParent(_heldHeadLocal);
                Vec3 eyeEntity = head.TransformToParent(monster.FirstPersonCameraOffsetWrtHead);

                Vec3 point = visuals.GetFrame().TransformToParentDouble(eyeEntity);
                if (!Finite(point))
                    return false;

                world = point;

                if (!_loggedEyeFollow)
                {
                    _loggedEyeFollow = true;
                    VrLog.Info("Body IK: the camera now follows the held body's own eye point "
                               + "every frame, instead of a generic formula.");
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The whole of "only the legs animate": takes the source frames for the
        /// torso from the pose the body was in at the first calibrated frame, and
        /// from every recentre after that. Held in the entity's frame, so the body
        /// still turns with the agent; what stops is the ANIMATION's motion - the
        /// walk's rock, the idle's sway, the combat lean.
        /// </summary>
        private static void CaptureHeld(Skeleton skeleton, VrSpineChain spine,
                                        VrLegChain left, VrLegChain right,
                                        MatrixFrame pelvis, MatrixFrame s, MatrixFrame s1,
                                        MatrixFrame s2)
        {
            if (_heldValid && _heldSkeleton == skeleton.Pointer && !_recaptureHeld)
                return;

            // A fresh skeleton - a spawn, a respawn, a new mission - can still
            // be blending in from its rest pose for the first frame or two,
            // before the animation graph has taken over. Nothing about that
            // frame looks busy (no attack, no velocity), so the calm-moment
            // gate below would happily capture it. Give every new skeleton a
            // short settle first.
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_pendingSkeleton != skeleton.Pointer)
            {
                _pendingSkeleton = skeleton.Pointer;
                _pendingSince = now;
            }

            double sinceSeen = (now - _pendingSince) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (sinceSeen < CaptureSettle)
                return;

            // Capture only at a calm moment: a guard or attack pose, or a
            // mid-stride torso, would otherwise be held until the next F10.
            // Leave _recaptureHeld set so the next frame tries again.
            Agent agent = Mission.Current?.MainAgent;
            if (agent != null
                && (VrMelee.EngineActing(agent) || agent.MovementVelocity.LengthSquared > 0.09f))
                return;

            // The settle timer above is a guess at how long a blend-in takes;
            // TEST 267's own re-run proved 0.5 s is not always enough - a
            // capture landed with the hip nearly a metre from a sane spot and
            // the leg reach climbing to 2.3+ right after. This checks the
            // actual geometry instead of trusting a clock: a standing leg can
            // never really be stretched past its own length, so a currently-
            // animated thigh-to-foot span beyond CaptureReachMax means the
            // frame is still mid-transition (a rest-pose blend, most likely),
            // whatever the settle timer and the calm-moment gate both said.
            if (!ReachSane(skeleton, left) || !ReachSane(skeleton, right))
                return;

            _recaptureHeld = false;
            _heldSkeleton = skeleton.Pointer;
            _heldValid = true;
            _heldPelvis = pelvis;

            if (spine.Valid)
            {
                _heldSpine = s;
                _heldSpine1 = s1;
                _heldSpine2 = s2;

                if (spine.Neck >= 0 && spine.Head >= 0)
                {
                    MatrixFrame neck = skeleton.GetBoneEntitialFrameWithIndex(spine.Neck);
                    MatrixFrame head = skeleton.GetBoneEntitialFrameWithIndex(spine.Head);

                    // Relative, so they ride the chest's position and tilt.
                    _heldNeckLocal = s2.TransformToLocalNonOrthogonal(neck);
                    _heldHeadLocal = neck.TransformToLocalNonOrthogonal(head);
                    _heldHaveNeck = true;
                }
            }

            if (left.Valid)
                _heldThighLeft = skeleton.GetBoneEntitialFrameWithIndex(left.Thigh);

            if (right.Valid)
                _heldThighRight = skeleton.GetBoneEntitialFrameWithIndex(right.Thigh);

            // NOT centred by shifting the pelvis. TEST 267 tried subtracting the
            // held eye's horizontal offset from every held origin so the eye would
            // sit under a camera built from agent.Position + eye height alone - but
            // the FEET are never shifted (they always follow the live animation),
            // so moving the pelvis away from the position the animation actually
            // captured it at pulls the hip out from over the feet by exactly that
            // much. On a real capture that offset is 0.2-0.3 m, way more than a
            // standing pose can absorb without looking wrong - confirmed by a
            // headset run where the eye offset was small and sane (0.31/-0.06,
            // leg reach 0.94-1.10 throughout) and the body still looked broken.
            // The camera is the thing that moves instead: see VrCameraDriver's
            // VrBodyIk.Drives branch, which now reads TryGetHeldEyeWorld every
            // frame - the held body's own actual eye point - rather than a
            // generic formula the held body then has to be bent to match.
            if (!_loggedHeld)
            {
                _loggedHeld = true;
                VrLog.Info("Body animation set to LEGS ONLY: everything above the legs is "
                           + "held at the pose it was in when the body calibrated, and only "
                           + "the legs animate. The arms are still solved from the "
                           + "controllers. Re-captured on every recentre, so stand normally "
                           + "when you press F10. body_anim = full brings the animation back.");
            }
        }

        private static bool SolveTorso(Skeleton skeleton, VrSpineChain spine,
                                       MatrixFrame srcSpine, MatrixFrame srcSpine1,
                                       MatrixFrame srcSpine2,
                                       MatrixFrame newPelvis,
                                       Vec3 pelvisDelta, Vec3 chestDelta, Mat3 chestTilt)
        {
            Vec3 basePos = srcSpine.origin + pelvisDelta;
            Vec3 chestPos = srcSpine2.origin + chestDelta;

            // The pole: the source mid-spine's own displacement from the chord, so
            // a player who is not moving gets the source curve back unchanged. A
            // straight spine names no direction; the natural fallback is the
            // spine's own kyphosis, the mid behind the chord.
            Vec3 chord = srcSpine2.origin - srcSpine.origin;
            float chordLen = chord.Length;

            Vec3 hint = chordLen > 1e-4f
                ? VrArmBend.Perp(srcSpine1.origin - srcSpine.origin,
                                 chord * (1f / chordLen))
                : Vec3.Zero;

            if (hint.LengthSquared < 1e-6f)
            {
                Vec3 toChest = chestPos - basePos;
                hint = toChest.LengthSquared > 1e-8f
                    ? VrArmBend.Perp(-VrArmBend.Forward, toChest.NormalizedCopy())
                    : Vec3.Zero;
            }

            if (hint.LengthSquared < 1e-8f)
                return false;

            Vec3 mid;
            float usedLower, usedUpper;

            if (!VrArmIk.SolveElbowWithBend(basePos, chestPos, hint,
                                            spine.LowerLen, spine.UpperLen,
                                            out mid, out usedLower, out usedUpper, VrArmIk.Soft))
                return false;

            Vec3 lowerDir = (mid - basePos).NormalizedCopy();
            Vec3 upperDir = (chestPos - mid).NormalizedCopy();

            MatrixFrame wantSpine = srcSpine;
            wantSpine.origin = basePos;
            wantSpine.rotation = VrArmIk.Aim(srcSpine.rotation, spine.SpineAxis, lowerDir);

            MatrixFrame wantSpine1 = srcSpine1;
            wantSpine1.origin = mid;
            wantSpine1.rotation = VrArmIk.Aim(srcSpine1.rotation, spine.Spine1Axis, upperDir);

            // The chest keeps the source orientation, TILTED by the player's own
            // lean: its facing is the body's, which the agent already owns, and the
            // neck and head hang off it from there.
            MatrixFrame wantSpine2 = srcSpine2;
            wantSpine2.origin = chestPos;
            wantSpine2.rotation = Compose(chestTilt, srcSpine2.rotation);

            bool wrote = SetLocal(skeleton, spine.Spine,
                                  newPelvis.TransformToLocalNonOrthogonal(wantSpine));

            wrote |= SetLocal(skeleton, spine.Spine1,
                              wantSpine.TransformToLocalNonOrthogonal(wantSpine1));

            wrote |= SetLocal(skeleton, spine.Spine2,
                              wantSpine1.TransformToLocalNonOrthogonal(wantSpine2));

            return wrote;
        }

        /// <summary>The leg from the moved pelvis to a foot: the animation's,
        /// or TEST 354's planted one (wantFoot), whose toes then lead the knee.</summary>
        internal static bool SolveLeg(Skeleton skeleton, VrLegChain leg,
                                      MatrixFrame newPelvis, Vec3 hip, MatrixFrame? wantFootFrame = null)
        {
            MatrixFrame animThigh = skeleton.GetBoneEntitialFrameWithIndex(leg.Thigh);
            MatrixFrame animCalf = skeleton.GetBoneEntitialFrameWithIndex(leg.Calf);
            MatrixFrame animFoot = skeleton.GetBoneEntitialFrameWithIndex(leg.Foot);
            MatrixFrame footAt = wantFootFrame ?? animFoot;

            // TEST 352c: never past the leg's length. SolveElbowWithBend stretches
            // both segments to reach, so a foot left behind drew a stretched leg
            // ("feet get stretched when moving"); now the foot lifts toward the hip.
            // TEST 381: eased over the last ik_soft of the reach, not cut at 0.999 -
            // a standing leg sat on the knee's singular point, where a millimetre of
            // hip noise swung the knee centimetres. The foot lifts a few mm instead.
            float legLen = leg.ThighLen + leg.CalfLen;
            Vec3 hipToAnkle = footAt.origin - hip;
            float hipSpan = hipToAnkle.Length;
            float eased = VrArmIk.Soft > 0f
                ? VrArmIk.SoftReach(hipSpan, legLen, VrArmIk.Soft)
                : Math.Min(hipSpan, legLen * 0.999f);
            if (legLen > 1e-4f && hipSpan > 1e-4f && eased < hipSpan)
                footAt.origin = hip + hipToAnkle * (eased / hipSpan);

            Vec3 ankle = footAt.origin;

            Vec3 toeDir = Vec3.Zero;
            if (leg.Toe >= 0)
            {
                MatrixFrame animToe = skeleton.GetBoneEntitialFrameWithIndex(leg.Toe);
                Vec3 toe = footAt.TransformToParent(animFoot.TransformToLocalNonOrthogonal(animToe).origin);
                if ((toe - footAt.origin).LengthSquared > 1e-8f)
                    toeDir = (toe - footAt.origin).NormalizedCopy();
            }

            Vec3 knee, thighDir, calfDir;

            if (!VrLegIk.Solve(hip, ankle, animCalf.origin, toeDir,
                               leg.ThighLen, leg.CalfLen, out knee, out thighDir, out calfDir))
                return false;

            float span = (ankle - hip).Length;
            float reach = leg.ThighLen + leg.CalfLen > 1e-4f
                ? span / (leg.ThighLen + leg.CalfLen)
                : 0f;

            if (reach > _legReach)
                _legReach = reach;

            MatrixFrame wantThigh = animThigh;
            wantThigh.origin = hip;
            wantThigh.rotation = VrArmIk.Aim(animThigh.rotation, leg.ThighAxis, thighDir);

            MatrixFrame wantCalf = animCalf;
            wantCalf.origin = knee;
            wantCalf.rotation = VrArmIk.Aim(animCalf.rotation, leg.CalfAxis, calfDir);

            // The foot is the engine's, exactly: it is the ground contact the
            // animation put there, and recomputing it would be a second guess
            // stacked on the first. Or TEST 354's planted foot.
            MatrixFrame wantFoot = footAt;

            bool wrote = SetLocal(skeleton, leg.Thigh,
                                  newPelvis.TransformToLocalNonOrthogonal(wantThigh));

            wrote |= SetLocal(skeleton, leg.Calf,
                              wantThigh.TransformToLocalNonOrthogonal(wantCalf));

            wrote |= SetLocal(skeleton, leg.Foot,
                              wantCalf.TransformToLocalNonOrthogonal(wantFoot));

            return wrote;
        }

        /// <summary>
        /// The lean the horizontal offset asks for: a rotation that tips the body's
        /// up axis toward where the player actually is. Forward is a tilt toward
        /// +Y (a negative turn about the right axis), right is a tilt toward +X
        /// (a positive turn about the forward axis). No vertical term: a
        /// straight-down crouch lowers the body without pitching it, which is what
        /// a real crouch does.
        /// </summary>
        private static Mat3 Tilt(Vec3 offset)
        {
            if (LeanDegrees <= 0f)
                return Mat3.Identity;

            float max = (float)(LeanDegrees * Math.PI / 180.0);

            float forward = Math.Max(-1f, Math.Min(1f, offset.y / LeanAt));
            float right = Math.Max(-1f, Math.Min(1f, offset.x / LeanAt));

            Mat3 r = Mat3.Identity;
            r = Rotate(r, VrArmBend.Right, -forward * max);
            r = Rotate(r, VrArmBend.Forward, right * max);
            return r;
        }

        /// <summary>outer * inner, built basis by basis: Mat3 has no operator for
        /// this and the composition order is the whole question.</summary>
        internal static Mat3 Compose(Mat3 outer, Mat3 inner)
        {
            Mat3 r = default(Mat3);
            r.s = outer.TransformToParent(inner.s);
            r.f = outer.TransformToParent(inner.f);
            r.u = outer.TransformToParent(inner.u);
            return r;
        }

        /// <summary>Rodrigues, applied to each basis vector. The arm solver has the
        /// same helper; it is four lines and not worth a shared home.</summary>
        private static Mat3 Rotate(Mat3 m, Vec3 axis, float angle)
        {
            float c = (float)Math.Cos(angle);
            float s = (float)Math.Sin(angle);

            Mat3 r = default(Mat3);
            r.s = RotateVec(m.s, axis, c, s);
            r.f = RotateVec(m.f, axis, c, s);
            r.u = RotateVec(m.u, axis, c, s);
            return r;
        }

        private static Vec3 RotateVec(Vec3 v, Vec3 axis, float cos, float sin)
        {
            return v * cos
                 + Vec3.CrossProduct(axis, v) * sin
                 + axis * (Vec3.DotProduct(axis, v) * (1f - cos));
        }

        /// <summary>The offset eased over time. A first call, or an implausible
        /// gap, snaps rather than easing from a stale value.</summary>
        private static Vec3 Smooth(Vec3 raw, float dt)
        {
            if (!_haveSmoothed || SmoothSeconds <= 1e-4f || dt <= 0f)
            {
                _smoothed = raw;
                _haveSmoothed = true;
                return _smoothed;
            }

            float alpha = 1f - (float)Math.Exp(-dt / SmoothSeconds);
            _smoothed = _smoothed + (raw - _smoothed) * alpha;
            return _smoothed;
        }

        private static float Dt()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long then = _lastTick;
            _lastTick = now;

            if (then == 0)
                return 1f / 90f;

            float dt = (float)((now - then) / (double)System.Diagnostics.Stopwatch.Frequency);
            if (dt <= 0f || dt > 0.25f)
                return 1f / 90f;

            return dt;
        }

        /// <summary>Scales the vector down so no axis exceeds the clamp; a crouch
        /// must not be limited by however far sideways the player happened to
        /// lean.</summary>
        private static Vec3 ClampAxis(Vec3 v, float limit)
        {
            return new Vec3(
                Math.Max(-limit, Math.Min(limit, v.x)),
                Math.Max(-limit, Math.Min(limit, v.y)),
                Math.Max(-limit, Math.Min(limit, v.z)),
                -1f);
        }

        internal static bool SetLocal(Skeleton skeleton, sbyte bone, MatrixFrame local)
        {
            if (bone < 0 || !Finite(local))
                return false;

            skeleton.SetBoneLocalFrame(bone, local);
            return true;
        }

        /// <summary>Whether the CURRENTLY-ANIMATED leg (not the held one - there
        /// is nothing held yet) is a plausible standing pose to capture from.
        /// See CaptureHeld and CaptureReachMax.</summary>
        private static bool ReachSane(Skeleton skeleton, VrLegChain leg)
        {
            if (!leg.Valid)
                return true;

            float span = leg.ThighLen + leg.CalfLen;
            if (span < 1e-4f)
                return true;

            MatrixFrame thigh = skeleton.GetBoneEntitialFrameWithIndex(leg.Thigh);
            MatrixFrame foot = skeleton.GetBoneEntitialFrameWithIndex(leg.Foot);

            return (foot.origin - thigh.origin).Length / span <= CaptureReachMax;
        }

        private static bool Finite(MatrixFrame f)
        {
            return Finite(f.origin) && Finite(f.rotation.s)
                && Finite(f.rotation.f) && Finite(f.rotation.u);
        }

        private static bool Finite(Vec3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        private static void LogOnce()
        {
            if (_logged)
                return;

            _logged = true;
            VrLog.Info("TEST 253: the full-body IK is running. The pelvis and the chest "
                       + "follow your own body offset, the legs are solved to the engine's "
                       + "own animated feet, and the head and neck are left to the animation "
                       + "(the camera does not read them). body_ik = 0 turns it off; "
                       + "hide_body = 1 stands it down with the body.");
        }

        private static void Trace(Vec3 offset, Vec3 pelvisDelta, Vec3 chestDelta)
        {
            if (!Debug)
                return;

            if (++_debugTick < 90)
                return;
            _debugTick = 0;

            VrLog.Info(string.Format(
                "Body IK: offset ({0:F2},{1:F2},{2:F2}) -> pelvis ({3:F2},{4:F2},{5:F2}), "
                + "chest ({6:F2},{7:F2},{8:F2}) m in body axes (right, forward, up); "
                + "leg reach {9:F2} (1.00 = the hip is exactly a leg's length from the "
                + "ankle).",
                offset.x, offset.y, offset.z,
                pelvisDelta.x, pelvisDelta.y, pelvisDelta.z,
                chestDelta.x, chestDelta.y, chestDelta.z,
                _legReach));
        }

        public static void Reset()
        {
            _haveSmoothed = false;
            _smoothed = Vec3.Zero;
            _lastTick = 0;
            _debugTick = 0;
            _logged = false;
            _heldValid = false;
            _recaptureHeld = false;
            _heldSkeleton = UIntPtr.Zero;
            _pendingSkeleton = UIntPtr.Zero;

            // Without these two, a bad capture on the SECOND mission of a
            // session prints nothing - both lines are guarded to print once
            // per process, and TEST 267's actual failure was exactly that: a
            // stretched-leg capture with no diagnostic anywhere near it,
            // because the first mission had already used up the log line.
            _loggedHeld = false;
            _loggedEyeFollow = false;

            VrHeadBody.Reset();
            VrBodyBones.Reset();
            VrBodyFreeze.ForgetIdle();
        }

        /// <summary>Forgets the eased offset so the next frame snaps to the
        /// current one. Called on a recentre, where the current one is zero by
        /// construction: the body should arrive with the view, not glide after it
        /// for the next few frames. The held upper body is re-captured there too -
        /// a recentre is the player saying "this stance is my stance".</summary>
        public static void ForgetOffset()
        {
            _haveSmoothed = false;
            _smoothed = Vec3.Zero;
            _lastTick = 0;
            _recaptureHeld = true;

            // Let the capture diagnostics print again on this recentre's
            // capture too, not just the first one of the process.
            _loggedHeld = false;
            _loggedEyeFollow = false;
        }
    }
}
