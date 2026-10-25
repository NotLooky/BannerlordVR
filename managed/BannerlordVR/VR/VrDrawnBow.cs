using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 269d-h. The bow while manual archery owns it: a bow of our own with
    /// the model's baked string taken out, and a string of ours from each limb
    /// tip - straight at rest, pulled to the fingers while an arrow is nocked,
    /// when the whole bow also turns about the grip onto the arrow.
    ///
    /// WHY THE BOW IS OURS
    ///
    /// The engine bends a string itself (sub_180683520 stores max(d - 0.19, 0) *
    /// 1.92 on its native weapon data; TEST 269c) and nothing managed reaches it.
    /// The model ('shortbow_d' + '.lod2') has the string baked in straight, and
    /// every way of hiding or bending it from outside failed: Mesh.MorphTime
    /// (269c), MetaMesh.GetMorphedCopy at any time (269f/g - the bow has no
    /// morph), Mesh.SetCustomClipPlane in world and in mesh space (269d/e).
    ///
    /// TEST 269h: so the string is taken out of the GEOMETRY, on a mesh of our
    /// own. ManagedMeshEditOperations.AddMesh copies the model's LOD0 into a
    /// fresh mesh (the resource, and every NPC's bow, are never touched), and
    /// the vertices within a few millimetres of the string line - the back of
    /// the bow, mid-thickness - are collapsed onto that line. A tube of zero
    /// width has no area and draws nothing; a limb vertex that happens to sit
    /// that close moves by those same few millimetres. Nothing managed says how
    /// many vertices a mesh has, and GetPositionOfVertex does not check its
    /// index (sub_1804CCB10 indexes [[ops+0x18]+0x18] raw) - so the count is
    /// read where the engine's own AddVertex keeps it, [[ops+0x18]+0x10], and
    /// cross-checked against the capacity beside it before a vertex is read.
    ///
    /// THE BOW, MEASURED
    ///
    /// In its own mesh space (269e - measured after the saved pose, the box of a
    /// tilted bow read 25 cm thick and aimed it 17 degrees off): longest axis the
    /// limbs, thinnest the thickness, the one between grip to string; the string
    /// is the end of that axis farther from the grip (bow_string_flip reverses).
    /// The string's own vertices then give where it really ends, and the cords
    /// start there.
    /// </summary>
    internal static class VrDrawnBow
    {
        private static readonly bool Enabled = VrConfig.Bool("bow_draw_visual", true);
        private static readonly bool StringFlip = VrConfig.Bool("bow_string_flip", false);

        /// <summary>How close to the string line (metres) a vertex must be to
        /// count as string: behind the back of the bow by at most this, and off
        /// its mid-thickness by at most half of it. The string is ~3 mm across.</summary>
        private static readonly float StringBand = VrConfig.Float("bow_string_band_m", 0.006f);

        /// <summary>TEST 269i: how far in front of the box's string edge (metres)
        /// a vertex may still be string - a tilted string leaves it at one end.</summary>
        private static readonly float StringSearch = VrConfig.Float("bow_string_search_m", 0.05f);

        /// <summary>TEST 269j: the string off its straight line (over the tips) is
        /// grown from it - a vertex this close (metres) to one already taken...</summary>
        private static readonly float StringGrow = VrConfig.Float("bow_string_grow_m", 0.006f);

        /// <summary>...and at most this far (metres) from the line.</summary>
        private static readonly float StringReach = VrConfig.Float("bow_string_reach_m", 0.02f);

        /// <summary>TEST 269k: string rings left off the line - searched this far
        /// from it, linked at RingLink, at most RingSize across (metres).</summary>
        private static readonly float RingReach = VrConfig.Float("bow_string_ring_reach_m", 0.035f);
        private static readonly float RingLink = VrConfig.Float("bow_string_ring_link_m", 0.005f);
        private static readonly float RingSize = VrConfig.Float("bow_string_ring_size_m", 0.01f);

        /// <summary>The arrow model thinned to a string: its ~9 mm shaft to ~3 mm.</summary>
        private const float CordThin = 0.3f;

        private static string _cordMesh;
        private static MatrixFrame _arrowLocal = MatrixFrame.Identity;
        private static float _arrowLength = 1f;
        private static MatrixFrame _cordLocal = MatrixFrame.Identity;
        private static float _cordLength = 1f;
        private static float _cordThin = CordThin;
        private static bool _stringCord;    // the cords are the bow's own string, not the arrow
        private static TaleWorlds.MountAndBlade.Mission _cordBuilt;
        private static readonly MetaMesh[] _cordMeta = new MetaMesh[2];

        private static GameEntity _bow;
        private static readonly GameEntity[] _cord = new GameEntity[2];
        /// <summary>TEST 308: the mission our entities were made in - not the
        /// scene pointer, which an arena reload can hand to a new scene.</summary>
        private static TaleWorlds.MountAndBlade.Mission _mission;
        private static MetaMesh _copy;
        private static int _stamp = int.MinValue;

        // The bow in its entity's own space, measured once per stamp.
        private static bool _measured;
        private static Vec3 _fwd;
        private static Vec3 _limbs;
        private static Vec3 _tipA;
        private static Vec3 _tipB;
        private static Vec3 _stringMid;

        private static bool _shown;
        private static bool _hidSkinned;
        private static bool _broken;

        /// <summary>TEST 373: what is measured and drawn is a crossbow - its
        /// stock along its own z, the prod across it, the string between the prod
        /// tips (VrCrossbow). Set by the caller's entry point.</summary>
        private static bool _crossbow;
        private static string _probedMorph;

        /// <summary>TEST 375: the weapon's box in its own mesh space - the item's
        /// space, for the crossbow's grippable stock (VrCrossbow).</summary>
        private static Vec3 _boxMin, _boxMax;

        /// <summary>
        /// The bow for this tick. Nocked, it is turned about <paramref name="pivot"/>
        /// (the bow hand) so it points from <paramref name="nockAt"/> through
        /// <paramref name="rest"/>, and the string runs to the nock; otherwise it
        /// sits where VrSkinnedWeapon puts it and the string is straight.
        /// </summary>
        internal static void Show(Scene scene, bool nocked, Vec3 pivot, Vec3 rest, Vec3 nockAt)
        {
            if (!Begin(scene, false, out MatrixFrame b0))
                return;

            try
            {
                MatrixFrame bow = b0;
                if (nocked && !Turn(b0, _fwd, _limbs, (rest - nockAt).NormalizedCopy(), pivot, out bow))
                    return;     // the arrow along the limbs has no bow plane; keep the last frame

                Place(bow, nocked ? nockAt : (Vec3?)null);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>TEST 373: the crossbow at <paramref name="frame"/> (the entity
        /// frame VrSkinnedWeapon's copy would have), its string pulled to
        /// <paramref name="pull"/> - null is the string at rest.</summary>
        internal static void ShowAt(Scene scene, MatrixFrame frame, Vec3? pull)
        {
            if (!Begin(scene, true, out _))
                return;

            try
            {
                Place(frame, pull);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>TEST 373: the crossbow string as measured, in the entity's
        /// space - the prod tips and the string's middle at rest.</summary>
        internal static bool TryString(out Vec3 tipA, out Vec3 tipB, out Vec3 rest)
        {
            tipA = _tipA;
            tipB = _tipB;
            rest = _stringMid;
            return _crossbow && _measured && !_broken;
        }

        /// <summary>TEST 375: the crossbow's box in its own (the item's) space.</summary>
        internal static bool TryBox(out Vec3 min, out Vec3 max)
        {
            min = _boxMin;
            max = _boxMax;
            return _crossbow && _measured && !_broken;
        }

        /// <summary>The held weapon copied, measured and ready, or false (then
        /// the engine's copy stays shown).</summary>
        private static bool Begin(Scene scene, bool crossbow, out MatrixFrame b0)
        {
            b0 = MatrixFrame.Identity;
            if (!Enabled || _broken || scene == null)
                return false;

            try
            {
                if (!VrSkinnedWeapon.TryGetVisual(HandRole.Main, out b0,
                                                  out List<MetaMesh> meshes, out int stamp, out string itemId))
                {
                    Decline(TaleWorlds.MountAndBlade.Mission.Current?.MainAgent?.WieldedWeapon.Item?.StringId,
                            "VrSkinnedWeapon draws no copy of it, so the engine's own bow shows");
                    Hide();
                    return false;
                }

                if (crossbow != _crossbow)
                {
                    _crossbow = crossbow;
                    _stamp = int.MinValue;     // measured the other way: measure again
                }

                Build(scene, meshes, stamp, itemId);

                // Never a bow with no string: until the arrow model the string is
                // made of has loaded, the bow stays the engine's, string and all.
                if (!_measured || _cordMeta[0] == null || _cordMeta[1] == null)
                {
                    Decline(itemId, !_measured ? "it could not be measured (no bounding box or no valid mesh)"
                                               : "no string to draw yet - the arrow model is not loaded (no quiver?)");
                    Hide();
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return false;
            }
        }

        private static void Place(MatrixFrame bow, Vec3? pull)
        {
            _bow.SetGlobalFrame(in bow, true);

            Vec3 mid = bow.TransformToParent(_stringMid);
            Vec3 at = pull ?? mid;
            Probe(bow, pull, mid);

            // TEST 565b: a recurve's string comes off its curled tips as it is drawn.
            float k = _haveNocks && !_crossbow ? Math.Min(1f, (at - mid).Length / RecurveFullM) : 0f;
            Cord(0, bow.TransformToParent(_tipA + (_nockA - _tipA) * k), at);
            Cord(1, bow.TransformToParent(_tipB + (_nockB - _tipB) * k), at);

            if (!_shown)
            {
                _shown = true;
                _bow.SetVisibilityExcludeParents(true);
                foreach (GameEntity cord in _cord)
                    cord?.SetVisibilityExcludeParents(true);

                VrSkinnedWeapon.SetShown(HandRole.Main, false);
                _hidSkinned = true;
            }
        }

        /// <summary>TEST 568: what is really drawn while a bow is out - "still a
        /// straight vanilla string, no bend" with every 269/565 line saying the
        /// string is out and the arrow nocked. Every 2 s: ours and VrSkinnedWeapon's
        /// visibility, where ours sits against VrSkinnedWeapon's, the cords'
        /// meshes and span, and whether a pull was passed in.</summary>
        private static void Probe(MatrixFrame bow, Vec3? pull, Vec3 mid)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!ProbeOn || now < _probeAt)
                return;

            _probeAt = now + 2 * System.Diagnostics.Stopwatch.Frequency;
            VrSkinnedWeapon.TryGetVisual(HandRole.Main, out MatrixFrame skinned, out _, out _, out string itemId);
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < _cord.Length; i++)
            {
                MatrixFrame f = _cord[i] != null ? _cord[i].GetGlobalFrame() : MatrixFrame.Identity;
                text.Append(string.Format(CultureInfo.InvariantCulture, " cord{0} visible {1}, {2} mesh(es), at {3:F2} m from the bow, stretch {4:F2};",
                    i, _cord[i] != null && _cord[i].IsVisibleIncludeParents(), _cordMeta[i]?.MeshCount ?? -1,
                    (f.origin - bow.origin).Length, f.rotation.f.Length));
            }

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 568 bow probe '{0}' ({1}): ours visible {2}, {3:F2} m from VrSkinnedWeapon's, whose own is visible {4}; "
                + "copy {5} mesh(es); pull {6} ({7:F2} m from the string's rest); string cords {8}, recurve nocks {9};{10}",
                itemId, _crossbow ? "crossbow" : "bow", _bow.IsVisibleIncludeParents(), (bow.origin - skinned.origin).Length,
                VrSkinnedWeapon.IsShown(HandRole.Main), _copy?.MeshCount ?? -1, pull.HasValue ? "yes" : "NO",
                pull.HasValue ? (pull.Value - mid).Length : 0f, _stringCord, _haveNocks, text));
        }

        private static readonly bool ProbeOn = VrConfig.Bool("bow_probe", true);
        private static long _probeAt;

        /// <summary>Back to VrSkinnedWeapon's bow - the bow put away, a menu, the
        /// mission over.</summary>
        internal static void Hide()
        {
            if (!_shown)
                return;

            _shown = false;

            try
            {
                // Only on the live scene: a native call on an entity whose scene
                // is gone faults rather than throws.
                TaleWorlds.MountAndBlade.Mission mission = TaleWorlds.MountAndBlade.Mission.Current;
                if (mission != null && mission == _mission)
                {
                    _bow?.SetVisibilityExcludeParents(false);
                    foreach (GameEntity cord in _cord)
                        cord?.SetVisibilityExcludeParents(false);
                }

                if (_hidSkinned)
                {
                    _hidSkinned = false;
                    VrSkinnedWeapon.SetShown(HandRole.Main, true);
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>The mission changed: everything died with its scene. Drops
        /// the references without touching them.</summary>
        internal static void Forget()
        {
            _bow = null;
            _cord[0] = null;
            _cord[1] = null;
            _cordMeta[0] = null;
            _cordMeta[1] = null;
            _cordBuilt = null;
            _stringCord = false;
            _mission = null;
            _copy = null;
            _stamp = int.MinValue;
            _measured = false;
            _shown = false;
            _hidSkinned = false;
        }

        private static void Build(Scene scene, List<MetaMesh> meshes, int stamp, string itemId)
        {
            if (_bow == null || _mission != TaleWorlds.MountAndBlade.Mission.Current)
            {
                _bow = GameEntity.CreateEmptyDynamic(scene, false);
                _bow.SetVisibilityExcludeParents(false);

                for (int i = 0; i < _cord.Length; i++)
                {
                    _cord[i] = GameEntity.CreateEmptyDynamic(scene, false);
                    _cord[i].SetVisibilityExcludeParents(false);
                    _cordMeta[i] = null;
                }

                _mission = TaleWorlds.MountAndBlade.Mission.Current;
                _cordBuilt = null;
                _copy = null;
                _stamp = int.MinValue;
            }

            BuildCords(scene);

            // VrSkinnedWeapon bumps the stamp on every attach and pose edit.
            if (stamp == _stamp)
                return;

            _stamp = stamp;

            if (_copy != null)
                _bow.RemoveMultiMesh(_copy);
            _copy = null;

            MetaMesh source = null;
            foreach (MetaMesh meta in meshes)
            {
                if (meta != null && meta.IsValid)
                {
                    source = meta;
                    break;
                }
            }

            _measured = false;
            if (source == null)
                return;

            _haveNocks = false;
            _measured = Measure(source, itemId, out int depth, out int limbs, out int thin,
                                out float stringEdge, out Vec3 centre);
            if (!_measured)
                return;

            MatrixFrame frame = source.Frame;
            bool wasString = _stringCord;
            _stringCord = false;
            _copy = Stringless(source, itemId, depth, limbs, thin, stringEdge, centre, frame)
                    ?? source.CreateCopy();
            if (wasString && !_stringCord)
                _cordBuilt = null;     // back to the arrow cords on the next Build

            if (_copy != null)
            {
                _copy.Frame = frame;
                VrSkinnedWeapon.Opaque(_copy);
                _bow.AddMultiMesh(_copy, true);
            }
        }

        /// <summary>
        /// The bow read in its OWN mesh space. Fills the entity-space axes and a
        /// first guess at the string's ends (the box's corners at the string
        /// depth); Stringless replaces them with the string's real ends.
        /// </summary>
        private static bool Measure(MetaMesh source, string itemId, out int depth, out int limbs, out int thin,
                                    out float stringEdge, out Vec3 centre)
        {
            depth = limbs = thin = 0;
            stringEdge = 0f;
            centre = Vec3.Zero;

            if (!Box(source, out Vec3 min, out Vec3 max))
            {
                VrLog.Warn("TEST 269e: '" + itemId + "' has no bounding box; the bow is left as it is held.");
                return false;
            }

            Vec3 size = max - min;
            _boxMin = min;
            _boxMax = max;
            if (_crossbow)
            {
                // TEST 373: an item's length runs along its own z (the bolt's
                // AmmoOffset, the centre of mass), so the stock is z whatever is
                // longest; the prod is the wider of the other two. The string's
                // depth comes from the prod tips' vertices in Stringless; until
                // then it is guessed at the front.
                depth = 2;
                limbs = VrBlade.Comp(size, 0) >= VrBlade.Comp(size, 1) ? 0 : 1;
                thin = 1 - limbs;
                ProbeMorph(source, itemId);
            }
            else
            {
                if (VrBlade.Comp(size, 1) > VrBlade.Comp(size, limbs)) limbs = 1;
                if (VrBlade.Comp(size, 2) > VrBlade.Comp(size, limbs)) limbs = 2;
                thin = limbs == 0 ? 1 : 0;
                for (int a = 0; a < 3; a++)
                    if (a != limbs && VrBlade.Comp(size, a) < VrBlade.Comp(size, thin))
                        thin = a;
                depth = 3 - limbs - thin;
            }

            float lo = VrBlade.Comp(min, depth);
            float hi = VrBlade.Comp(max, depth);
            bool stringAtMax = _crossbow || Math.Abs(hi) >= Math.Abs(lo);
            if (StringFlip)
                stringAtMax = !stringAtMax;

            stringEdge = stringAtMax ? hi : lo;
            centre = (min + max) * 0.5f;

            Vec3 stringMid = VrBlade.SetComp(centre, depth, stringEdge);
            MatrixFrame frame = source.Frame;
            _fwd = frame.rotation.TransformToParent(VrBlade.Unit(depth) * (stringAtMax ? -1f : 1f));
            _limbs = frame.rotation.TransformToParent(VrBlade.Unit(limbs));
            _stringMid = frame.TransformToParent(stringMid);
            _tipA = frame.TransformToParent(VrBlade.SetComp(stringMid, limbs, VrBlade.Comp(min, limbs)));
            _tipB = frame.TransformToParent(VrBlade.SetComp(stringMid, limbs, VrBlade.Comp(max, limbs)));

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                (_crossbow ? "TEST 373 crossbow" : "TEST 269e bow")
                + " '{0}' measures ({1:F3}, {2:F3}, {3:F3}) m in its own mesh space: limbs "
                + "along {4}, {5:F2} m; thickness along {6}, {7:F3} m; string at {8} {9:F3} (the end {10} "
                + "the grip), {11:F2} m from the front.",
                itemId, size.x, size.y, size.z, "XYZ"[limbs], VrBlade.Comp(size, limbs),
                "XYZ"[thin], VrBlade.Comp(size, thin), "XYZ"[depth], stringEdge,
                StringFlip ? "NEARER, flipped by bow_string_flip," : "farther from",
                VrBlade.Comp(size, depth)));

            return true;
        }

        /// <summary>
        /// TEST 269h: the bow's LOD0 as a mesh of our own, its baked string
        /// collapsed onto its own axis. Null when anything about it is not as
        /// measured - the caller then shows a plain copy, string and all.
        /// </summary>
        private static MetaMesh Stringless(MetaMesh source, string itemId, int depth, int limbs, int thin,
                                           float stringEdge, Vec3 centre, MatrixFrame frame)
        {
            Mesh original = source.MeshCount > 0 ? source.GetMeshAtIndex(0) : null;
            Material material = original?.GetMaterial();
            if (original == null || material == null)
                return null;

            Mesh mesh = Mesh.CreateMeshWithMaterial(material);
            ManagedMeshEditOperations ops = ManagedMeshEditOperations.Create(mesh);
            ops.AddMesh(original, MatrixFrame.Identity);

            int count = VertexCount(ops);
            if (count <= 0)
            {
                VrLog.Warn("TEST 269h: '" + itemId + "' copied into " + count + " vertices - not as expected; "
                           + "the bow keeps its own straight string.");
                return null;
            }

            // TEST 269i: the string is found as a LINE, not as the box's back
            // face. 269h asked for vertices on the box's depth edge AND its
            // mid-thickness, and on 'composite_steppe_bow' only 3 of 486 were:
            // an asymmetric bow's string is tilted in depth and off the middle.
            // Prefer aligned string rings through the open middle. Models without
            // those rings retain the endpoint-line search within StringBand.
            var pos = new Vec3[count];
            for (int v = 0; v < count; v++)
                pos[v] = ops.GetPositionOfVertex(v);

            float limbLo = float.MaxValue, limbHi = float.MinValue;
            for (int v = 0; v < count; v++)
            {
                float l = VrBlade.Comp(pos[v], limbs);
                limbLo = Math.Min(limbLo, l);
                limbHi = Math.Max(limbHi, l);
            }

            float span = limbHi - limbLo;

            // TEST 373: a crossbow's string at rest runs between the prod tips,
            // which are the outermost vertices across it - the string sits at
            // their depth along the stock, not at the end of the box.
            if (_crossbow)
            {
                Vec3 sumA = Vec3.Zero, sumB = Vec3.Zero;
                int na = 0, nb = 0;
                for (int v = 0; v < count; v++)
                {
                    float l = VrBlade.Comp(pos[v], limbs);
                    if (l <= limbLo + 0.03f * span) { sumA += pos[v]; na++; }
                    else if (l >= limbHi - 0.03f * span) { sumB += pos[v]; nb++; }
                }

                if (na > 0 && nb > 0)
                {
                    Vec3 a = sumA * (1f / na), b = sumB * (1f / nb);
                    float tipDepth = (VrBlade.Comp(a, depth) + VrBlade.Comp(b, depth)) * 0.5f;
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 373 crossbow '{0}': prod tips {1}+{2} vertices at stock depth {3:F3} (the box guess was "
                        + "{4:F3}); the string is searched there.", itemId, na, nb, tipDepth, stringEdge));
                    stringEdge = tipDepth;

                    // Until (unless) the line is found, the cords start at the tips.
                    _tipA = frame.TransformToParent(a);
                    _tipB = frame.TransformToParent(b);
                    _stringMid = frame.TransformToParent((a + b) * 0.5f);
                }
            }

            var near = new List<int>();
            for (int v = 0; v < count; v++)
            {
                if (Math.Abs(VrBlade.Comp(pos[v], depth) - stringEdge) <= StringSearch)
                    near.Add(v);
            }
            var endA = near.FindAll(v => VrBlade.Comp(pos[v], limbs) <= limbLo + 0.2f * span);
            var endB = near.FindAll(v => VrBlade.Comp(pos[v], limbs) >= limbHi - 0.2f * span);

            // ponytail: brute force, |A|*|B|*|near| - a few hundred thousand
            // distance checks per bow; fine at one bow per attach.
            Vec3 lineAt = Vec3.Zero, lineDir = Vec3.Zero;
            List<int> picked = _crossbow ? null
                : FindStringRings(pos, near, limbs, limbLo, span, out lineAt, out lineDir);
            bool ringFit = picked != null;
            foreach (int a in endA)
            {
                if (ringFit)
                    break;
                foreach (int b in endB)
                {
                    Vec3 dir = pos[b] - pos[a];
                    if (dir.Normalize() < 0.5f * span)
                        continue;

                    var on = new List<int>();
                    foreach (int c in near)
                    {
                        Vec3 d = pos[c] - pos[a];
                        if ((d - dir * Vec3.DotProduct(d, dir)).Length <= StringBand)
                            on.Add(c);
                    }

                    if (picked == null || on.Count > picked.Count)
                    {
                        picked = on;
                        lineAt = pos[a];
                        lineDir = dir;
                    }
                }
            }

            if (picked == null || picked.Count < 4)
            {
                VrLog.Warn("TEST 269i: '" + itemId + "' has no straight string line (best " + (picked?.Count ?? 0)
                           + " vertices of " + count + ", " + near.Count + " on the string side, ends "
                           + endA.Count + "/" + endB.Count + "); the bow keeps its own straight string. "
                           + "bow_string_band_m / bow_string_search_m widen the search.");
                return null;
            }

            float tLo = float.MaxValue, tHi = float.MinValue;
            foreach (int v in picked)
            {
                float t = Vec3.DotProduct(pos[v] - lineAt, lineDir);
                tLo = Math.Min(tLo, t);
                tHi = Math.Max(tHi, t);
            }

            // TEST 269j: 269i left a little string - the parts off the straight
            // line (where it bends over the tips). Grown from the line: a vertex
            // within StringGrow of one already taken, and within StringReach of
            // the line, is string too.
            var taken = new HashSet<int>(picked);
            int straight = picked.Count;
            for (bool more = !ringFit; more;)
            {
                more = false;
                foreach (int c in near)
                {
                    if (taken.Contains(c) || OffLine(pos[c], lineAt, lineDir) > StringReach)
                        continue;

                    foreach (int p in picked)
                    {
                        if ((pos[c] - pos[p]).Length <= StringGrow)
                        {
                            taken.Add(c);
                            picked.Add(c);
                            more = true;
                            break;
                        }
                    }
                }
            }

            // TEST 269k: 269j still left string on the upper end - tight rings of
            // 4 vertices ~1 cm off the line, 2 cm apart (the string bending to the
            // nock), too far from the line's own vertices to grow to. A ring is a
            // cluster of close vertices a few mm across; each collapses to its
            // centre, so every face between two rings has no area.
            var loose = near.FindAll(c => !ringFit && !taken.Contains(c)
                && OffLine(pos[c], lineAt, lineDir) <= RingReach);
            var rings = StringRings(pos, loose);

            var ringLog = new System.Text.StringBuilder();
            foreach (List<int> ring in rings)
            {
                Vec3 middle = Vec3.Zero;
                foreach (int v in ring)
                    middle += pos[v];
                middle *= 1f / ring.Count;

                foreach (int v in ring)
                {
                    ops.SetPositionOfVertex(v, middle);
                    taken.Add(v);
                }

                ringLog.Append(string.Format(CultureInfo.InvariantCulture, " {0}@{1:F3}/{2:F3}", ring.Count,
                                             Vec3.DotProduct(middle - lineAt, lineDir), OffLine(middle, lineAt, lineDir)));
            }

            // What is still close and was left, so the next test can see it.
            var left = new System.Text.StringBuilder();
            foreach (int c in near)
            {
                float off = OffLine(pos[c], lineAt, lineDir);
                if (!taken.Contains(c) && off < 0.03f)
                    left.Append(string.Format(CultureInfo.InvariantCulture, " {0:F3}/{1:F3}",
                                              Vec3.DotProduct(pos[c] - lineAt, lineDir), off));
            }

            // TEST 565: a string line well short of the limbs (noble_bow: 1.08 of
            // 1.52 m) is a recurve's - the rest of its string lies on the curled
            // tips, 1-3 cm off the line, and is left baked in. Its vertices are
            // dumped once (along, off, taken) so the cut can be fitted offline.
            if (!_crossbow && Probe565 && span > 0f)
            {
                float lineLo = float.MaxValue, lineHi = float.MinValue;
                foreach (int v in picked)
                {
                    float t = Vec3.DotProduct(pos[v] - lineAt, lineDir);
                    lineLo = Math.Min(lineLo, t);
                    lineHi = Math.Max(lineHi, t);
                }

                if (lineHi - lineLo < 0.9f * span)
                    DumpBow(itemId, pos, taken, lineAt, lineDir, depth, limbs);
            }

            // A recurve's string comes off its curled tips as it is drawn.
            _haveNocks = false;
            if (!_crossbow && span > 0f)
            {
                Vec3 lo = Vec3.Zero, hi = Vec3.Zero;
                int nlo = 0, nhi = 0;
                for (int v = 0; v < count; v++)
                {
                    float l = VrBlade.Comp(pos[v], limbs);
                    if (l <= limbLo + 0.012f) { lo += pos[v]; nlo++; }
                    else if (l >= limbHi - 0.012f) { hi += pos[v]; nhi++; }
                }

                if (nlo > 0 && nhi > 0)
                {
                    lo *= 1f / nlo;
                    hi *= 1f / nhi;
                    _nockLo = lo;
                    _nockHi = hi;
                    _haveNocks = true;
                }
            }

            // Build independent string geometry with the bow's material.
            BuildStringCord(material, pos, picked.GetRange(0, straight), itemId);

            // Onto the line - a tube of zero width draws nothing.
            foreach (int v in picked)
                ops.SetPositionOfVertex(v, lineAt + lineDir * Vec3.DotProduct(pos[v] - lineAt, lineDir));

            ops.FinalizeEditing();
            mesh.RecomputeBoundingBox();

            VrLog.Info("TEST 269j bow '" + itemId + "': " + straight + " string vertices on the line, "
                       + (picked.Count - straight) + " more grown off it; "
                       + (ringFit ? "matched string cross-sections; " : "endpoint-line fit; ")
                       + (_stringCord ? "our string is a measured tube with the bow's material."
                                       : "our string stays the arrow model.")
                       + " TEST 269k rings collapsed (count@along/off):" + (ringLog.Length > 0 ? ringLog.ToString() : " none")
                       + ". Left near the line (along/off, m):" + (left.Length > 0 ? left.ToString() : " none"));

            // The cords start where the baked string really ended.
            _tipA = frame.TransformToParent(lineAt + lineDir * tLo);
            _tipB = frame.TransformToParent(lineAt + lineDir * tHi);

            // TEST 565b: only a recurve's line stops short of its tips.
            _haveNocks &= tHi - tLo < 0.9f * span;
            if (_haveNocks)
            {
                Vec3 lineA = lineAt + lineDir * tLo, lineB = lineAt + lineDir * tHi;
                bool loIsA = (_nockLo - lineA).LengthSquared + (_nockHi - lineB).LengthSquared
                             <= (_nockLo - lineB).LengthSquared + (_nockHi - lineA).LengthSquared;
                _nockA = frame.TransformToParent(loIsA ? _nockLo : _nockHi);
                _nockB = frame.TransformToParent(loIsA ? _nockHi : _nockLo);
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 565b bow '{0}': a recurve - its string meets the limbs {1:F2}/{2:F2} m in from the tip nocks; "
                    + "drawn, the string's ends slide out to them (full at bow_recurve_full_m {3:F2} of pull).",
                    itemId, (_nockA - _tipA).Length, (_nockB - _tipB).Length, RecurveFullM));
            }
            _stringMid = frame.TransformToParent(lineAt + lineDir * ((tLo + tHi) * 0.5f));

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 269i bow '{0}': the baked string is out - {1} of {2} vertices ({3} on the string side) "
                + "collapsed onto its line, {4:F3} m long of {5:F3} m limbs. Our string starts there.",
                itemId, picked.Count, count, near.Count, tHi - tLo, span));

            MetaMesh meta = MetaMesh.CreateMetaMesh(null);
            meta.AddMesh(mesh);
            return meta;
        }

        private static readonly bool Probe565 = VrConfig.Bool("bow_recurve_dump", true);

        /// <summary>TEST 565b: a recurve's tip nocks (entity space) and the pull
        /// (metres from the string's rest) at which the string has left the curled
        /// tips entirely.</summary>
        private static readonly float RecurveFullM = Math.Max(0.01f, VrConfig.Float("bow_recurve_full_m", 0.30f));
        private static bool _haveNocks;
        private static Vec3 _nockLo, _nockHi, _nockA, _nockB;

        /// <summary>TEST 565: every vertex of a recurve's LOD0 in its mesh space,
        /// beside the cfg (bvr_bow_[item].csv): x,y,z, along/off the string line,
        /// and whether it was taken as string - once per item.</summary>
        private static void DumpBow(string itemId, Vec3[] pos, HashSet<int> taken, Vec3 lineAt, Vec3 lineDir,
                                    int depth, int limbs)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(VrConfig.Path ?? string.Empty);
                if (string.IsNullOrEmpty(dir))
                    return;

                string file = System.IO.Path.Combine(dir, "bvr_bow_" + itemId + ".csv");
                if (System.IO.File.Exists(file))
                    return;

                var text = new System.Text.StringBuilder("# depth axis " + "XYZ"[depth] + ", limbs axis " + "XYZ"[limbs]
                                                         + "\nx,y,z,along,off,taken\n");
                for (int v = 0; v < pos.Length; v++)
                {
                    text.Append(string.Format(CultureInfo.InvariantCulture, "{0:F5},{1:F5},{2:F5},{3:F5},{4:F5},{5}\n",
                        pos[v].x, pos[v].y, pos[v].z, Vec3.DotProduct(pos[v] - lineAt, lineDir),
                        OffLine(pos[v], lineAt, lineDir), taken.Contains(v) ? 1 : 0));
                }

                System.IO.File.WriteAllText(file, text.ToString());
                VrLog.Info("TEST 565 bow '" + itemId + "': its string line is short of the limbs (a recurve) - wrote "
                           + file + " for the offline fit.");
            }
            catch (Exception ex)
            {
                VrLog.Info("TEST 565: no vertex dump for '" + itemId + "' (" + ex.Message + ").");
            }
        }

        private static float OffLine(Vec3 p, Vec3 at, Vec3 dir)
        {
            Vec3 d = p - at;
            return (d - dir * Vec3.DotProduct(d, dir)).Length;
        }

        private static List<List<int>> StringRings(Vec3[] pos, List<int> vertices)
        {
            var rings = new List<List<int>>();
            var seen = new HashSet<int>();
            foreach (int c in vertices)
            {
                if (!seen.Add(c))
                    continue;
                var ring = new List<int> { c };
                for (int k = 0; k < ring.Count; k++)
                    foreach (int d in vertices)
                        if (!seen.Contains(d) && (pos[d] - pos[ring[k]]).Length <= RingLink)
                        {
                            seen.Add(d);
                            ring.Add(d);
                        }
                if (IsRing(ring, pos))
                    rings.Add(ring);
            }
            return rings;
        }

        // Dense tip wood must not outvote a sparse string: require aligned tube
        // cross-sections at both ends AND through the bow's open middle.
        private static List<int> FindStringRings(Vec3[] pos, List<int> near, int limbs,
            float limbLo, float span, out Vec3 lineAt, out Vec3 lineDir)
        {
            lineAt = lineDir = Vec3.Zero;
            var rings = StringRings(pos, near);
            var centres = new Vec3[rings.Count];
            for (int i = 0; i < rings.Count; i++)
            {
                foreach (int v in rings[i])
                    centres[i] += pos[v];
                centres[i] *= 1f / rings[i].Count;
            }
            List<int> picked = null;
            int best = 0;
            for (int a = 0; a < rings.Count; a++)
            {
                if (VrBlade.Comp(centres[a], limbs) > limbLo + 0.2f * span)
                    continue;
                for (int b = 0; b < rings.Count; b++)
                {
                    if (VrBlade.Comp(centres[b], limbs) < limbLo + 0.8f * span)
                        continue;
                    Vec3 dir = centres[b] - centres[a];
                    if (dir.Normalize() < 0.5f * span)
                        continue;
                    var on = new List<int>();
                    bool middle = false;
                    for (int c = 0; c < rings.Count; c++)
                    {
                        if (OffLine(centres[c], centres[a], dir) > 0.001f)
                            continue;
                        on.Add(c);
                        float along = VrBlade.Comp(centres[c], limbs) - limbLo;
                        middle |= along > 0.25f * span && along < 0.75f * span;
                    }
                    if (!middle || on.Count <= best)
                        continue;
                    best = on.Count;
                    picked = new List<int>();
                    foreach (int c in on)
                        picked.AddRange(rings[c]);
                    lineAt = centres[a];
                    lineDir = dir;
                }
            }
            return picked;
        }

        /// <summary>A string ring: at least 4 distinct points, 1 mm to RingSize
        /// across. Wood is sparser than that; seams (duplicates) are one point.</summary>
        private static bool IsRing(List<int> ring, Vec3[] pos)
        {
            var distinct = new List<Vec3>();
            float extent = 0f;
            foreach (int v in ring)
            {
                bool dup = false;
                foreach (Vec3 q in distinct)
                {
                    float d = (pos[v] - q).Length;
                    extent = Math.Max(extent, d);
                    dup |= d < 0.0005f;
                }

                if (!dup)
                    distinct.Add(pos[v]);
            }

            return distinct.Count >= 4 && extent >= 0.001f && extent <= RingSize;
        }

        /// <summary>Use the measured string radius and bow material on a clean tube;
        /// copied bow faces include limb connections that are not string geometry.</summary>
        private static void BuildStringCord(Material material, Vec3[] pos, List<int> tube, string itemId)
        {
            // The axis: the centres of the tube's two end quarters.
            Vec3 at = pos[tube[0]], dir = pos[tube[tube.Count - 1]] - at;
            if (dir.Normalize() < 0.1f)
            {
                at = pos[tube[0]];
                dir = pos[tube[1]] - at;
                dir.Normalize();
            }

            float lo = float.MaxValue, hi = float.MinValue;
            foreach (int v in tube)
            {
                float t = Vec3.DotProduct(pos[v] - at, dir);
                lo = Math.Min(lo, t);
                hi = Math.Max(hi, t);
            }

            Vec3 endA = Vec3.Zero, endB = Vec3.Zero;
            int na = 0, nb = 0;
            foreach (int v in tube)
            {
                float t = Vec3.DotProduct(pos[v] - at, dir);
                if (t <= lo + 0.25f * (hi - lo)) { endA += pos[v]; na++; }
                else if (t >= hi - 0.25f * (hi - lo)) { endB += pos[v]; nb++; }
            }

            if (na == 0 || nb == 0)
                return;

            at = endA * (1f / na);
            dir = endB * (1f / nb) - at;
            if (dir.Normalize() < 0.1f)
                return;

            lo = float.MaxValue;
            hi = float.MinValue;
            var radii = new List<float>();
            foreach (int v in tube)
            {
                float t = Vec3.DotProduct(pos[v] - at, dir);
                lo = Math.Min(lo, t);
                hi = Math.Max(hi, t);
                radii.Add(OffLine(pos[v], at, dir));
            }

            radii.Sort();
            float radius = Math.Max(radii[radii.Count / 2], 0.001f);

            Mesh mesh = Mesh.CreateMeshWithMaterial(material);
            ManagedMeshEditOperations ops = ManagedMeshEditOperations.Create(mesh);
            // Unit length along Y: Cord applies only the live length and direction.
            const int sides = 8;
            Vec3 color = new Vec3(1f, 1f, 1f);
            for (int side = 0; side < sides; side++)
            {
                float a = side * (float)(2 * Math.PI / sides);
                float b = (side + 1) * (float)(2 * Math.PI / sides);
                Vec3 normalA = new Vec3((float)Math.Cos(a), 0f, (float)Math.Sin(a));
                Vec3 normalB = new Vec3((float)Math.Cos(b), 0f, (float)Math.Sin(b));
                Vec3 p0 = normalA * radius, p1 = normalB * radius;
                Vec3 p2 = p0 + new Vec3(0f, 1f, 0f), p3 = p1 + new Vec3(0f, 1f, 0f);
                Vec2 uv0 = new Vec2((float)side / sides, 0f), uv1 = new Vec2((float)(side + 1) / sides, 0f);
                Vec2 uv2 = new Vec2(uv0.x, 1f), uv3 = new Vec2(uv1.x, 1f);
                ops.AddTriangle(p0, p2, p3, normalA, normalA, normalB, uv0, uv2, uv3, color, color, color);
                ops.AddTriangle(p0, p3, p1, normalA, normalB, normalB, uv0, uv3, uv1, color, color, color);
            }
            ops.ComputeTangents(false);
            ops.FinalizeEditing();
            mesh.RecomputeBoundingBox();
            mesh.Color = 0xFFFFFFFFu;
            for (int i = 0; i < _cord.Length; i++)
            {
                if (_cordMeta[i] != null)
                    _cord[i].RemoveMultiMesh(_cordMeta[i]);
                _cordMeta[i] = MetaMesh.CreateMetaMesh(null);
                _cordMeta[i].AddMesh(i == 0 ? mesh : mesh.CreateCopy());
                VrSkinnedWeapon.Opaque(_cordMeta[i]);
                _cord[i].AddMultiMesh(_cordMeta[i], true);
            }
            _cordLocal = MatrixFrame.Identity;
            _cordLength = 1f;
            _cordThin = 1f;
            VrLog.Info("Bow '" + itemId + "': clean string tubes, radius "
                       + radius.ToString("F4", CultureInfo.InvariantCulture) + " m.");
            _stringCord = true;
        }

        /// <summary>
        /// The vertex count of an edit, where the engine's own AddVertex keeps it
        /// (sub_180273E40: count at [edit+0x10], capacity at [edit+0x14], the
        /// positions at [edit+0x18], edit = [ops+0x18]). -1 when the pair does not
        /// look like a count and its capacity, so nothing is ever read past the end.
        /// </summary>
        internal static int VertexCount(ManagedMeshEditOperations ops)
        {
            IntPtr pointer = new IntPtr((long)ops.Pointer.ToUInt64());
            if (pointer == IntPtr.Zero)
                return -1;

            IntPtr edit = Marshal.ReadIntPtr(pointer, 0x18);
            if (edit == IntPtr.Zero)
                return -1;

            int count = Marshal.ReadInt32(edit, 0x10);
            int capacity = Marshal.ReadInt32(edit, 0x14);
            return count > 0 && count <= capacity && capacity < 4000000 ? count : -1;
        }

        /// <summary>TEST 373 probe, once per crossbow: does its model carry a
        /// string morph (bows do not, 269f)? Stepped boxes along the stock say so.</summary>
        private static void ProbeMorph(MetaMesh source, string itemId)
        {
            string name = source.GetName();
            if (string.IsNullOrEmpty(name) || name == _probedMorph)
                return;

            _probedMorph = name;
            var text = new System.Text.StringBuilder();
            foreach (float t in new[] { 0f, 0.5f, 1f })
            {
                MetaMesh morphed = MetaMesh.GetMorphedCopy(name, t, false);
                if (morphed != null && Box(morphed, out Vec3 lo, out Vec3 hi))
                    text.Append(string.Format(CultureInfo.InvariantCulture, " t={0:F1}: z {1:F3}..{2:F3};", t, lo.z, hi.z));
                else
                    text.Append(string.Format(CultureInfo.InvariantCulture, " t={0:F1}: none;", t));
            }

            VrLog.Info("TEST 373 crossbow '" + itemId + "' mesh '" + name + "' morph probe:" + text
                       + " the same box at every t means no string morph.");
        }

        /// <summary>A metamesh's box in its own space (each mesh through its local
        /// frame, not through the metamesh's).</summary>
        private static bool Box(MetaMesh meta, out Vec3 min, out Vec3 max)
        {
            bool any = false;
            min = Vec3.Zero;
            max = Vec3.Zero;

            int parts = meta.MeshCount;
            for (int p = 0; p < parts; p++)
            {
                Mesh mesh = meta.GetMeshAtIndex(p);
                if (mesh == null)
                    continue;

                MatrixFrame local = mesh.GetLocalFrame();
                if (local.rotation.s.LengthSquared < 1e-6f)
                    local = MatrixFrame.Identity;

                Vec3 boxMin = mesh.GetBoundingBoxMin();
                Vec3 boxMax = mesh.GetBoundingBoxMax();

                for (int corner = 0; corner < 8; corner++)
                {
                    Vec3 point = local.TransformToParent(new Vec3(
                        (corner & 1) == 0 ? boxMin.x : boxMax.x,
                        (corner & 2) == 0 ? boxMin.y : boxMax.y,
                        (corner & 4) == 0 ? boxMin.z : boxMax.z));

                    min = any ? Vec3.Vec3Min(min, point) : point;
                    max = any ? Vec3.Vec3Max(max, point) : point;
                    any = true;
                }
            }

            return any;
        }

        /// <summary>
        /// TEST 269g: the cord is the ARROW model, thinned. 269d-f's own tube in
        /// vertex_color_mat drew faint and white whatever colour it was given -
        /// that material is not a lit, opaque one. The arrow is: it is what the
        /// player already holds, drawn by the engine's own weapon material. Its
        /// nock sits on the limb tip (the fletching reads as the string loop)
        /// and its head at the pull point, where the hand covers it.
        /// </summary>
        internal static void SetCord(string meshName, MatrixFrame arrowLocal, float arrowLength)
        {
            if (meshName == _cordMesh || arrowLength < 0.1f)
                return;

            _cordMesh = meshName;
            _arrowLocal = arrowLocal;
            _arrowLength = arrowLength;
            _cordBuilt = null;     // rebuilt on the next Build
        }

        private static void BuildCords(Scene scene)
        {
            if (_stringCord || string.IsNullOrEmpty(_cordMesh)
                || _cordBuilt == TaleWorlds.MountAndBlade.Mission.Current)
                return;

            _cordBuilt = TaleWorlds.MountAndBlade.Mission.Current;
            _cordLocal = _arrowLocal;
            _cordLength = _arrowLength;
            _cordThin = CordThin;
            for (int i = 0; i < _cord.Length; i++)
            {
                if (_cordMeta[i] != null)
                    _cord[i].RemoveMultiMesh(_cordMeta[i]);

                _cordMeta[i] = MetaMesh.GetCopy(_cordMesh, false, true);
                if (_cordMeta[i] != null)
                    _cord[i].AddMultiMesh(_cordMeta[i], true);
            }
        }

        /// <summary>One cord from the limb tip <paramref name="a"/> to the pull
        /// point <paramref name="b"/>: the arrow model laid along it, scaled to
        /// its length and thinned to a string.</summary>
        private static void Cord(int i, Vec3 a, Vec3 b)
        {
            if (_cord[i] == null || _cordMeta[i] == null)
                return;

            if ((b - a).Length < 1e-3f)
                return;
            MatrixFrame frame = CordFrame(a, b, _cordLocal, _cordLength, _cordThin);
            _cord[i].SetGlobalFrame(in frame, true);
        }

        internal static MatrixFrame CordFrame(Vec3 a, Vec3 b, MatrixFrame local, float sourceLength, float thickness)
        {
            Vec3 dir = b - a;
            float length = dir.Normalize();
            MatrixFrame nock = MatrixFrame.Identity;
            nock.origin = a;
            nock.rotation.f = dir;
            if (Math.Abs(dir.z) > 0.95f)
                nock.rotation.u = new Vec3(1f, 0f, 0f);
            nock.rotation.Orthonormalize();
            nock.rotation.s *= thickness;
            nock.rotation.f *= length / sourceLength;
            nock.rotation.u *= thickness;
            return nock.TransformToParent(local.TransformToLocal(MatrixFrame.Identity));
        }

        /// <summary>The bow <paramref name="b0"/> turned about <paramref name="pivot"/>
        /// so its forward (<paramref name="fwdLocal"/>, in its own space) lies
        /// along <paramref name="d"/>, its limbs kept as near to where they were
        /// as that allows - the hand's cant survives, only the pointing changes.</summary>
        private static bool Turn(MatrixFrame b0, Vec3 fwdLocal, Vec3 limbsLocal, Vec3 d, Vec3 pivot,
                                 out MatrixFrame bow)
        {
            bow = b0;
            Vec3 fwd = b0.rotation.TransformToParent(fwdLocal).NormalizedCopy();
            Vec3 limbs = b0.rotation.TransformToParent(limbsLocal).NormalizedCopy();
            if (Math.Abs(Vec3.DotProduct(d, limbs)) > 0.98f)
                return false;

            Mat3 from = Basis(fwd, limbs);
            Mat3 to = Basis(d, limbs);
            bow = new MatrixFrame(
                to.TransformToParent(from.TransformToLocal(b0.rotation)),
                pivot + to.TransformToParent(from.TransformToLocal(b0.origin - pivot)));
            return true;
        }

        private static Mat3 Basis(Vec3 f, Vec3 u)
        {
            Mat3 m = Mat3.Identity;
            m.f = f;
            m.u = u;
            m.Orthonormalize();
            return m;
        }

        /// <summary>TEST 563: why the bow in hand keeps the engine's straight
        /// string - once per item and reason, so one run names it.</summary>
        private static void Decline(string itemId, string why)
        {
            string key = itemId + "|" + why;
            if (key == _declined)
                return;

            _declined = key;
            VrLog.Info("TEST 563 bow '" + (itemId ?? "?") + "': drawn with its own straight string, not ours - " + why + ".");
        }

        private static string _declined;

        private static void Fail(Exception ex)
        {
            if (_broken)
                return;

            _broken = true;
            VrLog.Error("TEST 269h: the drawn " + (_crossbow ? "crossbow" : "bow") + " failed; every bow and crossbow "
                        + "stays VrSkinnedWeapon's (straight string, not turned) for this session. Archery itself is "
                        + "unaffected.", ex);

            // Hide's own catch lands back here and returns: _broken is set.
            Hide();
        }
    }
}
