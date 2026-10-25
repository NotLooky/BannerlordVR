using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.MountAndBlade;
using BannerlordVR.VR;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// Physical blocking: an enemy blow on the player is cancelled when the
    /// player's blade is where the blow lands.
    ///
    /// WHY A PREFIX ON Agent.RegisterBlow
    ///
    /// RegisterBlow is the one place a blow becomes damage (TEST 236 proved it
    /// end to end: subtract health, call Die, play the sound, run the native
    /// reaction). A prefix that returns false is therefore a complete block -
    /// health, sound, stagger and death all never happen - and it runs at the
    /// exact instant the engine decided the blow landed. There is no frame to
    /// race and no second damage path to fight.
    ///
    /// THE TARGET IS RESOLVED BY HAND, AND IT HAS TO BE
    ///
    /// RegisterBlow's signature is (Blow, in AttackCollisionData). An `in`
    /// parameter is a BY-REF in metadata, so a [HarmonyPatch(typeof(Agent),
    /// "RegisterBlow", new Type[] { typeof(Blow), typeof(AttackCollisionData) })]
    /// annotation does not match it: PatchAll threw "Undefined target method"
    /// and - because PatchAll stops at the first bad class - every patch after
    /// this one silently never applied. That is what made the whole mod look
    /// vanilla (a session where hands, camera and stereo all died, 2026-09-23).
    /// The byref type cannot be written in an attribute (MakeByRefType is not a
    /// constant), so the target is resolved in TargetMethod() instead.
    ///
    /// The test itself - your blade against the blow's impact point, and the
    /// glide and opposition checks - lives in VrPhysicalCombat.BeforePlayerBlow,
    /// which is where the blade snapshots and the tuning keys already are. This
    /// class is only the seam.
    ///
    /// It patches EVERY agent's blows, so the first thing the other side does is
    /// throw away anything that is not the player's. That reference compare is
    /// the whole cost for the common case.
    /// </summary>
    [HarmonyPatch]
    public static class CombatHitPatch
    {
        /// <summary>Always applied: physical combat can be turned on from the VR
        /// panel (TEST 244), and a patch that was never laid down cannot be
        /// switched on. The runtime gate in VrPhysicalCombat does the work.</summary>
        private static bool Prepare()
        {
            return true;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Agent), "RegisterBlow",
                                      new Type[]
                                      {
                                          typeof(Blow),
                                          typeof(AttackCollisionData).MakeByRefType(),
                                      });
        }

        /// <summary>True lets the blow land; false cancels it before any of the
        /// engine's damage path runs.</summary>
        [HarmonyPrefix]
        private static bool Prefix(Agent __instance, Blow blow)
        {
            return VrPhysicalCombat.BeforePlayerBlow(__instance, blow);
        }
    }
}
