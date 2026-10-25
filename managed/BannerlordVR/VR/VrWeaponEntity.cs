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
    ///
    /// CORRECTION, TEST 211: THE PREMISE ABOVE IS NOT TRUE OF THE SHIELD
    ///
    /// "The engine computes that entity's transform from the bone" was the whole
    /// basis for this class, and TEST 208's own log contradicts it in a line nobody
    /// read at the time:
    ///
    ///     Off 'wpn_kite_shield_n': global (406.84,777.75,15.31) local (0.00,0.00,0.00)
    ///
    /// The agent was standing at about (407, 777.8, 15.3). So the shield's entity
    /// frame was identity-local, sitting at the agent's FEET, while the shield was
    /// plainly drawn up in the hand. An entity whose frame does not move with the
    /// animation is not the entity the animation is drawing, and writing it - in
    /// any space, at any seam - could never have moved the shield.
    ///
    /// That is why TESTs 207 through 210 all failed the same way while each of
    /// their individual conclusions was sound. The question was never WHEN the
    /// write lands. It was whether the object being written is the object being
    /// drawn, and nothing had asked that.
    ///
    /// None of the reasoning above is deleted, because it is the record of how the
    /// wrong question got asked four times. VrWeaponAttach asks the right one, and
    /// weapon_attach decides which of them runs.
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

        /// <summary>
        /// TEST 220. Where the weapon is anchored: "item" or "measured".
        ///
        /// WHY THE MEASURED BINDING IS WRONG, AND ALWAYS WAS
        ///
        /// This class opens by asserting that "while the animation is running, the
        /// weapon sits correctly in the hand, so boneToWeapon = handBoneWorld^-1 *
        /// weaponEntityWorld is exactly the mesh binding". That sentence is the
        /// foundation of everything below it and the census disproved it:
        ///
        ///   TEST 211 Main driven entity: 0.710 m from the animated item bone
        ///   TEST 211 Off  driven entity: 2.115 m from the animated item bone
        ///
        /// With the animation running normally and nobody writing anything, the
        /// weapon entity sits 71 cm from the hand and the shield sits over two
        /// metres away, down at the agent's origin. Those entities are PARKED, not
        /// gripped. So the captured binding is not a mesh binding at all - it is the
        /// offset from the hand to wherever the entity happens to be stored, and
        /// composing it onto the hand reproduces that storage offset faithfully.
        /// Which is exactly the floating hilt in the screenshots.
        ///
        /// WHAT TO USE INSTEAD
        ///
        /// The rig already names the answer. monsters.xml gives the human
        ///
        ///     main_hand_item_bone = r_finger0    off_hand_item_bone = l_finger0
        ///
        /// and an item bone exists for precisely one purpose: it is where the item
        /// goes. It sits in the palm. So "item" places the weapon AT that bone with
        /// no binding at all, which needs no capture, cannot be measured at a bad
        /// moment, and is the convention the rig was authored to.
        ///
        /// "measured" keeps the old behaviour for comparison. It is not the default
        /// any more and the numbers above are why.
        /// </summary>
        private static readonly string Bind =
            VrConfig.String("weapon_bind", "item").Trim().ToLowerInvariant();

        /// <summary>
        /// A fixed nudge applied in the anchor bone's own frame.
        ///
        /// A mesh pivot is an artist's choice and a bone axis is a rigger's, so the
        /// two need not agree even when both are correct. This is the one honest
        /// place for that constant - by eye, in the config, rather than smuggled
        /// into a capture that pretends to have measured it.
        /// </summary>
        private static readonly Vec3 NudgeOffset = new Vec3(
            VrConfig.Float("weapon_off_x", 0f),
            VrConfig.Float("weapon_off_y", 0f),
            VrConfig.Float("weapon_off_z", 0f));

        private static readonly float NudgeYaw = VrConfig.Float("weapon_yaw_deg", 0f);
        private static readonly float NudgePitch = VrConfig.Float("weapon_pitch_deg", 0f);
        private static readonly float NudgeRoll = VrConfig.Float("weapon_roll_deg", 0f);

        private static readonly bool _haveNudge =
            NudgeOffset.LengthSquared > 1e-9f
            || Math.Abs(NudgeYaw) > 1e-4f
            || Math.Abs(NudgePitch) > 1e-4f
            || Math.Abs(NudgeRoll) > 1e-4f;

        private static readonly MatrixFrame _nudge = BuildNudge();

        internal static bool HaveNudge => _haveNudge;
        internal static MatrixFrame NudgeFrame => _nudge;

        private static MatrixFrame BuildNudge()
        {
            MatrixFrame f = MatrixFrame.Identity;

            const float ToRad = (float)(Math.PI / 180.0);
            f.rotation.RotateAboutUp(NudgeYaw * ToRad);
            f.rotation.RotateAboutSide(NudgePitch * ToRad);
            f.rotation.RotateAboutForward(NudgeRoll * ToRad);
            f.origin = NudgeOffset;

            return f;
        }

        private const int Roles = 2;

        /// <summary>The mesh binding: where the weapon sits relative to the hand
        /// bone. Captured from the animation, then held.</summary>
        private static readonly MatrixFrame[] _boneToWeapon = new MatrixFrame[Roles];
        private static readonly bool[] _haveBinding = new bool[Roles];

        // --- the read-back check ---------------------------------------------
        private static bool _loggedVerdict;
        private static bool _loggedDriving;
        private static bool _broken;

        // --- TEST 207: does the write survive the frame? ----------------------
        //
        // CheckImmediate answers a different and much weaker question. It reads
        // the frame back in the SAME tick, so it proves SetGlobalFrame is not
        // being refused outright - and it says so in its own log line, that
        // whether the write SURVIVES to be drawn is still open. It is exactly the
        // gap that let "the weapon is fixed to the animation" sit here looking
        // like a confirmed success.
        //
        // So the target is held and re-read at the top of the NEXT frame, after
        // the engine has run its own skeleton and child-entity update. That is the
        // read that can tell "we own this entity" from "the engine puts it back
        // every frame", and the two need completely different fixes.
        private static readonly MatrixFrame[] _lastWritten = new MatrixFrame[Roles];
        private static readonly bool[] _haveLastWritten = new bool[Roles];
        private static readonly int[] _probeTick = new int[Roles];
        private static readonly int[] _probeReports = new int[Roles];
        private static readonly int[] _refreshTick = new int[Roles];
        private static readonly int[] _refreshReports = new int[Roles];

        /// <summary>Whether to check last frame's write. On: it is one subtraction
        /// and it is the only thing that can tell a working weapon from one that
        /// is quietly being put back on the animation.</summary>
        private static readonly bool Survives = VrConfig.Bool("weapon_entity_probe", true);

        /// <summary>How many times per hand to report before going quiet. Enough
        /// to see it settle, few enough not to fill the log.</summary>
        private static readonly int ProbeReports = VrConfig.Int("weapon_entity_probe_reports", 8);

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

        /// <summary>
        /// How the weapon's frame is written: "global" or "local".
        ///
        /// TEST 207 measured the global write being undone every frame - HELD 1.5
        /// to 2.1 m from where it was left, HAND 1.4 to 1.6 m from the controller,
        /// so the weapon was landing back on the animation and not near the player
        /// at all. Setting it harder was never going to help.
        ///
        /// If the entity has a PARENT, the engine composes its global frame as
        /// parent * local every frame, and a global write is overwritten by
        /// construction while a local one is preserved by construction. TEST 208
        /// reports which of those this actually is; this key is what acts on the
        /// answer without a rebuild.
        /// </summary>
        private static readonly string WriteMode =
            VrConfig.String("weapon_entity_write", "global").Trim().ToLowerInvariant();

        /// <summary>The hand bone where the ANIMATION had it this frame, before

        /// <summary>Resolved once from WriteMode, so the string comparison is not
        /// done per weapon per frame.</summary>
        private static readonly bool Local = WriteMode == "local";

        private static bool _loggedMode;

        private static void LogModeOnce(string what)
        {
            if (_loggedMode)
                return;

            _loggedMode = true;
            VrLog.Info("Weapon entities are written " + what + ".");
        }
        /// any of our writes. Needed to ask whether the weapon went back to the
        /// animated bone, which is the difference between "overwritten" and
        /// "aimed wrong".</summary>
        private static readonly MatrixFrame[] _animatedBone = new MatrixFrame[Roles];
        private static readonly bool[] _haveAnimatedBone = new bool[Roles];
        private static readonly bool[] _described = new bool[Roles];

        /// <summary>Told by the caller once per frame, before anything is posed.</summary>
        public static void NoteAnimatedBone(HandRole role, MatrixFrame boneWorld)
        {
            int i = (int)role;
            _animatedBone[i] = boneWorld;
            _haveAnimatedBone[i] = true;
        }

        /// <summary>
        /// TEST 208: what IS this entity, structurally.
        ///
        /// TEST 207 established that the write does not survive. It could not say
        /// why, and the two reasons want different code:
        ///
        ///   the entity has a PARENT and the engine recomposes global = parent *
        ///   local each frame - then a global write is overwritten by
        ///   construction, a local write is preserved by construction, and the fix
        ///   is weapon_entity_write = local with no seam hunting at all.
        ///
        ///   the entity is parentless and something writes its frame from the hand
        ///   bone at render time - then no write from here survives whatever space
        ///   it is in, and the work moves to finding that seam.
        ///
        /// So this prints the parent, both frames, and the two candidate
        /// compositions checked against the global frame the engine currently
        /// holds. Once per hand; it is a description, not a monitor.
        /// </summary>
        private static void DescribeOnce(VrHandSlot slot, WeakGameEntity weapon)
        {
            int i = (int)slot.Role;

            if (_described[i])
                return;
            _described[i] = true;

            try
            {
                MatrixFrame global = weapon.GetGlobalFrame();
                MatrixFrame local = weapon.GetLocalFrame();
                WeakGameEntity parent = weapon.Parent;

                string parentText;
                float viaParent = -1f;

                if (parent.IsValid)
                {
                    MatrixFrame parentGlobal = parent.GetGlobalFrame();
                    viaParent = (parentGlobal.TransformToParent(local).origin - global.origin).Length;
                    parentText = "parent '" + (parent.Name ?? "?") + "'";
                }
                else
                {
                    parentText = "NO parent";
                }

                float viaBone = -1f;
                if (_haveAnimatedBone[i])
                {
                    MatrixFrame fromBone = _animatedBone[i].TransformToParent(_boneToWeapon[i]);
                    viaBone = (fromBone.origin - global.origin).Length;
                }

                VrLog.Info(string.Format(
                    "TEST 208 {0} weapon '{1}': {2}. global ({3:F2},{4:F2},{5:F2}), local "
                    + "({6:F2},{7:F2},{8:F2}). parent*local is {9:F3} m from global "
                    + "(-1 = no parent); animatedBone*binding is {10:F3} m from global "
                    + "(-1 = not known yet). The one near ZERO is what the engine is "
                    + "composing this entity from, and therefore where the write belongs: "
                    + "parent*local means weapon_entity_write = local fixes it.",
                    slot.Role, weapon.Name ?? "?", parentText,
                    global.origin.x, global.origin.y, global.origin.z,
                    local.origin.x, local.origin.y, local.origin.z,
                    viaParent, viaBone));
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 208: describing the weapon entity failed; nothing else "
                            + "is affected.", ex);
            }
        }

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
            // TEST 211: in weapon_attach = postintegrate the engine places the
            // weapon and a write from here would be a second, competing answer.
            if (!Enabled || _broken || !slot.Valid || !VrWeaponAttach.DriveEntity)
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

                // BEFORE this frame's write, so what is read is whatever the
                // engine left there after its own update. See TEST 207.
                // Described before it is written, while both frames are still the
                // engine's own answer. See TEST 208.
                DescribeOnce(slot, weapon);

                CheckSurvived(slot, weapon, handWorld);

                MatrixFrame target = Bind == "measured"
                    ? handWorld.TransformToParent(_boneToWeapon[i])
                    : handWorld;

                // The nudge, in the anchor bone's own frame, so "forward" means
                // along the hand however the hand is turned. Rotation first, then
                // the offset, so the two are independent to tune.
                if (_haveNudge)
                    target = target.TransformToParent(_nudge);

                // LOCAL, when the entity turns out to be parent-composed.
                //
                // If the engine rebuilds global = parent * local every frame, then
                // writing global is writing the output of a calculation it is
                // about to redo - which is precisely what TEST 207 measured. The
                // same target expressed as a local frame is preserved instead,
                // because the engine's own composition then produces it.
                if (Local)
                {
                    WeakGameEntity parent = weapon.Parent;
                    if (parent.IsValid)
                    {
                        MatrixFrame parentGlobal = parent.GetGlobalFrame();

                        // NonOrthogonal: an agent's entity frame carries scale, and
                        // the transpose is not its inverse. Same trap as the hand
                        // targets, same 28 cm.
                        MatrixFrame asLocal = parentGlobal.TransformToLocalNonOrthogonal(target);
                        asLocal.rotation.Orthonormalize();

                        weapon.SetLocalFrame(ref asLocal, isTeleportation: true);

                        _lastWritten[i] = target;
                        _haveLastWritten[i] = true;

                        LogModeOnce("local, against the entity's parent");
                        return;
                    }

                    // Asked for, but there is nothing to be local to. Fall through
                    // to the global write rather than silently doing nothing.
                    LogModeOnce("global - weapon_entity_write = local was asked for, but this "
                                + "entity has no parent to be local to");
                }

                // isTeleportation: this is not motion the physics or the
                // interpolator should smooth. It is where the weapon IS, decided
                // fresh every frame from a tracked pose, and asking the engine to
                // ease into it would add exactly the lag a hand notices.
                weapon.SetGlobalFrame(in target, isTeleportation: true);

                _lastWritten[i] = target;
                _haveLastWritten[i] = true;

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
        /// TEST 207: where the weapon actually is at the top of a frame, against
        /// where we left it at the end of the last one.
        ///
        /// Two numbers, because "the weapon is in the wrong place" has two causes
        /// here and they want opposite fixes:
        ///
        ///   HELD is this frame's reading against last frame's write. Near zero
        ///   means the entity is ours and the engine leaves it alone; the write
        ///   survives and anything still wrong is the binding or the hand. A large
        ///   value means the engine recomputed the entity from its parent bone
        ///   after we set it, and no amount of setting it harder will help - the
        ///   write has to move to a later seam, or stop going through a global
        ///   frame at all.
        ///
        ///   HAND is the same reading against where the controller is now. This is
        ///   what says which of the two places it landed in. A weapon that snapped
        ///   back to the animation sits near the animated hand, not near yours, so
        ///   a large HELD with a large HAND is the engine winning outright.
        ///
        /// Reported a handful of times per hand and then quiet. It is a
        /// measurement, not a monitor.
        /// </summary>
        private static void CheckSurvived(VrHandSlot slot, WeakGameEntity weapon,
                                          MatrixFrame handWorld)
        {
            int i = (int)slot.Role;

            if (!Survives || !_haveLastWritten[i] || _probeReports[i] >= ProbeReports)
                return;

            if (++_probeTick[i] < 90)
                return;
            _probeTick[i] = 0;
            _probeReports[i]++;

            MatrixFrame now = weapon.GetGlobalFrame();

            float held = (now.origin - _lastWritten[i].origin).Length;
            float hand = (now.origin - handWorld.origin).Length;

            VrLog.Info(string.Format(
                "TEST 207 {0} weapon: HELD {1:F3} m from where it was left last frame, "
                + "HAND {2:F3} m from the controller now. Near-zero HELD means the entity "
                + "is ours; a large one means the engine put it back from the bone and the "
                + "write needs a later seam.",
                slot.Role, held, hand));
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


        /// <summary>
        /// Puts the weapon back where it was last told to go, without recomputing
        /// anything.
        ///
        /// For the pre-display seam: the target was worked out from a tracked pose
        /// during the tick and nothing about it has changed since, so re-deriving
        /// it here would only introduce a second answer. This re-applies the one
        /// already agreed.
        /// </summary>
        public static void ReapplyLast(Agent agent, VrHandSlot slot)
        {
            int i = (int)slot.Role;

            if (!Enabled || _broken || !slot.Valid || !_haveLastWritten[i]
                || !VrWeaponAttach.DriveEntity)
                return;

            try
            {
                if (slot.Equipment == EquipmentIndex.None)
                    return;

                WeakGameEntity weapon = agent.GetWeaponEntityFromEquipmentSlot(slot.Equipment);
                if (!weapon.IsValid)
                    return;

                MatrixFrame target = _lastWritten[i];
                weapon.SetGlobalFrame(in target, isTeleportation: true);
            }
            catch (Exception)
            {
                // The tick's own write already reported anything worth reporting.
            }
        }

        /// <summary>
        /// TEST 209: where the weapon ended up after the renderer data was
        /// refreshed, against where we want it.
        ///
        /// A different question from TEST 207, and the reason that one has stopped
        /// being useful. 207 asks "did OUR WRITE survive", and the answer is no in
        /// both spaces - global and local are each undone before the next frame.
        /// So writing the answer is finished as an approach.
        ///
        /// This asks the opposite thing: if the engine is going to recompute the
        /// weapon from the hand bone no matter what, and the hand bone is ALREADY
        /// ours - the skinned hand demonstrably follows the controller - then the
        /// fix is not to write the weapon at all but to make the engine redo its
        /// own sum after our bone write instead of before it. That is what
        /// LazyUpdateAgentRendererData is being asked to do here, exactly as
        /// UpdateEntitialFramesFromLocalFrames is asked to redo the bone frames.
        ///
        /// So: near zero means the refresh re-derived the weapon from our posed
        /// bones and the weapon is on the controller. Still over a metre means the
        /// refresh reads a snapshot taken earlier in the frame and the weapon
        /// cannot be reached from here at all.
        /// </summary>
        public static void CheckAfterRefresh(Agent agent, VrHandSlot slot)
        {
            int i = (int)slot.Role;

            if (!slot.Valid || !_haveLastWritten[i] || _refreshReports[i] >= ProbeReports)
                return;

            if (++_refreshTick[i] < 90)
                return;
            _refreshTick[i] = 0;
            _refreshReports[i]++;

            try
            {
                if (slot.Equipment == EquipmentIndex.None)
                    return;

                WeakGameEntity weapon = agent.GetWeaponEntityFromEquipmentSlot(slot.Equipment);
                if (!weapon.IsValid)
                    return;

                float miss = (weapon.GetGlobalFrame().origin - _lastWritten[i].origin).Length;

                VrLog.Info(string.Format(
                    "TEST 209 {0} weapon: {1:F3} m from where it should be, measured AFTER the "
                    + "renderer refresh in the same frame. Near zero means the engine re-derived "
                    + "it from our posed bones and the weapon is on the controller; over a metre "
                    + "means the refresh reads an earlier snapshot and this entity cannot be "
                    + "reached from this seam.",
                    slot.Role, miss));
            }
            catch (Exception)
            {
                // Diagnostics only.
            }
        }
        /// <summary>Per-mission. The binding belongs to one set of equipment on one
        /// skeleton, and the next mission measures its own.</summary>
        public static void Reset()
        {
            for (int i = 0; i < Roles; i++)
            {
                _haveBinding[i] = false;
                _haveLastWritten[i] = false;
                _probeTick[i] = 0;
                _probeReports[i] = 0;
                _described[i] = false;
                _haveAnimatedBone[i] = false;
                _refreshTick[i] = 0;
                _refreshReports[i] = 0;
            }

            _loggedDriving = false;
            _loggedMode = false;
            _loggedVerdict = false;
        }
    }
}
