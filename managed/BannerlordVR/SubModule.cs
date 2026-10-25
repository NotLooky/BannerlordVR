using System;
using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine.Options;
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

        /// <summary>What the align key does. False - the default - restores the
        /// saved default pose. True gives it back its old behaviour of re-taking
        /// the grip from the animation.</summary>
        private static readonly bool AlignRecaptures = VrConfig.Bool("grip_align_recaptures", false);

        private Harmony _harmony;


        /// <summary>
        /// Adds the pre-display seam to every mission.
        ///
        /// A MissionBehavior rather than another Harmony patch, because the engine
        /// already calls OnPreDisplayMissionTick on its behaviors once per frame
        /// and there is nothing to intercept - the hook exists, it just has to be
        /// registered. See VrLateWeapon for what it is for.
        /// </summary>
        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);

            try
            {
                VrLateWeapon.Reset();
                mission.AddMissionBehavior(new VrLateWeapon());
            }
            catch (Exception ex)
            {
                VrLog.Error("Could not add the pre-display weapon behavior; weapons stay on "
                            + "the AfterMissionTick seam. Nothing else is affected.", ex);
            }

            VR.Physics.VrEnemyPhysics.MissionBehaviorInitialize(mission);
        }
        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();

            VrLog.Initialize();
            VrLog.Info("OnSubModuleLoad");
            VrDlssRuntime.Install();
            VrConfig.Load();

            try
            {
                _harmony = new Harmony(HarmonyId);

                // ONE PATCH CLASS AT A TIME, so one bad target cannot take the
                // rest of the mod with it.
                //
                // This is not hypothetical. PatchAll stops at the first class
                // that fails, and a patch whose annotation did not match its
                // target - CombatHitPatch, whose `in` parameter is a byref in
                // metadata - made PatchAll throw. Every patch after it silently
                // stopped applying and a whole session ran vanilla: no hands,
                // flat stereo, "the mod is broken". A class that fails now
                // reports itself and the others still apply.
                int patched = 0;

                foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
                {
                    if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0)
                        continue;

                    try
                    {
                        _harmony.CreateClassProcessor(type).Patch();
                        patched++;
                    }
                    catch (Exception ex)
                    {
                        VrLog.Error("Harmony patch class " + type.Name + " failed; the other "
                                    + "patches still apply and VR keeps running.", ex);
                    }
                }

                VrLog.Info("Harmony patches applied: " + patched + " class(es).");
            }
            catch (Exception ex)
            {
                // A failed patch must not take the game down with it.
                VrLog.Error("Harmony patching failed outright; VR features will be inert.", ex);
            }

            VrHandCalibration.Load();
            VrSystem.Initialize();
            VR.Physics.VrEnemyPhysics.Load();
        }

        protected override void AfterAsyncTickTick(float dt)
        {
            base.AfterAsyncTickTick(dt);
            VR.Physics.VrEnemyPhysics.AfterAsyncTick();
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

            RepairStaleActionCodes();
            RepairStaleVoiceCodes();
            DisableFlatPostFx();
            DisableChatBox();

            // TEST 473: here, not with the others - War Sails is not a dependency,
            // so its assemblies are only promised to be loaded by now.
            Patches.HelmPatch.Install(_harmony);

            // TEST 512: likewise, so War Sails' ship ballista and mangonel are seen.
            Patches.SiegeAimPatch.Install(_harmony);

            // TEST 580: War Sails' placer overrides the land one's laser seam.
            Patches.NavalDeploymentPlacementPatch.Install(_harmony);
        }

        /// <summary>
        /// TEST 781: motion blur, depth of field and film grain are flat-screen
        /// effects - in a headset they smear head motion, blur what the eye is
        /// focused on and crawl over both eyes. ALWAYS off (TEST 784): at launch,
        /// and after every Apply from the options screen - a graphics preset turns
        /// them back on. OnNativeOptionsApplied fires inside NativeOptions.Apply,
        /// before OptionsVM's SaveConfig, so the saved file keeps them off too.
        /// The same order as the options screen: SetConfig, Apply with the changed
        /// flags, SaveConfig. Our own Apply re-raises the event; with all three
        /// off it does nothing.
        /// </summary>
        private static void DisableFlatPostFx()
        {
            NativeOptions.OnNativeOptionsApplied -= ForcePostFxOff;
            NativeOptions.OnNativeOptionsApplied += ForcePostFxOff;
            ForcePostFxOff();
        }

        private static void ForcePostFxOff()
        {
            try
            {
                bool dof = Off(NativeOptions.NativeOptionsType.DepthOfField);
                bool blur = Off(NativeOptions.NativeOptionsType.MotionBlur);
                bool grain = Off(NativeOptions.NativeOptionsType.FilmGrain);
                if (!dof && !blur && !grain)
                    return;

                NativeOptions.Apply(0, 0, 0, dof ? 1 : 0, blur ? 1 : 0, 0, 0, 0, 0, 0);
                NativeOptions.SaveConfig();
                VrLog.Info("TEST 784: turned off for VR -" + (dof ? " depth of field" : "") + (blur ? " motion blur" : "")
                           + (grain ? " film grain" : "") + " (they do not work in a headset).");
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 781: could not turn off motion blur / depth of field / film grain.", ex);
            }
        }

        /// <summary>
        /// TEST 783: the singleplayer chat box off by default - in battle it sits on
        /// the head HUD, and on the right wrist it would shrink the army bar and kill
        /// feeds (one scale per panel). Once per install, like DisableFlatPostFx:
        /// Options > Gameplay "Enable singleplayer chat box" turns it back on.
        /// chat_off_done = 0 in the cfg runs it again.
        /// </summary>
        private static void DisableChatBox()
        {
            if (VrConfig.Bool("chat_off_done", false))
                return;

            try
            {
                bool wasOn = ManagedOptions.GetConfig(ManagedOptions.ManagedOptionsType.EnableSingleplayerChatBox) != 0f;
                if (wasOn)
                {
                    ManagedOptions.SetConfig(ManagedOptions.ManagedOptionsType.EnableSingleplayerChatBox, 0f);
                    ManagedOptions.SaveConfig();
                }

                VrConfig.Save("chat_off_done", "1");
                VrLog.Info("TEST 783: singleplayer chat box " + (wasOn ? "turned off" : "already off")
                           + " for VR. Options > Gameplay turns it back on.");
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 783: could not turn off the singleplayer chat box.", ex);
            }
        }

        /// <summary>True when the option was on and is now off.</summary>
        private static bool Off(NativeOptions.NativeOptionsType type)
        {
            if (NativeOptions.GetConfig(type) == 0f)
                return false;

            NativeOptions.SetConfig(type, 0f);
            return true;
        }

        /// <summary>
        /// Re-resolves ActionIndexCache's static action codes if they were cached
        /// before the game loaded its animations.
        ///
        /// THE ARENA CRASH (2026-09-25, twice). ActionIndexCache resolves all ~215
        /// act_* statics once, in its static initializer, by name. The type is
        /// beforefieldinit, so the JIT may run that initializer early - and every
        /// module's OnSubModuleLoad runs before action_types.xml is read. Resolved
        /// then, every code is -1 for the life of the process. The arena audience
        /// passes act_arena_spectator (-1) to MBAnimation.PrefetchAnimationClip,
        /// whose native side reads anims[-1] - whatever the heap holds 8 bytes
        /// before the array - and indexes a table with it. The crash log's
        /// registers showed the action argument -1 and the index as ASCII "tion".
        /// Usually the stray value is harmless, hence "only sometimes".
        ///
        /// Re-running the initializer repeats exactly the vanilla Create(name)
        /// calls, now that the names resolve.
        /// </summary>
        private static void RepairStaleActionCodes()
        {
            try
            {
                FieldInfo probe = typeof(ActionIndexCache).GetField("act_arena_spectator");
                int cached = ((ActionIndexCache)probe.GetValue(null)).Index;
                int real = MBAnimation.GetActionCodeWithName("act_arena_spectator");
                if (cached == real)
                    return;

                typeof(ActionIndexCache).TypeInitializer.Invoke(null, null);
                VrLog.Warn($"ActionIndexCache was initialised before the animations loaded: "
                           + $"act_arena_spectator was {cached}, really {real}. Re-resolved all "
                           + $"act_* codes; it now reads {((ActionIndexCache)probe.GetValue(null)).Index}. "
                           + "Left alone, the arena audience crashes the engine at random.");
            }
            catch (Exception ex)
            {
                VrLog.Error("Could not check ActionIndexCache for stale action codes.", ex);
            }
        }

        private static void RepairStaleVoiceCodes()
        {
            try
            {
                // Like ActionIndexCache, voice IDs can be cached as -1 before their XML loads.
                Type voices = typeof(SkinVoiceManager.VoiceType);
                FieldInfo probe = voices.GetField("Charge");
                var cached = (SkinVoiceManager.SkinVoiceType)probe.GetValue(null);
                int real = new SkinVoiceManager.SkinVoiceType("Charge").Index;
                if (real < 0 || cached.Index == real)
                    return;

                voices.TypeInitializer.Invoke(null, null);
                VrLog.Info($"Voice IDs re-resolved after loading: Charge was {cached.Index}, "
                           + $"really {real}; now {((SkinVoiceManager.SkinVoiceType)probe.GetValue(null)).Index}.");
            }
            catch (Exception ex)
            {
                VrLog.Error("Could not repair stale voice IDs.", ex);
            }
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
            VR.Physics.VrEnemyPhysics.ApplicationTick();

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

                VrMapStereo.Tick();
                VrMissionPhase.Tick();
                VrFlatScreen.Tick();

                // Which part of the interface image is the world panel, the HUD
                // and the hands' (TEST 590); the projection sizes the world panel.
                VrUiSplit.Tick();

                // The interface drawn at k x the window while on the overlay (TEST 591).
                VrUiSupersample.Tick();

                // After the phase: the panel only fills the view while the
                // interface is on the overlay (TEST 283).
                VrUiProjection.Tick();

                // Straight after it: the factor that matches the headset is
                // worked out where the panel is sized (TEST 299).
                VrUiTarget.Tick();

                // The map bar's own render target, before the wrists read it (TEST 531).
                VrHandTarget.Tick();

                // Where the HUD widgets that live on the wrists are (TEST 377).
                VrWristHud.Tick();

                // What stays in front of the face in a battle (TEST 377g).
                VrHeadHud.Tick();

                // The laser first: while it is on the interface it owns the
                // cursor, and the stick pointer stands down (TEST 378).
                VrLaser.Tick();

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

            // AFTER EnsureCreated, which is what actually allocates the new pair
            // once a rescale has released the old one. This only drives the
            // sequence and watches for each step to land, so running it first
            // would cost the whole rebuild a frame at every stage.
            if (VrSystem.IsActive)
                VrResolution.Tick();

            // Recentre. Keyboard for now; Phase 6 moves this onto a controller
            // chord, because reaching for a keyboard in a headset is its own
            // small indignity. IsKeyPressed is edge-triggered, so this fires once
            // per press rather than every frame the key is held.
            if (VrSystem.IsActive && Input.IsKeyPressed(RecentreKey))
                VrCameraDriver.RequestRecentre(measureHeight: true);

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
            {
                // RESTORES the saved pose; it does not re-capture one.
                //
                // Re-taking the grip from the animation used to be what this key
                // did, and one mistaken press threw away a calibration that had
                // taken a session to get right - with nothing to fall back on,
                // because the pass overwrites the only copy. Restoring is the safe
                // direction for a bare function key. Capturing is still there, in
                // the panel and on manual mode's X, where it takes a deliberate act.
                if (AlignRecaptures)
                    VrHandCalibration.StartAuto(persist: true);
                else
                    VrHandCalibration.RestoreDefaults();
            }
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
