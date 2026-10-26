using System;
using BannerlordVR.VR.Physics;
using TaleWorlds.Library;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new Exception(message);
    }
    private static bool Near(float a, float b) => Math.Abs(a - b) < .0001f;
    public static int Main()
    {
        try
        {
            LivingLeaseTests.Run(Check);
            CutStrokeTests.Run(Check);
            BleedingWoundTests.Run(Check);
            ActiveBodyBalanceTests.Run(Check);
            PhysicsStepGateTests.Run(Check);
            ActiveRagdollTests.Run(Check);
            PenetrationJointTests.Run(Check);
            RagdollImpactTests.Run(Check);
            PenetrationDamageTests.Run(Check);
            DismembermentTests.Run(Check);
            PunchImpactTests.Run(Check);
            PoseMathTests.Run(Check);
            float t;
            Check(InteractionMath.SegmentBox(new Vec3(-2, 0, 0), new Vec3(2, 0, 0), new Vec3(-.1f, -1, -1), new Vec3(.1f, 1, 1), 0, out t) && Near(t, .475f), "Thin shield crossing reports first surface");
            Check(!InteractionMath.SegmentBox(new Vec3(-2, 2, 0), new Vec3(2, 2, 0), new Vec3(-.1f, -1, -1), new Vec3(.1f, 1, 1), 0, out t), "Shield can be bypassed above its bounds");
            Check(InteractionMath.SegmentBox(Vec3.Zero, Vec3.Zero, new Vec3(-1, -1, -1), Vec3.Up, 0, out t) && t == 0, "Start inside equipment volume");
            Check(InteractionMath.SegmentBox(new Vec3(-2, 1.05f, 0), new Vec3(2, 1.05f, 0), new Vec3(-.1f, -1, -1), new Vec3(.1f, 1, 1), .1f, out t), "Hand radius expands shield contact");
            Check(!InteractionMath.SegmentBox(Vec3.Zero, Vec3.Up, Vec3.Up, Vec3.Zero, 0, out t), "Invalid equipment bounds rejected");
            Check(!InteractionMath.SegmentBox(Vec3.Zero, Vec3.Up, Vec3.Zero, Vec3.Up, float.NaN, out t), "Invalid thickness rejected");
            Check(InteractionMath.SegmentSphere(new Vec3(-4, 0, 0), new Vec3(4, 0, 0), Vec3.Zero, .1f, out t) && Near(t, .4875f), "Fast hand must not tunnel through small target");
            Check(!InteractionMath.SegmentSphere(new Vec3(-4, .2f, 0), new Vec3(4, .2f, 0), Vec3.Zero, .1f, out t), "Parallel near miss");
            Check(InteractionMath.SegmentSphere(Vec3.Zero, Vec3.Zero, Vec3.Zero, .1f, out t) && t == 0, "Stationary overlap");
            Check(!InteractionMath.SegmentSphere(Vec3.Up, Vec3.Up, Vec3.Zero, .1f, out t), "Stationary outside");
            Check(InteractionMath.SegmentSphere(new Vec3(-1, .1f, 0), new Vec3(1, .1f, 0), Vec3.Zero, .1f, out t) && Near(t, .5f), "Tangent contact");
            Check(!InteractionMath.SegmentSphere(Vec3.Zero, Vec3.Up, Vec3.Zero, -1, out t), "Invalid radius");
            Check(!InteractionMath.SegmentSphere(new Vec3(float.NaN, 0, 0), Vec3.Zero, Vec3.Zero, 1, out t), "Invalid tracked position");
            Check(InteractionMath.ClampMagnitude(new Vec3(float.PositiveInfinity, 0, 0), 2).LengthSquared == 0, "Infinite force rejected");
            Check(Near(InteractionMath.ClampMagnitude(new Vec3(float.MaxValue, float.MaxValue, 0), 2).Length, 2), "Overflow-safe velocity limit");
            Vec3 damped = InteractionMath.SpringForce(new Vec3(1, 0, 0), new Vec3(3, 0, 0), 100, 20, 500);
            Check(Near(damped.x, 40), "Damping opposes body velocity relative to target");
            Check(Near(InteractionMath.SpringForce(new Vec3(1, 0, 0), new Vec3(-3, 0, 0), 100, 20, 500).x, 160), "Approaching target velocity sign");
            Check(Near(InteractionMath.SpringForce(new Vec3(100, 0, 0), Vec3.Zero, 500, 20, 350).Length, 350), "Grab force bounded");
            Check(InteractionMath.SpringForce(Vec3.Zero, Vec3.Zero, 100, 20, 350).LengthSquared == 0, "No drift at equilibrium");
            Check(InteractionMath.SpringForce(Vec3.Up, Vec3.Zero, float.NaN, 20, 350).LengthSquared == 0, "Invalid configuration rejected");
            Vec3 closest = InteractionMath.ClosestPointOnSegment(Vec3.Zero, Vec3.Up, new Vec3(0, 0, 5));
            Check(Near(closest.z, 1), "Closest point clamped to endpoint");
            Vec3 rotated = InteractionMath.RotateTowards(new Vec3(1, 0, 0), Vec3.Up, new Vec3(1, 0, 0), .35f);
            Check(Near(rotated.Length, 1) && Near(rotated.z, (float)Math.Sin(.35)), "Joint angular bound preserves bone length");
            // Moving-target relative sweep: both objects end apart but crossed this frame.
            Check(InteractionMath.SegmentSphere(new Vec3(-2, 0, 0), new Vec3(2, 0, 0), Vec3.Zero, .2f, out t) && Near(t, .45f), "Relative sweep catches moving target");
            var random = new Random(12946);
            var budget = new AdaptivePhysicsBudget(6, 1.5);
            Check(budget.BodyLimit == 6 && budget.Percentile(.99) == 0, "Budget starts at requested capacity without fabricated timing");
            for (int i = 0; i < 8; i++) budget.Record(2);
            Check(budget.BodyLimit == 5, "Sustained CPU overload reduces body count");
            for (int i = 0; i < 80; i++) budget.Record(4);
            Check(budget.BodyLimit == 1, "Overload cannot reduce capacity below one");
            int limit = budget.BodyLimit;
            for (int i = 0; i < 30; i++) { budget.Record(double.NaN); budget.Record(-1); }
            Check(budget.BodyLimit == limit && budget.Percentile(.99) == 4, "Invalid measurements cannot manufacture recovery or pollute percentiles");
            for (int i = 0; i < 179; i++) budget.Record(.1);
            Check(budget.BodyLimit == 1, "Capacity recovery requires sustained headroom");
            for (int i = 0; i < 1000; i++) budget.Record(.1);
            Check(budget.BodyLimit == 6 && Math.Abs(budget.Percentile(.95) - .1) < .00001, "Capacity recovers with hysteresis and bounded timing ring");
            var floored = new AdaptivePhysicsBudget(6, 1.5, 4);
            for (int i = 0; i < 200; i++) floored.Record(4);
            Check(floored.BodyLimit == 4, "Overload never sheds below the tracked minimum");
            var ordered = new AdaptivePhysicsBudget(2, 5);
            for (int i = 1; i <= 100; i++) ordered.Record(i * .01);
            Check(Math.Abs(ordered.Percentile(.95) - .95) < .00001 && Math.Abs(ordered.Percentile(.99) - .99) < .00001, "Reported p95/p99 reflect recorded samples");
            Check(InteractionMath.GestureSpeed(Vec3.Zero, Vec3.Zero, Vec3.Up, Vec3.Up, 1, 1, .01f) == 0, "Stationary stage input cannot cause locomotion damage");
            Check(InteractionMath.GestureSpeed(Vec3.Zero, Vec3.Zero, Vec3.Up, new Vec3(0, 1, 0), 1, 1, .1f) > 10, "Wrist-only blade rotation remains an attack");
            Check(InteractionMath.GestureSpeed(Vec3.Zero, Vec3.Up, Vec3.Up, Vec3.Up, 0, 1, 0) == 0, "Paused gesture rejected");
            for (int i = 0; i < 2000; i++)
            {
                float r = .02f + (float)random.NextDouble();
                float before = r + .01f + (float)random.NextDouble() * 10;
                float after = r + .01f + (float)random.NextDouble() * 10;
                Check(InteractionMath.SegmentSphere(new Vec3(-before, 0, 0), new Vec3(after, 0, 0), Vec3.Zero, r, out t)
                    && Math.Abs((-before + (before + after) * t) + r) < .001f, "Earliest surface contact property");
                Vec3 force = InteractionMath.SpringForce(new Vec3(before, after, r), new Vec3(-r, before, after), 500, 80, 350);
                Check(InteractionMath.IsFinite(force) && force.Length <= 350.001f, "Finite bounded spring property");
            }
            Console.WriteLine($"PASS: {_checks} physics checks against the installed TaleWorlds.Library.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
