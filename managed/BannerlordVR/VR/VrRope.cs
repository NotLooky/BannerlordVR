using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using BannerlordVR.Interop;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 471-471d: the reins on the player's horse, built the way vanilla's
    /// are, as two Verlet chains - one from each corner of the mouth (the head
    /// bone plus rein_bit_offset in that bone's frame, rein_bit_width_m apart) to
    /// vanilla's own rein handles (Monster.ReinHandleBone and its ReinHandleLeft/
    /// RightLocalPosition, raised rein_rest_up_m) at rest, or, held (VrRein),
    /// both to the hand. Simulated in the HORSE's frame, so a gallop does not whip
    /// them; the bones carry them with the animation.
    ///
    /// TEST 471d, after the 14:08 photo ("no rope collide, not the vanilla rope,
    /// vanilla still visible"):
    ///   - The collision is vanilla's own body, read from its definition:
    ///     Modules/Native/ModuleData/cloth_bodies.xml, body
    ///     Monster.ReinCollisionBody ('horse_neck_head_rein' - 9 capsules over
    ///     the neck, withers and shoulders, in the skeleton's rest space, each end
    ///     skinned to its bones by weight). 471c asked PhysicsShape for it, which
    ///     has no such resource, and fell back to three thin bone bodies.
    ///   - A taut rein is no longer laid straight THROUGH the neck: the chain
    ///     stretches to the span and still collides.
    ///   - Vanilla's reins are hidden through ReinsClothPatch, which keeps our
    ///     own copy of the rope mesh the native rein system draws.
    ///   - Vanilla's rein material and UVs are kept on static textured sections.
    /// </summary>
    internal static class VrRope
    {
        private static readonly bool On = VrConfig.Bool("rein", true);
        private static readonly bool Probe = VrConfig.Bool("rein_probe", true);
        private static readonly bool HideVanilla = VrConfig.Bool("rein_hide_vanilla", true);
        private static readonly bool CollideBody = VrConfig.Bool("rein_collide_body", true);
        private static readonly bool CollideBones = VrConfig.Bool("rein_collide_bones", false);
        private static readonly bool VanillaLook = VrConfig.Bool("rein_vanilla_mesh", true);
        private static readonly float Slack = VrConfig.Float("rein_slack", 1.35f);
        private static readonly float BitWidth = VrConfig.Float("rein_bit_width_m", 0.16f);
        /// <summary>TEST 471f, the user: "the rope is not connected before I grab
        /// it". At rest each rein ended at its own handle 12 cm ABOVE the withers -
        /// two loose ends in the air - and vanilla's capsules are thinner than the
        /// barded neck, so the reins lay inside it. Now the two reins JOIN at rest,
        /// on the withers' surface (the handles' midpoint pushed out of the body,
        /// plus rein_rest_up_m); the body is padded by rein_collide_pad_m (times the
        /// horse's scale) so the reins lie on the armour; and at rest they are
        /// rein_rest_slack long, so they hang along the neck, not through it.</summary>
        private static readonly float RestUp = VrConfig.Float("rein_rest_up_m", 0.01f);
        private static readonly float Pad = VrConfig.Float("rein_collide_pad_m", 0.06f);
        private static readonly float RestSlack = VrConfig.Float("rein_rest_slack", 1.10f);
        private static readonly float NeckRoot = VrConfig.Float("rein_neck_r_root", 0.24f);
        private static readonly float NeckHead = VrConfig.Float("rein_neck_r_head", 0.12f);
        private static readonly float Width = VrConfig.Float("rein_width_m", 0.028f);
        private static readonly float Thick = VrConfig.Float("rein_thick_m", 0.007f);
        private static readonly float Reach = VrConfig.Float("rein_reach_m", 0.15f);
        private static readonly Vec3 BitOffset = Parse(VrConfig.String("rein_bit_offset", "0.40,0.06,0"));

        internal const int N = 12;
        private const int CurveSteps = 3;
        private const int DrawLinks = (N - 1) * CurveSteps;
        private const int Sides = 2;
        private const float Damping = 0.98f;
        private const int Iterations = 12;
        private const float ReturnS = 0.3f;
        private const int MaxColliders = 32;
        private static readonly Vec3 Leather = new Vec3(0.30f, 0.19f, 0.11f);

        /// <summary>One end of a collider: up to three bones, each with the point
        /// in its own frame and a weight (normalised).</summary>
        private struct Skin
        {
            public int Count;
            public sbyte B0, B1, B2;
            public Vec3 L0, L1, L2;
            public float W0, W1, W2;
        }

        /// <summary>A tapered capsule (a sphere when its ends meet) riding bones;
        /// radii in the horse's own (unscaled) units.</summary>
        private struct Collider
        {
            public Skin A;
            public Skin B;
            public float RA;
            public float RB;
            public string From;
        }

        private static readonly Vec3[][] _p = { new Vec3[N], new Vec3[N] };
        private static readonly Vec3[][] _prev = { new Vec3[N], new Vec3[N] };
        private static readonly Vec3[][] _world = { new Vec3[N], new Vec3[N] };
        private static readonly float[] _segLen = new float[Sides];
        private static readonly float[] _restLen = new float[Sides];
        private static readonly bool[] _pin = new bool[N];
        private static readonly Vec3[] _end = new Vec3[Sides];
        private static readonly Vec3[] _returnFrom = new Vec3[Sides];
        private static readonly GameEntity[] _seg = new GameEntity[Sides * DrawLinks];
        private static readonly List<Collider> _colliders = new List<Collider>();
        private static readonly Vec3[] _capA = new Vec3[MaxColliders];
        private static readonly Vec3[] _capB = new Vec3[MaxColliders];
        private static readonly float[] _capRA = new float[MaxColliders];
        private static readonly float[] _capRB = new float[MaxColliders];
        private static readonly List<KeyValuePair<MetaMesh, VisibilityMaskFlags>> _hiddenMeta =
            new List<KeyValuePair<MetaMesh, VisibilityMaskFlags>>();
        private static readonly List<KeyValuePair<Mesh, VisibilityMaskFlags>> _hiddenMesh =
            new List<KeyValuePair<Mesh, VisibilityMaskFlags>>();

        /// <summary>TEST 471d: each horse's rein and rope meshes, as the native
        /// rein system got them (ReinsClothPatch), by the visuals' pointer.</summary>
        private static readonly Dictionary<UIntPtr, (Mission Mission, MetaMesh Rein, MetaMesh Rope)> _vanilla =
            new Dictionary<UIntPtr, (Mission, MetaMesh, MetaMesh)>();

        private static Mission _mission;
        private static Agent _mount;
        private static bool _haveWorld;
        private static bool _held;
        private static float _return = 1f;
        private static float _lastTime;
        private static float _taut;
        private static long _buzzAt;
        private static long _logAt;
        private static long _checkHideAt;
        private static bool _probed;
        private static bool _broken;
        private static int _dropped;
        private static int _resets;

        private static string _ropeName;
        private static MetaMesh _ropePiece;
        private static int _ropeAxis = -1;
        private static Vec3 _ropeMin;
        private static Vec3 _ropeMax;

        static VrRope()
        {
            _pin[0] = _pin[N - 1] = true;
        }

        /// <summary>From ReinsClothPatch, as each horse's visuals get their reins.</summary>
        internal static void RememberVanilla(MBAgentVisuals visuals, MetaMesh rein, MetaMesh rope)
        {
            _vanilla[visuals.Pointer] = (Mission.Current, rein, rope);
        }

        /// <summary>A bone's world frame, bounded - an index past the skeleton's
        /// bone count is an unchecked native read.</summary>
        internal static bool BoneWorld(Agent agent, sbyte bone, out MatrixFrame world)
        {
            world = MatrixFrame.Identity;
            MBAgentVisuals visuals = agent?.AgentVisuals;
            Skeleton skeleton = visuals?.GetSkeleton();
            GameEntity entity = visuals?.GetEntity();
            if (skeleton == null || entity == null || bone < 0 || bone >= skeleton.GetBoneCount())
                return false;

            world = entity.GetGlobalFrame().TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(bone));
            return true;
        }

        /// <summary>The distance from <paramref name="at"/> to the nearest point
        /// of either rein; true within reach.</summary>
        internal static bool Nearest(Vec3 at, out float distance)
        {
            distance = float.MaxValue;
            if (!On || _broken || !_haveWorld || _mount == null)
                return false;

            for (int side = 0; side < Sides; side++)
            {
                for (int i = 1; i < N; i++)
                    distance = Math.Min(distance, _world[side][i].Distance(at));
            }

            return distance <= Reach;
        }

        internal static void Hold() => _held = true;

        internal static void Let()
        {
            if (!_held)
                return;

            _held = false;
            _return = 0f;
            _returnFrom[0] = _returnFrom[1] = _end[0];
        }

        /// <summary>From AfterMissionTick, after VrRein.Tick.</summary>
        internal static void Tick()
        {
            if (!On || _broken)
                return;

            try
            {
                Mission mission = Mission.Current;
                if (!ReferenceEquals(mission, _mission))
                    Forget(mission);

                Agent agent = mission?.MainAgent;
                Agent mount = agent != null && agent.HasMount ? agent.MountAgent : null;
                if (!ReferenceEquals(mount, _mount))
                {
                    Detach();
                    if (mount != null)
                        Attach(mount, mission.Scene);
                }

                if (_mount == null)
                    return;

                float now = mission.CurrentTime;
                float dt = Math.Min(0.05f, Math.Max(0f, now - _lastTime));
                _lastTime = now;

                Simulate(dt);
                Draw();
                CheckHidden();
            }
            catch (Exception ex)
            {
                try { Detach(); } catch (Exception) { }
                _broken = true;
                _haveWorld = false;
                VrLog.Error("TEST 471: the reins failed; they are off for this session (the horse stays rideable "
                            + "with the stick).", ex);
            }
        }

        private static void Simulate(float dt)
        {
            if (!Ends(_mount, out MatrixFrame frame, out float scale, out Vec3 bitL, out Vec3 bitR, out Vec3 restL,
                      out Vec3 restR))
                return;

            int caps = Colliders(frame, scale);
            restL = restR = OnSurface((restL + restR) * 0.5f, scale, caps);

            if (_held)
            {
                _end[0] = _end[1] = frame.TransformToLocal(VrRein.HandWorld);
            }
            else
            {
                _return = Math.Min(1f, _return + (dt > 0f ? dt / ReturnS : 0f));
                float t = _return * _return * (3f - 2f * _return);
                _end[0] = _returnFrom[0] * (1f - t) + restL * t;
                _end[1] = _returnFrom[1] * (1f - t) + restR * t;
            }

            Vec3 gravity = frame.rotation.TransformToLocal(new Vec3(0f, 0f, -9.81f));
            _taut = 0f;

            for (int side = 0; side < Sides; side++)
            {
                Vec3[] p = _p[side];
                Vec3[] prev = _prev[side];
                Vec3 bit = side == 0 ? bitL : bitR;
                Vec3 end = _end[side];
                p[0] = bit;
                p[N - 1] = end;

                // Past its length a rein is taut: its links stretch to the span,
                // and it still lies around the neck rather than through it.
                // Held, the reins' full length; let go, it eases to the rest length.
                float seg = _held ? _segLen[side] : _segLen[side] + (_restLen[side] - _segLen[side]) * _return;
                float span = (end - bit).Length;
                float length = seg * (N - 1);
                if (span > length)
                {
                    _taut = Math.Max(_taut, span / Math.Max(length, 1e-3f) - 1f);
                    seg = span / (N - 1);
                }

                if (dt > 0f)
                    Step(p, prev, _pin, gravity, dt, seg, _capA, _capB, _capRA, _capRB, caps, Iterations);

                // TEST 471e: never a rein thrown off to nowhere - start it over.
                bool lost = false;
                for (int i = 1; i < N - 1 && !lost; i++)
                    lost = !Near(p[i]);
                if (lost)
                {
                    for (int i = 0; i < N; i++)
                        p[i] = prev[i] = bit + (end - bit) * (i / (float)(N - 1));
                    if (_resets++ < 3)
                        VrLog.Warn("TEST 471e: a rein was thrown off the horse; it starts over ("
                                   + _dropped + " collider placement(s) dropped so far).");
                }

                prev[0] = p[0];
                prev[N - 1] = p[N - 1];
                for (int i = 0; i < N; i++)
                    _world[side][i] = frame.TransformToParent(p[i]);
            }

            _haveWorld = true;
            Buzz();
            Report();
        }

        /// <summary>The colliders this tick, in the horse's frame; their count.</summary>
        private static int Colliders(MatrixFrame frame, float scale)
        {
            int n = 0;
            foreach (Collider c in _colliders)
            {
                if (n >= MaxColliders)
                    break;
                if (!Place(c.A, out Vec3 a) || !Place(c.B, out Vec3 b))
                    continue;

                Vec3 la = frame.TransformToLocal(a), lb = frame.TransformToLocal(b);
                if (!Near(la) || !Near(lb))
                {
                    _dropped++;
                    continue;
                }

                _capA[n] = la;
                _capB[n] = lb;
                _capRA[n] = (c.RA + Pad) * scale;
                _capRB[n] = (c.RB + Pad) * scale;
                n++;
            }
            return n;
        }

        /// <summary>TEST 471f: the reins' rest - the handles' midpoint lifted onto
        /// the padded body's surface, then rein_rest_up_m above it.</summary>
        private static Vec3 OnSurface(Vec3 p, float scale, int caps)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                for (int c = 0; c < caps; c++)
                    p = OutOfCapsule(p, _capA[c], _capB[c], _capRA[c], _capRB[c]);
            }
            return p + new Vec3(0f, 0f, RestUp * scale);
        }

        /// <summary>TEST 471e: within 5 m of the horse's origin, and a number.</summary>
        private static bool Near(Vec3 local) => local.LengthSquared < 25f && !float.IsNaN(local.LengthSquared);

        /// <summary>A skinned point in the world: its bones' blend.</summary>
        private static bool Place(Skin s, out Vec3 world)
        {
            world = Vec3.Zero;
            if (s.Count == 0)
                return false;

            for (int k = 0; k < s.Count; k++)
            {
                sbyte bone = k == 0 ? s.B0 : k == 1 ? s.B1 : s.B2;
                if (!BoneWorld(_mount, bone, out MatrixFrame f))
                    return false;
                Vec3 local = k == 0 ? s.L0 : k == 1 ? s.L1 : s.L2;
                float w = k == 0 ? s.W0 : k == 1 ? s.W1 : s.W2;
                world += f.TransformToParent(local) * w;
            }
            return true;
        }

        /// <summary>
        /// One Verlet step: inertia with damping and gravity, then the distance
        /// constraints and the capsules (tapered: radius ra at a, rb at b),
        /// <paramref name="iterations"/> times. Pinned points never move. Pure; the
        /// math tests call it.
        /// </summary>
        internal static void Step(Vec3[] p, Vec3[] prev, bool[] pinned, Vec3 gravity, float dt, float seg,
                                  Vec3[] capA, Vec3[] capB, float[] capRA, float[] capRB, int caps, int iterations)
        {
            int n = p.Length;
            Vec3 g = gravity * (dt * dt);
            for (int i = 0; i < n; i++)
            {
                if (pinned[i])
                    continue;

                Vec3 v = (p[i] - prev[i]) * Damping;
                prev[i] = p[i];
                p[i] = p[i] + v + g;
            }

            for (int it = 0; it < iterations; it++)
            {
                for (int i = 0; i + 1 < n; i++)
                {
                    float wa = pinned[i] ? 0f : 1f;
                    float wb = pinned[i + 1] ? 0f : 1f;
                    if (wa + wb == 0f)
                        continue;

                    Vec3 delta = p[i + 1] - p[i];
                    float d = delta.Length;
                    if (d < 1e-6f)
                        continue;

                    Vec3 fix = delta * ((d - seg) / (d * (wa + wb)));
                    p[i] = p[i] + fix * wa;
                    p[i + 1] = p[i + 1] - fix * wb;
                }

                for (int i = 0; i < n; i++)
                {
                    if (pinned[i])
                        continue;
                    for (int c = 0; c < caps; c++)
                        p[i] = OutOfCapsule(p[i], capA[c], capB[c], capRA[c], capRB[c]);
                }
            }
        }

        /// <summary>A point pushed to the surface of the tapered capsule a-b
        /// (radius rA at a, rB at b) if it is inside. Pure.</summary>
        internal static Vec3 OutOfCapsule(Vec3 p, Vec3 a, Vec3 b, float rA, float rB)
        {
            Vec3 ab = b - a;
            float len2 = ab.LengthSquared;
            float t = len2 > 1e-8f ? Math.Max(0f, Math.Min(1f, Vec3.DotProduct(p - a, ab) / len2)) : 0f;
            Vec3 c = a + ab * t;
            float r = rA + (rB - rA) * t;
            Vec3 out_ = p - c;
            float d = out_.Length;
            if (d >= r)
                return p;
            if (d < 1e-6f)
                return c + new Vec3(0f, 0f, r);
            return c + out_ * (r / d);
        }

        /// <summary>The ends of a capsule from cloth_bodies.xml: its frame's first
        /// axis, centred on the frame's origin (plus the capsule's own origin
        /// offset), length long. Pure.</summary>
        internal static void CapsuleEnds(float[] frame, Vec3 origin, float length, out Vec3 p0, out Vec3 p1)
        {
            Vec3 s = new Vec3(frame[0], frame[1], frame[2]);
            Vec3 f = new Vec3(frame[4], frame[5], frame[6]);
            Vec3 u = new Vec3(frame[8], frame[9], frame[10]);
            Vec3 o = new Vec3(frame[12], frame[13], frame[14]);
            Vec3 centre = o + s * origin.x + f * origin.y + u * origin.z;
            Vec3 axis = s.NormalizedCopy();
            p0 = centre - axis * (0.5f * length);
            p1 = centre + axis * (0.5f * length);
        }

        /// <summary>The horse's frame (orthonormal) and its scale, and in that
        /// frame the two bit rings and vanilla's two rein handles.</summary>
        private static bool Ends(Agent mount, out MatrixFrame frame, out float scale, out Vec3 bitL, out Vec3 bitR,
                                 out Vec3 restL, out Vec3 restR)
        {
            frame = MatrixFrame.Identity;
            scale = 1f;
            bitL = bitR = restL = restR = Vec3.Zero;

            GameEntity entity = mount.AgentVisuals?.GetEntity();
            Monster m = mount.Monster;
            if (entity == null || m == null
                || !BoneWorld(mount, m.HeadLookDirectionBoneIndex, out MatrixFrame headBone)
                || !BoneWorld(mount, m.ReinHandleBoneIndex, out MatrixFrame restBone))
                return false;

            frame = entity.GetGlobalFrame();
            scale = Math.Max(0.5f, frame.rotation.s.Length);
            frame.rotation.Orthonormalize();

            Vec3 bit = frame.TransformToLocal(headBone.TransformToParent(BitOffset));
            Vec3 side = new Vec3(0.5f * BitWidth * scale, 0f, 0f);
            bitL = bit - side;
            bitR = bit + side;

            Vec3 a = frame.TransformToLocal(restBone.TransformToParent(m.ReinHandleLeftLocalPosition));
            Vec3 b = frame.TransformToLocal(restBone.TransformToParent(m.ReinHandleRightLocalPosition));

            // Which handle is on which side is the bone's business; the left rein
            // goes to the one with the smaller x.
            restL = a.x <= b.x ? a : b;
            restR = a.x <= b.x ? b : a;
            return true;
        }

        private static void Attach(Agent mount, Scene scene)
        {
            _mount = mount;
            _held = false;
            _return = 1f;
            _haveWorld = false;
            _lastTime = Mission.Current.CurrentTime;

            if (!Ends(mount, out MatrixFrame frame, out float scale, out Vec3 bitL, out Vec3 bitR, out Vec3 restL,
                      out Vec3 restR))
            {
                VrLog.Warn("TEST 471: this mount has no head/rein bones; no reins.");
                return;
            }

            string bodies = BuildColliders(mount);
            Vec3 join = OnSurface((restL + restR) * 0.5f, scale, Colliders(frame, scale));

            _end[0] = _end[1] = _returnFrom[0] = _returnFrom[1] = join;
            for (int side = 0; side < Sides; side++)
            {
                Vec3 bit = side == 0 ? bitL : bitR;
                float span = Math.Max(0.3f, (join - bit).Length);
                _segLen[side] = Slack * span / (N - 1);
                _restLen[side] = Math.Min(RestSlack, Slack) * span / (N - 1);

                // Start along the neck's side, lifted, so the first step drapes it
                // rather than dragging it out of the neck.
                Vec3 outward = new Vec3(side == 0 ? -0.15f * scale : 0.15f * scale, 0f, 0.1f * scale);
                for (int i = 0; i < N; i++)
                {
                    float t = i / (float)(N - 1);
                    _p[side][i] = _prev[side][i] = bit * (1f - t) + join * t + outward * (4f * t * (1f - t));
                }
            }

            string look = Look(mount);

            for (int i = 0; i < _seg.Length; i++)
                if (_seg[i] == null)
                    _seg[i] = _ropeAxis >= 0 ? CreateRopePiece(scene) : CreateStrap(scene);

            if (HideVanilla)
            {
                HideReins(mount);
                _checkHideAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            }

            if (Probe && !_probed)
            {
                _probed = true;
                Describe(mount, frame, scale, bitL, bitR, restL, restR);
            }

            VrLog.Info("TEST 471f reins: on '" + mount.Name + "', joined at rest on the withers " + V(join) + ", 2 x "
                       + F(_restLen[0] * (N - 1)) + " m at rest / " + F(_segLen[0] * (N - 1)) + " m held, scale "
                       + F(scale) + "; colliders: " + bodies + "; look: " + look + "; hidden " + _hiddenMeta.Count
                       + " vanilla metamesh(es), " + _hiddenMesh.Count + " mesh(es).");
        }

        /// <summary>The colliders: vanilla's rein body from cloth_bodies.xml; the
        /// horse's own neck and head bone bodies (rein_collide_bones); the 471b
        /// tapered neck when neither gave any.</summary>
        private static string BuildColliders(Agent mount)
        {
            _colliders.Clear();
            Monster m = mount.Monster;
            Skeleton skeleton = mount.AgentVisuals.GetSkeleton();
            sbyte count = skeleton.GetBoneCount();
            bool Valid(sbyte b) => b >= 0 && b < count;
            var text = new StringBuilder();

            if (CollideBody && !string.IsNullOrEmpty(m.ReinCollisionBody))
                text.Append(LoadBody(m.ReinCollisionBody, skeleton));

            if (CollideBones)
            {
                int added = 0;
                foreach (sbyte bone in new[] { m.ReinCollision1BoneIndex, m.ReinCollision2BoneIndex,
                                               m.HeadLookDirectionBoneIndex })
                {
                    if (!Valid(bone))
                        continue;
                    CapsuleData d = default;
                    skeleton.GetBoneBody(bone, ref d);
                    if (d.Radius < 0.01f)
                        continue;
                    _colliders.Add(new Collider { A = Bound(bone, d.P1), B = Bound(bone, d.P2), RA = d.Radius,
                                                  RB = d.Radius, From = "bone" });
                    added++;
                }
                text.Append(text.Length > 0 ? ", " : "").Append(added).Append(" bone bod(ies)");
            }

            if (_colliders.Count == 0 && Valid(m.NeckRootBoneIndex) && Valid(m.HeadLookDirectionBoneIndex))
            {
                _colliders.Add(new Collider { A = Bound(m.NeckRootBoneIndex, Vec3.Zero),
                                              B = Bound(m.HeadLookDirectionBoneIndex, Vec3.Zero),
                                              RA = NeckRoot, RB = NeckHead, From = "fallback" });
                text.Append(text.Length > 0 ? ", " : "").Append("the 471b tapered neck (fallback)");
            }

            return text.ToString();
        }

        /// <summary>A point already in a bone's own frame.</summary>
        private static Skin Bound(sbyte bone, Vec3 local) =>
            new Skin { Count = 1, B0 = bone, L0 = local, W0 = 1f };

        /// <summary>
        /// Vanilla's rein body, from Modules/Native/ModuleData/cloth_bodies.xml:
        /// each capsule's ends in the skeleton's REST space, carried into each of
        /// their bones' rest frames, weights normalised - so the ends follow the
        /// neck and head as vanilla's do.
        /// </summary>
        private static string LoadBody(string name, Skeleton skeleton)
        {
            string path = System.IO.Path.Combine(BasePath.Name, "Modules", "Native", "ModuleData", "cloth_bodies.xml");
            if (!System.IO.File.Exists(path))
                return "no " + path;

            var doc = new XmlDocument();
            doc.Load(path);
            XmlNode body = null;
            foreach (XmlNode node in doc.GetElementsByTagName("body"))
            {
                if (node.Attributes?["name"]?.Value == name)
                {
                    body = node;
                    break;
                }
            }
            if (body == null)
                return "no body '" + name + "' in cloth_bodies.xml";

            var bones = new Dictionary<string, sbyte>(StringComparer.OrdinalIgnoreCase);
            sbyte count = skeleton.GetBoneCount();
            for (sbyte i = 0; i < count && i >= 0; i++)
            {
                string boneName = skeleton.GetBoneName(i);
                if (boneName != null)
                    bones[boneName] = i;
            }

            MatrixFrame[] rest = RestFrames(skeleton, body.Attributes?["owner_skeleton"]?.Value, out string source);

            int added = 0, skipped = 0;
            foreach (XmlNode cap in body.SelectNodes("capsules/capsule"))
            {
                float[] frame = Floats(cap.Attributes?["frame"]?.Value, 16);
                float[] origin = Floats(cap.Attributes?["origin"]?.Value, 3);
                float length = Floats(cap.Attributes?["length"]?.Value, 1)[0];
                XmlNodeList points = cap.SelectNodes("points/point");
                if (frame == null || points.Count < 2)
                {
                    skipped++;
                    continue;
                }

                CapsuleEnds(frame, new Vec3(origin[0], origin[1], origin[2]), length, out Vec3 p0, out Vec3 p1);
                if (!Skinned(points[0], p0, bones, rest, out Skin a, out float ra)
                    || !Skinned(points[1], p1, bones, rest, out Skin b, out float rb))
                {
                    skipped++;
                    continue;
                }

                _colliders.Add(new Collider { A = a, B = b, RA = ra, RB = rb,
                                              From = cap.Attributes?["name"]?.Value ?? "capsule" });
                added++;
            }

            return "vanilla body '" + name + "' " + added + " capsule(s)" + (skipped > 0 ? ", " + skipped + " skipped" : "")
                   + ", rest pose from " + source;
        }

        /// <summary>
        /// TEST 471e: the skeleton's REST pose, bone by bone. The agent skeleton's
        /// GetBoneEntitialRestFrame returned garbage in the 14:23 run (colliders at
        /// 1e29 m - every rein point was thrown off to infinity: "no rope at all"),
        /// so the first source whose frames are all sane wins: a fresh skeleton of
        /// the body's own model (CreateFromModel, never animated); the agent's rest
        /// frames without bone mapping; its local rest frames chained to the root;
        /// last, the pose the horse is in now.
        /// </summary>
        private static MatrixFrame[] RestFrames(Skeleton skeleton, string model, out string source)
        {
            sbyte count = skeleton.GetBoneCount();
            var frames = new MatrixFrame[count];

            try
            {
                Skeleton fresh = string.IsNullOrEmpty(model) ? null : Skeleton.CreateFromModel(model);
                if (fresh != null && fresh.GetBoneCount() == count)
                {
                    fresh.ForceUpdateBoneFrames();
                    for (sbyte i = 0; i < count; i++)
                        frames[i] = fresh.GetBoneEntitialFrameWithIndex(i);
                    if (AllSane(frames))
                    {
                        source = "a fresh '" + model + "'";
                        return frames;
                    }
                }
            }
            catch (Exception)
            {
                // The next source.
            }

            for (sbyte i = 0; i < count; i++)
                frames[i] = skeleton.GetBoneEntitialRestFrame(i, false);
            if (AllSane(frames))
            {
                source = "the agent's rest frames (unmapped)";
                return frames;
            }

            for (sbyte i = 0; i < count; i++)
            {
                MatrixFrame local = skeleton.GetBoneLocalRestFrame(i, true);
                sbyte parent = skeleton.GetParentBoneIndex(i);
                frames[i] = parent >= 0 && parent < i ? frames[parent].TransformToParent(local) : local;
            }
            if (AllSane(frames))
            {
                source = "the agent's local rest frames, chained";
                return frames;
            }

            for (sbyte i = 0; i < count; i++)
                frames[i] = skeleton.GetBoneEntitialFrameWithIndex(i);
            source = "the pose at mounting (no sane rest frames)";
            return frames;
        }

        /// <summary>Unit-ish axes, an origin within 5 m, nothing NaN. Pure.</summary>
        internal static bool Sane(MatrixFrame f)
        {
            float s = f.rotation.s.Length, fl = f.rotation.f.Length, u = f.rotation.u.Length, o = f.origin.Length;
            return s > 0.5f && s < 2f && fl > 0.5f && fl < 2f && u > 0.5f && u < 2f && o < 5f
                   && !float.IsNaN(s + fl + u + o);
        }

        private static bool AllSane(MatrixFrame[] frames)
        {
            foreach (MatrixFrame f in frames)
            {
                if (!Sane(f))
                    return false;
            }
            return frames.Length > 0;
        }

        private static bool Skinned(XmlNode point, Vec3 rest, Dictionary<string, sbyte> bones, MatrixFrame[] restFrames,
                                    out Skin skin, out float radius)
        {
            skin = default;
            radius = Floats(point.Attributes?["radius"]?.Value, 1)[0];

            float total = 0f;
            foreach (XmlNode bone in point.SelectNodes("bones/bone"))
            {
                if (skin.Count >= 3)
                    break;
                if (!bones.TryGetValue(bone.Attributes?["name"]?.Value ?? "", out sbyte index) || index >= restFrames.Length)
                    continue;

                float w = Floats(bone.Attributes?["weight"]?.Value, 1)[0];
                if (w <= 0f)
                    continue;

                Vec3 local = restFrames[index].TransformToLocal(rest);
                if (skin.Count == 0) { skin.B0 = index; skin.L0 = local; skin.W0 = w; }
                else if (skin.Count == 1) { skin.B1 = index; skin.L1 = local; skin.W1 = w; }
                else { skin.B2 = index; skin.L2 = local; skin.W2 = w; }
                skin.Count++;
                total += w;
            }

            if (skin.Count == 0 || total <= 0f)
                return false;

            skin.W0 /= total;
            skin.W1 /= total;
            skin.W2 /= total;
            return true;
        }

        private static float[] Floats(string text, int n)
        {
            var v = new float[n];
            if (text == null)
                return n == 16 ? null : v;

            string[] parts = text.Split(',');
            if (n == 16 && parts.Length < 16)
                return null;
            for (int i = 0; i < n && i < parts.Length; i++)
                float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]);
            return v;
        }

        private static string Look(Agent mount)
        {
            _ropeName = null;
            _ropePiece = null;
            _ropeAxis = -1;
            if (!VanillaLook)
            {
                return "strap (rein_vanilla_mesh = 0)";
            }
            ItemObject item = mount.SpawnEquipment[EquipmentIndex.HorseHarness].Item;
            string text = item?.ArmorComponent?.ReinsRopeMesh;
            MetaMesh metaMesh = (string.IsNullOrEmpty(text) ? null : MetaMesh.GetCopy(text, showErrors: false, mayReturnNull: true));
            if (metaMesh == null)
            {
                return "strap (the harness '" + item?.StringId + "' has no rope mesh '" + text + "')";
            }
            metaMesh.RecomputeBoundingBox(recomputeMeshes: true);
            BoundingBox boundingBox = metaMesh.GetBoundingBox();
            Vec3 v = boundingBox.max - boundingBox.min;
            float[] array = new float[3] { v.x, v.y, v.z };
            int num = ((!(array[0] >= array[1]) || !(array[0] >= array[2])) ? ((array[1] >= array[2]) ? 1 : 2) : 0);
            float num2 = Math.Max(array[(num + 1) % 3], array[(num + 2) % 3]);
            string text2 = "'" + text + "' " + V(v) + " m, " + metaMesh.MeshCount + " mesh(es)";
            if (array[num] < 3f * num2 || array[num] < 0.05f)
            {
                _ropePiece = ReinPiece(metaMesh);
                if (_ropePiece == null)
                {
                    return "strap (" + text2 + " - no usable rein section)";
                }
                _ropePiece.RecomputeBoundingBox(recomputeMeshes: true);
                BoundingBox boundingBox2 = _ropePiece.GetBoundingBox();
                _ropeAxis = 1;
                _ropeMin = boundingBox2.min;
                _ropeMax = boundingBox2.max;
                return "vanilla textured rein section from " + text2;
            }
            _ropeName = text;
            _ropeAxis = num;
            _ropeMin = boundingBox.min;
            _ropeMax = boundingBox.max;
            return "vanilla rope " + text2 + ", along " + "xyz"[num];
        }

        private static GameEntity CreateRopePiece(Scene scene)
        {
            GameEntity gameEntity = GameEntity.CreateEmptyDynamic(scene, isModifiableFromEditor: false);
            MetaMesh metaMesh = ((_ropePiece != null) ? _ropePiece.CreateCopy() : MetaMesh.GetCopy(_ropeName, showErrors: false, mayReturnNull: true));
            if (metaMesh != null)
            {
                gameEntity.AddMultiMesh(metaMesh);
            }
            gameEntity.SetVisibilityExcludeParents(visible: false);
            return gameEntity;
        }

        private static MetaMesh ReinPiece(MetaMesh rope)
        {
            for (int i = 0; i < rope.MeshCount; i++)
            {
                Mesh meshAtIndex = rope.GetMeshAtIndex(i);
                if (meshAtIndex == null || (meshAtIndex.Name ?? string.Empty).Contains(".lod"))
                {
                    continue;
                }
                Material material = meshAtIndex.GetMaterial();
                if (material == null)
                {
                    continue;
                }
                Mesh mesh = Mesh.CreateMeshWithMaterial(material);
                ManagedMeshEditOperations managedMeshEditOperations = ManagedMeshEditOperations.Create(mesh);
                try
                {
                    managedMeshEditOperations.AddMesh(meshAtIndex, meshAtIndex.GetLocalFrame());
                    int num = VrDrawnBow.VertexCount(managedMeshEditOperations);
                    if (num < 6)
                    {
                        continue;
                    }
                    Vec3[] array = new Vec3[num];
                    for (int j = 0; j < num; j++)
                    {
                        array[j] = managedMeshEditOperations.GetPositionOfVertex(j);
                    }
                    Vec3[] array2 = StraightReinSection(array);
                    if (array2 == null)
                    {
                        continue;
                    }
                    for (int k = 0; k < num; k++)
                    {
                        managedMeshEditOperations.SetPositionOfVertex(k, array2[k]);
                    }
                    managedMeshEditOperations.ComputeCornerNormals();
                    managedMeshEditOperations.ComputeTangents(checkFixedNormals: false);
                    managedMeshEditOperations.FinalizeEditing();
                    mesh.RecomputeBoundingBox();
                    mesh.Color = meshAtIndex.Color | 0xFF000000u;
                    MetaMesh metaMesh = MetaMesh.CreateMetaMesh();
                    metaMesh.AddMesh(mesh);
                    return metaMesh;
                }
                finally
                {
                    managedMeshEditOperations.ManualInvalidate();
                    mesh.ManualInvalidate();
                    material.ManualInvalidate();
                    meshAtIndex.ManualInvalidate();
                }
            }
            return null;
        }

        internal static Vec3[] StraightReinSection(Vec3[] vertices)
        {
            if (vertices.Length < 6)
            {
                return null;
            }
            Vec3 vec = vertices[0];
            Vec3 vec2 = vertices[0];
            Vec3[] array = vertices;
            for (int i = 0; i < array.Length; i++)
            {
                Vec3 vec3 = array[i];
                vec = new Vec3(Math.Min(vec.x, vec3.x), Math.Min(vec.y, vec3.y), Math.Min(vec.z, vec3.z));
                vec2 = new Vec3(Math.Max(vec2.x, vec3.x), Math.Max(vec2.y, vec3.y), Math.Max(vec2.z, vec3.z));
            }
            float num = (vec.x + vec2.x) * 0.5f;
            float[] array2 = new float[2] { 0.08f, 0.16f };
            foreach (float num2 in array2)
            {
                float[] array3 = new float[3] { 0.5f, 0.35f, 0.65f };
                foreach (float num3 in array3)
                {
                    float num4 = vec.y + (vec2.y - vec.y) * num3;
                    Vec3 zero = Vec3.Zero;
                    Vec3 zero2 = Vec3.Zero;
                    int num5 = 0;
                    int num6 = 0;
                    array = vertices;
                    for (int k = 0; k < array.Length; k++)
                    {
                        Vec3 vec4 = array[k];
                        if (!(vec4.x >= num) && !(Math.Abs(vec4.y - num4) > num2))
                        {
                            if (vec4.y < num4 - num2 * 0.5f)
                            {
                                zero += vec4;
                                num5++;
                            }
                            if (vec4.y > num4 + num2 * 0.5f)
                            {
                                zero2 += vec4;
                                num6++;
                            }
                        }
                    }
                    if (num5 < 2 || num6 < 2)
                    {
                        continue;
                    }
                    zero *= 1f / (float)num5;
                    zero2 *= 1f / (float)num6;
                    Vec3 dir = zero2 - zero;
                    if (dir.Normalize() < 0.03f)
                    {
                        continue;
                    }
                    Axes(dir, out var across, out var through);
                    float num7 = float.MaxValue;
                    float num8 = float.MinValue;
                    Vec3[] array4 = new Vec3[vertices.Length];
                    List<float> list = new List<float>();
                    List<float> list2 = new List<float>();
                    for (int l = 0; l < vertices.Length; l++)
                    {
                        Vec3 v = vertices[l];
                        if (v.x >= num)
                        {
                            continue;
                        }
                        float num9 = Vec3.DotProduct(v, across);
                        float num10 = Vec3.DotProduct(v, through);
                        float num11 = num9;
                        float num12 = num9;
                        float num13 = num10;
                        float num14 = num10;
                        array = vertices;
                        for (int k = 0; k < array.Length; k++)
                        {
                            Vec3 v2 = array[k];
                            if (!(v2.x >= num) && !(Math.Abs(v2.y - v.y) > 0.01f))
                            {
                                float val = Vec3.DotProduct(v2, across);
                                float val2 = Vec3.DotProduct(v2, through);
                                num11 = Math.Min(num11, val);
                                num12 = Math.Max(num12, val);
                                num13 = Math.Min(num13, val2);
                                num14 = Math.Max(num14, val2);
                            }
                        }
                        float num15 = num12 - num11;
                        float num16 = num14 - num13;
                        array4[l] = new Vec3((num15 > 1E-05f) ? ((num9 - num11) / num15 - 0.5f) : 0f, 0f, (num16 > 1E-05f) ? ((num10 - num13) / num16 - 0.5f) : 0f);
                        if (Math.Abs(v.y - num4) <= num2)
                        {
                            num7 = Math.Min(num7, v.y);
                            num8 = Math.Max(num8, v.y);
                            list.Add(num15);
                            list2.Add(num16);
                        }
                    }
                    list.Sort();
                    list2.Sort();
                    float num17 = list[list.Count / 2];
                    float num18 = list2[list2.Count / 2];
                    if (!(Math.Max(num17, num18) < 0.002f) && !(num17 > 0.08f) && !(num18 > 0.08f) && !(num8 - num7 < 0.03f))
                    {
                        Vec3[] array5 = new Vec3[vertices.Length];
                        for (int m = 0; m < vertices.Length; m++)
                        {
                            float y = Math.Max(0f, Math.Min(1f, (vertices[m].y - num7) / (num8 - num7)));
                            array5[m] = new Vec3(array4[m].x * num17, y, array4[m].z * num18);
                        }
                        return array5;
                    }
                }
            }
            return null;
        }

        /// <summary>A flat strap from y = 0 to 1, x across (width 1), z through
        /// (thickness 1), in plain leather; both windings, so it shows whichever
        /// way the material culls. Hidden until placed.</summary>
        private static GameEntity CreateStrap(Scene scene)
        {
            Material material = Material.GetDefaultMaterial().CreateCopy();
            material.SetTexture(Material.MBTextureType.DiffuseMap, Texture.GetFromResource("plain_white"));

            Mesh mesh = Mesh.CreateMeshWithMaterial(material);
            ManagedMeshEditOperations ops = ManagedMeshEditOperations.Create(mesh);
            Vec2 uv = new Vec2(0.5f, 0.5f);
            Vec3 c = Leather;

            // Four sides of the box: two broad faces (+-z), two edges (+-x).
            Vec3[] normals = { new Vec3(0f, 0f, 1f), new Vec3(0f, 0f, -1f), new Vec3(1f, 0f, 0f), new Vec3(-1f, 0f, 0f) };
            foreach (Vec3 n in normals)
            {
                Vec3 across = Math.Abs(n.z) > 0.5f ? new Vec3(0.5f, 0f, 0f) : new Vec3(0f, 0f, 0.5f);
                Vec3 centre = n * 0.5f;
                Vec3 b0 = centre - across, b1 = centre + across;
                Vec3 t0 = b0 + new Vec3(0f, 1f, 0f), t1 = b1 + new Vec3(0f, 1f, 0f);
                ops.AddTriangle(b0, b1, t1, n, n, n, uv, uv, uv, c, c, c);
                ops.AddTriangle(b0, t1, t0, n, n, n, uv, uv, uv, c, c, c);
                ops.AddTriangle(b0, t1, b1, n, n, n, uv, uv, uv, c, c, c);
                ops.AddTriangle(b0, t0, t1, n, n, n, uv, uv, uv, c, c, c);
            }

            ops.FinalizeEditing();
            mesh.RecomputeBoundingBox();
            mesh.Color = 0xFF4D3019;

            GameEntity entity = GameEntity.CreateEmptyDynamic(scene, false);
            entity.AddMesh(mesh, true);
            entity.SetVisibilityExcludeParents(false);
            return entity;
        }

        private static void Detach()
        {
            if (_mount == null)
                return;

            if (ReferenceEquals(_mission, Mission.Current))
            {
                for (int i = 0; i < _seg.Length; i++)
                {
                    _seg[i]?.Remove(0);
                    _seg[i] = null;
                }
                foreach (var hidden in _hiddenMeta)
                    hidden.Key.SetVisibilityMask(hidden.Value);
                foreach (var hidden in _hiddenMesh)
                    hidden.Key.SetVisibilityMask(hidden.Value);
            }

            _hiddenMeta.Clear();
            _hiddenMesh.Clear();
            _colliders.Clear();
            _mount = null;
            _held = false;
            _haveWorld = false;
        }

        /// <summary>The mission changed: drop every reference untouched, and every
        /// remembered vanilla mesh of an older mission.</summary>
        private static void Forget(Mission mission)
        {
            _mission = mission;
            for (int i = 0; i < _seg.Length; i++)
                _seg[i] = null;
            _hiddenMeta.Clear();
            _hiddenMesh.Clear();
            _colliders.Clear();
            _mount = null;
            _held = false;
            _haveWorld = false;

            var stale = new List<UIntPtr>();
            foreach (var entry in _vanilla)
            {
                if (!ReferenceEquals(entry.Value.Mission, mission))
                    stale.Add(entry.Key);
            }
            foreach (UIntPtr key in stale)
                _vanilla.Remove(key);
        }

        /// <summary>Vanilla's reins on this horse, masked off while ridden: the
        /// rein and rope meshes the native rein system was given (ReinsClothPatch),
        /// and any *rein* metamesh or cloth on its skeleton or entity - each
        /// metamesh and each of its meshes.</summary>
        private static void HideReins(Agent mount)
        {
            MBAgentVisuals visuals = mount.AgentVisuals;
            if (visuals == null)
                return;

            if (_vanilla.TryGetValue(visuals.Pointer, out var given))
            {
                Hide(given.Rein, true);
                Hide(given.Rope, true);
            }

            Skeleton skeleton = visuals.GetSkeleton();
            if (skeleton != null)
            {
                int metas = skeleton.GetComponentCount(GameEntity.ComponentType.MetaMesh);
                for (int i = 0; i < metas; i++)
                    Hide(skeleton.GetComponentAtIndex(GameEntity.ComponentType.MetaMesh, i) as MetaMesh, false);

                int cloths = skeleton.GetComponentCount(GameEntity.ComponentType.ClothSimulator);
                for (int i = 0; i < cloths; i++)
                    Hide((skeleton.GetComponentAtIndex(GameEntity.ComponentType.ClothSimulator, i)
                          as ClothSimulatorComponent)?.GetFirstMetaMesh(), false);
            }

            GameEntity entity = visuals.GetEntity();
            if (entity != null)
            {
                for (int i = 0; i < entity.MultiMeshComponentCount; i++)
                    Hide(entity.GetMetaMesh(i), false);
            }
        }

        private static void Hide(MetaMesh meta, bool known)
        {
            if (meta == null)
                return;

            string name = meta.GetName();
            if (!known && (name == null || name.IndexOf("rein", StringComparison.OrdinalIgnoreCase) < 0))
                return;

            foreach (var hidden in _hiddenMeta)
            {
                if (hidden.Key.Pointer == meta.Pointer)
                    return;
            }

            _hiddenMeta.Add(new KeyValuePair<MetaMesh, VisibilityMaskFlags>(meta, meta.GetVisibilityMask()));
            meta.SetVisibilityMask((VisibilityMaskFlags)0);
            for (int k = 0; k < meta.MeshCount; k++)
            {
                Mesh mesh = meta.GetMeshAtIndex(k);
                if (mesh == null)
                    continue;
                _hiddenMesh.Add(new KeyValuePair<Mesh, VisibilityMaskFlags>(mesh, mesh.VisibilityMask));
                mesh.SetVisibilityMask((VisibilityMaskFlags)0);
            }
        }

        /// <summary>TEST 471d probe, a second after the hide: whether anything put
        /// the masks back (a still-visible strap with masks at 0 is drawn by
        /// something that ignores them).</summary>
        private static void CheckHidden()
        {
            if (_checkHideAt == 0L || Stopwatch.GetTimestamp() < _checkHideAt)
                return;

            _checkHideAt = 0L;
            if (!Probe)
                return;

            var line = new StringBuilder("TEST 471d hide check:");
            foreach (var hidden in _hiddenMeta)
                line.Append(" '").Append(hidden.Key.GetName()).Append("' ").Append((int)hidden.Value).Append("->")
                    .Append((int)hidden.Key.GetVisibilityMask());
            int reset = 0;
            foreach (var hidden in _hiddenMesh)
            {
                if (hidden.Key.VisibilityMask != 0)
                    reset++;
            }
            line.Append("; ").Append(_hiddenMesh.Count).Append(" mesh(es), ").Append(reset).Append(" put back.");
            VrLog.Info(line.ToString());
        }

        private static void Draw()
        {
            if (!_haveWorld)
                return;

            for (int side = 0; side < Sides; side++)
            {
                for (int i = 0; i < DrawLinks; i++)
                {
                    GameEntity e = _seg[side * DrawLinks + i];
                    if (e == null)
                        continue;

                    Vec3 a = CurvePoint(_world[side], i / (float)CurveSteps);
                    Vec3 dir = CurvePoint(_world[side], (i + 1) / (float)CurveSteps) - a;
                    float length = dir.Normalize();
                    if (length < 1e-4f)
                    {
                        e.SetVisibilityExcludeParents(false);
                        continue;
                    }

                    MatrixFrame f = _ropeAxis >= 0 ? RopeFrame(a, dir, length) : StrapFrame(a, dir, length);
                    e.SetGlobalFrame(in f, true);
                    e.SetVisibilityExcludeParents(true);
                }
            }
        }

        /// <summary>Smooth the drawn curve without adding physics nodes or collision work.</summary>
        internal static Vec3 CurvePoint(Vec3[] points, float at)
        {
            at = Math.Max(0f, Math.Min(points.Length - 1, at));
            int i = Math.Min(points.Length - 2, (int)at);
            float t = at - i;
            Vec3 b = points[i], c = points[i + 1];
            Vec3 a = i > 0 ? points[i - 1] : b * 2f - c;
            Vec3 d = i + 2 < points.Length ? points[i + 2] : c * 2f - b;
            return (b * 2f + (c - a) * t + (a * 2f - b * 5f + c * 4f - d) * (t * t)
                    + (d - a + (b - c) * 3f) * (t * t * t)) * 0.5f;
        }

        /// <summary>A link's broad side lies along the neck's surface: across is
        /// level (the link x up), the thin side faces out. A hair longer, so the
        /// links overlap at the bends instead of showing gaps.</summary>
        private static void Axes(Vec3 dir, out Vec3 across, out Vec3 through)
        {
            across = Vec3.CrossProduct(dir, new Vec3(0f, 0f, 1f));
            if (across.Normalize() < 1e-3f)
            {
                across = Vec3.CrossProduct(dir, new Vec3(1f, 0f, 0f));
                across.Normalize();
            }
            through = Vec3.CrossProduct(across, dir);
        }

        private static MatrixFrame StrapFrame(Vec3 a, Vec3 dir, float length)
        {
            Axes(dir, out Vec3 across, out Vec3 through);
            MatrixFrame f = MatrixFrame.Identity;
            f.rotation.s = across * Width;
            f.rotation.f = dir * (length + 0.5f * Width);
            f.rotation.u = through * Thick;
            f.origin = a - dir * (0.25f * Width);
            return f;
        }

        private static MatrixFrame RopeFrame(Vec3 a, Vec3 dir, float length)
        {
            Axes(dir, out var across, out var through);
            int ropeAxis = _ropeAxis;
            int num = (ropeAxis + 1) % 3;
            float num2 = Math.Max(0.001f, Get(_ropeMax, ropeAxis) - Get(_ropeMin, ropeAxis));
            Vec3 vec = dir * ((length + 0.01f) / num2);
            MatrixFrame identity = MatrixFrame.Identity;
            identity.rotation.s = ((ropeAxis == 0) ? vec : ((num == 0) ? through : across));
            identity.rotation.f = ((ropeAxis == 1) ? vec : ((num == 1) ? through : across));
            identity.rotation.u = ((ropeAxis == 2) ? vec : ((num == 2) ? through : across));
            Vec3 vec2 = (_ropeMin + _ropeMax) * 0.5f;
            Vec3 v = new Vec3((ropeAxis == 0) ? _ropeMin.x : vec2.x, (ropeAxis == 1) ? _ropeMin.y : vec2.y, (ropeAxis == 2) ? _ropeMin.z : vec2.z);
            identity.origin = a - dir * 0.005f - identity.rotation.TransformToParent(in v);
            return identity;
        }

        private static float Get(Vec3 v, int i) => i == 0 ? v.x : i == 1 ? v.y : v.z;

        /// <summary>The hand feels the reins go taut: short pulses, stronger the
        /// further past their length.</summary>
        private static void Buzz()
        {
            VrHand? hand = VrRein.HeldBy;
            if (hand == null || _taut < 0.02f || Stopwatch.GetTimestamp() < _buzzAt)
                return;

            _buzzAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
            try
            {
                NativeMethods.bvr_trigger_haptic((int)hand.Value, Math.Min(0.6f, 0.15f + _taut), 0.02f);
            }
            catch (Exception)
            {
                // A pulse is never worth a throw.
            }
        }

        private static void Report()
        {
            if (!Probe || !_held || Stopwatch.GetTimestamp() < _logAt)
                return;

            _logAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            VrLog.Info("TEST 471d reins: held, taut " + F(_taut * 100f) + " %, mouth to hand " + F((_end[0] - _p[0][0]).Length)
                       + " m of " + F(_segLen[0] * (N - 1)) + " m.");
        }

        /// <summary>One-time: the rein points and every collider now, in the
        /// horse's frame.</summary>
        private static void Describe(Agent mount, MatrixFrame frame, float scale, Vec3 bitL, Vec3 bitR, Vec3 restL,
                                     Vec3 restR)
        {
            Monster m = mount.Monster;
            VrLog.Info("TEST 471d probe: bit rings " + V(bitL) + " / " + V(bitR) + ", handles " + V(restL) + " / "
                       + V(restR) + "; neck root " + m.NeckRootBoneIndex + ", head " + m.HeadLookDirectionBoneIndex
                       + ", rein body '" + m.ReinCollisionBody + "'.");

            int caps = Colliders(frame, scale);
            var line = new StringBuilder("TEST 471e probe: " + caps + " of " + _colliders.Count
                                         + " colliders on the horse now (horse frame):");
            for (int i = 0; i < caps; i++)
                line.Append(' ').Append(_colliders[i].From).Append(' ').Append(V(_capA[i])).Append("-").Append(V(_capB[i]))
                    .Append(" r ").Append(F(_capRA[i])).Append('/').Append(F(_capRB[i])).Append(';');
            VrLog.Info(line.ToString());
        }

        private static Vec3 Parse(string text)
        {
            string[] parts = (text ?? "").Split(',');
            float[] v = new float[3];
            for (int i = 0; i < 3 && i < parts.Length; i++)
                float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]);
            return new Vec3(v[0], v[1], v[2]);
        }

        private static string V(Vec3 v) => "(" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + ")";

        private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
