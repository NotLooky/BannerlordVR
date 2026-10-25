using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using BannerlordVR.Interop;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 316/317: the held weapon is a solid thing. An enemy's shield and an
    /// enemy's body stop it the way Blade &amp; Sorcery's do: the blade meets the
    /// surface, slides along it, and the hand you see stays with the weapon while
    /// the controller carries on.
    ///
    /// It works on the hand TARGET, after VrWeight's trail and VrTwoHand's steer
    /// and before the arm solve - the seam VrWeight already uses - so the arm, the
    /// drawn weapon and the blade every hit is measured from all carry the stop.
    /// The blade is known in the hand's frame from last tick's drawn weapon
    /// (Learn); the obstacles are this tick's: an enemy's wielded shield as a
    /// plate (VrBlade.TryGetEnemyPlate) and its skeleton as capsules
    /// (VrPhysicalCombat.BodyOf, the same ones the hits are tested on).
    ///
    /// A blade in an obstacle is pushed out to its surface, the push shared
    /// between moving the hand and turning the weapon about it by the weapon's own
    /// numbers (Push): a sword caught at the tip pivots, a mace caught at the head
    /// near the fist mostly stops. Only the push along the surface normal is
    /// taken, so the blade slides. A thin plate cannot be tunnelled: the blade
    /// must stay on the player's side of it, and a crossing within the tick is
    /// caught (Sweep). A body capsule caught between two poses pulls the pose back
    /// to the touch.
    ///
    /// TEST 317: a blow lets the edge bite into that body (collide_bite_*); a stab
    /// bites like a cut. TEST 321: the stuck stab (pinned at its entry) is gone,
    /// on request.
    ///
    /// The hand may be held collide_max_m / collide_max_deg off its controller;
    /// past that the weapon goes through that obstacle (never strands the hand),
    /// and the weapon you see never moves faster than the controller plus
    /// collide_catch_mps (TEST 320) - a release glides back, it does not jump.
    /// </summary>
    internal static class VrCollide
    {
        private static readonly bool Enabled = VrConfig.Bool("collide", true);
        private static readonly bool Bodies = VrConfig.Bool("collide_bodies", true);
        private static readonly bool Friends = VrConfig.Bool("collide_friends", false);
        private static readonly float Skin = Math.Max(0f, VrConfig.Float("collide_skin_m", 0.005f));
        private static readonly float MaxRad = Math.Max(1f, VrConfig.Float("collide_max_deg", 100f)) * DegToRad;

        /// <summary>
        /// TEST 335: the first this much of the blade out from the hand does not
        /// collide, in metres. The hand never moves now (the user: "my main hand
        /// doesn't move when colliding, only the weapon rotates from the grip"), so
        /// a hand pushed into a body would have the hilt inside it - and no turn
        /// can take a point at the hand out, it would only spin the weapon.
        /// </summary>
        private static readonly float HandFree = Math.Max(0f, VrConfig.Float("collide_hand_free_m", 0.12f));

        /// <summary>
        /// TEST 320: how much faster than the controller the weapon you see may
        /// move, m/s. Without it, every release was a jump: 12 contacts on 21:09
        /// went past the cap and the weapon snapped up to 0.67 m back to the
        /// controller in 80 ms. TEST 323: measured on the hand, the grip end and
        /// the tip - a stop turns the weapon now, and a turn limit slowed the very
        /// turn that stops it; a stopped tip moves LESS than the controller would
        /// carry it, so a stop is never slowed, only a catch-up is.
        /// </summary>
        private static readonly float CatchMps = Math.Max(0.1f, VrConfig.Float("collide_catch_mps", 2.0f));

        /// <summary>
        /// TEST 322: the blade's own thickness for the stop, in metres. The hit
        /// test pads the blade to 0.04 so no blow is missed; the stop used the
        /// same pad, so fast swings were bumped by the air a few centimetres off
        /// an arm or a shoulder - "swings hitch mid-air".
        /// </summary>
        private static readonly float CollideEdge = Math.Max(0.002f, VrConfig.Float("collide_edge_m", 0.012f));

        /// <summary>
        /// TEST 322: the push-out is averaged over this long (ms). Re-solved every
        /// tick against animated, overlapping body parts, its depth and direction
        /// flickered - "jitter while touching". It is never let sink deeper than
        /// collide_soft_body_m into a body or collide_soft_shield_m into a shield,
        /// so a hard hit still stops dead.
        /// </summary>
        private static readonly float SoftS = Math.Max(0f, VrConfig.Float("collide_soft_ms", 40f) * 0.001f);
        private static readonly float BodySoftM = Math.Max(0f, VrConfig.Float("collide_soft_body_m", 0.025f));
        private static readonly float PlateSoftM = Math.Max(0f, VrConfig.Float("collide_soft_shield_m", 0.008f));

        private static readonly float BiteCut = Math.Max(0f, VrConfig.Float("collide_bite_cut_m", 0.05f));
        private static readonly float BiteBlunt = Math.Max(0f, VrConfig.Float("collide_bite_blunt_m", 0.01f));
        private static readonly float PressHaptic = Math.Max(0f, Math.Min(1f, VrConfig.Float("collide_haptic_press", 0.15f)));

        /// <summary>
        /// TEST 642: "bash my shield with my weapon". The player's own shield is
        /// a plate for the weapon hand too - solid from whichever face the blade
        /// was on - and a contact closing faster than collide_own_shield_sound_mps
        /// plays the same impact/&lt;weapon&gt;/&lt;shield&gt; clash an enemy shield does.
        /// </summary>
        internal static bool OwnShield = VrConfig.Bool("collide_own_shield", true);   // TEST 645: the VR panel's toggle
        private static readonly float OwnSoundMps = Math.Max(0f, VrConfig.Float("collide_own_shield_sound_mps", 1.5f));

        private const float DegToRad = (float)(Math.PI / 180.0);
        private const int Iterations = 3;
        private const int NoKey = int.MinValue;
        private const int OwnKey = int.MinValue + 1;

        private static Vec3 _ownPrevCentre;
        private static long _ownPrevAt;
        private const int MaxObstacles = 512;

        private sealed class Hand
        {
            // The blade in the frame of the target it was drawn from.
            public bool HaveBlade;
            public Vec3 GripLocal;
            public Vec3 TipLocal;

            /// <summary>TEST 335: where the colliding part of the blade starts -
            /// collide_hand_free_m out from the hand along it.</summary>
            public Vec3 FromLocal;
            public float Edge;

            // This tick's target, for Learn.
            public bool HaveOut;
            public MatrixFrame Out;
            public long OutAt;

            // Last tick's weapon as drawn, and the controller's target then.
            public bool HavePrev;
            public MatrixFrame Prev;
            public MatrixFrame PrevProposed;
            public Vec3 PrevTurn;
            public long PrevAt;
            public Mission PrevMission;

            /// <summary>The obstacle the weapon went through at the cap, until the
            /// blade is clear of it.</summary>
            public int IgnoreKey = NoKey;

            // The contact episode, for the log and the haptics.
            public int Key = NoKey;
            public string Name;
            public bool Plate;
            public long Since;
            public long Last;
            public float Entry;
            public float MaxSep;
            public float MaxTurn;
            public bool Capped;
            public long PressAt;

            // TEST 317: what the last blow left in a body.
            public int BiteAgent = -1;
            public float Bite;
        }

        private struct Obstacle
        {
            public int Key;
            public int Agent;
            public Agent Who; // TEST 421: the name is read at contact, not per capsule per frame
            public bool IsPlate;
            public VrBlade.Plate Plate;
            public float Side;
            public Vec3 A;
            public Vec3 B;
            public float R;
            public bool Solid;

            /// <summary>TEST 642: the obstacle's own velocity (the player's shield
            /// swung into the blade); zero for the rest.</summary>
            public Vec3 V;

            /// <summary>TEST 646: a castle gate's door - no agent behind it.</summary>
            public bool Gate;
        }

        private struct Contact
        {
            public bool Valid;
            public int Index;
            public Vec3 Point;
            public Vec3 Normal;
            public float Depth;
        }

        private static readonly Hand[] _hands = { new Hand(), new Hand() };
        private static readonly Obstacle[] _obstacles = new Obstacle[MaxObstacles];
        private static int _count;
        private static bool _broken;
        private static bool _loggedOn;

        /// <summary>
        /// One hand's target, after the trail and the steer: pushed out of every
        /// shield and body its blade went into. True when it moved, so the two-hand
        /// grip can follow the corrected weapon.
        /// </summary>
        internal static bool Resolve(HandRole role, ref MatrixFrame world)
        {
            Hand h = _hands[(int)role];
            long now = Stopwatch.GetTimestamp();
            MatrixFrame proposed = world;

            h.Out = world;
            h.HaveOut = true;
            h.OutAt = now;

            Mission mission = Mission.Current;
            Agent player = mission?.MainAgent;

            if (!Enabled || _broken || !h.HaveBlade || VrHandCalibration.Capturing
                || player == null || !player.IsActive() || VrSkinnedWeapon.Gliding(role))
            {
                Drop(h, role, now);
                return false;
            }

            try
            {
                float dt = h.HavePrev ? (float)((now - h.PrevAt) / (double)Stopwatch.Frequency) : 0f;
                bool continuous = h.HavePrev && dt > 0f && dt < 0.25f && h.PrevMission == mission;

                if (!continuous)
                    Drop(h, role, now);

                if (!_loggedOn)
                {
                    _loggedOn = true;
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 316: the weapon is solid against enemy shields{0}. TEST 335: the hand stays with "
                        + "the controller and only the weapon turns about the grip, up to {1:F0} deg, then it goes "
                        + "through. collide = 0 turns it off.",
                        Bodies ? " and bodies (TEST 317: a blow bites " + F(BiteCut) + " m)" : string.Empty,
                        MaxRad / DegToRad));
                }

                MatrixFrame s = proposed;
                MatrixFrame prev = continuous ? h.Prev : s;

                Gather(player, mission, s.origin, h, role, now);
                s = Sweep(h, prev, s);

                Contact first = default(Contact);
                MatrixFrame met = s;
                bool resting = false;
                for (int i = 0; i < Iterations; i++)
                {
                    Contact c = Deepest(h, s, prev, out bool inBite);
                    if (i == 0)
                    {
                        first = c;
                        resting = inBite;
                    }

                    if (!c.Valid)
                        break;

                    Push(ref s, c.Point, c.Normal, c.Depth, h, role);
                }

                // TEST 322: the push-out averaged, never too deep.
                if (continuous && SoftS > 0f)
                    s = Soften(h, role, s, proposed, prev, dt);

                // TEST 320: the weapon you see keeps up with the controller exactly,
                // and no faster than that plus collide_catch_* - a release, a body
                // shoving it all glide.
                if (continuous)
                    s = Catch(h.Prev, s, h.PrevProposed, proposed, h.FromLocal, h.TipLocal, dt, CatchMps);

                // TEST 335: the hand is where the controller puts it - only the
                // weapon turns about it.
                s.origin = proposed.origin;

                // The cap: the weapon is never bent too far off the controller.
                Vec3 move = s.origin - proposed.origin;
                Vec3 turn = VrWeight.Between(proposed.rotation, s.rotation);
                float sep = move.Length;
                if (turn.Length > MaxRad)
                {
                    h.Capped = true;
                    if (first.Valid)
                        h.IgnoreKey = _obstacles[first.Index].Key;
                }

                Episode(h, role, first, resting, proposed, prev, met, dt, sep, turn.Length, now);

                h.Prev = s;
                h.PrevProposed = proposed;
                h.PrevTurn = turn;
                h.PrevAt = now;
                h.PrevMission = mission;
                h.HavePrev = true;
                h.Out = s;

                world = s;
                return move.LengthSquared > 1e-10f || turn.LengthSquared > 1e-10f;
            }
            catch (Exception ex)
            {
                _broken = true;
                Drop(h, role, now);
                VrLog.Error("TEST 316: weapon collision failed; it is off for this session. Hits are unaffected.", ex);
                return false;
            }
        }

        /// <summary>This tick's drawn blade, in the frame of the target it was
        /// drawn from - from VrPhysicalCombat, after the drive.</summary>
        internal static void Learn(HandRole role, VrBlade.Segment blade)
        {
            Hand h = _hands[(int)role];
            if (!h.HaveOut || !blade.Valid || Stopwatch.GetTimestamp() - h.OutAt > Stopwatch.Frequency / 10)
            {
                h.HaveBlade = false;
                return;
            }

            h.GripLocal = h.Out.TransformToLocal(blade.GripWorld);
            h.TipLocal = h.Out.TransformToLocal(blade.TipWorld);
            h.FromLocal = From(h.GripLocal, h.TipLocal, HandFree);
            h.Edge = Math.Min(blade.Radius, CollideEdge);
            h.HaveBlade = true;
        }

        /// <summary>The hand holds no blade: nothing to collide.</summary>
        internal static void Forget(HandRole role)
        {
            _hands[(int)role].HaveBlade = false;
        }

        /// <summary>TEST 317: a blow landed on a body - the edge may sink in by the
        /// bite while it stays in contact. A stab bites like a cut (TEST 321).</summary>
        internal static void Struck(HandRole role, Agent victim, DamageTypes type)
        {
            if (!Enabled || !Bodies || _broken || victim == null)
                return;

            Hand h = _hands[(int)role];
            h.BiteAgent = victim.Index;
            h.Bite = type == DamageTypes.Blunt ? BiteBlunt : BiteCut;
        }

        internal static void Reset()
        {
            foreach (Hand h in _hands)
            {
                h.HaveBlade = false;
                h.HaveOut = false;
                h.HavePrev = false;
                h.BiteAgent = -1;
                h.IgnoreKey = NoKey;
                h.Key = NoKey;
            }
        }

        // --- the obstacles -----------------------------------------------------

        /// <summary>Every shield and body the blade could reach this tick.</summary>
        private static void Gather(Agent player, Mission mission, Vec3 hand, Hand h, HandRole role, long now)
        {
            _count = 0;

            float reach = (h.TipLocal - h.FromLocal).Length + 1.5f;
            bool ignoredSeen = false;

            foreach (Agent agent in mission.Agents)
            {
                // TEST 421: distance first - Target and Hostile each make a native
                // IsEnemyOf call, and almost every agent in a battle is out of reach.
                if (agent == null || !agent.IsActive()
                    || (agent.Position.AsVec2 - hand.AsVec2).LengthSquared > reach * reach)
                    continue;

                if (!VrPhysicalCombat.Target(player, agent) || (!Friends && !Hostile(player, agent)))
                    continue;

                if (agent.IsHuman && VrBlade.TryGetEnemyPlate(agent, out VrBlade.Plate plate) && plate.Valid
                    && _count < MaxObstacles)
                {
                    // The player's side of the plate is the free one.
                    float side = Vec3.DotProduct(player.GetEyeGlobalPosition() - plate.Centre, plate.N) >= 0f ? 1f : -1f;
                    _obstacles[_count++] = new Obstacle
                    {
                        Key = ~agent.Index, Agent = agent.Index, Who = agent, IsPlate = true,
                        Plate = plate, Side = side, Solid = true,
                    };
                    ignoredSeen |= ~agent.Index == h.IgnoreKey;
                }

                // TEST 322: people only. A horse is one 0.16 m capsule per bone down
                // to its hooves - fast swings near one were bumped by air.
                if (!Bodies || !agent.IsHuman)
                    continue;

                VrPhysicalCombat.Body body = VrPhysicalCombat.BodyOf(agent);
                if (body == null)
                    continue;

                ignoredSeen |= agent.Index == h.IgnoreKey;

                for (int c = 0; c < body.Count && _count < MaxObstacles; c++)
                {
                    VrPhysicalCombat.Capsule capsule = body.Capsules[c];
                    _obstacles[_count++] = new Obstacle
                    {
                        Key = agent.Index, Agent = agent.Index, Who = agent,
                        A = capsule.A, B = capsule.B, R = capsule.Radius, Solid = true,
                    };
                }
            }

            // TEST 642: the player's own shield, for the weapon hand. The free side is
            // the one last tick's blade was on, so either face stops it.
            if (OwnShield && role == HandRole.Main && _count < MaxObstacles
                && VrBlade.TryGetOwnPlate(player, out VrBlade.Plate own))
            {
                Vec3 was = h.HavePrev ? h.Prev.TransformToParent((h.FromLocal + h.TipLocal) * 0.5f) : hand;
                float dt = (now - _ownPrevAt) / (float)Stopwatch.Frequency;
                Vec3 v = _ownPrevAt != 0 && dt > 1e-3f && dt < 0.25f ? (own.Centre - _ownPrevCentre) * (1f / dt) : Vec3.Zero;
                _ownPrevCentre = own.Centre;
                _ownPrevAt = now;

                _obstacles[_count++] = new Obstacle
                {
                    Key = OwnKey, Agent = player.Index, Who = player, IsPlate = true, Plate = own,
                    Side = Vec3.DotProduct(was - own.Centre, own.N) >= 0f ? 1f : -1f, Solid = true, V = v,
                };
                ignoredSeen |= OwnKey == h.IgnoreKey;
            }

            if (Gates)
                GatherGates(player, mission, hand, h, reach, ref ignoredSeen);

            if (!ignoredSeen)
                h.IgnoreKey = NoKey;
        }

        // --- TEST 646: castle gates are solid like shields ------------------------

        /// <summary>"Make gates have collision like shields." A closed, standing
        /// castle gate is a plate per door body, the same solver as a shield, solid
        /// from the player's side. Open or broken, the weapon passes.</summary>
        internal static bool Gates = VrConfig.Bool("collide_gates", true);

        private const int GateKey = int.MinValue + 16;

        private sealed class GateBodies
        {
            public CastleGate Gate;
            public readonly List<WeakGameEntity> Bodies = new List<WeakGameEntity>();
        }

        private static readonly List<GateBodies> _gates = new List<GateBodies>();
        private static Mission _gatesOf;

        private static readonly AccessTools.FieldRef<CastleGate, SynchedMissionObject> DoorOf =
            AccessTools.FieldRefAccess<CastleGate, SynchedMissionObject>("_door");

        private static void GatherGates(Agent player, Mission mission, Vec3 hand, Hand h, float reach,
                                        ref bool ignoredSeen)
        {
            if (!ReferenceEquals(mission, _gatesOf))
                FindGates(mission);

            Vec3 eye = player.GetEyeGlobalPosition();
            int key = GateKey;
            foreach (GateBodies g in _gates)
            {
                // ponytail: a gate closing is solid from the command on, while its
                // doors still swing shut; the animation's own pose if that shows.
                bool solid = !g.Gate.IsGateOpen;
                foreach (WeakGameEntity body in g.Bodies)
                {
                    int k = key++;
                    if (!solid || _count >= MaxObstacles || !BodyPlate(body, out VrBlade.Plate plate))
                        continue;

                    float far = Math.Max(plate.HalfA, plate.HalfB) + reach;
                    if ((plate.Centre - hand).LengthSquared > far * far)
                        continue;

                    _obstacles[_count++] = new Obstacle
                    {
                        Key = k, Agent = -1, IsPlate = true, Plate = plate, Solid = true, Gate = true,
                        Side = Vec3.DotProduct(eye - plate.Centre, plate.N) >= 0f ? 1f : -1f,
                    };
                    ignoredSeen |= k == h.IgnoreKey;
                }
            }
        }

        /// <summary>The door bodies of every castle gate, once per mission: the
        /// tagged door colliders, else the door entity, else the gate itself.</summary>
        private static void FindGates(Mission mission)
        {
            _gatesOf = mission;
            _gates.Clear();
            foreach (MissionObject o in mission.MissionObjects)
            {
                if (!(o is CastleGate gate) || !gate.GameEntity.IsValid)
                    continue;

                var g = new GateBodies { Gate = gate };
                g.Bodies.AddRange(gate.GameEntity.CollectChildrenEntitiesWithTag("collider_l"));
                g.Bodies.AddRange(gate.GameEntity.CollectChildrenEntitiesWithTag("collider_r"));
                string from = "its door colliders";
                if (g.Bodies.Count == 0)
                {
                    SynchedMissionObject door = DoorOf(gate);
                    if (door != null && door.GameEntity.IsValid)
                    {
                        g.Bodies.Add(door.GameEntity);
                        from = "its door entity";
                    }
                }
                if (g.Bodies.Count == 0)
                {
                    g.Bodies.Add(gate.GameEntity);
                    from = "the gate entity";
                }
                _gates.Add(g);

                var sizes = new List<string>();
                foreach (WeakGameEntity body in g.Bodies)
                    sizes.Add(BodyPlate(body, out VrBlade.Plate p)
                        ? $"{p.HalfA * 2f:F2} x {p.HalfB * 2f:F2} m, {p.HalfN * 2f:F2} m thick"
                        : "not plate-like (passes)");
                Log($"TEST 646 gate '{gate.GameEntity.Name}' ({(gate.IsGateOpen ? "open" : "closed")}): {g.Bodies.Count} "
                    + $"body/bodies from {from} - {string.Join("; ", sizes)}.");
            }
        }

        /// <summary>A body's physics box (its own frame) as a plate: the thinnest
        /// axis is the face's normal. False for a box that is no slab.</summary>
        private static bool BodyPlate(WeakGameEntity body, out VrBlade.Plate plate)
        {
            plate = default(VrBlade.Plate);
            if (!body.IsValid)
                return false;

            body.GetPhysicsMinMax(true, out Vec3 min, out Vec3 max, true);
            Vec3 size = max - min;
            MatrixFrame frame = body.GetGlobalFrame();
            Vec3[] axes = { frame.rotation.s, frame.rotation.f, frame.rotation.u };
            int thin = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
            int a = (thin + 1) % 3, b = (thin + 2) % 3;
            float la = axes[a].Length, lb = axes[b].Length, ln = axes[thin].Length;
            if (la < 1e-4f || lb < 1e-4f || ln < 1e-4f)
                return false;

            plate = new VrBlade.Plate
            {
                Centre = frame.TransformToParent((min + max) * 0.5f),
                A = axes[a] * (1f / la),
                B = axes[b] * (1f / lb),
                N = axes[thin] * (1f / ln),
                HalfA = size[a] * 0.5f * la,
                HalfB = size[b] * 0.5f * lb,
                HalfN = size[thin] * 0.5f * ln,
                Bone = -1,
                Slot = EquipmentIndex.None,
            };
            plate.Valid = plate.HalfN < 0.6f && Math.Min(plate.HalfA, plate.HalfB) >= 0.4f;
            return plate.Valid;
        }

        /// <summary>TEST 646: a gate is collided (TEST 641's own clash then stays quiet).</summary>
        internal static bool Collides(DestructableComponent target) =>
            Enabled && Gates && target != null && target.GameEntity.GetFirstScriptOfType<CastleGate>() != null;

        /// <summary>An enemy, or the horse under one.</summary>
        private static bool Hostile(Agent player, Agent agent)
        {
            if (player.IsEnemyOf(agent))
                return true;

            Agent rider = agent.IsMount ? agent.RiderAgent : null;
            return rider != null && player.IsEnemyOf(rider);
        }

        /// <summary>
        /// The move from last tick's solved pose to this one, cut finely enough
        /// that nothing is stepped over. Decides which plates are solid this tick
        /// (the blade met them from the player's side, not round the edge), and
        /// pulls the pose back to where a blade first touched a body or crossed a
        /// plate it then left behind - a fast blade stops at the first thing it
        /// met, not on the far side of it.
        /// </summary>
        private static MatrixFrame Sweep(Hand h, MatrixFrame prev, MatrixFrame s)
        {
            Vec3 pg = prev.TransformToParent(h.FromLocal);
            Vec3 pt = prev.TransformToParent(h.TipLocal);
            Vec3 sg = s.TransformToParent(h.FromLocal);
            Vec3 st = s.TransformToParent(h.TipLocal);

            // 5 cm at the tip: an arm's half-limit band is under 10 cm wide.
            int steps = Math.Max(1, Math.Min(16, (int)Math.Ceiling((st - pt).Length / 0.05f)));
            float pull = 1f;
            bool clearedIgnored = true;

            for (int o = 0; o < _count; o++)
            {
                ref Obstacle ob = ref _obstacles[o];

                if (ob.Key == h.IgnoreKey)
                {
                    ob.Solid = false;
                    if (Overlap(ob, sg, st, h.Edge))
                        clearedIgnored = false;
                    continue;
                }

                if (ob.IsPlate)
                {
                    // 0 unknown, 1 met on the player's side, 2 first met behind.
                    int state = 0;
                    float lastFront = 0f;
                    bool crossed = false;

                    for (int i = 0; i <= steps; i++)
                    {
                        float f = i / (float)steps;
                        Vec3 g = pg + (sg - pg) * f;
                        Vec3 e = VrPhysicalCombat.SweepTip(pg, pt, sg, st, f);

                        if (!PlateSpan(g, e, ob.Plate, h.Edge, out float u0, out float u1))
                            continue;

                        float least = Sided(g, e, ob.Plate, ob.Side, u0, u1, out float _);
                        if (least >= -ob.Plate.HalfN)
                        {
                            state = 1;
                            lastFront = f;
                        }
                        else if (state == 1)
                        {
                            crossed = true;
                            break;
                        }
                        else
                        {
                            state = 2;
                        }
                    }

                    ob.Solid = state != 2;

                    // Crossed and out past its edge by the end of the tick: back to
                    // the last pose over the face, and the push takes it from there.
                    if (crossed && !PlateSpan(sg, st, ob.Plate, h.Edge, out _, out _))
                        pull = Math.Min(pull, lastFront);

                    continue;
                }

                // TEST 319: a blade resting on a body was skipped here, so the next
                // tick's move went straight through it ("stayed on it 0 ms" at 22
                // m/s). Now any in-between pose sunk past half the limit stops the
                // move at the pose before it - a slide along the surface never
                // sinks that far, a pass through always does - and the push takes
                // it back out the side it came in.
                float limit = Limit(h, ob);
                if (SegmentDistance(sg, st, ob.A, ob.B, out _, out _) < limit * 0.5f)
                    continue;

                float last = 0f;
                for (int i = 1; i < steps; i++)
                {
                    float f = i / (float)steps;
                    Vec3 g = pg + (sg - pg) * f;
                    Vec3 e = VrPhysicalCombat.SweepTip(pg, pt, sg, st, f);

                    if (SegmentDistance(g, e, ob.A, ob.B, out _, out _) < limit * 0.5f)
                    {
                        pull = Math.Min(pull, last);
                        break;
                    }

                    last = f;
                }
            }

            if (clearedIgnored)
                h.IgnoreKey = NoKey;

            if (pull >= 1f)
                return s;

            MatrixFrame back = s;
            // TEST 335: back in angle only - the hand stays with the controller.
            back.rotation = VrWeight.Blend(prev.rotation, s.rotation, pull);
            return back;
        }

        /// <summary>The deepest a solid obstacle holds the blade at this pose.</summary>
        /// <summary>`inBite`: the blade sits in the bite a blow earned - inside
        /// the body's full limit but not past the bitten one - which is still a
        /// contact for the episode, not a release.</summary>
        private static Contact Deepest(Hand h, MatrixFrame s, MatrixFrame prev, out bool inBite)
        {
            Vec3 g = s.TransformToParent(h.FromLocal);
            Vec3 e = s.TransformToParent(h.TipLocal);
            Vec3 pg = prev.TransformToParent(h.FromLocal);
            Vec3 pe = prev.TransformToParent(h.TipLocal);
            Contact best = default(Contact);
            inBite = false;

            for (int o = 0; o < _count; o++)
            {
                Obstacle ob = _obstacles[o];
                if (!ob.Solid)
                    continue;

                Vec3 point;
                Vec3 normal;
                float depth;

                if (ob.IsPlate)
                {
                    if (!PlateSpan(g, e, ob.Plate, h.Edge, out float u0, out float u1))
                        continue;

                    float least = Sided(g, e, ob.Plate, ob.Side, u0, u1, out float u);
                    depth = ob.Plate.HalfN + h.Edge + Skin - least;
                    point = g + (e - g) * u;
                    normal = ob.Plate.N * ob.Side;
                }
                else
                {
                    float d = SegmentDistance(g, e, ob.A, ob.B, out float a, out float b);
                    depth = Limit(h, ob) - d;
                    if (ob.Agent == h.BiteAgent && d < ob.R + h.Edge + Skin)
                        inBite = true;

                    if (depth <= 0f)
                        continue;

                    point = g + (e - g) * a;
                    Vec3 axisPoint = ob.A + (ob.B - ob.A) * b;
                    normal = d > 1e-4f ? (point - axisPoint) * (1f / d) : Away(s.origin, ob.A, ob.B, axisPoint);

                    // Past the capsule's line in one tick: out the side it came
                    // in from, not the far one.
                    float pd = SegmentDistance(pg, pe, ob.A, ob.B, out float pa, out float pb);
                    if (pd > 1e-4f)
                    {
                        Vec3 from = (pg + (pe - pg) * pa - (ob.A + (ob.B - ob.A) * pb)) * (1f / pd);
                        if (Vec3.DotProduct(from, normal) < 0f)
                        {
                            normal = from;
                            depth = Limit(h, ob) + d;
                        }
                    }
                }

                if (depth > 0f && depth > best.Depth)
                {
                    best = new Contact { Valid = true, Index = o, Point = point, Normal = normal, Depth = depth };
                }
            }

            return best;
        }

        /// <summary>How close the blade may come to a capsule's line: its radius,
        /// the edge, the skin - less the bite a blow has earned in that body.</summary>
        private static float Limit(Hand h, Obstacle ob)
        {
            float limit = ob.R + h.Edge + Skin;
            if (ob.Agent == h.BiteAgent)
                limit = Math.Max(0.02f, limit - h.Bite);
            return limit;
        }

        /// <summary>A blade lying on a capsule's own line: out toward the hand,
        /// across the line.</summary>
        private static Vec3 Away(Vec3 hand, Vec3 a, Vec3 b, Vec3 on)
        {
            Vec3 axis = b - a;
            Vec3 out_ = hand - on;
            if (axis.LengthSquared > 1e-8f)
            {
                Vec3 unit = axis.NormalizedCopy();
                out_ -= unit * Vec3.DotProduct(out_, unit);
            }

            return out_.LengthSquared > 1e-8f ? out_.NormalizedCopy() : new Vec3(0f, 0f, 1f);
        }

        /// <summary>Whether the blade is still in an obstacle at all, from either
        /// side - for letting go of one it went through.</summary>
        private static bool Overlap(Obstacle ob, Vec3 g, Vec3 e, float edge)
        {
            if (!ob.IsPlate)
                return SegmentDistance(g, e, ob.A, ob.B, out _, out _) < ob.R + edge;

            if (!PlateSpan(g, e, ob.Plate, edge, out float u0, out float u1))
                return false;

            float s0 = Vec3.DotProduct(g + (e - g) * u0 - ob.Plate.Centre, ob.Plate.N);
            float s1 = Vec3.DotProduct(g + (e - g) * u1 - ob.Plate.Centre, ob.Plate.N);
            float band = ob.Plate.HalfN + edge;
            return Math.Min(s0, s1) <= band && Math.Max(s0, s1) >= -band;
        }

        /// <summary>
        /// TEST 320/323: the weapon's move this tick, from where it was drawn
        /// (prev) toward where the solve wants it (want), held so that no point of
        /// it - the hand, the grip end, the tip - moves further than the
        /// controller carries that point plus catchMps. It tracks the controller
        /// exactly, a stop is never slowed (its points move less), and anything
        /// that would make it jump glides instead. Pure, for the tests.
        /// </summary>
        internal static MatrixFrame Catch(MatrixFrame prev, MatrixFrame want, MatrixFrame prevController,
                                          MatrixFrame controller, Vec3 gripLocal, Vec3 tipLocal, float dt,
                                          float catchMps)
        {
            float f = 1f;
            Limit(ref f, gripLocal, prev, want, prevController, controller, catchMps * dt);
            Limit(ref f, tipLocal, prev, want, prevController, controller, catchMps * dt);

            if (f >= 1f)
                return want;

            // TEST 335: the turn only - the hand is the controller's.
            MatrixFrame s = want;
            s.rotation = VrWeight.Blend(prev.rotation, want.rotation, f);
            return s;
        }

        private static void Limit(ref float f, Vec3 local, MatrixFrame prev, MatrixFrame want,
                                  MatrixFrame prevController, MatrixFrame controller, float slack)
        {
            float move = (want.TransformToParent(local) - prev.TransformToParent(local)).Length;
            float allow = (controller.TransformToParent(local) - prevController.TransformToParent(local)).Length + slack;
            if (move > allow && move > 1e-6f)
                f = Math.Min(f, allow / move);
        }

        /// <summary>
        /// TEST 322: this tick's push-out (raw, as a move and a turn off the
        /// controller's target) averaged with last tick's over collide_soft_ms,
        /// then pushed back out only as far as it sinks past collide_soft_*_m. A
        /// flicker of a centimetre or two between ticks is averaged away; an impact
        /// still stops within the tolerance.
        /// </summary>
        private static MatrixFrame Soften(Hand h, HandRole role, MatrixFrame raw, MatrixFrame proposed,
                                          MatrixFrame prev, float dt)
        {
            float a = dt / (SoftS + dt);
            Vec3 turn = h.PrevTurn + (VrWeight.Between(proposed.rotation, raw.rotation) - h.PrevTurn) * a;

            MatrixFrame soft = proposed;
            soft.rotation = VrWeight.Turn(proposed.rotation, turn);

            for (int i = 0; i < Iterations; i++)
            {
                Contact c = Deepest(h, soft, prev, out _);
                if (!c.Valid)
                    break;

                float allow = _obstacles[c.Index].IsPlate ? PlateSoftM : BodySoftM;
                if (c.Depth <= allow)
                    break;

                Push(ref soft, c.Point, c.Normal, c.Depth - allow, h, role);
            }

            return soft;
        }

        // --- the push ----------------------------------------------------------

        private static void Push(ref MatrixFrame s, Vec3 point, Vec3 normal, float depth, Hand h, HandRole role)
        {
            Push(ref s, point, normal, depth);
        }

        /// <summary>
        /// TEST 335: turns the weapon (frame s) about the hand so that `point` on
        /// it comes `depth` out along `normal` - the hand never moves. The turn is
        /// exact, not a small-angle guess: the point keeps its distance from the
        /// hand and its height along the normal goes from r.n to r.n + depth. A
        /// thrust straight in has no plane to turn in, so it glances off sideways
        /// (about the hand's side axis); a point too close to the hand to come out
        /// by turning is turned as far as it can. Only the normal is asked for, so
        /// motion along the surface goes on. Pure, for the tests.
        /// </summary>
        internal static void Push(ref MatrixFrame s, Vec3 point, Vec3 normal, float depth)
        {
            Vec3 r = point - s.origin;
            float length = r.Length;
            if (length < 1e-4f || depth <= 0f)
                return;

            Vec3 u = r * (1f / length);
            float from = Math.Max(-1f, Math.Min(1f, Vec3.DotProduct(u, normal)));
            float to = Math.Max(-1f, Math.Min(1f, from + depth / length));
            float angle = (float)(Math.Acos(from) - Math.Acos(to));
            if (angle <= 0f)
                return;

            Vec3 axis = Vec3.CrossProduct(u, normal);
            if (axis.LengthSquared < 1e-8f)
            {
                axis = Vec3.CrossProduct(u, s.rotation.s);
                if (axis.LengthSquared < 1e-8f)
                    axis = Vec3.CrossProduct(u, s.rotation.u);
            }

            s.rotation = VrWeight.Turn(s.rotation, axis.NormalizedCopy() * angle);
        }

        /// <summary>TEST 335: the point `free` metres out from the hand (the
        /// local origin) along the blade, toward the tip; the grip end when the
        /// hand is further out than that. Pure, for the tests.</summary>
        internal static Vec3 From(Vec3 grip, Vec3 tip, float free)
        {
            Vec3 axis = tip - grip;
            float length = axis.Length;
            if (length < 1e-4f)
                return grip;

            Vec3 unit = axis * (1f / length);
            float hand = Vec3.DotProduct(grip * -1f, unit);
            float at = Math.Max(0f, Math.Min(length, hand + free));
            return grip + unit * at;
        }

        // --- plates, pure for the tests -----------------------------------------

        /// <summary>The part of segment g -> e over a plate's face (its extent grown
        /// by `grow`), as [u0, u1] along the segment; false when none is.</summary>
        internal static bool PlateSpan(Vec3 g, Vec3 e, VrBlade.Plate plate, float grow, out float u0, out float u1)
        {
            u0 = 0f;
            u1 = 1f;

            Vec3 d = e - g;
            Vec3 o = g - plate.Centre;

            return Clip(Vec3.DotProduct(o, plate.A), Vec3.DotProduct(d, plate.A), plate.HalfA + grow, ref u0, ref u1)
                   && Clip(Vec3.DotProduct(o, plate.B), Vec3.DotProduct(d, plate.B), plate.HalfB + grow, ref u0, ref u1);
        }

        /// <summary>o + d u kept within [-h, h].</summary>
        private static bool Clip(float o, float d, float h, ref float u0, ref float u1)
        {
            if (Math.Abs(d) < 1e-9f)
                return Math.Abs(o) <= h;

            float a = (-h - o) / d;
            float b = (h - o) / d;
            if (a > b)
            {
                float swap = a;
                a = b;
                b = swap;
            }

            u0 = Math.Max(u0, a);
            u1 = Math.Min(u1, b);
            return u0 <= u1;
        }

        /// <summary>The least height over the plate's mid-plane, on the free
        /// side's sign, of the span [u0, u1]; `u` is where it is.</summary>
        internal static float Sided(Vec3 g, Vec3 e, VrBlade.Plate plate, float side, float u0, float u1, out float u)
        {
            float s0 = side * Vec3.DotProduct(g + (e - g) * u0 - plate.Centre, plate.N);
            float s1 = side * Vec3.DotProduct(g + (e - g) * u1 - plate.Centre, plate.N);

            u = s0 <= s1 ? u0 : u1;
            return Math.Min(s0, s1);
        }

        /// <summary>Whether the blade touches the plate from either side, within
        /// `pad` of its faces - for the hit test. `along` is where on the blade.</summary>
        internal static bool PlateTouch(Vec3 g, Vec3 e, VrBlade.Plate plate, float pad, out float along)
        {
            along = 0f;
            if (!PlateSpan(g, e, plate, pad, out float u0, out float u1))
                return false;

            float s0 = Vec3.DotProduct(g + (e - g) * u0 - plate.Centre, plate.N);
            float s1 = Vec3.DotProduct(g + (e - g) * u1 - plate.Centre, plate.N);
            float band = plate.HalfN + pad;

            if (Math.Min(s0, s1) > band || Math.Max(s0, s1) < -band)
                return false;

            // Where it crosses the mid-plane, or the end nearer it.
            if (s0 * s1 < 0f)
                along = u0 + (u1 - u0) * (s0 / (s0 - s1));
            else
                along = Math.Abs(s0) <= Math.Abs(s1) ? u0 : u1;

            return true;
        }

        // --- the episode: the log line and the hands' feel ----------------------

        private static void Episode(Hand h, HandRole role, Contact first, bool resting, MatrixFrame proposed,
                                    MatrixFrame prev, MatrixFrame met, float dt, float sep, float turned, long now)
        {
            if (!first.Valid)
            {
                // Sunk in a bite is still on the body.
                if (resting && h.Key != NoKey)
                {
                    h.Last = now;
                    h.MaxSep = Math.Max(h.MaxSep, sep);
                    h.MaxTurn = Math.Max(h.MaxTurn, turned);
                }
                else if (h.Key != NoKey && now - h.Last > Stopwatch.Frequency * 15 / 100)
                {
                    End(h, role, now);
                }

                return;
            }

            Obstacle ob = _obstacles[first.Index];

            if (ob.Key != h.Key)
            {
                End(h, role, now);

                // How fast the touching point was going into it: where the
                // controller would have put it against where it was.
                Vec3 local = met.TransformToLocal(first.Point);
                Vec3 went = proposed.TransformToParent(local) - prev.TransformToParent(local) - ob.V * dt;
                float speed = dt > 1e-4f ? Math.Max(0f, -Vec3.DotProduct(went, first.Normal)) / dt : 0f;

                if ((ob.Key == OwnKey || ob.Gate) && speed >= OwnSoundMps)
                    Clang(role, ob, first.Point, speed);

                h.Key = ob.Key;
                h.Name = ob.Gate ? "a gate" : ob.Who?.Name ?? "?";
                h.Plate = ob.IsPlate;
                h.Since = now;
                h.Entry = speed;
                h.MaxSep = 0f;
                h.MaxTurn = 0f;
                h.Capped = false;

                Pulse(role, Math.Max(0.25f, Math.Min(1f, speed / 6f)), 0.04f);
                h.PressAt = now + Stopwatch.Frequency / 10;
            }

            h.Last = now;
            h.MaxSep = Math.Max(h.MaxSep, sep);
            h.MaxTurn = Math.Max(h.MaxTurn, turned);

            if (PressHaptic > 0f && now >= h.PressAt)
            {
                Pulse(role, PressHaptic, 0.03f);
                h.PressAt = now + Stopwatch.Frequency / 10;
            }
        }

        private static void End(Hand h, HandRole role, long now)
        {
            if (h.Key == NoKey)
                return;

            Log(string.Format(CultureInfo.InvariantCulture,
                "{0} contact: your {1} weapon met '{2}'s {3} at {4:F1} m/s and stayed on it {5:F0} ms; it was turned "
                + "up to {6:F0} deg about the grip{8}.",
                h.Plate ? "TEST 316" : "TEST 317", role, h.Name, h.Name == "a gate" ? "door" : h.Plate ? "shield" : "body", h.Entry,
                (h.Last - h.Since) * 1000.0 / Stopwatch.Frequency, h.MaxTurn / DegToRad, h.MaxSep,
                h.Capped ? " - past the cap, so it went through" : string.Empty));

            if (!h.Plate && h.BiteAgent == h.Key)
                h.BiteAgent = -1;

            h.Key = NoKey;
        }

        /// <summary>Not colliding this tick: the state goes, the episode ends.</summary>
        private static void Drop(Hand h, HandRole role, long now)
        {
            h.HavePrev = false;
            h.PrevTurn = Vec3.Zero;
            h.IgnoreKey = NoKey;
            h.BiteAgent = -1;
            End(h, role, now);
        }

        /// <summary>TEST 642/646: the weapon met the player's own shield or a gate -
        /// the clash an enemy shield gets (VrPhysicalCombat.Strike). Your own shield
        /// is felt in the shield hand too; a gate sounds as wood.</summary>
        private static void Clang(HandRole role, Obstacle ob, Vec3 at, float speed)
        {
            Agent player = Mission.Current?.MainAgent;
            if (player == null)
                return;
            try
            {
                EquipmentIndex held = role == HandRole.Main ? player.GetPrimaryWieldedItemIndex()
                                                            : player.GetOffhandWieldedItemIndex();
                WeaponComponentData weapon = held != EquipmentIndex.None ? player.Equipment[held].CurrentUsageItem : null;
                string struck = ob.Gate ? "wood_shield"
                                        : VrCombatSound.Material(player.Equipment[ob.Plate.Slot].CurrentUsageItem, true);
                VrCombatSound.Clash(Mission.Current, at, VrCombatSound.Material(weapon, false), struck, player.Index, -1);
            }
            catch (Exception)
            {
                // A sound is never worth a throw.
            }

            if (!ob.Gate)
                Pulse(HandRole.Off, Math.Max(0.25f, Math.Min(1f, speed / 6f)), 0.04f);
            Log((ob.Gate ? "TEST 646 gate: your weapon struck a gate" : "TEST 642 own shield: your weapon struck your shield")
                + " at " + F(speed) + " m/s.");
        }

        private static void Pulse(HandRole role, float amplitude, float seconds)
        {
            try
            {
                VrHand controller = role == HandRole.Main ? VrHandSlots.MainController : VrHandSlots.OffController;
                NativeMethods.bvr_trigger_haptic((int)controller, amplitude, seconds);
            }
            catch (Exception)
            {
                // A pulse is never worth a throw.
            }
        }

        private static float SegmentDistance(Vec3 p1, Vec3 q1, Vec3 p2, Vec3 q2, out float s, out float t)
        {
            return VrPhysicalCombat.SegmentDistance(p1, q1, p2, q2, out s, out t);
        }

        private static void Log(string text)
        {
            VrLog.Info(text);
        }

        private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
