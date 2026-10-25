using System;
using System.Linq;
using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews.SiegeWeapon;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// TEST 512: the two engine behaviours that fight a ranged machine aimed by
    /// hand (VrSiege), off only while the player's hand holds it.
    ///
    /// RangedSiegeWeaponView.HandleUserAiming gives the stick's input every tick it
    /// is moved, and GiveInput cancels the hands' GiveExactInput - skipped.
    ///
    /// Horizontal/VerticalAimSensitivity is the turn rate the target is approached
    /// at; a lone mangonel crew turns at 0.05 rad/s. Raised to siege_aim_rad_s on
    /// every declared override across the loaded RangedSiegeWeapon types (vanilla
    /// and War Sails' ship ballista and mangonel). Laid down at the main menu, as
    /// HelmPatch is, so War Sails' assemblies are loaded by then.
    ///
    /// TEST 512b: StartUsingWeaponCamera is skipped in VR (siege_vr_view), so the
    /// pilot stays in first person instead of a flat camera behind the machine.
    /// </summary>
    internal static class SiegeAimPatch
    {
        private static bool _installed;

        internal static void Install(Harmony harmony)
        {
            if (_installed || harmony == null)
                return;

            _installed = true;
            try
            {
                MethodInfo aiming = AccessTools.Method(typeof(RangedSiegeWeaponView), "HandleUserAiming",
                                                       new[] { typeof(float) });
                if (aiming != null)
                    harmony.Patch(aiming, prefix: new HarmonyMethod(typeof(SiegeAimPatch), nameof(AimingPrefix)));
                else
                    VrLog.Warn("TEST 512: RangedSiegeWeaponView.HandleUserAiming is not where it was - the stick "
                               + "can still cancel the hands' aim.");

                // TEST 512b: the base and every override (BallistaView's also zooms the
                // camera out), so no siege view ever swaps in its flat camera in VR.
                var camera = new HarmonyMethod(typeof(SiegeAimPatch), nameof(CameraPrefix));
                int views = 0;
                foreach (Type t in AccessTools.AllTypes().Where(t => typeof(RangedSiegeWeaponView).IsAssignableFrom(t)))
                {
                    MethodInfo start = AccessTools.DeclaredMethod(t, "StartUsingWeaponCamera", Type.EmptyTypes);
                    if (start == null || start.IsAbstract)
                        continue;

                    harmony.Patch(start, prefix: camera);
                    views++;
                }
                VrLog.Info("TEST 512b: " + views + " siege weapon camera(s) "
                           + (VrSiege.VrView ? "kept off in VR - machines are aimed from first person."
                                              : "left on (siege_vr_view = 0)."));

                // TEST 512d: after the engine turns a machine (Ballista.ApplyAimChange
                // moves its pilot's standing point round with it), the pinned pilot's
                // point goes back - every declared override of both turn methods.
                var hold = new HarmonyMethod(typeof(SiegeAimPatch), nameof(TurnPostfix));
                int turns = 0;
                foreach (Type t in AccessTools.AllTypes().Where(t => typeof(RangedSiegeWeapon).IsAssignableFrom(t)))
                {
                    foreach (string name in new[] { "ApplyAimChange", "ApplyCurrentDirectionToEntity" })
                    {
                        MethodInfo turn = AccessTools.DeclaredMethod(t, name, Type.EmptyTypes);
                        if (turn == null || turn.IsAbstract)
                            continue;

                        harmony.Patch(turn, postfix: hold);
                        turns++;
                    }
                }
                VrLog.Info("TEST 512d: " + turns + " machine turn method(s) keep a VR pilot where it stands.");

                // TEST 512e: the right stick turns you at a machine. MissionScreen
                // clamps the camera's bearing to the usage animation's neck limit, and
                // the VR view's turn is integrated from that clamped delta.
                try
                {
                    MethodInfo bearing = AccessTools.Method(typeof(MissionScreen), "CalculateNewBearingAndElevationForFirstPerson");
                    if (bearing == null)
                        throw new MissingMethodException("MissionScreen.CalculateNewBearingAndElevationForFirstPerson");
                    harmony.Patch(bearing, postfix: new HarmonyMethod(typeof(SiegeAimPatch), nameof(FreeTurnPostfix)));
                    VrLog.Info("TEST 512e: at a machine the right stick turns you past the usage animation's limit.");
                }
                catch (Exception ex)
                {
                    VrLog.Error("TEST 512e: the right stick stays limited at a machine.", ex);
                }

                // TEST 517: a ballista reloaded by hand winds only as the hand cranks -
                // its set-up animation's speed (FinalReloadSpeed x this) is held at 0.
                var reload = new HarmonyMethod(typeof(SiegeAimPatch), nameof(ReloadPostfix));
                int reloads = 0;
                foreach (Type t in AccessTools.AllTypes().Where(t => typeof(RangedSiegeWeapon).IsAssignableFrom(t)))
                {
                    MethodInfo getter = AccessTools.DeclaredPropertyGetter(t, "ReloadSpeedMultiplier");
                    if (getter == null || getter.IsAbstract)
                        continue;
                    harmony.Patch(getter, postfix: reload);
                    reloads++;
                }
                VrLog.Info("TEST 517: " + reloads + " reload-speed getter(s) wait for the hand on a ballista.");

                var postfix = new HarmonyMethod(typeof(SiegeAimPatch), nameof(SensitivityPostfix));
                int patched = 0;
                foreach (Type t in AccessTools.AllTypes().Where(t => typeof(RangedSiegeWeapon).IsAssignableFrom(t)))
                {
                    foreach (string name in new[] { "HorizontalAimSensitivity", "VerticalAimSensitivity" })
                    {
                        MethodInfo getter = AccessTools.DeclaredPropertyGetter(t, name);
                        if (getter == null || getter.IsAbstract)
                            continue;

                        harmony.Patch(getter, postfix: postfix);
                        patched++;
                    }
                }

                VrLog.Info("TEST 512: siege machines can be held, and ranged ones aimed by hand (" + patched
                           + " aim-rate getter(s) raised to " + VrSiege.AimRadS.ToString("F2") + " rad/s while held).");
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 512: the siege aim patch failed; a held machine aims at vanilla's rate and the "
                            + "stick can cancel it.", ex);
            }
        }

        private static bool AimingPrefix() => !VrSiege.Aiming;

        private static void TurnPostfix(object __instance) => VrSiege.HoldPilot(__instance);

        private static void ReloadPostfix(object __instance, ref float __result)
        {
            if (VrBallista.HoldsReload(__instance))
                __result = 0f;
        }

        private static void FreeTurnPostfix(Agent agentToFollow, float oldCameraBearing, float cameraBearingDelta,
                                            ref float newBearing)
        {
            if (VrSiege.FreeTurn(agentToFollow))
                newBearing = MBMath.WrapAngle(oldCameraBearing + cameraBearingDelta);
        }

        /// <summary>TEST 512b: with CustomCamera set, MissionScreen copies the weapon
        /// camera into CombatCamera and skips UpdateCamera, where the VR view is
        /// built - each eye showed a flat picture from behind the ballista.</summary>
        private static bool CameraPrefix() => !(VrSiege.VrView && VrSystem.IsActive);

        private static void SensitivityPostfix(object __instance, ref float __result)
        {
            if (__result < VrSiege.AimRadS && VrSiege.Aiming && ReferenceEquals(__instance, VrSiege.Weapon))
                __result = VrSiege.AimRadS;
        }
    }
}
