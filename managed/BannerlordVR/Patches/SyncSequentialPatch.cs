using System;
using HarmonyLib;
using TaleWorlds.MountAndBlade;
using BannerlordVR.VR;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// Holds the WORLD still between the two eyes of a pair.
    ///
    /// WHAT THIS IS FOR
    ///
    /// Native stereo is not reachable in this engine. rgl prepares the scene once
    /// per frame for one consumer, so a second registered view renders whatever
    /// the first one left - which is why it showed characters in one eye and
    /// terrain missing along the edges. That was proved here in a single battle
    /// with eye_order_swap, and independently by the Cyberpunk port after 35+
    /// sessions of disassembly against REDengine 4, which is architected the same
    /// way. Neither project talked the engine into preparing twice.
    ///
    /// What both converged on instead - UEVR ships it as Skip Tick - is to render
    /// the SINGLE view twice with the simulation frozen in between. The pair is
    /// then one moment seen from two places, rather than two moments seen from one
    /// each. Every eye is a real, fully prepared engine render: nothing is culled
    /// away, no skeleton goes unprepared, and there is no synthesis anywhere.
    ///
    /// WHY THE FREEZE IS ONE LINE HERE
    ///
    /// Mission.OnTick takes the simulation delta AS A PARAMETER. Passing zero for
    /// it is the entire mechanism. The Cyberpunk port had no such seam - its
    /// per-frame delta is dispatched through a job graph with no static call
    /// chain - and ended up driving it from Lua through Sandevistan slow motion.
    ///
    /// THREE THINGS THIS DELIBERATELY DOES NOT DO
    ///
    /// It does not skip the call. OnTick still RUNS, and every side effect a tick
    /// is expected to have still happens; only the clock stops. Skipping it
    /// outright is the version of this that breaks missions.
    ///
    /// It does not touch realDt. Input, interface and engine housekeeping keep
    /// their wall clock - zeroing both would freeze the frame rather than the
    /// world, which is a hang with extra steps.
    ///
    /// It does not decide anything. VrSyncSequential owns the question of WHICH
    /// frame is the second of a pair, because that answer depends on AFR's eye
    /// flip and belongs next to it. This patch asks and reports.
    /// </summary>
    [HarmonyPatch(typeof(Mission), "OnTick",
                  new Type[] { typeof(float), typeof(float), typeof(bool), typeof(bool) })]
    public static class SyncSequentialPatch
    {
        private static bool _broken;
        private static bool _loggedFirst;

        [HarmonyPrefix]
        private static void Prefix(ref float dt)
        {
            // Engaged, not Enabled: full AFR requires the freeze and switches it
            // on for itself. Reading the config flag here would leave that mode
            // rendering two eyes a tick apart and calling it a pair.
            if (_broken || !VrSyncSequential.Engaged)
                return;

            try
            {
                if (!VrSystem.IsActive)
                    return;

                VrBreadcrumb.Set(VrBreadcrumb.MissionTick);

                bool freeze = VrSyncSequential.ShouldFreeze();

                if (freeze)
                {
                    // The whole of it. dt is by-ref because Harmony passes the
                    // real argument slot, so writing here is what the engine
                    // actually receives.
                    dt = 0f;
                }

                VrSyncSequential.Record(freeze);

                if (!_loggedFirst)
                {
                    _loggedFirst = true;
                    VrLog.Info("Sync sequential: the world is now held still between "
                               + "the two eyes of a pair - Mission.OnTick is given "
                               + "dt = 0 on the frame carrying the second eye, so both "
                               + "eyes are one moment seen twice instead of two moments "
                               + "seen once each. realDt is untouched, so input and the "
                               + "interface keep their own clock. The world advances at "
                               + "half the display rate; head tracking does not, because "
                               + "each eye still carries its own pose. Watch the "
                               + "advanced/frozen line: near 1:1 is a correct pairing, "
                               + "and sync_sequential_phase = 1 inverts it if it is not.");
                }
            }
            catch (Exception ex)
            {
                // A VR failure drops to flat behaviour, never takes the mission
                // with it - and a freeze that throws every tick would be the
                // loudest possible way to break that rule.
                _broken = true;
                VrLog.Error("Sync sequential: the tick freeze failed and is off for the "
                            + "rest of the session; the world simulates every frame as "
                            + "usual and the two eyes go back to being a frame apart.", ex);
            }
        }
    }
}
