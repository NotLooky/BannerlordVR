using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.MountAndBlade.GauntletUI.Mission;
using TaleWorlds.MountAndBlade.ViewModelCollection.HUD;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// TEST 376: no crosshair, no attack/block direction arrows, no hit marker.
    ///
    /// All of them are one Gauntlet layer, "MissionCrosshair", which
    /// MissionGauntletCrosshair creates in OnCreateView and recreates whenever
    /// HideBattleUI toggles. In a headset the aim is the weapon in the hand, so
    /// a reticle at screen centre is noise. The direction arrows describe a swing
    /// the player makes physically.
    ///
    /// The layer is faded with ContextAlpha, which is how vanilla's own photo
    /// mode hides it (OnPhotoModeActivated). The vanilla options
    /// DisplayTargetingReticule / DisplayAttackDirection are not used: they leave
    /// the hit marker up, and they are the player's flat-screen settings.
    ///
    /// TEST 462: the hit marker alone, a VR panel option. In Crosshair.xml it is
    /// its own widget beside the CrosshairWidget, flashed through the binding
    /// (ShowHitMarker -> SetState), so the layer stays up and the rest goes: the
    /// reticle and the reload bars by their VM flags (set after vanilla's own,
    /// every tick), the four arrows by hand - siblings with no binding, whose
    /// alpha only CrosshairWidget.OnUpdate writes.
    /// </summary>
    [HarmonyPatch(typeof(MissionGauntletCrosshair), nameof(MissionGauntletCrosshair.OnMissionScreenTick))]
    public static class CrosshairPatch
    {
        private static readonly bool Show = VrConfig.Bool("hud_crosshair", false);

        /// <summary>TEST 462: the VR panel's "Hit marker".</summary>
        internal static bool HitMarker = VrConfig.Bool("hud_hit_marker", false);

        private static readonly AccessTools.FieldRef<MissionGauntletCrosshair, GauntletLayer> Layer =
            AccessTools.FieldRefAccess<MissionGauntletCrosshair, GauntletLayer>("_layer");

        private static readonly AccessTools.FieldRef<MissionGauntletCrosshair, CrosshairVM> Data =
            AccessTools.FieldRefAccess<MissionGauntletCrosshair, CrosshairVM>("_dataSource");

        private static readonly string[] Arrows = { "TopArrow", "BottomArrow", "LeftArrow", "RightArrow" };

        private static bool _logged;
        private static GauntletLayer _armsHidden;

        private static bool Prepare() => !Show;

        private static void Postfix(MissionGauntletCrosshair __instance)
        {
            var layer = Layer(__instance);
            if (layer?.UIContext == null)
                return;

            CrosshairVM vm = HitMarker ? Data(__instance) : null;
            if (vm == null)
            {
                layer.UIContext.ContextAlpha = 0f;
            }
            else
            {
                vm.IsVisible = false;
                vm.IsReloadPhasesVisible = false;
                if (!ReferenceEquals(_armsHidden, layer))
                {
                    _armsHidden = layer;
                    foreach (string id in Arrows)
                        Hide(layer.UIContext.Root, id);
                }

                layer.UIContext.ContextAlpha = 1f;
            }

            if (!_logged)
            {
                _logged = true;
                VrLog.Info("Crosshair: hidden in VR, with the direction arrows and hit marker "
                           + "(TEST 376; hud_crosshair = 1 shows them). TEST 462: the VR panel's "
                           + "\"Hit marker\" brings the marker back alone (hud_hit_marker, now "
                           + (HitMarker ? "1" : "0") + ").");
            }
        }

        private static void Hide(Widget w, string id)
        {
            if (w == null)
                return;
            if (w.Id == id)
                w.IsVisible = false;
            for (int i = 0; i < w.ChildCount; i++)
                Hide(w.GetChild(i), id);
        }
    }
}
