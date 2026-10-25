using System.Collections.Generic;
using System.Globalization;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Measures the switch from the physical body back to the animation: the
    // pelvis drawn each tick after the release, against the ragdoll's last
    // pelvis. Logs where the drawn body starts, every jump over 4 cm and
    // when it happens, and a summary, so a "pop" can be told apart (pose,
    // position, a frozen skeleton, a late animation).
    internal static class ReleaseWatch
    {
        // A new mission: the last one's agents and entities are gone.
        internal static void ForgetMission() { Watches.Clear(); Done.Clear(); }
        private sealed class Watch { public Vec3 Ragdoll, Last; public int Tick; public float MaxJump, Total; public bool First; }
        private static readonly Dictionary<Agent, Watch> Watches = new Dictionary<Agent, Watch>();
        private static readonly List<Agent> Done = new List<Agent>();
        private const int Ticks = 60;

        public static void Start(Agent agent, Vec3 ragdollPelvis)
        {
            if (agent == null || !EnemyPhysicsSettings.Trace) return;
            Watches[agent] = new Watch { Ragdoll = ragdollPelvis, First = true };
        }

        public static void Tick()
        {
            if (Watches.Count == 0) return;
            Done.Clear();
            foreach (KeyValuePair<Agent, Watch> pair in Watches)
            {
                Agent agent = pair.Key; Watch w = pair.Value;
                Skeleton skeleton = agent.IsActive() ? agent.AgentVisuals?.GetSkeleton() : null;
                if (skeleton == null || ++w.Tick > Ticks) { Done.Add(agent); continue; }
                MatrixFrame frame = agent.AgentVisuals.GetGlobalFrame();
                Vec3 pelvis = frame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(0)).origin;
                string state = skeleton.GetCurrentRagdollState() + (skeleton.IsFrozen() ? "/frozen" : "");
                if (w.First)
                {
                    w.First = false;
                    Vec3 d = pelvis - w.Ragdoll;
                    PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "RELEASE_POP agent={0} tick={1} drawn pelvis vs ragdoll: right={2:F2} forward={3:F2} up={4:F2} m ({5}) action={6}",
                        agent.Index, w.Tick, InteractionMath.Dot(d, frame.rotation.s.NormalizedCopy()), InteractionMath.Dot(d, frame.rotation.f.NormalizedCopy()),
                        d.z, state, agent.GetCurrentAction(0).GetName()));
                }
                else
                {
                    float jump = (pelvis - w.Last).Length;
                    w.Total += jump;
                    if (jump > w.MaxJump) w.MaxJump = jump;
                    if (jump > .04f)
                        PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                            "RELEASE_POP agent={0} tick={1} jump={2:F2} m to right={3:F2} forward={4:F2} up={5:F2} from the ragdoll ({6})",
                            agent.Index, w.Tick, jump, InteractionMath.Dot(pelvis - w.Ragdoll, frame.rotation.s.NormalizedCopy()),
                            InteractionMath.Dot(pelvis - w.Ragdoll, frame.rotation.f.NormalizedCopy()), pelvis.z - w.Ragdoll.z, state));
                }
                w.Last = pelvis;
            }
            foreach (Agent agent in Done)
            {
                Watch w = Watches[agent];
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "RELEASE_POP agent={0} done: biggest tick jump {1:F2} m, pelvis moved {2:F2} m in {3} ticks", agent.Index, w.MaxJump, w.Total, w.Tick - 1));
                Watches.Remove(agent);
            }
        }
    }
}
