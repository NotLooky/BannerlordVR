using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using BannerlordVR.VR;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// TEST 261. The controllers say when your own guard stops a blow.
    ///
    /// WHY THIS SEAM. With motion melee on, blocking is the engine's own guard
    /// STATE - VrMelee writes the Defend bits, the engine resolves the blow
    /// against them and calls OnAgentHitBlocked. The mod's own block test
    /// (VrPhysicalCombat.BeforeBlow) only sees the blows the engine did NOT block
    /// (TEST 264), so the one haptic that mattered - "your guard took that" - has
    /// no other caller for an engine-resolved block. This is where the engine announces
    /// exactly that, for a weapon block, a shield block and a missile block
    /// alike (the isMissile parameter says which kind of blow it was; the
    /// collision data says whether the shield or the weapon took it).
    ///
    /// The target is non-public and its collision data is a byref, so it is
    /// resolved by hand - the same reason MeleeBlowPatch and CombatHitPatch do
    /// it. VrPhysicalCombat.PlayerBlocked does the rest: it picks the hand that
    /// is holding a shield (falling back to the weapon hand), rate-limits, and
    /// stays silent while motion melee is off, where the mod's own block test
    /// and its Guard pulse already cover this.
    /// </summary>
    [HarmonyPatch]
    public static class PlayerBlockHapticPatch
    {
        /// <summary>Always applied: motion melee is a runtime switch, and a
        /// patch that was never laid down could not be switched on.</summary>
        private static bool Prepare()
        {
            return true;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Mission), "OnAgentHitBlocked",
                                      new Type[]
                                      {
                                          typeof(Agent),
                                          typeof(Agent),
                                          typeof(AttackCollisionData).MakeByRefType(),
                                          typeof(Vec3),
                                          typeof(Vec3),
                                          typeof(bool),
                                      });
        }

        /// <summary>The parameter names must match the game's own metadata, or
        /// Harmony throws "Parameter not found" at startup and this whole class
        /// silently leaves the build (the lesson MeleeBlowPatch already paid
        /// for). `ref` on the collision data for the same reason: it is a byref
        /// in the shipped method.</summary>
        [HarmonyPostfix]
        private static void Postfix(Agent affectedAgent, Agent affectorAgent,
                                    ref AttackCollisionData collisionData, Vec3 blowDirection)
        {
            try
            {
                if (affectedAgent == null || affectedAgent != Mission.Current?.MainAgent)
                    return;

                VrPhysicalCombat.PlayerBlocked(collisionData.AttackBlockedWithShield,
                                               affectorAgent, blowDirection);
            }
            catch (Exception)
            {
                // A pulse is never worth a throw inside the engine's collision path.
            }
        }
    }
}
