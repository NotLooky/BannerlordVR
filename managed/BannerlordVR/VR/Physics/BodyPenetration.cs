using System;
using BannerlordVR.VR;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Keeps a pierced body's contact attached laterally to the blade while
    // permitting axial withdrawal. The real engine still solves the ragdoll.
    public sealed class BodyPenetration
    {
        public EnemyBody Body { get; private set; }
        public int HandIndex;
        public HandRole Role;
        // Bannerlord ItemTiers Tier1..Tier6 are B&S item tiers 0..5.
        public static int TierOf(ItemObject item) => item == null ? 2 : Math.Max(0, Math.Min(6, (int)item.Tier));
        // True while the blade holds the part through the active ragdoll's
        // hand joint (rigid across the blade) rather than a soft spring.
        public bool Locked { get; private set; }
        private sbyte _bone;
        private Vec3 _localContact, _lastBladePoint;
        private bool _sampled;
        private float _age, _lastDepth, _mass = 5;
        private PenetrationProfile _profile = PenetrationProfile.SwordPierce;
        private PenetrationMaterial _material = PenetrationMaterial.Flesh;
        private readonly PhysicsStepGate _gate = new PhysicsStepGate();
        private Agent _source;
        private BodyZone _zone = BodyZone.Torso;
        private BoneBodyPartType _part;
        private bool _deepDone, _skewerDone;
        public static BodyZone ZoneFor(EnemyBody body, BodyPoint point)
        {
            string name = body.Skeleton.GetBoneName(point.Bone) ?? "";
            switch (point.Part)
            {
                case BoneBodyPartType.Head: return BodyZone.Head;
                case BoneBodyPartType.Neck: return BodyZone.Neck;
                case BoneBodyPartType.ArmLeft:
                case BoneBodyPartType.ArmRight: return name.EndsWith("hand") ? BodyZone.Hand : BodyZone.Arm;
                case BoneBodyPartType.Legs: return name.Contains("foot") ? BodyZone.Foot : BodyZone.Leg;
                default: return BodyZone.Torso;
            }
        }
        // Magnitude of the axial braking force, for the controller haptics.
        public float LastResistance { get; private set; }
        // UpdatePenetration haptics: a short pulse each time the depth moves
        // by gameData.haptics.penetrationPeriod (1 cm), at penetrationIntensity.
        public const float RumblePeriod = .01f, RumbleIntensity = .9f;
        private float _rumbleDepth;
        public bool RumbleDue()
        {
            if (Body == null || Math.Abs(_lastDepth - _rumbleDepth) <= RumblePeriod) return false;
            _rumbleDepth = _lastDepth;
            return true;
        }

        // Blade & Sorcery damager profile closest to the Bannerlord weapon.
        public static bool ProfileFor(WeaponComponentData usage, bool thrust, out PenetrationProfile profile)
        {
            profile = PenetrationProfile.SwordPierce;
            if (usage == null) return false;
            switch (usage.WeaponClass)
            {
                case WeaponClass.Dagger: profile = thrust ? PenetrationProfile.DaggerPierce : PenetrationProfile.DaggerSlash; return true;
                case WeaponClass.Pick: profile = PenetrationProfile.WarpickPierce; return thrust || usage.SwingDamageType == DamageTypes.Pierce;
                case WeaponClass.OneHandedAxe: profile = PenetrationProfile.AxeSlash1H; return !thrust;
                case WeaponClass.TwoHandedAxe:
                case WeaponClass.LowGripPolearm: profile = PenetrationProfile.AxeSlash2H; return !thrust;
                case WeaponClass.Arrow:
                case WeaponClass.Bolt: profile = PenetrationProfile.Arrow; return true;
                default:
                    if (thrust) { profile = PenetrationProfile.SwordPierce; return usage.ThrustDamageType == DamageTypes.Pierce || usage.ThrustDamageType == DamageTypes.Cut; }
                    // Damager_SwordSlash1H has SaberSlash's joint and damage.
                    profile = PenetrationProfile.SaberSlash;
                    return EnemyPhysicsSettings.SlashCuts && usage.SwingDamage > 0 && usage.SwingDamageType == DamageTypes.Cut;
            }
        }

        // B&S looks up the collider's physic material; Bannerlord armour has a
        // material per equipment piece covering the hit part.
        public static PenetrationMaterial MaterialFor(Agent agent, BoneBodyPartType part)
        {
            EquipmentIndex slot;
            switch (part)
            {
                case BoneBodyPartType.Head:
                case BoneBodyPartType.Neck: slot = EquipmentIndex.Head; break;
                case BoneBodyPartType.ArmLeft:
                case BoneBodyPartType.ArmRight: slot = EquipmentIndex.Gloves; break;
                case BoneBodyPartType.Legs: slot = EquipmentIndex.Leg; break;
                default: slot = EquipmentIndex.Body; break;
            }
            ArmorComponent armor = agent?.SpawnEquipment[slot].Item?.ArmorComponent;
            switch (armor?.MaterialType ?? ArmorComponent.ArmorMaterialTypes.None)
            {
                case ArmorComponent.ArmorMaterialTypes.Leather: return PenetrationMaterial.Leather;
                case ArmorComponent.ArmorMaterialTypes.Chainmail: return PenetrationMaterial.Chainmail;
                case ArmorComponent.ArmorMaterialTypes.Plate: return PenetrationMaterial.Plate;
                default: return PenetrationMaterial.Flesh;
            }
        }

        // pressure: Damager.TryHitByPressure, a slow push along the blade that
        // enters without impact damage and without the minimum speed.
        public void Begin(EnemyBody body, BodyPoint point, Vec3 contact, PenetrationProfile profile, float speed,
            Agent source = null, bool pressure = false, int tier = 2, Vec3 velocity = default(Vec3))
        {
            PenetrationMaterial material = body.IsCorpse ? PenetrationMaterial.Flesh : MaterialFor(body.Agent, point.Part);
            PenetrationModifiers.Row row = PenetrationModifiers.Get(profile, material, tier);
            // Damager.CheckPenetration: the material row must allow it, a
            // pressure entry needs pressureAllowed, an impact the min speed.
            string refused = !row.Allow ? "material" : pressure ? (row.Pressure ? null : "no_pressure") :
                speed < row.MinVelocity ? "speed" : null;
            if (refused != null)
            {
                if (EnemyPhysicsSettings.Trace)
                    PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "PENETRATION refused reason={0} speed={1:F2} min={2:F2} material={3} tier={4}", refused, speed, row.MinVelocity, material, tier));
                return;
            }
            Begin(body, point, contact);
            if (Body == null) return;
            _profile = profile; _material = material; _tier = tier;
            // penetrationVelocityMaxDepth: a pressure entry has no impact
            // speed and stops at the curve's start.
            _depthLimit = row.DepthLimit(pressure ? row.MinVelocity : speed);
            _source = source; _zone = ZoneFor(body, point); _part = point.Part;
            _cut = !PenetrationJoint.Get(profile).Pierce; _entryVelocity = velocity; _hasCutNormal = false; _cutBased = false; _lastAlong = float.NaN;
            if (!body.IsCorpse) BloodFx.Burst(BloodFx.Kind.Enter, contact, Vec3.Up);
            if (!_cut) WoundDecal.Stab(body.Agent, point.Bone, contact, velocity);
            Perf.Mark("entry_blood");
            _mass = EnemyPhysicsRuntime.PartMass(body, point.Bone);
            _maxDepth = 0; _maxSeparation = 0; _maxAxial = 0; _lockedLogged = false; _nextTrace = 0;
            // BrainModuleEquipment: a pierce into an arm or hand disarms that hand.
            if (!body.IsCorpse && (profile == PenetrationProfile.DaggerPierce || profile == PenetrationProfile.SwordPierce ||
                    profile == PenetrationProfile.WarpickPierce || profile == PenetrationProfile.Arrow))
                Disarm.ArmStab(body.Agent, point.Bone, point.Part, body.Skeleton.GetBoneName(point.Bone));
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "PENETRATION begin profile={0} zone={1} material={2} tier={3} speed={4:F2} pressure={5} bone={6} depth_limit={7:F3} damper_x={8:F2}",
                    profile, _zone, material, tier, speed, pressure, point.Bone, _depthLimit, row.DamperMultiplier));
            Perf.Mark("penetration");
        }

        // Tales of Glory's stab (TogStab): the entry test was passed; a vital
        // part dies on the blade (gone limp now, killed 1 s on unless pulled
        // out first), a limb takes kinetic/20 x modifier at once.
        private bool _tog;
        private float _togDeathAt = -1;
        public void BeginTog(EnemyBody body, BodyPoint point, Vec3 contact, PenetrationProfile profile, Agent source, int tier,
            Vec3 velocity, float kinetic, float damageModifier)
        {
            Begin(body, point, contact);
            if (Body == null) return;
            _tog = true; _togDeathAt = -1;
            PenetrationMaterial material = MaterialFor(body.Agent, point.Part);
            _profile = profile; _material = material; _tier = tier; _depthLimit = float.PositiveInfinity;
            _source = source; _zone = ZoneFor(body, point); _part = point.Part;
            _cut = false; _entryVelocity = velocity; _hasCutNormal = false; _cutBased = false; _lastAlong = float.NaN;
            BloodFx.Burst(BloodFx.Kind.Enter, contact, Vec3.Up);
            WoundDecal.Stab(body.Agent, point.Bone, contact, velocity);
            _mass = EnemyPhysicsRuntime.PartMass(body, point.Bone);
            _maxDepth = 0; _maxSeparation = 0; _maxAxial = 0; _lockedLogged = false; _nextTrace = 0;
            Disarm.ArmStab(body.Agent, point.Bone, point.Part, body.Skeleton.GetBoneName(point.Bone));
            string effect;
            if (TogStab.Lethal(point.Part))
            {
                _pending = body.Agent.Health + 1; _pendingVictim = body.Agent; _lastAnchor = contact;
                EnemyPhysicsRuntime.MarkFading(body);
                _togDeathAt = TogStab.DeathDelay;
                effect = "lethal";
            }
            else
            {
                float damage = Math.Min(kinetic, TogStab.LimbKineticCap) / 20 * damageModifier;
                float lost = PhysicalHit.ApplyPenetration(source, body.Agent, point.Bone, point.Part, contact, damage, DamageTypes.Pierce);
                if (lost > 0) RecordLimbWound(lost);
                effect = "limb damage=" + damage.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "PENETRATION begin model=tog profile={0} zone={1} material={2} kinetic={3:F1} J {4}", profile, _zone, material, kinetic, effect));
        }
        private void RecordLimbWound(float lost)
        {
            if (Body != null && Body.Agent.IsActive()) { _woundLost += lost; _woundBody = Body; }
        }

        // An axe that bites (B&S Damager_SlashAxe penetrating, TOG CutJoint:
        // every axis limited, broken by a strong pull): the shown axe stays
        // fixed in the part where it struck; pulling on it drags the part;
        // it comes out when the controller is drawn BitePull from the axe
        // shown, or with the victim's death.
        // TOG AllowCutJointBreak: the joint cannot break for 0.2 s; the
        // swing's follow-through left the hand 0.3-0.6 m past the axe and it
        // came out in 0.04 s. After that it comes out drawn BitePull further
        // than the hand has been from it since.
        public const float BitePull = .3f, BiteGrace = .25f, BiteMaxPull = 1.2f;
        private float _biteNearest;
        private bool _bite;
        private MatrixFrame _biteLocal, _biteHand;
        public bool Biting => Body != null && _bite;
        public void BeginBite(EnemyBody body, BodyPoint point, Vec3 contact, PenetrationProfile profile, Agent source, int tier,
            MatrixFrame shownHand, MatrixFrame rawHand)
        {
            Begin(body, point, contact);
            if (Body == null) return;
            _bite = true; _biteNearest = float.MaxValue;
            _profile = profile; _material = MaterialFor(body.Agent, point.Part); _tier = tier; _source = source;
            _zone = ZoneFor(body, point); _part = point.Part;
            MatrixFrame bone = body.Frame.TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(_bone));
            _biteLocal = bone.TransformToLocalNonOrthogonal(shownHand);
            // Where on the controller's hand the axe meets the part.
            _biteHand = rawHand.TransformToLocalNonOrthogonal(new MatrixFrame(Mat3.Identity, contact));
            _maxDepth = 0; _maxSeparation = 0;
            BloodFx.Burst(BloodFx.Kind.Enter, contact, Vec3.Up);
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info("PENETRATION begin model=bite profile=" + profile + " zone=" + _zone + " material=" + _material + " part=" + point.Part);
        }
        private bool TickBite()
        {
            BodyPoint p = Body.ReadContact(_bone);
            MatrixFrame bone = Body.Frame.TransformToParent(Body.Skeleton.GetBoneEntitialFrameWithIndex(p.Bone));
            MatrixFrame shown = bone.TransformToParent(_biteLocal);
            if (!PhysicalBlade.RawHand(Role, out MatrixFrame raw)) { End("no_hand"); return false; }
            float pull = (raw.origin - shown.origin).Length;
            _maxSeparation = Math.Max(_maxSeparation, pull);
            float grace = _mounted ? MountedGrace : BiteGrace;
            if (_age >= grace) _biteNearest = Math.Min(_biteNearest, pull);
            if (pull > (_mounted ? MountedMaxPull : BiteMaxPull) || (_age >= grace && pull > _biteNearest + BitePull))
            {
                Vec3 at = bone.TransformToParent(_localContact);
                BloodFx.Burst(BloodFx.Kind.Exit, at, Vec3.Up);
                End("pulled_free", pull);
                return false;
            }
            PhysicalBlade.SetBite(Role, shown);
            _lastAnchor = bone.TransformToParent(_localContact);
            if (_mounted) { if (_pending >= .5f) StopAttack(Body.Agent); return true; }
            EnemyPhysicsRuntime.TouchLiving(Body);
            // The hand pulls the part by the axe (the joint drags it).
            Vec3 target = raw.TransformToParent(_biteHand.origin);
            Vec3 force = InteractionMath.SpringForce(target - _lastAnchor, p.Velocity - _bladeVelocity,
                EnemyPhysicsSettings.Spring, EnemyPhysicsSettings.Damping, EnemyPhysicsSettings.MaxForce * .5f);
            long steps = EnemyPhysicsRuntime.PhysicsSteps;
            if (steps >= 0 && !_gate.CanApply(steps)) return true;
            Body.Agent.ApplyForceOnRagdoll(p.Bone, in force);
            if (steps >= 0) _gate.Applied(EnemyPhysicsRuntime.PhysicsSteps);
            return true;
        }

        // A stab into a rider: no ragdoll starts in the saddle, so no joint
        // holds the part. The shown blade stays where it entered and rides
        // with the part, as the axe's bite, until it is drawn out or the
        // horses part. B&S holds a killing stab's victim at one point until
        // the blade leaves (Damager: the damage lands on UnPenetrate).
        private bool _mounted;
        // Victims held at one point: one let go by any way but its blade
        // leaving (a rider stabbed in the chest lived 2 minutes at one point,
        // no end logged) dies as it would have.
        private struct Hold { public Agent Source; public sbyte Bone; public BoneBodyPartType Part; public Vec3 At; }
        private static readonly System.Collections.Generic.Dictionary<Agent, Hold> HeldAtOne = new System.Collections.Generic.Dictionary<Agent, Hold>();
        public static void SweepHolds(Func<Agent, bool> held)
        {
            if (HeldAtOne.Count == 0) return;
            foreach (Agent agent in new System.Collections.Generic.List<Agent>(HeldAtOne.Keys))
            {
                if (agent != null && agent.IsActive() && held(agent)) continue;
                Hold hold = HeldAtOne[agent];
                HeldAtOne.Remove(agent);
                if (agent == null || !agent.IsActive()) continue;
                PhysicalHit.ApplyPenetration(hold.Source, agent, hold.Bone, hold.Part, hold.At, agent.Health + 1, DamageTypes.Pierce);
                PhysicsLog.Info("PENETRATION hold of agent " + agent.Index + " lost without its blade leaving: dealt now, alive=" + agent.IsActive());
            }
        }
        // Two riders crossing part at 5-11 m/s: at the bite's 1.2 m the blade
        // came out 0.17-0.28 s in. A rider dying on it reins in, and the
        // blade holds 2 m of reach and half a second before drawing it out.
        public const float MountedGrace = .5f, MountedMaxPull = 2, DyingRideSpeed = 2;
        private Agent _reined;
        public void BeginMounted(EnemyBody body, BodyPoint point, Vec3 contact, PenetrationProfile profile, Agent source, int tier,
            MatrixFrame shownHand, MatrixFrame rawHand, Vec3 velocity, float damage, bool lethal)
        {
            Begin(body, point, contact, false);
            _bite = true; _mounted = true; _biteNearest = float.MaxValue;
            _profile = profile; _material = MaterialFor(body.Agent, point.Part); _tier = tier; _source = source;
            _zone = ZoneFor(body, point); _part = point.Part; _lastAnchor = contact;
            MatrixFrame bone = body.Frame.TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(_bone));
            _biteLocal = bone.TransformToLocalNonOrthogonal(shownHand);
            _biteHand = rawHand.TransformToLocalNonOrthogonal(new MatrixFrame(Mat3.Identity, contact));
            _maxDepth = 0; _maxSeparation = 0;
            BloodFx.Burst(BloodFx.Kind.Enter, contact, Vec3.Up);
            WoundDecal.Stab(body.Agent, point.Bone, contact, velocity);
            Disarm.ArmStab(body.Agent, point.Bone, point.Part, body.Skeleton.GetBoneName(point.Bone));
            Agent victim = body.Agent;
            string effect;
            if (lethal || damage >= victim.Health - .5f)
            {
                // Down to one point now (the blow's hurt and blood), dead as
                // the blade comes out.
                float now = (float)Math.Floor(victim.Health - 1);
                if (now >= 1) PhysicalHit.ApplyPenetration(source, victim, point.Bone, point.Part, contact, now, DamageTypes.Pierce);
                if (!victim.IsActive()) { Release(); return; }
                _pending = victim.Health + 1; _pendingVictim = victim;
                HeldAtOne[victim] = new Hold { Source = source, Bone = point.Bone, Part = point.Part, At = contact };
                StopAttack(victim);
                _reined = victim.MountAgent;
                try { _reined?.SetMaximumSpeedLimit(DyingRideSpeed, false); } catch (Exception) { _reined = null; }
                effect = "held at " + victim.Health.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " point(s) until withdrawn";
            }
            else
            {
                float lost = damage > 0 ? PhysicalHit.ApplyPenetration(source, victim, point.Bone, point.Part, contact, damage, DamageTypes.Pierce) : 0;
                if (lost > 0) RecordLimbWound(lost);
                effect = "limb damage=" + damage.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info("PENETRATION begin model=mounted profile=" + profile + " zone=" + _zone + " material=" + _material +
                    " part=" + point.Part + " agent=" + victim.Index + ": " + effect);
        }
        // A rider dying on the blade attacks no more.
        private static void StopAttack(Agent agent)
        {
            try
            {
                Agent.ActionStage stage = agent.GetCurrentActionStage(1);
                if (stage == Agent.ActionStage.AttackReady || stage == Agent.ActionStage.AttackQuickReady || stage == Agent.ActionStage.AttackRelease)
                    agent.SetActionChannel(1, ActionIndexCache.act_none, ignorePriority: true, blendInPeriod: .2f);
            }
            catch (Exception) { }
        }

        public void Begin(EnemyBody body, BodyPoint point, Vec3 contact, bool grab = true)
        {
            _tog = false; _togDeathAt = -1; _bite = false; _mounted = false;
            Body = body; _bone = point.Bone; _sampled = false; _anchorSampled = false; _bladeSampled = false; _bladeVelocity = Vec3.Zero; _age = 0; _lastDepth = 0; _rumbleDepth = 0; _effectDepth = 0; LastResistance = 0; _gate.Reset();
            _deepDone = _skewerDone = false; _source = null; _zone = BodyZone.Torso; _part = point.Part; _depthBase = -1; _prevSigned = float.NaN;
            MatrixFrame bone = body.Frame.TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(_bone));
            _localContact = bone.TransformToLocalNonOrthogonal(contact);
            _hasLocalAxis = false; _cut = false;
            if (grab && !body.IsCorpse && !EnemyPhysicsRuntime.BeginLivingGrab(body)) Release();
        }
        // Damage that would kill while the blade is still inside is held back
        // and dealt when it leaves the body: the victim dies on withdrawal.
        private float _pending, _woundLost, _depthBase = -1, _maxDepth, _prevSigned = float.NaN;
        private bool _lockedLogged;
        private float _maxSeparation;
        private const float MaxShove = .1f, LateralLoss = 2.5f, WithdrawDrag = 300, PushInDrag = 300, SlideBrake = 1500, WithdrawMargin = .03f;
        private float _nextTrace;
        private Vec3 _prevAnchor, _prevBladeBase, _prevBladeAxis, _bladeVelocity;
        private bool _anchorSampled, _bladeSampled;
        // STAB every 0.1 s while a blade is in: how deep, how the part and the
        // blade move along it and towards the player, the two damper forces.
        private void Trace(BodyPoint p, float depth, float limit, bool atLimit, bool dying, float bladeAxial, float partAxial,
            float fromBlade, float fromPart, float along)
        {
            if (!EnemyPhysicsSettings.Trace || _age < _nextTrace) return;
            _nextTrace = _age + .1f;
            Agent player = Mission.Current?.MainAgent;
            Vec3 toPlayer = player != null ? player.Position - p.World : Vec3.Zero;
            float distance = toPlayer.AsVec2.Length;
            Vec3 flat = distance > 1e-3f ? new Vec3(toPlayer.x / distance, toPlayer.y / distance, 0) : Vec3.Zero;
            PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "STAB age={0:F2} zone={1} depth={2:F3} limit={3:F2} at_limit={4} dying={5} locked={6} part_speed={7:F2} part_to_player={8:F2} part_up={9:F2} part_axial={10:F2} blade_axial={11:F2} f_blade={12:F0} f_part={13:F0} dist_xy={14:F2} hand_gap={15:F2} along={16:F2}",
                _age, _zone, depth, float.IsInfinity(limit) ? -1 : limit, atLimit, dying, Locked, p.Velocity.Length,
                InteractionMath.Dot(p.Velocity, flat), p.Velocity.z, partAxial, bladeAxial, fromBlade, fromPart, distance,
                PhysicalBlade.Separation(Role), along));
        }
        private float _maxAxial;
        // Slash joint (Damager_SwordSlash1H, DamageModifier_Slash): the edge
        // bites penetrationVelocityMaxDepth (3 mm per m/s over the material's
        // minimum, held to 3 mm) and the blade slides along its length with
        // penetrationSlideDamper 100; drawn back out or slid off the edge it
        // lets go (UpdatePenetration: depth <= 0, or past the edge's ends).
        private bool _cut, _hasCutNormal, _cutBased;
        private Vec3 _entryVelocity, _cutLocal;
        private float _lastAlong;
        private float _effectDepth, _effectAlong;
        private const float EffectRepeat = .06f;
        private const float SlideDamper = 100, CutExit = .015f, CutMaxDepth = .003f, CutThrough = .05f;

        private bool TickCut(BodyPoint p, MatrixFrame bone, Vec3 anchor, Vec3 axis, Vec3 bladeBase, Vec3 bladeTip,
            float bladeLength, float along, Vec3 bladePoint, float dt)
        {
            if (!_hasCutNormal)
            {
                Vec3 n0 = _entryVelocity - axis * InteractionMath.Dot(_entryVelocity, axis);
                if (n0.Length < 1e-3f) { End("no_cut_direction"); return false; }
                n0.Normalize();
                _cutLocal = bone.rotation.TransformToLocal(n0); _hasCutNormal = true;
            }
            Vec3 n = bone.rotation.TransformToParent(_cutLocal);
            n -= axis * InteractionMath.Dot(n, axis);
            if (n.Length < 1e-3f) { End("edge_turned"); return false; }
            n.Normalize();
            Vec3 f = new Vec3(axis.y * n.z - axis.z * n.y, axis.z * n.x - axis.x * n.z, axis.x * n.y - axis.y * n.x);
            // Slid off either end of the edge: the cut is drawn through.
            if (along < -.02f || along > bladeLength + .02f) { End("slid_off", along); return false; }
            float depth = InteractionMath.Dot(bladePoint - anchor, n);
            if (!_cutBased) { _depthBase = depth; _cutBased = true; }
            depth -= _depthBase;
            if (depth < -CutExit) { End("withdrawn", -depth); return false; }
            float limit = Math.Min(CutMaxDepth, float.IsInfinity(_depthLimit) ? CutMaxDepth : _depthLimit);
            // A swing carried on through the body ends the cut: the B&S weapon
            // (a rigidbody) is stopped there or slices; the controller is not.
            if (depth - limit > CutThrough) { End("cut_through", depth - limit); return false; }
            // The part is held to the blade's flat; along the blade and the
            // edge it is free. The swing's momentum went into the hit's push:
            // shoving the part ahead of a controller 30-58 cm past the edge
            // threw the body (0.5 m off its pose, back up 2 m away).
            Vec3 bladeTarget = anchor + f * InteractionMath.Dot(bladePoint - anchor, f);
            _lastAnchor = anchor;
            PhysicalBlade.SetCut(Role, anchor, n, _depthBase + limit, bladeBase, bladeTip);
            _maxDepth = Math.Max(_maxDepth, depth);
            _maxSeparation = Math.Max(_maxSeparation, PhysicalBlade.Separation(Role));
            if (!Body.IsCorpse && depth > 0)
            {
                DeepDamage(anchor, depth);
                if (Body == null) return false;
            }
            Vec3 targetVelocity = _bladeVelocity;
            _lastBladePoint = bladePoint; _sampled = true;
            Vec3 relative = p.Velocity - targetVelocity;
            Locked = !Body.IsCorpse && EnemyPhysicsSettings.ActiveRagdoll &&
                EnemyPhysicsRuntime.PierceActive(Body, HandIndex, p.Bone, bladeTarget, axis, false, Vec3.Zero, 0, bone, anchor);
            Vec3 force = Vec3.Zero;
            if (!Locked)
            {
                Vec3 lateral = relative - axis * InteractionMath.Dot(relative, axis);
                force = InteractionMath.SpringForce(bladeTarget - anchor, lateral,
                    EnemyPhysicsSettings.Spring, EnemyPhysicsSettings.Damping, EnemyPhysicsSettings.MaxForce * .5f);
            }
            // penetrationSlideDamper: the blade drawn along the cut drags the part.
            Vec3 slide = -axis * (InteractionMath.Dot(relative, axis) * SlideDamper * PenetrationModifiers.Get(_profile, _material, _tier).DamperMultiplier);
            force += InteractionMath.ClampMagnitude(slide, WithdrawDrag);
            LastResistance = slide.Length;
            if (!Body.IsCorpse && !float.IsNaN(_lastAlong) && Math.Abs(along - _effectAlong) > EffectRepeat)
            {
                _effectAlong = along;
                BloodFx.Burst(BloodFx.Kind.Inside, anchor, n);
            }
            if (float.IsNaN(_lastAlong)) _effectAlong = along;
            _lastAlong = along;
            _lastDepth = depth;
            long steps = EnemyPhysicsRuntime.PhysicsSteps;
            if (steps >= 0 && !_gate.CanApply(steps)) return true;
            Body.Agent.ApplyForceOnRagdoll(p.Bone, in force);
            if (steps >= 0) _gate.Applied(EnemyPhysicsRuntime.PhysicsSteps);
            return true;
        }
        private int _tier = 2;
        // The blade direction in the part's frame at entry: the joint's
        // angular limits are relative to it.
        private Vec3 _localAxis;
        private bool _hasLocalAxis;
        private float _depthLimit = float.PositiveInfinity;
        private void End(string reason, float value = 0)
        {
            if (EnemyPhysicsSettings.Trace && Body != null)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "PENETRATION end reason={0} value={1:F3} age={2:F2} max_depth={3:F3} locked={4} shown_hand_offset={5:F3} max_offset={6:F3} max_axial_n={7:F0}",
                    reason, value, _age, _maxDepth, Locked, PhysicalBlade.Separation(Role), _maxSeparation, _maxAxial));
            // One spill as the blade leaves a living body (UnPenetrate).
            if (Body != null && !Body.IsCorpse && _pending < .5f && _maxDepth > .02f) BloodFx.Burst(BloodFx.Kind.Exit, _lastAnchor, Vec3.Up);
            // PenetrationDeepBleeding: a neck stab past its deep depth (Neck
            // penetrationPierceDeepDepth 0.02 m) pours once the blade is out.
            if (Body != null && !Body.IsCorpse && _part == BoneBodyPartType.Neck && _maxDepth > NeckDeepDepth)
                WoundFlow.Begin(Body.Agent, _bone, _lastAnchor);
            _ending = true;
            try { Release(); } finally { _ending = false; }
        }
        private bool _ending;
        public const float RideThroughSpeed = 3;
        private const float RideThroughAge = .15f;
        // TOG's anchor follows the part out five times faster than in: the
        // blade drags the part ahead of it going in, slides out nearly free.
        private const float TogPushInDrag = 1500, TogWithdrawDrag = 300f / TogStab.StabOut * TogStab.StabIn;
        private float TogDamper(bool goingIn) => _mass * RagdollPinDrive.MaxDampingRate * (goingIn ? 1 : TogStab.StabIn / TogStab.StabOut);
        private const float NeckDeepDepth = .02f, WholeBodyBrakeMinPartMass = 5, NearOut = .03f, FastOut = 1;
        private EnemyBody _woundBody, _lastWoundBody;
        private Agent _pendingVictim;
        private Vec3 _lastAnchor;
        public float PendingDamage => _pending;

        // Out of the body with nothing dealt (the victim is killed elsewhere).
        public void Abandon() { if (_pendingVictim != null) HeldAtOne.Remove(_pendingVictim); _pending = 0; _pendingVictim = null; _woundLost = 0; _woundBody = null; Release(); }

        public void Release([System.Runtime.CompilerServices.CallerMemberName] string by = null)
        {
            if (Body != null && !_ending && EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "PENETRATION released by {0} age={1:F2} agent={2} held_damage={3:F1}", by, _age, Body.Agent?.Index ?? -1, _pending));
            if (_pendingVictim != null) HeldAtOne.Remove(_pendingVictim);
            if (_reined != null)
            {
                try { if (_reined.IsActive()) _reined.SetMaximumSpeedLimit(-1, false); } catch (Exception) { }
                _reined = null;
            }
            Agent victim = _pendingVictim;
            float pending = _pending, woundLost = _woundLost;
            EnemyBody woundBody = _woundBody;
            _lastWoundBody = Body;
            _pending = 0; _pendingVictim = null; _woundLost = 0; _woundBody = null;
            Agent source = _source;
            DamageTypes woundType = PenetrationJoint.Get(_profile).Pierce ? DamageTypes.Pierce : DamageTypes.Cut;
            if (woundLost > 0 && woundBody?.Agent != null && woundBody.Agent.IsActive() && (pending < .5f || victim == null))
                EnemyPhysicsRuntime.RecordWound(source, woundBody, woundBody.ReadContact(_bone), _lastAnchor, woundType, woundLost, false);
            PhysicalBlade.Clear(Role);
            if (Locked && Body != null) EnemyPhysicsRuntime.ReleaseActiveGrab(Body, HandIndex);
            Locked = false;
            Body = null; _sampled = false; _source = null;
            if (victim == null || pending < .5f || !victim.IsActive()) return;
            DamageTypes type = PenetrationJoint.Get(_profile).Pierce ? DamageTypes.Pierce : DamageTypes.Cut;
            float lost = PhysicalHit.ApplyPenetration(source, victim, _bone, _part, _lastAnchor, pending, type);
            BloodFx.Burst(BloodFx.Kind.Exit, _lastAnchor, Vec3.Up);
            // The survivor bleeds from the wound the blow left.
            if (lost > 0 && victim.IsActive() && woundBody == null && _lastWoundBody != null && _lastWoundBody.Agent == victim)
                EnemyPhysicsRuntime.RecordWound(source, _lastWoundBody, _lastWoundBody.ReadContact(_bone), _lastAnchor, woundType, lost, false);
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "PENETRATION withdrawn profile={0} zone={1} held_damage={2:F1} health_lost={3:F1} alive={4}",
                    _profile, _zone, pending, lost, victim.IsActive()));
        }

        private void DeepDamage(Vec3 anchor, float depth)
        {
            Agent victim = Body.Agent;
            float limit = victim.HealthLimit;
            if (!_deepDone && depth > PenetrationDamage.DeepTrigger(_profile, _zone))
            {
                _deepDone = true;
                Deliver(victim, anchor, PenetrationDamage.DeepDamage(_profile, _zone, limit), "deep");
            }
            if (Body != null && !_skewerDone && PenetrationDamage.SkewerDamage(_profile) > 0 &&
                depth > PenetrationDamage.SkewerDepth(_zone))
            {
                _skewerDone = true;
                Deliver(victim, anchor, PenetrationDamage.SkewerTotal(_profile, _zone, limit), "skewer");
            }
        }

        private void Deliver(Agent victim, Vec3 anchor, float damage, string kind)
        {
            DamageTypes type = PenetrationJoint.Get(_profile).Pierce ? DamageTypes.Pierce : DamageTypes.Cut;
            // Leave at least one point while the blade is in; the rest waits
            // for Release.
            // Bannerlord deals whole points: the cap is too, or 88.6 rounds
            // to 89 and leaves the victim under one point.
            // A stab is one blow: the deep and skewer damage are counted as the
            // blade reaches them and dealt together as it leaves. Each dealt on
            // the spot was a blow of its own (hurt voice, blood), so pushing a
            // stab deeper read as two more hits.
            float now = 0;
            if (damage > 0)
            {
                _pending += damage; _pendingVictim = victim; _lastAnchor = anchor;
                // A stab that will kill leaves the body inert on the blade now.
                if (_pending >= victim.Health - .5f && Body != null && Body.Agent == victim) EnemyPhysicsRuntime.MarkDying(Body);
            }
            float lost = now >= .5f ? PhysicalHit.ApplyPenetration(_source, victim, _bone, _part, anchor, now, type) : 0;
            if (lost > 0) BloodFx.Burst(kind == "skewer" ? BloodFx.Kind.Exit : BloodFx.Kind.Enter, anchor, Vec3.Up);
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "PENETRATION {0} profile={1} zone={2} material={3} damage={4:F1} health_lost={5:F1} held_until_withdrawal={6:F1}",
                    kind, _profile, _zone, _material, damage, lost, _pending));
            // The wound starts bleeding when the blade comes out, so bleeding
            // cannot finish a victim the blade is still holding at 1 point.
            if (lost > 0 && Body != null && Body.Agent == victim) { _woundLost += lost; _woundBody = Body; }
            if (!victim.IsActive()) Release();
        }
        public bool Tick(Vec3 bladeBase, Vec3 bladeTip, float dt)
        {
            if (Body == null) return false;
            EnemyPhysicsRuntime.MaintainLivingGrab(Body);
            _age += dt;
            // No time limit: Damager.UpdatePenetration keeps a blade in as long
            // as it is not pulled out (the 8 s cut ended held stabs).
            if (!Body.IsCorpse && !Body.Agent.IsActive()) { End("victim_inactive"); return false; }
            // Killed at one point by another blow, the body falls off the blade.
            if (_mounted && Body.IsCorpse) { End("victim_killed"); return false; }
            if (_mounted) return TickBite();
            // Let a newly killed target enter the engine's corpse state itself.
            if (!Body.HasNativePhysics)
            {
                if (_age > .4f) End("no_native_physics");
                else if (_bite && PhysicalBlade.RawHand(Role, out _))
                {
                    EnemyPhysicsRuntime.TouchLiving(Body);
                    // Held already while the ragdoll takes over.
                    MatrixFrame bone0 = Body.Frame.TransformToParent(Body.Skeleton.GetBoneEntitialFrameWithIndex(_bone));
                    PhysicalBlade.SetBite(Role, bone0.TransformToParent(_biteLocal));
                }
                return Body != null;
            }
            if (_bite) return TickBite();
            // TOG BreakStabJoint: at a gallop the stab joint cannot hold the
            // body; the blade slides out past its end as the rider passes,
            // the held damage lands and the body falls off it. Held, the arm
            // joint dragged it along at the horse's speed.
            if (_tog && _togDeathAt >= 0 && _age >= _togDeathAt) { End("tog_death"); return false; }
            float riding = EnemyPhysicsRuntime.RiderSpeed(TaleWorlds.MountAndBlade.Mission.Current?.MainAgent);
            if (riding > RideThroughSpeed && _age > RideThroughAge) { End("ridden_through", riding); return false; }
            BodyPoint p = Body.ReadContact(_bone);
            MatrixFrame bone = Body.Frame.TransformToParent(Body.Skeleton.GetBoneEntitialFrameWithIndex(p.Bone));
            Vec3 anchor = bone.TransformToParent(_localContact);
            // The part's speed is the entry point's own, measured tick to tick
            // on the drawn body.
            if (_anchorSampled && dt > 1e-4f) p.Velocity = InteractionMath.ClampMagnitude((anchor - _prevAnchor) * (1 / dt), 15);
            _prevAnchor = anchor; _anchorSampled = true;
            Vec3 axis = bladeTip - bladeBase;
            float bladeLength = axis.Length;
            if (bladeLength < .05f) { End("no_blade"); return false; }
            axis.Normalize();
            // The VR blade is the controller: it cannot be stopped by the
            // joint. Measured on the blade's line, so a hand pushed on past
            // the hilt still holds the part (the B&S weapon would stop there
            // while the arm keeps pushing).
            float along = InteractionMath.Dot(anchor - bladeBase, axis);
            Vec3 bladePoint = bladeBase + axis * along;
            // The blade's own speed at the entry: the same spot on the blade
            // (same distance from the grip) a tick apart. The closest point to
            // the part moves with the part, so measured there the damper
            // compared the part with itself and braked nothing: a limp body
            // slid freely down a sloping blade, away or onto the player.
            _bladeVelocity = _bladeSampled && dt > 1e-4f
                ? InteractionMath.ClampMagnitude((bladePoint - (_prevBladeBase + _prevBladeAxis * along)) * (1 / dt), 12) : Vec3.Zero;
            _prevBladeBase = bladeBase; _prevBladeAxis = axis; _bladeSampled = true;
            // Damager.Penetrate locks x and y and never lets go sideways: only
            // the blade leaving along its axis ends the joint. The VR blade
            // cannot be slowed by the body, so a fast hand leaves the part
            // behind; 16 cm released every stab within 0.2-0.36 s, and 50 cm
            // to 1.1 m every stab into a body lying on the ground (the
            // controller's blade goes through the ground, the B&S weapon
            // cannot). Only a hand teleported away (2.5 m) lets go.
            if ((bladePoint - anchor).Length > LateralLoss)
            { End("slipped_off_blade", (bladePoint - anchor).Length); return false; }
            if (!_tog && !Body.IsCorpse && _pending >= .5f && _pending >= Body.Agent.Health - .5f) EnemyPhysicsRuntime.MarkDying(Body);
            if (_cut) return TickCut(p, bone, anchor, axis, bladeBase, bladeTip, bladeLength, along, bladePoint, dt);
            if (!_hasLocalAxis)
            {
                Vec3 local = bone.rotation.TransformToLocal(axis);
                if (local.Length > 1e-4f) { local.Normalize(); _localAxis = local; _hasLocalAxis = true; }
            }
            // The tip came back out of the entry: the blade is withdrawn.
            if (along > bladeLength + .03f) { End("withdrawn", along - bladeLength); return false; }
            // Depth counts what went in after the entry, not where along the
            // blade the first contact happened to be sampled.
            float rawDepth = Math.Max(0, bladeLength - along);
            if (_depthBase < 0) _depthBase = Math.Min(rawDepth, bladeLength);
            float depth = Math.Max(0, rawDepth - _depthBase);
            // UpdatePenetration: out once the depth from the entry is back to
            // zero. The entry is found on the body's surface with the tip
            // already some 25 cm in, so waiting for the tip itself to come
            // back kept the body glued to the blade's line well clear of it:
            // stabs held 14 s and could not be drawn out.
            float signedDepth = bladeLength - along - _depthBase;
            if ((_maxDepth > .02f || _age > .2f) && signedDepth < -WithdrawMargin) { End("withdrawn", -signedDepth); return false; }
            // Drawn out fast and all but out: a blade 6 mm from its entry
            // and leaving at 2.6 m/s was still in, and the next tick the arm,
            // yanked by the hand, rode up it 73 cm towards the player.
            float leaving = float.IsNaN(_prevSigned) || dt <= 0 ? 0 : (_prevSigned - signedDepth) / dt;
            _prevSigned = signedDepth;
            if (_tog && _maxDepth > TogStab.BreakNear * 2 && signedDepth < TogStab.BreakNear)
            {
                // BreakStabJoint: the part is kicked off towards the hilt at its
                // mass x 5 (an impulse; one PhysX step at 60 Hz).
                Vec3 kick = -axis * Math.Min(EnemyPhysicsSettings.MaxForce, _mass * TogStab.OutKick * 60);
                if (!Body.IsCorpse) Body.Agent.ApplyForceOnRagdoll(p.Bone, in kick);
                End("tog_slid_out", signedDepth); return false;
            }
            if (_maxDepth > .05f && signedDepth < NearOut && leaving > FastOut) { End("withdrawn_fast", leaving); return false; }
            // Joint zMotion Limited at penetrationVelocityMaxDepth, and never
            // past the hilt: beyond it the stopped blade carries the part.
            float limit = Math.Min(_depthLimit, Math.Max(0, bladeLength - .02f - _depthBase));
            bool atLimit = depth > limit;
            Vec3 bladeTarget = bladePoint;
            if (atLimit)
            {
                // The stopped blade stays in while the hand pushes on (B&S:
                // the weapon stays in the body, the arm keeps shoving it);
                // only a hand far beyond any reach has let go of it.
                if (depth - limit > 1.5f) { End("overshoot", depth - limit); return false; }
                // The part belongs where the stopped blade's depth ends, further
                // along the blade (towards the tip). The shove is bounded: past
                // 10 cm the arm joint's pull would launch the body.
                bladeTarget = bladePoint + axis * Math.Min(depth - limit, MaxShove);
                depth = limit;
            }
            _lastAnchor = anchor;
            // The shown weapon and hand stay jointed into the part.
            Vec3 partAxis = Vec3.Zero;
            if (_hasLocalAxis)
            {
                partAxis = bone.rotation.TransformToParent(_localAxis);
                if (partAxis.Length > 1e-4f) partAxis.Normalize(); else partAxis = Vec3.Zero;
            }
            PhysicalBlade.SetPierce(Role, anchor, partAxis, PenetrationJoint.AngleLimit(_profile, depth),
                bladeLength - _depthBase - limit, bladeBase, bladeTip);
            _maxDepth = Math.Max(_maxDepth, depth);
            _maxSeparation = Math.Max(_maxSeparation, PhysicalBlade.Separation(Role));
            if (!Body.IsCorpse)
            {
                EnemyPhysicsRuntime.DeepenWound(Body.Agent, _bone, depth);
                if (!_tog) DeepDamage(anchor, depth);
                if (Body == null) return false;
            }
            Vec3 targetVelocity = _bladeVelocity;
            _lastBladePoint = bladePoint; _sampled = true;
            Vec3 error = bladeTarget - anchor;
            Vec3 relativeVelocity = p.Velocity - targetVelocity;
            // Damager.Penetrate locks x and y: on an active ragdoll the part is
            // carried across the blade by the player's arm joint (5000 N/m,
            // 3000 N), with its pose following. Elsewhere a soft spring.
            Vec3 force = Vec3.Zero;
            Locked = !Body.IsCorpse && EnemyPhysicsSettings.ActiveRagdoll &&
                EnemyPhysicsRuntime.PierceActive(Body, HandIndex, p.Bone, bladeTarget, axis, atLimit,
                    _hasLocalAxis ? _localAxis : Vec3.Zero, PenetrationJoint.AngleLimit(_profile, depth), bone, anchor);
            if (Locked && !_lockedLogged && EnemyPhysicsSettings.Trace)
            { _lockedLogged = true; PhysicsLog.Info("PENETRATION locked to the arm joint age=" + _age.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); }
            if (!Locked)
            {
                Vec3 lateralVelocity = atLimit ? relativeVelocity : relativeVelocity - axis * InteractionMath.Dot(relativeVelocity, axis);
                force = InteractionMath.SpringForce(error, lateralVelocity,
                    EnemyPhysicsSettings.Spring, EnemyPhysicsSettings.Damping, EnemyPhysicsSettings.MaxForce * .5f);
            }
            // B&S axial damper: the part is dragged along with the blade, with
            // a different brake going in and coming out, set by its armour.
            float damper = atLimit ? 0 : _tog ? TogDamper(depth >= _lastDepth) : PenetrationJoint.Damper(_profile, _material, true, depth >= _lastDepth, _tier);
            // The damper acts on the blade's speed into the part and on the
            // part's own speed along the blade. The blade's push is held to
            // 300 N (the controller is never slowed, and the full damper shoved
            // the body back instead of letting the blade in); the part's own
            // slide keeps the full B&S brake, on the whole body once it hangs
            // limp: held to 300 N too, a falling body slid down the blade
            // onto the player.
            bool dying = EnemyPhysicsRuntime.IsDying(Body);
            // The whole body's brake only on a part that can carry it: on an
            // upper arm (2.7 kg) it pulled 1500 N and stretched the arm.
            float braked = dying && _mass >= WholeBodyBrakeMinPartMass ? Math.Max(_mass, EnemyPhysicsRuntime.BodyMass(Body)) : _mass;
            float c = Math.Min(damper, braked * RagdollPinDrive.MaxDampingRate);
            float bladeAxial = InteractionMath.Dot(targetVelocity, axis), partAxial = InteractionMath.Dot(p.Velocity, axis);
            float fromBlade = c * bladeAxial, fromPart = -c * partAxial;
            float drag = _tog ? (depth < _lastDepth ? TogWithdrawDrag : TogPushInDrag) : depth < _lastDepth ? WithdrawDrag : PushInDrag;
            fromBlade = Math.Max(-drag, Math.Min(drag, fromBlade));
            fromPart = Math.Max(-SlideBrake, Math.Min(SlideBrake, fromPart));
            Vec3 axial = axis * (fromBlade + fromPart);
            // Pulling out, the brake drags the part along at up to its mass x
            // 30 N.s/m, about the whole body's weight on a torso: the body rose
            // with the blade. The B&S weapon is held by a 2000 N grip on a
            // heavy creature; here the drag out is held to WithdrawDrag.
            _maxAxial = Math.Max(_maxAxial, axial.Length);
            Trace(p, depth, limit, atLimit, dying, bladeAxial, partAxial, fromBlade, fromPart, along);
            // UpdatePenetration: the penetration effect repeats once per
            // ragdollData.penetrationEffectRepeatDistance (6 cm) of new depth,
            // never on the way out. A burst on every 4 mm both ways (at most
            // each 0.15 s) sprayed blood in volleys all through a stab.
            if (!Body.IsCorpse && depth > _effectDepth + EffectRepeat)
            {
                _effectDepth = depth;
                BloodFx.Burst(BloodFx.Kind.Inside, anchor, -axis);
            }
            _lastDepth = depth;
            LastResistance = axial.Length;
            force += axial;
            long steps = EnemyPhysicsRuntime.PhysicsSteps;
            if (steps >= 0 && !_gate.CanApply(steps)) return true;
            Body.Agent.ApplyForceOnRagdoll(p.Bone, in force);
            if (steps >= 0) _gate.Applied(EnemyPhysicsRuntime.PhysicsSteps);
            return true;
        }
    }
}
