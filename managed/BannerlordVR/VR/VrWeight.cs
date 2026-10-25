using System;
using System.Diagnostics;
using System.Globalization;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 309: what a held weapon weighs, the Blade &amp; Sorcery way. Each
    /// hand's target trails its controller on a damped spring whose stiffness is
    /// the item's own numbers - ItemObject.Weight for moving it, the weapon's
    /// TotalInertia for turning it - and a weapon held out level sags by its
    /// centre of mass. A two-hander gripped in both hands (VrTwoHand) is
    /// weight_twohand_mult times stiffer, and its pitch is steered through the
    /// off hand, so two hands is what makes it responsive.
    ///
    /// Applied to the hand TARGET, before VrTwoHand and the arm solve, so the
    /// arm, the wrist, the drawn weapon and the blade every hit is measured from
    /// all carry it: a heavy weapon's tip is simply slower, and the hit-speed
    /// gates in VrPhysicalCombat see that without being told.
    ///
    /// The spring runs in the camera anchor's frame - the one StageToHand puts
    /// the controllers through - so walking, riding and turning carry the weapon
    /// along; only the hand's own motion against the body lags.
    /// </summary>
    internal static class VrWeight
    {
        /// <summary>TEST 309b: not gated on skin_tune - the live cfg keeps the
        /// tuner key on all the time, and that gate is why TEST 309 never trailed.</summary>
        private static readonly bool Enabled = VrConfig.Bool("weight", true);

        /// <summary>Turning: omega = weight_turn / (hand inertia + item inertia),
        /// clamped. 1/I rather than physics' 1/sqrt(I), so the step from a sword to
        /// a two-hander is felt the way Blade &amp; Sorcery makes it felt. TEST 309's
        /// log: one-handed swords 0.16-0.29, a two-handed axe 0.76 - 13.5 puts the
        /// swords at 45-60 rad/s and the axe at 17.</summary>
        private static readonly float TurnRef = Math.Max(0.1f, VrConfig.Float("weight_turn", 13.5f));
        private static readonly float HandInertia = Math.Max(0.001f, VrConfig.Float("weight_hand_inertia", 0.02f));
        private static readonly float RotMin = VrConfig.Float("weight_rot_min", 6f);
        private static readonly float RotMax = VrConfig.Float("weight_rot_max", 60f);

        /// <summary>A shield's TotalInertia is the engine's Weight x 0.05, which
        /// makes a 3 kg kite turn like a sword. Its mass sits 0.3 m round the grip:
        /// Weight x this instead.</summary>
        private static readonly float ShieldInertiaPerKg = VrConfig.Float("weight_shield_inertia_per_kg", 0.12f);

        /// <summary>TEST 562: a crossbow's inertia, Weight x this - its prod and
        /// stock spread ~0.45 m round the left hand. 0.2 turns a 3 kg crossbow at
        /// ~22 rad/s one-handed (a two-handed axe is 17), a 1 kg one at the cap.</summary>
        private static readonly float XbowInertiaPerKg = VrConfig.Float("weight_xbow_inertia_per_kg", 0.2f);

        /// <summary>TEST 566: a bow's inertia, max(Weight, 0.5 kg) x this - the
        /// game's bow weights (0.1-0.8 kg) are arbitrary, and a bow's mass sits at
        /// its tips. 0.6 turns noble_bow (0.8 kg) at ~27 rad/s, a 0.3 kg bow at ~43.</summary>
        private static readonly float BowInertiaPerKg = VrConfig.Float("weight_bow_inertia_per_kg", 0.6f);

        /// <summary>Moving: omega = sqrt(k / (arm kg + item kg)), clamped. Kept
        /// stiff - a weapon's weight is felt in turning it, not in carrying it.</summary>
        private static readonly float PosK = Math.Max(1f, VrConfig.Float("weight_pos_k", 27000f));
        private static readonly float ArmKg = Math.Max(0.1f, VrConfig.Float("weight_arm_kg", 3f));
        private static readonly float PosMin = VrConfig.Float("weight_pos_min", 30f);
        private static readonly float PosMax = VrConfig.Float("weight_pos_max", 90f);

        /// <summary>Damping ratio. Below 1 the weapon overshoots a little when
        /// the hand stops - that carry-on is the momentum.</summary>
        private static readonly float Damping = Math.Max(0.1f, VrConfig.Float("weight_damping", 0.75f));

        private static readonly float TwoHandMult = Math.Max(1f, VrConfig.Float("weight_twohand_mult", 4f));

        /// <summary>TEST 496: an EMPTY hand trails too, lightly - stiffer than any
        /// weapon (a sword moves at ~78 rad/s, turns at ~44) and nearly critically
        /// damped, so it lags ~0.7 cm at 0.5 m/s, ~2 cm at 1.5 m/s and never swings
        /// past. 0 for either omega: the hand is locked to the controller again.</summary>
        private static readonly float HandPos = VrConfig.Float("weight_hand_pos_omega", 110f);
        private static readonly float HandRot = VrConfig.Float("weight_hand_rot_omega", 90f);
        private static readonly float HandDamp = Math.Max(0.1f, VrConfig.Float("weight_hand_damping", 0.9f));

        /// <summary>Sag: mass x g x centre of mass / this, in N·m per radian. 60
        /// droops a level two-handed axe ~14 deg one-handed and a sword 4-6.</summary>
        private static readonly float SagK = Math.Max(1f, VrConfig.Float("weight_sag_k", 60f));
        private static readonly float MaxSagRad = VrConfig.Float("weight_max_sag_deg", 25f) * DegToRad;

        /// <summary>The furthest the weapon may trail the controller; past this
        /// it is held at the limit, so it never leaves the hand.</summary>
        private static readonly float MaxLagM = VrConfig.Float("weight_max_lag_m", 0.12f);
        private static readonly float MaxLagRad = VrConfig.Float("weight_max_lag_deg", 50f) * DegToRad;

        /// <summary>TEST 475: a siege rock or fire pot (WeaponClass.Boulder, 15 kg)
        /// is CARRIED, not wielded: a soft position spring, sqrt(k / (arm + item)),
        /// and the droop gravity gives that spring, g / omega^2 - ~9 rad/s and
        /// ~0.12 m for a boulder. An off-hand grip under it (SupportHand) makes it
        /// weight_twohand_mult stiffer, which is also what takes the droop out.</summary>
        private static readonly float CarryPosK = Math.Max(1f, VrConfig.Float("weight_carry_pos_k", 1500f));
        private static readonly float CarryPosMin = VrConfig.Float("weight_carry_pos_min", 5f);
        private static readonly float CarryDroopMax = VrConfig.Float("weight_carry_droop_max_m", 0.15f);
        private static readonly float CarryMaxLagM = VrConfig.Float("weight_carry_max_lag_m", 0.30f);
        internal static readonly float SupportM = VrConfig.Float("weight_carry_support_m", 0.25f);

        private const float DegToRad = (float)(Math.PI / 180.0);
        private const float Gravity = 9.81f;

        /// <summary>omega x substep at most this, so the integration stays stable
        /// at any frame rate.</summary>
        private const float MaxPhase = 0.2f;

        private sealed class Hand
        {
            public string Item;     // null: nothing weighted, the target passes through; "": an empty hand (TEST 496)
            public bool Carry;      // TEST 475: a rock or pot, carried
            public float Mass;
            public float Com;
            public float RotOmega;
            public float PosOmega;
            public float Damp;

            public bool Snap = true;
            public long SteppedTick = -1;
            public MatrixFrame Out;

            // Anchor-local state.
            public Vec3 P;
            public Vec3 V;
            public Mat3 R = Mat3.Identity;
            public Vec3 W;

            public bool HaveAxis;
            public Vec3 AxisInHand;

            public float PeakM;
            public float PeakDeg;
            public int Clamps;
        }

        private static readonly Hand[] _hands = { new Hand(), new Hand() };
        private static long _tick;
        private static long _stamp;
        private static float _dt;
        private static bool _gripping;
        private static bool _loggedHand;

        /// <summary>In two hands or easing in/out (TEST 304e): the main hand is
        /// turned off its target, so no sag and no blade-axis reading.</summary>
        private static bool _twoHanded;

        /// <summary>TEST 562: a crossbow in both hands - the right shares its weight.</summary>
        private static bool _xbowBoth;
        private static bool _mounted;
        private static Mission _mission;
        private static long _traceAt;

        /// <summary>TEST 475: the off controller gripping under the carried rock,
        /// or null. Set and cleared by VrGrab.</summary>
        internal static VrHand? SupportHand;

        /// <summary>TEST 475: the main hand carries a rock or pot.</summary>
        internal static bool Carrying => _hands[0].Item != null && _hands[0].Carry;

        /// <summary>Once per tick, before VrTwoHand and either hand: the clock,
        /// what each hand holds, and when to snap instead of trail.</summary>
        internal static void BeginTick(Agent agent, VrHandSlot main, VrHandSlot off)
        {
            _tick++;

            long now = Stopwatch.GetTimestamp();
            float dt = _stamp == 0L ? 0f : (float)((now - _stamp) / (double)Stopwatch.Frequency);
            _stamp = now;
            _dt = Math.Min(dt, 0.1f);

            bool newMission = !ReferenceEquals(_mission, Mission.Current);
            _mission = Mission.Current;

            // A hitch, a load, a calibration: the weapon is where the hand is.
            bool snap = !Enabled || dt <= 0f || dt > 0.25f || newMission || VrHandCalibration.Capturing;

            _mounted = agent.HasMount;
            _gripping = VrTwoHand.Gripping != null;
            _twoHanded = VrTwoHand.Active;

            // TEST 562: a crossbow rides the off wrist (373c): that hand carries its
            // weight, and the main hand too while it is on the stock. TEST 566: a
            // bow rides it too, and weighs on it alone (the string hand is free).
            bool xbow = main.Valid && main.Carrier == HandRole.Off && main.Equipment != EquipmentIndex.None;
            _xbowBoth = xbow && VrCrossbow.Active && VrCrossbow.Holds(main.Controller);
            if (xbow)
            {
                Resolve(agent, main, _hands[1], snap, false, true);
                if (_xbowBoth)
                    Share(_hands[1], _hands[0], snap);
                else
                    Resolve(agent, main, _hands[0], snap, false);
            }
            else
            {
                Resolve(agent, main, _hands[0], snap, Light(main, off));

                // Both hands on one weapon: the off hand carries it too.
                if (_gripping)
                    Share(_hands[0], _hands[1], snap);
                else
                    Resolve(agent, off, _hands[1], snap, Light(off, main));
            }

            MeasureAxis(agent, main, _hands[0]);

            if (!Carrying)
                SupportHand = null;
        }

        /// <summary>The hand's target, trailed by what it holds. Steps once per
        /// tick; a second call (VrTwoHand reads the off target ahead of its own
        /// pass) returns the same frame.</summary>
        internal static void Follow(HandRole role, ref MatrixFrame world)
        {
            Hand hand = _hands[(int)role];
            if (hand.SteppedTick == _tick)
            {
                world = hand.Out;
                return;
            }

            bool continuous = hand.SteppedTick == _tick - 1;
            hand.SteppedTick = _tick;

            MatrixFrame anchor = VrCameraDriver.Anchor;
            Vec3 targetP = anchor.rotation.TransformToLocal(world.origin - anchor.origin);
            Mat3 targetR = anchor.rotation.TransformToLocal(world.rotation);

            if (hand.Item == null || hand.Snap || !continuous)
            {
                hand.Snap = false;
                hand.P = targetP;
                hand.V = Vec3.Zero;
                hand.R = targetR;
                hand.W = Vec3.Zero;
                hand.Out = world;
                return;
            }

            float stiff = _gripping || _xbowBoth || (hand.Carry && SupportHand != null) ? (float)Math.Sqrt(TwoHandMult) : 1f;

            // TEST 475: the droop of a carried weight - where a spring this soft
            // settles under g.
            if (hand.Carry)
            {
                float omega = hand.PosOmega * stiff;
                Vec3 down = anchor.rotation.TransformToLocal(new Vec3(0f, 0f, -1f));
                targetP += down * Math.Min(Gravity / Math.Max(1f, omega * omega), CarryDroopMax);
            }

            // ponytail: no sag on a horse - a couched lance is braced under the
            // arm. Add a mounted factor if riders want the droop.
            if (role == HandRole.Main && !_twoHanded && !_xbowBoth && !_mounted && hand.HaveAxis)
            {
                Vec3 axis = targetR.TransformToParent(hand.AxisInHand);
                Vec3 down = anchor.rotation.TransformToLocal(new Vec3(0f, 0f, -1f));
                Vec3 sag = Sag(axis, down, hand.Mass, hand.Com, SagK, MaxSagRad);
                targetR = Turn(targetR, sag);
            }

            Spring(ref hand.P, ref hand.V, targetP, hand.PosOmega * stiff, hand.Damp, _dt);
            SpringTurn(ref hand.R, ref hand.W, targetR, hand.RotOmega * stiff, hand.Damp, _dt);

            float lagM = ClampMove(ref hand.P, ref hand.V, targetP, hand.Carry ? CarryMaxLagM : MaxLagM,
                                   out bool clampedMove);
            float lagRad = ClampTurn(ref hand.R, ref hand.W, targetR, MaxLagRad, out bool clampedTurn);
            if (clampedMove || clampedTurn)
                hand.Clamps++;

            hand.PeakM = Math.Max(hand.PeakM, lagM);
            hand.PeakDeg = Math.Max(hand.PeakDeg, lagRad / DegToRad);

            Mat3 r = hand.R;
            r.Orthonormalize();
            hand.R = r;

            world.origin = anchor.origin + anchor.rotation.TransformToParent(hand.P);
            world.rotation = anchor.rotation.TransformToParent(hand.R);
            hand.Out = world;

            Trace();
        }

        /// <summary>TEST 562: where this tick's weighted target puts the hand -
        /// false while nothing weighs on it or it has not stepped this tick.</summary>
        internal static bool TryOut(HandRole role, out Vec3 point)
        {
            Hand hand = _hands[(int)role];
            point = hand.Out.origin;
            return Enabled && !string.IsNullOrEmpty(hand.Item) && hand.SteppedTick == _tick;
        }

        internal static void Reset()
        {
            foreach (Hand hand in _hands)
            {
                hand.Item = null;
                hand.Snap = true;
                hand.SteppedTick = -1;
                hand.HaveAxis = false;
            }

            _stamp = 0L;
            _mission = null;
        }

        /// <summary>TEST 496: an empty hand that may trail - not one that aims: it
        /// carries the other hand's bow or crossbow, draws the bow's string, or a
        /// crossbow is held.</summary>
        private static bool Light(VrHandSlot slot, VrHandSlot other)
        {
            return HandPos > 0f && HandRot > 0f && slot.Valid && slot.Equipment == EquipmentIndex.None
                   && !(other.Valid && other.Equipment != EquipmentIndex.None && other.Carrier == slot.Role)
                   && !(VrBow.HoldsArrow && VrBow.StringController == slot.Controller) && !VrCrossbow.Active;
        }

        /// <param name="crossbow">TEST 562: <paramref name="slot"/> is the crossbow
        /// another hand carries, and <paramref name="hand"/> is that hand.</param>
        private static void Resolve(Agent agent, VrHandSlot slot, Hand hand, bool snap, bool light, bool crossbow = false)
        {
            string item = null;
            bool carry = false;
            float mass = 0f;
            float inertia = 0f;
            float com = 0f;
            string what = null;

            try
            {
                // Melee weapons and shields: a bow is aimed, and a trailing aim is
                // just a worse aim. TEST 562: a crossbow is heavy enough that its
                // weight is the point. TEST 664: a throwing weapon too - VrThrow takes
                // the throw's speed and direction from the controller itself, so only
                // what is seen trails.
                if (slot.Valid && slot.Equipment != EquipmentIndex.None && (slot.Carrier == slot.Role || crossbow))
                {
                    MissionWeapon weapon = agent.Equipment[slot.Equipment];
                    WeaponComponentData usage = weapon.IsEmpty ? null : weapon.CurrentUsageItem;
                    // TEST 475: and a siege rock or pot, which is carried.
                    carry = usage != null && usage.WeaponClass == WeaponClass.Boulder;
                    bool xbow = crossbow && usage != null && usage.IsCrossBow;
                    bool bow = crossbow && usage != null && usage.IsBow;
                    bool thrown = weapon.Item?.ItemType == ItemObject.ItemTypeEnum.Thrown;
                    if (usage != null && weapon.Item != null && (usage.IsShield || usage.IsMeleeWeapon || carry || xbow || bow || thrown))
                    {
                        item = weapon.Item.StringId ?? "?";
                        mass = Math.Max(0f, weapon.Item.Weight);
                        // TEST 664: Item.Weight is one piece, but a consumable usage's
                        // TotalInertia is the whole stack's (x MaxDataValue) - one in hand.
                        inertia = usage.IsShield ? mass * ShieldInertiaPerKg
                            : xbow ? mass * XbowInertiaPerKg
                            : bow ? Math.Max(mass, 0.5f) * BowInertiaPerKg
                            : Math.Max(0f, usage.TotalInertia) / (usage.IsConsumable ? Math.Max(1, (int)usage.MaxDataValue) : 1);
                        com = usage.IsShield || xbow || bow ? 0f : Math.Max(0f, usage.CenterOfMass);
                        what = usage.WeaponClass.ToString();
                    }
                }
            }
            catch (Exception)
            {
                item = null;
            }

            if (item == null && light)
                item = string.Empty;

            if (snap)
                hand.Snap = true;

            if (item == hand.Item)
                return;

            hand.Item = item;
            hand.Snap = true;
            hand.HaveAxis = false;

            if (item == null)
                return;

            if (item.Length == 0)
            {
                hand.Carry = false;
                hand.Mass = hand.Com = 0f;
                hand.PosOmega = HandPos;
                hand.RotOmega = HandRot;
                hand.Damp = HandDamp;
                if (!_loggedHand)
                {
                    _loggedHand = true;
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 496: empty hands trail their controllers lightly - moves at {0:F0} rad/s, turns at {1:F0}, "
                        + "damping {2:F2}. weight_hand_pos_omega = 0 locks them again.", HandPos, HandRot, HandDamp));
                }

                return;
            }

            hand.Damp = Damping;
            hand.Carry = carry;
            hand.Mass = mass;
            hand.Com = com;
            hand.RotOmega = Clamp(TurnRef / (HandInertia + inertia), RotMin, RotMax);
            hand.PosOmega = carry
                ? Clamp((float)Math.Sqrt(CarryPosK / (ArmKg + mass)), CarryPosMin, PosMax)
                : Clamp((float)Math.Sqrt(PosK / (ArmKg + mass)), PosMin, PosMax);

            if (carry)
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 475 carry '{0}': {1:F1} kg, moves at {2:F1} rad/s, droops {3:F2} m ({4:F2} m with the off "
                    + "hand under it), trails up to {5:F2} m.", item, mass, hand.PosOmega,
                    Math.Min(Gravity / (hand.PosOmega * hand.PosOmega), CarryDroopMax),
                    Math.Min(Gravity / (hand.PosOmega * hand.PosOmega * TwoHandMult), CarryDroopMax), CarryMaxLagM));

            float stiff = (float)Math.Sqrt(TwoHandMult);
            float sagDeg = Math.Min(mass * Gravity * com / SagK, MaxSagRad) / DegToRad;
            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 309 weight '{0}' ({1}): {2:F2} kg, inertia {3:F3}, centre of mass {4:F2} m -> "
                + "turns at {5:F0} rad/s ({6:F0} in two hands), moves at {7:F0} rad/s, sags {8:F0} deg "
                + "held level in one hand. weight = 0 turns it off.",
                item, what, mass, inertia, com, hand.RotOmega, hand.RotOmega * stiff,
                hand.PosOmega, sagDeg));
        }

        /// <summary>The off hand on the shaft carries the main hand's weapon.</summary>
        private static void Share(Hand main, Hand off, bool snap)
        {
            if (snap)
                off.Snap = true;

            if (off.Item == main.Item)
                return;

            // From passing through to trailing is seamless: its state is the
            // target until now.
            off.Item = main.Item;
            off.Mass = main.Mass;
            off.Com = main.Com;
            off.RotOmega = main.RotOmega;
            off.PosOmega = main.PosOmega;
            off.Damp = main.Damp;
        }

        /// <summary>The blade's direction in the main hand's frame, from last
        /// tick's blade and last tick's target - they belong to each other. Only
        /// one-handed: a two-hand grip turns the hand off the target.</summary>
        private static void MeasureAxis(Agent agent, VrHandSlot main, Hand hand)
        {
            if (string.IsNullOrEmpty(hand.Item) || _twoHanded || hand.SteppedTick != _tick - 1)
                return;

            if (!VrBlade.TryGet(agent, main, out VrBlade.Segment blade))
                return;

            Vec3 axis = hand.Out.rotation.TransformToLocal(blade.AxisWorld);
            if (axis.LengthSquared < 0.25f)
                return;

            hand.AxisInHand = axis.NormalizedCopy();
            hand.HaveAxis = true;
        }

        private static void Trace()
        {
            long now = Stopwatch.GetTimestamp();
            if (now < _traceAt)
                return;

            _traceAt = now + 2 * Stopwatch.Frequency;

            for (int i = 0; i < _hands.Length; i++)
            {
                Hand hand = _hands[i];
                if (hand.Item != null && (hand.PeakM > 0.01f || hand.PeakDeg > 3f))
                {
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 309 lag {0} '{1}'{2}: up to {3:F3} m and {4:F0} deg behind the controller "
                        + "in the last 2 s, {5} tick(s) at the limit.",
                        (HandRole)i, hand.Item.Length == 0 ? "empty hand, TEST 496" : hand.Item, _gripping ? " (two hands)" : string.Empty,
                        hand.PeakM, hand.PeakDeg, hand.Clamps));
                }

                hand.PeakM = 0f;
                hand.PeakDeg = 0f;
                hand.Clamps = 0;
            }
        }

        // --- pure, for the tests ---------------------------------------------

        private static float Clamp(float value, float min, float max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        /// <summary>A damped spring pulling p to target, substepped so it is
        /// stable at any dt.</summary>
        internal static void Spring(ref Vec3 p, ref Vec3 v, Vec3 target, float omega, float zeta, float dt)
        {
            int steps = Steps(omega, dt);
            float h = dt / steps;

            for (int i = 0; i < steps; i++)
            {
                v += ((target - p) * (omega * omega) - v * (2f * zeta * omega)) * h;
                p += v * h;
            }
        }

        /// <summary>The same spring on a rotation: the error is the rotation
        /// vector from r to target, w the angular velocity, both in r's parent.</summary>
        private static void SpringTurn(ref Mat3 r, ref Vec3 w, Mat3 target, float omega, float zeta, float dt)
        {
            int steps = Steps(omega, dt);
            float h = dt / steps;

            for (int i = 0; i < steps; i++)
            {
                Vec3 error = Between(r, target);
                w += (error * (omega * omega) - w * (2f * zeta * omega)) * h;
                r = Turn(r, w * h);
            }
        }

        private static int Steps(float omega, float dt)
        {
            int steps = (int)Math.Ceiling(omega * dt / MaxPhase);
            return Math.Max(1, Math.Min(16, steps));
        }

        /// <summary>Holds p within max of target and drops the velocity that
        /// would carry it further out. Returns the gap before clamping.</summary>
        private static float ClampMove(ref Vec3 p, ref Vec3 v, Vec3 target, float max, out bool clamped)
        {
            Vec3 gap = p - target;
            float length = gap.Length;
            clamped = length > max && length > 1e-6f;
            if (!clamped)
                return length;

            Vec3 n = gap * (1f / length);
            p = target + n * max;
            float outward = Vec3.DotProduct(v, n);
            if (outward > 0f)
                v -= n * outward;

            return length;
        }

        private static float ClampTurn(ref Mat3 r, ref Vec3 w, Mat3 target, float max, out bool clamped)
        {
            Vec3 error = Between(r, target);        // r -> target
            float angle = error.Length;
            clamped = angle > max && angle > 1e-6f;
            if (!clamped)
                return angle;

            Vec3 n = error * (1f / angle);
            r = Turn(target, n * -max);
            float away = Vec3.DotProduct(w, n);     // negative = turning away from the target
            if (away < 0f)
                w -= n * away;

            return angle;
        }

        /// <summary>The rotation vector v with to = Turn(from, v): axis times
        /// angle, in the frames' parent. Sum of from_i x to_i is 2 sin(angle) times
        /// the axis; the trace gives the cosine; at 180 deg the axis comes from
        /// the symmetric part instead.</summary>
        internal static Vec3 Between(Mat3 from, Mat3 to)
        {
            Vec3 cross = Vec3.CrossProduct(from.s, to.s)
                       + Vec3.CrossProduct(from.f, to.f)
                       + Vec3.CrossProduct(from.u, to.u);

            float cos = (Vec3.DotProduct(from.s, to.s) + Vec3.DotProduct(from.f, to.f)
                         + Vec3.DotProduct(from.u, to.u) - 1f) * 0.5f;
            cos = Math.Max(-1f, Math.Min(1f, cos));
            float sin = cross.Length * 0.5f;
            float angle = (float)Math.Atan2(sin, cos);

            if (sin > 1e-4f)
                return cross * (angle / (2f * sin));

            if (cos > 0f)
                return cross * 0.5f;

            // Half a turn: R + I = 2 a a^T. Column k of R is sum to_i * from_i[k].
            Vec3 best = Vec3.Zero;
            for (int k = 0; k < 3; k++)
            {
                Vec3 column = to.s * from.s[k] + to.f * from.f[k] + to.u * from.u[k];
                column[k] += 1f;
                if (column.LengthSquared > best.LengthSquared)
                    best = column;
            }

            return best.LengthSquared < 1e-8f ? Vec3.Zero : best.NormalizedCopy() * (float)Math.PI;
        }

        /// <summary>The rotation t of the way from a to b (0 = a, 1 = b), on the
        /// shortest arc.</summary>
        internal static Mat3 Blend(Mat3 a, Mat3 b, float t)
        {
            return Turn(a, Between(a, b) * t);
        }

        /// <summary>m turned by the rotation vector v, in m's parent.</summary>
        internal static Mat3 Turn(Mat3 m, Vec3 v)
        {
            float angle = v.Length;
            if (angle < 1e-7f)
                return m;

            return VrArmIk.Rotate(m, v * (1f / angle), (float)Math.Cos(angle), (float)Math.Sin(angle));
        }

        /// <summary>The droop gravity gives a blade along `axis`: about axis x
        /// down (which turns the tip down), by mass g com sin(tilt) / k, at most
        /// max radians. A blade pointing straight up or down does not droop.</summary>
        private static Vec3 Sag(Vec3 axis, Vec3 down, float mass, float com, float k, float max)
        {
            Vec3 lever = Vec3.CrossProduct(axis.NormalizedCopy(), down.NormalizedCopy());
            float sin = lever.Length;
            if (sin < 1e-4f || mass <= 0f || com <= 0f)
                return Vec3.Zero;

            float angle = Math.Min(mass * Gravity * com * sin / k, max);
            return lever * (angle / sin);
        }
    }
}
