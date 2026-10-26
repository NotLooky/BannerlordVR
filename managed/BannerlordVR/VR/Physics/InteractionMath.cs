using System;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    public static class InteractionMath
    {
        public static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        public static bool IsFinite(Vec3 v) => IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
        public static float Dot(Vec3 a, Vec3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static float GestureSpeed(Vec3 oldPosition, Vec3 position, Vec3 oldAxis, Vec3 axis,
                                         float length, float scale, float dt)
        {
            if (!IsFinite(oldPosition) || !IsFinite(position) || !IsFinite(oldAxis) || !IsFinite(axis) ||
                !IsFinite(length) || !IsFinite(scale) || !IsFinite(dt) || dt <= 0 || length < 0 || scale <= 0) return 0;
            // Stage-space input excludes movement of the player/navigation capsule.
            float translation = (position - oldPosition).Length * scale;
            float rotation = (axis - oldAxis).Length * length;
            return Math.Min(20, Math.Max(translation, rotation) / dt);
        }
        public static Vec3 ClampMagnitude(Vec3 v, float maximum)
        {
            if (!IsFinite(v) || !IsFinite(maximum) || maximum <= 0) return Vec3.Zero;
            double length = Math.Sqrt((double)v.x * v.x + (double)v.y * v.y + (double)v.z * v.z);
            return length > maximum ? v * (float)(maximum / length) : v;
        }
        public static Vec3 ClosestPointOnSegment(Vec3 a, Vec3 b, Vec3 p)
        {
            if (!IsFinite(a) || !IsFinite(b) || !IsFinite(p)) return Vec3.Zero;
            Vec3 d = b - a;
            float length = Dot(d, d);
            return length < 1e-10f ? a : a + d * Math.Max(0, Math.Min(1, Dot(p - a, d) / length));
        }
        public static bool SegmentSphere(Vec3 a, Vec3 b, Vec3 center, float radius, out float fraction)
        {
            fraction = 0;
            if (!IsFinite(a) || !IsFinite(b) || !IsFinite(center) || !IsFinite(radius) || radius <= 0) return false;
            Vec3 m = a - center, d = b - a;
            double c = Dot(m, m) - (double)radius * radius;
            if (c <= 0) return true;
            double length = Dot(d, d), projection = Dot(m, d);
            if (length < 1e-12 || projection >= 0) return false;
            double disc = projection * projection - length * c;
            if (disc < 0) return false;
            double t = (-projection - Math.Sqrt(disc)) / length;
            if (t < 0 || t > 1) return false;
            fraction = (float)t;
            return true;
        }
        public static bool SegmentBox(Vec3 start, Vec3 end, Vec3 min, Vec3 max, float padding, out float fraction)
        {
            fraction = 0;
            if (!IsFinite(start) || !IsFinite(end) || !IsFinite(min) || !IsFinite(max) || !IsFinite(padding) || padding < 0 ||
                min.x > max.x || min.y > max.y || min.z > max.z) return false;
            double enter = 0, exit = 1;
            for (int axis = 0; axis < 3; axis++)
            {
                double a = axis == 0 ? start.x : axis == 1 ? start.y : start.z;
                double b = axis == 0 ? end.x : axis == 1 ? end.y : end.z;
                double lo = (axis == 0 ? min.x : axis == 1 ? min.y : min.z) - padding;
                double hi = (axis == 0 ? max.x : axis == 1 ? max.y : max.z) + padding;
                double delta = b - a;
                if (Math.Abs(delta) < 1e-12) { if (a < lo || a > hi) return false; continue; }
                double first = (lo - a) / delta, last = (hi - a) / delta;
                if (first > last) { double swap = first; first = last; last = swap; }
                enter = Math.Max(enter, first); exit = Math.Min(exit, last);
                if (enter > exit) return false;
            }
            fraction = (float)enter;
            return true;
        }
        // Error is target - body. Relative velocity is body - target.
        public static Vec3 SpringForce(Vec3 error, Vec3 relativeVelocity, float stiffness, float damping, float maximum)
        {
            if (!IsFinite(stiffness) || !IsFinite(damping) || stiffness < 0 || damping < 0) return Vec3.Zero;
            return ClampMagnitude(error * stiffness - relativeVelocity * damping, maximum);
        }
        public static Vec3 RotateTowards(Vec3 from, Vec3 to, Vec3 vector, float maxAngle)
        {
            float fl = from.Length, tl = to.Length;
            if (fl < 1e-5f || tl < 1e-5f) return vector;
            from *= 1 / fl; to *= 1 / tl;
            Vec3 axis = Vec3.CrossProduct(from, to);
            float al = axis.Length;
            if (al < 1e-5f) return vector;
            axis *= 1 / al;
            float angle = Math.Min(maxAngle, (float)Math.Acos(Math.Max(-1, Math.Min(1, Dot(from, to)))));
            float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
            return vector * c + Vec3.CrossProduct(axis, vector) * s + axis * (Dot(axis, vector) * (1 - c));
        }
    }
}
