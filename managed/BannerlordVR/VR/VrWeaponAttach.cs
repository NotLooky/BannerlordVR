using System;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 211. Stop writing the weapon's frame; hand the placement back to the
    /// engine at a seam the engine owns.
    ///
    /// WHY THE PREVIOUS FOUR ROUNDS RAN OUT
    ///
    /// TESTs 207 to 210 are a clean ladder and every rung held:
    ///
    ///   207  a global write does not survive the frame. HELD 1.5 to 2.1 m.
    ///   208  the entity is parented; parent*local reproduces global to 0.000 m.
    ///   208b a local write is no better. HELD 1.3 to 2.3 m.
    ///   209  after LazyUpdateAgentRendererData the weapon is 0.000 m from the
    ///        target - and the frame still draws it in the vanilla spot.
    ///   210  OnPreDisplayMissionTick really is per-frame (31/66/71 calls in a
    ///        second against a heartbeat ramping 34.5 to 90.0 fps), and the weapon
    ///        is STILL wrong there: HELD 0.39 to 0.89 m main, 1.93 to 3.92 m off.
    ///
    /// Four seams, each later than the last, all beaten. At that point "write it
    /// later still" has stopped being a theory with anywhere to go.
    ///
    /// THE LINE IN TEST 208's OWN LOG THAT WAS NEVER READ
    ///
    ///     Main 'wpn_batched_meshes_1': global (407.04,777.91,17.33) local (-0.22,0.09,1.90)
    ///     Off  'wpn_kite_shield_n':    global (406.84,777.75,15.31) local (0.00,0.00,0.00)
    ///
    /// The agent stands at about (407, 777.8, 15.3). So the shield's entity frame
    /// is identity-local, sitting at the agent's FEET, while the shield plainly
    /// draws up in the hand. That entity's frame is not the transform the shield is
    /// drawn from. It never was. Which means the problem was never WHEN the write
    /// lands - it is that the thing being written is not the thing being drawn, and
    /// no seam can fix that.
    ///
    /// WHAT THIS DOES INSTEAD
    ///
    /// MBAgentVisuals.SetAttachedPositionForMeshAfterAnimationPostIntegrate binds a
    /// child entity to a bone and lets the ENGINE place it, after the animation
    /// post-integrate step. The native name is
    /// set_attached_position_for_rope_entity_after_animation_post_integrate: it is
    /// what horse reins and ropes use, for exactly the reason we need it - a thing
    /// that must sit on a bone after the animation has finished moving that bone.
    ///
    /// That is the seam TESTs 207 to 210 were hunting, except the engine already
    /// owns it and does the arithmetic itself. We stop computing a target at all.
    ///
    /// IT DESCRIBES BEFORE IT ACTS
    ///
    /// The attach is a guess about which object the renderer reads, and this round
    /// is the fourth in a row to turn on that question. So the dump runs first and
    /// runs regardless of the mode: what is actually attached to the hand and item
    /// bones, and how far the entity the mod has been driving sits from the animated
    /// item bone. The dump is worth having whether or not the attach works, which is
    /// the whole reason it is not gated behind the attach.
    ///
    /// A CORRECTION THIS FILE OWES, HAVING CRASHED THE GAME ONCE
    ///
    /// The first version walked MBAgentVisuals.GetAttachedWeaponEntity(0..3) looking
    /// for a second candidate entity, on the guess that an index the engine did not
    /// have would simply return null. It does not: the accessor indexes native
    /// memory unchecked, and on an agent with nothing attached it dereferenced null
    /// and took the process down on entry to a custom battle - ACCESS VIOLATION
    /// reading address 0, on the AfterMissionTick seam. A managed try/catch cannot
    /// catch that, so the catch block around it was worth nothing.
    ///
    /// It was also the wrong question. Agent._attachedWeapons is the list of
    /// javelins and arrows stuck INTO an agent, not the item in its hand, so that
    /// list was never a route to the wielded weapon. It is still printed, bounded by
    /// Agent.GetAttachedWeaponsCount, because ruling it out in writing costs
    /// nothing and leaving it open would invite another round to go looking.
    /// </summary>
    public static class VrWeaponAttach
    {
        /// <summary>
        /// How the weapon is placed. "postintegrate" hands it to the engine's own
        /// after-animation attach; "entity" is the TEST 207-210 behaviour of
        /// writing the frame ourselves; "off" leaves the weapon to the game.
        ///
        /// A string rather than a bool because there are three real states here and
        /// two of them need to be comparable in one session without a rebuild.
        /// </summary>
        private static readonly string Mode =
            VrConfig.String("weapon_attach", "entity").Trim().ToLowerInvariant();

        /// <summary>True when the engine should be doing the placing.</summary>
        public static bool PostIntegrate => Mode == "postintegrate";

        /// <summary>True when VrWeaponEntity should still write the frame itself.
        /// Only in "entity" mode: in "postintegrate" a write would be a second
        /// answer competing with the engine's, and in "off" there is no answer.</summary>
        public static bool DriveEntity => Mode == "entity";

        /// <summary>
        /// TEST 215. Drive EVERY equipment child entity to the hand, not just the
        /// one GetWeaponEntityFromEquipmentSlot returns.
        ///
        /// The census closed every other door: 44 skeleton meshes and not one of
        /// them is equipment, no bone components on any hand bone, no metameshes on
        /// the agent entity. The drawn weapon is one of the four child entities,
        /// because there is nowhere else left for it to be.
        ///
        /// And three of those four are called 'wpn_batched_meshes_1' - the same
        /// name, three different pointers. The mod has only ever written to ONE of
        /// them, whichever the equipment-slot accessor hands back. If the wielded
        /// weapon is drawn from a different one, then every result from TEST 207
        /// onwards is explained by a single wrong pointer, and no amount of seam
        /// hunting or engine-side attaching was ever going to help.
        ///
        /// So this stops being careful and writes all four. It is deliberately crude
        /// - it will stack the shield and the spare weapons in the same place - and
        /// crude is the point: whichever piece of equipment visibly jumps to your
        /// hand is the one whose entity frame the renderer reads. If NOTHING moves,
        /// then entity frames are not the draw path at all and the answer is not on
        /// the managed side.
        ///
        /// One session, one unambiguous answer, and it needs no theory about which
        /// pointer is which.
        /// </summary>
        public static bool DriveAllChildren => Mode == "allchildren";

        /// <summary>Whether the one-shot structural dump runs. On: it is the only
        /// part of this round that produces information even if the attach is
        /// wrong.</summary>
        private static readonly bool Describe = VrConfig.Bool("weapon_attach_describe", true);

        /// <summary>How many attached-weapon slots to walk when looking for the
        /// entity the renderer actually reads. Four covers both hands with room
        /// for a quiver and a spare.</summary>
        private const int AttachedSlots = 4;

        private const int Roles = 2;

        private static readonly bool[] _described = new bool[Roles];
        private static bool _broken;
        private static bool _loggedMode;

        /// <summary>TEST 212. Whether to walk every mesh and child entity looking
        /// for the object that is actually drawn in the hand. One-shot per hand.</summary>
        private static readonly bool DoCensus = VrConfig.Bool("weapon_census", true);

        /// <summary>How many entries any one census list will print. A guard
        /// against a very long list filling the log, not a correctness limit.</summary>
        private static readonly int CensusCap = VrConfig.Int("weapon_census_cap", 40);

        private static readonly bool[] _censused = new bool[Roles];

        /// <summary>TEST 213. How many timed samples per hand before going quiet.
        /// Enough for the player to move a hand between them and for a constant
        /// distance to be told from a varying one.</summary>
        private static readonly int TrackReports = VrConfig.Int("weapon_track_reports", 6);

        private static readonly int[] _trackTick = new int[Roles];
        private static readonly int[] _trackReports = new int[Roles];

        /// <summary>TEST 214. How many children to carry across the write. Four is
        /// what this agent has; eight leaves room without making the arrays a
        /// consideration.</summary>
        private const int TrackChildren = 8;

        private static readonly bool[] _beforeValid = new bool[Roles];
        private static readonly int[] _beforeCount = new int[Roles];
        private static readonly sbyte[] _beforeBone = new sbyte[Roles];
        private static readonly MatrixFrame[] _beforeItem = new MatrixFrame[Roles];
        private static readonly UIntPtr[,] _beforePtr = new UIntPtr[Roles, TrackChildren];
        private static readonly string[,] _beforeName = new string[Roles, TrackChildren];
        private static readonly MatrixFrame[,] _beforeFrame =
            new MatrixFrame[Roles, TrackChildren];

        /// <summary>How many times per hand Measure reports before going quiet.
        /// Matched to weapon_entity_probe_reports so the two scoreboards are read
        /// side by side over the same stretch of play.</summary>
        private static readonly int MeasureReports =
            VrConfig.Int("weapon_entity_probe_reports", 8);

        private static readonly int[] _measureTick = new int[Roles];
        private static readonly int[] _measureReports = new int[Roles];

        // What each hand is currently attached with, so a weapon swap re-registers
        // rather than leaving the old entity bound. Keyed on the equipment slot AND
        // the entity pointer: swapping to a different weapon in the same slot is a
        // new entity, and re-equipping the same one can reuse the pointer.
        private static readonly EquipmentIndex[] _armedSlot = new EquipmentIndex[Roles];
        private static readonly UIntPtr[] _armedEntity = new UIntPtr[Roles];
        private static readonly sbyte[] _armedBone = new sbyte[Roles];
        private static readonly bool[] _armed = new bool[Roles];

        /// <summary>
        /// Binds one hand's weapon to its item bone and lets the engine place it.
        ///
        /// Called every frame but does work only when something changed - the
        /// binding is a registration, not a per-frame write, and re-registering the
        /// same entity on the same bone every frame would be asking the engine to
        /// redo bookkeeping ninety times a second for no reason.
        /// </summary>
        public static void Arm(Agent agent, MBAgentVisuals visuals, Skeleton skeleton,
                               VrHandSlot slot)
        {
            if (!PostIntegrate || _broken || !slot.Valid || visuals == null)
                return;

            int i = (int)slot.Role;

            try
            {
                if (slot.Equipment == EquipmentIndex.None)
                {
                    // Empty hand. Forget the binding so the next weapon re-arms
                    // rather than being mistaken for the one already attached.
                    _armed[i] = false;
                    return;
                }

                WeakGameEntity weapon = agent.GetWeaponEntityFromEquipmentSlot(slot.Equipment);
                if (!weapon.IsValid)
                    return;

                sbyte bone = ItemBone(agent, visuals, skeleton, slot);
                if (bone < 0)
                    return;

                if (_armed[i] && _armedSlot[i] == slot.Equipment
                    && _armedEntity[i] == weapon.Pointer && _armedBone[i] == bone)
                    return;

                visuals.SetAttachedPositionForMeshAfterAnimationPostIntegrate(weapon, bone);

                _armed[i] = true;
                _armedSlot[i] = slot.Equipment;
                _armedEntity[i] = weapon.Pointer;
                _armedBone[i] = bone;

                VrLog.Info(string.Format(
                    "TEST 211 {0}: '{1}' attached to bone [{2}] {3}, to be placed by the engine "
                    + "after the animation post-integrate. We no longer write its frame at all. "
                    + "Watch TEST 207's HAND number: near zero means the engine is placing it on "
                    + "the controller and this is the fix; unchanged from the 0.78-0.92 m main / "
                    + "1.31-2.44 m off baseline means the renderer does not read this entity and "
                    + "the dump above says what it reads instead.",
                    slot.Role, weapon.Name ?? "?", bone, BoneName(skeleton, bone)));

                LogModeOnce();
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("TEST 211: the post-integrate attach failed; weapons go back to the "
                            + "game's own animation. Nothing else is affected.", ex);
            }
        }

        /// <summary>
        /// The bone a weapon hangs from: r_finger0 on a human, in the palm.
        ///
        /// The arm chain already resolves it from the monster definition, which is
        /// the same source VrHandSlots reads, so this asks that rather than
        /// repeating the lookup. The wrist is the fallback: it is one joint out and
        /// visibly wrong for a sword, but it is a bone that certainly exists, and a
        /// weapon on the wrist is a better failure than no weapon at all.
        /// </summary>
        private static sbyte ItemBone(Agent agent, MBAgentVisuals visuals, Skeleton skeleton,
                                      VrHandSlot slot)
        {
            if (VrArmBones.TryChainForHand(agent, visuals, skeleton, slot.Bone,
                                           out VrArmChain chain)
                && chain.Valid && chain.Item >= 0)
                return chain.Item;

            return slot.Bone;
        }

        /// <summary>
        /// What is actually attached to this hand, once per hand per mission.
        ///
        /// IT LOGS EACH STAGE AS IT COMPLETES, AND THAT IS NOT DECORATION
        ///
        /// The first version of this crashed the game on entry to a custom battle,
        /// for the reason recorded on the class above. The lesson encoded here is
        /// that each reading is written to the log the moment it succeeds, instead
        /// of being assembled into one line at the end. If some other call in here
        /// turns out to be unsafe, the last line on disk names the stage that
        /// survived and the next call is the culprit. A crash that says where it
        /// happened costs one session; one that does not costs several.
        ///
        /// WHAT THE READINGS ARE WORTH
        ///
        ///   BONE COMPONENTS on the wrist and item bones are the real signal. A
        ///   weapon that is a bone component is placed by the skeleton, and our
        ///   posed bone already moves it - which would make the entity approach the
        ///   wrong KIND of fix rather than a mistimed one.
        ///
        ///   THE DRIVEN ENTITY's distance to the animated item bone says whether the
        ///   thing TESTs 207-210 were writing tracks the hand at all. The shield's
        ///   did not; it sat at the agent's feet.
        ///
        ///   ATTACHED WEAPONS are reported as the weak lead they are. See the class.
        /// </summary>
        public static void DescribeOnce(Agent agent, MBAgentVisuals visuals, Skeleton skeleton,
                                        MatrixFrame agentGlobal, VrHandSlot slot)
        {
            if (!Describe || _broken || !slot.Valid || visuals == null || skeleton == null)
                return;

            int i = (int)slot.Role;
            if (_described[i])
                return;
            _described[i] = true;

            try
            {
                sbyte item = ItemBone(agent, visuals, skeleton, slot);

                // The item bone in world space, this frame, straight off the
                // animation. Every distance below is measured against it, because
                // that is where a correctly-placed weapon would be.
                MatrixFrame itemWorld = agentGlobal.TransformToParent(
                    skeleton.GetBoneEntitialFrameWithIndex(item));

                // One line per bone, so a fault in the second still leaves the
                // first on disk.
                LogBoneComponents(skeleton, slot, slot.Bone, "wrist");
                LogBoneComponents(skeleton, slot, item, "item");

                WeakGameEntity driven = slot.Equipment == EquipmentIndex.None
                    ? default(WeakGameEntity)
                    : agent.GetWeaponEntityFromEquipmentSlot(slot.Equipment);

                if (driven.IsValid)
                {
                    VrLog.Info(string.Format(
                        "TEST 211 {0} driven entity: '{1}' ({2:X}), {3:F3} m from the animated "
                        + "item bone. Near zero means this entity does track the hand and the "
                        + "write was reaching the right object; a large value means it does not "
                        + "move with the animation at all, which is what the shield's "
                        + "identity-local frame at the agent's feet already showed.",
                        slot.Role, driven.Name ?? "?", driven.Pointer.ToUInt64(),
                        (driven.GetGlobalFrame().origin - itemWorld.origin).Length));
                }
                else
                {
                    VrLog.Info("TEST 211 " + slot.Role + " driven entity: none - this hand holds "
                               + "no equipment, so there is nothing for the mod to place.");
                }

                LogAttachedWeapons(agent, visuals, slot, itemWorld, driven);
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 211: describing the hand's attachments failed. Diagnostics "
                            + "only; nothing else is affected.", ex);
            }
        }

        /// <summary>One bone's components, on its own log line.</summary>
        private static void LogBoneComponents(Skeleton skeleton, VrHandSlot slot, sbyte bone,
                                              string which)
        {
            var text = new StringBuilder();
            text.Append("TEST 211 ").Append(slot.Role).Append(' ').Append(which)
                .Append(" bone [").Append(bone).Append("] ").Append(BoneName(skeleton, bone))
                .Append(" components: ");

            AppendBoneComponents(text, skeleton, bone);

            text.Append(". A weapon listed here is placed by the SKELETON, which our posed bone "
                        + "already moves - so it would mean the entity approach was the wrong "
                        + "kind of fix rather than a mistimed one. None here means the weapon is "
                        + "not a bone component and the renderer reads it from somewhere else.");

            VrLog.Info(text.ToString());
        }

        /// <summary>
        /// The agent's attached weapons, walked against Agent.GetAttachedWeaponsCount.
        ///
        /// The count is the whole point. MBAgentVisuals.GetAttachedWeaponEntity
        /// indexes native memory without checking, so asking it for an index the
        /// agent does not have reads a null pointer and takes the process with it.
        /// That is not hypothetical - it is how the first version of this file
        /// crashed on entry to a custom battle.
        /// </summary>
        private static void LogAttachedWeapons(Agent agent, MBAgentVisuals visuals,
                                               VrHandSlot slot, MatrixFrame itemWorld,
                                               WeakGameEntity driven)
        {
            int count;
            try
            {
                count = agent.GetAttachedWeaponsCount();
            }
            catch (Exception)
            {
                return;
            }

            if (count <= 0)
            {
                VrLog.Info("TEST 211 " + slot.Role + " attached weapons: none. That list is "
                           + "javelins and arrows stuck INTO the agent, not the item in its "
                           + "hand, so empty is the normal case and it rules this out as a route "
                           + "to the wielded weapon.");
                return;
            }

            var text = new StringBuilder();
            text.Append("TEST 211 ").Append(slot.Role).Append(" attached weapons: ")
                .Append(count).Append(" - ");

            for (int s = 0; s < count && s < AttachedSlots; s++)
            {
                GameEntity attached;
                try
                {
                    attached = visuals.GetAttachedWeaponEntity(s);
                }
                catch (Exception)
                {
                    text.Append('[').Append(s).Append("] unreadable; ");
                    continue;
                }

                if (attached == null || attached.Pointer == UIntPtr.Zero)
                {
                    text.Append('[').Append(s).Append("] null; ");
                    continue;
                }

                text.Append(string.Format(
                    "[{0}] '{1}' ({2:X}) {3:F3} m{4}; ",
                    s, attached.Name ?? "?", attached.Pointer.ToUInt64(),
                    (attached.GetGlobalFrame().origin - itemWorld.origin).Length,
                    driven.IsValid && attached.Pointer == driven.Pointer
                        ? " SAME as the driven entity" : ""));
            }

            text.Append("These are projectiles stuck into the agent rather than the wielded "
                        + "item, so a match with the driven entity would be the surprise here, "
                        + "not the expectation.");

            VrLog.Info(text.ToString());
        }

        /// <summary>
        /// TEST 212. Find the drawn weapon by measuring everything against the hand.
        ///
        /// WHAT TEST 211 SETTLED, AND WHY THAT LEAVES ONLY THIS
        ///
        /// The 211 dump ruled out both remaining candidates in one session:
        ///
        ///   r_hand, r_finger0, l_hand, l_finger0 all report ZERO bone components.
        ///   The weapon is not attached to the skeleton as a component.
        ///
        ///   the entity the mod has driven since TEST 207 sits 0.710 m (main) and
        ///   2.123 m (off) from the ANIMATED item bone. It does not track the hand
        ///   even when nobody is writing to it. TEST 208 already showed why: the
        ///   shield's local frame is identity against a parent at the agent's
        ///   origin, so that entity is parked at the agent's feet permanently.
        ///
        /// And the post-integrate attach changed nothing - 0.66 to 0.85 m main,
        /// 1.54 to 2.46 m off, indistinguishable from the baseline. Which makes
        /// sense: binding an entity that is not drawn to a bone does not make it
        /// drawn.
        ///
        /// So the drawn weapon is a third object nobody has enumerated, and five
        /// rounds have now been spent guessing at its identity. This stops guessing.
        ///
        /// HOW IT FINDS IT
        ///
        /// A correctly-drawn weapon is IN THE HAND, so it is within a few
        /// centimetres of the animated item bone. That is a measurement every
        /// candidate can be put through, so this walks all three places an object
        /// can hide and prints each one's distance:
        ///
        ///   the meshes on the player's SKELETON      (Skeleton.GetAllMeshes)
        ///   the CHILD ENTITIES of the agent entity   (GameEntity.GetChildren)
        ///   the METAMESHES on the agent entity       (GameEntity.GetMetaMesh)
        ///
        /// Whatever comes back near zero is what the renderer is drawing, and the
        /// round after this one moves that object instead of arguing about it.
        ///
        /// Every walk is bounded by its own count and every stage logs as it
        /// completes - see the class note about the crash that taught both.
        /// </summary>
        public static void Census(Agent agent, MBAgentVisuals visuals, Skeleton skeleton,
                                  GameEntity entity, MatrixFrame agentGlobal, VrHandSlot slot)
        {
            if (!DoCensus || _broken || !slot.Valid || skeleton == null || entity == null)
                return;

            int i = (int)slot.Role;
            if (_censused[i])
                return;
            _censused[i] = true;

            try
            {
                sbyte item = ItemBone(agent, visuals, skeleton, slot);
                MatrixFrame itemWorld = agentGlobal.TransformToParent(
                    skeleton.GetBoneEntitialFrameWithIndex(item));

                VrLog.Info(string.Format(
                    "TEST 212 {0}: hunting the drawn weapon. The animated item bone [{1}] {2} is "
                    + "at ({3:F2},{4:F2},{5:F2}) and the agent entity is at ({6:F2},{7:F2},{8:F2}). "
                    + "Everything below is measured from the item bone; a weapon that is drawn in "
                    + "the hand is a few centimetres from it, and that is the object to move.",
                    slot.Role, item, BoneName(skeleton, item),
                    itemWorld.origin.x, itemWorld.origin.y, itemWorld.origin.z,
                    agentGlobal.origin.x, agentGlobal.origin.y, agentGlobal.origin.z));

                CensusSkeletonMeshes(skeleton, slot);
                CensusChildEntities(entity, slot, itemWorld);
                CensusSlots(agent, slot, itemWorld);
                CensusMetaMeshes(entity, slot);
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 212: the census failed. Diagnostics only; nothing else is "
                            + "affected.", ex);
            }
        }

        /// <summary>The meshes hanging on the player's skeleton, by name. If the
        /// weapon is here it is a skeleton mesh, and our posed bone plus the
        /// entitial rebuild should already be moving it - which would make the
        /// question why it does not, rather than where it is.</summary>
        /// <summary>
        /// Words that mark a mesh as equipment rather than body or armour.
        ///
        /// A list rather than a "wpn_" prefix test, because the FIRST run of this
        /// census capped its output at 40 entries and printed forty LODs of heads,
        /// beards and plate armour - which looked like a clean negative result and
        /// was nothing of the sort. The weapon may simply be further down the list.
        /// So now the whole list is printed, and the interesting names are pulled
        /// out first so the answer does not depend on anyone reading to the end.
        /// </summary>
        private static readonly string[] WeaponWords =
        {
            "wpn", "sword", "shield", "axe", "spear", "lance", "bow", "mace",
            "javelin", "polearm", "dagger", "glaive", "pike", "hammer", "knife",
            "arrow", "quiver", "falx", "menavlion", "billhook", "scabbard", "sheath",
        };

        private static void CensusSkeletonMeshes(Skeleton skeleton, VrHandSlot slot)
        {
            var names = new System.Collections.Generic.List<string>();
            var interesting = new System.Collections.Generic.List<string>();
            bool truncated = false;

            try
            {
                foreach (Mesh mesh in skeleton.GetAllMeshes())
                {
                    if (mesh == null)
                        continue;

                    string name = mesh.Name ?? "?";
                    names.Add(name);

                    string lower = name.ToLowerInvariant();
                    for (int w = 0; w < WeaponWords.Length; w++)
                    {
                        if (lower.IndexOf(WeaponWords[w], StringComparison.Ordinal) >= 0)
                        {
                            Vec3 o = mesh.GetLocalFrame().origin;
                            interesting.Add(string.Format("'{0}' local ({1:F2},{2:F2},{3:F2})",
                                                          name, o.x, o.y, o.z));
                            break;
                        }
                    }

                    // Only a runaway guard now, an order of magnitude above any
                    // plausible mesh count. It is no longer doing the work the old
                    // cap of 40 was accidentally doing.
                    if (names.Count >= 2000)
                    {
                        truncated = true;
                        break;
                    }
                }
            }
            catch (Exception)
            {
                truncated = true;
            }

            VrLog.Info(string.Format(
                "TEST 212 {0} skeleton meshes: {1} total{2}. Equipment-shaped names among them: "
                + "{3}. If a weapon appears here, it IS a skeleton mesh - and the question flips "
                + "to why the entitial rebuild does not carry it, because our posed bone "
                + "demonstrably moves the skinned hand. If none appears, the renderer draws the "
                + "weapon from somewhere off the skeleton entirely.",
                slot.Role, names.Count, truncated ? " (enumeration stopped early)" : "",
                interesting.Count == 0 ? "NONE" : string.Join("; ", interesting.ToArray())));

            // The full list, in chunks, because the answer to "is it here" is worth
            // more than a tidy log and the last attempt at tidiness hid it.
            var chunk = new StringBuilder();
            int part = 0;

            for (int n = 0; n < names.Count; n++)
            {
                chunk.Append(names[n]).Append("; ");

                if (chunk.Length < 1200 && n < names.Count - 1)
                    continue;

                part++;
                VrLog.Info("TEST 212 " + slot.Role + " skeleton mesh names, part " + part + ": "
                           + chunk);
                chunk.Length = 0;
            }
        }

        /// <summary>The agent entity's children, with each one's distance to the
        /// hand. This is where a wielded weapon is most likely to be.</summary>
        private static void CensusChildEntities(GameEntity entity, VrHandSlot slot,
                                                MatrixFrame itemWorld)
        {
            var text = new StringBuilder();
            text.Append("TEST 212 ").Append(slot.Role).Append(" child entities: ");

            int n = 0;
            try
            {
                foreach (GameEntity child in entity.GetChildren())
                {
                    if (child == null || child.Pointer == UIntPtr.Zero)
                        continue;

                    if (n >= CensusCap)
                    {
                        text.Append("... (capped at ").Append(CensusCap).Append(") ");
                        break;
                    }

                    n++;
                    text.Append(string.Format(
                        "'{0}' ({1:X}) {2:F3} m, {3} grandchild(ren); ",
                        child.Name ?? "?", child.Pointer.ToUInt64(),
                        (child.GetGlobalFrame().origin - itemWorld.origin).Length,
                        child.ChildCount));
                }
            }
            catch (Exception)
            {
                text.Append("(enumeration stopped early) ");
            }

            if (n == 0)
                text.Append("none. ");

            text.Append("THE ONE NEAREST ZERO IS THE DRAWN WEAPON. If nothing here is close, the "
                        + "weapon is not a child of the agent entity and the skeleton-mesh list "
                        + "above is where it must be.");

            VrLog.Info(text.ToString());
        }

        /// <summary>
        /// Asks the census to run again. Called when what is held changes: the
        /// census is once per mission by default, and the symptom that needed it -
        /// a scabbard or quiver on the hand after a weapon switch - only exists
        /// AFTER a switch.
        /// </summary>
        public static void NoteHeldChanged()
        {
            for (int i = 0; i < Roles; i++)
                _censused[i] = false;
        }

        /// <summary>What GetWeaponEntityFromEquipmentSlot returns for each slot
        /// that holds an item, against the wielded indices. The drive fetches its
        /// entity by the wielded slot, so if that hands back a holster rather than
        /// the drawn weapon it shows up here as the wielded slot's entity sitting
        /// at the hip, or as the same pointer for two slots.
        ///
        /// ONLY SLOTS THAT HOLD AN ITEM, AND ONLY 0-3. The accessor is an
        /// unchecked native passthrough (bl2vr-native-accessors-unbounded): the
        /// first version of this asked every slot including the empty and extra
        /// ones and crashed the game on entering a battle. One line per slot,
        /// written before the next slot is touched, so the last line on disk names
        /// the one that survived.</summary>
        private static void CensusSlots(Agent agent, VrHandSlot slot, MatrixFrame itemWorld)
        {
            VrLog.Info("TEST 225 " + slot.Role + " slots: this hand's wielded slot = " + slot.Equipment
                       + ", main wielded = " + agent.GetPrimaryWieldedItemIndex()
                       + ", off wielded = " + agent.GetOffhandWieldedItemIndex());

            for (int k = 0; k < 4; k++)
            {
                EquipmentIndex idx = (EquipmentIndex)k;
                string item;
                try
                {
                    item = agent.Equipment[idx].Item?.StringId;
                }
                catch (Exception)
                {
                    continue;
                }

                if (item == null)
                    continue;

                WeakGameEntity e = agent.GetWeaponEntityFromEquipmentSlot(idx);
                if (!e.IsValid)
                {
                    VrLog.Info("TEST 225 " + slot.Role + " [" + idx + "] " + item + ": no entity");
                    continue;
                }

                VrLog.Info(string.Format("TEST 225 {0} [{1}] {2}: '{3}' ({4:X}) {5:F3} m from the item bone",
                    slot.Role, idx, item, e.Name ?? "?", e.Pointer.ToUInt64(),
                    (e.GetGlobalFrame().origin - itemWorld.origin).Length));

                SlotMeshes(e, slot, idx);
            }
        }

        /// <summary>The meshes inside one slot entity: metamesh names and frames,
        /// and the mesh names under each with their visibility mask. TEST 225
        /// showed the slot -> entity mapping is stable across draws, so the open
        /// question is what is INSIDE it: a scabbard and a blade as separate
        /// meshes would explain a holster on the hand with the blade elsewhere.
        /// Bounded by the component and mesh counts, per the native-accessor rule.</summary>
        private static void SlotMeshes(WeakGameEntity e, VrHandSlot slot, EquipmentIndex idx)
        {
            var text = new StringBuilder();
            text.Append("TEST 225 ").Append(slot.Role).Append(" [").Append(idx).Append("] meshes: visible=")
                .Append(e.GetVisibilityExcludeParents()).Append("; ");

            int count = e.MultiMeshComponentCount;
            for (int m = 0; m < count && m < 6; m++)
            {
                MetaMesh meta = e.GetMetaMesh(m);
                if (meta == null)
                {
                    text.Append('[').Append(m).Append("] null; ");
                    continue;
                }

                Vec3 o = meta.Frame.origin;
                text.Append(string.Format("'{0}' frame ({1:F2},{2:F2},{3:F2}) meshes:",
                                          meta.GetName() ?? "?", o.x, o.y, o.z));

                int meshes = meta.MeshCount;
                for (int k = 0; k < meshes && k < 8; k++)
                {
                    Mesh mesh = meta.GetMeshAtIndex(k);
                    if (mesh == null)
                        continue;
                    Vec3 mo = mesh.GetLocalFrame().origin;
                    text.Append(string.Format(" {0}(mask {1}, at {2:F2},{3:F2},{4:F2})",
                                              mesh.Name ?? "?", mesh.VisibilityMask, mo.x, mo.y, mo.z));
                }

                text.Append("; ");
            }

            VrLog.Info(text.ToString());
        }
        /// <summary>The metameshes directly on the agent entity.</summary>
        private static void CensusMetaMeshes(GameEntity entity, VrHandSlot slot)
        {
            var text = new StringBuilder();
            text.Append("TEST 212 ").Append(slot.Role).Append(" agent metameshes: ");

            int count;
            try
            {
                count = entity.MultiMeshComponentCount;
            }
            catch (Exception)
            {
                return;
            }

            if (count <= 0)
            {
                text.Append("none.");
                VrLog.Info(text.ToString());
                return;
            }

            for (int m = 0; m < count && m < CensusCap; m++)
            {
                try
                {
                    MetaMesh meta = entity.GetMetaMesh(m);
                    if (meta == null)
                    {
                        text.Append('[').Append(m).Append("] null; ");
                        continue;
                    }

                    Vec3 o = meta.Frame.origin;
                    text.Append(string.Format("'{0}' frame ({1:F2},{2:F2},{3:F2}); ",
                                              meta.GetName() ?? "?", o.x, o.y, o.z));
                }
                catch (Exception)
                {
                    text.Append('[').Append(m).Append("] unreadable; ");
                }
            }

            VrLog.Info(text.ToString());
        }

        /// <summary>
        /// TEST 214. Does our bone write move any of these entities at all?
        ///
        /// WHAT TEST 213 ACTUALLY MEASURED, WHICH WAS NOT WHAT IT SAID
        ///
        /// 213 sampled each child's distance to the item bone six times and told the
        /// reader "the bone moves with the controller, so a constant distance means
        /// rigidly attached". Both halves of that are wrong, and the numbers it
        /// produced cannot be read the way it asked.
        ///
        /// It ran BEFORE the pose write and before the entitial rebuild, so the bone
        /// it read was the ANIMATION's, not the controller's. And distance is a
        /// scalar: an object can hold its distance while rotating around the bone,
        /// or drift in and out while genuinely attached, because an entity's origin
        /// is its mesh pivot. The samples came back at 0.58 to 0.79 m for the three
        /// batched entities - too steady to be parked in the world, too loose
        /// (0.12 m of spread) to be rigidly bound. That is a measurement that cannot
        /// decide anything, which is the worst kind.
        ///
        /// THE MEASUREMENT THAT DOES DECIDE
        ///
        /// Take each child's global frame TWICE in the same frame: once before the
        /// pose write, once after UpdateEntitialFramesFromLocalFrames. Then:
        ///
        ///   MOVED is the distance between those two readings. It is the whole
        ///   question in one number. If our bone write moves the entity, the engine
        ///   is deriving it from bones we own and the weapon is reachable. If MOVED
        ///   is zero every sample, nothing we do to the skeleton touches this object
        ///   and the search has to leave the agent entity entirely.
        ///
        ///   ANIM is the child's origin expressed IN THE ANIMATED BONE'S OWN FRAME,
        ///   and POSED the same thing in the posed bone's frame. A rigid attachment
        ///   makes one of those two exactly constant across samples - not roughly
        ///   constant, exactly - because that vector IS the binding. Whichever one
        ///   holds names the bone the engine is really using.
        ///
        /// A vector, not a distance, because a scalar cannot tell a fixed offset
        /// from a rotating one and that ambiguity is what made 213 unreadable.
        /// </summary>
        public static void TrackBefore(Agent agent, MBAgentVisuals visuals, Skeleton skeleton,
                                       GameEntity entity, MatrixFrame agentGlobal,
                                       VrHandSlot slot)
        {
            if (!DoCensus || _broken || !slot.Valid || skeleton == null || entity == null)
                return;

            int i = (int)slot.Role;
            _beforeValid[i] = false;

            if (_trackReports[i] >= TrackReports)
                return;

            if (++_trackTick[i] < 90)
                return;
            _trackTick[i] = 0;

            try
            {
                sbyte item = ItemBone(agent, visuals, skeleton, slot);

                _beforeBone[i] = item;
                _beforeItem[i] = agentGlobal.TransformToParent(
                    skeleton.GetBoneEntitialFrameWithIndex(item));

                int n = 0;
                foreach (GameEntity child in entity.GetChildren())
                {
                    if (child == null || child.Pointer == UIntPtr.Zero)
                        continue;
                    if (n >= TrackChildren)
                        break;

                    _beforePtr[i, n] = child.Pointer;
                    _beforeName[i, n] = child.Name ?? "?";
                    _beforeFrame[i, n] = child.GetGlobalFrame();
                    n++;
                }

                _beforeCount[i] = n;
                _beforeValid[i] = n > 0;
            }
            catch (Exception)
            {
                _beforeValid[i] = false;
            }
        }

        /// <summary>
        /// The other half of TEST 214, run after the entitial rebuild.
        ///
        /// Re-reads the same children and reports, per child, whether our write
        /// moved it and what its offset is in each of the two candidate bone frames.
        /// </summary>
        public static void TrackAfter(Agent agent, MBAgentVisuals visuals, Skeleton skeleton,
                                      GameEntity entity, MatrixFrame agentGlobal,
                                      VrHandSlot slot)
        {
            int i = (int)slot.Role;

            if (!_beforeValid[i] || _broken)
                return;
            _beforeValid[i] = false;

            try
            {
                _trackReports[i]++;

                MatrixFrame posedItem = agentGlobal.TransformToParent(
                    skeleton.GetBoneEntitialFrameWithIndex(_beforeBone[i]));

                MatrixFrame animItem = _beforeItem[i];

                var text = new StringBuilder();
                text.Append(string.Format(
                    "TEST 214 {0} sample {1}: the pose write moved the item bone "
                    + "{2:F3} m this frame. ",
                    slot.Role, _trackReports[i],
                    (posedItem.origin - animItem.origin).Length));

                int n = 0;
                foreach (GameEntity child in entity.GetChildren())
                {
                    if (child == null || child.Pointer == UIntPtr.Zero)
                        continue;
                    if (n >= _beforeCount[i])
                        break;

                    // Same order as the before pass, but verify rather than assume.
                    if (child.Pointer != _beforePtr[i, n])
                    {
                        n++;
                        continue;
                    }

                    MatrixFrame after = child.GetGlobalFrame();
                    MatrixFrame before = _beforeFrame[i, n];

                    Vec3 inAnim = animItem.TransformToLocalNonOrthogonal(before).origin;
                    Vec3 inPosed = posedItem.TransformToLocalNonOrthogonal(after).origin;

                    text.Append(string.Format(
                        "'{0}'({1:X4}) MOVED {2:F4} m, ANIM ({3:F3},{4:F3},{5:F3}), "
                        + "POSED ({6:F3},{7:F3},{8:F3}); ",
                        _beforeName[i, n], _beforePtr[i, n].ToUInt64() & 0xFFFF,
                        (after.origin - before.origin).Length,
                        inAnim.x, inAnim.y, inAnim.z,
                        inPosed.x, inPosed.y, inPosed.z));

                    n++;
                }

                text.Append("MOVED near zero on every child means our bone write reaches none of "
                            + "them and the drawn weapon is not derived from this skeleton. A "
                            + "vector that is EXACTLY repeated across samples is a rigid binding "
                            + "to that bone - ANIM repeating means the engine holds the weapon on "
                            + "the animation, which is the bug stated precisely.");

                VrLog.Info(text.ToString());
            }
            catch (Exception)
            {
                // Diagnostics only.
            }
        }

        /// <summary>
        /// TEST 215. Put every equipment child at the hand and see what moves.
        ///
        /// Runs after the entitial rebuild and after TEST 214 has taken its reading,
        /// so the measurement is of the ENGINE's behaviour and this write cannot
        /// contaminate it.
        ///
        /// Only the main hand drives, even though both hands call in: sending both
        /// sets of children to two different places at once would make "which one
        /// moved" unreadable, which is the single thing this test exists to answer.
        /// </summary>
        public static void DriveAll(Agent agent, MBAgentVisuals visuals, Skeleton skeleton,
                                    GameEntity entity, MatrixFrame agentGlobal, VrHandSlot slot)
        {
            if (!DriveAllChildren || _broken || !slot.Valid || entity == null
                || slot.Role != HandRole.Main)
                return;

            try
            {
                sbyte item = ItemBone(agent, visuals, skeleton, slot);
                MatrixFrame target = agentGlobal.TransformToParent(
                    skeleton.GetBoneEntitialFrameWithIndex(item));
                target.rotation.Orthonormalize();

                int n = 0;
                foreach (GameEntity child in entity.GetChildren())
                {
                    if (child == null || child.Pointer == UIntPtr.Zero)
                        continue;
                    if (n >= TrackChildren)
                        break;
                    n++;

                    child.SetGlobalFrame(in target, isTeleportation: true);
                }

                if (_loggedDriveAll)
                    return;

                _loggedDriveAll = true;
                VrLog.Info(string.Format(
                    "TEST 215: writing all {0} equipment child entities to the main hand every "
                    + "frame. Expect them stacked in one place - that is intended. LOOK AT YOUR "
                    + "HAND: whatever is now in it is drawn from its entity frame, and the mod "
                    + "was simply writing the wrong one of three identically-named children. If "
                    + "NOTHING is in your hand and everything is still in the vanilla spot, then "
                    + "entity frames are not the draw path and the answer is not on the managed "
                    + "side at all.", n));
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("TEST 215: driving all child entities failed; equipment goes back to "
                            + "the game. Nothing else is affected.", ex);
            }
        }

        private static bool _loggedDriveAll;

        /// <summary>
        /// TEST 216b. Stop the player's skeleton animating, because the player's own
        /// evidence says that is exactly the state in which this works.
        ///
        /// THE OBSERVATION THIS IS BUILT ON
        ///
        /// With weapon_attach = allchildren, PAUSED, every weapon and the shield sit
        /// in the hand - screenshotted, unambiguous, a sword and two spears all
        /// converging on the glove. Unpaused they snap back to the vanilla spot.
        ///
        /// That settles the biggest open question in the whole sequence: writing a
        /// child entity's global frame DOES move what the renderer draws. The object
        /// is right and the write works. The only thing wrong is that something
        /// re-derives those frames from the animation after every managed hook has
        /// run, and pausing is what stops it.
        ///
        /// So rather than keep hunting for a seam later than the last one, this
        /// reproduces the paused condition on ONE skeleton while the game runs.
        /// GameEntity.PauseSkeletonAnimation is public and does exactly that.
        ///
        /// WHAT IT COSTS, STATED UP FRONT
        ///
        /// The player's body stops animating. Legs will not walk, the torso will not
        /// lean, and any animation-driven motion of the character is gone. The arms
        /// are already driven by VrArmIk rather than the animation, so those keep
        /// working, and in first person the legs are rarely looked at - but this is
        /// a real cost and it is not a finished feature. It is the cleanest possible
        /// test of "is the animation re-derivation the thing overwriting us", and if
        /// it works it also narrows the real fix to that derivation.
        ///
        /// Off by default. It changes how the character looks and should never turn
        /// itself on.
        ///
        /// AND IT NOW ACTUALLY IS. The default read here was "pauseanim" for the
        /// whole of TESTs 216b to 218 - the paragraph above said off, the code
        /// said on, and the code is what ran. Every session with motion hands
        /// enabled froze the player's animation: legs not walking, torso not
        /// leaning, one static posture held for the length of the mission, in
        /// exchange for a weapon placement that TEST 218 established is the wrong
        /// object anyway. The hold is a diagnostic and it stays available, but it
        /// is one config line away rather than on by default.
        /// </summary>
        private static readonly string Hold =
            VrConfig.String("weapon_hold", "none").Trim().ToLowerInvariant();

        private static bool _frozen;

        /// <summary>
        /// What was actually held, so it can be let go of again.
        ///
        /// Remembered rather than re-resolved, because the release has to undo the
        /// hold on the SAME skeleton it was applied to and the caller may be
        /// switching motion hands off at a moment when the agent has already
        /// changed. A stale GameEntity is not safe to touch - the native
        /// accessors behind it are unbounded and a dead one faults rather than
        /// throwing - so the release is only ever performed on a pair handed in by
        /// a caller that has just resolved them from the live mission.
        /// </summary>
        private static bool _held;

        /// <summary>
        /// Pauses the player's skeleton animation once per mission.
        ///
        /// Reports what the engine says afterwards rather than assuming the call
        /// took, because a pause that silently did nothing and a pause that worked
        /// but changed nothing need completely different next steps.
        /// </summary>
        /// <summary>
        /// TEST 217b. Give the frozen skeleton back the clean start the animation
        /// used to provide, every frame, before anything is solved.
        ///
        /// WHY THIS IS REQUIRED AND NOT AN EXTRA
        ///
        /// weapon_hold = pauseanim worked for about a second and then the arm
        /// exploded into stretched black shards. That is not a mystery and it was
        /// not even unforeseen - VrArmSeam has been printing the warning all along:
        ///
        ///   "A healthy number here is NOT zero - the animation is supposed to
        ///    repose every bone, and that is what makes each frame start clean.
        ///    Near 100 percent unchanged means our writes accumulate."
        ///
        /// And with the animation paused the probe read exactly that: 0.00 mm of
        /// movement between frames, unchanged on 75 percent of them. The arm IK
        /// reads the current pose, solves, and writes; with nothing resetting the
        /// bones it is reading its own output and compounding it. The twist helpers
        /// wind up, the forearm stretches, and within a second the mesh is garbage.
        ///
        /// So pausing the animation is only half a mechanism. The other half is
        /// replacing what the pause removed: one captured pose, written back to
        /// every bone before the solve, so each frame starts from the same clean
        /// base the animation used to hand us. After that the IK is idempotent
        /// again - it solves from a fixed pose rather than from yesterday's answer.
        ///
        /// The pose is captured from the LIVE animation on the frame before the
        /// pause, so it is a real posture rather than the T-pose that
        /// Skeleton.ResetFrames would give.
        /// </summary>
        private const int MaxBones = 64;

        private static readonly MatrixFrame[] _frozenLocal = new MatrixFrame[MaxBones];
        private static int _frozenCount;
        private static bool _frozenCaptured;

        public static void HoldPose(Skeleton skeleton)
        {
            if (_broken || skeleton == null || Hold == "none")
                return;

            try
            {
                if (!_frozenCaptured)
                {
                    CaptureFrozenPose(skeleton);
                    return;
                }

                for (sbyte b = 0; b < _frozenCount; b++)
                    skeleton.SetBoneLocalFrame(b, _frozenLocal[b]);

                // Immediately, so everything read after this point - the root
                // capture, the arm chain, the IK's "where is the bone now" - sees
                // the frozen pose rather than last frame's solved one.
                skeleton.UpdateEntitialFramesFromLocalFrames();
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("TEST 217b: restoring the frozen pose failed; the hold is abandoned "
                            + "so the arm cannot wind up. Set weapon_hold = none.", ex);
            }
        }

        /// <summary>
        /// Records every bone's local frame from the live animation, once.
        ///
        /// Local frames are derived from the entitial ones rather than read
        /// directly, because Skeleton exposes no GetBoneLocalFrame - only the rest
        /// frame, which is a different pose. NonOrthogonal because an agent's bone
        /// frames carry scale and the transpose is not the inverse; that trap has
        /// already cost 28 cm once in this codebase.
        /// </summary>
        private static void CaptureFrozenPose(Skeleton skeleton)
        {
            int count = skeleton.GetBoneCount();
            if (count <= 0)
                return;

            if (count > MaxBones)
                count = MaxBones;

            for (sbyte b = 0; b < count; b++)
            {
                MatrixFrame mine = skeleton.GetBoneEntitialFrameWithIndex(b);
                sbyte parent = skeleton.GetParentBoneIndex(b);

                if (parent < 0)
                {
                    _frozenLocal[b] = mine;
                    continue;
                }

                MatrixFrame up = skeleton.GetBoneEntitialFrameWithIndex(parent);
                MatrixFrame local = up.TransformToLocalNonOrthogonal(mine);
                local.rotation.Orthonormalize();
                _frozenLocal[b] = local;
            }

            _frozenCount = count;
            _frozenCaptured = true;

            VrLog.Info(string.Format(
                "TEST 217b: captured a {0}-bone reference pose from the live animation. It is "
                + "written back to every bone each frame before the arm is solved, because "
                + "pausing the animation removes the per-frame reset the IK depends on - which "
                + "is what made the arm stretch into black shards the first time. VrArmSeam had "
                + "this in writing before it happened: near-100-percent-unchanged means our "
                + "writes accumulate.",
                count));
        }

        public static void FreezeIfAsked(GameEntity entity, Skeleton skeleton)
        {
            if (_broken || entity == null || Hold == "none")
                return;

            // uptodate is a dirty FLAG, not a mode, so it has to be re-asserted
            // every frame. The other two are one-shot state changes.
            if (Hold != "uptodate" && _frozen)
                return;

            try
            {
                string did;

                switch (Hold)
                {
                    case "pauseanim":
                        entity.PauseSkeletonAnimation();
                        _held = true;
                        did = "PauseSkeletonAnimation; engine reports paused = "
                              + entity.IsSkeletonAnimationPaused();
                        break;

                    case "freeze":
                        if (skeleton == null)
                            return;
                        skeleton.Freeze(true);
                        _held = true;
                        did = "Skeleton.Freeze(true); engine reports frozen = "
                              + skeleton.IsFrozen();
                        break;

                    case "uptodate":
                        if (skeleton == null)
                            return;
                        skeleton.SetUptoDate(true);
                        did = "Skeleton.SetUptoDate(true), re-asserted every frame";
                        break;

                    default:
                        _frozen = true;
                        VrLog.Warn("weapon_hold = '" + Hold + "' is not a value this understands. "
                                   + "Use none, pauseanim, freeze or uptodate. Nothing was done.");
                        return;
                }

                if (_frozen)
                    return;
                _frozen = true;

                VrLog.Info(string.Format(
                    "TEST 216b: holding the player's skeleton with {0}. The frame order is now "
                    + "established rather than assumed - AfterMissionTick runs AFTER "
                    + "TickAgentsAndTeams, so our write is ALREADY the last managed word and "
                    + "whatever overwrites it is native and runs at render time. This tries to "
                    + "stop that native pass for one skeleton, which is what the player's pause "
                    + "does by hand. If the weapons hold their place UNPAUSED, this is the "
                    + "mechanism. The body may stop animating - legs not walking is the expected "
                    + "cost of the test, not a bug. weapon_hold = none puts it back.",
                    did));
            }
            catch (Exception ex)
            {
                _frozen = true;
                VrLog.Error("TEST 216b: weapon_hold = " + Hold + " failed; the skeleton keeps "
                            + "animating. Nothing else is affected.", ex);
            }
        }

        /// <summary>
        /// Gives the skeleton back to the game's own animation.
        ///
        /// WHY THIS HAS TO EXIST SEPARATELY FROM Reset
        ///
        /// PauseSkeletonAnimation and Skeleton.Freeze are STATE ON THE ENGINE
        /// OBJECT, not on this class. Reset clears our flags and that is all it
        /// ever did, so switching motion hands off in the panel stopped us posing
        /// the hands and left the player's animation paused forever - one static
        /// posture, legs not walking, for the rest of the mission, with nothing in
        /// the panel that could undo it. The only way out was to restart the
        /// mission, which made "turn the experiment off" look like it had broken
        /// something worse.
        ///
        /// So the undo is a call, and it takes the entity and skeleton from the
        /// caller. It is never performed on a remembered pointer: the accessors
        /// behind GameEntity are unbounded native calls and a dead entity faults
        /// rather than throwing, so the pair must come from a caller that has just
        /// resolved them out of the live mission.
        ///
        /// Safe to call when nothing was ever held - it checks first and does
        /// nothing - so callers do not have to track whether the hold was on.
        /// </summary>
        public static void Release(GameEntity entity, Skeleton skeleton)
        {
            // The captured reference pose goes whatever happens. It is only ever
            // correct for the posture it was taken from, and leaving it behind
            // means a later hold writes back a pose from a previous mission.
            _frozenCaptured = false;
            _frozenCount = 0;

            if (!_held)
            {
                _frozen = false;
                return;
            }

            _held = false;
            _frozen = false;

            try
            {
                // Both, unconditionally, rather than switching on Hold. The mode
                // is read once at startup but the hold may have been applied under
                // a previous value in a config that was edited and reloaded, and
                // resuming an animation that is not paused is a no-op either way.
                if (entity != null)
                    entity.ResumeSkeletonAnimation();

                if (skeleton != null)
                    skeleton.Freeze(false);

                VrLog.Info("The player's skeleton animates itself again - "
                           + "PauseSkeletonAnimation undone, Skeleton.Freeze cleared. The "
                           + "bones are wherever the last solve left them for exactly one "
                           + "frame; the animation reposes every bone, which is the whole "
                           + "reason the hold needed a reference pose in the first place.");
            }
            catch (Exception ex)
            {
                VrLog.Error("Releasing the skeleton hold failed. The character may stay in "
                            + "one posture until the mission is restarted; nothing else is "
                            + "affected.", ex);
            }
        }

        /// <summary>
        /// The one number this round is judged on: how far the weapon is from the
        /// hand it is supposed to be in.
        ///
        /// Separate from TEST 207 on purpose. That probe measures a write against
        /// where the write put it, which is meaningless here - the whole point of
        /// postintegrate is that there is no write. This measures the weapon
        /// against the CONTROLLER, which is the thing the player is complaining
        /// about and the only reading that means the same in all three modes.
        ///
        /// Reported on the same 90-frame cadence as the other probes, and then
        /// quiet. It is a measurement, not a monitor.
        /// </summary>
        public static void Measure(Agent agent, VrHandSlot slot, MatrixFrame handWorld)
        {
            if (_broken || !slot.Valid || _measureReports[(int)slot.Role] >= MeasureReports)
                return;

            int i = (int)slot.Role;

            if (++_measureTick[i] < 90)
                return;
            _measureTick[i] = 0;

            try
            {
                if (slot.Equipment == EquipmentIndex.None)
                    return;

                WeakGameEntity weapon = agent.GetWeaponEntityFromEquipmentSlot(slot.Equipment);
                if (!weapon.IsValid)
                    return;

                _measureReports[i]++;

                float miss = (weapon.GetGlobalFrame().origin - handWorld.origin).Length;

                VrLog.Info(string.Format(
                    "TEST 211 {0} weapon: {1:F3} m from the controller, mode {2}. This is the "
                    + "number the round is judged on. The baseline to beat is 0.78-0.92 m on the "
                    + "main hand and 1.31-2.44 m on the off hand, which is where four seams of "
                    + "writing the frame left it. Near zero, sustained, means the weapon is in "
                    + "the hand.",
                    slot.Role, miss, Mode));
            }
            catch (Exception)
            {
                // Diagnostics only. The attach reports its own failures.
            }
        }

        /// <summary>One bone's components, named by type. The count alone answers
        /// the question; the names say which is the weapon.</summary>
        private static void AppendBoneComponents(StringBuilder text, Skeleton skeleton, sbyte bone)
        {
            text.Append('[').Append(bone).Append("] ").Append(BoneName(skeleton, bone)).Append(' ');

            int count;
            try
            {
                count = skeleton.GetBoneComponentCount(bone);
            }
            catch (Exception)
            {
                text.Append("(count unavailable)");
                return;
            }

            if (count <= 0)
            {
                text.Append("none");
                return;
            }

            text.Append(count).Append(": ");

            for (int c = 0; c < count; c++)
            {
                if (c > 0)
                    text.Append(", ");

                try
                {
                    GameEntityComponent component = skeleton.GetBoneComponentAtIndex(bone, c);
                    if (component == null)
                    {
                        text.Append("null");
                        continue;
                    }

                    text.Append(component.GetType().Name);

                    WeakGameEntity owner = component.GetEntity();
                    if (owner.IsValid && !string.IsNullOrEmpty(owner.Name))
                        text.Append(" '").Append(owner.Name).Append('\'');
                }
                catch (Exception)
                {
                    text.Append("unreadable");
                }
            }
        }

        private static string BoneName(Skeleton skeleton, sbyte bone)
        {
            try
            {
                return skeleton.GetBoneName(bone) ?? "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static void LogModeOnce()
        {
            if (_loggedMode)
                return;

            _loggedMode = true;
            VrLog.Info("TEST 211: weapons are placed by the engine's own after-animation attach, "
                       + "and the mod writes no weapon frame. weapon_attach = entity goes back to "
                       + "the TEST 207-210 behaviour; weapon_attach = off leaves weapons to the "
                       + "game entirely.");
        }

        /// <summary>Per-mission. A new mission is a new skeleton and new equipment,
        /// so every binding has to be made again.</summary>
        public static void Reset()
        {
            for (int i = 0; i < Roles; i++)
            {
                _described[i] = false;
                _censused[i] = false;
                _trackTick[i] = 0;
                _trackReports[i] = 0;
                _beforeValid[i] = false;
                _beforeCount[i] = 0;
                _armed[i] = false;
                _armedEntity[i] = UIntPtr.Zero;
                _armedBone[i] = -1;
                _measureTick[i] = 0;
                _measureReports[i] = 0;
            }

            _loggedMode = false;
            _loggedDriveAll = false;
            _frozen = false;
            _frozenCaptured = false;
            _frozenCount = 0;

            // Deliberately NOT released here. Reset runs at scene boundaries,
            // where the entity that was held has usually already been destroyed,
            // and calling into a dead GameEntity faults rather than throwing. The
            // agent goes with the scene, so the hold goes with it. Releasing a
            // LIVE skeleton is Release's job and its caller supplies the pair.
            _held = false;
        }
    }
}
