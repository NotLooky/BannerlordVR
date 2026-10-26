using System;
using BannerlordVR.VR.Physics;
using TaleWorlds.Library;

internal static class ActiveBodyBalanceTests
{
    private const float Upper = ActiveBodyBalance.SupportedMassFraction;
    public static void Run(Action<bool, string> check)
    {
        Vec3 left = new Vec3(-.15f, 0, .1f), right = new Vec3(.15f, 0, .1f);
        Vec3 pelvis = new Vec3(0, 0, 1), chest = new Vec3(0, 0, 1.4f);
        var motor = new ActiveBodyBalance();
        check(motor.Begin(pelvis, chest, left, right, 80), "Standing anatomy can capture a balance reference");
        for (int i = 0; i < 20; i++) motor.Tick(pelvis, chest, left, right, true, .01f);
        check(motor.Phase == BodyBalancePhase.Supporting &&
            Math.Abs(motor.PelvisForce.z + motor.ChestForce.z - 80 * Upper * 9.81f) < .01f,
            "A stationary supported pose receives total weight compensation");
        check(motor.PelvisForce.x == 0 && motor.PelvisForce.y == 0,
            "A balanced pose has no horizontal drift force");
        check(motor.SetPhysicalMass(60) && !motor.SetPhysicalMass(float.NaN) && !motor.SetPhysicalMass(1000),
            "Measured physical mass may replace equipment mass but invalid readings cannot enter the motor");
        motor.Tick(pelvis, chest, left, right, true, .01f);
        check(Math.Abs(motor.PelvisForce.z + motor.ChestForce.z - 60 * Upper * 9.81f) < .01f,
            "Gravity assistance uses the actual physical mass after calibration");
        motor.SetPhysicalMass(80);
        motor.Tick(pelvis + Vec3.Up * .1f, chest + Vec3.Up * .1f,
            left + Vec3.Up * .1f, right + Vec3.Up * .1f, true, .05f);
        check(motor.PelvisForce.z + motor.ChestForce.z < 80 * Upper * 9.81f,
            "Lifting both feet cannot raise the vertical support target and create an elevator feedback loop");
        motor.Begin(pelvis, chest, left, right, 80);
        for (int i = 0; i < 20; i++) motor.Tick(pelvis, chest, left, right, true, .01f);
        motor.Tick(pelvis + Vec3.Up * .1f, chest + Vec3.Up * .1f,
            left + Vec3.Up * .1f, right + Vec3.Up * .1f, true, true, .05f, .1f);
        // One further stationary sample removes the initial upward velocity term.
        motor.Tick(pelvis + Vec3.Up * .1f, chest + Vec3.Up * .1f,
            left + Vec3.Up * .1f, right + Vec3.Up * .1f, true, true, .05f, .1f);
        check(Math.Abs(motor.PelvisForce.z + motor.ChestForce.z - 80 * Upper * 9.81f) < .01f,
            "An actual rise in measured terrain may shift the support reference");
        motor.Begin(pelvis, chest, left, right, 80);
        for (int i = 0; i < 20; i++) motor.Tick(pelvis, chest, left, right, true, .01f);
        motor.Tick(pelvis, chest, left, right, false, .05f);
        check(motor.Phase == BodyBalancePhase.Supporting && motor.PelvisForce.x == 0,
            "A brief ground-query miss has grace without horizontal support");
        for (int i = 0; i < 6; i++) motor.Tick(pelvis, chest, left, right, false, .05f);
        check(motor.Phase == BodyBalancePhase.Inactive && motor.PelvisForce.LengthSquared == 0 &&
            motor.ChestForce.LengthSquared == 0, "Leaving the ground ends assistance instead of levitating a body");

        motor.Begin(pelvis, chest, left, right, 80);
        for (int i = 1; i <= 50; i++)
        {
            Vec3 displacement = new Vec3(0, i * .01f, 0);
            motor.Tick(pelvis + displacement, chest + displacement, left, right, true, .02f);
        }
        check(motor.Phase == BodyBalancePhase.Inactive, "Dragging the centre outside the foot support yields a fall");

        motor.Begin(pelvis, chest, left, right, 80);
        for (int i = 0; i < 20; i++) motor.Tick(pelvis, chest, left, right, true, false, .01f);
        check(motor.Phase == BodyBalancePhase.Supporting, "A centre above one planted foot may retain support while the other foot swings");
        motor.Begin(pelvis, chest, left, right, 80);
        for (int i = 1; i <= 40; i++)
        {
            Vec3 displacement = new Vec3(i * .01f, 0, 0);
            motor.Tick(pelvis + displacement, chest + displacement, left, right, true, false, .02f);
        }
        check(motor.Phase == BodyBalancePhase.Inactive,
            "An airborne foot cannot extend the support polygon to hold a centre outside the planted foot");

        motor.Begin(pelvis, chest, left, right, 80);
        motor.Tick(pelvis, chest, left, right, true, float.NaN);
        check(motor.Phase == BodyBalancePhase.Inactive, "A nonfinite timestep cannot emit native balance forces");
        motor.Begin(pelvis, chest, left, right, 80);
        motor.Tick(pelvis, chest, left, right, true, .2f);
        check(motor.Phase == BodyBalancePhase.Inactive, "A loading hitch cancels balance without a catch-up force");
        motor.Begin(pelvis, chest, left, right, 80);
        motor.Tick(pelvis, chest, left, right, true, .01f);
        motor.Tick(pelvis + Vec3.Up * 2, chest + Vec3.Up * 2, left, right, true, .01f);
        check(motor.Phase == BodyBalancePhase.Inactive, "A pose discontinuity cannot produce a violent correction");
        motor.Begin(pelvis, chest, left, right, 80);
        motor.Tick(pelvis + Vec3.Up * .03f, chest + Vec3.Up * .03f, left, right, true, .001f);
        check(motor.Phase == BodyBalancePhase.Supporting && motor.Failure == BodyBalanceFailure.None,
            "The first active sample does not mistake movement during asynchronous activation for one-tick velocity");
        check(!motor.Begin(pelvis, chest, left, right, float.PositiveInfinity) &&
              !motor.Begin(pelvis, chest, left, right, 1000) &&
              !motor.Begin(pelvis, chest, left, left, 80) &&
              !motor.Begin(left, chest, left, right, 80), "Invalid mass, degenerate feet and a fallen initial pose are refused");

        foreach (int rate in new[] { 30, 60, 90, 144 })
        {
            motor.Begin(pelvis, chest, left, right, 80);
            Vec3 p = pelvis, c = chest, pv = Vec3.Zero, cv = Vec3.Zero;
            float dt = 1f / rate;
            bool finite = true;
            for (int i = 0; i < rate * 3; i++)
            {
                motor.Tick(p, c, left, right, true, dt);
                Vec3 pf = motor.PelvisForce, cf = motor.ChestForce;
                finite &= InteractionMath.IsFinite(pf) && InteractionMath.IsFinite(cf) &&
                    pf.Length <= 1080.01f && cf.Length <= 720.01f && pf.z >= 0 && cf.z >= 0;
                // Upper-body masses only: the ground carries the legs.
                pv += (pf * (1f / (48 * Upper)) - Vec3.Up * 9.81f) * dt;
                cv += (cf * (1f / (32 * Upper)) - Vec3.Up * 9.81f) * dt;
                p += pv * dt; c += cv * dt;
            }
            check(finite && motor.Phase == BodyBalancePhase.Supporting && Math.Abs(p.z - pelvis.z) < .03f &&
                Math.Abs(c.z - chest.z) < .03f, "Bounded weight support settles in the independent two-mass integration at " + rate + " Hz");
        }
        // Regression for the in-game float: with the ground carrying the legs,
        // the support settles near the captured height instead of 13.7 cm up.
        motor.Begin(pelvis, chest, left, right, 89.7f);
        {
            Vec3 p = pelvis, c = chest, pv = Vec3.Zero, cv = Vec3.Zero;
            float rise = 0;
            for (int i = 0; i < 90 * 3; i++)
            {
                motor.Tick(p, c, left, right, true, 1f / 90);
                pv += (motor.PelvisForce * (1f / (89.7f * Upper * .6f)) - Vec3.Up * 9.81f) * (1f / 90);
                cv += (motor.ChestForce * (1f / (89.7f * Upper * .4f)) - Vec3.Up * 9.81f) * (1f / 90);
                p += pv * (1f / 90); c += cv * (1f / 90);
                rise = Math.Max(rise, p.z - pelvis.z);
            }
            check(rise < .03f, "Upper-body compensation does not float a ragdoll whose legs rest on the ground");
        }
    }
}
