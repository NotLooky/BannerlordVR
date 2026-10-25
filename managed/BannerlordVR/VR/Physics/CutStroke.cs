using System;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    // One continuous draw along a blade, relative to the contacted bone.
    // Signed displacement cancels tremor; tracked travel caps locomotion/AI motion.
    public sealed class CutStroke
    {
        private int _target = -1;
        private Vec3 _previous, _axis;
        private float _started, _last, _draw, _net;
        private bool _fired;
        public void Reset() { _target = -1; _draw = _net = 0; _fired = false; }

        public bool Sample(int target, Vec3 relativeBladeBase, Vec3 bladeAxis,
                           float trackedTravel, float now)
        {
            if (target < 0 || !InteractionMath.IsFinite(relativeBladeBase) ||
                !InteractionMath.IsFinite(bladeAxis) || !InteractionMath.IsFinite(trackedTravel) ||
                !InteractionMath.IsFinite(now) || trackedTravel < 0 || trackedTravel > .4f ||
                bladeAxis.LengthSquared < .5f || bladeAxis.LengthSquared > 1.5f)
            { Reset(); return false; }
            bladeAxis.Normalize();
            bool restart = target != _target || now <= _last || now - _last > .1f ||
                InteractionMath.Dot(bladeAxis, _axis) < .866f;
            if (restart)
            {
                _target = target; _axis = bladeAxis; _previous = relativeBladeBase;
                _started = _last = now; _draw = _net = 0; _fired = false;
                return false;
            }
            Vec3 delta = relativeBladeBase - _previous;
            _previous = relativeBladeBase; _last = now;
            if (delta.Length > .4f) { Reset(); return false; }
            if (_fired) return false;
            if (now - _started > 1.5f) { Reset(); return false; }
            float signed = InteractionMath.Dot(delta, _axis);
            _net += signed;
            _draw += Math.Max(-trackedTravel, Math.Min(trackedTravel, signed));
            if (Math.Abs(_draw) < .12f || Math.Abs(_net) < .12f || _draw * _net <= 0 || now - _started < .08f) return false;
            _fired = true;
            return true;
        }
    }
}
