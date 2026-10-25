using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;
using TaleWorlds.ScreenSystem;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Which part of a mission is happening, and whether VR should be driving it.
    ///
    /// ONE ANSWER, NOT FIVE
    ///
    /// Four separate things write to the engine's camera or its look input -
    /// VrCameraDriver, VrAfrRenderer, VrEngineAim and VrGameCamera - and all of
    /// them have to agree about when to stand down, or the ones that do not
    /// simply undo the ones that do. Asking here means there is a single
    /// condition to change rather than four to keep in step.
    ///
    /// DEPLOYMENT IS TWO QUESTIONS, NOT ONE
    ///
    /// Before Ready is pressed, Bannerlord gives the player a free-flying camera
    /// to place their army with. There is no player agent yet, nothing to anchor
    /// a head to, and no first person to be in - so every VR LOOK INJECTION in
    /// that phase is a write on top of a camera the player is actively steering
    /// somewhere else. That is what drifted and then flew.
    ///
    /// The answer used to be to stand the whole mod down for the phase and show
    /// the game's own window on the flat screen. It does not work, and it cannot:
    /// the eye texture is bound to the engine's scene view at
    /// OnSceneRenderingStarted, which fires BEFORE deployment begins. Stand the
    /// renderer down after that and the world goes on being drawn into a texture
    /// nobody submits, while the window keeps whatever it last drew - the loading
    /// screen. Deployment showed a frozen picture with the formation cards on top
    /// of it, on the monitor and in the headset alike.
    ///
    /// So the two halves are separated. RENDERING stays on: deployment is a real
    /// battlefield seen from a real position, and of all the parts of a battle it
    /// is the one that is purely about reading terrain - the phase that gains
    /// most from being stood in. INJECTION stands down: the head moves the view
    /// and nothing else, and the camera stays the free-flying one the game builds
    /// from the stick and the mouse. See VrDrivesLook.
    /// </summary>
    public static class VrMissionPhase
    {
        /// <summary>
        /// 1 restores the old stand-down: no stereo during deployment, the game's
        /// own window on the flat screen.
        ///
        /// Kept as an escape hatch rather than deleted, but OFF - it never
        /// delivered the vanilla view it promised. The scene view is retargeted
        /// before deployment starts, so "hands off" only meant the flat screen
        /// was showing a window the world had stopped being drawn into.
        /// </summary>
        private static readonly bool VanillaDeployment =
            VrConfig.Bool("vanilla_deployment", false);

        /// <summary>
        /// Whether VR owns the mission camera this frame - that is, whether the
        /// stereo path is rendering at all.
        ///
        /// True for the whole of a mission INCLUDING deployment, and false only
        /// when there is no mission. Everything that decides whether the headset
        /// gets a world is gated on this.
        /// </summary>
        public static bool VrOwnsCamera { get; private set; }

        /// <summary>
        /// Whether VR may write the game's own LOOK - inject head movement into
        /// the camera deltas, force first person, steer the agent's aim.
        ///
        /// WHY THIS IS NOT VrOwnsCamera
        ///
        /// Rendering and steering are separate permissions, and deployment wants
        /// exactly one of them. The player is flying the camera themselves; head
        /// movement added to _cameraBearingDelta on top of that is a second hand
        /// on the stick, and because the movement axis flies along the camera's
        /// own heading, a camera that rotates by itself is a camera that departs
        /// by itself. That is the drift-then-fly, and it belongs to injection
        /// rather than to rendering.
        ///
        /// With this false the head still turns the VIEW - the anchor is the
        /// engine camera's own heading and the head pose composes on top of it,
        /// which is free head-look over a camera the game owns outright.
        ///
        /// FALSE WITH NO MAIN AGENT TOO (TEST 397). After the player dies the game
        /// hands over its free camera, the same one deployment flies. Left true,
        /// the anchor cut froze the heading (nothing integrates BodyHeading without
        /// an agent, so the right stick turned the culling but not the picture)
        /// and the restore that stops the origin feeding back never ran, so the
        /// camera drifted on every frame it moved.
        /// </summary>
        public static bool VrDrivesLook { get; private set; }

        /// <summary>
        /// Whether the game's free camera should fly the deployment way - planar,
        /// faster with height, stick without the trigger - which is deployment
        /// itself and the free camera after the player dies (TEST 397). Read by
        /// FreeCameraFlightPatch and VrGamepad.
        /// </summary>
        public static bool FlyLikeDeployment { get; private set; }

        /// <summary>The pre-battle formation phase (MissionMode.Deployment). No
        /// hands or weapons are posed in it and the laser is on (TEST 570).</summary>
        public static bool Deploying { get; private set; }

        /// <summary>The order menu is open. Its move-to-flag is confirmed by the
        /// left mouse or the right trigger (OrderTroopPlacer), so VrHolster gives
        /// the trigger back to the game meanwhile (TEST 571).</summary>
        public static bool OrderMenuOpen { get; private set; }

        /// <summary>
        /// Whether a user interface is open that the player needs to READ.
        ///
        /// WHY THE HEADSET SWITCHES TO A SCREEN FOR THESE
        ///
        /// Bannerlord draws its interface into the game's own swapchain, not into
        /// anything the stereo path touches, so inside a mission none of it ever
        /// reached the headset - the world was there and the inventory, the
        /// escape menu and every conversation were not.
        ///
        /// It cannot simply be composited over the stereo world either, because
        /// of what AFR does to get the world in the first place: VrAfrRenderer
        /// retargets the engine's own scene view at our eye texture
        /// (SetRenderTarget(ColorTarget(0))), so during a mission the backbuffer
        /// holds the interface with NO 3D behind it. There is nothing to blend it
        /// against and nothing lost by replacing the view with it.
        ///
        /// So a UI moment is treated exactly like the campaign map: the same flat
        /// screen, in the same place, by the same mechanism. Which also means it
        /// is readable at the panel's own resolution rather than through whatever
        /// the eye targets are scaled to.
        ///
        /// This deliberately does NOT cover the combat HUD - the health bar, the
        /// ammo count, the kill feed. Those want to be visible WHILE fighting,
        /// which means compositing rather than replacing, and compositing needs
        /// the interface on a texture of its own with real transparency. That is
        /// a different mechanism and it is not this one.
        /// </summary>
        public static bool UiWantsScreen { get; private set; }

        /// <summary>
        /// Whether a menu wants to float OVER the world, keyed, rather than
        /// replace it.
        ///
        /// TWO KINDS OF MENU, AND THEY WANT OPPOSITE THINGS
        ///
        /// The tactics wheel and the weapon wheel are used mid-fight and are
        /// meaningless without the field behind them, so they are keyed and
        /// composited over the stereo world.
        ///
        /// The escape menu, the pause menu and the scoreboard are the opposite:
        /// they are read, not used, and reading them through a transparency laid
        /// over a moving battle is harder than reading them on a plain panel.
        /// Those get the flat screen - opaque, at the panel's own resolution,
        /// with the world out of the way.
        ///
        /// So this is not "is a menu open" but "which of the two". Both cannot be
        /// true at once, and UiWantsScreen wins when they collide: if something
        /// has been opened to be read, the wheel underneath it is not the thing
        /// being looked at.
        /// </summary>
        public static bool UiWantsOverlay { get; private set; }

        /// <summary>
        /// Whether a menu that is READ is open - whatever it ends up drawn on.
        ///
        /// WHY THIS IS NOT UiWantsScreen
        ///
        /// It was, and that is how the pointer died in the pause menu. Classify
        /// works out that a read-menu is up, and then the panel swap at the end
        /// of it turns wantsScreen back OFF inside a mission: the flat panel
        /// shows the game WINDOW, and the window is not being drawn while the
        /// stereo path owns the frame. The menu is still open. It is floating
        /// over the world instead of replacing it.
        ///
        /// So everything that asked "is a menu open" was reading the answer to a
        /// different question - "is the flat panel up" - and got false for every
        /// menu opened during a battle. The stick pointer never activated and
        /// held directions never repeated, in the one place a menu is hardest to
        /// work any other way.
        ///
        /// This is the menu question, asked once, with no opinion about how the
        /// menu reaches the headset.
        /// </summary>
        public static bool UiMenuOpen { get; private set; }

        private static bool _wasMounted;
        private static bool _haveMountState;
        private static DateTime? _dismountAt;
        private static readonly float DismountRecentreDelay =
            Math.Max(0f, VrConfig.Float("dismount_recentre_delay_s", 1.5f));
        private static bool _loggedDeployment;
        private static bool _loggedHandback;

        /// <summary>
        /// Called once per frame from the application tick - which keeps running
        /// during deployment and between missions, unlike anything hung off the
        /// mission camera.
        /// </summary>
        public static void Tick()
        {
            try
            {
                Mission mission = Mission.Current;
                if (mission == null)
                {
                    // The stereo campaign map renders like a battle - mirror, keyed
                    // interface over the world - but never has its look driven.
                    bool map = VrMapStereo.Live;
                    VrOwnsCamera = map;
                    VrDrivesLook = false;
                    FlyLikeDeployment = false;
                    Deploying = false;
                    OrderMenuOpen = false;
                    UiWantsScreen = false;
                    UiWantsOverlay = map;
                    UiMenuOpen = false;
                    _haveMountState = false;
                    return;
                }

                bool deployment = mission.Mode == MissionMode.Deployment;
                Deploying = deployment;
                OrderMenuOpen = mission.CurrentState == Mission.State.Continuing
                    && ScreenManager.TopScreen is MissionScreen orderScreen && ReadOrderMenu(orderScreen);

                // The old stand-down, kept behind a switch. It takes the stereo
                // path away as well, which is what made deployment a frozen
                // picture; see the class note.
                bool standDown = VanillaDeployment && deployment;

                if (deployment && !_loggedDeployment)
                {
                    _loggedDeployment = true;
                    VrLog.Info(standDown
                        ? "Deployment: VR has stood down completely (vanilla_deployment = 1). "
                          + "Expect a frozen picture - the engine's scene view is already "
                          + "bound to the eye texture by this point, so nothing is drawing "
                          + "into the window the flat screen shows. Set it back to 0."
                        : "Deployment in VR: the battlefield is rendered in stereo and you "
                          + "look around it with your head. The camera itself is the game's "
                          + "own free-flying one - VR injects no look input until Ready is "
                          + "pressed, so flying it behaves exactly as it does without the "
                          + "mod. vanilla_deployment = 1 stands the whole path down again.");
                }

                if (!deployment && _loggedDeployment && !_loggedHandback)
                {
                    _loggedHandback = true;
                    VrLog.Info("Deployment over; VR has the look as well as the camera.");
                }

                VrOwnsCamera = !standDown;

                // The one permission deployment does not get. Everything that
                // WRITES the game's look reads this rather than VrOwnsCamera, so
                // the free-flying camera stays entirely the player's.
                bool noAgent = mission.MainAgent == null;
                VrDrivesLook = VrOwnsCamera && !deployment && !noAgent;
                FlyLikeDeployment = VrOwnsCamera
                    && (deployment || (mission.Mode == MissionMode.Battle && noAgent));

                bool wantsOverlay = false;
                bool wantsScreen = false;
                bool menuOpen = false;

                if (!standDown)
                    Classify(mission, out wantsScreen, out wantsOverlay, out menuOpen);

                UiWantsScreen = wantsScreen;
                UiMenuOpen = menuOpen;

                // Opaque wins. A wheel under an escape menu is not what is being
                // looked at, and submitting both would key one over the other.
                UiWantsOverlay = wantsOverlay && !wantsScreen;

                TrackMount(mission.MainAgent);
            }
            catch (Exception ex)
            {
                // Leave whatever it last decided rather than flipping the whole
                // camera architecture over on a transient read failure.
                VrLog.Error("Mission phase check failed; the camera stays on whichever "
                            + "path it was last on.", ex);
            }
        }

        /// <summary>
        /// 0 keeps the headset on the stereo world through every menu, which is
        /// what it did before - the interface stays on the monitor.
        /// </summary>
        private static readonly bool UiOnScreen = VrConfig.Bool("ui_screen", true);

        /// <summary>
        /// Show the game's interface over the world for the whole mission rather
        /// than only for menus we managed to recognise.
        ///
        /// On, because recognising them one at a time kept missing things - the
        /// scoreboard most recently. 0 goes back to showing only the wheels and
        /// the order menu, which is quieter in the corner of the eye but leaves
        /// the health bar, the kill feed and anything unrecognised invisible.
        /// </summary>
        private static readonly bool OverlayAlways = VrConfig.Bool("ui_overlay_always", true);

        /// <summary>
        /// Talk to people in the world rather than on a screen.
        ///
        /// 0 puts conversation and barter back on the flat panel, which is
        /// easier to read and is not being in the room. Worth having as a switch
        /// because long trade menus are genuinely a reading task, and someone
        /// may prefer the panel for those.
        /// </summary>
        private static readonly bool ConversationInVr =
            VrConfig.Bool("conversation_vr", true);

        private static bool _loggedUi;

        // IsOrderMenuOpen and IsTransferMenuOpen are not public, so they go
        // through their getters - bound ONCE into a delegate rather than
        // reflected per frame, and left null if a game update moves them. A
        // missing one costs the order menu its screen; it does not cost the
        // player their mission.
        /// <summary>
        /// Is any interface LAYER up inside the mission screen?
        ///
        /// THE ESCAPE MENU AND THE SCOREBOARD ARE NOT SCREENS
        ///
        /// The earlier test asked whether something had been pushed on top of the
        /// mission, which catches the inventory and the character sheet because
        /// those really are separate screens. The escape menu, the pause menu and
        /// the scoreboard are not: they are Gauntlet LAYERS added to the mission
        /// screen itself, so the top screen is still the mission and the test saw
        /// nothing. That is why they never reached the headset.
        ///
        /// Rather than name them - and miss the next one, and every one a mod
        /// adds - this asks what a layer is DOING. A layer that has asked for the
        /// cursor, or taken input focus, is a layer the player is meant to be
        /// reading. The scene layer is excluded because that is the game itself:
        /// it holds focus for the whole of normal play.
        /// </summary>
        private static bool _loggedPanelSwap;

        /// <summary>
        /// Whether the flat panel can show anything current.
        ///
        /// It mirrors the game WINDOW, and the window stops being drawn once the
        /// stereo path takes over - so inside a mission the panel is a still frame
        /// however correct the menu on top of it is. Outside one the window is
        /// live and the panel is exactly right.
        /// </summary>
        private static bool ScreenPanelIsLive =>
            !VrOwnsCamera || VrConfig.Bool("ui_screen_in_mission", false);

        private static string _reportedLayer;

        /// <summary>
        /// Names the layer that put the world on a panel, once per distinct layer.
        ///
        /// This test is "is something being READ rather than used", and it answers
        /// it by asking whether a layer takes focus or wants the mouse. If some
        /// layer that is up for the WHOLE battle answers yes, the flat screen
        /// latches on for the whole battle - and because AFR draws the world into
        /// the eye texture rather than the swapchain, the panel then shows the
        /// last thing the game window happened to draw. That is the custom battle
        /// menu frozen in front of the player, which is exactly how this was
        /// reported.
        ///
        /// Which layer it is cannot be reasoned out from here - the wheels, the
        /// escape menu and the battle HUD are all layers on the same screen and
        /// only differ in flags that are set at runtime. So it says which one,
        /// rather than being guessed at.
        /// </summary>
        private static void ReportLayer(ScreenLayer layer, string why)
        {
            string name;
            try
            {
                name = layer.Name;
            }
            catch
            {
                name = "<unreadable>";
            }

            if (string.Equals(name, _reportedLayer, StringComparison.Ordinal))
                return;

            _reportedLayer = name;
            VrLog.Info("Flat screen: the world was replaced by a panel because layer '"
                       + name + "' is up and " + why + ". If this is a layer that stays "
                       + "up for the whole battle, that is the bug - the panel is meant "
                       + "for menus that are read, not for the fight.");
        }

        private static bool AnyUiLayerIsUp(MissionScreen screen)
        {
            try
            {
                ScreenBase top = ScreenManager.TopScreen;
                if (top == null)
                    return false;

                ScreenLayer scene = screen == null ? null : screen.SceneLayer;

                foreach (ScreenLayer layer in top.Layers)
                {
                    // The two deployment order layers both request the mouse
                    // while the camera is idle; neither is a separate menu.
                    // VrCursorLayer only frees the mouse for the VR panel.
                    if (layer == null || ReferenceEquals(layer, scene) || !layer.IsActive
                        || layer is VrCursorLayer
                        || (Deploying && layer is TaleWorlds.Engine.GauntletUI.GauntletLayer g
                            && (VrLaser.IsDeploymentLayer(g.Name) || g.Name == "MissionOrder")))
                        continue;

                    if (layer.IsFocusLayer)
                    {
                        ReportLayer(layer, "it is a focus layer");
                        return true;
                    }

                    InputRestrictions restrictions = layer.InputRestrictions;
                    if (restrictions != null && restrictions.MouseVisibility)
                    {
                        ReportLayer(layer, "it asks for the mouse cursor");
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                // A layer list read badly is not worth taking the mission down
                // over; the other tests still cover the separate screens.
                return false;
            }
        }

        private static Func<MissionScreen, bool> _orderMenu;
        private static Func<MissionScreen, bool> _transferMenu;
        private static bool _uiResolved;
        private static bool _warnedUiMembers;

        private static void ResolveUiMembers()
        {
            if (_uiResolved)
                return;
            _uiResolved = true;

            _orderMenu = BindBool("IsOrderMenuOpen");
            _transferMenu = BindBool("IsTransferMenuOpen");

            if ((_orderMenu == null || _transferMenu == null) && !_warnedUiMembers)
            {
                _warnedUiMembers = true;
                VrLog.Warn("The order/transfer menu flags are not where they were, so those "
                           + "two will not raise the flat screen. Every other menu is "
                           + "unaffected.");
            }
        }

        private static Func<MissionScreen, bool> BindBool(string property)
        {
            try
            {
                MethodInfo getter = AccessTools.PropertyGetter(typeof(MissionScreen), property);
                if (getter == null)
                    return null;

                return (Func<MissionScreen, bool>)Delegate.CreateDelegate(
                    typeof(Func<MissionScreen, bool>), getter);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Both of these read engine state that can be half torn down, so a
        /// throw is a legitimate answer rather than a fault - it means "no menu",
        /// and it means it silently. The state guard in Classify covers the
        /// common case; this covers the frame it lands on the boundary.
        /// </summary>
        private static bool ReadOrderMenu(MissionScreen screen)
        {
            ResolveUiMembers();

            try
            {
                return _orderMenu != null && _orderMenu(screen);
            }
            catch
            {
                return false;
            }
        }

        private static bool ReadTransferMenu(MissionScreen screen)
        {
            ResolveUiMembers();

            try
            {
                return _transferMenu != null && _transferMenu(screen);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Is something open that wants reading?
        ///
        /// TWO KINDS, AND BOTH ARE NEEDED
        ///
        /// A separate SCREEN pushed over the mission - inventory, character,
        /// party, the escape menu - shows up as ScreenManager.TopScreen no longer
        /// being a MissionScreen. That one test covers every such screen there is
        /// and every one a mod might add, without naming any of them.
        ///
        /// The rest are drawn INSIDE the mission screen, so the top screen is
        /// still the mission and only the mission knows: the order menu, the
        /// transfer and radial menus, conversations and barter. Those have to be
        /// asked for individually, and they are the ones most likely to change
        /// between game versions - which is why each is a separate cheap test
        /// rather than one clever condition.
        /// </summary>
        /// <summary>
        /// Sorts whatever is open into the two treatments. See UiWantsOverlay.
        /// </summary>
        private static void Classify(Mission mission, out bool wantsScreen,
                                     out bool wantsOverlay, out bool menuOpen)
        {
            wantsScreen = false;
            wantsOverlay = false;
            menuOpen = false;

            if (!UiOnScreen)
                return;

            // A MISSION THAT IS ENDING IS NOT A MISSION TO ASK QUESTIONS OF.
            //
            // Mission.Current stays non-null through teardown while its insides
            // are being dismantled, and the screen's own properties go with it.
            // IsOrderMenuOpen threw a NullReferenceException on every frame of
            // that window - caught, so nothing broke, but logged thirty times a
            // second into the log somebody would later be reading to find out
            // why the session ended.
            //
            // Continuing is the state the classification means anything in.
            if (mission.CurrentState != Mission.State.Continuing)
                return;

            MissionScreen screen = ScreenManager.TopScreen as MissionScreen;

            // --- used while playing: keyed, over the world --------------------
            //
            // ALWAYS ON, and that is what finally fixed the scoreboard.
            //
            // Detecting individual menus was a losing game. The wheels have a
            // flag, the escape menu is a layer that takes focus, and the
            // scoreboard has neither - no view type of its own to test for, no
            // cursor requested, no focus taken. Every version of "is THIS open"
            // missed something, and would have gone on missing whatever came
            // next.
            //
            // But the interface is already keyed every mission frame, because
            // the monitor mirror needs it. Submitting that keyed layer
            // unconditionally shows whatever the game happens to be drawing -
            // the scoreboard, the health bar, the kill feed, a mod's own panel -
            // and asks no questions about what any of it is. The thing that was
            // hard to detect turns out not to need detecting.
            //
            // The read-menus below still override this, because those want to
            // replace the view rather than float over it.
            // The panel's master switch. Deliberately NOT applied to wantsScreen
            // below: hiding the interface must not lock the player out of the
            // escape menu, which is how they leave the battle.
            wantsOverlay = VrUiSettings.Show
                        && (OverlayAlways
                        || (screen != null && (screen.IsRadialMenuActive || ReadOrderMenu(screen))));

            // --- read, not used: opaque, instead of the world -----------------

            // CONVERSATION STAYS IN THE WORLD.
            //
            // It used to be treated as something to read, which put it on the
            // flat screen and took the world away - you stood in a void holding
            // a dialogue box. That is the wrong call for the one screen where
            // the thing being talked ABOUT is standing in front of you.
            //
            // On the overlay path instead: the stereo world keeps rendering, the
            // dialogue is keyed and composited over it exactly as the tactics
            // wheel is, and you look at the person you are talking to by looking
            // at them. The camera is yours throughout - the game's conversation
            // framing is simply not used, which is what being there instead of
            // watching it means.
            //
            // Barter goes with it. Same screen, same reason.
            bool talking = mission.Mode == MissionMode.Conversation
                        || mission.Mode == MissionMode.Barter;

            if (talking && ConversationInVr)
            {
                // THE DIALOGUE IS ALWAYS SHOWN, whatever the interface settings
                // say. Every other panel in the game can be hidden and the
                // player carries on playing; this one is the only way OUT of a
                // conversation, and a hidden dialogue is a soft lock in a
                // headset. Overriding both ui_show and ui_overlay_always here is
                // narrower than making either of them lie about what they do.
                wantsOverlay = true;

                // AND IT IS A MENU, whatever the panel does with it.
                //
                // menuOpen is set here rather than left to the assignment at
                // the end of Classify, which this return skips. That one reads
                // menuOpen = wantsScreen - "a menu is open" taken from "the
                // flat panel is up" - and the two came apart the moment
                // conversation moved onto the overlay: the dialogue went to
                // the world, wantsScreen stayed false, and the mod believed no
                // menu was open for the whole conversation.
                //
                // What that cost is the POINTER. VrStickMouse.InMenu and
                // VrGamepad.InMenu both ask this property, so the stick mouse
                // never took hold of the cursor, the pad lost its priority to
                // the idle timer while the player was reading, and held
                // directions lost their repeat - in the one screen that is
                // nothing but a list of lines to click.
                menuOpen = true;

                // AND NOTHING BELOW GETS A SAY.
                //
                // Returning rather than setting a flag, because every test that
                // follows would otherwise drag the dialogue back onto the panel:
                // it is a Gauntlet layer that takes focus and asks for the
                // cursor, so AnyUiLayerIsUp matches it exactly as it matches the
                // escape menu. Excluding it by mode is the only honest way to
                // say "this one is different"; the layer cannot tell us.
                return;
            }

            if (talking)
                wantsScreen = true;

            // Anything pushed on top of the mission - inventory, character,
            // party. Deliberately a type test rather than a list of names.
            if (!wantsScreen && !(ScreenManager.TopScreen is MissionScreen))
                wantsScreen = true;

            if (!wantsScreen && screen != null)
            {
                // The escape menu and the pause menu are LAYERS on the mission
                // screen rather than screens of their own, which is why naming
                // things does not find them - see AnyUiLayerIsUp.
                //
                // The wheels are layers too and would match that test as well,
                // so they are excluded explicitly. Note this asks about the
                // WHEELS rather than about wantsOverlay: the overlay is on for
                // the whole mission now, and testing it here would exclude
                // everything and leave the escape menu floating over the battle.
                bool wheelOpen = screen.IsRadialMenuActive || ReadOrderMenu(screen);

                wantsScreen = screen.IsViewingCharacter()
                           || ReadTransferMenu(screen)
                           || (!wheelOpen && AnyUiLayerIsUp(screen));
            }

            // A PANEL SHOWING THE GAME WINDOW IS ONLY WORTH ANYTHING WHILE THE
            // WINDOW IS STILL BEING DRAWN, AND IN A MISSION IT IS NOT.
            //
            // The panel exists because a menu that is READ is easier to read on
            // an opaque surface than through a transparency over a moving battle.
            // That reasoning is sound and it is not what is wrong here.
            //
            // What is wrong is the source. The panel shows the game's own window,
            // and once the stereo path is live AFR renders the world into the eye
            // texture rather than the swapchain - so the window keeps whatever it
            // last drew. Opening the escape menu mid-battle therefore replaces the
            // world with a FROZEN picture of the world, with the menu on top: the
            // "last UI that appeared", parked in front of the player. The battle
            // is still running behind it and cannot be seen.
            //
            // So in a mission the menu goes over the live stereo world instead.
            // Reading through a transparency is a real cost and the note above is
            // right about it - but it is a smaller cost than reading it over a
            // still frame of a fight that is still happening.
            //
            // Outside a mission nothing changes: the window is genuinely live
            // there, which is the case the panel was designed for and is still
            // the right answer for the main menu and the campaign map.
            // TAKEN BEFORE THE SWAP BELOW, which is the whole reason there are
            // two flags. From here down wantsScreen means "put it on the panel",
            // and in a mission the answer to that is no while the answer to "is
            // a menu open" is still yes.
            menuOpen = wantsScreen;

            if (wantsScreen && !ScreenPanelIsLive)
            {
                wantsScreen = false;
                wantsOverlay = VrUiSettings.Show;

                if (!_loggedPanelSwap)
                {
                    _loggedPanelSwap = true;
                    VrLog.Info("Menus opened during a mission now float over the world "
                               + "instead of replacing it. The panel shows the game's "
                               + "window, and while the stereo path is live the window is "
                               + "not being drawn - AFR renders into the eye texture - so "
                               + "the panel could only ever show a frozen frame with the "
                               + "menu on top. ui_screen_in_mission = 1 restores it.");
                }
            }

            if ((wantsScreen || wantsOverlay) && !_loggedUi)
            {
                _loggedUi = true;
                VrLog.Info("The interface is reaching the headset. It is keyed and drawn "
                           + "OVER the world for the whole mission - the wheels, the health "
                           + "bar, the scoreboard, anything the game puts up - because "
                           + "recognising menus one at a time kept missing things and the "
                           + "keyed layer is built every frame anyway for the monitor. The "
                           + "escape menu and the pause menu replace the view instead, on a "
                           + "plain panel: those are read rather than used, and reading "
                           + "through a transparency over a moving battle is worse. "
                           + "ui_overlay_always = 0 shows only the wheels; ui_screen = 0 "
                           + "shows neither.");
            }
        }

        /// <summary>
        /// Recentres when the player gets off a horse.
        ///
        /// Mounted, the character faces wherever the animal does, and the anchor's
        /// heading is the animal's heading. Step off it and the body is left
        /// pointing along the horse's last direction while the player is already
        /// looking somewhere else - so "forward" for the character and "forward"
        /// for the player disagree by whatever that angle happened to be.
        ///
        /// A recentre folds the current head yaw into the anchor, which is exactly
        /// "make the character face where I am looking". Fired on the transition
        /// rather than while dismounted, so it happens once per dismount instead
        /// of fighting the player every frame afterwards.
        /// </summary>
        private static void TrackMount(Agent agent)
        {
            if (agent == null || !agent.IsActive())
            {
                _haveMountState = false;
                return;
            }

            bool mounted = agent.HasMount;

            // TEST 292. Not on the transition frame: that is mid-dismount, and the
            // view came out high until F10 was pressed later. The recentre waits
            // out the dismount and then runs once, the same as the key.
            if (_haveMountState && _wasMounted && !mounted)
                _dismountAt = DateTime.UtcNow;

            if (mounted)
                _dismountAt = null;
            else if (_dismountAt.HasValue
                     && (DateTime.UtcNow - _dismountAt.Value).TotalSeconds >= DismountRecentreDelay)
            {
                _dismountAt = null;

                // TEST 449: under the head-driven body the dismount rides the
                // animation and glides onto the standing body (VrHeadBody.Saddled,
                // the anchor seam), and the body turns after the head on its own -
                // the recentre would only snap the view's yaw, which mounting never does.
                if (VrHeadBody.HoldsDismount)
                {
                    VrLog.Info("TEST 449: dismounted; no TEST 292 recentre - the hand-over glides the view "
                               + "onto the standing body instead (dismount_hold = 0 brings it back).");
                }
                else
                {
                    VrCameraDriver.RequestRecentre(keepHeight: true);
                    VrLog.Info(string.Format(
                        "TEST 292: dismounted {0:F1} s ago; recentred so the view sits on the body and "
                        + "the character faces where you are looking rather than where the horse was "
                        + "pointing.", DismountRecentreDelay));
                }
            }

            _wasMounted = mounted;
            _haveMountState = true;
        }

        public static void Reset()
        {
            _haveMountState = false;
            _wasMounted = false;
            _dismountAt = null;
            _loggedDeployment = false;
            _loggedHandback = false;
        }
    }
}
