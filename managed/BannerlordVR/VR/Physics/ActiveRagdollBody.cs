using System;
using System.Globalization;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Bannerlord adapter for the Blade & Sorcery pin drive. One physical body
    // per ragdoll body (its first mapped bone); targets are the entity-local
    // animated bone frames captured just before the native ragdoll starts,
    // placed in a root frame that the stand-up re-anchors under the hips.
    internal sealed class ActiveRagdollBody
    {
        private const int MaxBodies = 32;
        public const float MaxAngularVelocity = 7;
        private readonly RagdollPinDrive _drive = new RagdollPinDrive();
        private readonly ActiveRagdollCycle _cycle = new ActiveRagdollCycle();
        // Bone hierarchies per skeleton layout. Walking parents through the
        // engine cost one native call per step, thousands per activation.
        private static readonly System.Collections.Generic.Dictionary<Monster, sbyte[]> Parents =
            new System.Collections.Generic.Dictionary<Monster, sbyte[]>();
        private sbyte[] _parentOf = new sbyte[0];
        private readonly PhysicsStepGate _gate = new PhysicsStepGate();
        private readonly MatrixFrame[] _captured = new MatrixFrame[128];
        private readonly sbyte[] _bone = new sbyte[MaxBodies];
        private readonly Vec3[] _position = new Vec3[MaxBodies], _target = new Vec3[MaxBodies], _force = new Vec3[MaxBodies];
        // Rotation pins: each body's world axes now and at the last applied step.
        private readonly Vec3[] _rs = new Vec3[MaxBodies], _rf = new Vec3[MaxBodies], _ru = new Vec3[MaxBodies];
        private readonly Vec3[] _ps = new Vec3[MaxBodies], _pf = new Vec3[MaxBodies], _pu = new Vec3[MaxBodies];
        private bool _rotationSampled;
        private float[] _torques = new float[0];
        private MatrixFrame _root, _capturedRoot;
        private int _boneCount, _pelvisBody;
        private bool _captureValid, _configured, _destabilizeOnStart;
        private float _lastApplied = -1, _lastRootSample = -1, _nextGround;
        private bool _nearGround;
        private Vec3 _lastPelvis, _groundPoint;
        private Vec3 _pendingImpulse; private sbyte _pendingBone = -1;
        // A hit waiting for the next PhysX step, and the AddForceCoroutine of
        // the last hit on a destabilized body.
        private sbyte _hitBone = -1; private Vec3 _hitVelocity; private float _hitMass; private bool _hitFist;
        private int _addForceBody = -1; private Vec3 _addForceVelocity; private float _addForceUntil;
        // Only hit since it went physical (never grabbed nor pierced), and
        // how long its hit reaction lasts.
        private bool _touched; private float _hitHold;
        // Ragdoll.physicEnabledDuration: a standing body untouched for 2 s
        // returns to animation.
        public const float PhysicEnabledDuration = 2;
        // How long an untouched standing body stays physical: B&S's
        // physicEnabledDuration, or much less for an enemy taken from the
        // fight (its AI is paused for as long as the physics holds it).
        public float HoldSeconds { get; set; } = PhysicEnabledDuration;
        private float _lastInteraction;
        private int[] _bodyOfBone = new int[0];
        private int _headBody = -1;
        // Bodies of the arms (upper arm, forearm, hand): HandleRagdoll's
        // releaseArmSpring/Damper act on RagdollPart.Type.*Arm | *Hand.
        private bool[] _armBody = new bool[0];
        private readonly bool[] _limpHandle = new bool[2];
        private readonly int[] _limpTop = { -1, -1 };
        // The pierced part's blade direction now (from its entry direction).
        private readonly Vec3[] _pierceAxis = new Vec3[2];
        // PlayerLink slerp drive on a held item: Ragdoll.playerArmRotation
        // Spring 1000 / Damper 40, x gripForceRotationSpringDamperMult
        // (0.5, 1), at most gripForceMaxRotation 100 N.m.
        public const float GripRotationSpring = 500, GripRotationDamper = 40, GripMaxTorque = 100;
        private struct GrabSlot
        {
            public bool Active, HasBase; public sbyte Bone; public GrabHandle Kind; public Vec3 Local;
            // Heights measured when the hand closed. B&S compares with the
            // animated pose, which its physical body matches; a Bannerlord
            // ragdoll settles about 6 cm off it on activation, which tripped
            // the 5 cm lift rule on the very first frame of every grab.
            public float BaseReference, BaseRoot;
            // A blade inside the part instead of a hand on it: locked across
            // the blade, free along it, no lift rules.
            public bool Blade, AxialLock; public Vec3 Axis;
            // Damager angular X/Y limit: the blade may tilt this far (rad)
            // from its entry direction in the part before it levers the part.
            public Vec3 LocalAxis; public float AngleLimit;
            // HandleRagdoll.isBackGrab, decided when the hand closed.
            public bool HasFacing, BackGrab;
        }
        private readonly GrabSlot[] _grabs = new GrabSlot[RagdollPinDrive.MaxHandles];
        private readonly bool[] _ungrab = new bool[RagdollPinDrive.MaxHandles];
        private readonly Vec3[] _hand = new Vec3[RagdollPinDrive.MaxHandles];
        // Where each grabbed body should be for its contact to sit in the hand.
        private readonly int[] _grabBody = { -1, -1 };
        private readonly Vec3[] _grabTarget = new Vec3[RagdollPinDrive.MaxHandles];
        // HandleRagdollData.useIK: B&S moves the animated limb itself to the
        // grip (hand, shoulder or head IK anchor), so its pins pull WITH the
        // player's hand. Without IK on a Bannerlord ragdoll the same is done
        // on the pin targets: the grabbed body and everything below it follow
        // the hand fully, its parent half way and the grandparent a quarter,
        // the way an arm reaches; the hips stay unless the creature steps.
        private static readonly float[] ReachShare = { 1, .5f, .25f };
        public GrabOutcome LastGrabOutcome { get; private set; }
        // Ragdoll collision damage bookkeeping (RagdollImpact).
        private readonly Vec3[] _lastSample = new Vec3[MaxBodies], _sampleVelocity = new Vec3[MaxBodies];
        private readonly float[] _impactUntil = new float[MaxBodies];
        private bool _velocitySampled;
        public Agent LastPusher { get; set; }
        public sbyte ImpactBone { get; private set; } = -1;
        public float ImpactSpeed { get; private set; }
        public Vec3 ImpactPoint { get; private set; }
        public int Impacts { get; private set; }
        public bool Configured => _configured;
        // True on the tick that bound the bodies (native mass read, tree build).
        public bool ConfiguredThisTick { get; private set; }
        public double NativeMassMilliseconds { get; private set; }
        public ActiveRagdollPhase Phase => _cycle.Phase;
        public FallState Fall => _cycle.Fall;
        public int AppliedSteps { get; private set; }
        public int SkippedTicks { get; private set; }
        public float MaxStandingError { get; private set; }
        public float LastPoseError { get; private set; }
        public int Bodies => _drive.Count;
        public float RootSpeed { get; private set; }
        // Any visible drag counts: below this the animation restarted at the
        // old spot and the released enemy snapped back.
        public bool Moved => _configured && ((_root.origin - _capturedRoot.origin).AsVec2.Length > .05f || Turned);
        public Vec3 RecoveryPosition => _root.origin;
        public Vec3 PelvisPosition => _lastPelvis;
        // Set on the tick the body starts getting up; the mission logic may
        // hand it to an animation instead of the stand-up pins.
        public bool RiseRequested { get; set; }
        public Vec3 HeadPosition => _configured && _headBody >= 0 ? _position[_headBody] : _lastPelvis;

        // Lying on the back: the pelvis front (agent forward in the captured
        // pose) points up. bodyUp is the pelvis-to-head axis in the world.
        public bool LyingOnBack(out Vec3 bodyUp)
        {
            bodyUp = Vec3.Up;
            if (!_configured || _pelvisBody < 0) return false;
            Mat3 posed = _captured[_bone[_pelvisBody]].rotation;
            posed.Orthonormalize();
            Vec3 front = posed.TransformToLocal(new Vec3(0, 1, 0)), up = posed.TransformToLocal(new Vec3(0, 0, 1));
            int b = _pelvisBody;
            Vec3 worldFront = _rs[b] * front.x + _rf[b] * front.y + _ru[b] * front.z;
            bodyUp = _rs[b] * up.x + _rf[b] * up.y + _ru[b] * up.z;
            return worldFront.z > .5f;
        }

        // Creature.Kill: ragdoll.SetState(Inert), brain stopped. A stab that
        // will kill (its damage is held until the blade leaves, the agent at 1
        // point) leaves the body Inert on the blade: no pins, gravity on, only
        // the blade's joint holding it. Standing pinned at 1 point, the body
        // fought the blade, was flung (7 m/s) or rose 2 m off its pose.
        public bool Dying { get; private set; }

        // SkillDiscombobulate.StunCreature: the brain is switched off until
        // StunWakeTime; a destabilized body goes Inert (no pins) and cannot
        // stand up before it wakes. A later stun only ever extends it.
        private float _knockedUntil = -1;
        public bool KnockedOut => _knockedUntil > _lastRootSample;
        public void KnockOut(float now, float seconds)
        {
            if (!InteractionMath.IsFinite(now) || seconds <= 0) return;
            _knockedUntil = Math.Max(_knockedUntil, now + seconds);
            Destabilize();
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "ACTIVE_RAGDOLL knocked out until {0:F2} ({1:F1} s) phase={2}", _knockedUntil, _knockedUntil - now, _cycle.Phase));
        }

        // Tales of Glory NpcAI.FadeToDeath (PuppetMaster): a vital stab sets
        // the puppet's pin and muscle weights to 0.3 and eases both to 0 at
        // 1/5 a second (1.5 s): the body sags on its pose and on the blade,
        // gravity on, then is a limp ragdoll. Already down, it is limp at once.
        private float _fadeFrom = -1, _fadeMul = -1;
        public const float FadeStart = .3f, FadeRate = .2f;
        public bool Fading => _fadeFrom >= 0 && !Dying;
        public void FadeToDeath(float now)
        {
            if (Dying || !_configured || Fading) return;
            if (_cycle.Phase != ActiveRagdollPhase.Standing && _cycle.Phase != ActiveRagdollPhase.Recovered) { Die(); return; }
            _fadeFrom = now;
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "ACTIVE_RAGDOLL fading to death (pins {0:F1} to 0 over {1:F1} s) phase={2}", FadeStart, FadeStart / FadeRate, _cycle.Phase));
        }

        public void Die()
        {
            if (Dying || !_configured) return;
            Dying = true;
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "ACTIVE_RAGDOLL dying: inert on the blade phase={0} pose_error={1:F2}", _cycle.Phase, LastPoseError));
            if (_cycle.Phase != ActiveRagdollPhase.Destabilized) _cycle.Destabilize();
        }

        public void Clear()
        {
            Dying = false; _knockedUntil = -1; HoldSeconds = PhysicEnabledDuration; _fadeFrom = -1; _fadeMul = -1;
            _cycle.Stop(); _gate.Reset(); _drive.ResetVelocity();
            _captureValid = _configured = _destabilizeOnStart = false;
            _lastApplied = _lastRootSample = -1; _nextGround = 0; _nearGround = false;
            AppliedSteps = SkippedTicks = 0; MaxStandingError = LastPoseError = RootSpeed = 0;
            _pendingBone = -1; _pendingImpulse = Vec3.Zero; _rotationSampled = false;
            _hitBone = -1; _addForceBody = -1; _addForceUntil = 0; _touched = false; _hitHold = 0;
            _lastInteraction = 0; _headBody = -1; LastGrabOutcome = GrabOutcome.Hold; _lastTurn = -1; _turned = 0;
            _velocitySampled = false; LastPusher = null; ImpactBone = -1; ImpactSpeed = 0; Impacts = 0;
            for (int i = 0; i < MaxBodies; i++) _impactUntil[i] = 0;
            for (int h = 0; h < _grabs.Length; h++) { _grabs[h] = default(GrabSlot); _ungrab[h] = false; }
        }

        // Last standing pose captured per skeleton layout. The pins target the
        // pose the body had when it went physical; a body caught in a fall or
        // jump action would be held in that free-fall pose (B&S pins follow
        // the creature's standing animator instead).
        private static readonly System.Collections.Generic.Dictionary<Monster, MatrixFrame[]> StandingPose =
            new System.Collections.Generic.Dictionary<Monster, MatrixFrame[]>();
        public static bool Airborne(Agent agent)
        {
            Agent.ActionCodeType type = agent.GetCurrentActionType(0);
            return type == Agent.ActionCodeType.Fall || type == Agent.ActionCodeType.Jump ||
                type == Agent.ActionCodeType.JumpStart || type == Agent.ActionCodeType.JumpEnd || type == Agent.ActionCodeType.JumpEndHard;
        }
        public string CaptureSource { get; private set; } = "-";

        // Before StartRagdollAsCorpse, while the skeleton is still animated.
        public bool Capture(Agent agent, Skeleton skeleton, bool destabilized)
        {
            Clear();
            _boneCount = skeleton.GetBoneCount();
            if (_boneCount < 2 || _boneCount > _captured.Length) return false;
            bool airborne = Airborne(agent);
            MatrixFrame[] standing = null;
            if (airborne && agent.Monster != null && StandingPose.TryGetValue(agent.Monster, out standing) && standing.Length != _boneCount) standing = null;
            for (sbyte b = 0; b < _boneCount; b++)
            {
                _captured[b] = standing != null ? standing[b] : skeleton.GetBoneEntitialFrameWithIndex(b);
                if (!InteractionMath.IsFinite(_captured[b].origin)) return false;
            }
            CaptureSource = standing != null ? "standing_reference" : airborne ? "airborne_current" : "current";
            if (!airborne && !destabilized && agent.Monster != null)
            {
                var copy = new MatrixFrame[_boneCount];
                Array.Copy(_captured, copy, _boneCount);
                StandingPose[agent.Monster] = copy;
            }
            _capturedRoot = _root = agent.AgentVisuals.GetEntity().GetGlobalFrame();
            Vec3 look = agent.LookDirection; look.z = 0;
            Vec3 local = look.Length > 1e-4f ? _root.rotation.TransformToLocal(look.NormalizedCopy()) : new Vec3(0, 1, 0);
            _facingLocal = local.Length > 1e-4f ? local.NormalizedCopy() : new Vec3(0, 1, 0);
            _destabilizeOnStart = destabilized;
            _lastInteraction = agent.Mission?.CurrentTime ?? 0;
            _captureValid = InteractionMath.IsFinite(_root.origin);
            return _captureValid;
        }

        // First tick with an active native ragdoll: bind bodies and their tree.
        private bool Configure(Agent agent, Mission mission, Skeleton skeleton)
        {
            var boneMass = new float[_boneCount];
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            bool read = NativeRagdollBridge.ReadBoneMasses(agent, mission, skeleton, boneMass);
            NativeMassMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (!read) return false;
            var bodyOfBone = new int[_boneCount];
            int count = 0;
            for (int b = 0; b < _boneCount; b++)
            {
                bodyOfBone[b] = -1;
                if (boneMass[b] <= 0) continue;
                if (count >= MaxBodies) return false;
                bodyOfBone[b] = count; _bone[count] = (sbyte)b; count++;
            }
            if (count < 2) return false;
            _parentOf = ParentsOf(agent.Monster, skeleton);
            var mass = new float[count];
            var parent = new int[count];
            int pelvis = agent.Monster.PelvisBoneIndex;
            _pelvisBody = -1;
            for (int i = 0; i < count; i++)
            {
                mass[i] = boneMass[_bone[i]];
                parent[i] = -1;
                for (sbyte p = _parentOf[_bone[i]]; p >= 0; p = _parentOf[p])
                    if (bodyOfBone[p] >= 0) { parent[i] = bodyOfBone[p]; break; }
                if (_bone[i] == pelvis) _pelvisBody = i;
            }
            _bodyOfBone = new int[_boneCount];
            for (int b = 0; b < _boneCount; b++)
            {
                _bodyOfBone[b] = bodyOfBone[b];
                for (sbyte p = _parentOf[b]; _bodyOfBone[b] < 0 && p >= 0; p = _parentOf[p])
                    _bodyOfBone[b] = bodyOfBone[p];
                if (_bodyOfBone[b] < 0) _bodyOfBone[b] = 0;
            }
            sbyte head = agent.Monster.HeadLookDirectionBoneIndex;
            _headBody = head >= 0 && head < _boneCount ? _bodyOfBone[head] : -1;
            _armBody = new bool[count];
            try
            {
                for (int i = 0; i < count; i++)
                {
                    BoneBodyPartType part = agent.AgentVisuals.GetBoneTypeData(_bone[i]).BodyPartType;
                    // Bannerlord types the upper arm Shoulder*, the forearm Arm*: the
                    // B&S arm part is both. With the upper arm still pinned the
                    // limp forearm only swung about a fixed elbow.
                    // The clavicle is a torso part in B&S: it stays pinned, and a
                    // stab into the shoulder leaves the arm alone.
                    string name = (skeleton.GetBoneName(_bone[i]) ?? "").ToLowerInvariant();
                    _armBody[i] = !name.Contains("clavicle") && (part == BoneBodyPartType.ArmLeft || part == BoneBodyPartType.ArmRight ||
                        part == BoneBodyPartType.ShoulderLeft || part == BoneBodyPartType.ShoulderRight);
                }
            }
            catch (Exception) { Array.Clear(_armBody, 0, _armBody.Length); }
            // Several unparented bodies would be separate trees: hang them on
            // the first one so each still has a joint pin.
            for (int i = 1; i < count; i++) if (parent[i] < 0) parent[i] = 0;
            // The root body carries the hips when the pelvis bone is merged into it.
            if (_pelvisBody < 0) _pelvisBody = 0;
            if (!_drive.Configure(mass, parent))
            {
                PhysicsLog.Info("ACTIVE_RAGDOLL refused=body_tree; bodies=" + count);
                return false;
            }
            _cycle.Begin(_destabilizeOnStart);
            _configured = true;
            // Same order of magnitude as B&S Rigidbody.maxDepenetration and
            // part velocity caps: a solver spike cannot launch a soldier.
            // B&S leaves Rigidbody.maxAngularVelocity at Unity's 7 rad/s on
            // every ragdoll part; 40 let unpinned limbs (Bannerlord gives no
            // torque, so nothing holds a part's own twist) spin six times faster.
            agent.SetVelocityLimitsOnRagdoll(20, MaxAngularVelocity);
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "ACTIVE_RAGDOLL configured bodies={0} total_mass={1:F1} pelvis_body_bone={2} start={3} native_mass_ms={4:F3} arm_bodies={5}",
                count, _drive.TotalMass, _bone[_pelvisBody], _cycle.Phase, NativeMassMilliseconds, Array.FindAll(_armBody, a => a).Length) +
                " masses:" + string.Join(",", Array.ConvertAll(Enumerable(count), i => skeleton.GetBoneName(_bone[i]) + "=" +
                    _drive.Mass(i).ToString("F1", CultureInfo.InvariantCulture))));
            return true;
        }

        private sbyte[] ParentsOf(Monster monster, Skeleton skeleton)
        {
            if (monster != null && Parents.TryGetValue(monster, out sbyte[] cached) && cached.Length == _boneCount) return cached;
            var parents = new sbyte[_boneCount];
            for (sbyte b = 0; b < _boneCount; b++)
            {
                sbyte p = skeleton.GetParentBoneIndex(b);
                // A malformed hierarchy must not loop: parents precede children.
                parents[b] = p < b ? p : (sbyte)-1;
            }
            if (monster != null) Parents[monster] = parents;
            return parents;
        }

        private static int[] Enumerable(int count) { var a = new int[count]; for (int i = 0; i < count; i++) a[i] = i; return a; }

        public void Destabilize()
        {
            _lastInteraction = Math.Max(_lastInteraction, _lastRootSample);
            if (_configured) _cycle.Destabilize(); else _destabilizeOnStart = true;
        }

        // HandleRagdoll.OnGrab: the contact point is anchored in its bone and
        // pulled to the player's grip. Returns false once the handle let go.
        public bool Handle(int hand, sbyte bone, GrabHandle kind, Vec3 handWorld, MatrixFrame boneWorld, Vec3 contactWorld)
        {
            if (hand < 0 || hand >= _grabs.Length || !InteractionMath.IsFinite(handWorld)) return false;
            if (_ungrab[hand]) return false;
            _touched = true;
            if (!_grabs[hand].Active || _grabs[hand].Bone != bone)
                _grabs[hand] = new GrabSlot { Active = true, Bone = bone, Kind = kind,
                    Local = boneWorld.TransformToLocalNonOrthogonal(contactWorld) };
            _hand[hand] = handWorld;
            return true;
        }
        // Damager.Penetrate: the pierced part is jointed to the blade, held by
        // the player's arm. bladePoint is the blade point closest to the
        // contact, so the joint only corrects across the blade.
        public bool Pierce(int hand, sbyte bone, Vec3 bladePoint, Vec3 axis, bool axialLock, Vec3 localAxis, float angleLimit,
            MatrixFrame boneWorld, Vec3 contactWorld)
        {
            if (hand < 0 || hand >= _grabs.Length || !_configured || !InteractionMath.IsFinite(bladePoint) || !InteractionMath.IsFinite(axis)) return false;
            _touched = true;
            if (!_grabs[hand].Active || !_grabs[hand].Blade || _grabs[hand].Bone != bone)
                _grabs[hand] = new GrabSlot { Active = true, Bone = bone, Kind = GrabHandle.Torso, Blade = true,
                    Local = boneWorld.TransformToLocalNonOrthogonal(contactWorld) };
            GrabSlot slot = _grabs[hand]; slot.Axis = axis; slot.AxialLock = axialLock;
            slot.LocalAxis = localAxis; slot.AngleLimit = Math.Max(0, angleLimit); _grabs[hand] = slot;
            _hand[hand] = bladePoint;
            return true;
        }
        public void ReleaseHandle(int hand)
        {
            if (hand < 0 || hand >= _grabs.Length) return;
            _grabs[hand] = default(GrabSlot); _ungrab[hand] = false;
            _drive.ClearHandle(hand); _grabBody[hand] = -1;
        }
        public bool Grabbed => _grabs[0].Active || _grabs[1].Active;
        public float TotalMass => _configured ? _drive.TotalMass : 0;
        public float PartMass(sbyte bone) =>
            _configured && bone >= 0 && bone < _bodyOfBone.Length ? _drive.Mass(_bodyOfBone[bone]) : 5;

        // A one-step push on a body, e.g. the hit that destabilized it. It is
        // queued so that it shares the PhysX step gate with the drive.
        public void QueuePush(sbyte bone, Vec3 force)
        {
            if (!InteractionMath.IsFinite(force)) return;
            _pendingBone = bone; _pendingImpulse = InteractionMath.ClampMagnitude(force, 4000);
            _lastInteraction = Math.Max(_lastInteraction, _lastRootSample);
        }

        // A strike on a part. A fist is the B&S hand contact (the part is
        // brought to the fist's speed within the arm joint's force); an item
        // gives its momentum. On a body that is no longer standing the
        // damager's AddForceCoroutine follows for 0.15 s.
        // A force held on one part for this tick (a blade pressing the shield
        // the hand holds): applied with the drive, at most once per step.
        private sbyte _pressBone = -1; private Vec3 _pressForce; private float _pressUntil;
        // Held by something that is not a grab (an axe bitten in): an
        // interaction every tick, so physicEnabledDuration never runs out
        // under it (the body went back to animation 2 s in, the axe fell out).
        public void Touch() => _lastInteraction = Math.Max(_lastInteraction, _lastRootSample);

        public void Press(sbyte bone, Vec3 force, float now)
        {
            if (bone < 0 || !InteractionMath.IsFinite(force)) return;
            _pressBone = bone; _pressForce = force; _pressUntil = now + .05f;
            _hitHold = Math.Max(_hitHold, HitReaction);
            _lastInteraction = Math.Max(_lastInteraction, _lastRootSample);
        }

        public void QueueHit(sbyte bone, Vec3 velocity, float hitMass, bool fist, PushEffect push)
        {
            if (!InteractionMath.IsFinite(velocity) || bone < 0) return;
            _hitBone = bone; _hitVelocity = velocity; _hitMass = hitMass; _hitFist = fist;
            _hitHold = Math.Max(_hitHold, HitReaction + PushReaction.Recoil(push));
            _lastInteraction = Math.Max(_lastInteraction, _lastRootSample);
        }
        // A body only hit goes back to its animation once it has swayed back
        // to its pose, after the push's recoil (B&S plays the stagger on the
        // still-physical body; here the pose is held while physical).
        // A body only hit goes back to animation once nearly on its pose and
        // still: at 15 cm off it slid sideways into the animated pose.
        public const float HitReaction = .5f, HitReleaseError = .05f, HitReleaseSpeed = .3f, ItemSwayDv = 2.5f;

        private void ApplyHit(float now, float dt)
        {
            if (_hitBone >= 0)
            {
                int b = _hitBone < _bodyOfBone.Length ? _bodyOfBone[_hitBone] : -1;
                if (b >= 0 && b < _drive.Count)
                {
                    // An item's momentum, held to a sway on the struck part: a
                    // 7.6 m/s slash on a 4 kg head gave it 6 m/s and launched a
                    // standing body at 9 m/s. B&S edges cut through rather
                    // than shove; its push levels carry the rest.
                    Vec3 impulse = _hitFist ? PunchImpact.ContactImpulse(_drive.Mass(b), _hitVelocity)
                        : InteractionMath.ClampMagnitude(_hitVelocity * Math.Max(.5f, Math.Min(8, _hitMass)),
                            Math.Min(PunchImpact.MaxImpulse, _drive.Mass(b) * ItemSwayDv));
                    _force[b] += impulse * PunchImpact.StepRate;
                    if (_cycle.Phase == ActiveRagdollPhase.Destabilized)
                    {
                        _addForceBody = b; _addForceVelocity = _hitVelocity; _addForceUntil = now + PunchImpact.AddForceDuration;
                    }
                }
                _hitBone = -1;
            }
            if (_addForceBody < 0 || now > _addForceUntil || _cycle.Phase != ActiveRagdollPhase.Destabilized) return;
            for (int i = 0; i < _drive.Count; i++)
                _force[i] += PunchImpact.AddForceAcceleration(_addForceVelocity, i == _addForceBody, dt) * _drive.Mass(i);
        }

        // At most one activation per mission tick (native mass read included).
        private static float _configureTick = -1;
        public void Tick(Agent agent, Skeleton skeleton, Mission mission, PhysicsStepCounter clock)
        {
            if (!_captureValid) return;
            ConfiguredThisTick = false;
            if (!_configured && _configureTick == mission.CurrentTime) return;
            if (!_configured)
            {
                _configureTick = mission.CurrentTime;
                if (!Configure(agent, mission, skeleton)) { _captureValid = false; return; }
                ConfiguredThisTick = true;
            }
            float now = mission.CurrentTime;
            MatrixFrame frame = agent.AgentVisuals.GetEntity().GetGlobalFrame();
            for (int i = 0; i < _drive.Count; i++)
            {
                MatrixFrame bw = frame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(_bone[i]));
                _position[i] = bw.origin;
                bw.rotation.Orthonormalize();
                _rs[i] = bw.rotation.s; _rf[i] = bw.rotation.f; _ru[i] = bw.rotation.u;
            }
            Vec3 pelvis = _position[_pelvisBody];
            DetectImpacts(now);
            float sampleDt = _lastRootSample < 0 ? 0 : now - _lastRootSample;
            if (sampleDt > 0) RootSpeed = (pelvis - _lastPelvis).Length / sampleDt;
            // A body thrown at more than a running speed: say what held it.
            if (RootSpeed > LaunchSpeed && sampleDt > 0 && now - _lastLaunchLog > 1)
            {
                _lastLaunchLog = now;
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "ACTIVE_RAGDOLL launched speed={0:F1} phase={1} slot0={2} slot1={3} hit_until={4:F2} pose_error={5:F2}",
                    RootSpeed, _cycle.Phase, Describe(0), Describe(1), Math.Max(0, _addForceUntil - now), LastPoseError));
            }
            _lastPelvis = pelvis; _lastRootSample = now;
            if (now >= _nextGround)
            {
                // Creature.SphereCastGround within hips height, from the root.
                _nearGround = mission.Scene.RayCastForClosestEntityOrTerrainFixedPhysics(pelvis + Vec3.Up * .1f,
                    pelvis - Vec3.Up * 1.1f, out float _, out Vec3 hit, .05f) && InteractionMath.IsFinite(hit);
                if (_nearGround) _groundPoint = hit;
                _nextGround = now + .1f;
            }
            UpdateHandles(skeleton, frame, now);
            ReleaseHeldArms();
            // bodyTurnDirection is off: B&S turns the creature's animator,
            // whose legs step round; turning a frozen pose with its feet on
            // the ground twisted the legs and swung the limbs (3.6 rad of
            // turn over a few grabs in the logs).
            if (EnemyPhysicsSettings.GrabBodyTurn) TurnBody(mission.MainAgent, now);
            ActiveRagdollPhase before = _cycle.Phase;
            bool knocked = now < _knockedUntil;
            _cycle.Tick(sampleDt, _nearGround, RootSpeed, Grabbed || knocked);
            if (before == ActiveRagdollPhase.Destabilized && _cycle.Phase == ActiveRagdollPhase.StandingUp) { ReanchorUnderHips(); RiseRequested = true; }
            if (_cycle.Phase != before)
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "ACTIVE_RAGDOLL phase={0} fall={1} time={2:F3} root_speed={3:F2} pose_error={4:F3}",
                    _cycle.Phase, _cycle.Fall, now, RootSpeed, LastPoseError));
            for (int i = 0; i < _drive.Count; i++) _target[i] = _root.TransformToParent(_captured[_bone[i]]).origin;
            ReachTargets();
            // A released (limp) arm is off its pose by design: counted, a
            // pierced arm put the whole body 0.41 m off its pose, the start
            // guard felled it, and the blade could not be drawn out of a body
            // with nothing holding it up.
            float error = 0;
            for (int i = 0; i < _drive.Count; i++)
                if (_drive.PartScale(i) > 0) error = Math.Max(error, (_position[i] - _target[i]).Length);
            LastPoseError = error;
            if (_cycle.Phase == ActiveRagdollPhase.Standing) MaxStandingError = Math.Max(MaxStandingError, error);
            if (clock == null || !clock.Running || !_gate.CanApply(clock.Steps)) { SkippedTicks++; return; }
            float dt = _lastApplied < 0 ? 1f / 90 : now - _lastApplied;
            _drive.State = _cycle.DriveState;
            _fadeMul = -1;
            if (Fading)
            {
                _fadeMul = Math.Max(0, FadeStart - (now - _fadeFrom) * FadeRate);
                if (_fadeMul <= 0) { _fadeFrom = -1; _fadeMul = -1; Die(); _drive.State = _cycle.DriveState; }
            }
            _drive.Multiplier = _fadeMul >= 0 ? _fadeMul : _cycle.PinMultiplier;
            _drive.CancelGravity = _fadeMul < 0 && _cycle.CancelGravity;
            _drive.Grounded = _nearGround;
            // A body that starts far off its pose (a skeleton caught mid
            // change) would be flung by its pins: it goes limp instead.
            if (_cycle.Phase == ActiveRagdollPhase.Standing && AppliedSteps < 10 && LastPoseError > StartPoseLimit)
            {
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "ACTIVE_RAGDOLL started {0:F2} m off its pose: destabilized instead of pinned", LastPoseError));
                _cycle.Destabilize();
                _drive.State = _cycle.DriveState;
            }
            // The death blow lands as the last blade leaves; a body that lived
            // through it falls and gets up instead of staying inert for good.
            if (Dying && !Grabbed) Dying = false;
            if (Dying || knocked) _drive.State = PinState.Inert;
            if (!_drive.Tick(_position, _target, Math.Min(dt, .1f), _force)) { _lastApplied = now; return; }
            ApplyRotationPins(agent, skeleton, mission, Math.Min(dt, .1f));
            ApplyHit(now, Math.Min(dt, .1f));
            if (_pressBone >= 0 && now <= _pressUntil && _pressBone < _bodyOfBone.Length)
            {
                int pb = _bodyOfBone[_pressBone];
                if (pb >= 0 && pb < _drive.Count) _force[pb] += _pressForce;
            }
            for (int i = 0; i < _drive.Count; i++)
            {
                if (_pendingBone == _bone[i]) { _force[i] += _pendingImpulse; _pendingBone = -1; }
                if (_force[i].LengthSquared > 0) agent.ApplyForceOnRagdoll(_bone[i], in _force[i]);
            }
            if (_pendingBone >= 0)
            {
                agent.ApplyForceOnRagdoll(_pendingBone, in _pendingImpulse);
                _pendingBone = -1;
            }
            _gate.Applied(clock.Steps);
            _lastApplied = now;
            AppliedSteps++;
        }

        // B&S slerp drive per part towards its posed orientation, with the
        // state's scales (no drive on the root once destabilized, as for the
        // position pins).
        private void ApplyRotationPins(Agent agent, Skeleton skeleton, Mission mission, float dt)
        {
            bool inert = _drive.State == PinState.Inert;
            // An inert body (dying on the blade, knocked out) has no pins,
            // but its joints keep the damping of their spin: the engine's
            // ragdoll joints have next to none, and a body hanging on a
            // blade that moves had every limb spinning about its joint
            // (Unity's character joints, under B&S's inert ragdoll, drag).
            if (!EnemyPhysicsSettings.RotationPins || !NativeRagdollBridge.TorquesAvailable) { _rotationSampled = false; return; }
            int needed = _boneCount * 3;
            if (_torques.Length < needed) _torques = new float[needed];
            Array.Clear(_torques, 0, needed);
            bool standing = _drive.State == PinState.Standing;
            float spring = standing ? 1 : _nearGround ? RagdollPinDrive.DestabilizedGroundSpringRotation : RagdollPinDrive.DestabilizedSpringRotation;
            float damper = standing ? 1 : RagdollPinDrive.DestabilizedDamperRotation;
            float multiplier = Math.Max(0, Math.Min(1, _fadeMul >= 0 ? _fadeMul : _cycle.PinMultiplier));
            for (int i = 0; i < _drive.Count; i++)
            {
                if (!standing && i == _pelvisBody) continue;
                bool limp = inert || _drive.PartScale(i) <= 0;
                MatrixFrame posed = _root.TransformToParent(_captured[_bone[i]]);
                posed.rotation.Orthonormalize();
                Vec3 error = limp ? Vec3.Zero : RotationPin.Error(_rs[i], _rf[i], _ru[i], posed.rotation.s, posed.rotation.f, posed.rotation.u);
                Vec3 omega = _rotationSampled && dt > 0
                    ? RotationPin.Error(_ps[i], _pf[i], _pu[i], _rs[i], _rf[i], _ru[i]) * (1 / dt) : Vec3.Zero;
                // A limp arm keeps only the damping of its spin (see the drive).
                Vec3 a = limp ? RotationPin.Acceleration(Vec3.Zero, omega, _drive.Mass(i), 0, RagdollPinDrive.LimpJointDamping, 1)
                    : RotationPin.Acceleration(error, omega, _drive.Mass(i), spring, damper, multiplier);
                int b = _bone[i] * 3;
                _torques[b] = a.x; _torques[b + 1] = a.y; _torques[b + 2] = a.z;
            }
            ApplyPierceTorque(dt);
            for (int i = 0; i < _drive.Count; i++) { _ps[i] = _rs[i]; _pf[i] = _rf[i]; _pu[i] = _ru[i]; }
            _rotationSampled = true;
            NativeRagdollBridge.AddTorques(agent, mission, skeleton, _torques);
        }

        // Only a falling (destabilized) body collides with enough speed; the
        // strongest impact of the tick is reported to the mission logic.
        private void DetectImpacts(float now)
        {
            ImpactBone = -1; ImpactSpeed = 0;
            float dt = _lastRootSample < 0 ? 0 : now - _lastRootSample;
            if (dt <= 0 || dt > .1f) { _velocitySampled = false; }
            for (int i = 0; i < _drive.Count; i++)
            {
                Vec3 velocity = _velocitySampled ? (_position[i] - _lastSample[i]) * (1 / dt) : Vec3.Zero;
                if (_velocitySampled && _cycle.Phase == ActiveRagdollPhase.Destabilized && now >= _impactUntil[i])
                {
                    float change = (_sampleVelocity[i] - velocity).Length;
                    float speed = RagdollImpact.Detect(_sampleVelocity[i].Length, velocity.Length, change, i == _headBody);
                    if (speed > ImpactSpeed)
                    {
                        ImpactSpeed = speed; ImpactBone = _bone[i]; ImpactPoint = _position[i];
                        _impactUntil[i] = now + RagdollImpact.Cooldown;
                    }
                }
                _sampleVelocity[i] = velocity; _lastSample[i] = _position[i];
            }
            if (ImpactBone >= 0) Impacts++;
            _velocitySampled = true;
        }

        private const float GroundClearance = .12f, LaunchSpeed = 7, StartPoseLimit = .4f;
        private float _lastLaunchLog = -10;
        private string Describe(int h) => !_grabs[h].Active ? "-" : (_grabs[h].Blade ? "blade" : _grabs[h].Kind.ToString()) +
            (_grabs[h].AxialLock ? "/locked" : "") + "@" + _grabs[h].Bone;
        private void ReachTargets()
        {
            for (int h = 0; h < _grabBody.Length; h++)
            {
                int grabbed = _grabBody[h];
                if (grabbed < 0 || grabbed >= _drive.Count || !_grabs[h].Active) continue;
                if (_limpHandle[h])
                {
                    // The limp arm reaches by its joints; the body it hangs
                    // from leans after it by share, as for any held part.
                    Vec3 pull = _grabTarget[h] - _position[grabbed];
                    if (!InteractionMath.IsFinite(pull)) continue;
                    int above = _drive.Parent(_limpTop[h]);
                    for (int level = 1; level < ReachShare.Length && above >= 0 && above != _pelvisBody; level++, above = _drive.Parent(above))
                        _target[above] += pull * ReachShare[level];
                    continue;
                }
                // A standing body is not levered by the blade: its pins carried
                // the whole body round with the blade (launched at 8.8 m/s in the
                // logs); the B&S hand turns the weapon in the grip instead.
                if (_grabs[h].Blade && _cycle.Phase != ActiveRagdollPhase.Standing) LeverPart(h, grabbed);
                Vec3 delta = _grabTarget[h] - _target[grabbed];
                if (!InteractionMath.IsFinite(delta)) continue;
                // The animated pose keeps resisting along a blade: in B&S the
                // creature's pins brake the part sliding on it, the blade
                // only carries it sideways.
                if (_grabs[h].Blade && (!_grabs[h].AxialLock || InteractionMath.Dot(delta, _grabs[h].Axis) < 0))
                    delta -= _grabs[h].Axis * InteractionMath.Dot(delta, _grabs[h].Axis);
                // A blade joint is rigid; a hand grip reaches by the handle's
                // IK weight, so a body grabbed by the torso or head keeps most
                // of its pose instead of bending wholesale towards the hand.
                if (!_grabs[h].Blade) delta *= RagdollGrabRules.IkWeight(_grabs[h].Kind);
                for (int i = 0; i < _drive.Count; i++)
                    if (i != _pelvisBody && _drive.InSubtree(i, grabbed)) _target[i] += delta;
                int up = _drive.Parent(grabbed);
                for (int level = 1; level < ReachShare.Length && up >= 0 && up != _pelvisBody; level++, up = _drive.Parent(up))
                    _target[up] += delta * ReachShare[level];
            }
        }

        // The blade turned past the joint's angular limit relative to the
        // part's posed direction: the part's pose, its subtree and (by
        // share) its parents turn with it about the entry point, as the
        // locked joint levers the part in B&S.
        private void LeverPart(int h, int grabbed)
        {
            GrabSlot slot = _grabs[h];
            if (slot.LocalAxis.LengthSquared < .5f || !InteractionMath.IsFinite(slot.Axis)) return;
            MatrixFrame posed = _root.TransformToParent(_captured[slot.Bone]);
            Vec3 partAxis = posed.rotation.TransformToParent(slot.LocalAxis);
            if (partAxis.Length < 1e-4f) return;
            partAxis.Normalize();
            float cos = Math.Max(-1, Math.Min(1, InteractionMath.Dot(partAxis, slot.Axis)));
            float angle = (float)Math.Acos(cos) - slot.AngleLimit;
            if (angle <= 1e-3f) return;
            // The pose is levered at most this far: past it the pins would
            // swing the limbs round faster than the body can follow.
            angle = Math.Min(angle, MaxLever);
            Vec3 turnAxis = Cross(partAxis, slot.Axis);
            if (turnAxis.Length < 1e-5f) return;
            turnAxis.Normalize();
            Vec3 pivot = _target[grabbed] + (_hand[h] - _position[grabbed]);
            for (int i = 0; i < _drive.Count; i++)
                if (i != _pelvisBody && _drive.InSubtree(i, grabbed))
                    _target[i] = pivot + Rotate(_target[i] - pivot, turnAxis, angle);
            // Only the pierced part and what hangs from it turn; its parents
            // (and so the whole body) are left to the joints.
        }
        private const float MaxLever = .3f;

        // Damager.Penetrate locks the joint's twist and limits its tilt to
        // penetrationShortDepthAngle, closing to 0 at penetrationShortDepth:
        // the pierced part turns with the weapon, and the weapon with the
        // player's hand by the grip's slerp drive. The blade's turn past the
        // limit drives the part round with that drive's torque. Without it
        // the shown weapon stayed at the part's angle whatever the hand did.
        private void ApplyPierceTorque(float dt)
        {
            for (int h = 0; h < _grabs.Length; h++)
            {
                int b = _grabBody[h];
                // An inert body has nothing to turn it against: the torque on
                // one part only wrung its joints.
                if (Dying || !_grabs[h].Active || !_grabs[h].Blade || b < 0 || b >= _drive.Count) continue;
                Vec3 now = _pierceAxis[h], want = _grabs[h].Axis;
                if (now.LengthSquared < .5f || !InteractionMath.IsFinite(want) || want.Length < .5f) continue;
                want = want.NormalizedCopy();
                float cos = Math.Max(-1, Math.Min(1, InteractionMath.Dot(now, want)));
                float excess = (float)Math.Acos(cos) - _grabs[h].AngleLimit;
                Vec3 turn = Cross(now, want);
                Vec3 error = excess > 0 && turn.Length > 1e-5f ? turn.NormalizedCopy() * excess : Vec3.Zero;
                Vec3 omega = _rotationSampled && dt > 0
                    ? RotationPin.Error(_ps[b], _pf[b], _pu[b], _rs[b], _rf[b], _ru[b]) * (1 / dt) : Vec3.Zero;
                if (error.LengthSquared == 0 && omega.LengthSquared < 1e-6f) continue;
                // The arm hanging limp from it turns with it: inertia of the
                // spread bodies, as for the position grip.
                float mass = _drive.Mass(b);
                if (_limpHandle[h])
                    for (int i = 0; i < _drive.Count; i++) if (_drive.PartScale(i) <= 0 && _drive.InSubtree(i, b)) mass += i == b ? 0 : _drive.Mass(i);
                float inertia = Math.Max(1e-3f, mass * RotationPin.GyrationSquared);
                float k = Math.Min(GripRotationSpring / inertia, RagdollPinDrive.MaxNaturalFrequency * RagdollPinDrive.MaxNaturalFrequency);
                float c = Math.Min(GripRotationDamper / inertia, RagdollPinDrive.MaxDampingRate);
                // Damped only while the limit is passed: inside it the joint
                // lets the part tilt freely.
                Vec3 a = error * k - (excess > 0 ? omega * c : Vec3.Zero);
                a = InteractionMath.ClampMagnitude(a, GripMaxTorque / inertia);
                if (!InteractionMath.IsFinite(a) || a.LengthSquared == 0) continue;
                int t = _bone[b] * 3;
                _torques[t] += a.x; _torques[t + 1] += a.y; _torques[t + 2] += a.z;
            }
        }

        // HandleRagdoll.OnGrab on a wrist or hand (Interactable_HumanWrist /
        // HumanHand: releaseArmSpring 0, releaseArmDamper 0): the held arm's
        // pins go to zero and it follows the grip through its joints; the
        // rest of the body keeps its pins. A blade through the arm holds it
        // the same way. Pinned to its pose, a pierced forearm lifted by the
        // blade was pulled back by the arm's pins, its targets shifted while
        // the elbow could not follow: the arm bucked and the shown hand lagged
        // 57 cm. ResetPinForce on release: the scales go back to 1.
        private void ReleaseHeldArms()
        {
            for (int i = 0; i < _drive.Count; i++) _drive.SetPartScale(i, 1);
            for (int h = 0; h < _grabs.Length; h++)
            {
                _limpHandle[h] = false;
                _drive.ClearSpread(h);
                int grabbed = _grabBody[h];
                // A dying body hangs on the blade whole: the grip carries it all,
                // its own part and the chain above most. On the pierced part
                // alone (2000 N on 15 kg of a limp 100 kg body) the joints
                // stretched and the body broke apart on the blade.
                // Every part is carried in proportion to its mass, as gravity
                // pulls it: with the legs and the other arm carried at half,
                // the body turned about the blade until its centre of mass
                // hung under it, forwards or backwards with where the weapon
                // and the shield weighed.
                if (Dying && _grabs[h].Active && _grabs[h].Blade && grabbed >= 0 && grabbed < _drive.Count)
                {
                    for (int i = 0; i < _drive.Count; i++) _drive.SetSpread(h, i, 1);
                    _drive.SetCarry(h, true);
                    continue;
                }
                if (!_grabs[h].Active || grabbed < 0 || grabbed >= _armBody.Length || !_armBody[grabbed]) continue;
                if (!_grabs[h].Blade && _grabs[h].Kind != GrabHandle.Wrist && _grabs[h].Kind != GrabHandle.Hand) continue;
                int top = grabbed;
                while (_drive.Parent(top) >= 0 && _armBody[_drive.Parent(top)]) top = _drive.Parent(top);
                for (int i = 0; i < _drive.Count; i++)
                    if (_armBody[i] && _drive.InSubtree(i, top))
                    {
                        _drive.SetPartScale(i, 0);
                        // The pierced part and what hangs from it go with the
                        // blade; the arm above it turns about the shoulder,
                        // moving half as far.
                        _drive.SetSpread(h, i, _drive.InSubtree(i, grabbed) ? 1 : .5f);
                    }
                _limpHandle[h] = true; _limpTop[h] = top;
            }
        }
        private static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        // Rodrigues rotation of v about unit axis k.
        private static Vec3 Rotate(Vec3 v, Vec3 k, float angle)
        {
            float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
            return v * c + Cross(k, v) * s + k * (InteractionMath.Dot(k, v) * (1 - c));
        }

        private void UpdateHandles(Skeleton skeleton, MatrixFrame frame, float now)
        {
            for (int h = 0; h < _grabs.Length; h++)
            {
                if (!_grabs[h].Active) { _drive.ClearHandle(h); _grabBody[h] = -1; continue; }
                sbyte bone = _grabs[h].Bone;
                if (bone < 0 || bone >= _boneCount) { ReleaseHandle(h); _ungrab[h] = true; continue; }
                int body = _bodyOfBone[bone];
                MatrixFrame boneWorld = frame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(bone));
                Vec3 contact = boneWorld.TransformToParent(_grabs[h].Local);
                _grabBody[h] = body;
                _pierceAxis[h] = Vec3.Zero;
                if (_grabs[h].Blade && _grabs[h].LocalAxis.LengthSquared > .5f)
                {
                    Vec3 axisNow = boneWorld.rotation.TransformToParent(_grabs[h].LocalAxis);
                    if (axisNow.Length > 1e-4f) _pierceAxis[h] = axisNow.NormalizedCopy();
                }
                _grabTarget[h] = _position[body] + (_hand[h] - contact);
                // The B&S weapon collides with the ground; the controller's
                // blade does not, and dragged a pierced body into the terrain.
                if (_grabs[h].Blade && _nearGround && _grabTarget[h].z < _groundPoint.z + GroundClearance)
                    _grabTarget[h] = new Vec3(_grabTarget[h].x, _grabTarget[h].y, _groundPoint.z + GroundClearance);
                _drive.SetHandle(h, body, _grabTarget[h], _grabs[h].Blade && !_grabs[h].AxialLock ? _grabs[h].Axis : Vec3.Zero);
                // At the hilt the part is only pushed towards the tip (off the blade).
                _drive.SetPushOnly(h, _grabs[h].Blade && _grabs[h].AxialLock ? _grabs[h].Axis : Vec3.Zero);
                _lastInteraction = now;
                if (EnemyPhysicsSettings.Trace && !_grabs[h].Blade && now >= _nextGrabTrace)
                {
                    _nextGrabTrace = now + .2f;
                    float lowestFoot = float.MaxValue, highest = float.MinValue;
                    for (int i = 0; i < _drive.Count; i++) { lowestFoot = Math.Min(lowestFoot, _position[i].z); highest = Math.Max(highest, _position[i].z); }
                    PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "GRAB_TRACE hand={0} kind={1} bone={2} body={3} phase={4} pose_error={5:F3} hand_gap={6:F3} pelvis_off={7:F3} pelvis_z={8:F3} pelvis_target_z={9:F3} lowest_z={10:F3} ground_z={11:F3} near_ground={12} root_moved={13:F3} turned={14:F2}",
                        h, _grabs[h].Kind, bone, body, _cycle.Phase, LastPoseError, (_hand[h] - contact).Length,
                        (_position[_pelvisBody] - _target[_pelvisBody]).Length, _position[_pelvisBody].z, _target[_pelvisBody].z,
                        lowestFoot, _groundPoint.z, _nearGround, (_root.origin - _capturedRoot.origin).AsVec2.Length, _turned));
                }
                // HandleRagdollData.moveStep is a grab handle's; a creature
                // with a blade in it does not step after its body. Stepping the
                // pose root here let the body slide down the blade to the hilt
                // on a slope, the pose following it.
                if (_grabs[h].Blade) continue;
                // HandleRagdoll lift and head rules apply while the creature is
                // alive and standing; a destabilized body simply hangs.
                if (_cycle.Phase != ActiveRagdollPhase.Standing && _cycle.Phase != ActiveRagdollPhase.StandingUp) continue;
                GrabHandle kind = _grabs[h].Kind;
                int reference = RagdollGrabRules.LiftsSelf(kind) ? body : _pelvisBody;
                float groundZ = _root.origin.z;
                float referenceHeight = _position[reference].z - groundZ;
                float headHeight = _headBody >= 0 ? _position[_headBody].z - groundZ : float.MaxValue;
                if (!_grabs[h].HasBase)
                {
                    GrabSlot slot = _grabs[h];
                    slot.HasBase = true; slot.BaseReference = referenceHeight; slot.BaseRoot = _position[_pelvisBody].z - groundZ;
                    _grabs[h] = slot;
                }
                float referenceOriginal = _grabs[h].BaseReference;
                float rootOriginal = _grabs[h].BaseRoot;
                GrabOutcome outcome = RagdollGrabRules.Evaluate(kind, referenceHeight, referenceOriginal, headHeight, rootOriginal);
                if (outcome == GrabOutcome.Hold) { StepTowards(kind, body, now); continue; }
                LastGrabOutcome = outcome;
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "ACTIVE_RAGDOLL grab outcome={0} handle={1} reference_height={2:F3} original={3:F3} head={4:F3} time={5:F3}",
                    outcome, kind, referenceHeight, referenceOriginal, headHeight, now));
                if (outcome == GrabOutcome.Destabilize) _cycle.Destabilize();
                else { ReleaseHandle(h); _ungrab[h] = true; }
            }
        }

        // The pose root walks after the grabbed body, so pulling a standing
        // enemy drags it along; release then moves the agent under its hips.
        private float _lastStep = -1;
        private void StepTowards(GrabHandle kind, int grabbedBody, float now)
        {
            float dt = _lastStep < 0 ? 0 : now - _lastStep;
            _lastStep = now;
            if (_cycle.Phase != ActiveRagdollPhase.Standing || dt <= 0) return;
            int reference = RagdollGrabRules.StepsUnderHead(kind) && _headBody >= 0 ? _headBody : _pelvisBody;
            Vec3 targetNow = _root.TransformToParent(_captured[_bone[reference]]).origin;
            Vec2 offset = (_position[reference] - targetNow).AsVec2;
            Vec2 step = RagdollGrabRules.Step(kind, offset, dt);
            _root.origin = new Vec3(_root.origin.x + step.x, _root.origin.y + step.y, _root.origin.z);
        }

        // HandleRagdoll bodyTurnDirection (Interactable_Human*.json): torso
        // GrabberPosition, shoulder/head/neck PartDirection, hand/wrist/legs
        // None. The creature yaws its root towards the grabber at
        // angle * turnSpeed per second (Creature.turnSpeed 6).
        public const float TurnSpeed = 6, MaxTurnRate = 2;
        private float _lastTurn = -1, _nextGrabTrace;
        private float _turned;
        private void TurnBody(Agent grabber, float now)
        {
            float dt = _lastTurn < 0 ? 0 : Math.Min(.1f, now - _lastTurn);
            _lastTurn = now;
            if (grabber == null || dt <= 0 || _cycle.Phase != ActiveRagdollPhase.Standing) return;
            for (int h = 0; h < _grabs.Length; h++)
            {
                GrabSlot slot = _grabs[h];
                if (!slot.Active || slot.Blade) continue;
                bool grabberPosition = slot.Kind == GrabHandle.Torso;
                if (!grabberPosition && slot.Kind != GrabHandle.Shoulder && slot.Kind != GrabHandle.Head && slot.Kind != GrabHandle.Neck) continue;
                // Creature.transform turns under its hips; a dragged Bannerlord
                // pose root can sit away from them, so pivot on the hips.
                Vec3 pivot = _position[_pelvisBody];
                // Where the creature faces, not the visual entity's f axis
                // (which turned the body towards a wrong heading: 2.4 rad in
                // one grab in the logs).
                Vec2 forward = Facing().AsVec2;
                Vec2 to = (grabber.Position - pivot).AsVec2;
                if (forward.Length < .01f || to.Length < .3f) continue;
                forward.Normalize(); to.Normalize();
                bool front = forward.x * to.x + forward.y * to.y > 0;
                if (!slot.HasFacing) { slot.HasFacing = true; slot.BackGrab = !front; _grabs[h] = slot; }
                // Decided when the hand closed: re-deciding each frame flipped
                // the target 180 degrees whenever the grabber stood beside the
                // body and made it spin back and forth.
                bool back = slot.BackGrab;
                if (back) forward = new Vec2(-forward.x, -forward.y);
                float angle = (float)Math.Atan2(forward.x * to.y - forward.y * to.x, forward.x * to.x + forward.y * to.y);
                float turn = angle * Math.Min(1, TurnSpeed * dt);
                // The pins follow a turning pose at a limited rate; faster
                // turns left the body twisting behind its targets.
                turn = Math.Max(-MaxTurnRate * dt, Math.Min(MaxTurnRate * dt, turn));
                if (Math.Abs(turn) < 1e-4f) continue;
                _root.rotation.s = RotateYaw(_root.rotation.s, turn);
                _root.rotation.f = RotateYaw(_root.rotation.f, turn);
                Vec3 arm = RotateYaw(_root.origin - pivot, turn);
                _root.origin = new Vec3(pivot.x + arm.x, pivot.y + arm.y, _root.origin.z);
                _turned += Math.Abs(turn);
            }
        }
        private static Vec3 RotateYaw(Vec3 v, float angle)
        {
            float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
            return new Vec3(v.x * c - v.y * s, v.x * s + v.y * c, v.z);
        }
        public bool Turned => _configured && _turned > .1f;
        public Vec3 RecoveryForward => Facing();
        // The agent's look direction at capture, carried by the pose root.
        private Vec3 _facingLocal = new Vec3(0, 1, 0);
        private Vec3 Facing()
        {
            Vec3 f = _root.rotation.TransformToParent(_facingLocal); f.z = 0;
            return f.Length > 1e-4f ? f.NormalizedCopy() : _root.rotation.f;
        }

        // Ragdoll.SetBodyPositionToHips: the stand-up targets the pose placed
        // under the current hips, on the measured ground, original facing.
        private void ReanchorUnderHips()
        {
            int pelvisBone = _bone[_pelvisBody];
            Vec3 local = _root.rotation.TransformToParent(_captured[pelvisBone].origin);
            Vec3 pelvis = _lastPelvis;
            Vec3 origin = new Vec3(pelvis.x - local.x, pelvis.y - local.y, _nearGround ? _groundPoint.z : _root.origin.z);
            if (InteractionMath.IsFinite(origin)) _root.origin = origin;
        }

        // Ready to hand the body back to animation without a visible snap.
        public bool ReadyToRelease => _configured && LastPoseError < .25f && !Grabbed &&
            (_cycle.Phase == ActiveRagdollPhase.Recovered ||
             (_cycle.Phase == ActiveRagdollPhase.Standing && _lastRootSample - _lastInteraction > HoldSeconds) ||
             (_cycle.Phase == ActiveRagdollPhase.Standing && !_touched && _hitHold > 0 && _hitBone < 0 &&
              _lastRootSample - _lastInteraction > _hitHold && LastPoseError < HitReleaseError && RootSpeed < HitReleaseSpeed));
        // Untouched long after physicEnabledDuration yet still off its pose
        // (an arm caught in the body, a turned pose the pins could not reach):
        // B&S returns parts to animation after the duration regardless. Close
        // poses are handed back, far ones fall and get up.
        public bool StuckOffPose => _configured && !Grabbed && _cycle.Phase == ActiveRagdollPhase.Standing &&
            _lastRootSample - _lastInteraction > HoldSeconds + 1 && LastPoseError >= .25f;
        public bool WantsHold => _configured && _cycle.Phase != ActiveRagdollPhase.Off && _cycle.Phase != ActiveRagdollPhase.Recovered;
    }
}
