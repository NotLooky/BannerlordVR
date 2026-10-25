using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // B&S lets a sliced part fly as its own body. Bannerlord cannot cut the
    // mesh, so of a severed head the helmet flies: a copy of its mesh in a new
    // dynamic entity, placed where it was drawn (the bone's entity-space frame
    // times the inverse of its rest frame, the record's matrix at 0x60, which
    // takes the skinned mesh from its bind pose to the head) and thrown with
    // the cut. Built like a dropped item (Mission.SpawnWeaponAux): body flags,
    // a sphere body, AddPhysics, physics on.
    internal static class SeveredProp
    {
        // A new mission: the last one's agents and entities are gone.
        internal static void ForgetMission() { Props.Clear(); }
        private struct Prop { public GameEntity Entity; public float Until, Born; public Vec3 Start; public bool Checked; }
        private static readonly List<Prop> Props = new List<Prop>();
        private static Mission _mission;
        private const int MaxProps = 10;
        private const float Life = 60, Mass = 2.5f, Radius = .13f;
        private static bool _broken;

        // Ragdoll.SliceCoroutine: the severed part keeps its velocity times
        // sliceVelocityMultiplier (0.5) and gets sliceSeparationForce (Head
        // 3.5) as a velocity change along the slice direction, away from the
        // stump. Thrown along the blade at a third of its speed it flew metres.
        private const float SliceVelocityMultiplier = .5f, HeadSeparation = 3.5f;
        public static void ThrowHelmet(Agent victim, MatrixFrame boneEntitial, MatrixFrame restEntitial, Vec3 stumpEntitial)
        {
            if (!EnemyPhysicsSettings.SeveredProps || _broken || victim?.AgentVisuals == null) return;
            try
            {
                ItemObject helmet = victim.SpawnEquipment[EquipmentIndex.Head].Item;
                string mesh = helmet?.MultiMeshName;
                Scene scene = Mission.Current?.Scene;
                if (string.IsNullOrEmpty(mesh) || scene == null) return;
                MetaMesh copy = MetaMesh.GetCopy(mesh, false, true);
                if (copy == null) { PhysicsLog.Info("SEVERED no mesh " + mesh); return; }
                if (Mission.Current != _mission) { Props.Clear(); _mission = Mission.Current; }
                while (Props.Count >= MaxProps) { Remove(Props[0]); Props.RemoveAt(0); }

                MatrixFrame global = victim.AgentVisuals.GetGlobalFrame();
                MatrixFrame bind = restEntitial.Inverse();
                MatrixFrame frame = global.TransformToParent(boneEntitial.TransformToParent(bind));
                Vec3 head = global.TransformToParent(boneEntitial).origin;
                GameEntity entity = GameEntity.CreateEmpty(scene, false);
                entity.AddMultiMesh(copy);
                entity.SetGlobalFrame(in frame);
                entity.AddBodyFlags(BodyFlags.Moveable | BodyFlags.DroppedItem, false);
                // The sphere sits on the head, in the mesh's own (bind) space.
                entity.AddSphereAsBody(restEntitial.origin, Radius, BodyFlags.Moveable);
                Vec3 away = global.rotation.TransformToParent(boneEntitial.origin - stumpEntitial);
                away = away.LengthSquared > 1e-6f ? away.NormalizedCopy() : Vec3.Up;
                Vec3 throwVelocity = InteractionMath.ClampMagnitude(victim.Velocity * SliceVelocityMultiplier + away * HeadSeparation, 8);
                Vec3 spin = new Vec3(MBRandom.RandomFloatRanged(-8, 8), MBRandom.RandomFloatRanged(-8, 8), MBRandom.RandomFloatRanged(-4, 4));
                entity.AddPhysics(Mass, restEntitial.origin, null, throwVelocity, spin, Material(), false, -1);
                entity.SetPhysicsStateOnlyVariable(true, true);
                // A copy left asleep stayed where the helmet was drawn, on the
                // corpse, until the blade knocked it: wake it and give it the
                // throw as a velocity change.
                entity.SetPhysicsState(true, true);
                entity.EnableDynamicBody();
                entity.ApplyForceToDynamicBody(throwVelocity, GameEntityPhysicsExtensions.ForceMode.VelocityChange);
                float born = Mission.Current.CurrentTime;
                Props.Add(new Prop { Entity = entity, Until = born + Life, Born = born, Start = entity.GlobalPosition });
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "SEVERED helmet {0} thrown from ({1:F2},{2:F2},{3:F2}) at {4:F1} m/s", helmet.StringId, head.x, head.y, head.z, throwVelocity.Length));
            }
            catch (Exception ex)
            {
                _broken = true;
                PhysicsLog.Error("SEVERED helmet prop failed; severed heads drop no helmet.", ex);
            }
        }

        public static void Tick()
        {
            if (Props.Count == 0) return;
            if (Mission.Current != _mission) { Props.Clear(); return; }
            float now = Mission.Current.CurrentTime;
            for (int i = Props.Count - 1; i >= 0; i--)
            {
                Prop prop = Props[i];
                if (!prop.Checked && now - prop.Born > .5f)
                {
                    prop.Checked = true; Props[i] = prop;
                    try
                    {
                        float moved = (prop.Entity.GlobalPosition - prop.Start).Length;
                        PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "SEVERED helmet copy moved {0:F2} m in 0.5 s (dynamic={1})", moved, prop.Entity.HasDynamicRigidBodyAndActiveSimulation()));
                    }
                    catch (Exception) { }
                }
                if (now > prop.Until) { Remove(prop); Props.RemoveAt(i); }
            }
        }

        private static void Remove(Prop prop)
        {
            try { if (prop.Entity != null) prop.Entity.Remove(79); } catch (Exception) { }
        }

        private static PhysicsMaterial _material;
        private static bool _materialFound;
        private static PhysicsMaterial Material()
        {
            if (_materialFound) return _material;
            _materialFound = true;
            _material = PhysicsMaterial.GetFromIndex(0);
            for (int i = 0; i < PhysicsMaterial.GetMaterialCount(); i++)
            {
                string name = PhysicsMaterial.GetNameAtIndex(i) ?? "";
                if (name.IndexOf("metal", StringComparison.OrdinalIgnoreCase) >= 0) { _material = PhysicsMaterial.GetFromIndex(i); break; }
            }
            return _material;
        }
    }
}

namespace BannerlordVR.VR.Physics
{
    // The helmet of a beheaded body stayed drawn a second after the head was
    // gone from the skinning palette: it is drawn another way while the death
    // animation runs. Its own meshes are hidden instead, wherever they hang:
    // the skeleton's meshes and the head bone's.
    internal static class HelmetHide
    {
        private static bool _logged;
        // A helmet with a padded coif is not a mesh of the skeleton: the whole
        // helmet is one cloth simulator component (none of the skeleton's
        // meshes matched it, three cloth components did). The cloth pipeline
        // draws its own simulated vertices, not the skinning palette, so the
        // head vanished at once while such a helmet stayed until the body was
        // knocked; a helmet without cloth vanished with the head. The helmet's
        // cloth is hidden; any other cloth is left to simulate.
        public static int Cloth(Agent victim, bool reset) => Cloth(victim, reset, null);
        private static int Cloth(Agent victim, bool reset, System.Text.StringBuilder names)
        {
            int n = 0;
            try
            {
                string helmet = victim?.SpawnEquipment[EquipmentIndex.Head].Item?.MultiMeshName;
                if (string.IsNullOrEmpty(helmet)) return 0;
                Skeleton skeleton = victim.AgentVisuals?.GetSkeleton();
                GameEntity entity = victim.AgentVisuals?.GetEntity();
                if (skeleton != null)
                    for (int i = 0; i < skeleton.GetComponentCount(GameEntity.ComponentType.ClothSimulator); i++)
                        if (skeleton.GetComponentAtIndex(GameEntity.ComponentType.ClothSimulator, i) is ClothSimulatorComponent c) n += HideCloth(c, helmet, reset, names);
                if (entity != null)
                    for (int i = 0; i < entity.GetComponentCount(GameEntity.ComponentType.ClothSimulator); i++)
                        if (entity.GetComponentAtIndex(i, GameEntity.ComponentType.ClothSimulator) is ClothSimulatorComponent c) n += HideCloth(c, helmet, reset, names);
            }
            catch (Exception) { }
            return n;
        }
        private static int HideCloth(ClothSimulatorComponent cloth, string helmet, bool reset, System.Text.StringBuilder names)
        {
            MetaMesh mesh = cloth.GetFirstMetaMesh();
            string name = mesh?.GetName() ?? "";
            names?.Append(' ').Append(name);
            if (mesh == null || name.IndexOf(helmet, StringComparison.OrdinalIgnoreCase) < 0) return 0;
            mesh.SetVisibilityMask((VisibilityMaskFlags)0);
            cloth.SetMaxDistanceMultiplier(0);
            if (reset) cloth.SetResetRequired();
            names?.Append("(hidden)");
            return 1;
        }
        public static void Hide(Agent victim, sbyte head)
        {
            try
            {
                Skeleton skeleton = victim?.AgentVisuals?.GetSkeleton();
                if (skeleton == null) return;
                string helmet = victim.SpawnEquipment[EquipmentIndex.Head].Item?.MultiMeshName;
                if (string.IsNullOrEmpty(helmet)) return;
                var names = new System.Text.StringBuilder();
                int hidden = 0;
                int count = skeleton.GetComponentCount(GameEntity.ComponentType.MetaMesh);
                for (int i = 0; i < count; i++)
                    if (skeleton.GetComponentAtIndex(GameEntity.ComponentType.MetaMesh, i) is MetaMesh mesh)
                    {
                        string name = mesh.GetName() ?? "";
                        if (!_logged) names.Append(' ').Append(name);
                        if (name.StartsWith(helmet, StringComparison.OrdinalIgnoreCase)) { mesh.SetVisibilityMask((VisibilityMaskFlags)0); hidden++; }
                    }
                // The equipment's meshes hang on the agent's entity (the
                // skeleton itself only held boots and vambraces in game).
                GameEntity entity = victim.AgentVisuals.GetEntity();
                int onEntity = entity?.MultiMeshComponentCount ?? 0;
                for (int i = 0; i < onEntity; i++)
                {
                    MetaMesh mesh = entity.GetMetaMesh(i);
                    if (mesh == null) continue;
                    string name = mesh.GetName() ?? "";
                    if (!_logged) names.Append(" [entity] ").Append(name);
                    if (name.StartsWith(helmet, StringComparison.OrdinalIgnoreCase)) { mesh.SetVisibilityMask((VisibilityMaskFlags)0); hidden++; }
                }
                // Child entities (attached prefabs, the equipment's pieces).
                int children = entity?.ChildCount ?? 0;
                for (int c = 0; c < children; c++)
                {
                    GameEntity child = entity.GetChild(c);
                    if (child == null) continue;
                    if (!_logged) names.Append(" [child ").Append(child.Name).Append(']');
                    for (int i = 0; i < child.MultiMeshComponentCount; i++)
                    {
                        MetaMesh mesh = child.GetMetaMesh(i);
                        string name = mesh?.GetName() ?? "";
                        if (!_logged) names.Append(' ').Append(name);
                        if (mesh != null && name.StartsWith(helmet, StringComparison.OrdinalIgnoreCase)) { mesh.SetVisibilityMask((VisibilityMaskFlags)0); hidden++; }
                    }
                }
                int onBone = skeleton.GetBoneComponentCount(head);
                for (int i = 0; i < onBone; i++)
                    if (skeleton.GetBoneComponentAtIndex(head, i) is MetaMesh mesh)
                    {
                        if (!_logged) names.Append(" [head bone] ").Append(mesh.GetName());
                        mesh.SetVisibilityMask((VisibilityMaskFlags)0); hidden++;
                    }
                // The skeleton's own meshes, the skin included: the helmet's
                // pieces are here, not among its components. Each is hidden
                // unless it is the shared resource (every agent would lose it).
                var skinned = new System.Text.StringBuilder();
                foreach (Mesh mesh in skeleton.GetAllMeshes())
                {
                    string name = mesh?.Name ?? "";
                    skinned.Append(' ').Append(name);
                    if (name.IndexOf(helmet, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Mesh shared = Mesh.GetFromResource(name);
                    if (shared != null && shared.Pointer == mesh.Pointer) { skinned.Append("(shared)"); continue; }
                    mesh.SetVisibilityMask((VisibilityMaskFlags)0);
                    skinned.Append("(hidden)");
                    hidden++;
                }
                var clothNames = new System.Text.StringBuilder();
                int cloth = Cloth(victim, true, clothNames);
                PhysicsLog.Info("SEVERED helmet " + helmet + " meshes hidden=" + hidden + " helmet cloth=" + cloth + "; cloth:" + clothNames + "; skinned:" + skinned +
                    (_logged ? "" : "; skeleton components meta=" + count + " cloth=" + skeleton.GetComponentCount(GameEntity.ComponentType.ClothSimulator) +
                    " composite=" + skeleton.GetComponentCount(GameEntity.ComponentType.CompositeComponent) +
                    "; entity cloth=" + (entity?.GetComponentCount(GameEntity.ComponentType.ClothSimulator) ?? 0) +
                    " composite=" + (entity?.GetComponentCount(GameEntity.ComponentType.CompositeComponent) ?? 0) +
                    " children=" + (entity?.ChildCount ?? 0) + "; meshes:" + names));
                _logged = true;
            }
            catch (Exception ex) { PhysicsLog.Error("SEVERED helmet hide failed.", ex); }
        }
    }
}
