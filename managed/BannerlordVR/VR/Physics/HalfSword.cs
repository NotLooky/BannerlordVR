using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // B&S lets the free hand take hold of any weapon anywhere along it, the
    // blade of a one-handed sword included (half-swording). BannerlordVR's
    // two-hand grip already slides the off hand from the grip to the tip but
    // only offers it for two-handed weapons and polearms: a one-handed melee
    // weapon is offered too while the off hand holds nothing (no shield).
    // Only the grip's own eligibility is widened: VrGrab's TwoHandable, which
    // keeps the off hand from picking things up, is left as it is.
    [HarmonyPatch]
    internal static class HalfSword
    {
        private static MethodBase Target => AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrTwoHand"), "Eligible");
        public static bool Prepare() => EnemyPhysicsSettings.Configured && (EnemyPhysicsSettings.HalfSword && Target != null);
        public static MethodBase TargetMethod() => Target;
        public static void Postfix(Agent agent, VrHandSlot main, ref string __result)
        {
            if (__result != null || agent == null || !main.Valid || !EnemyPhysicsSettings.Enabled) return;
            EquipmentIndex slot = main.Equipment;
            if (slot < EquipmentIndex.WeaponItemBeginSlot || slot >= EquipmentIndex.ExtraWeaponSlot) return;
            MissionWeapon weapon = agent.Equipment[slot];
            WeaponComponentData usage = weapon.IsEmpty ? null : weapon.CurrentUsageItem;
            if (usage == null || !usage.IsMeleeWeapon || usage.IsShield || !agent.WieldedOffhandWeapon.IsEmpty) return;
            __result = weapon.Item?.StringId ?? "?";
        }
    }
}
