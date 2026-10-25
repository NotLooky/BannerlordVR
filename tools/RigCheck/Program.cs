using System;
using System.Collections.Generic;
using System.Drawing;
using Color = System.Drawing.Color;
using System.Globalization;
using System.IO;
using BannerlordVR.VR;
using TaleWorlds.Library;

// Scratch check: the finger rig on a dumped hand mesh (hand rest axes), wrapped on a
// handle line through the palm channel, drawn in two views:
//   A - looking down the handle (the wrap and any gap to the palm);
//   B - looking at the palm side.
// usage: RigCheck <csv> <out.png> [axis x y z] [radius] [channel x y z]
internal static class Program
{
    private static readonly Color[] Colors =
        { Color.Orange, Color.Red, Color.LimeGreen, Color.DodgerBlue, Color.Magenta, Color.Gray };

    private static int Main(string[] args)
    {
        var pts = new List<Vec3>();
        foreach (string line in File.ReadAllLines(args[0]))
        {
            string[] f = line.Split(',');
            if (f.Length == 3 && float.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x))
                pts.Add(new Vec3(x, F(f[1]), F(f[2]), -1f));
        }

        bool pose = args.Length > 2 && args[2] == "pose";
        Vec3 axis = args.Length >= 5 && !pose ? new Vec3(F(args[2]), F(args[3]), F(args[4]), -1f) : new Vec3(0.46f, -0.2f, 0.87f, -1f);
        float radius = args.Length >= 6 && !pose ? F(args[5]) : 0.016f;
        Vec3 channel = args.Length >= 9 && !pose ? new Vec3(F(args[6]), F(args[7]), F(args[8]), -1f) : new Vec3(0.084f, 0.015f, -0.008f, -1f);

        VrFingerRig.Fit fit = VrFingerRig.FitHand(pts, 0.09f, 0.012f, new[] { 0f, 2f, 3f, 5f }, channel);
        Console.WriteLine("valid " + fit.Valid + (fit.Mitten ? " MITTEN" : "") + " " + fit.Why + ", knuckle " + fit.KnuckleX.ToString("F3")
                          + ", palm top " + fit.PalmTop.ToString("F3") + ", palm sign " + fit.PalmSign + ", thumb side " + fit.ThumbSide);
        if (args.Length >= 6 && args[2] == "pose")
            return Pose(args[1], pts, fit, F(args[3]), F(args[4]), F(args[5]));
        VrFingerRig.Handle h = VrFingerRig.LineHandle(channel, axis, channel, radius, 0.07f);

        {
            var palmPts = new List<Vec3>();
            foreach (Vec3 q in pts)
            {
                if (VrFingerRig.Weigh(fit, q, out _) < 0)
                    palmPts.Add(q);
            }

            Vec3 seat = VrFingerRig.Seat(palmPts, h, new Vec3(0f, fit.PalmSign, 0f, -1f), fit.Joint[2, 0], fit.Dir[2], 0.04f);
            Console.WriteLine("seat " + (seat.Length * 1000f).ToString("F0") + " mm along " + V(seat.NormalizedCopy()));
            if (Environment.GetEnvironmentVariable("NOSEAT") == null)
                h.Center = h.Center + seat;
        }

        float[] cascade = { 1f, 1.2f, 1.4f, 1.6f };
        var relax = new Vec3(12f, 22f, 10f, -1f);
        var fist = new Vec3(85f, 100f, 70f, -1f);
        var wrap = new Vec3[5];
        var digitOf = new int[pts.Count];
        var wOf = new Vec3[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            digitOf[i] = VrFingerRig.Weigh(fit, pts[i], out wOf[i]);

        string[] names = { "thumb", "index", "middle", "ring", "pinky" };
        var wrapClock = System.Diagnostics.Stopwatch.StartNew();
        foreach (int d in new[] { 1, 2, 3, 4, 0 })   // TEST 504: the thumb last, stopping on the wrapped fingers
        {
            long digitStart = wrapClock.ElapsedTicks;
            var dp = new List<Vec3>();
            var dw = new List<Vec3>();
            for (int i = 0; i < pts.Count; i++)
            {
                if (digitOf[i] == d && wOf[i].x + wOf[i].y + wOf[i].z >= 0.3f)
                {
                    dp.Add(pts[i]);
                    dw.Add(wOf[i]);
                }
            }

            Vec3 r = d == 0 ? new Vec3(12f, 12f, 10f, -1f) : relax * cascade[d - 1];
            Vec3 f2 = d == 0 ? new Vec3(35f, 45f, 60f, -1f) : fist;
            Vec3 turn = d == 0 ? VrFingerRig.Lift(fit, d, r, f2, dp, dw, h, SkinM) : fit.Normal * VrFingerRig.Spread(fit, d, 0.7f, EnvF("ADDUCT_FIST", 0.75f), 1f);
            Turns[d] = turn;
            if (d == 0)
                Console.WriteLine("thumb lift " + (turn.Length * 57.3f).ToString("F0") + " deg");
            VrFingerRig.Handle hd = h;
            if (d == 0 && Environment.GetEnvironmentVariable("NOBLOCK") == null)
            {
                var fingers = new List<Vec3>();
                for (int i = 0; i < pts.Count; i++)
                {
                    if (digitOf[i] > 0)
                        fingers.Add(VrFingerRig.Bend(fit, digitOf[i], wrap[digitOf[i]], Turns[digitOf[i]], pts[i], wOf[i]));
                }
                hd = h.With(new VrFingerRig.Obstacle(fingers));
            }
            wrap[d] = VrFingerRig.Wrap(fit, d, r, f2, dp, dw, hd, SkinM, turn);
            Vec3 a = wrap[d] * 57.3f;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "wrap {0,-6} {1:F0}/{2:F0}/{3:F0} deg ({4:F1} ms)", names[d], a.x, a.y, a.z,
                                            (wrapClock.ElapsedTicks - digitStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
        }

        // Thumb-side points the thumb did not take (they stay up while it bends).
        float indexZ = fit.ThumbSide > 0 ? fit.Center[3] : fit.Center[0];
        int stray = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            Vec3 v = pts[i];
            if (digitOf[i] != 0 && (v.z - indexZ) * fit.ThumbSide > 0.008f && v.x > fit.KnuckleX - 0.08f && v.x < fit.KnuckleX + 0.06f
                && v.y * fit.PalmSign > fit.PalmTop - 0.01f)
            {
                stray++;
                Console.WriteLine("  thumb-side point not thumb: " + V(v) + " -> digit " + digitOf[i]);
            }
        }

        Console.WriteLine("thumb-side strays: " + stray);

        var bent = new Vec3[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            int d = digitOf[i];
            bent[i] = d < 0 ? pts[i] : VrFingerRig.Bend(fit, d, wrap[d], Turns[d], pts[i], wOf[i]);
        }

        // Gap: the palm points' distance from the handle surface, nearest few within its length.
        var gaps = new List<float>();
        for (int i = 0; i < pts.Count; i++)
        {
            if (digitOf[i] >= 0)
                continue;
            Vec3 dv = pts[i] - h.Center;
            float t = Vec3.DotProduct(dv, h.Axis);
            if (Math.Abs(t) > 0.04f)
                continue;
            gaps.Add((dv - h.Axis * t).Length - radius);
        }

        gaps.Sort();
        Console.WriteLine("palm to handle surface, nearest: " + string.Join(" ", gaps.GetRange(0, Math.Min(8, gaps.Count)).ConvertAll(g => (g * 1000f).ToString("F0"))) + " mm");

        // Fingers' distance from the handle surface once wrapped (closest per digit).
        for (int d = 0; d < 5; d++)
        {
            float best = float.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                if (digitOf[i] != d)
                    continue;
                Vec3 dv = bent[i] - h.Center;
                float t = Vec3.DotProduct(dv, h.Axis);
                if (Math.Abs(t) > 0.07f)
                    continue;
                best = Math.Min(best, (dv - h.Axis * t).Length - radius);
            }

            Console.WriteLine(names[d] + " closest to the handle surface " + (best * 1000f).ToString("F0") + " mm");
        }

        // Gaps between neighbouring wrapped fingers, surface to surface (mm), at 35/65/90 % along
        // them: their bent centre lines apart, less each finger's radius.
        var fr = new float[5];
        for (int d = 1; d < 5; d++)
        {
            var rs = new List<float>();
            for (int i = 0; i < pts.Count; i++)
            {
                if (digitOf[i] != d || wOf[i].x + wOf[i].y + wOf[i].z < 0.9f)
                    continue;
                Vec3 q = pts[i] - fit.Joint[d, 0];
                rs.Add((q - fit.Dir[d] * Vec3.DotProduct(q, fit.Dir[d])).Length);
            }

            rs.Sort();
            fr[d] = rs.Count > 0 ? rs[rs.Count / 2] : 0.008f;
            var across = new List<float>();
            for (int i = 0; i < pts.Count; i++)
            {
                if (digitOf[i] != d || wOf[i].x + wOf[i].y + wOf[i].z < 0.9f)
                    continue;
                across.Add(Math.Abs(Vec3.DotProduct(pts[i] - fit.Joint[d, 0], fit.Axis[d])));
            }
            across.Sort();
            Console.WriteLine("  " + names[d] + ": median radius " + (fr[d] * 1000f).ToString("F1") + " mm, half-width across (p90) "
                              + (across.Count > 0 ? (across[(int)(across.Count * 0.9f)] * 1000f).ToString("F1") : "-") + " mm, max "
                              + (across.Count > 0 ? (across[across.Count - 1] * 1000f).ToString("F1") : "-") + " mm");
            if (across.Count > 0)
                fr[d] = across[(int)(across.Count * 0.9f)];   // TEST 502: the half-width across, as the mod uses
        }

        // TEST 504: each finger's width at each section, as the mod measures it.
        var widths = new float[5][];
        for (int d = 1; d < 5; d++)
        {
            var dp = new List<Vec3>();
            var dw = new List<Vec3>();
            for (int i = 0; i < pts.Count; i++)
            {
                if (digitOf[i] == d && wOf[i].x + wOf[i].y + wOf[i].z >= 0.3f)
                {
                    dp.Add(pts[i]);
                    dw.Add(wOf[i]);
                }
            }
            widths[d] = VrFingerRig.Widths(fit, d, dp, dw);
            Console.WriteLine("  " + names[d] + " half-width at " + string.Join("/", Array.ConvertAll(VrFingerRig.Along, a => (a * 100f).ToString("F0")))
                              + " %: " + string.Join("/", Array.ConvertAll(widths[d], w => (w * 1000f).ToString("F1"))) + " mm");
        }

        // TEST 497: the fingers slid along the handle, side by side (SIDE=0: not).
        Vec3[] slide = Environment.GetEnvironmentVariable("SIDE") == "0"
            ? new Vec3[5]
            : VrFingerRig.SideBySide(fit, wrap, Turns, widths, h.Axis, EnvF("GAP", 0.001f), 0.012f);
        Console.WriteLine("slides (mm, index/ring/pinky): " + (slide[1].Length * 1000f).ToString("F1") + "/"
                          + (slide[3].Length * 1000f).ToString("F1") + "/" + (slide[4].Length * 1000f).ToString("F1"));
        for (int i = 0; i < pts.Count; i++)
        {
            if (digitOf[i] > 0)
                bent[i] = bent[i] + slide[digitOf[i]] * (wOf[i].x + wOf[i].y + wOf[i].z);
        }

        var after = new List<string>();
        for (int d = 1; d < 5; d++)
        {
            float best = float.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                if (digitOf[i] != d)
                    continue;
                Vec3 dv = bent[i] - h.Center;
                float t = Vec3.DotProduct(dv, h.Axis);
                if (Math.Abs(t) <= 0.07f)
                    best = Math.Min(best, (dv - h.Axis * t).Length - radius);
            }

            after.Add((best * 1000f).ToString("F0"));
        }

        Console.WriteLine("closest to the handle after the slides (mm, index..pinky): " + string.Join(" ", after));

        // TEST 499/505: the arrow's rest on this fist, as the mod works it out.
        {
            Vec3 rest = VrFingerRig.ArrowRest(fit, h, wrap, Turns, slide, bent, 0.004f);
            Vec3 up = h.Axis * (h.Axis.z * fit.ThumbSide >= 0f ? 1f : -1f);
            Vec3 n = new Vec3(0f, fit.PalmSign, 0f, -1f);
            Vec3 dorsal = -(n - up * Vec3.DotProduct(n, up));
            dorsal = dorsal.NormalizedCopy();
            Vec3 q = rest - h.Center;
            float nearest = float.MaxValue;
            foreach (Vec3 b in bent)
                nearest = Math.Min(nearest, (b - rest).Length);
            Console.WriteLine("arrow rest: " + (Vec3.DotProduct(q, up) * 1000f).ToString("F0") + " mm up the stave (toward the index), "
                              + (Vec3.DotProduct(q, dorsal) * 1000f).ToString("F0") + " mm to the back of the hand's side, "
                              + (Vec3.DotProduct(q, Vec3.CrossProduct(up, dorsal).NormalizedCopy()) * 1000f).ToString("F0")
                              + " mm along the arrow; nearest hand point " + (nearest * 1000f).ToString("F1") + " mm");
        }

        for (int d = 1; d < 4; d++)
        {
            var g = new List<string>();
            Vec3 n = (fit.Axis[d] + fit.Axis[d + 1]).NormalizedCopy();
            for (int k = 0; k < VrFingerRig.Along.Length; k++)
            {
                float at = VrFingerRig.Along[k];
                Vec3 dv = Centre(fit, d, wrap[d], at) + slide[d] - Centre(fit, d + 1, wrap[d + 1], at) - slide[d + 1];
                float side = Math.Abs(Vec3.DotProduct(dv, n));
                float stagger = (dv - n * Vec3.DotProduct(dv, n)).Length;
                g.Add(((side - widths[d][k] - widths[d + 1][k]) * 1000f).ToString("F0") + "(st " + (stagger * 1000f).ToString("F0") + ")");
            }
            Console.WriteLine("gap " + names[d] + "-" + names[d + 1] + " (mm at 35/50/65/80 % along, at each finger's width there): "
                              + string.Join(" ", g));
        }

        Raw = pts;
        Draw(args[1], pts, bent, digitOf, fit, h, radius);
        Render(args[1].Replace(".png", "_p.png"), pts, bent, digitOf, new bool[pts.Count], fit, wrap, Turns, slide, h);
        return 0;
    }

    private static void Draw(string file, List<Vec3> pts, Vec3[] bent, int[] digitOf, VrFingerRig.Fit fit, VrFingerRig.Handle h, float radius)
    {
        const int W = 1400, H = 700;
        using (var bmp = new Bitmap(W, H))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // View A: down the handle. Basis: u = palm normal projected off the axis, v = axis x u.
            Vec3 ax = h.Axis;
            Vec3 n = new Vec3(0f, fit.PalmSign, 0f, -1f);
            Vec3 u = (n - ax * Vec3.DotProduct(n, ax)).NormalizedCopy();
            Vec3 v = Vec3.CrossProduct(ax, u).NormalizedCopy();
            Panel(g, 0, "A: down the handle (u = palm side up)", bent, digitOf, p => new PointF(Vec3.DotProduct(p - h.Center, v), Vec3.DotProduct(p - h.Center, u)), radius, true);

            // View B: from the palm side: x along the fingers (right), z across (up).
            Panel(g, W / 2, "B: side (x right, palm side up)", bent, digitOf, p => new PointF(p.x - 0.1f, p.y * fit.PalmSign), radius, false);
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }

        // View C: the middle finger's slice (|z - its column| < 1 cm), x right, palm side up;
        // rest light, wrapped dark; the handle where it crosses that slice.
        using (var bmp = new Bitmap(900, 700))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            const float scale = 5000f;
            float cx = 100f, cy = 400f;
            float zm = fit.Center[fit.ThumbSide > 0 ? 2 : 1];
            for (int i = 0; i < pts.Count; i++)
            {
                if (Math.Abs(pts[i].z - zm) > 0.01f || pts[i].x < 0.0f)
                    continue;
                Color rest = digitOf[i] >= 0 ? Color.LightGreen : Color.LightGray;
                Color now = digitOf[i] >= 0 ? Color.DarkGreen : Color.DimGray;
                g.FillEllipse(new SolidBrush(rest), cx + pts[i].x * scale - 2, cy - pts[i].y * fit.PalmSign * scale - 2, 4, 4);
                g.FillEllipse(new SolidBrush(now), cx + bent[i].x * scale - 2, cy - bent[i].y * fit.PalmSign * scale - 2, 4, 4);
            }

            // The handle line at z = zm, and its cross-section ellipse in x-y.
            float t = (zm - h.Center.z) / (Math.Abs(h.Axis.z) > 1e-3f ? h.Axis.z : 1e-3f);
            Vec3 c = h.Center + h.Axis * t;
            float sx = radius / (float)Math.Sqrt(Math.Max(1e-4f, 1f - h.Axis.x * h.Axis.x));
            g.DrawEllipse(new Pen(Color.Black, 2), cx + (c.x - sx) * scale, cy - (c.y * fit.PalmSign + radius) * scale, 2 * sx * scale, 2 * radius * scale);
            g.DrawString("C: middle finger slice, x right (0 = wrist), palm side up; light = rest, dark = wrapped; knuckle line x="
                         + fit.KnuckleX.ToString("F3") + ", MCP " + fit.Joint[2, 0].x.ToString("F3") + "," + fit.Joint[2, 0].y.ToString("F3"),
                         SystemFonts.DefaultFont, Brushes.Black, 10, 10);
            g.FillEllipse(Brushes.Red, cx + fit.Joint[2, 0].x * scale - 4, cy - fit.Joint[2, 0].y * fit.PalmSign * scale - 4, 8, 8);
            bmp.Save(file.Replace(".png", "_c.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private static void Panel(Graphics g, int left, string title, Vec3[] pts, int[] digitOf, Func<Vec3, PointF> project, float radius, bool circle)
    {
        const float scale = 4000f; // px per metre
        float cx = left + 350, cy = 350;
        g.DrawString(title, SystemFonts.DefaultFont, Brushes.Black, left + 10, 10);
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < pts.Length; i++)
            {
                bool digit = digitOf[i] >= 0;
                if (digit != (pass == 1) || Raw[i].x < 0.03f)
                    continue;
                PointF q = project(pts[i]);
                var brush = new SolidBrush(Colors[digit ? digitOf[i] : 5]);
                g.FillEllipse(brush, cx + q.X * scale - 2, cy - q.Y * scale - 2, 4, 4);
                brush.Dispose();
            }
        }

        if (circle)
            g.DrawEllipse(new Pen(Color.Black, 2), cx - radius * scale, cy - radius * scale, 2 * radius * scale, 2 * radius * scale);
    }

    // TEST 492/493: the grid's own pose at curls (thumb, index, rest), drawn from three sides.
    // Light = rest, dark = posed; red = palm points within 12 mm of the thumb's tube and nearer
    // it than the index (what would stay where the thumb was). Env RELAX, CASCADE, THUMB_RELAX,
    // FIST, THUMB_FIST, ADDUCT_REST, ADDUCT_FIST override the defaults.
    private static int Pose(string file, List<Vec3> pts, VrFingerRig.Fit fit, float ct, float ci, float cr)
    {
        Vec3 relax = Env3("RELAX", 12f, 22f, 10f), fist = Env3("FIST", 85f, 100f, 70f);
        Vec3 thumbRelax = Env3("THUMB_RELAX", 12f, 12f, 10f), thumbFist = Env3("THUMB_FIST", 35f, 45f, 60f);
        float[] cascade = EnvN("CASCADE", 1f, 1.2f, 1.4f, 1.6f);
        float adRest = EnvF("ADDUCT_REST", 0.7f), adFist = EnvF("ADDUCT_FIST", 0.75f);

        var angles = new Vec3[5];
        var turn = new Vec3[5];
        for (int d = 0; d < 5; d++)
        {
            float c = d == 0 ? ct : d == 1 ? ci : cr;
            angles[d] = VrFingerRig.Angles(d == 0 ? thumbRelax : relax * cascade[d - 1], d == 0 ? thumbFist : fist, c);
            turn[d] = d == 0 ? Vec3.Zero : fit.Normal * VrFingerRig.Spread(fit, d, adRest, adFist, c);
        }

        var digit = new int[pts.Count];
        var bent = new Vec3[pts.Count];
        var stray = new bool[pts.Count];
        var count = new int[6];
        Vec3 dir = fit.Dir[0];
        var wts = new Vec3[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            digit[i] = VrFingerRig.Weigh(fit, pts[i], out wts[i]);
        VrFingerRig.Glue(fit, pts, digit, wts);   // TEST 509: as the mod does
        for (int i = 0; i < pts.Count; i++)
        {
            Vec3 w = wts[i];
            bent[i] = digit[i] < 0 ? pts[i] : VrFingerRig.Bend(fit, digit[i], angles[digit[i]], turn[digit[i]], pts[i], w);
            count[digit[i] + 1]++;
            if (digit[i] >= 0 || !fit.HasThumb)
                continue;

            float s = Vec3.DotProduct(pts[i] - fit.ThumbAt, dir);
            float r = (pts[i] - fit.ThumbAt - dir * s).Length;
            Vec3 j = fit.Joint[1, 0];
            float t = Math.Max(-0.03f, Math.Min(fit.Length[1], Vec3.DotProduct(pts[i] - j, fit.Dir[1])));
            stray[i] = s >= fit.ThumbLo && s <= fit.ThumbHi && r <= fit.ThumbRadius + 0.012f
                       && r < (pts[i] - j - fit.Dir[1] * t).Length;
        }

        // Each finger point's column digit against the finger whose axis it lies nearest
        // (perpendicular distance to the line through J0 along Dir).
        {
            var mis = new int[5, 5];
            for (int i = 0; i < pts.Count; i++)
            {
                if (digit[i] < 1)
                    continue;
                int nearest = 1;
                float bestD = float.MaxValue;
                for (int g = 1; g < 5; g++)
                {
                    Vec3 q = pts[i] - fit.Joint[g, 0];
                    float d = (q - fit.Dir[g] * Vec3.DotProduct(q, fit.Dir[g])).Length;
                    if (d < bestD) { bestD = d; nearest = g; }
                }
                mis[digit[i], nearest]++;
            }
            var lines = new List<string>();
            for (int f = 1; f < 5; f++)
                for (int g = 1; g < 5; g++)
                    if (f != g && mis[f, g] > 0)
                        lines.Add(mis[f, g] + " of digit " + f + " lie nearest digit " + g + "'s axis");
            Console.WriteLine("column vs nearest axis: " + (lines.Count == 0 ? "all agree" : string.Join("; ", lines)));
        }

        // How deep the bent digits go toward the back of the hand (palm side up): their
        // lowest point against the back's surface (the palm/back points' lowest), 5-15 cm out.
        {
            float digitLow = float.MaxValue, backLow = float.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                if (pts[i].x < 0.05f || pts[i].x > 0.15f)
                    continue;
                if (digit[i] >= 0)
                    digitLow = Math.Min(digitLow, bent[i].y * fit.PalmSign);
                else
                    backLow = Math.Min(backLow, pts[i].y * fit.PalmSign);
            }
            Console.WriteLine("digits' lowest " + (digitLow * 1000f).ToString("F0") + " mm, back of the hand " + (backLow * 1000f).ToString("F0")
                              + " mm (palm side up): " + ((digitLow - backLow) * 1000f).ToString("F0") + " mm inside it");
        }

        string dump = Environment.GetEnvironmentVariable("DUMP");
        if (dump != null)
        {
            var text = new System.Text.StringBuilder("x,y,z,bx,by,bz,digit,w").AppendLine();
            for (int i = 0; i < pts.Count; i++)
            {
                VrFingerRig.Weigh(fit, pts[i], out Vec3 wd);
                text.Append(V(pts[i])).Append(',').Append(V(bent[i])).Append(',').Append(digit[i]).Append(',')
                    .Append((wd.x + wd.y + wd.z).ToString("F3", CultureInfo.InvariantCulture)).AppendLine();
            }
            File.WriteAllText(dump, text.ToString());
        }

        int strays = 0;
        foreach (bool b in stray)
            strays += b ? 1 : 0;
        Console.WriteLine("pose thumb/index/rest " + ct + "/" + ci + "/" + cr + ": palm " + count[0] + ", thumb " + count[1] + ", index "
                          + count[2] + ", middle " + count[3] + ", ring " + count[4] + ", pinky " + count[5]
                          + "; palm points in the thumb's band (red) " + strays);

        Render(file, pts, bent, digit, stray, fit, angles, turn, null, null);

        return 0;
    }

    // Three sides of a posed hand: points (light = rest, dark = posed), each digit's bones
    // (thin = rest, thick = posed), and the handle (grey band) when there is one.
    private static void Render(string file, List<Vec3> pts, Vec3[] bent, int[] digit, bool[] stray, VrFingerRig.Fit fit,
                               Vec3[] angles, Vec3[] turn, Vec3[] slide, VrFingerRig.Handle h)
    {
        float ts = fit.ThumbSide, ps = fit.PalmSign;
        using (var bmp = new Bitmap(1500, 620))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var chains = new List<Vec3[]>();
            for (int d = 0; d < 5; d++)
            {
                if (d == 0 && !fit.HasThumb)
                    continue;
                Vec3 tip = fit.Joint[d, 0] + fit.Dir[d] * (fit.Length[d] + (d == 0 ? 0.015f : 0f));
                Vec3[] rest = { fit.Joint[d, 0], fit.Joint[d, 1], fit.Joint[d, 2], tip };
                Vec3[] ws = { Vec3.Zero, new Vec3(1f, 0f, 0f, -1f), new Vec3(0f, 1f, 0f, -1f), new Vec3(0f, 0f, 1f, -1f) };
                var posed = new Vec3[4];
                for (int k = 0; k < 4; k++)
                    posed[k] = VrFingerRig.Bend(fit, d, angles[d], turn[d], rest[k], ws[k]) + (slide != null && k > 0 ? slide[d] : Vec3.Zero);
                chains.Add(rest);
                chains.Add(posed);
            }

            Func<Vec3, Vec3>[] views =
            {
                p => new Vec3(p.x, p.z * ts, p.y * ps, -1f),
                p => new Vec3(p.x, p.y * ps, p.z * ts, -1f),
                p => new Vec3(p.z * ts + 0.1f, p.y * ps, p.x, -1f)
            };
            string[] titles = { "A: from the palm side (x right, thumb up)", "B: from the thumb side (x right, palm up)", "C: from the fingertips (thumb right, palm up)" };
            for (int v = 0; v < 3; v++)
            {
                if (h != null && h.Valid)
                {
                    Vec3 a = views[v](h.Center - h.Axis * 0.07f), b = views[v](h.Center + h.Axis * 0.07f);
                    using (var pen = new Pen(Color.FromArgb(60, Color.Black), 2f * h.Radius * 2200f))
                        g.DrawLine(pen, 500 * v + 30 + a.x * 2200f, 330 - a.y * 2200f, 500 * v + 30 + b.x * 2200f, 330 - b.y * 2200f);
                }
                PosePanel(g, 500 * v, titles[v], pts, bent, digit, stray, chains, views[v]);
            }
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private static readonly Color[] PoseColors = { Color.DarkOrange, Color.Goldenrod, Color.ForestGreen, Color.RoyalBlue, Color.DarkViolet, Color.DimGray };

    // project: x, y on screen (metres), z = depth (nearer the viewer drawn last).
    private static void PosePanel(Graphics g, int left, string title, List<Vec3> rest, Vec3[] bent, int[] digit, bool[] stray,
                                  List<Vec3[]> chains, Func<Vec3, Vec3> project)
    {
        const float scale = 2200f;
        float cx = left + 30, cy = 330;
        g.DrawString(title, SystemFonts.DefaultFont, Brushes.Black, left + 10, 10);
        g.DrawLine(Pens.LightGray, left + 495, 0, left + 495, 620);

        var order = new List<KeyValuePair<float, int>>();
        for (int i = 0; i < rest.Count; i++)
        {
            if (rest[i].x < 0.03f)
                continue;
            Vec3 q = project(rest[i]);
            Color c = PoseColors[digit[i] >= 0 ? digit[i] : 5];
            using (var b = new SolidBrush(Color.FromArgb(50, c)))
                g.FillEllipse(b, cx + q.x * scale - 3, cy - q.y * scale - 3, 6, 6);
            order.Add(new KeyValuePair<float, int>(project(bent[i]).z, i));
        }

        order.Sort((a, b) => a.Key.CompareTo(b.Key));
        foreach (KeyValuePair<float, int> o in order)
        {
            int i = o.Value;
            Vec3 q = project(bent[i]);
            Color c = stray[i] ? Color.Red : PoseColors[digit[i] >= 0 ? digit[i] : 5];
            using (var b = new SolidBrush(c))
                g.FillEllipse(b, cx + q.x * scale - 3, cy - q.y * scale - 3, 6, 6);
        }

        for (int k = 0; k < chains.Count; k++)
        {
            using (var pen = new Pen(k % 2 == 0 ? Color.FromArgb(120, Color.Black) : Color.Black, k % 2 == 0 ? 1.5f : 4f))
            {
                for (int j = 0; j < 3; j++)
                {
                    Vec3 a = project(chains[k][j]), b = project(chains[k][j + 1]);
                    g.DrawLine(pen, cx + a.x * scale, cy - a.y * scale, cx + b.x * scale, cy - b.y * scale);
                }
            }
        }
    }

    private static Vec3 Env3(string name, float x, float y, float z)
    {
        float[] v = EnvN(name, x, y, z);
        return new Vec3(v[0], v[1], v[2], -1f);
    }

    private static float EnvF(string name, float value) => EnvN(name, value)[0];

    private static float[] EnvN(string name, params float[] values)
    {
        string text = Environment.GetEnvironmentVariable(name);
        return text == null ? values : Array.ConvertAll(text.Split(','), F);
    }

    private static Vec3 Centre(VrFingerRig.Fit fit, int d, Vec3 angles, float at)
    {
        Vec3 p = fit.Joint[d, 0] + fit.Dir[d] * (fit.Length[d] * at);
        VrFingerRig.Weigh(fit, p, out Vec3 w);
        return VrFingerRig.Bend(fit, d, angles, Turns[d], p, w);
    }

    private static List<Vec3> Raw;
    private static readonly Vec3[] Turns = new Vec3[5];
    private static readonly float SkinM = float.Parse(Environment.GetEnvironmentVariable("SKIN") ?? "0.002", CultureInfo.InvariantCulture);

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    private static string V(Vec3 v) => v.x.ToString("F3", CultureInfo.InvariantCulture) + "," + v.y.ToString("F3", CultureInfo.InvariantCulture)
                                       + "," + v.z.ToString("F3", CultureInfo.InvariantCulture);
}
