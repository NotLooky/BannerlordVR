using System;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>One leg, resolved: the bones to write and the lengths to solve with.
    ///
    /// The chain is thigh -> calf -> foot (-> toe), and unlike the arm there are no
    /// twist helpers in between: the three joints hang off each other directly, which
    /// is why the leg solve below is the clean two-bone case.</summary>
    public struct VrLegChain
    {
        public bool Valid;

        public bool IsLeft;

        public sbyte Thigh;
        public sbyte Calf;
        public sbyte Foot;
        public sbyte Toe;

        /// <summary>Hip to knee, metres, from the live pose.</summary>
        public float ThighLen;

        /// <summary>Knee to ankle, metres, from the live pose.</summary>
        public float CalfLen;

        /// <summary>The direction, in the thigh's own frame, that points at the
        /// knee. Measured from the rest pose, like the arm axes, because which of
        /// a bone's three local axes runs down its length is rig data.</summary>
        public Vec3 ThighAxis;

        /// <summary>The same for the calf, pointing at the ankle.</summary>
        public Vec3 CalfAxis;
    }

    /// <summary>The spine from the pelvis to the chest, resolved.
    ///
    /// pelvis is the skeleton ROOT (parent -1), so its local frame is its entitial
    /// frame and the whole body hangs off it. The two adjustable segments of the
    /// torso solve are spine->spine1 and spine1->spine2; spine2 (HumanBone's
    /// Thorax) is the chest, the end the solve places and the arms attach to.</summary>
    public struct VrSpineChain
    {
        public bool Valid;

        public sbyte Pelvis;
        public sbyte Spine;
        public sbyte Spine1;
        public sbyte Spine2;
        public sbyte Neck;
        public sbyte Head;

        /// <summary>spine -> spine1, metres, from the live pose.</summary>
        public float LowerLen;

        /// <summary>spine1 -> spine2, metres, from the live pose.</summary>
        public float UpperLen;

        /// <summary>In the spine bone's own frame, pointing at spine1.</summary>
        public Vec3 SpineAxis;

        /// <summary>In the spine1 bone's own frame, pointing at spine2.</summary>
        public Vec3 Spine1Axis;
    }

    /// <summary>
    /// Resolves the pelvis, the spine and both legs from the game's own bone map,
    /// and measures the lengths the solves need.
    ///
    /// WHY THE ROLE MAP AND NOT THE NAMES. The arm work established this and it
    /// holds here: TaleWorlds.Core.HumanBone names bones by ROLE and
    /// MBAgentVisuals.GetRealBoneIndex maps role to real index, so
    ///
    ///     Abdomen  = pelvis      (the root)
    ///     Spine1   = spine       \
    ///     Spine2   = spine1       >  the torso, solved from the pelvis to spine2
    ///     Thorax   = spine2      /
    ///     ThighL   = l_thigh  -> l_calf -> l_foot -> l_toe0
    ///
    /// and the live log confirms every one of those names on the player skeleton.
    ///
    /// The lengths come from LIVE entitial frames rather than the rest pose,
    /// exactly as VrArmBones explains: a scaled agent's rest frames are in
    /// unscaled entity units while the solve works in scaled ones, so a
    /// rest-derived length is short by the agent's scale and the leg lands that
    /// fraction of its own length away from the foot.
    /// </summary>
    public static class VrBodyBones
    {
        private static bool _logged;
        private static bool _broken;

        private static VrSpineChain _spine;
        private static VrLegChain _left;
        private static VrLegChain _right;

        private static bool _resolved;
        private static UIntPtr _resolvedFor = UIntPtr.Zero;

        /// <summary>
        /// TEST 388: the DRAWN body's frame in the world. Axes: the entity's own
        /// (x right, y forward, z up) turned as the drawn chest is turned from its
        /// rest pose; origin: the drawn head bone - the head's pivot, on the line
        /// of the ears (the eyes sit 0.10 m forward of it, TEST 352e). Read after
        /// the hands pass, so it is the body you see: bending, crouching, the
        /// chest's twist and the horse are all in it, and no headset or room
        /// frame is. False when the skeleton cannot be resolved.
        /// </summary>
        internal static bool TryBodyFrame(Agent agent, out MatrixFrame body)
        {
            body = MatrixFrame.Identity;
            MBAgentVisuals visuals = agent?.AgentVisuals;
            Skeleton skeleton = visuals?.GetSkeleton();
            if (skeleton == null || !TryResolve(visuals, skeleton, out VrSpineChain spine, out _, out _)
                || !spine.Valid || spine.Head < 0)
                return false;

            MatrixFrame frame = visuals.GetFrame();
            body.rotation = BodyAxes(frame.rotation,
                                     skeleton.GetBoneEntitialFrameWithIndex(spine.Spine2).rotation,
                                     skeleton.GetBoneEntitialRestFrame(spine.Spine2, false).rotation);
            body.origin = frame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(spine.Head).origin);
            return true;
        }

        /// <summary>TEST 388: the entity's axes turned by chestNow · chestRestᵀ -
        /// the chest's turn from rest, whatever the bone's own axis convention.
        /// All three orthonormalised. Pure; the math tests call it.</summary>
        internal static Mat3 BodyAxes(Mat3 entity, Mat3 chestNow, Mat3 chestRest)
        {
            entity.Orthonormalize();
            chestNow.Orthonormalize();
            chestRest.Orthonormalize();

            Mat3 r = Mat3.Identity;
            r.s = entity.TransformToParent(chestNow.TransformToParent(chestRest.TransformToLocal(new Vec3(1f, 0f, 0f))));
            r.f = entity.TransformToParent(chestNow.TransformToParent(chestRest.TransformToLocal(new Vec3(0f, 1f, 0f))));
            r.u = entity.TransformToParent(chestNow.TransformToParent(chestRest.TransformToLocal(new Vec3(0f, 0f, 1f))));
            return r;
        }

        /// <summary>Resolves both legs and the spine once per skeleton.</summary>
        public static bool TryResolve(MBAgentVisuals visuals, Skeleton skeleton,
                                      out VrSpineChain spine, out VrLegChain left,
                                      out VrLegChain right)
        {
            spine = default(VrSpineChain);
            left = default(VrLegChain);
            right = default(VrLegChain);

            if (_broken || visuals == null || skeleton == null)
                return false;

            if (!Resolve(visuals, skeleton))
                return false;

            spine = _spine;
            left = _left;
            right = _right;
            return _spine.Valid || _left.Valid || _right.Valid;
        }

        private static bool Resolve(MBAgentVisuals visuals, Skeleton skeleton)
        {
            try
            {
                UIntPtr key = skeleton.Pointer;
                if (_resolved && key == _resolvedFor)
                    return _spine.Valid || _left.Valid || _right.Valid;

                _resolvedFor = key;
                _resolved = true;
                _logged = false;

                _spine = BuildSpine(visuals, skeleton);
                _left = BuildLeg(visuals, skeleton, true);
                _right = BuildLeg(visuals, skeleton, false);

                // The pelvis is the legs' parent as well as the spine's, so a
                // skeleton whose spine roles do not form a chain may still have a
                // usable root. Only the TORSO solve needs the full chain; the legs
                // need this one bone.
                if (_spine.Pelvis < 0)
                {
                    if (_left.Valid)
                        _spine.Pelvis = skeleton.GetParentBoneIndex(_left.Thigh);
                    else if (_right.Valid)
                        _spine.Pelvis = skeleton.GetParentBoneIndex(_right.Thigh);
                }

                Describe(skeleton);

                return _spine.Valid || _left.Valid || _right.Valid;
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("Resolving the body bones failed; the full-body IK stays off "
                            + "and the arms keep working on their own.", ex);
                return false;
            }
        }

        private static VrSpineChain BuildSpine(MBAgentVisuals visuals, Skeleton skeleton)
        {
            VrSpineChain c = default(VrSpineChain);

            c.Pelvis = visuals.GetRealBoneIndex(HumanBone.Abdomen);
            c.Spine = visuals.GetRealBoneIndex(HumanBone.Spine1);
            c.Spine1 = visuals.GetRealBoneIndex(HumanBone.Spine2);
            c.Spine2 = visuals.GetRealBoneIndex(HumanBone.Thorax);
            c.Neck = visuals.GetRealBoneIndex(HumanBone.Neck);
            c.Head = visuals.GetRealBoneIndex(HumanBone.Head);

            sbyte count = skeleton.GetBoneCount();

            if (c.Pelvis < 0 || c.Spine < 0 || c.Spine1 < 0 || c.Spine2 < 0)
                return c;

            if (c.Pelvis >= count || c.Spine >= count || c.Spine1 >= count || c.Spine2 >= count)
                return c;

            // A chain, or it is not one this can solve: each bone must hang off the
            // one before it, and the pelvis must be the root.
            if (skeleton.GetParentBoneIndex(c.Pelvis) >= 0
                || skeleton.GetParentBoneIndex(c.Spine) != c.Pelvis
                || skeleton.GetParentBoneIndex(c.Spine1) != c.Spine
                || skeleton.GetParentBoneIndex(c.Spine2) != c.Spine1)
            {
                return c;
            }

            MatrixFrame liveSpine = skeleton.GetBoneEntitialFrameWithIndex(c.Spine);
            MatrixFrame liveSpine1 = skeleton.GetBoneEntitialFrameWithIndex(c.Spine1);
            MatrixFrame liveSpine2 = skeleton.GetBoneEntitialFrameWithIndex(c.Spine2);

            c.LowerLen = (liveSpine1.origin - liveSpine.origin).Length;
            c.UpperLen = (liveSpine2.origin - liveSpine1.origin).Length;

            // The axes come from the REST pose - both the direction and the frame
            // it is projected onto - exactly as the arm axes do. A bone's offset to
            // its child is fixed in the parent's own frame, so the rest direction
            // is that direction in every pose; mixing a live direction with a rest
            // frame would skew the axis by however far the animation has moved the
            // bone. Orthonormalised first: bone frames carry scale and a scaled
            // basis would skew the direction rather than just lengthen it.
            MatrixFrame restSpine = skeleton.GetBoneEntitialRestFrame(c.Spine, false);
            MatrixFrame restSpine1 = skeleton.GetBoneEntitialRestFrame(c.Spine1, false);
            MatrixFrame restSpine2 = skeleton.GetBoneEntitialRestFrame(c.Spine2, false);

            if (c.LowerLen > 1e-5f && c.UpperLen > 1e-5f)
            {
                Mat3 rs = restSpine.rotation;
                rs.Orthonormalize();
                Mat3 rs1 = restSpine1.rotation;
                rs1.Orthonormalize();

                c.SpineAxis = rs.TransformToLocal(
                    (restSpine1.origin - restSpine.origin).NormalizedCopy());
                c.Spine1Axis = rs1.TransformToLocal(
                    (restSpine2.origin - restSpine1.origin).NormalizedCopy());
            }

            // A human pelvis-to-chest is roughly 0.25 to 0.55 m in two segments.
            // Outside a generous window the mapping is wrong, and solving with it
            // would put the chest somewhere arbitrary rather than fail visibly.
            if (c.LowerLen < 0.03f || c.LowerLen > 0.6f
                || c.UpperLen < 0.03f || c.UpperLen > 0.6f)
                return c;

            c.Valid = true;
            return c;
        }

        private static VrLegChain BuildLeg(MBAgentVisuals visuals, Skeleton skeleton, bool left)
        {
            VrLegChain c = default(VrLegChain);
            c.IsLeft = left;

            c.Thigh = visuals.GetRealBoneIndex(left ? HumanBone.ThighL : HumanBone.ThighR);
            c.Calf = visuals.GetRealBoneIndex(left ? HumanBone.CalfL : HumanBone.CalfR);
            c.Foot = visuals.GetRealBoneIndex(left ? HumanBone.FootL : HumanBone.FootR);
            c.Toe = visuals.GetRealBoneIndex(left ? HumanBone.ToeL : HumanBone.ToeR);

            sbyte count = skeleton.GetBoneCount();

            if (c.Thigh < 0 || c.Calf < 0 || c.Foot < 0)
                return c;

            if (c.Thigh >= count || c.Calf >= count || c.Foot >= count)
                return c;

            if (skeleton.GetParentBoneIndex(c.Calf) != c.Thigh
                || skeleton.GetParentBoneIndex(c.Foot) != c.Calf)
                return c;

            MatrixFrame liveThigh = skeleton.GetBoneEntitialFrameWithIndex(c.Thigh);
            MatrixFrame liveCalf = skeleton.GetBoneEntitialFrameWithIndex(c.Calf);
            MatrixFrame liveFoot = skeleton.GetBoneEntitialFrameWithIndex(c.Foot);

            c.ThighLen = (liveCalf.origin - liveThigh.origin).Length;
            c.CalfLen = (liveFoot.origin - liveCalf.origin).Length;

            // Rest pose for both the direction and the frame, for the reason the
            // spine's axes give.
            MatrixFrame restThigh = skeleton.GetBoneEntitialRestFrame(c.Thigh, false);
            MatrixFrame restCalf = skeleton.GetBoneEntitialRestFrame(c.Calf, false);
            MatrixFrame restFoot = skeleton.GetBoneEntitialRestFrame(c.Foot, false);

            if (c.ThighLen > 1e-5f && c.CalfLen > 1e-5f)
            {
                Mat3 rt = restThigh.rotation;
                rt.Orthonormalize();
                Mat3 rc = restCalf.rotation;
                rc.Orthonormalize();

                c.ThighAxis = rt.TransformToLocal(
                    (restCalf.origin - restThigh.origin).NormalizedCopy());
                c.CalfAxis = rc.TransformToLocal(
                    (restFoot.origin - restCalf.origin).NormalizedCopy());
            }

            if (c.Toe >= count)
                c.Toe = -1;

            // A human thigh and shin are each roughly 0.30 to 0.55 m.
            if (c.ThighLen < 0.15f || c.ThighLen > 0.9f
                || c.CalfLen < 0.15f || c.CalfLen > 0.9f)
                return c;

            c.Valid = true;
            return c;
        }

        /// <summary>Prints the chains once, the way the arm chains are printed, so
        /// a wrong role mapping is a number in the log rather than a leg in the
        /// wrong place.</summary>
        private static void Describe(Skeleton skeleton)
        {
            if (_logged)
                return;

            _logged = true;

            var sb = new StringBuilder();

            if (!_spine.Valid)
            {
                VrLog.Warn("Body chain: the spine is NOT RESOLVED - the monster does not "
                           + "declare pelvis/spine/spine1/spine2 in a single chain, so the "
                           + "torso IK cannot run on it. The legs are unaffected.");
            }
            else
            {
                sb.Length = 0;
                sb.Append("Body chain, spine: ");
                Append(sb, skeleton, "pelvis", _spine.Pelvis);
                Append(sb, skeleton, "spine", _spine.Spine);
                Append(sb, skeleton, "spine1", _spine.Spine1);
                Append(sb, skeleton, "spine2", _spine.Spine2);
                Append(sb, skeleton, "neck", _spine.Neck);
                Append(sb, skeleton, "head", _spine.Head);
                VrLog.Info(sb.ToString());

                VrLog.Info(string.Format(
                    "Body chain, spine: lengths {0:F3} m (spine->spine1) and {1:F3} m "
                    + "(spine1->spine2). A human torso is roughly 0.10 to 0.25 m per "
                    + "segment; anything else means the mapping is wrong.",
                    _spine.LowerLen, _spine.UpperLen));
            }

            Leg(skeleton, _left, "Left");
            Leg(skeleton, _right, "Right");
        }

        private static void Leg(Skeleton skeleton, VrLegChain c, string side)
        {
            if (!c.Valid)
            {
                VrLog.Warn("Body chain, " + side + ": NOT RESOLVED - the monster does not "
                           + "declare thigh/calf/foot in a chain, so that leg is left to "
                           + "the game's animation.");
                return;
            }

            var sb = new StringBuilder();
            sb.Append("Body chain, ").Append(side).Append(": ");
            Append(sb, skeleton, "thigh", c.Thigh);
            Append(sb, skeleton, "calf", c.Calf);
            Append(sb, skeleton, "foot", c.Foot);
            Append(sb, skeleton, "toe", c.Toe);
            VrLog.Info(sb.ToString());

            VrLog.Info(string.Format(
                "Body chain, {0}: thigh {1:F3} m, shin {2:F3} m, reach {3:F3} m. A human "
                + "pair is roughly 0.30 to 0.55 m each; anything outside 0.15 to 0.90 m "
                + "means the role mapping landed on the wrong bone.",
                side, c.ThighLen, c.CalfLen, c.ThighLen + c.CalfLen));
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
            _spine = default(VrSpineChain);
            _left = default(VrLegChain);
            _right = default(VrLegChain);
        }
    }
}
