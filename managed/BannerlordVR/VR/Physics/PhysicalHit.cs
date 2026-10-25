using System;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    public static class PhysicalHit
    {
        // Set while the physics registers its own blow, so the guard that
        // protects held enemies lets it through.
        [ThreadStatic] public static bool DeliveringOwnBlow;
        // RegisterBlow preserves mortality rules, hit events and kill attribution.
        // This prototype uses an explicit armor-aware damage approximation, not
        // a claim to reproduce the native combat model's full damage calculation.
        public static void Apply(Agent player, EnemyBody body, BodyPoint point, Vec3 contact,
                                 Vec3 velocity, MissionWeapon weapon, EquipmentIndex slot, bool thrust,
                                 PushEffect push = PushEffect.None)
        {
            Vec3 direction = velocity.NormalizedCopy();
            float speed = velocity.Length;
            if (speed < .01f) return;
            WeaponComponentData usage = weapon.IsEmpty ? null : weapon.CurrentUsageItem;
            if (body.IsCorpse)
            {
                if (body.HasNativePhysics)
                {
                    // A fist on a dead body: the contact brings the part to
                    // the fist's speed, then Damager_Punch's AddForceCoroutine
                    // (dead creatures only) adds its 0.15 s, in one step.
                    Vec3 force = usage != null ? InteractionMath.ClampMagnitude(velocity * 25, EnemyPhysicsSettings.MaxForce) :
                        InteractionMath.ClampMagnitude((PunchImpact.ContactImpulse(CorpsePartMass, velocity) + velocity * (CorpsePartMass *
                            PunchImpact.AddForce * PunchImpact.AddForcePart * PunchImpact.AddForceDuration / PunchImpact.StepRate)) * PunchImpact.StepRate,
                            PunchImpact.MaxImpulse * PunchImpact.StepRate);
                    body.Agent.ApplyForceOnRagdoll(point.Bone, in force);
                }
                return;
            }
            if (usage == null)
            {
                // Damager_Punch: velocityDamageCurve x the armour row's
                // damageMultiplier, nothing under its minVelocity. The row
                // already stands for the armour.
                Agent victim = body.Agent;
                float punch = PunchImpact.Damage(speed, PunchImpact.For(BodyPenetration.MaterialFor(victim, point.Part)), victim.HealthLimit);
                if (punch < .5f) return;
                float lost = Deliver(player, victim, point, contact, direction, weapon, slot, null, DamageTypes.Blunt,
                    punch, Math.Max(1, (int)Math.Round(punch)), 0, false, false, push);
                if (lost > 0 && body.Agent == victim)
                    EnemyPhysicsRuntime.RecordWound(player, body, point, contact, DamageTypes.Blunt, lost, false);
                return;
            }
            DamageTypes damageType = thrust ? usage.ThrustDamageType : usage.SwingDamageType;
            float raw = (thrust ? usage.ThrustDamage : usage.SwingDamage) * Math.Min(1.5f, speed / (thrust ? 2.5f : 5));
            Register(player, body, point, contact, direction, weapon, slot, usage, damageType, raw, thrust, false, push);
        }

        // A weapon's pommel or flat: the swing's damage scaled, as blunt.
        public static void ApplyBlunt(Agent player, EnemyBody body, BodyPoint point, Vec3 contact, Vec3 velocity,
            MissionWeapon weapon, EquipmentIndex slot, WeaponComponentData usage, float scale, PushEffect push)
        {
            float speed = velocity.Length;
            if (usage == null || speed < .01f || body.IsCorpse) return;
            float raw = usage.SwingDamage * scale * Math.Min(1.5f, speed / 5);
            if (raw < .5f) return;
            Register(player, body, point, contact, velocity.NormalizedCopy(), weapon, slot, usage, DamageTypes.Blunt, raw, false, false, push);
        }

        // Mean part of a 89.7 kg ragdoll of 16 bodies.
        private const float CorpsePartMass = 5.6f;

        // Blade & Sorcery material of a VR hit, for its push level table.
        public static PushMaterial Material(WeaponComponentData usage, bool thrust)
        {
            if (usage == null) return PushMaterial.Punch;
            // A mace's or a staff's thrust is a blunt blow (B&S Damager_Mace
            // has no pierce): pushed and stunned as one, not as a point.
            if (thrust && usage.ThrustDamageType == DamageTypes.Blunt) return usage.WeaponLength > 90 ? PushMaterial.Blunt : PushMaterial.BluntLight;
            if (thrust) return PushMaterial.Pierce;
            if (usage.SwingDamageType == DamageTypes.Blunt) return usage.WeaponLength > 90 ? PushMaterial.Blunt : PushMaterial.BluntLight;
            switch (usage.WeaponClass)
            {
                case WeaponClass.OneHandedAxe:
                case WeaponClass.TwoHandedAxe:
                case WeaponClass.LowGripPolearm: return PushMaterial.SlashAxe;
                case WeaponClass.TwoHandedSword:
                case WeaponClass.TwoHandedPolearm: return PushMaterial.SlashHeavy;
                default: return PushMaterial.Slash;
            }
        }

        public static void ApplyDrawCut(Agent player, EnemyBody body, BodyPoint point, Vec3 contact,
            Vec3 velocity, MissionWeapon weapon, EquipmentIndex slot)
        {
            if (body.IsCorpse || !body.Agent.IsActive() || point.Part != BoneBodyPartType.Neck || weapon.IsEmpty) return;
            WeaponComponentData usage = weapon.CurrentUsageItem;
            if (usage == null || usage.SwingDamageType != DamageTypes.Cut) return;
            // A held-neck draw has a length threshold instead of kinetic scaling.
            // Armor and RegisterBlow mortality still apply; this is not an instant-kill override.
            Vec3 direction = velocity.Length > .001f ? velocity.NormalizedCopy() : player.LookDirection;
            Register(player, body, point, contact, direction, weapon, slot, usage, DamageTypes.Cut,
                Math.Min(100, Math.Max(35, usage.SwingDamage * 1.25f)), false, true);
        }

        private static void Register(Agent player, EnemyBody body, BodyPoint point, Vec3 contact,
            Vec3 direction, MissionWeapon weapon, EquipmentIndex slot, WeaponComponentData usage,
            DamageTypes damageType, float raw, bool thrust, bool drawCut = false, PushEffect push = PushEffect.None)
        {
            float armor = body.Agent.GetBaseArmorEffectivenessForBodyPart(point.Part);
            float damage = raw * (point.Part == BoneBodyPartType.Head ? 1.5f : 1) / (1 + Math.Max(0, armor) * .045f);
            Agent victim = body.Agent;
            float lost = Deliver(player, victim, point, contact, direction, weapon, slot, usage,
                damageType, raw, Math.Max(1, Math.Min(150, (int)damage)), Math.Max(0, raw - damage), thrust, false, push);
            if (lost > 0 && body.Agent == victim)
                EnemyPhysicsRuntime.RecordWound(player, body, point, contact, damageType, lost, drawCut);
        }

        // B&S ragdoll collision: blunt, without a weapon, attributed to whoever
        // last pushed the body (BrainModuleHitReaction.lastHitSource).
        public static float ApplyRagdollImpact(Agent source, Agent victim, sbyte bone, BoneBodyPartType part, Vec3 contact, float damage)
        {
            if (victim == null || !victim.IsActive() || !InteractionMath.IsFinite(contact) || !InteractionMath.IsFinite(damage) || damage < .5f) return 0;
            // A body held on a blade dies when the blade comes out, not when
            // it knocks against something meanwhile.
            if (EnemyPhysicsRuntime.Penetrated(victim)) damage = Math.Min(damage, (float)Math.Floor(victim.Health - 1));
            if (damage < .5f) return 0;
            var point = new BodyPoint { Bone = bone, Part = part, World = contact };
            return Deliver(source, victim, point, contact, -Vec3.Up, MissionWeapon.Invalid, EquipmentIndex.None,
                null, DamageTypes.Blunt, damage, Math.Max(1, (int)Math.Round(damage)), 0, false, true, PushEffect.None);
        }

        // B&S penetration damage happens after the blade already passed the
        // material, so armour does not reduce it again.
        public static float ApplyPenetration(Agent source, Agent victim, sbyte bone, BoneBodyPartType part, Vec3 contact,
            float damage, DamageTypes type)
        {
            if (victim == null || !victim.IsActive() || !InteractionMath.IsFinite(contact) || !InteractionMath.IsFinite(damage) || damage < .5f) return 0;
            var point = new BodyPoint { Bone = bone, Part = part, World = contact };
            // Quiet (no sound, no hit reaction): the strike that entered already
            // played; B&S deals the deep and skewer damage with only the
            // penetration effects, and three blows in 0.2 s read as many hits.
            // The blow's magnitude is what Bannerlord throws a killed body
            // with: at the 100+ held damage of a withdrawn stab it flung the
            // corpse away. The damage stands; the magnitude is a touch.
            return Deliver(source, victim, point, contact, -Vec3.Up, MissionWeapon.Invalid, EquipmentIndex.None,
                null, type, Math.Min(damage, 1), Math.Max(1, Math.Min(500, (int)Math.Round(damage))), 0, type == DamageTypes.Pierce, true, PushEffect.None);
        }

        // SkillForceChoke's strangling blow: quiet, no reaction, and not held
        // back while the victim is held (it is held by the throat).
        public static void Strangle(Agent source, Agent victim, sbyte bone, Vec3 contact, float damage)
        {
            if (victim == null || !victim.IsActive() || damage <= 0 || !InteractionMath.IsFinite(contact)) return;
            var point = new BodyPoint { Bone = bone, Part = BoneBodyPartType.Neck, World = contact };
            float lost = Deliver(source, victim, point, contact, Vec3.Up, MissionWeapon.Invalid, EquipmentIndex.None,
                null, DamageTypes.Blunt, 0, Math.Max(1, (int)Math.Round(damage)), 0, false, true, PushEffect.None);
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "CHOKE agent={0} strangled for {1:F0} health={2:F0}", victim.Index, lost, victim.IsActive() ? victim.Health : 0));
        }

        public static void ApplyBloodLoss(Agent source, Agent victim, sbyte bone, BoneBodyPartType part, Vec3 contact, int dose)
        {
            if (victim == null || !victim.IsActive() || dose <= 0 || !InteractionMath.IsFinite(contact)) return;
            // An enemy the physics holds (blade inside, grip, physical body)
            // keeps its last point until it is let go; bleeding from an
            // earlier cut killed stab victims before withdrawal.
            if (EnemyPhysicsRuntime.Protects(victim)) dose = Math.Min(dose, (int)Math.Floor(victim.Health - 1));
            if (dose <= 0) return;
            // The initial wound already passed armor. Internal blood loss is a
            // quiet, non-staggering blow, not another weapon impact through armor.
            var point = new BodyPoint { Bone = bone, Part = part, World = contact };
            Deliver(source, victim, point, contact, Vec3.Up, MissionWeapon.Invalid, EquipmentIndex.None,
                null, DamageTypes.Cut, 0, Math.Min(6, dose), 0, false, true, PushEffect.None);
            BloodFx.Burst(BloodFx.Kind.Drip, contact, -Vec3.Up);
        }

        // A rider whose head or hand is grabbed and pulled hard comes off
        // its horse: the engine's dismount, as from a heavy blow.
        public static void PullOffMount(Agent player, Agent victim, BodyPoint point, Vec3 direction)
        {
            if (victim?.MountAgent == null || !victim.IsActive()) return;
            PhysicsLog.Info("RIDER agent=" + victim.Index + " pulled off the horse");
            Deliver(player, victim, point, point.World, direction, MissionWeapon.Invalid, EquipmentIndex.None,
                null, DamageTypes.Blunt, 0, 1, 0, false, false, PushEffect.Destabilize);
        }

        private static float Deliver(Agent player, Agent victim, BodyPoint point, Vec3 contact,
            Vec3 direction, MissionWeapon weapon, EquipmentIndex slot, WeaponComponentData usage,
            DamageTypes damageType, float raw, int damage, float absorbed, bool thrust, bool bloodLoss, PushEffect push)
        {
            // B&S staggers play as Bannerlord knockbacks; their recoil amount
            // (0.2 / 0.6 / 1) becomes the stun period. A destabilization is
            // handed to the physical body instead of a knockdown animation.
            // B&S: a blow under the first push level (effect None) does not
            // move a fighting creature at all; it fights on. The engine's own
            // 0.2 s flinch on every such touch broke its attacks and the duel.
            // Only a touch under minimumDamageToCancelAttack (5 % of its
            // health) is shrugged off: a real cut that drew no reaction at
            // all looked like a blow that never landed.
            bool shrug = push == PushEffect.None && victim.Controller == AgentControllerType.AI && !victim.IsPaused &&
                !bloodLoss && damage < victim.HealthLimit * .05f;
            float stun = bloodLoss || shrug ? 0 : push == PushEffect.None || push == PushEffect.Destabilize ? .2f : PushReaction.Recoil(push);
            BlowFlags flags = bloodLoss ? BlowFlags.NoSound | BlowFlags.ShrugOff : shrug ? BlowFlags.ShrugOff :
                push == PushEffect.StaggerMedium || push == PushEffect.StaggerFull ? BlowFlags.KnockBack : BlowFlags.None;
            // A rider is not knocked back (Mission.MeleeHitCallback gives a
            // mounted victim CanDismount instead): a destabilizing blow, the
            // one that sends a man on foot down, unseats it.
            bool rider = victim.MountAgent != null && victim.MountAgent.RiderAgent == victim;
            if (rider && !bloodLoss) flags = push == PushEffect.Destabilize ? BlowFlags.CanDismount : flags & ~BlowFlags.KnockBack;
            if (rider && push == PushEffect.Destabilize && EnemyPhysicsSettings.Trace)
                PhysicsLog.Info("RIDER agent=" + victim.Index + " unseated by a destabilizing blow");
            Mission mission = Mission.Current;
            int victimIndex = victim.Index;
            // A lethal blow without attacker crashes the custom battle morale
            // model (null affector); an unattributed ragdoll fall or a probe
            // cut is the victim's own accident.
            if (player == null) player = victim;
            float before = victim.Health;
            var blow = new Blow(player?.Index ?? -1)
            {
                BoneIndex = point.Bone, VictimBodyPart = point.Part,
                GlobalPosition = contact, Direction = direction, SwingDirection = direction,
                DamageType = damageType, StrikeType = thrust ? StrikeType.Thrust : StrikeType.Swing,
                AttackType = AgentAttackType.Standard, BaseMagnitude = raw,
                InflictedDamage = damage,
                AbsorbedByArmor = absorbed, DamageCalculated = true,
                DefenderStunPeriod = stun, MovementSpeedDamageModifier = bloodLoss ? 0 : 1,
                BlowFlag = flags
            };
            blow.WeaponRecord.FillAsMeleeBlow(weapon.IsEmpty ? null : weapon.Item, usage,
                usage == null ? -1 : (int)slot, player?.Monster.MainHandItemBoneIndex ?? -1);
            var collision = AttackCollisionData.GetAttackCollisionDataForDebugPurpose(
                false, false, false, true, false, false, false, false, false, thrust, false, false,
                CombatCollisionResult.StrikeAgent, usage == null ? -1 : (int)slot,
                (int)blow.StrikeType, (int)damageType, point.Bone, point.Part,
                player?.Monster.MainHandBoneIndex ?? -1, Agent.UsageDirection.AttackUp, -1,
                CombatHitResultFlags.NormalHit, 1, 0, 0, stun, 0, 0, 0, 0,
                Vec3.Up, direction, contact, Vec3.Zero, Vec3.Zero, victim.Velocity, Vec3.Up);
            collision.BaseMagnitude = blow.BaseMagnitude;
            collision.MovementSpeedDamageModifier = blow.MovementSpeedDamageModifier;
            collision.InflictedDamage = blow.InflictedDamage;
            collision.AbsorbedByArmor = (int)blow.AbsorbedByArmor;
            Perf.Mark("blow_prepared");
            DeliveringOwnBlow = true;
            try { victim.RegisterBlow(blow, in collision); }
            finally { DeliveringOwnBlow = false; }
            Perf.Mark("engine_blow");
            // A blow can synchronously remove its victim or end the mission.
            // Query the captured index before reading the native health again.
            if (mission == null || mission != Mission.Current || mission.IsFinalized ||
                mission.FindAgentWithIndex(victimIndex) != victim || !victim.IsActive()) return 0;
            float lost = before - victim.Health;
            // The wound stays where the edge or the point went in.
            if (!bloodLoss && lost > 0 && (damageType == DamageTypes.Cut || damageType == DamageTypes.Pierce))
            {
                if (thrust || damageType == DamageTypes.Pierce) WoundDecal.Stab(victim, point.Bone, contact, direction);
                else WoundDecal.Cut(victim, point.Bone, contact, direction);
            }
            if (EnemyPhysicsSettings.Trace && !bloodLoss)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "HIT agent={0} part={1} type={2} damage={3} lost={4:F0} push={5} shrugged={6} stun={7:F2} health={8:F0}",
                    victimIndex, point.Part, damageType, damage, lost, push, shrug, stun, victim.Health));
            return InteractionMath.IsFinite(lost) ? Math.Max(0, lost) : 0;
        }
    }
}
