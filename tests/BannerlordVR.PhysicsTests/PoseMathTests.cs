using System;
using BannerlordVR.VR.Physics;
using TaleWorlds.Library;

internal static class PoseMathTests
{
    private static Mat3 Yaw(float a) =>
        new Mat3(new Vec3((float)Math.Cos(a), (float)Math.Sin(a), 0), new Vec3(-(float)Math.Sin(a), (float)Math.Cos(a), 0), new Vec3(0, 0, 1));
    private static bool Near(Mat3 a, Mat3 b) =>
        (a.s - b.s).Length < 1e-3f && (a.f - b.f).Length < 1e-3f && (a.u - b.u).Length < 1e-3f;

    public static void Run(Action<bool, string> check)
    {
        bool round = true;
        foreach (float a in new[] { 0f, .7f, 2.5f, 3.1f, -1.2f })
            round &= Near(PoseMath.ToMat(PoseMath.ToQuat(Yaw(a))), Yaw(a));
        Mat3 tilt = new Mat3(new Vec3(1, 0, 0), new Vec3(0, 0, 1), new Vec3(0, -1, 0));
        round &= Near(PoseMath.ToMat(PoseMath.ToQuat(tilt)), tilt);
        check(round, "Quaternions round-trip TaleWorlds rotations");
        var a0 = new MatrixFrame(Yaw(0), new Vec3(0, 0, 0));
        var b0 = new MatrixFrame(Yaw(1.6f), new Vec3(2, 0, 0));
        check(Near(PoseMath.Blend(a0, b0, 0).rotation, a0.rotation) && Near(PoseMath.Blend(a0, b0, 1).rotation, b0.rotation),
            "Pose blend keeps its ends");
        MatrixFrame half = PoseMath.Blend(a0, b0, .5f);
        check(Near(half.rotation, Yaw(.8f)) && (half.origin - new Vec3(1, 0, 0)).Length < 1e-4f,
            "Half way turns half the angle and moves half the way");
    }
}
