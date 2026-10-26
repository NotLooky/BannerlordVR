using System;

namespace BannerlordVR.VR.Physics
{
    public enum ActiveRagdollPhase { Off, Standing, Destabilized, StandingUp, Recovered }
    public enum FallState { None, Falling, NearGround, Stabilizing, StabilizedOnGround }

    // Blade & Sorcery creature cycle around the pin drive: Creature fall state
    // (groundStabilizationMaxVelocity 1 m/s held for
    // groundStabilizationMinDuration 3 s), BrainModuleHitReaction.OnCycle
    // standing up once StabilizedOnGround, and Ragdoll.StandUpCoroutine: pins
    // restart at zero with gravity on, ramp over preStandUpDuration (3 s),
    // the get-up is committed at preStandUpRatio (0.7), then full pins with
    // gravity removed. A push during the rise cancels it (cancelGetUp).
    public sealed class ActiveRagdollCycle
    {
        public const float GroundStabilizationMaxVelocity = 1, GroundStabilizationMinDuration = 3;
        public const float PreStandUpDuration = 3, PreStandUpRatio = .7f;
        private float _groundStable, _rise;
        public ActiveRagdollPhase Phase { get; private set; }
        public FallState Fall { get; private set; }
        public bool GetUpCommitted { get; private set; }

        public void Begin(bool destabilized)
        {
            Phase = destabilized ? ActiveRagdollPhase.Destabilized : ActiveRagdollPhase.Standing;
            Fall = FallState.None; _groundStable = _rise = 0; GetUpCommitted = false;
        }
        public void Stop() { Phase = ActiveRagdollPhase.Off; Fall = FallState.None; }

        public void Destabilize()
        {
            if (Phase == ActiveRagdollPhase.Off || Phase == ActiveRagdollPhase.Recovered) return;
            Phase = ActiveRagdollPhase.Destabilized;
            _groundStable = _rise = 0; GetUpCommitted = false; Fall = FallState.Falling;
        }

        // nearGround: a ground sphere cast from the root within hips height.
        // noStandUp: Brain.noStandupModifiers (BrainModuleGrabbed adds one
        // while a hand holds the creature); a blade held in the body counts
        // as well, and calls off a get-up not yet committed.
        public void Tick(float dt, bool nearGround, float rootSpeed, bool noStandUp = false)
        {
            if (!InteractionMath.IsFinite(dt) || dt < 0 || dt > .5f || !InteractionMath.IsFinite(rootSpeed)) return;
            if (Phase == ActiveRagdollPhase.Destabilized)
            {
                if (!nearGround) { Fall = FallState.Falling; _groundStable = 0; return; }
                if (rootSpeed >= GroundStabilizationMaxVelocity) { Fall = FallState.NearGround; _groundStable = 0; return; }
                _groundStable += dt;
                Fall = _groundStable > GroundStabilizationMinDuration ? FallState.StabilizedOnGround : FallState.Stabilizing;
                if (Fall == FallState.StabilizedOnGround && !noStandUp) { Phase = ActiveRagdollPhase.StandingUp; _rise = 0; }
                return;
            }
            if (Phase == ActiveRagdollPhase.StandingUp && noStandUp && !GetUpCommitted)
            {
                Phase = ActiveRagdollPhase.Destabilized; _rise = 0; Fall = FallState.StabilizedOnGround;
                return;
            }
            if (Phase == ActiveRagdollPhase.StandingUp)
            {
                _rise += dt;
                if (!GetUpCommitted && _rise / PreStandUpDuration > PreStandUpRatio) GetUpCommitted = true;
                if (_rise >= PreStandUpDuration) Phase = ActiveRagdollPhase.Recovered;
            }
        }

        // The B&S standUpCurve is an editor asset; a smoothstep has the same
        // end points and zero slope at both ends.
        public float PinMultiplier
        {
            get
            {
                switch (Phase)
                {
                    case ActiveRagdollPhase.Standing:
                    case ActiveRagdollPhase.Recovered: return 1;
                    case ActiveRagdollPhase.StandingUp:
                        float t = Math.Max(0, Math.Min(1, _rise / PreStandUpDuration));
                        return t * t * (3 - 2 * t);
                    default: return 1;
                }
            }
        }
        public PinState DriveState => Phase == ActiveRagdollPhase.Destabilized ? PinState.Destabilized :
            Phase == ActiveRagdollPhase.Off ? PinState.Inert : PinState.Standing;
        // StandUpCoroutine removes the gravity modifier for the ramp.
        public bool CancelGravity => Phase == ActiveRagdollPhase.Standing || Phase == ActiveRagdollPhase.Recovered;
    }
}
