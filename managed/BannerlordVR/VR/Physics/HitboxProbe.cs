using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Measures the capsules blades are tested on (BannerlordVR's
    // VrPhysicalCombat.BodyOf: bone to child bone where the parent has a body
    // part, a radius per part, a 0.11 m sphere 0.1 m over the head) against
    // the body actually drawn: the agent's skinned meshes, read in the rest
    // pose by WoundDecal. Each vertex goes to its nearest capsule axis; the
    // spread of their distances is the drawn thickness round that bone. A
    // capsule thinner than it lets blades through the drawn body, a fatter
    // one strikes empty air. One line per capsule in the log and the whole
    // body (a share of its vertices and the capsules) in HitboxProbe.json.
    internal static class HitboxProbe
    {
        public static bool Enabled => EnemyPhysicsSettings.HitboxProbe;
        private const int MaxAgents = 3, SavedVertices = 6000;
        private static readonly HashSet<Agent> Measured = new HashSet<Agent>();
        private static Mission _mission;
        private static int _saved;

        internal struct Capsule { public Vec3 A, B; public float Radius; public sbyte Bone; public BoneBodyPartType Part; public string Name; }

        // BannerlordVR's PartRadius.
        internal static float PartRadius(BoneBodyPartType part)
        {
            switch (part)
            {
                case BoneBodyPartType.Head: return .1f;
                case BoneBodyPartType.Neck: return .06f;
                case BoneBodyPartType.Chest: return .15f;
                case BoneBodyPartType.Abdomen: return .14f;
                case BoneBodyPartType.ShoulderLeft:
                case BoneBodyPartType.ShoulderRight: return .07f;
                case BoneBodyPartType.ArmLeft:
                case BoneBodyPartType.ArmRight: return .05f;
                default: return .07f;
            }
        }

        public static void Tick()
        {
            if (!Enabled) return;
            Mission mission = Mission.Current;
            if (mission != _mission) { Measured.Clear(); _mission = mission; }
            if (Measured.Count >= MaxAgents) return;
            foreach (KeyValuePair<Agent, List<Vec3>> pair in WoundDecal.ReadBodies())
            {
                if (Measured.Contains(pair.Key)) continue;
                Measured.Add(pair.Key);
                try { Measure(pair.Key, pair.Value); }
                catch (Exception ex) { PhysicsLog.Error("Hitbox probe failed for agent " + pair.Key.Index + ".", ex); }
                if (Measured.Count >= MaxAgents) return;
            }
        }

        // BannerlordVR's capsules for this agent in its rest pose, in Fill's
        // order (bone 1 up, then the head sphere), and the drawn vertices'
        // distances to the nearest axis, per capsule.
        internal static bool Build(Agent agent, List<Vec3> vertices, out List<Capsule> capsules, out List<float>[] distances, out int outside, int stride = 1)
        {
            capsules = null; distances = null; outside = 0;
            MBAgentVisuals visuals = agent.AgentVisuals;
            Skeleton skeleton = visuals?.GetSkeleton();
            if (skeleton == null || vertices.Count < 100) return false;
            int count = skeleton.GetBoneCount();
            if (count < 2 || count > 64) return false;
            var rest = new Vec3[count];
            for (sbyte b = 0; b < count; b++) rest[b] = skeleton.GetBoneEntitialRestFrame(b).origin;
            sbyte pelvis = agent.Monster != null ? agent.Monster.PelvisBoneIndex : (sbyte)-1;
            capsules = new List<Capsule>();
            for (sbyte b = 1; b < count; b++)
            {
                sbyte parent = skeleton.GetParentBoneIndex(b);
                if (parent < 0 || parent >= count) continue;
                BoneBodyPartType part = visuals.GetBoneTypeData(parent).BodyPartType;
                if (part == BoneBodyPartType.None) { if (parent != pelvis) continue; part = BoneBodyPartType.Abdomen; }
                capsules.Add(new Capsule { A = rest[parent], B = rest[b], Radius = PartRadius(part), Bone = parent, Part = part,
                    Name = skeleton.GetBoneName(parent) + ">" + skeleton.GetBoneName(b) });
            }
            sbyte head = agent.Monster != null ? agent.Monster.HeadLookDirectionBoneIndex : (sbyte)-1;
            if (head >= 0 && head < count)
            {
                Vec3 c = rest[head] + new Vec3(0, 0, .1f);
                capsules.Add(new Capsule { A = c, B = c, Radius = .11f, Bone = head, Part = BoneBodyPartType.Head, Name = "head sphere" });
            }
            if (capsules.Count == 0) return false;
            distances = new List<float>[capsules.Count];
            for (int i = 0; i < capsules.Count; i++) distances[i] = new List<float>();
            for (int k = 0; k < vertices.Count; k += Math.Max(1, stride))
            {
                Vec3 v = vertices[k];
                int best = -1; float bestAxis = float.MaxValue, bestSurface = float.MaxValue;
                for (int i = 0; i < capsules.Count; i++)
                {
                    float d = Axis(v, capsules[i].A, capsules[i].B);
                    if (d < bestAxis) { bestAxis = d; best = i; }
                    bestSurface = Math.Min(bestSurface, d - capsules[i].Radius);
                }
                if (best >= 0) distances[best].Add(bestAxis);
                if (bestSurface > .02f) outside++;
            }
            foreach (List<float> d in distances) d.Sort();
            return true;
        }

        private static void Measure(Agent agent, List<Vec3> vertices)
        {
            if (!Build(agent, vertices, out List<Capsule> capsules, out List<float>[] distances, out int outside)) return;
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "HITBOX_FIT agent={0} ({1}): {2} drawn vertices, {3} capsules; {4:F0}% of the drawn surface lies over 2 cm outside every capsule (a blade there passes through)",
                agent.Index, agent.Character?.StringId ?? "?", vertices.Count, capsules.Count, 100f * outside / vertices.Count));
            var lines = new StringBuilder();
            for (int i = 0; i < capsules.Count; i++)
            {
                List<float> d = distances[i];
                if (d.Count < 10) continue;
                float p50 = d[d.Count / 2], p90 = d[(int)(d.Count * .9f)];
                string verdict = capsules[i].Radius < p50 - .02f ? "THIN: blades pass through the drawn body" :
                    capsules[i].Radius > p90 + .03f ? "FAT: strikes empty air" : "fits";
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "HITBOX_FIT agent={0} {1} part={2} capsule r={3:F3} m, drawn surface median {4:F3} m, 90% within {5:F3} m ({6} vertices): {7}",
                    agent.Index, capsules[i].Name, capsules[i].Part, capsules[i].Radius, p50, p90, d.Count, verdict));
            }
            Save(agent, vertices, capsules, distances);
        }

        private static float Axis(Vec3 x, Vec3 a, Vec3 b)
        {
            Vec3 ab = b - a;
            float len = ab.LengthSquared;
            float t = len < 1e-8f ? 0 : Math.Max(0, Math.Min(1, InteractionMath.Dot(x - a, ab) / len));
            return (x - (a + ab * t)).Length;
        }

        private static string F(float v) => v.ToString("F4", CultureInfo.InvariantCulture);
        private static void Save(Agent agent, List<Vec3> vertices, List<Capsule> capsules, List<float>[] distances)
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(PhysicsLog.Path) ?? ".", "HitboxProbe" + (_saved++ == 0 ? "" : "_" + _saved) + ".json");
                var s = new StringBuilder();
                s.Append("{\"agent\":").Append(agent.Index).Append(",\"character\":\"").Append(agent.Character?.StringId ?? "?").Append("\",\"vertices\":[");
                int step = Math.Max(1, vertices.Count / SavedVertices);
                for (int i = 0, n = 0; i < vertices.Count; i += step, n++)
                {
                    if (n > 0) s.Append(',');
                    Vec3 v = vertices[i];
                    s.Append('[').Append(F(v.x)).Append(',').Append(F(v.y)).Append(',').Append(F(v.z)).Append(']');
                }
                s.Append("],\"capsules\":[");
                for (int i = 0; i < capsules.Count; i++)
                {
                    if (i > 0) s.Append(',');
                    Capsule c = capsules[i];
                    List<float> d = distances[i];
                    float p50 = d.Count > 0 ? d[d.Count / 2] : 0, p90 = d.Count > 0 ? d[Math.Min(d.Count - 1, (int)(d.Count * .9f))] : 0;
                    s.Append("{\"name\":\"").Append(c.Name).Append("\",\"part\":\"").Append(c.Part).Append("\",\"r\":").Append(F(c.Radius))
                     .Append(",\"a\":[").Append(F(c.A.x)).Append(',').Append(F(c.A.y)).Append(',').Append(F(c.A.z)).Append("],\"b\":[")
                     .Append(F(c.B.x)).Append(',').Append(F(c.B.y)).Append(',').Append(F(c.B.z)).Append("],\"n\":").Append(d.Count)
                     .Append(",\"p50\":").Append(F(p50)).Append(",\"p90\":").Append(F(p90)).Append('}');
                }
                s.Append("]}");
                File.WriteAllText(path, s.ToString());
                PhysicsLog.Info("HITBOX_FIT agent=" + agent.Index + " saved to " + path);
            }
            catch (Exception ex) { PhysicsLog.Error("Hitbox probe could not save its file.", ex); }
        }
    }
}
