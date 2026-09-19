using System;
using System.Globalization;
using BannerlordVR.Interop;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Whether the game's interface is shown in the headset, and how big it hangs.
    ///
    /// WHY THESE ARE HERE AND NOT JUST CONFIG KEYS
    ///
    /// Because they are judged by looking, not by reading. How much of the view a
    /// panel should cover, and how far away it sits before text stops being
    /// comfortable, are questions with no right answer written down anywhere -
    /// they depend on the headset, the render scale and the player's eyes. A
    /// config key means edit, restart, look, repeat; a slider means look while
    /// moving it. World scale is in the panel for exactly the same reason.
    ///
    /// The values live in the NATIVE layer, because that is where the quad is
    /// built. This class is the managed half: it holds the current answer, sends
    /// changes across, and writes them to the config so the next session starts
    /// where this one left off.
    /// </summary>
    public static class VrUiSettings
    {
        /// <summary>
        /// Whether the keyed interface layer is submitted at all.
        ///
        /// Separate from ui_overlay_always, which decides how MUCH is shown - the
        /// whole interface or only the wheels. This is the master switch, and it
        /// deliberately does not affect the escape menu: that replaces the view
        /// on its own panel rather than going through this layer, so turning the
        /// interface off does not lock the player out of the game's own menu.
        /// </summary>
        public static bool Show { get; private set; } = VrConfig.Bool("ui_show", true);

        public static float Width { get; private set; } =
            Clamp(VrConfig.Float("ui_overlay_width", 2.2f), 0.2f, 20f);

        public static float Distance { get; private set; } =
            Clamp(VrConfig.Float("ui_overlay_distance", 1.6f), 0.3f, 20f);

        /// <summary>The FLAT SCREEN - the panel the main menu, the campaign map
        /// and the pause menus appear on. Its own numbers, because it is a
        /// screen across the room rather than a layer on the view.</summary>
        public static float ScreenWidth { get; private set; } =
            Clamp(VrConfig.Float("flat_screen_width", 2.0f), 0.2f, 20f);

        public static float ScreenDistance { get; private set; } =
            Clamp(VrConfig.Float("flat_screen_distance", 2.0f), 0.3f, 20f);

        private static bool _pushed;
        private static bool _failed;

        /// <summary>
        /// Hands the current geometry to the native layer.
        ///
        /// Called once on the first tick as well as on every change, because the
        /// native side starts from its OWN config read - and if the panel or a
        /// later edit ever disagreed with that, the quad would be built from one
        /// answer while the slider showed the other.
        /// </summary>
        public static void PushGeometry()
        {
            if (_failed)
                return;

            try
            {
                NativeMethods.bvr_set_ui_geometry(Width, Distance);
                NativeMethods.bvr_set_screen_geometry(ScreenWidth, ScreenDistance);
                _pushed = true;
            }
            catch (Exception ex)
            {
                _failed = true;
                VrLog.Error("Could not publish the interface geometry; it keeps whatever "
                            + "size the config gave it.", ex);
            }
        }

        public static void EnsurePushed()
        {
            if (!_pushed)
                PushGeometry();
        }

        public static void SetShow(bool show)
        {
            if (Show == show)
                return;

            Show = show;
            VrConfig.Save("ui_show", show ? "1" : "0");
            VrLog.Info("VR panel: the game's interface is now "
                       + (show ? "shown" : "hidden") + " in the headset, saved.");
        }

        public static void SetGeometry(float width, float distance)
        {
            width = Clamp(width, 0.2f, 20f);
            distance = Clamp(distance, 0.3f, 20f);

            bool moved = Math.Abs(width - Width) > 1e-3f
                      || Math.Abs(distance - Distance) > 1e-3f;

            if (!moved)
                return;

            Width = width;
            Distance = distance;

            PushGeometry();

            VrConfig.Save("ui_overlay_width", Format(Width));
            VrConfig.Save("ui_overlay_distance", Format(Distance));
        }

        public static void SetScreenGeometry(float width, float distance)
        {
            width = Clamp(width, 0.2f, 20f);
            distance = Clamp(distance, 0.3f, 20f);

            bool moved = Math.Abs(width - ScreenWidth) > 1e-3f
                      || Math.Abs(distance - ScreenDistance) > 1e-3f;

            if (!moved)
                return;

            ScreenWidth = width;
            ScreenDistance = distance;

            PushGeometry();

            VrConfig.Save("flat_screen_width", Format(ScreenWidth));
            VrConfig.Save("flat_screen_distance", Format(ScreenDistance));
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static string Format(float v)
        {
            return v.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
