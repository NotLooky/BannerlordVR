using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// TEST 761. The player is never faded in.
    ///
    /// The arena practice fight calls Agent.FadeIn on every spawn, the player
    /// included. Native then keeps a fade state on the agent that reaches the
    /// finger copies we hang on its skeleton: they drew as a dithered ghost that
    /// came and went (TEST 760's opaque copies did not stop it). From inside the
    /// body a fade-in shows nothing anyway, so the player's is skipped.
    /// </summary>
    [HarmonyPatch(typeof(Agent), nameof(Agent.FadeIn))]
    public static class FadeInPatch
    {
        private static bool _logged;

        private static bool Prefix(Agent __instance)
        {
            if (!__instance.IsPlayerControlled)
                return true;

            if (!_logged)
            {
                _logged = true;
                VrLog.Info("TEST 761: Agent.FadeIn skipped for the player (arena ghost fingers).");
            }

            return false;
        }
    }
}
