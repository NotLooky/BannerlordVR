using System;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 234. Holds the chest and shoulders still while the body animates.
    ///
    /// WHY
    ///
    /// The arms are solved from a shoulder that the animation moves. Walking,
    /// running, swinging, blocking and being hit all rock, lean and twist the
    /// spine, so the shoulders swing about and the elbows and the visible chest go
    /// with them, while the hands - which are ours, from the controllers - stay put.
    /// The player sees the arms and the chest move for reasons that have nothing to
    /// do with them.
    ///
    /// WHAT
    ///
    /// The three spine bones' ORIENTATION is replaced, each tick and before the arm
    /// solve reads the shoulders, by the skeleton's rest orientation: upright and
    /// square to the agent, which faces where the player looks. The correction is
    /// eased in along the spine (a third at the lowest bone, all of it at the
    /// chest) so a twist is spread over three segments rather than pinched into
    /// one. Both clavicles are put back to their rest frames, so the shoulder sits
    /// at a fixed place on the chest and no shrug or arm-raise from the animation
    /// reaches it.
    ///
    /// WHAT IS LEFT ALONE, AND WHY
    ///
    /// * Bone POSITIONS. Each spine bone keeps the position the animation gave it,
    ///   so the chest still rises and falls with the head. The camera is built off
    ///   the head bone (VrGameCamera) and follows that motion; removing it from the
    ///   chest alone would make the chest slide against the view. Only rotation -
    ///   the lean, twist and rock - is removed.
    /// * The pelvis and the legs. They keep animating and walking.
    /// * The neck and head. They hang off the chest and move with its correction,
    ///   but the camera has already been read from them this frame.
    ///
    /// It runs from VrWeaponHands, before the arm solve and after the rebuild that
    /// puts the root's placement back, because the arm solve reads the shoulder from
    /// the skeleton's entitial frames and those must already show the pinned chest.
    ///
    /// body_pin = 0 turns it off; body_pin_strength (0 to 1) blends it; and
    /// body_pin_shoulders = 0 leaves the clavicles to the animation.
    /// </summary>
    public static class VrUpperBody
    {
        public static bool Enabled { get; } = VrConfig.Bool("body_pin", true);

        private static readonly float Strength =
            Math.Max(0f, Math.Min(1f, VrConfig.Float("body_pin_strength", 1f)));

        private static readonly bool PinShoulders = VrConfig.Bool("body_pin_shoulders", true);

        /// <summary>
        /// TEST 250: while the engine is in an attack action, the chest's own
        /// POSITION is held where it was the instant the swing began.
        ///
        /// WHY. The engine's attack animation leans and lunges the torso forward.
        /// That animation is not something the player watches any more - the arms
        /// and the blade are the player's own, and the damage is the blade's
        /// (TEST 248) - but with the body visible it is the thing that says "my
        /// character is doing an attack animation", and it moves the camera
        /// anchor with it. The rotations are already pinned (TEST 234); this pins
        /// the position for the length of the swing, and lets go the moment the
        /// action ends. Walking, riding and every other animation keep the
        /// animation's own positions, as before.
        ///
        /// The chest counter-holds against the pelvis: the hips may still turn
        /// with the animation, the chest above them stays where it was, which is
        /// what a planted stance looks like.
        /// </summary>
        private static readonly bool HoldAttackPositions =
            VrConfig.Bool("melee_freeze_body", true);

        /// <summary>
        /// TEST 294. How far the chest may turn toward the head in the saddle.
        ///
        /// Mounted, the rider faces the horse, and the view does not turn with it:
        /// TEST 290's log had the view's anchor 55-133 deg off the rider's entity
        /// for minutes of riding. The hands come from where the player faces; the
        /// shoulders came from a chest square to the horse, so every reach was
        /// across or behind the body and the elbows twisted. The chest now turns
        /// toward the head's yaw, as a rider twists in the saddle, capped here.
        /// The hips stay with the horse. 0 leaves the chest square to the horse.
        /// </summary>
        private static readonly float MountedTwist =
            Math.Max(0f, Math.Min(150f, VrConfig.Float("mounted_torso_twist_deg", 90f))) * (float)(Math.PI / 180.0);

        /// <summary>TEST 294. The same twist on foot, where the body-turn deadzone
        /// (TEST 291) can leave the body up to 90 deg off the head. Off by default:
        /// the player asked for less body turning there.</summary>
        private static readonly bool TwistOnFoot = VrConfig.Bool("torso_twist_on_foot", false);

        private static bool _loggedTwist;

        /// <summary>
        /// TEST 295. With a shield held on foot, the chest stands upright over the
        /// pelvis instead of riding the stance's lean.
        ///
        /// The shield stance pitches the pelvis forward. The pin above straightens
        /// the spine's ROTATION but keeps the animation's POSITIONS, and those hang
        /// off the pitched pelvis - so the chest and head were still carried forward
        /// and down, and the camera, which is the game's animated eye, went with them.
        /// Here the spine's positions are rebuilt from the rest pose's own offsets on
        /// an upright pelvis, and the head's resulting move is handed to the camera
        /// (HeadShiftWorld) so the view sits on the head the player actually has.
        /// </summary>
        private static readonly bool ShieldUpright = VrConfig.Bool("shield_upright", true);

        private static readonly Vec3[] _restLocalOrigin = new Vec3[SpineCount];
        private static Mat3 _pelvisRestRot = Mat3.Identity;
        private static Vec3 _pelvisRestOrigin;
        private static bool _pelvisIsRoot;
        private static sbyte _head = -1;
        private static Vec3 _animatedHead;
        private static bool _loggedUpright;
        private static long _shiftStamp;
        private static float _shiftBlend;
        private static Vec3 _lastShift;
        private static int _shiftTick;

        /// <summary>TEST 295. True while this tick stood the chest upright.</summary>
        public static bool UprightActive { get; private set; }

        /// <summary>
        /// TEST 295c. The pelvis (the skeleton's root) as the rest pose has it -
        /// facing forward, over the agent - at the animation's height, while the
        /// shield stance is being stood upright. 295 straightened only the spine:
        /// the stance also turns and pushes the hips, and with the head pulled back
        /// onto an upright chest (the log: 8-17 cm) the hips and legs sat forward of
        /// the view - "the body comes forward". VrWeaponHands writes this as the root
        /// instead of the animation's, so the body stands like the sword stance.
        /// </summary>
        public static bool HasUprightRoot { get; private set; }
        public static MatrixFrame UprightRoot { get; private set; }

        /// <summary>TEST 295. How far standing the chest upright moved the head,
        /// in world space, from the last tick. The camera adds it to the game's
        /// animated eye. Zero whenever UprightActive is false.</summary>
        public static Vec3 HeadShiftWorld { get; private set; }

        private static readonly MatrixFrame[] _heldSpine = new MatrixFrame[SpineCount];
        private static bool _haveHeldSpine;
        private static bool _loggedHold;

        private const int SpineCount = 3;

        // Spine1, Spine2 and Thorax are HumanBone's names for the bones the skeleton
        // calls spine, spine1 and spine2: the game's own role map rather than a name
        // search, the way VrArmBones finds the arm.
        private static readonly HumanBone[] SpineRoles =
            { HumanBone.Spine1, HumanBone.Spine2, HumanBone.Thorax };

        private static readonly sbyte[] _spine = new sbyte[SpineCount];
        private static readonly Quaternion[] _restRot = new Quaternion[SpineCount];
        private static readonly sbyte[] _clavicle = { -1, -1 };
        private static readonly MatrixFrame[] _clavicleRest = new MatrixFrame[2];

        // TEST 235. The four skinning helpers that sit between the shoulder, elbow and
        // wrist (upperarm_twist1 and foretwist1, each side). They carry the animation's
        // twist, so they are put back to their rest frames too; the arm solve sets the
        // joints either side of them and would otherwise compose the animation's twist
        // back in.
        private static readonly bool PinTwist = VrConfig.Bool("arm_roll_pin", true);
        private const int HelperCount = 4;
        private static readonly HumanBone[] HelperRoles =
        {
            HumanBone.UpperarmTwist1L, HumanBone.Forearm1L,
            HumanBone.UpperarmTwist1R, HumanBone.Forearm1R,
        };
        private static readonly sbyte[] _helper = { -1, -1, -1, -1 };
        private static readonly MatrixFrame[] _helperRest = new MatrixFrame[HelperCount];

        private static UIntPtr _resolvedFor = UIntPtr.Zero;
        private static bool _resolved;
        private static bool _broken;
        private static bool _loggedOn;

        // The most the animation moved the chest, over a window, so the log can say
        // the pin is doing something and how much.
        private static float _maxDeviation;
        private static long _windowStart;

        /// <summary>
        /// Pins the spine and shoulders in the skeleton's LOCAL frames. Returns true
        /// when it wrote anything; the caller must then rebuild the entitial frames
        /// (with the root's placement put back first) before anything reads them.
        /// </summary>
        public static bool Pin(MBAgentVisuals visuals, Skeleton skeleton)
        {
            if (!Enabled || _broken || Strength <= 0f || visuals == null || skeleton == null)
                return false;

            try
            {
                if (!Resolve(visuals, skeleton))
                    return false;

                // Everything is read from the animation's frames before anything is
                // written: the entitial cache does not follow a write until it is
                // rebuilt, so a mid-way read would return the animation's value for
                // one bone and ours for the next.
                sbyte root = skeleton.GetParentBoneIndex(_spine[0]);
                MatrixFrame parent = skeleton.GetBoneEntitialFrameWithIndex(root);

                var locals = new MatrixFrame[SpineCount];
                float deviation = 0f;

                // TEST 250: hold the chest's position through an engine attack.
                // The agent comes from the mission rather than the caller so the
                // signature does not grow for a question the mission can answer.
                //
                // TEST 266 fix: this used to also require VrBodyHide.Hiding, back
                // when VrBodyFreeze's own call site did too - the two were tied
                // together, so a visible body took neither path and nothing here
                // clobbered anything. TEST 266 freed VrBodyFreeze to run with a
                // visible body but left THIS gate behind, and the ordering in
                // VrWeaponHands (VrBodyFreeze.Apply, then VrUpperBody.Pin, then
                // one entitial rebuild at the end) meant Pin ran second, read the
                // STALE pre-rebuild entitial frame - the raw mid-swing animated
                // pose, not what Freeze had just written - and wrote that back
                // over the spine/chest bones Freeze had just frozen. The chest
                // visibly kept moving through the swing even though the freeze
                // itself was working everywhere else. Both gates now agree.
                Agent agent = Mission.Current?.MainAgent;
                bool attacking = HoldAttackPositions
                                 && agent != null && VrMelee.EngineActing(agent);

                if (!attacking)
                    _haveHeldSpine = false;
                else if (!_haveHeldSpine && !_loggedHold)
                {
                    _loggedHold = true;
                    VrLog.Info("TEST 250: the body no longer plays the engine's attack animation. "
                               + "While the engine is in an attack action the chest holds the "
                               + "position it had when the swing began - the hips and the legs "
                               + "keep their gait, the arms and the blade are still yours, and "
                               + "the attack state still runs for the AI and the sound. "
                               + "melee_freeze_body = 0 restores the animation.");
                }

                float twist = TorsoTwist(agent, visuals);

                // TEST 352: never under the head-driven body - it owns the pelvis
                // and the head, and the camera must not be handed a head shift.
                bool upright = ShieldUpright && !attacking && agent != null && !agent.HasMount
                               && _head >= 0 && HoldsShield(agent) && !VrBodyIk.HeadDrives;
                MatrixFrame uprightPelvis = parent;
                HasUprightRoot = false;
                if (upright)
                {
                    uprightPelvis.rotation = _pelvisRestRot;

                    // TEST 295c: the pelvis is the root on this rig, so it can be
                    // stood up too - forward-facing, over the rest spot, at the
                    // animation's height so the walk's bob and a crouch still show.
                    if (_pelvisIsRoot)
                    {
                        uprightPelvis.origin = new Vec3(_pelvisRestOrigin.x, _pelvisRestOrigin.y,
                                                        parent.origin.z, -1f);
                        UprightRoot = uprightPelvis;
                        HasUprightRoot = true;
                        parent = uprightPelvis;   // the spine is written against it
                    }
                    _animatedHead = skeleton.GetBoneEntitialFrameWithIndex(_head).origin;

                    if (!_loggedUpright)
                    {
                        _loggedUpright = true;
                        VrLog.Info("TEST 295: a shield is held on foot, so the chest stands upright "
                                   + "over the pelvis instead of leaning with the stance, and the view "
                                   + "follows the upright head. shield_upright = 0 restores the lean.");
                    }
                }
                UprightActive = upright;

                for (int k = 0; k < SpineCount; k++)
                {
                    MatrixFrame animated = skeleton.GetBoneEntitialFrameWithIndex(_spine[k]);

                    // The animation's position, the pinned orientation - except
                    // during an attack, where the position is the held one.
                    MatrixFrame desired = animated;

                    // TEST 295: or the rest pose's offset from an upright parent.
                    if (upright)
                        desired.origin = (k == 0 ? uprightPelvis : parent)
                                         .TransformToParent(_restLocalOrigin[k]);

                    if (attacking)
                    {
                        if (!_haveHeldSpine)
                            _heldSpine[k] = animated;

                        desired = _heldSpine[k];
                    }

                    Mat3 rotation = desired.rotation;
                    rotation.Orthonormalize();

                    Quaternion from = rotation.ToQuaternion();
                    Quaternion to = twist != 0f ? YawAboutUp(_restRot[k], twist) : _restRot[k];

                    // The short way round.
                    if (from.Dotp4(to) < 0f)
                        to = to * -1f;

                    // Upright (TEST 295c): all of it, at every bone - the stance's twist
                    // must not survive in the lower spine.
                    float weight = upright ? Strength : Strength * (k + 1) / SpineCount;
                    Quaternion blended = Quaternion.Slerp(from, to, weight);
                    blended.SafeNormalize();

                    if (k == SpineCount - 1)
                    {
                        float dot = Math.Min(1f, Math.Abs(from.Dotp4(to)));
                        deviation = (float)(2.0 * Math.Acos(dot) * 180.0 / Math.PI);
                    }

                    // The animation's position (or the held one), with the pinned
                    // orientation.
                    desired.rotation = blended.ToMat3();

                    // The parent is the bone just before this one in the chain, at
                    // the frame we are about to give it, not the one the animation
                    // did.
                    MatrixFrame local = parent.TransformToLocalNonOrthogonal(desired);

                    if (!Finite(local))
                        return false;

                    locals[k] = local;
                    parent = desired;
                }

                if (attacking)
                    _haveHeldSpine = true;

                for (int k = 0; k < SpineCount; k++)
                    skeleton.SetBoneLocalFrame(_spine[k], locals[k]);

                if (PinShoulders)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        if (_clavicle[i] >= 0)
                            skeleton.SetBoneLocalFrame(_clavicle[i], _clavicleRest[i]);
                    }
                }

                if (PinTwist)
                {
                    for (int i = 0; i < HelperCount; i++)
                    {
                        if (_helper[i] >= 0)
                            skeleton.SetBoneLocalFrame(_helper[i], _helperRest[i]);
                    }
                }

                Note(deviation);
                return true;
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("TEST 234: pinning the chest failed; the body goes back to the game's "
                            + "own animation. Nothing else is affected.", ex);
                return false;
            }
        }

        private static bool HoldsShield(Agent agent)
        {
            MissionWeapon off = agent.WieldedOffhandWeapon;
            return !off.IsEmpty && off.CurrentUsageItem != null && off.CurrentUsageItem.IsShield;
        }

        /// <summary>
        /// TEST 295. After the rebuild: where the upright chest put the head, against
        /// where the animation had it, in world space. Called by VrWeaponHands once
        /// the entitial frames show the pin; read by the camera on the next frame.
        /// </summary>
        public static void MeasureHeadShift(Skeleton skeleton, MatrixFrame agentGlobal)
        {
            Vec3 measured = Vec3.Zero;
            Vec3 local = Vec3.Zero;
            bool on = false;
            if (UprightActive && _head >= 0 && skeleton != null)
            {
                local = skeleton.GetBoneEntitialFrameWithIndex(_head).origin - _animatedHead;
                Vec3 world = agentGlobal.rotation.TransformToParent(local);

                // A shift this large is a broken measurement, not a stance.
                if (world.Length < 0.6f)
                {
                    measured = world;
                    on = true;
                }
            }

            // TEST 295b: only the on/off BLEND is eased, so raising or lowering the
            // shield glides. The shift itself is taken at full rate: easing it made
            // it lag the walk's bob, and the view drifted into the chest each step.
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            float dt = _shiftStamp == 0L ? 0.016f
                : (float)((now - _shiftStamp) / (double)System.Diagnostics.Stopwatch.Frequency);
            _shiftStamp = now;
            float k = 1f - (float)Math.Exp(-Math.Min(dt, 0.1f) / 0.15f);
            _shiftBlend += ((on ? 1f : 0f) - _shiftBlend) * k;
            if (on)
                _lastShift = measured;
            HeadShiftWorld = _lastShift * _shiftBlend;

            if (on && ++_shiftTick >= 450)
            {
                _shiftTick = 0;
                VrLog.Info(string.Format(
                    "TEST 295b: standing the chest upright moves the head ({0:F2} right, {1:F2} "
                    + "forward, {2:F2} up) m in the body's frame; the view follows it.",
                    local.x, local.y, local.z));
            }
        }

        /// <summary>
        /// TEST 294. The head's yaw off the rider's entity, clamped, in radians
        /// (positive to the left, about entitial +Z). 0 when not mounted (unless
        /// torso_twist_on_foot) or with no pose.
        /// </summary>
        private static float TorsoTwist(Agent agent, MBAgentVisuals visuals)
        {
            if (agent == null || MountedTwist <= 0f || !VrCameraDriver.HasPose)
                return 0f;

            if (!agent.HasMount && !TwistOnFoot)
                return 0f;

            Vec3 e = visuals.GetGlobalFrame().rotation.f;
            Vec3 h = VrCameraDriver.HeadFrame.rotation.f;

            float angle = (float)Math.Atan2(e.x * h.y - e.y * h.x, e.x * h.x + e.y * h.y);
            float twist = Math.Max(-MountedTwist, Math.Min(MountedTwist, angle));

            if (!_loggedTwist && Math.Abs(twist) > 0.2f)
            {
                _loggedTwist = true;
                VrLog.Info(string.Format(
                    "TEST 294: the chest turns toward the head, {0:F0} deg now (cap {1:F0}, "
                    + "mounted_torso_twist_deg); the hips stay with the {2}.",
                    twist * 57.2957795f, MountedTwist * 57.2957795f, agent.HasMount ? "horse" : "legs"));
            }

            return twist;
        }

        /// <summary>q turned by angle about entitial up (+Z), counter-clockwise
        /// seen from above.</summary>
        private static Quaternion YawAboutUp(Quaternion q, float angle)
        {
            float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
            Mat3 m = q.ToMat3();
            Mat3 r = m;
            r.s = Yaw(m.s, c, s);
            r.f = Yaw(m.f, c, s);
            r.u = Yaw(m.u, c, s);
            r.Orthonormalize();
            return r.ToQuaternion();
        }

        private static Vec3 Yaw(Vec3 v, float c, float s) =>
            new Vec3(v.x * c - v.y * s, v.x * s + v.y * c, v.z, -1f);

        /// <summary>Finds the spine and the clavicles once per skeleton and reads
        /// their rest frames.</summary>
        private static bool Resolve(MBAgentVisuals visuals, Skeleton skeleton)
        {
            if (_resolved && _resolvedFor == skeleton.Pointer)
                return true;

            _resolved = false;
            _resolvedFor = skeleton.Pointer;

            int count = skeleton.GetBoneCount();

            for (int k = 0; k < SpineCount; k++)
            {
                sbyte bone = visuals.GetRealBoneIndex(SpineRoles[k]);
                if (bone < 0 || bone >= count)
                {
                    VrLog.Warn("TEST 234: no " + SpineRoles[k] + " bone on this skeleton, so the "
                               + "chest is left to the animation.");
                    return false;
                }

                _spine[k] = bone;
            }

            // A chain, or it is not one this can pin: each bone must hang off the
            // one before it, and the first off something.
            if (skeleton.GetParentBoneIndex(_spine[0]) < 0
                || skeleton.GetParentBoneIndex(_spine[1]) != _spine[0]
                || skeleton.GetParentBoneIndex(_spine[2]) != _spine[1])
            {
                VrLog.Warn("TEST 234: the spine bones are not a single chain on this skeleton, so "
                           + "the chest is left to the animation.");
                return false;
            }

            for (int k = 0; k < SpineCount; k++)
            {
                Mat3 rest = skeleton.GetBoneEntitialRestFrame(_spine[k], false).rotation;
                rest.Orthonormalize();
                _restRot[k] = rest.ToQuaternion();

                _restLocalOrigin[k] = skeleton.GetBoneLocalRestFrame(_spine[k], false).origin;
            }

            // TEST 295: the pelvis upright, and the head whose move the camera takes.
            sbyte pelvis = skeleton.GetParentBoneIndex(_spine[0]);
            MatrixFrame pelvisRest = skeleton.GetBoneEntitialRestFrame(pelvis, false);
            _pelvisRestRot = pelvisRest.rotation;
            _pelvisRestRot.Orthonormalize();
            _pelvisRestOrigin = pelvisRest.origin;
            _pelvisIsRoot = skeleton.GetParentBoneIndex(pelvis) < 0;

            sbyte head = visuals.GetRealBoneIndex(HumanBone.Head);
            _head = head >= 0 && head < count ? head : (sbyte)(-1);

            HumanBone[] shoulders = { HumanBone.ShoulderL, HumanBone.ShoulderR };
            for (int i = 0; i < 2; i++)
            {
                sbyte bone = visuals.GetRealBoneIndex(shoulders[i]);
                _clavicle[i] = bone >= 0 && bone < count ? bone : (sbyte)(-1);

                if (_clavicle[i] >= 0)
                {
                    _clavicleRest[i] = skeleton.GetBoneLocalRestFrame(_clavicle[i], false);

                    if (!Finite(_clavicleRest[i]))
                        _clavicle[i] = -1;
                }
            }

            for (int i = 0; i < HelperCount; i++)
            {
                sbyte bone = visuals.GetRealBoneIndex(HelperRoles[i]);
                _helper[i] = bone >= 0 && bone < count ? bone : (sbyte)(-1);

                if (_helper[i] >= 0)
                {
                    _helperRest[i] = skeleton.GetBoneLocalRestFrame(_helper[i], false);

                    if (!Finite(_helperRest[i]))
                        _helper[i] = -1;
                }
            }

            _resolved = true;

            VrLog.Info("TEST 234: chest and shoulders are held still. Spine bones ["
                       + _spine[0] + ", " + _spine[1] + ", " + _spine[2] + "] are set to the rest "
                       + "orientation (strength " + Strength.ToString("0.00") + "), clavicles ["
                       + _clavicle[0] + ", " + _clavicle[1] + "] to their rest frames. Positions "
                       + "and the legs still animate. body_pin = 0 turns it off.");

            return true;
        }

        /// <summary>Remembers the worst deviation and says it every few seconds.</summary>
        private static void Note(float deviation)
        {
            if (deviation > _maxDeviation)
                _maxDeviation = deviation;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_windowStart == 0)
                _windowStart = now;

            if ((now - _windowStart) < 5L * System.Diagnostics.Stopwatch.Frequency)
                return;

            _windowStart = now;

            // Only worth a line when the animation actually moved it.
            if (_maxDeviation >= 1f || !_loggedOn)
            {
                _loggedOn = true;
                VrLog.Info("TEST 234: in the last 5 s the animation turned the chest up to "
                           + _maxDeviation.ToString("0.0") + " degrees away from upright; that "
                           + "was taken out.");
            }

            _maxDeviation = 0f;
        }

        private static bool Finite(MatrixFrame f)
        {
            return Finite(f.origin) && Finite(f.rotation.s) && Finite(f.rotation.f)
                   && Finite(f.rotation.u);
        }

        private static bool Finite(Vec3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        public static void Reset()
        {
            _resolved = false;
            _resolvedFor = UIntPtr.Zero;
            _maxDeviation = 0f;
            _windowStart = 0;
            _loggedOn = false;
        }
    }
}
