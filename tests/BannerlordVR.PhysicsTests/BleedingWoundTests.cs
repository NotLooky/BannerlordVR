using System;
using BannerlordVR.VR.Physics;

internal static class BleedingWoundTests
{
    public static void Run(Action<bool, string> check)
    {
        var wound = new BleedingWound();
        check(wound.Add(0, 1, 4), "A finite blood-loss injury can be recorded");
        int damage = 0;
        for (int i = 1; i <= 40; i++) damage += wound.TakeDose(i * .1f);
        check(damage == 4 && !wound.Active, "Fractional damage integrates to four points, independent of render frequency");
        check(wound.TakeDose(4.1f) == 0, "Expired blood loss cannot keep damaging");
        wound.Add(10, 12, 8);
        check(wound.TakeDose(10.49f) == 0 && wound.TakeDose(10.5f) == 6 && wound.TakeDose(10.5f) == 0,
            "A scheduled dose cannot be repeated in one frame");
        check(wound.TakeDose(14) == 6, "A long frame cannot cause a catch-up burst");
        check(wound.TakeDose(30) == 0 && !wound.Active, "A loading-sized time jump expires old blood loss without a stale hit");
        wound.Add(40, 1, 8);
        check(wound.TakeDose(39) == 0 && !wound.Active, "A backwards clock clears the previous mission's wound");
        check(!wound.Add(float.NaN, 2, 3) && !wound.Add(0, float.PositiveInfinity, 3) && !wound.Add(0, 1, -1),
            "Invalid injury values cannot enter the scheduler");
        wound.Add(50, 2, 4); wound.Deepen(.1f);
        check(Math.Abs(wound.Rate - 3) < .0001f, "Deeper insertion can increase an existing wound's flow");
        wound.Deepen(.05f); wound.Deepen(.1f);
        check(Math.Abs(wound.Rate - 3) < .0001f, "Withdrawal or tremor cannot repeatedly add penetration severity");
        wound.Deepen(100);
        check(Math.Abs(wound.Rate - 4) < .0001f, "Depth contribution stops at the bounded anatomic proxy");
        wound.Clear(); wound.Deepen(.2f);
        check(!wound.Active, "Penetration cannot create bleeding without an injury that passed armor and mortality");
        wound.Add(60, 1000, 1000);
        damage = 0;
        for (int i = 1; i <= 16; i++) damage += wound.TakeDose(60 + i * .5f);
        check(damage == 96 && !wound.Active, "Even excessive input cannot exceed the rate and duration caps");
        wound.Add(100, 1, 8);
        for (int i = 1; i < 24; i++) { wound.Add(100 + i * .5f, 1, 8); wound.TakeDose(100 + i * .5f); }
        wound.TakeDose(112);
        check(!wound.Active, "Repeated injuries do not extend one wound beyond its maximum lifetime");
    }
}
