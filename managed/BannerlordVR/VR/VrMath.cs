using System;
using BannerlordVR.Interop;
using TaleWorlds.Library;

namespace BannerlordVR.VR
{
    /// <summary>
    /// The single place where OpenXR space is converted to Bannerlord space.
    /// Nothing else in this mod may perform a basis swap.
    ///
    ///                OpenXR              Bannerlord (MatrixFrame)
    ///   right          +X                        +X   (Mat3.s)
    ///   up             +Y                        +Z   (Mat3.u)
    ///   forward        -Z                        +Y   (Mat3.f)
    ///   handedness   right                      right
    ///   units        metres                     metres
    ///
    /// So a direction maps as:  bl = (xr.x, -xr.z, xr.y)
    ///
    /// Rotations are converted by mapping each of the three rotated basis vectors
    /// individually rather than by conjugating a matrix. It is the same result and
    /// it is obviously correct on inspection, which matters more here than elegance:
    /// applying the swap twice, or once in the wrong direction, is the root cause of
    /// almost every "the horizon is tilted" / "my head rolls when I turn" VR bug.
    /// </summary>
    public static class VrMath
    {
        /// <summary>OpenXR position/direction -> Bannerlord Vec3.</summary>
        public static Vec3 ToBannerlord(BvrVec3 v) => new Vec3(v.X, -v.Z, v.Y, -1f);

        /// <summary>Bannerlord Vec3 -> OpenXR. Inverse of <see cref="ToBannerlord(BvrVec3)"/>.</summary>
        public static BvrVec3 ToOpenXr(Vec3 v) => new BvrVec3 { X = v.x, Y = v.z, Z = -v.y };

        /// <summary>Halfway between two OpenXR positions. Used for the head pose,
        /// which sits midway between the two eyes.</summary>
        public static BvrVec3 Midpoint(BvrVec3 a, BvrVec3 b) => new BvrVec3
        {
            X = (a.X + b.X) * 0.5f,
            Y = (a.Y + b.Y) * 0.5f,
            Z = (a.Z + b.Z) * 0.5f,
        };

        /// <summary>OpenXR orientation -> Bannerlord rotation basis.</summary>
        public static Mat3 ToBannerlord(BvrQuat q)
        {
            // Rotated basis vectors, still in OpenXR space.
            BvrVec3 xrRight = Rotate(q, 1f, 0f, 0f);
            BvrVec3 xrUp = Rotate(q, 0f, 1f, 0f);
            BvrVec3 xrForward = Rotate(q, 0f, 0f, -1f);   // OpenXR looks down -Z

            Mat3 m = default;
            m.s = ToBannerlord(xrRight);    // side
            m.f = ToBannerlord(xrForward);  // forward
            m.u = ToBannerlord(xrUp);       // up
            return m;
        }

        /// <summary>Full OpenXR pose -> Bannerlord frame, in the pose's own space.</summary>
        public static MatrixFrame ToBannerlord(BvrQuat orientation, BvrVec3 position)
        {
            MatrixFrame frame = default;
            frame.rotation = ToBannerlord(orientation);
            frame.origin = ToBannerlord(position);
            return frame;
        }

        /// <summary>Rotate (x,y,z) by quaternion q. Standard v + 2q_v x (q_v x v + w v).</summary>
        private static BvrVec3 Rotate(BvrQuat q, float x, float y, float z)
        {
            // t = 2 * cross(q.xyz, v)
            float tx = 2f * (q.Y * z - q.Z * y);
            float ty = 2f * (q.Z * x - q.X * z);
            float tz = 2f * (q.X * y - q.Y * x);

            // v' = v + w*t + cross(q.xyz, t)
            return new BvrVec3
            {
                X = x + q.W * tx + (q.Y * tz - q.Z * ty),
                Y = y + q.W * ty + (q.Z * tx - q.X * tz),
                Z = z + q.W * tz + (q.X * ty - q.Y * tx),
            };
        }

        /// <summary>
        /// Flat heading basis from an arbitrary world-space forward vector, for
        /// callers that have a direction rather than a full rotation (an agent's
        /// LookDirection, say). Same contract as <see cref="YawOnly"/>: the result
        /// is level, unit, and never NaN.
        /// </summary>
        public static Mat3 HeadingFromForward(Vec3 forward)
        {
            Mat3 m = default;
            m.f = forward;
            m.u = new Vec3(0f, 0f, 1f, -1f);
            m.s = new Vec3(1f, 0f, 0f, -1f);   // only consulted if forward is vertical
            return YawOnly(m);
        }

        /// <summary>
        /// Strips pitch and roll, keeping only the yaw (heading) about Bannerlord's
        /// up axis (+Z). The mission camera's pitch must never reach the headset:
        /// the player's neck owns pitch and roll, the game owns yaw. Coupling engine
        /// pitch into the HMD view is the fastest route to motion sickness.
        /// </summary>
        public static Mat3 YawOnly(Mat3 rotation)
        {
            Vec3 forward = rotation.f;
            forward.z = 0f;

            // Degenerate when the camera looks straight up or down; fall back to the
            // side vector, which is horizontal in every case that matters.
            if (forward.LengthSquared < 1E-6f)
            {
                forward = rotation.s;
                forward.z = 0f;
                if (forward.LengthSquared < 1E-6f)
                    return Mat3.Identity;
                // Rotate the side vector 90 degrees to recover a forward.
                forward = new Vec3(-forward.y, forward.x, 0f, -1f);
            }

            forward.Normalize();

            Mat3 m = default;
            m.f = forward;
            m.u = new Vec3(0f, 0f, 1f, -1f);
            m.s = Vec3.CrossProduct(m.f, m.u);
            m.s.Normalize();
            return m;
        }

        /// <summary>
        /// Converts an OpenXR asymmetric FOV into near-plane frustum extents.
        /// OpenXR angles are signed half-angles from the view axis: AngleLeft and
        /// AngleDown are normally negative, so the extents come out signed correctly.
        ///
        /// These feed Camera.SetViewVolume, whose real signature was read off the
        /// shipped TaleWorlds.Engine.dll during Phase 3:
        ///
        ///     SetViewVolume(bool perspective, float dLeft, float dRight,
        ///                   float dBottom, float dTop, float dNear, float dFar)
        ///
        /// The leading flag is named <c>perspective</c>, so it is passed TRUE.
        /// Passing false asks for an orthographic projection, which renders
        /// without error and simply looks wrong - flat, with no depth - which is
        /// exactly the kind of failure that gets misdiagnosed as a tracking or
        /// IPD problem.
        /// </summary>
        public static void FovToFrustum(BvrFov fov, float near,
            out float left, out float right, out float bottom, out float top)
        {
            left = (float)Math.Tan(fov.AngleLeft) * near;
            right = (float)Math.Tan(fov.AngleRight) * near;
            bottom = (float)Math.Tan(fov.AngleDown) * near;
            top = (float)Math.Tan(fov.AngleUp) * near;
        }
    }
}
