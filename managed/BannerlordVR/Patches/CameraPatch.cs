using System;
using System.Reflection;
using BannerlordVR.Interop;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// Hands the mission camera to <see cref="VrCameraDriver"/>.
    ///
    /// Hooked on OnFrameTick, NOT on UpdateCamera. UpdateCamera looks like the
    /// obvious target and is the wrong one: MissionScreen reaches it through
    /// CheckForUpdateCamera(float), which only calls it when the engine decides
    /// the camera needs recomputing. A postfix there fires occasionally, sets the
    /// VR camera once, and leaves it frozen - which presents as "head tracking
    /// does not work" rather than as anything that looks like a rate problem.
    /// OnFrameTick runs every frame, unconditionally.
    ///
    /// Still a postfix: the engine's own camera work - collision-aware placement
    /// so the view does not clip through walls, target tracking, shake - lands in
    /// CombatCamera, which we read as the body anchor. We let it finish, then
    /// override only what VR owns: the head pose and the projection.
    /// </summary>
    [HarmonyPatch(typeof(MissionScreen), "OnFrameTick", new Type[] { typeof(float) })]
    public static class CameraPatch
    {
        private static readonly FieldInfo AllowInputField =
            AccessTools.Field(typeof(MissionScreen), "AllowInputWithCustomCamera");

        /// <summary>
        /// Whether to take over the monitor's camera as well. Off by default; see
        /// the note at the early return in Postfix. Set mirror_camera = 1 in the
        /// config to get the Phase 3 behaviour back for debugging.
        /// </summary>
        private static readonly bool MirrorEnabled = VrConfig.Bool("mirror_camera", false);

        private static bool _loggedOnce;
        private static bool _cameraInstalled;

        private static void LogFirstDrive()
        {
            if (_loggedOnce)
                return;

            _loggedOnce = true;
            VrLog.Info(MirrorEnabled
                ? "CameraPatch is driving the mission camera from the HMD (monitor hijacked)."
                : "CameraPatch is driving the eye cameras; the monitor keeps the game's own view.");
        }

        // Proves the hook rate from the log rather than by eye. A hook that fires
        // a handful of times per second and one that fires per frame produce the
        // same symptom in the headset and need completely different fixes.
        // One second, because the crash lands within about three of the stereo
        // views going live. A three-second cadence can miss the whole event.
        private const double ReportSeconds = 1.0;
        private const int MaxReports = 40;

        private static int _calls;
        private static DateTime _firstCall;
        private static DateTime _lastReport;
        private static int _callsAtLastReport;
        private static int _reports;

        private static void Postfix(MissionScreen __instance)
        {
            if (!VrSystem.IsActive)
                return;

            VrBreadcrumb.Set(VrBreadcrumb.CameraPatch);

            if (_calls == 0)
                _firstCall = _lastReport = DateTime.UtcNow;
            _calls++;

            // Before the early returns below, so the rate is reported even when
            // the pose is being rejected every frame.
            ReportProgress();

            // THE FRAME IS BEGUN HERE, NOT IN PRESENT.
            //
            // xrWaitFrame, xrBeginFrame and xrLocateViews run inside this call,
            // so the pose VrCameraDriver reads a few lines below was located for
            // the display time of the frame the engine is ABOUT to draw. Doing it
            // in the Present hook instead - which is where it used to live -
            // located it after the drawing, and the engine then rendered the
            // NEXT frame with it, one display period behind for its whole life.
            //
            // It blocks. That is what paces the game to the headset instead of
            // to whatever the desktop swapchain feels like, and it is what every
            // working VR injection does. See docs/reference-vr-mods.md.
            //
            // Not checked: SessionNotReady is the ordinary answer whenever there
            // is no session or a frame is already in flight, and the Present
            // hook falls back to doing the whole sequence itself.
            NativeMethods.bvr_prepare_frame();

            try
            {
                if (!VrCameraDriver.Update(__instance))
                {
                    // No pose this frame - headset idle, session not visible, or
                    // between missions. Give the camera back rather than freezing
                    // the player at the last VR pose.
                    ReleaseCamera(__instance);
                    return;
                }

                // Phase 4 does not need the game's view at all.
                //
                // In Phase 3 this was the only way to see anything: the engine's
                // own view was the sole renderer, so we had to hijack it. Now the
                // eye SceneViews draw the headset image themselves, and taking
                // over the monitor buys nothing while costing a great deal:
                //
                //   - the engine stops maintaining CombatCamera once CustomCamera
                //     is set, so its HorizontalFov sits at 0.000 forever and any
                //     camera we hand it renders through a broken frustum - no
                //     terrain, no agents;
                //   - CustomCamera is folded back into CombatCamera, which is the
                //     feedback loop the anchor has to defend against.
                //
                // Left off, the monitor simply keeps the game's normal flat view -
                // which is what the plan wanted as the desktop mirror anyway.
                VrStereoRenderer.EnsureCreated();
                LogFirstDrive();

                // The hands are NOT posed here any more.
                //
                // OnFrameTick runs BEFORE the mission tick, so the animation - and
                // the attached-entity resolve that follows it - overwrite anything
                // written at this point. That is what the log finally showed: the
                // weapon write took (0.000 m on read-back) and the weapon still did
                // not move, which is the signature of a correct write landing
                // upstream of something that redoes the work.
                //
                // MissionTickPatch does it from AfterMissionTick instead.

                // The tableau probe. ABOVE the AFR branch, and that placement is
                // the whole reason it works: everything below the return in that
                // branch runs in native stereo ONLY, and the probe's entire
                // purpose is to ask its question while a working mode is driving
                // the headset. Put below, it was called exactly never - the mode
                // it needs to coexist with is the one that returns first.
                //
                // It is not a stereo mode and touches no display path, so running
                // it before the branch costs the modes below nothing: it returns
                // on its first line unless tableau_stereo = 1.
                VrTableauStereo.Tick(__instance);

                // AFR drives the engine's own camera itself - it is the only
                // renderer in that mode - so it takes over here and the mirror
                // path below never runs.
                if (VrAfrRenderer.Enabled)
                {
                    VrAfrRenderer.Tick(__instance);
                    return;
                }

                if (!MirrorEnabled)
                    return;

                if (!_cameraInstalled)
                {
                    // Without this the game treats a custom camera as a cutscene
                    // and stops accepting mouse/keyboard/controller input.
                    AllowInputField?.SetValue(__instance, true);
                    _cameraInstalled = true;
                }

                // BOTH of these are required, and it took a wrong turn to learn why.
                //
                // CustomCamera is what makes MissionScreen push our camera into the
                // render path at the point in the frame where it actually sticks.
                // Setting only SceneLayer.SetCamera looks equivalent and is not: the
                // engine overrides it again later in the frame, so the view freezes
                // completely and only unfreezes when ESC pauses the engine's own
                // camera updates. The gain meter reads a perfect x1.00 throughout,
                // because our camera was right the whole time and simply was not the
                // one being rendered.
                // The MIRROR camera, never an eye camera. The engine reconfigures
                // whatever it is given here for the game window, and doing that to
                // an eye camera destroys its VR frustum every frame.
                Camera mirror = VrCameraDriver.MirrorCam;
                if (mirror == null)
                    return;

                __instance.CustomCamera = mirror;
                __instance.SceneLayer?.SetCamera(mirror);

                LogFirstDrive();

                // Phase 4. Allocates once, here rather than at load, because the
                // native CreateTexture2D hook cannot be installed until the engine
                // device has been captured by the first Present.
                VrStereoRenderer.EnsureCreated();
            }
            catch (Exception ex)
            {
                // A throw here fires every frame and floods the log; disable instead.
                VrLog.Error("CameraPatch failed; reverting to flat camera.", ex);
                ReleaseCamera(__instance);
                VrSystem.Shutdown();
            }
        }

        /// <summary>
        /// Logs twice, early and a bit later, and then never again. Between them
        /// these two lines separate the three ways this can fail:
        ///
        ///   rate far below the frame rate  -> wrong hook, camera updated rarely
        ///   rate fine, pose values frozen  -> the pose ring is not advancing
        ///   rate fine, pose values moving  -> our camera is correct and the
        ///                                     engine is rendering from another
        /// </summary>
        private static void ReportProgress()
        {
            // Time-based, not frame-based. A frame-count heartbeat is useless
            // exactly when it matters most: at 1 fps a "every 600 frames" line
            // takes ten minutes to appear, so a catastrophic slowdown and a true
            // hang produce the identical log - nothing at all. On a clock, a
            // frozen game and a 2 fps game look completely different.
            if (_reports >= MaxReports)
                return;

            DateTime now = DateTime.UtcNow;
            double since = (now - _lastReport).TotalSeconds;
            if (since < ReportSeconds)
                return;

            int frames = _calls - _callsAtLastReport;
            _lastReport = now;
            _callsAtLastReport = _calls;
            _reports++;

            VrLog.Info(string.Format(
                "Heartbeat {0}: {1} frames in {2:F1}s = {3:F1} fps | stereo {4}",
                _reports, frames, since, frames / since,
                VrStereoRenderer.TexturesReady ? "on" : "off"));
        }

        /// <summary>Returns the mission to its own camera. Safe to call repeatedly.</summary>
        private static void ReleaseCamera(MissionScreen screen)
        {
            if (!_cameraInstalled)
                return;

            _cameraInstalled = false;

            try
            {
                screen.CustomCamera = null;
                AllowInputField?.SetValue(screen, false);

                // Hand the render surface back too, or the scene keeps rendering
                // from an eye camera that nothing is updating any more.
                if (screen.CombatCamera != null)
                    screen.SceneLayer?.SetCamera(screen.CombatCamera);
            }
            catch (Exception ex)
            {
                VrLog.Error("Failed to hand the camera back to the engine.", ex);
            }
        }
    }
}
