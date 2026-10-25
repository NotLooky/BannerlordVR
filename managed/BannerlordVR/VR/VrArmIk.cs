using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BannerlordVR.VR
{
    /// <summary>TEST 290. What an arm is doing with a bow out, for VrArmIk.BowPole.</summary>
    public enum ArmRole { None, String, Bow }

    /// <summary>
    /// Two-bone arm IK: puts the elbow somewhere defensible while the wrist sits on
    /// the controller.
    ///
    /// THE GEOMETRY, WHICH IS THE SMALL PART
    ///
    /// With S the shoulder, T the wrist target, a the upper-arm length and b the
    /// forearm length, d = |T - S| and x = (T - S)/d, the law of cosines gives the
    /// angle at the shoulder and the elbow follows:
    ///
    ///     cos A = (a*a + d*d - b*b) / (2*a*d)
    ///     E     = S + a * (cos A * x + sin A * y)
    ///
    /// y is ANY unit vector perpendicular to x. Every such y is a valid solution -
    /// the elbow lives on a circle around the shoulder-to-wrist line - so choosing
    /// y is the entire problem and the trigonometry above is not.
    ///
    /// Choosing y is VrArmBend's job. This file owns the triangle, the bones and
    /// the writing; that one owns the anatomy.
    ///
    /// STRETCH, NOT CLAMP
    ///
    /// When the target is further than a + b, both segments lengthen proportionally
    /// so they sum to d. The cosine law then returns cos A = 1, sin A = 0, and the
    /// arm is exactly straight with the wrist on the target.
    ///
    /// The obvious alternative - clamp d to just under a + b - has a specific
    /// failure worth naming, because it is the more natural thing to write: the
    /// arm can then never reach full extension, so it holds a permanent slight bend
    /// and arms hanging at your sides never straighten.
    ///
    /// NO CONVENTION IS ASSUMED
    ///
    /// Which of a bone's three local axes runs down its length is rig data, so it
    /// is measured from the rest pose (VrArmChain.UpperAxis / ForeAxis) rather than
    /// assumed, and the bones are aimed by applying an explicit shortest-arc
    /// rotation to the animated orientation. Nothing here depends on s, f or u
    /// meaning anything in particular.
    /// </summary>
    public static class VrArmIk
    {
        public static bool Enabled { get; } = VrConfig.Bool("arm_ik", true);

        /// <summary>TEST 450: the arms' length against the character's own, 0.80 to
        /// 1.30 - "my arm to hands are shorter than my real life body". Each bone is
        /// lengthened ALONG ITSELF (Stretch), so the sleeve and the skin lengthen
        /// evenly; moving the joints apart alone (the old arm_length_m, retired)
        /// would thin the elbow and wrist. The VR panel sets it live.</summary>
        public static float ArmScale
        {
            get { return _armScale; }
            set { _armScale = Math.Max(0.8f, Math.Min(1.3f, value)); }
        }

        private static float _armScale = Math.Max(0.8f, Math.Min(1.3f, VrConfig.Float("arm_length_scale", 1f)));

        /// <summary>How strongly the elbow swings out when the hand crosses the
        /// body's midline. 0 leaves the other constraints in place.</summary>
        private static readonly float SwingGain = VrConfig.Float("arm_ik_swing_gain", 1f);

        /// <summary>Time constant for easing the elbow direction, in seconds.</summary>
        private static readonly float SmoothSeconds =
            VrConfig.Float("arm_ik_smooth_ms", 80f) * 0.001f;

        /// <summary>Ceiling on how fast the elbow may swing, degrees per second.</summary>
        private static readonly float RateCapDegPerSec = VrConfig.Float("arm_ik_rate_cap", 500f);

        /// <summary>How much of the rig's wrist-to-grip offset to apply. 1 puts the
        /// grip point on the controller, which is where your hand actually is; 0
        /// puts the wrist there, which is what it did before and reads as the
        /// controller floating at your wrist. Between the two for taste.</summary>
        private static readonly float GripAtItem =
            Clamp01(VrConfig.Float("arm_ik_grip_at_item", 1f));

        /// <summary>How far past the arm's own length a target may sit before it is
        /// treated as wrong rather than as a reach. 2.0 lets a fully extended arm
        /// stretch by its own length again, which is generous, and still rejects
        /// the metres-away targets that turn the mesh into shards.</summary>
        private static readonly float StretchLimit =
            VrConfig.Float("arm_ik_stretch_limit", 2.0f);

        private static int _stretchReports;

        /// <summary>TEST 381. The last fraction of every two-bone reach (arms,
        /// spine, legs) is eased instead of taken straight: near full extension a
        /// millimetre of target noise swung the elbow or knee centimetres (the
        /// offset grows as sqrt(2·L·δ)) - a joint, and the cloth on it, shaking
        /// with the player standing still. 0 is the old straight reach.</summary>
        internal static readonly float Soft =
            Math.Max(0f, Math.Min(0.3f, VrConfig.Float("ik_soft", 0.05f)));

        /// <summary>TEST 381. The distance a two-bone triangle is solved for: d
        /// below reach·(1 - soft), easing toward reach above it and never getting
        /// there (C1 at the seam, so no kink). Pure; the math tests call it.</summary>
        internal static float SoftReach(float d, float reach, float soft)
        {
            float s = soft * reach;
            float start = reach - s;
            if (s <= 1e-5f || d <= start)
                return d;
            return start + s * (1f - (float)Math.Exp(-(d - start) / s));
        }

        /// <summary>TEST 235. Whether each bone's roll is set from the elbow's bend
        /// plane rather than left as the animation had it. arm_roll_pin = 0 goes
        /// back.</summary>
        private static readonly bool RollPin = VrConfig.Bool("arm_roll_pin", true);

        /// <summary>TEST 289. Which way the hand moves when the REST arm bends: forward
        /// on a palm-down T-pose. auto uses the rest pose's own bend when it has one
        /// over 5 degrees. up / down / back are for a rig posed otherwise - a wrong
        /// value shows as the whole arm rolled 90 or 180 degrees.</summary>
        private static readonly string RestFlex =
            (VrConfig.String("arm_rest_flex", "forward") ?? "forward").Trim().ToLowerInvariant();

        private static Vec3 RestFlexDir
        {
            get
            {
                switch (RestFlex)
                {
                    case "up": return VrArmBend.Up;
                    case "down": return -VrArmBend.Up;
                    case "back": return -VrArmBend.Forward;
                    default: return VrArmBend.Forward;
                }
            }
        }

        /// <summary>TEST 270. The forearm twists with the hand and the elbow follows
        /// the hand's roll. arm_twist = 0 goes back to a wrist that takes it all.</summary>
        private static readonly bool Twist = VrConfig.Bool("arm_twist", true);

        /// <summary>The forearm's pronation/supination range either side of neutral.
        /// Roll past it moves the elbow instead, as it does on a real arm.</summary>
        private static readonly float TwistLimit =
            VrConfig.Float("arm_twist_limit_deg", 85f) * (float)(Math.PI / 180.0);

        /// <summary>How much of the in-range roll the elbow takes as well. 0 moves the
        /// elbow only past the limit.</summary>
        private static readonly float TwistElbowShare = VrConfig.Float("arm_twist_elbow_share", 0.3f);

        /// <summary>The forearm helper's share of the wrist's roll. Negative uses where
        /// the rig puts it (VrArmChain.ForeTwistFrac).</summary>
        private static readonly float TwistForeShare = VrConfig.Float("arm_twist_fore_share", -1f);

        // The elbow hinge's axis in each bone's own frame, per side, measured once.
        private static readonly Vec3[] _hingeUpper = new Vec3[2];
        private static readonly Vec3[] _hingeFore = new Vec3[2];
        private static readonly bool[] _haveHinge = new bool[2];

        // TEST 270. The hand-local axis that lay on the hinge normal when measured:
        // what "neutral wrist" means on this arm.
        private static readonly Vec3[] _hingeHand = new Vec3[2];
        private static bool _loggedNoHelper;

        private static float _rollUpperMax;
        private static float _rollForeMax;
        private static long _rollWindow;

        public static bool Debug { get; } = VrConfig.Bool("arm_ik_debug", false);

        private static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }

        private static bool _broken;
        private static bool _logged;
        private static int _debugTick;

        // TEST 453: last frame's raw elbow per side, before and after the roll.
        private static readonly Vec3[] _prevBest = new Vec3[2];
        private static readonly Vec3[] _prevRolled = new Vec3[2];
        private static readonly int[] _flips = new int[2], _rollFlips = new int[2];
        private static readonly Vec3[] _flipAt = new Vec3[2];
        private static long _flipLogAt;

        /// <summary>TEST 453, a probe: a raw elbow direction that swung over 60 deg
        /// in one frame - from the rules (unrolled) or from the wrist roll - with
        /// where the hand was from the shoulder (entity axes: x right, y forward,
        /// z up). Logged every 5 s when anything flipped.</summary>
        // ---- TEST 455: what jumps when the hand "snaps" --------------------------
        private static readonly bool SnapProbe = VrConfig.Bool("arm_snap_probe", true);
        private static readonly Mat3[] _snapUpper = new Mat3[2], _snapFore = new Mat3[2];
        private static readonly Vec3[] _snapElbow = new Vec3[2], _snapHand = new Vec3[2], _snapEased = new Vec3[2];
        private static readonly float[] _snapResidual = new float[2], _snapTwist = new float[2];
        private static readonly bool[] _snapHave = new bool[2];
        private static int _snapLines, _snapCount;
        private static long _snapWindow;

        /// <summary>TEST 455, a probe. 453's flip counter read 0 while the user still
        /// saw the hand snap, so it measures the wrong thing. This compares the
        /// solved arm with last frame's and reports any frame where the forearm or
        /// upper arm turned over 15 deg, the elbow moved over 3 cm, or the wrist's
        /// twist jumped over 30 deg, while the controller moved under 3 cm - an arm
        /// change the hand's own motion does not explain. Each line names the part,
        /// where the hand was from the shoulder (x right, y forward, z up), how near
        /// full reach (d/reach), the raw and eased elbow turn and the arm length.
        /// Up to 8 lines per 5 s, and a count.</summary>
        private static void ProbeSnap(int side, Mat3 upper, Mat3 fore, Vec3 elbow, Vec3 hand, Vec3 shoulder,
                                      float reachFrac, float twist, float residual, float rawTurn, Vec3 eased)
        {
            if (!SnapProbe)
                return;

            upper.Orthonormalize();
            fore.Orthonormalize();
            if (_snapHave[side])
            {
                float handMove = (hand - _snapHand[side]).Length;
                float foreTurn = Turn(_snapFore[side], fore);
                float upperTurn = Turn(_snapUpper[side], upper);
                float elbowMove = (elbow - _snapElbow[side]).Length;
                float twistJump = Math.Abs(residual - _snapResidual[side]) * 57.2958f;
                float easedTurn = _snapEased[side].LengthSquared > 0.5f && eased.LengthSquared > 0.5f
                    ? Deg(_snapEased[side], eased) : 0f;

                if (handMove < 0.03f && (foreTurn > 15f || upperTurn > 15f || elbowMove > 0.03f || twistJump > 30f))
                {
                    _snapCount++;
                    if (_snapLines < 8)
                    {
                        _snapLines++;
                        Vec3 h = hand - shoulder;
                        VrLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "TEST 455 arm snap {0}: forearm turned {1:F0} deg, upper arm {2:F0} deg, elbow moved {3:F3} m, "
                            + "wrist twist jumped {4:F0} deg ({5:F0} -> {6:F0}, hand roll {7:F0}) while the hand moved {8:F3} m; "
                            + "hand at ({9:F2}, {10:F2}, {11:F2}) from the shoulder, {12:F3} of reach; elbow direction turned "
                            + "{13:F0} deg raw, {14:F0} eased; arm length x{15:F2}.",
                            side == 0 ? "R" : "L", foreTurn, upperTurn, elbowMove, twistJump,
                            _snapResidual[side] * 57.2958f, residual * 57.2958f, twist * 57.2958f, handMove,
                            h.x, h.y, h.z, reachFrac, rawTurn, easedTurn, ArmScale));
                    }
                }
            }

            _snapUpper[side] = upper;
            _snapFore[side] = fore;
            _snapElbow[side] = elbow;
            _snapHand[side] = hand;
            _snapResidual[side] = residual;
            _snapTwist[side] = twist;
            _snapEased[side] = eased;
            _snapHave[side] = true;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now >= _snapWindow)
            {
                if (_snapCount > 8)
                    VrLog.Info("TEST 455 arm snap: " + _snapCount + " in the last 5 s (8 shown).");
                _snapWindow = now + 5 * System.Diagnostics.Stopwatch.Frequency;
                _snapLines = 0;
                _snapCount = 0;
            }
        }

        private static float Turn(Mat3 a, Mat3 b)
        {
            float trace = Vec3.DotProduct(a.s, b.s) + Vec3.DotProduct(a.f, b.f) + Vec3.DotProduct(a.u, b.u);
            return (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, (trace - 1f) * 0.5f))) * 57.2958);
        }

        private static float Deg(Vec3 a, Vec3 b)
        {
            float la = a.Length, lb = b.Length;
            if (la < 1e-6f || lb < 1e-6f)
                return 0f;
            return (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, Vec3.DotProduct(a, b) / (la * lb)))) * 57.2958);
        }

        private static void ProbeFlip(int side, Vec3 unrolled, Vec3 final, Vec3 hand)
        {
            const float Cos60 = 0.5f;
            if (_prevBest[side].LengthSquared > 0.5f && Vec3.DotProduct(unrolled, _prevBest[side]) < Cos60)
            {
                _flips[side]++;
                _flipAt[side] = hand;
            }
            else if (_prevRolled[side].LengthSquared > 0.5f && Vec3.DotProduct(final, _prevRolled[side]) < Cos60)
            {
                _rollFlips[side]++;
                _flipAt[side] = hand;
            }

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now < _flipLogAt)
                return;
            _flipLogAt = now + 5 * System.Diagnostics.Stopwatch.Frequency;
            if (_flips[0] + _flips[1] + _rollFlips[0] + _rollFlips[1] == 0)
                return;

            VrLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "TEST 453 elbow flips in 5 s (over 60 deg in one frame, before smoothing): right {0} by the rules, "
                + "{1} by the wrist roll, last with the hand at ({2:F2}, {3:F2}, {4:F2}) m from the shoulder; left {5} / {6}, "
                + "last at ({7:F2}, {8:F2}, {9:F2}). arm_ik_elbow_stick {10:F2}.",
                _flips[0], _rollFlips[0], _flipAt[0].x, _flipAt[0].y, _flipAt[0].z,
                _flips[1], _rollFlips[1], _flipAt[1].x, _flipAt[1].y, _flipAt[1].z, VrArmBend.Stick));
            _flips[0] = _flips[1] = _rollFlips[0] = _rollFlips[1] = 0;
        }

        // Per side, 0 = right, 1 = left. The smoothing filter's whole state.
        private static readonly Vec3[] _prevBend = new Vec3[2];
        private static readonly bool[] _hasPrevBend = new bool[2];
        private static readonly long[] _lastTick = new long[2];

        /// <summary>
        /// Seconds since this arm was last solved.
        ///
        /// Measured here rather than taken from the mission's delta because the arm
        /// is solved from AfterMissionTick and a paused or slowed mission still
        /// moves the player's real hands. A first call, or an implausible gap,
        /// reports a nominal frame so the filter starts sanely instead of jumping.
        /// </summary>
        private static float Dt(int side)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long then = _lastTick[side];
            _lastTick[side] = now;

            if (then == 0)
                return 1f / 90f;

            float dt = (float)((now - then) / (double)System.Diagnostics.Stopwatch.Frequency);
            if (dt <= 0f || dt > 0.25f)
                return 1f / 90f;

            return dt;
        }

        /// <summary>
        /// Solves one arm and writes the upper arm, forearm and hand.
        ///
        /// targetEntitial is the wrist target in the agent entity's frame: origin
        /// where the controller is, rotation the calibrated grip. Returns false if
        /// it wrote nothing, in which case the caller poses the hand as before.
        /// </summary>
        public static bool Solve(Skeleton skeleton, VrArmChain chain, MatrixFrame targetEntitial,
                                 ArmRole role = ArmRole.None)
        {
            if (!Enabled || _broken || skeleton == null || !chain.Valid)
                return false;

            try
            {
                // The animation's pose, read before anything is written. Every
                // frame below is derived from these; none is re-read afterwards,
                // because the entitial frames do not reflect our writes until
                // UpdateEntitialFramesFromLocalFrames runs and a mid-solve read
                // would quietly return last frame's values.
                MatrixFrame oldUpper = skeleton.GetBoneEntitialFrameWithIndex(chain.Upper);
                MatrixFrame oldFore  = skeleton.GetBoneEntitialFrameWithIndex(chain.Fore);
                MatrixFrame oldHand  = skeleton.GetBoneEntitialFrameWithIndex(chain.Hand);

                Vec3 s = oldUpper.origin;
                Vec3 t = targetEntitial.origin;

                // PIN THE GRIP, NOT THE WRIST.
                //
                // The target is where the controller is, and a controller sits in
                // your PALM. The bone being placed is the wrist. Putting the wrist
                // on the controller therefore pushes the whole hand a palm's length
                // past it - visible in game as the controller floating at the wrist
                // with the fingers beyond it.
                //
                // So the wrist is set back along the hand's own axis by the rig's
                // wrist-to-grip offset, which lands the item bone - the point a
                // weapon is held at - exactly on the controller. Rotated by the
                // hand's orientation, so it follows the wrist round rather than
                // being a fixed offset in the body frame that is only right for one
                // pose.
                if (GripAtItem > 0f && chain.ItemLocal.LengthSquared > 1e-8f)
                {
                    Mat3 handRot = targetEntitial.rotation;
                    Vec3 shift = handRot.TransformToParent(chain.ItemLocal);
                    t = t - shift * GripAtItem;
                }

                // TEST 450: the player's arm length.
                float a = chain.UpperLen * ArmScale;
                float b = chain.ForeLen * ArmScale;

                // A TARGET THAT IS NOT A PLACE A HAND CAN BE.
                //
                // Stretching rather than clamping is right for an arm reaching a
                // little past its length, and wrong for a target that has gone
                // wild: the solve happily produces a forearm hundreds of metres
                // long and the mesh becomes the black shards the player
                // screenshotted while paused. VrArmSeam already records one
                // measured case of the target sitting 136 m away before the anchor
                // settled, so this is not hypothetical.
                //
                // Paused is the reliable way to see it. The controller poses and
                // the anchor stop agreeing while the game is not ticking, the
                // target leaves the room, and the arm follows it. Skipping the pose
                // leaves the hand wherever it was, which reads as a frozen arm -
                // an obviously-wrong pose that stays a pose, rather than a smear
                // across the sky.
                float span = (t - s).Length;
                float limit = StretchLimit * (a + b);

                if (limit > 0f && span > limit)
                {
                    if (_stretchReports < 4)
                    {
                        _stretchReports++;
                        VrLog.Warn(string.Format(
                            "Arm IK {0}: target is {1:F2} m from the shoulder against a {2:F2} m "
                            + "arm - {3:F0}x reach. Not posing this frame; the hand stays where "
                            + "it was. A target this far out is the anchor or the controller "
                            + "pose being wrong, not a reach, and stretching to it is what turns "
                            + "the arm into shards. arm_ik_stretch_limit raises the bar.",
                            chain.IsLeft ? "L" : "R", span, a + b, span / (a + b)));
                    }

                    return false;
                }

                // The elbow direction is chosen inside the solve, because the
                // anatomical rules depend on the triangle and the triangle depends
                // on the stretch. See VrArmBend for why it is a pure function of
                // the target, which is the load-bearing part and not a style note.
                float poleWeight;
                Vec3 pole = BowPole(role, chain.IsLeft, s, t, a + b, out poleWeight);

                Vec3 elbow, bend;
                float usedA, usedB;
                int side = chain.IsLeft ? 1 : 0;

                // TEST 453: last frame's elbow, so a tie between two far-apart elbows
                // does not flip it.
                if (!SolveElbow(s, t, a, b, chain.IsLeft, SwingGain,
                                out elbow, out usedA, out usedB, out bend, 0f, pole, poleWeight, Soft,
                                _prevBest[side]))
                    return false;
                Vec3 unrolled = bend;

                Vec3 axis = (t - s).NormalizedCopy();
                Mat3 handTarget = targetEntitial.rotation;

                if (!_haveHinge[side] && (RollPin || Twist))
                    MeasureHinge(side, skeleton, chain);

                // TEST 270. THE ELBOW FOLLOWS THE HAND'S ROLL.
                //
                // Turn your palm down with the arm bent in front of you and the elbow
                // lifts out; turn it up and the elbow tucks in. The forearm covers
                // only about 85 degrees of roll either way, so past that the upper arm
                // rotates and the elbow HAS to move. The solve above placed the elbow
                // from the hand's position alone, so the wrist took all of the roll.
                //
                // So the wrist's roll against that elbow is measured, and the elbow is
                // swung about the shoulder-to-wrist axis by a share of it, plus all of
                // anything past the forearm's range. The rules in VrArmBend still apply
                // after the swing. The roll is the controller's orientation, which is
                // part of the target, so the direction is still a pure function of the
                // target and nothing here reads back the arm's own pose.
                float twist = 0f, roll = 0f;
                if (Twist && _haveHinge[side])
                {
                    Vec3 foreBase = (t - elbow).NormalizedCopy();
                    Vec3 upperBase = (elbow - s).NormalizedCopy();

                    float conf;
                    twist = VrArmBend.WristTwist(handTarget, _hingeHand[side], foreBase,
                                                 HingeNormal(upperBase, foreBase, axis, bend), out conf);

                    // TEST 290: faded at the wrap and where the angle is noise, and
                    // handed to the archery pose wherever that holds the elbow.
                    roll = ElbowRoll(twist, TwistLimit, TwistElbowShare)
                           * VrArmBend.TwistWeight(twist, conf) * (1f - poleWeight);

                    Vec3 rolledElbow, rolledBend;
                    float ra, rb;
                    if (Math.Abs(roll) > 1e-4f
                        && SolveElbow(s, t, a, b, chain.IsLeft, SwingGain,
                                      out rolledElbow, out ra, out rb, out rolledBend, roll,
                                      pole, poleWeight, Soft, _prevBest[side], _prevRolled[side]))
                    {
                        elbow = rolledElbow;
                        bend = rolledBend;
                    }
                }

                float rawTurn = _prevRolled[side].LengthSquared > 0.5f ? Deg(_prevRolled[side], bend) : 0f;
                ProbeFlip(side, unrolled, bend, t - s);
                _prevBest[side] = unrolled;
                _prevRolled[side] = bend;

                // Then eased over time, and the arm re-solved from the eased
                // direction. The filter's own state never re-enters the choice of
                // direction above - only the placement below - so the solve stays a
                // function of the target and cannot settle into two poses.
                Vec3 eased = VrArmBend.Smooth(_prevBend[side], _hasPrevBend[side], bend, axis,
                                              Dt(side), SmoothSeconds, RateCapDegPerSec);

                _prevBend[side] = eased;
                _hasPrevBend[side] = true;

                if (eased.LengthSquared > 1e-8f
                    && !SolveElbowWithBend(s, t, eased, a, b, out elbow, out usedA, out usedB, Soft))
                    return false;

                float d = (t - s).Length;

                // ---- aim the two bones -------------------------------------------------

                Vec3 wantUpperDir = (elbow - s).NormalizedCopy();
                Vec3 wantForeDir  = (t - elbow).NormalizedCopy();

                Mat3 upperRot = Aim(oldUpper.rotation, chain.UpperAxis, wantUpperDir);
                Mat3 foreRot  = Aim(oldFore.rotation,  chain.ForeAxis,  wantForeDir);

                // TEST 235. THE ROLL, FROM THE BEND PLANE INSTEAD OF THE ANIMATION.
                //
                // Aim turns each bone by the shortest arc from the ANIMATED orientation,
                // which points it where we want and leaves its roll about its own axis
                // exactly as the animation had it. Walking, swinging and blocking all
                // roll the arm, so the sleeve and gauntlet plates turned with the
                // animation while the hand and elbow stayed where the controller and
                // the solve put them.
                //
                // An elbow is a hinge. Both bones' hinge axis has to lie along the
                // normal of the plane the arm bends in, and that plane is already a pure
                // function of the target. So the roll is taken from that: the hinge axis,
                // taken once in each bone's own frame from the REST pose (TEST 289 - it
                // was the animated pose, which stopped animating), is put on the plane
                // normal every tick.
                Vec3 planeBend = eased.LengthSquared > 1e-8f ? eased : bend;

                if (RollPin)
                {
                    if (_haveHinge[side])
                    {
                        Vec3 normal = HingeNormal(wantUpperDir, wantForeDir, axis, planeBend);

                        Mat3 pinnedUpper, pinnedFore;
                        if (AlignHinge(oldUpper.rotation, chain.UpperAxis, wantUpperDir,
                                       _hingeUpper[side], normal, out pinnedUpper)
                            && AlignHinge(oldFore.rotation, chain.ForeAxis, wantForeDir,
                                          _hingeFore[side], normal, out pinnedFore))
                        {
                            NoteRoll(upperRot, pinnedUpper, foreRot, pinnedFore);
                            upperRot = pinnedUpper;
                            foreRot = pinnedFore;
                        }
                    }
                }

                // TEST 450: each bone as long as the solve placed the joints - the
                // rig's own along/across ratio times ArmScale, SET rather than
                // multiplied: a paused skeleton hands back last frame's stretched
                // bone and Aim keeps its scale, so a multiplier would compound.
                // TEST 456: UNIFORM by default. Stretched along the bone alone, the
                // skinning snapped at certain arm angles whenever the length was not
                // x1.00 - the user: "at 1 it doesn't snap at all", both eyes - while
                // every solved bone was smooth (TEST 455 saw nothing). A renderer
                // that turns bone matrices into rotations cannot take a one-axis
                // stretch; the engine's own character scale is uniform. So the
                // bone is scaled evenly to ArmScale x its own length (6% thicker at
                // x1.06). arm_length_uniform = 0 is the along-the-bone stretch.
                if (LengthUniform)
                {
                    upperRot = ScaleTo(upperRot, chain.UpperAxis, ArmScale * chain.UpperAlong);
                    foreRot = ScaleTo(foreRot, chain.ForeAxis, ArmScale * chain.ForeAlong);
                }
                else
                {
                    upperRot = Stretch(upperRot, chain.UpperAxis, ArmScale * chain.UpperRatio);
                    foreRot = Stretch(foreRot, chain.ForeAxis, ArmScale * chain.ForeRatio);
                }

                MatrixFrame newUpper = oldUpper;
                newUpper.rotation = upperRot;

                MatrixFrame newFore = oldFore;
                newFore.rotation = foreRot;
                newFore.origin = elbow;

                MatrixFrame newHand = oldHand;
                newHand.rotation = targetEntitial.rotation;
                newHand.origin = t;              // the pin: the wrist IS the controller

                // ---- write, each local to its own parent's NEW frame --------------------
                //
                // The twist helpers sit between the solved joints, so the forearm's
                // parent is not the upper arm and the hand's parent is not the
                // forearm. Their local frames are left as the animation set them
                // and recomposed onto our new joints, which is both correct and the
                // reason the helpers do not need writing at all in this stage.

                if (!Write(skeleton, chain.Upper, oldUpper, newUpper, ParentFrame(skeleton, chain.Upper)))
                    return false;

                MatrixFrame foreParent = Recompose(skeleton, chain.Upper, newUpper, chain.Fore);
                if (!Write(skeleton, chain.Fore, oldFore, newFore, foreParent))
                    return false;

                MatrixFrame handParent = Recompose(skeleton, chain.Fore, newFore, chain.Hand);

                // TEST 270. THE FOREARM CARRIES ITS SHARE OF THE ROLL.
                //
                // The helper between elbow and wrist is put back to its rest frame
                // every tick (VrUpperBody), so it never rolled, and every degree of
                // pronation landed in one ring of vertices at the wrist - the wrung
                // candy-wrapper look. A real forearm spreads the roll from the elbow
                // (none) to the wrist (all), so the helper takes the share its
                // position along the bone says it should. The hand is still written
                // with the controller's full rotation below, so the wrist takes only
                // what is left and the weapon does not move.
                float residual = 0f;
                if (Twist && _haveHinge[side])
                {
                    float conf;
                    residual = VrArmBend.WristTwist(handTarget, _hingeHand[side], wantForeDir,
                                                    HingeNormal(wantUpperDir, wantForeDir, axis, planeBend),
                                                    out conf);
                    residual *= VrArmBend.TwistWeight(residual, conf);   // TEST 290

                    if (!TwistHelper(skeleton, chain, newFore, wantForeDir, residual, ref handParent))
                        return false;
                }

                if (!Write(skeleton, chain.Hand, oldHand, newHand, handParent))
                    return false;

                LogOnce();
                Trace(chain, s, elbow, t, d, a + b, twist, roll, residual);
                ProbeSnap(side, upperRot, foreRot, elbow, t, s, d / (a + b), twist, residual, rawTurn,
                          eased.LengthSquared > 1e-8f ? eased : bend);
                return true;
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("The arm IK failed; the arms go back to the game's animation and "
                            + "the hands keep posing on their own. Nothing else is affected.", ex);
                return false;
            }
        }
        /// <summary>
        /// The geometry, with no skeleton in it: where the elbow goes.
        ///
        /// Separated from Solve deliberately. This is the part that is pure, the
        /// part the regression tests can reach, and the part every later stage
        /// changes. Everything around it is bone bookkeeping that cannot be tested
        /// outside a running mission.
        ///
        /// The order matters and is not obvious: the segments are stretched FIRST,
        /// because stretching changes the triangle, and the triangle is what the
        /// anatomical rules are applied to. Choosing the bend direction before
        /// knowing the solved angle would mean the height rule was reasoning about
        /// an elbow position that is not the one we are about to produce.
        ///
        /// usedUpper and usedFore come back because the stretch changes them, and
        /// the caller needs to know the lengths the answer was actually built from.
        /// </summary>
        public static bool SolveElbow(Vec3 shoulder, Vec3 target, float upperLen, float foreLen,
                                      bool isLeft, float swingGain,
                                      out Vec3 elbow, out float usedUpper, out float usedFore)
        {
            Vec3 ignored;
            return SolveElbow(shoulder, target, upperLen, foreLen, isLeft, swingGain,
                              out elbow, out usedUpper, out usedFore, out ignored);
        }

        /// <summary>
        /// As above, and also reports the bend direction it chose - which the caller
        /// needs in order to filter it over time. See VrArmBend.Smooth. rollRad is the
        /// elbow following the hand's roll; see VrArmBend.Direction.
        /// </summary>
        public static bool SolveElbow(Vec3 shoulder, Vec3 target, float upperLen, float foreLen,
                                      bool isLeft, float swingGain,
                                      out Vec3 elbow, out float usedUpper, out float usedFore,
                                      out Vec3 bendUsed, float rollRad = 0f,
                                      Vec3 pole = default(Vec3), float poleWeight = 0f,
                                      float soft = 0f, Vec3 prefer = default(Vec3),
                                      Vec3 preferRolled = default(Vec3))
        {
            elbow = Vec3.Zero;
            usedUpper = upperLen;
            usedFore = foreLen;
            bendUsed = Vec3.Zero;

            if (upperLen <= 1e-5f || foreLen <= 1e-5f)
                return false;

            Vec3 toTarget = target - shoulder;
            float real = toTarget.Length;
            if (real < 1e-4f)
                return false;              // wrist on top of the shoulder: no defined direction

            Vec3 x = toTarget * (1f / real);

            float a = upperLen;
            float b = foreLen;

            // TEST 381: the triangle is solved for the eased distance; the hand
            // stays on the real target, so the last few mm are the wrist's.
            float d = SoftReach(real, a + b, soft);

            // Stretch to reach, rather than clamping the distance. See the class
            // note: clamping is what leaves an arm permanently unable to straighten.
            if (d > a + b)
            {
                float over = d - a - b;
                float share = b / (a + b);
                b += share * over;
                a += (1f - share) * over;
            }

            // Target closer than the segments can fold. The triangle has no
            // solution; equalise so the solve stays defined instead of trying to
            // take the square root of a negative number.
            if (d < Math.Abs(a - b) + 1e-3f)
            {
                float avg = (a + b) * 0.5f;
                a = avg;
                b = avg;
            }

            float cosA = (a * a + d * d - b * b) / (2f * a * d);
            if (cosA > 1f) cosA = 1f;
            if (cosA < -1f) cosA = -1f;
            float sinA = (float)Math.Sqrt(Math.Max(0f, 1f - cosA * cosA));

            bendUsed = VrArmBend.Direction(shoulder, target, upperLen + foreLen, isLeft,
                                           swingGain, cosA, sinA, a, rollRad, pole, poleWeight,
                                           prefer, preferRolled);
            if (bendUsed.LengthSquared < 1e-8f)
                return false;

            if (!Place(shoulder, x, bendUsed, cosA, sinA, a, b,
                       out elbow, out usedUpper, out usedFore))
                return false;

            if (d < real)
                usedFore = (target - elbow).Length;
            return true;
        }

        /// <summary>
        /// The same geometry with the bend direction supplied rather than derived.
        ///
        /// This is the raw cosine law, and it exists so the regression tests can
        /// check the placement on its own - that the two segments really do connect
        /// the shoulder to the wrist, and that the elbow sits on its circle -
        /// without the anatomical rules in the way.
        /// </summary>
        public static bool SolveElbowWithBend(Vec3 shoulder, Vec3 target, Vec3 bend,
                                              float upperLen, float foreLen,
                                              out Vec3 elbow, out float usedUpper, out float usedFore,
                                              float soft = 0f)
        {
            elbow = Vec3.Zero;
            usedUpper = upperLen;
            usedFore = foreLen;

            if (upperLen <= 1e-5f || foreLen <= 1e-5f)
                return false;

            Vec3 toTarget = target - shoulder;
            float real = toTarget.Length;
            if (real < 1e-4f)
                return false;

            Vec3 x = toTarget * (1f / real);

            float a = upperLen;
            float b = foreLen;
            float d = SoftReach(real, a + b, soft);      // TEST 381

            if (d > a + b)
            {
                float over = d - a - b;
                float share = b / (a + b);
                b += share * over;
                a += (1f - share) * over;
            }

            if (d < Math.Abs(a - b) + 1e-3f)
            {
                float avg = (a + b) * 0.5f;
                a = avg;
                b = avg;
            }

            Vec3 y = Perpendicular(bend, x);
            if (y.LengthSquared < 1e-8f)
            {
                // The bend is parallel to the arm axis, so it names no plane. Any
                // perpendicular is as good as any other; take one deterministically
                // rather than refusing, so a straight-down reach still solves.
                Vec3 pick = Math.Abs(x.z) < 0.9f ? new Vec3(0f, 0f, 1f) : new Vec3(1f, 0f, 0f);
                y = Perpendicular(pick, x);
                if (y.LengthSquared < 1e-8f)
                    return false;
            }

            float cosA = (a * a + d * d - b * b) / (2f * a * d);
            if (cosA > 1f) cosA = 1f;
            if (cosA < -1f) cosA = -1f;
            float sinA = (float)Math.Sqrt(Math.Max(0f, 1f - cosA * cosA));

            Place(shoulder, x, y, cosA, sinA, a, b, out elbow, out usedUpper, out usedFore);
            if (d < real)
                usedFore = (target - elbow).Length;
            return true;
        }

        private static bool Place(Vec3 shoulder, Vec3 x, Vec3 bend, float cosA, float sinA,
                                  float a, float b,
                                  out Vec3 elbow, out float usedUpper, out float usedFore)
        {
            elbow = shoulder + x * (cosA * a) + bend * (sinA * a);
            usedUpper = a;
            usedFore = b;
            return true;
        }


        /// <summary>TEST 290. The archery pose while a bow is out. arm_bow_pose = 0
        /// leaves both arms on the ordinary rules.</summary>
        private static readonly bool BowPose = VrConfig.Bool("arm_bow_pose", true);

        /// <summary>
        /// TEST 290/290b. The elbow direction an archer holds, and how strongly.
        ///
        /// The ordinary rules want the elbow down, which with the string hand at
        /// the mouth hangs it under the shoulder - and the hand's roll then swung
        /// it round a short arm axis. TEST 290 pointed it back along the arrow;
        /// the screenshots showed that wrong: with the hand forward, inboard and
        /// up of the shoulder, "back" about that axis is "up", and the upper arm
        /// crossed the face (the plain solve put the elbow 10 cm off the midline
        /// at eye height). 290b points it OUT to the right, a little back, and
        /// lets it past the never-forward rule (VrArmBend.Direction). The bow arm
        /// is straight with its elbow rotated out and down, clear of the string.
        ///
        /// Constant directions in entitial space, so this stays a pure function of
        /// the target and cannot feed on the arm's own pose. The string arm eases
        /// in as the hand rises to nock height, so reaching down for anything else
        /// with a bow out still looks like an arm.
        /// </summary>
        public static Vec3 BowPole(ArmRole role, bool isLeft, Vec3 shoulder, Vec3 target, float reach,
                                   out float weight)
        {
            weight = 0f;
            if (!BowPose || role == ArmRole.None || reach < 1e-4f)
                return Vec3.Zero;

            Vec3 outward = isLeft ? -VrArmBend.Right : VrArmBend.Right;

            if (role == ArmRole.Bow)
            {
                weight = 1f;
                return outward - VrArmBend.Up * 0.5f;
            }

            weight = VrArmBend.Ramp((target.z - shoulder.z) / reach, -0.2f, 0f);
            return outward - VrArmBend.Forward * 0.3f;
        }

        /// <summary>
        /// TEST 270. How far to swing the elbow for a wrist rolled by twistRad: a
        /// share of the roll inside the forearm's range, and all of it past the range,
        /// where a real forearm has run out and the upper arm turns instead.
        /// </summary>
        public static float ElbowRoll(float twistRad, float limitRad, float share)
        {
            float inRange = Math.Max(-limitRad, Math.Min(limitRad, twistRad));
            return share * inRange + (twistRad - inRange);
        }

        /// <summary>
        /// Rolls the forearm helper about the forearm by its share of the wrist's
        /// roll and writes it, and hands back its new frame as the hand's parent.
        /// Leaves everything alone on a rig where the helper is not the bone between
        /// the elbow and the hand.
        /// </summary>
        private static bool TwistHelper(Skeleton skeleton, VrArmChain chain, MatrixFrame newFore,
                                        Vec3 foreDir, float residual, ref MatrixFrame handParent)
        {
            if (chain.ForeTwist < 0
                || skeleton.GetParentBoneIndex(chain.Hand) != chain.ForeTwist
                || skeleton.GetParentBoneIndex(chain.ForeTwist) != chain.Fore)
            {
                if (!_loggedNoHelper)
                {
                    _loggedNoHelper = true;
                    VrLog.Info("TEST 270: the forearm twist helper is not the bone between the elbow "
                               + "and the hand on this rig, so the forearm does not twist and the "
                               + "wrist takes all of the roll. The elbow still follows it.");
                }
                return true;
            }

            float share = TwistForeShare >= 0f ? Math.Min(1f, TwistForeShare) : chain.ForeTwistFrac;
            float angle = share * Math.Max(-TwistLimit, Math.Min(TwistLimit, residual));
            if (Math.Abs(angle) < 1e-4f)
                return true;

            // handParent is the helper's new frame, from Recompose: rest local on the
            // solved forearm. Rodrigues on the basis keeps its scale.
            MatrixFrame helper = handParent;
            helper.rotation = Rotate(helper.rotation, foreDir,
                                     (float)Math.Cos(angle), (float)Math.Sin(angle));

            if (!Write(skeleton, chain.ForeTwist, helper, helper, newFore))
                return false;

            handParent = helper;
            return true;
        }

        /// <summary>
        /// The component of v perpendicular to unit axis, normalised. This is the
        /// projection that puts a pole direction onto the circle the elbow lives
        /// on; without it the cosine-law placement below is simply wrong, because
        /// it assumes y is perpendicular to x.
        /// </summary>
        private static Vec3 Perpendicular(Vec3 v, Vec3 axis)
        {
            float k = Vec3.DotProduct(v, axis);
            Vec3 r = v - axis * k;

            float len = r.Length;
            if (len < 1e-5f)
                return Vec3.Zero;

            return r * (1f / len);
        }

        /// <summary>
        /// Rotates an orientation by the shortest arc that takes its own bone axis
        /// onto a desired world direction.
        ///
        /// Starting from the ANIMATED orientation rather than building a fresh
        /// basis is deliberate: it keeps whatever roll the animator put into the
        /// bone, and only changes the thing we actually mean to change, which is
        /// where the bone points.
        /// </summary>
        internal static Mat3 Aim(Mat3 current, Vec3 boneAxisLocal, Vec3 wantDir)
        {
            // A bone frame carries the agent's scale. Orthonormalising is what makes
            // the arc below a rotation rather than a rotation-and-squash, but the
            // scale has to go back afterwards or a scaled character's arm would be
            // quietly resized every frame we solve it.
            Vec3 scale = current.GetScaleVector();

            Mat3 m = current;
            m.Orthonormalize();

            Vec3 have = m.TransformToParent(boneAxisLocal);
            have.Normalize();

            Vec3 want = wantDir;
            want.Normalize();

            float dot = Vec3.DotProduct(have, want);
            if (dot > 1f) dot = 1f;
            if (dot < -1f) dot = -1f;

            Mat3 result;

            if (dot > 0.999999f)
            {
                result = m;                    // already pointing there
            }
            else
            {
                Vec3 axis = Vec3.CrossProduct(have, want);
                float axisLen = axis.Length;

                if (axisLen < 1e-6f)
                {
                    // Exactly antiparallel: the arc is 180 degrees and its axis is
                    // undefined, so any perpendicular will do. Picking the more
                    // orthogonal of two candidates avoids a near-zero cross product.
                    Vec3 pick = Math.Abs(have.z) < 0.9f ? new Vec3(0f, 0f, 1f) : new Vec3(1f, 0f, 0f);
                    axis = Vec3.CrossProduct(have, pick);
                    axisLen = axis.Length;

                    if (axisLen < 1e-6f)
                        result = m;
                    else
                        result = Rotate(m, axis * (1f / axisLen), -1f, 0f);
                }
                else
                {
                    // For unit vectors the cross product's length is the sine.
                    result = Rotate(m, axis * (1f / axisLen), dot, axisLen);
                }
            }

            if (scale.x > 1e-5f && scale.y > 1e-5f && scale.z > 1e-5f)
                result.ApplyScaleLocal(scale);

            return result;
        }

        /// <summary>
        /// Rodrigues, applied to each basis vector of the matrix. Written out
        /// rather than routed through a library rotation because the composition
        /// order of the library helpers is a convention, and this has none.
        /// </summary>
        internal static Mat3 Rotate(Mat3 m, Vec3 axis, float cos, float sin)
        {
            Mat3 r = default(Mat3);
            r.s = RotateVec(m.s, axis, cos, sin);
            r.f = RotateVec(m.f, axis, cos, sin);
            r.u = RotateVec(m.u, axis, cos, sin);
            return r;
        }

        private static Vec3 RotateVec(Vec3 v, Vec3 axis, float cos, float sin)
        {
            return v * cos
                 + Vec3.CrossProduct(axis, v) * sin
                 + axis * (Vec3.DotProduct(axis, v) * (1f - cos));
        }

        /// <summary>
        /// TEST 289. The elbow's hinge axis in the upper arm's, the forearm's and the
        /// hand's own frames, taken from the REST pose, once per arm.
        ///
        /// It used to be measured from the animated pose (TEST 235). Since TEST 257
        /// the arms do not animate, so what it read was a frozen frame or our own
        /// previous write: the same rig's left-arm hinge came out (0, -0.99, -0.13)
        /// in one run and (0, 0.73, 0.68) in the next, in the forearm's frame it had
        /// a large component along the bone itself (impossible for a hinge), the
        /// arms were built rolled up to 180 degrees from the animation, and the wrist
        /// read -110 to -170 degrees of roll at rest. Every downstream roll - the
        /// hinge pin, the forearm twist, the elbow following the hand - inherited it.
        ///
        /// The rest pose is a rig constant. Its hand-to-forearm relation is also the
        /// skinning's bind, which is exactly the zero the twist has to be measured
        /// from: on a palm-down T-pose it is a thumb-up wrist once the arm points
        /// forward.
        /// </summary>
        private static void MeasureHinge(int side, Skeleton skeleton, VrArmChain chain)
        {
            MatrixFrame upper = skeleton.GetBoneEntitialRestFrame(chain.Upper, false);
            MatrixFrame fore = skeleton.GetBoneEntitialRestFrame(chain.Fore, false);
            MatrixFrame hand = skeleton.GetBoneEntitialRestFrame(chain.Hand, false);

            bool fromBend;
            Vec3 n = VrArmBend.RestHinge(upper.origin, fore.origin, hand.origin,
                                         RestFlexDir, RestFlex == "auto", out fromBend);
            if (n.LengthSquared < 1e-8f)
                return;

            Mat3 ru = upper.rotation;
            ru.Orthonormalize();
            Mat3 rf = fore.rotation;
            rf.Orthonormalize();
            Mat3 rh = hand.rotation;
            rh.Orthonormalize();

            _hingeUpper[side] = ru.TransformToLocal(n);
            _hingeFore[side] = rf.TransformToLocal(n);
            _hingeHand[side] = rh.TransformToLocal(n);
            _haveHinge[side] = true;

            Vec3 u = (fore.origin - upper.origin).NormalizedCopy();
            Vec3 f = (hand.origin - fore.origin).NormalizedCopy();
            double restBend = Math.Acos(Math.Max(-1f, Math.Min(1f, Vec3.DotProduct(u, f)))) * 180.0 / Math.PI;

            VrLog.Info(string.Format(
                "TEST 289 {0} arm: elbow hinge from the REST pose ({1}; the rest arm is bent {2:F1} "
                + "degrees). Entitial ({3:F2}, {4:F2}, {5:F2}); in the upper arm's frame ({6:F2}, "
                + "{7:F2}, {8:F2}), the forearm's ({9:F2}, {10:F2}, {11:F2}), the hand's ({12:F2}, "
                + "{13:F2}, {14:F2}). Nothing is read from the animation. arm_rest_flex = {15}.",
                chain.IsLeft ? "Left" : "Right",
                fromBend ? "its own bend" : "flexing toward " + RestFlex, restBend,
                n.x, n.y, n.z,
                _hingeUpper[side].x, _hingeUpper[side].y, _hingeUpper[side].z,
                _hingeFore[side].x, _hingeFore[side].y, _hingeFore[side].z,
                _hingeHand[side].x, _hingeHand[side].y, _hingeHand[side].z,
                RestFlex));
        }

        /// <summary>
        /// The normal of the plane the arm bends in. From the two solved segments
        /// when the arm is bent; from the bend direction when it is nearly straight,
        /// where the cross product vanishes. Both give the same normal in the limit.
        /// </summary>
        private static Vec3 HingeNormal(Vec3 upperDir, Vec3 foreDir, Vec3 axis, Vec3 bend)
        {
            Vec3 n = Vec3.CrossProduct(upperDir, foreDir);

            if (n.LengthSquared < 0.0025f)
            {
                Vec3 y = Perpendicular(bend, axis);
                n = y.LengthSquared > 1e-8f ? Vec3.CrossProduct(y, axis) : n;
            }

            if (n.LengthSquared > 1e-10f)
                n.Normalize();

            return n;
        }

        /// <summary>
        /// A bone orientation that points its length axis along <paramref name="dir"/>
        /// and lays its hinge axis along <paramref name="normal"/>. Built from the two
        /// frames directly - the bone's own (length axis, hinge) and the wanted one -
        /// so nothing of the animated orientation is in it but the scale.
        /// </summary>
        private static bool AlignHinge(Mat3 current, Vec3 lengthLocal, Vec3 dir,
                                       Vec3 hingeLocal, Vec3 normal, out Mat3 result)
        {
            result = current;

            Vec3 scale = current.GetScaleVector();

            Vec3 e1 = lengthLocal;
            if (e1.LengthSquared < 1e-10f)
                return false;
            e1.Normalize();

            // The hinge as it stands is not exactly square to the length axis
            // (it was measured, not defined), so take its square part.
            Vec3 e2 = hingeLocal - e1 * Vec3.DotProduct(hingeLocal, e1);
            if (e2.LengthSquared < 1e-6f)
                return false;
            e2.Normalize();
            Vec3 e3 = Vec3.CrossProduct(e1, e2);

            Vec3 f1 = dir;
            if (f1.LengthSquared < 1e-10f)
                return false;
            f1.Normalize();

            Vec3 f2 = normal - f1 * Vec3.DotProduct(normal, f1);
            if (f2.LengthSquared < 1e-6f)
                return false;
            f2.Normalize();
            Vec3 f3 = Vec3.CrossProduct(f1, f2);

            // The rotation taking each local axis to its wanted one: the image of local
            // x, y and z, as the matrix's three basis vectors.
            Mat3 r = default(Mat3);
            r.s = f1 * e1.x + f2 * e2.x + f3 * e3.x;
            r.f = f1 * e1.y + f2 * e2.y + f3 * e3.y;
            r.u = f1 * e1.z + f2 * e2.z + f3 * e3.z;

            if (scale.x > 1e-5f && scale.y > 1e-5f && scale.z > 1e-5f)
                r.ApplyScaleLocal(scale);

            result = r;
            return true;
        }

        /// <summary>Remembers how far the roll moved from the animation's, and says
        /// it every few seconds so the log can show this is doing something.</summary>
        private static void NoteRoll(Mat3 animatedUpper, Mat3 pinnedUpper,
                                     Mat3 animatedFore, Mat3 pinnedFore)
        {
            _rollUpperMax = Math.Max(_rollUpperMax, AngleBetween(animatedUpper, pinnedUpper));
            _rollForeMax = Math.Max(_rollForeMax, AngleBetween(animatedFore, pinnedFore));

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_rollWindow == 0)
                _rollWindow = now;

            if (now - _rollWindow < 5L * System.Diagnostics.Stopwatch.Frequency)
                return;

            _rollWindow = now;

            VrLog.Info("TEST 235: in the last 5 s the roll set from the bend plane differed from the "
                       + "animation's by up to " + _rollUpperMax.ToString("0.0") + " degrees on the "
                       + "upper arm and " + _rollForeMax.ToString("0.0") + " on the forearm. Both "
                       + "bones already pointed the same way; this is roll only.");

            _rollUpperMax = 0f;
            _rollForeMax = 0f;
        }

        private static float AngleBetween(Mat3 a, Mat3 b)
        {
            a.Orthonormalize();
            b.Orthonormalize();

            Quaternion qa = a.ToQuaternion();
            Quaternion qb = b.ToQuaternion();

            float dot = Math.Min(1f, Math.Abs(qa.Dotp4(qb)));
            return (float)(2.0 * Math.Acos(dot) * 180.0 / Math.PI);
        }

        /// <summary>The parent bone's entitial frame as the animation left it.</summary>
        /// <summary>TEST 450: r lengthened along its own local axis so that the
        /// bone's length there is `ratio` x its scale across the axis, whatever it
        /// was before: r * (I + m a a^T). The perpendicular is untouched, and
        /// applying it twice is applying it once. Pure; the math tests call it.</summary>
        internal static Mat3 Stretch(Mat3 r, Vec3 axisLocal, float ratio)
        {
            if (axisLocal.LengthSquared < 1e-8f || ratio <= 0f)
                return r;

            Vec3 a = axisLocal.NormalizedCopy();
            float along = r.TransformToParent(a).Length;
            float across = r.TransformToParent(Across(a)).Length;
            if (along < 1e-6f || across < 1e-6f)
                return r;

            float m = ratio * across / along - 1f;
            if (Math.Abs(m) < 1e-6f)
                return r;

            Mat3 o = r;
            o.s = r.TransformToParent(new Vec3(1f + m * a.x * a.x, m * a.x * a.y, m * a.x * a.z, -1f));
            o.f = r.TransformToParent(new Vec3(m * a.y * a.x, 1f + m * a.y * a.y, m * a.y * a.z, -1f));
            o.u = r.TransformToParent(new Vec3(m * a.z * a.x, m * a.z * a.y, 1f + m * a.z * a.z, -1f));
            return o;
        }

        private static readonly bool LengthUniform = VrConfig.Bool("arm_length_uniform", false);   // TEST 456 RESULT: not the snap

        /// <summary>TEST 456: r scaled EVENLY so that its length along its own axis is
        /// `along` (the bone's own scale x the player's arm length), whatever it was
        /// before - a paused skeleton hands back last frame's bone, so this sets
        /// rather than multiplies. The bone's own proportions are kept. Pure; the
        /// math tests call it.</summary>
        internal static Mat3 ScaleTo(Mat3 r, Vec3 axisLocal, float along)
        {
            if (axisLocal.LengthSquared < 1e-8f || along <= 0f)
                return r;
            float now = r.TransformToParent(axisLocal.NormalizedCopy()).Length;
            if (now < 1e-6f)
                return r;
            float k = along / now;
            if (Math.Abs(k - 1f) < 1e-6f)
                return r;
            r.s *= k;
            r.f *= k;
            r.u *= k;
            return r;
        }

        /// <summary>TEST 450: a bone's own length/girth ratio along that axis, read
        /// from the animation before any write (VrArmBones.Build) - 1 for a
        /// uniformly scaled bone. Pure.</summary>
        internal static float AxisRatio(Mat3 r, Vec3 axisLocal)
        {
            if (axisLocal.LengthSquared < 1e-8f)
                return 1f;
            Vec3 a = axisLocal.NormalizedCopy();
            float across = r.TransformToParent(Across(a)).Length;
            return across > 1e-6f ? r.TransformToParent(a).Length / across : 1f;
        }

        /// <summary>One fixed unit perpendicular to a, the same every frame.</summary>
        private static Vec3 Across(Vec3 a)
        {
            Vec3 pick = Math.Abs(a.z) < 0.9f ? new Vec3(0f, 0f, 1f, -1f) : new Vec3(1f, 0f, 0f, -1f);
            return Vec3.CrossProduct(a, pick).NormalizedCopy();
        }

        private static MatrixFrame ParentFrame(Skeleton skeleton, sbyte bone)
        {
            sbyte parent = skeleton.GetParentBoneIndex(bone);
            if (parent < 0)
                return MatrixFrame.Identity;

            return skeleton.GetBoneEntitialFrameWithIndex(parent);
        }

        /// <summary>
        /// The new entitial frame of `target`'s PARENT, given that `ancestor` has
        /// moved to `newAncestor`.
        ///
        /// Walks from the target's parent up to the ancestor, collecting the local
        /// frames the animation produced, then composes them back down from the new
        /// ancestor. This is what lets a bone be written relative to a parent that
        /// has already moved this frame but whose entitial frame the skeleton has
        /// not recomputed yet.
        /// </summary>
        private static MatrixFrame Recompose(Skeleton skeleton, sbyte ancestor,
                                             MatrixFrame newAncestor, sbyte target)
        {
            sbyte parent = skeleton.GetParentBoneIndex(target);
            if (parent < 0)
                return MatrixFrame.Identity;

            if (parent == ancestor)
                return newAncestor;

            // At most a couple of helper bones in practice; the bound is a guard
            // against a malformed hierarchy, not an expected depth.
            const int MaxWalk = 8;
            var stack = new sbyte[MaxWalk];
            int n = 0;

            sbyte walk = parent;
            while (walk >= 0 && walk != ancestor && n < MaxWalk)
            {
                stack[n++] = walk;
                walk = skeleton.GetParentBoneIndex(walk);
            }

            if (walk != ancestor)
            {
                // The target is not under the ancestor, so there is nothing to
                // recompose and the animation's own frame is the honest answer.
                return skeleton.GetBoneEntitialFrameWithIndex(parent);
            }

            MatrixFrame frame = newAncestor;
            MatrixFrame parentOld = skeleton.GetBoneEntitialFrameWithIndex(ancestor);

            for (int i = n - 1; i >= 0; i--)
            {
                MatrixFrame boneOld = skeleton.GetBoneEntitialFrameWithIndex(stack[i]);
                MatrixFrame local = parentOld.TransformToLocalNonOrthogonal(boneOld);
                frame = frame.TransformToParent(local);
                parentOld = boneOld;
            }

            return frame;
        }

        private static bool Write(Skeleton skeleton, sbyte bone, MatrixFrame old,
                                  MatrixFrame want, MatrixFrame parent)
        {
            if (bone < 0)
                return false;

            // NonOrthogonal, deliberately. MatrixFrame.TransformToLocal multiplies by
            // the TRANSPOSE, which is the inverse only for an orthonormal rotation -
            // and a bone frame on a scaled agent is not one, so the plain call would
            // scale the wrong way by the square of the agent's scale. This overload
            // takes a real inverse and is correct either way.
            MatrixFrame local = parent.TransformToLocalNonOrthogonal(want);

            // A non-finite frame would propagate into the skeleton and stay there.
            // Cheaper to refuse the whole arm for one frame than to find out later
            // why a character folded up.
            if (!Finite(local.origin) || !Finite(local.rotation.s)
                || !Finite(local.rotation.f) || !Finite(local.rotation.u))
                return false;

            skeleton.SetBoneLocalFrame(bone, local);
            return true;
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
            VrLog.Info("Arm IK is solving, with the anatomical constraints on (stage B): the "
                       + "elbow will not bend forward, will not lie flat on the ribs, swings "
                       + "out when you reach across yourself, and stays below the wrist while "
                       + "your hand is low. It does not yet rotate smoothly through the "
                       + "workspace the way a real elbow does - that is stage C, and it is "
                       + "fitted from your own arm rather than guessed.");
        }

        private static void Trace(VrArmChain chain, Vec3 shoulder, Vec3 elbow, Vec3 target,
                                  float dist, float reach, float twist, float roll, float residual)
        {
            if (!Debug)
                return;

            if (++_debugTick < 90)
                return;
            _debugTick = 0;

            VrLog.Info(string.Format(
                "Arm IK {0}: shoulder ({1:F2}, {2:F2}, {3:F2}) elbow ({4:F2}, {5:F2}, {6:F2}) "
                + "target ({7:F2}, {8:F2}, {9:F2}) reach {10:F0}%; TEST 270 wrist roll "
                + "{11:F0} deg, elbow swung {12:F0}, left after the swing {13:F0} (about 0 "
                + "with your wrist relaxed, thumb up)",
                chain.IsLeft ? "L" : "R",
                shoulder.x, shoulder.y, shoulder.z,
                elbow.x, elbow.y, elbow.z,
                target.x, target.y, target.z,
                reach > 1e-4f ? dist / reach * 100f : 0f,
                twist * 57.2957795f, roll * 57.2957795f, residual * 57.2957795f));
        }

        /// <summary>
        /// Forgets the elbow smoothing and nothing else. TEST 236: the animation has
        /// the arm for the length of a swing's release, so what the filter remembers
        /// is a bend from before it and a clock that stopped. Handed the arm back,
        /// the wrist snaps to the controller at once (it is pinned) while the elbow
        /// crawls after it - a rubber band over 10-20 frames after every swing.
        ///
        /// Not Reset(), which also clears the measured hinge and would re-run the
        /// measurement, and re-log TEST 235, after every swing.
        /// </summary>
        public static void ForgetSmoothing()
        {
            for (int i = 0; i < 2; i++)
            {
                _hasPrevBend[i] = false;
                _lastTick[i] = 0;
            }
        }

        public static void Reset()
        {
            _logged = false;
            _debugTick = 0;

            for (int i = 0; i < 2; i++)
            {
                _hasPrevBend[i] = false;
                _prevBend[i] = Vec3.Zero;
                _lastTick[i] = 0;
                _haveHinge[i] = false;
                _hingeHand[i] = Vec3.Zero;
            }

            _loggedNoHelper = false;
            _rollUpperMax = 0f;
            _rollForeMax = 0f;
            _rollWindow = 0;
        }
    }
}
