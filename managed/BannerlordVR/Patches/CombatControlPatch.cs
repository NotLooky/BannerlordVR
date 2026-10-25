using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;
using BannerlordVR.VR;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// TEST 245. The motion melee seam: the game's own control tick, after it
    /// has composed the trigger and the stick into Agent.MovementFlags.
    ///
    /// WHY THIS METHOD
    ///
    /// MovementFlags is the engine's melee input - four attack directions, four
    /// guards and DefendBlock - and ControlTick is the one place the player's
    /// input becomes it, once per mission tick. A postfix here writes the same
    /// bits the trigger would have; everything downstream is untouched, so the
    /// engine's attack state machine, its collision, its AI reaction and its
    /// block rules all run because none of them were replaced.
    ///
    /// The decision itself is VrMelee.Apply. This class is only the seam, and it
    /// is deliberately the same seam TEST 236 used (VrMelee and
    /// CombatControlPatch, later replaced by physical combat); the difference is
    /// the gesture table and that the arms are never handed back to the
    /// animation.
    ///
    /// It is a postfix because ControlTick REBUILDS the flags from zero every
    /// tick - a prefix, or a write from anywhere else in the frame, would be
    /// overwritten before the engine reads it.
    /// </summary>
    [HarmonyPatch]
    public static class CombatControlPatch
    {
        /// <summary>Always applied: the melee switch is a runtime gate, so a
        /// patch that was never laid down could not be switched on without a
        /// restart, and a toggle that needs a restart is not a toggle.</summary>
        private static bool Prepare()
        {
            if (TargetMethod() != null)
                return true;

            VrLog.Warn("Motion melee: MissionMainAgentController.ControlTick is not where "
                       + "it was, so swings cannot drive the game's attacks. The trigger and "
                       + "the stick keep working; nothing else is affected.");
            return false;
        }

        private static MethodBase TargetMethod()
        {
            // Empty parameter list, not "any overload": the name alone would
            // also match a future overload with arguments, and patching the
            // wrong one would look exactly like "melee silently stopped".
            return AccessTools.Method(typeof(MissionMainAgentController), "ControlTick",
                                      new Type[0]);
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            // VrMelee catches its own failures and reports them once; this is
            // the belt to that pair of braces, because a throw here would land
            // inside the game's own input tick.
            try
            {
                VrMelee.Apply(Mission.Current?.MainAgent);

                // TEST 269: a bow in hand masks the attack bits instead, so the
                // trigger cannot start the vanilla draw under the manual one.
                VrBow.Apply(Mission.Current?.MainAgent);

                // TEST 373: likewise a crossbow - no vanilla shot, no reload.
                VrCrossbow.Apply(Mission.Current?.MainAgent);

                // TEST 470/472: the rope's gear and steer, and a saddle grip's mount.
                VrRein.Apply(Mission.Current?.MainAgent);

                // TEST 512: piloting a ranged machine, the main trigger is the shot.
                VrSiege.Apply(Mission.Current?.MainAgent);
            }
            catch (Exception ex)
            {
                VrMelee.ReportFailure(ex);
            }
        }
    }
}
