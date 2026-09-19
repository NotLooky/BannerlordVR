using System;
using System.Globalization;
using BannerlordVR.Interop;
using TaleWorlds.Library;
using TaleWorlds.InputSystem;

namespace BannerlordVR.VR
{
    /// <summary>
    /// The game-side half of the in-headset settings panel.
    ///
    /// The panel itself is drawn natively - ImGui into a texture, submitted as an
    /// OpenXR quad layer - because that is the only way to put pixels in front of
    /// a player wearing the headset. The headset sees exactly one thing, the scene
    /// view that AFR retargets into the eye texture, while Gauntlet draws into the
    /// game window's own layers; a menu built the game's way would be perfectly
    /// functional and completely invisible where it is needed.
    ///
    /// This half owns what the values MEAN. Native can move a slider, but only
    /// managed code can apply a world scale to the camera chain or write the
    /// config file, so the two keep a mirror rather than the panel reaching into
    /// the game.
    ///
    ///   push   whenever the truth changes here
    ///   poll   every frame; answers Ok only when the PANEL moved something, so an
    ///          edit is applied and saved exactly once instead of at frame rate
    ///
    /// LIVE VERSUS LATCHED
    ///
    /// World scale multiplies two stage-space translations on the way through and
    /// reconfigures nothing, so it applies the instant the slider moves - which is
    /// the whole point, since it is a setting judged by looking at the world rather
    /// than by reading a number.
    ///
    /// Resolution does not. It sizes the OpenXR swapchains and the eye textures at
    /// session creation, and changing it in place means tearing both down while the
    /// compositor is reading from them. It is written to the config immediately and
    /// applied on the next launch - the same call, for the same reason, the
    /// BioShock VR mod records for its own resolution picker.
    /// </summary>
    public static class VrMenu
    {
        /// <summary>
        /// Opens and closes the panel.
        ///
        /// Not a function key: F1 through F8 are Bannerlord's troop order keys, and
        /// a menu that also shouts "Charge!" every time it opens is not a menu.
        /// </summary>
        private static readonly InputKey ToggleKey = InputKey.End;

        private static float _worldScale = VrCameraDriver.WorldScale;
        private static float _renderScale = VrConfig.Float("render_scale", 0.9f);
        private static float _sharpen = VrConfig.Float("sharpen", 0.0f);

        private static bool _pushed;
        private static bool _brokenLogged;

        /// <summary>Called every frame from the module tick.</summary>
        public static void Tick()
        {
            try
            {
                // The panel is drawn on the render thread and cannot read the
                // config, so it has to be told the starting values once the native
                // side is alive enough to hold them.
                if (!_pushed)
                {
                    _pushed = true;
                    Push();
                    VrLog.Info(string.Format(
                        "VR panel ready: End opens it. World scale {0}, resolution {1}.",
                        Format(_worldScale), Format(_renderScale)));
                }

                if (Input.IsKeyPressed(ToggleKey))
                    NativeMethods.bvr_overlay_toggle();

                Poll();
            }
            catch (Exception ex)
            {
                // A throw here repeats every frame. Report once, then go quiet -
                // the panel is a convenience and must never take the session down.
                if (!_brokenLogged)
                {
                    _brokenLogged = true;
                    VrLog.Error("VR panel failed; it will be left alone from here.", ex);
                }
            }
        }

        /// <summary>
        /// The panel's mirror of the world.
        ///
        /// The calibration half of this is pushed EVERY frame rather than only when
        /// it changes, unlike the settings. It has to be: a progress bar and a
        /// live "which hand am I editing" are values that change continuously
        /// during a calibration, and there is no edit to hang a push off. It is one
        /// struct copy across the ABI per frame.
        /// </summary>
        private static void Push()
        {
            var state = new BvrOverlayState
            {
                WorldScale = _worldScale,
                RenderScale = _renderScale,
                StereoMode = (int)VrRenderMode.Requested,
                StereoPending = VrRenderMode.ChangePending ? 1 : 0,

                CalibState = (int)VrHandCalibration.State,
                CalibHand = (int)VrHandCalibration.Editing,
                CalibProgress = VrHandCalibration.Progress,
                CalibSpread = VrHandCalibration.SpreadDegrees,
                CalibHave = VrHandCalibration.HaveMask,

                HideBody = VrBodyHide.Hiding ? 1 : 0,
                MotionHands = VrMotionHands.Enabled ? 1 : 0,

                UiShow = VrUiSettings.Show ? 1 : 0,
                UiWidth = VrUiSettings.Width,
                UiDistance = VrUiSettings.Distance,
                ScreenWidth = VrUiSettings.ScreenWidth,
                ScreenDistance = VrUiSettings.ScreenDistance,

                TableauProbeState = VrTableauStereo.PanelState,

                Sharpen = _sharpen,
            };

            // The sliders show the hand currently being edited. Pushed every frame
            // so that switching hands, resetting one, or running the automatic pass
            // moves them - the panel has no way to know any of that happened.
            VrHandCalibration.Adjustment(VrHandCalibration.Editing,
                                         out Vec3 offset, out Vec3 euler);

            state.CalibX = offset.x;
            state.CalibY = offset.y;
            state.CalibZ = offset.z;
            state.CalibYaw = euler.x;
            state.CalibPitch = euler.y;
            state.CalibRoll = euler.z;

            NativeMethods.bvr_overlay_push(ref state);
        }

        /// <summary>
        /// The pending flag has to keep flowing, not just be sent once.
        ///
        /// A mode change is applied at the next scene boundary, which may be
        /// minutes away or may be the very next second. The panel cannot know when
        /// that happened, so the state is re-pushed whenever it moves - otherwise
        /// the "takes effect at the next mission" warning stays on screen forever
        /// after the mission that cleared it.
        /// </summary>
        private static bool _lastPushedPending;

        private static void PushPendingIfChanged()
        {
            bool pending = VrRenderMode.ChangePending;
            if (pending == _lastPushedPending)
                return;

            _lastPushedPending = pending;
            Push();
        }

        /// <summary>
        /// Applies whatever the player just moved.
        ///
        /// Persisted the moment it changes rather than when the panel closes: a
        /// crash or an alt-F4 with the headset still on should not cost the value
        /// somebody spent two minutes getting right. Each key is written only when
        /// its own value actually moved, so dragging one slider does not rewrite
        /// the other's line.
        /// </summary>
        private static void Poll()
        {
            // The calibration mirror moves on its own - a progress bar, a hand
            // being switched - so the panel is refreshed unconditionally while one
            // is running rather than only when the player edits something.
            if (VrHandCalibration.Capturing)
                Push();
            else
                PushPendingIfChanged();

            if (NativeMethods.bvr_overlay_poll(out BvrOverlayState panel) != (int)BvrStatus.Ok)
                return;

            Calibrate((BvrCalibCommand)panel.CalibCommand, panel);

            VrUiSettings.SetShow(panel.UiShow != 0);
            VrUiSettings.SetGeometry(panel.UiWidth, panel.UiDistance);
            VrUiSettings.SetScreenGeometry(panel.ScreenWidth, panel.ScreenDistance);

            bool hideBody = panel.HideBody != 0;
            if (hideBody != VrBodyHide.Hiding)
            {
                VrBodyHide.SetHiding(hideBody);
                VrConfig.Save("hide_body", hideBody ? "1" : "0");
                VrLog.Info("VR panel: the player's body is now "
                           + (hideBody ? "hidden" : "shown") + ", saved.");
            }

            bool motionHands = panel.MotionHands != 0;
            if (motionHands != VrMotionHands.Enabled)
            {
                VrMotionHands.SetEnabled(motionHands);
                VrConfig.Save("motion_hands", motionHands ? "1" : "0");
                VrLog.Info("VR panel: motion-control hands and melee are now "
                           + (motionHands ? "ON (experimental)" : "off") + ", saved. "
                           + "The gamepad emulation is unaffected either way.");
            }

            float worldScale = panel.WorldScale;
            float renderScale = panel.RenderScale;

            // Native applies it the moment the slider moves; this half only
            // keeps the mirror and writes the config.
            float sharpen = Math.Max(0f, Math.Min(1f, panel.Sharpen));
            if (Math.Abs(sharpen - _sharpen) > 1e-4f)
            {
                _sharpen = sharpen;
                VrConfig.Save("sharpen", Format(_sharpen));
                VrLog.Info("VR panel: sharpening " + Format(_sharpen) + ", saved.");
            }

            // The probe button. An event rather than a setting, so it is acted on
            // and never compared against a previous value - native clears it as
            // it is read, so arriving here at all means it was clicked once.
            if (panel.TableauProbe != 0)
            {
                VrTableauStereo.RequestRun();
                Push();
            }

            var wanted = (VrRenderMode.Mode)panel.StereoMode;
            if (wanted != VrRenderMode.Requested)
            {
                VrRenderMode.Request(wanted);
                VrConfig.Save("stereo_mode", VrRenderMode.Name(wanted));
                Push();
                _lastPushedPending = VrRenderMode.ChangePending;
            }

            if (Math.Abs(worldScale - _worldScale) > 1e-4f)
            {
                _worldScale = VrCameraDriver.SetWorldScale(worldScale);
                VrConfig.Save("world_scale", Format(_worldScale));
                VrLog.Info("VR panel: world scale " + Format(_worldScale) + ", saved.");

                // The clamp may have moved it, and the panel should show what was
                // actually applied rather than what was asked for.
                if (Math.Abs(_worldScale - worldScale) > 1e-4f)
                    Push();
            }

            if (Math.Abs(renderScale - _renderScale) > 1e-4f)
            {
                _renderScale = renderScale;
                VrResolution.SetWanted(_renderScale);
                VrAfrRenderer.ApplyResolutionScale();
                VrConfig.Save("render_scale", Format(_renderScale));
                VrLog.Info("VR panel: resolution " + Format(_renderScale) + ", saved. "
                           + (VrResolution.CappedByLaunchSize
                              ? "Above what the eye targets were allocated for at launch, "
                                + "so the extra waits for a relaunch; everything up to the "
                                + "launch value is live."
                              : "Live - the scene is shaded at the new size now."));
            }
        }

        /// <summary>
        /// Carries out what the panel asked for.
        ///
        /// The panel is drawn on the render thread and can see neither a skeleton
        /// nor a controller pose, so its buttons set a command rather than doing
        /// the work. This is where that command meets the game thread and an agent
        /// that actually exists.
        ///
        /// Unknown values are ignored rather than throwing. The panel and this
        /// switch are versioned together in the same build, but a stale native DLL
        /// left in the module folder is a real thing that happens, and the right
        /// answer to a command from the future is to do nothing rather than to take
        /// the mission down.
        /// </summary>
        private static void Calibrate(BvrCalibCommand command, BvrOverlayState panel)
        {
            switch (command)
            {
                case BvrCalibCommand.None:
                    return;

                case BvrCalibCommand.Adjust:
                    // Applied without saving. A slider is dragged through dozens of
                    // values on the way to the one that is wanted, and writing the
                    // config file for each would rewrite it at frame rate; Save
                    // closes the mode and is where it is written.
                    VrHandCalibration.SetAdjustment(
                        VrHandCalibration.Editing,
                        new Vec3(panel.CalibX, panel.CalibY, panel.CalibZ, -1f),
                        new Vec3(panel.CalibYaw, panel.CalibPitch, panel.CalibRoll, -1f));
                    return;     // no Push - the panel already has these values

                case BvrCalibCommand.Auto:
                    // Persisted, because this one was asked for. The implicit pass
                    // at the start of a mission is not.
                    VrHandCalibration.StartAuto(persist: true);
                    break;

                case BvrCalibCommand.Manual:
                    VrHandCalibration.OpenManual();
                    break;

                case BvrCalibCommand.Accept:
                    VrHandCalibration.Accept();
                    break;

                case BvrCalibCommand.Cancel:
                    VrHandCalibration.Cancel();
                    break;

                case BvrCalibCommand.Reset:
                    VrHandCalibration.ResetEditing();
                    break;

                case BvrCalibCommand.SelectMain:
                    VrHandCalibration.Select(HandRole.Main);
                    break;

                case BvrCalibCommand.SelectOff:
                    VrHandCalibration.Select(HandRole.Off);
                    break;

                default:
                    VrLog.Warn("VR panel asked for calibration command " + (int)command
                               + ", which this build does not know. Ignored - check that the "
                               + "native DLL in the module folder matches the managed one.");
                    return;
            }

            // The panel's copy of the state is now a frame out of date, and the
            // player is looking at it.
            Push();
        }

        private static string Format(float v)
        {
            return v.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
