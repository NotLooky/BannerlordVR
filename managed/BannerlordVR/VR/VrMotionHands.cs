using BannerlordVR.VR;

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

        /// <summary>Set from the VR panel. Takes effect on the next frame - the
        /// hands simply stop being posed, and the game's own animation is already
        /// running underneath and takes back over with nothing to restore.</summary>
        public static void SetEnabled(bool enabled)
        {
            if (Enabled == enabled)
                return;

            Enabled = enabled;

            if (!enabled)
            {
                // Let go of anything currently held so the character is not left
                // mid-pose with a weapon parked where a controller used to be.
                VrWeaponHands.Reset();
                VrWeaponEntity.Reset();
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

                return true;
            }
        }
    }
}
