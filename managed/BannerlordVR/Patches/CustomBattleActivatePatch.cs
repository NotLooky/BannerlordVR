using System.Reflection;
using HarmonyLib;
using BannerlordVR.VR;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// Stops the crash on leaving a custom battle (2026-09-27, finish or retreat).
    ///
    /// Any UI-scale change runs ScreenManager.OnScaleChange, which re-lays-out
    /// every screen in the stack, and a GauntletLayer relayout calls
    /// RefreshValues on its view model. CustomBattleVM cannot survive that with
    /// its movie loaded: each side clears a list bound to a dropdown, the
    /// dropdown drops to -1, and its selection handler reads a null item.
    /// Two different handlers were seen doing it (OnCharacterSelection,
    /// OnWallHitpointSelection) - the whole VM is unsafe, not one handler.
    ///
    /// Under a mission the screen has its movie unloaded and the relayout is
    /// harmless, which is why entering the battle and the escape menu never
    /// crashed. VrUiProjection handed the scale back the frame after the mission
    /// ended, when OnActivate had already reloaded the movie. So the scale is
    /// handed back here instead, before the reload.
    /// </summary>
    [HarmonyPatch]
    public static class CustomBattleActivatePatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                "TaleWorlds.MountAndBlade.CustomBattle.CustomBattleScreen:OnActivate");
        }

        /// <summary>The CustomBattle module is optional; without it there is
        /// nothing to patch.</summary>
        private static bool Prepare()
        {
            return TargetMethod() != null;
        }

        private static void Prefix()
        {
            try
            {
                VrUiProjection.RestoreGameScale();
            }
            catch (System.Exception ex)
            {
                VrLog.Error("Handing the UI scale back before the custom battle menu failed.", ex);
            }
        }
    }
}
