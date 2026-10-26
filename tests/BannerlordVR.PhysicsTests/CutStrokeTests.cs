using System;
using BannerlordVR.VR.Physics;
using TaleWorlds.Library;

internal static class CutStrokeTests
{
    public static void Run(Action<bool, string> check)
    {
        var stroke = new CutStroke();
        Vec3 axis = new Vec3(1, 0, 0);
        check(!stroke.Sample(1, Vec3.Zero, axis, 0, 1), "A contact alone is not a cut");
        bool fired = false;
        for (int i = 1; i <= 15; i++) fired |= stroke.Sample(1, axis * (i * .01f), axis, .01f, 1 + i * .01f);
        check(fired, "A slow coherent tracked draw cuts once");
        check(!stroke.Sample(1, axis * .2f, axis, .05f, 1.16f), "Continuous contact cannot cause a second cut");
        stroke.Reset();
        fired = false;
        for (int i = 0; i < 100; i++) fired |= stroke.Sample(1, axis * ((i % 2) * .01f), axis, .01f, 2 + i * .01f);
        check(!fired, "Oscillating tracking jitter cannot accumulate cut length");
        stroke.Reset();
        fired = false;
        for (int i = 0; i < 100; i++) fired |= stroke.Sample(1, axis * ((i % 2) * .03f), axis,
            i % 2 == 0 ? .001f : .03f, 3 + i * .01f);
        check(!fired, "Asymmetric physical travel cannot turn stationary relative oscillation into a cut");
        stroke.Reset();
        for (int i = 0; i < 25; i++) check(!stroke.Sample(1, axis * (i * .02f), axis, 0, 4 + i * .01f), "Locomotion or enemy motion with stationary controllers cannot cut");
        stroke.Reset();
        for (int i = 0; i < 10; i++) check(!stroke.Sample(i, axis * (i * .03f), axis, .03f, 5 + i * .01f), "Contact changes cannot join strokes across enemies");
        stroke.Reset();
        stroke.Sample(1, Vec3.Zero, axis, 0, 6);
        stroke.Sample(1, axis * .08f, axis, .08f, 6.05f);
        check(!stroke.Sample(1, axis * .16f, axis, .08f, 6.2f), "A contact gap discards the earlier draw");
        check(!stroke.Sample(1, axis * .3f, Vec3.Up, .14f, 6.22f), "A large axis change starts a new contact");
        check(!stroke.Sample(1, axis, axis, float.NaN, 7), "Non-finite tracking is rejected");
        check(!stroke.Sample(1, axis * 2, axis, 1, 7.01f), "A tracking jump is rejected");
        stroke.Reset();
        stroke.Sample(1, Vec3.Zero, axis, 0, 8);
        fired = false;
        for (int i = 1; i <= 15; i++) fired |= stroke.Sample(1, -axis * (i * .01f), axis, .01f, 8 + i * .01f);
        check(fired, "Either longitudinal draw direction is accepted");
    }
}
