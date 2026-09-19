using System;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// Builds the stereo eye views at the engine's own scene-rendering entry
    /// point rather than from the per-frame tick.
    ///
    /// This exists because of a measured result, not a guess. Isolation runs
    /// established, in order:
    ///
    ///   no stereo                          stable
    ///   eye views BUILT but not enabled    stable, 55 s at 90 fps
    ///   ONE eye view enabled               crash in about a second
    ///   two enabled, copy disabled         crash
    ///   two enabled, rendered after        crash
    ///
    /// So constructing a SceneView is harmless and enabling one is fatal - and
    /// the only suspicious thing about our enable was where it happened:
    /// MissionScreen.OnFrameTick, in the middle of a frame, while rgl is very
    /// likely iterating its own list of views. OnSceneRenderingStarted is the
    /// point the engine itself uses to bring scene rendering up.
    /// </summary>
    [HarmonyPatch(typeof(MissionScreen), "OnSceneRenderingStarted")]
    public static class SceneRenderingPatch
    {
        private static void Postfix(MissionScreen __instance)
        {
            if (!VrSystem.IsActive)
                return;

            VrBreadcrumb.Set(VrBreadcrumb.SceneRender);

            try
            {
                VrLog.Info("MissionScreen.OnSceneRenderingStarted.");

                // The one moment a SceneView may safely be created or destroyed,
                // and therefore the only moment the stereo strategy may change.
                // Applied BEFORE either renderer looks at it, so both agree about
                // which mode this scene is being set up for.
                VrRenderMode.ApplyAtSceneBoundary();

                VrStereoRenderer.OnSceneRenderingStarted(__instance);
                VrAfrRenderer.OnSceneRenderingStarted(__instance);

                // THE HANDS ARE NOT POSED HERE, AND THE LOG IS WHY.
                //
                // They were, for one round. It looked like the right place - after
                // the animation, just before the frame is drawn - and it is not a
                // per-frame hook at all. One scene, one call:
                //
                //   grep -c OnSceneRenderingStarted   ->  1
                //   grep -c "re-aimed from UpdateCamera" -> 134   (150 frames each)
                //
                // So the hands were posed exactly once, at scene setup, and never
                // again. The visible result was a weapon that did not track and an
                // automatic calibration that reported "saw no frames" - because
                // the thing that feeds it had run once, twenty minutes earlier.
                //
                // OnSceneRenderingStarted is a scene-setup callback. Per-frame work
                // belongs in the UpdateCamera postfix, which is where it is.
            }
            catch (Exception ex)
            {
                // Never let this take the mission down with it.
                VrLog.Error("OnSceneRenderingStarted postfix failed.", ex);
            }
        }
    }
}
