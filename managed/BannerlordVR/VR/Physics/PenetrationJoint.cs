using System;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    public enum PenetrationProfile { DaggerPierce, SwordPierce, WarpickPierce, Arrow, DaggerSlash, SaberSlash, AxeSlash1H, AxeSlash2H }
    public enum PenetrationMaterial { Flesh, Leather, Chainmail, Plate }

    // Blade & Sorcery Damager penetration joint. The weapon is locked to the
    // part laterally; along the blade a damper without spring brakes motion,
    // with DamagerData.penetrationHeldDamperIn while the depth grows and
    // penetrationHeldDamperOut while it shrinks (penetrationDamper when the
    // item is not held), times the target material's
    // penetrationDamperMultiplier from the DamageModifier catalog.
    public static class PenetrationJoint
    {
        public struct Profile
        {
            public float HeldIn, HeldOut, Loose;
            public bool Pierce;
        }

        public static Profile Get(PenetrationProfile profile)
        {
            switch (profile)
            {
                case PenetrationProfile.DaggerPierce: return new Profile { HeldIn = 500, HeldOut = 250, Loose = 500, Pierce = true };
                case PenetrationProfile.SwordPierce: return new Profile { HeldIn = 1000, HeldOut = 500, Loose = 1250, Pierce = true };
                case PenetrationProfile.WarpickPierce: return new Profile { HeldIn = 500, HeldOut = 1500, Loose = 3000, Pierce = true };
                case PenetrationProfile.Arrow: return new Profile { HeldIn = 750, HeldOut = 1000, Loose = 5000, Pierce = true };
                case PenetrationProfile.DaggerSlash: return new Profile { HeldIn = 10000, HeldOut = 100, Loose = 100 };
                case PenetrationProfile.SaberSlash: return new Profile { HeldIn = 10000, HeldOut = 100, Loose = 100 };
                case PenetrationProfile.AxeSlash1H: return new Profile { HeldIn = 0, HeldOut = 10000, Loose = 5000 };
                default: return new Profile { HeldIn = 0, HeldOut = 100000, Loose = 5000 };
            }
        }

        // DamagerData.penetrationShortDepth (penetrationShortDepthAngle 30 for
        // every damager): the joint's angular X limit goes from 30 degrees at
        // the surface to 0 at this depth; 0 means locked from the start.
        public const float ShortDepthAngle = 30;
        public static float ShortDepth(PenetrationProfile profile)
        {
            switch (profile)
            {
                case PenetrationProfile.DaggerPierce: return .03f;
                case PenetrationProfile.Arrow: return 0;
                case PenetrationProfile.AxeSlash1H:
                case PenetrationProfile.AxeSlash2H: return .12f;
                default: return .1f;
            }
        }
        // Damager.RefreshJointDrive: Lerp(angle, 0, InverseLerp(0, shortDepth, depth)), radians.
        public static float AngleLimit(PenetrationProfile profile, float depth)
        {
            float shortDepth = ShortDepth(profile);
            if (shortDepth <= 0) return 0;
            float t = Math.Max(0, Math.Min(1, depth / shortDepth));
            return ShortDepthAngle * (1 - t) * (float)(Math.PI / 180);
        }

        // DamagerData.penetrationPressureForceCurve, keyed on the contact's
        // relative speed: (time, value, inTangent, outTangent).
        private static readonly float[,] PierceCurve = { { 0, 5, 0, 0 }, { .5f, 1.5f, -7.125f, -7.125f }, { 1, .25f, 0, 0 } };
        private static readonly float[,] SlashCurve = { { 0, 5, 0, 0 }, { .5f, 1.5f, -7.125f, -7.125f }, { 1, .35f, 0, 0 } };
        private static readonly float[,] AxeCurve = { { 0, 5, 0, 0 }, { 2, .5f, 0, 0 } };
        private static readonly float[,] ArrowCurve = { { 0, 5, 0, 0 }, { 2, 5, 0, 0 } };
        public static float PressureThreshold(PenetrationProfile profile, float relativeSpeed)
        {
            switch (profile)
            {
                case PenetrationProfile.DaggerSlash:
                case PenetrationProfile.SaberSlash: return Evaluate(SlashCurve, relativeSpeed);
                case PenetrationProfile.AxeSlash1H:
                case PenetrationProfile.AxeSlash2H: return Evaluate(AxeCurve, relativeSpeed);
                case PenetrationProfile.Arrow: return Evaluate(ArrowCurve, relativeSpeed);
                default: return Evaluate(PierceCurve, relativeSpeed);
            }
        }
        // Unity AnimationCurve.Evaluate for unweighted keys (cubic Hermite),
        // clamped outside the keys.
        public static float Evaluate(float[,] keys, float t)
        {
            int n = keys.GetLength(0);
            if (!(t > keys[0, 0])) return keys[0, 1];
            if (t >= keys[n - 1, 0]) return keys[n - 1, 1];
            int i = 0;
            while (t > keys[i + 1, 0]) i++;
            float t0 = keys[i, 0], t1 = keys[i + 1, 0], span = t1 - t0;
            float s = (t - t0) / span, s2 = s * s, s3 = s2 * s;
            float p0 = keys[i, 1], p1 = keys[i + 1, 1], m0 = keys[i, 3] * span, m1 = keys[i + 1, 2] * span;
            return (2 * s3 - 3 * s2 + 1) * p0 + (s3 - 2 * s2 + s) * m0 + (-2 * s3 + 3 * s2) * p1 + (s3 - s2) * m1;
        }
        // CollisionInstance.pressureForce = contact impulse / weapon mass, the
        // speed one PhysX step of contact takes off the weapon. The VR blade is
        // not a body, so the impulse is rebuilt from what B&S's arm joint
        // would push into the surface: the approach speed plus the arm spring
        // on the controller's overshoot past the contact, over one 90 Hz step.
        public const float PhysicsStep = 1f / 90;
        public static float Pressure(float approachSpeed, float overshoot, float weaponMass)
        {
            float arm = Math.Min(RagdollPinDrive.HandMaxForce, RagdollPinDrive.HandSpring * Math.Max(0, overshoot));
            return Math.Max(0, approachSpeed) + arm / Math.Max(.2f, weaponMass) * PhysicsStep;
        }

        // penetrationDamperMultiplier of the DamageModifier row for the
        // material and weapon tier (PenetrationModifiers).
        public static float MaterialMultiplier(PenetrationProfile profile, PenetrationMaterial material, int tier) =>
            PenetrationModifiers.Get(profile, material, tier).DamperMultiplier;

        // penetrationMinVelocity: slower contacts do not enter; a row that
        // refuses penetration never does.
        public static float MinVelocity(PenetrationProfile profile, PenetrationMaterial material, int tier)
        {
            PenetrationModifiers.Row row = PenetrationModifiers.Get(profile, material, tier);
            return row.Allow ? row.MinVelocity : float.PositiveInfinity;
        }

        public static float Damper(PenetrationProfile profile, PenetrationMaterial material, bool held, bool deepening, int tier)
        {
            Profile p = Get(profile);
            float d = held ? (deepening ? p.HeldIn : p.HeldOut) : p.Loose;
            return d * MaterialMultiplier(profile, material, tier);
        }

        // Force on the pierced part along the blade axis: it follows the blade
        // through the damper. The cap keeps the explicit damper stable for the
        // part's mass at one PhysX step of latency.
        public static Vec3 AxialForce(Vec3 axis, Vec3 partVelocity, Vec3 bladeVelocity, float damper, float partMass)
        {
            if (!InteractionMath.IsFinite(axis) || !InteractionMath.IsFinite(partVelocity) || !InteractionMath.IsFinite(bladeVelocity) ||
                !InteractionMath.IsFinite(damper) || damper <= 0 || !InteractionMath.IsFinite(partMass) || partMass <= 0) return Vec3.Zero;
            float length = axis.Length;
            if (length < 1e-4f) return Vec3.Zero;
            axis = axis * (1 / length);
            float relative = InteractionMath.Dot(bladeVelocity - partVelocity, axis);
            float c = Math.Min(damper, partMass * RagdollPinDrive.MaxDampingRate);
            return axis * (relative * c);
        }
    }
}
