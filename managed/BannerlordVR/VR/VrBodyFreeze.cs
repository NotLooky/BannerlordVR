using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 252. While the engine is in an attack action, the body holds the pose
    /// it had when the swing began. Nothing of the attack animation is drawn.
    ///
    /// WHY THE CHEST PIN WAS NOT ENOUGH. TEST 234 pins the chest and shoulders
    /// (orientations) and TEST 250 holds the chest's position, which took the lean
    /// out of the torso - but the hips, the legs, the neck and the head still
    /// played the animation, and so did every helper bone. The player sees their
    /// own body at first-person range, so what is left is still "my character is
    /// doing an attack animation": a step, a turn of the hips, a head that swings
    /// with the blow. The animation has no job here any more - the arms and the
    /// blade are the player's, and the damage is the blade's (TEST 248) - so the
    /// whole skeleton is held for the length of the swing.
    ///
    /// HOW. The local frames of every bone are snapshotted on the frame the attack
    /// action appears, and written back every frame until it ends, converted
    /// through the snapshot's own parents so a bone whose parent also moved to the
    /// animation's pose still lands where it was. The bones the player drives -
    /// the arms, the hands, the chest the pin owns - are written after this by the
    /// posing pass, so they stay the player's; the freeze only holds what would
    /// otherwise be the animation's.
    ///
    /// WHAT IT COSTS, PLAINLY: while an attack is running the legs do not cycle,
    /// so running and swinging at the same time reads as a short glide. That is a
    /// deliberate trade against "my body plays the attack animation", and it is
    /// why the whole thing is behind melee_freeze_body.
    /// </summary>
    public static class VrBodyFreeze
    {
        /// <summary>Shared with TEST 250's chest hold: one switch for "the body
        /// does not play engine attacks". Read by VrCameraDriver (TEST 266): once
        /// the whole skeleton is held, a visible body no longer disagrees with a
        /// held camera, which is the reason TEST 259 turned the view hold off for
        /// a visible body in the first place.</summary>
        internal static readonly bool Enabled = VrConfig.Bool("melee_freeze_body", true);

        private static readonly Pose _attack = new Pose();
        private static bool _have;
        private static bool _logged;

        /// <summary>
        /// Writes the held pose when an attack is running. Returns true when it
        /// wrote, so the caller knows the entitial frames need rebuilding.
        /// </summary>
        public static bool Apply(Agent agent, Skeleton skeleton)
        {
            if (!Enabled || agent == null || skeleton == null)
                return false;

            bool attacking;

            try
            {
                attacking = VrMelee.EngineActing(agent);
            }
            catch (Exception)
            {
                return false;
            }

            if (!attacking)
            {
                _have = false;
                return false;
            }

            if (!_have || !_attack.Fits(skeleton))
            {
                if (!_attack.Take(skeleton))
                    return false;

                _have = true;

                if (!_logged)
                {
                    _logged = true;
                    VrLog.Info("TEST 252: nothing of the body plays the engine's attack animation. "
                               + "The skeleton is held at the pose it had when the swing began - "
                               + "the arms, the hands and the weapon are still yours, and the "
                               + "attack state still runs for the AI and the sound. "
                               + "melee_freeze_body = 0 puts the animation back.");
                }

                // The pose is already what we want on the frame it is taken.
                return false;
            }

            _attack.Restore(skeleton);
            return true;
        }

        /// <summary>A whole skeleton's entitial frames, and writing them back as
        /// locals through the SNAPSHOT's own parents, not this frame's - so a bone
        /// whose parent has since been animated still lands where it was.</summary>
        private sealed class Pose
        {
            private MatrixFrame[] _frames;
            private sbyte[] _parent;
            private int _count;

            internal bool Fits(Skeleton skeleton) => _frames != null && _count == skeleton.GetBoneCount();

            internal bool Take(Skeleton skeleton)
            {
                int count = skeleton.GetBoneCount();
                if (count <= 0 || count > 127)
                    return false;

                _frames = new MatrixFrame[count];
                _parent = new sbyte[count];
                _count = count;
                for (sbyte i = 0; i < count; i++)
                {
                    _frames[i] = skeleton.GetBoneEntitialFrameWithIndex(i);
                    _parent[i] = skeleton.GetParentBoneIndex(i);
                }
                return true;
            }

            internal void Restore(Skeleton skeleton)
            {
                for (sbyte i = 0; i < _count; i++)
                {
                    sbyte p = _parent[i];
                    MatrixFrame local = p >= 0 && p < _count
                        ? _frames[p].TransformToLocalNonOrthogonal(_frames[i])
                        : _frames[i];
                    skeleton.SetBoneLocalFrame(i, local);
                }
            }
        }

        // ---- TEST 384: the idle hold -------------------------------------------
        //
        // Every frame the engine re-animates the skeleton and we overwrite it after
        // the tick, so whatever reads the bones in between (the equipment, TEST
        // 216-218 - and, the bet here, the cloth) sees two poses a frame apart:
        // the idle animation and ours. The user's armour cloth "shakes without me
        // moving", and always has. Standing still, nothing needs the animation, so
        // it is paused (GameEntity.PauseSkeletonAnimation, the TEST 217 lever) and
        // the skeleton keeps our pose between frames. Every read our passes make
        // must still see the animation's still idle, never our own last output, so
        // the pose the engine had when the hold began is written back first thing
        // each held frame (TEST 217b's reference pose).

        /// TEST 384 RESULT: NEGATIVE - the cloth still shook, and a held (paused)
        /// skeleton cannot turn: the body stopped rotating with the head, and the
        /// planted feet re-stepped ~4 times a second against the frozen reference.
        /// Off; kept behind the key.
        private static readonly bool IdleEnabled = VrConfig.Bool("body_idle_hold", false);

        /// <summary>How long the engine's actions and the wielded slots must stay
        /// unchanged before a hold: a hit reaction or a draw must play out, not
        /// freeze half way.</summary>
        private const float IdleSettleSeconds = 0.5f;

        private static readonly Pose _idle = new Pose();
        private static Agent _heldAgent;
        private static Mission _heldMission;
        private static long _signature;
        private static long _signatureSince;
        private static int _idleCalls, _idleCallsSeen;

        internal static bool IdleHeld => _heldAgent != null;

        /// <summary>
        /// TEST 384, from VrWeaponHands before anything reads the skeleton: holds,
        /// keeps or lets go. While held, the reference pose is written back and
        /// the entitial frames rebuilt, so the passes after it read exactly what
        /// they would have read from the animation.
        /// </summary>
        internal static void Idle(Agent agent, GameEntity entity, Skeleton skeleton)
        {
            _idleCalls++;
            if (!IdleEnabled || agent == null || entity == null || skeleton == null)
                return;

            try
            {
                // Paused, the hands pass writes nothing: a reference pose put back
                // now would be drawn un-posed. The hold simply stays.
                if (VrWeaponHands.Paused())
                    return;

                string why = IdleBlocked(agent, skeleton);
                if (why != null)
                {
                    ReleaseIdle(why);
                    return;
                }

                if (_heldAgent == null)
                {
                    if (!_idle.Take(skeleton))
                        return;

                    entity.PauseSkeletonAnimation();
                    _heldAgent = agent;
                    _heldMission = Mission.Current;
                    VrLog.Info("TEST 384 idle hold ON: standing still, the engine's animation is paused (engine "
                               + "reports paused = " + entity.IsSkeletonAnimationPaused() + "), so the bones keep "
                               + "our pose between frames. Watch the armour cloth: still and settled = the shake was "
                               + "the animation and our pose alternating.");
                    return;     // this frame's bones are the engine's own fresh pose
                }

                if (!ReferenceEquals(_heldAgent, agent) || !_idle.Fits(skeleton))
                {
                    ReleaseIdle("a new agent");
                    return;
                }

                _idle.Restore(skeleton);
                skeleton.UpdateEntitialFramesFromLocalFrames();
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 384: the idle hold failed; letting go.", ex);
                ReleaseIdle("an error");
            }
        }

        /// <summary>TEST 384b: no hold within this long of being in the saddle -
        /// the dismount is still lowering the body.</summary>
        private const float AfterMountSeconds = 2f;
        private static long _mountedAt;

        /// <summary>Why the body may not be held this frame, or null.</summary>
        private static string IdleBlocked(Agent agent, Skeleton skeleton)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (agent.HasMount)
            {
                _mountedAt = now;
                return "mounted";
            }
            if (!VrBodyIk.HeadDrives)
                return "the head-driven body is not driving";

            // TEST 384b: the pose a hold freezes must be a body standing on the
            // ground. 384's first run took it 0.5 s after a dismount, still at
            // saddle height, and held the character there in mid-air.
            if (_heldAgent == null)
            {
                if (now - _mountedAt < (long)(AfterMountSeconds * System.Diagnostics.Stopwatch.Frequency))
                    return Settling;
                if (!VrBodyBones.TryResolve(agent.AgentVisuals, skeleton, out _, out VrLegChain left, out VrLegChain right)
                    || !VrHeadBody.FeetDown(skeleton, left, right))
                    return Settling;
            }
            if (VrHeadBody.Moving(agent))
                return "the stick moved";
            if (agent.CrouchMode)
                return "a game crouch";
            if (VrMelee.EngineActing(agent))
                return "an engine attack or block";

            // The engine's actions on both channels and the wielded slots: a hold
            // only once they have all stood still for IdleSettleSeconds, and none
            // the moment any of them changes.
            long signature = ((long)(ushort)agent.GetCurrentAction(0).Index << 48)
                             | ((long)(ushort)agent.GetCurrentAction(1).Index << 32)
                             | ((long)(ushort)(int)agent.GetPrimaryWieldedItemIndex() << 16)
                             | (ushort)(int)agent.GetOffhandWieldedItemIndex();
            if (signature != _signature)
            {
                _signature = signature;
                _signatureSince = now;
                return _heldAgent != null ? "the engine changed an action or a weapon" : Settling;
            }

            if (_heldAgent == null
                && now - _signatureSince < (long)(IdleSettleSeconds * System.Diagnostics.Stopwatch.Frequency))
                return Settling;

            return null;
        }

        private const string Settling = "settling";

        /// <summary>Lets go: the animation resumes. The entity is resolved from the
        /// live agent now, never remembered (a dead entity faults rather than
        /// throws - VrWeaponAttach.Release's rule).</summary>
        internal static void ReleaseIdle(string why)
        {
            Agent agent = _heldAgent;
            if (agent == null)
                return;

            _heldAgent = null;
            Mission mission = Mission.Current;
            try
            {
                if (mission != null && ReferenceEquals(mission, _heldMission) && !mission.IsFinalized)
                {
                    MBAgentVisuals visuals = agent.AgentVisuals;
                    if (visuals != null && visuals.IsValid())
                        visuals.GetEntity()?.ResumeSkeletonAnimation();
                }

                VrLog.Info("TEST 384 idle hold off (" + why + "): the engine animates again.");
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 384: resuming the animation failed.", ex);
            }
        }

        /// <summary>
        /// Every frame from MissionTickPatch, before the hands pass: a hold whose
        /// pass did not run last frame (hands switched off, the agent gone or dead,
        /// any early return) is let go, so a paused skeleton is never left behind.
        /// </summary>
        internal static void IdleGuard()
        {
            if (_heldAgent == null)
            {
                _idleCallsSeen = _idleCalls;
                return;
            }

            Mission mission = Mission.Current;
            if (mission == null || !ReferenceEquals(mission, _heldMission))
            {
                ForgetIdle();       // the scene is gone, and the entity with it
                return;
            }

            if (_idleCalls == _idleCallsSeen || !ReferenceEquals(mission.MainAgent, _heldAgent)
                || !_heldAgent.IsActive())
                ReleaseIdle("the hands pass stopped, or the agent changed");

            _idleCallsSeen = _idleCalls;
        }

        /// <summary>A mission ending: drop the references, touch nothing.</summary>
        internal static void ForgetIdle()
        {
            _heldAgent = null;
            _heldMission = null;
            _signature = 0;
            _clothAgent = null;
        }

        // ---- TEST 384 fallback: stiffer cloth on the player ----------------------

        /// <summary>GameEntity.SetClothMaxDistanceMultiplier on the player's own
        /// entity: how far its cloth may swing from the skinned surface. 1 = the
        /// engine's own (nothing is called); lower is stiffer; 0 is rigid. For a
        /// run with body_idle_hold = 0, if the hold freezes the cloth.</summary>
        private static readonly float ClothDistance =
            Math.Max(0f, VrConfig.Float("body_cloth_distance", 1f));

        private static Agent _clothAgent;

        internal static void Cloth(Agent agent, GameEntity entity)
        {
            if (Math.Abs(ClothDistance - 1f) < 1e-4f || ReferenceEquals(_clothAgent, agent) || entity == null)
                return;

            _clothAgent = agent;
            try
            {
                entity.SetClothMaxDistanceMultiplier(ClothDistance);
                VrLog.Info("TEST 384: the player's cloth may swing x" + ClothDistance.ToString("F2")
                           + " of the engine's distance (body_cloth_distance).");
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 384: body_cloth_distance failed.", ex);
            }
        }
    }
}
