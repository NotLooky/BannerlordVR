using System;
using System.Diagnostics;
using System.Globalization;
using BannerlordVR.Interop;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 470: riding by the lead rope (VrRope draws it). A free hand grips
    /// the rope; the hand's moves then drive the horse:
    ///
    ///   left / right of where it took hold   steer, proportionally
    ///   flicked up then down                  one gear faster (stop, walk, trot, canter, gallop)
    ///   pulled to the middle of the chest     one gear slower, again every rein_pull_repeat_s held
    ///   yanked to the chest                   the vanilla quick stop (double-tap S)
    ///
    /// Let go, the horse keeps its gear and heading; the stick is vanilla
    /// whenever it is deflected, and its throttle ends the gear.
    ///
    /// The gestures are measured in STAGE space against the head's facing at
    /// the grab, so the horse's own motion never reads as a pull. The bits go in
    /// from the ControlTick postfix (CombatControlPatch), which rebuilds them
    /// every tick.
    ///
    /// TEST 472: a free hand gripping a riderless horse's saddle mounts it
    /// (Agent.Mount, the vanilla Use action's own call). Dismounting is vanilla.
    /// </summary>
    internal static class VrRein
    {
        private static readonly bool On = VrConfig.Bool("rein", true);
        private static readonly bool MountOn = VrConfig.Bool("saddle_mount", true);
        private static readonly bool Probe = VrConfig.Bool("rein_probe", true);
        private static readonly bool Analog = VrConfig.String("rein_speed_mode", "limit").Trim()
                                                 .Equals("analog", StringComparison.OrdinalIgnoreCase);

        /// <summary>TEST 470c, the user: "moving my hand a bit steers very harsh; I
        /// need it smooth and gradual". The 470 probe: x 0.55 turned the horse at
        /// the same 2.55 rad/s as x 1 - the engine's TurnRight/TurnLeft flag is the
        /// turn, and a held flag is a full turn. Now the steer is a curve over a
        /// wider throw (rein_steer_curve, full at rein_steer_full_m), eased over
        /// rein_steer_smooth_s, and the flag is PULSED: held on that fraction of
        /// ticks, so half a steer is half the turn. TEST 470e, the user: "reduce the
        /// middle deadzone and make it smoother" - 1.5 cm dead, a 1.4 curve, 0.35 s
        /// easing.</summary>
        private static readonly float SteerDead = VrConfig.Float("rein_steer_dead_m", 0.015f);
        private static readonly float SteerFull = VrConfig.Float("rein_steer_full_m", 0.30f);
        /// <summary>TEST 470g, the user: "make the first 0.4 slow to medium steer,
        /// more than this full steer". Over the first rein_steer_knee of the throw
        /// the steer climbs from rein_steer_slow to rein_steer_medium; past it, full.
        /// (470f's log: the turn grows faster than the maneuver share - 0.13 barely
        /// turned, 0.5 ~0.45 rad/s, 0.86 ~2.2 - so slow starts at 0.25, not 0.)</summary>
        /// TEST 470h, the user: "first 20% very slow to slow, then 20% slow to
        /// medium, then full" - a first knee, rein_steer_knee1, from
        /// rein_steer_very_slow to rein_steer_slow. TEST 470i: "first 40% very slow
        /// to low, second 20% medium, then full" - knees 0.4 and 0.6.
        private static readonly float SteerKnee1 = Math.Max(0.01f, VrConfig.Float("rein_steer_knee1", 0.4f));
        private static readonly float SteerKnee = Math.Max(SteerKnee1 + 0.01f, Math.Min(1f, VrConfig.Float("rein_steer_knee", 0.6f)));
        /// TEST 470j: "first 40% very slow, 40-60 slow, 60-100 full" - each band
        /// runs from its own start to its own end value (equal = flat), so any shape
        /// the user asks for is only cfg: band 1 rein_steer_band1_from/to, band 2
        /// rein_steer_band2_from/to, full past rein_steer_knee.
        private static readonly float Band1From = VrConfig.Float("rein_steer_band1_from", 0.12f);
        private static readonly float Band1To = VrConfig.Float("rein_steer_band1_to", 0.12f);
        private static readonly float Band2From = VrConfig.Float("rein_steer_band2_from", 0.25f);
        private static readonly float Band2To = VrConfig.Float("rein_steer_band2_to", 0.25f);
        private static readonly float SteerSmooth = Math.Max(0.01f, VrConfig.Float("rein_steer_smooth_s", 0.35f));

        /// <summary>TEST 470f, the user: "standing still, steering very gently and
        /// slowly is jittery". 470c's pulse is the cause - a gentle rein is the flag
        /// on one tick in ten, so the horse turns in little bursts. Now (maneuver)
        /// the flag is HELD while steering and the horse's own turn rate, its
        /// MountManeuver driven property, is set to that share of its value (never
        /// under rein_turn_min): a slow, continuous turn. It goes back the moment
        /// the rein stops steering. pulse = the 470c behaviour.</summary>
        private static readonly bool TurnByManeuver = !VrConfig.String("rein_turn_mode", "maneuver").Trim()
                                                          .Equals("pulse", StringComparison.OrdinalIgnoreCase);
        private static readonly float TurnMin = Math.Max(0.005f, VrConfig.Float("rein_turn_min", 0.03f));
        internal static readonly float FlickUp = VrConfig.Float("rein_flick_up_mps", 1.0f);
        internal static readonly float FlickDown = VrConfig.Float("rein_flick_down_mps", 1.2f);
        private static readonly float FlickWindow = VrConfig.Float("rein_flick_window_s", 0.4f);
        private static readonly float StepCooldown = VrConfig.Float("rein_step_cooldown_s", 0.35f);
        internal static readonly float YankMps = VrConfig.Float("rein_yank_mps", 2.2f);
        internal static readonly float PullM = VrConfig.Float("rein_pull_m", 0.12f);
        internal static readonly float CenterM = VrConfig.Float("rein_center_m", 0.08f);
        private static readonly float PullRepeat = VrConfig.Float("rein_pull_repeat_s", 0.7f);

        /// <summary>TEST 470b: what tells the strokes apart. The 09:27 run read
        /// flicks as yanks - a flick's down-stroke arcs back toward the chest at
        /// 2-3 m/s against 4-7 down, and one up-flick at a gallop braked the horse.
        /// A yank is mostly LEVEL (back over rein_yank_level x up), a flick mostly
        /// vertical; a pull is a steady hold (under rein_pull_still_mps for
        /// rein_pull_hold_s), never a stroke passing through, and never within
        /// rein_pull_after_step_s of a gear change.</summary>
        internal static readonly float YankLevel = VrConfig.Float("rein_yank_level", 1.0f);
        private static readonly float PullStill = VrConfig.Float("rein_pull_still_mps", 0.6f);
        private static readonly float PullHold = VrConfig.Float("rein_pull_hold_s", 0.35f);
        private static readonly float PullAfterStep = VrConfig.Float("rein_pull_after_step_s", 0.8f);
        private static readonly float Trot = VrConfig.Float("rein_trot", 0.45f);
        private static readonly float Canter = VrConfig.Float("rein_canter", 0.70f);

        private static readonly float SaddleUp = VrConfig.Float("saddle_up_m", 0.10f);
        private static readonly float SaddleReach = VrConfig.Float("saddle_reach_m", 0.35f);

        private const float Up = 0.4f;
        private const int Top = 4;
        private static readonly string[] GearName = { "stop", "walk", "trot", "canter", "gallop" };

        internal enum Gesture { None, Faster, Slower, Stop }

        /// <summary>The recogniser's memory between ticks.</summary>
        internal struct GestureState
        {
            public float UpAt;
            public float StepAt;
            public float PullAt;
            public bool Pulling;
            public bool Stepped;
            public bool Yanking;
        }

        private static VrHand? _heldBy;
        private static Vec3 _grab;
        private static Vec3 _fwd;
        private static Vec3 _right;
        private static GestureState _gs = new GestureState { UpAt = -10f, StepAt = -10f };
        private static float _steer;
        private static float _steerEased;
        private static float _steerAt;
        private static float _turnPhase;
        private static Agent _maneuvered;
        private static float _baseManeuver;
        private static float _setManeuver;

        private static int _gear;
        private static bool _stopEdge;
        private static bool _stopping;
        private static float _stopUntil;
        private static Agent _mount;
        private static Agent _limited;
        private static Agent _mountWanted;
        private static Agent _mountAsked;
        private static float _mountAskedAt;
        private static Mission _mission;

        private static long _logAt;
        private static bool _broken;
        private static bool _loggedOn;

        /// <summary>The controller holding the rope, or null.</summary>
        internal static VrHand? HeldBy => _heldBy;

        /// <summary>Where the holding hand is this tick (world), for VrRope.</summary>
        internal static Vec3 HandWorld { get; private set; }

        /// <summary>TEST 472b: a saddle grip is starting a mount. VrRoomscale holds
        /// its step meanwhile - a lean to the saddle is a head move, and the instant
        /// step's teleport in the same ticks is the prime suspect for the 09:29 run's
        /// seven sent mounts that never took.</summary>
        internal static bool Mounting => _mountWanted != null || _mountAsked != null;

        /// <summary>TEST 470: a free hand's grip on the rope. Called by VrGrab on a
        /// press edge while mounted.</summary>
        internal static bool TryGrab(Agent agent, VrHand hand, BvrHandState state, Vec3 at)
        {
            if (!On || _broken || !agent.HasMount)
                return false;

            Guard();
            if (_heldBy != null)
                return false;

            if (!VrRope.Nearest(at, out float distance))
                return false;

            _heldBy = hand;
            _grab = VrMath.ToBannerlord(state.GripPosition);
            Vec3 f = VrCameraDriver.HeadStage.rotation.f;
            f.z = 0f;
            if (f.Normalize() < 1e-3f)
                f = new Vec3(0f, 1f, 0f);
            _fwd = f;
            _right = new Vec3(f.y, -f.x, 0f);
            _gs.Pulling = false;
            _gs.Yanking = false;
            _steer = _steerEased = 0f;
            _steerAt = Now();
            HandWorld = at;
            VrRope.Hold();

            Pulse(hand, 0.5f, 0.05f);
            VrLog.Info("TEST 470 rein: " + hand + " took the reins (" + F(distance) + " m off them); gear "
                       + GearName[_gear] + ".");
            return true;
        }

        /// <summary>TEST 472: a free hand's grip on a riderless horse's saddle.
        /// The mount itself happens in Apply - Agent.Mount sets an event flag that
        /// ControlTick would otherwise clear.</summary>
        internal static bool TryMount(Agent agent, VrHand hand, Vec3 at)
        {
            if (!MountOn || _broken || agent.MountAgent != null)
                return false;

            Guard();

            Mission mission = Mission.Current;
            if (mission == null)
                return false;

            Agent best = null;
            float bestD = float.MaxValue;
            foreach (var entry in mission.MountsWithoutRiders)
            {
                Agent horse = entry.Key;
                if (horse == null || !horse.IsActive() || horse.RiderAgent != null
                    || horse.Position.DistanceSquared(agent.Position) > 9f
                    || !Saddle(horse, out Vec3 seat))
                    continue;

                float d = seat.Distance(at);
                if (d < bestD)
                {
                    bestD = d;
                    best = horse;
                }
            }

            if (best == null || bestD > SaddleReach)
                return false;

            _mountWanted = best;
            Pulse(hand, 0.6f, 0.08f);
            VrLog.Info("TEST 472 mount: " + hand + " gripped the saddle of '" + best.Name + "' (" + F(bestD)
                       + " m); mounting next tick.");
            return true;
        }

        /// <summary>The saddle: the sit bone's world origin, raised to the seat.</summary>
        private static bool Saddle(Agent horse, out Vec3 seat)
        {
            seat = Vec3.Zero;
            if (!VrRope.BoneWorld(horse, horse.Monster.RiderSitBoneIndex, out MatrixFrame bone))
                return false;

            seat = bone.origin + new Vec3(0f, 0f, SaddleUp);
            return true;
        }

        /// <summary>The rope hand's gestures, from AfterMissionTick.</summary>
        internal static void Tick()
        {
            if (!On || _broken || _heldBy == null)
                return;

            try
            {
                Guard();
                if (_heldBy == null)
                    return;

                Agent agent = Mission.Current?.MainAgent;
                if (agent == null || !agent.HasMount || agent.IsUsingGameObject)
                {
                    Let(null);
                    return;
                }

                if (VrWeaponHands.Paused()
                    || NativeMethods.bvr_get_input_state(out BvrInputState input, battleGrip: true) != (int)BvrStatus.Ok)
                    return;

                VrHand hand = _heldBy.Value;
                BvrHandState state = hand == VrHand.Left ? input.Left : input.Right;

                // Untracked: the hand has no place to be - it holds on.
                if (state.IsActive == 0)
                    return;

                if (state.Trigger < Up)
                {
                    Let(hand);
                    return;
                }

                HandWorld = VrWeaponHands.HandPoint(hand, state);

                Vec3 d = VrMath.ToBannerlord(state.GripPosition) - _grab;
                Vec3 v = VrMath.ToBannerlord(state.LinearVelocity);
                float lat = Vec3.DotProduct(d, _right);
                float back = -Vec3.DotProduct(d, _fwd);
                _steer = Steer(lat, SteerDead, SteerFull, SteerKnee1, SteerKnee, Band1From, Band1To, Band2From, Band2To);
                float now = Now();
                float sdt = Math.Min(0.1f, Math.Max(0f, now - _steerAt));
                _steerAt = now;
                _steerEased += (_steer - _steerEased) * (1f - (float)Math.Exp(-sdt / SteerSmooth));

                float vBack = -Vec3.DotProduct(v, _fwd);
                Gesture g = Classify(ref _gs, Now(), v.z, vBack, back, lat);
                if (g == Gesture.None)
                    return;

                int was = _gear;
                switch (g)
                {
                    case Gesture.Faster:
                        _gear = Math.Min(Top, _gear + 1);
                        Pulse(hand, 0.5f, 0.05f);
                        break;
                    case Gesture.Slower:
                        _gear = Math.Max(0, _gear - 1);
                        Pulse(hand, 0.3f, 0.04f);
                        break;
                    case Gesture.Stop:
                        _gear = 0;
                        _stopEdge = _stopping = true;
                        _stopUntil = Now() + 3f;
                        Pulse(hand, 0.9f, 0.15f);
                        break;
                }

                if (Probe)
                    VrLog.Info("TEST 470 rein: " + g + " - gear " + GearName[was] + " -> " + GearName[_gear]
                               + " (back " + F(back) + " m, side " + F(lat) + " m, up-speed " + F(v.z) + ", back-speed "
                               + F(vBack) + ").");
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>
        /// The recogniser. Pure; the math tests call it. A yank wins over
        /// everything; a flick is an up-stroke and then a down-stroke within the
        /// window; a pull steps once on entry and again every repeat held there.
        /// </summary>
        internal static Gesture Classify(ref GestureState s, float now, float vUp, float vBack, float back, float lat)
        {
            // A yank: fast to the chest and mostly level. Once per stroke.
            if (vBack > YankMps && vBack > YankLevel * Math.Abs(vUp))
            {
                if (s.Yanking)
                    return Gesture.None;

                // Its own travel must not also count as a pull.
                s.Yanking = true;
                s.Pulling = true;
                s.Stepped = true;
                s.PullAt = now;
                s.StepAt = now;
                return Gesture.Stop;
            }

            if (vBack < 0.5f * YankMps)
                s.Yanking = false;

            // A flick: an up-stroke, then a down-stroke, both mostly vertical.
            bool vertical = Math.Abs(vUp) > Math.Abs(vBack);
            if (vertical && vUp > FlickUp)
                s.UpAt = now;

            if (vertical && vUp < -FlickDown && now - s.UpAt <= FlickWindow && now - s.StepAt >= StepCooldown)
            {
                s.StepAt = now;
                s.UpAt = -10f;
                s.Pulling = false;
                return Gesture.Faster;
            }

            // A pull: a steady hold toward the chest, centred.
            if (back <= PullM || Math.Abs(lat) >= CenterM)
            {
                s.Pulling = false;
                return Gesture.None;
            }

            if (!s.Pulling)
            {
                if (Math.Abs(vUp) >= PullStill || Math.Abs(vBack) >= PullStill)
                    return Gesture.None;

                s.Pulling = true;
                s.Stepped = false;
                s.PullAt = now;
                return Gesture.None;
            }

            if (!s.Stepped)
            {
                if (now - s.PullAt < PullHold || now - s.StepAt < PullAfterStep)
                    return Gesture.None;

                s.Stepped = true;
                s.PullAt = now;
                s.StepAt = now;
                return Gesture.Slower;
            }

            if (now - s.PullAt >= PullRepeat)
            {
                s.PullAt = now;
                s.StepAt = now;
                return Gesture.Slower;
            }

            return Gesture.None;
        }

        /// <summary>The steer for a sideways offset: 0 inside the dead band; up to
        /// <paramref name="knee1"/> of the way to full, band 1 (from its start to
        /// its end value); up to <paramref name="knee2"/>, band 2; past it, full.
        /// Signed. Pure.</summary>
        internal static float Steer(float lat, float dead, float full, float knee1, float knee2,
                                    float band1From, float band1To, float band2From, float band2To)
        {
            float a = Math.Abs(lat);
            if (a <= dead || full <= dead)
                return 0f;
            float t = Math.Min(1f, (a - dead) / (full - dead));
            float share = t > knee2 ? 1f
                        : t > knee1 ? band2From + (band2To - band2From) * ((t - knee1) / (knee2 - knee1))
                        : band1From + (band1To - band1From) * (t / knee1);
            return Math.Sign(lat) * share;
        }

        /// <summary>A pulse train whose share of ON ticks is |duty|: the phase
        /// collects the duty and fires each time it passes 1. Pure.</summary>
        internal static bool Duty(ref float phase, float duty)
        {
            phase += Math.Min(1f, Math.Abs(duty));
            if (phase < 1f)
                return false;
            phase -= 1f;
            return true;
        }

        /// <summary>
        /// The ControlTick postfix: after the controller rebuilt the flags and the
        /// vector from the stick, so a write here is what the engine reads.
        /// </summary>
        internal static void Apply(Agent agent)
        {
            if (!On || _broken || agent == null)
                return;

            try
            {
                Guard();
                if (_mountWanted != null)
                {
                    Agent horse = _mountWanted;
                    _mountWanted = null;
                    Mount(agent, horse);
                }

                Watch(agent);

                Agent mount = agent.MountAgent;
                if (mount == null || agent.IsUsingGameObject || !ReferenceEquals(mount, _mount))
                {
                    if (_gear != 0 || _stopping)
                        Log("TEST 470 rein: " + (mount == null ? "dismounted" : agent.IsUsingGameObject
                            ? "using a machine" : "a new horse") + " - gear back to stop.");
                    ClearLimit(agent);
                    RestoreManeuver();
                    _gear = 0;
                    _stopEdge = _stopping = false;
                    _mount = mount;
                    if (mount == null || agent.IsUsingGameObject)
                        return;
                }

                if (!_loggedOn)
                {
                    _loggedOn = true;
                    VrLog.Info("TEST 470: grip the lead rope; side = steer, flick up-down = faster, pull to the "
                               + "chest = slower, yank = stop. Speed mode " + (Analog ? "analog" : "limit")
                               + ". TEST 472: grip a saddle to mount.");
                }

                Vec2 stick = agent.MovementInputVector;
                float x = stick.x;
                float y = stick.y;

                // The stick's throttle ends the gear: it is vanilla from here.
                if (stick.y != 0f && (_gear != 0 || _stopping))
                {
                    Log("TEST 470 rein: the stick took the throttle - gear " + GearName[_gear] + " -> stop.");
                    _gear = 0;
                    _stopEdge = _stopping = false;
                }

                if (_stopping)
                {
                    if (_stopEdge)
                    {
                        _stopEdge = false;
                        agent.EventControlFlags = (agent.EventControlFlags & ~Agent.EventControlFlag.DoubleTapToDirectionMask)
                                                  | Agent.EventControlFlag.DoubleTapToDirectionDown;
                    }

                    // As vanilla's own auto-dismount brakes: full back until slow
                    // (3 s at most, so a misread speed cannot back the horse away).
                    if (mount.GetCurrentVelocity().y > 0.5f && Now() < _stopUntil)
                        y = -1f;
                    else
                        _stopping = false;
                    ClearLimit(agent);
                }
                else if (_gear > 0 && stick.y == 0f)
                {
                    y = Throttle(agent, mount);
                }
                else
                {
                    ClearLimit(agent);
                }

                // The reins steer only while held and the stick is centred; let go,
                // the horse keeps its heading (the user's choice).
                float rein = _heldBy != null ? _steerEased : 0f;
                if (_heldBy == null)
                    _steerEased = 0f;
                bool steering = x == 0f && Math.Abs(rein) > 0.02f;

                // TEST 470d, the user: "the horse moves right, not only rotates".
                // Without its turn flag the vector's x is a SIDESTEP; vanilla only
                // ever sends x with the flag. So x rides the pulse: on the ticks the
                // flag is on, x is the rein; on the others, 0 - a turn, never a slide.
                Agent.MovementControlFlag turn = 0;
                if (steering)
                {
                    bool on = TurnByManeuver || Duty(ref _turnPhase, rein);
                    if (TurnByManeuver)
                        Maneuver(mount, Math.Abs(rein));
                    if (on)
                    {
                        x = rein;
                        turn = rein > 0f ? Agent.MovementControlFlag.TurnRight : Agent.MovementControlFlag.TurnLeft;
                    }
                }
                else
                {
                    _turnPhase = 0f;
                    RestoreManeuver();
                }

                if (x != stick.x || y != stick.y)
                    agent.MovementInputVector = new Vec2(x, y);

                if (steering)
                    agent.MovementFlags = (agent.MovementFlags
                                           & ~(Agent.MovementControlFlag.TurnRight | Agent.MovementControlFlag.TurnLeft))
                                          | turn;

                Report(mount, x, y);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>TEST 470f: the horse's turn rate at <paramref name="share"/> of
        /// its own (MountManeuver), pushed to the engine only when it changes.</summary>
        private static void Maneuver(Agent mount, float share)
        {
            if (!ReferenceEquals(_maneuvered, mount))
            {
                RestoreManeuver();
                _maneuvered = mount;
                _baseManeuver = mount.GetAgentDrivenPropertyValue(DrivenProperty.MountManeuver);
                _setManeuver = _baseManeuver;
            }

            float want = _baseManeuver * Math.Max(TurnMin, Math.Min(1f, share));
            if (Math.Abs(want - _setManeuver) <= 0.01f * Math.Max(1f, _baseManeuver))
                return;

            mount.SetAgentDrivenPropertyValueFromConsole(DrivenProperty.MountManeuver, want);
            mount.UpdateCustomDrivenProperties();
            _setManeuver = want;
        }

        /// <summary>TEST 470f: the horse's own turn rate back.</summary>
        private static void RestoreManeuver()
        {
            Agent mount = _maneuvered;
            _maneuvered = null;
            if (mount == null || !mount.IsActive() || Math.Abs(_setManeuver - _baseManeuver) < 1e-4f)
                return;

            mount.SetAgentDrivenPropertyValueFromConsole(DrivenProperty.MountManeuver, _baseManeuver);
            mount.UpdateCustomDrivenProperties();
            _setManeuver = _baseManeuver;
        }

        /// <summary>The throttle for the current gear. Limit mode drives the
        /// horse at full and caps it, the way the AI paces its riders (rider and
        /// mount together); analog mode only scales the stick.</summary>
        private static float Throttle(Agent agent, Agent mount)
        {
            if (Analog)
            {
                ClearLimit(agent);
                return _gear == 1 ? 0.25f : _gear == 2 ? 0.45f : _gear == 3 ? 0.70f : 1f;
            }

            float max = mount.GetMaximumForwardUnlimitedSpeed();
            float walk = mount.WalkingSpeedLimitOfMountable;
            float limit = _gear == 1 ? (walk > 0f ? walk : 0.2f * max)
                        : _gear == 2 ? Trot * max
                        : _gear == 3 ? Canter * max
                        : -1f;

            agent.SetMaximumSpeedLimit(limit, false);
            mount.SetMaximumSpeedLimit(limit, false);
            _limited = limit > 0f ? mount : null;
            return 1f;
        }

        private static void ClearLimit(Agent agent)
        {
            if (_limited == null)
                return;

            if (_limited.IsActive())
                _limited.SetMaximumSpeedLimit(-1f, false);
            if (agent != null && agent.IsActive())
                agent.SetMaximumSpeedLimit(-1f, false);
            _limited = null;
        }

        private static void Mount(Agent agent, Agent horse)
        {
            if (agent.MountAgent != null || !horse.IsActive() || horse.RiderAgent != null)
                return;

            if (!agent.CanInteractWithAgent(horse, 0f))
            {
                VrLog.Info("TEST 472 mount: the game says no - reach " + (agent.CanReachAgent(horse) ? "ok" : "too far")
                           + ", " + F(agent.Position.Distance(horse.Position)) + " m from the horse.");
                return;
            }

            bool idle = agent.GetCurrentAction(0) == ActionIndexCache.act_none;
            bool rearing = horse.GetCurrentActionType(0) == Agent.ActionCodeType.Rear;
            agent.Mount(horse);
            bool flagged = (agent.EventControlFlags & Agent.EventControlFlag.Mount) != 0;
            _mountAsked = flagged ? horse : null;
            _mountAskedAt = Now();
            VrLog.Info("TEST 472 mount: Agent.Mount('" + horse.Name + "') sent - " + (flagged
                ? "the Mount event is set."
                : "the game refused it (channel 0 idle " + idle + ", horse rearing " + rearing + ")."));
        }

        /// <summary>TEST 472b: did the asked mount take? Logged once either way.</summary>
        private static void Watch(Agent agent)
        {
            if (_mountAsked == null)
                return;

            float waited = Now() - _mountAskedAt;
            if (agent.MountAgent != null)
            {
                VrLog.Info("TEST 472b mount: in the saddle after " + F(waited) + " s.");
                _mountAsked = null;
            }
            else if (waited > 3f)
            {
                VrLog.Info("TEST 472b mount: NOT mounted 3 s after the event - channel 0 " + agent.GetCurrentActionType(0)
                           + ", " + F(agent.Position.Distance(_mountAsked.Position)) + " m from the horse, horse "
                           + (_mountAsked.IsActive() ? _mountAsked.GetCurrentActionType(0).ToString() : "gone") + ".");
                _mountAsked = null;
            }
        }

        /// <summary>The rope let go (null: not by the hand - dismount, a machine).</summary>
        private static void Let(VrHand? hand)
        {
            if (_heldBy == null)
                return;

            _heldBy = null;
            _steer = 0f;
            _gs.Pulling = false;
            VrRope.Let();
            if (hand != null)
                Pulse(hand.Value, 0.25f, 0.03f);
            Log("TEST 470 rein: let go" + (hand == null ? " (not by the hand)" : "") + "; gear "
                + GearName[_gear] + " kept.");
        }

        private static void Report(Agent mount, float x, float y)
        {
            if (!Probe || (_gear == 0 && _heldBy == null && !_stopping) || Stopwatch.GetTimestamp() < _logAt)
                return;

            _logAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
            Vec2 v = mount.GetCurrentVelocity();
            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 470 rein: gear {0}, y {1:F2}, limit {2:F1}, speed {3:F1} m/s (max {4:F1}, walk {5:F1}), "
                + "turn {6:F2} rad/s at x {7:F2}, maneuver {8:F1} of {9:F1}{10}.",
                GearName[_gear], y, mount.GetMaximumSpeedLimit(), v.y, mount.GetMaximumForwardUnlimitedSpeed(),
                mount.WalkingSpeedLimitOfMountable, mount.GetTurnSpeed(), x,
                _maneuvered != null ? _setManeuver : mount.GetAgentDrivenPropertyValue(DrivenProperty.MountManeuver),
                _maneuvered != null ? _baseManeuver : mount.GetAgentDrivenPropertyValue(DrivenProperty.MountManeuver),
                _stopping ? ", stopping" : _heldBy != null ? ", held by " + _heldBy.Value : ""));
        }

        /// <summary>The mission changed: everything died with it - drop the
        /// references without touching them.</summary>
        private static void Guard()
        {
            if (ReferenceEquals(_mission, Mission.Current))
                return;

            _mission = Mission.Current;
            _heldBy = null;
            _mount = _limited = _mountWanted = _mountAsked = _maneuvered = null;
            _gear = 0;
            _stopEdge = _stopping = false;
        }

        private static void Log(string text)
        {
            if (Probe)
                VrLog.Info(text);
        }

        private static void Fail(Exception ex)
        {
            _broken = true;
            _heldBy = null;
            try
            {
                RestoreManeuver();
            }
            catch (Exception)
            {
                // Already failing; the horse's next stat update restores it anyway.
            }
            VrLog.Error("TEST 470/472: riding by the rope failed; the horse is vanilla for this session.", ex);
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

        private static readonly long T0 = Stopwatch.GetTimestamp();

        private static float Now() => (float)((Stopwatch.GetTimestamp() - T0) / (double)Stopwatch.Frequency);

        private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
