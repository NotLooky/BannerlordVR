using System;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// The one per-frame seam that runs after the engine has finished with the
    /// agent and before the frame is drawn.
    ///
    /// WHY ANOTHER SEAM AT ALL
    ///
    /// TESTs 207 to 209 between them fixed the shape of the weapon problem
    /// exactly:
    ///
    ///   207  a global write is undone before the next frame - metres, not
    ///        millimetres. So is a local one. Writing the frame is finished as an
    ///        approach, in either space.
    ///   208  the entity is parented and tracks the ANIMATED hand bone.
    ///   209  asking the engine to re-derive its renderer data puts the weapon
    ///        exactly where it belongs - 0.000 m, both hands, every sample.
    ///
    /// So the answer is right when we compute it and wrong when it is drawn, and
    /// the player can see the join: paused, the weapon sits in the hand; unpaused,
    /// it snaps back to the animation. That is not a wrong calculation. It is a
    /// correct one being redone by something that runs after us.
    ///
    /// AfterMissionTick is the last hook the mod had, and it is upstream of
    /// whatever that is. MissionBehavior.OnPreDisplayMissionTick is called once
    /// per frame immediately before the mission is displayed, which is downstream
    /// of the mission tick and of the renderer data being built - so it is the
    /// natural candidate, and the only way to find out is to put a frame counter
    /// in it.
    ///
    /// IT REPORTS ITS OWN RATE, FIRST
    ///
    /// A hook that fires once per scene and a hook that fires once per frame
    /// produce the same symptom in the headset and need completely different
    /// fixes - that is CameraPatch's lesson and it cost a session there too. So
    /// this counts its own calls against the heartbeat before anything is
    /// concluded from what it does.
    /// </summary>
    public class VrLateWeapon : MissionBehavior
    {
        public override MissionBehaviorType BehaviorType => MissionBehaviorType.Other;

        private static int _frames;
        private static double _nextReport;
        private static int _reports;
        private static bool _broken;

        /// <summary>How many rate reports to print before going quiet. Enough to
        /// tell per-frame from per-scene and no more.</summary>
        private const int RateReports = 3;

        public override void OnPreDisplayMissionTick(float dt)
        {
            if (_broken || !VrSystem.IsActive)
                return;

            try
            {
                ReportRate();

                // Does nothing unless weapon_seam = predisplay. The seam is chosen
                // in the config rather than here so the two can be compared in one
                // session without a rebuild.
                VrWeaponHands.LateWeaponPass();
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("The pre-display weapon pass failed; weapons go back to the "
                            + "game's own animation. Nothing else is affected.", ex);
            }
        }

        /// <summary>Prints how often this hook actually fires, so "it runs per
        /// frame" is a measurement rather than an assumption.</summary>
        private static void ReportRate()
        {
            _frames++;

            if (_reports >= RateReports)
                return;

            double now = (double)System.Diagnostics.Stopwatch.GetTimestamp()
                         / System.Diagnostics.Stopwatch.Frequency;

            if (_nextReport == 0)
            {
                _nextReport = now + 1.0;
                return;
            }

            if (now < _nextReport)
                return;

            _reports++;
            VrLog.Info("OnPreDisplayMissionTick fired " + _frames + " time(s) in the last "
                       + "second. Compare against the heartbeat: the same number means this "
                       + "is a per-frame seam and the weapon can be written here; a handful "
                       + "means it is not.");

            _frames = 0;
            _nextReport = now + 1.0;
        }

        public static void Reset()
        {
            _frames = 0;
            _nextReport = 0;
            _reports = 0;
            _broken = false;
        }
    }
}
