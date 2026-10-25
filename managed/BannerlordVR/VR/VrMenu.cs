using System;
using System.Globalization;
using BannerlordVR.Interop;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.InputSystem;
using TaleWorlds.ScreenSystem;

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
        private static float _fovCropH = VrConfig.Float("fov_crop_h", 1f);   // TEST 770
        private static float _fovCropV = VrConfig.Float("fov_crop_v", 1f);
        private static bool _fovCropDetailed = VrConfig.Bool("fov_crop_detailed", false);   // TEST 785
        private static float _fovCropLeft = VrConfig.Float("fov_crop_left", _fovCropH);
        private static float _fovCropRight = VrConfig.Float("fov_crop_right", _fovCropH);
        private static float _fovCropUp = VrConfig.Float("fov_crop_up", _fovCropV);
        private static float _fovCropDown = VrConfig.Float("fov_crop_down", _fovCropV);
        private static float _sharpen = VrConfig.Float("sharpen", 0.0f);
        private static int _sharpenMode = VrConfig.Int("sharpen_mode", 0) == 1 ? 1 : 0;   // TEST 790
        private static int _dlssPreset = VrConfig.Int("dlss_preset", 0);                     // TEST 797
        private static float _wristWidth = VrConfig.Float("wrist_hud_width_m", 0.12f);
        private static float _mapHandWidth = VrConfig.Float("hand_ui_width_m", 0.15f);
        private static int _vrs = Math.Max(0, Math.Min(3, VrConfig.Int("vrs", 0)));
        private static bool _vrsEye = VrConfig.Bool("vrs_eye", true);

        private static bool _pushed;
        private static bool _brokenLogged;

        /// <summary>True while the panel is on screen. The game's own input, the
        /// motion combat, the bow and the holsters all stand down for it.</summary>
        public static bool Open
        {
            get => VrInputManager.PanelOpen;
            private set => VrInputManager.PanelOpen = value;
        }

        /// <summary>Both triggers held this long opens or closes the panel.</summary>
        private static readonly float HoldSeconds = VrConfig.Float("menu_hold_seconds", 2f);
        private static long _bothSince;
        private static bool _holdFired;
        private static bool _scrollLogged;

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

                TriggerHold();
                Open = NativeMethods.bvr_overlay_visible() != 0;
                if (!_cursorBroken)
                {
                    try
                    {
                        FreeCursor(Open);
                    }
                    catch (Exception ex)
                    {
                        _cursorBroken = true;
                        VrLog.Error("VR panel: could not free the mouse cursor; it stays pinned in "
                                    + "play (it still moves with the game paused).", ex);
                    }
                }
                if (Open)
                    Scroll();

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
                GripController = VrHandCalibration.Controller,

                HideBody = VrBodyHide.Hiding ? 1 : 0,
                HandsOnly = VrBodyHide.HandsOnly ? 1 : 0,
                MotionHands = VrMotionHands.Enabled ? 1 : 0,
                PhysicalCombat = VrPhysicalCombat.Enabled ? 1 : 0,
                BodyIk = VrBodyIk.Enabled ? 1 : 0,
                MotionMelee = VrMelee.Enabled ? 1 : 0,
                FriendlyFire = VrPhysicalCombat.FriendlyFire ? 1 : 0,
                Holster = VrHolster.Enabled ? 1 : 0,
                MapVr = VrMapStereo.Wanted ? 1 : 0,

                UiShow = VrUiSettings.Show ? 1 : 0,
                UiWidth = VrUiSettings.Width,
                UiDistance = VrUiSettings.Distance,
                ScreenWidth = VrUiSettings.ScreenWidth,
                ScreenDistance = VrUiSettings.ScreenDistance,

                TableauProbeState = VrTableauStereo.PanelState,

                // So the button can say "rebuilding" while the headset is
                // black, rather than looking like it did nothing.
                ResolutionState = VrResolution.PanelState,

                Sharpen = _sharpen,
                SharpenMode = _sharpenMode,
                DlssPreset = _dlssPreset,
                GripToggle = VrGrip.Toggle ? 1 : 0,
                WristHudWidth = _wristWidth,
                MapHandUiWidth = _mapHandWidth,
                Vrs = _vrs,
                VrsEye = _vrsEye ? 1 : 0,

                StickMouseSpeed = VrStickMouse.Speed,

                PlayerHeight = VrBodyHeight.PlayerHeight,
                BodyScale = VrBodyHeight.Scale,

                HeadUp = VrCameraDriver.HeadOffsetUp,
                HeadUpMounted = VrCameraDriver.HeadOffsetMountedUp,
                ArmScale = VrArmIk.ArmScale,
                BodyWeight = VrHeadBody.WeightS,
                HudHitMarker = BannerlordVR.Patches.CrosshairPatch.HitMarker ? 1 : 0,
                HudPrompts = VrHeadHud.Prompts ? 1 : 0,
                HandFingers = VrHandMesh.Enabled ? 1 : 0,
                DpadThumbrest = VrGamepad.DpadThumbrest ? 1 : 0,
                UiWorldScaleBattle = VrUiSplit.BattleScale,
                UiWorldScaleMap = VrUiSplit.MapScale,
                OwnShieldBash = VrCollide.OwnShield ? 1 : 0,
                UiWorldScaleDeploy = VrUiSplit.DeployMarkerScale,
                HeadForward = VrCameraDriver.HeadOffsetForward,
                BodyTurnDeg = VrHeadAim.BodyTurnDeg,
                UiRecenterNewScreen = VrFlatScreen.RecenterScreens ? 1 : 0,
                UiRecenterAngle = VrFlatScreen.RecenterAngle,
                FovTrueVertical = VrCameraDriver.TrueVerticalChoice ? 1 : 0,
                FovCropH = _fovCropH,
                FovCropV = _fovCropV,
                FovCropDetailed = _fovCropDetailed ? 1 : 0,
                FovCropLeft = _fovCropLeft,
                FovCropRight = _fovCropRight,
                FovCropUp = _fovCropUp,
                FovCropDown = _fovCropDown,
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

            // TEST 750: the Calibration tool, live - what the selected hand holds now.
            state.ToolMode = _toolMode;
            state.ToolHand = _toolHand;
            state.ToolValue = new float[6];
            state.ToolStatus = _toolStatus;
            state.ToolProfile = VrSkinnedWeapon.Profile;
            Slots(ref state);
            if (_toolMode == 0)
            {
                state.ToolLive = VrSkinnedWeapon.ToolSnapshot(_toolHand, state.ToolValue, out string label);
                state.ToolLabel = label;
            }
            else
            {
                VrHandCalibration.Adjustment((HandRole)_toolHand, out Vec3 o, out Vec3 e);
                state.ToolValue[0] = o.x;
                state.ToolValue[1] = o.y;
                state.ToolValue[2] = o.z;
                state.ToolValue[3] = e.x;
                state.ToolValue[4] = e.y;
                state.ToolValue[5] = e.z;
                state.ToolLive = 1;
                state.ToolLabel = (_toolHand == 0 ? "Main" : "Off") + " hand on the "
                                  + VrHandCalibration.ControllerName + " controller"
                                  + (VrMotionHands.Enabled ? "." : " - motion controls are off, so it has no effect.");
            }

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

        private static bool _pushRequested;

        /// <summary>Something the panel shows moved on the game's side (TEST 356:
        /// the character's scale); pushed on the next tick.</summary>
        internal static void RequestPush() => _pushRequested = true;

        private static int _lastPushedResolutionState;

        private static void PushPendingIfChanged()
        {
            // The resolution rebuild's status needs exactly the same treatment
            // and for exactly the same reason: it moves on its own, over several
            // frames, with no edit to hang a push off. Without this the button
            // would say "rebuilding" until the player next touched a slider.
            int resolution = VrResolution.PanelState;
            bool pending = VrRenderMode.ChangePending;

            if (pending == _lastPushedPending && resolution == _lastPushedResolutionState)
                return;

            _lastPushedPending = pending;
            _lastPushedResolutionState = resolution;
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
            // TEST 750: and while the panel is open, so the Calibration tool's
            // sliders follow whatever is picked up or dropped.
            if (VrHandCalibration.Capturing || _pushRequested || Open)
            {
                _pushRequested = false;
                Push();
            }
            else
            {
                PushPendingIfChanged();
            }

            if (NativeMethods.bvr_overlay_poll(out BvrOverlayState panel) != (int)BvrStatus.Ok)
                return;

            Calibrate((BvrCalibCommand)panel.CalibCommand, panel);
            Tool(panel);

            if (panel.GripController != VrHandCalibration.Controller)
            {
                VrHandCalibration.SetController(panel.GripController);
                Push();
            }

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

            bool handsOnly = panel.HandsOnly != 0;
            if (handsOnly != VrBodyHide.HandsOnly)
            {
                VrBodyHide.SetHandsOnly(handsOnly);
                VrConfig.Save("hands_only", handsOnly ? "1" : "0");
                VrLog.Info("VR panel: hands-only is now " + (handsOnly ? "ON" : "off")
                           + ", saved. Only has an effect while the body is hidden.");
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

            bool physicalCombat = panel.PhysicalCombat != 0;
            if (physicalCombat != VrPhysicalCombat.Enabled)
            {
                VrPhysicalCombat.SetEnabled(physicalCombat);
                VrConfig.Save("combat", physicalCombat ? "1" : "0");
                VrLog.Info("VR panel: physical collision combat is now "
                           + (physicalCombat ? "ON" : "off") + ", saved. "
                           + "Off leaves the hands and weapons posed and the game's own "
                           + "trigger attacks in charge - no physical hits, blocks or "
                           + "arrow blocks.");
            }

            bool bodyIk = panel.BodyIk != 0;
            if (bodyIk != VrBodyIk.Enabled)
            {
                VrBodyIk.SetEnabled(bodyIk);
                VrConfig.Save("body_ik", bodyIk ? "1" : "0");
                // TEST 551: one panel switch for all motion controls; room-scale
                // walking rides with the body, it has no switch of its own.
                VrRoomscale.Enabled = bodyIk;
                VrConfig.Save("roomscale", bodyIk ? "1" : "0");
                VrLog.Info("VR panel: the full-body IK is now "
                           + (bodyIk ? "ON" : "off") + ", saved. Off is the game's own "
                           + "animated body, with no pelvis/torso follow, no leg solve "
                           + "and no held pose; the arms still follow your hands.");
            }

            bool motionMelee = panel.MotionMelee != 0;
            if (motionMelee != VrMelee.Enabled)
            {
                VrMelee.SetEnabled(motionMelee);
                VrConfig.Save("melee", motionMelee ? "1" : "0");
                VrLog.Info("VR panel: motion-control combat is now "
                           + (motionMelee ? "ON" : "off") + ", saved. "
                           + (motionMelee
                               ? "Swings attack, a raised hand blocks, your blade lands the hit."
                               : "Off is the game's own combat: the trigger and the movement "
                                 + "stick attack and block, every animation plays, and the "
                                 + "camera follows the body."));
            }

            bool friendlyFire = panel.FriendlyFire != 0;
            if (friendlyFire != VrPhysicalCombat.FriendlyFire)
            {
                VrPhysicalCombat.SetFriendlyFire(friendlyFire);
                VrConfig.Save("combat_friendly_fire", friendlyFire ? "1" : "0");
                VrLog.Info("VR panel: friendly fire is now "
                           + (friendlyFire ? "ON" : "off") + ", saved. "
                           + (friendlyFire
                               ? "Physical swings can land on friends and neutrals; a mission "
                                 + "that forbids friendly fire still refuses."
                               : "Only enemies can be hit - the vanilla single-player rule."));
            }

            bool holster = panel.Holster != 0;
            if (holster != VrHolster.Enabled)
            {
                VrHolster.SetEnabled(holster);
                VrConfig.Save("holster", holster ? "1" : "0");
                VrLog.Info("VR panel: body holsters are now " + (holster ? "ON" : "off") + ", saved. "
                           + (holster
                               ? "The trigger in a body zone draws or sheathes; it no longer attacks."
                               : "The trigger attacks again."));
            }

            bool mapVr = panel.MapVr != 0;
            if (mapVr != VrMapStereo.Wanted)
            {
                VrMapStereo.SetEnabled(mapVr);
                VrConfig.Save("map_stereo", mapVr ? "1" : "0");
                VrLog.Info("VR panel: campaign map in VR is now " + (mapVr ? "ON" : "off")
                           + ", saved. A map already in the headset keeps it until the "
                           + "next campaign load.");
            }

            float worldScale = panel.WorldScale;
            float renderScale = panel.RenderScale;

            // Native applies it the moment the slider moves; this half only
            // keeps the mirror and writes the config.
            float sharpen = Math.Max(0f, Math.Min(1.5f, panel.Sharpen));   // TEST 791: RCAS to 1.5
            if (Math.Abs(sharpen - _sharpen) > 1e-4f)
            {
                _sharpen = sharpen;
                VrConfig.Save("sharpen", Format(_sharpen));
                VrLog.Info("VR panel: sharpening " + Format(_sharpen) + ", saved.");
            }
            if ((panel.GripToggle != 0) != VrGrip.Toggle)
            {
                VrGrip.Toggle = panel.GripToggle != 0;
                VrConfig.Save("grip_toggle", VrGrip.Toggle ? "1" : "0");
                VrLog.Info("VR panel: toggle battle grips " + (VrGrip.Toggle ? "ON" : "off") + ", saved.");
            }
            int sharpenMode = panel.SharpenMode == 1 ? 1 : 0;   // TEST 790
            if (sharpenMode != _sharpenMode)
            {
                _sharpenMode = sharpenMode;
                VrConfig.Save("sharpen_mode", sharpenMode == 1 ? "1" : "0");
                VrLog.Info("VR panel: sharpener " + (sharpenMode == 1 ? "RCAS" : "CAS") + ", saved.");
            }
            if (panel.DlssPreset != _dlssPreset)   // TEST 797
            {
                _dlssPreset = panel.DlssPreset;
                VrConfig.Save("dlss_preset", _dlssPreset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                VrLog.Info("VR panel: DLSS preset " + _dlssPreset + ", saved (applies at the next battle).");
            }

            // TEST 377e: native resizes the wrist panels as the slider moves;
            // this half keeps the mirror and writes the config.
            float wrist = Math.Max(0.03f, Math.Min(0.5f, panel.WristHudWidth));
            if (Math.Abs(wrist - _wristWidth) > 1e-4f)
            {
                _wristWidth = wrist;
                VrConfig.Save("wrist_hud_width_m", Format(_wristWidth));
                VrLog.Info("VR panel: wrist HUD " + Format(_wristWidth) + " m wide, saved.");
            }

            // TEST 527: the campaign map palette, sized apart from the battle wrists.
            float palette = Math.Max(0.03f, Math.Min(0.5f, panel.MapHandUiWidth));
            if (Math.Abs(palette - _mapHandWidth) > 1e-4f)
            {
                _mapHandWidth = palette;
                VrConfig.Save("hand_ui_width_m", Format(_mapHandWidth));
                VrLog.Info("VR panel: campaign hand UI " + Format(_mapHandWidth) + " m wide, saved.");
            }

            // TEST 620: the world markers' size, applied by VrUiSplit next tick.
            if (VrUiSplit.SetScales(panel.UiWorldScaleBattle, panel.UiWorldScaleMap))
            {
                VrConfig.Save("ui_world_scale_battle", Format(VrUiSplit.BattleScale));
                VrConfig.Save("ui_world_scale_map", Format(VrUiSplit.MapScale));
                VrLog.Info($"VR panel: battle markers x{Format(VrUiSplit.BattleScale)}, campaign labels "
                           + $"x{Format(VrUiSplit.MapScale)}, saved.");
            }

            // TEST 648: the pre-battle markers' size.
            if (VrUiSplit.SetDeployMarkerScale(panel.UiWorldScaleDeploy))
            {
                VrConfig.Save("ui_world_scale_deploy_markers", Format(VrUiSplit.DeployMarkerScale));
                VrLog.Info($"VR panel: pre-battle markers x{Format(VrUiSplit.DeployMarkerScale)}, saved.");
            }

            // TEST 427: native switches VRS as the dropdown moves; this half
            // keeps the mirror and writes the config.
            int vrs = Math.Max(0, Math.Min(3, panel.Vrs));
            if (vrs != _vrs)
            {
                _vrs = vrs;
                VrConfig.Save("vrs", _vrs.ToString(System.Globalization.CultureInfo.InvariantCulture));
                VrLog.Info("VR panel: foveated rendering (NVIDIA VRS) "
                           + new[] { "off", "wide", "balanced", "narrow" }[_vrs] + ", saved.");
            }

            bool vrsEye = panel.VrsEye != 0;
            if (vrsEye != _vrsEye)
            {
                _vrsEye = vrsEye;
                VrConfig.Save("vrs_eye", _vrsEye ? "1" : "0");
                VrLog.Info("VR panel: foveation follows the eyes "
                           + (_vrsEye ? "ON" : "off") + ", saved.");
            }

            // Live, like world scale and for the same reason: a pointer speed is
            // judged by pushing the stick and watching, not by reading a number.
            // VrStickMouse owns the clamp, so what comes back is what was taken.
            if (Math.Abs(panel.StickMouseSpeed - VrStickMouse.Speed) > 1e-4f)
            {
                float speed = VrStickMouse.SetSpeed(panel.StickMouseSpeed);
                VrConfig.Save("stick_mouse_speed", Format(speed));
                VrLog.Info("VR panel: menu pointer speed " + Format(speed) + ", saved.");

                // Show what was applied rather than what was asked for, if the
                // clamp moved it.
                if (Math.Abs(speed - panel.StickMouseSpeed) > 1e-4f)
                    Push();
            }

            // TEST 293, the VR tab. Live, like the pointer speed: a head height is
            // judged by looking.
            if (Math.Abs(panel.HeadUp - VrCameraDriver.HeadOffsetUp) > 1e-4f
                || Math.Abs(panel.HeadForward - VrCameraDriver.HeadOffsetForward) > 1e-4f)
            {
                VrCameraDriver.HeadOffsetUp = VrCameraDriver.ClampOffset(panel.HeadUp);
                VrCameraDriver.HeadOffsetForward = VrCameraDriver.ClampOffset(panel.HeadForward);
                VrConfig.Save("head_offset_up_m", Format(VrCameraDriver.HeadOffsetUp));
                VrConfig.Save("head_offset_forward_m", Format(VrCameraDriver.HeadOffsetForward));
                VrLog.Info("VR panel: head camera up " + Format(VrCameraDriver.HeadOffsetUp)
                           + " m, forward " + Format(VrCameraDriver.HeadOffsetForward) + " m, saved.");
            }

            // TEST 442: the saddle's own height.
            if (Math.Abs(panel.HeadUpMounted - VrCameraDriver.HeadOffsetMountedUp) > 1e-4f)
            {
                VrCameraDriver.HeadOffsetMountedUp = VrCameraDriver.ClampOffset(panel.HeadUpMounted);
                VrConfig.Save("head_offset_mounted_up_m", Format(VrCameraDriver.HeadOffsetMountedUp));
                VrLog.Info("VR panel: head camera on a mount up "
                           + Format(VrCameraDriver.HeadOffsetMountedUp) + " m, saved.");
            }

            // TEST 450: the arms' length.
            if (Math.Abs(panel.ArmScale - VrArmIk.ArmScale) > 1e-4f)
            {
                VrArmIk.ArmScale = panel.ArmScale;
                VrConfig.Save("arm_length_scale", Format(VrArmIk.ArmScale));
                VrLog.Info("VR panel: arm length x" + Format(VrArmIk.ArmScale) + ", saved.");
            }

            // TEST 447: the upper body's weight.
            if (Math.Abs(panel.BodyWeight - VrHeadBody.WeightS) > 1e-4f)
            {
                VrHeadBody.WeightS = Math.Max(0f, Math.Min(0.6f, panel.BodyWeight));
                VrConfig.Save("body_weight_s", Format(VrHeadBody.WeightS));
                VrLog.Info("VR panel: upper body weight " + Format(VrHeadBody.WeightS) + " s, saved.");
            }

            // TEST 462: the hit marker and the pickup prompt.
            bool hitMarker = panel.HudHitMarker != 0;
            if (hitMarker != BannerlordVR.Patches.CrosshairPatch.HitMarker)
            {
                BannerlordVR.Patches.CrosshairPatch.HitMarker = hitMarker;
                VrConfig.Save("hud_hit_marker", hitMarker ? "1" : "0");
                VrLog.Info("VR panel: hit marker " + (hitMarker ? "ON" : "off") + ", saved.");
            }

            bool prompts = panel.HudPrompts != 0;
            if (prompts != VrHeadHud.Prompts)
            {
                VrHeadHud.Prompts = prompts;
                VrConfig.Save("hud_prompts", prompts ? "1" : "0");
                VrLog.Info("VR panel: pickup prompt " + (prompts ? "ON" : "off") + ", saved.");
            }

            bool fingers = panel.HandFingers != 0;
            if (fingers != VrHandMesh.Enabled)
            {
                VrHandMesh.SetEnabled(fingers);
                VrConfig.Save("finger_mesh", fingers ? "1" : "0");
                VrLog.Info("VR panel: five-finger hands " + (fingers ? "ON" : "off") + ", saved.");
            }

            bool thumbrest = panel.DpadThumbrest != 0;
            if (thumbrest != VrGamepad.DpadThumbrest)
            {
                VrGamepad.DpadThumbrest = thumbrest;
                VrConfig.Save("dpad_thumbrest", thumbrest ? "1" : "0");
                VrLog.Info("VR panel: D-pad on left thumbrest + right stick " + (thumbrest ? "ON" : "off") + ", saved.");
            }

            // TEST 645: shield bashing (TEST 642).
            bool bash = panel.OwnShieldBash != 0;
            if (bash != VrCollide.OwnShield)
            {
                VrCollide.OwnShield = bash;
                VrConfig.Save("collide_own_shield", bash ? "1" : "0");
                VrLog.Info("VR panel: weapon hits my own shield " + (bash ? "ON" : "off") + ", saved.");
            }

            // TEST 711: the new-screen re-centre and its turn-away angle.
            bool recenterNew = panel.UiRecenterNewScreen != 0;
            if (recenterNew != VrFlatScreen.RecenterScreens)
            {
                VrFlatScreen.RecenterScreens = recenterNew;
                VrConfig.Save("ui_recenter_new_screen", recenterNew ? "1" : "0");
                VrLog.Info("VR panel: re-centre on a new screen " + (recenterNew ? "ON" : "off") + ", saved.");
            }

            // fov_true_vertical: the projection cache and HoldEngineFov both key on
            // it, so the eyes re-fit on the next frame.
            bool trueVertical = panel.FovTrueVertical != 0;
            if (trueVertical != VrCameraDriver.TrueVerticalChoice)
            {
                VrCameraDriver.TrueVerticalChoice = trueVertical;
                VrConfig.Save("fov_true_vertical", trueVertical ? "1" : "0");
                VrLog.Info("VR panel: true vertical fov " + (trueVertical ? "ON" : "off") + ", saved.");
            }

            // TEST 770: native already applies the fov; the eye targets follow
            // through the Apply the panel raises when the slider is let go.
            if (Math.Abs(panel.FovCropH - _fovCropH) > 1e-4f || Math.Abs(panel.FovCropV - _fovCropV) > 1e-4f)
            {
                _fovCropH = panel.FovCropH;
                _fovCropV = panel.FovCropV;
                VrConfig.Save("fov_crop_h", Format(_fovCropH));
                VrConfig.Save("fov_crop_v", Format(_fovCropV));
                VrLog.Info("VR panel: fov crop " + Format(_fovCropH) + " x " + Format(_fovCropV) + ", saved.");
            }

            // TEST 785: the detailed crop, one value per edge.
            bool detailed = panel.FovCropDetailed != 0;
            if (detailed != _fovCropDetailed || Math.Abs(panel.FovCropLeft - _fovCropLeft) > 1e-4f
                || Math.Abs(panel.FovCropRight - _fovCropRight) > 1e-4f || Math.Abs(panel.FovCropUp - _fovCropUp) > 1e-4f
                || Math.Abs(panel.FovCropDown - _fovCropDown) > 1e-4f)
            {
                _fovCropDetailed = detailed;
                _fovCropLeft = panel.FovCropLeft;
                _fovCropRight = panel.FovCropRight;
                _fovCropUp = panel.FovCropUp;
                _fovCropDown = panel.FovCropDown;
                VrConfig.Save("fov_crop_detailed", detailed ? "1" : "0");
                VrConfig.Save("fov_crop_left", Format(_fovCropLeft));
                VrConfig.Save("fov_crop_right", Format(_fovCropRight));
                VrConfig.Save("fov_crop_up", Format(_fovCropUp));
                VrConfig.Save("fov_crop_down", Format(_fovCropDown));
                VrLog.Info("VR panel: detailed fov crop " + (detailed ? "ON" : "off") + " - left " + Format(_fovCropLeft)
                           + ", right " + Format(_fovCropRight) + ", top " + Format(_fovCropUp) + ", bottom "
                           + Format(_fovCropDown) + ", saved.");
            }

            if (Math.Abs(panel.UiRecenterAngle - VrFlatScreen.RecenterAngle) > 1e-4f)
            {
                VrFlatScreen.RecenterAngle = Math.Max(0f, Math.Min(180f, panel.UiRecenterAngle));
                VrConfig.Save("ui_recenter_angle", Format(VrFlatScreen.RecenterAngle));
                VrLog.Info("VR panel: a new screen re-centres past " + Format(VrFlatScreen.RecenterAngle)
                           + " deg of head turn, saved.");
            }

            if (Math.Abs(panel.BodyTurnDeg - VrHeadAim.BodyTurnDeg) > 1e-4f)
            {
                VrHeadAim.BodyTurnDeg = Math.Max(0f, Math.Min(180f, panel.BodyTurnDeg));
                VrConfig.Save("body_turn_deadzone_deg", Format(VrHeadAim.BodyTurnDeg));
                VrLog.Info("VR panel: the body turns after " + Format(VrHeadAim.BodyTurnDeg)
                           + " deg of head turn, saved.");
            }

            // An event, like the probe below: native clears it as it is read.
            // TEST 356: an explicit recentre also measures the player's height.
            if (panel.Recenter != 0)
            {
                VrCameraDriver.RequestRecentre(measureHeight: true);
                VrLog.Info("VR panel: recentred.");
            }

            // TEST 356, the VR tab: measure now, or the slider.
            if (panel.HeightMeasure != 0)
                VrCameraDriver.RequestHeightMeasure();
            else if (Math.Abs(panel.PlayerHeight - VrBodyHeight.PlayerHeight) > 1e-4f)
                VrBodyHeight.SetHeight(panel.PlayerHeight, "VR panel slider");

            // The probe button. An event rather than a setting, so it is acted on
            // and never compared against a previous value - native clears it as
            // it is read, so arriving here at all means it was clicked once.
            if (panel.TableauProbe != 0)
            {
                VrTableauStereo.RequestRun();
                Push();
            }

            // The Apply button under the resolution slider. An event too, and
            // read the same way - but acted on AFTER the slider block below has
            // had a chance to run, because a player who drags and then clicks in
            // one panel visit sends both in one poll, and rebuilding at the value
            // from before the drag is the one outcome that makes the button look
            // broken. See the end of this method.
            bool applyResolution = panel.ResolutionApply != 0;

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
                              ? "Above what the eye targets are allocated for, so the "
                                + "extra waits for Apply; everything up to the allocated "
                                + "value is live already."
                              : "Live - the scene is shaded at the new size now."));
            }

            // LAST, so the slider above has already told VrResolution what was
            // asked for. Both arrive in the same poll when the player drags and
            // then clicks without closing the panel, and the rebuild has to use
            // the value they just dragged to rather than the one before it.
            if (applyResolution)
            {
                VrResolution.RequestApply();
                Push();
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

        // TEST 750: the Calibration tool's selection (the panel's, echoed back) and
        // the last thing a button did.
        private static int _toolMode;
        private static int _toolHand;
        private static string _toolStatus = string.Empty;

        /// <summary>A command is carried out for the selection it was clicked
        /// under, then the selection follows the panel.</summary>
        private static void Tool(BvrOverlayState panel)
        {
            var command = (BvrToolCommand)panel.ToolCommand;
            float[] v = panel.ToolValue ?? new float[6];
            string status = null;

            // TEST 751: the profile first, so a save in the same poll lands in it.
            if (panel.ToolProfile != VrSkinnedWeapon.Profile)
            {
                VrSkinnedWeapon.SetProfile(panel.ToolProfile);
                status = "Now using " + (VrSkinnedWeapon.Profile == 0 ? "Original" : "Profile " + VrSkinnedWeapon.Profile) + ".";
            }

            if (command >= BvrToolCommand.Equip0 && command <= BvrToolCommand.Sheathe)
            {
                status = Equip(command);
            }
            else if (command != BvrToolCommand.None && _toolMode == 0)
            {
                status = VrSkinnedWeapon.ToolCommand(_toolHand, command, v);
            }
            else if (command != BvrToolCommand.None)
            {
                var role = (HandRole)_toolHand;
                switch (command)
                {
                    case BvrToolCommand.Adjust:
                        VrHandCalibration.SetAdjustment(role, new Vec3(v[0], v[1], v[2], -1f),
                                                        new Vec3(v[3], v[4], v[5], -1f));
                        break;
                    case BvrToolCommand.Zero:
                        VrHandCalibration.SetAdjustment(role, Vec3.Zero, Vec3.Zero);
                        status = "Zeroed - not saved yet.";
                        break;
                    case BvrToolCommand.Revert:
                        VrHandCalibration.RevertAdjustment(role);
                        status = "Back to the saved grip.";
                        break;
                    case BvrToolCommand.SaveHand:
                        VrHandCalibration.SetAdjustment(role, new Vec3(v[0], v[1], v[2], -1f),
                                                        new Vec3(v[3], v[4], v[5], -1f));
                        VrHandCalibration.SaveOne(role);
                        status = "Saved the " + role + " hand grip for " + VrHandCalibration.ControllerName + ".";
                        VrLog.Info("TEST 750 calibration tool: " + status);
                        break;
                }
            }

            if (status != null)
                _toolStatus = status;

            int mode = panel.ToolMode == 1 ? 1 : 0;
            int hand = panel.ToolHand == 1 ? 1 : 0;
            if (mode != _toolMode || hand != _toolHand)
                _toolStatus = string.Empty;
            _toolMode = mode;
            _toolHand = hand;
        }

        /// <summary>TEST 751: the player's four weapon slots and which one the main
        /// hand holds, for the tool's Equip dropdown.</summary>
        private static void Slots(ref BvrOverlayState state)
        {
            Agent agent = TaleWorlds.MountAndBlade.Mission.Current?.MainAgent;
            var names = new string[4];
            state.ToolWielded = -1;

            if (agent != null && agent.IsActive())
            {
                for (int i = 0; i < 4; i++)
                {
                    ItemObject item = agent.Equipment[(EquipmentIndex)i].Item;
                    string name = item?.Name?.ToString() ?? string.Empty;
                    names[i] = name.Length > 47 ? name.Substring(0, 47) : name;
                }

                EquipmentIndex main = agent.GetPrimaryWieldedItemIndex();
                EquipmentIndex off = agent.GetOffhandWieldedItemIndex();
                EquipmentIndex shown = main != EquipmentIndex.None ? main : off;
                if (shown >= EquipmentIndex.Weapon0 && shown <= EquipmentIndex.Weapon3)
                    state.ToolWielded = (int)shown;
            }

            state.ToolSlot0 = names[0] ?? string.Empty;
            state.ToolSlot1 = names[1] ?? string.Empty;
            state.ToolSlot2 = names[2] ?? string.Empty;
            state.ToolSlot3 = names[3] ?? string.Empty;
        }

        /// <summary>TEST 751: wields a slot, or puts both hands' weapons away -
        /// the engine's own swap, instant, like the holsters.</summary>
        private static string Equip(BvrToolCommand command)
        {
            Agent agent = TaleWorlds.MountAndBlade.Mission.Current?.MainAgent;
            if (agent == null || !agent.IsActive())
                return "Equipping works in a mission, with your character on the field.";

            try
            {
                if (command == BvrToolCommand.Sheathe)
                {
                    agent.TryToSheathWeaponInHand(Agent.HandIndex.OffHand, Agent.WeaponWieldActionType.Instant);
                    agent.TryToSheathWeaponInHand(Agent.HandIndex.MainHand, Agent.WeaponWieldActionType.Instant);
                    return "Weapons put away.";
                }

                var slot = (EquipmentIndex)(command - BvrToolCommand.Equip0);
                ItemObject item = agent.Equipment[slot].Item;
                if (item == null)
                    return "That slot is empty.";

                agent.TryToWieldWeaponInSlot(slot, Agent.WeaponWieldActionType.Instant, false);
                VrLog.Info("TEST 751 calibration tool: equipped slot " + (int)slot + " '" + item.StringId + "'.");
                return "Equipped " + item.Name + ".";
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 751 calibration tool: equipping failed.", ex);
                return "Equipping failed - see the log.";
            }
        }

        /// <summary>
        /// Frees the Windows cursor while the panel is open. In play the engine
        /// hides the cursor and pins it to the window centre for mouse-look, so
        /// the panel's cursor sat in the middle and only moved with the game
        /// paused - the pause menu is a layer asking for a visible mouse. This is
        /// the same ask, from an empty layer (VrMissionPhase ignores it, so the
        /// flat-screen panel does not come up for it).
        /// </summary>
        private static void FreeCursor(bool open)
        {
            ScreenBase top = open ? ScreenManager.TopScreen : null;
            if (_cursorScreen == top)
                return;

            if (_cursorScreen != null && _cursorScreen.HasLayer(_cursorLayer))
                _cursorScreen.RemoveLayer(_cursorLayer);
            _cursorScreen = null;

            if (top == null)
                return;

            if (_cursorLayer == null)
            {
                _cursorLayer = new VrCursorLayer();
                _cursorLayer.InputRestrictions.SetMouseVisibility(true);
            }

            top.AddLayer(_cursorLayer);
            _cursorScreen = top;
        }

        private static VrCursorLayer _cursorLayer;
        private static bool _cursorBroken;
        private static ScreenBase _cursorScreen;

        /// <summary>Both triggers held for HoldSeconds toggles the panel, once per
        /// hold - they have to be let go before it can fire again.</summary>
        private static void TriggerHold()
        {
            if (HoldSeconds <= 0f
                || NativeMethods.bvr_get_input_state(out BvrInputState input) != (int)BvrStatus.Ok)
                return;

            // TEST 370b: under grip_hold both grips are also a sword and a shield,
            // or a crossbow held in two hands - only empty hands open the panel.
            bool both = input.Left.IsActive != 0 && input.Right.IsActive != 0
                        && input.Left.Trigger >= 0.7f && input.Right.Trigger >= 0.7f
                        && !VrGrab.Holding(VrHand.Left) && !VrGrab.Holding(VrHand.Right);
            if (!both)
            {
                _bothSince = 0L;
                _holdFired = false;
                return;
            }

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_bothSince == 0L)
                _bothSince = now;

            if (_holdFired || now - _bothSince < (long)(HoldSeconds * System.Diagnostics.Stopwatch.Frequency))
                return;

            _holdFired = true;
            NativeMethods.bvr_overlay_toggle();
            NativeMethods.bvr_trigger_haptic((int)VrHand.Left, 0.5f, 0.08f);
            NativeMethods.bvr_trigger_haptic((int)VrHand.Right, 0.5f, 0.08f);
            VrLog.Info("VR panel: both triggers held " + Format(HoldSeconds) + " s - toggled.");
        }

        /// <summary>The mouse wheel, handed to the panel. The engine's delta may
        /// come in Windows units (120 a notch) or in notches; either becomes
        /// notches, and the first one is logged so the guess can be checked.</summary>
        private static void Scroll()
        {
            float raw = VrInputManager.PanelScroll();
            if (raw == 0f)
                return;

            // ponytail: unit guessed from magnitude; fix from the logged value if scrolling is 120x off.
            float notches = Math.Abs(raw) >= 30f ? raw / 120f : raw;
            NativeMethods.bvr_overlay_scroll(notches);

            if (!_scrollLogged)
            {
                _scrollLogged = true;
                VrLog.Info("VR panel: first wheel delta " + Format(raw) + " -> " + Format(notches) + " notch(es).");
            }
        }

        private static string Format(float v)
        {
            return v.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>An empty layer whose only job is asking for the mouse cursor
    /// while the VR panel is open. See VrMenu.FreeCursor.</summary>
    public sealed class VrCursorLayer : ScreenLayer
    {
        public VrCursorLayer() : base("BannerlordVRPanelCursor", 100000)
        {
        }
    }
}
