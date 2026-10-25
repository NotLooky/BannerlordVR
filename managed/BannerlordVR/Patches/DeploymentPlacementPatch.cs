using BannerlordVR.VR;
using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews.Order;
using TaleWorlds.MountAndBlade.ViewModelCollection.OrderOfBattle;

namespace BannerlordVR.Patches
{
    [HarmonyPatch]
    internal static class DeploymentPlacementPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(OrderTroopPlacer),
            "TryGetScreenMiddleToWorldPosition", new[] { typeof(WorldPosition).MakeByRefType(),
                typeof(float).MakeByRefType(), typeof(WeakGameEntity).MakeByRefType() });

        internal static bool Prefix(ref WorldPosition worldPosition, ref float collisionDistance,
                                    ref WeakGameEntity collidedEntity, ref bool __result)
        {
            if (!VrMissionPhase.Deploying || (!VrLaser.GroundActive && !VrLaser.Active))
                return true;

            worldPosition = WorldPosition.Invalid;
            collisionDistance = 0f;
            collidedEntity = WeakGameEntity.Invalid;
            if (VrLaser.GroundActive && VrLaser.TryDeploymentGround(out Vec3 hit,
                    out collisionDistance, out collidedEntity))
            {
                worldPosition = new WorldPosition(Mission.Current.Scene, System.UIntPtr.Zero, hit, false);
                __result = true;
            }
            else
                __result = false;
            return false;
        }
    }

    /// <summary>War Sails overrides the same method with its own projection onto
    /// the water (the mouse, or the screen centre for a pad), so the base patch
    /// never runs there and ships followed the head. Same laser point here; the
    /// naval placer grounds it on the sea itself. Installed from the main menu,
    /// like HelmPatch: War Sails' assemblies are only loaded by then.</summary>
    internal static class NavalDeploymentPlacementPatch
    {
        internal static void Install(Harmony harmony)
        {
            try
            {
                MethodBase target = AccessTools.TypeByName("NavalDLC.View.MissionViews.Order.NavalOrderTroopPlacer") is Type t
                    ? AccessTools.DeclaredMethod(t, "TryGetScreenMiddleToWorldPosition", new[] {
                          typeof(WorldPosition).MakeByRefType(), typeof(float).MakeByRefType(),
                          typeof(WeakGameEntity).MakeByRefType() })
                    : null;
                if (target == null)
                {
                    VrLog.Info("War Sails placement: no NavalOrderTroopPlacer, nothing to patch.");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(NavalDeploymentPlacementPatch), nameof(Prefix)));
                VrLog.Info("War Sails placement: ships and formations at sea go where the laser points (TEST 580).");
            }
            catch (Exception ex)
            {
                VrLog.Error("War Sails placement patch failed; ships follow the head.", ex);
            }
        }

        private static bool Prefix(ref WorldPosition worldPosition, ref float collisionDistance,
                                   ref WeakGameEntity collidedEntity, ref bool __result) =>
            DeploymentPlacementPatch.Prefix(ref worldPosition, ref collisionDistance,
                                            ref collidedEntity, ref __result);
    }

    [HarmonyPatch(typeof(OrderOfBattleFormationItemVM), nameof(OrderOfBattleFormationItemVM.Tick))]
    internal static class DeploymentMarkerPatch
    {
        private static void Prefix(OrderOfBattleFormationItemVM __instance)
        {
            // Recheck the actual agents before projecting the marker each tick.
            if (VrMissionPhase.Deploying)
                __instance.MakeMarkerWorldPositionDirty();
        }
    }

    [HarmonyPatch(typeof(OrderFlag), "GetFlagPosition")]
    internal static class DeploymentFlagPatch
    {
        private static bool Prefix(OrderFlag __instance, ref bool isOnValidGround, bool checkForTargetEntity,
                                   ref Vec3 __result)
        {
            if (!VrMissionPhase.Deploying)
                return true;

            isOnValidGround = false;
            __result = Vec3.Invalid;
            if (VrLaser.GroundActive && VrLaser.TryDeploymentGround(out Vec3 hit, out _, out _))
            {
                var position = new WorldPosition(Mission.Current.Scene, System.UIntPtr.Zero, hit, false);
                isOnValidGround = checkForTargetEntity
                    ? Mission.Current.IsOrderPositionAvailable(position, Mission.Current.PlayerTeam)
                    : __instance.IsPositionOnValidGround(position);
                __result = hit;
            }
            return false;
        }
    }
}
