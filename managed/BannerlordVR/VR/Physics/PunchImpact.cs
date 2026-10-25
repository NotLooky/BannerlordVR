using System;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    // Blade & Sorcery fist (Damager_Punch.json, DamageModifier_Punch.json).
    // Near the player every B&S creature is physical, so a fist striking a
    // standing creature is a PhysX contact: the player's hand, held to the
    // controller by the arm joint (Ragdoll.playerArmMaxPositionForce 3000 N),
    // keeps going and shoves the struck part out of its way. Once the
    // creature is no longer alive and standing (destabilized or dead) the
    // damager also runs AddForceCoroutine for 0.15 s.
    public static class PunchImpact
    {
        public const float ArmMaxForce = 3000, StepRate = 90;
        // The contact lasts about two PhysX steps before the part is out of
        // the fist's way or the arm joint gives.
        public const float ContactSteps = 2;
        public const float MaxImpulse = ArmMaxForce * ContactSteps / StepRate;

        // Damager_Punch: addForce 100 (Acceleration) over addForceDuration
        // 0.15 s, x2 on the struck part, x0.5 on the rest of its region.
        public const float AddForce = 100, AddForceDuration = .15f, AddForcePart = 2, AddForceOther = .5f;

        // Damager_Punch intensityMinVelocity / intensityMaxVelocity (effects
        // and haptics).
        public const float IntensityMin = 2, IntensityMax = 9;

        public struct Row
        {
            public float MinVelocity, DamageMultiplier;
            public float[] Push;
        }
        private static readonly Row Flesh = new Row { MinVelocity = 2, DamageMultiplier = .5f, Push = new float[] { 2, 4, 6, 12, 24 } };
        private static readonly Row Leather = new Row { MinVelocity = 3, DamageMultiplier = .4f, Push = new float[] { 3, 9, 12, 18, 24 } };
        private static readonly Row Chainmail = new Row { MinVelocity = 4.5f, DamageMultiplier = .3f, Push = new float[] { 3, 9, 12, 24, 26 } };

        // Bannerlord plate and lamellar play as B&S leather, as for blades.
        public static Row For(PenetrationMaterial material)
        {
            switch (material)
            {
                case PenetrationMaterial.Flesh: return Flesh;
                case PenetrationMaterial.Chainmail: return Chainmail;
                default: return Leather;
            }
        }

        // Damager_Punch.velocityDamageCurve: (2, 1) to (20, 5), flat tangents.
        public static float Curve(float speed)
        {
            if (!InteractionMath.IsFinite(speed) || speed <= 2) return 1;
            if (speed >= 20) return 5;
            float t = (speed - 2) / 18;
            return 1 + 4 * t * t * (3 - 2 * t);
        }

        // B&S health points, scaled to the victim's health limit.
        public static float Damage(float speed, Row row, float healthLimit)
        {
            if (!InteractionMath.IsFinite(speed) || speed < row.MinVelocity) return 0;
            return Curve(speed) * row.DamageMultiplier * PenetrationDamage.HealthScale(healthLimit);
        }

        public static int PushLevel(float speed, Row row)
        {
            if (!InteractionMath.IsFinite(speed) || speed <= 0 || row.Push == null) return 0;
            for (int i = row.Push.Length - 1; i >= 0; i--) if (speed > row.Push[i]) return i;
            return 0;
        }

        public static float Intensity(float speed) =>
            !InteractionMath.IsFinite(speed) ? 0 : Math.Max(0, Math.Min(1, (speed - IntensityMin) / (IntensityMax - IntensityMin)));

        // Impulse the fist gives a part of this mass: the part is brought to
        // the fist's speed, within what the arm joint can push.
        public static Vec3 ContactImpulse(float partMass, Vec3 velocity)
        {
            if (!InteractionMath.IsFinite(velocity) || !InteractionMath.IsFinite(partMass) || partMass <= 0) return Vec3.Zero;
            return InteractionMath.ClampMagnitude(velocity * partMass, MaxImpulse);
        }

        // AddForceCoroutine acceleration on one part for one step of dt.
        public static Vec3 AddForceAcceleration(Vec3 velocity, bool struckPart, float dt) =>
            !InteractionMath.IsFinite(velocity) || !InteractionMath.IsFinite(dt) || dt <= 0 ? Vec3.Zero :
            velocity * (AddForce * (struckPart ? AddForcePart : AddForceOther) * Math.Min(dt, .1f));
    }
}
