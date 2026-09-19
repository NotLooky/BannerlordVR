using System;
using BannerlordVR.Interop;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Moves the weapon itself, rather than the bone it hangs from.
    ///
    /// WHY THIS EXISTS - THE BONE WAS NEVER GOING TO WORK
    ///
    /// The whole approach so far rested on one sentence in VrHandSlots: weapons
    /// are parented to r_finger0, so "posing a hand carries its item along". The
    /// log says that sentence is wrong. Everything upstream of it worked - the
    /// bones resolved, both grips were captured, and the poser reported itself
    /// running every frame:
    ///
    ///     Player skeleton has 28 bone(s)
    ///     Grip captured for the Main hand from 240 samples, wobble 6.7 deg
    ///     Hands are following the controllers
    ///
    /// and the weapon still sat where the animation left it. So the hand bone WAS
    /// being written and the weapon did not care.
    ///
    /// It does not care because it is not a skinned mesh. A wielded weapon is its
    /// own GameEntity - Agent.GetWeaponEntityFromEquipmentSlot hands one back, and
    /// MBAgentVisuals.AddChildEntity is how it got there - and the engine computes
    /// that entity's transform from the bone during its own skeleton update. Our
    /// write lands after that update. It moves the skinned hand, because the hand
    /// is vertices weighted to the bone and those are read later; it cannot move
    /// the weapon, because the weapon's transform was resolved before we touched
    /// anything and nothing recomputes it.
    ///
    /// The fix is to stop going through the bone. The weapon is an entity with a
    /// settable frame, so it gets set.
    ///
    /// WHERE THE WEAPON GOES, AND THE ONE THING STILL WORTH CAPTURING
    ///
    /// A weapon entity's origin is not its grip - it is wherever the artist put
    /// the mesh pivot - so placing it at the controller would hold a sword by
    /// whatever point that happens to be. That offset is a real, fixed property of
    /// the equipment, and the game already knows it: while the animation is
    /// running, the weapon sits correctly in the hand, so
    ///
    ///     boneToWeapon = handBoneWorld' * weaponEntityWorld
    ///
    /// is exactly the mesh binding, and unlike the arm-gap number that started all
    /// of this, BOTH halves of it are meaningful. It is captured during the same
    /// window the grip rotation is - the one time the hand is deliberately left on
    /// its animation - and then held, because after that the weapon's frame is
    /// ours and reading it back would only return our own writing.
    ///
    /// So the chain is:
    ///
    ///     handWorld   = controller position + captured grip rotation   (as before)
    ///     weaponWorld = handWorld * boneToWeapon
    ///
    /// which puts the weapon in the hand the way the game intends, and the hand on
    /// your controller.
    ///
    /// IT CHECKS THAT IT WORKED
    ///
    /// Whether the engine leaves a child entity's frame alone once it has been set
    /// is exactly the kind of thing that is cheaper to measure than to reason
    /// about - and the last round was lost to reasoning about it. So the frame
    /// that was written is remembered, read back on the next frame, and the
    /// difference reported once. If the engine is overwriting us, the log will say
    /// so in one sentence instead of costing another session.
    /// </summary>
    public static class VrWeaponEntity
    {
        /// <summary>
        /// Drive the weapon entities directly. On by default: posing the bone
        /// demonstrably does not move them.
        /// </summary>
        public static bool Enabled { get; } = VrConfig.Bool("weapon_entity_follow", true);

        /// <summary>Whether the write is checked against what comes back next
        /// frame. Cheap, and it is the only way to know whether this works.</summary>
        private static readonly bool Verify = VrConfig.Bool("weapon_entity_verify", true);

        /// <summary>How far the read-back may differ before it counts as the
        /// engine having overwritten us, in metres.</summary>
        private static readonly float VerifyTolerance =
            VrConfig.Float("weapon_entity_tolerance", 0.02f);

        private const int Roles = 2;

        /// <summary>The mesh binding: where the weapon sits relative to the hand
        /// bone. Captured from the animation, then held.</summary>
        private static readonly MatrixFrame[] _boneToWeapon = new MatrixFrame[Roles];
        private static readonly bool[] _haveBinding = new bool[Roles];

        // --- the read-back check ---------------------------------------------
        private static bool _loggedVerdict;
        private static bool _loggedDriving;
        private static bool _broken;

        /// <summary>
        /// Records where the weapon sits relative to its hand bone.
        ///
        /// Called only while that hand is being sampled - the window where the
        /// bone is deliberately left on its animation - because that is the only
        /// time the weapon's frame is the game's answer rather than ours.
        /// </summary>
        /// <summary>
        /// Whether the mesh binding for this hand has been measured.
        ///
        /// The caller asks so it can measure it, and it must be measured BEFORE
        /// the first write to that weapon - after that its frame is ours and
        /// reading it back returns our own writing.
        /// </summary>
        public static bool HasBinding(HandRole role) => _haveBinding[(int)role];

        public static void Observe(HandRole role, MatrixFrame boneWorld, MatrixFrame weaponWorld)
        {
            int i = (int)role;

            MatrixFrame binding = boneWorld.TransformToLocal(weaponWorld);

            // Bone frames can carry scale, and TransformToLocal will hand back a
            // basis that is not orthonormal. Posing through one would shear the
            // sword - subtly, and in a way that looks like a tracking fault.
            binding.rotation.Orthonormalize();

            _boneToWeapon[i] = binding;
            _haveBinding[i] = true;
        }

        /// <summary>
        /// Puts one weapon where its hand is. <paramref name="handWorld"/> is the
        /// frame the hand bone was posed to, in world space.
        /// </summary>
        public static void Drive(Agent agent, VrHandSlot slot, MatrixFrame handWorld)
        {
            if (!Enabled || _broken || !slot.Valid)
                return;

            int i = (int)slot.Role;

            try
            {
                if (slot.Equipment == EquipmentIndex.None)
                    return;

                WeakGameEntity weapon = agent.GetWeaponEntityFromEquipmentSlot(slot.Equipment);
                if (!weapon.IsValid)
                    return;

                if (!_haveBinding[i])
                {
                    // Nothing measured yet, so we do not know where along the sword
                    // the hand goes. Leaving it on the animation for a frame or two
                    // is invisible; guessing an identity binding would hold every
                    // weapon by its mesh pivot, which is not a grip.
                    return;
                }

                MatrixFrame target = handWorld.TransformToParent(_boneToWeapon[i]);

                // isTeleportation: this is not motion the physics or the
                // interpolator should smooth. It is where the weapon IS, decided
                // fresh every frame from a tracked pose, and asking the engine to
                // ease into it would add exactly the lag a hand notices.
                weapon.SetGlobalFrame(in target, isTeleportation: true);

                if (Verify)
                    CheckImmediate(slot.Role, weapon, target);

                if (!_loggedDriving)
                {
                    _loggedDriving = true;
                    VrLog.Info("Weapon entities are being driven directly. The hand BONE was "
                               + "being posed correctly all along - a wielded weapon is its own "
                               + "GameEntity whose transform the engine resolves from the bone "
                               + "before our write lands, so moving the bone never moved it.");
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("Driving the weapon entity failed; weapons go back to the "
                            + "animation. Nothing else is affected.", ex);
            }
        }

        /// <summary>
        /// Reads back last frame's write and says once whether it survived.
        ///
        /// This is the whole question the round turns on and it is one subtraction.
        /// If the engine recomputes a child entity's frame from its parent bone
        /// after we set it, the weapon will sit on the animation and nothing will
        /// look different from the bug we are fixing - so it has to be possible to
        /// tell those two apart from a log.
        /// </summary>
        private static void CheckImmediate(HandRole role, WeakGameEntity weapon,
                                           MatrixFrame target)
        {
            if (_loggedVerdict)
                return;

            _loggedVerdict = true;

            MatrixFrame now = weapon.GetGlobalFrame();
            float drift = (now.origin - target.origin).Length;

            if (drift <= VerifyTolerance)
            {
                VrLog.Info("Weapon entity write CONFIRMED: read straight back "
                           + drift.ToString("0.000") + " m from where it was put, so the frame "
                           + "takes. Whether it SURVIVES to be drawn is now a question about "
                           + "where in the frame this runs, and it runs at scene-render time.");
                return;
            }

            VrLog.Warn("Weapon entity write was REFUSED: read straight back "
                       + drift.ToString("0.000") + " m away, so SetGlobalFrame is not taking on "
                       + "this entity at all - it is probably being clamped to its parent. "
                       + "Report this line.");
        }

        /// <summary>Per-mission. The binding belongs to one set of equipment on one
        /// skeleton, and the next mission measures its own.</summary>
        public static void Reset()
        {
            for (int i = 0; i < Roles; i++)
                _haveBinding[i] = false;

            _loggedDriving = false;
            _loggedVerdict = false;
        }
    }
}
