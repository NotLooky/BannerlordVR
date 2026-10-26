using System;

namespace BannerlordVR.VR.Physics
{
    // Mission-time blood loss, sampled at most twice a second. Fractional damage
    // carries forward; there is no minimum one-point hit per render frame.
    public sealed class BleedingWound
    {
        private float _started, _last, _next, _expires, _fraction, _rate, _baseRate, _depth;
        private int _remaining;
        public bool Active { get; private set; }
        public float Rate => _rate;
        public void Clear() { Active = false; _fraction = 0; _remaining = 0; _depth = 0; }

        public bool Add(float now, float rate, float duration)
        {
            if (!InteractionMath.IsFinite(now) || !InteractionMath.IsFinite(rate) ||
                !InteractionMath.IsFinite(duration) || rate <= 0 || duration <= 0) return false;
            rate = Math.Min(12, rate); duration = Math.Min(8, duration);
            if (Active && now < _last) Clear();
            if (!Active || now > _expires)
            {
                Clear(); Active = true; _started = _last = now; _next = now + .5f;
                _expires = now + duration; _baseRate = _rate = rate;
                _remaining = (int)Math.Ceiling(rate * duration);
            }
            else
            {
                // Several injuries can worsen the same wound, but cannot make
                // it immortal or manufacture an immediate damage tick.
                _baseRate = Math.Min(12, _baseRate + rate * .5f);
                _rate = Math.Min(12, _rate + rate * .5f);
                _expires = Math.Min(_started + 12, Math.Max(_expires, now + duration));
                _remaining = Math.Min(96, _remaining + (int)Math.Ceiling(rate * duration));
            }
            return true;
        }

        public void Deepen(float depthMetres)
        {
            if (!Active || !InteractionMath.IsFinite(depthMetres)) return;
            float depth = Math.Max(0, Math.Min(.2f, depthMetres));
            if (depth <= _depth) return;
            _rate = Math.Min(12, _rate + _baseRate * ((depth - _depth) / .2f));
            _depth = depth;
        }

        public bool Due(float now) => Active && (!InteractionMath.IsFinite(now) || now < _last || now >= _next);

        public int TakeDose(float now)
        {
            if (!Active) return 0;
            if (!InteractionMath.IsFinite(now) || now < _last || now > _expires + .5f)
            { Clear(); return 0; }
            if (now < _next) return 0;
            // No catch-up burst after a pause, loading hitch or time jump.
            float elapsed = Math.Min(.5f, Math.Max(0, Math.Min(now, _expires) - _last));
            _fraction += _rate * elapsed;
            int dose = Math.Min(_remaining, Math.Min(6, (int)_fraction));
            _fraction -= dose; _remaining -= dose;
            _last = now; _next = now + .5f;
            if (now >= _expires || _remaining <= 0) Active = false;
            return dose;
        }
    }
}
