using System;
using System.Globalization;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 352/353/354: body_anim = head. The body is solved FROM the headset,
    /// so the two cannot separate; the camera never moves for the body.
    ///
    /// The head bone goes where the eyes are (HeadFrame minus the monster's own
    /// FirstPersonCameraOffsetWrtHead), turned the way the headset is. The neck
    /// takes neck_share of the head's turn, and the chest hangs below it by the
    /// rig's own offsets. The chest's yaw follows the head's, clamped
    /// (body_chest_yaw_deg on foot, mounted_torso_twist_deg in the saddle); its
    /// pitch and roll follow the trunk's chord, so it bends WITH the spine.
    ///
    /// TEST 352c, on foot, B&amp;S-style: the eye's move from standing is split
    /// into a bend about the hips and a crouch (Bend). The trunk tips by the
    /// bend; the pelvis stays over the feet, pitched by body_hip_hinge of a
    /// forward bend, and drops straight down by the crouch. No game crouch
    /// animation. Only what no bend can reach is a step (StepPart), which
    /// VrRoomscale walks; the camera's standing point is the solve's own upright
    /// eye (UprightEye), so standing still the body fits it exactly.
    ///
    /// The spine between them is the existing two-bone solve with the rest
    /// curve as the pole. Out of reach, up to body_neck_slack_m goes into the
    /// neck. The legs solve from the moved hips to the feet (TEST 354 plants
    /// those).
    ///
    /// The camera was anchored before the mission tick, so in the post-tick
    /// entity frame the head lands under it by itself. That is the travel
    /// compensation TEST 325 asked for; no separate term exists.
    ///
    /// Mounted (TEST 353): the rider's placed pelvis and the legs are left as
    /// they are; only spine, neck and head are solved.
    /// </summary>
    internal static class VrHeadBody
    {
        private static readonly float ChestYawFoot = Rad(VrConfig.Float("body_chest_yaw_deg", 45f));
        private static readonly float ChestYawMounted = Rad(VrConfig.Float("mounted_torso_twist_deg", 90f));
        private static readonly float NeckShare = Clamp01(VrConfig.Float("body_neck_share", 0.5f));
        private static readonly float HipHinge = Clamp01(VrConfig.Float("body_hip_hinge", 0.4f));

        /// <summary>TEST 352f: how far the trunk may lean BACK, degrees. The user:
        /// "make chest bend backward less when I move my real body backward" - at
        /// 15 a step back of up to ~0.2 m leaned the chest instead of stepping.
        /// Smaller also means room scale steps the body back sooner.</summary>
        /// TEST 383: 3 (was 5), and now it caps where the spine is AIMED too.
        private static readonly float BackBend = Math.Max(0f, Math.Min(30f, VrConfig.Float("body_back_bend_deg", 3f)));
        private static readonly float PelvisClamp = Math.Max(0f, VrConfig.Float("body_head_pelvis_clamp", 0.65f));

        /// <summary>TEST 383: past the lean-back cap the hips sit back this far
        /// (knees bend, chest upright) before room scale steps the body back.</summary>
        private static readonly float HipBack = Math.Max(0f, Math.Min(0.25f, VrConfig.Float("body_hip_back_m", 0.08f)));

        /// <summary>TEST 382: the first few mm of a head drop are not a crouch
        /// (smoothly): standing, the crouch flickered 0 to 2 mm with tracking noise
        /// and the knees sat on their singular point.</summary>
        private static readonly float CrouchDead = Math.Max(0f, VrConfig.Float("body_crouch_dead_m", 0.015f));
        private static readonly float NeckSlack = Math.Max(0f, VrConfig.Float("body_neck_slack_m", 0.08f));

        /// <summary>TEST 447: the upper body's weight - how long the chest takes to
        /// follow the head's turn (the bend takes half). "The upper chest moves
        /// instant with the HMD": both were rebuilt from the headset every frame
        /// with no state. Rotation only; the head stays on the view. 0 = instant.
        /// The VR panel sets it live.</summary>
        internal static float WeightS { get; set; } =
            Math.Max(0f, Math.Min(0.6f, VrConfig.Float("body_weight_s", 0.25f)));

        private static bool _haveChest;
        private static float _chestYaw, _chestYawV, _chestLagMax;
        private static float _leanX, _leanY, _leanVX, _leanVY;

        /// <summary>TEST 449: the dismount rides the animation like the mount.
        /// HasMount goes false mid-dismount, with the skeleton still at the saddle;
        /// until the animation is done and the feet are down, the body and the view
        /// stay on the saddle path. 0 = the old switch on the HasMount frame.</summary>
        private static readonly bool DismountHold = VrConfig.Bool("dismount_hold", true);
        private const float DismountHoldMaxS = 3f, FallHoldMaxS = 10f;
        private static bool _saddleHold;
        private static long _offAt;

        // TEST 354: plant and step.
        private static readonly bool Plant = VrConfig.Bool("foot_plant", true);
        private static readonly float StepM = VrConfig.Float("foot_step_m", 0.22f);
        private static readonly float StepS = Math.Max(0.05f, VrConfig.Float("foot_step_s", 0.25f));
        private static readonly float LiftM = VrConfig.Float("foot_step_lift_m", 0.08f);
        private static readonly float StepYaw = Rad(VrConfig.Float("foot_step_deg", 45f));
        private const float BlendS = 0.10f;

        private static UIntPtr _rested = UIntPtr.Zero;
        private static Mat3 _restPelvis, _restSpine, _restSpine1, _restSpine2, _restNeck, _restHead;
        private static Vec3 _restChord;      // spine -> spine2, unit, entity space
        private static Vec3 _restCurve;      // spine1 off that chord: the pole

        // TEST 352b: the camera's standing point on foot - the body's own upright
        // eye, in the entity's frame, taken once calm and frozen (TEST 389).
        private static Vec3 _neutral;
        private static Vec3 _neutralFromHips;   // _neutral minus the pelvis it stands on
        private static Vec3 _headFromHips;      // TEST 352e: the standing HEAD bone (the pivot) over the hips
        private static Vec3 _eyeSwing;          // TEST 352e: how far turning the head moved the eye, model space
        private static float _calmSeconds;      // TEST 356b: standing calm since the standing point was taken
        private static bool _haveNeutral;
        private static UIntPtr _neutralSkeleton = UIntPtr.Zero;
        private static float _scale = 1f;       // the agent frame's scale (TEST 356)

        private sealed class Foot
        {
            public bool Planted;
            public MatrixFrame World;
            public bool Stepping;
            public long StepStart;
            public MatrixFrame StepFrom;
            public int Steps;
        }

        private static readonly Foot[] _feet = { new Foot(), new Foot() };
        private static float _plantWeight;
        private static long _lastTick;

        private static bool _logged;
        private static long _traceAt;
        private static float _drop, _hinge, _yaw, _stretch, _residual, _slide;

        // TEST 410: the travel the camera's lead missed (RMS), and mounted, the
        // drawn eye ahead of the view along the travel, by speed: walk, canter, gallop.
        private static float _leadSq;
        private static float _camelEyeMax;
        private static int _leadN;
        private static readonly float[] _ahead = new float[3];
        private static readonly int[] _aheadN = new int[3];

        /// <summary>From VrBodyIk.Apply. Writes the spine, neck and head (and on
        /// foot the pelvis via rootWanted and both legs); returns true when
        /// anything was written, so the caller rebuilds.</summary>
        internal static bool Apply(Agent agent, MBAgentVisuals visuals, Skeleton skeleton, VrSpineChain spine,
                                   VrLegChain left, VrLegChain right, ref MatrixFrame rootWanted)
        {
            Monster monster = agent.Monster;
            if (!VrCameraDriver.HasPose || monster == null || !spine.Valid || spine.Pelvis < 0
                || spine.Neck < 0 || spine.Head < 0)
                return false;

            Rest(skeleton, spine);
            bool feetDown = FeetDown(skeleton, left, right);   // read before any write
            TrackSaddle(agent, feetDown);
            bool mounted = Saddled(agent);
            if (mounted)
            {
                _haveLean = false;    // TEST 447: the saddle has no bend; start again on foot

                // TEST 449: the feet planted before the ride stayed planted THERE,
                // at plant weight 1 (mounting is done standing still), so the first
                // frame off the horse reached both legs back to where the player got
                // on. Off the horse they plant afresh, blended in from the animation.
                _plantWeight = 0f;
                foreach (Foot f in _feet)
                    f.Planted = f.Stepping = false;
            }

            // ---- the animation, all of it, before any write --------------------
            // Bone frames carry the character's scale, and the local offsets below
            // are in those scaled frames, so every rotation built here gets its
            // bone's scale back (352b's standing point used the unscaled rest pose
            // and sat ~0.2 m out of the live body's reach).
            MatrixFrame pA = skeleton.GetBoneEntitialFrameWithIndex(spine.Pelvis);
            MatrixFrame sA = skeleton.GetBoneEntitialFrameWithIndex(spine.Spine);
            MatrixFrame s1A = skeleton.GetBoneEntitialFrameWithIndex(spine.Spine1);
            MatrixFrame s2A = skeleton.GetBoneEntitialFrameWithIndex(spine.Spine2);
            MatrixFrame nA = skeleton.GetBoneEntitialFrameWithIndex(spine.Neck);
            MatrixFrame hA = skeleton.GetBoneEntitialFrameWithIndex(spine.Head);
            var chain = new Chain
            {
                PelvisToSpine = pA.TransformToLocalNonOrthogonal(sA).origin,
                ChestToNeck = s2A.TransformToLocalNonOrthogonal(nA).origin,
                NeckToHead = nA.TransformToLocalNonOrthogonal(hA).origin,
                ChordLen = (s2A.origin - sA.origin).Length,
                Eye = monster.FirstPersonCameraOffsetWrtHead,
                KP = pA.rotation.GetScaleVector(),
                KS = sA.rotation.GetScaleVector(),
                KS1 = s1A.rotation.GetScaleVector(),
                KS2 = s2A.rotation.GetScaleVector(),
                KN = nA.rotation.GetScaleVector(),
                KH = hA.rotation.GetScaleVector(),
            };

            bool idle = !Moving(agent);
            float dt = Dt();
            if (!mounted)
            {
                Neutral(agent, skeleton, pA, chain, idle, dt, feetDown);

                // TEST 356: the character scaled to the player's height, from the
                // body's standing eye in model space (the engine scales the frame,
                // not the bones, so this does not move with the scale).
                if (_haveNeutral && _calmSeconds >= 1f)
                    VrBodyHeight.Tick(agent, _neutral.z);
            }

            // ---- the head, from the headset, in the entity's frame ---------------
            // The agent's scale lives on this frame (TEST 356 scales it to the
            // player), and the bones are in its unscaled space: world metres are
            // divided by it on the way in.
            MatrixFrame agentFrame = visuals.GetFrame();
            Mat3 er = agentFrame.rotation;
            float k = Math.Max(1e-3f, er.s.Length);
            _scale = k;
            er.Orthonormalize();

            // TEST 389: the level head's eye offset from the pivot, world metres.
            Vec3 eyeFromPivot = Sc(_restHead, chain.KH).TransformToParent(chain.Eye) * k;
            if (eyeFromPivot.Length > 0.05f && eyeFromPivot.Length < 0.30f)
                EyeFromPivot = eyeFromPivot;

            // On foot the anchor is the body's own standing eye (TryNeutralEyeWorld),
            // which reads no bone written here, so the eyes are taken as they are. In
            // the saddle the anchor is the game's camera off the head bone - possibly
            // the one written here last frame - so the target is the animated eye
            // plus the player's own offset from the recentred neutral (RoomOffset).
            // 353 used HeadFrame - Anchor, which carries the headset's height in the
            // room: the rider's head target sat 1.7 m up ("head missed by 1.7 m").
            // TEST 352d: plus the head offset the camera's anchor carries (the VR
            // tab's, and head_offset_mounted_* in the saddle: 0.10 back) - without it
            // the rider's head sat ahead of the camera, "chest very close to my face".
            // TEST 414: minus the animation's upper-body lean (equip, swing, the
            // gallop's); the camera drops its own (CameraLean, TEST 415).
            Vec3 animEye = hA.TransformToParent(chain.Eye);
            Vec3 lean = mounted ? SaddleLean(agent, pA, animEye, dt) : Vec3.Zero;
            _pelvisBone = spine.Pelvis;
            bool camelSaddle = VrCameraDriver.CamelSaddleActive(agent);
            Vec3 eye = mounted
                ? animEye - lean
                  + er.TransformToLocal(VrCameraDriver.HeadOffsetWorld
                                        + VrCameraDriver.RoomRotation.TransformToParent(VrCameraDriver.RoomOffset)) * (1f / k)
                : er.TransformToLocal(VrCameraDriver.HeadFrame.origin - agentFrame.origin) * (1f / k);
            if (camelSaddle)
                eye = RenderedEyeTarget(agentFrame, VrCameraDriver.HeadFrame.origin);
            Mat3 dHead = Relative(er, VrCameraDriver.HeadFrame.rotation);
            Mat3 headRot = Sc(VrBodyIk.Compose(dHead, _restHead), chain.KH);
            Vec3 h = eye - headRot.TransformToParent(chain.Eye);
            _eyeSwing = headRot.TransformToParent(chain.Eye) - Sc(_restHead, chain.KH).TransformToParent(chain.Eye);

            float limit = mounted ? ChestYawMounted : ChestYawFoot;
            float yaw = Math.Max(-limit, Math.Min(limit, WeightedYaw(er, YawOf(dHead), dt)));
            Mat3 yawRot = Yaw(yaw);
            float reach = spine.LowerLen + spine.UpperLen;

            // ---- TEST 352c, on foot: the head's move from standing split into a
            //      bend about the hips and a crouch (Bend). No game animation. ---
            MatrixFrame p = pA;
            Mat3 dChest = yawRot;
            float crouch = 0f, bend = 0f, slide = 0f;
            Vec3 shift = Vec3.Zero;
            if (!mounted && _haveNeutral)
            {
                // TEST 518 (replaces 514b): the WHOLE drawn body rides the view's stair
                // glide. 514b kept it on the agent, which the engine lifts up stairs in
                // jumps, so under the gliding view it shook and bobbed ("stairs shake my
                // body, it goes down or up"); before that, only the head target glided
                // and the body crouched and stretched. The glide is a shift of hips,
                // spine and feet together, so nothing bends by it.
                Vec3 stair = er.TransformToLocal(new Vec3(0f, 0f, VrCameraDriver.StairShift, -1f)) * (1f / k);

                // TEST 352e: from the head's PIVOT, not the eyes. The eyes sit
                // 0.14 m up and 0.10 m forward of the head bone, so looking down
                // swung them down and forward - and the body crouched under every
                // look at it ("the character moves down when I look down").
                Vec3 standingHead = _neutral - _neutralFromHips + _headFromHips;

                // TEST 451: the step no bend can reach, drawn. Room scale takes it as
                // a teleport before the tick, so standing it is ~0 here - but not
                // while the stick walks (TEST 297c: a teleport restarts the walk
                // cycle), and the body stood still under a moving head until the
                // stick was let go: "body stops following the HMD until I stop
                // moving". So the drawn body (hips, spine, feet) is moved under the
                // head by what is still pending; the frame the stick is released the
                // teleport lands and this drops to ~0 together - no seam.
                if (StepDraw)
                    shift = StepPart(_headFromHips, h - standingHead - stair, HipBack);
                shift += stair;
                Bend(_headFromHips, h - standingHead - shift, PelvisClamp, CrouchDead, HipBack,
                     out bend, out Vec3 dir, out crouch, out slide);
                Vec3 leanDir = dir;
                WeightedBend(ref bend, ref leanDir, dt);   // TEST 447; the crouch and slide stay exact
                Vec3 axis = Vec3.CrossProduct(VrArmBend.Up, leanDir);
                Mat3 tilt = Turn(axis, bend);

                // TEST 352d: the pelvis stands SQUARE - the rest pose's upright
                // orientation, at the eased standing spot - not the idle
                // animation's tilted, swaying one, which bent the spine into an S
                // ("my body is not straight"). The hips hinge with the FORWARD
                // share of a bend only (352b), and go straight down by the crouch.
                // TEST 383: and back by the slide - the hips sitting back.
                Vec3 standing = _neutral - _neutralFromHips + shift;
                float ahead = Math.Max(0f, leanDir.y);
                p.rotation = VrBodyIk.Compose(Turn(axis, bend * HipHinge * ahead), Sc(_restPelvis, chain.KP));
                p.origin = new Vec3(standing.x + dir.x * slide, standing.y + dir.y * slide, standing.z - crouch, -1f);
                dChest = VrBodyIk.Compose(tilt, yawRot);
            }

            Vec3 b = p.TransformToParent(chain.PelvisToSpine);
            Mat3 dNeck = Slerp(dChest, dHead, NeckShare);
            Vec3 n = h - Sc(VrBodyIk.Compose(dNeck, _restNeck), chain.KN).TransformToParent(chain.NeckToHead);
            Vec3 c = n - Sc(VrBodyIk.Compose(dChest, _restSpine2), chain.KS2).TransformToParent(chain.ChestToNeck);

            // Mounted: the hips are the horse's, so the chest leans along the
            // trunk toward the head (two passes settle the weak coupling).
            for (int i = 0; mounted && i < 2; i++)
            {
                dChest = VrBodyIk.Compose(Arc(yawRot.TransformToParent(_restChord), Unit(c - b)), yawRot);
                dNeck = Slerp(dChest, dHead, NeckShare);
                n = h - Sc(VrBodyIk.Compose(dNeck, _restNeck), chain.KN).TransformToParent(chain.NeckToHead);
                c = n - Sc(VrBodyIk.Compose(dChest, _restSpine2), chain.KS2).TransformToParent(chain.ChestToNeck);
            }

            // ---- the spine: the two-bone solve, never stretched -----------------
            // TEST 383: on foot the trunk never leans back past body_back_bend_deg.
            // 352f capped only the chest's TURN; spine and spine1 were still aimed
            // at a chest target behind the hips, so a head moved back arched the
            // lower back anyway. Past the cap the head misses into the neck slack.
            if (!mounted)
                c = CapLean(b, c, Rad(BackBend));

            // Out of reach, the chest is LOWERED to reach, its horizontal offset
            // kept - aiming at an unreachable point tipped the trunk back (352).
            Vec3 cReach = Reachable(b, c, reach);

            Vec3 pole = dChest.TransformToParent(_restCurve);
            if (pole.LengthSquared < 1e-6f)
                pole = -dChest.f;

            if (!VrArmIk.SolveElbowWithBend(b, cReach, pole, spine.LowerLen, spine.UpperLen,
                                            out Vec3 mid, out float _, out float _, VrArmIk.Soft))
                return false;

            Mat3 lower = Slerp(yawRot, dChest, 1f / 3f), upper = Slerp(yawRot, dChest, 2f / 3f);
            MatrixFrame wS = Frame(VrArmIk.Aim(Sc(VrBodyIk.Compose(lower, _restSpine), chain.KS),
                                               spine.SpineAxis, mid - b), b);
            MatrixFrame wS1 = Frame(VrArmIk.Aim(Sc(VrBodyIk.Compose(upper, _restSpine1), chain.KS1),
                                                spine.Spine1Axis, cReach - mid), mid);
            MatrixFrame wS2 = Frame(Sc(VrBodyIk.Compose(dChest, _restSpine2), chain.KS2), cReach);

            Vec3 neckAt = wS2.TransformToParent(chain.ChestToNeck);
            Vec3 gap = n - neckAt;
            float gapLen = gap.Length;
            Vec3 slack = gapLen > NeckSlack && gapLen > 1e-6f ? gap * (NeckSlack / gapLen) : gap;
            MatrixFrame wN = Frame(Sc(VrBodyIk.Compose(dNeck, _restNeck), chain.KN), neckAt + slack);
            MatrixFrame wH = Frame(headRot, wN.TransformToParent(chain.NeckToHead));

            bool wrote = VrBodyIk.SetLocal(skeleton, spine.Spine, p.TransformToLocalNonOrthogonal(wS));
            wrote |= VrBodyIk.SetLocal(skeleton, spine.Spine1, wS.TransformToLocalNonOrthogonal(wS1));
            wrote |= VrBodyIk.SetLocal(skeleton, spine.Spine2, wS1.TransformToLocalNonOrthogonal(wS2));
            wrote |= VrBodyIk.SetLocal(skeleton, spine.Neck, wS2.TransformToLocalNonOrthogonal(wN));
            wrote |= VrBodyIk.SetLocal(skeleton, spine.Head, wN.TransformToLocalNonOrthogonal(wH));

            // ---- on foot: the pelvis (the root; the caller writes it) and legs --
            if (!mounted)
            {
                rootWanted = p;
                _plantWeight = Plant && idle
                    ? Math.Min(1f, _plantWeight + dt / BlendS)
                    : Math.Max(0f, _plantWeight - dt / BlendS);

                wrote |= Leg(skeleton, left, 0, pA, p, agentFrame, idle, shift);
                wrote |= Leg(skeleton, right, 1, pA, p, agentFrame, idle, shift);
            }

            _drop = crouch;
            _hinge = bend;
            _slide = slide;
            _shiftMax = Math.Max(_shiftMax, shift.Length);
            _yaw = yaw;
            _stretch = slack.Length;
            _residual = (h - wH.origin).Length;
            if (camelSaddle)
                _camelEyeMax = Math.Max(_camelEyeMax,
                    (agentFrame.TransformToParent(wH.TransformToParent(chain.Eye)) - VrCameraDriver.HeadFrame.origin).Length);

            // TEST 410: how far the drawn body still leads the view.
            if (VrCameraDriver.AnchorAgentValid)
            {
                Vec3 travel = agent.Position - VrCameraDriver.AnchorAgentPosition;
                // Root travel only; the camel's additional saddle animation is measured by drawnEye below.
                _leadSq += (travel - VrCameraDriver.Lead - VrCameraDriver.ViewFinish).LengthSquared;
                _leadN++;
                float len = travel.Length;
                float speed = VrCameraDriver.FrameDt > 1e-4f ? len / VrCameraDriver.FrameDt : 0f;
                if (mounted && speed > 0.5f)
                {
                    int bin = speed < 3f ? 0 : speed < 7f ? 1 : 2;
                    Vec3 drawnEye = agentFrame.TransformToParent(wH.TransformToParent(chain.Eye));
                    _ahead[bin] += Vec3.DotProduct(drawnEye - VrCameraDriver.HeadFrame.origin, travel * (1f / len));
                    _aheadN[bin]++;
                }
            }

            if (wrote)
                Trace(mounted);
            return wrote;
        }

        /// <summary>The live chain's offsets and bone scales, read once a frame
        /// before any write; the standing point and the solve share them, so the
        /// standing body reaches its own standing eye exactly.</summary>
        private struct Chain
        {
            public Vec3 PelvisToSpine, ChestToNeck, NeckToHead, Eye;
            public float ChordLen;
            public Vec3 KP, KS, KS1, KS2, KN, KH;
        }

        /// <summary>The eye of the upright chain standing on pelvis p: exactly
        /// what Apply builds with the head level and straight.</summary>
        private static Vec3 UprightEye(MatrixFrame p, Chain chain) =>
            UprightHead(p, chain) + Sc(_restHead, chain.KH).TransformToParent(chain.Eye);

        /// <summary>The head bone (the head's pivot) of the same upright chain.</summary>
        private static Vec3 UprightHead(MatrixFrame p, Chain chain)
        {
            Vec3 c = p.TransformToParent(chain.PelvisToSpine) + _restChord * chain.ChordLen;
            Vec3 n = c + Sc(_restSpine2, chain.KS2).TransformToParent(chain.ChestToNeck);
            return n + Sc(_restNeck, chain.KN).TransformToParent(chain.NeckToHead);
        }

        /// <summary>
        /// TEST 352c. The eye's move from standing, split into a bend and a
        /// crouch, B&amp;S-style: the eye turns about the hips (v = standing eye -
        /// hips) by `bend` radians about the horizontal `axis` until it is over the
        /// target, and whatever drop the bend does not explain is a crouch - the
        /// hips go straight down that far (capped). dir is the horizontal way the
        /// eye moved, which the trunk tips toward: up to 85 deg forward, 30 to the
        /// side, body_back_bend_deg back (MaxBend). Pure; the math tests call it.
        ///
        /// TEST 382: the crouch goes through a smooth dead-band (DeadBand).
        /// TEST 383: slide is what the capped bend leaves of a move BACK, up to
        /// maxSlide - the hips sit back that far (weighted by how far back the
        /// move points, so it fades out continuously toward the side).
        /// </summary>
        internal static void Bend(Vec3 v, Vec3 offset, float maxCrouch, float dead, float maxSlide,
                                  out float bend, out Vec3 dir, out float crouch, out float slide)
        {
            bend = 0f;
            slide = 0f;
            dir = VrArmBend.Forward;
            float fx = offset.x, fy = offset.y;
            float f = (float)Math.Sqrt(fx * fx + fy * fy);
            float drop = -offset.z;

            if (f > 1e-4f)
            {
                dir = new Vec3(fx / f, fy / f, 0f, -1f);
                float a = v.x * dir.x + v.y * dir.y;             // v ahead along the move
                float r = (float)Math.Sqrt(a * a + v.z * v.z);
                if (r > 1e-3f)
                {
                    float phi = (float)Math.Atan2(a, v.z);
                    float want = (float)Math.Asin(Math.Max(-1f, Math.Min(1f, (a + f) / r)));
                    bend = Math.Max(0f, Math.Min(MaxBend(dir), want - phi));
                    drop -= v.z - r * (float)Math.Cos(phi + bend);

                    float left = a + f - r * (float)Math.Sin(phi + bend);
                    slide = Math.Max(0f, Math.Min(maxSlide, left)) * Math.Max(0f, -dir.y);
                }
            }

            crouch = Math.Min(maxCrouch, DeadBand(drop, dead));
        }

        /// <summary>TEST 382: 0 below 0, x²/2d up to d, then x - d/2 - continuous
        /// with its slope, so the crouch eases in instead of flickering on. Pure.</summary>
        internal static float DeadBand(float x, float dead)
        {
            if (x <= 0f)
                return 0f;
            if (dead <= 1e-6f)
                return x;
            return x < dead ? x * x / (2f * dead) : x - dead * 0.5f;
        }

        /// <summary>TEST 447: a critically damped spring (the SmoothDamp form): x
        /// follows target with no overshoot, about `time` behind a steady move, and
        /// it eases in and out - the weight. time 0 is instant. Pure; the math tests
        /// call it.</summary>
        internal static float Spring(float x, ref float v, float target, float time, float dt)
        {
            if (time <= 1e-4f)
            {
                v = 0f;
                return target;
            }

            float w = 2f / time;
            float k = w * dt;
            float e = 1f / (1f + k + 0.48f * k * k + 0.235f * k * k * k);
            float change = x - target;
            float temp = (v + w * change) * dt;
            v = (v - w * temp) * e;
            float y = target + (change + temp) * e;
            if ((target > x) == (y > target) && y != target)
            {
                v = 0f;          // never past the target in one step
                return target;
            }
            return y;
        }

        /// <summary>TEST 447: the chest's yaw off the entity, following the head's
        /// with weight. Smoothed in the ROOM's frame: a stick turn (or a horse, when
        /// the room turns with it) turns head and room together and must not drag the
        /// chest, and the body's own catch-up turn (VrHeadAim) turns the entity, not
        /// the room, so it must not swing the chest either.</summary>
        private static float WeightedYaw(Mat3 er, float headYaw, float dt)
        {
            if (WeightS <= 0f)
            {
                _haveChest = false;
                return headYaw;
            }

            float room = YawOf(Relative(er, VrCameraDriver.RoomRotation));   // the room in the entity's axes
            float target = Wrap(headYaw - room);                               // the head in the room
            if (!_haveChest)
            {
                _chestYaw = target;
                _chestYawV = 0f;
                _haveChest = true;
            }

            _chestYaw = Wrap(Spring(_chestYaw, ref _chestYawV, _chestYaw + Wrap(target - _chestYaw), WeightS, dt));
            _chestLagMax = Math.Max(_chestLagMax, Math.Abs(Wrap(target - _chestYaw)));
            return Wrap(_chestYaw + room);
        }

        /// <summary>TEST 447: the trunk's bend follows with half the chest's weight.
        /// The lean (dir x bend, entity xy) is what is smoothed, so a bend that
        /// changes direction passes through upright instead of flipping.</summary>
        private static void WeightedBend(ref float bend, ref Vec3 dir, float dt)
        {
            float tx = dir.x * bend, ty = dir.y * bend;
            if (WeightS <= 0f || !_haveLean)
            {
                _leanX = tx;
                _leanY = ty;
                _leanVX = _leanVY = 0f;
                _haveLean = WeightS > 0f;
                return;
            }

            float t = WeightS * 0.5f;
            _leanX = Spring(_leanX, ref _leanVX, tx, t, dt);
            _leanY = Spring(_leanY, ref _leanVY, ty, t, dt);
            float len = (float)Math.Sqrt(_leanX * _leanX + _leanY * _leanY);
            bend = len;
            if (len > 1e-5f)
                dir = new Vec3(_leanX / len, _leanY / len, 0f, -1f);
        }

        private static bool _haveLean;
        private static float _shiftMax;
        private static readonly bool StepDraw = VrConfig.Bool("body_step_draw", true);   // TEST 451

        private static float Wrap(float a)
        {
            const float Tau = (float)(2.0 * Math.PI);
            a %= Tau;
            if (a > Math.PI)
                a -= Tau;
            else if (a < -Math.PI)
                a += Tau;
            return a;
        }

        /// <summary>TEST 383: the spine chord b -> c tipped forward to at most
        /// maxBack radians of lean back (entity space: +y forward, +z up), its
        /// length and sideways part kept. A forward or upright chord is left
        /// exactly as it is. Pure; the math tests call it.</summary>
        internal static Vec3 CapLean(Vec3 b, Vec3 c, float maxBack)
        {
            Vec3 d = c - b;
            float yz = (float)Math.Sqrt(d.y * d.y + d.z * d.z);
            if (yz < 1e-5f || (float)Math.Atan2(-d.y, d.z) <= maxBack)
                return c;
            return b + new Vec3(d.x, -yz * (float)Math.Sin(maxBack), yz * (float)Math.Cos(maxBack), -1f);
        }

        /// <summary>
        /// TEST 352c: the part of the eye's offset from standing that no bend
        /// with crouch can reach from where the body stands - that much the body
        /// has to step (VrRoomscale moves the agent by it). A bend's forward is
        /// bought with height, so a level head moving forward steps at once, and a
        /// head going forward AND down is a bend. Pure; the math tests call it.
        /// TEST 383: a move back first takes the hips' slide (slideCap, weighted
        /// like Bend's), so the two agree on what is left to step.
        /// </summary>
        internal static Vec3 StepPart(Vec3 v, Vec3 offset, float slideCap)
        {
            float fx = offset.x, fy = offset.y;
            float f = (float)Math.Sqrt(fx * fx + fy * fy);
            if (f < 1e-4f)
                return Vec3.Zero;

            Vec3 u = new Vec3(fx / f, fy / f, 0f, -1f);
            float a = v.x * u.x + v.y * u.y;
            float r = (float)Math.Sqrt(a * a + v.z * v.z);
            float z = Math.Min(r, v.z + offset.z);                  // the eye's height over the hips
            float phi = (float)Math.Atan2(a, v.z);
            float reach = Math.Min((float)Math.Sqrt(Math.Max(0f, r * r - z * z)),
                                   r * (float)Math.Sin(Math.Min(Math.PI / 2.0, phi + MaxBend(u))));
            reach += slideCap * Math.Max(0f, -u.y);
            float over = Math.Max(0f, a + f - Math.Max(a, reach));
            return new Vec3(u.x * over, u.y * over, 0f, -1f);
        }

        /// <summary>How far the trunk may tip toward dir: 85 deg forward, 30 to
        /// the side, body_back_bend_deg back (352f: 5, was 15).</summary>
        private static float MaxBend(Vec3 dir)
        {
            float t = Math.Max(-1f, Math.Min(1f, dir.y));
            return t >= 0f ? Rad(30f + 55f * t) : Rad(30f + (BackBend - 30f) * -t);
        }

        /// <summary>VrRoomscale, under the head-driven body: what of the room
        /// offset is a step (the rest is the body bending over its feet).</summary>
        internal static bool TryStepPart(Vec3 roomOffset, out Vec3 step)
        {
            step = roomOffset;
            if (!_haveNeutral)
                return false;
            // The head's pivot, not the eyes (352e): looking down is not a step.
            // Room metres, so the scale back on.
            step = StepPart(_headFromHips * _scale, roomOffset - _eyeSwing * _scale, HipBack * _scale);
            return true;
        }

        // ---- the legs and TEST 354's feet ----------------------------------------

        private static bool Leg(Skeleton skeleton, VrLegChain leg, int i, MatrixFrame pA, MatrixFrame p,
                                MatrixFrame agentFrame, bool idle, Vec3 shift)
        {
            if (!leg.Valid)
                return false;

            MatrixFrame thighA = skeleton.GetBoneEntitialFrameWithIndex(leg.Thigh);
            MatrixFrame footA = skeleton.GetBoneEntitialFrameWithIndex(leg.Foot);
            footA.origin += shift;      // TEST 451: the feet go with the drawn body
            Vec3 hip = p.TransformToParent(pA.TransformToLocalNonOrthogonal(thighA).origin);

            MatrixFrame foot = FootTarget(_feet[i], _feet[1 - i], leg, footA, hip, agentFrame, idle);
            return VrBodyIk.SolveLeg(skeleton, leg, p, hip, foot);
        }

        /// <summary>Where this foot goes: the animation's, blended toward a foot
        /// planted in the WORLD while the stick is idle. z always follows the
        /// animated foot, so the engine's ground (slopes, steps) still holds it.
        /// A planted foot steps - one at a time - when the animation's spot has
        /// drifted foot_step_m away, turned foot_step_deg, or the leg can't
        /// reach it.</summary>
        private static MatrixFrame FootTarget(Foot f, Foot other, VrLegChain leg, MatrixFrame footA, Vec3 hip,
                                              MatrixFrame agentFrame, bool idle)
        {
            MatrixFrame animWorld = agentFrame.TransformToParent(footA);

            if (_plantWeight <= 0f)
            {
                f.Planted = f.Stepping = false;
                return footA;
            }

            if (!f.Planted)
            {
                f.Planted = true;
                f.World = animWorld;
            }

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            MatrixFrame planted = agentFrame.TransformToLocalNonOrthogonal(f.World);

            if (f.Stepping)
            {
                float t = (float)((now - f.StepStart) / (double)System.Diagnostics.Stopwatch.Frequency) / StepS;
                if (t >= 1f)
                {
                    f.Stepping = false;
                    f.World = animWorld;
                }
                else
                {
                    MatrixFrame at = animWorld;
                    at.origin = StepArc(f.StepFrom.origin, animWorld.origin, t, LiftM);
                    at.rotation = Slerp(Ortho(f.StepFrom.rotation), Ortho(animWorld.rotation), Smooth(t));
                    at.rotation.ApplyScaleLocal(animWorld.rotation.GetScaleVector());
                    planted = agentFrame.TransformToLocalNonOrthogonal(at);
                }
            }
            else if (idle)
            {
                Vec3 flat = planted.origin - footA.origin;
                float drift = (float)Math.Sqrt(flat.x * flat.x + flat.y * flat.y);
                float turn = FlatAngle(planted.rotation.f, footA.rotation.f);
                float legLen = leg.ThighLen + leg.CalfLen;

                // TEST 389: only a foot LEFT BEHIND is stepped out of a stretch. A leg
                // stretched straight down (the hips over the feet, standing tall) is
                // not fixed by a step to the same spot - it stepped again at once,
                // forever: 130 steps in 55 s standing still, the knees jumping.
                Vec3 toFoot = planted.origin - hip;
                float outFromHip = (float)Math.Sqrt(toFoot.x * toFoot.x + toFoot.y * toFoot.y);
                float stretch = legLen > 1e-4f && outFromHip > LeftBehind * legLen ? toFoot.Length / legLen : 0f;

                // One foot at a time - unless this leg is already at full stretch,
                // which must never wait (352c: "feet get stretched when moving").
                if (StepDue(drift, turn, stretch, StepM, StepYaw) && (!other.Stepping || stretch > 0.95f))
                {
                    f.Stepping = true;
                    f.StepStart = now;
                    f.StepFrom = f.World;
                    f.Steps++;
                }
            }

            // Ground height from the animation, every frame.
            if (!f.Stepping)
                planted.origin = new Vec3(planted.origin.x, planted.origin.y, footA.origin.z, -1f);

            MatrixFrame result = footA;
            result.origin = footA.origin + (planted.origin - footA.origin) * _plantWeight;
            result.rotation = Slerp(Ortho(footA.rotation), Ortho(planted.rotation), _plantWeight);
            result.rotation.ApplyScaleLocal(footA.rotation.GetScaleVector());
            return result;
        }

        /// <summary>TEST 352b/352c: the camera's standing point - the eye of the
        /// solve's own upright chain on the animated pelvis (UprightEye), taken on a
        /// calm frame (no stick, no crouch, no engine attack, both feet down) and
        /// then held until a recentre (TEST 389), so neither the gait nor the
        /// animation's stance ever moves the camera.</summary>
        private static void Neutral(Agent agent, Skeleton skeleton, MatrixFrame pA, Chain chain, bool idle, float dt,
                                    bool feetDown)
        {
            // TEST 384b: only from a body standing on the ground. A mission that
            // starts in the saddle took its first standing point on the first
            // frame after a dismount, still at saddle height ("2.579 m up"); the
            // body floated there, and TEST 356 then shrank it to fit that eye.
            if (!feetDown)
            {
                _calmSeconds = 0f;
                return;
            }

            // Only from a calm frame: the stick idle, no game crouch, no engine attack.
            // TEST 780: and not on the ground - see Falling.
            if (!idle || agent.CrouchMode || VrMelee.EngineActing(agent) || Falling(agent))
                return;

            MatrixFrame square = pA;
            square.rotation = Sc(_restPelvis, chain.KP);
            Vec3 now = UprightEye(square, chain);
            Vec3 head = UprightHead(square, chain);
            if (!_haveNeutral || _neutralSkeleton != skeleton.Pointer || _recapture)
            {
                _recapture = false;
                _neutral = now;
                _neutralFromHips = now - pA.origin;
                _headFromHips = head - pA.origin;
                _calmSeconds = 0f;
                _haveNeutral = true;
                _neutralSkeleton = skeleton.Pointer;
                VrLog.Info("TEST 352c: on foot the camera's standing point is the solve's own upright eye, "
                           + F(_neutral.y) + " m ahead of the agent and " + F(_neutral.z) + " m up, "
                           + F(_neutralFromHips.z) + " m above the hips.");
                return;
            }

            // TEST 389: and then FROZEN - only a recentre (F10, the panel) takes it
            // again. It used to ease after the animation's pelvis, and a weapon-ready
            // idle stands several cm lower than the bare one: every draw moved the
            // standing point, the camera on it, and TEST 356 re-scaled the character
            // to fit (9 times in a minute, 1.097-1.144) - the body shook, the feet
            // re-stepped and the "height" kept changing.
            _calmSeconds += dt;
        }

        private static bool _recapture;

        /// <summary>TEST 389: an F10 / panel recentre - "this is my stance": the
        /// standing point is taken again on the next calm frame with both feet down.</summary>
        internal static void Recapture()
        {
            _recapture = true;
            _haveSaddle = false;
            _saddleCalm = 0f;   // TEST 661: a fresh 0.5 s of calm, not the last capture's
        }

        /// <summary>
        /// TEST 414. In the saddle the view is the game's first-person camera, built
        /// off the ANIMATED head (MissionScreen.UpdateCamera), and the rider's head
        /// target was that eye too - so every upper-body animation moved the view:
        /// "when I equip my bow on a horse my camera moves like my character is doing
        /// the equip animation". The answer on foot was a standing point the
        /// animation cannot move (TEST 389); in the saddle the hips must still ride
        /// the horse's gait, so the view keeps the pelvis and drops only the head's
        /// move against it: (animated eye - pelvis) - the same offset on a calm frame
        /// (upper body Other/Idle, no engine action, horse under 3 m/s, 0.5 s),
        /// taken once per mount and on a recentre. Before that capture it is zero -
        /// exactly the old view - and at the capture it is zero, so nothing jumps.
        /// Entity space, the skeleton as the animation left it (read before writes).
        /// </summary>
        private static Vec3 SaddleLean(Agent agent, MatrixFrame pA, Vec3 animEye, float dt)
        {
            Agent mount = agent.MountAgent;
            int mountIndex = mount?.Index ?? -1;
            if (!SaddleFromHips || mount == null)
                return Vec3.Zero;

            if (mountIndex != _saddleMount)
            {
                _saddleMount = mountIndex;
                _haveSaddle = false;
                _saddleCalm = 0f;
            }

            Vec3 dev = animEye - pA.origin;
            if (!_haveSaddle)
            {
                bool calm = Calm(agent, 0) && Calm(agent, 1) && !VrMelee.EngineActing(agent)
                            && mount.MovementVelocity.LengthSquared < 9f;
                _saddleCalm = calm ? _saddleCalm + dt : 0f;
                if (_saddleCalm < 0.5f)
                    return Vec3.Zero;

                _saddleBase = dev;
                _haveSaddle = true;
                VrLog.Info("TEST 414: in the saddle the view now rides the hips, not the animated head - the "
                           + "calm riding posture puts the eye " + F(dev.Length) + " m from the pelvis; equips, "
                           + "swings and the gallop's lean no longer move it (mounted_view_from_hips = 0 undoes).");
            }

            return LeanGuard(agent, dev - _saddleBase, ref _leanMax);
        }

        /// <summary>
        /// TEST 441. Both leans used to read ZERO past 0.6 m, "a broken measure (a
        /// mount/dismount), not a lean". A horse's gallop already reads 0.45-0.50 m; a
        /// camel's run goes past 0.6 once a stride, so the correction vanished for
        /// those frames and the view jumped by the whole lean and back: "riding camels
        /// and running breaks camera every second", "jumps and snaps back". The case
        /// the cutoff was for is now named - the engine's Mount and Dismount actions -
        /// and anything else is clamped to mounted_lean_max_m, never dropped, so the
        /// correction cannot jump. The raw size is logged (TEST 414 line).
        /// </summary>
        private static Vec3 LeanGuard(Agent agent, Vec3 lean, ref float rawMax)
        {
            if (Mounting(agent, 0) || Mounting(agent, 1))
                return Vec3.Zero;

            float len = lean.Length;
            rawMax = Math.Max(rawMax, len);
            if (len <= LeanMax)
                return lean;

            _leanClamped++;
            return lean * (LeanMax / len);
        }

        private static bool Mounting(Agent agent, int channel)
        {
            try
            {
                Agent.ActionCodeType type = agent.GetCurrentActionType(channel);
                return type == Agent.ActionCodeType.Mount || type == Agent.ActionCodeType.Dismount;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// TEST 780: "when the horse dies I fall on the ground but the character
        /// stays crouched and the height is wrong". The rider's fall (act_horse_fall_*)
        /// and the get-up after it (act_strike_fall_*_rise) are both actt_fall. Lying
        /// or kneeling there, the feet are down and the stick idle, so FeetDown and
        /// the calm test both passed: a mounted spawn (no standing point yet) or a
        /// saddle recentre (TEST 661, Recapture) took the standing point from the
        /// ground, and TEST 389 then froze it - the camera stayed low and the body
        /// crouched to reach it. Now neither the capture nor the dismount handover
        /// happens until the fall is over.
        /// </summary>
        private static bool Falling(Agent agent)
        {
            try
            {
                return agent.GetCurrentActionType(0) == Agent.ActionCodeType.Fall
                       || agent.GetCurrentActionType(1) == Agent.ActionCodeType.Fall;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static readonly float LeanMax = Math.Max(0.1f, VrConfig.Float("mounted_lean_max_m", 0.9f));
        private static int _leanClamped;

        private static bool Calm(Agent agent, int channel)
        {
            try
            {
                Agent.ActionCodeType type = agent.GetCurrentActionType(channel);
                return type == Agent.ActionCodeType.Other || type == Agent.ActionCodeType.Idle;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// TEST 415: the lean the camera takes out of the game's mounted eye, world
        /// metres - measured from THAT eye, not from the rider's skeleton. 414 took
        /// the skeleton's lean (after the tick) out of the game's camera, but
        /// UpdateCamera builds that camera from the head WITHOUT the game's head-look
        /// turn, which the ticked skeleton has: looking down dipped the skeleton's
        /// eye, not the camera's, so taking the dip out lifted the view ("looking
        /// down makes my camera go up"), and looking around shifted it (414 logged
        /// leans up to 0.45 m). So: the camera's eye back in the entity frame it was
        /// built in (UpdateCamera: visuals frame x the head's entitial eye), against
        /// the pelvis as that same skeleton has it now, less the same offset taken
        /// one frame after the body's calm capture. Exactly what the game's camera
        /// carries comes out, nothing more; all head motion is the headset's, as on foot.
        /// </summary>
        internal static Vec3 CameraLean(Agent agent, Vec3 camWorld)
        {
            MBAgentVisuals visuals = agent?.AgentVisuals;
            Skeleton skeleton = visuals?.GetSkeleton();
            if (!SaddleFromHips || !_haveSaddle || skeleton == null || _pelvisBone < 0
                || _pelvisBone >= skeleton.GetBoneCount())
            {
                _haveSaddleCam = false;
                return Vec3.Zero;
            }

            MatrixFrame vf = visuals.GetFrame();
            Mat3 er = vf.rotation;
            float k = Math.Max(1e-3f, er.s.Length);
            er.Orthonormalize();
            Vec3 dev = er.TransformToLocal(camWorld - vf.origin) * (1f / k)
                       - skeleton.GetBoneEntitialFrameWithIndex((sbyte)_pelvisBone).origin;
            if (!_haveSaddleCam)
            {
                _saddleCamBase = dev;
                _haveSaddleCam = true;
            }

            Vec3 lean = LeanGuard(agent, dev - _saddleCamBase, ref _camLeanMax);
            return er.TransformToParent(lean) * k;
        }

        private static readonly bool SaddleFromHips = VrConfig.Bool("mounted_view_from_hips", true);

        internal static bool TryCamelSaddleEye(Agent agent, out Vec3 eye)
        {
            eye = Vec3.Zero;
            Agent mount = agent?.MountAgent;
            if (!VrBodyIk.HeadDrives || !SaddleFromHips || !_haveSaddle
                || mount?.Monster?.StringId != "camel" || mount.Index != _saddleMount
                || Mounting(agent, 0) || Mounting(agent, 1))
                return false;
            MBAgentVisuals visuals = agent.AgentVisuals;
            if (visuals == null || !visuals.IsValid())
                return false;
            Skeleton skeleton = visuals.GetSkeleton();
            if (skeleton == null || _pelvisBone < 0 || _pelvisBone >= skeleton.GetBoneCount())
                return false;

            // Mounted IK leaves the pelvis untouched; reading it cannot feed our head pose back.
            Vec3 pelvis = skeleton.GetBoneEntitialFrameWithIndex((sbyte)_pelvisBone).origin;
            eye = visuals.GetFrame().TransformToParent(pelvis + _saddleBase);
            return true;
        }

        internal static Vec3 RenderedEyeTarget(MatrixFrame body, Vec3 eyeWorld) =>
            body.TransformToLocalNonOrthogonal(eyeWorld);

        private static Vec3 _saddleBase, _saddleCamBase;
        private static bool _haveSaddle, _haveSaddleCam;
        private static int _saddleMount = -1, _pelvisBone = -1;
        private static float _saddleCalm, _leanMax, _camLeanMax;

        /// <summary>TEST 389: the eyes from the head's pivot (the head bone) with
        /// the head level, in the body's axes (x right, y forward, z up), world
        /// metres - what VrCameraDriver.LevelEye takes out of a looked-down head.
        /// The character's own, once a body has been solved; a typical head before.</summary>
        internal static Vec3 EyeFromPivot { get; private set; } = new Vec3(0f, 0.10f, 0.136f, -1f);

        /// <summary>TEST 384b: the animation has both feet on the ground - each
        /// ankle within FeetDownM of the entity's origin, which is the agent's
        /// ground point. A rider's skeleton is lifted to the saddle by its root
        /// (TEST 206) and stays there for a moment after a dismount; its feet are
        /// then ~1 m up. Read before any write.</summary>
        internal static bool FeetDown(Skeleton skeleton, VrLegChain left, VrLegChain right)
        {
            return left.Valid && right.Valid
                   && Math.Abs(skeleton.GetBoneEntitialFrameWithIndex(left.Foot).origin.z) < FeetDownM
                   && Math.Abs(skeleton.GetBoneEntitialFrameWithIndex(right.Foot).origin.z) < FeetDownM;
        }

        private const float FeetDownM = 0.40f;

        /// <summary>TEST 449: the rider's path this frame - mounted, or a dismount
        /// still under way. The camera's anchor and head offsets and room scale ask
        /// this rather than HasMount, so the view rides the dismount animation down
        /// the way it rides the mount animation up.</summary>
        internal static bool Saddled(Agent agent) =>
            agent != null && (agent.HasMount || (DismountHold && _saddleHold));

        /// <summary>TEST 449: the dismount is this file's to hand over (no TEST 292 recentre).</summary>
        internal static bool HoldsDismount => DismountHold && VrBodyIk.HeadDrives;

        /// <summary>TEST 449: holds the saddle path from the frame HasMount drops
        /// until neither channel plays Mount/Dismount and both feet are down (384b:
        /// never a timer alone). A fall from a dying horse plays no Dismount and may
        /// land anywhere, so 3 s ends it regardless.</summary>
        private static void TrackSaddle(Agent agent, bool feetDown)
        {
            if (!DismountHold || agent.HasMount)
            {
                _saddleHold = DismountHold && agent.HasMount;
                _offAt = 0;
                return;
            }

            if (!_saddleHold)
                return;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_offAt == 0)
                _offAt = now;
            float off = (float)((now - _offAt) / (double)System.Diagnostics.Stopwatch.Frequency);
            bool done = feetDown && !Mounting(agent, 0) && !Mounting(agent, 1) && !Falling(agent);
            // TEST 780: a fall and its get-up outlast 3 s; the view rides them on the
            // game's camera (as in the saddle) rather than standing up before the body.
            if (!done && (off < DismountHoldMaxS || (off < FallHoldMaxS && Falling(agent))))
                return;

            _saddleHold = false;
            _offAt = 0;
            VrLog.Info("TEST 449: dismount handed over to the standing body after " + F(off) + " s ("
                       + (done ? "the animation is done and the feet are down" : "timed out")
                       + "); the view glides onto the standing point (anchor_seam_s).");
        }

        /// <summary>
        /// TEST 390: the agent is walking - the stick, OR its own movement. The
        /// stick alone read idle all the time: by AfterMissionTick the engine has
        /// consumed MovementInputVector, so the feet stayed planted in the world
        /// while the body walked off ("plant weight 1.000" at 2-3 m/s) and dragged,
        /// then swiped to catch up ("feet are dragging and swiping").
        /// </summary>
        internal static bool Moving(Agent agent)
        {
            return agent.MovementInputVector.LengthSquared >= 0.01f
                   || agent.MovementVelocity.LengthSquared > 0.09f;
        }

        // TEST 390's head-target WalkLag is gone: TEST 391 leads the CAMERA instead
        // (VrCameraDriver.WalkLead), so the head target needs no correction.

        /// <summary>TEST 389: a planted foot this far out from under the hip, as a
        /// fraction of the leg, is "left behind" (a standing foot is ~0.1).</summary>
        private const float LeftBehind = 0.35f;

        /// <summary>TEST 352b: the camera's anchor on foot, in the world - false
        /// until a body has been seen, or mounted.</summary>
        internal static bool TryNeutralEyeWorld(Agent agent, out Vec3 world)
        {
            world = Vec3.Zero;
            MBAgentVisuals visuals = agent?.AgentVisuals;
            if (!_haveNeutral || Saddled(agent) || visuals == null || !visuals.IsValid()
                || visuals.GetSkeleton()?.Pointer != _neutralSkeleton)
                return false;

            world = visuals.GetFrame().TransformToParentDouble(_neutral);
            return true;
        }

        /// <summary>The chest target brought within reach of the spine's base by
        /// lowering it, the horizontal offset kept (shortened only if it alone is
        /// past reach). Pure; the math tests call it.</summary>
        internal static Vec3 Reachable(Vec3 b, Vec3 c, float reach)
        {
            Vec3 d = c - b;
            if (d.LengthSquared <= reach * reach)
                return c;

            float flat = (float)Math.Sqrt(d.x * d.x + d.y * d.y);
            if (flat >= reach)
                return b + new Vec3(d.x * reach / flat, d.y * reach / flat, 0f, -1f);

            float up = (float)Math.Sqrt(reach * reach - flat * flat);
            return b + new Vec3(d.x, d.y, d.z >= 0f ? up : -up, -1f);
        }

        /// <summary>Whether a planted foot should step. Pure; the math tests call
        /// it.</summary>
        internal static bool StepDue(float drift, float turnRad, float legStretch, float stepM, float stepRad)
        {
            return drift > stepM || turnRad > stepRad || legStretch > 0.95f;
        }

        /// <summary>A step from one spot to another at t in [0, 1]: smoothstep
        /// along the line, lifted by sin(pi t). Exactly from at 0 and exactly to
        /// at 1. Pure; the math tests call it.</summary>
        internal static Vec3 StepArc(Vec3 from, Vec3 to, float t, float lift)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            Vec3 at = from + (to - from) * Smooth(t);
            return new Vec3(at.x, at.y, at.z + lift * (float)Math.Sin(Math.PI * t), -1f);
        }

        // ---- rest, per skeleton ----------------------------------------------------

        private static void Rest(Skeleton skeleton, VrSpineChain spine)
        {
            if (_rested == skeleton.Pointer)
                return;

            _rested = skeleton.Pointer;
            _haveChest = _haveLean = false;    // TEST 447: a new body starts square under the head
            _restPelvis = Ortho(skeleton.GetBoneEntitialRestFrame(spine.Pelvis, false).rotation);
            MatrixFrame s = skeleton.GetBoneEntitialRestFrame(spine.Spine, false);
            MatrixFrame s1 = skeleton.GetBoneEntitialRestFrame(spine.Spine1, false);
            MatrixFrame s2 = skeleton.GetBoneEntitialRestFrame(spine.Spine2, false);
            _restSpine = Ortho(s.rotation);
            _restSpine1 = Ortho(s1.rotation);
            _restSpine2 = Ortho(s2.rotation);
            _restNeck = Ortho(skeleton.GetBoneEntitialRestFrame(spine.Neck, false).rotation);
            _restHead = Ortho(skeleton.GetBoneEntitialRestFrame(spine.Head, false).rotation);
            _restChord = Unit(s2.origin - s.origin);
            _restCurve = VrArmBend.Perp(s1.origin - s.origin, _restChord);
            if (_restCurve.LengthSquared < 1e-6f)
                _restCurve = -VrArmBend.Forward;
        }

        // ---- small math ---------------------------------------------------------

        /// <summary>The headset's rotation in the entity's axes, rebuilt from its
        /// forward and up so it is a proper rotation with s = f x u, the
        /// entity's own handedness.</summary>
        private static Mat3 Relative(Mat3 entity, Mat3 hmd)
        {
            Vec3 f = Unit(hmd.f);
            Vec3 s = Unit(Vec3.CrossProduct(f, hmd.u));
            Vec3 u = Vec3.CrossProduct(s, f);
            Mat3 r = Mat3.Identity;
            r.s = entity.TransformToLocal(s);
            r.f = entity.TransformToLocal(f);
            r.u = entity.TransformToLocal(u);
            return r;
        }

        /// <summary>Yaw about +z, positive to the left, of a rotation's facing.
        /// Looking straight up or down, the up axis names the facing.</summary>
        private static float YawOf(Mat3 r)
        {
            Vec3 f = r.f;
            if (f.x * f.x + f.y * f.y < 1e-4f)
                f = f.z < 0f ? r.u : -r.u;
            return (float)Math.Atan2(-f.x, f.y);
        }

        private static Mat3 Yaw(float a)
        {
            float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
            Mat3 r = Mat3.Identity;
            r.s = new Vec3(cos, sin, 0f, -1f);
            r.f = new Vec3(-sin, cos, 0f, -1f);
            return r;
        }

        /// <summary>The shortest rotation taking unit a onto unit b.</summary>
        private static Mat3 Arc(Vec3 a, Vec3 b)
        {
            Vec3 axis = Vec3.CrossProduct(a, b);
            float len = axis.Length;
            float dot = Math.Max(-1f, Math.Min(1f, Vec3.DotProduct(a, b)));
            Mat3 r = Mat3.Identity;
            if (len < 1e-6f)
                return r;
            r.RotateAboutAnArbitraryVector(axis * (1f / len), (float)Math.Atan2(len, dot));
            return r;
        }

        /// <summary>A rotation with a bone's scale put back (Aim does the same).</summary>
        private static Mat3 Sc(Mat3 rotation, Vec3 scale)
        {
            rotation.ApplyScaleLocal(scale);
            return rotation;
        }

        /// <summary>A turn of angle radians about a unit axis (right-handed).</summary>
        private static Mat3 Turn(Vec3 axis, float angle)
        {
            Mat3 r = Mat3.Identity;
            if (angle != 0f && axis.LengthSquared > 1e-8f)
                r.RotateAboutAnArbitraryVector(axis.NormalizedCopy(), angle);
            return r;
        }

        private static Mat3 Slerp(Mat3 a, Mat3 b, float t)
        {
            Quaternion qa = Ortho(a).ToQuaternion(), qb = Ortho(b).ToQuaternion();
            if (qa.Dotp4(qb) < 0f)
                qb = qb * -1f;
            Quaternion q = Quaternion.Slerp(qa, qb, t);
            q.SafeNormalize();
            return q.ToMat3();
        }

        private static float Angle(Mat3 r)
        {
            float trace = r.s.x + r.f.y + r.u.z;
            return (float)Math.Acos(Math.Max(-1f, Math.Min(1f, (trace - 1f) * 0.5f)));
        }

        private static float FlatAngle(Vec3 a, Vec3 b)
        {
            float ca = (float)Math.Atan2(a.y, a.x), cb = (float)Math.Atan2(b.y, b.x);
            float d = Math.Abs(ca - cb);
            return d > Math.PI ? (float)(2.0 * Math.PI) - d : d;
        }

        private static Mat3 Ortho(Mat3 m)
        {
            m.Orthonormalize();
            return m;
        }

        private static MatrixFrame Frame(Mat3 rotation, Vec3 origin)
        {
            MatrixFrame f = MatrixFrame.Identity;
            f.rotation = rotation;
            f.origin = origin;
            return f;
        }

        private static Vec3 Unit(Vec3 v) => v.LengthSquared > 1e-12f ? v.NormalizedCopy() : VrArmBend.Up;
        private static float Smooth(float t) => t * t * (3f - 2f * t);
        private static float Rad(float deg) => deg * (float)(Math.PI / 180.0);
        private static float Clamp01(float v) => Math.Max(0f, Math.Min(1f, v));

        private static float Dt()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            float dt = _lastTick == 0 ? 1f / 90f : (float)((now - _lastTick) / (double)System.Diagnostics.Stopwatch.Frequency);
            _lastTick = now;
            return dt <= 0f || dt > 0.25f ? 1f / 90f : dt;
        }

        private static void Trace(bool mounted)
        {
            if (!_logged)
            {
                _logged = true;
                VrLog.Info("TEST 352: the body is solved from the headset (body_anim = head): the head sits at the "
                           + "eyes, the chest bends and turns under it, the hips hinge and drop only for a crouch"
                           + (mounted ? "; in the saddle the hips and legs stay the horse's (TEST 353)" : "")
                           + ". Feet plant while the stick is idle (TEST 354, foot_plant).");
            }

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now < _traceAt)
                return;
            _traceAt = now + 5 * System.Diagnostics.Stopwatch.Frequency;

            VrLog.Info("TEST 352 body" + (mounted ? " (mounted, 353)" : "") + ": pelvis drop " + F(_drop) + " m, hip hinge "
                       + F(_hinge * 57.2958f) + " deg, hips back " + F(_slide) + " m (383), chest yaw " + F(_yaw * 57.2958f) + " deg, neck stretch "
                       + F(_stretch) + " m, head missed by " + F(_residual) + " m; steps L " + _feet[0].Steps
                       + " R " + _feet[1].Steps + ", plant weight " + F(_plantWeight)
                       + (mounted ? "" : "; standing eye " + F(_neutral.y) + " ahead, " + F(_neutral.z) + " up")
                       + "; TEST 410/446 agent travel residual " + F(_leadN > 0 ? (float)Math.Sqrt(_leadSq / _leadN) : 0f)
                       + " m RMS over " + _leadN + " frame(s)"
                       + (mounted ? ", eye ahead of the view: walk " + Ahead(0) + ", canter " + Ahead(1)
                                    + ", gallop " + Ahead(2) + "; TEST 414 animation lean taken out, up to "
                                    + F(_leanMax) + " m from the head, " + F(_camLeanMax) + " m from the view (415)"
                                    + "; TEST 441 clamped to " + F(LeanMax) + " m on " + _leanClamped + " frame(s)"
                                    + (_haveSaddle ? "" : " (no calm capture yet)") : "")
                       + "; camel rendered-eye error max " + F(_camelEyeMax) + " m"
                       + "; TEST 447 chest lagged the head by up to " + F(_chestLagMax * 57.2958f) + " deg (weight "
                       + F(WeightS) + " s); TEST 451 body drawn up to " + F(_shiftMax) + " m under a head the step had not reached.");
            _chestLagMax = 0f;
            _camelEyeMax = 0f;
            _shiftMax = 0f;
            _leanMax = 0f;
            _camLeanMax = 0f;
            _leanClamped = 0;
            _leadSq = 0f;
            _leadN = 0;
            for (int i = 0; i < 3; i++)
            {
                _ahead[i] = 0f;
                _aheadN[i] = 0;
            }
        }

        private static string Ahead(int bin)
        {
            return _aheadN[bin] > 0 ? F(_ahead[bin] / _aheadN[bin]) + " m (" + _aheadN[bin] + ")" : "-";
        }

        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

        internal static void Reset()
        {
            _camelEyeMax = 0f;
            _rested = UIntPtr.Zero;
            _haveNeutral = false;
            _haveSaddle = false;
            _saddleCalm = 0f;   // TEST 661: else the next battle captures on its spawn frame
            _saddleMount = -1;
            _pelvisBone = -1;
            _recapture = false;
            _saddleHold = false;
            _offAt = 0;
            _haveChest = _haveLean = false;
            _neutralSkeleton = UIntPtr.Zero;
            _logged = false;
            _plantWeight = 0f;
            _lastTick = 0;
            foreach (Foot f in _feet)
            {
                f.Planted = f.Stepping = false;
                f.Steps = 0;
            }
        }
    }
}
