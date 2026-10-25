using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using BannerlordVR.Interop;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 371/372. What happens when the hand lets go of what it holds
    /// (VrGrab, grip_hold = 1).
    ///
    /// TEST 371: THE THROW. The runtime's own fused hand velocity, taken into
    /// the world with the body's or the horse's motion, is kept for the last
    /// few hundred ms. The release reads the PEAK of the last throw_window_ms - people
    /// slow down before they open the hand, so the release frame itself
    /// undershoots - and its direction from the samples near that peak. A
    /// throwing weapon (a usage that is ranged AND consumable: javelins,
    /// throwing axes and knives, stones) released faster than throw_min_mps
    /// becomes the engine's own missile, through the same entry point manual
    /// archery shoots with: engine flight, damage, shields, sticking, pickup.
    /// Its speed is the weapon's vanilla speed times how hard the hand threw,
    /// so a full throw is a vanilla throw and the engine scales a soft one's
    /// damage itself. The direction is bent at most throw_assist_deg toward the
    /// enemy whose ballistic line is nearest the hand's - tracking noise is a
    /// degree or two, so a throw at someone lands on them, and a throw that was
    /// not at anyone is left alone.
    ///
    /// TEST 372: EVERYTHING ELSE FLIES AND COMES HOME. A sword, a shield, a
    /// bow, a crossbow - or a throwing weapon let go too slowly to be a throw -
    /// goes to its holster on the engine side at once (VrGrab sheathes it). Only
    /// a copy of what the hand showed flies: thrown with the hand's velocity,
    /// it falls, lands on whatever the ray finds (terrain, a wall, a deck),
    /// lies there drop_rest_s, and glides home. It is ours, so it can never be
    /// lost, picked up by anyone, or despawned; and it does no damage (the
    /// user's choice). Grabbing it on the way catches it back into the hand.
    /// </summary>
    internal static class VrThrow
    {
        private static readonly bool Probe = VrConfig.Bool("throw_probe", true);
        private static readonly float MinMps = VrConfig.Float("throw_min_mps", 2.5f);
        private static readonly float FullMps = Math.Max(0.5f, VrConfig.Float("throw_full_mps", 6.0f));
        private static readonly float MinPower = VrConfig.Float("throw_min_power", 0.35f);
        private static readonly float WindowS = VrConfig.Float("throw_window_ms", 100f) / 1000f;
        private static readonly float SpawnForward = VrConfig.Float("throw_spawn_forward_m", 0.10f);
        private static readonly float AssistDeg = VrConfig.Float("throw_assist_deg", 5f);
        private static readonly float ConeDeg = VrConfig.Float("throw_assist_cone_deg", 15f);
        private static readonly float RangeM = VrConfig.Float("throw_assist_range_m", 60f);
        private static readonly float Gravity = VrConfig.Float("throw_gravity", 9.81f);
        private static readonly float RestS = VrConfig.Float("drop_rest_s", 1.0f);
        private static readonly float ReturnMps = Math.Max(0.5f, VrConfig.Float("drop_return_mps", 4f));
        private static readonly float CatchM = VrConfig.Float("grab_reach_m", 0.35f);

        private const int History = 32;     // ~0.3 s at 90 Hz; Solve reads only the window
        private const float NearPeak = 0.7f;
        private const float MaxOmega = 15f;
        // TEST 513: the longest a let-go copy falls before it flies home, even in
        // mid-air (6 s was only for "nothing below it"; 0.2 s is a short fall).
        private static readonly float FallS = VrConfig.Float("drop_fall_s", 6f);
        private const float DegToRad = (float)(Math.PI / 180.0);

        private const string ThrowSound = "event:/mission/combat/missile/foley/javelinrelease";

        // Per controller (VrHand), a ring of world velocities and their times.
        private static readonly Vec3[,] _vel = new Vec3[2, History];
        private static readonly float[,] _time = new float[2, History];
        private static readonly int[] _head = new int[2];
        private static readonly int[] _count = new int[2];

        private sealed class Flight
        {
            public EquipmentIndex Slot;
            public string ItemId;
            public GameEntity Entity;
            public readonly List<MetaMesh> Copies = new List<MetaMesh>();
            public MatrixFrame Frame;           // the weapon as seen: its mesh frame, world
            public Vec3 Velocity;
            public Vec3 Omega;
            public int Phase;                   // 0 flying, 1 resting, 2 going home
            public float PhaseAt;
            public float StartedAt;
            public MatrixFrame From;
            public float Dur;
        }

        private static readonly List<Flight> _flights = new List<Flight>();
        private static readonly GameEntity[] _entity = new GameEntity[4];
        private static Mission _mission;
        private static float _last;
        private static bool _dumpedFirst;
        private static bool _broken;

        /// <summary>One controller's velocity this tick, from VrGrab.</summary>
        internal static void Feed(Agent agent, VrHand hand, BvrHandState state)
        {
            if (state.IsActive == 0)
                return;

            int i = (int)hand;
            Vec3 v = VrCameraDriver.Anchor.rotation.TransformToParent(VrMath.ToBannerlord(state.LinearVelocity))
                     * VrCameraDriver.HandScale
                     + VrPhysicalCombat.Carried(agent);

            _head[i] = (_head[i] + 1) % History;
            _vel[i, _head[i]] = v;
            _time[i, _head[i]] = Now();
            _count[i] = Math.Min(History, _count[i] + 1);
        }

        /// <summary>
        /// The hand let go of <paramref name="slot"/>. A throwing weapon thrown
        /// hard enough becomes a missile; anything else flies as a copy and comes
        /// home. The caller sheathes the slot either way, after this.
        /// <paramref name="seen"/> is the entity frame the weapon is shown at,
        /// when that is not VrSkinnedWeapon's (the crossbow's own placement).
        /// Returns whether it was thrown (TEST 474: a rock that was not is dropped).
        /// </summary>
        internal static bool Release(Agent agent, VrHand hand, EquipmentIndex slot, HandRole role, MatrixFrame? seen)
        {
            if (_broken)
                return false;

            try
            {
                int i = (int)hand;
                int n = _count[i];
                var times = new float[n];
                var vels = new Vec3[n];
                for (int k = 0; k < n; k++)
                {
                    int at = (_head[i] - n + 1 + k + History) % History;
                    times[k] = _time[i, at];
                    vels[k] = _vel[i, at];
                }

                bool solved = Solve(times, vels, n, Now(), WindowS, out float peak, out Vec3 dir);

                MissionWeapon weapon = agent.Equipment[slot];
                int usage = ThrownUsage(weapon);
                if (solved && peak >= MinMps && usage >= 0 && weapon.Amount > 0
                    && Throw(agent, hand, slot, role, usage, peak, dir, seen))
                    return true;

                Toss(agent, slot, role, solved ? dir * peak : Vec3.Zero, seen, peak, usage >= 0);
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("TEST 371: throwing failed; a release now only puts the weapon away.", ex);
            }

            return false;
        }

        /// <summary>
        /// The release solve: the peak speed among the samples no older than
        /// <paramref name="window"/>, and the direction of the samples at or
        /// above NearPeak of it, weighted by their speed (their vector sum).
        /// Pure; the math tests call it.
        /// </summary>
        internal static bool Solve(float[] times, Vec3[] vels, int count, float now, float window,
                                   out float peak, out Vec3 dir)
        {
            peak = 0f;
            dir = Vec3.Zero;
            int best = -1;
            for (int k = 0; k < count; k++)
            {
                if (now - times[k] > window)
                    continue;

                float s = vels[k].Length;
                if (s > peak)
                {
                    peak = s;
                    best = k;
                }
            }

            if (best < 0 || peak < 1e-4f)
                return false;

            Vec3 sum = Vec3.Zero;
            for (int k = 0; k < count; k++)
            {
                if (now - times[k] <= window && vels[k].Length >= NearPeak * peak)
                    sum += vels[k];
            }

            dir = sum.Length > 1e-4f ? sum.NormalizedCopy() : vels[best] * (1f / peak);
            return true;
        }

        /// <summary>
        /// The low-arc launch direction that carries a missile at
        /// <paramref name="speed"/> over <paramref name="delta"/> (z up) under
        /// gravity <paramref name="g"/>. False when it cannot reach, or the target
        /// is straight above or below. Pure; the math tests call it.
        /// </summary>
        internal static bool Ballistic(Vec3 delta, float speed, float g, out Vec3 dir)
        {
            dir = Vec3.Zero;
            float x = (float)Math.Sqrt(delta.x * delta.x + delta.y * delta.y);
            float y = delta.z;
            if (x < 1e-3f || speed <= 0f)
                return false;

            float v2 = speed * speed;
            float disc = v2 * v2 - g * (g * x * x + 2f * y * v2);
            if (disc < 0f)
                return false;

            float tan = (v2 - (float)Math.Sqrt(disc)) / (g * x);
            float cos = 1f / (float)Math.Sqrt(1f + tan * tan);
            dir = new Vec3(delta.x / x * cos, delta.y / x * cos, tan * cos);
            return true;
        }

        /// <summary><paramref name="from"/> turned toward <paramref name="to"/> by
        /// at most <paramref name="maxRad"/> (both unit). Pure; the math tests
        /// call it.</summary>
        internal static Vec3 Bend(Vec3 from, Vec3 to, float maxRad)
        {
            float angle = Angle(from, to);
            if (angle <= maxRad || angle < 1e-5f)
                return to;

            float t = maxRad / angle;
            float sin = (float)Math.Sin(angle);
            return (from * ((float)Math.Sin((1f - t) * angle) / sin) + to * ((float)Math.Sin(t * angle) / sin))
                .NormalizedCopy();
        }

        private static float Angle(Vec3 a, Vec3 b)
        {
            float d = Vec3.DotProduct(a.NormalizedCopy(), b.NormalizedCopy());
            return (float)Math.Acos(Math.Max(-1f, Math.Min(1f, d)));
        }

        private static float Power(float peak) => Math.Max(MinPower, Math.Min(1f, peak / FullMps));

        /// <summary>The usage that flies as a consumable missile, or -1.</summary>
        internal static int ThrownUsage(MissionWeapon weapon)
        {
            if (weapon.IsEmpty)
                return -1;

            for (int k = 0; k < weapon.WeaponsCount; k++)
            {
                WeaponComponentData usage = weapon.GetWeaponComponentDataForUsage(k);
                if (usage != null && usage.IsRangedWeapon && usage.IsConsumable)
                    return k;
            }

            return -1;
        }

        /// <summary>TEST 371b: the throwing weapon becomes the engine's missile.</summary>
        private static bool Throw(Agent agent, VrHand hand, EquipmentIndex slot, HandRole role, int usage,
                                  float peak, Vec3 dir, MatrixFrame? seen)
        {
            Mission mission = Mission.Current;
            if (!HandAt(role, seen, out Vec3 hand3))
                return false;

            // A throwing axe held in its melee usage: the shot reads the agent's
            // own current usage, so it is switched first; the managed copy says
            // whether the engine took it.
            MissionWeapon weapon = agent.Equipment[slot];
            string how = "its thrown usage";
            if (weapon.CurrentUsageIndex != usage)
            {
                agent.SetUsageIndexOfWeaponInSlotAsClient(slot, usage);
                weapon = agent.Equipment[slot];
                how = weapon.CurrentUsageIndex == usage
                    ? "switched to usage " + usage
                    : "usage switch refused - a custom missile of usage " + usage;
            }

            MissionWeapon thrown = weapon;
            thrown.CurrentUsageIndex = usage;
            float baseSpeed = thrown.GetModifiedMissileSpeedForCurrentUsage();
            float power = Power(peak);
            float speed = baseSpeed * power;

            Vec3 at = hand3 + dir * SpawnForward;
            Vec3 aim = Assist(agent, at, dir, speed, out string assist);
            Mat3 rot = Mat3.Identity;
            rot.f = aim;
            rot.Orthonormalize();

            short before = weapon.Amount;
            bool first = Probe && !_dumpedFirst;
            if (first)
                VrLog.Info("TEST 371b first throw, before: slot " + slot + " '" + weapon.Item?.StringId + "' amount "
                           + before + "/" + weapon.MaxAmmo + ", usage " + weapon.CurrentUsageIndex + ".");

            if (weapon.CurrentUsageIndex == usage)
            {
                if (!VrBow.Shoot(mission, agent, slot, at, aim * speed, rot))
                    return false;
            }
            else
            {
                thrown.Amount = 1;
                mission.AddCustomMissile(agent, thrown, at, aim, rot, baseSpeed, speed, false, null);
            }

            agent.SetWeaponAmountInSlot(slot, (short)Math.Max(0, before - 1), false);

            VrCombatSound.Play(mission, ThrowSound, at, agent.Index, -1);
            NativeMethods.bvr_trigger_haptic((int)hand, 0.4f + 0.4f * power, 0.04f);

            if (first)
            {
                _dumpedFirst = true;
                VrLog.Info("TEST 371b first throw, after: amount " + agent.Equipment[slot].Amount
                           + " (must be exactly one less).");
            }

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 371 throw: '{0}' ({1}) peak {2:F1} m/s, power {3:F2}, speed {4:F1} of {5:F1}, dir ({6:F2}, {7:F2}, "
                + "{8:F2}), assist {9}, amount {10} -> {11}.",
                weapon.Item?.StringId, how, peak, power, speed, baseSpeed, aim.x, aim.y, aim.z, assist, before,
                agent.Equipment[slot].Amount));
            return true;
        }

        /// <summary>The hand's direction, bent toward the enemy whose ballistic
        /// line is nearest it - inside the cone, within range, and by at most
        /// throw_assist_deg.</summary>
        private static Vec3 Assist(Agent agent, Vec3 from, Vec3 dir, float speed, out string text)
        {
            text = "none";
            if (AssistDeg <= 0f)
                return dir;

            float best = ConeDeg * DegToRad;
            Agent target = null;
            Vec3 line = dir;
            foreach (Agent other in Mission.Current.Agents)
            {
                if (other == agent || !other.IsActive() || !other.IsHuman || !other.IsEnemyOf(agent))
                    continue;

                Vec3 delta = other.GetChestGlobalPosition() - from;
                if (delta.Length > RangeM || !Ballistic(delta, speed, Gravity, out Vec3 solved))
                    continue;

                float angle = Angle(dir, solved);
                if (angle < best)
                {
                    best = angle;
                    target = other;
                    line = solved;
                }
            }

            if (target == null)
                return dir;

            Vec3 bent = Bend(dir, line, AssistDeg * DegToRad);
            text = string.Format(CultureInfo.InvariantCulture, "'{0}' {1:F1} deg off, bent {2:F1}",
                                 target.Name, best / DegToRad, Angle(dir, bent) / DegToRad);
            return bent;
        }

        private static bool HandAt(HandRole role, MatrixFrame? seen, out Vec3 at)
        {
            if (seen.HasValue)
            {
                at = seen.Value.origin;
                return true;
            }

            bool ok = VrSkinnedWeapon.TryGetVisual(role, out MatrixFrame world, out _, out _, out _);
            at = world.origin;
            return ok;
        }

        /// <summary>TEST 372: a copy of what the hand showed flies, lands and
        /// comes home; the engine has it in its holster already.</summary>
        private static void Toss(Agent agent, EquipmentIndex slot, HandRole role, Vec3 velocity,
                                 MatrixFrame? seen, float peak, bool throwable)
        {
            // Nothing hangs to come home to without the holster view: the
            // engine's own sheathe is the whole release then.
            if (!VrHolsterView.Enabled || (int)slot < 0 || (int)slot >= _entity.Length)
                return;

            if (!VrSkinnedWeapon.TryGetVisual(role, out MatrixFrame world, out List<MetaMesh> meshes, out _,
                                              out string itemId)
                || meshes == null || meshes.Count == 0)
                return;

            Mission mission = Mission.Current;
            Scene scene = mission?.Scene;
            if (scene == null)
                return;

            Live(mission);
            End(slot, "tossed again");
            if (_flights.Count == 0)
                _last = mission.CurrentTime;

            if (_entity[(int)slot] == null)
            {
                _entity[(int)slot] = GameEntity.CreateEmptyDynamic(scene, false);
                _entity[(int)slot].SetVisibilityExcludeParents(false);
            }

            var f = new Flight
            {
                Slot = slot,
                ItemId = agent.Equipment[slot].Item?.StringId,
                Entity = _entity[(int)slot],
                Frame = (seen ?? world).TransformToParent(meshes[0].Frame),
                Velocity = velocity,
                StartedAt = mission.CurrentTime,
                PhaseAt = mission.CurrentTime,
            };

            // Every copy shares the hand's pose frame (VrSkinnedWeapon.AddCopy),
            // so the entity carries the mesh frame and the copies sit at its origin.
            foreach (MetaMesh meta in meshes)
            {
                MetaMesh copy = meta?.CreateCopy();
                if (copy == null)
                    continue;

                copy.Frame = MatrixFrame.Identity;
                for (int m = 0; m < copy.MeshCount && m < 16; m++)
                {
                    Mesh mesh = copy.GetMeshAtIndex(m);
                    if (mesh != null && mesh.VisibilityMask == 0)
                        mesh.SetVisibilityMask((VisibilityMaskFlags)33);
                }

                VrSkinnedWeapon.Opaque(copy);
                f.Entity.AddMultiMesh(copy, true);
                f.Copies.Add(copy);
            }

            if (VrPhysicalCombat.TryMotion((int)role, out _, out _, out _, out Vec3 omega, out _))
            {
                float w = omega.Length;
                f.Omega = w > MaxOmega ? omega * (MaxOmega / w) : omega;
            }

            f.Entity.SetGlobalFrame(in f.Frame, true);
            f.Entity.SetVisibilityExcludeParents(true);
            _flights.Add(f);
            VrHolsterView.Away(slot, true);

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 372 flight: '{0}' let go at {1:F1} m/s{2} - it flies, lands, and glides home to its holster.",
                itemId, peak, throwable && peak < MinMps ? " (a throwing weapon, under throw_min_mps " + MinMps.ToString("F1", CultureInfo.InvariantCulture) + ": dropped, not thrown)" : ""));
        }

        /// <summary>From AfterMissionTick, after the holster view: moves every
        /// flight on the mission clock (a pause freezes them).</summary>
        internal static void Tick()
        {
            if (_broken || _flights.Count == 0)
                return;

            try
            {
                Mission mission = Mission.Current;
                if (mission == null || !ReferenceEquals(mission, _mission))
                {
                    Forget();
                    return;
                }

                Agent agent = mission.MainAgent;
                float now = mission.CurrentTime;
                float dt = Math.Max(0f, Math.Min(0.1f, now - _last));
                _last = now;

                for (int k = _flights.Count - 1; k >= 0; k--)
                {
                    Flight f = _flights[k];
                    if (agent == null || !agent.IsActive() || agent.Equipment[f.Slot].Item?.StringId != f.ItemId)
                    {
                        End(f.Slot, "the weapon is no longer carried");
                        continue;
                    }

                    if (agent.GetPrimaryWieldedItemIndex() == f.Slot || agent.GetOffhandWieldedItemIndex() == f.Slot)
                    {
                        End(f.Slot, "drawn again");
                        continue;
                    }

                    if (f.Phase == 0)
                        Fly(mission.Scene, f, now, dt);
                    else if (f.Phase == 1 && now - f.PhaseAt >= RestS)
                        GoHome(f, now);

                    if (f.Phase == 2)
                    {
                        if (!VrHolsterView.TryVisual(f.Slot, out MatrixFrame home))
                        {
                            End(f.Slot, "no holster to go to");
                            continue;
                        }

                        float t = (now - f.PhaseAt) / f.Dur;
                        if (t >= 1f)
                        {
                            End(f.Slot, null);
                            continue;
                        }

                        f.Frame = VrSkinnedWeapon.GlideAt(f.From, home, t);
                    }

                    f.Entity.SetGlobalFrame(in f.Frame, true);
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("TEST 372: the weapon flights failed; a release now only puts the weapon away.", ex);
                Forget();
            }
        }

        private static void Fly(Scene scene, Flight f, float now, float dt)
        {
            Vec3 prev = f.Frame.origin;
            f.Velocity.z -= Gravity * dt;
            Vec3 next = prev + f.Velocity * dt;

            // ponytail: ballistic, no bounce or roll - give the entity engine
            // physics (AddPhysics, as Mission.SpawnWeaponAux does) if it should tumble.
            if (scene.RayCastForClosestEntityOrTerrain(prev, next, out float distance, out Vec3 hit, 0.01f,
                                                       BodyFlags.CommonFocusRayCastExcludeFlags))
            {
                f.Frame.origin = hit + new Vec3(0f, 0f, 0.02f);
                f.Phase = 1;
                f.PhaseAt = now;
                if (Probe)
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 372 flight: '{0}' landed after {1:F2} s at ({2:F1}, {3:F1}, {4:F1}).",
                        f.ItemId, now - f.StartedAt, hit.x, hit.y, hit.z));
                return;
            }

            f.Frame.origin = next;
            float w = f.Omega.Length;
            if (w > 1e-3f)
            {
                Vec3 axis = f.Omega * (1f / w);
                f.Frame.rotation.s = Turn(f.Frame.rotation.s, axis, w * dt);
                f.Frame.rotation.f = Turn(f.Frame.rotation.f, axis, w * dt);
                f.Frame.rotation.u = Turn(f.Frame.rotation.u, axis, w * dt);
            }

            // Nothing below it (off a wall, into the sea), or drop_fall_s up: home.
            if (now - f.StartedAt > FallS)
                GoHome(f, now);
        }

        private static void GoHome(Flight f, float now)
        {
            f.Phase = 2;
            f.PhaseAt = now;
            f.From = f.Frame;
            float d = VrHolsterView.TryVisual(f.Slot, out MatrixFrame home) ? (home.origin - f.Frame.origin).Length : 1f;
            f.Dur = Math.Max(0.25f, d / ReturnMps);
        }

        /// <summary>Rodrigues: <paramref name="v"/> turned about the unit
        /// <paramref name="axis"/> by <paramref name="angle"/>.</summary>
        private static Vec3 Turn(Vec3 v, Vec3 axis, float angle)
        {
            float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
            return v * c + Vec3.CrossProduct(axis, v) * s + axis * (Vec3.DotProduct(axis, v) * (1f - c));
        }

        /// <summary>A grip press within reach of a flying, resting or homing
        /// weapon catches it back into the hand that holds it.</summary>
        internal static bool TryCatch(Agent agent, VrHand hand, Vec3 at)
        {
            if (_broken || _flights.Count == 0 || !ReferenceEquals(Mission.Current, _mission))
                return false;

            Flight best = null;
            float bestD = CatchM;
            foreach (Flight f in _flights)
            {
                float d = (f.Frame.origin - at).Length;
                if (d < bestD)
                {
                    bestD = d;
                    best = f;
                }
            }

            if (best == null)
                return false;

            HandRole role = VrGrab.RoleFor(agent.Equipment[best.Slot].Item);
            VrHand wanted = role == HandRole.Main ? VrHandSlots.MainController : VrHandSlots.OffController;
            if (wanted != hand)
            {
                NativeMethods.bvr_trigger_haptic((int)hand, 0.3f, 0.03f);
                return true;    // the press was at it; not a ground pickup either
            }

            MatrixFrame seen = best.Frame;
            EquipmentIndex slot = best.Slot;
            End(slot, null);

            VrSkinnedWeapon.Glide(VrGrab.EntityRole(agent.Equipment[slot].Item), seen);
            agent.TryToWieldWeaponInSlot(slot, Agent.WeaponWieldActionType.Instant, false);
            NativeMethods.bvr_trigger_haptic((int)hand, 0.6f, 0.06f);
            VrLog.Info("TEST 372 catch: " + hand + " caught '" + agent.Equipment[slot].Item?.StringId + "' "
                       + bestD.ToString("F2", CultureInfo.InvariantCulture) + " m from the hand.");
            return true;
        }

        private static void End(EquipmentIndex slot, string why)
        {
            for (int k = _flights.Count - 1; k >= 0; k--)
            {
                Flight f = _flights[k];
                if (f.Slot != slot)
                    continue;

                _flights.RemoveAt(k);
                if (ReferenceEquals(Mission.Current, _mission))
                {
                    foreach (MetaMesh copy in f.Copies)
                        f.Entity.RemoveMultiMesh(copy);
                    f.Entity.SetVisibilityExcludeParents(false);
                }

                VrHolsterView.Away(slot, false);
                if (why != null && Probe)
                    VrLog.Info("TEST 372 flight: '" + f.ItemId + "' ended - " + why + ".");
            }
        }

        /// <summary>The flights belong to this mission; a new one starts clean.</summary>
        private static void Live(Mission mission)
        {
            if (ReferenceEquals(mission, _mission))
                return;

            Forget();
            _mission = mission;
            _last = mission.CurrentTime;
        }

        /// <summary>The mission changed: the entities died with its scene, so
        /// they are dropped untouched (TEST 308).</summary>
        internal static void Forget()
        {
            foreach (Flight f in _flights)
                VrHolsterView.Away(f.Slot, false);

            _flights.Clear();
            for (int k = 0; k < _entity.Length; k++)
                _entity[k] = null;

            _mission = null;
            _count[0] = _count[1] = 0;
        }

        // Seconds since the class loaded: a float of seconds since boot is too
        // coarse (8 ms after a day) for a 100 ms window.
        private static readonly long _t0 = Stopwatch.GetTimestamp();

        private static float Now() => (float)((Stopwatch.GetTimestamp() - _t0) / (double)Stopwatch.Frequency);
    }
}
