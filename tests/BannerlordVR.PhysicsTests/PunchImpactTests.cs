using System;
using BannerlordVR.VR.Physics;
using TaleWorlds.Library;

internal static class PunchImpactTests
{
    public static void Run(Action<bool, string> check)
    {
        PunchImpact.Row flesh = PunchImpact.For(PenetrationMaterial.Flesh), leather = PunchImpact.For(PenetrationMaterial.Leather),
            mail = PunchImpact.For(PenetrationMaterial.Chainmail), plate = PunchImpact.For(PenetrationMaterial.Plate);
        check(PunchImpact.Curve(2) == 1 && PunchImpact.Curve(20) == 5 && PunchImpact.Curve(40) == 5 && Math.Abs(PunchImpact.Curve(11) - 3) < 1e-4f,
            "Damager_Punch curve: 1 at 2 m/s, 5 from 20 m/s, flat tangents (3 half way)");
        check(PunchImpact.Damage(1.9f, flesh, 100) == 0 && PunchImpact.Damage(4, mail, 100) == 0 && PunchImpact.Damage(4, leather, 100) > 0,
            "No punch damage under the armour's minVelocity (flesh 2, leather 3, mail 4.5 m/s)");
        check(Math.Abs(PunchImpact.Damage(11, flesh, 100) - 3) < 1e-3f && Math.Abs(PunchImpact.Damage(11, flesh, 50) - 1.5f) < 1e-3f,
            "Punch damage: curve x 0.5 on flesh, scaled from B&S's 50 health");
        check(plate.DamageMultiplier == leather.DamageMultiplier && plate.MinVelocity == leather.MinVelocity,
            "Bannerlord plate punches as B&S leather");
        check(PunchImpact.PushLevel(5, flesh) == 1 && PunchImpact.PushLevel(7, flesh) == 2 && PunchImpact.PushLevel(7, leather) == 0 &&
              PunchImpact.PushLevel(10, mail) == 1, "Push speeds per armour (flesh 2/4/6, leather and mail 3/9/12)");
        check(PunchImpact.Intensity(2) == 0 && PunchImpact.Intensity(9) == 1 && Math.Abs(PunchImpact.Intensity(5.5f) - .5f) < 1e-4f &&
              PunchImpact.Intensity(float.NaN) == 0, "Punch intensity from 2 to 9 m/s");
        Vec3 v = new Vec3(6, 0, 0);
        check(Math.Abs(PunchImpact.ContactImpulse(4, v).x - 24) < 1e-3f, "A light part is brought to the fist's speed");
        check(Math.Abs(PunchImpact.ContactImpulse(20, v).Length - PunchImpact.MaxImpulse) < 1e-3f,
            "A heavy part gets what the arm joint (3000 N) can push over the contact");
        check(PunchImpact.ContactImpulse(0, v).Length == 0 && PunchImpact.ContactImpulse(4, new Vec3(float.NaN, 0, 0)).Length == 0,
            "Invalid mass or velocity gives no impulse");
        // AddForceCoroutine: 0.15 s of v * 100 * 2 per second on the struck part.
        float dv = 0, dt = 1f / 90;
        for (float t = 0; t < PunchImpact.AddForceDuration - 1e-4f; t += dt) dv += PunchImpact.AddForceAcceleration(v, true, dt).x * dt;
        check(Math.Abs(dv / v.x - .333f) < .02f, "AddForce brings the struck part about a third of the hit speed over 0.15 s");
        check(PunchImpact.AddForceAcceleration(v, false, dt).x * 4 == PunchImpact.AddForceAcceleration(v, true, dt).x,
            "Other parts of the region get a quarter of the struck part's push (x0.5 vs x2)");
        check(RagdollGrabRules.ForcesLift(GrabHandle.Neck, PushContext.Idle) && RagdollGrabRules.ForcesLift(GrabHandle.Torso, PushContext.StaggerFull) &&
              !RagdollGrabRules.ForcesLift(GrabHandle.Torso, PushContext.Combat) && !RagdollGrabRules.ForcesLift(GrabHandle.Torso, PushContext.Attack),
            "Neck and torso grabs drop an idle or fully staggered creature, not a fighting one");
        check(RagdollGrabRules.ForcesLift(GrabHandle.Legs, PushContext.Attack) && RagdollGrabRules.ForcesLift(GrabHandle.Legs, PushContext.Stagger) &&
              !RagdollGrabRules.ForcesLift(GrabHandle.Legs, PushContext.Combat), "Legs also drop an attacking or staggered creature");
        check(!RagdollGrabRules.ForcesLift(GrabHandle.Head, PushContext.Idle) && !RagdollGrabRules.ForcesLift(GrabHandle.Wrist, PushContext.Idle) &&
              !RagdollGrabRules.ForcesLift(GrabHandle.Hand, PushContext.StaggerFull) && !RagdollGrabRules.ForcesLift(GrabHandle.Shoulder, PushContext.Idle),
            "Head, wrist, hand and shoulder grabs never force a fall");
    }
}
