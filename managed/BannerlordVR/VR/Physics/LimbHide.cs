using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // A severed limb folded into the body in the skeleton itself. The engine
    // keeps one 0x100-byte record per bone (BannerlordVR.Physics.Native,
    // skeleton_bones.cpp); read in game on 22 Oct 2026, a record holds the
    // frame the bone is drawn with at 0x04 (rotation, quaternion w x y z) and
    // 0x14 (position, entity space), then the local frame at 0x30 / 0x40 and
    // the rest frame as a 4x4 matrix at 0x60. The drawn frame has no scale
    // (a local scale of 0.001 was kept by the engine and never drawn, and its
    // derived transforms broke the sound system before a crash), so the limb
    // cannot shrink: it is moved rigidly instead. The head sinks into the
    // chest along the neck; a forearm, hand, calf or foot is laid onto the
    // bone above it, inside its volume. Bannerlord cannot cut a mesh, so the
    // skin between stays stretched, mostly inside the body.
    //
    // The engine rewrites the records of a moving ragdoll after the mission
    // tick: the fold is written in the mission tick and again after the agents'
    // tick (AfterAsyncTickTick), and whether it survives to the next frame's
    // start is counted (LIMB_TUCK).
    internal static class LimbHide
    {
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_skeleton_bone_record(UIntPtr skeleton, int bone, byte[] output, int bytes);
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_skeleton_write_record(UIntPtr skeleton, int bone, int offset, float[] values, int count);
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_skeleton_collapse_bone(UIntPtr skeleton, int bone, float x, float y, float z);
        // The hook on the engine's palette refresh keeps a severed limb
        // collapsed whenever the engine redraws its bones (native).
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_limb_sever(UIntPtr skeleton, int root, ulong bones);
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_limb_sever_sunk(UIntPtr skeleton, int root, ulong bones, int parent, int grand, float sink);
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_limb_forget(UIntPtr skeleton);
        private static Mission _mission;

        private const int RecordBytes = 0x100, DrawnRotation = 0x04, DrawnPosition = 0x14;
        // Kept for the whole mission, until the body's visuals go.
        private const float HoldSeconds = float.MaxValue, Tolerance = .003f, HeadSink = .30f;
        private struct Pose { public float W, X, Y, Z; public Vec3 P; }
        private sealed class Cut
        {
            public Agent Agent; public Skeleton Skeleton; public SliceJoint Kind; public sbyte Root, Parent, Grandparent;
            public sbyte[] Bones; public float Until, Started; public Vec3 Written; public bool HasWritten, Reported;
            public int Writes, Skipped, Survived, Lost, Collapsed, CollapseFailed, LastError, Spurts;
            public float NextSpurt, NextCloth;
            // Watch: the record a tick before and how far each 16-byte block moved.
            public byte[] Prev; public float[] Moved = new float[16], Total = new float[16]; public float NextLog; public int Samples;
        }
        private static readonly List<Cut> Cuts = new List<Cut>();
        private static bool _broken, _layoutChecked, _layoutOk;
        private static readonly byte[] Buffer = new byte[RecordBytes];
        private static readonly float[] Values = new float[4];

        public static void Begin(Agent agent, sbyte bone, sbyte parent, SliceJoint kind)
        {
            if (!EnemyPhysicsSettings.LimbHide || _broken || agent?.AgentVisuals == null) return;
            try
            {
                Skeleton skeleton = agent.AgentVisuals.GetSkeleton();
                if (skeleton == null || parent < 0) return;
                var bones = new List<sbyte>();
                int count = skeleton.GetBoneCount();
                for (sbyte b = 0; b < count; b++)
                    for (sbyte up = b; up >= 0; up = skeleton.GetParentBoneIndex(up))
                        if (up == bone) { bones.Add(b); break; }
                float now = Mission.Current?.CurrentTime ?? 0;
                Perf.Mark("limb_bones");
                // The helmet flies before the head is folded away.
                if (kind == SliceJoint.Neck && bvr_skeleton_bone_record(skeleton.Pointer, bone, Buffer, RecordBytes) == RecordBytes)
                    SeveredProp.ThrowHelmet(agent, skeleton.GetBoneEntitialFrameWithIndex(bone), RestFrame(),
                        skeleton.GetBoneEntitialFrameWithIndex(parent).origin);
                Perf.Mark("helmet_prop");
                if (kind == SliceJoint.Neck) HelmetHide.Hide(agent, bone);
                Perf.Mark("helmet_hide");
                ulong mask = 0;
                foreach (sbyte b in bones) if (b < 64) mask |= 1ul << b;
                // A head is also sunk into the chest in its record, natively
                // after every engine pose (a helmet's coif follows the record).
                int sever = kind == SliceJoint.Neck
                    ? bvr_limb_sever_sunk(skeleton.Pointer, bone, mask, parent, skeleton.GetParentBoneIndex(parent), HeadSink)
                    : bvr_limb_sever(skeleton.Pointer, bone, mask);
                Perf.Mark("sever_hook");
                PhysicsLog.Info("LIMB_TUCK sever hook status=" + sever + (sever == 0 ? "" : " (the limb is collapsed from the mission tick only)"));
                _mission = Mission.Current;
                Cuts.Add(new Cut { Agent = agent, Skeleton = skeleton, Kind = kind, Root = bone, Parent = parent,
                    Grandparent = skeleton.GetParentBoneIndex(parent), Bones = bones.ToArray(), Started = now, Until = now + HoldSeconds });
                PhysicsLog.Info("LIMB_TUCK begin agent=" + agent.Index + " joint=" + kind + " root=" + skeleton.GetBoneName(bone) +
                    " onto=" + skeleton.GetBoneName(parent) + " bones=" + bones.Count);
            }
            catch (Exception ex) { Fail("begin", ex); }
        }

        // Mission tick and after the agents' tick: fold every cut limb.
        public static void Tick() => Pass(false);
        public static void LateTick() => Pass(true);

        // Start of the next frame: is the fold written last frame still there?
        public static void FrameStart()
        {
            if (Cuts.Count == 0 || _broken) return;
            try
            {
                foreach (Cut cut in Cuts)
                {
                    if (!cut.HasWritten || !Read(cut, cut.Root, out Pose root)) continue;
                    if ((root.P - cut.Written).LengthSquared < 1e-6f) cut.Survived++; else cut.Lost++;
                }
            }
            catch (Exception ex) { Fail("frame", ex); }
        }

        private static void Pass(bool late)
        {
            long perf = Perf.Start();
            try { PassCore(late); }
            finally { Perf.Stop(Perf.Section.Limbs, perf); }
        }
        private static void PassCore(bool late)
        {
            if (Cuts.Count == 0 || _broken) return;
            // A new mission: the old skeletons are gone, their pointers may be reused.
            if (Mission.Current != _mission) { foreach (Cut old in Cuts) Forget(old); Cuts.Clear(); return; }
            float now = Mission.Current?.CurrentTime ?? 0;
            for (int i = Cuts.Count - 1; i >= 0; i--)
            {
                Cut cut = Cuts[i];
                try
                {
                    if (now > cut.Until || cut.Agent.AgentVisuals == null || !cut.Agent.AgentVisuals.IsValid() ||
                        cut.Agent.AgentVisuals.GetSkeleton()?.Pointer != cut.Skeleton.Pointer)
                    {
                        Report(cut, "ended");
                        Forget(cut);
                        Cuts.RemoveAt(i); continue;
                    }
                    if (EnemyPhysicsSettings.LimbWatch) { Watch(cut, now); continue; }
                    if (!_layoutChecked) CheckLayout(cut);
                    if (!_layoutOk) { Cuts.RemoveAt(i); continue; }
                    // A neck cut also sinks the head into the chest in the record:
                    // the helmet follows the bone's frame there, not the palette,
                    // and stayed drawn a second while the head was gone.
                    Collapse(cut);
                    if (late) Bleed(cut, now);
                    // The coif's cloth is pinned and reset ten times a second,
                    // not twice a frame: it holds between (0.27 ms a frame).
                    if (cut.Kind == SliceJoint.Neck && now - cut.Started < 1.5f && now >= cut.NextCloth)
                    {
                        cut.NextCloth = now + .1f;
                        HelmetHide.Cloth(cut.Agent, true);
                    }
                    if (late && !cut.Reported && now - cut.Started > 1.5f) Report(cut, "1.5s");
                }
                catch (Exception ex) { Fail("tick", ex); return; }
            }
        }

        // Which fields of the cut bone's record the engine updates as the body
        // falls: the fold written at 0x04 / 0x14 held every frame yet nothing
        // changed on screen, so the ragdoll is drawn from elsewhere. Logged
        // every 0.3 s with the bone's world position for comparison.
        private static void Watch(Cut cut, float now)
        {
            if (bvr_skeleton_bone_record(cut.Skeleton.Pointer, cut.Root, Buffer, RecordBytes) < RecordBytes) return;
            if (cut.Prev != null)
                for (int b = 0; b < 16; b++)
                    for (int k = 0; k < 4; k++)
                    {
                        float d = Math.Abs(BitConverter.ToSingle(Buffer, b * 16 + k * 4) - BitConverter.ToSingle(cut.Prev, b * 16 + k * 4));
                        if (float.IsNaN(d) || float.IsInfinity(d)) continue;
                        cut.Moved[b] += d; cut.Total[b] += d;
                    }
            if (cut.Prev == null) cut.Prev = new byte[RecordBytes];
            Array.Copy(Buffer, cut.Prev, RecordBytes);
            cut.Samples++;
            if (now < cut.NextLog) return;
            cut.NextLog = now + .3f;
            MatrixFrame e = cut.Skeleton.GetBoneEntitialFrameWithIndex(cut.Root);
            Vec3 world = cut.Agent.AgentVisuals.GetGlobalFrame().TransformToParent(e).origin;
            var sb = new System.Text.StringBuilder();
            for (int b = 0; b < 16; b++) sb.Append(' ').Append((b * 16).ToString("x2")).Append(':').Append(cut.Moved[b].ToString("F3", CultureInfo.InvariantCulture));
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "LIMB_WATCH t={0:F2} agent={1} bone_world=({2:F2},{3:F2},{4:F2}) entitial_o=({5:F3},{6:F3},{7:F3}) rec14=({8:F3},{9:F3},{10:F3}) rec90=({11:F3},{12:F3},{13:F3}) moved{14}",
                now - cut.Started, cut.Agent.Index, world.x, world.y, world.z, e.origin.x, e.origin.y, e.origin.z,
                BitConverter.ToSingle(Buffer, 0x14), BitConverter.ToSingle(Buffer, 0x18), BitConverter.ToSingle(Buffer, 0x1c),
                BitConverter.ToSingle(Buffer, 0x90), BitConverter.ToSingle(Buffer, 0x94), BitConverter.ToSingle(Buffer, 0x98), sb));
            Array.Clear(cut.Moved, 0, 16);
        }

        // The drawn position must match the engine's entity-space bone frame.
        private static void CheckLayout(Cut cut)
        {
            _layoutChecked = true;
            MatrixFrame e = cut.Skeleton.GetBoneEntitialFrameWithIndex(cut.Root);
            _layoutOk = Read(cut, cut.Root, out Pose root) && (root.P - e.origin).Length < Tolerance &&
                Math.Abs(root.W * root.W + root.X * root.X + root.Y * root.Y + root.Z * root.Z - 1) < .01f;
            PhysicsLog.Info("LIMB_TUCK layout " + (_layoutOk ? "matches" : "does not match: folding off"));
        }

        // The record's rest frame (4x4 at 0x60, rows s f u origin) in Buffer.
        private static MatrixFrame RestFrame()
        {
            Vec3 Row(int o) => new Vec3(BitConverter.ToSingle(Buffer, o), BitConverter.ToSingle(Buffer, o + 4), BitConverter.ToSingle(Buffer, o + 8));
            return new MatrixFrame(new Mat3(Row(0x60), Row(0x70), Row(0x80)), Row(0x90));
        }

        // The stump spurts with the heartbeat, weaker and rarer for 4 s.
        private const float BleedSeconds = 4;
        private static void Bleed(Cut cut, float now)
        {
            float age = now - cut.Started;
            if (age > BleedSeconds || now < cut.NextSpurt) return;
            cut.NextSpurt = now + .12f + age * .07f;
            if (!Read(cut, cut.Parent, out Pose stump)) return;
            Vec3 axis = Vec3.Up;
            if (cut.Grandparent >= 0 && Read(cut, cut.Grandparent, out Pose above))
            {
                Vec3 d = stump.P - above.P;
                if (d.Length > 1e-3f) axis = d.NormalizedCopy();
            }
            if (cut.Kind != SliceJoint.Neck && Read(cut, cut.Root, out Pose root))
            {
                Vec3 d = root.P - stump.P;
                if (d.Length > 1e-3f) axis = d.NormalizedCopy();
            }
            MatrixFrame global = cut.Agent.AgentVisuals.GetGlobalFrame();
            Vec3 at = global.TransformToParent(cut.Kind == SliceJoint.Neck ? stump.P : (Read(cut, cut.Root, out Pose r) ? r.P : stump.P));
            Vec3 direction = global.rotation.TransformToParent(axis);
            BloodFx.Burst((cut.Spurts++ & 1) == 0 ? BloodFx.Kind.Exit : BloodFx.Kind.Enter, at, direction);
        }

        // The skinning palette, not the record, is what the GPU draws: the
        // engine fills it from the record in 0x361010 right after its own
        // writes, so a fold written to the record alone was never drawn. Each
        // bone of the limb gets its palette matrix collapsed onto the joint
        // (native bvr_skeleton_collapse_bone): every vertex it skins, armour
        // included, is drawn at that point, and the limb is gone.
        private static void Collapse(Cut cut)
        {
            if (!Read(cut, cut.Root, out Pose root)) return;
            foreach (sbyte bone in cut.Bones)
            {
                int status = bvr_skeleton_collapse_bone(cut.Skeleton.Pointer, bone, root.P.x, root.P.y, root.P.z);
                if (status == 0) cut.Collapsed++; else { cut.CollapseFailed++; cut.LastError = status; }
            }
            cut.Writes++;
        }

        private static void Fold(Cut cut)
        {
            if (!Read(cut, cut.Root, out Pose root) || !Read(cut, cut.Parent, out Pose parent)) return;
            // Still where we put it: the engine has not rewritten the limb.
            if (cut.HasWritten && (root.P - cut.Written).LengthSquared < 1e-6f) { cut.Skipped++; return; }
            Pose target;
            if (cut.Kind == SliceJoint.Neck)
            {
                // Down the neck into the chest.
                Vec3 up = Vec3.Up;
                if (cut.Grandparent >= 0 && Read(cut, cut.Grandparent, out Pose chest))
                {
                    Vec3 d = parent.P - chest.P;
                    if (d.Length > 1e-3f) up = d.NormalizedCopy();
                }
                target = root; target.P = parent.P - up * HeadSink;
            }
            else target = parent; // laid along the bone above, from its start
            // Delta from the limb's root as drawn to its target, applied to the
            // whole limb so it keeps its shape.
            Conjugate(root, out float cw, out float cx, out float cy, out float cz);
            Multiply(target.W, target.X, target.Y, target.Z, cw, cx, cy, cz, out float dw, out float dx, out float dy, out float dz);
            foreach (sbyte bone in cut.Bones)
            {
                if (!Read(cut, bone, out Pose p)) continue;
                Multiply(dw, dx, dy, dz, p.W, p.X, p.Y, p.Z, out float nw, out float nx, out float ny, out float nz);
                float n = (float)Math.Sqrt(nw * nw + nx * nx + ny * ny + nz * nz);
                if (n < 1e-4f || float.IsNaN(n)) continue;
                Vec3 offset = Rotate(dw, dx, dy, dz, p.P - root.P);
                Vec3 position = target.P + offset;
                if (!InteractionMath.IsFinite(position)) continue;
                Values[0] = nw / n; Values[1] = nx / n; Values[2] = ny / n; Values[3] = nz / n;
                bvr_skeleton_write_record(cut.Skeleton.Pointer, bone, DrawnRotation, Values, 4);
                Values[0] = position.x; Values[1] = position.y; Values[2] = position.z;
                bvr_skeleton_write_record(cut.Skeleton.Pointer, bone, DrawnPosition, Values, 3);
                if (bone == cut.Root) { cut.Written = position; cut.HasWritten = true; }
            }
            cut.Writes++;
        }

        private static bool Read(Cut cut, sbyte bone, out Pose pose)
        {
            pose = default;
            if (bvr_skeleton_bone_record(cut.Skeleton.Pointer, bone, Buffer, RecordBytes) < RecordBytes) return false;
            pose.W = BitConverter.ToSingle(Buffer, DrawnRotation); pose.X = BitConverter.ToSingle(Buffer, DrawnRotation + 4);
            pose.Y = BitConverter.ToSingle(Buffer, DrawnRotation + 8); pose.Z = BitConverter.ToSingle(Buffer, DrawnRotation + 12);
            pose.P = new Vec3(BitConverter.ToSingle(Buffer, DrawnPosition), BitConverter.ToSingle(Buffer, DrawnPosition + 4),
                BitConverter.ToSingle(Buffer, DrawnPosition + 8));
            return InteractionMath.IsFinite(pose.P) && !float.IsNaN(pose.W);
        }

        // Hamilton quaternions, w first.
        private static void Conjugate(Pose q, out float w, out float x, out float y, out float z) { w = q.W; x = -q.X; y = -q.Y; z = -q.Z; }
        private static void Multiply(float aw, float ax, float ay, float az, float bw, float bx, float by, float bz,
            out float w, out float x, out float y, out float z)
        {
            w = aw * bw - ax * bx - ay * by - az * bz;
            x = aw * bx + ax * bw + ay * bz - az * by;
            y = aw * by - ax * bz + ay * bw + az * bx;
            z = aw * bz + ax * by - ay * bx + az * bw;
        }
        private static Vec3 Rotate(float w, float x, float y, float z, Vec3 v)
        {
            Multiply(w, x, y, z, 0, v.x, v.y, v.z, out float rw, out float rx, out float ry, out float rz);
            Multiply(rw, rx, ry, rz, w, -x, -y, -z, out float _, out float ox, out float oy, out float oz);
            return new Vec3(ox, oy, oz);
        }

        private static void Report(Cut cut, string when)
        {
            cut.Reported = true;
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "LIMB_TUCK {0} agent={1} joint={2} passes={3} collapsed={4} failed={5} last_error={6}",
                when, cut.Agent.Index, cut.Kind, cut.Writes, cut.Collapsed, cut.CollapseFailed, cut.LastError));
            if (cut.Samples > 0)
            {
                var sb = new System.Text.StringBuilder();
                for (int b = 0; b < 16; b++) sb.Append(' ').Append((b * 16).ToString("x2")).Append(':').Append(cut.Total[b].ToString("F2", CultureInfo.InvariantCulture));
                PhysicsLog.Info("LIMB_WATCH " + when + " agent=" + cut.Agent.Index + " samples=" + cut.Samples + " total movement per block" + sb);
            }
        }

        // A mission starts: no severed limb of an old one is kept.
        public static void ForgetAll()
        {
            foreach (Cut cut in Cuts) Forget(cut);
            Cuts.Clear(); _mission = null;
        }

        private static void Forget(Cut cut)
        {
            try { bvr_limb_forget(cut.Skeleton.Pointer); } catch (Exception) { }
        }

        private static void Fail(string where, Exception ex)
        {
            foreach (Cut cut in Cuts) Forget(cut);
            _broken = true; Cuts.Clear();
            PhysicsLog.Error("LIMB_TUCK " + where + " failed; severed limbs stay drawn.", ex);
        }
    }
}
