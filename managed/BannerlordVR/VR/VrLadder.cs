using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using BannerlordVR.Interop;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Ladders. Climbing is the engine's own navmesh climb: SiegeLadder marks its
    /// on-wall faces as ladder, and the agent plays act_climb_ladder on them. There
    /// is no managed "is on a ladder", so Climbing asks both.
    ///
    /// TEST 510: the shake on a ladder. While climbing (ladder_gates), VrHeadAim,
    /// VrHeadMovement and VrRoomscale stand down - the engine faces the body up the
    /// rungs, and our look, our held body facing, the stick turned to the head and
    /// the room-scale teleport to the ground all fought it. ladder_probe logs the
    /// numbers that name whatever shake is left.
    ///
    /// TEST 511: climbing by hand. A free grip on a raised ladder, or anywhere while
    /// climbing, latches that world point. With the stick at rest, the hand's height
    /// below its latch is the stick's forward, full at ladder_full_m: pull the hand
    /// down and the body climbs until the hand is back on its latch. Both points are
    /// world points and the hand rides the body, so it is a closed loop with no
    /// tracking-space math, and the engine keeps its navmesh, its climb animation
    /// and its dismounts at the top and the bottom. The newer of two latched hands
    /// drives (hand over hand); the stick still climbs, and wins while it is used.
    ///
    /// TEST 520: a siege ladder's shape is its own - the ladder object's root bone
    /// (SiegeLadder reads its angle from the same bone's forward axis), its length
    /// from its box - and its rungs are found once by rays across it, every 2 cm up
    /// the centre line, against what a missile hits (the 'precise' body that lets
    /// arrows through between the rungs). Only a rung takes a grip, and the hand is
    /// drawn on it. A grip at the foot climbs too: pulling down walks the body into
    /// the ladder until the engine's climb takes it - no stick first. No rungs found:
    /// the centre line takes it, as in 511b.
    /// </summary>
    internal static class VrLadder
    {
        private static readonly bool GatesOn = VrConfig.Bool("ladder_gates", true);
        private static readonly bool HandOn = VrConfig.Bool("ladder_hand", true);
        private static readonly bool Probe = VrConfig.Bool("ladder_probe", true);
        // TEST 511b: from the ladder's own centre line, not its box - a ladder is
        // ~0.8 m wide, so 0.45 reaches its rails. The fallback when no rung is found.
        private static readonly float ReachM = VrConfig.Float("ladder_line_m", 0.45f);
        private static readonly float RungReachM = VrConfig.Float("ladder_rung_reach_m", 0.12f);
        private static readonly float RungHalfM = VrConfig.Float("ladder_rung_half_m", 0.30f);
        private static readonly float FullM = Math.Max(0.02f, VrConfig.Float("ladder_full_m", 0.10f));
        private static readonly float DeadM = Math.Max(0f, VrConfig.Float("ladder_dead_m", 0.02f));
        private static readonly float Sign = VrConfig.Float("ladder_sign", 1f) < 0f ? -1f : 1f;

        private const float EndHoldS = 0.3f;    // after a climb ends, at the top or the bottom
        private const float OutOfReachM = 1.0f; // the controller this far from its latch lets go
        private const float LaddersEveryS = 2f;
        private const float ScanStepM = 0.02f;
        private const float RungMaxM = 0.10f;   // a longer run of hits is a rail or the wall, not a rung

        private static readonly FieldInfo LadderObjectField = AccessTools.Field(typeof(SiegeLadder), "_ladderObject");

        private static Mission _mission;
        private static int _climbAction = -1;
        private static readonly List<SiegeLadder> _ladders = new List<SiegeLadder>();
        private static float _laddersAt = float.MinValue;

        /// <summary>TEST 520: a ladder in its own (moving) entity's frame - its
        /// centre line, the rungs' direction, and the rung centres. 511b took the
        /// line from the on-wall navmesh faces, which gave none at the 11:30 castle
        /// ("0 ladder line(s)"), so a grip at the foot never held.</summary>
        private sealed class Shape
        {
            public WeakGameEntity Entity;
            public Vec3 Bottom, Top, Side;
            public readonly List<Vec3> Rungs = new List<Vec3>();
        }

        private static readonly Dictionary<SiegeLadder, Shape> _shapes = new Dictionary<SiegeLadder, Shape>();

        private static float _checkedAt = float.NaN;
        private static int _checkedAgent = -1;
        private static bool _climbing;
        private static string _why = "";

        private static readonly bool[] _latched = new bool[2];
        private static readonly Vec3[] _latch = new Vec3[2];    // where the hand is drawn (the rung)
        private static readonly Vec3[] _ref = new Vec3[2];      // where the controller was at the grab
        private static readonly float[] _latchAt = new float[2];
        private static readonly bool[] _climbed = new bool[2];
        private static float _climbSeenAt;

        private static float _yaw;
        private static bool _haveYaw;
        private static float _yawMax;
        private static long _traceAt;
        private static long _logAt;
        private static bool _broken;

        /// <summary>Whether this hand holds the ladder.</summary>
        internal static bool Holds(VrHand hand) => _latched[(int)hand];

        /// <summary>TEST 510: on a ladder with the gates on - head aim, head-relative
        /// movement and room scale leave the agent to the engine.</summary>
        internal static bool Gated(Agent agent) => GatesOn && Climbing(agent);

        /// <summary>The stick's forward for a hand <paramref name="below"/> metres
        /// under its latch: nothing inside <paramref name="dead"/> (TEST 511b: the
        /// climb hunted about the latch), then linear, full <paramref name="full"/>
        /// past it. Pure.</summary>
        internal static float StickFor(float below, float dead, float full, float sign)
        {
            float past = Math.Max(0f, Math.Abs(below) - dead);
            return sign * Math.Sign(below) * Math.Min(1f, past / Math.Max(1e-3f, full));
        }

        /// <summary>Distance from <paramref name="p"/> to the segment a-b. Pure.</summary>
        internal static float SegmentDistance(Vec3 p, Vec3 a, Vec3 b)
        {
            Vec3 ab = b - a;
            float len2 = ab.LengthSquared;
            float t = len2 > 1e-8f ? Math.Max(0f, Math.Min(1f, Vec3.DotProduct(p - a, ab) / len2)) : 0f;
            return (p - (a + ab * t)).Length;
        }

        /// <summary>Whether the agent is on a ladder: the climb action on either
        /// channel, or standing on a SiegeLadder's on-wall faces. Once per mission
        /// tick; the action code is resolved inside a mission, never earlier (the
        /// arena crash cached act_* codes before the animations had loaded).</summary>
        internal static bool Climbing(Agent agent)
        {
            if (agent == null || _broken)
                return false;

            Mission mission = Mission.Current;
            if (mission == null)
                return false;

            try
            {
                if (!ReferenceEquals(mission, _mission))
                    Forget(mission);

                float now = mission.CurrentTime;
                if (now == _checkedAt && agent.Index == _checkedAgent)
                    return _climbing;

                _checkedAt = now;
                _checkedAgent = agent.Index;

                if (_climbAction < 0)
                    _climbAction = MBAnimation.GetActionCodeWithName("act_climb_ladder");

                string why = null;
                if (_climbAction >= 0 && (agent.GetCurrentAction(0).Index == _climbAction
                                          || agent.GetCurrentAction(1).Index == _climbAction))
                {
                    why = "act_climb_ladder";
                }
                else
                {
                    int face = agent.GetCurrentNavigationFaceId();
                    foreach (SiegeLadder ladder in Ladders(mission, now))
                    {
                        if (ladder.OnWallNavMeshId > 0 && ladder.OnWallNavMeshId == face)
                        {
                            why = "ladder face " + face;
                            break;
                        }
                    }
                }

                bool climbing = why != null;
                if (climbing != _climbing && Probe)
                    VrLog.Info(climbing
                        ? "TEST 510 ladder: climbing (" + why + ")" + (GatesOn
                            ? "; head aim, head-relative movement and room scale stand down."
                            : "; ladder_gates = 0, nothing stands down.")
                        : "TEST 510 ladder: off the ladder (was " + _why + ").");

                _climbing = climbing;
                if (climbing)
                    _why = why;
                return climbing;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return false;
            }
        }

        private static List<SiegeLadder> Ladders(Mission mission, float now)
        {
            if (now - _laddersAt < LaddersEveryS && now >= _laddersAt)
                return _ladders;

            _laddersAt = now;
            _ladders.Clear();
            foreach (MissionObject o in mission.ActiveMissionObjects)
            {
                if (o is SiegeLadder ladder)
                    _ladders.Add(ladder);
            }
            return _ladders;
        }

        /// <summary>
        /// TEST 520, once per raised ladder: its centre line from the ladder
        /// object's root bone (forward = up the ladder, side = along the rungs) and
        /// its box, and its rungs. A ray crosses the ladder every ScanStepM up the
        /// line against what a missile hits; a run of hits on the ladder no longer
        /// than RungMaxM is a rung, centred between its front and back faces. Only
        /// raised (its bodies are where they will stay); a failure is remembered.
        /// </summary>
        private static Shape ShapeOf(SiegeLadder ladder, Scene scene)
        {
            if (_shapes.TryGetValue(ladder, out Shape known))
                return known;
            if (ladder.State != SiegeLadder.LadderState.OnWall)
                return null;

            Shape shape = null;
            long start = Stopwatch.GetTimestamp();
            int rays = 0;
            if (LadderObjectField?.GetValue(ladder) is SynchedMissionObject obj && obj.GameEntity.Skeleton != null)
            {
                WeakGameEntity e = obj.GameEntity;
                MatrixFrame g = e.GetGlobalFrame();
                MatrixFrame bone = g.TransformToParent(e.Skeleton.GetBoneEntitialFrameWithIndex(0));
                Vec3 up = Unit(bone.rotation.f);
                if (up.z < 0f)
                    up = -up;
                Vec3 side = Unit(bone.rotation.s);
                Vec3 n = Unit(Vec3.CrossProduct(up, side));
                if (n.z < 0f)
                    n = -n;     // the climber's side: a leaning ladder's open face looks up
                Vec3 o = bone.origin;

                float t0 = float.MaxValue, t1 = float.MinValue;
                Vec3 lo = e.GlobalBoxMin, hi = e.GlobalBoxMax;
                for (int c = 0; c < 8; c++)
                {
                    Vec3 corner = new Vec3((c & 1) == 0 ? lo.x : hi.x, (c & 2) == 0 ? lo.y : hi.y, (c & 4) == 0 ? lo.z : hi.z, -1f);
                    float t = Vec3.DotProduct(corner - o, up);
                    t0 = Math.Min(t0, t);
                    t1 = Math.Max(t1, t);
                }
                t0 = Math.Max(t0, -2f);
                t1 = Math.Min(t1, 20f);

                shape = new Shape
                {
                    Entity = e,
                    Bottom = g.TransformToLocalNonOrthogonal(o + up * t0),
                    Top = g.TransformToLocalNonOrthogonal(o + up * t1),
                    Side = g.rotation.TransformToLocal(side),
                };

                const float across = 0.3f;
                float runFrom = float.NaN, runTo = 0f;
                for (float t = t0; t <= t1 + ScanStepM; t += ScanStepM)
                {
                    Vec3 p = o + up * t;
                    rays++;
                    bool hit = t <= t1 && Hits(scene, p + n * across, p - n * across, ladder.GameEntity, out _);
                    if (hit)
                    {
                        if (float.IsNaN(runFrom))
                            runFrom = t;
                        runTo = t;
                        continue;
                    }
                    if (float.IsNaN(runFrom))
                        continue;

                    if (runTo - runFrom <= RungMaxM)
                    {
                        // Centred between its faces: the same ray from the other side.
                        float tc = (runFrom + runTo) * 0.5f;
                        Vec3 c = o + up * tc;
                        rays += 2;
                        Vec3 rung = c;
                        if (Hits(scene, c + n * across, c - n * across, ladder.GameEntity, out float front)
                            && Hits(scene, c - n * across, c + n * across, ladder.GameEntity, out float back))
                            rung = c + n * ((back - front) * 0.5f);
                        shape.Rungs.Add(g.TransformToLocalNonOrthogonal(rung));
                    }
                    runFrom = float.NaN;
                }
            }

            _shapes[ladder] = shape;
            if (Probe)
                VrLog.Info(shape == null
                    ? "TEST 520 ladder '" + ladder.GameEntity.Name + "': no ladder object or skeleton - only the climb takes a grip."
                    : string.Format(CultureInfo.InvariantCulture,
                        "TEST 520 ladder '{0}': line {1:F2} m, {2} rung(s) from {3} rays in {4:F1} ms{5}.",
                        ladder.GameEntity.Name, (shape.Top - shape.Bottom).Length, shape.Rungs.Count, rays,
                        (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency, RungList(shape)));
            return shape;
        }

        /// <summary>Whether the segment a-b hits <paramref name="root"/> (or a child
        /// of it) first, as a missile would; how far from a.</summary>
        private static bool Hits(Scene scene, Vec3 a, Vec3 b, WeakGameEntity root, out float distance)
        {
            if (!scene.RayCastForClosestEntityOrTerrain(a, b, out distance, out WeakGameEntity hit, 0.01f,
                                                         BodyFlags.CommonCollisionExcludeFlagsForMissile))
                return false;
            for (int k = 0; k < 10 && hit.IsValid; k++, hit = hit.Parent)
            {
                if (hit == root)
                    return true;
            }
            return false;
        }

        /// <summary>The nearest graspable point of a ladder to <paramref name="at"/>:
        /// on a rung when it has any, else on its line.</summary>
        private static float Nearest(Shape shape, Vec3 at, out Vec3 point)
        {
            MatrixFrame g = shape.Entity.GetGlobalFrame();
            point = at;
            if (shape.Rungs.Count == 0)
            {
                Vec3 a = g.TransformToParent(shape.Bottom), b = g.TransformToParent(shape.Top);
                return SegmentDistance(at, a, b);
            }

            Vec3 side = Unit(g.rotation.TransformToParent(shape.Side)) * RungHalfM;
            float best = float.MaxValue;
            foreach (Vec3 local in shape.Rungs)
            {
                Vec3 c = g.TransformToParent(local);
                Vec3 p = Closest(at, c - side, c + side);
                float d = (at - p).Length;
                if (d < best)
                {
                    best = d;
                    point = p;
                }
            }
            return best;
        }

        /// <summary>The point of the segment a-b nearest p. Pure.</summary>
        internal static Vec3 Closest(Vec3 p, Vec3 a, Vec3 b)
        {
            Vec3 ab = b - a;
            float len2 = ab.LengthSquared;
            float t = len2 > 1e-8f ? Math.Max(0f, Math.Min(1f, Vec3.DotProduct(p - a, ab) / len2)) : 0f;
            return a + ab * t;
        }

        private static string RungList(Shape shape)
        {
            if (shape.Rungs.Count == 0)
                return " - none: the line takes a grip (ladder_line_m)";
            var text = new System.Text.StringBuilder(", heights up the line:");
            Vec3 axis = shape.Top - shape.Bottom;
            float len = Math.Max(1e-4f, axis.Length);
            foreach (Vec3 r in shape.Rungs)
                text.Append(' ').Append(F(Vec3.DotProduct(r - shape.Bottom, axis) / len));
            return text.ToString();
        }

        private static Vec3 Unit(Vec3 v)
        {
            float l = v.Length;
            return l > 1e-6f ? v * (1f / l) : v;
        }

        /// <summary>
        /// TEST 511, from VrGrab with the hand empty: latches <paramref name="at"/>
        /// while climbing, or within ladder_reach_m of a raised ladder's box.
        /// </summary>
        internal static bool TryGrab(Agent agent, VrHand hand, Vec3 at)
        {
            if (!HandOn || _broken || agent == null || Mission.Current == null)
                return false;

            try
            {
                bool climbing = Climbing(agent);
                Mission mission = Mission.Current;

                // TEST 520: the nearest ladder's rung (or, with none found, its line).
                Shape near = null;
                float d = float.MaxValue;
                Vec3 on = at;
                foreach (SiegeLadder ladder in Ladders(mission, mission.CurrentTime))
                {
                    if (!climbing && ladder.State != SiegeLadder.LadderState.OnWall)
                        continue;
                    Shape shape = ShapeOf(ladder, mission.Scene);
                    if (shape == null)
                        continue;

                    float dl = Nearest(shape, at, out Vec3 p);
                    if (dl < d)
                    {
                        d = dl;
                        near = shape;
                        on = p;
                    }
                }

                // On a siege ladder the grip must be on a rung; climbing a ladder that
                // has no shape (a siege tower's) any grip holds, as in 511.
                bool onLadder = near != null && d < 3f;
                float reach = onLadder && near.Rungs.Count > 0 ? RungReachM : ReachM;
                if (onLadder ? d > reach : !climbing)
                {
                    if (near != null && d < 2f && Probe && Stopwatch.GetTimestamp() >= _logAt)
                    {
                        _logAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                        VrLog.Info("TEST 520 grab: " + hand + " grip " + F(d) + " m from the nearest "
                                   + (near.Rungs.Count > 0 ? "rung" : "point of the ladder's line") + " (reach " + F(reach) + ").");
                    }
                    return false;
                }

                int i = (int)hand;
                _latched[i] = true;
                // TEST 511b: without the view's stair glide, as the servo reads the hand
                // (the climb's grabs read 6-7 cm above their latch and climbed down).
                // TEST 520: drawn on the rung; the climb measures from where the
                // controller was, so taking hold never moves the body.
                if (!onLadder)
                    on = at;
                _latch[i] = new Vec3(on.x, on.y, on.z - VrCameraDriver.StairShift, -1f);
                _ref[i] = new Vec3(at.x, at.y, at.z - VrCameraDriver.StairShift, -1f);
                _latchAt[i] = Mission.Current.CurrentTime;
                _climbed[i] = climbing;
                Pulse(hand, 0.5f, 0.05f);

                VrLog.Info("TEST 520 grab: " + hand + " took the ladder at z " + F(at.z) + " ("
                           + (climbing ? "climbing, " + _why + ", " : "at the foot, ") + (onLadder
                               ? F(d) + " m from its " + (near.Rungs.Count > 0 ? "rung" : "line") + ")."
                               : "no siege ladder near - a tower's?)."));
                return true;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return false;
            }
        }

        internal static void Let(VrHand hand) => Let(hand, null);

        private static void Let(VrHand hand, string why)
        {
            int i = (int)hand;
            if (!_latched[i])
                return;

            _latched[i] = false;
            Pulse(hand, 0.25f, 0.03f);
            if (Probe)
                VrLog.Info("TEST 511: " + hand + " let go of the ladder" + (why == null ? "." : " (" + why + ")."));
        }

        /// <summary>
        /// From MovementInputPatch, after the head-relative correction:
        /// <paramref name="stick"/> is the game's own input, <paramref name="input"/>
        /// what would be written. Returns what to write - the hand's climb when a
        /// hand holds and the stick rests - and keeps the 510 probe.
        /// </summary>
        internal static Vec2 Servo(Agent agent, Vec2 stick, Vec2 input)
        {
            if (_broken || agent == null)
                return input;

            try
            {
                bool climbing = Climbing(agent);
                Vec2 result = HandOn && (_latched[0] || _latched[1]) ? Hands(agent, climbing, stick, input) : input;

                if (!climbing)
                    _haveYaw = false;
                else if (Probe)
                    Trace(agent, result);

                return result;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return input;
            }
        }

        private static Vec2 Hands(Agent agent, bool climbing, Vec2 stick, Vec2 input)
        {
            float now = Mission.Current.CurrentTime;
            if (climbing)
                _climbSeenAt = now;

            int drive = -1;
            for (int i = 0; i < 2; i++)
            {
                if (!_latched[i])
                    continue;

                if (climbing)
                    _climbed[i] = true;
                else if (_climbed[i] && now - _climbSeenAt > EndHoldS)
                {
                    Let((VrHand)i, "the climb ended");
                    continue;
                }

                if (drive < 0 || _latchAt[i] > _latchAt[drive])
                    drive = i;
            }

            // The stick wins while it is used.
            if (drive < 0 || Math.Abs(stick.x) > 1e-4f || Math.Abs(stick.y) > 1e-4f)
                return input;

            if (NativeMethods.bvr_get_input_state(out BvrInputState state) != (int)BvrStatus.Ok)
                return input;

            VrHand hand = (VrHand)drive;
            BvrHandState hs = hand == VrHand.Left ? state.Left : state.Right;
            if (hs.IsActive == 0)
                return input;

            Vec3 at = VrWeaponHands.HandPoint(hand, hs);

            // TEST 520 (replaces 511's 1.5 s at the foot): walked or reached away.
            if ((at - _ref[drive] - new Vec3(0f, 0f, VrCameraDriver.StairShift, -1f)).Length > OutOfReachM)
            {
                Let(hand, "out of reach");
                return input;
            }

            float below = _ref[drive].z - at.z;
            float y = StickFor(below, DeadM, FullM, Sign);

            if (Probe && Stopwatch.GetTimestamp() >= _logAt)
            {
                _logAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 511 climb: {0} hand {1:F3} m below its latch -> forward {2:F2} ({3}; ladder_sign {4:F0}, "
                    + "full at {5:F2} m). Pulling down should raise the body.",
                    hand, below, y, climbing ? "climbing" : "at the foot", Sign, FullM));
            }

            // TEST 520: at the foot a pull walks the body to the ladder - the engine's
            // climb starts when it reaches it, and then forward is up.
            if (!climbing)
                return y > 0f ? Toward(agent, _latch[drive], y) : input;
            return new Vec2(0f, y);
        }

        /// <summary>The movement input that walks <paramref name="agent"/> toward
        /// <paramref name="target"/> at <paramref name="speed"/> - the engine applies
        /// it along the look (TEST 291c), yaw from +y toward +x.</summary>
        private static Vec2 Toward(Agent agent, Vec3 target, float speed)
        {
            Vec3 w = target - agent.Position;
            Vec3 look = agent.LookDirection;
            if (w.x * w.x + w.y * w.y < 0.0025f || look.x * look.x + look.y * look.y < 1e-6f)
                return new Vec2(0f, speed);
            float d = (float)(Math.Atan2(w.x, w.y) - Math.Atan2(look.x, look.y));
            return new Vec2((float)Math.Sin(d) * speed, (float)Math.Cos(d) * speed);
        }

        /// <summary>TEST 511: from VrWeaponHands before the arm solve - the hand on
        /// the ladder is drawn on its latch.</summary>
        internal static void AdjustHand(VrHand controller, ref MatrixFrame world)
        {
            int i = (int)controller;
            if (_latched[i])
                world.origin = _latch[i] + new Vec3(0f, 0f, VrCameraDriver.StairShift, -1f);   // drawn in this frame's view
        }

        /// <summary>TEST 510: every 0.25 s while climbing, the numbers that name a
        /// shake - the body's height, the visuals' height and tilt, the largest
        /// facing turn in one tick, the input written, the camera lead and its
        /// finish.</summary>
        private static void Trace(Agent agent, Vec2 input)
        {
            Vec2 dir = agent.GetMovementDirection();
            float yaw = (float)Math.Atan2(dir.x, dir.y);
            if (_haveYaw)
            {
                float d = yaw - _yaw;
                while (d > Math.PI) d -= (float)(2.0 * Math.PI);
                while (d < -Math.PI) d += (float)(2.0 * Math.PI);
                _yawMax = Math.Max(_yawMax, Math.Abs(d));
            }
            _yaw = yaw;
            _haveYaw = true;

            if (Stopwatch.GetTimestamp() < _traceAt)
                return;
            _traceAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;

            MatrixFrame vis = agent.AgentVisuals?.GetFrame() ?? MatrixFrame.Identity;
            Vec3 up = vis.rotation.u;
            float tilt = (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, up.z / Math.Max(1e-4f, up.Length)))) * 180.0 / Math.PI);
            Vec3 lead = VrCameraDriver.Lead, fin = VrCameraDriver.ViewFinish;

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 510 ladder ({0}): agent z {1:F3}, visuals z {2:F3} tilt {3:F1} deg, facing turn max {4:F1} "
                + "deg/tick, input ({5:F2}, {6:F2}), lead ({7:F3}, {8:F3}, {9:F3}), finish ({10:F3}, {11:F3}, {12:F3}).",
                _why, agent.Position.z, vis.origin.z, tilt, _yawMax * 57.2957795f, input.x, input.y,
                lead.x, lead.y, lead.z, fin.x, fin.y, fin.z));
            _yawMax = 0f;
        }

        private static void Forget(Mission mission)
        {
            _mission = mission;
            _climbAction = -1;
            _ladders.Clear();
            _laddersAt = float.MinValue;
            _checkedAt = float.NaN;
            _climbing = false;
            _latched[0] = _latched[1] = false;
            _haveYaw = false;
            _shapes.Clear();
        }

        private static void Fail(Exception ex)
        {
            _broken = true;
            _climbing = false;
            _latched[0] = _latched[1] = false;
            VrLog.Error("TESTs 510/511: the ladder code failed; ladders are vanilla for this session.", ex);
        }

        private static void Pulse(VrHand hand, float amplitude, float seconds)
        {
            try
            {
                NativeMethods.bvr_trigger_haptic((int)hand, amplitude, seconds);
            }
            catch (Exception)
            {
                // A pulse is never worth a throw.
            }
        }

        private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);

        private static string V(Vec3 v) => F(v.x) + ", " + F(v.y) + ", " + F(v.z);
    }
}
