using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    public static class CompliantPose
    {
        // CCD arm solve. Rotate joints, preserve bone lengths and the engine's root placement.
        // No root translation: the navigation capsule stays under engine ownership.
        public static bool Arm(Skeleton skeleton, sbyte hand, MatrixFrame target, sbyte upperArm = -1)
        {
            if (hand < 0 || hand >= skeleton.GetBoneCount() || !InteractionMath.IsFinite(target.origin)) return false;
            sbyte shoulder = upperArm;
            sbyte ancestor = skeleton.GetParentBoneIndex(hand);
            bool found = false;
            for (int n = 0; n < 16 && ancestor >= 0; n++)
            {
                string name = (skeleton.GetBoneName(ancestor) ?? "").ToLowerInvariant();
                if (ancestor == shoulder || (shoulder < 0 && (name.Contains("upperarm") || name.Contains("upper_arm"))))
                { shoulder = ancestor; found = true; break; }
                ancestor = skeleton.GetParentBoneIndex(ancestor);
            }
            if (!found || shoulder < 0) return false;
            Vec3 shoulderPos = skeleton.GetBoneEntitialFrameWithIndex(shoulder).origin;
            Vec3 handPos = skeleton.GetBoneEntitialFrameWithIndex(hand).origin;
            // Native humans contain foretwist and upperarm_twist nodes. Select
            // the actual bend from the chain's geometry rather than skipping
            // every node named twist (which skipped the native elbow entirely).
            sbyte elbow = -1;
            float bestScore = float.NegativeInfinity;
            Vec3 arm = handPos - shoulderPos;
            float armLength2 = Math.Max(1e-6f, arm.LengthSquared);
            ancestor = skeleton.GetParentBoneIndex(hand);
            for (int n = 0; n < 16 && ancestor >= 0 && ancestor != shoulder; n++)
            {
                Vec3 position = skeleton.GetBoneEntitialFrameWithIndex(ancestor).origin;
                float projection = InteractionMath.Dot(position - shoulderPos, arm) / armLength2;
                float bend = (position - (shoulderPos + arm * projection)).LengthSquared;
                float score = bend - Math.Abs(projection - .5f) * .0001f;
                if ((position - shoulderPos).Length > .05f && (position - handPos).Length > .05f && score > bestScore)
                { bestScore = score; elbow = ancestor; }
                ancestor = skeleton.GetParentBoneIndex(ancestor);
            }
            if (elbow < 0) return false;
            Vec3 elbowPos = skeleton.GetBoneEntitialFrameWithIndex(elbow).origin;
            float reach = (shoulderPos - elbowPos).Length + (elbowPos - handPos).Length;
            target.origin = shoulderPos + InteractionMath.ClampMagnitude(target.origin - shoulderPos, reach * .98f);
            for (int pass = 0; pass < 4; pass++)
            {
                Aim(skeleton, elbow, hand, target.origin, .30f);
                Aim(skeleton, shoulder, hand, target.origin, .30f);
            }
            // Wrist orientation follows the grip; its position comes from the solved chain.
            MatrixFrame wrist = skeleton.GetBoneEntitialFrameWithIndex(hand);
            wrist.rotation = target.rotation;
            WriteEntity(skeleton, hand, wrist);
            return true;
        }

        public static void Aim(Skeleton skeleton, sbyte joint, sbyte endpoint, Vec3 target, float maxAngle)
        {
            MatrixFrame frame = skeleton.GetBoneEntitialFrameWithIndex(joint);
            Vec3 from = skeleton.GetBoneEntitialFrameWithIndex(endpoint).origin - frame.origin;
            Vec3 to = target - frame.origin;
            frame.rotation.s = InteractionMath.RotateTowards(from, to, frame.rotation.s, maxAngle);
            frame.rotation.f = InteractionMath.RotateTowards(from, to, frame.rotation.f, maxAngle);
            frame.rotation.u = InteractionMath.RotateTowards(from, to, frame.rotation.u, maxAngle);
            WriteEntity(skeleton, joint, frame);
        }

        public static void WriteEntity(Skeleton skeleton, sbyte bone, MatrixFrame entityFrame)
        {
            sbyte parent = skeleton.GetParentBoneIndex(bone);
            if (parent < 0) return; // Never drag or rotate the root.
            MatrixFrame local = skeleton.GetBoneEntitialFrameWithIndex(parent).TransformToLocalNonOrthogonal(entityFrame);
            skeleton.SetBoneLocalFrame(bone, local);
            // Rebuilding from locals otherwise loses the placed root (including rider lift).
            for (sbyte i = 0; i < skeleton.GetBoneCount(); i++)
                if (skeleton.GetParentBoneIndex(i) < 0)
                    skeleton.SetBoneLocalFrame(i, skeleton.GetBoneEntitialFrameWithIndex(i));
            skeleton.UpdateEntitialFramesFromLocalFrames();
        }
    }
}
