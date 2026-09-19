using System;
using HarmonyLib;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;
using BannerlordVR.VR;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// Poses the hands and the weapons, at the latest per-frame seam there is.
    ///
    /// WHY NOT OnFrameTick, WHERE IT WAS
    ///
    /// The write worked and the weapon still did not move, and the log finally
    /// separated those two facts:
    ///
    ///     Weapon entity write CONFIRMED: read straight back 0.000 m from where
    ///     it was put, so the frame takes.
    ///
    /// SetGlobalFrame is not being refused. The frame is set, reads back exactly,
    /// and is then thrown away before anything is drawn - because OnFrameTick runs
    /// BEFORE the mission tick, the mission tick animates the skeleton, and the
    /// engine resolves every attached entity's transform from the bone afterwards.
    /// Our write is simply the earliest of three, and the last one wins.
    ///
    /// The same is true of the bone write, for the same reason, which is why
    /// neither approach ever moved anything: both were landing upstream of the
    /// animation that overwrote them.
    ///
    /// WHY THIS ONE
    ///
    /// MissionScreen's handler interface exposes the frame in order:
    ///
    ///     BeforeMissionTick  ->  [mission tick: agents animate]  ->  AfterMissionTick
    ///
    /// AfterMissionTick is by construction after the animation and after the
    /// attachment resolve, and unlike OnSceneRenderingStarted - which was the
    /// previous attempt and turned out to fire once per SCENE - it is per frame.
    /// That is the whole of the requirement: after the animation, every frame.
    ///
    /// It is patched rather than the interface method because the explicit
    /// implementation forwards to this protected virtual one, and a name is a
    /// steadier patch target than an explicitly implemented interface slot.
    /// </summary>
    [HarmonyPatch(typeof(MissionScreen), "AfterMissionTick",
                  new Type[] { typeof(Mission), typeof(float) })]
    public static class MissionTickPatch
    {
        private static bool _brokenLogged;
        private static bool _loggedFirst;

        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                if (!VrSystem.IsActive)
                    return;

                VrBreadcrumb.Set(VrBreadcrumb.MissionTick);

                if (!_loggedFirst)
                {
                    _loggedFirst = true;
                    VrLog.Info("Hands and weapons are now posed from AfterMissionTick - after "
                               + "the animation, every frame. The two earlier seams were both "
                               + "upstream of the animation that overwrote them: OnFrameTick "
                               + "runs before the mission tick, and OnSceneRenderingStarted "
                               + "fires once per scene rather than per frame.");
                }

                // BEFORE the hands, and after the mission tick, which is the
                // point of moving it here.
                //
                // The aim used to be written at the tail of UpdateCamera, which
                // runs before the mission tick - so the controller recomputed
                // LookDirection immediately afterwards and the write was thrown
                // away. The probe measured 13 to 56 degrees of drift per frame
                // against an expected one or two, and the symptom was the body
                // and weapon refusing to follow the head however far it turned.
                //
                // Exactly the fault this patch already exists to fix for the
                // weapon: a correct write landing upstream of the thing that
                // overwrites it.
                if (VrMissionPhase.VrOwnsCamera)
                    VrHeadAim.Apply();

                // The hands and the weapon are the experimental half. Off by
                // default; the gamepad emulation is a separate path and is not
                // affected by this. See VrMotionHands.
                if (VrMotionHands.ShouldPose)
                    VrWeaponHands.Tick();
            }
            catch (Exception ex)
            {
                // Never take the mission down. Report once, then stay quiet.
                if (!_brokenLogged)
                {
                    _brokenLogged = true;
                    VrLog.Error("AfterMissionTick postfix failed; hands and weapons go back to "
                                + "the game's own animation.", ex);
                }
            }
        }
    }
}
