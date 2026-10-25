using System;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Tales of Glory 4's stab (WepScript, TOG4.Game), on trial in place of the
    // Blade & Sorcery one (enemy_physics_stab_model=tog; bs restores it).
    //
    // Entry (OnCollisionEnter): the weapon moves along its blade (velocity
    // within 0.8 of the stab collider's forward) and its kinetic energy
    // 1/2 m v^2, v times config.ImpactBoost 1.2 and capped at 400 J, beats
    // 20 J times GetThresholdModifier. Never into a hand.
    // Effect: a head, neck, chest or pelvis is a death (Suffocate, GutStab,
    // FadeToDeath(1 s)); a limb is crippled and takes kinetic/20 (kinetic
    // capped at 300) times GetDamageModifier.
    // Joint (FixedUpdate): locked across the blade, free to slide along it,
    // the anchor following the body out at 50/s and in at 10/s; it breaks
    // once slid within 5 cm of the blade's end, kicking the part off the
    // blade towards the hilt at its mass times StabJointOutMult 5.
    internal static class TogStab
    {
        public const float ImpactBoost = 1.2f, Threshold = 20, MaxKinetic = 400, LimbKineticCap = 300, Align = .8f;
        // The kill lands as the fade ends (ActiveRagdollBody.FadeToDeath).
        public const float BreakNear = .05f, OutKick = 5, DeathDelay = ActiveRagdollBody.FadeStart / ActiveRagdollBody.FadeRate, RecentHit = 1;
        public const float StabIn = 10, StabOut = 50;
        public const float ChainmailThreshold = 2, PlateThreshold = 3;

        public static float Kinetic(ItemObject item, Vec3 velocity)
        {
            float mass = Math.Max(.5f, item?.Weight ?? 1);
            float speed = velocity.Length * ImpactBoost;
            return Math.Min(MaxKinetic, .5f * mass * speed * speed);
        }

        // WepDesc.StabBoost: TOG adds a per-weapon energy to the stab test
        // (WepKinetic + wp.StabBoost > 20 x threshold). Spears and lances
        // are built to stab, daggers nearly as much, swords a little; a
        // sharper point (higher thrust damage) adds up to 20 J more.
        public const float SpearBoost = 30, DaggerBoost = 20, SwordBoost = 10, OtherBoost = 5, SharpBoostMax = 20;
        public static float StabBoost(WeaponComponentData usage)
        {
            if (usage == null) return 0;
            float boost;
            switch (usage.WeaponClass)
            {
                case WeaponClass.OneHandedPolearm:
                case WeaponClass.TwoHandedPolearm:
                case WeaponClass.Javelin: boost = SpearBoost; break;
                case WeaponClass.Dagger:
                case WeaponClass.ThrowingKnife: boost = DaggerBoost; break;
                case WeaponClass.OneHandedSword:
                case WeaponClass.TwoHandedSword: boost = SwordBoost; break;
                default: boost = OtherBoost; break;
            }
            if (usage.ThrustDamageType == DamageTypes.Pierce || usage.ThrustDamageType == DamageTypes.Cut)
                boost += Math.Max(0, Math.Min(SharpBoostMax, (usage.ThrustDamage - 20) * .5f));
            return boost;
        }

        private static bool Helmet(Agent agent, BoneBodyPartType part) =>
            part == BoneBodyPartType.Head && agent?.SpawnEquipment != null && !agent.SpawnEquipment[EquipmentIndex.Head].IsEmpty &&
            agent.SpawnEquipment[EquipmentIndex.Head].Item?.StringId != "bvr_severed_head";

        private static bool Down(EnemyBody body) =>
            EnemyPhysicsRuntime.IsDying(body) || EnemyPhysicsRuntime.HasLivingLease(body.Agent);

        // GetThresholdModifier.
        // TOG's TestHelmetDetection: the helmet counts only where the blade's
        // line meets it before the head; an open face is the head's own
        // collider, with no helmet x20 and no metal. Every helmet worn made
        // the whole head 180-600 J, a face stab out of reach. Bannerlord
        // helmets cover the crown, the back and the sides; a closed one (its
        // beard covered whole in mail or plate: visors, face masks, full
        // helms) the face too.
        public const float FaceTop = .04f, FaceCos = .5f;
        public static bool FaceExposed(EnemyBody body, BoneBodyPartType part, Vec3? at)
        {
            if (part != BoneBodyPartType.Head || !at.HasValue || body?.Agent == null) return false;
            Agent agent = body.Agent;
            ArmorComponent armor = agent.SpawnEquipment?[EquipmentIndex.Head].Item?.ArmorComponent;
            if (armor != null && armor.BeardCoverType == ArmorComponent.BeardCoverTypes.All &&
                (armor.MaterialType == ArmorComponent.ArmorMaterialTypes.Chainmail || armor.MaterialType == ArmorComponent.ArmorMaterialTypes.Plate))
                return false;
            sbyte head = agent.Monster != null ? agent.Monster.HeadLookDirectionBoneIndex : (sbyte)-1;
            if (head < 0 || head >= body.Skeleton.GetBoneCount()) return false;
            MatrixFrame bone = body.Frame.TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(head));
            // BannerlordVR's head sphere sits 0.1 m over the head bone, at the eyes.
            Vec3 d = at.Value - (bone.origin + new Vec3(0, 0, .1f));
            Vec2 flat = d.AsVec2, look = agent.LookDirection.AsVec2;
            if (d.z > FaceTop || flat.Length < 1e-3f || look.Length < 1e-3f) return false;
            return (flat.x * look.x + flat.y * look.y) / (flat.Length * look.Length) > FaceCos;
        }

        public static float ThresholdModifier(Agent player, EnemyBody body, BoneBodyPartType part, float lastHitAge, Vec3? at = null)
        {
            float m = 1;
            bool face = FaceExposed(body, part, at);
            bool helmet = !face && Helmet(body.Agent, part);
            if (body.IsCorpse || Down(body))
            {
                m *= .5f;
                if (helmet) m *= 20;
                return m;
            }
            if (player?.MountAgent != null) m *= .5f;
            else if (lastHitAge < RecentHit) m *= .9f;
            // TOG has one metal (x5, full plate); Bannerlord's mail and its
            // plate pieces are scaled under it: at x5 every armoured soldier
            // needed 100 J (9 m/s with a 1.5 kg spear) and 93 stabs in 103
            // were refused.
            switch (face ? PenetrationMaterial.Flesh : BodyPenetration.MaterialFor(body.Agent, part))
            {
                case PenetrationMaterial.Flesh: m *= .9f; break;
                case PenetrationMaterial.Chainmail: m *= ChainmailThreshold; break;
                case PenetrationMaterial.Plate: m *= PlateThreshold; break;
            }
            if (helmet) m *= 20;
            return m;
        }

        // GetDamageModifier.
        public static float DamageModifier(EnemyBody body, BoneBodyPartType part, float lastHitAge, Vec3? at = null)
        {
            float m = 1;
            bool face = FaceExposed(body, part, at);
            bool helmet = !face && Helmet(body.Agent, part);
            if (Down(body)) return helmet ? 1.5f : 3;
            if (lastHitAge < RecentHit) m *= 1.5f;
            switch (face ? PenetrationMaterial.Flesh : BodyPenetration.MaterialFor(body.Agent, part))
            {
                case PenetrationMaterial.Leather: m *= .75f; break;
                case PenetrationMaterial.Chainmail:
                case PenetrationMaterial.Plate: m *= .5f; break;
            }
            if (helmet) m *= .5f;
            return m;
        }

        // Suffocate (head, neck), GutStab (chest, pelvis): the stab kills.
        public static bool Lethal(BoneBodyPartType part) =>
            part == BoneBodyPartType.Head || part == BoneBodyPartType.Neck || part == BoneBodyPartType.Chest || part == BoneBodyPartType.Abdomen;

        public static bool Hand(EnemyBody body, sbyte bone) => (body.Skeleton.GetBoneName(bone) ?? "").EndsWith("hand");
    }
}
