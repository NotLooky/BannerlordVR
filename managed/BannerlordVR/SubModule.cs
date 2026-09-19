using System;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR
{
    public class SubModule : MBSubModuleBase
    {
        public const string HarmonyId = "com.bannerlordvr.mod";

        /// <summary>Re-anchors VR forward to wherever the player is facing.</summary>
        private const InputKey RecentreKey = InputKey.F10;

        /// <summary>Re-takes the weapon grip from whatever the animation is doing
        /// right now. Named in the config, because a hotkey that collides with a
        /// player's own binding should not need a rebuild to move.</summary>
        private static readonly string AlignKeyName = VrConfig.String("grip_align_key", "F11");

        private Harmony _harmony;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();

            VrLog.Initialize();
            VrLog.Info("OnSubModuleLoad");
            VrConfig.Load();

            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll();
                VrLog.Info("Harmony patches applied.");
            }
            catch (Exception ex)
            {
                // A failed patch must not take the game down with it.
                VrLog.Error("Harmony PatchAll failed; VR features will be inert.", ex);
            }

            VrHandCalibration.Load();
            VrSystem.Initialize();
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();

            // Kept from the original bring-up: visible confirmation that managed
            // code is live. Colour doubles as status - green when VR is up.
            InformationManager.DisplayMessage(new InformationMessage(
                "BannerlordVR: " + VrSystem.StatusText,
                VrSystem.IsActive ? Colors.Green : Colors.Red));

            VrLog.Info("Main menu reached. Status: " + VrSystem.StatusText);
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);

            // Stamped, never cleared - the AGE is the signal, so a second call to
            // clear it would cost a P/Invoke to say something the timestamp
            // already says. "OnApplicationTick, 400 ms ago" means our code has not
            // run for 400 ms, which for a crash that lands during scene loading is
            // exactly the thing the log could not previously say either way.
            VrBreadcrumb.Set(VrBreadcrumb.AppTick);

            VrSystem.Tick();

            // BEFORE the IsActive gate below, and that is the point: this is what
            // puts the main menu and the campaign map in the headset, and neither
            // of them is a mission. Everything after the gate is mission work.
            //
            // Phase first: the flat screen asks it which way to go, and every
            // camera write in the mod is gated on the same answer.
            if (VrSystem.IsActive)
            {
                // Once, so the native quad is built from the same numbers the
                // panel is about to show rather than from its own config read.
                VrUiSettings.EnsurePushed();

                VrMissionPhase.Tick();
                VrFlatScreen.Tick();

                // After the phase, because it asks the phase whether there is a
                // menu to point at.
                VrStickMouse.Tick(dt);
            }

            // Eye textures are allocated here, not from the mission camera hook,
            // so they exist well before OnSceneRenderingStarted needs them. Their
            // creation is deferred onto rgl's own thread and takes a frame or two
            // to come back, which is far too late if it starts when a mission does.
            if (VrSystem.IsActive && VrSystem.IsStereoReady)
                VrStereoRenderer.EnsureCreated();

            // Recentre. Keyboard for now; Phase 6 moves this onto a controller
            // chord, because reaching for a keyboard in a headset is its own
            // small indignity. IsKeyPressed is edge-triggered, so this fires once
            // per press rather than every frame the key is held.
            if (VrSystem.IsActive && Input.IsKeyPressed(RecentreKey))
                VrCameraDriver.RequestRecentre();

            // Settings. Same keyboard caveat as above, and the same reason it is
            // survivable: world scale is judged by looking at the world, not by
            // reading a number, so the keys are all a player in a headset needs to
            // find by feel.
            if (VrSystem.IsActive)
                VrMenu.Tick();

            if (!VrSystem.IsActive)
                return;

            // The controllers as an Xbox pad.
            //
            // Retried every frame rather than done once at load, because
            // Input._inputManager is set by the engine during startup and there is
            // no event that says when. Install() returns immediately once it has
            // taken, so the retry costs a null check per frame until it does.
            VrInputManager.Install();

            // Grip calibration: the shortcut keys, the automatic pass's clock, and
            // the sticks while the manual mode is open. Ticked here rather than
            // from the camera hook because it must keep responding on the frames
            // where the hands are not being posed - the whole automatic window is
            // made of those, by design.
            //
            // The panel is where this actually lives now; these two keys are
            // shortcuts to the same thing rather than a second way of doing it.
            VrHandCalibration.Tick(dt);

            if (VrKeys.Pressed(AlignKeyName))
                VrHandCalibration.StartAuto(persist: true);
        }

        protected override void OnSubModuleUnloaded()
        {
            // Before anything else: the game keeps running long enough to read
            // input after this, and it must read the real thing.
            VrInputManager.Restore();

            VrSystem.Shutdown();

            try
            {
                _harmony?.UnpatchAll(HarmonyId);
            }
            catch (Exception ex)
            {
                VrLog.Error("UnpatchAll failed.", ex);
            }

            VrLog.Info("OnSubModuleUnloaded");
            base.OnSubModuleUnloaded();
        }
    }
}
