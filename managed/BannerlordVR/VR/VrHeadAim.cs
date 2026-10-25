using System;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;

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
                    HoldBody(null);
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
                    HoldBody(null);
                    WarnOnce();
                    return;
                }

                // TEST 510: on a ladder the engine faces the body up the rungs; no
                // look, held look or body facing of ours. Active stays as it was, so
                // the anchor keeps reading the body heading.
                if (VrLadder.Gated(agent))
                {
                    HoldBody(null);
                    _haveWrote = _haveFaced = false;
                    return;
                }

                Measure(agent);

                Vec3 look = HeadLook(agent, out bool held);
                if (look.LengthSquared < 1e-6f)
                {
                    Active = false;
                    HoldBody(null);
                    return;
                }

                HoldBody(held ? (Vec3?)look : null);
                if (held)
                    FaceBody(agent, look);

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
                HoldBody(null);
                VrLog.Error("Head aiming failed; the agent goes back to aiming where the "
                            + "game's camera points. Head look and head movement are "
                            + "unaffected.", ex);
            }
        }

        /// <summary>TEST 291. How far the head may turn from the body before the
        /// body follows - and then only by the excess. 0 is the old behaviour, the
        /// body turning with every glance. Live: the VR tab sets it.</summary>
        public static float BodyTurnDeg { get; set; } =
            Math.Max(0f, Math.Min(180f, VrConfig.Float("body_turn_deadzone_deg", 90f)));

        // The yaw the body is told to face, flat and unit, in the ANCHOR's frame
        // (TEST 291d): the right stick turns the anchor, so the body turns with the
        // stick at once and only the physical head turn is held. Held across
        // frames: that holding is the deadzone.
        private static Vec2 _bodyDir;
        private static bool _haveBodyDir;
        private static bool _loggedDeadzone;

        /// <summary>
        /// The head's world forward, flattened to yaw only if pitch is off, with
        /// its yaw held inside the body-turn deadzone (TEST 291).
        ///
        /// Only the look is held, not the engine bearing in VrEngineAim: that
        /// drives the camera's culling, and 90 degrees off it would cull what you
        /// are looking at.
        /// </summary>
        private static Vec3 HeadLook(Agent agent, out bool held)
        {
            held = false;
            Mat3 rotation = VrCameraDriver.HeadFrame.rotation;

            Vec3 f = DrivePitch ? rotation.f : VrMath.YawOnly(rotation).f;
            f.Normalize();

            float flat = (float)Math.Sqrt(f.x * f.x + f.y * f.y);
            if (flat < 1e-3f)
                return f;                       // straight up or down: no yaw to hold

            // Into the anchor's frame: +y its forward, +x its right.
            Vec3 af = VrCameraDriver.Anchor.rotation.f;
            float aLen = (float)Math.Sqrt(af.x * af.x + af.y * af.y);
            Vec2 a = aLen > 1e-4f ? new Vec2(af.x / aLen, af.y / aLen) : new Vec2(0f, 1f);
            Vec2 r = new Vec2(a.y, -a.x);

            Vec2 headWorld = new Vec2(f.x / flat, f.y / flat);
            Vec2 head = new Vec2(headWorld.x * r.x + headWorld.y * r.y,
                                 headWorld.x * a.x + headWorld.y * a.y);

            // Follow fully where vanilla aims with the look: a crossbow or a thrown
            // weapon (and a bow when manual archery is off). Mounted, the body sits
            // the saddle and the horse turns it anyway.
            bool follow = BodyTurnDeg <= 0f || !_haveBodyDir || agent.HasMount || AimsWithLook(agent);

            _bodyDir = follow ? head : Deadzone(_bodyDir, head, BodyTurnDeg * (float)(Math.PI / 180.0));
            _haveBodyDir = true;
            held = !follow;

            if (!_loggedDeadzone && !follow)
            {
                _loggedDeadzone = true;
                VrLog.Info(string.Format(
                    "TEST 291: the body turns only once the head is more than {0:F0} deg off it, "
                    + "and then only by the excess (body_turn_deadzone_deg, or the VR tab).",
                    BodyTurnDeg));
            }

            Vec2 body = new Vec2(r.x * _bodyDir.x + a.x * _bodyDir.y,
                                 r.y * _bodyDir.x + a.y * _bodyDir.y);
            return new Vec3(body.x * flat, body.y * flat, f.z);
        }

        /// <summary>
        /// TEST 291b. The engine's own look override.
        ///
        /// Writing agent.LookDirection alone did not hold the body: the log showed
        /// the look drifting 16-24 deg from our write on foot. The game's
        /// MissionMainAgentController.LookTick sets LookDirection from the camera
        /// bearing in OnPreMissionTick - BEFORE the agents move - and our write
        /// lands after, so the body only ever saw the head. LookTick uses
        /// CustomLookDir instead of the camera whenever it is non-zero, so the held
        /// look goes there. Zero hands the look back to the camera, as vanilla.
        /// </summary>
        private static void HoldBody(Vec3? look)
        {
            Vec3 want = look ?? Vec3.Zero;
            if (!look.HasValue && !_holding)
                return;

            MissionMainAgentController controller =
                Mission.Current?.GetMissionBehavior<MissionMainAgentController>();
            if (controller == null)
            {
                _holding = false;
                return;
            }

            controller.CustomLookDir = want;
            _holding = look.HasValue;
        }

        private static bool _holding;

        /// <summary>
        /// TEST 291e. The body faces the held direction NOW.
        ///
        /// 291d held the look in the stick-turned frame, but a right-stick turn
        /// still lagged: a standing agent turns its body toward its look only once
        /// they are far enough apart - the engine's own deadzone, on top of ours.
        /// Agent.SetMovementDirection is the body's facing, so it is written with
        /// the held look every frame and the engine has nothing left to catch up.
        /// The probe logs how far the engine moved it between frames.
        /// </summary>
        private static void FaceBody(Agent agent, Vec3 look)
        {
            float len = (float)Math.Sqrt(look.x * look.x + look.y * look.y);
            if (len < 1e-4f)
                return;

            Vec2 dir = new Vec2(look.x / len, look.y / len);

            if (_haveFaced)
            {
                Vec2 now = agent.GetMovementDirection();
                float dot = Math.Max(-1f, Math.Min(1f, now.x * _faced.x + now.y * _faced.y));
                _faceDriftSum += (float)Math.Acos(dot);
                if (++_faceTick >= 450)
                {
                    VrLog.Info(string.Format(
                        "TEST 291e: the body's facing moved {0:F1} deg between our writes, on "
                        + "average. Near 0: the engine keeps it and the body turns with the stick "
                        + "at once.", _faceDriftSum / _faceTick * 57.2957795f));
                    _faceTick = 0;
                    _faceDriftSum = 0f;
                }
            }

            agent.SetMovementDirection(in dir);
            _faced = dir;
            _haveFaced = true;
        }

        private static Vec2 _faced;
        private static bool _haveFaced;
        private static float _faceDriftSum;
        private static int _faceTick;

        private static bool AimsWithLook(Agent agent)
        {
            MissionWeapon held = agent.WieldedWeapon;
            if (held.IsEmpty || held.CurrentUsageItem == null || !held.CurrentUsageItem.IsRangedWeapon)
                return false;

            // TEST 373: the manual crossbow aims with the hands, like the manual bow.
            return !(held.CurrentUsageItem.IsBow && VrBow.Enabled)
                   && !(held.CurrentUsageItem.IsCrossBow && VrCrossbow.Enabled);
        }

        /// <summary>
        /// TEST 291. body turned toward head by only as much as puts it limitRad
        /// off; unchanged inside the limit. Both flat and unit.
        /// </summary>
        public static Vec2 Deadzone(Vec2 body, Vec2 head, float limitRad)
        {
            float cross = body.x * head.y - body.y * head.x;
            float dot = body.x * head.x + body.y * head.y;
            float angle = (float)Math.Atan2(cross, dot);

            float excess = Math.Abs(angle) - limitRad;
            if (excess <= 0f)
                return body;

            float turn = Math.Sign(angle) * excess;
            float c = (float)Math.Cos(turn), s = (float)Math.Sin(turn);
            Vec2 r = new Vec2(body.x * c - body.y * s, body.x * s + body.y * c);
            float len = (float)Math.Sqrt(r.x * r.x + r.y * r.y);
            return len > 1e-6f ? new Vec2(r.x / len, r.y / len) : head;
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

        /// <summary>TEST 291d. A recentre means "face where I look": the held body
        /// snaps to the head on the next frame.</summary>
        public static void SnapBody() => _haveBodyDir = false;

        public static void Reset()
        {
            Active = false;
            _haveWrote = false;
            _haveBodyDir = false;
            _haveFaced = false;
            HoldBody(null);
            _driftSum = 0f;
            _driftCount = 0;
            _tick = 0;
        }
    }
}
