using System;
using TaleWorlds.Library;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Which way the elbow points.
    ///
    /// The elbow lives on a circle around the shoulder-to-wrist line, so the IK
    /// geometry has a whole family of correct answers and this file picks one. It
    /// is the half of an arm solver that decides whether the result looks like an
    /// arm or like a broken prop.
    ///
    /// ONE CIRCLE, SEVERAL CONSTRAINTS, SOLVED TOGETHER
    ///
    /// The anatomical rules are each an arc of that circle:
    ///
    ///   - the elbow never bends forward,
    ///   - it never rises above the shoulder while the hand is low,
    ///   - it never lies flat against the ribs,
    ///   - and reaching across the body it swings outward.
    ///
    /// Applying them one after another does not work, and that is worth stating
    /// because it is the obvious implementation and it is subtly wrong: each clamp
    /// moves the direction along the circle, so a later one silently undoes an
    /// earlier one. Enforcing "not flat on the ribs" after "never forward" puts the
    /// elbow forward again - measured, not theorised, by the regression test that
    /// now guards it.
    ///
    /// So the circle is parameterised by one angle and every rule becomes an
    /// inequality in that angle, scored together. Each rule carries a weight, which
    /// settles what happens when they genuinely cannot all hold at once - and they
    /// cannot, for some poses, because these are arcs on a circle and arcs need not
    /// intersect. Never-forward outranks the rest; not-flat-on-the-ribs yields
    /// first, because it is a preference about how a relaxed arm looks rather than
    /// a thing a real joint refuses to do.
    ///
    /// EVERYTHING HERE IS A PURE FUNCTION OF THE TARGET
    ///
    /// Not of the current arm pose, and that is the single most important property
    /// in the file rather than a stylistic preference.
    ///
    /// If any term read the live arm orientation, the solve would feed on its own
    /// previous output. That is a closed loop, and a closed loop carrying an
    /// angular constraint has more than one fixed point - so the elbow acquires two
    /// stable poses and flips between them for the same hand position. It shows up
    /// as the elbow teleporting, it is not a tuning problem, and no amount of
    /// smoothing removes it. Keeping this a map from target to direction makes that
    /// failure structurally impossible instead of merely absent.
    ///
    /// AXES
    ///
    /// Entitial space is the agent entity's own frame: X right, Y forward, Z up.
    /// Because the solve happens there, these are constants rather than something
    /// derived from the camera, so the elbow's frame of reference cannot drift as
    /// the player looks around. VrArmBones logs the shoulder positions once so the
    /// handedness here is confirmed rather than assumed.
    /// </summary>
    public static class VrArmBend
    {
        public static readonly Vec3 Right = new Vec3(1f, 0f, 0f);
        public static readonly Vec3 Forward = new Vec3(0f, 1f, 0f);
        public static readonly Vec3 Up = new Vec3(0f, 0f, 1f);

        /// <summary>A real arm keeps a few degrees of abduction even hanging
        /// relaxed; the elbow never rests flat on the ribs. About 7 degrees.</summary>
        private const float MinOutward = 0.12f;

        // How much each rule outranks the next when they cannot all hold.
        private const float WeightForward = 100f;
        private const float WeightHeight = 10f;
        private const float WeightOutward = 1f;

        /// <summary>The elbow-height range a fully-weighted height rule corresponds
        /// to. A pose with this much height on the circle scores the height rule at
        /// its nominal weight; a pose with less scores it proportionally lower. See
        /// where it is used for the measurement that set it.</summary>
        private const float HeightSpanReference = 0.15f;

        /// <summary>How finely the circle is searched. 0.5 degrees moves the elbow
        /// about 2.5 mm on a human arm, which is below what anyone can see and far
        /// below what the tracking noise already is.</summary>
        private const int Steps = 720;

        /// <summary>TEST 453: the elbow keeps its side. Best is an argmin over the
        /// circle, and where the rules conflict - the right hand high across the
        /// face, "not forward" against "out to the side" - two far-apart elbows
        /// score nearly the same and the winner flipped as the hand hovered there:
        /// "the hand snaps at a specific position" (the elbow, and the wrist roll
        /// measured from it, swung across). The best within StickWindow of last
        /// frame's elbow is kept unless the global one is better by this much.
        /// Inside one legal arc the window walks to the answer (60 deg a frame), so
        /// only a jump between separate basins is held. 0 = the old argmin.</summary>
        internal static readonly float Stick = Math.Max(0f, VrConfig.Float("arm_ik_elbow_stick", 0.05f));
        private const float StickWindow = (float)(Math.PI / 3.0);

        /// <summary>TEST 454: how far forward the elbow may point (bend . Forward).
        /// It was 0. For an arm reaching ahead and a little up or across, the
        /// natural elbow - down and out - carries 0.1-0.25 of forward purely from
        /// the arm's tilt, so a zero line banned it: the elbow was pushed inward
        /// across the ribs, or flat to the side, and jumped between the two as the
        /// hand crossed shoulder height - every TEST 453 flip, at z +0.01..+0.05. A
        /// real hyperextended elbow points far more forward than this.</summary>
        public static readonly float ForwardTol =
            Math.Max(0f, Math.Min(0.6f, VrConfig.Float("arm_ik_forward_tol", 0.3f)));

        /// <summary>TEST 454: the forward allowance for an arm along x - ForwardTol,
        /// plus the arm's own elevation: an elbow hanging straight down below an arm
        /// raised e degrees points sin(e) forward purely from the tilt, and at a fixed
        /// limit it was pushed sideways (inward) as the hand rose past ~+0.11 m.</summary>
        public static float ForwardLimit(Vec3 x) => ForwardTol + Math.Max(0f, x.z);

        /// <summary>One rule: satisfied when dot(bend, N) is at least Min.</summary>
        private struct Rule
        {
            public Vec3 N;
            public float Min;
            public float Weight;
        }

        /// <summary>
        /// The bend direction: a unit vector perpendicular to the shoulder-to-wrist
        /// axis, pointing the way the elbow should be displaced.
        ///
        /// cosA, sinA and upperLen describe the solved triangle, and are needed
        /// because the height rule is about where the elbow ENDS UP, which depends
        /// on the geometry rather than on the direction alone.
        ///
        /// swingGain scales the cross-body swing only; 0 leaves the rest in place.
        ///
        /// rollRad turns the answer about the shoulder-to-wrist axis afterwards -
        /// the elbow following the hand's roll (TEST 270) - and the rules are then
        /// applied again, so the hand can only swing the elbow along the legal arc.
        /// It comes from the controller's orientation, which is part of the target,
        /// so this stays a pure function of the target.
        /// </summary>
        public static Vec3 Direction(Vec3 shoulder, Vec3 target, float reach, bool isLeft,
                                     float swingGain, float cosA, float sinA, float upperLen,
                                     float rollRad = 0f, Vec3 pole = default(Vec3), float poleWeight = 0f,
                                     Vec3 prefer = default(Vec3), Vec3 preferRolled = default(Vec3))
        {
            Vec3 toTarget = target - shoulder;
            float d = toTarget.Length;
            if (d < 1e-4f || reach < 1e-4f)
                return Vec3.Zero;

            Vec3 x = toTarget * (1f / d);

            // The circle's own basis. u is the perpendicular-to-x direction most
            // nearly straight up; v completes it. Choosing u this way makes v
            // horizontal - it is perpendicular to the plane containing up and x,
            // so it has no vertical component at all - which is what lets the
            // height rule below be a plain inequality rather than a special case.
            Vec3 u = Perp(Up, x);
            if (u.LengthSquared < 1e-8f)
            {
                // The arm is vertical, so "up" names no direction on this circle.
                // Any basis will do; take one deterministically.
                u = Perp(Math.Abs(x.x) < 0.9f ? Right : Forward, x);
                if (u.LengthSquared < 1e-8f)
                    return Vec3.Zero;
            }

            Vec3 v = Vec3.CrossProduct(x, u);
            float vLen = v.Length;
            if (vLen < 1e-6f)
                return Vec3.Zero;
            v = v * (1f / vLen);

            Vec3 outward = isLeft ? -Right : Right;

            Vec3 wanted = Desired(toTarget, x, outward, reach, d, swingGain);
            if (wanted.LengthSquared < 1e-8f)
                return Vec3.Zero;

            // TEST 290. A pose hint - the archer's raised draw elbow - pulls what is
            // wanted toward a fixed entitial direction. Before the rules, so they
            // still hold; a constant, so this stays a function of the target.
            if (poleWeight > 0f)
            {
                Vec3 p = Perp(pole, x);
                if (p.LengthSquared > 1e-8f)
                {
                    float w = Math.Min(1f, poleWeight);
                    Vec3 mixed = Perp(wanted * (1f - w) + p * w, x);
                    if (mixed.LengthSquared > 1e-8f)
                        wanted = mixed;
                }
            }

            // --- the rules -------------------------------------------------------

            var rules = new Rule[3];
            int n = 0;

            // Never forward. A human elbow does not bend that way at all.
            //
            // TEST 290b: except as far as a pose hint says otherwise. With the draw
            // hand at the mouth - inboard, forward and up of the shoulder - an elbow
            // pointing out to the right has a small forward component about that
            // arm axis, and this rule (weight 100) clamped it back until the upper
            // arm crossed the face. The hint is explicit about where the elbow goes.
            rules[n].N = -Forward;
            rules[n].Min = -ForwardLimit(x);     // TEST 454
            rules[n].Weight = WeightForward * (1f - Math.Max(0f, Math.Min(1f, poleWeight)));
            n++;

            // Never above the shoulder while the hand is at or below shoulder
            // height. Expressed on the circle: the elbow's height is
            // shoulder.z + cosA*len*x.z + sinA*len*(bend . u)*u.z, because the
            // v component contributes no height. So it is a ceiling on bend . u.
            // TEST 454: FADED in over the 10 cm above the shoulder, not switched on
            // at +5 cm. The hard edge was where every logged elbow flip sat (TEST
            // 453: the hand straight ahead at shoulder +0.01..+0.05 m): bobbing the
            // hand across it added and dropped a weight-10 rule, the elbow jumped,
            // and a longer arm (more bend at the same reach) made it plain to see.
            float lift = sinA * upperLen;
            float heightOn = Ramp(shoulder.z + 0.10f - target.z, 0f, 0.10f);
            if (heightOn > 0f && lift > 1e-4f && u.z > 1e-4f)
            {
                float baseZ = shoulder.z + cosA * upperLen * x.z;

                // The ideal ceiling is the lower of wrist and shoulder. It is often
                // unreachable - a bent arm hanging at your side has its elbow above
                // its wrist no matter what, which is simply what a folded arm does -
                // and the scoring below then takes the lowest the circle allows
                // instead of failing.
                float span = lift * u.z;
                float cap = Math.Min(target.z, shoulder.z);
                float pMax = (cap - baseZ) / span;

                // Weighted by how much elbow height is ACTUALLY at stake, not by
                // the bare inequality.
                //
                // `span` is the most the elbow's height can change across the whole
                // circle. With the arm hanging down that is a few millimetres - the
                // circle is nearly horizontal - and a rule scored on the raw dot
                // product would then spend the entire bend direction, swinging the
                // elbow most of the way inward, to buy 7 mm of height. Measured, on
                // the hand-at-your-side pose: outward component -0.65, i.e. the
                // elbow tucked across the ribs to satisfy a rule that had almost
                // nothing to win. Scoring in metres makes the rule care exactly as
                // much as there is to care about.
                rules[n].N = -u;
                rules[n].Min = -pMax;
                rules[n].Weight = WeightHeight * (span / HeightSpanReference) * heightOn;
                n++;
            }

            // Not flat on the ribs - which are only beside the elbow at or below the
            // shoulder. TEST 454: faded out over the first 8 cm above it, before the
            // height rule ends (+10 cm). Raised across the face, out-from-the-ribs
            // and not-forward left no elbow-down answer at all: the elbow chattered
            // between hanging down and flaring up and out right where the height
            // rule let go - "the pose nobody holds" Desired already drops there.
            Vec3 outPerp = Perp(outward, x);
            float outOn = Ramp(shoulder.z + 0.08f - target.z, 0f, 0.08f);
            if (outPerp.LengthSquared > 1e-8f && outOn > 0f)
            {
                rules[n].N = outPerp;
                rules[n].Min = MinOutward;
                rules[n].Weight = WeightOutward * outOn;
                n++;
            }

            Vec3 best = Best(wanted, u, v, rules, n, prefer);

            // Rolled from the ANSWER, not from `wanted`: `wanted` may sit outside the
            // legal arc, and rolling it there would move nothing until it crossed in.
            if (Math.Abs(rollRad) > 1e-4f && best.LengthSquared > 1e-8f)
            {
                float c = (float)Math.Cos(rollRad);
                float s = (float)Math.Sin(rollRad);
                best = Best(best * c + Vec3.CrossProduct(x, best) * s, u, v, rules, n, preferRolled);
            }

            return best;
        }

        /// <summary>
        /// TEST 289. The elbow's hinge normal in the REST pose, in the cross(upper,
        /// fore) sense the solve uses: from the rest pose's own bend when it has one
        /// (over 5 degrees), else from the direction the hand would move if the rest
        /// arm were flexed a little - forward, on a palm-down T-pose. fromBend says
        /// which one was used.
        ///
        /// A rig constant, so it cannot drift or flip the way the animated-pose
        /// measurement it replaces did once the arms stopped animating (TEST 257).
        /// </summary>
        public static Vec3 RestHinge(Vec3 shoulder, Vec3 elbow, Vec3 wrist, Vec3 flexDir,
                                     bool useRestBend, out bool fromBend)
        {
            fromBend = false;

            Vec3 u = elbow - shoulder;
            Vec3 f = wrist - elbow;
            if (u.LengthSquared < 1e-8f || f.LengthSquared < 1e-8f)
                return Vec3.Zero;
            u.Normalize();
            f.Normalize();

            Vec3 n = Vec3.CrossProduct(u, f);
            if (useRestBend && n.Length > 0.087f)
                fromBend = true;
            else
                n = Vec3.CrossProduct(u, Perp(flexDir, u));

            return n.LengthSquared > 1e-8f ? n.NormalizedCopy() : Vec3.Zero;
        }

        /// <summary>
        /// TEST 270. How far the hand is rolled about the forearm from neutral, in
        /// radians, right-handed about foreDir.
        ///
        /// hingeHandLocal is the hand-local axis that lay on the elbow's hinge normal
        /// when the wrist was measured neutral (VrArmIk.TryMeasureHinge). Zero means
        /// the hand is where a relaxed wrist holds it on this arm; plus or minus is
        /// pronation or supination - the twist a real forearm spreads along its length
        /// and, past its range, hands to the elbow.
        /// </summary>
        public static float WristTwist(Mat3 handRot, Vec3 hingeHandLocal, Vec3 foreDir, Vec3 hingeNormal)
        {
            return WristTwist(handRot, hingeHandLocal, foreDir, hingeNormal, out _);
        }

        /// <summary>
        /// As above; conf is how much of the hand's hinge axis lies off the forearm,
        /// 0..1, before normalising. Near 0 the axis runs along the forearm and the
        /// angle is noise over the whole circle - the string hand at the jaw.
        /// </summary>
        public static float WristTwist(Mat3 handRot, Vec3 hingeHandLocal, Vec3 foreDir, Vec3 hingeNormal,
                                       out float conf)
        {
            Vec3 a = handRot.TransformToParent(hingeHandLocal);
            float aLen = a.Length;
            conf = aLen > 1e-6f
                ? (a - foreDir * Vec3.DotProduct(a, foreDir)).Length / aLen
                : 0f;

            Vec3 v = Perp(a, foreDir);
            Vec3 n = Perp(hingeNormal, foreDir);

            if (v.LengthSquared < 1e-8f || n.LengthSquared < 1e-8f)
                return 0f;

            return (float)Math.Atan2(Vec3.DotProduct(Vec3.CrossProduct(n, v), foreDir),
                                     Vec3.DotProduct(n, v));
        }

        /// <summary>
        /// TEST 290. How much of a measured twist to act on. atan2 wraps at +-180,
        /// where the elbow roll jumped ~240 degrees in one frame (the bow-draw and
        /// mounted flips); and a hinge axis along the forearm gives an angle that is
        /// noise. Both fade to 0, so the result is continuous everywhere.
        /// </summary>
        public static float TwistWeight(float twistRad, float conf)
        {
            const float Deg = (float)(Math.PI / 180.0);
            return (1f - Ramp(Math.Abs(twistRad), 120f * Deg, 180f * Deg)) * Ramp(conf, 0.3f, 0.6f);
        }

        /// <summary>
        /// Where the elbow would like to be, before any rule is applied: down and
        /// slightly back, swung outward when the hand crosses the midline.
        /// </summary>
        private static Vec3 Desired(Vec3 toTarget, Vec3 x, Vec3 outward,
                                    float reach, float d, float swingGain)
        {
            Vec3 bend = Perp(-Up - Forward * 0.3f, x);

            if (bend.LengthSquared < 1e-8f)
                bend = Perp(outward, x);

            if (bend.LengthSquared < 1e-8f)
                return Vec3.Zero;

            if (swingGain <= 0f)
                return bend;

            // Reaching across the midline, a real elbow swings out; held down it
            // would have to pass through the ribs. Measured against the arm's own
            // length, so it is scale-free and works on a child or a giant.
            float inward = -Vec3.DotProduct(toTarget, outward) / reach;
            float cross = Ramp(inward, 0.05f, 0.30f);

            // A chest-level aid. With the hand raised to face height a real elbow
            // stays DOWN; applying the swing there flares it into a pose nobody
            // holds and which is tiring to look at.
            float low = 1f - Ramp(Vec3.DotProduct(toTarget, Up) / reach, 0.05f, 0.35f);

            // Near full stretch the bend direction barely moves the elbow, but
            // continuing to blend it produces a visible sideways twitch exactly at
            // the limit.
            float bent = 1f - Ramp(d / reach, 0.90f, 1f);

            float f = swingGain * cross * low * bent;
            if (f > 1f) f = 1f;
            if (f <= 0f)
                return bend;

            Vec3 outPerp = Perp(outward, x);
            if (outPerp.LengthSquared < 1e-8f)
                return bend;

            Vec3 mixed = Perp(bend * (1f - f) + outPerp * f, x);
            return mixed.LengthSquared > 1e-8f ? mixed : bend;
        }

        /// <summary>
        /// The point on the circle that satisfies the rules and is otherwise as
        /// close as possible to what was wanted.
        ///
        /// Searched rather than solved in closed form. Each rule is an arc, and
        /// intersecting several arcs on a circle with a sensible answer when they
        /// do not overlap is a good deal more code than a sweep - and a sweep at
        /// half a degree is already finer than the tracking noise it is fed. The
        /// cost is a few thousand multiply-adds per arm per frame.
        /// </summary>
        private static Vec3 Best(Vec3 wanted, Vec3 u, Vec3 v, Rule[] rules, int n,
                                 Vec3 prefer = default(Vec3))
        {
            float wantedAngle = (float)Math.Atan2(Vec3.DotProduct(wanted, v),
                                                  Vec3.DotProduct(wanted, u));

            // TEST 453: last frame's elbow, on this frame's circle.
            float pu = Vec3.DotProduct(prefer, u), pv = Vec3.DotProduct(prefer, v);
            bool havePrefer = Stick > 0f && pu * pu + pv * pv > 0.09f;
            float preferAngle = havePrefer ? (float)Math.Atan2(pv, pu) : 0f;
            float nearScore = float.MaxValue;
            Vec3 near = wanted;

            float bestScore = float.MaxValue;
            Vec3 best = wanted;

            for (int i = 0; i < Steps; i++)
            {
                float angle = (float)(i * (2.0 * Math.PI / Steps) - Math.PI);
                float c = (float)Math.Cos(angle);
                float s = (float)Math.Sin(angle);

                Vec3 candidate = u * c + v * s;

                float score = 0f;
                for (int r = 0; r < n; r++)
                {
                    float have = Vec3.DotProduct(candidate, rules[r].N);
                    float missing = rules[r].Min - have;
                    if (missing > 0f)
                        score += missing * rules[r].Weight;
                }

                // Among equally legal directions, the one nearest what was wanted.
                // Scaled far below any real violation so it only ever breaks ties.
                score += AngleBetween(angle, wantedAngle) * 1e-3f;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }

                // Near last frame's elbow, a tie goes to the closer one (ten times
                // weaker than the pull toward `wanted`, so it never holds a move).
                float fromPrefer = havePrefer ? AngleBetween(angle, preferAngle) : 0f;
                if (havePrefer && fromPrefer <= StickWindow && score + fromPrefer * 1e-4f < nearScore)
                {
                    nearScore = score + fromPrefer * 1e-4f;
                    near = candidate;
                }
            }

            if (havePrefer && nearScore <= bestScore + Stick)
                best = near;

            float len = best.Length;
            return len > 1e-6f ? best * (1f / len) : Vec3.Zero;
        }

        /// <summary>Absolute angular distance, wrapped to at most pi.</summary>
        private static float AngleBetween(float a, float b)
        {
            float diff = a - b;
            while (diff > Math.PI) diff -= (float)(2.0 * Math.PI);
            while (diff < -Math.PI) diff += (float)(2.0 * Math.PI);
            return Math.Abs(diff);
        }

        /// <summary>
        /// Eases the bend direction toward a new one, rate-limited, and puts the
        /// result back on the elbow's circle.
        ///
        /// The direction is already a continuous function of the target, so this is
        /// not covering for a jump. It is for the speed of an honest change: moving
        /// the hand across the body swings the elbow through a large arc correctly,
        /// and done in a single frame that reads as a snap. A real arm re-poses over
        /// something like a tenth of a second.
        ///
        /// THE RE-PROJECTION AT THE END IS NOT OPTIONAL. Interpolating between two
        /// unit vectors gives a vector that is neither unit nor perpendicular to the
        /// arm axis, and the cosine-law placement assumes both. Skipping it puts the
        /// elbow off its circle, which changes the segment lengths - the arm appears
        /// to grow and shrink while it turns.
        ///
        /// Pure: previous state comes in as an argument and the result goes out. It
        /// never reaches Direction, so the choice of direction stays a function of
        /// the target alone and cannot form the feedback loop described above.
        /// </summary>
        public static Vec3 Smooth(Vec3 previous, bool hasPrevious, Vec3 desired, Vec3 armAxis,
                                  float dt, float tauSeconds, float rateCapDegPerSec)
        {
            if (!hasPrevious || previous.LengthSquared < 1e-8f)
                return desired;

            if (desired.LengthSquared < 1e-8f)
                return previous;

            if (dt <= 0f)
                return previous;

            if (dt > 0.05f)
                dt = 0.05f;             // a hitch must not teleport the elbow

            float alpha = tauSeconds > 1e-4f
                ? 1f - (float)Math.Exp(-dt / tauSeconds)
                : 1f;

            float dot = Vec3.DotProduct(previous, desired);
            if (dot > 1f) dot = 1f;
            if (dot < -1f) dot = -1f;

            float angle = (float)Math.Acos(dot);
            float maxStep = rateCapDegPerSec * (float)(Math.PI / 180.0) * dt;

            if (angle > 1e-4f && angle * alpha > maxStep)
                alpha = maxStep / angle;

            Vec3 mixed = previous + (desired - previous) * alpha;

            if (mixed.LengthSquared < 1e-6f)
                return desired;         // exactly antiparallel; snap and smooth from there

            Vec3 onCircle = Perp(mixed, armAxis);
            return onCircle.LengthSquared > 1e-8f ? onCircle : desired;
        }

        /// <summary>
        /// The component of v perpendicular to unit axis, normalised. Returns zero
        /// when v is parallel to the axis and names no direction.
        /// </summary>
        public static Vec3 Perp(Vec3 v, Vec3 axis)
        {
            Vec3 r = v - axis * Vec3.DotProduct(v, axis);
            float len = r.Length;
            if (len < 1e-5f)
                return Vec3.Zero;

            return r * (1f / len);
        }

        /// <summary>0 below lo, 1 above hi, linear between. Every gate above is one
        /// of these, so they read as "from here to here" rather than as arithmetic.</summary>
        public static float Ramp(float v, float lo, float hi)
        {
            if (hi - lo < 1e-6f)
                return v >= hi ? 1f : 0f;

            float t = (v - lo) / (hi - lo);
            if (t < 0f) return 0f;
            if (t > 1f) return 1f;
            return t;
        }
    }
}
