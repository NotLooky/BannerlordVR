using System;
using System.Reflection;
using BannerlordVR.VR.Physics;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.Physics
{
    // SandBox's PreloadScreen calls SandBoxSaveHelper.TryLoadSave on every
    // frame once its delay is over, until the game state changes. A save made
    // with other modules (this one added) asks "load with different
    // modules?" first: the frame the answer is given, the screen asks again.
    // A Yes left a second copy of the question waiting, shown on the campaign
    // map, and its Yes loaded the save a second time from inside the running
    // campaign, which never finished. A Cancel (no onCancel from the preload)
    // was asked again at once, so the question never went away.
    // The preload screen asks once; its Cancel goes back to the main menu.
    internal static class SaveLoadGuard
    {
        internal static object Ticking, Asked;

        internal static void BackToMenu()
        {
            Asked = null;
            PhysicsLog.Info("SAVE_LOAD cancelled at the preload screen: back to the main menu");
            try
            {
                GameStateManager states = TaleWorlds.MountAndBlade.Module.CurrentModule.GlobalGameStateManager;
                states.CleanAndPushState(states.CreateState<InitialState>());
            }
            catch (Exception ex) { PhysicsLog.Error("Going back to the main menu after a cancelled load failed.", ex); }
        }
    }

    [HarmonyPatch]
    internal static class PreloadTickPatch
    {
        private static MethodBase Target() => AccessTools.Method(AccessTools.TypeByName("SandBox.View.PreloadScreen"), "OnFrameTick");
        public static bool Prepare() => BannerlordVR.VR.VrConfig.Bool("save_load_guard", false) && Target() != null;
        public static MethodBase TargetMethod() => Target();

        [HarmonyPrefix]
        public static void Prefix(object __instance) => SaveLoadGuard.Ticking = __instance;
        [HarmonyFinalizer]
        public static void Finalizer() => SaveLoadGuard.Ticking = null;
    }

    [HarmonyPatch]
    internal static class TryLoadSavePatch
    {
        private static MethodBase Target() => AccessTools.Method(AccessTools.TypeByName("SandBox.SandBoxSaveHelper"), "TryLoadSave");
        public static bool Prepare() => BannerlordVR.VR.VrConfig.Bool("save_load_guard", false) && Target() != null;
        public static MethodBase TargetMethod() => Target();

        [HarmonyPrefix]
        public static bool Prefix(ref Action onCancel)
        {
            object screen = SaveLoadGuard.Ticking;
            if (screen == null) return true;
            if (screen == SaveLoadGuard.Asked) return false;
            SaveLoadGuard.Asked = screen;
            if (onCancel == null) onCancel = SaveLoadGuard.BackToMenu;
            PhysicsLog.Info("SAVE_LOAD the preload screen asks once; it will not ask again every frame");
            return true;
        }
    }
}
