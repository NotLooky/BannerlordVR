using System;
using BannerlordVR.Interop;
using TaleWorlds.MountAndBlade;

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

        /// <summary>Called once per frame from the application tick, which is the
        /// only place that keeps running when there is no mission at all - which
        /// is precisely the case this exists to handle.</summary>
        public static void Tick()
        {
            if (_failed)
                return;

            // VrOwnsCamera rather than "is there a mission", so deployment counts
            // as a menu: VR is not driving that camera, the game is, and the
            // headset should be showing the game's own view of it rather than a
            // stereo rendering of a camera nobody is anchored to.
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
