using System;
using System.Collections.Generic;
using TaleWorlds.Library;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 481: a five-finger rig fitted to ONE hand's vertices, and the bend
    /// that moves them. Pure math - the math tests run it on a synthetic hand.
    ///
    /// Points are in the hand bone's rest axes, measured by TEST 480 on the
    /// game's gauntlets: x runs down the fingers (wrist 0, tips ~0.20 m), z
    /// across the knuckles, y through the palm; the four fingers are separate
    /// columns 0.115-0.205 m out, the thumb a lump on the palm side.
    ///
    ///  * knuckle line = the farthest tip minus finger_length_m;
    ///  * the four fingers = 4 clusters across z, 2-6 cm past the knuckle line;
    ///  * the thumb side = the side the hand reaches farthest past its outer
    ///    finger; the palm side = the side the thumb lump sits on;
    ///  * each finger = its points' main axis. TEST 489, a real hand's
    ///    proportions: the middle finger's MCP sits finger_mcp_back_m inside the
    ///    knuckle line; every other finger is as long as a real one is against
    ///    the middle finger (index 0.88, ring 0.95, pinky 0.76), so its MCP sits
    ///    back on the knuckle arc, the pinky's furthest; PIP and DIP at a real
    ///    finger's fractions (PipAt/DipAt) - the middle finger's segments the
    ///    longest, the index's and pinky's end segments the shortest. Its weight
    ///    ramps across the MCP, 6 mm behind to 18 mm past, so the knuckle rounds
    ///    over as it bends (481c started it past the web, when each finger had a
    ///    copy of its own; the four share one since 481d);
    ///  * the thumb = the lump's main axis; CMC 1.5 cm inside its base, MCP at
    ///    35 %, IP at 68 %; it folds toward the palm's centre. Its weight ramps
    ///    over its first 1.5 cm (489: it moved whole, and the palm around its
    ///    base tore away from it as it bent).
    /// Weights blend smoothly across every joint, so the palm never moves and
    /// nothing tears where a finger meets it.
    ///
    /// TEST 488, a real hand's close: each finger's SPLAY as modelled (its angle
    /// from the middle finger about the palm normal) is measured, and a bend can
    /// take it back (Spread) - the game's hands are modelled flat and fanned, a
    /// closing hand brings its fingers side by side. Each finger's flex axis can
    /// lean toward the thumb (convergeDeg), so curled fingertips aim at the
    /// thumb's base the way a fist's do.
    ///
    /// Digits: 0 thumb, 1 index, 2 middle, 3 ring, 4 pinky.
    /// </summary>
    public static class VrFingerRig
    {
        public const int Digits = 5;
        private const float ReachM = 0.035f;

        /// <summary>TEST 492: how far outside the thumb's fitted radius a point is
        /// still thumb (the radius is fitted from its palm-side lump only).</summary>
        private const float ThumbMarginM = 0.004f;

        /// <summary>TEST 492: and over how far past that its share fades out - the
        /// points there are mostly web skin (their neighbours are palm).</summary>
        private const float ThumbFadeM = 0.008f;

        // TEST 489: a real hand (thumb, index, middle, ring, pinky) - each finger's
        // length against the middle finger's, and where along it the PIP and DIP sit.
        private static readonly float[] LengthRatio = { 0f, 0.88f, 1f, 0.95f, 0.76f };
        private static readonly float[] PipAt = { 0.35f, 0.50f, 0.49f, 0.49f, 0.49f };
        private static readonly float[] DipAt = { 0.68f, 0.78f, 0.79f, 0.79f, 0.76f };

        public sealed class Fit
        {
            public bool Valid;
            public string Why = string.Empty;
            public bool HasThumb;
            public float TipX;
            public float KnuckleX;
            public float ThumbSide;
            public float PalmSign;
            public float PalmTop;
            public Vec3 PalmCenter;

            /// <summary>TEST 490: the thumb as a tube - a point on its axis, its
            /// radius, and how far along the axis it runs (from that point).</summary>
            public Vec3 ThumbAt;
            public float ThumbRadius;
            public float ThumbLo;
            public float ThumbHi;
            public readonly float[] Center = new float[4];
            public readonly float[] Bound = new float[3];
            public readonly Vec3[] Dir = new Vec3[Digits];
            public readonly Vec3[] Axis = new Vec3[Digits];

            /// <summary>TEST 488: the palm normal (toward the palm side), and each
            /// finger's splay as modelled - radians from the middle finger about it.</summary>
            public Vec3 Normal;
            public readonly float[] Splay = new float[Digits];
            public readonly Vec3[,] Joint = new Vec3[Digits, 3];
            public readonly float[] Length = new float[Digits];

            /// <summary>Where each digit's weight starts and is full, along its
            /// axis from its first joint; and how far either side of the next two
            /// joints a point blends between their bones (TEST 489).</summary>
            public readonly float[] Ramp0 = new float[Digits];
            public readonly float[] Ramp1 = new float[Digits];
            public readonly float[] Blend = new float[Digits];

            /// <summary>TEST 504: the four fingers are one block, modelled curled
            /// (FitMitten): every finger point rides the middle finger, whose bones
            /// follow the curl - its joints and Rest are as modelled, and MittenTip
            /// ends it; only points across the knuckles' width (MittenZLo..Hi) move.</summary>
            public bool Mitten;
            public Vec3 MittenTip;
            public float MittenZLo, MittenZHi;

            /// <summary>TEST 504: each digit's joint angles as MODELLED (radians);
            /// Bend turns a digit by the angles asked less these. Zero but on a
            /// mitten, whose block is modelled curled.</summary>
            public readonly Vec3[] Rest = new Vec3[Digits];
        }

        /// <summary>The rig of one hand, from its vertices (hand rest axes). TEST
        /// 504: with the grip (finger0's rest spot, the palm channel) given, a hand
        /// whose fingers are not separate is fitted as a mitten (FitMitten).</summary>
        public static Fit FitHand(IList<Vec3> p, float fingerLength, float mcpBack, float[] convergeDeg = null, Vec3? grip = null)
        {
            Fit fit = FitFingers(p, fingerLength, mcpBack, convergeDeg);
            if (fit.Valid || grip == null || p.Count < 100)
                return fit;

            Fit mitten = FitMitten(p, grip.Value, fit, fingerLength + mcpBack);
            if (mitten.Valid)
                return mitten;

            fit.Why += "; not a mitten either (" + mitten.Why + ")";
            return fit;
        }

        private static Fit FitFingers(IList<Vec3> p, float fingerLength, float mcpBack, float[] convergeDeg)
        {
            var fit = new Fit();
            if (p.Count < 100)
                return Fail(fit, p.Count + " points");

            float tip = float.MinValue;
            foreach (Vec3 v in p)
                tip = Math.Max(tip, v.x);

            fit.TipX = tip;
            fit.KnuckleX = tip - fingerLength;

            // The fingers across z, where they are four separate columns.
            var band = new List<float>();
            foreach (Vec3 v in p)
            {
                if (v.x >= fit.KnuckleX + 0.02f && v.x <= fit.KnuckleX + 0.06f)
                    band.Add(v.z);
            }

            if (band.Count < 40)
                return Fail(fit, band.Count + " points in the finger band");

            KMeans4(band, fit.Center);
            for (int i = 1; i < 4; i++)
            {
                if (fit.Center[i] - fit.Center[i - 1] < 0.008f)
                    return Fail(fit, "fingers closer than 8 mm across");
            }

            for (int i = 0; i < 3; i++)
                fit.Bound[i] = 0.5f * (fit.Center[i] + fit.Center[i + 1]);

            // The palm slab: where the hand is widest past its fingers is the thumb.
            var slabZ = new List<float>();
            foreach (Vec3 v in p)
            {
                if (v.x >= fit.KnuckleX - 0.06f && v.x <= fit.KnuckleX)
                    slabZ.Add(v.z);
            }

            if (slabZ.Count < 20)
                return Fail(fit, "no palm");

            slabZ.Sort();
            float zLo = slabZ[(int)(slabZ.Count * 0.02f)], zHi = slabZ[(int)(slabZ.Count * 0.98f)];
            fit.ThumbSide = zHi - fit.Center[3] > fit.Center[0] - zLo ? 1f : -1f;

            // Fingers numbered from the thumb: index is the outermost on its side.
            float indexZ = fit.ThumbSide > 0 ? fit.Center[3] : fit.Center[0];
            float midBound = fit.ThumbSide > 0 ? fit.Bound[2] : fit.Bound[0];

            // The palm side: the one the thumb lump sits on, against the pinky half.
            Vec3 lump = Vec3.Zero, pinkyHalf = Vec3.Zero;
            int nLump = 0, nPinky = 0;
            foreach (Vec3 v in p)
            {
                if (v.x < fit.KnuckleX - 0.07f || v.x > fit.KnuckleX + 0.01f)
                    continue;

                if ((v.z - indexZ) * fit.ThumbSide > 0.01f)
                {
                    lump += v;
                    nLump++;
                }
                else if ((v.z - midBound) * fit.ThumbSide < 0f && v.x <= fit.KnuckleX)
                {
                    pinkyHalf += v;
                    nPinky++;
                }
            }

            if (nPinky < 10)
                return Fail(fit, "no pinky half of the palm");

            pinkyHalf *= 1f / nPinky;
            fit.PalmSign = nLump >= 10 && lump.y / nLump < pinkyHalf.y ? -1f : 1f;
            fit.PalmCenter = new Vec3(fit.KnuckleX - 0.04f, pinkyHalf.y, 0.5f * (fit.Center[0] + fit.Center[3]));

            // The palm's top along the palm side (the pinky half has no thumb).
            var tops = new List<float>();
            foreach (Vec3 v in p)
            {
                if (v.x >= fit.KnuckleX - 0.06f && v.x <= fit.KnuckleX && (v.z - midBound) * fit.ThumbSide < 0f)
                    tops.Add(v.y * fit.PalmSign);
            }

            tops.Sort();
            fit.PalmTop = tops[(int)(tops.Count * 0.9f)];

            Vec3 palm = new Vec3(0f, fit.PalmSign, 0f);
            fit.Normal = palm;
            for (int f = 1; f < Digits; f++)
            {
                int column = fit.ThumbSide > 0 ? 4 - f : f - 1;
                var pts = new List<Vec3>();
                foreach (Vec3 v in p)
                {
                    if (v.x >= fit.KnuckleX && Column(fit, v.z) == column && !InLump(fit, v, indexZ))
                        pts.Add(v);
                }

                if (pts.Count < 10)
                    return Fail(fit, "finger " + f + " has " + pts.Count + " points");

                Line(pts, out Vec3 centre, out Vec3 dir);
                if (dir.x < 0f)
                    dir = -dir;

                // TEST 504: open fingers run along the hand (the gauntlets' fan is
                // +-12 deg); a column far off it is a curled block - FitMitten's.
                if (dir.x < 0.8f)
                    return Fail(fit, "finger " + f + " runs " + (Math.Acos(dir.x) * 180.0 / Math.PI).ToString("F0") + " deg off the hand");

                Vec3 j1 = centre + dir * ((fit.KnuckleX - mcpBack - centre.x) / Math.Max(0.3f, dir.x));
                float length = 0f;
                foreach (Vec3 v in pts)
                    length = Math.Max(length, Vec3.DotProduct(v - j1, dir));

                if (length < 0.04f || length > 0.16f)
                    return Fail(fit, "finger " + f + " is " + length.ToString("F3") + " m long");

                fit.Dir[f] = dir;
                fit.Axis[f] = Vec3.CrossProduct(dir, palm).NormalizedCopy();

                // TEST 488: the flex axis leant by convergeDeg toward the finger, on
                // the side that swings the curling tip toward the thumb (the tip
                // moves along the axis by sin*cos*(1-cos flex) of its length).
                float lean = convergeDeg != null && convergeDeg.Length >= 4 ? convergeDeg[f - 1] * (float)(Math.PI / 180.0) : 0f;
                if (lean != 0f)
                {
                    float side = fit.Axis[f].z * fit.ThumbSide >= 0f ? 1f : -1f;
                    fit.Axis[f] = (fit.Axis[f] * (float)Math.Cos(lean) + dir * (side * (float)Math.Sin(lean))).NormalizedCopy();
                }
                fit.Length[f] = length;
                fit.Joint[f, 0] = j1;
            }

            // TEST 489: a real hand's proportions against the middle finger. A
            // finger only ever grows back toward the wrist (by 2 cm at most): its
            // tip is where the mesh puts it.
            for (int f = 1; f < Digits; f++)
            {
                Vec3 dir = fit.Dir[f];
                Vec3 end = fit.Joint[f, 0] + dir * fit.Length[f];
                float length = Math.Max(fit.Length[f], Math.Min(LengthRatio[f] * fit.Length[2], fit.Length[f] + 0.02f));
                Vec3 j1 = end - dir * length;
                fit.Length[f] = length;
                fit.Joint[f, 0] = j1;
                fit.Joint[f, 1] = j1 + dir * (PipAt[f] * length);
                fit.Joint[f, 2] = j1 + dir * (DipAt[f] * length);
                fit.Ramp0[f] = -0.006f;
                fit.Ramp1[f] = 0.018f;
                fit.Blend[f] = Math.Max(0.004f, Math.Min(0.008f, 0.07f * length));
            }

            // TEST 488: each finger's splay from the middle finger, about the palm normal.
            for (int f = 1; f < Digits; f++)
            {
                Vec3 m = fit.Dir[2];
                fit.Splay[f] = (float)Math.Atan2(Vec3.DotProduct(Vec3.CrossProduct(m, fit.Dir[f]), palm),
                                                 Vec3.DotProduct(m, fit.Dir[f]));
            }

            // The thumb lump: past the index on the thumb side, above the palm.
            var thumb = new List<Vec3>();
            foreach (Vec3 v in p)
            {
                if (InLump(fit, v, indexZ) && v.y * fit.PalmSign > fit.PalmTop - 0.002f)
                    thumb.Add(v);
            }

            fit.HasThumb = thumb.Count >= 15;
            if (fit.HasThumb)
            {
                Line(thumb, out Vec3 centre, out Vec3 dir);
                if (dir.x < 0f)
                    dir = -dir;

                float lo = float.MaxValue, hi = float.MinValue;
                var radii = new List<float>(thumb.Count);
                foreach (Vec3 v in thumb)
                {
                    float s = Vec3.DotProduct(v - centre, dir);
                    lo = Math.Min(lo, s);
                    hi = Math.Max(hi, s);
                    radii.Add((v - centre - dir * s).Length);
                }

                // TEST 490: the tube the lump fills, then as far along it as the
                // thumb's own points go - past the knuckle line too.
                radii.Sort();
                fit.ThumbAt = centre;
                fit.ThumbRadius = Math.Max(0.006f, Math.Min(0.02f, radii[(int)(radii.Count * 0.95f)]));
                fit.ThumbLo = lo;
                fit.ThumbHi = hi;
                foreach (Vec3 v in p)
                {
                    float s = Vec3.DotProduct(v - centre, dir);
                    if (s > hi && s <= lo + 0.11f && (v.z - indexZ) * fit.ThumbSide > 0.008f
                        && (v - centre - dir * s).Length <= fit.ThumbRadius + 0.003f)
                        hi = s;
                }

                fit.ThumbHi = hi;
                Vec3 baseAt = centre + dir * lo;
                float length = hi - lo;
                // TEST 489: it folds ACROSS the palm at its own height (over a grip
                // or the curled fingers), not down into the palm - 487's thumb
                // dived into the sword's grip at once (6/11/14 deg).
                Vec3 toPalm = fit.PalmCenter - centre;
                toPalm.y = 0f;
                toPalm -= dir * Vec3.DotProduct(toPalm, dir);
                fit.Dir[0] = dir;
                fit.Axis[0] = Vec3.CrossProduct(dir, toPalm.NormalizedCopy()).NormalizedCopy();
                fit.Length[0] = length;
                fit.Joint[0, 0] = baseAt - dir * 0.015f;
                fit.Joint[0, 1] = baseAt + dir * (PipAt[0] * length);
                fit.Joint[0, 2] = baseAt + dir * (DipAt[0] * length);
                fit.Ramp0[0] = 0.005f;
                fit.Ramp1[0] = 0.015f + 0.3f * length;
                fit.Blend[0] = 0.004f;
            }

            fit.Valid = true;
            return fit;
        }

        /// <summary>TEST 504: a mitten's fingers' half-thickness - the bones lie this
        /// far inside the block's surface.</summary>
        private const float MittenR = 0.012f;

        /// <summary>
        /// TEST 504: a hand whose four fingers are ONE block, modelled curled round
        /// the grip - battania_warlord_bracers (373 vertices, no finger columns:
        /// FitFingers saw "finger 1 is 0.163 m long"). The user's call: the four bend
        /// together. In the side plane (x along the hand, y to the palm side):
        ///  * the knuckle corner - as far out as the back of the hand goes - with
        ///    the knuckles' width across (z) from the points there;
        ///  * a middle finger's three bones (a real one's proportions, `full` long
        ///    x0.85-1.15) laid from near that corner through the block, by search:
        ///    the knuckle (+-8 mm) and angles that put the block's points nearest
        ///    their surface (MittenR out) with no bone through empty space.
        /// Those angles are the modelled pose (Rest): a bend to any other turns the
        /// block by the difference - it opens for an empty hand and closes on a
        /// handle. Every finger digit gets the same bones; Weigh gives the block
        /// to the middle finger. The thumb, if any, stays as modelled.
        /// </summary>
        private static Fit FitMitten(IList<Vec3> p, Vec3 grip, Fit failed, float full)
        {
            var fit = new Fit();
            float ps = failed.PalmSign != 0f ? failed.PalmSign : 1f;
            float gu = grip.x, gw = grip.y * ps;

            var back = new List<float>();
            foreach (Vec3 v in p)
            {
                if (v.x >= gu - 0.03f && v.x <= gu + 0.02f)
                    back.Add(v.y * ps);
            }

            if (back.Count < 10)
                return Fail(fit, "no back of the hand at the grip");

            back.Sort();
            float wBack = back[(int)(back.Count * 0.1f)];
            float xk = float.MinValue;
            foreach (Vec3 v in p)
            {
                if (v.y * ps <= wBack + 0.012f && Math.Abs(v.x - gu) <= 0.08f)
                    xk = Math.Max(xk, v.x);
            }

            if (xk < gu + 0.01f)
                return Fail(fit, "no knuckles past the grip");

            var kz = new List<float>();
            foreach (Vec3 v in p)
            {
                if (v.y * ps <= wBack + 0.015f && v.x >= xk - 0.025f && v.x <= xk)
                    kz.Add(v.z);
            }

            if (kz.Count < 4)
                return Fail(fit, kz.Count + " points on the knuckles");

            kz.Sort();
            fit.MittenZLo = kz[(int)(kz.Count * 0.1f)];
            fit.MittenZHi = kz[(int)(kz.Count * 0.9f)];

            // The block: near the grip, across the knuckles, not the palm or the
            // back of the hand behind the knuckles.
            float u0 = xk - MittenR, w0 = wBack + MittenR;
            var qu = new List<float>();
            var qw = new List<float>();
            float tip = float.MinValue;
            foreach (Vec3 v in p)
            {
                float u = v.x, w = v.y * ps;
                tip = Math.Max(tip, u);
                if (v.z < fit.MittenZLo - 0.01f || v.z > fit.MittenZHi + 0.01f
                    || (u - gu) * (u - gu) + (w - gw) * (w - gw) > 0.08f * 0.08f || (u < u0 && w < gw))
                    continue;
                qu.Add(u);
                qw.Add(w);
            }

            if (qu.Count < 20)
                return Fail(fit, qu.Count + " points in the block");

            // How far each spot is from the block's nearest point (3 mm cells).
            const float cell = 0.003f, span = 0.1f;
            int n = (int)(2f * span / cell);
            var field = new float[n, n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    float cu = gu - span + (i + 0.5f) * cell, cw = gw - span + (j + 0.5f) * cell, best = float.MaxValue;
                    for (int k = 0; k < qu.Count; k++)
                        best = Math.Min(best, (qu[k] - cu) * (qu[k] - cu) + (qw[k] - cw) * (qw[k] - cw));
                    field[i, j] = (float)Math.Sqrt(best);
                }
            }

            const float cap = 0.012f, rad = (float)(Math.PI / 180.0);
            var cu4 = new float[4];
            var cw4 = new float[4];
            float Cost(float ju, float jw, float q1, float q2, float q3, float scale)
            {
                Chain4(ju, jw, q1, q2, q3, full * scale, cu4, cw4);
                float sum = 0f;
                for (int k = 0; k < qu.Count; k++)
                {
                    float d = float.MaxValue;
                    for (int b = 0; b < 3; b++)
                        d = Math.Min(d, SegDist(qu[k], qw[k], cu4[b], cw4[b], cu4[b + 1], cw4[b + 1]));
                    float e = Math.Max(0f, Math.Min(cap, d - MittenR));
                    sum += e * e;
                }

                // A bone through empty space (the grip's hollow, past the block).
                for (int b = 0; b < 3; b++)
                {
                    for (int k = 1; k < 8; k++)
                    {
                        float su = cu4[b] + (cu4[b + 1] - cu4[b]) * (k / 8f), sw = cw4[b] + (cw4[b + 1] - cw4[b]) * (k / 8f);
                        int i = (int)((su - gu + span) / cell), j = (int)((sw - gw + span) / cell);
                        if (i < 0 || j < 0 || i >= n || j >= n || field[i, j] > MittenR + 0.006f)
                            sum += cap * cap;
                    }
                }

                return sum;
            }

            float bestCost = float.MaxValue, bu = 0f, bw = 0f, b1 = 0f, b2 = 0f, b3 = 0f, bs = 1f;
            foreach (float scale in new[] { 0.85f, 1f, 1.15f })
                for (float du = -0.008f; du <= 0.0081f; du += 0.004f)
                    for (float dw = -0.008f; dw <= 0.0081f; dw += 0.004f)
                        for (float q1 = -10f; q1 <= 120.1f; q1 += 15f)
                            for (float q2 = 0f; q2 <= 120.1f; q2 += 15f)
                                for (float q3 = 0f; q3 <= 90.1f; q3 += 15f)
                                {
                                    float c = Cost(u0 + du, w0 + dw, q1 * rad, q2 * rad, q3 * rad, scale);
                                    if (c < bestCost)
                                    {
                                        bestCost = c;
                                        bu = u0 + du; bw = w0 + dw; b1 = q1; b2 = q2; b3 = q3; bs = scale;
                                    }
                                }

            float f1 = b1, f2 = b2, f3 = b3, fu = bu, fw = bw;
            for (float du = -0.002f; du <= 0.0021f; du += 0.002f)
                for (float dw = -0.002f; dw <= 0.0021f; dw += 0.002f)
                    for (float q1 = f1 - 7.5f; q1 <= f1 + 7.6f; q1 += 2.5f)
                        for (float q2 = f2 - 7.5f; q2 <= f2 + 7.6f; q2 += 2.5f)
                            for (float q3 = f3 - 7.5f; q3 <= f3 + 7.6f; q3 += 2.5f)
                            {
                                float c = Cost(fu + du, fw + dw, q1 * rad, q2 * rad, q3 * rad, bs);
                                if (c < bestCost)
                                {
                                    bestCost = c;
                                    bu = fu + du; bw = fw + dw; b1 = q1; b2 = q2; b3 = q3;
                                }
                            }

            // The mean miss per point: past ~7 mm the bones do not run through a block.
            float miss = (float)Math.Sqrt(bestCost / qu.Count);
            if (miss > 0.007f)
                return Fail(fit, "no curled block (bones miss its points by " + (miss * 1000f).ToString("F1") + " mm)");

            Chain4(bu, bw, b1 * rad, b2 * rad, b3 * rad, full * bs, cu4, cw4);
            float zMid = 0.5f * (fit.MittenZLo + fit.MittenZHi);
            Vec3 At(int k) => new Vec3(cu4[k], cw4[k] * ps, zMid);

            fit.Valid = true;
            fit.Mitten = true;
            fit.Why = "a mitten: modelled " + b1.ToString("F0") + "/" + b2.ToString("F0") + "/" + b3.ToString("F0")
                      + " deg, bones " + (full * bs * 100f).ToString("F1") + " cm, missing its points by "
                      + (miss * 1000f).ToString("F1") + " mm";
            fit.HasThumb = false;
            fit.PalmSign = ps;
            fit.ThumbSide = failed.ThumbSide != 0f ? failed.ThumbSide : 1f;
            fit.Normal = new Vec3(0f, ps, 0f);
            fit.TipX = tip;
            fit.KnuckleX = xk;
            fit.PalmCenter = new Vec3(gu - 0.02f, grip.y, zMid);
            fit.MittenTip = At(3);
            for (int f = 1; f < Digits; f++)
            {
                fit.Joint[f, 0] = At(0);
                fit.Joint[f, 1] = At(1);
                fit.Joint[f, 2] = At(2);
                fit.Dir[f] = new Vec3(1f, 0f, 0f);
                fit.Axis[f] = new Vec3(0f, 0f, ps);
                fit.Length[f] = full * bs;
                fit.Rest[f] = new Vec3(b1, b2, b3) * rad;
                fit.Ramp0[f] = -0.006f;
                fit.Ramp1[f] = 0.018f;
                fit.Blend[f] = 0.006f;
            }

            return fit;
        }

        /// <summary>A middle finger's joints and tip in the side plane: from (u, w),
        /// each bone turned q further toward the palm side.</summary>
        private static void Chain4(float u, float w, float q1, float q2, float q3, float length, float[] cu, float[] cw)
        {
            float[] bone = { PipAt[2] * length, (DipAt[2] - PipAt[2]) * length, (1f - DipAt[2]) * length };
            float[] turn = { q1, q1 + q2, q1 + q2 + q3 };
            cu[0] = u;
            cw[0] = w;
            for (int b = 0; b < 3; b++)
            {
                cu[b + 1] = cu[b] + bone[b] * (float)Math.Cos(turn[b]);
                cw[b + 1] = cw[b] + bone[b] * (float)Math.Sin(turn[b]);
            }
        }

        private static float SegDist(float pu, float pw, float au, float aw, float bu, float bw)
        {
            float du = bu - au, dw = bw - aw, l2 = du * du + dw * dw;
            float t = l2 > 1e-12f ? Math.Max(0f, Math.Min(1f, ((pu - au) * du + (pw - aw) * dw) / l2)) : 0f;
            float eu = pu - au - du * t, ew = pw - aw - dw * t;
            return (float)Math.Sqrt(eu * eu + ew * ew);
        }

        /// <summary>
        /// TEST 509: a mitten's fingertips lie ON its palm, welded to it by faces; a
        /// block point moved fully beside a palm point that stays stretched one edge
        /// x6 at a 20/20/10 close ("the palm gets squeezed"). Each block point now
        /// moves only as far as it is from the nearest point that does not move
        /// (Smooth over GlueNearM .. GlueFarM), so the weld stretches over 2 cm.
        /// digit[i] &lt; 0: a point that does not move. A no-op but on a mitten.
        /// </summary>
        public static void Glue(Fit fit, IList<Vec3> pts, IList<int> digit, IList<Vec3> w)
        {
            if (!fit.Mitten)
                return;

            var still = new List<Vec3>();
            for (int i = 0; i < pts.Count; i++)
            {
                if (digit[i] < 0)
                    still.Add(pts[i]);
            }

            for (int i = 0; i < pts.Count; i++)
            {
                if (digit[i] < 0)
                    continue;

                float best = float.MaxValue;
                foreach (Vec3 q in still)
                    best = Math.Min(best, (q - pts[i]).LengthSquared);
                w[i] = w[i] * Smooth(GlueNearM, GlueFarM, (float)Math.Sqrt(best));
            }
        }

        private const float GlueNearM = 0.003f, GlueFarM = 0.02f;

        /// <summary>TEST 504: how far along a mitten's curled bones (from its
        /// knuckle) the point nearest v is - negative behind the knuckle - and how
        /// far v is from them, in the side plane.</summary>
        private static float MittenArc(Fit fit, Vec3 v, out float off)
        {
            Vec3[] c = { fit.Joint[2, 0], fit.Joint[2, 1], fit.Joint[2, 2], fit.MittenTip };
            off = float.MaxValue;
            float arc = 0f, at = 0f;
            for (int b = 0; b < 3; b++)
            {
                float du = c[b + 1].x - c[b].x, dw = c[b + 1].y - c[b].y, len = (float)Math.Sqrt(du * du + dw * dw);
                float t = len > 1e-6f ? ((v.x - c[b].x) * du + (v.y - c[b].y) * dw) / (len * len) : 0f;
                float tc = Math.Max(b == 0 ? float.MinValue : 0f, Math.Min(1f, t));
                float eu = v.x - c[b].x - du * tc, ew = v.y - c[b].y - dw * tc;
                float d = (float)Math.Sqrt(eu * eu + ew * ew);
                if (d < off)
                {
                    off = d;
                    at = arc + tc * len;
                }

                arc += len;
            }

            return at;
        }

        /// <summary>Which digit moves this point, and how much of it rides each of
        /// that digit's three bones (the rest stays with the palm). -1 = palm only.</summary>
        public static int Weigh(Fit fit, Vec3 v, out Vec3 w)
        {
            w = Vec3.Zero;
            if (!fit.Valid)
                return -1;

            // TEST 504: a mitten's block, along its curl, across the knuckles' width.
            if (fit.Mitten)
            {
                if (v.z < fit.MittenZLo - 0.01f || v.z > fit.MittenZHi + 0.01f)
                    return -1;
                MittenArc(fit, v, out float off);
                if (off > MittenR + 0.012f)
                    return -1;
                w = Chain(fit, 2, v, 1f);
                return w.x + w.y + w.z > 0f ? 2 : -1;
            }

            // TEST 490: the whole thumb - every point in its tube (TEST 492: that
            // is nearer it than the index). 481's lump box stopped at the knuckle
            // line and above the palm, and the bare hand's thumb runs past both:
            // its middle stayed put while the rest bent (flat slabs).
            float share = fit.HasThumb ? ThumbShare(fit, v) : 0f;
            if (share > 0f)
            {
                w = Chain(fit, 0, v, share);
                if (w.x + w.y + w.z > 0f)
                    return 0;
            }

            // TEST 505: the finger whose axis is nearest - not the straight column
            // across z: the game's fingers are fanned (index +11 deg), and the
            // columns gave 7 of the bare index's points to the middle finger (the
            // index "cut" as the others closed, "left over" as it closed alone).
            int f = 1;
            float best = float.MaxValue;
            for (int g = 1; g < Digits; g++)
            {
                Vec3 q = v - fit.Joint[g, 0];
                float d = (q - fit.Dir[g] * Vec3.DotProduct(q, fit.Dir[g])).LengthSquared;
                if (d < best)
                {
                    best = d;
                    f = g;
                }
            }

            if (!Near(fit, f, v))
                return -1;

            w = Chain(fit, f, v, 1f);
            return w.x + w.y + w.z > 0f ? f : -1;
        }

        /// <summary>The point bent by the digit's three joint angles (radians),
        /// in the same rest axes. Linear blend over the palm and the three bones.</summary>
        public static Vec3 Bend(Fit fit, int digit, Vec3 angles, Vec3 v, Vec3 w) => Bend(fit, digit, angles, 0f, v, w);

        /// <summary>The same, the bent digit then turned by spread (radians) about
        /// the palm normal at its first joint - TEST 488's fingers coming together.</summary>
        public static Vec3 Bend(Fit fit, int digit, Vec3 angles, float spread, Vec3 v, Vec3 w)
            => Bend(fit, digit, angles, fit.Normal * spread, v, w);

        /// <summary>The same with a turn of any axis (axis times angle, radians) -
        /// TEST 491's thumb lift.</summary>
        public static Vec3 Bend(Fit fit, int digit, Vec3 angles, Vec3 turn, Vec3 v, Vec3 w)
            => new Posed(fit, digit, angles, turn).Apply(v, w);

        /// <summary>
        /// TEST 508: one digit bent to fixed angles (radians) after its turn, its
        /// sines and cosines worked out once. A wrap bends every point of a digit
        /// some hundred times, and Bend's 12 sin/cos a point made a hand's five
        /// wraps 23 ms - 4 frames of the hold budget before a grab's fingers could
        /// close. A point rides only the bones it has weight on.
        /// </summary>
        public readonly struct Posed
        {
            private readonly Vec3 _a, _j1, _j2, _j3, _turnAxis;
            private readonly float _c1, _s1, _c2, _s2, _c3, _s3, _ct, _st;
            private readonly bool _turned;

            public Posed(Fit fit, int digit, Vec3 angles, Vec3 turn)
            {
                angles -= fit.Rest[digit];
                _a = fit.Axis[digit];
                _j1 = fit.Joint[digit, 0];
                _j2 = fit.Joint[digit, 1];
                _j3 = fit.Joint[digit, 2];
                _c1 = (float)Math.Cos(angles.x);
                _s1 = (float)Math.Sin(angles.x);
                _c2 = (float)Math.Cos(angles.y);
                _s2 = (float)Math.Sin(angles.y);
                _c3 = (float)Math.Cos(angles.z);
                _s3 = (float)Math.Sin(angles.z);
                float angle = turn.Length;
                _turned = angle > 1e-6f;
                _turnAxis = _turned ? turn * (1f / angle) : Vec3.Zero;
                _ct = (float)Math.Cos(angle);
                _st = (float)Math.Sin(angle);
            }

            public Vec3 Apply(Vec3 v, Vec3 w)
            {
                Vec3 result = v * (1f - w.x - w.y - w.z);
                if (w.x != 0f)
                    result += Base(Rot(v, _j1, _a, _c1, _s1)) * w.x;
                if (w.y != 0f)
                    result += Base(Rot(Rot(v, _j2, _a, _c2, _s2), _j1, _a, _c1, _s1)) * w.y;
                if (w.z != 0f)
                    result += Base(Rot(Rot(Rot(v, _j3, _a, _c3, _s3), _j2, _a, _c2, _s2), _j1, _a, _c1, _s1)) * w.z;
                return result;
            }

            private Vec3 Base(Vec3 v) => _turned ? Rot(v, _j1, _turnAxis, _ct, _st) : v;

            private static Vec3 Rot(Vec3 v, Vec3 joint, Vec3 axis, float c, float s)
            {
                if (s == 0f && c == 1f)
                    return v;

                Vec3 r = v - joint;
                return joint + r * c + Vec3.CrossProduct(axis, r) * s + axis * (Vec3.DotProduct(axis, r) * (1f - c));
            }
        }

        /// <summary>TEST 488: how far a finger turns toward the middle finger at
        /// curl c - restFrac of its modelled splay taken back when open, fistFrac
        /// in a fist. Radians; 0 for the thumb and the middle finger.</summary>
        public static float Spread(Fit fit, int digit, float restFrac, float fistFrac, float c)
        {
            if (digit <= 0)
                return 0f;

            c = Math.Max(0f, Math.Min(1f, c));
            return -fit.Splay[digit] * (restFrac + (fistFrac - restFrac) * c);
        }

        /// <summary>The three joint angles (radians) at curl c, from the relaxed
        /// and the fist angles (degrees).</summary>
        public static Vec3 Angles(Vec3 relaxDeg, Vec3 fistDeg, float c)
        {
            c = Math.Max(0f, Math.Min(1f, c));
            const float rad = (float)(Math.PI / 180.0);
            return (relaxDeg + (fistDeg - relaxDeg) * c) * rad;
        }

        /// <summary>TEST 483: what the hand holds, near the palm, as a cylinder -
        /// fitted to the held item's own vertices (hand rest axes), so the fingers
        /// close on THIS weapon's handle, guard or shaft.</summary>
        public sealed class Handle
        {
            public bool Valid;
            public string Why = string.Empty;
            public Vec3 Center;
            public Vec3 Axis;
            public float Radius;
            public float Half;
            public float Ratio;
            public float[] Profile = new float[0];
            public int Points;

            /// <summary>TEST 504: what else this wrap stops on (the thumb's: the
            /// wrapped fingers).</summary>
            public Obstacle Block;

            /// <summary>The same handle, stopping on `block` as well.</summary>
            public Handle With(Obstacle block)
            {
                var h = (Handle)MemberwiseClone();
                h.Block = block;
                return h;
            }
        }

        private const float ProfileStep = 0.005f;

        /// <summary>TEST 487: a handle that is a LINE (a crafted haft, whose
        /// points cannot be read) - centred on the point of the line nearest the
        /// palm channel, wherever along the shaft the hand is (483b's two-handed
        /// off hand had it 0.381 m away and closed into a fist in the air).</summary>
        public static Handle LineHandle(Vec3 point, Vec3 axis, Vec3 channel, float radius, float half)
        {
            var h = new Handle { Why = "no item axis", Radius = radius, Half = half, Profile = new[] { radius } };
            if (axis.Length < 0.1f)
                return h;

            h.Axis = axis.NormalizedCopy();
            h.Center = point + h.Axis * Vec3.DotProduct(channel - point, h.Axis);
            h.Valid = true;
            return h;
        }

        /// <summary>The held item's points within zoneM of the palm channel, as
        /// a cylinder: main axis, median radius, and the radius every 5 mm along
        /// it (handles taper, guards stick out). Invalid when the points are not
        /// long and thin (a shield board, a blob).</summary>
        public static Handle FitHandle(IList<Vec3> pts, Vec3 channel, float zoneM)
        {
            var h = new Handle();
            var zone = new List<Vec3>();
            foreach (Vec3 v in pts)
            {
                if ((v - channel).LengthSquared <= zoneM * zoneM)
                    zone.Add(v);
            }

            h.Points = zone.Count;
            if (zone.Count < 12)
            {
                h.Why = zone.Count + " points near the palm";
                return h;
            }

            Line(zone, out Vec3 centre, out Vec3 axis);
            float along = 0f, across = 0f;
            var t = new float[zone.Count];
            var r = new float[zone.Count];
            for (int i = 0; i < zone.Count; i++)
            {
                Vec3 d = zone[i] - centre;
                t[i] = Vec3.DotProduct(d, axis);
                r[i] = (d - axis * t[i]).Length;
                along += t[i] * t[i];
                across += r[i] * r[i];
                h.Half = Math.Max(h.Half, Math.Abs(t[i]));
            }

            // Spread along against spread across (2 axes): long and thin = a handle.
            h.Ratio = across > 1e-12f ? along / (0.5f * across) : 999f;
            if (h.Ratio < 4f)
            {
                h.Why = "not long and thin (ratio " + h.Ratio.ToString("F1") + ")";
                return h;
            }

            var sorted = (float[])r.Clone();
            Array.Sort(sorted);
            h.Radius = sorted[sorted.Length / 2];
            h.Center = centre;
            h.Axis = axis;

            int bins = (int)(2f * h.Half / ProfileStep) + 1;
            h.Profile = new float[bins];
            var near = new List<float>();
            for (int b = 0; b < bins; b++)
            {
                float at = -h.Half + b * ProfileStep;
                near.Clear();
                for (int i = 0; i < zone.Count; i++)
                {
                    if (Math.Abs(t[i] - at) <= 0.012f)
                        near.Add(r[i]);
                }

                near.Sort();
                h.Profile[b] = near.Count >= 4 ? near[near.Count / 2] : h.Radius;
            }

            h.Valid = true;
            return h;
        }

        /// <summary>
        /// The digit's three joint angles (radians) WRAPPED around the handle,
        /// the way a hand closes on a grip, base first (TEST 491 - all three joints
        /// together put the fingertip on the handle's near side with the base barely
        /// bent, a hook): the base joint alone until the digit touches (its radius
        /// there plus skin), then the middle and end joints until they touch, then
        /// the end joint alone. A joint that never touches closes to its fist angle.
        /// TEST 489: a point already
        /// inside the handle when relaxed is not a touch - the grip lies across the
        /// finger roots, and 487 stopped the index there unbent (8/12/8 deg).
        /// </summary>
        public static Vec3 Wrap(Fit fit, int digit, Vec3 relaxDeg, Vec3 fistDeg, IList<Vec3> pts, IList<Vec3> ws,
                                Handle h, float skin, float spread = 0f)
            => Wrap(fit, digit, relaxDeg, fistDeg, pts, ws, h, skin, fit.Normal * spread);

        /// <summary>The same, the whole digit first turned by `turn` (its axis
        /// times the angle, radians) about its first joint - a finger's spread,
        /// or TEST 491's thumb lift (Lift).</summary>
        public static Vec3 Wrap(Fit fit, int digit, Vec3 relaxDeg, Vec3 fistDeg, IList<Vec3> pts, IList<Vec3> ws,
                                Handle h, float skin, Vec3 turn)
        {
            const float rad = (float)(Math.PI / 180.0);
            Vec3 fist = fistDeg * rad;
            Vec3 relaxed = Angles(relaxDeg, fistDeg, 0f);
            Vec3 a = Close(u => Lerp1(relaxed, fist, u), fit, digit, turn, pts, ws, h, skin);

            Vec3 from = a;
            a = Close(u => Lerp2(from, fist, u), fit, digit, turn, pts, ws, h, skin);

            // TEST 504: the end joint alone closes no further than the joint before
            // it - a fingertip cannot curl past its middle joint (the tendons couple
            // them). 491 took it to the fist whenever it touched nothing: index
            // 65/36/70, ring 77/55/70 on the gauntlets - the tip folded double.
            Vec3 mid = a;
            Vec3 tip = new Vec3(fist.x, fist.y, Math.Max(mid.z, Math.Min(fist.z, mid.y)));
            return Close(u => Lerp3(mid, tip, u), fit, digit, turn, pts, ws, h, skin);
        }

        /// <summary>
        /// TEST 491: the thumb's way round a handle. Seated in the palm behind the
        /// knuckles (Seat), a handle lies where the relaxed thumb is, and folding the
        /// thumb only drove it through. A real thumb goes OVER the handle: the whole
        /// digit turns at its first joint the way that raises it along the palm
        /// normal, by the least angle (5 deg steps) that clears it - then it closes
        /// onto the handle like a finger. Returns the turn (axis times angle); zero
        /// when nothing of it is in the way.
        /// TEST 504: it turns about its own ABDUCTION axis (square to the thumb and
        /// the palm normal), at most LiftMaxDeg - a thumb lifts off the palm that
        /// way and no other. 491 turned it about the handle's direction, up to 75
        /// deg: on a handle across the hand that twisted the thumb on its side, and
        /// the gauntlets took 65-75 deg on every sword and mace ("bends like it is
        /// going to break").
        /// </summary>
        public static Vec3 Lift(Fit fit, int digit, Vec3 relaxDeg, Vec3 fistDeg, IList<Vec3> pts, IList<Vec3> ws, Handle h,
                                float skin)
        {
            Vec3 relaxed = Angles(relaxDeg, fistDeg, 0f);
            if (!h.Valid || !Hits(fit, digit, relaxed, Vec3.Zero, pts, ws, h, skin))
                return Vec3.Zero;

            // The sign that moves the digit's far end up the palm normal.
            Vec3 tip = fit.Joint[digit, 0] + fit.Dir[digit] * fit.Length[digit];
            Vec3 axis = Vec3.CrossProduct(fit.Dir[digit], fit.Normal);
            axis = axis.Length > 0.1f ? axis.NormalizedCopy() : h.Axis;
            if (Vec3.DotProduct(Vec3.CrossProduct(axis, tip - fit.Joint[digit, 0]), fit.Normal) < 0f)
                axis = -axis;

            const float step = (float)(5.0 * Math.PI / 180.0);
            int steps = (int)(LiftMaxDeg / 5f);
            for (int k = 1; k <= steps; k++)
            {
                Vec3 turn = axis * (k * step);
                if (!Hits(fit, digit, relaxed, turn, pts, ws, h, skin))
                    return turn;
            }

            return axis * (steps * step);
        }

        /// <summary>TEST 504: the most a thumb lifts off the palm (palmar abduction).</summary>
        public const float LiftMaxDeg = 45f;

        /// <summary>TEST 504: how much deeper than where a stage found it a touching
        /// point may press into the handle.</summary>
        private const float PressM = 0.004f;

        /// <summary>One stage of a wrap: the pose at the largest u before a NEW
        /// touch. TEST 491: a point already touching when the stage starts (within
        /// 3 mm - the one that stopped the stage before, or one lying in the handle
        /// at rest) presses on into the flesh instead of stopping it, so the middle
        /// joint closes over the handle once the base has met it.
        /// TEST 504: but only PressM deeper than it was. 491 let it go anywhere, so a
        /// digit lying ALONG a diagonal handle - every point touching at once - closed
        /// its next joints straight through it to the fist (index 24/98/70, the thumb
        /// 35/45/60 on nearly every grip: "it bends like it is going to break").</summary>
        private static Vec3 Close(Func<float, Vec3> pose, Fit fit, int digit, Vec3 turn, IList<Vec3> pts, IList<Vec3> ws,
                                  Handle h, float skin)
        {
            var start = new Posed(fit, digit, pose(0f), turn);
            var limit = new float[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                float depth = Depth(start, pts[i], ws[i], h);
                limit[i] = depth < skin + 0.003f ? Math.Min(depth, skin) - PressM : skin;
            }

            return pose(Stop(pose, fit, digit, turn, pts, ws, h, skin, limit));
        }

        private static Vec3 Lerp1(Vec3 a, Vec3 b, float u) => new Vec3(a.x + (b.x - a.x) * u, a.y, a.z);

        private static Vec3 Lerp2(Vec3 a, Vec3 b, float u) => new Vec3(a.x, a.y + (b.y - a.y) * u, a.z + (b.z - a.z) * u);

        private static Vec3 Lerp3(Vec3 a, Vec3 b, float u) => new Vec3(a.x, a.y, a.z + (b.z - a.z) * u);

        /// <summary>The largest u in [0, 1] before the posed digit touches the
        /// handle: steps of 0.05, then 8 halvings.</summary>
        private static float Stop(Func<float, Vec3> pose, Fit fit, int digit, Vec3 turn, IList<Vec3> pts,
                                  IList<Vec3> ws, Handle h, float skin, float[] limit)
        {
            if (Hits(fit, digit, pose(0f), turn, pts, ws, h, skin, limit))
                return 0f;

            float lo = 0f, hi = -1f;
            for (float u = 0.05f; u <= 1.0001f; u += 0.05f)
            {
                if (Hits(fit, digit, pose(Math.Min(1f, u)), turn, pts, ws, h, skin, limit))
                {
                    hi = Math.Min(1f, u);
                    break;
                }

                lo = Math.Min(1f, u);
            }

            if (hi < 0f)
                return 1f;

            for (int i = 0; i < 8; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (Hits(fit, digit, pose(mid), turn, pts, ws, h, skin, limit))
                    hi = mid;
                else
                    lo = mid;
            }

            return lo;
        }

        /// <summary>Whether any point is deeper in the handle than skin (per point:
        /// limit[i], when given).</summary>
        private static bool Hits(Fit fit, int digit, Vec3 angles, Vec3 turn, IList<Vec3> pts, IList<Vec3> ws,
                                 Handle h, float skin, float[] limit = null)
        {
            var pose = new Posed(fit, digit, angles, turn);
            for (int i = 0; i < pts.Count; i++)
            {
                if (Depth(pose, pts[i], ws[i], h) < (limit?[i] ?? skin))
                    return true;
            }

            return false;
        }

        /// <summary>How far the bent point is outside the handle's surface
        /// (negative: inside); MaxValue past its ends.</summary>
        private static float Depth(in Posed pose, Vec3 p, Vec3 w, Handle h)
        {
            Vec3 bent = pose.Apply(p, w);
            Vec3 d = bent - h.Center;
            float t = Vec3.DotProduct(d, h.Axis);
            float depth = float.MaxValue;
            if (Math.Abs(t) <= h.Half)
            {
                int b = Math.Max(0, Math.Min(h.Profile.Length - 1, (int)Math.Round((t + h.Half) / ProfileStep)));
                depth = (d - h.Axis * t).Length - h.Profile[b];
            }

            return h.Block == null ? depth : Math.Min(depth, h.Block.Gap(bent));
        }

        /// <summary>
        /// TEST 504: points a wrap may not pass through besides the handle - the
        /// fingers as wrapped, for the thumb. The handle lies inside the fingers'
        /// fist, so a thumb folding over it met nothing and closed to its own fist
        /// (35/45/60 on nearly every grip), folded into a Z over the fingers. It
        /// now stops on them, the way a thumb rests on the index's middle bone.
        /// A point touches within ContactM + the wrap's skin of one of them
        /// (surface vertices a few mm apart: 5 mm with finger_hold_skin_m -0.003).
        /// </summary>
        public sealed class Obstacle
        {
            // A cell as wide as anything that can count as touching (ContactM, plus
            // the wrap's skin of up to a few mm), so the 27 around a point hold all of it.
            private const float Cell = 0.012f;
            private const float ContactM = 0.008f;
            private readonly Dictionary<long, List<Vec3>> _cells = new Dictionary<long, List<Vec3>>();

            public Obstacle(IEnumerable<Vec3> pts)
            {
                foreach (Vec3 p in pts)
                {
                    long k = Key(C(p.x), C(p.y), C(p.z));
                    if (!_cells.TryGetValue(k, out List<Vec3> list))
                        _cells[k] = list = new List<Vec3>();
                    list.Add(p);
                }
            }

            public int Count => _cells.Count;

            /// <summary>How far p is from the nearest point, less ContactM (never
            /// more than a cell: past that it touches nothing anyway).</summary>
            public float Gap(Vec3 p)
            {
                int x = C(p.x), y = C(p.y), z = C(p.z);
                float best = Cell * Cell;
                for (int i = -1; i <= 1; i++)
                    for (int j = -1; j <= 1; j++)
                        for (int k = -1; k <= 1; k++)
                        {
                            if (!_cells.TryGetValue(Key(x + i, y + j, z + k), out List<Vec3> list))
                                continue;
                            foreach (Vec3 q in list)
                                best = Math.Min(best, (q - p).LengthSquared);
                        }

                return (float)Math.Sqrt(best) - ContactM;
            }

            private static int C(float v) => (int)Math.Floor(v / Cell);

            private static long Key(int x, int y, int z) => ((long)(x & 0xFFFFF) << 40) | ((long)(y & 0xFFFFF) << 20) | (uint)(z & 0xFFFFF);
        }

        /// <summary>
        /// TEST 491: where a handle must move to lie the way a real power grip holds
        /// one - in the palm behind the knuckles, resting ON the palm. The engine
        /// hangs a weapon at finger0, inside the open hand's palm: on the gauntlets
        /// 2 cm of the shaft sank under the palm's skin with its front edge a
        /// centimetre past the knuckles, so a finger's first segment could not rise
        /// in front of it - the fingers curled into a ball beside it and the weapon
        /// sat off them. So:
        ///  * back, square to the handle in the palm's plane, until its front edge
        ///    is at the middle finger's base joint (mcp, along fingerDir);
        ///  * then up the palm normal until its bottom is 1 mm above the palm's
        ///    surface there (the 90th percentile height of the palm's points within
        ///    the handle's width and 3 cm of its centre).
        /// Never toward the fingers or into the palm; at most maxM in all.
        /// </summary>
        public static Vec3 Seat(IList<Vec3> palm, Handle h, Vec3 normal, Vec3 mcp, Vec3 fingerDir, float maxM)
        {
            if (!h.Valid)
                return Vec3.Zero;

            Vec3 n = (normal - h.Axis * Vec3.DotProduct(normal, h.Axis)).NormalizedCopy();
            Vec3 f = fingerDir - n * Vec3.DotProduct(fingerDir, n);
            f = (f - h.Axis * Vec3.DotProduct(f, h.Axis)).NormalizedCopy();
            float back = Math.Max(0f, Vec3.DotProduct(h.Center - mcp, f) + h.Radius - 0.002f);
            Vec3 centre = h.Center - f * back;

            Vec3 across = Vec3.CrossProduct(h.Axis, n).NormalizedCopy();
            var heights = new List<float>();
            foreach (Vec3 p in palm)
            {
                Vec3 d = p - centre;
                if (Math.Abs(Vec3.DotProduct(d, h.Axis)) <= 0.03f && Math.Abs(Vec3.DotProduct(d, across)) <= h.Radius)
                    heights.Add(Vec3.DotProduct(d, n));
            }

            float up = 0f;
            if (heights.Count >= 5)
            {
                heights.Sort();
                up = Math.Max(0f, heights[(int)(heights.Count * 0.9f)] + h.Radius + 0.001f);
            }

            Vec3 move = n * up - f * back;
            return move.Length > maxM ? move.NormalizedCopy() * maxM : move;
        }

        /// <summary>
        /// TEST 497: how far each finger slides along the handle (`along`) so the
        /// wrapped fingers lie side by side, the way a real grip packs them. The
        /// game's hands are modelled open, their knuckles spaced for it: wrapped,
        /// the middle and ring fingers stood 7-9 mm apart (bare hand and gauntlet
        /// alike), and adduction - a turn about the palm normal - barely moves a
        /// curled finger sideways. The middle finger stays; the index and ring
        /// close on it, the pinky on the ring as moved - each gap, across the two
        /// fingers averaged over Along of their wrapped length at their widths
        /// there (TEST 504), brought to gapM (negative: an overlap pushed apart).
        /// Sliding ALONG the handle keeps every finger on it. At most maxM each;
        /// the thumb's is zero.
        /// </summary>
        public static Vec3[] SideBySide(Fit fit, Vec3[] angles, Vec3[] turns, float[][] widths, Vec3 along, float gapM, float maxM)
        {
            var shift = new Vec3[Digits];
            if (!fit.Valid || fit.Mitten || along.Length < 0.1f)
                return shift;

            along = along.NormalizedCopy();
            var at = new Vec3[Digits, Along.Length];
            for (int f = 1; f < Digits; f++)
            {
                for (int k = 0; k < Along.Length; k++)
                {
                    Vec3 p = fit.Joint[f, 0] + fit.Dir[f] * (Along[k] * fit.Length[f]);
                    at[f, k] = Bend(fit, f, angles[f], turns[f], p, Chain(fit, f, p, 1f));
                }
            }

            // TEST 504: the gap averaged along the fingers, each at its own width
            // there (Widths) - 502 took one point at 60 % and the p90 width of the
            // whole finger, the knuckle's: thinner further out, they stood apart.
            Vec3 Toward(int f, int to, Vec3 moved)
            {
                Vec3 n = (fit.Axis[f] + fit.Axis[to]).NormalizedCopy();
                float per = Vec3.DotProduct(along, n);
                // A handle running along the fingers cannot close a gap across them.
                if (Math.Abs(per) < 0.3f)
                    return Vec3.Zero;

                float across = 0f, gap = 0f;
                for (int k = 0; k < Along.Length; k++)
                {
                    float a = Vec3.DotProduct(at[to, k] + moved - at[f, k], n);
                    across += a;
                    gap += Math.Abs(a) - widths[f][k] - widths[to][k] - gapM;
                }

                gap /= Along.Length;
                float slide = Math.Max(-maxM, Math.Min(maxM, gap / Math.Abs(per)));
                return along * (per * across >= 0f ? slide : -slide);
            }

            shift[1] = Toward(1, 2, Vec3.Zero);
            shift[3] = Toward(3, 2, Vec3.Zero);
            shift[4] = Toward(4, 3, shift[3]);
            return shift;
        }

        /// <summary>TEST 497/502: a finger's half-width ACROSS the hand (along its flex
        /// axis) - the 90th percentile of its fully weighted points' distance from
        /// its centre line that way. Fingers are wider than they are deep: 497 used
        /// the median radius (middle 10.6 mm against 13.4 across) and slid the index
        /// 1-3 mm into the middle finger - it looked thin.</summary>
        public static float HalfWidth(Fit fit, int digit, IList<Vec3> pts, IList<Vec3> ws)
        {
            var r = new List<float>();
            for (int i = 0; i < pts.Count; i++)
            {
                if (ws[i].x + ws[i].y + ws[i].z >= 0.9f)
                    r.Add(Math.Abs(Vec3.DotProduct(pts[i] - fit.Joint[digit, 0], fit.Axis[digit])));
            }

            if (r.Count == 0)
                return 0.009f;
            r.Sort();
            return r[(int)(r.Count * 0.9f)];
        }

        /// <summary>
        /// TEST 499/505: where an arrow rests on a wrapped fist, in the hand's rest
        /// axes. The stave's side away from the palm (the back of the hand's side:
        /// left for a bow in the left hand) - the arrow's shaft against it (gap
        /// out) - at the height where the index's first bone passes the stave, then
        /// raised up the stave (toward the index) until the arrow's line, square to
        /// the stave and that side, 8 cm either way, clears every point of the hand
        /// as wrapped (`hand`). 499 put it where the index's bone passes nearest the
        /// stave's axis - in FRONT of it for a real hand, and anywhere across it for
        /// a mitten, whose "index" is the block's middle: the arrow on the wrong
        /// side.
        /// </summary>
        public static Vec3 ArrowRest(Fit fit, Handle h, Vec3[] wrap, Vec3[] turns, Vec3[] slide, IList<Vec3> hand, float gap)
        {
            Vec3 up = h.Axis * (h.Axis.z * fit.ThumbSide >= 0f ? 1f : -1f);
            Vec3 dorsal = -(fit.Normal - up * Vec3.DotProduct(fit.Normal, up));
            dorsal = dorsal.Length > 0.1f ? dorsal.NormalizedCopy() : Vec3.CrossProduct(up, new Vec3(1f, 0f, 0f)).NormalizedCopy();
            Vec3 way = Vec3.CrossProduct(up, dorsal).NormalizedCopy();

            // The height of the index's first bone where it passes the stave.
            Vec3 a0 = fit.Joint[1, 0] + slide[1];
            Vec3 a1 = Bend(fit, 1, wrap[1], turns[1], fit.Joint[1, 1], new Vec3(1f, 0f, 0f)) + slide[1];
            float best = float.MaxValue, height = 0f;
            for (int k = 0; k <= 20; k++)
            {
                Vec3 q = a0 + (a1 - a0) * (k / 20f) - h.Center;
                float t = Vec3.DotProduct(q, up);
                float d = (q - up * t).Length;
                if (d < best)
                {
                    best = d;
                    height = t;
                }
            }

            // TEST 506: clear of the hand's SURFACE, not just its vertices - a line can
            // pass between vertices a centimetre apart (the battania mitten) straight
            // through the faces they make: "the arrow is left but inside my hand".
            float clear = gap + 0.7f * Spacing(hand);
            Vec3 rest = h.Center + up * height + dorsal * (h.Radius + gap);
            for (int step = 0; step < 80 && Blocks(hand, rest, way, clear); step++)
                rest += up * 0.001f;
            return rest;
        }

        /// <summary>TEST 506: the median distance from a point of the hand to its
        /// nearest neighbour (up to 200 sampled) - how far apart its vertices are.</summary>
        public static float Spacing(IList<Vec3> pts)
        {
            if (pts.Count < 2)
                return 0f;

            var near = new List<float>();
            int step = Math.Max(1, pts.Count / 200);
            for (int i = 0; i < pts.Count; i += step)
            {
                float best = float.MaxValue;
                for (int j = 0; j < pts.Count; j++)
                {
                    // Seams repeat a vertex at one spot; those are not neighbours.
                    float d = (pts[j] - pts[i]).LengthSquared;
                    if (j != i && d > 0.0005f * 0.0005f)
                        best = Math.Min(best, d);
                }

                if (best < float.MaxValue)
                    near.Add(best);
            }

            if (near.Count == 0)
                return 0f;
            near.Sort();
            return (float)Math.Sqrt(near[near.Count / 2]);
        }

        /// <summary>Whether any point lies within `gap` of the line along `way`
        /// through `at`, within 8 cm of it.</summary>
        private static bool Blocks(IList<Vec3> hand, Vec3 at, Vec3 way, float gap)
        {
            foreach (Vec3 p in hand)
            {
                Vec3 q = p - at;
                float k = Vec3.DotProduct(q, way);
                if (Math.Abs(k) <= 0.08f && (q - way * k).Length < gap)
                    return true;
            }

            return false;
        }

        /// <summary>TEST 504: where along a finger (fractions of its length) the side
        /// gaps are measured.</summary>
        public static readonly float[] Along = { 0.35f, 0.5f, 0.65f, 0.8f };

        /// <summary>TEST 504: a finger's half-width ACROSS the hand at each Along -
        /// half the 10-90 % spread of its fully weighted points within 8 % of the
        /// length there; HalfWidth where fewer than 6 points are.</summary>
        public static float[] Widths(Fit fit, int digit, IList<Vec3> pts, IList<Vec3> ws)
        {
            var w = new float[Along.Length];
            float whole = HalfWidth(fit, digit, pts, ws);
            var across = new List<float>();
            for (int k = 0; k < Along.Length; k++)
            {
                across.Clear();
                for (int i = 0; i < pts.Count; i++)
                {
                    Vec3 d = pts[i] - fit.Joint[digit, 0];
                    float s = Vec3.DotProduct(d, fit.Dir[digit]) / Math.Max(0.01f, fit.Length[digit]);
                    if (ws[i].x + ws[i].y + ws[i].z >= 0.9f && Math.Abs(s - Along[k]) <= 0.08f)
                        across.Add(Vec3.DotProduct(d, fit.Axis[digit]));
                }

                if (across.Count < 6)
                {
                    w[k] = whole;
                    continue;
                }

                across.Sort();
                w[k] = 0.5f * (across[(int)(across.Count * 0.9f)] - across[(int)(across.Count * 0.1f)]);
            }

            return w;
        }

        /// <summary>TEST 488: which of `keys` keyframes shows curl c (0..1), given
        /// the one shown now (-1: none). It changes only once the curl is 0.7 of
        /// a key from it, so a grip held between two keys never flickers.</summary>
        public static int Key(float c, int keys, int shown)
        {
            float at = Math.Max(0f, Math.Min(1f, c)) * (keys - 1);
            if (shown >= 0 && shown < keys && Math.Abs(at - shown) < 0.7f)
                return shown;

            return (int)Math.Round(at);
        }

        /// <summary>TEST 482: bvr_get_finger_touch's bits, per hand.</summary>
        public const uint TouchTrigger = 0x1, TouchThumb = 0x2, TouchTriggerBound = 0x4, TouchThumbBound = 0x8,
                          TouchThumbrest = 0x10;

        /// <summary>TEST 482: an EMPTY hand's curl targets from the physical
        /// controls - x thumb, y index, z middle/ring/pinky. The grip closes the
        /// three; a finger resting on the trigger moves the index by indexTouch,
        /// and closes it as the grip is squeezed (TEST 494: smoothly over 0.2-0.8 of
        /// the squeeze - it jumped shut at half); the thumb rests at thumbRest
        /// while it touches the stick or a button. A sensor the runtime did not
        /// bind: the trigger counts as touched once pulled, the thumb with the grip.</summary>
        public static Vec3 FreeCurls(float trigger, float squeeze, uint touch, float indexTouch, float thumbRest)
        {
            bool onTrigger = (touch & TouchTriggerBound) != 0 ? (touch & TouchTrigger) != 0 : trigger > 0.05f;
            bool onThumb = (touch & TouchThumbBound) != 0 ? (touch & TouchThumb) != 0 : squeeze >= 0.5f;
            float index = Math.Max(trigger, onTrigger ? indexTouch + (1f - indexTouch) * Smooth(0.2f, 0.8f, squeeze) : 0f);
            return new Vec3(onThumb ? thumbRest : 0f, index, squeeze);
        }

        /// <summary>TEST 482: whether a HOLDING hand's index and thumb are on the
        /// handle - while touching (the index also while pulling); always, where
        /// the runtime bound no sensor.</summary>
        public static void Engaged(float trigger, uint touch, out bool index, out bool thumb)
        {
            index = (touch & TouchTriggerBound) == 0 || (touch & TouchTrigger) != 0 || trigger > 0.05f;
            thumb = (touch & TouchThumbBound) == 0 || (touch & TouchThumb) != 0;
        }

        private static Vec3 Chain(Fit fit, int f, Vec3 v, float scale)
        {
            float s = Vec3.DotProduct(v - fit.Joint[f, 0], fit.Dir[f]);
            float s2 = Vec3.DotProduct(fit.Joint[f, 1] - fit.Joint[f, 0], fit.Dir[f]);
            float s3 = Vec3.DotProduct(fit.Joint[f, 2] - fit.Joint[f, 0], fit.Dir[f]);
            if (fit.Mitten)
            {
                // TEST 504: along the curled bones, not a straight finger's axis.
                s = MittenArc(fit, v, out _);
                s2 = (fit.Joint[f, 1] - fit.Joint[f, 0]).Length;
                s3 = s2 + (fit.Joint[f, 2] - fit.Joint[f, 1]).Length;
            }

            float move = Smooth(fit.Ramp0[f], fit.Ramp1[f], s) * scale;
            float t2 = Smooth(s2 - fit.Blend[f], s2 + fit.Blend[f], s);
            float t3 = Smooth(s3 - fit.Blend[f], s3 + fit.Blend[f], s);
            return new Vec3(move * (1f - t2), move * (t2 - t3), move * t3);
        }

        private static bool Near(Fit fit, int f, Vec3 v)
        {
            Vec3 d = v - fit.Joint[f, 0];
            Vec3 off = d - fit.Dir[f] * Vec3.DotProduct(d, fit.Dir[f]);
            return off.Length <= ReachM;
        }

        /// <summary>TEST 492: how much of the thumb's bend a point takes - 1 in its
        /// tube, fading to 0 over ThumbFadeM outside it (the web skin bends with
        /// it instead of shearing), 0 where the index's axis is nearer. 490 cut
        /// everything within 8 mm of the index column instead, which sliced off
        /// the thumb's index-facing half: 36 gauntlet vertices there stayed at
        /// bind while the thumb bent - the flap at the web.</summary>
        private static float ThumbShare(Fit fit, Vec3 v)
        {
            Vec3 dir = fit.Dir[0];
            float s = Vec3.DotProduct(v - fit.ThumbAt, dir);
            if (s < fit.ThumbLo - 0.01f || s > fit.ThumbHi + 0.003f)
                return 0f;

            float r = (v - fit.ThumbAt - dir * s).Length;
            float edge = fit.ThumbRadius + ThumbMarginM;
            if (r > edge + ThumbFadeM)
                return 0f;

            Vec3 j = fit.Joint[1, 0];
            float t = Math.Max(-0.03f, Math.Min(fit.Length[1], Vec3.DotProduct(v - j, fit.Dir[1])));
            return r < (v - j - fit.Dir[1] * t).Length ? 1f - Smooth(edge, edge + ThumbFadeM, r) : 0f;
        }

        private static bool InLump(Fit fit, Vec3 v, float indexZ)
        {
            return v.x >= fit.KnuckleX - 0.07f && v.x <= fit.KnuckleX + 0.005f
                   && (v.z - indexZ) * fit.ThumbSide > 0.008f;
        }

        private static int Column(Fit fit, float z)
        {
            int c = 0;
            while (c < 3 && z > fit.Bound[c])
                c++;
            return c;
        }

        private static float Smooth(float a, float b, float x)
        {
            float t = Math.Max(0f, Math.Min(1f, (x - a) / (b - a)));
            return t * t * (3f - 2f * t);
        }

        /// <summary>Four 1D cluster centres, sorted.</summary>
        private static void KMeans4(List<float> z, float[] centre)
        {
            z.Sort();
            for (int i = 0; i < 4; i++)
                centre[i] = z[(int)(z.Count * (0.125f + 0.25f * i))];

            var sum = new float[4];
            var n = new int[4];
            for (int iter = 0; iter < 25; iter++)
            {
                Array.Clear(sum, 0, 4);
                Array.Clear(n, 0, 4);
                foreach (float v in z)
                {
                    int best = 0;
                    for (int i = 1; i < 4; i++)
                    {
                        if (Math.Abs(v - centre[i]) < Math.Abs(v - centre[best]))
                            best = i;
                    }

                    sum[best] += v;
                    n[best]++;
                }

                for (int i = 0; i < 4; i++)
                {
                    if (n[i] > 0)
                        centre[i] = sum[i] / n[i];
                }
            }

            Array.Sort(centre);
        }

        /// <summary>The centroid and main axis (power iteration on the scatter).</summary>
        private static void Line(List<Vec3> pts, out Vec3 centre, out Vec3 dir)
        {
            centre = Vec3.Zero;
            foreach (Vec3 v in pts)
                centre += v;
            centre *= 1f / pts.Count;

            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (Vec3 v in pts)
            {
                Vec3 d = v - centre;
                xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z;
                yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
            }

            dir = new Vec3(1f, 0f, 0f);
            for (int i = 0; i < 30; i++)
            {
                var next = new Vec3(xx * dir.x + xy * dir.y + xz * dir.z,
                                    xy * dir.x + yy * dir.y + yz * dir.z,
                                    xz * dir.x + yz * dir.y + zz * dir.z);
                if (next.Length < 1e-12f)
                    break;
                dir = next.NormalizedCopy();
            }
        }

        private static Fit Fail(Fit fit, string why)
        {
            fit.Valid = false;
            fit.Why = why;
            return fit;
        }
    }
}
