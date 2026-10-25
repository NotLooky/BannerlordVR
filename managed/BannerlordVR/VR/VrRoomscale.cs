using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 254. Room scale: physically walking in the room walks the character.
    ///
    /// THE ONE WAY THIS CAN BE DONE WITHOUT BREAKING THE GAME
    ///
    /// The agent's position is the engine's. Writing it directly (TeleportToPosition)
    /// skips collision, navmesh, the gait and every movement lock the engine keeps,
    /// and the engine reasserts its own position on the next tick anyway. So the
    /// physical offset is turned into the game's own MOVEMENT INPUT instead - the
    /// left stick's analog answer, exactly what the player's own stick produces -
    /// and the engine walks the character with its own collision, its own
    /// animation and its own speed. See VrGamepad for the seam itself.
    ///
    /// WHY NOT Agent.MovementInputVector. The first version wrote the property
    /// directly from the setter seam. The live log answered it: the offset grew
    /// to 0.86 m and stayed there for seconds with the command at 0.9, the agent
    /// never moved, and the probe never saw one millimetre consumed. The stick is
    /// the path the player already walks with, so the stick is the path this
    /// takes; anything else is a second guess stacked on a working one.
    ///
    /// THE CONTROL LAW
    ///
    ///   offset   raw head position - standing neutral - what the avatar walked off
    ///   velocity the raw head's own derivative, the player's real walking speed
    ///
    ///   input = (kp * (|offset| - dead) + kd * velocity-toward-the-offset) / speed
    ///
    /// kp is a position servo: it walks the offset back to nothing, which is what
    /// stops the player drifting out of their room over a session. kd is the
    /// feed-forward that makes a physical step a step: without it the character
    /// only moves once the offset has already grown, which reads as everything
    /// lagging a quarter of a second behind the player's legs.
    ///
    /// THE CONSUMPTION LOOP, WHICH IS THE LOAD-BEARING PART
    ///
    /// As the avatar walks, the camera driver's room shift is fed the distance it
    /// walked ALONG the offset - and only along it, and never more than the offset
    /// itself. That is what stops the step being counted twice: the view stays
    /// where the player left it, and the body catches up underneath rather than
    /// shooting past. It is also why stick movement is untouched - a stick walks
    /// the avatar with no physical offset, so there is nothing to consume and the
    /// world slides exactly as it always did.
    ///
    /// WHEN IT STANDS DOWN
    ///
    /// Mounted (there the movement vector is a throttle and a rein, not a
    /// direction - see VrHeadMovement), deployment, menus the player is reading,
    /// no agent, no pose. When the avatar cannot move at all - a wall, an attack
    /// lock - the offset grows until the soft recentre slides the world instead,
    /// which is the honest failure mode: the body never stretches, the room does.
    /// </summary>
    public static class VrRoomscale
    {
        public static bool Enabled { get; set; } = VrConfig.Bool("roomscale", true);

        public static bool Debug { get; } = VrConfig.Bool("roomscale_debug", false);

        /// <summary>
        /// TEST 297: the body is MOVED under the head, not walked after it. A real
        /// step lands on the character the same frame, as instant as the stick,
        /// instead of the servo below chasing it through the engine's gait (TEST
        /// 296b's log: the offset overshot from -0.19 to +0.22 m inside a second,
        /// and the body walked 3 cm a frame at right angles to a 0.30 m offset it
        /// never closed). Collision is the navmesh's own: the move is clipped at
        /// its edge, and what a wall stops is left to the soft recentre. The cost
        /// is the legs: a moved body plays no walk cycle. 0 is the servo.
        /// </summary>
        public static bool Instant { get; } = VrConfig.Bool("roomscale_instant", true);

        private static float _stepped;

        /// <summary>Below this the player is standing still, not walking.</summary>
        private static readonly float Dead = VrConfig.Float("roomscale_dead", 0.05f);

        /// <summary>TEST 448: the instant step's own floor. It took mag - 0.05, so
        /// the body always trailed the head by 5 cm ("a couple of cm before the
        /// body moves, my chest ahead of me" walking back). 5 mm is tracking noise,
        /// which must not teleport the agent every frame. The servo keeps Dead.</summary>
        private static readonly float StepDead = Math.Max(0f, VrConfig.Float("roomscale_step_dead", 0.005f));

        /// <summary>Input per metre of remaining offset, per second. 3.0 walks
        /// a 0.2 m offset at about 0.6 m/s, which is what closes the lag on a
        /// real step.</summary>
        private static readonly float GainP = VrConfig.Float("roomscale_kp", 3.0f);

        /// <summary>Input per metre per second of head motion. 1.2 lets a step
        /// land as a step, with a little lead on top of the servo.</summary>
        private static readonly float GainD = VrConfig.Float("roomscale_kd", 1.2f);

        /// <summary>The speed an input magnitude of 1.0 is taken to mean.</summary>
        private static readonly float Speed = Math.Max(0.2f, VrConfig.Float("roomscale_speed", 2.0f));

        /// <summary>Ceiling on the injected magnitude, so a fast step cannot
        /// command a sprint.</summary>
        private static readonly float MaxInput = VrConfig.Float("roomscale_max", 0.9f);

        /// <summary>Offset at which the world starts to slide instead of the body
        /// stretching. Deliberately under an arm's reach: the hands are anchored
        /// to the head, so an offset the avatar cannot walk off is an offset that
        /// pulls the arm out of its socket.</summary>
        private static readonly float RecentreAt = VrConfig.Float("roomscale_recentre_m", 0.55f);

        /// <summary>How fast the world may slide, metres per second.</summary>
        private static readonly float RecentreRate = VrConfig.Float("roomscale_recentre_rate", 1.8f);

        /// <summary>
        /// The movement this frame, in the agent's own frame (X right, Y forward),
        /// magnitude 0..1 - the same axes and the same range the left stick uses.
        /// Read by VrGamepad, which adds it to the left stick's ANALOG answer so
        /// the engine walks the character through its own movement path: collision,
        /// animation, movement locks and all. See class note.
        /// </summary>
        public static Vec2 Stick { get; private set; }

        private static Vec3 _lastAgent;
        private static bool _haveAgent;
        private static long _lastTick;
        private static int _debugTick;

        /// <summary>
        /// Per frame, from AfterMissionTick: measures the avatar's own travel,
        /// consumes it from the player's offset, and publishes the input the
        /// movement seam will add. One frame behind the tick that uses it, which
        /// at 90 Hz is below the acceleration the engine applies anyway.
        /// </summary>
        public static void Tick(Agent agent)
        {
            Stick = Vec2.Zero;

            if (!Enabled || agent == null || !agent.IsActive())
            {
                _haveAgent = false;
                return;
            }

            if (!Walkable(agent))
            {
                _haveAgent = false;
                return;
            }

            Vec3 offset = VrCameraDriver.RoomOffset;
            Vec3 velocity = VrCameraDriver.RoomVelocity;

            // TEST 352c: a bend is not a walk - the soft recentre and the servo see
            // only the step part (as Step does), or a deep bend slid the world.
            if (VrBodyIk.HeadDrives && VrHeadBody.TryStepPart(offset, out Vec3 step))
                offset = new Vec3(step.x, step.y, offset.z, -1f);

            // The avatar's own travel since the last tick, in body axes.
            Vec3 delta = agent.Position - _lastAgent;
            if (!_haveAgent || delta.Length > 2f)
                delta = Vec3.Zero;              // first sample, or a teleport

            _lastAgent = agent.Position;
            _haveAgent = true;

            Vec3 bodyDelta = VrCameraDriver.RoomRotation.TransformToLocal(
                new Vec3(delta.x, delta.y, 0f, -1f));

            Vec3 flat = new Vec3(offset.x, offset.y, 0f, -1f);
            float mag = flat.Length;

            // Consume what the avatar walked off, along the offset, never more
            // than the offset itself. TEST 297: an instant step consumed its own
            // move as it made it (Step), so measuring it again would count it twice.
            float consumed = Instant ? 0f : Consumed(offset, bodyDelta);

            if (consumed > 0f)
            {
                Vec3 dir = flat * (1f / mag);
                VrCameraDriver.ConsumeRoomOffset(dir * consumed);
            }

            // Soft recentre: beyond the play area, or when the avatar cannot move
            // at all, the world slides rather than the body stretching.
            float excess = mag - RecentreAt;

            if (excess > 0f && mag > 1e-4f)
            {
                float slide = Math.Min(excess, RecentreRate * Dt());
                VrCameraDriver.ConsumeRoomOffset(flat * (1f / mag) * slide);
            }

            // TEST 296: in the head's axes, and past the game's own stick deadzone.
            if (!Instant)
                Stick = Deliver(Command(offset, velocity), HeadOffBody(), mag > Dead, EngineDead);

            Trace(offset, velocity, consumed, delta);
        }

        private static bool Walkable(Agent agent)
        {
            return agent != null && agent.IsActive()
                && ReferenceEquals(agent, Mission.Current?.MainAgent)
                && !VrHeadBody.Saddled(agent)     // TEST 449: never mid-dismount
                && !VrRein.Mounting               // TEST 472b: nor while a saddle grip mounts
                && !VrLadder.Gated(agent)         // TEST 510: no step, no teleport to the ground, on a ladder
                // TEST 341: a helm, oar, rope or siege weapon pins its user to a
                // standing point; a real step must not teleport them off it.
                && !agent.IsUsingGameObject
                && VrCameraDriver.HasPose
                && VrCameraDriver.RoomCalibrated
                && VrMissionPhase.VrDrivesLook
                && !VrMissionPhase.UiMenuOpen
                && !VrMissionPhase.UiWantsScreen;
        }

        /// <summary>
        /// TEST 297. From the game's own control tick (MovementInputPatch), which
        /// runs before the engine moves the agents - so the engine's tick puts the
        /// visuals, and so the eye the camera anchors on, at the new spot in the
        /// same frame, and the consumed shift below cancels it exactly: the view
        /// never moves, the body arrives under it. Everything past roomscale_dead
        /// is taken in one go; inside it is head sway, which never moves the body.
        /// </summary>
        public static void Step(Agent agent)
        {
            if (!Enabled || !Instant || !Walkable(agent))
                return;

            Vec3 offset = VrCameraDriver.RoomOffset;

            // TEST 352c: under the head-driven body only the part of the head's move
            // that no bend can reach is a step; the rest is the body bending over its
            // own feet (it used to walk the agent forward under every bend).
            if (VrBodyIk.HeadDrives && VrHeadBody.TryStepPart(offset, out Vec3 step))
                offset = step;

            float mag = (float)Math.Sqrt(offset.x * offset.x + offset.y * offset.y);
            if (mag <= StepDead)
                return;

            float take = (mag - StepDead) / mag;
            Vec3 world = VrCameraDriver.RoomRotation.TransformToParent(
                new Vec3(offset.x * take, offset.y * take, 0f, -1f));

            Scene scene = Mission.Current?.Scene;
            if (scene == null)
                return;

            // TEST 297b: the destination must stand on the navmesh - the check the
            // game's own LordsHallFightMissionController makes. A tick's move is
            // centimetres, so a point test is a wall test. 297 used
            // GetLastPointOnNavigationMeshFromPositionToDestination, which nothing
            // in the game calls: it walked forward and refused every step BACK
            // (log: offset -0.17 / -0.28 m, "stepped 0").
            Vec3 from = agent.Position;
            Vec3 wanted = new Vec3(from.x + world.x, from.y + world.y, from.z, -1f);
            PathFaceRecord face = PathFaceRecord.NullFaceRecord;
            scene.GetNavMeshFaceIndex(ref face, wanted, true);

            if (face.FaceIndex == -1)
                return;     // a wall: the offset stays, and the soft recentre slides the world

            Vec2 moved = new Vec2(world.x, world.y);

            agent.TeleportToPosition(new WorldPosition(scene, wanted).GetGroundVec3());

            Vec3 local = VrCameraDriver.RoomRotation.TransformToLocal(new Vec3(moved.x, moved.y, 0f, -1f));
            VrCameraDriver.ConsumeRoomOffset(new Vec3(local.x, local.y, 0f, -1f));
            _stepped += moved.Length;
        }

        /// <summary>
        /// TEST 296. The game's own analog deadzone on the left stick. The log's
        /// "stick (0.21,-0.06), agent applied (0.00,0.00)" lines: room-scale
        /// commands under about 0.3 never reached the engine, so a short real step
        /// left the body 10-20 cm behind until the soft recentre slid the world -
        /// the player "not aligned with the centre of the body".
        /// </summary>
        private static readonly float EngineDead =
            Math.Max(0f, Math.Min(0.6f, VrConfig.Float("roomscale_engine_dead", 0f)));

        /// <summary>
        /// TEST 296. What the stick finally carries.
        ///
        /// The command is in the body frame's axes (RoomRotation), but the movement
        /// input is now turned from the look to the HEAD (TEST 291c), so it is put
        /// into the head's axes first: headOffBodyRad is the head's yaw off the body
        /// frame, counter-clockwise. Then, while there is a real offset to walk off
        /// (lift), the magnitude is carried past the game's deadzone so a small
        /// step is still a step. Head sway alone (no offset) is never lifted, or
        /// the body would shuffle while the player aims.
        /// </summary>
        public static Vec2 Deliver(Vec2 stick, float headOffBodyRad, bool lift, float engineDead)
        {
            float m = (float)Math.Sqrt(stick.x * stick.x + stick.y * stick.y);
            if (m < 1e-5f)
                return Vec2.Zero;

            float c = (float)Math.Cos(-headOffBodyRad), s = (float)Math.Sin(-headOffBodyRad);
            Vec2 r = new Vec2(stick.x * c - stick.y * s, stick.x * s + stick.y * c);

            if (!lift)
                return r;

            float lifted = Math.Min(1f, engineDead + (1f - engineDead) * m);
            return new Vec2(r.x * lifted / m, r.y * lifted / m);
        }

        /// <summary>The head's yaw off the room's body frame, counter-clockwise.</summary>
        private static float HeadOffBody()
        {
            Vec3 b = VrCameraDriver.RoomRotation.f;
            Vec3 h = VrCameraDriver.HeadFrame.rotation.f;
            return (float)Math.Atan2(b.x * h.y - b.y * h.x, b.x * h.x + b.y * h.y);
        }

        /// <summary>
        /// The movement command from the player's offset and velocity, both in
        /// body axes (X right, Y forward). Pure, so the regression tests can call
        /// it: returns the input vector in the agent's own frame, magnitude 0..1.
        /// </summary>
        public static Vec2 Command(Vec3 offset, Vec3 velocity)
        {
            Vec3 flat = new Vec3(offset.x, offset.y, 0f, -1f);
            Vec3 vel = new Vec3(velocity.x, velocity.y, 0f, -1f);

            float mag = flat.Length;
            Vec2 dir;
            float speed;

            if (mag > Dead)
            {
                dir = new Vec2(flat.x / mag, flat.y / mag);

                // The feed-forward counts only motion that is OPENING the offset:
                // walking back toward the body must not be commanded as a walk.
                float closing = flat.x * vel.x / mag + flat.y * vel.y / mag;
                speed = GainP * (mag - Dead) + GainD * Math.Max(0f, closing);
            }
            else
            {
                float vLen = vel.Length;

                if (vLen < 0.05f)
                    return Vec2.Zero;

                dir = new Vec2(vel.x / vLen, vel.y / vLen);
                speed = GainD * vLen;
            }

            if (speed <= 0f)
                return Vec2.Zero;

            float input = speed / Speed;
            if (input > MaxInput)
                input = MaxInput;

            return new Vec2(dir.x * input, dir.y * input);
        }

        /// <summary>
        /// How much of the avatar's own movement this frame walks off the offset:
        /// its component along the offset, clamped to the offset's own length.
        /// Pure.
        /// </summary>
        public static float Consumed(Vec3 offset, Vec3 agentBodyDelta)
        {
            Vec3 flat = new Vec3(offset.x, offset.y, 0f, -1f);
            float mag = flat.Length;

            if (mag < 1e-5f)
                return 0f;

            float along = (flat.x * agentBodyDelta.x + flat.y * agentBodyDelta.y) / mag;

            if (along <= 0f)
                return 0f;

            return Math.Min(along, mag);
        }

        /// <summary>Seconds since the previous tick, for the recentre rate and
        /// the debug line. Clamped, so a hitch cannot slide the world.</summary>
        private static float Dt()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long then = _lastTick;
            _lastTick = now;

            if (then == 0L)
                return 1f / 90f;

            float dt = (float)((now - then) / (double)System.Diagnostics.Stopwatch.Frequency);
            if (dt <= 0f || dt > 0.25f)
                return 1f / 90f;

            return dt;
        }

        private static void Trace(Vec3 offset, Vec3 velocity, float consumed, Vec3 agentDelta)
        {
            if (!Debug)
                return;

            if (++_debugTick < 90)
                return;
            _debugTick = 0;

            // The agent's own read-back is what says whether the injection lands:
            // MovementInputVector is where the game's controller puts the stick,
            // so if this reads (0,0) while Stick is not, the seam is not reaching
            // the engine; if it reads the combined value, it is.
            Vec2 applied = Mission.Current?.MainAgent?.MovementInputVector ?? Vec2.Zero;

            VrLog.Info(string.Format(
                "Room scale: offset ({0:F2},{1:F2},{2:F2}) m, head speed ({3:F2},{4:F2}) m/s, "
                + "stick ({5:F2},{6:F2}), agent applied ({7:F2},{8:F2}), walked {9:F3} m, "
                + "consumed {10:F3} m this frame, stepped {11:F3} m instantly since the last "
                + "line (TEST 297).",
                offset.x, offset.y, offset.z,
                velocity.x, velocity.y,
                Stick.x, Stick.y,
                applied.x, applied.y,
                agentDelta.Length, consumed, _stepped));
            _stepped = 0f;
        }

        public static void Reset()
        {
            Stick = Vec2.Zero;
            _haveAgent = false;
            _lastAgent = Vec3.Zero;
            _lastTick = 0L;
            _debugTick = 0;
        }
    }
}
