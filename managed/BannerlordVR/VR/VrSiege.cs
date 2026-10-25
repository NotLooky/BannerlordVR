using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using BannerlordVR.Interop;
using BannerlordVR.Patches;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using MovementControlFlag = TaleWorlds.MountAndBlade.Agent.MovementControlFlag;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 512: siege machines by hand.
    ///
    /// The handle. Land machines carry no managed handle entities, but the engine's
    /// usage animation and machine IK put the hand bones on the machine. So the
    /// ANIMATED hand, read before anything of ours is written (Learn, from
    /// VrWeaponHands), is each hand's handle on every machine - and it rides the
    /// machine: a ram's swing, a ballista's turn.
    ///
    /// The hold. At any machine (ram, tower, ladder raise or push, mangonel crew), a
    /// grip within siege_reach_m of that hand's handle holds it, and the hand is
    /// drawn there while held (AdjustHand). For those machines holding is the whole
    /// feature; the engine still does the work.
    ///
    /// The aim, piloting a RangedSiegeWeapon (ballista, mangonel, trebuchet, War
    /// Sails' ship ballista and mangonel), through the engine's own GiveExactInput.
    /// TEST 512d: the gripped part follows the hand AROUND THE MACHINE'S PIVOT - the
    /// turn is the held hand(s)' bearing from the pivot, the lift their angle in the
    /// barrel's vertical plane, both from the grab. Handles behind the pivot make
    /// it a lever by geometry (hands right turn it left, hands down raise it). 512c
    /// measured from the head, so a head move turned the machine ("head moves the
    /// view"). The hand is corrected for any travel of the pilot since the grab, so
    /// a machine that carries its pilot cannot feed back. SiegeAimPatch keeps the
    /// stick from cancelling it and raises the turn rate to siege_aim_rad_s ("follow
    /// the hands, up to a cap"; vanilla's crew-size rule is ignored while held).
    ///
    /// TEST 512d: the pilot stays put. A ballista carries its pilot's standing point
    /// round as it turns; in VR that point is held where it was when the player took
    /// the machine (siege_pin_pilot) - "if I want to move I will move physically".
    /// The pin and hand aim are relative to its base, so they travel with a ship.
    ///
    /// The shot: the main controller's physical trigger (.Grip under
    /// controls_swap_trigger_grip, as VrCrossbow reads it) is the attack while
    /// piloting; the grip never is. The ballista's click-to-reload is the same press.
    ///
    /// Not inside VrGrab, which stands down at a machine (TEST 341). Not the War
    /// Sails helm (VrHelm), nor aiming the ship's ballista from the helm.
    /// </summary>
    internal static class VrSiege
    {
        private static readonly bool On = VrConfig.Bool("siege_hand", true);
        private static readonly bool Probe = VrConfig.Bool("siege_probe", true);
        private static readonly float Reach = VrConfig.Float("siege_reach_m", 0.25f);
        internal static readonly float AimRadS = VrConfig.Float("siege_aim_rad_s", 1.5f);

        /// <summary>TEST 512b: in VR the game's flat weapon camera is never used
        /// (SiegeAimPatch) - the machine is aimed from first person.</summary>
        internal static readonly bool VrView = VrConfig.Bool("siege_vr_view", true);
        // TEST 512d: around the pivot the lever is geometry, so +1 is "the gripped part
        // follows the hand" (+direction and +release are the engine's counter-clockwise
        // and up, as its AI aims). New keys: 512c's siege_yaw/pitch_sign meant the head model.
        private static readonly float TurnSign = VrConfig.Float("siege_turn_sign", 1f) < 0f ? -1f : 1f;
        private static readonly float LiftSign = VrConfig.Float("siege_lift_sign", 1f) < 0f ? -1f : 1f;
        private static readonly float PitchGain = VrConfig.Float("siege_pitch_gain", 1f);
        private static readonly bool PinOn = VrConfig.Bool("siege_pin_pilot", true);
        private static readonly bool StickTurn = VrConfig.Bool("siege_stick_turn", true);
        private static readonly bool GrabUse = VrConfig.Bool("siege_grab_use", true);
        private static readonly float SlideM = Math.Max(0f, VrConfig.Float("siege_slide_m", 1.0f));
        private static readonly float SlideMps = VrConfig.Float("siege_slide_mps", 1.0f);
        private static readonly float OrbitRadS = Math.Max(0f, VrConfig.Float("siege_orbit_rad_s", 0.6f));
        private static readonly float OrbitSign = VrConfig.Float("siege_orbit_sign", 1f) < 0f ? -1f : 1f;
        private static readonly long[] _pending = new long[2];
        private static Vec3 _pinSlide;
        private static float _pinSlideAt;

        /// <summary>TEST 512e: at any machine in VR the right stick turns you past the
        /// usage animation's neck limit (SiegeAimPatch) - you are pinned there, so the
        /// stick is the only turn you have besides your own body.</summary>
        internal static bool FreeTurn(Agent agent) =>
            StickTurn && agent != null && agent.IsMainAgent && agent.IsUsingGameObject && VrSystem.IsActive;

        private const float Down = 0.7f;
        private const float Up = 0.4f;
        private const float MinReach = 0.15f;   // closer in, the hand's bearing is noise: hold the last target

        // Protected; looked up softly, so a renamed field only turns the aim off
        // (a throwing type initializer would take VrHolster's trigger gate with it).
        private static readonly FieldInfo DirectionField = AccessTools.Field(typeof(RangedSiegeWeapon), "CurrentDirection");
        private static readonly FieldInfo ReleaseField = AccessTools.Field(typeof(RangedSiegeWeapon), "CurrentReleaseAngle");
        private static readonly PropertyInfo Sensitivity =
            AccessTools.Property(typeof(RangedSiegeWeapon), "HorizontalAimSensitivity");
        private static readonly bool CanAim = DirectionField != null && ReleaseField != null;

        // TEST 512c: the target as the engine clamped it (private).
        private static readonly FieldInfo InputXField = AccessTools.Field(typeof(RangedSiegeWeapon), "_inputTargetX");
        private static readonly FieldInfo InputYField = AccessTools.Field(typeof(RangedSiegeWeapon), "_inputTargetY");

        // TEST 512d: the turning parts (protected) - the ballista's navel (yaw) and
        // body (rides yaw and pitch), else the base RotationObject - and the barrel.
        private static readonly PropertyInfo BallistaNavel = AccessTools.Property(typeof(Ballista), "ballistaNavel");
        private static readonly PropertyInfo BallistaBody = AccessTools.Property(typeof(Ballista), "ballistaBody");
        private static readonly FieldInfo RotationObject = AccessTools.Field(typeof(RangedSiegeWeapon), "RotationObject");
        private static readonly PropertyInfo ShootingDirection =
            AccessTools.Property(typeof(RangedSiegeWeapon), "ShootingDirection");

        private static readonly bool[] _down = new bool[2];
        private static readonly bool[] _hold = new bool[2];
        private static readonly Vec3[] _handle = new Vec3[2];
        private static readonly bool[] _haveHandle = new bool[2];
        private static readonly Vec3[] _grabOffset = new Vec3[2];
        private static readonly float[] _grabYaw = new float[2];
        private static readonly bool[] _grabYawOk = new bool[2];

        private static UsableMissionObject _point;
        private static UsableMachine _pointMachine;
        private static UsableMachine _machine;
        private static RangedSiegeWeapon _weapon;
        private static bool _fire;

        private static bool _aimSet;
        private static bool _haveTarget;
        private static float _dir0, _rel0, _yaw0, _lift0;
        private static Vec2 _target;
        private static Vec3 _barrel0, _agentAt0;

        private static WeakGameEntity _pivot, _ride;
        private static readonly Vec3[] _rideLocal = new Vec3[2];
        private static readonly bool[] _rideOk = new bool[2];

        private static bool _pinned;
        private static object _pinWeapon;
        private static Mission _pinMission;
        private static WeakGameEntity _pinPoint;
        private static MatrixFrame _pinBase, _pinLocal;
        private static float _pinYaw0, _pinTurn;
        private static long _logAt;
        private static bool _broken;

        /// <summary>The player pilots a ranged machine and a hand holds it: the
        /// hands aim it (SiegeAimPatch).</summary>
        internal static bool Aiming => CanAim && _weapon != null && (_hold[0] || _hold[1]);

        /// <summary>The ranged machine the player pilots, or null.</summary>
        internal static object Weapon => _weapon;

        /// <summary>Whether this controller holds the machine - its grip is then
        /// never the game's Attack (VrHolster.TriggerKeyAllowed).</summary>
        internal static bool Holds(bool left) => _hold[left ? 0 : 1] || VrBallista.Holds(left ? 0 : 1);

        /// <summary>
        /// TEST 515: a grip on a ranged machine nobody pilots takes its pilot point -
        /// vanilla's Use, with vanilla's checks - and holds it at once: no Use key.
        /// From VrGrab, for an empty hand, before the player uses anything.
        /// </summary>
        internal static bool TryUse(Agent agent, VrHand hand, Vec3 at)
        {
            Mission mission = Mission.Current;
            if (!On || _broken || !GrabUse || agent == null || agent.IsUsingGameObject || mission == null
                || !mission.IsMainAgentObjectInteractionEnabled || !agent.IsAbleToUseMachine())
                return false;

            try
            {
                foreach (MissionObject o in mission.ActiveMissionObjects)
                {
                    if (!(o is RangedSiegeWeapon w))
                        continue;

                    StandingPoint point = w.PilotStandingPoint;
                    if (point == null || point.HasUser || point.IsDeactivated || point.IsDisabledForAgent(agent)
                        || !agent.CanUseObject(point) || !agent.ObjectHasVacantPosition(point))
                        continue;

                    Parts(w, out _, out WeakGameEntity body);
                    if (!body.IsValid)
                        body = w.GameEntity;
                    float d = VrGrab.BoxDistance(at, body.GlobalBoxMin, body.GlobalBoxMax);
                    if (d > Reach)
                        continue;

                    agent.HandleStartUsingAction(point, -1);
                    _pending[(int)hand] = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
                    Pulse(hand, 0.5f, 0.05f);
                    VrLog.Info("TEST 515: " + hand + " took '" + Name(w) + "' by hand (" + F(d)
                               + " m from it) - no Use key.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
            return false;
        }

        /// <summary>A ranged machine's turning parts: the ballista's navel (yaw) and
        /// body (rides yaw and pitch), else the base RotationObject for both.</summary>
        private static void Parts(RangedSiegeWeapon w, out WeakGameEntity pivot, out WeakGameEntity ride)
        {
            pivot = ride = default;
            object navel = w is Ballista ? BallistaNavel?.GetValue(w) : null;
            object body = w is Ballista ? BallistaBody?.GetValue(w) : null;
            object turning = RotationObject?.GetValue(w);
            if ((navel ?? turning) is SynchedMissionObject p)
                pivot = p.GameEntity;
            if ((body ?? turning) is SynchedMissionObject r)
                ride = r.GameEntity;
        }

        /// <summary>The main controller's physical trigger is the shot while
        /// piloting a ranged machine - never the bumper (VrGamepad).</summary>
        internal static bool TriggerOwned(bool left) =>
            _weapon != null && (left ? VrHand.Left : VrHand.Right) == VrHandSlots.MainController;

        /// <summary>The machine target for a hand turned <paramref name="dYaw"/>
        /// (counter-clockwise from above, as RotateAboutUp turns) and lifted
        /// <paramref name="dElev"/> since the grab - both wrapped, as a hand behind
        /// the pivot sits near +-pi in the barrel's plane. Pure.</summary>
        internal static Vec2 Target(float dir0, float rel0, float dYaw, float dElev,
                                    float yawSign, float pitchSign, float pitchGain)
        {
            return new Vec2(dir0 + yawSign * WrapPi(dYaw), rel0 + pitchSign * pitchGain * WrapPi(dElev));
        }

        /// <summary>A press with hysteresis: down at 0.7, up again below 0.4. Pure.</summary>
        internal static bool Pressed(float value, bool wasDown) => value >= (wasDown ? Up : Down);

        /// <summary>An angle into (-pi, pi]. Pure.</summary>
        internal static float WrapPi(float a)
        {
            while (a > Math.PI) a -= (float)(2.0 * Math.PI);
            while (a <= -Math.PI) a += (float)(2.0 * Math.PI);
            return a;
        }

        /// <summary>From VrWeaponHands before anything is written: while a machine
        /// is used, each hand bone where the engine's animation put it is that
        /// hand's handle.</summary>
        internal static void Learn(Agent agent, GameEntity entity, Skeleton skeleton, VrHandSlot main, VrHandSlot off)
        {
            _haveHandle[0] = _haveHandle[1] = false;
            if (!On || _broken || !agent.IsUsingGameObject)
                return;

            try
            {
                MatrixFrame agentGlobal = entity.GetGlobalFrame();
                Read(agentGlobal, skeleton, main);
                Read(agentGlobal, skeleton, off);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        private static void Read(MatrixFrame agentGlobal, Skeleton skeleton, VrHandSlot slot)
        {
            if (!slot.Valid)
                return;

            int i = (int)slot.Controller;
            _handle[i] = agentGlobal.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(slot.Bone)).origin;
            _haveHandle[i] = true;
        }

        /// <summary>From VrWeaponHands, on each hand's target before the arm solve:
        /// the hand on the machine stays where it took hold, riding the machine.
        /// TEST 512c: not ON the animated hand - that sat 0.1-0.6 m below the grips
        /// ("hand grab too low"); the grab's offset from it is kept, turned with the
        /// machine's heading (the line between the two animated hands).</summary>
        internal static void AdjustHand(VrHand controller, ref MatrixFrame world)
        {
            int i = (int)controller;

            // TEST 519: a hand on the ballista's ring rides the ring.
            if (VrBallista.TryCrankPoint(i, out Vec3 ring))
            {
                world.origin = ring;
                return;
            }

            if (!_hold[i])
                return;

            // TEST 512d: on a ranged machine the grab point rides its turning part
            // (the pilot no longer turns with it, so the animated hand does not).
            if (_rideOk[i] && _ride.IsValid)
            {
                world.origin = _ride.GetGlobalFrame().TransformToParent(_rideLocal[i]);
                return;
            }

            if (!_haveHandle[i])
                return;

            Vec3 off = _grabOffset[i];
            if (_grabYawOk[i] && PairYaw(out float yaw))
                off = RotateZ(off, WrapPi(yaw - _grabYaw[i]));
            world.origin = _handle[i] + off;
        }

        /// <summary>The machine's heading as the line from the left animated hand
        /// to the right, when both are known and apart.</summary>
        private static bool PairYaw(out float yaw)
        {
            yaw = 0f;
            if (!_haveHandle[0] || !_haveHandle[1])
                return false;

            Vec3 d = _handle[1] - _handle[0];
            if (d.x * d.x + d.y * d.y < 0.01f)
                return false;

            yaw = (float)Math.Atan2(d.y, d.x);
            return true;
        }

        /// <summary><paramref name="v"/> turned counter-clockwise about up. Pure.</summary>
        internal static Vec3 RotateZ(Vec3 v, float a)
        {
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            return new Vec3(v.x * c - v.y * s, v.x * s + v.y * c, v.z, -1f);
        }

        /// <summary>From MissionTickPatch, after VrWeaponHands has learnt this
        /// frame's handles.</summary>
        internal static void Tick()
        {
            if (!On || _broken)
                return;

            try
            {
                if (NativeMethods.bvr_get_input_state(out BvrInputState input, battleGrip: true) != (int)BvrStatus.Ok)
                    return;

                Agent agent = Mission.Current?.MainAgent;
                UsableMachine machine = Machine(agent);

                // The helm is VrHelm's.
                if (machine != null && ReferenceEquals(machine, HelmPatch.Machine))
                    machine = null;

                if (!ReferenceEquals(machine, _machine))
                {
                    LetAll(machine == null ? "left the machine" : null);
                    _machine = machine;
                    _haveTarget = false;
                }

                RangedSiegeWeapon weapon =
                    machine is RangedSiegeWeapon w && agent != null && w.PilotAgent == agent ? w : null;
                if (!ReferenceEquals(weapon, _weapon))
                    Pilot(weapon);
                if (_pinned)
                    MovePilot(input);

                if (machine == null || VrWeaponHands.Paused())
                {
                    _fire = false;
                    Follow(VrHand.Left, input.Left);
                    Follow(VrHand.Right, input.Right);
                    return;
                }

                Hand(VrHand.Left, input.Left, machine);
                Hand(VrHand.Right, input.Right, machine);

                BvrHandState ms = VrHandSlots.MainController == VrHand.Left ? input.Left : input.Right;
                bool fire = _weapon != null && ms.IsActive != 0 && Pressed(ms.Grip, _fire);
                if (fire && !_fire && Probe)
                    VrLog.Info("TEST 512 fire: the " + VrHandSlots.MainController + " trigger on '" + Name(_weapon)
                               + "' (state " + _weapon.State + ").");
                _fire = fire;

                if (_weapon != null && CanAim)
                    Aim(input, agent);

                // TEST 517: the ballista's reload by hand.
                VrBallista.Tick(input);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>The machine whose standing point the agent uses - the first
        /// UsableMachine up its entity's parents, as the game's own focus text
        /// finds it.</summary>
        private static UsableMachine Machine(Agent agent)
        {
            UsableMissionObject point = agent?.CurrentlyUsedGameObject;
            if (ReferenceEquals(point, _point))
                return _pointMachine;

            _point = point;
            _pointMachine = null;
            if (point == null)
                return null;

            WeakGameEntity e = point.GameEntity;
            while (e.IsValid && !e.HasScriptOfType<UsableMachine>())
                e = e.Parent;
            _pointMachine = e.IsValid ? e.GetFirstScriptOfType<UsableMachine>() : null;
            return _pointMachine;
        }

        private static void Hand(VrHand hand, BvrHandState state, UsableMachine machine)
        {
            int i = (int)hand;
            if (state.IsActive == 0)
            {
                Follow(hand, state);
                return;
            }

            bool pressed = Pressed(state.Trigger, _down[i]);
            bool edge = pressed && !_down[i];
            bool released = !pressed && _down[i];
            _down[i] = pressed;

            // TEST 517: a crank or a carried bolt lets go first.
            if (released && VrBallista.Release(hand))
                return;

            if (released && _hold[i])
            {
                Let(hand, true, null);
                return;
            }

            // TEST 515: the grip that took the machine (TryUse) holds it as soon as the
            // player is on it - still pressed, no second press.
            bool taken = pressed && !_hold[i] && _pending[i] > Stopwatch.GetTimestamp();
            if ((!edge && !taken) || _hold[i] || VrBallista.Holds(i) || !_haveHandle[i])
                return;
            _pending[i] = 0L;

            Vec3 at = VrWeaponHands.HandPoint(hand, state);

            // TEST 517: a wheel or the rack before the machine itself.
            if (!taken && VrBallista.Claim(hand, state, at))
                return;

            float d = at.Distance(_handle[i]);

            // TEST 512b: piloting a ranged machine, a grip on its body takes it too -
            // the first run's grips missed the animated hand by 1-2 cm.
            WeakGameEntity body = machine.GameEntity;
            float onBody = _weapon != null ? VrGrab.BoxDistance(at, body.GlobalBoxMin, body.GlobalBoxMax) : float.MaxValue;

            // Hand from handle in the view's axes (right, forward, up): whether the
            // animated hand sits on the machine at all.
            Vec3 off = VrCameraDriver.Anchor.rotation.TransformToLocal(at - _handle[i]);
            string where = F(d) + " m from its handle (right " + F(off.x) + ", forward " + F(off.y) + ", up "
                           + F(off.z) + ")" + (_weapon != null ? ", " + F(onBody) + " m from the machine's box" : "");

            if (!taken && d > Reach && onBody > Reach)
            {
                if (Probe && Stopwatch.GetTimestamp() >= _logAt)
                {
                    _logAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                    VrLog.Info("TEST 512 hold: " + hand + " grip " + where + " on '" + Name(machine) + "' (reach "
                               + F(Reach) + ").");
                }
                return;
            }

            _hold[i] = true;
            _aimSet = false;
            _grabOffset[i] = at - _handle[i];
            _grabYawOk[i] = PairYaw(out _grabYaw[i]);
            _rideOk[i] = _weapon != null && _ride.IsValid;
            if (_rideOk[i])
                _rideLocal[i] = _ride.GetGlobalFrame().TransformToLocalNonOrthogonal(at);
            Pulse(hand, 0.5f, 0.05f);
            VrLog.Info("TEST 512 hold: " + hand + " took '" + Name(machine) + "' (" + machine.GetType().Name + "), "
                       + where + (_weapon != null ? " - it aims with the hands." : "."));
        }

        /// <summary>
        /// TEST 512d. The held hand(s) - their midpoint for two - around the machine's
        /// pivot: the bearing from the pivot turns it, the angle in the barrel's
        /// vertical plane lifts it, both from the grab. The hand is taken back by any
        /// travel of the pilot since the grab. Below MinReach from the pivot the last
        /// target holds.
        /// </summary>
        private static void Aim(BvrInputState input, Agent agent)
        {
            if (!_hold[0] && !_hold[1])
            {
                _aimSet = false;
                return;
            }

            Vec3 sum = Vec3.Zero;
            int n = 0;
            for (int i = 0; i < 2; i++)
            {
                BvrHandState s = i == (int)VrHand.Left ? input.Left : input.Right;
                if (!_hold[i] || s.IsActive == 0)
                    continue;
                sum += VrWeaponHands.HandPoint((VrHand)i, s);
                n++;
            }
            if (n == 0 || !_pivot.IsValid || agent == null)
                return;

            MatrixFrame machine = _weapon.GameEntity.GetGlobalFrame();
            Vec3 agentAt = machine.TransformToLocalNonOrthogonal(agent.Position);
            if (!_aimSet)
                _agentAt0 = agentAt;

            // In base coordinates ship travel is gone; pinned travel is the player's
            // own (the stick's orbit, a step), so the machine follows the hands.
            Vec3 d = AimOffset(machine, sum * (1f / n), _pivot.GetGlobalFrame().origin)
                     - (_pinned ? Vec3.Zero : agentAt - _agentAt0);
            float flat = (float)Math.Sqrt(d.x * d.x + d.y * d.y);
            if (flat < MinReach)
                return;

            // From the last target when there is one, so a second hand taking hold
            // (the midpoint jumps) or one letting go moves nothing.
            if (!_aimSet)
            {
                _aimSet = true;
                _barrel0 = Barrel(d, flat, machine);
                _yaw0 = (float)Math.Atan2(d.y, d.x);
                _lift0 = Lift(d, flat, _barrel0);
                _dir0 = _haveTarget ? _target.x : Angle(DirectionField);
                _rel0 = _haveTarget ? _target.y : Angle(ReleaseField);
            }

            float turn = (float)Math.Atan2(d.y, d.x) - _yaw0;
            float lift = Lift(d, flat, _barrel0) - _lift0;
            Vec2 want = Target(_dir0, _rel0, turn, lift, TurnSign, LiftSign, PitchGain);
            _weapon.GiveExactInput(want.x, want.y);

            // TEST 512c: past a turn or elevation limit the engine clamps the target.
            // The mapping is re-based on the clamped one, so the hands turn the machine
            // back at once (the ballista sat at 0.785 while the target ran to 1.57).
            if (InputXField != null && InputYField != null)
            {
                Vec2 got = new Vec2((float)InputXField.GetValue(_weapon), (float)InputYField.GetValue(_weapon));
                _dir0 += WrapPi(got.x - want.x);
                _rel0 += got.y - want.y;
                want = got;
            }

            _target = want;
            _haveTarget = true;

            if (Probe && Stopwatch.GetTimestamp() >= _logAt)
            {
                _logAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 512d aim '{0}': hand {1:F2} m from the pivot, turned {2:F1} lifted {3:F1} deg from the grab "
                    + "-> target ({4:F3}, {5:F3}), current ({6:F3}, {7:F3}), sens {8:F2} rad/s; pilot moved {9:F3} m "
                    + "(siege_turn_sign {10:F0}, siege_lift_sign {11:F0}: +1 = the gripped part follows the hand).",
                    Name(_weapon), flat, WrapPi(turn) * 57.2957795f, WrapPi(lift) * 57.2957795f, _target.x, _target.y,
                    Angle(DirectionField), Angle(ReleaseField),
                    Sensitivity != null ? (float)Sensitivity.GetValue(_weapon) : float.NaN,
                    (agentAt - _agentAt0).Length, TurnSign, LiftSign));
            }
        }

        /// <summary>The barrel's level heading at the grab - the engine's shooting
        /// direction, or away from the hand when it has none.</summary>
        private static Vec3 Barrel(Vec3 d, float flat, MatrixFrame machine)
        {
            if (ShootingDirection?.GetValue(_weapon) is Vec3 shot)
            {
                shot = machine.rotation.TransformToLocal(shot);
                float len = (float)Math.Sqrt(shot.x * shot.x + shot.y * shot.y);
                if (len > 0.1f)
                    return new Vec3(shot.x / len, shot.y / len, 0f, -1f);
            }
            return new Vec3(-d.x / flat, -d.y / flat, 0f, -1f);
        }

        // Ship translation, yaw and roll must not count as a hand aiming the weapon.
        internal static Vec3 AimOffset(MatrixFrame machine, Vec3 hand, Vec3 pivot) =>
            machine.TransformToLocalNonOrthogonal(hand) - machine.TransformToLocalNonOrthogonal(pivot);

        internal static MatrixFrame PilotFrame(MatrixFrame machine, MatrixFrame pin, float turn, Vec3 slide)
        {
            pin.rotation.RotateAboutUp(turn);
            pin.origin += slide;
            return machine.TransformToParent(pin);
        }

        /// <summary>The hand's angle about the pivot in the barrel's vertical plane:
        /// 0 straight along the barrel, pi straight behind. Pure.</summary>
        internal static float Lift(Vec3 d, float flat, Vec3 barrel)
        {
            float along = d.x * barrel.x + d.y * barrel.y;
            return (float)Math.Atan2(d.z, along >= 0f ? flat : -flat);
        }

        /// <summary>The player took or left a ranged machine's pilot point: its
        /// turning parts, and the pilot held where it stands.</summary>
        private static void Pilot(RangedSiegeWeapon weapon)
        {
            Unpin();
            _weapon = weapon;
            _aimSet = false;
            _pivot = _ride = default;
            VrBallista.Pilot(weapon);    // TEST 517 (a null leaves)
            if (weapon == null)
                return;

            Parts(weapon, out _pivot, out _ride);

            WeakGameEntity point = weapon.PilotStandingPoint != null ? weapon.PilotStandingPoint.GameEntity : default;
            if (!PinOn || !VrSystem.IsActive || !point.IsValid)
                return;

            _pinPoint = point;
            MatrixFrame machine = weapon.GameEntity.GetGlobalFrame();
            // Pin to the machine's base so a moving ship cannot leave its pilot behind.
            _pinBase = machine.TransformToLocalNonOrthogonal(point.GetGlobalFrame());
            _pinYaw0 = ViewYaw(machine);
            _pinTurn = 0f;
            _pinSlide = Vec3.Zero;
            _pinSlideAt = Mission.Current?.CurrentTime ?? 0f;
            _pinLocal = point.GetFrame();
            _pinWeapon = weapon;
            _pinMission = Mission.Current;
            _pinned = true;
            if (Probe)
                VrLog.Info("TEST 512d: piloting '" + Name(weapon) + "' - the pilot stays where it stands while it turns.");
        }

        /// <summary>The pilot's standing point back on the machine (vanilla's next
        /// aim change places it from there).</summary>
        private static void Unpin()
        {
            if (!_pinned)
                return;

            _pinned = false;
            _pinWeapon = null;
            Mission mission = Mission.Current;
            if (!ReferenceEquals(mission, _pinMission) || mission == null || mission.IsFinalized || !_pinPoint.IsValid)
                return;

            _pinPoint.SetLocalFrame(ref _pinLocal, false);
        }

        /// <summary>
        /// TEST 512f: the right stick turns the whole character, not only the view
        /// ("right stick should move whole character"). The pinned point - which the
        /// agent stands on and faces with, as vanilla's ballista turns its pilot - is
        /// turned in place by the view's heading since the player took the machine.
        /// Only while that heading is the stick's own (VrEngineAim), never the agent's
        /// look, or turning the agent would turn the view that turns it.
        /// </summary>
        private static void MovePilot(BvrInputState input)
        {
            MatrixFrame machine = _weapon.GameEntity.GetGlobalFrame();
            float now = Mission.Current?.CurrentTime ?? 0f;
            float dt = Math.Max(0f, Math.Min(0.1f, now - _pinSlideAt));
            _pinSlideAt = now;

            bool changed = false;
            if (StickTurn && VrEngineAim.AnchorMustIgnoreLook)
            {
                float turn = WrapPi(ViewYaw(machine) - _pinYaw0);
                if (Math.Abs(WrapPi(turn - _pinTurn)) >= 1e-3f)
                {
                    _pinTurn = turn;
                    changed = true;
                }
            }

            // TEST 518: holding the machine, the left stick carries you round its pivot
            // and the machine with you ("doesn't move/rotate me with the machine"): the
            // pinned point orbits, the view turns the same, so the hands swing round the
            // pivot and Aim turns the machine by it. Stops at the machine's turn limit.
            float x = input.Left.IsActive != 0 ? input.Left.ThumbstickX : 0f;
            float push = Math.Abs(x) > 0.2f ? (x - Math.Sign(x) * 0.2f) / 0.8f : 0f;
            if (Aiming && _pivot.IsValid && VrEngineAim.AnchorMustIgnoreLook)
            {
                float a = OrbitSign * push * OrbitRadS * dt;    // counter-clockwise: stick right walks you right
                if (a != 0f && Math.Abs(Angle(DirectionField) + TurnSign * a) <= _weapon.DirectionRestriction * 0.5f)
                {
                    Vec3 pivot = machine.TransformToLocalNonOrthogonal(_pivot.GetGlobalFrame().origin);
                    _pinBase.origin = pivot + RotateZ(_pinBase.origin - pivot, a);
                    _pinSlide = RotateZ(_pinSlide, a);
                    VrEngineAim.TurnBody(a);    // 512f turns the point's facing by the view next tick
                    changed = true;
                }
            }

            // TEST 516: not holding, the left stick slides the pinned point left and
            // right along the view, siege_slide_mps, at most siege_slide_m from where it
            // was taken.
            // ponytail: no navmesh or wall test - siege_slide_m is the whole guard.
            else if (SlideM > 0f && push != 0f && dt > 0f)
            {
                Vec3 s = machine.rotation.TransformToLocal(VrCameraDriver.Anchor.rotation.s);
                float len = (float)Math.Sqrt(s.x * s.x + s.y * s.y);
                if (len > 1e-3f)
                {
                    _pinSlide += new Vec3(s.x / len, s.y / len, 0f, -1f) * (push * SlideMps * dt);
                    float l = _pinSlide.Length;
                    if (l > SlideM)
                        _pinSlide *= SlideM / l;
                    changed = true;
                }
            }

            if (!changed)
                return;

            HoldPilot(_weapon);
        }

        /// <summary>The view's heading relative to the machine's base,
        /// counter-clockwise from +x: the stick's turn and recentre, not the head.</summary>
        private static float ViewYaw(MatrixFrame machine)
        {
            Vec3 f = machine.rotation.TransformToLocal(VrCameraDriver.Anchor.rotation.f);
            return (float)Math.Atan2(f.y, f.x);
        }

        /// <summary>TEST 512d, from SiegeAimPatch after the engine turns a machine:
        /// the pinned pilot's standing point goes back where the player took it.</summary>
        internal static void HoldPilot(object weapon)
        {
            if (!_pinned || !ReferenceEquals(weapon, _pinWeapon))
                return;

            try
            {
                MatrixFrame frame = PilotFrame(_weapon.GameEntity.GetGlobalFrame(), _pinBase, _pinTurn, _pinSlide);
                _pinPoint.SetGlobalFrame(in frame, false);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>From CombatControlPatch: piloting a ranged machine, the attack
        /// is the main trigger alone.</summary>
        internal static void Apply(Agent agent)
        {
            if (_weapon == null || agent == null || !ReferenceEquals(_weapon.PilotAgent, agent))
                return;

            try
            {
                agent.MovementFlags &= ~MovementControlFlag.AttackMask;
                if (_fire && VrBallista.FireAllowed(_weapon))    // TEST 517: by hand, only when ready
                    agent.MovementFlags |= MovementControlFlag.AttackDown;
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        private static void Follow(VrHand hand, BvrHandState state)
        {
            int i = (int)hand;
            _down[i] = Pressed(state.Trigger, _down[i]);
        }

        private static void Let(VrHand hand, bool pulse, string why)
        {
            int i = (int)hand;
            if (!_hold[i])
                return;

            _hold[i] = false;
            _aimSet = false;
            if (pulse)
                Pulse(hand, 0.25f, 0.03f);
            if (Probe)
                VrLog.Info("TEST 512: " + hand + " let go" + (why == null ? "." : " (" + why + ")."));
        }

        private static void LetAll(string why)
        {
            Let(VrHand.Left, false, why);
            Let(VrHand.Right, false, why);
        }

        private static float Angle(FieldInfo field) => (float)field.GetValue(_weapon);

        private static string Name(UsableMachine machine) =>
            machine == null ? "?" : machine.GameEntity.Name ?? machine.GetType().Name;

        private static void Fail(Exception ex)
        {
            _broken = true;
            _hold[0] = _hold[1] = false;
            _pinned = false;
            _weapon = null;
            _fire = false;
            VrLog.Error("TEST 512: siege machines by hand failed; they are vanilla for this session.", ex);
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

        private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
