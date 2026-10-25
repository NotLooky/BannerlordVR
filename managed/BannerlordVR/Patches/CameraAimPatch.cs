using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// Gives <see cref="VrEngineAim"/> the one moment where writing to the
    /// engine's camera actually sticks.
    ///
    /// UpdateCamera, and a PREFIX - which is the opposite of every other patch in
    /// this mod, and deliberately so. Bearing, elevation and view angle are INPUTS
    /// the engine reads to build the camera, not outputs it recomputes over the
    /// top - so the correct place to set them is immediately before it reads them.
    ///
    /// (CameraPatch's old worry that UpdateCamera only runs now and then is
    /// stale: CheckForUpdateCamera has no input condition, and the log shows one
    /// call per frame - "re-aimed from UpdateCamera's tail: 150" every ~2 s.)
    ///
    /// The postfix is where the head pose lands on the engine's camera, and since
    /// TEST 397 where the culling frustum is fitted to it. See
    /// VrAfrRenderer.CullRefit.
    /// </summary>
    [HarmonyPatch(typeof(MissionScreen), "UpdateCamera", new Type[] { typeof(float) })]
    public static class CameraAimPatch
    {
        private static void Prefix(MissionScreen __instance)
        {
            if (!VrSystem.IsActive)
                return;

            // Both of these WRITE to the camera the game is about to build - one
            // picks first person, the other injects look input. During deployment,
            // and in the free camera after the player dies (TEST 397), the player
            // is steering that camera themselves and neither has any business
            // touching it.
            //
            // VrDrivesLook, NOT VrOwnsCamera. The stereo path keeps rendering
            // through deployment - the headset needs a world - and only the two
            // writes below stand down. See VrMissionPhase.VrDrivesLook.
            if (!VrMissionPhase.VrDrivesLook)
            {
                // ...but standing down is not enough by itself, because the free
                // camera INTEGRATES FROM THE FIELD WE OVERWRITE.
                //
                // MissionScreen.UpdateCamera's free-camera branch opens with
                //
                //     val.origin = CombatCamera.Frame.origin;
                //
                // and then adds this frame's stick movement to it. That field is
                // the one the postfix below fills with our eye pose, so the head
                // offset between the anchor and the eye is handed back to the
                // engine as the camera's new STARTING POINT, once per frame. The
                // camera therefore advances by that offset every frame on top of
                // whatever the player asked for: a few centimetres at 90 fps is
                // metres per second of camera that departs on its own. That is
                // the drift-then-fly this phase has always been reported to have,
                // and it is why deployment was given up on and handed back to a
                // flat screen instead.
                //
                // Putting the engine's own last camera back before it reads it
                // closes the loop. It is the frame VrGameCamera captured at the
                // top of the postfix, which is the engine's answer before any VR
                // write reached it - so the engine integrates from where IT left
                // the camera, and our eye pose goes back on top afterwards purely
                // for the render.
                RestoreEngineCamera(__instance);
                return;
            }

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

        private static bool _loggedRestore;

        /// <summary>
        /// Hands the engine back its own camera before it integrates the next one.
        ///
        /// Only while the game owns the look - deployment - because that is the
        /// only phase whose camera is built by adding to the previous value of the
        /// very field we write. Every other camera the engine builds is computed
        /// from the agent and the bearing outright, so there is nothing there for
        /// our write to accumulate into and nothing to put back.
        ///
        /// A missing capture means the postfix has not run yet - the first frame
        /// of a mission - and the engine's field is still its own, so leaving it
        /// alone is correct rather than merely safe.
        /// </summary>
        private static void RestoreEngineCamera(MissionScreen screen)
        {
            try
            {
                // NOTHING WRITTEN, NOTHING TO PUT BACK.
                //
                // This undoes our own write, so it is only correct while we are
                // making one. With vanilla_deployment = 1 the postfix stands down
                // on VrOwnsCamera and never captures, which would leave the last
                // captured frame stale - and writing a stale frame back every
                // frame would pin the camera rather than free it, turning the
                // escape hatch into a worse version of the bug it escapes.
                if (!VrMissionPhase.VrOwnsCamera)
                    return;

                if (screen == null || screen.CombatCamera == null)
                    return;

                if (!VrGameCamera.TryGetCameraFrame(out TaleWorlds.Library.MatrixFrame engine))
                    return;

                screen.CombatCamera.Frame = engine;

                if (!_loggedRestore)
                {
                    _loggedRestore = true;
                    VrLog.Info("Deployment: the engine's own camera is handed back before "
                               + "it builds the next one, so the free camera integrates from "
                               + "where the game left it rather than from the eye pose we "
                               + "wrote for the render. Without this the head offset is "
                               + "added to the camera once per frame and it flies off by "
                               + "itself.");
                }
            }
            catch (Exception ex)
            {
                // The engine is about to build a camera either way. A failure here
                // costs deployment its stable camera, not the mission.
                VrLog.Error("Could not hand the engine camera back before deployment's "
                            + "camera update; the free camera may drift.", ex);
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
                //
                // DEPLOYMENT AND THE DEAD CAMERA CULL FROM THE HEAD because
                // AimEngineCamera sets the projection while the camera is on the
                // eye (TEST 397). VrDeploymentAim, which re-aimed the camera here
                // with no projection after it, rewrote an identical pose and was
                // deleted with that change.
                // TEST 446: the agents have moved and the render has not started -
                // the view goes where the tick really put the body, before it is aimed.
                VrCameraDriver.FinishView();

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

    /// <summary>
    /// The free camera after death flies the way the deployment camera does
    /// (TEST 397), which is what the user asked for: "just copy how camera flies".
    ///
    /// Both are the SAME branch of UpdateCamera; it switches on Mission.Mode ==
    /// Deployment for planar movement along the camera's yaw, speed scaled by
    /// height, scroll height, the +100 m ground probe and the pad's vertical keys.
    /// After death the mode is Battle, so the same camera flew in full 3D at a
    /// flat 10 m/s. Every "Mode == Deployment" read in UpdateCamera goes through
    /// <see cref="Mode"/> instead, which says Deployment while
    /// VrMissionPhase.FlyLikeDeployment holds - except the deployment-boundary
    /// clamp, which would pin the dead camera inside the starting zone.
    ///
    /// Only the reads compared against 6 are touched; UpdateCamera reads the mode
    /// 30 times, and the others (conversation, stealth) must keep seeing Battle.
    /// Anything unexpected in the IL leaves the method untouched.
    /// </summary>
    [HarmonyPatch(typeof(MissionScreen), "UpdateCamera", new Type[] { typeof(float) })]
    public static class FreeCameraFlightPatch
    {
        private static readonly bool Enabled =
            VrConfig.Bool("death_cam_like_deployment", true);

        private static bool _logged;

        private static bool Prepare() => Enabled;

        public static MissionMode Mode(Mission mission)
        {
            MissionMode mode = mission.Mode;
            if (mode != MissionMode.Battle || !VrSystem.IsActive || !VrMissionPhase.FlyLikeDeployment)
                return mode;

            if (!_logged)
            {
                _logged = true;
                VrLog.Info("Free camera with no main agent: flying it the deployment way - planar, "
                           + "faster with height, the stick without the trigger "
                           + "(death_cam_like_deployment = 0 restores the game's).");
            }
            return MissionMode.Deployment;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var getMode = AccessTools.PropertyGetter(typeof(Mission), nameof(Mission.Mode));
            var isMultiplayer = AccessTools.PropertyGetter(typeof(GameNetwork), nameof(GameNetwork.IsMultiplayer));
            var replacement = AccessTools.Method(typeof(FreeCameraFlightPatch), nameof(Mode));

            var sites = new List<int>();
            int boundary = 0;
            for (int i = 0; i + 1 < codes.Count; i++)
            {
                if (!codes[i].Calls(getMode) || codes[i + 1].opcode != OpCodes.Ldc_I4_6)
                    continue;

                // get_IsMultiplayer, brtrue, ldarg.0, get_Mission, get_Mode: the
                // deployment-boundary clamp.
                if (i >= 4 && codes[i - 4].Calls(isMultiplayer))
                    boundary++;
                else
                    sites.Add(i);
            }

            // Seven reads plus the boundary one, as of the game build this was
            // written against. A different count means a different method.
            if (sites.Count != 7 || boundary != 1)
            {
                VrLog.Warn($"Free camera flight: UpdateCamera has {sites.Count} deployment check(s) "
                           + $"and {boundary} boundary check(s), expected 7 and 1. Left untouched; the "
                           + "camera after death flies the game's way.");
                return codes;
            }

            foreach (int i in sites)
            {
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = replacement;
            }
            return codes;
        }
    }
}
