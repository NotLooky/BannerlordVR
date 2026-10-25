using System;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    public struct BodyPoint
    {
        public sbyte Bone;
        public BoneBodyPartType Part;
        public float Radius;
        public Vec3 World;
        public Vec3 Velocity;
    }

    // One record per admitted nearby agent, never one per member of the army.
    public sealed class EnemyBody
    {
        public Agent Agent;
        public Skeleton Skeleton;
        public readonly BodyPoint[] Points = new BodyPoint[16];
        public int Count;
        public float LastHitLeft = -10, LastHitRight = -10;
        // When a stab that could not be held last struck this body.
        public float LastEntry = -10;
        public MatrixFrame Frame;
        private bool _sampled;
        public void Clear()
        {
            Agent = null; Skeleton = null; Count = 0; _sampled = false;
        }
        public bool IsCorpse => Agent.State == AgentState.Killed || Agent.State == AgentState.Unconscious;
        public bool HasNativePhysics => (IsCorpse || EnemyPhysicsRuntime.OwnsLivingPhysics(Agent)) &&
            Skeleton.GetCurrentRagdollState() == RagdollState.Active;
        public BodyPoint ReadContact(sbyte bone)
        {
            if (bone < 0 || bone >= Skeleton.GetBoneCount()) return default;
            MBAgentVisuals visuals = Agent.AgentVisuals;
            // A filed corpse's visuals no longer answer: the part comes from
            // the bound points, the nearest bound ancestor for other bones.
            bool live = visuals != null && visuals.IsValid();
            BoneBodyPartType part = live ? visuals.GetBoneTypeData(bone).BodyPartType : BoneBodyPartType.None;
            Vec3 world = Frame.TransformToParent(Skeleton.GetBoneEntitialFrameWithIndex(bone)).origin;
            // A bone without a sampled point moves with its nearest sampled
            // ancestor on the drawn body. Agent.Velocity is the hidden agent's
            // walk (horizontal, up to 4 m/s), not the ragdoll's: a stab into a
            // spine bone braked a part that 'moved' at 4 m/s, with 1500 N the
            // wrong way, and threw the body or dragged it onto the player.
            Vec3 velocity = Vec3.Zero;
            bool found = false;
            for (int i = 0; i < Count; i++) if (Points[i].Bone == bone) { velocity = Points[i].Velocity; found = true; if (!live) part = Points[i].Part; break; }
            for (sbyte up = Skeleton.GetParentBoneIndex(bone); !found && up >= 0; up = Skeleton.GetParentBoneIndex(up))
                for (int i = 0; i < Count; i++) if (Points[i].Bone == up) { velocity = Points[i].Velocity; found = true; break; }
            if (!found && !IsCorpse) velocity = Agent.Velocity;
            for (sbyte up = Skeleton.GetParentBoneIndex(bone); !live && part == BoneBodyPartType.None && up >= 0; up = Skeleton.GetParentBoneIndex(up))
                for (int i = 0; i < Count; i++) if (Points[i].Bone == up) { part = Points[i].Part; break; }
            return new BodyPoint { Bone = bone, Part = part, World = world, Velocity = velocity };
        }

        public string LastFailure { get; private set; } = "-";
        public bool Bind(Agent agent)
        {
            Agent = agent;
            Skeleton = agent.AgentVisuals?.GetSkeleton();
            if (Skeleton != null) { CorpseVisuals.Verify(agent); CorpseVisuals.Remember(agent, agent.AgentVisuals.GetEntity(), Skeleton); }
            // A filed corpse's visuals stop answering; its body lives on.
            else if (IsCorpse) Skeleton = CorpseVisuals.Skeleton(agent);
            if (Skeleton == null) { LastFailure = "no_skeleton " + CorpseVisuals.Describe(agent); return false; }
            LastFailure = "no_bones";
            Count = 0; _sampled = false;
            LastHitLeft = LastHitRight = LastEntry = -10;
            int bones = Skeleton.GetBoneCount();
            for (sbyte i = 0; i < bones && i >= 0 && Count < Points.Length; i++)
            {
                string name = (Skeleton.GetBoneName(i) ?? "").ToLowerInvariant();
                BoneBodyPartType part;
                float radius;
                if (name == "head") { part = BoneBodyPartType.Head; radius = .13f; }
                else if (name == "neck") { part = BoneBodyPartType.Neck; radius = .09f; }
                else if (name == "spine2" || name == "spine_2") { part = BoneBodyPartType.Chest; radius = .23f; }
                else if (name == "pelvis") { part = BoneBodyPartType.Abdomen; radius = .22f; }
                else if (name == "l_hand" || name == "r_hand" || name == "l_forearm" || name == "r_forearm" ||
                    name == "l_foretwist" || name == "r_foretwist" || name == "l_upperarm_twist" || name == "r_upperarm_twist")
                { part = name[0] == 'l' ? BoneBodyPartType.ArmLeft : BoneBodyPartType.ArmRight; radius = .09f; }
                else if (name == "l_calf" || name == "r_calf" || name == "l_foot" || name == "r_foot" || name == "l_thigh" || name == "r_thigh")
                { part = BoneBodyPartType.Legs; radius = .12f; }
                else continue;
                Points[Count++] = new BodyPoint { Bone = i, Part = part, Radius = radius };
            }
            return Count > 0;
        }

        public bool Sample(float dt)
        {
            var visuals = Agent.AgentVisuals;
            var entity = visuals?.GetEntity();
            Skeleton current;
            if (entity != null)
            {
                Frame = entity.GetGlobalFrame(); current = visuals.GetSkeleton();
                CorpseVisuals.Remember(Agent, entity, current);
            }
            else if (IsCorpse)
            {
                if (!CorpseVisuals.Pose(Agent, out Frame, out current)) { LastFailure = "no_entity " + CorpseVisuals.Describe(Agent); return false; }
            }
            else { LastFailure = "no_entity"; return false; }
            // Every filed corpse joins the engine's fading list (it stays
            // visible meanwhile): only a living agent fading out is gone.
            if (!IsCorpse && Agent.IsFadingOut()) { LastFailure = "fading_out"; return false; }
            // Equipment/LOD or scene rebuild may replace the skeleton; discard the cache.
            if (current == null || current.Pointer != Skeleton.Pointer) { LastFailure = "skeleton_replaced"; return false; }
            for (int i = 0; i < Count; i++)
            {
                BodyPoint point = Points[i];
                Vec3 position = Frame.TransformToParent(Skeleton.GetBoneEntitialFrameWithIndex(point.Bone)).origin;
                if (!InteractionMath.IsFinite(position)) { LastFailure = "bone_not_finite"; return false; }
                point.Velocity = _sampled ? InteractionMath.ClampMagnitude((position - point.World) * (1 / dt), 15) : Vec3.Zero;
                point.World = position;
                Points[i] = point;
            }
            _sampled = true;
            return true;
        }

        public static bool Hostile(Agent agent, Agent other) =>
            agent.Team != null && other.Team != null ? agent.Team.IsEnemyOf(other.Team) : agent.IsEnemyOf(other);
        public static bool Eligible(Agent agent, Agent player, bool ownedPhysicalFall = false) =>
            EligibleReason(agent, player, ownedPhysicalFall) == null;
        // Why an agent is not taken by the physics (null when it is).
        public static string EligibleReason(Agent agent, Agent player, bool ownedPhysicalFall = false)
        {
            if (agent == null || agent == player) return "self";
            if (!agent.IsHuman) return "not_human";
            if (agent.RiderAgent != null || (agent.MountAgent != null && !EnemyPhysicsSettings.Riders)) return "mounted";
            if (agent.State != AgentState.Active && agent.State != AgentState.Killed && agent.State != AgentState.Unconscious) return "state_" + agent.State;
            // Teams decide: the native IsEnemy is false for an agent without a
            // controller (the sandbox dummies), which left them untracked.
            if (!Hostile(agent, player)) return "not_enemy";
            if (agent.IsUsingGameObject || agent.IsInLadderQueue) return "using_object";
            // Scripted movement and combat modes belong to the AI; an agent
            // without one (a sandbox dummy) has none to respect.
            if (agent.Controller == AgentControllerType.AI)
            {
                // A held body carries InConversation, set by the physics.
                Agent.AIScriptedFrameFlags scripted = agent.GetScriptedFlags();
                if (ownedPhysicalFall) scripted &= ~Agent.AIScriptedFrameFlags.InConversation;
                if (scripted != Agent.AIScriptedFrameFlags.None) return "scripted_" + scripted;
                // Only an attack on an entity (a gate, a siege engine) is the
                // AI's script; IgnoreAmmoLimitForRangeCalculation, set on most
                // soldiers of a field battle, dropped them mid-fight.
                if ((agent.GetScriptedCombatFlags() & (Agent.AISpecialCombatModeFlags.AttackEntity | Agent.AISpecialCombatModeFlags.SurroundAttackEntity)) != 0)
                    return "scripted_combat";
            }
            if (agent.State != AgentState.Active) return null;
            for (int channel = 0; channel < 2; channel++)
            {
                var action = agent.GetCurrentActionType(channel);
                if (action == Agent.ActionCodeType.Mount || action == Agent.ActionCodeType.Dismount ||
                    (action == Agent.ActionCodeType.Fall && !ownedPhysicalFall) || (action >= Agent.ActionCodeType.JumpStart && action <= Agent.ActionCodeType.JumpEndHard) ||
                    (action >= Agent.ActionCodeType.Sit && action <= Agent.ActionCodeType.Rear))
                {
                    // Getting up from a knockdown is a body the physics may
                    // take again, as B&S grabs a creature standing up.
                    if (agent.GetCurrentAction(channel).GetName().StartsWith("act_strike_fall")) continue;
                    return "action_" + action;
                }
            }
            return null;
        }
    }
}
