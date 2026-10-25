using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using BannerlordVR.VR;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// TEST 324. A weapon the player drops lies visible on the ground.
    ///
    /// Every drop - the G key, OnItemPickup replacing a full slot, death - comes
    /// through Mission.SpawnWeaponAsDropFromAgentAux, which takes the SLOT
    /// ENTITY itself and makes it the ground item. VrSkinnedWeapon had already
    /// taken that entity's meshes off (TEST 229) and only gives them back while
    /// the slot still holds it, so the ground item had nothing to draw. The
    /// prefix runs while it still does.
    ///
    /// TEST 324c: that covers a weapon in a HAND. A weapon dropped while
    /// sheathed (in the run that found this: sheathe at the hip, then G) was
    /// still invisible, for another reason: the engine itself masks the player's
    /// sheathed weapons out of the final view in first person (TEST 328: a
    /// sword at the hip mask 0, a shield on the back 32 = shadow only), and a
    /// ground item is no longer the agent's, so nothing ever unmasks it. The
    /// postfix shows a dropped item none of whose meshes is in the final view.
    ///
    /// The parameter names are the shipped method's own (Harmony binds by name).
    /// </summary>
    [HarmonyPatch]
    public static class DropPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Mission), "SpawnWeaponAsDropFromAgentAux");
        }

        [HarmonyPrefix]
        private static void Prefix(Agent agent, EquipmentIndex equipmentIndex, out WeakGameEntity __state)
        {
            __state = WeakGameEntity.Invalid;
            try
            {
                if (agent == null || !agent.IsMainAgent)
                    return;

                __state = agent.GetWeaponEntityFromEquipmentSlot(equipmentIndex);

                // TEST 336: a holstered weapon's own meshes are off while our copy
                // hangs; they go back on before the slot entity becomes the item.
                VrHolsterView.RestoreSlot(agent, equipmentIndex);

                if (VrSkinnedWeapon.RestoreSlot(agent, equipmentIndex))
                    VrLog.Info("TEST 324 drop: slot " + equipmentIndex + " '"
                               + agent.Equipment[equipmentIndex].Item?.StringId
                               + "' had its meshes put back before the engine dropped it.");
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 324: restoring a dropped weapon failed; it may lie invisible.", ex);
            }
        }

        [HarmonyPostfix]
        private static void Postfix(Agent agent, ref Vec3 globalVelocity, Mission.WeaponSpawnFlags spawnFlags,
                                    WeakGameEntity __state)
        {
            try
            {
                if (agent == null || !agent.IsMainAgent || !__state.IsValid)
                    return;

                int shown = Show(__state);
                if (shown > 0)
                    VrLog.Info("TEST 324c drop: the item was masked out of the final view (a sheathed "
                               + "weapon in first person); " + shown + " mesh(es) shown again.");

                // Probe: the ground item deletes itself if its BoxCast finds no
                // ground under it, so a drop from a hand at or below the ground
                // would vanish. Only worth fixing if this shows it happening.
                Vec3 at = __state.GlobalPosition;
                float ground = Mission.Current.Scene.GetGroundHeightAtPosition(at);
                VrLog.Info("TEST 324 drop: lands from z " + F(at.z) + ", ground z " + F(ground)
                           + " (" + F(at.z - ground) + " m above); flags " + spawnFlags + ", velocity "
                           + F(globalVelocity.Length) + " m/s, body " + __state.BodyFlag + ".");

                SpawnedItemEntity item = __state.GetFirstScriptOfType<SpawnedItemEntity>();
                if (item != null)
                {
                    _tracked = item;
                    _trackedId = item.WeaponCopy.Item?.StringId;
                    _trackedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    _trackedLog = 0L;
                }
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 324: the drop probe failed.", ex);
            }
        }

        // TEST 324d: the last dropped item, followed for three seconds. Reported:
        // drops "sometimes get stuck in mid air, then disappear". The engine
        // deletes a ground item whose body has sat still for a second with no
        // ground found under it (SpawnedItemEntity.TryFindProperGroundEntity),
        // so the question is why the body is not falling.
        private static readonly bool TrackOn = VrConfig.Bool("drop_track", false);
        private static SpawnedItemEntity _tracked;
        private static string _trackedId;
        private static long _trackedAt;
        private static long _trackedLog;

        /// <summary>From the AfterMissionTick postfix: one line per 0.2 s.</summary>
        internal static void Track()
        {
            SpawnedItemEntity item = TrackOn ? _tracked : null;
            if (item == null)
                return;

            try
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                double t = (now - _trackedAt) / (double)System.Diagnostics.Stopwatch.Frequency;

                if (item.IsRemoved || t > 3.0)
                {
                    VrLog.Info("TEST 324d '" + _trackedId + "' " + t.ToString("F2", CultureInfo.InvariantCulture)
                               + " s: " + (item.IsRemoved ? "DELETED by the engine." : "followed for 3 s, done."));
                    _tracked = null;
                    return;
                }

                if (now < _trackedLog)
                    return;
                _trackedLog = now + System.Diagnostics.Stopwatch.Frequency / 5;

                WeakGameEntity e = item.GameEntity;
                Vec3 at = e.GlobalPosition;
                float ground = Mission.Current.Scene.GetGroundHeightAtPosition(at);
                BodyFlags body = e.BodyFlag;
                VrLog.Info("TEST 324d '" + _trackedId + "' " + t.ToString("F2", CultureInfo.InvariantCulture)
                           + " s: " + F(at.z - ground) + " m above ground, speed " + F(e.GetLinearVelocity().Length)
                           + ", dynamic " + body.HasAnyFlag(BodyFlags.Dynamic)
                           + ", simulating " + e.HasDynamicRigidBodyAndActiveSimulation()
                           + ", stationary " + e.IsDynamicBodyStationary()
                           + ", stopped " + VrGrab.PhysicsStopped?.GetValue(item) + ", to delete " + VrGrab.ReadyToDelete?.GetValue(item)
                           + ", parent " + (e.Parent.IsValid ? e.Parent.Name : "none") + ", body " + body + ".");
            }
            catch (Exception ex)
            {
                _tracked = null;
                VrLog.Error("TEST 324d: following the dropped item failed.", ex);
            }
        }

        /// <summary>The ground item and its children (stuck arrows): if no mesh
        /// draws in the final view, every mesh does. A partly masked item is left
        /// as the engine made it. Returns how many meshes were turned on.</summary>
        private static int Show(WeakGameEntity e)
        {
            var metas = new System.Collections.Generic.List<MetaMesh>();
            Collect(e, metas);
            int children = e.ChildCount;
            for (int c = 0; c < children && c < 16; c++)
            {
                WeakGameEntity child = e.GetChild(c);
                if (child.IsValid)
                    Collect(child, metas);
            }

            foreach (MetaMesh meta in metas)
            {
                int n = meta.MeshCount;
                for (int i = 0; i < n && i < 32; i++)
                {
                    Mesh mesh = meta.GetMeshAtIndex(i);
                    if (mesh != null && ((uint)mesh.VisibilityMask & 1u) != 0)
                        return 0;
                }
            }

            int shown = 0;
            foreach (MetaMesh meta in metas)
            {
                int n = meta.MeshCount;
                for (int i = 0; i < n && i < 32; i++)
                {
                    Mesh mesh = meta.GetMeshAtIndex(i);
                    if (mesh == null)
                        continue;
                    mesh.SetVisibilityMask((VisibilityMaskFlags)33);
                    shown++;
                }
            }

            if (shown > 0)
                e.SetVisibilityExcludeParents(true);
            return shown;
        }

        private static void Collect(WeakGameEntity e, System.Collections.Generic.List<MetaMesh> into)
        {
            int metas = e.MultiMeshComponentCount;
            for (int m = 0; m < metas && m < 8; m++)
            {
                MetaMesh meta = e.GetMetaMesh(m);
                if (meta != null)
                    into.Add(meta);
            }

            int cloths = e.ClothSimulatorComponentCount;
            for (int c = 0; c < cloths && c < 8; c++)
            {
                MetaMesh meta = e.GetClothSimulator(c)?.GetFirstMetaMesh();
                if (meta != null)
                    into.Add(meta);
            }
        }

        private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
