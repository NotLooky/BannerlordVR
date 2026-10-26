using System;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    // Blade & Sorcery ragdoll states that keep physics on (Ragdoll.State).
    public enum PinState { Inert, Destabilized, Standing }

    // Port of the Blade & Sorcery creature drive. There, every ragdoll part is
    // a rigidbody joined to a kinematic anchor that follows its animated bone:
    // a position drive (spring 1000, damper 50, max 1000) and a slerp rotation
    // drive (800, 50, 100). Standing pins position and rotation and disables
    // gravity on the parts; Destabilized drops position pins, restores gravity
    // and keeps rotation pins at 0.5x spring / 0.1x damper except on the root
    // (0.2x spring once on the ground); Inert has no pins at all.
    //
    // Bannerlord exposes neither joint drives nor torque on ragdoll bodies,
    // only a continuous force per body. Position drives map directly to a
    // force towards the target. A rotation drive is reproduced by an equal
    // and opposite force pair between a body and its parent body that restores
    // their animated relative offset: the joint angle error times the lever
    // arm, with torque k*theta becoming force (k / L^2) * offset error.
    public sealed class RagdollPinDrive
    {
        public const float SpringPosition = 1000, DamperPosition = 50, MaxPosition = 1000;
        public const float SpringRotation = 800, DamperRotation = 50, MaxRotation = 100;
        public const float DestabilizedSpringRotation = .5f, DestabilizedDamperRotation = .1f;
        public const float DestabilizedGroundSpringRotation = .2f;
        public const float Gravity = 9.81f;
        // Share of the rotation damper a released (limp) part keeps.
        public const float LimpJointDamping = .3f;
        // PhysX integrates Unity's drives implicitly. Here forces are explicit
        // and arrive one PhysX step late, so very light bodies would oscillate
        // under the raw spring. Cap each body's natural frequency instead.
        public const float MaxNaturalFrequency = 18, MaxDampingRate = 30;
        private const float MinLever = .05f;
        // Ragdoll.playerArmPositionSpring / Damper / MaxPositionForce: the joint
        // between the player's grip and a grabbed part is the player's arm.
        // A hand that holds something switches to the creature's grip force
        // (Creature_PlayerDefault*.json: gripForcePositionSpringDamperMult
        // 0.5, gripForceMaxPosition 2000 N, PlayerLink.SetJointConfig): a held
        // body or a planted blade pulls with that, not the free hand's 3000 N.
        public const float GripMultiplier = .5f;
        public const float HandSpring = 5000 * GripMultiplier, HandDamper = 40 * GripMultiplier, HandMaxForce = 2000;
        public const int MaxHandles = 2;
        // The grip joint acts alone on its body once the pins follow the hand
        // (see ActiveRagdollBody.ReachTargets): omega*h = 0.31, c*h = 0.5.
        // 20 rad/s with 45/s damping is past critical: at 28 a pierced 1 kg arm
        // shook on the controller's own tremor.
        public const float HandNaturalFrequency = 20, HandDampingRate = 45;
        private readonly int[] _handleBody = { -1, -1 };
        private readonly Vec3[] _handleTarget = new Vec3[MaxHandles], _lastHandleTarget = new Vec3[MaxHandles];
        private readonly bool[] _handleSampled = new bool[MaxHandles];
        // Damager.Penetrate joint: x and y Locked, z (along the blade) free
        // with its own damper. Zero means a grip, locked on every axis.
        private readonly Vec3[] _handleAxis = new Vec3[MaxHandles];
        // A blade at its depth limit (the hilt): zMotion Limited stops the
        // part going further up the blade, never holds it from sliding off.
        // Locked both ways, a blade pulled back dragged the body with it and
        // the stab could not be withdrawn.
        private readonly Vec3[] _pushOnlyAxis = new Vec3[MaxHandles];
        public void SetPushOnly(int h, Vec3 axis)
        {
            if (h < 0 || h >= MaxHandles) return;
            _pushOnlyAxis[h] = InteractionMath.IsFinite(axis) && axis.Length > .5f ? axis.NormalizedCopy() : Vec3.Zero;
        }

        private float[] _mass = new float[0];
        private int[] _parent = new int[0];
        private Vec3[] _lastPosition = new Vec3[0], _lastTarget = new Vec3[0];
        private bool _sampled;
        public int Count { get; private set; }
        public PinState State { get; set; } = PinState.Inert;
        // Blade & Sorcery SetPinForceMultiplier: scales spring, damper and caps.
        public float Multiplier { get; set; } = 1;
        // Standing removes gravity from the parts; the stand-up ramp keeps it.
        public bool CancelGravity { get; set; } = true;
        public bool Grounded { get; set; }
        public float TotalMass { get; private set; }
        // SetPinForceMultiplier with partTypes: a scale on one body's own
        // pins (its position pin and its joint's rotation drive), 1 by default.
        private float[] _partScale = new float[0];
        public void SetPartScale(int body, float scale) { if (body >= 0 && body < Count) _partScale[body] = Math.Max(0, Math.Min(1, scale)); }
        public float PartScale(int body) => body >= 0 && body < Count ? _partScale[body] : 1;

        // parent[i] < 0 marks the root body. Parents must precede children.
        public bool Configure(float[] mass, int[] parent)
        {
            Count = 0; TotalMass = 0; _sampled = false;
            if (mass == null || parent == null || mass.Length != parent.Length || mass.Length == 0 || mass.Length > 64) return false;
            float total = 0;
            int roots = 0;
            for (int i = 0; i < mass.Length; i++)
            {
                if (!InteractionMath.IsFinite(mass[i]) || mass[i] <= 0 || mass[i] > 200) return false;
                if (parent[i] >= i) return false;
                if (parent[i] < 0) roots++;
                total += mass[i];
            }
            if (roots != 1 || parent[0] >= 0 || total > 500) return false;
            _mass = (float[])mass.Clone(); _parent = (int[])parent.Clone();
            _lastPosition = new Vec3[mass.Length]; _lastTarget = new Vec3[mass.Length];
            _partScale = new float[mass.Length];
            for (int i = 0; i < mass.Length; i++) _partScale[i] = 1;
            Count = mass.Length; TotalMass = total;
            return true;
        }

        public float Mass(int body) => body >= 0 && body < Count ? _mass[body] : 0;
        public int Parent(int body) => body >= 0 && body < Count ? _parent[body] : -1;
        // True when ancestor is body itself or one of its parents.
        public bool InSubtree(int body, int ancestor)
        {
            for (int b = body; b >= 0; b = _parent[b]) if (b == ancestor) return true;
            return false;
        }
        public void ResetVelocity() { _sampled = false; for (int h = 0; h < MaxHandles; h++) _handleSampled[h] = false; }

        // Handle h (one per player hand) pulls body towards a world point.
        public bool SetHandle(int h, int body, Vec3 target) => SetHandle(h, body, target, Vec3.Zero);
        public bool SetHandle(int h, int body, Vec3 target, Vec3 freeAxis)
        {
            if (h < 0 || h >= MaxHandles || body < 0 || body >= Count || !InteractionMath.IsFinite(target)) return false;
            if (_handleBody[h] != body) _handleSampled[h] = false;
            _handleBody[h] = body; _handleTarget[h] = target;
            _handleAxis[h] = InteractionMath.IsFinite(freeAxis) && freeAxis.Length > .5f ? freeAxis.NormalizedCopy() : Vec3.Zero;
            return true;
        }
        public void ClearHandle(int h) { if (h >= 0 && h < MaxHandles) { _handleBody[h] = -1; _handleSampled[h] = false; _pushOnlyAxis[h] = Vec3.Zero; ClearSpread(h); } }
        // Damager.Penetrate's joint is locked: PhysX moves the item and the
        // pierced limb as one, and the player's grip (2500 N/m, 2000 N) pulls
        // that whole. A limp arm is pulled here as one too: the grip is sized
        // on the arm's mass and shared over its bodies by weight. On the
        // pierced part alone (1.5 kg, capped at 20 rad/s) the arm trailed far
        // behind the blade and the player had no hold of it.
        private readonly float[][] _spread = new float[MaxHandles][];
        // A blade carrying a dying body: the player holds it up. B&S makes a
        // killed body heavy (ragdolledMass 227.5 kg) and its 2000 N grip lets
        // it drag the weapon down; here, by choice, the arm is stronger: at
        // 2500 N/m a 100-115 kg body sagged 45 cm, pulled the shown sword
        // down with it and threw the stab into chaos.
        public const float CarrySpring = 4000, CarryMaxForce = 2500, CarryMaxSpeed = 1.5f, CarryWeightRatio = 1.25f;
        private readonly bool[] _carry = new bool[MaxHandles];
        public void SetCarry(int h, bool carry) { if (h >= 0 && h < MaxHandles) _carry[h] = carry; }
        public void SetSpread(int h, int body, float weight)
        {
            if (h < 0 || h >= MaxHandles || body < 0 || body >= Count) return;
            if (_spread[h] == null || _spread[h].Length < Count) _spread[h] = new float[Count];
            _spread[h][body] = Math.Max(0, weight);
        }
        public void ClearSpread(int h)
        {
            if (h < 0 || h >= MaxHandles) return;
            _carry[h] = false;
            if (_spread[h] != null) Array.Clear(_spread[h], 0, _spread[h].Length);
        }
        public bool Handled(int body) => body >= 0 && (_handleBody[0] == body || _handleBody[1] == body);
        public bool AnyHandle => _handleBody[0] >= 0 || _handleBody[1] >= 0;

        // dt is the time since the previous call that actually reached PhysX.
        public bool Tick(Vec3[] position, Vec3[] target, float dt, Vec3[] force)
        {
            if (Count == 0 || position == null || target == null || force == null ||
                position.Length < Count || target.Length < Count || force.Length < Count) return false;
            for (int i = 0; i < Count; i++) force[i] = Vec3.Zero;
            if (!InteractionMath.IsFinite(dt) || dt <= 0 || dt > .1f) { _sampled = false; return false; }
            for (int i = 0; i < Count; i++)
                if (!InteractionMath.IsFinite(position[i]) || !InteractionMath.IsFinite(target[i])) { _sampled = false; return false; }
            float multiplier = InteractionMath.IsFinite(Multiplier) ? Math.Max(0, Math.Min(1, Multiplier)) : 0;
            bool sampled = _sampled;
            for (int i = 0; i < Count && State != PinState.Inert; i++)
            {
                Vec3 velocity = sampled ? (position[i] - _lastPosition[i]) * (1 / dt) : Vec3.Zero;
                Vec3 targetVelocity = sampled ? (target[i] - _lastTarget[i]) * (1 / dt) : Vec3.Zero;
                float part = multiplier * _partScale[i];
                if (State == PinState.Standing)
                {
                    float k = Math.Min(SpringPosition * part, _mass[i] * MaxNaturalFrequency * MaxNaturalFrequency);
                    float c = Math.Min(DamperPosition * part, _mass[i] * MaxDampingRate);
                    Vec3 pin = (target[i] - position[i]) * k - (velocity - targetVelocity) * c;
                    force[i] += InteractionMath.ClampMagnitude(pin, MaxPosition * part);
                    if (CancelGravity) force[i] += Vec3.Up * (_mass[i] * Gravity);
                }
                int p = _parent[i];
                // Destabilized keeps no rotation drive on the root (rootPart).
                if (p < 0) continue;
                if (part <= 0)
                {
                    // A released part keeps its joint's damping: PhysX joints
                    // and Unity's implicit solver damp a limp B&S arm; with the
                    // explicit, one-step-late forces here an undamped arm
                    // flailed on the blade and shook the shown weapon with it.
                    Vec3 offsetNow = position[i] - position[p];
                    float limpLever = offsetNow.Length;
                    if (limpLever < MinLever) continue;
                    Vec3 parentVel = sampled ? (position[p] - _lastPosition[p]) * (1 / dt) : Vec3.Zero;
                    float limpReduced = _mass[i] * _mass[p] / (_mass[i] + _mass[p]);
                    float limpDamper = Math.Min(DamperRotation * LimpJointDamping / (limpLever * limpLever), limpReduced * MaxDampingRate);
                    Vec3 drag = InteractionMath.ClampMagnitude((parentVel - velocity) * limpDamper, MaxRotation / limpLever);
                    force[i] += drag; force[p] -= drag;
                    continue;
                }
                float springScale = State == PinState.Standing ? 1 :
                    Grounded ? DestabilizedGroundSpringRotation : DestabilizedSpringRotation;
                float damperScale = State == PinState.Standing ? 1 : DestabilizedDamperRotation;
                Vec3 targetOffset = target[i] - target[p];
                float lever = targetOffset.Length;
                if (lever < MinLever) continue;
                Vec3 offset = position[i] - position[p];
                Vec3 parentVelocity = sampled ? (position[p] - _lastPosition[p]) * (1 / dt) : Vec3.Zero;
                Vec3 parentTargetVelocity = sampled ? (target[p] - _lastTarget[p]) * (1 / dt) : Vec3.Zero;
                float reduced = _mass[i] * _mass[p] / (_mass[i] + _mass[p]);
                float inverseLever = 1 / (lever * lever);
                float kr = Math.Min(SpringRotation * springScale * part * inverseLever,
                    reduced * MaxNaturalFrequency * MaxNaturalFrequency);
                float cr = Math.Min(DamperRotation * damperScale * part * inverseLever, reduced * MaxDampingRate);
                Vec3 relativeVelocity = (velocity - parentVelocity) - (targetVelocity - parentTargetVelocity);
                Vec3 pair = InteractionMath.ClampMagnitude((targetOffset - offset) * kr - relativeVelocity * cr,
                    MaxRotation * part / lever);
                force[i] += pair; force[p] -= pair;
            }
            // The grip joint acts in every state, as the hand joint does in B&S.
            for (int h = 0; h < MaxHandles; h++)
            {
                int i = _handleBody[h];
                if (i < 0 || i >= Count) continue;
                Vec3 velocity = sampled ? (position[i] - _lastPosition[i]) * (1 / dt) : Vec3.Zero;
                Vec3 handVelocity = _handleSampled[h] ? (_handleTarget[h] - _lastHandleTarget[h]) * (1 / dt) : Vec3.Zero;
                float[] spread = _spread[h];
                float moved = _mass[i], weighted = 0;
                if (spread != null && spread.Length >= Count)
                {
                    moved = 0;
                    for (int j = 0; j < Count; j++) { moved += _mass[j] * Math.Min(1, spread[j]); weighted += _mass[j] * spread[j]; }
                    if (weighted <= 0) { spread = null; moved = _mass[i]; }
                }
                else spread = null;
                bool carry = _carry[h] && spread != null;
                float k = Math.Min(carry ? CarrySpring : HandSpring, moved * HandNaturalFrequency * HandNaturalFrequency);
                // At least 0.7 of critical damping on what the grip moves: at
                // HandDamper x 4 (80 N.s/m) a 15 kg part was at 0.2 and a body
                // carried whole at 0.09, and they bounced on the blade.
                float c = Math.Min(Math.Max(HandDamper * 4, 1.4f * (float)Math.Sqrt(k * moved)), moved * HandDampingRate);
                // Carrying a body that hangs on the blade: the grip holds its
                // weight, not more, and drags it no faster than CarryMaxSpeed.
                // At 2500 N and the hand's own speed, a stab pulled out fast
                // flung the 90 kg body at 4 m/s onto the player and up the
                // blade to the hilt; pushed in, it threw it back.
                Vec3 grip = (_handleTarget[h] - position[i]) * k -
                    (velocity - InteractionMath.ClampMagnitude(handVelocity, carry ? CarryMaxSpeed : 12)) * c;
                Vec3 axis = _handleAxis[h];
                if (axis.LengthSquared > 0) grip -= axis * InteractionMath.Dot(grip, axis);
                Vec3 pushOnly = _pushOnlyAxis[h];
                if (pushOnly.LengthSquared > 0 && InteractionMath.Dot(grip, pushOnly) < 0) grip -= pushOnly * InteractionMath.Dot(grip, pushOnly);
                grip = InteractionMath.ClampMagnitude(grip, carry ? Math.Min(CarryMaxForce, moved * 9.81f * CarryWeightRatio) : HandMaxForce);
                if (spread == null) force[i] += grip;
                else for (int j = 0; j < Count; j++) if (spread[j] > 0) force[j] += grip * (_mass[j] * spread[j] / weighted);
                _lastHandleTarget[h] = _handleTarget[h]; _handleSampled[h] = true;
            }
            for (int i = 0; i < Count; i++) { _lastPosition[i] = position[i]; _lastTarget[i] = target[i]; }
            _sampled = true;
            return true;
        }
    }
}
