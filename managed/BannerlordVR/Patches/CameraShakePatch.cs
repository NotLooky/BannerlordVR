using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// Stops the game shaking the camera.
    ///
    /// WHY IT MATTERS MORE IN VR THAN ON A MONITOR
    ///
    /// Camera shake is a flat-screen effect that assumes the camera is a picture
    /// frame the player is looking at. In a headset it is not a picture frame -
    /// it is the player's head. Shaking it moves the whole world relative to a
    /// head the inner ear knows perfectly well did NOT move, which is the exact
    /// vestibular mismatch that makes people ill. It is also indistinguishable,
    /// while it lasts, from the tracking being bad.
    ///
    /// It reaches the view through the anchor. MissionScreen writes the shake
    /// into CombatCamera, VrCameraDriver reads the result as the body anchor, and
    /// the head pose is composed on top - so every bit of it lands in the
    /// headset, on top of real head motion, at whatever frequency the effect
    /// chose.
    ///
    /// WHERE IT IS CUT
    ///
    /// At the source, not at the symptom. MissionOnCameraShakeTriggered is the
    /// one place _cameraShakeIntensity is armed; skip it and the three shake
    /// fields stay at zero, so the code that applies them in UpdateCamera runs
    /// its own no-op path rather than being second-guessed from outside. Zeroing
    /// the intensity in a postfix somewhere would fight the engine every frame
    /// for the same result.
    ///
    /// The signature is (ref Vec3 position, float radius) and it is private, so
    /// TargetMethod resolves it by name and Prepare disables the patch cleanly if
    /// a game update moves it - a missing shake cut is a comfort regression, not
    /// a reason to take the mod down.
    /// </summary>
    [HarmonyPatch]
    public static class CameraShakePatch
    {
        /// <summary>Whether the game may shake the camera. Default 0: in a
        /// headset the effect is a vestibular mismatch rather than drama.</summary>
        private static readonly bool ShakeAllowed = VrConfig.Bool("camera_shake", false);

        private static bool Prepare()
        {
            if (ShakeAllowed)
            {
                VrLog.Info("Camera shake is left as the game intends it "
                           + "(camera_shake = 1).");
                return false;
            }

            if (TargetMethod() == null)
            {
                VrLog.Warn("Camera shake: MissionScreen.MissionOnCameraShakeTriggered is "
                           + "not where it was, so shake still reaches the headset. "
                           + "Nothing else is affected.");
                return false;
            }

            return true;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(MissionScreen), "MissionOnCameraShakeTriggered",
                                      new[] { typeof(Vec3).MakeByRefType(), typeof(float) });
        }

        private static bool _logged;

        /// <summary>Returning false skips the original, so the shake is never
        /// armed in the first place.</summary>
        private static bool Prefix()
        {
            if (!_logged)
            {
                _logged = true;
                VrLog.Info("Camera shake suppressed; explosions and impacts no longer "
                           + "move the headset. Set camera_shake = 1 to get it back.");
            }

            return false;
        }
    }
}
