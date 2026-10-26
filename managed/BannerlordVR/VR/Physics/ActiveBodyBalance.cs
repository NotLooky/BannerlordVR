using System;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    public enum BodyBalancePhase { Inactive, Supporting, Falling }
    public enum BodyBalanceFailure { None, InvalidInput, PoseJump, NoGround, OutsideSupport, Collapsed, Tilted }

    // A yielding support motor, not a root teleport or an animation override.
    // Outputs are continuous forces; Bannerlord divides by mass before passing
    // them to PhysX in acceleration mode. The solver integrates time itself.
    public sealed class ActiveBodyBalance
    {
        // Feet resting on the ground carry the legs (two thigh+shank+foot
        // segments, 16.1% each in Winter's anthropometric tables). Forces on
        // pelvis and chest compensate only the upper body; compensating the
        // whole ragdoll floated it: the in-game probe settled 13.7 cm high,
        // the equilibrium predicted for about 69% of the weight.
        public const float SupportedMassFraction = .678f;
        private Vec3 _pelvisOffset, _chestOffset, _lastPelvis, _lastChest;
        private float _mass, _age, _unsupported, _fade, _referenceFeetZ;
        private bool _sampled;
        public BodyBalancePhase Phase { get; private set; }
        public BodyBalanceFailure Failure { get; private set; }
        public bool SetPhysicalMass(float mass)
        {
            if (!InteractionMath.IsFinite(mass) || mass < 30 || mass > 200) return false;
            _mass = mass; return true;
        }
        public Vec3 PelvisForce { get; private set; }
        public Vec3 ChestForce { get; private set; }
        public void Clear()
        {
            Phase = BodyBalancePhase.Inactive;
            Failure = BodyBalanceFailure.None; _sampled = false;
            PelvisForce = ChestForce = Vec3.Zero;
            _age = _unsupported = _fade = 0;
        }
        public bool Begin(Vec3 pelvis, Vec3 chest, Vec3 leftFoot, Vec3 rightFoot, float mass)
        {
            Clear();
            if (!Finite(pelvis, chest, leftFoot, rightFoot) || !InteractionMath.IsFinite(mass) ||
                mass < 30 || mass > 200) return false;
            Vec3 feet = (leftFoot + rightFoot) * .5f;
            float height = pelvis.z - feet.z, torso = chest.z - pelvis.z;
            float span = (leftFoot - rightFoot).Length;
            if (height < .5f || height > 1.5f || torso < .15f || torso > .8f || span < .08f || span > 1.2f)
                return false;
            _mass = mass;
            _referenceFeetZ = feet.z;
            _pelvisOffset = pelvis - feet; _chestOffset = chest - feet;
            _lastPelvis = pelvis; _lastChest = chest;
            Phase = BodyBalancePhase.Supporting;
            return true;
        }
        public void Tick(Vec3 pelvis, Vec3 chest, Vec3 leftFoot, Vec3 rightFoot, bool grounded, float dt)
            => Tick(pelvis, chest, leftFoot, rightFoot, grounded, grounded, dt);
        public void Tick(Vec3 pelvis, Vec3 chest, Vec3 leftFoot, Vec3 rightFoot,
            bool leftGrounded, bool rightGrounded, float dt, float groundHeightChange = 0)
        {
            PelvisForce = ChestForce = Vec3.Zero;
            if (Phase == BodyBalancePhase.Inactive) return;
            if (!Finite(pelvis, chest, leftFoot, rightFoot) || !InteractionMath.IsFinite(dt) || dt < 0 || dt > .1f ||
                !InteractionMath.IsFinite(groundHeightChange) || Math.Abs(groundHeightChange) > 1.5f)
            { Stop(BodyBalanceFailure.InvalidInput); return; }
            if (dt == 0) return;
            // Activation can span several engine ticks. Do not divide movement
            // since pre-activation capture by only the current tick's delta.
            Vec3 pelvisVelocity = _sampled ? (pelvis - _lastPelvis) * (1 / dt) : Vec3.Zero;
            Vec3 chestVelocity = _sampled ? (chest - _lastChest) * (1 / dt) : Vec3.Zero;
            _lastPelvis = pelvis; _lastChest = chest;
            _sampled = true;
            if (pelvisVelocity.Length > 12 || chestVelocity.Length > 12) { Stop(BodyBalanceFailure.PoseJump); return; }
            bool grounded = leftGrounded || rightGrounded;
            _age += dt; _unsupported = grounded ? 0 : _unsupported + dt;
            Vec3 feet = (leftFoot + rightFoot) * .5f;
            Vec3 centre = pelvis * .65f + chest * .35f;
            Vec3 a = leftFoot, b = rightFoot;
            if (!leftGrounded && rightGrounded) a = b;
            if (!rightGrounded && leftGrounded) b = a;
            a.z = b.z = centre.z;
            Vec3 support = InteractionMath.ClosestPointOnSegment(a, b, centre);
            Vec3 torso = chest - pelvis;
            if (Phase == BodyBalancePhase.Supporting)
            {
                if (_unsupported > .12f) Failure = BodyBalanceFailure.NoGround;
                else if ((centre - support).Length > .28f) Failure = BodyBalanceFailure.OutsideSupport;
                else if (pelvis.z - feet.z < _pelvisOffset.z * .55f) Failure = BodyBalanceFailure.Collapsed;
                else if (torso.z < torso.Length * .45f) Failure = BodyBalanceFailure.Tilted;
                if (Failure != BodyBalanceFailure.None) { Phase = BodyBalancePhase.Falling; _fade = 1; }
            }
            if (Phase == BodyBalancePhase.Falling)
            {
                _fade = Math.Max(0, _fade - dt / .12f);
                if (_fade <= 0) { Stop(Failure); return; }
            }
            float blend = Math.Min(1, _age / .15f) * (Phase == BodyBalancePhase.Falling ? _fade : 1);
            // Vertical targets follow measured terrain height, never a lifted
            // foot: otherwise lifting the body also raises its own target.
            Vec3 targetFeet = feet;
            targetFeet.z = _referenceFeetZ + groundHeightChange;
            // No support across a ledge: horizontal corrections require actual
            // ground and vertical assistance expires during the short loss grace.
            PelvisForce = Motor(targetFeet + _pelvisOffset - pelvis, pelvisVelocity, .6f, grounded) * blend;
            ChestForce = Motor(targetFeet + _chestOffset - chest, chestVelocity, .4f, grounded) * blend;
        }
        private Vec3 Motor(Vec3 error, Vec3 velocity, float share, bool grounded)
        {
            float mass = _mass * SupportedMassFraction * share;
            Vec3 acceleration = error * 22 - velocity * 5;
            Vec3 horizontal = new Vec3(acceleration.x, acceleration.y, 0);
            horizontal = grounded ? InteractionMath.ClampMagnitude(horizontal, 3) : Vec3.Zero;
            float up = Math.Max(0, Math.Min(16, 9.81f + acceleration.z));
            return InteractionMath.ClampMagnitude(new Vec3(horizontal.x, horizontal.y, up) * mass, 1800 * share);
        }
        private static bool Finite(Vec3 a, Vec3 b, Vec3 c, Vec3 d) =>
            InteractionMath.IsFinite(a) && InteractionMath.IsFinite(b) &&
            InteractionMath.IsFinite(c) && InteractionMath.IsFinite(d);
        private void Stop(BodyBalanceFailure reason) { Clear(); Failure = reason; }
    }
}
