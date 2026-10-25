using System;
using System.Globalization;
using System.Threading;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Read-only comparison of engine callbacks. The fixed callback may be marked
    // multithread-callable by Bannerlord; it only updates atomic counters here.
    internal sealed class PhysicsCadenceProbe : MissionLogic, IPhysicsStepSink
    {
        private int _fixedCount, _fixedThread, _fixedThreadChanges, _invalidFixed;
        private int _fixedMinUs = int.MaxValue, _fixedMaxUs;
        private int _baselineFixed, _frames;
        private float _began = -1, _frameMin = float.MaxValue, _frameMax;
        private bool _finished;
        private GameEntity _probeEntity;
        private BvrPhysicsStepClock _component;
        private int _physicsSteps, _physicsThread, _physicsThreadChanges;
        private int _physicsMinUs = int.MaxValue, _physicsMaxUs;
        public void RecordPhysicsStep(float dt)
        {
            int thread = Thread.CurrentThread.ManagedThreadId;
            int previous = Interlocked.Exchange(ref _physicsThread, thread);
            if (previous != 0 && previous != thread) Interlocked.Increment(ref _physicsThreadChanges);
            if (float.IsNaN(dt) || float.IsInfinity(dt) || dt <= 0 || dt > 1) return;
            int micros = (int)Math.Round(dt * 1000000);
            Min(ref _physicsMinUs, micros); Max(ref _physicsMaxUs, micros);
            Interlocked.Increment(ref _physicsSteps);
        }
        public override void OnFixedMissionTick(float fixedDt)
        {
            int thread = Thread.CurrentThread.ManagedThreadId;
            int previous = Interlocked.Exchange(ref _fixedThread, thread);
            if (previous != 0 && previous != thread) Interlocked.Increment(ref _fixedThreadChanges);
            if (float.IsNaN(fixedDt) || float.IsInfinity(fixedDt) || fixedDt <= 0 || fixedDt > 1)
            { Interlocked.Increment(ref _invalidFixed); return; }
            int micros = (int)Math.Round(fixedDt * 1000000);
            Min(ref _fixedMinUs, micros); Max(ref _fixedMaxUs, micros);
            Interlocked.Increment(ref _fixedCount);
        }
        public override void OnMissionTick(float dt)
        {
            try { Tick(dt); }
            catch (Exception ex)
            {
                _finished = true;
                PhysicsLog.Error("CADENCE_PROBE stopped after diagnostic error.", ex);
                try { Shutdown(); } catch (Exception cleanup) { PhysicsLog.Error("Cadence diagnostic cleanup failed.", cleanup); }
            }
        }
        private void Tick(float dt)
        {
            if (_finished || Mission.Mode != MissionMode.Battle || Mission.MainAgent == null) return;
            if (_began < 0)
            {
                if (Game.Current?.GameType?.GetType().FullName != "TaleWorlds.MountAndBlade.CustomBattle.CustomGame")
                { _finished = true; PhysicsLog.Info("CADENCE_PROBE refused=not_custom_battle"); return; }
                _probeEntity = GameEntity.CreateEmpty(Mission.Scene, false, false, false);
                _probeEntity.CreateAndAddScriptComponent(nameof(BvrPhysicsStepClock), true);
                _component = _probeEntity.GetFirstScriptOfType<BvrPhysicsStepClock>();
                _component?.Attach(this);
                Mission.Scene.EnableFixedTick();
                Mission.Scene.SetFixedTickCallbackActive(true);
                _began = Mission.CurrentTime; _baselineFixed = Volatile.Read(ref _fixedCount);
                PhysicsLog.Info("CADENCE_PROBE begin; diagnostic_entity=True; script_attached=" + (_component != null) +
                    "; fixed_scene_callback_enabled=True; ragdoll_cycle=" + EnemyPhysicsSettings.CadenceRagdoll + "; no_added_force_or_damage=True");
            }
            _frames++;
            if (!float.IsNaN(dt) && !float.IsInfinity(dt) && dt > 0)
            { _frameMin = Math.Min(_frameMin, dt); _frameMax = Math.Max(_frameMax, dt); }
            float elapsed = Mission.CurrentTime - _began;
            if (elapsed < (EnemyPhysicsSettings.CadenceRagdoll ? 6 : 2)) return;
            _finished = true;
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "CADENCE_PROBE result=OBSERVED elapsed={0:F4} mission_callbacks={1} fixed_callbacks={2} mission_thread={3} fixed_thread={4} fixed_thread_changes={5} frame_dt_min={6:F6} frame_dt_max={7:F6} fixed_dt_min_us={8} fixed_dt_max_us={9} invalid_fixed={10}",
                elapsed, _frames, Volatile.Read(ref _fixedCount) - _baselineFixed,
                Thread.CurrentThread.ManagedThreadId, Volatile.Read(ref _fixedThread),
                Volatile.Read(ref _fixedThreadChanges), _frameMin, _frameMax,
                Volatile.Read(ref _fixedMinUs), Volatile.Read(ref _fixedMaxUs), Volatile.Read(ref _invalidFixed)));
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "CADENCE_PROBE physics_steps={0} physics_thread={1} thread_changes={2} step_min_us={3} step_max_us={4}",
                Volatile.Read(ref _physicsSteps), Volatile.Read(ref _physicsThread),
                Volatile.Read(ref _physicsThreadChanges), Volatile.Read(ref _physicsMinUs), Volatile.Read(ref _physicsMaxUs)));
            _component?.Detach();
        }
        private static void Min(ref int location, int value)
        {
            int before = Volatile.Read(ref location);
            while (value < before)
            {
                int result = Interlocked.CompareExchange(ref location, value, before);
                if (result == before) return;
                before = result;
            }
        }
        private static void Max(ref int location, int value)
        {
            int before = Volatile.Read(ref location);
            while (value > before)
            {
                int result = Interlocked.CompareExchange(ref location, value, before);
                if (result == before) return;
                before = result;
            }
        }
        protected override void OnEndMission() => Shutdown();
        public override void OnRemoveBehavior() => Shutdown();
        private void Shutdown()
        {
            _component?.Detach(); _component = null;
            if (_probeEntity != null && !Mission.IsFinalized) _probeEntity.Remove(0);
            _probeEntity = null;
        }
    }
}
