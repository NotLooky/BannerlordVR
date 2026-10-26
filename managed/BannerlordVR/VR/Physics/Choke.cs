using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Blade & Sorcery choking. HandleRagdoll on the neck has the Choke
    // behaviour: held by a hand, the creature is choking (Brain.isChoke) and
    // cannot stand up while held (AddNoStandUpModifier). BrainModuleGrabbed:
    // after freeOneHandDelay 2 s a creature holding two weapons lets go of
    // the left one, after freeBothHandDelay 5 s of both. The damage is
    // SkillForceChoke's (SpellTelekinesis.UpdateChoke), the game's only
    // strangling one: chokeDamage 5 every chokeDamageInterval 1 s, varied by
    // chokeDamageVariation 0.4 s, a quiet blow (isStrangle). Let go, it ends.
    internal static class Choke
    {
        public const float Damage = 5, Interval = 1, Variation = .4f, FreeOneHandDelay = 2, FreeBothHandDelay = 5;
        private sealed class State { public float Started, NextDamage; public bool FreedOne, FreedBoth; public int Hands; public sbyte Bone; public Vec3 At; }
        private static readonly Dictionary<Agent, State> Choking = new Dictionary<Agent, State>();
        private static readonly List<Agent> Gone = new List<Agent>();

        internal static void ForgetMission() => Choking.Clear();

        public static bool IsChoking(Agent agent) => agent != null && Choking.ContainsKey(agent);

        // Each frame, with the bodies the player's hands hold and where.
        public static void Tick(Agent player, EnemyGrab left, EnemyGrab right, float now)
        {
            foreach (State s in Choking.Values) s.Hands = 0;
            Hold(player, left, now); Hold(player, right, now);
            Gone.Clear();
            foreach (KeyValuePair<Agent, State> c in Choking)
            {
                Agent victim = c.Key; State s = c.Value;
                if (s.Hands == 0 || !victim.IsActive())
                {
                    Gone.Add(victim);
                    if (EnemyPhysicsSettings.Trace)
                        PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "CHOKE agent={0} ended after {1:F1} s ({2})", victim.Index, now - s.Started, victim.IsActive() ? "let go" : "dead"));
                    continue;
                }
                if (!s.FreedOne && now - s.Started >= FreeOneHandDelay) { s.FreedOne = true; FreeOneHand(victim); }
                if (!s.FreedBoth && now - s.Started >= FreeBothHandDelay) { s.FreedBoth = true; FreeBothHands(victim); }
                if (now >= s.NextDamage)
                {
                    s.NextDamage = now + Interval + MBRandom.RandomFloatRanged(-Variation, Variation);
                    PhysicalHit.Strangle(player, victim, s.Bone, s.At, Damage);
                }
            }
            foreach (Agent agent in Gone) Choking.Remove(agent);
        }

        private static void Hold(Agent player, EnemyGrab grab, float now)
        {
            EnemyBody body = grab?.Body;
            if (body == null || body.IsCorpse || body.Agent == null || !body.Agent.IsActive() || grab.Point < 0 || grab.Point >= body.Count) return;
            if (body.Points[grab.Point].Part != BoneBodyPartType.Neck) return;
            if (!Choking.TryGetValue(body.Agent, out State s))
            {
                Choking[body.Agent] = s = new State { Started = now, NextDamage = now + Interval };
                PhysicsLog.Info("CHOKE agent=" + body.Agent.Index + " held by the neck");
            }
            s.Hands++; s.Bone = body.Points[grab.Point].Bone; s.At = body.Points[grab.Point].World;
        }

        // FreeOneHand: two weapons held, the left one goes.
        private static void FreeOneHand(Agent victim)
        {
            EquipmentIndex main = victim.GetPrimaryWieldedItemIndex(), off = victim.GetOffhandWieldedItemIndex();
            if (main != EquipmentIndex.None && off != EquipmentIndex.None && !victim.Equipment[off].IsEmpty)
            {
                victim.DropItem(off);
                PhysicsLog.Info("CHOKE agent=" + victim.Index + " lets go of its off-hand item");
            }
        }

        private static void FreeBothHands(Agent victim)
        {
            EquipmentIndex off = victim.GetOffhandWieldedItemIndex();
            if (off != EquipmentIndex.None && !victim.Equipment[off].IsEmpty) victim.DropItem(off);
            EquipmentIndex main = victim.GetPrimaryWieldedItemIndex();
            if (main != EquipmentIndex.None && !victim.Equipment[main].IsEmpty) victim.DropItem(main);
            PhysicsLog.Info("CHOKE agent=" + victim.Index + " lets go of everything it holds");
        }
    }
}
