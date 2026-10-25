using System;
using System.Globalization;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // One reusable adapter per leased body. Fixed-world ground rays intentionally
    // do not accept a moving horse or another character as a standing platform.
    internal sealed class LivingBodyBalance
    {
        private readonly ActiveBodyBalance _motor = new ActiveBodyBalance();
        private readonly PhysicsStepGate _gate = new PhysicsStepGate();
        private sbyte _pelvis, _chest, _left, _right;
        private float _nextGround, _initialGroundHeight, _groundHeight;
        private bool _leftGrounded, _rightGrounded;
        private bool _measured;
        public bool Captured { get; private set; }
        public int AppliedTicks { get; private set; }
        public int SkippedTicks { get; private set; }
        public float PeakForce { get; private set; }
        public BodyBalancePhase Phase => _motor.Phase;
        public void Clear()
        {
            _motor.Clear(); _gate.Reset();
            _leftGrounded = _rightGrounded = Captured = _measured = false; _nextGround = 0;
            AppliedTicks = SkippedTicks = 0; PeakForce = 0;
        }
        public bool Capture(Agent agent, Skeleton skeleton, Mission mission)
        {
            Clear();
            Monster monster = agent.Monster;
            _pelvis = monster.PelvisBoneIndex; _chest = monster.SpineUpperBoneIndex;
            _left = monster.LeftFootIkEndEffectorBoneIndex; _right = monster.RightFootIkEndEffectorBoneIndex;
            int count = skeleton.GetBoneCount();
            if (!Valid(_pelvis, count) || !Valid(_chest, count) || !Valid(_left, count) || !Valid(_right, count) ||
                _pelvis == _chest || _left == _right)
            {
                PhysicsLog.Info("BALANCE reference refused=bone_mapping; pelvis=" + _pelvis + "; chest=" + _chest +
                    "; left=" + _left + "; right=" + _right + "; count=" + count);
                return false;
            }
            MatrixFrame frame = agent.AgentVisuals.GetEntity().GetGlobalFrame();
            Vec3 left = Point(skeleton, frame, _left), right = Point(skeleton, frame, _right);
            Vec3 pelvis = Point(skeleton, frame, _pelvis), chest = Point(skeleton, frame, _chest);
            bool leftGround = Ground(mission.Scene, left, out float leftGap);
            bool rightGround = Ground(mission.Scene, right, out float rightGap);
            float mass = agent.GetTotalMass();
            _leftGrounded = leftGround; _rightGrounded = rightGround;
            _initialGroundHeight = _groundHeight = FloorHeight(left, right, leftGap, rightGap, leftGround, rightGround);
            _nextGround = mission.CurrentTime + .1f;
            bool anatomy = _motor.Begin(pelvis, chest, left, right, mass);
            Captured = (leftGround || rightGround) && anatomy;
            if (!Captured) _motor.Clear();
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "BALANCE reference mass={0:F2} pelvis_height={1:F3} torso_height={2:F3} foot_span={3:F3} left_ground={4} right_ground={5} left_gap={6:F3} right_gap={7:F3} anatomy={8}",
                mass, pelvis.z - (left.z + right.z) * .5f, chest.z - pelvis.z, (left - right).Length,
                leftGround, rightGround, leftGap, rightGap, anatomy));
            return Captured;
        }
        public void Tick(Agent agent, Skeleton skeleton, Mission mission, float dt, PhysicsStepCounter clock)
        {
            if (_motor.Phase == BodyBalancePhase.Inactive) return;
            if (clock == null || clock.Stalled(mission.CurrentTime))
            { _motor.Clear(); PhysicsLog.Info("BALANCE stopped: PhysX step clock unavailable."); return; }
            if (!_measured)
            {
                _measured = true;
                if (!NativeRagdollBridge.ReadMass(agent, mission, skeleton, out float mass) || !_motor.SetPhysicalMass(mass))
                { _motor.Clear(); PhysicsLog.Info("BALANCE stopped: physical mass unavailable or outside supported range."); return; }
            }
            MatrixFrame frame = agent.AgentVisuals.GetEntity().GetGlobalFrame();
            Vec3 pelvis = Point(skeleton, frame, _pelvis), chest = Point(skeleton, frame, _chest);
            Vec3 left = Point(skeleton, frame, _left), right = Point(skeleton, frame, _right);
            if (mission.CurrentTime >= _nextGround)
            {
                _leftGrounded = Ground(mission.Scene, left, out float leftGap);
                _rightGrounded = Ground(mission.Scene, right, out float rightGap);
                if (_leftGrounded || _rightGrounded)
                    _groundHeight = FloorHeight(left, right, leftGap, rightGap, _leftGrounded, _rightGrounded);
                if (EnemyPhysicsSettings.RuntimeProbe)
                    PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "BALANCE sample pelvis_z={0:F3} left_gap={1:F3} right_gap={2:F3} phase={3}",
                        pelvis.z, leftGap, rightGap, _motor.Phase));
                _nextGround = mission.CurrentTime + .1f;
            }
            BodyBalancePhase before = _motor.Phase;
            _motor.Tick(pelvis, chest, left, right, _leftGrounded, _rightGrounded, dt, _groundHeight - _initialGroundHeight);
            if (_motor.Phase != before)
                PhysicsLog.Info("BALANCE transition phase=" + _motor.Phase + "; reason=" + _motor.Failure +
                    "; left_ground=" + _leftGrounded + "; right_ground=" + _rightGrounded);
            Vec3 pelvisForce = _motor.PelvisForce, chestForce = _motor.ChestForce;
            if (pelvisForce.LengthSquared == 0 && chestForce.LengthSquared == 0) return;
            // The previous force is still pending until PhysX steps again.
            if (!clock.Running || !_gate.CanApply(clock.Steps)) { SkippedTicks++; return; }
            if (pelvisForce.LengthSquared > 0) agent.ApplyForceOnRagdoll(_pelvis, in pelvisForce);
            if (chestForce.LengthSquared > 0) agent.ApplyForceOnRagdoll(_chest, in chestForce);
            _gate.Applied(clock.Steps);
            AppliedTicks++;
            PeakForce = Math.Max(PeakForce, pelvisForce.Length + chestForce.Length);
        }
        private static bool Valid(sbyte bone, int count) => bone >= 0 && bone < count;
        private static float FloorHeight(Vec3 left, Vec3 right, float leftGap, float rightGap, bool leftGround, bool rightGround)
        {
            if (leftGround && rightGround) return (left.z - leftGap + right.z - rightGap) * .5f;
            return leftGround ? left.z - leftGap : rightGround ? right.z - rightGap : 0;
        }
        private static Vec3 Point(Skeleton skeleton, MatrixFrame frame, sbyte bone) =>
            frame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(bone)).origin;
        private static bool Ground(Scene scene, Vec3 foot, out float gap)
        {
            gap = float.NaN;
            if (!InteractionMath.IsFinite(foot)) return false;
            bool hitGround = scene.RayCastForClosestEntityOrTerrainFixedPhysics(foot + Vec3.Up * .12f, foot - Vec3.Up * .28f,
                out float distance, out Vec3 hit, .015f) && InteractionMath.IsFinite(hit) &&
                InteractionMath.IsFinite(distance);
            if (hitGround) gap = foot.z - hit.z;
            return hitGround && Math.Abs(gap) <= .2f;
        }
    }
}
