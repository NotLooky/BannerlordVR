using System;
using BannerlordVR.VR.Physics;
using TaleWorlds.Library;

internal static class PenetrationJointTests
{
    public static void Run(Action<bool, string> check)
    {
        check(PenetrationJoint.Damper(PenetrationProfile.SwordPierce, PenetrationMaterial.Flesh, true, true, 2) == 1000 &&
              PenetrationJoint.Damper(PenetrationProfile.SwordPierce, PenetrationMaterial.Flesh, true, false, 2) == 500,
            "Held tier 2 sword: 1000 going in, 500 coming out (DamagerData SwordPierce, flesh x1)");
        check(PenetrationJoint.Damper(PenetrationProfile.SwordPierce, PenetrationMaterial.Chainmail, true, true, 2) == 6000 &&
              PenetrationJoint.Damper(PenetrationProfile.SwordPierce, PenetrationMaterial.Chainmail, true, true, 3) == 4000,
            "Chainmail brakes by tier (DamageModifier_Pierce x6 for T1-T2, x4 from T3)");
        check(PenetrationJoint.Damper(PenetrationProfile.SwordPierce, PenetrationMaterial.Plate, true, true, 2) ==
              PenetrationJoint.Damper(PenetrationProfile.SwordPierce, PenetrationMaterial.Leather, true, true, 2) &&
              float.IsPositiveInfinity(PenetrationModifiers.Get(PenetrationProfile.SwordPierce, PenetrationMaterial.Plate, 3).DepthLimit(4)),
            "Bannerlord plate and lamellar use the leather rows: pierced along the whole blade");
        check(PenetrationJoint.Damper(PenetrationProfile.AxeSlash1H, PenetrationMaterial.Flesh, true, true, 3) == 0 &&
              PenetrationJoint.Damper(PenetrationProfile.AxeSlash1H, PenetrationMaterial.Flesh, true, false, 3) == 10000,
            "An axe bites freely and is hard to pull out");
        check(PenetrationJoint.Damper(PenetrationProfile.SwordPierce, PenetrationMaterial.Flesh, false, true, 2) == 1250,
            "A loose weapon uses penetrationDamper");
        check(PenetrationJoint.MinVelocity(PenetrationProfile.SwordPierce, PenetrationMaterial.Flesh, 1) == .5f &&
              PenetrationJoint.MinVelocity(PenetrationProfile.SwordPierce, PenetrationMaterial.Leather, 0) == 6 &&
              PenetrationJoint.MinVelocity(PenetrationProfile.SwordPierce, PenetrationMaterial.Chainmail, 2) == 3 &&
              float.IsPositiveInfinity(PenetrationJoint.MinVelocity(PenetrationProfile.SwordPierce, PenetrationMaterial.Chainmail, 0)) &&
              float.IsPositiveInfinity(PenetrationJoint.MinVelocity(PenetrationProfile.SaberSlash, PenetrationMaterial.Chainmail, 3)),
            "Minimum penetration speeds per material and tier; mail stops tier 0 points and every slash");
        check(Math.Abs(PenetrationJoint.AngleLimit(PenetrationProfile.SwordPierce, 0) - 30 * Math.PI / 180) < 1e-4 &&
              Math.Abs(PenetrationJoint.AngleLimit(PenetrationProfile.SwordPierce, .05f) - 15 * Math.PI / 180) < 1e-4 &&
              PenetrationJoint.AngleLimit(PenetrationProfile.SwordPierce, .2f) == 0 &&
              PenetrationJoint.AngleLimit(PenetrationProfile.Arrow, 0) == 0,
            "The blade may tilt 30 degrees at the surface, none past penetrationShortDepth (arrows locked)");
        check(PenetrationJoint.PressureThreshold(PenetrationProfile.SwordPierce, 0) == 5 &&
              Math.Abs(PenetrationJoint.PressureThreshold(PenetrationProfile.SwordPierce, .5f) - 1.5f) < 1e-4f &&
              Math.Abs(PenetrationJoint.PressureThreshold(PenetrationProfile.SwordPierce, 3) - .25f) < 1e-4f &&
              PenetrationJoint.PressureThreshold(PenetrationProfile.SwordPierce, .25f) < 5 &&
              PenetrationJoint.PressureThreshold(PenetrationProfile.SwordPierce, .25f) > 1.5f,
            "Pressure curve: 5 at rest, 1.5 at 0.5 m/s, 0.25 from 1 m/s (Hermite between keys)");
        check(PenetrationJoint.Pressure(0, 0, 1.5f) == 0 &&
              Math.Abs(PenetrationJoint.Pressure(.2f, .1f, 1.5f) - (.2f + RagdollPinDrive.HandSpring * .1f / 1.5f / 90)) < 1e-4f &&
              PenetrationJoint.Pressure(0, 2, 1.5f) <= RagdollPinDrive.HandMaxForce / 1.5f / 90 + 1e-4f,
            "Pressure is approach speed plus the arm spring on the overshoot, capped at the arm's force");
        PenetrationModifiers.Row mail = PenetrationModifiers.Get(PenetrationProfile.SwordPierce, PenetrationMaterial.Chainmail, 2);
        check(!mail.Pressure && Math.Abs(mail.DepthLimit(3.5f) - .1f) < 1e-4f && Math.Abs(mail.DepthLimit(9) - .2f) < 1e-4f &&
              float.IsPositiveInfinity(PenetrationModifiers.Get(PenetrationProfile.SwordPierce, PenetrationMaterial.Flesh, 2).DepthLimit(5)),
            "Mail: no pressure entry, depth limited by impact speed (0.2 m per m/s over the minimum, held at 0.2 m)");

        Vec3 axis = new Vec3(0, 1, 0);
        Vec3 pull = PenetrationJoint.AxialForce(axis, Vec3.Zero, new Vec3(.5f, -1, 0), 500, 20);
        check(Math.Abs(pull.y + 500) < .01f && Math.Abs(pull.x) < 1e-4f,
            "Withdrawing the blade drags the part along the blade axis only");
        Vec3 capped = PenetrationJoint.AxialForce(axis, Vec3.Zero, new Vec3(0, 1, 0), 8000, 2);
        check(Math.Abs(capped.y - 2 * RagdollPinDrive.MaxDampingRate) < .01f, "A light part's damper is capped for stability");
        check(PenetrationJoint.AxialForce(axis, Vec3.Zero, new Vec3(0, 1, 0), 0, 20).LengthSquared == 0 &&
              PenetrationJoint.AxialForce(Vec3.Zero, Vec3.Zero, Vec3.Up, 500, 20).LengthSquared == 0 &&
              PenetrationJoint.AxialForce(axis, new Vec3(float.NaN, 0, 0), Vec3.Up, 500, 20).LengthSquared == 0,
            "No axial force without damper, axis or valid velocity");

        // Explicit 90 Hz model: a 20 kg torso pierced by a held sword follows
        // a withdrawing blade without overshooting it.
        float v = 0, blade = -1;
        bool bounded = true;
        for (int i = 0; i < 90; i++)
        {
            Vec3 f = PenetrationJoint.AxialForce(axis, new Vec3(0, v, 0), new Vec3(0, blade, 0),
                PenetrationJoint.Damper(PenetrationProfile.SwordPierce, PenetrationMaterial.Flesh, true, false, 2), 20);
            v += f.y / 20 * (1f / 90);
            bounded &= v <= 0 && v >= blade - 1e-4f;
        }
        check(bounded && v < -.9f, "The pierced body converges to the blade speed without overshoot");
    }
}
