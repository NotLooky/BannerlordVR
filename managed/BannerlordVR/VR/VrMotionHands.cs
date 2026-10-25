using System;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// One switch for the motion-control hands, the weapon following, and the
    /// melee that hangs off them.
    ///
    /// WHY IT IS OFF, AND WHY IT IS NOT DELETED
    ///
    /// The hand posing writes bone frames every frame that the game's own
    /// animation writes back over, and the two have never fully agreed - the
    /// weapon lands where the controller is only for the frames the animation
    /// happens not to contest. The swing detection on top of that is unfinished.
    /// Meanwhile the mod is entirely playable without any of it, because the
    /// controllers already work as a gamepad and that path is unrelated.
    ///
    /// So it becomes a labelled experiment rather than a default. That is a
    /// different thing from removing it: the calibration, the grip capture and
    /// the bone work all still exist and still run when this is on, and turning
    /// it on is one checkbox rather than a rebuild.
    ///
    /// WHAT THIS DOES NOT TOUCH
    ///
    /// VrGamepad and VrInputManager. The controllers reporting as an Xbox pad is
    /// how the player moves, aims a menu and presses things, and none of that is
    /// experimental. Anyone reading this looking for why their sticks stopped
    /// working is in the wrong file.
    /// </summary>
    public static class VrMotionHands
    {
        /// <summary>
        /// Off by default now. Was implicitly on through hand_follow_both and
        /// weapon_follow; those keys still work and are read here, so an existing
        /// config that deliberately enabled them keeps its behaviour.
        /// </summary>
        public static bool Enabled { get; private set; } =
            VrConfig.Bool("motion_hands", false);

        private static bool _logged;

        /// <summary>
        /// Set from the VR panel. Takes effect on the next frame.
        ///
        /// Turning it OFF is not just "stop calling Tick". That was the old belief
        /// - the game's own animation is running underneath and takes back over
        /// with nothing to restore - and it was true of the bone writes and false
        /// of everything else we had asked the engine to do. weapon_hold pauses the
        /// skeleton outright, and a paused skeleton stays paused however many of
        /// our own flags get cleared. See ReleaseSkeleton.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            if (Enabled == enabled)
                return;

            Enabled = enabled;

            if (!enabled)
            {
                // FIRST, and before the resets, because this is the only half that
                // touches the ENGINE rather than our own flags.
                //
                // weapon_hold pauses the player's skeleton animation, and that is
                // state on the engine's entity - clearing our booleans does not
                // undo it. Switching the experiment off used to stop us posing the
                // hands and leave the animation paused: one static posture, legs
                // not walking, for the rest of the mission, with no way back short
                // of restarting it. Turning a feature off has to actually turn it
                // off.
                //
                // The entity is resolved HERE, from the live mission, rather than
                // remembered by VrWeaponAttach. The accessors behind GameEntity are
                // unbounded native calls and a dead one faults rather than
                // throwing, so the only pointer worth handing to the release is one
                // that has just been read out of Mission.Current.
                ReleaseSkeleton();

                // Let go of anything currently held so the character is not left
                // mid-pose with a weapon parked where a controller used to be.
                // TEST 228: items drawn on our own entities are handed back to the
                // engine first, from the live mission, while it is still there.
                VrSkinnedWeapon.Release(Mission.Current?.MainAgent);
                VrWeaponHands.Reset();
                VrWeaponEntity.Reset();
                VrWeaponAttach.Reset();

                VrLog.Info("Motion-control hands off: head aiming and the gamepad are "
                           + "untouched and the character animates itself again. Nothing "
                           + "writes bones from here, so the game's own animation is the "
                           + "only thing posing the body.");
            }
        }

        /// <summary>
        /// Hands the player's skeleton back to the game's animation, if there is
        /// one to hand back.
        ///
        /// Every step is null-checked on the way down rather than wrapped in a
        /// single try: no mission, no agent, an agent between visuals - these are
        /// ordinary states, not failures, and there is simply nothing to release
        /// in any of them.
        /// </summary>
        private static void ReleaseSkeleton()
        {
            try
            {
                Agent agent = Mission.Current?.MainAgent;
                if (agent == null || !agent.IsActive())
                    return;

                MBAgentVisuals visuals = agent.AgentVisuals;
                if (visuals == null)
                    return;

                VrWeaponAttach.Release(visuals.GetEntity(), visuals.GetSkeleton());
            }
            catch (Exception ex)
            {
                VrLog.Error("Could not reach the player's skeleton to release the animation "
                            + "hold. If the character is stuck in one posture, the mission "
                            + "restart clears it.", ex);
            }
        }

        /// <summary>
        /// Whether the hand and weapon posing should run this frame. Every entry
        /// point into that work asks this rather than checking its own config
        /// key, so one switch really does turn all of it off.
        /// </summary>
        public static bool ShouldPose
        {
            get
            {
                if (!Enabled)
                {
                    if (!_logged)
                    {
                        _logged = true;
                        VrLog.Info("Motion-control hands and melee are OFF - the character "
                                   + "animates itself as the game intends. Turn them on in "
                                   + "the VR panel (End) under Body and hands; they are "
                                   + "experimental. The controllers work as a gamepad "
                                   + "either way.");
                    }

                    return false;
                }

                // TEST 570: pre-battle the camera flies free of the body, so
                // posed hands and weapons would float in front of it.
                if (VrMissionPhase.Deploying)
                    return false;

                return true;
            }
        }
    }
}
