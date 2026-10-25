using System;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// Every world-anchored widget is placed through one of these. With ui_per_eye
    /// the camera is swapped for the per-eye panel camera. See VrUiProjection.
    /// </summary>
    [HarmonyPatch(typeof(MBWindowManager), nameof(MBWindowManager.WorldToScreen))]
    public static class WorldToScreenPatch
    {
        private static bool _loggedShift;

        /// <summary>TEST 649: project through the caller's own camera (VrLaser passes
        /// the panel camera already built). The world-page remap still applies.</summary>
        [ThreadStatic]
        internal static bool KeepCamera;

        private static void Prefix(ref Camera camera, Vec3 worldSpacePosition)
        {
            if (KeepCamera)
                return;
            camera = VrUiProjection.For() ?? camera;
            VrMapStereo.NoteLabel(worldSpacePosition);
        }

        /// <summary>
        /// TEST 299: the raw variant hands back WINDOW pixels, and every widget that
        /// uses it is then laid out inside the USABLE AREA - which TEST 283 shrank to
        /// the HUD's share of the filled panel. So the formation markers and the
        /// damage numbers sat ~(1-f)/2 of the panel right and down of their targets,
        /// and the ones near the right or bottom edge fell off the page entirely.
        ///
        /// This is the same subtraction vanilla's own WorldToScreenInsideUsableArea
        /// does, applied at the one place both raw callers go through. The
        /// decompile says there are exactly four - MissionGauntletFormationMarker,
        /// MissionAgentTakenDamageItemVM and two multiplayer duel markers - and all
        /// four are widget placement, so none of them wants window pixels.
        ///
        /// No -10000 guard: that sentinel is written by the callers AFTER this
        /// returns, never passed in. A NaN from a degenerate projection stays NaN
        /// through the subtraction, which is exactly what their validity checks
        /// already look for.
        /// </summary>
        private static void Postfix(ref float screenX, ref float screenY)
        {
            if (WorldPage.Remap(ref screenX, ref screenY) || !VrUiProjection.HudArea(out float f))
                return;

            float dx = (1f - f) * 0.5f * Screen.RealScreenResolutionWidth;
            float dy = (1f - f) * 0.5f * Screen.RealScreenResolutionHeight;

            screenX -= dx;
            screenY -= dy;

            if (!_loggedShift)
            {
                _loggedShift = true;
                VrLog.Info($"World widgets: the raw WorldToScreen is now shifted into "
                           + $"the usable area like vanilla's own variant "
                           + $"(-{dx:F0}, -{dy:F0} px at {f:P0}). Formation markers and "
                           + "damage numbers land on their targets again.");
            }
        }
    }

    [HarmonyPatch(typeof(MBWindowManager), nameof(MBWindowManager.WorldToScreenInsideUsableArea))]
    public static class WorldToScreenInsideUsableAreaPatch
    {
        private static void Prefix(ref Camera camera, Vec3 worldSpacePosition)
        {
            camera = VrUiProjection.For() ?? camera;

            // TEST 402: the map's nameplates come through here; their depth sets
            // the interface plane.
            VrMapStereo.NoteLabel(worldSpacePosition);
        }

        private static void Postfix(ref float screenX, ref float screenY) =>
            WorldPage.Remap(ref screenX, ref screenY);
    }

    /// <summary>
    /// TEST 590: the window's pixels onto the world page. The panel camera's
    /// frustum IS the world panel, and the engine spreads it over the whole window,
    /// so the result is scaled down onto the page at the window's top-left - where
    /// the world layers lay out, and against whose size vanilla clamps and culls.
    /// With the usable area left at 1 the inside-usable-area variant subtracts
    /// nothing, so all three variants take the same scale.
    /// </summary>
    internal static class WorldPage
    {
        internal static bool Remap(ref float screenX, ref float screenY)
        {
            if (!VrUiSplit.Active)
                return false;
            screenX *= VrUiSplit.WorldW / Math.Max(Screen.RealScreenResolutionWidth, 1f);
            screenY *= VrUiSplit.WorldH / Math.Max(Screen.RealScreenResolutionHeight, 1f);
            return true;
        }
    }

    /// <summary>
    /// TEST 283: with the panel filling the whole view, the HUD and menus are
    /// confined to the central share the player sized (VrUiProjection.HudArea) -
    /// the game's own usable-area mechanism, the one a TV safe area uses.
    /// </summary>
    [HarmonyPatch(typeof(ScreenManager), nameof(ScreenManager.EarlyUpdate))]
    public static class UsableAreaPatch
    {
        /// <summary>The engine's own area, last frame. Handed back exactly - the
        /// setter relayouts on any bit of difference. See RestoreGameScale.</summary>
        internal static Vec2 Raw;

        private static void Prefix(ref Vec2 usableArea)
        {
            Raw = usableArea;
            if (VrUiProjection.HudArea(out float f))
                usableArea = new Vec2(usableArea.x * f, usableArea.y * f);
        }
    }

    [HarmonyPatch(typeof(MBWindowManager), nameof(MBWindowManager.WorldToScreenWithFixedZ))]
    public static class WorldToScreenWithFixedZPatch
    {
        private static void Prefix(ref Camera camera)
        {
            camera = VrUiProjection.For() ?? camera;
        }

        private static void Postfix(ref float screenX, ref float screenY) =>
            WorldPage.Remap(ref screenX, ref screenY);
    }
}
