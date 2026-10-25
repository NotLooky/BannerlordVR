using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using TaleWorlds.DotNet;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 481-488 (finger_mesh = 1, the VR tab's "Five-finger hands"): five
    /// fingers on the player's OWN hand and glove meshes, whatever armour - the
    /// skeleton has no finger bones.
    ///
    /// TEST 480/480c measured the way: a copy of a hand mesh hung on the
    /// skeleton shows and follows the arm, its vertices sit in the hand's bind
    /// space, and editing them every frame costs 12.6 ms (FinalizeEditing on a
    /// drawn mesh) - while a fresh copy costs ~1.4 ms once and switching which
    /// copy shows costs nothing. So every pose is built once, as KEYFRAMES, per
    /// LOD0 hand mesh and per hand. One rig per hand is fitted to ALL that hand's
    /// pieces together (TEST 488: a layered gauntlet piece fails a fit of its
    /// own, and stayed open over a closing hand), then:
    ///  * BASE: palm, wrist and cuff; every vertex a digit moves is hidden;
    ///  * THUMB: finger_keys copies, relaxed to closed;
    ///  * FINGERS (481d): a finger_index_keys x finger_grid_keys grid - index curl
    ///    by middle/ring/pinky curl - all four fingers in ONE copy. A face whose
    ///    corners belong to two copies is drawn by neither (481c's knuckle
    ///    holes); one copy for the four fingers has no such face;
    ///  * HOLD (487): when a hand holds something, finger_hold_keys copies of
    ///    the four fingers (the index from relaxed to closed on the handle, the
    ///    rest closed on it) and as many of the thumb - so a finger lifting off
    ///    the handle or landing on it moves (TEST 482's touch) instead of popping.
    ///    Each digit curls until it TOUCHES the handle, taken from the STATIC
    ///    grip (VrSkinnedWeapon.TryHeldLocal / VrTwoHand.TryOffLine), never the
    ///    drawn frame (483b: 1.9-14 cm off from the glide, lag and turn).
    /// TEST 489, what a copy draws. Every copy is skinned like the game's mesh
    /// (AddSkinnedMeshWithColor, as the base): 481e's "tie to the hand bone"
    /// (AddMeshWithSkinData) proved nothing (the CPU skinning check returned bind
    /// positions), and the shard flying off the forearm says it never tied -
    /// every cuff vertex a copy hid on the wrist pivot followed the forearm and
    /// upper arm, so a bent or stretched arm flung that point away with faces
    /// fanned to it. A point skins to one spot only for vertices that follow the
    /// hand (finger0 is held at rest), so a copy hides ONLY digit vertices - each
    /// on its own root joint (TEST 490) - and keeps every palm and cuff vertex,
    /// of both hands, exactly where the base draws it (the two coincide). The
    /// joint from a bent
    /// digit to the palm is then always drawn whole: 481c's rest ring came from a
    /// digit's barely-weighted vertices, and the thumb had none - the slabs at
    /// its base. The copies build a few per frame (finger_build_ms); the game's
    /// hands stay until the base, thumb and grid are ready. finger0 is held at
    /// rest meanwhile (VrFingers).
    ///
    /// TEST 494, a smooth close: finger_grid_keys 12 (the thumb's keys span only
    /// the curls it is asked for, VrFingers.ThumbSpan), built every other key
    /// first - the hands go live on those - then the in-betweens; a hold's wrap
    /// and copies bake in jobs ahead of the grid's, and its copies take over once
    /// the grid has closed to the wrap's own curl (TryHoldCurls). TEST 495: the
    /// alignment tool's five-finger poses are not seated, and a weapon moving in
    /// the hand keeps its hold until the grip holds still.
    ///
    /// TEST 486: every engine object made here is let go of on the MAIN thread
    /// (Release). The engine's wrappers release in their finalizer otherwise,
    /// on the finalizer thread - and since 481b's skinning probe (unfinalized
    /// copies, now gone) the game hung at exit in GC.WaitForPendingFinalizers.
    ///
    /// Vertex indices are only ever used below VrDrawnBow.VertexCount: the
    /// engine's GetPositionOfVertex does not check them.
    /// </summary>
    internal static class VrHandMesh
    {
        private static readonly bool DirectSwap = !VrConfig.Bool("perf_legacy", false)
                                                  && VrConfig.Bool("perf_direct_hand_swap", true);
        /// <summary>finger_mesh; the VR tab switches it live (TEST 485).</summary>
        internal static bool Enabled { get; private set; } = VrConfig.Bool("finger_mesh", false);

        private static readonly int ThumbKeys = Math.Max(2, Math.Min(8, VrConfig.Int("finger_thumb_keys", 4)));
        /// <summary>The grid's middle/ring/pinky keys, and (TEST 504) its index
        /// keys apart: the grip's close is most of what the hands do, and 12 keys
        /// were 6-8 deg a joint a step ("still feels low framerate").</summary>
        private static readonly int GridKeys = Math.Max(3, Math.Min(32, VrConfig.Int("finger_grid_keys", 20)));
        private static readonly int IndexKeys = Math.Max(3, Math.Min(16, VrConfig.Int("finger_index_keys", 10)));
        private static readonly int HoldKeys = Math.Max(2, Math.Min(16, VrConfig.Int("finger_hold_keys", 12)));
        private static readonly float FingerLength = VrConfig.Float("finger_length_m", 0.09f);
        private static readonly float McpBack = VrConfig.Float("finger_mcp_back_m", 0.012f);
        private static readonly Vec3 RelaxDeg = V3("finger_relax_deg", 12f, 22f, 10f);
        private static readonly Vec3 FistDeg = V3("finger_fist_deg", 85f, 100f, 70f);
        private static readonly Vec3 ThumbRelaxDeg = V3("finger_thumb_relax_deg", 12f, 12f, 10f);
        private static readonly Vec3 ThumbFistDeg = V3("finger_thumb_fist_deg", 35f, 45f, 60f);
        private static readonly float[] Cascade = VrConfig.Floats("finger_relax_cascade", new[] { 1f, 1.2f, 1.4f, 1.6f });
        private static readonly float[] ConvergeDeg = VrConfig.Floats("finger_converge_deg", new[] { 0f, 2f, 3f, 5f });
        private static readonly float AdductRest = VrConfig.Float("finger_adduct_rest", 0.7f);
        private static readonly float AdductFist = VrConfig.Float("finger_adduct_fist", 0.75f);
        private static readonly float BuildMs = VrConfig.Float("finger_build_ms", 3f);
        private static readonly bool HoldBake = VrConfig.Bool("finger_hold_bake", true);
        private static readonly float HoldZone = VrConfig.Float("finger_hold_zone_m", 0.07f);
        private static readonly float HoldSkin = VrConfig.Float("finger_hold_skin_m", -0.003f);
        private static readonly float SeatMax = VrConfig.Float("finger_seat_max_m", 0.04f);
        private static readonly float HandleRadius = VrConfig.Float("finger_handle_radius_m", 0.016f);

        /// <summary>TEST 495: how long a held weapon's grip must hold still (the
        /// alignment tool nudging it) before its hold is baked again.</summary>
        private static readonly float Rebake = VrConfig.Float("finger_hold_rebake_s", 0.25f);

        /// <summary>TEST 508: how long after a hand takes hold its hold is baked at
        /// once for any new handle - a fresh grab, not a nudge.</summary>
        private const float FreshS = 0.3f;

        private static readonly VrFingers.Grip[] _lastGrip = { VrFingers.Grip.Free, VrFingers.Grip.Free };
        private static readonly long[] _freshUntil = new long[2];

        /// <summary>TEST 497: wrapped fingers slide along the handle until they lie
        /// side by side, this far apart (VrFingerRig.SideBySide), by at most
        /// finger_side_max_m (0: they stay where their knuckles put them).</summary>
        private static readonly float SideGap = VrConfig.Float("finger_side_gap_m", 0.001f);
        private static readonly float SideMax = VrConfig.Float("finger_side_max_m", 0.012f);

        /// <summary>TEST 498: a hold bakes on this budget a frame (the grid's
        /// in-betweens keep finger_build_ms) - a grab closes in ~10 frames.</summary>
        private static readonly float HoldBuildMs = VrConfig.Float("finger_hold_build_ms", 6f);

        /// <summary>TEST 499: an arrow rests this far clear of the index finger's top
        /// and of the handle's side.</summary>
        private static readonly float ArrowGap = VrConfig.Float("finger_arrow_gap_m", 0.004f);
        private const float Rad = (float)(Math.PI / 180.0);
        private const string Prefix = "bvr_fingers_";

        /// <summary>True while our copies are drawn instead of the game's hands.</summary>
        internal static bool Live { get; private set; }

        /// <summary>One keyframe: a posed copy and the metamesh it hangs on the
        /// skeleton by.</summary>
        private sealed class Copy
        {
            public Mesh Mesh;
            public MetaMesh Meta;

            /// <summary>TEST 505: on the skeleton now.</summary>
            public bool On;
        }

        /// <summary>
        /// TEST 505 (finger_attach_shown): only the copies SHOWN hang on the skeleton;
        /// the rest stay built, off it, and go on when shown. Every copy used to hang
        /// on it hidden - 1648 for bare hands after TEST 504 (902 metameshes on the
        /// skeleton) - and the body was then reported to "stutter or jitter, not the
        /// world view": the engine walks a skeleton's meshes for every view it draws.
        /// 0: the old way (all on, the shown one unmasked).
        /// </summary>
        private static readonly bool AttachShown = VrConfig.Bool("finger_attach_shown", true);

        // TEST 505: what the finger hands cost a frame, and how often a frame came late.
        private static long _statAt, _lastTick;
        private static int _statFrames, _statLate, _statSwaps;
        private static double _statMs, _statMaxMs, _statSwapMaxMs;

        private sealed class Side
        {
            public VrFingerRig.Fit Fit;
            public MatrixFrame Rest;

            /// <summary>TEST 490: where a copy hides a digit it does not draw - that
            /// digit's own root joint (MCP; the thumb's CMC), inside the hand, in
            /// bind space. A face from a drawn neighbour to it stays inside the
            /// flesh; 481b's single wrist pivot pulled every such face down to the
            /// wrist, and between the thumb and index that showed as a dark wedge.
            /// Digit vertices follow the hand (finger0 is held at rest), so any
            /// point skins to one spot for all of them.</summary>
            public readonly Vec3[] Root = new Vec3[VrFingerRig.Digits];

            public readonly bool[] Has = new bool[VrFingerRig.Digits];

            /// <summary>TEST 491: every copy is the WHOLE hand - a grid of thumb x
            /// index x middle/ring/pinky curls, and a hold grid of thumb x index
            /// closure. A face the thumb shares with the index was split between
            /// the thumb's copies and the fingers' (the streak at their web, part
            /// of the thumb "staying up"); within one copy it is whole.</summary>
            public Copy[,,] Grid;

            /// <summary>TEST 495: the hold shown, and the one baking behind it while
            /// the same weapon is moved in the hand (the alignment tool); the key the
            /// grip last moved to, and since when.</summary>
            public HoldSet Hold, Next;

            /// <summary>TEST 662: a hold replaced since the last swap, its shown copy
            /// still on - dropped in Show after what replaces it is on.</summary>
            public HoldSet Old;
            public string Want;
            public long WantSince;
            public int ShownGrid = -1, ShownHold = -1;

            /// <summary>The thumb's keys (1 when the fit found no thumb).</summary>
            public int Thumbs => Has[0] ? ThumbKeys : 1;
        }

        /// <summary>TEST 494: one baked hold - a [thumb, index] grid of copies
        /// closed on one handle, its wrap, and the grid curls nearest that wrap.
        /// Copies null: dropped, or nothing to close on.</summary>
        private sealed class HoldSet
        {
            public string Key, Item;
            public Copy[,] Copies;
            public int Built;
            public readonly Vec3[] Wrap = new Vec3[VrFingerRig.Digits];
            public readonly Vec3[] Turn = new Vec3[VrFingerRig.Digits];
            public readonly float[] Radius = new float[VrFingerRig.Digits];
            public readonly float[][] Widths = new float[VrFingerRig.Digits][];
            public Vec3[] Slide = new Vec3[VrFingerRig.Digits];
            public Vec3 Lift;
            public Vec3 Curls;
            public bool Wrapped;

            /// <summary>TEST 499: in the hand's rest axes, where an arrow rests on
            /// this fist, and the handle's centre the bow turns about.</summary>
            public Vec3 Rest, Pivot;

            public bool Complete => Copies != null && Built == Copies.Length;

            /// <summary>TEST 508: the fully closed copy is built (it is built first).</summary>
            public bool Ready => Copies != null && Copies[Copies.GetLength(0) - 1, Copies.GetLength(1) - 1] != null;
        }

        private sealed class Piece
        {
            public Mesh Source;
            public readonly List<Mesh> Originals = new List<Mesh>();
            public string Name;
            public int Count;
            public Vec3[] Local;
            public Vec3[] Bind;
            public int[] SideOf;
            public int[] Digit;
            public Vec3[] W;
            public readonly Side[] Sides = new Side[2];
            public Copy Base;
        }

        private static readonly List<Piece> _pieces = new List<Piece>();
        private static readonly Queue<Action> _jobs = new Queue<Action>();

        /// <summary>TEST 494: hold bakes run before the grid's in-between keys.</summary>
        private static readonly Queue<Action> _holdJobs = new Queue<Action>();
        private static readonly HashSet<string> _owned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Per hand [0 left, 1 right]: finger0's rest spot in the hand's
        /// rest axes - the palm channel, where the engine hangs a weapon - and
        /// this tick's hold key (null: nothing to close on yet).</summary>
        private static readonly Vec3[] _channel = new Vec3[2];
        private static readonly string[] _wantKey = new string[2];
        private static readonly string[] _wantItem = new string[2];

        /// <summary>TEST 491: per VrSkinnedWeapon role, where its weapon sits in the
        /// holding hand beyond the engine's grip (hand-bone frame) - Seat; and per
        /// hand the handle it was worked out for.</summary>
        private static readonly Vec3[] _seat = new Vec3[2];
        private static readonly string[] _seatKey = new string[2];
        private static readonly Vec3[] _seatOf = new Vec3[2];

        /// <summary>TEST 491: where the role's weapon is seated in the hand, in its
        /// hand bone's frame; zero unless our hands are drawn, and for a weapon
        /// aligned for them (TEST 495 - its pose has the seat in it).</summary>
        internal static Vec3 SeatOf(HandRole role) =>
            Live && (int)role < 2 && !VrSkinnedWeapon.FingerPosed(role) ? _seat[(int)role] : Vec3.Zero;

        private static Mission _mission;
        private static UIntPtr _skeleton;
        private static GameEntity _entity;
        private static int _meshCount = -1;
        private static int _initialLeft;
        private static int _built, _buildFrames, _fineLeft;
        private static double _buildMs;
        private static long _logAt;
        private static bool _broken;

        /// <summary>VrBodyHide leaves these alone: our copies always, and the
        /// game's hand meshes while ours are drawn.</summary>
        internal static bool Owns(string name)
        {
            return name != null && (name.StartsWith(Prefix, StringComparison.Ordinal) || (Live && _owned.Contains(name)));
        }

        /// <summary>TEST 485: the VR tab's switch. Off takes our copies off and
        /// shows the game's hands; on builds them again on the next tick.</summary>
        internal static void SetEnabled(bool on)
        {
            if (on == Enabled)
                return;

            Enabled = on;
            if (on)
                _broken = false;
            else
                Reset();

            // TEST 495: the weapons take the poses aligned for these hands, or back.
            VrSkinnedWeapon.Repose();
        }

        internal static void Tick(GameEntity entity, Skeleton skeleton, VrHandSlot main, VrHandSlot off)
        {
            if (!Enabled || _broken || entity == null || skeleton == null)
                return;

            long t0 = Stopwatch.GetTimestamp();
            try
            {
                TickBody(entity, skeleton, main, off);
            }
            finally
            {
                Stat(t0);
            }
        }

        /// <summary>
        /// TEST 662: "fingers disappear, more when I close and open them fast, but even
        /// open" (an 85 fps battle; the palm still drawn). Since TEST 505 a swap takes
        /// the old copy off the skeleton and hangs the new one on - but the engine draws
        /// an agent from its cached renderer data, rebuilt once a frame by the
        /// pre-display weapon pass (vanilla rebuilds it after any visibility change,
        /// Agent.SetSynchedPrefabComponentVisibility), while Tick runs at
        /// AfterMissionTick, after the render has started, and the scene is built on its
        /// own thread whose phase slips against the tick (TEST 167/396). A frame built
        /// in between drew the stale list with the old copy already hidden: no fingers
        /// on that hand, at every swap. So the copies are swapped HERE, in that pass,
        /// just before its rebuild (VrWeaponHands.LateWeaponPass), and a retired copy's
        /// mask waits a tick (_hideNext). Off that seam (weapon_seam / weapon_refresh
        /// changed) Tick swaps as before.
        /// </summary>
        internal static void LateShow()
        {
            if (!Enabled || _broken || _entity == null || (!Live && (_initialLeft > 0 || _pieces.Count == 0)))
                return;

            try
            {
                ShowNow();
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("TEST 481: the finger meshes failed; the game's hands are shown again. Nothing else is affected.", ex);
                try
                {
                    Forget(_entity);
                }
                catch
                {
                    // The log above is the answer either way.
                }
            }
        }

        private static void ShowNow()
        {
            if (!Live)
                GoLive();
            Show();
        }

        /// <summary>TEST 505: every 10 s while the hands are live - frames, those that
        /// came 16 ms or more after the last (a 72 Hz frame is 13.9), this code's
        /// own time a frame, the copies on the skeleton, and the swaps.</summary>
        private static void Stat(long t0)
        {
            long now = Stopwatch.GetTimestamp();
            double ms = (now - t0) * 1000.0 / Stopwatch.Frequency;
            if (_lastTick != 0 && (t0 - _lastTick) * 1000.0 / Stopwatch.Frequency >= 16.0)
                _statLate++;
            _lastTick = t0;
            _statFrames++;
            _statMs += ms;
            _statMaxMs = Math.Max(_statMaxMs, ms);
            if (_statAt == 0)
                _statAt = now + 10 * Stopwatch.Frequency;
            if (now < _statAt)
                return;

            if (Live)
            {
                int on = 0, all = 0;
                foreach (Piece piece in _pieces)
                {
                    foreach (Side side in piece.Sides)
                    {
                        if (side == null)
                            continue;
                        foreach (Copy[] set in new[] { Flat(side.Grid), Flat(side.Hold?.Copies), Flat(side.Next?.Copies) })
                        {
                            foreach (Copy c in set)
                            {
                                all += c != null ? 1 : 0;
                                on += c != null && c.On ? 1 : 0;
                            }
                        }
                    }
                }

                VrLog.Info("TEST 505 timing: " + _statFrames + " frames in 10 s, " + _statLate + " came 16+ ms after the last; "
                           + "the finger hands took " + (_statMs / Math.Max(1, _statFrames)).ToString("F2", CultureInfo.InvariantCulture)
                           + " ms a frame (worst " + _statMaxMs.ToString("F1", CultureInfo.InvariantCulture) + "); " + on + " of " + all
                           + " copies on the skeleton; " + _statSwaps + " swaps (worst "
                           + _statSwapMaxMs.ToString("F2", CultureInfo.InvariantCulture) + " ms).");
            }

            _statAt = now + 10 * Stopwatch.Frequency;
            _statFrames = _statLate = _statSwaps = 0;
            _statMs = _statMaxMs = _statSwapMaxMs = 0;
        }

        private static Copy[] Flat(Array copies)
        {
            if (copies == null)
                return new Copy[0];
            var flat = new Copy[copies.Length];
            int i = 0;
            foreach (Copy c in copies)
                flat[i++] = c;
            return flat;
        }

        private static void TickBody(GameEntity entity, Skeleton skeleton, VrHandSlot main, VrHandSlot off)
        {
            try
            {
                if (Mission.Current != _mission || skeleton.Pointer != _skeleton
                    || entity.MultiMeshComponentCount != _meshCount)
                    Rebuild(entity, skeleton, main, off);

                if (_jobs.Count > 0 || _holdJobs.Count > 0)
                    RunJobs();

                if (!Live && (_initialLeft > 0 || _pieces.Count == 0))
                    return;

                Bakes(entity);
                if (!VrWeaponHands.ShowsFingersLate)
                    ShowNow();
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("TEST 481: the finger meshes failed; the game's hands are shown again. Nothing else is affected.", ex);
                try
                {
                    Forget(_entity);
                }
                catch
                {
                    // The log above is the answer either way.
                }
            }
        }

        /// <summary>
        /// Lets go of every engine object held here (TEST 486), on this thread.
        /// With the mission still running (the toggle, motion hands switched off)
        /// our copies come off the skeleton and the game's hands come back first;
        /// with it gone, the scene took them off already.
        /// </summary>
        internal static void Reset()
        {
            GameEntity alive = null;
            try
            {
                Mission mission = Mission.Current;
                if (mission != null && mission == _mission && !mission.IsFinalized
                    && mission.CurrentState == Mission.State.Continuing)
                    alive = _entity;
            }
            catch (Exception)
            {
                // No mission to ask: the scene is gone.
            }

            try
            {
                Forget(alive);
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 486: taking the finger copies off failed; their references are dropped anyway.", ex);
            }

            _pieces.Clear();
            _jobs.Clear();
            _holdJobs.Clear();
            _fineLeft = 0;
            _owned.Clear();
            _entity = null;
            _mission = null;
            _skeleton = UIntPtr.Zero;
            _meshCount = -1;
            _initialLeft = 0;
        }

        private static void Rebuild(GameEntity entity, Skeleton skeleton, VrHandSlot main, VrHandSlot off)
        {
            // Same entity in the same mission (equipment changed): take ours off.
            // Anything older died with its scene or its agent; only our
            // references to it go.
            Forget(Mission.Current == _mission && _entity != null && _entity.Pointer == entity.Pointer ? entity : null);

            _mission = Mission.Current;
            _skeleton = skeleton.Pointer;
            _entity = entity;
            _initialLeft = 0;
            _built = _buildFrames = 0;
            _buildMs = 0;
            _meshCount = entity.MultiMeshComponentCount;

            if (!main.Valid || !off.Valid || main.Bone < 0 || off.Bone < 0)
                return;

            var rest = new MatrixFrame[2];
            foreach (VrHandSlot slot in new[] { main, off })
            {
                int s = slot.Controller == VrHand.Left ? 0 : 1;
                rest[s] = skeleton.GetBoneEntitialRestFrame(slot.Bone, false);
                sbyte item = ItemOf(skeleton, slot.Bone);
                _channel[s] = item >= 0
                    ? rest[s].rotation.TransformToLocal(skeleton.GetBoneEntitialRestFrame(item, false).origin - rest[s].origin)
                    : new Vec3(0.084f, 0.015f, 0f);
            }

            var all = new List<Mesh>();
            foreach (Mesh m in skeleton.GetAllMeshes())
            {
                if (m != null && !(m.Name ?? string.Empty).StartsWith(Prefix, StringComparison.Ordinal))
                    all.Add(m);
                else
                    Release(m);
            }

            // LOD0 only is copied (first person draws nothing else); every LOD
            // of it is hidden, so no unbent shadow is left behind.
            var read = new List<KeyValuePair<Piece, ManagedMeshEditOperations>>();
            var points = new[] { new List<Vec3>(), new List<Vec3>() };
            var taken = new HashSet<Mesh>();
            void Take(Mesh m, bool byName)
            {
                Piece piece = Read(m, rest, out ManagedMeshEditOperations ops);
                if (piece == null)
                    return;

                if (!byName && !HandShaped(piece))
                {
                    Release(ops);
                    Release(piece.Base.Mesh);
                    return;
                }

                if (!byName)
                    VrLog.Info("TEST 500: '" + m.Name + "' carries the hands (found by its shape - no hand, glove or gauntlet in "
                               + "its name); it gets five fingers.");

                taken.Add(m);
                for (int v = 0; v < piece.Count; v++)
                    points[piece.SideOf[v]].Add(piece.Local[v]);

                Dump(piece);
                foreach (Mesh lod in all)
                {
                    if (lod.Name == m.Name || lod.Name.StartsWith(m.Name + ".lod", StringComparison.Ordinal))
                        piece.Originals.Add(lod);
                }

                read.Add(new KeyValuePair<Piece, ManagedMeshEditOperations>(piece, ops));
            }

            foreach (Mesh m in all)
            {
                if (!m.Name.Contains(".lod") && VrBodyHide.IsHandName(m.Name))
                    Take(m, true);
            }

            // TEST 500: an armour whose hands come in a mesh named for something
            // else (battania_warlord_bracers - War Sails and the next battle had no
            // fingers at all): the other meshes, by their shape.
            if (points[0].Count < 100 || points[1].Count < 100)
            {
                foreach (Mesh m in all)
                {
                    if (!m.Name.Contains(".lod") && !taken.Contains(m) && !VrBodyHide.IsHandName(m.Name))
                        Take(m, false);
                }
            }

            // TEST 488: one rig per hand, from every piece's vertices together.
            var fits = new VrFingerRig.Fit[2];
            for (int s = 0; s < 2; s++)
            {
                fits[s] = VrFingerRig.FitHand(points[s], FingerLength, McpBack, ConvergeDeg, _channel[s]);
                Describe(fits[s], s, read.Count, points[s].Count);
            }

            foreach (KeyValuePair<Piece, ManagedMeshEditOperations> pair in read)
            {
                Piece piece = pair.Key;
                if (Prepare(entity, piece, pair.Value, fits, rest))
                {
                    _pieces.Add(piece);
                    continue;
                }

                Drop(null, piece.Base);
            }

            // TEST 624: a sparse first tier - the hands go live on it (~0.3 s, not
            // 494's ~3 s for every other key) - then every other key, then the rest.
            for (int tier = 0; tier < 3; tier++)
            {
                foreach (Piece piece in _pieces)
                    Queue(piece, tier);
            }

            // The wrappers of the game's meshes no piece keeps go now as well.
            var kept = new HashSet<Mesh>();
            foreach (Piece piece in _pieces)
                kept.UnionWith(piece.Originals);
            foreach (Mesh m in all)
            {
                if (!kept.Contains(m))
                    Release(m);
            }

            VrLog.Info("TEST 481: " + _pieces.Count + " hand mesh(es) get five fingers; " + _initialLeft
                       + " finger copies to build, ~" + BuildMs.ToString("F1", CultureInfo.InvariantCulture)
                       + " ms a frame. The game's hands stay until they are ready (TEST 494: then " + _fineLeft
                       + " in-between keys).");
        }

        /// <summary>TEST 500: a mesh that IS hands - fingers out past the palm on a
        /// hand bone, and next to nothing far from the hands (a body armour's sleeve
        /// may reach a wrist, but most of it is the torso, far away).</summary>
        private static bool HandShaped(Piece piece)
        {
            int fingers = 0, near = 0;
            for (int v = 0; v < piece.Count; v++)
            {
                Vec3 p = piece.Local[v];
                if (p.Length < 0.35f)
                    near++;
                if (p.x > 0.11f && p.x < 0.26f && Math.Abs(p.y) < 0.06f && Math.Abs(p.z) < 0.08f)
                    fingers++;
            }

            return fingers >= 60 && near >= 0.85f * piece.Count;
        }

        /// <summary>TEST 490: each hand mesh's vertices in the hand's rest axes,
        /// once per mesh, beside the cfg (bvr_hand_[mesh]_[side].csv) - the rig
        /// can then be checked offline on the meshes the player wears.</summary>
        private static void Dump(Piece piece)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(VrConfig.Path ?? string.Empty);
                if (string.IsNullOrEmpty(dir))
                    return;

                for (int s = 0; s < 2; s++)
                {
                    string file = System.IO.Path.Combine(dir, "bvr_hand_" + piece.Name + (s == 0 ? "_left" : "_right") + ".csv");
                    if (System.IO.File.Exists(file))
                        continue;

                    var text = new System.Text.StringBuilder("x,y,z\n");
                    for (int v = 0; v < piece.Count; v++)
                    {
                        if (piece.SideOf[v] == s)
                            text.Append(V(piece.Local[v])).Append('\n');
                    }

                    System.IO.File.WriteAllText(file, text.ToString());
                    VrLog.Info("TEST 490: wrote " + file + ".");
                }
            }
            catch (Exception ex)
            {
                VrLog.Info("TEST 490: no vertex dump for '" + piece.Name + "' (" + ex.Message + ").");
            }
        }

        private static string V(Vec3 v) => v.x.ToString("F5", CultureInfo.InvariantCulture) + ","
                                           + v.y.ToString("F5", CultureInfo.InvariantCulture) + ","
                                           + v.z.ToString("F5", CultureInfo.InvariantCulture);

        private static void Describe(VrFingerRig.Fit fit, int s, int pieces, int points)
        {
            string label = "TEST 488 " + (s == 0 ? "left" : "right") + " hand (" + pieces + " piece(s), " + points + " vertices)";
            if (!fit.Valid)
            {
                VrLog.Info(label + ": no fingers fitted (" + fit.Why + "); this hand keeps the game's look.");
                return;
            }

            if (fit.Mitten)
            {
                VrLog.Info(label + ": TEST 504 - its four fingers are one block, " + fit.Why + "; they bend together with the grip"
                           + " (the thumb, if it has one, stays as modelled).");
                return;
            }

            VrLog.Info(label + ": fingers " + Cm(fit.Length[1]) + "/" + Cm(fit.Length[2]) + "/" + Cm(fit.Length[3])
                       + "/" + Cm(fit.Length[4]) + " cm (index..pinky), "
                       + (fit.HasThumb ? "thumb " + Cm(fit.Length[0]) + " cm" : "NO thumb found")
                       + ", knuckles " + fit.KnuckleX.ToString("F3", CultureInfo.InvariantCulture) + " m out, palm side "
                       + (fit.PalmSign > 0 ? "+y" : "-y") + ", thumb side " + (fit.ThumbSide > 0 ? "+z" : "-z")
                       + ", modelled splay from the middle finger " + Deg(fit.Splay[1]) + "/" + Deg(fit.Splay[3]) + "/"
                       + Deg(fit.Splay[4]) + " deg (index/ring/pinky).");
        }

        /// <summary>A copy of the source for the base, its vertices read into a
        /// new piece in the hands' rest axes. The edit stays open for Prepare.</summary>
        private static Piece Read(Mesh source, MatrixFrame[] rest, out ManagedMeshEditOperations ops)
        {
            ops = null;
            Material material = source.GetMaterial();
            if (material == null)
                return null;

            Mesh mesh = Mesh.CreateMeshWithMaterial(material);
            Release(material);
            ops = ManagedMeshEditOperations.Create(mesh);
            ops.AddSkinnedMeshWithColor(source, MatrixFrame.Identity, new Vec3(1f, 1f, 1f));
            int count = VrDrawnBow.VertexCount(ops);
            if (count <= 0)
            {
                VrLog.Info("TEST 481 '" + source.Name + "': the copy holds " + count + " vertices; it keeps the game's look.");
                Release(ops);
                Release(mesh);
                ops = null;
                return null;
            }

            var piece = new Piece
            {
                Source = source, Name = source.Name, Count = count, Local = new Vec3[count], Bind = new Vec3[count],
                SideOf = new int[count], Digit = new int[count], W = new Vec3[count], Base = new Copy { Mesh = mesh }
            };

            for (int v = 0; v < count; v++)
            {
                Vec3 p = ops.GetPositionOfVertex(v);
                piece.Bind[v] = p;
                int s = (p - rest[1].origin).Length < (p - rest[0].origin).Length ? 1 : 0;
                piece.SideOf[v] = s;
                piece.Local[v] = rest[s].rotation.TransformToLocal(p - rest[s].origin);
                piece.Digit[v] = -1;
            }

            return piece;
        }

        /// <summary>The piece's weights from its hand's rig, its base copy (every
        /// moving vertex hidden) finished and hung. False when no vertex of it
        /// moves with a finger (a cuff): the game's own piece stays.</summary>
        private static bool Prepare(GameEntity entity, Piece piece, ManagedMeshEditOperations ops, VrFingerRig.Fit[] fits,
                                    MatrixFrame[] rest)
        {
            try
            {
                for (int s = 0; s < 2; s++)
                {
                    if (!fits[s].Valid)
                        continue;

                    var side = new Side { Fit = fits[s], Rest = rest[s] };
                    for (int d = 0; d < VrFingerRig.Digits; d++)
                    {
                        side.Has[d] = d > 0 || fits[s].HasThumb;
                        side.Root[d] = rest[s].origin + rest[s].rotation.TransformToParent(fits[s].Joint[d, 0]);
                    }
                    piece.Sides[s] = side;
                }

                int moving = 0;
                for (int v = 0; v < piece.Count; v++)
                {
                    Side side = piece.Sides[piece.SideOf[v]];
                    if (side == null)
                        continue;

                    int d = VrFingerRig.Weigh(side.Fit, piece.Local[v], out Vec3 w);
                    if (d < 0 || !side.Has[d])
                        continue;

                    piece.Digit[v] = d;
                    piece.W[v] = w;
                    ops.SetPositionOfVertex(v, side.Root[d]);
                    moving++;
                }

                if (moving == 0)
                {
                    VrLog.Info("TEST 488 '" + piece.Name + "': no vertex moves with a finger; it keeps the game's look.");
                    return false;
                }

                // TEST 509: a mitten's block glued to its palm where it lies on it.
                for (int s = 0; s < 2; s++)
                {
                    if (piece.Sides[s] == null || !piece.Sides[s].Fit.Mitten)
                        continue;

                    var idx = new List<int>();
                    for (int v = 0; v < piece.Count; v++)
                    {
                        if (piece.SideOf[v] == s)
                            idx.Add(v);
                    }

                    var pts = idx.ConvertAll(v => piece.Local[v]);
                    var digits = idx.ConvertAll(v => piece.Digit[v]);
                    var ws = idx.ConvertAll(v => piece.W[v]);
                    VrFingerRig.Glue(piece.Sides[s].Fit, pts, digits, ws);
                    for (int k = 0; k < idx.Count; k++)
                        piece.W[idx[k]] = ws[k];
                }

                ops.FinalizeEditing();
            }
            finally
            {
                Release(ops);
            }

            Finish(piece.Base.Mesh, piece.Source, "base");
            piece.Base.Meta = Hang(entity, piece.Base.Mesh, piece.Name + "_base", true);
            piece.Base.On = true;
            int count = 0;
            foreach (int d in piece.Digit)
                count += d >= 0 ? 1 : 0;
            VrLog.Info("TEST 481 '" + piece.Name + "': " + count + " of " + piece.Count + " vertices move with a finger; the base keeps the rest.");
            return true;
        }

        private static float Sum(Vec3 w) => w.x + w.y + w.z;

        /// <summary>TEST 494/624: tier 0 (index open/closed, every 4th grip key and
        /// the last) - the hands go live once it is built; tier 1 the rest of the
        /// coarse keys (every other one); tier 2 the in-betweens. Show falls back
        /// to the nearest built tier.</summary>
        private static void Queue(Piece piece, int tier)
        {
            for (int s = 0; s < 2; s++)
            {
                Side side = piece.Sides[s];
                if (side == null)
                    continue;

                int ss = s;
                if (tier == 0)
                    side.Grid = new Copy[side.Thumbs, IndexKeys, GridKeys];
                for (int t = 0; t < side.Thumbs; t++)
                {
                    for (int i = 0; i < IndexKeys; i++)
                    {
                        for (int m = 0; m < GridKeys; m++)
                        {
                            if (TierOf(i, m) != tier)
                                continue;

                            int tt = t, ii = i, mm = m;
                            if (tier == 0)
                            {
                                _initialLeft++;
                                _jobs.Enqueue(() => { BuildGrid(piece, ss, tt, ii, mm); _initialLeft--; });
                                continue;
                            }

                            _fineLeft++;
                            _jobs.Enqueue(() =>
                            {
                                BuildGrid(piece, ss, tt, ii, mm);
                                if (--_fineLeft == 0)
                                    VrLog.Info("TEST 494: the in-between finger keys are built too - " + _built + " copies in all.");
                            });
                        }
                    }
                }
            }
        }

        private static int Coarse(int key, int keys) => key % 2 == 0 || key == keys - 1 ? key : key - 1;

        /// <summary>TEST 624: the nearest of 0, stride, 2 stride, .. and the last key.</summary>
        private static int Snap(int key, int keys, int stride)
        {
            int down = key - key % stride, up = Math.Min(down + stride, keys - 1);
            return up - key < key - down ? up : down;
        }

        private static int LiveI(int i) => Snap(i, IndexKeys, IndexKeys - 1);
        private static int LiveM(int m) => Snap(m, GridKeys, 4);

        private static int TierOf(int i, int m) =>
            LiveI(i) == i && LiveM(m) == m ? 0 : Coarse(i, IndexKeys) == i && Coarse(m, GridKeys) == m ? 1 : 2;

        /// <summary>A finger's relaxed joint angles (degrees): finger_relax_deg,
        /// more curled toward the pinky (finger_relax_cascade) like a resting hand.</summary>
        private static Vec3 RelaxOf(Side side, int digit)
        {
            if (digit == 0)
                return ThumbRelaxDeg;

            // TEST 505: a mitten loosens only MittenOpen of the way from its modelled
            // fist - unrolled all the way its block was a flat plank, the inside of
            // the grip showing ("broken default hands pose").
            Vec3 open = RelaxDeg * Cascade[digit - 1];
            if (!side.Fit.Mitten)
                return open;

            Vec3 fist = side.Fit.Rest[digit] * (1f / Rad);
            return fist + (open - fist) * MittenOpen;
        }

        /// <summary>TEST 505: how far a mitten opens when let go, 0 .. 1 (a relaxed
        /// hand). TEST 506: 0 (the default) - let go, the fist is exactly as modelled:
        /// its fingertips are welded to the palm, and any opening stretched those
        /// faces across the grip's hollow ("inside the grip is full of mesh"). It is
        /// still fitted, for the arrow's rest on it.</summary>
        private static readonly float MittenOpen = Math.Max(0f, Math.Min(1f, VrConfig.Float("finger_mitten_open", 0f)));

        /// <summary>A digit's fist (degrees). TEST 504: a mitten's is its block as
        /// modelled - the closed hand the armour was made with.</summary>
        private static Vec3 FistOf(Side side, int digit) =>
            digit == 0 ? ThumbFistDeg : side.Fit.Mitten ? side.Fit.Rest[digit] * (1f / Rad) + MittenClose : FistDeg;

        /// <summary>TEST 507: how much tighter than modelled a mitten closes (deg,
        /// base/middle/end) - "the battania can't close its hand" with 506's still
        /// fist. TEST 509: 5/15/5 - mostly the middle joint (the knuckle swings the
        /// welded fingertips on a 10 cm lever); glued (VrFingerRig.Glue), no edge
        /// stretches past x1.4 offline, the tips still travel ~3 cm. 507's 20/20/10
        /// stretched edges x2.6 even glued ("the palm gets squeezed").</summary>
        private static readonly Vec3 MittenClose = V3("finger_mitten_close", 5f, 15f, 5f);

        private static void BuildGrid(Piece piece, int s, int t, int i, int m)
        {
            Side side = piece.Sides[s];
            var angles = new Vec3[VrFingerRig.Digits];
            var turn = new Vec3[VrFingerRig.Digits];
            if (side.Thumbs > 1)
                angles[0] = VrFingerRig.Angles(ThumbRelaxDeg, ThumbFistDeg, VrFingers.ThumbSpan * t / (side.Thumbs - 1));
            for (int f = 1; f < VrFingerRig.Digits; f++)
            {
                float c = f == 1 ? i / (float)(IndexKeys - 1) : m / (float)(GridKeys - 1);
                angles[f] = VrFingerRig.Angles(RelaxOf(side, f), FistOf(side, f), c);
                turn[f] = side.Fit.Normal * VrFingerRig.Spread(side.Fit, f, AdductRest, AdductFist, c);
            }

            side.Grid[t, i, m] = BuildPose(piece, s, angles, turn, null, "g" + t + "_" + i + "_" + m);
        }

        /// <summary>
        /// One pose copy for one hand: its digits bent to their joint angles
        /// (radians) after their turn (spread, the thumb's lift); the other hand's
        /// digits hidden on their root joints; the palms and cuffs where they are
        /// (the base draws them there too). Hung hidden on the skeleton.
        /// </summary>
        private static Copy BuildPose(Piece piece, int s, Vec3[] angles, Vec3[] turn, Vec3[] slide, string tag)
        {
            Side side = piece.Sides[s];
            Material material = piece.Source.GetMaterial();
            Mesh mesh = Mesh.CreateMeshWithMaterial(material);
            Release(material);
            ManagedMeshEditOperations ops = ManagedMeshEditOperations.Create(mesh);
            try
            {
                ops.AddSkinnedMeshWithColor(piece.Source, MatrixFrame.Identity, new Vec3(1f, 1f, 1f));
                if (VrDrawnBow.VertexCount(ops) != piece.Count)
                    throw new InvalidOperationException("a copy of '" + piece.Name + "' no longer holds " + piece.Count + " vertices");

                var posed = new VrFingerRig.Posed[VrFingerRig.Digits];
                for (int d = 0; d < VrFingerRig.Digits; d++)
                    posed[d] = new VrFingerRig.Posed(side.Fit, d, angles[d], turn[d]);

                for (int v = 0; v < piece.Count; v++)
                {
                    int d = piece.Digit[v];
                    Vec3 at;
                    if (d < 0)
                    {
                        at = piece.Bind[v];
                    }
                    else if (piece.SideOf[v] == s)
                    {
                        Vec3 bent = posed[d].Apply(piece.Local[v], piece.W[v]);
                        if (slide != null)
                            bent += slide[d] * Sum(piece.W[v]);
                        at = side.Rest.origin + side.Rest.rotation.TransformToParent(bent);
                    }
                    else
                    {
                        at = piece.Sides[piece.SideOf[v]].Root[d];
                    }

                    ops.SetPositionOfVertex(v, at);
                }

                ops.FinalizeEditing();
            }
            catch
            {
                Release(mesh);
                throw;
            }
            finally
            {
                Release(ops);
            }

            string name = (s == 0 ? "l_" : "r_") + tag;
            Finish(mesh, piece.Source, name);
            _built++;
            var copy = new Copy { Mesh = mesh, Meta = Hang(_entity, mesh, piece.Name + "_" + name, !AttachShown) };
            copy.On = !AttachShown;
            return copy;
        }

        /// <summary>
        /// TEST 487: what each holding hand closes on, from the STATIC grip -
        /// built again only when that changes (another item, another pose, a new
        /// two-handed hold), never from the drawn frame:
        ///  * the off hand two-handing: the shaft line VrTwoHand latched;
        ///  * a shield, bow or crossbow (not crafted - their vertices can be
        ///    read): its LOD0 vertices near the palm, fitted as a cylinder;
        ///  * anything else (crafted: 483 read 0 vertices from a sword and a
        ///    javelin, and the weapon went invisible): its own z axis through its
        ///    origin - the haft (TEST 460), radius finger_handle_radius_[WeaponClass],
        ///    SEATED in the palm the way a real grip holds it (TEST 491, Seat) - the
        ///    weapon is drawn there too (VrSkinnedWeapon, SeatOf).
        /// TEST 495: a weapon aligned for these hands with the tool
        /// (VrSkinnedWeapon.FingerPosed) is not seated - it is drawn exactly where
        /// it was aligned. While the same weapon moves in the hand (the tool's
        /// nudges, a key a frame - 491 started a bake every frame and finished
        /// none, dropping the fingers to the grid) its hold stays shown, and the
        /// new one is baked once the grip has held still for finger_hold_rebake_s.
        /// </summary>
        private static void Bakes(GameEntity entity)
        {
            var seat = new Vec3[2];
            long now = Stopwatch.GetTimestamp();
            for (int s = 0; s < 2; s++)
            {
                _wantKey[s] = _wantItem[s] = null;
                VrFingers.Grip grip = VrFingers.Grips[s];
                HandRole role = VrFingers.HeldRole[s];
                if (grip != _lastGrip[s])
                {
                    _lastGrip[s] = grip;
                    _freshUntil[s] = now + (long)(FreshS * Stopwatch.Frequency);
                }

                bool fresh = now < _freshUntil[s];
                bool stockGrip = VrCrossbow.TryMainGrip((VrHand)s, out Vec3 stockPoint,
                    out Vec3 stockAxis, out string stockItem);
                List<MetaMesh> copies = null;
                MatrixFrame itemInHand = MatrixFrame.Identity;
                string itemId = stockItem, weaponClass = "Crossbow";
                if (!HoldBake || (grip != VrFingers.Grip.Held && grip != VrFingers.Grip.TwoHand)
                    || (!stockGrip && !VrSkinnedWeapon.TryHeldLocal(role, out copies,
                                                                      out itemInHand, out itemId, out weaponClass)))
                    continue;

                float radius = VrConfig.Float("finger_handle_radius_" + weaponClass, HandleRadius);
                VrFingerRig.Handle handle = null;
                string from, key;
                if (stockGrip)
                {
                    handle = VrFingerRig.LineHandle(stockPoint, stockAxis, _channel[s], radius, HoldZone);
                    from = "the crossbow stock";
                    key = K(handle.Center, 0.001f) + K(handle.Axis, 0.01f);
                }
                else if (grip == VrFingers.Grip.TwoHand)
                {
                    if (!VrTwoHand.TryOffLine(_channel[s], out Vec3 point, out Vec3 axis))
                        continue;
                    handle = VrFingerRig.LineHandle(point, axis, _channel[s], radius, HoldZone);
                    from = "the two-handed hold";
                    key = K(handle.Center, 0.001f) + K(handle.Axis, 0.01f);
                }
                else if (weaponClass.Contains("Shield") || weaponClass.Contains("Bow"))
                {
                    from = "its vertices";
                    key = K(itemInHand.origin, 0.001f) + K(itemInHand.rotation.u, 0.01f) + K(itemInHand.rotation.f, 0.01f);
                }
                else
                {
                    handle = VrFingerRig.LineHandle(itemInHand.origin, itemInHand.rotation.u, _channel[s], radius, HoldZone);
                    from = "the grip";
                    key = K(handle.Center, 0.001f) + K(handle.Axis, 0.01f);
                    if (VrSkinnedWeapon.FingerPosed(role))
                    {
                        _seatKey[s] = null;
                        _seatOf[s] = Vec3.Zero;
                    }
                    else if (_seatKey[s] != key)
                    {
                        _seatKey[s] = key;
                        _seatOf[s] = SeatFor(s, handle);
                    }

                    handle.Center = handle.Center + _seatOf[s];
                    seat[(int)role] = _seatOf[s];
                    key += K(_seatOf[s], 0.001f);
                }

                key = s + "|" + itemId + "|" + key + (radius * 1000f).ToString("F0", CultureInfo.InvariantCulture);
                _wantKey[s] = key;
                _wantItem[s] = itemId;

                foreach (Piece piece in _pieces)
                {
                    Side side = piece.Sides[s];
                    if (side == null || side.Hold?.Key == key || side.Next?.Key == key)
                        continue;

                    // TEST 498: one still baking for this weapon is left to finish - a
                    // grip settling in the hand moves the key every frame, and 495
                    // started a new bake each time (the bow: no fingers for 1.5 s).
                    // TEST 508: a FRESH grab (the off hand taking the shaft again
                    // somewhere else) is not a nudge: no wait, and the hold made for the
                    // last grab is not shown meanwhile - the fingers adjusted ~0.4 s late.
                    bool moved = !fresh && side.Hold != null && side.Hold.Item == itemId && side.Hold.Copies != null;

                    // TEST 505: a drawn bow turns about the grip in the hand (TEST 499) -
                    // 6 bakes of 48 copies in 4 s, frames late while they built. The hold
                    // made for it stays until the arrow is gone.
                    if (moved && VrBow.HoldsArrow && weaponClass.Contains("Bow"))
                        continue;

                    if (moved)
                    {
                        if (side.Want != key)
                        {
                            side.Want = key;
                            side.WantSince = now;
                        }

                        if (!side.Hold.Complete || now - side.WantSince < (long)(Rebake * Stopwatch.Frequency))
                            continue;
                    }

                    if (handle == null)
                        handle = VrFingerRig.FitHandle(HeldPoints(copies, itemInHand), _channel[s], HoldZone);

                    if (moved)
                    {
                        DropSet(entity, side.Next);
                        side.Next = Bake(piece, s, handle, key, itemId, from);
                        continue;
                    }

                    // TEST 662: the shown hold stays until Show has put up what replaces it.
                    DropSet(entity, side.Next);
                    side.Next = null;
                    side.Want = null;
                    Retire(entity, side);
                    side.Hold = Bake(piece, s, handle, key, itemId, from);
                }
            }

            _seat[0] = seat[0];
            _seat[1] = seat[1];
        }

        /// <summary>TEST 491: the handle's seat in hand s - over every piece's palm
        /// points (the shared rig), against the middle finger's knuckle.</summary>
        private static Vec3 SeatFor(int s, VrFingerRig.Handle handle)
        {
            VrFingerRig.Fit fit = null;
            var palm = new List<Vec3>();
            foreach (Piece piece in _pieces)
            {
                if (piece.Sides[s] == null)
                    continue;
                fit = piece.Sides[s].Fit;
                for (int v = 0; v < piece.Count; v++)
                {
                    if (piece.SideOf[v] == s && piece.Digit[v] < 0)
                        palm.Add(piece.Local[v]);
                }
            }

            if (fit == null)
                return Vec3.Zero;

            Vec3 move = VrFingerRig.Seat(palm, handle, fit.Normal, fit.Joint[2, 0], fit.Dir[2], SeatMax);
            VrLog.Info("TEST 491 " + (s == 0 ? "left" : "right") + ": the handle is seated "
                       + (move.Length * 1000f).ToString("F0", CultureInfo.InvariantCulture) + " mm into the palm's hold ("
                       + V(move) + " in the hand's axes) - the weapon is drawn there.");
            return move;
        }

        /// <summary>TEST 494: a hold baked in jobs that run before the grid's
        /// (RunJobs) - one per digit's wrap (491 wrapped all five in one go, 11 ms:
        /// a dropped frame at every grab), then its copies. Nothing to close on: a
        /// set with no copies - never shown, never baked again for this key.</summary>
        private static HoldSet Bake(Piece piece, int s, VrFingerRig.Handle handle, string key, string itemId, string from)
        {
            Side side = piece.Sides[s];
            var set = new HoldSet { Key = key, Item = itemId };
            string label = "TEST 487 " + (s == 0 ? "left" : "right") + " '" + itemId + "' (" + piece.Name + "): ";
            if (!handle.Valid)
            {
                VrLog.Info(label + "nothing to close on from " + from + " (" + handle.Why + "); the fingers keep finger_hold_curl.");
                return set;
            }

            set.Copies = new Copy[side.Has[0] ? ThumbKeys : 1, HoldKeys];

            // TEST 504: the fingers first - the thumb stops on them as wrapped.
            for (int k = 1; k <= VrFingerRig.Digits; k++)
            {
                int d = k % VrFingerRig.Digits, dd = d;
                if (side.Has[d])
                    _holdJobs.Enqueue(() => { if (set.Copies != null) WrapDigit(piece, s, handle, set, dd); });
            }

            _holdJobs.Enqueue(() =>
            {
                if (set.Copies == null)
                    return;

                // TEST 504: a mitten's block is the middle finger; the others hold its wrap.
                if (side.Fit.Mitten)
                {
                    foreach (int f in new[] { 1, 3, 4 })
                    {
                        set.Wrap[f] = set.Wrap[2];
                        set.Turn[f] = set.Turn[2];
                        set.Radius[f] = set.Radius[2];
                        set.Widths[f] = set.Widths[2];
                    }
                }

                set.Curls = CurlsOf(side, set.Wrap);
                if (SideMax > 0f)
                    set.Slide = VrFingerRig.SideBySide(side.Fit, set.Wrap, set.Turn, set.Widths, handle.Axis, SideGap, SideMax);
                ArrowRest(piece, s, side, set, handle);
                set.Wrapped = true;
                LogWrap(label, from, handle, set, s);
                QueueHold(piece, s, side, set);
            });
            return set;
        }

        /// <summary>The hold grid: thumb (finger_thumb_keys) x index
        /// (finger_hold_keys) closure, from relaxed to wrapped, middle, ring and
        /// pinky wrapped - the whole hand in every copy. The thumb's lift grows
        /// with its closure.</summary>
        private static void QueueHold(Piece piece, int s, Side side, HoldSet set)
        {
            Vec3[] wrap = set.Wrap;
            int thumbs = set.Copies.GetLength(0);
            // TEST 508: from the fully closed copy down - the hold shows once that is built.
            for (int n = thumbs * HoldKeys - 1; n >= 0; n--)
            {
                int t = n / HoldKeys, k = n % HoldKeys;
                {
                    int tt = t, kk = k;
                    float ct = thumbs > 1 ? t / (float)(thumbs - 1) : 1f;
                    float ci = k / (float)(HoldKeys - 1);
                    var a = (Vec3[])wrap.Clone();
                    var turn = new Vec3[VrFingerRig.Digits];
                    a[0] = ThumbRelaxDeg * Rad + (wrap[0] - ThumbRelaxDeg * Rad) * ct;
                    turn[0] = set.Lift * ct;
                    a[1] = RelaxOf(side, 1) * Rad + (wrap[1] - RelaxOf(side, 1) * Rad) * ci;
                    var slide = new Vec3[VrFingerRig.Digits];
                    for (int f = 1; f < VrFingerRig.Digits; f++)
                    {
                        turn[f] = side.Fit.Normal * VrFingerRig.Spread(side.Fit, f, AdductRest, AdductFist, f == 1 ? ci : 1f);
                        slide[f] = set.Slide[f] * (f == 1 ? ci : 1f);
                    }

                    _holdJobs.Enqueue(() =>
                    {
                        if (set.Copies == null)
                            return;
                        set.Copies[tt, kk] = BuildPose(piece, s, a, turn, slide, "h" + tt + "_" + kk);
                        set.Built++;
                    });
                }
            }
        }

        /// <summary>A shield's, bow's or crossbow's LOD0 vertices in the holding
        /// hand bone's frame: the static item frame x each mesh's own frame. The
        /// temporary copies they are read through are finalized and let go of
        /// at once (TEST 486).</summary>
        private static List<Vec3> HeldPoints(List<MetaMesh> copies, MatrixFrame itemInHand)
        {
            var pts = new List<Vec3>();
            foreach (MetaMesh meta in copies)
            {
                int n = meta.MeshCount;
                for (int i = 0; i < n; i++)
                {
                    Mesh m = meta.GetMeshAtIndex(i);
                    Material material = m?.GetMaterial();
                    if (m == null || (m.Name ?? string.Empty).Contains(".lod") || material == null)
                    {
                        Release(material);
                        Release(m);
                        continue;
                    }

                    Mesh tmp = Mesh.CreateMeshWithMaterial(material);
                    Release(material);
                    ManagedMeshEditOperations ops = ManagedMeshEditOperations.Create(tmp);
                    try
                    {
                        ops.AddMesh(m, MatrixFrame.Identity);
                        int count = VrDrawnBow.VertexCount(ops);
                        MatrixFrame local = itemInHand.TransformToParent(m.GetLocalFrame());
                        for (int v = 0; v < count; v++)
                            pts.Add(local.TransformToParent(ops.GetPositionOfVertex(v)));
                        ops.FinalizeEditing();
                    }
                    finally
                    {
                        Release(ops);
                        Release(tmp);
                        Release(m);
                    }
                }
            }

            return pts;
        }

        /// <summary>One digit's joint angles wrapped around the handle (fingers
        /// side by side - finger_adduct_fist; the thumb lifted over it - TEST 491).</summary>
        private static void WrapDigit(Piece piece, int s, VrFingerRig.Handle handle, HoldSet set, int d)
        {
            Side side = piece.Sides[s];
            var pts = new List<Vec3>();
            var ws = new List<Vec3>();
            for (int v = 0; v < piece.Count; v++)
            {
                if (piece.SideOf[v] == s && piece.Digit[v] == d && Sum(piece.W[v]) >= 0.3f)
                {
                    pts.Add(piece.Local[v]);
                    ws.Add(piece.W[v]);
                }
            }

            Vec3 fistDeg = FistOf(side, d);
            Vec3 turn = d == 0
                ? VrFingerRig.Lift(side.Fit, 0, RelaxOf(side, 0), fistDeg, pts, ws, handle, HoldSkin)
                : side.Fit.Normal * VrFingerRig.Spread(side.Fit, d, AdductRest, AdductFist, 1f);
            if (d == 0)
            {
                set.Lift = turn;
                handle = handle.With(new VrFingerRig.Obstacle(Wrapped(piece, s, set)));
            }

            set.Turn[d] = turn;
            set.Radius[d] = VrFingerRig.HalfWidth(side.Fit, d, pts, ws);
            set.Widths[d] = VrFingerRig.Widths(side.Fit, d, pts, ws);
            set.Wrap[d] = VrFingerRig.Wrap(side.Fit, d, RelaxOf(side, d), fistDeg, pts, ws, handle, HoldSkin, turn);
        }

        /// <summary>TEST 504: hand s's four fingers as this hold wraps them.</summary>
        private static IEnumerable<Vec3> Wrapped(Piece piece, int s, HoldSet set)
        {
            Side side = piece.Sides[s];
            for (int v = 0; v < piece.Count; v++)
            {
                int d = piece.Digit[v];
                if (piece.SideOf[v] == s && d > 0)
                    yield return VrFingerRig.Bend(side.Fit, d, set.Wrap[d], set.Turn[d], piece.Local[v], piece.W[v]);
            }
        }

        /// <summary>
        /// TEST 499: where an arrow rests on the wrapped fist, in the hand's rest axes
        /// (VrFingerRig.ArrowRest - TEST 505: against the stave on the back of the
        /// hand's side, on the index knuckle). 269c's rest was 5 cm up the
        /// CONTROLLER's fist - inside the drawn one.
        /// </summary>
        private static void ArrowRest(Piece piece, int s, Side side, HoldSet set, VrFingerRig.Handle handle)
        {
            VrFingerRig.Fit fit = side.Fit;

            // The hand as wrapped: every digit closed (the thumb lifted), slid side by side.
            var hand = new List<Vec3>();
            for (int v = 0; v < piece.Count; v++)
            {
                if (piece.SideOf[v] != s)
                    continue;

                int d = piece.Digit[v];
                hand.Add(d < 0
                    ? piece.Local[v]
                    : VrFingerRig.Bend(fit, d, set.Wrap[d], set.Turn[d], piece.Local[v], piece.W[v]) + set.Slide[d] * Sum(piece.W[v]));
            }

            set.Pivot = handle.Center;
            set.Rest = VrFingerRig.ArrowRest(fit, handle, set.Wrap, set.Turn, set.Slide, hand, ArrowGap);
        }

        /// <summary>TEST 499: where an arrow rests on hand s's fist, and the handle's
        /// centre, in that hand bone's frame - while it holds a weapon whose wrap is
        /// worked out.</summary>
        internal static bool TryArrowRest(int s, out Vec3 rest, out Vec3 pivot)
        {
            rest = pivot = Vec3.Zero;
            if (!Live || _wantItem[s] == null)
                return false;

            foreach (Piece piece in _pieces)
            {
                HoldSet set = piece.Sides[s]?.Hold;
                if (set != null && set.Wrapped && set.Item == _wantItem[s])
                {
                    rest = set.Rest;
                    pivot = set.Pivot;
                    return true;
                }
            }

            return false;
        }

        /// <summary>TEST 560: hand s's palm in its hand bone's frame - the channel
        /// (finger0's rest spot, where a handle sits), the way the palm faces
        /// (the fit's normal), the fingers (+x, the knuckles' side, squared to the
        /// normal) and across to the thumb (the fit's thumb side). False until
        /// the hand is fitted.</summary>
        internal static bool TryPalm(int s, out Vec3 channel, out Vec3 normal, out Vec3 fingers, out Vec3 thumb)
        {
            channel = _channel[s];
            normal = fingers = thumb = Vec3.Zero;
            if (!Live)
                return false;

            foreach (Piece piece in _pieces)
            {
                VrFingerRig.Fit fit = piece.Sides[s]?.Fit;
                if (fit == null || !fit.Valid || fit.Normal.LengthSquared < 1e-6f)
                    continue;

                normal = fit.Normal.NormalizedCopy();
                fingers = new Vec3(1f, 0f, 0f);
                fingers -= normal * Vec3.DotProduct(fingers, normal);
                if (fingers.Normalize() < 0.3f)
                    continue;

                thumb = Vec3.CrossProduct(normal, fingers);
                if (thumb.z * fit.ThumbSide < 0f)
                    thumb *= -1f;
                return true;
            }

            return false;
        }

        /// <summary>TEST 494: the grid curls (thumb, index, middle/ring/pinky)
        /// nearest a wrap - per digit, the mean over its joints of how far each
        /// closed from relaxed toward the fist.</summary>
        private static Vec3 CurlsOf(Side side, Vec3[] wrap)
        {
            var c = new float[VrFingerRig.Digits];
            for (int d = 0; d < VrFingerRig.Digits; d++)
            {
                if (!side.Has[d])
                    continue;

                Vec3 r = RelaxOf(side, d), f = FistOf(side, d), a = wrap[d] * (1f / Rad);
                c[d] = (Frac(a.x, r.x, f.x) + Frac(a.y, r.y, f.y) + Frac(a.z, r.z, f.z)) / 3f;
            }

            return new Vec3(c[0], c[1], (c[2] + c[3] + c[4]) / 3f);
        }

        private static float Frac(float angle, float relaxed, float fist) =>
            Math.Abs(fist - relaxed) < 0.001f ? 1f : Math.Max(0f, Math.Min(1f, (angle - relaxed) / (fist - relaxed)));

        private static void LogWrap(string label, string from, VrFingerRig.Handle handle, HoldSet set, int s)
        {
            var text = new System.Text.StringBuilder();
            for (int d = 0; d < VrFingerRig.Digits; d++)
            {
                Vec3 deg = set.Wrap[d] * (1f / Rad);
                text.Append(d == 0 ? " thumb " : d == 1 ? ", index " : d == 2 ? ", middle " : d == 3 ? ", ring " : ", pinky ")
                    .Append(deg.x.ToString("F0", CultureInfo.InvariantCulture)).Append('/')
                    .Append(deg.y.ToString("F0", CultureInfo.InvariantCulture)).Append('/')
                    .Append(deg.z.ToString("F0", CultureInfo.InvariantCulture));
            }

            VrLog.Info(label + "handle from " + from + ", radius " + (handle.Radius * 1000f).ToString("F0", CultureInfo.InvariantCulture)
                       + " mm" + (handle.Points > 0 ? " (" + handle.Points + " vertices near the palm)" : string.Empty) + ", axis "
                       + handle.Axis.x.ToString("F2", CultureInfo.InvariantCulture) + "," + handle.Axis.y.ToString("F2", CultureInfo.InvariantCulture)
                       + "," + handle.Axis.z.ToString("F2", CultureInfo.InvariantCulture) + ", "
                       + (handle.Center - _channel[s]).Length.ToString("F3", CultureInfo.InvariantCulture)
                       + " m from the palm channel; thumb lifted " + (set.Lift.Length / Rad).ToString("F0", CultureInfo.InvariantCulture)
                       + " deg over it; wrapped joint angles (deg, base/middle/end):" + text + "; TEST 497 slid side by side "
                       + Mm(set.Slide[1]) + "/" + Mm(set.Slide[3]) + "/" + Mm(set.Slide[4]) + " mm (index/ring/pinky); TEST 494 grid curls "
                       + set.Curls.x.ToString("F2", CultureInfo.InvariantCulture) + "/" + set.Curls.y.ToString("F2", CultureInfo.InvariantCulture)
                       + "/" + set.Curls.z.ToString("F2", CultureInfo.InvariantCulture) + ".");
        }

        /// <summary>TEST 494: the grid curls (thumb, index, middle/ring/pinky) of
        /// the hold hand s is closing onto - the newest wrap worked out for the
        /// weapon it holds. VrFingers closes the grid toward them while the hold's
        /// copies bake, so the switch to them is no pop.</summary>
        internal static bool TryHoldCurls(int s, out Vec3 curls)
        {
            curls = Vec3.Zero;
            if (!Live || _wantItem[s] == null)
                return false;

            foreach (Piece piece in _pieces)
            {
                Side side = piece.Sides[s];
                HoldSet set = side?.Next != null && side.Next.Wrapped ? side.Next : side?.Hold;
                if (set != null && set.Wrapped && set.Item == _wantItem[s])
                {
                    curls = set.Curls;
                    return true;
                }
            }

            return false;
        }

        private static void RunJobs()
        {
            long t0 = Stopwatch.GetTimestamp();
            // TEST 624: the first tier on the hold budget too - the hands wait on it.
            long budget = (long)((_holdJobs.Count > 0 || !Live ? Math.Max(BuildMs, HoldBuildMs) : BuildMs) * Stopwatch.Frequency / 1000.0);
            do
            {
                (_holdJobs.Count > 0 ? _holdJobs : _jobs).Dequeue()();
            }
            while (_holdJobs.Count + _jobs.Count > 0 && Stopwatch.GetTimestamp() - t0 < budget);

            if (!Live)
            {
                _buildFrames++;
                _buildMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }
        }

        private static void GoLive()
        {
            foreach (Piece piece in _pieces)
            {
                foreach (Mesh lod in piece.Originals)
                {
                    lod.SetVisibilityMask((VisibilityMaskFlags)0);
                    _owned.Add(lod.Name);
                }

                piece.Base.Mesh.SetVisibilityMask(VisibilityMaskFlags.DefaultDynamic);
            }

            Live = true;
            VrLog.Info("TEST 481: " + _built + " finger copies built over " + _buildFrames + " frames ("
                       + _buildMs.ToString("F0", CultureInfo.InvariantCulture) + " ms); your own hand meshes now bend per finger.");
        }

        private static void Show()
        {
            // TEST 662: last tick's retired copies - the renderer data has been rebuilt
            // without them since; one shown again since is on the skeleton and stays.
            foreach (Copy c in _hideNext)
            {
                if (!c.On && c.Mesh != null)
                    c.Mesh.SetVisibilityMask((VisibilityMaskFlags)0);
            }

            _hideNext.Clear();

            foreach (Piece piece in _pieces)
            {
                for (int s = 0; s < 2; s++)
                {
                    Side side = piece.Sides[s];
                    if (side == null)
                        continue;

                    // TEST 495: a bake finished behind the shown hold takes its place.
                    if (side.Next != null && side.Next.Ready)
                    {
                        Retire(_entity, side);
                        side.Hold = side.Next;
                        side.Next = null;
                    }

                    // The hold copies only for the hold they were built for (TEST 495:
                    // or for this weapon while it moves in the hand). TEST 508: at once,
                    // as soon as its closed copy is built - 494 waited for the grid to
                    // close to their curl first ("make sure it's instant").
                    float rest = (VrFingers.Curl[s, 2] + VrFingers.Curl[s, 3] + VrFingers.Curl[s, 4]) / 3f;
                    HoldSet set = side.Hold;
                    bool hold = _wantKey[s] != null && set != null && set.Ready
                                && (set.Key == _wantKey[s] || set.Item == _wantItem[s]);

                    int held = -1;
                    if (hold)
                    {
                        int thumbs = set.Copies.GetLength(0);
                        bool shown = side.ShownHold >= 0;
                        held = VrFingerRig.Key(VrFingers.Close[s, 0], thumbs, shown ? side.ShownHold / HoldKeys : -1) * HoldKeys
                               + VrFingerRig.Key(VrFingers.Close[s, 1], HoldKeys, shown ? side.ShownHold % HoldKeys : -1);
                        if (set.Copies[held / HoldKeys, held % HoldKeys] == null)
                            held = thumbs * HoldKeys - 1;
                    }

                    int grid = -1;
                    if (!hold)
                    {
                        int per = IndexKeys * GridKeys;
                        bool shown = side.ShownGrid >= 0;
                        int t = VrFingerRig.Key(VrFingers.Curl[s, 0] / VrFingers.ThumbSpan, side.Thumbs, shown ? side.ShownGrid / per : -1);
                        int i = VrFingerRig.Key(VrFingers.Curl[s, 1], IndexKeys, shown ? side.ShownGrid % per / GridKeys : -1);
                        int m = VrFingerRig.Key(rest, GridKeys, shown ? side.ShownGrid % GridKeys : -1);

                        // TEST 494: an in-between key not built yet - the coarse one below it;
                        // TEST 624: not built either - the nearest first-tier key.
                        if (side.Grid[t, i, m] == null)
                        {
                            int ci = Coarse(i, IndexKeys), cm = Coarse(m, GridKeys);
                            if (side.Grid[t, ci, cm] != null)
                            {
                                i = ci;
                                m = cm;
                            }
                            else
                            {
                                i = LiveI(i);
                                m = LiveM(m);
                            }
                        }

                        grid = (t * IndexKeys + i) * GridKeys + m;
                    }

                    if (DirectSwap)
                    {
                        SwapDirect(side, held, true);
                        SwapDirect(side, grid, false);
                    }
                    else
                    {
                        // Isolated method keeps the fallback's closure allocation out
                        // of the normal loop, including when the JIT lifts locals.
                        SwapLegacy(side, held, grid);
                    }

                    // TEST 662: a replaced hold's copies come off only now.
                    DropSet(_entity, side.Old);
                    side.Old = null;
                }
            }

            long now = Stopwatch.GetTimestamp();
            if (now < _logAt)
                return;

            _logAt = now + 5 * Stopwatch.Frequency;
            VrLog.Info("TEST 481 curls thumb/index/middle/ring/pinky - left " + Curls(0) + ", right " + Curls(1)
                       + "; on the handle thumb/index - left " + Closes(0) + ", right " + Closes(1) + ".");
        }

        private static void SwapLegacy(Side side, int held, int grid)
        {
            Swap(ref side.ShownHold, held, k => side.Hold?.Copies?[k / HoldKeys, k % HoldKeys]);
            Swap(ref side.ShownGrid, grid,
                 k => side.Grid?[k / (IndexKeys * GridKeys), k % (IndexKeys * GridKeys) / GridKeys, k % GridKeys]);
        }

        private static Copy ShownCopy(Side side, int k, bool hold)
        {
            if (k < 0) return null;
            return hold ? side.Hold?.Copies?[k / HoldKeys, k % HoldKeys]
                        : side.Grid?[k / (IndexKeys * GridKeys), k % (IndexKeys * GridKeys) / GridKeys, k % GridKeys];
        }

        private static void SwapDirect(Side side, int want, bool hold)
        {
            int shown = hold ? side.ShownHold : side.ShownGrid;
            if (shown == want) return;
            long t0 = Stopwatch.GetTimestamp();
            ApplySwap(ShownCopy(side, shown, hold), ShownCopy(side, want, hold));
            if (hold) side.ShownHold = want;
            else side.ShownGrid = want;
            _statSwaps++;
            _statSwapMaxMs = Math.Max(_statSwapMaxMs, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        }

        private static void Swap(ref int shown, int want, Func<int, Copy> copy)
        {
            if (shown == want)
                return;

            long t0 = Stopwatch.GetTimestamp();
            Copy old = shown >= 0 ? copy(shown) : null, next = want >= 0 ? copy(want) : null;
            ApplySwap(old, next);
            shown = want;
            _statSwaps++;
            _statSwapMaxMs = Math.Max(_statSwapMaxMs, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        }

        private static void ApplySwap(Copy old, Copy next)
        {
            if (next?.Mesh != null)
            {
                if (AttachShown && !next.On && next.Meta != null)
                {
                    _entity.AddMultiMeshToSkeleton(next.Meta);
                    next.On = true;
                }

                next.Mesh.SetVisibilityMask(VisibilityMaskFlags.DefaultDynamic);
            }

            if (old?.Mesh != null && old != next)
            {
                if (AttachShown && old.On && old.Meta != null)
                {
                    // TEST 662: off the skeleton now, hidden only at the next swap - a
                    // frame built from renderer data not yet rebuilt still lists this
                    // copy and not the new one; hidden now, that hand had no fingers.
                    _entity.RemoveMultiMeshFromSkeleton(old.Meta);
                    old.On = false;
                    _hideNext.Add(old);
                }
                else
                {
                    old.Mesh.SetVisibilityMask((VisibilityMaskFlags)0);
                }
            }

            if (AttachShown)
                _meshCount = _entity.MultiMeshComponentCount;
        }

        /// <summary>TEST 662: copies taken off the skeleton by the last swap, hidden at
        /// the next (see LateShow).</summary>
        private static readonly List<Copy> _hideNext = new List<Copy>();

        private static void RemoveHold(GameEntity entity, Side side)
        {
            DropSet(entity, side.Hold);
            DropSet(entity, side.Next);
            DropSet(entity, side.Old);
            side.Hold = side.Next = side.Old = null;
            side.Want = null;
            side.ShownHold = -1;
        }

        /// <summary>TEST 662: the hold shown becomes Old - its copy stays on until Show
        /// has swapped to what replaces it; ShownHold forgets it, so no swap touches it.</summary>
        private static void Retire(GameEntity entity, Side side)
        {
            DropSet(entity, side.Old);  // a second replacement before any Show
            side.Old = side.Hold;
            side.Hold = null;
            side.ShownHold = -1;
        }

        /// <summary>A hold's copies off the skeleton and let go of; its jobs still
        /// queued then do nothing.</summary>
        private static void DropSet(GameEntity entity, HoldSet set)
        {
            if (set?.Copies == null)
                return;

            foreach (Copy copy in set.Copies)
                Drop(entity, copy);
            set.Copies = null;
            if (entity != null)
                _meshCount = entity.MultiMeshComponentCount;
        }

        /// <summary>
        /// TEST 486: every copy taken off the skeleton (entity: still in its
        /// running scene, whose hands are shown again; null: the scene is gone)
        /// and every engine object of ours let go of NOW, on this thread.
        /// </summary>
        private static void Forget(GameEntity entity)
        {
            Live = false;
            foreach (Piece piece in _pieces)
            {
                foreach (Mesh lod in piece.Originals)
                {
                    if (entity != null)
                        lod.SetVisibilityMask(VisibilityMaskFlags.DefaultDynamic);
                    Release(lod);
                }

                Release(piece.Source);
                Drop(entity, piece.Base);
                foreach (Side side in piece.Sides)
                {
                    if (side == null)
                        continue;

                    RemoveHold(entity, side);
                    if (side.Grid != null)
                    {
                        foreach (Copy copy in side.Grid)
                            Drop(entity, copy);
                    }

                    side.Grid = null;
                }
            }

            _pieces.Clear();
            _jobs.Clear();
            _holdJobs.Clear();
            _hideNext.Clear();
            _fineLeft = 0;
            _owned.Clear();
            _seat[0] = _seat[1] = Vec3.Zero;
            _seatKey[0] = _seatKey[1] = null;
            if (entity != null)
                _meshCount = entity.MultiMeshComponentCount;
        }

        private static void Drop(GameEntity entity, Copy copy)
        {
            if (copy == null)
                return;

            if (entity != null && copy.Meta != null && copy.On)
                entity.RemoveMultiMeshFromSkeleton(copy.Meta);
            copy.On = false;
            Release(copy.Meta);
            Release(copy.Mesh);
            copy.Meta = null;
            copy.Mesh = null;
        }

        /// <summary>TEST 486: one engine reference let go of now, on the calling
        /// (main) thread, instead of by its wrapper's finalizer on the
        /// finalizer thread. Safe twice.</summary>
        private static void Release(NativeObject o)
        {
            try
            {
                o?.ManualInvalidate();
            }
            catch (Exception)
            {
                // Its scene took it already.
            }
        }

        private static void Finish(Mesh mesh, Mesh source, string tag)
        {
            mesh.RecomputeBoundingBox();
            mesh.Color = source.Color | 0xFF000000u;   // TEST 760: never bake a FadeIn alpha
            mesh.Color2 = source.Color2;
            mesh.SetLocalFrame(source.GetLocalFrame());
            mesh.Name = Prefix + source.Name + "_" + tag;
        }

        /// <summary>Hidden, on the skeleton like the body's own meshes (TEST 505:
        /// or off it until shown).</summary>
        private static MetaMesh Hang(GameEntity entity, Mesh mesh, string name, bool attach)
        {
            MetaMesh meta = MetaMesh.CreateMetaMesh(Prefix + name);
            meta.AddMesh(mesh);
            mesh.SetVisibilityMask((VisibilityMaskFlags)0);
            if (attach)
            {
                entity.AddMultiMeshToSkeleton(meta);
                _meshCount = entity.MultiMeshComponentCount;
            }

            return meta;
        }

        private static sbyte ItemOf(Skeleton skeleton, sbyte hand)
        {
            int count = skeleton.GetBoneCount();
            for (sbyte b = 0; b < count; b++)
            {
                if (skeleton.GetParentBoneIndex(b) == hand)
                    return b;
            }

            return -1;
        }

        private static string K(Vec3 v, float step) =>
            Math.Round(v.x / step).ToString(CultureInfo.InvariantCulture) + ","
            + Math.Round(v.y / step).ToString(CultureInfo.InvariantCulture) + ","
            + Math.Round(v.z / step).ToString(CultureInfo.InvariantCulture) + ";";

        private static string Curls(int s)
        {
            var text = new System.Text.StringBuilder();
            for (int d = 0; d < VrFingerRig.Digits; d++)
            {
                if (d > 0)
                    text.Append('/');
                text.Append(VrFingers.Curl[s, d].ToString("F2", CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        private static string Closes(int s) =>
            VrFingers.Close[s, 0].ToString("F2", CultureInfo.InvariantCulture) + "/"
            + VrFingers.Close[s, 1].ToString("F2", CultureInfo.InvariantCulture);

        private static string Cm(float m) => (m * 100f).ToString("F1", CultureInfo.InvariantCulture);

        private static string Mm(Vec3 v) => (v.Length * 1000f).ToString("F0", CultureInfo.InvariantCulture);

        private static string Deg(float rad) => (rad / Rad).ToString("F0", CultureInfo.InvariantCulture);

        private static Vec3 V3(string key, float x, float y, float z)
        {
            float[] v = VrConfig.Floats(key, new[] { x, y, z });
            return new Vec3(v[0], v[1], v[2]);
        }
    }
}
