using System;
using System.Reflection;
using System.Runtime.InteropServices;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Once the engine files a dead agent as a corpse (TaleWorlds.Native,
    // REA/Ghidra: add_as_corpse -> 0x1805c6f40), its visuals' validity byte
    // (+0x8ce) is cleared, and get_skeleton / get_entity / get_entity_pointer
    // all return nothing, although the body's entity (+0x8b8) and its
    // skeleton (entity+0x298) live on in the mission's corpse list and still
    // take ragdoll forces (apply_force_on_ragdoll reads the same chain
    // without the check). The entity is read here from +0x8b8, an offset
    // first checked against the game's own answer on a live agent.
    internal static class CorpseVisuals
    {
        private const int EntityOffset = 0x8b8;
        private static int _verified; // 0 unknown, 1 trusted, -1 refused
        private static ConstructorInfo _weakCtor;

        // On an agent whose visuals still answer: does +0x8b8 hold the entity?
        public static void Verify(Agent agent)
        {
            if (_verified != 0) return;
            try
            {
                MBAgentVisuals visuals = agent?.AgentVisuals;
                if (visuals == null || !visuals.IsValid()) return;
                WeakGameEntity weak = visuals.GetWeakEntity();
                if (!weak.IsValid) return;
                UIntPtr read = Read(visuals.Pointer);
                _verified = read == weak.Pointer ? 1 : -1;
                _weakCtor = typeof(WeakGameEntity).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(UIntPtr) }, null);
                if (_weakCtor == null) _verified = -1;
                PhysicsLog.Info("CORPSE visuals offset " + (_verified == 1 ? "verified" : "refused (corpses cannot be grabbed)"));
            }
            catch (Exception ex) { _verified = -1; PhysicsLog.Error("Checking the corpse visuals offset failed.", ex); }
        }

        // The body's entity of a filed corpse, invalid when unknown.
        public static WeakGameEntity Entity(Agent agent)
        {
            if (_verified != 1 || agent == null || agent.State == AgentState.Deleted) return WeakGameEntity.Invalid;
            try
            {
                MBAgentVisuals visuals = agent.AgentVisuals;
                if (visuals == null) return WeakGameEntity.Invalid;
                UIntPtr entity = Read(visuals.Pointer);
                return entity == UIntPtr.Zero ? WeakGameEntity.Invalid : (WeakGameEntity)_weakCtor.Invoke(new object[] { entity });
            }
            catch (Exception) { return WeakGameEntity.Invalid; }
        }

        public static Skeleton Skeleton(Agent agent)
        {
            if (Cached(agent, out _, out Skeleton cached)) return cached;
            WeakGameEntity entity = Entity(agent);
            return entity.IsValid ? entity.Skeleton : null;
        }

        // The body's own entity and skeleton, kept while the game still hands
        // them out: these managed handles hold their native objects, so a
        // filed corpse is still reachable through them.
        private sealed class Body { public GameEntity Entity; public Skeleton Skeleton; }
        private static readonly System.Collections.Generic.Dictionary<Agent, Body> Bodies =
            new System.Collections.Generic.Dictionary<Agent, Body>();
        public static void Remember(Agent agent, GameEntity entity, Skeleton skeleton)
        {
            if (agent == null || entity == null || skeleton == null) return;
            if (!Bodies.TryGetValue(agent, out Body body)) Bodies[agent] = body = new Body();
            body.Entity = entity; body.Skeleton = skeleton;
        }
        public static bool Cached(Agent agent, out GameEntity entity, out Skeleton skeleton)
        {
            entity = null; skeleton = null;
            if (agent == null || !Bodies.TryGetValue(agent, out Body body)) return false;
            if (agent.State == AgentState.Deleted) { Bodies.Remove(agent); return false; }
            entity = body.Entity; skeleton = body.Skeleton;
            return true;
        }
        public static void Forget() => Bodies.Clear();

        // Frame and skeleton of any agent's body, live or filed.
        public static bool Pose(Agent agent, out MatrixFrame frame, out Skeleton skeleton)
        {
            frame = MatrixFrame.Identity; skeleton = null;
            MBAgentVisuals visuals = agent?.AgentVisuals;
            GameEntity live = visuals?.GetEntity();
            if (live != null) { frame = live.GetGlobalFrame(); skeleton = visuals.GetSkeleton(); return skeleton != null; }
            if (Cached(agent, out GameEntity entity, out skeleton)) { frame = entity.GetGlobalFrame(); return true; }
            WeakGameEntity weak = Entity(agent);
            if (!weak.IsValid) return false;
            frame = weak.GetGlobalFrame(); skeleton = weak.Skeleton;
            return skeleton != null;
        }

        // The body's entity pointer, live or filed (zero when unknown).
        public static UIntPtr EntityPointer(Agent agent)
        {
            GameEntity live = agent?.AgentVisuals?.GetEntity();
            if (live != null) return live.Pointer;
            if (Cached(agent, out GameEntity entity, out _) && entity != null) return entity.Pointer;
            return Entity(agent).Pointer;
        }

        public static string Describe(Agent agent)
        {
            try
            {
                MBAgentVisuals visuals = agent?.AgentVisuals;
                WeakGameEntity weak = Entity(agent);
                return "visuals=" + (visuals == null ? "none" : visuals.IsValid() ? "valid" : "filed") +
                    " cached=" + Cached(agent, out _, out _) + " offset=" + _verified +
                    " entity=" + (weak.IsValid ? "0x" + ((ulong)weak.Pointer).ToString("x") : "none") +
                    " entity_skeleton=" + (weak.IsValid && weak.Skeleton != null);
            }
            catch (Exception ex) { return "describe_failed " + ex.GetType().Name; }
        }

        private static UIntPtr Read(UIntPtr visuals) =>
            visuals == UIntPtr.Zero ? UIntPtr.Zero : (UIntPtr)(ulong)Marshal.ReadInt64(new IntPtr((long)(ulong)visuals + EntityOffset));
    }
}
