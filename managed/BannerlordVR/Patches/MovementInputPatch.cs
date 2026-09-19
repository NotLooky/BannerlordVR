using System;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// Rotates the player's movement input into the head's frame.
    ///
    /// THE SETTER, NOT THE CALLER, AND ON PURPOSE
    ///
    /// The obvious target is whatever reads the keyboard and works out the
    /// vector - but this build has no MissionMainAgentController at all, and the
    /// type that replaced it is not something a patch should be hunting for by
    /// name across game versions. Agent.MovementInputVector's setter is the one
    /// place every producer of movement input has to pass through, whatever the
    /// controller is called this month, and it is public.
    ///
    /// The cost is that it fires for EVERY agent, so the first thing the
    /// correction does is check this is the player's. That is a reference
    /// compare against Mission.Current.MainAgent - cheap enough to sit in front
    /// of a property setter that a battle calls a few hundred times a frame.
    ///
    /// A PREFIX with a ref parameter, because the value is an input: we are
    /// substituting what gets stored, not reacting to what was.
    /// </summary>
    [HarmonyPatch]
    public static class MovementInputPatch
    {
        private static bool Prepare()
        {
            if (!VrHeadMovement.Enabled)
                return false;

            if (TargetMethod() != null)
                return true;

            VrLog.Warn("Head-relative movement: Agent.MovementInputVector has no "
                       + "setter to patch, so movement keeps following the game's own "
                       + "facing. Nothing else is affected.");
            return false;
        }

        private static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.PropertySetter(typeof(Agent), "MovementInputVector");
        }

        private static void Prefix(Agent __instance, ref Vec2 value)
        {
            // VrHeadMovement owns every decision and every guard, including its
            // own exception handling - this runs inside a property setter at
            // frame rate and a throw here would take the battle down over a
            // comfort feature.
            value = VrHeadMovement.Correct(__instance, value);
        }
    }
}
