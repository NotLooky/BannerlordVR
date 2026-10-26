using System;
using BannerlordVR.VR.Physics;

internal static class RagdollImpactTests
{
    public static void Run(Action<bool, string> check)
    {
        check(RagdollImpact.Damage(0) == 0 && RagdollImpact.Damage(12) == 5 && RagdollImpact.Damage(40) == 5,
            "Damager_Ragdoll curve: 0 at rest, 5 from 12 m/s (clamped)");
        float last = 0; bool monotonic = true;
        for (float v = .5f; v <= 12; v += .5f) { float d = RagdollImpact.Damage(v); monotonic &= d >= last - 1e-4f; last = d; }
        check(monotonic && RagdollImpact.Damage(6) > 1 && RagdollImpact.Damage(6) < 4, "Curve rises monotonically between its keys");
        check(RagdollImpact.Damage(float.NaN) == 0 && RagdollImpact.Damage(-3) == 0, "Invalid impact speed deals nothing");
        check(RagdollImpact.Detect(7, .5f, 6.6f, false) == 6.6f, "A part stopped from 7 m/s collided");
        check(RagdollImpact.Detect(7, .5f, 4.5f, false) == 0 && RagdollImpact.Detect(7, .5f, 4.5f, true) == 4.5f,
            "The head registers impacts from 3 m/s, the body from 5 m/s (DamageModifier_Ragdoll)");
        check(RagdollImpact.Detect(8, 6, 6, false) == 0, "A swing that keeps its speed is not a collision");
        check(RagdollImpact.Detect(float.NaN, 0, 9, false) == 0, "Invalid samples are ignored");
    }
}
