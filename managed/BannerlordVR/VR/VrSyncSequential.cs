using System;
using BannerlordVR.Interop;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Freezes the mission simulation on the second frame of every eye pair, so
    /// both eyes are drawn from ONE world state.
    ///
    /// WHERE THIS COMES FROM
    ///
    /// Alternate-frame rendering draws the left eye at simulation time T and the
    /// right at T+1. Every soldier, horse and arrow has moved between them. The
    /// two images are therefore not a stereo pair of one moment - they are two
    /// different moments, one per eye, and the brain reads that as things
    /// shimmering and tearing apart in depth. In a battle, where essentially
    /// everything is moving, it is the dominant artefact.
    ///
    /// Two other VR ports arrived at the same answer independently. UEVR ships it
    /// as "Skip Tick". The Cyberpunk 2077 port reached it after concluding that
    /// genuine simultaneous stereo was "exhausted at every explored lever" -
    /// because the engine is "architected around ONE main view" - and its own note
    /// describes the alternative exactly:
    ///
    ///     "don't make a 2nd registered view; render the SINGLE view twice with
    ///      sim/time FROZEN between eye-0 and eye-1, submit as a synced OpenXR
    ///      pair. Delivers the user's actual experiential goal (both eyes one
    ///      consistent sim tick -> NO ghosting)."
    ///
    /// That project then spent most of a session failing to find the freeze: the
    /// engine's per-frame delta is dispatched through a job graph with no static
    /// call chain, and the time-dilation apply point is arithmetic buried inside a
    /// 48KB function that will not decompile. They ended up piggybacking the
    /// game's own Sandevistan time-dilation from script.
    ///
    /// Bannerlord hands us the same primitive as a method parameter.
    /// Mission.OnTick takes dt. Passing zero for it is the whole freeze.
    ///
    /// WHAT IT COSTS, HONESTLY
    ///
    /// The world simulates at half the frame rate - 45 Hz of world at 90 Hz of
    /// display. That is not hidden by anything; animation and physics genuinely
    /// advance half as often. What does NOT slow down is head tracking: each eye
    /// is still submitted with its own pose and the compositor reprojects both, so
    /// looking around stays exactly as smooth as it is now.
    ///
    /// The trade is "the world moves in slightly coarser steps" against "the two
    /// eyes agree". For a battle full of moving men, the second is worth more.
    ///
    /// THE PAIRING HAS TO BE IN PHASE, AND THAT IS THE ONE FRAGILE PART
    ///
    /// Freezing every other frame only helps if the frozen frame is the SECOND of
    /// a pair. Land it on the first instead and each eye still sees a different
    /// world state - the same artefact, no benefit, and half the sim rate paid for
    /// nothing.
    ///
    /// The phase is derived from which eye AFR is about to draw, which depends on
    /// Mission.OnTick running before MissionScreen.OnFrameTick within a frame.
    /// That is true, but it is an ordering assumption rather than something this
    /// file can enforce - so sync_sequential_phase inverts it, and the log reports
    /// which eye each frozen frame belonged to so the answer is readable rather
    /// than a matter of squinting at the headset.
    /// </summary>
    public static class VrSyncSequential
    {
        /// <summary>
        /// OFF by default. It halves the simulation rate, which is a real cost and
        /// a matter of taste, not a bug fix that everyone wants.
        /// </summary>
        public static bool Enabled { get; } = VrConfig.Bool("sync_sequential", false);

        /// <summary>
        /// Whether the freeze is actually running, which is NOT the same as the
        /// config above.
        ///
        /// Full AFR requires it and does not ask. The whole claim of that mode
        /// is that the two eyes of a pair are one moment; without the freeze the
        /// pair is two moments rendered back to back, which is ordinary AFR with
        /// the frames merely closer together - the artefact intact and twice the
        /// frame rate paid for it. There is no configuration in which that is
        /// what someone wanted, so the mode turns it on rather than offering it.
        ///
        /// It is also FREE there, which is why the honest cost written down at
        /// the top of this file does not apply. That cost is "the world advances
        /// at half the frame rate" - true when a pair spans two display frames.
        /// Full AFR fits the pair inside one, so the world advances once per
        /// display frame: full sim rate, and the freeze is pure gain.
        /// </summary>
        public static bool Engaged =>
            Enabled || VrRenderMode.FullAfrActive;

        /// <summary>
        /// The tick the world is on. Advances on every frame the simulation was
        /// NOT frozen, and is what the native capture path pairs eyes by.
        ///
        /// int rather than long because it crosses the ABI and is only ever
        /// compared for equality; wrapping after two billion frames changes
        /// nothing, and would take eight months of continuous play to reach.
        /// </summary>
        public static int Generation { get; private set; }

        /// <summary>
        /// Hands the current tick to the native layer.
        ///
        /// Called from VrAfrRenderer.Tick, in MissionScreen.OnFrameTick's
        /// postfix - before the frame renders, which is the only property that
        /// matters. It deliberately does NOT matter whether Mission.OnTick has
        /// already run this frame: if it has, this is the tick the frame will
        /// be drawn from; if it has not, this is one behind and EVERY frame is
        /// one behind by the same amount. Pairing compares two generations for
        /// equality, and a constant offset cancels out of a comparison.
        ///
        /// Publishing -1 when the mode is off is what makes the native side put
        /// every eye up as it arrives, which is the pre-pairing behaviour.
        /// </summary>
        public static void PublishGeneration()
        {
            try
            {
// -1 whenever pairing is not actually operating, which is NOT the
                // same as "sync_sequential = 0".
                //
                // This cost a whole round. In AFW the freeze is correctly
                // stood down - both eyes come from one render, so there is
                // nothing to freeze - but the GENERATION kept being published,
                // and it now advanced on EVERY frame because no frame was ever
                // frozen. The native side pairs by matching generations, so two
                // adjacent frames never matched, no pair was ever promoted, and
                // the only thing reaching the headset was the safety valve
                // firing ten times a second - one eye each, so about 5 Hz per
                // eye. Reported, accurately, as "feels like 5 fps".
                //
                // Pairing is an AFR device. If the freeze is not running, the
                // pairing it exists to complete must not be running either.
                bool pairing = Engaged && !VrAfw.Enabled;

                NativeMethods.bvr_set_sim_generation(pairing ? Generation : -1);
            }
            catch (Exception ex)
            {
                if (_publishFailed)
                    return;

                _publishFailed = true;
                VrLog.Error("Could not publish the simulation tick; eye pairing is off "
                            + "and AFR falls back to putting each eye up as it arrives.", ex);
            }
        }

        private static bool _publishFailed;


        /// <summary>Inverts which frame of the pair is frozen. See the class note -
        /// if it lands out of phase the mode costs sim rate and buys nothing.</summary>
        private static readonly bool InvertPhase = VrConfig.Bool("sync_sequential_phase", false);

        private static int _frozen;
        private static int _advanced;
        private static int _tick;
        private static bool _loggedFirst;

        /// <summary>
        /// True when this frame's simulation should not advance.
        ///
        /// Only in AFR. Native stereo draws both eyes from the same tick already -
        /// that is the entire point of it - so freezing there would halve the sim
        /// rate for nothing at all.
        /// </summary>
        public static bool ShouldFreeze()
        {
            if (!Engaged || !VrAfrRenderer.Enabled || !VrCameraDriver.HasPose)
                return false;

            // AFW builds both eyes out of one render, so they already share a
            // tick and there is nothing for a freeze to fix. Running it anyway
            // would halve the world rate and buy nothing at all.
            if (VrAfw.Enabled)
                return false;

            // AFR flips its eye in MissionScreen.OnFrameTick's postfix, which runs
            // after this. So the eye it is about to draw is the opposite of the one
            // it last drew, and the frame to freeze is the one carrying the SECOND
            // eye of the pair.
            int eyeAboutToDraw = 1 - VrAfrRenderer.CurrentEye;
            bool freeze = eyeAboutToDraw == 1;

            return InvertPhase ? !freeze : freeze;
        }

        /// <summary>Called from the tick patch with what it decided, so the log can
        /// show the pairing actually alternating rather than drifting.</summary>
        public static void Record(bool frozen)
        {
            if (frozen)
            {
                _frozen++;
            }
            else
            {
                _advanced++;

                // The world moved, so this is a new state to pair against.
                // Everything rendered from here until the next advance belongs
                // to this generation, and the native side promotes a pair to
                // the screen exactly when both its halves carry this number.
                Generation++;
            }

            if (++_tick < 300)
                return;
            _tick = 0;

            // A healthy pairing is one frozen frame for every advanced one. A ratio
            // far off 1:1 means the eye parity is not alternating cleanly and the
            // pairs are not pairs.
            VrLog.Info(string.Format(
                "Sync sequential: {0} frame(s) advanced the world, {1} frozen "
                + "(phase {2}). Near 1:1 is a correct pairing; anything else means "
                + "the two eyes are still seeing different moments.",
                _advanced, _frozen, InvertPhase ? "inverted" : "normal"));

            _advanced = 0;
            _frozen = 0;

            if (!_loggedFirst)
            {
                _loggedFirst = true;
                VrLog.Info("Sync sequential is live: both eyes of a pair now come from "
                           + "one simulation tick. The world advances at half the frame "
                           + "rate; head tracking is unaffected.");
            }
        }

        public static void Reset()
        {
            _frozen = 0;
            _advanced = 0;
            _tick = 0;
        }
    }
}
