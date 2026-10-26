using System;

namespace BannerlordVR.VR.Physics
{
    // Blade & Sorcery ragdoll collision damage: a part of a physical body that
    // strikes something takes Damager_Ragdoll.velocityDamageCurve(impact
    // speed), a Hermite curve from (0, 0) to (12 m/s, 5) clamped beyond, once
    // the impact exceeds DamageModifier_Ragdoll.minVelocity (5 m/s, 3 m/s for
    // the head). Bannerlord reports no ragdoll contacts, so an impact is a
    // sudden loss of most of a part's speed between two samples.
    public static class RagdollImpact
    {
        public const float BodyMinVelocity = 5, HeadMinVelocity = 3, Cooldown = .2f;

        public static float Damage(float impactSpeed)
        {
            if (!InteractionMath.IsFinite(impactSpeed) || impactSpeed <= 0) return 0;
            if (impactSpeed >= 12) return 5;
            float t = impactSpeed / 12, t2 = t * t, t3 = t2 * t;
            // Unweighted Hermite with the catalog tangents (0.2825 and 0.8298 per m/s).
            float h = (t3 - 2 * t2 + t) * 12 * .282455564f + (-2 * t3 + 3 * t2) * 5 + (t3 - t2) * 12 * .829812765f;
            return Math.Max(0, Math.Min(5, h));
        }

        // Returns the impact speed when the part collided, else 0.
        public static float Detect(float previousSpeed, float speed, float speedChange, bool head)
        {
            if (!InteractionMath.IsFinite(previousSpeed) || !InteractionMath.IsFinite(speed) || !InteractionMath.IsFinite(speedChange))
                return 0;
            float min = head ? HeadMinVelocity : BodyMinVelocity;
            // A collision stops most of the motion; a smooth swing does not.
            if (speedChange <= min || speed > previousSpeed * .5f) return 0;
            return speedChange;
        }
    }
}
