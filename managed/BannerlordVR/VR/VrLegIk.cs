using System;
using TaleWorlds.Library;

namespace BannerlordVR.VR
{
    /// <summary>
    /// The leg: where the knee goes, and the two-bone triangle that puts it there.
    ///
    /// The triangle is the arm's, reused unchanged - VrArmIk.SolveElbowWithBend is
    /// the cosine law with a stretch instead of a clamp, and a hip-to-ankle reach
    /// past the leg's length stretches the same way an over-reaching arm does. What
    /// is different is the POLE: a knee has its own anatomy, and the animated knee
    /// is the best possible hint at where it should be.
    ///
    /// WHY THE ANIMATION IS THE HINT RATHER THAN A FIXED DIRECTION
    ///
    /// A walking gait swings the knee lift, the passing-leg lift and both
    /// plant/lift transitions through the whole legal arc. A fixed "forward" pole
    /// would fight the animation on every step and the legs would read as two
    /// stiff sticks. Taking the bend direction from the animated knee - the
    /// component of the animated knee perpendicular to the hip-ankle chord -
    /// reproduces the gait exactly when the player's body has not moved, and
    /// degrades to a forward-facing knee only when the pelvis offset has changed
    /// the geometry out from under it.
    ///
    /// PURE, LIKE THE ARM'S. The whole solve is a function of its arguments: the
    /// regression tests in the math project call it directly, and nothing here
    /// reads the live bone it is about to write.
    /// </summary>
    public static class VrLegIk
    {
        /// <summary>
        /// Which way the knee points, from the animated knee, clamped so the leg
        /// can never hyperextend.
        ///
        /// The fallbacks matter: a standing leg is straight, its animated knee
        /// lies ON the chord and names no direction at all. The toe direction is
        /// the next best answer - a knee bends the way the foot points - and the
        /// body's own forward axis is the last resort.
        /// </summary>
        public static Vec3 KneeDirection(Vec3 hip, Vec3 ankle, Vec3 animatedKnee, Vec3 toeDir)
        {
            Vec3 chord = ankle - hip;
            float d = chord.Length;

            if (d < 1e-4f)
                return Vec3.Zero;

            Vec3 axis = chord * (1f / d);

            Vec3 bend = VrArmBend.Perp(animatedKnee - hip, axis);

            if (bend.LengthSquared < 1e-6f)
                bend = VrArmBend.Perp(toeDir, axis);

            if (bend.LengthSquared < 1e-6f)
                bend = VrArmBend.Perp(VrArmBend.Forward, axis);

            if (bend.LengthSquared < 1e-6f)
                return Vec3.Zero;

            // A knee bends forward. The animation can hand back a bend pointing
            // behind the chord for one frame during a fast pelvis offset; a leg
            // that bends that way is a broken leg. Swing it around to the forward
            // side by exactly the amount needed, no more.
            if (bend.y < -0.05f)
            {
                Vec3 forward = VrArmBend.Perp(VrArmBend.Forward, axis);

                if (forward.LengthSquared > 1e-6f && forward.y > 1e-3f)
                {
                    float k = (0.05f - bend.y) / forward.y;
                    Vec3 mixed = bend + forward * k;
                    float len = mixed.Length;

                    if (len > 1e-6f)
                        bend = mixed * (1f / len);
                }
            }

            return bend;
        }

        /// <summary>
        /// Solves one leg: the knee's position and the two bone directions.
        ///
        /// hip is where the thigh's origin has ended up after the pelvis move;
        /// ankle is the animated foot's origin - the engine's own ground contact,
        /// kept rather than recomputed, which is what makes a crouch bend the knee
        /// instead of sinking the feet through the floor.
        ///
        /// Returns false when there is nothing defensible to write, in which case
        /// the caller leaves the leg to the animation for the frame.
        /// </summary>
        public static bool Solve(Vec3 hip, Vec3 ankle, Vec3 animatedKnee, Vec3 toeDir,
                                 float thighLen, float calfLen,
                                 out Vec3 knee, out Vec3 thighDir, out Vec3 calfDir)
        {
            knee = Vec3.Zero;
            thighDir = Vec3.Zero;
            calfDir = Vec3.Zero;

            if (thighLen <= 1e-4f || calfLen <= 1e-4f)
                return false;

            Vec3 bend = KneeDirection(hip, ankle, animatedKnee, toeDir);
            if (bend.LengthSquared < 1e-8f)
                return false;

            float usedThigh, usedCalf;

            if (!VrArmIk.SolveElbowWithBend(hip, ankle, bend, thighLen, calfLen,
                                            out knee, out usedThigh, out usedCalf))
                return false;

            Vec3 t = knee - hip;
            Vec3 c = ankle - knee;

            if (t.LengthSquared < 1e-10f || c.LengthSquared < 1e-10f)
                return false;

            thighDir = t.NormalizedCopy();
            calfDir = c.NormalizedCopy();
            return true;
        }
    }
}
