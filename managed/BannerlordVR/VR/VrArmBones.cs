using System;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>One arm, resolved: the bones to write and the lengths to solve with.</summary>
    public struct VrArmChain
    {
        public bool Valid;

        /// <summary>True for the character's LEFT arm. The solver needs this for
        /// the outward direction, which is the one thing that mirrors.</summary>
        public bool IsLeft;

        // The chain, shoulder outward. Clavicle rotates; Upper and Fore are the
        // two-bone solve; the two twists are skinning helpers; Hand is pinned.
        public sbyte Clavicle;
        public sbyte Upper;
        public sbyte UpperTwist;
        public sbyte Fore;
        public sbyte ForeTwist;
        public sbyte Hand;

        /// <summary>The bone a weapon hangs from - r_finger0 on a human, named by
        /// the monster definition. It sits in the PALM, which is where a controller
        /// actually is when you hold one.</summary>
        public sbyte Item;

        /// <summary>The item bone's position in the HAND bone's own frame: the
        /// wrist-to-grip offset, taken from the rig instead of guessed.</summary>
        public Vec3 ItemLocal;

        /// <summary>Shoulder to elbow, metres, from the rest pose.</summary>
        public float UpperLen;

        /// <summary>Elbow to wrist, metres, from the rest pose.</summary>
        public float ForeLen;

        /// <summary>The same two, from the REST pose. Diagnostics only: on a
        /// scaled agent these differ from the live ones by the scale, and seeing
        /// both is what turns "the hand is slightly off" into a number.</summary>
        public float RestUpperLen;
        public float RestForeLen;

        /// <summary>The direction, in the UPPER bone's own local frame, that points
        /// at the elbow. Measured from the rest pose rather than assumed, because
        /// which of a bone's three axes runs down its length is a rig convention
        /// and guessing it is three ways to be wrong.</summary>
        public Vec3 UpperAxis;

        /// <summary>The same for the forearm bone, pointing at the wrist.</summary>
        public Vec3 ForeAxis;

        /// <summary>TEST 270. How far along the forearm the ForeTwist helper sits,
        /// elbow 0 to wrist 1. Pronation builds up linearly from the elbow, so this
        /// is the share of the wrist's roll the helper carries. 0 when absent.</summary>
        public float ForeTwistFrac;

        /// <summary>TEST 450. Each bone's own length/girth ratio along its axis, from
        /// the first live frame (VrArmIk.AxisRatio): VrArmIk.Stretch keeps the rig's
        /// build and multiplies only the player's arm length onto it.</summary>
        public float UpperRatio;
        public float ForeRatio;

        /// <summary>TEST 456. Each bone's own scale along its axis on the first live
        /// frame (1 for an unscaled rig): VrArmIk.ScaleTo sets it to this x ArmScale.</summary>
        public float UpperAlong;
        public float ForeAlong;

        public float Reach { get { return UpperLen + ForeLen; } }
    }

    /// <summary>
    /// Resolves the arm chain from the game's own bone map, and measures the two
    /// segment lengths the IK needs.
    ///
    /// THE NAMING TRAP THIS EXISTS TO STEP OVER
    ///
    /// Read as names, this skeleton looks like it has no elbow:
    ///
    ///     l_clavicle -> l_upperarm_twist -> l_upperarm_twist1
    ///                -> l_foretwist -> l_foretwist1 -> l_hand
    ///
    /// Four of the six read as "twist", and this repo's own notes concluded there
    /// was no dedicated elbow bone and that a two-bone solve therefore had nowhere
    /// to put one. That is a naming artifact, and it is worth being precise about
    /// because it is part of why the IK was deferred.
    ///
    /// TaleWorlds.Core.HumanBone names the same bones by ROLE, and
    /// MBAgentVisuals.GetRealBoneIndex maps role to real index:
    ///
    ///     ShoulderL       = l_clavicle          clavicle
    ///     UpperarmL       = l_upperarm_twist    the humerus      &lt;- solved
    ///     UpperarmTwist1L = l_upperarm_twist1   skinning helper
    ///     ForearmL        = l_foretwist         THE ELBOW        &lt;- solved
    ///     Forearm1L       = l_foretwist1        skinning helper
    ///     HandL           = l_hand              the wrist        &lt;- pinned
    ///     ItemL           = l_finger0           weapon attach
    ///
    /// The last row is the proof rather than an assumption: monsters.xml declares
    /// off_hand_item_bone = l_finger0, which pins ItemL to that bone, and the six
    /// above it follow by parent order. So it is an ordinary two-bone chain and
    /// the conventions are readable rather than guessed - GetBoneEntitialRestFrame
    /// gives every bone's rest position and orientation in entity space.
    ///
    /// WHY THE LENGTHS COME FROM ENTITIAL REST AND NOT LOCAL TRANSLATIONS
    ///
    /// A bone's parent-local translation is its offset from its parent, so the
    /// naive segment length is |localRest(Fore)|. That is wrong here: a twist
    /// helper sits between Upper and Fore, so the shoulder-to-elbow distance is
    /// two local translations composed, not one. Differencing two ENTITIAL rest
    /// positions steps over the helpers without composing anything and is correct
    /// whatever the chain looks like.
    /// </summary>
    public static class VrArmBones
    {
        private static bool _logged;
        private static bool _broken;

        private static VrArmChain _left;
        private static VrArmChain _right;
        private static bool _resolved;
        private static UIntPtr _resolvedFor = UIntPtr.Zero;
        private static Vec3 _visualsEntityScale = new Vec3(1f, 1f, 1f);
        private static Vec3 _entityOrigin;
        private static Vec3 _visualsOrigin;

        /// <summary>
        /// The arm that ends in this hand bone, or an invalid chain. Matching on
        /// the hand bone rather than assuming main = right is deliberate: which
        /// hand is "main" comes from the monster definition and a left-handed or
        /// non-human monster is allowed to disagree.
        /// </summary>
        public static bool TryChainForHand(Agent agent, MBAgentVisuals visuals, Skeleton skeleton,
                                           sbyte handBone, out VrArmChain chain)
        {
            chain = default(VrArmChain);

            if (_broken || skeleton == null || visuals == null || handBone < 0)
                return false;

            // Captured for the one-shot log in Describe. Read here because the
            // entity is to hand and the resolver below does not take one.
            try
            {
                GameEntity e = visuals.GetEntity();
                if (e != null)
                {
                    MatrixFrame ef = e.GetGlobalFrame();
                    _visualsEntityScale = ef.rotation.GetScaleVector();
                    _entityOrigin = ef.origin;
                }

                // The visuals carry a frame of their own, and whether the skeleton's
                // entitial frames are relative to THIS or to the entity is not
                // something the names settle. If the two origins differ, every hand
                // target is offset by the difference - which would look exactly like
                // a scale error and is not one, so both are measured together rather
                // than one after the other.
                _visualsOrigin = visuals.GetGlobalFrame().origin;
            }
            catch (Exception)
            {
                // Diagnostics only.
            }

            if (!Resolve(visuals, skeleton))
                return false;

            if (_left.Valid && _left.Hand == handBone)
            {
                chain = _left;
                return true;
            }

            if (_right.Valid && _right.Hand == handBone)
            {
                chain = _right;
                return true;
            }

            return false;
        }

        /// <summary>The left or right arm of this skeleton, by side rather than by
        /// hand bone (TEST 377d: the wrist HUD hangs off whichever arm is drawn
        /// on that side).</summary>
        public static bool TryChain(MBAgentVisuals visuals, Skeleton skeleton, bool left,
                                    out VrArmChain chain)
        {
            chain = default(VrArmChain);
            if (_broken || skeleton == null || visuals == null || !Resolve(visuals, skeleton))
                return false;

            chain = left ? _left : _right;
            return chain.Valid;
        }

        /// <summary>Resolves both arms once per skeleton. Cheap enough to re-check
        /// every frame, but the skeleton pointer changes when the agent does, and
        /// bone indices are per-skeleton.</summary>
        private static bool Resolve(MBAgentVisuals visuals, Skeleton skeleton)
        {
            try
            {
                UIntPtr key = skeleton.Pointer;
                if (_resolved && key == _resolvedFor)
                    return _left.Valid || _right.Valid;

                _resolvedFor = key;
                _resolved = true;
                _logged = false;

                _left = Build(visuals, skeleton, true);
                _right = Build(visuals, skeleton, false);

                Describe(skeleton);

                return _left.Valid || _right.Valid;
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("Resolving the arm bones failed; the arm IK stays off and the "
                            + "hands keep posing on their own.", ex);
                return false;
            }
        }

        private static VrArmChain Build(MBAgentVisuals visuals, Skeleton skeleton, bool left)
        {
            VrArmChain c = default(VrArmChain);
            c.IsLeft = left;

            c.Clavicle   = visuals.GetRealBoneIndex(left ? HumanBone.ShoulderL       : HumanBone.ShoulderR);
            c.Upper      = visuals.GetRealBoneIndex(left ? HumanBone.UpperarmL       : HumanBone.UpperarmR);
            c.UpperTwist = visuals.GetRealBoneIndex(left ? HumanBone.UpperarmTwist1L : HumanBone.UpperarmTwist1R);
            c.Fore       = visuals.GetRealBoneIndex(left ? HumanBone.ForearmL        : HumanBone.ForearmR);
            c.ForeTwist  = visuals.GetRealBoneIndex(left ? HumanBone.Forearm1L       : HumanBone.Forearm1R);
            c.Hand       = visuals.GetRealBoneIndex(left ? HumanBone.HandL           : HumanBone.HandR);
            c.Item       = visuals.GetRealBoneIndex(left ? HumanBone.ItemL           : HumanBone.ItemR);

            // The twists are optional - they only distribute skinning - but the
            // three solved joints are not.
            if (c.Upper < 0 || c.Fore < 0 || c.Hand < 0)
                return c;

            sbyte count = skeleton.GetBoneCount();
            if (c.Upper >= count || c.Fore >= count || c.Hand >= count)
                return c;

            // useBoneMapping: false, because GetRealBoneIndex already returned REAL
            // indices and mapping them a second time would resolve a different bone.
            // Describe() prints the mapped numbers too, so a wrong choice here shows
            // up as an implausible length on the first run rather than as a subtly
            // short arm nobody can explain.
            MatrixFrame restUpper = skeleton.GetBoneEntitialRestFrame(c.Upper, false);
            MatrixFrame restFore  = skeleton.GetBoneEntitialRestFrame(c.Fore,  false);
            MatrixFrame restHand  = skeleton.GetBoneEntitialRestFrame(c.Hand,  false);

            Vec3 shoulder = restUpper.origin;
            Vec3 elbow    = restFore.origin;
            Vec3 wrist    = restHand.origin;

            // MEASURED FROM THE LIVE POSE, NOT THE REST POSE, AND THIS IS THE POINT.
            //
            // The two are not in the same space. A scaled agent's LIVE entitial
            // frames carry that scale; its REST frames do not - measured in game at
            // 1.0615, rest shoulder x = 0.186 against a live 0.20. The solve works
            // entirely in live frames, so rest-derived lengths are about six percent
            // short, and the arm lands that fraction of its own length away from the
            // target: 0.0615 x 0.557 m = 34 mm, against a measured 27 mm residual.
            //
            // Bones are rigid, so the distance between two joints is the same in any
            // pose - taking it from the live frames costs nothing in stability and
            // is correct by construction whatever the agent's scale turns out to be.
            // Read before anything has been written this frame, so it cannot feed on
            // our own output.
            Vec3 liveShoulder = skeleton.GetBoneEntitialFrameWithIndex(c.Upper).origin;
            Vec3 liveElbow    = skeleton.GetBoneEntitialFrameWithIndex(c.Fore).origin;
            Vec3 liveWrist    = skeleton.GetBoneEntitialFrameWithIndex(c.Hand).origin;

            c.UpperLen = (liveElbow - liveShoulder).Length;
            c.ForeLen  = (liveWrist - liveElbow).Length;

            // Kept for the log below, so the discrepancy is visible rather than
            // inferred from a hand that is slightly in the wrong place.
            c.RestUpperLen = (elbow - shoulder).Length;
            c.RestForeLen  = (wrist - elbow).Length;

            // A live frame can be caught mid-stretch by an animation that scales
            // bones. Fall back to rest rather than trusting an absurd measurement.
            if (c.UpperLen < 0.05f || c.UpperLen > 1.0f)
                c.UpperLen = c.RestUpperLen;
            if (c.ForeLen < 0.05f || c.ForeLen > 1.0f)
                c.ForeLen = c.RestForeLen;

            if (c.ForeTwist >= 0 && c.ForeTwist < count && c.ForeLen > 1e-4f)
            {
                Vec3 liveTwist = skeleton.GetBoneEntitialFrameWithIndex(c.ForeTwist).origin;
                c.ForeTwistFrac = Math.Min(1f, (liveTwist - liveElbow).Length / c.ForeLen);
            }

            // WRIST TO GRIP, FROM THE RIG RATHER THAN BY EYE.
            //
            // The hand BONE is the wrist joint. OpenXR defines a controller's grip
            // pose at the palm centroid of a closed fist. Pinning one to the other
            // puts the wrist where the palm is and leaves the hand sticking out
            // past the controller - which is exactly what it looks like in game.
            //
            // The rig already knows the distance: the item bone is where a weapon
            // hangs, which is the point in the palm your hand closes around. Taking
            // the offset from there needs no tuning and is right for any monster,
            // any scale, and either hand.
            if (c.Item >= 0 && c.Item < count)
            {
                MatrixFrame liveHand = skeleton.GetBoneEntitialFrameWithIndex(c.Hand);
                MatrixFrame liveItem = skeleton.GetBoneEntitialFrameWithIndex(c.Item);

                Vec3 local = liveHand.TransformToLocalNonOrthogonal(liveItem).origin;

                // A grip more than a hand's length from the wrist is not a grip.
                if (local.Length < 0.30f)
                    c.ItemLocal = local;
            }

            // Which way each bone points at its child, in that bone's own frame.
            // Orthonormalised first: bone frames carry scale, and a scaled basis
            // would skew the direction rather than just lengthen it.
            if (c.UpperLen > 1e-5f && c.ForeLen > 1e-5f)
            {
                Mat3 ru = restUpper.rotation; ru.Orthonormalize();
                Mat3 rf = restFore.rotation;  rf.Orthonormalize();

                c.UpperAxis = ru.TransformToLocal((elbow - shoulder).NormalizedCopy());
                c.ForeAxis  = rf.TransformToLocal((wrist - elbow).NormalizedCopy());

                // TEST 450: the live bones' own ratio, read before any write.
                c.UpperRatio = VrArmIk.AxisRatio(skeleton.GetBoneEntitialFrameWithIndex(c.Upper).rotation, c.UpperAxis);
                c.ForeRatio  = VrArmIk.AxisRatio(skeleton.GetBoneEntitialFrameWithIndex(c.Fore).rotation, c.ForeAxis);
                c.UpperAlong = skeleton.GetBoneEntitialFrameWithIndex(c.Upper).rotation.TransformToParent(c.UpperAxis).Length;
                c.ForeAlong  = skeleton.GetBoneEntitialFrameWithIndex(c.Fore).rotation.TransformToParent(c.ForeAxis).Length;
            }

            // A human upper arm and forearm are each roughly 0.20 to 0.45 m, and an
            // agent may be scaled. Anything outside a generous window means the
            // rest pose was not what we think it is, and solving with it would put
            // the elbow somewhere arbitrary rather than fail visibly.
            if (c.UpperLen < 0.05f || c.UpperLen > 1.0f || c.ForeLen < 0.05f || c.ForeLen > 1.0f)
                return c;

            c.Valid = true;
            return c;
        }

        /// <summary>
        /// Prints both chains once. This is the check that the HumanBone mapping
        /// landed on the bones we think it did - the names must read clavicle,
        /// upperarm, upperarm twist, foretwist, foretwist1, hand.
        /// </summary>
        private static void Describe(Skeleton skeleton)
        {
            if (_logged)
                return;

            _logged = true;

            Line(skeleton, _left, "Left");
            Line(skeleton, _right, "Right");

            // The agent's own scale, which is why the world-to-entitial conversion
            // has to use a true inverse rather than a transpose. Anything other
            // than 1.000 here means the two differ, by that factor squared on every
            // distance from the agent's origin - which is metres at the hand.
            try
            {
                Vec3 s = _visualsEntityScale;
                VrLog.Info(string.Format(
                    "Agent entity scale ({0:F4}, {1:F4}, {2:F4}). At 1.000 the transpose and "
                    + "the inverse agree and this matters to nothing; away from it, only the "
                    + "inverse is correct and the hand is off by roughly (scale^2 - 1) times "
                    + "its height above the agent's origin.",
                    s.x, s.y, s.z));

                Vec3 d = _visualsOrigin - _entityOrigin;
                VrLog.Info(string.Format(
                    "Agent visuals origin minus entity origin ({0:F4}, {1:F4}, {2:F4}) m. The "
                    + "hand target is built in the ENTITY's frame, so anything other than zero "
                    + "here is a constant offset on every hand position - and it would look "
                    + "just like the scale error above without being one.",
                    d.x, d.y, d.z));
            }
            catch (Exception)
            {
                // Diagnostics only.
            }
        }

        private static void Line(Skeleton skeleton, VrArmChain c, string side)
        {
            var sb = new StringBuilder();
            sb.Append("Arm chain, ").Append(side).Append(": ");

            if (c.Upper < 0 || c.Fore < 0 || c.Hand < 0)
            {
                sb.Append("NOT RESOLVED - the monster does not declare the arm bones, so "
                          + "the arm IK cannot run on it. Hand posing is unaffected.");
                VrLog.Info(sb.ToString());
                return;
            }

            Append(sb, skeleton, "clavicle", c.Clavicle);
            Append(sb, skeleton, "upper", c.Upper);
            Append(sb, skeleton, "uptwist", c.UpperTwist);
            Append(sb, skeleton, "fore", c.Fore);
            Append(sb, skeleton, "foretwist", c.ForeTwist);
            Append(sb, skeleton, "hand", c.Hand);
            Append(sb, skeleton, "item", c.Item);

            VrLog.Info(sb.ToString());

            VrLog.Info(string.Format(
                "Arm chain, {0}: wrist-to-grip offset ({1:F3}, {2:F3}, {3:F3}) m in the hand's "
                + "own frame, {4:F3} m long. The wrist is set back by this so the GRIP lands on "
                + "the controller - a controller sits in your palm, and the bone being placed is "
                + "the wrist, so without it the hand hangs a palm's length past the controller. "
                + "Zero means the item bone was not found and the wrist is pinned directly, "
                + "which is the old behaviour. arm_ik_grip_at_item scales it.",
                side, c.ItemLocal.x, c.ItemLocal.y, c.ItemLocal.z, c.ItemLocal.Length));

            VrLog.Info(string.Format(
                "TEST 270 arm chain, {0}: the forearm twist helper sits {1:F2} of the way from "
                + "elbow to wrist, so it carries that share of the wrist's roll. 0 means no helper "
                + "was found and the wrist takes all of it, as before.",
                side, c.ForeTwistFrac));

            // Both mappings, so the useBoneMapping choice in Build is visible rather
            // than assumed. The two should differ; the false row is the one used.
            float upMapped = 0f, foreMapped = 0f;
            try
            {
                Vec3 s = skeleton.GetBoneEntitialRestFrame(c.Upper, true).origin;
                Vec3 e = skeleton.GetBoneEntitialRestFrame(c.Fore, true).origin;
                Vec3 w = skeleton.GetBoneEntitialRestFrame(c.Hand, true).origin;
                upMapped = (e - s).Length;
                foreMapped = (w - e).Length;
            }
            catch (Exception)
            {
                // Only diagnostics; a mapped read that throws is itself the answer.
            }

            VrLog.Info(string.Format(
                "Arm chain, {0}: rest lengths upper {1:F3} m, forearm {2:F3} m, reach {3:F3} m "
                + "(unmapped, used). With bone mapping they would read {4:F3} / {5:F3} m. The "
                + "used pair should look like a human arm at this agent's scale - roughly 0.20 "
                + "to 0.40 m each. If it does not, the mapping flag in VrArmBones.Build is the "
                + "first thing to change. Valid: {6}.",
                side, c.UpperLen, c.ForeLen, c.Reach, upMapped, foreMapped, c.Valid));

            // Live against rest. The ratio is the agent's scale, and it is the
            // reason the solve measures from live frames: the rest pose is in
            // unscaled entity units while everything the solve touches is not.
            float ratio = c.RestUpperLen > 1e-4f ? c.UpperLen / c.RestUpperLen : 0f;
            VrLog.Info(string.Format(
                "Arm chain, {0}: live lengths {1:F3} / {2:F3} m against rest {3:F3} / {4:F3} m, "
                + "ratio {5:F4}. A ratio of 1.000 means the agent is unscaled and the two are "
                + "interchangeable; anything else is the agent's scale, and solving with the "
                + "rest pair would put the hand about {6:F0} mm short of the controller.",
                side, c.UpperLen, c.ForeLen, c.RestUpperLen, c.RestForeLen, ratio,
                Math.Abs(ratio - 1f) * (c.RestUpperLen + c.RestForeLen) * 1000f));

            // The measured bone axes. Each should be close to a signed unit axis -
            // a rig normally runs one of s/f/u down the bone. Something like
            // (0.6, 0.8, 0.0) is not wrong in itself, but it means the rig is
            // skewed and is worth knowing before blaming the solver for a twist.
            VrLog.Info(string.Format(
                "Arm chain, {0}: bone axis toward the elbow, in the upper arm's own frame, is "
                + "({1:F3}, {2:F3}, {3:F3}); toward the wrist, in the forearm's frame, "
                + "({4:F3}, {5:F3}, {6:F3}). TEST 450: length/girth {7:F3} / {8:F3} (1 = uniform), "
                + "arm length x{9:F2}; TEST 456: own scale along {10:F3} / {11:F3}.",
                side, c.UpperAxis.x, c.UpperAxis.y, c.UpperAxis.z,
                c.ForeAxis.x, c.ForeAxis.y, c.ForeAxis.z, c.UpperRatio, c.ForeRatio, VrArmIk.ArmScale,
                c.UpperAlong, c.ForeAlong));

            // Handedness, confirmed rather than assumed. VrArmBend takes the
            // character's right to be +X in entitial space, so the RIGHT shoulder
            // must sit at positive X and the LEFT at negative. If these come back
            // with the signs swapped, every outward-facing constraint in the bend
            // direction is being applied to the wrong side and the elbows will
            // splay inward - which looks like a solver bug and is not one.
            try
            {
                Vec3 sh = skeleton.GetBoneEntitialRestFrame(c.Upper, false).origin;
                VrLog.Info(string.Format(
                    "Arm chain, {0}: shoulder rest position ({1:F3}, {2:F3}, {3:F3}). Expect "
                    + "x to be {4} on this side, z to be the tallest component, and y near "
                    + "zero - that is X right, Y forward, Z up.",
                    side, sh.x, sh.y, sh.z, c.IsLeft ? "NEGATIVE" : "POSITIVE"));
            }
            catch (Exception)
            {
                // Diagnostics only.
            }
        }

        private static void Append(StringBuilder sb, Skeleton skeleton, string role, sbyte bone)
        {
            sb.Append(role).Append("=[").Append(bone).Append("] ");

            if (bone >= 0 && bone < skeleton.GetBoneCount())
                sb.Append(skeleton.GetBoneName(bone) ?? "?");
            else
                sb.Append("MISSING");

            sb.Append("  ");
        }

        public static void Reset()
        {
            _resolved = false;
            _resolvedFor = UIntPtr.Zero;
            _logged = false;
            _left = default(VrArmChain);
            _right = default(VrArmChain);
        }
    }
}
