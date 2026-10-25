using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BannerlordVR.Interop;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Tells the native layer whether to show the stereo world or the flat screen.
    ///
    /// WHAT THE FLAT SCREEN IS FOR
    ///
    /// Everything Bannerlord draws that is not a mission - the main menu, the
    /// campaign map, the inventory, the party and encounter screens, loading -
    /// is a 2D image in the game's own swapchain. None of it goes near the
    /// stereo path, so the headset had nothing to show and displayed a cleared
    /// colour while the game was perfectly playable on the monitor.
    ///
    /// The native side answers that by putting the game's own backbuffer on a
    /// quad layer, at a fixed distance and its own aspect ratio. This class is
    /// only the switch: it decides, once per frame, which of the two the headset
    /// should be looking at.
    ///
    /// WHY A MISSION IS THE TEST, AND NOT THE MISSION SCREEN
    ///
    /// Mission.Current is non-null for the whole life of a battle including the
    /// deployment phase, which is exactly the span the stereo path is set up
    /// for. Testing for a MissionScreen instead would be a test of whether the
    /// UI exists rather than whether there is a world to render, and it goes
    /// true a little before the scene does.
    ///
    /// The state is published on CHANGE rather than every frame. It crosses the
    /// P/Invoke boundary into an atomic the render thread reads, and a battle is
    /// tens of thousands of frames long; there is no reason to make that call
    /// ninety times a second to write the same value.
    /// </summary>
    public static class VrFlatScreen
    {
        /// <summary>
        /// 0 leaves the headset on the stereo path everywhere, which is the
        /// behaviour before the flat screen existed - menus go back to being a
        /// blank colour in the headset and readable only on the monitor.
        /// </summary>
        public static bool Enabled { get; } = VrConfig.Bool("flat_screen", true);

        /// <summary>Tri-state deliberately: -1 means nothing has been published
        /// yet, so the first tick always writes whichever way it lands rather
        /// than assuming the native default matches.</summary>
        private static int _published = -1;

        /// <summary>Same tri-state as _published, for the keyed overlay.</summary>
        private static int _publishedOverlay = -1;

        /// <summary>Same tri-state, for "there is a battle".</summary>
        private static int _publishedInMission = -1;

        private static bool _failed;
        private static bool _loggedFirst;

        /// <summary>
        /// Puts the screen back in front of the head, wherever that is now.
        ///
        /// Wired to recentre, because "put things back in front of me" is what
        /// recentre means and outside a mission the screen is the only thing
        /// there is to put. Safe to call when there is no screen: the native
        /// side only drops the pin, and the next frame that needs one places it.
        /// </summary>
        public static void RequestRepin()
        {
            if (_failed || !Enabled)
                return;

            try
            {
                NativeMethods.bvr_repin_flat_screen();
            }
            catch (Exception ex)
            {
                // Deliberately NOT _failed: this is a convenience, and losing it
                // is no reason to stop publishing the mission state, which is
                // what actually decides whether menus are visible at all.
                VrLog.Error("Could not re-pin the flat screen.", ex);
            }
        }

        /// <summary>
        /// TEST 592: every new screen opens in front of the head. The flat screen
        /// and the pinned interface were each placed once and then stayed in the
        /// room, so a town menu, the inventory or the battle results could come up
        /// off to the side or behind. A new screen is a change of the top screen,
        /// of the campaign's open game menu, or of the topmost focus layer (a
        /// popup, a conversation, the results); each drops both pins and native
        /// places them again ahead of where the head looks now.
        /// </summary>
        internal static bool RecenterScreens = VrConfig.Bool("ui_recenter_new_screen", true);

        /// <summary>TEST 710/711: a new screen re-centres only when the head is turned
        /// more than this many degrees left or right of it. The VR panel's slider.</summary>
        internal static float RecenterAngle = VrConfig.Float("ui_recenter_angle", 75f);

        private static string _screenKey;
        private static bool _recenterBroken;

        /// <summary>TEST 593: focus layers already seen on this screen and menu. In
        /// the smithy the focus flips between the crafting layer and its 3D weapon
        /// view as the pointer crosses the middle - 30 re-placements a minute. Only a
        /// focus layer this screen has not shown yet (a popup, the results) counts.</summary>
        private static readonly HashSet<string> SeenFocus = new HashSet<string>();

        /// <summary>TEST 740: the limit native last got. Native also applies it to the
        /// screens it re-shows by itself (leaving a battle, a menu over the world, the
        /// VR panel), so it is pushed from the start and on every change; 180 when
        /// recentring is off means "never turned far enough".</summary>
        private static float _pushedLimit = -1f;

        private static void RecenterOnNewScreen()
        {
            if (_recenterBroken || !Enabled)
                return;

            try
            {
                float limit = RecenterScreens ? RecenterAngle : 180f;
                if (limit != _pushedLimit)
                {
                    _pushedLimit = limit;
                    NativeMethods.bvr_repin_flat_screen_if_turned(limit);
                }
                if (!RecenterScreens)
                    return;

                string key = ScreenKey(out string focus, out bool settlement);
                bool newScreen = key != _screenKey;
                bool first = _screenKey == null;
                if (newScreen)
                {
                    _screenKey = key;
                    SeenFocus.Clear();
                }
                // TEST 625: a popup over a town's menus (recruit) is the same page.
                bool newFocus = SeenFocus.Add(focus) && !newScreen && focus != "-" && !settlement;
                if (first || (!newScreen && !newFocus))
                    return;

                // TEST 710: only if the head has turned well away from the screen.
                NativeMethods.bvr_repin_flat_screen_if_turned(RecenterAngle);
                VrLog.Info("Screen (TEST 592/710): now " + key + " focus " + focus + " - re-pinned if the head is turned away.");
            }
            catch (Exception ex)
            {
                _recenterBroken = true;
                VrLog.Error("Screen recentring failed; screens stay where they were placed.", ex);
            }
        }

        // ponytail: one small string a frame; compare fields if it ever shows in a profile.
        private static string ScreenKey(out string focus, out bool settlement)
        {
            ScreenBase top = ScreenManager.TopScreen;
            focus = "-";
            List<ScreenLayer> layers = ScreenManager.SortedLayers;
            for (int i = layers.Count - 1; i >= 0; i--)
            {
                if (layers[i] is GauntletLayer g && g.IsFocusLayer && g.IsActive && !g.IsFinalized)
                {
                    focus = g.Name;
                    break;
                }
            }
            string menu = Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId ?? "-";

            // TEST 625: a settlement's menus (town, tavern/backstreet, arena, wait)
            // are one page - keyed on the settlement, not the menu.
            string place = menu != "-" ? TaleWorlds.CampaignSystem.Party.MobileParty.MainParty?.CurrentSettlement?.StringId : null;
            settlement = place != null;
            if (settlement)
                menu = "settlement " + place;
            return (top == null ? "-" : top.GetType().Name + "#" + RuntimeHelpers.GetHashCode(top))
                   + " menu " + menu;
        }

        /// <summary>Called once per frame from the application tick, which is the
        /// only place that keeps running when there is no mission at all - which
        /// is precisely the case this exists to handle.</summary>
        public static void Tick()
        {
            if (_failed)
                return;

            RecenterOnNewScreen();

            // VrOwnsCamera rather than "is there a mission" - which for a
            // mission are now the same answer, deployment included. They were
            // not: deployment used to drop to the flat screen on the reasoning
            // that the game owned that camera. It still owns it, and the stereo
            // path renders it anyway, because standing the renderer down after
            // the scene view has been bound to the eye texture leaves the window
            // showing a frozen frame rather than the game. See VrMissionPhase.
            //
            // With the flat screen off the native side must be held on the stereo
            // path, so this still publishes - it just always publishes 1.
            // Two treatments, and which one a menu gets is decided in
            // VrMissionPhase.
            //
            //   UiWantsOverlay - the wheels. Keyed and composited OVER the
            //     world, because they are aimed at mid-fight. The stereo path
            //     stays live and only the overlay comes and goes.
            //
            //   UiWantsScreen - the escape menu, the pause menu, the scoreboard,
            //     the inventory. These REPLACE the view with a plain opaque
            //     panel, because they are read rather than used and reading
            //     through a transparency over a moving battle is worse.
            bool active = !Enabled
                || (VrMissionPhase.VrOwnsCamera && !VrMissionPhase.UiWantsScreen);

            // The keyed overlay: a menu meant to be USED while playing, floating
            // over the battle rather than taking it over.
            // "There is a battle", which the window mirror and the backbuffer
            // clear follow - they must keep running through a pause menu, or the
            // interface goes back to being drawn over a stale frame.
            int inMission = VrMissionPhase.VrOwnsCamera ? 1 : 0;
            if (inMission != _publishedInMission)
            {
                try
                {
                    NativeMethods.bvr_set_in_mission(inMission);
                    _publishedInMission = inMission;
                }
                catch (Exception ex)
                {
                    VrLog.Error("Could not publish the in-mission state.", ex);
                }
            }

            int wantOverlay = VrMissionPhase.UiWantsOverlay ? 1 : 0;
            if (wantOverlay != _publishedOverlay)
            {
                try
                {
                    NativeMethods.bvr_set_ui_overlay(wantOverlay);
                    _publishedOverlay = wantOverlay;
                }
                catch (Exception ex)
                {
                    // Not _failed: losing the overlay must not also cost the
                    // main-menu screen, which is the more important of the two.
                    VrLog.Error("Could not publish the UI overlay state.", ex);
                }
            }

            int wanted = active ? 1 : 0;
            if (wanted == _published)
                return;

            try
            {
                NativeMethods.bvr_set_mission_active(wanted);
                _published = wanted;

                if (!_loggedFirst)
                {
                    _loggedFirst = true;
                    VrLog.Info(Enabled
                        ? "Flat screen is on: outside a mission the headset shows the "
                          + "game's own image on a screen in front of you. Set "
                          + "flat_screen = 0 to leave the headset blank there instead."
                        : "Flat screen is off (flat_screen = 0); the headset stays on "
                          + "the stereo path even where there is no world to render.");
                }
            }
            catch (Exception ex)
            {
                _failed = true;
                VrLog.Error("Could not publish the mission state; the headset stays on "
                            + "whichever path it was last told about. Missions are "
                            + "unaffected.", ex);
            }
        }
    }
}
