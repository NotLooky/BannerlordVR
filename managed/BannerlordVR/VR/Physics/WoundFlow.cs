using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Creature parts: the Neck alone has penetrationDeepEffectId
    // PenetrationDeepBleeding (every other part PenetrationDeepFlesh, a stab
    // sound with no blood). Its Bleeding.Flesh particle plays at the End
    // step, as the blade leaves: the wound pours where it was, following the
    // part (parented to it), and its drops stain the ground (DropBlood).
    internal static class WoundFlow
    {
        // A new mission: the last one's agents and entities are gone.
        internal static void ForgetMission() { Flows.Clear(); }
        private struct Flow { public Agent Agent; public sbyte Bone; public Vec3 Local; public float Start, Until, Next; }
        private static readonly List<Flow> Flows = new List<Flow>();
        private static Mission _mission;
        private const int MaxFlows = 8;
        public const float Seconds = 4;

        public static void Begin(Agent agent, sbyte bone, Vec3 world)
        {
            try
            {
                Mission mission = Mission.Current;
                Skeleton skeleton = agent?.AgentVisuals?.GetSkeleton();
                if (mission == null || skeleton == null || bone < 0 || !InteractionMath.IsFinite(world)) return;
                if (mission != _mission) { Flows.Clear(); _mission = mission; }
                if (Flows.Count >= MaxFlows) Flows.RemoveAt(0);
                MatrixFrame frame = agent.AgentVisuals.GetGlobalFrame().TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(bone));
                Vec3 local = frame.TransformToLocalNonOrthogonal(world);
                if (!InteractionMath.IsFinite(local)) return;
                float now = mission.CurrentTime;
                Flows.Add(new Flow { Agent = agent, Bone = bone, Local = local, Start = now, Until = now + Seconds, Next = now });
                if (EnemyPhysicsSettings.Trace) PhysicsLog.Info("WOUND_FLOW neck agent=" + agent.Index + " bone=" + bone);
            }
            catch (Exception ex) { PhysicsLog.Error("Neck wound flow failed.", ex); }
        }

        public static void Tick()
        {
            if (Flows.Count == 0) return;
            Mission mission = Mission.Current;
            if (mission != _mission) { Flows.Clear(); return; }
            float now = mission.CurrentTime;
            for (int i = Flows.Count - 1; i >= 0; i--)
            {
                Flow flow = Flows[i];
                Skeleton skeleton = flow.Agent?.AgentVisuals?.GetSkeleton();
                if (now > flow.Until || skeleton == null || flow.Agent.State == AgentState.Deleted) { Flows.RemoveAt(i); continue; }
                if (now < flow.Next) continue;
                float age = (now - flow.Start) / Seconds;
                flow.Next = now + .08f + .2f * age;
                Flows[i] = flow;
                try
                {
                    MatrixFrame frame = flow.Agent.AgentVisuals.GetGlobalFrame().TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(flow.Bone));
                    BloodFx.Burst(BloodFx.Kind.Flow, frame.TransformToParent(flow.Local), -Vec3.Up);
                }
                catch (Exception) { Flows.RemoveAt(i); }
            }
        }
    }
}
