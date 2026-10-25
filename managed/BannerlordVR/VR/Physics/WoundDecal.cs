using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Blade & Sorcery EffectModuleReveal / RevealDecal: every cut and stab
    // leaves a wound where it landed, on the skin or the armour, that stays
    // and moves with the body. Bannerlord has no reveal mask on its skinned
    // meshes, its blood decals are terrain decals, and a mesh put on a bone
    // with Skeleton.AddMeshToBone was not drawn (both tried in game, 23 Oct
    // 2026). A small quad of the game's blood texture is built the way
    // BannerlordVR builds the meshes it draws (default material copy,
    // ManagedMeshEditOperations, FinalizeEditing, on a dynamic scene entity)
    // and that entity is put back on the struck bone every frame, after the
    // engine posed the skeleton: round for a stab, drawn along the swing for
    // a cut.
    internal static class WoundDecal
    {
        // The game's blood splatters are terrain decals: their colour maps are
        // dark, the blood in the alpha the default material ignores (drawn
        // black in game). BannerlordVR's own drawn meshes are plain_white
        // under vertex colours; a wound is drawn that way, as a blob of
        // blood with a ragged edge.
        private const int RimPoints = 12;
        // BannerlordVR colours its meshes with Mesh.Color (ARGB) over vertex
        // colours: the blood red is the mesh's, the vertices only shade it,
        // the core darker than the rim.
        private const uint Blood = 0xFF9A0E0E;
        private static readonly Vec3 Core = new Vec3(.55f, .55f, .55f), Rim = new Vec3(1, 1, 1);
        private const int PerAgent = 10, Total = 60;
        private const float StabSize = .07f, CutLength = .16f, CutWidth = .05f;
        // The contact is on the body's collision capsule, under the armour:
        // the wound is laid out on the armour's surface (a mail sleeve or a
        // plate covered it in game).
        private static float Lift(Agent agent, BoneBodyPartType part)
        {
            if (part == BoneBodyPartType.Head && agent.SpawnEquipment != null && !agent.SpawnEquipment[EquipmentIndex.Head].IsEmpty)
                return HelmetVolume.Margin + .01f;
            switch (BodyPenetration.MaterialFor(agent, part))
            {
                case PenetrationMaterial.Leather: return .025f;
                case PenetrationMaterial.Chainmail: return .03f;
                case PenetrationMaterial.Plate: return .045f;
                default: return .015f;
            }
        }
        private struct Wound { public Agent Agent; public sbyte Bone; public MatrixFrame Local; public GameEntity Entity; }

        // The drawn surface. The contact is on a collision capsule, a stock
        // radius per body part: wounds sat under a mail sleeve, or floated
        // off a slim arm. The agent's drawn meshes (body, armour, cloth) are
        // read in their rest pose, as BannerlordVR reads its bow and hands
        // (AddSkinnedMeshWithColor, GetPositionOfVertex), and every point of
        // a wound goes on the outermost vertex out from the bone's line there.
        // Read at once, 47 000 vertices cost 41-44 ms on the hit's frame: they
        // are read a few at a time from the moment the physics takes the
        // agent, one vertex in three.
        private const int MaxVertices = 60000, CachedAgents = 8, Stride = 3, ReadsPerFrame = 1200;
        private const float SliceAlong = .03f, SliceCos = .94f, OnSurface = .003f, SettleSeconds = 1;
        private sealed class Reader
        {
            public readonly List<Vec3> Vertices = new List<Vec3>(16000);
            public int MeshIndex, Vertex, Count = -1;
            public float StartAt;
            public Mesh Copy; public ManagedMeshEditOperations Ops;
            public bool Done;
        }
        private static readonly Dictionary<Agent, Reader> Readers = new Dictionary<Agent, Reader>();
        private static readonly List<Agent> ReaderOrder = new List<Agent>();

        // From the physics' admission: start reading this agent's surface.
        public static void Prepare(Agent agent)
        {
            if (_broken || FastAccess.VertexCount == null || agent == null || Readers.ContainsKey(agent) ||
                !(EnemyPhysicsSettings.Wounds || HitboxProbe.Enabled || HitboxFit.Wants(agent))) return;
            // A second after it is taken: a body just placed or equipping
            // rebuilds its visuals.
            Readers[agent] = new Reader { StartAt = (Mission.Current?.CurrentTime ?? 0) + SettleSeconds }; ReaderOrder.Add(agent);
            while (ReaderOrder.Count > CachedAgents) { Close(Readers[ReaderOrder[0]]); Readers.Remove(ReaderOrder[0]); ReaderOrder.RemoveAt(0); }
        }
        private static void Close(Reader r)
        {
            try { r.Ops?.ManualInvalidate(); r.Copy?.ManualInvalidate(); } catch (Exception) { }
            r.Ops = null; r.Copy = null;
        }
        // The engine's meshes are only touched within the frame they are asked
        // for: kept from one frame to the next, a body that re-equipped had
        // freed them and the copy crashed the game (23 Oct 2026). Each mesh
        // is copied whole into one of our own, then read from that copy.
        private static Mesh MeshAt(Skeleton skeleton, int index)
        {
            int i = 0;
            foreach (Mesh mesh in skeleton.GetAllMeshes()) if (i++ == index) return mesh;
            return null;
        }
        private static void Read(int budget)
        {
            float now = Mission.Current?.CurrentTime ?? 0;
            foreach (Agent agent in ReaderOrder)
            {
                Reader r = Readers[agent];
                if (r.Done || now < r.StartAt) continue;
                MBAgentVisuals visuals = agent.AgentVisuals;
                Skeleton skeleton = agent.IsActive() && visuals != null && visuals.IsValid() ? visuals.GetSkeleton() : null;
                if (skeleton == null) { Close(r); r.Done = true; continue; }
                while (budget > 0 && !r.Done)
                {
                    if (r.Ops == null)
                    {
                        if (r.Vertices.Count >= MaxVertices) { r.Done = true; break; }
                        Mesh source = MeshAt(skeleton, r.MeshIndex++);
                        if (source == null) { r.Done = true; break; }
                        Material material = source.GetMaterial();
                        if (material == null) continue;
                        r.Copy = Mesh.CreateMeshWithMaterial(material);
                        r.Ops = ManagedMeshEditOperations.Create(r.Copy);
                        r.Ops.AddSkinnedMeshWithColor(source, MatrixFrame.Identity, new Vec3(1, 1, 1), true);
                        r.Count = FastAccess.VertexCount(r.Ops); r.Vertex = 0;
                        // One mesh copy a frame: several in one frame were a
                        // 4-11 ms frame while a body was read.
                        return;
                    }
                    for (; r.Vertex < r.Count && budget > 0 && r.Vertices.Count < MaxVertices; r.Vertex += Stride, budget--)
                        r.Vertices.Add(r.Ops.GetPositionOfVertex(r.Vertex));
                    if (r.Vertex >= r.Count || r.Vertices.Count >= MaxVertices) Close(r);
                }
                if (budget <= 0) return;
            }
        }

        // The rest-pose surface out of the bone's line through p (along the
        // bone at p's height); false when nothing is drawn there.
        private static bool Surface(List<Vec3> vertices, Vec3 a, Vec3 axis, Vec3 p, out Vec3 point)
        {
            point = p;
            float along = InteractionMath.Dot(p - a, axis);
            Vec3 line = a + axis * along;
            Vec3 n = p - line;
            if (n.Length < 1e-3f) return false;
            n.Normalize();
            float best = -1;
            for (int i = 0; i < vertices.Count; i++)
            {
                Vec3 d = vertices[i] - a;
                float t = InteractionMath.Dot(d, axis);
                if (Math.Abs(t - along) > SliceAlong) continue;
                Vec3 radial = d - axis * t;
                float r = radial.Length;
                if (r < 1e-3f || r > .5f || InteractionMath.Dot(radial, n) < r * SliceCos) continue;
                if (r > best) best = r;
            }
            if (best < 0) return false;
            point = line + n * (best + OnSurface);
            return true;
        }
        private static readonly List<Wound> Wounds = new List<Wound>();
        private static Material _material;
        private static Mission _mission;
        private static bool _broken, _logged, _searched;

        // At each mission start (loading), so the texture is in before a hit.
        internal static void ForgetMission()
        {
            Wounds.Clear(); _mission = null;
            foreach (Reader r in Readers.Values) Close(r);
            Readers.Clear(); ReaderOrder.Clear();
            if (EnemyPhysicsSettings.Wounds) FindMaterial();
        }

        public static void Stab(Agent agent, sbyte bone, Vec3 contact, Vec3 direction) => Add(agent, bone, contact, direction, false);
        public static void Cut(Agent agent, sbyte bone, Vec3 contact, Vec3 direction) => Add(agent, bone, contact, direction, true);

        private static Material FindMaterial()
        {
            if (_searched) return _material;
            _searched = true;
            try
            {
                Material source = Material.GetDefaultMaterial();
                Texture white = Texture.GetFromResource("plain_white");
                if (source == null || white == null) { PhysicsLog.Info("WOUND no default material or plain_white; wounds are not drawn."); return null; }
                white.PreloadTexture(true);
                _material = source.CreateCopy();
                _material.SetTexture(Material.MBTextureType.DiffuseMap, white);
                PhysicsLog.Info("WOUND mesh material default with plain_white under vertex colours");
            }
            catch (Exception ex) { Fail(ex.Message); }
            return _material;
        }

        // A fan: a dark core and a ragged rim, its points at random radii in
        // the wound's plane (x along, y across), each then laid on the drawn
        // surface by place (bone space) so the wound follows the body's curve.
        private static Mesh Blob(Material material, bool cut, Func<Vec2, Vec3> place, Vec3 outward)
        {
            Mesh mesh = Mesh.CreateMeshWithMaterial(material);
            ManagedMeshEditOperations ops = ManagedMeshEditOperations.Create(mesh);
            Vec3 down = -outward, centre = place(Vec2.Zero);
            Vec2 uv = new Vec2(.5f, .5f);
            var rim = new Vec3[RimPoints];
            for (int k = 0; k < RimPoints; k++)
            {
                double angle = 2 * Math.PI * k / RimPoints;
                float r = .5f * MBRandom.RandomFloatRanged(cut ? .8f : .6f, 1f);
                rim[k] = place(new Vec2((float)Math.Cos(angle) * r, (float)Math.Sin(angle) * r));
            }
            for (int k = 0; k < RimPoints; k++)
            {
                Vec3 a = rim[k], b = rim[(k + 1) % RimPoints];
                ops.AddTriangle(centre, a, b, outward, outward, outward, uv, uv, uv, Core, Rim, Rim);
                // The back face too: which way the engine culls is not known here.
                ops.AddTriangle(centre, b, a, down, down, down, uv, uv, uv, Core, Rim, Rim);
            }
            ops.FinalizeEditing();
            mesh.RecomputeBoundingBox();
            mesh.Color = Blood;
            return mesh;
        }

        private static void Add(Agent agent, sbyte bone, Vec3 contact, Vec3 direction, bool cut)
        {
            if (_broken || !EnemyPhysicsSettings.Wounds || agent?.AgentVisuals == null || bone < 0 || !InteractionMath.IsFinite(contact)) return;
            try
            {
                Mission mission = Mission.Current;
                Scene scene = mission?.Scene;
                if (scene == null) return;
                if (mission != _mission) { Wounds.Clear(); _mission = mission; }
                Material material = FindMaterial();
                if (material == null) return;
                Skeleton skeleton = agent.AgentVisuals.GetSkeleton();
                if (skeleton == null || bone >= skeleton.GetBoneCount()) return;
                MatrixFrame visuals = agent.AgentVisuals.GetGlobalFrame();
                MatrixFrame boneWorld = visuals.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(bone));
                // Out of the body: from the bone's line to the contact; a hit
                // on the line itself faces back along the blow.
                Vec3 a = boneWorld.origin, b = a;
                if (skeleton.GetBoneChildCount(bone) > 0)
                    b = visuals.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(skeleton.GetBoneChildAtIndex(bone, 0))).origin;
                Vec3 ab = b - a;
                float t = ab.LengthSquared > 1e-6f ? Math.Max(0, Math.Min(1, InteractionMath.Dot(contact - a, ab) / ab.LengthSquared)) : 0;
                Vec3 normal = contact - (a + ab * t);
                if (normal.Length < .02f) normal = -direction;
                if (normal.Length < 1e-4f) return;
                normal.Normalize();
                // Across the surface: a cut is drawn along the blow's path.
                Vec3 along = direction - normal * InteractionMath.Dot(direction, normal);
                if (along.Length < 1e-3f) along = Math.Abs(normal.z) < .9f ? Vec3.Up - normal * normal.z : Vec3.Forward;
                along.Normalize();
                Vec3 side = Vec3.CrossProduct(normal, along);
                float jitter = MBRandom.RandomFloatRanged(.8f, 1.2f);
                float length = (cut ? CutLength : StabSize) * jitter, width = (cut ? CutWidth : StabSize) * jitter;
                BoneBodyPartType part = agent.AgentVisuals.GetBoneTypeData(bone).BodyPartType;
                Vec3 centreWorld = contact + normal * Lift(agent, part);
                // Bone space: the mesh is built there and its entity put on the
                // bone each frame. The rest pose has the same bone space.
                Vec3 localCentre = boneWorld.TransformToLocalNonOrthogonal(centreWorld);
                Vec3 localAlong = boneWorld.rotation.TransformToLocal(along), localSide = boneWorld.rotation.TransformToLocal(side);
                Vec3 localNormal = boneWorld.rotation.TransformToLocal(normal);
                if (!Readers.ContainsKey(agent)) Prepare(agent);
                List<Vec3> vertices = Readers.TryGetValue(agent, out Reader reader) && reader.Vertices.Count > 0 ? reader.Vertices : null;
                MatrixFrame restBone = skeleton.GetBoneEntitialRestFrame(bone);
                Vec3 restA = restBone.origin, restAxis = Vec3.Zero;
                if (skeleton.GetBoneChildCount(bone) > 0)
                {
                    restAxis = skeleton.GetBoneEntitialRestFrame(skeleton.GetBoneChildAtIndex(bone, 0)).origin - restA;
                    if (restAxis.Length > 1e-4f) restAxis.Normalize(); else restAxis = Vec3.Zero;
                }
                int fitted = 0;
                Func<Vec2, Vec3> place = q =>
                {
                    Vec3 local = localCentre + localAlong * (q.x * length) + localSide * (q.y * width);
                    if (vertices == null) return local;
                    Vec3 rest = restBone.TransformToParent(local);
                    if (!Surface(vertices, restA, restAxis, rest, out Vec3 onSurface)) return local;
                    fitted++;
                    return restBone.TransformToLocalNonOrthogonal(onSurface);
                };
                Perf.Mark("wound_place");
                Mesh blob = Blob(material, cut, place, localNormal);
                Perf.Mark("wound_mesh");
                GameEntity entity = GameEntity.CreateEmptyDynamic(scene, false);
                entity.AddMesh(blob, true);
                entity.SetGlobalFrame(in boneWorld);
                MatrixFrame local0 = MatrixFrame.Identity;
                Wounds.Add(new Wound { Agent = agent, Bone = bone, Local = local0, Entity = entity });
                Trim(agent);
                if (!_logged)
                {
                    _logged = true;
                    PhysicsLog.Info("WOUND first wound on agent " + agent.Index + " bone " + skeleton.GetBoneName(bone) + (cut ? " (cut)" : " (stab)") +
                        " surface=" + (vertices == null ? "capsule (vertices not read yet)" : fitted + "/" + (RimPoints + 1) + " points on the drawn mesh (" + vertices.Count + " vertices)") +
                        " at (" + contact.x.ToString("F2") + "," + contact.y.ToString("F2") + "," + contact.z.ToString("F2") + ")");
                }
            }
            catch (Exception ex) { Fail(ex.Message); }
        }

        // In the mission tick, where the engine's meshes were read safely
        // (after the agents' async tick it crashed): a share of a body's
        // surface each frame.
        // The bodies whose drawn surface has been read whole (rest pose).
        public static IEnumerable<KeyValuePair<Agent, List<Vec3>>> ReadBodies()
        {
            foreach (Agent agent in ReaderOrder)
                if (Readers.TryGetValue(agent, out Reader r) && r.Done && r.Vertices.Count > 0)
                    yield return new KeyValuePair<Agent, List<Vec3>>(agent, r.Vertices);
        }

        public static void ReadSurfaces()
        {
            if (_broken || ReaderOrder.Count == 0) return;
            try { Read(ReadsPerFrame); }
            catch (Exception ex) { Fail("reading a body's surface: " + ex.Message); }
        }

        // After the agents' tick: each wound back on its bone. A body whose
        // visuals are gone (a filed corpse) keeps its wounds where they are.
        public static void Tick()
        {
            if (_broken || Wounds.Count == 0) return;
            if (Mission.Current != _mission) { Wounds.Clear(); return; }
            try
            {
                foreach (Wound w in Wounds)
                {
                    MBAgentVisuals visuals = w.Agent?.AgentVisuals;
                    if (visuals == null || !visuals.IsValid()) continue;
                    Skeleton skeleton = visuals.GetSkeleton();
                    if (skeleton == null || w.Bone >= skeleton.GetBoneCount()) continue;
                    MatrixFrame frame = visuals.GetGlobalFrame().TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(w.Bone)).TransformToParent(w.Local);
                    w.Entity.SetGlobalFrame(in frame);
                }
            }
            catch (Exception ex) { Fail(ex.Message); }
        }

        // Oldest first: ten on one body, sixty in the battle.
        private static void Trim(Agent agent)
        {
            int mine = 0;
            for (int i = Wounds.Count - 1; i >= 0; i--) if (Wounds[i].Agent == agent && ++mine > PerAgent) Remove(i);
            while (Wounds.Count > Total) Remove(0);
        }
        private static void Remove(int i)
        {
            Wound w = Wounds[i];
            Wounds.RemoveAt(i);
            try { w.Entity?.Remove(79); } catch (Exception) { }
        }

        private static void Fail(string why)
        {
            _broken = true;
            PhysicsLog.Info("WOUND wounds stopped for the session: " + why);
        }
    }
}
