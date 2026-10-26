using System;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    public enum SliceJoint { Neck, Elbow, Wrist, Knee, Ankle }

    // Blade & Sorcery Damager dismemberment. A cutting hit (penetration Hit,
    // not a thrust) faster than DamagerData.dismembermentMinVelocity slices a
    // part when Damager.CheckSlice passes: the contact lies within the part's
    // sliceWidth of its slice point, and the slice axis is within
    // dismemberment{Vertical,Horizontal}Angle (30 deg) of perpendicular to the
    // blade (up) and to its edge (forward). Every human part has
    // sliceForceKill: the cut kills (Creature_HumanMale ragdoll data).
    // B&S knockout uses DamageModifier allowKnockout / knockoutAllowedMinVelocity;
    // the human brains set knockoutDuration 0, so a knockout is a forced fall.
    public static class Dismemberment
    {
        // Damager_SwordSlash1H tiers: dismembermentMaxVerticalAngle 90 (the
        // blade's tilt never refuses a slice), dismembermentMaxHorizontalAngle
        // 35 (the cut's direction across the limb). 30 for both refused nearly
        // every neck cut.
        public const float MaxVerticalAngle = 90, MaxHorizontalAngle = 35, SliceWidth = .12f;

        // Damager_*.json dismembermentMinVelocity; 0 when the damager cannot slice.
        public static float BaseMinVelocity(PenetrationProfile profile, bool twoHanded)
        {
            switch (profile)
            {
                case PenetrationProfile.SaberSlash: return twoHanded ? 11 : 12;
                case PenetrationProfile.DaggerSlash: return 13;
                case PenetrationProfile.AxeSlash1H:
                case PenetrationProfile.AxeSlash2H: return 9;
                default: return 0;
            }
        }

        // Damager.tiers[].dismembermentMinVelocityMultiplier (B&S tiers 0-5,
        // Bannerlord Tier1-Tier6): a poor blade needs 2-2.5 times the speed,
        // a fine one 0.8-0.9. Without it every tier cut at the tier-2 speed.
        private static readonly float[] Sword1H = { 2.5f, 1.5f, 1, .9f, .8f, .8f }, Sword2H = { 2, 1.5f, 1, .9f, .8f, .8f },
            Axe2H = { 2, 1.5f, 1, .95f, .9f, .9f };
        public static float TierMultiplier(PenetrationProfile profile, bool twoHanded, int tier)
        {
            float[] row = profile == PenetrationProfile.AxeSlash2H ? Axe2H : profile == PenetrationProfile.SaberSlash && twoHanded ? Sword2H : Sword1H;
            return row[Math.Max(0, Math.Min(row.Length - 1, tier))];
        }
        public static float MinVelocity(PenetrationProfile profile, bool twoHanded, int tier = 2) =>
            BaseMinVelocity(profile, twoHanded) * TierMultiplier(profile, twoHanded, tier);

        // dismemberment{Vertical,Horizontal}Angle per damager: swords 90/35,
        // axes 80/45, daggers 60/35.
        public static void Angles(PenetrationProfile profile, out float vertical, out float horizontal)
        {
            switch (profile)
            {
                case PenetrationProfile.AxeSlash1H:
                case PenetrationProfile.AxeSlash2H: vertical = 80; horizontal = 45; return;
                case PenetrationProfile.DaggerSlash: vertical = 60; horizontal = 35; return;
                default: vertical = MaxVerticalAngle; horizontal = MaxHorizontalAngle; return;
            }
        }

        public static bool CheckSlice(Vec3 sliceAxis, Vec3 bladeUp, Vec3 bladeForward, float contactDistance,
            PenetrationProfile profile = PenetrationProfile.SaberSlash)
        {
            if (!InteractionMath.IsFinite(sliceAxis) || !InteractionMath.IsFinite(bladeUp) || !InteractionMath.IsFinite(bladeForward) ||
                !InteractionMath.IsFinite(contactDistance) || contactDistance >= SliceWidth) return false;
            if (sliceAxis.LengthSquared < 1e-8f || bladeUp.LengthSquared < 1e-8f || bladeForward.LengthSquared < 1e-8f) return false;
            // Damager.CheckSlice compares the dot products with angle / 90.
            Angles(profile, out float vertical, out float horizontal);
            Vec3 axis = sliceAxis.NormalizedCopy();
            return Math.Abs(InteractionMath.Dot(axis, bladeUp.NormalizedCopy())) < vertical / 90 &&
                   Math.Abs(InteractionMath.Dot(axis, bladeForward.NormalizedCopy())) < horizontal / 90;
        }

        public static bool Slices(PenetrationProfile profile, bool twoHanded, bool thrust, float speed,
            Vec3 sliceAxis, Vec3 bladeUp, Vec3 bladeForward, float contactDistance, int tier = 2)
        {
            float min = MinVelocity(profile, twoHanded, tier);
            return !thrust && min > 0 && InteractionMath.IsFinite(speed) && speed > min &&
                CheckSlice(sliceAxis, bladeUp, bladeForward, contactDistance, profile);
        }

        // The DamageModifier knockout rows are no longer read by ThunderRoad:
        // only SkillDiscombobulate knocks a creature out. CheckStunConditions:
        // a Blunt blow (fists, kicks, maces, pommels and flats) to the Head
        // part at impactVelocity 5 m/s or more, whatever the armour; the
        // creature is out for stunTime 6 s. A blow that kills does not stun.
        public const float KnockoutMinVelocity = 5, KnockoutSeconds = 6;
        public static bool KnocksOut(PushMaterial material, BodyZone zone, float speed)
        {
            bool blunt = material == PushMaterial.Blunt || material == PushMaterial.BluntLight ||
                material == PushMaterial.Punch || material == PushMaterial.Kick;
            return blunt && zone == BodyZone.Head && InteractionMath.IsFinite(speed) && speed >= KnockoutMinVelocity;
        }
    }
}
