using System.Globalization;
using TaleWorlds.Library;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // B&S BrainModuleEquipment.allowArmStabDisarm: a pierce that enters an
    // arm or a hand makes that hand let go of what it holds. Bannerlord
    // holds the main weapon in the right hand and the shield or off-hand
    // weapon in the left (a bow in the left). A weapon held in both hands
    // stays in the other one, as in B&S where only the stabbed hand releases.
    internal static class Disarm
    {
        public static void ArmStab(Agent agent, sbyte bone, BoneBodyPartType part, string boneName)
        {
            if (agent == null || !agent.IsActive() || !EnemyPhysicsSettings.ArmStabDisarm) return;
            int side = Side(part, boneName);
            if (side == 0) return;
            EquipmentIndex main = agent.GetPrimaryWieldedItemIndex();
            EquipmentIndex off = agent.GetOffhandWieldedItemIndex();
            WeaponComponentData mainUsage = main == EquipmentIndex.None || agent.Equipment[main].IsEmpty ? null : agent.Equipment[main].CurrentUsageItem;
            bool leftHolder = mainUsage != null && (mainUsage.WeaponClass == WeaponClass.Bow);
            bool bothHands = mainUsage != null && mainUsage.WeaponFlags.HasAnyFlag(WeaponFlags.NotUsableWithOneHand);
            EquipmentIndex drop = EquipmentIndex.None;
            if (side < 0) drop = off != EquipmentIndex.None ? off : leftHolder ? main : EquipmentIndex.None;
            else if (!leftHolder) drop = main;
            if (drop == EquipmentIndex.None || agent.Equipment[drop].IsEmpty) return;
            if (drop == main && bothHands && !leftHolder) return;
            string item = agent.Equipment[drop].Item?.StringId ?? "?";
            agent.DropItem(drop);
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "DISARM agent={0} hand={1} bone={2} item={3}", agent.Index, side < 0 ? "left" : "right", boneName, item));
        }

        // -1 left arm or hand, 1 right, 0 elsewhere (shoulders and clavicles
        // are not the B&S arm parts).
        public static int Side(BoneBodyPartType part, string boneName)
        {
            if (part == BoneBodyPartType.ArmLeft) return -1;
            if (part == BoneBodyPartType.ArmRight) return 1;
            string name = boneName ?? "";
            if (name.Contains("clavicle") || name.Contains("shoulder")) return 0;
            bool arm = name.Contains("arm") || name.Contains("hand") || name.Contains("finger") || name.Contains("thumb") || name.Contains("wrist");
            if (!arm) return 0;
            return name.StartsWith("l_") ? -1 : name.StartsWith("r_") ? 1 : 0;
        }
    }
}
