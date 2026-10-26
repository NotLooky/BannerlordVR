using System;

namespace BannerlordVR.VR.Physics
{
    public enum BodyZone { Head, Neck, Torso, Arm, Hand, Leg, Foot }

    // Blade & Sorcery Damager.UpdatePenetration damage. Once a pierce goes
    // deeper than the part's penetrationPierceDeepDepth, the part takes
    // DamagerData.penetrationDamage x penetrationPierceDeepDamageMultiplier
    // (slashes: x penetrationSlashDamageMultiplier as soon as they enter);
    // a pierce leaving the far side (skewer) adds penetrationSkewerDamage x
    // penetrationSkewerDamageMultiplier. Values: Creature_HumanMale ragdoll
    // parts and the Damager_* catalog. B&S humans have 50 health; damage is
    // scaled to the Bannerlord victim's health limit.
    public static class PenetrationDamage
    {
        public const float BladeAndSorceryHumanHealth = 50;

        public static float DeepDepth(BodyZone zone)
        {
            switch (zone)
            {
                case BodyZone.Neck: return .02f;
                case BodyZone.Head:
                case BodyZone.Hand: return .04f;
                default: return .1f;
            }
        }

        public static float PierceMultiplier(BodyZone zone)
        {
            switch (zone)
            {
                case BodyZone.Head:
                case BodyZone.Neck: return 32;
                case BodyZone.Torso: return 2;
                case BodyZone.Arm:
                case BodyZone.Leg: return .5f;
                default: return .25f;
            }
        }

        public static float SlashMultiplier(BodyZone zone)
        {
            switch (zone)
            {
                case BodyZone.Neck: return 32;
                case BodyZone.Head: return 2;
                case BodyZone.Torso: return 1;
                case BodyZone.Arm:
                case BodyZone.Leg: return .5f;
                default: return .25f;
            }
        }

        public static float SkewerMultiplier(BodyZone zone)
        {
            switch (zone)
            {
                case BodyZone.Head:
                case BodyZone.Neck: return 4;
                case BodyZone.Torso: return 3;
                case BodyZone.Arm:
                case BodyZone.Leg: return .5f;
                default: return .25f;
            }
        }

        public static float Damage(PenetrationProfile profile)
        {
            switch (profile)
            {
                case PenetrationProfile.SwordPierce: return 5;
                case PenetrationProfile.DaggerPierce:
                case PenetrationProfile.Arrow: return 7.5f;
                case PenetrationProfile.WarpickPierce: return 20;
                case PenetrationProfile.DaggerSlash: return 5;
                case PenetrationProfile.SaberSlash: return 15;
                default: return 25;
            }
        }

        // penetrationSkewerDetection applies to pierce damagers only.
        public static float SkewerDamage(PenetrationProfile profile)
        {
            switch (profile)
            {
                case PenetrationProfile.SwordPierce:
                case PenetrationProfile.WarpickPierce: return 15;
                case PenetrationProfile.DaggerPierce: return 7.5f;
                default: return 0;
            }
        }

        // B&S tests whether the blade point left the part's colliders.
        // Bannerlord exposes capsules only through the limb ray cast, so the
        // through-depth is the part's typical thickness.
        public static float SkewerDepth(BodyZone zone)
        {
            switch (zone)
            {
                case BodyZone.Torso: return .3f;
                case BodyZone.Head: return .2f;
                case BodyZone.Neck: return .12f;
                default: return .12f;
            }
        }

        public static float HealthScale(float healthLimit) =>
            InteractionMath.IsFinite(healthLimit) && healthLimit > 0 ? healthLimit / BladeAndSorceryHumanHealth : 2;

        public static float DeepDamage(PenetrationProfile profile, BodyZone zone, float healthLimit)
        {
            bool pierce = PenetrationJoint.Get(profile).Pierce;
            return Damage(profile) * (pierce ? PierceMultiplier(zone) : SlashMultiplier(zone)) * HealthScale(healthLimit);
        }

        public static float SkewerTotal(PenetrationProfile profile, BodyZone zone, float healthLimit) =>
            SkewerDamage(profile) * SkewerMultiplier(zone) * HealthScale(healthLimit);

        // Depth at which the deep damage triggers: 0 for slashes.
        // Arrows have penetrationDeepDepthMultiplier 0: deep on entry.
        public static float DeepTrigger(PenetrationProfile profile, BodyZone zone) =>
            profile == PenetrationProfile.Arrow || !PenetrationJoint.Get(profile).Pierce ? 0 : DeepDepth(zone);
    }
}
