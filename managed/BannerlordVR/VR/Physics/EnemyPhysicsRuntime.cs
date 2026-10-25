using System;
using System.Diagnostics;
using BannerlordVR.Interop;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    public static class EnemyPhysicsRuntime
    {
        private sealed class Hand
        {
            public readonly EnemyGrab Grab = new EnemyGrab();
            // Trace diagnostics, logged every 2 s per hand.
            public float DiagSpeed, DiagNearest = float.MaxValue, DiagBoneGap, DiagGrip, DiagTrigger, DiagNext;
            public int DiagCasts, DiagRayHits, DiagRejected, DiagHits, DiagGrabs;
            public string DiagReject = "-", DiagItem = "-";
            public Hand(int index) { Grab.HandIndex = index; Penetration.HandIndex = index; }
            public readonly BodyPenetration Penetration = new BodyPenetration();
            public readonly CutStroke Cut = new CutStroke();
            public bool Sampled, GripDown, SuppressTrigger;
            // A point resting on a body while the blade pushes (pressure entry).
            public EnemyBody PressBody; public sbyte PressBone; public Vec3 PressLocal; public float PressAge;
            // The body the blade last came out of, and when.
            public EnemyBody ReleasedFrom; public float ReleasedAt = -10;
            public float NextStabNote = -10;
            public Vec3 Previous, PreviousBase, PreviousTip, Stage, Axis;
            public ItemObject Item;
            public EquipmentIndex Slot;
            // BladeShape: the flat's normal (world) and the pommel's reach.
            public bool HasFlat; public Vec3 Flat; public float Pommel, Handle;
            public void Reset() { PressBody = null; Grab.Release(); Penetration.Release(); Cut.Reset(); Sampled = GripDown = SuppressTrigger = false; Item = null; Slot = EquipmentIndex.None; ReleasedFrom = null; ReleasedAt = -10; }
        }
        private static readonly EnemyBody[] Bodies = new EnemyBody[EnemyPhysicsSettings.Capacity];
        private static readonly EnemyBody[] Records = CreateRecords();
        private static readonly Hand[] Hands = { new Hand(0), new Hand(1) };
        private static readonly AdaptivePhysicsBudget Budget = new AdaptivePhysicsBudget(
            EnemyPhysicsSettings.Capacity, EnemyPhysicsSettings.BudgetMilliseconds, 4);
        private static long _workStarted;
        private static Mission _mission, _lastMission;
        private static LivingRagdollMissionLogic _living;
        private static BleedingMissionLogic _bleeding;
        private static Agent _player;
        private static int _cursor;
        private static float _time = -1, _lastLog;
        private static readonly System.Collections.Generic.Dictionary<int, string> _refusals = new System.Collections.Generic.Dictionary<int, string>();
        // Every static that holds a mission's agents, at each mission start.
        public static void ForgetMission()
        {
            _refusals.Clear(); ShieldHitAt.Clear();
            BladeClash.ForgetMission(); CorpseKeeper.ForgetMission(); ReleaseWatch.ForgetMission(); SeveredProp.ForgetMission();
            ShieldBlock.ForgetMission(); StuckMissiles.ForgetMission(); KillPushes.Clear(); ThrownBlade.ForgetMission(); WoundFlow.ForgetMission(); DismemberFx.ForgetMission();
            LivingRagdollMissionLogic.ForgetMission(); CorpseVisuals.Forget(); WoundDecal.ForgetMission(); Choke.ForgetMission();
        }
        public static bool Tracks(Agent agent)
        {
            foreach (EnemyBody body in Bodies) if (body?.Agent == agent) return true;
            return false;
        }
        private static string _blocked = "start";
        private static bool _failed;
        public static bool HandlingCombat { get; private set; }
        public static bool OwnsTrigger(bool left)
        {
            return HandlingCombat && Hands[left ? 0 : 1].SuppressTrigger;
        }
        public static int ActiveBodies { get; private set; }
        public static bool SupportsLivingBody => _living?.Available == true;
        public static bool OwnsLivingPhysics(Agent agent) => _living?.Simulating(agent) == true;
        public static bool HasLivingLease(Agent agent) => _living?.HasLease(agent) == true;
        public static bool Penetrated(Agent agent)
        {
            if (agent == null) return false;
            foreach (Hand hand in Hands) if (hand.Penetration.Body?.Agent == agent) return true;
            return false;
        }
        public static bool Protects(Agent agent)
        {
            if (agent == null) return false;
            foreach (Hand hand in Hands)
                if (hand.Penetration.Body?.Agent == agent || hand.Grab.Body?.Agent == agent) return true;
            return HasLivingLease(agent);
        }
        public static bool BeginLivingGrab(EnemyBody body) => !body.IsCorpse && _living?.KeepHeld(body, true) == true;
        public static void MaintainLivingGrab(EnemyBody body) { if (!body.IsCorpse) _living?.KeepHeld(body); }
        public static void TouchLiving(EnemyBody body) { if (!body.IsCorpse) _living?.Touch(body.Agent); }

        // TOG's killing blow: the part takes the weapon's push
        // (AddForceAtPosition along the hit, clamped). The blow that killed
        // made the body a corpse in the same tick: no living impact was given
        // and the game's death ragdoll fell slack, the blade drawn through
        // it. The push is held until the corpse's ragdoll is active, then
        // given over KillPushSteps ticks.
        private struct KillPush { public Agent Agent; public sbyte Bone; public Vec3 Force; public float Until; public int Left; public long Step; }
        private static readonly System.Collections.Generic.List<KillPush> KillPushes = new System.Collections.Generic.List<KillPush>();
        public const float KillPushGain = 25, KillPushWait = 1;
        public const int KillPushSteps = 4;
        // A body the player's blow (in hand or thrown) just killed: ragdoll at
        // once, and the blow's push given to it.
        public static void Killed(Agent agent, sbyte bone, Vec3 velocity)
        {
            if (agent == null || Mission.Current == null) return;
            CorpseKeeper.RagdollNow(agent);
            QueueKillPush(agent, bone, velocity, Mission.Current.CurrentTime);
        }
        private static void QueueKillPush(Agent agent, sbyte bone, Vec3 velocity, float now)
        {
            if (agent == null || bone < 0 || !InteractionMath.IsFinite(velocity) || velocity.Length < 1) return;
            KillPushes.Add(new KillPush { Agent = agent, Bone = bone, Force = InteractionMath.ClampMagnitude(velocity * KillPushGain, EnemyPhysicsSettings.MaxForce),
                Until = now + KillPushWait, Left = KillPushSteps, Step = -1 });
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "KILL_PUSH agent={0} queued at {1:F1} m/s", agent.Index, velocity.Length));
        }
        private static void TickKillPushes(float now)
        {
            for (int i = KillPushes.Count - 1; i >= 0; i--)
            {
                KillPush push = KillPushes[i];
                try
                {
                    Skeleton skeleton = push.Agent?.AgentVisuals?.GetSkeleton();
                    if (skeleton == null || now > push.Until || now < push.Until - KillPushWait - 1) { KillPushes.RemoveAt(i); continue; }
                    if (skeleton.GetCurrentRagdollState() != RagdollState.Active) continue;
                    // Mission ticks run about twice per PhysX step: applied on
                    // each, the push was doubled (PhysicsStepGate).
                    long steps = PhysicsSteps;
                    if (steps >= 0 && push.Step >= 0 && steps <= push.Step) continue;
                    push.Agent.ApplyForceOnRagdoll(push.Bone, in push.Force);
                    push.Step = PhysicsSteps;
                    push.Left--;
                    if (push.Left <= 0) KillPushes.RemoveAt(i); else KillPushes[i] = push;
                }
                catch (Exception) { KillPushes.RemoveAt(i); }
            }
        }
        // PhysX steps counted by the scene clock, or -1 when no clock runs.
        public static long PhysicsSteps => _living?.PhysicsSteps ?? -1;
        // Horizontal gap between a capsule of this radius at a point and the
        // nearest tracked living enemy's capsule (negative when overlapping).
        public static float Clearance(Vec2 at, float radius) => Nearest(at, radius, out _);
        public static System.Collections.Generic.IEnumerable<Agent> TrackedLiving()
        {
            foreach (EnemyBody body in Bodies)
                if (body?.Agent != null && !body.IsCorpse && body.Agent.IsActive()) yield return body.Agent;
        }
        public static float Nearest(Vec2 at, float radius, out Vec2 position)
        {
            float best = float.MaxValue; position = at;
            foreach (EnemyBody body in Bodies)
            {
                Agent agent = body?.Agent;
                // A held body counts too: dropping it from the clearance pulled
                // the view back onto the player's body the moment it was grabbed.
                if (agent == null || body.IsCorpse || !agent.IsActive()) continue;
                float gap = (agent.Position.AsVec2 - at).Length - PlayerReach.Radius(agent) - radius;
                if (gap < best) { best = gap; position = agent.Position.AsVec2; }
            }
            return best;
        }
        public static float PartMass(EnemyBody body, sbyte bone) => _living?.PartMass(body.Agent, bone) ?? 5;
        public static float BodyMass(EnemyBody body) => body == null || body.IsCorpse ? 0 : _living?.BodyMass(body.Agent) ?? 0;
        public static bool IsDying(EnemyBody body) => body != null && !body.IsCorpse && (_living?.IsDying(body.Agent) ?? false);
        public static bool HandleActiveGrab(EnemyBody body, int hand, BodyPoint point, Vec3 handWorld, MatrixFrame boneWorld, Vec3 contact)
            => !body.IsCorpse && _living != null && _living.HandleActive(body, hand, point, handWorld, boneWorld, contact);
        public static bool PierceActive(EnemyBody body, int hand, sbyte bone, Vec3 bladePoint, Vec3 axis, bool axialLock,
            Vec3 localAxis, float angleLimit, MatrixFrame boneWorld, Vec3 contact)
            => !body.IsCorpse && _living != null && _living.PierceActive(body, hand, bone, bladePoint, axis, axialLock, localAxis, angleLimit, boneWorld, contact);
        public static void MarkDying(EnemyBody body) { if (body != null && !body.IsCorpse) _living?.Die(body); }
        public static void MarkFading(EnemyBody body) { if (body != null && !body.IsCorpse) _living?.FadeToDeath(body); }
        public static void ReleaseActiveGrab(EnemyBody body, int hand) { if (body != null && !body.IsCorpse) _living?.ReleaseActive(body, hand); }
        public static void ApplyLivingImpact(EnemyBody body, BodyPoint point, Vec3 velocity,
            PushEffect push = PushEffect.None, float hitMass = 2, bool fist = false)
        { if (!body.IsCorpse) _living?.Impact(body, point, velocity, push, hitMass, fist); }
        // A parried attacker: the attack stops (BrainModuleMelee) and the hand
        // holding the weapon is thrown back with the impact (TryPush Parry).
        public static void Parried(Agent agent, Vec3 impact)
        {
            if (_living == null || agent == null) return;
            EnemyBody body = null;
            foreach (EnemyBody candidate in Bodies) if (candidate?.Agent == agent) { body = candidate; break; }
            if (body == null || body.IsCorpse || !_living.Parry(body, impact.Length)) return;
            sbyte bone = agent.Monster.MainHandItemBoneIndex;
            if (bone < 0 || bone >= body.Skeleton.GetBoneCount()) return;
            BodyPoint hand = body.ReadContact(bone);
            if (hand.Part != BoneBodyPartType.None) _living.Impact(body, hand, impact, PushEffect.StaggerMedium, 3);
        }
        public static void RecordWound(Agent source, EnemyBody body, BodyPoint point, Vec3 contact,
            DamageTypes type, float healthLost, bool drawCut)
        { _bleeding?.Record(source, body, point, contact, type, healthLost, drawCut); }
        public static void DeepenWound(Agent victim, sbyte bone, float depth) => _bleeding?.Deepen(victim, bone, depth);
        private static EnemyBody[] CreateRecords()
        {
            var result = new EnemyBody[EnemyPhysicsSettings.Capacity];
            for (int i = 0; i < result.Length; i++) result[i] = new EnemyBody();
            return result;
        }

        public static void Reset()
        {
            _living?.ReleaseAll();
            _refusals.Clear();
            foreach (Hand hand in Hands) hand.Reset();
            Array.Clear(Bodies, 0, Bodies.Length);
            foreach (EnemyBody record in Records) record.Clear();
            _mission = null; _player = null; _cursor = 0; _time = -1;
            _living = null;
            _bleeding = null;
            HandlingCombat = false; ActiveBodies = 0;
        }

        private static bool RadialMenuOpen() =>
            TaleWorlds.ScreenSystem.ScreenManager.TopScreen is TaleWorlds.MountAndBlade.View.Screens.MissionScreen screen && screen.IsRadialMenuActive;
        private static bool _loggedInput, _loggedSlots;
        private static void Once(ref bool done, string message) { if (!done) { done = true; PhysicsLog.Info(message); } }
        public static void Tick(Mission mission, float dt)
        {
            Perf.Frame();
            long perf = Perf.Start();
            try { TickCore(mission, dt); }
            finally { Perf.Stop(Perf.Section.Runtime, perf); }
        }
        private static void TickCore(Mission mission, float dt)
        {
            bool measured = false;
            try
            {
                Agent player = mission?.MainAgent;
                string blocked = _failed ? "failed_earlier" : !EnemyPhysicsSettings.Enabled ? "enemy_physics_off" :
                    !VrMotionHands.Enabled ? "motion_hands_off" : !VrSystem.IsActive ? "vr_inactive" :
                    mission == null || mission.MissionEnded ? "no_mission" : !EnemyPhysicsSettings.Fighting(mission.Mode) ? "mode_" + mission.Mode :
                    player == null || !player.IsActive() ? "no_player" : player.HasMount && !EnemyPhysicsSettings.Mounted ? "player_mounted" :
                    GameNetwork.IsMultiplayer ? "multiplayer" : !VrCameraDriver.HasPose ? "no_head_pose" :
                    // The release keys the HUD over the world for the whole mission
                    // (ui_overlay_always), so UiWantsOverlay no longer means a menu:
                    // pause only for an actual menu, the order menu or a wheel.
                    VrMissionPhase.UiWantsScreen ? "ui_screen" : VrMissionPhase.UiMenuOpen ? "menu_open" :
                    VrMissionPhase.OrderMenuOpen ? "order_menu" : RadialMenuOpen() ? "radial_menu" :
                    VrHandCalibration.Capturing ? "hand_calibration" : PhysicsInterop.OverlayVisible() ? "vr_overlay" : null;
                if (blocked != _blocked)
                {
                    PhysicsLog.Info("Enemy physics " + (blocked == null ? "ACTIVE" : "paused: " + blocked) +
                        (player != null ? "; player mounted=" + player.HasMount : ""));
                    _blocked = blocked;
                }
                if (blocked != null)
                {
                    try { if (mission != null && !mission.MissionEnded) StuckMissiles.Paused(player); } catch (Exception) { }
                    Reset(); return;
                }
                if (_mission != mission || _player != player)
                {
                    // A new mission: the kept body handles belong to the old one
                    // (Reset clears _mission on every pause, so track it apart).
                    if (_lastMission != mission) { CorpseVisuals.Forget(); _lastMission = mission; }
                    Reset(); _mission = mission; _player = player;
                    _living = mission.GetMissionBehavior<LivingRagdollMissionLogic>();
                    _bleeding = mission.GetMissionBehavior<BleedingMissionLogic>();
                }
                float now = mission.CurrentTime;
                DismemberFx.Tick();
                WoundFlow.Tick();
                WoundDecal.ReadSurfaces();
                try { HitboxProbe.Tick(); HitboxFit.Tick(); } catch (Exception) { }
                try { BodyPenetration.SweepHolds(a => { foreach (Hand h in Hands) if (h.Penetration.Body?.Agent == a) return true; return false; }); } catch (Exception) { }
                if (now <= _time) { ClearHands(); return; }
                float elapsed = _time < 0 ? 0 : now - _time;
                _time = now;
                if (!InteractionMath.IsFinite(dt) || dt <= 0 || dt > .1f || elapsed > .1f)
                { Reset(); return; }
                // Disable the first sample: no sweep may start at world origin.
                if (elapsed <= 0) { ClearHands(); return; }
                dt = elapsed;
                measured = true;
                _workStarted = Stopwatch.GetTimestamp();
                HandlingCombat = true;
                ActiveBodies = 0;
                Vec3 at = player.Position;
                float exit = ExitRadius(player);
                for (int i = 0; i < Bodies.Length; i++)
                {
                    EnemyBody body = Bodies[i];
                    if (body == null) continue;
                    bool leased = _living?.HasLease(body.Agent) == true;
                    if (!EnemyBody.Eligible(body.Agent, player, leased) ||
                        (!leased && (body.Agent.Position - at).Length > exit) || !body.Sample(dt))
                        Remove(i);
                    else
                    {
                        ActiveBodies++;
                        // Within reach a corpse stays physical (not filed).
                        if (body.IsCorpse && CorpseKeeper.Keep(body.Agent, mission.CurrentTime)) CorpseKeeper.KeepAwake(body, _time);
                        // A blade held against its shield pushes it aside.
                        else if (!body.IsCorpse && _living != null && ShieldBlock.Pressing(body.Agent.Index, out Vec3 press))
                            _living.ShieldPress(body, press);
                    }
                }
                ShedUnheldBodies(player);
                long scan = Perf.Start();
                Scan(mission, player, dt);
                Perf.Stop(Perf.Section.Scan, scan);
                if (PhysicsInterop.GetInputState(out BvrInputState input) != (int)BvrStatus.Ok)
                { Once(ref _loggedInput, "Enemy physics: controller input unavailable this frame."); ClearHands(); return; }
                var skeleton = player.AgentVisuals?.GetSkeleton();
                if (skeleton == null || !VrHandSlots.Resolve(player, skeleton, out VrHandSlot main, out VrHandSlot off))
                { Once(ref _loggedSlots, "Enemy physics: hand bones not resolved for the player."); ClearHands(); return; }
                if ((player.WieldedWeapon.CurrentUsageItem?.IsRangedWeapon == true && MeleeUsage(player.WieldedWeapon) == null) ||
                    (player.WieldedOffhandWeapon.CurrentUsageItem?.IsRangedWeapon == true && MeleeUsage(player.WieldedOffhandWeapon) == null))
                { ClearHands(); return; }
                StuckMissiles.DrawOnPlayer(player);
                TickKillPushes(now);
                SampleRig(player, dt);
                UpdateHand(0, input.Left, input.Right, main.Controller == VrHand.Left ? main : off, player, dt, now);
                UpdateHand(1, input.Right, input.Left, main.Controller == VrHand.Right ? main : off, player, dt, now);
                if (EnemyPhysicsSettings.Choke) Choke.Tick(player, Hands[0].Grab, Hands[1].Grab, now);
                if (now - _lastLog > 10)
                {
                    _lastLog = now;
                    PhysicsLog.Info(string.Format("Enemy physics: {0}/{1} bodies (adaptive limit {2}) of {7} agents; CPU mean {3:F3} ms, p95 {4:F3}, p99 {5:F3}; budget {6:F2} ms. CPU work only, not total frame time.",
                        ActiveBodies, Bodies.Length, Budget.BodyLimit, Budget.Mean,
                        Budget.Percentile(.95), Budget.Percentile(.99), Budget.Milliseconds, mission.Agents.Count));
                }
            }
            catch (Exception ex)
            {
                Reset(); _failed = true;
                PhysicsLog.Error("Enemy physics disabled for this session after an error; all grabs released.", ex);
            }
            finally
            {
                if (measured) Budget.Record((Stopwatch.GetTimestamp() - _workStarted) * 1000.0 / Stopwatch.Frequency +
                    (_living?.LastTickCpuMilliseconds ?? 0) + (_bleeding?.LastTickCpuMilliseconds ?? 0));
            }
        }

        // At a gallop (10 m/s) a 4 m radius was crossed in 0.4 s, about the
        // time a body takes to be admitted and sampled: the reach grows by
        // 0.5 s of the player's ground speed, up to 10 m.
        private static float AdmitRadius(Agent player)
        {
            Vec3 v = player.HasMount ? player.MountAgent?.Velocity ?? player.Velocity : player.Velocity;
            return Math.Min(10, EnemyPhysicsSettings.Radius + new Vec2(v.x, v.y).Length * .5f);
        }
        private static float ExitRadius(Agent player) => AdmitRadius(player) + (EnemyPhysicsSettings.ExitRadius - EnemyPhysicsSettings.Radius);

        private static bool AdmissionTimeRemaining =>
            (Stopwatch.GetTimestamp() - _workStarted) * 1000.0 / Stopwatch.Frequency < Budget.Milliseconds * .35;

        private static void ShedUnheldBodies(Agent player)
        {
            while (ActiveBodies > Budget.BodyLimit)
            {
                int remove = -1; float farthest = -1;
                for (int i = 0; i < Bodies.Length; i++)
                {
                    EnemyBody b = Bodies[i];
                    if (b == null || Held(b)) continue;
                    float distance = (b.Agent.Position - player.Position).LengthSquared;
                    if (distance > farthest) { farthest = distance; remove = i; }
                }
                if (remove < 0) break; // Never drop a held body just to improve a statistic.
                Remove(remove); ActiveBodies--;
            }
        }

        private static void ClearHands()
        {
            HandlingCombat = false;
            foreach (Hand h in Hands) h.Reset();
        }
        private static void Remove(int i)
        {
            if (Bodies[i] != null) _living?.Release(Bodies[i].Agent);
            foreach (Hand hand in Hands)
            {
                if (hand.Grab.Body == Bodies[i]) hand.Grab.Release();
                if (hand.Penetration.Body == Bodies[i]) hand.Penetration.Release();
            }
            Bodies[i] = null;
            Records[i].Clear();
        }
        // A body the physics simulates (thrown, falling, getting up) is held
        // too: shedding it handed it back to its animation mid-fall, at the
        // spot the agent never left.
        private static bool Held(EnemyBody body) => Hands[0].Grab.Body == body || Hands[1].Grab.Body == body ||
            Hands[0].Penetration.Body == body || Hands[1].Penetration.Body == body ||
            (body?.Agent != null && _living != null && _living.HasLease(body.Agent));

        private static void Scan(Mission mission, Agent player, float dt)
        {
            var agents = mission.AllAgents; // Includes corpses, unlike mission.Agents.
            int count = agents.Count;
            int budget = Math.Min(count, EnemyPhysicsSettings.ScanBudget);
            // Read once: Position and Velocity are engine calls, and were
            // made for every agent scanned (0.3 ms a frame in a 240-agent
            // battle with no body admitted).
            Vec3 at = player.Position;
            float radius = AdmitRadius(player), reach = radius * radius;
            for (int n = 0; n < budget; n++)
            {
                if ((n & 15) == 0 && !AdmissionTimeRemaining) break;
                if (_cursor >= count) _cursor = 0;
                Agent agent = agents[_cursor++];
                if (agent == player) continue;
                float distance = (agent.Position - at).LengthSquared;
                if (distance > reach) continue;
                int chosen = -1; float farthest = distance + .25f;
                bool found = false;
                for (int i = 0; i < Bodies.Length; i++)
                {
                    EnemyBody b = Bodies[i];
                    if (b?.Agent == agent) { found = true; break; }
                    if (b == null && ActiveBodies < Budget.BodyLimit) { chosen = i; farthest = float.MaxValue; }
                    else if (b != null && farthest != float.MaxValue && !Held(b))
                    {
                        float d = (b.Agent.Position - at).LengthSquared;
                        if (d > farthest) { farthest = d; chosen = i; }
                    }
                }
                if (found || chosen < 0) continue;
                string refused = EnemyBody.EligibleReason(agent, player);
                if (refused != null)
                {
                    // Close agents only: why one within reach is ignored.
                    // Once per agent and reason: a mounted rider logged every
                    // 2 s hid every other refusal.
                    if (EnemyPhysicsSettings.Trace && agent.IsHuman && distance < 25 && refused != "not_enemy" &&
                        (!_refusals.TryGetValue(agent.Index, out string last) || last != refused))
                    {
                        _refusals[agent.Index] = refused;
                        PhysicsLog.Info("Physics ignores agent " + agent.Index + ": " + refused + " controller=" + agent.Controller);
                    }
                    continue;
                }
                // Reuse the record and its arrays after releasing any stale reference.
                EnemyBody candidate = Records[chosen];
                bool occupied = Bodies[chosen] != null;
                Remove(chosen);
                if (candidate.Bind(agent) && candidate.Sample(dt))
                { Bodies[chosen] = candidate; if (!occupied) ActiveBodies++; if (!candidate.IsCorpse) WoundDecal.Prepare(agent); }
                else
                {
                    string why = "bind_" + candidate.LastFailure;
                    if (EnemyPhysicsSettings.Trace && (!_refusals.TryGetValue(agent.Index, out string last) || last != why))
                    { _refusals[agent.Index] = why; PhysicsLog.Info("Physics could not bind agent " + agent.Index + ": " + candidate.LastFailure); }
                    candidate.Clear(); if (occupied) ActiveBodies--;
                }
            }
            ActiveBodies = 0;
            for (int i = 0; i < Bodies.Length; i++) if (Bodies[i] != null) ActiveBodies++;
        }

        // Tales of Glory (WepScript.GetWepInfo): a rider's weapon moves at its
        // hand's speed relative to the rider, plus the rider's own speed
        // along that motion clamped to 4 m/s (2 on foot, where the full
        // walking speed is kept here). The whole gallop counted: every touch
        // of a blade held still struck at the 12 m/s cap and skewered bodies
        // were flung at 12 m/s.
        public const float RiderBoost = 4, RiderThreshold = .7f;
        public static float RiderSpeed(Agent player)
        {
            Agent mount = player?.MountAgent;
            if (mount == null) return 0;
            Vec3 v = mount.Velocity;
            return new Vec2(v.x, v.y).Length;
        }
        // TOG's CtrlVelocity is the controller's motion in the rig: the
        // horse carrying, turning and rocking it is taken out, and only its
        // speed along the hand's motion is added back (AvatarBoost). Taking
        // out the horse's velocity alone left its turns (1-2 m/s at the hand
        // in a turn) and its gait in the hand's motion, and turned stabs
        // sideways. The rig is BannerlordVR's stage anchor, sampled once a
        // tick; where it does not ride with the horse, the horse's velocity.
        private static MatrixFrame _rigPrev, _rigNow;
        private static float _rigDt;
        private static bool _rigSampled, _rigValid, _rigLogged;
        private static void SampleRig(Agent player, float dt)
        {
            MatrixFrame anchor = VrCameraDriver.Anchor;
            bool finite = InteractionMath.IsFinite(anchor.origin) && InteractionMath.IsFinite(anchor.rotation.f) && InteractionMath.IsFinite(anchor.rotation.u);
            _rigValid = _rigSampled && finite && dt > 1e-4f;
            _rigPrev = _rigNow; _rigNow = anchor; _rigDt = dt; _rigSampled = finite;
            // A recentre or a pause between samples is not a motion.
            if (_rigValid && player != null && RigCarried(player.Position).Length > 30) _rigValid = false;
            Agent mount = player?.MountAgent;
            if (_rigValid && mount != null)
            {
                Vec3 horse = mount.Velocity, carried = RigCarried(player.Position);
                // The anchor must ride with the horse to stand for it.
                if (horse.Length > 2 && (carried.Length < horse.Length * .5f || carried.Length > horse.Length * 2 + 4)) _rigValid = false;
                if (!_rigLogged && horse.Length > 2)
                {
                    _rigLogged = true;
                    PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "RIG mounted hand motion from {0}: horse {1:F1} m/s, rig carries the rider at {2:F1} m/s",
                        _rigValid ? "the stage anchor" : "the horse's velocity", horse.Length, carried.Length));
                }
            }
        }
        private static Vec3 RigCarried(Vec3 previous) =>
            (_rigNow.TransformToParent(_rigPrev.TransformToLocalNonOrthogonal(previous)) - previous) * (1 / _rigDt);
        public static Vec3 HandMotion(Agent player, Vec3 world, Vec3 from)
        {
            Agent mount = player?.MountAgent;
            if (mount == null) return world;
            Vec3 avatar = mount.Velocity, own = world - (_rigValid ? RigCarried(from) : avatar);
            float length = own.Length;
            if (length < 1e-3f) return own;
            Vec3 dir = own * (1 / length);
            return own + dir * Math.Max(0, Math.Min(RiderBoost, InteractionMath.Dot(avatar, dir)));
        }
        // A mounted strike needs half the energy to enter or wound (TOG
        // GetThresholdModifier halves it in the saddle): 0.7 of the speed.
        public static float Threshold(Agent player) => player?.MountAgent != null ? RiderThreshold : 1;

        // A point carried along its blade at the gallop (a couched lance, a
        // sword held out) strikes with the horse's speed, up to 8 m/s: cut to
        // 4 m/s along the hand's own motion, a blade held out at the charge
        // only touched (4 thrusts counted in a mounted battle, one entered).
        // Only along the blade and at its point: an edge held out still only
        // touches.
        public const float CouchCap = 8, PointAlong = .8f, MountedPointAlong = .6f;
        public static float PointFrom(Agent player) => player?.MountAgent != null ? MountedPointAlong : PointAlong;
        // TOG tests a stab on the weapon's own velocity (WepVelocity, the
        // rigidbody's): measured against the part instead, an AI that
        // stepped back or swayed in its animation took 26 stabs of 33 down
        // to 1-3 m/s, neither a thrust nor enough to enter. The part's motion
        // counts only when it drives the point in harder (a charge onto it).
        private static Vec3 StabMotion(Agent player, Vec3 world, Vec3 from, Vec3 target, Vec3 bladeAxis, bool point)
        {
            Vec3 relative = StrikeMotion(player, world, from, target, bladeAxis, point);
            Vec3 own = StrikeMotion(player, world, from, Vec3.Zero, bladeAxis, point);
            return InteractionMath.Dot(relative, bladeAxis) > InteractionMath.Dot(own, bladeAxis) ? relative : own;
        }
        private static Vec3 StrikeMotion(Agent player, Vec3 world, Vec3 from, Vec3 target, Vec3 bladeAxis, bool point)
        {
            Vec3 motion = HandMotion(player, world, from) - target;
            if (!point || player?.MountAgent == null) return motion;
            Vec3 carried = world - target;
            float length = carried.Length;
            if (length <= motion.Length || InteractionMath.Dot(carried, bladeAxis) <= length * .7f) return motion;
            return InteractionMath.ClampMagnitude(carried, CouchCap);
        }

        // MissionCombatMechanicsHelper.ComputeBlowDamageOnShield and
        // Agent.OnShieldDamaged: the blow's raw damage on the shield's armour,
        // x0.5 for a point and x0.7 for an edge or a blunt head, x2 for a
        // BonusAgainstShield weapon (axes); the shield loses that many hit
        // points and is destroyed at 0. Blocked by the physics, a struck
        // shield never wore down and never broke.
        // One blow per shield per 0.35 s: the blade sweeps it on several
        // samples and frames.
        private static readonly System.Collections.Generic.Dictionary<int, float> ShieldHitAt = new System.Collections.Generic.Dictionary<int, float>();
        private static void ShieldDamage(Agent agent, WeaponComponentData usage, float speed, bool thrust)
        {
            try
            {
                float now = Mission.Current?.CurrentTime ?? 0;
                if (ShieldHitAt.TryGetValue(agent.Index, out float at) && now >= at && now - at < .35f) return;
                ShieldHitAt[agent.Index] = now;
                EquipmentIndex off = agent.GetOffhandWieldedItemIndex();
                if (off == EquipmentIndex.None) return;
                MissionWeapon shield = agent.Equipment[off];
                if (shield.IsEmpty || shield.CurrentUsageItem?.IsShield != true) return;
                DamageTypes type = thrust ? usage.ThrustDamageType : usage.SwingDamageType;
                float magnitude = (thrust ? usage.ThrustDamage : usage.SwingDamage) * Math.Min(1.5f, speed / (thrust ? 2.5f : 5));
                float damage = MissionGameModels.Current.StrikeMagnitudeModel.ComputeRawDamage(type, magnitude, shield.GetGetModifiedArmorForCurrentUsage(), 1);
                damage *= type == DamageTypes.Pierce ? .5f : .7f;
                if (usage.WeaponFlags.HasAnyFlag(WeaponFlags.BonusAgainstShield)) damage *= 2;
                int dealt = (int)damage;
                if (dealt <= 0) return;
                int left = Math.Max(0, shield.HitPoints - dealt);
                agent.ChangeWeaponHitPoints(off, (short)left);
                if (EnemyPhysicsSettings.Trace)
                    PhysicsLog.Info("SHIELD_DAMAGE agent=" + agent.Index + " -" + dealt + " hit points, " + left + " left");
                if (left == 0)
                {
                    PhysicsLog.Info("SHIELD_BROKEN agent=" + agent.Index + "'s " + (shield.Item?.StringId ?? "shield") + " breaks");
                    agent.RemoveEquippedWeapon(off);
                }
            }
            catch (Exception ex) { PhysicsLog.Error("Shield damage failed.", ex); }
        }

        // Damager.CheckAngles for a slash: the blow must come within
        // maxNormalAngle 60 deg of the edge; across the blade further than
        // that it lands on the flat.
        private const float FlatCos = .866f;
        private static bool OnTheFlat(Hand hand, Vec3 velocity, Vec3 bladeAxis)
        {
            if (!hand.HasFlat || bladeAxis.Length < 1e-3f) return false;
            Vec3 axis = bladeAxis.NormalizedCopy();
            Vec3 across = velocity - axis * InteractionMath.Dot(velocity, axis);
            if (across.Length < 1e-3f) return false;
            return Math.Abs(InteractionMath.Dot(across.NormalizedCopy(), hand.Flat)) > FlatCos;
        }

        // A pommel or flat blow: blunt, no cut, no entry. The pommel is the
        // handle damager (light, heavy past 2 kg) and can stun as any blunt
        // blow to the head; the flat is the slash's bad-angle fallback
        // (BluntLight, badAngleDamage 0.5) and, a bad angle, never stuns.
        public const float PommelDamage = .35f, FlatDamage = .5f, BluntMinVelocity = 2;
        private static void BluntBladeHit(int index, Hand hand, BladeZone zone, EnemyBody hit, BodyPoint point, Vec3 contact, Vec3 velocity,
            WeaponComponentData usage, MissionWeapon weapon, EquipmentIndex slot, Agent player)
        {
            bool pommel = zone == BladeZone.Pommel;
            PushMaterial material = pommel && weapon.Item != null && weapon.Item.Weight > 2 ? PushMaterial.Blunt : PushMaterial.BluntLight;
            float speed = velocity.Length;
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "BLUNT_BLADE {0} agent={1} part={2} speed={3:F2} material={4}", zone, hit.Agent.Index, point.Part, speed, material));
            if (speed < BluntMinVelocity) return;
            PushEffect push = hit.IsCorpse || _living == null ? PushEffect.None : _living.EvaluatePush(hit, point, velocity, material, pommel);
            if (!hit.IsCorpse)
                PhysicalHit.ApplyBlunt(player, hit, point, contact, velocity, weapon, slot, usage, pommel ? PommelDamage : FlatDamage, push);
            if (!hit.IsCorpse && speed > PushReaction.Thresholds(material)[0])
                ApplyLivingImpact(hit, point, velocity, push, 2 + (weapon.Item == null ? 0 : Math.Min(6, weapon.Item.Weight)), false);
            PhysicsInterop.Haptic(index, .45f, .035f);
        }

        // TOG OnCollisionEnter: along the blade, kinetic over 20 J x threshold.
        private static bool TogEnters(Agent player, EnemyBody body, BodyPoint point, Vec3 contact, Vec3 velocity, Vec3 bladeAxis,
            MissionWeapon weapon, float lastHitAge)
        {
            if (TogStab.Hand(body, point.Bone) || velocity.Length < 1e-3f || bladeAxis.Length < 1e-3f) return false;
            // In the saddle the stab is told at 0.7 (the thrust test's): told a
            // thrust and refused here unlogged at 0.7-0.8, mounted stabs into
            // infantry dealt nothing.
            float align = InteractionMath.Dot(velocity.NormalizedCopy(), bladeAxis.NormalizedCopy());
            if (align <= (player?.MountAgent != null ? .7f : TogStab.Align))
            {
                if (EnemyPhysicsSettings.Trace)
                    PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "TOG_STAB refused part={0} speed={1:F2}: moving {2:F2} along the blade", point.Part, velocity.Length, align));
                return false;
            }
            float kinetic = TogStab.Kinetic(weapon.Item, velocity);
            float boost = TogStab.StabBoost(MeleeUsage(weapon));
            float needed = TogStab.Threshold * TogStab.ThresholdModifier(player, body, point.Part, lastHitAge, contact);
            bool enters = kinetic + boost > needed;
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "TOG_STAB {0} part={1} speed={2:F2} kinetic={3:F1} J boost={4:F1} J needed={5:F1} J{6}", enters ? "enters" : "refused", point.Part,
                    velocity.Length, kinetic, boost, needed,
                    point.Part == BoneBodyPartType.Head ? (TogStab.FaceExposed(body, point.Part, contact) ? " (open face)" : " (helmet or skull)") : ""));
            return enters;
        }

        // A javelin, throwing axe or knife held in its throwing usage still
        // strikes in the hand as its melee usage (Bannerlord gives each one).
        public static WeaponComponentData MeleeUsage(MissionWeapon weapon)
        {
            if (weapon.IsEmpty) return null;
            WeaponComponentData current = weapon.CurrentUsageItem;
            if (current?.IsMeleeWeapon == true) return current;
            var usages = weapon.Item?.Weapons;
            if (usages == null) return null;
            foreach (WeaponComponentData usage in usages) if (usage.IsMeleeWeapon && !usage.IsShield) return usage;
            return null;
        }

        private static void UpdateHand(int index, BvrHandState input, BvrHandState otherInput, VrHandSlot slot, Agent player, float dt, float now)
        {
            Hand hand = Hands[index];
            hand.Penetration.Role = slot.Role;
            hand.Grab.Role = slot.Role;
            if (input.IsActive == 0 || !slot.Valid) { hand.Reset(); return; }
            // The second hand on a two-handed weapon holds its haft: it is
            // not an empty hand (it punched, grabbed bodies and pulled arrows
            // with the haft in it).
            if (FastAccess.TwoHandActive?.Invoke() == true &&
                FastAccess.TwoHandGripping?.Invoke() == (index == 0 ? VrHand.Left : VrHand.Right))
            { hand.Reset(); return; }
            MatrixFrame pose = VrCameraDriver.StageToHand(input.GripOrientation, input.GripPosition);
            Vec3 stage = new Vec3(input.GripPosition.X, input.GripPosition.Y, input.GripPosition.Z);
            Vec3 stageAxis = VrMath.ToBannerlord(input.GripOrientation, input.GripPosition).rotation.u;
            MissionWeapon weapon = slot.Equipment == EquipmentIndex.None ? MissionWeapon.Invalid : player.Equipment[slot.Equipment];
            WeaponComponentData usage = weapon.IsEmpty ? null : MeleeUsage(weapon) ?? weapon.CurrentUsageItem;
            // A pulled arrow still held when the hand draws a weapon is let
            // go: it stayed frozen in the air where the hand was.
            if (usage != null && StuckMissiles.Holding(index)) StuckMissiles.Tick(player, index, pose, false, now);
            Vec3 basePoint = pose.origin, tip = basePoint;
            bool blade = usage != null && usage.IsMeleeWeapon && EnemyPhysicsSettings.WeaponDamage;
            hand.SuppressTrigger = usage == null || blade;
            if (blade)
            {
                // The release draws the held weapon as its own skinned visual
                // (VrSkinnedWeapon), not where the game's weapon entity sits;
                // VrBlade measures the blade actually shown in the hand.
                hand.HasFlat = false; hand.Pommel = 0; hand.Handle = BladeShape.HandleFraction(weapon.Item);
                if (VrBlade.TryGet(player, slot, out VrBlade.Segment segment) && segment.Valid)
                {
                    basePoint = segment.GripWorld;
                    tip = segment.TipWorld;
                    BladeShape.Shape shape = BladeShape.Of(slot.Role, segment);
                    if (shape.Valid)
                    {
                        hand.Pommel = shape.Pommel;
                        Vec3 flat = segment.World.rotation.TransformToParent(shape.LocalFlat);
                        hand.HasFlat = shape.HasFlat && flat.Length > 1e-4f;
                        if (hand.HasFlat) hand.Flat = flat.NormalizedCopy();
                    }
                    // The shown blade may be held back by a joint or by
                    // VrCollide; the physics works from the controller's.
                    PhysicalBlade.LearnBlade(slot.Role, basePoint, tip);
                    PhysicalBlade.ToRaw(slot.Role, ref basePoint, ref tip);
                }
                else
                {
                    var entity = player.GetWeaponEntityFromEquipmentSlot(slot.Equipment);
                    if (!entity.IsValid) { hand.Reset(); return; }
                    MatrixFrame weaponFrame = entity.GetGlobalFrame();
                    basePoint = weaponFrame.origin;
                    tip = basePoint + weaponFrame.rotation.u.NormalizedCopy() * Math.Min(2.5f, usage.WeaponLength * .01f);
                }
            }
            bool changed = !hand.Sampled || hand.Item != weapon.Item || hand.Slot != slot.Equipment;
            if (changed || !blade) { hand.Penetration.Release(); hand.Cut.Reset(); }
            if (!InteractionMath.IsFinite(basePoint) || !InteractionMath.IsFinite(tip) ||
                !InteractionMath.IsFinite(pose.rotation.s) || !InteractionMath.IsFinite(pose.rotation.f) || !InteractionMath.IsFinite(pose.rotation.u) ||
                (!changed && ((pose.origin - hand.Previous).Length > .5f || (tip - hand.PreviousTip).Length > 1.5f)))
            { hand.Reset(); return; }
            bool grip = input.Grip >= (hand.GripDown ? .45f : .7f);
            if (!grip || usage != null) hand.Grab.Release();
            bool newGrab = grip && !hand.GripDown && usage == null;
            if (!changed)
            {
                // An arrow stuck in the player or a shield is grabbed before
                // any body; one held in the hand follows it until let go.
                StuckMissiles.Tick(player, index, pose, grip, now);
                if (newGrab && !StuckMissiles.Holding(index) && !StuckMissiles.TryPull(player, pose, index)) { TryGrab(hand, pose.origin); if (hand.Grab.Held) hand.DiagGrabs++; }
                // Use stage-space displacement to reject locomotion-only punches.
                float gestureSpeed = InteractionMath.GestureSpeed(hand.Stage, stage, hand.Axis, stageAxis,
                    blade ? (tip - basePoint).Length : 0, VrCameraDriver.HandScale, dt);
                if (EnemyPhysicsSettings.Trace) Diagnose(index, hand, pose, slot, player, usage, blade, input, gestureSpeed, now);
                EnemyBody heldBefore = hand.Penetration.Body;
                bool embedded = blade && hand.Penetration.Tick(basePoint, tip, dt);
                // Damager hitDelayByCollider: a blade just drawn out of a body
                // does not strike it again at once (the withdrawn stab cut the
                // victim's head 2-40 ms later, a hit on every withdrawal).
                if (heldBefore != null && hand.Penetration.Body == null)
                {
                    if (index == 0) heldBefore.LastHitLeft = now; else heldBefore.LastHitRight = now;
                    hand.ReleasedFrom = heldBefore; hand.ReleasedAt = now;
                }
                if (embedded && hand.Penetration.RumbleDue())
                    PhysicsInterop.Haptic(index, BodyPenetration.RumbleIntensity, .01f);
                bool drawing = !embedded && TryNeckCut(index, hand, otherInput, input.Trigger, basePoint, tip,
                    usage, weapon, slot.Equipment, player, gestureSpeed * dt, dt, now);
                // A shield bashes (a blunt B&S item); a fist hits when closed.
                bool shield = usage != null && usage.IsShield;
                // A blade a shield holds back strikes nothing behind it.
                bool held = blade && (ShieldBlock.Blocked(slot.Role) || BladeClash.Holding(slot.Role));
                if (!held && !embedded && !drawing && !hand.Grab.Held && gestureSpeed >= (blade ? .6f : 1.4f) &&
                    (blade || shield || (usage == null && input.Trigger >= .55f)))
                    TryHit(index, hand, basePoint, tip, usage, weapon, slot.Equipment, player, dt, now, blade);
                else if (!held && !embedded && !drawing && blade && EnemyPhysicsSettings.ActiveRagdoll)
                    // TOG has no slow pressure entry: only a stab's kinetic enters.
                    if (!EnemyPhysicsSettings.TogStab) TryPressure(index, hand, basePoint, tip, usage, player, dt);
                if (hand.Grab.Held) hand.Grab.Tick(pose, dt);
            }
            hand.Previous = pose.origin;
            hand.PreviousBase = basePoint;
            hand.PreviousTip = tip;
            hand.Stage = stage;
            hand.Axis = stageAxis;
            hand.Sampled = true; hand.GripDown = grip; hand.Item = weapon.Item; hand.Slot = slot.Equipment;
        }

        // This path intentionally has no minimum strike speed or strike cooldown:
        // a cut is a continuous draw, while TryHit handles ballistic impacts.
        private static bool TryNeckCut(int index, Hand hand, BvrHandState otherInput, float trigger,
            Vec3 start, Vec3 tip, WeaponComponentData usage, MissionWeapon weapon, EquipmentIndex slot,
            Agent player, float trackedTravel, float dt, float now)
        {
            EnemyGrab hold = Hands[1 - index].Grab;
            EnemyBody body = hold.Body;
            bool sword = usage != null && (usage.WeaponClass == WeaponClass.Dagger ||
                usage.WeaponClass == WeaponClass.OneHandedSword || usage.WeaponClass == WeaponClass.TwoHandedSword);
            if (!EnemyPhysicsSettings.NeckCuts || !EnemyPhysicsSettings.WeaponDamage || !sword ||
                usage.SwingDamageType != DamageTypes.Cut || usage.SwingDamage <= 0 ||
                !InteractionMath.IsFinite(trigger) || trigger < .55f ||
                otherInput.IsActive == 0 || !InteractionMath.IsFinite(otherInput.Grip) || otherInput.Grip < .45f || body == null || body.IsCorpse ||
                !body.Agent.IsActive() || hold.Point < 0 || hold.Point >= body.Count)
            { hand.Cut.Reset(); return false; }
            MatrixFrame otherPose = VrCameraDriver.StageToHand(otherInput.GripOrientation, otherInput.GripPosition);
            if (!InteractionMath.IsFinite(otherPose.origin) || !InteractionMath.IsFinite(otherPose.rotation.s) ||
                !InteractionMath.IsFinite(otherPose.rotation.f) || !InteractionMath.IsFinite(otherPose.rotation.u) ||
                (otherPose.origin - body.Points[hold.Point].World).Length > EnemyPhysicsSettings.BreakDistance)
            { hand.Cut.Reset(); return false; }
            BoneBodyPartType heldPart = body.Points[hold.Point].Part;
            EquipmentIndex otherSlot = Hands[1 - index].Slot;
            if ((heldPart != BoneBodyPartType.Head && heldPart != BoneBodyPartType.Neck) ||
                (otherSlot != EquipmentIndex.None && player.Equipment[otherSlot].CurrentUsageItem != null))
            { hand.Cut.Reset(); return false; }
            Vec3 axis = (tip - start).NormalizedCopy();
            Vec3 edgeStart = start + axis * .08f;
            Agent agent = CastLimbs(edgeStart, tip, player.Index,
                .015f, out float distance, out sbyte bone);
            float length = (tip - edgeStart).Length;
            if (agent != body.Agent || bone < 0 || bone >= body.Skeleton.GetBoneCount() ||
                !InteractionMath.IsFinite(distance) || distance < 0 || distance > length)
            { hand.Cut.Reset(); return false; }
            BodyPoint point = body.ReadContact(bone);
            Vec3 contact = edgeStart + axis * distance;
            if (point.Part != BoneBodyPartType.Neck || !Visible(edgeStart, contact, body) ||
                EquipmentBlocks(body, edgeStart, tip, distance / Math.Max(.001f, length), .015f))
            { hand.Cut.Reset(); return false; }
            if (hand.Cut.Sample(agent.Index, start - point.World, axis, trackedTravel, now))
            {
                Vec3 velocity = HandMotion(player, (start - hand.PreviousBase) * (1 / dt), hand.PreviousBase) - point.Velocity;
                PhysicalHit.ApplyDrawCut(player, body, point, contact, velocity, weapon, slot);
                if (index == 0) body.LastHitLeft = now; else body.LastHitRight = now;
                PhysicsInterop.Haptic(index, .35f, .045f);
            }
            return true; // Reserve this continuous contact instead of also applying impact damage.
        }

        private static bool Visible(Vec3 start, Vec3 end, EnemyBody body)
        {
            float length = (end - start).Length;
            if (length < .001f) return true;
            var ignored = _player.AgentVisuals.GetEntity().WeakEntity;
            if (!_mission.Scene.RayCastForClosestEntityOrTerrainIgnoreEntity(in start, in end, ignored,
                out float distance, out GameEntity entity, .01f)) return true;
            return distance >= length - .06f || (entity != null && entity.Pointer == CorpseVisuals.EntityPointer(body.Agent));
        }

        // B&S HandleRagdoll on the neck: a handle along the whole neck, the
        // Choke handle. The nearest point alone chose the head (its bone
        // starts just above the throat) for a hand closed on the throat, and
        // the enemy was never choked. A hand within NeckReach of the neck's
        // line, from its base to the chin, takes the neck first.
        public const float NeckReach = .14f, ChinAbove = .06f;
        private static bool NeckZone(EnemyBody body, Vec3 position, out int neck, out float gap)
        {
            neck = -1; gap = float.MaxValue;
            if (body.IsCorpse || !EnemyPhysicsSettings.Choke) return false;
            int head = -1;
            for (int i = 0; i < body.Count; i++)
            {
                if (body.Points[i].Part == BoneBodyPartType.Neck) neck = i;
                else if (body.Points[i].Part == BoneBodyPartType.Head && head < 0) head = i;
            }
            if (neck < 0 || head < 0) return false;
            Vec3 a = body.Points[neck].World, b = body.Points[head].World;
            Vec3 axis = b - a;
            if (axis.Length < .02f) return false;
            Vec3 dir = axis.NormalizedCopy();
            float t = Math.Max(0, Math.Min(axis.Length + ChinAbove, InteractionMath.Dot(position - a, dir)));
            gap = (position - (a + dir * t)).Length;
            return gap <= NeckReach;
        }

        private static void TryGrab(Hand hand, Vec3 position)
        {
            EnemyBody best = null; int point = -1; float distance = .22f;
            float neckGap = NeckReach;
            foreach (EnemyBody body in Bodies)
            {
                if (body == null || !body.Agent.IsActive()) continue;
                if (NeckZone(body, position, out int neck, out float gap) && gap <= neckGap && Visible(position, body.Points[neck].World, body))
                { neckGap = gap; best = body; point = neck; }
            }
            if (best != null)
            {
                if (EnemyPhysicsSettings.Trace)
                    PhysicsLog.Info("GRAB neck zone agent=" + best.Agent.Index + " " + neckGap.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " m from the neck's line");
                hand.Grab.Begin(best, point, position);
                return;
            }
            foreach (EnemyBody body in Bodies)
            {
                if (body == null || (!body.IsCorpse && !body.Agent.IsActive())) continue;
                for (int i = 0; i < body.Count; i++)
                {
                    BodyPoint p = body.Points[i];
                    string name = body.Skeleton.GetBoneName(p.Bone) ?? "";
                    // The pose fallback supports head/hands; the native living
                    // backend exposes the full admitted body when explicitly enabled.
                    if (!body.IsCorpse && !SupportsLivingBody && p.Part != BoneBodyPartType.Head &&
                        p.Part != BoneBodyPartType.Neck && !name.EndsWith("hand")) continue;
                    float d = (p.World - position).Length;
                    if (d < distance && Visible(position, p.World, body)) { distance = d; best = body; point = i; }
                }
            }
            if (best == null) return;
            // The engine puts a corpse's ragdoll to sleep once it settles; a
            // hand closing on it wakes it, as any B&S body is physical.
            // A corpse the engine filed is never woken: activating its ragdoll
            // crashed the engine (TaleWorlds.Native+0x1D3A10 read 0x2BE0).
            if (best.IsCorpse && !best.HasNativePhysics && (Filed(best) || !EnemyPhysicsSettings.KeepNearCorpses)) return;
            if (best.IsCorpse && !best.HasNativePhysics && best.Skeleton.GetCurrentRagdollState() == RagdollState.Disabled)
            {
                if (best.Skeleton.IsFrozen()) best.Skeleton.Freeze(false);
                // The agent path (start_ragdoll_as_corpse) is what the engine
                // ticks for a dead agent; Skeleton.ActivateRagdoll alone stayed
                // NeedsActivation forever on a corpse.
                // Only while its visuals still answer (not filed): the start
                // goes through the mission's ragdoll routine (0x1805d34e0).
                bool live = best.Agent.AgentVisuals != null && best.Agent.AgentVisuals.IsValid();
                if (live) best.Agent.StartRagdollAsCorpse();
                RagdollState started = best.Skeleton.GetCurrentRagdollState();
                if (started == RagdollState.Disabled) best.Skeleton.ActivateRagdoll();
                if (EnemyPhysicsSettings.Trace)
                    PhysicsLog.Info("GRAB corpse agent=" + best.Agent.Index + " ragdoll woken visuals=" + (live ? "valid" : "filed") +
                        " after_agent_start=" + started + " now=" + best.Skeleton.GetCurrentRagdollState());
            }
            if (!best.IsCorpse || best.HasNativePhysics || best.Skeleton.GetCurrentRagdollState() != RagdollState.Disabled)
                hand.Grab.Begin(best, point, position);
        }

        // Damager.TryHitByPressure: a point pushed into a body along the blade
        // (within penetrationPressureMaxDot 0.2 of its axis) enters without a
        // strike once the contact's pressure reaches the damager's
        // penetrationPressureForceCurve at the contact speed, times the
        // material's pressureForceMultiplier. The point rests on the surface
        // while the hand pushes; damage then comes from depth.
        private const float PressureMaxDot = .2f, PressLateral = .06f, PressTimeout = 2;
        private static void TryPressure(int index, Hand hand, Vec3 start, Vec3 tip, WeaponComponentData usage, Agent player, float dt)
        {
            if (!hand.Sampled || dt <= 0 || !BodyPenetration.ProfileFor(usage, true, out PenetrationProfile profile) ||
                !PenetrationJoint.Get(profile).Pierce) { hand.PressBody = null; return; }
            Vec3 axis = tip - start;
            if (axis.Length < .05f) { hand.PressBody = null; return; }
            Vec3 axisN = axis.NormalizedCopy();
            Vec3 motion = tip - hand.PreviousTip;
            float length = motion.Length;
            EnemyBody body = hand.PressBody;
            sbyte bone = hand.PressBone;
            Vec3 contact;
            if (body == null || body.Agent == null || !body.Agent.IsActive() || bone >= body.Skeleton.GetBoneCount())
            {
                hand.PressBody = null;
                if (length < 1e-4f || InteractionMath.Dot(motion * (1 / length), axisN) < 1 - PressureMaxDot) return;
                Agent agent = CastLimbs(hand.PreviousTip, tip, player.Index, .02f, out float distance, out bone);
                if (agent == null || bone < 0 || !InteractionMath.IsFinite(distance) || distance < 0 || distance > length) return;
                body = null;
                foreach (EnemyBody candidate in Bodies) if (candidate?.Agent == agent) { body = candidate; break; }
                if (body == null || bone >= body.Skeleton.GetBoneCount()) return;
                contact = hand.PreviousTip + motion * (distance / length);
                if (!Visible(hand.PreviousTip, contact, body) || EquipmentBlocks(body, hand.PreviousTip, tip, distance / length, .02f)) return;
                MatrixFrame first = body.Frame.TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(bone));
                hand.PressBody = body; hand.PressBone = bone; hand.PressAge = 0;
                hand.PressLocal = first.TransformToLocalNonOrthogonal(contact);
            }
            else
            {
                MatrixFrame boneWorld = body.Frame.TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(bone));
                contact = boneWorld.TransformToParent(hand.PressLocal);
                hand.PressAge += dt;
            }
            float overshoot = InteractionMath.Dot(tip - contact, axisN);
            Vec3 lateral = (tip - contact) - axisN * overshoot;
            if (overshoot < -.02f || lateral.Length > PressLateral || hand.PressAge > PressTimeout) { hand.PressBody = null; return; }
            BodyPoint point = body.ReadContact(bone);
            if (point.Part == BoneBodyPartType.None || !InteractionMath.IsFinite(point.World)) { hand.PressBody = null; return; }
            int tier = BodyPenetration.TierOf(hand.Item);
            PenetrationMaterial material = body.IsCorpse ? PenetrationMaterial.Flesh : BodyPenetration.MaterialFor(body.Agent, point.Part);
            PenetrationModifiers.Row row = PenetrationModifiers.Get(profile, material, tier);
            float relativeSpeed = (HandMotion(player, motion * (1 / dt), hand.PreviousTip) - point.Velocity).Length;
            float pressure = PenetrationJoint.Pressure(InteractionMath.Dot(motion, axisN) / dt, overshoot, hand.Item?.Weight ?? 1.5f);
            float threshold = PenetrationJoint.PressureThreshold(profile, relativeSpeed) * row.PressureForceMultiplier;
            if (pressure < threshold) return;
            hand.PressBody = null;
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "PRESSURE reached pressure={0:F2} threshold={1:F2} overshoot={2:F3} relative_speed={3:F2} material={4}",
                    pressure, threshold, overshoot, relativeSpeed, material));
            hand.Penetration.Begin(body, point, contact, profile, relativeSpeed, player, true, tier);
            if (hand.Penetration.Body != null) StopEngineAttack(player);
            if (hand.Penetration.Body != null) PhysicsInterop.Haptic(index, .3f, .03f);
        }

        // Where the physics thinks the hand is, against the character's own hand
        // bone, plus what the hand is doing; validates the pose conversion of
        // the BannerlordVR release and shows which gate stops a contact.
        private static void Diagnose(int index, Hand hand, MatrixFrame pose, VrHandSlot slot, Agent player,
            WeaponComponentData usage, bool blade, BvrHandState input, float speed, float now)
        {
            hand.DiagSpeed = Math.Max(hand.DiagSpeed, speed);
            hand.DiagGrip = Math.Max(hand.DiagGrip, input.Grip);
            hand.DiagTrigger = Math.Max(hand.DiagTrigger, input.Trigger);
            foreach (EnemyBody body in Bodies)
                if (body != null) hand.DiagNearest = Math.Min(hand.DiagNearest, (body.Agent.Position + Vec3.Up - pose.origin).Length);
            Skeleton skeleton = player.AgentVisuals?.GetSkeleton();
            if (skeleton != null && slot.Bone >= 0 && slot.Bone < skeleton.GetBoneCount())
            {
                Vec3 bone = player.AgentVisuals.GetEntity().GetGlobalFrame().TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(slot.Bone)).origin;
                hand.DiagBoneGap = (bone - pose.origin).Length;
            }
            hand.DiagItem = usage == null ? "empty" : (usage.WeaponClass + (blade ? "/blade" : ""));
            if (blade && VrBlade.TryGet(player, slot, out VrBlade.Segment seg) && seg.Valid)
                hand.DiagItem += string.Format(System.Globalization.CultureInfo.InvariantCulture, "/vrblade_len={0:F2}", seg.Length);
            if (now < hand.DiagNext) return;
            hand.DiagNext = now + 2;
            PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "HAND {0} item={1} pose=({2:F2},{3:F2},{4:F2}) gap_to_hand_bone_m={5:F2} max_speed={6:F2} max_grip={7:F2} max_trigger={8:F2} nearest_body_m={9:F2} casts={10} ray_hits={11} rejected={12} last_reject={13} hits={14} grabs={15}",
                index == 0 ? "L" : "R", hand.DiagItem, pose.origin.x, pose.origin.y, pose.origin.z, hand.DiagBoneGap, hand.DiagSpeed,
                hand.DiagGrip, hand.DiagTrigger, hand.DiagNearest == float.MaxValue ? -1 : hand.DiagNearest,
                hand.DiagCasts, hand.DiagRayHits, hand.DiagRejected, hand.DiagReject, hand.DiagHits, hand.DiagGrabs));
            hand.DiagSpeed = hand.DiagGrip = hand.DiagTrigger = 0; hand.DiagNearest = float.MaxValue;
            hand.DiagCasts = hand.DiagRayHits = hand.DiagRejected = hand.DiagHits = hand.DiagGrabs = 0; hand.DiagReject = "-";
        }
        // Contact query used by every gesture. The engine's closest-limb ray
        // returned nothing in VR against frozen sandbox dummies at 45 cm, so
        // the tracked bodies are also asked one by one, then tested against
        // their sampled bone spheres.
        public static string LastCastMethod { get; private set; } = "-";
        // A thrown blade's sweep: the tracked body and limb it meets first.
        public static bool CastTracked(Vec3 from, Vec3 to, Agent player, float thickness, out EnemyBody body, out BodyPoint point, out float fraction)
        {
            body = null; point = default; fraction = 1;
            if (_mission == null || player == null) return false;
            float length = (to - from).Length;
            if (length < 1e-5f) return false;
            Agent agent = CastLimbs(from, to, player.Index, thickness, out float distance, out sbyte bone);
            if (agent == null || bone < 0 || !InteractionMath.IsFinite(distance) || distance > length) return false;
            foreach (EnemyBody candidate in Bodies) if (candidate?.Agent == agent) { body = candidate; break; }
            if (body == null || bone >= body.Skeleton.GetBoneCount()) return false;
            point = body.ReadContact(bone);
            fraction = distance / length;
            return point.Part != BoneBodyPartType.None;
        }
        public static PushEffect EvaluateThrownPush(EnemyBody body, BodyPoint point, Vec3 velocity) =>
            body.IsCorpse || _living == null ? PushEffect.None : _living.EvaluatePush(body, point, velocity, PushMaterial.Pierce);

        // Tested on its drawn bones: a filed corpse (no engine limbs) or a
        // body whose ragdoll the physics drives.
        // A corpse lies where its ragdoll fell, the engine's limbs where its
        // animation was: tested on its drawn points, weapons went through it.
        private static bool DrawnOnly(EnemyBody body) => body.IsCorpse || (_living != null && _living.HasLease(body.Agent));
        private static bool Filed(EnemyBody body) => body.IsCorpse && (body.Agent.AgentVisuals == null || !body.Agent.AgentVisuals.IsValid());

        private static Agent CastLimbs(Vec3 from, Vec3 to, int excluded, float thickness, out float distance, out sbyte bone)
        {
            // A body that is physical (struck, held, pierced) is drawn where its
            // ragdoll is, while the engine's limbs stay with the hidden agent:
            // a swing through empty air hit an enemy a metre away. Such bodies
            // are tested on their drawn bones only.
            // The drawn capsules answer first for every tracked body: the
            // engine's limbs are a second body, off the drawn one.
            Agent drawnBest = null; float drawnDistance = float.MaxValue; sbyte drawnBone = -1;
            bool anyDrawn = false;
            foreach (EnemyBody body in Bodies)
            {
                // Living agents only: BodyOf reads the agent's visuals, which a
                // corpse may no longer have (a thrown blade among corpses
                // crashed in native code).
                if (body?.Agent == null || body.Agent.Index == excluded || body.IsCorpse || !body.Agent.IsActive()) continue;
                if (!DrawnBody.Cast(body.Agent, from, to, thickness, out float d, out sbyte b, out bool hit)) continue;
                anyDrawn = true;
                if (hit && d < drawnDistance) { drawnBest = body.Agent; drawnDistance = d; drawnBone = b; }
            }
            if (anyDrawn)
            {
                Agent corpse = CastFiled(from, to, excluded, thickness, out float cd, out sbyte cb);
                if (corpse != null && cd < drawnDistance) { drawnBest = corpse; drawnDistance = cd; drawnBone = cb; }
                distance = drawnBest == null ? 0 : drawnDistance; bone = drawnBone;
                if (drawnBest != null) LastCastMethod = "drawn_capsules";
                return drawnBest;
            }
            Agent agent = _mission.RayCastForClosestAgentsLimbs(from, to, excluded, thickness, out distance, out bone);
            if (agent != null && bone >= 0)
            {
                EnemyBody hitBody = null;
                foreach (EnemyBody candidate in Bodies) if (candidate?.Agent == agent) { hitBody = candidate; break; }
                // A horse (the player's own, its head and neck before the
                // saddle, or a rider's) or a friend, untracked, is closest
                // along the blade: the tracked bodies are tested past it.
                // Returned, it hid the enemy behind it (hundreds of mounted
                // blows ended agent_not_tracked).
                bool past = hitBody == null && (!agent.IsHuman || agent == _player?.MountAgent || _player == null || !EnemyBody.Hostile(agent, _player));
                if (!past && (hitBody == null || !DrawnOnly(hitBody))) { LastCastMethod = "closest_limbs"; return agent; }
            }
            float length = (to - from).Length;
            Agent best = null; float bestDistance = float.MaxValue; sbyte bestBone = -1;
            foreach (EnemyBody body in Bodies)
            {
                if (body?.Agent == null || body.Agent.Index == excluded) continue;
                if (!DrawnOnly(body))
                {
                    if (_mission.RayCastForGivenAgentsLimbs(from, to, body.Agent.Index, thickness, out float d, out sbyte b) &&
                        b >= 0 && InteractionMath.IsFinite(d) && d < bestDistance)
                    { best = body.Agent; bestDistance = d; bestBone = b; LastCastMethod = "given_limbs"; }
                    continue;
                }
                if (length < 1e-5f) continue;
                for (int i = 0; i < body.Count; i++)
                {
                    BodyPoint point = body.Points[i];
                    float helmet = point.Part == BoneBodyPartType.Head ? HelmetVolume.HeadMargin(body.Agent) : 0;
                    if (!InteractionMath.SegmentSphere(from, to, point.World, point.Radius + helmet + thickness, out float t)) continue;
                    float d = t * length;
                    if (d < bestDistance) { best = body.Agent; bestDistance = d; bestBone = point.Bone; LastCastMethod = "bone_spheres"; }
                }
            }
            distance = best == null ? 0 : bestDistance; bone = bestBone;
            return best;
        }

        // Corpses (and anything not living) are tested on their kept bone
        // spheres, never through BannerlordVR's visuals reads.
        private static Agent CastFiled(Vec3 from, Vec3 to, int excluded, float thickness, out float distance, out sbyte bone)
        {
            float length = (to - from).Length;
            Agent best = null; distance = float.MaxValue; bone = -1;
            if (length < 1e-5f) return null;
            foreach (EnemyBody body in Bodies)
            {
                if (body?.Agent == null || body.Agent.Index == excluded || (!body.IsCorpse && body.Agent.IsActive())) continue;
                for (int i = 0; i < body.Count; i++)
                {
                    BodyPoint point = body.Points[i];
                    if (!InteractionMath.SegmentSphere(from, to, point.World, point.Radius + thickness, out float t)) continue;
                    if (t * length < distance) { best = body.Agent; distance = t * length; bone = point.Bone; }
                }
            }
            return best;
        }

        private const float ReleaseHitDelay = .35f, HitCooldown = .2f, ImpactMinSpeed = 2, AxeBite = 40, AxeBoost = 30;

        // A blade struck through a body it is not held in, living or dead,
        // stops at the contact and recoils (TOG), instead of being drawn
        // through it.
        private static void ShowImpact(Hand hand, EnemyBody hit, BodyPoint hitPoint, Vec3 contact, Vec3 start, Vec3 tip, float hitAlong,
            Vec3 velocity, WeaponComponentData usage, bool blade) =>
            ImpactAt(hand, hit.Agent, hitPoint.Part, contact, start, tip, hitAlong, velocity, usage, blade, hit.IsCorpse);
        private static void ImpactAt(Hand hand, Agent agent, BoneBodyPartType part, Vec3 contact, Vec3 start, Vec3 tip, float hitAlong,
            Vec3 velocity, WeaponComponentData usage, bool blade, bool corpse)
        {
            if (!blade || usage == null || usage.IsShield || hand.Penetration.Body != null || velocity.Length < ImpactMinSpeed) return;
            PenetrationMaterial material = BodyPenetration.MaterialFor(agent, part);
            Vec3 shownNow = start + (tip - start) * hitAlong;
            PhysicalBlade.Impact(hand.Penetration.Role, contact - shownNow, velocity,
                material == PenetrationMaterial.Chainmail || material == PenetrationMaterial.Plate);
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "BLADE_IMPACT agent={0}{1} part={2} material={3} speed={4:F1}: the shown blade stops {5:F2} m back at the contact and recoils",
                    agent.Index, corpse ? " (corpse)" : "", part, material, velocity.Length, (contact - shownNow).Length));
        }
        private static void Touch(EnemyBody body, sbyte bone, float fraction, float along, Vec3 previous, Vec3 current, float dt, Agent player,
            ref Agent touched, ref Vec3 at, ref Vec3 velocity, ref float touchFraction, ref float touchAlong, ref BoneBodyPartType part)
        {
            if (bone < 0 || bone >= body.Skeleton.GetBoneCount()) return;
            BodyPoint point = body.ReadContact(bone);
            if (point.Part == BoneBodyPartType.None || !InteractionMath.IsFinite(point.World)) return;
            touched = body.Agent; touchFraction = fraction; touchAlong = along; part = point.Part;
            at = previous + (current - previous) * fraction;
            velocity = HandMotion(player, (current - previous) * (1 / dt), previous) - point.Velocity;
        }

        // BannerlordVR's VrMelee starts an engine attack from every hand
        // motion over swing_speed (2 m/s, one per 250 ms). A blade held in a
        // body moved that fast while pushed or drawn out: each engine attack
        // struck the held enemy (its blow refused, its hit sound, blood and
        // reaction still played), a string of hits on every stab. The hand
        // that holds a body, or just let go of one, starts none.
        // Lets go of a body without dealing what its blades held back: the
        // beheading rebuilds its visuals, then kills it.
        public static void LetGo(Agent agent)
        {
            foreach (Hand hand in Hands)
            {
                if (hand.Penetration.Body?.Agent == agent) hand.Penetration.Abandon();
                if (hand.Grab.Body?.Agent == agent) hand.Grab.Release();
            }
            _living?.Release(agent);
        }
        public static bool HandHolds(int index)
        {
            if (index < 0 || index >= Hands.Length) return false;
            Hand hand = Hands[index];
            float now = Mission.Current != null ? Mission.Current.CurrentTime : 0;
            // Mission time restarts with each mission: a stamp ahead of it is stale.
            return hand.Penetration.Body != null || hand.Grab.Held || (now >= hand.ReleasedAt && now - hand.ReleasedAt < .5f);
        }
        // The engine attack the stabbing swing started is stopped as the
        // blade enters: the physics deals that blow.
        private static void StopEngineAttack(Agent player)
        {
            try
            {
                Agent.ActionCodeType type = player.GetCurrentActionType(1);
                if (type != Agent.ActionCodeType.ReadyMelee && type != Agent.ActionCodeType.ReleaseMelee) return;
                player.SetActionChannel(1, ActionIndexCache.act_none, ignorePriority: true);
                if (EnemyPhysicsSettings.Trace) PhysicsLog.Info("ENGINE_ATTACK stopped: the blade entered a body (" + type + ")");
            }
            catch (Exception) { }
        }
        private static void Reject(Hand hand, string reason) { hand.DiagRejected++; hand.DiagReject = reason; }

        private static void TryHit(int index, Hand hand, Vec3 start, Vec3 tip, WeaponComponentData usage,
            MissionWeapon weapon, EquipmentIndex slot, Agent player, float dt, float now, bool blade)
        {
            hand.DiagCasts++;
            EnemyBody hit = null; BodyPoint hitPoint = default; float earliest = 2, hitAlong = 0; bool hitCooled = false;
            // A contact that deals nothing (inside the cooldown, just drawn
            // out, or an enemy past the bodies simulated) still stops the
            // shown blade: each was drawn through the body.
            Agent touched = null; Vec3 touchAt = Vec3.Zero, touchVelocity = Vec3.Zero; float touchFraction = 2, touchAlong = 0;
            BoneBodyPartType touchPart = BoneBodyPartType.Chest;
            Vec3 rawVelocity = Vec3.Zero, stabVelocity = Vec3.Zero;
            Vec3 contact = Vec3.Zero, velocity = Vec3.Zero;
            int samples = blade ? Math.Min(32, Math.Max(2, (int)Math.Ceiling((tip - start).Length / .06f))) : 1;
            // A shield's face is about 0.22 m round its grip, a fist 6.5 cm.
            float thickness = blade ? .025f : usage != null && usage.IsShield ? .22f : .065f;
            // Behind the grip, the pommel (a blunt damager of its own).
            float bladeLength = (tip - start).Length;
            int pommelSamples = blade && hand.Pommel > .03f && bladeLength > .1f ? Math.Max(1, (int)Math.Ceiling(hand.Pommel / .06f)) : 0;
            for (int s = -pommelSamples; s <= (blade ? samples : 0); s++)
            {
                float along = !blade ? 0 : s >= 0 ? (float)s / samples : s * (hand.Pommel / bladeLength) / pommelSamples;
                Vec3 previous = hand.PreviousBase + (hand.PreviousTip - hand.PreviousBase) * along;
                Vec3 current = start + (tip - start) * along;
                float length = (current - previous).Length;
                if (length < 1e-5f) continue;
                // Query the engine's real limb colliders and returned bone, not
                // guessed spheres. Closest-agent selection also prevents hitting
                // an admitted enemy through a nearer ally or excluded rider.
                Agent agent = CastLimbs(previous, current, player.Index,
                    thickness, out float distance, out sbyte bone);
                if (agent == null || bone < 0 || !InteractionMath.IsFinite(distance) || distance < 0 || distance > length) continue;
                hand.DiagRayHits++; hand.DiagReject = "via_" + LastCastMethod;
                float fraction = distance / length;
                if (fraction >= earliest) continue;
                EnemyBody body = null;
                foreach (EnemyBody candidate in Bodies) if (candidate?.Agent == agent) { body = candidate; break; }
                if (body == null)
                {
                    Reject(hand, "agent_not_tracked");
                    if (blade && fraction < touchFraction && agent.IsActive() && agent.IsHuman && EnemyBody.Hostile(agent, player))
                    {
                        touched = agent; touchFraction = fraction; touchAlong = along; touchAt = previous + (current - previous) * fraction;
                        touchVelocity = HandMotion(player, (current - previous) * (1 / dt), previous) - agent.Velocity; touchPart = BoneBodyPartType.Chest;
                    }
                    continue;
                }
                if (body == hand.ReleasedFrom && now >= hand.ReleasedAt && now - hand.ReleasedAt < ReleaseHitDelay)
                {
                    Reject(hand, "just_withdrawn");
                    if (blade && fraction < touchFraction) Touch(body, bone, fraction, along, previous, current, dt, player, ref touched, ref touchAt, ref touchVelocity, ref touchFraction, ref touchAlong, ref touchPart);
                    continue;
                }
                if (bone >= body.Skeleton.GetBoneCount()) { Reject(hand, "bone_range"); continue; }
                // A point driven along the blade is a new pierce attempt: B&S
                // tries penetration on every contact. The cooldown only spaces
                // impacts, so a first glancing touch no longer blocks the stab
                // that follows it.
                BodyPoint early = body.ReadContact(bone);
                Vec3 bladeAxis = (tip - start).NormalizedCopy();
                bool atTip = blade && usage.ThrustDamage > 0 && along >= PointFrom(player);
                Vec3 sampleVelocity = StabMotion(player, (current - previous) * (1 / dt), previous, early.Velocity, bladeAxis, atTip);
                bool piercing = atTip &&
                    InteractionMath.Dot(sampleVelocity, bladeAxis) > sampleVelocity.Length * .7f;
                // 0.2 s: one swing crosses a body in under 0.1 s; at 0.35 s a
                // quick second blow went through the enemy unfelt.
                bool cooled = now - (index == 0 ? body.LastHitLeft : body.LastHitRight) < HitCooldown;
                if (cooled && !piercing)
                {
                    Reject(hand, "cooldown");
                    if (blade && fraction < touchFraction) Touch(body, bone, fraction, along, previous, current, dt, player, ref touched, ref touchAt, ref touchVelocity, ref touchFraction, ref touchAlong, ref touchPart);
                    continue;
                }
                Vec3 c = previous + (current - previous) * fraction;
                if (!Visible(previous, c, body)) { Reject(hand, "occluded"); continue; }
                // A blade lying in or across the shield is on the shield, not the
                // body behind it: the whole blade is tested, not only this
                // sample's path (one shield touch was counted in a whole test).
                if (EquipmentBlocks(body, previous, current, fraction, thickness) ||
                    (blade && BladeMetShield(body.Agent, hand.PreviousBase, hand.PreviousTip, start, tip, thickness)))
                {
                    // B&S BrainModuleMelee: a weapon striking an attacking
                    // enemy's weapon or shield parries the attack.
                    Reject(hand, "equipment");
                    // The blow wears the shield down, and breaks it at 0.
                    if (!cooled && usage != null && !usage.IsShield) ShieldDamage(body.Agent, usage, length / dt, piercing);
                    bool parried = _living != null && _living.Parry(body, length / dt);
                    // Heavy weapons push the blocker through the block.
                    if (_living != null && !body.IsCorpse && usage != null && !usage.IsShield)
                        parried |= _living.BlockTransfer(body, (current - previous) * (1 / dt), PhysicalHit.Material(usage, false),
                            2 + (weapon.IsEmpty || weapon.Item == null ? 0 : Math.Min(6, weapon.Item.Weight)));
                    PhysicsInterop.Haptic(index, parried ? .6f : .15f, parried ? .05f : .02f);
                    continue;
                }
                BodyPoint point = body.ReadContact(bone);
                if (point.Part == BoneBodyPartType.None || !InteractionMath.IsFinite(point.World)) continue;
                earliest = fraction; hit = body; hitPoint = point; contact = c; hitAlong = along; hitCooled = cooled;
                rawVelocity = StrikeMotion(player, (current - previous) * (1 / dt), previous, point.Velocity, bladeAxis, atTip);
                stabVelocity = StabMotion(player, (current - previous) * (1 / dt), previous, point.Velocity, bladeAxis, atTip);
                velocity = InteractionMath.ClampMagnitude(rawVelocity, 12);
            }
            if (hit == null)
            {
                if (touched != null)
                    ImpactAt(hand, touched, touchPart, touchAt, start, tip, touchAlong, InteractionMath.ClampMagnitude(touchVelocity, 12), usage, blade, false);
                return;
            }
            hand.DiagHits++;
            float lastHitAge = now - Math.Max(hit.LastHitLeft, hit.LastHitRight);
            if (index == 0) hit.LastHitLeft = now; else hit.LastHitRight = now;
            Vec3 stab = InteractionMath.ClampMagnitude(stabVelocity, 12);
            bool thrust = blade && usage.ThrustDamage > 0 && InteractionMath.Dot(stab, (tip - start).NormalizedCopy()) > stab.Length * .7f;
            // B&S: the pommel (Damager_HandleLight/Heavy) and the flat (the
            // slash damager's bad angle, badAngleBluntFallback) strike blunt.
            // The grip end of VrBlade's segment is the weapon's butt: the first
            // stretch along it is the handle and the pommel.
            BladeZone zone = !blade || usage.IsShield ? BladeZone.Edge : hitAlong < hand.Handle ? BladeZone.Pommel :
                !thrust && OnTheFlat(hand, velocity, tip - start) ? BladeZone.Flat : BladeZone.Edge;
            if (zone != BladeZone.Edge)
            {
                if (!hitCooled) BluntBladeHit(index, hand, zone, hit, hitPoint, contact, velocity, usage, weapon, slot, player);
                // A haft, pommel or flat is as solid as the edge.
                ShowImpact(hand, hit, hitPoint, contact, start, tip, hitAlong, velocity, usage, blade);
                return;
            }
            // B&S dismemberment: a fast cut across a joint slices and kills.
            // Only an edge slices: a mace or a hammer at speed took heads off.
            if (blade && !thrust && !hit.IsCorpse && EnemyPhysicsSettings.ActiveRagdoll && usage.SwingDamageType == DamageTypes.Cut)
            {
                BodyPenetration.ProfileFor(usage, false, out PenetrationProfile sliceProfile);
                bool twoHanded = usage.WeaponFlags.HasAnyFlag(WeaponFlags.NotUsableWithOneHand);
                // Damager dismembermentMinVelocity is 12 m/s for a one-handed
                // sword: measured on the 12 m/s-capped hit speed it never sliced.
                if (DismemberFx.TrySlice(player, hit, contact, start, tip, InteractionMath.ClampMagnitude(rawVelocity, 30), sliceProfile, twoHanded, false,
                        BodyPenetration.TierOf(weapon.Item)))
                {
                    PhysicsInterop.Haptic(index, .8f, .06f);
                    return;
                }
            }
            // Blade & Sorcery push: level from impact speed and material, effect
            // from the human hit reaction table and the enemy's current action.
            // B&S pushes only from a contact that deals damage (the material's
            // minVelocity): slow touches at 0.3-2 m/s staggered the enemy.
            float pushMin = usage == null ? PunchImpact.For(BodyPenetration.MaterialFor(hit.Agent, hitPoint.Part)).MinVelocity : 0;
            if (blade && !hit.IsCorpse)
            {
                BodyPenetration.ProfileFor(usage, thrust, out PenetrationProfile pushProfile);
                pushMin = PenetrationModifiers.Get(pushProfile, BodyPenetration.MaterialFor(hit.Agent, hitPoint.Part),
                    BodyPenetration.TierOf(weapon.Item)).DamageMinVelocity;
            }
            PushEffect push = hit.IsCorpse || _living == null || velocity.Length < pushMin ? PushEffect.None :
                _living.EvaluatePush(hit, hitPoint, velocity, PhysicalHit.Material(usage, thrust));
            // A thrust that enters is not an impact kill: its damage comes from
            // depth and skewering, and lands when the blade is pulled out.
            // A thrust enters through its point (B&S pierce damagers sit at the
            // tip), never through the middle of a blade swept into the body.
            bool atPoint = !blade || hitAlong >= PointFrom(player);
            bool enters = thrust && atPoint && !hit.IsCorpse && EnemyPhysicsSettings.ActiveRagdoll &&
                BodyPenetration.ProfileFor(usage, true, out PenetrationProfile entering) && PenetrationJoint.Get(entering).Pierce &&
                (EnemyPhysicsSettings.TogStab
                    ? TogEnters(player, hit, hitPoint, contact, stabVelocity, tip - start, weapon, lastHitAge)
                    : velocity.Length >= PenetrationJoint.MinVelocity(entering, BodyPenetration.MaterialFor(hit.Agent, hitPoint.Part), BodyPenetration.TierOf(weapon.Item)) * Threshold(player));
            if (thrust && !atPoint && !hit.IsCorpse && EnemyPhysicsSettings.Trace && now >= hand.NextStabNote)
            {
                hand.NextStabNote = now + .25f;
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "TOG_STAB not at the point part={0} speed={1:F2}: the body met the blade {2:F2} along it, the point is from {3:F2}{4}",
                    hitPoint.Part, stab.Length, hitAlong, PointFrom(player), player.MountAgent != null ? " (mounted)" : ""));
            }
            // Inside the impact cooldown only an actual entry counts; the
            // blade is still stopped at the body (a quick second blow was
            // drawn through the enemy).
            if (hitCooled && !enters) { ShowImpact(hand, hit, hitPoint, contact, start, tip, hitAlong, velocity, usage, blade); return; }
            // DamageModifier minVelocity: a slower contact (a blade resting or
            // dragged against a body) deals no damage; it still pushes.
            bool damages = true;
            if (blade && !hit.IsCorpse)
            {
                BodyPenetration.ProfileFor(usage, thrust, out PenetrationProfile damageProfile);
                PenetrationModifiers.Row damageRow = PenetrationModifiers.Get(damageProfile,
                    BodyPenetration.MaterialFor(hit.Agent, hitPoint.Part), BodyPenetration.TierOf(weapon.Item));
                damages = velocity.Length >= damageRow.DamageMinVelocity * Threshold(player);
            }
            // An axe's chop that beats TOG's cut test (kinetic over 40 J x the
            // threshold modifier: half in the saddle, x2 mail, x3 plate)
            // bites and stays in the part (below). Its blow staggers at most:
            // knocked down by it, the body fell off the axe at once.
            bool axeBites = false; PenetrationProfile chop = PenetrationProfile.AxeSlash1H; MatrixFrame rawHand = MatrixFrame.Identity;
            if (blade && !thrust && usage != null && !hit.IsCorpse && hand.Penetration.Body == null && hit.Agent.MountAgent == null &&
                hit.Agent.IsActive() && EnemyPhysicsSettings.ActiveRagdoll && BodyPenetration.ProfileFor(usage, false, out chop) &&
                (chop == PenetrationProfile.AxeSlash1H || chop == PenetrationProfile.AxeSlash2H) && PhysicalBlade.RawHand(hand.Penetration.Role, out rawHand))
            {
                // An axe is made to bite, as a spear to stab (TOG StabBoost):
                // a moving soldier in plate needed 120 J, his blows gave 56-88.
                float kinetic = TogStab.Kinetic(weapon.Item, rawVelocity) + AxeBoost;
                float needed = AxeBite * TogStab.ThresholdModifier(player, hit, hitPoint.Part, lastHitAge, contact);
                axeBites = kinetic > needed;
                if (EnemyPhysicsSettings.Trace)
                    PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "AXE_BITE {0} part={1} speed={2:F2} kinetic={3:F1} J needed={4:F1} J", axeBites ? "bites" : "refused", hitPoint.Part, rawVelocity.Length, kinetic, needed));
                if (axeBites && push > PushEffect.StaggerLight) push = PushEffect.StaggerLight;
            }
            bool wasAlive = !hit.IsCorpse;
            if (!enters && damages) PhysicalHit.Apply(player, hit, hitPoint, contact, velocity, weapon, slot, thrust, push);
            if (wasAlive && hit.IsCorpse) Killed(hit.Agent, hitPoint.Bone, velocity);
            // A corpse struck is pushed over a few ticks, not one PhysX step.
            else if (!wasAlive && damages) QueueKillPush(hit.Agent, hitPoint.Bone, velocity * .5f, now);
            // A blade under the material's first push speed only touches: B&S
            // resolves it as a plain contact. Its impulses lifted a body
            // over a blade drawn slowly across the head.
            bool pushes = usage == null || velocity.Length > PushReaction.Thresholds(PhysicalHit.Material(usage, thrust))[0];
            if (!hit.IsCorpse && pushes && !axeBites) ApplyLivingImpact(hit, hitPoint, velocity, push,
                2 + (weapon.IsEmpty || weapon.Item == null ? 0 : Math.Min(6, weapon.Item.Weight)), usage == null);
            if ((hit.IsCorpse || EnemyPhysicsSettings.LivingRagdolls || EnemyPhysicsSettings.ActiveRagdoll) &&
                (!thrust || atPoint) && BodyPenetration.ProfileFor(usage, thrust, out PenetrationProfile profile))
            {
                // A slash through a fighting enemy is dealt without the
                // physical cut: its drag leased the body and stopped the AI
                // half a second on every cut.
                bool fighting = !hit.IsCorpse && hit.Agent.Controller == AgentControllerType.AI && !hit.Agent.IsPaused &&
                    !EnemyPhysicsRuntime.HasLivingLease(hit.Agent);
                if (EnemyPhysicsSettings.TogStab && thrust && !hit.IsCorpse && PenetrationJoint.Get(profile).Pierce)
                {
                    // TOG: only a stab that passed its entry test holds.
                    if (enters)
                    {
                        float kinetic = TogStab.Kinetic(weapon.Item, stabVelocity);
                        // Riding past, TOG's joint breaks 0.15 s in (all 9
                        // stabs into infantry from the saddle ended so, limbs
                        // dealt 1-2.6 points): the blade is held as in a rider.
                        bool ridePast = player.MountAgent != null && RiderSpeed(player) > BodyPenetration.RideThroughSpeed;
                        if (!ridePast) hand.Penetration.BeginTog(hit, hitPoint, contact, profile, player, BodyPenetration.TierOf(weapon.Item), velocity,
                            kinetic, TogStab.DamageModifier(hit, hitPoint.Part, lastHitAge, contact));
                        if (hand.Penetration.Body != null) StopEngineAttack(player);
                        // A rider has no ragdoll to hold on the blade: the
                        // stab was entered and lost (3 into a rider's shoulder
                        // dealt nothing). Its vital part kills, a limb takes
                        // the engine's thrust, once per stab: the point driven
                        // in right after the stroke's first touch (a cut or a
                        // glance, 20-60 ms before) is inside that touch's
                        // cooldown, and all 5 stabs entered into riders in a
                        // mounted test dealt nothing.
                        else if (now - hit.LastEntry >= HitCooldown && hit.Agent.IsActive())
                        {
                            hit.LastEntry = now;
                            // A rider keeps the blade in the part it entered.
                            if ((hit.Agent.MountAgent != null || ridePast) && PhysicalBlade.RawHand(hand.Penetration.Role, out MatrixFrame stabHand))
                            {
                                MatrixFrame shownStab = stabHand;
                                shownStab.origin += contact - (start + (tip - start) * hitAlong);
                                // A limb takes the engine's thrust, as before the
                                // hold: TOG's kinetic/20 dealt it 0.3-2.6 points.
                                bool lethalStab = TogStab.Lethal(hitPoint.Part);
                                if (!lethalStab) PhysicalHit.Apply(player, hit, hitPoint, contact, velocity, weapon, slot, thrust, push);
                                if (hit.Agent.IsActive())
                                    hand.Penetration.BeginMounted(hit, hitPoint, contact, profile, player, BodyPenetration.TierOf(weapon.Item),
                                        shownStab, stabHand, velocity, 0, lethalStab);
                                if (hand.Penetration.Body != null) { StopEngineAttack(player); PhysicsInterop.Haptic(index, .9f, .07f); return; }
                                if (!hit.Agent.IsActive()) return;
                            }
                            if (TogStab.Lethal(hitPoint.Part))
                                PhysicalHit.ApplyPenetration(player, hit.Agent, hitPoint.Bone, hitPoint.Part, contact, hit.Agent.Health + 1, DamageTypes.Pierce);
                            else PhysicalHit.Apply(player, hit, hitPoint, contact, velocity, weapon, slot, thrust, push);
                            BloodFx.Burst(BloodFx.Kind.Enter, contact, Vec3.Up);
                            if (EnemyPhysicsSettings.Trace)
                                PhysicsLog.Info("TOG_STAB agent=" + hit.Agent.Index + " not held (" + (hit.Agent.MountAgent != null ? "a rider" : "no ragdoll") +
                                    "): " + (TogStab.Lethal(hitPoint.Part) ? "killed" : "struck") + " at once");
                        }
                    }
                }
                // An axe's chop bites instead (below): the slash joint held it
                // 3 mm deep and it slid off at once.
                // Into a corpse only while its ragdoll is live: asleep, the
                // blade was held 0.4 s for nothing and let go.
                // A slash's joint (3 mm deep) ended cut_through 0.05 s in, 18
                // times in 35, the blade drawn through the body with no stop:
                // a blow that does not slice stops at the body instead.
                else if ((!hit.IsCorpse || hit.HasNativePhysics) && thrust)
                {
                    hand.Penetration.Begin(hit, hitPoint, contact, profile, velocity.Length, player, false, BodyPenetration.TierOf(weapon.Item), velocity);
                    if (hand.Penetration.Body != null) StopEngineAttack(player);
                }
            }
            // The axe bites.
            if (axeBites && hand.Penetration.Body == null && hit.Agent.IsActive())
            {
                {
                    MatrixFrame shownHand = rawHand;
                    shownHand.origin += contact - (start + (tip - start) * hitAlong);
                    hand.Penetration.BeginBite(hit, hitPoint, contact, chop, player, BodyPenetration.TierOf(weapon.Item), shownHand, rawHand);
                    if (hand.Penetration.Body != null) { StopEngineAttack(player); PhysicsInterop.Haptic(index, .9f, .07f); }
                }
            }
            ShowImpact(hand, hit, hitPoint, contact, start, tip, hitAlong, velocity, usage, blade);
            if (usage == null)
            {
                // Damager_Punch intensity (2 to 9 m/s) drives the effect.
                float intensity = PunchImpact.Intensity(velocity.Length);
                PhysicsInterop.Haptic(index, .2f + .8f * intensity, .02f + .04f * intensity);
            }
            else PhysicsInterop.Haptic(index, .45f, .035f);
        }

        // A blade that met the shield anywhere on its way this tick: a fast
        // blow crosses the plate between two ticks, and by the time a limb is
        // hit the blade is already behind it (3 parries counted in a test).
        private static bool BladeMetShield(Agent agent, Vec3 prevBase, Vec3 prevTip, Vec3 baseNow, Vec3 tipNow, float radius)
        {
            EquipmentIndex off = agent.GetOffhandWieldedItemIndex();
            if (off == EquipmentIndex.None || agent.Equipment[off].IsEmpty || agent.Equipment[off].CurrentUsageItem?.IsShield != true) return false;
            Vec3 prevMid = (prevBase + prevTip) * .5f, midNow = (baseNow + tipNow) * .5f;
            return ItemBlocks(agent, off, baseNow, tipNow, 1, radius) || ItemBlocks(agent, off, prevBase, prevTip, 1, radius) ||
                ItemBlocks(agent, off, prevTip, tipNow, 1, radius) || ItemBlocks(agent, off, prevMid, midNow, 1, radius) ||
                ItemBlocks(agent, off, prevBase, baseNow, 1, radius);
        }

        private static bool EquipmentBlocks(EnemyBody body, Vec3 start, Vec3 end, float bodyFraction, float radius)
        {
            if (body.IsCorpse) return false;
            return ItemBlocks(body.Agent, body.Agent.GetOffhandWieldedItemIndex(), start, end, bodyFraction, radius) ||
                ItemBlocks(body.Agent, body.Agent.GetPrimaryWieldedItemIndex(), start, end, bodyFraction, radius);
        }

        private static bool ItemBlocks(Agent agent, EquipmentIndex slot, Vec3 start, Vec3 end, float bodyFraction, float radius)
        {
            if (slot == EquipmentIndex.None) return false;
            WeaponComponentData item = agent.Equipment[slot].CurrentUsageItem;
            if (item == null || (!item.IsShield && !item.IsMeleeWeapon)) return false;
            // A shield is where BannerlordVR draws it (its plate); the weapon
            // entity's box was 0.93 m away, and blows went past the shield.
            if (item.IsShield && VrBlade.TryGetEnemyPlate(agent, out VrBlade.Plate plate) && plate.Valid)
            {
                Vec3 a = plate.A.NormalizedCopy(), b = plate.B.NormalizedCopy(), n = plate.N.NormalizedCopy();
                Vec3 ls = new Vec3(InteractionMath.Dot(start - plate.Centre, a), InteractionMath.Dot(start - plate.Centre, b), InteractionMath.Dot(start - plate.Centre, n));
                Vec3 le = new Vec3(InteractionMath.Dot(end - plate.Centre, a), InteractionMath.Dot(end - plate.Centre, b), InteractionMath.Dot(end - plate.Centre, n));
                Vec3 half = new Vec3(plate.HalfA, plate.HalfB, Math.Max(plate.HalfN, ShieldPlate.MinHalfThickness));
                return InteractionMath.SegmentBox(ls, le, -half, half, radius, out float hit) && hit <= bodyFraction;
            }
            WeakGameEntity entity = agent.GetWeaponEntityFromEquipmentSlot(slot);
            if (!entity.IsValid) return false;
            MatrixFrame frame = entity.GetGlobalFrame();
            BoundingBox bounds = entity.GetLocalBoundingBox();
            // Some modded items have no usable bounds. Preserve a held shield
            // conservatively in that case; never invent a weapon mesh.
            if (!InteractionMath.IsFinite(bounds.min) || !InteractionMath.IsFinite(bounds.max) ||
                (bounds.max - bounds.min).LengthSquared < 1e-6f) return item.IsShield;
            float scale = Math.Min(frame.rotation.s.Length, Math.Min(frame.rotation.f.Length, frame.rotation.u.Length));
            if (scale < .001f) return item.IsShield;
            return InteractionMath.SegmentBox(frame.TransformToLocalNonOrthogonal(start),
                frame.TransformToLocalNonOrthogonal(end), bounds.min, bounds.max, radius / scale,
                out float fraction) && fraction <= bodyFraction;
        }
    }
}
