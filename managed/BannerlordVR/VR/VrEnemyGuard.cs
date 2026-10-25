using System;
using System.Diagnostics;
using System.Globalization;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

// The engine's combat types are nested in Agent, like VrMelee's aliases.
using UsageDirection = TaleWorlds.MountAndBlade.Agent.UsageDirection;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 261. The enemy's side of a physical swing: does the engine's own AI
    /// see the attack and raise a guard?
    ///
    /// WHY THIS EXISTS. A physical swing is still an engine attack walk
    /// (VrMelee writes the game's own attack direction), so the AI *can* react -
    /// but at melee_ready_ticks = 1 the engine's ReadyMelee was over in ~33 ms,
    /// shorter than the AI's own check interval, and every report of "enemies
    /// don't block my swings" was consistent with an attack the AI never
    /// sampled. Nothing in the log could tell "the AI chose not to block" from
    /// "the AI never saw the swing", which is the whole tuning question - so
    /// this class answers it in two halves:
    ///
    ///   * THE PROBE (melee_ai_probe): per swing, the nearest few enemies that
    ///     are in reach and facing the swing are watched for melee_ai_window;
    ///     every defend action is logged with its latency, and a summary line
    ///     says how many reacted. That is the number the next decision is made
    ///     from.
    ///
    ///   * THE NUDGE (melee_ai_block): the engine decides a block from the
    ///     defender's own AgentDrivenProperties - AIBlockOnDecideAbility,
    ///     AIDecideOnRealizeEnemyBlockingAttackAbility, AIParryOnDecideAbility
    ///     and the two shield/direction chances. For an enemy that could see
    ///     the swing, those five are raised to melee_ai_target for the window
    ///     and restored exactly afterwards. The AI still does the deciding and
    ///     plays its own guard; the mod only turns up the engine's own dial.
    ///
    /// Nothing here touches the swing, the damage or the block test: the guard
    /// the enemy raises stops the blade through the normal GuardStops path, and
    /// an engine block of the engine's own swing is the engine's business.
    ///
    /// TEST 314: with melee_engine_swing = 0 there is no attack for the engine's
    /// AI to read, and TEST 272's answer - a guard forced on the gesture, its
    /// side read off the controller, at a boosted 0.9 - blocked nearly every
    /// gesture whatever was coming. Track replaces it: every tick, each enemy near
    /// the blade looks ahead along the blade's own motion (the hand's velocity and
    /// the blade's turn) for where it will land on THAT enemy's body, guards the
    /// side it will land on, after its own reaction time, if its own block stat
    /// says so - once per attack - and re-aims when the blade changes course. A
    /// feint or a late turn beats it by timing, as it should.
    /// </summary>
    internal static class VrEnemyGuard
    {
        /// <summary>Raise the engine's defend chances while a swing is coming.
        /// melee_ai_block = 0 leaves the AI's own numbers alone.</summary>
        private static readonly bool Block = VrConfig.Bool("melee_ai_block", true);

        /// <summary>Logs the enemy reactions to every swing.</summary>
        private static readonly bool Probe = VrConfig.Bool("melee_ai_probe", true);

        /// <summary>How close an enemy must be for the swing to be its
        /// business, in metres.</summary>
        private static readonly float Reach = Math.Max(0.5f, VrConfig.Float("melee_ai_reach", 2.5f));

        /// <summary>How far off the line between the two may an enemy's look
        /// direction be and still count as facing the swing, in degrees. An
        /// enemy staring somewhere else is not going to block regardless of
        /// what the dials say.</summary>
        private static readonly float ArcCos = (float)Math.Cos(
            Math.Max(5f, Math.Min(180f, VrConfig.Float("melee_ai_arc", 60f))) * Math.PI / 180.0);

        /// <summary>How long the raised chances last, in milliseconds. Covers
        /// the AI's check interval plus the swing itself.</summary>
        private static readonly float WindowS =
            Math.Max(0.05f, VrConfig.Float("melee_ai_window_ms", 400f) * 0.001f);

        /// <summary>The chance each defend stat is raised TO; the engine's own
        /// value is kept when it is already higher.</summary>
        private static readonly float Target =
            Math.Max(0f, Math.Min(1f, VrConfig.Float("melee_ai_target", 0.9f)));

        /// <summary>How long the probe watches after a swing.</summary>
        private const float WatchS = 0.8f;

        private const int Watched = 3;
        private const int NudgeSlots = 6;

        private sealed class Nudge
        {
            public bool Live;
            public int Index;
            public long Until;

            /// <summary>The mission the bases were taken in. A mission change
            /// must not write old numbers onto a fresh agent that happens to
            /// reuse the index.</summary>
            public Mission Mission;
            public float Realize;
            public float BlockOnDecide;
            public float Parry;
            public float Shield;
            public float Random;
        }

        private static readonly Nudge[] _nudges = new Nudge[NudgeSlots];

        private sealed class Watch
        {
            public bool Live;
            public long At;
            public long Until;
            public int Count;
            public readonly int[] Index = new int[Watched];
            public readonly int[] Seen = new int[Watched];
            public readonly bool[] Reacted = new bool[Watched];
        }

        private static readonly Watch _watch = new Watch();

        // --- TEST 314: the blade, as the enemy sees it coming ------------------

        /// <summary>melee_ai_track = 0 leaves the enemies to the engine alone.</summary>
        private static readonly bool TrackOn = VrConfig.Bool("melee_ai_track", true);

        /// <summary>How far ahead an enemy reads the blade's motion, in seconds.</summary>
        private static readonly float HorizonS = Math.Max(0.05f, VrConfig.Float("melee_ai_horizon_ms", 300f) * 0.001f);

        /// <summary>How long a new threat must be seen before the enemy acts on
        /// it, in seconds - jittered by a quarter either way per attack.</summary>
        private static readonly float ReactS = Math.Max(0f, VrConfig.Float("melee_ai_react_ms", 200f) * 0.001f);

        /// <summary>How long a guarding enemy takes to move its guard to a new
        /// side once the blade has changed course.</summary>
        private static readonly float ReaimS = Math.Max(0f, VrConfig.Float("melee_ai_reaim_ms", 150f) * 0.001f);

        /// <summary>How long a guard stays up after the blade stops threatening.</summary>
        private static readonly float HoldS = Math.Max(0.05f, VrConfig.Float("melee_ai_hold_ms", 300f) * 0.001f);

        /// <summary>Times the enemy's own block (shield) or parry (weapon) stat. 1 =
        /// its own skill decides.</summary>
        private static readonly float BlockScale = Math.Max(0f, VrConfig.Float("melee_ai_block_scale", 1f));

        /// <summary>Swaps the side the guard is raised on, should the log show the
        /// engine names DefendLeft/Right the other way round.</summary>
        private static readonly bool SideFlip = VrConfig.Bool("melee_ai_side_flip", false);

        /// <summary>The touching point must close faster than this (m/s) to be an
        /// attack: a blade held still beside someone is not one.</summary>
        private static readonly float ThreatSpeed = Math.Max(0f, VrConfig.Float("melee_ai_threat_speed", 2.0f));

        /// <summary>Above the chest bone by more than this, the guard goes up.</summary>
        private const float HighM = 0.30f;

        private const float StepS = 0.05f;
        private const float Margin = 0.05f;
        private const float ThrustDot = 0.7f;
        private const int Threats = 6;

        /// <summary>One enemy's view of one attack: when it saw it, whether it
        /// rolled, and the guard it holds. The guard itself is the engine's own -
        /// EnforceShieldUsage for a shield (the shield wall's call), SetWeaponGuard
        /// otherwise - a real defend action, so the physical shield and
        /// GuardStops judge the blade against it.</summary>
        private sealed class Threat
        {
            public bool Live;
            public int Index;
            public Mission Mission;
            public long FirstSeen;
            public long LastSeen;
            public long PendingSince;
            public float React;
            public bool Rolled;
            public bool Guarding;
            public bool Shield;
            public UsageDirection Dir;
            public UsageDirection Pending;
            public UsageDirection Seen;
        }

        private static readonly Threat[] _threats = new Threat[Threats];
        private static bool _loggedTrack;

        private static bool _loggedBase;
        private static bool _loggedNudge;

        /// <summary>
        /// Called by VrMelee the moment a swing fires - the same instant the
        /// engine attack is asked for. Picks the enemies the swing can concern,
        /// starts the probe clock and raises their defend chances.
        /// </summary>
        internal static void OnSwing(Agent attacker, float speed, UsageDirection direction)
        {
            // TEST 314: with the engine's swing off there is no attack for the
            // engine's AI to read, and Track plays the enemy's side from the blade
            // itself - nothing is raised here.
            if (attacker == null || !VrMelee.EngineSwing || (!Block && !Probe))
                return;

            try
            {
                Mission mission = Mission.Current;
                if (mission == null)
                    return;

                long now = Stopwatch.GetTimestamp();
                _watch.Live = false;
                _watch.Count = 0;

                // The nearest few enemies within reach that are actually
                // looking at the swing. A guard from behind is not a reaction
                // to anything.
                var best = new Agent[Watched];
                var bestSquared = new float[Watched];

                for (int i = 0; i < Watched; i++)
                    bestSquared[i] = float.MaxValue;

                foreach (Agent candidate in mission.Agents)
                {
                    if (candidate == null || candidate == attacker || !candidate.IsActive()
                        || !candidate.IsHuman || !attacker.IsEnemyOf(candidate))
                        continue;

                    Vec3 offset = candidate.Position - attacker.Position;
                    var flat = new Vec3(offset.x, offset.y, 0f, -1f);
                    float squared = flat.LengthSquared;

                    if (squared > Reach * Reach || squared < 1e-6f)
                        continue;

                    if (!Facing(attacker.LookDirection, flat))
                        continue;

                    if (!Facing(candidate.LookDirection, flat * -1f))
                        continue;

                    for (int i = 0; i < Watched; i++)
                    {
                        if (squared >= bestSquared[i])
                            continue;

                        for (int j = Watched - 1; j > i; j--)
                        {
                            best[j] = best[j - 1];
                            bestSquared[j] = bestSquared[j - 1];
                        }

                        best[i] = candidate;
                        bestSquared[i] = squared;
                        break;
                    }
                }

                for (int i = 0; i < Watched; i++)
                {
                    if (best[i] == null)
                        break;

                    _watch.Index[i] = best[i].Index;
                    _watch.Seen[i] = (int)best[i].GetCurrentActionType(1);
                    _watch.Reacted[i] = false;
                    _watch.Count++;
                }

                if (_watch.Count == 0)
                    return;

                _watch.Live = true;
                _watch.At = now;
                _watch.Until = now + Seconds(WatchS);

                for (int i = 0; i < _watch.Count; i++)
                    Raise(best[i], now);

                if (Probe)
                {
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 261 AI: the swing at {0:F1} m/s has {1} enemy(s) within {2:F1} m "
                        + "looking at it; watching them for {3:F0} ms.",
                        speed, _watch.Count, Reach, WatchS * 1000f));
                }
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 261: the enemy-guard pass failed once; the swing itself is "
                            + "unaffected.", ex);
            }
        }

        /// <summary>
        /// One frame: restore anything the nudge raised once its window is over,
        /// and report the defend actions the watched enemies took.
        /// </summary>
        internal static void Tick(Agent agent)
        {
            try
            {
                long now = Stopwatch.GetTimestamp();

                for (int i = 0; i < NudgeSlots; i++)
                {
                    Nudge nudge = _nudges[i];
                    if (nudge == null || !nudge.Live || now < nudge.Until)
                        continue;

                    Restore(i, nudge);
                }

                if (!_watch.Live)
                    return;

                if (now >= _watch.Until)
                {
                    Report();
                    return;
                }

                if (!Probe)
                    return;

                Mission mission = Mission.Current;
                if (mission == null)
                {
                    _watch.Live = false;
                    return;
                }

                for (int i = 0; i < _watch.Count; i++)
                {
                    if (_watch.Reacted[i])
                        continue;

                    Agent enemy = mission.FindAgentWithIndex(_watch.Index[i]);
                    if (enemy == null || !enemy.IsActive())
                        continue;

                    int type = (int)enemy.GetCurrentActionType(1);
                    if (type == _watch.Seen[i] || !Guarding(type))
                        continue;

                    _watch.Seen[i] = type;
                    _watch.Reacted[i] = true;

                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 261 AI: '{0}' [{1}] raised a guard +{2:F0} ms after the swing - "
                        + "{3} ({4}).",
                        enemy.Name ?? "?", enemy.Index,
                        (now - _watch.At) * 1000.0 / Stopwatch.Frequency,
                        (Agent.ActionCodeType)type, type));
                }
            }
            catch (Exception)
            {
                // Diagnostics never take a swing down.
                _watch.Live = false;
            }
        }

        /// <summary>The nudge window is over: put the engine's own numbers
        /// back, exactly, and let the log say one line per session.</summary>
        private static void Restore(int slot, Nudge nudge)
        {
            nudge.Live = false;

            try
            {
                Mission mission = Mission.Current;

                // A different mission means those numbers belong to a body that
                // no longer exists - or worse, to a fresh agent reusing the
                // index. Drop them rather than write them onto a stranger.
                if (mission == null || mission != nudge.Mission)
                    return;

                Agent enemy = mission.FindAgentWithIndex(nudge.Index);
                if (enemy == null || !enemy.IsActive())
                    return;

                AgentDrivenProperties props = enemy.AgentDrivenProperties;
                if (props == null)
                    return;

                props.AIDecideOnRealizeEnemyBlockingAttackAbility = nudge.Realize;
                props.AIBlockOnDecideAbility = nudge.BlockOnDecide;
                props.AIParryOnDecideAbility = nudge.Parry;
                props.AiDefendWithShieldDecisionChanceValue = nudge.Shield;
                props.AiRandomizedDefendDirectionChance = nudge.Random;
                enemy.UpdateCustomDrivenProperties();

                if (!_loggedNudge)
                {
                    _loggedNudge = true;
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 261 AI: the engine's own defend stats are back (realize {0:F2}, "
                        + "block {1:F2}, parry {2:F2}, shield {3:F2}, direction {4:F2}).",
                        nudge.Realize, nudge.BlockOnDecide, nudge.Parry, nudge.Shield,
                        nudge.Random));
                }
            }
            catch (Exception)
            {
                // A stat that cannot be written back is left to the engine.
            }
        }

        /// <summary>Raise one enemy's own defend chances for the window. An
        /// enemy already raised is not re-snapshotted - its bases must stay the
        /// engine's originals, not a boosted value - it is only held longer.</summary>
        private static void Raise(Agent enemy, long now)
        {
            try
            {
                for (int i = 0; i < NudgeSlots; i++)
                {
                    Nudge live = _nudges[i];
                    if (live != null && live.Live && live.Index == enemy.Index)
                    {
                        live.Until = now + Seconds(WindowS);
                        return;
                    }
                }

                if (!Block)
                    return;

                AgentDrivenProperties props = enemy.AgentDrivenProperties;
                if (props == null)
                    return;

                for (int i = 0; i < NudgeSlots; i++)
                {
                    if (_nudges[i] != null && _nudges[i].Live)
                        continue;

                    var nudge = _nudges[i] ?? new Nudge();
                    _nudges[i] = nudge;

                    nudge.Live = true;
                    nudge.Index = enemy.Index;
                    nudge.Mission = Mission.Current;
                    nudge.Until = now + Seconds(WindowS);
                    nudge.Realize = props.AIDecideOnRealizeEnemyBlockingAttackAbility;
                    nudge.BlockOnDecide = props.AIBlockOnDecideAbility;
                    nudge.Parry = props.AIParryOnDecideAbility;
                    nudge.Shield = props.AiDefendWithShieldDecisionChanceValue;
                    nudge.Random = props.AiRandomizedDefendDirectionChance;

                    Boost(props);

                    if (!_loggedBase)
                    {
                        _loggedBase = true;
                        VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                            "TEST 261 AI: the engine's own defend stats are realize {0:F2}, "
                            + "block {1:F2}, parry {2:F2}, shield {3:F2}, direction {4:F2}; "
                            + "while a swing is coming they are raised to {5:F2} for {6:F0} ms.",
                            nudge.Realize, nudge.BlockOnDecide, nudge.Parry, nudge.Shield,
                            nudge.Random, Target, WindowS * 1000f));
                    }

                    return;
                }
            }
            catch (Exception)
            {
                // An agent whose stats cannot be read keeps the engine's own.
            }
        }

        private static void Boost(AgentDrivenProperties props)
        {
            props.AIDecideOnRealizeEnemyBlockingAttackAbility =
                Math.Max(props.AIDecideOnRealizeEnemyBlockingAttackAbility, Target);
            props.AIBlockOnDecideAbility = Math.Max(props.AIBlockOnDecideAbility, Target);
            props.AIParryOnDecideAbility = Math.Max(props.AIParryOnDecideAbility, Target);
            props.AiDefendWithShieldDecisionChanceValue =
                Math.Max(props.AiDefendWithShieldDecisionChanceValue, Target);
            props.AiRandomizedDefendDirectionChance =
                Math.Max(props.AiRandomizedDefendDirectionChance, Target);
        }

        /// <summary>The one line the tuning session reads: how many of the
        /// enemies the swing concerned actually raised a guard.</summary>
        private static void Report()
        {
            _watch.Live = false;

            if (!Probe || _watch.Count == 0)
                return;

            int reacted = 0;
            for (int i = 0; i < _watch.Count; i++)
            {
                if (_watch.Reacted[i])
                    reacted++;
            }

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 261 AI: {0} of {1} enemies within reach raised a guard within {2:F0} ms "
                + "of the swing. {3}",
                reacted, _watch.Count, WatchS * 1000f,
                reacted > 0
                    ? "The AI sees the swing; melee_ai_target tunes how often it commits."
                    : "The AI did not react to that one; check melee_ready_ticks against the "
                      + "TEST 246 timeline, or raise melee_ai_reach/melee_ai_arc."));
        }

        /// <summary>Restore every raised stat now - used when gestures stand
        /// down, the panel switch goes off, or the mission changes.</summary>
        internal static void Clear()
        {
            _watch.Live = false;

            for (int i = 0; i < NudgeSlots; i++)
            {
                Nudge nudge = _nudges[i];
                if (nudge != null && nudge.Live)
                    Restore(i, nudge);
            }

            for (int i = 0; i < Threats; i++)
                End(_threats[i]);
        }

        /// <summary>The window is over: hand the guard back to the AI. A
        /// shield goes back to whatever its formation asks for.</summary>
        private static void Lower(Threat g)
        {
            if (g == null || !g.Guarding)
                return;

            g.Guarding = false;

            try
            {
                Mission mission = Mission.Current;
                if (mission == null || mission != g.Mission)
                    return;

                Agent enemy = mission.FindAgentWithIndex(g.Index);
                if (enemy == null || !enemy.IsActive())
                    return;

                if (g.Shield)
                {
                    enemy.EnforceShieldUsage(UsageDirection.None);
                    enemy.UpdateFormationOrders();
                }
                else
                {
                    enemy.ResetGuard();
                }
            }
            catch (Exception)
            {
                // Left to the AI's own next decision.
            }
        }

        /// <summary>
        /// TEST 314, once a tick after the blades are read: every enemy near a
        /// blade looks ahead along its motion, and a threat it has watched long
        /// enough is guarded - or let through - on its own skill.
        /// </summary>
        internal static void Track(Agent player)
        {
            if (VrMelee.EngineSwing || !Block || !TrackOn)
                return;

            long now = Stopwatch.GetTimestamp();

            try
            {
                Mission mission = Mission.Current;
                if (player != null && mission != null)
                {
                    if (!_loggedTrack)
                    {
                        _loggedTrack = true;
                        VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                            "TEST 314 AI: enemies read your blade's own motion {0:F0} ms ahead, react after ~{1:F0} ms, "
                            + "roll their own block/parry skill once per attack (x{2:F2}) and re-aim in {3:F0} ms.",
                            HorizonS * 1000f, ReactS * 1000f, BlockScale, ReaimS * 1000f));
                    }

                    for (int role = 0; role < 2; role++)
                    {
                        if (VrPhysicalCombat.TryMotion(role, out VrBlade.Segment blade, out Vec3 hand,
                                                       out Vec3 velocity, out Vec3 omega, out bool canThrust))
                            Scan(player, mission, blade, hand, velocity, omega, canThrust, now);
                    }
                }

                for (int i = 0; i < Threats; i++)
                {
                    Threat t = _threats[i];
                    if (t != null && t.Live && now - t.LastSeen >= Seconds(HoldS))
                        End(t);
                }
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 314: the enemy look-ahead failed once; their guards are lowered.", ex);
                for (int i = 0; i < Threats; i++)
                    End(_threats[i]);
            }
        }

        /// <summary>One blade against every enemy it could reach soon.</summary>
        private static void Scan(Agent player, Mission mission, VrBlade.Segment blade, Vec3 hand, Vec3 velocity,
                                 Vec3 omega, bool canThrust, long now)
        {
            Vec3 grip = blade.GripWorld - hand;
            Vec3 tip = blade.TipWorld - hand;
            Vec3 axis = blade.AxisWorld;
            float reach = Math.Max(Reach, blade.Length + 1.2f);

            foreach (Agent enemy in mission.Agents)
            {
                // TEST 421: distance before the native IsEnemyOf call.
                if (enemy == null || enemy == player || !enemy.IsHuman || !enemy.IsActive()
                    || (enemy.Position.AsVec2 - player.Position.AsVec2).LengthSquared > reach * reach
                    || !player.IsEnemyOf(enemy))
                    continue;

                // Mid-attack is committed to it.
                Agent.ActionCodeType type = enemy.GetCurrentActionType(1);
                if (type == Agent.ActionCodeType.ReadyMelee || type == Agent.ActionCodeType.ReleaseMelee)
                    continue;

                VrPhysicalCombat.Body body = VrPhysicalCombat.BodyOf(enemy);
                if (body == null)
                    continue;

                Vec3 relative = velocity - VrPhysicalCombat.Carried(enemy);

                if (!Ahead(body, hand, grip, tip, relative, omega, out float tau, out Vec3 point,
                           out BoneBodyPartType part))
                    continue;

                // How fast the touching point closes, at the moment it would touch.
                Vec3 lever = point - (hand + relative * tau);
                Vec3 closing = relative + Vec3.CrossProduct(omega, lever);
                float speed = closing.Length;
                if (speed < ThreatSpeed)
                    continue;

                // It must see it coming.
                Vec3 toPoint = point - enemy.Position;
                if (!Facing(enemy.LookDirection, new Vec3(toPoint.x, toPoint.y, 0f, -1f)))
                    continue;

                bool stab = canThrust && Vec3.DotProduct(closing, axis) > ThrustDot * speed;
                Vec3 local = Local(enemy, point);
                bool head = part == BoneBodyPartType.Head || part == BoneBodyPartType.Neck;

                Note(enemy, mission, GuardFor(local, stab, head, SideFlip), part, tau, stab, now);
            }
        }

        /// <summary>
        /// The blade's motion run ahead in StepS steps to HorizonS: the hand by
        /// its velocity against the body, the blade by its turn about the hand.
        /// The first pose within reach of a body capsule is where it lands.
        /// </summary>
        private static bool Ahead(VrPhysicalCombat.Body body, Vec3 hand, Vec3 grip, Vec3 tip, Vec3 velocity,
                                  Vec3 omega, out float tau, out Vec3 point, out BoneBodyPartType part)
        {
            tau = 0f;
            point = Vec3.Zero;
            part = BoneBodyPartType.None;

            for (float t = 0f; t <= HorizonS + 1e-4f; t += StepS)
            {
                Vec3 at = hand + velocity * t;
                Vec3 spin = omega * t;
                Vec3 g = at + Spin(grip, spin);
                Vec3 e = at + Spin(tip, spin);

                for (int c = 0; c < body.Count; c++)
                {
                    VrPhysicalCombat.Capsule capsule = body.Capsules[c];
                    float d = VrPhysicalCombat.SegmentDistance(g, e, capsule.A, capsule.B, out float a, out float _);
                    if (d > capsule.Radius + Margin)
                        continue;

                    tau = t;
                    point = g + (e - g) * a;
                    part = capsule.Part;
                    return true;
                }
            }

            return false;
        }

        /// <summary>A point in the enemy's own frame from its chest: x right,
        /// y forward (its look, flat), z up.</summary>
        private static Vec3 Local(Agent enemy, Vec3 point)
        {
            Vec3 look = enemy.LookDirection;
            var forward = new Vec3(look.x, look.y, 0f, -1f);
            if (forward.LengthSquared < 1e-6f)
                forward = new Vec3(0f, 1f, 0f, -1f);
            forward = forward.NormalizedCopy();

            Vec3 right = Vec3.CrossProduct(forward, new Vec3(0f, 0f, 1f, -1f));
            Vec3 d = point - VrPhysicalCombat.Chest(enemy);
            return new Vec3(Vec3.DotProduct(d, right), Vec3.DotProduct(d, forward), d.z, -1f);
        }

        /// <summary>
        /// The guard for where the blade will land, in the defender's own frame
        /// (x right, z up from the chest): a stab is met low (the engine's own
        /// answer to a thrust), the head and anything well above the chest high,
        /// the rest on the side it comes to. Pure, for the tests.
        /// </summary>
        internal static UsageDirection GuardFor(Vec3 local, bool stab, bool head, bool flip)
        {
            if (stab)
                return UsageDirection.DefendDown;

            if (head || local.z > HighM)
                return UsageDirection.DefendUp;

            return (local.x >= 0f) != flip ? UsageDirection.DefendRight : UsageDirection.DefendLeft;
        }

        /// <summary>DefendUp is 4, which the enum also calls AttackEnd and
        /// DefendBegin - and ToString may pick either.</summary>
        private static string Named(UsageDirection d)
        {
            return d == UsageDirection.DefendUp ? "DefendUp" : d.ToString();
        }

        /// <summary>v turned by the rotation vector w (Rodrigues). Pure.</summary>
        internal static Vec3 Spin(Vec3 v, Vec3 w)
        {
            float angle = w.Length;
            if (angle < 1e-6f)
                return v;

            Vec3 k = w * (1f / angle);
            float cos = (float)Math.Cos(angle);
            float sin = (float)Math.Sin(angle);
            return v * cos + Vec3.CrossProduct(k, v) * sin + k * (Vec3.DotProduct(k, v) * (1f - cos));
        }

        /// <summary>One enemy sees one attack this tick.</summary>
        private static void Note(Agent enemy, Mission mission, UsageDirection dir, BoneBodyPartType part, float tau,
                                 bool stab, long now)
        {
            Threat t = Find(enemy.Index, mission);
            if (t == null)
                return;

            if (!t.Live)
            {
                t.Live = true;
                t.Index = enemy.Index;
                t.Mission = mission;
                t.FirstSeen = now;
                t.React = ReactS * (0.75f + 0.5f * MBRandom.RandomFloat);
                t.Rolled = false;
                t.Guarding = false;
                t.Dir = UsageDirection.None;
                t.Pending = UsageDirection.None;
            }
            else if (!t.Guarding && t.Rolled && dir != t.Seen)
            {
                // A new line of attack is a new attack: a fresh look at it.
                t.FirstSeen = now;
                t.React = ReactS * (0.75f + 0.5f * MBRandom.RandomFloat);
                t.Rolled = false;
            }

            t.LastSeen = now;
            t.Seen = dir;

            if (!t.Rolled)
            {
                if (now - t.FirstSeen < Seconds(t.React))
                    return;

                t.Rolled = true;

                AgentDrivenProperties props = enemy.AgentDrivenProperties;
                bool shield = HasShield(enemy);
                bool weapon = !shield && HasMeleeWeapon(enemy);
                if (props == null || (!shield && !weapon))
                    return;

                float chance = Math.Min(1f, (shield ? props.AIBlockOnDecideAbility : props.AIParryOnDecideAbility)
                                            * BlockScale);
                float roll = MBRandom.RandomFloat;
                bool guards = roll < chance;

                if (Probe)
                {
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 314 AI: '{0}' [{1}] sees your {2} coming at its {3} in {4:F0} ms, after {5:F0} ms "
                        + "rolled {6:F2} against its {7} {8:F2} -> {9}.",
                        enemy.Name ?? "?", enemy.Index, stab ? "stab" : "cut", part, tau * 1000f,
                        (now - t.FirstSeen) * 1000.0 / Stopwatch.Frequency, roll, shield ? "block" : "parry",
                        chance, guards ? "guards " + Named(dir) + (shield ? " with its shield" : " with its weapon")
                                       : "lets it come"));
                }

                if (guards)
                    Apply(enemy, t, dir, shield);

                return;
            }

            if (!t.Guarding)
                return;

            if (dir == t.Dir)
            {
                t.Pending = UsageDirection.None;
                return;
            }

            if (t.Pending != dir)
            {
                t.Pending = dir;
                t.PendingSince = now;
                return;
            }

            if (now - t.PendingSince < Seconds(ReaimS))
                return;

            if (Probe)
            {
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 314 AI: '{0}' [{1}] moves its guard {2} -> {3}.",
                    enemy.Name ?? "?", enemy.Index, Named(t.Dir), Named(dir)));
            }

            Apply(enemy, t, dir, t.Shield);
        }

        /// <summary>The engine's own guard, held until the threat is over.</summary>
        private static void Apply(Agent enemy, Threat t, UsageDirection dir, bool shield)
        {
            if (shield)
                enemy.EnforceShieldUsage(dir);
            else
                enemy.SetWeaponGuard(dir);

            t.Guarding = true;
            t.Shield = shield;
            t.Dir = dir;
            t.Pending = UsageDirection.None;
        }

        /// <summary>The threat is over: the guard goes back to the AI.</summary>
        private static void End(Threat t)
        {
            if (t == null || !t.Live)
                return;

            Lower(t);
            t.Live = false;
        }

        /// <summary>This enemy's record, or a free one; null when all are busy.</summary>
        private static Threat Find(int index, Mission mission)
        {
            Threat free = null;
            for (int i = 0; i < Threats; i++)
            {
                Threat t = _threats[i] ?? (_threats[i] = new Threat());
                if (t.Live && t.Index == index && t.Mission == mission)
                    return t;

                if (!t.Live && free == null)
                    free = t;
            }

            return free;
        }

        private static bool HasShield(Agent enemy)
        {
            MissionWeapon off = enemy.WieldedOffhandWeapon;
            return !off.IsEmpty && off.CurrentUsageItem != null && off.CurrentUsageItem.IsShield;
        }

        private static bool HasMeleeWeapon(Agent enemy)
        {
            MissionWeapon main = enemy.WieldedWeapon;
            return !main.IsEmpty && main.CurrentUsageItem != null && main.CurrentUsageItem.IsMeleeWeapon;
        }

        /// <summary>A flat direction check: is `facing` (x,y) within the arc of
        /// `toward` (x,y)? Both are yaw-plane vectors, so a height difference
        /// cannot turn a level sword swing into "not facing".</summary>
        private static bool Facing(Vec3 facing, Vec3 toward)
        {
            var f = new Vec3(facing.x, facing.y, 0f, -1f);
            float length = f.Length;
            float other = toward.Length;

            if (length < 1e-4f || other < 1e-4f)
                return false;

            return Vec3.DotProduct(f, toward) / (length * other) >= ArcCos;
        }

        private static bool Guarding(int type)
        {
            return type >= (int)Agent.ActionCodeType.DefendAllBegin
                   && type < (int)Agent.ActionCodeType.DefendAllEnd;
        }

        private static long Seconds(float seconds)
        {
            return (long)(seconds * Stopwatch.Frequency);
        }
    }
}
