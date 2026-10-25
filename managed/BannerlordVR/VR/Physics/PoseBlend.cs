using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    // Hands a physical body back to its animation over a short blend instead
    // of a cut: the ragdoll's last world pose is captured at the release and
    // mixed into the animated pose for Seconds, rotations slerped and
    // positions lerped per bone, then written back as local frames.
    public sealed class PoseBlend
    {
        public const float Seconds = .3f;
        private MatrixFrame[] _world = new MatrixFrame[0];
        private MatrixFrame[] _blended = new MatrixFrame[0];
        private bool[] _head = new bool[0];
        private MatrixFrame[] _anim = new MatrixFrame[0];
        private int _count;
        private float _started = -1;
        private bool _applied;
        public bool Running => _started >= 0;

        // While the ragdoll is still active, before the native release.
        public bool Capture(Skeleton skeleton, MatrixFrame entity, float now)
        {
            _started = -1;
            if (skeleton == null) return false;
            _count = skeleton.GetBoneCount();
            if (_count <= 0 || _count > 256) return false;
            if (_world.Length < _count) { _world = new MatrixFrame[_count]; _blended = new MatrixFrame[_count]; _head = new bool[_count]; _anim = new MatrixFrame[_count]; }
            for (sbyte b = 0; b < _count; b++)
            {
                sbyte parent = skeleton.GetParentBoneIndex(b);
                // Only the limbs blend. The spine, neck and head take the
                // animated pose outright: the helmet follows the animated
                // head, and a blended head or neck went through it.
                string name = skeleton.GetBoneName(b) ?? "";
                bool limb = name.Contains("clavicle") || name.Contains("upperarm") || name.Contains("forearm") || name.Contains("hand") ||
                    name.Contains("finger") || name.Contains("thumb") || name.Contains("thigh") || name.Contains("calf") ||
                    name.Contains("foot") || name.Contains("toe");
                _head[b] = !limb && !(parent >= 0 && parent < b && !_head[parent]);
            }
            for (sbyte b = 0; b < _count; b++)
            {
                _world[b] = entity.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(b));
                if (!InteractionMath.IsFinite(_world[b].origin)) return false;
            }
            _started = now; _applied = false;
            return true;
        }

        public void Stop() => _started = -1;

        // After the animation was ticked this frame. Returns false once done.
        public bool Apply(Skeleton skeleton, MatrixFrame entity, float now)
        {
            if (_started < 0 || skeleton == null || skeleton.GetBoneCount() != _count) { _started = -1; return false; }
            // The blend runs from the first animated frame, not from the
            // release request (the engine takes a few ticks to let go).
            if (!_applied) { _applied = true; _started = now; }
            float t = (now - _started) / Seconds;
            if (t >= 1 || t < 0) { _started = -1; return false; }
            // Weight of the physical pose, eased out.
            float w = 1 - t * t * (3 - 2 * t);
            for (sbyte b = 0; b < _count; b++)
            {
                MatrixFrame anim = skeleton.GetBoneEntitialFrameWithIndex(b);
                _anim[b] = anim;
                if (_head[b]) { _blended[b] = anim; continue; }
                MatrixFrame rag = entity.TransformToLocalNonOrthogonal(_world[b]);
                _blended[b] = PoseMath.Blend(anim, rag, w);
            }
            for (sbyte b = 0; b < _count; b++)
            {
                sbyte parent = skeleton.GetParentBoneIndex(b);
                MatrixFrame local = parent < 0 || parent >= _count ? _blended[b] : _blended[parent].TransformToLocalNonOrthogonal(_blended[b]);
                if (InteractionMath.IsFinite(local.origin)) skeleton.SetBoneLocalFrame(b, local);
            }
            skeleton.UpdateEntitialFramesFromLocalFrames();
            return true;
        }
    }
}
