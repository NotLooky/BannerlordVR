using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using BannerlordVR.VR;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// TEST 305. An arrow the VR shield stopped sticks in the SHIELD.
    ///
    /// The mod's shield block (TEST 243, VrPhysicalCombat.MissileGuard) cancels
    /// the blow inside the RegisterBlow prefix, but Mission.MissileHitCallback
    /// carries on: it decides Stick, builds a frame local to the BONE the
    /// arrow met, and hands it here with attachedToShield = false - so the
    /// arrow went into the player's body, through a shield that had stopped it.
    /// This prefix turns that into the engine's own shield stick
    /// (Agent.AttachWeaponToWeapon on the off-hand slot), so the arrow lands in
    /// the shield's attached list, stays with it through sheathe, drop and
    /// pickup, and VrSkinnedWeapon draws it on the VR copy.
    ///
    /// The parameter names are the shipped method's own (Harmony binds by
    /// name; the test suite checks them).
    /// </summary>
    [HarmonyPatch]
    public static class MissileStickPatch
    {
        private static bool Prepare()
        {
            return true;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Mission), "HandleMissileCollisionReaction");
        }

        [HarmonyPrefix]
        private static void Prefix(int missileIndex, Mission.MissileCollisionReaction collisionReaction,
                                   ref MatrixFrame attachLocalFrame, bool isAttachedFrameLocal,
                                   Agent attachedAgent, ref bool attachedToShield,
                                   sbyte attachedBoneIndex)
        {
            try
            {
                if (collisionReaction == Mission.MissileCollisionReaction.Stick && isAttachedFrameLocal
                    && !attachedToShield)
                {
                    VrPhysicalCombat.RedirectStick(missileIndex, ref attachLocalFrame, attachedAgent,
                                                   ref attachedToShield, attachedBoneIndex);
                }
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 305: redirecting a stuck missile failed; it sticks where the "
                            + "engine put it.", ex);
            }
        }
    }
}
