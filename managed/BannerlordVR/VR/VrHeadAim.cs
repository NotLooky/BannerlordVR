using System;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Points the player agent where the head is looking, on both axes.
    ///
    /// WHY THIS EXISTS ALONGSIDE VrEngineAim
    ///
    /// VrEngineAim adds the head's movement to the two private delta fields the
    /// MOUSE writes and lets the engine build the camera from them. That is the
    /// polite way to do it and it moves the camera, the culling and the aim
    /// together. Its weakness is that it is entirely conditional on a write into
    /// a non-public field continuing to have an effect, and there is no way to
    /// know from outside whether it does - which is how "head aiming is locked in
    /// the direction of the mouse camera" happened.
    ///
    /// Agent.LookDirection is a public settable Vec3 and it is the vector the
    /// game aims and swings with. Writing it says the thing we actually mean -
    /// point the character where I am looking - in one statement, with no
    /// intermediary that can quietly stop working.
    ///
    /// The two are complementary rather than competing. The delta injection moves
    /// the CAMERA; this moves the AGENT. If the injection is landing, both agree
    /// and this write changes nothing. If it is not, this still aims.
    ///
    /// THE FEEDBACK LOOP, AND WHY IT IS ALREADY HANDLED
    ///
    /// The rendered view is anchor * head. If the anchor were read from the
    /// agent's look direction - which is exactly what this class now drives -
    /// the head would be applied twice and the world would turn twice as far as
    /// the head does. The cut is already in place: whenever anything is steering
    /// the look, ResolveAnchor reads VrEngineAim.BodyHeading() instead, which is
    /// integrated from the player's mouse alone and cannot contain our own
    /// contribution. This class extends the condition rather than inventing a
    /// second answer to it.
    ///
    /// WHAT IT DOES NOT TOUCH
    ///
    /// The agent's BODY facing and its feet. Bannerlord turns the body to follow
    /// the look on its own schedule - while moving, while attacking, and not at
    /// all while standing and glancing about - and that is the game's animation
    /// system doing something reasonable. Forcing the body would fight it. What
    /// is wanted is the AIM, and the aim is the look direction.
    /// </summary>
    public static class VrHeadAim
    {
        /// <summary>On by default: this is the feature, not an experiment.</summary>
        public static bool Enabled { get; } = VrConfig.Bool("head_aim", true);

        /// <summary>Pitch as well as yaw. Both, unless someone wants only the
        /// horizontal half - which is the one shape of this that a person prone
        /// to motion sickness might genuinely prefer.</summary>
        private static readonly bool DrivePitch = VrConfig.Bool("head_aim_pitch", true);

        /// <summary>True while this is actually writing. Read by ResolveAnchor,
        /// which must stop reading the agent's look the moment we start driving
        /// it - see the class note.</summary>
        public static bool Active { get; private set; }

        private static bool _logged;
        private static bool _broken;

        // The survival probe. Same idea as the engine camera's: write, then read
        // back a frame later and report the difference. Near zero means the write
        // sticks and the agent really is aiming where you look. A large number
        // means something downstream recomputes LookDirection after us and this
        // seam is the wrong one - which is worth knowing in one run rather than
        // by feel.
        private static Vec3 _wrote;
        private static bool _haveWrote;
        private static float _driftSum;
        private static int _driftCount;
        private static int _tick;

        public static void Apply()
        {
            if (!Enabled || _broken)
                return;

            try
            {
                Agent agent = Mission.Current?.MainAgent;
                if (agent == null || !agent.IsActive() || !VrCameraDriver.HasPose)
                {
                    Active = false;
                    return;
                }

                // The anchor has to have somewhere else to read from before we
                // start writing the thing it currently reads. Until VrEngineAim
                // has calibrated and is integrating a body heading from the
                // mouse, driving the look would feed straight back into the view
                // and turn the world twice as far as the head.
                if (!VrEngineAim.MaintainsBodyHeading)
                {
                    Active = false;
                    WarnOnce();
                    return;
                }

                Measure(agent);

                Vec3 look = HeadLook();
                if (look.LengthSquared < 1e-6f)
                {
                    Active = false;
                    return;
                }

                Active = true;

                agent.LookDirection = look;
                _wrote = look;
                _haveWrote = true;

                if (!_logged)
                {
                    _logged = true;
                    VrLog.Info("Head aiming is driving the agent's look direction directly. "
                               + "The character now aims where you look on both axes; the "
                               + "anchor has switched to the mouse-integrated body heading "
                               + "so the head cannot be applied twice.");
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                Active = false;
                VrLog.Error("Head aiming failed; the agent goes back to aiming where the "
                            + "game's camera points. Head look and head movement are "
                            + "unaffected.", ex);
            }
        }

        /// <summary>
        /// The head's world forward, flattened to yaw only if pitch is off.
        /// </summary>
        private static Vec3 HeadLook()
        {
            Mat3 rotation = VrCameraDriver.HeadFrame.rotation;

            Vec3 f = DrivePitch ? rotation.f : VrMath.YawOnly(rotation).f;
            f.Normalize();
            return f;
        }

        /// <summary>
        /// How far the engine moved our write before we saw it again. Reported
        /// once every few seconds; see the note on the probe.
        /// </summary>
        private static void Measure(Agent agent)
        {
            if (!_haveWrote)
                return;

            Vec3 now = agent.LookDirection;

            float dot = now.x * _wrote.x + now.y * _wrote.y + now.z * _wrote.z;
            if (dot > 1f) dot = 1f;
            if (dot < -1f) dot = -1f;

            _driftSum += (float)Math.Acos(dot);
            _driftCount++;

            if (++_tick < 450)
                return;
            _tick = 0;

            float mean = _driftCount > 0 ? _driftSum / _driftCount * 57.2957795f : 0f;
            _driftSum = 0f;
            _driftCount = 0;

            VrLog.Info(string.Format(
                "Head aim: the agent's look drifted {0:F1} deg from what we wrote, on "
                + "average, between frames. A degree or two is the head moving during "
                + "the frame and is expected; tens of degrees means something "
                + "recomputes LookDirection after this seam and the write is not "
                + "sticking.", mean));
        }

        private static bool _warnedWaiting;

        /// <summary>Said once, because "waiting to calibrate" and "never going to
        /// work" look identical from inside a headset.</summary>
        private static void WarnOnce()
        {
            if (_warnedWaiting)
                return;

            _warnedWaiting = true;
            VrLog.Info("Head aiming is waiting for the engine-aim calibration to "
                       + "finish before it drives the agent's look - it needs the "
                       + "mouse-integrated body heading to anchor against first. Move "
                       + "the mouse briefly. If this never clears, engine_aim = 0 or "
                       + "the calibration failed, and head aiming stays off.");
        }

        public static void Reset()
        {
            Active = false;
            _haveWrote = false;
            _driftSum = 0f;
            _driftCount = 0;
            _tick = 0;
        }
    }
}
