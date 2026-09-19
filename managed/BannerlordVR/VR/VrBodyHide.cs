using System;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Hides the player's body and leaves the weapon and shield floating.
    ///
    /// WHY THIS IS THE RIGHT ANSWER RATHER THAN A RETREAT
    ///
    /// The hands were being posed onto the controllers correctly and it still
    /// looked wrong, because only the HAND bones were posed. The forearm, upper
    /// arm and clavicle stayed wherever the animation put them, so the arm ran
    /// from a shoulder doing one thing to a hand doing another, and the body -
    /// drawn at first-person range, filling most of the view - made every bit of
    /// that disagreement obvious.
    ///
    /// Fixing the arm means a two-bone IK solve, which needs the elbow plane, a
    /// cross-body rule, and a decision about what to do when the target is out of
    /// reach. That is a real piece of work with its own ways of being wrong.
    ///
    /// Removing the body removes the entire question. A floating weapon that
    /// tracks your hand is a complete, coherent thing - it is what a lot of VR
    /// games ship deliberately - and it is honest in a way a broken arm is not.
    /// The weapon was already following the controller; this stops drawing the
    /// argument around it.
    ///
    /// MESHES, NOT THE ENTITY
    ///
    /// The obvious lever is MBAgentVisuals.SetVisible(false) or the entity's own
    /// visibility flag. Both are wrong here, and the engine says so: GameEntity
    /// exposes IsVisibleIncludeParents, so visibility is inherited down the
    /// hierarchy - and a wielded weapon is a CHILD entity of the agent, added
    /// through MBAgentVisuals.AddChildEntity. Hiding the parent hides the weapon
    /// with it, which is the one thing this must not do.
    ///
    /// MetaMesh.SetVisibilityMask does not touch the hierarchy at all. It clears
    /// the Final bit on the meshes hanging off the AGENT's own entity - the body,
    /// the armour, the hair - and a child entity's meshes are not among them, so
    /// the weapon carries on rendering because nothing ever asked it not to.
    ///
    /// NOTHING IS RESTORED, AND THAT IS DELIBERATE
    ///
    /// VrAgentVisibility has the scar for this one: the previous attempt at
    /// meddling with agent rendering held engine pointers across the agent's
    /// lifetime and handed one back after the engine had freed it. A MetaMesh
    /// belongs to an agent that can die between the hide and the restore, so the
    /// masks are not stashed and put back. The meshes are re-hidden whenever the
    /// entity changes instead, and switching the feature off takes effect on the
    /// next mission. Nothing of ours can outlive what it points at.
    /// </summary>
    public static class VrBodyHide
    {
        /// <summary>
        /// Hide the player's body and show only what is in its hands.
        ///
        /// On by default: the arms are not solved, so the alternative is a body
        /// whose arms visibly disagree with the hands attached to them.
        /// </summary>
        /// <summary>Whether the body is hidden right now. Runtime state rather
        /// than a fixed config read, because the VR panel toggles it.</summary>
        public static bool Hiding { get; private set; } = VrConfig.Bool("hide_body", true);

        /// <summary>Set from the VR panel. Showing the body again is immediate;
        /// hiding it takes effect on the next visibility pass.</summary>
        public static void SetHiding(bool hide)
        {
            if (Hiding == hide)
                return;

            Hiding = hide;

            // The next Tick rebuilds the mesh visibility from scratch, so the
            // only thing needed here is to forget what was decided last time.
            Reset();
        }

        public static bool Enabled => Hiding;

        /// <summary>Print the mesh table once, the way the bone table is printed.
        /// It is what makes the next question about this answerable from a log
        /// rather than from a guess.</summary>
        private static readonly bool LogMeshes = VrConfig.Bool("hide_body_log", true);

        /// <summary>
        /// Substring of a mesh name never hidden.
        ///
        /// "wpn_" by default, and that is read off the log rather than guessed:
        /// the weapon entities this game hands back are named wpn_kite_shield_n
        /// and wpn_batched_meshes_1. Anything a weapon put on the SKELETON rather
        /// than on its own entity will share that prefix, so this keeps it.
        /// </summary>
        private static readonly string Keep =
            VrConfig.String("hide_body_keep", "wpn_").Trim().ToLowerInvariant();

        private static bool _broken;
        private static bool _loggedMeshes;
        private static bool _loggedResult;
        private static bool _warnedNotSeparate;

        /// <summary>Identity of the entity the meshes were hidden on, and how many
        /// there were. The count is half the key because changing equipment adds
        /// and removes meshes on the same entity, and a new one would otherwise
        /// arrive visible and stay that way.</summary>
        private static UIntPtr _hiddenFor;
        private static int _hiddenCount = -1;

        /// <summary>
        /// Called every frame from the hand poser, which has already resolved all
        /// three of these. Cheap after the first pass on an entity: two integer
        /// compares.
        /// </summary>
        public static void Tick(Agent agent, MBAgentVisuals visuals, GameEntity entity,
                                Skeleton skeleton)
        {
            if (!Enabled || _broken || agent == null || entity == null)
                return;

            try
            {
                UIntPtr key = entity.Pointer;
                int count = entity.MultiMeshComponentCount;

                if (key == _hiddenFor && count == _hiddenCount)
                    return;

                _hiddenFor = key;
                _hiddenCount = count;

                DescribeOnce(agent, entity, skeleton, count);

                if (!WeaponsAreSeparate(agent, entity))
                    return;

                Hide(entity, skeleton, count);
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("Hiding the body failed; it stays visible for the rest of the "
                            + "session. Nothing else is affected.", ex);
            }
        }

        /// <summary>
        /// Whether the wielded weapons live on their own entities.
        ///
        /// The whole approach rests on this: hiding the agent entity's meshes is
        /// only safe if the weapon's meshes are not among them. Rather than
        /// assume it, ask - the pointers either differ or they do not - and refuse
        /// to hide anything if the answer is not the expected one. A visible body
        /// is a disappointment; an invisible sword is the feature not working.
        /// </summary>
        private static bool WeaponsAreSeparate(Agent agent, GameEntity entity)
        {
            UIntPtr agentPtr = entity.Pointer;
            bool sawOne = false;

            for (int slot = 0; slot < (int)EquipmentIndex.NumAllWeaponSlots; slot++)
            {
                MissionWeapon weapon = agent.Equipment[(EquipmentIndex)slot];
                if (weapon.IsEmpty)
                    continue;

                WeakGameEntity we = agent.GetWeaponEntityFromEquipmentSlot((EquipmentIndex)slot);
                if (!we.IsValid)
                    continue;

                sawOne = true;

                if (we.Pointer != agentPtr)
                    continue;

                if (!_warnedNotSeparate)
                {
                    _warnedNotSeparate = true;
                    VrLog.Warn("Body hiding is OFF: this agent's weapons share the body's entity, "
                               + "so hiding its meshes would take the weapon with them. The mesh "
                               + "table above lists what is there; set hide_body_keep to the "
                               + "weapon's mesh name if you want to try it anyway.");
                }

                return false;
            }

            if (!sawOne)
            {
                // Nothing wielded. Hiding is still correct - an empty-handed player
                // in VR should see empty hands, not a body - so this is not a
                // refusal, just nothing to check against.
                return true;
            }

            return true;
        }

        /// <summary>
        /// Hides the body.
        ///
        /// THE MESHES ARE ON THE SKELETON, NOT THE ENTITY
        ///
        /// The first version of this walked the agent entity's own mesh components
        /// and hid them. The log's answer to that was flat:
        ///
        ///     Player entity '' carries 0 mesh(es) of its own
        ///     Body hidden: 0 mesh(es) taken out of the render
        ///
        /// The entity is nameless and empty. A character's body is added through
        /// GameEntity.AddMultiMeshToSkeleton, so it hangs off the SKELETON, and
        /// Skeleton.GetAllMeshes is what enumerates it. Both are walked now,
        /// because an empty list is not proof that a thing is not there and the
        /// entity list costs nothing to check.
        /// </summary>
        private static void Hide(GameEntity entity, Skeleton skeleton, int count)
        {
            int hidden = 0;
            int kept = 0;

            for (int i = 0; i < count; i++)
            {
                MetaMesh mesh = entity.GetMetaMesh(i);
                if (mesh == null)
                    continue;

                if (Skip(mesh.GetName()))
                {
                    kept++;
                    continue;
                }

                // Nought rather than "clear the Final bit": the shadow bits would
                // otherwise leave the body casting a shadow it no longer has, and
                // a headless shadow at first-person range is more startling than
                // the body was.
                mesh.SetVisibilityMask(0);
                hidden++;
            }

            if (skeleton != null)
            {
                foreach (Mesh mesh in skeleton.GetAllMeshes())
                {
                    if (mesh == null)
                        continue;

                    if (Skip(mesh.Name))
                    {
                        kept++;
                        continue;
                    }

                    mesh.SetVisibilityMask(0);
                    hidden++;
                }
            }

            if (_loggedResult)
                return;

            _loggedResult = true;
            VrLog.Info("Body hidden: " + hidden + " mesh(es) taken out of the render, "
                       + kept + " kept. The weapon and shield are separate entities with their "
                       + "own meshes, so they carry on drawing. Set hide_body = 0 to get the "
                       + "body back.");
        }

        private static bool Skip(string name)
        {
            if (Keep.Length == 0)
                return false;

            string lower = (name ?? string.Empty).ToLowerInvariant();
            return lower.Length > 0 && lower.Contains(Keep);
        }

        /// <summary>
        /// Prints the agent's mesh table and the weapon entities once.
        ///
        /// The same reasoning as the bone table: every later question about this -
        /// which mesh is the body, whether a weapon is separate, what to put in
        /// hide_body_keep - is a question about this list, and reading it from a
        /// log beats another round of inference.
        /// </summary>
        private static void DescribeOnce(Agent agent, GameEntity entity, Skeleton skeleton,
                                         int count)
        {
            if (_loggedMeshes || !LogMeshes)
                return;

            _loggedMeshes = true;

            VrLog.Info("Player entity '" + (entity.Name ?? "?") + "' carries " + count
                       + " mesh(es) of its own:");

            var line = new StringBuilder();

            for (int i = 0; i < count; i++)
            {
                MetaMesh mesh = entity.GetMetaMesh(i);

                line.Length = 0;
                line.Append("  [").Append(i).Append("] ")
                    .Append(mesh == null ? "?" : (mesh.GetName() ?? "?"));

                VrLog.Info(line.ToString());
            }

            // The list that actually matters. The entity's own came back empty on
            // the first run - the body is added through AddMultiMeshToSkeleton and
            // so hangs off the skeleton instead.
            if (skeleton != null)
            {
                int n = 0;

                foreach (Mesh mesh in skeleton.GetAllMeshes())
                {
                    string name = mesh == null ? "?" : (mesh.Name ?? "?");

                    line.Length = 0;
                    line.Append("  skeleton [").Append(n++).Append("] ").Append(name);

                    if (Skip(name))
                        line.Append("   (kept - matches hide_body_keep)");

                    VrLog.Info(line.ToString());
                }

                VrLog.Info("Skeleton carries " + n + " mesh(es); that is where the body is.");
            }

            for (int slot = 0; slot < (int)EquipmentIndex.NumAllWeaponSlots; slot++)
            {
                MissionWeapon weapon = agent.Equipment[(EquipmentIndex)slot];
                if (weapon.IsEmpty)
                    continue;

                WeakGameEntity we = agent.GetWeaponEntityFromEquipmentSlot((EquipmentIndex)slot);

                VrLog.Info("  slot " + (EquipmentIndex)slot + ": '"
                           + (we.IsValid ? (we.Name ?? "?") : "no entity") + "', "
                           + (we.IsValid && we.Pointer != entity.Pointer
                                ? "its own entity - safe to hide the body"
                                : "NOT a separate entity"));
            }
        }

        /// <summary>Per-mission. The masks are not restored - see the class note -
        /// so this only forgets which entity was done, and the next mission's
        /// agent gets hidden on its own first frame.</summary>
        public static void Reset()
        {
            _hiddenFor = UIntPtr.Zero;
            _hiddenCount = -1;
        }
    }
}
