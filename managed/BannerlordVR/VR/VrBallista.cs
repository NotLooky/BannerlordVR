using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using BannerlordVR.Interop;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using WeaponState = TaleWorlds.MountAndBlade.RangedSiegeWeapon.WeaponState;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 517: the ballista reloaded by hand - take a bolt from its rack, seat it in
    /// the groove, wind the iron wheels; in either order, and it is ready only when
    /// both are done.
    ///
    /// Vanilla's reload is a state machine: a click after the shot -> Reloading, where
    /// the ballista skeleton plays its set-up animation (the wheels turn, the string
    /// comes back) until its parameter reaches 1 -> LoadingAmmo, where the pilot's
    /// pick-up and place animations bring a bolt from the rack (AmmoCount, drawn by a
    /// shader argument, 8 - count) -> Idle, which consumes one.
    ///
    /// By hand: right after the shot the reload is started (ManualReload) with its
    /// speed held at 0 (SiegeAimPatch's ReloadSpeedMultiplier postfix), so the set-up
    /// animation waits. TEST 519: the iron ring is the skeleton's handwheel bone. The
    /// set-up animation is sampled once (Skeleton.TickAnimationsAndForceUpdate, as
    /// SiegeLadder samples its own) into how far the ring has turned at each point of
    /// it, so the ring follows the hand's turn about its axle 1:1 - it is a ratchet:
    /// forward the hand drives it, backward the hand slips round it - no faster than
    /// ballista_reload_min_s for the whole wind, and the drawn hand rides the ring
    /// where it took hold. A grip on the rack takes a bolt (drawn in the hand, gone from
    /// the rack); brought to the groove (the projectile's own rest frame) it seats.
    /// Wound and seated -> Idle directly (LoadingAmmo's animations are skipped), and
    /// vanilla consumes the bolt there.
    ///
    /// TEST 518: the rack is the prefab's 'projectile_pile' mesh (no standing point
    /// marks it), a grip within ballista_rack_m of its box; with none, any grip on
    /// the ballista away from the wheels and the groove. Native's ballistas have
    /// StartingAmmoCount 0 = unlimited: AmmoCount stays 0 and a bolt is always there.
    /// </summary>
    internal static class VrBallista
    {
        private static readonly bool On = VrConfig.Bool("ballista_manual", true);
        private static readonly bool Probe = VrConfig.Bool("siege_probe", true);
        private static readonly float MinS = Math.Max(0.2f, VrConfig.Float("ballista_reload_min_s", 2.5f));
        private static readonly float WheelM = VrConfig.Float("ballista_wheel_m", 0.30f);
        private static readonly float SeatM = VrConfig.Float("ballista_seat_m", 0.45f);
        private static readonly float SeatCos = (float)Math.Cos(VrConfig.Float("ballista_seat_deg", 90f) * Math.PI / 180.0);
        private static readonly float RackM = VrConfig.Float("ballista_rack_m", 0.75f);

        private const float Wound = 0.998f;
        private const int Samples = 40;

        private static readonly PropertyInfo ProjectileProp = AccessTools.Property(typeof(RangedSiegeWeapon), "Projectile");
        private static readonly FieldInfo SkeletonsField = AccessTools.Field(typeof(RangedSiegeWeapon), "Skeletons");
        private static readonly FieldInfo SetUpField = AccessTools.Field(typeof(RangedSiegeWeapon), "SetUpAnimationIndices");
        private static readonly FieldInfo MissileField = AccessTools.Field(typeof(RangedSiegeWeapon), "OriginalMissileItem");
        private static readonly FieldInfo IdleField = AccessTools.Field(typeof(Ballista), "_idleAnimationActionIndex");
        private static readonly PropertyInfo BodyProp = AccessTools.Property(typeof(Ballista), "ballistaBody");
        private static readonly FieldInfo OwnersField = AccessTools.Field(typeof(RangedSiegeWeapon), "SkeletonOwnerObjects");

        private static Ballista _b;
        private static WeakGameEntity _body;
        private static bool _seated;
        private static float _progress;
        private static WeaponState _last = WeaponState.Invalid;

        // TEST 519: per hand on the ring - its place along the axle and out from it
        // (where it took hold), its last raw angle, its turn (unwrapped, in the
        // ring's winding direction) and where on the ring it holds.
        private static readonly bool[] _crank = new bool[2];
        private static readonly bool[] _crankInit = new bool[2];
        private static readonly float[] _crankAxial = new float[2];
        private static readonly float[] _crankRad = new float[2];
        private static readonly float[] _crankRaw = new float[2];
        private static readonly float[] _crankTurn = new float[2];
        private static readonly float[] _crankOff = new float[2];
        private static bool _cranked;
        private static float _tickAt;

        private static readonly FieldInfo SoundField = AccessTools.Field(typeof(RangedSiegeWeapon), "ReloadSound");

        // TEST 519: the ring - the handwheel bone in the skeleton owner's frame, and
        // its turn over the set-up animation (rising, in its winding direction).
        private static Skeleton _skeleton;
        private static WeakGameEntity _owner;
        private static sbyte _wheel = -1;
        private static bool _ringTried, _ringReady, _noRing;
        private static readonly float[] _ring = new float[Samples + 1];
        private static float _ringSign = 1f, _ring0;
        private static Vec3 _axle, _e1, _e2;

        private static int _boltHand = -1;
        private static GameEntity _bolt;
        private static Mission _boltMission;
        private static MetaMesh _boltMeta;
        private static MatrixFrame _boltLocal;
        private static float _boltLen;

        private static WeakGameEntity _rack;
        private static readonly HashSet<string> _dumped = new HashSet<string>();
        private static long _logAt;
        private static bool _broken;

        /// <summary>The player pilots a ballista in VR and reloads it by hand.</summary>
        internal static bool Manual => On && !_broken && !_noRing && _b != null && VrSystem.IsActive;

        /// <summary>SiegeAimPatch: this ballista's reload waits for the hand.</summary>
        internal static bool HoldsReload(object weapon) => Manual && ReferenceEquals(weapon, _b);

        /// <summary>The trigger may fire it: by hand only when it is ready.</summary>
        internal static bool FireAllowed(RangedSiegeWeapon weapon) =>
            !Manual || !ReferenceEquals(weapon, _b) || weapon.State == WeaponState.Idle;

        /// <summary>This hand cranks a wheel or carries a bolt.</summary>
        internal static bool Holds(int hand) => _crank[hand] || _boltHand == hand;

        /// <summary>TEST 519: how far the ring has turned at animation parameter p,
        /// from the sampled table. Pure.</summary>
        internal static float RingAt(float[] ring, float p)
        {
            int n = ring.Length - 1;
            float x = Math.Max(0f, Math.Min(1f, p)) * n;
            int k = Math.Min(n - 1, (int)x);
            return ring[k] + (ring[k + 1] - ring[k]) * (x - k);
        }

        /// <summary>TEST 519: the parameter at which the ring has turned r - the
        /// table rises. Pure.</summary>
        internal static float ParamFor(float[] ring, float r)
        {
            int n = ring.Length - 1;
            if (r <= ring[0])
                return 0f;
            for (int k = 0; k < n; k++)
            {
                if (r <= ring[k + 1])
                {
                    float span = ring[k + 1] - ring[k];
                    return (k + (span > 1e-6f ? (r - ring[k]) / span : 1f)) / n;
                }
            }
            return 1f;
        }

        /// <summary>TEST 519: a hand on a ratchet ring. Back, it slips round the ring
        /// (where it holds moves with it); forward, it drives. Returns the ring's
        /// turn it asks for - never less than the ring's own. Pure.</summary>
        internal static float Ratchet(float hand, float ring, ref float offset)
        {
            offset = Math.Min(offset, hand - ring);
            return hand - offset;
        }

        /// <summary>TEST 519: where the hand on the ring is drawn - on the ring,
        /// where it took hold, turned with it.</summary>
        internal static bool TryCrankPoint(int hand, out Vec3 at)
        {
            at = Vec3.Zero;
            if (!Manual || !_crank[hand] || !_crankInit[hand] || !_ringReady)
                return false;

            float raw = _ring0 + _ringSign * (RingAt(_ring, _progress) + _crankOff[hand]);
            Vec3 c = _skeleton.GetBoneEntitialFrameWithIndex(_wheel).origin;
            Vec3 p = c + _axle * _crankAxial[hand]
                     + (_e1 * (float)Math.Cos(raw) + _e2 * (float)Math.Sin(raw)) * _crankRad[hand];
            at = _owner.GetGlobalFrame().TransformToParent(p);
            return true;
        }

        /// <summary>From VrSiege when the player takes or leaves a pilot point.</summary>
        internal static void Pilot(RangedSiegeWeapon weapon)
        {
            Leave();
            if (!On || _broken)
                return;

            _b = weapon as Ballista;
            if (_b == null)
                return;

            try
            {
                _body = BodyProp?.GetValue(_b) is SynchedMissionObject body ? body.GameEntity : _b.GameEntity;
                WeaponState state = _b.State;
                _seated = state == WeaponState.Idle || ProjectileShown();
                _progress = state == WeaponState.Idle ? 1f : SetUpParameter();
                _last = state;
                FindWheel();
                FindRack();
                DumpOnce();
                Log("TEST 517: reloading '" + _b.GameEntity.Name + "' by hand (" + state + ", " + _b.AmmoCount
                    + " bolt(s), " + (_seated ? "loaded" : "empty") + ", wound " + F(_progress) + ").");
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>Off the ballista: a bolt in hand goes back on the rack.</summary>
        internal static void Leave()
        {
            if (_b != null && _boltHand >= 0)
                ReturnBolt("left the ballista");
            _crank[0] = _crank[1] = false;
            _crankInit[0] = _crankInit[1] = false;
            _b = null;
            _boltHand = -1;
            HideBolt();
        }

        /// <summary>A grip's first press on the ballista: a wheel while it needs
        /// winding, the rack while it has no bolt. True when it is ours.</summary>
        internal static bool Claim(VrHand hand, BvrHandState state, Vec3 at)
        {
            if (!Manual)
                return false;

            try
            {
                int i = (int)hand;
                LogGrip(hand, at);

                if (NeedsWind() && NearWheel(at, out float dw))
                {
                    _crank[i] = true;
                    _crankInit[i] = false;
                    if (_b.State == WeaponState.WaitingAfterShooting)
                        _b.ManualReload();
                    Pulse(hand, 0.5f, 0.05f);
                    Log("TEST 517 crank: " + hand + " took a wheel (" + F(dw) + " m from it), wound " + F(_progress) + ".");
                    return true;
                }

                if (!_seated && _boltHand < 0 && HasBolt() && NearRack(at, out float dr))
                {
                    TakeBolt(hand, dr);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
            return false;
        }

        /// <summary>A grip let go: true when it was ours.</summary>
        internal static bool Release(VrHand hand)
        {
            int i = (int)hand;
            if (_crank[i])
            {
                _crank[i] = false;
                _crankInit[i] = false;
                Log("TEST 517 crank: " + hand + " let go, wound " + F(_progress) + ".");
                return true;
            }

            if (_boltHand == i)
            {
                ReturnBolt("let go away from the groove");
                return true;
            }
            return false;
        }

        /// <summary>From VrSiege.Tick while the player pilots it.</summary>
        internal static void Tick(BvrInputState input)
        {
            if (!Manual)
                return;

            try
            {
                WeaponState state = _b.State;
                if (state != _last)
                {
                    if (state == WeaponState.WaitingBeforeProjectileLeaving || state == WeaponState.Shooting)
                    {
                        _seated = false;
                        _progress = 0f;
                    }
                    Log("TEST 517: " + _last + " -> " + state + ".");
                    _last = state;
                }

                // Straight on into the reload after the shot; it waits at speed 0.
                if (state == WeaponState.WaitingAfterShooting)
                    _b.ManualReload();

                bool reloading = state == WeaponState.Reloading || state == WeaponState.ReloadingPaused;
                if (reloading && !_ringTried)
                    BuildRing();

                // TEST 519: the ring goes as far as the hand that asks most, no faster
                // than the whole wind in ballista_reload_min_s.
                float now = Mission.Current?.CurrentTime ?? 0f;
                float dt = Math.Max(0f, Math.Min(0.1f, now - _tickAt));
                _tickAt = now;
                float want = _progress;
                for (int i = 0; i < 2; i++)
                {
                    BvrHandState s = i == (int)VrHand.Left ? input.Left : input.Right;
                    if (_crank[i] && s.IsActive != 0 && _ringReady)
                        want = Math.Max(want, Crank(i, VrWeaponHands.HandPoint((VrHand)i, s)));
                }
                if (want > _progress)
                {
                    _progress = Math.Min(Math.Min(1f, want), _progress + dt / MinS);
                    _cranked = true;
                    if (Probe && Stopwatch.GetTimestamp() >= _logAt)
                    {
                        _logAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                        Log("TEST 519 crank: wound " + F(_progress) + " (the hand asks " + F(want) + ").");
                    }
                }

                if (_boltHand >= 0)
                {
                    BvrHandState s = _boltHand == (int)VrHand.Left ? input.Left : input.Right;
                    if (s.IsActive != 0)
                        CarryBolt((VrHand)_boltHand, s);
                }

                if (reloading)
                    Drive();
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        private static bool NeedsWind()
        {
            WeaponState s = _b.State;
            return _progress < Wound
                   && (s == WeaponState.WaitingAfterShooting || s == WeaponState.WaitingBeforeReloading
                       || s == WeaponState.Reloading || s == WeaponState.ReloadingPaused);
        }

        /// <summary>The set-up animation follows the hand; wound and seated, the
        /// ballista is ready.</summary>
        private static void Drive()
        {
            Skeleton skeleton = SkeletonsField?.GetValue(_b) is Skeleton[] all && all.Length > 0 ? all[0] : null;
            int setUp = SetUpField?.GetValue(_b) is int[] idx && idx.Length > 0 ? idx[0] : -1;
            if (skeleton == null || skeleton.GetAnimationIndexAtChannel(0) != setUp)
                return;

            bool done = _progress >= Wound && _seated;
            skeleton.SetAnimationParameterAtChannel(0, done ? 1f : Math.Min(_progress, Wound));

            // Vanilla's winding sound plays through Reloading; by hand only while the
            // hand turns, and it ends with the reload (LoadingAmmo, which stopped it, is
            // skipped).
            SoundEvent sound = SoundField?.GetValue(_b) as SoundEvent;
            bool cranked = _cranked;
            _cranked = false;
            if (sound != null && sound.IsValid)
            {
                if (done)
                {
                    sound.Stop();
                    SoundField.SetValue(_b, null);
                }
                else if (cranked && sound.IsPaused())
                    sound.Resume();
                else if (!cranked && !sound.IsPaused())
                    sound.Pause();
            }

            if (!done)
                return;

            _b.State = WeaponState.Idle;
            Agent pilot = _b.PilotAgent;
            if (pilot != null && IdleField?.GetValue(_b) is ActionIndexCache idle)
                pilot.SetActionChannel(1, in idle);
            _crank[0] = _crank[1] = false;
            Log("TEST 517: wound and loaded - ready (" + _b.AmmoCount + " bolt(s) left after this one).");
        }

        /// <summary>TEST 519: the ring's turn this hand asks for, as a parameter of
        /// the set-up animation. The first frame only takes hold.</summary>
        private static float Crank(int i, Vec3 at)
        {
            OnRing(at, out float axial, out float raw, out float rad);
            float ring = RingAt(_ring, _progress);
            if (!_crankInit[i])
            {
                _crankInit[i] = true;
                _crankAxial[i] = axial;
                _crankRad[i] = Math.Max(0.04f, Math.Min(WheelM, rad));
                _crankRaw[i] = raw;
                _crankOff[i] = VrSiege.WrapPi(_ringSign * (raw - _ring0) - ring);
                _crankTurn[i] = ring + _crankOff[i];
                Log("TEST 519 crank: " + (VrHand)i + " holds the ring " + F(rad) + " m out from its axle, " + F(axial)
                    + " m along it.");
                return _progress;
            }

            // Next to the axle the angle is noise: hold.
            if (rad > 0.03f)
            {
                _crankTurn[i] += _ringSign * VrSiege.WrapPi(raw - _crankRaw[i]);
                _crankRaw[i] = raw;
            }
            return ParamFor(_ring, Ratchet(_crankTurn[i], ring, ref _crankOff[i]));
        }

        /// <summary>The hand about the ring: along its axle, its raw angle in the
        /// ring's plane, and out from the axle - in the skeleton owner's frame.</summary>
        private static void OnRing(Vec3 at, out float axial, out float raw, out float rad)
        {
            Vec3 l = _owner.GetGlobalFrame().TransformToLocalNonOrthogonal(at)
                     - _skeleton.GetBoneEntitialFrameWithIndex(_wheel).origin;
            axial = Vec3.DotProduct(l, _axle);
            float x = Vec3.DotProduct(l, _e1), y = Vec3.DotProduct(l, _e2);
            raw = (float)Math.Atan2(y, x);
            rad = (float)Math.Sqrt(x * x + y * y);
        }

        /// <summary>The ballista skeleton and its handwheel bone (TEST 517's dump:
        /// 'bn_handwheel'). None: the reload stays vanilla.</summary>
        private static void FindWheel()
        {
            _ringTried = _ringReady = _noRing = false;
            _wheel = -1;
            _skeleton = SkeletonsField?.GetValue(_b) is Skeleton[] all && all.Length > 0 ? all[0] : null;
            _owner = OwnersField?.GetValue(_b) is SynchedMissionObject[] owners && owners.Length > 0
                ? owners[0].GameEntity : default;
            if (_skeleton != null && _owner.IsValid)
            {
                for (sbyte k = 0; k < _skeleton.GetBoneCount() && k >= 0; k++)
                {
                    if ((_skeleton.GetBoneName(k) ?? "").ToLowerInvariant().Contains("wheel"))
                    {
                        _wheel = k;
                        break;
                    }
                }
            }
            if (_wheel < 0)
            {
                _noRing = true;
                Log("TEST 519: '" + _b.GameEntity.Name + "' has no wheel bone - it reloads the vanilla way.");
            }
        }

        /// <summary>
        /// TEST 519, once per ballista taken, in Reloading (the set-up animation on
        /// channel 0): the animation sampled at Samples points. The axle is the bone
        /// axis that stays put; the ring's turn is another axis's angle about it,
        /// unwrapped. A ring that barely turns leaves the reload vanilla.
        /// </summary>
        private static void BuildRing()
        {
            int setUp = SetUpField?.GetValue(_b) is int[] idx && idx.Length > 0 ? idx[0] : -1;
            if (_wheel < 0 || _skeleton.GetAnimationIndexAtChannel(0) != setUp)
                return;     // not on the set-up animation yet: next tick
            _ringTried = true;

            MatrixFrame g = _owner.GetGlobalFrame();
            float keep = _skeleton.GetAnimationParameterAtChannel(0);
            var rot = new Mat3[Samples + 1];
            for (int k = 0; k <= Samples; k++)
            {
                _skeleton.SetAnimationParameterAtChannel(0, k / (float)Samples);
                _skeleton.TickAnimationsAndForceUpdate(0.0001f, g, false);
                rot[k] = _skeleton.GetBoneEntitialFrameWithIndex(_wheel).rotation;
            }
            _skeleton.SetAnimationParameterAtChannel(0, keep);
            _skeleton.TickAnimationsAndForceUpdate(0.0001f, g, false);

            int axle = 0;
            float held = -1f;
            for (int c = 0; c < 3; c++)
            {
                float worst = 1f;
                for (int k = 1; k <= Samples; k++)
                    worst = Math.Min(worst, Math.Abs(Vec3.DotProduct(Unit(Axis(rot[k], c)), Unit(Axis(rot[0], c)))));
                if (worst > held)
                {
                    held = worst;
                    axle = c;
                }
            }
            int spoke = (axle + 1) % 3;
            _axle = Unit(Axis(rot[0], axle));
            Vec3 s0 = Unit(Axis(rot[0], spoke));
            _e1 = Unit(s0 - _axle * Vec3.DotProduct(s0, _axle));
            _e2 = Vec3.CrossProduct(_axle, _e1);

            float prev = SpokeAngle(rot[0], spoke), total = 0f;
            var turned = new float[Samples + 1];
            _ring0 = prev;
            for (int k = 1; k <= Samples; k++)
            {
                float a = SpokeAngle(rot[k], spoke);
                total += VrSiege.WrapPi(a - prev);
                prev = a;
                turned[k] = total;
            }

            if (Math.Abs(total) < 0.5f)
            {
                _noRing = true;
                Log("TEST 519: the ring turns only " + F(total * 57.2957795f) + " deg over the reload - it reloads the vanilla way.");
                return;
            }

            // Rising, with a floor on each step so a still stretch of the animation
            // still takes some turn (it never jumps).
            _ringSign = Math.Sign(total);
            float floor = Math.Abs(total) / Samples * 0.2f;
            _ring[0] = 0f;
            for (int k = 1; k <= Samples; k++)
                _ring[k] = Math.Max(_ring[k - 1] + floor, _ringSign * turned[k]);
            _ringReady = true;
            Log("TEST 519 ring: '" + _skeleton.GetBoneName(_wheel) + "' turns " + F(Math.Abs(total) / (float)(2.0 * Math.PI))
                + " turn(s) over the reload about its " + "sfu"[axle] + " axis (held within "
                + F((float)Math.Acos(Math.Min(1f, held)) * 57.2957795f) + " deg), half of it by parameter "
                + F(ParamFor(_ring, _ring[Samples] * 0.5f)) + "; the whole wind takes at least " + F(MinS) + " s.");
        }

        private static float SpokeAngle(Mat3 r, int spoke)
        {
            Vec3 v = Axis(r, spoke);
            return (float)Math.Atan2(Vec3.DotProduct(v, _e2), Vec3.DotProduct(v, _e1));
        }

        private static Vec3 Axis(Mat3 r, int c) => c == 0 ? r.s : c == 1 ? r.f : r.u;

        private static Vec3 Unit(Vec3 v)
        {
            float l = v.Length;
            return l > 1e-6f ? v * (1f / l) : v;
        }

        private static bool NearWheel(Vec3 at, out float d)
        {
            d = float.MaxValue;
            if (_wheel < 0)
                return false;
            d = at.Distance(_owner.GetGlobalFrame().TransformToParent(_skeleton.GetBoneEntitialFrameWithIndex(_wheel).origin));
            return d <= WheelM;
        }

        /// <summary>TEST 518: a bolt to take - vanilla's own rule (CheckAmmo):
        /// StartingAmmoCount 0 never runs out, and AmmoCount is the spares besides the
        /// loaded one. 517 refused at AmmoCount 0, so no grip ever took one.</summary>
        private static bool HasBolt() => _b.AmmoCount > 0 || _b.StartingAmmoCount <= 0;

        private static bool NearRack(Vec3 at, out float d)
        {
            if (_rack.IsValid)
            {
                d = VrGrab.BoxDistance(at, _rack.GlobalBoxMin, _rack.GlobalBoxMax);
                return d <= RackM;
            }

            // No rack known: anywhere on the ballista away from the groove.
            d = VrGrab.BoxDistance(at, _body.GlobalBoxMin, _body.GlobalBoxMax);
            return d <= RackM && (!TrySeat(out Vec3 seat) || at.Distance(seat) > SeatM);
        }

        private static void TakeBolt(VrHand hand, float d)
        {
            if (!EnsureBolt())
            {
                Log("TEST 517 bolt: no bolt model - taken without one.");
            }

            _boltHand = (int)hand;
            if (_b.StartingAmmoCount > 0)    // Ballista.UpdateAmmoMesh's rule, one fewer on the rack
                _b.GameEntity.SetVectorArgument(0f, 8 - (_b.AmmoCount - 1), 0f, 0f);
            Pulse(hand, 0.6f, 0.06f);
            Log("TEST 517 bolt: " + hand + " took a bolt from the rack (" + F(d) + " m, " + (_rack.IsValid ? "the pile" : "on the ballista")
                + "). Bring it to the groove.");
        }

        private static void CarryBolt(VrHand hand, BvrHandState s)
        {
            Vec3 at = VrWeaponHands.HandPoint(hand, s);
            Vec3 f = VrCameraDriver.StageToHand(s.GripOrientation, s.GripPosition).rotation.f;
            f.Normalize();

            // TEST 522: laid along the groove - anywhere along it - it seats.
            float along = 0f, off = 0f, cos = 0f;
            bool groove = TryGroove(out Vec3 tail, out Vec3 dir, out float len);
            bool seats = groove && InGroove(at, f, tail, dir, len, SeatM, SeatCos, out along, out off, out cos);
            if (groove && Probe && Stopwatch.GetTimestamp() >= _logAt)
            {
                _logAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
                Log("TEST 522 bolt: the hand " + F(along) + " m along the groove (0 = its tail, " + F(len) + " its tip), "
                    + F(off) + " m off it, the bolt " + F((float)Math.Acos(Math.Min(1f, cos)) * 57.2957795f)
                    + " deg from it (seats within " + F(SeatM) + " m and " + F((float)Math.Acos(SeatCos) * 57.2957795f) + " deg).");
            }
            if (seats)
            {
                _boltHand = -1;
                _seated = true;
                HideBolt();
                if (ProjectileProp?.GetValue(_b) is SynchedMissionObject projectile)
                    projectile.SetVisibleSynched(true);
                Pulse(hand, 0.8f, 0.08f);
                Log("TEST 517 bolt: seated in the groove (" + F(off) + " m off it, " + F(along) + " m along)" + (_progress >= Wound
                    ? "." : " - wind the wheels."));
                return;
            }

            if (_bolt == null || _boltMeta == null)
                return;

            MatrixFrame frame = VrBow.ArrowFrame(at - f * (_boltLen * 0.5f), f, _boltLocal);
            _bolt.SetGlobalFrame(in frame, true);
            _bolt.SetVisibilityExcludeParents(true);
        }

        private static void ReturnBolt(string why)
        {
            _boltHand = -1;
            HideBolt();
            try
            {
                if (_b != null && _b.StartingAmmoCount > 0)
                    _b.GameEntity.SetVectorArgument(0f, 8 - _b.AmmoCount, 0f, 0f);
            }
            catch (Exception)
            {
                // The ballista is gone with its mission.
            }
            Log("TEST 517 bolt: back on the rack (" + why + ").");
        }

        /// <summary>The groove: where the middle of a loaded bolt lies - the
        /// projectile's frame (its tail; vanilla keeps it at rest while a pilot is
        /// on) plus half a bolt along its shooting axis (u, vanilla's
        /// ShootingDirection). The carried bolt is centred on the hand. TEST 521: 517
        /// measured the hand to the tail with 0.45 m, so a bolt seated as soon as the
        /// hand swept past (every seat at 0.41-0.45 m, 0.2-0.6 s off the pile).</summary>
        private static bool TrySeat(out Vec3 seat)
        {
            bool ok = TryGroove(out Vec3 tail, out Vec3 dir, out float len);
            seat = tail + dir * (len * 0.5f);
            return ok;
        }

        /// <summary>TEST 522: the groove as the loaded bolt's line - from the
        /// projectile's frame (its tail) along its shooting axis for a bolt's length.
        /// 521's single point (half a bolt along) was never reached: the 12:15 run's
        /// hands came no closer than 0.42 m.</summary>
        private static bool TryGroove(out Vec3 tail, out Vec3 dir, out float len)
        {
            tail = dir = Vec3.Zero;
            len = _boltLen > 0.3f ? _boltLen : 1.5f;
            if (!(ProjectileProp?.GetValue(_b) is SynchedMissionObject projectile))
                return false;
            MatrixFrame g = projectile.GameEntity.GetGlobalFrame();
            tail = g.origin;
            dir = g.rotation.u;
            dir.Normalize();
            return true;
        }

        /// <summary>TEST 522: a bolt centred at <paramref name="at"/> along
        /// <paramref name="f"/> lies in the groove (tail + dir x 0..len): within
        /// <paramref name="reach"/> of that segment, and pointing along it either
        /// way (|cos| at least <paramref name="minCos"/>). Pure.</summary>
        internal static bool InGroove(Vec3 at, Vec3 f, Vec3 tail, Vec3 dir, float len, float reach, float minCos,
                                      out float along, out float off, out float cos)
        {
            along = Vec3.DotProduct(at - tail, dir);
            off = (at - (tail + dir * Math.Max(0f, Math.Min(len, along)))).Length;
            cos = Math.Abs(Vec3.DotProduct(f, dir));
            return off <= reach && cos + 1e-6f >= minCos;
        }

        private static bool ProjectileShown() =>
            ProjectileProp?.GetValue(_b) is SynchedMissionObject p && p.GameEntity.IsVisibleIncludeParents();

        private static float SetUpParameter()
        {
            Skeleton skeleton = SkeletonsField?.GetValue(_b) is Skeleton[] all && all.Length > 0 ? all[0] : null;
            int setUp = SetUpField?.GetValue(_b) is int[] idx && idx.Length > 0 ? idx[0] : -1;
            return skeleton != null && skeleton.GetAnimationIndexAtChannel(0) == setUp
                ? Math.Min(1f, skeleton.GetAnimationParameterAtChannel(0)) : 0f;
        }

        /// <summary>One bolt model per mission, hidden while not in a hand (the
        /// finger work's rule: never leave engine objects to finalizers).</summary>
        private static bool EnsureBolt()
        {
            Mission mission = Mission.Current;
            if (mission?.Scene == null)
                return false;

            if (_bolt == null || !ReferenceEquals(_boltMission, mission))
            {
                _bolt = GameEntity.CreateEmptyDynamic(mission.Scene, false);
                _bolt.SetVisibilityExcludeParents(false);
                _boltMission = mission;
                _boltMeta = null;

                ItemObject item = MissileField?.GetValue(_b) as ItemObject;
                string name = item == null ? null
                    : !string.IsNullOrEmpty(item.MultiMeshName) ? item.MultiMeshName : item.FlyingMeshName;
                _boltMeta = string.IsNullOrEmpty(name) ? null : MetaMesh.GetCopy(name, false, true);
                if (_boltMeta != null)
                {
                    _bolt.AddMultiMesh(_boltMeta, true);
                    _boltLocal = VrBow.FitArrow(name, _boltMeta, out _boltLen);
                }
            }
            return _boltMeta != null;
        }

        private static void HideBolt()
        {
            try
            {
                if (_bolt != null && ReferenceEquals(_boltMission, Mission.Current))
                    _bolt.SetVisibilityExcludeParents(false);
            }
            catch (Exception)
            {
                _bolt = null;
            }
        }

        /// <summary>The rack: a child entity named for ammo - Native's is
        /// 'projectile_pile' (TEST 518: 517 skipped every "projectile" name).</summary>
        private static void FindRack()
        {
            _rack = default;
            MatrixFrame body = _body.GetGlobalFrame();
            foreach (WeakGameEntity e in Tree(_b.GameEntity, 0))
            {
                string n = (e.Name ?? "").ToLowerInvariant();
                if (n.Contains("pile") || ((n.Contains("ammo") || n.Contains("bolt") || n.Contains("rack")) && !n.Contains("projectile")))
                {
                    _rack = e;
                    Log("TEST 518 rack: '" + e.Name + "' at body-local (" + V(body.TransformToLocalNonOrthogonal((e.GlobalBoxMin + e.GlobalBoxMax) * 0.5f))
                        + "), box " + V(e.GlobalBoxMax - e.GlobalBoxMin) + " m.");
                    return;
                }
            }
        }

        private static IEnumerable<WeakGameEntity> Tree(WeakGameEntity root, int depth)
        {
            if (!root.IsValid || depth > 6)
                yield break;
            yield return root;
            foreach (WeakGameEntity child in root.GetChildren())
            {
                foreach (WeakGameEntity e in Tree(child, depth + 1))
                    yield return e;
            }
        }

        /// <summary>Once per ballista type: its entity tree and its skeleton, in the
        /// body's frame - where the rack and the wheels are.</summary>
        private static void DumpOnce()
        {
            string key = _b.GameEntity.Name ?? "?";
            if (!Probe || !_dumped.Add(key))
                return;

            MatrixFrame body = _body.GetGlobalFrame();
            var text = new System.Text.StringBuilder("TEST 517 dump '" + key + "' (body-local):");
            foreach (WeakGameEntity e in Tree(_b.GameEntity, 0))
                text.Append(" [" + e.Name + " " + V(body.TransformToLocalNonOrthogonal((e.GlobalBoxMin + e.GlobalBoxMax) * 0.5f)) + "]");

            Skeleton skeleton = SkeletonsField?.GetValue(_b) is Skeleton[] all && all.Length > 0 ? all[0] : null;
            if (skeleton != null)
            {
                text.Append(" skeleton:");
                for (sbyte k = 0; k < skeleton.GetBoneCount() && k >= 0; k++)
                    text.Append(" " + skeleton.GetBoneName(k) + "(" + V(skeleton.GetBoneEntitialFrameWithIndex(k).origin) + ")");
            }
            if (TrySeat(out Vec3 seat))
                text.Append(" groove (" + V(body.TransformToLocalNonOrthogonal(seat)) + ")");
            VrLog.Info(text.ToString());
        }

        private static void LogGrip(VrHand hand, Vec3 at)
        {
            if (Probe)
                Log("TEST 517 grip: " + hand + " at body-local (" + V(_body.GetGlobalFrame().TransformToLocalNonOrthogonal(at))
                    + "), state " + _b.State + (_rack.IsValid ? ", " + F(VrGrab.BoxDistance(at, _rack.GlobalBoxMin, _rack.GlobalBoxMax))
                    + " m from the pile" : "") + ".");
        }

        private static void Fail(Exception ex)
        {
            _broken = true;
            _crank[0] = _crank[1] = false;
            _boltHand = -1;
            VrLog.Error("TEST 517: the ballista by hand failed; it reloads the vanilla way for this session.", ex);
        }

        private static void Pulse(VrHand hand, float amplitude, float seconds)
        {
            try
            {
                NativeMethods.bvr_trigger_haptic((int)hand, amplitude, seconds);
            }
            catch (Exception)
            {
                // A pulse is never worth a throw.
            }
        }

        private static void Log(string text)
        {
            if (Probe)
                VrLog.Info(text);
        }

        private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);

        private static string V(Vec3 v) => F(v.x) + ", " + F(v.y) + ", " + F(v.z);
    }
}
