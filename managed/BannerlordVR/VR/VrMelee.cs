using System;
using System.Diagnostics;
using System.Globalization;
using BannerlordVR.Interop;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

// The engine's combat types are nested in Agent, which is also where the
// methods that read them back live. Aliases keep the gesture code readable.
using ActionCodeType = TaleWorlds.MountAndBlade.Agent.ActionCodeType;
using ActionStage = TaleWorlds.MountAndBlade.Agent.ActionStage;
using MovementControlFlag = TaleWorlds.MountAndBlade.Agent.MovementControlFlag;
using UsageDirection = TaleWorlds.MountAndBlade.Agent.UsageDirection;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 245. Motion melee, played through the game's own combat.
    ///
    /// THE APPROACH, AND WHY IT IS NOT PHYSICAL COMBAT
    ///
    /// Physical combat (VrPhysicalCombat) decides the hit itself and hands the
    /// engine a finished Blow. It works and it is instant, but the engine never
    /// sees an ATTACK: the AI does not defend against it, an enemy's raised
    /// shield only stops it because the mod reconstructed their blade, and every
    /// block is geometry the mod invented. This class does the opposite. It
    /// writes the two bits the game's own controller writes - the attack
    /// direction and the guard - and then stays out of the way. The engine
    /// swings, its collision decides what the blade met, and its damage, AI,
    /// sound, stun, armour and missile rules all run because none of them were
    /// replaced.
    ///
    /// THE SEAM IS MissionMainAgentController.ControlTick
    ///
    /// Each mission tick the game rebuilds Agent.MovementFlags from the trigger
    /// and the stick. CombatControlPatch postfixes that method and ORs our bits
    /// in after the game has had its say - per mission tick, so it cannot race
    /// the engine's own combat state machine whatever the frame rate is doing.
    /// While a melee weapon or a shield is in hand we also clear the melee bits
    /// the game set, so one control scheme owns the fight rather than two
    /// fighting over it (melee_owns_combat = 0 leaves the trigger in play).
    ///
    /// THE GESTURES, AND THE FRAME THEY ARE MEASURED IN
    ///
    /// A swing is a controller velocity above swing_speed, classified on its
    /// strongest body-relative axis: hand right, hand left, vertical (either
    /// way) or forward. The velocity is the runtime's fused LinearVelocity, not
    /// a differentiated pose - two reads of a pose inside one frame are
    /// identical, and a difference of zero reads as a hand that stopped. The
    /// axes are the body anchor's (right, forward, up), which is a yaw frame:
    /// turning to look somewhere does not change what "left" means, and walking
    /// or riding cancels out of it for the same reason it did in TEST 237.
    ///
    /// The engine's own dictionary (Native/ModuleData/item_usage_sets.xml, read
    /// rather than guessed): attack_up is the overswing, attack_down is the
    /// thrust, attack_left/right are the side slashes. So melee_swing_map's
    /// default left,right,up,down means: hand left -> AttackLeft, hand right ->
    /// AttackRight, vertical -> AttackUp (overhead), forward -> AttackDown
    /// (stab). If a direction comes out mirrored, one line in the config fixes
    /// it - the swing result probe says which way the engine went.
    ///
    /// A block is a HAND POSITION, not a motion: the hand held raised and in
    /// front, and where it sits says which guard. Out to a side ->
    /// DefendLeft/Right (asked first, because the poses are named by place),
    /// centred and high -> DefendUp, centred and low -> DefendDown (the
    /// engine's own guard for a thrust). DefendBlock is always written with it:
    /// the direction bit alone is a weapon stance, and the journal's TEST 236
    /// says so. A shield guards by the same rule - raised and in front, nothing
    /// else - and if it is not, the weapon hand is asked instead.
    ///
    /// INSTANT, AND NO ANIMATIONS OF OURS
    ///
    /// The attack bit is set the frame the swing is seen and dropped
    /// melee_ready_ticks mission ticks later, which is the shortest window the
    /// engine will accept (raise it if the log says the engine stayed idle).
    /// The arms and the weapon are never handed to the animation: VrWeaponHands
    /// keeps writing the hands, so what you see is your controller and the
    /// engine's swing runs underneath it. What is left of the engine's timing -
    /// its wind-up - is the price of the engine doing the hitting, and TEST 249
    /// is where that gets attacked if it still feels slow.
    /// </summary>
    public static class VrMelee
    {
        /// <summary>melee = 0 gives melee back to the trigger, exactly as it
        /// was before this class existed. Physical combat is held off while it
        /// is on, because two hit systems on one swing is one too many.
        ///
        /// Settable at runtime from the VR panel (the "Swing and guard with your
        /// hands" checkbox): off is the game's own combat, live, with no restart.
        /// See SetEnabled.</summary>
        public static bool Enabled { get; private set; } = VrConfig.Bool("melee", true);

        /// <summary>
        /// The VR panel's switch. Turning it OFF is the whole feature going away
        /// at once: the gesture state is cleared, so no attack or guard bits are
        /// written any more (Apply returns at once), the engine's own trigger and
        /// stick combat is in charge, the instant blade strike is off because
        /// VrPhysicalCombat reads Enabled, and the attack holds - the body freeze,
        /// the chest hold, the camera anchor - all stop because EngineAttacking
        /// answers false when this is off. Nothing needs a restart.
        ///
        /// The hands, the weapons, the full-body IK and the gamepad emulation are
        /// separate switches and are untouched.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            if (enabled == Enabled)
                return;

            Enabled = enabled;
            Clear();

            VrLog.Info("VR panel: motion-control combat is now "
                       + (enabled ? "ON" : "off") + ". "
                       + (enabled
                           ? "Swings drive the game's own attack direction, a raised hand "
                             + "is the guard, and the blade lands the hit."
                           : "The game's own combat is in charge: the trigger and the "
                             + "movement stick attack and block, every animation plays, "
                             + "and the camera follows the body."));
        }

        /// <summary>
        /// melee_instant_hit = 1: the blade lands the blow the moment it touches,
        /// through the physical strike path, while the engine's own attack still
        /// runs for the AI and the animation.
        ///
        /// WHY THIS EXISTS. TEST 245-247 drove everything through the engine, and
        /// the engine's own swing was measured at ~430 ms from the gesture to the
        /// blow - ready phase, release, then the collision - with most swings
        /// missing outright because the strike is timed by an animation the
        /// player's hand does not follow. The speed multiplier moves neither the
        /// ready phase nor the strike, so that timing is not ours to shorten.
        /// The blade, which is where the player's swing actually is, is the only
        /// thing that can land the damage on time - and it is the hit that was
        /// proven and tuned in TEST 236-244.
        ///
        /// What stays the engine's: the attack state (so the AI sees the swing
        /// and defends), the guard state (so blocks, shields and arrows are
        /// vanilla), the swing whoosh, and the hit reaction and sound of the blow
        /// itself. 0 gives the damage back to the engine's animation timing.
        /// </summary>
        public static bool InstantStrike { get; } = VrConfig.Bool("melee_instant_hit", true);

        /// <summary>When the last swing fired, on the Stopwatch clock. Read by
        /// VrCameraDriver's TEST 251 attack-motion probe, so "the camera moves
        /// when I swing" becomes numbers in the log rather than an argument.</summary>
        internal static long LastSwingAt { get; private set; }

        // --- TEST 261: one swing, one event ---------------------------------
        //
        // SwingSerial / SwingHand / SwingHandSpeed / SwingThrust are gone (TEST
        // 303): the physical blade no longer opens a hit window on the gesture -
        // it is live every tick - so nothing read them. The gesture still drives
        // VrEnemyGuard.OnSwing and SwingDirection below.

        /// <summary>TEST 263: the attack direction the gesture asked the engine
        /// for (AttackLeft/Right/Up/Down), so the player's own hit can hand the
        /// vanilla pipeline the same AttackDirection the engine's swing carries -
        /// None until the first swing of the mission, or in classic mode, which
        /// never fires a gesture.</summary>
        internal static UsageDirection SwingDirection { get; private set; } = UsageDirection.None;

        // --- the swing (TEST 245) -------------------------------------------

        /// <summary>Hand speed in m/s that starts an attack. 2.0 is TEST 242b's
        /// bar, where a reach or a handover stopped misfiring and a real swing
        /// reads 3-12 m/s at the grip.</summary>
        private static readonly float SwingSpeed = VrConfig.Float("swing_speed", 2.0f);

        /// <summary>Hand speed the swing must drop below to re-arm. Hysteresis:
        /// without it one swing that hovers at the threshold fires repeatedly.</summary>
        private static readonly float SwingReset = VrConfig.Float("swing_reset_speed", 1.0f);

        /// <summary>Minimum gap between attacks, counted from the previous
        /// attack's start. 250 ms is TEST 242b's 0.5 s halved, because a gesture
        /// attack that fires on release has no hold to spend.</summary>
        private static readonly float SwingCooldownS =
            VrConfig.Float("swing_cooldown_ms", 250f) * 0.001f;

        /// <summary>Exponential smoothing for the velocity, in milliseconds; 0
        /// turns it off. Tracking noise at the grip is a few cm/s and a real
        /// swing is a ramp - a filter delays the ramp, it does not deform it.</summary>
        private static readonly float SmoothMs = Math.Max(0f, VrConfig.Float("melee_smooth_ms", 40f));

        /// <summary>Mission ticks the attack bit is held before it is released.
        /// The engine holds its ready pose while the bit is set and swings when
        /// it clears - so this IS the wind-up, and one mission tick is the
        /// shortest press there is.
        ///
        /// 4 (TEST 261, was 1): at one tick the engine's ReadyMelee was over
        /// inside ~33 ms, which is shorter than the AI's own decide interval -
        /// so the AI could not react to a swing it never sampled. Four ticks is
        /// ~66 ms at 60 Hz: still instant to the player (the blade lands the
        /// damage on the swing frame), long enough for the engine's AI to see
        /// the attack and choose a guard. melee_ai_probe reports whether it
        /// does; drop this back toward 1 if the swing-result line says the
        /// engine never entered an attack.</summary>
        private static readonly int ReadyTicks = Math.Max(1, VrConfig.Int("melee_ready_ticks", 4));

        /// <summary>TEST 271. 0 stops a gesture writing the engine's attack bit,
        /// so the character plays no swing animation and the blade's collision
        /// (melee_instant_hit) is the only hit. The swing event, the cooldown and
        /// the guard are unchanged. Cost: the engine never sees an attack, so
        /// the AI cannot read a swing it never sees, so VrEnemyGuard raises the
        /// guard itself (TEST 272).</summary>
        internal static readonly bool EngineSwing = VrConfig.Bool("melee_engine_swing", true);

        /// <summary>0 keeps the trigger on a horse; the gesture story is untested
        /// there and a horse's own motion is a bad input to a swing.</summary>
        private static readonly bool Mounted = VrConfig.Bool("melee_mounted", false);

        /// <summary>1 masks the game's own melee bits while we hold a melee
        /// weapon or shield, so the gesture is the fight's only input. 0 leaves
        /// the trigger and the stick in play as well - and 0 is the default,
        /// deliberately: on the first run of a new input path, a fallback that
        /// works is worth more than a purity the player did not ask for. Set it
        /// to 1 once the gestures have earned it.</summary>
        private static readonly bool OwnsCombat = VrConfig.Bool("melee_owns_combat", false);

        /// <summary>Logs every swing and what the engine did with it.</summary>
        private static readonly bool Probe = VrConfig.Bool("swing_probe", true);

        // --- TEST 246: the strike, as close to the swing as the engine goes --

        /// <summary>
        /// The engine's own attack-speed multiplier for the player, while the
        /// engine is in a melee attack action. 1.0 is the engine's own pace -
        /// what TEST 245 shipped, where the damage lands when the swing
        /// animation reaches the target. Higher runs the whole attack (ready and
        /// release) faster, so the strike frames arrive sooner.
        ///
        /// It is the engine's OWN stat: AgentDrivenProperties.
        /// SwingSpeedMultiplier is the number weapon skill feeds in vanilla, and
        /// ThrustOrRangedReadySpeedMultiplier is the same for a thrust's ready
        /// phase. Nothing here invents a hit; the collision, the damage and the
        /// reactions are still the engine's, only sooner. The cost is that the
        /// engine's own speed bonus reads a faster blade, so a higher number
        /// also hits harder - which is vanilla's rule, applied faster.
        /// </summary>
        private static readonly float StrikeSpeed =
            Math.Max(1f, VrConfig.Float("melee_strike_speed", 2.0f));

        /// <summary>Logs the timeline of the engine's upper body after a swing
        /// (ready, release, idle, with progress) and the latency to the engine's
        /// own blow - the number "instant" is measured in.</summary>
        private static readonly bool TimelineProbe = VrConfig.Bool("melee_probe", true);

        /// <summary>The attack each gesture asks for, in the order: hand moved
        /// left, hand moved right, hand moved vertically, hand moved forward.
        /// Each entry is left, right, up or down.
        ///
        /// A NEW KEY NAME, deliberately: the old gesture experiment's swing_map
        /// is still pinned in people's configs, and its third and fourth entries
        /// meant different gestures (hand down, hand forward) - reading it under
        /// the same name would silently answer for the wrong motions.</summary>
        private static readonly UsageDirection[] SwingMap =
            ParseMap(VrConfig.String("melee_swing_map", "left,right,up,down"));

        // --- the guard (TEST 245) -------------------------------------------

        /// <summary>How long the pose must be held before it counts as a guard,
        /// in milliseconds. 0 is instant, which is the point; the key exists for
        /// the case where a wind-up passes through a guard pose on its way to an
        /// attack.</summary>
        private static readonly float BlockHoldS =
            Math.Max(0f, VrConfig.Float("block_hold_ms", 0f)) * 0.001f;

        /// <summary>Hand height relative to the head that counts as raised. This
        /// and the three below are the whole block table; the probe logs the
        /// hand's actual offsets so they can be tuned from numbers.
        ///
        /// -0.45 (TEST 261, was -0.25): the session log had 30 "no guard" lines
        /// with the weapon hand at -0.25 to -0.36 below the eye - a sword held
        /// at chest height, which is where a guard actually lives - and no
        /// guard bit was ever written, so the sword could not block at all.
        /// -0.45 takes the chest and still refuses a hand at the waist.</summary>
        private static readonly float BlockRaise = VrConfig.Float("block_raise_m", -0.45f);

        /// <summary>How far in front of the head the hand must be to guard.</summary>
        private static readonly float BlockForward = VrConfig.Float("block_forward_m", 0.15f);

        /// <summary>Above this height, relative to the head, the guard is up.</summary>
        private static readonly float BlockUp = VrConfig.Float("block_up_m", -0.05f);

        /// <summary>Further than this to a side is a left or right guard; between
        /// the two sides and low is the guard for a thrust.</summary>
        private static readonly float BlockSide = VrConfig.Float("block_side_m", 0.15f);

        /// <summary>Logs the guard pose and the direction it chose, on change and
        /// once a second while held.</summary>
        private static readonly bool BlockProbe = VrConfig.Bool("block_probe", true);

        /// <summary>
        /// TEST 261: where the guard DIRECTION comes from while a guard is held.
        /// On (default), it is the game's own AutoBlockModel - the same answer
        /// vanilla's "Block: Auto" option uses. That model scans for enemies in
        /// an attack stage and returns the direction their blow is coming from,
        /// which is the only way to get this right: the engine's block test is
        /// directional, and its left/right flags are CROSSED against the
        /// attack's (an attack from your left is guarded with DefendRight - the
        /// mapping in MissionMainAgentController.ControlTick). Reading the
        /// direction off the hand, which the session log shows happening for
        /// every centered guard, writes the wrong side and the sword never
        /// stops the blow. block_auto_direction = 0 restores the hand mapping.
        /// </summary>
        private static readonly bool AutoDirection = VrConfig.Bool("block_auto_direction", true);

        // --------------------------------------------------------------------

        private const int Roles = 2;

        /// <summary>Everything the engine's melee input consists of, and nothing
        /// else: the four attack directions, the four guards, and the bit that
        /// turns a guard into a block. Masking exactly these means the movement
        /// flags are untouched and the game's own auto-block cannot double up.</summary>
        private const MovementControlFlag MeleeMask =
            MovementControlFlag.AttackMask
            | MovementControlFlag.DefendMask
            | MovementControlFlag.DefendBlock;

        private sealed class Hand
        {
            public bool Have;
            public Vec3 Smooth;
            public float Speed;

            /// <summary>Ready to fire. Cleared by a swing, set again by slowing
            /// below swing_reset_speed - one gesture, one attack.</summary>
            public bool Armed = true;

            public long CooldownUntil;
        }

        private static readonly Hand[] _hands = { new Hand(), new Hand() };

        // The attack window. _attack is what is being asked for; _attackReads
        // counts the mission ticks the bit has been held.
        private static UsageDirection _attack = UsageDirection.None;
        private static int _attackReads;

        // What to log when the engine answers, and how many ticks until then.
        private static UsageDirection _attackAsked = UsageDirection.None;
        private static int _responseIn = -1;

        // The guard, and the clock that proves the pose is held rather than passed
        // through.
        private static UsageDirection _guard = UsageDirection.None;
        private static UsageDirection _guardPending = UsageDirection.None;
        private static long _guardSince;
        private static UsageDirection _guardLogged = UsageDirection.None;
        private static long _guardLoggedAt;

        /// <summary>When the last "no guard" probe line was written, so a sword
        /// held at your side does not fill the log.</summary>
        private static long _blockProbeAt;

        // --- TEST 246: the strike window and its timeline ---------------------

        /// <summary>The player's own speed stats, captured before we multiply
        /// them and given back when the attack ends. Captured rather than
        /// assumed, because weapon skill feeds them.</summary>
        private static bool _haveBase;
        private static float _baseSwing;
        private static float _baseThrust;
        private static bool _loggedBase;
        private static float _loggedSwing;

        /// <summary>When the swing being measured happened, how long the
        /// timeline is watched for, and whether a blow arrived inside it.</summary>
        private static long _probeAt;
        private static long _probeUntil;
        private static bool _probeHit;
        private static ActionCodeType _lastType = ActionCodeType.Other;
        private static ActionStage _lastStage = ActionStage.None;

        private static long _stamp;
        private static bool _broken;
        private static bool _loggedOn;

        /// <summary>
        /// One frame, from AfterMissionTick beside the hands and the physical
        /// combat. Reads the controllers and decides; writes nothing to the
        /// engine. The bits are written by Apply, from the game's own control
        /// tick, because that is the tick that owns MovementFlags.
        /// </summary>
        public static void Tick()
        {
            if (!Enabled || _broken)
                return;

            try
            {
                long now = Stopwatch.GetTimestamp();
                float dt = _stamp == 0L
                    ? 0f
                    : (float)((now - _stamp) / (double)Stopwatch.Frequency);
                _stamp = now;

                if (dt > 0.1f)
                    dt = 0.1f;

                Mission mission = Mission.Current;
                Agent agent = mission?.MainAgent;

                if (!Ready(mission, agent))
                {
                    Clear();
                    return;
                }

                if (NativeMethods.bvr_get_input_state(out BvrInputState input) != (int)BvrStatus.Ok)
                {
                    Clear();
                    return;
                }

                // Which controller is the weapon hand and which the shield hand.
                // The game's own monster definition answers it (VrHandSlots), so a
                // bow carried on the left wrist cannot turn the shield hand into
                // the swing hand.
                MBAgentVisuals visuals = agent.AgentVisuals;
                Skeleton skeleton = visuals != null ? visuals.GetSkeleton() : null;
                if (skeleton != null)
                    VrHandSlots.Resolve(agent, skeleton, out _, out _);

                WeaponComponentData main = Held(agent.WieldedWeapon);
                WeaponComponentData off = Held(agent.WieldedOffhandWeapon);

                // Bows and crossbows stay with the trigger: the game's own aim
                // path owns them and a swing is not a shot. A weapon with no
                // melee usage at all (a bow, a quiver) never gestures.
                if (main != null && (main.IsBow || main.IsCrossBow || !main.IsMeleeWeapon))
                {
                    Clear();
                    return;
                }

                bool weapon = main != null && main.IsMeleeWeapon;
                bool shield = off != null && off.IsShield;

                if (!weapon && !shield)
                {
                    Clear();
                    return;
                }

                Sample(VrHand.Left, input.Left, dt);
                Sample(VrHand.Right, input.Right, dt);

                // TEST 261: the enemy's reaction (the engine's own AI stats and
                // its defend actions) runs beside the swing, one frame each.
                VrEnemyGuard.Tick(agent);

                if (!_loggedOn)
                    LogOn();

                if (weapon)
                    SwingPass(agent, VrHandSlots.MainController, now);

                GuardPass(agent, input, weapon, shield, now);
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
            }
        }

        /// <summary>
        /// The combat bits for this mission tick, consumed by CombatControlPatch.
        /// Called from inside the game's own control tick, so the attack window
        /// is counted in the engine's ticks rather than in frames.
        /// </summary>
        public static void Apply(Agent agent)
        {
            if (!Enabled || _broken || agent == null)
                return;

            try
            {
                // TEST 246 first: the attack-speed window is about what the
                // engine is DOING, not about what this tick asks for, so it runs
                // whether or not there are bits to write.
                Strike(agent, Stopwatch.GetTimestamp());

                if (!Owns(agent))
                    return;

                MovementControlFlag ours = Next(agent);
                MovementControlFlag flags = agent.MovementFlags;

                // Clear first, then OR: the game composes the trigger and the
                // stick into these same bits earlier in the tick, and while we
                // own the fight ours must be the whole answer rather than one
                // more voice in it.
                if (OwnsCombat)
                    flags &= ~MeleeMask;

                flags |= ours;
                agent.MovementFlags = flags;
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
            }
        }

        /// <summary>Named by CombatControlPatch when a postfix call throws.</summary>
        internal static void ReportFailure(Exception ex)
        {
            if (_broken)
                return;

            _broken = true;
            Clear();
            VrLog.Error("TEST 245: motion melee failed; it is off for this session and melee "
                        + "goes back to the trigger. Physical combat is unaffected.", ex);
        }

        // --------------------------------------------------------------------

        /// <summary>
        /// Whether this is a moment gestures may drive: an active player in a
        /// fight, no menu, no conversation, deployment not in progress and not
        /// mounted (unless asked). Anything else stands down rather than guessing
        /// - the trigger is always there underneath.
        /// </summary>
        private static bool Ready(Mission mission, Agent agent)
        {
            if (mission == null || agent == null || !agent.IsActive() || !agent.CombatActionsEnabled)
                return false;

            if (!VrSystem.IsActive || !VrMissionPhase.VrDrivesLook || VrMissionPhase.UiMenuOpen || VrMenu.Open)
                return false;

            if (mission.Mode == MissionMode.Conversation || mission.Mode == MissionMode.Barter)
                return false;

            // TEST 341: a siege weapon - a ship's ballista or mangonel, a land
            // onager - fires on the pilot's attack bits (RangedSiegeWeapon.OnTick),
            // and clearing them here meant it never fired.
            if (agent.IsUsingGameObject)
                return false;

            if (agent.MountAgent != null && !Mounted)
                return false;

            return true;
        }

        /// <summary>The same question from Apply's side, plus the one weapon
        /// rule: a ranged weapon in hand means the whole melee mask is left to
        /// the game.</summary>
        private static bool Owns(Agent agent)
        {
            if (!Ready(Mission.Current, agent))
                return false;

            WeaponComponentData main = Held(agent.WieldedWeapon);
            return main == null || (!main.IsBow && !main.IsCrossBow && main.IsMeleeWeapon);
        }

        /// <summary>One controller's velocity, in the body's own axes (right,
        /// forward, up) and smoothed. The fused runtime velocity, not a
        /// differentiated pose.</summary>
        private static void Sample(VrHand hand, BvrHandState state, float dt)
        {
            Hand h = _hands[(int)hand];

            if (state.IsActive == 0)
            {
                h.Have = false;
                h.Armed = true;
                h.Speed = 0f;
                return;
            }

            Vec3 v = Body(VrMath.ToBannerlord(state.LinearVelocity));

            if (!h.Have)
            {
                h.Have = true;
                h.Smooth = v;
            }
            else
            {
                h.Smooth = h.Smooth + (v - h.Smooth) * SmoothAlpha(dt);
            }

            h.Speed = h.Smooth.Length;
        }

        /// <summary>
        /// One mission tick of the weapon hand. Fires at most one attack, and
        /// only when the hand is armed, moving and free of the cooldown.
        /// </summary>
        private static void SwingPass(Agent agent, VrHand controller, long now)
        {
            Hand h = _hands[(int)controller];

            if (!h.Have || _attack != UsageDirection.None)
                return;

            if (now < h.CooldownUntil)
                return;

            if (!h.Armed)
            {
                if (h.Speed < SwingReset)
                    h.Armed = true;

                return;
            }

            if (h.Speed < SwingSpeed)
                return;

            int gesture = Gesture(h.Smooth);

            if (gesture < 0)
            {
                // Moving fast, but backwards - a pull or a wind-up, not a blow.
                // Disarmed until the hand slows so the same motion cannot fire
                // the moment it turns around.
                h.Armed = false;
                return;
            }

            Fire(agent, controller, h, gesture, now);
        }

        /// <summary>Which of the four gestures the velocity is.</summary>
        private static int Gesture(Vec3 v)
        {
            float x = Math.Abs(v.x);
            float y = Math.Abs(v.y);
            float z = Math.Abs(v.z);

            if (z >= x && z >= y)
                return 2;                       // vertical: overhead either way

            if (y >= x)
                return v.y > 0f ? 3 : -1;       // forward is a stab; backward is not

            return v.x >= 0f ? 1 : 0;
        }

        private static void Fire(Agent agent, VrHand controller, Hand h, int gesture, long now)
        {
            UsageDirection dir = SwingMap[gesture];

            h.Armed = false;
            h.CooldownUntil = now + Seconds(SwingCooldownS);

            _attack = dir;
            _attackAsked = dir;
            _attackReads = 0;
            _responseIn = ReadyTicks + 6;

            // Published for VrCameraDriver's attack-motion probe (TEST 251), which
            // measures what a swing does to the body and to the camera anchor.
            LastSwingAt = now;

            // The AI's guard and the blow's AttackDirection read this swing; the
            // blade's own hits do not wait for it (TEST 303).
            SwingDirection = dir;
            VrEnemyGuard.OnSwing(agent, h.Speed, dir);

            // TEST 246: watch this swing - where the engine goes with it and how
            // long its blow takes.
            _probeAt = now;
            _probeUntil = now + Seconds(1.2f);
            _probeHit = false;
            _lastType = ActionCodeType.Other;
            _lastStage = ActionStage.None;

            // An attack is not a stance. Whatever the hand was guarding with
            // stops the moment it swings.
            _guard = UsageDirection.None;
            _guardPending = UsageDirection.None;
            _guardSince = 0L;
            _guardLogged = UsageDirection.None;

            if (Probe)
            {
                Vec3 v = h.Smooth;
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 245 swing: the {0} hand at {1:F1} m/s, body-relative (right {2:F2}, "
                    + "forward {3:F2}, up {4:F2}); gesture {5}, so the attack asked for is {6}.",
                    controller == VrHand.Right ? "right" : "left", h.Speed, v.x, v.y, v.z,
                    GestureName(gesture), Name(dir)));
            }
        }

        /// <summary>
        /// The guard, once a frame. The shield hand first (a shield is a
        /// shield): a raised hand in front of the head, exactly as before -
        /// the plan's own answer for a shield is "raised in front = guard",
        /// unchanged by TEST 264. The weapon hand's trigger is real blade
        /// contact now (TEST 264, VrPhysicalCombat.TryWeaponBladeContact):
        /// your blade actually touching a nearby attacking enemy's own
        /// TEST-262-fitted blade, not a hand held in a zone - a guard is a
        /// touch, not a pose. Either way the DIRECTION it defends against
        /// comes from the game's own AutoBlockModel while block_auto_direction
        /// is on (TEST 261), not from the hand: the engine's block test is
        /// directional and its left/right are crossed against the attack's, so
        /// a hand-read direction wrote the wrong side for every centered guard
        /// and the sword stopped nothing. See ModelDirection.
        /// </summary>
        private static void GuardPass(Agent agent, BvrInputState input, bool weapon, bool shield,
                                      long now)
        {
            if (_attack != UsageDirection.None)
            {
                _guard = UsageDirection.None;
                _guardPending = UsageDirection.None;
                _guardSince = 0L;
                return;
            }

            // A defend bit does not merely coexist with a swing - the engine
            // treats it as "cancel the attack into a block". TEST 246's log is
            // full of swings that died this way.
            if (EngineAttacking(agent))
            {
                _guard = UsageDirection.None;
                _guardPending = UsageDirection.None;
                _guardSince = 0L;
                _guardLogged = UsageDirection.None;
                return;
            }

            Vec3 shieldLocal = Vec3.Zero;
            Vec3 weaponLocal = Vec3.Zero;
            float shieldSpeed = 0f;
            float weaponSpeed = 0f;

            bool haveShield = shield
                              && Describe(input, VrHandSlots.OffController,
                                          out shieldLocal, out shieldSpeed);
            bool haveWeapon = weapon
                              && Describe(input, VrHandSlots.MainController,
                                          out weaponLocal, out weaponSpeed);

            // TEST 264: the weapon guard's trigger is real blade contact
            // against a nearby attacking enemy's own fitted blade, not a
            // hand-position zone - see VrPhysicalCombat.TryWeaponBladeContact.
            // Gated the same way the old zone test was (slower than a swing),
            // so a hand mid-attack cannot also read as a held guard.
            float bladeGap = 0f;
            float bladeLimit = 0f;
            Agent bladeAgent = null;
            bool weaponBladeContact = haveWeapon && weaponSpeed < SwingSpeed
                && VrPhysicalCombat.TryWeaponBladeContact(agent, out bladeGap, out bladeLimit,
                                                          out bladeAgent);

            UsageDirection guard = UsageDirection.None;
            Vec3 local = Vec3.Zero;
            bool fromModel = false;
            bool fromBlade = false;

            if (haveShield && shieldSpeed < SwingSpeed && Raised(shieldLocal))
            {
                guard = GuardFor(shieldLocal);
                local = shieldLocal;
            }
            else if (weaponBladeContact)
            {
                guard = GuardFor(weaponLocal);
                local = weaponLocal;
                fromBlade = true;
            }
            else if (BlockProbe && now - _blockProbeAt > Seconds(1f)
                     && ((haveShield && shieldLocal.y >= BlockForward && shieldLocal.z < BlockRaise)
                         || (haveShield && shieldSpeed >= SwingSpeed)
                         || bladeAgent != null))
            {
                // Nothing to guard with. For the shield, the hand is somewhere
                // that could have been meant as a guard - in front but too
                // low, or still moving; for the weapon, the closest attacking
                // enemy found was out of blade reach. Say why, once a second:
                // "my block did nothing" is answerable only if the log has the
                // numbers being compared against.
                _blockProbeAt = now;

                if (bladeAgent != null)
                {
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 264 block: no guard - the nearest attacking enemy ('{0}') is "
                        + "{1:F2} m from your blade (limit {2:F2}).",
                        bladeAgent.Name ?? "?", bladeGap, bladeLimit));
                }
                else
                {
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 245 block: no guard - the shield hand is ({0:F2} right, "
                        + "{1:F2} forward, {2:F2} up) of the head at {3:F1} m/s. A guard needs "
                        + "forward >= {4:F2}, up >= {5:F2} and slower than {6:F1} m/s.",
                        shieldLocal.x, shieldLocal.y, shieldLocal.z, shieldSpeed, BlockForward,
                        BlockRaise, SwingSpeed));
                }
            }

            // TEST 261: the direction the engine will actually accept. The hand
            // pose says a guard is up; the model says which way it faces. The
            // probe's no-guard case above is not affected - this only re-aims a
            // guard that already exists.
            if (guard != UsageDirection.None && AutoDirection)
            {
                UsageDirection staged = ModelDirection();
                if (staged != UsageDirection.None)
                {
                    guard = staged;
                    fromModel = true;
                }
            }

            if (guard == UsageDirection.None)
            {
                _guard = UsageDirection.None;
                _guardPending = UsageDirection.None;
                _guardSince = 0L;
                _guardLogged = UsageDirection.None;
                return;
            }

            if (guard != _guardPending)
            {
                _guardPending = guard;
                _guardSince = now;
            }

            float held = (float)((now - _guardSince) / (double)Stopwatch.Frequency);
            UsageDirection before = _guard;
            _guard = BlockHoldS <= 0f || held >= BlockHoldS ? guard : UsageDirection.None;

            if (BlockProbe && (before != _guard
                               || (_guard != UsageDirection.None
                                   && (_guardLogged != _guard
                                       || now - _guardLoggedAt > Seconds(1f)))))
            {
                _guardLogged = _guard;
                _guardLoggedAt = now;

                string mode;
                try
                {
                    mode = agent.CurrentGuardMode.ToString();
                }
                catch (Exception)
                {
                    mode = "?";
                }

                string modelNote = fromModel
                    ? " The direction is the engine's own auto-block answer for the attack that "
                      + "is staged, not the hand."
                    : string.Empty;

                if (fromBlade)
                {
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 264 block: your blade is {0:F2} m from '{1}' 's blade (limit "
                        + "{2:F2}), so the guard asked for is {3} + block (the engine's guard "
                        + "mode reads {4}).{5}",
                        bladeGap, bladeAgent != null ? (bladeAgent.Name ?? "?") : "?", bladeLimit,
                        Name(guard), mode, modelNote));
                }
                else
                {
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 245 block: the shield hand sits ({0:F2} right, {1:F2} forward, "
                        + "{2:F2} up) of the head, so the guard asked for is {3} + block (the "
                        + "engine's guard mode reads {4}). Raised and in front, which is the "
                        + "shield's whole rule.{5}",
                        local.x, local.y, local.z, Name(guard), mode, modelNote));
                }
            }
        }

        /// <summary>A controller's hand relative to the head, in body axes.
        /// False when that controller is not tracking.</summary>
        private static bool Describe(BvrInputState input, VrHand controller, out Vec3 local,
                                     out float speed)
        {
            local = Vec3.Zero;
            speed = 0f;

            BvrHandState hand = controller == VrHand.Left ? input.Left : input.Right;
            if (hand.IsActive == 0)
                return false;

            MatrixFrame world = VrCameraDriver.StageToHand(hand.GripOrientation, hand.GripPosition);
            local = Body(world.origin - VrCameraDriver.HeadFrame.origin);
            speed = _hands[(int)controller].Speed;
            return true;
        }

        /// <summary>Raised and in front, or not a guard at all.</summary>
        private static bool Raised(Vec3 local)
        {
            return local.y >= BlockForward && local.z >= BlockRaise;
        }

        /// <summary>
        /// TEST 246. The engine's attack, at our speed.
        ///
        /// WHILE THE ENGINE IS IN A MELEE ATTACK ACTION - ReadyMelee or
        /// ReleaseMelee on the upper body - its own two speed stats are
        /// multiplied and the live action's playback is set to match. The
        /// moment the action ends, the captured values go back, so a block, a
        /// bow draw or an idle that happens to share the channel is never
        /// sped up.
        ///
        /// Two writes because the engine may read the stat when the action
        /// STARTS (the stat) or from the live action itself (the speed), and
        /// which one it honours is not documented anywhere. The stat is the
        /// engine's own number for exactly this; SetCurrentActionSpeed is the
        /// direct one.
        /// </summary>
        private static void Strike(Agent agent, long now)
        {
            if (StrikeSpeed <= 1f && !_haveBase)
                return;

            AgentDrivenProperties props = agent.AgentDrivenProperties;
            if (props == null)
                return;

            ActionCodeType type;

            try
            {
                type = agent.GetCurrentActionType(1);
            }
            catch (Exception)
            {
                return;
            }

            bool attacking = type == ActionCodeType.ReadyMelee
                             || type == ActionCodeType.ReleaseMelee;

            if (attacking)
            {
                if (!_haveBase)
                {
                    _baseSwing = props.SwingSpeedMultiplier;
                    _baseThrust = props.ThrustOrRangedReadySpeedMultiplier;
                    _haveBase = true;

                    // Once, and again only when the number moves - it is fed by
                    // weapon skill and equipment, and a line per swing would
                    // drown the timeline it sits next to.
                    if (Probe && (!_loggedBase || Math.Abs(_baseSwing - _loggedSwing) > 0.001f))
                    {
                        _loggedBase = true;
                        _loggedSwing = _baseSwing;

                        VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                            "TEST 246: the engine's own swing speed is {0:F2} (thrust/ready {1:F2}); "
                            + "melee_strike_speed = {2:F2} runs the attack at {3:F2} x.",
                            _baseSwing, _baseThrust, StrikeSpeed, _baseSwing * StrikeSpeed));
                    }
                }

                props.SwingSpeedMultiplier = _baseSwing * StrikeSpeed;
                props.ThrustOrRangedReadySpeedMultiplier = _baseThrust * StrikeSpeed;
                agent.UpdateCustomDrivenProperties();

                // Scoped to the attack actions: nothing else on channel 1 is
                // touched, and the write is repeated every mission tick because
                // the action may start between two of them.
                agent.SetCurrentActionSpeed(1, _baseSwing * StrikeSpeed);
            }
            else if (_haveBase)
            {
                props.SwingSpeedMultiplier = _baseSwing;
                props.ThrustOrRangedReadySpeedMultiplier = _baseThrust;
                agent.UpdateCustomDrivenProperties();
                _haveBase = false;
            }

            LogTimeline(agent, now, type);
        }

        /// <summary>
        /// TEST 246. Where the engine is in the swing, and how long the blow
        /// took. The timeline prints on every stage change; the blow line is the
        /// number the request is measured in - the milliseconds from the gesture
        /// to the engine's own damage.
        /// </summary>
        private static void LogTimeline(Agent agent, long now, ActionCodeType type)
        {
            if (!TimelineProbe || _probeUntil == 0L)
                return;

            try
            {
                ActionStage stage = agent.GetCurrentActionStage(1);

                if (type != _lastType || stage != _lastStage)
                {
                    _lastType = type;
                    _lastStage = stage;

                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 246 timeline: +{0:F0} ms after the swing the engine's upper body is "
                        + "{1} ({2}) {3}, progress {4:F2}.",
                        (now - _probeAt) * 1000.0 / Stopwatch.Frequency, type, (int)type, stage,
                        agent.GetCurrentActionProgress(1)));
                }

                if (now >= _probeUntil)
                {
                    if (!_probeHit)
                    {
                        VrLog.Info("TEST 246 timeline: no blow came from that swing within 1200 ms. "
                                   + "If every swing reads like this, the attack may be running too "
                                   + "fast for the collision - lower melee_strike_speed and compare.");
                    }

                    _probeUntil = 0L;
                }
            }
            catch (Exception)
            {
                // A probe that cannot read is a probe that stops.
                _probeUntil = 0L;
            }
        }

        /// <summary>
        /// TEST 246. Called by MeleeBlowPatch for every engine blow at
        /// Mission.RegisterBlow, which is where the engine's own melee damage is
        /// registered. Only the player's own blows matter here, and only the
        /// first after a swing: the latency is what is being measured.
        /// </summary>
        internal static void NoteEngineBlow(Agent attacker, Agent victim, Blow blow)
        {
            if (!Enabled || !TimelineProbe || _probeUntil == 0L || _probeHit)
                return;

            try
            {
                Agent main = Mission.Current?.MainAgent;
                if (main == null || attacker != main)
                    return;

                _probeHit = true;

                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 246: the engine's own blow landed +{0:F0} ms after the swing - {1} damage, "
                    + "{2}, on '{3}'.",
                    (Stopwatch.GetTimestamp() - _probeAt) * 1000.0 / Stopwatch.Frequency,
                    blow.InflictedDamage, blow.StrikeType, victim?.Name ?? "?"));
            }
            catch (Exception)
            {
                // Diagnostics only; the blow itself must not care.
                _probeUntil = 0L;
            }
        }

        /// <summary>Where the hand is decides which attack it can stop. Up is
        /// the guard against an overhead, the sides against the side slashes,
        /// and low-middle against the thrust - the same four the engine has.
        ///
        /// A SIDE IS CHECKED FIRST, and that is the whole reason this is
        /// ordered rather than scored: the guard poses are named by PLACE
        /// ("middle right", "middle up"), and a height-first test reads every
        /// hand held above the chin as "up" - including a sword held out to the
        /// right at head height, which is a right guard.</summary>
        private static UsageDirection GuardFor(Vec3 local)
        {
            if (local.x > BlockSide)
                return UsageDirection.DefendRight;

            if (local.x < -BlockSide)
                return UsageDirection.DefendLeft;

            return local.z > BlockUp ? UsageDirection.DefendUp : UsageDirection.DefendDown;
        }

        /// <summary>
        /// One mission tick's bits, and the engine-response probe. The attack
        /// holds its bit for ReadyTicks and is then released; a guard holds
        /// DefendBlock as long as the pose does.
        /// </summary>
        private static MovementControlFlag Next(Agent agent)
        {
            MovementControlFlag flags = MovementControlFlag.None;

            if (_responseIn > 0 && --_responseIn == 0)
                LogResponse(agent);

            if (_attack != UsageDirection.None)
            {
                if (_attackReads < ReadyTicks)
                {
                    _attackReads++;
                    if (EngineSwing)
                        flags |= AttackFlag(_attack);
                }
                else
                {
                    _attack = UsageDirection.None;
                }
            }

            if ((flags & MovementControlFlag.AttackMask) == MovementControlFlag.None
                && _guard != UsageDirection.None
                && !EngineAttacking(agent))
            {
                flags |= GuardFlag(_guard) | MovementControlFlag.DefendBlock;
            }

            return flags;
        }

        /// <summary>
        /// Whether the engine is mid-attack on the upper body. The guard is held
        /// off while it is, and this is the fix for TEST 246's "swing attack not
        /// working": a defend bit during a swing CANCELS the swing - that is how
        /// the engine lets an attack be aborted into a block - and after a swing
        /// the hand is usually still in a pose the guard table calls a guard.
        /// Every swing in that log aborted itself a few ticks in:
        ///
        ///     +16 ms  AttackMeleeAllBegin (19) AttackReady
        ///     +258 ms DefendUp1h (8) Defend
        ///     +375 ms StrikeBegin (48)            <- no blow, ever
        ///
        /// So the guard waits for the attack to finish. Nothing is lost at the
        /// other end: an attack gesture still overrides a guard, because the
        /// attack bits win by construction.
        ///
        /// Also read by VrCameraDriver (TEST 249), which drops the animation's
        /// own lean out of the camera anchor for exactly these actions - and by
        /// VrBodyFreeze and VrUpperBody for the same reason. ALL of those are the
        /// gesture melee's behaviour, so this answers false when the panel's
        /// switch is off: turning the feature off has to turn off everything that
        /// hangs off it, or "off" would still freeze the body and hold the camera
        /// through every trigger attack.
        /// </summary>
        internal static bool EngineAttacking(Agent agent)
        {
            if (!Enabled)
                return false;

            try
            {
                switch (agent.GetCurrentActionType(1))
                {
                    case ActionCodeType.ReadyMelee:
                    case ActionCodeType.ReleaseMelee:
                    case ActionCodeType.ParriedMelee:
                    case ActionCodeType.BlockedMelee:
                    case ActionCodeType.StrikeLight:
                    case ActionCodeType.StrikeMedium:
                    case ActionCodeType.StrikeHeavy:
                    case ActionCodeType.StrikeKnockBack:
                    case ActionCodeType.MountStrike:
                        return true;
                    default:
                        return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// TEST 266. EngineAttacking plus the engine's own DEFEND actions - the
        /// animation a guard bit (this class's, or TEST 264's blade-contact one)
        /// now plays. Body freeze, the chest hold and the camera anchor hold
        /// through a block exactly as they do through a swing; EngineAttacking
        /// itself is unchanged and keeps guarding only the swing-cancels-into-a-
        /// block decision at GuardPass and Next, which must not widen - a block
        /// held through a DEFEND animation that is not mid-swing would just be
        /// this same test lying to itself.
        /// </summary>
        internal static bool EngineActing(Agent agent)
        {
            if (EngineAttacking(agent))
                return true;

            if (!Enabled)
                return false;

            try
            {
                ActionCodeType type = agent.GetCurrentActionType(1);
                return type >= ActionCodeType.DefendAllBegin && type < ActionCodeType.DefendAllEnd;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>What the engine actually did about the attack that was
        /// asked for. THE line to read: if the direction here is not the one in
        /// the swing line above it, melee_swing_map is permuted, not the code.</summary>
        private static void LogResponse(Agent agent)
        {
            if (!Probe || agent == null || _attackAsked == UsageDirection.None)
                return;

            try
            {
                Agent.ActionCodeType type = agent.GetCurrentActionType(1);

                // The numbers are in the line as well as the names: this enum
                // carries aliases (AttackMeleeAllBegin == ReadyMelee), and a
                // ToString that picks the alias is exactly how a log says the
                // wrong thing convincingly.
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 245 swing result: asked for {0}; the engine's upper body is {1} ({2}), "
                    + "direction {3}, stage {4}.",
                    Name(_attackAsked), type, (int)type,
                    Name(agent.GetCurrentActionDirection(1)), agent.GetCurrentActionStage(1)));
            }
            catch (Exception)
            {
                // A read that fails is a missing log line, not a lost attack.
            }
        }

        /// <summary>
        /// The direction to guard with, from the game's OWN AutoBlockModel - the
        /// component vanilla's "Block: Auto" option uses. It scans the enemies
        /// whose action stage is an attack ready/release, picks the one most
        /// plausibly coming for the player, and returns that attack's direction
        /// (AttackDown when nothing is staged). Its answer is in ATTACK
        /// directions, which GuardFlag maps to the defend flags with the same
        /// crossing the engine's own controller uses.
        ///
        /// Only read while a guard is held; a missing model or a read that
        /// throws falls back to the hand mapping, and never takes the guard
        /// down.
        /// </summary>
        private static UsageDirection ModelDirection()
        {
            try
            {
                Mission mission = Mission.Current;
                if (mission == null)
                    return UsageDirection.None;

                MissionGameModels models = MissionGameModels.Current;
                var model = models != null ? models.AutoBlockModel : null;
                if (model == null)
                    return UsageDirection.None;

                return model.GetBlockDirection(mission);
            }
            catch (Exception)
            {
                return UsageDirection.None;
            }
        }

        private static void LogOn()
        {
            _loggedOn = true;

            VrLog.Info("TEST 245: motion melee is on. A swing writes the game's own attack "
                       + "direction (" + MapText() + "); a raised hand in front writes the "
                       + "game's own guard plus DefendBlock. The engine swings, blocks and "
                       + "reacts - the mod writes no guard test of its own. melee = 0 gives "
                       + "melee back to the trigger.");

            if (InstantStrike)
            {
                VrLog.Info("TEST 248: the blade lands the blow itself, at the swing, because "
                           + "the engine's own strike takes ~0.43 s and misses most swings "
                           + "(TEST 246/247 measured it). The engine's attack still runs - the "
                           + "AI sees it and defends - and its duplicate blow on a victim the "
                           + "blade already struck is dropped. melee_instant_hit = 0 puts the "
                           + "damage back on the engine's animation timing.");
            }

            if (VrPhysicalCombat.Enabled)
            {
                VrLog.Info("TEST 245: physical collision combat is held off while motion melee "
                           + "is on (combat = 1 is ignored). melee = 0 gives it back.");
            }

            if (!VrMotionHands.Enabled)
            {
                VrLog.Warn("TEST 245: motion_hands is off, so the game's own swing animations "
                           + "will move the arms. The attacks still fire; turn motion hands on "
                           + "for the controller to be the arm.");
            }
        }

        private static void Clear()
        {
            _attack = UsageDirection.None;
            _attackReads = 0;
            _responseIn = -1;
            _guard = UsageDirection.None;
            _guardPending = UsageDirection.None;
            _guardSince = 0L;
            _guardLogged = UsageDirection.None;

            // TEST 261: anything the AI nudge raised goes back to the engine's
            // own numbers the moment gestures stand down.
            VrEnemyGuard.Clear();
        }

        // --- small shared pieces ---------------------------------------------

        /// <summary>A world-space vector in the body's own axes (right, forward,
        /// up). The anchor is a yaw frame, so turning to look somewhere does not
        /// change what "left" means.</summary>
        private static Vec3 Body(Vec3 world)
        {
            return VrCameraDriver.Anchor.rotation.TransformToLocal(world);
        }

        private static float SmoothAlpha(float dt)
        {
            if (SmoothMs <= 0f)
                return 1f;

            float tau = SmoothMs * 0.001f;
            return dt / (tau + dt);
        }

        private static WeaponComponentData Held(MissionWeapon weapon)
        {
            return weapon.IsEmpty ? null : weapon.CurrentUsageItem;
        }

        private static long Seconds(float seconds)
        {
            return (long)(seconds * Stopwatch.Frequency);
        }

        private static MovementControlFlag AttackFlag(UsageDirection dir)
        {
            switch (dir)
            {
                case UsageDirection.AttackLeft: return MovementControlFlag.AttackLeft;
                case UsageDirection.AttackRight: return MovementControlFlag.AttackRight;
                case UsageDirection.AttackUp: return MovementControlFlag.AttackUp;
                case UsageDirection.AttackDown: return MovementControlFlag.AttackDown;
                default: return MovementControlFlag.None;
            }
        }

        private static MovementControlFlag GuardFlag(UsageDirection dir)
        {
            switch (dir)
            {
                // The hand-place fallback (block_auto_direction = 0). The side
                // flags are the side the HAND is on.
                case UsageDirection.DefendLeft: return MovementControlFlag.DefendLeft;
                case UsageDirection.DefendRight: return MovementControlFlag.DefendRight;
                case UsageDirection.DefendUp: return MovementControlFlag.DefendUp;
                case UsageDirection.DefendDown: return MovementControlFlag.DefendDown;

                // The engine's AutoBlockModel answers in ATTACK directions, and
                // the engine's own controller maps them like this (decompiled
                // from MissionMainAgentController.ControlTick): an attack from
                // your left is guarded with DefendRight and vice versa; up and
                // down map straight through. Reading the answer without this
                // crossing is a guard written on the wrong side.
                case UsageDirection.AttackUp: return MovementControlFlag.DefendUp;
                case UsageDirection.AttackDown: return MovementControlFlag.DefendDown;
                case UsageDirection.AttackLeft: return MovementControlFlag.DefendRight;
                case UsageDirection.AttackRight: return MovementControlFlag.DefendLeft;
                default: return MovementControlFlag.None;
            }
        }

        private static UsageDirection[] ParseMap(string text)
        {
            var map = new[]
            {
                UsageDirection.AttackLeft,
                UsageDirection.AttackRight,
                UsageDirection.AttackUp,
                UsageDirection.AttackDown,
            };

            string[] parts = (text ?? string.Empty).Split(',');

            for (int i = 0; i < map.Length; i++)
            {
                if (i >= parts.Length)
                    break;

                string entry = parts[i].Trim().ToLowerInvariant();

                switch (entry)
                {
                    case "left": map[i] = UsageDirection.AttackLeft; break;
                    case "right": map[i] = UsageDirection.AttackRight; break;
                    case "up": map[i] = UsageDirection.AttackUp; break;
                    case "down": map[i] = UsageDirection.AttackDown; break;
                    default:
                        VrLog.Warn("Motion melee: melee_swing_map entry " + (i + 1) + " is '"
                                   + entry + "', not left/right/up/down; keeping "
                                   + Name(map[i]) + ".");
                        break;
                }
            }

            return map;
        }

        private static string MapText()
        {
            return "hand left->" + Name(SwingMap[0])
                   + ", hand right->" + Name(SwingMap[1])
                   + ", vertical->" + Name(SwingMap[2])
                   + ", forward->" + Name(SwingMap[3]);
        }

        private static string GestureName(int gesture)
        {
            switch (gesture)
            {
                case 0: return "hand moved left";
                case 1: return "hand moved right";
                case 2: return "hand moved vertically";
                default: return "hand moved forward";
            }
        }

        private static string Name(UsageDirection dir)
        {
            switch (dir)
            {
                case UsageDirection.AttackUp: return "attack up (overhead)";
                case UsageDirection.AttackDown: return "attack down (thrust)";
                case UsageDirection.AttackLeft: return "attack left";
                case UsageDirection.AttackRight: return "attack right";
                case UsageDirection.DefendUp: return "defend up";
                case UsageDirection.DefendDown: return "defend down (stab guard)";
                case UsageDirection.DefendLeft: return "defend left";
                case UsageDirection.DefendRight: return "defend right";
                default: return "none";
            }
        }
    }
}
