using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    public sealed class LivingRagdollMissionLogic : MissionLogic
    {
        private sealed class Entry : ILivingRagdollHost
        {
            public Agent Agent;
            public Skeleton Skeleton;
            public Mission Mission;
            public readonly LivingRagdollLease Lease = new LivingRagdollLease();
            public readonly LivingBodyBalance Balance = new LivingBodyBalance();
            public readonly ActiveRagdollBody Active = new ActiveRagdollBody();
            public bool StartDestabilized;
            public bool ImpactPending;
            public sbyte ImpactBone;
            public Vec3 ImpactForce;
            public float ImpactDeadline;
            public bool ReleasePending;
            public bool Exists => Agent != null && Agent.State != AgentState.Deleted;
            public bool Alive => Exists && Agent.IsActive();
            private bool SkeletonMatches => Skeleton != null &&
                Agent?.AgentVisuals?.GetSkeleton()?.Pointer == Skeleton.Pointer;
            public bool Safe => Alive && Mission.MainAgent != null &&
                SkeletonMatches && EnemyBody.Eligible(Agent, Mission.MainAgent, Lease.Occupied);
            public bool Paused { get => Agent.IsPaused; set => Agent.IsPaused = value; }
            public bool NativeActive => SkeletonMatches && Skeleton.GetCurrentRagdollState() == RagdollState.Active;
            public bool NativeDisabled => !SkeletonMatches || Skeleton.GetCurrentRagdollState() == RagdollState.Disabled;
            public void BeginNative()
            {
                if (!NativeRagdollBridge.Preflight(Agent, Mission, Skeleton))
                    throw new InvalidOperationException("Living ragdoll activation refused by recovery preflight.");
                Perf.Mark("preflight");
                if (EnemyPhysicsSettings.ActiveRagdoll)
                {
                    bool ready = Active.Capture(Agent, Skeleton, StartDestabilized);
                    Perf.Mark("capture");
                    PhysicsLog.Info("ACTIVE_RAGDOLL capture ready=" + ready + " pose=" + Active.CaptureSource + " " + Actions(Agent) +
                        " frozen=" + Skeleton.IsFrozen());
                }
                else if (EnemyPhysicsSettings.ActiveBalance)
                    PhysicsLog.Info("BALANCE capture ready=" + Balance.Capture(Agent, Skeleton, Mission));
                Agent.StartRagdollAsCorpse();
                Perf.Mark("engine_ragdoll");
                CloseCapsule();
                Perf.Mark("capsule");
            }
            // Whatever ends the lease (a timeout, the agent no longer safe to
            // hold), the agent comes back where its body lies, not at the
            // spot it was held at while the body was thrown about.
            private void PlaceUnderBody()
            {
                try
                {
                    if (!EnemyPhysicsSettings.ActiveRagdoll || !Active.Configured || Agent.MountAgent != null) return;
                    Vec3 pelvis = Active.PelvisPosition;
                    if (!InteractionMath.IsFinite(pelvis) || (pelvis - Agent.Position).Length > 30) return;
                    // Off its ground (it sank while held) it is set back on it.
                    bool off = Math.Abs(Agent.Position.z - Mission.Scene.GetGroundHeightAtPosition(Agent.Position)) > .2f;
                    if ((pelvis - Agent.Position).AsVec2.Length < .5f)
                    {
                        if (!off) return;
                        pelvis = Agent.Position;
                    }
                    var face = new PathFaceRecord(-1, -1, -1);
                    Mission.Scene.GetNavMeshFaceIndex(ref face, pelvis, true);
                    if (!face.IsValid()) return;
                    Vec3 ground = new WorldPosition(Mission.Scene, pelvis).GetGroundVec3();
                    if (!InteractionMath.IsFinite(ground)) return;
                    Agent.TeleportToPosition(ground);
                    Rehold();
                    PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "ACTIVE_RAGDOLL lease_end placed agent={0} at its body phase={1}", Agent.Index, Active.Phase));
                }
                catch (Exception ex) { PhysicsLog.Error("Placing a released agent under its body failed.", ex); }
            }
            public static string Actions(Agent agent)
            {
                try
                {
                    return "action0=" + agent.GetCurrentAction(0).GetName() + "/" + agent.GetCurrentActionType(0) +
                        " action1=" + agent.GetCurrentAction(1).GetName() + "/" + agent.GetCurrentActionType(1);
                }
                catch (Exception) { return "action=?"; }
            }
            public void EndNative()
            {
                if (!SkeletonMatches || NativeDisabled) { ReleasePending = false; return; }
                if (Agent != null && Mission != null) LastRelease[Agent] = Mission.CurrentTime;
                // The last physical pose, in the world, before anything moves
                // the agent: the animation takes over from it gradually.
                if (EnemyPhysicsSettings.ActiveRagdoll && EnemyPhysicsSettings.ReleaseBlend) StartBlend(Agent, Skeleton);
                PlaceUnderBody();
                RagdollState state = Skeleton.GetCurrentRagdollState();
                if (state == RagdollState.NeedsActivation) { ReleasePending = true; return; }
                OpenCapsule("release");
                NativeRagdollBridge.Release(Agent, Mission, Skeleton);
                ReleasePending = false;
                // The corpse ragdoll leaves the skeleton frozen (captured
                // unfrozen, frozen after every release): no animation shows.
                if (Skeleton.IsFrozen()) Skeleton.Freeze(false);
                // The animation takes over at once, not when the lease has
                // finished letting go: meanwhile the body stood frozen in its
                // last physical pose, then snapped.
                if (Skeleton.GetCurrentRagdollState() == RagdollState.Disabled)
                    Skeleton.TickAnimationsAndForceUpdate(.001f, Agent.AgentVisuals.GetGlobalFrame(), false);
                ReleaseWatch.Start(Agent, Active.Configured ? Active.PelvisPosition : Agent.Position);
            }
            // Agent.IsPaused only stops AI decisions: the engine kept walking a
            // paused agent towards its last target, 15-28 m away from its body
            // in a battle test. Hold the agent where its body went physical.
            private bool _held;
            private Vec2 _holdPosition;
            // Agent.SetIsPhysicsForceClosed (IMBAgent set_is_physics_force_closed,
            // unused by the game): the capsule the engine collides agents with.
            // Watched: an agent sinking under its hold spot gets it back.
            private bool _capsuleClosed, _capsuleLogged;
            private float _capsuleZ;
            private Agent.AIScriptedFrameFlags _savedScripted;
            private void CloseCapsule()
            {
                if (!EnemyPhysicsSettings.CloseHeldCapsule || _capsuleClosed) return;
                try
                {
                    _capsuleZ = Agent.Position.z;
                    // TaleWorlds.Native (REA/Ghidra): set_is_physics_force_closed
                    // only skips the agent's own movement step; other agents
                    // skip a neighbour whose movement flags carry bit 0x100,
                    // which is AIScriptedFrameFlags.InConversation.
                    Agent.SetIsPhysicsForceClosed(true);
                    // Closed, its step no longer stands it on the ground and it
                    // sank 0.5 m in 2 s: keep gravity off it too.
                    Agent.SetExcludedFromGravity(true, false);
                    _savedScripted = Agent.GetScriptedFlags();
                    Agent.SetScriptedFlags(_savedScripted | Agent.AIScriptedFrameFlags.InConversation);
                    _capsuleClosed = true;
                    if (!_capsuleLogged) { _capsuleLogged = true; PhysicsLog.Info("ACTIVE_RAGDOLL capsule closed agent=" + Agent.Index); }
                }
                catch (Exception ex) { PhysicsLog.Error("Closing a held agent's capsule failed.", ex); }
            }
            public bool CapsuleClosed => _capsuleClosed;
            public void OpenCapsule(string reason)
            {
                if (!_capsuleClosed) return;
                _capsuleClosed = false;
                try
                {
                    if (Agent != null && Agent.State != AgentState.Deleted)
                    {
                        Agent.SetIsPhysicsForceClosed(false);
                        Agent.SetExcludedFromGravity(false, false);
                        Agent.SetScriptedFlags(_savedScripted);
                    }
                    if (reason != "release") PhysicsLog.Info("ACTIVE_RAGDOLL capsule reopened reason=" + reason);
                }
                catch (Exception ex) { PhysicsLog.Error("Reopening an agent's capsule failed.", ex); }
            }
            private void WatchCapsule()
            {
                if (!_capsuleClosed) return;
                Vec3 position = Agent.Position;
                if (InteractionMath.IsFinite(position) && position.z > _capsuleZ - .5f) return;
                // No teleport while the body is physical: it moved the ragdoll
                // with it (the body hopped 0.5 m); the release places it.
                OpenCapsule("sank_" + (_capsuleZ - position.z).ToString("F2", CultureInfo.InvariantCulture));
            }
            public void Rehold() { if (_held && Agent != null) _holdPosition = Agent.Position.AsVec2; }
            public void SuppressMovement()
            {
                WatchCapsule();
                // The blow's knock-back would walk the hidden agent away.
                ClearHitReaction(Agent);
                if (!_held) { _held = true; _holdPosition = Agent.Position.AsVec2; }
                // Held still (its own movement step is closed), the agent only
                // moves when the physics teleports it under its body; its spot
                // goes with it, or it walked back to where it was grabbed.
                // (the physics' own teleports call Rehold; any other drift is
                // the blow's root motion and is not adopted)
                Agent.MovementFlags = 0;
                Agent.MovementInputVector = Vec2.Zero;
                Agent.EventControlFlags = 0;
                Agent.SetMaximumSpeedLimit(0, false);
                // Without a target the engine walks the agent straight ahead
                // (3-4 m/s measured); its own spot keeps it under the body.
                Agent.SetTargetPosition(_holdPosition);
            }
            public void ResumeDecisions()
            {
                _held = false;
                // Same reset Bannerlord applies when control changes hands.
                Agent.SetMaximumSpeedLimit(-1, false);
                // The spot it was held at is no goal any more: an AI picks its
                // own, an agent without one stays where its body got up.
                if (Agent.Controller == AgentControllerType.AI) Agent.ClearTargetFrame();
                else Agent.SetTargetPosition(Agent.Position.AsVec2);
                Agent.ForceAiBehaviorSelection();
            }
            public void Clear() { OpenCapsule("clear"); _held = false; Balance.Clear(); Active.Clear(); StartDestabilized = false; Agent = null; Skeleton = null; Mission = null; ImpactPending = ReleasePending = false; ImpactForce = Vec3.Zero; }
        }
        private static readonly Dictionary<Agent, PoseBlend> _blends = new Dictionary<Agent, PoseBlend>();
        private static readonly Dictionary<Agent, float> LastRelease = new Dictionary<Agent, float>();
        private const float ReleaseCooldown = .5f;
        private static readonly List<Agent> _blendsDone = new List<Agent>();
        internal static void ForgetMission() { _blends.Clear(); LastRelease.Clear(); _blendsDone.Clear(); }
        private static void StartBlend(Agent agent, Skeleton skeleton)
        {
            try
            {
                if (!_blends.TryGetValue(agent, out PoseBlend blend)) _blends[agent] = blend = new PoseBlend();
                var entity = agent.AgentVisuals?.GetEntity();
                if (entity == null || !blend.Capture(skeleton, entity.GetGlobalFrame(), agent.Mission?.CurrentTime ?? 0)) _blends.Remove(agent);
            }
            catch (Exception ex) { _blends.Remove(agent); PhysicsLog.Error("Capturing the release pose failed.", ex); }
        }
        private void ApplyBlends()
        {
            if (_blends.Count == 0) return;
            _blendsDone.Clear();
            foreach (KeyValuePair<Agent, PoseBlend> pair in _blends)
            {
                Agent agent = pair.Key;
                Skeleton skeleton = agent.IsActive() ? agent.AgentVisuals?.GetSkeleton() : null;
                var entity = agent.IsActive() ? agent.AgentVisuals?.GetEntity() : null;
                if (skeleton == null || entity == null || HasLease(agent) && skeleton.GetCurrentRagdollState() != RagdollState.Disabled)
                {
                    // Still being released (or taken again): wait, or drop it.
                    if (skeleton == null || entity == null || !pair.Value.Running) _blendsDone.Add(agent);
                    continue;
                }
                if (skeleton.GetCurrentRagdollState() != RagdollState.Disabled) continue;
                if (!pair.Value.Apply(skeleton, entity.GetGlobalFrame(), Mission.CurrentTime)) _blendsDone.Add(agent);
            }
            foreach (Agent agent in _blendsDone) _blends.Remove(agent);
        }
        private readonly Entry[] _entries = CreateEntries();
        private readonly PhysicsStepCounter _clock = new PhysicsStepCounter();
        private bool _failed;
        private Entry _probeEntry;
        private bool _probeStarted, _probeFinished, _probeSawActive;
        private bool _probeInspectedForceApi;
        private float _probeHealth, _probeMaxDisplacement;
        private Vec3 _probeOrigin;
        private RagdollState _probeLastState;
        private Agent _probeAgent;
        private Skeleton _probeSkeleton;
        private float _probeFollowUntil;
        private float _probeBattleBegan = -1;
        private float _probeBegan, _probeMaxDrop, _probeMaxRise, _probePeakSupport;
        private bool _probeBalanceCaptured, _probeBalanceFell;
        private int _probeBalanceTicks, _probeBalanceSkipped;
        private long _probeBalanceStepsAt = -1, _probeBalanceStepsEnd = -1;
        private Vec3 _probeRecoveryOrigin;
        private float _probeNextFollow, _probeLocomotionDistance;
        private static bool _warmed;
        // The first push, fall and stand-up each cost several ms of JIT on
        // the mission thread (1.6, 7.3 and 16 ms ticks); compile while the
        // mission loads. Mission behaviours added from OnMissionBehaviorInitialize
        // never receive OnBehaviorInitialize, so this runs at construction.
        public LivingRagdollMissionLogic()
        {
            if (_warmed) return;
            _warmed = true;
            if (EnemyPhysicsSettings.ActiveRagdoll)
                JitWarmup.Prepare(typeof(ActiveRagdollBody), typeof(RagdollPinDrive), typeof(ActiveRagdollCycle),
                    typeof(PushReaction), typeof(RagdollGrabRules), typeof(PenetrationJoint), typeof(BodyPenetration),
                    typeof(PhysicsStepCounter), typeof(PhysicsStepGate), typeof(NativeRagdollBridge), typeof(LivingRagdollLease),
                    typeof(LivingRagdollMissionLogic), typeof(InteractionMath), typeof(PenetrationDamage), typeof(RagdollImpact),
                    typeof(BloodFx), typeof(PhysicalHit), typeof(EnemyGrab), typeof(Dismemberment), typeof(DismemberFx),
                    // The first parry, block or cut compiled these mid-fight: 16-23 ms frames.
                    typeof(BladeClash), typeof(ShieldBlock), typeof(PhysicalBlade), typeof(DrawnBody), typeof(EnemyPhysicsRuntime),
                    typeof(LimbHide), typeof(HelmetHide), typeof(SeveredProp), typeof(WoundFlow), typeof(Perf), typeof(ThrownBlade),
                    typeof(Disarm), typeof(EnemyBody), typeof(PunchImpact));
            // The capsule reader is an expression tree compiled on first use.
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(FastAccess).TypeHandle);
        }
        public bool Available => EnemyPhysicsSettings.LivingRagdolls && NativeRagdollBridge.Ready && !_failed;
        public double LastTickCpuMilliseconds { get; private set; }
        private static Entry[] CreateEntries()
        {
            var result = new Entry[12];
            for (int i = 0; i < result.Length; i++) result[i] = new Entry();
            return result;
        }
        public bool HasLease(Agent agent)
        {
            foreach (Entry entry in _entries) if (entry.Agent == agent && entry.Lease.Occupied) return true;
            return false;
        }
        public bool Simulating(Agent agent)
        {
            foreach (Entry entry in _entries)
                if (entry.Agent == agent && entry.Lease.OwnsPhysics) return true;
            return false;
        }
        // A fighting enemy is handed back to its AI this soon after the last
        // touch (a cut, a shield push, a stab pulled out).
        public const float FightingHold = .4f;
        private static float LeaseSeconds => EnemyPhysicsSettings.ActiveRagdoll ? 14 : 6;
        public bool KeepHeld(EnemyBody body, bool allowBegin = false, bool destabilized = false)
        {
            // A rider's ragdoll would start in the saddle, on the moving horse.
            if (!Available || body.IsCorpse || body.Agent?.MountAgent != null) return false;
            foreach (Entry entry in _entries)
            {
                if (entry.Agent != body.Agent || !entry.Lease.Occupied) continue;
                if (entry.Lease.Phase == LivingRagdollPhase.Recovering) return false;
                entry.Lease.KeepHeld(Mission.CurrentTime);
                return true;
            }
            if (!allowBegin) return false;
            // Not again right after a release: taken back one tick after one,
            // the ragdoll started from a half-released skeleton and the pins
            // catapulted the body (40 m/s, 2.8 m up in the logs).
            if (LastRelease.TryGetValue(body.Agent, out float released) && Mission.CurrentTime - released < ReleaseCooldown &&
                Mission.CurrentTime >= released) return false;
            foreach (Entry entry in _entries)
            {
                if (entry.Lease.Occupied || entry.Lease.Faulted) continue;
                // A lease that ended by death never reopened its agent's capsule.
                if (entry.Agent != null) entry.OpenCapsule("reuse");
                entry.Agent = body.Agent; entry.Skeleton = body.Skeleton; entry.Mission = Mission;
                entry.StartDestabilized = destabilized;
                try
                {
                    if (!NativeRagdollBridge.Preflight(entry.Agent, Mission, entry.Skeleton))
                    { _failed = true; entry.Clear(); return false; }
                    bool fighting = body.Agent.Controller == AgentControllerType.AI && !body.Agent.IsPaused;
                    if (entry.Lease.Begin(entry, Mission.CurrentTime, LeaseSeconds))
                    {
                        if (fighting) entry.Active.HoldSeconds = FightingHold;
                        return true;
                    }
                    entry.Clear(); return false;
                }
                catch (Exception ex)
                {
                    _failed = true; entry.Clear();
                    PhysicsLog.Error("Living ragdoll activation failed; backend stopped for this mission.", ex);
                    return false;
                }
            }
            return false;
        }
        public void Touch(Agent agent)
        {
            Entry entry = Find(agent);
            if (entry != null && entry.Lease.Occupied && entry.Lease.Phase != LivingRagdollPhase.Recovering)
            {
                entry.Lease.KeepHeld(Mission.CurrentTime, .3f);
                entry.Active?.Touch();
            }
        }
        public void Release(Agent agent)
        {
            foreach (Entry entry in _entries)
                if (entry.Agent == agent && entry.Lease.Occupied) SafeRelease(entry);
        }
        public void Impact(EnemyBody body, BodyPoint point, Vec3 velocity, PushEffect push = PushEffect.None, float hitMass = 2, bool fist = false)
        {
            if (EnemyPhysicsSettings.ActiveRagdoll) { ActiveImpact(body, point, velocity, push, hitMass, fist); return; }
            if (!KeepHeld(body, true)) return;
            foreach (Entry entry in _entries)
            {
                if (entry.Agent != body.Agent || !entry.Lease.Occupied ||
                    entry.Lease.Phase == LivingRagdollPhase.Recovering) continue;
                entry.Lease.KeepHeld(Mission.CurrentTime, .6f);
                entry.ImpactPending = true; entry.ImpactBone = point.Bone;
                entry.ImpactForce = InteractionMath.ClampMagnitude(velocity * 25, EnemyPhysicsSettings.MaxForce);
                entry.ImpactDeadline = Mission.CurrentTime + .35f;
                return;
            }
        }
        // B&S: the colliding item's momentum reaches the part through the
        // contact over one simulation step. A destabilizing push hands the
        // living enemy to the physical body; lesser pushes only nudge a body
        // that is already simulated.
        private void ActiveImpact(EnemyBody body, BodyPoint point, Vec3 velocity, PushEffect push, float hitMass, bool fist)
        {
            if (!Available || body.IsCorpse || !InteractionMath.IsFinite(velocity)) return;
            Entry entry = Find(body.Agent);
            if (entry == null)
            {
                // B&S keeps every creature near the player physical
                // (PhysicToggle): any hit moves the struck part, pinned back
                // to its pose; the head snaps, the torso rocks.
                bool destabilize = push == PushEffect.Destabilize;
                if (!destabilize && !EnemyPhysicsSettings.HitPhysics) return;
                // A lease pauses the AI for the whole hold (about a second): a
                // fighting enemy froze at every touch and every parry. B&S
                // keeps its creature animated under the physics; here an
                // enemy whose AI is running keeps it, with the engine's own
                // flinch, and only a blow that floors it hands it over.
                if (!destabilize && body.Agent.Controller == AgentControllerType.AI && !body.Agent.IsPaused) return;
                if (!KeepHeld(body, true, destabilize)) return;
                entry = Find(body.Agent);
                if (entry == null) return;
            }
            else if (push == PushEffect.Destabilize) entry.Active.Destabilize();
            if (entry.Lease.Phase == LivingRagdollPhase.Recovering) return;
            // The stun judged for this very blow (EvaluatePush, just before).
            if (_knockouts.TryGetValue(body.Agent, out float judged) && Mission.CurrentTime - judged < .25f)
            {
                _knockouts.Remove(body.Agent);
                if (body.Agent.IsActive())
                {
                    entry.Active.KnockOut(Mission.CurrentTime, Dismemberment.KnockoutSeconds);
                    // Out 6 s, then the 3 s ground settle and the 3 s rise.
                    entry.Lease.ExtendUntil(Mission.CurrentTime + Dismemberment.KnockoutSeconds + 7);
                }
            }
            // The blow just registered started its knock-back: stop it before
            // its root motion walks the hidden agent off the physical body.
            ClearHitReaction(body.Agent);
            entry.Active.QueueHit(point.Bone, velocity, hitMass, fist, push);
            entry.Active.LastPusher = Mission.MainAgent;
        }
        public bool HandleActive(EnemyBody body, int hand, BodyPoint point, Vec3 handWorld, MatrixFrame boneWorld, Vec3 contact)
        {
            Entry entry = Find(body.Agent);
            if (entry == null || !entry.Lease.OwnsPhysics) return false;
            entry.Lease.KeepHeld(Mission.CurrentTime);
            GrabHandle kind = Classify(body, point);
            if (entry.Active.Configured && entry.Active.Phase == ActiveRagdollPhase.Standing)
            {
                PushContext context = ContextOf(body.Agent, Mission.CurrentTime);
                if (RagdollGrabRules.ForcesLift(kind, context))
                {
                    PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "ACTIVE_RAGDOLL grab force_lift handle={0} context={1} time={2:F3}", kind, context, Mission.CurrentTime));
                    entry.Active.Destabilize();
                }
            }
            return entry.Active.Handle(hand, point.Bone, kind, handWorld, boneWorld, contact);
        }
        // BrainModuleDeath.DropGrabbedCoroutine: a dying creature lets go of
        // what it holds after dropObjectsDelay (0 to 0.5 s).
        private readonly Dictionary<Agent, float> _drops = new Dictionary<Agent, float>();
        // TOG FadeToDeath: the body sags on the blade, its weapons dropped now.
        public void FadeToDeath(EnemyBody body)
        {
            Entry entry = Find(body.Agent);
            if (entry == null) return;
            entry.Active.FadeToDeath(Mission.CurrentTime);
            if (EnemyPhysicsSettings.DropOnDeath && !_drops.ContainsKey(body.Agent)) _drops[body.Agent] = Mission.CurrentTime;
        }
        public void Die(EnemyBody body)
        {
            Entry entry = Find(body.Agent);
            if (entry == null) return;
            bool was = entry.Active.Dying;
            entry.Active.Die();
            if (!was && entry.Active.Dying && EnemyPhysicsSettings.DropOnDeath && !_drops.ContainsKey(body.Agent))
                _drops[body.Agent] = Mission.CurrentTime + MBRandom.RandomFloatRanged(0, .5f);
        }
        private readonly List<Agent> _dropped = new List<Agent>();
        private void DropDue()
        {
            if (_drops.Count == 0) return;
            _dropped.Clear();
            foreach (KeyValuePair<Agent, float> drop in _drops) if (Mission.CurrentTime >= drop.Value) _dropped.Add(drop.Key);
            foreach (Agent agent in _dropped)
            {
                _drops.Remove(agent);
                if (agent == null || !agent.IsActive()) continue;
                foreach (EquipmentIndex slot in new[] { agent.GetOffhandWieldedItemIndex(), agent.GetPrimaryWieldedItemIndex() })
                    if (slot != EquipmentIndex.None && !agent.Equipment[slot].IsEmpty)
                    {
                        PhysicsLog.Info("ACTIVE_RAGDOLL dying agent " + agent.Index + " drops " + (agent.Equipment[slot].Item?.StringId ?? "?"));
                        agent.DropItem(slot);
                    }
            }
        }
        public void ReleaseActive(EnemyBody body, int hand) => Find(body.Agent)?.Active.ReleaseHandle(hand);
        public bool PierceActive(EnemyBody body, int hand, sbyte bone, Vec3 bladePoint, Vec3 axis, bool axialLock,
            Vec3 localAxis, float angleLimit, MatrixFrame boneWorld, Vec3 contact)
        {
            Entry entry = Find(body.Agent);
            if (entry == null || !entry.Lease.OwnsPhysics || !entry.Active.Configured) return false;
            entry.Lease.KeepHeld(Mission.CurrentTime);
            return entry.Active.Pierce(hand, bone, bladePoint, axis, axialLock, localAxis, angleLimit, boneWorld, contact);
        }
        // Human handle used by B&S for the part the hand closed on.
        private static GrabHandle Classify(EnemyBody body, BodyPoint point)
        {
            string name = body.Skeleton.GetBoneName(point.Bone) ?? "";
            switch (point.Part)
            {
                case BoneBodyPartType.Head: return GrabHandle.Head;
                case BoneBodyPartType.Neck: return GrabHandle.Neck;
                case BoneBodyPartType.Legs: return GrabHandle.Legs;
                case BoneBodyPartType.ShoulderLeft:
                case BoneBodyPartType.ShoulderRight: return GrabHandle.Shoulder;
                case BoneBodyPartType.ArmLeft:
                case BoneBodyPartType.ArmRight: return name.EndsWith("hand") ? GrabHandle.Hand : GrabHandle.Wrist;
                default: return GrabHandle.Torso;
            }
        }
        // BrainModuleMelee parry recoil: recoilMinWeaponSpeed 1 m/s, pushLevel 2
        // against an enemy (full recoil, attack stopped), parryRecoilCooldown
        // 1.5 s (Brain_Human*.json).
        public const float RecoilMinWeaponSpeed = 1, ParryRecoilCooldown = 1.5f;
        private readonly Dictionary<Agent, float> _lastParry = new Dictionary<Agent, float>();
        public bool Parry(EnemyBody body, float weaponSpeed)
        {
            Agent agent = body?.Agent;
            if (agent == null || body.IsCorpse || !agent.IsActive() || !InteractionMath.IsFinite(weaponSpeed) ||
                weaponSpeed <= RecoilMinWeaponSpeed) return false;
            Agent.ActionStage stage = agent.GetCurrentActionStage(1);
            if (stage != Agent.ActionStage.AttackRelease && stage != Agent.ActionStage.AttackReady &&
                stage != Agent.ActionStage.AttackQuickReady) return false;
            float now = Mission.CurrentTime;
            // meleeModule.StopAttack on every parry: the swing ends in the
            // engine's own blocked recoil for it (act_release_overswing_1h ->
            // act_blocked_overswing_1h); act_none alone let the AI pick the
            // same attack straight back up.
            string attack = agent.GetCurrentAction(1).GetName();
            ActionIndexCache blocked = BlockedAction(attack);
            // B&S: the swinging weapon is a rigidbody and stops on the blade it
            // meets, then the creature recoils. The swing stops where the
            // blades met (the animation held still) for ParryStop, then the
            // blocked recoil plays from there. Switched at once, the recoil
            // blended over the rest of the swing: the blade seemed to finish
            // its blow through the player's.
            if (!_parryStops.ContainsKey(agent))
            {
                agent.SetCurrentActionSpeed(1, 0);
                _parryStops[agent] = new ParryStopState { Since = now, Until = now + ParryStop, Blocked = blocked, Attack = attack,
                    Action = agent.GetCurrentAction(1).Index, Progress = agent.GetCurrentActionProgress(1) };
            }
            // The recoil push only once per parryRecoilCooldown.
            if (_lastParry.TryGetValue(agent, out float last) && now - last <= ParryRecoilCooldown)
            {
                PhysicsLog.Info("PARRY agent=" + agent.Index + " attack " + attack + " -> " + (blocked.Index >= 0 ? blocked.GetName() : "act_none") + " (recoil cooling down)");
                return false;
            }
            _lastParry[agent] = now;
            _staggers[agent] = new Stagger { Until = now + .5f + PushReaction.Recoil(PushEffect.StaggerFull), Effect = PushEffect.StaggerFull };
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture, "PARRY agent={0} stage={1} weapon_speed={2:F2} time={3:F3} attack {4} -> {5}",
                agent.Index, stage, weaponSpeed, now, attack, blocked.Index >= 0 ? blocked.GetName() : "act_none"));
            return true;
        }
        // Held still ParryStop on the blade, the swing is then run backwards
        // for RewindSeconds over up to RewindProgress of its animation: the
        // blade leaves the way it came, away from the player's. The engine's
        // blocked recoil starts from its own pose, further along the swing,
        // and its blend carried the blade through the sword.
        public const float ParryStop = .08f, RewindSeconds = .25f, RewindProgress = .35f;
        private struct ParryStopState { public float Since, Until; public ActionIndexCache Blocked; public string Attack; public int Action; public float Progress; }
        private readonly Dictionary<Agent, ParryStopState> _parryStops = new Dictionary<Agent, ParryStopState>();
        private readonly List<Agent> _stopsDone = new List<Agent>();
        private void ParryStops()
        {
            if (_parryStops.Count == 0) return;
            float now = Mission.CurrentTime;
            _stopsDone.Clear();
            foreach (KeyValuePair<Agent, ParryStopState> pair in _parryStops)
            {
                Agent agent = pair.Key;
                ParryStopState stop = pair.Value;
                if (agent == null || !agent.IsActive()) { _stopsDone.Add(agent); continue; }
                // Another action took over (hit, death, the engine's own block).
                if (agent.GetCurrentAction(1).Index != stop.Action) { agent.SetCurrentActionSpeed(1, 1); _stopsDone.Add(agent); continue; }
                if (now < stop.Until) continue;
                float back = Math.Min(1, (now - stop.Until) / RewindSeconds);
                agent.SetCurrentActionProgress(1, Math.Max(0, stop.Progress - back * RewindProgress));
                if (back < 1 && stop.Progress - back * RewindProgress > 0) continue;
                agent.SetCurrentActionSpeed(1, 1);
                bool played = agent.SetActionChannel(1, ActionIndexCache.act_none, ignorePriority: true, blendInPeriod: .2f);
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "PARRY agent={0} held {1:F0} ms on the blade, swung back from {2:F2} to {3:F2} of {4}, then guard{5}",
                    agent.Index, ParryStop * 1000, stop.Progress, Math.Max(0, stop.Progress - RewindProgress), stop.Attack, played ? "" : " (refused)"));
                _stopsDone.Add(agent);
            }
            foreach (Agent agent in _stopsDone) _parryStops.Remove(agent);
        }
        private static ActionIndexCache BlockedAction(string attack)
        {
            if (string.IsNullOrEmpty(attack)) return ActionIndexCache.act_none;
            string rest = null;
            foreach (string prefix in new[] { "act_quick_release_", "act_release_", "act_ready_" })
                if (attack.StartsWith(prefix, StringComparison.Ordinal)) { rest = attack.Substring(prefix.Length); break; }
            if (rest == null) return ActionIndexCache.act_none;
            foreach (string candidate in new[] { rest, rest.Replace("_left_stance", ""), rest.Replace("_balanced", ""),
                rest.Replace("_left_stance", "").Replace("_balanced", "") })
            {
                ActionIndexCache action = ActionIndexCache.Create("act_blocked_" + candidate);
                if (action.Index >= 0) return action;
            }
            return ActionIndexCache.act_none;
        }
        public long PhysicsSteps => _clock.Running ? _clock.Steps : -1;
        public float PartMass(Agent agent, sbyte bone) => Find(agent)?.Active.PartMass(bone) ?? 5;
        public float BodyMass(Agent agent) => Find(agent)?.Active.TotalMass ?? 0;
        public bool IsDying(Agent agent) => Find(agent)?.Active.Dying ?? false;
        private Entry Find(Agent agent)
        {
            foreach (Entry entry in _entries) if (entry.Agent == agent && entry.Lease.Occupied) return entry;
            return null;
        }

        // Context for the B&S human push table: idle without a target, attack
        // while an attack is readied or released, guarding otherwise; a recent
        // stagger escalates the next push.
        private readonly Dictionary<Agent, Stagger> _staggers = new Dictionary<Agent, Stagger>();
        private readonly Dictionary<Agent, float> _knockouts = new Dictionary<Agent, float>();
        private struct Stagger { public float Until; public PushEffect Effect; }
        public PushEffect EvaluatePush(EnemyBody body, BodyPoint point, Vec3 velocity, PushMaterial material, bool canStun = true)
        {
            Agent agent = body.Agent;
            if (agent == null || !agent.IsActive()) return PushEffect.None;
            Entry held = Find(agent);
            float now = Mission.CurrentTime;
            bool knockout = canStun && EnemyPhysicsSettings.Knockout &&
                Dismemberment.KnocksOut(material, BodyPenetration.ZoneFor(body, point), velocity.Length);
            if (knockout) _knockouts[agent] = now;
            if (held != null && held.Active.Phase == ActiveRagdollPhase.Destabilized)
            {
                // Hit again while down: the stun is extended (StunWakeTime).
                if (knockout && held.Active.KnockedOut)
                {
                    held.Active.KnockOut(now, Dismemberment.KnockoutSeconds);
                    held.Lease.ExtendUntil(now + Dismemberment.KnockoutSeconds + 7);
                }
                return PushEffect.None;
            }
            PushContext context = ContextOf(agent, now);
            PushBodyZone zone = point.Part == BoneBodyPartType.Head || point.Part == BoneBodyPartType.Neck ||
                point.Part == BoneBodyPartType.Legs ? PushBodyZone.HeadOrLeg : PushBodyZone.Torso;
            // DamageModifier_Punch has its own push speeds per armour.
            int level = material == PushMaterial.Punch
                ? PunchImpact.PushLevel(velocity.Length, PunchImpact.For(BodyPenetration.MaterialFor(agent, point.Part)))
                : PushReaction.Level(material, velocity.Length);
            PushEffect effect = PushReaction.HitEffect(level, zone, context);
            // StunCreature: a stunned creature goes limp whatever its stance.
            if (knockout) effect = PushEffect.Destabilize;
            if (effect == PushEffect.Destabilize && (!Available || !EnemyPhysicsSettings.ActiveRagdoll))
                effect = PushEffect.StaggerFull;
            if (effect != PushEffect.None && effect != PushEffect.Destabilize)
                _staggers[agent] = new Stagger { Until = now + .5f + PushReaction.Recoil(effect), Effect = effect };
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture, "PUSH material={0} speed={1:F2} level={2} zone={3} context={4} effect={5}",
                    material, velocity.Length, level, zone, context, effect));
            return effect;
        }
        // BrainModuleDefense.OnWeaponCollision: a weapon with transferPushToBlocker
        // (maces, hammers, long axes and polearms in the catalog; never swords)
        // passes its push to the creature blocking it, at the weapon's push
        // level for the impact speed, kept within minTransferredPush 2 and
        // maxBlockTransferredPushLevel 4, as a torso hit. A destabilized
        // blocker takes the impulse and lets go of what blocked
        // (disarmOnBlockDestabilize).
        public bool BlockTransfer(EnemyBody body, Vec3 velocity, PushMaterial material, float hitMass)
        {
            Agent agent = body?.Agent;
            if (agent == null || body.IsCorpse || !agent.IsActive()) return false;
            if (material != PushMaterial.Blunt && material != PushMaterial.BluntLight && material != PushMaterial.SlashAxe &&
                material != PushMaterial.SlashHeavy) return false;
            int level = Math.Min(4, PushReaction.Level(material, velocity.Length));
            if (level < 2) return false;
            float now = Mission.CurrentTime;
            PushContext context = ContextOf(agent, now);
            PushEffect effect = PushReaction.HitEffect(level, PushBodyZone.Torso, context);
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture, "BLOCK_PUSH agent={0} material={1} speed={2:F2} level={3} context={4} effect={5}",
                agent.Index, material, velocity.Length, level, context, effect));
            if (effect == PushEffect.None) return true;
            if (effect != PushEffect.Destabilize)
                _staggers[agent] = new Stagger { Until = now + .5f + PushReaction.Recoil(effect), Effect = effect };
            // The blow reaches the arm that blocked (the off hand).
            BodyPoint arm = default; bool found = false;
            for (int i = 0; i < body.Count; i++)
                if ((body.Skeleton.GetBoneName(body.Points[i].Bone) ?? "") == "l_hand") { arm = body.ReadContact(body.Points[i].Bone); found = true; break; }
            if (found && EnemyPhysicsSettings.ActiveRagdoll) ActiveImpact(body, arm, velocity, effect, hitMass, false);
            if (effect == PushEffect.Destabilize)
            {
                EquipmentIndex off = agent.GetOffhandWieldedItemIndex();
                if (off != EquipmentIndex.None && !agent.Equipment[off].IsEmpty) agent.DropItem(off);
            }
            return true;
        }
        // B&S: a creature's shield is an item its physical hand holds, so a
        // blade driven into it pushes the shield and the arm back or aside,
        // and the arm's pins bring the guard back. The drive depth becomes the
        // grip's force (spring 2500 N/m, at most 2000 N) on the shield hand.
        public void ShieldPress(EnemyBody body, Vec3 depth)
        {
            if (body == null || body.IsCorpse || !Available || !EnemyPhysicsSettings.ActiveRagdoll || !EnemyPhysicsSettings.ShieldDeflect) return;
            Entry entry = Find(body.Agent);
            if (entry == null)
            {
                // A fighting enemy keeps its guard animated: leased for the
                // push, its AI stood paused a second on every blow it blocked.
                if (body.Agent.Controller == AgentControllerType.AI && !body.Agent.IsPaused) return;
                if (!KeepHeld(body, true)) return;
                entry = Find(body.Agent);
                if (entry == null) return;
            }
            if (entry.Lease.Phase == LivingRagdollPhase.Recovering) return;
            entry.Lease.KeepHeld(Mission.CurrentTime, .3f);
            sbyte hand = -1;
            for (int i = 0; i < body.Count; i++)
                if ((body.Skeleton.GetBoneName(body.Points[i].Bone) ?? "") == "l_hand") { hand = body.Points[i].Bone; break; }
            if (hand < 0) return;
            // The hand is a ~1 kg body: the grip's stiffness is held to the
            // drive's natural-frequency cap for its mass, or 2000 N threw it.
            float mass = Math.Max(.5f, entry.Active.PartMass(hand));
            float k = Math.Min(RagdollPinDrive.HandSpring, mass * RagdollPinDrive.HandNaturalFrequency * RagdollPinDrive.HandNaturalFrequency);
            Vec3 force = InteractionMath.ClampMagnitude(depth * k, Math.Min(RagdollPinDrive.HandMaxForce, mass * 60));
            entry.Active.Press(hand, force, Mission.CurrentTime);
        }
        public PushContext ContextOf(Agent agent, float now)
        {
            if (_staggers.TryGetValue(agent, out Stagger stagger) && stagger.Until > now)
                return stagger.Effect == PushEffect.StaggerFull ? PushContext.StaggerFull : PushContext.Stagger;
            Agent.ActionStage stage = agent.GetCurrentActionStage(1);
            bool attacking = stage == Agent.ActionStage.AttackReady || stage == Agent.ActionStage.AttackQuickReady ||
                stage == Agent.ActionStage.AttackRelease;
            if (attacking) return agent.Velocity.z > 1.5f ? PushContext.AttackJump : PushContext.Attack;
            // Brain.State Idle/Patrol: no controlling AI (a sandbox dummy) or
            // no target. A body the physics holds is paused, not idle: the
            // pause is the physics', its AI still has its target.
            return agent.Controller != AgentControllerType.AI || agent.GetTargetAgent() == null ? PushContext.Idle : PushContext.Combat;
        }
        public void ReleaseAll()
        {
            foreach (Entry entry in _entries)
                if (entry.Lease.Occupied) SafeRelease(entry);
        }
        private void SafeRelease(Entry entry)
        {
            try { entry.Lease.RequestRelease(Mission.CurrentTime); }
            catch (Exception ex)
            {
                _failed = true;
                try { entry.Lease.EmergencyStop(); }
                catch (Exception cleanup) { PhysicsLog.Error("Living ragdoll emergency cleanup failed.", cleanup); }
                finally { entry.Clear(); }
                PhysicsLog.Error("Living ragdoll release failed; backend stopped for this mission.", ex);
            }
        }
        private int _releasesThisTick;
        // Actions of released agents, one second later, for the fall pose.
        private readonly Dictionary<Agent, float> _released = new Dictionary<Agent, float>();
        private readonly List<Agent> _releasedDone = new List<Agent>();
        private void TraceReleased()
        {
            if (_released.Count == 0) return;
            _releasedDone.Clear();
            foreach (KeyValuePair<Agent, float> pair in _released)
                if (Mission.CurrentTime - pair.Value > 1) _releasedDone.Add(pair.Key);
            foreach (Agent agent in _releasedDone)
            {
                _released.Remove(agent);
                if (agent.IsActive())
                {
                    Skeleton skeleton = agent.AgentVisuals?.GetSkeleton();
                    PhysicsLog.Info("ACTIVE_RAGDOLL after_release agent=" + agent.Index + " " + Entry.Actions(agent) +
                        string.Format(CultureInfo.InvariantCulture, " z_above_ground={0:F2} ragdoll={1} frozen={2} tracked={3} eligible={4} controller={5}",
                            agent.Position.z - Mission.Scene.GetGroundHeightAtPosition(agent.Position),
                            skeleton?.GetCurrentRagdollState().ToString() ?? "?", skeleton?.IsFrozen().ToString() ?? "?",
                            EnemyPhysicsRuntime.Tracks(agent), EnemyBody.EligibleReason(agent, Mission.MainAgent) ?? "yes", agent.Controller));
                }
            }
        }
        // Animation restart after a release. An AI agent starts walking or
        // fighting and its skeleton is re-animated; one without a controller
        // (a sandbox dummy) starts nothing and kept the last physical pose.
        private readonly Dictionary<Agent, float> _reanimate = new Dictionary<Agent, float>();
        private readonly List<Agent> _reanimateDone = new List<Agent>();
        private readonly HashSet<Agent> _reanimateLogged = new HashSet<Agent>();
        private const float ReanimateSeconds = 3;
        private void Reanimate()
        {
            if (_reanimate.Count == 0) return;
            _reanimateDone.Clear();
            foreach (KeyValuePair<Agent, float> pair in _reanimate) _reanimateDone.Add(pair.Key);
            float now = Mission.CurrentTime;
            foreach (Agent agent in _reanimateDone)
            {
                if (!agent.IsActive()) { _reanimate.Remove(agent); _reanimateLogged.Remove(agent); continue; }
                // Not waiting for the lease to finish letting go: the skeleton
                // is animated as soon as its ragdoll is off.
                bool last = now - _reanimate[agent] > ReanimateSeconds;
                Skeleton skeleton = agent.AgentVisuals?.GetSkeleton();
                if (skeleton == null) { _reanimate.Remove(agent); continue; }
                if (skeleton.GetCurrentRagdollState() == RagdollState.Disabled)
                {
                    bool frozen = skeleton.IsFrozen();
                    // The corpse ragdoll freezes the skeleton once the body
                    // settles; a living agent handed back must animate again.
                    if (frozen) skeleton.Freeze(false);
                    skeleton.TickAnimationsAndForceUpdate(.001f, agent.AgentVisuals.GetGlobalFrame(), false);
                    if (_reanimateLogged.Add(agent))
                        PhysicsLog.Info("ACTIVE_RAGDOLL reanimate agent=" + agent.Index + " frozen_before=" + frozen +
                            " frozen_after=" + skeleton.IsFrozen());
                }
                if (last)
                {
                    PhysicsLog.Info("ACTIVE_RAGDOLL reanimate done agent=" + agent.Index + " frozen=" + skeleton.IsFrozen() +
                        " ragdoll=" + skeleton.GetCurrentRagdollState());
                    _reanimate.Remove(agent); _reanimateLogged.Remove(agent);
                }
            }
        }
        public override void OnMissionTick(float dt)
        {
            long perf = Perf.Start();
            try { TickCore(dt); }
            finally { Perf.Stop(Perf.Section.Ragdoll, perf); }
        }
        private void TickCore(float dt)
        {
            long started = Stopwatch.GetTimestamp();
            try { Reanimate(); } catch (Exception ex) { _reanimate.Clear(); PhysicsLog.Error("Re-animating a released agent failed.", ex); }
            try { DropDue(); } catch (Exception ex) { _drops.Clear(); PhysicsLog.Error("A dying enemy could not drop its weapons.", ex); }
            try { ParryStops(); } catch (Exception ex) { _parryStops.Clear(); PhysicsLog.Error("A parried swing could not recoil.", ex); }
            try { AlignRises(); } catch (Exception ex) { _rises.Clear(); PhysicsLog.Error("Aligning an animated rise failed.", ex); }
            try { ApplyBlends(); } catch (Exception ex) { _blends.Clear(); PhysicsLog.Error("Blending a released pose failed.", ex); }
            try { ReleaseWatch.Tick(); } catch (Exception ex) { PhysicsLog.Error("Release watch failed.", ex); }
            int collections = GC.CollectionCount(0);
            _releasesThisTick = 0;
            try { TraceReleased(); } catch (Exception) { _released.Clear(); }
            if ((EnemyPhysicsSettings.RuntimeProbe && !EnemyPhysicsSettings.WoundProbe &&
                (!EnemyPhysicsSettings.CadenceProbe || EnemyPhysicsSettings.CadenceRagdoll)) || EnemyPhysicsSettings.LivingRagdolls)
                NativeRagdollBridge.BindMissionTick(Mission);
            if ((EnemyPhysicsSettings.ActiveBalance || EnemyPhysicsSettings.ActiveRagdoll) && EnemyPhysicsSettings.Fighting(Mission.Mode))
                _clock.Ensure(Mission);
            if (EnemyPhysicsSettings.RuntimeProbe && !EnemyPhysicsSettings.WoundProbe &&
                (!EnemyPhysicsSettings.CadenceProbe || EnemyPhysicsSettings.CadenceRagdoll))
            {
                try
                {
                    StartProbe(); FollowProbe();
                }
                catch (Exception ex)
                {
                    _probeStarted = _probeFinished = _failed = true;
                    if (_probeEntry != null) SafeRelease(_probeEntry);
                    PhysicsLog.Error("PHYSICS_PROBE result=FAIL reason=managed_exception", ex);
                }
            }
            foreach (Entry entry in _entries)
            {
                if (!entry.Lease.Occupied) continue;
                try
                {
                    if (entry == _probeEntry && !_probeFinished) ObserveProbe(entry);
                    if (entry.ReleasePending && entry.Alive && entry.NativeActive) entry.EndNative();
                    entry.Lease.Tick(Mission.CurrentTime);
                    if (entry.Lease.OwnsPhysics && entry.NativeActive && EnemyPhysicsSettings.ActiveRagdoll)
                    {
                        entry.Active.Tick(entry.Agent, entry.Skeleton, Mission, _clock);
                        if (entry.Active.RiseRequested)
                        {
                            entry.Active.RiseRequested = false;
                            if (TryAnimatedRise(entry)) continue;
                        }
                        if (entry.Active.ImpactBone >= 0) RagdollCollision(entry);
                        if (entry.Active.WantsHold) entry.Lease.KeepHeld(Mission.CurrentTime);
                        if (entry == _probeEntry && !_probeFinished) ObserveActiveProbe(entry);
                        else if (!_probeFinished) ObserveProbeLoad(entry);
                        if (entry.Active.ReadyToRelease) ReleaseRecovered(entry);
                        else if (entry.Active.StuckOffPose)
                        {
                            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                                "ACTIVE_RAGDOLL stuck_off_pose pose_error={0:F3} action={1}", entry.Active.LastPoseError,
                                entry.Active.LastPoseError < .6f ? "release" : "destabilize"));
                            if (entry.Active.LastPoseError < .6f) ReleaseRecovered(entry);
                            else entry.Active.Destabilize();
                        }
                    }
                    else if (entry.Lease.OwnsPhysics && entry.NativeActive)
                    {
                        entry.Balance.Tick(entry.Agent, entry.Skeleton, Mission, dt, _clock);
                        if (entry == _probeEntry && !_probeFinished && EnemyPhysicsSettings.ActiveBalance)
                        {
                            _probeBalanceTicks = entry.Balance.AppliedTicks;
                            _probeBalanceSkipped = entry.Balance.SkippedTicks;
                            if (_probeBalanceStepsAt < 0 && entry.Balance.AppliedTicks > 0) _probeBalanceStepsAt = _clock.Steps;
                            if (entry.Balance.Phase == BodyBalancePhase.Supporting || entry.Balance.Phase == BodyBalancePhase.Falling)
                                _probeBalanceStepsEnd = _clock.Steps;
                            _probePeakSupport = Math.Max(_probePeakSupport, entry.Balance.PeakForce);
                            _probeBalanceFell |= _probeBalanceCaptured && entry.Balance.Phase != BodyBalancePhase.Supporting;
                        }
                    }
                    else if (entry.Lease.Phase != LivingRagdollPhase.Activating) entry.Balance.Clear();
                    if (entry.ImpactPending)
                    {
                        if (Mission.CurrentTime > entry.ImpactDeadline || !entry.Lease.Occupied) entry.ImpactPending = false;
                        else if (entry.Lease.OwnsPhysics && entry.NativeActive)
                        {
                            entry.Agent.ApplyForceOnRagdoll(entry.ImpactBone, in entry.ImpactForce);
                            entry.ImpactPending = false;
                        }
                    }
                    if (entry.Lease.Faulted)
                    {
                        _failed = true;
                        PhysicsLog.Info("Living ragdoll recovery did not finish in time; backend stopped for this mission.");
                    }
                    if (!entry.Lease.Occupied)
                    {
                        if (entry == _probeEntry && !_probeFinished) FinishProbe(entry);
                        entry.Clear();
                    }
                }
                catch (Exception ex)
                {
                    _failed = true;
                    if (entry == _probeEntry && !_probeFinished)
                    {
                        _probeFinished = true;
                        PhysicsLog.Info("PHYSICS_PROBE result=FAIL reason=managed_native_transition_exception");
                    }
                    try { entry.Lease.EmergencyStop(); }
                    catch (Exception cleanup) { PhysicsLog.Error("Living ragdoll emergency cleanup failed.", cleanup); }
                    finally { entry.Clear(); }
                    PhysicsLog.Error("Living ragdoll recovery failed; backend stopped for this mission.", ex);
                }
            }
            LastTickCpuMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            if (_probeEntry != null && !_probeFinished && _probeEntry.Active.Configured)
            {
                bool activation = false;
                foreach (Entry entry in _entries) activation |= entry.Lease.Occupied && entry.Active.ConfiguredThisTick;
                if (activation) _probeCpuActivationMax = Math.Max(_probeCpuActivationMax, LastTickCpuMilliseconds);
                else
                {
                    _probeCpuTotal += LastTickCpuMilliseconds; _probeCpuTicks++;
                    if (LastTickCpuMilliseconds > _probeCpuMax && LastTickCpuMilliseconds > 1)
                    {
                        int occupied = 0, phases = 0;
                        foreach (Entry entry in _entries)
                        {
                            if (!entry.Lease.Occupied) continue;
                            occupied++;
                            phases |= 1 << (int)entry.Active.Phase;
                        }
                        PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                            "ACTIVE_PROBE spike ms={0:F3} time={1:F3} leases={2} phases_mask={3} releases_this_tick={4} gc0_during_tick={5}",
                            LastTickCpuMilliseconds, Mission.CurrentTime, occupied, phases, _releasesThisTick, GC.CollectionCount(0) - collections));
                    }
                    _probeCpuMax = Math.Max(_probeCpuMax, LastTickCpuMilliseconds);
                }
            }
        }
        // B&S ends the get-up by handing the body back to the animator where it
        // stands. Bannerlord's animation restarts at the agent position, so the
        // agent is first moved under the hips when the body travelled.
        // The pins only pulled a lying body into its standing pose, which
        // looked forced. Bannerlord's knockdown rise (act_strike_fall_*_back_rise,
        // the _continue part starting from the ground) gets up from the back;
        // the agent is placed and turned so the animation's lying body lies
        // where the ragdoll does, then the alignment is corrected from the
        // animated pose on the next ticks.
        private static readonly ActionIndexCache RiseAction = ActionIndexCache.Create("act_strike_fall_back_back_rise_continue");
        private sealed class Rise { public float Started; public Vec3 Pelvis, BodyUp; public int Step; }
        private readonly Dictionary<Agent, Rise> _rises = new Dictionary<Agent, Rise>();
        private bool TryAnimatedRise(Entry entry)
        {
            if (!EnemyPhysicsSettings.AnimatedRise || entry.Agent.MountAgent != null || !entry.Active.LyingOnBack(out Vec3 bodyUp))
                return false;
            Vec3 pelvis = entry.Active.PelvisPosition;
            Vec3 forward = -bodyUp; forward.z = 0;
            if (!InteractionMath.IsFinite(pelvis) || forward.Length < .2f || !OnNavmesh(pelvis)) return false;
            forward.Normalize();
            Agent agent = entry.Agent;
            // The rise's first frame, read from the animation itself: the agent
            // is turned and placed so its lying body is where the ragdoll lies
            // before the switch (two corrections after it showed as pops).
            Vec3 place = pelvis;
            bool aligned = false;
            int anim = MBActionSet.GetAnimationIndexOfAction(agent.ActionSet, RiseAction);
            if (anim >= 0)
            {
                Vec3 pl = agent.GetBoneEntitialFrameAtAnimationProgress(0, anim, 0).origin;
                Vec3 hl = agent.GetBoneEntitialFrameAtAnimationProgress(agent.Monster.HeadLookDirectionBoneIndex, anim, 0).origin;
                Vec2 a = (hl - pl).AsVec2, b = bodyUp.AsVec2;
                if (InteractionMath.IsFinite(pl) && InteractionMath.IsFinite(hl) && a.Length > .2f && b.Length > .2f)
                {
                    float phi = (float)(Math.Atan2(b.y, b.x) - Math.Atan2(a.y, a.x));
                    float c = (float)Math.Cos(phi), sn = (float)Math.Sin(phi);
                    forward = new Vec3(-sn, c, 0);
                    place = new Vec3(pelvis.x - (pl.x * c - pl.y * sn), pelvis.y - (pl.x * sn + pl.y * c), pelvis.z);
                    aligned = OnNavmesh(place);
                    if (!aligned) { place = pelvis; forward = -bodyUp; forward.z = 0; forward.Normalize(); }
                }
            }
            Vec3 ground = new WorldPosition(Mission.Scene, place).GetGroundVec3();
            if (!InteractionMath.IsFinite(ground)) return false;
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "ACTIVE_RAGDOLL animated_rise agent={0} action={1} body_up=({2:F2},{3:F2},{4:F2}) placed_from_animation={5}",
                agent.Index, "act_strike_fall_back_back_rise_continue", bodyUp.x, bodyUp.y, bodyUp.z, aligned));
            _released[agent] = Mission.CurrentTime;
            _releasesThisTick++;
            SafeRelease(entry);
            agent.TeleportToPosition(ground);
            entry.Rehold();
            agent.LookDirection = forward;
            agent.SetMovementDirection(forward.AsVec2);
            agent.SetActionChannel(0, RiseAction, ignorePriority: true);
            // Placed from the animation, no correction follows.
            _rises[agent] = new Rise { Started = Mission.CurrentTime, Pelvis = pelvis, BodyUp = bodyUp, Step = aligned ? 100 : 0 };
            _reanimate[agent] = Mission.CurrentTime;
            return true;
        }
        private readonly List<Agent> _risesDone = new List<Agent>();
        private void AlignRises()
        {
            if (_rises.Count == 0) return;
            _risesDone.Clear();
            foreach (KeyValuePair<Agent, Rise> pair in _rises)
            {
                Agent agent = pair.Key; Rise rise = pair.Value;
                if (!agent.IsActive() || Mission.CurrentTime - rise.Started > 4) { _risesDone.Add(agent); continue; }
                if (HasLease(agent)) continue;
                Skeleton skeleton = agent.AgentVisuals?.GetSkeleton();
                if (skeleton == null || skeleton.GetCurrentRagdollState() != RagdollState.Disabled) continue;
                rise.Step++;
                if (rise.Step != 2 && rise.Step != 4) continue;
                MatrixFrame frame = agent.AgentVisuals.GetGlobalFrame();
                Vec3 pelvis = frame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(0)).origin;
                Vec3 head = frame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(agent.Monster.HeadLookDirectionBoneIndex)).origin;
                if (rise.Step == 2)
                {
                    // Turn: the animated body axis onto the ragdoll's.
                    Vec2 animated = (head - pelvis).AsVec2, wanted = rise.BodyUp.AsVec2;
                    if (animated.Length > .2f && wanted.Length > .2f)
                    {
                        float angle = (float)(Math.Atan2(wanted.y, wanted.x) - Math.Atan2(animated.y, animated.x));
                        Vec3 look = agent.LookDirection; look.z = 0;
                        float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
                        Vec3 turned = new Vec3(look.x * c - look.y * s, look.x * s + look.y * c, 0);
                        if (turned.Length > .1f) { turned.Normalize(); agent.LookDirection = turned; agent.SetMovementDirection(turned.AsVec2); }
                    }
                }
                else
                {
                    // Slide: the animated pelvis onto the ragdoll's.
                    Vec3 shift = rise.Pelvis - pelvis; shift.z = 0;
                    Vec3 target = agent.Position + shift;
                    if (shift.Length > .05f && shift.Length < 2 && OnNavmesh(target))
                    {
                        agent.TeleportToPosition(new WorldPosition(Mission.Scene, target).GetGroundVec3());
                        if (agent.Controller != AgentControllerType.AI) agent.SetTargetPosition(agent.Position.AsVec2);
                    }
                    PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "ACTIVE_RAGDOLL animated_rise aligned agent={0} shift_m={1:F2} action={2}",
                        agent.Index, shift.Length, Entry.Actions(agent)));
                }
            }
            foreach (Agent agent in _risesDone) _rises.Remove(agent);
        }
        public static void ClearHitReaction(Agent agent)
        {
            for (int channel = 0; channel < 2; channel++)
            {
                Agent.ActionCodeType type = agent.GetCurrentActionType(channel);
                if (type >= Agent.ActionCodeType.StrikeLight && type <= Agent.ActionCodeType.StrikeKnockBack)
                    agent.SetActionChannel(channel, ActionIndexCache.act_none, ignorePriority: true);
            }
        }
        private void ReleaseRecovered(Entry entry)
        {
            bool moved = entry.Active.Moved;
            Vec3 position = entry.Active.RecoveryPosition;
            bool teleported = false;
            // On the ground under the body's root: the root frame can sit at
            // hip height (0.85 m up in the logs), and a teleport there left the
            // agent in the air, drawn low until it landed half a second later.
            if (InteractionMath.IsFinite(position))
            {
                Vec3 ground = new WorldPosition(Mission.Scene, position).GetGroundVec3();
                if (InteractionMath.IsFinite(ground) && Math.Abs(ground.z - position.z) < 2) position = ground;
            }
            float distance = (position - entry.Agent.Position).AsVec2.Length;
            bool navmesh = InteractionMath.IsFinite(position) && OnNavmesh(position);
            // Agent.CanTeleport only reflects the deployment-phase order gate;
            // SetPosition itself is valid for a dismounted agent in battle.
            // Only onto a walkable navmesh face, so a body that slid down a
            // slope is never placed inside geometry.
            // A hit reaction the blow started under the physical body plays from
            // its middle at the switch: back to the idle instead.
            ClearHitReaction(entry.Agent);
            // A moved body, or an agent the blow's knock-back walked off its
            // body (the ~1 m seen at every release: the switch jumped there).
            if ((moved || distance > .25f) && entry.Agent.MountAgent == null && InteractionMath.IsFinite(position) &&
                distance < 30 && navmesh)
            {
                entry.Agent.TeleportToPosition(position); teleported = true;
                // A body turned by a grab keeps its new facing.
                Vec3 forward = entry.Active.RecoveryForward; forward.z = 0;
                if (entry.Active.Turned && forward.Length > .1f)
                {
                    forward.Normalize();
                    entry.Agent.LookDirection = forward;
                    entry.Agent.SetMovementDirection(forward.AsVec2);
                }
            }
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "ACTIVE_RAGDOLL release time={0:F3} pose_error={1:F3} moved={2} teleported={3} distance_m={4:F2} navmesh={5} {6}",
                Mission.CurrentTime, entry.Active.LastPoseError, moved, teleported, distance, navmesh, Entry.Actions(entry.Agent)));
            // The body comes back on its feet: whatever the engine started
            // while it was physical (a fall loop for an agent it saw leave
            // the ground) must not keep playing.
            if (ActiveRagdollBody.Airborne(entry.Agent))
            {
                entry.Agent.SetActionChannel(0, ActionIndexCache.act_none, ignorePriority: true);
                entry.Agent.SetActionChannel(1, ActionIndexCache.act_none, ignorePriority: true);
                PhysicsLog.Info("ACTIVE_RAGDOLL release cleared an airborne action.");
            }
            _released[entry.Agent] = Mission.CurrentTime;
            _reanimate[entry.Agent] = Mission.CurrentTime;
            if (entry == _probeEntry) { _probeActiveReleased = true; _probeTeleported = teleported || !moved; _probeReleaseError = entry.Active.LastPoseError; }
            _releasesThisTick++;
            SafeRelease(entry);
        }
        private void RagdollCollision(Entry entry)
        {
            ActiveRagdollBody body = entry.Active;
            float damage = RagdollImpact.Damage(body.ImpactSpeed) * PenetrationDamage.HealthScale(entry.Agent.HealthLimit);
            sbyte bone = body.ImpactBone;
            BoneBodyPartType part = entry.Agent.AgentVisuals.GetBoneTypeData(bone).BodyPartType;
            Agent victim = entry.Agent;
            float lost = PhysicalHit.ApplyRagdollImpact(body.LastPusher, victim, bone, part, body.ImpactPoint, damage);
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "RAGDOLL_IMPACT agent={0} bone={1} part={2} speed={3:F2} damage={4:F2} health_lost={5:F2} time={6:F3}",
                    victim.Index, bone, part, body.ImpactSpeed, damage, lost, Mission.CurrentTime));
            if (entry == _probeEntry) { _probeImpacts++; _probeImpactLoss += lost; }
        }
        private int _probeImpacts;
        private float _probeImpactLoss;
        private bool OnNavmesh(Vec3 position)
        {
            var face = new PathFaceRecord(-1, -1, -1);
            Mission.Scene.GetNavMeshFaceIndex(ref face, position, true);
            return face.IsValid();
        }
        private bool _probePushed, _probeActiveReleased, _probeTeleported, _probeSawDestabilized, _probeSawStandUp, _probeSawRecovered;
        private float _probeReleaseError, _probeStandingError, _probeMinPelvis = float.MaxValue;
        private int _probeActiveSteps, _probeCpuTicks;
        private double _probeCpuTotal, _probeCpuMax, _probeCpuActivationMax;
        // Extra stationary enemies that run the same push cycle for the CPU
        // measurement; only the main probe entry is judged.
        private readonly HashSet<Entry> _probeLoad = new HashSet<Entry>();
        private readonly HashSet<Entry> _probeLoadPushed = new HashSet<Entry>();
        private void StartProbeLoad(Agent main)
        {
            int wanted = Math.Min(EnemyPhysicsSettings.ActiveProbeBodies, _entries.Length) - 1;
            foreach (Agent agent in Mission.Agents)
            {
                if (wanted <= 0) break;
                if (agent == main || !EnemyBody.Eligible(agent, Mission.MainAgent) || !agent.IsActive() || agent.IsPaused ||
                    !agent.IsAIControlled || agent.Velocity.Length > 3 || (agent.Position - Mission.MainAgent.Position).Length < 20) continue;
                Skeleton skeleton = agent.AgentVisuals?.GetSkeleton();
                if (skeleton == null || skeleton.GetCurrentRagdollState() != RagdollState.Disabled) continue;
                foreach (Entry entry in _entries)
                {
                    if (entry.Lease.Occupied) continue;
                    entry.Agent = agent; entry.Skeleton = skeleton; entry.Mission = Mission;
                    if (!NativeRagdollBridge.Preflight(agent, Mission, skeleton) || !entry.Lease.Begin(entry, Mission.CurrentTime, LeaseSeconds))
                    { entry.Clear(); break; }
                    entry.Lease.KeepHeld(Mission.CurrentTime, .6f);
                    _probeLoad.Add(entry); wanted--;
                    break;
                }
            }
            PhysicsLog.Info("ACTIVE_PROBE load bodies=" + (_probeLoad.Count + 1));
        }
        private void ObserveProbeLoad(Entry entry)
        {
            if (!_probeLoad.Contains(entry) || _probeLoadPushed.Contains(entry) || !entry.Active.Configured ||
                entry.Active.Phase != ActiveRagdollPhase.Standing || Mission.CurrentTime - _probeBegan < 1.5f) return;
            _probeLoadPushed.Add(entry);
            Vec3 back = -entry.Agent.LookDirection; back.z = 0;
            if (back.LengthSquared < 1e-4f) back = Vec3.Forward;
            entry.Active.Destabilize();
            entry.Active.QueuePush(entry.Agent.Monster.SpineUpperBoneIndex, back.NormalizedCopy() * (7 * 2f * 90));
        }
        private void ObserveActiveProbe(Entry entry)
        {
            ActiveRagdollBody body = entry.Active;
            _probeActiveSteps = body.AppliedSteps;
            _probeSawDestabilized |= body.Phase == ActiveRagdollPhase.Destabilized;
            _probeSawStandUp |= body.Phase == ActiveRagdollPhase.StandingUp;
            _probeSawRecovered |= body.Phase == ActiveRagdollPhase.Recovered;
            if (body.Configured) _probeMinPelvis = Math.Min(_probeMinPelvis, ProbePelvis(entry).z - _probeOrigin.z);
            SampleAnimationChannel(entry);
            if (EnemyPhysicsSettings.ActiveProbeGrab) { ProbeGrab(entry); return; }
            if (!_probePushed && body.Configured && body.Phase == ActiveRagdollPhase.Standing &&
                Mission.CurrentTime - _probeBegan > 1.5f)
            {
                _probePushed = true;
                _probeStandingError = body.MaxStandingError;
                // A level 2 punch (B&S DamageModifier_Punch: above 6 m/s) from
                // the agent's front, which destabilizes an idle human.
                Vec3 back = -entry.Agent.LookDirection; back.z = 0;
                if (back.LengthSquared < 1e-4f) back = Vec3.Forward;
                back = back.NormalizedCopy() * 7;
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "ACTIVE_PROBE push time={0:F3} standing_max_error_m={1:F3} effect={2}",
                    Mission.CurrentTime, _probeStandingError,
                    PushReaction.HitEffect(PushReaction.Level(PushMaterial.Punch, back.Length), PushBodyZone.Torso, PushContext.Idle)));
                entry.Active.Destabilize();
                entry.Active.QueuePush(entry.Agent.Monster.SpineUpperBoneIndex, back * (2f * 90));
            }
        }
        // A virtual hand closes on the neck after 1 s of standing, then rises
        // 40 cm over one second. Interactable_HumanHeadNeck lifts the root by
        // more than 5 cm and must make the creature fall (liftBehaviour Fall).
        private Vec3 _probeHand;
        private bool _probeGrabbing, _probeGrabOutcome;
        private void ProbeGrab(Entry entry)
        {
            ActiveRagdollBody body = entry.Active;
            if (!body.Configured) return;
            float t = Mission.CurrentTime - _probeBegan;
            sbyte neck = -1;
            for (sbyte b = 0; b < entry.Skeleton.GetBoneCount() && neck < 0; b++)
                if ((entry.Skeleton.GetBoneName(b) ?? "").Contains("neck")) neck = b;
            if (neck < 0) return;
            MatrixFrame frame = entry.Agent.AgentVisuals.GetEntity().GetGlobalFrame();
            MatrixFrame boneWorld = frame.TransformToParent(entry.Skeleton.GetBoneEntitialFrameWithIndex(neck));
            if (!_probeGrabbing && !_probePushed && t > 1)
            {
                _probeGrabbing = _probePushed = true;
                _probeStandingError = body.MaxStandingError;
                _probeHand = boneWorld.origin;
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "ACTIVE_PROBE grab neck time={0:F3} standing_max_error_m={1:F3}", Mission.CurrentTime, _probeStandingError));
            }
            if (!_probeGrabbing) return;
            Vec3 hand = _probeHand + Vec3.Up * Math.Min(.4f, Math.Max(0, t - 1) * .4f);
            if (body.LastGrabOutcome != GrabOutcome.Hold) _probeGrabOutcome = true;
            if (body.Phase == ActiveRagdollPhase.Destabilized || t > 3)
            {
                _probeGrabbing = false;
                body.ReleaseHandle(0);
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "ACTIVE_PROBE grab release time={0:F3} outcome={1} phase={2}", Mission.CurrentTime, body.LastGrabOutcome, body.Phase));
                return;
            }
            body.Handle(0, neck, GrabHandle.Neck, hand, boneWorld, boneWorld.origin);
        }
        // Does the animation keep running under an active ragdoll? Compare the
        // channel 0 animated frame with the captured pose and with itself.
        private float _nextChannelSample;
        private Vec3 _lastChannel;
        private int _channelSamples;
        private float _channelMaxStep, _channelMaxFromCapture;
        private void SampleAnimationChannel(Entry entry)
        {
            if (!entry.Active.Configured || Mission.CurrentTime < _nextChannelSample) return;
            _nextChannelSample = Mission.CurrentTime + .25f;
            sbyte hand = entry.Agent.Monster.MainHandItemBoneIndex;
            if (hand < 0 || hand >= entry.Skeleton.GetBoneCount()) return;
            Vec3 channel = entry.Skeleton.GetBoneEntitialFrameAtChannel(0, hand).origin;
            Vec3 physical = entry.Skeleton.GetBoneEntitialFrameWithIndex(hand).origin;
            float step = _channelSamples > 0 ? (channel - _lastChannel).Length : 0;
            _channelMaxStep = Math.Max(_channelMaxStep, step);
            _channelMaxFromCapture = Math.Max(_channelMaxFromCapture, (channel - physical).Length);
            _lastChannel = channel; _channelSamples++;
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "ANIM_CHANNEL time={0:F3} phase={1} channel=({2:F3},{3:F3},{4:F3}) physical=({5:F3},{6:F3},{7:F3}) step_m={8:F3} anim={9}",
                Mission.CurrentTime, entry.Active.Phase, channel.x, channel.y, channel.z, physical.x, physical.y, physical.z,
                step, entry.Skeleton.GetAnimationAtChannel(0)));
        }

        private Vec3 ProbePelvis(Entry entry) => entry.Agent.AgentVisuals.GetEntity().GetGlobalFrame()
            .TransformToParent(entry.Skeleton.GetBoneEntitialFrameWithIndex(entry.Agent.Monster.PelvisBoneIndex)).origin;
        private void StartProbe()
        {
            if (_probeStarted || Mission.Mode != MissionMode.Battle || Mission.MainAgent == null) return;
            if (_probeBattleBegan < 0) _probeBattleBegan = Mission.CurrentTime;
            float elapsed = Mission.CurrentTime - _probeBattleBegan;
            if (elapsed < (EnemyPhysicsSettings.ActiveBalance || EnemyPhysicsSettings.ActiveRagdoll ? .05f : 3)) return;
            if (GameNetwork.IsMultiplayer || Game.Current?.GameType?.GetType().FullName != "TaleWorlds.MountAndBlade.CustomBattle.CustomGame")
            {
                _probeStarted = _probeFinished = true;
                PhysicsLog.Info("PHYSICS_PROBE result=INCONCLUSIVE reason=not_terrestrial_custom_battle");
                return;
            }
            if (!NativeRagdollBridge.Ready)
            {
                _probeStarted = _probeFinished = true;
                PhysicsLog.Info("PHYSICS_PROBE result=INCONCLUSIVE reason=native_recovery_bridge_unavailable");
                return;
            }
            if (elapsed > 60)
            {
                _probeStarted = _probeFinished = true;
                PhysicsLog.Info("PHYSICS_PROBE result=INCONCLUSIVE reason=no_isolated_eligible_enemy");
                return;
            }
            foreach (Agent agent in Mission.Agents)
            {
                if (!EnemyBody.Eligible(agent, Mission.MainAgent) || !agent.IsActive() || agent.IsPaused ||
                    !agent.IsAIControlled || (agent.Position - Mission.MainAgent.Position).Length < 20) continue;
                if ((EnemyPhysicsSettings.ActiveBalance || EnemyPhysicsSettings.ActiveRagdoll) && agent.Velocity.Length > .3f) continue;
                Skeleton skeleton = agent.AgentVisuals?.GetSkeleton();
                if (skeleton == null || skeleton.GetCurrentRagdollState() != RagdollState.Disabled ||
                    agent.Monster.PelvisBoneIndex < 0 || agent.Monster.PelvisBoneIndex >= skeleton.GetBoneCount()) continue;
                foreach (Entry entry in _entries)
                {
                    if (entry.Lease.Occupied) continue;
                    entry.Agent = agent; entry.Skeleton = skeleton; entry.Mission = Mission;
                    if (!NativeRagdollBridge.Preflight(agent, Mission, skeleton))
                    {
                        _probeStarted = _probeFinished = true; entry.Clear();
                        PhysicsLog.Info("PHYSICS_PROBE result=INCONCLUSIVE reason=native_recovery_preflight_refused");
                        return;
                    }
                    _probeStarted = true; _probeEntry = entry; _probeHealth = agent.Health;
                    _probeBegan = Mission.CurrentTime;
                    _probeOrigin = ProbePelvis(entry); _probeLastState = RagdollState.Disabled;
                    PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "PHYSICS_PROBE begin agent={0} time={1:F3} health={2:F1} state={3} paused={4}",
                        agent.Index, Mission.CurrentTime, agent.Health, agent.State, agent.IsPaused));
                    if (!entry.Lease.Begin(entry, Mission.CurrentTime, LeaseSeconds))
                    {
                        _probeFinished = true; entry.Clear();
                        PhysicsLog.Info("PHYSICS_PROBE result=INCONCLUSIVE reason=activation_rejected");
                    }
                    else
                    {
                        entry.Lease.KeepHeld(Mission.CurrentTime, .6f);
                        _probeBalanceCaptured = entry.Balance.Captured;
                        // Only once the main lease owns its slot.
                        if (EnemyPhysicsSettings.ActiveRagdoll && EnemyPhysicsSettings.ActiveProbeBodies > 1) StartProbeLoad(agent);
                    }
                    return;
                }
            }
        }
        private void ObserveProbe(Entry entry)
        {
            if (!entry.Exists || !entry.Alive || !entry.Safe) return;
            RagdollState state = entry.Skeleton.GetCurrentRagdollState();
            if (state != _probeLastState)
            {
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "PHYSICS_PROBE transition time={0:F3} native={1} lease={2} paused={3}",
                    Mission.CurrentTime, state, entry.Lease.Phase, entry.Agent.IsPaused));
                _probeLastState = state;
            }
            if (state == RagdollState.Active)
            {
                if (EnemyPhysicsSettings.ActiveBalance && Mission.CurrentTime - _probeBegan < 1.2f)
                    entry.Lease.KeepHeld(Mission.CurrentTime);
                _probeSawActive = true;
                if (!_probeInspectedForceApi)
                {
                    _probeInspectedForceApi = true;
                    NativeRagdollBridge.LogForceApi(entry.Agent, Mission, entry.Skeleton, entry.Agent.Monster.PelvisBoneIndex);
                }
                _probeMaxDisplacement = Math.Max(_probeMaxDisplacement, (ProbePelvis(entry) - _probeOrigin).Length);
                _probeMaxDrop = Math.Max(_probeMaxDrop, _probeOrigin.z - ProbePelvis(entry).z);
                _probeMaxRise = Math.Max(_probeMaxRise, ProbePelvis(entry).z - _probeOrigin.z);
            }
        }
        private void FinishProbe(Entry entry)
        {
            _probeFinished = true;
            bool alive = entry.Alive;
            bool disabled = alive && entry.NativeDisabled;
            bool paused = alive && entry.Agent.IsPaused;
            // B&S ragdoll collisions may legitimately cost a few points on the fall.
            bool sameHealth = alive && Math.Abs(entry.Agent.Health - (_probeHealth - _probeImpactLoss)) < .01f;
            bool passed = _probeSawActive && disabled && !paused && sameHealth && !entry.Lease.Faulted &&
                (_probeMaxDisplacement > .02f || (EnemyPhysicsSettings.ActiveBalance && _probeBalanceTicks >= 10));
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "PHYSICS_PROBE result={0} active_seen={1} native_disabled={2} alive={3} paused={4} same_health={5} max_pelvis_displacement_m={6:F3}",
                passed ? "PASS" : "FAIL", _probeSawActive, disabled, alive, paused, sameHealth, _probeMaxDisplacement));
            if (EnemyPhysicsSettings.ActiveRagdoll)
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "ACTIVE_PROBE result={0} standing_max_error_m={1:F3} pushed={2} destabilized={3} stood_up={4} recovered={5} released_by_cycle={6} release_pose_error_m={7:F3} teleported={8} lowest_pelvis_m={9:F3} force_steps={10} alive={11} same_health={12} unpaused={13} native_disabled={14} cpu_ms_mean={15:F4} cpu_ms_max_steady={16:F4} cpu_ms_activation_tick={18:F4} agents={17} impacts={19} impact_health_lost={20:F1}",
                    passed && _probePushed && (!EnemyPhysicsSettings.ActiveProbeGrab || _probeGrabOutcome) && _probeSawDestabilized && _probeSawStandUp && _probeSawRecovered && _probeActiveReleased && _probeTeleported &&
                        _probeStandingError < .15f && _probeMinPelvis < -.4f ? "PASS" : "FAIL",
                    _probeStandingError, _probePushed, _probeSawDestabilized, _probeSawStandUp, _probeSawRecovered,
                    _probeActiveReleased, _probeReleaseError, _probeTeleported, _probeMinPelvis, _probeActiveSteps,
                    alive, sameHealth, !paused, disabled, _probeCpuTicks == 0 ? 0 : _probeCpuTotal / _probeCpuTicks, _probeCpuMax,
                    Mission.Agents.Count, _probeCpuActivationMax, _probeImpacts, _probeImpactLoss));
            else if (EnemyPhysicsSettings.ActiveBalance)
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "BALANCE_PROBE result={0} captured={1} force_ticks={2} support_lost={3} max_drop_m={4:F3} peak_force={5:F1} skipped_ticks={6} physx_steps_while_supported={7} max_rise_m={8:F3}",
                    passed && _probeBalanceCaptured && _probeBalanceTicks >= 10 && !_probeBalanceFell && _probeMaxDrop < .25f && _probeMaxRise < .1f ? "PASS" : "FAIL",
                    _probeBalanceCaptured, _probeBalanceTicks, _probeBalanceFell, _probeMaxDrop, _probePeakSupport,
                    _probeBalanceSkipped, _probeBalanceStepsAt < 0 ? 0 : _probeBalanceStepsEnd - _probeBalanceStepsAt, _probeMaxRise));
            if (alive)
            {
                _probeAgent = entry.Agent; _probeSkeleton = entry.Skeleton;
                _probeHealth = entry.Agent.Health; // follow-up watches for later loss only
                _probeFollowUntil = Mission.CurrentTime + 10;
                _probeNextFollow = Mission.CurrentTime;
                _probeRecoveryOrigin = entry.Agent.Position;
            }
        }
        private void FollowProbe()
        {
            if (_probeAgent == null) return;
            bool alive = _probeAgent.State != AgentState.Deleted && _probeAgent.IsActive();
            bool matches = alive && _probeAgent.AgentVisuals?.GetSkeleton()?.Pointer == _probeSkeleton.Pointer;
            if (!matches)
            {
                PhysicsLog.Info("PHYSICS_PROBE followup=STOP reason=agent_removed_or_skeleton_replaced");
                _probeAgent = null; _probeSkeleton = null;
                return;
            }
            RagdollState state = _probeSkeleton.GetCurrentRagdollState();
            bool done = Mission.CurrentTime >= _probeFollowUntil;
            _probeLocomotionDistance = Math.Max(_probeLocomotionDistance, (_probeAgent.Position - _probeRecoveryOrigin).Length);
            if (state != _probeLastState || done || Mission.CurrentTime >= _probeNextFollow)
            {
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "PHYSICS_PROBE followup time={0:F3} native={1} alive={2} paused={3} same_health={4} observation_done={5} root_distance_m={6:F3} speed={7:F3} action={8}",
                    Mission.CurrentTime, state, alive, _probeAgent.IsPaused,
                    Math.Abs(_probeAgent.Health - _probeHealth) < .01f, done, _probeLocomotionDistance,
                    _probeAgent.Velocity.Length, _probeAgent.GetCurrentActionType(0)));
                _probeLastState = state;
                _probeNextFollow = Mission.CurrentTime + .5f;
            }
            if (done) { _probeAgent = null; _probeSkeleton = null; }
        }
        public override void OnAgentDeleted(Agent affectedAgent)
        {
            if (_probeAgent == affectedAgent) { _probeAgent = null; _probeSkeleton = null; }
            foreach (Entry entry in _entries) if (entry.Agent == affectedAgent)
            { entry.Lease.AbandonDeleted(); entry.Clear(); }
        }
        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            // Mission sets State and invokes this before Agent.OnRemove tears
            // down its components. Transfer a real death before that teardown.
            foreach (Entry entry in _entries)
            {
                if (entry.Agent != affectedAgent || !entry.Lease.Occupied) continue;
                try { entry.Lease.Tick(Mission.CurrentTime); }
                catch (Exception ex) { _failed = true; PhysicsLog.Error("Living ragdoll death handoff failed.", ex); }
                finally { entry.Lease.AbandonDeleted(); entry.Clear(); }
            }
        }
        public override void OnAgentMount(Agent agent) => Release(agent);
        protected override void OnEndMission() => Shutdown();
        public override void OnRemoveBehavior() => Shutdown();
        private void Shutdown()
        {
            _probeAgent = null; _probeSkeleton = null;
            if (_probeStarted && !_probeFinished)
            {
                _probeFinished = true;
                PhysicsLog.Info("PHYSICS_PROBE result=INCONCLUSIVE reason=mission_ended_before_observation");
            }
            // Mission calls OnEndMission before Agent.OnRemove and scene teardown.
            foreach (Entry entry in _entries)
            {
                try { entry.Lease.EmergencyStop(); }
                catch (Exception ex) { PhysicsLog.Error("Living ragdoll shutdown failed.", ex); }
                finally { entry.Clear(); }
            }
            try { _clock.Shutdown(Mission); }
            catch (Exception ex) { PhysicsLog.Error("PhysX step clock shutdown failed.", ex); }
        }
    }
}
