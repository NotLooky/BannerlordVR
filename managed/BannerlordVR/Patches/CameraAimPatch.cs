using System;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// Gives <see cref="VrEngineAim"/> the one moment where writing to the
    /// engine's camera actually sticks.
    ///
    /// UpdateCamera, and a PREFIX - which is the opposite of every other patch in
    /// this mod, and deliberately so. CameraPatch explains at length why the head
    /// POSE must not be driven from here: UpdateCamera is reached through
    /// CheckForUpdateCamera and only runs when the engine decides the camera
    /// needs recomputing, so a pose written here freezes between calls and looks
    /// exactly like broken head tracking.
    ///
    /// That objection does not apply to what this patch writes. Bearing,
    /// elevation and view angle are INPUTS the engine reads to build the camera,
    /// not outputs it recomputes over the top - so the correct place to set them
    /// is immediately before it reads them, and the correct time is whenever it
    /// actually rebuilds. On the frames it does not rebuild, the camera has not
    /// moved, and there is nothing to correct.
    ///
    /// None of this touches what the player sees. The rendered view comes from
    /// the view-projection patcher, off the live head pose. This only decides
    /// what the engine considers worth submitting to the GPU.
    /// </summary>
    [HarmonyPatch(typeof(MissionScreen), "UpdateCamera", new Type[] { typeof(float) })]
    public static class CameraAimPatch
    {
        private static void Prefix(MissionScreen __instance)
        {
            if (!VrSystem.IsActive)
                return;

            // Both of these WRITE to the camera the game is about to build - one
            // picks first person, the other injects look input. During deployment
            // the player is steering that camera themselves and neither has any
            // business touching it.
            if (!VrMissionPhase.VrOwnsCamera)
                return;

            try
            {
                // BEFORE the engine builds the camera, because this decides WHICH
                // camera it builds. The anchor rides the first-person one; see
                // VrGameCamera.
                VrGameCamera.ForceFirstPerson();

                VrEngineAim.Apply(__instance);
            }
            catch (Exception ex)
            {
                // A throw here would land inside the engine's own camera update.
                // VrEngineAim disables itself on failure; this is the backstop.
                VrLog.Error("Engine aim prefix failed.", ex);
            }
        }

        /// <summary>
        /// The head pose reaches the render from HERE, and the prefix above is why
        /// it has to.
        ///
        /// The prefix writes look INPUT, so it belongs before the engine reads it.
        /// The camera pose is an OUTPUT - the engine has just finished computing
        /// CombatCamera from that input, and anything written earlier in the frame
        /// has now been overwritten. That includes the write AFR does from
        /// OnFrameTick's postfix, which is why the headset showed a mouse-driven
        /// view while the gain meter read a perfect x1.00: the VR camera was
        /// correct all along and was simply not the one being rendered.
        ///
        /// So this substitutes what the engine just produced, which is the seam a
        /// camera-hooking VR mod normally uses. It is additive - AFR still writes
        /// from OnFrameTick, and on frames where the engine skips its camera update
        /// that earlier write is the one that stands.
        /// </summary>
        private static void Postfix(MissionScreen __instance)
        {
            if (!VrSystem.IsActive)
                return;

            if (!VrMissionPhase.VrOwnsCamera)
                return;

            try
            {
                // FIRST, and the ordering is the whole point. CombatCamera holds
                // the camera the GAME just built, and the two AimEngineCamera
                // calls below overwrite it with ours. Read after them and the
                // anchor would be reading its own output back. See VrGameCamera.
                VrGameCamera.Capture(__instance);

                // VrHeadAim.Apply() IS NOT CALLED HERE ANY MORE.
                //
                // It wrote Agent.LookDirection from this seam and the write did
                // not survive the frame. Its own probe said so, in the log, for
                // every run: "the agent's look drifted 22.6 deg from what we
                // wrote" - then 13.7, 56.2, 32.1 - against a note saying a degree
                // or two is expected and tens of degrees means something
                // recomputes LookDirection afterwards. Tens of degrees is what it
                // read, so the aim was never really following the head; it was
                // being overwritten by the mission tick a moment later.
                //
                // That is the same fault, with the same cause, that MissionTickPatch
                // was created for: this seam runs BEFORE the mission tick, and the
                // mission tick recomputes what the agent is looking at. The head
                // aim is applied from AfterMissionTick now, alongside the hands,
                // which is after the animation and after the controller.
                //
                // Both stereo strategies need the engine's camera to tell the
                // truth, and for the same reason: it decides what is submitted at
                // all. Each returns immediately when the other mode is active.
                VrAfrRenderer.AimEngineCamera(__instance);
                VrStereoRenderer.AimEngineCullingCamera(__instance);
            }
            catch (Exception ex)
            {
                VrLog.Error("Engine camera postfix failed.", ex);
            }
        }
    }

    /// <summary>
    /// Widens the engine's vertical field of view by intercepting the property
    /// itself.
    ///
    /// The engine renders 97.1 x 65.0 degrees. The headset wants 94 x 99. Sixty
    /// five degrees of vertical inside a ninety nine degree display leaves hard
    /// black above and below the image, which is what "the sky is black" has been
    /// the whole time - the photo from inside the headset shows the bands.
    ///
    /// Writing the backing field before UpdateCamera did nothing: the frustum
    /// stayed at exactly 65.0 for the entire run, so the engine recomputes the
    /// value from its zoom state after that point. The getter is the one place
    /// that cannot be bypassed. CameraViewAngle is an auto-property, so every
    /// read of it inside MissionScreen compiles to a call to this method, and a
    /// scaled return value reaches all of them regardless of when the field was
    /// last written.
    /// </summary>
    [HarmonyPatch(typeof(MissionScreen), "get_CameraViewAngle")]
    public static class CameraViewAnglePatch
    {
        private static bool Prepare()
        {
            // Harmony skips the patch entirely when this returns false, so the
            // default configuration pays nothing at all for it.
            return VrEngineAim.WidensViewAngle;
        }

        private static void Postfix(ref float __result)
        {
            if (!VrSystem.IsActive)
                return;

            __result = VrEngineAim.ScaleViewAngle(__result);
        }
    }
}
