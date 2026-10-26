namespace BannerlordVR.VR.Physics
{
    // Bannerlord forwards ApplyForceOnRagdoll to PhysX as an accumulated
    // acceleration that the next simulation step consumes. Mission ticks run
    // about twice as often as PhysX steps, so applying a continuous force on
    // every mission tick stacks it. Allow one application per observed step.
    // Callers record the counter after applying: a step racing the call then
    // costs one skipped application, never a doubled one.
    public sealed class PhysicsStepGate
    {
        private long _appliedAt = -1;
        public bool CanApply(long steps) => steps >= 0 && (_appliedAt < 0 || steps > _appliedAt);
        public void Applied(long stepsAfterApply) => _appliedAt = stepsAfterApply;
        public void Reset() => _appliedAt = -1;
    }
}
