using System;
using HarmonyLib;
using TaleWorlds.MountAndBlade;
using BannerlordVR.VR;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// Drops the "-10" the damage feed would show for a blow that was blocked
    /// (TEST 242).
    ///
    /// WHY THIS EXISTS AS WELL AS MeleeBlowPatch
    ///
    /// MeleeBlowPatch is the real fix: it cancels the blow before
    /// Mission.RegisterBlow queues the combat log, so there is nothing to
    /// suppress. This class is the belt to that pair of braces. The block at
    /// Mission.RegisterBlow needs Harmony to bind the target's ref/in
    /// parameters; if that particular patch fails to apply on some future game
    /// build, blocking still works through the Agent.RegisterBlow prefix and
    /// the only regression would be the popup - exactly the symptom TEST 242
    /// was opened for. The block records itself here (VrPhysicalCombat.Guard),
    /// and this drops the matching log entry.
    ///
    /// The match is victimMine + the same damage within a second, then it is
    /// consumed. Two blows of the same damage inside one second, one blocked and
    /// one landing, could swallow the landing one's popup; that is cosmetic, and
    /// with MeleeBlowPatch applied this class never sees a blocked blow at all.
    /// </summary>
    [HarmonyPatch(typeof(CombatLogManager), "GenerateCombatLog",
                  new Type[] { typeof(CombatLogData) })]
    public static class BlockedLogPatch
    {
        /// <summary>Always applied: physical combat can be turned on from the VR
        /// panel (TEST 244). Suppression itself is a no-op while nothing is
        /// blocking.</summary>
        private static bool Prepare()
        {
            return true;
        }

        [HarmonyPrefix]
        private static bool Prefix(CombatLogData logData)
        {
            return !VrPhysicalCombat.SuppressBlockedDamage(logData.IsVictimAgentMine,
                                                           logData.TotalDamage);
        }
    }
}
