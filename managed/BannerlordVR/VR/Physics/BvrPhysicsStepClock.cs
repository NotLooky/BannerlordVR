using System.Threading;
using TaleWorlds.Engine;

namespace BannerlordVR.VR.Physics
{
    internal interface IPhysicsStepSink { void RecordPhysicsStep(float fixedDt); }

    // The scene's serial script fixed callback is invoked from the engine's
    // pre-simulation hook, once per PhysX substep and possibly off the mission
    // thread. Never query agents or apply forces here: only count steps.
    public sealed class BvrPhysicsStepClock : ScriptComponentBehavior
    {
        private IPhysicsStepSink _sink;
        internal void Attach(IPhysicsStepSink sink) => Volatile.Write(ref _sink, sink);
        internal void Detach()
        {
            Volatile.Write(ref _sink, null);
            SetScriptComponentToTick(TickRequirement.None);
        }
        public override TickRequirement GetTickRequirement() => TickRequirement.FixedTick;
        protected override void OnInit() => SetScriptComponentToTick(TickRequirement.FixedTick);
        protected override void OnFixedTick(float fixedDt) => Volatile.Read(ref _sink)?.RecordPhysicsStep(fixedDt);
        protected override void OnRemoved(int removeReason) => Volatile.Write(ref _sink, null);
    }
}
