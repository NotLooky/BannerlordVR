using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;
using BannerlordVR.VR;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// The block seam, moved UPSTREAM of the damage UI (TEST 242).
    ///
    /// WHY NOT Agent.RegisterBlow
    ///
    /// The block has always been cancelled at Agent.RegisterBlow, and the health
    /// was always right - but the "-10" the player sees on a blocked blow comes
    /// from the combat log, and Mission.RegisterBlow generates that log BEFORE it
    /// calls Agent.RegisterBlow:
    ///
    ///     Mission.RegisterBlow
    ///       -> PrintAttackCollisionResults -> _combatLogsCreated queue
    ///       -> CombatLogManager.GenerateCombatLog
    ///       -> MissionGauntletAgentStatus.OnGenerateCombatLog
    ///       -> MissionAgentStatusVM.OnMainAgentHit -> the damage popup
    ///
    /// So the blow was stopped one step too late: the number was already queued.
    /// A prefix here returns false before the log, before the damage, before the
    /// sound and before any MissionBehavior hears about the blow.
    ///
    /// The private target is resolved by hand for the same reason CombatHitPatch
    /// does it: `ref AttackCollisionData`, `in MissionWeapon` and
    /// `ref CombatLogData` are all byrefs in metadata, and only
    /// MakeByRefType() can name them.
    ///
    /// The decision itself lives in VrPhysicalCombat.BeforeMeleeBlow, so both
    /// seams share one implementation: this one for engine melee (which carries
    /// the collision data), and the Agent.RegisterBlow prefix as the fallback for
    /// blow sources that never pass through here.
    /// </summary>
    [HarmonyPatch]
    public static class MeleeBlowPatch
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
            return AccessTools.Method(typeof(Mission), "RegisterBlow",
                                      new Type[]
                                      {
                                          typeof(Agent),
                                          typeof(Agent),
                                          typeof(WeakGameEntity),
                                          typeof(Blow),
                                          typeof(AttackCollisionData).MakeByRefType(),
                                          typeof(MissionWeapon).MakeByRefType(),
                                          typeof(CombatLogData).MakeByRefType(),
                                      });
        }

        /// <summary>True lets the blow land; false cancels it before the combat
        /// log, the damage, the sound, the reaction and the death all happen.
        ///
        /// The parameter is named `b` because Harmony binds patch parameters BY
        /// NAME against the game's own metadata, and the shipped method names it
        /// `b`. It was called `blow` here for a whole test series, which threw
        /// "Parameter blow not found" at startup and took this whole class - the
        /// combat-log fix included - silently out of the build. The exception is
        /// in the log at every launch; this is what it was saying.</summary>
        [HarmonyPrefix]
        private static bool Prefix(Agent attacker, Agent victim, Blow b,
                                   ref AttackCollisionData collisionData)
        {
            // TEST 263: this is the player's OWN synthetic blow, arriving here
            // because MeleeHitCallback's real body calls the real (private)
            // RegisterBlow, which this same patch also targets. It already
            // passed GuardStops/ShieldStops in Strike; neither the latency probe
            // (that measures the HIDDEN attack) nor the block-cancel test below
            // apply to a hit that is already fully decided. TEST 248's dupe
            // window is gone with it - the hidden attack is ghosted outright
            // now (MeleeHitPatch), not merely deduplicated per victim.
            if (MeleeHitPatch.RunningOurBlow)
            {
                MeleeHitPatch.LastOurBlowFlags = b.BlowFlag;
                return true;
            }

            // TEST 246: the latency probe rides here because this is the one
            // place a blow becomes damage - the engine's own swing included.
            VrMelee.NoteEngineBlow(attacker, victim, b);

            bool land = VrPhysicalCombat.BeforeMeleeBlow(attacker, victim, b,
                                                         in collisionData);
            if (!land)
                MeleeHitPatch.BlowCancelled = true;
            return land;
        }
    }
}
