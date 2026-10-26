using System;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    // The rotation half of the B&S pin: a slerp drive (spring 800, damper 50,
    // max 100 N.m) turns every part towards its animated orientation. The
    // position pins alone held where a part is, never how it is turned, so
    // hands, feet, head and limbs twisted freely. Applied through the engine's
    // PxRigidBody::addTorque in PhysX acceleration mode, so the gains are per
    // unit inertia: like the position pins, a light part's natural frequency is
    // capped for an explicit drive arriving one PhysX step late.
    public static class RotationPin
    {
        public const float SpringRotation = 800, DamperRotation = 50, MaxRotation = 100;
        // Part inertia estimated as m * r^2 with r = 0.11 m (a limb's radius
        // of gyration about its long axes is 0.08-0.15 m).
        public const float GyrationSquared = .012f;

        // Rotation vector (axis * angle, world) turning current onto target;
        // bases are each frame's s, f, u axes in world space.
        public static Vec3 Error(Vec3 cs, Vec3 cf, Vec3 cu, Vec3 ts, Vec3 tf, Vec3 tu)
        {
            Vec3 sum = Cross(cs, ts) + Cross(cf, tf) + Cross(cu, tu);
            float cos = (InteractionMath.Dot(cs, ts) + InteractionMath.Dot(cf, tf) + InteractionMath.Dot(cu, tu) - 1) * .5f;
            Vec3 sinAxis = sum * .5f;
            float sin = sinAxis.Length;
            if (sin < 1e-6f) return Vec3.Zero;
            float angle = (float)Math.Atan2(sin, Math.Max(-1, Math.Min(1, cos)));
            return sinAxis * (angle / sin);
        }

        // Angular acceleration for one part. springScale / damperScale are the
        // state's multipliers (Destabilized 0.5 / 0.1, on the ground 0.2).
        public static Vec3 Acceleration(Vec3 error, Vec3 angularVelocity, float mass, float springScale, float damperScale, float multiplier)
        {
            if (!InteractionMath.IsFinite(error) || !InteractionMath.IsFinite(angularVelocity) || mass <= 0) return Vec3.Zero;
            float inertia = mass * GyrationSquared;
            float k = Math.Min(SpringRotation * springScale * multiplier / inertia,
                RagdollPinDrive.MaxNaturalFrequency * RagdollPinDrive.MaxNaturalFrequency * springScale * multiplier);
            float c = Math.Min(DamperRotation * damperScale * multiplier / inertia, RagdollPinDrive.MaxDampingRate * Math.Max(damperScale, .1f) * multiplier);
            Vec3 a = error * k - angularVelocity * c;
            return InteractionMath.ClampMagnitude(a, MaxRotation * multiplier / inertia);
        }

        public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
    }
}
