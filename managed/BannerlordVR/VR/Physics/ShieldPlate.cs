using System;
using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // BannerlordVR fits an enemy's shield plate from its mesh and places it
    // on the off-hand bone, with or without the usage frame (plate_frame,
    // guessed at run time); the box often missed the shield drawn, and blades
    // stopped in the air or went through. The shield's own weapon entity is
    // where it is drawn: its local bounding box in its global frame gives
    // the plate (the thinnest axis is the normal). VrCollide and the solid
    // shields both read this plate.
    internal static class ShieldPlate
    {
        private static bool _logged, _compared;
        public const float MinHalfThickness = .03f;

        private static readonly Vec3[] Axes = new Vec3[3];
        private static readonly float[] Half = new float[3];
        public static bool FromEntity(Agent agent, out VrBlade.Plate plate)
        {
            plate = default(VrBlade.Plate);
            if (agent == null || !agent.IsActive()) return false;
            EquipmentIndex slot = agent.GetOffhandWieldedItemIndex();
            if (slot == EquipmentIndex.None || agent.Equipment[slot].IsEmpty) return false;
            WeaponComponentData usage = agent.Equipment[slot].CurrentUsageItem;
            if (usage == null || !usage.IsShield) return false;
            WeakGameEntity entity = agent.GetWeaponEntityFromEquipmentSlot(slot);
            if (!entity.IsValid) return false;
            BoundingBox box = entity.GetLocalBoundingBox();
            if (!InteractionMath.IsFinite(box.min) || !InteractionMath.IsFinite(box.max) || (box.max - box.min).LengthSquared < 1e-4f) return false;
            MatrixFrame frame = entity.GetGlobalFrame();
            // Scratch arrays: this runs for every shield bearer BannerlordVR
            // tests each frame.
            Vec3[] axes = Axes; float[] half = Half;
            axes[0] = frame.rotation.s; axes[1] = frame.rotation.f; axes[2] = frame.rotation.u;
            half[0] = (box.max.x - box.min.x) * .5f; half[1] = (box.max.y - box.min.y) * .5f; half[2] = (box.max.z - box.min.z) * .5f;
            for (int i = 0; i < 3; i++)
            {
                float length = axes[i].Length;
                if (length < 1e-4f) return false;
                half[i] *= length; axes[i] = axes[i] * (1 / length);
            }
            int n = half[0] <= half[1] && half[0] <= half[2] ? 0 : half[1] <= half[2] ? 1 : 2;
            int a = (n + 1) % 3, b = (n + 2) % 3;
            plate = new VrBlade.Plate
            {
                Valid = true, Centre = frame.TransformToParent((box.min + box.max) * .5f),
                A = axes[a], B = axes[b], N = axes[n], HalfA = half[a], HalfB = half[b], HalfN = Math.Max(.01f, half[n]),
                Bone = agent.Monster != null ? agent.Monster.OffHandItemBoneIndex : (sbyte)-1, Slot = slot
            };
            if (!_logged)
            {
                _logged = true;
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "SHIELD plate from the shield entity: {0:F2} x {1:F2} x {2:F2} m", plate.HalfA * 2, plate.HalfB * 2, plate.HalfN * 2));
            }
            return true;
        }

        [HarmonyPatch]
        internal static class EnemyPlatePatch
        {
            // Off unless enemy_physics: without it the game plays as without this patch.
            public static bool Prepare() => EnemyPhysicsSettings.Configured;
            public static MethodBase TargetMethod() => AccessTools.Method(typeof(VrBlade), "TryGetEnemyPlate");

            [HarmonyPostfix]
            public static void Postfix(Agent agent, ref VrBlade.Plate plate, ref bool __result)
            {
                if (!EnemyPhysicsSettings.Enabled) return;
                // VrBlade's plate is where the shield is drawn but has no
                // thickness (HalfN 0 in the logs): a blade crossed its plane
                // between two frames. It is given a shield's thickness.
                if (!EnemyPhysicsSettings.EntityShieldPlates)
                {
                    if (__result && plate.Valid && plate.HalfN < MinHalfThickness) plate.HalfN = MinHalfThickness;
                    return;
                }
                try
                {
                    VrBlade.Plate original = plate; bool had = __result;
                    if (FromEntity(agent, out VrBlade.Plate fitted))
                    {
                        if (!_compared && had && original.Valid)
                        {
                            _compared = true;
                            PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "SHIELD compare: BannerlordVR {0:F2}x{1:F2}x{2:F2} vs entity {3:F2}x{4:F2}x{5:F2}; centres {6:F2} m apart; normals {7:F0} deg apart",
                                original.HalfA * 2, original.HalfB * 2, original.HalfN * 2, fitted.HalfA * 2, fitted.HalfB * 2, fitted.HalfN * 2,
                                (original.Centre - fitted.Centre).Length,
                                Math.Acos(Math.Min(1, Math.Abs(InteractionMath.Dot(original.N.NormalizedCopy(), fitted.N.NormalizedCopy())))) * 57.3));
                        }
                        plate = fitted; __result = true;
                    }
                }
                catch (Exception) { }
            }
        }
    }
}
