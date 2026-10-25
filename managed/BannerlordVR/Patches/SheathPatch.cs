using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;
using BannerlordVR.VR;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// TEST 350. The player's sheaths are drawn by us, never by the engine.
    ///
    /// TESTs 336-338 looked for the vanilla sheath on the slot entity, then on
    /// the holster bone, and found it on neither. It is a CRAFTED item's holster
    /// look: TaleWorlds.MountAndBlade.View's OnGetWeaponData fills
    /// WeaponData.HolsterMesh (the empty sheath) and HolsterMeshWithWeapon (the
    /// sheath with the blade in it) from the item's CraftedDataView, and every
    /// equip hands them to native through this private Agent.WeaponEquipped.
    /// ItemObject.HolsterMeshName is empty for a crafted item, which is why the
    /// TEST 328 probe said "no scabbard meshes".
    ///
    /// For the player's crafted, non-consumable weapons both are nulled here -
    /// null is what every item without a holster sends, so native already
    /// handles it - and VrHolsterView hangs its own copies of the same looks.
    /// Javelin bundles keep theirs: they sit on the slot entity, where TEST
    /// 334/335 already hang them right.
    ///
    /// Mission.BuildAgent sets Controller = Player before
    /// EquipItemsFromSpawnEquipment, so the spawn equip is caught too. Known
    /// limit: a troop taken over after the player's death was equipped before
    /// it was the player's, and keeps the engine's sheath.
    ///
    /// The parameter names are the shipped method's own (Harmony binds by name).
    /// </summary>
    [HarmonyPatch]
    public static class SheathPatch
    {
        private static readonly HashSet<string> _logged = new HashSet<string>();

        private static MethodBase TargetMethod()
        {
            Type data = typeof(WeaponData).MakeByRefType();
            return AccessTools.Method(typeof(Agent), "WeaponEquipped", new[]
            {
                typeof(EquipmentIndex), data, typeof(WeaponStatsData[]), data, typeof(WeaponStatsData[]),
                typeof(WeakGameEntity), typeof(bool), typeof(bool),
            });
        }

        [HarmonyPrefix]
        private static void Prefix(Agent __instance, EquipmentIndex equipmentSlot, ref WeaponData weaponData)
        {
            try
            {
                if (!VrSystem.IsActive || !VrHolsterView.Enabled || __instance == null
                    || __instance.Controller != AgentControllerType.Player
                    || equipmentSlot < EquipmentIndex.WeaponItemBeginSlot || equipmentSlot > EquipmentIndex.Weapon3
                    || (weaponData.HolsterMesh == null && weaponData.HolsterMeshWithWeapon == null))
                    return;

                ItemObject item = __instance.Equipment[equipmentSlot].Item;
                if (!VrHolsterView.OwnsSheath(item))
                    return;

                if (_logged.Add(item.StringId))
                    VrLog.Info("TEST 350: '" + item.StringId + "' equipped without the engine's sheath (full '"
                               + Name(weaponData.HolsterMeshWithWeapon) + "', empty '" + Name(weaponData.HolsterMesh)
                               + "'); VrHolsterView hangs ours.");

                weaponData.HolsterMesh = null;
                weaponData.HolsterMeshWithWeapon = null;
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 350: stripping the engine's sheath failed; it stays as vanilla.", ex);
            }
        }

        private static string Name(MetaMesh meta) => meta == null ? "-" : meta.GetName();
    }
}
