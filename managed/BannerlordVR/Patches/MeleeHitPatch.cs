using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using BannerlordVR.VR;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// TEST 262/263. The seam Mission.MeleeHitCallback: the engine's own
    /// pipeline for every melee collision, agent or world, missile or not - the
    /// one place damage, armor, perks, difficulty, bone multipliers, knockback,
    /// the combat log, the hit sound and OnMeleeHit all happen together. Two
    /// jobs share this one internal prefix because they are the same seam:
    ///
    ///   PROBE (TEST 262) - every real engine melee collision by a human with a
    ///   weapon is a free sample of "where does a contact land on THIS weapon
    ///   class, given how far along it the engine says the hit was" - fed to
    ///   VrBlade's running fit, which is the only way TryGetEnemyBlade (TEST
    ///   264's blade-on-blade block) ever gets an enemy's blade that is not a
    ///   guess.
    ///
    ///   GHOST (TEST 263) - VrPhysicalCombat.Strike now lands the player's own
    ///   hit by calling this same real method (through Invoke, below) with a
    ///   hand-built AttackCollisionData, so the blade's hit gets everything a
    ///   vanilla blow gets. The engine's HIDDEN attack - the animation VrMelee
    ///   still fires so the AI sees a swing and the wind-up plays - would
    ///   otherwise land ITS OWN blow through this same callback about 0.4 s
    ///   later, on whatever the blade missed or already hit. The plan's answer
    ///   is that only the blade does damage: a collision from the player's own
    ///   hidden attack, arriving within melee_ready_ticks/InstantStrike's own
    ///   window of the swing, is ghosted here - no damage, no blood, no sound,
    ///   no bounce, no alarm - by handing back exactly what the engine's own
    ///   code hands back for a collision it wants to disregard
    ///   (colReaction = ContinueChecking, hitParticleResultData.Reset()) and
    ///   skipping the rest of the method. The attack still ran, so the AI still
    ///   saw it and still guards against it.
    ///
    /// The private target is resolved by hand for the same reason MeleeBlowPatch
    /// does it: `ref AttackCollisionData`, `ref float`, `ref MeleeCollisionReaction`
    /// and `ref HitParticleResultData` are all byrefs in metadata, and only
    /// MakeByRefType() can name them. Parameter names match the game's exactly -
    /// Harmony binds by name, and MeleeBlowPatch's own doc records what happens
    /// when they do not (the whole patch class silently does not apply).
    /// </summary>
    [HarmonyPatch]
    public static class MeleeHitPatch
    {
        /// <summary>Always applied, for the same reason MeleeBlowPatch always is:
        /// the runtime gate is VrPhysicalCombat.Enabled/VrMelee.Enabled, checked
        /// inside the prefix, and a patch never laid down cannot be switched on
        /// later from the VR panel.</summary>
        private static bool Prepare()
        {
            return true;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Mission), "MeleeHitCallback",
                                      new[]
                                      {
                                          typeof(AttackCollisionData).MakeByRefType(),
                                          typeof(Agent),
                                          typeof(Agent),
                                          typeof(GameEntity),
                                          typeof(float).MakeByRefType(),
                                          typeof(MeleeCollisionReaction).MakeByRefType(),
                                          typeof(CrushThroughState),
                                          typeof(Vec3),
                                          typeof(Vec3),
                                          typeof(HitParticleResultData).MakeByRefType(),
                                          typeof(bool),
                                      });
        }

        /// <summary>Set around Invoke's own call so this same prefix lets the
        /// player's synthetic blow through untouched - it must run the real
        /// method, not be ghosted as if it were the hidden attack. Not a lock:
        /// the mission tick this all runs on is single-threaded, and the plan's
        /// own wording ([ThreadStatic]) is followed anyway as the cheap extra
        /// margin against a re-entrant call this method never expects.</summary>
        [ThreadStatic]
        private static bool _ourBlow;

        /// <summary>MeleeBlowPatch's own prefix (Mission.RegisterBlow, called
        /// FROM inside MeleeHitCallback) needs to know the same thing: this
        /// blow is already fully decided, so its own probe and block test must
        /// stand aside rather than run a second time on a hit that already
        /// passed GuardStops/ShieldStops in Strike.</summary>
        internal static bool RunningOurBlow => _ourBlow;

        /// <summary>
        /// The BlowFlag CreateMeleeBlow computed for the player's own last
        /// synthetic hit - KnockBack, KnockDown, ShrugOff and the rest, which
        /// the engine decides inside MeleeHitCallback on a Blow this class never
        /// sees directly. MeleeBlowPatch stashes it here (Mission.RegisterBlow
        /// is where the Blow object briefly exists) while RunningOurBlow is
        /// true, purely so VrPhysicalCombat's own log line can report it - a
        /// diagnostic mirror, not something this class or MeleeBlowPatch acts
        /// on.
        /// </summary>
        internal static BlowFlags LastOurBlowFlags;

        /// <summary>Set by MeleeBlowPatch when the player's VR guard cancels an
        /// engine blow at RegisterBlow. MeleeHitCallback still runs
        /// DecideAgentHitParticles AFTER that, off the blow's uncancelled
        /// damage, so the blood the native caller spawns is cleared in the
        /// postfix below.</summary>
        [ThreadStatic]
        internal static bool BlowCancelled;

        private static bool _loggedGhost;
        private static bool _broken;

        [HarmonyPrefix]
        private static bool Prefix(ref AttackCollisionData collisionData, Agent attacker, Agent victim,
                                   GameEntity realHitEntity, ref float inOutMomentumRemaining,
                                   ref MeleeCollisionReaction colReaction,
                                   CrushThroughState crushThroughState, Vec3 blowDir, Vec3 swingDir,
                                   ref HitParticleResultData hitParticleResultData,
                                   bool crushedThroughWithoutAgentCollision)
        {
            BlowCancelled = false;

            if (_ourBlow)
                return true;

            if (!_broken)
            {
                try
                {
                    Probe(attacker, victim, in collisionData);

                    // TEST 315: an NPC's shield block says where its plate is.
                    VrBlade.ProbePlate(attacker, victim, in collisionData);
                }
                catch (Exception ex)
                {
                    _broken = true;
                    VrLog.Error("TEST 262: the enemy-blade probe failed; it is off for this "
                                + "session. Nothing else about melee is affected.", ex);
                }
            }

            if (!VrMelee.Enabled || !VrMelee.InstantStrike || attacker == null)
                return true;

            Mission mission = Mission.Current;
            if (mission == null || attacker != mission.MainAgent)
                return true;

            long since = Stopwatch.GetTimestamp() - VrMelee.LastSwingAt;
            if (VrMelee.LastSwingAt == 0L || since > (long)(1.2 * Stopwatch.Frequency))
                return true;

            // TEST 263: the ghost. Handing back exactly what the engine's own
            // code hands back for a collision it wants to disregard entirely -
            // no damage, no particles, and colReaction says "nothing happened
            // here" to whatever native code called this (which, for the hidden
            // attack, is the engine's own animation-driven collision test).
            hitParticleResultData.Reset();
            colReaction = MeleeCollisionReaction.ContinueChecking;

            if (!_loggedGhost)
            {
                _loggedGhost = true;
                VrLog.Info("TEST 263 ghost: the hidden engine attack does no damage of its own "
                           + "any more - only the blade hits. Its own collisions are dropped: no "
                           + "damage, no blood, no sound, no bounce, no alarm. The attack still "
                           + "runs, so the AI still sees the swing and still guards against it.");
            }

            return false;
        }

        [HarmonyPostfix]
        private static void Postfix(ref HitParticleResultData hitParticleResultData)
        {
            if (!BlowCancelled)
                return;

            BlowCancelled = false;
            hitParticleResultData.Reset();
        }

        // --- TEST 262: the probe --------------------------------------------

        private static readonly bool ProbeEnabled = VrConfig.Bool("combat_probe_enemy_blade", true);

        private static int _checkAt, _checkCount, _checkFromFrame;
        private static float _checkSum, _checkWorst;

        /// <summary>Every real engine melee collision by a human with a weapon on
        /// an agent is a free sample: bone-local contact, paired with the
        /// engine's own along-weapon distance. Validated against the CURRENT fit
        /// before this sample is folded in, so the log line is honestly
        /// out-of-sample.</summary>
        private static void Probe(Agent attacker, Agent victim, in AttackCollisionData collisionData)
        {
            if (!ProbeEnabled || attacker == null || !attacker.IsHuman || collisionData.IsMissile)
                return;

            int slotIndex = collisionData.AffectorWeaponSlotOrMissileIndex;
            if (slotIndex < 0)
                return;

            EquipmentIndex slot = (EquipmentIndex)slotIndex;
            MissionWeapon weapon = attacker.Equipment[slot];
            if (weapon.IsEmpty)
                return;

            WeaponComponentData usage = weapon.CurrentUsageItem;
            if (usage == null || !usage.IsMeleeWeapon)
                return;

            sbyte attackBone = collisionData.AttackBoneIndex;
            if (attackBone < 0 || !VrBlade.TryBoneWorld(attacker, attackBone, out MatrixFrame boneWorld))
                return;

            Vec3 contact = collisionData.CollisionGlobalPosition;
            float d = collisionData.CollisionDistanceOnWeapon;

            // Out-of-sample check against the fit as it stood before this hit.
            // TEST 421: summarised every 5 s. One line per hit was every NPC-vs-NPC
            // blow in the battle - 1425 lines in 90 s, each a synchronous write.
            if (VrBlade.TryGetEnemyBlade(attacker, slot, out VrBlade.Segment predicted))
            {
                float gap = PointSegment(contact, predicted.GripWorld, predicted.TipWorld);
                _checkCount++;
                _checkSum += gap;
                if (gap > _checkWorst)
                    _checkWorst = gap;
                if (predicted.FromFrame)
                    _checkFromFrame++;

                int now = Environment.TickCount;
                if (now - _checkAt >= 5000)
                {
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 262 check: enemy blade vs engine contact over {0} hit(s): {1:F3} m "
                        + "mean, {2:F3} m worst; {3} from the item frame (TEST 311), the rest "
                        + "from the fit. Last: '{4}', {5}.",
                        _checkCount, _checkSum / _checkCount, _checkWorst, _checkFromFrame,
                        weapon.Item?.StringId ?? "?", usage.WeaponClass));
                    _checkAt = now;
                    _checkCount = _checkFromFrame = 0;
                    _checkSum = _checkWorst = 0f;
                }
            }

            Vec3 boneLocalContact = boneWorld.TransformToLocal(contact);
            VrBlade.AccumulateEnemyBladeSample(usage.WeaponClass, boneLocalContact, d);
            VrBlade.LogEnemyBladeFitIfDue(usage.WeaponClass);
        }

        private static float PointSegment(Vec3 point, Vec3 a, Vec3 b)
        {
            Vec3 ab = b - a;
            float lengthSquared = ab.LengthSquared;
            if (lengthSquared < 1e-8f)
                return (point - a).Length;

            float t = Math.Max(0f, Math.Min(1f, Vec3.DotProduct(point - a, ab) / lengthSquared));
            return (point - (a + ab * t)).Length;
        }

        // --- TEST 263: invoking the real method for the player's own hit ----

        private static MethodInfo _callback;
        private static MethodInfo _defendResults;

        private static MethodInfo Callback()
        {
            return _callback ?? (_callback = (MethodInfo)TargetMethod());
        }

        /// <summary>
        /// Runs the REAL Mission.MeleeHitCallback for the player's own landed
        /// hit, with momentumRemaining = 1.0 (a fresh swing, nothing spent yet).
        /// Everything the engine does for its own blows now happens for this one
        /// too: friend/neutral rules, armor, perks, difficulty, bone
        /// multipliers, knockback/knockdown/dismount, the combat log and damage
        /// popup, the hit sound with its armor parameter, XP, death, the hit
        /// reaction, and every MissionBehavior's OnMeleeHit.
        /// </summary>
        internal static void Invoke(Mission mission, ref AttackCollisionData collisionData,
                                    Agent attacker, Agent victim, ref float momentumRemaining,
                                    ref MeleeCollisionReaction colReaction, Vec3 blowDir, Vec3 swingDir,
                                    ref HitParticleResultData hitParticleResultData,
                                    GameEntity realHitEntity = null)
        {
            // TEST 641: realHitEntity with no victim is a scene object (a gate) -
            // vanilla's own OnEntityHit -> DestructableComponent.OnHit path.
            object[] args =
            {
                collisionData, attacker, victim, realHitEntity, momentumRemaining, colReaction,
                CrushThroughState.None, blowDir, swingDir, hitParticleResultData, false,
            };

            _ourBlow = true;
            try
            {
                Callback().Invoke(mission, args);
            }
            finally
            {
                _ourBlow = false;
            }

            collisionData = (AttackCollisionData)args[0];
            momentumRemaining = (float)args[4];
            colReaction = (MeleeCollisionReaction)args[5];
            hitParticleResultData = (HitParticleResultData)args[9];
        }

        /// <summary>
        /// The stun periods a real engine collision would carry into its Blow -
        /// computed the vanilla way (MissionCombatMechanicsHelper.
        /// GetDefendCollisionResults, internal static) rather than guessed,
        /// because MeleeHitCallback itself never derives them: in a real swing
        /// they are set by native code before the callback runs, which our own
        /// call bypasses. Also returns whether vanilla would call this hit a
        /// crush-through, for the cleave decision in VrPhysicalCombat.Strike.
        ///
        /// TEST 265: collisionResult is the SAME value the caller is about to
        /// put on the AttackCollisionData it hands to Invoke - StrikeAgent for
        /// a clean hit, Blocked when GuardStops/ShieldStops found real contact
        /// - because GetDefendCollisionResults itself branches on it (a Parried
        /// collision gets extra attacker stun, see the decompiled source).
        /// Decompiling Mission.MeleeHitCallback confirmed CollisionResult is an
        /// INPUT the engine reads, never something it infers from the victim's
        /// action state - see [[bl2vr-decompile-engine-api]].
        /// </summary>
        internal static void ComputeStunPeriods(Agent attacker, Agent victim, int weaponSlotIndex,
                                                StrikeType strikeType, Agent.UsageDirection attackDirection,
                                                float collisionDistanceOnWeapon, float attackProgress,
                                                CombatCollisionResult collisionResult,
                                                out float attackerStun, out float defenderStun,
                                                out bool crushedThrough)
        {
            attackerStun = 0.2f;
            defenderStun = 0.4f;
            crushedThrough = false;

            try
            {
                if (_defendResults == null)
                {
                    _defendResults = AccessTools.Method(typeof(MissionCombatMechanicsHelper),
                        "GetDefendCollisionResults",
                        new[]
                        {
                            typeof(Agent), typeof(Agent), typeof(CombatCollisionResult), typeof(int),
                            typeof(bool), typeof(StrikeType), typeof(Agent.UsageDirection),
                            typeof(float), typeof(float), typeof(bool), typeof(bool), typeof(bool),
                            typeof(float).MakeByRefType(), typeof(float).MakeByRefType(),
                            typeof(bool).MakeByRefType(), typeof(bool).MakeByRefType(),
                        });
                }

                if (_defendResults == null)
                    return;

                object[] args =
                {
                    attacker, victim, collisionResult, weaponSlotIndex, false,
                    strikeType, attackDirection, collisionDistanceOnWeapon, attackProgress, false,
                    false, false, defenderStun, attackerStun, crushedThrough, false,
                };

                _defendResults.Invoke(null, args);

                defenderStun = (float)args[12];
                attackerStun = (float)args[13];
                crushedThrough = (bool)args[14];
            }
            catch (Exception)
            {
                // The flat defaults above are a landed hit with modest stun,
                // which is a better failure than throwing out of the hit path.
            }
        }

        /// <summary>
        /// TEST 263. The AttackProgress that makes SpeedGraphFunction return
        /// exactly 1.0 - the plateau between the graph's own FirstMaximumPoint
        /// and SecondMaximumPoint, read from ManagedParameters (TaleWorlds.Core),
        /// the same constants the engine's own speed bonus reads. A hit built at
        /// this progress carries neither an under-swing penalty nor an
        /// over-swing bonus: the weapon's own numbers, full stop, which is the
        /// vanilla-exact request for offense (problem 1).
        /// </summary>
        internal static float ComputeAttackProgress(StrikeType strikeType, Agent.UsageDirection direction)
        {
            ManagedParametersEnum first;
            ManagedParametersEnum second;

            if (strikeType == StrikeType.Thrust)
            {
                first = ManagedParametersEnum.ThrustCombatSpeedGraphFirstMaximumPoint;
                second = ManagedParametersEnum.ThrustCombatSpeedGraphSecondMaximumPoint;
            }
            else if (direction == Agent.UsageDirection.AttackUp)
            {
                first = ManagedParametersEnum.OverSwingCombatSpeedGraphFirstMaximumPoint;
                second = ManagedParametersEnum.OverSwingCombatSpeedGraphSecondMaximumPoint;
            }
            else
            {
                first = ManagedParametersEnum.SwingCombatSpeedGraphFirstMaximumPoint;
                second = ManagedParametersEnum.SwingCombatSpeedGraphSecondMaximumPoint;
            }

            try
            {
                float a = ManagedParameters.Instance.GetManagedParameter(first);
                float b = ManagedParameters.Instance.GetManagedParameter(second);
                return (a + b) * 0.5f;
            }
            catch (Exception)
            {
                return 0.5f;
            }
        }
    }
}
