using System;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Makes W go where you are LOOKING rather than where the game thinks you
    /// are facing.
    ///
    /// WHAT IS ACTUALLY WRONG WITHOUT IT
    ///
    /// Agent.MovementInputVector is in the agent's own frame: +y is straight
    /// ahead along its look direction, +x is its right. On foot the player agent
    /// faces wherever the game's camera bearing points - which is the MOUSE, not
    /// the head.
    ///
    /// The headset shows a view built as anchor * head, so the moment you turn
    /// your head the picture and the agent's facing disagree by exactly the head
    /// yaw. Press W and you walk off at an angle to what you are looking at. It
    /// is a small thing standing still and thoroughly disorienting in a fight,
    /// because it breaks the one assumption every player has: forward is where I
    /// am looking.
    ///
    /// THE CORRECTION
    ///
    /// Rotate the input vector by the angle between the view and the agent's
    /// look, in the agent's own frame:
    ///
    ///     delta = viewYaw - lookYaw
    ///
    /// and that is all. Note that it is self-cancelling when head aiming IS
    /// working: if the head is successfully steering the game's look then
    /// lookYaw already equals viewYaw, delta is zero, and this does nothing. It
    /// only does work in proportion to how far the game has been left behind -
    /// which is the right amount, by construction, in both cases.
    ///
    /// MOUNTED IS DELIBERATELY EXCLUDED
    ///
    /// On a horse the same vector means something completely different: y is the
    /// throttle and x is the REIN. Rotating it would turn "look left" into "pull
    /// the horse left" and make the animal steer itself by where you glance,
    /// which is not head-relative movement - it is a horse that fights you. On
    /// foot the vector is a direction; mounted it is a pair of controls, and only
    /// the first is a thing that can be rotated.
    /// </summary>
    public static class VrHeadMovement
    {
        public static bool Enabled { get; } = VrConfig.Bool("head_move", true);

        /// <summary>Off, and see the class note - mounted, this vector is a
        /// throttle and a rein rather than a direction. Exposed only so the
        /// finding can be reproduced rather than taken on trust.</summary>
        private static readonly bool WhileMounted = VrConfig.Bool("head_move_mounted", false);

        /// <summary>
        /// Below this the correction is skipped entirely.
        ///
        /// It used to be 0.6 degrees, on the reasoning that a correction that
        /// small is not worth the arithmetic. That is true of the arithmetic and
        /// false of the feel: it means the smallest head movements produce no
        /// change in which way you walk at all, so movement snaps into agreement
        /// with the head rather than following it. Effectively zero now - small
        /// enough that any real head movement is a real change of direction, big
        /// enough that a perfectly still head is still bit-for-bit the game's own
        /// vector rather than one that has been through a rotation.
        /// </summary>
        private static readonly float MinDelta =
            VrConfig.Float("head_move_min_deg", 0.02f) * 0.0174532925f;

        private static Vec2 _lastWritten;
        private static bool _haveWritten;
        private static bool _logged;
        private static bool _broken;

        /// <summary>
        /// Rotates one movement input into the head's frame. Returns the value
        /// the setter should actually store.
        /// </summary>
        public static Vec2 Correct(Agent agent, Vec2 value)
        {
            if (!Enabled || _broken || agent == null)
                return value;

            try
            {
                if (!ReferenceEquals(agent, Mission.Current?.MainAgent))
                    return value;

                if (agent.HasMount && !WhileMounted)
                    return value;

                if (!VrCameraDriver.HasPose)
                    return value;

                // A read-modify-write of our own output must not be rotated a
                // second time. Anything that reads the property back and stores
                // it unchanged arrives here as exactly what we last produced, and
                // rotating that again would compound every frame into a spin.
                if (_haveWritten &&
                    Math.Abs(value.x - _lastWritten.x) < 1e-6f &&
                    Math.Abs(value.y - _lastWritten.y) < 1e-6f)
                {
                    return value;
                }

                // Nothing pressed. Rotating a zero vector is a zero vector, but
                // returning early also keeps _lastWritten meaningful.
                if (Math.Abs(value.x) < 1e-4f && Math.Abs(value.y) < 1e-4f)
                    return value;

                float view = ViewYaw();
                float delta = WrapPi(view - ReferenceYaw(agent));

                Report(agent, view, delta);

                if (Math.Abs(delta) < MinDelta)
                    return value;

                float c = (float)Math.Cos(delta);
                float s = (float)Math.Sin(delta);

                // Yaw here is measured from +Y toward +X, so a direction at yaw t
                // is (sin t, cos t) and this is the rotation that takes a local
                // vector's yaw from t to t + delta.
                Vec2 rotated = new Vec2(value.x * c + value.y * s,
                                        -value.x * s + value.y * c);

                _lastWritten = rotated;
                _haveWritten = true;

                if (!_logged)
                {
                    _logged = true;
                    VrLog.Info("Head-relative movement is on: W now goes where you are "
                               + "looking. It corrects by the angle between the view and "
                               + "the agent's facing, so it does nothing at all whenever "
                               + "head aiming is already keeping the two together.");
                }

                return rotated;
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("Head-relative movement failed; movement goes back to "
                            + "following the game's own facing.", ex);
                return value;
            }
        }

        private static float ViewYaw()
        {
            Mat3 flat = VrMath.YawOnly(VrCameraDriver.HeadFrame.rotation);
            return (float)Math.Atan2(flat.f.x, flat.f.y);
        }

        /// <summary>
        /// The frame the movement vector is expressed in.
        ///
        /// THIS USED TO READ LookDirection, AND HEAD AIMING BROKE IT.
        ///
        /// The correction is viewYaw minus this. When LookDirection was left
        /// alone by us it followed the camera bearing, so reading it worked and
        /// movement followed the head. Then VrHeadAim started WRITING
        /// LookDirection to the head direction - at which point viewYaw minus
        /// lookYaw is zero by construction, the correction vanished, and
        /// movement went back to following the mouse. The feature disabled
        /// itself the moment its sibling started working.
        ///
        /// The agent's own FRAME is the honest reference. MovementInputVector is
        /// an agent-local vector, so the frame it is local TO is the thing to
        /// measure against - and unlike the look direction, nothing in this mod
        /// writes it. That the two used to agree is why the old version worked;
        /// that they no longer do is why it stopped.
        /// </summary>
        private static float ReferenceYaw(Agent agent)
        {
            Mat3 flat = VrMath.YawOnly(agent.Frame.rotation);
            return (float)Math.Atan2(flat.f.x, flat.f.y);
        }

        /// <summary>
        /// Every candidate reference at once, once every few seconds.
        ///
        /// Guessing which frame an engine expresses a vector in has now cost a
        /// round. All three are cheap to print, and between them they say which
        /// one movement is really relative to without another round of inference:
        /// the correct reference is the one whose difference from the view
        /// matches the angle you have to walk at to go where you are looking.
        /// </summary>
        private static void Report(Agent agent, float view, float used)
        {
            if (++_reportTick < 450)
                return;
            _reportTick = 0;

            Vec3 look = agent.LookDirection;

            VrLog.Info(string.Format(
                "Head move: view {0:F1} deg | agent frame {1:F1} | look {2:F1} | "
                + "correcting by {3:F1}. The reference in use is the agent frame; "
                + "if movement still does not follow the head, the one to try is "
                + "the look column.",
                Deg(view), Deg(ReferenceYaw(agent)),
                Deg((float)Math.Atan2(look.x, look.y)), Deg(used)));
        }

        private static float Deg(float radians) => radians * 57.2957795f;

        private static int _reportTick;

        private static float WrapPi(float a)
        {
            while (a > Math.PI) a -= (float)(2.0 * Math.PI);
            while (a < -Math.PI) a += (float)(2.0 * Math.PI);
            return a;
        }

        public static void Reset()
        {
            _haveWritten = false;
            _lastWritten = default;
        }
    }
}
