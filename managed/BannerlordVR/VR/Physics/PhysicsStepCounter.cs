using System.Threading;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // One invisible entity without a physics body per mission, created only
    // when a feature needs continuous ragdoll forces.
    internal sealed class PhysicsStepCounter : IPhysicsStepSink
    {
        private long _steps;
        private GameEntity _entity;
        private BvrPhysicsStepClock _clock;
        private float _attachedAt = -1;
        public long Steps => Interlocked.Read(ref _steps);
        public bool Attached => _clock != null;
        // Before the first observed step the cadence is unknown; refuse forces.
        public bool Running => Attached && Steps > 0;
        public bool Stalled(float now) => Attached && Steps == 0 && now - _attachedAt > .5f;
        public void RecordPhysicsStep(float fixedDt)
        {
            if (fixedDt > 0 && fixedDt <= 1) Interlocked.Increment(ref _steps);
        }
        public bool Ensure(Mission mission)
        {
            if (_clock != null) return true;
            if (_attachedAt >= 0 || mission?.Scene == null) return false;
            _attachedAt = mission.CurrentTime;
            _entity = GameEntity.CreateEmpty(mission.Scene, false, false, false);
            _entity.CreateAndAddScriptComponent(nameof(BvrPhysicsStepClock), true);
            _clock = _entity.GetFirstScriptOfType<BvrPhysicsStepClock>();
            if (_clock == null) { PhysicsLog.Info("PHYSICS_CLOCK refused=script_not_attached"); return false; }
            _clock.Attach(this);
            mission.Scene.EnableFixedTick();
            mission.Scene.SetFixedTickCallbackActive(true);
            PhysicsLog.Info("PHYSICS_CLOCK attached; forces limited to one application per PhysX step.");
            return true;
        }
        public void Shutdown(Mission mission)
        {
            _clock?.Detach(); _clock = null;
            if (_entity != null && mission != null && !mission.IsFinalized) _entity.Remove(0);
            _entity = null; _attachedAt = -1;
            Interlocked.Exchange(ref _steps, 0);
        }
    }
}
