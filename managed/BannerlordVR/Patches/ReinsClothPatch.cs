using System;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// TEST 471d: vanilla's reins could not be hidden. MountVisualCreator hands
    /// the harness's rope mesh to the native rein system
    /// (AddHorseReinsClothMesh) and then ManualInvalidates its own handle, so
    /// nothing managed can reach that rope afterwards; masking the rein metamesh
    /// on the skeleton (471c) left the strap drawn.
    ///
    /// This prefix gives the native our OWN copy of the rope instead, and keeps
    /// it - with the rein mesh - against the horse's visuals, so VrRope can mask
    /// both while the player rides that horse. Every horse still gets its reins.
    /// </summary>
    [HarmonyPatch(typeof(MBAgentVisuals), nameof(MBAgentVisuals.AddHorseReinsClothMesh))]
    public static class ReinsClothPatch
    {
        private static bool _failed;

        [HarmonyPrefix]
        private static void Prefix(MBAgentVisuals __instance, MetaMesh reinMesh, ref MetaMesh ropeMesh)
        {
            if (_failed || __instance == null || ropeMesh == null)
                return;

            try
            {
                MetaMesh ours = MetaMesh.GetCopy(ropeMesh.GetName(), false, true);
                if (ours == null)
                    return;

                ropeMesh = ours;
                VrRope.RememberVanilla(__instance, reinMesh, ours);
            }
            catch (Exception ex)
            {
                _failed = true;
                VrLog.Error("TEST 471d: keeping vanilla's rein rope failed; its reins stay as they are.", ex);
            }
        }
    }
}
