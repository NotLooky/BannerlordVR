using System;
using HarmonyLib;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.VR.Physics
{
    // Blade & Sorcery / Tales of Glory style enemy physics (docs/physical-enemies.md).
    // Off unless enemy_physics = 1; on, the VR panel's physical combat switches
    // it with BannerlordVR's own collisions. SubModule calls these four hooks;
    // the patch classes in this folder apply with the mod's own and stand
    // aside (Prepare) while the key is off.
    public static class VrEnemyPhysics
    {
        public static void Load()
        {
            if (!EnemyPhysicsSettings.Configured) return;
            PhysicsLog.Initialize();
            PhysicsLog.Info("Enemy physics on (enemy_physics = 1); BannerlordVR " + typeof(VrSystem).Assembly.GetName().Version);
            if (EnemyPhysicsSettings.LivingRagdolls || EnemyPhysicsSettings.RuntimeProbe)
            {
                if (PhysicsInterop.LoadPhysicsNative()) NativeRagdollBridge.Initialize();
            }
        }

        // The VR panel's Enemy physics box: -1 hides it (enemy_physics = 0).
        public static int PanelState => !EnemyPhysicsSettings.Configured ? -1 : EnemyPhysicsSettings.Active ? 1 : 0;
        public static void FromPanel(int state)
        {
            if (!EnemyPhysicsSettings.Configured || state < 0 || (state != 0) == EnemyPhysicsSettings.Active) return;
            EnemyPhysicsSettings.Active = state != 0;
            VrConfig.Save("enemy_physics_active", EnemyPhysicsSettings.Active ? "1" : "0");
            PhysicsLog.Info("VR panel: enemy physics " + (EnemyPhysicsSettings.Active ? "ON" : "off (BannerlordVR's own physical combat)") + ", saved.");
            VrLog.Info("VR panel: enemy physics " + (EnemyPhysicsSettings.Active ? "ON" : "off") + ", saved.");
        }

        public static void ApplicationTick()
        {
            if (!EnemyPhysicsSettings.Configured) return;
            try { LimbHide.FrameStart(); } catch (Exception) { }
        }

        // A severed limb is folded again after the agents' tick, and checked at
        // the next frame's start (LimbHide).
        public static void AfterAsyncTick()
        {
            if (!EnemyPhysicsSettings.Configured) return;
            try { LimbHide.LateTick(); } catch (Exception) { }
            try { WoundDecal.Tick(); } catch (Exception) { }
        }

        public static void MissionBehaviorInitialize(Mission mission)
        {
            if (!EnemyPhysicsSettings.Configured) return;
            try { LimbHide.ForgetAll(); } catch (Exception) { }
            try { EnemyPhysicsRuntime.ForgetMission(); } catch (Exception ex) { PhysicsLog.Error("Forgetting the last mission failed.", ex); }
            try
            {
                if (EnemyPhysicsSettings.LivingRagdolls || EnemyPhysicsSettings.RuntimeProbe)
                    mission.AddMissionBehavior(new LivingRagdollMissionLogic());
                if (EnemyPhysicsSettings.Bleeding)
                    mission.AddMissionBehavior(new BleedingMissionLogic());
                if (EnemyPhysicsSettings.Sandbox)
                    mission.AddMissionBehavior(new PhysicsSandbox());
                if (EnemyPhysicsSettings.RuntimeProbe && EnemyPhysicsSettings.CadenceProbe)
                    mission.AddMissionBehavior(new PhysicsCadenceProbe());
            }
            catch (Exception ex) { PhysicsLog.Error("Enemy physics behaviours could not be added to this mission.", ex); }
        }
    }

    // After MissionTickPatch (same seam, lower priority): this frame's hand
    // poses are already in place.
    [HarmonyPatch(typeof(MissionScreen), "AfterMissionTick", new[] { typeof(Mission), typeof(float) })]
    internal static class PhysicsTickPatch
    {
        public static bool Prepare() => EnemyPhysicsSettings.Configured;
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        public static void Postfix(Mission __0, float __1)
        {
            try
            {
                EnemyPhysicsRuntime.Tick(__0, __1);
            }
            catch (Exception ex)
            {
                PhysicsLog.Error("Enemy physics tick failed; resetting.", ex);
                try { EnemyPhysicsRuntime.Reset(); } catch { }
            }
        }
    }

    // One damage system per blade: with the physics owning weapon damage,
    // BannerlordVR's physical strikes are skipped on the bodies it tracks.
    // Switching VrPhysicalCombat off is not enough (VrMelee's instant hits
    // still strike).
    [HarmonyPatch(typeof(VrPhysicalCombat), "Strike")]
    internal static class ReleaseStrikePatch
    {
        public static bool Prepare() => EnemyPhysicsSettings.Configured;
        private static bool _logged;
        [HarmonyPrefix]
        public static bool Prefix(Agent victim)
        {
            if (!EnemyPhysicsSettings.Enabled || !EnemyPhysicsSettings.WeaponDamage) return true;
            // Only the bodies the physics tracks are its own to damage: a
            // horse or camel, or a soldier past the six bodies simulated,
            // took no blow at all from the blade.
            if (victim != null && !EnemyPhysicsRuntime.Tracks(victim)) return true;
            if (!_logged) { _logged = true; PhysicsLog.Info("BannerlordVR physical strikes skipped: the enemy physics owns blade damage."); }
            return false;
        }
    }

    // Blows the physics did not deal never reach an enemy it is holding: a
    // blade inside the body, a grip, or a body in physics mode. Covers the
    // engine attacks VrMelee starts from swing gestures.
    [HarmonyPatch(typeof(Agent), nameof(Agent.RegisterBlow))]
    internal static class ProtectHeldVictimPatch
    {
        public static bool Prepare() => EnemyPhysicsSettings.Configured;
        [HarmonyPrefix]
        public static bool Prefix(Agent __instance, Blow blow)
        {
            if (PhysicalHit.DeliveringOwnBlow || !EnemyPhysicsSettings.Enabled) return true;
            Agent player = Mission.Current?.MainAgent;
            if (player == null || blow.OwnerId != player.Index || !EnemyPhysicsRuntime.Protects(__instance)) return true;
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info("Foreign blow refused on held enemy " + __instance.Index + " (" + blow.InflictedDamage + " damage).");
            return false;
        }
    }

    // While a VR hand closes a fist or draws a blade, its trigger belongs to
    // the physics, not to the game's attack key.
    [HarmonyPatch(typeof(VrHolster), "TriggerKeyAllowed")]
    internal static class TriggerOwnershipPatch
    {
        public static bool Prepare() => EnemyPhysicsSettings.Configured;
        [HarmonyPostfix]
        public static void Postfix(bool left, ref bool __result)
        {
            if (__result && EnemyPhysicsSettings.Enabled && EnemyPhysicsRuntime.OwnsTrigger(left)) __result = false;
        }
    }
}
