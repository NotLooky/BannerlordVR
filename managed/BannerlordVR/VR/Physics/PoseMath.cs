using System;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    // Rotation blending for PoseBlend: quaternions from TaleWorlds frames
    // (Mat3 rows s, f, u are the x, y, z axes).
    public static class PoseMath
    {
        // a towards b by w (0 keeps a).
        public static MatrixFrame Blend(MatrixFrame a, MatrixFrame b, float w)
        {
            float scale = a.rotation.s.Length;
            Mat3 ra = a.rotation, rb = b.rotation;
            ra.Orthonormalize(); rb.Orthonormalize();
            Quat qa = ToQuat(ra), qb = ToQuat(rb);
            Mat3 r = ToMat(Slerp(qa, qb, w));
            if (InteractionMath.IsFinite(scale) && scale > 1e-3f && Math.Abs(scale - 1) > 1e-3f)
            { r.s *= scale; r.f *= scale; r.u *= scale; }
            return new MatrixFrame(r, a.origin + (b.origin - a.origin) * w);
        }

        public struct Quat { public float X, Y, Z, W; }
        public static Quat ToQuat(Mat3 m)
        {
            // Columns s, f, u are the basis vectors (x, y, z axes).
            float m00 = m.s.x, m11 = m.f.y, m22 = m.u.z;
            float trace = m00 + m11 + m22;
            Quat q;
            if (trace > 0)
            {
                float k = (float)Math.Sqrt(trace + 1) * 2;
                q.W = .25f * k; q.X = (m.f.z - m.u.y) / k; q.Y = (m.u.x - m.s.z) / k; q.Z = (m.s.y - m.f.x) / k;
            }
            else if (m00 > m11 && m00 > m22)
            {
                float k = (float)Math.Sqrt(1 + m00 - m11 - m22) * 2;
                q.W = (m.f.z - m.u.y) / k; q.X = .25f * k; q.Y = (m.f.x + m.s.y) / k; q.Z = (m.u.x + m.s.z) / k;
            }
            else if (m11 > m22)
            {
                float k = (float)Math.Sqrt(1 + m11 - m00 - m22) * 2;
                q.W = (m.u.x - m.s.z) / k; q.X = (m.f.x + m.s.y) / k; q.Y = .25f * k; q.Z = (m.u.y + m.f.z) / k;
            }
            else
            {
                float k = (float)Math.Sqrt(1 + m22 - m00 - m11) * 2;
                q.W = (m.s.y - m.f.x) / k; q.X = (m.u.x + m.s.z) / k; q.Y = (m.u.y + m.f.z) / k; q.Z = .25f * k;
            }
            return q;
        }
        public static Mat3 ToMat(Quat q)
        {
            float xx = q.X * q.X, yy = q.Y * q.Y, zz = q.Z * q.Z, xy = q.X * q.Y, xz = q.X * q.Z, yz = q.Y * q.Z;
            float wx = q.W * q.X, wy = q.W * q.Y, wz = q.W * q.Z;
            return new Mat3(
                new Vec3(1 - 2 * (yy + zz), 2 * (xy + wz), 2 * (xz - wy)),
                new Vec3(2 * (xy - wz), 1 - 2 * (xx + zz), 2 * (yz + wx)),
                new Vec3(2 * (xz + wy), 2 * (yz - wx), 1 - 2 * (xx + yy)));
        }
        public static Quat Slerp(Quat a, Quat b, float t)
        {
            float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            if (dot < 0) { b.X = -b.X; b.Y = -b.Y; b.Z = -b.Z; b.W = -b.W; dot = -dot; }
            float s0, s1;
            if (dot > .9995f) { s0 = 1 - t; s1 = t; }
            else
            {
                float theta = (float)Math.Acos(Math.Min(1, dot)), sin = (float)Math.Sin(theta);
                s0 = (float)Math.Sin((1 - t) * theta) / sin; s1 = (float)Math.Sin(t * theta) / sin;
            }
            Quat q = new Quat { X = a.X * s0 + b.X * s1, Y = a.Y * s0 + b.Y * s1, Z = a.Z * s0 + b.Z * s1, W = a.W * s0 + b.W * s1 };
            float n = (float)Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            if (n > 1e-6f) { q.X /= n; q.Y /= n; q.Z /= n; q.W /= n; }
            return q;
        }
    }
}
