using System;
using System.Diagnostics;
using System.Globalization;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    public sealed class BleedingMissionLogic : MissionLogic
    {
        private sealed class Entry
        {
            public readonly BleedingWound Wound = new BleedingWound();
            public Agent Victim, Source;
            public Skeleton Skeleton;
            public sbyte Bone;
            public BoneBodyPartType Part;
            public string BoneName;
            public Vec3 LocalContact;
            public void Clear()
            {
                Wound.Clear(); Victim = Source = null; Skeleton = null; BoneName = null;
                LocalContact = Vec3.Zero;
            }
        }
        private readonly Entry[] _entries = CreateEntries();
        private int _cursor;
        private bool _failed;
        private bool _probeStarted, _probeFinished, _probeApplying, _probeAfterInitial, _probeForeignHit;
        private bool _probeOwnerMatches = true;
        private Agent _probeAgent, _probeSource;
        private int _probeIndex, _probeHitCallbacks;
        private int _probeScanCursor;
        private readonly EnemyBody _probeBody = new EnemyBody();
        private float _probeBattleStart = -1, _probeDeadline, _probeAfterHit;
        public double LastTickCpuMilliseconds { get; private set; }
        private static Entry[] CreateEntries()
        {
            var result = new Entry[64];
            for (int i = 0; i < result.Length; i++) result[i] = new Entry();
            return result;
        }

        public void Record(Agent source, EnemyBody body, BodyPoint point, Vec3 contact,
                           DamageTypes type, float healthLost, bool drawCut)
        {
            if (_failed || !EnemyPhysicsSettings.Bleeding || source == null || body?.Agent == null ||
                !body.Agent.IsActive() || !EnemyBody.Hostile(body.Agent, source) || !body.Agent.IsHuman ||
                GameNetwork.IsMultiplayer || !EnemyPhysicsSettings.Fighting(Mission.Mode) ||
                (type != DamageTypes.Cut && type != DamageTypes.Pierce) ||
                !InteractionMath.IsFinite(healthLost) || healthLost < 1 || !InteractionMath.IsFinite(contact)) return;
            try
            {
                Entry chosen = null;
                foreach (Entry entry in _entries)
                {
                    if (entry.Victim == body.Agent && entry.Part == point.Part && entry.Wound.Active)
                    { chosen = entry; break; }
                    if (chosen == null && !entry.Wound.Active) chosen = entry;
                }
                if (chosen == null) return; // Preserve existing wounds when the bounded table is full.
                bool vital = point.Part == BoneBodyPartType.Neck || point.Part == BoneBodyPartType.Head;
                bool torso = point.Part == BoneBodyPartType.Chest || point.Part == BoneBodyPartType.Abdomen;
                float rate = healthLost * (vital ? .14f : torso ? .07f : .035f) * (drawCut ? 1.3f : 1);
                float duration = vital || torso ? 8 : 6;
                MatrixFrame frame = body.Agent.AgentVisuals.GetEntity().GetGlobalFrame()
                    .TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(point.Bone));
                Vec3 local = frame.TransformToLocalNonOrthogonal(contact);
                if (!InteractionMath.IsFinite(local)) return;
                if (!chosen.Wound.Active) chosen.Clear();
                if (!chosen.Wound.Add(Mission.CurrentTime, rate, duration)) return;
                chosen.Victim = body.Agent; chosen.Source = source; chosen.Skeleton = body.Skeleton;
                chosen.Bone = point.Bone; chosen.Part = point.Part;
                chosen.BoneName = body.Skeleton.GetBoneName(point.Bone);
                chosen.LocalContact = local;
            }
            catch (Exception ex) { Disable(ex); }
        }

        public void Deepen(Agent victim, sbyte bone, float depth)
        {
            if (_failed) return;
            foreach (Entry entry in _entries)
                if (entry.Victim == victim && entry.Bone == bone && entry.Wound.Active) entry.Wound.Deepen(depth);
        }

        public override void OnMissionTick(float dt)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                if (_failed || !EnemyPhysicsSettings.Bleeding || GameNetwork.IsMultiplayer ||
                    Mission.MissionEnded || Mission.IsFinalized) { Clear(); return; }
                if (!EnemyPhysicsSettings.Fighting(Mission.Mode)) return;
                if (EnemyPhysicsSettings.RuntimeProbe && EnemyPhysicsSettings.WoundProbe) { StartProbe(dt); FinishProbe(); }
                int work = 0;
                // Pure Due checks are cheap. At most four entries may perform
                // native pose queries / blows during any one mission callback.
                for (int n = 0; n < _entries.Length && work < 4; n++)
                {
                    Entry entry = _entries[_cursor];
                    _cursor = (_cursor + 1) % _entries.Length;
                    if (!entry.Wound.Due(Mission.CurrentTime)) continue;
                    work++;
                    if (entry.Victim == null || !entry.Victim.IsActive() || !RefreshSkeleton(entry))
                    { entry.Clear(); continue; }
                    if (entry.Source != null && (entry.Source.State == AgentState.Deleted ||
                        Mission.FindAgentWithIndex(entry.Source.Index) != entry.Source)) entry.Source = null;
                    int dose = entry.Wound.TakeDose(Mission.CurrentTime);
                    if (dose > 0)
                    {
                        MatrixFrame frame = entry.Victim.AgentVisuals.GetEntity().GetGlobalFrame()
                            .TransformToParent(entry.Skeleton.GetBoneEntitialFrameWithIndex(entry.Bone));
                        Vec3 contact = frame.TransformToParent(entry.LocalContact);
                        if (InteractionMath.IsFinite(contact))
                        {
                            _probeApplying = !_probeFinished && entry.Victim == _probeAgent;
                            try { PhysicalHit.ApplyBloodLoss(entry.Source, entry.Victim, entry.Bone, entry.Part, contact, dose); }
                            finally { _probeApplying = false; }
                        }
                    }
                    // RegisterBlow can synchronously invoke OnAgentRemoved and
                    // clear this entry; never dereference its victim afterwards.
                    if (!entry.Wound.Active) entry.Clear();
                }
            }
            catch (Exception ex) { Disable(ex); }
            finally { LastTickCpuMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency; }
        }

        private bool RefreshSkeleton(Entry entry)
        {
            Skeleton skeleton = entry.Victim.AgentVisuals?.GetSkeleton();
            if (skeleton == null) return false;
            if (entry.Skeleton != null && skeleton.Pointer == entry.Skeleton.Pointer)
                return entry.Bone >= 0 && entry.Bone < skeleton.GetBoneCount();
            // LOD or equipment changes may replace the skeleton. Rebind by the
            // original bone name once, without transferring an old pointer.
            int count = skeleton.GetBoneCount();
            for (sbyte bone = 0; bone >= 0 && bone < count; bone++)
            {
                if (skeleton.GetBoneName(bone) != entry.BoneName ||
                    entry.Victim.AgentVisuals.GetBoneTypeData(bone).BodyPartType != entry.Part) continue;
                entry.Skeleton = skeleton; entry.Bone = bone;
                return true;
            }
            return false;
        }

        private void StartProbe(float dt)
        {
            if (_probeStarted || Mission.MainAgent == null) return;
            if (_probeBattleStart < 0) _probeBattleStart = Mission.CurrentTime;
            if (Mission.CurrentTime - _probeBattleStart < 3) return;
            if (Game.Current?.GameType?.GetType().FullName != "TaleWorlds.MountAndBlade.CustomBattle.CustomGame")
            { ProbeStop("INCONCLUSIVE", "not_terrestrial_custom_battle"); return; }
            if (Mission.CurrentTime - _probeBattleStart > 60)
            { ProbeStop("INCONCLUSIVE", "no_isolated_eligible_enemy"); return; }
            Agent source = Mission.MainAgent;
            int agents = Mission.Agents.Count;
            for (int n = 0; n < Math.Min(16, agents); n++)
            {
                if (_probeScanCursor >= agents) _probeScanCursor = 0;
                Agent agent = Mission.Agents[_probeScanCursor++];
                if (!EnemyBody.Eligible(agent, source) || !agent.IsActive() || agent.IsPlayerControlled ||
                    agent.Health < 50 || (agent.Position - source.Position).Length < 40) continue;
                EnemyBody body = _probeBody;
                if (!body.Bind(agent) || !body.Sample(Math.Max(.001f, dt))) continue;
                for (int i = 0; i < body.Count; i++)
                {
                    BodyPoint point = body.ReadContact(body.Points[i].Bone);
                    if (point.Part != BoneBodyPartType.Neck) continue;
                    _probeStarted = true; _probeAgent = agent; _probeSource = source; _probeIndex = agent.Index;
                    float before = agent.Health;
                    _probeApplying = true;
                    try { PhysicalHit.ApplyBloodLoss(source, agent, point.Bone, point.Part, point.World, 2); }
                    finally { _probeApplying = false; }
                    if (Mission.FindAgentWithIndex(_probeIndex) != agent || !agent.IsActive())
                    { ProbeStop("INCONCLUSIVE", "initial_hit_removed_target"); return; }
                    _probeAfterHit = agent.Health;
                    float lost = before - _probeAfterHit;
                    if (Math.Abs(lost - 2) > .01f)
                    { ProbeStop("INCONCLUSIVE", "initial_damage_changed_or_blocked"); return; }
                    Record(source, body, point, point.World, DamageTypes.Cut, lost, true);
                    bool recorded = false;
                    foreach (Entry entry in _entries) if (entry.Victim == agent && entry.Wound.Active) recorded = true;
                    if (!recorded) { ProbeStop("FAIL", "wound_not_recorded"); return; }
                    _probeAfterInitial = true; _probeDeadline = Mission.CurrentTime + 9;
                    PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "WOUND_PROBE begin agent={0} initial_health={1:F1} after_hit={2:F1} expected_bleed_damage=2",
                        _probeIndex, before, _probeAfterHit));
                    return;
                }
            }
        }

        private void FinishProbe()
        {
            if (!_probeStarted || _probeFinished || Mission.CurrentTime < _probeDeadline) return;
            Agent agent = Mission.FindAgentWithIndex(_probeIndex);
            if (agent != _probeAgent || !agent.IsActive())
            { ProbeStop("INCONCLUSIVE", "target_no_longer_active"); return; }
            bool woundActive = false;
            foreach (Entry entry in _entries) if (entry.Victim == agent && entry.Wound.Active) woundActive = true;
            float bleed = _probeAfterHit - agent.Health;
            bool pass = !_probeForeignHit && _probeOwnerMatches && _probeHitCallbacks == 3 &&
                Math.Abs(bleed - 2) < .01f && !woundActive;
            _probeFinished = true;
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "WOUND_PROBE result={0} bleed_damage={1:F1} callbacks={2} owner_matches={3} external_hit={4} wound_expired={5} alive=True",
                _probeForeignHit ? "INCONCLUSIVE" : pass ? "PASS" : "FAIL", bleed, _probeHitCallbacks,
                _probeOwnerMatches, _probeForeignHit, !woundActive));
        }

        public override void OnAgentHit(Agent victim, Agent source, in MissionWeapon weapon, in Blow blow, in AttackCollisionData collision)
        {
            // Every blow any system lands while the physics runs, with who dealt
            // it: ours (PhysicalHit), BannerlordVR's or the engine's own melee.
            // Only blows near the player: in a large battle every soldier's
            // blow made a formatted line, hundreds a second.
            if (EnemyPhysicsSettings.Trace && (PhysicalHit.DeliveringOwnBlow || victim?.IsMainAgent == true || source?.IsMainAgent == true ||
                (victim != null && EnemyPhysicsRuntime.Tracks(victim))))
            {
                try
                {
                    PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "BLOW victim={0} source={1} origin={2} damage={3} type={4} flags={5} bone={6} weapon={7} health_after={8:F0}",
                        victim?.Index ?? -1, source?.Index ?? -1, PhysicalHit.DeliveringOwnBlow ? "physics" : "other",
                        blow.InflictedDamage, blow.DamageType, blow.BlowFlag, blow.BoneIndex,
                        weapon.IsEmpty ? "-" : weapon.Item?.StringId ?? "?", victim?.Health ?? 0));
                }
                catch (Exception) { }
            }
            if (!_probeStarted || _probeFinished || victim != _probeAgent) return;
            if (!_probeApplying) { _probeForeignHit = true; return; }
            _probeHitCallbacks++;
            _probeOwnerMatches &= source == _probeSource;
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "WOUND_PROBE hit phase={0} damage={1} health={2:F1} owner_matches={3}",
                _probeAfterInitial ? "delayed" : "initial", blow.InflictedDamage, victim.Health, source == _probeSource));
        }

        private void ProbeStop(string result, string reason)
        {
            if (_probeFinished) return;
            _probeStarted = _probeFinished = true;
            PhysicsLog.Info("WOUND_PROBE result=" + result + " reason=" + reason);
        }

        public override void OnAgentRemoved(Agent victim, Agent affector, AgentState state, KillingBlow blow)
        {
            if (victim == _probeAgent && !_probeFinished) ProbeStop("INCONCLUSIVE", "target_removed");
            foreach (Entry entry in _entries) if (entry.Victim == victim) entry.Clear();
        }
        public override void OnAgentDeleted(Agent agent)
        {
            if (agent == _probeAgent && !_probeFinished) ProbeStop("INCONCLUSIVE", "target_deleted");
            foreach (Entry entry in _entries)
            {
                if (entry.Victim == agent) entry.Clear();
                else if (entry.Source == agent) entry.Source = null;
            }
        }
        protected override void OnEndMission() => Shutdown();
        public override void OnRemoveBehavior() => Shutdown();
        private void Shutdown()
        {
            if (_probeStarted && !_probeFinished) ProbeStop("INCONCLUSIVE", "mission_ended_before_observation");
            _probeAgent = _probeSource = null;
            Clear();
        }
        private void Clear() { foreach (Entry entry in _entries) entry.Clear(); }
        private void Disable(Exception ex)
        {
            _failed = true; Clear();
            if (EnemyPhysicsSettings.RuntimeProbe && EnemyPhysicsSettings.WoundProbe) ProbeStop("FAIL", "managed_exception");
            PhysicsLog.Error("Bleeding stopped for this mission after an error; wounds cleared.", ex);
        }
    }
}
