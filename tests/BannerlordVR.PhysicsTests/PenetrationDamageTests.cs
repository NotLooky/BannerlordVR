using System;
using BannerlordVR.VR.Physics;
using TaleWorlds.Library;

internal static class PenetrationDamageTests
{
    public static void Run(Action<bool, string> check)
    {
        check(Math.Abs(PenetrationDamage.DeepDamage(PenetrationProfile.SwordPierce, BodyZone.Neck, 50) - 160) < .01f,
            "A sword deep in the neck deals 5 x 32 (B&S human scale)");
        check(Math.Abs(PenetrationDamage.DeepDamage(PenetrationProfile.SwordPierce, BodyZone.Neck, 100) - 320) < .01f,
            "Damage scales from B&S 50 health to the Bannerlord victim's limit");
        check(Math.Abs(PenetrationDamage.DeepDamage(PenetrationProfile.SwordPierce, BodyZone.Torso, 50) - 10) < .01f &&
              Math.Abs(PenetrationDamage.DeepDamage(PenetrationProfile.SwordPierce, BodyZone.Arm, 50) - 2.5f) < .01f,
            "Torso x2 and limbs x0.5");
        check(PenetrationDamage.DeepTrigger(PenetrationProfile.SwordPierce, BodyZone.Neck) == .02f &&
              PenetrationDamage.DeepTrigger(PenetrationProfile.SwordPierce, BodyZone.Torso) == .1f &&
              PenetrationDamage.DeepTrigger(PenetrationProfile.AxeSlash1H, BodyZone.Torso) == 0 &&
              PenetrationDamage.DeepTrigger(PenetrationProfile.Arrow, BodyZone.Torso) == 0,
            "Deep depth per part; slashes and arrows count on entry");
        check(Math.Abs(PenetrationDamage.DeepDamage(PenetrationProfile.AxeSlash1H, BodyZone.Neck, 50) - 800) < .01f &&
              Math.Abs(PenetrationDamage.DeepDamage(PenetrationProfile.AxeSlash1H, BodyZone.Head, 50) - 50) < .01f,
            "Slashes use penetrationSlashDamageMultiplier (neck 32, head 2)");
        check(Math.Abs(PenetrationDamage.SkewerTotal(PenetrationProfile.SwordPierce, BodyZone.Torso, 50) - 45) < .01f &&
              PenetrationDamage.SkewerTotal(PenetrationProfile.AxeSlash1H, BodyZone.Torso, 50) == 0,
            "Skewering the torso adds 15 x 3; only pierce damagers skewer");
        check(PenetrationDamage.HealthScale(float.NaN) == 2 && PenetrationDamage.HealthScale(0) == 2, "Invalid health limit falls back to 100");

        check(RagdollGrabRules.Steps(GrabHandle.Neck) && !RagdollGrabRules.Steps(GrabHandle.Hand) && !RagdollGrabRules.Steps(GrabHandle.Legs),
            "Head, neck, shoulder, torso and wrist grabs make the creature step");
        Vec2 step = RagdollGrabRules.Step(GrabHandle.Torso, new Vec2(.5f, 0), 1f / 90);
        check(step.x > 0 && step.x < .5f && Math.Abs(step.y) < 1e-6f, "A pulled torso walks the root towards the head");
        check(RagdollGrabRules.Step(GrabHandle.Wrist, new Vec2(.03f, 0), 1f / 90).LengthSquared == 0 &&
              RagdollGrabRules.Step(GrabHandle.Wrist, new Vec2(.3f, 0), 1f / 90).LengthSquared > 0 &&
              RagdollGrabRules.Step(GrabHandle.Torso, new Vec2(.1f, 0), 1f / 90).LengthSquared == 0,
            "A wrist grab only steps beyond 4 cm; sways under the 12 cm dead zone keep the feet planted");
        Vec2 far = RagdollGrabRules.Step(GrabHandle.Neck, new Vec2(20, 0), .1f);
        check(far.Length <= RagdollGrabRules.MaxStepSpeed * .1f + 1e-4f, "Stepping is capped at walking speed");
        check(RagdollGrabRules.Step(GrabHandle.Neck, new Vec2(float.NaN, 0), .01f).LengthSquared == 0 &&
              RagdollGrabRules.Step(GrabHandle.Neck, new Vec2(1, 0), 0).LengthSquared == 0, "Invalid step input is ignored");
    }
}
