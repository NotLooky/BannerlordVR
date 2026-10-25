using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // B&S weapons are rigidbodies: an enemy's blade is as solid as its shield.
    // After VrCollide the shown blade is tested against the blade of every
    // armed enemy near the player (VrBlade's enemy segment, a capsule); one
    // inside it moves the hand back out along the line between the two
    // blades, to the side the blade was on last frame, by at most MaxLag.
    //
    // BrainModuleMelee.OnWeaponCollision: the attacking creature's weapon
    // meeting the player's weapon or shield at more than recoilMinWeaponSpeed
    // (1 m/s) is a parry: the attack stops and the attacker recoils
    // (TryPush Parry, push level 2 against an enemy: the hand holding the
    // weapon is thrown back). The engine's own hit for that swing (its blade
    // goes on to the body behind the player's guard) bounces off instead.
    internal static class BladeClash
    {
        // A new mission: the last one's agents and entities are gone.
        internal static void ForgetMission() { Enemy.Clear(); ContactAt.Clear(); ParriedUntil.Clear(); ParriedAttack.Clear(); NearAt.Clear(); }
        public const float MaxLag = .6f, PlayerRadius = .015f, RecoilMinWeaponSpeed = 1, ContactCooldown = .25f;
        // The parried swing's hold and rewind (LivingRagdollMissionLogic
        // ParryStop + RewindSeconds), then a little: no longer. Held 0.8 s,
        // the window covered a combo's second blow, often the same animation,
        // taken for the parried one: not solid, it went through the sword.
        public const float BounceSeconds = .36f;
        private struct Last { public Vec3 Point; public long At; public Vec3 Normal; public Vec3 Grip, Tip; }
        private static readonly Dictionary<int, Last> Enemy = new Dictionary<int, Last>();
        private static readonly Dictionary<int, long> ContactAt = new Dictionary<int, long>();
        private static readonly Dictionary<Agent, float> ParriedUntil = new Dictionary<Agent, float>();
        private static readonly Dictionary<Agent, int> ParriedAttack = new Dictionary<Agent, int>();
        // An attacking blade seen within NearReach of the guard: the engine's
        // hit can come the same frame the blades meet, before a frame shows
        // them touching (thrusts landed, the contact logged 2 ms after).
        private static readonly Dictionary<Agent, long> NearAt = new Dictionary<Agent, long>();
        private const float NearReach = .1f, NearSeconds = .12f;
        private static bool Attacking(Agent agent)
        {
            Agent.ActionStage stage = agent.GetCurrentActionStage(1);
            return stage == Agent.ActionStage.AttackRelease || stage == Agent.ActionStage.AttackReady || stage == Agent.ActionStage.AttackQuickReady;
        }
        private static bool WasNear(Agent attacker) =>
            NearAt.TryGetValue(attacker, out long at) && Stopwatch.GetTimestamp() - at < Stopwatch.Frequency * NearSeconds;
        private static Mission _mission;
        private static bool _broken;
        private static int _logs;

        public static bool Parried(Agent attacker)
        {
            Mission mission = Mission.Current;
            if (attacker == null || mission == null || !ParriedUntil.TryGetValue(attacker, out float until) || mission.CurrentTime > until) return false;
            // A chained second swing is a new attack: parryable and landing
            // like any other. The window held over it, so it could neither be
            // parried nor do damage (its hit bounced).
            Agent.ActionStage stage = attacker.GetCurrentActionStage(1);
            // The parried swing itself, held still on the blade before its
            // recoil, is not a new one.
            bool same = ParriedAttack.TryGetValue(attacker, out int action) && attacker.GetCurrentAction(1).Index == action;
            if (!same && (stage == Agent.ActionStage.AttackReady || stage == Agent.ActionStage.AttackQuickReady || stage == Agent.ActionStage.AttackRelease))
            {
                ParriedUntil.Remove(attacker);
                return false;
            }
            return true;
        }

        public static bool Apply(HandRole role, ref MatrixFrame world, MatrixFrame previous, Vec3 gripLocal, Vec3 tipLocal)
        {
            long perf = Perf.Start();
            try
            {
                bool moved = ApplyCore(role, ref world, previous, gripLocal, tipLocal);
                if (moved) HeldAt[(int)role] = Stopwatch.GetTimestamp();
                return moved;
            }
            finally { Perf.Stop(Perf.Section.Clash, perf); }
        }
        // A blade an enemy's blade holds back strikes nothing behind it, as
        // one a shield holds (the controller's blade went on through the
        // parry into the body).
        private static readonly long[] HeldAt = new long[2];
        public static bool Holding(HandRole role) =>
            HeldAt[(int)role] != 0 && Stopwatch.GetTimestamp() - HeldAt[(int)role] < Stopwatch.Frequency / 10;
        private static bool ApplyCore(HandRole role, ref MatrixFrame world, MatrixFrame previous, Vec3 gripLocal, Vec3 tipLocal)
        {
            if (_broken || !EnemyPhysicsSettings.SolidWeapons) return false;
            try
            {
                Mission mission = Mission.Current;
                Agent player = mission?.MainAgent;
                if (player == null || (tipLocal - gripLocal).Length < .1f) return false;
                if (mission != _mission) { Enemy.Clear(); ContactAt.Clear(); ParriedUntil.Clear(); _mission = mission; }
                long now = Stopwatch.GetTimestamp();
                // A shield is not pushed aside by the blades it stops (that
                // carried it out of the swing it was blocking): a blade that
                // meets it parries, as a weapon does (BrainModuleMelee).
                if (role == HandRole.Off && player.WieldedOffhandWeapon.CurrentUsageItem?.IsShield == true)
                {
                    ShieldContacts(mission, player, now);
                    return false;
                }
                Vec3 total = Vec3.Zero;
                bool moved = false;
                foreach (Agent agent in EnemyPhysicsRuntime.TrackedLiving())
                {
                    EquipmentIndex slot = agent.GetPrimaryWieldedItemIndex();
                    if (slot == EquipmentIndex.None || agent.Equipment[slot].CurrentUsageItem?.IsMeleeWeapon != true) continue;
                    if (!EnemyBlade(agent, slot, out VrBlade.Segment blade) || !blade.Valid) continue;
                    moved |= Push(mission, player, agent, slot, ref world, previous, gripLocal, tipLocal, blade, ref total, now);
                }
                if (role == HandRole.Main)
                {
                    // The guard as shown this frame, for the engine's hit test.
                    _guard0 = world.TransformToParent(gripLocal); _guard1 = world.TransformToParent(tipLocal); _guardAt = now;
                    // The shield hand may have no learnt blade, and then never
                    // came here: its contacts are tested with the sword's.
                    if (player.WieldedOffhandWeapon.CurrentUsageItem?.IsShield == true) ShieldContacts(mission, player, now);
                }
                return moved;
            }
            catch (Exception ex)
            {
                _broken = true;
                PhysicsLog.Error("Solid enemy weapons failed; blades go through enemy weapons as before.", ex);
                return false;
            }
        }

        private static bool Push(Mission mission, Agent player, Agent agent, EquipmentIndex slot, ref MatrixFrame world, MatrixFrame previous,
            Vec3 gripLocal, Vec3 tipLocal, VrBlade.Segment blade, ref Vec3 total, long now)
        {
            Vec3 a0 = world.TransformToParent(gripLocal), a1 = world.TransformToParent(tipLocal);
            Vec3 b0 = blade.GripWorld, b1 = blade.TipWorld;
            float reach = blade.Radius + PlayerRadius;
            bool known = Enemy.TryGetValue(agent.Index, out Last last) && now - last.At < Stopwatch.Frequency / 4;
            // A parried swing's recoil animation whips the blade back at 20-35
            // m/s some 0.6 s later, through the guard that stopped it: it
            // knocked the player's sword 17 cm aside after every parry. The
            // recoiling blade is not solid.
            if (Parried(agent))
            {
                Enemy[agent.Index] = new Last { Point = b1, At = now, Normal = last.Normal, Grip = b0, Tip = b1 };
                return false;
            }
            // A slash sweeps its blade sideways at 15-50 m/s at the tip, 20-40
            // cm a frame: by the frame the blades were seen touching it was
            // already through the sword, and the parry held it on the far
            // side. Its motion is carried on a frame and a half ahead; a
            // swing whose path cuts the sword is parried now, before it crosses.
            if (known && Attacking(agent))
            {
                Vec3 g = b0 + (b0 - last.Grip) * Lookahead, t = b1 + (b1 - last.Tip) * Lookahead;
                if (Sweeps(a0, a1, b0, b1, g, t, reach))
                {
                    Vec3 v = (b1 - last.Tip) * (float)(Stopwatch.Frequency / (double)Math.Max(1, now - last.At));
                    Closest(a0, a1, b0, b1, out _, out Vec3 near);
                    Enemy[agent.Index] = new Last { Point = near, At = now, Normal = last.Normal, Grip = b0, Tip = b1 };
                    Contact(mission, player, agent, slot, near, Vec3.Zero, v, now, false);
                    if (Parried(agent)) return false;
                }
            }
            Closest(a0, a1, b0, b1, out Vec3 mine, out Vec3 theirs);
            Vec3 gap = mine - theirs;
            // The side the blade was on last frame (a fast blade can cross
            // the other between two frames), else where it is now.
            Vec3 n = known ? last.Normal : gap.NormalizedCopy();
            if (!known || gap.Length > reach * 2)
            {
                Vec3 pa0 = previous.TransformToParent(gripLocal), pa1 = previous.TransformToParent(tipLocal);
                Closest(pa0, pa1, b0, b1, out Vec3 pm, out Vec3 pt);
                if ((pm - pt).Length > 1e-4f) n = (pm - pt).NormalizedCopy();
            }
            if (n.LengthSquared < .25f) return false;
            float depth = reach - InteractionMath.Dot(gap, n);
            if (gap.Length < reach + NearReach && Attacking(agent)) NearAt[agent] = now;
            Vec3 point = theirs;
            Vec3 enemyVelocity = known ? (point - last.Point) * (float)(Stopwatch.Frequency / (double)Math.Max(1, now - last.At)) : Vec3.Zero;
            if (depth <= 0)
            {
                // A fast swing (20-25 m/s) moves the enemy blade 40 cm a frame:
                // seen on one side then the other, never touching, its parry
                // was missed. Its sweep since last frame counts as a meeting.
                if (known && Sweeps(a0, a1, last.Grip, last.Tip, b0, b1, reach))
                    Contact(mission, player, agent, slot, point, Vec3.Zero, enemyVelocity, now, false);
                Enemy[agent.Index] = new Last { Point = point, At = now, Normal = gap.Length > 1e-4f ? gap.NormalizedCopy() : n, Grip = b0, Tip = b1 };
                return false;
            }
            // B&S: the weapon is a rigidbody on the hand, the hand on a joint to
            // the controller, stiff in position (forcePositionSpringDamper 5000
            // N/m) and soft in rotation (forceRotationSpringDamper 1000 N.m per
            // rad). Pushed at the contact, the sword gives where it is softest:
            // it turns in the hand about the grip, the farther out the more,
            // and the hand gives the rest. Moved back whole, the hand felt like
            // hitting a wall. The share of each is their compliance at the
            // contact: L^2 / k_rot against 1 / k_pos.
            Vec3 pivot = a0;
            Vec3 lever = mine - pivot;
            float l = lever.Length;
            float turnCompliance = l * l / RotationSpring, slideCompliance = 1 / PositionSpring;
            float turnShare = l > .1f ? turnCompliance / (turnCompliance + slideCompliance) : 0;
            float angle = Math.Min(MaxTurn, depth * turnShare / Math.Max(.1f, l));
            Vec3 axis = Vec3.CrossProduct(lever, n);
            float turned = 0;
            if (angle > 1e-4f && axis.Length > 1e-4f)
            {
                axis.Normalize();
                world.rotation.s = Rotate(world.rotation.s, axis, angle);
                world.rotation.f = Rotate(world.rotation.f, axis, angle);
                world.rotation.u = Rotate(world.rotation.u, axis, angle);
                world.origin = pivot + Rotate(world.origin - pivot, axis, angle);
                turned = angle * l;
            }
            Vec3 shift = n * Math.Max(0, depth - turned);
            if ((total + shift).Length > MaxLag) { Enemy.Remove(agent.Index); return false; } // out of reach: it goes through
            total += shift;
            world.origin += shift;
            // What the contact felt: the whole give, turned and slid.
            shift = n * depth;
            Enemy[agent.Index] = new Last { Point = point, At = now, Normal = n, Grip = b0, Tip = b1 };
            Contact(mission, player, agent, slot, point, shift, enemyVelocity, now, false);
            return true;
        }

        // Creature_PlayerDefault forcePositionSpringDamper.x / forceRotationSpringDamper.x.
        private const float PositionSpring = 5000, RotationSpring = 1000, MaxTurn = .6f, PressDepth = .015f, Lookahead = 1.5f;
        private static Vec3 Rotate(Vec3 v, Vec3 axis, float angle)
        {
            float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
            return v * c + Vec3.CrossProduct(axis, v) * s + axis * (Vec3.DotProduct(axis, v) * (1 - c));
        }

        // The enemy blade's sweep from last frame to this one (sampled at
        // quarters) passes within reach of the player's blade.
        private static bool Sweeps(Vec3 a0, Vec3 a1, Vec3 grip0, Vec3 tip0, Vec3 grip1, Vec3 tip1, float reach)
        {
            for (int i = 1; i < 4; i++)
            {
                float t = i / 4f;
                Closest(a0, a1, grip0 + (grip1 - grip0) * t, tip0 + (tip1 - tip0) * t, out Vec3 m, out Vec3 q);
                if ((m - q).Length <= reach) return true;
            }
            return false;
        }

        private static void Contact(Mission mission, Agent player, Agent agent, EquipmentIndex slot, Vec3 at, Vec3 shift, Vec3 enemyVelocity, long now, bool shield)
        {
            // The impact speed: the enemy blade's own and the player's into it.
            Vec3 impact = enemyVelocity - shift * 60;
            float speed = impact.Length;
            Agent.ActionStage stage = agent.GetCurrentActionStage(1);
            bool attacking = stage == Agent.ActionStage.AttackRelease || stage == Agent.ActionStage.AttackReady ||
                stage == Agent.ActionStage.AttackQuickReady;
            // Every meeting of an attacking blade is judged, whatever touched
            // just before: a guard contact a moment earlier held the parry
            // back for the sound's cooldown and the swing came through.
            // The player's blade laid on the side of the swinging one and
            // pushing it away is a parry too, however slowly it meets it: the
            // solid enemy blade held the sword back and the swing went on.
            bool pressed = shift.Length > PressDepth;
            if (attacking && (speed > RecoilMinWeaponSpeed || pressed) && !Parried(agent))
            {
                ParriedUntil[agent] = mission.CurrentTime + BounceSeconds;
                ParriedAttack[agent] = agent.GetCurrentAction(1).Index;
                // A slow push still recoils the blade away from the player's.
                EnemyPhysicsRuntime.Parried(agent, speed > RecoilMinWeaponSpeed ? impact : -shift.NormalizedCopy() * 2);
            }
            if (ContactAt.TryGetValue(agent.Index, out long last) && now - last < Stopwatch.Frequency * ContactCooldown) return;
            ContactAt[agent.Index] = now;
            try
            {
                WeaponComponentData mine = shield ? player.WieldedOffhandWeapon.CurrentUsageItem : player.WieldedWeapon.CurrentUsageItem;
                WeaponComponentData theirs = agent.Equipment[slot].CurrentUsageItem;
                if (mine != null && theirs != null)
                    VrCombatSound.Clash(mission, at, VrCombatSound.Material(theirs, false), VrCombatSound.Material(mine, shield), agent.Index, player.Index);
            }
            catch (Exception) { }
            float strength = Math.Min(1, .3f + speed / 8);
            VrHand hand = shield ? VrHandSlots.OffController : VrHandSlots.MainController;
            PhysicsInterop.Haptic(hand == VrHand.Left ? 0 : 1, strength, .04f);
            if (EnemyPhysicsSettings.Trace || _logs < 6)
            {
                _logs++;
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "CLASH agent={0} stage={1} speed={2:F2} parried={3} hand_held_back={4:F3}", agent.Index, stage, speed,
                    attacking && speed > RecoilMinWeaponSpeed, shift.Length));
            }
        }

        private static void ShieldContacts(Mission mission, Agent player, long now)
        {
            if (FastAccess.OwnPlate == null || !FastAccess.OwnPlate(player, out VrBlade.Plate plate) || !plate.Valid) return;
            Vec3 n = plate.N.NormalizedCopy();
            foreach (Agent agent in EnemyPhysicsRuntime.TrackedLiving())
            {
                EquipmentIndex slot = agent.GetPrimaryWieldedItemIndex();
                if (slot == EquipmentIndex.None || agent.Equipment[slot].CurrentUsageItem?.IsMeleeWeapon != true) continue;
                if (!EnemyBlade(agent, slot, out VrBlade.Segment blade) || !blade.Valid) continue;
                int key = -1 - agent.Index;
                Vec3 grip = blade.GripWorld, tip = blade.TipWorld;
                bool known = Enemy.TryGetValue(key, out Last last) && now - last.At < Stopwatch.Frequency / 4;
                Vec3 velocity = known ? (tip - last.Point) * (float)(Stopwatch.Frequency / (double)Math.Max(1, now - last.At)) : Vec3.Zero;
                Enemy[key] = new Last { Point = tip, At = now, Grip = grip, Tip = tip };
                // The blade now and its sweep since last frame.
                if (Attacking(agent) && InPlate(plate, n, grip, tip, blade.Radius + NearReach, out _)) NearAt[agent] = now;
                if (InPlate(plate, n, grip, tip, blade.Radius, out Vec3 at) ||
                    (known && SweepInPlate(plate, n, last.Grip, last.Tip, grip, tip, blade.Radius, out at)))
                    Contact(mission, player, agent, slot, at, Vec3.Zero, velocity, now, true);
            }
        }

        private static bool InPlate(VrBlade.Plate plate, Vec3 n, Vec3 grip, Vec3 tip, float pad, out Vec3 at)
        {
            for (int i = 0; i <= 8; i++)
            {
                at = grip + (tip - grip) * (i / 8f);
                Vec3 d = at - plate.Centre;
                if (Math.Abs(InteractionMath.Dot(d, n)) <= plate.HalfN + pad &&
                    Math.Abs(InteractionMath.Dot(d, plate.A)) <= plate.HalfA + pad * .5f && Math.Abs(InteractionMath.Dot(d, plate.B)) <= plate.HalfB + pad * .5f)
                    return true;
            }
            at = Vec3.Zero;
            return false;
        }
        private static bool SweepInPlate(VrBlade.Plate plate, Vec3 n, Vec3 grip0, Vec3 tip0, Vec3 grip1, Vec3 tip1, float pad, out Vec3 at)
        {
            for (int k = 1; k < 4; k++)
            {
                float t = k / 4f;
                if (InPlate(plate, n, grip0 + (grip1 - grip0) * t, tip0 + (tip1 - tip0) * t, pad, out at)) return true;
            }
            at = Vec3.Zero;
            return false;
        }

        // The guard shown last frame (sword) for the engine's hit test.
        private static Vec3 _guard0, _guard1;
        private static long _guardAt;
        private const float LateReachPad = .04f;

        // At the engine's hit on the player: the striking blade had to come
        // through the guard to reach the body. The engine can register the
        // hit before a frame shows the two blades touching (a fast blow, or
        // both in one frame), and the blow went through sword and shield.
        // Tested: the blade now and its sweep since the last frame, and the
        // line from its grip to the struck point, against the sword (a
        // little wider than for the per-frame contact) and the shield plate.
        private static bool ThroughGuard(Agent attacker, Vec3 struck, out Vec3 impact)
        {
            impact = Vec3.Zero;
            Agent player = Mission.Current?.MainAgent;
            EquipmentIndex slot = attacker.GetPrimaryWieldedItemIndex();
            if (player == null || slot == EquipmentIndex.None) return false;
            if (!EnemyBlade(attacker, slot, out VrBlade.Segment blade) || !blade.Valid) return false;
            long now = Stopwatch.GetTimestamp();
            Vec3 b0 = blade.GripWorld, b1 = blade.TipWorld;
            bool known = Enemy.TryGetValue(attacker.Index, out Last last) && now - last.At < Stopwatch.Frequency / 4;
            impact = known ? (b1 - last.Tip) * (float)(Stopwatch.Frequency / (double)Math.Max(1, now - last.At)) : (struck - b0);
            if (impact.Length < 5) impact = impact.NormalizedCopy() * 5;
            bool strikeLine = InteractionMath.IsFinite(struck) && (struck - b0).Length > .05f;
            if (_guardAt != 0 && now - _guardAt < Stopwatch.Frequency / 6)
            {
                float reach = blade.Radius + PlayerRadius + LateReachPad;
                Closest(_guard0, _guard1, b0, b1, out Vec3 m, out Vec3 q);
                if ((m - q).Length <= reach) return true;
                if (known && Sweeps(_guard0, _guard1, last.Grip, last.Tip, b0, b1, reach)) return true;
                if (strikeLine) { Closest(_guard0, _guard1, b0, struck, out m, out q); if ((m - q).Length <= reach) return true; }
            }
            if (player.WieldedOffhandWeapon.CurrentUsageItem?.IsShield == true && FastAccess.OwnPlate != null)
            {
                if (FastAccess.OwnPlate(player, out VrBlade.Plate plate) && plate.Valid)
                {
                    Vec3 n = plate.N.NormalizedCopy();
                    float pad = blade.Radius + LateReachPad;
                    if (InPlate(plate, n, b0, b1, pad, out _)) return true;
                    if (known && SweepInPlate(plate, n, last.Grip, last.Tip, b0, b1, pad, out _)) return true;
                    if (strikeLine && InPlate(plate, n, b0, struck, pad, out _)) return true;
                }
            }
            return false;
        }

        // The enemy blade. VrBlade gives a fit of the engine's contacts per
        // weapon class, which the VR log measured 0.14-0.21 m (worst 0.30 m)
        // off the contacts it came from and 11 deg off the item's own frame:
        // its tip went through the player's blade. The item frame on its bone
        // (the weapon's own axis and length) is the other candidate. Each
        // engine hit on the player scores both against the struck point; per
        // weapon class the closer one is used, the frame until they are known.
        private sealed class Score { public float Fit, Frame; public int Count; }
        private static readonly Dictionary<WeaponClass, Score> Scores = new Dictionary<WeaponClass, Score>();
        private static bool Candidates(Agent agent, EquipmentIndex slot, out VrBlade.Segment fit, out VrBlade.Segment frame)
        {
            frame = default(VrBlade.Segment);
            long perf = Perf.Start();
            bool found;
            try { found = VrBlade.TryGetEnemyBlade(agent, slot, out fit); }
            finally { Perf.Stop(Perf.Section.EnemyBlade, perf); }
            if (!found || !fit.Valid) return false;
            WeaponComponentData usage = agent.Equipment[slot].CurrentUsageItem;
            if (usage == null) return false;
            // VrBlade.FrameBlade: the item frame's origin and its up axis.
            Vec3 grip = usage.Frame.origin, tip = grip + usage.Frame.rotation.u.NormalizedCopy() * (usage.WeaponLength * .01f);
            frame = fit; frame.Grip = grip; frame.Tip = tip; frame.FromFrame = true;
            return true;
        }
        private static bool EnemyBlade(Agent agent, EquipmentIndex slot, out VrBlade.Segment segment)
        {
            if (!Candidates(agent, slot, out VrBlade.Segment fit, out VrBlade.Segment frame)) { segment = fit; return fit.Valid; }
            WeaponClass type = agent.Equipment[slot].CurrentUsageItem.WeaponClass;
            bool useFit = Scores.TryGetValue(type, out Score score) && score.Count >= 3 && score.Fit < score.Frame;
            segment = useFit ? fit : frame;
            return true;
        }
        private static void ScoreHit(Agent attacker, Vec3 struck)
        {
            EquipmentIndex slot = attacker.GetPrimaryWieldedItemIndex();
            if (slot == EquipmentIndex.None || !InteractionMath.IsFinite(struck)) return;
            if (!Candidates(attacker, slot, out VrBlade.Segment fit, out VrBlade.Segment frame)) return;
            WeaponClass type = attacker.Equipment[slot].CurrentUsageItem.WeaponClass;
            if (!Scores.TryGetValue(type, out Score score)) Scores[type] = score = new Score();
            Closest(struck, struck, fit.GripWorld, fit.TipWorld, out _, out Vec3 onFit);
            Closest(struck, struck, frame.GripWorld, frame.TipWorld, out _, out Vec3 onFrame);
            float a = (struck - onFit).Length, b = (struck - onFrame).Length;
            // A running mean that follows the recent hits.
            float w = score.Count < 10 ? 1f / (score.Count + 1) : .1f;
            score.Fit += (a - score.Fit) * w; score.Frame += (b - score.Frame) * w; score.Count++;
            if (score.Count <= 3 || score.Count % 10 == 0)
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "CLASH enemy blade {0}: struck point {1:F2} m off the fit, {2:F2} m off the item frame (means over {3}: fit {4:F2}, frame {5:F2}) -> {6}",
                    type, a, b, score.Count, score.Fit, score.Frame, score.Count >= 3 && score.Fit < score.Frame ? "fit" : "frame"));
        }

        // Closest points between segments p0-p1 and q0-q1.
        private static void Closest(Vec3 p0, Vec3 p1, Vec3 q0, Vec3 q1, out Vec3 onP, out Vec3 onQ)
        {
            Vec3 d1 = p1 - p0, d2 = q1 - q0, r = p0 - q0;
            float a = InteractionMath.Dot(d1, d1), e = InteractionMath.Dot(d2, d2), f = InteractionMath.Dot(d2, r);
            float s, t;
            if (a < 1e-8f && e < 1e-8f) { onP = p0; onQ = q0; return; }
            if (a < 1e-8f) { s = 0; t = Clamp(f / e); }
            else
            {
                float c = InteractionMath.Dot(d1, r);
                if (e < 1e-8f) { t = 0; s = Clamp(-c / a); }
                else
                {
                    float b = InteractionMath.Dot(d1, d2), denom = a * e - b * b;
                    s = denom > 1e-8f ? Clamp((b * f - c * e) / denom) : 0;
                    t = (b * s + f) / e;
                    if (t < 0) { t = 0; s = Clamp(-c / a); }
                    else if (t > 1) { t = 1; s = Clamp((b - c) / a); }
                }
            }
            onP = p0 + d1 * s; onQ = q0 + d2 * t;
        }
        private static float Clamp(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        // The engine's hit of a parried swing bounces off the player's guard.
        [HarmonyPatch]
        private static class BouncePatch
        {
            private static MethodBase Target => AccessTools.Method(typeof(Mission), "MeleeHitCallback", new[]
            {
                typeof(AttackCollisionData).MakeByRefType(), typeof(Agent), typeof(Agent), typeof(GameEntity),
                typeof(float).MakeByRefType(), typeof(MeleeCollisionReaction).MakeByRefType(), typeof(CrushThroughState),
                typeof(Vec3), typeof(Vec3), typeof(HitParticleResultData).MakeByRefType(), typeof(bool)
            });
            public static bool Prepare() => EnemyPhysicsSettings.Configured && (EnemyPhysicsSettings.SolidWeapons && Target != null);
            public static MethodBase TargetMethod() => Target;
            [HarmonyPriority(Priority.First)]
            public static bool Prefix(ref AttackCollisionData collisionData, Agent attacker, Agent victim, ref MeleeCollisionReaction colReaction, ref HitParticleResultData hitParticleResultData)
            {
                if (!EnemyPhysicsSettings.Enabled || victim == null || attacker == null || !victim.IsMainAgent || collisionData.IsMissile) return true;
                // The engine's own block or parry (the VR guard) stands as it is.
                if (collisionData.AttackBlockedWithShield || collisionData.CollisionResult == CombatCollisionResult.Blocked ||
                    collisionData.CollisionResult == CombatCollisionResult.Parried || collisionData.CollisionResult == CombatCollisionResult.ChamberBlocked)
                    return true;
                try { ScoreHit(attacker, collisionData.CollisionGlobalPosition); } catch (Exception) { }
                bool parried = Parried(attacker);
                if (!parried)
                {
                    try
                    {
                        bool near = WasNear(attacker);
                        Vec3 impact = Vec3.Zero;
                        if (near || ThroughGuard(attacker, collisionData.CollisionGlobalPosition, out impact))
                        {
                            parried = true;
                            Mission mission = Mission.Current;
                            ParriedUntil[attacker] = mission.CurrentTime + BounceSeconds;
                            ParriedAttack[attacker] = attacker.GetCurrentAction(1).Index;
                            if (impact.LengthSquared < 1e-4f) impact = (collisionData.CollisionGlobalPosition - attacker.Position).NormalizedCopy() * 5;
                            EnemyPhysicsRuntime.Parried(attacker, impact);
                            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                                "LATE_PARRY agent={0} action={1}: the blow came {2}; parried at the engine's hit",
                                attacker.Index, attacker.GetCurrentAction(1).GetName(), near ? "within reach of the guard a moment before" : "through the guard"));
                        }
                    }
                    catch (Exception ex) { PhysicsLog.Error("Late parry test failed.", ex); }
                }
                if (EnemyPhysicsSettings.Trace)
                    PhysicsLog.Info("ENEMY_HIT on the player by agent " + attacker.Index + " action=" + attacker.GetCurrentAction(1).GetName() +
                        " stage=" + attacker.GetCurrentActionStage(1) + (parried ? " -> bounced (parried)" : " -> lands"));
                if (!parried) return true;
                colReaction = MeleeCollisionReaction.Bounced;
                hitParticleResultData.Reset();
                if (EnemyPhysicsSettings.Trace) PhysicsLog.Info("CLASH engine hit of parried agent " + attacker.Index + " bounced");
                return false;
            }
        }
    }
}
