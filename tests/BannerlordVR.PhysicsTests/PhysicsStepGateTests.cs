using System;
using BannerlordVR.VR.Physics;

internal static class PhysicsStepGateTests
{
    public static void Run(Action<bool, string> check)
    {
        var gate = new PhysicsStepGate();
        check(!gate.CanApply(-1), "Invalid step counter refuses forces");
        check(gate.CanApply(0), "First application is allowed once the clock runs");
        gate.Applied(5);
        check(!gate.CanApply(5), "A force still pending for the next PhysX step is not stacked");
        check(gate.CanApply(6), "A new step consumed the previous force");
        gate.Reset();
        check(gate.CanApply(5), "Reset forgets the previous application");

        // Measured cadence: ~197 mission ticks/s against 90 PhysX steps/s, with
        // a step thread independent of the mission thread. Forces accumulate
        // until the next step consumes them.
        foreach (double missionHz in new[] { 60.0, 90.0, 144.0, 197.0, 400.0 })
        {
            gate.Reset();
            long steps = 0; double nextStep = 1 / 90.0, pending = 0, worst = 0;
            int applied = 0;
            for (int tick = 1; tick <= (int)(missionHz * 6); tick++)
            {
                double now = tick / missionHz;
                while (nextStep <= now) { worst = Math.Max(worst, pending); pending = 0; steps++; nextStep += 1 / 90.0; }
                if (gate.CanApply(steps)) { pending += 1; gate.Applied(steps); applied++; }
            }
            check(worst <= 1, "No PhysX step receives a stacked continuous force at " + missionHz + " Hz");
            double coverage = applied / (6 * Math.Min(missionHz, 90.0));
            check(coverage > .9 && coverage <= 1.01, "Forces cover nearly every reachable PhysX step at " + missionHz + " Hz");
        }

        // A step racing between the force call and the counter read must
        // cost one skipped step, not a doubled force.
        gate.Reset();
        long racing = 10;
        check(gate.CanApply(racing), "Racing case starts open");
        racing++; // step consumed the force before the counter was read back
        gate.Applied(racing);
        check(!gate.CanApply(racing), "Race resolves toward under-application");
    }
}
