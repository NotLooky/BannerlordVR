using System;
using BannerlordVR.VR.Physics;
using TaleWorlds.Library;

internal static class DismembermentTests
{
    public static void Run(Action<bool, string> check)
    {
        Vec3 neck = Vec3.Up, blade = new Vec3(1, 0, 0), swing = new Vec3(0, 1, 0);
        check(Dismemberment.Slices(PenetrationProfile.SaberSlash, false, false, 13, neck, blade, swing, .05f),
            "A level cut at 13 m/s through the neck slices with a one-handed sabre (12 m/s)");
        check(!Dismemberment.Slices(PenetrationProfile.SaberSlash, false, false, 11.5f, neck, blade, swing, .05f) &&
              Dismemberment.Slices(PenetrationProfile.SaberSlash, true, false, 11.5f, neck, blade, swing, .05f),
            "Two-handed sabres slice from 11 m/s");
        check(Dismemberment.Slices(PenetrationProfile.AxeSlash1H, false, false, 9.5f, neck, blade, swing, .05f),
            "Axes slice from 9 m/s");
        check(!Dismemberment.Slices(PenetrationProfile.SwordPierce, false, false, 30, neck, blade, swing, .05f) &&
              !Dismemberment.Slices(PenetrationProfile.SaberSlash, false, true, 30, neck, blade, swing, .05f),
            "Thrusts and piercing damagers never slice");
        check(Dismemberment.CheckSlice(neck, new Vec3(1, 0, 1), swing, .05f),
            "A blade tilted 45 degrees still slices (dismembermentMaxVerticalAngle 90)");
        check(!Dismemberment.CheckSlice(neck, blade, new Vec3(0, 1, .9f), .05f) &&
              Dismemberment.CheckSlice(neck, blade, new Vec3(0, 1, .3f), .05f),
            "A cut more than 35 degrees off perpendicular to the limb fails (dismembermentMaxHorizontalAngle)");
        check(!Dismemberment.CheckSlice(neck, blade, new Vec3(0, 1, 1), .05f), "An edge moving along the limb does not cut through");
        check(!Dismemberment.CheckSlice(neck, blade, swing, .2f), "Contact outside sliceWidth does not slice");
        check(!Dismemberment.CheckSlice(Vec3.Zero, blade, swing, 0) && !Dismemberment.CheckSlice(neck, blade, swing, float.NaN),
            "Degenerate slice input refused");
        check(Dismemberment.KnocksOut(PushMaterial.Blunt, BodyZone.Head, 5) &&
              Dismemberment.KnocksOut(PushMaterial.Punch, BodyZone.Head, 6) &&
              Dismemberment.KnocksOut(PushMaterial.Kick, BodyZone.Head, 5) &&
              !Dismemberment.KnocksOut(PushMaterial.Punch, BodyZone.Head, 4.9f) &&
              !Dismemberment.KnocksOut(PushMaterial.Slash, BodyZone.Head, 30) &&
              !Dismemberment.KnocksOut(PushMaterial.Blunt, BodyZone.Torso, 30) &&
              !Dismemberment.KnocksOut(PushMaterial.Punch, BodyZone.Head, float.NaN),
            "Knockout follows SkillDiscombobulate: a blunt blow or a punch to the head at 5 m/s, never a blade or a body blow");
    }
}
